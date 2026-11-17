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
/// made safe (<see cref="Render.PicturePage"/>); the encoding; the page's width in CSS pixels and the pixels per CSS pixel; the ground, a
/// <c>#RRGGBB</c> or null for transparent.
/// </summary>
public sealed record RenderJob(string? Mermaid, string? Html, PictureFormat Format, int Width, double Scale, string? Background);

/// <summary>
/// What a draw produced: the picture's bytes and pixel size, or why not. <see cref="MermaidError"/> is Mermaid's own complaint about the text,
/// for the model to fix; <see cref="Cut"/> says the page ran past <see cref="BrowserPictureRenderer.MaxPixels"/> and was cut at the bottom.
/// </summary>
public sealed record PictureRender(bool Ok, string Detail, byte[]? Bytes, int Width, int Height)
{
    public bool InJob { get; init; }

    public bool Cut { get; init; }

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

    /// <summary>The embedded build's text; null when the build carries none.</summary>
    public static string? LibraryText()
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
    /// Mermaid's theme for a ground (pass 32): <c>dark</c> on a dark color, else <c>default</c> (white, a light color, transparent). A diagram's
    /// own <c>%%{init: …}%%</c> theme still wins, as Mermaid allows; <c>securityLevel</c> can't be lowered from there.
    /// </summary>
    public static string MermaidTheme(string? background) =>
        background is not null && UI.ThemeFile.TryParseColor(background, out var color) && !ColorContrast.IsLight(color) ? "dark" : "default";

    /// <summary>
    /// The expression that draws <paramref name="source"/> to SVG with <c>securityLevel: 'strict'</c>: <c>{ svg }</c>, or <c>{ error }</c> with
    /// Mermaid's message. The text goes in as a JSON string, never as code.
    /// </summary>
    public static string MermaidExpression(string source, string theme)
    {
        ArgumentNullException.ThrowIfNull(source);
        return "(async () => { try { mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: " + JsonString(theme) + " }); " +
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
        return (null, error.Length == 0 ? "no drawing" : error.Length > 400 ? error[..400] + "…" : error);
    }

    /// <summary>
    /// The expression that waits for the page's fonts and pictures, then measures it: an SVG's frame by the drawing (its right and bottom edge
    /// and the padding), a page of HTML whole. Answers <c>{ width, height }</c> in CSS pixels.
    /// </summary>
    public const string SizeExpression =
        "(async () => { await document.fonts.ready; " +
        "await Promise.all(Array.from(document.images, i => i.decode().catch(() => null))); " +
        "const f = document.getElementById('" + Render.PicturePage.FrameId + "'); const d = document.documentElement; const b = document.body; " +
        "if (f && f.firstElementChild) { const r = f.firstElementChild.getBoundingClientRect(); " +
        "return { width: Math.ceil(r.right + 16), height: Math.ceil(r.bottom + 16) }; } " +   // 16: PicturePage.Pad
        // A page of HTML by its content's box (a range over the body, plus its bottom margin), not the scroll height, which is never less than
        // the viewport, nor the document's box, which fills it in quirks mode (a short card with no doctype came out 800 tall).
        "if (!b) { return { width: d.scrollWidth, height: d.scrollHeight }; } " +
        "const range = document.createRange(); range.selectNodeContents(b); const c = range.getBoundingClientRect(); " +
        "return { width: Math.ceil(Math.max(d.scrollWidth, b.scrollWidth)), height: Math.ceil(c.bottom + (parseFloat(getComputedStyle(b).marginBottom) || 0)) }; })()";

    /// <summary>The size answer, each side at least one CSS pixel; null for no answer.</summary>
    public static (int Width, int Height)? Size(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("width", out var w) && w.TryGetDouble(out double width)
            && value.TryGetProperty("height", out var h) && h.TryGetDouble(out double height)
            ? (Math.Max(1, (int)Math.Ceiling(width)), Math.Max(1, (int)Math.Ceiling(height)))
            : null;

    /// <summary>
    /// The clip in CSS pixels for a measured page: the width held to the job's (a page wider than asked is cut there) and to
    /// <see cref="MaxPixels"/>, the height to <see cref="MaxPixels"/>; <paramref name="cut"/> when the height was. Pure.
    /// </summary>
    public static (int Width, int Height) Clip(int measuredWidth, int measuredHeight, int jobWidth, double scale, out bool cut)
    {
        int most = (int)Math.Floor(MaxPixels / scale);
        int width = Math.Min(Math.Min(measuredWidth, jobWidth), most);
        cut = measuredHeight > most;
        return (Math.Max(1, width), Math.Max(1, Math.Min(measuredHeight, most)));
    }

    /// <summary>The ground as DevTools' RGBA: transparent for null, else the color, opaque.</summary>
    public static (int R, int G, int B, double A) Ground(string? background) =>
        background is not null && UI.ThemeFile.TryParseColor(background, out var c) ? (c.R, c.G, c.B, 1) : (0, 0, 0, 0);

    private static string JsonString(string text)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(text);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static string N(double value) => value.ToString(CultureInfo.InvariantCulture);
}
