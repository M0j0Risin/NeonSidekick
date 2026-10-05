using System.Drawing;
using System.Globalization;
using System.Numerics;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Images;

/// <summary>The colour presets <c>image_edit</c>'s <c>filter</c> takes, MagicScaler's own matrices.</summary>
public enum ImageFilter
{
    None,
    Grey,
    Sepia,
    Negative,
    Polaroid,
}

/// <summary>
/// The colour side of <c>image_edit</c> (2026-10-04): one <see cref="Matrix4x4"/> for MagicScaler's <c>ColorMatrixTransform</c>, and the
/// colours <c>background</c> and <c>tint</c> take. The transform reads the matrix as 3×4 over RGB — a pixel is a row vector, so
/// <c>R' = R·M11 + G·M21 + B·M31 + M41</c>, the fourth row the offset, channels 0 to 1 — and never touches alpha (so no opacity
/// here). Matrices compose by multiplication in the order they apply: the preset, then brightness, contrast, saturation, hue and
/// tint. The identity means no transform is added at all. Pure.
/// </summary>
public static class ImageColor
{
    /// <summary>The Rec. 601 luma weights, the ones <c>ColorMatrix.Grey</c> uses: saturation and tint keep a pixel's luma by them.</summary>
    public const float LumaR = 0.299f, LumaG = 0.587f, LumaB = 0.114f;

    /// <summary>The choices <c>filter</c> takes. Pinned.</summary>
    public const string FilterChoices = "none, grey, sepia, negative, polaroid";

    /// <summary>A <c>filter</c> word (<c>gray</c>, <c>greyscale</c>, <c>grayscale</c> and <c>invert</c> too); false for anything else. Pure.</summary>
    public static bool TryParseFilter(string text, out ImageFilter filter)
    {
        ArgumentNullException.ThrowIfNull(text);
        filter = text.Trim().ToLowerInvariant() switch
        {
            "" or "none" => ImageFilter.None,
            "grey" or "gray" or "greyscale" or "grayscale" => ImageFilter.Grey,
            "sepia" => ImageFilter.Sepia,
            "negative" or "invert" => ImageFilter.Negative,
            "polaroid" => ImageFilter.Polaroid,
            _ => (ImageFilter)(-1),
        };
        return Enum.IsDefined(filter);
    }

    /// <summary>
    /// The one matrix for a request's colour steps, <see cref="Matrix4x4.Identity"/> when there are none. <paramref name="brightness"/>,
    /// <paramref name="contrast"/> and <paramref name="saturation"/> run −100 to 100 (0 none); <paramref name="hue"/> is degrees;
    /// <paramref name="tint"/> pulls every pixel toward that colour at its own luma by <paramref name="tintAmount"/> percent.
    /// </summary>
    public static Matrix4x4 Matrix(ImageFilter filter, int brightness, int contrast, int saturation, int hue, Color? tint, int tintAmount)
    {
        var m = Preset(filter);
        if (brightness != 0)
        {
            m *= Brightness(brightness);
        }

        if (contrast != 0)
        {
            m *= Contrast(contrast);
        }

        if (saturation != 0)
        {
            m *= Saturation(saturation);
        }

        if (hue % 360 != 0)
        {
            m *= Hue(hue);
        }

        if (tint is { } colour && tintAmount != 0)
        {
            m *= Tint(colour, tintAmount);
        }

        return m;
    }

    /// <summary>A preset's matrix: MagicScaler's own, the identity for none.</summary>
    public static Matrix4x4 Preset(ImageFilter filter) => filter switch
    {
        ImageFilter.Grey => ColorMatrix.Grey,
        ImageFilter.Sepia => ColorMatrix.Sepia,
        ImageFilter.Negative => ColorMatrix.Negative,
        ImageFilter.Polaroid => ColorMatrix.Polaroid,
        _ => Matrix4x4.Identity,
    };

    /// <summary>Brightness as an offset on every channel: 100 adds half the range, −100 takes half away.</summary>
    public static Matrix4x4 Brightness(int amount)
    {
        float offset = Math.Clamp(amount, -100, 100) / 200f;
        var m = Matrix4x4.Identity;
        m.M41 = offset;
        m.M42 = offset;
        m.M43 = offset;
        return m;
    }

    /// <summary>Contrast about mid-grey: a factor of 1 + amount/100 (−100 flat grey, 100 twice as steep).</summary>
    public static Matrix4x4 Contrast(int amount)
    {
        float factor = 1 + (Math.Clamp(amount, -100, 100) / 100f);
        float offset = 0.5f * (1 - factor);
        var m = Matrix4x4.CreateScale(factor, factor, factor);
        m.M41 = offset;
        m.M42 = offset;
        m.M43 = offset;
        return m;
    }

    /// <summary>Saturation by the luma weights: a factor of 1 + amount/100 (−100 grey, 100 twice as vivid), luma kept.</summary>
    public static Matrix4x4 Saturation(int amount)
    {
        float k = 1 + (Math.Clamp(amount, -100, 100) / 100f);
        float r = (1 - k) * LumaR, g = (1 - k) * LumaG, b = (1 - k) * LumaB;
        return new Matrix4x4(
            r + k, r, r, 0,
            g, g + k, g, 0,
            b, b, b + k, 0,
            0, 0, 0, 1);
    }

