using System.Globalization;
using System.Text;
using System.Text.Json;
using NeonSidekick.Skills;

namespace NeonSidekick.Obsidian;

/// <summary>
/// One top-level frontmatter key: its scalar (<paramref name="Value"/>) or its list
/// (<paramref name="Items"/>, from <c>[a, b]</c> or <c>- a</c> lines), neither for a nested map or
/// anything else this reader does not model (<paramref name="Opaque"/>: shown as its raw lines, kept
/// byte for byte). <paramref name="Start"/> and <paramref name="End"/> are the 0-based line range it
/// occupies in the note (end exclusive) — what a set or a remove replaces.
/// </summary>
public sealed record NoteProperty(string Key, string? Value, IReadOnlyList<string>? Items, bool Opaque, int Start, int End, IReadOnlyList<string> RawLines)
{
    /// <summary>The value as a list: the items, a scalar as one (empty when blank), nothing for an opaque one.</summary>
    public IReadOnlyList<string> Values => Items ?? (string.IsNullOrWhiteSpace(Value) ? [] : [Value!]);
}

/// <summary>A note's frontmatter: whether it has one, the 0-based line of its closing fence, and the keys in file order.</summary>
public sealed record NoteFrontmatter(bool Present, int Close, IReadOnlyList<NoteProperty> Properties)
{
    public static NoteFrontmatter None { get; } = new(false, -1, []);

    /// <summary>
    /// The key's entry: the last when it is given more than once (2026-10-22, review H12: the first was read and changed while YAML and
    /// Obsidian read the last, so a set did nothing visible and a remove said done with the key still there).
    /// </summary>
    public NoteProperty? Find(string key) =>
        Properties.LastOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every entry of the key, in file order (review H12).</summary>
    public IReadOnlyList<NoteProperty> FindAll(string key) =>
        Properties.Where(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
}

/// <summary>
/// Obsidian's "properties": the YAML frontmatter of a note, read and edited by hand (2026-09-22, the
/// Obsidian tools; no YAML package under NativeAOT, the <see cref="SkillFrontmatter"/> precedent, whose
/// <see cref="SkillFrontmatter.Unquote"/> and <see cref="SkillFrontmatter.Scalar"/> are shared). The
/// subset Obsidian's own property editor writes: <c>key: scalar</c>, <c>key: [a, b]</c>, <c>key:</c>
/// followed by <c>- item</c> lines, and block scalars; a nested map is carried as its raw lines.
///
/// <para>An edit replaces only the lines of the key it names (<see cref="Set"/>, <see cref="Remove"/>):
/// every other line — comments, odd spacing, keys this reader cannot model — comes back byte for byte,
/// so a note Obsidian or another plugin wrote is never reformatted. A new key goes before the closing
/// fence; a note without frontmatter gains one. Lists are written the way Obsidian writes them, one
/// <c>  - item</c> per line. Pure; never throws for a note's contents.</para>
/// </summary>
public static class NoteProperties
{
    public const string Fence = "---";
    public const string TagsKey = "tags";
    public const string AliasesKey = "aliases";

    /// <summary>The 0-based index of the first body line: past the closing fence, or 0 without frontmatter.</summary>
    public static int BodyStart(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        int close = CloseIndex(lines);
        return close < 0 ? 0 : close + 1;
    }

    /// <summary>The closing fence's index, or -1 when the note does not open with a fence or never closes it.</summary>
    private static int CloseIndex(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || lines[0].TrimStart('﻿').TrimEnd() != Fence)
        {
            return -1;
        }

        for (int i = 1; i < lines.Count; i++)
        {
            string line = lines[i].TrimEnd();
            if (line == Fence || line == "...")
            {
                return i;
            }
        }

        return -1;
    }

    public static NoteFrontmatter Parse(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        int close = CloseIndex(lines);
        if (close < 0)
        {
            return NoteFrontmatter.None;
        }

        var properties = new List<NoteProperty>();
        int index = 1;
        while (index < close)
        {
            string line = lines[index];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#') || char.IsWhiteSpace(line[0]) || line.StartsWith('-'))
            {
                index++;
                continue;
            }

            int colon = KeyColon(line);
            if (colon <= 0)
            {
                index++;
                continue;
            }

            string key = SkillFrontmatter.Unquote(line[..colon].Trim());
            string rest = WithoutComment(line[(colon + 1)..]).Trim();
            int end = index + 1;
            while (end < close && (lines[end].Trim().Length == 0 || char.IsWhiteSpace(lines[end][0]) || lines[end].StartsWith('-')))
            {
                end++;
            }

            while (end > index + 1 && lines[end - 1].Trim().Length == 0)
            {
                end--;
            }

            var block = new List<string>();
            for (int i = index + 1; i < end; i++)
            {
                block.Add(lines[i]);
            }

            var raw = new List<string>(end - index);
            for (int i = index; i < end; i++)
            {
                raw.Add(lines[i]);
            }

            properties.Add(Read(key, rest, block, index, end, raw));
            index = end;
        }

        return new NoteFrontmatter(true, close, properties);
    }

