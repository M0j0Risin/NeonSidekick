using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// Reads tool arguments by hand out of whatever the adapter delivers: a <see cref="JsonElement"/>
/// from the wire, or a CLR value from a test. Every tool goes through here so the shapes are
/// handled once; nothing here throws for a model's mistake.
/// </summary>
public static class ToolArguments
{
    /// <summary>The string argument, or empty when missing or null. A non-string JSON value is its raw text.</summary>
    public static string ReadString(AIFunctionArguments arguments, string name)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return "";
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
            JsonElement { ValueKind: JsonValueKind.Null } => "",
            JsonElement element => element.GetRawText(),
            string s => s,
            _ => value.ToString() ?? "",
        };
    }

    /// <summary>
    /// A whole-number argument: null when missing or JSON null, the value when it is an integer
    /// (a JSON number, a numeric string, or a CLR integer), false when it is anything else —
    /// <paramref name="raw"/> then holds what was sent, for the error sentence.
    /// </summary>
    public static bool TryReadInt32(AIFunctionArguments arguments, string name, out int? value, out string raw)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        value = null;
        raw = "";
        if (!arguments.TryGetValue(name, out var sent) || sent is null)
        {
            return true;
        }

        switch (sent)
        {
            case JsonElement { ValueKind: JsonValueKind.Null }:
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                raw = element.GetRawText();
                if (element.TryGetInt32(out int number))
                {
                    value = number;
                    return true;
                }

                return false;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                raw = element.GetString() ?? "";
                break;
            case JsonElement element:
                raw = element.GetRawText();
                return false;
            case int i:
                value = i;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                value = (int)l;
                return true;
            default:
                raw = sent.ToString() ?? "";
                break;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            // An empty string is how some models leave an optional argument out.
            return true;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A whole-number argument too wide for <see cref="TryReadInt32"/> (a ComfyUI seed, 2026-09-24): the same shapes
    /// and the same rules — null when missing, JSON null or blank; false for anything else, <paramref name="raw"/>
    /// holding what was sent.
    /// </summary>
    public static bool TryReadInt64(AIFunctionArguments arguments, string name, out long? value, out string raw)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        value = null;
        raw = "";
        if (!arguments.TryGetValue(name, out var sent) || sent is null)
        {
            return true;
        }

        switch (sent)
        {
            case JsonElement { ValueKind: JsonValueKind.Null }:
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                raw = element.GetRawText();
                if (element.TryGetInt64(out long number))
                {
                    value = number;
                    return true;
                }

                return false;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                raw = element.GetString() ?? "";
                break;
            case JsonElement element:
                raw = element.GetRawText();
                return false;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            default:
                raw = sent.ToString() ?? "";
                break;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (long.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A number argument that may have a fraction (ComfyUI's <c>cfg</c> and <c>denoise</c>, 2026-09-24): null when
    /// missing, JSON null or blank; the value for a JSON number, a numeric string (invariant culture) or a CLR number;
    /// false for anything else or a value that is not finite, <paramref name="raw"/> holding what was sent.
    /// </summary>
    public static bool TryReadDouble(AIFunctionArguments arguments, string name, out double? value, out string raw)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        value = null;
        raw = "";
        if (!arguments.TryGetValue(name, out var sent) || sent is null)
        {
            return true;
        }

        switch (sent)
        {
            case JsonElement { ValueKind: JsonValueKind.Null }:
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                raw = element.GetRawText();
                if (element.TryGetDouble(out double number) && double.IsFinite(number))
                {
                    value = number;
                    return true;
                }

                return false;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                raw = element.GetString() ?? "";
                break;
            case JsonElement element:
                raw = element.GetRawText();
                return false;
            case double d when double.IsFinite(d):
                value = d;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            default:
                raw = Convert.ToString(sent, CultureInfo.InvariantCulture) ?? "";
                break;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A yes/no argument: null when missing, JSON null or blank; the value for a JSON boolean, a
    /// CLR bool or the words <c>true</c> / <c>false</c> in any case; false for anything else, with
    /// <paramref name="raw"/> holding what was sent.
    /// </summary>
    public static bool TryReadBoolean(AIFunctionArguments arguments, string name, out bool? value, out string raw)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        value = null;
        raw = "";
        if (!arguments.TryGetValue(name, out var sent) || sent is null)
        {
            return true;
        }

        switch (sent)
        {
            case JsonElement { ValueKind: JsonValueKind.Null }:
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                value = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                value = false;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                raw = element.GetString() ?? "";
                break;
            case JsonElement element:
                raw = element.GetRawText();
                return false;
            case bool b:
                value = b;
                return true;
            default:
                raw = sent.ToString() ?? "";
                break;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (bool.TryParse(raw.Trim(), out bool parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A list-of-strings argument (<c>view_image</c>'s <c>paths</c>): empty when missing, JSON null
    /// or an empty array; the strings of a JSON array of strings or a CLR sequence of strings; a
    /// single string counts as a list of one (a model that sends <c>"a.png"</c> for <c>paths</c>
    /// meant that). False for anything else — an array holding a number, an object — with
    /// <paramref name="raw"/> holding what was sent, for the error sentence.
    /// </summary>
    public static bool TryReadStringList(AIFunctionArguments arguments, string name, out IReadOnlyList<string> values, out string raw)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var found = new List<string>();
        values = found;
        raw = "";
        if (!arguments.TryGetValue(name, out var sent) || sent is null)
        {
            return true;
        }

        switch (sent)
        {
            case JsonElement { ValueKind: JsonValueKind.Null }:
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                if (!string.IsNullOrWhiteSpace(element.GetString()))
                {
                    found.Add(element.GetString()!);
                }

                return true;
            case JsonElement { ValueKind: JsonValueKind.Array } element:
                raw = element.GetRawText();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        found.Clear();
                        return false;
                    }

                    found.Add(item.GetString() ?? "");
                }

                return true;
            case JsonElement element:
                raw = element.GetRawText();
                return false;
            case string s:
                if (!string.IsNullOrWhiteSpace(s))
                {
                    found.Add(s);
                }

                return true;
            case IEnumerable<string> list:
                found.AddRange(list);
                return true;
            default:
                raw = sent.ToString() ?? "";
                return false;
        }
    }

    /// <summary>
    /// A list-of-objects argument (<c>ask_user</c>'s <c>questions</c>): empty when missing, JSON
    /// null or an empty array; the elements of a JSON array whose items are all objects (cloned,
    /// so they outlive the document); a lone object as a list of one (a model that sent one
    /// question bare); a string that parses to either — JSON with a trailing comma or a comment
    /// allowed, else a Python-style literal through <see cref="LooseJson.Normalize"/> (single
    /// quotes, <c>True</c>, a raw line break in a string: what a server's tool parser hands over
    /// as text when its own parse failed); a CLR sequence of <see cref="JsonElement"/>. False for
    /// anything else — an array holding a string or a number, a string that is no list — with
    /// <paramref name="raw"/> holding what was sent, for the error sentence.
    /// </summary>
    public static bool TryReadObjectList(AIFunctionArguments arguments, string name, out IReadOnlyList<JsonElement> objects, out string raw)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var found = new List<JsonElement>();
        objects = found;
        raw = "";
        if (!arguments.TryGetValue(name, out var sent) || sent is null)
        {
            return true;
        }

        switch (sent)
        {
            case JsonElement { ValueKind: JsonValueKind.Null }:
                return true;
            case JsonElement { ValueKind: JsonValueKind.Array or JsonValueKind.Object } element:
                raw = element.GetRawText();
                return TakeListOrObject(element, found);
            case JsonElement { ValueKind: JsonValueKind.String } element:
                raw = element.GetString() ?? "";
                return TakeEncoded(raw, found);
            case JsonElement element:
                raw = element.GetRawText();
                return false;
            case string s:
                raw = s;
                return TakeEncoded(s, found);
            case IEnumerable<JsonElement> list:
                found.AddRange(list);
                return found.TrueForAll(e => e.ValueKind == JsonValueKind.Object) || Refuse(found);
            default:
                raw = sent.ToString() ?? "";
                return false;
        }

        static bool TakeListOrObject(JsonElement element, List<JsonElement> into)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                into.Add(element.Clone());
                return true;
            }

            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    into.Clear();
                    return false;
                }

                into.Add(item.Clone());
            }

            return true;
        }

        static bool TakeEncoded(string text, List<JsonElement> into)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            using var document = LooseJson.TryParse(text);
            return document is not null
                && document.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object
                && TakeListOrObject(document.RootElement, into);
        }

        static bool Refuse(List<JsonElement> found)
        {
            found.Clear();
            return false;
        }
    }
}

