using System.Drawing;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Images;

/// <summary>How a new size is reached when both sides are given.</summary>
public enum ImageFit
{
    /// <summary>Keep the aspect, fit inside the box; may enlarge.</summary>
    Contain,

    /// <summary>Keep the aspect, fill the box, cut the overflow at the anchor.</summary>
    Cover,

    /// <summary>Keep the aspect, fit inside the box, fill the rest with the background at the anchor.</summary>
    Pad,

    /// <summary>Exactly the box, the aspect not kept.</summary>
    Stretch,

    /// <summary>As contain, but never enlarge.</summary>
    Shrink,
}

/// <summary>Which part of the picture a cover keeps, or where a pad puts it.</summary>
public enum ImageAnchor
{
    Center,
    Top,
    Bottom,
    Left,
    Right,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>A mirror after the rotation.</summary>
public enum ImageFlip
{
    None,
    Horizontal,
    Vertical,
}

/// <summary>Which metadata a written picture keeps (2026-10-04): none by default, so a shared picture carries no camera, date or place.</summary>
public enum ImageMetadataPolicy
{
    None,

    /// <summary>Author, copyright, title, comment, date taken, the camera and the exposure.</summary>
    Basic,

    /// <summary>Basic and the GPS position.</summary>
    All,
}

/// <summary>
/// Where an edited picture goes when nothing names a place (later on 2026-10-04, <c>Image edit mode</c>): beside the source as a new
/// file, or over the source.
/// </summary>
public enum ImageEditMode
{
    BesideOriginal,

    /// <summary>The result replaces the source; a format change writes the new name and deletes the source.</summary>
    OverwriteOriginal,
}

/// <summary>Chroma subsampling for a JPEG.</summary>
public enum ImageChroma
{
    Auto,
    Subsample420,
    Subsample422,
    Subsample444,
}

/// <summary>
/// One <c>image_edit</c> call, parsed (2026-10-04): every step the picture goes through in one decode and one encode. Null and
/// zero mean "not asked for"; <see cref="ImageEditor.Apply"/> checks what does not fit together.
/// </summary>
public sealed record ImageEditRequest
{
    // ---- size ----
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Scale { get; init; }
    public ImageFit Fit { get; init; } = ImageFit.Contain;
    public ImageAnchor Anchor { get; init; } = ImageAnchor.Center;
    public string? Interpolation { get; init; }

    // ---- geometry ----
    public Rectangle? Crop { get; init; }

    /// <summary>Clockwise degrees: 0, 90, 180 or 270.</summary>
    public int Rotate { get; init; }
    public ImageFlip Flip { get; init; }

    // ---- colour ----
    public ImageFilter Filter { get; init; }
    public int Brightness { get; init; }
    public int Contrast { get; init; }
    public int Saturation { get; init; }
    public int Hue { get; init; }
    public Color? Tint { get; init; }
    public int TintAmount { get; init; } = ImageEditor.DefaultTintAmount;
    public double Blur { get; init; }

    /// <summary>True: a firm unsharp mask; false: none, not even the scaler's own after a resize; null: the scaler's own.</summary>
    public bool? Sharpen { get; init; }

    // ---- border ----
    public int Pad { get; init; }
    public Color? Background { get; init; }

    // ---- output ----
    public ImageFormat? Format { get; init; }
    public int? Quality { get; init; }
    public int? MaxKb { get; init; }
    public ImageMetadataPolicy Metadata { get; init; }
    public int? Dpi { get; init; }
    public ImageChroma Chroma { get; init; }
    public int? Colors { get; init; }
    public DitherMode Dither { get; init; } = DitherMode.Auto;
    public bool Interlace { get; init; }

    /// <summary>
    /// The call asked for nothing but <c>metadata: none</c> (2026-10-05): <c>image_edit</c> then strips a JPEG, PNG, WebP or GIF
    /// losslessly (<see cref="MetadataStripper"/>) instead of re-encoding it. Given, never inferred from the setting's default.
    /// </summary>
    public bool StripOnly { get; init; }

    /// <summary>Whether anything at all is asked of the pixels (a size, a crop, a turn, a colour step, a border); a bare format change is not.</summary>
    public bool ChangesPixels =>
        Width is not null || Height is not null || Scale is not null || Crop is not null || Rotate != 0 || Flip != ImageFlip.None
        || Filter != ImageFilter.None || Brightness != 0 || Contrast != 0 || Saturation != 0 || Hue % 360 != 0
        || (Tint is not null && TintAmount != 0) || Blur > 0 || Sharpen == true || Pad > 0;
}

/// <summary>The words <c>image_edit</c>'s choice arguments take, and their parsers. Pure; the choice lists pinned.</summary>
public static class ImageWords
{
    public const string FitChoices = "contain, cover, pad, stretch, shrink";
    public const string AnchorChoices = "center, top, bottom, left, right, top-left, top-right, bottom-left, bottom-right";
    public const string FlipChoices = "none, horizontal, vertical";
    public const string MetadataChoices = "none, basic, all";
    public const string ChromaChoices = "auto, 420, 422, 444";
    public const string DitherChoices = "auto, none, diffuse";
    public const string InterpolationChoices = "lanczos, spline36, cubic, linear, nearest";

