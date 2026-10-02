using NeonSidekick.Diagnostics;

namespace NeonSidekick.UI;

/// <summary>
/// A theme file's problem: the whole file skipped, or a key in it ignored (<see cref="ThemeText"/>'s sentence). <paramref name="Shown"/>
/// is how the warning names the file: its path under the themes folder (<c>solid\blueprint.json</c>), the file name when null.
/// </summary>
public sealed record ThemeProblem(string FilePath, string Problem, string? Shown = null);

/// <summary>The themes found by one <see cref="ThemeCatalog.Scan"/> and what their files got wrong.</summary>
public sealed record ThemeScan(IReadOnlyList<ThemePalette> Themes, IReadOnlyList<ThemeProblem> Problems)
{
    /// <summary>The names, in menu order.</summary>
    public IReadOnlyList<string> Names => Themes.Select(t => t.Name).ToList();
}

/// <summary>
/// Every theme the operator can pick (2026-10-01, the user's ask): the built-ins in <see cref="ThemePalette.All"/>'s
/// order, then the user's themes — every <c>*.json</c> in <c>&lt;home&gt;/themes</c> (<see cref="DirectoryName"/>) and in its
/// first-level subfolders (2026-10-02, the user's ask: a category folder of <c>assets/themes</c> dropped in whole; a dot-folder is
/// skipped, a deeper one never read), in name order. Files are taken in <see cref="JsonFiles"/>' order, the folder's own first, so a
/// loose file keeps a name a subfolder's file also gives (that one is skipped as a duplicate, named by its path under the folder). Read afresh at every <see cref="Scan"/>, so a file dropped in or edited shows the next time a list opens
/// with no restart (the <c>comfy</c> folder's habit; the files are small, so there is no cache). A file named like a
/// built-in overrides it (later on 2026-10-01, the user's call; the file was skipped and the built-in won until then):
/// it takes the built-in's place in the list, and every theme whose base names it, the default base included, builds on
/// the file. Only the override itself, when its base is its own name or left out, builds on the compiled built-in; an
/// override that fails to load leaves the built-in in its place. A file is skipped, with a
/// problem, when it cannot be read or parsed, when its name is no theme name or a name an earlier file took, and when its
/// <c>base</c> is no theme, leads back to itself or did not load; a base may be a built-in or another file. A missing
/// folder is no user theme.
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
            files = JsonFiles(directory, problems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new ThemeProblem(directory, ThemeText.Unreadable(ex.Message)));
            return new ThemeScan(ThemePalette.All, problems);
        }

        string Shown(string file) => Path.GetRelativePath(directory, file);

        var builtIns = ThemePalette.All.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var pending = new Dictionary<string, (string Path, ThemeFileData Data)>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            var data = ThemeFile.Read(file, out string? problem);
            if (data is null)
            {
                problems.Add(new ThemeProblem(file, problem!, Shown(file)));
                continue;
            }

            string name = ThemeFile.NameOf(data, file);
            if (!ThemeFile.IsValidName(name))
            {
                problems.Add(new ThemeProblem(file, ThemeText.BadName(name), Shown(file)));
            }
            else if (pending.TryGetValue(name, out var first))
            {
                problems.Add(new ThemeProblem(file, ThemeText.Duplicate(name, Shown(first.Path)), Shown(file)));
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

        // An override that built takes its built-in's place; one that failed leaves the built-in there.
        var themes = ThemePalette.All.Select(p => built.GetValueOrDefault(p.Name) ?? p).ToList();
        themes.AddRange(built.Values.OfType<ThemePalette>()
            .Where(p => !builtIns.ContainsKey(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal));
        return new ThemeScan(themes, problems);

        // A user theme over its base, built once; null (with its problem said) when it cannot be.
        ThemePalette? Resolve(string name)
        {
            if (built.TryGetValue(name, out var done))
            {
                return done;
            }

            var (path, data) = pending[name];
            // An override's base is its own built-in when left out, so a file that changes one colour of noir is noir with that colour.
            string baseName = string.IsNullOrWhiteSpace(data.Base)
                ? (builtIns.ContainsKey(name) ? name : ThemeName.Default)
                : data.Base.Trim().ToLowerInvariant();
            ThemePalette? basePalette = null;
            string? failure = null;
            bool own = string.Equals(baseName, name, StringComparison.OrdinalIgnoreCase);
            if (own && builtIns.TryGetValue(baseName, out var original))
            {
                basePalette = original;   // an override over its own built-in
            }
            else if (!pending.ContainsKey(baseName))
            {
                basePalette = builtIns.GetValueOrDefault(baseName);
                failure = basePalette is null ? ThemeText.NoBase(baseName) : null;
            }
            else if (visiting.Contains(baseName) || own)
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
                problems.Add(new ThemeProblem(path, failure!, Shown(path)));
                built[name] = null;
                return null;
            }

            var notes = new List<string>();
            var palette = ThemeFile.Build(name, data, basePalette, path, notes);
            problems.AddRange(notes.Select(note => new ThemeProblem(path, note, Shown(path))));
            built[name] = palette;
            return palette;
        }
    }

    /// <summary>
    /// The <c>*.json</c> files a scan reads, in the order it takes them: <paramref name="directory"/>'s own in name order, then each
    /// first-level subfolder's in name order, the folders in name order too (2026-10-02). A folder whose name starts with <c>.</c> is
    /// skipped; a subfolder that cannot be listed is a problem and skipped, the root's failure the caller's.
    /// </summary>
    public static List<string> JsonFiles(string directory, List<ThemeProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string folder in Directory.EnumerateDirectories(directory).Where(d => !Path.GetFileName(d).StartsWith('.')).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new ThemeProblem(folder, ThemeText.Unreadable(ex.Message), Path.GetRelativePath(directory, folder)));
            }
        }

        return files;
    }

    /// <summary>Says each of <paramref name="scan"/>'s problems as a warning (the screen shows warnings in the transcript).</summary>
    public static void Report(ThemeScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        foreach (var problem in scan.Problems)
        {
            DiagnosticLog.Warn(Category, ThemeText.Problem(problem.Shown ?? Path.GetFileName(problem.FilePath), problem.Problem));
        }
    }
}
