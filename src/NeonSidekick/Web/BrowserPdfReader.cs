using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeonSidekick.Web;

/// <summary>One page of a PDF read through the browser (2026-10-11): its number (1-based), its text, and its picture as PNG bytes (null when not asked for).</summary>
public sealed record PdfPageRead(int Number, string Text, byte[]? Png, int Width, int Height);

/// <summary>One entry of a PDF's outline (its bookmarks, 2026-10-14): the title, its depth (1 the top), and its physical page (null when its destination could not be followed).</summary>
public sealed record PdfOutlineItem(string Title, int Level, int? Page);

/// <summary>
/// One AcroForm field (2026-10-14, the PDF plan's gap 98): its full name, pdf.js's kind (<c>text</c>, <c>checkbox</c>, <c>radiobutton</c>,
/// <c>combobox</c>, <c>listbox</c>, <c>button</c>, <c>signature</c>), its value now, the values it takes (a check box's on value, a radio
/// group's, a choice's display texts: what <c>pdf_edit</c>'s <c>fill_form</c> matches), its page, and whether it is read-only.
/// </summary>
public sealed record PdfFormField(string Name, string Kind, string Value, IReadOnlyList<string> Options, int? Page, bool ReadOnly);

/// <summary>
/// What <see cref="IHeadlessBrowser.ReadPdfAsync"/> read (2026-10-11): the pages it got to (at most <see cref="PdfReadOptions.MaxPages"/>,
/// or those of <see cref="PdfReadOptions.Pages"/>), how many the file has, and why it stopped short (<see cref="Detail"/>, empty when it
/// read them all). Not <see cref="Ok"/> when it read none of the pages asked for (a read that asked for none is Ok once the file opened).
/// </summary>
public sealed record PdfRead(bool Ok, string Detail, int PageCount, IReadOnlyList<PdfPageRead> Pages)
{
    /// <summary>Whether the run began in the app's kill-on-close job (Windows), <see cref="BrowserDump.InJob"/>'s.</summary>
    public bool InJob { get; init; }

    /// <summary>Whether the file asks for a password (2026-10-14, the PDF plan's gap 2): pdf.js's <c>PasswordException</c>; nothing read.</summary>
    public bool Locked { get; init; }

    /// <summary>The outline's first two levels when <see cref="PdfReadOptions.Outline"/> asked (empty for a PDF without one); null when not asked.</summary>
    public IReadOnlyList<PdfOutlineItem>? Outline { get; init; }

    /// <summary>The printed page labels, one per page, when <see cref="PdfReadOptions.Labels"/> asked (empty for a PDF without any); null when not asked.</summary>
    public IReadOnlyList<string>? Labels { get; init; }

    /// <summary>The form's fields when <see cref="PdfReadOptions.Fields"/> asked (empty for a PDF without a form); null when not asked.</summary>
    public IReadOnlyList<PdfFormField>? Fields { get; init; }

    /// <summary>Whether the form is XFA alone (pdf.js's <c>isPureXfa</c>), read with the fields.</summary>
    public bool PureXfa { get; init; }
}

/// <summary>
/// How a PDF is read: the most pages, the longest side of a page's picture in pixels, and whether pictures are drawn at all — and since
/// 2026-10-14 (the PDF plan's gaps 44 and 94) which pages exactly (<see cref="Pages"/>, physical numbers; one past the end is left out),
/// which of them are drawn (<see cref="PicturePages"/>, null = every page read when <see cref="Pictures"/>), and whether the outline and
/// the page labels are read in the same run; later that day (gap 98) the form's fields too.
/// </summary>
public sealed record PdfReadOptions(
    int MaxPages = 40,
    int MaxSide = 1600,
    bool Pictures = true,
    IReadOnlyList<int>? Pages = null,
    IReadOnlyList<int>? PicturePages = null,
    bool Outline = false,
    bool Labels = false,
    bool Fields = false)
{
    public static readonly PdfReadOptions Default = new();

    /// <summary>The pages to read of a file of <paramref name="pageCount"/>: <see cref="Pages"/> within it, else the first <see cref="MaxPages"/>.</summary>
    public IReadOnlyList<int> PagesOf(int pageCount) =>
        Pages is { } asked ? [.. asked.Where(p => p >= 1 && p <= pageCount).Distinct().Order()] : [.. Enumerable.Range(1, Math.Max(0, Math.Min(pageCount, MaxPages)))];

    /// <summary>Whether page <paramref name="number"/> is drawn.</summary>
    public bool Draws(int number) => PicturePages is { } drawn ? drawn.Contains(number) : Pictures;
}

