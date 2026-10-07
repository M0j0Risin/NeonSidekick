using NeonSidekick.Shell;

namespace NeonSidekick.Viewer;

/// <summary>
/// The process window (2026-10-05, the user's pick from the external windows brainstorm: <c>/process &lt;id&gt;</c>): a background
/// process's output live, the log window's engine (<see cref="LogWindowThread"/>) over a <see cref="ProcessFeed"/> — following
/// the newest line while at the bottom, scrolled, selected and copied as the log is, stderr in the warning colour, the title
/// the process's state. The user's choices: one window for every process (<c>/process</c> with another id switches it, in the
/// same place), opened only when asked (never on a background start), and read-only but for Ctrl+K pressed twice
/// (<see cref="ProcessKillArm"/>), which stops the process through the registry's notified exit, so the chat prints the line
/// and the model hears of it. Its own place (<see cref="Position"/>); no native code of its own, so the smoke's
/// <c>viewer:log-window</c> covers it.
/// </summary>
public static class ProcessWindow
{
    private static readonly Lock s_gate = new();
    private static LogWindowThread? s_open;
    private static ProcessSession? s_session;

    /// <summary>Where the window was when it last closed (<c>Program</c>: the profile's <c>ProcessWindowLeft</c> / <c>ProcessWindowTop</c>); null is Windows' own place.</summary>
    public static Func<(int X, int Y)?>? Position { get; set; }

    /// <summary>Told the window's corner as it closes, on its thread. It must not block.</summary>
    public static Action<int, int>? Placed { get; set; }

    /// <summary>Whether a window can be opened here at all: on Windows, and on a Mac with a window server since 2026-10-07 (<see cref="MacLineWindows"/>).</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows() || (OperatingSystem.IsMacOS() && AppKitHost.IsEnabled);

    /// <summary>
    /// The window on <paramref name="session"/>: the open one brought forward when it shows that session already, else the open
    /// one switched to it in place (<see cref="LogWindowThread.Swap"/>), at the bottom and following; a new one only when none is
    /// open. <paramref name="stop"/>
    /// is Ctrl+K's second press, on the window's thread. Throws <see cref="InvalidOperationException"/> with the reason when no
    /// window could be made.
    /// </summary>
    public static void Show(ProcessSession session, Action<ProcessSession> stop)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(stop);
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException(ProcessWindowText.UnavailableHere);
        }

        if (OperatingSystem.IsMacOS())
        {
            MacLineWindows.ShowProcess(session, stop);
            return;
        }

        lock (s_gate)
        {
            if (s_open is { Alive: true } open)
            {
                if (ReferenceEquals(s_session, session) && open.Raise())
                {
                    return;
                }

                // Another process: the open window swapped onto it (2026-10-05, the code review: a close and a reopen held the
                // caller for up to twelve seconds, a slash command under a reply among them). Only a window gone meanwhile opens anew.
                if (open.Swap(new ProcessFeed(session, stop, TimeProvider.System)))
                {
                    s_session = session;
                    return;
                }
            }

            var window = new LogWindowThread(new ProcessFeed(session, stop, TimeProvider.System), "Process window", Position, Placed);
            s_open = window;
            s_session = session;
            window.Start();
        }
    }

    /// <summary>The open window closed and waited for briefly (the app's exit); true when one was open.</summary>
    public static bool Close()
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacLineWindows.CloseProcess();
        }

        LogWindowThread? open;
        lock (s_gate)
        {
            open = s_open;
            s_open = null;
            s_session = null;
        }

        bool alive = open is { Alive: true };
        open?.Close();
        return alive;
    }
}
