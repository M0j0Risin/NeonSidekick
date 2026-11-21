namespace NeonSidekick.UI.Markdown;

/// <summary>
/// Splits a fenced block's text into <see cref="CodeToken"/>s for <see cref="MarkdownView"/> to
/// colour: one forward pass, no regex, no backtracking. The pane re-parses the whole reply on
/// every tick while it streams, so this must stay linear and allocation-light; and the fence is
/// often still open, so an unterminated string or block comment simply runs to the end of the
/// text rather than failing. The whole block is lexed at once (not line by line) so a block
/// comment or a triple-quoted string colours every line it spans.
///
/// <para>The tokens cover the text exactly — no gaps, no overlaps, adjacent runs of one kind merged.</para>
/// </summary>
public static class CodeLexer
{
    private const string PunctuationChars = "{}[]()<>=+-*/%&|^!~?:;,.@#\\";

    /// <summary>
    /// <paramref name="lines"/> lexed as one text (a block comment colours every line it spans) and cut back into exactly one list of
    /// (text, kind) runs per line, a token crossing a line break split at it; a blank line is an empty list (2026-11-20, the memory-remember
    /// review's #10, #30, #31: the one walk a reply's code block, a diff and the theme preview share, each drawing the runs its own way).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(string Text, CodeTokenKind Kind)>> LexLines(IReadOnlyList<string> lines, CodeLanguage language)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(language);
        var result = new List<IReadOnlyList<(string Text, CodeTokenKind Kind)>>(lines.Count);
        if (lines.Count == 0)
        {
            return result;
        }

        string text = string.Join('\n', lines);
        var current = new List<(string Text, CodeTokenKind Kind)>();
        foreach (var token in Lex(text, language))
        {
            int start = token.Start;
            while (start < token.End)
            {
                int newline = text.IndexOf('\n', start, token.End - start);
                int stop = newline < 0 ? token.End : newline;
                if (stop > start)
                {
                    current.Add((text[start..stop], token.Kind));
                }

                if (newline < 0)
                {
                    break;
                }

                result.Add(current);
                current = [];
                start = newline + 1;
            }
        }

        result.Add(current);

        // A lexer that dropped trailing blank lines' breaks leaves the list short: blank rows for them (DiffView's guard until 2026-11-20).
        while (result.Count < lines.Count)
        {
            result.Add([]);
        }

