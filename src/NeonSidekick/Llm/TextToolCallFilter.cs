using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm;

/// <summary>
/// Catches a tool call a model wrote out as text — <c>generate_image(prompt="a dog surfing", seed=7)</c> — in a streamed
/// reply, keeps it out of what is shown and spoken, and hands it to <see cref="Assistant"/> to run as a real call. The
/// <see cref="ThinkTagFilter"/> shape: one instance per model call, pure, no console, no log — the caller reads
/// <see cref="Calls"/> and <see cref="SawBroken"/>.
///
/// <para>Why it exists (2026-09-25, the user's report from <c>/botchat</c>): a small local model offered
/// <c>generate_image</c> sometimes writes the call into its reply instead of making it, so the transcript showed the
/// prompt, the voice read it out, and no picture came. Only the names of the tools the turn offers are caught, and only
/// when <see cref="Assistant.TextToolCalls"/> is on — botchat's call: in the main chat a reply that shows a call as an
/// example would otherwise run it.</para>
///
/// <para>Rules: a call is a tool's name at a word start (the character before is no letter, digit or <c>_</c>), optional
/// spaces, then <c>(</c>, and runs to the matching <c>)</c> outside quoted strings (<c>"…"</c> or <c>'…'</c>, backslash
/// escapes honoured). One backtick around it goes with it, and so does the whitespace after it. A suffix that could still
/// become a call's start is held back until the next delta completes or disproves it. A call still open when the stream
/// ends is dropped (<see cref="SawBroken"/>): half a call is no reply either. Everything else passes through in order.</para>
///
/// <para>A quote opens a string only where a value or key starts — right after the <c>(</c>, or after <c>=</c>, <c>:</c>,
/// <c>,</c>, <c>(</c>, <c>[</c> or <c>{</c> and any spaces (2026-09-28, code review): before, the apostrophe in
/// <c>generate_image(prompt=a dog's party) Look!</c> opened a string that never closed, so the call swallowed the rest of the
/// reply and the end of the stream dropped it all, text included. A quote mid-word (<c>dog's</c>, <c>12" vinyl</c>) is now
/// just a character. What is still misread: an apostrophe that starts a word after a comma (<c>prompt=dogs, 'tis fine</c>).</para>
///
/// <para>The line form (later on 2026-09-25, the user's report: <c>generate_image prompt: score_9, …, messy background    width: 1024
/// height: 1024</c> reached a <c>/botchat</c> transcript, spoken, no picture made): given the tools' parameter names, a line that
/// starts with a tool's name (spaces or one backtick before it) and goes on, after an optional <c>:</c>, with one of that tool's
/// parameters and <c>:</c> or <c>=</c>, is a call too, to the end of its line — its line break dropped with it. The arguments are
/// split at every parameter name followed by <c>:</c> or <c>=</c> (<see cref="LineArguments"/>), so a prompt keeps its commas. A line
/// that starts with the name and goes on otherwise is text as ever; the name mid-line never starts the line form. A line held for
/// the test is only released once it is whole, so the stream waits on such a line alone.</para>
/// </summary>
public sealed class TextToolCallFilter
{
    private readonly string[] _names;
    private readonly (string Name, string[] Parameters)[] _lineTools;
    private readonly List<(string Name, string Arguments)> _calls = [];
    private string _pending = string.Empty;
    private char _last = ' ';

    // The line form: whether the next character starts a line, and the line held for the test (null = none).
    private bool _lineStart = true;
    private StringBuilder? _line;

    // Inside a call: its name, its arguments so far, the paren depth, the open quote (none = '\0'), a pending escape.
    private string? _callName;
    private readonly StringBuilder _arguments = new();
    private int _depth;
    private char _quote;
    private bool _escape;
    private bool _backticked;

    // After a call: a closing backtick (when one opened it), then whitespace, are dropped.
    private bool _skipTick;
    private bool _skipWhitespace;

    /// <param name="names">The names of the tools the turn offers; no other name is ever caught.</param>
    public TextToolCallFilter(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _names = names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToArray();
        _lineTools = [];
    }

