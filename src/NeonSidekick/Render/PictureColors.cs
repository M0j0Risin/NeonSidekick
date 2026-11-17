using System.Globalization;

namespace NeonSidekick.Render;

/// <summary>
/// The app's own pictures' colors, light and dark (2026-11-17, the admin-themes-render review's #41: the chart and the collage each kept a
/// copy): the ground, the ink, the quiet text, a chart's grid lines and a collage's empty tile, as <c>#RRGGBB</c>. <see cref="GroundColor"/>
/// is the ground parsed once, what a draw's job carries. Pure.
/// </summary>
public sealed record PictureColors(string Ground, string Ink, string Quiet, string Grid, string Tile)
{
    public static readonly PictureColors Light = new("#FFFFFF", "#1F2328", "#57606A", "#E5E7EB", "#F3F4F6");

    public static readonly PictureColors Dark = new("#17151F", "#ECEAF2", "#A8A3B8", "#34303F", "#221F2C");

    /// <summary>The colors for a dark ground, or a light one.</summary>
    public static PictureColors For(bool dark) => dark ? Dark : Light;

    /// <summary><see cref="Ground"/> as a color, opaque.</summary>
    public System.Drawing.Color GroundColor { get; } = Parse(Ground);

    private static System.Drawing.Color Parse(string hex) => System.Drawing.Color.FromArgb(
        255,
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
