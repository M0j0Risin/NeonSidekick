using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NeonSidekick.Web;

/// <summary>What <see cref="IHeadlessBrowser.EditPdfAsync"/> does to its PDFs (2026-10-14, the PDF plan's phase 4b).</summary>
public enum PdfEditOp
{
    /// <summary>Every page of every input, in order, into one new PDF.</summary>
    Merge,

    /// <summary>The pages asked for, copied into a new PDF.</summary>
    Extract,

    /// <summary>The PDF without the pages asked for (never all of them).</summary>
    Delete,

    /// <summary>The pages asked for (every page by default) turned by the angle, clockwise.</summary>
    Rotate,

    /// <summary>The form's fields set to the values given, flattened into the pages when asked.</summary>
    FillForm,
}

/// <summary>A form field's new value (2026-10-14): text, true/false for a check box, an option, or a list of options for a list box.</summary>
public sealed record PdfFieldValue(string Name, JsonElement Value);

/// <summary>
/// A value a field would not take (2026-10-14): one that is not among its <paramref name="Options"/> (a radio group, a drop-down, a list box),
/// a field that takes none (<see cref="TakesNone"/>: a button, a signature), or pdf-lib's own <paramref name="Error"/>.
/// </summary>
public sealed record PdfFieldRefusal(string Name, string Error, IReadOnlyList<string> Options)
{
    /// <summary>The page's word for a field that takes no value.</summary>
    public const string NoValue = "none";

    public bool TakesNone => Options.Count == 0 && Error == NoValue;
}

/// <summary>
/// One edit for the browser (2026-10-14, the PDF plan's phase 4b): the operation, the PDFs' bytes in order, and the pages it acts on worked
/// out from the page count once the first input is open (<paramref name="PagesFor"/>: the physical pages, or the refusal sentence) — the
/// count is the browser's to know, the rule the caller's. <paramref name="Angle"/> for <see cref="PdfEditOp.Rotate"/>, <paramref name="Fields"/>
/// and <paramref name="Flatten"/> for <see cref="PdfEditOp.FillForm"/>.
/// </summary>
public sealed record PdfEditJob(
    PdfEditOp Op,
    IReadOnlyList<byte[]> Inputs,
    Func<int, (IReadOnlyList<int>? Pages, string? Error)> PagesFor,
    int Angle = 0,
    IReadOnlyList<PdfFieldValue>? Fields = null,
    bool Flatten = false);

/// <summary>
/// What an edit made (2026-10-14): the new PDF's bytes and page count, or why not — a run that failed (<see cref="Detail"/>), an input
/// locked with a password (<see cref="LockedInput"/>, its index), the pages refused (<see cref="Refused"/>, the caller's sentence), or a form
/// that could not take the values: XFA alone (<see cref="Xfa"/>), no fields at all (<see cref="NoFields"/>), names it does not have
/// (<see cref="UnknownFields"/>, with <see cref="FieldNames"/>, the ones it does), or values a field refused (<see cref="BadFields"/>).
/// </summary>
public sealed record PdfEdit(bool Ok, string Detail, byte[]? Output, int PageCount)
{
    /// <summary>As <see cref="BrowserDump.InJob"/>.</summary>
    public bool InJob { get; init; }

    public int? LockedInput { get; init; }

    public string? Refused { get; init; }

    public bool Xfa { get; init; }

    public bool NoFields { get; init; }

    public IReadOnlyList<string> UnknownFields { get; init; } = [];

    public IReadOnlyList<string> FieldNames { get; init; } = [];

    public IReadOnlyList<PdfFieldRefusal> BadFields { get; init; } = [];

    /// <summary>The pages the edit acted on, physical numbers, as <see cref="PdfEditJob.PagesFor"/> gave them (empty for a merge or a form).</summary>
    public IReadOnlyList<int> Pages { get; init; } = [];
}

/// <summary>
/// The page side of editing a PDF in the headless browser (2026-10-14, the PDF plan's phase 4b, the user's pick): pdf-lib
/// (<c>assets/pdflib</c>, <c>@cantoo/pdf-lib</c>'s UMD build, embedded) evaluated into the same blank, network-less page the reader uses
/// (<see cref="HeadlessBrowser.PdfReadArguments"/>), each input loaded in a message of its own, the edit made, and the new file read back
/// in chunks of <see cref="ChunkBytes"/> so no answer nears <see cref="DevToolsClient.MaxMessageBytes"/>. An encrypted input is refused,
/// not decrypted: pdf-lib's fork could, with a password the app does not take (gap 14). The class names in the minified build are not
/// stable, so the page tells errors and field kinds apart with <c>instanceof</c> on what <c>PDFLib</c> exports. Pure: the expressions
/// and the reading of their answers; <see cref="HeadlessBrowser"/> runs them.
/// </summary>
public static class BrowserPdfEditor
{
    public const string LibraryResource = "pdflib/pdf-lib.min.js";

    /// <summary>The most the inputs may come to together, and the largest PDF an edit writes (bytes).</summary>
    public const int MaxOutputBytes = 64 * 1024 * 1024;

