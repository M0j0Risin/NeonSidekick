using System.Globalization;
using NeonSidekick.Files;

namespace NeonSidekick.Office;

/// <summary>
/// Every string <c>read_office</c> and its readers show the model or the user (2026-10-28, the Office plan; the house rule: an area's text in
/// its <c>…Text</c> class). The refusals and headers are model-facing and pinned.
/// </summary>
public static class OfficeText
{
    /// <summary>The name the damage answer gives a zip that won't open at all.</summary>
    public const string ZipPart = "the zip";

    /// <summary>A file past <see cref="OfficeLimits.MaxFileBytes"/>. Pinned.</summary>
    public static string TooBig(string path, long max) =>
        $"Error: '{path}' is over {(max / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB, too large to read";

    /// <summary>A part held whole past its cap, or a zip bomb's. Pinned.</summary>
    public static string PartTooLarge(string path, string part) => $"Error: '{path}' holds a part too large to read ({part})";

    /// <summary>A broken zip, a part that isn't well-formed XML, a part named but missing, an index past the end. Pinned.</summary>
    public static string Damaged(string path, string part) =>
        $"Error: the file looks damaged ({part}): open '{path}' in Office and save it again";

    /// <summary>Damage found after some of a streamed body was read: the text above stands, and this says where it stopped. Pinned.</summary>
    public static string DamagedPast(string part) =>
        $"(the file looks damaged past this point ({part}), so the text stops here: open it in Office and save it again)";

    /// <summary>A streamed body that inflated past its bound: the rest isn't read. Pinned.</summary>
    public static string RestTooLarge(string part) => $"(the rest of {part} is too large to read)";

    /// <summary>A path whose extension <c>read_office</c> doesn't read. Pinned.</summary>
    public static string NotAnOfficePath(string path) =>
        $"Error: read_office reads Word (.docx), Excel (.xlsx) and PowerPoint (.pptx) files, not '{path}'";

    /// <summary>A legacy binary file (<c>.doc</c>, <c>.xls</c>, <c>.ppt</c>). Pinned.</summary>
    public static string Legacy(string path) =>
        $"Error: '{path}' is in an older Office format that can't be read; open it in Office and save it as .docx, .xlsx or .pptx";

    /// <summary>An Excel binary workbook. Pinned.</summary>
    public static string BinaryWorkbook(string path) => $"Error: '{path}' is an Excel binary workbook that can't be read; save it as .xlsx in Excel";

    /// <summary>An OpenDocument file. Pinned.</summary>
    public static string OpenDocument(string path) => $"Error: '{path}' is an OpenDocument file, not read yet; save it as .docx, .xlsx or .pptx";

    /// <summary>An Office extension over an OLE container: a password, encryption or a sensitivity label. Pinned.</summary>
    public static string Protected(string path) =>
        $"Error: '{path}' is password-protected or encrypted (a sensitivity label does this too); open it in Office and save an unprotected copy";

    /// <summary>An Office extension over something else: a zip with no document inside, or not a zip. Pinned.</summary>
    public static string NotOffice(string path) => $"Error: '{path}' is not an Office document (no Word, Excel or PowerPoint content inside)";

    /// <summary>
    /// An Office file of another kind than its name says (2026-10-28, review P12-06: an <c>.xlsx</c> renamed <c>.docx</c> was called "no
    /// Office content inside", which is false). Pinned.
    /// </summary>
    public static string OtherKind(string path, OfficeKind actual) =>
        $"Error: '{path}' holds {KindName(actual)} inside, not what its name says; copy it to a {ExtensionOf(actual)} name and read that";

    /// <summary>Office's lock file (<c>~$name.docx</c>) whose document's name is known: Excel's and PowerPoint's, or a short Word name. Pinned.</summary>
    public static string LockFile(string path, string document) => $"Error: '{path}' is Office's lock file for an open document; read '{document}'";

    /// <summary>
    /// Word's lock file for a longer name (2026-10-28, review P2-05): Word puts <c>~$</c> over the name's first one or two characters
    /// (<c>~$port.docx</c> for <c>report.docx</c>), so only the name's end is known. Pinned.
    /// </summary>
    public static string LockFileOf(string path, string tail) =>
        $"Error: '{path}' is Office's lock file for an open document; read the document whose name ends in '{tail}'";

