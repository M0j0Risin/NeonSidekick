using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>web_fetch(url, offset?, find?, outline?)</c>: a web page as light Markdown (<see cref="HtmlToMarkdown"/>),
/// <see cref="MaxChars"/> characters at a time — <c>offset</c> continues a long one, the page
/// served from <see cref="PageCache"/> so paging never downloads twice. Plain text, JSON, XML and
/// CSV come back as they are; a picture is an error. A PDF (2026-10-14, the PDF plan's phase 2) is read by pdf.js
/// (<see cref="WebFetcher.PdfAnswerText"/>): its outline and its first pages, the rest through <c>download_file</c> and <c>read_pdf</c>.
/// The result starts with a header line
/// naming the title, the final URL, the window and the engine, so the transcript's one-line note
/// reads <c>Example Domain — https://example.com/ (chars 1–2,100 of 2,100, http)</c>.
/// <para>
/// Since 2026-10-13 a page refused by a bot check that wants a person (<see cref="BotCheck.Offers(FetchResult)"/>) can be shown to the
/// user in the Browser window (<see cref="BotCheckSolver"/>, per <c>Web browser botcheck window</c>); the page they let through is the
/// answer, its engine <see cref="WebText.WindowEngine"/>.
/// </para>
/// </summary>
public sealed class WebFetchTool : AIFunction
{
    public const string ToolName = "web_fetch";
    public const string UrlArgument = "url";
    public const string OffsetArgument = "offset";
    public const string FindArgument = "find";
    public const string OutlineArgument = "outline";

    /// <summary>One call's window, the <c>read_file</c> cap.</summary>
    public const int MaxChars = Files.WorkingDirectory.MaxReadChars;

