using System.Globalization;
using System.Text;

namespace NeonSidekick.Skills;

/// <summary>
/// The head of a <c>SKILL.md</c>: the YAML between the two <c>---</c> fences, read by hand (no
/// YAML package: the app is NativeAOT and the format is a handful of <c>key: value</c> lines). The
/// Agent Skills specification names <c>name</c> and <c>description</c> as required and
/// <c>license</c>, <c>compatibility</c>, <c>metadata</c> (a map) and <c>allowed-tools</c> as
/// optional; this reader keeps the two it uses and carries every other line through verbatim
/// (<see cref="OtherLines"/>), so <see cref="Write"/> can rewrite a skill without losing them.
///
/// <para>Lenient the way the specification's integration guide asks: a plain, single-quoted,
/// double-quoted or block (<c>|</c> / <c>&gt;</c>) scalar; an unquoted value with a colon in it
/// (<c>description: Use when: the user asks…</c>) is taken whole; comment lines and blank lines
/// are skipped; an indented map under an unknown key (<c>metadata:</c>) is kept as its lines. A
/// missing fence or a line that is no <c>key: value</c> pair is a problem, as is a missing name or
/// description — the caller skips the skill and says why.</para>
/// </summary>
public sealed record SkillFrontmatter(string Name, string Description, IReadOnlyList<string> OtherLines)
{
    /// <summary>The fence line.</summary>
    public const string Fence = "---";

    public const string NameKey = "name";
    public const string DescriptionKey = "description";

    /// <summary>The specification's caps.</summary>
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;

    /// <summary>The problems, pinned: the caller shows them beside the folder.</summary>
    public const string NoFenceProblem = "the file does not start with a --- line";
    public const string NoClosingFenceProblem = "the frontmatter has no closing --- line";
    public const string NoNameProblem = "the frontmatter has no name";
    public const string NoDescriptionProblem = "the frontmatter has no description";

    /// <summary>The problem for a frontmatter line that is no <c>key: value</c> pair. Pinned.</summary>
    public static string NotAPairProblem(int line) => "frontmatter line " + line.ToString(CultureInfo.InvariantCulture) + " is not a key: value pair";

    /// <summary>The specification's rule, in the words the tool answers with. Pinned.</summary>
    public const string NameRule = "1 to 64 lowercase letters, digits and hyphens, not starting or ending with a hyphen and with no two hyphens in a row";

    /// <summary>Whether <paramref name="name"/> is a valid skill name: <see cref="NameRule"/>.</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name[0] == '-' || name[^1] == '-' || name.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A description as the catalog carries it: whitespace runs folded to one space, trimmed. Pure.</summary>
    public static string Flatten(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }

            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads <paramref name="text"/> (a whole <c>SKILL.md</c>). True with the frontmatter and the
    /// body (everything after the closing fence, CRLF folded, trimmed); false with the problem.
    /// The name and description come back trimmed, the description flattened; neither is validated
    /// against the caps here — the catalog warns and loads, the specification's lenient path.
    /// </summary>
    public static bool TryParse(string text, out SkillFrontmatter? frontmatter, out string body, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(text);
        frontmatter = null;
        body = "";
        problem = null;

        string folded = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (folded.Length > 0 && folded[0] == '﻿')
        {
            folded = folded[1..];
        }

        string[] lines = folded.Split('\n');
        if (lines.Length == 0 || !IsFence(lines[0]))
        {
            problem = NoFenceProblem;
            return false;
        }

        int close = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (IsFence(lines[i]) || lines[i].TrimEnd() == "...")
            {
                close = i;
                break;
            }
        }

        if (close < 0)
        {
            problem = NoClosingFenceProblem;
            return false;
        }

        string? name = null;
        string? description = null;
        var other = new List<string>();
        int index = 1;
        while (index < close)
        {
            string line = lines[index];
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                index++;
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                // An indented line with no key above it to belong to: not a pair.
                problem = NotAPairProblem(index + 1);
                return false;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                problem = NotAPairProblem(index + 1);
                return false;
            }

            string key = line[..colon].Trim();
            string rest = line[(colon + 1)..];
            if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
            {
                // "http://x" style: a colon with no space after it is part of a plain value only
                // when a key precedes it; a key with no space after its colon is not a pair.
                problem = NotAPairProblem(index + 1);
                return false;
            }

            int start = index;
            string value = ReadScalar(lines, ref index, close, rest);
            bool known = key.Equals(NameKey, StringComparison.OrdinalIgnoreCase) || key.Equals(DescriptionKey, StringComparison.OrdinalIgnoreCase);
            if (!known)
            {
                for (int i = start; i < index; i++)
                {
                    other.Add(lines[i]);
                }

                continue;
            }

