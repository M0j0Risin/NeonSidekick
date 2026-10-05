namespace NeonSidekick.Sqlite;

/// <summary>
/// Whether <c>sqlite_execute</c> may run a text (2026-10-05, <c>SQLite protection mode</c> <c>read-write</c>): the read gate's
/// lexer (<see cref="SqliteReadOnlyGate.Tokenize"/>), a wider door. Allowed: one statement of any kind — DML (RETURNING too), DDL,
/// PRAGMA, VACUUM, REINDEX, ANALYZE, a SELECT — the user's pick of DML + DDL + PRAGMA. One statement means one <c>;</c>-free text
/// (a trailing one dropped), but a <c>CREATE TRIGGER</c>'s body between BEGIN and END holds its own <c>;</c>s, so there the gate
/// counts only those outside it. Refused wherever they stand: <c>ATTACH</c>/<c>DETACH</c> (another file), the denied functions
/// (<see cref="SqliteReadOnlyGate.DeniedFunctions"/>, a quoted name too), <c>sqlite_dbpage</c> (raw pages) and
/// <c>writable_schema</c> (both can corrupt the file), a positional <c>?</c>. Refused as the statement's first word only, since
/// BEGIN, END and ROLLBACK stand inside a trigger and an <c>ON CONFLICT</c>: the transaction words (each call is a transaction
/// of its own). And <c>VACUUM INTO</c>, which writes another file outside the sandbox's reach.
/// </summary>
public static class SqliteWriteGate
{
    /// <summary>Words that start a transaction's control, refused as a statement's first word: the app makes each call its own transaction.</summary>
    public static readonly IReadOnlySet<string> TransactionWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "BEGIN", "COMMIT", "END", "ROLLBACK", "SAVEPOINT", "RELEASE",
    };

    /// <summary>Null when <paramref name="sql"/> may run; else the <c>Error:</c> sentence the model reads.</summary>
    public static string? Check(string sql)
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
            if (t.Kind is not (SqliteReadOnlyGate.TokenKind.Word or SqliteReadOnlyGate.TokenKind.Quoted))
            {
                continue;
            }

            // A quoted name is a name all the same ("sqlite_dbpage", [load_extension](…)); SQLite's names are case-blind.
            string name = t.Kind == SqliteReadOnlyGate.TokenKind.Word ? t.Text : t.Text.ToUpperInvariant();
            if (IsSymbol(next, "(") && SqliteReadOnlyGate.DeniedFunctions.Contains(name) && name != "SQLITE_DBPAGE")
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

        return null;
    }

    /// <summary>
    /// The <c>;</c>s that end a statement: every one, but in a <c>CREATE [TEMP|TEMPORARY] TRIGGER</c> only those outside its
    /// BEGIN … END body (a CASE … END inside it nests, so its END does not close the body).
    /// </summary>
    private static int Separators(List<SqliteReadOnlyGate.Token> tokens, int lead)
    {
        bool trigger = IsWord(tokens, lead, "CREATE")
            && (IsWord(tokens, lead + 1, "TRIGGER") || (IsWord(tokens, lead + 1, "TEMP") || IsWord(tokens, lead + 1, "TEMPORARY")) && IsWord(tokens, lead + 2, "TRIGGER"));
        int count = 0, depth = 0;
        for (int i = lead; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (IsSymbol(t, ";"))
            {
                count += depth == 0 ? 1 : 0;
            }
            else if (trigger && t.Kind == SqliteReadOnlyGate.TokenKind.Word)
            {
                if (t.Text is "BEGIN" or "CASE")
                {
                    depth++;
                }
                else if (t.Text == "END" && depth > 0)
                {
                    depth--;
                }
            }
        }

        return count;
    }

    private static bool IsWord(List<SqliteReadOnlyGate.Token> tokens, int index, string word) =>
        index < tokens.Count && tokens[index].Kind == SqliteReadOnlyGate.TokenKind.Word && tokens[index].Text == word;

    private static bool IsSymbol(SqliteReadOnlyGate.Token token, string symbol) => token.Kind == SqliteReadOnlyGate.TokenKind.Symbol && token.Text == symbol;
}
