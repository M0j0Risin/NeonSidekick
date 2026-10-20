using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Claude;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Pdf;
using NeonSidekick.Sessions;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The pieces under an attached PDF (2026-10-14, the PDF plan's phase 3): the paste store's token and labels, the pasted path's rule, the
/// history's part and its text, the endpoints that take text, the stored session, the picture budget and the compactor's estimate.
/// </summary>
public sealed class AttachPdfTests
{
    private static readonly byte[] Bytes = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF\n");

    /// <summary>The attached file's full path, spelled as the machine spells one (2026-10-17, the Mac pass: off Windows a backslash is
    /// no separator, so <c>C:\docs\report.pdf</c> was one file name and the labels read the whole path).</summary>
    private static readonly string ReportPath = OperatingSystem.IsWindows() ? @"C:\docs\report.pdf" : "/docs/report.pdf";

    private static ImageAttachment Document(string? path = null, string text = "--- page 1 ---\nHello", int pages = 1) =>
        new(path ?? ReportPath, Bytes, PdfFile.MediaType, 0, 0) { DocumentText = Task.FromResult(new PdfDocumentText(text, pages, pages)) };

    [Fact]
    public void ThePasteStore_NumbersPdfsApart_AndLabelsThemByName()
    {
        var pastes = new PasteBlocks();
        var picture = new ImageAttachment("a.png", [1, 2], "image/png", 1, 1);
        char first = pastes.AddImage(picture);
        char pdf = pastes.AddDocument(Document());
        char second = pastes.AddImage(picture with { Path = "b.png" });

        Assert.Equal("[Image #1]", pastes.LabelOf(first));
        Assert.Equal("[PDF #1 report.pdf]", pastes.LabelOf(pdf));
        Assert.Equal("[Image #2]", pastes.LabelOf(second));
        Assert.Equal(2, pastes.ImageCount);
        Assert.Equal(1, pastes.DocumentCount);
        Assert.Equal(3, pastes.ImagesIn(new string([first, pdf, second])).Count);
        Assert.Equal("see [PDF #1 report.pdf]", pastes.Expand("see " + pdf));
        Assert.Null(pastes.Original(3));   // a PDF is no picture for generate_image
    }

    [Fact]
    public void ASentPdfLabel_ReadsBackAsTheDraftLabel()
    {
        Assert.Equal("[PDF #2, from docs/report.pdf]", PdfText.DocumentSentLabel(2, "docs/report.pdf"));
        Assert.Equal("see [PDF #2 report.pdf] and [Image #1]", PasteBlocks.WithoutSavedPaths("see [PDF #2, from docs/report.pdf] and [Image #1, saved as C:\\p\\x.png]"));
    }

    /// <summary>A name or folder holding a ']' keeps its label whole (2026-10-20, review: the labels' patterns stopped at the first ']').</summary>
    [Fact]
    public void APdfLabel_WithABracketInItsPath_ReadsBackWhole()
    {
        Assert.Equal("see [PDF #1 a.pdf] and [PDF #2 report [v2].pdf]",
            PasteBlocks.WithoutSavedPaths("see [PDF #1, from docs/[old]/a.pdf] and [PDF #2, from report [v2].pdf]"));
        string line = "see [PDF #1 a.pdf] and [PDF #2 report [v2].pdf] then";
        Assert.Equal(["[PDF #1 a.pdf]", "[PDF #2 report [v2].pdf]"], App.ConversationRewind.DocumentLabels(line).Select(l => line.Substring(l.Start, l.Length)));
    }

    /// <summary>The same PDF attached twice, each sent as a copy (its text cut), keeps each its own number (2026-10-20, review).</summary>
    [Fact]
    public void TheSamePdfAttachedTwice_EachCopyKeepsItsNumber()
    {
        var pastes = new PasteBlocks();
        var first = Document() with { Bytes = [.. Bytes] };
        var second = Document() with { Bytes = [.. Bytes] };
        pastes.AddDocument(first);
        pastes.AddDocument(second);

        Assert.Equal(1, pastes.DocumentNumberOf(first with { DocumentText = Task.FromResult(new PdfDocumentText("cut", 1, 1)) }));
        Assert.Equal(2, pastes.DocumentNumberOf(second with { DocumentText = Task.FromResult(new PdfDocumentText("cut", 1, 1)) }));
    }

    [Fact]
    public void APastedPdfPath_CountsOnlyWhenDocumentsAreTaken()
    {
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "report.pdf");
            File.WriteAllBytes(file, Bytes);
            Assert.False(ImageFile.TryPastedPaths(file, out _));
            Assert.True(ImageFile.TryPastedPaths(file, out var paths, documents: true));
            Assert.Equal(file, Assert.Single(paths));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheHistoryPart_IsTheNativePdf_NamedAndCarryingItsText()
    {
        var part = ConversationHistory.ImagePart(Document());
        Assert.Equal(PdfFile.MediaType, part.MediaType);
        Assert.Equal("report.pdf", part.Name);
        Assert.Equal(ReportPath, ConversationHistory.PicturePath(part));
        Assert.Equal("(the PDF report.pdf, attached from " + ReportPath + ": its text, 1 page)\n--- page 1 ---\nHello", ConversationHistory.DocumentText(part));
        Assert.True(ConversationHistory.IsDocument(part));
    }

    [Fact]
    public void AttachedText_SaysWhatWasLeft_AndWhyThereIsNone()
    {
        Assert.Equal("(the PDF r.pdf, attached from r.pdf: its text, pages 1–2 of 5)\nbody\n(the rest is not attached; read_pdf reads it, pages \"3-\")",
            PdfText.AttachedText("r.pdf", "r.pdf", new PdfDocumentText("body", 5, 2)));
        Assert.Equal("(the PDF r.pdf, attached from r.pdf: no text could be read (no browser))",
            PdfText.AttachedText("r.pdf", "r.pdf", new PdfDocumentText("", 0, 0, Problem: "no browser")));
    }

    [Fact]
    public void AnEndpointThatTakesText_GetsThePdfsText()
    {
        var message = new ChatMessage(ChatRole.User, [new TextContent("see [PDF #1, from r.pdf]"), ConversationHistory.ImagePart(Document())]);
        var plain = new ChatMessage(ChatRole.User, "hi");

        var sent = ConversationHistory.DocumentsAsText([plain, message]).ToList();

        Assert.Same(plain, sent[0]);
        Assert.DoesNotContain(sent[1].Contents, c => c is DataContent);
        Assert.Contains("--- page 1 ---\nHello", sent[1].Text, StringComparison.Ordinal);
        Assert.Contains(message.Contents, c => c is DataContent);   // the history's own message untouched
    }

    [Fact]
    public void TheClaudeCodeServer_GetsThePdfsTextInItsTurn()
    {
        var turn = ClaudeServerInput.Split([new ChatMessage(ChatRole.User, [new TextContent("see it"), ConversationHistory.ImagePart(Document())])]);
        var text = string.Join("\n", turn.NewContent.OfType<TextContent>().Select(t => t.Text));
        Assert.Contains("see it", text, StringComparison.Ordinal);
        Assert.Contains("--- page 1 ---\nHello", text, StringComparison.Ordinal);
        Assert.DoesNotContain(turn.NewContent, c => c is DataContent);
    }

    [Fact]
    public void AStoredSession_KeepsThePdfsText_NotItsBytes()
    {
        var message = new ChatMessage(ChatRole.User, [new TextContent("see"), ConversationHistory.ImagePart(Document())]);
        var stored = SessionHistory.Store(message);
        Assert.All(stored.Parts, p => Assert.Equal(StoredPart.TextKind, p.Kind));
        Assert.Contains(stored.Parts, p => p.Text!.Contains("--- page 1 ---\nHello", StringComparison.Ordinal));
        var restored = SessionHistory.Restore(stored)!;
        Assert.DoesNotContain(restored.Contents, c => c is DataContent);
    }

    /// <summary>
    /// A restored session's PDF text is no part of what the user typed, and its file goes back on the line at a rewind (2026-10-20, review:
    /// the rewind's user line joined it in, up to 100,000 characters, and the PDF was not put back). A second save keeps the mark; a session
    /// stored before the mark is read by its text's opening.
    /// </summary>
    [Fact]
    public void ARestoredSessionsPdfText_IsNoPartOfTheUsersLine_AndNamesItsFile()
    {
        var message = new ChatMessage(ChatRole.User, [new TextContent("summarize [PDF #1, from " + ReportPath + "]"), ConversationHistory.ImagePart(Document())]);

        var restored = SessionHistory.Restore(SessionHistory.Store(message))!;
        var again = SessionHistory.Restore(SessionHistory.Store(restored))!;
        var legacy = new ChatMessage(ChatRole.User, [new TextContent("summarize [PDF #1, from " + ReportPath + "]"), new TextContent(ConversationHistory.DocumentText(ConversationHistory.ImagePart(Document()))!)]);

        foreach (var m in new[] { restored, again, legacy })
        {
            Assert.Equal("summarize [PDF #1, from " + ReportPath + "]", App.ConversationRewind.UserLine(m));
            Assert.Equal([ReportPath], App.ConversationRewind.DocumentPaths(m));
            Assert.Contains("--- page 1 ---\nHello", m.Text, StringComparison.Ordinal);   // still what the model reads
        }

        Assert.Null(PdfText.AttachedFrom("(the PDF a thing I typed)"));
        Assert.Equal("r.pdf", PdfText.AttachedFrom(PdfText.AttachedText("r.pdf", "r.pdf", new PdfDocumentText("", 0, 0, Problem: "no browser"))));
    }

    [Fact]
    public void ThePictureBudget_WeighsAPdfByItsTextWhereThatIsSent_AndLeavesItsTextWhenTakenOut()
    {
        var big = Document(text: new string('x', 1000)) with { Bytes = new byte[300_000] };
        var part = ConversationHistory.ImagePart(big);
        Assert.Equal(400_000, PictureBudget.WireBytes(part));
        Assert.True(PictureBudget.WireBytes(part, documentsAsText: true) < 2_000);
        Assert.StartsWith("(an attached PDF taken out to keep the request small; its text was:)\n(the PDF report.pdf", PictureBudget.LeftOutFor(part), StringComparison.Ordinal);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent("one"), part]),
            new(ChatRole.Assistant, "ok"),
            new(ChatRole.User, [new TextContent("two"), ConversationHistory.ImagePart(new ImageAttachment("p.png", [1], "image/png", 1, 1))]),
        };
        var trim = PictureBudget.TakeOut(messages, keepPictures: 0, keepBytes: 0);
        Assert.Equal(1, trim.Pictures);
        Assert.Equal(1, trim.Documents);
        Assert.Equal("🖼️ 1 older PDF taken out (0.4 MB) to keep the request small", PictureBudget.TakenOutNotice(trim.Pictures, trim.Bytes, trim.Documents));
        Assert.Equal("🖼️ 2 older pictures and 1 PDF taken out (1 MB) to keep the request small", PictureBudget.TakenOutNotice(3, 1_000_000, 1));
    }

    [Fact]
    public void TheEstimate_CountsAPdfsText_NotOnePicture()
    {
        var message = new ChatMessage(ChatRole.User, [ConversationHistory.ImagePart(Document(text: new string('x', 40_000)))]);
        Assert.True(ConversationCompactor.EstimateTokens([message]) >= 10_000);
    }
}
