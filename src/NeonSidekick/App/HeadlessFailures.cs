namespace NeonSidekick.App;

/// <summary>
/// What failed in a headless run (2026-11-21, feature idea #41), for the closing summary and exit 4: a message's turn, a command's
/// <c>[error]</c> line, a server a call needed that wanted a sign-in, a loop or a review that stopped, a session that wasn't saved. One failure an
/// input line (its first; a loop's stop replaces it, <see cref="Replace"/>), and the same failure on several lines counted, not dropped (2026-11-22,
/// the review's #132: six failed messages read "6 failed: a message ×6"). A pass the app runs again takes its line's failure back (<see cref="Mark"/>,
/// <see cref="BackTo"/>), so a blip it recovered from is no failed run; a run-wide one (a sign-in, an unsaved session) stays, since a pass run again
/// can't sign in or save either (#1). Locked: a sign-in's call runs on a tool's thread, a save's failure is noted from the log's.
/// </summary>
public sealed class HeadlessFailures
{
    /// <summary>The line of a failure that belongs to the run, not to one input line.</summary>
    private const long RunWide = -1;

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

    /// <summary>What failed, in the order each first failed, the same what and why counted together.</summary>
    public IReadOnlyList<(string What, string Why, int Count)> Entries
    {
        get
        {
            lock (_gate)
            {
                var entries = new List<(string What, string Why, int Count)>();
                foreach (var (what, why, _) in _failures)
                {
                    int at = entries.FindIndex(e => e.What == what && e.Why == why);
                    if (at < 0)
                    {
                        entries.Add((what, why, 1));
                    }
                    else
                    {
                        entries[at] = (what, why, entries[at].Count + 1);
                    }
                }

                return entries;
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

    /// <summary>The input line's failure, unless it has one already.</summary>
    public void Add(string what, string why)
    {
        ArgumentNullException.ThrowIfNull(what);
        ArgumentNullException.ThrowIfNull(why);
        lock (_gate)
        {
            if (!_failures.Any(f => f.Line == _line))
            {
                _failures.Add((what, why, _line));
            }
        }
    }

    /// <summary>
    /// The input line's failure in place of any it has (2026-11-22, the review's #97): how a Ralph loop ended names the line, whatever a pass
    /// before its end noted on it.
    /// </summary>
    public void Replace(string what, string why)
    {
        ArgumentNullException.ThrowIfNull(what);
        ArgumentNullException.ThrowIfNull(why);
        lock (_gate)
        {
            _failures.RemoveAll(f => f.Line == _line);
            _failures.Add((what, why, _line));
        }
    }

    /// <summary>A server a call needed that wanted a sign-in: once a server, whichever line called it.</summary>
    public void AddSignIn(string server)
    {
        ArgumentNullException.ThrowIfNull(server);
        AddRunWide(server, HeadlessText.NeedsSignIn);
    }

    /// <summary>A failure of the run's, not a line's (a sign-in, an unsaved session): once, and never taken back by <see cref="BackTo"/>.</summary>
    public void AddRunWide(string what, string why)
    {
        ArgumentNullException.ThrowIfNull(what);
        ArgumentNullException.ThrowIfNull(why);
        lock (_gate)
        {
            if (!_failures.Any(f => f.Line == RunWide && f.What == what && f.Why == why))
            {
                _failures.Add((what, why, RunWide));
            }
        }
    }

    /// <summary>How many failures there are, for <see cref="BackTo"/>.</summary>
    public int Mark()
    {
        lock (_gate)
        {
            return _failures.Count;
        }
    }

    /// <summary>The lines' failures since <paramref name="mark"/> taken back: the pass they came from runs again. The run-wide ones stay.</summary>
    public void BackTo(int mark)
    {
        lock (_gate)
        {
            if (mark < 0 || mark >= _failures.Count)
            {
                return;
            }

            var kept = _failures.Skip(mark).Where(f => f.Line == RunWide).ToList();
            _failures.RemoveRange(mark, _failures.Count - mark);
            _failures.AddRange(kept);
        }
    }

    /// <summary>The closing <c>[notice]</c>, or null when nothing failed.</summary>
    public string? SummaryLine() => Entries is { Count: > 0 } entries ? HeadlessText.FailedSummary(entries) : null;
}