    /// <summary>
    /// A hue rotation by <paramref name="degrees"/>, the SVG <c>feColorMatrix hueRotate</c> matrix (its luminance-keeping
    /// coefficients), transposed to the row-vector form. 120 turns red toward green.
    /// </summary>
    public static Matrix4x4 Hue(int degrees)
    {
        double radians = degrees % 360 * Math.PI / 180;
        float c = (float)Math.Cos(radians), s = (float)Math.Sin(radians);
        // The column-vector coefficients: R' = a00·R + a01·G + a02·B, and so on.
        float a00 = 0.213f + (c * 0.787f) - (s * 0.213f);
        float a01 = 0.715f - (c * 0.715f) - (s * 0.715f);
        float a02 = 0.072f - (c * 0.072f) + (s * 0.928f);
        float a10 = 0.213f - (c * 0.213f) + (s * 0.143f);
        float a11 = 0.715f + (c * 0.285f) + (s * 0.140f);
        float a12 = 0.072f - (c * 0.072f) - (s * 0.283f);
        float a20 = 0.213f - (c * 0.213f) - (s * 0.787f);
        float a21 = 0.715f - (c * 0.715f) + (s * 0.715f);
        float a22 = 0.072f + (c * 0.928f) + (s * 0.072f);
        return new Matrix4x4(
            a00, a10, a20, 0,
            a01, a11, a21, 0,
            a02, a12, a22, 0,
            0, 0, 0, 1);
    }

    /// <summary>A tint: the identity blended toward "this colour at the pixel's luma" by <paramref name="amount"/> percent (0 to 100).</summary>
    public static Matrix4x4 Tint(Color colour, int amount)
    {
        float t = Math.Clamp(amount, 0, 100) / 100f;
        float tr = colour.R / 255f, tg = colour.G / 255f, tb = colour.B / 255f;
        // Row i is what input channel i adds to each output: its luma weight times the tint colour.
        var toward = new Matrix4x4(
            LumaR * tr, LumaR * tg, LumaR * tb, 0,
            LumaG * tr, LumaG * tg, LumaG * tb, 0,
            LumaB * tr, LumaB * tg, LumaB * tb, 0,
            0, 0, 0, 1);
        return Matrix4x4.Lerp(Matrix4x4.Identity, toward, t);
    }

    /// <summary>The names <see cref="TryParseColor"/> knows besides hex. Pinned.</summary>
    public static readonly IReadOnlyDictionary<string, Color> Names = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = Color.FromArgb(255, 255, 255, 255),
        ["black"] = Color.FromArgb(255, 0, 0, 0),
        ["transparent"] = Color.FromArgb(0, 0, 0, 0),
        ["grey"] = Color.FromArgb(255, 128, 128, 128),
        ["gray"] = Color.FromArgb(255, 128, 128, 128),
        ["red"] = Color.FromArgb(255, 255, 0, 0),
        ["green"] = Color.FromArgb(255, 0, 128, 0),
        ["blue"] = Color.FromArgb(255, 0, 0, 255),
        ["yellow"] = Color.FromArgb(255, 255, 255, 0),
        ["orange"] = Color.FromArgb(255, 255, 165, 0),
        ["purple"] = Color.FromArgb(255, 128, 0, 128),
        ["pink"] = Color.FromArgb(255, 255, 192, 203),
        ["brown"] = Color.FromArgb(255, 165, 42, 42),
    };

    /// <summary>
    /// A colour as the model writes one: <c>#rgb</c>, <c>#rrggbb</c>, <c>#aarrggbb</c> (the <c>#</c> optional) or a name of
    /// <see cref="Names"/>. Built with <see cref="Color.FromArgb(int, int, int, int)"/>, never a known colour, so equality is by value. Pure.
    /// </summary>
    public static bool TryParseColor(string text, out Color colour)
    {
        ArgumentNullException.ThrowIfNull(text);
        colour = default;
        string value = text.Trim();
        if (Names.TryGetValue(value, out colour))
        {
            return true;
        }

        string hex = value.StartsWith('#') ? value[1..] : value;
        if (hex.Length is not (3 or 6 or 8) || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint n))
        {
            return false;
        }

        colour = hex.Length switch
        {
            3 => Color.FromArgb(255, (int)((n >> 8) & 0xF) * 17, (int)((n >> 4) & 0xF) * 17, (int)(n & 0xF) * 17),
            6 => Color.FromArgb(255, (int)((n >> 16) & 0xFF), (int)((n >> 8) & 0xFF), (int)(n & 0xFF)),
            _ => Color.FromArgb((int)(n >> 24), (int)((n >> 16) & 0xFF), (int)((n >> 8) & 0xFF), (int)(n & 0xFF)),
        };
        return true;
    }
}
