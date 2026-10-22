using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Plans;

/// <summary>
/// A Ralph log's head: what the run was fixed to, when it began, and how its last run stopped (null while one runs). <paramref name="InPassLine"/>
/// and <paramref name="InPassStep"/> (2026-10-19, the third review: a crash, a kill or a reset mid-pass left a box the model ticked standing with no
/// check, and a resume passed the step by) name the step a pass is working while it runs — its line in the plan file and its words — cleared once
/// the pass is judged; a start that finds them set puts that step's box back first (<see cref="RalphLog.InPassOf"/>). <paramref name="InPassLines"/>
/// (2026-10-19, the fourth review: only the pass's own box went back, so other boxes it ticked, steps it reworded or deleted stood) are the plan's
/// task lines as that pass began, its <c>## In pass</c> section: the start puts the whole plan back as the judge would (<c>RalphJudge.Restore</c>).
/// Set for the final fix too, which has no step of its own; null with no pass under way (or a log the previous build wrote).
/// <paramref name="InPassHeaded"/> (2026-10-19, the fifth review) is whether the plan read its steps under <c>## Steps</c> then, and each in-pass line
/// keeps its <c>Done when:</c> (a <c>done when:</c> item under it), so the start puts back a renamed heading and a weakened check as the judge does;
/// null in a log the previous build wrote (which kept neither). <paramref name="InPassPlan"/> (2026-10-20, a review) is the plan's
/// <see cref="PlanDocument.Stamp"/> as the pass began: a start whose plan was revised, approved or run since puts nothing back.
/// </summary>
public sealed record RalphLogHead(string Plan, string? Verify, string? Final, bool FinalOnly, string Shell, DateTimeOffset Started, string? Stopped = null, int InPassLine = -1, string? InPassStep = null, IReadOnlyList<PlanStep>? InPassLines = null, bool? InPassHeaded = null, string? InPassPlan = null);

/// <summary>
/// One pass as the log keeps it (2026-10-18): its number, its step, the outcome and the model's summary, what it said it checked, the app's check
/// (<c>passed</c>, <c>failed (exit 1)</c>, <c>timed out</c>, <c>none</c>…) and its output's tail, the evidence's flags, the files changed, the
/// review's verdict, a checkpoint's answer, and when and how long.
/// </summary>
public sealed record RalphLogEntry(
    int Pass,
    int Step,
    string StepText,
    string Outcome,
    string Summary,
    IReadOnlyList<string> Verified,
    string Check,
    string? Tail,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> Changed,
    string Review,
    string? Checkpoint,
    DateTimeOffset At,
    TimeSpan? Elapsed = null,
    long? Tokens = null,
    string? Notes = null);

/// <summary>A Ralph log read back: its head (null when the file has none the app wrote), its learned facts, its passes in order.</summary>
public sealed record RalphLogDocument(RalphLogHead? Head, IReadOnlyList<string> Learned, IReadOnlyList<RalphLogEntry> Entries)
{
    public static RalphLogDocument Empty { get; } = new(null, [], []);
}

/// <summary>
/// The Ralph loop's log beside the plan (2026-10-18): <c>.neon/plans/&lt;slug&gt;/ralph.md</c>, written by the app, read back tolerant of hand edits
/// (the code audit's report way): a head, a <c>## Learned</c> section — each pass's lasting facts, redacted, the newest kept within
/// <see cref="LearnedChars"/>, carried whole into every pass's directive (the loop's own AGENTS.md) — and a <c>## Pass N</c> entry a pass. A check's
/// output is scrubbed, redacted (<see cref="ToolRedaction.Text"/>) and fenced, at most <see cref="TailLines"/> lines. <c>ralph.lock</c> beside it holds
/// the process that runs the loop (its pid and start time): another live one refuses a start, a stale one is taken over.
/// </summary>
public static partial class RalphLog
{
    public const string FileName = "ralph.md";
    public const string LockName = "ralph.lock";

    /// <summary>The most lines of a check's output kept.</summary>
    public const int TailLines = 40;

    /// <summary>The most characters the learned facts keep (the newest win).</summary>
    public const int LearnedChars = 2000;

    /// <summary>The most entries and characters <see cref="RecentNotes"/> hands a pass.</summary>
    public const int NotesEntries = 3;
    public const int NotesChars = 1500;

    /// <summary>The most characters of one pass's <c>finish_step</c> notes <see cref="RecentNotes"/> carries (so one long note never crowds out the rest).</summary>
    public const int NoteChars = 600;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:sszzz";
    private const string LearnedHeading = "## Learned";
    private const string InPassHeading = "## In pass";

    /// <summary>The label of an in-pass line's <c>Done when:</c>, the item under it.</summary>
    private const string InPassDoneWhen = "done when: ";
    private const string PassHeading = "## Pass ";

    /// <summary>The log of <paramref name="planPath"/>.</summary>
    public static string PathFor(string planPath) => PlanReferences.FolderOf(planPath) + "/" + FileName;

    /// <summary>The lock of <paramref name="planPath"/>.</summary>
    public static string LockPathFor(string planPath) => PlanReferences.FolderOf(planPath) + "/" + LockName;

    /// <summary>
    /// The most characters the log keeps (2026-10-19, the second review: it only grew, and past <see cref="WorkingDirectory.MaxWriteChars"/> it was
    /// read cut off, its newest passes lost, and every write refused): the oldest passes go first, the newest always kept.
    /// </summary>
    public const int MaxLogChars = 150_000;

    /// <summary>The log of <paramref name="planPath"/> read back whole; null when there is none, or it cannot be read.</summary>
    public static RalphLogDocument? Read(WorkingDirectory files, string planPath) => ReadFor(files, planPath, out _);

    /// <summary>
    /// The log read whole (its size bounded by <see cref="WorkingDirectory.MaxReadFileBytes"/>, never a window); null with
    /// <paramref name="problem"/> the error sentence when a file is there that cannot be read, so a write never replaces it. Null with no problem
    /// when there is no log yet.
    /// </summary>
    internal static RalphLogDocument? ReadFor(WorkingDirectory files, string planPath, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(files);
        string path = PathFor(planPath);
        var read = files.ReadText(path, null, null, int.MaxValue);
        problem = read.Outcome is FileOutcome.Ok or FileOutcome.Missing ? null : FileText.Error(read.Outcome, path, "read", read.Detail);
        return read.Outcome == FileOutcome.Ok ? Parse(read.Text) : null;
    }

