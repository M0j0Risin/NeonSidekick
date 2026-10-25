using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace NeonSidekick.Sql;

/// <summary>
/// The names a connection's boundary reads before a model's statement (2026-10-09, the review of the lock: an alias's <c>t.col</c>
/// sent every join for the server's databases, schemas or users, a second connection each time): kept per connection for
/// <see cref="Lifetime"/>, so a turn's queries read them once. Keyed by the caller (the connection's name and what it points at, so an
/// edited entry reads afresh); a failed read is never kept. One per access object, shared by its tools.
/// </summary>
public sealed class BoundaryNames
{
    /// <summary>How long names read are trusted: a database or schema made since is seen within it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<string[]> Rows)> _rows = new(StringComparer.Ordinal);
    private IReadOnlyList<string[]>? _seeded;

    /// <summary>What a boundary built from one read's rows (a set, a pair of sets), kept beside them (2026-10-24, the database review's R14).</summary>
    private readonly ConditionalWeakTable<IReadOnlyList<string[]>, object> _built = new();

    public BoundaryNames(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// A test's names (2026-10-24, the database review's S2/S3/J6: with no server every names read failed, so a test could never reach the
    /// branch where the names come back and the boundary refuses before the pane or the run): every key reads <paramref name="rows"/> from
    /// now on, no server asked.
    /// </summary>
    public void Seed(IReadOnlyList<string[]> rows) => _seeded = rows ?? throw new ArgumentNullException(nameof(rows));

    /// <summary>The key of one connection: its name and the parts that say where it points, joined.</summary>
    public static string Key(params string?[] parts) => string.Join("\n", parts.Select(p => p?.Trim() ?? ""));

    /// <summary>
    /// The rows <paramref name="read"/> brings (its first result set), from the cache while fresh; the failed run when the read did not
    /// succeed (never kept, so the next call tries again).
    /// </summary>
    public async Task<(IReadOnlyList<string[]>? Rows, SqlRun? Failed)> GetAsync(string key, Func<CancellationToken, Task<SqlRun>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(read);
        if (_seeded is { } seeded)
        {
            return (seeded, null);
        }

        var now = _time.GetUtcNow();
        if (_rows.TryGetValue(key, out var hit) && now - hit.At < Lifetime)
        {
            return (hit.Rows, null);
        }

        var run = await read(cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return (null, run);
        }

        // A list cut at its cap, or none at all, cannot settle a qualifier (2026-10-24, the database review's E8, mechanism 7: a name past
        // the cap passed as an alias), so it is a failed read: the statement that needed it is refused, nothing kept.
        if (run.Grids.Count == 0 || run.Grids[0].More || run.Grids[0].Rows.Count == 0)
        {
            return (null, run with { Outcome = SqlOutcome.Failed, Detail = SqlText.NamesUnread, Grids = [] });
        }

        IReadOnlyList<string[]> rows = run.Grids[0].Rows;
        _rows[key] = (now, rows);
        return (rows, null);
    }

    /// <summary>
    /// What <paramref name="build"/> makes of <paramref name="rows"/> (rows <see cref="GetAsync"/> gave), made once per read and kept while
    /// the rows are (R14: each check rebuilt sets over up to 200,000 names).
    /// </summary>
    public T Built<T>(IReadOnlyList<string[]> rows, Func<IReadOnlyList<string[]>, T> build) where T : class
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(build);
        return (T)_built.GetValue(rows, r => build(r));
    }

    /// <summary>
    /// Forgets every name read (2026-10-24, the database review's A11): after a committed change a schema or database made, renamed or
    /// moved is seen by the next statement's check, not a minute later.
    /// </summary>
    public void Forget() => _rows.Clear();
}
