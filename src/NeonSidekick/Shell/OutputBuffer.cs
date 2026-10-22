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
/// Since 2026-10-05 <see cref="Appended"/> tells a reader (<c>/process</c>'s window) of each line, outside the lock; since 2026-10-12
/// each stream's line in progress is kept beside the lines (<see cref="SetUnfinished"/>), so a prompt with no newline is seen.
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
    private string? _unfinishedOut;
    private string? _unfinishedErr;

    public OutputBuffer(int maxLines = DefaultMaxLines, int maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, MaxLineChars);
        _maxLines = maxLines;
        _maxBytes = maxBytes;
    }

    /// <summary>
    /// Raised after each line is appended, outside the lock, on the pump's pool thread (2026-10-05, <c>/process</c>'s window), and
    /// since 2026-10-12 when a stream's unfinished line changes (<see cref="SetUnfinished"/>, for <c>process wait</c>'s <c>until</c>).
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
        // Never inside a surrogate pair (2026-10-22, review L6).
        if (text.Length > MaxLineChars)
        {
            text = Files.TextCut.Safe(text, MaxLineChars - CutMark.Length) + CutMark;
        }

        lock (_lock)
        {
            // Bytes as held: two a UTF-16 char (2026-10-22, review F4: the cap counted chars, so a buffer held twice what it said).
            _lines.AddLast(new OutputLine(text, isError));
            _bytes += text.Length * (long)sizeof(char);
            _totalChars += text.Length + 1;
            while (_lines.Count > _maxLines || (_bytes > _maxBytes && _lines.Count > 1))
            {
                _bytes -= _lines.First!.Value.Text.Length * (long)sizeof(char);
                _lines.RemoveFirst();
                _dropped++;
            }
        }

        Raise();
    }

    private void Raise()
    {
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
    /// The new lines are found from the newest end (the code review, same day): the window reads after every line, and a walk from
    /// the head crossed up to <see cref="DefaultMaxLines"/> nodes under the lock the pumps append under; this crosses only the new ones.
    /// </summary>
    public long CopyFrom(long from, ICollection<OutputLine> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_lock)
        {
            long first = _dropped + 1;
            long skip = Math.Max(0, from - first);
            int wanted = _lines.Count - (int)Math.Min(skip, _lines.Count);
            if (wanted == 0)
            {
                return first;
            }

            var node = _lines.Last!;
            for (int i = 1; i < wanted; i++)
            {
                node = node.Previous!;
            }

            for (LinkedListNode<OutputLine>? at = node; at is not null; at = at.Next)
            {
                into.Add(at.Value);
            }

            return first;
        }
    }

    /// <summary>
    /// The line a stream has written so far without its newline (2026-10-12, the shell review): a prompt (<c>Password:</c>,
    /// <c>Continue? [y/N]</c>, Python's <c>&gt;&gt;&gt; </c>) that waits for <c>process write</c> and that a reader of whole lines never
    /// saw; null (or empty) clears it. Not a line yet: it is in no count, no cursor and no number, and the newline that ends it
    /// appends it as an ordinary one. Set by the pump (<see cref="ShellRunner"/>) after each read.
    /// </summary>
    public void SetUnfinished(string? text, bool isError)
    {
        if (text is { Length: > MaxLineChars })
        {
            text = text[..(MaxLineChars - CutMark.Length)] + CutMark;
        }

        text = string.IsNullOrEmpty(text) ? null : text;
        lock (_lock)
        {
            ref string? field = ref isError ? ref _unfinishedErr : ref _unfinishedOut;
            if (string.Equals(field, text, StringComparison.Ordinal))
            {
                return;
            }

            field = text;
        }

        // A change tells the readers as a line does (process wait's until reads a prompt; /process's window reads again and finds no line).
        Raise();
    }

    /// <summary>The streams' unfinished lines (<see cref="SetUnfinished"/>), stdout's then stderr's: what <c>poll</c>, <c>wait</c> and a tail <c>log</c> show after the lines.</summary>
    public IReadOnlyList<OutputLine> Unfinished()
    {
        lock (_lock)
        {
            if (_unfinishedOut is null && _unfinishedErr is null)
            {
                return [];
            }

            var unfinished = new List<OutputLine>(2);
            if (_unfinishedOut is { } output)
            {
                unfinished.Add(new OutputLine(output, false));
            }

            if (_unfinishedErr is { } error)
            {
                unfinished.Add(new OutputLine(error, true));
            }

            return unfinished;
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

    /// <summary>
    /// Every kept line, oldest first, and how many were dropped before them, read under one lock so the two agree (2026-10-12, the
    /// shell review: <c>run_command</c> and <c>execute_code</c> built their result and spill from <see cref="Lines"/> alone, so a long
    /// run's spill claimed to be the whole output — the <c>/cmd</c> fix of 2026-10-11 had not reached them — and a line landing between
    /// two separate reads would have miscounted the drop).
    /// </summary>
    public IReadOnlyList<OutputLine> Snapshot(out long dropped)
    {
        lock (_lock)
        {
            dropped = _dropped;
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
