using System.Globalization;
using NeonSidekick.Shell;

namespace NeonSidekick.Viewer;

/// <summary>
/// The process window's words and <c>/process</c>'s (2026-10-05, the user's pick from the external windows brainstorm: a live
/// output window for a background process, <see cref="ProcessWindow"/>): the window's title in each state, the line it shows
/// before any output, the kill key's prompt, the command's list, notices and errors. Pinned.
/// </summary>
public static class ProcessWindowText
{
    /// <summary>The command's word. Pinned.</summary>
    public const string Word = "/process";

    /// <summary>The longest label a title or a list row carries before it is cut with an ellipsis.</summary>
    public const int LabelCells = 80;

    /// <summary>The line the window shows before the process has written anything. Pinned.</summary>
    public const string Empty = "No output yet.";

    /// <summary>The title's tail while the user has scrolled away from the newest line, as the log window's. Pinned.</summary>
    public const string PausedTail = " (paused: Ctrl+E follows)";

    /// <summary>A process's state in a few words: <c>running</c>, <c>exited 0</c>, <c>stopped by you</c> or <c>killed</c>. Pinned.</summary>
    public static string State(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.HasExited)
        {
            return "running";
        }

        if (session.StoppedByUser)
        {
            return "stopped by you";
        }

        return session.Killed ? "killed" : "exited " + (session.ExitCode ?? -1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The window's title: <c>proc_3f2a1b · npm run dev — running</c>, the paused tail while it does not follow, and while the
    /// kill key is armed <see cref="KillPrompt"/> instead. Pinned.
    /// </summary>
    public static string Title(ProcessSession session, bool following, bool armed)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (armed)
        {
            return KillPrompt(session.Id);
        }

        return $"{session.Id} · {Label(session.Label)} — {State(session)}{(following ? "" : PausedTail)}";
    }

    /// <summary>The title after the first Ctrl+K: a second within <see cref="ProcessKillArm.Window"/> stops it. Pinned.</summary>
    public static string KillPrompt(string id) => $"Press Ctrl+K again to stop {id}";

    /// <summary>The command on one line (its breaks as spaces), cut to <see cref="LabelCells"/> with an ellipsis.</summary>
    public static string Label(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        string flat = string.Join(' ', label.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= LabelCells ? flat : flat[..(LabelCells - 1)] + "…";
    }

    /// <summary>The list pane's label. Pinned.</summary>
    public const string PaneLabel = Word;

    /// <summary><c>/process</c> with no session started in this conversation's screen. Pinned.</summary>
    public const string NoneYet = "No background processes yet: the model starts one with run_command's background option.";

    /// <summary>One row of <c>/process</c>'s list: <see cref="ShellText.SessionRow"/> with <see cref="State"/> and the cut <see cref="Label"/>. Pinned.</summary>
    public static string Row(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ShellText.SessionRow(session, State(session), 14, Label(session.Label));
    }

    /// <summary>
    /// The footer under <c>/process</c>'s list for <paramref name="session"/> (2026-10-07, the user's ask: the row cuts the command to
    /// <see cref="LabelCells"/>): the whole command on one line, then <c>proc_3f2a1b · exited 0 · ran 1m 4s · pid 4120</c>. Pinned.
    /// </summary>
    public static NeonSidekick.UI.MenuFooter Footer(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string command = string.Join(' ', session.Label.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        string facts = $"{session.Id} · {State(session)} · ran {ShellText.Elapsed(session.Elapsed)} · pid {session.Pid.ToString(CultureInfo.InvariantCulture)}";
        return new NeonSidekick.UI.MenuFooter(command, facts);
    }

    /// <summary>The list's last line: how to open one. Pinned.</summary>
    public const string ListHint = "/process <id> opens one in the process window; Ctrl+K twice there stops it.";

    /// <summary>The note beside an id on <c>/process</c>'s argument list: its state and command.</summary>
    public static string CompletionNote(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return $"{State(session)} · {Label(session.Label)}";
    }

    /// <summary><c>/process &lt;id&gt;</c>'s notice once the window shows it. Pinned.</summary>
    public static string OpenedNotice(string id) => $"({App.ChatScreen.ProcessToolGlyph} {id} in the process window; Ctrl+K twice there stops it)";

    /// <summary><c>/process &lt;id&gt;</c> where no window can be made (not Windows, or headless). Pinned.</summary>
    public const string Unavailable = "The process window needs Windows; the model's process tool can still read a process's output.";

    /// <summary>The same error on a Mac (2026-10-07): the window works there, but only with a window server. Pinned.</summary>
    public const string UnavailableMac = "The process window needs the Mac's desktop (there is no window server here, over SSH, say); the model's process tool can still read a process's output.";

    /// <summary>The error for this system: <see cref="UnavailableMac"/> on a Mac, <see cref="Unavailable"/> elsewhere.</summary>
    public static string UnavailableHere => OperatingSystem.IsMacOS() ? UnavailableMac : Unavailable;

    /// <summary><c>/process &lt;id&gt;</c> when the window could not be made. Pinned.</summary>
    public static string WindowFailedError(string detail) => $"Could not open the process window: {detail}";

    /// <summary><c>/process &lt;id&gt;</c> naming no session. Pinned.</summary>
    public static string NoMatchError(string id) => $"No process matches '{id}'; /process lists them.";

    /// <summary><c>/process &lt;id&gt;</c> matching several sessions. Pinned.</summary>
    public static string AmbiguousError(string id, IReadOnlyList<string> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return $"'{id}' matches {string.Join(", ", matches)}; type more of the id.";
    }

    /// <summary><c>/process</c> given more than one word. Pinned.</summary>
    public static string UsageError(string args) => $"/process takes nothing or a process id, not '{args}'.";
}
