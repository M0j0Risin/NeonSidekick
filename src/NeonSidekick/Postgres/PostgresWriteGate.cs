using NeonSidekick.Sql;

namespace NeonSidekick.Postgres;

/// <summary>
/// Whether <c>postgres_execute</c> may run a text (2026-10-05, <c>PostgreSQL mode</c> <c>read-write</c>, the user's ask: SQLite's
/// <c>SqliteWriteGate</c> mirrored): the read gate's lexer (<see cref="PostgresReadOnlyGate.Tokenize"/> — nested comments, <c>E''</c>,
/// dollar quoting, so a function's or a <c>DO</c> block's <c>;</c>s are inside a string and never count; <c>U&amp;</c> and <c>$1</c>
/// refused) under <see cref="ServerWriteGate"/>'s rules. One statement. Refused first words: the transaction's own (each call commits on
/// its own), <c>GRANT</c>/<c>REVOKE</c>/<c>REASSIGN</c>/<c>SECURITY LABEL</c>, <c>SET</c>/<c>RESET</c>/<c>DISCARD</c>,
/// <c>PREPARE</c>/<c>EXECUTE</c>/<c>DEALLOCATE</c>, <c>COPY</c>, <c>LOAD</c>, <c>IMPORT</c>, <c>LISTEN</c>/<c>NOTIFY</c>/<c>UNLISTEN</c>,
/// <c>LOCK</c>, <c>CHECKPOINT</c>; a <c>CREATE</c>/<c>ALTER</c>/<c>DROP</c> of a database, role, user, group, tablespace, extension,
/// server, foreign wrapper or table, subscription, publication, event trigger, language, policy, default privileges or access
/// method; <c>OWNER TO</c> anywhere; the read gate's denied functions anywhere (bar <c>nextval</c>/<c>setval</c>, which a change to
/// data may use: a serial column's default is one).
/// </summary>
public static class PostgresWriteGate
{
    private static readonly Dictionary<string, string> RefusedFirst = ServerWriteGate.Table(
        (ServerWriteText.OwnTransaction, "BEGIN START COMMIT END ROLLBACK ABORT SAVEPOINT RELEASE"),
        (ServerWriteText.Accounts, "GRANT REVOKE REASSIGN SECURITY"),
        (ServerWriteText.Session, "SET RESET DISCARD"),
        (ServerWriteText.Dynamic, "PREPARE EXECUTE DEALLOCATE"),
        (ServerWriteText.Outside, "COPY LOAD IMPORT"),
        (ServerWriteText.Denied, "LISTEN NOTIFY UNLISTEN CHECKPOINT"),
        (ServerWriteText.Locks, "LOCK"));

    private static readonly Dictionary<string, string> RefusedObjects = ServerWriteGate.Table(
        (ServerWriteText.Accounts, "ROLE USER GROUP POLICY DEFAULT OWNED"),
        (ServerWriteText.ServerWide, "DATABASE TABLESPACE SYSTEM EVENT ACCESS"),
        (ServerWriteText.Outside, "EXTENSION SERVER FOREIGN SUBSCRIPTION PUBLICATION LANGUAGE"));

    private const string Objects = "TABLE INDEX VIEW MATERIALIZED SEQUENCE TYPE DOMAIN SCHEMA STATISTICS";

    private const string Routines = "FUNCTION PROCEDURE TRIGGER RULE";

    /// <summary>The rules (<see cref="ServerWriteGate"/>).</summary>
    public static readonly WriteGateRules Rules = new(
        PostgresStatementKinds.Family,
        RefusedFirst,
        ServerWriteGate.Table(
            (ServerStatementKind.Delete, "TRUNCATE"),
            (ServerStatementKind.Upkeep, "VACUUM ANALYZE REINDEX CLUSTER REFRESH"),
            (ServerStatementKind.Alter, "COMMENT"),
            (ServerStatementKind.Procedures, "CALL DO")),
        ServerWriteGate.Table((ServerStatementKind.Data, "INSERT UPDATE MERGE"), (ServerStatementKind.Delete, "DELETE")),
        ServerWriteGate.Words("SELECT", "VALUES", "TABLE"),
        new Dictionary<string, ObjectVerb>(StringComparer.Ordinal)
        {
            ["CREATE"] = new(ServerWriteGate.Table((ServerStatementKind.Create, Objects), (ServerStatementKind.Procedures, Routines)), RefusedObjects),
            ["ALTER"] = new(ServerWriteGate.Table((ServerStatementKind.Alter, Objects), (ServerStatementKind.Procedures, Routines)), RefusedObjects),
            ["DROP"] = new(ServerWriteGate.Table((ServerStatementKind.Drop, Objects + " " + Routines)), RefusedObjects),
        },
        ServerWriteGate.Words("OR", "REPLACE", "TEMP", "TEMPORARY", "UNLOGGED", "GLOBAL", "LOCAL", "UNIQUE", "RECURSIVE", "CONSTRAINT", "TRUSTED", "PROCEDURAL"),
        Scan);

