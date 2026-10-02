using System.Globalization;
using System.Text.Json;
using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// One user theme file (2026-10-01, the user's ask: themes of their own beside the built-ins, as JSON in
/// <c>&lt;home&gt;/themes</c>): reading it (<see cref="Read"/>), turning it into a <see cref="ThemePalette"/> over its
/// base (<see cref="Build"/>) and writing a theme out as one (<see cref="Export"/>). A file says only what it changes:
/// the colours, the gradient and the style changes it leaves out are its base's. Two of a base's slots are derived
/// rather than set — <c>warn</c> is the highlight and the gradient runs secondary → tertiary → primary → warm →
/// highlight on synthwave and most built-ins — and a derived slot the file does not set is derived again from the
/// file's colours, so a file that recolours the accents gets a banner in them; a base that sets the slot itself
/// (netrunner's gradient, noir's warn) passes its value on. A bad key or value is skipped with a note and the theme
/// still loads; <see cref="ThemeCatalog"/> decides what skips the whole file. Pure but for <see cref="Read"/>.
/// </summary>
public static class ThemeFile
{
    /// <summary>The longest gradient a file may give (the banner samples it, so more adds nothing visible).</summary>
    public const int MaxGradientStops = 16;

    /// <summary>The longest theme name: it pads the picker's column.</summary>
    public const int MaxNameLength = 32;

    /// <summary>Reads and parses <paramref name="path"/>; the problem (unreadable, not JSON) when it fails.</summary>
    public static ThemeFileData? Read(string path, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(path);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ThemeText.Unreadable(ex.Message);
            return null;
        }

