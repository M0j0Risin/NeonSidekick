using NeonSidekick.Diagnostics;
using NeonSidekick.Shell;

namespace NeonSidekick.Viewer;

/// <summary>
/// What a line window (<see cref="LogWindowThread"/>) shows (2026-10-05, when <c>/process</c>'s window came to share the log
/// window's engine): the lines, a word when there are more, the title and the empty line, and a key of its own. The window
/// owns its feed and disposes it as it closes. <see cref="DiagnosticFeed"/> is <c>/log</c>'s, <see cref="ProcessFeed"/> a
/// background process's.
/// </summary>
internal interface ILineFeed : IDisposable
{
    /// <summary><see cref="DiagnosticBuffer.CopySince"/>'s contract: the lines from <paramref name="from"/> on, and the oldest <see cref="LogLine.Seq"/> held.</summary>
    long CopySince(long from, List<LogLine> into);

    /// <summary>Raised when there are lines to read or the title may have changed; outside any lock, never to block.</summary>
    event Action? Appended;

    /// <summary>The window's title; compared as a string, so a change the feed makes (an exit, an armed key) reaches it.</summary>
    string Title(bool following);

    /// <summary>The line the window shows with nothing held.</summary>
    string Empty { get; }

    /// <summary>
    /// A key the window has no use for, on its thread, before it goes to the terminal (<see cref="TerminalHandoff"/>). True when
    /// the feed took it; the window then reads the title again.
    /// </summary>
    bool Key(int virtualKey, bool control);
}

/// <summary><c>/log</c>'s feed: the run's <see cref="DiagnosticBuffer"/>, exactly as the log window read it before the feed came (2026-10-05).</summary>
internal sealed class DiagnosticFeed(DiagnosticBuffer buffer) : ILineFeed
{
    public event Action? Appended
    {
        add => buffer.Appended += value;
        remove => buffer.Appended -= value;
    }

    public long CopySince(long from, List<LogLine> into) => buffer.CopySince(from, into);

    public string Title(bool following) => LogViewText.TitleFor(following);

    public string Empty => LogViewText.Empty;

    public bool Key(int virtualKey, bool control) => false;

    public void Dispose()
    {
        // The buffer is the run's, not the window's.
    }
}

/// <summary>
/// A background process's output as a line feed (2026-10-05, <c>/process</c>'s window): each line's <see cref="LogLine.Seq"/>
/// is its <see cref="OutputBuffer"/> number, stable across drops; stdout reads as <see cref="DiagnosticLevel.Info"/> and stderr as
/// <see cref="DiagnosticLevel.Warning"/>, so stderr takes the warning colour. <see cref="Appended"/> is raised for every line
/// (<see cref="OutputBuffer.Appended"/>, on the pump's thread), at the exit, and when an armed kill key lapses, so the title
/// follows. Ctrl+K twice (<see cref="ProcessKillArm"/>) calls <c>stop</c> on the window's thread.
/// </summary>
internal sealed class ProcessFeed : ILineFeed
{
    /// <summary>Ctrl+K's virtual-key code.</summary>
    public const int VkK = 0x4B;

    private readonly ProcessSession _session;
    private readonly Action<ProcessSession> _stop;
    private readonly TimeProvider _time;
    private readonly ProcessKillArm _arm = new();
    private readonly List<OutputLine> _scratch = [];
    private readonly Lock _gate = new();
    private ITimer? _lapse;
    private volatile bool _disposed;

    public ProcessFeed(ProcessSession session, Action<ProcessSession> stop, TimeProvider time)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _session.Output.Appended += Raise;
        _ = _session.Exited.ContinueWith(_ => Raise(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public event Action? Appended;

    /// <summary>The session shown.</summary>
    public ProcessSession Session => _session;

    public long CopySince(long from, List<LogLine> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_gate)
        {
            return Copy(_session.Output, from, _scratch, into);
        }
    }

    /// <summary>
    /// <paramref name="buffer"/>'s lines from number <paramref name="from"/> on as log lines (their numbers the <c>Seq</c>, stderr a
    /// warning) added to <paramref name="into"/>, through <paramref name="scratch"/>; returns the oldest number kept. Pure over the buffer.
    /// </summary>
    internal static long Copy(OutputBuffer buffer, long from, List<OutputLine> scratch, List<LogLine> into)
    {
        scratch.Clear();
        long first = buffer.CopyFrom(from, scratch);
        long number = Math.Max(from, first);
        foreach (var line in scratch)
        {
            into.Add(new LogLine(number++, line.IsError ? DiagnosticLevel.Warning : DiagnosticLevel.Info, line.Text));
        }

        return first;
    }

    public string Title(bool following) => ProcessWindowText.Title(_session, following, _arm.IsArmed(_time.GetUtcNow()));

    public string Empty => ProcessWindowText.Empty;

    public bool Key(int virtualKey, bool control)
    {
        if (virtualKey != VkK || !control)
        {
            return false;
        }

        switch (_arm.Press(_time.GetUtcNow(), _session.HasExited))
        {
            case KillPress.Armed:
                // The title goes back once the window lapses: a tick just past it raises Appended, and the title is read again.
                _lapse?.Dispose();
                _lapse = _time.CreateTimer(_ => Raise(), null, ProcessKillArm.Window + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
                break;
            case KillPress.Fire:
                _lapse?.Dispose();
                _lapse = null;
                _stop(_session);
                break;
        }

        return true;
    }

    private void Raise()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Appended?.Invoke();
        }
        catch
        {
            // A reader that fails must not take the pump down with it.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _session.Output.Appended -= Raise;
        _lapse?.Dispose();
    }
}
