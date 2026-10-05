using NeonSidekick.Sql;

namespace NeonSidekick.Oracle;

/// <summary>
/// Whether <c>oracle_execute</c> may run a text (2026-10-05, <c>Oracle mode</c> <c>read-write</c>, the user's ask: SQLite's
/// <c>SqliteWriteGate</c> mirrored): the read gate's lexer (<see cref="OracleReadOnlyGate.Tokenize"/> — <c>q'[…]'</c> strings,
/// <c>"names"</c>, <c>:binds</c>) under <see cref="ServerWriteGate"/>'s rules. One statement; a PL/SQL unit — an anonymous
/// <c>BEGIN</c>/<c>DECLARE</c> block, or a <c>CREATE</c> of a procedure, function, package, trigger or type — is one whatever
/// <c>;</c>s its body holds, and keeps its final <c>;</c> (<see cref="Body"/>; PL/SQL needs it, plain SQL refuses it); a SQL*Plus
/// <c>/</c> after a <c>;</c> with more text behind it is a second statement. Refused first words: <c>COMMIT</c>/<c>ROLLBACK</c>/
/// <c>SAVEPOINT</c>, <c>SET</c>, <c>GRANT</c>/<c>REVOKE</c>/<c>AUDIT</c>/<c>NOAUDIT</c>/<c>ADMINISTER</c>, <c>LOCK</c>,
/// <c>PURGE</c>/<c>FLASHBACK</c>; a <c>CREATE</c>/<c>ALTER</c>/<c>DROP</c> of a user, role, profile, tablespace, directory, database,
/// pluggable database, library, Java source, context, or anything <c>PUBLIC</c>; <c>ALTER SESSION</c>/<c>SYSTEM</c>. Refused anywhere,
/// bodies included: a database link (<c>@</c>), <see cref="OracleReadOnlyGate.DeniedPackages"/> (a quoted upper-case name too),
/// <c>EXECUTE IMMEDIATE</c>, <c>WITH FUNCTION</c>, <c>BFILENAME</c>, <c>EXTERNAL(</c>; and, outside PL/SQL, <c>RETURNING</c> (its
/// <c>INTO</c> needs out binds the tool does not read back). <c>NEXTVAL</c> is allowed: an insert may use a sequence.
/// </summary>
public static class OracleWriteGate
{
    private static readonly Dictionary<string, string> RefusedObjects = ServerWriteGate.Table(
        (ServerWriteText.Accounts, "USER ROLE PROFILE PUBLIC AUDIT"),
        (ServerWriteText.ServerWide, "TABLESPACE DIRECTORY DATABASE PLUGGABLE CONTROLFILE SPFILE PFILE RESTORE SYSTEM CONTEXT"),
        (ServerWriteText.Session, "SESSION"),
        (ServerWriteText.Outside, "LIBRARY JAVA"));

    private const string Objects = "TABLE INDEX VIEW SEQUENCE SYNONYM MATERIALIZED";

    private const string Routines = "PROCEDURE FUNCTION PACKAGE TRIGGER TYPE";

    /// <summary>The words between <c>CREATE</c> and what it makes.</summary>
    private static readonly IReadOnlySet<string> Modifiers = ServerWriteGate.Words(
        "OR", "REPLACE", "EDITIONABLE", "NONEDITIONABLE", "EDITIONING", "FORCE", "NOFORCE", "NO", "GLOBAL", "PRIVATE", "TEMPORARY", "UNIQUE", "BITMAP", "MULTIVALUE", "IMMUTABLE", "BLOCKCHAIN", "SHARDED", "DUPLICATED");