        try
        {
            var data = JsonSerializer.Deserialize(text, ThemeJsonContext.Default.ThemeFileData);
            problem = data is null ? ThemeText.NotJson("empty") : null;
            return data;
        }
        catch (JsonException ex)
        {
            problem = ThemeText.NotJson(ex.Message);
            return null;
        }
    }

    /// <summary>The theme's name: its <c>name</c>, else the file's stem; trimmed and lower-cased.</summary>
    public static string NameOf(ThemeFileData data, string path)
    {
        ArgumentNullException.ThrowIfNull(data);
        string name = string.IsNullOrWhiteSpace(data.Name) ? Path.GetFileNameWithoutExtension(path) : data.Name;
        return name.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Whether <paramref name="name"/> can be a theme's: a word <c>/theme</c> takes and the setting stores — lower-case
    /// letters, digits, <c>-</c> and <c>_</c>, a letter or digit first, up to <see cref="MaxNameLength"/> — and not
    /// <see cref="ThemeText.ExportWord"/>, <c>/theme</c>'s own sub-command.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name == ThemeText.ExportWord || !IsLowerAlnum(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!IsLowerAlnum(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The palette <paramref name="data"/> makes over <paramref name="basePalette"/>; a key or value it cannot use is
    /// skipped and said in <paramref name="notes"/>.
    /// </summary>
    public static ThemePalette Build(string name, ThemeFileData data, ThemePalette basePalette, string sourcePath, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(basePalette);
        ArgumentNullException.ThrowIfNull(notes);

        Color[] colors = basePalette.Colors();
        var set = new bool[colors.Length];
        foreach (var (key, value) in data.Colors ?? [])
        {
            if (!ThemeKeys.TryParseColor(key, out var slot))
            {
                notes.Add(ThemeText.UnknownColor(key));
            }
            else if (!TryParseColor(value, out var color))
            {
                notes.Add(ThemeText.BadColor(key, value));
            }
            else
            {
                colors[(int)slot] = color;
                set[(int)slot] = true;
            }
        }

        int warn = (int)ThemeColorSlot.Warn, highlight = (int)ThemeColorSlot.Highlight;
        if (!set[warn] && basePalette.Warn == basePalette.Highlight)
        {
            colors[warn] = colors[highlight];
        }

        Color[] gradient = Gradient(data.Gradient, notes) ?? GradientOver(basePalette, colors);
        var styles = Styles(data.Styles, basePalette.Styles, colors, notes);
        string description = string.IsNullOrWhiteSpace(data.Description) ? ThemeText.CustomDescription : data.Description.Trim();
        return ThemePalette.FromColors(name, description, colors, gradient, styles, sourcePath);
    }

    /// <summary>
    /// <paramref name="palette"/> as a full file named <paramref name="name"/> (<c>/theme export</c>): every colour role,
    /// the gradient and the style changes it has, so the copy stands alone with no base. Its description is kept.
    /// </summary>
    public static string Export(ThemePalette palette, string name)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var data = new ThemeFileData
        {
            Name = name,
            Description = palette.Description,
            Colors = Enumerable.Range(0, ThemeKeys.Colors.Count).ToDictionary(i => ThemeKeys.Colors[i], i => Theme.ToHex(palette.ColorOf((ThemeColorSlot)i))),
            Gradient = palette.GradientStops.Select(Theme.ToHex).ToList(),
            Styles = (palette.Styles ?? new Dictionary<ThemeStyleSlot, StyleOverride>())
                .OrderBy(pair => pair.Key)
                .ToDictionary(pair => ThemeKeys.Of(pair.Key), pair => ToData(pair.Value)),
        };
        return JsonSerializer.Serialize(data, ThemeJsonContext.Default.ThemeFileData);
    }

    /// <summary><c>#RRGGBB</c>, <c>RRGGBB</c>, <c>#RGB</c> or <c>RGB</c>, any case, as a colour.</summary>
    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        string hex = (text ?? "").Trim();
        if (hex.StartsWith('#'))
        {
            hex = hex[1..];
        }

        if (hex.Length == 3)
        {
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }

        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
        {
            return false;
        }

        color = new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static bool IsLowerAlnum(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');

    /// <summary>The file's gradient, or null when it gives none or one it cannot use (noted).</summary>
    private static Color[]? Gradient(List<string>? stops, ICollection<string> notes)
    {
        if (stops is null)
        {
            return null;
        }

        if (stops.Count < 2 || stops.Count > MaxGradientStops)
        {
            notes.Add(ThemeText.BadGradient(stops.Count));
            return null;
        }

        var colors = new Color[stops.Count];
        for (int i = 0; i < stops.Count; i++)
        {
            if (!TryParseColor(stops[i], out colors[i]))
            {
                notes.Add(ThemeText.BadColor(string.Create(CultureInfo.InvariantCulture, $"gradient[{i}]"), stops[i]));
                return null;
            }
        }

        return colors;
    }

    /// <summary>The base's gradient, derived again from <paramref name="colors"/> when the base's own was derived.</summary>
    private static Color[] GradientOver(ThemePalette basePalette, Color[] colors)
    {
        Color[] derivedFromBase = ThemePalette.DerivedGradient(basePalette.Primary, basePalette.Secondary, basePalette.Tertiary, basePalette.Warm, basePalette.Highlight);
        return basePalette.GradientStops.SequenceEqual(derivedFromBase)
            ? ThemePalette.DerivedGradient(
                colors[(int)ThemeColorSlot.Primary], colors[(int)ThemeColorSlot.Secondary], colors[(int)ThemeColorSlot.Tertiary],
                colors[(int)ThemeColorSlot.Warm], colors[(int)ThemeColorSlot.Highlight])
            : (Color[])basePalette.GradientStops.Clone();
    }

    /// <summary>The base's style changes with the file's over them; null when neither has any.</summary>
    private static Dictionary<ThemeStyleSlot, StyleOverride>? Styles(
        Dictionary<string, ThemeStyleData>? written, IReadOnlyDictionary<ThemeStyleSlot, StyleOverride>? inherited, Color[] colors, ICollection<string> notes)
    {
        var styles = inherited is null ? [] : new Dictionary<ThemeStyleSlot, StyleOverride>(inherited);
        foreach (var (key, value) in written ?? [])
        {
            if (!ThemeKeys.TryParseStyle(key, out var slot))
            {
                notes.Add(ThemeText.UnknownStyle(key));
                continue;
            }

            if (value is null)
            {
                continue;
            }

            Color? fg = StyleColor(key + ".fg", value.Fg, colors, notes);
            Color? bg = StyleColor(key + ".bg", value.Bg, colors, notes);
            var (on, off) = Decorations(value);
            styles[slot] = new StyleOverride(fg, bg, on, off).Over(styles.GetValueOrDefault(slot));
        }

        return styles.Count == 0 ? null : styles;
    }

    /// <summary>A style's colour: a hex, or a colour role's word meaning that role in this theme; null when absent or unusable (noted).</summary>
    private static Color? StyleColor(string where, string? text, Color[] colors, ICollection<string> notes)
    {
        if (text is null)
        {
            return null;
        }

        if (ThemeKeys.TryParseColor(text, out var role))
        {
            return colors[(int)role];
        }

        if (TryParseColor(text, out var color))
        {
            return color;
        }

        notes.Add(ThemeText.BadColor(where, text));
        return null;
    }

    private static (Decoration On, Decoration Off) Decorations(ThemeStyleData data)
    {
        Decoration on = Decoration.None, off = Decoration.None;
        Flag(data.Bold, Decoration.Bold);
        Flag(data.Italic, Decoration.Italic);
        Flag(data.Underline, Decoration.Underline);
        Flag(data.Dim, Decoration.Dim);
        Flag(data.Strikethrough, Decoration.Strikethrough);
        return (on, off);

        void Flag(bool? value, Decoration decoration)
        {
            if (value == true)
            {
                on |= decoration;
            }
            else if (value == false)
            {
                off |= decoration;
            }
        }
    }

    private static ThemeStyleData ToData(StyleOverride change)
    {
        return new ThemeStyleData
        {
            Fg = change.Foreground is { } fg ? Theme.ToHex(fg) : null,
            Bg = change.Background is { } bg ? Theme.ToHex(bg) : null,
            Bold = Flag(Decoration.Bold),
            Italic = Flag(Decoration.Italic),
            Underline = Flag(Decoration.Underline),
            Dim = Flag(Decoration.Dim),
            Strikethrough = Flag(Decoration.Strikethrough),
        };

        bool? Flag(Decoration decoration) =>
            change.Set.HasFlag(decoration) ? true : change.Clear.HasFlag(decoration) ? false : null;
    }
}
