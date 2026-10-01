using System.Text;

namespace NeonSidekick.Oracle;

/// <summary>
/// Whether <c>oracle_query</c> may run a text (2026-09-30), the <see cref="Sql.SqlReadOnlyGate"/>'s part for Oracle: the
/// first of four layers (the session's <c>READ_ONLY</c> on 23ai, a <c>SET TRANSACTION READ ONLY</c> always rolled back,
/// and a read-only account — <see cref="OracleAccess"/>, the README). No managed Oracle parser exists, so the text is
/// <b>lexed</b>, never matched by pattern: comments (a <c>/*+ hint */</c> is one), <c>'…'</c>, <c>n'…'</c> and
/// <c>q'[…]'</c> literals, <c>"quoted"</c> names and <c>:binds</c> are tokens, so a keyword inside any of them counts for
/// nothing and a keyword outside them cannot hide. Allowed: one statement (a trailing <c>;</c> or SQL*Plus <c>/</c>
/// dropped) that starts <c>SELECT</c> or <c>WITH</c>, parentheses before either allowed. Refused wherever it stands:
/// <c>WITH FUNCTION</c>/<c>WITH PROCEDURE</c> (12c's PL/SQL inside a query, where an autonomous transaction commits),
/// <c>FOR UPDATE</c>, <c>INTO</c>, <c>NEXTVAL</c> (a sequence moves on and no rollback moves it back), a database link
/// (<c>@</c>: another server), an inline <c>EXTERNAL(…)</c> table and <c>BFILENAME</c> (files on the server's disk), the
/// DML and DDL words Oracle reserves (never a bare name, so refusing them costs no honest query), and the packages of
/// <see cref="DeniedPackages"/>. The spike found the 23ai session layer alone refusing DML, DDL, <c>FOR UPDATE</c>,
/// <c>WITH FUNCTION</c> and an autonomous-transaction function — but not <c>NEXTVAL</c>, and not on an older server:
/// this gate is what stands in front of both.
/// </summary>
public static class OracleReadOnlyGate
{
    /// <summary>The kinds of token the lexer yields.</summary>
    public enum TokenKind
    {
        /// <summary>An unquoted word, upper-cased: a keyword or a name.</summary>
        Word,

        /// <summary>A <c>"quoted"</c> name; the text is what is inside the quotes.</summary>
        Quoted,

        /// <summary>A string literal of any form; the text is its source.</summary>
        String,

        /// <summary>A numeric literal.</summary>
        Number,

        /// <summary>A <c>:name</c> or <c>:1</c> placeholder; the text is the name.</summary>
        Bind,

        /// <summary>Any other character: <c>(</c>, <c>;</c>, <c>@</c>, an operator.</summary>
        Symbol,
    }

    /// <summary>One token, where it starts (1-based line and column), and its span in the text (<c>[Start, End)</c>).</summary>
    public readonly record struct Token(TokenKind Kind, string Text, int Line, int Column, int Start, int End);

    /// <summary>
    /// The packages (and types) a query may not call: they reach the network, the server's files, other sessions or the
    /// scheduler — state no rollback undoes, or a door out — or run SQL text of their own past this gate.
    /// </summary>
    public static readonly IReadOnlySet<string> DeniedPackages = new HashSet<string>(StringComparer.Ordinal)
    {
        "UTL_HTTP", "UTL_TCP", "UTL_SMTP", "UTL_MAIL", "UTL_FILE", "UTL_INADDR", "HTTPURITYPE",
        "DBMS_LDAP", "DBMS_PIPE", "DBMS_ALERT", "DBMS_AQ", "DBMS_AQADM", "DBMS_SCHEDULER", "DBMS_JOB", "DBMS_LOCK",
        "DBMS_SQL", "DBMS_XMLGEN", "DBMS_XMLQUERY", "DBMS_XMLSTORE", "DBMS_JAVA", "DBMS_CLOUD", "DBMS_DEBUG_JDWP",
        "DBMS_NETWORK_ACL_ADMIN", "DBMS_BACKUP_RESTORE", "DBMS_SESSION", "DBMS_SYSTEM", "DBMS_REPAIR",
    };

