using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>open(path?, share?)</c>: hands a file to the user's own editor or viewer, or a folder to Explorer,
/// through the injected opener (<c>PersonaFile.OpenInEditor</c>: shell execute, never waits).
/// No path is the working directory itself.
///
/// <para><b>On a UNC share</b> (2026-10-01, the user's ask: "make it so that the open tool can open files in either of the
/// connected UNC shares"): while the UNC group is offered (<see cref="UncAccess.IsOffered"/>) the schema carries <c>share</c>,
/// and a <c>share</c> or a full <c>\\server\share</c> path outside the working directory opens there — resolved, checked and
/// worded as the UNC tools' reads are (<see cref="UncAccess.Resolve"/>, the preflight, <see cref="UncText.Scoped"/>). A
/// <c>runas</c> share on the network is refused (the user's call): its sign-in is a netonly token per call, and the program
/// the shell starts runs with the user's own token, so Explorer or the editor would knock with the wrong account — the model
/// is pointed to <c>unc_fetch</c> and then <c>open</c> on the copy. A local folder under <c>runas</c> is the user's own
/// anyway, so it opens. Without a share and with a relative path, nothing changed: the working directory.</para>
///
/// <para><b>A picture</b> (2026-10-11, the user's ask: asked to view a picture "in the built-in viewer", the model had only
/// <c>view_image</c>, which shows it to the model, and <c>open</c>, which handed it to the registered app): with an
/// <c>openPicture</c> given, a file <see cref="ImageFile.IsImagePath"/> calls a picture goes there instead of the opener — the screen opens it
/// where a double-click would (the <c>Image viewer</c> setting; the built-in viewer quietly when it is empty) and says where, for
/// the result. Other files and folders are as before; headless gives none.</para>
///
/// <para><b>The images folder</b> (2026-10-11, code review: the description sends the model here to show a picture, and the app
/// keeps its camera, screen, ComfyUI and pasted pictures in the profile's images folder, outside the working directory): with
/// <c>images</c> given, a full path in that folder opens there, as <c>file_info</c> reads one (<see cref="FileTool.FilesFor"/>) — a
/// picture or a folder only (code review, later that day: the shell would run any other file found there,
/// <see cref="FileText.OpenImagesFolderNotPicture"/>), and the schema's <c>path</c> says a full path there works.</para>
/// </summary>
public sealed class OpenTool : FileTool
{
    public const string ToolName = "open";
    public const string ShareArgument = UncTool.ShareArgument;

    // The four schemas, by whether the images folder is reached and a UNC share offered (code review, later on 2026-10-11: four
    // literals that differed by a phrase each). The images folder's (as file_info's): path may be a full path there too, so the
    // schema and the description agree.
    private static readonly JsonElement Schema = SchemaFor(images: false, shares: false);
    private static readonly JsonElement ShareSchema = SchemaFor(images: false, shares: true);
    private static readonly JsonElement ImagesSchema = SchemaFor(images: true, shares: false);
    private static readonly JsonElement ImagesShareSchema = SchemaFor(images: true, shares: true);

    /// <summary><c>path</c>, and <c>share</c> while <paramref name="shares"/>; a full path in the images folder named while <paramref name="images"/>.</summary>
    private static JsonElement SchemaFor(bool images, bool shares)
    {
        string relative = shares ? "relative to the working directory (or to the share when share is given)" : "relative to the working directory";
        string full = images ? ", or a full path in the user's images folder" : "";
        string none = shares ? "the working directory (or the share's root)" : "the working directory";
        return ToolSchema.Parse($$"""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "The file or folder to open, {{relative}}{{full}}. Leave it out to open {{none}} itself." }{{(shares ? ShareProperty : "")}}
              }
            }
            """);
    }

    private const string ShareProperty =
        """
        ,
            "share": { "type": "string", "description": "A UNC share's name (unc_shares lists them) to open the file or folder there instead of the working directory; not needed when path is a full \\\\server\\share path under one." }
        """;

    private const string BaseDescription =
        "Opens a file in the user's own editor or viewer, or a folder in Explorer, on their screen; no path opens the working directory (the user's cwd / current directory) itself. " +
        "Use it when they ask to open, show or see something rather than to have it read out.";

    /// <summary><see cref="BaseDescription"/> on macOS (2026-10-06, the macOS build): a folder opens in Finder there. Pinned.</summary>
    private const string MacBaseDescription =
        "Opens a file in the user's own editor or viewer, or a folder in Finder, on their screen; no path opens the working directory (the user's cwd / current directory) itself. " +
        "Use it when they ask to open, show or see something rather than to have it read out.";

    /// <summary>The sentence a picture opener adds (2026-10-11): where a picture opens, and that <c>open</c>, not <c>view_image</c>, shows the user one. Pinned.</summary>
    public const string PictureDescription =
        " A picture opens in the app's own picture viewer (or the image viewer the user chose): use open, not view_image, when they ask to see, show or view a picture themselves.";

