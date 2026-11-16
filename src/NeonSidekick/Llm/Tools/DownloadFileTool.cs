using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>download_file(url, path?, overwrite?)</c> (2026-09-18): a file from the web — a picture, a PDF,
/// an archive, a data file, a page's source — saved under the working directory as it came, through
/// <see cref="WebFetcher.OpenDownloadAsync"/> (the HTTP leg alone, the LAN rule at every hop) and
/// <see cref="WebFetcher.SaveAsync"/> into <see cref="WorkingDirectory.BeginWrite"/>'s temporary sibling (the sandbox,
/// <c>overwrite</c>), streamed since 2026-10-01 (the user's ask: the size is the <c>Web download max (MB)</c> setting,
/// <see cref="MaxBytesOf"/>, a constant 50 MB held in memory until then) and given its name only when whole. <c>path</c> is the
/// file to write; a folder (an existing one, or a path ending in a separator) takes the file's own
/// name inside it — from the server's <c>Content-Disposition</c>, else the URL's last segment
/// (<see cref="FileNameFor"/>); no <c>path</c> is the top of the working directory. Offered while
/// <c>Web tools</c> and <c>File tools</c> are both on: it reaches the web and writes the sandbox.
/// Nothing is read or opened; <c>view_image</c> and <c>read_file</c> look at the result.
/// <para>
/// Since 2026-10-13 a refusal that may be a bot check (<see cref="BotCheck.Offers(OpenedDownload)"/>) can be shown to the user in the
/// Browser window (<see cref="BotCheckSolver"/>, per <c>Web browser botcheck window</c>): once they are through it, the window's cookies
/// go to the HTTP client and the download is asked for once more. A site that ties its clearance to the browser's own connection
/// refuses that too, which the result says.
/// </para>
/// </summary>
public sealed class DownloadFileTool : AIFunction
{
    public const string ToolName = "download_file";
    public const string UrlArgument = "url";
    public const string PathArgument = FileTool.PathArgument;
    public const string OverwriteArgument = FileTool.OverwriteArgument;
    public const string Sha256Argument = "sha256";
    public const string Sha512Argument = "sha512";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "The file's address (http or https)." },
            "path": { "type": "string", "description": "Where to save it, relative to the working directory: a file name, or a folder to put it in (the file keeps its own name). Leave it out to save it at the top under its own name." },
            "overwrite": { "type": "boolean", "description": "true to replace a file that already exists. Without it an existing file is left alone." },
            "sha256": { "type": "string", "description": "The file's published SHA-256, in hex: a file that doesn't match is not kept." },
            "sha512": { "type": "string", "description": "The file's published SHA-512, in hex, instead of sha256." }
          },
          "required": ["url"]
        }
        """);

    private readonly WebAccess _web;
    private readonly WorkingDirectory _files;
    private readonly Func<AppSettingsData> _effective;
    private readonly BotCheckSolver? _botCheck;

    /// <param name="botCheck">Who shows a bot check to the user (2026-10-13); null where nobody can be asked (headless).</param>
    public DownloadFileTool(WebAccess web, WorkingDirectory files, Func<AppSettingsData> effective, BotCheckSolver? botCheck = null)
    {
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _botCheck = botCheck;
    }

    public override string Name => ToolName;

    public override string Description => DescriptionFor(MaxBytesOf(_effective()));

    /// <summary>The largest file one call saves: <c>Web download max (MB)</c> in bytes (megabytes of 1,000,000), clamped (2026-10-01). Pure.</summary>
    public static long MaxBytesOf(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.WebDownloadMaxMegabytes, AppSettingsData.MinWebDownloadMaxMegabytes, AppSettingsData.MaxWebDownloadMaxMegabytes) * 1_000_000L;
    }

    /// <summary>
    /// The description under the cap <paramref name="maxBytes"/> (2026-10-01; a constant <c>DescriptionText</c> under 50 MB until
    /// then); its <c>.trash</c> clause went with File safe edits (2026-10-01, the user's call). Pinned.
    /// </summary>
    public static string DescriptionFor(long maxBytes) =>
        "Downloads a file from the web — a picture, a PDF, an archive, a data file, a page's source — and saves it under the working directory (the user's cwd / current directory), creating any missing folders; nothing is read or opened. " +
        "path is the file to write, or a folder to put it in under the file's own name; without it the file lands at the top under its own name. " +
        "A file that already exists is left alone unless overwrite is true. " +
        "With sha256 or sha512 the file is checked against it and not kept when it doesn't match. " +
        "Up to " + WebText.Size(maxBytes) + ". To read a page use " + WebFetchTool.ToolName + "; to look at a saved picture use " + ViewImageTool.ToolName + ".";

    public override JsonElement JsonSchema => Schema;

    /// <summary>
    /// The path a download is saved at: <paramref name="path"/> itself when it names a file; the
    /// derived name (<paramref name="headerName"/> from <c>Content-Disposition</c>, else the last
    /// segment of <paramref name="finalUrl"/>, unescaped, the query dropped, the characters a file
    /// name cannot hold stripped, <see cref="Sanitise"/>) inside <paramref name="path"/> when that is blank, ends in a
    /// separator or <paramref name="isFolder"/> says it exists as a folder; null when nothing names
    /// the file (<see cref="WebText.NoFileName"/>). Pure; pinned.
    /// </summary>
    public static string? FileNameFor(string? path, string headerName, Uri finalUrl, Func<string, bool> isFolder)
    {
        ArgumentNullException.ThrowIfNull(headerName);
        ArgumentNullException.ThrowIfNull(finalUrl);
        ArgumentNullException.ThrowIfNull(isFolder);
        string folder = (path ?? "").Trim();
        bool intoFolder = folder.Length == 0 || folder is "." || folder.EndsWith('/') || folder.EndsWith('\\') || isFolder(folder);
        if (!intoFolder)
        {
            return folder;
        }

        // Both are the server's names: their hidden characters made visible (FileNames.ServerName); one left empty falls back to the URL's.
        string name = FileNames.ServerName(Sanitise(headerName));
        if (name.Length == 0)
        {
            string segment = finalUrl.AbsolutePath;
            int cut = segment.LastIndexOf('/');
            name = FileNames.ServerName(Sanitise(Uri.UnescapeDataString(cut >= 0 ? segment[(cut + 1)..] : segment)));
        }

        if (name.Length == 0)
        {
            return null;
        }

        return folder.Length == 0 || folder is "." ? name : folder.TrimEnd('/', '\\') + "/" + name;
    }

    /// <summary>
    /// A candidate file name with any path and the characters Windows refuses stripped, trimmed of dots and blanks; <c>""</c> when nothing is
    /// left. The user's names keep every other character as given (<c>OutputNames</c>); a server's go on through <see cref="FileNames.ServerName"/>
    /// (2026-11-16, the web-tools review's #108: this rule had grown to change the user's Persian names too).
    /// </summary>
    public static string Sanitise(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int cut = name.LastIndexOfAny(['/', '\\']);
        string tail = cut >= 0 ? name[(cut + 1)..] : name;
        var invalid = Path.GetInvalidFileNameChars();
        var kept = new System.Text.StringBuilder(tail.Length);
        foreach (char c in tail)
        {
            if (Array.IndexOf(invalid, c) < 0)
            {
                kept.Append(c);
            }
        }

        // Trailing dots and blanks are what Windows refuses; a leading dot (.gitignore) is a name.
        return kept.ToString().Trim().TrimEnd('.').Trim();
    }

    public async Task<string> DownloadAsync(string url, string? path, bool overwrite, CancellationToken cancellationToken) =>
        (await DownloadSourcedAsync(url, path, overwrite, null, cancellationToken).ConfigureAwait(false)).Text;

    /// <summary>A digest the downloaded bytes must match (2026-11-15, the web-tools plan's §5, feature idea #29): the algorithm, its name as the answer says it, and the hex in lowercase.</summary>
    internal sealed record Expected(HashAlgorithmName Algorithm, string Name, string Hex);

    /// <summary>
    /// The <c>sha256</c> / <c>sha512</c> arguments (2026-11-15, the web-tools plan's §5, feature idea #29): blank ones as not given; both given an error; a value read with spaces
    /// dropped, case ignored and a <c>sha256:</c> prefix tolerated, refused unless it is 64 (or 128) hex digits. Since 2026-11-16 (the web-tools
    /// review's #85) a value that fails that is split on spaces, <c>=</c>, <c>:</c> and parentheses, and the one token of exactly that many hex
    /// digits is the digest: a <c>sha256sum</c> line, <c>sha-256:…</c>, <c>sha256=…</c>, the BSD <c>SHA256 (f) = …</c>. Two different such
    /// tokens, or none, is refused. Pure.
    /// </summary>
    internal static (Expected? Digest, string? Error) ReadDigest(string sha256, string sha512)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(sha512);
        bool has256 = sha256.Trim().Length > 0;
        bool has512 = sha512.Trim().Length > 0;
        if (has256 && has512)
        {
            return (null, WebText.BothDigests);
        }

        if (!has256 && !has512)
        {
            return (null, null);
        }

        var (argument, raw, digits, algorithm, name) = has256
            ? (Sha256Argument, sha256, 64, HashAlgorithmName.SHA256, WebText.Sha256Name)
            : (Sha512Argument, sha512, 128, HashAlgorithmName.SHA512, WebText.Sha512Name);
        string hex = string.Concat(raw.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        if (hex.StartsWith(argument + ":", StringComparison.Ordinal))
        {
            hex = hex[(argument.Length + 1)..];
        }

        if (hex.Length == digits && hex.All(char.IsAsciiHexDigit))
        {
            return (new Expected(algorithm, name, hex), null);
        }

        var tokens = raw.Split([' ', '\t', '\r', '\n', '=', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length == digits && t.All(char.IsAsciiHexDigit))
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return tokens.Count == 1
            ? (new Expected(algorithm, name, tokens[0]), null)
            : (null, WebText.BadDigest(argument, digits, raw));
    }

    // The answer and, for a file saved, what it was (2026-11-15).
    private async Task<(string Text, WebSource? Source)> DownloadSourcedAsync(string url, string? path, bool overwrite, Expected? expected, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (WebText.NoDownloadUrl, null);
        }

        var parsed = WebFetcher.ParseUrl(url);
        if (parsed is null)
        {
            return (WebText.NotHttp(url.Trim()), null);
        }

        var effective = _effective();
        long maxBytes = MaxBytesOf(effective);
        // With a digest the body is asked for as sent (2026-11-16, the web-tools review's #55), so the digest is of the server's bytes.
        var options = WebAccess.Options(effective) with { RawBody = expected is not null };
        var opened = await _web.Fetcher.OpenDownloadAsync(parsed, options, maxBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!opened.Ok && _botCheck is not null && BotCheck.Offers(opened))
            {
                var solved = await _botCheck(new BotCheckRequest(parsed, BotCheckUse.Download, BotCheck.WhyOf(opened.Detail)), cancellationToken).ConfigureAwait(false);
                if (solved is { Cleared: true })
                {
                    if (await _web.Fetcher.SolvedElsewhereAsync(parsed, solved, options, cancellationToken).ConfigureAwait(false) is { } elsewhere)
                    {
                        return (WebText.Error(opened.Outcome, opened.Url, opened.Detail + elsewhere), null);
                    }

                    _web.Fetcher.Adopt(solved.Cookies, solved.Identity, parsed);
                    opened.Dispose();
                    opened = await _web.Fetcher.OpenDownloadAsync(parsed, options, maxBytes, cancellationToken).ConfigureAwait(false);
                    if (!opened.Ok)
                    {
                        return (opened.Outcome == FetchOutcome.Blocked ? WebText.Error(opened.Outcome, opened.Url, opened.Detail + BotCheckText.ClearedButClientRefused) : opened.Error, null);
                    }
                }
                else if (solved is not null)
                {
                    return (WebText.Error(opened.Outcome, opened.Url, opened.Detail + BotCheckText.For(solved)), null);
                }
            }

            return await SaveAsync(opened, path, overwrite, maxBytes, parsed, expected, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            opened.Dispose();
        }
    }

    // The opened download saved under the sandbox, or why not.
    private async Task<(string Text, WebSource? Source)> SaveAsync(OpenedDownload opened, string? path, bool overwrite, long maxBytes, Uri parsed, Expected? expected, CancellationToken cancellationToken)
    {
        if (!opened.Ok)
        {
            return (opened.Error, null);
        }

        var final = Uri.TryCreate(opened.FinalUrl, UriKind.Absolute, out var finalUrl) ? finalUrl : parsed;
        string? target = FileNameFor(path, opened.FileName, final, _files.IsExistingDirectory);
        if (target is null)
        {
            return (WebText.NoFileName(opened.Url), null);
        }

        // The sandbox's refusals (outside, a folder in the way, a file there without overwrite) before a byte is read.
        var begun = _files.BeginWrite(target, overwrite, out var pending);
        if (pending is null)
        {
            // Refused before a byte was read: a digest given was never checked, and the answer says so (2026-11-16, the review's #173).
            return (WebText.Downloaded(begun, opened.Url, opened.MediaType) + (expected is null ? "" : WebText.DigestNotChecked(expected.Name)), null);
        }

        using (pending)
        {
            var saved = await _web.Fetcher.SaveAsync(opened, pending.Stream, maxBytes, cancellationToken, expected?.Algorithm).ConfigureAwait(false);
            // Marked as from the internet, as a browser marks its downloads (2026-10-23, the Mac pass, the user's call): Gatekeeper, SmartScreen
            // and Office's Protected View look at what the model fetched, and unzip carries the mark to what is inside.
            if (!saved.Ok)
            {
                return (saved.Error, null);
            }

            // A digest that doesn't match keeps nothing (2026-11-15, the web-tools plan's §5, feature idea #29): the pending write is dropped uncommitted, its temporary sibling with
            // it, and a file there under overwrite stays as it was.
            if (expected is not null && !string.Equals(saved.Digest, expected.Hex, StringComparison.Ordinal))
            {
                return (WebText.HashMismatch(expected.Name, expected.Hex, saved.Digest ?? "", opened.Url, opened.FinalUrl, opened.ContentEncoding, begun.Replaced), null);
            }

            string from = opened.FinalUrl.Length > 0 ? opened.FinalUrl : opened.Url;
            var written = pending.Commit(MarkOfTheWeb.ForDownload(from, _web.Time.GetUtcNow()));
            string text = WebText.Downloaded(written, opened.Url, opened.MediaType, expected is not null && written.Outcome == Files.FileOutcome.Ok ? WebText.Verified(expected.Name) : "");
            // The file saved, named as the answer names it, for the 📥 under the reply (2026-11-15, the web-tools plan's §2). The address asked,
            // not the one it ended at (2026-11-16, the web-tools review's #43): a redirect's signed URL carries its key, and stays in the mark.
            return (text, written.Outcome == Files.FileOutcome.Ok ? new WebSource(WebSourceKind.Download, opened.Url, Path.GetFileName(written.Relative)) : null);
        }
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryReadBoolean(arguments, OverwriteArgument, out var overwrite, out var raw))
        {
            return FileText.BadBoolean(OverwriteArgument, raw);
        }

        var (expected, digestError) = ReadDigest(ToolArguments.ReadString(arguments, Sha256Argument), ToolArguments.ReadString(arguments, Sha512Argument));
        if (digestError is not null)
        {
            return digestError;
        }

        string path = ToolArguments.ReadString(arguments, PathArgument);
        var (text, source) = await DownloadSourcedAsync(ToolArguments.ReadString(arguments, UrlArgument), path, overwrite ?? false, expected, cancellationToken).ConfigureAwait(false);
        return ToolSourcedResult.Of(text, source);
    }
}
