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
/// </summary>
public sealed class TextToolCallFilter
{
    private readonly string[] _names;
    private readonly List<(string Name, string Arguments)> _calls = [];
    private string _pending = string.Empty;
    private char _last = ' ';

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

            var (start, open, name, hold) = Find(s, pos);
            if (name is null)
            {
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
        return held;
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
            else if (c is '"' or '\'')
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
                    return pos + 1;
                }

                _depth = Math.Max(0, _depth - 1);
            }

            _arguments.Append(c);
        }

        return pos;
    }

    /// <summary>
    /// The first call start at or after <paramref name="pos"/>: where it starts (its backtick included), where its <c>(</c> is, and
    /// the tool's name. With no whole start, <c>Name</c> is null and <c>Hold</c> is where the text that could still become one begins
    /// (<paramref name="s"/>'s length when none can).
    /// </summary>
    private (int Start, int Open, string? Name, int Hold) Find(string s, int pos)
    {
        int hold = s.Length;
        for (int i = pos; i < s.Length; i++)
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
        if (hold == s.Length && s.Length > pos && s[^1] == '`')
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

            pos++;
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
                int end = t.IndexOf(',', pos);
                end = end < 0 ? t.Length : end;
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
