using System.Globalization;
using System.Xml;
using NeonSidekick.Images;
using NeonSidekick.Pdf;
using NeonSidekick.UI.Markdown;

namespace NeonSidekick.Office;

/// <summary>How a Word document is laid out: the paper and whether it lies on its side.</summary>
public sealed record WordPage(PdfPaperSize Paper = PdfPaper.Default, bool Landscape = false);

/// <summary>What went into a Word document: the pictures embedded and those left as their alt text.</summary>
public sealed record WordStats(int Pictures, int Missing);

/// <summary>
/// A <see cref="MarkdownDocument"/> written as a Word document (2026-10-29, the Office writing plan; the readers' twin, by hand over
/// <see cref="OfficePackageWriter"/>). The parts: <c>document.xml</c>, <c>styles.xml</c> (Calibri 11, Consolas for code; the headings under
/// Word's built-in names <c>heading 1…</c>, which <see cref="WordReader"/> and Word's navigation pane both know), <c>numbering.xml</c> (a
/// bullet and a decimal list of nine levels, and one <c>w:num</c> per list so each restarts), <c>settings.xml</c> (compatibility mode 15:
/// without it Word opens the file in Compatibility Mode), a header with the title when there is one, a footer with "Page X of Y", the
/// pictures and the properties. The title goes to the header and <c>dc:title</c>, never into the body, as the PDF puts it in the page's
/// corner: a Markdown that opens with its own <c># heading</c> isn't headed twice.
/// <para>
/// Links to <c>http</c>, <c>https</c> and <c>mailto</c> become hyperlinks; any other target is plain text with the target in parentheses, so
/// there is never a <c>file:</c> or <c>javascript:</c> link. A picture comes from the caller's <c>picture</c> function (the sandbox's, held
/// there); null leaves it as italic <c>[picture: alt]</c>. Not thread-safe; one write.
/// </para>
/// </summary>
public sealed class WordWriter
{
    public const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string WpNs = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string PicNs = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string Ml = "application/vnd.openxmlformats-officedocument.wordprocessingml.";
    public const string DocumentType = Ml + "document.main+xml";
    private const string DocumentPart = "word/document.xml";

    /// <summary>1-inch margins, in twentieths of a point.</summary>
    private const int Margin = 1440;

    /// <summary>How far each list or quote level steps in, in twentieths of a point.</summary>
    private const int Step = 720;

    /// <summary>English Metric Units in a twentieth of a point, and in a pixel at 96 dpi.</summary>
    private const long EmuPerTwip = 635;

    private const long EmuPerPixel = 9525;

    private const double EmuPerInch = 914400;

    /// <summary>A table cell's left and right margins together (<c>tblCellMar</c>'s 108 each), what a picture in a cell gives up.</summary>
    private const int CellMargins = 216;

    private readonly OfficePackageWriter _package;
    private readonly WordPage _page;
    private readonly string? _title;
    private readonly Func<string, WordPicture?> _picture;

    /// <summary>Checked before each block (2026-10-30, review F52: Esc reached only a picture's read, so a long text ran to its end).</summary>
    private CancellationToken _cancellation;
    private readonly List<(int Abstract, int Level, int Start)> _lists = [];
    private readonly Dictionary<string, (string Rel, WordPicture Picture)?> _pictureRels = new(StringComparer.Ordinal);
    private readonly List<(string Part, WordPicture Picture)> _pictureParts = [];
    private int _pictures;
    private int _missing;
    private int _drawingId;
    private XmlWriter _w = null!;

    private WordWriter(Stream output, WordPage page, string? title, Func<string, WordPicture?> picture, TimeProvider time)
    {
        _package = new OfficePackageWriter(output, time);
        _page = page;
        _title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        _picture = picture;
    }

    /// <summary>The page's sides in twentieths of a point, turned for landscape.</summary>
    public static (int Width, int Height) PageSize(WordPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var (width, height) = page.Paper switch
        {
            PdfPaperSize.A4 => (11906, 16838),
            PdfPaperSize.Legal => (12240, 20160),
            _ => (12240, 15840),
        };
        return page.Landscape ? (height, width) : (width, height);
    }

    /// <summary>
    /// <paramref name="document"/> written to <paramref name="output"/> (left open) as a whole package.
    /// </summary>
    /// <param name="header">The page header's title (the given title, else the first <c>#</c> heading); null for no header.</param>
    /// <param name="title">The document's <c>dc:title</c>.</param>
    /// <param name="picture">A picture's address → the picture, or null for its placeholder.</param>
    /// <param name="cancellationToken">Checked before each block; a canceled write throws and leaves <paramref name="output"/> unfinished.</param>
    public static WordStats Write(Stream output, MarkdownDocument document, WordPage page, string? header, string? title, Func<string, WordPicture?> picture, TimeProvider time,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(time);
        var writer = new WordWriter(output, page, header, picture, time) { _cancellation = cancellationToken };
        try
        {
            writer.WriteAll(document, title);
            return new WordStats(writer._pictures, writer._missing);
        }
        finally
        {
            writer._package.Dispose();
        }
    }