/// <summary>
/// The page side of reading a PDF in the headless browser (2026-10-11, the shopping list's reference documents; the plan's "render
/// PDFs through the headless browser", the user's pick): Chromium headless downloads a PDF rather than showing it, so the app brings
/// its own renderer, Mozilla's pdf.js (<c>assets/pdfjs</c>, embedded), imports both modules into the blank page as <c>data:</c> URLs
/// (nothing written to disk, no origin needed), runs the worker on the page's own thread (<c>globalThis.pdfjsWorker</c>), and asks
/// for one page at a time: its text (<c>getTextContent</c>) and its picture (a canvas, white behind, read back as a PNG data URL).
/// <c>isEvalSupported: false</c> keeps pdf.js from compiling a font's functions (CVE-2024-4367's door, shut since 4.2.67 too). Pure:
/// the expressions and the reading of their answers; <see cref="HeadlessBrowser"/> runs them.
/// </summary>
public static partial class BrowserPdfReader
{
    public const string LibraryResource = "pdfjs/pdf.min.mjs";
    public const string WorkerResource = "pdfjs/pdf.worker.min.mjs";

    /// <summary>The largest PDF read (bytes): it travels as base64 inside one DevTools message.</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    /// <summary>The embedded module <paramref name="resource"/> as base64; null when the build carries none (a test assembly's load fails loudly instead).</summary>
    public static string? ModuleBase64(string resource)
    {
        using var stream = typeof(BrowserPdfReader).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Convert.ToBase64String(copy.GetBuffer(), 0, (int)copy.Length);
    }

    /// <summary>The expression that imports pdf.js and its worker into the page; it answers <c>{ v: version }</c>.</summary>
    public static string LoadExpression(string libraryBase64, string workerBase64) =>
        "(async () => { const lib = await import('data:text/javascript;base64," + libraryBase64 + "'); " +
        "globalThis.pdfjsWorker = await import('data:text/javascript;base64," + workerBase64 + "'); " +
        "globalThis.__neonPdfLib = lib; return { v: lib.version }; })()";

    /// <summary>The prefix of the packed CMaps' resource names (2026-10-14, the PDF plan's gap 3).</summary>
    public const string CMapResourcePrefix = "pdfjs/cmaps/";

    private static readonly Lazy<(string Expression, int Count)> s_cmaps = new(BuildCMaps);

    /// <summary>How many packed CMaps this build carries (0 for a build without them, a test assembly's load).</summary>
    public static int CMapCount => s_cmaps.Value.Count;

    /// <summary>
    /// The expression that puts every packed CMap into the page (2026-10-14, the PDF plan's gaps 3 and 48): one object of names to base64,
    /// about 1.6 MB, built once a process from the embedded resources; it answers <c>{ n: count }</c>. Always sent: pdf.js swallows a
    /// font it cannot build (an <c>ErrorFont</c>, its text blank), so there is no error to retry on, and a lazy load would need the page
    /// to call back to the app mid-read. <see cref="OpenExpression"/>'s factory answers from it.
    /// </summary>
    public static string CMapsExpression() => s_cmaps.Value.Expression;

