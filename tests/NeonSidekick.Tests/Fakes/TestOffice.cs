using System.IO.Compression;
using System.Text;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// Minimal Word, Excel and PowerPoint files built in memory for the <c>read_office</c> tests (2026-10-28, the Office plan), like
/// <see cref="SkillZip"/>: the parts each reader needs and the relationships that name them, in the Transitional dialect or the Strict
/// one (<c>purl.oclc.org/ooxml</c>), so both are read alike. A part's bytes and its zip size fields can be bent for the damage tests.
/// </summary>
public static class TestOffice
{
    /// <summary>One dialect's namespaces and relationship types.</summary>
    public sealed record Dialect(string W, string S, string P, string A, string R, string M, string RelBase)
    {
        public string Rel(string kind) => RelBase + kind;
    }

    public static readonly Dialect Transitional = new(
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main",
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main",
        "http://schemas.openxmlformats.org/presentationml/2006/main",
        "http://schemas.openxmlformats.org/drawingml/2006/main",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
        "http://schemas.openxmlformats.org/officeDocument/2006/math",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/");

    public static readonly Dialect Strict = new(
        "http://purl.oclc.org/ooxml/wordprocessingml/main",
        "http://purl.oclc.org/ooxml/spreadsheetml/main",
        "http://purl.oclc.org/ooxml/presentationml/main",
        "http://purl.oclc.org/ooxml/drawingml/main",
        "http://purl.oclc.org/ooxml/officeDocument/relationships",
        "http://purl.oclc.org/ooxml/officeDocument/math",
        "http://purl.oclc.org/ooxml/officeDocument/relationships/");

    private const string PackageRelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The other namespaces a Word body uses, declared on its root.</summary>
    public const string WordExtras =
        " xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"" +
        " xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"" +
        " xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"" +
        " xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"" +
        " xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\"";

    /// <summary>A zip of <paramref name="parts"/> (paths with forward slashes), each compressed as <paramref name="level"/> says.</summary>
    public static byte[] Zip(IEnumerable<(string Path, string Content)> parts, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in parts)
            {
                var entry = zip.CreateEntry(path, level);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A relationships part: each <c>(id, kind, target)</c>, a kind of <c>"hyperlink!"</c> marked external.</summary>
    public static string Rels(Dialect d, params (string Id, string Kind, string Target)[] rels)
    {
        var sb = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{PackageRelsNs}\">");
        foreach (var (id, kind, target) in rels)
        {
            bool external = kind.EndsWith('!');
            sb.Append($"<Relationship Id=\"{id}\" Type=\"{d.Rel(kind.TrimEnd('!'))}\" Target=\"{target}\"{(external ? " TargetMode=\"External\"" : "")}/>");
        }

        return sb.Append("</Relationships>").ToString();
    }

    /// <summary>Word's built-in heading and title styles, as <c>styles.xml</c> names them.</summary>
    public static string WordStyles(Dialect d) =>
        $"<w:styles xmlns:w=\"{d.W}\">" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/><w:basedOn w:val=\"Normal\"/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"MyHeading\"><w:name w:val=\"My Heading\"/><w:basedOn w:val=\"Heading2\"/></w:style>" +
        "</w:styles>";

    /// <summary>Lists: numId 1 bullets, numId 2 numbers.</summary>
    public static string WordNumbering(Dialect d) =>
        $"<w:numbering xmlns:w=\"{d.W}\">" +
        "<w:abstractNum w:abstractNumId=\"10\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/></w:lvl><w:lvl w:ilvl=\"1\"><w:numFmt w:val=\"bullet\"/></w:lvl></w:abstractNum>" +
        "<w:abstractNum w:abstractNumId=\"20\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"decimal\"/></w:lvl></w:abstractNum>" +
        "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"10\"/></w:num><w:num w:numId=\"2\"><w:abstractNumId w:val=\"20\"/></w:num>" +
        "</w:numbering>";

    /// <summary>
    /// A Word document with <paramref name="body"/> as <c>w:body</c>'s content, its styles and numbering, plus <paramref name="parts"/>
    /// (path → XML) and <paramref name="rels"/> on the document.
    /// </summary>
    public static byte[] Docx(string body, Dialect? dialect = null, IEnumerable<(string Path, string Content)>? parts = null, IEnumerable<(string Id, string Kind, string Target)>? rels = null)
    {
        var d = dialect ?? Transitional;
        var all = new List<(string, string)>
        {
            ("_rels/.rels", Rels(d, ("rId1", "officeDocument", "word/document.xml"))),
            ("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{d.W}\" xmlns:r=\"{d.R}\" xmlns:a=\"{d.A}\" xmlns:m=\"{d.M}\"{WordExtras}><w:body>{body}</w:body></w:document>"),
            ("word/styles.xml", WordStyles(d)),
            ("word/numbering.xml", WordNumbering(d)),
            ("word/_rels/document.xml.rels", Rels(d, [("rIdS", "styles", "styles.xml"), ("rIdN", "numbering", "numbering.xml"), .. rels ?? []])),
        };
        all.AddRange(parts ?? []);
        return Zip(all);
    }

