using System.Globalization;
using System.Text;

namespace NeonSidekick.Web;

/// <summary>Why a fetch did not produce a page (<see cref="WebFetcher.FetchAsync"/>).</summary>
public enum FetchOutcome
{
    Ok,

    /// <summary>No URL was given.</summary>
    NoUrl,

    /// <summary>Not an <c>http</c> / <c>https</c> URL (a file path, <c>ftp:</c>, a bare word).</summary>
    NotHttp,

    /// <summary>The host is this machine or the local network and <c>Web browser network mode</c> is <c>internet</c>.</summary>
    LanRefused,

    /// <summary>The host is on the internet and <c>Web browser network mode</c> is <c>local_area_network</c> (2026-09-18).</summary>
    InternetRefused,

    /// <summary>No answer within <see cref="WebFetcher.FetchTimeout"/>.</summary>
    Timeout,

    /// <summary>The server refused (403 / 429 / 503, a challenge page) and nothing else could be tried.</summary>
    Blocked,

    /// <summary>A PDF, an image, an archive: not a page and not text.</summary>
    Binary,

    /// <summary>The mode wants the headless browser and none was found.</summary>
    NoBrowser,

    /// <summary>The headless browser ran and produced nothing.</summary>
    BrowserFailed,

    /// <summary>Anything else: DNS, a refused connection, TLS.</summary>
    Failed,

    /// <summary>A file over <see cref="WebFetcher.MaxFileDownloadBytes"/> (<see cref="WebFetcher.DownloadAsync"/>, 2026-09-18): refused, never cut.</summary>
    TooBig,
}

/// <summary>
/// The sentences the web tools answer with: pure statics, every string pinned, the
/// <see cref="Files.FileText"/> pattern. A result is what the model reads and, flattened to its first
/// 200 characters, the one dim <c>🛠️</c> line the transcript shows, so a multi-line answer starts with a
/// header line that stands on its own. Every error starts with <c>Error:</c>. Invariant culture.
/// </summary>
public static class WebText
{
    public const string NoUrl = "Error: give the URL to fetch";
    public const string NoDownloadUrl = "Error: give the URL to download";
    public const string NoQuery = "Error: give the text to search for";
    public const string NegativeOffset = "Error: offset must be 0 or more";
    public const string Untitled = "(untitled)";

    /// <summary>The engine names on a fetch header.</summary>
    public const string HttpEngine = "http";
    public const string BrowserEngine = "headless browser";

    /// <summary>The page the user let through a bot check in the Browser window (2026-10-13, <see cref="BotCheck"/>).</summary>
    public const string WindowEngine = "browser window";

    /// <summary>The search engine name on a SearXNG search's header.</summary>
    public const string SearxngName = "SearXNG";
    public const string MetaSearchName = "Metasearch";

    /// <summary>
    /// Why a browser run made nothing when another browser held its profile folder (2026-10-11: an Edge that relaunched itself
    /// outlived the app and held <see cref="HeadlessBrowser.UserDataDirectory"/>, and every later run exited with code 21 behind
    /// "it exited before it opened its DevTools port"). The detail after a fetch's or a PDF's own lead-in. Pinned.
    /// </summary>
    public static string BrowserProfileInUse(string folder) =>
        "another browser is using its profile folder " + folder + " (one left running, or another NeonSidekick reading a page); end it and try again";

    public static readonly string NoBrowser =
        "Error: no headless browser (Edge, Chrome or Brave) was found; the user can set Web browser path on the Web tab of /tools";

    public static readonly string TooManyLinks =
        "Error: " + Llm.Tools.OpenUrlTool.ToolName + " takes at most " + Llm.Tools.OpenUrlTool.MaxUrls.ToString(CultureInfo.InvariantCulture) + " links at a time";

    public static readonly string BadResultCount =
        "Error: max_results must be " + Llm.Tools.WebSearchTool.MinResults.ToString(CultureInfo.InvariantCulture) + " to " + Llm.Tools.WebSearchTool.MaxResults.ToString(CultureInfo.InvariantCulture);

    // ---- errors ----

