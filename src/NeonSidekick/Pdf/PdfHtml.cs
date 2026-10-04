using System.Globalization;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using NeonSidekick.UI.Markdown;

namespace NeonSidekick.Pdf;

/// <summary>
/// The page the browser prints (2026-10-03, <c>convert_to_pdf</c> and <c>/pdf</c>): Markdown, a listing or a picture as one
/// self-contained HTML document with a fixed, light, print-minded stylesheet — not the terminal's theme, paper is white. Nothing
/// on it is fetched: the Content-Security-Policy allows only what is inline (styles, <c>data:</c> pictures and fonts), so a page
/// the app wrote loads nothing off the disk or the network and runs no script, and the browser needs no file-access flags.
/// Markdown's raw HTML is escaped, never rendered, as the transcript and <c>/print</c> do; its pictures are inlined from the
/// sandbox by the caller's resolver, anything else becomes <c>[image: alt]</c>. Code is coloured by the transcript's own
/// <see cref="CodeLexer"/> into <c>tk-*</c> classes. The page's header (the title) and footer (page N of M) are CSS margin
/// boxes, the browser's own header and footer being off. Pure.
/// </summary>
public static class PdfHtml
{
    /// <summary>The policy every page the app writes carries. Pinned.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:; base-uri 'none'; form-action 'none'";

    /// <summary>The meta tag that carries <see cref="ContentSecurityPolicy"/>.</summary>
    public static string PolicyMeta => $"<meta http-equiv=\"Content-Security-Policy\" content=\"{ContentSecurityPolicy}\">";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .UseFootnotes()
        .DisableHtml()
        .Build();