    /// <summary>A paragraph of plain runs, styled when <paramref name="style"/> is given.</summary>
    public static string P(string text, string? style = null) =>
        "<w:p>" + (style is null ? "" : $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>") + $"<w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    /// <summary>A list paragraph.</summary>
    public static string Li(string text, int numId, int level = 0) =>
        $"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"{level}\"/><w:numId w:val=\"{numId}\"/></w:numPr></w:pPr><w:r><w:t>{text}</w:t></w:r></w:p>";

    /// <summary>One sheet of a workbook: its name, its worksheet XML's content (inside <c>worksheet</c>), its state, or a chart sheet.</summary>
    public sealed record SheetSpec(string Name, string Content, string? State = null, bool Chart = false, string? Raw = null);

    /// <summary>A worksheet's <c>sheetData</c> rows, with an optional <c>dimension</c>.</summary>
    public static string SheetData(string rows, string? dimension = null, string cols = "") =>
        (dimension is null ? "" : $"<dimension ref=\"{dimension}\"/>") + cols + "<sheetData>" + rows + "</sheetData>";

    /// <summary>A workbook of <paramref name="sheets"/>, its shared strings and styles (cellXfs as numFmtIds, custom codes by id).</summary>
    public static byte[] Xlsx(IEnumerable<SheetSpec> sheets, IEnumerable<string>? shared = null, IEnumerable<int>? xfs = null, IDictionary<int, string>? codes = null,
        bool date1904 = false, string definedNames = "", Dialect? dialect = null, bool sharedRaw = false, CompressionLevel level = CompressionLevel.Optimal)
    {
        var d = dialect ?? Transitional;
        var list = sheets.ToList();
        var sb = new StringBuilder($"<workbook xmlns=\"{d.S}\" xmlns:r=\"{d.R}\">");
        if (date1904)
        {
            sb.Append("<workbookPr date1904=\"1\"/>");
        }

        sb.Append("<sheets>");
        var rels = new List<(string, string, string)> { ("rIdSS", "sharedStrings", "sharedStrings.xml"), ("rIdST", "styles", "styles.xml") };
        var parts = new List<(string, string)>();
        for (int i = 0; i < list.Count; i++)
        {
            var sheet = list[i];
            string id = "rIdSheet" + (i + 1);
            sb.Append($"<sheet name=\"{sheet.Name}\" sheetId=\"{i + 1}\" r:id=\"{id}\"{(sheet.State is null ? "" : $" state=\"{sheet.State}\"")}/>");
            string target = sheet.Chart ? $"chartsheets/sheet{i + 1}.xml" : $"worksheets/sheet{i + 1}.xml";
            rels.Add((id, sheet.Chart ? "chartsheet" : "worksheet", target));
            parts.Add(("xl/" + target, sheet.Raw ?? (sheet.Chart ? $"<chartsheet xmlns=\"{d.S}\"/>" : $"<worksheet xmlns=\"{d.S}\" xmlns:r=\"{d.R}\">{sheet.Content}</worksheet>")));
        }

        sb.Append("</sheets>");
        if (definedNames.Length > 0)
        {
            sb.Append("<definedNames>").Append(definedNames).Append("</definedNames>");
        }

        sb.Append("</workbook>");
        var strings = new StringBuilder($"<sst xmlns=\"{d.S}\">");
        foreach (string s in shared ?? [])
        {
            strings.Append(sharedRaw ? s : $"<si><t xml:space=\"preserve\">{s}</t></si>");
        }

        strings.Append("</sst>");
        var styles = new StringBuilder($"<styleSheet xmlns=\"{d.S}\">");
        if (codes is { Count: > 0 })
        {
            styles.Append("<numFmts>");
            foreach (var (id, code) in codes)
            {
                styles.Append($"<numFmt numFmtId=\"{id}\" formatCode=\"{code}\"/>");
            }

            styles.Append("</numFmts>");
        }

        styles.Append("<cellXfs>");
        foreach (int id in xfs ?? [0])
        {
            styles.Append($"<xf numFmtId=\"{id}\"/>");
        }

        styles.Append("</cellXfs></styleSheet>");
        var all = new List<(string, string)>
        {
            ("_rels/.rels", Rels(d, ("rId1", "officeDocument", "xl/workbook.xml"))),
            ("xl/workbook.xml", sb.ToString()),
            ("xl/_rels/workbook.xml.rels", Rels(d, [.. rels])),
            ("xl/sharedStrings.xml", strings.ToString()),
            ("xl/styles.xml", styles.ToString()),
        };
        all.AddRange(parts);
        return Zip(all, level);
    }