    public static string NotHttp(string url) => $"Error: '{url}' is not an http or https URL";
    public static string LanRefused(string host) => $"Error: '{host}' is on this machine or the local network, which Web browser network mode 'internet' keeps off limits";
    public static string InternetRefused(string host) => $"Error: '{host}' is on the internet, which Web browser network mode 'lan' keeps off limits";
    public static string Timeout(string url, TimeSpan after) => $"Error: '{url}' did not answer within {Llm.LlmTimeouts.Format(after)}";
    public static string Blocked(string url, string detail) => $"Error: '{url}' refused the request ({detail})";
    /// <summary>
    /// An answer that is no page, no text and no PDF (a PDF was named among the readable on 2026-10-14, the PDF plan's gap 71); an Office
    /// file by its type or its name is pointed at <c>download_file</c> and <c>read_office</c> (2026-10-28, the Office plan). Pinned.
    /// </summary>
    public static string Binary(string url, string mediaType, long bytes) =>
        $"Error: '{url}' is {mediaType} ({Size(bytes)}); only web pages, PDFs and text are readable" + (IsOffice(url, mediaType) ? SaveOfficeNote : "");

    /// <summary>What <see cref="Binary"/> adds for an Office file (2026-10-28). Pinned.</summary>
    public const string SaveOfficeNote = "; save it with download_file and read_office reads it";

