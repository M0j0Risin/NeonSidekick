using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>web_fetch(url, offset?, find?, outline?)</c>: a web page as light Markdown (<see cref="HtmlToMarkdown"/>),
/// <see cref="MaxChars"/> characters at a time — <c>offset</c> continues a long one, or with <c>find</c> or <c>outline</c> continues
/// their lists (2026-11-16), the page served from <see cref="PageCache"/> so paging never downloads twice. Plain text, JSON, XML and
/// CSV come back as they are; a picture is an error. A PDF (2026-10-14, the PDF plan's phase 2) is read by pdf.js
/// (<see cref="WebFetcher.PdfAnswerPage"/>): its outline and its first pages, the rest through <c>download_file</c> and <c>read_pdf</c>.
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
            "offset": { "type": "integer", "description": "Where to continue a long page, in characters: the number the previous result's header named. Leave it out to start at the top. With find or outline, where the list continues, as their header names." },
            "find": { "type": "string", "description": "Words to look for: returns the passages of the whole page holding every word, each with its offset, instead of the text. Not with outline." },
            "outline": { "type": "boolean", "description": "true lists the page's headings, each with its offset, instead of the text. Not with find." }
          },
          "required": ["url"]
        }
        """);

    private readonly WebAccess _web;
    private readonly Func<AppSettingsData> _effective;
    private readonly BotCheckSolver? _botCheck;

    // The page find folded last (2026-11-16, the web-tools review's #8): paging find over one page folds it once. Keyed by the cached fetch
    // itself, so a re-fetch misses; one slot, swapped whole, safe if two calls race; never kept per cached page (20 pages at 5 MB each).
    private sealed record FoldSlot(FetchResult Page, FoldedText Folded);

    private volatile FoldSlot? _fold;

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
        "the header names the offset to continue a long page with, or outline lists its headings and find returns the passages holding given words, each with its offset, and offset continues either list. Also reads plain text, JSON, XML and CSV, and a PDF's first " + WebFetcher.MaxPdfPages.ToString(System.Globalization.CultureInfo.InvariantCulture) + " pages; for an exact copy, use " + DownloadFileTool.ToolName + ". Use it on a " + WebSearchTool.ToolName + " result or a URL the user gives you.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The window of a fetched page from <paramref name="offset"/>, under its header. Pure; pinned.</summary>
    public static string Format(FetchResult page, int offset) => Format(page, offset, fragment: null);

    /// <summary>
    /// What every answer about one fetched page shares (2026-11-16, the web-tools review's #165: each mode re-derived them, and the PDF note's
    /// separator differed): its title, the address without the fetch's fragment, the text, and the app's notes that follow the last fence
    /// — a PDF read in part or with no text (<see cref="PageText.More"/>), a page cut at the download cap (#131) — each on a line of its own.
    /// </summary>
    private sealed record Parts(string Title, string Url, string Text, string Notes);

    private static Parts PartsOf(FetchResult page)
    {
        var notes = new System.Text.StringBuilder();
        if (page.Page?.More is { } more)
        {
            notes.Append('\n').Append(more);
        }

        if (page.Truncated)
        {
            notes.Append('\n').Append(WebText.PageCutAtCap(WebFetcher.MaxDownloadBytes));
        }

        return new Parts(page.Page?.Title ?? "", WebFetcher.PageKey(page.FinalUrl), page.Page?.Markdown ?? page.Text, notes.ToString());
    }

    /// <summary>
    /// <see cref="Format(FetchResult, int)"/> starting at <paramref name="fragment"/> (2026-10-17, the toki-pona session: <c>#n</c> and
    /// <c>#epiku</c> on one page both read from its top), the URL's fragment when no offset was given: the page's anchor of that id
    /// (exact, then ignoring case) is where the window starts, named in the header; one the page lacks reads from the top and says so.
    /// A text answer or a PDF has no anchors and ignores it. Pure.
    /// </summary>
    public static string Format(FetchResult page, int offset, string? fragment)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (Unreadable(page) is { } unreadable)
        {
            return unreadable;
        }

        if (offset < 0)
        {
            return WebText.NegativeOffset;
        }

        var parts = PartsOf(page);
        string text = parts.Text;
        int total = text.Length;
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

        // The page's text is the page's (2026-11-15, the web-tools plan's §3): one fence under the header, the app's notes after it, the
        // quote note last. Offsets index the page's text, outside the fence. The window is scrubbed first (2026-11-16, the web-tools
        // review's #112, #49, #130): an escape is longer than the character it stands for, so the window takes source characters until the
        // scrubbed text fills MaxChars, and the header's next offset is where it stopped; its leading newlines sit inside the fence.
        var (window, consumed) = UntrustedText.ScrubPrefix(text, offset, MaxChars);
        int end = offset + consumed;
        string header = WebText.FetchHeader(parts.Title, parts.Url, offset + 1, end, total, page.Engine, end < total ? end : null, anchor, missing);
        return UntrustedText.WithNote(header + "\n\n" + UntrustedText.Quote(window) + parts.Notes);
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
        return text.Length == 0 ? WebText.EmptyPage(page.Page?.Title ?? "", WebFetcher.PageKey(page.FinalUrl), page.Engine) : null;
    }

    /// <summary>
    /// <c>outline</c>'s answer (2026-11-15, the web-tools plan's §1): the page's headings, each with its offset, under
    /// <see cref="WebText.OutlineHeader"/>, held to <see cref="MaxChars"/>, the app's notes after the list. Since 2026-11-16 (the web-tools
    /// review's #177) <paramref name="from"/> lists the headings at or after it, and the header names where a list the cap cut goes on.
    /// Pure.
    /// </summary>
    public static string FormatOutline(FetchResult page, int from = 0)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (Unreadable(page) is { } unreadable)
        {
            return unreadable;
        }

        var parts = PartsOf(page);
        int total = parts.Text.Length;
        var all = WebOutline.Headings(page);
        if (all.Count == 0)
        {
            return UntrustedText.WithNote(WebText.NoHeadings(parts.Title, parts.Url, total, page.Engine) + parts.Notes);
        }

        IReadOnlyList<PageHeading> headings = from > 0 ? [.. all.Where(h => h.Offset is { } at && at >= from)] : all;

        // Each heading fenced on its own line (WebOutline.Line), the app's notes after the last, the quote note last of all; the room
        // measured against the longest header this list could have.
        string widest = WebText.OutlineHeader(parts.Title, parts.Url, headings.Count, total, page.Engine, from, int.MaxValue);
        int room = MaxChars - widest.Length - parts.Notes.Length - UntrustedText.QuoteNote.Length - 3;
        var (list, next) = WebOutline.Render(headings, room);
        string header = WebText.OutlineHeader(parts.Title, parts.Url, headings.Count, total, page.Engine, from, next);
        return UntrustedText.WithNote(header + (list.Length > 0 ? "\n\n" + list : "") + parts.Notes);
    }

    /// <summary>
    /// <c>find</c>'s answer (2026-11-15, the web-tools plan's §1): the passages holding every one of <paramref name="words"/>
    /// (<see cref="WebFind"/>), each under its <c>@offset</c>, under <see cref="WebText.FindHeader"/>; none matching names the page's
    /// first headings instead. The app's notes follow the last fence, so a word on a PDF page not read is never a bare "no". Since
    /// 2026-11-16 <paramref name="from"/> lists the passages at or after it (#177), the words are echoed as found (#104), each passage's
    /// offset is its first shown character (#130), and a PDF's own outline block and page markers are never a hit (#31). Pure.
    /// </summary>
    public static string FormatFind(FetchResult page, IReadOnlyList<string> words, int from = 0, FoldedText? folded = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(words);
        if (Unreadable(page) is { } unreadable)
        {
            return unreadable;
        }

        var parts = PartsOf(page);
        string text = parts.Text;
        string shownWords = WebText.FindWordsShown(words);
        var found = WebFind.Passages(text, words, from, folded ?? WebFind.Fold(Masked(page)));
        if (found.Total == 0)
        {
            var headings = WebOutline.First(WebOutline.Headings(page), FindNoneHeadings);
            return UntrustedText.WithNote(WebText.FindNone(parts.Title, parts.Url, shownWords, text.Length, page.Engine) + "\n\n"
                + (headings.Count > 0 ? WebText.FindNoneHint(headings) : WebText.FindNoneNoHeadings) + parts.Notes);
        }

        var sb = new System.Text.StringBuilder(WebText.FindHeader(parts.Title, parts.Url, shownWords, found.Shown.Count, found.Total, text.Length, page.Engine, from, found.NextOffset));
        foreach (var passage in found.Shown)
        {
            // Each passage fenced on its own, its @offset outside (2026-11-15, the web-tools plan's §3), at the first character shown.
            string shown = passage.Text.Trim();
            int lead = passage.Text.Length - passage.Text.TrimStart().Length;
            sb.Append("\n\n").Append(WebText.PassageAt(passage.Offset + lead)).Append('\n').Append(UntrustedText.Fenced(shown));
        }

        return UntrustedText.WithNote(sb.Append(parts.Notes).ToString());
    }

    /// <summary>
    /// The text find searches (2026-11-16, the web-tools review's #31): the page's, with the spans the app wrote (a PDF's outline block and
    /// page markers, <see cref="PageText.AppSpans"/>) blanked, same length, so offsets still index the page and no hit lands in them.
    /// </summary>
    public static string Masked(FetchResult page)
    {
        ArgumentNullException.ThrowIfNull(page);
        string text = page.Page?.Markdown ?? page.Text;
        if (page.Page?.AppSpans is not { Count: > 0 } spans)
        {
            return text;
        }

        var masked = text.ToCharArray();
        foreach (var (start, length) in spans)
        {
            for (int i = Math.Max(0, start); i < Math.Min(masked.Length, start + length); i++)
            {
                masked[i] = masked[i] == '\n' ? '\n' : ' ';
            }
        }

        return new string(masked);
    }

    /// <summary>
    /// The words of a <c>find</c> (<see cref="Pdf.PdfFind.Words"/>), each trimmed of leading and trailing punctuation (2026-11-16, the
    /// web-tools review's #132: <c>warranty,</c> found nothing), inner hyphens kept, those left with no letter or digit set aside.
    /// </summary>
    public static IReadOnlyList<string> FindWords(string find)
    {
        ArgumentNullException.ThrowIfNull(find);
        return [.. Pdf.PdfFind.Words(find).Select(w => w.Trim().Trim(PunctuationOf(w))).Where(w => w.Any(char.IsLetterOrDigit)).Distinct(StringComparer.Ordinal)];
    }

    // The punctuation characters of a word, for its trim.
    private static char[] PunctuationOf(string word) => [.. word.Where(char.IsPunctuation).Distinct()];

    /// <summary>The fetch itself, apart from the argument reading: the result text the model reads.</summary>
    public async Task<string> FetchAsync(string url, int? offset, CancellationToken cancellationToken) =>
        (await ReadAsync(url, offset, find: "", outline: false, cancellationToken).ConfigureAwait(false)).Text;

    // The answer and the page it came from, when one was fetched (or served from the cache). A blank find and outline false count as not
    // given, as read_pdf's find does, so a model sending defaults isn't refused; an offset of 0 is the start, as not giving one is.
    private async Task<(string Text, FetchResult? Page)> ReadAsync(string url, int? offset, string find, bool outline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(find);

        // The url first (2026-11-16, the web-tools review's #164): a bad address is named before any other argument.
        if (string.IsNullOrWhiteSpace(url))
        {
            return (WebText.NoUrl, null);
        }

        var parsed = WebFetcher.ParseUrl(url);
        if (parsed is null)
        {
            return (WebText.NotHttp(url.Trim()), null);
        }

        find = find.Trim();
        if (find.Length > 0 && outline)
        {
            return (WebText.FindAndOutline, null);
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
            return (FormatOutline(page, from), page);
        }

        if (words.Count > 0)
        {
            return (FormatFind(page, words, from, FoldedFor(page)), page);
        }

        // An offset given wins over the fragment.
        return (Format(page, from, offset is null ? WebFetcher.FragmentOf(parsed) : null), page);
    }

    // The page folded for find, from the slot when it holds this very fetch.
    private FoldedText? FoldedFor(FetchResult page)
    {
        if (!page.Ok)
        {
            return null;
        }

        if (_fold is { } slot && ReferenceEquals(slot.Page, page))
        {
            return slot.Folded;
        }

        var folded = WebFind.Fold(Masked(page));
        _fold = new FoldSlot(page, folded);
        return folded;
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
    /// a <c>find</c> and an <c>outline</c> alike; the text alone for a failed fetch, a refusal or an empty page (2026-11-16, the web-tools
    /// review's #21). The URL is the page's, without the fragment the cached fetch carried (#33).
    /// </summary>
    public static object Sourced(string text, FetchResult? page)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool read = page is { Ok: true } && !Assistant.IsToolError(text) && (page.Page?.Markdown ?? page.Text).Length > 0;
        return ToolSourcedResult.Of(text, read ? new WebSource(WebSourceKind.Page, WebFetcher.PageKey(page!.FinalUrl), page.Page?.Title ?? "") : null);
    }
}