    /// <summary>An argument that doesn't fit the file's kind. Pinned.</summary>
    public static string WrongArgument(string argument, OfficeKind kind) =>
        $"Error: {argument} doesn't apply to {KindName(kind)}; " + kind switch
        {
            OfficeKind.Word => "a Word document takes from_char",
            OfficeKind.Excel => "a workbook takes sheet, range and formulas",
            _ => "a presentation takes slides, notes and from_char",
        };

    /// <summary><c>from_char</c> on more than one slide. Pinned.</summary>
    public const string FromCharNeedsOneSlide = "Error: from_char reads on within one slide: give slides a single slide number with it";

    /// <summary>A bad <c>slides</c>. Pinned.</summary>
    public static string BadSlides(string spec) =>
        $"Error: slides takes slide numbers from 1: \"3\", \"2-5\", \"5-\" (to the end) or a list like \"1,3,7-9\", not \"{spec}\"";

    /// <summary>Slides that all lie past the end. Pinned.</summary>
    public static string SlidesPastTheEnd(string path, int count, string spec) =>
        $"Error: '{path}' has {Count(count, "slide")}; slides \"{spec}\" names none of them";

    /// <summary>A presentation with no slides.</summary>
    public static string NoSlides(string path) => $"{path} has no slides";

    /// <summary><c>from_char</c> past a document's or a slide's end. Pinned.</summary>
    public static string PastTheText(string path, int length, int fromChar) =>
        $"Error: '{path}' has {Count(length, "character")} of text here; from_char {fromChar.ToString(CultureInfo.InvariantCulture)} is past its end";

    /// <summary>The Word answer's first line: what was shown and, when more is left, the <c>from_char</c> to ask for next.</summary>
    public static string WordHeader(string path, int from, int to, int? total, int? next) =>
        path + " (Word document, " + (from == 0 && next is null && total is { } all ? Count(all, "character")
            : "characters " + N(from + 1) + "–" + N(to) + (total is { } t ? " of " + N(t) : ""))
        + (next is { } n ? "; next: from_char " + n.ToString(CultureInfo.InvariantCulture) : "") + ")";

    /// <summary>The slides answer's first line.</summary>
    public static string SlidesHeader(string path, IReadOnlyList<int> shown, int count, string? next) =>
        path + " (" + (shown.Count == 1 ? "slide " : "slides ") + Pdf.PdfText.Runs(shown) + " of " + count.ToString(CultureInfo.InvariantCulture)
        + (next is { Length: > 0 } n ? $"; next: slides \"{n}\"" : "") + ")";

    /// <summary>A slide's heading: <c>## Slide 3 — Results</c>, marked when hidden.</summary>
    public static string SlideHeading(int number, string title, bool hidden) =>
        "## Slide " + number.ToString(CultureInfo.InvariantCulture) + (title.Length > 0 ? " — " + title : "") + (hidden ? " (hidden)" : "");

    /// <summary>The line before a slide's speaker notes.</summary>
    public const string NotesHeading = "Notes:";

    /// <summary>
    /// A slide that didn't read, in its place among the others (2026-10-28, review P2-04/P3-07: one damaged or oversized slide threw the
    /// whole deck's answer away). Pinned.
    /// </summary>
    public static string SlideNotRead(int slide, string part, bool tooLarge) =>
        $"## Slide {slide.ToString(CultureInfo.InvariantCulture)}\n(not read: " + (tooLarge ? $"{part} is too large to read)" : $"{part} looks damaged; open the file in Office and save it again)");

    /// <summary>A slide longer than the read's cap, cut. Pinned.</summary>
    public static string SlideCut(int slide, int shown, int total) =>
        $"(slide {slide.ToString(CultureInfo.InvariantCulture)} cut at {N(shown)} of {N(total)} characters; next: slides \"{slide.ToString(CultureInfo.InvariantCulture)}\" with from_char {shown.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>The workbook overview's first line; <paramref name="columns"/> the columns past the shown ones, of the rows shown.</summary>
    public static string WorkbookHeader(string path, int sheets, string? shown, string? used, string? next, string? columns = null) =>
        path + " (Excel workbook, " + Count(sheets, "sheet")
        + (shown is not null ? "; showing " + shown + (used is not null ? " of its used range " + used : "") : "")
        + Next(next, columns) + ")";

