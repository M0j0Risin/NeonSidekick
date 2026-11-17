using Spectre.Console;

namespace NeonSidekick.Companion;

/// <summary>
/// A theme's colors by role — Neon Sidekick's fifteen, in its order (<c>ThemeColorSlot</c>), so a host fills this from its
/// theme in one line. Every character maps these onto its own parts (<see cref="ICompanion.Theme"/>; the robot's mapping is
/// <see cref="RobotPalette.FromTheme"/>). <paramref name="Bg"/> is the page behind everything: dark in most shipped themes, light in the
/// <c>daylight</c> ones (2026-11-16), so a mapping reads by contrast with it rather than by lightness.
/// </summary>
/// <param name="Primary">The lead accent.</param>
/// <param name="Secondary">The second accent; the color the prompt and the user's text wear.</param>
/// <param name="Tertiary">The third accent.</param>
/// <param name="Deep">A saturated tone a step off the page: the rules between panes.</param>
/// <param name="Highlight">A light, warm highlight.</param>
/// <param name="Warm">A warm accent.</param>
/// <param name="Tint">The theme's tint for pale surfaces.</param>
/// <param name="Ink">The body text: the palest color on a dark page, the darkest on a light one.</param>
/// <param name="Dim">Muted text.</param>
/// <param name="Dimmer">More muted still: placeholders.</param>
/// <param name="Bg">The page background.</param>
/// <param name="PanelBg">Panels on the page: set a little off <paramref name="Bg"/>.</param>
/// <param name="Good">Success.</param>
/// <param name="Bad">Errors.</param>
/// <param name="Warn">Warnings.</param>
public sealed record ThemeRoles(
    Color Primary,
    Color Secondary,
    Color Tertiary,
    Color Deep,
    Color Highlight,
    Color Warm,
    Color Tint,
    Color Ink,
    Color Dim,
    Color Dimmer,
    Color Bg,
    Color PanelBg,
    Color Good,
    Color Bad,
    Color Warn)
{
    /// <summary>A theme's fifteen colors as roles (2026-10-20, the welcome splash's robot wearing <see cref="UI.Theme.Current"/>).</summary>
    public static ThemeRoles From(UI.ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return new(
            palette.Primary, palette.Secondary, palette.Tertiary, palette.Deep, palette.Highlight, palette.Warm, palette.Tint,
            palette.Ink, palette.Dim, palette.Dimmer, palette.Bg, palette.PanelBg, palette.Good, palette.Bad, palette.Warn);
    }
}
