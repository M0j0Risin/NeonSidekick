using System.Globalization;
using System.Text;

namespace NeonSidekick.Git;

/// <summary>
/// The sentences the git tools answer with: pure statics, every string pinned, the
/// <see cref="Web.WebText"/> pattern. A result is what the model reads and, cut to its first line
/// (<see cref="Note"/>), the one dim <c>🛠️</c> line the transcript shows, so every multi-line answer opens
/// with a header that stands on its own. Every error starts with <c>Error:</c>. Invariant culture;
/// a moment is shown in the caller's zone as <c>yyyy-MM-dd HH:mm</c>.
/// </summary>
public static class GitText
{
    public const string Legend = "A added · M modified · D deleted · R renamed · T type changed · ? untracked · U conflict";
    public const string NoPaths = "Error: give the paths to act on (\".\" for everything changed under path)";
    public const string NoMessage = "Error: give the commit message";
    public const string NoName = "Error: give the branch's name";
    public const string NoNewName = "Error: give the branch's new name";
    public const string NoReference = "Error: give the commit to show (a sha, a branch, a tag, HEAD~1)";
    public const string NoBlamePath = "Error: give the file to blame";
    public const string NothingStaged = "Error: nothing is staged; stage the changes with gitlib_stage first";
    public const string NothingToAmend = "Error: there is no commit to amend yet";
    public const string NothingToStash = "Error: nothing to stash: the working tree is clean";
    public const string NoCommits = "Error: the repository has no commits yet";
    public const string NoIdentity = "Error: git has no user.name / user.email for commits; ask the user to run git config --global user.name \"…\" and git config --global user.email \"…\", then try again";
    public const string CheckoutConflict = "Error: that would overwrite local changes; commit or stash them first";
    public const string Conflicts = "Error: the index has unmerged conflicts; the user resolves them first";
    public const string BadDiffArguments = "Error: give ref alone, from with to, or staged — not a mix";

    public static string NoRepository(string root) => $"Error: '{root}' is not inside a git repository; /cwd into one, or ask the user to git init it";
    public static string AboveSandbox(string workTree) => $"Error: the repository's root '{workTree}' is above the working directory, so the git tools cannot reach it; the user can /cwd to the repository's root";
    public static string Bare(string path) => $"Error: '{path}' is a bare repository (no working tree)";
    public static string NotOwned(string detail) => $"Error: git refuses the repository ({detail}); the user can allow it with git config --global --add safe.directory <path>";
    public static string OutsideRoot(string path) => $"Error: '{path}' is outside the working directory";
    public static string NotInRepository(string path) => $"Error: '{path}' is not inside the repository";
    public static string Missing(string path) => $"Error: '{path}' is not there";
    public static string RefNotFound(string reference) => $"Error: '{reference}' names no commit, branch or tag";
    public static string RefAmbiguous(string reference) => $"Error: '{reference}' is ambiguous; give a longer sha or the full name";
    public static string NoStash(string index) => $"Error: there is no stash@{{{index}}}";
    public static string NameConflict(string name) => $"Error: '{name}' exists already";
    public static string CannotDeleteCurrentBranch(string name) => $"Error: '{name}' is the branch checked out; switch to another first";
    public static string CannotStageRoot(string path) => $"Error: '{path}' is the repository itself; give \".\" to stage everything under path";
    public static string Binary(string path) => $"Error: '{path}' is binary";
    public static string TooBig(string path) => $"Error: '{path}' is too big to read as text";
    public static string TooManyPaths(int max) => $"Error: at most {N(max)} paths in one call";
    public static string BadAction(string action) => $"Error: '{action}' is not an action here";
    public static string BadKind(string kind) => $"Error: '{kind}' is not a kind here";
    public static string BadLogCount(int min, int max) => $"Error: max_commits must be {N(min)} to {N(max)}";
    public static string BadDiffLines(int min, int max) => $"Error: max_lines must be {N(min)} to {N(max)}";
    public static string BadLine(string argument) => $"Error: {argument} must be 1 or more";
    public static string Failed(string detail) => $"Error: git refused ({detail})";
    public static string CreatedButNotSwitched(string name, string sha, string why) => $"Created branch {name} at {sha}, but the switch failed: {why}";

