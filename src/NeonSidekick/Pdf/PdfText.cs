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

    /// <summary>A web address that is a PDF already (2026-10-14, the PDF plan's gaps 15 and 23). Pinned.</summary>
    public static string UrlIsPdf(string url) => $"Error: {url} is a PDF already; download_file saves it, and web_fetch reads it";

    /// <summary>A PDF given to make a PDF of; it names what reads and edits one instead (2026-10-14, the PDF plan's gap 75). Pinned.</summary>
    public static string AlreadyPdf(string file) => $"Error: {file} is a PDF already; read_pdf reads it, and pdf_edit merges, cuts, turns or fills one";

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
        "Usage: /pdf <file|https://url> [to=<out.pdf>] [paper=letter|a4|legal] [landscape] [overwrite] · /pdf reply [options] for the last reply · " +
        "/pdf merge <file> <file>… · /pdf extract|delete <file> pages=2-5 · /pdf rotate <file> angle=90|180|270 [pages=…], each with [to=…] [overwrite]";

    public const string ReplyNote = "make a PDF of the last reply";

    /// <summary>An edit verb's line in <c>/pdf</c>'s word list (2026-10-14).</summary>
    public static string VerbNote(string verb) => verb switch
    {
        PdfEditPlan.MergeWord => "join PDFs into one",
        PdfEditPlan.ExtractWord => "copy pages into a new PDF",
        PdfEditPlan.DeleteWord => "a PDF without some pages",
        _ => "rotate pages",
    };

    /// <summary>A word an edit does not take (2026-10-14). Pinned.</summary>
    public static string NotAnEditOption(string word, string verb) =>
        $"Error: '{word}' is not for /pdf {verb}; it takes pages=, angle= (rotate), paths= (merge), to= and overwrite";

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

    /// <summary><c>fields</c> asked with pages, find or pictures (gap 98). Pinned.</summary>
    public const string FieldsAlone = "Error: fields lists the form's fields and reads no page; leave out pages, find and pictures with it";

    /// <summary>The most fields <c>read_pdf</c> lists.</summary>
    public const int MaxFieldLines = 200;

    /// <summary>
    /// A form's fields for <c>read_pdf</c> (gap 98): a header naming how many and what sets them, then a line each — the name, its kind, its
    /// value (<c>empty</c> for none), the values it takes, its page — or why there is nothing to fill. Pinned.
    /// </summary>
    public static string FieldList(string path, IReadOnlyList<Web.PdfFormField> fields, bool pureXfa)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (pureXfa)
        {
            return $"{path}: an XFA form; pdf_edit cannot fill it (it fills AcroForm fields)";
        }

        if (fields.Count == 0)
        {
            return $"{path} has no form fields";
        }

        var sb = new System.Text.StringBuilder($"{path}: {fields.Count.ToString(CultureInfo.InvariantCulture)} form field{(fields.Count == 1 ? "" : "s")} (pdf_edit's fill_form sets them by name)");
        foreach (var field in fields.Take(MaxFieldLines))
        {
            sb.Append("\n- ").Append(field.Name).Append(": ").Append(field.Kind.Length > 0 ? field.Kind : "field").Append(", ")
              .Append(field.Value.Length > 0 ? "\"" + field.Value + "\"" : "empty");
            if (field.Options.Count > 0)
            {
                sb.Append(field.Kind == "checkbox" ? "; on value " : "; one of ").Append(string.Join(", ", field.Options.Select(o => "\"" + o + "\"")));
            }

            if (field.ReadOnly)
            {
                sb.Append("; read-only");
            }

            if (field.Page is { } page)
            {
                sb.Append(" (page ").Append(page.ToString(CultureInfo.InvariantCulture)).Append(')');
            }
        }

        if (fields.Count > MaxFieldLines)
        {
            sb.Append("\n(and ").Append((fields.Count - MaxFieldLines).ToString(CultureInfo.InvariantCulture)).Append(" more)");
        }

        return sb.ToString();
    }

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

    // ─── attaching (2026-10-14, the PDF plan's phase 3) ─────────────────────────

    /// <summary>
    /// An attached PDF as an endpoint that takes no PDF reads it: a line naming it and where it came from, then its pages under their
    /// headings, then what was left. Pinned.
    /// </summary>
    public static string AttachedText(string name, string path, Files.PdfDocumentText read)
    {
        ArgumentNullException.ThrowIfNull(read);
        string head = read.Empty
            ? $"(the PDF {name}, attached from {path}: no text could be read{(read.Problem is { Length: > 0 } problem ? " (" + problem.Trim() + ")" : "")})"
            : $"(the PDF {name}, attached from {path}: its text, {(read.PagesRead >= read.PageCount ? Pages(read.PageCount) : $"pages 1–{read.PagesRead.ToString(CultureInfo.InvariantCulture)} of {read.PageCount.ToString(CultureInfo.InvariantCulture)}")})";
        string tail = !read.Empty && read.PagesRead < read.PageCount
            ? $"\n(the rest is not attached; read_pdf reads it, pages \"{(read.PagesRead + 1).ToString(CultureInfo.InvariantCulture)}-\")"
            : "";
        return read.Empty ? head : head + "\n" + read.Text + tail;
    }

    /// <summary>The send's spinner while an attached PDF's text is still being read (gap 17). Pinned.</summary>
    public const string ReadingAttached = "Reading the attached PDF";

    /// <summary>The send given up under that spinner (ESC, the Web UI's Stop): the draft is kept. Pinned.</summary>
    public const string AttachWaitStopped = "(the message was not sent: its PDF was still being read; the draft is kept)";

    /// <summary>A locked PDF on the line (gap 84): no endpoint can read it. Pinned.</summary>
    public static string AttachedLocked(string name) => $"{name} is locked with a password, so it can't be sent; take it off the line and send again";

    /// <summary>A PDF with no text on the line for an endpoint that takes no PDF. Pinned.</summary>
    public static string NoTextForThisServer(string name) =>
        $"{name} has no text to send (a scan, or it could not be read), and this server takes text only; take it off the line, or ask the model to look at its pages with read_pdf and pictures";

    /// <summary>A PDF past what the Anthropic API reads of one (100 pages): sent all the same. Pinned.</summary>
    public static string ManyPagesWarning(string name, int pages) =>
        $"{name} has {pages.ToString(CultureInfo.InvariantCulture)} pages; the Anthropic API reads at most 100 of a PDF";

    /// <summary>A PDF over what one request may carry (gaps 18, 51), refused when it is pasted. Pinned.</summary>
    public static string TooBigToAttach(string name, long bytes, long cap) =>
        $"{name} is {Web.WebText.Size(bytes)}; the line's pictures and PDFs may come to {Web.WebText.Size(cap)} (LLM max images (MB)), so it was left as its path";

    /// <summary>What an attached PDF's text is cut to for a server whose window it would fill (gap 43): the note after the cut. Pinned.</summary>
    public static string AttachedCut(string name) => $"\n(the text of {name} was cut to fit this server's window; read_pdf reads the rest)";

    /// <summary>The draft's label for an attached PDF. Pinned.</summary>
    public static string DocumentLabel(int number, string name) => $"[PDF #{number} {name}]";

    /// <summary>The label a sent PDF carries in the text (gap 27): the file's path, so the model, an advisor or a later turn can read_pdf it. Pinned.</summary>
    public static string DocumentSentLabel(int number, string path) => $"[PDF #{number}, from {path}]";

    /// <summary>A PDF part taken out of a request to keep it small (gap 18): its text stays, cut to <see cref="LeftOutChars"/>. Pinned.</summary>
    public static string DocumentLeftOut(string text) =>
        "(an attached PDF taken out to keep the request small; its text was:)\n" + (text.Length > LeftOutChars ? text[..LeftOutChars] + "…" : text);

    /// <summary>How much of an attached PDF's text stays when the PDF is taken out of a request, or goes into a summary.</summary>
    public const int LeftOutChars = 4_000;

    /// <summary><c>/read</c>'s spinner while a PDF is read for speech (2026-10-14, the PDF plan's gap 38). Pinned.</summary>
    public const string ReadingAloudLabel = "Reading the PDF";

    /// <summary>A PDF's read for <c>/read</c> stopped by ESC before it spoke (gap 38). Pinned.</summary>
    public const string ReadAloudStopped = "(reading the PDF stopped)";

    // ─── editing (2026-10-14, the PDF plan's phase 4b: pdf_edit and /pdf's edits) ──

    /// <summary>A merge with fewer than two PDFs. Pinned.</summary>
    public const string MergeNeedsTwo = "Error: merge takes two or more PDFs in paths, in the order to join them";

    public static string MergeTooMany(int max) => $"Error: merge takes {max.ToString(CultureInfo.InvariantCulture)} PDFs at most";

    /// <summary>Any other edit given no PDF or several. Pinned.</summary>
    public static string OnePdf(string action) => $"Error: {action} takes one PDF in path";

    /// <summary>An extract or delete without pages. Pinned.</summary>
    public static string PagesNeeded(string action) => $"Error: {action} needs pages: \"3\", \"2-5\", \"5-\" (to the end) or \"1,3,7-9\"";

    /// <summary>An argument the action does not take. Pinned.</summary>
    public static string NotFor(string argument, string action) => $"Error: {argument} is not for {action}";

    /// <summary>A turn by another angle. Pinned.</summary>
    public static string BadAngle(int angle) =>
        $"Error: angle takes 90, 180 or 270 (clockwise; -90 turns the other way), not {angle.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>An angle that is no number. Pinned.</summary>
    public static string BadAngleText(string raw) => $"Error: '{raw.Trim()}' is not a number of degrees for 'angle'; give 90, 180 or 270";

    /// <summary><c>fields</c> that is not one object of names and values. Pinned.</summary>
    public static string BadFields(string raw) =>
        $"Error: fields takes one object of field names and their values, like {{\"name\": \"Ada\", \"agree\": true}}, not '{raw.Trim()}'";

    /// <summary>A field's value that is not text, a number, true or false, or a list. Pinned.</summary>
    public static string BadFieldValue(string name) =>
        $"Error: the value for '{name}' is not text, a number, true or false, or a list of options";

    /// <summary>A form fill with no values. Pinned.</summary>
    public const string FieldsNeeded = "Error: fill_form needs fields: each field's name and its value, as read_pdf with fields: true lists them";

    /// <summary>A delete of every page. Pinned.</summary>
    public static string CannotDeleteAll(string path, int pageCount) =>
        $"Error: that would delete every page of '{path}' ({Pages(pageCount)}); a PDF keeps one page at least";

    /// <summary>No browser to edit with: pdf-lib runs in one whatever <c>PDF engine</c> says. Pinned.</summary>
    public static string NoBrowserToEdit => OperatingSystem.IsMacOS()
        ? "Error: editing a PDF needs Edge, Chrome, Brave or Chromium in /Applications, and none was found; set Web browser path"
        : "Error: editing a PDF needs Edge, Chrome or Brave, and none was found; set Web browser path";

    public static string TooBigToEdit(string path) =>
        $"Error: '{path}' is over {(Web.BrowserPdfReader.MaxBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB, too large to edit";

    public static string TooBigTogether(int mb) => $"Error: the PDFs come to over {mb.ToString(CultureInfo.InvariantCulture)} MB together, too large to merge";

    /// <summary>An input locked with a password (gap 14): refused, never decrypted. Pinned.</summary>
    public static string LockedToEdit(string path) => $"Error: '{path}' is locked with a password and cannot be edited";

    /// <summary>A form that is XFA alone (gap 25). Pinned.</summary>
    public static string XfaForm(string path) =>
        $"Error: '{path}' is an XFA form, which pdf_edit cannot fill (it fills AcroForm fields); fill it in Adobe Acrobat or Reader";

    /// <summary>A PDF with no form. Pinned.</summary>
    public static string NoFormFields(string path) => $"Error: '{path}' has no form fields to fill";

    /// <summary>Names the form does not have, with the ones it does (40 at most). Pinned.</summary>
    public static string UnknownFields(string path, IReadOnlyList<string> unknown, IReadOnlyList<string> names) =>
        $"Error: '{path}' has no field {string.Join(", ", unknown.Select(n => "'" + n + "'"))}; its fields are "
        + string.Join(", ", names.Take(40).Select(n => "'" + n + "'")) + (names.Count > 40 ? $" and {(names.Count - 40).ToString(CultureInfo.InvariantCulture)} more (read_pdf with fields: true lists them)" : "");

    /// <summary>Values a field would not take, each with why; nothing was written. Pinned.</summary>
    public static string BadFieldValues(string path, IReadOnlyList<Web.PdfFieldRefusal> refusals) =>
        $"Error: nothing was written; '{path}' would not take " + string.Join("; ", refusals.Select(r =>
            r.Options.Count > 0 ? $"'{r.Name}', which takes one of {string.Join(", ", r.Options.Select(o => "'" + o + "'"))}"
            : r.TakesNone ? $"'{r.Name}', which takes no value (a button or a signature)"
            : $"'{r.Name}' ({r.Error.Trim()})"));

    public static string EditFailed(string browser, string detail) => $"Error: {browser} could not edit the PDF: {detail.Trim()}";

    /// <summary>
    /// A PDF edited: <c>Wrote report-p2-5.pdf (4 pages, 40 KB): pages 2–5 of report.pdf</c> (<c>Replaced</c> over a file). The first word and
    /// the name are what the transcript's note shows.
    /// </summary>
    public static string Edited(WriteResult result, int pageCount, string what)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return FileText.Error(result.Outcome, result.Relative, "write", result.Detail);
        }

        return (result.Replaced ? "Replaced " : "Wrote ") + result.Relative + " (" + Pages(pageCount) + ", " + FileText.Size(result.Bytes) + "): " + what;
    }

    /// <summary><c>/pdf</c>'s spinner while an edit runs.</summary>
    public const string Editing = "Editing the PDF";

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
