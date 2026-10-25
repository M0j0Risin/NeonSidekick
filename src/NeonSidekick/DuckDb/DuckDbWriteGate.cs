using static NeonSidekick.DuckDb.DuckDbReadOnlyGate;

namespace NeonSidekick.DuckDb;

/// <summary>
/// Whether <c>duckdb_execute</c> may run a text (2026-10-25, <c>DuckDB mode</c> <c>read-write</c>), <c>SqliteWriteGate</c>'s twin: the read
/// gate's lexer (<see cref="DuckDbReadOnlyGate.Tokenize"/>), a wider door. Allowed: one statement (DuckDB has no trigger body, so every
/// <c>;</c> but a trailing one is a second statement) of a kind <see cref="Classify(string)"/> knows and <c>DuckDB statements allowed</c>
/// ticks. Refused wherever they stand: ATTACH, DETACH and USE (another database), COPY, EXPORT and IMPORT (other files: <c>COPY … TO</c> wrote
/// into the working directory under a read-only open, the spike's finding), INSTALL and LOAD (code), PREPARE, EXECUTE, DEALLOCATE and CALL
/// (SQL as text), a secret made or dropped, <c>UPDATE EXTENSIONS</c>, and the read gate's function lists (a reader's argument a literal, the
/// denied table functions and <c>getenv</c>; <c>nextval</c> is a write's own). Refused as the statement's first word: the transaction words
/// (each call is a transaction of its own), SET and RESET (the settings are locked). An EXPLAIN shows only a read: <c>EXPLAIN ANALYZE</c> runs
/// its statement. Every file reference — a view's or macro's body too — is handed out (<see cref="Check(string, IReadOnlyList{DuckDbStatementKind}?, out IReadOnlyList{DuckDbFileReference})"/>)
/// for <see cref="DuckDbFilePaths"/>, so what a view stores is the checked full path.
/// </summary>
public static class DuckDbWriteGate
{
    /// <summary>Words that control a transaction, refused as a statement's first word.</summary>
    public static readonly IReadOnlySet<string> TransactionWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "BEGIN", "START", "COMMIT", "END", "ROLLBACK", "ABORT", "SAVEPOINT", "RELEASE",
    };

    /// <summary>Words refused wherever they stand, each with its reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> Refused = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ATTACH"] = DuckDbText.AnotherFile, ["DETACH"] = DuckDbText.AnotherFile, ["USE"] = DuckDbText.AnotherFile,
        ["COPY"] = DuckDbText.WritesFiles, ["EXPORT"] = DuckDbText.WritesFiles, ["IMPORT"] = DuckDbText.WritesFiles,
        ["INSTALL"] = DuckDbText.OutsideCode, ["LOAD"] = DuckDbText.OutsideCode,
        ["PREPARE"] = DuckDbText.RunsText, ["EXECUTE"] = DuckDbText.RunsText, ["DEALLOCATE"] = DuckDbText.RunsText, ["CALL"] = DuckDbText.RunsText,
    };

    /// <summary>Null when <paramref name="sql"/> may run; else the <c>Error:</c> sentence. <paramref name="allowed"/> null allows every kind.</summary>
    public static string? Check(string sql, IReadOnlyList<DuckDbStatementKind>? allowed = null) => Check(sql, allowed, out _);

    /// <summary><see cref="Check(string, IReadOnlyList{DuckDbStatementKind}?)"/>, and the file references the text holds (empty when refused).</summary>
    public static string? Check(string sql, IReadOnlyList<DuckDbStatementKind>? allowed, out IReadOnlyList<DuckDbFileReference> files)
    {
        ArgumentNullException.ThrowIfNull(sql);
        files = [];
        if (string.IsNullOrWhiteSpace(sql))
        {
            return DuckDbText.NoStatement;
        }

        if (Tokenize(sql, out var lexError) is not { } all)
        {
            return lexError;
        }

        var tokens = Statement(all);
        int lead = Lead(tokens);
        if (lead < 0)
        {
            return DuckDbText.NoStatement;
        }

        var first = tokens[lead];
        if (first.Kind == TokenKind.Word && TransactionWords.Contains(first.Text))
        {
            return DuckDbText.WriteForbidden(first.Text, DuckDbText.OwnTransaction);
        }

        if (first.Kind == TokenKind.Word && first.Text is "SET" or "RESET")
        {
            return DuckDbText.WriteForbidden(first.Text, DuckDbText.OutsideCode);
        }

        int separators = tokens.Count(t => IsSymbol(t, ";"));
        if (separators > 0)
        {
            return DuckDbText.WriteNotOneStatement(separators + 1);
        }

        for (int i = lead; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.Word && Refused.TryGetValue(tokens[i].Text, out string? why))
            {
                return DuckDbText.WriteForbidden(tokens[i].Text, why);
            }
        }

        if (first.Kind == TokenKind.Word && first.Text is "CREATE" or "DROP" && tokens.Skip(lead + 1).Take(4).Any(t => t.Kind == TokenKind.Word && t.Text == "SECRET"))
        {
            return DuckDbText.WriteForbidden(first.Text + " SECRET", DuckDbText.Secrets);
        }

        if (IsWord(tokens, lead, "UPDATE") && IsWord(tokens, lead + 1, "EXTENSIONS"))
        {
            return DuckDbText.WriteForbidden("UPDATE EXTENSIONS", DuckDbText.OutsideCode);
        }

        if (IsWord(tokens, lead, "EXPLAIN") && Classify(tokens, lead) != DuckDbStatementKind.Read)
        {
            return DuckDbText.WriteForbidden("EXPLAIN", DuckDbText.ExplainsAChange);
        }

        var found = new List<DuckDbFileReference>();
        if (Scan(sql, tokens, lead, found, what => DuckDbText.WriteForbidden(what, DuckDbText.NeverCalled), DuckDbFunctions.WriteDeniedScalars) is { } refused)
        {
            return refused;
        }

        if (Kinds(tokens, lead) is not { } kinds)
        {
            return DuckDbText.UnknownStatement(first.Kind == TokenKind.Word ? first.Text : "'" + first.Text + "'");
        }

        foreach (var kind in kinds)
        {
            if (allowed is not null && !allowed.Contains(kind))
            {
                return DuckDbText.KindNotAllowed(kind, allowed);
            }
        }

        files = found;
        return null;
    }

    /// <summary>The kind of the one statement <paramref name="sql"/> is; null when it does not lex or is none <c>duckdb_execute</c> knows.</summary>
    public static DuckDbStatementKind? Classify(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (Tokenize(sql, out _) is not { } all)
        {
            return null;
        }

        var tokens = Statement(all);
        int lead = Lead(tokens);
        return lead < 0 ? null : Classify(tokens, lead);
    }

    /// <summary>
    /// The kind by the statement's first word; a <c>WITH</c> by the statement after its common table expressions, an <c>EXPLAIN [ANALYZE]</c>
    /// by the statement it explains, <c>FORCE CHECKPOINT</c> upkeep, <c>COMMENT ON</c> a change of structure.
    /// </summary>
    private static DuckDbStatementKind? Classify(List<Token> tokens, int lead)
    {
        if (tokens[lead].Kind != TokenKind.Word)
        {
            return null;
        }

        int at = lead;
        if (tokens[at].Text == "EXPLAIN")
        {
            at++;
            if (IsWord(tokens, at, "ANALYZE") || IsWord(tokens, at, "ANALYSE"))
            {
                at++;
            }

            at = Lead(tokens, at);
            if (at < 0 || tokens[at].Kind != TokenKind.Word)
            {
                return null;
            }
        }

        string word = tokens[at].Text;
        if (word == "WITH")
        {
            if (AfterCommonTables(tokens, at) is not { } statement || tokens[statement].Kind != TokenKind.Word)
            {
                return null;
            }

            word = tokens[statement].Text;
        }

        return word switch
        {
            "INSERT" or "UPDATE" or "MERGE" => DuckDbStatementKind.Data,
            "DELETE" or "TRUNCATE" => DuckDbStatementKind.Delete,
            "CREATE" => DuckDbStatementKind.Create,
            "ALTER" or "COMMENT" => DuckDbStatementKind.Alter,
            "DROP" => DuckDbStatementKind.Drop,
            "CHECKPOINT" or "VACUUM" or "ANALYZE" or "ANALYSE" => DuckDbStatementKind.Upkeep,
            "FORCE" when IsWord(tokens, at + 1, "CHECKPOINT") => DuckDbStatementKind.Upkeep,
            "PRAGMA" => DuckDbStatementKind.Pragma,
            _ when ReadLeads.Contains(word) => DuckDbStatementKind.Read,
            _ => null,
        };
    }

    /// <summary>
    /// Where the statement after a <c>WITH</c>'s common table expressions starts, read by the grammar (SQLite's C1): <c>WITH [RECURSIVE] name
    /// [(columns)] [USING KEY (…)] AS [[NOT] MATERIALIZED] (…)</c>, comma-separated. Null when the text does not follow it.
    /// </summary>
    private static int? AfterCommonTables(List<Token> tokens, int lead)
    {
        int i = lead + 1;
        if (IsWord(tokens, i, "RECURSIVE"))
        {
            i++;
        }

        while (true)
        {
            if (i >= tokens.Count || tokens[i].Kind is not (TokenKind.Word or TokenKind.Quoted))
            {
                return null;
            }

            i++;
            if (i < tokens.Count && IsSymbol(tokens[i], "("))
            {
                i = Close(tokens, i);
            }

            if (IsWord(tokens, i, "USING") && IsWord(tokens, i + 1, "KEY") && i + 2 < tokens.Count && IsSymbol(tokens[i + 2], "("))
            {
                i = Close(tokens, i + 2);
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
    private static int Close(List<Token> tokens, int open)
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
    /// The kinds a statement needs: its own, deleting for every insert that replaces a row (<c>INSERT OR REPLACE</c>, <c>ON CONFLICT … DO
    /// UPDATE</c>: SQLite's A7) and a MERGE that deletes, and dropping for <c>CREATE OR REPLACE</c>, which drops what it replaces. A word
    /// before <c>(</c> is a function's (<c>replace()</c>).
    /// </summary>
    private static List<DuckDbStatementKind>? Kinds(List<Token> tokens, int lead)
    {
        if (Classify(tokens, lead) is not { } own)
        {
            return null;
        }

        var kinds = new HashSet<DuckDbStatementKind> { own };
        bool merge = false;
        for (int i = lead; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Word || (i + 1 < tokens.Count && IsSymbol(tokens[i + 1], "(")))
            {
                continue;
            }

            switch (tokens[i].Text)
            {
                case "MERGE":
                    merge = true;
                    break;
                case "REPLACE" when IsWord(tokens, i - 1, "OR"):
                    kinds.Add(IsWord(tokens, i - 2, "CREATE") ? DuckDbStatementKind.Drop : DuckDbStatementKind.Delete);
                    break;
                case "DELETE" when merge:
                    kinds.Add(DuckDbStatementKind.Delete);
                    break;
                case "UPDATE" when IsWord(tokens, i - 1, "DO"):
                    kinds.Add(DuckDbStatementKind.Delete);
                    break;
            }
        }

        return Enum.GetValues<DuckDbStatementKind>().Where(kinds.Contains).ToList();
    }
}