    /// <summary>The transcript's one-line note for a result: its first line.</summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n');
        return newline < 0 ? result : result[..newline];
    }

    /// <summary>The sentence for a refused outcome; the detail names the path, the ref or the message it needs.</summary>
    public static string Error(GitOutcome outcome, string detail) => outcome switch
    {
        GitOutcome.OutsideRoot => OutsideRoot(detail),
        GitOutcome.NoRepository => NoRepository(detail),
        GitOutcome.AboveSandbox => AboveSandbox(detail),
        GitOutcome.Bare => Bare(detail),
        GitOutcome.NotOwned => NotOwned(detail),
        GitOutcome.NotInRepository => NotInRepository(detail),
        GitOutcome.Missing => Missing(detail),
        GitOutcome.RefNotFound => RefNotFound(detail),
        GitOutcome.RefAmbiguous => RefAmbiguous(detail),
        GitOutcome.Unborn => NoCommits,
        GitOutcome.NothingStaged => NothingStaged,
        GitOutcome.NothingToAmend => NothingToAmend,
        GitOutcome.NothingToStash => NothingToStash,
        GitOutcome.NoStash => NoStash(detail),
        GitOutcome.Conflicts => Conflicts,
        GitOutcome.CheckoutConflict => CheckoutConflict,
        GitOutcome.NameConflict => NameConflict(detail),
        GitOutcome.CurrentBranch => CannotDeleteCurrentBranch(detail),
        GitOutcome.NoIdentity => NoIdentity,
        GitOutcome.Binary => Binary(detail),
        GitOutcome.TooBig => TooBig(detail),
        GitOutcome.TooManyPaths => TooManyPaths(GitAccess.MaxPathsPerCall),
        GitOutcome.BadArguments => CannotStageRoot(detail),
        _ => Failed(detail),
    };

    // ---- status ----

    /// <summary><c>On branch main (ahead 2 of origin/main): 1 staged, 2 modified, 1 untracked</c>, then the sections.</summary>
    public static string Status(GitStatusReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.Append(report.Detached ? "Detached at " + report.HeadShort : "On branch " + report.Branch);
        var notes = new List<string>();
        if (report.Unborn)
        {
            notes.Add("no commits yet");
        }

        if (report.Upstream is { } upstream)
        {
            if (report.Ahead is > 0 && report.Behind is > 0)
            {
                notes.Add($"ahead {N(report.Ahead.Value)}, behind {N(report.Behind.Value)} of {upstream}");
            }
            else if (report.Ahead is > 0)
            {
                notes.Add($"ahead {N(report.Ahead.Value)} of {upstream}");
            }
            else if (report.Behind is > 0)
            {
                notes.Add($"behind {N(report.Behind.Value)} of {upstream}");
            }
            else
            {
                notes.Add($"up to date with {upstream}");
            }
        }

        if (notes.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", notes)).Append(')');
        }

        sb.Append(": ");
        if (report.Clean)
        {
            sb.Append("clean");
            return sb.ToString();
        }

        var counts = new List<string>();
        if (report.Conflicts.Count > 0)
        {
            counts.Add(Count(report.Conflicts.Count, "conflict"));
        }

        if (report.Staged.Count > 0)
        {
            counts.Add(N(report.Staged.Count) + " staged");
        }

        if (report.Unstaged.Count > 0)
        {
            counts.Add(N(report.Unstaged.Count) + " modified");
        }

        if (report.Untracked.Count > 0)
        {
            counts.Add(N(report.Untracked.Count) + " untracked");
        }

        sb.Append(string.Join(", ", counts));
        if (report.Truncated)
        {
            sb.Append(" (the first ").Append(N(GitAccess.MaxStatusEntries)).Append(" entries)");
        }

        Section(sb, "conflicts", report.Conflicts);
        Section(sb, "staged", report.Staged);
        Section(sb, "unstaged", report.Unstaged);
        Section(sb, "untracked", report.Untracked);
        sb.Append('\n').Append(Legend);
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, IReadOnlyList<GitEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        sb.Append('\n').Append(title).Append(':');
        foreach (var entry in entries)
        {
            sb.Append('\n').Append(entry.Code).Append("  ").Append(entry.OldPath is { } old ? old + " → " + entry.Path : entry.Path);
        }
    }

    // ---- log ----

    /// <summary><c>20 commits on main, newest first:</c> / <c>12 commits touching src\a.cs on main, newest first:</c> then one row per commit.</summary>
    public static string Log(GitLogReport report, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(zone);
        var sb = new StringBuilder();
        if (report.Commits.Count == 0)
        {
            sb.Append(report.Path is { } none ? $"No commits touch {none} on {report.Reference}" : $"No commits on {report.Reference}");
            return sb.ToString();
        }

        sb.Append(Count(report.Commits.Count, "commit"));
        if (report.Path is { } path)
        {
            sb.Append(" touching ").Append(path);
        }

        sb.Append(" on ").Append(report.Reference).Append(", newest first");
        if (report.Truncated)
        {
            sb.Append(" (more before them)");
        }

        sb.Append(':');
        foreach (var commit in report.Commits)
        {
            sb.Append('\n').Append(CommitRow(commit, zone));
        }

        return sb.ToString();
    }

    /// <summary><c>abc1234 2026-09-20 14:05 Chris: subject</c>.</summary>
    public static string CommitRow(GitCommitInfo commit, TimeZoneInfo zone) =>
        $"{commit.Short} {When(commit.When, zone)} {commit.Author}: {commit.Subject}";

    // ---- show ----

    /// <summary>A commit's header, message and files; a file's text; a folder's entries.</summary>
    public static string Show(GitShowReport report, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(zone);
        var commit = report.Commit!;
        var sb = new StringBuilder();
        if (report.Path is null)
        {
            sb.Append("Commit ").Append(commit.Short).Append(" by ").Append(commit.Author).Append(" at ").Append(When(commit.When, zone)).Append(": ").Append(commit.Subject);
            string body = commit.Message.Trim();
            if (body.Length > commit.Subject.Length)
            {
                sb.Append('\n').Append(body);
            }

            sb.Append('\n').Append(FilesChanged(report.Changes, report.ChangesTruncated));
            return sb.ToString();
        }

        if (report.Text is null)
        {
            sb.Append(report.Path).Append(" at ").Append(commit.Short).Append(" (").Append(Count(report.Entries.Count, "entry", "entries")).Append(report.Cut ? ", the first " + N(Files.WorkingDirectory.MaxEntries) : "").Append("):");
            foreach (var entry in report.Entries)
            {
                sb.Append('\n').Append(entry);
            }

            return sb.ToString();
        }

        sb.Append(report.Path).Append(" at ").Append(commit.Short).Append(" (").Append(Count(report.Lines, "line")).Append(report.Cut ? ", cut at " + N(GitAccess.MaxShowChars) + " characters" : "").Append("):\n");
        sb.Append(report.Text);
        return sb.ToString();
    }

    /// <summary><c>Files changed (3, +10 −3): M src\a.cs (+4 −1)</c> …</summary>
    public static string FilesChanged(IReadOnlyList<GitChange> changes, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return "No files changed";
        }

        var sb = new StringBuilder();
        sb.Append("Files changed (").Append(N(changes.Count)).Append(truncated ? "+" : "").Append(", +").Append(N(changes.Sum(c => c.Added))).Append(" −").Append(N(changes.Sum(c => c.Deleted))).Append("):");
        foreach (var change in changes)
        {
            sb.Append('\n').Append(ChangeRow(change));
        }

        if (truncated)
        {
            sb.Append("\n… and more (the first ").Append(N(GitAccess.MaxChangedFiles)).Append(" shown)");
        }

        return sb.ToString();
    }

    public static string ChangeRow(GitChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        string path = change.OldPath is { } old ? old + " → " + change.Path : change.Path;
        return change.Binary ? $"{change.Code} {path} (binary)" : $"{change.Code} {path} (+{N(change.Added)} −{N(change.Deleted)})";
    }

    // ---- diff ----

    /// <summary><c>Unstaged changes (2 files, +10 −3):</c> the file rows, a blank line, the patch, a cut note when it stopped early.</summary>
    public static string Diff(GitDiffReport report, string path)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(path);
        string what = report.Kind switch
        {
            GitDiffKind.Unstaged => "Unstaged changes",
            GitDiffKind.Staged => "Staged changes",
            GitDiffKind.Commit => "Changes in " + report.Label,
            _ => "Changes from " + report.Label,
        };
        string under = path.Length > 0 ? " under " + path : "";
        if (report.Files.Count == 0)
        {
            return report.Kind switch
            {
                GitDiffKind.Unstaged => "No unstaged changes" + under,
                GitDiffKind.Staged => "No staged changes" + under,
                _ => "No changes " + (report.Kind == GitDiffKind.Commit ? "in " : "from ") + report.Label + under,
            };
        }

        var sb = new StringBuilder();
        sb.Append(what).Append(under).Append(" (").Append(Count(report.Files.Count, "file")).Append(report.FilesTruncated ? "+" : "").Append(", +").Append(N(report.Added)).Append(" −").Append(N(report.Deleted)).Append("):");
        foreach (var change in report.Files)
        {
            sb.Append('\n').Append(ChangeRow(change));
        }

        if (report.FilesTruncated)
        {
            sb.Append("\n… and more (the first ").Append(N(GitAccess.MaxChangedFiles)).Append(" files shown)");
        }

        if (report.Patch.Length > 0)
        {
            sb.Append("\n\n").Append(report.Patch);
        }

        if (report.Truncated)
        {
            sb.Append('\n').Append(DiffCutSuffix(report.TotalLines));
        }

        return sb.ToString();
    }

    public static string DiffCutSuffix(int total) => $"[… cut: {N(total)} lines in all; raise max_lines or narrow with path]";

    // ---- blame ----

    /// <summary><c>Blame of src\a.cs lines 1–120 of 120 at main (5 commits):</c> then <c>abc1234 2026-09-01 Chris   12│ text</c> per line.</summary>
    public static string Blame(GitBlameReport report, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(zone);
        if (report.TotalLines == 0)
        {
            return $"{report.Path} at {report.Reference} is empty";
        }

        var sb = new StringBuilder();
        sb.Append("Blame of ").Append(report.Path).Append(" lines ").Append(N(report.From)).Append('–').Append(N(report.To)).Append(" of ").Append(N(report.TotalLines))
          .Append(" at ").Append(report.Reference).Append(" (").Append(Count(report.Commits, "commit")).Append("):");
        int width = N(report.To).Length;
        int author = report.Lines.Count == 0 ? 0 : report.Lines.Max(l => l.Author.Length);
        foreach (var line in report.Lines)
        {
            sb.Append('\n').Append(line.Short).Append(' ').Append(Day(line.When, zone)).Append(' ').Append(line.Author.PadRight(author)).Append(' ').Append(N(line.Number).PadLeft(width)).Append('│').Append(line.Text);
        }

        if (report.To < report.TotalLines)
        {
            sb.Append("\n[… ").Append(N(report.TotalLines - report.To)).Append(" more lines; continue with from_line ").Append(N(report.To + 1)).Append(']');
        }

        return sb.ToString();
    }

    // ---- refs ----

    /// <summary><c>3 branches (current: main), 2 remote-tracking, 4 tags:</c> then the rows.</summary>
    public static string Refs(GitRefsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.Append(Count(report.Local.Count, "branch", "branches"));
        sb.Append(report.Detached ? " (HEAD detached)" : report.Current is { } current ? " (current: " + current + ")" : "");
        if (report.Remote.Count > 0)
        {
            sb.Append(", ").Append(N(report.Remote.Count)).Append(" remote-tracking");
        }

        if (report.Tags.Count > 0)
        {
            sb.Append(", ").Append(Count(report.Tags.Count, "tag"));
        }

        sb.Append(report.Truncated ? " (cut at the caps):" : ":");
        foreach (var b in report.Local)
        {
            sb.Append('\n').Append(b.Current ? "* " : "  ").Append(b.Name).Append(' ').Append(b.Short).Append(b.Upstream is { } up ? " [" + up + "]" : "").Append(' ').Append(b.Subject);
        }

        foreach (var b in report.Remote)
        {
            sb.Append("\n  ").Append(b.Name).Append(' ').Append(b.Short).Append(' ').Append(b.Subject);
        }

        foreach (var t in report.Tags)
        {
            sb.Append("\n  tag ").Append(t.Name).Append(' ').Append(t.Short).Append(' ').Append(t.Subject);
        }

        return sb.ToString();
    }

    public static string Created(GitBranchResult result) => $"Created branch {result.Name} at {result.Short}";
    public static string CreatedAndSwitched(GitBranchResult result) => $"Created branch {result.Name} at {result.Short} and switched to it";
    public static string Switched(GitBranchResult result) => $"Switched to branch {result.Name} ({result.Short})";
    public static string Renamed(GitBranchResult result) => $"Renamed branch {result.OldName} to {result.Name}";

    // ---- stage / commit ----

    public static string Staged(GitStageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string verb = result.Unstage ? "Unstaged" : "Staged";
        if (result.Paths.Count == 0)
        {
            return $"Nothing to {(result.Unstage ? "unstage" : "stage")}" + (result.Folder.Length > 0 ? " under " + result.Folder : "");
        }

        return $"{verb} {Count(result.Paths.Count, "path")}: {string.Join(", ", result.Paths)}";
    }

    public static string Committed(GitCommitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"{(result.Amended ? "Amended" : "Committed")} {result.Short} on {result.Branch}: {result.Subject} ({Count(result.Files, "file")}, +{N(result.Added)} −{N(result.Deleted)})";
    }

    // ---- stash ----

    public static string Stashed(GitStashResult result) => $"Stashed {Count(result.Files, "file")} as {GitAccess.StashName(0)}: {result.Message}";
    public static string StashApplied(GitStashResult result, bool pop) => $"{(pop ? "Popped" : "Applied")} {GitAccess.StashName(result.Index)}: {result.Message} ({Count(result.Files, "file")} back)";

    public static string Stashes(GitStashResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Stashes.Count == 0)
        {
            return "No stashes";
        }

        var sb = new StringBuilder();
        sb.Append(Count(result.Stashes.Count, "stash", "stashes")).Append(result.Truncated ? " (the first " + N(GitAccess.MaxStashes) + "):" : ":");
        foreach (var s in result.Stashes)
        {
            sb.Append('\n').Append(GitAccess.StashName(s.Index)).Append(' ').Append(s.Short).Append(' ').Append(s.Message);
        }

        return sb.ToString();
    }

    // ---- discard / delete ----

    public static string Discarded(GitDiscardResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Paths.Count == 0
            ? $"Reset hard to {result.Reference} ({result.Short}: {result.Subject}): {Count(result.Files, "file")} changed; untracked files left alone"
            : $"Discarded changes in {Count(result.Paths.Count, "path")} (back to {result.Reference}): {string.Join(", ", result.Paths)}";
    }

    public static string Deleted(GitDeleteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Kind switch
        {
            GitDeleteKind.Branch => $"Deleted branch {result.Name} (was {result.Short})",
            GitDeleteKind.Tag => $"Deleted tag {result.Name} (was {result.Short})",
            _ => $"Dropped {result.Name}: {result.Message}",
        };
    }

    // ---- helpers ----

    /// <summary><c>yyyy-MM-dd HH:mm</c> in <paramref name="zone"/>.</summary>
    public static string When(DateTimeOffset when, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(when, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary><c>yyyy-MM-dd</c> in <paramref name="zone"/>.</summary>
    public static string Day(DateTimeOffset when, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(when, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Count(int n, string singular, string? plural = null) =>
        N(n) + " " + (n == 1 ? singular : plural ?? singular + "s");

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
