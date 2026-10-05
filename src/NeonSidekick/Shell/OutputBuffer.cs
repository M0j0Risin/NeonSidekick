namespace NeonSidekick.Shell;

/// <summary>One line a child wrote, and on which stream.</summary>
public readonly record struct OutputLine(string Text, bool IsError);

/// <summary>
/// What a child has written, bounded (2026-09-21): the last <see cref="MaxLines"/> lines within
/// <see cref="MaxBytes"/> of text, a line longer than <see cref="MaxLineChars"/> cut with an
/// ellipsis, the oldest dropped first — so a chatty server never grows the process. Lines are
/// numbered from 1 over everything ever written (<see cref="TotalLines"/>), and
/// <see cref="FirstKeptLine"/> says where the kept ones start, so a <c>log</c> offset stays stable
/// across drops. Thread-safe: the two pumps append from the pool, the tools read on the turn task.
/// Since 2026-10-05 <see cref="Appended"/> tells a reader (<c>/process</c>'s window) of each line, outside the lock.
/// </summary>
public sealed class OutputBuffer
{
    public const int DefaultMaxLines = 5000;
    public const int DefaultMaxBytes = 2 * 1024 * 1024;
    public const int MaxLineChars = 8 * 1024;
    public const string CutMark = "…";

    private readonly object _lock = new();
    private readonly LinkedList<OutputLine> _lines = new();
    private readonly int _maxLines;
    private readonly int _maxBytes;
    private long _dropped;
    private long _bytes;
    private long _totalChars;

    public OutputBuffer(int maxLines = DefaultMaxLines, int maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, MaxLineChars);
        _maxLines = maxLines;
        _maxBytes = maxBytes;
    }

    /// <summary>
    /// Raised after each line is appended, outside the lock, on the pump's pool thread (2026-10-05, <c>/process</c>'s window).
    /// A subscriber must not block: the window only posts itself a message and reads with <see cref="CopyFrom"/>.
    /// </summary>
    public event Action? Appended;

    /// <summary>How many lines were ever written.</summary>
    public long TotalLines
    {
        get
        {
            lock (_lock)
            {
                return _dropped + _lines.Count;
            }
        }
    }

    /// <summary>The number (from 1) of the oldest line still kept; <c>TotalLines + 1</c> when nothing is.</summary>
    public long FirstKeptLine
    {
        get
        {
            lock (_lock)
            {
                return _dropped + 1;
            }
        }
    }

    /// <summary>How many chars were ever written, drops included: the cut sentence's count.</summary>
    public long TotalChars
    {
        get
        {
            lock (_lock)
            {
                return _totalChars;
            }
        }
    }

    /// <summary>Appends one line (cut at <see cref="MaxLineChars"/>), dropping the oldest until the caps hold.</summary>
    public void Append(string text, bool isError)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxLineChars)
        {
            text = text[..(MaxLineChars - CutMark.Length)] + CutMark;
        }

        lock (_lock)
        {
            _lines.AddLast(new OutputLine(text, isError));
            _bytes += text.Length;
            _totalChars += text.Length + 1;
            while (_lines.Count > _maxLines || (_bytes > _maxBytes && _lines.Count > 1))
            {
                _bytes -= _lines.First!.Value.Text.Length;
                _lines.RemoveFirst();
                _dropped++;
            }
        }

        try
        {
            Appended?.Invoke();
        }
        catch
        {
            // A reader that fails must not take the pump down with it (DiagnosticBuffer's rule).
        }
    }

    /// <summary>
    /// Every kept line numbered <paramref name="from"/> or later (1-based over everything written), oldest first, added to
    /// <paramref name="into"/>; returns <see cref="FirstKeptLine"/> as the copy saw it, so the first line copied is numbered
    /// <c>Math.Max(from, returned)</c> — <c>DiagnosticBuffer.CopySince</c>'s contract, for <c>/process</c>'s window (2026-10-05).
    /// </summary>
    public long CopyFrom(long from, ICollection<OutputLine> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_lock)
        {
            long first = _dropped + 1;
            long skip = Math.Max(0, from - first);
            foreach (var line in _lines.Skip((int)Math.Min(skip, _lines.Count)))
            {
                into.Add(line);
            }

            return first;
        }
    }

    /// <summary>Every kept line, oldest first.</summary>
    public IReadOnlyList<OutputLine> Lines()
    {
        lock (_lock)
        {
            return _lines.ToList();
        }
    }

    /// <summary>The kept lines numbered after <paramref name="cursor"/> (a line count seen so far), and the count to pass next time.</summary>
    public IReadOnlyList<OutputLine> Since(long cursor, out long next)
    {
        lock (_lock)
        {
            next = _dropped + _lines.Count;
            long skip = Math.Max(0, cursor - _dropped);
            return skip >= _lines.Count ? [] : _lines.Skip((int)skip).ToList();
        }
    }

    /// <summary>
    /// Up to <paramref name="limit"/> kept lines from line number <paramref name="offset"/> (1-based over
    /// everything written); <paramref name="first"/> and <paramref name="last"/> are the numbers of what
    /// came back (0 and 0 for none). An offset before the kept range starts at the first kept line.
    /// </summary>
    public IReadOnlyList<OutputLine> Slice(long offset, int limit, out long first, out long last)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_lock)
        {
            long start = Math.Max(offset, _dropped + 1);
            long skip = start - _dropped - 1;
            if (skip >= _lines.Count)
            {
                first = last = 0;
                return [];
            }

            var slice = _lines.Skip((int)skip).Take(limit).ToList();
            first = start;
            last = start + slice.Count - 1;
            return slice;
        }
    }

    /// <summary>The last <paramref name="limit"/> kept lines, with their numbers.</summary>
    public IReadOnlyList<OutputLine> Tail(int limit, out long first, out long last)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_lock)
        {
            int count = Math.Min(limit, _lines.Count);
            if (count == 0)
            {
                first = last = 0;
                return [];
            }

            last = _dropped + _lines.Count;
            first = last - count + 1;
            return _lines.Skip(_lines.Count - count).ToList();
        }
    }
}
