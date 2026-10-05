using System.Drawing;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Settings;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>image_edit(path, …)</c> (2026-10-04, the user's ask: resize up and down and convert, then the geometry and effects): every
/// step in one pass through <see cref="ImageEditor"/> — crop, resize, rotate in quarter turns, flip, a colour filter and
/// adjustments, blur, sharpen, a border — then one encode into PNG, JPEG, GIF, BMP, TIFF, or JPEG XL/HEIF where Windows has
/// them. The arguments are flat (a small local model handles that better than nested objects). The picture lands beside the
/// source (or in <c>Image edit output folder</c>) as <c>photo-edited.png</c>, or <c>photo.png</c> for a bare conversion, a
/// <c>-2</c> on a clash; never over a file without <c>overwrite</c>. A file tool: <c>File tools</c> rules it, no confirm (a
/// sandbox write, as <c>write_file</c>'s), not in plan mode. <c>view: true</c> attaches the result as <c>view_image</c> would.
/// </summary>
public sealed class ImageEditTool : FileTool
{
    public const string ToolName = "image_edit";

    public const string ToArgument = "to";
    public const string FormatArgument = "format";
    public const string QualityArgument = "quality";
    public const string MaxKbArgument = "max_kb";
    public const string WidthArgument = "width";
    public const string HeightArgument = "height";
    public const string ScaleArgument = "scale";
    public const string FitArgument = "fit";
    public const string AnchorArgument = "anchor";
    public const string InterpolationArgument = "interpolation";
    public const string CropXArgument = "crop_x";
    public const string CropYArgument = "crop_y";
    public const string CropWidthArgument = "crop_width";
    public const string CropHeightArgument = "crop_height";
    public const string RotateArgument = "rotate";
    public const string FlipArgument = "flip";
    public const string FilterArgument = "filter";
    public const string BrightnessArgument = "brightness";
    public const string ContrastArgument = "contrast";
    public const string SaturationArgument = "saturation";
    public const string HueArgument = "hue";
    public const string TintArgument = "tint";
    public const string TintAmountArgument = "tint_amount";
    public const string BlurArgument = "blur";
    public const string SharpenArgument = "sharpen";
    public const string PadArgument = "pad";
    public const string BackgroundArgument = "background";
    public const string MetadataArgument = "metadata";
    public const string DpiArgument = "dpi";
    public const string ChromaArgument = "chroma";
    public const string ColorsArgument = "colors";
    public const string DitherArgument = "dither";
    public const string InterlaceArgument = "interlace";
    public const string ViewArgument = "view";

    /// <summary>The widest border <c>pad</c> adds.</summary>
    public const int MaxPad = 4096;

    /// <summary>The biggest <c>max_kb</c> (a gigabyte: past it the cap means nothing).</summary>
    public const int MaxMaxKb = 1_000_000;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The picture to edit, relative to the working directory (png, jpg, gif, bmp, tiff, webp, heic, jxl…)." },
            "to": { "type": "string", "description": "Where to write: a file path, or a folder for the default name. Default: beside the source (or the user's image output folder) as name-edited.ext, or name.ext for a format change alone." },
            "overwrite": { "type": "boolean", "description": "Replace a file already at the output path, the source included. Default false." },
            "format": { "type": "string", "enum": ["png", "jpeg", "gif", "bmp", "tiff", "jxl", "heif"], "description": "The output format. Default: to's extension, else the source's format when it can be written, else png. webp and avif cannot be written." },
            "quality": { "type": "integer", "minimum": 1, "maximum": 100, "description": "jpeg, jxl or heif quality. Default: the user's setting (90)." },
            "max_kb": { "type": "integer", "minimum": 1, "description": "Make the file at most this many KB: lowers the quality (lossy formats), then the size, until it fits." },
            "width": { "type": "integer", "minimum": 1, "maximum": 32768, "description": "The result's width in pixels. Alone, the height follows the aspect." },
            "height": { "type": "integer", "minimum": 1, "maximum": 32768, "description": "The result's height in pixels. Alone, the width follows the aspect." },
            "scale": { "type": "number", "minimum": 0.01, "maximum": 16, "description": "Resize by a factor instead of width/height: 0.5 halves, 2 doubles." },
            "fit": { "type": "string", "enum": ["contain", "cover", "pad", "stretch", "shrink"], "description": "With both width and height: contain fits inside (default, may enlarge), cover fills and cuts the overflow at anchor, pad fits inside and fills the rest with background, stretch ignores the aspect, shrink is contain that never enlarges." },
            "anchor": { "type": "string", "enum": ["center", "top", "bottom", "left", "right", "top-left", "top-right", "bottom-left", "bottom-right"], "description": "What cover keeps, or where pad puts the picture. Default center." },
            "interpolation": { "type": "string", "enum": ["lanczos", "spline36", "cubic", "linear", "nearest"], "description": "The resize filter. nearest keeps pixel art sharp. Default: a high-quality one." },
            "crop_x": { "type": "integer", "minimum": 0, "description": "Crop first: the left edge, in the source's pixels as it displays." },
            "crop_y": { "type": "integer", "minimum": 0, "description": "Crop: the top edge." },
            "crop_width": { "type": "integer", "minimum": 1, "description": "Crop: the width." },
            "crop_height": { "type": "integer", "minimum": 1, "description": "Crop: the height." },
            "rotate": { "type": "integer", "enum": [0, 90, 180, 270], "description": "Turn clockwise by quarter turns only (no other angles)." },
            "flip": { "type": "string", "enum": ["none", "horizontal", "vertical"], "description": "Mirror, after the turn." },
            "filter": { "type": "string", "enum": ["none", "grey", "sepia", "negative", "polaroid"], "description": "A colour preset." },
            "brightness": { "type": "integer", "minimum": -100, "maximum": 100, "description": "Brighter (+) or darker (-)." },
            "contrast": { "type": "integer", "minimum": -100, "maximum": 100, "description": "More (+) or less (-) contrast." },
            "saturation": { "type": "integer", "minimum": -100, "maximum": 100, "description": "More vivid (+) or greyer (-); -100 is greyscale." },
            "hue": { "type": "integer", "minimum": -180, "maximum": 180, "description": "Rotate every colour's hue by these degrees: 120 turns red to green." },
            "tint": { "type": "string", "description": "Colour the picture toward this colour (#rrggbb or a name), keeping light and dark." },
            "tint_amount": { "type": "integer", "minimum": 0, "maximum": 100, "description": "How strong the tint is, in percent. Default 50." },
            "blur": { "type": "number", "minimum": 0, "maximum": 100, "description": "Gaussian blur radius in pixels." },
            "sharpen": { "type": "boolean", "description": "true: sharpen firmly; false: no sharpening at all. Default: a light sharpen after a resize." },
            "pad": { "type": "integer", "minimum": 0, "maximum": 4096, "description": "A border of this many pixels on every side, in background." },
            "background": { "type": "string", "description": "The colour for pad, fit pad, and transparency in jpeg/bmp: #rrggbb, #aarrggbb, or a name (white, black, transparent…). Default white." },
            "metadata": { "type": "string", "enum": ["none", "basic", "all"], "description": "What metadata the result keeps: none (default unless the user set otherwise), basic (author, copyright, date taken, camera), all (basic and the GPS location: where it was taken)." },
            "dpi": { "type": "integer", "minimum": 1, "maximum": 10000, "description": "The DPI written into the file, for printing. Default: the source's." },
            "chroma": { "type": "string", "enum": ["auto", "420", "422", "444"], "description": "jpeg only: chroma subsampling. 444 keeps text and sharp colour edges crisp." },
            "colors": { "type": "integer", "minimum": 2, "maximum": 256, "description": "png or gif: a palette of at most this many colours, for a much smaller file." },
            "dither": { "type": "string", "enum": ["auto", "none", "diffuse"], "description": "With a palette (gif, or png with colors): dithering." },
            "interlace": { "type": "boolean", "description": "png only: interlaced, so it shows coarse first while loading." },
            "view": { "type": "boolean", "description": "Also attach the written picture to the next message so you can check it." }
          },
          "required": ["path"]
        }
        """);

    private readonly Func<AppSettingsData> _effective;

    public ImageEditTool(WorkingDirectory files, Func<AppSettingsData> effective) : base(files)
    {
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    public override string Name => ToolName;

    public override string Description => DescriptionText;

    /// <summary>The description. Pinned.</summary>
    public const string DescriptionText =
        "Edits a picture in the working directory and writes the result as a new file: resize (up or down), crop, rotate by quarter turns, flip, " +
        "colour (filter, brightness, contrast, saturation, hue, tint), blur, sharpen, a border, and conversion between png, jpeg, gif, bmp and tiff (jxl and heif where Windows has them; webp and avif read only). " +
        "Every step runs in one pass, in this order: crop, resize, rotate, flip, colour, blur, border; then one encode. " +
        "Metadata is dropped unless asked for, the EXIF orientation is baked in, and enlarging is interpolation, not AI. " +
        "This is the way to resize, convert or touch up pictures: never write a script for it.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The quality <paramref name="effective"/> holds (<c>Image edit quality</c>), clamped. Pure.</summary>
    public static int QualityOf(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.ImageEditQuality, AppSettingsData.MinImageEditQuality, AppSettingsData.MaxImageEditQuality);
    }

    /// <summary>
    /// The call's arguments as a request, or the sentence for the first that does not read. <paramref name="metadataDefault"/> is
    /// <c>Image edit metadata</c>, used when the call gives none. <paramref name="reencodes"/> says whether an output-only argument
    /// (quality, max_kb, a palette, dpi, chroma, interlace, metadata) was given, so a call with nothing else is still something to do. Pure.
    /// </summary>
    public static (ImageEditRequest? Request, bool Reencodes, string? Error) Read(AIFunctionArguments arguments, ImageMetadataPolicy metadataDefault = ImageMetadataPolicy.None)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? error = null;
        int? Int(string name, int min, int max)
        {
            if (error is not null)
            {
                return null;
            }

            if (!ToolArguments.TryReadInt32(arguments, name, out int? value, out string raw))
            {
                error = ClockText.BadInteger(name, raw);
                return null;
            }

            if (value is { } n && (n < min || n > max))
            {
                error = ImageText.OutOfRange(name, n.ToString(CultureInfo.InvariantCulture), min, max);
                return null;
            }

            return value;
        }

        double? Number(string name, double min, double max)
        {
            if (error is not null)
            {
                return null;
            }

            if (!ToolArguments.TryReadDouble(arguments, name, out double? value, out string raw))
            {
                error = GenerateImageTool.BadNumber(name, raw);
                return null;
            }

            if (value is { } n && (double.IsNaN(n) || n < min || n > max))
            {
                error = ImageText.OutOfRange(name, n.ToString(CultureInfo.InvariantCulture), min, max);
                return null;
            }

            return value;
        }

        bool? Flag(string name)
        {
            if (error is not null)
            {
                return null;
            }

            if (!ToolArguments.TryReadBoolean(arguments, name, out bool? value, out string raw))
            {
                error = FileText.BadBoolean(name, raw);
            }

            return value;
        }

        T? Word<T>(string name, TryParser<T> parse, string choices) where T : struct
        {
            string text = ToolArguments.ReadString(arguments, name).Trim();
            if (error is not null || text.Length == 0)
            {
                return null;
            }

            if (parse(text, out T value))
            {
                return value;
            }

            error = FileText.BadChoice(name, text, choices);
            return null;
        }

        Color? Colour(string name)
        {
            string text = ToolArguments.ReadString(arguments, name).Trim();
            if (error is not null || text.Length == 0)
            {
                return null;
            }

            if (ImageColor.TryParseColor(text, out var colour))
            {
                return colour;
            }

            error = ImageText.BadColour(name, text);
            return null;
        }

        ImageFormat? format = null;
        string formatText = ToolArguments.ReadString(arguments, FormatArgument).Trim();
        if (formatText.Length > 0 && (format = ImageFormats.ByName(formatText)) is null)
        {
            return (null, false, FileText.BadChoice(FormatArgument, formatText, ImageFormats.Choices));
        }

        int? quality = Int(QualityArgument, 1, 100);
        int? maxKb = Int(MaxKbArgument, 1, MaxMaxKb);
        int? width = Int(WidthArgument, 1, ImageEditor.MaxSide);
        int? height = Int(HeightArgument, 1, ImageEditor.MaxSide);
        double? scale = Number(ScaleArgument, 0.01, 16);
        var fit = Word<ImageFit>(FitArgument, ImageWords.TryParseFit, ImageWords.FitChoices);
        var anchor = Word<ImageAnchor>(AnchorArgument, ImageWords.TryParseAnchor, ImageWords.AnchorChoices);
        string? interpolation = null;
        string interpolationText = ToolArguments.ReadString(arguments, InterpolationArgument).Trim();
        if (error is null && interpolationText.Length > 0 && !ImageWords.TryParseInterpolation(interpolationText, out interpolation))
        {
            error = FileText.BadChoice(InterpolationArgument, interpolationText, ImageWords.InterpolationChoices);
        }

        int? cropX = Int(CropXArgument, 0, int.MaxValue / 2);
        int? cropY = Int(CropYArgument, 0, int.MaxValue / 2);
        int? cropWidth = Int(CropWidthArgument, 1, int.MaxValue / 2);
        int? cropHeight = Int(CropHeightArgument, 1, int.MaxValue / 2);
        int rotate = 0;
        if (error is null)
        {
            if (!ToolArguments.TryReadInt32(arguments, RotateArgument, out int? turn, out string rawTurn))
            {
                error = ImageText.Rotation(rawTurn);
            }
            else if (turn is { } degrees)
            {
                // -90 is 270, 360 is 0; anything not a quarter turn is refused, never rounded.
                if (degrees % 90 != 0)
                {
                    error = ImageText.Rotation(degrees.ToString(CultureInfo.InvariantCulture));
                }

                rotate = ((degrees % 360) + 360) % 360;
            }
        }

        var flip = Word<ImageFlip>(FlipArgument, ImageWords.TryParseFlip, ImageWords.FlipChoices);
        var filter = Word<ImageFilter>(FilterArgument, ImageColor.TryParseFilter, ImageColor.FilterChoices);
        int? brightness = Int(BrightnessArgument, -100, 100);
        int? contrast = Int(ContrastArgument, -100, 100);
        int? saturation = Int(SaturationArgument, -100, 100);
        int? hue = Int(HueArgument, -360, 360);
        var tint = Colour(TintArgument);
        int? tintAmount = Int(TintAmountArgument, 0, 100);
        double? blur = Number(BlurArgument, 0, 100);
        bool? sharpen = Flag(SharpenArgument);
        int? pad = Int(PadArgument, 0, MaxPad);
        var background = Colour(BackgroundArgument);
        var metadata = Word<ImageMetadataPolicy>(MetadataArgument, ImageWords.TryParseMetadata, ImageWords.MetadataChoices);
        int? dpi = Int(DpiArgument, 1, 10_000);
        var chroma = Word<ImageChroma>(ChromaArgument, ImageWords.TryParseChroma, ImageWords.ChromaChoices);
        int? colors = Int(ColorsArgument, 2, 256);
        var dither = Word<DitherMode>(DitherArgument, ImageWords.TryParseDither, ImageWords.DitherChoices);
        bool? interlace = Flag(InterlaceArgument);
        if (error is not null)
        {
            return (null, false, error);
        }

        if (scale is not null && (width is not null || height is not null))
        {
            return (null, false, ImageText.ScaleAndSize);
        }

        Rectangle? crop = null;
        int cropParts = (cropX is null ? 0 : 1) + (cropY is null ? 0 : 1) + (cropWidth is null ? 0 : 1) + (cropHeight is null ? 0 : 1);
        if (cropParts is > 0 and < 4)
        {
            return (null, false, ImageText.CropIncomplete);
        }

        if (cropParts == 4)
        {
            crop = new Rectangle(cropX!.Value, cropY!.Value, cropWidth!.Value, cropHeight!.Value);
        }

        var request = new ImageEditRequest
        {
            Width = width,
            Height = height,
            Scale = scale,
            Fit = fit ?? ImageFit.Contain,
            Anchor = anchor ?? ImageAnchor.Center,
            Interpolation = interpolation,
            Crop = crop,
            Rotate = rotate,
            Flip = flip ?? ImageFlip.None,
            Filter = filter ?? ImageFilter.None,
            Brightness = brightness ?? 0,
            Contrast = contrast ?? 0,
            Saturation = saturation ?? 0,
            Hue = hue ?? 0,
            Tint = tint,
            TintAmount = tintAmount ?? ImageEditor.DefaultTintAmount,
            Blur = blur ?? 0,
            Sharpen = sharpen,
            Pad = pad ?? 0,
            Background = background,
            Format = format,
            Quality = quality,
            MaxKb = maxKb,
            Metadata = metadata ?? metadataDefault,
            Dpi = dpi,
            Chroma = chroma ?? ImageChroma.Auto,
            Colors = colors,
            Dither = dither ?? DitherMode.Auto,
            Interlace = interlace ?? false,
        };
        bool reencodes = quality is not null || maxKb is not null || colors is not null || dpi is not null || chroma is not null || interlace == true || metadata is not null;
        return (request, reencodes, null);
    }

    private delegate bool TryParser<T>(string text, out T value);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var effective = _effective();
        var (request, reencodes, error) = Read(arguments, ImageWords.MetadataOf(effective.ImageEditMetadata));
        if (request is null)
        {
            return error;
        }

        if (!RequirePath(arguments, PathArgument, out string path, out string pathError))
        {
            return pathError;
        }

        if (ReadOverwrite(arguments, out string raw) is not { } overwrite)
        {
            return FileText.BadBoolean(OverwriteArgument, raw);
        }

        if (ReadFlag(arguments, ViewArgument, out raw) is not { } view)
        {
            return FileText.BadBoolean(ViewArgument, raw);
        }

        string to = ToolArguments.ReadString(arguments, ToArgument).Trim();
        return await Task.Run(() => Edit(path, to, overwrite, view, request, reencodes, effective, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    // The work, off the turn's thread: read, plan the output, edit, write, and the picture back when asked.
    private object? Edit(string path, string to, bool overwrite, bool view, ImageEditRequest request, bool reencodes, AppSettingsData effective, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = Files.ReadBytes(path, ImageEditor.MaxSourceBytes);
        if (read.Outcome != FileOutcome.Ok || read.Bytes is null)
        {
            return read.Outcome == FileOutcome.TooBig ? ImageText.SourceTooBig : FileText.Error(read.Outcome, read.Relative, "read", read.Detail);
        }

        var info = ImageEditor.Info(read.Bytes);
        if (info is null)
        {
            return FileText.NotAnImage(read.Relative);
        }

        bool toIsFolder = to.Length > 0 && Files.IsExistingDirectory(to);
        var format = ImageOutput.FormatFor(request.Format, to, toIsFolder, info.MimeType, ImageFormats.CanWrite);
        bool sameFormat = ImageFormats.ByMimeType(info.MimeType) == format;
        if (!request.ChangesPixels && sameFormat && !reencodes && to.Length == 0)
        {
            return ImageText.NothingToDo;
        }

        var (target, nameError) = ImageOutput.OutputFor(to, toIsFolder, read.Relative, format, onlyFormatChanged: !request.ChangesPixels && !sameFormat, effective.ImageEditOutputFolder);
        if (target is null)
        {
            return nameError;
        }

        bool named = to.Length > 0 && !toIsFolder;
        if (!overwrite && ImageOutput.SamePath(target, read.Relative))
        {
            return ImageText.OverwriteSource;
        }

        var (result, editError) = ImageEditor.Apply(read.Bytes, request, format, QualityOf(effective), cancellationToken);
        if (result is null)
        {
            return editError;
        }

        cancellationToken.ThrowIfCancellationRequested();
        WriteResult written;
        if (named || overwrite)
        {
            written = Files.WriteBytes(target, result.Bytes, overwrite);
        }
        else
        {
            // A made-up name never replaces anything: the next free -2, -3… instead (the camera's loop).
            int n = 1;
            do
            {
                written = Files.WriteBytes(ImageOutput.Numbered(target, n), result.Bytes, overwrite: false);
            }
            while (written.Outcome == FileOutcome.Exists && ++n < 100);
        }

        if (written.Outcome != FileOutcome.Ok)
        {
            return FileText.Error(written.Outcome, written.Relative, "write", written.Detail);
        }

        string text = ImageText.Written(written.Relative, result, request.MaxKb);
        if (!view)
        {
            return text;
        }

        var shown = Files.ReadImage(written.Relative);
        return shown.Outcome == FileOutcome.Ok && shown.Image is { } image
            ? new ToolImageResult(text + "\n" + FileText.Image(shown), [image])
            : text + "\n" + FileText.Image(shown);
    }
}
