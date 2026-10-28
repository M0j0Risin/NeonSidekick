using System.Globalization;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// The words of <c>/undo</c>, its purges and the restore's report (2026-10-28, the checkpoints plan), shared with <c>/rewind … files</c>. User
/// text always says "file checkpoints", never a bare "checkpoint" (the word is the Ralph loop's and ComfyUI's too). One place, so tests can
/// pin them: the contract words and the model's note pinned, the prose not.
/// </summary>
public static class UndoText
{
    /// <summary>The command word. Pinned.</summary>
    public const string Word = "/undo";

    /// <summary>The <c>/help</c> row's summary (≤ 40 cells). Pinned.</summary>
    public const string HelpSummary = "put back the files a turn changed";

    /// <summary>The purge argument. Pinned.</summary>
    public const string PurgeWord = "purge";

    /// <summary>The every-folder purge's argument. Pinned.</summary>
    public const string AllWord = "all";

    /// <summary>The completion note on <c>purge</c>.</summary>
    public const string PurgeNote = "delete this folder's file checkpoints";

    /// <summary>The completion note on <c>purge all</c>.</summary>
    public const string PurgeAllNote = "every folder's";

    /// <summary>No stamped turn left in this conversation.</summary>
    public const string NothingToUndo = "(" + NoticeGlyphs.Rewind + "nothing to undo: no turn of this conversation changed files)";

    /// <summary><see cref="NothingToUndo"/>, naming the setting while it is off.</summary>
    public static string NothingNotice(bool off) => off ? NothingToUndo[..^1] + "; File checkpoints is off in /tools › Files › File)" : NothingToUndo;

    /// <summary>Stamped turns whose file checkpoints are all gone.</summary>
    public const string NoLongerKept = "(" + NoticeGlyphs.Rewind + "the files of those turns are no longer kept: cleared by age or size, or purged)";

    /// <summary><c>/undo 0</c>, a negative or another word. Pinned.</summary>
    public const string UsageError = "/undo takes a number of turns back (1 by default), or purge, or purge all.";

    /// <summary>The spinner's label while files go back.</summary>
    public const string Restoring = "Putting files back";

    /// <summary>The conflict pane's rows. Pinned.</summary>
    public static readonly IReadOnlyList<string> ConflictRows = ["Restore over them", "Skip them", "Cancel"];

    /// <summary>The conflict pane's keys. Pinned.</summary>
    public const string ConflictKeys = "r / s = pick · Enter = choose · Esc = cancel";

    /// <summary>The conflict pane's hotkeys: <c>r</c> restore over, <c>s</c> skip. Pinned.</summary>
    public static readonly IReadOnlyDictionary<char, int> ConflictHotkeys = new Dictionary<char, int> { ['r'] = 0, ['s'] = 1 };

    /// <summary>The page's ids for the conflict card's choices, in row order.</summary>
    public static readonly IReadOnlyList<string> ConflictChoices = ["restore", "skip", "cancel"];

    /// <summary>The typed question for conflicts without a pane.</summary>
    public const string TypedConflictQuestion = "Restore over the files you changed since?";

    /// <summary>The question: <c>↩️ Put back the files the last turn changed?</c>, or the last 3 turns'.</summary>
    public static string Question(int turns) => turns == 1
        ? NoticeGlyphs.Rewind + "Put back the files the last turn changed?"
        : string.Create(CultureInfo.InvariantCulture, $"{NoticeGlyphs.Rewind}Put back the files the last {turns} turns changed?");

    /// <summary>The first <see cref="FileRestore.ListedFiles"/> names, then <c>and 12 more</c>.</summary>
    public static IReadOnlyList<string> Listed(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count <= FileRestore.ListedFiles)
        {
            return names;
        }

