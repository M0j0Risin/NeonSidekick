namespace NeonSidekick.Diagnostics;

/// <summary>One line the <see cref="DiagnosticBuffer"/> holds: its place in the run, its level and the text the <c>--log</c> file would hold.</summary>
/// <param name="Seq">The line's number in the run, from 0, one more for every line: a reader asks for the lines after the last it has.</param>
/// <param name="Level">Severity, for the log window's colours.</param>
/// <param name="Text"><see cref="DiagnosticFileSink.Format"/>'s line, exactly as the file has it.</param>
public readonly record struct LogLine(long Seq, DiagnosticLevel Level, string Text);

/// <summary>
/// The run's diagnostic lines in memory (2026-10-02, the user's ask: <c>/log</c> opens a window that follows the log in
/// any run, <c>--log</c> or not): every level from Trace up, the newest <see cref="Capacity"/> kept, each in the
/// <c>--log</c> file's form. <c>Program</c> attaches one before anything is logged, so the window opens on the run from
/// its first line. Lines arrive on whichever thread logged them, so <see cref="Add"/> is short and serialised by one lock,
/// and <see cref="Appended"/> is raised outside it; a reader (the log window) asks for what it lacks with
/// <see cref="CopySince"/>. Nothing here touches the file: <c>/log --file</c> is <see cref="DiagnosticFileSink"/>'s.
/// </summary>
public sealed class DiagnosticBuffer : IDisposable
{
    /// <summary>The lines kept when no capacity is given: a long session's worth, a few megabytes at most.</summary>
    public const int DefaultCapacity = 20_000;

    private readonly Lock _gate = new();
    private readonly LogLine[] _ring;
    private int _start;
    private int _count;
    private long _next;
    private bool _attached;

    public DiagnosticBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new LogLine[capacity];
    }

    /// <summary>
    /// Raised after every line is added, outside the lock, on the thread that logged it. A subscriber must not block and
    /// must not log (it would raise itself again); the log window only posts itself a message.
    /// </summary>
    public event Action? Appended;

    /// <summary>The most lines kept; the oldest goes as each one past it arrives.</summary>
    public int Capacity => _ring.Length;

    /// <summary>The lines held now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>The <see cref="LogLine.Seq"/> the next line will have: the number of lines the run has logged so far.</summary>
    public long NextSeq
    {
        get
        {
            lock (_gate)
            {
                return _next;
            }
        }
    }

    /// <summary>A buffer subscribed to <see cref="DiagnosticLog.Emitted"/> until it is disposed: what <c>Program</c> makes.</summary>
    public static DiagnosticBuffer Attach(int capacity = DefaultCapacity)
    {
        var buffer = new DiagnosticBuffer(capacity);
        DiagnosticLog.Emitted += buffer.Add;
        buffer._attached = true;
        return buffer;
    }

    /// <summary>One line kept, the oldest dropped when the buffer is full, and <see cref="Appended"/> raised. Never throws.</summary>
    public void Add(DiagnosticEvent evt)
    {
        var line = DiagnosticFileSink.Format(evt);
        lock (_gate)
        {
            int at = (_start + _count) % _ring.Length;
            _ring[at] = new LogLine(_next++, evt.Level, line);
            if (_count < _ring.Length)
            {
                _count++;
            }
            else
            {
                _start = (_start + 1) % _ring.Length;
            }
        }

        try
        {
            Appended?.Invoke();
        }
        catch
        {
            // A reader that throws must not take a log call down with it (DiagnosticLog's own rule).
        }
    }

    /// <summary>
    /// Every line held from <paramref name="from"/> on, oldest first, appended to <paramref name="into"/>; returns the
    /// <see cref="LogLine.Seq"/> of the oldest line held (<see cref="NextSeq"/> when none is), so a reader knows which of its
    /// lines have gone from the front.
    /// </summary>
    public long CopySince(long from, List<LogLine> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_gate)
        {
            long first = _next - _count;
            long skip = Math.Max(0, from - first);
            for (long i = skip; i < _count; i++)
            {
                into.Add(_ring[(int)((_start + i) % _ring.Length)]);
            }

            return first;
        }
    }

    public void Dispose()
    {
        if (_attached)
        {
            DiagnosticLog.Emitted -= Add;
            _attached = false;
        }
    }
}
