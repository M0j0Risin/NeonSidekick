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
    public static bool TryParse(string text, out SkillFrontmatter? frontmatter, out string body, out string? problem) =>
        Parse(text, null, out frontmatter, out body, out problem);

    /// <summary>
    /// Reads <paramref name="text"/> by Claude Code's rules for a <c>.claude/skills</c> skill (2026-11-23, feature idea #16; code.claude.com's skills page,
    /// checked 2026-11-22: "all fields are optional"): a missing <c>name</c> is <paramref name="folderName"/>, a missing <c>description</c> the body's
    /// first non-empty line, the frontmatter itself may be absent, and <c>when_to_use</c> is joined to the description with a space (its listing
    /// shows "the combined description and when_to_use text"). Every other key, the acting ones included (<see cref="ActingKeys"/>), is kept as
    /// <see cref="OtherLines"/> and grants nothing here. False only for an unclosed fence, a line that is no pair, or a file with no words at all.
    /// </summary>
    public static bool TryParseClaude(string text, string folderName, out SkillFrontmatter? frontmatter, out string body, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(folderName);
        return Parse(text, folderName, out frontmatter, out body, out problem);
    }

    /// <summary>Claude Code's key whose text joins the description (<see cref="TryParseClaude"/>).</summary>
    public const string WhenToUseKey = "when_to_use";

    /// <summary>Claude Code's key for a skill only you start (<c>disable-model-invocation: true</c>): such a project skill is never offered here.</summary>
    public const string ManualOnlyKey = "disable-model-invocation";

    /// <summary>
    /// Claude Code's keys that act (tools used "without asking permission", hooks, a model, a forked context, a shell; the skills page, checked
    /// 2026-11-22), and the Agent Skills standard's <c>allowed-tools</c>: read as unknown keys and ignored, so a project skill never skips a question,
    /// adds a hook, runs a command or switches a model. A trust question marks a skill holding any of them. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> ActingKeys = ["allowed-tools", "disallowed-tools", "model", "effort", "context", "agent", "background", "hooks", "shell"];

    /// <summary>The top-level keys of <paramref name="lines"/> (<see cref="OtherLines"/>): each column-0 <c>key:</c>, lower-cased. Pure.</summary>
    public static IReadOnlyList<string> KeysOf(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var keys = new List<string>();
        foreach (string line in lines)
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#' && line[0] != '-' && colon > 0)
            {
                keys.Add(line[..colon].Trim().ToLowerInvariant());
            }
        }

        return keys;
    }

    /// <summary>Whether this frontmatter says only the user starts the skill (<see cref="ManualOnlyKey"/> set to <c>true</c>).</summary>
    public bool ManualOnly => OtherLines.Any(line =>
        line.IndexOf(':', StringComparison.Ordinal) is var colon and > 0
        && line[..colon].Trim().Equals(ManualOnlyKey, StringComparison.OrdinalIgnoreCase)
        && line[(colon + 1)..].Trim().Trim('"', '\'').Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether this frontmatter holds any of <see cref="ActingKeys"/> (the trust question's mark).</summary>
    public bool HasActingKeys => KeysOf(OtherLines).Any(key => ActingKeys.Contains(key, StringComparer.Ordinal));

    private static bool Parse(string text, string? claudeFolder, out SkillFrontmatter? frontmatter, out string body, out string? problem)
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
            if (claudeFolder is not null)
            {
                // No frontmatter at all (Claude Code's rules): the folder's name, the first line's words, the whole file the body.
                body = folded.Trim();
                return Defaults(claudeFolder, null, null, null, [], body, out frontmatter, out problem);
            }

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

        if (!TryReadBlock(lines, close, out var entries, out problem))
        {
            return false;
        }

        string? name = null;
        string? description = null;
        string? whenToUse = null;
        var other = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Key.Equals(NameKey, StringComparison.OrdinalIgnoreCase))
            {
                name = entry.Value.Trim();
            }
            else if (entry.Key.Equals(DescriptionKey, StringComparison.OrdinalIgnoreCase))
            {
                description = Flatten(entry.Value);
            }
            else if (claudeFolder is not null && entry.Key.Equals(WhenToUseKey, StringComparison.OrdinalIgnoreCase))
            {
                whenToUse = Flatten(entry.Value);
            }
            else
            {
                for (int i = entry.Start; i < entry.End; i++)
                {
                    other.Add(lines[i]);
                }
            }
        }

        if (claudeFolder is null && string.IsNullOrEmpty(name))
        {
            problem = NoNameProblem;
            return false;
        }

        if (claudeFolder is null && string.IsNullOrEmpty(description))
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

        body = sb.ToString().Trim();
        if (claudeFolder is not null)
        {
            return Defaults(claudeFolder, name, description, whenToUse, other, body, out frontmatter, out problem);
        }

        frontmatter = new SkillFrontmatter(name!, description!, other);
        return true;
    }

    // Claude Code's defaults (TryParseClaude): the folder's name, the body's first non-empty line, when_to_use joined on.
    private static bool Defaults(string folder, string? name, string? description, string? whenToUse, List<string> other, string body, out SkillFrontmatter? frontmatter, out string? problem)
    {
        frontmatter = null;
        problem = null;
        string named = string.IsNullOrEmpty(name) ? folder : name;
        string described = string.IsNullOrEmpty(description)
            ? Flatten(body.Split('\n').Select(line => line.Trim().TrimStart('#').Trim()).FirstOrDefault(line => line.Length > 0) ?? "")
            : description;
        if (!string.IsNullOrEmpty(whenToUse))
        {
            described = described.Length == 0 ? whenToUse : described + " " + whenToUse;
        }

        if (described.Length == 0)
        {
            problem = NoDescriptionProblem;
            return false;
        }

        frontmatter = new SkillFrontmatter(named, described, other);
        return true;
    }

    /// <summary>
    /// One key of a frontmatter block (2026-10-30, the custom commands plan: the key/value reader shared by <c>SKILL.md</c> and a command file):
    /// its key, its scalar (<see cref="ReadScalar"/>), the <c>- item</c> lines under it when it has an empty value (null when none), and the lines
    /// it took, <paramref name="Start"/> up to <paramref name="End"/> (exclusive).
    /// </summary>
    internal sealed record Entry(string Key, string Value, IReadOnlyList<string>? Items, int Start, int End);

    /// <summary>
    /// The <c>key: value</c> lines between line 1 and <paramref name="close"/> (the closing fence's index), comment and blank lines skipped.
    /// False with the problem for a line that is no pair (<see cref="NotAPairProblem"/>). Lifted out of <see cref="TryParse"/> on 2026-10-30
    /// so a custom command's frontmatter reads by the same rules.
    /// </summary>
    internal static bool TryReadBlock(string[] lines, int close, out IReadOnlyList<Entry> entries, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var found = new List<Entry>();
        entries = found;
        problem = null;
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
            string value = ReadScalar(lines, ref index, close, key, rest, out var items);
            found.Add(new Entry(key, value, items, start, index));
        }

        return true;
    }

    /// <summary>
    /// The scalar for a key: the rest of its line, or the indented lines that follow a block
    /// indicator (<c>|</c>, <c>&gt;</c>, with <c>-</c> / <c>+</c>) or an empty value (a nested
    /// map — kept as text, the caller ignores it). <paramref name="index"/> lands on the next key.
    /// Under an empty value the <c>- item</c> lines are read too, at column 0 or indented (both are YAML; Claude Code's command files
    /// write <c>allowed-tools:</c> lists at column 0, 2026-10-30), each item into <paramref name="items"/>. A column-0 list is read only under
    /// a key other than <c>name</c> and <c>description</c> (2026-10-31, review P4-2: <c>description:</c> then <c>- a</c> loaded as a
    /// description of "- a"), where the old <see cref="NotAPairProblem"/> stands again, for <c>SKILL.md</c> and command files alike.
    /// </summary>
    private static string ReadScalar(string[] lines, ref int index, int close, string key, string rest, out List<string>? items)
    {
        items = null;
        bool columnZeroItems = !key.Equals(NameKey, StringComparison.OrdinalIgnoreCase) && !key.Equals(DescriptionKey, StringComparison.OrdinalIgnoreCase);
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
        while (index < close && (lines[index].Length == 0 || char.IsWhiteSpace(lines[index][0]) || (!block && columnZeroItems && IsSequenceItem(lines[index]))))
        {
            string continuation = lines[index].Trim();
            if (!block && IsSequenceItem(continuation))
            {
                (items ??= []).Add(Unquote(continuation[1..].Trim()));
            }

            if (sb.Length > 0)
            {
                sb.Append(literal ? '\n' : ' ');
            }

            sb.Append(continuation);
            index++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> as a name in kebab case (2026-10-31, review P7-6: the Skills pane's and the custom commands' rules were two
    /// copies): lower case, a run of anything else one hyphen, none at either end; an underscore kept with <paramref name="keepUnderscore"/>
    /// (never first). Empty when nothing survives. No length cap: each caller has its own. Pure.
    /// </summary>
    internal static string Kebab(string text, bool keepUnderscore)
    {
        ArgumentNullException.ThrowIfNull(text);
        var kebab = new StringBuilder(text.Length);
        bool gap = false;
        foreach (char c in text.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' || (keepUnderscore && c == '_'))
            {
                if (c == '_' && kebab.Length == 0)
                {
                    continue;
                }

                if (gap && kebab.Length > 0)
                {
                    kebab.Append('-');
                }

                gap = false;
                kebab.Append(c);
            }
            else
            {
                gap = true;
            }
        }

        return kebab.ToString();
    }

    // A YAML block-sequence line: a hyphen, then a space or nothing.
    private static bool IsSequenceItem(string line) => line.Length > 0 && line[0] == '-' && (line.Length == 1 || char.IsWhiteSpace(line[1]));

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