    /// <summary>The first level-1 heading's text, which a file's page header shows; null when it has none.</summary>
    public static string? FirstHeading(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var block in document.Blocks)
        {
            if (block is HeadingBlock { Level: 1 } heading && Plain(heading.Inlines).Trim() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private void WriteAll(MarkdownDocument document, string? title)
    {
        _package.Relate("", "rId1", "officeDocument", DocumentPart);
        _package.Default("png", ImageFormats.Png.MimeType);
        _package.Default("jpeg", ImageFormats.Jpeg.MimeType);
        _package.Default("gif", ImageFormats.Gif.MimeType);
        string headerRel = "", footerRel;
        using (_w = _package.Part(DocumentPart, DocumentType))
        {
            _w.WriteStartElement("w", "document", W);
            _w.WriteAttributeString("xmlns", "r", null, OfficePackageWriter.RNs);
            _w.WriteAttributeString("xmlns", "wp", null, WpNs);
            _w.WriteAttributeString("xmlns", "a", null, ANs);
            _w.WriteAttributeString("xmlns", "pic", null, PicNs);
            _w.WriteStartElement("w", "body", W);
            Blocks(document.Blocks, new Place(0, 0));
            if (_title is not null)
            {
                headerRel = _package.NextId(DocumentPart);
                _package.Relate(DocumentPart, headerRel, "header", "header1.xml");
            }

            footerRel = _package.NextId(DocumentPart);
            _package.Relate(DocumentPart, footerRel, "footer", "footer1.xml");
            Section(headerRel, footerRel);
            _w.WriteEndElement();
            _w.WriteEndElement();
        }

        foreach (var (part, made) in _pictureParts)
        {
            using var stream = _package.Raw("word/" + part, null);
            stream.Write(made.Bytes);
        }

        Styles();
        Numbering();
        Settings();
        if (_title is not null)
        {
            Header(_title);
        }

        Footer();
        _package.Relate(DocumentPart, _package.NextId(DocumentPart), "styles", "styles.xml");
        _package.Relate(DocumentPart, _package.NextId(DocumentPart), "numbering", "numbering.xml");
        _package.Relate(DocumentPart, _package.NextId(DocumentPart), "settings", "settings.xml");
        _package.Properties(title);
        _package.Finish();
    }

    /// <summary>Where a block sits: its list depth (0 outside a list) and its quote depth.</summary>
    private readonly record struct Place(int Lists, int Quotes)
    {
        /// <summary>The left indent a plain paragraph here takes.</summary>
        public int Indent => (Lists + Quotes) * Step;
    }

    private void Blocks(IReadOnlyList<MarkdownBlock> blocks, Place place)
    {
        foreach (var block in blocks)
        {
            _cancellation.ThrowIfCancellationRequested();
            Block(block, place, number: null);
        }
    }

    /// <summary>One block; <paramref name="number"/> a list item's numbering for its first paragraph.</summary>
    private void Block(MarkdownBlock block, Place place, (int NumId, int Level)? number)
    {
        switch (block)
        {
            case HeadingBlock heading:
                Paragraph("Heading" + Math.Clamp(heading.Level, 1, 6).ToString(CultureInfo.InvariantCulture), place, number, heading.Inlines);
                break;
            case ParagraphBlock paragraph:
                Paragraph(place.Quotes > 0 ? "Quote" : number is null && place.Lists > 0 ? "ListParagraph" : null, place, number, paragraph.Inlines);
                break;
            case CodeBlock code:
                if (number is not null)
                {
                    Paragraph(null, place, number, []);
                }

                foreach (string line in code.Lines.Count == 0 ? [""] : code.Lines)
                {
                    StartParagraph("CodeBlock", place, null);
                    Text(line, default);
                    _w.WriteEndElement();
                }

                break;
            case ListBlock list:
                List(list, place, number);
                break;
            case QuoteBlock quote:
                if (number is not null)
                {
                    Paragraph(null, place, number, []);
                }

                Blocks(quote.Blocks, place with { Quotes = place.Quotes + 1 });
                break;
            case RuleBlock:
                StartParagraph("Rule", place, number);
                _w.WriteEndElement();
                break;
            case TableBlock table:
                if (number is not null)
                {
                    Paragraph(null, place, number, []);
                }

                Table(table, place);
                break;
        }
    }

    private void List(ListBlock list, Place outer, (int NumId, int Level)? number)
    {
        if (number is not null)
        {
            Paragraph(null, outer, number, []);
        }

        int level = Math.Min(outer.Lists, 8);
        _lists.Add((list.Ordered ? 1 : 0, level, Math.Max(list.Start, 0)));
        int numId = _lists.Count;
        var inside = outer with { Lists = outer.Lists + 1 };
        foreach (var item in list.Items)
        {
            if (item.Blocks.Count == 0)
            {
                Paragraph(null, inside, (numId, level), []);
                continue;
            }

            Block(item.Blocks[0], inside, (numId, level));
            for (int i = 1; i < item.Blocks.Count; i++)
            {
                Block(item.Blocks[i], inside, null);
            }
        }
    }

    private void Paragraph(string? style, Place place, (int NumId, int Level)? number, IReadOnlyList<MarkdownInline> inlines)
    {
        int left = StartParagraph(style, place, number);
        Inlines(inlines, default, TextWidth(_page) - left);
        _w.WriteEndElement();
    }

    /// <summary>
    /// A <c>w:p</c> begun with its properties: the style, the list numbering, a quote's bar, and the indent of its list and quote depth;
    /// the left indent it wrote, in twentieths of a point, so a picture in it fits what is left (2026-10-29, review P2-10: a picture in a
    /// list item or a quote ran past the right margin). A heading keeps its list or quote indent, and a paragraph of another style inside
    /// a quote (a heading, code, a list item) draws the quote's bar itself (review P3-6: <c>&gt; ## h</c> sat at the margin, unmarked).
    /// </summary>
    private int StartParagraph(string? style, Place place, (int NumId, int Level)? number)
    {
        _w.WriteStartElement("w", "p", W);
        bool indent = place.Indent > 0;
        bool bar = place.Quotes > 0 && style is not ("Quote" or "Rule");
        if (style is null && number is null && !indent)
        {
            return 0;
        }

        _w.WriteStartElement("w", "pPr", W);
        if (style is not null)
        {
            Val("pStyle", style);
        }

        if (number is { } n)
        {
            _w.WriteStartElement("w", "numPr", W);
            Val("ilvl", n.Level.ToString(CultureInfo.InvariantCulture));
            Val("numId", n.NumId.ToString(CultureInfo.InvariantCulture));
            _w.WriteEndElement();
        }

        if (bar)
        {
            // The Quote style's own bar; pBdr comes between numPr and ind in CT_PPr.
            _w.WriteStartElement("w", "pBdr", W);
            Border("left", "12", "8", "BFBFBF");
            _w.WriteEndElement();
        }

        int left = 0;
        if (number is { } numbered)
        {
            // The numbering's own indent, moved in by any quotes around the list.
            left = (numbered.Level + 1 + place.Quotes) * Step;
            _w.WriteStartElement("w", "ind", W);
            Attr("left", left.ToString(CultureInfo.InvariantCulture));
            Attr("hanging", "360");
            _w.WriteEndElement();
        }
        else if (indent)
        {
            left = place.Indent;
            _w.WriteStartElement("w", "ind", W);
            Attr("left", left.ToString(CultureInfo.InvariantCulture));
            _w.WriteEndElement();
        }

        _w.WriteEndElement();
        return left;
    }

    /// <summary>The run properties a stretch of text carries.</summary>
    private readonly record struct Look(bool Bold, bool Italic, bool Code, bool Link);

    /// <summary>A paragraph's runs; <paramref name="room"/> the width a picture may take, in twentieths of a point.</summary>
    private void Inlines(IReadOnlyList<MarkdownInline> inlines, Look outer, int room)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    Text(run.Text, new Look(outer.Bold || run.Bold, outer.Italic || run.Italic, run.Code, false));
                    break;
                case LinkRun link:
                    Link(link, outer);
                    break;
                case ImageRun image:
                    Picture(image, room);
                    break;
                case HardBreak:
                    _w.WriteStartElement("w", "r", W);
                    _w.WriteElementString("w", "br", W, null);
                    _w.WriteEndElement();
                    break;
            }
        }
    }

    /// <summary>
    /// A link's target when it may be a hyperlink (<c>http</c>, <c>https</c> or <c>mailto</c>, nothing else), escaped as a relationship
    /// holds it: spaces and accents percent-encoded, a non-ASCII host in its IDN form (2026-10-29, review P1-6: the raw text made Word
    /// report unreadable content). False for any other target.
    /// </summary>
    public static bool TryLinkable(string url, out string target)
    {
        ArgumentNullException.ThrowIfNull(url);
        target = "";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto))
        {
            return false;
        }

        target = uri.AbsoluteUri;
        if (uri.Scheme != Uri.UriSchemeMailto && !uri.IdnHost.Equals(uri.Host, StringComparison.OrdinalIgnoreCase))
        {
            target = new UriBuilder(uri) { Host = uri.IdnHost }.Uri.AbsoluteUri;
        }

        return true;
    }

    private void Link(LinkRun link, Look outer)
    {
        string text = link.Text.Length > 0 ? link.Text : link.Url;
        string url = link.Url.Trim();
        if (!TryLinkable(url, out string target))
        {
            Text(url.Length == 0 || url == text ? text : text + " (" + url + ")", outer with { Code = false, Link = false });
            return;
        }

        StartHyperlink(target);
        Text(text, outer with { Code = false, Link = true });
        _w.WriteEndElement();
    }

    /// <summary>A <c>w:hyperlink</c> begun to <paramref name="target"/> (already <see cref="TryLinkable"/>'s), its relationship made.</summary>
    private void StartHyperlink(string target)
    {
        string id = _package.NextId(DocumentPart);
        _package.Relate(DocumentPart, id, "hyperlink", XlsxValue.XmlSafe(target), external: true);
        _w.WriteStartElement("w", "hyperlink", W);
        _w.WriteAttributeString("r", "id", OfficePackageWriter.RNs, id);
    }

    /// <summary>
    /// Text as runs: a tab is <c>w:tab</c>, a line break <c>w:br</c>, and <c>w:t</c> keeps its spaces where they lead or trail. Characters
    /// XML can't hold are dropped.
    /// </summary>
    private void Text(string text, Look look)
    {
        text = XlsxValue.XmlSafe(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        _w.WriteStartElement("w", "r", W);
        RunProperties(look);
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\t' && text[i] != '\n')
            {
                continue;
            }

            if (i > start)
            {
                string piece = text[start..i];
                _w.WriteStartElement("w", "t", W);
                if (piece[0] == ' ' || piece[^1] == ' ')
                {
                    _w.WriteAttributeString("xml", "space", null, "preserve");
                }

                _w.WriteString(piece);
                _w.WriteEndElement();
            }

            if (i < text.Length)
            {
                _w.WriteElementString("w", text[i] == '\t' ? "tab" : "br", W, null);
            }

            start = i + 1;
        }

        _w.WriteEndElement();
    }

    private void RunProperties(Look look)
    {
        if (!look.Bold && !look.Italic && !look.Code && !look.Link)
        {
            return;
        }

        _w.WriteStartElement("w", "rPr", W);
        if (look.Code || look.Link)
        {
            Val("rStyle", look.Link ? "Hyperlink" : "Code");
        }

        if (look.Bold)
        {
            _w.WriteElementString("w", "b", W, null);
        }

        if (look.Italic)
        {
            _w.WriteElementString("w", "i", W, null);
        }

        _w.WriteEndElement();
    }

    private void Picture(ImageRun image, int room)
    {
        string alt = XlsxValue.XmlSafe(image.Alt.Trim());
        // One part per address: a picture shown twice is embedded once.
        if (!_pictureRels.TryGetValue(image.Url, out var found))
        {
            found = null;
            if (_picture(image.Url) is { } made)
            {
                string relId = _package.NextId(DocumentPart);
                string part = "media/image" + (_pictureParts.Count + 1).ToString(CultureInfo.InvariantCulture) + (made.Format == ImageFormats.Jpeg ? ".jpeg" : made.Format == ImageFormats.Gif ? ".gif" : ".png");
                _package.Relate(DocumentPart, relId, "image", part);
                _pictureParts.Add((part, made));
                found = (relId, made);
            }

            _pictureRels[image.Url] = found;
        }

        if (found is not { } hit)
        {
            _missing++;
            Text(OfficeText.Picture(alt), new Look(false, true, false, false));
            return;
        }

        var (rel, picture) = hit;
        _pictures++;
        // A linked picture opens its link from Word (2026-10-29, review P3-2); any other target leaves it a plain picture.
        string? linkTarget = image.Link is { } link && TryLinkable(link, out string target) ? target : null;
        if (linkTarget is not null)
        {
            StartHyperlink(linkTarget);
        }

        var (cx, cy) = Fit(picture.Width, picture.Height, _page, room, picture.Dpi);
        int id = ++_drawingId;
        string number = id.ToString(CultureInfo.InvariantCulture);
        _w.WriteStartElement("w", "r", W);
        _w.WriteStartElement("w", "drawing", W);
        _w.WriteStartElement("wp", "inline", WpNs);
        foreach (string side in new[] { "distT", "distB", "distL", "distR" })
        {
            _w.WriteAttributeString(side, "0");
        }

        Extent("wp", "extent", WpNs, cx, cy);
        _w.WriteStartElement("wp", "effectExtent", WpNs);
        foreach (string side in new[] { "l", "t", "r", "b" })
        {
            _w.WriteAttributeString(side, "0");
        }

        _w.WriteEndElement();
        _w.WriteStartElement("wp", "docPr", WpNs);
        _w.WriteAttributeString("id", number);
        _w.WriteAttributeString("name", "Picture " + number);
        if (alt.Length > 0)
        {
            _w.WriteAttributeString("descr", alt);
        }

        _w.WriteEndElement();
        _w.WriteStartElement("wp", "cNvGraphicFramePr", WpNs);
        _w.WriteStartElement("a", "graphicFrameLocks", ANs);
        _w.WriteAttributeString("noChangeAspect", "1");
        _w.WriteEndElement();
        _w.WriteEndElement();
        _w.WriteStartElement("a", "graphic", ANs);
        _w.WriteStartElement("a", "graphicData", ANs);
        _w.WriteAttributeString("uri", PicNs);
        _w.WriteStartElement("pic", "pic", PicNs);
        _w.WriteStartElement("pic", "nvPicPr", PicNs);
        _w.WriteStartElement("pic", "cNvPr", PicNs);
        _w.WriteAttributeString("id", "0");
        _w.WriteAttributeString("name", "Picture " + number);
        _w.WriteEndElement();
        _w.WriteElementString("pic", "cNvPicPr", PicNs, null);
        _w.WriteEndElement();
        _w.WriteStartElement("pic", "blipFill", PicNs);
        _w.WriteStartElement("a", "blip", ANs);
        _w.WriteAttributeString("r", "embed", OfficePackageWriter.RNs, rel);
        _w.WriteEndElement();
        _w.WriteStartElement("a", "stretch", ANs);
        _w.WriteElementString("a", "fillRect", ANs, null);
        _w.WriteEndElement();
        _w.WriteEndElement();
        _w.WriteStartElement("pic", "spPr", PicNs);
        _w.WriteStartElement("a", "xfrm", ANs);
        _w.WriteStartElement("a", "off", ANs);
        _w.WriteAttributeString("x", "0");
        _w.WriteAttributeString("y", "0");
        _w.WriteEndElement();
        Extent("a", "ext", ANs, cx, cy);
        _w.WriteEndElement();
        _w.WriteStartElement("a", "prstGeom", ANs);
        _w.WriteAttributeString("prst", "rect");
        _w.WriteElementString("a", "avLst", ANs, null);
        _w.WriteEndElement();
        _w.WriteEndElement();
        _w.WriteEndElement();
        _w.WriteEndElement();
        _w.WriteEndElement();
        _w.WriteEndElement();
        _w.WriteEndElement();
        _w.WriteEndElement();
        if (linkTarget is not null)
        {
            _w.WriteEndElement();
        }
    }

    private void Extent(string prefix, string name, string ns, long cx, long cy)
    {
        _w.WriteStartElement(prefix, name, ns);
        _w.WriteAttributeString("cx", cx.ToString(CultureInfo.InvariantCulture));
        _w.WriteAttributeString("cy", cy.ToString(CultureInfo.InvariantCulture));
        _w.WriteEndElement();
    }

    /// <summary>The width between the margins, in twentieths of a point.</summary>
    public static int TextWidth(WordPage page) => PageSize(page).Width - 2 * Margin;

    /// <summary>A picture's size in EMUs at 96 dpi, shrunk (sides in proportion) to the text's width and height.</summary>
    public static (long Cx, long Cy) Fit(int width, int height, WordPage page) => Fit(width, height, page, int.MaxValue, 0);

    /// <summary>
    /// A picture's size in EMUs at its own <paramref name="dpi"/> (96 when it records none, 2026-10-29, review P6-4: a 300-dpi scan came out
    /// three times its print size), shrunk in proportion to <paramref name="room"/> (twentieths of a point; the text's width at most) and the
    /// text's height.
    /// </summary>
    public static (long Cx, long Cy) Fit(int width, int height, WordPage page, int room, double dpi)
    {
        var (_, pageHeight) = PageSize(page);
        long maxX = Math.Max(1, Math.Min(room, TextWidth(page))) * EmuPerTwip;
        long maxY = (pageHeight - 2 * Margin) * EmuPerTwip;
        double perPixel = dpi > 1 ? EmuPerInch / dpi : EmuPerPixel;
        double cx = Math.Max(1, width) * perPixel;
        double cy = Math.Max(1, height) * perPixel;
        double scale = Math.Min(1.0, Math.Min(maxX / cx, maxY / cy));
        return (Math.Max(1, (long)(cx * scale)), Math.Max(1, (long)(cy * scale)));
    }

    /// <summary>
    /// A table: the width its list or quote leaves, moved in as far (2026-10-29, review P3-5: one in a list item spanned the page from the
    /// margin), bordered, the header row bold and repeated on each page, each column aligned as the Markdown says.
    /// </summary>
    private void Table(TableBlock table, Place place)
    {
        int columns = Math.Max(1, Math.Max(table.Header.Cells.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Cells.Count)));
        int available = Math.Max(columns * 2 * CellMargins, TextWidth(_page) - place.Indent);
        int width = available / columns;
        _w.WriteStartElement("w", "tbl", W);
        _w.WriteStartElement("w", "tblPr", W);
        _w.WriteStartElement("w", "tblW", W);
        Attr("w", (width * columns).ToString(CultureInfo.InvariantCulture));
        Attr("type", "dxa");
        _w.WriteEndElement();
        if (place.Indent > 0)
        {
            // After tblW, before tblBorders, in CT_TblPr's order.
            _w.WriteStartElement("w", "tblInd", W);
            Attr("w", place.Indent.ToString(CultureInfo.InvariantCulture));
            Attr("type", "dxa");
            _w.WriteEndElement();
        }

        _w.WriteStartElement("w", "tblBorders", W);
        foreach (string side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
        {
            _w.WriteStartElement("w", side, W);
            Attr("val", "single");
            Attr("sz", "4");
            Attr("space", "0");
            Attr("color", "A6A6A6");
            _w.WriteEndElement();
        }

        _w.WriteEndElement();
        _w.WriteStartElement("w", "tblLayout", W);
        Attr("type", "autofit");
        _w.WriteEndElement();
        _w.WriteStartElement("w", "tblCellMar", W);
        foreach (string side in new[] { "left", "right" })
        {
            _w.WriteStartElement("w", side, W);
            Attr("w", "108");
            Attr("type", "dxa");
            _w.WriteEndElement();
        }

        _w.WriteEndElement();
        _w.WriteEndElement();
        _w.WriteStartElement("w", "tblGrid", W);
        for (int i = 0; i < columns; i++)
        {
            _w.WriteStartElement("w", "gridCol", W);
            Attr("w", width.ToString(CultureInfo.InvariantCulture));
            _w.WriteEndElement();
        }

        _w.WriteEndElement();
        Row(table.Header, table.Alignments, columns, width, header: true);
        foreach (var row in table.Rows)
        {
            Row(row, table.Alignments, columns, width, header: false);
        }

        _w.WriteEndElement();
        // Word wants a paragraph between a table and whatever follows; an empty one keeps two tables apart too.
        _w.WriteElementString("w", "p", W, null);
    }

    private void Row(TableRow row, IReadOnlyList<TableAlignment> alignments, int columns, int width, bool header)
    {
        _w.WriteStartElement("w", "tr", W);
        if (header)
        {
            _w.WriteStartElement("w", "trPr", W);
            _w.WriteElementString("w", "tblHeader", W, null);
            _w.WriteEndElement();
        }

        for (int i = 0; i < columns; i++)
        {
            _w.WriteStartElement("w", "tc", W);
            _w.WriteStartElement("w", "tcPr", W);
            _w.WriteStartElement("w", "tcW", W);
            Attr("w", width.ToString(CultureInfo.InvariantCulture));
            Attr("type", "dxa");
            _w.WriteEndElement();
            _w.WriteEndElement();
            _w.WriteStartElement("w", "p", W);
            _w.WriteStartElement("w", "pPr", W);
            _w.WriteStartElement("w", "spacing", W);
            Attr("after", "0");
            _w.WriteEndElement();
            if ((i < alignments.Count ? alignments[i] : TableAlignment.Default) is var align and not TableAlignment.Default)
            {
                Val("jc", align switch { TableAlignment.Center => "center", TableAlignment.Right => "right", _ => "left" });
            }

            _w.WriteEndElement();
            if (i < row.Cells.Count)
            {
                Inlines(row.Cells[i].Inlines, new Look(header, false, false, false), width - CellMargins);
            }

            _w.WriteEndElement();
            _w.WriteEndElement();
        }

        _w.WriteEndElement();
    }

    private void Section(string headerRel, string footerRel)
    {
        var (width, height) = PageSize(_page);
        _w.WriteStartElement("w", "sectPr", W);
        if (headerRel.Length > 0)
        {
            Reference("headerReference", headerRel);
        }

        Reference("footerReference", footerRel);
        _w.WriteStartElement("w", "pgSz", W);
        Attr("w", width.ToString(CultureInfo.InvariantCulture));
        Attr("h", height.ToString(CultureInfo.InvariantCulture));
        if (_page.Landscape)
        {
            Attr("orient", "landscape");
        }

        _w.WriteEndElement();
        _w.WriteStartElement("w", "pgMar", W);
        string margin = Margin.ToString(CultureInfo.InvariantCulture);
        foreach (string side in new[] { "top", "right", "bottom", "left" })
        {
            Attr(side, margin);
        }

        Attr("header", "720");
        Attr("footer", "720");
        Attr("gutter", "0");
        _w.WriteEndElement();
        _w.WriteEndElement();
    }

    private void Reference(string name, string id)
    {
        _w.WriteStartElement("w", name, W);
        Attr("type", "default");
        _w.WriteAttributeString("r", "id", OfficePackageWriter.RNs, id);
        _w.WriteEndElement();
    }

    private void Header(string title)
    {
        using (_w = _package.Part("word/header1.xml", Ml + "header+xml"))
        {
            _w.WriteStartElement("w", "hdr", W);
            _w.WriteStartElement("w", "p", W);
            _w.WriteStartElement("w", "pPr", W);
            Val("pStyle", "Header");
            _w.WriteEndElement();
            Text(title, default);
            _w.WriteEndElement();
            _w.WriteEndElement();
        }
    }

    /// <summary>"Page X of Y", the numbers from Word's <c>PAGE</c> and <c>NUMPAGES</c> fields.</summary>
    private void Footer()
    {
        using (_w = _package.Part("word/footer1.xml", Ml + "footer+xml"))
        {
            _w.WriteStartElement("w", "ftr", W);
            _w.WriteStartElement("w", "p", W);
            _w.WriteStartElement("w", "pPr", W);
            Val("pStyle", "Footer");
            Val("jc", "center");
            _w.WriteEndElement();
            Text(OfficeText.PageWord, default);
            Field("PAGE");
            Text(OfficeText.OfWord, default);
            Field("NUMPAGES");
            _w.WriteEndElement();
            _w.WriteEndElement();
        }
    }

    private void Field(string instruction)
    {
        FieldChar("begin");
        _w.WriteStartElement("w", "r", W);
        _w.WriteStartElement("w", "instrText", W);
        _w.WriteAttributeString("xml", "space", null, "preserve");
        _w.WriteString(" " + instruction + " ");
        _w.WriteEndElement();
        _w.WriteEndElement();
        FieldChar("separate");
        Text("1", default);
        FieldChar("end");
    }

    private void FieldChar(string kind)
    {
        _w.WriteStartElement("w", "r", W);
        _w.WriteStartElement("w", "fldChar", W);
        Attr("fldCharType", kind);
        _w.WriteEndElement();
        _w.WriteEndElement();
    }

    private void Settings()
    {
        using (_w = _package.Part("word/settings.xml", Ml + "settings+xml"))
        {
            _w.WriteStartElement("w", "settings", W);
            Val("defaultTabStop", "720");
            _w.WriteStartElement("w", "compat", W);
            _w.WriteStartElement("w", "compatSetting", W);
            Attr("name", "compatibilityMode");
            Attr("uri", "http://schemas.microsoft.com/office/word");
            Attr("val", "15");
            _w.WriteEndElement();
            _w.WriteEndElement();
            _w.WriteEndElement();
        }
    }

    /// <summary>The bullets of each level, in turn.</summary>
    private static readonly string[] Bullets = ["•", "◦", "▪"];

    /// <summary>The numbering of each level, in turn: 1. a. i.</summary>
    private static readonly string[] Numbers = ["decimal", "lowerLetter", "lowerRoman"];

    private void Numbering()
    {
        using (_w = _package.Part("word/numbering.xml", Ml + "numbering+xml"))
        {
            _w.WriteStartElement("w", "numbering", W);
            for (int kind = 0; kind < 2; kind++)
            {
                _w.WriteStartElement("w", "abstractNum", W);
                Attr("abstractNumId", kind.ToString(CultureInfo.InvariantCulture));
                Val("multiLevelType", "hybridMultilevel");
                for (int level = 0; level < 9; level++)
                {
                    string ilvl = level.ToString(CultureInfo.InvariantCulture);
                    _w.WriteStartElement("w", "lvl", W);
                    Attr("ilvl", ilvl);
                    Val("start", "1");
                    Val("numFmt", kind == 0 ? "bullet" : Numbers[level % Numbers.Length]);
                    Val("lvlText", kind == 0 ? Bullets[level % Bullets.Length] : "%" + (level + 1).ToString(CultureInfo.InvariantCulture) + ".");
                    Val("lvlJc", "left");
                    _w.WriteStartElement("w", "pPr", W);
                    _w.WriteStartElement("w", "ind", W);
                    Attr("left", ((level + 1) * Step).ToString(CultureInfo.InvariantCulture));
                    Attr("hanging", "360");
                    _w.WriteEndElement();
                    _w.WriteEndElement();
                    _w.WriteEndElement();
                }

                _w.WriteEndElement();
            }

            for (int i = 0; i < _lists.Count; i++)
            {
                var (kind, level, start) = _lists[i];
                _w.WriteStartElement("w", "num", W);
                Attr("numId", (i + 1).ToString(CultureInfo.InvariantCulture));
                Val("abstractNumId", kind.ToString(CultureInfo.InvariantCulture));
                _w.WriteStartElement("w", "lvlOverride", W);
                Attr("ilvl", level.ToString(CultureInfo.InvariantCulture));
                Val("startOverride", (kind == 1 ? start : 1).ToString(CultureInfo.InvariantCulture));
                _w.WriteEndElement();
                _w.WriteEndElement();
            }

            _w.WriteEndElement();
        }
    }

    private void Styles()
    {
        using (_w = _package.Part("word/styles.xml", Ml + "styles+xml"))
        {
            _w.WriteStartElement("w", "styles", W);
            _w.WriteStartElement("w", "docDefaults", W);
            _w.WriteStartElement("w", "rPrDefault", W);
            _w.WriteStartElement("w", "rPr", W);
            Fonts("Calibri");
            Val("sz", "22");
            Val("szCs", "22");
            Val("lang", "en-US");
            _w.WriteEndElement();
            _w.WriteEndElement();
            _w.WriteStartElement("w", "pPrDefault", W);
            _w.WriteStartElement("w", "pPr", W);
            Spacing(null, "160", "259");
            _w.WriteEndElement();
            _w.WriteEndElement();
            _w.WriteEndElement();

            Style("paragraph", "Normal", "Normal", basedOn: null, isDefault: true);
            _w.WriteElementString("w", "qFormat", W, null);
            _w.WriteEndElement();

            Style("character", "DefaultParagraphFont", "Default Paragraph Font", basedOn: null, isDefault: true);
            Val("uiPriority", "1");
            _w.WriteElementString("w", "semiHidden", W, null);
            _w.WriteElementString("w", "unhideWhenUsed", W, null);
            _w.WriteEndElement();

            string[] sizes = ["32", "28", "26", "24", "22", "22"];
            for (int level = 1; level <= 6; level++)
            {
                string n = level.ToString(CultureInfo.InvariantCulture);
                Style("paragraph", "Heading" + n, "heading " + n, "Normal");
                Val("next", "Normal");
                Val("uiPriority", "9");
                _w.WriteElementString("w", "qFormat", W, null);
                _w.WriteStartElement("w", "pPr", W);
                _w.WriteElementString("w", "keepNext", W, null);
                _w.WriteElementString("w", "keepLines", W, null);
                Spacing(level == 1 ? "360" : "240", "80", null);
                Val("outlineLvl", (level - 1).ToString(CultureInfo.InvariantCulture));
                _w.WriteEndElement();
                _w.WriteStartElement("w", "rPr", W);
                _w.WriteElementString("w", "b", W, null);
                if (level >= 5)
                {
                    _w.WriteElementString("w", "i", W, null);
                }

                Val("color", "1F3864");
                Val("sz", sizes[level - 1]);
                Val("szCs", sizes[level - 1]);
                _w.WriteEndElement();
                _w.WriteEndElement();
            }

            Style("paragraph", "ListParagraph", "List Paragraph", "Normal");
            Val("uiPriority", "34");
            _w.WriteElementString("w", "qFormat", W, null);
            _w.WriteStartElement("w", "pPr", W);
            _w.WriteStartElement("w", "ind", W);
            Attr("left", "720");
            _w.WriteEndElement();
            _w.WriteElementString("w", "contextualSpacing", W, null);
            _w.WriteEndElement();
            _w.WriteEndElement();

            Style("paragraph", "Quote", "Quote", "Normal");
            Val("uiPriority", "29");
            _w.WriteElementString("w", "qFormat", W, null);
            _w.WriteStartElement("w", "pPr", W);
            _w.WriteStartElement("w", "pBdr", W);
            Border("left", "12", "8", "BFBFBF");
            _w.WriteEndElement();
            _w.WriteStartElement("w", "ind", W);
            Attr("left", "720");
            _w.WriteEndElement();
            _w.WriteEndElement();
            _w.WriteStartElement("w", "rPr", W);
            _w.WriteElementString("w", "i", W, null);
            Val("color", "404040");
            _w.WriteEndElement();
            _w.WriteEndElement();

            Style("paragraph", "CodeBlock", "Code Block", "Normal");
            _w.WriteStartElement("w", "pPr", W);
            _w.WriteStartElement("w", "shd", W);
            Attr("val", "clear");
            Attr("color", "auto");
            Attr("fill", "F2F2F2");
            _w.WriteEndElement();
            Spacing("0", "0", "240");
            _w.WriteEndElement();
            _w.WriteStartElement("w", "rPr", W);
            Fonts("Consolas");
            Val("sz", "20");
            Val("szCs", "20");
            _w.WriteEndElement();
            _w.WriteEndElement();

            Style("paragraph", "Rule", "Rule", "Normal");
            _w.WriteStartElement("w", "pPr", W);
            _w.WriteStartElement("w", "pBdr", W);
            Border("bottom", "6", "1", "A6A6A6");
            _w.WriteEndElement();
            _w.WriteEndElement();
            _w.WriteEndElement();

            Style("paragraph", "Header", "header", "Normal");
            Val("uiPriority", "99");
            _w.WriteStartElement("w", "pPr", W);
            Spacing(null, "0", "240");
            _w.WriteEndElement();
            _w.WriteStartElement("w", "rPr", W);
            Val("color", "595959");
            Val("sz", "18");
            Val("szCs", "18");
            _w.WriteEndElement();
            _w.WriteEndElement();

            Style("paragraph", "Footer", "footer", "Header");
            Val("uiPriority", "99");
            _w.WriteEndElement();

            Style("character", "Code", "Code", "DefaultParagraphFont");
            _w.WriteStartElement("w", "rPr", W);
            Fonts("Consolas");
            Val("sz", "20");
            Val("szCs", "20");
            _w.WriteStartElement("w", "shd", W);
            Attr("val", "clear");
            Attr("color", "auto");
            Attr("fill", "F2F2F2");
            _w.WriteEndElement();
            _w.WriteEndElement();
            _w.WriteEndElement();

            Style("character", "Hyperlink", "Hyperlink", "DefaultParagraphFont");
            Val("uiPriority", "99");
            _w.WriteElementString("w", "unhideWhenUsed", W, null);
            _w.WriteStartElement("w", "rPr", W);
            Val("color", "0563C1");
            Val("u", "single");
            _w.WriteEndElement();
            _w.WriteEndElement();

            _w.WriteEndElement();
        }
    }

    /// <summary>A <c>w:style</c> begun with its name and parent; the caller adds the rest and ends it.</summary>
    private void Style(string type, string id, string name, string? basedOn, bool isDefault = false)
    {
        _w.WriteStartElement("w", "style", W);
        Attr("type", type);
        if (isDefault)
        {
            Attr("default", "1");
        }

        Attr("styleId", id);
        Val("name", name);
        if (basedOn is not null)
        {
            Val("basedOn", basedOn);
        }
    }

    private void Fonts(string font)
    {
        _w.WriteStartElement("w", "rFonts", W);
        Attr("ascii", font);
        Attr("hAnsi", font);
        Attr("eastAsia", font);
        Attr("cs", font);
        _w.WriteEndElement();
    }

    private void Spacing(string? before, string? after, string? line)
    {
        _w.WriteStartElement("w", "spacing", W);
        if (before is not null)
        {
            Attr("before", before);
        }

        if (after is not null)
        {
            Attr("after", after);
        }

        if (line is not null)
        {
            Attr("line", line);
            Attr("lineRule", "auto");
        }

        _w.WriteEndElement();
    }

    private void Border(string side, string size, string space, string color)
    {
        _w.WriteStartElement("w", side, W);
        Attr("val", "single");
        Attr("sz", size);
        Attr("space", space);
        Attr("color", color);
        _w.WriteEndElement();
    }

    private void Val(string element, string value)
    {
        _w.WriteStartElement("w", element, W);
        Attr("val", value);
        _w.WriteEndElement();
    }

    private void Attr(string name, string value) => _w.WriteAttributeString("w", name, W, value);

    private static string Plain(IReadOnlyList<MarkdownInline> inlines) =>
        string.Concat(inlines.Select(i => i switch
        {
            TextRun t => t.Text,
            LinkRun l => l.Text,
            ImageRun m => m.Alt,
            HardBreak => " ",
            _ => "",
        }));
}
