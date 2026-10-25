namespace NeonSidekick.Sqlite;

/// <summary>
/// Whether <c>sqlite_execute</c> may run a text (2026-10-05, <c>SQLite mode</c> <c>read-write</c>): the read gate's
/// lexer (<see cref="SqliteReadOnlyGate.Tokenize"/>), a wider door. Allowed: one statement of any kind — DML (RETURNING too), DDL,
/// PRAGMA, VACUUM, REINDEX, ANALYZE, a SELECT — the user's pick of DML + DDL + PRAGMA. One statement means one <c>;</c>-free text
/// (a trailing one dropped), but a <c>CREATE TRIGGER</c>'s body between BEGIN and END holds its own <c>;</c>s, so there the gate
/// counts only those outside it. Refused wherever they stand: <c>ATTACH</c>/<c>DETACH</c> (another file), the denied functions
/// (<see cref="SqliteReadOnlyGate.DeniedFunctions"/>, a quoted name too), <c>sqlite_dbpage</c> (raw pages) and
/// <c>writable_schema</c> (both can corrupt the file), a positional <c>?</c>. Refused as the statement's first word only, since
/// BEGIN, END and ROLLBACK stand inside a trigger and an <c>ON CONFLICT</c>: the transaction words (each call is a transaction
/// of its own). And <c>VACUUM INTO</c>, which writes another file outside the sandbox's reach. Since later on 2026-10-05 the
/// statement's kind (<see cref="Classify(string)"/>) must also be one of those <c>SQLite statements allowed</c> ticks — a trigger's, and
/// those its body's changes need (the review, 2026-10-05).
/// </summary>
public static class SqliteWriteGate
{
    /// <summary>Words that start a transaction's control, refused as a statement's first word: the app makes each call its own transaction.</summary>
    public static readonly IReadOnlySet<string> TransactionWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "BEGIN", "COMMIT", "END", "ROLLBACK", "SAVEPOINT", "RELEASE",
    };

    /// <summary>
    /// Null when <paramref name="sql"/> may run; else the <c>Error:</c> sentence the model reads. <paramref name="allowed"/> is the
    /// kinds the user ticked (<see cref="SqliteStatementKinds.Resolve(Settings.AppSettingsData)"/>); null allows every kind.
    /// </summary>
    public static string? Check(string sql, IReadOnlyList<SqliteStatementKind>? allowed = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqliteText.NoStatement;
        }

        if (SqliteReadOnlyGate.Tokenize(sql, out var lexError) is not { } all)
        {
            return lexError;
        }

        var tokens = all.Count > 0 && IsSymbol(all[^1], ";") ? all[..^1] : all;
        int lead = tokens.FindIndex(t => !IsSymbol(t, "("));
        if (lead < 0)
        {
            return SqliteText.NoStatement;
        }

        var first = tokens[lead];
        if (first.Kind == SqliteReadOnlyGate.TokenKind.Word && TransactionWords.Contains(first.Text))
        {
            return SqliteText.WriteForbidden(first.Text, SqliteText.OwnTransaction);
        }

        if (Separators(tokens, lead) is int separators and > 0)
        {
            return SqliteText.WriteNotOneStatement(separators + 1);
        }

        bool vacuum = first.Kind == SqliteReadOnlyGate.TokenKind.Word && first.Text == "VACUUM";
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (IsSymbol(t, "?"))
            {
                return SqliteText.Positional;
            }

            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
            if (t.Kind is not (SqliteReadOnlyGate.TokenKind.Word or SqliteReadOnlyGate.TokenKind.Quoted or SqliteReadOnlyGate.TokenKind.String))
            {
                continue;
            }

            // A quoted name is a name all the same ("sqlite_dbpage", [load_extension](…)); SQLite's names are case-blind. So is a string
            // where a name fits (2026-10-24, the database review's I4: FROM 'sqlite_dbpage' and PRAGMA 'writable_schema' = 1 both work), so
            // the two that corrupt are refused as strings too — a value spelled the same way among them, which no data needs.
            string name = SqliteReadOnlyGate.NameOf(t);
            if (t.Kind != SqliteReadOnlyGate.TokenKind.String && IsSymbol(next, "(") && SqliteReadOnlyGate.DeniedFunctions.Contains(name) && name != "SQLITE_DBPAGE")
            {
                return SqliteText.WriteForbidden(name.ToLowerInvariant() + "()", SqliteText.OutsideCode);
            }

            if (name is "SQLITE_DBPAGE" or "WRITABLE_SCHEMA")
            {
                return SqliteText.WriteForbidden(name.ToLowerInvariant(), SqliteText.Corrupts);
            }

            if (t.Kind != SqliteReadOnlyGate.TokenKind.Word)
            {
                continue;
            }

            if (name is "ATTACH" or "DETACH")
            {
                return SqliteText.WriteForbidden(name, SqliteText.AnotherFile);
            }

            if (vacuum && name == "INTO")
            {
                return SqliteText.WriteForbidden("VACUUM INTO", SqliteText.AnotherFile);
            }
        }

        if (PragmaRefusal(tokens, lead) is { } pragma)
        {
            return pragma;
        }

        if (Kinds(tokens, lead) is not { } kinds)
        {
            return SqliteText.UnknownStatement(first.Kind == SqliteReadOnlyGate.TokenKind.Word ? first.Text : "'" + first.Text + "'");
        }

        foreach (var kind in kinds)
        {
            if (allowed is not null && !allowed.Contains(kind))
            {
                return SqliteText.KindNotAllowed(kind, allowed);
            }
        }

        return null;
    }

    /// <summary>
    /// The pragmas no statement kind covers (2026-10-24, the database review's C9, G9): setting <c>schema_version</c> (SQLite's own words:
    /// misuse "can result in database corruption"), and the process-wide ones — <c>hard_heap_limit</c>, <c>soft_heap_limit</c>,
    /// <c>temp_store_directory</c>, <c>data_store_directory</c> — which reach the app's own session and skill stores in the same process
    /// and can put temporary files outside the sandbox. Each is refused when set (<c>= value</c> or <c>(value)</c>), and as a
    /// <c>pragma_…</c> function too; reading one passes.
    /// </summary>
    public static readonly IReadOnlySet<string> RefusedPragmas = new HashSet<string>(StringComparer.Ordinal)
    {
        "SCHEMA_VERSION", "HARD_HEAP_LIMIT", "SOFT_HEAP_LIMIT", "TEMP_STORE_DIRECTORY", "DATA_STORE_DIRECTORY",
    };

    /// <summary>The refusal for a <see cref="RefusedPragmas"/> one set by a PRAGMA (or called as a <c>pragma_</c> function anywhere); null for the rest.</summary>
    private static string? PragmaRefusal(List<SqliteReadOnlyGate.Token> tokens, int lead)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind is SqliteReadOnlyGate.TokenKind.Word or SqliteReadOnlyGate.TokenKind.Quoted && i + 1 < tokens.Count && IsSymbol(tokens[i + 1], "(")
                && SqliteReadOnlyGate.NameOf(tokens[i]) is { } function && function.StartsWith("PRAGMA_", StringComparison.Ordinal) && RefusedPragmas.Contains(function["PRAGMA_".Length..]))
            {
                return SqliteText.WriteForbidden(function.ToLowerInvariant() + "()", SqliteText.Corrupts);
            }
        }

        if (!IsWord(tokens, lead, "PRAGMA"))
        {
            return null;
        }

        int at = lead + 1;
        if (at + 2 < tokens.Count && IsSymbol(tokens[at + 1], "."))
        {
            at += 2;   // schema.name
        }

        if (at >= tokens.Count || tokens[at].Kind is not (SqliteReadOnlyGate.TokenKind.Word or SqliteReadOnlyGate.TokenKind.Quoted or SqliteReadOnlyGate.TokenKind.String))
        {
            return null;
        }

        string pragma = SqliteReadOnlyGate.NameOf(tokens[at]);
        bool set = at + 1 < tokens.Count && (IsSymbol(tokens[at + 1], "=") || IsSymbol(tokens[at + 1], "("));
        return set && RefusedPragmas.Contains(pragma) ? SqliteText.WriteForbidden("PRAGMA " + pragma.ToLowerInvariant(), SqliteText.Corrupts) : null;
    }

    /// <summary>The kind of the one statement <paramref name="sql"/> is; null when it does not lex or is none <c>sqlite_execute</c> knows.</summary>
    public static SqliteStatementKind? Classify(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (SqliteReadOnlyGate.Tokenize(sql, out _) is not { } tokens)
        {
            return null;
        }

        int lead = tokens.FindIndex(t => !IsSymbol(t, "("));
        return lead < 0 ? null : Classify(tokens, lead);
    }

    /// <summary>
    /// The kind by the statement's first word; a <c>WITH</c> by the first statement word after its common table expressions (the
    /// first SELECT, VALUES, INSERT, REPLACE, UPDATE or DELETE outside every parenthesis), an <c>EXPLAIN</c> a read (it runs
    /// nothing).
    /// </summary>
    private static SqliteStatementKind? Classify(List<SqliteReadOnlyGate.Token> tokens, int lead)
    {
        if (tokens[lead].Kind != SqliteReadOnlyGate.TokenKind.Word)
        {
            return null;
        }

        string word = tokens[lead].Text;
        if (word == "WITH")
        {
            if (AfterCommonTables(tokens, lead) is not { } statement || tokens[statement].Kind != SqliteReadOnlyGate.TokenKind.Word)
            {
                return null;
            }

            word = tokens[statement].Text;
        }

        return word switch
        {
            "INSERT" or "REPLACE" or "UPDATE" => SqliteStatementKind.Data,
            "DELETE" => SqliteStatementKind.Delete,
            "CREATE" => SqliteStatementKind.Create,
            "ALTER" => SqliteStatementKind.Alter,
            "DROP" => SqliteStatementKind.Drop,
            "VACUUM" or "REINDEX" or "ANALYZE" => SqliteStatementKind.Upkeep,
            "PRAGMA" => SqliteStatementKind.Pragma,
            "SELECT" or "VALUES" or "EXPLAIN" => SqliteStatementKind.Read,
            _ => null,
        };
    }

    /// <summary>
    /// Where the statement after a <c>WITH</c>'s common table expressions starts, read by the grammar (2026-10-24, the database review's
    /// C1: the first SELECT, INSERT, REPLACE, UPDATE or DELETE word outside every parenthesis was taken, so a CTE named <c>replace</c> made
    /// <c>WITH replace AS (…) DELETE FROM users</c> changing data): <c>WITH [RECURSIVE] name [(columns)] AS [[NOT] MATERIALIZED] (…)</c>,
    /// comma-separated. Null when the text does not follow it (refused as unknown).
    /// </summary>
    private static int? AfterCommonTables(List<SqliteReadOnlyGate.Token> tokens, int lead)
    {
        int i = lead + 1;
        if (IsWord(tokens, i, "RECURSIVE"))
        {
            i++;
        }

        while (true)
        {
            if (i >= tokens.Count || tokens[i].Kind is not (SqliteReadOnlyGate.TokenKind.Word or SqliteReadOnlyGate.TokenKind.Quoted or SqliteReadOnlyGate.TokenKind.String))
            {
                return null;
            }

            i++;
            if (i < tokens.Count && IsSymbol(tokens[i], "("))
            {
                i = Close(tokens, i);
            }

            if (!IsWord(tokens, i, "AS"))
            {
                return null;
            }

            i++;
            if (IsWord(tokens, i, "NOT"))
            {
                i++;
            }

            if (IsWord(tokens, i, "MATERIALIZED"))
            {
                i++;
            }

            if (!(i < tokens.Count && IsSymbol(tokens[i], "(")))
            {
                return null;
            }

            i = Close(tokens, i);
            if (i < tokens.Count && IsSymbol(tokens[i], ","))
            {
                i++;
                continue;
            }

            return i < tokens.Count ? i : null;
        }
    }

    /// <summary>The index after the <c>)</c> closing the <c>(</c> at <paramref name="open"/>; the end when none does.</summary>
    private static int Close(List<SqliteReadOnlyGate.Token> tokens, int open)
    {
        int depth = 0;
        for (int i = open; i < tokens.Count; i++)
        {
            depth += IsSymbol(tokens[i], "(") ? 1 : IsSymbol(tokens[i], ")") ? -1 : 0;
            if (depth == 0)
            {
                return i + 1;
            }
        }

        return tokens.Count;
    }

    /// <summary>
    /// The <c>;</c>s that end a statement: every one, but in a <c>CREATE [TEMP|TEMPORARY] TRIGGER</c> only those before its first
    /// BEGIN and after its body's END (<see cref="TriggerEnd"/>).
    /// </summary>
    private static int Separators(List<SqliteReadOnlyGate.Token> tokens, int lead)
    {
        int end = IsTrigger(tokens, lead) ? TriggerEnd(tokens, lead) : -1;
        int begin = end < 0 ? -1 : tokens.FindIndex(lead, end - lead, t => t.Kind == SqliteReadOnlyGate.TokenKind.Word && t.Text == "BEGIN");
        if (begin < 0)
        {
            end = -1;   // no body: every ; counts
        }

        int count = 0;
        for (int i = lead; i < tokens.Count; i++)
        {
            count += IsSymbol(tokens[i], ";") && (end < 0 || i < begin || i > end) ? 1 : 0;
        }

        return count;
    }

    private static bool IsTrigger(List<SqliteReadOnlyGate.Token> tokens, int lead) => IsWord(tokens, lead, "CREATE")
        && (IsWord(tokens, lead + 1, "TRIGGER") || (IsWord(tokens, lead + 1, "TEMP") || IsWord(tokens, lead + 1, "TEMPORARY")) && IsWord(tokens, lead + 2, "TRIGGER"));

    /// <summary>
    /// Where a trigger's body ends: the first END straight after a <c>;</c>, or -1. Since the review of 2026-10-05 (BEGIN and END
    /// were counted as they came, and SQLite lets both stand as names: <c>… UPDATE OF begin ON x BEGIN SELECT 1; END; DELETE FROM
    /// users</c> passed as one statement, and the DELETE ran) the end is found as SQLite's grammar finds it: every statement of a
    /// body ends with <c>;</c>, the body with END, and after a <c>;</c> there only another statement or that END may stand, so the
    /// first END after a <c>;</c> is the body's whatever BEGIN, END or CASE words its header or its expressions hold.
    /// </summary>
    private static int TriggerEnd(List<SqliteReadOnlyGate.Token> tokens, int lead)
    {
        for (int i = lead + 1; i < tokens.Count; i++)
        {
            if (IsWord(tokens, i, "END") && IsSymbol(tokens[i - 1], ";"))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The kinds a statement needs: its own (<c>Classify</c>), and for a trigger also
    /// those of its body's changes (the review, 2026-10-05: creating is on by default, and a trigger whose body deletes, made under
    /// it, deleted on the next INSERT although deleting was off). The body is read from the first BEGIN to its END, so a header's
    /// name spelled BEGIN can only add a kind. A word before <c>(</c> is a function's (<c>replace()</c>).
    /// </summary>
    private static List<SqliteStatementKind>? Kinds(List<SqliteReadOnlyGate.Token> tokens, int lead)
    {
        if (Classify(tokens, lead) is not { } own)
        {
            return null;
        }

        var kinds = new HashSet<SqliteStatementKind> { own };

        // REPLACE in any form deletes the row it replaces (2026-10-24, the database review's A7): REPLACE INTO, INSERT OR REPLACE, UPDATE
        // OR REPLACE, and a table or a trigger made with ON CONFLICT REPLACE, whose later inserts do. A REPLACE before ( is the function.
        for (int i = lead; i < tokens.Count; i++)
        {
            if (IsWord(tokens, i, "REPLACE") && !(i + 1 < tokens.Count && IsSymbol(tokens[i + 1], "(")))
            {
                kinds.Add(SqliteStatementKind.Delete);
            }
        }

        if (IsTrigger(tokens, lead))
        {
            int begin = tokens.FindIndex(lead, t => t.Kind == SqliteReadOnlyGate.TokenKind.Word && t.Text == "BEGIN");
            int end = TriggerEnd(tokens, lead);
            for (int i = Math.Max(begin, lead); i < (end < 0 ? tokens.Count : end); i++)
            {
                if (tokens[i].Kind != SqliteReadOnlyGate.TokenKind.Word || (i + 1 < tokens.Count && IsSymbol(tokens[i + 1], "(")))
                {
                    continue;
                }

                if (tokens[i].Text is "INSERT" or "UPDATE" or "REPLACE")
                {
                    kinds.Add(SqliteStatementKind.Data);
                }
                else if (tokens[i].Text == "DELETE")
                {
                    kinds.Add(SqliteStatementKind.Delete);
                }
            }
        }

        return Enum.GetValues<SqliteStatementKind>().Where(kinds.Contains).ToList();
    }

    private static bool IsWord(List<SqliteReadOnlyGate.Token> tokens, int index, string word) =>
        index < tokens.Count && tokens[index].Kind == SqliteReadOnlyGate.TokenKind.Word && tokens[index].Text == word;

    private static bool IsSymbol(SqliteReadOnlyGate.Token token, string symbol) => token.Kind == SqliteReadOnlyGate.TokenKind.Symbol && token.Text == symbol;
}
