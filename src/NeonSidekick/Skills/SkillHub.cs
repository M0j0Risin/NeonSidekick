using System.Globalization;
using System.Text;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Web;

namespace NeonSidekick.Skills;

/// <summary>A search's hits, or the error that stands in for them.</summary>
public sealed record SkillSearchResult(IReadOnlyList<SkillsShSkill> Hits, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The network side of <c>/skills add</c> (2026-09-26): the search on skills.sh and the repository's
/// listing or archive, all through the app's <see cref="WebFetcher"/> — the LAN rule on every hop and at the
/// socket, <c>Web browser network mode</c> honoured (a <c>local_area_network</c> mode refuses both),
/// the <see cref="WebFetcher.MaxFileDownloadBytes"/> cap and the download timeout. The <c>Web tools</c>
/// switch is not consulted: it is the model's, and the user typed this. Every failure is a sentence,
/// never an exception; only cancellation escapes.
///
/// <para>agentskills.io publishes the format and no catalog; skills.sh is the directory the other
/// clients search. Its documented API (<c>/api/v1</c>) wants a Vercel OIDC token, which only an app
/// deployed on Vercel has, so the search goes to <see cref="SearchUrl"/>, the endpoint its own site
/// uses — undocumented and open, answering <c>{skills: [{id, source, skillId, name, installs}]}</c>
/// with no description. The parse is tolerant (a missing list is no hits); if the endpoint changes,
/// an <c>owner/repo</c> or a URL still installs, since the download goes to GitHub itself.</para>
/// </summary>
public sealed class SkillHub
{
    public const string SearchUrl = "https://skills.sh/api/search";

    /// <summary>Hits asked for: 25 since later on 2026-09-26 (the user's call; 10 at first) — the pane scrolls, and past that the fuzzy matches wander off the query.</summary>
    public const int SearchLimit = 25;

    private readonly WebFetcher _fetcher;

    public SkillHub(WebFetcher fetcher)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
    }

    /// <summary>The search's URL for <paramref name="query"/>.</summary>
    public static Uri SearchUri(string query) =>
        new(SearchUrl + "?q=" + Uri.EscapeDataString(query.Trim()) + "&limit=" + SearchLimit.ToString(CultureInfo.InvariantCulture));

    /// <summary>skills.sh's hits for <paramref name="query"/>, best first, those with no usable id dropped.</summary>
    public async Task<SkillSearchResult> SearchAsync(string query, FetchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        var download = await _fetcher.DownloadAsync(SearchUri(query), options with { Engine = FetchEngine.HttpClient }, cancellationToken).ConfigureAwait(false);
        if (!download.Ok)
        {
            return new SkillSearchResult([], download.Error);
        }

        try
        {
            var response = JsonSerializer.Deserialize(Encoding.UTF8.GetString(download.Bytes), SkillsJsonContext.Default.SkillsShSearchResponse);
            var hits = (response?.Skills ?? [])
                .Where(hit => SkillSource.TryParse(hit.Id, out var source, out _) && source!.Kind == SkillSourceKind.GitHub && source.SkillId is not null)
                .ToList();
            return new SkillSearchResult(hits, null);
        }
        catch (JsonException ex)
        {
            return new SkillSearchResult([], SkillInstallText.SearchUnreadableError(ex.Message));
        }
    }

    /// <summary>The API's answer of a commit's SHA alone.</summary>
    public const string ShaMediaType = "application/vnd.github.sha";

    /// <summary>The API's JSON.</summary>
    public const string ApiMediaType = "application/vnd.github+json";

    /// <summary>
    /// The archive of <paramref name="source"/>, opened; null with the error. A GitHub repository is
    /// listed first (later on 2026-09-26, the user's call: repositories can be large): the commit its
    /// ref names, then that commit's tree, <see cref="SkillArchive.FromTree"/> fetching files from
    /// <see cref="SkillSource.RawBase"/> pinned to the commit, so what was previewed is what is
    /// installed. Two API requests of the 60 an hour GitHub allows without a token. Any failure
    /// there — a rate limit, a 404, an unreadable or truncated listing, the API unreachable — falls
    /// back to codeload's zip of the ref, whose own failure is then the error said.
    /// </summary>
    public async Task<(SkillArchive? Archive, string? Error)> OpenAsync(SkillSource source, FetchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (source.ArchiveUrl is not { } url)
        {
            throw new ArgumentException("A search has no archive.", nameof(source));
        }

        options = options with { Engine = FetchEngine.HttpClient };
        if (source.Kind == SkillSourceKind.GitHub)
        {
            var (listed, why) = await ListAsync(source, options, cancellationToken).ConfigureAwait(false);
            if (listed is not null)
            {
                return (listed, null);
            }

            DiagnosticLog.Info(SkillCatalog.Category, $"{source.Label}: no listing ({why}); downloading the whole archive instead.");
        }

        var download = await _fetcher.DownloadAsync(url, options, cancellationToken).ConfigureAwait(false);
        if (!download.Ok)
        {
            // A 404 from both the API and codeload (2026-09-27, the user's report: a skills.sh hit whose repository was
            // deleted) is said as that, not as a codeload URL; a renamed repository redirects, so it never lands here.
            return (null, source.Kind == SkillSourceKind.GitHub && download.Status == 404 ? SkillInstallText.RepoNotFoundError(source) : download.Error);
        }

        var archive = SkillArchive.TryOpen(download.Bytes, out string? error, url);
        return (archive, error);
    }

    /// <summary>The repository's listing at the ref's commit, or null with why not.</summary>
    private async Task<(SkillArchive? Archive, string? Why)> ListAsync(SkillSource source, FetchOptions options, CancellationToken cancellationToken)
    {
        var commit = await _fetcher.DownloadAsync(source.CommitUrl, options with { Accept = ShaMediaType }, cancellationToken).ConfigureAwait(false);
        if (!commit.Ok)
        {
            return (null, commit.Error);
        }

        string sha = Encoding.UTF8.GetString(commit.Bytes).Trim();
        if (sha.Length != 40 || !sha.All(char.IsAsciiHexDigit))
        {
            return (null, "the commit's answer is not a SHA");
        }

        sha = sha.ToLowerInvariant();
        var listing = await _fetcher.DownloadAsync(source.TreeUrl(sha), options with { Accept = ApiMediaType }, cancellationToken).ConfigureAwait(false);
        if (!listing.Ok)
        {
            return (null, listing.Error);
        }

        GitHubTree? tree;
        try
        {
            tree = JsonSerializer.Deserialize(listing.Bytes, SkillsJsonContext.Default.GitHubTree);
        }
        catch (JsonException ex)
        {
            return (null, "the listing could not be read: " + ex.Message);
        }

        if (tree?.Tree is not { } entries)
        {
            return (null, "the listing is empty");
        }

        if (tree.Truncated)
        {
            return (null, "the listing was cut short");
        }

        var blobs = entries.Where(e => e.Type == "blob" && e.Mode != SymlinkMode).Select(e => (e.Path, e.Size)).ToList();
        var links = entries.Where(e => e.Type == "blob" && e.Mode == SymlinkMode).Select(e => e.Path).ToList();
        FetchOptions raw = options with { Accept = null };
        return (SkillArchive.FromTree(blobs, links, sha, source.FolderUrl(sha, ""), async (path, ct) =>
        {
            var file = await _fetcher.DownloadAsync(source.RawUrl(sha, path), raw, ct).ConfigureAwait(false);
            return file.Ok ? (file.Bytes, null) : (null, file.Error);
        }), null);
    }

    /// <summary>Git's mode of a symbolic link.</summary>
    private const string SymlinkMode = "120000";
}
