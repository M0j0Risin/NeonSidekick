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
/// Every theme the operator can pick (2026-10-01, the user's ask): the built-ins (<see cref="ThemeLibrary"/>, <c>assets/themes</c>
/// embedded since 2026-10-05) and the user's themes — every <c>*.json</c> in <c>&lt;home&gt;/themes</c> (<see cref="DirectoryName"/>)
/// and in its first-level subfolders (2026-10-02, the user's ask: a category folder dropped in whole; a dot-folder is skipped, a
/// deeper one never read) — as one list in name order (2026-10-03, the user's ask), so the pickers, <c>/theme</c>'s completion and
/// its error all read A to Z. Files are taken in <see cref="JsonFiles"/>' order, the folder's own first, so a loose file keeps a name
/// a subfolder's file also gives (that one is skipped as a duplicate, named by its path under the folder). Read afresh at every
/// <see cref="Scan"/>, so a file dropped in or edited shows the next time a list opens with no restart (the <c>comfy</c> folder's
/// habit; the files are small, so there is no cache). A file named like a built-in replaces it (2026-10-01, the user's call); one
/// that fails to load leaves the built-in in its place. Every file stands alone (2026-10-05, the user's call: <see cref="ThemeFile"/>
/// reads no <c>base</c>; a file built on a base, a built-in or another file, until then). A file is skipped, with a problem, when
/// it cannot be read or parsed, when its name is no theme name or a name an earlier file took, and when it leaves a colour role
/// unset. A missing folder is no user theme.
/// </summary>
public static class ThemeCatalog
{
    /// <summary>The folder's name under the home.</summary>
    public const string DirectoryName = "themes";

    private const string Category = "Theme";

    /// <summary>The built-ins in name order (2026-10-03).</summary>
    public static IReadOnlyList<ThemePalette> SortedBuiltIns => ThemeLibrary.All;

    /// <summary>The built-ins alone: what a scan of no folder finds.</summary>
    public static readonly ThemeScan BuiltIn = new(ThemeLibrary.All, []);

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
            return new ThemeScan(SortedBuiltIns, problems);
        }

        string Shown(string file) => Path.GetRelativePath(directory, file);

        // A file claims its name before it is built, so a later file of that name is a duplicate even when the first fails.
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mine = new Dictionary<string, ThemePalette>(StringComparer.OrdinalIgnoreCase);
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
                continue;
            }

            if (claimed.TryGetValue(name, out string? first))
            {
                problems.Add(new ThemeProblem(file, ThemeText.Duplicate(name, Shown(first)), Shown(file)));
                continue;
            }

            claimed[name] = file;
            var notes = new List<string>();
            var palette = ThemeFile.Build(name, data, file, notes, out problem);
            problems.AddRange(notes.Select(note => new ThemeProblem(file, note, Shown(file))));
            if (palette is null)
            {
                problems.Add(new ThemeProblem(file, problem!, Shown(file)));
                continue;
            }

            mine[name] = palette;
        }

        // A file that built stands in for its built-in; one that failed leaves the built-in. All of them A to Z.
        var builtIns = ThemeLibrary.All.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var themes = ThemeLibrary.All.Select(p => mine.GetValueOrDefault(p.Name) ?? p)
            .Concat(mine.Values.Where(p => !builtIns.Contains(p.Name)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        return new ThemeScan(themes, problems);
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
