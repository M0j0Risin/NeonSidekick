using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Web;

/// <summary>What a <see cref="WebSource"/> records: a page read (<c>web_fetch</c>), a file saved (<c>download_file</c>), or a search.</summary>
public enum WebSourceKind
{
    Page,
    Download,

    /// <summary>A <c>web_search</c> that answered (2026-11-15): counted under a reply, never listed, so stored with no URL or title.</summary>
    Search,
}

/// <summary>
/// One thing a turn took from the web (2026-11-15, the web-tools plan's §2, feature idea #27): the address read or saved and the page's
/// title (a download's file name), for the 📚 line under the reply, the Web UI and the session's export.
/// </summary>
public sealed record WebSource(WebSourceKind Kind, string Url, string Title);

/// <summary>
/// The sources under a reply, in one home (2026-11-16, the web-tools review's #27, #80, #94, #121): the label every view names a source by,
/// its vetted link, and its Markdown item for the Web UI's line and both exports. The transcript's line (<c>TranscriptRenderer.SourcesLine</c>)
/// and the exports build on these, so the views never disagree. Its words are <see cref="WebText"/>'s. Pure.
/// </summary>
public static class WebSources
{
    /// <summary>The app's download glyph (📥): a download's mark on its label, and the background jobs' (<c>BackgroundJobText</c>).</summary>
    public const string DownloadGlyph = "📥";

    /// <summary>Whether a turn's sources draw a line: a page or a download read; searches alone don't.</summary>
    public static bool HasLine(IReadOnlyList<WebSource>? sources) =>
        sources is not null && sources.Any(s => s.Kind != WebSourceKind.Search);

    /// <summary>The pages and downloads of <paramref name="sources"/>, in order; the searches are only counted.</summary>
    public static IReadOnlyList<WebSource> Listed(IReadOnlyList<WebSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return [.. sources.Where(s => s.Kind != WebSourceKind.Search)];
    }

    /// <summary>How many searches <paramref name="sources"/> counts.</summary>
    public static int Searches(IReadOnlyList<WebSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return sources.Count(s => s.Kind == WebSourceKind.Search);
    }

    /// <summary>
    /// One source as every view names it (2026-11-15): its title, else its host alone, so a page with no title is never an empty link;
    /// a download marked <see cref="DownloadGlyph"/>. <c>Host</c> is set only beside a title (the page's own words, so the address it came
    /// from follows it); null when the label is the host already, or the title is the host (2026-11-16, the web-tools review's #149: case
    /// and a leading <c>www.</c> aside). The host is the final URL's, in its ASCII form, never the title's.
    /// </summary>
    public static (string Label, string? Host) Label(WebSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string host = Host(source.Url);
        string title = source.Title.Trim();
        string mark = source.Kind == WebSourceKind.Download ? DownloadGlyph + " " : "";
        if (title.Length == 0)
        {
            return (mark + (host.Length > 0 ? host : source.Url), null);
        }

        bool same = host.Length > 0 && string.Equals(WithoutWww(title), WithoutWww(host), StringComparison.OrdinalIgnoreCase);
        return (mark + title, host.Length > 0 && !same ? host : null);
    }

    private static string WithoutWww(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    /// <summary>
    /// The host of <paramref name="url"/> as a link opens it, in its ASCII form (punycode); empty for one that doesn't parse. One the link
    /// vetting refuses is still named by its ASCII host (2026-11-16, the web-tools review's #61: the Unicode form let a look-alike pass).
    /// </summary>
    public static string Host(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var uri = Link(url)?.Uri ?? (Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null);
        if (uri is null)
        {
            return "";
        }

        try
        {
            return uri.IdnHost;
        }
        catch (UriFormatException)
        {
            return "";
        }
    }

    /// <summary>
    /// The link a source opens (2026-11-16, the web-tools review's #80: one rule for the transcript, the feed and the exports): the app's
    /// vetted web link (<see cref="SafeText.WebLink"/>), never a <c>mailto:</c>; null for one the vetting refuses, shown as its label alone.
    /// </summary>
    public static SafeWebLink? Link(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return SafeText.WebLink(url) is { } link && link.Uri.Scheme != Uri.UriSchemeMailto ? link : null;
    }

    /// <summary>
    /// One source as a Markdown item (2026-11-15, the web-tools plan's §2): <c>[title](&lt;url&gt;) (host)</c> by <see cref="Label"/>'s rule,
    /// the label and host escaped as a label (<see cref="MarkdownEscape.Label"/>: since 2026-11-16 no autolink and no list marker, the
    /// review's #135, #138, #142), the destination the vetted target in angle brackets, so a URL with parentheses stays whole. One the
    /// vetting refuses is its label alone, as the transcript's line shows it.
    /// </summary>
    public static string Markdown(WebSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (label, host) = Label(source);
        string shown = MarkdownEscape.Label(UntrustedText.OneLine(label));
        string tail = host is null ? "" : " (" + MarkdownEscape.Label(host) + ")";
        if (Link(source.Url) is { } link)
        {
            string target = link.Target.Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal);
            return "[" + shown + "](<" + target + ">)" + tail;
        }

        return shown + tail;
    }

    /// <summary>A turn's pages and files as Markdown items, in order; empty when only searches were made. The searches are counted apart.</summary>
    public static IReadOnlyList<string> Items(IReadOnlyList<WebSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return [.. Listed(sources).Select(Markdown)];
    }

    /// <summary>The Web UI's 📚 line (2026-11-15): the glyph, then every source's item, then the searches, on one line. Empty for none.</summary>
    public static string LineMarkdown(IReadOnlyList<WebSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (!HasLine(sources))
        {
            return "";
        }

        var items = Items(sources).ToList();
        int searches = Searches(sources);
        if (searches > 0)
        {
            items.Add(WebText.SourcesSearches(searches));
        }

        return WebText.SourcesGlyph + " " + string.Join(WebText.SourcesSeparator, items);
    }
}
