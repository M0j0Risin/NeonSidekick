using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Viewer;

/// <summary>
/// The picture viewer's colours from the theme in force (later on 2026-09-27, the user's call: the window in the app's look,
/// not the stock light bar), as Win32 COLORREFs (<c>0x00BBGGRR</c>). The title bar is Windows' own, recoloured through DWM
/// — not a caption drawn by hand, which would mean redoing the buttons, snap layouts, dragging and resizing (the user's
/// call): Windows 11 takes <see cref="Caption"/>, <see cref="CaptionText"/> and <see cref="Border"/>, Windows 10 only the
/// dark bar, anything older nothing. <see cref="Background"/> fills round the picture and the empty window,
/// <see cref="Text"/> is the line the window shows with no picture. Pure; <see cref="PictureWindow"/> applies it.
/// </summary>
public readonly record struct ViewerStyle(uint Caption, uint CaptionText, uint Border, uint Background, uint Text)
{
    /// <summary>
    /// The look with <c>Themed external windows</c> off (later on 2026-09-27, the user's ask): a black bar with white text, black
    /// round the picture and the grey line the window had before the theme reached it.
    /// </summary>
    public static readonly ViewerStyle Black = new(Caption: 0, CaptionText: 0x00FFFFFF, Border: 0, Background: 0, Text: 0x00A0A0A0);

    /// <summary>The style for <paramref name="palette"/>: its background for the bar and the fill, its body text on the bar, its main accent for the edge, its dim text in the window; <see cref="Black"/> when not <paramref name="themed"/>.</summary>
    public static ViewerStyle For(ThemePalette palette, bool themed = true)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (!themed)
        {
            return Black;
        }

        return new ViewerStyle(
            Caption: ColorRef(palette.Bg),
            CaptionText: ColorRef(palette.Ink),
            Border: ColorRef(palette.Primary),
            Background: ColorRef(palette.Bg),
            Text: ColorRef(palette.Dim));
    }

    /// <summary>A colour as a COLORREF: red in the low byte.</summary>
    public static uint ColorRef(Color color) => color.R | ((uint)color.G << 8) | ((uint)color.B << 16);
}
