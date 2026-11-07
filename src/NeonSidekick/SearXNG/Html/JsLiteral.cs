// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SearXNG.Net.Html;

/// <summary>
/// Reads the JavaScript data literals some pages embed: objects, arrays, strings, numbers, and names bound by an enclosing function.
/// It stands in for the <c>js_*</c> helpers in SearXNG's <c>searx/utils.py</c>, which rewrite the text into JSON. Nothing is run:
/// anything but data is a <see cref="FormatException"/>. Objects become dictionaries and arrays become lists.
/// </summary>
internal sealed partial class JsLiteral
{
    private const int MaximumDepth = 128;

    private readonly string _text;
    private readonly int _end;
    private readonly IReadOnlyDictionary<string, object?> _names;
    private int _position;

    private JsLiteral(string text, int start, int end, IReadOnlyDictionary<string, object?> names)
    {
        _text = text;
        _position = start;
        _end = end;
        _names = names;
    }

    /// <summary>
    /// Reads the array whose <c>[</c> is at <paramref name="start"/>. An element may be plain data, or a function that only returns
    /// data, called with the values for its parameters: <c>(function(a){return {x:a}}(1))</c>. That's how SvelteKit shrinks a page.
    /// </summary>
    public static List<object?> ParseArrayOfCalls(string text, int start)
    {
        int end = FindClosing(text, start, '[', ']');
        var empty = new Dictionary<string, object?>(StringComparer.Ordinal);
        var reader = new JsLiteral(text, start + 1, end, empty);
        var items = new List<object?>();
        while (true)
        {
            reader.SkipSpaceAnd(",)");
            if (reader._position >= end)
            {
                return items;
            }

            items.Add(reader.ReadCallOrValue());
        }
    }

