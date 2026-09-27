using System.Globalization;
using System.Text;
using System.Text.Json;
using NeonSidekick.Web;

namespace NeonSidekick.Skills;

/// <summary>A search's hits, or the error that stands in for them.</summary>
public sealed record SkillSearchResult(IReadOnlyList<SkillsShSkill> Hits, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The network side of <c>/skills add</c> (2026-09-26): the search on skills.sh and the archive's
/// download, both through the app's <see cref="WebFetcher"/> — the LAN rule on every hop and at the
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

    /// <summary>The archive of <paramref name="source"/>, opened; null with the error.</summary>
    public async Task<(SkillArchive? Archive, string? Error)> DownloadAsync(SkillSource source, FetchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (source.ArchiveUrl is not { } url)
        {
            throw new ArgumentException("A search has no archive.", nameof(source));
        }

        var download = await _fetcher.DownloadAsync(url, options with { Engine = FetchEngine.HttpClient }, cancellationToken).ConfigureAwait(false);
        if (!download.Ok)
        {
            return (null, download.Error);
        }

        var archive = SkillArchive.TryOpen(download.Bytes, out string? error);
        return (archive, error);
    }
}
