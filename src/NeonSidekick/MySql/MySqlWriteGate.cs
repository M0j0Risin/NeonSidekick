using NeonSidekick.Sql;

namespace NeonSidekick.MySql;

/// <summary>
/// Whether <c>mysql_execute</c> may run a text (2026-10-05, <c>MySQL mode</c> <c>read-write</c>, the user's ask: SQLite's
/// <c>SqliteWriteGate</c> mirrored): the read gate's lexer (<see cref="MySqlReadOnlyGate.Tokenize"/> — <c>#</c> and <c>-- </c> comments,
/// backslash escapes, backticks; an executable comment <c>/*! … */</c> refused) under <see cref="ServerWriteGate"/>'s rules. One
/// statement — the one-statement rule is load-bearing here, since the driver runs <c>a; b</c> as two and DDL commits first — but a
/// <c>CREATE PROCEDURE</c>, <c>FUNCTION</c>, <c>TRIGGER</c> body between <c>BEGIN</c> and its <c>END</c> holds its own <c>;</c>s
/// (<see cref="Separators"/>: the compound statements nest). Refused first words: the transaction's own and <c>XA</c>,
/// <c>GRANT</c>/<c>REVOKE</c>, <c>SET</c>/<c>USE</c>, <c>PREPARE</c>/<c>EXECUTE</c>/<c>DEALLOCATE</c>, <c>LOAD</c>/<c>IMPORT</c>,
/// <c>HANDLER</c>, <c>DO</c>, <c>LOCK</c>/<c>UNLOCK</c>, <c>FLUSH</c>/<c>KILL</c>/<c>SHUTDOWN</c>/<c>RESTART</c>/<c>RESET</c>/<c>PURGE</c>/
/// <c>CHANGE</c>/<c>INSTALL</c>/<c>UNINSTALL</c>/<c>CLONE</c>/<c>BINLOG</c>/<c>CACHE</c>; a <c>CREATE</c>/<c>ALTER</c>/<c>DROP</c> of a
/// database (MySQL's schema), user, role, server, tablespace, logfile group, resource group, event or instance; <c>RENAME USER</c>.
/// Refused anywhere: <c>INTO OUTFILE</c>/<c>DUMPFILE</c>, <c>DEFINER =</c> (who a routine or view runs as is the user's to say), and the
/// read gate's denied functions (bar MariaDB's <c>NEXTVAL</c>/<c>SETVAL</c>, which a change to data may use).
/// </summary>
public static class MySqlWriteGate
{
    private static readonly Dictionary<string, string> RefusedObjects = ServerWriteGate.Table(
        (ServerWriteText.Accounts, "USER ROLE"),
        (ServerWriteText.ServerWide, "DATABASE SCHEMA SERVER TABLESPACE LOGFILE RESOURCE EVENT INSTANCE"));

    private const string Objects = "TABLE INDEX VIEW SEQUENCE";

    private const string Routines = "PROCEDURE FUNCTION TRIGGER";

