using NeonSidekick.Pdf;

namespace NeonSidekick.WebUi;

/// <summary>
/// A reply's Markdown as the Web UI's page shows it (2026-10-14): the app's own renderer (<see cref="PdfHtml.Markdown"/> — raw HTML escaped,
/// never rendered; pipe tables, task lists, autolinks, footnotes; code colored into <c>tk-*</c> classes), no picture fetched (each is its
/// alt text in brackets: a reply's remote image would be a tracking pixel, and the page's policy allows none), and every link vetted:
/// http, https and mailto alone, each opened in a new tab with no opener and no referrer. Pure.
/// </summary>
public static class WebUiMarkdown
{
    /// <summary><paramref name="markdown"/> as HTML for the page.</summary>
    public static string ToHtml(string markdown) => PdfHtml.Markdown(markdown ?? "", _ => null, SafeLink);

    /// <summary>The address a link may keep, or null: an absolute http, https or mailto address alone.</summary>
    public static string? SafeLink(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" or "mailto" ? uri.AbsoluteUri : null;
    }
}
