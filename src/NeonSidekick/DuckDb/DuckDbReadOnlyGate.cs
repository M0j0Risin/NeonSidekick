using System.Text;

namespace NeonSidekick.DuckDb;

/// <summary>How a file reference the gate found must be met (<see cref="DuckDbFileReference"/>).</summary>
public enum DuckDbFileNeed
{
    /// <summary>A string literal (in table position or a reader's argument): it must be a file in the working directory.</summary>
    Required,

    /// <summary>An unqualified quoted name in table position that looks like a file: a file in the working directory, else refused (<see cref="DuckDbText.TableLooksLikeFile"/>).</summary>
    FileLike,

    /// <summary>A qualified name in table position whose parts read as a file name (<c>sales.csv</c>): rewritten when it is a file there, else left as a table.</summary>
    IfFile,
}

/// <summary>
/// One place in a statement that DuckDB would read as a file (2026-10-25): its span in the text (rewritten by <see cref="DuckDbFilePaths"/>),
/// the path as written, how it must be met, whether a glob may stand there (a reader's argument alone), and whether it sits inside a list
/// literal (a glob's expansion is spliced in without brackets there).
/// </summary>
public sealed record DuckDbFileReference(int Start, int End, string Path, DuckDbFileNeed Need, bool InReader, bool InList);

/// <summary>
/// Whether <c>duckdb_query</c> may run a text (2026-10-25), <c>SqliteReadOnlyGate</c>'s part for DuckDB: the first layer, ahead of the
/// file opened read-only, the locked configuration (outside access off, the working directory the only folder) and a transaction always
/// rolled back (<see cref="DuckDbAccess"/>). A <b>lexer</b> for DuckDB's rules (the spike's conformance table): <c>--</c> comments and
/// <b>nested</b> <c>/* */</c> ones; <c>//</c> is division and <c>#</c> a positional column, never comments; <c>'…'</c> strings with
/// <c>''</c>, <c>E'…'</c> ones with backslash escapes; dollar quoting (<c>$$…$$</c>, <c>$tag$…$tag$</c>); <c>"…"</c> names; <c>$name</c>
/// placeholders. Positional <c>$1</c> and <c>?</c> are refused (<c>params</c> binds names). Allowed: one statement (a trailing <c>;</c>
/// dropped) that starts SELECT, WITH, VALUES, FROM, TABLE, DESCRIBE, SHOW, SUMMARIZE, PIVOT, UNPIVOT, or EXPLAIN [ANALYZE] in front of
/// one of those. Refused wherever they stand: <see cref="ChangingWords"/>, a denied table function (<see cref="DuckDbFunctions"/>), a
/// denied scalar, and a reader whose first argument is not a string literal or a list of them. Every file reference — a string literal
/// in table position or a reader's argument, a quoted name in table position that looks like a file — is handed out
/// (<see cref="Inspect"/>) for <see cref="DuckDbFilePaths"/> to hold to the working directory.
/// </summary>
public static class DuckDbReadOnlyGate
{
    /// <summary>The kinds of token the lexer yields.</summary>
    public enum TokenKind
    {
        /// <summary>An unquoted word, upper-cased (A to Z alone): a keyword or a name. The source spelling is <c>sql[Start..End]</c>.</summary>
        Word,

        /// <summary>A quoted name (<c>"…"</c>); the text is what is inside.</summary>
        Quoted,

        /// <summary>A string literal; the text is its source (<c>'it''s'</c>, <c>E'…'</c>, <c>$$…$$</c>).</summary>
        String,

        /// <summary>A numeric literal.</summary>
        Number,

        /// <summary>A named placeholder; the text is its name without the <c>$</c>.</summary>
        Bind,

        /// <summary>Any other character: <c>(</c>, <c>;</c>, <c>[</c>, an operator.</summary>
        Symbol,
    }

    /// <summary>One token, where it starts (1-based line and column) and its span in the text.</summary>
    public readonly record struct Token(TokenKind Kind, string Text, int Line, int Column, int Start, int End);

