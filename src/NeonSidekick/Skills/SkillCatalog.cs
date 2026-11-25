using System.Globalization;
using System.Text;
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

    /// <summary>
    /// Characters of a bundled file <see cref="ReadResource"/> hands the model at most (2026-09-27, the user's
    /// call): its own cap, twice the file tools' <see cref="WorkingDirectory.MaxReadChars"/>, since
    /// <c>load_skill</c>'s <c>file</c> has no paging — past the cap a file's tail was unreachable (a 51,000-character
    /// mapping lost its phrases and grammar). The shared cap stays where it is for <c>read_file</c>, the web and SQL.
    /// </summary>
    public const int MaxResourceChars = 64_000;

    /// <summary>Bundled files listed with a body at most.</summary>
    public const int MaxResources = 50;

    /// <summary>How deep <see cref="Resources"/> walks under the skill folder.</summary>
    public const int MaxResourceDepth = 4;

    /// <summary>
    /// Folders <see cref="Resources"/> never enters, and <c>skill_editor</c>'s file actions never write
    /// into. <c>.trash</c> was one from 2026-09-27 until 2026-10-01, when File safe edits stopped keeping a skill's
    /// previous file versions there (the user's call).
    /// </summary>
    public static readonly string[] SkippedFolders = { ".git", "node_modules" };

    private sealed record Entry(DateTime LastWriteUtc, long Length, SkillFrontmatter? Frontmatter, string? Problem);

    private readonly Func<SkillRoots> _roots;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _shadowNoted = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private IReadOnlyList<Skill> _skills = [];
    private IReadOnlyList<Skill> _disabled = [];
    private IReadOnlyList<Skill> _all = [];
    private IReadOnlyList<Skill> _shadowed = [];
    private IReadOnlyList<SkillProblem> _problems = [];
    private IReadOnlyList<ProjectPendingSkill> _pending = [];
    private IReadOnlyList<ProjectFolder> _projectFolders = [];
    private ProjectPart? _projectPart;
    private readonly ProjectSkillHasher _hasher = new();
    private readonly HashSet<string> _changedNoted = new(StringComparer.Ordinal);
    private string _names = "";

    /// <param name="roots">Read at every scan: the profile root moves with a profile switch.</param>
    public SkillCatalog(Func<SkillRoots> roots)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
    }

    /// <summary>The roots right now: read live at each call, so a profile switch or a flip of <c>Global skills</c> shows at once (the last scan's own value is <see cref="GlobalEnabled"/>).</summary>
    public SkillRoots Roots => _roots();

    /// <summary>The skills the model is offered, in precedence order then by folder name within a root; a disabled one never (2026-10-09, <see cref="Disabled"/>).</summary>
    public IReadOnlyList<Skill> Skills { get { lock (_gate) { return _skills; } } }

    /// <summary>
    /// The skills the user switched off (2026-10-09, <see cref="SkillDisable"/>), same order: they hold their names (a lower root's twin
    /// stays <see cref="Shadowed"/>) but are not offered. The <c>/skills</c> pane lists them, a reflection sees them, the records keep them.
    /// </summary>
    public IReadOnlyList<Skill> Disabled { get { lock (_gate) { return _disabled; } } }

    /// <summary>Every skill that holds its name, offered or disabled, in <see cref="Skills"/>' order (2026-10-09): the pane's list and a reflection's.</summary>
    public IReadOnlyList<Skill> All { get { lock (_gate) { return _all; } } }

    /// <summary>The skills a higher scope hides, same order.</summary>
    public IReadOnlyList<Skill> Shadowed { get { lock (_gate) { return _shadowed; } } }

    /// <summary>The folders skipped by the last scan and why.</summary>
    public IReadOnlyList<SkillProblem> Problems { get { lock (_gate) { return _problems; } } }

    /// <summary>
    /// The project skills listed but not offered (2026-11-23, feature idea #16), each with its reason: not trusted, over the cap, linked outside the
    /// repo, only started by hand in Claude Code, a name too long or unprintable. Read by the <c>/skills</c> pane, the trust notice and
    /// <c>/project-init</c>'s digest; never by the prompt, <c>load_skill</c> or a <c>#</c> completion.
    /// </summary>
    public IReadOnlyList<ProjectPendingSkill> ProjectPending { get { lock (_gate) { return _pending; } } }

    /// <summary>The walk's folders that hold skills, nearest first, with their trust as the last scan read it.</summary>
    public IReadOnlyList<ProjectFolder> ProjectFolders { get { lock (_gate) { return _projectFolders; } } }

    /// <summary>
    /// The trusted project skills folders (their <c>.agents/skills</c> and <c>.claude/skills</c>): a <c>read_file</c> of one outside the working folder gets
    /// the <c>load_skill</c> wording (<see cref="WorkingDirectory.ProjectSkillFolders"/>). From the kept part, no walk of its own; empty while off.
    /// </summary>
    public IReadOnlyList<string> TrustedProjectFolders
    {
        get
        {
            lock (_gate)
            {
                return _projectFolders.Where(f => f.Trust == ProjectFolderTrust.Trusted).SelectMany(f => f.SkillsFolders).ToList();
            }
        }
    }

    /// <summary>The not-offered project skill named <paramref name="name"/> (ordinal), or null: <c>load_skill</c> answers it in its own words.</summary>
    public ProjectPendingSkill? FindPending(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string wanted = name.Trim();
        return ProjectPending.FirstOrDefault(p => string.Equals(p.Skill.Name, wanted, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether <paramref name="skill"/> may be handed over now: always for the user's own scopes; for a project skill, only while its files still hash
    /// to what was trusted (a <c>git pull</c> between the scan and the load would otherwise hand over text nobody trusted). The bytes are read again,
    /// never the stamp cache (2026-11-23, review #40): a same-size edit with its write time put back would pass a stamp check.
    /// </summary>
    public bool StillTrusted(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return skill.Scope != SkillScope.Project
            || (skill.TrustedHash is { } trusted && string.Equals(_hasher.SkillHash(skill.Directory, fresh: true), trusted, StringComparison.Ordinal));
    }

    /// <summary>One project skill's hash now (the trust question's per-skill compare), from the stamp cache.</summary>
    public string ProjectSkillHash(string directory) => _hasher.SkillHash(directory);

    /// <summary>The hasher's count of computed hashes (a test proves an untouched folder isn't re-hashed).</summary>
    internal int HashesComputed => _hasher.Computed;

    /// <summary>
    /// <paramref name="level"/>'s skills as a trust question reads them right now: the fingerprint, each skill's hash and the <c>SKILL.md</c> texts to keep.
    /// The walk's rules (links, the size bound) as a scan's. Never throws.
    /// </summary>
    public ProjectFolderState ReadProjectFolder(string level, string? bound)
    {
        ArgumentNullException.ThrowIfNull(level);
        var (keys, hashes, copies) = ReadLevel(level, bound ?? level);
        return new ProjectFolderState(ProjectSkillHasher.FolderFingerprint(keys.Select(k => new KeyValuePair<string, string?>(k, hashes.GetValueOrDefault(k)))), hashes, copies, keys);
    }

    /// <summary>Whether the last scan read the external root.</summary>
    public bool ExternalEnabled { get; private set; }

    /// <summary>
    /// Whether the last scan read the global root (2026-10-13, the profile's <c>Global skills</c> switch): the value the scan itself
    /// used, so the prompt's editor sentence never disagrees with the list it heads, whatever a flip did since.
    /// </summary>
    public bool GlobalEnabled { get; private set; } = true;

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
    /// Rescans the roots — the profile's always, the global one while the roots read it (<see cref="SkillRoots.GlobalOn"/>,
    /// 2026-10-13), the external one when <paramref name="external"/> — and replaces <see cref="Skills"/>, <see cref="Shadowed"/>
    /// and <see cref="Problems"/>. Never throws.
    /// </summary>
    public void Scan(bool external) => Scan(new SkillScan(external));

    /// <summary>
    /// Rescans as <paramref name="scan"/> says (2026-11-23): the three roots as <see cref="Scan(bool)"/> does, then a repo's project skills when
    /// <see cref="SkillScan.Project"/> is set, ranked last (yours win a clash), nearest level first and <c>.agents</c> before <c>.claude</c>. A trusted
    /// one under the 50 cap is offered; every other goes to <see cref="ProjectPending"/> without taking its name, so a stranger's subfolder can't hide a
    /// trusted skill by naming its own the same. Never throws.
    /// </summary>
    public void Scan(SkillScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        bool external = scan.External;
        var roots = _roots();
        var all = new List<Skill>();
        var shadowed = new List<Skill>();
        var problems = new List<SkillProblem>();
        var seen = new Dictionary<string, Skill>(StringComparer.Ordinal);
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scope in new[] { SkillScope.Profile, SkillScope.Global, SkillScope.External })
        {
            if ((scope == SkillScope.External && !external) || !roots.Reads(scope))
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

                var skill = new Skill(entry.Frontmatter.Name, Cap(entry.Frontmatter.Description), scope, directory, Warn(entry.Frontmatter, Path.GetFileName(directory)))
                {
                    Locked = scope != SkillScope.External && SkillLock.IsLocked(directory),
                    Disabled = scope != SkillScope.External && SkillDisable.IsDisabled(directory),
                };
                if (seen.TryGetValue(skill.Name, out var winner))
                {
                    shadowed.Add(skill with { ShadowedBy = winner.Scope });
                    if (NoteOnce(_shadowNoted, skill.FilePath))
                    {
                        DiagnosticLog.Info(Category, ShadowedNote(skill, winner));
                    }

                    continue;
                }

                // A disabled skill still holds its name (2026-10-09, the user's pick): seen, so a lower twin stays shadowed.
                seen.Add(skill.Name, skill);
                all.Add(skill);
            }
        }

        var pending = new List<ProjectPendingSkill>();
        var part = scan.Project is { } source ? ProjectPartFor(source, scan.RefreshProject) : null;
        if (part is not null)
        {
            problems.AddRange(part.Problems);

            // The project SKILL.md files this part loaded stay in the parse cache (2026-11-23, review #46): pruned, each scan read and logged them again.
            live.UnionWith(part.Files);
            int offered = 0;
            foreach (var (candidate, reason) in part.Candidates)
            {
                if (reason is { } why)
                {
                    pending.Add(new ProjectPendingSkill(candidate, why));
                    continue;
                }

                if (seen.TryGetValue(candidate.Name, out var winner))
                {
                    shadowed.Add(candidate with { ShadowedBy = winner.Scope, ShadowedByDirectory = winner.Directory });
                    if (NoteOnce(_shadowNoted, candidate.FilePath))
                    {
                        DiagnosticLog.Info(Category, ShadowedNote(candidate, winner));
                    }

                    continue;
                }

                if (offered >= ProjectSkillTrust.ProjectSkillCap)
                {
                    pending.Add(new ProjectPendingSkill(candidate, ProjectPendingReason.OverCap));
                    continue;
                }

                offered++;
                seen.Add(candidate.Name, candidate);
                all.Add(candidate);
            }
        }

        var skills = all.Where(s => !s.Disabled).ToList();

        lock (_gate)
        {
            foreach (var stale in _cache.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _cache.Remove(stale);
            }

            _hasher.Retain(part?.Hashed ?? []);
            string names = string.Join("\n", skills.Select(s => s.Name));
            if (!string.Equals(names, _names, StringComparison.Ordinal))
            {
                _names = names;
                Version++;
            }

            _skills = skills;
            _disabled = all.Where(s => s.Disabled).ToList();
            _all = all;
            _shadowed = shadowed;
            _problems = problems;
            _pending = pending;
            _projectFolders = part?.Folders ?? [];
            _projectPart = part;
            ExternalEnabled = external;
            GlobalEnabled = roots.GlobalOn;
        }
    }

    // ── Project skills (2026-11-23, feature idea #16) ──────────────────────────

    /// <summary>
    /// The project part of a scan: the folder it was read from, each candidate with its reason not to offer it (null: trusted), the problems, the
    /// folders, the <c>SKILL.md</c> files it parsed (kept in the parse cache, review #46) and the skill folders it hashed (kept in the hasher's, #54).
    /// </summary>
    private sealed record ProjectPart(string Root, IReadOnlyList<(Skill Skill, ProjectPendingReason? Reason)> Candidates, IReadOnlyList<SkillProblem> Problems, IReadOnlyList<ProjectFolder> Folders,
        IReadOnlyList<string> Files, IReadOnlyList<string> Hashed);

    /// <summary>The problem for a project <c>SKILL.md</c> over the read bound. Pinned.</summary>
    public static string TooLargeProblem(long bytes) =>
        $"{FileName} is {(bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture)} MB; too large to read";

    // The kept part when the scan may reuse it and its folder is still the working folder; otherwise read again.
    private ProjectPart ProjectPartFor(ProjectSkillSource source, bool refresh)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.WorkingFolder()));
        ProjectPart? kept;
        lock (_gate)
        {
            kept = _projectPart;
        }

        if (!refresh && kept is not null && string.Equals(kept.Root, root, CaseRule.Comparison(root)))
        {
            return kept;
        }

        return ReadProjectPart(source, root);
    }

    private ProjectPart ReadProjectPart(ProjectSkillSource source, string root)
    {
        var candidates = new List<(Skill, ProjectPendingReason?)>();
        var problems = new List<SkillProblem>();
        var folders = new List<ProjectFolder>();
        var files = new List<string>();
        var hashed = new List<string>();
        string bound = ProjectSkillWalk.RepoRoot(root, source.UserHome, source.AppHome) ?? root;
        foreach (var group in ProjectSkillWalk.Levels(root, source.UserHome, source.AppHome).GroupBy(l => l.Level))
        {
            string level = group.Key;
            var found = new List<(ProjectSkillLevel Kind, string Directory, string Key)>();
            var skillsFolders = new List<string>();
            foreach (var kind in group)
            {
                if (!Directory.Exists(kind.SkillsFolder))
                {
                    continue;
                }

                skillsFolders.Add(kind.SkillsFolder);
                foreach (string directory in Folders(kind.SkillsFolder))
                {
                    found.Add((kind, directory, kind.Kind + "/" + Path.GetFileName(directory)));
                }
            }

            if (found.Count == 0)
            {
                continue;
            }

            var (keys, hashes, _) = ReadLevel(level, bound, found, copies: false);
            hashed.AddRange(found.Where(f => hashes.ContainsKey(f.Key)).Select(f => f.Directory));
            string fingerprint = ProjectSkillHasher.FolderFingerprint(keys.Select(k => new KeyValuePair<string, string?>(k, hashes.GetValueOrDefault(k))));
            var held = source.Trust.Trusted(level);
            var trust = held is null ? ProjectFolderTrust.Untrusted
                : string.Equals(held.Fingerprint, fingerprint, StringComparison.Ordinal) ? ProjectFolderTrust.Trusted
                : ProjectFolderTrust.Changed;
            if (trust == ProjectFolderTrust.Changed && NoteOnce(_changedNoted, level + "\0" + fingerprint))
            {
                DiagnosticLog.Info(Category, $"Project skills in {level} changed since they were trusted; not offered until trusted again.");
            }

            var disabled = source.Trust.DisabledIn(level);
            folders.Add(new ProjectFolder(level, trust, fingerprint, keys, skillsFolders));
            foreach (var (kind, directory, key) in found)
            {
                string file = Path.Combine(directory, FileName);
                if (LinkedOutside(level, kind, directory, bound))
                {
                    var linked = new Skill(Path.GetFileName(directory), "", SkillScope.Project, directory) { TrustFolder = level, ProjectKey = key, ClaudeRules = kind.Claude };
                    candidates.Add((linked, ProjectPendingReason.LinkedOutside));
                    continue;
                }

                // A SKILL.md that is itself a link, even one inside the repo (2026-11-23, review #73): listed, never read or offered. Its size below
                // would be the link's own, and the trust would follow a link a later edit can re-point.
                if (WorkingDirectory.RealLinkTarget(file) is not null)
                {
                    var linkedFile = new Skill(Path.GetFileName(directory), "", SkillScope.Project, directory) { TrustFolder = level, ProjectKey = key, ClaudeRules = kind.Claude };
                    candidates.Add((linkedFile, ProjectPendingReason.LinkedFile));
                    continue;
                }

                long length = SafeLength(file);
                if (length > ProjectSkillTrust.MaxSkillFileBytes)
                {
                    problems.Add(new SkillProblem(directory, TooLargeProblem(length)));
                    continue;
                }

                files.Add(file);
                var entry = Load(file, kind.Claude ? Path.GetFileName(directory) : null);
                if (entry.Frontmatter is null)
                {
                    problems.Add(new SkillProblem(directory, entry.Problem ?? "unreadable"));
                    continue;
                }

                var frontmatter = entry.Frontmatter;
                bool trusted = trust == ProjectFolderTrust.Trusted;
                var skill = new Skill(frontmatter.Name, Cap(frontmatter.Description), SkillScope.Project, directory, Warn(frontmatter, Path.GetFileName(directory)))
                {
                    Disabled = disabled.Contains(frontmatter.Name),
                    TrustFolder = level,
                    ProjectKey = key,
                    ClaudeRules = kind.Claude,
                    TrustedHash = trusted ? hashes.GetValueOrDefault(key) : null,
                    Acting = frontmatter.HasActingKeys,
                };
                ProjectPendingReason? reason = !IsBoundedName(frontmatter.Name) ? ProjectPendingReason.BadName
                    : kind.Claude && frontmatter.ManualOnly ? ProjectPendingReason.ManualOnly
                    : !trusted ? ProjectPendingReason.NotTrusted
                    : skill.TrustedHash is null ? ProjectPendingReason.PastFolderCap   // past the level's 50th folder: never hashed, so never trusted (review #70)
                    : null;
                candidates.Add((skill, reason));
            }
        }

        return new ProjectPart(root, candidates, problems, folders, files, hashed);
    }

    /// <summary>
    /// Whether a project skill's name may be offered: at most 64 characters and no control, format (a right-to-left override, a zero-width joiner) or
    /// line or paragraph separator character (2026-11-23, review #45): a stranger's 5,000 would widen every row, and an invisible one would pass for
    /// another skill's name.
    /// </summary>
    public static bool IsBoundedName(string name) =>
        name.Length <= SkillFrontmatter.MaxNameLength && !name.EnumerateRunes().Any(IsUnprintable);

    /// <summary>
    /// A control, format, line separator or paragraph separator character: one a skill's name never shows. Judged by rune (2026-11-24, review #2):
    /// a supplementary format character (the U+E00xx tags) read as two surrogates and passed, and a broken pair's replacement rune counts as unprintable.
    /// </summary>
    public static bool IsUnprintable(Rune rune) =>
        rune == Rune.ReplacementChar || Rune.IsControl(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    // A note set's Add under the gate (2026-11-23, review #53): two scans at once (the screen's and a turn's) shared the set unlocked.
    private bool NoteOnce(HashSet<string> noted, string key)
    {
        lock (_gate)
        {
            return noted.Add(key);
        }
    }

    // The level's skill folders in order (.agents before .claude, each by name), each one's hash for the first 50 (null past them), and the SKILL.md copies.
    private (IReadOnlyList<string> Keys, Dictionary<string, string> Hashes, Dictionary<string, string> Copies) ReadLevel(string level, string bound)
    {
        var found = new List<(ProjectSkillLevel Kind, string Directory, string Key)>();
        foreach (var kind in ProjectSkillWalk.Levels(level, null, null).Where(l => string.Equals(l.Level, Path.TrimEndingDirectorySeparator(Path.GetFullPath(level)), StringComparison.Ordinal)))
        {
            if (Directory.Exists(kind.SkillsFolder))
            {
                found.AddRange(Folders(kind.SkillsFolder).Select(d => (kind, d, kind.Kind + "/" + Path.GetFileName(d))));
            }
        }

        return ReadLevel(level, bound, found, copies: true);
    }

    private (IReadOnlyList<string> Keys, Dictionary<string, string> Hashes, Dictionary<string, string> Copies) ReadLevel(
        string level, string bound, List<(ProjectSkillLevel Kind, string Directory, string Key)> found, bool copies)
    {
        var keys = found.Select(f => f.Key).ToList();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (kind, directory, key) in found.Take(ProjectSkillTrust.ProjectSkillCap))
        {
            if (LinkedOutside(level, kind, directory, bound))
            {
                // Never followed: hashed by where it points, so a re-pointed link changes the fingerprint.
                hashes[key] = ProjectSkillHasher.FolderFingerprint([new(key, "link:" + (WorkingDirectory.RealLinkTarget(directory) ?? WorkingDirectory.RealLinkTarget(Path.Combine(directory, FileName)) ?? ""))]);
                continue;
            }

            hashes[key] = _hasher.SkillHash(directory);
            if (copies && SafeLength(Path.Combine(directory, FileName)) is var length and >= 0 and <= ProjectSkillTrust.MaxSkillFileBytes)
            {
                try
                {
                    texts[key] = WorkingDirectory.Decode(File.ReadAllBytes(Path.Combine(directory, FileName)), out _);
                }
                catch (Exception ex) when (IsFileFailure(ex))
                {
                    DiagnosticLog.Debug(Category, $"Could not copy {directory}'s {FileName}: {ex.Message}");
                }
            }
        }

        return (keys, hashes, texts);
    }

    // A link on the way to the skill (the .agents/.claude folder, its skills folder, the skill's folder, its SKILL.md) that leads outside the repo
    // (the working folder outside one). Judged on where each link points, not its attribute alone; one whose target can't be worked out counts as outside.
    private static bool LinkedOutside(string level, ProjectSkillLevel kind, string directory, string bound)
    {
        string kindFolder = Path.GetDirectoryName(kind.SkillsFolder)!;
        _ = level;
        return new[] { kindFolder, kind.SkillsFolder, directory, Path.Combine(directory, FileName) }.Any(path => LinksOutside(path, bound));
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a link leading outside <paramref name="bound"/> (2026-11-24, review #28, lifted for the move and the delete):
    /// judged on where it points, and one whose target can't be worked out counts as outside. A missing path or a plain folder is no link.
    /// </summary>
    internal static bool LinksOutside(string path, string bound)
    {
        if (WorkingDirectory.RealLinkTarget(path) is not { } target)
        {
            return false;
        }

        if (target.Length == 0)
        {
            return true;
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target)));
        return !WorkingDirectory.IsInside(bound, full);
    }

    private static long SafeLength(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? info.Length : -1;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return -1;
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

    // Never through a surrogate pair (2026-11-24, review #57): an emoji across the cut left half of it.
    internal static string Cap(string description) =>
        description.Length <= SkillFrontmatter.MaxDescriptionLength ? description : Files.TextCut.Safe(description, SkillFrontmatter.MaxDescriptionLength - 1).TrimEnd() + "…";

    /// <summary>The subfolders of <paramref name="root"/> that hold a <see cref="FileName"/>, by name; none for a missing or unreadable root.</summary>
    internal static List<string> Folders(string root)
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
    private Entry Load(string file, string? claudeFolder = null)
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
            Entry entry = (claudeFolder is null
                    ? SkillFrontmatter.TryParse(text, out var frontmatter, out _, out var problem)
                    : SkillFrontmatter.TryParseClaude(text, claudeFolder, out frontmatter, out _, out problem))
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

        /// <summary>A project skill's file that isn't one of its listed (trusted) files (2026-11-23).</summary>
        NotListed,
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

            if (skill.Scope == SkillScope.Project && new FileInfo(skill.FilePath).Length > ProjectSkillTrust.MaxSkillFileBytes)
            {
                return new ReadResult(ReadOutcome.TooBig, "", false);
            }

            string text = WorkingDirectory.Decode(File.ReadAllBytes(skill.FilePath), out _);
            bool parsed = skill.ClaudeRules
                ? SkillFrontmatter.TryParseClaude(text, skill.FolderName, out _, out string body, out string? problem)
                : SkillFrontmatter.TryParse(text, out _, out body, out problem);
            if (!parsed)
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
        if (skill.Scope == SkillScope.Project)
        {
            return ProjectResources(skill.Directory, out more);
        }

        more = false;
        var files = new List<string>();
        try
        {
            Walk(skill.Directory, skill.Directory, 0, files, skipLinks: false);
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

    /// <summary>
    /// A project skill's bundled files (2026-11-23): as <see cref="Resources"/> lists them, but never through a link (a repo's <c>notes.md</c> linked to
    /// <c>~/.ssh/id_rsa</c> would be handed over), the first <see cref="MaxResources"/>. What its trust hashes and what <see cref="ReadResource"/> may read.
    /// </summary>
    public static IReadOnlyList<string> ProjectResources(string directory, out bool more)
    {
        ArgumentNullException.ThrowIfNull(directory);
        more = false;
        var files = new List<string>();
        try
        {
            Walk(directory, directory, 0, files, skipLinks: true);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            DiagnosticLog.Warn(Category, $"Could not list the files of {directory}: {ex.Message}");
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        if (files.Count > MaxResources)
        {
            more = true;
            files.RemoveRange(MaxResources, files.Count - MaxResources);
        }

        return files;
    }

    private static void Walk(string root, string directory, int depth, List<string> into, bool skipLinks)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (skipLinks && WorkingDirectory.RealLinkTarget(file) is not null)
            {
                continue;
            }

            // The SKILL.md itself, and the sidecar /skills add writes (2026-09-26): the app's, never the skill's.
            // The lock (2026-10-07) and the disable (2026-10-09) are the app's too.
            if (depth == 0 && (string.Equals(Path.GetFileName(file), FileName, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(file), SkillProvenance.FileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(file), SkillLock.FileName, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(file), SkillDisable.FileName, StringComparison.OrdinalIgnoreCase)))
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
            if (SkippedFolders.Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase)
                || (skipLinks && WorkingDirectory.RealLinkTarget(folder) is not null))
            {
                continue;
            }

            Walk(root, folder, depth + 1, into, skipLinks);
        }
    }

    /// <summary>
    /// One bundled text file, <paramref name="relative"/> to the skill folder and inside it by
    /// spelling (the sandbox rule; <c>..</c> out of it is <see cref="ReadOutcome.Outside"/>),
    /// cut at <see cref="MaxResourceChars"/> (<see cref="WorkingDirectory.MaxReadChars"/> until 2026-09-27).
    /// </summary>
    public static ReadResult ReadResource(Skill skill, string relative)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(relative);
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(skill.Directory));
            // Off Windows a backslash is a file-name character, so a skill written on Windows (`scripts\run.py`) named one file
            // (2026-10-06, the macOS build); it is read as the separator, as Windows reads it. A `..\` that leaves the folder is still Outside.
            string spelled = OperatingSystem.IsWindows() ? relative.Trim() : relative.Trim().Replace('\\', '/');
            string full = Path.GetFullPath(Path.Combine(root, spelled));
            if (full.Length <= root.Length || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || (full[root.Length] != Path.DirectorySeparatorChar && full[root.Length] != Path.AltDirectorySeparatorChar))
            {
                return new ReadResult(ReadOutcome.Outside, "", false);
            }

            if (Directory.Exists(full))
            {
                return new ReadResult(ReadOutcome.IsDirectory, "", false);
            }

            if (skill.Scope == SkillScope.Project)
            {
                // Only a listed file (what the trust hashed), and nothing through a link on the way (2026-11-23).
                string asListed = Path.GetRelativePath(root, full).Replace('\\', '/');
                // By the volume's own case rule (2026-11-23, review #84): on a case-sensitive one REF.md is another file than the listed ref.md.
                if (!ProjectResources(skill.Directory, out _).Contains(asListed, CaseRule.Comparer(root)))
                {
                    return new ReadResult(ReadOutcome.NotListed, "", false);
                }

                for (string? step = full; step is not null && step.Length > root.Length; step = Path.GetDirectoryName(step))
                {
                    if (WorkingDirectory.RealLinkTarget(step) is not null)
                    {
                        return new ReadResult(ReadOutcome.Outside, "", false);
                    }
                }
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

            return Cut(WorkingDirectory.Decode(bytes, out _).Replace("\r\n", "\n", StringComparison.Ordinal), MaxResourceChars);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new ReadResult(ReadOutcome.Failed, "", false, ex.Message);
        }
    }

    private static ReadResult Cut(string text, int max) =>
        text.Length <= max ? new ReadResult(ReadOutcome.Ok, text, false) : new ReadResult(ReadOutcome.Ok, text[..max], true);

    // A slicing bug is no file failure (2026-10-22, review K18, WorkingDirectory.IsFileFailure's rule).
    private static bool IsFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException || (ex is ArgumentException && ex is not (ArgumentOutOfRangeException or ArgumentNullException));
}
