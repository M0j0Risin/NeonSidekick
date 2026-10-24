using NeonSidekick.Docker;

namespace NeonSidekick.Viewer;

/// <summary>What <see cref="DockerLogWindow.Show"/> does with the window: bring the open one forward, swap it onto the container, or open one.</summary>
public enum DockerLogWindowMove
{
    Raise,
    Swap,
    Open,
}

/// <summary>
/// The Docker log window (2026-10-23, the user's ask: "/docker logs &lt;container&gt; … tail the container log in an external
/// window similar to how we do for /log", and the <c>/docker</c> pane's logs button the same): a container's log live, the log
/// window's engine (<see cref="LogWindowThread"/>) over a <see cref="DockerLogFeed"/>, as <see cref="ProcessWindow"/> is for a
/// process. Following the newest line while at the bottom, scrolled, selected and copied as the log is, stderr in the warning
/// colour, the title the container and what the feed is doing. One window for every container: another container swaps it in
/// place; the same container with the same count brings it forward. Its own place (<see cref="Position"/>); no native code of its
/// own, so the smoke's <c>viewer:log-window</c> covers it. On a Mac <see cref="MacLineWindows"/> holds it.
/// </summary>
public static class DockerLogWindow
{
    private static readonly Lock s_gate = new();
    private static LogWindowThread? s_open;
    private static DockerLogFeed? s_feed;

    /// <summary>Where the window was when it last closed (<c>Program</c>: the profile's <c>DockerLogWindowLeft</c> / <c>DockerLogWindowTop</c>); null is the system's own place.</summary>
    public static Func<(int X, int Y)?>? Position { get; set; }

    /// <summary>Told the window's corner as it closes, on its thread. It must not block.</summary>
    public static Action<int, int>? Placed { get; set; }

    /// <summary>Whether a window can be opened here at all: on Windows, and on a Mac with a window server.</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows() || (OperatingSystem.IsMacOS() && AppKitHost.IsEnabled);

    /// <summary>
    /// What a show does (pure, the window's choice tested without a window): none open opens one; the same container name (an id
    /// can change under compose's down and up, a name cannot be two containers at once) with the same count, its feed not
    /// refused, is brought forward; anything else swaps the open window onto a fresh feed.
    /// </summary>
    public static DockerLogWindowMove Decide(bool open, string? shownName, int shownTail, bool refused, DockerLogSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!open)
        {
            return DockerLogWindowMove.Open;
        }

        return string.Equals(shownName, source.Name, StringComparison.Ordinal) && shownTail == source.Tail && !refused
            ? DockerLogWindowMove.Raise
            : DockerLogWindowMove.Swap;
    }

    /// <summary>
    /// The window on <paramref name="source"/>: brought forward, swapped onto it, or opened (<see cref="Decide"/>). Throws
    /// <see cref="InvalidOperationException"/> with the reason when no window could be made.
    /// </summary>
    public static void Show(DockerLogSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException(DockerLogWindowText.UnavailableHere);
        }

        if (OperatingSystem.IsMacOS())
        {
            MacLineWindows.ShowDockerLog(source);
            return;
        }

        lock (s_gate)
        {
            var open = s_open;
            var shown = s_feed;
            switch (Decide(open is { Alive: true }, shown?.Name, shown?.Tail ?? 0, shown?.Refused ?? false, source))
            {
                case DockerLogWindowMove.Raise when open!.Raise():
                    return;
                case DockerLogWindowMove.Swap:
                {
                    var swapped = new DockerLogFeed(source, TimeProvider.System);
                    if (open!.Swap(swapped))
                    {
                        s_feed = swapped;
                        return;
                    }

                    break;   // gone meanwhile (the feed disposed by Swap): a new window below
                }
            }

            var feed = new DockerLogFeed(source, TimeProvider.System);
            var window = new LogWindowThread(feed, DockerLogWindowText.WindowName, Position, Placed);
            s_open = window;
            s_feed = feed;
            window.Start();
        }
    }

    /// <summary>The open window closed and waited for briefly (the screen's teardown, the app's exit); true when one was open.</summary>
    public static bool Close()
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacLineWindows.CloseDockerLog();
        }

        LogWindowThread? open;
        lock (s_gate)
        {
            open = s_open;
            s_open = null;
            s_feed = null;
        }

        bool alive = open is { Alive: true };
        open?.Close();
        return alive;
    }
}