    /// <summary>Null when <paramref name="sql"/> may run under <paramref name="allowed"/> (null allows every kind); else the <c>Error:</c> sentence.</summary>
    public static string? Check(string sql, IReadOnlyList<ServerStatementKind>? allowed = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return ServerWriteText.NoStatement;
        }

        return Lex(sql, out var error) is { } tokens ? ServerWriteGate.Check(Rules, tokens, allowed) : error;
    }

    /// <summary>The kinds <paramref name="sql"/> needs; null when it does not lex or is none <c>postgres_execute</c> runs.</summary>
    public static IReadOnlyList<ServerStatementKind>? Kinds(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return Lex(sql, out _) is { } tokens ? ServerWriteGate.Kinds(Rules, tokens) : null;
    }

    private static List<GateToken>? Lex(string sql, out string? error)
    {
        if (PostgresReadOnlyGate.Tokenize(sql, out error) is not { } tokens)
        {
            return null;
        }

        return tokens.Select(t => new GateToken(
            t.Kind switch
            {
                PostgresReadOnlyGate.TokenKind.Word => GateTokenKind.Word,
                PostgresReadOnlyGate.TokenKind.Quoted or PostgresReadOnlyGate.TokenKind.OperatorBind => GateTokenKind.Name,
                PostgresReadOnlyGate.TokenKind.Symbol => GateTokenKind.Symbol,
                _ => GateTokenKind.Other,
            },
            t.Text)).ToList();
    }

    /// <summary>What is refused wherever it stands: a denied function called (a quoted name too), <c>OWNER TO</c>.</summary>
    private static (string What, string Why)? Scan(IReadOnlyList<GateToken> tokens, int i)
    {
        var t = tokens[i];
        var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
        if (t.Kind is GateTokenKind.Word or GateTokenKind.Name && next.IsSymbol("("))
        {
            string upper = t.Text.ToUpperInvariant();
            if (upper is not ("NEXTVAL" or "SETVAL") && PostgresReadOnlyGate.IsDeniedFunction(upper))
            {
                return (t.Text.ToLowerInvariant() + "()", ServerWriteText.Denied);
            }
        }

        if (t.IsWord("OWNER") && next.IsWord("TO"))
        {
            return ("OWNER TO", ServerWriteText.Accounts);
        }

        return null;
    }
}

/// <summary>
/// What each statement kind covers in PostgreSQL (2026-10-05), and the family as the write path names it
/// (<see cref="ServerWriteFamily"/>). Pinned.
/// </summary>
public static class PostgresStatementKinds
{
    public static string Statements(ServerStatementKind kind) => kind switch
    {
        ServerStatementKind.Data => "INSERT, UPDATE, MERGE",
        ServerStatementKind.Delete => "DELETE, TRUNCATE, a MERGE that deletes",
        ServerStatementKind.Create => "CREATE TABLE, INDEX, VIEW, MATERIALIZED VIEW, SEQUENCE, TYPE, DOMAIN, SCHEMA",
        ServerStatementKind.Alter => "ALTER TABLE, INDEX, VIEW, SEQUENCE, TYPE, DOMAIN, SCHEMA; COMMENT ON; CREATE OR REPLACE VIEW (with creating)",
        ServerStatementKind.Drop => "DROP TABLE, INDEX, VIEW, SEQUENCE, TYPE, DOMAIN, SCHEMA, FUNCTION, PROCEDURE, TRIGGER",
        ServerStatementKind.Upkeep => "VACUUM, ANALYZE, REINDEX, CLUSTER, REFRESH MATERIALIZED VIEW",
        ServerStatementKind.Procedures => "CALL, DO; CREATE or ALTER FUNCTION, PROCEDURE, TRIGGER, RULE",
        _ => "SELECT, VALUES, TABLE",
    };

    public static readonly ServerWriteFamily Family = new(
        "PostgreSQL", "PostgreSQL", "postgres_execute", "postgres.json", "database", PostgresConfigFile.Category,
        nameof(Settings.AppSettingsData.PostgresMode), Statements);
}
