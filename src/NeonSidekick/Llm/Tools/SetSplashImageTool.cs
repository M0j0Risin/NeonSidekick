using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Comfy;
using NeonSidekick.Files;
using NeonSidekick.Images;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>set_splash_image(path, name?)</c> (2026-09-24, the user's ask: generated "splash / persona art"): copies a
/// picture from the working directory into the loaded profile's splash folder (<c>images\splash</c>; <c>images\splash_images</c> from 2026-10-10, <c>splash</c> under the profile before), which the splash walk reads at
/// every show (<see cref="UI.SplashImages.FromDirectory"/>), so "make that my splash" ends with the picture on the next
/// <c>/splash</c>. The folder is outside the sandbox, which is why this is a tool of its own rather than a
/// <c>copy</c>: it writes there and nowhere else, never over a picture already there (a number is added to the name).
/// The result says when the copy is the folder's only picture, since a profile's folder with any picture stands in for
/// the bundled set — the user should know the built-in splashes are hidden until more are added. Since 2026-10-10 (the user's
/// call, when the splash folder moved under the profile's images folder as <c>splash_images</c>) the picture may also be a full
/// path in the images folder, as the picture tools take it: <c>generate_image</c> has saved there since 2026-10-09, so "make that
/// my splash" read nothing until then. A picture already in the splash folder is refused (the copy would land beside itself).
/// Since 2026-10-12 (the user's call) it is one of the picture tools (<c>ChatScreen.ImageTools</c>, switch <c>Image tools</c>), its
/// sentences <see cref="ImageText"/>'s: it sat in the ComfyUI group until then, offered only with a ComfyUI server set up, though a
/// camera shot, a screen capture or an edit makes as good a splash as a generated picture. Never a <c>/botchat</c> bot's.
/// </summary>
public sealed class SetSplashImageTool : AIFunction
{
    public const string ToolName = "set_splash_image";
    public const string PathArgument = "path";
    public const string NameArgument = "name";

    private static readonly JsonElement Schema = SchemaFor(images: false);

    private static readonly JsonElement ImagesSchema = SchemaFor(images: true);

    private readonly WorkingDirectory _files;
    private readonly WorkingDirectory? _images;
    private readonly Func<string> _splashDirectory;
    private readonly Func<bool>? _robotSplash;

    /// <param name="splashDirectory">The loaded profile's splash folder, read at each call (a profile switch needs no rebuild).</param>
    /// <param name="images">The profile's images folder, whose pictures are read by their full paths (2026-10-10); null = the working directory alone.</param>
    /// <param name="robotSplash">Whether <c>Welcome splash</c> is <c>robot</c> now (2026-10-20): the result then says the pictures wait for <c>image</c>; null = never.</param>
    public SetSplashImageTool(WorkingDirectory files, Func<string> splashDirectory, WorkingDirectory? images = null, Func<bool>? robotSplash = null)
    {
        _robotSplash = robotSplash;
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _splashDirectory = splashDirectory ?? throw new ArgumentNullException(nameof(splashDirectory));
        _images = images;
    }

    public override string Name => ToolName;

    public override string Description => Describe(_images?.Root);

    /// <summary>
    /// The description; <paramref name="imagesRoot"/> (2026-10-10) names the profile's images folder, whose pictures it takes by
    /// their full paths, <c>view_image</c>'s wording. Pinned.
    /// </summary>
    public static string Describe(string? imagesRoot = null) =>
        "Adds a picture from the working directory" + (imagesRoot is null ? "" : " or the user's images folder (" + imagesRoot + ", by its full path)") +
        " to the user's splash screens (the startup picture and /splash) by copying it into their profile's splash folder. " +
        "Use it when the user asks to make a picture — often one generate_image just made — their splash or welcome screen.";

    public override JsonElement JsonSchema => _images is null ? Schema : ImagesSchema;

    // The schema; with the images folder (2026-10-10) the path's description says a full path there works.
    private static JsonElement SchemaFor(bool images) => ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The picture under the working directory{{(images ? ", or a full path in the user's images folder," : "")}} to add to the splash screens (png, jpg, gif, webp or bmp)." },
            "name": { "type": "string", "description": "The file name it gets in the splash folder, without the extension; left out, the picture's own name." }
          },
          "required": ["path"]
        }
        """);

    /// <summary>Copies <paramref name="path"/> into the splash folder as <paramref name="name"/> (or its own name); the result sentence either way.</summary>
    public string Set(string path, string? name)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path))
        {
            return FileText.PathRequired(PathArgument);
        }

        var files = FileTool.For(_files, _images, path);
        var read = files.ReadImage(path);
        if (read.Outcome != FileOutcome.Ok)
        {
            return read.Outcome is FileOutcome.NotAnImage or FileOutcome.ImageTooBig ? ImageText.SplashNotAnImage(read.Relative) : FileText.Error(read.Outcome, read.Relative, "read", read.Detail);
        }

        files.Resolve(path, forWrite: false, out string source);
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (!ImageFile.IsImagePath(source))
        {
            return ImageText.SplashNotAnImage(read.Relative);
        }

        string folder = _splashDirectory();
        if (string.Equals(Path.GetDirectoryName(source), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase))
        {
            return ImageText.SplashAlready(read.Relative);
        }

        string stem = ComfyStudio.SafeName(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(source) : Path.GetFileNameWithoutExtension(name.Trim()));
        try
        {
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, stem + extension);
            for (int n = 2; File.Exists(target); n++)
            {
                target = Path.Combine(folder, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + extension);
            }

            File.Copy(source, target, overwrite: false);
            bool only = Directory.EnumerateFiles(folder).Count(ImageFile.IsImagePath) == 1;
            return ImageText.SplashSet(read.Relative, target, only) + (_robotSplash?.Invoke() == true ? ImageText.SplashRobotNote : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImageText.SplashFailed(folder, ex.Message);
        }
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string name = ToolArguments.ReadString(arguments, NameArgument);
        return new ValueTask<object?>(Set(ToolArguments.ReadString(arguments, PathArgument), name.Length == 0 ? null : name));
    }
}
