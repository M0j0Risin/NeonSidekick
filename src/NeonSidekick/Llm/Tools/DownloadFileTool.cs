using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>download_file(url, path?, overwrite?)</c> (2026-09-18): a file from the web — a picture, a PDF,
/// an archive, a data file, a page's source — saved under the working directory as it came, through
/// <see cref="WebFetcher.DownloadAsync"/> (the HTTP leg alone, the LAN rule at every hop, up to
/// <see cref="WebFetcher.MaxFileDownloadBytes"/>) and <see cref="WorkingDirectory.WriteBytes"/> (the
/// sandbox, <c>overwrite</c>). <c>path</c> is the
/// file to write; a folder (an existing one, or a path ending in a separator) takes the file's own
/// name inside it — from the server's <c>Content-Disposition</c>, else the URL's last segment
/// (<see cref="FileNameFor"/>); no <c>path</c> is the top of the working directory. Offered while
/// <c>Web tools</c> and <c>File tools</c> are both on: it reaches the web and writes the sandbox.
/// Nothing is read or opened; <c>view_image</c> and <c>read_file</c> look at the result.
/// </summary>
public sealed class DownloadFileTool : AIFunction
{
    public const string ToolName = "download_file";
    public const string UrlArgument = "url";
    public const string PathArgument = FileTool.PathArgument;
    public const string OverwriteArgument = FileTool.OverwriteArgument;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "The file's address (http or https)." },
            "path": { "type": "string", "description": "Where to save it, relative to the working directory: a file name, or a folder to put it in (the file keeps its own name). Leave it out to save it at the top under its own name." },
            "overwrite": { "type": "boolean", "description": "true to replace a file that already exists. Without it an existing file is left alone." }
          },
          "required": ["url"]
        }
        """);

    private readonly WebAccess _web;
    private readonly WorkingDirectory _files;
    private readonly Func<AppSettingsData> _effective;

    public DownloadFileTool(WebAccess web, WorkingDirectory files, Func<AppSettingsData> effective)
    {
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    public override string Name => ToolName;

    public override string Description => DescriptionText;

    /// <summary>The description; its <c>.trash</c> clause went with File safe edits (2026-10-01, the user's call). Pinned.</summary>
    public static readonly string DescriptionText =
        "Downloads a file from the web — a picture, a PDF, an archive, a data file, a page's source — and saves it under the working directory (the user's cwd / current directory), creating any missing folders; nothing is read or opened. " +
        "path is the file to write, or a folder to put it in under the file's own name; without it the file lands at the top under its own name. " +
        "A file that already exists is left alone unless overwrite is true. " +
        "Up to " + WebText.Size(WebFetcher.MaxFileDownloadBytes) + ". To read a page use " + WebFetchTool.ToolName + "; to look at a saved picture use " + ViewImageTool.ToolName + ".";

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

        string name = Sanitise(headerName);
        if (name.Length == 0)
        {
            string segment = finalUrl.AbsolutePath;
            int cut = segment.LastIndexOf('/');
            name = Sanitise(Uri.UnescapeDataString(cut >= 0 ? segment[(cut + 1)..] : segment));
        }

        if (name.Length == 0)
        {
            return null;
        }

        return folder.Length == 0 || folder is "." ? name : folder.TrimEnd('/', '\\') + "/" + name;
    }

    /// <summary>A candidate file name with any path and the characters Windows refuses stripped, trimmed of dots and blanks; <c>""</c> when nothing is left.</summary>
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

    public async Task<string> DownloadAsync(string url, string? path, bool overwrite, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return WebText.NoDownloadUrl;
        }

        var parsed = WebFetcher.ParseUrl(url);
        if (parsed is null)
        {
            return WebText.NotHttp(url.Trim());
        }

        var effective = _effective();
        var download = await _web.Fetcher.DownloadAsync(parsed, WebAccess.Options(effective), cancellationToken).ConfigureAwait(false);
        if (!download.Ok)
        {
            return download.Error;
        }

        var final = Uri.TryCreate(download.FinalUrl, UriKind.Absolute, out var finalUrl) ? finalUrl : parsed;
        string? target = FileNameFor(path, download.FileName, final, _files.IsExistingDirectory);
        if (target is null)
        {
            return WebText.NoFileName(download.Url);
        }

        var written = _files.WriteBytes(target, download.Bytes, overwrite);
        return WebText.Downloaded(written, download.Url, download.MediaType);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryReadBoolean(arguments, OverwriteArgument, out var overwrite, out var raw))
        {
            return FileText.BadBoolean(OverwriteArgument, raw);
        }

        string path = ToolArguments.ReadString(arguments, PathArgument);
        return await DownloadAsync(ToolArguments.ReadString(arguments, UrlArgument), path, overwrite ?? false, cancellationToken).ConfigureAwait(false);
    }
}
