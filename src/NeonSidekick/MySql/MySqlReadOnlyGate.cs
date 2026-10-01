using System.Text;

namespace NeonSidekick.MySql;

/// <summary>
/// Whether <c>mysql_query</c> may run a text (2026-09-30), <see cref="Oracle.OracleReadOnlyGate"/>'s part for MySQL and MariaDB:
/// the first layer, ahead of the session's own (<see cref="MySqlAccess"/>: the risky <c>sql_mode</c> flags stripped, a statement
/// time cap, a <c>START TRANSACTION READ ONLY</c> always rolled back) and a SELECT-only account. A <b>lexer</b> for MySQL's rules,
/// never a pattern: <c>#</c>, <c>-- </c> (a space after, else it is minus minus) and <c>/* */</c> comments; <c>'…'</c> and <c>"…"</c>
/// strings with backslash escapes (the session runs without <c>NO_BACKSLASH_ESCAPES</c> and <c>ANSI_QUOTES</c>, so the server reads
/// them as this does); <c>`quoted`</c> names; <c>@name</c> placeholders and <c>@@system</c> variables. An executable comment
/// (<c>/*! … */</c>, MariaDB's <c>/*M! … */</c>) is refused outright: the server runs what it holds. Allowed: one statement (a
/// trailing <c>;</c> dropped) that starts <c>SELECT</c> or <c>WITH</c>. Refused wherever it stands: <c>INTO</c> (<c>OUTFILE</c>,
/// <c>DUMPFILE</c>: a file on the server), <c>FOR UPDATE</c>/<c>FOR SHARE</c>/<c>LOCK IN SHARE MODE</c>, <c>LOAD_FILE</c>, the named
/// locks, MariaDB's sequence moves (<c>NEXTVAL</c>, <c>SETVAL</c>, <c>NEXT VALUE FOR</c>), the data- and schema-changing words
/// (<c>INSERT</c> and <c>REPLACE</c> only as statements: as functions they edit a string), and <see cref="DeniedFunctions"/>. The
/// spike found the read-only transaction alone refusing every write on both servers — but the driver ran <c>SELECT 1; SELECT 2</c>
/// as two statements, so the one-statement rule here is what keeps a DDL (which commits first) from riding behind a SELECT.
/// </summary>
public static class MySqlReadOnlyGate
{
    /// <summary>The kinds of token the lexer yields.</summary>
    public enum TokenKind
    {
        /// <summary>An unquoted word, upper-cased: a keyword or a name; <c>@@system</c> variables too.</summary>
        Word,

        /// <summary>A <c>`quoted`</c> name; the text is what is inside the backticks.</summary>
        Quoted,

        /// <summary>A string literal (<c>'…'</c> or <c>"…"</c>); the text is its source.</summary>
        String,

        /// <summary>A numeric literal.</summary>
        Number,

        /// <summary>An <c>@name</c> placeholder; the text is the name.</summary>
        Bind,

        /// <summary>Any other character: <c>(</c>, <c>;</c>, <c>?</c>, an operator.</summary>
        Symbol,
    }

    /// <summary>One token, where it starts (1-based line and column), and its span in the text (<c>[Start, End)</c>).</summary>
    public readonly record struct Token(TokenKind Kind, string Text, int Line, int Column, int Start, int End);

    /// <summary>Functions a query may not call: a file on the server's disk, a lock no rollback releases, a door out of the database.</summary>
    public static readonly IReadOnlySet<string> DeniedFunctions = new HashSet<string>(StringComparer.Ordinal)
    {
        "LOAD_FILE", "GET_LOCK", "RELEASE_LOCK", "RELEASE_ALL_LOCKS", "NEXTVAL", "SETVAL", "SYS_EXEC", "SYS_EVAL", "SYS_GET", "SYS_SET",
        "MASTER_POS_WAIT", "SOURCE_POS_WAIT", "WAIT_FOR_EXECUTED_GTID_SET",
    };

