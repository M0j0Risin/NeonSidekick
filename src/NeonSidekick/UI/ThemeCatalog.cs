using NeonSidekick.Diagnostics;

namespace NeonSidekick.UI;

/// <summary>A theme file's problem: the whole file skipped, or a key in it ignored (<see cref="ThemeText"/>'s sentence).</summary>
public sealed record ThemeProblem(string FilePath, string Problem);

/// <summary>The themes found by one <see cref="ThemeCatalog.Scan"/> and what their files got wrong.</summary>
public sealed record ThemeScan(IReadOnlyList<ThemePalette> Themes, IReadOnlyList<ThemeProblem> Problems)
{
    /// <summary>The names, in menu order.</summary>
    public IReadOnlyList<string> Names => Themes.Select(t => t.Name).ToList();
}

/// <summary>
/// Every theme the operator can pick (2026-10-01, the user's ask): the built-ins in <see cref="ThemePalette.All"/>'s
/// order, then the user's themes — every <c>*.json</c> in <c>&lt;home&gt;/themes</c> (<see cref="DirectoryName"/>), in
/// name order. Read afresh at every <see cref="Scan"/>, so a file dropped in or edited shows the next time a list opens
/// with no restart (the <c>comfy</c> folder's habit; the files are small, so there is no cache). A file is skipped,
/// with a problem, when it cannot be read or parsed, when its name is no theme name, a built-in's (the built-in
/// wins, the user's call) or a name an earlier file took, and when its <c>base</c> is no theme, leads back to
/// itself or did not load; a base may be a built-in or another file. A missing folder is no user theme.
/// </summary>
public static class ThemeCatalog
{
    /// <summary>The folder's name under the home.</summary>
    public const string DirectoryName = "themes";

    private const string Category = "Theme";

    /// <summary>The built-ins alone: what a scan of no folder finds.</summary>
    public static readonly ThemeScan BuiltIn = new(ThemePalette.All, []);

    /// <summary>The themes of <paramref name="directory"/> (null or missing: the built-ins alone).</summary>
    public static ThemeScan Scan(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return BuiltIn;
        }

        var problems = new List<ThemeProblem>();
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new ThemeProblem(directory, ThemeText.Unreadable(ex.Message)));
            return new ThemeScan(ThemePalette.All, problems);
        }

        var builtIns = ThemePalette.All.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var pending = new Dictionary<string, (string Path, ThemeFileData Data)>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            var data = ThemeFile.Read(file, out string? problem);
            if (data is null)
            {
                problems.Add(new ThemeProblem(file, problem!));
                continue;
            }

            string name = ThemeFile.NameOf(data, file);
            if (!ThemeFile.IsValidName(name))
            {
                problems.Add(new ThemeProblem(file, ThemeText.BadName(name)));
            }
            else if (builtIns.ContainsKey(name))
            {
                problems.Add(new ThemeProblem(file, ThemeText.BuiltInClash(name)));
            }
            else if (pending.TryGetValue(name, out var first))
            {
                problems.Add(new ThemeProblem(file, ThemeText.Duplicate(name, first.Path)));
            }
            else
            {
                pending[name] = (file, data);
            }
        }

        var built = new Dictionary<string, ThemePalette?>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in pending.Keys)
        {
            Resolve(name);
        }

        var users = built.Values.OfType<ThemePalette>().OrderBy(p => p.Name, StringComparer.Ordinal);
        return new ThemeScan([.. ThemePalette.All, .. users], problems);

        // A user theme over its base, built once; null (with its problem said) when it cannot be.
        ThemePalette? Resolve(string name)
        {
            if (built.TryGetValue(name, out var done))
            {
                return done;
            }

            var (path, data) = pending[name];
            string baseName = string.IsNullOrWhiteSpace(data.Base) ? ThemeName.Default : data.Base.Trim().ToLowerInvariant();
            ThemePalette? basePalette = null;
            string? failure = null;
            if (builtIns.TryGetValue(baseName, out var builtIn))
            {
                basePalette = builtIn;
            }
            else if (!pending.ContainsKey(baseName))
            {
                failure = ThemeText.NoBase(baseName);
            }
            else if (visiting.Contains(baseName) || string.Equals(baseName, name, StringComparison.OrdinalIgnoreCase))
            {
                failure = ThemeText.BaseCycle(baseName);
            }
            else
            {
                visiting.Add(name);
                basePalette = Resolve(baseName);
                visiting.Remove(name);
                failure = basePalette is null ? ThemeText.BaseFailed(baseName) : null;
            }

            if (basePalette is null)
            {
                problems.Add(new ThemeProblem(path, failure!));
                built[name] = null;
                return null;
            }

            var notes = new List<string>();
            var palette = ThemeFile.Build(name, data, basePalette, path, notes);
            problems.AddRange(notes.Select(note => new ThemeProblem(path, note)));
            built[name] = palette;
            return palette;
        }
    }

    /// <summary>Says each of <paramref name="scan"/>'s problems as a warning (the screen shows warnings in the transcript).</summary>
    public static void Report(ThemeScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        foreach (var problem in scan.Problems)
        {
            DiagnosticLog.Warn(Category, ThemeText.Problem(problem.FilePath, problem.Problem));
        }
    }
}
