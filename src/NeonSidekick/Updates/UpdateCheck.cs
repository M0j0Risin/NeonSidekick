using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Llm;

namespace NeonSidekick.Updates;

/// <summary>One asset of GitHub's release JSON.</summary>
public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? Url { get; set; }
}

/// <summary>What's read of GitHub's release JSON. <see cref="Body"/> can be null, which the pane shows as no notes with the release link.</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? Tag { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? PageUrl { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

/// <summary>A release this copy could install, its assets vetted.</summary>
/// <param name="Version">Its version, from the tag.</param>
/// <param name="Notes">The notes as GitHub holds them: untrusted, and null with none.</param>
/// <param name="PageUrl">The release's page, for the notes' last line.</param>
/// <param name="ArchiveName">The archive's name, which the signed <c>.sha256</c> must name exactly.</param>
/// <param name="ArchiveUrl">Where the archive is.</param>
/// <param name="ArchiveBytes">Its size.</param>
/// <param name="ShaUrl">Where its <c>.sha256</c> is.</param>
/// <param name="SigUrl">Where its <c>.sha256.sig</c> is.</param>
public sealed record UpdateRelease(UpdateVersion Version, string? Notes, string PageUrl, string ArchiveName, Uri ArchiveUrl, long ArchiveBytes, Uri ShaUrl, Uri SigUrl);

/// <summary>What a check found.</summary>
public enum UpdateCheckKind
{
    /// <summary>A newer release this copy can install: <see cref="UpdateCheckResult.Release"/>.</summary>
    Newer,

    /// <summary>Nothing newer, or a tag that isn't plain <c>vX.Y.Z</c>.</summary>
    Latest,

    /// <summary>A newer release with this platform's archive and no signature. Never offered; <c>/update</c> says so.</summary>
    Unsigned,

    /// <summary>A newer release with nothing for this platform.</summary>
    NoPackage,

    /// <summary>The check failed: <see cref="UpdateCheckResult.Reason"/>. Silent at start; <c>/update</c> says why.</summary>
    Failed,
}

/// <summary>A check's answer.</summary>
public sealed record UpdateCheckResult(UpdateCheckKind Kind, UpdateRelease? Release = null, string? Reason = null, UpdateVersion Found = default);

/// <summary>
/// Where releases come from (2026-11-04, self-update). Each asset URL must be HTTPS on <c>github.com</c>, under this repository's <c>releases/download/</c>, and anything else counts as a release without that
/// asset. The redirect it then takes isn't checked, because that host is GitHub's to change and a list would break every update the day it did: the signature and hash vouch for the bytes, and .NET's client
/// won't follow HTTPS to HTTP. The repository comes from <see cref="App.AboutText.RepositoryUrl"/>, not a second copy of its name.
/// </summary>
public sealed class UpdateSource
{
    private readonly string _assetPrefix;
    private readonly string _pagePrefix;
    private readonly string _assetScheme;
    private readonly string _assetHost;
    private readonly int _assetPort;

    private UpdateSource(Uri latest, string assetScheme, string assetHost, int assetPort, string repositoryPath)
    {
        Latest = latest;
        _assetScheme = assetScheme;
        _assetHost = assetHost;
        _assetPort = assetPort;
        _pagePrefix = repositoryPath + "/releases/";
        _assetPrefix = _pagePrefix + "download/";
    }

    /// <summary>The URL that answers with the latest release.</summary>
    public Uri Latest { get; }

    /// <summary>The real one, from a repository's web address.</summary>
    public static UpdateSource GitHub(string repositoryUrl)
    {
        var repository = new Uri(repositoryUrl);
        string path = repository.AbsolutePath.TrimEnd('/');
        return new UpdateSource(new Uri("https://api.github.com/repos" + path + "/releases/latest"), Uri.UriSchemeHttps, repository.Host, repository.Port, path);
    }

#if UPDATE_SIM
    /// <summary>A simulation build's fake GitHub: the one loopback origin stands in for <c>github.com</c> and HTTPS, for the assets too.</summary>
    public static UpdateSource Simulated(Uri origin) => new(new Uri(origin, "repos/sim/releases/latest"), origin.Scheme, origin.Host, origin.Port, "/sim");
#endif

