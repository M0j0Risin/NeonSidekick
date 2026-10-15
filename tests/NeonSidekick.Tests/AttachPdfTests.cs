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

    private static ImageAttachment Document(string path = @"C:\docs\report.pdf", string text = "--- page 1 ---\nHello", int pages = 1) =>
        new(path, Bytes, PdfFile.MediaType, 0, 0) { DocumentText = Task.FromResult(new PdfDocumentText(text, pages, pages)) };

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
        Assert.Equal(@"C:\docs\report.pdf", ConversationHistory.PicturePath(part));
        Assert.Equal("(the PDF report.pdf, attached from C:\\docs\\report.pdf: its text, 1 page)\n--- page 1 ---\nHello", ConversationHistory.DocumentText(part));
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
