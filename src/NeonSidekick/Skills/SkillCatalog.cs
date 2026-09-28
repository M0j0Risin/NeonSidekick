using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Skills;

/// <summary>
/// The skills the model can load: every folder one level under a root (<see cref="SkillRoots"/>)
/// that holds a file named exactly <see cref="FileName"/>, read the Agent Skills way
/// (<see cref="SkillFrontmatter"/>). Scanned once per turn (<c>ChatScreen.PrepareTurn</c>) so a
/// skill written by <c>skill_editor</c> or dropped into a folder is in the next request's catalog;
/// a file is re-read only when its write time or length changed, the <c>PromptFile</c> economy.
///
/// <para>Precedence is the roots' order: the first skill found by name wins, a later one with the
/// same name is kept as <see cref="Shadowed"/> (listed on <c>/skill</c>, logged once at Info).
/// A folder whose <c>SKILL.md</c> has no usable frontmatter is a <see cref="SkillProblem"/>, warned
/// once per file version and never offered; a name that differs from the folder or runs past the
/// specification's cap loads with a <see cref="Skill.Warning"/>. Nothing here throws: an
/// unreadable root is empty, an unreadable file a problem.</para>
///
/// <para>The body is not cached: <see cref="ReadBody"/> reads the file at activation, so the
/// model sees an edit made since the scan. <see cref="Resources"/> lists the files bundled beside
/// the <c>SKILL.md</c> and <see cref="ReadResource"/> reads one, the way <c>load_skill</c> serves
/// the specification's third tier — the file tools cannot reach a skill folder, which lies outside
/// the working directory; <c>skill_editor</c>'s <c>write_file</c> / <c>edit_file</c> write those
/// files (2026-09-27, <see cref="SkillEditor.WriteFile"/>).</para>
/// </summary>
public sealed class SkillCatalog
{
    /// <summary>The one file that makes a folder a skill: exactly this name.</summary>
    public const string FileName = "SKILL.md";

    public const string Category = "Skills";

    /// <summary>Characters of a body handed to the model at most; a longer one is cut with a note.</summary>
    public const int MaxBodyChars = 48_000;

    /// <summary>Bundled files listed with a body at most.</summary>
    public const int MaxResources = 50;

    /// <summary>How deep <see cref="Resources"/> walks under the skill folder.</summary>
    public const int MaxResourceDepth = 4;

    /// <summary>
    /// Folders <see cref="Resources"/> never enters, and <c>skill_editor</c>'s file actions never write
    /// into. <c>.trash</c> since 2026-09-27: where a skill's previous file versions go under
    /// <c>File safe edits</c> (<see cref="SkillEditor.WriteFile"/>), the app's, never the skill's.
    /// </summary>
    public static readonly string[] SkippedFolders = { ".git", "node_modules", WorkingDirectory.TrashFolderName };

    private sealed record Entry(DateTime LastWriteUtc, long Length, SkillFrontmatter? Frontmatter, string? Problem);

    private readonly Func<SkillRoots> _roots;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _shadowNoted = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private IReadOnlyList<Skill> _skills = [];
    private IReadOnlyList<Skill> _shadowed = [];
    private IReadOnlyList<SkillProblem> _problems = [];
    private string _names = "";