    /// <summary>The words that change data or the schema, and the statements that are not a query; refused wherever they stand.</summary>
    public static readonly IReadOnlySet<string> ChangingWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "UPDATE", "DELETE", "DROP", "ALTER", "CREATE", "GRANT", "REVOKE", "LOCK", "UNLOCK", "RENAME", "HANDLER", "CALL",
        "LOAD", "FLUSH", "KILL", "SHUTDOWN", "INSTALL", "UNINSTALL", "PREPARE", "EXECUTE", "DEALLOCATE",
    };

    /// <summary>The changing words that are also functions when a parenthesis follows (<c>INSERT(s, 1, 2, 'x')</c>, <c>TRUNCATE(1.25, 1)</c>).</summary>
    public static readonly IReadOnlySet<string> FunctionOrStatement = new HashSet<string>(StringComparer.Ordinal) { "INSERT", "REPLACE", "TRUNCATE" };

    /// <summary>Null when <paramref name="sql"/> may run; else the <c>Error:</c> sentence the model reads.</summary>
    public static string? Check(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return MySqlText.NoSql;
        }

        if (Tokenize(sql, out var lexError) is not { } all)
        {
            return lexError;
        }

        var tokens = Statement(all);
        int lead = tokens.FindIndex(t => !IsSymbol(t, "("));
        if (lead < 0)
        {
            return MySqlText.NoSql;
        }

        var first = tokens[lead];
        if (first.Kind != TokenKind.Word || first.Text is not ("SELECT" or "WITH"))
        {
            return MySqlText.NotASelect(first.Kind == TokenKind.Word ? first.Text : "'" + first.Text + "'");
        }

        int separators = tokens.Count(t => IsSymbol(t, ";"));
        if (separators > 0)
        {
            return MySqlText.NotOneStatement(separators + 1);
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Word)
            {
                continue;
            }

            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
            var after = i + 2 < tokens.Count ? tokens[i + 2] : default;
            string word = t.Text;
            if (word == "INTO")
            {
                return MySqlText.SelectInto;
            }

            if (word == "FOR" && next.Kind == TokenKind.Word && next.Text is "UPDATE" or "SHARE")
            {
                return MySqlText.Forbidden("FOR " + next.Text + " (it locks rows)");
            }

            if (word == "NEXT" && next.Kind == TokenKind.Word && next.Text == "VALUE" && after.Kind == TokenKind.Word && after.Text == "FOR")
            {
                return MySqlText.Forbidden("NEXT VALUE FOR (a sequence moves on, and no rollback moves it back)");
            }

            if (FunctionOrStatement.Contains(word))
            {
                if (!IsSymbol(next, "("))
                {
                    return MySqlText.Forbidden(word);
                }

                continue;
            }

            if (ChangingWords.Contains(word) || DeniedFunctions.Contains(word))
            {
                return MySqlText.Forbidden(word);
            }
        }

        return null;
    }

    /// <summary>
    /// The placeholders <paramref name="sql"/> uses, each once, in order of first use (<c>@@system</c> variables are not among
    /// them). Names compare case-insensitively, as MySqlConnector binds them. Empty when the text does not lex.
    /// </summary>
    public static IReadOnlyList<string> Binds(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (Tokenize(sql, out _) is not { } tokens)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return tokens.Where(t => t.Kind == TokenKind.Bind && seen.Add(t.Text)).Select(t => t.Text).ToList();
    }

    /// <summary>The text cut after its last token once a trailing <c>;</c> is dropped (a comment after it goes too); as it is when it does not lex.</summary>
    public static string Body(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (Tokenize(sql, out _) is not { } tokens)
        {
            return sql;
        }

        var kept = Statement(tokens);
        return kept.Count == 0 ? "" : sql[..kept[^1].End];
    }

    private static List<Token> Statement(IReadOnlyList<Token> tokens)
    {
        var list = tokens.ToList();
        if (list.Count > 0 && IsSymbol(list[^1], ";"))
        {
            list.RemoveAt(list.Count - 1);
        }

        return list;
    }

    private static bool IsSymbol(Token token, string symbol) => token.Kind == TokenKind.Symbol && token.Text == symbol;

    /// <summary>
    /// <paramref name="sql"/> as tokens, comments dropped; null with the <c>Error:</c> sentence for an executable comment, or a
    /// literal, a quoted name or a comment that never ends (a gate that guessed where it ends could be fooled).
    /// </summary>
    public static IReadOnlyList<Token>? Tokenize(string sql, out string? error)
    {
        ArgumentNullException.ThrowIfNull(sql);
        error = null;
        var tokens = new List<Token>();
        int i = 0;
        int line = 1;
        int lineStart = 0;

        void Advance(int to)
        {
            for (; i < to && i < sql.Length; i++)
            {
                if (sql[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }
        }

        while (i < sql.Length)
        {
            char c = sql[i];
            int startLine = line;
            int startColumn = i - lineStart + 1;
            if (char.IsWhiteSpace(c))
            {
                Advance(i + 1);
                continue;
            }

            // '#', and '--' followed by a space or a control character (or the end): MySQL's line comments.
            if (c == '#' || (c == '-' && At(sql, i + 1) == '-' && (i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2]))))
            {
                int end = sql.IndexOf('\n', i);
                Advance(end < 0 ? sql.Length : end);
                continue;
            }

            if (c == '/' && At(sql, i + 1) == '*')
            {
                if (At(sql, i + 2) == '!' || (At(sql, i + 2) is 'M' or 'm' && At(sql, i + 3) == '!'))
                {
                    error = MySqlText.ExecutableComment(startLine, startColumn);
                    return null;
                }

                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    error = MySqlText.Unterminated("/* comment", startLine, startColumn);
                    return null;
                }

                Advance(end + 2);
                continue;
            }

            if (c is '\'' or '"')
            {
                int end = QuotedEnd(sql, i, c, backslash: true);
                if (end < 0)
                {
                    error = MySqlText.Unterminated("string literal", startLine, startColumn);
                    return null;
                }

                tokens.Add(new Token(TokenKind.String, sql[i..end], startLine, startColumn, i, end));
                Advance(end);
                continue;
            }

            if (c == '`')
            {
                int end = QuotedEnd(sql, i, '`', backslash: false);
                if (end < 0)
                {
                    error = MySqlText.Unterminated("quoted name", startLine, startColumn);
                    return null;
                }

                tokens.Add(new Token(TokenKind.Quoted, sql[(i + 1)..(end - 1)].Replace("``", "`", StringComparison.Ordinal), startLine, startColumn, i, end));
                Advance(end);
                continue;
            }

            if (c == '@')
            {
                bool system = At(sql, i + 1) == '@';
                int from = system ? i + 2 : i + 1;
                int end = from;
                while (end < sql.Length && (IsWordPart(sql[end]) || sql[end] == '.'))
                {
                    end++;
                }

                if (end > from)
                {
                    tokens.Add(system
                        ? new Token(TokenKind.Word, sql[i..end].ToUpperInvariant(), startLine, startColumn, i, end)
                        : new Token(TokenKind.Bind, sql[from..end], startLine, startColumn, i, end));
                    Advance(end);
                    continue;
                }
            }

            if (IsWordStart(c))
            {
                int end = i + 1;
                while (end < sql.Length && IsWordPart(sql[end]))
                {
                    end++;
                }

                tokens.Add(new Token(TokenKind.Word, sql[i..end].ToUpperInvariant(), startLine, startColumn, i, end));
                Advance(end);
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(At(sql, i + 1))))
            {
                int end = i;
                while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '.' || (sql[end] is '+' or '-' && sql[end - 1] is 'e' or 'E')))
                {
                    end++;
                }

                tokens.Add(new Token(TokenKind.Number, sql[i..end], startLine, startColumn, i, end));
                Advance(end);
                continue;
            }

            tokens.Add(new Token(TokenKind.Symbol, c.ToString(), startLine, startColumn, i, i + 1));
            Advance(i + 1);
        }

        return tokens;
    }

    private static char At(string text, int index) => index < text.Length ? text[index] : '\0';

    private static bool IsWordStart(char c) => char.IsLetter(c) || c is '_' or '$';

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>The index past a literal or quoted name opened at <paramref name="open"/> with <paramref name="quote"/>: a doubled quote is one inside it, and with <paramref name="backslash"/> a backslash escapes the next character. -1 when it never ends.</summary>
    private static int QuotedEnd(string sql, int open, char quote, bool backslash)
    {
        int j = open + 1;
        while (j < sql.Length)
        {
            char ch = sql[j];
            if (backslash && ch == '\\')
            {
                j += 2;
                continue;
            }

            if (ch == quote)
            {
                if (At(sql, j + 1) == quote)
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            j++;
        }

        return -1;
    }

    /// <summary>The text of the tokens, one space apart — what a test shows of the lexer's view.</summary>
    public static string Show(IEnumerable<Token> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var sb = new StringBuilder();
        foreach (var t in tokens)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(t.Kind switch
            {
                TokenKind.Quoted => "`" + t.Text + "`",
                TokenKind.Bind => "@" + t.Text,
                _ => t.Text,
            });
        }

        return sb.ToString();
    }
}