    /// <summary>How many of a page's first headings a <c>find</c> that matched nothing names.</summary>
    public const int FindNoneHeadings = 5;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "The page's address (http or https). A #anchor starts the text at that part of the page." },
            "offset": { "type": "integer", "description": "Where to continue a long page, in characters: the number the previous result's header named. Leave it out to start at the top." },
            "find": { "type": "string", "description": "Words to look for: returns the passages of the whole page holding every word, each with its offset, instead of the text. Not with offset or outline." },
            "outline": { "type": "boolean", "description": "true lists the page's headings, each with its offset, instead of the text. Not with offset or find." }
          },
          "required": ["url"]
        }
        """);

    private readonly WebAccess _web;
    private readonly Func<AppSettingsData> _effective;
    private readonly BotCheckSolver? _botCheck;

    /// <param name="botCheck">Who shows a bot check to the user (2026-10-13); null where nobody can be asked (headless).</param>
    public WebFetchTool(WebAccess web, Func<AppSettingsData> effective, BotCheckSolver? botCheck = null)
    {
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _botCheck = botCheck;
    }

    public override string Name => ToolName;

    public override string Description =>
        "Fetches a web page and returns its readable content as Markdown, " + MaxChars.ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters at a time; " +
        "the header names the offset to continue a long page with, or outline lists its headings and find returns the passages holding given words, each with its offset. Also reads plain text, JSON, XML and CSV, and a PDF's first " + WebFetcher.MaxPdfPages.ToString(System.Globalization.CultureInfo.InvariantCulture) + " pages. Use it on a " + WebSearchTool.ToolName + " result or a URL the user gives you.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The window of a fetched page from <paramref name="offset"/>, under its header. Pure; pinned.</summary>
    public static string Format(FetchResult page, int offset) => Format(page, offset, fragment: null);

    /// <summary>
    /// <see cref="Format(FetchResult, int)"/> starting at <paramref name="fragment"/> (2026-10-17, the toki-pona session: <c>#n</c> and
    /// <c>#epiku</c> on one page both read from its top), the URL's fragment when no offset was given: the page's anchor of that id
    /// (exact, then ignoring case) is where the window starts, named in the header; one the page lacks reads from the top and says so.
    /// A text answer or a PDF has no anchors and ignores it. Pure.
    /// </summary>
    public static string Format(FetchResult page, int offset, string? fragment)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!page.Ok)
        {
            return page.Error;
        }

        string title = page.Page?.Title ?? "";
        string text = page.Page?.Markdown ?? page.Text;
        string url = WithoutFragment(page.FinalUrl);
        int total = text.Length;
        if (offset < 0)
        {
            return WebText.NegativeOffset;
        }

        if (total == 0)
        {
            return WebText.EmptyPage(title, url, page.Engine);
        }

        string? anchor = null;
        bool missing = false;
        if (fragment is { Length: > 0 } && page.Page is { } html && page.Engine != WebText.PdfEngine)
        {
            anchor = fragment;
            if (FindAnchor(html.Anchors, fragment) is { } at)
            {
                offset = Math.Min(at, total - 1);
            }
            else
            {
                missing = true;
            }
        }

        if (offset >= total)
        {
            return WebText.OffsetPastEnd(offset, total);
        }

        int end = Math.Min(total, offset + MaxChars);
        string header = WebText.FetchHeader(title, url, offset + 1, end, total, page.Engine, end < total ? end : null, anchor, missing);
        // The page's text is the page's (2026-11-15, the web-tools plan's §3): one fence under the header, the note last. Offsets index
        // the page's text, outside the fence.
        return UntrustedText.FenceBody(header + "\n\n" + text[offset..end]);
    }

    private static int? FindAnchor(IReadOnlyDictionary<string, int>? anchors, string id)
    {
        if (anchors is null)
        {
            return null;
        }

        if (anchors.TryGetValue(id, out int exact))
        {
            return exact;
        }

        foreach (var (key, at) in anchors)
        {
            if (string.Equals(key, id, StringComparison.OrdinalIgnoreCase))
            {
                return at;
            }
        }

        return null;
    }

    // The header names the page, not the fragment one fetch of it carried (a cached page keeps the first fetch's).
    private static string WithoutFragment(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Fragment.Length > 0 ? WebFetcher.PageKey(parsed) : url;

    /// <summary>
    /// A failed fetch's error or an empty page's header (2026-11-15, lifted out of <see cref="Format(FetchResult, int, string?)"/> so
    /// <c>find</c> and <c>outline</c> on a 404 answer with the error, not "no headings"); null for a page with text to read.
    /// </summary>
    public static string? Unreadable(FetchResult page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!page.Ok)
        {
            return page.Error;
        }

        string text = page.Page?.Markdown ?? page.Text;
        return text.Length == 0 ? WebText.EmptyPage(page.Page?.Title ?? "", WithoutFragment(page.FinalUrl), page.Engine) : null;
    }

    /// <summary>
    /// <c>outline</c>'s answer (2026-11-15, the web-tools plan's §1): the page's headings, each with its offset, under
    /// <see cref="WebText.OutlineHeader"/>, held to <see cref="MaxChars"/>; a PDF read in part ends with its <see cref="PageText.More"/>. Pure.
    /// </summary>
    public static string FormatOutline(FetchResult page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (Unreadable(page) is { } unreadable)
        {
            return unreadable;
        }

        string title = page.Page?.Title ?? "";
        string url = WithoutFragment(page.FinalUrl);
        int total = (page.Page?.Markdown ?? page.Text).Length;
        var headings = WebOutline.Headings(page);
        string more = page.Page?.More is { } note ? "\n" + note : "";
        if (headings.Count == 0)
        {
            return UntrustedText.WithNote(WebText.NoHeadings(title, url, total, page.Engine) + more);
        }

        // Each heading fenced on its own line (WebOutline.Line), the app's note on a cut PDF after the last, the quote note last of all.
        string header = WebText.OutlineHeader(title, url, headings.Count, total, page.Engine);
        int room = MaxChars - header.Length - more.Length - UntrustedText.QuoteNote.Length - 3;
        return UntrustedText.WithNote(header + "\n\n" + WebOutline.Render(headings, room) + more);
    }

    /// <summary>
    /// <c>find</c>'s answer (2026-11-15, the web-tools plan's §1): the passages holding every one of <paramref name="words"/>
    /// (<see cref="WebFind"/>), each under its <c>@offset</c>, under <see cref="WebText.FindHeader"/>; none matching names the page's
    /// first headings instead. A PDF read in part ends with its <see cref="PageText.More"/>, so a word on a page not read is never a bare "no".
    /// Pure.
    /// </summary>
    public static string FormatFind(FetchResult page, string query, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(words);
        if (Unreadable(page) is { } unreadable)
        {
            return unreadable;
        }

        string title = page.Page?.Title ?? "";
        string url = WithoutFragment(page.FinalUrl);
        string text = page.Page?.Markdown ?? page.Text;
        string more = page.Page?.More is { } note ? "\n\n" + note : "";
        string shownQuery = HtmlToMarkdown.Collapse(query);
        var found = WebFind.Passages(text, words);
        if (found.Total == 0)
        {
            var headings = WebOutline.First(WebOutline.Headings(page), FindNoneHeadings);
            return UntrustedText.WithNote(WebText.FindNone(title, url, shownQuery, text.Length, page.Engine) + "\n\n"
                + (headings.Count > 0 ? WebText.FindNoneHint(headings) : WebText.FindNoneNoHeadings) + more);
        }

        var sb = new System.Text.StringBuilder(WebText.FindHeader(title, url, shownQuery, found.Shown.Count, found.Total, text.Length, page.Engine));
        foreach (var passage in found.Shown)
        {
            // Each passage fenced on its own, its @offset outside (2026-11-15, the web-tools plan's §3).
            sb.Append("\n\n").Append(WebText.PassageAt(passage.Offset)).Append('\n').Append(UntrustedText.Fenced(passage.Text.Trim()));
        }

        return UntrustedText.WithNote(sb.Append(more).ToString());
    }

    /// <summary>The words of a <c>find</c> (<see cref="Pdf.PdfFind.Words"/>), those of only punctuation set aside.</summary>
    public static IReadOnlyList<string> FindWords(string find)
    {
        ArgumentNullException.ThrowIfNull(find);
        return [.. Pdf.PdfFind.Words(find).Where(w => w.Any(char.IsLetterOrDigit))];
    }

    /// <summary>The fetch itself, apart from the argument reading: the result text the model reads.</summary>
    public Task<string> FetchAsync(string url, int? offset, CancellationToken cancellationToken) =>
        FetchAsync(url, offset, find: "", outline: false, cancellationToken);

    /// <summary>
    /// <see cref="FetchAsync(string, int?, CancellationToken)"/> with <c>find</c> or <c>outline</c> (2026-11-15): a blank
    /// <paramref name="find"/> and <paramref name="outline"/> false count as not given, as <c>read_pdf</c>'s <c>find</c> does, so a model
    /// sending defaults isn't refused. Both cover the whole page: they take no offset, ignore a URL's fragment, and share the cache.
    /// </summary>
    public async Task<string> FetchAsync(string url, int? offset, string find, bool outline, CancellationToken cancellationToken) =>
        (await ReadAsync(url, offset, find, outline, cancellationToken).ConfigureAwait(false)).Text;

    // The answer and the page it came from, when one was fetched (or served from the cache).
    private async Task<(string Text, FetchResult? Page)> ReadAsync(string url, int? offset, string find, bool outline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(find);
        find = find.Trim();
        if (find.Length > 0 && outline)
        {
            return (WebText.FindAndOutline, null);
        }

        if (offset is not null && (find.Length > 0 || outline))
        {
            return (WebText.WholePageNoOffset(outline ? OutlineArgument : FindArgument), null);
        }

        IReadOnlyList<string> words = [];
        if (find.Length > 0)
        {
            words = FindWords(find);
            if (words.Count == 0)
            {
                return (WebText.FindNoWords, null);
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return (WebText.NoUrl, null);
        }

        var parsed = WebFetcher.ParseUrl(url);
        if (parsed is null)
        {
            return (WebText.NotHttp(url.Trim()), null);
        }

        int from = offset ?? 0;
        if (from < 0)
        {
            return (WebText.NegativeOffset, null);
        }

        // The page without its fragment (2026-10-17): another fragment of a page fetched a moment ago is the same download.
        string key = WebFetcher.PageKey(parsed);
        var page = _web.Pages.TryGet(key);
        if (page is null)
        {
            // No Referer since the DuckDuckGo scrape went (2026-11-07): it was the one engine whose hits went as a click from its results page
            // would (2026-10-11), and neither SearXNG's pages nor the metasearch send one.
            var options = WebAccess.Options(_effective());
            page = await _web.Fetcher.FetchAsync(parsed, options, cancellationToken).ConfigureAwait(false);
            if (_botCheck is not null && BotCheck.Offers(page))
            {
                page = await _web.Fetcher.PastBotCheckAsync(parsed, page, options, _botCheck, BotCheckUse.Fetch, cancellationToken).ConfigureAwait(false);
            }

            _web.Pages.Put(key, page);
        }

        if (outline)
        {
            return (FormatOutline(page), page);
        }

        if (words.Count > 0)
        {
            return (FormatFind(page, find, words), page);
        }

        // An offset given wins over the fragment.
        return (Format(page, from, offset is null ? WebFetcher.FragmentOf(parsed) : null), page);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, OffsetArgument, out var offset, out var raw))
        {
            return ClockText.BadInteger(OffsetArgument, raw);
        }

        if (!ToolArguments.TryReadBoolean(arguments, OutlineArgument, out var outline, out raw))
        {
            return Files.FileText.BadBoolean(OutlineArgument, raw);
        }

        var (text, page) = await ReadAsync(ToolArguments.ReadString(arguments, UrlArgument), offset, ToolArguments.ReadString(arguments, FindArgument),
            outline ?? false, cancellationToken).ConfigureAwait(false);
        return Sourced(text, page);
    }

    /// <summary>
    /// The answer with the page it read as its source (2026-11-15, the web-tools plan's §2): the final URL and the title, for a read,
    /// a <c>find</c> and an <c>outline</c> alike; the text alone for a failed fetch or a refusal.
    /// </summary>
    public static object Sourced(string text, FetchResult? page)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (page is not { Ok: true } || Assistant.IsToolError(text))
        {
            return text;
        }

        return new ToolSourcedResult(text, new WebSource(WebSourceKind.Page, page.FinalUrl, page.Page?.Title ?? ""));
    }
}