    /// <param name="tools">The tools the turn offers with their parameter names: the parenthesised form for every name, and the line form for a tool with parameters.</param>
    public TextToolCallFilter(IEnumerable<(string Name, IReadOnlyList<string> Parameters)> tools)
        : this((tools ?? throw new ArgumentNullException(nameof(tools))).Select(t => t.Name).ToList())
    {
        _lineTools = tools
            .Where(t => !string.IsNullOrEmpty(t.Name) && t.Parameters.Count > 0)
            .DistinctBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => (t.Name, t.Parameters.Where(p => p.Length > 0).OrderByDescending(p => p.Length).ToArray()))
            .ToArray();
    }

    /// <summary>The calls caught so far, in order: the tool's name and the text between its parentheses.</summary>
    public IReadOnlyList<(string Name, string Arguments)> Calls => _calls;

    /// <summary>A call was still open when the stream ended; its text was dropped.</summary>
    public bool SawBroken { get; private set; }

    /// <summary>Feeds one delta and returns the text that is safe to emit now, possibly empty.</summary>
    public string Push(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.Length == 0)
        {
            return string.Empty;
        }

        string s = _pending.Length == 0 ? delta : _pending + delta;
        _pending = string.Empty;
        StringBuilder? output = null;
        int pos = 0;

        while (pos < s.Length)
        {
            if (_callName is not null)
            {
                pos = ReadCall(s, pos);
                continue;
            }

            if (_line is not null)
            {
                // The line form's line, held to its end; then it is a call or its text.
                int end = s.IndexOf('\n', pos);
                if (end < 0)
                {
                    _line.Append(s, pos, s.Length - pos);
                    break;
                }

                _line.Append(s, pos, end - pos);
                pos = end + 1;
                string line = _line.ToString();
                if (!EndLine())
                {
                    // Not a call: the line is text after all, its break with it.
                    Emit(ref output, line + "\n", 0, line.Length + 1);
                }

                _lineStart = true;
                continue;
            }

            if (_skipTick)
            {
                _skipTick = false;
                if (s[pos] == '`')
                {
                    pos++;
                    continue;
                }
            }

            if (_skipWhitespace)
            {
                while (pos < s.Length && char.IsWhiteSpace(s[pos]))
                {
                    pos++;
                }

                if (pos == s.Length)
                {
                    break;
                }

                _skipWhitespace = false;
            }

            if (_lineStart && _lineTools.Length > 0)
            {
                var start0 = LineStartAt(s, pos);
                if (start0 == LineStart.Undecided)
                {
                    _pending = s[pos..];
                    break;
                }

                _lineStart = false;
                if (start0 == LineStart.Line)
                {
                    _line = new StringBuilder();
                    continue;
                }
            }

            // Up to the end of this line: the next one's start is the line form's to test.
            int newline = _lineTools.Length > 0 ? s.IndexOf('\n', pos) : -1;
            var (start, open, name, hold) = Find(s, pos, newline < 0 ? s.Length : newline + 1);
            if (name is null)
            {
                if (newline >= 0)
                {
                    Emit(ref output, s, pos, newline + 1 - pos);
                    pos = newline + 1;
                    _lineStart = true;
                    continue;
                }

                Emit(ref output, s, pos, hold - pos);
                _pending = s[hold..];
                break;
            }

            Emit(ref output, s, pos, start - pos);
            _callName = name;
            _backticked = s[start] == '`';
            _arguments.Clear();
            _depth = 0;
            _quote = '\0';
            _escape = false;
            pos = open + 1;
        }

        return output?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Releases whatever was held back as a possible call start. A call still open is dropped, and <see cref="SawBroken"/> set.
    /// </summary>
    public string Flush()
    {
        if (_callName is not null)
        {
            SawBroken = true;
            _callName = null;
            _arguments.Clear();
        }

        string held = _pending;
        _pending = string.Empty;
        if (_line is not null)
        {
            // A line form's line the stream ended on: a call, or its text.
            string line = _line.ToString();
            held = EndLine() ? held : line + held;
        }

        return held;
    }

    private enum LineStart
    {
        None,
        Undecided,
        Line,
    }

    /// <summary>
    /// What the text from <paramref name="pos"/>, at a line's start, is to the line form: a tool's name then something other than
    /// <c>(</c> or a word character (<see cref="LineStart.Line"/>, tested once the line is whole), too little yet to say
    /// (<see cref="LineStart.Undecided"/>), or neither — the parenthesised form's own start included.
    /// </summary>
    private LineStart LineStartAt(string s, int pos)
    {
        int i = pos;
        while (i < s.Length && s[i] is ' ' or '\t')
        {
            i++;
        }

        if (i < s.Length && s[i] == '`')
        {
            i++;
        }

        if (i == s.Length)
        {
            return LineStart.Undecided;
        }

        var result = LineStart.None;
        foreach (var (name, _) in _lineTools)
        {
            int k = 0;
            while (k < name.Length && i + k < s.Length && s[i + k] == name[k])
            {
                k++;
            }

            if (i + k == s.Length)
            {
                // The text ends inside the name, or right after it: the next delta decides.
                result = LineStart.Undecided;
                continue;
            }

            if (k == name.Length && !IsWordChar(s[i + k]) && s[i + k] != '(')
            {
                int j = i + k;
                while (j < s.Length && s[j] is ' ' or '\t')
                {
                    j++;
                }

                if (j == s.Length)
                {
                    result = LineStart.Undecided;
                    continue;
                }

                if (s[j] != '(')
                {
                    return LineStart.Line;
                }
            }
        }

        return result;
    }

    /// <summary>The held line ends: a call (caught, true) or text for the caller to emit (false); the line is let go either way.</summary>
    private bool EndLine()
    {
        string line = _line!.ToString();
        _line = null;
        if (LineCall(line) is not { } call)
        {
            if (line.Length > 0)
            {
                _last = line[^1];
            }

            return false;
        }

        _calls.Add(call);
        _last = ' ';
        return true;
    }

    /// <summary>The line form's call on <paramref name="line"/> (no line break): the tool and its arguments as a JSON object, or null.</summary>
    private (string Name, string Arguments)? LineCall(string line)
    {
        string t = line.TrimStart(' ', '\t');
        if (t.StartsWith('`'))
        {
            t = t[1..];
        }

        foreach (var (name, parameters) in _lineTools)
        {
            if (!t.StartsWith(name, StringComparison.Ordinal) || (t.Length > name.Length && IsWordChar(t[name.Length])))
            {
                continue;
            }

            string rest = t[name.Length..].TrimStart();
            if (rest.StartsWith(':'))
            {
                rest = rest[1..].TrimStart();
            }

            if (LineArguments(rest, parameters) is { } arguments)
            {
                return (name, arguments);
            }
        }

        return null;
    }

    /// <summary>
    /// The line form's arguments (later on 2026-09-25): <paramref name="text"/> split at every one of <paramref name="parameters"/>
    /// (case ignored, the longest first, so <c>negative_extra</c> is never read as <c>negative</c>) at a word's start and followed by
    /// optional spaces and <c>:</c> or <c>=</c>; each value is the text up to the next, trimmed, a closing backtick and one pair of
    /// quotes around it taken off. A whole number, a number and <c>true</c>/<c>false</c> are themselves, anything else a string; a
    /// later key wins over an earlier one. A JSON object's text in the parameters' own spelling, which
    /// <see cref="ParseArguments"/> reads; null unless <paramref name="text"/> starts with a key. Pure.
    /// </summary>
    public static string? LineArguments(string text, IReadOnlyList<string> parameters)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(parameters);
        var ordered = parameters.Where(p => p.Length > 0).OrderByDescending(p => p.Length).ToArray();
        var keys = new List<(string Name, int Start, int ValueStart)>();
        for (int i = 0; i < text.Length; i++)
        {
            if (i > 0 && !char.IsWhiteSpace(text[i - 1]))
            {
                continue;
            }

            foreach (string parameter in ordered)
            {
                if (string.Compare(text, i, parameter, 0, parameter.Length, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    continue;
                }

                int j = i + parameter.Length;
                while (j < text.Length && text[j] is ' ' or '\t')
                {
                    j++;
                }

                if (j < text.Length && text[j] is ':' or '=')
                {
                    keys.Add((parameter, i, j + 1));
                    i = j;
                    break;
                }
            }
        }

        if (keys.Count == 0 || keys[0].Start != 0)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        for (int k = 0; k < keys.Count; k++)
        {
            int end = k + 1 < keys.Count ? keys[k + 1].Start : text.Length;
            string value = text[keys[k].ValueStart..end].Trim().TrimEnd('`').Trim();
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            if (!values.ContainsKey(keys[k].Name))
            {
                order.Add(keys[k].Name);
            }

            values[keys[k].Name] = value;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (string name in order)
            {
                string value = values[name];
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
                {
                    writer.WriteNumber(name, whole);
                }
                else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
                {
                    writer.WriteNumber(name, number);
                }
                else if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteBoolean(name, value.Equals("true", StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    writer.WriteString(name, value);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads a call's arguments from <paramref name="pos"/>; returns where it stopped (the end, or just past the closing paren).</summary>
    private int ReadCall(string s, int pos)
    {
        for (; pos < s.Length; pos++)
        {
            char c = s[pos];
            if (_quote != '\0')
            {
                if (_escape)
                {
                    _escape = false;
                }
                else if (c == '\\')
                {
                    _escape = true;
                }
                else if (c == _quote)
                {
                    _quote = '\0';
                }
            }
            else if (c is '"' or '\'' && AtValueStart())
            {
                _quote = c;
            }
            else if (c is '(' or '{' or '[')
            {
                _depth++;
            }
            else if (c is ')' or '}' or ']')
            {
                if (_depth == 0 && c == ')')
                {
                    _calls.Add((_callName!, _arguments.ToString()));
                    _callName = null;
                    _arguments.Clear();
                    _skipTick = _backticked;
                    _skipWhitespace = true;
                    _last = ' ';
                    _lineStart = true;
                    return pos + 1;
                }

                _depth = Math.Max(0, _depth - 1);
            }

            _arguments.Append(c);
        }

        return pos;
    }

    /// <summary>Whether a quote here would open a value or a key: nothing but spaces so far, or <c>= : , ( [ {</c> just before.</summary>
    private bool AtValueStart()
    {
        int i = _arguments.Length - 1;
        while (i >= 0 && char.IsWhiteSpace(_arguments[i]))
        {
            i--;
        }

        return i < 0 || _arguments[i] is '=' or ':' or ',' or '(' or '[' or '{';
    }

    /// <summary>
    /// The first call start at or after <paramref name="pos"/>: where it starts (its backtick included), where its <c>(</c> is, and
    /// the tool's name. With no whole start, <c>Name</c> is null and <c>Hold</c> is where the text that could still become one begins
    /// (<paramref name="s"/>'s length when none can).
    /// </summary>
    private (int Start, int Open, string? Name, int Hold) Find(string s, int pos, int limit)
    {
        int hold = s.Length;
        for (int i = pos; i < limit; i++)
        {
            char before = i > 0 ? s[i - 1] : _last;
            if (IsWordChar(before))
            {
                continue;
            }

            foreach (string name in _names)
            {
                int k = 0;
                while (k < name.Length && i + k < s.Length && s[i + k] == name[k])
                {
                    k++;
                }

                if (i + k == s.Length && k < name.Length)
                {
                    // The text ends inside the name: it may yet become a call.
                    hold = Math.Min(hold, StartWithTick(s, pos, i));
                    continue;
                }

                if (k < name.Length)
                {
                    continue;
                }

                int j = i + k;
                while (j < s.Length && s[j] is ' ' or '\t')
                {
                    j++;
                }

                if (j == s.Length)
                {
                    // The whole name, then only spaces so far: the next delta decides.
                    hold = Math.Min(hold, StartWithTick(s, pos, i));
                    continue;
                }

                if (s[j] == '(')
                {
                    return (StartWithTick(s, pos, i), j, name, hold);
                }
            }

            if (hold < s.Length && hold <= i)
            {
                break;
            }
        }

        // A lone backtick at the very end may open a call too.
        if (limit == s.Length && hold == s.Length && s.Length > pos && s[^1] == '`')
        {
            hold = s.Length - 1;
        }

        return (0, 0, null, hold);
    }

    /// <summary><paramref name="i"/>, or the backtick just before it when there is one this delta may still drop.</summary>
    private static int StartWithTick(string s, int pos, int i) => i > pos && s[i - 1] == '`' ? i - 1 : i;

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void Emit(ref StringBuilder? output, string s, int start, int count)
    {
        if (count <= 0)
        {
            return;
        }

        (output ??= new StringBuilder()).Append(s, start, count);
        _last = s[start + count - 1];
    }

    /// <summary>
    /// A written call's arguments as the tool reads them (2026-09-25): <c>key="value"</c> or <c>key: 'value'</c> pairs split by commas —
    /// quoted strings (<c>\" \' \\ \n</c> escapes), whole numbers, numbers, <c>true</c>/<c>false</c>, <c>null</c>/<c>None</c> (left out),
    /// or a bare word as a string — or one JSON object. Empty text is a call with no arguments. Null when it does not parse. Pure.
    ///
    /// <para>A bare value keeps its commas up to the next key (2026-09-28, code review): a comma ends it only when a key and the
    /// same separator its own key used follow (<see cref="NextIsKey"/>), so <c>prompt=score_9, masterpiece, a castle, seed=7</c> is
    /// a prompt and a seed. Before, the value stopped at the first comma, <c>masterpiece</c> was read as a key with no <c>=</c>,
    /// and a call whose text was already cut from the reply was not run.</para>
    /// </summary>
    public static AIFunctionArguments? ParseArguments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string t = text.Trim();
        var arguments = new AIFunctionArguments();
        if (t.Length == 0)
        {
            return arguments;
        }

        if (t[0] == '{')
        {
            try
            {
                using var document = JsonDocument.Parse(t);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    arguments[property.Name] = property.Value.Clone();
                }

                return arguments;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        int pos = 0;
        while (true)
        {
            SkipSpace(t, ref pos);
            if (pos == t.Length)
            {
                return arguments;
            }

            string? key = t[pos] is '"' or '\'' ? ReadQuoted(t, ref pos) : ReadIdentifier(t, ref pos);
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            SkipSpace(t, ref pos);
            if (pos == t.Length || t[pos] is not ('=' or ':'))
            {
                return null;
            }

            char separator = t[pos++];
            SkipSpace(t, ref pos);
            if (pos < t.Length && t[pos] is '"' or '\'')
            {
                if (ReadQuoted(t, ref pos) is not { } value)
                {
                    return null;
                }

                arguments[key] = value;
            }
            else
            {
                int end = pos;
                while (true)
                {
                    int comma = t.IndexOf(',', end);
                    if (comma < 0 || NextIsKey(t, comma + 1, separator))
                    {
                        end = comma < 0 ? t.Length : comma;
                        break;
                    }

                    end = comma + 1;
                }

                string raw = t[pos..end].Trim();
                pos = end;
                if (raw.Length == 0)
                {
                    return null;
                }

                if (Scalar(raw) is { } value)
                {
                    arguments[key] = value;
                }
            }

            SkipSpace(t, ref pos);
            if (pos == t.Length)
            {
                return arguments;
            }

            if (t[pos] != ',')
            {
                return null;
            }

            pos++;
        }
    }

    /// <summary>A bare value: a bool, a whole number, a number, nothing for <c>null</c>/<c>None</c>, else the word itself.</summary>
    private static object? Scalar(string raw)
    {
        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (raw.Equals("null", StringComparison.OrdinalIgnoreCase) || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
        {
            return whole;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
        {
            return number;
        }

        return raw;
    }

    /// <summary>Whether a key (a word, or a quoted one) and then <paramref name="separator"/> follow <paramref name="pos"/>, spaces allowed.</summary>
    private static bool NextIsKey(string t, int pos, char separator)
    {
        SkipSpace(t, ref pos);
        if (pos == t.Length)
        {
            return false;
        }

        string? key = t[pos] is '"' or '\'' ? ReadQuoted(t, ref pos) : ReadIdentifier(t, ref pos);
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        SkipSpace(t, ref pos);
        return pos < t.Length && t[pos] == separator;
    }

    private static void SkipSpace(string t, ref int pos)
    {
        while (pos < t.Length && char.IsWhiteSpace(t[pos]))
        {
            pos++;
        }
    }

    private static string ReadIdentifier(string t, ref int pos)
    {
        int start = pos;
        while (pos < t.Length && IsWordChar(t[pos]))
        {
            pos++;
        }

        return t[start..pos];
    }

    /// <summary>A quoted string from <paramref name="pos"/> (on its opening quote) to its closing one, unescaped; null when it never closes.</summary>
    private static string? ReadQuoted(string t, ref int pos)
    {
        char quote = t[pos++];
        var value = new StringBuilder();
        while (pos < t.Length)
        {
            char c = t[pos++];
            if (c == quote)
            {
                return value.ToString();
            }

            if (c == '\\' && pos < t.Length)
            {
                char next = t[pos++];
                value.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
                continue;
            }

            value.Append(c);
        }

        return null;
    }
}
