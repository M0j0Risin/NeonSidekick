using System.Globalization;

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

    /// <summary>A stop with running dependents (error 1051), naming them. Pinned.</summary>
    public static string DependentsRunning(string name, IReadOnlyList<string> dependents) =>
        $"Error: {name} cannot be stopped while services that depend on it run: {string.Join(", ", dependents)}.";

    /// <summary>The approval question: the action, the service, the computer, the account and the full image path. Pinned.</summary>
    public static string ConfirmQuestion(string action, string name, string display, string computer, string account, string imagePath) =>
        $"{Act(action)} {name} ({display}) on {computer} as {account}? It runs {imagePath}";

    /// <summary>The approval question for a start type change. Pinned.</summary>
    public static string ConfirmStartType(string name, string display, string computer, string account, string from, string to) =>
        $"Change {name} ({display}) on {computer} from {from} to {to} start, as {account}?";

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
