using NeonSidekick.UI;

namespace NeonSidekick.Viewer;

/// <summary>
/// The picture menu's colours (2026-10-04, the user's pick: the menu in the theme, not Windows' own) as Win32 COLORREFs, from the
/// theme in force: the panel fill, body text, dim text for a row that cannot be chosen, the main accent under the highlighted row
/// with the background's colour on it, the accent for the edge and the dimmer text for a separator. With <c>Themed external
/// windows</c> off, <see cref="Black"/>, the log window's black look's twin. Pure; <see cref="ContextMenuWindow"/> applies it.
/// </summary>
public readonly record struct MenuStyle(uint Background, uint Text, uint Dim, uint HotBack, uint HotText, uint Border, uint Separator)
{
    /// <summary>The look with <c>Themed external windows</c> off: grey on near-black, a blue highlight.</summary>
    public static readonly MenuStyle Black = new(
        Background: 0x00202020,
        Text: 0x00E0E0E0,
        Dim: 0x00808080,
        HotBack: 0x00C07830,
        HotText: 0x00FFFFFF,
        Border: 0x00606060,
        Separator: 0x00404040);

    /// <summary>The style for <paramref name="palette"/>; <see cref="Black"/> when not <paramref name="themed"/>.</summary>
    public static MenuStyle For(ThemePalette palette, bool themed = true)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (!themed)
        {
            return Black;
        }

        return new MenuStyle(
            Background: ViewerStyle.ColorRef(palette.PanelBg),
            Text: ViewerStyle.ColorRef(palette.Ink),
            Dim: ViewerStyle.ColorRef(palette.Dim),
            HotBack: ViewerStyle.ColorRef(palette.Primary),
            HotText: ViewerStyle.ColorRef(palette.Bg),
            Border: ViewerStyle.ColorRef(palette.Primary),
            Separator: ViewerStyle.ColorRef(palette.Dimmer));
    }
}

/// <summary>
/// The thumbnail browser's colours (2026-10-04) as Win32 COLORREFs, from the theme in force: the page, the tiles' captions in dim
/// text, the panel fill behind a tile still being read, the main accent round the selected tile and under its caption with the
/// background's colour on it, and the log window's scroll bar. The title bar is <see cref="ViewerStyle"/>'s, so the app's windows
/// share one look; with <c>Themed external windows</c> off, <see cref="Black"/>. Pure; <see cref="ThumbsWindow"/> applies it.
/// </summary>
public readonly record struct ThumbsStyle(uint Background, uint Text, uint Caption, uint Placeholder, uint Selected, uint SelectedText, uint Track, uint Thumb, uint ThumbActive)
{
    /// <summary>The look with <c>Themed external windows</c> off: grey on black, a blue selection.</summary>
    public static readonly ThumbsStyle Black = new(
        Background: 0,
        Text: 0x00A0A0A0,
        Caption: 0x00A0A0A0,
        Placeholder: 0x00202020,
        Selected: 0x00C07830,
        SelectedText: 0x00FFFFFF,
        Track: 0x00202020,
        Thumb: 0x00606060,
        ThumbActive: 0x00909090);

    /// <summary>The style for <paramref name="palette"/>; <see cref="Black"/> when not <paramref name="themed"/>.</summary>
    public static ThumbsStyle For(ThemePalette palette, bool themed = true)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (!themed)
        {
            return Black;
        }

        return new ThumbsStyle(
            Background: ViewerStyle.ColorRef(palette.Bg),
            Text: ViewerStyle.ColorRef(palette.Dim),
            Caption: ViewerStyle.ColorRef(palette.Dim),
            Placeholder: ViewerStyle.ColorRef(palette.PanelBg),
            Selected: ViewerStyle.ColorRef(palette.Primary),
            SelectedText: ViewerStyle.ColorRef(palette.Bg),
            Track: ViewerStyle.ColorRef(palette.PanelBg),
            Thumb: ViewerStyle.ColorRef(palette.Dimmer),
            ThumbActive: ViewerStyle.ColorRef(palette.Dim));
    }
}