        return [.. names.Take(FileRestore.ListedFiles), string.Create(CultureInfo.InvariantCulture, $"and {names.Count - FileRestore.ListedFiles} more")];
    }

    /// <summary>A path as the lists show it: relative with the system's separator, or full when its folder isn't the working folder.</summary>
    public static string Shown(RestorePath where, string currentRoot)
    {
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(currentRoot);
        return WorkingDirectory.PathComparer.Equals(where.Root, currentRoot) ? where.Path.Replace('/', Path.DirectorySeparatorChar) : where.Full;
    }

    /// <summary>
    /// What a restore question says under its first line: the files that come back (listed), the other folder when it isn't the working
    /// folder, the kinds of change it can't put back, and what can't be read or isn't kept. Lines joined by line breaks.
    /// </summary>
    public static string Caption(RestorePlan plan, string currentRoot, string imagesRoot, IReadOnlyList<UntrackedKind> untracked)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var names = Names(plan, currentRoot);
        return string.Join('\n', [FilesHeading(names.Count) + " " + string.Join(", ", Listed(names)), .. Notes(plan, currentRoot, imagesRoot, untracked)]);
    }

    /// <summary>The paths a restore puts back, as the lists show them.</summary>
    public static IReadOnlyList<string> Names(RestorePlan plan, string currentRoot)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Paths.Select(p => Shown(p, currentRoot)).ToList();
    }

    /// <summary>A caption's first words: <c>3 files:</c>.</summary>
    public static string FilesHeading(int count) => Files(count) + ":";

    /// <summary>The caption's lines after the file list: the other folder, what isn't undone, what isn't kept or can't be read.</summary>
    public static IReadOnlyList<string> Notes(RestorePlan plan, string currentRoot, string imagesRoot, IReadOnlyList<UntrackedKind> untracked)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(untracked);
        var lines = new List<string>();
        foreach (string root in plan.Roots.Where(r => !WorkingDirectory.PathComparer.Equals(r, currentRoot) && !WorkingDirectory.PathComparer.Equals(r, imagesRoot)))
        {
            lines.Add("In " + root + ", not the working folder.");
        }

        if (untracked.Count > 0)
        {
            lines.Add(NotUndone(untracked));
        }

        if (plan.Incomplete)
        {
            lines.Add("Some of those turns' changes weren't kept.");
        }

        if (plan.StoppedAt is { } stopped)
        {
            lines.Add(Unreadable(stopped.Problem));
        }

        if (plan.NoLongerKept.Count > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"The files of {Turns(plan.NoLongerKept.Count)} are no longer kept."));
        }

        return lines;
    }

    /// <summary>The line naming what can't be put back: <c>Not undone: shell commands, git.</c></summary>
    public static string NotUndone(IReadOnlyList<UntrackedKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        return "Not undone: what " + string.Join(", ", kinds.Select(KindName)) + " changed.";
    }

    /// <summary>A kind's name in <see cref="NotUndone"/>.</summary>
    public static string KindName(UntrackedKind kind) => kind switch
    {
        UntrackedKind.Shell => "shell commands",
        UntrackedKind.Git => "git tools",
        UntrackedKind.Share => "share tools",
        UntrackedKind.Vault => "vault tools",
        UntrackedKind.Database => "database tools",
        UntrackedKind.Skill => "skill_editor",
        UntrackedKind.Splash => "set_splash_image",
        _ => "MCP tools",
    };

    /// <summary>Why the replay stops before a turn: a newer version's checkpoint, or a damaged one.</summary>
    public static string Unreadable(CheckpointProblem problem) => problem == CheckpointProblem.NewerVersion
        ? "Stops before a turn whose file checkpoint a newer version of the app made."
        : "Stops before a turn whose file checkpoint is damaged.";

    /// <summary>The conflict question: <c>2 files changed since. Restore over them?</c></summary>
    public static string ConflictQuestion(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"{NoticeGlyphs.Rewind}{Files(count)} changed since. Restore over them?");

    /// <summary>The conflict pane's caption: the files, what "restore over" does with the current copy, and the ones always skipped.</summary>
    public static string ConflictCaption(IReadOnlyList<RestoreConflict> conflicts, string currentRoot, bool toBin)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        var lines = new List<string> { string.Join(", ", Listed(conflicts.Where(c => !c.KindChanged).Select(c => Shown(c.Where, currentRoot)).ToList())) };
        lines.Add(toBin
            ? "Restore over sends your version to the " + FileText.RecycleBinName + " first."
            : "Restore over writes over your version: File deletes to " + FileText.RecycleBinName + " is off.");
        if (conflicts.Any(c => c.KindChanged))
        {
            lines.Add("Always skipped (a folder now where a file was, or the other way): "
                + string.Join(", ", Listed(conflicts.Where(c => c.KindChanged).Select(c => Shown(c.Where, currentRoot)).ToList())));
        }

        return string.Join('\n', lines);
    }

    /// <summary>The note over a restored file's diff: <c>Put back notes.md</c>.</summary>
    public static string DiffNote(string shown) => "Put back " + shown;

    /// <summary>The restored files with no diff drawn (a binary file, a folder, past the first 20): <c>Put back: a.png, b/</c>.</summary>
    public static string PutBack(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return "Put back: " + string.Join(", ", names);
    }

    /// <summary>How a failure's line in the report starts.</summary>
    public const string CouldNotPrefix = "Could not put back ";

    /// <summary>The report's lines: what came back, then what was skipped, not kept, or failed. The first line is a notice, the rest warnings.</summary>
    public static IReadOnlyList<string> Report(RestoreReport report, string currentRoot)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>();
        int back = report.Count(RestoreOutcome.Restored) + report.Count(RestoreOutcome.AlreadyBack);
        lines.Add(report.Stopped
            ? string.Create(CultureInfo.InvariantCulture, $"({NoticeGlyphs.Rewind}stopped: {Files(back)} back; {Word} again puts back the rest)")
            : string.Create(CultureInfo.InvariantCulture, $"({NoticeGlyphs.Rewind}put back {Files(back)})"));
        Add(lines, report, RestoreOutcome.Skipped, "Skipped, changed since: ", currentRoot);
        Add(lines, report, RestoreOutcome.NotSaved, "Not kept, so left as they are: ", currentRoot);
        foreach (var failed in report.Files.Where(f => f.Outcome == RestoreOutcome.Failed).Take(FileRestore.ListedFiles))
        {
            lines.Add(CouldNotPrefix + Shown(failed.Where, currentRoot) + (failed.Detail.Length > 0 ? ": " + failed.Detail : ""));
        }

        return lines;
    }

    private static void Add(List<string> lines, RestoreReport report, RestoreOutcome outcome, string label, string currentRoot)
    {
        var names = report.Files.Where(f => f.Outcome == outcome).Select(f => Shown(f.Where, currentRoot)).ToList();
        if (names.Count > 0)
        {
            lines.Add(label + string.Join(", ", Listed(names)));
        }
    }

    /// <summary>
    /// The note the model gets ahead of the user's next message, pinned: which turns' file changes are undone (named by the start of
    /// their messages), the files back, and those kept as they are.
    /// </summary>
    public static string ModelNote(IReadOnlyList<string> messages, IReadOnlyList<string> restored, IReadOnlyList<string> kept)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(restored);
        ArgumentNullException.ThrowIfNull(kept);
        string turns = string.Join(" and ", messages.Select(m => "\"" + Excerpt(m) + "\""));
        string note = ConversationHistory.UndoNotePreamble + "the file changes you made answering " + turns + " are undone";
        if (restored.Count > 0)
        {
            note += "; these files are back as they were before: " + string.Join(", ", Listed(restored));
        }

        if (kept.Count > 0)
        {
            note += "; these were kept as they are: " + string.Join(", ", Listed(kept));
        }

        return note + "]";
    }

    /// <summary>Two notes before the next message, merged into one line.</summary>
    public static string Merge(string? pending, string next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (pending is null)
        {
            return next;
        }

        return pending.TrimEnd(']') + "; then " + next[ConversationHistory.UndoNotePreamble.Length..];
    }

    /// <summary>A turn's message as the note names it: its first line, about 40 characters.</summary>
    public static string Excerpt(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string line = message.Trim().Split('\n')[0].Trim();
        return line.Length <= 40 ? line : TextCut.Safe(line, 40).TrimEnd() + "…";
    }

    // ---- purges ----

    /// <summary><c>/undo purge</c>'s question for the working folder (and the images folder).</summary>
    public static string PurgeQuestion(string folder) =>
        NoticeGlyphs.Rewind + "Purge every conversation's file checkpoints for " + folder + "?\nThose turns' files can no longer be put back; deleted for good.";

    /// <summary><c>/undo purge all</c>'s question, naming what it frees.</summary>
    public static string PurgeAllQuestion(long bytes, int folders) =>
        string.Create(CultureInfo.InvariantCulture, $"{NoticeGlyphs.Rewind}Purge {Megabytes(bytes)} of file checkpoints in {Folders(folders)}?\nThose turns' files can no longer be put back; deleted for good.");

    /// <summary>The spinner's label while stores are deleted.</summary>
    public const string Purging = "Purging file checkpoints";

    /// <summary>The diagnostic line after a purge.</summary>
    public static string PurgedLogLine(int stores, long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"Purged {stores} file checkpoint store{(stores == 1 ? "" : "s")} ({bytes} bytes)");

    /// <summary>With nothing kept.</summary>
    public const string NothingToPurge = "(" + NoticeGlyphs.Rewind + "no file checkpoints are kept)";

    /// <summary>After a purge.</summary>
    public static string Purged(int folders, long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"({NoticeGlyphs.Rewind}purged the file checkpoints of {Folders(folders)}, {Megabytes(bytes)})");

    /// <summary>Stores a purge left: a turn is still writing to them, or they couldn't be deleted.</summary>
    public static string PurgeLeft(IReadOnlyList<string> folders) =>
        "Left as they are (in use, or couldn't be deleted): " + string.Join(", ", Listed(folders));

    public static string Megabytes(long bytes) =>
        Math.Max(1, (bytes + (1024 * 1024) - 1) / (1024 * 1024)).ToString("N0", CultureInfo.InvariantCulture) + " MB";

    private static string Files(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " file" : " files");

    private static string Folders(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " folder" : " folders");

    private static string Turns(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " earlier turn" : " earlier turns");

    /// <summary>
    /// <c>/undo</c>'s argument: empty is 1, a positive whole number is itself, <c>purge</c> and <c>purge all</c> their own; anything else null
    /// (<see cref="UsageError"/>).
    /// </summary>
    public static UndoArgs? Parse(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return new UndoArgs(1, Purge: false, All: false);
        }

        if (string.Equals(words[0], PurgeWord, StringComparison.OrdinalIgnoreCase))
        {
            return words.Length switch
            {
                1 => new UndoArgs(0, Purge: true, All: false),
                2 when string.Equals(words[1], AllWord, StringComparison.OrdinalIgnoreCase) => new UndoArgs(0, Purge: true, All: true),
                _ => null,
            };
        }

        return words.Length == 1 && int.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count > 0
            ? new UndoArgs(count, Purge: false, All: false)
            : null;
    }
}

/// <summary><c>/undo</c>'s parsed argument: how many turns back, or a purge (of every folder with <paramref name="All"/>).</summary>
public sealed record UndoArgs(int Count, bool Purge, bool All);
