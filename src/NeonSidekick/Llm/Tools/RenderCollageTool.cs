using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Render;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>render_collage(paths | folder, …)</c> (2026-11-16, plan sharded-hugging-puppy #9): pictures laid out in a grid on one picture
/// (<see cref="CollageHtml"/>), read where the picture tools read them — the working directory, or full paths in the profile's images folder,
/// so camera stills and screen captures can go in. Each is fitted first (<see cref="ImageFile.TryLoad(byte[], string, out ImageAttachment?, out ImageLoadFailure, bool)"/>)
/// so the page stays small; a file that is no picture is skipped and named. Saved as a JPEG (<c>collage.jpg</c>), or a PNG when <c>to</c> says so.
/// </summary>
public sealed class RenderCollageTool(WorkingDirectory files, PictureRendering rendering, WorkingDirectory? images = null) : RenderTool(files, images, rendering)
{
    public const string ToolName = "render_collage";

    public const string FolderArgument = "folder";
    public const string PatternArgument = "pattern";
    public const string ColumnsArgument = "columns";
    public const string CaptionsArgument = "captions";
    public const string TitleArgument = "title";
    public const string StyleArgument = "style";

    /// <summary>The <c>captions</c> word that captions each picture with its file name.</summary>
    public const string NamesWord = "names";

    public const int DefaultWidth = 2400;

    private static readonly string[] Styles = ["light", "dark"];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "paths": { "type": "array", "items": { "type": "string" }, "description": "The pictures in order: relative to the working directory, or full paths of saved pictures. At most 48." },
            "folder": { "type": "string", "description": "Instead of paths: a folder whose pictures go in, by name." },
            "pattern": { "type": "string", "description": "With folder: the file names to take, such as *.jpg or IMG_*. Default every picture." },
            "columns": { "type": "integer", "minimum": 1, "maximum": 12, "description": "Pictures per row. Default about square." },
            "captions": { "type": "array", "items": { "type": "string" }, "description": "A caption per picture in order, or [\"names\"] for the file names. Default none." },
            "title": { "type": "string", "description": "A title over the grid." },
            "style": { "type": "string", "enum": ["light", "dark"], "description": "light (default) or dark ground." },
            "to": { "type": "string", "description": "The .jpg or .png to write, relative to the working directory. Default: collage.jpg at the top, numbered when taken." },
            "overwrite": { "type": "boolean", "description": "Only with to: replace a file already there. Default false." },
            "width": { "type": "integer", "minimum": 200, "maximum": 4096, "description": "The collage's width in CSS pixels. Default 2400." },
            "scale": { "type": "number", "minimum": 1, "maximum": 3, "description": "Pixels per CSS pixel. Default 2 (held to 4096 pixels a side)." }
          }
        }
        """);

    public override string Name => ToolName;

    public override string Description => RenderText.CollageDescription;

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadStringList(arguments, PathsArgument, out var paths, out string raw))
        {
            return RenderText.BadList(PathsArgument, raw);
        }

        string folder = ToolArguments.ReadString(arguments, FolderArgument).Trim();
        paths = [.. paths.Select(p => p.Trim()).Where(p => p.Length > 0)];
        if (paths.Count > 0 && folder.Length > 0)
        {
            return RenderText.OnePictureSource;
        }

        if (!ToolArguments.TryReadStringList(arguments, CaptionsArgument, out var captions, out raw))
        {
            return RenderText.BadList(CaptionsArgument, raw);
        }

        if (!ToolArguments.TryReadInt32(arguments, ColumnsArgument, out int? columns, out raw) || columns is < 1 or > 12)
        {
            return RenderText.BadNumber(ColumnsArgument, raw.Length > 0 ? raw : Shown(columns), "1–12");
        }

        string styleText = ToolArguments.ReadString(arguments, StyleArgument).Trim().ToLowerInvariant();
        if (styleText.Length > 0 && Array.IndexOf(Styles, styleText) < 0)
        {
            return RenderText.BadChoice(StyleArgument, styleText, Styles);
        }

        if (ReadOverwrite(arguments, out raw) is not { } overwrite)
        {
            return FileText.BadBoolean(OverwriteArgument, raw);
        }

        if (ReadWidth(arguments, DefaultWidth, out string? error) is not { } width || ReadScale(arguments, out error) is not { } scale)
        {
            return error;
        }

        if (folder.Length > 0 && Folder(folder, ToolArguments.ReadString(arguments, PatternArgument).Trim(), out error) is { } listed)
        {
            paths = listed;
        }
        else if (error is not null)
        {
            return error;
        }

        if (paths.Count == 0)
        {
            return RenderText.NoPictures;
        }

        if (paths.Count > CollageHtml.MaxPictures)
        {
            return RenderText.TooManyPictures(CollageHtml.MaxPictures);
        }

        bool names = captions.Count == 1 && captions[0].Trim().Equals(NamesWord, StringComparison.OrdinalIgnoreCase);
        if (!names && captions.Count > 0 && captions.Count != paths.Count)
        {
            return RenderText.CaptionsCount(captions.Count, paths.Count);
        }

        var items = new List<CollageItem>(paths.Count);
        var skipped = new List<string>();
        for (int i = 0; i < paths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = paths[i];
            var read = FilesFor(path).ReadBytes(path, MaxInlineBytes);
            if (read.Outcome != FileOutcome.Ok || read.Bytes is not { } bytes)
            {
                return FileText.Error(read.Outcome, path, "read", read.Detail);
            }

            if (!ImageFile.TryLoad(bytes, Path.GetFileName(path), out var image, out _, photo: true) || image is null)
            {
                skipped.Add(Path.GetFileName(path));
                continue;
            }

            string? caption = names ? Path.GetFileNameWithoutExtension(path) : captions.Count > 0 ? captions[i] : null;
            items.Add(new CollageItem(image.MediaType, image.Bytes, caption));
        }

        if (items.Count == 0)
        {
            return RenderText.NoPictures + RenderText.Skipped(skipped);
        }

        string to = ToolArguments.ReadString(arguments, ToArgument).Trim();
        var format = Path.GetExtension(to).Equals(".png", StringComparison.OrdinalIgnoreCase) ? PictureFormat.Png : PictureFormat.Jpeg;
        bool dark = styleText == "dark";
        string page = CollageHtml.Page(items, columns ?? CollageHtml.ColumnsFor(items.Count), ToolArguments.ReadString(arguments, TitleArgument), dark);
        var job = new RenderJob(null, page, format, width, scale, dark ? ChartSvg.DarkGround : ChartSvg.LightGround);
        return await DrawAndSaveAsync(job, to, overwrite, "collage", [".jpg", ".jpeg", ".png"], RenderText.Skipped(skipped), cancellationToken).ConfigureAwait(false);
    }

    // A folder's pictures by name, those the pattern matches (every picture without one); null with the refusal when the folder can't be listed.
    private IReadOnlyList<string>? Folder(string folder, string pattern, out string? error)
    {
        error = null;
        var listed = FilesFor(folder).List(folder);
        if (listed.Outcome != FileOutcome.Ok)
        {
            error = FileText.Error(listed.Outcome, folder, "list", listed.Detail);
            return null;
        }

        return [.. listed.Entries
            .Where(e => !e.IsDirectory && ImageFile.IsImagePath(e.Name) && (pattern.Length == 0 || PathGlob.MatchSegment(pattern, e.Name)))
            .Select(e => e.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => Path.Join(folder, name))];
    }
}
