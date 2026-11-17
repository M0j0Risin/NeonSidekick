using System.Globalization;
using System.Text;
using System.Text.Json;
using NeonSidekick.UI;

namespace NeonSidekick.Web;

/// <summary>A drawn picture's encoding.</summary>
public enum PictureFormat
{
    Png,
    Jpeg,
}

/// <summary>
/// One picture to draw (2026-11-16, plan sharded-hugging-puppy #9): Mermaid text (drawn to SVG first, in the page) or a page of HTML already
/// made safe (<see cref="Render.PicturePage"/>); the encoding; the page's width in CSS pixels and the pixels per CSS pixel; the ground, a color
/// with its alpha or null for transparent (2026-11-17, the admin-themes-render review's #55: parsed once, by the one parser); and whether the
/// page is an SVG in its frame (Mermaid, the model's SVG, a chart) or a page of HTML, measured each its own way (#126).
/// </summary>
public sealed record RenderJob(string? Mermaid, string? Html, PictureFormat Format, int Width, double Scale, System.Drawing.Color? Background, bool Svg);

/// <summary>
/// What a draw produced: the picture's bytes and pixel size, or why not. <see cref="MermaidError"/> is Mermaid's own complaint about the text,
/// for the model to fix; <see cref="Cut"/> says the page ran past <see cref="BrowserPictureRenderer.MaxPixels"/> and was cut at the bottom,
/// <see cref="Wide"/> that it ran past its width and was cut at the right (2026-11-17, #2).
/// </summary>
public sealed record PictureRender(bool Ok, string Detail, byte[]? Bytes, int Width, int Height)
{
    public bool InJob { get; init; }

    public bool Cut { get; init; }

    public bool Wide { get; init; }

    public string? MermaidError { get; init; }

    /// <summary>Whether the picture was too large to hand back over DevTools.</summary>
    public bool TooLarge { get; init; }

    /// <summary>Whether no browser was found to draw with (<see cref="Detail"/> says how to set one).</summary>
    public bool NoBrowser { get; init; }
}

/// <summary>
/// The pieces of a draw in the headless browser (2026-11-16, plan sharded-hugging-puppy #9), as <see cref="BrowserPdfEditor"/>'s are for an
/// edit: the embedded Mermaid build and the expressions evaluated in the page. The run itself is <see cref="HeadlessBrowser.RenderPictureAsync"/>:
/// the PDF reads' network-less browser, Mermaid's strict pass in one page, the drawing in a fresh one under the picture page's policy, measured,
/// then captured. Pure.
/// </summary>
public static class BrowserPictureRenderer
{
    public const string LibraryResource = "mermaid/mermaid.min.js";

    /// <summary>The most pixels a side of a picture has; a page past it is cut at the bottom (and a width held to it).</summary>
    public const int MaxPixels = 4096;

    /// <summary>A JPEG's quality (a collage of photos).</summary>
    public const int JpegQuality = 90;

    /// <summary>The longest a draw may take: the browser's start, Mermaid's load and pass, the page, the capture.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // The build's text, read once a process (2026-11-17, #30, #94: every draw and the smoke probe decoded the 3 MB resource again).
    private static readonly Lazy<string?> Library = new(ReadLibrary);

    /// <summary>The embedded build's text; null when the build carries none.</summary>
    public static string? LibraryText() => Library.Value;

