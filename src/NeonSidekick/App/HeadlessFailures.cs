namespace NeonSidekick.App;

/// <summary>
/// What failed in a headless run (2026-11-21, feature idea #41), for the closing summary and exit 4: a message's turn, a command's
/// <c>[error]</c> line, a server a call needed that wanted a sign-in, a loop or a review that stopped. One entry an input line (its first
/// failure), one a server for a sign-in; a pass the app runs again takes its entry back (<see cref="Mark"/>, <see cref="BackTo"/>), so a
/// blip it recovered from is no failed run. Locked: a sign-in's call runs on a tool's thread.
/// </summary>
public sealed class HeadlessFailures
{
    private readonly object _gate = new();
    private readonly List<(string What, string Why, long Line)> _failures = [];
    private long _line;

    /// <summary>Whether anything failed.</summary>
    public bool Any
    {
        get
        {
            lock (_gate)
            {
                return _failures.Count > 0;
            }
        }
    }

    /// <summary>What failed, in order.</summary>
    public IReadOnlyList<(string What, string Why)> Entries
    {
        get
        {
            lock (_gate)
            {
                return _failures.Select(f => (f.What, f.Why)).ToList();
            }
        }
    }

    /// <summary>A new input line begins: its first failure is its entry.</summary>
    public void NextLine()
    {
        lock (_gate)
        {
            _line++;
        }
    }

    /// <summary>The input line's failure, unless it has one already or the same failure was noted before.</summary>
    public void Add(string what, string why)
    {
        ArgumentNullException.ThrowIfNull(what);
        ArgumentNullException.ThrowIfNull(why);
        lock (_gate)
        {
            // The same failure on a later line is said once (two /video lines are one "/video (needs the screen)").
            if (!_failures.Any(f => f.Line == _line || (f.What == what && f.Why == why)))
            {
                _failures.Add((what, why, _line));
            }
        }
    }

    /// <summary>A server a call needed that wanted a sign-in: once a server, whichever line called it.</summary>
    public void AddSignIn(string server)
    {
        ArgumentNullException.ThrowIfNull(server);
        lock (_gate)
        {
            if (!_failures.Any(f => f.What == server && f.Why == HeadlessText.NeedsSignIn))
            {
                _failures.Add((server, HeadlessText.NeedsSignIn, -1));
            }
        }
    }

    /// <summary>How many entries there are, for <see cref="BackTo"/>.</summary>
    public int Mark()
    {
        lock (_gate)
        {
            return _failures.Count;
        }
    }

    /// <summary>The entries since <paramref name="mark"/> taken back: the pass they came from runs again.</summary>
    public void BackTo(int mark)
    {
        lock (_gate)
        {
            if (mark >= 0 && mark < _failures.Count)
            {
                _failures.RemoveRange(mark, _failures.Count - mark);
            }
        }
    }

    /// <summary>The closing <c>[notice]</c>, or null when nothing failed.</summary>
    public string? SummaryLine() => Entries is { Count: > 0 } entries ? HeadlessText.FailedSummary(entries) : null;
}
