using NeonSidekick.Diagnostics;
using NeonSidekick.UI;

namespace NeonSidekick.Viewer;

/// <summary>
/// The log window's colours (2026-10-02, the user's call: lines coloured by level) as Win32 COLORREFs, from the theme in
/// force: the page background, body text for Info, the dim text for Trace and Debug, the theme's warning and error
/// colours, the main accent behind a selection with the background's colour on it, and the lifted fill for the scroll
/// bar's track under a dim thumb. The title bar is <see cref="ViewerStyle"/>'s, so the app's windows share one look; with
/// <c>Themed image viewer</c> off, <see cref="Black"/>, the viewer's black look's twin. Pure; <c>LogWindow</c> applies it.
/// </summary>
public readonly record struct LogViewStyle(
    uint Background,
    uint Text,
    uint Dim,
    uint Warning,
    uint Error,
    uint SelectionBack,
    uint SelectionText,
    uint Track,
    uint Thumb,
    uint ThumbActive)
{
    /// <summary>The look with <c>Themed image viewer</c> off: grey on black, yellow warnings, red errors, a blue selection.</summary>
    public static readonly LogViewStyle Black = new(
        Background: 0,
        Text: 0x00D0D0D0,
        Dim: 0x00808080,
        Warning: 0x0000D7FF,
        Error: 0x005050FF,
        SelectionBack: 0x00C07830,
        SelectionText: 0x00FFFFFF,
        Track: 0x00202020,
        Thumb: 0x00606060,
        ThumbActive: 0x00909090);

    /// <summary>The style for <paramref name="palette"/>; <see cref="Black"/> when not <paramref name="themed"/>.</summary>
    public static LogViewStyle For(ThemePalette palette, bool themed = true)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (!themed)
        {
            return Black;
        }

        return new LogViewStyle(
            Background: ViewerStyle.ColorRef(palette.Bg),
            Text: ViewerStyle.ColorRef(palette.Ink),
            Dim: ViewerStyle.ColorRef(palette.Dim),
            Warning: ViewerStyle.ColorRef(palette.Warn),
            Error: ViewerStyle.ColorRef(palette.Bad),
            SelectionBack: ViewerStyle.ColorRef(palette.Primary),
            SelectionText: ViewerStyle.ColorRef(palette.Bg),
            Track: ViewerStyle.ColorRef(palette.PanelBg),
            Thumb: ViewerStyle.ColorRef(palette.Dimmer),
            ThumbActive: ViewerStyle.ColorRef(palette.Dim));
    }

    /// <summary>A line's colour by its level: Trace and Debug dim, Info body text, Warning and Error their own.</summary>
    public uint ColorOf(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Trace or DiagnosticLevel.Debug => Dim,
        DiagnosticLevel.Warning => Warning,
        DiagnosticLevel.Error => Error,
        _ => Text,
    };
}
