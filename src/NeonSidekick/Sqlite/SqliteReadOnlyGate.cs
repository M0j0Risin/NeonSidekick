using System.Text;

namespace NeonSidekick.Sqlite;

/// <summary>
/// Whether <c>sqlite_query</c> may run a text (2026-10-04), <c>MySqlReadOnlyGate</c>'s part for SQLite: the first layer, ahead of
/// the file opened read-only, <c>PRAGMA query_only</c> and a transaction always rolled back (<see cref="SqliteAccess"/>). A
/// <b>lexer</b> for SQLite's rules, never a pattern: <c>--</c> and <c>/* */</c> comments (not nested); <c>'…'</c> strings with
/// <c>''</c>; names in <c>"…"</c>, <c>[…]</c> and <c>`…`</c>; <c>@name</c>, <c>:name</c>, <c>$name</c> and <c>#name</c> placeholders
/// read as SQLite's tokenizer reads them (<see cref="VariableLength"/>). Allowed: one
/// statement (a trailing <c>;</c> dropped) that starts <c>SELECT</c>, <c>WITH</c> or <c>VALUES</c>. Refused wherever it stands: the
/// changing words (a <c>WITH</c> may lead an <c>INSERT</c>, so they are refused anywhere; <c>REPLACE</c> only before <c>INTO</c>,
/// since <c>replace()</c> is a string function), <c>ATTACH</c>/<c>DETACH</c> (another file), <c>PRAGMA</c>, the transaction words,
/// and <see cref="DeniedFunctions"/>. A positional <c>?</c> is refused: <c>params</c> binds names.
/// </summary>
public static class SqliteReadOnlyGate
{
    /// <summary>The kinds of token the lexer yields.</summary>
    public enum TokenKind
    {
        /// <summary>An unquoted word, upper-cased: a keyword or a name.</summary>
        Word,

        /// <summary>A quoted name (<c>"…"</c>, <c>[…]</c>, <c>`…`</c>); the text is what is inside.</summary>
        Quoted,

        /// <summary>A string literal; the text is its source.</summary>
        String,

        /// <summary>A numeric literal.</summary>
        Number,

        /// <summary>A named placeholder; the text is it with its prefix (<c>@id</c>, <c>:id</c>, <c>$id</c>, <c>#id</c>, <c>$a::b</c>, <c>$a(x)</c>).</summary>
        Bind,

        /// <summary>Any other character: <c>(</c>, <c>;</c>, <c>?</c>, an operator.</summary>
        Symbol,
    }