    /// <summary>The words Oracle reserves for changing data or the schema: never an unquoted name, so a query never needs them bare.</summary>
    public static readonly IReadOnlySet<string> ChangingWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE", "GRANT", "REVOKE", "LOCK", "RENAME", "TRUNCATE", "MERGE",
    };

    /// <summary>Null when <paramref name="sql"/> may run; else the <c>Error:</c> sentence the model reads.</summary>
    public static string? Check(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return OracleText.NoSql;
        }

        if (Tokenize(sql, out var lexError) is not { } all)
        {
            return lexError;
        }

        var tokens = Statement(all);
        if (tokens.Count == 0)
        {
            return OracleText.NoSql;
        }

        // The first word before the count (a PL/SQL block is named as one, not as the statements inside it), and
        // WITH FUNCTION before it too: its body's semicolons are what makes it look like many statements.
        int lead = tokens.FindIndex(t => !IsSymbol(t, "("));
        if (lead < 0)
        {
            return OracleText.NoSql;
        }

        var first = tokens[lead];
        if (first.Kind != TokenKind.Word || first.Text is not ("SELECT" or "WITH"))
        {
            return OracleText.NotASelect(first.Kind == TokenKind.Word ? first.Text : "'" + first.Text + "'");
        }

        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i] is { Kind: TokenKind.Word, Text: "WITH" } && tokens[i + 1] is { Kind: TokenKind.Word, Text: "FUNCTION" or "PROCEDURE" } next)
            {
                return OracleText.Forbidden("WITH " + next.Text + " (PL/SQL inside the query)");
            }
        }

        int separators = tokens.Count(t => IsSymbol(t, ";"));
        if (separators > 0)
        {
            return OracleText.NotOneStatement(separators + 1);
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
            if (t.Kind == TokenKind.Symbol && t.Text == "@")
            {
                return OracleText.Forbidden("a database link (@name: another server)");
            }

            if (t.Kind != TokenKind.Word)
            {
                continue;
            }

            string word = t.Text;
            if (word == "FOR" && next.Kind == TokenKind.Word && next.Text == "UPDATE")
            {
                return OracleText.Forbidden("FOR UPDATE (it locks rows)");
            }

            if (word == "INTO")
            {
                return OracleText.SelectInto;
            }

            if (word == "NEXTVAL")
            {
                return OracleText.Forbidden("NEXTVAL (a sequence moves on, and no rollback moves it back)");
            }

            if (word == "EXTERNAL" && IsSymbol(next, "("))
            {
                return OracleText.Forbidden("EXTERNAL(…) (an inline external table: files on the server)");
            }

            if (word == "BFILENAME")
            {
                return OracleText.Forbidden("BFILENAME (files on the server)");
            }

            if (ChangingWords.Contains(word))
            {
                return OracleText.Forbidden(word);
            }

            if (DeniedPackages.Contains(word))
            {
                return OracleText.Forbidden(word);
            }
        }

        return null;
    }

    /// <summary>
    /// The placeholders <paramref name="sql"/> uses, each once, in order of first use — what <see cref="OracleAccess"/>
    /// binds (ODP.NET refuses a bound name the statement does not use). Names compare case-insensitively, as Oracle's
    /// unquoted placeholders do. Empty when the text does not lex.
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

    /// <summary>
    /// The text as ODP.NET takes it (a command with a trailing <c>;</c> fails ORA-00933): <paramref name="sql"/> cut after its
    /// last token once a trailing SQL*Plus <c>/</c> and <c>;</c> are dropped — so a comment after them goes too, and one
    /// before (a hint) stays. The text as it is when it does not lex (the gate refuses it anyway).
    /// </summary>
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

    /// <summary>The tokens with one trailing SQL*Plus <c>/</c> and then one trailing <c>;</c> dropped.</summary>
    private static List<Token> Statement(IReadOnlyList<Token> tokens)
    {
        var list = tokens.ToList();
        if (list.Count > 0 && IsSymbol(list[^1], "/"))
        {
            list.RemoveAt(list.Count - 1);
        }

        if (list.Count > 0 && IsSymbol(list[^1], ";"))
        {
            list.RemoveAt(list.Count - 1);
        }

        return list;
    }

    private static bool IsSymbol(Token token, string symbol) => token.Kind == TokenKind.Symbol && token.Text == symbol;

    /// <summary>
    /// <paramref name="sql"/> as tokens, comments dropped; null with the <c>Error:</c> sentence for a literal, a quoted
    /// name or a comment that never ends (the server would refuse it too, and a gate that guessed where it ends could be
    /// fooled into reading a keyword as text).
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

            if (c == '-' && At(sql, i + 1) == '-')
            {
                int end = sql.IndexOf('\n', i);
                Advance(end < 0 ? sql.Length : end);
                continue;
            }

            if (c == '/' && At(sql, i + 1) == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    error = OracleText.Unterminated("/* comment", startLine, startColumn);
                    return null;
                }

                Advance(end + 2);
                continue;
            }

            int prefix = LiteralPrefix(sql, i);
            if (prefix >= 0)
            {
                int quote = i + prefix;
                bool alternative = prefix > 0 && char.ToUpperInvariant(sql[quote - 1]) == 'Q';
                int end = alternative ? AlternativeEnd(sql, quote) : StandardEnd(sql, quote);
                if (end < 0)
                {
                    error = OracleText.Unterminated("string literal", startLine, startColumn);
                    return null;
                }

                tokens.Add(new Token(TokenKind.String, sql[i..end], startLine, startColumn, i, end));
                Advance(end);
                continue;
            }

            if (c == '"')
            {
                int end = sql.IndexOf('"', i + 1);
                if (end < 0)
                {
                    error = OracleText.Unterminated("quoted name", startLine, startColumn);
                    return null;
                }

                tokens.Add(new Token(TokenKind.Quoted, sql[(i + 1)..end], startLine, startColumn, i, end + 1));
                Advance(end + 1);
                continue;
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
                int end = NumberEnd(sql, i);
                tokens.Add(new Token(TokenKind.Number, sql[i..end], startLine, startColumn, i, end));
                Advance(end);
                continue;
            }

            if (c == ':' && (IsWordStart(At(sql, i + 1)) || char.IsAsciiDigit(At(sql, i + 1))))
            {
                int end = i + 2;
                while (end < sql.Length && IsWordPart(sql[end]))
                {
                    end++;
                }

                tokens.Add(new Token(TokenKind.Bind, sql[(i + 1)..end], startLine, startColumn, i, end));
                Advance(end);
                continue;
            }

            tokens.Add(new Token(TokenKind.Symbol, c.ToString(), startLine, startColumn, i, i + 1));
            Advance(i + 1);
        }

        return tokens;
    }

    private static char At(string text, int index) => index < text.Length ? text[index] : '\0';

    private static bool IsWordStart(char c) => char.IsLetter(c);

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    /// <summary>
    /// How many prefix letters stand before the opening quote of a literal at <paramref name="i"/>: 0 for <c>'…'</c>, 1 for
    /// <c>n'…'</c> and <c>q'…'</c>, 2 for <c>nq'…'</c>; -1 when no literal starts there (a word like <c>name</c> goes on as a
    /// word). Only at a word's start: <c>xq'…'</c> is the word <c>xq</c>, then a literal.
    /// </summary>
    private static int LiteralPrefix(string sql, int i)
    {
        if (sql[i] == '\'')
        {
            return 0;
        }

        if (i > 0 && IsWordPart(sql[i - 1]))
        {
            return -1;
        }

        char a = char.ToUpperInvariant(sql[i]);
        if (a is 'N' or 'Q' && At(sql, i + 1) == '\'')
        {
            return 1;
        }

        return a == 'N' && char.ToUpperInvariant(At(sql, i + 1)) == 'Q' && At(sql, i + 2) == '\'' ? 2 : -1;
    }

    /// <summary>The index past a <c>'…'</c> literal whose quote is at <paramref name="quote"/> (<c>''</c> is a quote inside), or -1.</summary>
    private static int StandardEnd(string sql, int quote)
    {
        int j = quote + 1;
        while (j < sql.Length)
        {
            if (sql[j] == '\'')
            {
                if (At(sql, j + 1) == '\'')
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

    /// <summary>The index past a <c>q'X…X'</c> literal whose quote is at <paramref name="quote"/> (<c>[ { ( &lt;</c> close with their pair), or -1.</summary>
    private static int AlternativeEnd(string sql, int quote)
    {
        char open = At(sql, quote + 1);
        if (open == '\0' || char.IsWhiteSpace(open))
        {
            return -1;
        }

        char close = open switch
        {
            '[' => ']',
            '{' => '}',
            '(' => ')',
            '<' => '>',
            _ => open,
        };

        for (int j = quote + 2; j + 1 < sql.Length; j++)
        {
            if (sql[j] == close && sql[j + 1] == '\'')
            {
                return j + 2;
            }
        }

        return -1;
    }

    /// <summary>The index past a number at <paramref name="i"/>: digits, a point, digits, an exponent, an <c>f</c>/<c>d</c> suffix.</summary>
    private static int NumberEnd(string sql, int i)
    {
        int j = i;
        while (j < sql.Length && char.IsAsciiDigit(sql[j]))
        {
            j++;
        }

        if (At(sql, j) == '.' && At(sql, j + 1) != '.')
        {
            j++;
            while (j < sql.Length && char.IsAsciiDigit(sql[j]))
            {
                j++;
            }
        }

        if (char.ToUpperInvariant(At(sql, j)) == 'E' && (char.IsAsciiDigit(At(sql, j + 1)) || (At(sql, j + 1) is '+' or '-' && char.IsAsciiDigit(At(sql, j + 2)))))
        {
            j += 2;
            while (j < sql.Length && char.IsAsciiDigit(sql[j]))
            {
                j++;
            }
        }

        if (char.ToUpperInvariant(At(sql, j)) is 'F' or 'D' && !IsWordPart(At(sql, j + 1)))
        {
            j++;
        }

        return j;
    }

    /// <summary>The text of the tokens, one space apart — what a test or a log line shows of the lexer's view.</summary>
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
                TokenKind.Quoted => "\"" + t.Text + "\"",
                TokenKind.Bind => ":" + t.Text,
                _ => t.Text,
            });
        }

        return sb.ToString();
    }
}
