using System.Globalization;
using System.Text;

namespace NeonSidekick.Web;

/// <summary>
/// <c>web_fetch</c>'s <c>outline</c> (2026-11-15, the web-tools plan's §1, feature idea #26): a page's headings as an indented list, each
/// with the offset its line starts at, so the model reads one part with <c>offset</c> instead of paging the whole page. The headings come
/// from <see cref="PageText.Headings"/>, recorded where the text was written, or for a Markdown answer from its own <c>#</c> lines.
/// Pure; pinned by <c>WebOutlineTests</c>.
/// </summary>
public static class WebOutline
{
    /// <summary>
    /// The headings of <paramref name="page"/>: an HTML page's, a PDF's outline or page markers; a Markdown answer's ATX lines outside its
    /// code fences (2026-11-16, the web-tools review's #133: a README's outline said "no headings"); empty for any other text answer.
    /// </summary>
    public static IReadOnlyList<PageHeading> Headings(FetchResult page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Page?.Headings ?? (IsMarkdown(page) ? MarkdownHeadings(page.Text) : []);
    }

    /// <summary>Whether a text answer is Markdown: served as <c>text/markdown</c>, or from a path ending <c>.md</c>.</summary>
    public static bool IsMarkdown(FetchResult page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.MediaType.StartsWith("text/markdown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(page.FinalUrl, UriKind.Absolute, out var url) && url.AbsolutePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ATX headings of Markdown <paramref name="text"/> (<c># Title</c> to <c>###### Title</c>, at most three spaces in), each at its
    /// line's offset, a closing run of <c>#</c> dropped; lines inside a <c>```</c> or <c>~~~</c> fence are code, never headings. Pure.
    /// </summary>
    public static IReadOnlyList<PageHeading> MarkdownHeadings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var headings = new List<PageHeading>();
        string? fence = null;
        int at = 0;
        while (at <= text.Length)
        {
            int end = text.IndexOf('\n', at);
            if (end < 0)
            {
                end = text.Length;
            }

            string line = text[at..end].TrimEnd('\r');
            string lead = line.TrimStart(' ');
            if (line.Length - lead.Length <= 3)
            {
                if (fence is not null)
                {
                    if (lead.StartsWith(fence, StringComparison.Ordinal) && lead.Trim(fence[0]).Trim().Length == 0)
                    {
                        fence = null;
                    }
                }
                else if (lead.StartsWith("```", StringComparison.Ordinal) || lead.StartsWith("~~~", StringComparison.Ordinal))
                {
                    fence = lead[..3];
                }
                else if (Atx(lead) is { } heading)
                {
                    headings.Add(new PageHeading(heading.Level, heading.Text, at));
                }
            }

            at = end + 1;
        }

        return headings;
    }

    // An ATX heading line's level and text, or null.
    private static (int Level, string Text)? Atx(string line)
    {
        int level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level is < 1 or > 6 || (level < line.Length && line[level] is not ' ' and not '\t'))
        {
            return null;
        }

        string rest = line[level..].Trim();
        string closed = rest.TrimEnd('#');
        if (closed.Length < rest.Length && (closed.Length == 0 || closed[^1] is ' ' or '\t'))
        {
            rest = closed.TrimEnd();
        }

        return rest.Length == 0 ? null : (level, rest);
    }

    /// <summary>
    /// One heading's line (<see cref="WebText.OutlineLine"/>): the offset (or the page it is on, not read), then its text, fenced as the
    /// page's words; a PDF's own page marker (<see cref="PageHeading.Own"/>) is the app's and stays bare.
    /// </summary>
    public static string Line(PageHeading heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        string at = heading.Offset is { } offset
            ? offset.ToString(CultureInfo.InvariantCulture)
            : WebText.HeadingNotRead(heading.Page);
        return WebText.OutlineLine(heading.Level, at, Text(heading));
    }

    /// <summary>A heading's text as a line shows it: the page's words fenced (2026-11-15, the web-tools plan's §3), the app's marker bare.</summary>
    public static string Text(PageHeading heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        return heading.Own ? heading.Text : Llm.Tools.UntrustedText.Fenced(Shown(heading.Text));
    }

    /// <summary>
    /// The list of <paramref name="headings"/>, one <see cref="Line"/> each, cut to <paramref name="maxChars"/> with
    /// <see cref="WebText.OutlineMore"/> counting what was left, and where the rest starts (the first left out with an offset), null when
    /// every heading fit. The last heading needs no room for the count (2026-11-16, the web-tools review's #83); the count's width is
    /// measured once (#97).
    /// </summary>
    public static (string Text, int? Next) Render(IReadOnlyList<PageHeading> headings, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(headings);
        int moreWidth = WebText.OutlineMore(headings.Count).Length;
        var sb = new StringBuilder();
        for (int i = 0; i < headings.Count; i++)
        {
            string line = Line(headings[i]);
            bool last = i == headings.Count - 1;
            if (sb.Length + line.Length + (last ? 0 : 1 + moreWidth) > maxChars)
            {
                int? next = headings.Skip(i).Select(h => h.Offset).FirstOrDefault(o => o is not null);
                sb.Append(WebText.OutlineMore(headings.Count - i));
                return (sb.ToString(), next);
            }

            sb.Append(line).Append('\n');
        }

        return (sb.ToString().TrimEnd('\n'), null);
    }

    /// <summary>The first few headings as a <c>find</c> that matched nothing names them: where the model might look instead.</summary>
    public static IReadOnlyList<string> First(IReadOnlyList<PageHeading> headings, int count)
    {
        ArgumentNullException.ThrowIfNull(headings);
        return [.. headings.Take(count).Select(Text)];
    }

    // A heading's text on one line, held to a readable length (2026-11-16, the web-tools review's #37: the shared cut).
    private static string Shown(string text) => Llm.Tools.UntrustedText.Cut(HtmlToMarkdown.Collapse(text), MaxHeadingChars);

    /// <summary>The longest heading text a line shows.</summary>
    public const int MaxHeadingChars = 200;
}
