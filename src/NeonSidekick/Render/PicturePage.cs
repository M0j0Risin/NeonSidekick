using NeonSidekick.Pdf;

namespace NeonSidekick.Render;

/// <summary>
/// The page a picture is drawn from (2026-11-16, plan sharded-hugging-puppy #9): every one carries <see cref="PdfHtml.ContentSecurityPolicy"/>
/// (no script, nothing fetched, pictures and fonts from <c>data:</c> only). An SVG sits in a frame (<see cref="FrameId"/>) the drawing is
/// measured by; a page of HTML is measured whole. The model's SVG and HTML are rebuilt by <see cref="PdfHtmlSanitizer.RewriteForPicture"/>
/// first, so its markup never runs script; the app's own (Mermaid's SVG from its strict pass, a chart, a collage) are trusted as written. Pure.
/// </summary>
public static class PicturePage
{
    /// <summary>The frame an SVG sits in, which the drawing measures.</summary>
    public const string FrameId = "neon-picture";

    /// <summary>The space around an SVG, in CSS pixels.</summary>
    public const int Pad = 16;

    /// <summary>The frame's styles: no page margin, the padding around the drawing, the drawing a block.</summary>
    public static readonly string FrameStyle =
        "<style>html, body { margin: 0; padding: 0; } #" + FrameId + " { padding: " + Pad.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px; } #" + FrameId + " > svg { display: block; }</style>\n";

    /// <summary>An SVG the app made or Mermaid drew, framed and trusted.</summary>
    public static string ForTrustedSvg(string svg)
    {
        ArgumentNullException.ThrowIfNull(svg);
        return "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\">\n" + PdfHtml.PolicyMeta + "\n" + FrameStyle + "</head><body><div id=\"" + FrameId + "\">" + svg + "</div></body></html>";
    }

    /// <summary>The model's SVG, framed, then rebuilt: no script, no event handler, a picture only from <paramref name="resolve"/>.</summary>
    public static string ForModelSvg(string svg, Func<string, byte[]?> resolve)
    {
        ArgumentNullException.ThrowIfNull(svg);
        string body = svg.TrimStart();
        if (body.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && body.IndexOf("?>", StringComparison.Ordinal) is int end and > 0)
        {
            body = body[(end + 2)..];
        }

        return PdfHtmlSanitizer.RewriteForPicture("<!DOCTYPE html><html><head></head><body><div id=\"" + FrameId + "\">" + body + "</div></body></html>", resolve, FrameStyle);
    }

    /// <summary>The model's HTML, rebuilt: no script, no event handler, a picture or stylesheet only from <paramref name="resolve"/>.</summary>
    public static string ForModelHtml(string html, Func<string, byte[]?> resolve)
    {
        ArgumentNullException.ThrowIfNull(html);
        return PdfHtmlSanitizer.RewriteForPicture(html, resolve);
    }
}

/// <summary>One picture of a collage: its bytes as a <c>data:</c> URI's media type and payload, and its caption (null for none).</summary>
public sealed record CollageItem(string MediaType, byte[] Bytes, string? Caption);

/// <summary>
/// A collage's page (2026-11-16, plan sharded-hugging-puppy #9): the pictures in a grid of <paramref name="columns"/>, each fitted into its
/// cell keeping its shape, a caption under it, a title over all. Every text is HTML-escaped (a file named <c>&lt;b&gt;.jpg</c> stays text);
/// the pictures are <c>data:</c>, fitted beforehand so the page stays small. The colors are <see cref="PictureColors"/>' (2026-11-17, the
/// admin-themes-render review's #41); the columns are <c>minmax(0, 1fr)</c> and a figure may shrink below its caption (#32), so a long
/// caption is cut with its ellipsis instead of holding its column open. Pure.
/// </summary>
public static class CollageHtml
{
    public const int MaxPictures = 48;

    /// <summary>The columns for <paramref name="count"/> pictures when none are asked: about square, at most six.</summary>
    public static int ColumnsFor(int count) => Math.Clamp((int)Math.Ceiling(Math.Sqrt(Math.Max(1, count))), 1, 6);

    public static string Page(IReadOnlyList<CollageItem> items, int columns, string? title, bool dark)
    {
        ArgumentNullException.ThrowIfNull(items);
        var colors = PictureColors.For(dark);
        string ground = colors.Ground, ink = colors.Ink, quiet = colors.Quiet, tile = colors.Tile;
        var page = new System.Text.StringBuilder();
        page.Append("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\">\n").Append(PdfHtml.PolicyMeta).Append('\n')
            .Append("<style>html, body { margin: 0; background: ").Append(ground).Append("; color: ").Append(ink).Append("; font-family: 'Segoe UI', system-ui, -apple-system, Arial, sans-serif; }")
            .Append(" main { padding: 24px; } h1 { margin: 0 0 18px; font-size: 30px; font-weight: 600; text-align: center; }")
            .Append(" .grid { display: grid; grid-template-columns: repeat(").Append(columns.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(", minmax(0, 1fr)); gap: 14px; }")
            .Append(" figure { margin: 0; min-width: 0; } .tile { aspect-ratio: 4 / 3; background: ").Append(tile).Append("; border-radius: 8px; overflow: hidden; display: flex; align-items: center; justify-content: center; }")
            .Append(" img { max-width: 100%; max-height: 100%; object-fit: contain; display: block; }")
            .Append(" figcaption { margin-top: 6px; font-size: 15px; color: ").Append(quiet).Append("; text-align: center; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }</style>\n")
            .Append("</head><body><main>");
        if (!string.IsNullOrWhiteSpace(title))
        {
            page.Append("<h1>").Append(System.Net.WebUtility.HtmlEncode(title.Trim())).Append("</h1>");
        }

        page.Append("<div class=\"grid\">");
        foreach (var item in items)
        {
            page.Append("<figure><div class=\"tile\"><img alt=\"\" src=\"").Append(PdfHtml.DataUri(item.Bytes, item.MediaType)).Append("\"></div>");
            if (!string.IsNullOrWhiteSpace(item.Caption))
            {
                page.Append("<figcaption>").Append(System.Net.WebUtility.HtmlEncode(item.Caption.Trim())).Append("</figcaption>");
            }

            page.Append("</figure>");
        }

        return page.Append("</div></main></body></html>").ToString();
    }
}