    /// <summary>The image types a page may inline, by extension, and the media type each is sent as.</summary>
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
    };

    /// <summary>The media type a picture file is inlined as, or null when it is not one a page can show.</summary>
    public static string? ImageMediaType(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ImageTypes.TryGetValue(Path.GetExtension(path), out var type) ? type : null;
    }

    /// <summary>A <c>data:</c> URI of <paramref name="bytes"/>.</summary>
    public static string DataUri(byte[] bytes, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(mediaType);
        return "data:" + mediaType + ";base64," + Convert.ToBase64String(bytes);
    }

    /// <summary>The whole page: the policy, the title, the stylesheet for <paramref name="paper"/>, and <paramref name="body"/>.</summary>
    public static string Document(string title, string body, PdfPaperSize paper, bool landscape)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        var html = new StringBuilder(body.Length + 4096);
        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append(PolicyMeta).Append('\n')
            .Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n")
            .Append("<style>\n").Append(PageRule(paper, landscape, title)).Append(Stylesheet).Append("</style>\n")
            .Append("</head>\n<body>\n").Append(body).Append("\n</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>The <c>@page</c> rule: the paper, the margins, the title top left and the page number top right. Pinned in part.</summary>
    public static string PageRule(PdfPaperSize paper, bool landscape, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        const string margin = "font: 8pt \"Segoe UI\", system-ui, sans-serif; color: #777;";
        return "@page {\n"
            + "  size: " + PdfPaper.Css(paper, landscape) + ";\n"
            + "  margin: 18mm 16mm;\n"
            + "  @top-left { content: " + CssString(title) + "; " + margin + " }\n"
            + "  @top-right { content: \"page \" counter(page) \" of \" counter(pages); " + margin + " }\n"
            + "}\n";
    }

    /// <summary>A CSS string literal of <paramref name="text"/>: quotes and backslashes escaped, <c>&lt;</c> too (it sits in a style element), control characters dropped.</summary>
    public static string CssString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var css = new StringBuilder(text.Length + 2).Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"':
                    css.Append("\\\"");
                    break;
                case '\\':
                    css.Append("\\\\");
                    break;
                case '<':
                    css.Append("\\3C ");
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        css.Append(c);
                    }

                    break;
            }
        }

        return css.Append('"').ToString();
    }

    /// <summary>
    /// <paramref name="markdown"/> as the page's body. <paramref name="resolveImage"/> turns a picture's address into the
    /// <c>data:</c> URI to show, or null — the picture is then its alt text in brackets.
    /// </summary>
    public static string Markdown(string markdown, Func<string, string?> resolveImage)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(resolveImage);
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var image in document.Descendants<LinkInline>().Where(l => l.IsImage).ToList())
        {
            string? data = string.IsNullOrWhiteSpace(image.Url) ? null : resolveImage(image.Url);
            if (data is null)
            {
                string alt = string.Concat(image.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
                image.ReplaceBy(new LiteralInline(PdfText.MissingImage(alt)), copyChildren: false);
            }
            else
            {
                image.Url = data;
            }
        }

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<CodeBlockRenderer>(new LexedCodeBlockRenderer());
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>A text file as the page's body: one listing, coloured when <paramref name="display"/>'s extension names a language.</summary>
    public static string Listing(string text, string display)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(display);
        return "<pre class=\"listing\"><code>" + Code(text, UI.DiffView.LanguageOf(display)) + "</code></pre>";
    }

    /// <summary>A picture as the page's body: centred, fitted to the page, never enlarged past its own size.</summary>
    public static string Picture(string dataUri, string display)
    {
        ArgumentNullException.ThrowIfNull(dataUri);
        ArgumentNullException.ThrowIfNull(display);
        return "<div class=\"picture\"><img src=\"" + dataUri + "\" alt=\"" + WebUtility.HtmlEncode(display) + "\"></div>";
    }

    /// <summary><paramref name="code"/> escaped, its runs in <c>tk-*</c> spans when <paramref name="language"/> is known.</summary>
    public static string Code(string code, CodeLanguage? language)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (language is null)
        {
            return WebUtility.HtmlEncode(code);
        }

        var html = new StringBuilder(code.Length * 2);
        foreach (var token in CodeLexer.Lex(code, language))
        {
            string text = WebUtility.HtmlEncode(code.Substring(token.Start, token.Length));
            if (CodeClass(token.Kind) is { } css)
            {
                html.Append("<span class=\"").Append(css).Append("\">").Append(text).Append("</span>");
            }
            else
            {
                html.Append(text);
            }
        }

        return html.ToString();
    }

    /// <summary>A token kind's class in the stylesheet; null for plain text. Pinned.</summary>
    public static string? CodeClass(CodeTokenKind kind) => kind switch
    {
        CodeTokenKind.Keyword => "tk-keyword",
        CodeTokenKind.Type => "tk-type",
        CodeTokenKind.String => "tk-string",
        CodeTokenKind.Number => "tk-number",
        CodeTokenKind.Comment => "tk-comment",
        CodeTokenKind.Punctuation => "tk-punctuation",
        CodeTokenKind.Function => "tk-function",
        CodeTokenKind.Variable => "tk-variable",
        CodeTokenKind.Attribute => "tk-attribute",
        CodeTokenKind.Tag => "tk-tag",
        CodeTokenKind.Heading => "tk-heading",
        CodeTokenKind.Inserted => "tk-inserted",
        CodeTokenKind.Deleted => "tk-deleted",
        _ => null,
    };

    /// <summary>Markdig's code blocks through <see cref="Code"/>: a fence's language coloured, an indented block plain.</summary>
    private sealed class LexedCodeBlockRenderer : HtmlObjectRenderer<Markdig.Syntax.CodeBlock>
    {
        protected override void Write(HtmlRenderer renderer, Markdig.Syntax.CodeBlock obj)
        {
            var language = obj is FencedCodeBlock fenced ? CodeLanguages.Find(fenced.Info) : null;
            string text = obj.Lines.ToString();
            renderer.EnsureLine();
            renderer.Write("<pre><code>").Write(Code(text, language)).Write("</code></pre>");
            renderer.EnsureLine();
        }
    }

    /// <summary>The stylesheet after the <c>@page</c> rule: light, black on white, colour kept for code and table heads.</summary>
    public const string Stylesheet = """
        :root { color-scheme: light; }
        * { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        body { margin: 0; font-family: "Segoe UI", system-ui, sans-serif; font-size: 10.5pt; line-height: 1.45; color: #1a1a1a; background: #fff; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.1em 0 0.5em; break-after: avoid; }
        h1 { font-size: 20pt; border-bottom: 1px solid #ccc; padding-bottom: 0.2em; }
        h2 { font-size: 15pt; border-bottom: 1px solid #e3e3e3; padding-bottom: 0.15em; }
        h3 { font-size: 12.5pt; }
        h4, h5, h6 { font-size: 11pt; }
        body > :first-child { margin-top: 0; }
        p, ul, ol, blockquote, table, pre, dl { margin: 0 0 0.8em; }
        a { color: #0b57d0; text-decoration: none; }
        code, pre, kbd, samp { font-family: "Cascadia Mono", Consolas, monospace; font-size: 9pt; }
        :not(pre) > code { background: #f1f3f5; padding: 0.1em 0.3em; border-radius: 3px; }
        pre { background: #f6f8fa; border: 1px solid #e1e4e8; border-radius: 4px; padding: 0.6em 0.8em; white-space: pre-wrap; overflow-wrap: anywhere; tab-size: 4; }
        pre.listing { background: none; border: none; border-radius: 0; padding: 0; line-height: 1.35; }
        blockquote { margin-left: 0; padding: 0 1em; color: #555; border-left: 3px solid #d0d7de; }
        table { border-collapse: collapse; }
        th, td { border: 1px solid #d0d7de; padding: 0.3em 0.6em; vertical-align: top; }
        th { background: #f6f8fa; }
        tr, img { break-inside: avoid; }
        img { max-width: 100%; }
        hr { border: none; border-top: 1px solid #ccc; margin: 1.2em 0; }
        li.task-list-item { list-style: none; }
        li.task-list-item input { margin: 0 0.4em 0 -1.3em; }
        del { color: #777; }
        .footnotes { font-size: 9pt; color: #444; }
        .picture { text-align: center; }
        .picture img { max-height: 95vh; object-fit: contain; }
        .tk-keyword { color: #cf222e; }
        .tk-type { color: #953800; }
        .tk-string { color: #0a3069; }
        .tk-number { color: #0550ae; }
        .tk-comment { color: #6e7781; font-style: italic; }
        .tk-punctuation { color: #24292f; }
        .tk-function { color: #8250df; }
        .tk-variable { color: #953800; }
        .tk-attribute { color: #0550ae; }
        .tk-tag { color: #116329; }
        .tk-heading { color: #0550ae; font-weight: bold; }
        .tk-inserted { color: #116329; background: #dafbe1; }
        .tk-deleted { color: #82071e; background: #ffebe9; }

        """;
}