    /// <summary>The rules (<see cref="ServerWriteGate"/>).</summary>
    public static readonly WriteGateRules Rules = new(
        MySqlStatementKinds.Family,
        ServerWriteGate.Table(
            (ServerWriteText.OwnTransaction, "START BEGIN COMMIT ROLLBACK SAVEPOINT RELEASE XA"),
            (ServerWriteText.Accounts, "GRANT REVOKE"),
            (ServerWriteText.Session, "SET USE"),
            (ServerWriteText.Dynamic, "PREPARE EXECUTE DEALLOCATE"),
            (ServerWriteText.Outside, "LOAD IMPORT"),
            (ServerWriteText.Denied, "HANDLER DO"),
            (ServerWriteText.Locks, "LOCK UNLOCK"),
            (ServerWriteText.ServerWide, "FLUSH KILL SHUTDOWN RESTART RESET PURGE CHANGE INSTALL UNINSTALL CLONE BINLOG CACHE")),
        ServerWriteGate.Table(
            (ServerStatementKind.Delete, "TRUNCATE"),
            (ServerStatementKind.Upkeep, "ANALYZE OPTIMIZE CHECK REPAIR CHECKSUM"),
            (ServerStatementKind.Procedures, "CALL")),
        ServerWriteGate.Table((ServerStatementKind.Data, "INSERT UPDATE REPLACE"), (ServerStatementKind.Delete, "DELETE")),
        ServerWriteGate.Words("SELECT"),
        new Dictionary<string, ObjectVerb>(StringComparer.Ordinal)
        {
            ["CREATE"] = new(ServerWriteGate.Table((ServerStatementKind.Create, Objects), (ServerStatementKind.Procedures, Routines)), RefusedObjects),
            ["ALTER"] = new(ServerWriteGate.Table((ServerStatementKind.Alter, Objects), (ServerStatementKind.Procedures, Routines)), RefusedObjects),
            ["DROP"] = new(ServerWriteGate.Table((ServerStatementKind.Drop, Objects + " " + Routines)), RefusedObjects),
            ["RENAME"] = new(ServerWriteGate.Table((ServerStatementKind.Alter, "TABLE")), RefusedObjects),
        },
        ServerWriteGate.Words("OR", "REPLACE", "TEMPORARY", "UNIQUE", "FULLTEXT", "SPATIAL", "ALGORITHM", "SQL", "SECURITY", "DEFINER", "INVOKER", "ONLINE", "OFFLINE", "AGGREGATE", "IGNORE"),
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

    /// <summary>The kinds <paramref name="sql"/> needs; null when it does not lex or is none <c>mysql_execute</c> runs.</summary>
    public static IReadOnlyList<ServerStatementKind>? Kinds(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return Lex(sql, out _) is { } tokens ? ServerWriteGate.Kinds(Rules, tokens) : null;
    }

    private static List<GateToken>? Lex(string sql, out string? error)
    {
        if (MySqlReadOnlyGate.Tokenize(sql, out error) is not { } tokens)
        {
            return null;
        }

        return tokens.Select(t => new GateToken(
            t.Kind switch
            {
                MySqlReadOnlyGate.TokenKind.Word => GateTokenKind.Word,
                MySqlReadOnlyGate.TokenKind.Quoted => GateTokenKind.Name,
                MySqlReadOnlyGate.TokenKind.Symbol => GateTokenKind.Symbol,
                _ => GateTokenKind.Other,
            },
            t.Text)).ToList();
    }

    /// <summary>What is refused wherever it stands: a denied function called (a backticked name too), <c>INTO OUTFILE</c>/<c>DUMPFILE</c>, <c>DEFINER =</c>.</summary>
    private static (string What, string Why)? Scan(IReadOnlyList<GateToken> tokens, int i)
    {
        var t = tokens[i];
        var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
        if (t.Kind is GateTokenKind.Word or GateTokenKind.Name && next.IsSymbol("("))
        {
            string upper = t.Text.ToUpperInvariant();
            if (upper is not ("NEXTVAL" or "SETVAL") && MySqlReadOnlyGate.DeniedFunctions.Contains(upper))
            {
                return (t.Text.ToUpperInvariant() + "()", ServerWriteText.Denied);
            }
        }

        if (t.IsWord("INTO") && next.Kind == GateTokenKind.Word && next.Text is "OUTFILE" or "DUMPFILE")
        {
            return ("INTO " + next.Text, ServerWriteText.Outside);
        }

        if (t.IsWord("DEFINER") && next.IsSymbol("="))
        {
            return ("DEFINER =", ServerWriteText.Accounts);
        }

        return null;
    }

    /// <summary>
    /// The <c>;</c>s that end a statement: every one, but in a <c>CREATE … PROCEDURE</c>, <c>FUNCTION</c> or <c>TRIGGER</c> only those
    /// outside its <c>BEGIN … END</c> body. Inside it the compound statements nest — <c>BEGIN</c>, and <c>IF</c>, <c>CASE</c>,
    /// <c>LOOP</c>, <c>WHILE</c>, <c>REPEAT</c> as statements (not <c>IF(</c>, <c>REPEAT(</c>, <c>IF EXISTS</c>), each closed by an
    /// <c>END</c> (whose <c>END IF</c>'s word is no new opening).
    /// </summary>
    public static int Separators(IReadOnlyList<GateToken> tokens, int lead)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        int begin = -1;
        for (int i = lead; i < tokens.Count; i++)
        {
            if (tokens[i].IsWord("BEGIN"))
            {
                begin = i;
                break;
            }
        }

        bool routine = tokens[lead].IsWord("CREATE") && begin > 0
            && tokens.Take(begin).Any(t => t.Kind == GateTokenKind.Word && t.Text is "PROCEDURE" or "FUNCTION" or "TRIGGER");
        int count = 0, depth = 0;
        for (int i = lead; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
            if (t.IsSymbol(";"))
            {
                count += depth == 0 ? 1 : 0;
            }
            else if (routine && t.Kind == GateTokenKind.Word)
            {
                if (t.Text == "END")
                {
                    depth = Math.Max(0, depth - 1);
                    if (next.Kind == GateTokenKind.Word && next.Text is "IF" or "CASE" or "LOOP" or "WHILE" or "REPEAT")
                    {
                        i++;
                    }
                }
                else if (t.Text == "BEGIN" && i >= begin)
                {
                    depth++;
                }
                else if (depth > 0 && t.Text is "IF" or "CASE" or "LOOP" or "WHILE" or "REPEAT" && !next.IsSymbol("(") && !(t.Text == "IF" && next.Kind == GateTokenKind.Word && next.Text is "EXISTS" or "NOT"))
                {
                    depth++;
                }
            }
        }

        return count;
    }
}

/// <summary>What each statement kind covers in MySQL and MariaDB (2026-10-05), and the family as the write path names it. Pinned.</summary>
public static class MySqlStatementKinds
{
    public static string Statements(ServerStatementKind kind) => kind switch
    {
        ServerStatementKind.Data => "INSERT, UPDATE, REPLACE",
        ServerStatementKind.Delete => "DELETE, TRUNCATE",
        ServerStatementKind.Create => "CREATE TABLE, INDEX, VIEW, SEQUENCE",
        ServerStatementKind.Alter => "ALTER TABLE, VIEW, SEQUENCE; RENAME TABLE",
        ServerStatementKind.Drop => "DROP TABLE, INDEX, VIEW, SEQUENCE, PROCEDURE, FUNCTION, TRIGGER",
        ServerStatementKind.Upkeep => "ANALYZE, OPTIMIZE, CHECK, REPAIR, CHECKSUM TABLE",
        ServerStatementKind.Procedures => "CALL; CREATE or ALTER PROCEDURE, FUNCTION, TRIGGER",
        _ => "SELECT",
    };

    public static readonly ServerWriteFamily Family = new(
        "MySQL", "MySQL", "mysql_execute", "mysql.json", "database", MySqlConfigFile.Category,
        nameof(Settings.AppSettingsData.MySqlMode), Statements);
}
