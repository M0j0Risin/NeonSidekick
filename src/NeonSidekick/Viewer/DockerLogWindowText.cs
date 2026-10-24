using System.Globalization;
using NeonSidekick.Docker;

namespace NeonSidekick.Viewer;

/// <summary>Where a Docker log window's feed stands (2026-10-23): what its title says.</summary>
public enum DockerLogState
{
    /// <summary>Reading the container's log as it grows.</summary>
    Following,

    /// <summary>The container is not running; the window waits for it to start.</summary>
    Waiting,

    /// <summary>No container of that name; the window waits for a new one.</summary>
    Gone,

    /// <summary>The engine could not be reached; the window tries again.</summary>
    WaitingForDocker,

    /// <summary>The engine refused the log (a logging driver that cannot be read); the window has stopped.</summary>
    Refused,
}

/// <summary>
/// The Docker log window's words and <c>/docker logs</c>' about it (2026-10-23, the user's ask: <c>/docker logs &lt;container&gt;</c>
/// tails the log in a window of its own, as <c>/log</c> does the app's, <see cref="DockerLogWindow"/>): the title in each state,
/// the marker lines a stop, a restart or a new container leaves in the text, the empty line, the notices and errors, and the
/// command's <c>--pane</c> word.
/// </summary>
public static class DockerLogWindowText
{
    /// <summary>The window's name on its thread and in the log.</summary>
    public const string WindowName = "Docker log window";

    /// <summary><c>/docker logs</c>' word for today's pane instead of the window. Pinned: it is a command word.</summary>
    public const string PaneWord = "--pane";

    /// <summary>How many earlier lines the window shows when no number is given (the pane's is <see cref="DockerCommand.DefaultLogLines"/>).</summary>
    public const int DefaultWindowLines = 200;

    /// <summary>The line the window shows before the container has written anything.</summary>
    public const string Empty = "No log lines yet.";

    /// <summary>A state's words in the title.</summary>
    public static string StateWords(DockerLogState state) => state switch
    {
        DockerLogState.Waiting => "waiting for it to start",
        DockerLogState.Gone => "gone; waiting for a new one",
        DockerLogState.WaitingForDocker => "waiting for Docker",
        DockerLogState.Refused => "can't be read",
        _ => "following",
    };

    /// <summary>The window's title: <c>Docker · mysql_dev — following</c>, the paused tail while it does not follow.</summary>
    public static string Title(string name, DockerLogState state, bool following)
    {
        ArgumentNullException.ThrowIfNull(name);
        return $"Docker · {name} — {StateWords(state)}{(following ? "" : ProcessWindowText.PausedTail)}";
    }

    /// <summary>A marker's time: the app's computer's local clock as the feed saw the change, <c>14:02:11</c>.</summary>
    public static string Clock(DateTimeOffset local) => local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The marker for a stream that ended with the container stopped.</summary>
    public static string NotRunningMarker(string clock) => $"── {clock} · not running; waiting for it to start ──";

    /// <summary>The marker for a container that started again under the same id.</summary>
    public static string RestartedMarker(string clock) => $"── {clock} · restarted ──";

    /// <summary>The marker for a new container under the same name (compose's down and up).</summary>
    public static string NewContainerMarker(string clock, string name) => $"── {clock} · a new {name} ──";

    /// <summary>The marker for a name no container has any more.</summary>
    public static string GoneMarker(string clock, string name) => $"── {clock} · {name} is gone; waiting for a new one ──";

    /// <summary><c>/docker logs</c>' notice once the window shows the container.</summary>
    public static string OpenedNotice(string name) => $"({DockerText.Glyph} {name}'s log in the Docker log window, following)";

    /// <summary>The <c>/docker</c> pane's logs button's note with the window there.</summary>
    public const string MenuNote = "follow its log in the Docker log window";

    /// <summary><c>/docker logs</c> where no window can be made (neither Windows nor a Mac).</summary>
    public const string Unavailable = "The Docker log window needs Windows or a Mac; /docker logs <container> --pane shows the last lines in a pane.";

    /// <summary>The same error on a Mac: the window works there, but only with a window server.</summary>
    public const string UnavailableMac = "The Docker log window needs the Mac's desktop (there is no window server here, over SSH, say); /docker logs <container> --pane shows the last lines in a pane.";

    /// <summary>The error for this system.</summary>
    public static string UnavailableHere => OperatingSystem.IsMacOS() ? UnavailableMac : Unavailable;

    /// <summary><c>/docker logs</c> when the window could not be made.</summary>
    public static string WindowFailedError(string detail) => $"Could not open the Docker log window: {detail}";
}