/// <summary>
/// A tolerant read of a value a model wrote by hand (2026-09-15, after Qwen3 on SGLang answered
/// every first <c>ask_user</c> with the "not a list" error: the server's tool parser JSON-parses a
/// parameter by its schema type and, when that fails, hands the text through as a string).
/// <see cref="TryParse"/> takes JSON with a trailing comma or a comment, then the same text through
/// <see cref="Normalize"/> — a Python-style literal made JSON: a single-quoted string becomes
/// double-quoted (<c>\'</c> inside it reads as the quote, a <c>"</c> inside it is escaped), a
/// double-quoted string is copied as it is, bare <c>True</c> / <c>False</c> / <c>None</c> become
/// <c>true</c> / <c>false</c> / <c>null</c>, and a raw line break inside a string becomes <c>\n</c>.
/// Then, when the text still does not parse, the same two over <see cref="FirstValue"/>: the text
/// cut at the end of its first complete <c>[…]</c> / <c>{…}</c> (2026-09-16, after SGLang handed
/// <c>ask_user</c> its list as a string ending <c>}]}</c> — the outer object's brace sliced in with
/// the value, so a well-formed list was refused for the one character after it). Pure; never throws.
/// </summary>
public static class LooseJson
{
    private static readonly JsonDocumentOptions Tolerant = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>The text as a document — as sent, normalised, or either over its first complete value — or null when nothing parses. The caller disposes it.</summary>
    public static JsonDocument? TryParse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if ((Parse(text) ?? Parse(Normalize(text))) is { } whole)
        {
            return whole;
        }

