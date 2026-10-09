using System.Collections.Concurrent;

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

    public BoundaryNames(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

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

        IReadOnlyList<string[]> rows = run.Grids.Count > 0 ? run.Grids[0].Rows : [];
        _rows[key] = (now, rows);
        return (rows, null);
    }
}
