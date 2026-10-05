using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>image_info(path | paths)</c> (2026-10-04, with <c>image_edit</c>): each picture's format, upright size, file size, frames,
/// transparency and EXIF orientation, read from its header (nothing is downscaled or sent to the model), the metadata a JPEG, PNG,
/// WebP or GIF carries (<see cref="MetadataStripper.Survey"/>, 2026-10-05), then one line naming the
/// formats this Windows can write and <c>image_edit</c>'s defaults in force. One of the file tools, so <c>File tools</c> rules it;
/// read-only, so plan mode keeps it.
/// </summary>
public sealed class ImageInfoTool : FileTool
{
    public const string ToolName = "image_info";

    public const string PathsArgument = "paths";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "One picture, relative to the working directory." },
            "paths": { "type": "array", "items": { "type": "string" }, "description": "Several pictures in one call, each relative to the working directory." }
          }
        }
        """);

    private readonly Func<AppSettingsData> _effective;

    public ImageInfoTool(WorkingDirectory files, Func<AppSettingsData> effective) : base(files)
    {
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    public override string Name => ToolName;

    public override string Description => DescriptionText;

    /// <summary>The description. Pinned.</summary>
    public const string DescriptionText =
        "Reads facts about pictures in the working directory without loading them for you to see: format, size (upright), file size, frames, transparency and EXIF orientation, " +
        "which metadata it carries (EXIF, GPS, XMP, data after the picture…), and which formats image_edit can write here. Use it before image_edit to plan a resize or crop; use view_image to look at a picture.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadStringList(arguments, PathsArgument, out var several, out string raw))
        {
            return FileText.BadStringList(PathsArgument, raw);
        }

        var paths = new List<string>(several.Count + 1);
        string one = ReadPath(arguments).Trim();
        if (one.Length > 0)
        {
            paths.Add(one);
        }

        paths.AddRange(several.Select(p => p.Trim()).Where(p => p.Length > 0));
        if (paths.Count == 0)
        {
            return ImageText.NoPathError;
        }

        var effective = _effective();
        return await Task.Run(() => Describe(Files, paths, effective), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The answer for <paramref name="paths"/>: one line each, success or error, then the formats line.</summary>
    public static string Describe(WorkingDirectory files, IReadOnlyList<string> paths, AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(effective);
        var lines = new List<string>(paths.Count + 1);
        foreach (var path in paths)
        {
            var read = files.ReadBytes(path, ImageEditor.MaxSourceBytes);
            if (read.Outcome != FileOutcome.Ok || read.Bytes is null)
            {
                lines.Add(read.Outcome == FileOutcome.TooBig ? read.Relative + ": " + ImageText.SourceTooBig : FileText.Error(read.Outcome, read.Relative, "read", read.Detail));
                continue;
            }

            lines.Add(ImageEditor.Info(read.Bytes) is { } info ? ImageText.Info(read.Relative, info, MetadataStripper.Survey(read.Bytes)) : FileText.NotAnImage(read.Relative));
        }

        lines.Add(ImageText.Formats(ImageFormats.WritableFormats(), ImageEditTool.QualityOf(effective), ImageWords.MetadataName(effective.ImageEditMetadata), effective.ImageEditOutputFolder));
        return string.Join("\n", lines);
    }
}
