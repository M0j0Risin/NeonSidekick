using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeonSidekick.Web;

/// <summary>One page of a PDF read through the browser (2026-10-11): its number (1-based), its text, and its picture as PNG bytes (null when not asked for).</summary>
public sealed record PdfPageRead(int Number, string Text, byte[]? Png, int Width, int Height);

/// <summary>
/// What <see cref="IHeadlessBrowser.ReadPdfAsync"/> read (2026-10-11): the pages it got to (at most <see cref="PdfReadOptions.MaxPages"/>),
/// how many the file has, and why it stopped short (<see cref="Detail"/>, empty when it read them all). Not <see cref="Ok"/> when it read none.
/// </summary>
public sealed record PdfRead(bool Ok, string Detail, int PageCount, IReadOnlyList<PdfPageRead> Pages)
{
    /// <summary>Whether the run began in the app's kill-on-close job (Windows), <see cref="BrowserDump.InJob"/>'s.</summary>
    public bool InJob { get; init; }
}

/// <summary>How a PDF is read: the most pages, the longest side of a page's picture in pixels, and whether pictures are drawn at all.</summary>
public sealed record PdfReadOptions(int MaxPages = 40, int MaxSide = 1600, bool Pictures = true)
{
    public static readonly PdfReadOptions Default = new();
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

    /// <summary>The expression that opens <paramref name="pdfBase64"/>; it answers <c>{ n: pages }</c>.</summary>
    public static string OpenExpression(string pdfBase64) =>
        "(async () => { const bytes = Uint8Array.from(atob('" + pdfBase64 + "'), c => c.charCodeAt(0)); " +
        "globalThis.__neonPdf = await globalThis.__neonPdfLib.getDocument({ data: bytes, isEvalSupported: false, useSystemFonts: true }).promise; " +
        "return { n: globalThis.__neonPdf.numPages }; })()";

    /// <summary>
    /// The expression that reads page <paramref name="number"/>: its text, and with <paramref name="picture"/> its picture scaled so its
    /// longer side is at most <paramref name="maxSide"/> pixels (never above twice the page's own size); it answers <c>{ t, p, w, h }</c>.
    /// </summary>
    public static string PageExpression(int number, int maxSide, bool picture) =>
        "(async () => { const page = await globalThis.__neonPdf.getPage(" + number.ToString(CultureInfo.InvariantCulture) + "); " +
        "const tc = await page.getTextContent(); " +
        "const t = tc.items.map(i => (i.str || '') + (i.hasEOL ? '\\n' : ' ')).join(''); " +
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
