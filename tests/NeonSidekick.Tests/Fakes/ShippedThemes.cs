using NeonSidekick.UI;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The built-in themes the tests name (2026-10-05): the palettes were <c>ThemePalette</c>'s static fields until the themes became
/// <c>assets/themes</c>' files (<see cref="ThemeLibrary"/>), so a test that renders in noir or pins synthwave reads the shipped file.
/// Synthwave stays the suite's theme in force (<see cref="ModuleInit"/>, <see cref="ThemeScope"/>) though the app starts on
/// <see cref="ThemeName.Default"/>: many tests pin its colours in what they render.
/// </summary>
internal static class ShippedThemes
{
    public static IReadOnlyList<ThemePalette> All => ThemeLibrary.All;
    public static ThemePalette Synthwave => ThemeLibrary.Get("synthwave");
    public static ThemePalette Netrunner => ThemeLibrary.Get("netrunner");
    public static ThemePalette Nostromo => ThemeLibrary.Get("nostromo");
    public static ThemePalette Noir => ThemeLibrary.Get("noir");
    public static ThemePalette Cyberpunk => ThemeLibrary.Get("cyberpunk");
    public static ThemePalette Vaporwave => ThemeLibrary.Get("vaporwave");
    public static ThemePalette Grid => ThemeLibrary.Get("grid");
}