            if (key.Equals(NameKey, StringComparison.OrdinalIgnoreCase))
            {
                name = value.Trim();
            }
            else
            {
                description = Flatten(value);
            }
        }

        if (string.IsNullOrEmpty(name))
        {
            problem = NoNameProblem;
            return false;
        }

        if (string.IsNullOrEmpty(description))
        {
            problem = NoDescriptionProblem;
            return false;
        }

        var sb = new StringBuilder();
        for (int i = close + 1; i < lines.Length; i++)
        {
            if (i > close + 1)
            {
                sb.Append('\n');
            }

            sb.Append(lines[i]);
        }

        frontmatter = new SkillFrontmatter(name, description, other);
        body = sb.ToString().Trim();
        return true;
    }

    /// <summary>
    /// The scalar for a key: the rest of its line, or the indented lines that follow a block
    /// indicator (<c>|</c>, <c>&gt;</c>, with <c>-</c> / <c>+</c>) or an empty value (a nested
    /// map — kept as text, the caller ignores it). <paramref name="index"/> lands on the next key.
    /// </summary>
    private static string ReadScalar(string[] lines, ref int index, int close, string rest)
    {
        string inline = rest.Trim();
        bool block = inline is "|" or ">" or "|-" or ">-" or "|+" or ">+";
        if (!block && inline.Length > 0)
        {
            index++;
            return Unquote(inline);
        }

        bool literal = inline.Length > 0 && inline[0] == '|';
        var sb = new StringBuilder();
        index++;
        while (index < close && (lines[index].Length == 0 || char.IsWhiteSpace(lines[index][0])))
        {
            string continuation = lines[index].Trim();
            if (sb.Length > 0)
            {
                sb.Append(literal ? '\n' : ' ');
            }

            sb.Append(continuation);
            index++;
        }

        return sb.ToString();
    }

    /// <summary>A quoted scalar without its quotes (the two YAML escapes that matter, <c>\"</c> and <c>\\</c>, and <c>''</c>); a plain one without a trailing comment.</summary>
    internal static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        int comment = value.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? value[..comment].TrimEnd() : value;
    }

    private static bool IsFence(string line) => line.TrimEnd() == Fence;

    /// <summary>
    /// A whole <c>SKILL.md</c>: the fences around <c>name</c>, <c>description</c> and the other
    /// lines, a blank line, the body, a final newline. The description is written plain when YAML
    /// reads it back as the same text, double-quoted otherwise (<see cref="NeedsQuotes"/>). Pure.
    /// </summary>
    public static string Write(string name, string description, IReadOnlyList<string> otherLines, string body)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(otherLines);
        ArgumentNullException.ThrowIfNull(body);
        var sb = new StringBuilder();
        sb.Append(Fence).Append('\n');
        sb.Append(NameKey).Append(": ").Append(name).Append('\n');
        sb.Append(DescriptionKey).Append(": ").Append(Scalar(Flatten(description))).Append('\n');
        foreach (var line in otherLines)
        {
            sb.Append(line).Append('\n');
        }

        sb.Append(Fence).Append('\n').Append('\n');
        sb.Append(body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>The scalar as <see cref="Write"/> emits it: plain, or double-quoted with <c>\</c> and <c>"</c> escaped.</summary>
    public static string Scalar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!NeedsQuotes(value))
        {
            return value;
        }

        // Double-quoted, its escapes YAML's own: a control character written as one (2026-10-22, review T10: a tab or a line break inside
        // went in raw).
        var sb = new System.Text.StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\t' => "\\t",
                '\n' => "\\n",
                '\r' => "\\r",
                _ when char.IsControl(c) => "\\x" + ((int)c).ToString("X2", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Whether a plain scalar would read back differently: empty; a leading YAML indicator
    /// character; a <c>: </c> or <c> #</c> inside; a trailing colon; a word YAML types as a
    /// boolean, null or number.
    /// </summary>
    public static bool NeedsQuotes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || "\"'#&*!|>%@`[]{},?-:".Contains(value[0]) || value[^1] == ':')
        {
            return true;
        }

        // Spaces at either end, a tab before '#', a control character (2026-10-22, review T10: " draft " read back as "draft", and
        // "done\t#later" as "done", both reported set).
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]) || value.Any(char.IsControl))
        {
            return true;
        }

        if (value.Contains(": ", StringComparison.Ordinal) || value.Contains(" #", StringComparison.Ordinal) || value.Contains(":\t", StringComparison.Ordinal))
        {
            return true;
        }

        return value.ToLowerInvariant() is "true" or "false" or "yes" or "no" or "on" or "off" or "null" or "~"
            || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }
}
