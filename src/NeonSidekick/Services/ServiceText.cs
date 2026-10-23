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
    /// <summary>A failure count with no reset period (2026-10-23, G13; here since the branch review of DP7).</summary>
    public const string FailureCountNeverResets = "the failure count never resets";

    /// <summary>The visible dependents past the first 100, counted (2026-10-23, I9).</summary>
    public static string MoreNotListed(int count) => count.ToString(CultureInfo.InvariantCulture) + " more not listed";

    /// <summary>Which failure a recovery action answers (2026-10-23, G13): <c>1st failure</c>, <c>2nd failure</c>, and the last <c>later failures</c>.</summary>
    public static string FailureOrdinal(int index, int count)
    {
        // The last action answers its failure and every later one (Windows repeats it).
        string nth = index switch { 0 => "1st", 1 => "2nd", 2 => "3rd", _ => (index + 1).ToString(CultureInfo.InvariantCulture) + "th" };
        return count == 1 ? "every failure" : index == count - 1 ? nth + " and later failures" : nth + " failure";
    }

    /// <summary>A change whose connect ran out of time before it began (2026-10-23, B2): nothing was changed. Pinned.</summary>
    public static string NotChangedTimedOut(string name, string computer) => $"Error: {computer} did not answer in time, so {name} was not changed.";

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

    public static string NotBoolean(string argument, string raw) => Llm.Tools.ArgumentText.NotBoolean(argument, raw);

    public static string OutOfRange(string argument, string given, int min, int max) => Llm.Tools.ArgumentText.OutOfRange(argument, given, min, max);

    public static string BadState(string word) => $"Error: state '{Llm.Tools.ArgumentText.Echo(word)}' is not running, stopped or failed.";

    public static string BadStart(string word) => $"Error: start '{Llm.Tools.ArgumentText.Echo(word)}' is not automatic, delayed, manual, disabled, boot or system.";

    public static string BadAction(string word) => $"Error: action '{Llm.Tools.ArgumentText.Echo(word)}' is not start, stop or restart.";

    /// <summary>
    /// A name no service can have (2026-10-20, a review: a NUL ended the name Windows read, past the checks made on the whole): empty, over
    /// <see cref="ServiceAccess.MaxNameLength"/> characters, or holding a control character, a slash or a backslash. Pinned.
    /// </summary>
    public static string BadName(string name) =>
        $"Error: '{UntrustedText.OneLine(UntrustedText.Cut(name, 80))}' is not a service name; a service's name has no control characters, slashes or backslashes and is at most {ServiceAccess.MaxNameLength.ToString(CultureInfo.InvariantCulture)} characters.";

    public static string BadStartChoice(string word) => $"Error: start_type '{Llm.Tools.ArgumentText.Echo(word)}' is not automatic, delayed, manual or disabled.";

    public const string Canceled = "Error: the read was canceled.";

    public static string TimedOut(string computer) => $"Error: the read on {computer} took too long and was stopped.";

    // ── Writes ───────────────────────────────────────────────────────────────

    public const string WritesOff = "Error: Service writes is off (" + MenuPath + "), so no service can be changed.";

    /// <summary>A driver: never controlled. Pinned.</summary>
    public static string Driver(string name) => $"Error: '{name}' is a driver; drivers are never changed from here.";

    /// <summary>A protected service, naming which list. Pinned.</summary>
    public static string ProtectedByApp(string name) => $"Error: {name} is protected by the app; it is never stopped, restarted or changed in start type from here.";

    public static string ProtectedByFile(string name) => $"Error: {name} is protected on this computer; it is never stopped, restarted or changed in start type from here.";

    /// <summary>A detail's dependents when the account may not list them (2026-10-15). Pinned.</summary>
    public const string DependentsUnknown = "not listed (this account may not list them here)";

    /// <summary>
    /// A stop with running dependents (error 1051), naming the ones the computer's <c>services</c> list shows and counting the rest
    /// (2026-10-20, a review: the hidden ones were named too). Pinned.
    /// </summary>
    public static string DependentsRunning(string name, IReadOnlyList<string> dependents, int hidden = 0) =>
        $"Error: {UntrustedText.Name(name)} cannot be stopped while services that depend on it run: {string.Join(", ", dependents.Select(UntrustedText.Name).Append(hidden > 0 ? NotShown(hidden) : null).OfType<string>())}.";

    /// <summary>The related services the computer's <c>services</c> list hides, counted (2026-10-20). Pinned.</summary>
    public static string NotShown(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " service" : " services") + " not shown here";

    /// <summary>
    /// The approval question: the action, the service and the computer on its title row, then the display name, the account and the full image
    /// path under it, each on one line (2026-10-20, a review: a newline or a run of spaces in what a service's installer wrote hid the rest of the
    /// row). Pinned.
    /// </summary>
    /// <remarks>
    /// Since 2026-10-23 (the admin review): <paramref name="runsAs"/> is the account the service runs as (G3: it said the app's connecting
    /// account); <paramref name="dll"/> a shared service's DLL (T10); <paramref name="alsoStarts"/> the stopped dependencies a start starts too (M3).
    /// </remarks>
    public static string ConfirmQuestion(string action, string name, string display, string computer, string runsAs, string imagePath, string? dll = null, IReadOnlyList<string>? alsoStarts = null) =>
        $"{Act(action)} {UntrustedText.Name(name)} on {UntrustedText.Name(computer)}?\n{UntrustedText.Name(display)}, runs as {UntrustedText.Name(runsAs)}. It runs {UntrustedText.Name(imagePath)}"
        + (dll is null ? "" : " with " + UntrustedText.Name(dll))
        + (alsoStarts is { Count: > 0 } ? "\nThis also starts: " + string.Join(", ", alsoStarts.Select(UntrustedText.Name)) : "");

    /// <summary>
    /// The approval question for a start type change, its title row and the lines under it. Since 2026-10-23: the program and its DLL (L2, T10:
    /// a disabled service made automatic showed no program), and for a disable the services that depend on it (M4). Pinned.
    /// </summary>
    public static string ConfirmStartType(string name, string display, string computer, string runsAs, string from, string to, string? imagePath = null, string? dll = null, IReadOnlyList<string>? dependents = null) =>
        $"Change {UntrustedText.Name(name)} on {UntrustedText.Name(computer)} from {from} to {to} start?\n{UntrustedText.Name(display)}, runs as {UntrustedText.Name(runsAs)}."
        + (imagePath is null ? "" : " It runs " + UntrustedText.Name(imagePath) + (dll is null ? "" : " with " + UntrustedText.Name(dll)) + ".")
        + (dependents is { Count: > 0 } ? "\nServices that depend on it: " + string.Join(", ", dependents.Select(UntrustedText.Name)) : "");

    /// <summary>What a shared service's DLL reads as when it could not be read (Remote Registry off, no rights; T10). Pinned.</summary>
    public const string DllUnread = "a DLL that could not be read (its registry key)";

    /// <summary>A start whose stopped dependency the checks refuse (M3): the dependency is started too, so its refusal is the start's. Pinned.</summary>
    public static string DependencyRefused(string name, string dependency, string refusal) =>
        $"Error: starting {UntrustedText.Name(name)} would also start {UntrustedText.Name(dependency)}, which may not be changed here: " + refusal.Replace("Error: ", "", StringComparison.Ordinal);

    /// <summary>A start whose chain of stopped dependencies passes the cap (M3). Pinned.</summary>
    public static string TooManyDependencies(string name, int cap) =>
        $"Error: starting {UntrustedText.Name(name)} would start more than {cap.ToString(CultureInfo.InvariantCulture)} stopped services it depends on; start them yourself with /services.";

    /// <summary>A start that ended stopped (G1): failed, with its exit code. Pinned.</summary>
    public static string StartFailed(string action, string name, string computer, uint exit, uint serviceExit) =>
        $"Error: {(action == ServiceActions.Restart ? "the restart of" : "the start of")} {UntrustedText.Name(name)} on {computer} failed: it stopped again (exit {ExitWords(exit, serviceExit)}).";

    /// <summary>The pane's note after a copy (2026-10-23, the admin review's T19: the panes' words lived inline). Pinned.</summary>
    public static string CopiedNotice(string name) => "(" + Glyph + " copied " + name + ")";

    /// <summary>The pane's note when ask can draft nothing (T19). Pinned.</summary>
    public const string AskOffNotice = "Service tools is off (/tools services): the model could not read the service, so nothing was drafted.";

    /// <summary>The start types the pane offers, in its order (T19).</summary>
    public static readonly IReadOnlyList<string> StartTypeWords = ["automatic", "delayed", "manual", "disabled"];

    /// <summary><c>/services</c>' printed header (T19). Pinned.</summary>
    public static string CommandHeader(string where, int count, string state) =>
        "Services on " + where + ": " + count.ToString(CultureInfo.InvariantCulture) + " (" + state + ")";

    /// <summary>What a restart's stop applied, for <see cref="RestartStartFailed"/> (G2).</summary>
    public const string StoppedApplied = "stopped";

    /// <summary>A restart that stopped the service, then failed to start it (G2): the service is down now. Pinned.</summary>
    public static string RestartStartFailed(string name, string computer, string failure) =>
        $"Error: {UntrustedText.Name(name)} on {computer} was stopped, but starting it again failed, so it is stopped now. " + failure;

    private static string ExitWords(uint exit, uint serviceExit) => exit == 1066 ? "service error " + serviceExit.ToString(CultureInfo.InvariantCulture) : exit.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The model's change refused because its question would not show what runs whole (2026-10-20, a review): an image path over
    /// <see cref="ComputerText.MaxShownInQuestion"/> characters. Pinned.
    /// </summary>
    public static string TooLongToAsk(string name, int length) =>
        $"Error: {UntrustedText.Name(name)}'s program line is {length.ToString(CultureInfo.InvariantCulture)} characters, too long to show whole in the question; change it yourself with /services.";

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
    /// <remarks>Since 2026-10-23 (T14) the reason is named: the stop's wait ran out, you stopped waiting, or its state could not be read.</remarks>
    public static string RestartNotStarted(string name, string computer, ServiceRunState state, RestartStop why = RestartStop.TimedOut) => why switch
    {
        RestartStop.Cancelled => $"Error: the wait for {name} on {computer} to stop was stopped (it is {StateWord(state)}), so it was not started again; start it once it has stopped.",
        RestartStop.Unreadable => $"Error: {name} on {computer} was asked to stop, but its state could not be read after, so it was not started again; check it and start it if needed.",
        _ => $"Error: {name} on {computer} did not stop in time (it is {StateWord(state)}), so it was not started again; start it once it has stopped.",
    };

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
