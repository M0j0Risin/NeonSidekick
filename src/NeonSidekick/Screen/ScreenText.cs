using System.Globalization;
using System.Text;

namespace NeonSidekick.Screen;

/// <summary>
/// The screen capture's user-visible wording (2026-10-04): the targets, the failures, the result lines the transcript and the
/// model read, the allow pane, <c>/screen</c>. The <c>*Text.cs</c> shape, <c>CameraText</c>'s twin.
/// </summary>
public static class ScreenText
{
    public const string Category = "Screen";

    // ── Targets ─────────────────────────────────────────────────────────────

    /// <summary>The targets as the tool's schema and the usage line name them. Pinned (prompt text).</summary>
    public const string Targets = "screen (the monitor this app is on, the default), all (every monitor), monitor:N, window:<id or title words>, behind (the window behind this app)";

    public static string BadTarget(string raw) => $"'{raw}' is not a screen target. Use " + Targets + ".";

    public static string BadMonitor(string raw) => $"'{raw}' is not a monitor number (monitor:1, monitor:2…).";

    public static string NoSuchMonitor(int number, int count) =>
        $"There is no monitor {N(number)}: this computer has {N(count)} (monitor:1" + (count > 1 ? $" to monitor:{N(count)}" : "") + ").";

    public static string NoSuchWindow(string words) => $"No window's title has '{words}' (screen_list or /screen list shows them).";

    // ── /screen's argument list (2026-10-04, the user's report: it had none) ──

    /// <summary>The words <c>/screen</c>'s list offers first, each with its note, in this order. Pinned.</summary>
    public static readonly IReadOnlyList<(string Word, string Note)> CompletionWords =
    [
        ("list", "list the monitors and windows"),
        ("screen", "the monitor the app is on"),
        ("all", "every monitor"),
        ("behind", "the window behind the app"),
        (MonitorPrefix, "a monitor by number"),
        (WindowPrefix, "a window by id or title"),
    ];

    /// <summary>The two words a target follows without a space: <c>monitor:2</c>, <c>window:1234</c>.</summary>
    public const string MonitorPrefix = "monitor:";
    public const string WindowPrefix = "window:";

    /// <summary>A monitor's note on the list: <c>2560x1440, primary, this app's</c>. Pinned.</summary>
    public static string MonitorNote(ScreenMonitor monitor, int? ownMonitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return Size(monitor.Bounds) + (monitor.Primary ? ", primary" : "") + (monitor.Number == ownMonitor ? ", this app's" : "");
    }

    /// <summary>A window's note on the list: <c>"notes.txt - Notepad" (Notepad)</c>, as <see cref="List"/> names it. Pinned.</summary>
    public static string WindowNote(ScreenWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return $"\"{window.Title}\"" + (window.Process.Length > 0 ? $" ({window.Process})" : "");
    }

    /// <summary>A monitor's or a window's id as the targets write it.</summary>
    public static string Id(long value) => N(value);

    /// <summary>The words more than one window has: the first few listed, with their ids, so the next call can name one. Pinned.</summary>
    public static string AmbiguousWindow(string words, IReadOnlyList<ScreenWindow> some, int total)
    {
        ArgumentNullException.ThrowIfNull(some);
        var text = new StringBuilder($"{N(total)} windows' titles have '{words}'; name one by its id: ");
        text.Append(string.Join("; ", some.Select(w => $"window:{N(w.Id)} \"{w.Title}\"" + (w.Process.Length > 0 ? $" ({w.Process})" : ""))));
        if (total > some.Count)
        {
            text.Append($"; and {N(total - some.Count)} more");
        }

        return text.Append('.').ToString();
    }

    public const string NothingBehind = "There is no window behind this app's.";

    public const string NoMonitors = "Windows lists no monitor.";

    public const string WindowGone = "That window is gone.";

    public const string WindowMinimized = "That window is minimized: restore it first.";

    public const string Unsupported = "There is no screen capture on this system (Windows only).";

    public static string Failed(string detail) => $"The screen could not be captured ({detail}).";

    public const string TimedOut = "The screen capture did not finish in time (a window that stopped responding?).";

    public static string NotSaved(string error) => "The screenshot could not be saved: " + error;

    // ── What a target came to ───────────────────────────────────────────────