    /// <summary>
    /// Whether an answer is a Word, Excel or PowerPoint file <c>read_office</c> reads: one of <see cref="Files.OfficeFile.MediaTypes"/>
    /// (2026-10-28, review P8-11/P11-09: an exact list, not prefixes), or the URL's path ending in one's extension.
    /// </summary>
    public static bool IsOffice(string url, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (Files.OfficeFile.IsOfficeMediaType(mediaType))
        {
            return true;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && Files.OfficeFile.IsOfficePath(uri.AbsolutePath);
    }

    // ---- a PDF (2026-10-14, the PDF plan's phase 2) ----

    /// <summary>The engine a PDF read names on its header: the HTTP leg's bytes, read by pdf.js.</summary>
    public const string PdfEngine = "http, pdf.js";

    /// <summary>A PDF with no browser to read it. Pinned.</summary>
    public static string PdfNoBrowser(string url, long bytes) =>
        $"Error: '{url}' is a PDF ({Size(bytes)}); reading one needs Edge, Chrome or Brave, and none was found; download_file saves it";

    /// <summary>A PDF over what a read takes, refused unread when the server says so. Pinned.</summary>
    public static string PdfTooBig(string url, long bytes) =>
        (bytes > 0 ? $"Error: '{url}' is a PDF of {Size(bytes)}" : $"Error: '{url}' is a PDF")
        + $", over the {Size(BrowserPdfReader.MaxBytes)} a read takes; download_file saves it";

    /// <summary>A PDF that asks for a password. Pinned.</summary>
    public const string PdfLocked = "the PDF is locked with a password";

    /// <summary>A PDF pdf.js could not read: the detail after <see cref="CouldNot"/>'s lead-in.</summary>
    public static string PdfUnreadable(string detail) => "pdf.js could not read the PDF: " + detail.Trim();

    /// <summary>The note under a PDF read in part: its first pages here, the rest through <c>download_file</c> and <c>read_pdf</c>. Pinned.</summary>
    public static string PdfMore(int read, int pageCount) =>
        $"(pages 1–{read.ToString(CultureInfo.InvariantCulture)} of {pageCount.ToString(CultureInfo.InvariantCulture)}; for the rest, save it with download_file and read it with read_pdf, pages \"{(read + 1).ToString(CultureInfo.InvariantCulture)}-\")";

    /// <summary>A PDF with no text on the pages read. Pinned.</summary>
    public const string PdfNoText =
        "(no text in this PDF: a scan or pictures only; save it with download_file and look at it with read_pdf, pictures: true)";

    /// <summary>A bot check's window ended on a file (a PDF, say) the HTTP client still could not fetch (the PDF plan's gaps 22 and 86). Pinned.</summary>
    public const string FileAfterBotCheck =
        "; the check was passed, but it is a file the window would only download: save it with download_file (the cookies it earned go with it); read_pdf reads a PDF";

    /// <summary>A download over the cap (2026-09-18): the size when the server declared one, else the cap alone. Pinned.</summary>
    public static string TooBig(string url, long bytes, long cap) =>
        bytes > 0 ? $"Error: '{url}' is {Size(bytes)}, over the {Size(cap)} download limit" : $"Error: '{url}' is over the {Size(cap)} download limit";

    /// <summary>A streamed download whose server sent nothing for <paramref name="after"/> (<see cref="WebFetcher.DownloadStallTimeout"/>, 2026-10-01): dropped, its partial file deleted. Pinned.</summary>
    public static string Stalled(string url, TimeSpan after) => $"Error: '{url}' sent nothing for {Llm.LlmTimeouts.Format(after)}; the download was dropped";

    /// <summary>A download whose URL ends in no file name and came with none (2026-09-18). Pinned.</summary>
    public static string NoFileName(string url) => $"Error: '{url}' names no file; give a path to save it as";
    public static string BrowserFailed(string url, string detail) => $"Error: the headless browser could not load '{url}': {detail}";
    public static string CouldNot(string url, string detail) => $"Error: could not fetch '{url}': {detail}";
    public static string OffsetPastEnd(int offset, int total) =>
        $"Error: offset {offset.ToString(CultureInfo.InvariantCulture)} is past the end of the page ({total.ToString("N0", CultureInfo.InvariantCulture)} characters)";
    public static string SearxngRefused(string url) => $"Error: SearXNG at {url} refused the JSON format (HTTP 403); add json to search.formats in its settings.yml";
    public static string SearxngUnreachable(string url, string detail) => $"Error: SearXNG at {url} did not answer ({detail})";
    /// <remarks>
    /// Since 2026-11-16 (the web-tools review's #123) the metasearch's engine messages arrive fenced, so the answer ends with the quote note;
    /// <paramref name="hint"/> (a range's <see cref="NoResultsSinceHint"/>) goes before it.
    /// </remarks>
    public static string SearchFailed(string engine, string detail, string hint = "") =>
        Llm.Tools.UntrustedText.WithNote($"Error: {engine} could not be searched ({detail}){hint}");

    /// <summary>
    /// The metasearch's refusal (2026-11-06), not the DuckDuckGo scrape's "try again in a minute" (gone 2026-11-07): that's wrong here, since a refused engine
    /// rests for up to 15 days, in memory only.
    /// </summary>
    public static string MetaSearchRefused(string detail, string hint = "") =>
        Llm.Tools.UntrustedText.WithNote($"Error: {MetaSearchName}'s engines refused the search ({detail}); they are resting, for seconds to days depending on the refusal, so searching again now won't help, and restarting the app clears the rests{hint}");

    public const string SafeSearchOffHint = "asks Bing, Brave and Startpage not to filter";
    public const string SafeSearchModerateHint = "asks them for their moderate filter";
    public const string SafeSearchStrictHint = "asks them for their strict filter";

    /// <summary>What the metasearch says with every engine switched off, or only names it doesn't know (2026-11-06).</summary>
    public const string MetaSearchNoEngines = "Error: no search engine is turned on in Web metasearch engines";

    /// <summary>
    /// A ranged metasearch with no engine turned on that filters by date (2026-11-15, the web-tools plan's §4, feature idea #25): the ones that can, so turning them on reads as a
    /// choice.
    /// </summary>
    /// <remarks>
    /// The names joined as the ranged header's scope is, ", ", with "does" for one and "do" for more; with none the clause is left out
    /// (2026-11-16, the web-tools review's #106).
    /// </remarks>
    public static string MetaSearchNoTimeRange(IReadOnlyList<string> filtering)
    {
        ArgumentNullException.ThrowIfNull(filtering);
        string can = filtering.Count switch
        {
            0 => "",
            1 => " " + filtering[0] + " does.",
            _ => " " + string.Join(", ", filtering) + " do.",
        };
        return "Error: none of the engines turned on in Web metasearch engines filters by date;" + (can.Length == 0 ? "" : can) + " Search without since, or turn one on.";
    }

    /// <summary><c>since</c> that is none of the four words (2026-11-15, the web-tools plan's §4, feature idea #25).</summary>
    public static string BadSince(string raw) =>
        "Error: since must be day, week, month or year, not \"" + Llm.Tools.ArgumentText.Echo(raw) + "\".";

    /// <summary>The range as a header names it: <c>past week</c>.</summary>
    public static string SinceShown(SearchSince since) => "past " + SearxngSearch.TimeRangeWord(since);

    /// <summary>
    /// What a header names after the count (2026-11-15, the web-tools plan's §4, feature idea #25): the engine alone; with a range, <c>past week, searxng</c>, or the engines
    /// that answered a metasearch, <c>past week; brave, startpage</c>.
    /// </summary>
    public static string SearchedBy(string engine, SearchSince? since, string scope) =>
        since is not { } recent ? engine : scope.Length > 0 ? SinceShown(recent) + "; " + scope : SinceShown(recent) + ", " + engine;

    /// <summary>
    /// A ranged search's answer adds that the range left engines out, so silence isn't read as nothing published: since 2026-11-16 (the
    /// web-tools review's #16, #26, #99) only when the metasearch skipped one, and on a failure or a rest too.
    /// </summary>
    public const string NoResultsSinceHint = "; without since, more engines answer";

    /// <summary>A SearXNG instance's form of <see cref="NoResultsSinceHint"/>: it skips engines without saying which (2026-11-16, the review's #16).</summary>
    public const string NoResultsSinceMayHint = "; without since, more engines may answer";

    /// <summary>What the metasearch says under network mode <c>lan</c> (2026-11-06): it has no engine on the LAN to ask.</summary>
    public const string MetaSearchNeedsInternet = "Error: the built-in metasearch asks search engines on the internet, which Web browser network mode 'lan' keeps off limits";

    public static string OpenFailed(string url, string detail) => $"Error: could not open '{url}' in your browser: {detail}";
    public static string BadUrlList(string argument, string raw) => $"Error: '{raw.Trim()}' is not a list of links for '{argument}'";
    public static string NoneOpened(int count) => $"Error: none of the {count.ToString(CultureInfo.InvariantCulture)} links could be opened";

    // ---- download_file ----

    /// <summary>
    /// A <c>sha256</c> or <c>sha512</c> that isn't 64 or 128 hex digits (2026-11-15, the web-tools plan's §5, feature idea #29), refused before any download, the value repeated
    /// safely.
    /// </summary>
    public static string BadDigest(string argument, int digits, string raw) =>
        $"Error: {argument} must be {digits.ToString(CultureInfo.InvariantCulture)} hex digits, not \"{Llm.Tools.ArgumentText.Echo(raw)}\"; nothing was downloaded.";

    /// <summary>Both digests given (2026-11-15, the web-tools plan's §5, feature idea #29).</summary>
    public const string BothDigests = "Error: give sha256 or sha512, not both; nothing was downloaded.";

    /// <summary>
    /// A download whose digest isn't the one asked (2026-11-15, the web-tools plan's §5, feature idea #29): both in full, and that nothing was kept.
    /// Since 2026-11-16: the host it came from after a redirect (the web-tools review's #86); that a file already there was left as it was
    /// (#187); and, a download with a digest being asked for its bytes as sent, a server that compressed them anyway named by its
    /// <c>Content-Encoding</c> (#55), so the model doesn't call the file tampered.
    /// </summary>
    public static string HashMismatch(string algorithm, string expected, string actual, string url, string finalUrl, string contentEncoding, bool replaced)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(finalUrl);
        ArgumentNullException.ThrowIfNull(contentEncoding);
        string via = finalUrl.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out var asked) && Uri.TryCreate(finalUrl, UriKind.Absolute, out var final)
            && !string.Equals(asked.IdnHost, final.IdnHost, StringComparison.OrdinalIgnoreCase)
                ? " (via " + final.IdnHost + ")"
                : "";
        string kept = replaced ? "the file was not kept, and the one already there was left as it was." : "the file was not kept.";
        string encoding = contentEncoding.Trim();
        string compressed = encoding.Length > 0 && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)
            ? $" The server sent it with Content-Encoding: {Llm.Tools.ArgumentText.Echo(encoding)} despite identity, so the bytes checked are compressed."
            : "";
        return $"Error: {url}{via} does not match the {algorithm} given (expected {expected}, got {actual}); {kept}{compressed}";
    }

    /// <summary>The note a matching download carries (2026-11-15, the web-tools plan's §5, feature idea #29): <c>; SHA-256 verified</c>.</summary>
    public static string Verified(string algorithm) => "; " + algorithm + " verified";

    /// <summary>The digests' names as the answers say them (2026-11-16, the web-tools review's #14).</summary>
    public const string Sha256Name = "SHA-256";

    /// <summary>See <see cref="Sha256Name"/>.</summary>
    public const string Sha512Name = "SHA-512";

    /// <summary>A refusal before any byte was read, with a digest given (2026-11-16, the web-tools review's #173).</summary>
    public static string DigestNotChecked(string algorithm) => "; nothing was downloaded, so the " + algorithm + " was not checked";

    /// <summary>
    /// An HTTP failure's detail (2026-11-16, the web-tools review's #123): the status, then the server's reason phrase fenced, one line and
    /// cut, since a server writes it; the status alone with no phrase. Each answer holding one ends with the quote note.
    /// </summary>
    public static string HttpDetail(int status, string? phrase)
    {
        string code = "HTTP " + status.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(phrase) ? code : code + " " + Llm.Tools.UntrustedText.Quote(Llm.Tools.UntrustedText.OneLine(Llm.Tools.UntrustedText.Cut(phrase.Trim())));
    }

    /// <summary>
    /// A saved download (2026-09-18): <c>downloaded images/cat.png (213.4 KB, image/png) from https://example.com/cat.png</c>
    /// — <c>replaced</c> over a file that was there, <c>(no type)</c> for a server that named none, and for a picture
    /// <see cref="ViewImageNote"/>; a file outcome that is not Ok is <see cref="Files.FileText.Error"/>'s
    /// sentence with the verb <c>download</c>. Pinned.
    /// </summary>
    /// <remarks><paramref name="verified"/> (<see cref="Verified"/>) comes before the tail note (2026-11-16, the web-tools review's #48).</remarks>
    public static string Downloaded(Files.WriteResult result, string url, string mediaType, string verified = "")
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(mediaType);
        if (result.Outcome != Files.FileOutcome.Ok)
        {
            return Files.FileText.Error(result.Outcome, result.Relative, "download", result.Detail);
        }

        return (result.Replaced ? "replaced " : "downloaded ") + result.Relative + " (" + Size(result.Bytes) + ", " + (mediaType.Length == 0 ? "(no type)" : mediaType) + ") from " + url
            + verified
            + (Files.ImageFile.IsImagePath(result.Relative) ? ViewImageNote : Files.PdfFile.IsPdfPath(result.Relative) ? ReadPdfNote
                : Files.OfficeFile.IsOfficePath(result.Relative) ? ReadOfficeNote : "");
    }

    /// <summary>What a downloaded picture's sentence ends with. Pinned.</summary>
    public const string ViewImageNote = "; view_image shows it";

    /// <summary>What a downloaded PDF's sentence ends with (2026-10-14, the PDF plan's gap 16). Pinned.</summary>
    public const string ReadPdfNote = "; read_pdf reads it";

    /// <summary>What a downloaded Office file's sentence ends with (2026-10-28, the Office plan). Pinned.</summary>
    public const string ReadOfficeNote = "; read_office reads it";

    // ---- open_url ----

    public static string OpenedOne(string url) => $"Opened {url} in your browser";
    public static string OpenedHeader(int count) => $"Opened {count.ToString(CultureInfo.InvariantCulture)} links in your browser:";
    public static string OpenedLine(string url) => "- " + url;

    /// <summary>A link opened in the Browser window under <c>Web browser open</c> <c>browser-window</c> (later still on 2026-10-13): the model's line, not the user's cookie notice.</summary>
    public static string OpenedInWindow(string url) => $"Opened {url} in the app's Browser window";

    /// <summary>The links past the first under <c>browser-window</c>: the window shows one page at a time, so they were not opened.</summary>
    public static string OneAtATime(IReadOnlyList<string> rest)
    {
        ArgumentNullException.ThrowIfNull(rest);
        return "The Browser window shows one page at a time, so these were not opened: " + string.Join(", ", rest);
    }

    /// <summary>A link that went to the default browser because the Browser window could not take it now (<see cref="Viewer.IBrowserWindow.Unavailable"/>).</summary>
    public static string WindowFellBack(string url, string reason) => $"Opened {url} in your browser instead: {reason}";

    /// <summary>What no headless browser adds to a blocked http result under the <c>default</c> mode. Pinned.</summary>
    public const string NoBrowserToTry = "; no headless browser was found to try instead";

    /// <summary>What a rate limit's <c>Retry-After</c> adds to a refused fetch's detail (2026-10-11), so the model knows to wait rather than retry. Pinned.</summary>
    public static string AskedToWait(TimeSpan wait) => $"; the site asks to wait {Llm.LlmTimeouts.Format(wait)} before trying again";

    /// <summary>The sentence for a non-<see cref="FetchOutcome.Ok"/> fetch.</summary>
    /// <remarks>An answer holding a fenced value (an HTTP reason, <see cref="HttpDetail"/>) ends with the quote note (2026-11-16, #123).</remarks>
    public static string Error(FetchOutcome outcome, string url, string detail)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(detail);
        return Llm.Tools.UntrustedText.WithNote(outcome switch
        {
            FetchOutcome.NoUrl => NoUrl,
            FetchOutcome.NotHttp => NotHttp(url),
            FetchOutcome.LanRefused => LanRefused(detail),
            FetchOutcome.InternetRefused => InternetRefused(detail),
            FetchOutcome.Timeout => detail.Length == 0 ? Timeout(url, WebFetcher.FetchTimeout) : detail,   // a download's body under its own ceiling carries its sentence
            FetchOutcome.Blocked => Blocked(url, detail),
            FetchOutcome.Binary => detail,
            FetchOutcome.TooBig => detail,
            FetchOutcome.NoBrowser => NoBrowser,
            FetchOutcome.BrowserFailed => BrowserFailed(url, detail),
            _ => CouldNot(url, detail),
        });
    }

    // ---- results ----

    /// <summary>
    /// The first line of a fetched page: the title, the final URL, the window in characters and the
    /// engine — <c>Example Domain — https://example.com/ (chars 1–2,100 of 2,100, http)</c>; a longer
    /// page adds <c>; continue with offset 32000</c> before the engine.
    /// </summary>
    /// <param name="anchor">
    /// The URL's fragment the window was asked to start at (2026-10-17, the toki-pona session): found, <c>, from #epiku</c> follows the
    /// window; not found (<paramref name="anchorMissing"/>), <c>#epiku not found, from the top; </c> leads it.
    /// </param>
    public static string FetchHeader(string title, string url, int from, int to, int total, string engine, int? nextOffset, string? anchor = null, bool anchorMissing = false)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(engine);
        var sb = new StringBuilder();
        sb.Append(TitleShown(title)).Append(" — ").Append(url).Append(" (");
        // The fragment echoed (2026-11-16, the web-tools review's #171): it can come from a page's own link, so one line, cut, defanged.
        string? shownAnchor = anchor is null ? null : Llm.Tools.ArgumentText.Echo(anchor);
        if (shownAnchor is not null && anchorMissing)
        {
            sb.Append('#').Append(shownAnchor).Append(" not found, from the top; ");
        }

        sb.Append("chars ").Append(from.ToString("N0", CultureInfo.InvariantCulture)).Append('–').Append(to.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" of ").Append(total.ToString("N0", CultureInfo.InvariantCulture));
        if (shownAnchor is not null && !anchorMissing)
        {
            sb.Append(", from #").Append(shownAnchor);
        }

        if (nextOffset is { } next)
        {
            sb.Append("; continue with offset ").Append(next.ToString(CultureInfo.InvariantCulture));
        }

        return sb.Append(", ").Append(engine).Append(')').ToString();
    }

    /// <summary>An empty page: the header alone says so, and a page's own title is fenced, the note under it (2026-11-15).</summary>
    public static string EmptyPage(string title, string url, string engine) =>
        Llm.Tools.UntrustedText.WithNote($"{TitleShown(title)} — {url} (no readable text, {engine})");

    /// <summary>
    /// A page's title as an answer shows it (2026-11-15, the web-tools plan's §3): the page's own words fenced as untrusted, one line; the
    /// app's <see cref="Untitled"/> for none, bare, since a fence would say the page wrote it.
    /// </summary>
    /// <remarks>
    /// Cut to <see cref="TitleChars"/> for every header and search hit (2026-11-16, the web-tools review's #45): the raw title is cut first,
    /// then made one line, so an escape is never split (#141's lesson); a blank title is the app's <see cref="Untitled"/>, bare (#24).
    /// </remarks>
    public static string TitleShown(string? title) =>
        Llm.Tools.UntrustedText.QuoteOr(title is null ? null : Llm.Tools.UntrustedText.OneLine(Llm.Tools.UntrustedText.Cut(title, TitleChars)), Untitled);

    /// <summary>The most of a page's title a header or a search hit shows (2026-11-16, the web-tools review's #45).</summary>
    public const int TitleChars = 200;

    // ── web_fetch's find and outline (2026-11-15, the web-tools plan's §1) ──

    /// <summary><c>find</c> and <c>outline</c> asked together. Prompt text.</summary>
    public const string FindAndOutline = "Error: find and outline each cover the whole page: ask for one of them";

    /// <summary>A <c>find</c> with nothing to look for once quotes and punctuation are set aside. Prompt text.</summary>
    public const string FindNoWords = "Error: find needs a word to look for, not only quotes or punctuation";

    /// <summary>
    /// The header over <c>outline</c>'s list: its first line, the transcript's one-line note. Since 2026-11-16 (the web-tools review's #177)
    /// <paramref name="from"/> names where a later page of the list starts, and <paramref name="nextOffset"/> where the next one would.
    /// </summary>
    public static string OutlineHeader(string title, string url, int headings, int total, string engine, int from = 0, int? nextOffset = null) =>
        $"{TitleShown(title)} — {url} (outline: {headings.ToString("N0", CultureInfo.InvariantCulture)} {(headings == 1 ? "heading" : "headings")} " +
        (from > 0 ? $"from offset {from.ToString(CultureInfo.InvariantCulture)} " : "") +
        $"in {total.ToString("N0", CultureInfo.InvariantCulture)} chars; read a part with offset" +
        (nextOffset is { } next ? $"; the list goes on with outline and offset {next.ToString(CultureInfo.InvariantCulture)}" : "") + $", {engine})";

    /// <summary>A page with no headings: the header alone, with the length, so the model pages by <c>offset</c>.</summary>
    public static string NoHeadings(string title, string url, int total, string engine) =>
        $"{TitleShown(title)} — {url} (outline: no headings in {total.ToString("N0", CultureInfo.InvariantCulture)} chars; read it with offset, {engine})";

    /// <summary>The line ending an outline cut at its cap.</summary>
    public static string OutlineMore(int left) => $"({left.ToString("N0", CultureInfo.InvariantCulture)} more)";

    /// <summary>
    /// A PDF outline item on a page past the ones read: in place of its offset. One whose destination named no page reads "(page unknown)"
    /// (2026-11-16, the web-tools review's #64: it said "not read" though every page was).
    /// </summary>
    public static string HeadingNotRead(int? page) =>
        page is { } p ? $"(page {p.ToString(CultureInfo.InvariantCulture)}, not read)" : "(page unknown)";

    /// <summary>
    /// One line of <c>outline</c>'s list (2026-11-16, the web-tools review's #110, out of <c>WebOutline</c>): two spaces a level below the top,
    /// the offset (or where it is, not read), then the heading's text as given (fenced already, or the app's own page marker).
    /// </summary>
    public static string OutlineLine(int level, string at, string text) =>
        new string(' ', Math.Clamp(level - 1, 0, 5) * 2) + at + " · " + text;

    /// <summary>
    /// The note a page cut at the download cap ends with (2026-11-16, the web-tools review's #131): its text stops short, so a word past the cut
    /// is never found and later headings never listed. The app's words, after the last fence. Pinned.
    /// </summary>
    public static string PageCutAtCap(int capBytes) =>
        $"(the page was cut at {(capBytes / 1_000_000).ToString(CultureInfo.InvariantCulture)} MB; nothing after the cut was read)";

    /// <summary>
    /// The words a <c>find</c> looked for, as its header repeats them (2026-11-16, the web-tools review's #104): joined, one line, cut and
    /// defanged by <see cref="Llm.Tools.ArgumentText.Echo"/>, so a long or quoted query can't stretch or break line one.
    /// </summary>
    public static string FindWordsShown(IReadOnlyList<string> words) => Llm.Tools.ArgumentText.Echo(string.Join(" ", words));

    /// <summary>The header over <c>find</c>'s passages: <c>… (find "warranty": 4 of 7 matches in 45,000 chars, http)</c>.</summary>
    /// <remarks>
    /// Since 2026-11-16: counted as passages (the review's #56: <see cref="WebFind"/> merges hits into them), <paramref name="query"/> the words as
    /// <see cref="FindWordsShown"/> gives them, <paramref name="from"/> where this page of passages starts and <paramref name="nextOffset"/>
    /// where the next one does (#177).
    /// </remarks>
    public static string FindHeader(string title, string url, string query, int shown, int total, int chars, string engine, int from = 0, int? nextOffset = null) =>
        $"{TitleShown(title)} — {url} (find \"{query}\": " +
        (shown == total ? $"{total.ToString("N0", CultureInfo.InvariantCulture)} {(total == 1 ? "passage" : "passages")}" : $"{shown.ToString(CultureInfo.InvariantCulture)} of {total.ToString("N0", CultureInfo.InvariantCulture)} passages") +
        (from > 0 ? $" from offset {from.ToString(CultureInfo.InvariantCulture)}" : "") +
        $" in {chars.ToString("N0", CultureInfo.InvariantCulture)} chars; read on with offset" +
        (nextOffset is { } next ? $"; more passages with find and offset {next.ToString(CultureInfo.InvariantCulture)}" : "") + $", {engine})";

    /// <summary>A <c>find</c> that matched nothing: the header says so, with the length.</summary>
    public static string FindNone(string title, string url, string query, int chars, string engine) =>
        $"{TitleShown(title)} — {url} (find \"{query}\": no passage holds every word in {chars.ToString("N0", CultureInfo.InvariantCulture)} chars, {engine})";

    /// <summary>Under <see cref="FindNone"/>: the page's first headings, where else to look.</summary>
    /// <remarks>Each heading comes as <see cref="WebOutline.Text"/> gives it: the page's fenced, a PDF's page marker the app's (2026-11-16, #63).</remarks>
    public static string FindNoneHint(IReadOnlyList<string> headings) =>
        "Its first headings: " + string.Join("; ", headings) + ". Try other words, or outline.";

    /// <summary>Under <see cref="FindNone"/> on a page with no headings.</summary>
    public const string FindNoneNoHeadings = "Try other words, or read it with offset.";

    /// <summary>One passage's line above its text: where it starts, for <c>offset</c>.</summary>
    public static string PassageAt(int offset) => "@" + offset.ToString(CultureInfo.InvariantCulture);


    // ── The sources under a reply (2026-11-15, the web-tools plan's §2, feature idea #27) ──

    /// <summary>The line's glyph (the user's pick, 2026-11-15; 🔗 is the UNC tools' already).</summary>
    public const string SourcesGlyph = "📚";

    /// <summary>A download's mark on its label: the app's download glyph.</summary>
    public const string DownloadMark = WebSources.DownloadGlyph;

    /// <summary>How many sources the transcript's line names before "+N more"; the feed and the exports list them all.</summary>
    public const int SourcesShown = 5;

    /// <summary>Between two items of the line.</summary>
    public const string SourcesSeparator = " · ";

    /// <summary>The sources past <see cref="SourcesShown"/>, counted.</summary>
    public static string SourcesMore(int more) => "+" + more.ToString(CultureInfo.InvariantCulture) + " more";

    /// <summary>The searches the turn made, counted, never listed.</summary>
    public static string SourcesSearches(int searches) =>
        searches.ToString(CultureInfo.InvariantCulture) + (searches == 1 ? " search" : " searches");

    /// <summary>
    /// The query as a search's header repeats it: the model's own words, whole, with any fence mark made a look-alike (2026-11-16, the
    /// web-tools review's #139), so a query can never open or close a fence around the results below it.
    /// </summary>
    private static string QueryShown(string query) => Llm.Tools.UntrustedText.Defang(query);

    public static string SearchHeader(string query, int count, string engine) =>
        $"Searched \"{QueryShown(query)}\" ({count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "result" : "results")}, {engine}):";

    /// <remarks><paramref name="hint"/> is the outcome's <see cref="SearchOutcome.SinceHint"/>: empty unless a range left engines out (2026-11-16).</remarks>
    public static string NoResults(string query, string engine, SearchSince? since = null, string scope = "", string hint = "") =>
        $"Searched \"{QueryShown(query)}\" ({SearchedBy(engine, since, scope)}): no results{(since is null ? "" : hint)}";

    /// <summary>The numbered results under the header: <c>1. Title — url</c> and the snippet indented under it.</summary>
    public static string Results(string query, IReadOnlyList<SearchResult> results, string engine, SearchSince? since = null, string scope = "", string hint = "")
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            return NoResults(query, engine, since, scope, hint);
        }

        // Each hit's title and snippet are the page's words, fenced (2026-11-15, the web-tools plan's §3); the URL stays bare.
        var sb = new StringBuilder(SearchHeader(query, results.Count, SearchedBy(engine, since, scope)));
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.Append('\n').Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ")
                .Append(TitleShown(r.Title)).Append(" — ").Append(r.Url);
            if (!string.IsNullOrWhiteSpace(r.Snippet))
            {
                sb.Append("\n   ").Append(Llm.Tools.UntrustedText.Quote(Llm.Tools.UntrustedText.OneLine(r.Snippet)));
            }
        }

        return Llm.Tools.UntrustedText.WithNote(sb.ToString());
    }

    /// <summary>A byte count as a size: <c>213.4 KB</c>, <c>1.2 MB</c>, <c>512 B</c>. Pinned.</summary>
    public static string Size(long bytes)
    {
        if (bytes >= 1_000_000)
        {
            return (bytes / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        if (bytes >= 1_000)
        {
            return (bytes / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }
}
