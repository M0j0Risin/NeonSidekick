using NeonSidekick.Skills;

namespace NeonSidekick.CustomCommands;

/// <summary>
/// A command file read (<see cref="CustomCommandFile.Read"/>): the four keys the app reads, the body, and the lenient read's note when the
/// frontmatter didn't read. <paramref name="Description"/> is the frontmatter's alone (null when none; the display falls back to the body).
/// </summary>
public sealed record CustomCommandFileText(
    string? Description,
    string? ArgumentHint,
    IReadOnlyList<string> Arguments,
    bool UserInvocable,
    string? FrontmatterWarning,
    string Body,
    bool HasShellLines);

/// <summary>
/// A custom command's Markdown file read as Claude Code reads one (2026-10-30): a frontmatter only when the first line (a BOM dropped, CRLF
/// folded) is <c>---</c>, closed by <c>---</c> or YAML's <c>...</c>, read by the skills' own key/value reader
/// (<see cref="SkillFrontmatter.TryReadBlock"/>). Lenient: a frontmatter that doesn't read sets no field and carries a warning, its body still
/// the text after the closing fence; with no closing fence the whole text after line 1 is the body. Keys read: <c>description</c>,
/// <c>argument-hint</c> (as text: Claude's <c>[issue]</c> is no list), <c>arguments</c> (a flow list, a space-separated string or a
/// <c>- item</c> list) and <c>user-invocable</c>; any other key is ignored, so Claude's command files load as they are. Pure.
/// </summary>
public static class CustomCommandFile
{
    public const string DescriptionKey = "description";
    public const string ArgumentHintKey = "argument-hint";
    public const string ArgumentsKey = "arguments";
    public const string UserInvocableKey = "user-invocable";

    /// <summary>The largest file read (its length checked before it is read).</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>Reads <paramref name="text"/>, a whole command file.</summary>
    public static CustomCommandFileText Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string folded = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (folded.Length > 0 && folded[0] == '﻿')
        {
            folded = folded[1..];
        }

        string[] lines = folded.Split('\n');
        if (lines[0].TrimEnd() != SkillFrontmatter.Fence)
        {
            return Plain(folded, null);
        }

        int close = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line == SkillFrontmatter.Fence || line == "...")
            {
                close = i;
                break;
            }
        }

        if (close < 0)
        {
            return Plain(string.Join('\n', lines.Skip(1)), CustomCommandText.UnclosedFrontmatterWarning);
        }

        string body = string.Join('\n', lines.Skip(close + 1));
        if (!SkillFrontmatter.TryReadBlock(lines, close, out var entries, out string? problem))
        {
            return Plain(body, CustomCommandText.UnreadFrontmatterWarning(problem ?? ""));
        }

        string? description = null;
        string? hint = null;
        IReadOnlyList<string> arguments = [];
        bool invocable = true;
        foreach (var entry in entries)
        {
            switch (entry.Key.ToLowerInvariant())
            {
                case DescriptionKey:
                    description = SkillFrontmatter.Flatten(entry.Value) is { Length: > 0 } flat ? flat : null;
                    break;
                case ArgumentHintKey:
                    hint = SkillFrontmatter.Flatten(entry.Value) is { Length: > 0 } h ? h : null;
                    break;
                case ArgumentsKey:
                    arguments = ArgumentNames(entry.Value, entry.Items);
                    break;
                case UserInvocableKey:
                    invocable = !entry.Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        string cleaned = CleanBody(body);
        return new CustomCommandFileText(description, hint, arguments, invocable, null, cleaned, HasShellLine(cleaned));
    }

    /// <summary>
    /// The declared argument names: a flow list (<c>[issue, branch]</c>), the <c>- item</c> lines, or a space-separated string; each name
    /// <c>[A-Za-z_][A-Za-z0-9_]*</c>, any other dropped, a repeat kept once. Pure.
    /// </summary>
    public static IReadOnlyList<string> ArgumentNames(string value, IReadOnlyList<string>? items)
    {
        ArgumentNullException.ThrowIfNull(value);
        IEnumerable<string> raw;
        string trimmed = value.Trim();
        if (items is { Count: > 0 })
        {
            raw = items;
        }
        else if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            raw = trimmed[1..^1].Split(',').Select(s => SkillFrontmatter.Unquote(s.Trim()));
        }
        else
        {
            raw = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        }

        var names = new List<string>();
        foreach (string name in raw.Select(r => r.Trim()))
        {
            if (IsArgumentName(name) && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Whether <paramref name="name"/> may be a declared argument: <c>[A-Za-z_][A-Za-z0-9_]*</c>.</summary>
    public static bool IsArgumentName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        return name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    /// <summary>
    /// Whether <paramref name="body"/> holds a line Claude Code would run in a shell: <c>!`cmd`</c> at a line's start or after white space, or
    /// a <c>```!</c> fence line. The app sends such lines as written and never runs them, so the command carries a warning. Pure.
    /// </summary>
    public static bool HasShellLine(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        for (int i = 0; i + 1 < body.Length; i++)
        {
            if (body[i] == '!' && body[i + 1] == '`' && (i == 0 || char.IsWhiteSpace(body[i - 1])))
            {
                return true;
            }
        }

        return body.Split('\n').Any(line => line.TrimStart().StartsWith("```!", StringComparison.Ordinal));
    }

    /// <summary>
    /// The text shown for a command with no <c>description</c> (Claude's rule): the body's first line that isn't blank, a leading <c>#</c>
    /// heading mark taken off. Display only; the cell cuts it.
    /// </summary>
    public static string? FallbackDescription(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (string line in body.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            string unmarked = trimmed.TrimStart('#').Trim();
            return unmarked.Length > 0 ? unmarked : null;
        }

        return null;
    }

    /// <summary>A body as it is sent: the blank lines at its top dropped, its end trimmed; the text inside as written.</summary>
    public static string CleanBody(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string text = body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0 || text[start..end].Trim().Length > 0)
            {
                break;
            }

            start = end + 1;
        }

        return text[start..];
    }

    private static CustomCommandFileText Plain(string body, string? warning)
    {
        string cleaned = CleanBody(body);
        return new CustomCommandFileText(null, null, [], true, warning, cleaned, HasShellLine(cleaned));
    }
}
