using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Render;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>render_collage(paths | folder, …)</c> (2026-11-16, plan sharded-hugging-puppy #9): pictures laid out in a grid on one picture
/// (<see cref="CollageHtml"/>), read where the picture tools read them — the working directory, or full paths in the profile's images folder,
/// so camera stills and screen captures can go in. Each is fitted to its tile first (<see cref="ImageFile.TryLoad(byte[], string, out ImageAttachment?, out ImageLoadFailure, bool, int)"/>)
/// so the page stays small; a file that is no picture, too large or unreadable is skipped and named with its reason (2026-11-17, the
/// admin-themes-render review's #13, #77). A folder gives its first <see cref="CollageHtml.MaxPictures"/> pictures in natural name order
/// (#102, #137), never this tool's own earlier unnamed collages (#125). Saved as a JPEG (<c>collage.jpg</c>), or a PNG when <c>to</c> says so.
/// </summary>
public sealed class RenderCollageTool(WorkingDirectory files, PictureRendering rendering, WorkingDirectory? images = null) : RenderTool(files, images, rendering)
{
    public const string ToolName = "render_collage";

    public const string FolderArgument = "folder";
    public const string PatternArgument = "pattern";
    public const string ColumnsArgument = "columns";
    public const string CaptionsArgument = "captions";
    public const string TitleArgument = "title";

    /// <summary>The <c>captions</c> word that captions each picture with its file name.</summary>
    public const string NamesWord = "names";

    public const int DefaultWidth = 2400;

    public const int MaxColumns = 12;

    /// <summary>The stem an unnamed collage is saved under (<c>collage.jpg</c>, <c>collage-2.jpg</c>…).</summary>
    public const string Stem = "collage";

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "paths": { "type": "array", "items": { "type": "string" }, "description": "The pictures in order: relative to the working directory, or full paths of saved pictures. At most {{CollageHtml.MaxPictures}}." },
            "folder": { "type": "string", "description": "Instead of paths: a folder whose pictures go in, by name." },
            "pattern": { "type": "string", "description": "With folder: the file names to take, such as *.jpg or IMG_*. Default every picture." },
            "columns": { "type": "integer", "minimum": 1, "maximum": {{MaxColumns}}, "description": "Pictures per row. Default about square." },
            "captions": { "type": "array", "items": { "type": "string" }, "description": "A caption per picture in order, or [\"names\"] for the file names (so one picture can't be captioned \"names\" itself). Default none." },
            "title": { "type": "string", "description": "A title over the grid." },
            "style": { "type": "string", "enum": ["light", "dark"], "description": "light (default) or dark ground." },
            "to": { "type": "string", "description": "The .jpg or .png to write, relative to the working directory. Default: collage.jpg at the top, numbered when taken." },
            "overwrite": { "type": "boolean", "description": "Only with to: replace a file already there. Default false." },
            "width": { "type": "integer", "minimum": {{MinWidth}}, "maximum": {{MaxWidth}}, "description": "The collage's width in CSS pixels. Default 2400." },
            "scale": { "type": "number", "minimum": 1, "maximum": 3, "description": "Pixels per CSS pixel. Default 2 (held to {{BrowserPictureRenderer.MaxPixels}} pixels a side)." }
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
        string pattern = ToolArguments.ReadString(arguments, PatternArgument).Trim();
        paths = [.. paths.Select(p => p.Trim()).Where(p => p.Length > 0)];
        if (paths.Count > 0 && folder.Length > 0)
        {
            return RenderText.OnePictureSource;
        }

        if (pattern.Length > 0 && folder.Length == 0)
        {
            return RenderText.PatternNeedsFolder;
        }

        if (!ToolArguments.TryReadStringList(arguments, CaptionsArgument, out var captions, out raw))
        {
            return RenderText.BadList(CaptionsArgument, raw);
        }

        if (!ToolArguments.TryReadInt32(arguments, ColumnsArgument, out int? columns, out raw) || columns is < 1 or > MaxColumns)
        {
            return ArgumentText.OutOfRange(ColumnsArgument, raw.Length > 0 ? raw : Shown(columns), 1, MaxColumns);
        }

        if (!ReadStyle(arguments, out bool dark, out string? error) || !ReadTarget(arguments, out string to, out bool overwrite, out error))
        {
            return error;
        }

        if (ReadWidth(arguments, DefaultWidth, out error) is not { } width || ReadScale(arguments, out error) is not { } scale)
        {
            return error;
        }

        var skipped = new List<(string Name, string Reason)>();
        string tail = "";
        if (folder.Length > 0)
        {
            if (Folder(folder, pattern, cancellationToken, out error) is not { } listed)
            {
                return error;
            }

            paths = listed.Paths;
            skipped.AddRange(listed.EarlierCollages.Select(name => (name, RenderText.SkipEarlierCollage)));
            tail = (listed.Found > listed.Paths.Count ? RenderText.FirstPictures(listed.Paths.Count, listed.Found) : "")
                + (listed.ListCut ? RenderText.FolderListCut(WorkingDirectory.MaxResultLimit) : "");
        }

        if (paths.Count == 0)
        {
            return RenderText.NoPictures + RenderText.Skipped(skipped);
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

        // Each picture fitted to its tile (#78): the columns first guessed from the candidates, the settled ones after the skips.
        int guess = Math.Min(columns ?? CollageHtml.ColumnsFor(paths.Count), paths.Count);
        int Tile(int count) => (int)Math.Clamp(width / (double)count * scale, 64, 4096);
        var kept = new List<(byte[] Bytes, string Name, string? Caption, ImageAttachment Image)>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = paths[i];
            string name = Path.GetFileName(path);
            var read = FilesFor(path).ReadBytes(path, ImageFile.MaxFileBytes);
            if (read.Outcome is FileOutcome.TooBig or FileOutcome.ImageTooBig)
            {
                skipped.Add((name, RenderText.SkipTooLarge));
                continue;
            }

            if (read.Outcome == FileOutcome.Failed)
            {
                skipped.Add((name, RenderText.SkipUnreadable(read.Detail)));
                continue;
            }

            // A wrong path (outside, missing, a folder) is the model's to fix, not one to leave out quietly.
            if (read.Outcome != FileOutcome.Ok || read.Bytes is not { } bytes)
            {
                return FileText.Error(read.Outcome, path, "read", read.Detail);
            }

            if (!ImageFile.TryLoad(bytes, name, out var image, out var failure, photo: true, maxSide: Tile(guess)) || image is null)
            {
                skipped.Add((name, failure == ImageLoadFailure.TooLarge ? RenderText.SkipTooLarge : RenderText.SkipNotPicture));
                continue;
            }

            string? caption = names ? Path.GetFileNameWithoutExtension(path) : captions.Count > 0 ? captions[i] : null;
            kept.Add((bytes, name, caption, image));
        }

        if (kept.Count == 0)
        {
            return RenderText.NoPictures + RenderText.Skipped(skipped);
        }

        int settled = Math.Min(columns ?? CollageHtml.ColumnsFor(kept.Count), kept.Count);   // #181: never more columns than pictures
        var items = new List<CollageItem>(kept.Count);
        foreach (var (bytes, name, caption, image) in kept)
        {
            // Fitted again only when the skips changed the tile, so no picture is drawn larger than it was fitted.
            var fitted = settled != guess && ImageFile.TryLoad(bytes, name, out var again, out _, photo: true, maxSide: Tile(settled)) && again is not null ? again : image;
            items.Add(new CollageItem(fitted.MediaType, fitted.Bytes, caption));
        }

        var format = Path.GetExtension(to).Equals(".png", StringComparison.OrdinalIgnoreCase) ? PictureFormat.Png : PictureFormat.Jpeg;
        string page = CollageHtml.Page(items, settled, ToolArguments.ReadString(arguments, TitleArgument), dark);
        var job = new RenderJob(null, page, format, width, scale, PictureColors.For(dark).GroundColor, Svg: false);
        return await DrawAndSaveAsync(job, to, overwrite, Stem, Extensions, RenderText.Skipped(skipped) + tail, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a folder gives a collage: its first pictures in order, how many it holds, whether its listing stopped, and the earlier collages left out.</summary>
    private sealed record FolderPictures(IReadOnlyList<string> Paths, int Found, bool ListCut, IReadOnlyList<string> EarlierCollages);

    /// <summary>
    /// A folder's pictures (2026-11-17, #102, #125, #133, #137): listed up to <see cref="WorkingDirectory.MaxResultLimit"/>, those
    /// <paramref name="pattern"/> matches (every picture without one) in natural name order, the first <see cref="CollageHtml.MaxPictures"/>
    /// taken. At the working directory's top, the names an unnamed collage is saved under are left out and named, so a collage of <c>.</c>
    /// never takes in the last one. Null with the refusal when the folder can't be listed.
    /// </summary>
    private FolderPictures? Folder(string folder, string pattern, CancellationToken cancellationToken, out string? error)
    {
        error = null;
        var root = FilesFor(folder);
        var listed = root.List(folder, cancellationToken, WorkingDirectory.MaxResultLimit);
        if (listed.Outcome != FileOutcome.Ok)
        {
            error = FileText.Error(listed.Outcome, folder, "list", listed.Detail);
            return null;
        }

        bool top = ReferenceEquals(root, Files) && root.Resolve(folder, forWrite: false, out string full) == FileOutcome.Ok
            && string.Equals(Path.TrimEndingDirectorySeparator(full), root.Root, CaseRule.Comparison(root.Root));
        var earlier = new List<string>();
        var pictures = new List<string>();
        foreach (var entry in listed.Entries)
        {
            if (entry.IsDirectory || !ImageFile.IsImagePath(entry.Name) || (pattern.Length > 0 && !PathGlob.MatchSegment(pattern, entry.Name)))
            {
                continue;
            }

            if (top && IsEarlierCollage(entry.Name))
            {
                earlier.Add(entry.Name);
                continue;
            }

            pictures.Add(entry.Name);
        }

        var ordered = pictures.OrderBy(n => n, NaturalComparer.Instance).ThenBy(n => n, StringComparer.Ordinal).ToList();
        return new FolderPictures(
            [.. ordered.Take(CollageHtml.MaxPictures).Select(name => Path.Join(folder, name))],
            ordered.Count,
            listed.Truncated,
            earlier);
    }

    /// <summary>Whether <paramref name="name"/> is one an unnamed collage is saved under: <c>collage</c> or <c>collage-N</c>, as .jpg, .jpeg or .png.</summary>
    public static bool IsEarlierCollage(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string extension = Path.GetExtension(name);
        if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Equals(Stem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length > Stem.Length + 1 && stem.StartsWith(Stem + "-", StringComparison.OrdinalIgnoreCase)
            && stem[(Stem.Length + 1)..].All(char.IsAsciiDigit);
    }
}
