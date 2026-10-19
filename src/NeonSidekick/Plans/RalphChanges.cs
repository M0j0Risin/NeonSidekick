using System.Globalization;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Git;

namespace NeonSidekick.Plans;

/// <summary>A file as a snapshot saw it: its size and time, and in a repository its text (null for a binary file, one too large, one gone).</summary>
public sealed record RalphFileMark(long Length, DateTime Written, string? Text, bool Binary);

/// <summary>
/// The working tree before or after a pass (2026-10-18, <see cref="RalphChanges.Snapshot"/>): in a repository the changed files with their text (so a
/// pass's own changes are told apart from what was dirty before it), else every file's size and time; paths relative to the working directory,
/// spelled with <c>/</c>, <c>.neon/</c> never in it. <paramref name="Head"/> is HEAD's commit in a repository (null outside one, or with no commit
/// yet): a pass that commits its work moves it (2026-10-19, the second review: a file changed and committed was in neither snapshot).
/// </summary>
public sealed record RalphSnapshotOfFiles(bool Git, IReadOnlyDictionary<string, RalphFileMark> Files, bool Cut, string? Head = null);

/// <summary>What a pass changed (2026-10-18): the files, the diff (cut to <see cref="RalphChanges.MaxDiffChars"/>), and the evidence's flags.</summary>
public sealed record RalphDelta(IReadOnlyList<string> Changed, string Diff, bool NothingChanged, IReadOnlyList<string> NamedMissing, bool TestsTouched)
{
    /// <summary>The flags as the log and the reviewer read them.</summary>
    public IReadOnlyList<string> Flags =>
    [
        .. NothingChanged ? [RalphText.FlagNothingChanged] : Array.Empty<string>(),
        .. NamedMissing.Count > 0 ? [RalphText.FlagNamedMissing(NamedMissing)] : Array.Empty<string>(),
        .. TestsTouched ? [RalphText.FlagTestsTouched] : Array.Empty<string>(),
    ];
}

/// <summary>
/// A Ralph pass's evidence (2026-10-18): a snapshot of the working tree before the pass and after it, and the delta — the files the pass changed and a
/// unified diff of them (<see cref="UnifiedDiff"/>, the HEAD text through <see cref="GitAccess.Show"/> for a file that was clean before). In a repository
/// the changed paths come from <see cref="GitAccess.ChangedPaths"/> (an untracked folder walked), filtered under the working directory, and the files
/// of any commit the pass made (HEAD before to HEAD after, diffed from HEAD as the pass began; 2026-10-19, the second review); outside one,
/// every file's size and time (at most <see cref="MaxFiles"/>), with no diff. A binary file is listed with its size, never diffed; a file past
/// <see cref="UnifiedDiff.MaxDistinctLines"/> is "too large to diff" with its line counts. The flags (nothing changed, a file the step names missing,
/// test files touched) go to the log, the notices and the reviewer, and never untick a step on their own.
/// </summary>
public static class RalphChanges
{
    /// <summary>The most diff characters a delta keeps.</summary>
    public const int MaxDiffChars = 24_000;

    /// <summary>The most files a snapshot outside a repository stamps.</summary>
    public const int MaxFiles = 20_000;

    /// <summary>The largest file whose text a snapshot keeps.</summary>
    public const long MaxTextBytes = 2_000_000;