    private static string? ReadLibrary()
    {
        using var stream = typeof(BrowserPictureRenderer).Assembly.GetManifestResourceStream(LibraryResource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The expression that evaluates Mermaid into the page, then answers <c>{ v: 'ok' }</c> (an evaluate answers objects only).</summary>
    public static string LoadExpression(string library) =>
        library + "\n;({ v: (typeof mermaid === 'object' && typeof mermaid.render === 'function') ? 'ok' : 'missing' })";

    /// <summary>Whether <see cref="LoadExpression"/>'s answer says Mermaid is there.</summary>
    public static bool Loaded(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == "ok";

    /// <summary>
    /// Mermaid's theme for a ground (pass 32): <c>dark</c> on a dark color, judged on its red, green and blue, else <c>default</c> (white, a
    /// light color, transparent, an alpha of 0). A diagram's own <c>%%{init: …}%%</c> theme still wins, as Mermaid allows; <c>securityLevel</c>
    /// can't be lowered from there.
    /// </summary>
    public static string MermaidTheme(System.Drawing.Color? background) =>
        background is { A: > 0 } c && !ColorContrast.IsLight(new Spectre.Console.Color(c.R, c.G, c.B)) ? "dark" : "default";

    /// <summary>
    /// The expression that draws <paramref name="source"/> to SVG with <c>securityLevel: 'strict'</c>: <c>{ svg }</c>, or <c>{ error }</c> with
    /// Mermaid's message. The text goes in as a JSON string, never as code. <c>maxTextSize</c> is the tool's own cap (2026-11-17, #176), so a
    /// diagram the tool took isn't refused by Mermaid's smaller default.
    /// </summary>
    public static string MermaidExpression(string source, string theme)
    {
        ArgumentNullException.ThrowIfNull(source);
        return "(async () => { try { mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', maxTextSize: " +
            Llm.Tools.RenderTool.MaxChars.ToString(CultureInfo.InvariantCulture) + ", theme: " + JsonString(theme) + " }); " +
            "const r = await mermaid.render('neonpicture', " + JsonString(source) + "); return { svg: r.svg }; } " +
            "catch (e) { return { error: String((e && (e.message || e.str)) || e) }; } })()";
    }

    /// <summary>Mermaid's answer: the SVG, or the error's first line.</summary>
    public static (string? Svg, string? Error) MermaidAnswer(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("svg", out var svg) && svg.ValueKind == JsonValueKind.String && svg.GetString() is { Length: > 0 } text)
        {
            return (text, null);
        }

        string error = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
        error = string.Join(' ', error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
        return (null, error.Length == 0 ? "no drawing" : error.Length > 400 ? Files.TextCut.Safe(error, 400) + "…" : error);
    }

    /// <summary>
    /// The expression that waits for the page's fonts and pictures, then measures it (2026-11-17, #126: the job says which, so a page of HTML
    /// holding an element named <see cref="Render.PicturePage.FrameId"/> is never measured as a frame). An SVG by its frame's drawing, its right
    /// and bottom edge and the padding; a root with no <c>width</c>, <c>height</c> or <c>viewBox</c> first takes its drawing's box as its
    /// <c>viewBox</c> and size (#188), so a drawing at negative coordinates isn't cut at the left or top. A page of HTML by the larger of its
    /// content's range, the body's border box and every element's box (#21: a positioned element the range missed), plus the body's bottom
    /// margin. The body's box counts only in standards mode: the model's page has no doctype, and in quirks mode the body stretches to the
    /// viewport. Answers <c>{ width, height }</c> in CSS pixels.
    /// </summary>
    public static string SizeExpression(bool svg) =>
        "(async () => { await document.fonts.ready; " +
        "await Promise.all(Array.from(document.images, i => i.decode().catch(() => null))); " +
        (svg
            ? "const f = document.getElementById('" + Render.PicturePage.FrameId + "'); const s = f && f.firstElementChild; " +
              "if (!s) { return null; } " +
              "if (s.tagName.toLowerCase() === 'svg' && !s.hasAttribute('width') && !s.hasAttribute('height') && !s.hasAttribute('viewBox') && s.getBBox) { " +
              "try { const k = s.getBBox(); const p = 2; if (k.width > 0 && k.height > 0) { " +   // 2: the stroke's slack around the box
              "s.setAttribute('viewBox', (k.x - p) + ' ' + (k.y - p) + ' ' + (k.width + 2 * p) + ' ' + (k.height + 2 * p)); " +
              "s.setAttribute('width', k.width + 2 * p); s.setAttribute('height', k.height + 2 * p); } } catch (e) { } } " +
              "const r = s.getBoundingClientRect(); " +
              "return { width: Math.ceil(r.right + " + Pad + "), height: Math.ceil(r.bottom + " + Pad + ") }; })()"
            // In quirks mode (no doctype) the body's box is the viewport's, so only a standards-mode body adds its own.
            : "const d = document.documentElement; const b = document.body; " +
              "if (!b) { return { width: d.scrollWidth, height: d.scrollHeight }; } " +
              "const range = document.createRange(); range.selectNodeContents(b); const c = range.getBoundingClientRect(); " +
              "const o = document.compatMode === 'CSS1Compat' ? b.getBoundingClientRect() : c; " +
              "let right = Math.max(d.scrollWidth, b.scrollWidth, c.right, o.right); let bottom = Math.max(c.bottom, o.bottom); " +
              "for (const e of b.querySelectorAll('*')) { const r = e.getBoundingClientRect(); if (r.width > 0 || r.height > 0) { " +
              "if (r.right > right) { right = r.right; } if (r.bottom > bottom) { bottom = r.bottom; } } } " +
              "return { width: Math.ceil(right), height: Math.ceil(bottom + (parseFloat(getComputedStyle(b).marginBottom) || 0)) }; })()");

    private static readonly string Pad = Render.PicturePage.Pad.ToString(CultureInfo.InvariantCulture);

    /// <summary>The size answer, each side at least one CSS pixel; null for no answer.</summary>
    public static (int Width, int Height)? Size(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("width", out var w) && w.TryGetDouble(out double width)
            && value.TryGetProperty("height", out var h) && h.TryGetDouble(out double height)
            ? (Math.Max(1, (int)Math.Ceiling(width)), Math.Max(1, (int)Math.Ceiling(height)))
            : null;

    /// <summary>
    /// The width a page is laid out at: the job's, held to what <see cref="MaxPixels"/> takes at its scale (2026-11-17, #105: a 4096-wide page
    /// at scale 2 was laid out at 4096 and cut to its left half).
    /// </summary>
    public static int LayoutWidth(int jobWidth, double scale) => Math.Max(1, Math.Min(jobWidth, (int)Math.Floor(MaxPixels / scale)));

    /// <summary>
    /// The clip in CSS pixels for a measured page: the width held to <see cref="LayoutWidth"/> (a page wider is cut there,
    /// <paramref name="wide"/>, 2026-11-17, #2: said, not silent), the height to <see cref="MaxPixels"/> (<paramref name="cut"/> when it was).
    /// A pixel over is rounding, not a cut. Pure.
    /// </summary>
    public static (int Width, int Height) Clip(int measuredWidth, int measuredHeight, int jobWidth, double scale, out bool cut, out bool wide)
    {
        int most = (int)Math.Floor(MaxPixels / scale);
        int layout = LayoutWidth(jobWidth, scale);
        wide = measuredWidth > layout + 1;
        cut = measuredHeight > most;
        return (Math.Max(1, Math.Min(measuredWidth, layout)), Math.Max(1, Math.Min(measuredHeight, most)));
    }

    /// <summary>The ground as DevTools' RGBA: transparent for null, else the color with its alpha.</summary>
    public static (int R, int G, int B, double A) Ground(System.Drawing.Color? background) =>
        background is { } c ? (c.R, c.G, c.B, c.A / 255.0) : (0, 0, 0, 0);

    private static string JsonString(string text)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(text);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
