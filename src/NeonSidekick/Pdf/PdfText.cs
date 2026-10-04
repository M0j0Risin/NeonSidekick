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

    public static string AlreadyPdf(string file) => $"Error: {file} is a PDF already";

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

    public static string NoRoute(string printer) =>
        $"Error: no browser was found and {printer} is not installed, so nothing can make the PDF; set Web browser path, or add {printer} in Windows' optional features";

    public static string NoPrinter(string printer) =>
        $"Error: PDF engine is printer, and {printer} is not installed; add it in Windows' optional features, or set PDF engine to auto";

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
}