    /// <summary>A snapshot of <paramref name="files"/>' tree, through <paramref name="git"/> when the working directory is in a repository.</summary>
    public static RalphSnapshotOfFiles Snapshot(WorkingDirectory files, GitAccess? git, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (git is not null && git.ChangedPaths("") is { Outcome: GitOutcome.Ok } changed)
        {
            var marks = new Dictionary<string, RalphFileMark>(StringComparer.Ordinal);
            foreach (string raw in changed.Paths)
            {
                string path = Slash(raw);
                if (!Skipped(path))
                {
                    marks[path] = Mark(files, path, withText: true);
                }
            }

            string? head = git.MergeBase("", "HEAD", "HEAD") is { Outcome: GitOutcome.Ok, Head: { } tip } ? tip : null;
            return new RalphSnapshotOfFiles(true, marks, changed.Truncated, head);
        }

        var listed = files.ListFiles("", MaxFiles, name => name is PlanSlug.AppFolder or ".git", cancellationToken);
        var stamps = new Dictionary<string, RalphFileMark>(StringComparer.Ordinal);
        foreach (string raw in listed.Files)
        {
            string path = Slash(raw);
            if (!Skipped(path))
            {
                stamps[path] = Mark(files, path, withText: false);
            }
        }

        return new RalphSnapshotOfFiles(false, stamps, listed.Cut);
    }

    /// <summary>
    /// What changed from <paramref name="before"/> to <paramref name="after"/>: the files, their diff (in a repository), and the flags, with
    /// <paramref name="named"/> the paths the step names (missing after the pass is a flag).
    /// </summary>
    public static RalphDelta Delta(RalphSnapshotOfFiles before, RalphSnapshotOfFiles after, WorkingDirectory files, GitAccess? git, IReadOnlyList<string> named)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(named);
        var changed = new List<string>();
        var diff = new StringBuilder();
        var committed = Committed(before, after, git);
        foreach (string path in before.Files.Keys.Union(after.Files.Keys, StringComparer.Ordinal).Union(committed, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            before.Files.TryGetValue(path, out var was);
            after.Files.TryGetValue(path, out var now);
            bool differs = (was, now) switch
            {
                // In neither snapshot: clean before and clean again, changed only when a commit of the pass's holds it.
                (null, null) => committed.Contains(path),
                (null, _) or (_, null) => true,
                _ => was.Length != now.Length || was.Written != now.Written || !string.Equals(was.Text, now.Text, StringComparison.Ordinal),
            };
            if (!differs)
            {
                continue;
            }

            changed.Add(path);
            if (!after.Git || diff.Length >= MaxDiffChars)
            {
                continue;
            }

            // The text before the pass: the dirty file's own, else HEAD's as the pass began (a file clean before, made dirty or committed by the pass).
            string? oldText = was is not null ? was.Text : HeadText(git, before.Head ?? "HEAD", path);
            // A file the pass cleaned again (back to HEAD's) is no longer in the after snapshot: its text now is HEAD's or the disk's.
            var current = now ?? Mark(files, path, withText: true);
            if ((was?.Binary ?? false) || current.Binary)
            {
                diff.Append($"(binary {path}, {Size(current.Length)})\n");
                continue;
            }

            string newText = current.Text ?? (now is null && !Exists(files, path) ? "" : HeadText(git, "HEAD", path) ?? "");
            var hunks = UnifiedDiff.Hunks(UnifiedDiff.Split(oldText ?? ""), UnifiedDiff.Split(newText));
            if (hunks is null)
            {
                diff.Append($"({path} is too large to diff: {Lines(oldText)} lines before, {Lines(newText)} after)\n");
                continue;
            }

            diff.Append(UnifiedDiff.Format(oldText is null ? "/dev/null" : "a/" + path, newText.Length == 0 && !Exists(files, path) ? "/dev/null" : "b/" + path, hunks));
        }

        string text = diff.ToString();
        if (text.Length > MaxDiffChars)
        {
            text = text[..MaxDiffChars] + "\n… (the diff goes on; cut at " + MaxDiffChars.ToString("N0", CultureInfo.InvariantCulture) + " characters)\n";
        }

        var missing = named.Select(Slash).Where(p => !Exists(files, p)).ToList();
        return new RalphDelta(changed, text, changed.Count == 0, missing, changed.Any(IsTestPath));
    }

