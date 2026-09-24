using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Comfy;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>set_splash_image(path, name?)</c> (2026-09-24, the user's ask: generated "splash / persona art"): copies a
/// picture from the working directory into the loaded profile's <c>splash</c> folder, which the splash walk reads at
/// every show (<see cref="UI.SplashImages.FromDirectory"/>), so "make that my splash" ends with the picture on the next
/// <c>/splash</c>. The folder is outside the sandbox, which is why this is a tool of its own rather than a
/// <c>copy</c>: it writes there and nowhere else, never over a picture already there (a number is added to the name).
/// The result says when the copy is the folder's only picture, since a profile's folder with any picture stands in for
/// the bundled set — the user should know the built-in splashes are hidden until more are added.
/// </summary>
public sealed class SetSplashImageTool : AIFunction
{
    public const string ToolName = "set_splash_image";
    public const string PathArgument = "path";
    public const string NameArgument = "name";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The picture under the working directory to add to the splash screens (png, jpg, gif, webp or bmp)." },
            "name": { "type": "string", "description": "The file name it gets in the splash folder, without the extension; left out, the picture's own name." }
          },
          "required": ["path"]
        }
        """);

    private readonly WorkingDirectory _files;
    private readonly Func<string> _splashDirectory;

    /// <param name="splashDirectory">The loaded profile's splash folder, read at each call (a profile switch needs no rebuild).</param>
    public SetSplashImageTool(WorkingDirectory files, Func<string> splashDirectory)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _splashDirectory = splashDirectory ?? throw new ArgumentNullException(nameof(splashDirectory));
    }

    public override string Name => ToolName;

    public override string Description =>
        "Adds a picture from the working directory to the user's splash screens (the startup picture and /splash) by copying it into their profile's splash folder. " +
        "Use it when the user asks to make a picture — often one generate_image just made — their splash or welcome screen.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>Copies <paramref name="path"/> into the splash folder as <paramref name="name"/> (or its own name); the result sentence either way.</summary>
    public string Set(string path, string? name)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path))
        {
            return FileText.PathRequired(PathArgument);
        }

        var read = _files.ReadImage(path);
        if (read.Outcome != FileOutcome.Ok)
        {
            return read.Outcome is FileOutcome.NotAnImage or FileOutcome.ImageTooBig ? ComfyText.SplashNotAnImage(read.Relative) : FileText.Error(read.Outcome, read.Relative, "read", read.Detail);
        }

        _files.Resolve(path, forWrite: false, out string source);
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (!ImageFile.IsImagePath(source))
        {
            return ComfyText.SplashNotAnImage(read.Relative);
        }

        string folder = _splashDirectory();
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
            return ComfyText.SplashSet(read.Relative, target, only);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ComfyText.SplashFailed(folder, ex.Message);
        }
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string name = ToolArguments.ReadString(arguments, NameArgument);
        return new ValueTask<object?>(Set(ToolArguments.ReadString(arguments, PathArgument), name.Length == 0 ? null : name));
    }
}