    /// <summary>The log written whole (its oldest passes left out past <see cref="MaxLogChars"/>); null when written, else the error sentence.</summary>
    public static string? Write(WorkingDirectory files, string planPath, RalphLogDocument document)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(document);
        string path = PathFor(planPath);
        var written = files.WriteText(path, Render(Bounded(document)), overwrite: true);
        return written.Outcome == FileOutcome.Ok ? null : FileText.Error(written.Outcome, path, "write", written.Detail);
    }

    /// <summary><paramref name="document"/> with its oldest passes dropped until it renders within <see cref="MaxLogChars"/>; the newest pass always kept. Pure.</summary>
    public static RalphLogDocument Bounded(RalphLogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        int length = Render(document).Length;
        if (length <= MaxLogChars || document.Entries.Count <= 1)
        {
            return document;
        }

        // Each entry's own share of the text (rendered alone, less the empty document's), dropped from the oldest.
        int bare = Render(document with { Entries = [] }).Length;
        int drop = 0;
        while (length > MaxLogChars && drop < document.Entries.Count - 1)
        {
            length -= Render(new RalphLogDocument(document.Head, document.Learned, [document.Entries[drop]])).Length - bare;
            drop++;
        }

        return document with { Entries = document.Entries.Skip(drop).ToList() };
    }

    /// <summary>
    /// <paramref name="entry"/> added to the log of <paramref name="planPath"/>, its <paramref name="learned"/> facts merged into the Learned section
    /// (redacted, once each, the newest kept), the head set to <paramref name="head"/>; null when written, else the error sentence.
    /// </summary>
    public static string? Append(WorkingDirectory files, string planPath, RalphLogHead head, RalphLogEntry entry, IReadOnlyList<string> learned)
    {
        var document = ReadFor(files, planPath, out string? problem);
        return problem ?? Append(files, planPath, document, head, entry, learned);
    }

    /// <summary>
    /// <see cref="Append"/> onto <paramref name="document"/>, the log as the caller just read it with no problem (<see cref="ReadFor"/>; null for none
    /// yet), so a pass's entry costs one read of the log (2026-10-20, a review: the judge read it for the head's start time, then Append read and
    /// parsed it again).
    /// </summary>
    internal static string? Append(WorkingDirectory files, string planPath, RalphLogDocument? document, RalphLogHead head, RalphLogEntry entry, IReadOnlyList<string> learned)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(learned);
        document ??= RalphLogDocument.Empty;
        var entries = document.Entries.ToList();
        entries.Add(Capped(entry));
        return Write(files, planPath, new RalphLogDocument(head, MergeLearned(document.Learned, learned), entries));
    }

    /// <summary>The most characters an entry's summary and notes keep, each.</summary>
    public const int EntryTextChars = 2000;

    /// <summary>The most lines of an entry's <c>verified</c> kept, and the characters of each.</summary>
    public const int VerifiedLines = 20;
    public const int VerifiedChars = 300;

    /// <summary>The most characters of a check's tail an entry keeps (its end).</summary>
    public const int TailChars = 8000;

    /// <summary>The most changed files an entry lists, a count of the rest after them.</summary>
    public const int ChangedKept = 200;

    /// <summary>
    /// <paramref name="entry"/> cleaned and cut as the log keeps it (2026-10-20, a review: finish_step's summary, verified lines and notes, a check's
    /// lines and the files changed had no cap, and <see cref="Bounded"/> always keeps the newest pass, so one long pass made every write of the log
    /// refused, the in-pass mark with it). The checkpoint field too (2026-10-22, a review: the by-hand question's Redo note reached it here raw, a key the
    /// user pasted kept as typed and handed to the next pass, while <see cref="SetCheckpoint(WorkingDirectory, string, int, string, out bool)"/> cleaned the
    /// checkpoint's own). Pure.
    /// </summary>
    internal static RalphLogEntry Capped(RalphLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var changed = entry.Changed.Count <= ChangedKept ? entry.Changed : [.. entry.Changed.Take(ChangedKept), RalphText.ChangedMore(entry.Changed.Count - ChangedKept)];
        return entry with
        {
            Summary = Shorten(Clean(entry.Summary), EntryTextChars),
            Verified = entry.Verified.Take(VerifiedLines).Select(v => Shorten(Clean(v), VerifiedChars)).ToList(),
            Tail = entry.Tail is { } tail ? KeepEnd(CleanTail(tail), TailChars) : null,
            Notes = entry.Notes is { } notes ? Shorten(Clean(notes), EntryTextChars) : null,
            Checkpoint = entry.Checkpoint is { } checkpoint ? CleanCheckpoint(checkpoint) : null,
            Changed = changed,
        };
    }

    /// <summary>A checkpoint field as the log keeps it, by every writer: redacted, scrubbed, on one line, cut at <see cref="EntryTextChars"/>.</summary>
    private static string CleanCheckpoint(string checkpoint) => Shorten(Flat(Clean(checkpoint)), EntryTextChars);

    /// <summary><paramref name="text"/>'s last <paramref name="max"/> characters, led by "…" when it was longer.</summary>
    private static string KeepEnd(string text, int max) => text.Length <= max ? text : "…" + text[^(max - 1)..];

    /// <summary>The head set (a start, a resume, a stop), the rest as it was; null when written.</summary>
    public static string? SetHead(WorkingDirectory files, string planPath, RalphLogHead head)
    {
        var document = ReadFor(files, planPath, out string? problem) ?? RalphLogDocument.Empty;
        return problem ?? Write(files, planPath, document with { Head = head });
    }

    /// <summary>
    /// The head's mark of a pass under way set: its step (null for the final fix) and the plan's task lines as it began (<see cref="PlanSteps.Flat"/>
    /// of the file whole) with their <c>Done when:</c>, and whether they were read under <c>## Steps</c> (<paramref name="headed"/>), the rest as it was;
    /// null when written (or no head to mark). <paramref name="read"/> is the log as the caller just read it (<see cref="Read"/>), else it is read here.
    /// </summary>
    public static string? MarkInPass(WorkingDirectory files, string planPath, PlanStep? step, IReadOnlyList<PlanStep> lines, bool headed, RalphLogDocument? read = null, string? planStamp = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        string? problem = null;
        var document = read ?? ReadFor(files, planPath, out problem);
        if (problem is not null || document?.Head is not { } head)
        {
            return problem;
        }

        var marked = head with
        {
            InPassLine = step?.Line ?? -1,
            InPassStep = step is null ? null : Flat(step.Text),
            InPassLines = lines.Select(l => new PlanStep(0, l.Line, 0, Flat(l.Text), l.Ticked, l.DoneWhen is { } d ? Flat(d) : null, [], [], [])).ToList(),
            InPassHeaded = headed,
            InPassPlan = planStamp,
        };
        return Write(files, planPath, document with { Head = marked });
    }

    /// <summary>The head's mark of a pass under way cleared (the pass judged, or put back), the rest as it was; null when written (or nothing to clear).</summary>
    public static string? ClearInPass(WorkingDirectory files, string planPath)
    {
        var document = ReadFor(files, planPath, out string? problem);
        if (problem is not null || document?.Head is not { } head)
        {
            return problem;
        }

        var cleared = head with { InPassLine = -1, InPassStep = null, InPassLines = null, InPassHeaded = null, InPassPlan = null };
        return cleared == head ? null : Write(files, planPath, document with { Head = cleared });
    }

    /// <summary>
    /// The step a pass was working when the run ended without judging it (its head's mark, <see cref="RalphLogHead.InPassStep"/>): a step of no
    /// children, unticked as it began, at its line with its words, and — from the task lines kept beside it (2026-10-19, the fourth review: built as
    /// the only one of its words, a step whose words two lines share was lost once its line moved) — its place among the lines of those words; null
    /// with no mark. Pure.
    /// </summary>
    public static PlanStep? InPassOf(RalphLogHead? head)
    {
        if (head is not { InPassStep: { Length: > 0 } words, InPassLine: >= 0 })
        {
            return null;
        }

        var same = head.InPassLines?.Where(l => string.Equals(l.Text, words, StringComparison.Ordinal)).ToList() ?? [];
        int occurrence = same.FindIndex(l => l.Line == head.InPassLine);
        return occurrence >= 0
            ? new PlanStep(0, head.InPassLine, 0, words, false, null, [], [], [], occurrence, same.Count)
            : new PlanStep(0, head.InPassLine, 0, words, false, null, [], [], []);
    }

    /// <summary>Whether <paramref name="head"/> marks a pass the last run left unjudged (a step's, or the final fix's lines). Pure.</summary>
    public static bool InPass(RalphLogHead? head) => head is not null && (head.InPassLines is not null || InPassOf(head) is not null);

    /// <summary>
    /// The checkpoint's answer written into pass <paramref name="pass"/>'s entry (2026-10-19, the fourth review: <see cref="RalphLogEntry.Checkpoint"/>
    /// was never set, so a Redo's note lived only in memory); null when written (or no such entry), else the error sentence.
    /// </summary>
    public static string? SetCheckpoint(WorkingDirectory files, string planPath, int pass, string checkpoint) => SetCheckpoint(files, planPath, pass, checkpoint, out _);

    /// <summary>
    /// <see cref="SetCheckpoint(WorkingDirectory, string, int, string)"/>, <paramref name="written"/> saying the entry now holds the answer (2026-10-20,
    /// a review: a Redo's note went to the next pass both from the loop and from this entry, through <see cref="RecentNotes"/>, in two wordings).
    /// </summary>
    public static string? SetCheckpoint(WorkingDirectory files, string planPath, int pass, string checkpoint, out bool written)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        written = false;
        var document = ReadFor(files, planPath, out string? problem);
        if (problem is not null || document is null)
        {
            return problem;
        }

        var entries = document.Entries.ToList();
        int at = entries.FindLastIndex(e => e.Pass == pass);
        if (at < 0)
        {
            return null;
        }

        entries[at] = entries[at] with { Checkpoint = CleanCheckpoint(checkpoint) };
        string? failed = Write(files, planPath, document with { Entries = entries });
        written = failed is null;
        return failed;
    }

    /// <summary>The copy of the plan as a pass left it, kept whenever the judge put lines back (2026-10-19, the fourth review).</summary>
    public const string AsLeftName = "as-left.md";

    /// <summary>Where <see cref="KeepAsLeft"/> keeps <paramref name="planPath"/>'s copy.</summary>
    public static string AsLeftPathFor(string planPath) => PlanReferences.FolderOf(planPath) + "/" + AsLeftName;

    /// <summary>
    /// <paramref name="text"/>, the plan as a pass left it, kept beside the log (the last one only), so the edits the judge put back — the user's own
    /// among them, which it cannot tell from the model's — are never lost; its path, or null when it could not be written.
    /// </summary>
    public static string? KeepAsLeft(WorkingDirectory files, string planPath, string text)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(text);
        string path = AsLeftPathFor(planPath);
        return files.WriteText(path, text, overwrite: true).Outcome == FileOutcome.Ok ? path : null;
    }

    /// <summary>The learned facts with <paramref name="added"/> after them, redacted, once each, the oldest dropped past <see cref="LearnedChars"/>.</summary>
    public static IReadOnlyList<string> MergeLearned(IReadOnlyList<string> learned, IReadOnlyList<string> added)
    {
        var all = new List<string>();
        foreach (string fact in learned.Concat(added).Select(f => Flat(Clean(f))).Where(f => f.Length > 0))
        {
            all.RemoveAll(f => string.Equals(f, fact, StringComparison.OrdinalIgnoreCase));
            all.Add(fact);
        }

        while (all.Count > 0 && all.Sum(f => f.Length + 3) > LearnedChars)
        {
            all.RemoveAt(0);
        }

        return all;
    }

    /// <summary>The learned facts as the directive carries them, a bullet each; null with none.</summary>
    public static string? LearnedText(RalphLogDocument? document) =>
        document is { Learned.Count: > 0 } d ? string.Join('\n', d.Learned.Select(f => "- " + f)) : null;

    /// <summary>
    /// The notes a pass on <paramref name="stepText"/> gets from the log: this step's earlier entries first (newest first), then the latest of the
    /// rest, at most <see cref="NotesEntries"/> and <see cref="NotesChars"/>; null with none. Each entry's summary is cut to <see cref="NoteChars"/>, and
    /// a first entry still past the room is cut to it (2026-10-20, a review: a summary over the room ended the list before its first line, so a long
    /// one dropped every note, the checkpoint's Redo with them). <paramref name="stepIndex"/> (2026-10-20, a review: two steps of the same words read
    /// each other's notes) narrows "this step's" entries to the step of that number when the plan has twins of its words.
    /// </summary>
    public static string? RecentNotes(RalphLogDocument? document, string stepText, int? stepIndex = null)
    {
        if (document is not { Entries.Count: > 0 } d)
        {
            return null;
        }

        var mine = d.Entries.Where(e => IsStep(e, stepText, stepIndex)).Reverse();
        var chosen = mine.Concat(d.Entries.Reverse().Take(1)).Distinct().Take(NotesEntries).ToList();
        var text = new StringBuilder();
        foreach (var entry in chosen)
        {
            string line = $"- pass {N(entry.Pass)} (step {N(entry.Step)}): {entry.Outcome}; {Shorten(Flat(entry.Summary), NoteChars)}" + (entry.Check is { Length: > 0 } c && c != "none" ? $"; check {c}" : "") +
                (entry.Review is { Length: > 0 } r && r != "off" ? $"; review {Flat(r)}" : "") +
                (entry.Notes is { Length: > 0 } notes ? $"; notes for the next pass: {Shorten(Flat(notes), NoteChars)}" : "") +
                // The checkpoint's answer (2026-10-19, the fourth review: a Redo's note never reached a pass after a pause, a crash or a resume), and the
                // by-hand question's in words of its own (2026-10-21).
                (entry.Checkpoint is { Length: > 0 } checkpoint ? CheckpointNote(checkpoint) : "");
            if (text.Length + line.Length + 1 > NotesChars)
            {
                if (text.Length == 0)
                {
                    text.Append(Shorten(line, NotesChars - 1)).Append('\n');
                }

                break;
            }

            text.Append(line).Append('\n');
        }

        return text.Length == 0 ? null : text.ToString().TrimEnd();
    }

    /// <summary>
    /// An entry's checkpoint field as <see cref="RecentNotes"/> words it: the checkpoint's answer as the user's choice, the by-hand question's
    /// (2026-10-21, <see cref="RalphText.ByHandLogText"/>) as what the user found, or that it waited for the user's check.
    /// </summary>
    private static string CheckpointNote(string checkpoint)
    {
        if (string.Equals(checkpoint, RalphText.ByHandWorksLog, StringComparison.Ordinal))
        {
            return RalphText.ByHandWorksNote;
        }

        if (checkpoint.StartsWith(RalphText.ByHandRedoLog, StringComparison.Ordinal))
        {
            string note = checkpoint[RalphText.ByHandRedoLog.Length..].TrimStart(':', ' ');
            return RalphText.ByHandRedoNote + (note.Length > 0 ? ": " + Shorten(Flat(note), NoteChars) : "");
        }

        if (IsAwaitingHand(checkpoint))
        {
            return RalphText.ByHandWaitedNote;
        }

        return RalphText.CheckpointChoiceNote(Shorten(Flat(checkpoint), NoteChars));
    }

    /// <summary>
    /// The log's last entry when it is <paramref name="step"/>'s (its words, and its number among twins) and holds a by-hand question nobody answered
    /// yes or redo to — <see cref="RalphText.ByHandStoppedLog"/> or <see cref="RalphText.NeedsYouLog"/> (2026-10-21, the plan at
    /// <c>~/.claude/plans/i-put-you-in-snoopy-perlis.md</c>): a resume asks it again before any pass, and <c>/plan show</c> says the loop waits. Null
    /// otherwise, and for an entry an older build wrote (it keeps no such text). Pure.
    /// </summary>
    public static RalphLogEntry? LastEntryAwaitingHand(RalphLogDocument? document, PlanStep? step)
    {
        if (document is not { Entries.Count: > 0 } d || step is null)
        {
            return null;
        }

        var last = d.Entries[^1];
        return IsStep(last, step.Text, step.Twins > 1 ? step.Index : null) && IsAwaitingHand(last.Checkpoint) ? last : null;
    }

    /// <summary>
    /// The step a by-hand question waits on and its entry: <paramref name="steps"/>' first unticked leaf (parsed from the plan file whole, as the loop
    /// reads it) when <see cref="LastEntryAwaitingHand"/> holds for it; null otherwise (2026-10-22, a review: the resume, <c>/plan show</c> and headless each
    /// wrote this out, and two of them drifted once). Pure.
    /// </summary>
    public static (PlanStep Step, RalphLogEntry Entry)? AwaitingHand(IReadOnlyList<PlanStep> steps, RalphLogDocument? document) =>
        PlanSteps.FirstUnticked(steps) is { } step && LastEntryAwaitingHand(document, step) is { } entry ? (step, entry) : null;

    /// <summary>
    /// Whether a checkpoint field holds a by-hand question nobody answered yes or redo to: <see cref="RalphText.ByHandStoppedLog"/> or
    /// <see cref="RalphText.NeedsYouLog"/> (2026-10-22, a review: two copies of the test).
    /// </summary>
    private static bool IsAwaitingHand(string? checkpoint) =>
        string.Equals(checkpoint, RalphText.ByHandStoppedLog, StringComparison.Ordinal) || string.Equals(checkpoint, RalphText.NeedsYouLog, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="entry"/> is a pass on the step of <paramref name="stepText"/> (and of <paramref name="stepIndex"/>, when given).</summary>
    private static bool IsStep(RalphLogEntry entry, string stepText, int? stepIndex) =>
        string.Equals(entry.StepText, stepText, StringComparison.Ordinal) && (stepIndex is not { } index || entry.Step == index);

    /// <summary>The highest pass number the log holds; 0 with none.</summary>
    public static int PassCount(RalphLogDocument? document) => document is { Entries.Count: > 0 } d ? d.Entries.Max(e => e.Pass) : 0;

    /// <summary>The files the passes on <paramref name="stepText"/> (of <paramref name="stepIndex"/> among twins, as <see cref="RecentNotes"/>) changed, once each, in order.</summary>
    public static IReadOnlyList<string> ChangedBy(RalphLogDocument? document, string stepText, int? stepIndex = null) =>
        document is null ? [] : document.Entries.Where(e => IsStep(e, stepText, stepIndex)).SelectMany(e => e.Changed).Distinct(StringComparer.Ordinal).ToList();

    // ── The file ───────────────────────────────────────────────────────────

    /// <summary>The log's text. Pure.</summary>
    public static string Render(RalphLogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = new StringBuilder();
        var head = document.Head;
        text.Append("# Ralph log").Append(head is null ? "" : ": " + head.Plan).Append("\n\n");
        if (head is not null)
        {
            text.Append("- plan: ").Append(head.Plan).Append('\n');
            text.Append("- verify: ").Append(head.Verify is { Length: > 0 } v ? Quote(v) : "none").Append('\n');
            text.Append("- final: ").Append(head.Final is { Length: > 0 } f ? Quote(f) : "none").Append('\n');
            text.Append("- final-only: ").Append(head.FinalOnly ? "yes" : "no").Append('\n');
            text.Append("- shell: ").Append(head.Shell).Append('\n');
            text.Append("- started: ").Append(head.Started.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append('\n');
            if (head.Stopped is { Length: > 0 } stopped)
            {
                text.Append("- stopped: ").Append(Flat(stopped)).Append('\n');
            }

            if (head is { InPassStep: { Length: > 0 } inPass, InPassLine: >= 0 })
            {
                text.Append("- in pass: ").Append(N(head.InPassLine)).Append(" · ").Append(Flat(inPass)).Append('\n');
            }

            if (head.InPassHeaded is { } headed)
            {
                text.Append("- in pass steps heading: ").Append(headed ? "yes" : "no").Append('\n');
            }

            if (head.InPassPlan is { Length: > 0 } stamp)
            {
                text.Append("- in pass plan: ").Append(Flat(stamp)).Append('\n');
            }

            text.Append('\n');
            if (head.InPassLines is { } inPassLines)
            {
                // The plan's task lines as the pass under way began: its line, its box, its words.
                text.Append(InPassHeading).Append('\n');
                foreach (var line in inPassLines)
                {
                    text.Append("- ").Append(N(line.Line)).Append(line.Ticked ? " [x] " : " [ ] ").Append(Flat(line.Text)).Append('\n');
                    if (line.DoneWhen is { Length: > 0 } doneWhen)
                    {
                        text.Append("  - ").Append(InPassDoneWhen).Append(Flat(doneWhen)).Append('\n');
                    }
                }

                text.Append('\n');
            }
        }

        text.Append(LearnedHeading).Append('\n');
        foreach (string fact in document.Learned)
        {
            text.Append("- ").Append(Flat(fact)).Append('\n');
        }

        foreach (var e in document.Entries)
        {
            text.Append('\n').Append(PassHeading).Append(N(e.Pass)).Append('\n');
            text.Append("- step: ").Append(N(e.Step)).Append(" · ").Append(Flat(e.StepText)).Append('\n');
            text.Append("- outcome: ").Append(Flat(e.Outcome)).Append('\n');
            text.Append("- summary: ").Append(Flat(e.Summary)).Append('\n');
            if (e.Notes is { Length: > 0 } notes)
            {
                text.Append("- notes: ").Append(Flat(notes)).Append('\n');
            }

            text.Append("- verified: ").Append(e.Verified.Count == 0 ? "none" : string.Join(" | ", e.Verified.Select(Flat))).Append('\n');
            text.Append("- check: ").Append(Flat(e.Check)).Append('\n');
            if (e.Flags.Count > 0)
            {
                text.Append("- flags: ").Append(string.Join(" | ", e.Flags.Select(Flat))).Append('\n');
            }

            text.Append("- changed: ").Append(e.Changed.Count == 0 ? "none" : string.Join(", ", e.Changed)).Append('\n');
            text.Append("- review: ").Append(Flat(e.Review)).Append('\n');
            if (e.Checkpoint is { Length: > 0 } checkpoint)
            {
                text.Append("- checkpoint: ").Append(Flat(checkpoint)).Append('\n');
            }

            text.Append("- at: ").Append(e.At.ToString(TimeFormat, CultureInfo.InvariantCulture));
            if (e.Elapsed is { } elapsed)
            {
                text.Append(" · ").Append(N((int)Math.Round(elapsed.TotalSeconds))).Append(" s");
            }

            if (e.Tokens is { } tokens)
            {
                text.Append(" · ").Append(tokens.ToString(CultureInfo.InvariantCulture)).Append(" tokens");
            }

            text.Append('\n');
            if (e.Tail is { Length: > 0 } tail)
            {
                text.Append('\n').Append(RalphText.Fence(tail)).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>A log's text read back, tolerant of hand edits: unknown lines skipped, a missing field its empty value. Pure.</summary>
    public static RalphLogDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var headFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var learned = new List<string>();
        var entries = new List<RalphLogEntry>();
        string section = "head";
        Dictionary<string, string>? fields = null;
        int pass = 0;
        StringBuilder? tail = null;
        string? fence = null;
        List<PlanStep>? inPassLines = null;

        void Flush()
        {
            if (fields is null)
            {
                return;
            }

            string step = fields.GetValueOrDefault("step") ?? "";
            int dot = step.IndexOf(" · ", StringComparison.Ordinal);
            int stepNumber = int.TryParse(dot < 0 ? step : step[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : 0;
            string stepText = dot < 0 ? "" : step[(dot + 3)..];
            string at = fields.GetValueOrDefault("at") ?? "";
            var parts = at.Split(" · ");
            var when = DateTimeOffset.TryParseExact(parts[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTimeOffset.MinValue;
            TimeSpan? elapsed = parts.FirstOrDefault(p => p.EndsWith(" s", StringComparison.Ordinal)) is { } secs && int.TryParse(secs[..^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec) ? TimeSpan.FromSeconds(sec) : null;
            long? tokens = parts.FirstOrDefault(p => p.EndsWith(" tokens", StringComparison.Ordinal)) is { } tok && long.TryParse(tok[..^7], NumberStyles.Integer, CultureInfo.InvariantCulture, out long tk) ? tk : null;
            entries.Add(new RalphLogEntry(
                pass, stepNumber, stepText,
                fields.GetValueOrDefault("outcome") ?? "",
                fields.GetValueOrDefault("summary") ?? "",
                List(fields.GetValueOrDefault("verified"), " | "),
                fields.GetValueOrDefault("check") ?? "",
                tail is { Length: > 0 } ? tail.ToString().TrimEnd('\n') : null,
                List(fields.GetValueOrDefault("flags"), " | "),
                List(fields.GetValueOrDefault("changed"), ", "),
                fields.GetValueOrDefault("review") ?? "",
                fields.GetValueOrDefault("checkpoint"),
                when, elapsed, tokens,
                fields.GetValueOrDefault("notes") is { Length: > 0 } notes ? notes : null));
            fields = null;
            tail = null;
        }

        foreach (string raw in lines)
        {
            if (fence is not null)
            {
                if (raw.Trim() == fence)
                {
                    fence = null;
                }
                else
                {
                    tail?.Append(raw).Append('\n');
                }

                continue;
            }

            string line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fence = new string('`', line.TakeWhile(c => c == '`').Count());
                if (fields is not null)
                {
                    tail = new StringBuilder();
                }

                continue;
            }

            if (line.StartsWith(PassHeading, StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                section = "pass";
                pass = int.TryParse(line[PassHeading.Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : 0;
                fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (line.Equals(LearnedHeading, StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                section = "learned";
                continue;
            }

            if (line.Equals(InPassHeading, StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                section = "inpass";
                inPassLines ??= [];
                continue;
            }

            if (!line.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            string item = line[2..];
            if (section == "learned")
            {
                learned.Add(item.Trim());
                continue;
            }

            if (section == "inpass")
            {
                // "done when: words" under a line: its check (2026-10-19, the fifth review).
                if (item.StartsWith(InPassDoneWhen, StringComparison.OrdinalIgnoreCase))
                {
                    if (inPassLines!.Count > 0 && item[InPassDoneWhen.Length..].Trim() is { Length: > 0 } words)
                    {
                        inPassLines[^1] = inPassLines[^1] with { DoneWhen = words };
                    }

                    continue;
                }

                // "12 [x] words": a line that does not read so is skipped (a hand edit).
                int space = item.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0 && int.TryParse(item[..space], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lineAt) && lineAt >= 0
                    && item[(space + 1)..] is { Length: >= 3 } box && box[0] == '[' && box[2] == ']' && box[1] is ' ' or 'x' or 'X')
                {
                    inPassLines!.Add(new PlanStep(0, lineAt, 0, box[3..].Trim(), box[1] != ' ', null, [], [], []));
                }

                continue;
            }

            int colon = item.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string key = item[..colon].Trim();
            string value = item[(colon + 2)..].Trim();
            if (section == "pass" && fields is not null)
            {
                fields[key] = value;
            }
            else if (section == "head")
            {
                headFields[key] = value;
            }
        }

        Flush();
        RalphLogHead? head = headFields.TryGetValue("plan", out string? plan)
            ? new RalphLogHead(
                plan,
                CheckOf(headFields.GetValueOrDefault("verify")),
                CheckOf(headFields.GetValueOrDefault("final")),
                string.Equals(headFields.GetValueOrDefault("final-only"), "yes", StringComparison.OrdinalIgnoreCase),
                headFields.GetValueOrDefault("shell") ?? "",
                DateTimeOffset.TryParseExact(headFields.GetValueOrDefault("started"), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var started) ? started : DateTimeOffset.MinValue,
                headFields.GetValueOrDefault("stopped"))
            : null;
        if (head is not null && headFields.GetValueOrDefault("in pass") is { } inPass && inPass.IndexOf(" · ", StringComparison.Ordinal) is int at and > 0
            && int.TryParse(inPass[..at], NumberStyles.Integer, CultureInfo.InvariantCulture, out int inPassLine) && inPassLine >= 0 && inPass[(at + 3)..].Trim() is { Length: > 0 } inPassStep)
        {
            head = head with { InPassLine = inPassLine, InPassStep = inPassStep };
        }

        if (head is not null && headFields.GetValueOrDefault("in pass plan") is { Length: > 0 } planStamp)
        {
            head = head with { InPassPlan = planStamp };
        }

        if (head is not null && inPassLines is not null)
        {
            head = head with { InPassLines = inPassLines };
            if (headFields.GetValueOrDefault("in pass steps heading") is { } headedWord)
            {
                head = head with { InPassHeaded = string.Equals(headedWord, "yes", StringComparison.OrdinalIgnoreCase) };
            }
        }

        return new RalphLogDocument(head, learned, entries);
    }

    // ── The lock ───────────────────────────────────────────────────────────

    /// <summary>
    /// What a lock says: the process, when it started, and the machine it runs on (<paramref name="Host"/>, 2026-10-20, a review: a plan in a shared or
    /// synced folder was locked by a pid on another PC, which this one read as a process of its own that was gone, took over, and ran the plan beside
    /// it). A lock with no host (one the previous build wrote) is this machine's.
    /// </summary>
    public sealed record LockOwner(int ProcessId, DateTimeOffset Started, string? Host = null)
    {
        /// <summary>Whether the lock was written on another machine (whose processes this one cannot see).</summary>
        public bool Foreign => Host is { Length: > 0 } host && !string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Lazy<LockOwner> s_me = new(() =>
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        return new LockOwner(Environment.ProcessId, new DateTimeOffset(me.StartTime.ToUniversalTime(), TimeSpan.Zero), Environment.MachineName);
    });

    /// <summary>This process as a lock names it (the screen's and headless's one owner, 2026-10-19, the second review: each kept a copy).</summary>
    public static LockOwner Me => s_me.Value;

    /// <summary>
    /// Whether a lock's owner still runs: a process of its id started within 2 s of the time it wrote (an id reused later is another process). A
    /// process of that id whose start time cannot be read (another user's, an elevated one: access denied) counts as the owner (2026-10-19, the
    /// third review: read as gone, a live loop's lock was taken over and two loops ran one plan); only a process that is gone frees the lock.
    /// </summary>
    public static bool OwnerAlive(LockOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Foreign)
        {
            // Another machine's process: never seen from here, so never taken for gone (the user deletes a lock its crash left: LockError says where).
            return true;
        }

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.GetProcessById(owner.ProcessId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // No process of that id runs.
            return false;
        }

        using (process)
        {
            try
            {
                return Math.Abs((process.StartTime.ToUniversalTime() - owner.Started.UtcDateTime).TotalSeconds) < 2;
            }
            catch (InvalidOperationException)
            {
                // It ended meanwhile.
                return false;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// The lock of <paramref name="planPath"/> taken for <paramref name="me"/>: true when it was free, stale (<paramref name="alive"/> says its owner is
    /// gone) or already this process's; false while another live process holds it. Taken by creating the file whole, never over one that is there
    /// (2026-10-19, the review: read-then-write let two instances starting together both take it): published whole, only where none is
    /// (<see cref="TryCreate"/>), so of two racing for it one fails, and a reader finds it whole or not at all. A stale lock is removed only
    /// while it still says what was read (under the take-over guard, <see cref="RemoveIfStill"/>), then the take tried again: of several
    /// takers, the one that removes it and publishes first holds it, and the rest read that one's.
    /// </summary>
    public static bool TryLock(WorkingDirectory files, string planPath, LockOwner me, Func<LockOwner, bool> alive) => TryLock(files, planPath, me, alive, out _);

    /// <summary>
    /// <see cref="TryLock(WorkingDirectory, string, LockOwner, Func{LockOwner, bool})"/>, <paramref name="refusal"/> the error sentence when it
    /// failed for want of an atomic publish on the plan's volume (<see cref="RalphText.LockUnsupportedError"/>, later on 2026-10-20, the code review),
    /// not because another holds it; null otherwise (the caller then says <see cref="LockError"/>).
    /// </summary>
    public static bool TryLock(WorkingDirectory files, string planPath, LockOwner me, Func<LockOwner, bool> alive, out string? refusal)
    {
        refusal = null;
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(alive);
        string path = LockPathFor(planPath);
        if (files.Resolve(path, forWrite: true, out string full) != FileOutcome.Ok)
        {
            return false;
        }

        string text = "pid: " + N(me.ProcessId) + "\nstarted: " + me.Started.ToString("O", CultureInfo.InvariantCulture) + "\n" + (me.Host is { Length: > 0 } host ? "host: " + host + "\n" : "");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var published = TryCreate(full, text);
            if (published == PublishOutcome.Published)
            {
                return true;
            }

            if (published == PublishOutcome.Unsupported)
            {
                refusal = RalphText.LockUnsupportedError(planPath, path);
                return false;
            }

            var (state, held) = ReadLockAt(full);
            if (state == LockRead.Missing)
            {
                // Let go since: take it again.
                continue;
            }

            // A lock that cannot be read is never taken for a stale one (2026-10-19, the third review: read as no owner, it matched "not a lock").
            if (state == LockRead.Unreadable || (held is not null && (held == me || alive(held))))
            {
                return held == me;
            }

            // Stale (its owner gone) or not a lock at all: removed while it still says so, then taken again.
            if (!RemoveIfStill(full, held, out bool unsupported))
            {
                refusal = unsupported ? RalphText.LockUnsupportedError(planPath, path) : null;
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a running loop still holds <paramref name="planPath"/>'s lock, asked at each pass's top (2026-10-20, a review: the lock was read only as
    /// the loop began, so a pass that deleted it, every tool being kept, let a second instance start the same plan while the first ran on): this
    /// process's still, or taken again when it went; false while another live process holds it (the loop stops, <see cref="RalphStop.Locked"/>).
    /// </summary>
    public static bool StillHeld(WorkingDirectory files, string planPath, Func<LockOwner, bool> alive) => TryLock(files, planPath, Me, alive);

    /// <summary>What publishing a lock (or a take-over guard) found: published, one there already (or not written), or no atomic way on its volume.</summary>
    internal enum PublishOutcome
    {
        Published,
        NotTaken,
        Unsupported,
    }

    /// <summary>
    /// <paramref name="text"/> as a new file at <paramref name="full"/>: <see cref="PublishOutcome.NotTaken"/> when one is there (or it could not be
    /// written). Written whole to a temp file of its own beside it, then published under the lock's name without ever replacing one (2026-10-20, the
    /// code review): see <see cref="Publish"/>. The temp file is removed after, published or not. A reader so finds the lock whole or not at all, and
    /// of any number of racers exactly one publishes. Until then it was created in place with <see cref="FileMode.CreateNew"/>: a reader could find it
    /// empty or half written (read as held for a few seconds, then as garbage a second taker removed, so a stalled writer and that taker both ran), and
    /// off Windows <see cref="FileShare.None"/> is no share mode but an <c>flock</c> taken after the <c>O_EXCL</c> create, so a reader's shared
    /// <c>flock</c> in between failed the creator's and nobody held it. The move alone had been tried before that, and on macOS .NET's move without
    /// overwrite is a look then a rename: of eight racers all eight took it (the Mac's gate).
    /// </summary>
    private static PublishOutcome TryCreate(string full, string text)
    {
        string temp = full + "." + N(Environment.ProcessId) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(temp, text);
            return Publish(temp, full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PublishOutcome.NotTaken;
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>
    /// <paramref name="temp"/> made <paramref name="full"/>, never over a file there. On Windows by <c>File.Move(overwrite: false)</c>, MoveFileEx
    /// without a replace, atomic there (a share's server makes it so too). Off Windows by <c>link(2)</c>, which fails with <c>EEXIST</c> on a name
    /// already there, in one step; where the volume takes no hard links (later on 2026-10-20, the code review: it fell back to the racy look-then-move),
    /// on a Mac by <c>renamex_np(RENAME_EXCL)</c>, the same in one rename. Measured on the Mac's own disk images: FAT32 refuses the link (ENOTSUP)
    /// and takes the exclusive rename, EEXIST over a lock there; exFAT refuses both (ENOTSUP). Where neither holds the lock is
    /// <see cref="PublishOutcome.Unsupported"/> and the start is refused (<see cref="RalphText.LockUnsupportedError"/>), rather than taken by a
    /// move two instances could both win: a lock that can be held twice is no lock, and only a plan on exFAT (or a share whose client takes neither)
    /// loses its loop, not a common home for a project. An O_EXCL guard around a move was weighed and left: it serializes the takers only while no
    /// crash leaves the guard, and clearing a stale one is a race of its own.
    /// </summary>
    private static PublishOutcome Publish(string temp, string full)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(temp, full, overwrite: false);   // IOException when one is there: TryCreate's NotTaken
            return PublishOutcome.Published;
        }

        var outcome = PublishOutcomeOf(LinkCall(temp, full));
        if (outcome == PublishOutcome.Unsupported && OperatingSystem.IsMacOS())
        {
            outcome = PublishOutcomeOf(RenameExclusiveCall(temp, full));
        }

        if (outcome == PublishOutcome.Unsupported)
        {
            DiagnosticLog.Warn("Plan", "ralph lock: the volume of " + full + " takes neither a hard link nor an exclusive rename; the lock is refused");
        }

        return outcome;
    }

    /// <summary>
    /// What a publishing call's answer means (0, else its errno): published; <see cref="PublishOutcome.Unsupported"/> for the errnos that say the
    /// volume has no such call (EPERM, which Linux's link gives on FAT; ENOTSUP and EOPNOTSUPP, macOS's and Linux's); anything else (EEXIST: held;
    /// the folder gone, no write access) not taken. Pure.
    /// </summary>
    internal static PublishOutcome PublishOutcomeOf(int errno) => errno switch
    {
        0 => PublishOutcome.Published,
        Eperm or EnotsupMac or EopnotsuppMac or EopnotsuppLinux => PublishOutcome.Unsupported,
        _ => PublishOutcome.NotTaken,
    };

    // The errno values that say a volume has no such call: macOS's and Linux's EPERM, macOS's ENOTSUP and EOPNOTSUPP, Linux's EOPNOTSUPP.
    private const int Eperm = 1;
    private const int EnotsupMac = 45;
    private const int EopnotsuppMac = 102;
    private const int EopnotsuppLinux = 95;

    // renamex_np's flag (<sys/stdio.h>): fail with EEXIST rather than replace what is there.
    private const uint RenameExcl = 0x00000004;

    /// <summary><c>link(2)</c>: 0, else its errno. A seam for the tests (the volume's answer injected).</summary>
    internal static Func<string, string, int> LinkCall { get; set; } = (existing, created) => Link(existing, created) == 0 ? 0 : Marshal.GetLastPInvokeError();

    /// <summary><c>renamex_np(RENAME_EXCL)</c>, a Mac's only: 0, else its errno. A seam for the tests.</summary>
    internal static Func<string, string, int> RenameExclusiveCall { get; set; } = RenameExclusive;

    /// <summary>The Mac's exclusive rename itself (macOS 10.12+, in libSystem as link is).</summary>
    internal static int RenameExclusive(string from, string to) => RenameX(from, to, RenameExcl) == 0 ? 0 : Marshal.GetLastPInvokeError();

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Link(string existing, string created);

    [LibraryImport("libc", EntryPoint = "renamex_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameX(string from, string to, uint flags);

    /// <summary>How old a take-over guard is before it counts as left by a process that died holding it.</summary>
    private static readonly TimeSpan GuardStale = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The lock at <paramref name="full"/> removed when it still names <paramref name="held"/> (or is gone already); false when another took it
    /// meanwhile, or is taking it over now. Done under a take-over guard beside it (<c>ralph.lock.takeover</c>, created whole as the lock is), so one
    /// taker at a time reads and removes (2026-10-19, the third review: a read then a delete let one of two instances taking over the same stale
    /// lock delete the other's fresh one, and both ran). Under the guard the read and the delete cannot be raced: a fresh lock is only ever
    /// created where none is, and only a guard holder removes one it did not write. A guard older than <see cref="GuardStale"/> was left by a crash
    /// and goes.
    /// </summary>
    private static bool RemoveIfStill(string full, LockOwner? held, out bool unsupported)
    {
        string guard = full + ".takeover";
        var guarded = TryCreate(guard, N(Environment.ProcessId) + "\n");
        unsupported = guarded == PublishOutcome.Unsupported;
        if (guarded != PublishOutcome.Published)
        {
            try
            {
                if (File.Exists(guard) && DateTime.UtcNow - File.GetLastWriteTimeUtc(guard) > GuardStale)
                {
                    File.Delete(guard);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Its holder lets it go.
            }

            return false;
        }

        try
        {
            var (state, now) = ReadLockAt(full);
            if (state == LockRead.Missing)
            {
                return true;
            }

            if (state == LockRead.Unreadable || now != held)
            {
                return false;
            }

            File.Delete(full);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            DeleteQuietly(guard);
        }
    }

    private static void DeleteQuietly(string full)
    {
        try
        {
            File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray file beside the lock: never read as the lock.
        }
    }

    /// <summary>What reading a lock file found: none there, one that could not be read, or one read (its owner null when it is not a lock).</summary>
    private enum LockRead
    {
        Missing,
        Unreadable,
        Read,
    }

    /// <summary>
    /// The lock file at a full path read, sharing every access so a taker's read never stops another's delete. Always whole, being published
    /// whole (<see cref="TryCreate"/>), so what it says is what its writer wrote: one that names no owner is not a lock.
    /// </summary>
    private static (LockRead State, LockOwner? Owner) ReadLockAt(string full)
    {
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return (LockRead.Read, ParseLock(reader.ReadToEnd()));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (LockRead.Missing, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (LockRead.Unreadable, null);
        }
    }

    /// <summary>The lock let go when it is <paramref name="me"/>'s (a stop, a profile switch, the app's exit).</summary>
    public static void Unlock(WorkingDirectory files, string planPath, LockOwner me)
    {
        ArgumentNullException.ThrowIfNull(files);
        string path = LockPathFor(planPath);
        if (ReadLock(files, path) == me && files.Resolve(path, forWrite: true, out string full) == FileOutcome.Ok)
        {
            try
            {
                File.Delete(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left behind: stale once this process is gone, and taken over then.
            }
        }
    }

    /// <summary>Whether a lock's owner still runs, for <see cref="HeldElsewhere"/> (a seam for the tests: <see cref="OwnerAlive"/>).</summary>
    internal static Func<LockOwner, bool> LiveOwner { get; set; } = OwnerAlive;

    /// <summary>
    /// The error sentence when another live process holds <paramref name="planPath"/>'s lock, else null (none, a stale one, or this process's own):
    /// what every write of a plan file asks first (2026-10-19, the fourth review: the lock was read only as a loop started, so <c>/plan open</c>, a
    /// revision or an approve in a second instance wrote the plan a loop was running in the first, and the loop put the edits back as a pass's). A
    /// lock that cannot be read counts as held, as <see cref="TryLock"/> reads it.
    /// </summary>
    public static string? HeldElsewhere(WorkingDirectory files, string planPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(planPath);
        if (files.Resolve(LockPathFor(planPath), forWrite: false, out string full) != FileOutcome.Ok)
        {
            return null;
        }

        var (state, owner) = ReadLockAt(full);
        return state == LockRead.Missing || (state == LockRead.Read && (owner is null || owner == Me || !LiveOwner(owner))) ? null : LockError(files, planPath);
    }

    /// <summary>
    /// The error sentence for a lock this process could not take: another machine's names that machine and where the lock is, so the user can delete
    /// one its crash left (2026-10-20, a review); else <see cref="RalphText.LockedError"/>.
    /// </summary>
    public static string LockError(WorkingDirectory files, string planPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        string path = LockPathFor(planPath);
        return files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && ReadLockAt(full) is (LockRead.Read, { Foreign: true, Host: { } host })
            ? RalphText.LockedOnHostError(planPath, host, path)
            : RalphText.LockedError(planPath);
    }

    private static LockOwner? ReadLock(WorkingDirectory files, string path)
    {
        var read = files.ReadText(path, null, null);
        return read.Outcome == FileOutcome.Ok ? ParseLock(read.Text) : null;
    }

    private static LockOwner? ParseLock(string text)
    {
        int pid = 0;
        DateTimeOffset started = DateTimeOffset.MinValue;
        string? host = null;
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("pid: ", StringComparison.Ordinal))
            {
                int.TryParse(line[5..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
            }
            else if (line.StartsWith("started: ", StringComparison.Ordinal))
            {
                DateTimeOffset.TryParse(line[9..].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started);
            }
            else if (line.StartsWith("host: ", StringComparison.Ordinal) && line[6..].Trim() is { Length: > 0 } named)
            {
                host = named;
            }
        }

        return pid > 0 ? new LockOwner(pid, started, host) : null;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>A pass's notes from two places (the loop's pending one, the log's recent ones), a line apart; null with neither.</summary>
    public static string? JoinNotes(string? first, string? second) =>
        first is null ? second : second is null ? first : first + "\n" + second;

    /// <summary>A check's output as the log keeps it: its last <see cref="TailLines"/> lines, scrubbed and redacted.</summary>
    public static string CleanTail(string tail)
    {
        var lines = UntrustedText.Scrub(tail).TrimEnd().Split('\n');
        return ToolRedaction.Text(string.Join('\n', lines.Skip(Math.Max(0, lines.Length - TailLines))));
    }

    private static string Clean(string text) => ToolRedaction.Text(UntrustedText.Scrub(text));

    private static string? CheckOf(string? text) => text is null || text == "none" ? null : Unquote(text);

    private static IReadOnlyList<string> List(string? text, string separator) =>
        text is null || text == "none" || text.Length == 0 ? [] : text.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string Quote(string text) => PlanDocument.Quote(text);

    private static string Unquote(string text) => PlanDocument.Unquote(text);

    private static string Flat(string text) => PlanDocument.OneLine(text);

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
