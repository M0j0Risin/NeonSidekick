using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.UI;

/// <summary>
/// The theme setting (2026-09-23, the user's ask): the names the operator picks from on the General
/// tab's <c>Theme</c> row or with <c>/theme</c>, the way <see cref="ThumbnailSize"/> maps its words.
/// <see cref="Resolve"/> is the one place the saved string becomes a <see cref="ThemePalette"/>: a
/// hand-edited value that is none of them falls back to <see cref="Default"/> with a warning.
/// Named <c>ThemeName</c>, not <c>Theme</c>, so it never clashes with the palette class. Since 2026-10-01 the user's own
/// themes (<see cref="ThemeCatalog"/>) are names too wherever a themes folder or a scan is passed.
/// </summary>
public static class ThemeName
{
    /// <summary>
    /// The default theme, pinned by <c>AppSettingsTests</c>: a fresh profile's and what an unknown name reads as. Collider since
    /// 2026-10-05 (the user's call; synthwave until then); the name of an <c>assets/themes</c> file, which the tests hold to
    /// being shipped.
    /// </summary>
    public const string Default = "collider";

    /// <summary>The built-ins' names A to Z (since 2026-10-03; <see cref="ThemeLibrary"/>'s since 2026-10-05); a scan's own are <see cref="ThemeScan.Names"/>.</summary>
    public static readonly string[] Names = [.. ThemeLibrary.All.Select(p => p.Name)];

    private const string Category = "Theme";

    /// <summary>Trims and ignores case; false (and the default) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out ThemePalette palette) => TryParse(text, ThemeLibrary.All, out palette);

    /// <summary>As <see cref="TryParse(string?, out ThemePalette)"/> among <paramref name="themes"/> (a <see cref="ThemeCatalog"/> scan's, 2026-10-01).</summary>
    public static bool TryParse(string? text, IReadOnlyList<ThemePalette> themes, out ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(themes);
        string? name = text?.Trim();
        foreach (var candidate in themes)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                palette = candidate;
                return true;
            }
        }

        palette = ThemeLibrary.Default;
        return false;
    }

    /// <summary>The note beside a name in the picker and <c>/theme</c>'s argument list; empty for an unknown one. Pinned.</summary>
    public static string Describe(string name) => Describe(name, ThemeLibrary.All);

    /// <summary>As <see cref="Describe(string)"/> among <paramref name="themes"/>.</summary>
    public static string Describe(string name, IReadOnlyList<ThemePalette> themes) => TryParse(name, themes, out var palette) ? palette.Description : "";

    /// <summary>The palette in force for <paramref name="effective"/> among the built-ins; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static ThemePalette Resolve(AppSettingsData effective) => Resolve(effective, ThemeCatalog.BuiltIn);

    /// <summary>
    /// As <see cref="Resolve(AppSettingsData)"/> with the user's themes of <paramref name="themesDirectory"/> too
    /// (2026-10-01). The folder is always scanned: a built-in's name may be a file's that overrides it (later on 2026-10-01,
    /// the user's call; a built-in's name was answered without reading the folder until then).
    /// </summary>
    public static ThemePalette Resolve(AppSettingsData effective, string? themesDirectory)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Resolve(effective, ThemeCatalog.Scan(themesDirectory));
    }

    private static ThemePalette Resolve(AppSettingsData effective, ThemeScan scan)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.Theme, scan.Themes, out var palette))
        {
            return palette;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.Theme)}='{effective.Theme}' is not one of {string.Join(", ", scan.Names)}. Using {Default}.");
        TryParse(Default, scan.Themes, out var fallback);   // the scan's default: a file's when one overrides it
        return fallback;
    }

    /// <summary>Puts <paramref name="effective"/>'s theme in force (<see cref="Theme.Use"/>), among the built-ins.</summary>
    public static void Apply(AppSettingsData effective) => Theme.Use(Resolve(effective));

    /// <summary>Puts <paramref name="effective"/>'s theme in force, among the built-ins and the themes of <paramref name="themesDirectory"/>.</summary>
    public static void Apply(AppSettingsData effective, string? themesDirectory) => Theme.Use(Resolve(effective, themesDirectory));
}
