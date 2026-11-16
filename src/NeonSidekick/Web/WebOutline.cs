using System.Globalization;
using System.Text;

namespace NeonSidekick.Web;

/// <summary>
/// <c>web_fetch</c>'s <c>outline</c> (2026-11-15, the web-tools plan's §1, feature idea #26): a page's headings as an indented list, each
/// with the offset its line starts at, so the model reads one part with <c>offset</c> instead of paging the whole page. The headings come
/// from <see cref="PageText.Headings"/>, recorded where the text was written. Pure; pinned by <c>WebOutlineTests</c>.
/// </summary>
public static class WebOutline
{
    /// <summary>The headings of <paramref name="page"/>: an HTML page's, a PDF's outline or page markers; empty for a text answer.</summary>
    public static IReadOnlyList<PageHeading> Headings(FetchResult page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Page?.Headings ?? [];
    }

    /// <summary>One heading's line: two spaces a level below the top, the offset (or the page it is on, not read), then its text, fenced.</summary>
    public static string Line(PageHeading heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        string at = heading.Offset is { } offset
            ? offset.ToString(CultureInfo.InvariantCulture)
            : WebText.HeadingNotRead(heading.Page);
        // The heading is the page's words: fenced (2026-11-15, the web-tools plan's §3), its offset outside.
        return new string(' ', Math.Clamp(heading.Level - 1, 0, 5) * 2) + at + " · " + Llm.Tools.UntrustedText.Fenced(Shown(heading.Text));
    }

    /// <summary>
    /// The list of <paramref name="headings"/>, one <see cref="Line"/> each, cut to <paramref name="maxChars"/> with
    /// <see cref="WebText.OutlineMore"/> counting what was left.
    /// </summary>
    public static string Render(IReadOnlyList<PageHeading> headings, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(headings);
        var sb = new StringBuilder();
        for (int i = 0; i < headings.Count; i++)
        {
            string line = Line(headings[i]);
            string more = WebText.OutlineMore(headings.Count - i);
            if (sb.Length + line.Length + 1 + more.Length + 1 > maxChars)
            {
                sb.Append(more);
                return sb.ToString();
            }

            sb.Append(line).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The first few headings' texts, for a <c>find</c> that matched nothing: where the model might look instead.</summary>
    public static IReadOnlyList<string> First(IReadOnlyList<PageHeading> headings, int count)
    {
        ArgumentNullException.ThrowIfNull(headings);
        return [.. headings.Take(count).Select(h => Shown(h.Text))];
    }

    // A heading's text on one line, held to a readable length.
    private static string Shown(string text)
    {
        string line = HtmlToMarkdown.Collapse(text);
        return line.Length <= MaxHeadingChars ? line : Files.TextCut.Safe(line, MaxHeadingChars - 1) + "…";
    }

    /// <summary>The longest heading text a line shows.</summary>
    public const int MaxHeadingChars = 200;
}