    /// <summary>The metadata policies by name, in the picker's order. Pinned.</summary>
    public static readonly string[] MetadataNames = ["none", "basic", "all"];

    /// <summary>The edit modes by name, in the picker's order (later on 2026-10-04, <c>Image edit mode</c>). Pinned.</summary>
    public static readonly string[] EditModeNames = ["beside-original", "overwrite-original"];

    public static bool TryParseFit(string text, out ImageFit fit) => TryParse(text, out fit, ("contain", ImageFit.Contain), ("cover", ImageFit.Cover), ("fill", ImageFit.Cover), ("crop", ImageFit.Cover), ("pad", ImageFit.Pad), ("stretch", ImageFit.Stretch), ("shrink", ImageFit.Shrink), ("max", ImageFit.Shrink));

    public static bool TryParseAnchor(string text, out ImageAnchor anchor) => TryParse(text.Replace('_', '-').Replace(' ', '-'), out anchor,
        ("center", ImageAnchor.Center), ("centre", ImageAnchor.Center), ("middle", ImageAnchor.Center), ("top", ImageAnchor.Top), ("bottom", ImageAnchor.Bottom), ("left", ImageAnchor.Left), ("right", ImageAnchor.Right),
        ("top-left", ImageAnchor.TopLeft), ("top-right", ImageAnchor.TopRight), ("bottom-left", ImageAnchor.BottomLeft), ("bottom-right", ImageAnchor.BottomRight));

    public static bool TryParseFlip(string text, out ImageFlip flip) => TryParse(text, out flip, ("none", ImageFlip.None), ("horizontal", ImageFlip.Horizontal), ("vertical", ImageFlip.Vertical));

    public static bool TryParseMetadata(string text, out ImageMetadataPolicy policy) => TryParse(text, out policy, ("none", ImageMetadataPolicy.None), ("basic", ImageMetadataPolicy.Basic), ("all", ImageMetadataPolicy.All));

    public static bool TryParseChroma(string text, out ImageChroma chroma) => TryParse(text.Replace(":", ""), out chroma, ("auto", ImageChroma.Auto), ("420", ImageChroma.Subsample420), ("422", ImageChroma.Subsample422), ("444", ImageChroma.Subsample444));

    public static bool TryParseDither(string text, out DitherMode dither) => TryParse(text, out dither, ("auto", DitherMode.Auto), ("none", DitherMode.None), ("diffuse", DitherMode.ErrorDiffusion));

    /// <summary>An interpolation word, as its canonical name (<c>nearest-neighbor</c> → <c>nearest</c>); false for anything else.</summary>
    public static bool TryParseInterpolation(string text, out string name) => TryParse(text, out name,
        ("lanczos", "lanczos"), ("spline36", "spline36"), ("cubic", "cubic"), ("bicubic", "cubic"), ("linear", "linear"), ("bilinear", "linear"), ("nearest", "nearest"), ("nearest-neighbor", "nearest"), ("nearestneighbor", "nearest"), ("point", "nearest"));

    /// <summary>A metadata setting as the policy it names; an unknown or empty one is none (the safe reading).</summary>
    public static ImageMetadataPolicy MetadataOf(string? setting) =>
        TryParseMetadata(setting ?? "", out var policy) ? policy : ImageMetadataPolicy.None;

    /// <summary>A metadata setting as its name (<see cref="MetadataNames"/>); an unknown one reads as <c>none</c>.</summary>
    public static string MetadataName(string? setting) => MetadataNames[(int)MetadataOf(setting)];

    /// <summary>An edit-mode setting as the mode it names; an unknown or empty one is beside-original (the safe reading). Case and <c>_</c> for <c>-</c> do not matter.</summary>
    public static ImageEditMode EditModeOf(string? setting) =>
        string.Equals((setting ?? "").Trim().Replace('_', '-'), EditModeNames[1], StringComparison.OrdinalIgnoreCase) ? ImageEditMode.OverwriteOriginal : ImageEditMode.BesideOriginal;

    /// <summary>An edit-mode setting as its name (<see cref="EditModeNames"/>); an unknown one reads as <c>beside-original</c>.</summary>
    public static string EditModeName(string? setting) => EditModeNames[(int)EditModeOf(setting)];

    /// <summary>Where an edit mode puts the result, for the picker's dim note. Pinned.</summary>
    public static string DescribeEditMode(string name) => EditModeOf(name) switch
    {
        ImageEditMode.OverwriteOriginal => "the result replaces the picture; a format change deletes the old file",
        _ => "a new file beside the picture (photo-edited.png)",
    };

    /// <summary>What a metadata policy keeps, for the picker's dim note. Pinned.</summary>
    public static string DescribeMetadata(string name) => MetadataOf(name) switch
    {
        ImageMetadataPolicy.Basic => "author, copyright, title, comment, date taken, camera and exposure",
        ImageMetadataPolicy.All => "basic and the GPS position: the picture says where it was taken",
        _ => "nothing: no camera, date or place in the written picture",
    };

    private static bool TryParse<T>(string text, out T value, params (string Word, T Value)[] words)
    {
        string key = text.Trim().ToLowerInvariant();
        foreach (var (word, v) in words)
        {
            if (key == word)
            {
                value = v;
                return true;
            }
        }

        value = default!;
        return false;
    }
}