    private const string ShareDescription =
        " With share, or a full \\\\server\\share path, it opens the file or folder on that UNC share instead; a share signed in as another account cannot be opened this way (unc_fetch the file, then open the copy).";

    private readonly Action<string> _opener;
    private readonly UncAccess? _unc;
    private readonly Func<AppSettingsData>? _effective;
    private readonly Func<string, string>? _openPicture;

    /// <param name="files">The working directory.</param>
    /// <param name="opener">The shell's "open with".</param>
    /// <param name="unc">The UNC shares' door (2026-10-01); null = the working directory alone.</param>
    /// <param name="effective">The settings in force, read at each call for <c>UNC tools</c> and the offered shares; needed with <paramref name="unc"/>.</param>
    /// <param name="openPicture">Opens a picture's full path where a double-click would (2026-10-11) and returns where it went
    /// (<see cref="FileText.InPictureViewer"/>, <see cref="FileText.InImageApp"/>); null = pictures go to <paramref name="opener"/> like any file.</param>
    /// <param name="images">The profile's images folder, opened by its full paths (2026-10-11); null = the working directory alone.</param>
    public OpenTool(WorkingDirectory files, Action<string> opener, UncAccess? unc = null, Func<AppSettingsData>? effective = null, Func<string, string>? openPicture = null, WorkingDirectory? images = null) : base(files, images)
    {
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        _unc = unc;
        _effective = effective;
        _openPicture = openPicture;
    }

    public override string Name => ToolName;

    public override string Description =>
        (_openPicture is null ? Base : Base + PictureDescription) + (Images is null ? "" : " " + FileText.ImagesFolderInfoSentence(Images.Root)) + (SharesOffered ? ShareDescription : "");

    private static string Base => OperatingSystem.IsMacOS() ? MacBaseDescription : BaseDescription;

    public override JsonElement JsonSchema => Images is null ? (SharesOffered ? ShareSchema : Schema) : (SharesOffered ? ImagesShareSchema : ImagesSchema);

    /// <summary>Whether a UNC share can be named now: the door given, <c>UNC tools</c> on, a share offered.</summary>
    private bool SharesOffered => _unc is not null && _effective is not null && _unc.IsOffered(_effective());

    public string Describe(string path) => OpenIn(FilesFor(path), path);

    /// <summary><paramref name="path"/> opened in <paramref name="files"/> (the working directory or a share's), a picture through <see cref="_openPicture"/>, and the result's line.</summary>
    private string OpenIn(WorkingDirectory files, string path)
    {
        // In the images folder only pictures and folders open (code review, 2026-10-11): the folder is reached for the app's own
        // pictures, and the shell's "open" runs a .bat, .lnk or .exe that ended up there. A picture is one the viewer reads or one
        // image_edit writes (code review, later that day: its TIFF, JXL and HEIF files were refused as no picture); those it cannot
        // show go to the opener, the registered app.
        if (ReferenceEquals(files, Images) && files.Resolve(path, forWrite: false, out string target) == FileOutcome.Ok
            && File.Exists(target) && !ImageFile.IsImagePath(target) && NeonSidekick.Images.ImageFormats.ByExtension(target) is null)
        {
            return FileText.OpenImagesFolderNotPicture(path);
        }

        string? where = null;
        var result = files.Open(path, full =>
        {
            if (_openPicture is not null && ImageFile.IsImagePath(full) && File.Exists(full))
            {
                where = _openPicture(full);
                return;
            }

            _opener(full);
        });

        return FileText.Opened(result, where);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string path = ReadPath(arguments);
        string? share = ToolArguments.ReadString(arguments, ShareArgument).Trim() is { Length: > 0 } named ? named : null;
        if (share is null && !OnAShare(path))
        {
            return Describe(path);
        }

        return await OpenOnShareAsync(share, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a call without a share means one: a full path outside the working directory (and the images folder) while a share is offered.</summary>
    private bool OnAShare(string path)
    {
        string spelled = path.Trim().Replace('/', '\\');
        return UncCatalog.IsAbsolute(spelled) && SharesOffered && FilesFor(spelled).Resolve(spelled, forWrite: false, out _) != FileOutcome.Ok;
    }

    /// <summary>The open on the share the call means, or the refusal: no share offered, the share unknown, a network <c>runas</c> share.</summary>
    private async Task<string> OpenOnShareAsync(string? share, string path, CancellationToken cancellationToken)
    {
        if (!SharesOffered)
        {
            return UncText.NoSharesOffered;
        }

        var target = _unc!.Resolve(share, path, out string relative, out string? error);
        if (target is null)
        {
            return error!;
        }

        if (target.Config.IsRunAs && target.Config.IsUnc)
        {
            return UncText.OpenRunAsRefused(target.Name);
        }

        // As the user (signIn false): a local runas folder needs no token, and the opened program runs as the user anyway.
        var result = await _unc.RunAsync(target, write: false, files => UncText.Scoped(OpenIn(files, relative), target), cancellationToken, signIn: false).ConfigureAwait(false);
        return result.Error ?? result.Value!;
    }
}
