using System.Globalization;
using NeonSidekick.Files;

namespace NeonSidekick.Pdf;

/// <summary>
/// The PDF words (2026-10-03): <c>convert_to_pdf</c>'s results and refusals and the <c>/pdf</c> lines. Every failure starts <c>Error:</c>; a result is one sentence, the transcript's note. Pure; the refusals are pinned.
/// </summary>
public static class PdfText
{
    /// <summary>The log category.</summary>
    public const string Category = "Pdf";

    /// <summary>The paper names a request may give, as the tool's schema and <c>/pdf</c> spell them.</summary>
    public const string PaperChoices = "letter, a4 or legal";

    // ─── refusals ───────────────────────────────────────────────────────────────

    public const string OneSource = "Error: give exactly one of path, url or markdown: the file, the web page, or the Markdown text to make a PDF of";

    public const string NoReply = "Error: there is no reply to make a PDF of yet";

    public const string UrlNeedsWebTools = "Error: a web page needs the web tools, which are off; turn on Web tools, or save the page first";

    /// <summary>A PDF given to make a PDF of; it names what reads one instead (2026-10-14, the PDF plan's gap 75). Pinned.</summary>
    public static string AlreadyPdf(string file) => $"Error: {file} is a PDF already; read_pdf reads it";

    public static string NotConvertible(string file) =>
        $"Error: {file} is not a file the sidekick can make a PDF of; it takes Markdown, text and code, HTML and pictures";

    public static string TooLarge(string file) =>
        $"Error: {file} is over {(Printing.PrintText.MaxTextBytes / 1_000_000).ToString(CultureInfo.InvariantCulture)} MB, too long to make a PDF of";

    public static string PicturesTooLarge(int mb) =>
        $"Error: the pictures come to over {mb.ToString(CultureInfo.InvariantCulture)} MB, too much for one PDF";

    public static string NeedsBrowser(string what) =>
        $"Error: {what} needs Edge, Chrome or Brave to make a PDF of, and none was found; set Web browser path, or give Markdown, text or a picture instead";

    public static string NeedsBrowserByEngine(string what) =>
        $"Error: {what} needs the browser to make a PDF of, and PDF engine is printer; set PDF engine to auto or browser";

    public const string NoBrowser = "Error: PDF engine is browser, and no Edge, Chrome or Brave was found; set Web browser path, or set PDF engine to auto";

    public static string NoRoute(string printer) => OperatingSystem.IsMacOS()   // macOS, 2026-10-06: no printer route to offer there
        ? "Error: no Edge, Chrome, Brave or Chromium was found in /Applications, so nothing can make the PDF; set Web browser path"
        : $"Error: no browser was found and {printer} is not installed, so nothing can make the PDF; set Web browser path, or add {printer} in Windows' optional features";

    public static string NoPrinter(string printer) => OperatingSystem.IsMacOS()
        ? $"Error: PDF engine is printer, and {printer} needs Windows; set PDF engine to auto or browser"
        : $"Error: PDF engine is printer, and {printer} is not installed; add it in Windows' optional features, or set PDF engine to auto";

    public const string PageLayoutFixed =
        "Error: a web page keeps its own page layout; leave out paper and landscape for a url";

    public static string BrowserFailed(string browser, string detail) => $"Error: {browser} could not make the PDF: {detail.Trim()}";

    public static string PrinterNoFile(string printer) => $"Error: {printer} took the job but wrote no PDF within a minute";

    public static string PrinterUnfinished(string printer) => $"Error: {printer} was still writing the PDF after a minute";

    public static string PrinterNotPdf(string printer) => $"Error: {printer} wrote a file that is not a PDF";


    // ─── results ────────────────────────────────────────────────────────────────

    /// <summary>A PDF made: <c>Made notes.pdf (182 KB) from notes.md with msedge</c>, and why the fallback ran when it did.</summary>
    public static string Made(WriteResult result, string source, string engine, string? fallbackReason = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(engine);
        if (result.Outcome != FileOutcome.Ok)
        {
            return FileText.Error(result.Outcome, result.Relative, "write", result.Detail);
        }

        string made = (result.Replaced ? "Replaced " : "Made ") + result.Relative + " (" + FileText.Size(result.Bytes) + ") from " + source + " with " + engine;
        return fallbackReason is null ? made : made + "; the browser failed first: " + fallbackReason.Trim();
    }

    /// <summary>The browser's name in a result: its executable without the extension (<c>msedge</c>, <c>chrome</c>, <c>brave</c>).</summary>
    public static string BrowserName(string executable) => Path.GetFileNameWithoutExtension(executable ?? "");

