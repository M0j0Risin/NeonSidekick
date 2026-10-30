using System.IO.Compression;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Office;
using NeonSidekick.Pdf;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI.Markdown;

namespace NeonSidekick.Tests;

/// <summary>
/// The Word writer and its converter (2026-10-29, the Office writing plan): Markdown's every construct into a document the Open XML SDK's
/// validator passes and our own <see cref="WordReader"/> reads back, the header and footer, the pictures (found in the sandbox only, their
/// metadata out, a turned photo turned), a <c>.txt</c>'s lines, and the target rules.
/// </summary>
public sealed class WordWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "docx-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly DocxConverter _converter;

    public WordWriterTests()
    {
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, _time);
        _converter = new DocxConverter(_files, _time);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Put(string relative, string text) => Put(relative, Encoding.UTF8.GetBytes(text));

    private void Put(string relative, byte[] bytes)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private byte[] Bytes(string relative) => File.ReadAllBytes(Path.Combine(_root, relative));

    private string Convert(DocxRequest request) => _converter.Convert(request, CancellationToken.None);

    internal static string ReadBack(byte[] file)
    {
        using var stream = new MemoryStream(file, writable: false);
        return OfficeReader.Read(stream, "out.docx", OfficeKind.Word, new OfficeRequest(), OfficeLimits.Default, CancellationToken.None);
    }

    private static string Part(byte[] file, string name) => OfficeWriterTests.Part(file, name);

    private static bool HasPart(byte[] file, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(file, writable: false), ZipArchiveMode.Read);
        return zip.GetEntry(name) is not null;
    }

    private static byte[] Write(string markdown, string? header = null, Func<string, WordPicture?>? picture = null, WordPage? page = null)
    {
        using var buffer = new MemoryStream();
        WordWriter.Write(buffer, new MarkdigParser().Parse(markdown), page ?? new WordPage(), header, header, picture ?? (_ => null), new ManualTimeProvider());
        return buffer.ToArray();
    }

    private const string Rich =
        "# Plan\n\nSome **bold**, *italic* and `code` text with a [link](https://example.com/a?b=1&c=2).\n\n" +
        "## Steps\n\n1. First\n2. Second\n   - nested bullet\n   - another\n3. Third\n\n" +
        "- a bullet\n\n  with a second paragraph\n\n" +
        "> A quote\n>\n> > nested\n\n" +
        "```csharp\nvar x = 1;\n\n\tindented();\n```\n\n---\n\n" +
        "| Left | Middle | Right |\n|:-----|:------:|------:|\n| a | b | c |\n| 1 | 2 |\n\n" +
        "Line one  \nline two\n\n###### Small\n";

    // ── the writer ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryConstruct_PassesTheValidator_AndReadsBack()
    {
        byte[] file = Write(Rich, header: "The plan");
        OfficeSchema.ValidDocument(file);
        string text = ReadBack(file);
        Assert.Contains("# Plan", text);
        Assert.Contains("## Steps", text);
        Assert.Contains("###### Small", text);
        Assert.Contains("bold", text);
        Assert.Contains("First", text);
        Assert.Contains("nested bullet", text);
        Assert.Contains("A quote", text);
        Assert.Contains("var x = 1;", text);
        Assert.Contains("| Left", text);
        Assert.Contains("link", text);
        // The title is the header's, never a body paragraph.
        Assert.DoesNotContain("The plan", text);
    }

    [Fact]
    public void Header_ShowsTheTitle_FooterCountsPages_SettingsAreModern()
    {
        byte[] file = Write("Body", header: "Quarterly report");
        Assert.Contains("Quarterly report", Part(file, "word/header1.xml"));
        string footer = Part(file, "word/footer1.xml");
        Assert.Contains(" PAGE ", footer);
        Assert.Contains(" NUMPAGES ", footer);
        Assert.Contains(OfficeText.PageWord.Trim(), footer);
        Assert.Contains("compatibilityMode", Part(file, "word/settings.xml"));
        Assert.Contains("w:val=\"15\"", Part(file, "word/settings.xml"));
        Assert.Contains("<dc:title>Quarterly report</dc:title>", Part(file, "docProps/core.xml"));
        Assert.Contains("heading 1", Part(file, "word/styles.xml"));

        byte[] plain = Write("Body");
        Assert.False(HasPart(plain, "word/header1.xml"));
        Assert.True(HasPart(plain, "word/footer1.xml"));
        OfficeSchema.ValidDocument(plain);
    }

    [Fact]
    public void EveryList_RestartsItsNumbering_AtItsStart()
    {
        byte[] file = Write("1. a\n2. b\n\npara\n\n3. c\n4. d\n");
        string numbering = Part(file, "word/numbering.xml");
        Assert.Contains("<w:num w:numId=\"1\">", numbering);
        Assert.Contains("<w:num w:numId=\"2\">", numbering);
        Assert.Contains("<w:startOverride w:val=\"3\" />", numbering.Replace("\"/>", "\" />", StringComparison.Ordinal));
        OfficeSchema.ValidDocument(file);
    }

    [Fact]
    public void Links_OnlyWebAndMail_BecomeHyperlinks()
    {
        byte[] file = Write("[web](https://example.com) [mail](mailto:a@b.c) [run](javascript:alert(1)) [local](file:///C:/x.txt) [rel](docs/x.md)");
        string rels = Part(file, "word/_rels/document.xml.rels");
        Assert.Contains("https://example.com", rels);
        Assert.Contains("mailto:a@b.c", rels);
        Assert.DoesNotContain("javascript", rels);
        Assert.DoesNotContain("file:", rels);
        string body = Part(file, "word/document.xml");
        Assert.Contains("run (javascript:alert(1))", body);
        Assert.Contains("rel (docs/x.md)", body);
        OfficeSchema.ValidDocument(file);
    }

    [Theory]
    [InlineData(PdfPaperSize.Letter, false, "12240", "15840")]
    [InlineData(PdfPaperSize.A4, false, "11906", "16838")]
    [InlineData(PdfPaperSize.Legal, true, "20160", "12240")]
    public void ThePage_IsThePaperAsked(PdfPaperSize paper, bool landscape, string width, string height)
    {
        byte[] file = Write("Body", page: new WordPage(paper, landscape));
        string body = Part(file, "word/document.xml");
        Assert.Contains($"w:w=\"{width}\" w:h=\"{height}\"", body);
        Assert.Equal(landscape, body.Contains("w:orient=\"landscape\"", StringComparison.Ordinal));
        Assert.Contains("w:top=\"1440\"", body);
    }

    [Fact]
    public void APicture_FitsTheRoomItsListQuoteOrCellLeaves_AndATableIsMovedIn()
    {
        // 2026-10-29, review P2-10, P3-5, P3-6.
        var wide = new WordPicture(StripFixtures.Png(), ImageFormats.Png, 4000, 100);
        string markdown = "- ![a](w.png)\n\n> ![b](w.png)\n\n| h |\n|---|\n| ![c](w.png) |\n\n- item\n\n  | h |\n  |---|\n  | x |\n\n> ## Quoted heading\n";
        byte[] file = Write(markdown, picture: _ => wide);
        OfficeSchema.ValidDocument(file);
        string body = Part(file, "word/document.xml");
        var extents = System.Text.RegularExpressions.Regex.Matches(body, "<wp:extent cx=\"(\\d+)\"").Select(m => long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        int text = WordWriter.TextWidth(new WordPage());
        Assert.Equal([(text - 720) * 635L, (text - 720) * 635L, (text - 216) * 635L], extents);
        Assert.Contains("<w:tblInd w:w=\"720\" w:type=\"dxa\" />", body, StringComparison.Ordinal);
        Assert.Contains("<w:pStyle w:val=\"Heading2\" /><w:pBdr><w:left w:val=\"single\" w:sz=\"12\" w:space=\"8\" w:color=\"BFBFBF\" /></w:pBdr><w:ind w:left=\"720\" />", body, StringComparison.Ordinal);
    }

    [Fact]
    public void APicturesOwnDpi_SizesIt()
    {
        // 2026-10-29, review P6-4: 300 pixels at 300 dpi is an inch.
        Assert.Equal((914400L, 914400L), WordWriter.Fit(300, 300, new WordPage(), int.MaxValue, 300));
        Assert.Equal((300 * 9525L, 300 * 9525L), WordWriter.Fit(300, 300, new WordPage(), int.MaxValue, 0));
        Assert.Equal(1000 * 635L, WordWriter.Fit(4000, 100, new WordPage(), 1000, 0).Cx);
    }

    [Fact]
    public void NestedBlocks_PassTheValidator()
    {
        // 2026-10-29, review P3-13: pictures in a table, a list, a quote and a link; a heading, code and a table as a list item's first
        // block; a list in a quote; a heading in a quote.
        var picture = new WordPicture(StripFixtures.Png(), ImageFormats.Png, 16, 16);
        const string Nested =
            "| a | b |\n|---|---|\n| ![p](x.png) | [![q](x.png)](https://example.com) |\n\n" +
            "- ![r](x.png) in a list\n- # Heading item\n-     code item\n- | t |\n  |---|\n  | 1 |\n\n" +
            "> ![s](x.png) quoted\n>\n> - listed in a quote\n>   1. and numbered\n>\n> ## A quoted heading\n>\n> ```\n> code in a quote\n> ```\n";
        byte[] file = Write(Nested, header: "Nested", picture: _ => picture);
        OfficeSchema.ValidDocument(file);
        string text = ReadBack(file);
        Assert.Contains("Heading item", text);
        Assert.Contains("listed in a quote", text);
        Assert.Contains("code in a quote", text);
    }

    [Fact]
    public void ABigPicture_IsFittedToTheTextWidth_InProportion()
    {
        var (cx, cy) = WordWriter.Fit(4000, 2000, new WordPage());
        Assert.Equal((12240 - 2880) * 635L, cx);
        Assert.Equal(cx / 2, cy);
        Assert.Equal((100 * 9525L, 50 * 9525L), WordWriter.Fit(100, 50, new WordPage()));
    }

    // ── pictures ────────────────────────────────────────────────────────────

    [Fact]
    public void APicture_IsEmbeddedOnce_AndAMissingOneIsItsAltText()
    {
        byte[] png = StripFixtures.Png();
        Put("pics/a.png", png);
        Put("notes.md", "![first](pics/a.png) ![again](pics/a.png) ![gone](pics/none.png) ![web](https://example.com/x.png)");
        string result = Convert(new DocxRequest("notes.md"));
        Assert.StartsWith("Made notes.docx", result);
        Assert.Contains("2 pictures", result);
        Assert.Contains("2 pictures not found", result);
        byte[] file = Bytes("notes.docx");
        OfficeSchema.ValidDocument(file);
        Assert.True(HasPart(file, "word/media/image1.png"));
        Assert.False(HasPart(file, "word/media/image2.png"));
        string body = Part(file, "word/document.xml");
        Assert.Contains("descr=\"first\"", body);
        Assert.Contains("[picture: gone]", body);
        Assert.Contains("[picture: web]", body);
    }

    [Fact]
    public void APicture_IsJudgedByItsBytes_NotItsName()
    {
        Put("fake.png", Encoding.ASCII.GetBytes("RIFF0000WEBPVP8 not really"));
        Put("notes.md", "![x](fake.png)");
        Assert.Contains("1 picture not found", Convert(new DocxRequest("notes.md")));
    }

    [Fact]
    public void APicture_LosesItsMetadata()
    {
        byte[] jpeg = ImageFixtures.ExifJpeg();
        Assert.True(ImageFixtures.HasGps(jpeg));
        Put("photo.jpg", jpeg);
        Put("notes.md", "![photo](photo.jpg)");
        Assert.Contains("1 picture", Convert(new DocxRequest("notes.md")));
        byte[] embedded = OfficeWriterTests.Entry(Bytes("notes.docx"), "word/media/image1.jpeg");
        Assert.False(ImageFixtures.HasGps(embedded));
        Assert.False(ImageFixtures.HasCopyright(embedded));
    }

    [Fact]
    public void ATurnedPhoto_IsTurnedInThePixels()
    {
        var picture = WordPicture.From(ImageFixtures.SidewaysJpeg(40, 20));
        Assert.NotNull(picture);
        Assert.Equal((20, 40), (picture.Width, picture.Height));
        Assert.Null(MetadataStripper.ExifOrientation(picture.Bytes) is > 1 ? picture : null);
        Assert.Equal((20, 40), (ImageEditor.Info(picture.Bytes)!.Width, ImageEditor.Info(picture.Bytes)!.Height));
    }

    [Fact]
    public void APictureWordCantEmbed_IsNeverRead_SoItSpendsNoBudget()
    {
        // 2026-10-29, review P3-1.
        byte[] png = StripFixtures.Png();
        Put("big.webp", new byte[png.Length * 4]);
        Put("small.png", png);
        var pictures = new SandboxPictures(_files, _root, budget: png.Length * 2);
        static bool OnlyPng(string full) => full.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        Assert.Null(pictures.Bytes("big.webp", OnlyPng));
        Assert.False(pictures.OverBudget);
        Assert.Equal(png, pictures.Bytes("small.png", OnlyPng));
        Assert.Null(pictures.Bytes("big.webp"));   // read with no filter, it is counted, and over
        Assert.True(pictures.OverBudget);
    }

    [Fact]
    public void ALinkedPicture_IsEmbedded_InsideItsHyperlink()
    {
        // 2026-10-29, review P3-2.
        var picture = new WordPicture(StripFixtures.Png(), ImageFormats.Png, 16, 16);
        byte[] file = Write("[![logo](x.png)](https://example.com/home) and [ ![spaced](x.png) ](https://example.com/b)", picture: _ => picture);
        OfficeSchema.ValidDocument(file);
        string body = Part(file, "word/document.xml");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(body, "<w:hyperlink r:id=\"rId\\d+\"><w:r><w:drawing>").Count);
        Assert.Contains("Target=\"https://example.com/home\"", Part(file, "word/_rels/document.xml.rels"), StringComparison.Ordinal);
        // A picture in a link to another kind of target stays a plain picture.
        byte[] local = Write("[![logo](x.png)](notes.md)", picture: _ => picture);
        OfficeSchema.ValidDocument(local);
        Assert.DoesNotContain("<w:hyperlink", Part(local, "word/document.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void ALinksTarget_IsEscaped_AsARelationshipHoldsIt()
    {
        // 2026-10-29, review P1-6.
        byte[] file = Write("[a](<https://example.com/a b/é>) and [b](https://bücher.example/x)");
        OfficeSchema.ValidDocument(file);
        string rels = Part(file, "word/_rels/document.xml.rels");
        Assert.Contains("Target=\"https://example.com/a%20b/%C3%A9\"", rels, StringComparison.Ordinal);
        Assert.Contains("Target=\"https://xn--bcher-kva.example/x\"", rels, StringComparison.Ordinal);
        Assert.False(WordWriter.TryLinkable("file:///c:/x", out _));
        Assert.True(WordWriter.TryLinkable("mailto:a@example.com", out string mail));
        Assert.Equal("mailto:a@example.com", mail);
    }

    [Fact]
    public void ADataPicture_IsDecoded_AndBadBase64IsTheAltText()
    {
        string data = "data:image/png;base64," + System.Convert.ToBase64String(StripFixtures.Png());
        Put("notes.md", $"![inline]({data}) ![bad](data:image/png;base64,!!!) ![svg](data:image/svg+xml;base64,PHN2Zz4=)");
        string result = Convert(new DocxRequest("notes.md"));
        Assert.Contains("1 picture", result);
        Assert.Contains("2 pictures not found", result);
    }

    [Fact]
    public void APictureOutsideTheSandbox_IsNeverRead()
    {
        string outside = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "outside-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(outside, StripFixtures.Png());
        try
        {
            Put("notes.md", $"![out]({new Uri(outside).AbsoluteUri}) ![up](../{Path.GetFileName(outside)})");
            Assert.Contains("2 pictures not found", Convert(new DocxRequest("notes.md")));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    // ── the converter ───────────────────────────────────────────────────────

    [Fact]
    public void AFile_GoesBesideIt_TitledByItsFirstHeading()
    {
        Put("docs/report.md", "# Q3 report\n\nBody");
        Assert.StartsWith("Made " + Path.Combine("docs", "report.docx") + " (", Convert(new DocxRequest("docs/report.md")));
        byte[] file = Bytes("docs/report.docx");
        Assert.Contains("Q3 report", Part(file, "word/header1.xml"));
        Assert.Contains("<dc:title>Q3 report</dc:title>", Part(file, "docProps/core.xml"));
    }

    [Fact]
    public void AFileWithNoHeading_HasNoHeader_AndItsStemAsTitle()
    {
        Put("notes.md", "Just a line");
        Convert(new DocxRequest("notes.md"));
        byte[] file = Bytes("notes.docx");
        Assert.False(HasPart(file, "word/header1.xml"));
        Assert.Contains("<dc:title>notes</dc:title>", Part(file, "docProps/core.xml"));
    }

    [Fact]
    public void APlan_GoesToTheTopOfTheWorkingFolder()
    {
        Put(".neon/plans/x.md", "# Plan\n\nStep");
        Assert.StartsWith("Made x.docx", Convert(new DocxRequest(".neon/plans/x.md")));
        Assert.True(File.Exists(Path.Combine(_root, "x.docx")));
    }

    [Fact]
    public void ALongSource_IsConvertedWhole_PastTheReadWindow()
    {
        // 100,000 characters, past WorkingDirectory.ReadText's window (the plan's ReadText trap): the last heading must be there.
        var text = new StringBuilder("# Start\n\n");
        while (text.Length < 100_000)
        {
            text.Append("A paragraph of plan text that goes on for a while to fill the file.\n\n");
        }

        text.Append("## The very end\n\nDone.\n");
        Put("long.md", text.ToString());
        Assert.StartsWith("Made long.docx", Convert(new DocxRequest("long.md")));
        Assert.Contains("The very end", Part(Bytes("long.docx"), "word/document.xml"));
    }

    [Fact]
    public void AnUpperCaseMd_IsMarkdown()
    {
        Put("NOTES.MD", "# Notes\n\n- **one**\n");
        Convert(new DocxRequest("NOTES.MD"));
        string body = Part(Bytes("NOTES.docx"), "word/document.xml");
        Assert.Contains("Heading1", body);
        Assert.Contains("<w:b />", body.Replace("<w:b/>", "<w:b />", StringComparison.Ordinal));
        Assert.DoesNotContain("**", body);
    }

    [Fact]
    public void Text_IsNamedByTitleAndMinute()
    {
        string result = Convert(new DocxRequest(Markdown: "Hello", Title: "Weekly notes"));
        string name = "Weekly-notes-" + _time.GetLocalNow().ToString("yyyy-MM-dd-HHmm", System.Globalization.CultureInfo.InvariantCulture) + ".docx";
        Assert.StartsWith("Made " + name, result);
        Assert.Contains("Weekly notes", Part(Bytes(name), "word/header1.xml"));
    }

    [Fact]
    public void ATextFile_KeepsItsLines()
    {
        Put("poem.txt", "Roses are red\nViolets are blue\n\nSecond verse\n");
        Convert(new DocxRequest("poem.txt"));
        byte[] file = Bytes("poem.docx");
        OfficeSchema.ValidDocument(file);
        string body = Part(file, "word/document.xml");
        Assert.Contains("Roses are red</w:t></w:r><w:r><w:br />", body.Replace("<w:br/>", "<w:br />", StringComparison.Ordinal));
        Assert.Equal(2, DocxConverter.Paragraphs("a\nb\n\n\nc").Blocks.Count);
    }

    [Fact]
    public void AnUnnamedDefault_NeverReplaces_ANamedOneOnlyWithOverwrite()
    {
        Put("notes.md", "Body");
        Put("notes.docx", "old");
        Assert.StartsWith("Made notes-2.docx", Convert(new DocxRequest("notes.md")));
        Assert.StartsWith("Error:", Convert(new DocxRequest("notes.md", To: "notes.docx")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "notes.docx")));
        Assert.StartsWith("Replaced notes.docx", Convert(new DocxRequest("notes.md", To: "notes.docx", Overwrite: true)));
    }

    [Theory]
    [InlineData("out.pdf")]
    [InlineData("out.xlsx")]
    [InlineData("~$out.docx")]
    public void ATargetOfAnotherKind_OrALockFileName_IsRefused(string to)
    {
        Put("notes.md", "Body");
        Assert.StartsWith("Error:", Convert(new DocxRequest("notes.md", To: to)));
    }

    [Fact]
    public void ATargetWithAnotherEnding_GetsDocxAdded()
    {
        Put("notes.md", "Body");
        Assert.StartsWith("Made report.v2.docx", Convert(new DocxRequest("notes.md", To: "report.v2")));
    }

    [Fact]
    public void Refusals_ComeBeforeAnyWrite()
    {
        Put("data.csv", "a,b");
        Put("empty.md", "   \n");
        Put("notes.md", "Body");
        Assert.Equal(OfficeText.OneDocumentSource, Convert(new DocxRequest()));
        Assert.Equal(OfficeText.OneDocumentSource, Convert(new DocxRequest("notes.md", Markdown: "x")));
        Assert.Equal(OfficeText.NotADocumentSource("data.csv"), Convert(new DocxRequest("data.csv")));
        Assert.Equal(OfficeText.EmptyDocument("empty.md"), Convert(new DocxRequest("empty.md")));
        Assert.StartsWith("Error:", Convert(new DocxRequest("missing.md")));
        // .neon refuses the model; the user's own /docx writes through the app's pass, as /pdf does (WriteGuard: D2 refuses the model, not the user).
        Assert.StartsWith("Error:", Convert(new DocxRequest("notes.md", To: ".neon/out.docx")));
        Assert.False(File.Exists(Path.Combine(_root, ".neon", "out.docx")));
    }

    [Fact]
    public void OnlyMarkdownAndText_AreSources()
    {
        Assert.True(DocxConverter.IsSource("a.md"));
        Assert.True(DocxConverter.IsSource("a.MARKDOWN"));
        Assert.True(DocxConverter.IsSource("a.txt"));
        Assert.False(DocxConverter.IsSource("a.html"));
        Assert.False(DocxConverter.IsSource("a.docx"));
    }

    [Fact]
    public void DataPictures_OnlyTheThreeFormats()
    {
        Assert.NotNull(DocxConverter.DataPicture("data:image/png;base64,AAAA"));
        Assert.NotNull(DocxConverter.DataPicture("data:image/GIF;base64,AAAA"));
        Assert.Null(DocxConverter.DataPicture("data:image/webp;base64,AAAA"));
        Assert.Null(DocxConverter.DataPicture("data:image/png,raw"));
    }
}
