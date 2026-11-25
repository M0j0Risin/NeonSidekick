namespace NeonSidekick.Diagnostics;

/// <summary>
/// The two process-wide switches of a private conversation (2026-11-25, the private-conversations plan), beside <see cref="DiagnosticLog"/> since the
/// writers that read them are static and run on any thread. <see cref="HideLog"/>: the <c>--log</c> file and <c>crash.log</c> show
/// <see cref="Placeholder"/> for what is said (<c>Private log text</c> <c>hide</c>), set at entry and cleared once the ended conversation's jobs have
/// stopped. <see cref="Active"/>: a private conversation is on, whatever that setting says — the clipboard writers mark their copies by it. Both off
/// in the tests' <c>ModuleInit</c>, and put back by a scope after a test that goes private (the suite is one process).
/// </summary>
public static class PrivateMode
{
    /// <summary>What a hidden line's text reads. Pinned.</summary>
    public const string Placeholder = "[private]";

    private static volatile bool _hideLog;
    private static volatile bool _active;

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