    /// <summary>Finds the bracket closing the one at <paramref name="start"/>, ignoring any in strings.</summary>
    public static int FindClosing(string text, int start, char open, char close)
    {
        int depth = 0;
        char quote = '\0';
        bool escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'' or '`')
            {
                quote = c;
            }
            else if (c == open)
            {
                depth++;
            }
            else if (c == close && --depth == 0)
            {
                return i;
            }
        }

        throw new FormatException("Unbalanced bracket.");
    }

    private object? ReadCallOrValue()
    {
        var call = Call().Match(_text, _position, _end - _position);
        if (!call.Success)
        {
            return ReadValue(0);
        }

        int bodyStart = call.Index + call.Length - 1;
        int bodyEnd = FindClosing(_text, bodyStart, '{', '}');
        int argumentsStart = _text.IndexOf('(', bodyEnd);
        if (argumentsStart < 0 || argumentsStart >= _end)
        {
            throw new FormatException("A function with no call.");
        }

        int argumentsEnd = FindClosing(_text, argumentsStart, '(', ')');
        var arguments = new JsLiteral(_text, argumentsStart + 1, argumentsEnd, _names);
        var names = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in call.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            arguments.SkipSpaceAnd(",");
            if (arguments._position >= argumentsEnd)
            {
                break;
            }

            names[parameter] = arguments.ReadValue(0);
        }

        var body = new JsLiteral(_text, bodyStart, bodyEnd + 1, names).ReadValue(0);
        _position = argumentsEnd + 1;
        return body;
    }

    private object? ReadValue(int depth)
    {
        if (depth > MaximumDepth)
        {
            throw new FormatException("Nested too deeply.");
        }

        SkipSpaceAnd("");
        if (_position >= _end)
        {
            throw new FormatException("Ran out of text.");
        }

        char c = _text[_position];
        return c switch
        {
            '{' => ReadObject(depth + 1),
            '[' => ReadArray(depth + 1),
            '"' or '\'' or '`' => ReadString(),
            _ when char.IsAsciiDigit(c) || c is '-' or '+' or '.' => ReadNumber(),
            _ => ReadWord(depth),
        };
    }

    private Dictionary<string, object?> ReadObject(int depth)
    {
        _position++;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            SkipSpaceAnd(",");
            Require();
            if (_text[_position] == '}')
            {
                _position++;
                return result;
            }

            var key = ReadKey();
            SkipSpaceAnd("");
            Require();
            if (_text[_position] != ':')
            {
                throw new FormatException("Expected a colon at " + _position.ToString(CultureInfo.InvariantCulture) + ".");
            }

            _position++;
            result[key] = ReadValue(depth);
        }
    }

    private List<object?> ReadArray(int depth)
    {
        _position++;
        var result = new List<object?>();
        while (true)
        {
            SkipSpaceAnd(",");
            Require();
            if (_text[_position] == ']')
            {
                _position++;
                return result;
            }

            result.Add(ReadValue(depth));
        }
    }

    private string ReadKey()
    {
        if (_text[_position] is '"' or '\'' or '`')
        {
            return ReadString();
        }

        int start = _position;
        while (_position < _end && (char.IsLetterOrDigit(_text[_position]) || _text[_position] is '_' or '$' or '-'))
        {
            _position++;
        }

        return _position > start
            ? _text[start.._position]
            : throw new FormatException("Expected a key at " + start.ToString(CultureInfo.InvariantCulture) + ".");
    }

    private string ReadString()
    {
        char quote = _text[_position++];
        var value = new StringBuilder();
        while (true)
        {
            Require();
            char c = _text[_position++];
            if (c == quote)
            {
                return value.ToString();
            }

            if (c != '\\')
            {
                value.Append(c);
                continue;
            }

            Require();
            ReadEscape(value);
        }
    }

    private void ReadEscape(StringBuilder value)
    {
        char c = _text[_position++];
        switch (c)
        {
            case 'n': value.Append('\n'); break;
            case 't': value.Append('\t'); break;
            case 'r': value.Append('\r'); break;
            case 'b': value.Append('\b'); break;
            case 'f': value.Append('\f'); break;
            case 'v': value.Append('\v'); break;
            case '0': value.Append('\0'); break;
            case '\n': break; // A line continuation.
            case '\r':
                if (_position < _end && _text[_position] == '\n')
                {
                    _position++;
                }

                break;
            case 'x': value.Append((char)ReadHex(2)); break;
            case 'u' when _position < _end && _text[_position] == '{':
                int close = _text.IndexOf('}', _position);
                if (close < 0 || close >= _end || !int.TryParse(_text.AsSpan(_position + 1, close - _position - 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code) || code is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
                {
                    throw new FormatException("A bad Unicode escape.");
                }

                value.Append(char.ConvertFromUtf32(code));
                _position = close + 1;
                break;
            case 'u': value.Append((char)ReadHex(4)); break;
            default: value.Append(c); break;
        }
    }

    private int ReadHex(int length)
    {
        if (_position + length > _end || !int.TryParse(_text.AsSpan(_position, length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
        {
            throw new FormatException("A bad escape.");
        }

        _position += length;
        return code;
    }

    private double ReadNumber()
    {
        int start = _position;
        while (_position < _end && (char.IsAsciiLetterOrDigit(_text[_position]) || _text[_position] is '-' or '+' or '.'))
        {
            _position++;
        }

        return double.TryParse(_text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? number
            : throw new FormatException("A bad number at " + start.ToString(CultureInfo.InvariantCulture) + ".");
    }

    private object? ReadWord(int depth)
    {
        int start = _position;
        while (_position < _end && (char.IsLetterOrDigit(_text[_position]) || _text[_position] is '_' or '$'))
        {
            _position++;
        }

        var word = _text[start.._position];
        switch (word)
        {
            case "":
                throw new FormatException("Unexpected text at " + start.ToString(CultureInfo.InvariantCulture) + ".");
            case "true":
                return true;
            case "false":
                return false;
            case "null" or "undefined":
                return null;
            case "void":
                SkipVoidOperand(depth);
                return null;
            default:
                // A name nothing binds reads as undefined, as a lenient reader of the page would treat it.
                return _names.GetValueOrDefault(word);
        }
    }

    /// <summary>The operand of <c>void 0</c> or <c>void(0)</c>.</summary>
    private void SkipVoidOperand(int depth)
    {
        SkipSpaceAnd("");
        Require();
        if (_text[_position] == '(')
        {
            _position = FindClosing(_text, _position, '(', ')') + 1;
        }
        else
        {
            ReadValue(depth + 1);
        }
    }

    private void SkipSpaceAnd(string separators)
    {
        while (_position < _end && (char.IsWhiteSpace(_text[_position]) || separators.Contains(_text[_position], StringComparison.Ordinal)))
        {
            _position++;
        }
    }

    private void Require()
    {
        if (_position >= _end)
        {
            throw new FormatException("Ran out of text.");
        }
    }

    [GeneratedRegex(@"\G\(function\s*\(([^)]*)\)\s*\{\s*return\s*\{")]
    private static partial Regex Call();
}
