using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Pdf;
using NeonSidekick.Render;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What <c>render_picture</c>, <c>render_chart</c> and <c>render_collage</c> share (2026-11-16, plan sharded-hugging-puppy #9): the picture's
/// width, scale, ground and style read and held to their ranges, the draw through <see cref="PictureRendering"/>, and the save — at <c>to</c>
/// (over a file only with <c>overwrite</c>, checked before anything is drawn), else the first free <c>diagram.png</c>, <c>diagram-2.png</c>… at
/// the working directory's top, never over anything. The answer is always the picture fitted for the model beside the line
/// (<see cref="ToolImageResult"/>), made from the bytes drawn: what reaches the model is the turn's picture budget's call, and the
/// transcript's thumbnail reads it. <c>Image tools</c> rules them, offered while a browser is found (the turn's <c>Drawable</c>); not in plan
/// mode. The model's text is held to <see cref="MaxChars"/> as it streams (<see cref="IArgumentCeiling"/>).
/// </summary>
public abstract class RenderTool : FileTool, IArgumentCeiling
{
    public const string ToArgument = "to";
    public const string WidthArgument = "width";
    public const string ScaleArgument = "scale";
    public const string BackgroundArgument = "background";
    public const string StyleArgument = "style";

    public const int MinWidth = 200;
    public const int MaxWidth = BrowserPictureRenderer.MaxPixels;
    public const double MinScale = 1;
    public const double MaxScale = 3;
    public const double DefaultScale = 2;

    /// <summary>The most characters of arguments a call sends: a runaway stream of SVG or HTML is stopped there.</summary>
    public const int MaxChars = 512 * 1024;

    private static readonly string[] Styles = ["light", "dark"];

    protected RenderTool(WorkingDirectory files, WorkingDirectory? images, PictureRendering rendering) : base(files, images)
    {
        Rendering = rendering ?? throw new ArgumentNullException(nameof(rendering));
    }

    protected PictureRendering Rendering { get; }

    public int MaxArgumentChars => MaxChars;

    /// <summary>The width in CSS pixels: <paramref name="fallback"/> when not given; null with the refusal outside its range.</summary>
    protected static int? ReadWidth(AIFunctionArguments arguments, int fallback, out string? error)
    {
        error = null;
        if (!ToolArguments.TryReadInt32(arguments, WidthArgument, out int? value, out string raw) || value is < MinWidth or > MaxWidth)
        {
            error = ArgumentText.OutOfRange(WidthArgument, raw.Length > 0 ? raw : Shown(value), MinWidth, MaxWidth);
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

    /// <summary>
    /// The ground: white when not given, null for <c>transparent</c> (or <c>none</c>, or any color of alpha 0), else the color as the one
    /// parser reads it (2026-11-17, the admin-themes-render review's #55, #210: <c>#rgb</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c> or a name); false
    /// with the refusal for anything else.
    /// </summary>
    protected static bool ReadBackground(AIFunctionArguments arguments, out System.Drawing.Color? background, out string? error)
    {
        error = null;
        string text = ToolArguments.ReadString(arguments, BackgroundArgument).Trim();
        background = System.Drawing.Color.FromArgb(255, 255, 255, 255);
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
            background = System.Drawing.Color.FromArgb(255, 0, 0, 0);
            return true;
        }

        if (ImageColor.TryParseColor(text, out var color))
        {
            background = color.A == 0 ? null : color;
            return true;
        }

        error = RenderText.BadBackground(text);
        return false;
    }

    /// <summary>The <c>style</c>: true for <c>dark</c>, false for <c>light</c> or none; false with the refusal for anything else (2026-11-17, #58: one reader).</summary>
    protected static bool ReadStyle(AIFunctionArguments arguments, out bool dark, out string? error)
    {
        error = null;
        string text = ToolArguments.ReadString(arguments, StyleArgument).Trim().ToLowerInvariant();
        dark = text == "dark";
        if (text.Length > 0 && Array.IndexOf(Styles, text) < 0)
        {
            error = RenderText.BadChoice(StyleArgument, text, Styles);
            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>to</c> and <c>overwrite</c>: false with the refusal for an <c>overwrite</c> that is no boolean, or one sent without <c>to</c>
    /// (2026-11-17, #193: an unnamed picture is never written over anything, so it would be ignored).
    /// </summary>
    protected static bool ReadTarget(AIFunctionArguments arguments, out string to, out bool overwrite, out string? error)
    {
        error = null;
        to = ToolArguments.ReadString(arguments, ToArgument).Trim();
        overwrite = false;
        if (ReadOverwrite(arguments, out string raw) is not { } asked)
        {
            error = FileText.BadBoolean(OverwriteArgument, raw);
            return false;
        }

        overwrite = asked;
        if (overwrite && to.Length == 0)
        {
            error = RenderText.OverwriteNeedsTo;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The pictures a page names, read as the PDF tools read them (2026-11-17, #42, #46, #52, #165, #184): the working directory, or a full path
    /// in the profile's images folder; <see cref="ImageFile.MaxFileBytes"/> a picture and <see cref="PdfConverter.MaxInlineBytes"/> in all;
    /// what was left out is in <see cref="SandboxPictures.Missed"/> for the answer.
    /// </summary>
    protected SandboxPictures Pictures() => new(Files, Files.Root, PdfConverter.MaxInlineBytes, Images);

    /// <summary>
    /// The picture drawn and saved; the answer line (with <paramref name="notes"/> after it) and the picture, or the refusal. A named
    /// <paramref name="to"/> must end in one of <paramref name="extensions"/> and is judged before anything is drawn (2026-11-17, #9: a write
    /// the sandbox refuses, or a file there without <paramref name="overwrite"/>, cost a whole draw first); with none, <paramref name="stem"/>
    /// plus the first extension, numbered as the camera's and the screenshots' are (#128).
    /// </summary>
    protected async Task<object?> DrawAndSaveAsync(RenderJob job, string to, bool overwrite, string stem, IReadOnlyList<string> extensions, string notes, CancellationToken cancellationToken)
    {
        if (to.Length > 0)
        {
            if (!extensions.Contains(Path.GetExtension(to), StringComparer.OrdinalIgnoreCase))
            {
                return RenderText.BadExtension(to, string.Join(" or ", extensions));
            }

            var outcome = FilesFor(to).Resolve(to, forWrite: true, out string full);
            if (outcome != FileOutcome.Ok)
            {
                return FileText.Error(outcome, to, "write");
            }

            if (Directory.Exists(full))
            {
                return FileText.Error(FileOutcome.IsDirectory, to, "write");
            }

            if (!overwrite && File.Exists(full))
            {
                return FileText.Error(FileOutcome.Exists, to, "write");
            }
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
        WriteResult written;
        if (to.Length > 0)
        {
            written = FilesFor(to).WriteBytes(to, bytes, overwrite);
            if (written.Outcome != FileOutcome.Ok)
            {
                return FileText.Error(written.Outcome, written.Relative, "write", written.Detail);
            }
        }
        else
        {
            (written, string? failed) = ImageOutput.WriteNumbered(Files, stem, extensions[0], bytes);
            if (failed is not null)
            {
                return failed;
            }
        }

        // The model's copy from the bytes drawn (#199), not the file read back: a file changed meanwhile can't stand in for it.
        string text = RenderText.Written(written.Relative, drawn.Width, drawn.Height, bytes.LongLength, drawn.Cut, drawn.Wide) + notes;
        return ImageFile.TryLoad(bytes, written.Relative, out var image, out _) && image is not null
            ? new ToolImageResult(text, [image with { Path = written.Relative }])
            : text;
    }

    /// <summary>A number the model sent in range of its type but not of the tool's, as it reads back.</summary>
    protected static string Shown(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Range(double min, double max) =>
        min.ToString(CultureInfo.InvariantCulture) + "–" + max.ToString(CultureInfo.InvariantCulture);
}
