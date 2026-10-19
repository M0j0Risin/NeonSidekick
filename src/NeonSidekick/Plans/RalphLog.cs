using System.Globalization;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Plans;

/// <summary>
/// A Ralph log's head: what the run was fixed to, when it began, and how its last run stopped (null while one runs). <paramref name="InPassLine"/>
/// and <paramref name="InPassStep"/> (2026-10-19, the third review: a crash, a kill or a reset mid-pass left a box the model ticked standing with no
/// check, and a resume passed the step by) name the step a pass is working while it runs — its line in the plan file and its words — cleared once
/// the pass is judged; a start that finds them set puts that step's box back first (<see cref="RalphLog.InPassOf"/>).
/// </summary>
public sealed record RalphLogHead(string Plan, string? Verify, string? Final, bool FinalOnly, string Shell, DateTimeOffset Started, string? Stopped = null, int InPassLine = -1, string? InPassStep = null);

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
public static class RalphLog
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
    /// <paramref name="problem"/> the error sentence when a file is there that cannot be read, so a write never replaces it.
    /// </summary>
    private static RalphLogDocument? ReadFor(WorkingDirectory files, string planPath, out string? problem)
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
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(learned);
        var document = ReadFor(files, planPath, out string? problem) ?? RalphLogDocument.Empty;
        if (problem is not null)
        {
            return problem;
        }

        var entries = document.Entries.ToList();
        entries.Add(entry with { Summary = Clean(entry.Summary), Verified = entry.Verified.Select(Clean).ToList(), Tail = entry.Tail is { } tail ? CleanTail(tail) : null, Notes = entry.Notes is { } notes ? Clean(notes) : null });
        return Write(files, planPath, new RalphLogDocument(head, MergeLearned(document.Learned, learned), entries));
    }

    /// <summary>The head set (a start, a resume, a stop), the rest as it was; null when written.</summary>
    public static string? SetHead(WorkingDirectory files, string planPath, RalphLogHead head)
    {
        var document = ReadFor(files, planPath, out string? problem) ?? RalphLogDocument.Empty;
        return problem ?? Write(files, planPath, document with { Head = head });
    }

    /// <summary>The head's mark of a pass under way set (<paramref name="step"/>) or cleared (null), the rest as it was; null when written (or no head to mark).</summary>
    public static string? MarkInPass(WorkingDirectory files, string planPath, PlanStep? step)
    {
        var document = ReadFor(files, planPath, out string? problem);
        if (problem is not null || document?.Head is not { } head)
        {
            return problem;
        }

        var marked = step is null ? head with { InPassLine = -1, InPassStep = null } : head with { InPassLine = step.Line, InPassStep = Flat(step.Text) };
        return marked == head ? null : Write(files, planPath, document with { Head = marked });
    }

    /// <summary>
    /// The step a pass was working when the run ended without judging it (its head's mark, <see cref="RalphLogHead.InPassStep"/>): a step of no
    /// children, unticked as it began, at its line with its words (else the one task line of its words); null with no mark. Pure.
    /// </summary>
    public static PlanStep? InPassOf(RalphLogHead? head) =>
        head is { InPassStep: { Length: > 0 } words, InPassLine: >= 0 } ? new PlanStep(0, head.InPassLine, 0, words, false, null, [], [], []) : null;

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
    /// rest, at most <see cref="NotesEntries"/> and <see cref="NotesChars"/>; null with none.
    /// </summary>
    public static string? RecentNotes(RalphLogDocument? document, string stepText)
    {
        if (document is not { Entries.Count: > 0 } d)
        {
            return null;
        }

        var mine = d.Entries.Where(e => string.Equals(e.StepText, stepText, StringComparison.Ordinal)).Reverse();
        var chosen = mine.Concat(d.Entries.Reverse().Take(1)).Distinct().Take(NotesEntries).ToList();
        var text = new StringBuilder();
        foreach (var entry in chosen)
        {
            string line = $"- pass {N(entry.Pass)} (step {N(entry.Step)}): {entry.Outcome}; {Flat(entry.Summary)}" + (entry.Check is { Length: > 0 } c && c != "none" ? $"; check {c}" : "") +
                (entry.Review is { Length: > 0 } r && r != "off" ? $"; review {Flat(r)}" : "") +
                (entry.Notes is { Length: > 0 } notes ? $"; notes for the next pass: {Shorten(Flat(notes), NoteChars)}" : "");
            if (text.Length + line.Length + 1 > NotesChars)
            {
                break;
            }

            text.Append(line).Append('\n');
        }

        return text.Length == 0 ? null : text.ToString().TrimEnd();
    }

    /// <summary>The highest pass number the log holds; 0 with none.</summary>
    public static int PassCount(RalphLogDocument? document) => document is { Entries.Count: > 0 } d ? d.Entries.Max(e => e.Pass) : 0;

    /// <summary>The files the passes on <paramref name="stepText"/> changed, once each, in order.</summary>
    public static IReadOnlyList<string> ChangedBy(RalphLogDocument? document, string stepText) =>
        document is null ? [] : document.Entries.Where(e => e.StepText == stepText).SelectMany(e => e.Changed).Distinct(StringComparer.Ordinal).ToList();

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

            text.Append('\n');
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

        return new RalphLogDocument(head, learned, entries);
    }

    // ── The lock ───────────────────────────────────────────────────────────

    /// <summary>What a lock says: the process and when it started.</summary>
    public sealed record LockOwner(int ProcessId, DateTimeOffset Started);

    private static readonly Lazy<LockOwner> s_me = new(() =>
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        return new LockOwner(Environment.ProcessId, new DateTimeOffset(me.StartTime.ToUniversalTime(), TimeSpan.Zero));
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
    /// (2026-10-19, the review: read-then-write let two instances starting together both take it): its text is written beside it and moved into
    /// place without overwriting, so of two racing for it one fails, and a reader never sees it half written. A stale lock is removed only while it
    /// still says what was read, then the take tried again.
    /// </summary>
    public static bool TryLock(WorkingDirectory files, string planPath, LockOwner me, Func<LockOwner, bool> alive)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(alive);
        string path = LockPathFor(planPath);
        if (files.Resolve(path, forWrite: true, out string full) != FileOutcome.Ok)
        {
            return false;
        }

        string text = "pid: " + N(me.ProcessId) + "\nstarted: " + me.Started.ToString("O", CultureInfo.InvariantCulture) + "\n";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (TryCreate(full, text))
            {
                return true;
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
            if (!RemoveIfStill(full, held))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary><paramref name="text"/> as a new file at <paramref name="full"/>: false when one is there (or it could not be written).</summary>
    private static bool TryCreate(string full, string text)
    {
        string temp = full + "." + N(Environment.ProcessId) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(temp, text);
            File.Move(temp, full, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stray temp file: harmless, never read as the lock.
            }
        }
    }

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
    private static bool RemoveIfStill(string full, LockOwner? held)
    {
        string guard = full + ".takeover";
        if (!TryCreate(guard, N(Environment.ProcessId) + "\n"))
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

    /// <summary>The lock file at a full path read, sharing every access so a taker's read never stops another's delete.</summary>
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

    private static LockOwner? ReadLock(WorkingDirectory files, string path)
    {
        var read = files.ReadText(path, null, null);
        return read.Outcome == FileOutcome.Ok ? ParseLock(read.Text) : null;
    }

    private static LockOwner? ParseLock(string text)
    {
        int pid = 0;
        DateTimeOffset started = DateTimeOffset.MinValue;
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
        }

        return pid > 0 ? new LockOwner(pid, started) : null;
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

    private static string Quote(string text) => "\"" + Flat(text).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Unquote(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (int i = 1; i < text.Length - 1; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length - 1)
            {
                i++;
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    private static string Flat(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