    /// <summary>What a Markdown image becomes when it cannot be shown: its alt text in brackets.</summary>
    public static string MissingImage(string alt) => "[image" + (string.IsNullOrWhiteSpace(alt) ? "" : ": " + alt.Trim()) + "]";

    // ─── /pdf ───────────────────────────────────────────────────────────────────

    public const string Working = "Making a PDF";

    public const string Usage =
        "Usage: /pdf <file|https://url> [to=<out.pdf>] [paper=letter|a4|legal] [landscape] [overwrite] · /pdf reply [options] for the last reply";

    public const string ReplyNote = "make a PDF of the last reply";

    public const string ToWord = "to=";

    public const string PaperWord = "paper=";

    public const string OverwriteWord = "overwrite";

    public static string UnknownOption(string word) => $"Error: '{word}' is not a /pdf option; " + Usage;

    // ─── reading (2026-10-14, the PDF plan's phase 1: read_pdf) ──────────────────

    /// <summary>A <c>pages</c> argument that is not one. Pinned.</summary>
    public static string BadPages(string spec) =>
        $"Error: pages takes physical page numbers from 1: \"3\", \"2-5\", \"5-\" (to the end) or a list like \"1,3,7-9\", not \"{spec}\"";

    /// <summary>No browser to read with. Pinned.</summary>
    public static string NoBrowserToRead => OperatingSystem.IsMacOS()
        ? "Error: reading a PDF needs Edge, Chrome, Brave or Chromium in /Applications, and none was found; set Web browser path"
        : "Error: reading a PDF needs Edge, Chrome or Brave, and none was found; set Web browser path";

    public static string NotAPdf(string path) => $"Error: '{path}' is not a PDF";

    public static string TooBigToRead(string path) =>
        $"Error: '{path}' is over {(Web.BrowserPdfReader.MaxBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB, too large to read";

    /// <summary>A PDF that asks for a password (gap 2). Pinned.</summary>
    public static string Locked(string path) => $"Error: '{path}' is locked with a password and cannot be read";

    public static string ReadFailed(string path, string detail) => $"Error: '{path}' could not be read: {detail.Trim()}";

    /// <summary>Pages that all lie past the end. Pinned.</summary>
    public static string PastTheEnd(string path, int pageCount, string spec) =>
        $"Error: '{path}' has {Pages(pageCount)}; pages \"{spec}\" names none of them";

    /// <summary><c>find</c> asked together with <c>pictures</c> (gap 96). Pinned.</summary>
    public const string FindNoPictures = "Error: find lists the pages that hold the words and draws none; read one of them with pages and pictures";

    /// <summary>
    /// The answer's first line, which the transcript shows: <c>report.pdf (pages 1–12 of 87; next: pages "13-")</c>, the pages a list
    /// when they do not run, <c>next</c> only when pages are left to read.
    /// </summary>
    public static string Header(string path, IReadOnlyList<int> shown, int pageCount, int? next) =>
        path + " (" + (shown.Count == 1 ? "page " : "pages ") + Runs(shown) + " of " + pageCount.ToString(CultureInfo.InvariantCulture)
        + (next is { } n ? $"; next: pages \"{n.ToString(CultureInfo.InvariantCulture)}-\"" : "") + ")";

    /// <summary>A page's heading, its printed label beside the number when they differ (gap 92): <c>--- page 14 (printed "xii") ---</c>.</summary>
    public static string PageHeading(int page, string? label) =>
        "--- page " + page.ToString(CultureInfo.InvariantCulture) + (Printed(page, label) is { } printed ? " (printed \"" + printed + "\")" : "") + " ---";

    /// <summary>A page named in a list (<c>find</c>'s), its label as the heading puts it: <c>page 14 (printed "12")</c>.</summary>
    public static string PageName(int page, string? label) =>
        "page " + page.ToString(CultureInfo.InvariantCulture) + (Printed(page, label) is { } printed ? " (printed \"" + printed + "\")" : "");

    private static string? Printed(int page, string? label) =>
        string.IsNullOrWhiteSpace(label) || label.Trim() == page.ToString(CultureInfo.InvariantCulture) ? null : label.Trim();

    /// <summary>Pages with no text at all (gap 31's scans). Pinned.</summary>
    public const string NoTextLayer =
        "(no text on these pages: a scan or pictures only; call read_pdf again with pictures: true to look at them)";

    /// <summary>A page longer than the read's cap, cut (gap 90). Pinned.</summary>
    public static string PageCut(int page, int shown, int total) =>
        $"(page {page.ToString(CultureInfo.InvariantCulture)} cut at {shown.ToString("N0", CultureInfo.InvariantCulture)} of {total.ToString("N0", CultureInfo.InvariantCulture)} characters; pictures: true shows it all)";