    /// <summary>A range's answer's first line; <paramref name="columns"/> as <see cref="WorkbookHeader"/>'s.</summary>
    public static string RangeHeader(string path, string sheet, string shown, string? used, string? next, string? columns = null) =>
        path + " (sheet " + sheet + ", " + shown + (used is not null ? " of used range " + used : "")
        + Next(next, columns) + ")";

    // A table cut by rows and by columns names both (2026-10-28, review P2-02: only the rows were named, so the columns past the 40th of
    // the rows shown were never offered).
    private static string Next(string? next, string? columns) =>
        (next is { } n ? "; next: range \"" + n + "\"" : "") + (columns is { } c ? "; the columns past these: range \"" + c + "\"" : "");

    /// <summary>The overview's list of sheets.</summary>
    public const string SheetsHeading = "Sheets:";

    /// <summary>One sheet in the overview: its used range, "empty", what kind of sheet isn't read, or that it looks damaged (review P2-08).</summary>
    public static string SheetLine(int number, string name, string? used, string state, SheetKind kind, bool damaged = false) =>
        number.ToString(CultureInfo.InvariantCulture) + ". " + name + ": "
        + (kind != SheetKind.Worksheet ? SheetKindName(kind) + " (not read)" : damaged ? "looks damaged (not read)" : used ?? "empty")
        + (state == "veryHidden" ? " (very hidden)" : state == "hidden" ? " (hidden)" : "");

    /// <summary>The overview's defined names, which <c>range</c> takes by their references.</summary>
    public const string NamesHeading = "Defined names (give range the reference, not the name):";

    /// <summary>One defined name in the overview, with the sheet it is scoped to (2026-10-28, review P5-09: it was written inline).</summary>
    public static string NameLine(string name, string reference, string? scope) =>
        "- " + name + ": " + reference + (scope is { } s ? " (in " + s + ")" : "");

    /// <summary>The line after a list cut short: how many more there are (review P5-09, P12-01).</summary>
    public static string More(int count) => "(and " + count.ToString(CultureInfo.InvariantCulture) + " more)";

    /// <summary>The line naming the rows and columns hidden in what is shown.</summary>
    public static string HiddenLine(string what) => "hidden: " + what;

    /// <summary>The hidden rows' part of <see cref="HiddenLine"/>: <c>rows 4–6</c> (review P5-09).</summary>
    public static string HiddenRows(string runs, int count) => (count == 1 ? "row " : "rows ") + runs;

    /// <summary>The hidden columns' part of <see cref="HiddenLine"/>: <c>column C</c>.</summary>
    public static string HiddenColumns(string runs, int count) => (count == 1 ? "column " : "columns ") + runs;

    /// <summary>A range that holds no values. Pinned.</summary>
    public static string NoValues(string path, string sheet, string range, string? used) =>
        $"{path}: sheet {sheet} holds no values in {range}" + (used is not null ? $"; its used range is {used}" : "; it is empty");

    /// <summary>A whole sheet that holds no values (2026-10-28, review P5-09: the range read "it"). Pinned.</summary>
    public static string SheetHoldsNoValues(string path, string sheet) => $"{path}: sheet {sheet} holds no values";

    /// <summary>A workbook with no worksheet to show first.</summary>
    public const string NoVisibleSheet = "(no visible worksheet to show)";

    /// <summary>The first rows' heading in the overview.</summary>
    public static string FirstRows(string sheet) => "First rows of " + sheet + ":";

    /// <summary>The most sheets <see cref="NoSuchSheet"/> names (2026-10-28, review P12-02: thousands of sheets made a huge error).</summary>
    public const int MaxSheetsNamed = 50;

    /// <summary>A sheet asked for that isn't there: the first <paramref name="max"/> names, then how many more. Pinned.</summary>
    public static string NoSuchSheet(string path, string sheet, IReadOnlyList<string> names, int max = MaxSheetsNamed) =>
        $"Error: '{path}' has no sheet \"{sheet}\"; its sheets: " + string.Join(", ", names.Take(max).Select(n => "\"" + n + "\""))
        + (names.Count > max ? " " + More(names.Count - max) : "");

