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
        Scan,
        null,
        Also,
        BodyRefusal);

    /// <summary>The languages whose bodies the gate reads (2026-10-24, the database review's DB3, G6): every other one is code it cannot.</summary>
    private static readonly IReadOnlySet<string> ReadableLanguages = ServerWriteGate.Words("SQL", "PLPGSQL");

    /// <summary>Words refused anywhere in a body, as a top-level statement's first word is (bar a transaction's own, which a procedure may use, and SET, refused where a statement starts).</summary>
    private static readonly Dictionary<string, string> BodyRefused = RefusedFirst
        .Where(r => r.Value != ServerWriteText.OwnTransaction && r.Key != "SET")
        .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

    /// <summary>
    /// The kinds a statement needs beyond its verb's (2026-10-24, the database review's T7): <c>SELECT … INTO t</c> (an INTO not after
    /// INSERT or MERGE, outside every parenthesis) makes a table.
    /// </summary>
    private static IEnumerable<ServerStatementKind> Also(IReadOnlyList<GateToken> tokens, int lead)
    {
        if (!(tokens[lead].IsWord("SELECT") || tokens[lead].IsWord("WITH")))
        {
            yield break;
        }

        int depth = 0;
        for (int i = lead; i < tokens.Count; i++)
        {
            depth += tokens[i].IsSymbol("(") ? 1 : tokens[i].IsSymbol(")") ? -1 : 0;
            if (depth == 0 && tokens[i].IsWord("INTO") && !(tokens[i - 1].IsWord("INSERT") || tokens[i - 1].IsWord("MERGE")))
            {
                yield return ServerStatementKind.Create;
                yield break;
            }
        }
    }

    /// <summary>
    /// A function's, procedure's or <c>DO</c> block's body (2026-10-24, the database review's DB3, A6, G1, G6): its <c>LANGUAGE</c> must be
    /// <c>sql</c> or <c>plpgsql</c> (C, the untrusted procedural languages and the rest are code the gate cannot read, refused as
    /// <c>LOAD</c> is); the body — the string after <c>AS</c>, or a <c>DO</c>'s — is lexed as SQL and meets the top level's refusals
    /// anywhere in it (the scan's: denied functions, <c>OWNER TO</c>, permissions, tablespaces; and the refused first words, <c>EXECUTE</c>
    /// among them, plpgsql's dynamic SQL), a <c>SET</c> where a statement starts, and a <c>CREATE</c>/<c>ALTER</c>/<c>DROP</c> of what the
    /// family refuses. A body that is no plain, dollar-quoted or standard string, or that does not lex, is refused as unclear. Its
    /// parameters (<c>$1</c>) are read as names.
    /// </summary>
    private static (string What, string Why)? BodyRefusal(IReadOnlyList<GateToken> tokens, int lead)
    {
        if (Bodies(tokens, lead, out string? language) is not { } bodies)
        {
            return ("a body the gate cannot read", ServerWriteText.UnreadableBody);
        }

        if (language is { } lang && !ReadableLanguages.Contains(lang))
        {
            return ("LANGUAGE " + lang.ToLowerInvariant(), ServerWriteText.ForeignCode);
        }

        foreach (var inner in bodies)
        {
            if (InnerRefusal(inner) is { } refused)
            {
                return (refused.What + " in a body", refused.Why);
            }
        }

        return null;
    }

    /// <summary>
    /// The bodies of a function, procedure or <c>DO</c> block, each lexed (empty for any other statement), and its language (upper-cased;
    /// a DO's is plpgsql unless it says); null when a body is no plain or dollar-quoted string, or does not lex.
    /// </summary>
    private static List<List<GateToken>>? Bodies(IReadOnlyList<GateToken> tokens, int lead, out string? language)
    {
        language = null;
        bool block = tokens[lead].IsWord("DO");
        bool routine = tokens[lead].IsWord("CREATE") || tokens[lead].IsWord("ALTER");
        int at = lead + 1;
        while (routine && at < tokens.Count && tokens[at].Kind == GateTokenKind.Word && Rules.Modifiers.Contains(tokens[at].Text))
        {
            at++;
        }

        if (!block && !(routine && at < tokens.Count && tokens[at].Kind == GateTokenKind.Word && tokens[at].Text is "FUNCTION" or "PROCEDURE"))
        {
            return [];
        }

        language = block ? "PLPGSQL" : null;
        var sources = new List<string>();
        for (int i = at; i < tokens.Count; i++)
        {
            if (tokens[i].IsWord("LANGUAGE") && i + 1 < tokens.Count)
            {
                language = Unquoted(tokens[i + 1]).ToUpperInvariant();
                i++;
            }
            else if (IsString(tokens[i]) && (block || tokens[i - 1].IsWord("AS") || (tokens[i - 1].IsSymbol(",") && sources.Count > 0)))
            {
                sources.Add(tokens[i].Text);   // a DO's string (its LANGUAGE's value was stepped over), or AS 'body'[, 'link symbol']
            }
        }

        var bodies = new List<List<GateToken>>();
        foreach (string source in sources)
        {
            if (StringValue(source) is not { } text || PostgresReadOnlyGate.Tokenize(ParametersAsNames(text), out _) is not { } lexed)
            {
                return null;
            }

            bodies.Add(Map(lexed));
        }

        return bodies;
    }

    /// <summary>
    /// The text's tokens with every function's or block's body lexed and put after it (2026-10-24, the database review's G1: a body was one
    /// string token, so <see cref="PostgresBoundary"/> never saw a qualifier inside it); a body that cannot be read is left a string, which
    /// the gate refuses. Null with the error when the text does not lex.
    /// </summary>
    internal static List<GateToken>? LexWithBodies(string sql, out string? error)
    {
        if (Lex(sql, out error) is not { } tokens)
        {
            return null;
        }

        int lead = tokens.FindIndex(t => !t.IsSymbol("("));
        if (lead < 0 || Bodies(tokens, lead, out _) is not { Count: > 0 } bodies)
        {
            return tokens;
        }

        var all = new List<GateToken>(tokens);
        foreach (var body in bodies)
        {
            all.Add(new GateToken(GateTokenKind.Symbol, ";"));
            all.AddRange(body);
        }

        return all;
    }

    /// <summary>
    /// Upkeep that reaches the whole database (2026-10-24, the database review's H8): <c>VACUUM</c>, <c>ANALYZE</c> or <c>CLUSTER</c> with no
    /// table named after its options, and <c>REINDEX DATABASE|SYSTEM|SCHEMA</c> — every table, other schemas' too. Refused under a schema lock
    /// alone (<see cref="PostgresBoundary"/>; the review of these fixes: an unlocked connection's whole-database upkeep is the user's to tick).
    /// The statement's word, or null.
    /// </summary>
    internal static string? WholeDatabase(IReadOnlyList<GateToken> tokens, int lead)
    {
        var first = tokens[lead];
        if (first.IsWord("REINDEX"))
        {
            int at = lead + 1;
            if (at < tokens.Count && tokens[at].IsSymbol("("))
            {
                at = SkipParentheses(tokens, at);
            }

            return at < tokens.Count && tokens[at].Kind == GateTokenKind.Word && tokens[at].Text is "DATABASE" or "SYSTEM" or "SCHEMA"
                ? "REINDEX " + tokens[at].Text
                : null;
        }

        if (!(first.Kind == GateTokenKind.Word && first.Text is "VACUUM" or "ANALYZE" or "CLUSTER"))
        {
            return null;
        }

        int k = lead + 1;
        while (k < tokens.Count)
        {
            if (tokens[k].IsSymbol("("))
            {
                k = SkipParentheses(tokens, k);
            }
            else if (tokens[k].Kind == GateTokenKind.Word && tokens[k].Text is "FULL" or "FREEZE" or "VERBOSE" or "ANALYZE" or "ANALYSE")
            {
                k++;
            }
            else
            {
                break;
            }
        }

        return k < tokens.Count && tokens[k].Kind is GateTokenKind.Word or GateTokenKind.Name ? null : first.Text + " with no table";
    }

    /// <summary>The index after the <c>)</c> closing the <c>(</c> at <paramref name="open"/> (the end when none does).</summary>
    private static int SkipParentheses(IReadOnlyList<GateToken> tokens, int open)
    {
        int depth = 0;
        for (int i = open; i < tokens.Count; i++)
        {
            depth += tokens[i].IsSymbol("(") ? 1 : tokens[i].IsSymbol(")") ? -1 : 0;
            if (depth == 0)
            {
                return i + 1;
            }
        }

        return tokens.Count;
    }

    /// <summary>What a lexed body holds that the top level refuses (<see cref="BodyRefusal"/>).</summary>
    private static (string What, string Why)? InnerRefusal(List<GateToken> inner)
    {
        for (int i = 0; i < inner.Count; i++)
        {
            if (Scan(inner, i) is { } scanned)
            {
                return scanned;
            }

            var t = inner[i];
            if (t.Kind != GateTokenKind.Word || (i > 0 && inner[i - 1].IsSymbol(".")) || (i + 1 < inner.Count && inner[i + 1].IsSymbol("(")))
            {
                continue;
            }

            if (BodyRefused.TryGetValue(t.Text, out var why))
            {
                return (t.Text, why);
            }

            bool start = i == 0 || inner[i - 1].IsSymbol(";") || inner[i - 1].IsSymbol(">>")
                || (inner[i - 1].Kind == GateTokenKind.Word && inner[i - 1].Text is "BEGIN" or "THEN" or "ELSE" or "LOOP" or "DECLARE" or "EXCEPTION" or "ATOMIC");
            if (start && t.Text is "SET" or "RESET")
            {
                return (t.Text, ServerWriteText.Session);
            }

            if (t.Text is "CREATE" or "ALTER" or "DROP")
            {
                int at = i + 1;
                while (at < inner.Count && inner[at].Kind == GateTokenKind.Word && Rules.Modifiers.Contains(inner[at].Text))
                {
                    at++;
                }

                if (at < inner.Count && inner[at].Kind == GateTokenKind.Word && RefusedObjects.TryGetValue(inner[at].Text, out var objectWhy))
                {
                    return (t.Text + " " + inner[at].Text, objectWhy);
                }
            }
        }

        return null;
    }

    /// <summary>Whether a token is a string literal: <c>'…'</c>, <c>$tag$…$tag$</c>, or a prefixed one (<c>E'…'</c>, <c>B'…'</c>, <c>X'…'</c>, <c>N'…'</c>).</summary>
    private static bool IsString(GateToken token) => token.Kind == GateTokenKind.Other && token.Text.Length >= 2
        && (token.Text[0] is '\'' or '$' || (char.ToUpperInvariant(token.Text[0]) is 'E' or 'B' or 'X' or 'N' && token.Text[1] == '\''));

    /// <summary>A word's or a quoted name's or a string's value: <c>plpgsql</c>, <c>"plpgsql"</c>, <c>'plpgsql'</c> all give <c>plpgsql</c>.</summary>
    private static string Unquoted(GateToken token) =>
        token.Kind == GateTokenKind.Other && StringValue(token.Text) is { } value ? value : token.Text;

    /// <summary>A string literal's value from its source: <c>'…'</c> with <c>''</c> undoubled, or <c>$tag$…$tag$</c>; null for an <c>E'…'</c> or anything else (refused as unclear rather than decoded).</summary>
    internal static string? StringValue(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length >= 2 && source[0] == '\'' && source[^1] == '\'')
        {
            return source[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (source.Length >= 2 && source[0] == '$')
        {
            int tag = source.IndexOf('$', 1);
            if (tag > 0 && source.Length >= 2 * (tag + 1) && source.EndsWith(source[..(tag + 1)], StringComparison.Ordinal))
            {
                return source[(tag + 1)..^(tag + 1)];
            }
        }

        return null;
    }

    /// <summary>A body's <c>$1</c>, <c>$2</c>… as names (<c>_arg1</c>), so the read gate's lexer, which refuses a positional placeholder, reads it.</summary>
    private static string ParametersAsNames(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '$' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]) && !(i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')))
            {
                sb.Append(" _arg");
                continue;
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
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

    /// <summary>The kinds <paramref name="sql"/> needs; null when it does not lex or is none <c>postgres_execute</c> runs.</summary>
    public static IReadOnlyList<ServerStatementKind>? Kinds(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return Lex(sql, out _) is { } tokens ? ServerWriteGate.Kinds(Rules, tokens) : null;
    }

    /// <summary>The text as <see cref="ServerWriteGate"/>'s tokens (<see cref="PostgresBoundary"/> reads them too, 2026-10-09); null with the error when it does not lex.</summary>
    internal static List<GateToken>? Lex(string sql, out string? error)
    {
        if (PostgresReadOnlyGate.Tokenize(sql, out error) is not { } tokens)
        {
            return null;
        }

        return Map(tokens);
    }

    private static List<GateToken> Map(IEnumerable<PostgresReadOnlyGate.Token> tokens) =>
        tokens.Select(t => new GateToken(
            t.Kind switch
            {
                PostgresReadOnlyGate.TokenKind.Word or PostgresReadOnlyGate.TokenKind.OperatorBind => GateTokenKind.Word,
                PostgresReadOnlyGate.TokenKind.Quoted => GateTokenKind.Name,
                PostgresReadOnlyGate.TokenKind.Symbol => GateTokenKind.Symbol,
                _ => GateTokenKind.Other,
            },
            // An unbound @name after an operator is a bare name to the server, folded as one (2026-10-24, the database review's F5: kept
            // as written, as a quoted name is, it missed a lock's names in another case).
            t.Kind == PostgresReadOnlyGate.TokenKind.OperatorBind ? PostgresReadOnlyGate.AsciiUpper(t.Text) : t.Text)).ToList();

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

        if (t.Kind != GateTokenKind.Word || (i > 0 && tokens[i - 1].IsSymbol(".")))
        {
            return null;
        }

        // 2026-10-24, the database review's Phase 8. GRANT, REVOKE and AUTHORIZATION anywhere (C2: CREATE SCHEMA s AUTHORIZATION r
        // GRANT … ran its elements); SECURITY DEFINER (DB3: who a function runs as); TABLESPACE anywhere (H7: SET TABLESPACE and ALL IN
        // TABLESPACE moved tables past the lock); ROW LEVEL SECURITY (F12: DISABLE and NO FORCE lifted policies, which POLICY statements
        // may not touch); COMMENT ON a database, role or the like (F13: a cluster-wide change); NOTIFY anywhere (a rule's action).
        var after = i + 2 < tokens.Count ? tokens[i + 2] : default;
        switch (t.Text)
        {
            case "GRANT" or "REVOKE" or "AUTHORIZATION":
                return (t.Text, ServerWriteText.Accounts);
            case "SECURITY" when next.IsWord("DEFINER"):
                return ("SECURITY DEFINER", ServerWriteText.RunsAs);
            case "TABLESPACE":
                return ("TABLESPACE", ServerWriteText.ServerWide);
            case "LEVEL" when i > 0 && tokens[i - 1].IsWord("ROW") && next.IsWord("SECURITY"):
                return ("ROW LEVEL SECURITY", ServerWriteText.Accounts);
            case "COMMENT" when next.IsWord("ON") && after.Kind == GateTokenKind.Word && RefusedObjects.TryGetValue(after.Text, out var why):
                return ("COMMENT ON " + after.Text, why);
            case "NOTIFY":
                return ("NOTIFY", ServerWriteText.Denied);
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
        ServerStatementKind.Create => "CREATE TABLE, INDEX, VIEW, MATERIALIZED VIEW, SEQUENCE, TYPE, DOMAIN, SCHEMA; SELECT … INTO",
        ServerStatementKind.Alter => "ALTER TABLE, INDEX, VIEW, SEQUENCE, TYPE, DOMAIN, SCHEMA; COMMENT ON; CREATE OR REPLACE VIEW (with creating)",
        ServerStatementKind.Drop => "DROP TABLE, INDEX, VIEW, SEQUENCE, TYPE, DOMAIN, SCHEMA, FUNCTION, PROCEDURE, TRIGGER",
        ServerStatementKind.Upkeep => "VACUUM, ANALYZE, REINDEX, CLUSTER, REFRESH MATERIALIZED VIEW",
        ServerStatementKind.Procedures => "CALL, DO; CREATE or ALTER FUNCTION, PROCEDURE (SQL or PL/pgSQL), TRIGGER, RULE; ENABLE|DISABLE TRIGGER|RULE",
        _ => "SELECT, VALUES, TABLE",
    };

    public static readonly ServerWriteFamily Family = new(
        "PostgreSQL", "PostgreSQL", "postgres_execute", "postgres.json", "database", PostgresConfigFile.Category,
        nameof(Settings.AppSettingsData.PostgresMode), Statements);
}