    /// <summary>
    /// A value's text without a YAML comment after it (2026-10-22, review R8: <c>tags: [project, work] # note</c> read as the tags
    /// <c>[project</c> and <c>work]</c>, and a quoted status kept its quotes): a <c>#</c> after a space or tab, outside quotes, ends it.
    /// </summary>
    public static string WithoutComment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        char quote = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"' && i + 1 < value.Length)
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'' && (i == 0 || value[i - 1] is ' ' or '\t' or '[' or ',' or '{'))
            {
                quote = c;
                continue;
            }

            // Not the value's own first character: "tags: #a, b" has always read as tags here, as Obsidian users write them.
            if (c == '#' && i > 0 && value[i - 1] is ' ' or '\t' && value[..i].Trim().Length > 0)
            {
                return value[..i].TrimEnd();
            }
        }

        return value;
    }

    /// <summary>The colon that ends a key (<c>key:</c> at the line's end or before a space), a quoted key's quotes stepped over; -1 when none.</summary>
    private static int KeyColon(string line)
    {
        int i = 0;
        if (line[0] is '"' or '\'')
        {
            int close = line.IndexOf(line[0], 1);
            i = close < 0 ? line.Length : close + 1;
        }

        for (; i < line.Length; i++)
        {
            if (line[i] == ':' && (i + 1 == line.Length || line[i + 1] == ' ' || line[i + 1] == '\t'))
            {
                return i;
            }
        }

        return -1;
    }

    private static NoteProperty Read(string key, string rest, List<string> block, int start, int end, List<string> raw)
    {
        var content = block.Where(l => l.Trim().Length > 0).ToList();
        if (rest.Length == 0)
        {
            if (content.Count == 0)
            {
                return new NoteProperty(key, "", null, false, start, end, raw);
            }

            if (content.TrueForAll(l => l.TrimStart().StartsWith('-')))
            {
                var items = content.Select(l => SkillFrontmatter.Unquote(WithoutComment(l.TrimStart()[1..]).Trim())).Where(s => s.Length > 0).ToList();
                return new NoteProperty(key, null, items, false, start, end, raw);
            }

            return new NoteProperty(key, null, null, true, start, end, raw);
        }

        if (rest.StartsWith('[') && rest.EndsWith(']'))
        {
            return new NoteProperty(key, null, InlineList(rest[1..^1]), false, start, end, raw);
        }

        if (rest is "|" or ">" or "|-" or ">-" or "|+" or ">+")
        {
            string joined = string.Join(rest[0] == '|' ? "\n" : " ", content.Select(l => l.Trim()));
            return new NoteProperty(key, joined, null, false, start, end, raw);
        }

        if (rest.StartsWith('{'))
        {
            return new NoteProperty(key, null, null, true, start, end, raw);
        }

        string value = SkillFrontmatter.Unquote(rest);
        if (content.Count > 0)
        {
            // A plain scalar folded over indented lines.
            value = string.Join(" ", new[] { value }.Concat(content.Select(l => l.Trim())));
        }

        return new NoteProperty(key, value, null, false, start, end, raw);
    }

    /// <summary><c>a, "b, c", d</c> as its items, quotes honoured.</summary>
    private static List<string> InlineList(string inner)
    {
        var items = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (char c in inner)
        {
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                sb.Append(c);
                continue;
            }

            if (c == ',')
            {
                Add();
                continue;
            }

            sb.Append(c);
        }

        Add();
        return items;

        void Add()
        {
            string item = SkillFrontmatter.Unquote(sb.ToString().Trim());
            if (item.Length > 0)
            {
                items.Add(item);
            }

            sb.Clear();
        }
    }

    /// <summary>
    /// The note's tags from its properties (<c>tags</c>, or the old <c>tag</c>): a list, or a scalar split
    /// on commas and spaces; a leading <c>#</c> dropped.
    /// </summary>
    public static IReadOnlyList<string> Tags(NoteFrontmatter front)
    {
        ArgumentNullException.ThrowIfNull(front);
        return Words(front.Find(TagsKey) ?? front.Find("tag"), splitSpaces: true).Select(t => t.TrimStart('#')).Where(t => t.Length > 0).ToList();
    }

    /// <summary>The note's aliases (<c>aliases</c>, or the old <c>alias</c>): a list, or a scalar split on commas.</summary>
    public static IReadOnlyList<string> Aliases(NoteFrontmatter front)
    {
        ArgumentNullException.ThrowIfNull(front);
        return Words(front.Find(AliasesKey) ?? front.Find("alias"), splitSpaces: false);
    }

    private static IReadOnlyList<string> Words(NoteProperty? property, bool splitSpaces)
    {
        if (property is null || property.Opaque)
        {
            return [];
        }

        if (property.Items is { } items)
        {
            return items;
        }

        char[] separators = splitSpaces ? [',', ' '] : [','];
        return (property.Value ?? "").Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>A property as one line for a tool result: <c>status: draft</c>, <c>tags: [a, b]</c>, an opaque one as its raw lines.</summary>
    public static string Describe(NoteProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.Opaque)
        {
            return string.Join("\n", property.RawLines);
        }

        if (property.Items is { } items)
        {
            return property.Key + ": [" + string.Join(", ", items) + "]";
        }

        return property.Key + ": " + property.Value;
    }

    /// <summary>
    /// The lines that write <paramref name="key"/> with <paramref name="value"/>: a string as a scalar
    /// (quoted when YAML would read it otherwise), a number or a boolean as written, null as an empty
    /// value, an array one <c>  - item</c> per line (<c>key: []</c> when empty). False for an object,
    /// or an array holding one: a nested map is not a property Obsidian can show.
    /// </summary>
    public static bool TryRender(string key, JsonElement value, out List<string> lines)
    {
        ArgumentNullException.ThrowIfNull(key);
        lines = [];
        // A key with a line break or another control character is refused (2026-10-22, review H11: one holding "\n---\n" closed the
        // frontmatter early and wrote the rest into the note's body).
        if (key.Any(char.IsControl) || key.Contains('\u2028') || key.Contains('\u2029'))
        {
            return false;
        }

        string head = SkillFrontmatter.NeedsQuotes(key) || key.Contains(':', StringComparison.Ordinal) ? SkillFrontmatter.Scalar(key) : key;
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                var items = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (Item(item) is not { } text)
                    {
                        return false;
                    }

                    items.Add(text);
                }

                if (items.Count == 0)
                {
                    lines.Add(head + ": []");
                    return true;
                }

                lines.Add(head + ":");
                lines.AddRange(items.Select(i => "  - " + i));
                return true;
            case JsonValueKind.Object:
            case JsonValueKind.Undefined:
                return false;
            default:
                string? scalar = Item(value);
                lines.Add(scalar is null || scalar.Length == 0 ? head + ":" : head + ": " + scalar);
                return scalar is not null;
        }

        static string? Item(JsonElement item) => item.ValueKind switch
        {
            JsonValueKind.String => item.GetString() is { Length: > 0 } s ? SkillFrontmatter.Scalar(s.ReplaceLineEndings(" ")) : "\"\"",
            JsonValueKind.Number => item.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => null,
        };
    }

    /// <summary>
    /// The note's lines with <paramref name="key"/> set to <paramref name="rendered"/> (from
    /// <see cref="TryRender"/>): its own lines replaced in place, or the lines added before the closing
    /// fence, or a new frontmatter at the top. Every other line as it was.
    /// </summary>
    public static List<string> Set(IReadOnlyList<string> lines, string key, IReadOnlyList<string> rendered)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(rendered);
        var front = Parse(lines);
        var result = new List<string>(lines.Count + rendered.Count + 2);
        if (!front.Present)
        {
            result.Add(Fence);
            result.AddRange(rendered);
            result.Add(Fence);
            result.AddRange(lines);
            return result;
        }

        // Every entry of a key given twice goes, the new lines where the last stood (review H12).
        var all = front.FindAll(key);
        if (all.Count > 0)
        {
            var last = all[^1];
            for (int i = 0; i < lines.Count; i++)
            {
                if (i == last.Start)
                {
                    result.AddRange(rendered);
                }

                if (!all.Any(p => i >= p.Start && i < p.End))
                {
                    result.Add(lines[i]);
                }
            }

            return result;
        }

        result.AddRange(lines.Take(front.Close));
        result.AddRange(rendered);
        result.AddRange(lines.Skip(front.Close));
        return result;
    }

    /// <summary>The note's lines less <paramref name="key"/>'s lines; the same lines when it is not there. An emptied frontmatter stays (two fences), as Obsidian leaves it.</summary>
    public static List<string> Remove(IReadOnlyList<string> lines, string key, out bool removed)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(key);
        var front = Parse(lines);
        removed = false;
        var all = front.FindAll(key);
        if (all.Count == 0)
        {
            return [.. lines];
        }

        // Every entry of the key (review H12).
        removed = true;
        return lines.Where((_, i) => !all.Any(p => i >= p.Start && i < p.End)).ToList();
    }

    /// <summary>A value for a search's <c>value</c> filter: whether any of the property's values equals <paramref name="wanted"/>, ignoring case (a number compared as written).</summary>
    public static bool Matches(NoteProperty property, string wanted)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(wanted);
        string w = wanted.Trim();
        return property.Values.Any(v => string.Equals(v.Trim(), w, StringComparison.OrdinalIgnoreCase)
            || (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && a == b));
    }
}