    /// <summary>One slide: its shape tree's content, hidden or not, its notes' text.</summary>
    public sealed record SlideSpec(string Shapes, bool Hidden = false, string? Notes = null);

    /// <summary>A shape with a placeholder of <paramref name="placeholder"/> (null for none) holding <paramref name="lines"/>.</summary>
    public static string Shape(string? placeholder, params string[] lines) =>
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"s\"/><p:cNvSpPr/><p:nvPr>" + (placeholder is null ? "" : $"<p:ph type=\"{placeholder}\"/>") + "</p:nvPr></p:nvSpPr>" +
        "<p:txBody><a:bodyPr/>" + string.Concat(lines.Select(l => $"<a:p><a:r><a:t>{l}</a:t></a:r></a:p>")) + "</p:txBody></p:sp>";

    /// <summary>
    /// A presentation of <paramref name="slides"/>, listed in <c>presentation.xml</c> in that order but stored under file names in
    /// <paramref name="fileOrder"/> (1-based numbers), so a reader that goes by file names reads them wrongly.
    /// </summary>
    public static byte[] Pptx(IEnumerable<SlideSpec> slides, Dialect? dialect = null, IReadOnlyList<int>? fileOrder = null)
    {
        var d = dialect ?? Transitional;
        var list = slides.ToList();
        string ns = $"xmlns:p=\"{d.P}\" xmlns:a=\"{d.A}\" xmlns:r=\"{d.R}\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"";
        var sb = new StringBuilder($"<p:presentation {ns}><p:sldIdLst>");
        var rels = new List<(string, string, string)>();
        var parts = new List<(string, string)>();
        for (int i = 0; i < list.Count; i++)
        {
            int file = fileOrder?[i] ?? i + 1;
            sb.Append($"<p:sldId id=\"{256 + i}\" r:id=\"rIdSlide{i + 1}\"/>");
            rels.Add(($"rIdSlide{i + 1}", "slide", $"slides/slide{file}.xml"));
            parts.Add(($"ppt/slides/slide{file}.xml", $"<p:sld {ns}{(list[i].Hidden ? " show=\"0\"" : "")}><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{list[i].Shapes}</p:spTree></p:cSld></p:sld>"));
            if (list[i].Notes is { } notes)
            {
                parts.Add(($"ppt/slides/_rels/slide{file}.xml.rels", Rels(d, ("rIdN", "notesSlide", $"../notesSlides/notesSlide{file}.xml"))));
                parts.Add(($"ppt/notesSlides/notesSlide{file}.xml", $"<p:notes {ns}><p:cSld><p:spTree>{Shape("sldImg")}{Shape("body", notes)}</p:spTree></p:cSld></p:notes>"));
            }
        }

        sb.Append("</p:sldIdLst></p:presentation>");
        var all = new List<(string, string)>
        {
            ("_rels/.rels", Rels(d, ("rId1", "officeDocument", "ppt/presentation.xml"))),
            ("ppt/presentation.xml", sb.ToString()),
            ("ppt/_rels/presentation.xml.rels", Rels(d, [.. rels])),
        };
        all.AddRange(parts);
        return Zip(all);
    }

    /// <summary>
    /// <paramref name="zip"/> with <paramref name="entry"/>'s uncompressed size set to <paramref name="declared"/> in its local header and
    /// the central directory: a header that lies about how big the part is.
    /// </summary>
    public static byte[] ForgeSize(byte[] zip, string entry, uint declared)
    {
        var bytes = (byte[])zip.Clone();
        byte[] name = Encoding.UTF8.GetBytes(entry);
        for (int i = 0; i + 46 < bytes.Length; i++)
        {
            uint signature = BitConverter.ToUInt32(bytes, i);
            if (signature == 0x04034b50 && Named(bytes, i + 30, BitConverter.ToUInt16(bytes, i + 26), name))
            {
                BitConverter.GetBytes(declared).CopyTo(bytes, i + 22);
            }
            else if (signature == 0x02014b50 && Named(bytes, i + 46, BitConverter.ToUInt16(bytes, i + 28), name))
            {
                BitConverter.GetBytes(declared).CopyTo(bytes, i + 24);
            }
        }

        return bytes;
    }

    private static bool Named(byte[] bytes, int at, int length, byte[] name) =>
        length == name.Length && at + length <= bytes.Length && bytes.AsSpan(at, length).SequenceEqual(name);
}
