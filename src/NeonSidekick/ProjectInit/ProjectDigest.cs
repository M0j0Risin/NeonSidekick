using System.Globalization;
using System.Text;
using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Git;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.ProjectInit;

/// <summary>One project skill as the survey lists it: its name, description and the folder label it sits under.</summary>
public sealed record ProjectDigestSkill(string Name, string Description, string Folder);

/// <summary>
/// What <see cref="ProjectDigest.Build"/> reads (2026-11-23): the sandbox, its git (null for none), how the notes are treated and the file
/// whose text is the starting point (null to write fresh), the shared budget in characters, and the folder's project skills with the
/// setting that keeps NeonSidekick from reading them (null when they're read).
/// </summary>
public sealed record ProjectDigestInput(WorkingDirectory Files, GitAccess? Git, ProjectInitMode Mode, string? InPlay, int SharedBudget)
{
    public IReadOnlyList<ProjectDigestSkill> Skills { get; init; } = [];

    public string? SkillsOff { get; init; }
}

/// <summary>The survey: its text, the sections cut or left out (the <c>--log</c> line), whether the file in play had values masked, its length, and whether the repo's root is above the folder.</summary>
public sealed record ProjectDigestResult(string Text, IReadOnlyList<string> Cut, bool InPlayMasked, int InPlayLength, bool Subfolder);

/// <summary>
/// <c>/project-init</c>'s survey of the working folder (2026-11-23), gathered in C# so a small local model needs few tool calls: the file in
/// play whole (up to <see cref="ProjectFile.MaxLength"/>, outside the budget, the starting point), then, sharing <see cref="ProjectDigestInput.SharedBudget"/>
/// in this order, the tree (the index and untracked files inside a repo, a walk elsewhere that names build folders without entering them),
/// the build and project files, the README's head, other assistants' rule files, the last commit subjects and the project skills. Every read
/// goes through the sandbox (<see cref="WorkingDirectory.ReadText"/>, so a link out of the folder is refused); vendored and secret-looking files
/// are skipped; every text is masked (<see cref="ProjectSecrets"/>) and the repo's own words fenced (<see cref="UntrustedText.Fenced"/>).
/// Each section is cut to what is left and labeled. Pure over its input, but for the files it reads; the token stops it.
/// </summary>
public static class ProjectDigest
{
    /// <summary>The shared part's size when the window is unknown or large.</summary>
    public const int DefaultBudget = 8_000;

    /// <summary>Each root build file's text, at most.</summary>
    public const int ManifestChars = 1_500;

    /// <summary>The README's head, at most.</summary>
    public const int ReadmeChars = 3_000;

    /// <summary>Each other rule file, at most.</summary>
    public const int RuleChars = 3_000;

    /// <summary>The commit subjects read.</summary>
    public const int Commits = 20;

    private static readonly string[] ManifestNames =
    [
        "package.json", "pyproject.toml", "requirements.txt", "Cargo.toml", "go.mod", "pom.xml", "Makefile", "CMakeLists.txt", "Dockerfile",
        "build.ps1", "build.gradle", "build.gradle.kts", "global.json", "Directory.Build.props", "justfile", "Taskfile.yml",
    ];

