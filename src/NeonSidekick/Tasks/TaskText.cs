using System.Globalization;
using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tasks;

/// <summary>
/// The scheduled tasks' visible strings (2026-10-15, the admin-tools plan): the log category, the glyph, the menu path, the tools' refusals and
/// headers, the guards' sentences, the approval questions, the error sentences (<see cref="TaskErrors"/>), the history sentence and
/// <c>/tasks</c>' words. Pinned by the tests.
/// </summary>
public static class TaskText
{
    public const string Category = "Tasks";

    /// <summary>The group's glyph: a calendar (the toolbar's, <c>/tasks</c>' title).</summary>
    public const string Glyph = "📅";

    public const string MenuPath = "/tools › Admin › Tasks";

    public const string Working = "Reading the scheduled tasks…";

    /// <summary>The refusal off Windows. Pinned.</summary>
    public const string NoneHere = "Error: there are no scheduled tasks to read here.";

    public const string NoneHereReason = "there are no scheduled tasks to read here";

    /// <summary>The Task Scheduler's own history log.</summary>
    public const string HistoryLog = "Microsoft-Windows-TaskScheduler/Operational";

    /// <summary>The history log turned off (Windows' default). Pinned.</summary>
    public const string HistoryOff = "Task history is off on this computer; Task Scheduler's Enable All Tasks History turns it on.";

    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n', StringComparison.Ordinal);
        string first = newline < 0 ? result : result[..newline];
        return "(" + Glyph + " " + (first.Length > 160 ? first[..159] + "…" : first) + ")";
    }

    public static string WithheldFromAdvisor(string tool, string? path) =>
        "(" + tool + (string.IsNullOrWhiteSpace(path) ? "" : " " + path.Trim()) + ": read it with your own task tools)";

    // ── Refusals of a call ──────────────────────────────────────────────────

    public static string Missing(string argument) => $"Error: {argument} is required.";

    public static string NotBoolean(string argument, string raw) => $"Error: {argument} '{raw}' is not true or false.";

    public static string OutOfRange(string argument, string given, int min, int max) =>
        $"Error: {argument} '{given}' is out of range; it must be {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.";

    public static string BadState(string word) => $"Error: state '{word}' is not ready, running, disabled or queued.";

    public static string BadAction(string word) => $"Error: action '{word}' is not run, end, enable or disable.";

    /// <summary>
    /// A path no task can have (2026-10-20, a review: <c>\Backup\../Secret/Task</c> passed a folder pattern's star): holding a control
    /// character or a slash, an empty, <c>.</c> or <c>..</c> part, or over 1,024 characters. Pinned.
    /// </summary>
    public static string BadPath(string path) =>
        $"Error: '{UntrustedText.OneLine(UntrustedText.Cut(path, 120))}' is not a task path; a path is \\folder\\name with no slashes, control characters or . and .. parts.";

    public static string BadFolder(string folder) => $"Error: folder '{folder}' must start with \\.";

    public static string TimedOut(string computer) => $"Error: the read on {computer} took too long and was stopped.";

    public const string XmlTooLarge = "the task's definition is over 1 MB and was not read";

    public static string XmlUnreadable(string detail) => "the task's definition could not be read: " + detail;

    // ── Writes ───────────────────────────────────────────────────────────────

    public const string WritesOff = "Error: Task writes is off (" + MenuPath + "), so no task can be changed.";

    /// <summary>
    /// A task under <c>\Microsoft\</c> that no <c>control</c> pattern starting there matches (2026-10-20, a review: any such pattern on the
    /// computer opened every Windows task to a broader one). Pinned.
    /// </summary>
    public static string MicrosoftTask(string path) =>
        $"Error: {path} is one of Windows' own tasks; it can be changed only when a \"control\" pattern on this computer that starts with \\Microsoft\\ matches it.";

    /// <summary>A privileged run, end or enable refused. Pinned.</summary>
    public static string Privileged(string path, string principal) =>
        $"Error: {path} runs as {principal}, a privileged principal; running, ending or enabling it from here needs \"privileged\": true on this computer's entry in tasks.json.";

    /// <summary>
    /// The approval question for a run: the path and the computer on its title row, then the principal, the run level and every command line
    /// under it, each on one line (2026-10-20, a review: a command padded with spaces hid its tail), since a run is a privilege boundary. Pinned.
    /// </summary>
    public static string ConfirmRun(string path, string computer, string principal, string runLevel, string commands) =>
        $"Run {UntrustedText.OneLine(path)} on {computer} now?\nIt runs as {principal} ({runLevel}): {commands}";

    /// <summary>The approval question for end, enable or disable, the principal under its title row. Pinned.</summary>
    public static string ConfirmChange(string action, string path, string computer, string principal) =>
        $"{Act(action)} {UntrustedText.OneLine(path)} on {computer}?\nIt runs as {principal}.";

    /// <summary>What a run's question says of the commands when the definition could not be read (2026-10-20, a review: it said "(no actions)"). Pinned.</summary>
    public const string CommandsUnknown = "what it runs is not known (its definition could not be read)";

    /// <summary>Who a task runs as in its question when the definition could not be read (2026-10-20). Pinned.</summary>
    public const string PrincipalUnknown = "an account not known";

    /// <summary>A run level in that question when the definition could not be read (2026-10-20). Pinned.</summary>
    public const string RunLevelUnknown = "run level not known";

    /// <summary>
    /// The model's run refused because its question would not show the commands whole (2026-10-20, a review): over
    /// <see cref="ComputerText.MaxShownInQuestion"/> characters. Pinned.
    /// </summary>
    public static string TooLongToAsk(string path, int length) =>
        $"Error: {UntrustedText.OneLine(path)}'s command lines are {length.ToString(CultureInfo.InvariantCulture)} characters, too long to show whole in the question; run it yourself with /tasks.";

    /// <summary>A change made whose task could not be read after it (2026-10-20, a review: it was answered and audited as a failure). Pinned.</summary>
    public static string DoneUnread(string action, string path, string computer) =>
        $"{Past(action)} {path} on {computer}; the task could not be read after the change.";

    public static string Act(string action) => action switch
    {
        TaskActions.Run => "Run",
        TaskActions.End => "End",
        TaskActions.Enable => "Enable",
        _ => "Disable",
    };

    /// <summary>A change done. Pinned.</summary>
    public static string Done(string action, string path, string computer, TaskRunState state) =>
        $"{Past(action)} {path} on {computer}: {StateWord(state)}.";

    public static string Past(string action) => action switch
    {
        TaskActions.Run => "Ran",
        TaskActions.End => "Ended",
        TaskActions.Enable => "Enabled",
        _ => "Disabled",
    };

    public static string AuditAct(string action, string path) => action + " task " + path;

    // ── Words ───────────────────────────────────────────────────────────────

    public static string StateWord(TaskRunState state) => state switch
    {
        TaskRunState.Disabled => "disabled",
        TaskRunState.Queued => "queued",
        TaskRunState.Ready => "ready",
        TaskRunState.Running => "running",
        _ => "unknown",
    };

    /// <summary>A computer's own clock time (COM's DATE carries no zone): <c>2026-10-15 03:00</c>, or "never".</summary>
    public static string When(DateTime? time) => time is { } t ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "never";

    public static string TestOk(string name, string server, int count) =>
        $"Reached '{name}' ({server}): {count.ToString(CultureInfo.InvariantCulture)} tasks outside \\Microsoft\\.";
}
