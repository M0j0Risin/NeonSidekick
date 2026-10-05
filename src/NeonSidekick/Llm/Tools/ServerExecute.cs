using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The user's allow for one change by a server family's <c>_execute</c> tool: the family, the connection's name, the database (or
/// schema) it works in, the statement. True to run, false declined, null when no pane could ask.
/// </summary>
public delegate Task<bool?> DatabaseWriteAllow(ServerWriteFamily family, string connection, string place, string sql, CancellationToken cancellationToken);

/// <summary>Where a change would run: the connection's name, whether its entry allows changes, and the database (or schema) a call works in.</summary>
public sealed record ExecuteTarget(string Connection, bool ReadWrite, string Place);

/// <summary>
/// One family's hooks for <see cref="ServerExecute.RunAsync"/>: its write gate (<paramref name="Check"/>, <paramref name="Kinds"/>), its
/// read gate (a read runs only if it passes it too), what of the text runs (<paramref name="Body"/>), the target a call names (an
/// <c>Error:</c> sentence when none), the read-only run (the <c>_query</c> tool's answer), the write run, and its error sentences.
/// </summary>
public sealed record ServerExecuteHooks(
    Func<string, IReadOnlyList<ServerStatementKind>, string?> Check,
    Func<string, IReadOnlyList<ServerStatementKind>?> Kinds,
    Func<string, string?> ReadCheck,
    Func<string, string> Body,
    Func<(ExecuteTarget? Target, string? Error)> Resolve,
    Func<string, CancellationToken, Task<string>> Read,
    Func<string, CancellationToken, Task<SqlRun>> Execute,
    Func<SqlRun, string> Error);

/// <summary>
/// The server families' <c>_execute</c> tools' one order of checks (2026-10-05, <c>sqlite_execute</c>'s, mirrored): the mode
/// (<c>read-write</c>), a kind ticked, the write gate under the ticked kinds, the connection (it must say <c>"access": "readwrite"</c>),
/// then a read — what the read gate passes too — runs on the read-only path without asking; anything else asks the user (Deny / Allow
/// once / Allow for this session, per connection and database), runs committed, and is written to the log. Every refusal is an
/// <c>Error:</c> sentence the model reads.
/// </summary>
public static class ServerExecute
{
    public static async Task<string> RunAsync(ServerWriteFamily family, string? mode, IReadOnlyList<string>? kindsSaved, string sql, int maxRows, int maxChars, ServerExecuteHooks hooks, DatabaseWriteAllow? allow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(hooks);
        if (DatabaseWriteModes.Resolve(mode, family) != DatabaseWriteMode.ReadWrite)
        {
            return ServerWriteText.ReadOnlyMode(family);
        }

        var kinds = ServerStatementKinds.Resolve(kindsSaved);
        if (kinds.Count == 0)
        {
            return ServerWriteText.NoKindsAllowed(family);
        }

        if (hooks.Check(sql, kinds) is { } refused)
        {
            return refused;
        }

        var (target, unresolved) = hooks.Resolve();
        if (target is null)
        {
            return unresolved!;
        }

        if (!target.ReadWrite)
        {
            return ServerWriteText.ReadOnlyConnection(family, target.Connection);
        }

        string body = hooks.Body(sql);

        // A read asks nothing (sqlite_execute's way): it runs as the _query tool's do, read-only, so it cannot change anything and is no
        // change to audit — and only what the read gate passes counts as one, so a WITH that only looks like a read cannot slip past.
        if (ServerWriteGate.IsRead(hooks.Kinds(body)))
        {
            return hooks.ReadCheck(body) is { } notRead ? notRead : await hooks.Read(body, cancellationToken).ConfigureAwait(false);
        }

        if (allow is null)
        {
            return ServerWriteText.NoPane(family);
        }

        switch (await allow(family, target.Connection, target.Place, body, cancellationToken).ConfigureAwait(false))
        {
            case null:
                return ServerWriteText.NoPane(family);
            case false:
                return ServerWriteText.Declined;
        }

        var run = await hooks.Execute(body, cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return hooks.Error(run);
        }

        // Rows are counted for a change to data alone: the drivers' count for DDL or a CALL is 0, -1 or a guess (MySQL's 0 for CREATE
        // TABLE would read as "Changed 0 rows"), so anything else answers "Ran it".
        if (hooks.Kinds(body) is { } ran && !ran.Any(k => k is ServerStatementKind.Data or ServerStatementKind.Delete))
        {
            run = run with { Changes = null };
        }

        string where = run.Database.Length == 0 ? run.Connection : run.Connection + "/" + run.Database;
        DiagnosticLog.Info(family.Category, ServerWriteText.AuditLogLine(where, run.Changes, LogText.Excerpt(body)));
        return ServerWriteText.Executed(run, maxRows, maxChars);
    }
}
