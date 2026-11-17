using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Render;
using NeonSidekick.UI;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What <c>render_picture</c>, <c>render_chart</c> and <c>render_collage</c> share (2026-11-16, plan sharded-hugging-puppy #9): the picture's
/// width, scale and ground read and held to their ranges, the draw through <see cref="PictureRendering"/>, and the save — at <c>to</c> (over a
/// file only with <c>overwrite</c>), else the first free <c>diagram.png</c>, <c>diagram-2.png</c>… at the working directory's top, never over
/// anything. The answer is always the picture fitted for the model beside the line (<see cref="ToolImageResult"/>): what reaches the model is
/// the turn's picture budget's call, and the transcript's thumbnail reads it. <c>Image tools</c> rules them, offered while a browser is found
/// (<see cref="Available"/>); not in plan mode.
/// The model's text is held to <see cref="MaxChars"/> as it streams (<see cref="IArgumentCeiling"/>).
/// </summary>
public abstract class RenderTool : FileTool, IArgumentCeiling
{
    public const string ToArgument = "to";
    public const string WidthArgument = "width";
    public const string ScaleArgument = "scale";
    public const string BackgroundArgument = "background";

    public const int MinWidth = 200;
    public const int MaxWidth = 4096;
    public const double MinScale = 1;
    public const double MaxScale = 3;
    public const double DefaultScale = 2;

    /// <summary>The most characters of arguments a call sends: a runaway stream of SVG or HTML is stopped there.</summary>
    public const int MaxChars = 512 * 1024;

    /// <summary>The largest picture a page may name for inlining.</summary>
    public const long MaxInlineBytes = 20L * 1024 * 1024;

    protected RenderTool(WorkingDirectory files, WorkingDirectory? images, PictureRendering rendering) : base(files, images)
    {
        Rendering = rendering ?? throw new ArgumentNullException(nameof(rendering));
    }

    protected PictureRendering Rendering { get; }

    public int MaxArgumentChars => MaxChars;

    /// <summary>Whether a browser is found to draw with now (the turn's list leaves the tool out otherwise).</summary>
    public bool Available => Rendering.Executable is not null;

    /// <summary>The width in CSS pixels: <paramref name="fallback"/> when not given; null with the refusal outside its range.</summary>
    protected static int? ReadWidth(AIFunctionArguments arguments, int fallback, out string? error)
    {
        error = null;
        if (!ToolArguments.TryReadInt32(arguments, WidthArgument, out int? value, out string raw) || value is < MinWidth or > MaxWidth)
        {
            error = RenderText.BadNumber(WidthArgument, raw.Length > 0 ? raw : Shown(value), Range(MinWidth, MaxWidth));
            return null;
        }

        return value ?? fallback;
    }

    /// <summary>The pixels per CSS pixel: <see cref="DefaultScale"/> when not given; null with the refusal outside its range.</summary>
    protected static double? ReadScale(AIFunctionArguments arguments, out string? error)
    {
        error = null;
        if (!ToolArguments.TryReadDouble(arguments, ScaleArgument, out double? value, out string raw) || value is < MinScale or > MaxScale || (value is { } v && !double.IsFinite(v)))
        {
            error = RenderText.BadNumber(ScaleArgument, raw.Length > 0 ? raw : Shown(value), Range(MinScale, MaxScale));
            return null;
        }

        return value ?? DefaultScale;
    }

    /// <summary>The ground: white when not given, null for <c>transparent</c>, else a <c>#RRGGBB</c>; false with the refusal for anything else.</summary>
    protected static bool ReadBackground(AIFunctionArguments arguments, out string? background, out string? error)
    {
        error = null;
        string text = ToolArguments.ReadString(arguments, BackgroundArgument).Trim();
        background = "#FFFFFF";
        if (text.Length == 0 || text.Equals("white", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase) || text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            background = null;
            return true;
        }

        if (text.Equals("black", StringComparison.OrdinalIgnoreCase))
        {
            background = "#000000";
            return true;
        }

        if (ThemeFile.TryParseColor(text, out var color))
        {
            background = Theme.ToHex(color);
            return true;
        }

        error = RenderText.BadBackground(text);
        return false;
    }

    /// <summary>
    /// The picture drawn and saved; the answer line (with <paramref name="notes"/> after it) and the picture, or the refusal. A named
    /// <paramref name="to"/> must end in one of <paramref name="extensions"/>; with none, <paramref name="stem"/> plus the first extension.
    /// </summary>
    protected async Task<object?> DrawAndSaveAsync(RenderJob job, string to, bool overwrite, string stem, IReadOnlyList<string> extensions, string notes, CancellationToken cancellationToken)
    {
        if (to.Length > 0 && !extensions.Contains(Path.GetExtension(to), StringComparer.OrdinalIgnoreCase))
        {
            return RenderText.BadExtension(to, string.Join(" or ", extensions));
        }

        var drawn = await Rendering.RenderAsync(job, cancellationToken).ConfigureAwait(false);
        if (drawn.MermaidError is { } complaint)
        {
            return RenderText.MermaidError(complaint);
        }

        if (drawn.TooLarge)
        {
            return RenderText.TooLarge;
        }

        if (!drawn.Ok || drawn.Bytes is not { } bytes)
        {
            return drawn.NoBrowser ? drawn.Detail : RenderText.Failed(drawn.Detail);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var written = to.Length > 0 ? FilesFor(to).WriteBytes(to, bytes, overwrite) : WriteNumbered(stem + extensions[0], bytes);
        if (written.Outcome != FileOutcome.Ok)
        {
            return FileText.Error(written.Outcome, written.Relative, "write", written.Detail);
        }

        string text = RenderText.Written(written.Relative, drawn.Width, drawn.Height, bytes.LongLength, drawn.Cut) + notes;
        var shown = FilesFor(written.Relative).ReadImage(written.Relative);
        return shown.Outcome == FileOutcome.Ok && shown.Image is { } image ? new ToolImageResult(text, [image]) : text;
    }

    /// <summary>
    /// A picture a page names (<c>&lt;img src="photo.jpg"&gt;</c>), read where the picture tools read: the working directory, or a full path
    /// in the profile's images folder. Anything with a scheme (the web, <c>file:</c>) or outside both is null, so it is dropped.
    /// </summary>
    protected byte[]? Inline(string address)
    {
        string path = address.Trim();
        int cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        if (path.Length == 0 || path.StartsWith("//", StringComparison.Ordinal) || (path.Contains(':', StringComparison.Ordinal) && !Path.IsPathRooted(path)))
        {
            return null;
        }

        try
        {
            path = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return null;
        }

        var read = FilesFor(path).ReadBytes(path, MaxInlineBytes);
        return read.Outcome == FileOutcome.Ok ? read.Bytes : null;
    }

    // An unnamed picture: the first free name of the stem's spelling at the top, -2, -3…, never over a file.
    private WriteResult WriteNumbered(string target, byte[] bytes)
    {
        WriteResult written;
        int n = 1;
        do
        {
            written = Files.WriteBytes(ImageOutput.Numbered(target, n), bytes, overwrite: false);
        }
        while (written.Outcome == FileOutcome.Exists && ++n < 1000);
        return written;
    }

    /// <summary>A number the model sent in range of its type but not of the tool's, as it reads back.</summary>
    protected static string Shown(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Range(double min, double max) =>
        min.ToString(CultureInfo.InvariantCulture) + "–" + max.ToString(CultureInfo.InvariantCulture);
}
