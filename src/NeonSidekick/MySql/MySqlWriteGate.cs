using NeonSidekick.Sql;

namespace NeonSidekick.MySql;

/// <summary>
/// Whether <c>mysql_execute</c> may run a text (2026-10-05, <c>MySQL mode</c> <c>read-write</c>, the user's ask: SQLite's
/// <c>SqliteWriteGate</c> mirrored): the read gate's lexer (<see cref="MySqlReadOnlyGate.Tokenize"/> — <c>#</c> and <c>-- </c> comments,
/// backslash escapes, backticks; an executable comment <c>/*! … */</c> refused) under <see cref="ServerWriteGate"/>'s rules. One
/// statement — the one-statement rule is load-bearing here, since the driver runs <c>a; b</c> as two and DDL commits first — but a
/// <c>CREATE PROCEDURE</c>, <c>FUNCTION</c>, <c>TRIGGER</c> body between <c>BEGIN</c> and its <c>END</c> holds its own <c>;</c>s
/// (<see cref="Separators"/>: the body read as MySQL reads it, a body it cannot read for sure refused). Refused first words: the transaction's own and <c>XA</c>,
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
        Separators,
        Also,
        BodyRefusal);

    /// <summary>
    /// The kinds a statement needs beyond its verb's (2026-10-24, the database review's A7, G3): a <c>REPLACE</c> deletes the row it
    /// replaces (and what cascades from it), so it needs deleting too; MariaDB's <c>ANALYZE UPDATE|DELETE|SELECT …</c> runs the statement
    /// after it, so it needs that statement's kinds (an <c>ANALYZE TABLE</c> is upkeep alone).
    /// </summary>
    private static IEnumerable<ServerStatementKind> Also(IReadOnlyList<GateToken> tokens, int lead)
    {
        for (int i = lead; i < tokens.Count; i++)
        {
            if (tokens[i].IsWord("REPLACE") && !(i + 1 < tokens.Count && tokens[i + 1].IsSymbol("(")) && !(i > 0 && (tokens[i - 1].IsWord("OR") || tokens[i - 1].IsSymbol("."))))
            {
                yield return ServerStatementKind.Delete;
            }
        }

        if (tokens[lead].IsWord("ANALYZE") && !(lead + 1 < tokens.Count && tokens[lead + 1].Kind == GateTokenKind.Word && tokens[lead + 1].Text is "TABLE" or "NO_WRITE_TO_BINLOG" or "LOCAL"))
        {
            for (int i = lead + 1; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == GateTokenKind.Word && !(i + 1 < tokens.Count && tokens[i + 1].IsSymbol("(")))
                {
                    if (Rules.Dml.TryGetValue(tokens[i].Text, out var kind))
                    {
                        yield return kind;
                    }
                    else if (tokens[i].Text == "SELECT")
                    {
                        yield return ServerStatementKind.Read;
                    }
                }
            }
        }
    }

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

    /// <summary>The text as <see cref="ServerWriteGate"/>'s tokens (<see cref="MySqlBoundary"/> reads them too, 2026-10-09); null with the error when it does not lex.</summary>
    internal static List<GateToken>? Lex(string sql, out string? error)
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
        var before = i > 0 ? tokens[i - 1] : default;
        if (t.Kind is GateTokenKind.Word or GateTokenKind.Name && next.IsSymbol("("))
        {
            string upper = t.Text.ToUpperInvariant();
            if (upper is not ("NEXTVAL" or "SETVAL") && MySqlReadOnlyGate.IsDeniedFunction(upper))
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

        if (t.Kind != GateTokenKind.Word || before.IsSymbol("."))
        {
            return null;
        }

        // 2026-10-24, the database review's Phase 8. SQL SECURITY DEFINER (T5): an ALTER of a routine made by another account switched it
        // to run as that account. SONAME (G5): CREATE FUNCTION … SONAME loads a native library, as INSTALL PLUGIN does. A FEDERATED,
        // CONNECT or Spider table, or a CONNECTION string (A13), is another server's table. DATA DIRECTORY and INDEX DIRECTORY (F19) put
        // a table's files where the server can write; IMPORT TABLESPACE reads a data-directory file and DISCARD TABLESPACE deletes one (R10).
        var after = i + 2 < tokens.Count ? tokens[i + 2] : default;
        switch (t.Text)
        {
            case "SECURITY" when before.IsWord("SQL") && next.IsWord("DEFINER"):
                return ("SQL SECURITY DEFINER", ServerWriteText.RunsAs);
            case "SONAME":
                return ("SONAME (a native function library)", ServerWriteText.ForeignCode);
            case "ENGINE" when (next.IsSymbol("=") ? after : next) is { Kind: GateTokenKind.Word or GateTokenKind.Name } engine
                && engine.Text.ToUpperInvariant() is "FEDERATED" or "CONNECT" or "SPIDER":
                return ("ENGINE = " + engine.Text.ToUpperInvariant(), ServerWriteText.Outside);
            case "CONNECTION" when next.IsSymbol("=") || next.Kind == GateTokenKind.Other:
                return ("CONNECTION = '…' (another server's table)", ServerWriteText.Outside);
            case "DIRECTORY" when before.IsWord("DATA") || before.IsWord("INDEX"):
                return (before.Text + " DIRECTORY", ServerWriteText.Outside);
            case "TABLESPACE" when before.IsWord("IMPORT") || before.IsWord("DISCARD"):
                return (before.Text + " TABLESPACE", ServerWriteText.Outside);
        }

        return null;
    }

    /// <summary>
    /// What a routine's or trigger's body may not hold (2026-10-24, the database review's DB3, A6, G4): its statements meet the top level's
    /// refusals — accounts and permissions, the server-wide statements, files, locks, dynamic SQL (<c>PREPARE</c>/<c>EXECUTE</c>), a
    /// <c>CREATE</c>/<c>ALTER</c>/<c>DROP</c> of a database, user, role, server or event — bar a transaction's own words, which a
    /// procedure may use. A <c>SET</c> may set only the routine's own variables: a parameter, a <c>DECLARE</c>d name, a user variable
    /// (<c>@x</c>) or a trigger's <c>NEW.x</c>; any other name is a system variable (a bare <c>sql_mode</c> sets the session's), refused.
    /// Null when the statement has no body or the body holds none of these.
    /// </summary>
    private static (string What, string Why)? BodyRefusal(IReadOnlyList<GateToken> tokens, int lead)
    {
        int body = RoutineBody(tokens, lead);
        if (body < 0)
        {
            return null;
        }

        var starts = new List<int>();
        if (Walk(tokens, lead, starts) < 0)
        {
            return null;   // the separators' count refuses it as unclear
        }

        // A handler's own statement (DECLARE EXIT HANDLER FOR SQLEXCEPTION <statement>) starts after its conditions.
        foreach (int s in starts.ToList())
        {
            if (tokens[s].IsWord("DECLARE") && s + 4 < tokens.Count && tokens[s + 2].IsWord("HANDLER") && tokens[s + 3].IsWord("FOR"))
            {
                int k = s + 4;
                while (k < tokens.Count)
                {
                    k += tokens[k].IsWord("NOT") ? 2 : tokens[k].IsWord("SQLSTATE") ? (k + 1 < tokens.Count && tokens[k + 1].IsWord("VALUE") ? 3 : 2) : 1;
                    if (!(k < tokens.Count && tokens[k].IsSymbol(",")))
                    {
                        break;
                    }

                    k++;
                }

                if (k < tokens.Count)
                {
                    starts.Add(k);
                }
            }
        }

        var own = Parameters(tokens, lead, body);
        foreach (int s in starts)
        {
            if (tokens[s].IsWord("DECLARE"))
            {
                for (int k = s + 1; k < tokens.Count && tokens[k].Kind is GateTokenKind.Word or GateTokenKind.Name; k += 2)
                {
                    own.Add(tokens[k].Text.ToUpperInvariant());
                    if (!(k + 1 < tokens.Count && tokens[k + 1].IsSymbol(",")))
                    {
                        break;
                    }
                }
            }
        }

        foreach (int s in starts)
        {
            var t = tokens[s];
            if (t.Kind != GateTokenKind.Word || s <= body)
            {
                continue;
            }

            if (t.Text == "SET")
            {
                if (SetTarget(tokens, s, own) is { } target)
                {
                    return ("SET " + target + " in a routine's body", ServerWriteText.Session);
                }

                continue;
            }

            if (Rules.Refused.TryGetValue(t.Text, out var why) && why != ServerWriteText.OwnTransaction)
            {
                return (t.Text + " in a routine's body", why);
            }

            if (t.Text is "CREATE" or "ALTER" or "DROP" or "RENAME")
            {
                int at = s + 1;
                while (at < tokens.Count && tokens[at].Kind == GateTokenKind.Word && Rules.Modifiers.Contains(tokens[at].Text))
                {
                    at++;
                }

                if (at < tokens.Count && tokens[at].Kind == GateTokenKind.Word && RefusedObjects.TryGetValue(tokens[at].Text, out var objectWhy))
                {
                    return (t.Text + " " + tokens[at].Text + " in a routine's body", objectWhy);
                }
            }
        }

        return null;
    }

    /// <summary>The names of a routine's parameter list (the first parenthesized list of its header), upper-cased; empty for a trigger.</summary>
    private static HashSet<string> Parameters(IReadOnlyList<GateToken> tokens, int lead, int body)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        int open = -1;
        for (int i = lead; i < body; i++)
        {
            if (tokens[i].IsSymbol("("))
            {
                open = i;
                break;
            }
        }

        if (open < 0)
        {
            return names;
        }

        int depth = 0;
        for (int i = open; i < body; i++)
        {
            if (tokens[i].IsSymbol("("))
            {
                depth++;
            }
            else if (tokens[i].IsSymbol(")") && --depth == 0)
            {
                break;
            }
            else if (depth == 1 && (tokens[i - 1].IsSymbol("(") || tokens[i - 1].IsSymbol(",")))
            {
                int k = tokens[i].Kind == GateTokenKind.Word && tokens[i].Text is "IN" or "OUT" or "INOUT" ? i + 1 : i;
                if (k < body && tokens[k].Kind is GateTokenKind.Word or GateTokenKind.Name)
                {
                    names.Add(tokens[k].Text.ToUpperInvariant());
                }
            }
        }

        return names;
    }

    /// <summary>The first target of the <c>SET</c> at <paramref name="s"/> that is not the routine's own, as written; null when every one is.</summary>
    private static string? SetTarget(IReadOnlyList<GateToken> tokens, int s, HashSet<string> own)
    {
        int i = s + 1;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            bool mine = t.Kind == GateTokenKind.Other                                                          // @x, a user variable
                || (t.Kind is GateTokenKind.Word or GateTokenKind.Name && i + 1 < tokens.Count && tokens[i + 1].IsSymbol(".")
                    && t.Text.ToUpperInvariant() is "NEW" or "OLD")                                             // a trigger's row
                || (t.Kind is GateTokenKind.Word or GateTokenKind.Name && !t.Text.StartsWith('@') && own.Contains(t.Text.ToUpperInvariant()));
            if (!mine)
            {
                return t.Text;
            }

            int depth = 0;
            for (i++; i < tokens.Count; i++)
            {
                if (tokens[i].IsSymbol("("))
                {
                    depth++;
                }
                else if (tokens[i].IsSymbol(")"))
                {
                    depth--;
                }
                else if (depth == 0 && (tokens[i].IsSymbol(",") || tokens[i].IsSymbol(";")))
                {
                    break;
                }
            }

            if (i >= tokens.Count || tokens[i].IsSymbol(";"))
            {
                return null;
            }

            i++;
        }

        return null;
    }

    /// <summary>
    /// The <c>;</c>s that end a statement: every one, but in a <c>CREATE</c> of a procedure, function or trigger whose body is a
    /// <c>BEGIN … END</c> block only those outside the block; -1 when the body cannot be read for sure (refused,
    /// <see cref="ServerWriteText.UnclearBody"/>).
    /// <para>
    /// Since the review of 2026-10-05 it reads the body as MySQL does rather than counting words: BEGIN and END are not reserved, so
    /// <c>CREATE VIEW v AS SELECT 1 AS function, 2 AS begin; DROP DATABASE prod</c> passed as one statement (a bare FUNCTION and
    /// BEGIN made it a "routine" whose <c>;</c>s were never counted) and MySqlConnector ran both. Now the routine is known by the
    /// object CREATE makes (<see cref="RoutineBody"/>: the word after its modifiers, the body's BEGIN right after the parameter
    /// list and characteristics or a trigger's <c>FOR EACH ROW</c>), and inside the body a block opens only where a statement
    /// starts (after <c>;</c>, a label's <c>:</c>, a block's BEGIN, an IF's or CASE statement's THEN/ELSE, a WHILE's DO, a LOOP or
    /// REPEAT, a handler's conditions) and closes only by an END that can close it: after <c>;</c>, straight after its BEGIN,
    /// <c>END IF</c>/<c>CASE</c>/<c>LOOP</c>/<c>WHILE</c>/<c>REPEAT</c> on its own opener, or a CASE expression's END. A BEGIN
    /// elsewhere, an END that closes nothing it could, a block word used as a label, a body left open: -1. A word after a dot is a
    /// name whatever it spells (<c>t.end</c>).
    /// </para>
    /// </summary>
    public static int Separators(IReadOnlyList<GateToken> tokens, int lead)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return Walk(tokens, lead, null);
    }

    /// <summary><see cref="Separators"/>' walk; <paramref name="starts"/>, when given, gets where each of the body's statements starts (2026-10-24, for <see cref="BodyRefusal"/>).</summary>
    private static int Walk(IReadOnlyList<GateToken> tokens, int lead, List<int>? starts)
    {
        int body = RoutineBody(tokens, lead);
        int count = 0;
        for (int i = lead; i < (body < 0 ? tokens.Count : body); i++)
        {
            count += tokens[i].IsSymbol(";") ? 1 : 0;
        }

        if (body < 0)
        {
            return count;
        }

        var open = new List<Block>();
        int start = body;
        for (int i = body; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.IsSymbol(";"))
            {
                count += open.Count == 0 ? 1 : 0;
                continue;
            }

            if (open.Count == 0 && i > body)
            {
                continue;   // after the body: only its ;s count
            }

            bool atStart = i == body || StatementStart(tokens, i, open);
            if (atStart)
            {
                start = i;
                starts?.Add(i);
            }

            if (t.Kind != GateTokenKind.Word || tokens[i - 1].IsSymbol("."))
            {
                continue;
            }

            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
            var top = open.Count > 0 ? open[^1] : null;
            switch (t.Text)
            {
                case "BEGIN":
                    if (next.IsSymbol(":") || !(atStart || IsHandlerBody(tokens, start, i)))
                    {
                        return -1;
                    }

                    open.Add(new Block("BEGIN", i));
                    break;
                case "IF" or "LOOP" or "WHILE" or "REPEAT" when atStart:
                    open.Add(new Block(t.Text, i));
                    break;
                case "CASE":
                    open.Add(new Block(atStart ? "CASE" : CaseExpression, i));
                    break;
                case "DO" when top is { Word: "WHILE", Do: < 0 }:
                    top.Do = i;
                    break;
                case "END":
                    if (top is null || next.IsSymbol(":"))
                    {
                        return -1;
                    }

                    if (next.Kind == GateTokenKind.Word && next.Text is "IF" or "CASE" or "LOOP" or "WHILE" or "REPEAT")
                    {
                        if (top.Word != next.Text)
                        {
                            return -1;
                        }

                        i++;
                    }
                    else if (top.Word == CaseExpression ? !EndsOperand(tokens[i - 1]) : !(top.Word == "BEGIN" && (atStart || top.At == i - 1)))
                    {
                        return -1;
                    }

                    open.RemoveAt(open.Count - 1);
                    break;
            }
        }

        return open.Count > 0 ? -1 : count;
    }

    /// <summary>The block word a CASE expression's opener is kept under, so <c>END CASE</c> never closes one.</summary>
    private const string CaseExpression = "CASE expression";

    /// <summary>The words an operand must follow: an END straight after one is a name (<c>THEN end END</c>), not the CASE's close.</summary>
    private static readonly IReadOnlySet<string> Operators = ServerWriteGate.Words(
        "THEN", "ELSE", "WHEN", "CASE", "AND", "OR", "NOT", "XOR", "IS", "IN", "LIKE", "BETWEEN", "DIV", "MOD", "REGEXP", "RLIKE", "SOUNDS", "ESCAPE", "COLLATE", "BINARY", "INTERVAL");

    /// <summary>Whether <paramref name="prev"/> can end an operand, so a CASE expression's END may follow it: a literal, a name, <c>)</c>, a word no operator.</summary>
    private static bool EndsOperand(GateToken prev) => prev.Kind switch
    {
        GateTokenKind.Other or GateTokenKind.Name => true,
        GateTokenKind.Symbol => prev.Text == ")",
        _ => !Operators.Contains(prev.Text),
    };

    /// <summary>An open block of a routine's body: its word, where it opened, and a WHILE's DO once seen.</summary>
    private sealed class Block(string word, int at)
    {
        public string Word { get; } = word;

        public int At { get; } = at;

        public int Do { get; set; } = -1;
    }

    /// <summary>Whether the token at <paramref name="i"/> starts a statement of the body, by the token before it and the block it is in.</summary>
    private static bool StatementStart(IReadOnlyList<GateToken> tokens, int i, List<Block> open)
    {
        var prev = tokens[i - 1];
        if (prev.IsSymbol(";"))
        {
            return true;
        }

        if (prev.IsSymbol(":"))
        {
            return i >= 2 && tokens[i - 2].Kind is GateTokenKind.Word or GateTokenKind.Name;   // a label's
        }

        if (prev.Kind != GateTokenKind.Word || open.Count == 0)
        {
            return false;
        }

        var top = open[^1];
        return prev.Text switch
        {
            "THEN" or "ELSE" => top.Word is "IF" or "CASE",
            "DO" => top.Word == "WHILE" && top.Do == i - 1,
            "BEGIN" or "LOOP" or "REPEAT" => top.Word == prev.Text && top.At == i - 1,
            _ => false,
        };
    }

    /// <summary>
    /// Whether the BEGIN at <paramref name="i"/> is a handler's body: the statement from <paramref name="start"/> is exactly
    /// <c>DECLARE CONTINUE|EXIT|UNDO HANDLER FOR</c> and its conditions (<c>SQLEXCEPTION</c>, <c>SQLWARNING</c>, <c>NOT FOUND</c>,
    /// <c>SQLSTATE [VALUE] '…'</c>, an error number, a condition's name — never one spelled BEGIN or END), comma-separated.
    /// </summary>
    private static bool IsHandlerBody(IReadOnlyList<GateToken> tokens, int start, int i)
    {
        int k = start;
        if (!(tokens[k].IsWord("DECLARE") && k + 3 < i && tokens[k + 1].Kind == GateTokenKind.Word && tokens[k + 1].Text is "CONTINUE" or "EXIT" or "UNDO"
            && tokens[k + 2].IsWord("HANDLER") && tokens[k + 3].IsWord("FOR")))
        {
            return false;
        }

        k += 4;
        while (true)
        {
            var c = tokens[k];
            if (c.IsWord("NOT") && tokens[k + 1].IsWord("FOUND"))
            {
                k += 2;
            }
            else if (c.IsWord("SQLSTATE"))
            {
                k += tokens[k + 1].IsWord("VALUE") ? 3 : 2;
            }
            else if (c.Kind == GateTokenKind.Other || c.Kind == GateTokenKind.Name || (c.Kind == GateTokenKind.Word && c.Text is not ("BEGIN" or "END")))
            {
                k++;
            }
            else
            {
                return false;
            }

            if (k >= i)
            {
                return k == i;
            }

            if (!tokens[k].IsSymbol(","))
            {
                return false;
            }

            k++;
        }
    }

    /// <summary>The words a routine's characteristics and a function's return type are made of: what may stand between the parameter list and the body's BEGIN.</summary>
    private static readonly IReadOnlySet<string> Characteristics = ServerWriteGate.Words(
        "RETURNS", "COMMENT", "LANGUAGE", "SQL", "NOT", "DETERMINISTIC", "CONTAINS", "NO", "READS", "MODIFIES", "DATA", "SECURITY", "DEFINER", "INVOKER",
        "CHARACTER", "CHARSET", "COLLATE", "SET", "UNSIGNED", "SIGNED", "ZEROFILL", "BINARY", "ASCII", "UNICODE", "BYTE", "NATIONAL", "VARYING", "PRECISION",
        "TYPE", "OF", "ROW", "TINYINT", "SMALLINT", "MEDIUMINT", "INT", "INTEGER", "BIGINT", "INT1", "INT2", "INT3", "INT4", "INT8", "MIDDLEINT", "DECIMAL",
        "DEC", "NUMERIC", "FIXED", "FLOAT", "FLOAT4", "FLOAT8", "DOUBLE", "REAL", "BIT", "BOOL", "BOOLEAN", "SERIAL", "DATE", "DATETIME", "TIMESTAMP", "TIME",
        "YEAR", "CHAR", "VARCHAR", "VARCHARACTER", "NCHAR", "NVARCHAR", "VARBINARY", "TINYBLOB", "BLOB", "MEDIUMBLOB", "LONGBLOB", "LONG", "TINYTEXT", "TEXT",
        "MEDIUMTEXT", "LONGTEXT", "ENUM", "JSON", "GEOMETRY", "POINT", "LINESTRING", "POLYGON", "MULTIPOINT", "MULTILINESTRING", "MULTIPOLYGON",
        "GEOMETRYCOLLECTION", "GEOMCOLLECTION", "UUID", "INET4", "INET6", "VECTOR");

    /// <summary>
    /// Where a routine's <c>BEGIN … END</c> body starts, or -1 when the text is no <c>CREATE</c> of a procedure, function or trigger
    /// with such a body (then every <c>;</c> counts). The object is the word after CREATE's modifiers (<c>OR REPLACE</c>,
    /// <c>AGGREGATE</c>, <c>DEFINER = …</c>), never a word further on. A procedure's or function's body follows its parameter list
    /// and only the characteristics' and return type's words (<see cref="Characteristics"/>); a trigger's follows <c>FOR EACH ROW</c>
    /// and an optional <c>FOLLOWS</c>/<c>PRECEDES</c> name. A label may stand before the BEGIN.
    /// </summary>
    private static int RoutineBody(IReadOnlyList<GateToken> tokens, int lead)
    {
        int n = tokens.Count;
        if (!tokens[lead].IsWord("CREATE"))
        {
            return -1;
        }

        int k = lead + 1;
        while (k < n)
        {
            if (tokens[k].Kind == GateTokenKind.Word && tokens[k].Text is "OR" or "REPLACE" or "AGGREGATE")
            {
                k++;
            }
            else if (tokens[k].IsWord("DEFINER") && k + 2 < n && tokens[k + 1].IsSymbol("="))
            {
                k += 2;
                if (tokens[k].IsWord("CURRENT_USER"))
                {
                    k += k + 2 < n && tokens[k + 1].IsSymbol("(") && tokens[k + 2].IsSymbol(")") ? 3 : 1;
                }
                else
                {
                    k++;
                    if (k + 1 < n && tokens[k].IsSymbol("@"))
                    {
                        k += 2;
                    }
                    else if (k < n && tokens[k].Kind == GateTokenKind.Other)
                    {
                        k++;   // name@host, the @host lexed as one token
                    }
                }
            }
            else
            {
                break;
            }
        }

        if (k >= n || tokens[k].Kind != GateTokenKind.Word)
        {
            return -1;
        }

        if (tokens[k].Text == "TRIGGER")
        {
            for (int j = k + 1; j + 2 < n && !tokens[j].IsSymbol(";"); j++)
            {
                if (tokens[j].IsWord("FOR") && tokens[j + 1].IsWord("EACH") && tokens[j + 2].IsWord("ROW"))
                {
                    int at = j + 3;
                    if (at < n && tokens[at].Kind == GateTokenKind.Word && tokens[at].Text is "FOLLOWS" or "PRECEDES")
                    {
                        at += 2;
                    }

                    return BlockAt(tokens, at);
                }
            }

            return -1;
        }

        if (tokens[k].Text is not ("PROCEDURE" or "FUNCTION"))
        {
            return -1;
        }

        // The name (IF NOT EXISTS, db.name, a quoted one), then the parameter list.
        int open = k + 1;
        while (open < n && open <= k + 6 && !tokens[open].IsSymbol("(") && (tokens[open].Kind is GateTokenKind.Word or GateTokenKind.Name || tokens[open].IsSymbol(".")))
        {
            open++;
        }

        int after = AfterParentheses(tokens, open);
        if (after < 0)
        {
            return -1;
        }

        while (after < n)
        {
            var t = tokens[after];
            if (t.Kind == GateTokenKind.Word && Characteristics.Contains(t.Text))
            {
                // A charset's, a collation's or an anchored type's name follows these.
                after += t.Text is "CHARSET" or "COLLATE" or "SET" or "OF" && after + 1 < n && tokens[after + 1].Kind is GateTokenKind.Word or GateTokenKind.Name ? 2 : 1;
                if (after < n && tokens[after].IsSymbol("("))
                {
                    after = AfterParentheses(tokens, after);   // a type's length or an ENUM's list
                    if (after < 0)
                    {
                        return -1;
                    }
                }
            }
            else if (t.Kind == GateTokenKind.Other || (t.IsSymbol(".") && after + 1 < n && tokens[after + 1].Kind is GateTokenKind.Word or GateTokenKind.Name))
            {
                after += t.Kind == GateTokenKind.Other ? 1 : 2;   // COMMENT's string; TYPE OF t.col's column
            }
            else
            {
                break;
            }
        }

        return BlockAt(tokens, after);
    }

    /// <summary>The index of the BEGIN a body opens with at <paramref name="at"/> (after an optional label), or -1.</summary>
    private static int BlockAt(IReadOnlyList<GateToken> tokens, int at)
    {
        if (at < tokens.Count && tokens[at].IsWord("BEGIN"))
        {
            return at;
        }

        return at + 2 < tokens.Count && tokens[at].Kind is GateTokenKind.Word or GateTokenKind.Name && tokens[at + 1].IsSymbol(":") && tokens[at + 2].IsWord("BEGIN")
            ? at + 2
            : -1;
    }

    /// <summary>The index after the <c>)</c> that closes the <c>(</c> at <paramref name="open"/>, or -1.</summary>
    private static int AfterParentheses(IReadOnlyList<GateToken> tokens, int open)
    {
        if (open >= tokens.Count || !tokens[open].IsSymbol("("))
        {
            return -1;
        }

        int depth = 0;
        for (int i = open; i < tokens.Count; i++)
        {
            if (tokens[i].IsSymbol("("))
            {
                depth++;
            }
            else if (tokens[i].IsSymbol(")") && --depth == 0)
            {
                return i + 1;
            }
            else if (tokens[i].IsSymbol(";"))
            {
                return -1;
            }
        }

        return -1;
    }
}