    /// <summary>Whether <paramref name="path"/> looks like a test file (generic names: a <c>test</c>/<c>tests</c>/<c>spec</c> folder, <c>*Tests.cs</c>, <c>test_*.py</c>, <c>*.test.ts</c>, <c>*_test.go</c>…).</summary>
    public static bool IsTestPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string slashed = Slash(path);
        string lower = slashed.ToLowerInvariant();
        string name = slashed[(slashed.LastIndexOf('/') + 1)..];
        string stem = name.Contains('.') ? name[..name.IndexOf('.')] : name;
        string lowerStem = stem.ToLowerInvariant();
        string lowerName = name.ToLowerInvariant();
        // A folder of tests, a CamelCase *Test/*Tests, test_*, *_test, *.test.*, *.spec.* — never a word that only ends in "test" (Contest).
        return lower.Split('/').SkipLast(1).Any(part => part is "test" or "tests" or "spec" or "specs" or "__tests__")
            || stem.EndsWith("Tests", StringComparison.Ordinal) || stem.EndsWith("Test", StringComparison.Ordinal) || lowerStem is "test" or "tests"
            || lowerStem.StartsWith("test_", StringComparison.Ordinal) || lowerStem.EndsWith("_test", StringComparison.Ordinal) || lowerStem.EndsWith("_tests", StringComparison.Ordinal)
            || lowerName.Contains(".test.", StringComparison.Ordinal) || lowerName.Contains(".spec.", StringComparison.Ordinal);
    }

    private static bool Skipped(string path) => path.StartsWith(PlanSlug.AppFolder + "/", StringComparison.Ordinal) || path == PlanSlug.AppFolder;

    private static RalphFileMark Mark(WorkingDirectory files, string path, bool withText)
    {
        if (files.Resolve(path, forWrite: false, out string full) != FileOutcome.Ok)
        {
            return new RalphFileMark(-1, DateTime.MinValue, null, false);
        }

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                return new RalphFileMark(-1, DateTime.MinValue, null, false);
            }

            if (!withText || info.Length > MaxTextBytes)
            {
                return new RalphFileMark(info.Length, info.LastWriteTimeUtc, null, false);
            }

            byte[] bytes = File.ReadAllBytes(full);
            bool binary = WorkingDirectory.LooksBinary(bytes);
            return new RalphFileMark(info.Length, info.LastWriteTimeUtc, binary ? null : Encoding.UTF8.GetString(bytes), binary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RalphFileMark(-1, DateTime.MinValue, null, false);
        }
    }

    private static string? HeadText(GitAccess? git, string reference, string path) =>
        git?.Show(reference, path) is { Outcome: GitOutcome.Ok, Text: { } text } ? text : null;

    /// <summary>
    /// The files the commits a pass made hold (HEAD moved from <paramref name="before"/>'s to <paramref name="after"/>'s): a range diff's paths, both
    /// sides of a rename, <c>.neon/</c> left out; none when HEAD stayed or either end is unknown.
    /// </summary>
    private static HashSet<string> Committed(RalphSnapshotOfFiles before, RalphSnapshotOfFiles after, GitAccess? git)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (git is null || before.Head is not { } from || after.Head is not { } to || string.Equals(from, to, StringComparison.Ordinal))
        {
            return paths;
        }

        if (git.Diff(new GitDiffRequest(GitDiffKind.Range, "", From: from, To: to), 1) is { Outcome: GitOutcome.Ok } range)
        {
            foreach (var change in range.Files)
            {
                foreach (string path in change.OldPath is { } old ? [change.Path, old] : new[] { change.Path })
                {
                    if (Slash(path) is var slashed && !Skipped(slashed))
                    {
                        paths.Add(slashed);
                    }
                }
            }
        }

        return paths;
    }

    private static bool Exists(WorkingDirectory files, string path) =>
        files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && File.Exists(full);

    private static string Slash(string path) => path.Replace('\\', '/').TrimStart('/');

    private static int Lines(string? text) => text is null ? 0 : UnifiedDiff.Split(text).Lines.Count;

    private static string Size(long bytes) => bytes < 0 ? "gone" : bytes < 1024 ? bytes.ToString(CultureInfo.InvariantCulture) + " bytes" : (bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
}