    /// <summary>A sheet that isn't a worksheet asked for: a chart, dialog or macro sheet (2026-10-28, review P5-11: all were "chart"). Pinned.</summary>
    public static string NotReadSheet(string sheet, SheetKind kind) => $"Error: {sheet} is a {SheetKindName(kind)}, which isn't read";

    private static string SheetKindName(SheetKind kind) => kind switch
    {
        SheetKind.Chart => "chart sheet",
        SheetKind.Dialog => "dialog sheet",
        SheetKind.Macro => "macro sheet",
        _ => "worksheet",
    };

    /// <summary><c>sheet</c> and <c>range</c> naming different sheets. Pinned.</summary>
    public static string SheetsDisagree(string sheet, string rangeSheet) =>
        $"Error: sheet \"{sheet}\" and range's sheet \"{rangeSheet}\" disagree; give one";

    /// <summary>A range that isn't one. Pinned.</summary>
    public static string BadRange(string range) =>
        $"Error: range takes a cell (\"B7\"), cells (\"A1:F200\"), columns (\"A:C\") or rows (\"2:10\"), with a sheet in front if wanted (\"'Q3 Sales'!A1:F200\"), not \"{range}\"";

    /// <summary>A range past Excel's last cell. Pinned.</summary>
    public static string RangePastTheEdge(string range) => $"Error: range \"{range}\" is past Excel's last cell, XFD1048576";

    /// <summary>A defined name given as a range. Pinned.</summary>
    public static string NameAsRange(string name) =>
        $"Error: \"{name}\" isn't a cell reference; defined names aren't resolved: read the workbook without sheet to list them with their references, then pass the reference";

    /// <summary>A cell's formula beside its value (<c>formulas: true</c>).</summary>
    public static string WithFormula(string value, string formula) => value.Length == 0 ? "=" + formula : value + " (=" + formula + ")";

    /// <summary>A shared formula's following cell (<c>formulas: true</c>).</summary>
    public static string WithShared(string value, string first) => value + " (formula shared with " + first + ")";

    /// <summary>A sheet range's whole value (a one-cell range).</summary>
    public static string OneCell(string cell, string value) => cell + ": " + (value.Length == 0 ? "(empty)" : value);

    /// <summary>The markers for what isn't text: a picture, a chart, a diagram, an equation, an embedded object or document.</summary>
    public static string Picture(string alt) => alt.Length > 0 ? "[picture: " + alt + "]" : "[picture]";

    public static string Chart(string alt) => alt.Length > 0 ? "[chart: " + alt + "]" : "[chart]";

    public static string Diagram(string alt) => alt.Length > 0 ? "[diagram: " + alt + "]" : "[diagram]";

    public const string Equation = "[equation]";

    public static string EmbeddedObject(string program) => program.Length > 0 ? "[embedded object: " + program + "]" : "[embedded object]";

    public const string EmbeddedDocument = "[embedded document not read]";

    /// <summary>Hidden text's marks: the model never acts on words the reader can't see without being told so.</summary>
    public const string HiddenOpen = "[hidden: ";

    public const string HiddenClose = "]";

    public const string FootnotesHeading = "Footnotes:";
    public const string EndnotesHeading = "Endnotes:";
    public const string CommentsHeading = "Comments:";

    /// <summary><c>/read</c> of a workbook. Pinned.</summary>
    public static string NotReadAloud(string path) => $"{path} is a spreadsheet; /read reads documents and slides aloud, not sheets";

    /// <summary><c>/read</c> of a document with no text.</summary>
    public static string NothingToRead(string path) => $"{path} holds no text to read";

    /// <summary><c>/read</c>'s spinner while the file is read.</summary>
    public const string Reading = "Reading the Office file";

    /// <summary><c>/read</c>'s read stopped by Esc before it spoke.</summary>
    public const string ReadAloudStopped = "(reading the Office file stopped)";

    private static string KindName(OfficeKind kind) => kind switch
    {
        OfficeKind.Word => "a Word document",
        OfficeKind.Excel => "a workbook",
        _ => "a presentation",
    };

    private static string ExtensionOf(OfficeKind kind) => kind switch
    {
        OfficeKind.Word => ".docx",
        OfficeKind.Excel => ".xlsx",
        _ => ".pptx",
    };

    private static string Count(int count, string what) => N(count) + " " + what + (count == 1 ? "" : "s");

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
