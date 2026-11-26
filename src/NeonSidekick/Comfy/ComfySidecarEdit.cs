using System.Globalization;
using NeonSidekick.Skills;

namespace NeonSidekick.Comfy;

/// <summary>
/// One key of a workflow's sidecar <c>&lt;name&gt;.md</c> set or removed in the raw text (2026-11-25, the workflow manager's Edit): everything
/// else survives as it stands — comments, keys the app doesn't know, <c>reinforce</c>, the tips under the head, the line endings. It reads the
/// head as <see cref="ComfyWorkflow"/> does (the first line a <c>---</c> fence, <c>key: value</c> lines to the next fence or the end, the
/// last of a duplicated key winning), so every copy of the key is replaced by one line, where the first stood. A text with no head gets one,
/// the old text kept under it as the tips; a new key goes before the closing fence, or at the end of a head that has none. A value is written
/// only when it reads back as the same text (<see cref="SkillFrontmatter.Scalar"/> then <see cref="SkillFrontmatter.Unquote"/>); line breaks
/// and tabs fold to a space first, since the reader can't take them back. Pure.
/// </summary>
public static class ComfySidecarEdit
{
    /// <summary>
    /// <paramref name="text"/> with <paramref name="key"/> set to <paramref name="value"/> as a scalar (quoted when it must be), or removed
    /// when <paramref name="value"/> is null; null when the value wouldn't read back the same. A file not made yet is "".
    /// </summary>
    public static string? Set(string text, string key, string? value) =>
        value is null ? Remove(text, key) : Write(text, key, SkillFrontmatter.Scalar(Fold(value)), Fold(value));

    /// <summary><see cref="Set"/> for a number, written plain (<c>1024</c>, <c>0.6</c>), as the wizard's sidecar writes one.</summary>
    public static string? SetNumber(string text, string key, double? value) =>
        value is not { } number ? Remove(text, key)
        : double.IsFinite(number) ? Write(text, key, number.ToString(CultureInfo.InvariantCulture), number.ToString(CultureInfo.InvariantCulture))
        : null;

    /// <summary><see cref="Set"/> for a word written plain (<c>reinforce: false</c>, a family's name).</summary>
    public static string? SetWord(string text, string key, string word) => Write(text, key, word, word);

    /// <summary>The value of <paramref name="key"/> in the head as the reader takes it (the last copy wins), or null.</summary>
    public static string? Get(string text, string key)
    {
        ArgumentNullException.ThrowIfNull(text);
        var doc = Parse(text);
        string? found = null;
        for (int i = 0; i < doc.Lines.Count; i++)
        {
            if (InHead(doc, i) && KeyOf(doc.Lines[i]) is { } k && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                string line = doc.Lines[i];
                found = SkillFrontmatter.Unquote(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim());
            }
        }

        return found;
    }

    /// <summary>
    /// <paramref name="text"/> with the tips under the head replaced by <paramref name="body"/>, trimmed (empty for none); a text with no head
    /// gets an empty one, an unclosed head its closing fence. The head and the line endings stay as they are.
    /// </summary>
    public static string SetBody(string text, string body)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(body);
        var doc = Parse(text);
        var lines = new List<string>();
        if (doc.Close == -2)
        {
            lines.Add(SkillFrontmatter.Fence);
            lines.Add(SkillFrontmatter.Fence);
        }
        else if (doc.Close == -1)
        {
            lines.AddRange(doc.Lines.Count > 1 && doc.Lines[^1].Length == 0 ? doc.Lines.Take(doc.Lines.Count - 1) : doc.Lines);
            lines.Add(SkillFrontmatter.Fence);
        }
        else
        {
            lines.AddRange(doc.Lines.Take(doc.Close + 1));
        }

        string tips = body.Trim();
        if (tips.Length > 0)
        {
            lines.AddRange(tips.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        }

        lines.Add("");
        return Join(doc, lines);
    }

    /// <summary><paramref name="value"/> with its line breaks and tabs folded to a space: what <see cref="Set"/> writes.</summary>
    public static string Fold(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }

    private static string Remove(string text, string key)
    {
        ArgumentNullException.ThrowIfNull(text);
        var doc = Parse(text);
        if (doc.Close == -2)
        {
            return text;   // no head: nothing to remove
        }

        var keep = new List<string>(doc.Lines.Count);
        for (int i = 0; i < doc.Lines.Count; i++)
        {
            if (!(InHead(doc, i) && KeyOf(doc.Lines[i]) is { } k && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
            {
                keep.Add(doc.Lines[i]);
            }
        }

        return doc.Lines.Count == keep.Count ? text : Join(doc, keep);
    }

    private static string? Write(string text, string key, string written, string wanted)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0 || key.Contains(':', StringComparison.Ordinal) || key.Trim() != key || written.Contains('\n', StringComparison.Ordinal) || written.Contains('\r', StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.Equals(SkillFrontmatter.Unquote(written.Trim()), wanted, StringComparison.Ordinal))
        {
            return null;
        }

        string line = key + ": " + written;
        var doc = Parse(text);
        if (doc.Close == -2)
        {
            // No head: one made, the old text kept under it as the tips (SidecarBody reads the same tips, trimmed).
            var made = new List<string> { SkillFrontmatter.Fence, line, SkillFrontmatter.Fence };
            if (doc.Lines.Count > 0 && !(doc.Lines.Count == 1 && doc.Lines[0].Length == 0))
            {
                made.AddRange(doc.Lines);
            }
            else
            {
                made.Add("");
            }

            return Join(doc, made);
        }

        var lines = new List<string>(doc.Lines.Count + 1);
        bool placed = false;
        for (int i = 0; i < doc.Lines.Count; i++)
        {
            if (InHead(doc, i) && KeyOf(doc.Lines[i]) is { } k && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                if (!placed)
                {
                    lines.Add(line);
                    placed = true;
                }

                continue;
            }

            if (!placed && i == doc.Close)
            {
                lines.Add(line);
                placed = true;
            }

            lines.Add(doc.Lines[i]);
        }

        if (!placed)
        {
            // An unclosed head runs to the end: the line goes last, before a final empty line (the text's last newline).
            int at = lines.Count > 0 && lines[^1].Length == 0 ? lines.Count - 1 : lines.Count;
            lines.Insert(at, line);
        }

        return Join(doc, lines);
    }

    /// <summary>The text's lines (LF), its ending and BOM, and the closing fence's index: −1 for a head with none, −2 for no head.</summary>
    private sealed record Doc(List<string> Lines, string Ending, bool Bom, int Close);

    private static Doc Parse(string text)
    {
        bool bom = text.StartsWith('﻿');
        string body = bom ? text[1..] : text;
        string ending = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        int close = -2;
        if (lines.Count > 0 && lines[0].TrimEnd() == SkillFrontmatter.Fence)
        {
            close = -1;
            for (int i = 1; i < lines.Count; i++)
            {
                if (lines[i].TrimEnd() == SkillFrontmatter.Fence)
                {
                    close = i;
                    break;
                }
            }
        }

        return new Doc(lines, ending, bom, close);
    }

    private static bool InHead(Doc doc, int i) => doc.Close != -2 && i > 0 && (doc.Close == -1 || i < doc.Close);

    /// <summary>The key of a <c>key: value</c> line as the reader takes it, or null (a comment, no colon).</summary>
    private static string? KeyOf(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        return line.TrimStart().StartsWith('#') || colon <= 0 ? null : line[..colon].Trim();
    }

    private static string Join(Doc doc, List<string> lines) => (doc.Bom ? "﻿" : "") + string.Join(doc.Ending, lines);
}