        return result;
    }

    public static IReadOnlyList<CodeToken> Lex(string code, CodeLanguage language)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(language);
        var tokens = new List<CodeToken>();
        switch (language.Mode)
        {
            case CodeLexMode.Markup:
                LexMarkup(code, tokens);
                break;
            case CodeLexMode.Diff:
                LexDiff(code, tokens);
                break;
            default:
                LexCode(code, language, tokens);
                break;
        }

        return tokens;
    }

    private static void Add(List<CodeToken> tokens, CodeTokenKind kind, int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        if (tokens.Count > 0 && tokens[^1].Kind == kind && tokens[^1].End == start)
        {
            tokens[^1] = tokens[^1] with { Length = end - tokens[^1].Start };
            return;
        }

        tokens.Add(new CodeToken(kind, start, end - start));
    }

    // ── The general scanner ─────────────────────────────────────────────────

    private static void LexCode(string code, CodeLanguage lang, List<CodeToken> tokens)
    {
        int i = 0;
        int depth = 0;
        bool lineStart = true;
        while (i < code.Length)
        {
            if (code[i] == '\n')
            {
                Add(tokens, CodeTokenKind.Plain, i, i + 1);
                i++;
                lineStart = true;
                continue;
            }

            if (lineStart)
            {
                lineStart = false;
                int text = i;
                while (text < code.Length && code[text] is ' ' or '\t')
                {
                    text++;
                }

                Add(tokens, CodeTokenKind.Plain, i, text);
                i = text < code.Length ? LineHead(code, text, lang, tokens) : text;
                continue;
            }

            int end = Next(code, i, lang, ref depth, out var kind);
            Add(tokens, kind, i, end);
            i = end;
        }
    }

    /// <summary>The shapes only a line's first word can have: a directive, a section, a bare key. Returns where the rest begins.</summary>
    private static int LineHead(string code, int i, CodeLanguage lang, List<CodeToken> tokens)
    {
        int n = code.Length;
        if (lang.Preprocessor && code[i] == '#')
        {
            int j = i + 1;
            while (j < n && code[j] == ' ')
            {
                j++;
            }

            while (j < n && char.IsAsciiLetter(code[j]))
            {
                j++;
            }

            Add(tokens, CodeTokenKind.Keyword, i, j);
            return j;
        }

        if (lang.Sections && code[i] == '[')
        {
            int lineEnd = LineEnd(code, i);
            int close = code.LastIndexOf(']', lineEnd - 1, lineEnd - i);
            int end = close >= i ? close + 1 : lineEnd;
            Add(tokens, CodeTokenKind.Heading, i, end);
            return end;
        }

        if (lang.LineKeySeparator is not char separator)
        {
            return i;
        }

        // YAML's list markers ahead of the key: "- name: x", "- - x".
        int start = i;
        while (separator == ':' && start + 1 < n && code[start] == '-' && code[start + 1] == ' ')
        {
            Add(tokens, CodeTokenKind.Punctuation, start, start + 1);
            Add(tokens, CodeTokenKind.Plain, start + 1, start + 2);
            start += 2;
        }

        int k = start;
        while (k < n && code[k] != separator && code[k] is not ('\n' or '#' or '"' or '\''))
        {
            k++;
        }

        bool isKey = k > start && k < n && code[k] == separator
            && (separator != ':' || k + 1 >= n || code[k + 1] is ' ' or '\t' or '\n' or '\r');
        if (!isKey)
        {
            return start;
        }

        int keyEnd = k;
        while (keyEnd > start && code[keyEnd - 1] is ' ' or '\t')
        {
            keyEnd--;
        }

        Add(tokens, CodeTokenKind.Attribute, start, keyEnd);
        Add(tokens, CodeTokenKind.Plain, keyEnd, k);
        Add(tokens, CodeTokenKind.Punctuation, k, k + 1);
        return k + 1;
    }

    /// <summary>The token at <paramref name="i"/> (never at a line break): its end, and its kind in <paramref name="kind"/>.</summary>
    private static int Next(string code, int i, CodeLanguage lang, ref int depth, out CodeTokenKind kind)
    {
        int n = code.Length;
        char c = code[i];
        bool css = lang.Mode == CodeLexMode.Css;

        foreach (var (open, close) in lang.BlockComments)
        {
            if (At(code, i, open))
            {
                kind = CodeTokenKind.Comment;
                int e = code.IndexOf(close, i + open.Length, StringComparison.Ordinal);
                return e < 0 ? n : e + close.Length;
            }
        }

        foreach (string prefix in lang.LineComments)
        {
            if (At(code, i, prefix) && (!lang.CommentNeedsBoundary || i == 0 || char.IsWhiteSpace(code[i - 1])))
            {
                kind = CodeTokenKind.Comment;
                return LineEnd(code, i);
            }
        }

        if (lang.Lifetimes && c == '\'' && i + 1 < n && IsIdentStart(code[i + 1]))
        {
            int j = i + 2;
            while (j < n && IsIdentChar(code[j]))
            {
                j++;
            }

            if (j >= n || code[j] != '\'')
            {
                kind = CodeTokenKind.Type;
                return j;
            }
        }

        if (!IsIdentChar(Before(code, i)))
        {
            foreach (string prefix in lang.StringPrefixes)
            {
                int q = i + prefix.Length;
                if (q < n && lang.Quotes.Contains(code[q]) && string.Compare(code, i, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    kind = CodeTokenKind.String;
                    return StringEnd(code, q, lang, verbatim: prefix.Contains('@'));
                }
            }
        }

        if (lang.Quotes.Contains(c))
        {
            int e = StringEnd(code, i, lang, verbatim: false);
            kind = lang.StringKeys && FollowedBy(code, e, ':') ? CodeTokenKind.Attribute : CodeTokenKind.String;
            return e;
        }

        if (lang.VariablePrefix == c && i + 1 < n)
        {
            char d = code[i + 1];
            if (d == '{')
            {
                int close = code.IndexOf('}', i + 2);
                int lineEnd = LineEnd(code, i);
                kind = CodeTokenKind.Variable;
                return close < 0 || close > lineEnd ? lineEnd : close + 1;
            }

            if (IsIdentStart(d))
            {
                int j = i + 2;
                while (j < n && (IsIdentChar(code[j]) || (code[j] == ':' && j + 1 < n && IsIdentStart(code[j + 1]))))
                {
                    j++;
                }

                kind = CodeTokenKind.Variable;
                return j;
            }

            if (char.IsAsciiDigit(d) || d is '?' or '@' or '#' or '*' or '!' or '$')
            {
                kind = CodeTokenKind.Variable;
                return i + 2;
            }
        }

        if (lang.AttributePrefix == c && i + 1 < n && IsIdentStart(code[i + 1]))
        {
            int j = i + 2;
            while (j < n && (IsIdentChar(code[j]) || code[j] == '-' || (code[j] == '.' && j + 1 < n && IsIdentStart(code[j + 1]))))
            {
                j++;
            }

            kind = CodeTokenKind.Attribute;
            return j;
        }

        if (lang.HashAttributes && c == '#' && (At(code, i + 1, "[") || At(code, i + 1, "![")))
        {
            kind = CodeTokenKind.Attribute;
            return BracketEnd(code, code.IndexOf('[', i));
        }

        if (css && c == '#' && i + 1 < n && char.IsAsciiLetterOrDigit(code[i + 1]))
        {
            int j = i + 2;
            while (j < n && (IsIdentChar(code[j]) || code[j] == '-'))
            {
                j++;
            }

            kind = depth > 0 ? CodeTokenKind.Number : CodeTokenKind.Tag;
            return j;
        }

        if (css && depth == 0 && c is '.' or ':' && i + 1 < n && (IsIdentStart(code[i + 1]) || code[i + 1] == ':'))
        {
            int j = i + 1;
            while (j < n && code[j] == ':')
            {
                j++;
            }

            while (j < n && (IsIdentChar(code[j]) || code[j] == '-'))
            {
                j++;
            }

            kind = c == '.' ? CodeTokenKind.Tag : CodeTokenKind.Keyword;
            return j;
        }

        if (lang.DashFlags && c == '-' && char.IsWhiteSpace(Before(code, i)) && i + 1 < n
            && (char.IsLetter(code[i + 1]) || (code[i + 1] == '-' && i + 2 < n && char.IsLetter(code[i + 2]))))
        {
            int j = i + 2;
            while (j < n && (IsIdentChar(code[j]) || code[j] == '-'))
            {
                j++;
            }

            kind = CodeTokenKind.Attribute;
            return j;
        }

        if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(code[i + 1]) && !IsIdentChar(Before(code, i))))
        {
            kind = CodeTokenKind.Number;
            return NumberEnd(code, i);
        }

        if (IsIdentStart(c) || (css && c == '-' && i + 1 < n && (char.IsLetter(code[i + 1]) || code[i + 1] == '-')))
        {
            int j = i + 1;
            while (j < n && (IsIdentChar(code[j]) || (code[j] == '-' && (css || lang.DashedNames) && j + 1 < n && char.IsLetterOrDigit(code[j + 1]))))
            {
                j++;
            }

            if (lang.Macros && j < n && code[j] == '!' && (j + 1 >= n || code[j + 1] != '='))
            {
                kind = CodeTokenKind.Function;
                return j + 1;
            }

            kind = Classify(code, i, j, lang, depth);
            return j;
        }

        if (PunctuationChars.Contains(c))
        {
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
            }

            kind = CodeTokenKind.Punctuation;
            return i + 1;
        }

        int run = i + 1;
        while (run < n && code[run] is ' ' or '\t')
        {
            run++;
        }

        kind = CodeTokenKind.Plain;
        return c is ' ' or '\t' ? run : i + 1;
    }

    private static CodeTokenKind Classify(string code, int start, int end, CodeLanguage lang, int depth)
    {
        var word = code.AsSpan(start, end - start);
        if (lang.Mode == CodeLexMode.Css)
        {
            if (depth == 0)
            {
                return CodeTokenKind.Tag;
            }

            if (FollowedBy(code, end, ':'))
            {
                return CodeTokenKind.Attribute;
            }
        }

        if (lang.IsKeyword(word))
        {
            return CodeTokenKind.Keyword;
        }

        if (lang.IsBuiltin(word))
        {
            return CodeTokenKind.Type;
        }

        if (lang.DashedNames && word.Contains('-'))
        {
            return CodeTokenKind.Function;
        }

        if (end < code.Length && code[end] == '(')
        {
            return CodeTokenKind.Function;
        }

        if (lang.CapitalizedTypes && char.IsUpper(word[0]) && !IsAllCaps(word))
        {
            return CodeTokenKind.Type;
        }

        return CodeTokenKind.Plain;
    }

    /// <summary>The end of the string whose quote is at <paramref name="open"/>: past its closing quote, at the line end for a single-line quote left open, or the text's end.</summary>
    private static int StringEnd(string code, int open, CodeLanguage lang, bool verbatim)
    {
        int n = code.Length;
        char quote = code[open];
        if (lang.TripleQuotes && open + 2 < n && code[open + 1] == quote && code[open + 2] == quote)
        {
            int close = code.IndexOf(new string(quote, 3), open + 3, StringComparison.Ordinal);
            return close < 0 ? n : close + 3;
        }

        bool multiline = verbatim || lang.MultilineQuotes.Contains(quote);
        for (int j = open + 1; j < n; j++)
        {
            char ch = code[j];
            if (ch == '\n' && !multiline)
            {
                return j;
            }

            if (!verbatim && ch == lang.Escape)
            {
                j++;
                continue;
            }

            if (ch == quote)
            {
                if ((verbatim || lang.DoubledQuoteEscapes) && j + 1 < n && code[j + 1] == quote)
                {
                    j++;
                    continue;
                }

                return j + 1;
            }
        }

        return n;
    }

    private static int NumberEnd(string code, int i)
    {
        int n = code.Length;
        bool hex = code[i] == '0' && i + 1 < n && code[i + 1] is 'x' or 'X';
        int j = i + 1;
        while (j < n)
        {
            char d = code[j];
            bool more = char.IsAsciiLetterOrDigit(d) || d == '_'
                || (d == '.' && j + 1 < n && char.IsAsciiDigit(code[j + 1]))
                || (!hex && d is '+' or '-' && code[j - 1] is 'e' or 'E' && j + 1 < n && char.IsAsciiDigit(code[j + 1]));
            if (!more)
            {
                break;
            }

            j++;
        }

        return j;
    }

    /// <summary>Past the <c>]</c> matching the <c>[</c> at <paramref name="open"/>, or the text's end.</summary>
    private static int BracketEnd(string code, int open)
    {
        int depth = 0;
        for (int j = open; j < code.Length; j++)
        {
            if (code[j] == '[')
            {
                depth++;
            }
            else if (code[j] == ']' && --depth == 0)
            {
                return j + 1;
            }
        }

        return code.Length;
    }

    // ── Markup ──────────────────────────────────────────────────────────────

    private static void LexMarkup(string code, List<CodeToken> tokens)
    {
        int n = code.Length;
        int i = 0;
        while (i < n)
        {
            if (At(code, i, "<!--"))
            {
                int e = code.IndexOf("-->", i + 4, StringComparison.Ordinal);
                e = e < 0 ? n : e + 3;
                Add(tokens, CodeTokenKind.Comment, i, e);
                i = e;
                continue;
            }

            if (At(code, i, "<![CDATA["))
            {
                int e = code.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                e = e < 0 ? n : e + 3;
                Add(tokens, CodeTokenKind.String, i, e);
                i = e;
                continue;
            }

            if (code[i] == '<' && i + 1 < n && code[i + 1] is '!' or '?')
            {
                int e = code.IndexOf('>', i);
                e = e < 0 ? n : e + 1;
                Add(tokens, CodeTokenKind.Keyword, i, e);
                i = e;
                continue;
            }

            if (code[i] == '<' && i + 1 < n && (char.IsLetter(code[i + 1]) || code[i + 1] == '/'))
            {
                i = MarkupTag(code, i, tokens);
                continue;
            }

            if (code[i] == '&')
            {
                int e = i + 1;
                while (e < n && (char.IsAsciiLetterOrDigit(code[e]) || code[e] == '#'))
                {
                    e++;
                }

                if (e > i + 1 && e < n && code[e] == ';')
                {
                    Add(tokens, CodeTokenKind.Keyword, i, e + 1);
                    i = e + 1;
                    continue;
                }
            }

            int j = i + 1;
            while (j < n && code[j] is not ('<' or '&'))
            {
                j++;
            }

            Add(tokens, CodeTokenKind.Plain, i, j);
            i = j;
        }
    }

    /// <summary>The tag opening at <paramref name="i"/>: bracket, name, attributes, values. Returns its end.</summary>
    private static int MarkupTag(string code, int i, List<CodeToken> tokens)
    {
        int n = code.Length;
        int j = code[i + 1] == '/' ? i + 2 : i + 1;
        Add(tokens, CodeTokenKind.Punctuation, i, j);
        int name = j;
        while (j < n && IsMarkupNameChar(code[j]))
        {
            j++;
        }

        Add(tokens, CodeTokenKind.Tag, name, j);
        while (j < n)
        {
            char c = code[j];
            if (c == '>')
            {
                Add(tokens, CodeTokenKind.Punctuation, j, j + 1);
                return j + 1;
            }

            if (c == '/' && j + 1 < n && code[j + 1] == '>')
            {
                Add(tokens, CodeTokenKind.Punctuation, j, j + 2);
                return j + 2;
            }

            if (c == '<')
            {
                // A new tag: this one was never closed (or is still streaming).
                return j;
            }

            if (c is '"' or '\'')
            {
                int e = code.IndexOf(c, j + 1);
                e = e < 0 ? n : e + 1;
                Add(tokens, CodeTokenKind.String, j, e);
                j = e;
                continue;
            }

            if (c == '=')
            {
                Add(tokens, CodeTokenKind.Punctuation, j, j + 1);
                j++;
                continue;
            }

            if (char.IsLetter(c) || c is '_' or ':' or '@')
            {
                int e = j + 1;
                while (e < n && IsMarkupNameChar(code[e]))
                {
                    e++;
                }

                Add(tokens, CodeTokenKind.Attribute, j, e);
                j = e;
                continue;
            }

            Add(tokens, CodeTokenKind.Plain, j, j + 1);
            j++;
        }

        return n;
    }

    private static bool IsMarkupNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.';

    // ── Diff ────────────────────────────────────────────────────────────────

    private static void LexDiff(string code, List<CodeToken> tokens)
    {
        int i = 0;
        while (i < code.Length)
        {
            int end = LineEnd(code, i);
            var kind = code.AsSpan(i, end - i) switch
            {
                var l when l.StartsWith("+++") || l.StartsWith("---") || l.StartsWith("@@") || l.StartsWith("diff ") || l.StartsWith("index ") => CodeTokenKind.Heading,
                var l when l.StartsWith("+") => CodeTokenKind.Inserted,
                var l when l.StartsWith("-") => CodeTokenKind.Deleted,
                _ => CodeTokenKind.Plain,
            };
            Add(tokens, kind, i, end);
            Add(tokens, CodeTokenKind.Plain, end, Math.Min(end + 1, code.Length));
            i = end + 1;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool At(string code, int i, string text) =>
        i + text.Length <= code.Length && string.CompareOrdinal(code, i, text, 0, text.Length) == 0;

    private static char Before(string code, int i) => i > 0 ? code[i - 1] : ' ';

    private static int LineEnd(string code, int i)
    {
        int e = code.IndexOf('\n', i);
        return e < 0 ? code.Length : e;
    }

    /// <summary>The next character at or after <paramref name="i"/>, past spaces and tabs, is <paramref name="c"/>.</summary>
    private static bool FollowedBy(string code, int i, char c)
    {
        while (i < code.Length && code[i] is ' ' or '\t')
        {
            i++;
        }

        return i < code.Length && code[i] == c;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsAllCaps(ReadOnlySpan<char> word)
    {
        if (word.Length < 2)
        {
            return false;
        }

        foreach (char c in word)
        {
            if (char.IsLower(c))
            {
                return false;
            }
        }

        return true;
    }
}