/// <summary>What each statement kind covers in MySQL and MariaDB (2026-10-05), and the family as the write path names it. Pinned.</summary>
public static class MySqlStatementKinds
{
    public static string Statements(ServerStatementKind kind) => kind switch
    {
        ServerStatementKind.Data => "INSERT, UPDATE, REPLACE",
        ServerStatementKind.Delete => "DELETE, TRUNCATE, REPLACE",
        ServerStatementKind.Create => "CREATE TABLE, INDEX, VIEW, SEQUENCE",
        ServerStatementKind.Alter => "ALTER TABLE, VIEW, SEQUENCE; RENAME TABLE; CREATE OR REPLACE VIEW, INDEX (with creating)",
        ServerStatementKind.Drop => "DROP TABLE, INDEX, VIEW, SEQUENCE, PROCEDURE, FUNCTION, TRIGGER; CREATE OR REPLACE TABLE, SEQUENCE (with creating)",
        ServerStatementKind.Upkeep => "ANALYZE, OPTIMIZE, CHECK, REPAIR, CHECKSUM TABLE",
        ServerStatementKind.Procedures => "CALL; CREATE or ALTER PROCEDURE, FUNCTION, TRIGGER",
        _ => "SELECT",
    };

    public static readonly ServerWriteFamily Family = new(
        "MySQL", "MySQL", "mysql_execute", "mysql.json", "database", MySqlConfigFile.Category,
        nameof(Settings.AppSettingsData.MySqlMode), Statements);   // no NamesElsewhere since 2026-10-09: MySqlBoundary refuses another database outright
}
