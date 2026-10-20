using System.Globalization;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Services;

/// <summary>
/// The services' visible strings (2026-10-15, the admin-tools plan): the log category, the glyph, the menu path, the tools' refusals and
/// headers, the error sentences (<see cref="ServiceErrors"/>), the approval question, the advisors' stand-in and <c>/services</c>' words.
/// Pinned by the tests.
/// </summary>
public static class ServiceText
{
    public const string Category = "Services";

    /// <summary>The group's glyph: a puzzle piece (the toolbar's, <c>/services</c>' title).</summary>
    public const string Glyph = "🧩";

    /// <summary>Where the services' rows live.</summary>
    public const string MenuPath = "/tools › Admin › Services";

    public const string Working = "Reading the services…";

    /// <summary>The refusal off Windows (the house rule: say what is missing here). Pinned.</summary>
    public const string NoneHere = "Error: there are no services to read here.";

    /// <summary>Why the group is not offered off Windows. Pinned.</summary>
    public const string NoneHereReason = "there are no services to read here";

    /// <summary>The transcript's note for a services tool's result: its first line.</summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n', StringComparison.Ordinal);
        string first = newline < 0 ? result : result[..newline];
        return "(" + Glyph + " " + (first.Length > 160 ? first[..159] + "…" : first) + ")";
    }

    /// <summary>What an advisor reads in place of a services tool's result. Pinned.</summary>
    public static string WithheldFromAdvisor(string tool, string? name) =>
        "(" + tool + (string.IsNullOrWhiteSpace(name) ? "" : " " + name.Trim()) + ": read it with your own service tools)";

    // ── Refusals of a call ──────────────────────────────────────────────────

    public static string Missing(string argument) => $"Error: {argument} is required.";

    public static string NotBoolean(string argument, string raw) => $"Error: {argument} '{raw}' is not true or false.";

    public static string OutOfRange(string argument, string given, int min, int max) =>
        $"Error: {argument} '{given}' is out of range; it must be {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.";

    public static string BadState(string word) => $"Error: state '{word}' is not running, stopped or failed.";

    public static string BadStart(string word) => $"Error: start '{word}' is not automatic, delayed, manual, disabled, boot or system.";

    public static string BadAction(string word) => $"Error: action '{word}' is not start, stop or restart.";

    /// <summary>
    /// A name no service can have (2026-10-20, a review: a NUL ended the name Windows read, past the checks made on the whole): empty, over
    /// <see cref="ServiceAccess.MaxNameLength"/> characters, or holding a control character, a slash or a backslash. Pinned.
    /// </summary>
    public static string BadName(string name) =>
        $"Error: '{UntrustedText.OneLine(UntrustedText.Cut(name, 80))}' is not a service name; a service's name has no control characters, slashes or backslashes and is at most {ServiceAccess.MaxNameLength.ToString(CultureInfo.InvariantCulture)} characters.";

    public static string BadStartChoice(string word) => $"Error: start_type '{word}' is not automatic, delayed, manual or disabled.";

    public const string Canceled = "Error: the read was canceled.";

    public static string TimedOut(string computer) => $"Error: the read on {computer} took too long and was stopped.";

    // ── Writes ───────────────────────────────────────────────────────────────

    public const string WritesOff = "Error: Service writes is off (" + MenuPath + "), so no service can be changed.";

    /// <summary>A driver: never controlled. Pinned.</summary>
    public static string Driver(string name) => $"Error: '{name}' is a driver; drivers are never changed from here.";

    /// <summary>A protected service, naming which list. Pinned.</summary>
    public static string ProtectedByApp(string name) => $"Error: {name} is protected by the app; it is never stopped, restarted or changed in start type from here.";

    public static string ProtectedByFile(string name) => $"Error: {name} is protected on this computer (services.json); it is never stopped, restarted or changed in start type from here.";

    /// <summary>A detail's dependents when the account may not list them (2026-10-15). Pinned.</summary>
    public const string DependentsUnknown = "not listed (this account may not list them here)";

    /// <summary>
    /// A stop with running dependents (error 1051), naming the ones the computer's <c>services</c> list shows and counting the rest
    /// (2026-10-20, a review: the hidden ones were named too). Pinned.
    /// </summary>
    public static string DependentsRunning(string name, IReadOnlyList<string> dependents, int hidden = 0) =>
        $"Error: {name} cannot be stopped while services that depend on it run: {string.Join(", ", dependents.Select(UntrustedText.OneLine).Append(hidden > 0 ? NotShown(hidden) : null).OfType<string>())}.";

    /// <summary>The related services the computer's <c>services</c> list hides, counted (2026-10-20). Pinned.</summary>
    public static string NotShown(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " service" : " services") + " not shown here";

    /// <summary>
    /// The approval question: the action, the service and the computer on its title row, then the display name, the account and the full image
    /// path under it, each on one line (2026-10-20, a review: a newline or a run of spaces in what a service's installer wrote hid the rest of the
    /// row). Pinned.
    /// </summary>
    public static string ConfirmQuestion(string action, string name, string display, string computer, string account, string imagePath) =>
        $"{Act(action)} {UntrustedText.OneLine(name)} on {computer}?\n{UntrustedText.OneLine(display)}, as {UntrustedText.OneLine(account)}. It runs {UntrustedText.OneLine(imagePath)}";

    /// <summary>The approval question for a start type change, its title row and the line under it. Pinned.</summary>
    public static string ConfirmStartType(string name, string display, string computer, string account, string from, string to) =>
        $"Change {UntrustedText.OneLine(name)} on {computer} from {from} to {to} start?\n{UntrustedText.OneLine(display)}, as {UntrustedText.OneLine(account)}.";

    /// <summary>
    /// The model's change refused because its question would not show what runs whole (2026-10-20, a review): an image path over
    /// <see cref="ComputerText.MaxShownInQuestion"/> characters. Pinned.
    /// </summary>
    public static string TooLongToAsk(string name, int length) =>
        $"Error: {UntrustedText.OneLine(name)}'s program line is {length.ToString(CultureInfo.InvariantCulture)} characters, too long to show whole in the question; change it yourself with /services.";

    /// <summary>The verb as a question starts it.</summary>
    public static string Act(string action) => action switch
    {
        ServiceActions.Start => "Start",
        ServiceActions.Stop => "Stop",
        ServiceActions.Restart => "Restart",
        _ => "Change",
    };

    /// <summary>A change done: <c>Started Spooler on this PC: running.</c> Pinned.</summary>
    public static string Done(string action, string name, string computer, ServiceRunState state) =>
        $"{Past(action)} {name} on {computer}: {StateWord(state)}.";

    public static string StartTypeDone(string name, string computer, string to) => $"Set {name} on {computer} to {to} start.";

    /// <summary>A change made whose state could not be read after it (2026-10-20, a review: it was answered and audited as a failure). Pinned.</summary>
    public static string DoneUnread(string action, string name, string computer) =>
        $"{Past(action)} {name} on {computer}; its state could not be read after the change.";

    /// <summary>A restart whose stop did not end in time or was stopped waiting: the start is not tried (2026-10-20, a review). Pinned.</summary>
    public static string RestartNotStarted(string name, string computer, ServiceRunState state) =>
        $"Error: {name} on {computer} did not stop in time (it is {StateWord(state)}), so it was not started again; start it once it has stopped.";

    /// <summary>A change that took effect in part: what did, then the failure's sentence (2026-10-20, a review). Pinned.</summary>
    public static string Partly(string name, string computer, string applied, string failure) =>
        $"Error: {name} on {computer} now has {applied} start, but the rest of the change failed. {failure}";

    /// <summary>The state the poll gave up at (the wait hint ran out). Pinned.</summary>
    public static string StillPending(string action, string name, string computer, ServiceRunState state) =>
        $"{Past(action)} {name} on {computer}, but it is still {StateWord(state)}; check it again shortly.";

    public static string Past(string action) => action switch
    {
        ServiceActions.Start => "Started",
        ServiceActions.Stop => "Stopped",
        ServiceActions.Restart => "Restarted",
        _ => "Changed",
    };

    /// <summary>The audit's act: <c>stop service Spooler</c>.</summary>
    public static string AuditAct(string action, string name) => (action == ServiceActions.StartType ? "change the start type of" : action) + " service " + name;

    // ── Words ───────────────────────────────────────────────────────────────

    public static string StateWord(ServiceRunState state) => state switch
    {
        ServiceRunState.Stopped => "stopped",
        ServiceRunState.StartPending => "starting",
        ServiceRunState.StopPending => "stopping",
        ServiceRunState.Running => "running",
        ServiceRunState.ContinuePending => "resuming",
        ServiceRunState.PausePending => "pausing",
        ServiceRunState.Paused => "paused",
        _ => "unknown",
    };

    public static string StartWord(ServiceStartType start, bool delayed) => start switch
    {
        ServiceStartType.Automatic => delayed ? "delayed" : "automatic",
        ServiceStartType.Manual => "manual",
        ServiceStartType.Disabled => "disabled",
        ServiceStartType.Boot => "boot",
        ServiceStartType.System => "system",
        _ => "unknown",
    };

    public static string ChoiceWord(ServiceStartChoice choice) => choice switch
    {
        ServiceStartChoice.Automatic => "automatic",
        ServiceStartChoice.Delayed => "delayed",
        ServiceStartChoice.Manual => "manual",
        _ => "disabled",
    };

    /// <summary>A recovery action in words.</summary>
    public static string FailureAction(ServiceFailureAction action) => action.Type switch
    {
        0 => "take no action",
        1 => "restart the service after " + Seconds(action.DelayMs),
        2 => "restart the computer after " + Seconds(action.DelayMs),
        3 => "run the command after " + Seconds(action.DelayMs),
        _ => "action " + action.Type.ToString(CultureInfo.InvariantCulture),
    };

    private static string Seconds(uint ms) => (ms / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s";

    // ── Config and the wizard ───────────────────────────────────────────────

    public static string TestOk(string name, int count, int running) =>
        $"Reached '{name}': {count.ToString(CultureInfo.InvariantCulture)} services, {running.ToString(CultureInfo.InvariantCulture)} running.";
}
