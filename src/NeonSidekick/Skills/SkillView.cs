using NeonSidekick.UI;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.Skills;

/// <summary>
/// A <c>SKILL.md</c> drawn for reading (2026-11-24, the user's ask, from a screenshot of the trust question's View): its front matter as a header
/// block (the name in bold, the description, every other line dim and verbatim, an acting key's whole block in the warning color, as the question
/// marks it), a rule, then the body as markdown. Safe for a stranger's file: a link draws as <c>text (url)</c>, so the address always shows; HTML
/// is off, so a comment shows as text; there is no path resolver, so it makes no path links; and the exact text stays one tab away (Source),
/// which is what a trust is given for. Pure.
/// </summary>
internal static class SkillView
{
    /// <summary>
    /// <paramref name="text"/> drawn: null when its front matter can't be read (no closing fence, a line that's no pair), so nothing is guessed
    /// at. A <c>.claude/skills</c> file (<paramref name="claude"/>, its folder <paramref name="folderName"/>) is read by Claude Code's rules; with
    /// no front matter at all it gets no header, only the rendered body: the header shows only what the file says.
    /// </summary>
    public static IRenderable? Of(string text, bool claude, string folderName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(folderName);
        bool parsed = claude
            ? SkillFrontmatter.TryParseClaude(text, folderName, out var read, out string body, out _)
            : SkillFrontmatter.TryParse(text, out read, out body, out _);
        if (!parsed || read is null)
        {
            return null;
        }

        var parts = new List<IRenderable>();
        if (HasFrontmatter(text))
        {
            parts.Add(new Markup("[bold]" + Markup.Escape(read.Name) + "[/]"));
            if (read.Description.Length > 0)
            {
                parts.Add(new Markup(Markup.Escape(read.Description)));
            }

            foreach (string line in HeaderLines(read.OtherLines))
            {
                parts.Add(new Markup(line));
            }

            parts.Add(new SectionRule(SectionRule.Bare));
        }

        parts.Add(MarkdownView.Of(body));
        return new Rows(parts);
    }

    /// <summary>
    /// <paramref name="lines"/> as markup, each verbatim: an acting key's line (<see cref="SkillFrontmatter.ActingKeys"/>, matched as
    /// <see cref="SkillFrontmatter.KeysOf"/> reads it, lower-cased) in the warning color, the rest dim. An indented or blank line takes the style of
    /// the column-0 key above it, so a whole <c>hooks:</c> block reads as a warning. All of them, none cut. Pure.
    /// </summary>
    internal static IReadOnlyList<string> HeaderLines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var shown = new List<string>(lines.Count);
        bool acting = false;
        foreach (string line in lines)
        {
            if (SkillFrontmatter.KeysOf([line]) is [var key])
            {
                acting = SkillFrontmatter.ActingKeys.Contains(key, StringComparer.Ordinal);
            }

            string text = line.Length == 0 ? " " : line;
            shown.Add(acting ? $"[{Theme.WarnText.ToMarkup()}]{Markup.Escape(text)}[/]" : Theme.DimMarkup(text));
        }

        return shown;
    }

    /// <summary>Whether <paramref name="text"/> opens with a front-matter fence (a BOM and blank-free first line read as the parser reads it).</summary>
    private static bool HasFrontmatter(string text)
    {
        string folded = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿');
        int end = folded.IndexOf('\n', StringComparison.Ordinal);
        return (end < 0 ? folded : folded[..end]).Trim() == SkillFrontmatter.Fence;
    }
}
