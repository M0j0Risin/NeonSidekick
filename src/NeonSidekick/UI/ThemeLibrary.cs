using NeonSidekick.Diagnostics;

namespace NeonSidekick.UI;

/// <summary>The built-in themes one <see cref="ThemeLibrary.Load"/> made: the themes A to Z, each one's category folder, and the problems.</summary>
public sealed record BuiltInThemes(
    IReadOnlyList<ThemePalette> Themes,
    IReadOnlyDictionary<string, string> Categories,
    IReadOnlyList<ThemeProblem> Problems)
{
    /// <summary>The default theme (<see cref="ThemeName.Default"/>); <see cref="ThemePalette.Emergency"/> when it did not load.</summary>
    public ThemePalette Default { get; } =
        Themes.FirstOrDefault(t => string.Equals(t.Name, ThemeName.Default, StringComparison.Ordinal)) ?? ThemePalette.Emergency;
}

/// <summary>
/// The built-in themes (2026-10-05, the user's ask: themes entirely JSON, so a theme is added, changed or removed by a file edit
/// and nothing in code; ten palettes compiled into <c>ThemePalette</c> until then, the fifty in <c>assets/themes</c> only examples
/// to copy). Every <c>assets/themes/&lt;category&gt;/&lt;name&gt;.json</c> is embedded by the csproj as
/// <see cref="ResourcePrefix"/><c>&lt;category&gt;/&lt;name&gt;.json</c> and built here once, the first time a theme is asked for,
/// through <see cref="ThemeFile"/> exactly as a user's file is (standalone: every colour role its own) but with no source path, so
/// <see cref="ThemePalette.IsBuiltIn"/> holds. The <c>VoicePresets.Embedded</c> shape: a file that does not load is left out with a
/// warning, never fatal; <c>ThemeLibraryTests</c> holds every shipped one to loading with no problem, so a warning here means a
/// build that skipped the tests. A resource loose at the root or deeper than one folder is left out too. The default
/// (<see cref="ThemeName.Default"/>) failing leaves <see cref="ThemePalette.Emergency"/> in its place, so the app always has a theme.
/// </summary>
public static class ThemeLibrary
{
    /// <summary>The csproj's <c>LogicalName</c> prefix for the theme files.</summary>
    public const string ResourcePrefix = "themes/";

    private const string Category = "Theme";

    private static readonly Lazy<BuiltInThemes> s_embedded = new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The built-ins, A to Z.</summary>
    public static IReadOnlyList<ThemePalette> All => s_embedded.Value.Themes;

    /// <summary>The default theme (<see cref="ThemeName.Default"/>, collider since 2026-10-05).</summary>
    public static ThemePalette Default => s_embedded.Value.Default;

    /// <summary>The whole load: the themes, their categories and what went wrong.</summary>
    public static BuiltInThemes Embedded => s_embedded.Value;

    /// <summary>The built-in named <paramref name="name"/> (any case), if there is one.</summary>
    public static bool TryGet(string? name, out ThemePalette palette)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                palette = candidate;
                return true;
            }
        }

        palette = Default;
        return false;
    }

    /// <summary>The built-in named <paramref name="name"/>; throws for none (a caller that knows the name is shipped).</summary>
    public static ThemePalette Get(string name) =>
        TryGet(name, out var palette) ? palette : throw new ArgumentException($"No built-in theme \"{name}\".", nameof(name));

    /// <summary>The category folder of the built-in <paramref name="name"/> (<c>machines</c>), or null.</summary>
    public static string? CategoryOf(string name) => Embedded.Categories.GetValueOrDefault(name);

    /// <summary>
    /// The built-ins from <paramref name="resources"/>: logical name (<see cref="ResourcePrefix"/><c>&lt;category&gt;/&lt;file&gt;.json</c>,
    /// a backslash taken as a slash) to its text. The seam the tests feed; <see cref="Embedded"/> is this over the manifest. Files are
    /// taken in name order, so of two giving one name the first (by category, then file) keeps it, the other skipped as a duplicate.
    /// </summary>
    public static BuiltInThemes Load(IEnumerable<KeyValuePair<string, string>> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var themes = new Dictionary<string, ThemePalette>(StringComparer.OrdinalIgnoreCase);
        var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shownOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<ThemeProblem>();
        foreach (var (resource, text) in resources.Select(r => (Normalize(r.Key), r.Value)).OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase))
        {
            string shown = resource[ResourcePrefix.Length..];
            string[] parts = shown.Split('/');
            if (parts.Length != 2)
            {
                problems.Add(new ThemeProblem(resource, ThemeText.NotInCategory, shown));
                continue;
            }

            var data = ThemeFile.Parse(text, out string? problem);
            if (data is null)
            {
                problems.Add(new ThemeProblem(resource, problem!, shown));
                continue;
            }

            string name = ThemeFile.NameOf(data, parts[1]);
            if (!ThemeFile.IsValidName(name))
            {
                problems.Add(new ThemeProblem(resource, ThemeText.BadName(name), shown));
                continue;
            }

            if (shownOf.TryGetValue(name, out string? first))
            {
                problems.Add(new ThemeProblem(resource, ThemeText.Duplicate(name, first), shown));
                continue;
            }

            var notes = new List<string>();
            var palette = ThemeFile.Build(name, data, sourcePath: null, notes, out problem);
            problems.AddRange(notes.Select(note => new ThemeProblem(resource, note, shown)));
            if (palette is null)
            {
                problems.Add(new ThemeProblem(resource, problem!, shown));
                continue;
            }

            themes[name] = palette;
            categories[name] = parts[0];
            shownOf[name] = shown;
        }

        return new BuiltInThemes([.. themes.Values.OrderBy(t => t.Name, StringComparer.Ordinal)], categories, problems);
    }

    private static string Normalize(string resource) => resource.Replace('\\', '/');

    /// <summary>The manifest's theme files, loaded; each problem and a missing default said as a warning.</summary>
    private static BuiltInThemes LoadEmbedded()
    {
        var assembly = typeof(ThemeLibrary).Assembly;
        var resources = new List<KeyValuePair<string, string>>();
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => Normalize(n).StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            resources.Add(new(resource, reader.ReadToEnd()));
        }

        var loaded = Load(resources);
        foreach (var problem in loaded.Problems)
        {
            DiagnosticLog.Warn(Category, ThemeText.Problem(problem.Shown ?? problem.FilePath, problem.Problem));
        }

        if (ReferenceEquals(loaded.Default, ThemePalette.Emergency))
        {
            DiagnosticLog.Warn(Category, ThemeText.NoDefault(ThemeName.Default));
        }

        return loaded;
    }
}
