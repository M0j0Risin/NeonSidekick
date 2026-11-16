using System.Text;

namespace NeonSidekick.Files;

/// <summary>
/// Text written into Markdown as plain text (2026-11-16, the web-tools review's #94: moved down out of <c>App.SessionExport</c>, so the Web
/// layer's sources can use it without reaching up). Pure.
/// </summary>
public static class MarkdownEscape
{
    /// <summary>
    /// <paramref name="text"/> as plain Markdown text: a backslash before each character that would make it something else (<c>\ ` * _ [ ] &lt; &gt; # |</c>,
    /// and since 2026-10-30 <c>$ = ~ &amp; %</c>, review F50: Obsidian read <c>$…$</c> as math, <c>==</c> and <c>~~</c> as marks, <c>&amp;amp;</c> as an
    /// entity and <c>%%</c> as a comment), for the app's own lines — the title and the facts — so "Fix the *bug* in [x]" shows as typed.
    /// </summary>
    public static string Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var escaped = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|' or '$' or '=' or '~' or '&' or '%')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// <see cref="Text"/> for a source's label or host (2026-11-16, the web-tools review's #135, #138, #142): also no autolink (<c>www.</c> and
    /// <c>://</c> broken by an escape, so <c>(www.bbc.com)</c> or a URL in a page's title never becomes a second link) and no list marker at
    /// the start (<c>1. </c>, <c>- </c>, <c>+ </c>), so an unlinked label can't nest a list. The note export keeps <see cref="Text"/>'s rule.
    /// </summary>
    public static string Label(string text)
    {
        string escaped = Text(text);
        var sb = new StringBuilder(escaped.Length + 4);
        for (int i = 0; i < escaped.Length; i++)
        {
            char c = escaped[i];
            if (c == ':' && i + 2 < escaped.Length && escaped[i + 1] == '/' && escaped[i + 2] == '/')
            {
                sb.Append('\\');
            }
            else if (c == '.' && i >= 3 && escaped.AsSpan(i - 3, 3).Equals("www", StringComparison.OrdinalIgnoreCase)
                && (i == 3 || !char.IsLetterOrDigit(escaped[i - 4])))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        string label = sb.ToString();
        if (label.Length >= 2 && label[0] is '-' or '+' && label[1] == ' ')
        {
            return "\\" + label;
        }

        int digits = 0;
        while (digits < label.Length && char.IsAsciiDigit(label[digits]))
        {
            digits++;
        }

        return digits > 0 && digits + 1 < label.Length && label[digits] is '.' or ')' && label[digits + 1] == ' '
            ? label[..digits] + "\\" + label[digits..]
            : label;
    }
}