    /// <summary>The outline's heading (gap 91). Pinned.</summary>
    public const string OutlineHeading = "Outline:";

    /// <summary>An outline entry: two spaces a level below the top, then the title and its page.</summary>
    public static string OutlineLine(string title, int level, int? page) =>
        new string(' ', Math.Max(0, level - 1) * 2) + "- " + title + (page is { } p ? " (page " + p.ToString(CultureInfo.InvariantCulture) + ")" : "");

    /// <summary>An outline cut at its cap. Pinned.</summary>
    public const string OutlineMore = "(more in the PDF's outline)";

    /// <summary>Where the pages' pictures went: <c>pictures of pages 1–4 are in the next message</c>, and the pages left out over the cap.</summary>
    public static string PicturesNote(IReadOnlyList<int> drawn, IReadOnlyList<int> leftOut) =>
        (drawn.Count == 1 ? "the picture of page " : "pictures of pages ") + Runs(drawn) + (drawn.Count == 1 ? " is" : " are") + " in the next message"
        + (leftOut.Count == 0 ? "" : "; at most " + drawn.Count.ToString(CultureInfo.InvariantCulture) + " a call: ask for pages \"" + Spec(leftOut) + "\" with pictures next");

    /// <summary>Pages that drew no picture. Pinned.</summary>
    public static string NotDrawn(IReadOnlyList<int> pages) => "(no picture of " + (pages.Count == 1 ? "page " : "pages ") + Runs(pages) + ")";

    /// <summary><c>find</c>'s words found nowhere. Pinned.</summary>
    public static string NotFound(string path, string words, int searched, int pageCount) =>
        $"{path}: \"{words}\" is on none of the pages ({Searched(searched, pageCount)})";

    /// <summary><c>find</c>'s header, its first line: the pages holding every word, and how many were searched.</summary>
    public static string Found(string path, string words, int matches, int searched, int pageCount) =>
        $"{path}: \"{words}\" is on {Pages(matches)} ({Searched(searched, pageCount)})";

    private static string Searched(int searched, int pageCount) =>
        searched >= pageCount
            ? "searched all " + Pages(pageCount)
            : "searched " + searched.ToString(CultureInfo.InvariantCulture) + " of " + Pages(pageCount);

    /// <summary>Matches past <c>find</c>'s cap. Pinned.</summary>
    public static string FoundMore(int more) =>
        $"(and {Pages(more)} more: add a word, or limit the search with pages)";

    /// <summary>A read that stopped early and why, after the pages it got.</summary>
    public static string StoppedShort(string detail) => "(" + detail.Trim() + ")";

    /// <summary>A page's line in <c>find</c>'s list: the page and its line that holds the words.</summary>
    public static string FoundLine(int page, string? label, string context) => PageName(page, label) + ": " + context;

    /// <summary>
    /// A page picture's line in a stored session or a request trimmed for size (gap 21): <c>(page 3 of report.pdf; read_pdf with pictures
    /// reads it again)</c>. Pinned.
    /// </summary>
    public static string PageNotKept(string file, int page) =>
        page > 0
            ? $"(page {page.ToString(CultureInfo.InvariantCulture)} of {file}; read_pdf with pictures reads it again)"
            : $"(a page of {file}; read_pdf with pictures reads it again)";

    /// <summary><c>3 pages</c>, <c>1 page</c>.</summary>
    public static string Pages(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " page" : " pages");

    /// <summary>Pages as runs for reading: <c>1–3, 7, 9–12</c>.</summary>
    public static string Runs(IReadOnlyList<int> pages) => Join(pages, "–", ", ");

    /// <summary>Pages as a <c>pages</c> argument: <c>1-3,7,9-12</c>.</summary>
    public static string Spec(IReadOnlyList<int> pages) => Join(pages, "-", ",");

    private static string Join(IReadOnlyList<int> pages, string dash, string comma)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var parts = new List<string>();
        int i = 0;
        while (i < pages.Count)
        {
            int start = pages[i];
            int end = start;
            while (i + 1 < pages.Count && pages[i + 1] == end + 1)
            {
                end = pages[++i];
            }

            parts.Add(end == start
                ? start.ToString(CultureInfo.InvariantCulture)
                : start.ToString(CultureInfo.InvariantCulture) + dash + end.ToString(CultureInfo.InvariantCulture));
            i++;
        }

        return string.Join(comma, parts);
    }
}