    /// <summary>The rules (<see cref="ServerWriteGate"/>).</summary>
    public static readonly WriteGateRules Rules = new(
        OracleStatementKinds.Family,
        ServerWriteGate.Table(
            (ServerWriteText.OwnTransaction, "COMMIT ROLLBACK SAVEPOINT"),
            (ServerWriteText.Session, "SET"),
            (ServerWriteText.Accounts, "GRANT REVOKE AUDIT NOAUDIT ADMINISTER"),
            (ServerWriteText.Locks, "LOCK"),
            (ServerWriteText.ServerWide, "PURGE FLASHBACK SHUTDOWN STARTUP")),
        ServerWriteGate.Table(
            (ServerStatementKind.Delete, "TRUNCATE"),
            (ServerStatementKind.Upkeep, "ANALYZE"),
            (ServerStatementKind.Alter, "COMMENT RENAME"),
            (ServerStatementKind.Procedures, "CALL BEGIN DECLARE")),
        ServerWriteGate.Table((ServerStatementKind.Data, "INSERT UPDATE MERGE"), (ServerStatementKind.Delete, "DELETE")),
        ServerWriteGate.Words("SELECT"),
        new Dictionary<string, ObjectVerb>(StringComparer.Ordinal)
        {
            ["CREATE"] = new(ServerWriteGate.Table((ServerStatementKind.Create, Objects), (ServerStatementKind.Procedures, Routines)), RefusedObjects),
            ["ALTER"] = new(ServerWriteGate.Table((ServerStatementKind.Alter, Objects), (ServerStatementKind.Procedures, Routines)), RefusedObjects),
            ["DROP"] = new(ServerWriteGate.Table((ServerStatementKind.Drop, Objects + " " + Routines)), RefusedObjects),
        },
        Modifiers,
        Scan,
        Separators);

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

    /// <summary>The kinds <paramref name="sql"/> needs; null when it does not lex or is none <c>oracle_execute</c> runs.</summary>
    public static IReadOnlyList<ServerStatementKind>? Kinds(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return Lex(sql, out _) is { } tokens ? ServerWriteGate.Kinds(Rules, tokens) : null;
    }

    /// <summary>
    /// The text as ODP.NET takes it: a trailing SQL*Plus <c>/</c> dropped, then a trailing <c>;</c> dropped from plain SQL (it fails
    /// ORA-00933) but kept on a PL/SQL unit (its <c>END;</c> needs it); cut after the last token kept, so a comment after it goes.
    /// </summary>
    public static string Body(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (OracleReadOnlyGate.Tokenize(sql, out _) is not { } tokens)
        {
            return sql;
        }

        var kept = tokens.ToList();
        if (kept.Count > 0 && kept[^1] is { Kind: OracleReadOnlyGate.TokenKind.Symbol, Text: "/" })
        {
            kept.RemoveAt(kept.Count - 1);
        }

        if (kept.Count > 0 && kept[^1] is { Kind: OracleReadOnlyGate.TokenKind.Symbol, Text: ";" } && !IsPlSql(Map(kept)))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return kept.Count == 0 ? "" : sql[..kept[^1].End];
    }

    /// <summary>Whether the statement is a PL/SQL unit: an anonymous block, or a <c>CREATE</c> of a procedure, function, package, trigger or type.</summary>
    public static bool IsPlSql(IReadOnlyList<GateToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        int at = 0;
        while (at < tokens.Count && tokens[at].IsSymbol("("))
        {
            at++;
        }

        if (at >= tokens.Count)
        {
            return false;
        }

        if (tokens[at].IsWord("BEGIN") || tokens[at].IsWord("DECLARE"))
        {
            return true;
        }

        if (!tokens[at].IsWord("CREATE"))
        {
            return false;
        }

        at++;
        while (at < tokens.Count && tokens[at].Kind == GateTokenKind.Word && Modifiers.Contains(tokens[at].Text))
        {
            at++;
        }

        return at < tokens.Count && tokens[at].Kind == GateTokenKind.Word && tokens[at].Text is "PROCEDURE" or "FUNCTION" or "PACKAGE" or "TRIGGER" or "TYPE";
    }

    private static List<GateToken>? Lex(string sql, out string? error)
    {
        if (OracleReadOnlyGate.Tokenize(sql, out error) is not { } tokens)
        {
            return null;
        }

        var mapped = Map(tokens);
        if (mapped.Count > 0 && mapped[^1].IsSymbol("/"))
        {
            mapped.RemoveAt(mapped.Count - 1);
        }

        return mapped;
    }