        string? first = FirstValue(text);
        return first is null ? null : Parse(first) ?? Parse(Normalize(first));

        static JsonDocument? Parse(string candidate)
        {
            try
            {
                return JsonDocument.Parse(candidate, Tolerant);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>A Python-style literal as JSON text (see the class note); text that is JSON already comes back unchanged.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new System.Text.StringBuilder(text.Length + 8);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"' || c == '\'')
            {
                i = CopyString(text, i, sb);
                continue;
            }

            if (char.IsLetter(c))
            {
                int start = i;
                while (i < text.Length && char.IsLetterOrDigit(text[i]))
                {
                    i++;
                }

                sb.Append(text.AsSpan(start, i - start) switch
                {
                    "True" => "true",
                    "False" => "false",
                    "None" => "null",
                    var word => word.ToString(),
                });
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// The text from its first <c>[</c> or <c>{</c> through the bracket that closes it — strings of
    /// either quote style skipped, so a bracket inside one does not count — or null when there is no
    /// such value, it never closes, or the cut would be the text itself (whole already, refused for
    /// another reason). What is cut off is discarded: a word ahead, a stray closing brace, a second
    /// value, a trailing remark.
    /// </summary>
    public static string? FirstValue(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int start = text.AsSpan().IndexOfAny('[', '{');
        if (start < 0)
        {
            return null;
        }

        int depth = 0;
        int i = start;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"' || c == '\'')
            {
                i = SkipString(text, i);
                continue;
            }

            if (c == '[' || c == '{')
            {
                depth++;
            }
            else if (c == ']' || c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return start == 0 && text.AsSpan(i + 1).IsWhiteSpace() ? null : text.Substring(start, i + 1 - start);
                }
            }

            i++;
        }

        return null;
    }

    /// <summary>The index after the string starting at <paramref name="at"/> (its quote style, backslash escapes stepped over), or the text's end when unterminated.</summary>
    private static int SkipString(string text, int at)
    {
        char quote = text[at];
        int i = at + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
            {
                return i + 1;
            }

            i++;
        }

        return i;
    }

    /// <summary>Copies the string starting at <paramref name="at"/> as a double-quoted JSON string; returns the index after its closing quote (or the text's end when unterminated).</summary>
    private static int CopyString(string text, int at, System.Text.StringBuilder sb)
    {
        char quote = text[at];
        sb.Append('"');
        int i = at + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                if (quote == '\'' && next == '\'')
                {
                    sb.Append('\'');
                }
                else
                {
                    sb.Append(c).Append(next);
                }

                i += 2;
                continue;
            }

            if (c == quote)
            {
                sb.Append('"');
                return i + 1;
            }

            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\r':
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                default:
                    sb.Append(c);
                    break;
            }

            i++;
        }

        sb.Append('"');
        return i;
    }
}