    /// <summary><c>monitor 2 (2560x1440, primary)</c>; <c>, this app's</c> for the default. Pinned.</summary>
    public static string MonitorDescribed(ScreenMonitor monitor, bool own)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return $"monitor {N(monitor.Number)} ({Size(monitor.Bounds)}" + (monitor.Primary ? ", primary" : "") + (own ? ", this app's" : "") + ")";
    }

    public static string AllDescribed(int monitors, ScreenRect area) => $"all {N(monitors)} monitor{(monitors == 1 ? "" : "s")} ({Size(area)})";

    /// <summary><c>window "Untitled - Notepad" (notepad)</c>. Pinned.</summary>
    public static string WindowDescribed(ScreenWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return $"window \"{window.Title}\"" + (window.Process.Length > 0 ? $" ({window.Process})" : "");
    }

    // ── screen_capture ──────────────────────────────────────────────────────

    public const string DefaultPrompt = "The model would like to see your screen.";

    /// <summary>
    /// The tool's result for a screenshot: <c>screenshot of monitor 1 (2560x1440, primary) as 2048x1152, saved as
    /// screen_images/20261004-140203.jpg: the picture is in the next message</c> (the <c>view_image</c> shape). Pinned.
    /// </summary>
    public static string Taken(ScreenShot shot)
    {
        ArgumentNullException.ThrowIfNull(shot);
        return $"screenshot of {shot.Described} as {N(shot.Image.Width)}x{N(shot.Image.Height)}, saved as {shot.RelativePath}: the picture is in the next message";
    }

    public const string Denied = "The user did not allow a screenshot. Carry on without it, and do not ask for one again unless they ask.";

    public const string AlreadyDeclined = "The user already declined a screenshot in this turn; carry on without it.";

    public const string NoScreen = "Error: screen_capture needs the app's screen to ask the user; there is none here.";

    public static string ToolFailed(string message) => "Error: " + message;

    /// <summary>The allow pane (<c>Screen capture ask</c> <c>ask</c>): its title, and the caption naming what would be captured.</summary>
    public const string AllowTitle = "The model asks to see your screen";

    public static string AllowCaption(string described, string prompt) => $"{Capitalized(described)}: {prompt}";

    // ── screen_list ─────────────────────────────────────────────────────────

    /// <summary>The most windows <c>screen_list</c> and <c>/screen list</c> name.</summary>
    public const int MaxListed = 40;

    /// <summary>The targets as lines: the monitors, then the windows front to back with their ids. Pinned (the model reads it).</summary>
    public static IReadOnlyList<string> List(IReadOnlyList<ScreenMonitor> monitors, int? ownMonitor, IReadOnlyList<ScreenWindow> windows, long? ownWindow)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(windows);
        var lines = new List<string> { "Monitors:" };
        lines.AddRange(monitors.Select(m => $"  monitor:{N(m.Number)}  {Size(m.Bounds)} at {N(m.Bounds.Left)},{N(m.Bounds.Top)}" + (m.Primary ? ", primary" : "") + (m.Number == ownMonitor ? ", this app's" : "")));
        lines.Add(windows.Count == 0 ? "Windows: none" : "Windows (front to back):");
        foreach (var w in windows.Take(MaxListed))
        {
            lines.Add($"  window:{N(w.Id)}  \"{w.Title}\"" + (w.Process.Length > 0 ? $" ({w.Process})" : "") + $" {Size(w.Bounds)}" + (w.Id == ownWindow ? ", this app" : ""));
        }

        if (windows.Count > MaxListed)
        {
            lines.Add($"  and {N(windows.Count - MaxListed)} more");
        }

        return lines;
    }

    // ── The transcript ──────────────────────────────────────────────────────

    public const string Glyph = "🖥️";

    /// <summary>The note under the model's call for a screenshot it got: <c>🖥️ screen_images/20261004-140203.jpg</c>.</summary>
    public static string Note(string relativePath) => Glyph + " " + relativePath;

    public static string Attached(string relativePath) => $"{Glyph} {relativePath} is on the input line.";

    /// <summary>The stored session's stand-in for a screenshot (<c>Screen capture keep in sessions</c> off). Pinned.</summary>
    public static string NotKept(string path) => $"[screenshot not kept in the session: {path}]";

    // ── /screen ─────────────────────────────────────────────────────────────

    public const string Word = "/screen";

    public const string HelpSummary = "capture a monitor or a window and put it on the input line, or /screen list";

    public const string Usage = "Usage: /screen [screen | all | monitor:N | window:<id or title words> | behind | list]";

    public const string Capturing = "Capturing the screen…";

    public const string NeedsScreen = "/screen needs the app's screen for that; headless has /screen list.";

    private static string Size(ScreenRect r) => $"{N(r.Width)}x{N(r.Height)}";

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