    private static List<GateToken> Map(IEnumerable<OracleReadOnlyGate.Token> tokens) =>
        tokens.Select(t => new GateToken(
            t.Kind switch
            {
                OracleReadOnlyGate.TokenKind.Word => GateTokenKind.Word,
                OracleReadOnlyGate.TokenKind.Quoted => GateTokenKind.Name,
                OracleReadOnlyGate.TokenKind.Symbol => GateTokenKind.Symbol,
                _ => GateTokenKind.Other,
            },
            t.Text)).ToList();

    /// <summary>What is refused wherever it stands, PL/SQL bodies included.</summary>
    private static (string What, string Why)? Scan(IReadOnlyList<GateToken> tokens, int i)
    {
        var t = tokens[i];
        var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
        if (t.IsSymbol("@"))
        {
            return ("a database link (@name: another server)", ServerWriteText.Outside);
        }

        if (t.Kind is not (GateTokenKind.Word or GateTokenKind.Name))
        {
            return null;
        }

        string word = t.Kind == GateTokenKind.Name ? t.Text.ToUpperInvariant() : t.Text;
        if (OracleReadOnlyGate.DeniedPackages.Contains(word))
        {
            return (word, ServerWriteText.Denied);
        }

        if (word == "BFILENAME" || word == "EXTERNAL" && next.IsSymbol("("))
        {
            return (word, ServerWriteText.Outside);
        }

        if (t.Kind != GateTokenKind.Word)
        {
            return null;
        }

        if (word == "EXECUTE" && next.IsWord("IMMEDIATE"))
        {
            return ("EXECUTE IMMEDIATE", ServerWriteText.Dynamic);
        }

        if (word == "WITH" && next.Kind == GateTokenKind.Word && next.Text is "FUNCTION" or "PROCEDURE")
        {
            return ("WITH " + next.Text, ServerWriteText.Denied);
        }

        if (word == "RETURNING" && !IsPlSql(tokens))
        {
            return ("RETURNING … INTO", ServerWriteText.ReturnsInto);
        }

        return null;
    }

    /// <summary>
    /// The <c>;</c>s that end a statement: in plain SQL every one; in a PL/SQL unit none of the body's, only a SQL*Plus <c>/</c>
    /// straight after a <c>;</c> with text behind it (the server would parse what follows as part of the unit; refused to be plain).
    /// </summary>
    public static int Separators(IReadOnlyList<GateToken> tokens, int lead)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (!IsPlSql(tokens))
        {
            return tokens.Count(t => t.IsSymbol(";"));
        }

        int count = 0;
        for (int i = lead + 1; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].IsSymbol("/") && tokens[i - 1].IsSymbol(";"))
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>What each statement kind covers in Oracle (2026-10-05), and the family as the write path names it. Pinned.</summary>
public static class OracleStatementKinds
{
    public static string Statements(ServerStatementKind kind) => kind switch
    {
        ServerStatementKind.Data => "INSERT, UPDATE, MERGE",
        ServerStatementKind.Delete => "DELETE, TRUNCATE, a MERGE that deletes",
        ServerStatementKind.Create => "CREATE TABLE, INDEX, VIEW, MATERIALIZED VIEW, SEQUENCE, SYNONYM",
        ServerStatementKind.Alter => "ALTER TABLE, INDEX, VIEW, SEQUENCE, SYNONYM; RENAME; COMMENT ON",
        ServerStatementKind.Drop => "DROP TABLE, INDEX, VIEW, SEQUENCE, SYNONYM, PROCEDURE, FUNCTION, PACKAGE, TRIGGER, TYPE",
        ServerStatementKind.Upkeep => "ANALYZE",
        ServerStatementKind.Procedures => "CALL, a BEGIN … END or DECLARE block; CREATE or ALTER PROCEDURE, FUNCTION, PACKAGE, TRIGGER, TYPE",
        _ => "SELECT",
    };

    public static readonly ServerWriteFamily Family = new(
        "Oracle", "Oracle", "oracle_execute", "oracle.json", "schema", OracleConfigFile.Category,
        nameof(Settings.AppSettingsData.OracleMode), Statements);
}