    /// <param name="roots">Read at every scan: the profile root moves with a profile switch.</param>
    public SkillCatalog(Func<SkillRoots> roots)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
    }

    /// <summary>The roots as of the last scan (or right now, before any).</summary>
    public SkillRoots Roots => _roots();

    /// <summary>The skills the model is offered, in precedence order then by folder name within a root.</summary>
    public IReadOnlyList<Skill> Skills { get { lock (_gate) { return _skills; } } }

    /// <summary>The skills a higher scope hides, same order.</summary>
    public IReadOnlyList<Skill> Shadowed { get { lock (_gate) { return _shadowed; } } }

    /// <summary>The folders skipped by the last scan and why.</summary>
    public IReadOnlyList<SkillProblem> Problems { get { lock (_gate) { return _problems; } } }

    /// <summary>Whether the last scan read the external root.</summary>
    public bool ExternalEnabled { get; private set; }

    /// <summary>Bumped when the set of offered names changes: <c>load_skill</c> rebuilds its schema off it.</summary>
    public int Version { get; private set; }

    /// <summary>The offered skill named <paramref name="name"/> (ordinal, the frontmatter spelling), or null.</summary>
    public Skill? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string wanted = name.Trim();
        foreach (var skill in Skills)
        {
            if (string.Equals(skill.Name, wanted, StringComparison.Ordinal))
            {
                return skill;
            }
        }

        return null;
    }

    /// <summary>
    /// Rescans the roots — the profile's and the global one always, the external one when
    /// <paramref name="external"/> — and replaces <see cref="Skills"/>, <see cref="Shadowed"/>
    /// and <see cref="Problems"/>. Never throws.
    /// </summary>
    public void Scan(bool external)
    {
        var roots = _roots();
        var skills = new List<Skill>();
        var shadowed = new List<Skill>();
        var problems = new List<SkillProblem>();
        var seen = new Dictionary<string, Skill>(StringComparer.Ordinal);
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scope in new[] { SkillScope.Profile, SkillScope.Global, SkillScope.External })
        {
            if (scope == SkillScope.External && !external)
            {
                continue;
            }

            foreach (var directory in Folders(roots.Of(scope)))
            {
                string file = Path.Combine(directory, FileName);
                live.Add(file);
                var entry = Load(file);
                if (entry.Frontmatter is null)
                {
                    problems.Add(new SkillProblem(directory, entry.Problem ?? "unreadable"));
                    continue;
                }

                var skill = new Skill(entry.Frontmatter.Name, Cap(entry.Frontmatter.Description), scope, directory, Warn(entry.Frontmatter, Path.GetFileName(directory)));
                if (seen.TryGetValue(skill.Name, out var winner))
                {
                    shadowed.Add(skill with { ShadowedBy = winner.Scope });
                    if (_shadowNoted.Add(skill.FilePath))
                    {
                        DiagnosticLog.Info(Category, ShadowedNote(skill, winner));
                    }

                    continue;
                }

                seen.Add(skill.Name, skill);
                skills.Add(skill);
            }
        }

        lock (_gate)
        {
            foreach (var stale in _cache.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _cache.Remove(stale);
            }

            string names = string.Join("\n", skills.Select(s => s.Name));
            if (!string.Equals(names, _names, StringComparison.Ordinal))
            {
                _names = names;
                Version++;
            }

            _skills = skills;
            _shadowed = shadowed;
            _problems = problems;
            ExternalEnabled = external;
        }
    }

    /// <summary>The note logged once when a skill is hidden by one of the same name in a higher scope. Pinned.</summary>
    public static string ShadowedNote(Skill hidden, Skill winner) =>
        $"Skill '{hidden.Name}' in the {SkillScopes.Name(hidden.Scope)} skills is shadowed by the one in the {SkillScopes.Name(winner.Scope)} skills ({winner.Directory}).";

    /// <summary>The warning logged once when a folder's SKILL.md is skipped. Pinned.</summary>
    public static string SkippedWarning(string directory, string reason) =>
        $"Skill folder {directory} skipped: {reason}.";

    /// <summary>The note a loaded-anyway skill carries. Pinned.</summary>
    public static string NameMismatchWarning(string name, string folder) =>
        $"name '{name}' does not match the folder '{folder}'";

    public static string NameTooLongWarning(int length) =>
        $"the name is {length.ToString(CultureInfo.InvariantCulture)} characters; the limit is {SkillFrontmatter.MaxNameLength.ToString(CultureInfo.InvariantCulture)}";

    public static string DescriptionTooLongWarning(int length) =>
        $"the description is {length.ToString(CultureInfo.InvariantCulture)} characters; the first {SkillFrontmatter.MaxDescriptionLength.ToString(CultureInfo.InvariantCulture)} are shown";

    private static string? Warn(SkillFrontmatter frontmatter, string folder)
    {
        var notes = new List<string>(3);
        if (!string.Equals(frontmatter.Name, folder, StringComparison.Ordinal))
        {
            notes.Add(NameMismatchWarning(frontmatter.Name, folder));
        }

        if (frontmatter.Name.Length > SkillFrontmatter.MaxNameLength)
        {
            notes.Add(NameTooLongWarning(frontmatter.Name.Length));
        }

        if (frontmatter.Description.Length > SkillFrontmatter.MaxDescriptionLength)
        {
            notes.Add(DescriptionTooLongWarning(frontmatter.Description.Length));
        }

        return notes.Count == 0 ? null : string.Join("; ", notes);
    }

    private static string Cap(string description) =>
        description.Length <= SkillFrontmatter.MaxDescriptionLength ? description : description[..(SkillFrontmatter.MaxDescriptionLength - 1)].TrimEnd() + "…";

    /// <summary>The subfolders of <paramref name="root"/> that hold a <see cref="FileName"/>, by name; none for a missing or unreadable root.</summary>
    private static List<string> Folders(string root)
    {
        var folders = new List<string>();
        try
        {
            if (!Directory.Exists(root))
            {
                return folders;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (File.Exists(Path.Combine(directory, FileName)))
                {
                    folders.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
                }
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            DiagnosticLog.Warn(Category, $"Could not read the skills folder {root}: {ex.Message}");
        }

        folders.Sort(StringComparer.OrdinalIgnoreCase);
        return folders;
    }

    /// <summary>The cached parse of <paramref name="file"/>, re-read when it changed; a problem is warned once per version.</summary>
    private Entry Load(string file)
    {
        try
        {
            var info = new FileInfo(file);
            lock (_gate)
            {
                if (_cache.TryGetValue(file, out var held) && held.LastWriteUtc == info.LastWriteTimeUtc && held.Length == info.Length)
                {
                    return held;
                }
            }

            string text = WorkingDirectory.Decode(File.ReadAllBytes(file), out _);
            Entry entry = SkillFrontmatter.TryParse(text, out var frontmatter, out _, out var problem)
                ? new Entry(info.LastWriteTimeUtc, info.Length, frontmatter, null)
                : new Entry(info.LastWriteTimeUtc, info.Length, null, problem);
            if (entry.Problem is not null)
            {
                DiagnosticLog.Warn(Category, SkippedWarning(Path.GetDirectoryName(file)!, entry.Problem));
            }
            else
            {
                DiagnosticLog.Info(Category, $"Skill '{frontmatter!.Name}' read from {file}.");
            }

            lock (_gate)
            {
                _cache[file] = entry;
            }

            return entry;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            var entry = new Entry(default, -1, null, "could not read " + FileName + ": " + ex.Message);
            lock (_gate)
            {
                _cache.Remove(file);
            }

            DiagnosticLog.Warn(Category, SkippedWarning(Path.GetDirectoryName(file)!, entry.Problem!));
            return entry;
        }
    }

    // ── Activation ──────────────────────────────────────────────────────────

    /// <summary>The outcome of reading a skill's body or one of its files.</summary>
    public enum ReadOutcome
    {
        Ok,
        Missing,
        Outside,
        IsDirectory,
        NotText,
        TooBig,
        Unparseable,
        Failed,
    }

    /// <summary>A body or a bundled file as the model gets it: the text (cut at the cap when <see cref="Truncated"/>) or the outcome and a detail.</summary>
    public sealed record ReadResult(ReadOutcome Outcome, string Text, bool Truncated, string Detail = "");

    /// <summary>The body of <paramref name="skill"/>'s SKILL.md right now, frontmatter stripped, cut at <see cref="MaxBodyChars"/>.</summary>
    public static ReadResult ReadBody(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        try
        {
            if (!File.Exists(skill.FilePath))
            {
                return new ReadResult(ReadOutcome.Missing, "", false);
            }

            string text = WorkingDirectory.Decode(File.ReadAllBytes(skill.FilePath), out _);
            if (!SkillFrontmatter.TryParse(text, out _, out string body, out string? problem))
            {
                return new ReadResult(ReadOutcome.Unparseable, "", false, problem ?? "");
            }

            return Cut(body, MaxBodyChars);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new ReadResult(ReadOutcome.Failed, "", false, ex.Message);
        }
    }

    /// <summary>
    /// The files bundled beside the SKILL.md, as forward-slash paths relative to the skill folder,
    /// sorted, at most <see cref="MaxResources"/> deep to <see cref="MaxResourceDepth"/>;
    /// <paramref name="more"/> is true when the cap cut the list. Never throws.
    /// </summary>
    public static IReadOnlyList<string> Resources(Skill skill, out bool more)
    {
        ArgumentNullException.ThrowIfNull(skill);
        more = false;
        var files = new List<string>();
        try
        {
            Walk(skill.Directory, skill.Directory, 0, files);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            DiagnosticLog.Warn(Category, $"Could not list the files of skill '{skill.Name}': {ex.Message}");
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        if (files.Count > MaxResources)
        {
            more = true;
            files.RemoveRange(MaxResources, files.Count - MaxResources);
        }

        return files;
    }

    private static void Walk(string root, string directory, int depth, List<string> into)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            // The SKILL.md itself, and the sidecar /skills add writes (2026-09-26): the app's, never the skill's.
            if (depth == 0 && (string.Equals(Path.GetFileName(file), FileName, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(file), SkillProvenance.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            into.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        if (depth + 1 >= MaxResourceDepth)
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            if (SkippedFolders.Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            Walk(root, folder, depth + 1, into);
        }
    }

    /// <summary>
    /// One bundled text file, <paramref name="relative"/> to the skill folder and inside it by
    /// spelling (the sandbox rule; <c>..</c> out of it is <see cref="ReadOutcome.Outside"/>),
    /// cut at <see cref="WorkingDirectory.MaxReadChars"/>.
    /// </summary>
    public static ReadResult ReadResource(Skill skill, string relative)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(relative);
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(skill.Directory));
            string full = Path.GetFullPath(Path.Combine(root, relative.Trim()));
            if (full.Length <= root.Length || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || (full[root.Length] != Path.DirectorySeparatorChar && full[root.Length] != Path.AltDirectorySeparatorChar))
            {
                return new ReadResult(ReadOutcome.Outside, "", false);
            }

            if (Directory.Exists(full))
            {
                return new ReadResult(ReadOutcome.IsDirectory, "", false);
            }

            if (!File.Exists(full))
            {
                return new ReadResult(ReadOutcome.Missing, "", false);
            }

            if (new FileInfo(full).Length > WorkingDirectory.MaxReadFileBytes)
            {
                return new ReadResult(ReadOutcome.TooBig, "", false);
            }

            byte[] bytes = File.ReadAllBytes(full);
            if (WorkingDirectory.LooksBinary(bytes))
            {
                return new ReadResult(ReadOutcome.NotText, "", false);
            }

            return Cut(WorkingDirectory.Decode(bytes, out _).Replace("\r\n", "\n", StringComparison.Ordinal), WorkingDirectory.MaxReadChars);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new ReadResult(ReadOutcome.Failed, "", false, ex.Message);
        }
    }

    private static ReadResult Cut(string text, int max) =>
        text.Length <= max ? new ReadResult(ReadOutcome.Ok, text, false) : new ReadResult(ReadOutcome.Ok, text[..max], true);

    private static bool IsFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
}
