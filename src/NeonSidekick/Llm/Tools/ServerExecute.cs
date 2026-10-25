using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The user's allow for one change by a server family's <c>_execute</c> tool: the family, where it would run (<see cref="ExecuteTarget"/>),
/// the statement and its bound values (2026-10-24, the database review's B5: the pane showed the statement but not what its placeholders
/// stood for). True to run, false declined, null when no pane could ask.
/// </summary>
public delegate Task<bool?> DatabaseWriteAllow(ServerWriteFamily family, ExecuteTarget target, string sql, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken);

/// <summary>
/// Where a change would run: the connection's name, whether its entry allows changes, the database (or schema) a call works in, and the
/// server it reaches (<paramref name="Where"/>: the host and port, or the data source; 2026-10-24, decision DB6: the session allow is keyed
/// on it, so a connection re-pointed at another server is asked again).
/// </summary>
public sealed record ExecuteTarget(string Connection, bool ReadWrite, string Place, string Where = "");

/// <summary>
/// One family's hooks for <see cref="ServerExecute.RunAsync"/>: its write gate (<paramref name="Check"/>, <paramref name="Kinds"/>), its
/// read gate (a read runs only if it passes it too), what of the text runs (<paramref name="Body"/>), the target a call names (an
/// <c>Error:</c> sentence when none), the read-only run (the <c>_query</c> tool's answer), the write run, and its error sentences.
/// <paramref name="Outside"/> (2026-10-09, the lock to a connection's place) is the family's check that needs the server — the names of
/// the other databases or schemas there, which a lexer cannot tell from an alias — run before a read or the pane: the refusal, or null.
/// Since 2026-10-24 (the database review's H11) every hook works on the one connection the call resolved at its start (each tool pins it),
/// so a file edited between the pane and the run cannot send the allowed change elsewhere.
/// </summary>
public sealed record ServerExecuteHooks(
    Func<string, IReadOnlyList<ServerStatementKind>, string?> Check,
    Func<string, IReadOnlyList<ServerStatementKind>?> Kinds,
    Func<string, string?> ReadCheck,
    Func<string, string> Body,
    Func<(ExecuteTarget? Target, string? Error)> Resolve,
    Func<string, CancellationToken, Task<string>> Read,
    Func<string, CancellationToken, Task<SqlRun>> Execute,
    Func<SqlRun, string> Error,
    Func<string, CancellationToken, Task<string?>>? Outside = null,
    Action? Changed = null);

/// <summary>
/// The server families' <c>_execute</c> tools' one order of checks (2026-10-05, <c>sqlite_execute</c>'s, mirrored): the mode
/// (<c>read-write</c>), a kind ticked, the write gate under the ticked kinds, the connection (it must say <c>"access": "readwrite"</c>),
/// then a read — what the read gate passes too — runs on the read-only path without asking; anything else asks the user (Deny / Allow
/// once / Allow for this session, per connection, server and database), runs committed, and is written to the log. Every refusal is an
/// <c>Error:</c> sentence the model reads.
/// </summary>
public static class ServerExecute
{
    public static async Task<string> RunAsync(ServerWriteFamily family, string? mode, IReadOnlyList<string>? kindsSaved, string sql, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int maxChars, ServerExecuteHooks hooks, DatabaseWriteAllow? allow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
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

        // The statement is classified once (2026-10-24, the database review's K7: Kinds ran again for the audit, a second ScriptDom parse on
        // SQL Server). A read is what the write gate calls one and the read gate passes too; one the read gate refuses (SELECT setval(…),
        // SELECT … FOR UPDATE: the write gate passed it under the ticked kinds) is asked about as a change rather than refused outright (P1:
        // it could never be asked on the pane). The read gate's look comes before the boundary's server round trip (P8).
        string body = hooks.Body(sql);
        var kindsRun = hooks.Kinds(body);
        bool classedRead = ServerWriteGate.IsRead(kindsRun);
        bool read = classedRead && hooks.ReadCheck(body) is null;

        // A "read" the read gate refuses changes something no read may (a sequence, a lock), so it needs changing data ticked as well as
        // reading (2026-10-24, the review of these fixes: with reading alone ticked it was asked and committed).
        if (classedRead && !read && !ServerStatementKinds.Resolve(kindsSaved).Contains(ServerStatementKind.Data))
        {
            return ServerWriteText.KindNotAllowed(family, ServerStatementKind.Data, ServerStatementKinds.Resolve(kindsSaved));
        }

        if (hooks.Outside is { } outside && await outside(body, cancellationToken).ConfigureAwait(false) is { } beyond)
        {
            return beyond;
        }

        // A read asks nothing (sqlite_execute's way): it runs as the _query tool's do, read-only, so it cannot change anything and is no
        // change to audit.
        if (read)
        {
            return await hooks.Read(body, cancellationToken).ConfigureAwait(false);
        }

        if (allow is null)
        {
            return ServerWriteText.NoPane(family);
        }

        switch (await allow(family, target, body, parameters, cancellationToken).ConfigureAwait(false))
        {
            case null:
                return ServerWriteText.NoPane(family);
            case false:
                return ServerWriteText.Declined;
        }

        // Every attempted change is audited whatever its end (2026-10-24, the database review's A8, decision DB7): a statement that committed
        // part of its work and then failed or timed out (a MyISAM table, a procedure that commits as it goes), or one canceled under way,
        // was never written to the log.
        string where = target.Place.Length == 0 ? target.Connection : target.Connection + "/" + target.Place;
        SqlRun run;
        try
        {
            run = await hooks.Execute(body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            hooks.Changed?.Invoke();
            DiagnosticLog.Info(family.Category, DatabaseAudit.Line(where, DatabaseAuditOutcome.Canceled, null, body));
            throw;
        }

        // The boundary's names read afresh after any attempt (2026-10-24, the database review's A11): a schema or database it made or moved,
        // even in part, is known to the next statement's check.
        hooks.Changed?.Invoke();

        // Rows are counted for a change to data alone: the drivers' count for DDL or a CALL is 0, -1 or a guess (MySQL's 0 for CREATE
        // TABLE would read as "Changed 0 rows"), so anything else answers "Ran it".
        if (kindsRun is { } ran && !ran.Any(k => k is ServerStatementKind.Data or ServerStatementKind.Delete))
        {
            run = run with { Changes = null };
        }

        DiagnosticLog.Info(family.Category, DatabaseAudit.Line(run.Database.Length == 0 ? run.Connection.Length == 0 ? where : run.Connection : run.Connection + "/" + run.Database, DatabaseAudit.OutcomeOf(run), run.Changes, body));
        return run.Outcome != SqlOutcome.Ok ? hooks.Error(run) : ServerWriteText.Executed(run, maxRows, maxChars);
    }
}
