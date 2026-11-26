namespace NeonSidekick.Diagnostics;

/// <summary>
/// The two process-wide switches of a private conversation (2026-11-25, the private-conversations plan), beside <see cref="DiagnosticLog"/> since the
/// writers that read them are static and run on any thread. <see cref="HideLog"/>: the <c>--log</c> file and <c>crash.log</c> show
/// <see cref="Placeholder"/> for what is said (<c>Private log text</c> <c>hide</c>), set at entry and cleared once the ended conversation's jobs have
/// stopped. <see cref="Active"/>: a private conversation is on, whatever that setting says — the clipboard writers mark their copies by it, on Windows and the Mac. Both off
/// in the tests' <c>ModuleInit</c>, and put back by a scope after a test that goes private (the suite is one process).
/// </summary>
public static class PrivateMode
{
    /// <summary>What a hidden line's text reads. Pinned.</summary>
    public const string Placeholder = "[private]";

    private static volatile bool _hideLog;
    private static volatile bool _active;
    private static volatile bool _hidAtExit;

    /// <summary>
    /// Whether the log hid a private conversation's words when the run's exit ended it (2026-11-26, the review's #34/#72): set by the exit's end and a
    /// headless private run's before they let the hide go, so a crash entry written after (the run's catch, the domain handler during the exit's flush)
    /// still keeps no message. Never cleared in a run; the tests' module init resets it.
    /// </summary>
    public static bool HidAtExit
    {
        get => _hidAtExit;
        set => _hidAtExit = value;
    }

    /// <summary>Whether a crash entry leaves messages out: the hide on now, or on when the exit ended a private conversation.</summary>
    public static bool HideCrash => _hideLog || _hidAtExit;

    /// <summary>A log line's text as shown outside the log file (the echo, headless stderr): the placeholder while hiding, unless an audit line.</summary>
    public static string Shown(DiagnosticEvent evt)
    {
        return _hideLog && !evt.Audit ? Placeholder : evt.Message;
    }

    /// <summary>Whether the log hides what is said.</summary>
    public static bool HideLog
    {
        get => _hideLog;
        set => _hideLog = value;
    }

    /// <summary>Whether a private conversation is on.</summary>
    public static bool Active
    {
        get => _active;
        set => _active = value;
    }
}
