using System.Diagnostics;

namespace NeonSidekick.Sql;

/// <summary>
/// One deadline over a call of several statements (2026-10-24, the database review's O13): the Oracle, MySQL and PostgreSQL reads gave each
/// statement the full timeout, so a call of nine statements could run nine times the setting. Pure.
/// </summary>
public static class SqlDeadline
{
    /// <summary>The whole seconds left of <paramref name="timeoutSeconds"/> since <paramref name="watch"/> started, at least 1 while any is; 0 once none is.</summary>
    public static int Left(Stopwatch watch, int timeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(watch);
        double left = Math.Max(1, timeoutSeconds) - watch.Elapsed.TotalSeconds;
        return left <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(left));
    }
}