    private static (string, int) BuildCMaps()
    {
        var assembly = typeof(BrowserPdfReader).Assembly;
        var sb = new StringBuilder("(() => { globalThis.__neonCMaps = {");
        int count = 0;
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(CMapResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".bcmap", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            // The names are Adobe's (UniJIS-UCS2-H, 78-EUC-H, Adobe-Japan1-UCS2): letters, digits and dashes, quoted as they are; any
            // other character would need escaping in the literal, so such a name is left out rather than written wrong.
            string key = name[CMapResourcePrefix.Length..^".bcmap".Length];
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                continue;
            }

            sb.Append(count > 0 ? "," : "").Append('\'').Append(key).Append("':'")
                .Append(Convert.ToBase64String(copy.GetBuffer(), 0, (int)copy.Length)).Append('\'');
            count++;
        }

        sb.Append("}; return { n: ").Append(count.ToString(CultureInfo.InvariantCulture)).Append(" }; })()");
        return (sb.ToString(), count);
    }

    /// <summary>
    /// The expression that opens <paramref name="pdfBase64"/>; it answers <c>{ n: pages }</c>, or <c>{ locked: true }</c> for a file that asks
    /// for a password (2026-10-14, the PDF plan's gap 2: pdf.js's <c>PasswordException</c>, a raw error until then). Since the same day a
    /// <c>CMapReaderFactory</c> answers pdf.js's CMap requests from <see cref="CMapsExpression"/>'s map (gap 3); <c>useWorkerFetch</c> stays
    /// off, as it already was with no CMap URL, so the requests come to the page's side, where the map is.
    /// </summary>
    public static string OpenExpression(string pdfBase64) =>
        "(async () => { const bytes = Uint8Array.from(atob('" + pdfBase64 + "'), c => c.charCodeAt(0)); " +
        "class NeonCMaps { constructor() {} async fetch({ name }) { const b = (globalThis.__neonCMaps || {})[name]; " +
        "if (!b) { throw new Error('Unable to load CMap ' + name); } " +
        "return { cMapData: Uint8Array.from(atob(b), c => c.charCodeAt(0)), isCompressed: true }; } } " +
        "try { globalThis.__neonPdf = await globalThis.__neonPdfLib.getDocument({ data: bytes, isEvalSupported: false, useSystemFonts: true, useWorkerFetch: false, CMapReaderFactory: NeonCMaps }).promise; } " +
        "catch (e) { if (e && e.name === 'PasswordException') { return { locked: true }; } throw e; } " +
        "return { n: globalThis.__neonPdf.numPages }; })()";

    /// <summary>Whether an answer of <see cref="OpenExpression"/> says the file is locked.</summary>
    public static bool IsLocked(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("locked", out var locked) && locked.ValueKind == JsonValueKind.True;

    /// <summary>The most outline entries the page sends back; <c>read_pdf</c> shows fewer (2026-10-14, the PDF plan's gap 91).</summary>
    public const int MaxOutlineItems = 200;

    /// <summary>
    /// The expression that reads the outline's first two levels (2026-10-14, the PDF plan's gap 91): each entry's title, depth and physical
    /// page, a named destination looked up and a page reference turned into its index; one that cannot be followed answers page 0. It
    /// answers <c>{ o: [{ t, l, p }] }</c>, empty for a PDF without an outline.
    /// </summary>
    public static string OutlineExpression() =>
        "(async () => { const doc = globalThis.__neonPdf; const out = []; " +
        "const pageOf = async (dest) => { try { const d = typeof dest === 'string' ? await doc.getDestination(dest) : dest; " +
        "if (!Array.isArray(d) || d.length === 0) { return 0; } const ref = d[0]; " +
        "return (typeof ref === 'number' ? ref : await doc.getPageIndex(ref)) + 1; } catch (e) { return 0; } }; " +
        "const walk = async (list, level) => { for (const it of (list || [])) { if (out.length >= " + MaxOutlineItems.ToString(CultureInfo.InvariantCulture) + ") { return; } " +
        "out.push({ t: String(it.title || ''), l: level, p: await pageOf(it.dest) }); if (level < 2) { await walk(it.items, level + 1); } } }; " +
        "await walk(await doc.getOutline(), 1); return { o: out }; })()";

    /// <summary>An answer of <see cref="OutlineExpression"/> read; empty for any other shape.</summary>
    public static IReadOnlyList<PdfOutlineItem> ReadOutline(JsonElement value)
    {
        var items = new List<PdfOutlineItem>();
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("o", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            int level = item.TryGetProperty("l", out var l) && l.TryGetInt32(out int lv) ? lv : 1;
            int page = item.TryGetProperty("p", out var p) && p.TryGetInt32(out int pv) ? pv : 0;
            string title = Spaces().Replace(t.GetString() ?? "", " ").Trim();
            if (title.Length > 0)
            {
                items.Add(new PdfOutlineItem(title, level, page > 0 ? page : null));
            }
        }

        return items;
    }

    /// <summary>The expression that reads the printed page labels (2026-10-14, the PDF plan's gap 92); it answers <c>{ l: [labels] }</c>, empty without them.</summary>
    public static string LabelsExpression() =>
        "(async () => { const l = await globalThis.__neonPdf.getPageLabels(); return { l: Array.isArray(l) ? l.map(x => String(x || '')) : [] }; })()";

    /// <summary>The most form fields the page sends back (gap 98).</summary>
    public const int MaxFields = 500;

    /// <summary>
    /// The expression that reads the form's fields (2026-10-14, the PDF plan's gaps 25 and 98): <c>getFieldObjects()</c>, one entry a name
    /// from its first widget that has a kind, a radio group's options and value from all its widgets, a choice's display texts; and
    /// <c>isPureXfa</c>. It answers <c>{ f: [{ n, t, v, o, p, r }], x }</c>, <c>f</c> empty for a PDF without a form.
    /// </summary>
    public static string FieldsExpression() =>
        "(async () => { const doc = globalThis.__neonPdf; const objs = (await doc.getFieldObjects()) || {}; const out = []; " +
        "for (const [name, widgets] of Object.entries(objs)) { if (out.length >= " + MaxFields.ToString(CultureInfo.InvariantCulture) + ") { break; } " +
        "const list = Array.isArray(widgets) ? widgets : []; const w = list.find(x => x && x.type) || list[0] || {}; let opts = []; let v = w.value; " +
        "if (w.type === 'radiobutton') { opts = list.map(x => x.exportValues).filter(x => typeof x === 'string' && x.length > 0); " +
        "const on = list.find(x => x.value && x.value !== 'Off'); v = on ? on.value : 'Off'; } " +
        "else if (w.type === 'checkbox') { opts = [w.exportValues].filter(x => typeof x === 'string' && x.length > 0); } " +
        "else if (Array.isArray(w.items)) { opts = w.items.map(i => String(i.displayValue ?? i.exportValue ?? '')); } " +
        "out.push({ n: String(name), t: String(w.type || ''), v: v == null ? '' : (Array.isArray(v) ? v.map(String).join(', ') : String(v)), " +
        "o: [...new Set(opts)], p: typeof w.page === 'number' && w.page >= 0 ? w.page + 1 : 0, r: w.editable === false || w.readOnly === true }); } " +
        "return { f: out, x: !!doc.isPureXfa }; })()";

    /// <summary>An answer of <see cref="FieldsExpression"/> read: the fields (empty for any other shape) and whether the form is XFA alone.</summary>
    public static (IReadOnlyList<PdfFormField> Fields, bool PureXfa) ReadFields(JsonElement value)
    {
        var fields = new List<PdfFormField>();
        if (value.ValueKind != JsonValueKind.Object)
        {
            return (fields, false);
        }

        bool xfa = value.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.True;
        if (!value.TryGetProperty("f", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return (fields, xfa);
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("n", out var n) || n.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string Text(string key) => item.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
            var options = item.TryGetProperty("o", out var o) && o.ValueKind == JsonValueKind.Array
                ? [.. o.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "")]
                : new List<string>();
            int page = item.TryGetProperty("p", out var p) && p.TryGetInt32(out int pv) ? pv : 0;
            bool readOnly = item.TryGetProperty("r", out var r) && r.ValueKind == JsonValueKind.True;
            fields.Add(new PdfFormField(n.GetString() ?? "", Text("t"), Text("v"), options, page > 0 ? page : null, readOnly));
        }

        return (fields, xfa);
    }

    /// <summary>An answer of <see cref="LabelsExpression"/> read; empty for any other shape.</summary>
    public static IReadOnlyList<string> ReadLabels(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("l", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "").Trim() : "")]
            : [];

    /// <summary>
    /// The expression that reads page <paramref name="number"/>: its text, and with <paramref name="picture"/> its picture scaled so its
    /// longer side is at most <paramref name="maxSide"/> pixels (never above twice the page's own size); it answers <c>{ t, p, w, h }</c>.
    /// The text is the items' strings run together, a line break after each <c>hasEOL</c>, as pdf.js's own find controller joins them:
    /// pdf.js already gives a gap its <c>" "</c> item. A space after every item (until 2026-10-11, the Mac's first publish smoke) split
    /// words wherever the font changes inside one: Chrome on a Mac prints <c>system-ui</c> (SF Pro) as several subsets a word, so
    /// "Eggs" came as "E", "gg", "s" and read "E gg s"; Segoe UI on Windows prints whole runs, which is why it never showed there.
    /// </summary>
    public static string PageExpression(int number, int maxSide, bool picture) =>
        "(async () => { const page = await globalThis.__neonPdf.getPage(" + number.ToString(CultureInfo.InvariantCulture) + "); " +
        "const tc = await page.getTextContent(); " +
        "const t = tc.items.map(i => (i.str || '') + (i.hasEOL ? '\\n' : '')).join(''); " +
        (picture
            ? "const base = page.getViewport({ scale: 1 }); " +
              "const scale = Math.min(2, " + maxSide.ToString(CultureInfo.InvariantCulture) + " / Math.max(base.width, base.height)); " +
              "const vp = page.getViewport({ scale }); " +
              "const c = document.createElement('canvas'); c.width = Math.ceil(vp.width); c.height = Math.ceil(vp.height); " +
              "const ctx = c.getContext('2d'); ctx.fillStyle = '#ffffff'; ctx.fillRect(0, 0, c.width, c.height); " +
              "await page.render({ canvasContext: ctx, viewport: vp }).promise; " +
              "page.cleanup(); return { t, p: c.toDataURL('image/png'), w: c.width, h: c.height }; })()"
            : "page.cleanup(); return { t, p: '', w: 0, h: 0 }; })()");

    /// <summary>A page's answer read: its text tidied (<see cref="TidyText"/>) and its picture's bytes; null for an answer of another shape.</summary>
    public static PdfPageRead? ReadPage(int number, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        byte[]? png = null;
        const string Prefix = "data:image/png;base64,";
        if (value.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } url && url.StartsWith(Prefix, StringComparison.Ordinal))
        {
            try
            {
                png = Convert.FromBase64String(url[Prefix.Length..]);
            }
            catch (FormatException)
            {
                png = null;
            }
        }

        int width = value.TryGetProperty("w", out var w) && w.TryGetInt32(out int wv) ? wv : 0;
        int height = value.TryGetProperty("h", out var h) && h.TryGetInt32(out int hv) ? hv : 0;
        return new PdfPageRead(number, TidyText(t.GetString() ?? ""), png, width, height);
    }

    /// <summary>A page's text tidied: runs of spaces and tabs one space, each line trimmed, blank lines dropped.</summary>
    public static string TidyText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        foreach (string line in text.Split('\n'))
        {
            string tidy = Spaces().Replace(line, " ").Trim();
            if (tidy.Length > 0)
            {
                sb.Append(sb.Length > 0 ? "\n" : "").Append(tidy);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();
}