    /// <summary>The words that change data or the schema, reach another file or setting, or are not a query; refused wherever they stand.</summary>
    public static readonly IReadOnlySet<string> ChangingWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "UPSERT", "RETURNING", "CREATE", "DROP", "ALTER", "COPY", "EXPORT", "IMPORT",
        "ATTACH", "DETACH", "USE", "INSTALL", "LOAD", "SET", "RESET", "CALL", "PREPARE", "EXECUTE", "DEALLOCATE", "BEGIN", "COMMIT",
        "ROLLBACK", "ABORT", "TRANSACTION", "CHECKPOINT", "VACUUM", "ANALYZE", "PRAGMA",
    };

    /// <summary>The words a read may start with.</summary>
    public static readonly IReadOnlySet<string> ReadLeads = new HashSet<string>(StringComparer.Ordinal)
    {
        "SELECT", "WITH", "VALUES", "FROM", "TABLE", "DESCRIBE", "SHOW", "SUMMARIZE", "PIVOT", "UNPIVOT", "PIVOT_WIDER", "PIVOT_LONGER",
    };

    /// <summary>
    /// The words that end a FROM clause at its depth (a comma after one is no longer a join). Not ON or USING: a join's condition keeps
    /// its FROM open, so <c>JOIN b USING (id), "d.json"</c> still hands out the file after the comma (its commas sit a parenthesis deeper).
    /// </summary>
    private static readonly IReadOnlySet<string> FromEnds = new HashSet<string>(StringComparer.Ordinal)
    {
        "WHERE", "GROUP", "HAVING", "ORDER", "LIMIT", "OFFSET", "QUALIFY", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "SELECT", "SAMPLE", "TABLESAMPLE", "RETURNING", "SET", "VALUES", "AS", "DO", "WHEN", "THEN",
    };

    /// <summary>The words after which the next name is a table read: a FROM, a JOIN, and a statement that names a table to describe.</summary>
    private static readonly IReadOnlySet<string> TableLeads = new HashSet<string>(StringComparer.Ordinal)
    {
        "FROM", "JOIN", "DESCRIBE", "SHOW", "SUMMARIZE", "PIVOT", "UNPIVOT", "PIVOT_WIDER", "PIVOT_LONGER",
    };

    /// <summary>The extensions that make a quoted name read as a data file.</summary>
    public static readonly IReadOnlyList<string> DataExtensions = [".csv", ".tsv", ".txt", ".parquet", ".json", ".jsonl", ".ndjson"];

    /// <summary>Null when <paramref name="sql"/> may run; else the <c>Error:</c> sentence the model reads.</summary>
    public static string? Check(string sql) => Inspect(sql, out _);

    /// <summary><see cref="Check"/>, and the file references the text holds (empty when refused).</summary>
    public static string? Inspect(string sql, out IReadOnlyList<DuckDbFileReference> files)
    {
        ArgumentNullException.ThrowIfNull(sql);
        files = [];
        if (string.IsNullOrWhiteSpace(sql))
        {
            return DuckDbText.NoSql;
        }

        if (Tokenize(sql, out var lexError) is not { } all)
        {
            return lexError;
        }

        var tokens = Statement(all);
        int lead = Lead(tokens);
        if (lead < 0)
        {
            return DuckDbText.NoSql;
        }

        int separators = tokens.Count(t => IsSymbol(t, ";"));
        if (separators > 0)
        {
            return DuckDbText.NotOneStatement(separators + 1);
        }

        // EXPLAIN [ANALYZE] in front of a read is a read (ANALYZE runs it); the rest is checked as the statement.
        int body = lead;
        if (IsWord(tokens, body, "EXPLAIN"))
        {
            body++;
            if (IsWord(tokens, body, "ANALYZE") || IsWord(tokens, body, "ANALYSE"))
            {
                body++;
            }

            body = Lead(tokens, body);
            if (body < 0)
            {
                return DuckDbText.NoSql;
            }
        }

        var first = tokens[body];
        if (first.Kind != TokenKind.Word || !ReadLeads.Contains(first.Text))
        {
            return DuckDbText.NotASelect(first.Kind == TokenKind.Word ? first.Text : "'" + first.Text + "'");
        }

        for (int i = body; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Word && ChangingWords.Contains(t.Text))
            {
                return DuckDbText.Forbidden(t.Text);
            }
        }

        var found = new List<DuckDbFileReference>();
        if (Scan(sql, tokens, body, found, DuckDbText.Forbidden, DuckDbFunctions.DeniedScalars) is { } refused)
        {
            return refused;
        }

        files = found;
        return null;
    }

    /// <summary>
    /// What both gates check past their own words (<see cref="DuckDbWriteGate"/> reads it too), from <paramref name="from"/>: every call of a
    /// denied table function or scalar refused through <paramref name="forbidden"/>, every reader's first argument a string literal or a list
    /// of them, and every file reference added to <paramref name="files"/>. Null when nothing is refused.
    /// </summary>
    internal static string? Scan(string sql, List<Token> tokens, int from, List<DuckDbFileReference> files, Func<string, string> forbidden, IReadOnlySet<string> deniedScalars)
    {
        // One frame per parenthesis depth: whether a FROM clause is open at it, so a comma there joins another table.
        var inFrom = new Stack<bool>();
        inFrom.Push(false);
        bool expectTable = false;
        for (int i = from; i < tokens.Count; i++)
        {
            var t = tokens[i];
            bool tablePosition = expectTable;
            expectTable = false;

            if (IsSymbol(t, "("))
            {
                inFrom.Push(false);

                // A statement that opens a parenthesis may start with a word that names a table (FROM (DESCRIBE 'x.csv')).
                continue;
            }

            if (IsSymbol(t, ")"))
            {
                if (inFrom.Count > 1)
                {
                    inFrom.Pop();
                }

                continue;
            }

            if (IsSymbol(t, ",") && inFrom.Peek())
            {
                expectTable = true;
                continue;
            }

            if (t.Kind == TokenKind.Word)
            {
                if (t.Text is "FROM" or "JOIN")
                {
                    inFrom.Pop();
                    inFrom.Push(true);
                }
                else if (FromEnds.Contains(t.Text) && !(t.Text == "AS" && inFrom.Peek()))
                {
                    inFrom.Pop();
                    inFrom.Push(false);
                }

                // A describing word names a table only as a statement's first word (its lead, or straight after a parenthesis).
                bool statementStart = i == from || (i > 0 && IsSymbol(tokens[i - 1], "("));
                if (TableLeads.Contains(t.Text) && (t.Text is "FROM" or "JOIN" || statementStart))
                {
                    expectTable = true;
                }
            }

            // A function call: a name (the last part of a dotted one) straight before a parenthesis.
            if (t.Kind is TokenKind.Word or TokenKind.Quoted && i + 1 < tokens.Count && IsSymbol(tokens[i + 1], "("))
            {
                string name = t.Kind == TokenKind.Word ? t.Text : AsciiUpper(t.Text);
                if (deniedScalars.Contains(name) || DuckDbFunctions.Of(name) == DuckDbFunctionClass.Denied)
                {
                    return forbidden(name.ToLowerInvariant() + "()");
                }

                if (DuckDbFunctions.Of(name) == DuckDbFunctionClass.Reader && ReaderArgument(sql, tokens, i + 1, files) is { } argument)
                {
                    return argument.Length == 0 ? DuckDbText.ReaderNeedsLiteral(name.ToLowerInvariant()) : argument;
                }

                continue;
            }

            // A denied table function named with no parentheses in table position (a view or a bare name DuckDB may resolve).
            if (tablePosition && t.Kind is TokenKind.Word or TokenKind.Quoted
                && DuckDbFunctions.Of(t.Kind == TokenKind.Word ? t.Text : AsciiUpper(t.Text)) == DuckDbFunctionClass.Denied)
            {
                return forbidden((t.Kind == TokenKind.Word ? t.Text : AsciiUpper(t.Text)).ToLowerInvariant());
            }

            if (!tablePosition)
            {
                continue;
            }

            if (t.Kind == TokenKind.String)
            {
                files.Add(new DuckDbFileReference(t.Start, t.End, StringValue(t.Text), DuckDbFileNeed.Required, InReader: false, InList: false));
                continue;
            }

            if (t.Kind is TokenKind.Word or TokenKind.Quoted)
            {
                // The name's dotted chain: a quoted name alone that looks like a file is DuckDB's replacement scan, and so is a qualified one
                // whose parts read as a file name (the spike: main."sales.csv" read the relative file main.sales.csv).
                int end = i;
                var parts = new List<string> { t.Kind == TokenKind.Word ? sql[t.Start..t.End] : t.Text };
                while (end + 2 < tokens.Count && IsSymbol(tokens[end + 1], ".") && tokens[end + 2].Kind is TokenKind.Word or TokenKind.Quoted)
                {
                    end += 2;
                    parts.Add(tokens[end].Kind == TokenKind.Word ? sql[tokens[end].Start..tokens[end].End] : tokens[end].Text);
                }

                if (end + 1 < tokens.Count && IsSymbol(tokens[end + 1], "("))
                {
                    continue;   // a qualified function call: met at its name above
                }

                if (parts.Count == 1 && t.Kind == TokenKind.Quoted && LooksLikeFile(t.Text))
                {
                    files.Add(new DuckDbFileReference(t.Start, t.End, t.Text, DuckDbFileNeed.FileLike, InReader: false, InList: false));
                }
                else if (parts.Count > 1 && string.Join('.', parts) is { } joined && HasDataExtension(joined))
                {
                    files.Add(new DuckDbFileReference(t.Start, tokens[end].End, joined, DuckDbFileNeed.IfFile, InReader: false, InList: false));
                }

                i = end;
            }
        }

        return null;
    }

    /// <summary>
    /// A reader's first argument, from its <c>(</c> at <paramref name="open"/>: a string literal or a list of them, each added to
    /// <paramref name="files"/>. Null when it is one; "" when it is anything else (the caller words it).
    /// </summary>
    private static string? ReaderArgument(string sql, List<Token> tokens, int open, List<DuckDbFileReference> files)
    {
        int i = open + 1;
        if (i < tokens.Count && tokens[i].Kind == TokenKind.String)
        {
            if (!(i + 1 < tokens.Count && (IsSymbol(tokens[i + 1], ",") || IsSymbol(tokens[i + 1], ")"))))
            {
                return "";
            }

            files.Add(new DuckDbFileReference(tokens[i].Start, tokens[i].End, StringValue(tokens[i].Text), DuckDbFileNeed.Required, InReader: true, InList: false));
            return null;
        }

        if (i < tokens.Count && IsSymbol(tokens[i], "["))
        {
            var items = new List<DuckDbFileReference>();
            i++;
            while (i < tokens.Count && tokens[i].Kind == TokenKind.String)
            {
                items.Add(new DuckDbFileReference(tokens[i].Start, tokens[i].End, StringValue(tokens[i].Text), DuckDbFileNeed.Required, InReader: true, InList: true));
                i++;
                if (i < tokens.Count && IsSymbol(tokens[i], ","))
                {
                    i++;
                    continue;
                }

                break;
            }

            if (items.Count == 0 || !(i < tokens.Count && IsSymbol(tokens[i], "]")) || !(i + 1 < tokens.Count && (IsSymbol(tokens[i + 1], ",") || IsSymbol(tokens[i + 1], ")"))))
            {
                return "";
            }

            files.AddRange(items);
            return null;
        }

        return "";
    }

    /// <summary>Whether a quoted name reads as a file: a path separator in it, or a data extension (a <c>.gz</c> or <c>.zst</c> after one too).</summary>
    public static bool LooksLikeFile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.IndexOfAny(['/', '\\']) >= 0 || HasDataExtension(name);
    }

    /// <summary>Whether <paramref name="name"/> ends with a data extension, a <c>.gz</c> or <c>.zst</c> after one counted.</summary>
    public static bool HasDataExtension(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string text = name.Trim();
        foreach (string compressed in new[] { ".gz", ".zst" })
        {
            if (text.EndsWith(compressed, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^compressed.Length];
                break;
            }
        }

        return DataExtensions.Any(e => text.EndsWith(e, StringComparison.OrdinalIgnoreCase) && text.Length > e.Length);
    }

    /// <summary>The <c>$name</c> placeholders the text uses, each once, without the <c>$</c>; empty when the text does not lex. DuckDB's names are case-blind.</summary>
    public static IReadOnlyList<string> Binds(string sql) =>
        Tokenize(sql ?? "", out _) is { } tokens ? tokens.Where(t => t.Kind == TokenKind.Bind).Select(t => t.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];

    /// <summary>
    /// A <c>params</c> name as <see cref="DuckDbAccess"/> looks it up: <c>id</c> or <c>$id</c> both name <c>id</c> (one leading <c>$</c>
    /// dropped); null when it is not a name DuckDB reads after a <c>$</c> (a letter or <c>_</c>, then letters, digits and <c>_</c>).
    /// </summary>
    public static string? ParamName(string written)
    {
        ArgumentNullException.ThrowIfNull(written);
        string name = written.StartsWith('$') ? written[1..] : written;
        return name.Length > 0 && IsNameStart(name[0]) && name.All(c => IsNameChar(c) && c != '$') ? name : null;
    }

    /// <summary>The text without a trailing <c>;</c>: what runs.</summary>
    public static string Body(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        string trimmed = sql.TrimEnd();
        return trimmed.EndsWith(';') ? trimmed[..^1].TrimEnd() : trimmed;
    }

    /// <summary>Whether a read's statement is an EXPLAIN (its columns are text already, and it cannot be wrapped in a subquery).</summary>
    public static bool IsExplain(string sql) =>
        Tokenize(sql ?? "", out _) is { } tokens && Lead(tokens) is int lead and >= 0 && IsWord(tokens, lead, "EXPLAIN");

    /// <summary>Whether the statement has a RETURNING clause (its rows are the answer and the count of a change).</summary>
    public static bool Returns(string sql) =>
        Tokenize(sql ?? "", out _) is { } tokens && tokens.Any(t => t.Kind == TokenKind.Word && t.Text == "RETURNING");

    /// <summary>A string literal's value: <c>''</c> undoubled, an <c>E'…'</c> one's backslash escapes read, a dollar-quoted one's inside.</summary>
    public static string StringValue(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.StartsWith('$'))
        {
            int tag = source.IndexOf('$', 1);
            return tag < 0 || source.Length < 2 * (tag + 1) ? "" : source[(tag + 1)..^(tag + 1)];
        }

        bool escapes = source.Length > 0 && source[0] is 'E' or 'e';
        string inner = source.Length >= (escapes ? 3 : 2) ? source[(escapes ? 2 : 1)..^1] : "";
        if (!escapes)
        {
            return inner.Replace("''", "'", StringComparison.Ordinal);
        }

        var sb = new StringBuilder(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '\\' && i + 1 < inner.Length)
            {
                char n = inner[++i];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', _ => n });
            }
            else if (c == '\'' && i + 1 < inner.Length && inner[i + 1] == '\'')
            {
                sb.Append('\'');
                i++;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>The tokens up to a last <c>;</c> (dropped); everything when there is none.</summary>
    internal static List<Token> Statement(List<Token> tokens) =>
        tokens.Count > 0 && IsSymbol(tokens[^1], ";") ? tokens[..^1] : tokens;

    /// <summary>The first token from <paramref name="from"/> that is not an opening parenthesis; -1 when there is none.</summary>
    internal static int Lead(List<Token> tokens, int from = 0)
    {
        for (int i = from; i < tokens.Count; i++)
        {
            if (!IsSymbol(tokens[i], "("))
            {
                return i;
            }
        }

        return -1;
    }

    internal static bool IsSymbol(Token token, string symbol) => token.Kind == TokenKind.Symbol && token.Text == symbol;

    internal static bool IsWord(List<Token> tokens, int index, string word) =>
        index >= 0 && index < tokens.Count && tokens[index].Kind == TokenKind.Word && tokens[index].Text == word;

    /// <summary>A word upper-cased A to Z alone (the Postgres gate's rule: DuckDB folds names by ASCII).</summary>
    internal static string AsciiUpper(string text) => Postgres.PostgresReadOnlyGate.AsciiUpper(text);

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';

    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_' || c > 127;

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' || c > 127;

    /// <summary>The text as tokens, comments dropped; null with the sentence for something that never ends or a positional placeholder.</summary>
    public static List<Token>? Tokenize(string sql, out string? error)
    {
        ArgumentNullException.ThrowIfNull(sql);
        error = null;
        var tokens = new List<Token>();
        int line = 1, lineStart = 0, i = 0;

        void Advance(int to)
        {
            for (int k = i; k < to && k < sql.Length; k++)
            {
                if (sql[k] == '\n')
                {
                    line++;
                    lineStart = k + 1;
                }
            }

            i = to;
        }

        while (i < sql.Length)
        {
            char c = sql[i];
            int column = i - lineStart + 1;
            if (IsSpace(c))
            {
                Advance(i + 1);
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int end = sql.IndexOfAny(['\n', '\r'], i);
                Advance(end < 0 ? sql.Length : end);
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int depth = 0, k = i;
                while (k < sql.Length)
                {
                    if (k + 1 < sql.Length && sql[k] == '/' && sql[k + 1] == '*')
                    {
                        depth++;
                        k += 2;
                    }
                    else if (k + 1 < sql.Length && sql[k] == '*' && sql[k + 1] == '/')
                    {
                        depth--;
                        k += 2;
                        if (depth == 0)
                        {
                            break;
                        }
                    }
                    else
                    {
                        k++;
                    }
                }

                if (depth != 0)
                {
                    error = DuckDbText.Unterminated("comment", line, column);
                    return null;
                }

                Advance(k);
                continue;
            }

            bool escapes = (c is 'E' or 'e') && i + 1 < sql.Length && sql[i + 1] == '\'' && (i == 0 || !IsNameChar(sql[i - 1]));
            if (c == '\'' || escapes)
            {
                int k = i + (escapes ? 2 : 1);
                bool closed = false;
                while (k < sql.Length)
                {
                    if (escapes && sql[k] == '\\')
                    {
                        k += 2;
                        continue;
                    }

                    if (sql[k] == '\'')
                    {
                        if (k + 1 < sql.Length && sql[k + 1] == '\'')
                        {
                            k += 2;
                            continue;
                        }

                        closed = true;
                        break;
                    }

                    k++;
                }

                if (!closed)
                {
                    error = DuckDbText.Unterminated("string", line, column);
                    return null;
                }

                tokens.Add(new Token(TokenKind.String, sql[i..(k + 1)], line, column, i, k + 1));
                Advance(k + 1);
                continue;
            }

            if (c == '"')
            {
                int k = i + 1;
                var name = new StringBuilder();
                bool closed = false;
                while (k < sql.Length)
                {
                    if (sql[k] == '"')
                    {
                        if (k + 1 < sql.Length && sql[k + 1] == '"')
                        {
                            name.Append('"');
                            k += 2;
                            continue;
                        }

                        closed = true;
                        break;
                    }

                    name.Append(sql[k]);
                    k++;
                }

                if (!closed)
                {
                    error = DuckDbText.Unterminated("quoted name", line, column);
                    return null;
                }

                tokens.Add(new Token(TokenKind.Quoted, name.ToString(), line, column, i, k + 1));
                Advance(k + 1);
                continue;
            }

            if (c == '$')
            {
                if (i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1]))
                {
                    error = DuckDbText.Positional;
                    return null;
                }

                // $tag$…$tag$ (an empty tag too), else $name, a placeholder.
                int k = i + 1;
                while (k < sql.Length && sql[k] != '$' && (IsNameStart(sql[k]) || (k > i + 1 && char.IsAsciiDigit(sql[k]))))
                {
                    k++;
                }

                if (k < sql.Length && sql[k] == '$')
                {
                    string tag = sql[i..(k + 1)];
                    int end = sql.IndexOf(tag, k + 1, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        error = DuckDbText.Unterminated("dollar-quoted string", line, column);
                        return null;
                    }

                    tokens.Add(new Token(TokenKind.String, sql[i..(end + tag.Length)], line, column, i, end + tag.Length));
                    Advance(end + tag.Length);
                    continue;
                }

                if (k > i + 1)
                {
                    tokens.Add(new Token(TokenKind.Bind, sql[(i + 1)..k], line, column, i, k));
                    Advance(k);
                    continue;
                }

                tokens.Add(new Token(TokenKind.Symbol, "$", line, column, i, i + 1));
                Advance(i + 1);
                continue;
            }

            if (c == '?')
            {
                error = DuckDbText.Positional;
                return null;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1])))
            {
                int k = Postgres.PostgresReadOnlyGate.NumberEnd(sql, i);
                tokens.Add(new Token(TokenKind.Number, sql[i..k], line, column, i, k));
                Advance(k);
                continue;
            }

            if (IsNameStart(c))
            {
                int k = i + 1;
                while (k < sql.Length && IsNameChar(sql[k]))
                {
                    k++;
                }

                tokens.Add(new Token(TokenKind.Word, AsciiUpper(sql[i..k]), line, column, i, k));
                Advance(k);
                continue;
            }

            tokens.Add(new Token(TokenKind.Symbol, c.ToString(), line, column, i, i + 1));
            Advance(i + 1);
        }

        return tokens;
    }
}