    private static readonly string[] ManifestExtensions = [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj", ".sh"];

    private static readonly string[] ReadmeNames = ["README.md", "README.rst", "README.txt", "README", "readme.md"];

    private static readonly string[] RuleFiles = [".cursorrules", ".windsurfrules", ".github/copilot-instructions.md"];

    /// <summary>The shared part for a window of <paramref name="windowTokens"/> (0 unknown): the smaller of 8k and a quarter of the window.</summary>
    public static int BudgetFor(int windowTokens) =>
        windowTokens > 0 ? Math.Min(DefaultBudget, windowTokens * ConversationCompactor.CharsPerToken / 4) : DefaultBudget;

    /// <summary>The survey of <paramref name="input"/>'s folder.</summary>
    public static ProjectDigestResult Build(ProjectDigestInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var files = input.Files;
        var cut = new List<string>();
        var sb = new StringBuilder();

        // The notes in play, whole up to the prompt's cap: the starting point, outside the shared budget.
        bool masked = false;
        int inPlayLength = 0;
        sb.Append("### The notes now");
        if (input.InPlay is { } inPlay && files.ReadText(inPlay, null, null, WorkingDirectory.MaxWriteChars) is { Outcome: FileOutcome.Ok } notes)
        {
            inPlayLength = notes.Text.Length;
            string kept = notes.Text.Length > ProjectFile.MaxLength ? TextCut.Safe(notes.Text, ProjectFile.MaxLength) : notes.Text;
            string hidden = ProjectSecrets.Mask(kept);
            masked = !string.Equals(hidden, kept, StringComparison.Ordinal);
            sb.Append(": ").Append(inPlay);
            if (kept.Length < notes.Text.Length)
            {
                sb.Append(' ').Append(ProjectInitText.CutNote(kept.Length, notes.Text.Length));
                cut.Add(inPlay);
            }

            sb.Append('\n').Append(UntrustedText.Fenced(hidden)).Append("\n\n");
        }
        else
        {
            sb.Append(' ').Append(ProjectInitText.NoNotesNote).Append("\n\n");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var located = input.Git?.Locate("", out _, out _, out _) ?? GitOutcome.NoRepository;
        bool repo = located == GitOutcome.Ok;
        bool subfolder = located == GitOutcome.AboveSandbox;
        var (paths, skipped, listCut) = List(files, input.Git, repo, cancellationToken);
        int left = Math.Max(0, input.SharedBudget);

        void Section(string title, string body, int ceiling, bool fenced = false)
        {
            sb.Append("### ").Append(title);
            if (body.Length == 0)
            {
                sb.Append("\n(none)\n\n");
                return;
            }

            int room = Math.Min(ceiling, left);
            if (room < 80)
            {
                sb.Append(' ').Append(ProjectInitText.NoRoomNote).Append("\n\n");
                cut.Add(title);
                return;
            }

            string kept = body;
            if (body.Length > room)
            {
                kept = fenced ? UntrustedText.CutClosed(body, room) : TextCut.Safe(body, room);
                sb.Append(' ').Append(ProjectInitText.CutNote(kept.Length, body.Length));
                cut.Add(title);
            }

            sb.Append('\n').Append(kept).Append("\n\n");
            left -= kept.Length;
        }

        // The tree.
        string treeNote = repo ? "" : subfolder ? ProjectInitText.AboveNote + "\n" : ProjectInitText.NoRepoNote + "\n";
        Section("Files" + (listCut ? " (the listing was cut)" : ""), treeNote + Tree(paths, skipped), int.MaxValue);
        cancellationToken.ThrowIfCancellationRequested();

        // The build and project files: names at the root and one level down, the root ones' text cut; the first CI workflow's text.
        var manifests = paths.Where(p => Depth(p) <= 1 && IsManifest(p)).Order(StringComparer.Ordinal).ToList();
        var workflows = paths.Where(p => p.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase) && (p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal).ToList();
        var build = new StringBuilder();
        foreach (string name in manifests.Concat(workflows))
        {
            build.Append("- ").Append(name).Append('\n');
        }

        foreach (string name in manifests.Where(p => Depth(p) == 0).Concat(workflows.Take(1)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Read(files, name, ManifestChars) is { } text)
            {
                build.Append(name).Append(":\n").Append(text).Append('\n');
            }
        }

        Section("Build and project files", build.ToString().TrimEnd(), int.MaxValue, fenced: true);

        // The README's head.
        string? readme = ReadmeNames.FirstOrDefault(n => paths.Contains(n, StringComparer.Ordinal));
        Section("README" + (readme is null ? "" : ": " + readme), readme is not null && Read(files, readme, ReadmeChars) is { } head ? head : "", ReadmeChars + 200, fenced: true);
        cancellationToken.ThrowIfCancellationRequested();

        // Other assistants' rule files, and the project files not in play (they often hold the build commands).
        var rules = new StringBuilder();
        var ruleNames = RuleFiles.Where(r => paths.Contains(r, StringComparer.OrdinalIgnoreCase))
            .Concat(paths.Where(p => p.StartsWith(".cursor/rules/", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).Take(5))
            .Concat(ProjectFile.FileNames.Where(n => !string.Equals(n, input.InPlay, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(files.Root, n))));
        foreach (string name in ruleNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Read(files, name, RuleChars) is { } text)
            {
                rules.Append(name).Append(" (").Append(ProjectInitText.NotReadNote).Append("):\n").Append(text).Append('\n');
            }
        }

        Section("Other assistants' rule files", rules.ToString().TrimEnd(), int.MaxValue, fenced: true);

        // The last commit subjects, inside a repo with commits.
        if (repo && input.Git is { } git)
        {
            var log = git.Log("", "HEAD", Commits);
            string commits = log.Outcome == GitOutcome.Ok && log.Commits.Count > 0
                ? string.Join("\n", log.Commits.Select(c => "- " + UntrustedText.Fenced(ProjectSecrets.Mask(UntrustedText.OneLine(c.Subject)))))
                : ProjectInitText.UnbornNote;
            Section("Recent commits", commits, int.MaxValue);
        }

        // The folder's project skills, trusted or not: names and descriptions, so the notes can point to them.
        if (input.Skills.Count > 0)
        {
            string skills = (input.SkillsOff is { } off ? ProjectInitText.SkillsOffNote(off) + "\n" : "")
                + string.Join("\n", input.Skills.Select(s => "- " + UntrustedText.Fenced(UntrustedText.OneLine(s.Name)) + " (" + s.Folder + "): " + UntrustedText.Fenced(ProjectSecrets.Mask(UntrustedText.OneLine(s.Description)))));
            Section("Project skills", skills, int.MaxValue, fenced: true);
        }

        return new ProjectDigestResult(sb.ToString().TrimEnd(), cut, masked, inPlayLength, subfolder);
    }

    // A file's text through the sandbox, cut at max (by whole lines), masked and fenced; null when it can't be read as text.
    private static string? Read(WorkingDirectory files, string relative, int max)
    {
        var read = files.ReadText(relative, null, null, max);
        if (read.Outcome != FileOutcome.Ok || read.Text.Length == 0)
        {
            return null;
        }

        return UntrustedText.Fenced(ProjectSecrets.Mask(read.Text)) + (read.Truncated || read.LineCut ? " (cut)" : "");
    }

    /// <summary>
    /// The folder's files in <c>/</c> spelling, vendored and secret-looking ones left out: inside a repo the index and the untracked files, else a
    /// walk that doesn't enter <see cref="AuditScopes.WalkSkips"/>; with the skipped build folders found at the top two levels, by name.
    /// </summary>
    private static (IReadOnlyList<string> Paths, IReadOnlyList<string> Skipped, bool Cut) List(WorkingDirectory files, GitAccess? git, bool repo, CancellationToken cancellationToken)
    {
        IEnumerable<string> found;
        bool listCut;
        var skipped = new List<string>();
        if (repo && git is not null && git.TrackedFiles("", includeUntracked: true, cancellationToken) is { Outcome: GitOutcome.Ok } known)
        {
            found = known.Tracked.Concat(known.Untracked);
            listCut = known.Truncated;
        }
        else
        {
            var walked = files.ListFiles("", WorkingDirectory.MaxListEntries, name => AuditScopes.WalkSkips.Contains(name, StringComparer.OrdinalIgnoreCase), cancellationToken);
            found = walked.Files;
            listCut = walked.Cut;
            skipped.AddRange(SkippedFolders(files.Root, cancellationToken));
        }

        var paths = found.Select(p => p.Replace('\\', '/').TrimStart('/'))
            .Where(p => p.Length > 0 && !AuditScopes.LooksVendored(p) && !AuditScopes.LooksSecret(p))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return (paths, skipped, listCut);
    }

    // The WalkSkips folders at the top and one level down, named without entering them (2026-11-23: build output would fill the walk's cap).
    private static IEnumerable<string> SkippedFolders(string root, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        try
        {
            foreach (string top in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(top);
                if (name.StartsWith('.'))
                {
                    continue;
                }

                if (AuditScopes.WalkSkips.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name + "/");
                    continue;
                }

                if (new FileInfo(top).LinkTarget is not null)
                {
                    continue;
                }

                foreach (string inner in Directory.EnumerateDirectories(top))
                {
                    string innerName = Path.GetFileName(inner);
                    if (!innerName.StartsWith('.') && AuditScopes.WalkSkips.Contains(innerName, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(name + "/" + innerName + "/");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that can't be listed names nothing.
        }

        return names.Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// The tree folded to two levels: the root's files, each top folder with its file count, its subfolders with theirs and its first few
    /// files; the skipped build folders named last. Pure.
    /// </summary>
    public static string Tree(IReadOnlyList<string> paths, IReadOnlyList<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(skipped);
        var lines = new List<string>();
        lines.AddRange(paths.Where(p => Depth(p) == 0));
        foreach (var top in paths.Where(p => Depth(p) > 0).GroupBy(p => p[..p.IndexOf('/', StringComparison.Ordinal)], StringComparer.Ordinal))
        {
            lines.Add(top.Key + "/ (" + Files(top.Count()) + ")");
            var inner = top.Select(p => p[(top.Key.Length + 1)..]).ToList();
            foreach (var sub in inner.Where(p => p.Contains('/', StringComparison.Ordinal)).GroupBy(p => p[..p.IndexOf('/', StringComparison.Ordinal)], StringComparer.Ordinal))
            {
                lines.Add("  " + top.Key + "/" + sub.Key + "/ (" + Files(sub.Count()) + ")");
            }

            var direct = inner.Where(p => !p.Contains('/', StringComparison.Ordinal)).ToList();
            lines.AddRange(direct.Take(8).Select(p => "  " + top.Key + "/" + p));
            if (direct.Count > 8)
            {
                lines.Add("  (" + (direct.Count - 8).ToString(CultureInfo.InvariantCulture) + " more files in " + top.Key + "/)");
            }
        }

        lines.AddRange(skipped.Select(s => s + " (build or dependency folder, not listed)"));
        return string.Join("\n", lines);

        static string Files(int count) => count.ToString("N0", CultureInfo.InvariantCulture) + (count == 1 ? " file" : " files");
    }

    private static int Depth(string path) => path.Count(c => c == '/');

    private static bool IsManifest(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        return ManifestNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || ManifestExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }
}