    /// <summary>How much of the new file one answer carries: 8 MB, about 11 MB as base64.</summary>
    public const int ChunkBytes = 8 * 1024 * 1024;

    /// <summary>The embedded build's text; null when the build carries none (a test assembly's load fails loudly instead).</summary>
    public static string? LibraryText()
    {
        using var stream = typeof(BrowserPdfEditor).Assembly.GetManifestResourceStream(LibraryResource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The expression that evaluates pdf-lib into the page: the classic script as it is, which sets <c>globalThis.PDFLib</c>, then an
    /// object, since an evaluate that ends on <c>undefined</c> is no answer (gap 68). It answers <c>{ v: 'ok' }</c>.
    /// </summary>
    public static string LoadExpression(string library) =>
        library + "\n;({ v: (typeof PDFLib === 'object' && typeof PDFLib.PDFDocument === 'function') ? 'ok' : 'missing' })";

    /// <summary>Whether <see cref="LoadExpression"/>'s answer says pdf-lib is there.</summary>
    public static bool Loaded(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == "ok";

    /// <summary>
    /// The expression that loads one input into <c>globalThis.__neonDocs</c>; it answers <c>{ n: pages }</c>, or <c>{ locked: true }</c> for an
    /// encrypted file. A form fill keeps the XFA data (<paramref name="preserveXfa"/>), so the run can tell an XFA-only form from a hybrid.
    /// </summary>
    public static string InputExpression(string pdfBase64, bool preserveXfa) =>
        "(async () => { const bytes = Uint8Array.from(atob('" + pdfBase64 + "'), c => c.charCodeAt(0)); " +
        "globalThis.__neonDocs = globalThis.__neonDocs || []; " +
        "try { const d = await PDFLib.PDFDocument.load(bytes, { updateMetadata: false, preserveXFA: " + (preserveXfa ? "true" : "false") + " }); " +
        "globalThis.__neonDocs.push(d); return { n: d.getPageCount() }; } " +
        "catch (e) { if (e instanceof PDFLib.EncryptedPDFError) { return { locked: true }; } throw e; } })()";

    /// <summary>The page count an answer of <see cref="InputExpression"/> gives; null for a locked file or another shape.</summary>
    public static int? PageCountOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("n", out var n) && n.TryGetInt32(out int pages) ? pages : null;

    /// <summary>Whether an answer of <see cref="InputExpression"/> says the file is encrypted.</summary>
    public static bool IsLocked(JsonElement value) => BrowserPdfReader.IsLocked(value);

    /// <summary>
    /// The expression that makes the edit from the loaded inputs and saves it into <c>globalThis.__neonOut</c>; it answers
    /// <c>{ n: pages, s: bytes }</c>, or for a form <c>{ xfa: true }</c>, <c>{ nofields: true }</c>, <c>{ unknown, names }</c> or
    /// <c>{ bad: [{ n, e }] }</c>. <paramref name="pages"/> are physical numbers from 1.
    /// </summary>
    public static string RunExpression(PdfEditOp op, IReadOnlyList<int> pages, int angle = 0, IReadOnlyList<PdfFieldValue>? fields = null, bool flatten = false)
    {
        ArgumentNullException.ThrowIfNull(pages);
        string indices = "[" + string.Join(",", pages.Select(p => (p - 1).ToString(CultureInfo.InvariantCulture))) + "]";
        string body = op switch
        {
            PdfEditOp.Merge =>
                "out = await L.PDFDocument.create(); " +
                "for (const d of docs) { const copied = await out.copyPages(d, d.getPageIndices()); copied.forEach(p => out.addPage(p)); } ",
            PdfEditOp.Extract =>
                "out = await L.PDFDocument.create(); const copied = await out.copyPages(docs[0], " + indices + "); copied.forEach(p => out.addPage(p)); ",
            PdfEditOp.Delete =>
                "out = docs[0]; for (const i of " + indices + ".sort((a, b) => b - a)) { out.removePage(i); } ",
            PdfEditOp.Rotate =>
                "out = docs[0]; for (const i of " + indices + ") { const p = out.getPage(i); " +
                "p.setRotation(L.degrees(((p.getRotation().angle + " + angle.ToString(CultureInfo.InvariantCulture) + ") % 360 + 360) % 360)); } ",
            PdfEditOp.FillForm => FillBody(fields ?? [], flatten),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        return "(async () => { const L = PDFLib; const docs = globalThis.__neonDocs || []; let out; " + body +
            "globalThis.__neonOut = await out.save(); return { n: out.getPageCount(), s: globalThis.__neonOut.length }; })()";
    }

    // The form's part: an XFA-only form refused (pdf-lib fills AcroForm fields alone, gap 25), a hybrid's XFA dropped so viewers show the
    // AcroForm values, every name checked before any is set, each field set by its kind, and the values flattened into the pages when asked.
    private static string FillBody(IReadOnlyList<PdfFieldValue> fields, bool flatten) =>
        "const d = docs[0]; const form = d.getForm(); const all = form.getFields(); " +
        "if (all.length === 0) { return form.hasXFA() ? { xfa: true } : { nofields: true }; } " +
        "if (form.hasXFA()) { form.deleteXFA(); } " +
        "const want = " + FieldsJson(fields) + "; const names = all.map(f => f.getName()); " +
        "const unknown = Object.keys(want).filter(k => !names.includes(k)); if (unknown.length > 0) { return { unknown, names }; } " +
        "const bad = []; " +
        "for (const [k, v] of Object.entries(want)) { const f = form.getField(k); try { " +
        "if (f instanceof L.PDFTextField) { f.setText(v === null ? '' : String(v)); } " +
        "else if (f instanceof L.PDFCheckBox) { const on = v === true || /^(true|yes|on|checked|x|1)$/i.test(String(v)); if (on) { f.check(); } else { f.uncheck(); } } " +
        "else if (f instanceof L.PDFRadioGroup || f instanceof L.PDFDropdown || f instanceof L.PDFOptionList) { " +
        "const opts = f.getOptions(); const vs = Array.isArray(v) ? v.map(String) : [String(v)]; " +
        "if (vs.some(x => !opts.includes(x)) && !(f instanceof L.PDFDropdown && f.isEditable())) { bad.push({ n: k, o: opts }); continue; } " +
        "if (f instanceof L.PDFRadioGroup) { f.select(vs[0]); } else { f.select(f instanceof L.PDFOptionList ? vs : vs[0]); } } " +
        "else { bad.push({ n: k, e: '" + PdfFieldRefusal.NoValue + "' }); } " +
        "} catch (e) { bad.push({ n: k, e: String((e && e.message) || e).split('\\n')[0] }); } } " +
        "if (bad.length > 0) { return { bad }; } " +
        (flatten ? "form.flatten(); " : "") +
        "out = d; ";

    /// <summary>The values as a JSON object literal, which is also a JavaScript one: the writer escapes every quote, angle bracket and non-ASCII character.</summary>
    public static string FieldsJson(IReadOnlyList<PdfFieldValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in fields)
            {
                writer.WritePropertyName(field.Name);
                field.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The expression that reads <paramref name="length"/> bytes of the new file from <paramref name="start"/>; it answers <c>{ c: base64 }</c>.</summary>
    public static string ChunkExpression(int start, int length) =>
        "(() => { const b = globalThis.__neonOut.subarray(" + start.ToString(CultureInfo.InvariantCulture) + ", " +
        (start + length).ToString(CultureInfo.InvariantCulture) + "); let s = ''; " +
        "for (let i = 0; i < b.length; i += 32768) { s += String.fromCharCode.apply(null, b.subarray(i, i + 32768)); } " +
        "return { c: btoa(s) }; })()";

    /// <summary>A chunk's bytes; null for another shape or bad base64.</summary>
    public static byte[]? ReadChunk(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(c.GetString() ?? "");
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>What <see cref="RunExpression"/> answered, read into an edit's outcome: <paramref name="saved"/> the size saved, or why there is none.</summary>
    public static (int PageCount, int Saved, PdfEdit? Refusal) ReadRun(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return (0, 0, new PdfEdit(false, "no answer from the edit", null, 0));
        }

        if (value.TryGetProperty("xfa", out var xfa) && xfa.ValueKind == JsonValueKind.True)
        {
            return (0, 0, new PdfEdit(false, "an XFA form", null, 0) { Xfa = true });
        }

        if (value.TryGetProperty("nofields", out var none) && none.ValueKind == JsonValueKind.True)
        {
            return (0, 0, new PdfEdit(false, "no form fields", null, 0) { NoFields = true });
        }

        if (value.TryGetProperty("unknown", out var unknown) && unknown.ValueKind == JsonValueKind.Array)
        {
            return (0, 0, new PdfEdit(false, "unknown fields", null, 0) { UnknownFields = Strings(unknown), FieldNames = value.TryGetProperty("names", out var names) ? Strings(names) : [] });
        }

        if (value.TryGetProperty("bad", out var bad) && bad.ValueKind == JsonValueKind.Array)
        {
            var list = bad.EnumerateArray()
                .Where(b => b.ValueKind == JsonValueKind.Object)
                .Select(b => new PdfFieldRefusal(
                    b.TryGetProperty("n", out var n) ? n.GetString() ?? "" : "",
                    b.TryGetProperty("e", out var e) ? e.GetString() ?? "" : "",
                    b.TryGetProperty("o", out var o) ? Strings(o) : []))
                .ToList();
            return (0, 0, new PdfEdit(false, "fields refused their values", null, 0) { BadFields = list });
        }

        return value.TryGetProperty("n", out var n2) && n2.TryGetInt32(out int pages) && value.TryGetProperty("s", out var s) && s.TryGetInt32(out int size)
            ? (pages, size, null)
            : (0, 0, new PdfEdit(false, "the edit answered no size", null, 0));
    }

    private static IReadOnlyList<string> Strings(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "")]
            : [];
}