    /// <summary>Whether <paramref name="url"/> is an asset this repository published.</summary>
    public bool IsAsset(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return IsOurs(url, _assetPrefix);
    }

    /// <summary>
    /// What's shown for a release's page: <paramref name="raw"/> if it's on this repository, else the Releases link (2026-11-04, review P1-17). It comes from the release's JSON and lands in the transcript as a link, so it must not be able to name another site or carry control characters.
    /// </summary>
    public string PageOrReleases(string? raw) =>
        raw is not null && !raw.Any(char.IsControl) && Uri.TryCreate(raw, UriKind.Absolute, out var url) && IsOurs(url, _pagePrefix) ? raw : UpdateText.ReleasesUrl;

    // The repository's path ignores case (2026-11-04, review P1-18, the user's call): GitHub does, so a change of its capitals mustn't read as a release with no assets. That assets come from this repository stays, so a true rename still means one update by hand.
    private bool IsOurs(Uri url, string prefix) =>
        url.IsAbsoluteUri
        && string.Equals(url.Scheme, _assetScheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(url.Host, _assetHost, StringComparison.OrdinalIgnoreCase)
        && url.Port == _assetPort
        && url.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Asks GitHub for the latest release (2026-11-04, self-update). It has its own 10 s limit and its own User-Agent, because the shared model client has an infinite timeout and no User-Agent, and GitHub's API refuses a
/// request without one. The generic User-Agent names the app and nothing about the user. The unauthenticated limit is 60 requests an hour per address, so one per launch is fine. <c>Web browser network mode</c> doesn't
/// apply: that governs the model's <c>web_fetch</c>, and this is the app's own request. Also fetches the two small signed files, which the model client's lack of a limit would leave able to hang forever.
/// </summary>
public sealed class UpdateCheck
{
    /// <summary>The request's limit.</summary>
    public static readonly TimeSpan RequestLimit = TimeSpan.FromSeconds(10);

    /// <summary>The release JSON's size cap. v0.5.6's notes are 8 KB and its assets a few hundred bytes each.</summary>
    public const int JsonCap = 2 * 1024 * 1024;

    /// <summary>The <c>.sha256</c>'s and <c>.sig</c>'s size cap: two lines, and a line per key.</summary>
    public const int SignedFileCap = 64 * 1024;

    private const string ArchivePrefix = "NeonSidekick-v";

    private readonly HttpClient _http;
    private readonly UpdateSource _source;
    private readonly UpdateVersion _running;
    private readonly string _rid;
    private readonly string _userAgent;

    public UpdateCheck(HttpClient http, UpdateSource source, UpdateVersion running, string rid)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _running = running;
        _rid = rid ?? throw new ArgumentNullException(nameof(rid));
        _userAgent = UpdateText.UserAgent(running.ToString());
    }

    /// <summary>The archive's name for a version and platform, as <c>build.ps1 -Package</c> names it: a zip on Windows, a tarball elsewhere.</summary>
    public static string ArchiveName(UpdateVersion version, string rid) =>
        $"{ArchivePrefix}{version}-{rid}{(rid.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".tar.gz")}";

    /// <summary>The version an archive's name (or its partial's) says, for the start's tidy of <c>staging.parts</c>.</summary>
    public static bool TryParseArchiveVersion(string name, out UpdateVersion version)
    {
        ArgumentNullException.ThrowIfNull(name);
        version = default;
        if (!name.StartsWith(ArchivePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int dash = name.IndexOf('-', ArchivePrefix.Length);
        return dash > 0 && UpdateVersion.TryParse(name[ArchivePrefix.Length..dash], out version);
    }

    /// <summary>The check. Never throws but for the caller's own cancellation: any failure is an answer.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        byte[] json;
        try
        {
            json = await FetchAsync(_source.Latest, JsonCap, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UpdateFetchException ex)
        {
            return new UpdateCheckResult(UpdateCheckKind.Failed, Reason: ex.Message);
        }

        GitHubRelease? release;
        try
        {
            release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubRelease);
        }
        catch (JsonException)
        {
            return new UpdateCheckResult(UpdateCheckKind.Failed, Reason: UpdateText.AnswerUnreadable);
        }

        return release is null ? new UpdateCheckResult(UpdateCheckKind.Failed, Reason: UpdateText.AnswerUnreadable) : Judge(release);
    }

    /// <summary>Fetches one small file under the request's limit and <paramref name="cap"/>. Throws <see cref="UpdateFetchException"/> with the reason, or the caller's own cancellation.</summary>
    public async Task<byte[]> FetchAsync(Uri url, int cap, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(RequestLimit);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(url == _source.Latest ? "application/vnd.github+json" : "application/octet-stream"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // What's said of the release list isn't true of one of its files (2026-11-04, review P6-7).
                bool latest = url == _source.Latest;
                throw new UpdateFetchException(response.StatusCode switch
                {
                    HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when latest => UpdateText.RateLimited,
                    HttpStatusCode.NotFound when latest => UpdateText.NoReleaseFound,
                    HttpStatusCode.NotFound => UpdateText.AssetGone,
                    var other => UpdateText.ServerAnswered((int)other),
                });
            }

            await using var body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            while (true)
            {
                int read = await body.ReadAsync(buffer, limit.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return bytes.ToArray();
                }

                bytes.Write(buffer, 0, read);
                if (bytes.Length > cap)
                {
                    throw new UpdateFetchException(UpdateText.AnswerUnreadable);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new UpdateFetchException(UpdateText.NoAnswerInTime);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UpdateFetchException(Assistant.Explain(ex));
        }
    }

    private UpdateCheckResult Judge(GitHubRelease release)
    {
        if (!UpdateVersion.TryParseTag(release.Tag, out var version) || version <= _running)
        {
            return new UpdateCheckResult(UpdateCheckKind.Latest);
        }

        string archiveName = ArchiveName(version, _rid);
        var archive = Asset(release, archiveName);
        var sha = Asset(release, archiveName + ".sha256");
        var sig = Asset(release, archiveName + ".sha256.sig");
        if (archive is null)
        {
            return new UpdateCheckResult(UpdateCheckKind.NoPackage, Found: version);
        }

        // An archive with neither proof is unsigned too (2026-11-04, review P7-9): "no package for this system" sent the user looking for one that was there.
        return sha is null || sig is null
            ? new UpdateCheckResult(UpdateCheckKind.Unsigned, Found: version)
            : new UpdateCheckResult(
                UpdateCheckKind.Newer,
                new UpdateRelease(version, release.Body, _source.PageOrReleases(release.PageUrl), archiveName, archive.Value.Url, archive.Value.Bytes, sha.Value.Url, sig.Value.Url),
                Found: version);
    }

    // Anything off the rule counts as a release without that asset, and so does a size of nothing, which no download can check against.
    private (Uri Url, long Bytes)? Asset(GitHubRelease release, string name)
    {
        foreach (var asset in release.Assets ?? [])
        {
            if (asset?.Name == name && asset.Size > 0 && Uri.TryCreate(asset.Url, UriKind.Absolute, out var url) && _source.IsAsset(url))
            {
                return (url, asset.Size);
            }
        }

        return null;
    }
}

/// <summary>A fetch that failed, its message the reason in words.</summary>
public sealed class UpdateFetchException : Exception
{
    public UpdateFetchException(string message)
        : base(message)
    {
    }

    public UpdateFetchException()
    {
    }

    public UpdateFetchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