    /// <summary>One token and where it starts (1-based line and column).</summary>
    public readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);

    /// <summary>Functions a query may not call: an extension loaded into the process, a file written or read, a tokenizer pointer swapped.</summary>
    public static readonly IReadOnlySet<string> DeniedFunctions = new HashSet<string>(StringComparer.Ordinal)
    {
        "LOAD_EXTENSION", "WRITEFILE", "READFILE", "EDIT", "FTS3_TOKENIZER", "SQLITE_DBPAGE",
    };

    /// <summary>The words that change data or the schema, reach another file, or are not a query; refused wherever they stand.</summary>
    public static readonly IReadOnlySet<string> ChangingWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "INSERT", "UPDATE", "DELETE", "UPSERT", "RETURNING", "CREATE", "DROP", "ALTER", "ATTACH", "DETACH", "PRAGMA", "VACUUM",
        "REINDEX", "ANALYZE", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "TRUNCATE",
    };

    /// <summary>Null when <paramref name="sql"/> may run; else the <c>Error:</c> sentence the model reads.</summary>
    public static string? Check(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqliteText.NoSql;
        }

        if (Tokenize(sql, out var lexError) is not { } all)
        {
            return lexError;
        }

        var tokens = Statement(all);
        int lead = tokens.FindIndex(t => !IsSymbol(t, "("));
        if (lead < 0)
        {
            return SqliteText.NoSql;
        }

        var first = tokens[lead];
        if (first.Kind != TokenKind.Word || first.Text is not ("SELECT" or "WITH" or "VALUES"))
        {
            return SqliteText.NotASelect(first.Kind == TokenKind.Word ? first.Text : "'" + first.Text + "'");
        }

        int separators = tokens.Count(t => IsSymbol(t, ";"));
        if (separators > 0)
        {
            return SqliteText.NotOneStatement(separators + 1);
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (IsSymbol(t, "?"))
            {
                return SqliteText.Positional;
            }

            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;

            // A quoted name is a function's name too: "load_extension"('x.dll') and [fts3_tokenizer](…) call them (the 2026-10-04
            // review found both past the gate, held only by Microsoft.Data.Sqlite's defaults). SQLite's names are case-blind.
            if (t.Kind == TokenKind.Quoted)
            {
                if (IsSymbol(next, "(") && DeniedFunctions.Contains(t.Text.ToUpperInvariant()))
                {
                    return SqliteText.Forbidden(t.Text.ToLowerInvariant() + "()");
                }

                continue;
            }

            if (t.Kind != TokenKind.Word)
            {
                continue;
            }

            if (ChangingWords.Contains(t.Text))
            {
                return SqliteText.Forbidden(t.Text);
            }

            if (t.Text == "REPLACE" && next.Kind == TokenKind.Word && next.Text == "INTO")
            {
                return SqliteText.Forbidden("REPLACE INTO");
            }

            if (DeniedFunctions.Contains(t.Text) && IsSymbol(next, "("))
            {
                return SqliteText.Forbidden(t.Text.ToLowerInvariant() + "()");
            }
        }

        return null;
    }

    /// <summary>
    /// The named placeholders the text uses, each once, as written (<c>@id</c>, <c>:name</c>); empty when the text does not lex.
    /// Told apart by case (code review, 2026-10-04): SQLite's names are case-sensitive, so <c>:id</c> and <c>:ID</c> are two, and
    /// folding them left one unbound for Microsoft.Data.Sqlite to throw on.
    /// </summary>
    public static IReadOnlyList<string> Binds(string sql) =>
        Tokenize(sql ?? "", out _) is { } tokens ? tokens.Where(t => t.Kind == TokenKind.Bind).Select(t => t.Text).Distinct(StringComparer.Ordinal).ToList() : [];

    /// <summary>
    /// A <c>params</c> name as <see cref="SqliteAccess"/> looks it up: the name after the placeholder's mark, so <c>id</c>,
    /// <c>@id</c>, <c>:id</c>, <c>$id</c> and <c>#id</c> all name <c>id</c> (one leading mark dropped), and a name SQLite reads in
    /// a placeholder whole — <c>a$b</c>, <c>ñame</c>, the TCL forms <c>a::b</c> and <c>a(1)</c>; null when it is not one (the fourth
    /// 2026-10-04 review: the other families' ASCII rule refused <c>{":id": 5}</c>, which sqlite_query's own doc invited, and left
    /// such placeholders bound NULL with no way to name them).
    /// </summary>
    public static string? ParamName(string written)
    {
        ArgumentNullException.ThrowIfNull(written);
        string placeholder = written.Length > 0 && written[0] is '@' or ':' or '$' or '#' ? written : "@" + written;
        return placeholder.Length > 1 && VariableLength(placeholder, 0) == placeholder.Length ? placeholder[1..] : null;
    }

    /// <summary>The text without a trailing <c>;</c> (and what follows it of blanks and comments): what runs.</summary>
    public static string Body(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        string trimmed = sql.TrimEnd();
        return trimmed.EndsWith(';') ? trimmed[..^1].TrimEnd() : trimmed;
    }

    /// <summary>The tokens up to a last <c>;</c> that only blanks and comments follow (dropped); everything when there is none.</summary>
    private static List<Token> Statement(List<Token> tokens) =>
        tokens.Count > 0 && IsSymbol(tokens[^1], ";") ? tokens[..^1] : tokens;

    private static bool IsSymbol(Token token, string symbol) => token.Kind == TokenKind.Symbol && token.Text == symbol;

    /// <summary>The text as tokens, comments dropped; null with the sentence for a string, name or comment that never ends.</summary>
    public static List<Token>? Tokenize(string sql, out string? error)
    {
        ArgumentNullException.ThrowIfNull(sql);
        error = null;
        var tokens = new List<Token>();
        int line = 1, lineStart = 0, i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            int column = i - lineStart + 1;
            if (c == '\n')
            {
                line++;
                lineStart = i + 1;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    error = SqliteText.Unterminated("comment", line, column);
                    return null;
                }

                for (int k = i; k < end; k++)
                {
                    if (sql[k] == '\n')
                    {
                        line++;
                        lineStart = k + 1;
                    }
                }

                i = end + 2;
                continue;
            }

            if (c is '\'' or '"' or '`' or '[')
            {
                char close = c == '[' ? ']' : c;
                var text = new StringBuilder();
                int k = i + 1;
                bool closed = false;
                while (k < sql.Length)
                {
                    if (sql[k] == close)
                    {
                        if (close != ']' && k + 1 < sql.Length && sql[k + 1] == close)
                        {
                            text.Append(close);
                            k += 2;
                            continue;
                        }

                        closed = true;
                        break;
                    }

                    if (sql[k] == '\n')
                    {
                        line++;
                        lineStart = k + 1;
                    }

                    text.Append(sql[k]);
                    k++;
                }

                if (!closed)
                {
                    error = SqliteText.Unterminated(c == '\'' ? "string" : "quoted name", line, column);
                    return null;
                }

                tokens.Add(new Token(c == '\'' ? TokenKind.String : TokenKind.Quoted, c == '\'' ? sql[i..(k + 1)] : text.ToString(), line, column));
                i = k + 1;
                continue;
            }

            if (c is '@' or ':' or '$' or '#' && VariableLength(sql, i) is int length and > 0)
            {
                tokens.Add(new Token(TokenKind.Bind, sql.Substring(i, length), line, column));
                i += length;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1])))
            {
                int k = i + 1;
                while (k < sql.Length && (char.IsLetterOrDigit(sql[k]) || sql[k] == '.'))
                {
                    k++;
                }

                tokens.Add(new Token(TokenKind.Number, sql[i..k], line, column));
                i = k;
                continue;
            }

            if (IsNameChar(c) && c != '$')
            {
                int k = i + 1;
                while (k < sql.Length && IsNameChar(sql[k]))
                {
                    k++;
                }

                tokens.Add(new Token(TokenKind.Word, sql[i..k].ToUpperInvariant(), line, column));
                i = k;
                continue;
            }

            tokens.Add(new Token(TokenKind.Symbol, c.ToString(), line, column));
            i++;
        }

        return tokens;
    }

    /// <summary>SQLite's <c>IdChar</c>: a letter, a digit, <c>_</c>, <c>$</c> (inside a name, never leading one) or past ASCII.</summary>
    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' || c > 127;

    /// <summary>
    /// How long the placeholder at <paramref name="start"/> (its <c>@</c>, <c>:</c>, <c>$</c> or <c>#</c>) is; 0 when SQLite would not
    /// read one there. tokenize.c's <c>CC_VARALPHA</c> case over again (the third 2026-10-04 review: the gate read <c>#a</c> as no
    /// placeholder and stopped <c>@a$b</c> at the <c>$</c>, so <see cref="Binds"/> missed or misnamed them and Microsoft.Data.Sqlite
    /// threw past the <c>SqliteException</c> catches): name characters, <c>$</c> among them; a <c>::</c> anywhere; after at least one
    /// name character a <c>(…)</c> with no blank in it ends it (unclosed, SQLite's illegal token: 0 here too).
    /// </summary>
    private static int VariableLength(string sql, int start)
    {
        int i = start + 1, n = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (IsNameChar(c))
            {
                n++;
                i++;
            }
            else if (c == '(' && n > 0)
            {
                int k = i + 1;
                while (k < sql.Length && !char.IsWhiteSpace(sql[k]) && sql[k] != ')')
                {
                    k++;
                }

                return k < sql.Length && sql[k] == ')' ? k + 1 - start : 0;
            }
            else if (c == ':' && i + 1 < sql.Length && sql[i + 1] == ':')
            {
                i += 2;
            }
            else
            {
                break;
            }
        }

        return n > 0 ? i - start : 0;
    }
}
