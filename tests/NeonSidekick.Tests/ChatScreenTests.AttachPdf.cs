using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Pdf;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// PDFs attached to a message on the screen (2026-10-14, the PDF plan's phase 3): a dropped PDF's path becomes a token, its text read in the
/// background through the fake browser; the message carries the PDF as a native part with its text and its file's name, the sent text
/// naming its path; a PDF with no text is refused on a server that takes text, the draft kept; on a /command line a dropped PDF stays its path.
/// </summary>
public partial class ChatScreenTests
{
    private string Pdf(string name)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("%PDF-1.7\n% attached in a test\n%%EOF\n"));
        return path;
    }

    private void BrowserReads(params string[] pages)
    {
        _browser.Executable = OperatingSystem.IsWindows() ? @"C:\Edge\msedge.exe" : "/Applications/Edge";
        for (int i = 0; i < pages.Length; i++)
        {
            _browser.PdfReadPages.Add(new PdfPageRead(i + 1, pages[i], null, 0, 0));
        }
    }

    [Fact]
    public async Task ADroppedPdf_IsAttached_AsANativePart_WithItsText_AndItsPathNamed()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("It is a manual.");
        BrowserReads("The warranty lasts two years.", "Page two.");
        string file = Pdf("manual.pdf");
        var input = new ScriptedInput();
        input.Push(Keys.Char('s'), Keys.Char('e'), Keys.Char('e'), Keys.Char(' ')).PushPaste(file).Push(Keys.Enter);
        PushLine(input, "/exit");

        await RunAsync(input);

        var sent = _chat.Requests[0].Last(m => m.Role == ChatRole.User);
        Assert.Equal("see " + PdfText.DocumentSentLabel(1, file), sent.Text);
        var part = Assert.Single(sent.Contents.OfType<DataContent>());
        Assert.Equal("application/pdf", part.MediaType);
        Assert.Equal("manual.pdf", part.Name);
        string text = ConversationHistory.DocumentText(part)!;
        Assert.StartsWith("(the PDF manual.pdf, attached from " + file + ": its text, 2 pages)", text, StringComparison.Ordinal);
        Assert.Contains("--- page 1 ---\nThe warranty lasts two years.", text, StringComparison.Ordinal);
        Assert.Single(_browser.PdfReads);
    }

    [Fact]
    public async Task APdfWithNoText_IsRefused_OnAServerThatTakesText_TheDraftKept()
    {
        _settings.Update(d => d.TtsOutput = false);
        BrowserReads("", "");
        string file = Pdf("scan.pdf");
        // The refusal puts the draft back on the line: ESC clears it before the exit.
        StepsWhenIdle(input => input.PushPaste(file), Key(Keys.Enter), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("  ✗ " + PdfText.NoTextForThisServer("scan.pdf"), output);
        Assert.Contains(PdfText.DocumentLabel(1, "scan.pdf"), output);   // the draft back, its token drawn
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task ALockedPdf_IsRefused_BeforeAnythingIsSent()
    {
        _settings.Update(d => d.TtsOutput = false);
        BrowserReads("text");
        _browser.PdfLocked = true;
        string file = Pdf("secret.pdf");
        StepsWhenIdle(input => input.PushPaste(file), Key(Keys.Enter), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("  ✗ " + PdfText.AttachedLocked("secret.pdf"), output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>The PDF plan's gap 42: a /command keeps no attachment, so a PDF dropped on its line stays its path, as it always did.</summary>
    [Fact]
    public async Task APdfDroppedOnACommandLine_StaysItsPath()
    {
        _settings.Update(d => d.TtsOutput = false);
        BrowserReads("text");
        string file = Pdf("notes.pdf");
        var input = new ScriptedInput();
        input.Push(Keys.Char('/'), Keys.Char('e'), Keys.Char('c'), Keys.Char('h'), Keys.Char('o'), Keys.Char(' ')).PushPaste(file).Push(Keys.Enter);
        PushLine(input, "/exit");

        string output = await RunAsync(input);

        Assert.Contains(file, output);
        Assert.DoesNotContain("[PDF #", output);
        Assert.Empty(_browser.PdfReads);
    }

    [Fact]
    public void TheIgnoredNotice_CountsPicturesAndPdfsApart()
    {
        var picture = new Files.ImageAttachment("a.png", [1], "image/png", 1, 1);
        var document = new Files.ImageAttachment("a.pdf", [1], "application/pdf", 0, 0);
        Assert.Equal("(the image was ignored: a /command takes none)", ChatScreen.ImagesIgnoredNotice([picture]));
        Assert.Equal("(the PDF was ignored: a /command takes none)", ChatScreen.ImagesIgnoredNotice([document]));
        Assert.Equal("(1 image and 1 PDF were ignored: a /command takes none)", ChatScreen.ImagesIgnoredNotice([picture, document]));
        Assert.Equal("(2 PDFs were ignored: a /command takes none)", ChatScreen.ImagesIgnoredNotice([document, document]));
    }
}
