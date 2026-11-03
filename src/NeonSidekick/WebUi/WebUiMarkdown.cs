using NeonSidekick.Files;
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
    /// <summary><paramref name="markdown"/> as HTML for the page; a single line break a <c>&lt;br&gt;</c>, as the transcript shows it (2026-11-01).</summary>
    public static string ToHtml(string markdown) => ToHtml(markdown, "");

    /// <summary>
    /// <see cref="ToHtml(string)"/> with <paramref name="idPrefix"/> ahead of its footnotes' ids (2026-11-02, review P5-2): the page holds
    /// every reply at once, so each passes its own (the feed's <c>e{seq}-</c>, an ask's <c>a{id}-</c>).
    /// </summary>
    public static string ToHtml(string markdown, string idPrefix) =>
        PdfHtml.Markdown(markdown ?? "", _ => null, SafeLink, softBreaksAsLines: true, idPrefix: idPrefix ?? "");

    /// <summary>
    /// The address a link may keep, or null: an absolute http, https or mailto address alone, by <see cref="SafeText.WebLink"/>'s one rule
    /// (2026-11-02, review P12-8): a bidi or zero-width character or an address past the length cap now gets no link, and the host is its
    /// ASCII (<c>xn--</c>) form, as Word's links and the transcript's open.
    /// </summary>
    public static string? SafeLink(string url) => SafeText.WebLink(url)?.Target;
}
