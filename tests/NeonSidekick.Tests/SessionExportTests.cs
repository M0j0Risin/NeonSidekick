using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Pdf;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>A stored session written out (2026-10-30, plan i-want-to-plan-effervescent-adleman): the builder, then the writer into a temp working folder.</summary>
public sealed class SessionExportTests : IDisposable
{
    private static readonly TimeZoneInfo Zone = ManualTimeProvider.DefaultZone;
    private static readonly DateTimeOffset At = ManualTimeProvider.DefaultUtcNow;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public SessionExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static SessionSummary Summary(string title = "Vosk wiring", string model = "qwen3_30b", int turns = 2) =>
        new(12, At, At.AddMinutes(30), title, TitleSource.FirstLine, model, turns);

    private static SessionTurn Turn(int ordinal, string user, string reply, int toolCalls = 0, IReadOnlyList<string>? names = null, bool cancelled = false) =>
        new(ordinal, At.AddMinutes(ordinal), user, reply, toolCalls, names ?? [], [], 0, 1, 1, cancelled);

    /// <summary>Two turns, the first with a picture of the user's (stamped with its row), the second with a tool's; the history as a run leaves it.</summary>
    private static SessionRecord PicturedRecord(string title = "Vosk wiring", bool stamp = true, int big = 0, string path = "C:\\Users\\me\\pasted-1.png")
    {
        byte[] png = ImageFileTests.BlackPng(4, 4);
        var history = new ConversationHistory("");
        history.AddUser("look [Image #1, saved as " + path + "]", [new ImageAttachment(path, png, ImageFile.Png, 4, 4)]);
        history.AddAssistant("A pony.");
        if (stamp)
        {
            ConversationHistory.SetTurnOrdinal(history.LastTurnStart()!, 1);
        }

        history.AddUser("draw a dog");
        // A big picture is a PNG's head and then noise: past a base64 slice (review F84).
        byte[] tool = big > 0 ? [.. png, .. Enumerable.Range(0, big).Select(i => (byte)(i * 7))] : png;
        history.AddToolImages([new ImageAttachment("comfy\\dog.png", tool, ImageFile.Png, 4, 4)], "view_image");
        history.AddAssistant("Here is a dog.");
        if (stamp)
        {
            ConversationHistory.SetTurnOrdinal(history.LastTurnStart()!, 2);
        }

        return new SessionRecord(Summary(title),
            [Turn(1, "look [Image #1, saved as C:\\Users\\me\\pasted-1.png]", "A pony."), Turn(2, "draw a dog", "Here is a dog.", 2, ["view_image", "view_image"])],
            SessionHistory.ToJson(history.Messages));
    }

    [Fact]
    public void Markdown_HasObsidianProperties_ThenTheTitle_TheFacts_AndATurnASection()
    {
        var record = new SessionRecord(Summary("Fix the *bug*: \"x\" #1"),
            [Turn(1, "hello\nthere", "**Bold** [link](https://example.com)"), Turn(2, "go on", "", 3, ["read_file", "read_file", "web_search"], cancelled: true)], "");

        var doc = SessionExport.Build(record, SessionExportFormat.Markdown, Zone, Theme.Current, "pics");

        Assert.Empty(doc.Pictures);
        Assert.StartsWith(
            "---\ntitle: \"Fix the *bug*: \\\"x\\\" #1\"\nsession: 12\nstarted: 2026-09-11T14:05:30-07:00\nupdated: 2026-09-11T14:35:30-07:00\nturns: 2\nmodel: qwen3_30b\ntags: [neon-session]\n---\n\n"
            + "# Fix the \\*bug\\*: \"x\" \\#1\n\n\\#12 · started 2026-09-11 14:05 · last 2026-09-11 14:35 · 2 turns · qwen3\\_30b\n\n## Turn 1 · 2026-09-11 14:06\n\n",
            doc.Text);
        Assert.Contains(ChatLog.Quote("hello\nthere") + "\n\n**Bold** [link](https://example.com)\n\n---\n\n## Turn 2 · 2026-09-11 14:07\n\n", doc.Text);   // the reply's own Markdown as it is
        Assert.EndsWith(ChatLog.Quote("go on") + "\n\n🛠️ " + SessionText.ToolCallsNote(3) + ": `read_file`, `web_search`\n\n_(canceled)_\n", doc.Text);
    }

    [Fact]
    public void Markdown_WithoutAModel_LeavesTheProperty_AndTheFactOut()
    {
        var doc = SessionExport.Build(new SessionRecord(Summary(model: ""), [Turn(1, "hi", "hello")], ""), SessionExportFormat.Markdown, Zone, Theme.Current, "pics");

        Assert.DoesNotContain("model:", doc.Text);
        // The stored count says 2, the record holds 1 row: the export counts what it writes, in the properties and the facts (2026-10-30, review F9).
        Assert.Contains("\nturns: 1\n", doc.Text);
        Assert.Contains("\n\\#12 · started 2026-09-11 14:05 · last 2026-09-11 14:35 · 1 turn\n", doc.Text);
    }

    [Fact]
    public void Markdown_PicturesAreNamedByTurn_LinkedInAngleBrackets_AndTheSavedPathIsGone()
    {
        var doc = SessionExport.Build(PicturedRecord(), SessionExportFormat.Markdown, Zone, Theme.Current, "Vosk wiring-pictures");

        Assert.Equal(["turn-1-1.png", "turn-2-1.png"], doc.Pictures.Select(p => p.Name));
        Assert.Equal(ImageFileTests.BlackPng(4, 4), doc.Pictures[0].Bytes);
        Assert.Contains(ChatLog.Quote("look [Image #1]") + "\n\n![](<Vosk wiring-pictures/turn-1-1.png>)\n", doc.Text);
        Assert.Contains(": `view_image`\n\n![](<Vosk wiring-pictures/turn-2-1.png>)\n\nHere is a dog.\n", doc.Text);   // a tool's picture after the tool line
        Assert.DoesNotContain("C:\\Users", doc.Text);
    }

    [Fact]
    public void ATurnNeverStamped_ExportsWithoutItsPictures_AndADamagedHistoryWithoutAny()
    {
        Assert.Empty(SessionExport.Build(PicturedRecord(stamp: false), SessionExportFormat.Markdown, Zone, Theme.Current, "p").Pictures);

        var damaged = PicturedRecord() with { HistoryJson = "{not json" };
        var doc = SessionExport.Build(damaged, SessionExportFormat.Markdown, Zone, Theme.Current, "p");

        Assert.Empty(doc.Pictures);
        Assert.Contains("Here is a dog.", doc.Text);   // the text, from the rows, whole
    }

    [Fact]
    public void Html_OpensWithTheCharset_ThenThePolicy_EscapesTheUsersText_AndEmbedsThePictures()
    {
        var record = PicturedRecord("<b>Tags</b> & more");
        record = record with { Turns = [record.Turns[0] with { UserText = "<script>alert(1)</script> look [Image #1, saved as C:\\Users\\me\\pasted-1.png]" }, record.Turns[1]] };

        string html = SessionExport.Build(record, SessionExportFormat.Html, Zone, Theme.Current, "").Text;

        Assert.StartsWith("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n" + PdfHtml.PolicyMeta + "\n", html);
        Assert.Contains("<title>&lt;b&gt;Tags&lt;/b&gt; &amp; more</title>", html);
        Assert.Contains("<div class=\"user\">&lt;script&gt;alert(1)&lt;/script&gt; look [Image #1]</div>", html);
        Assert.DoesNotContain("<script", html);
        Assert.Equal(2, html.Split("<img src=\"data:image/png;base64,").Length - 1);
        Assert.Contains("alt=\"pasted-1.png\"", html);   // the file's name, never its folder
        Assert.DoesNotContain("C:\\Users", html);
        Assert.Contains("<p class=\"tools\">" + System.Net.WebUtility.HtmlEncode("🛠️ " + SessionText.ToolCallsNote(2) + ": view_image") + "</p>", html);
    }

    [Fact]
    public void Html_RendersTheReplysMarkdown_AndDropsAScriptLink()
    {
        var record = new SessionRecord(Summary(), [Turn(1, "hi", "**Bold** [bad](javascript:alert(1)) [good](https://example.com)")], "");

        string html = SessionExport.Build(record, SessionExportFormat.Html, Zone, Theme.Current, "").Text;

        Assert.Contains("<strong>Bold</strong>", html);
        Assert.Contains("<a href=\"https://example.com/\" target=\"_blank\" rel=\"noopener noreferrer\">good</a>", html);
        Assert.Contains("<strong>Bold</strong> bad <a", html);   // the script link kept as its text alone
        Assert.DoesNotContain("javascript:", html);
    }

    [Fact]
    public void TheSheet_TakesTheThemesGround_AndCodeColors_OrTheDefaultWhenItsInkWontRead()
    {
        var palette = Theme.Current;
        string sheet = SessionExport.ThemeSheet(palette);

        Assert.Contains("--bg: " + WebTheme.Hex(palette.Bg) + ";", sheet);
        Assert.Contains("--ink: " + WebTheme.Hex(palette.Ink) + ";", sheet);
        Assert.Contains("color-scheme: dark;", sheet);   // synthwave's ground is dark
        Assert.Matches(@"\.tk-\w+ \{ color: #[0-9a-f]{6};", sheet);

        var unreadable = palette with { Name = "murk", Ink = palette.Bg, Dim = palette.Bg };
        string fallback = SessionExport.ThemeSheet(unreadable);

        Assert.False(WebTheme.Readable(unreadable));
        Assert.Equal(SessionExport.ThemeSheet(ThemeLibrary.Default), fallback);
    }

    [Fact]
    public void MarkdownText_EscapesWhatWouldBeMarkup()
    {
        Assert.Equal("a\\*b\\_c \\[d\\] \\#e \\|f \\`g\\` \\<h\\> \\\\i", SessionExport.MarkdownText("a*b_c [d] #e |f `g` <h> \\i"));
        Assert.Equal("plain words", SessionExport.MarkdownText("plain words"));
        // Obsidian's own marks (2026-10-30, review F50): math, highlight, strike, entities and comments show as typed.
        Assert.Equal("Cost \\$5 vs \\$10", SessionExport.MarkdownText("Cost $5 vs $10"));
        Assert.Equal("R\\&amp;D \\=\\=hi\\=\\= \\~\\~x\\~\\~ \\%\\%c\\%\\%", SessionExport.MarkdownText("R&amp;D ==hi== ~~x~~ %%c%%"));
    }

    [Fact]
    public void TheStem_IsTheTitleAndTheStart_WithLinkSyntaxTakenOut()
    {
        Assert.Equal("Vosk-wiring-2026-09-11-1405", SessionExport.Stem(Summary(), Zone));
        Assert.Equal("C-notes-(draft)-2026-09-11-1405", SessionExport.Stem(Summary("C# notes (draft)"), Zone));
        Assert.Equal("session-2026-09-11-1405", SessionExport.Stem(Summary("#[]|"), Zone));
    }

    // ── The writer ───────────────────────────────────────────────────────────

    private WorkingDirectory Files() => new(() => _dir, TimeProvider.System);

    [Fact]
    public void Write_Markdown_PutsThePicturesBesideIt_AndASecondExportTakesDash2()
    {
        var files = Files();
        var record = PicturedRecord();

        var first = SessionExport.Write(files, record, SessionExportFormat.Markdown, Zone, Theme.Current);
        var second = SessionExport.Write(files, record, SessionExportFormat.Markdown, Zone, Theme.Current);

        Assert.Equal(("Vosk-wiring-2026-09-11-1405.md", (string?)null), first);
        Assert.Equal(("Vosk-wiring-2026-09-11-1405-2.md", (string?)null), second);
        Assert.True(File.Exists(Path.Combine(_dir, "Vosk-wiring-2026-09-11-1405-pictures", "turn-1-1.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "Vosk-wiring-2026-09-11-1405-2-pictures", "turn-2-1.png")));
        string text = File.ReadAllText(Path.Combine(_dir, "Vosk-wiring-2026-09-11-1405-2.md"));
        Assert.Contains("![](<Vosk-wiring-2026-09-11-1405-2-pictures/turn-1-1.png>)", text);
        Assert.False(File.ReadAllBytes(Path.Combine(_dir, first.Path!)).AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));   // no BOM
    }

    [Fact]
    public void Write_Html_IsOneFile()
    {
        var (path, error) = SessionExport.Write(Files(), PicturedRecord(), SessionExportFormat.Html, Zone, Theme.Current);

        Assert.Null(error);
        Assert.Equal("Vosk-wiring-2026-09-11-1405.html", path);
        Assert.Equal(["Vosk-wiring-2026-09-11-1405.html"], Directory.EnumerateFileSystemEntries(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Write_ADocumentThatFails_TakesItsPicturesBackOut()
    {
        var (path, error) = SessionExport.Write(Files(), PicturedRecord(), SessionExportFormat.Markdown, Zone, Theme.Current,
            pending => new WriteResult(FileOutcome.Failed, pending.Relative, 0, false, "denied"));

        Assert.Null(path);
        Assert.Equal(FileText.Error(FileOutcome.Failed, "Vosk-wiring-2026-09-11-1405.md", "write", "denied"), error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));

        // A file that appeared at the name meanwhile fails it too, and its pictures go (2026-10-30, review F84).
        (path, error) = SessionExport.Write(Files(), PicturedRecord(), SessionExportFormat.Markdown, Zone, Theme.Current, pending =>
        {
            pending.Dispose();
            return new WriteResult(FileOutcome.Exists, pending.Relative, 0, false);
        });

        Assert.Null(path);
        Assert.Equal(FileText.Error(FileOutcome.Exists, "Vosk-wiring-2026-09-11-1405.md", "write", ""), error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }

    /// <summary>
    /// The export streams (2026-10-30, review F84): the file written is the built text, byte for byte, and no single write is bigger than one
    /// base64 slice, however big a picture is.
    /// </summary>
    [Theory]
    [InlineData(SessionExportFormat.Markdown)]
    [InlineData(SessionExportFormat.Html)]
    public void Write_Streams_TheBuiltText_InWritesNoBiggerThanASlice(SessionExportFormat format)
    {
        var record = PicturedRecord(big: 300 * 1024);
        var counting = new List<CountingWriter>();

        var (path, error) = SessionExport.Write(Files(), record, format, Zone, Theme.Current, null, stream =>
        {
            var writer = new CountingWriter(new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true));
            counting.Add(writer);
            return writer;
        });

        Assert.Null(error);
        string folder = format == SessionExportFormat.Markdown ? "Vosk-wiring-2026-09-11-1405-pictures" : "";
        string written = File.ReadAllText(Path.Combine(_dir, path!));
        Assert.Equal(SessionExport.Build(record, format, Zone, Theme.Current, folder).Text, written);
        Assert.InRange(Assert.Single(counting).Largest, 1, SessionExport.Base64Slice / 3 * 4);
        if (format == SessionExportFormat.Html)
        {
            Assert.Contains(Convert.ToBase64String(ImageFileTests.BlackPng(4, 4)), written);
        }
    }

    /// <summary>A writer that counts the largest single write it is handed.</summary>
    private sealed class CountingWriter(TextWriter inner) : TextWriter
    {
        public int Largest { get; private set; }

        public override System.Text.Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            Largest = Math.Max(Largest, 1);
            inner.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            Largest = Math.Max(Largest, count);
            inner.Write(buffer, index, count);
        }

        public override void Write(string? value)
        {
            Largest = Math.Max(Largest, value?.Length ?? 0);
            inner.Write(value);
        }

        public override void Flush() => inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The note and its pictures folder share one free stem (2026-10-30, review F7): a dotted title stays whole, and a taken folder alone moves both.
    /// </summary>
    [Fact]
    public void Write_ADottedTitle_TakesTheNextStem_ForTheNoteAndItsFolderTogether()
    {
        var record = PicturedRecord("x v1.2");
        string stem = SessionExport.Stem(record.Summary, Zone);
        Assert.Contains("v1.2", stem, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(_dir, stem + ".md"), "taken");
        Assert.Equal((stem + "-2.md", (string?)null), SessionExport.Write(Files(), record, SessionExportFormat.Markdown, Zone, Theme.Current));
        Assert.True(File.Exists(Path.Combine(_dir, stem + "-2-pictures", "turn-1-1.png")));

        string other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(Path.Combine(other, stem + "-pictures"));
        var files = new WorkingDirectory(() => other, TimeProvider.System);
        Assert.Equal((stem + "-2.md", (string?)null), SessionExport.Write(files, record, SessionExportFormat.Markdown, Zone, Theme.Current));
        Assert.True(File.Exists(Path.Combine(other, stem + "-2-pictures", "turn-1-1.png")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(other, stem + "-pictures")));
    }

    /// <summary>A failed export in a folder only the app may write (the profile's own files) still takes its pictures back (2026-10-30, review F6).</summary>
    [Fact]
    public void Write_AFailedExportUnderTheHome_TakesItsPicturesBack()
    {
        string home = Path.Combine(_dir, "home");
        string root = Path.Combine(home, "profiles", "neon", "files");
        Directory.CreateDirectory(root);
        using var guard = WriteGuard.Configure(new WriteGuardFacts(() => home, () => null, () => [], () => null, () => false, () => null));
        var files = new WorkingDirectory(() => root, TimeProvider.System);

        var (path, _) = SessionExport.Write(files, PicturedRecord(), SessionExportFormat.Markdown, Zone, Theme.Current,
            pending => new WriteResult(FileOutcome.Failed, pending.Relative, 0, false, "denied"));

        Assert.Null(path);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    /// <summary>
    /// A stored media type the page doesn't know never reaches the <c>src</c> (2026-10-30, review F48): an unknown one is sniffed from the bytes,
    /// and one carrying a quote can't open an attribute.
    /// </summary>
    [Theory]
    [InlineData("image/x-weird")]
    [InlineData("image/png\\\" onload=\\\"alert(1)")]
    public void Html_AStoredTypeItDoesntKnow_IsSniffed_AndNeverEndsTheAttribute(string stored)
    {
        var record = PicturedRecord();
        record = record with { HistoryJson = record.HistoryJson.Replace("image/png", stored, StringComparison.Ordinal) };

        string html = SessionExport.Build(record, SessionExportFormat.Html, Zone, Theme.Current, "").Text;

        Assert.DoesNotContain("onload", html);
        Assert.DoesNotContain("x-weird", html);
        Assert.All(html.Split("<img src=\"")[1..], img => Assert.StartsWith("data:image/png;base64,", img, StringComparison.Ordinal));
    }

    /// <summary>A Windows path's folders are gone on either OS, and a Mac's too (2026-10-30, review F39).</summary>
    [Fact]
    public void Html_TheAltText_IsTheFileName_FromEitherSeparator()
    {
        var record = PicturedRecord(path: "/Users/me/pasted-1.png");
        record = record with { Turns = [record.Turns[0] with { UserText = "look [Image #1, saved as /Users/me/pasted-1.png] [PDF #1, from C:\\docs\\a.pdf]" }, record.Turns[1]] };

        string html = SessionExport.Build(record, SessionExportFormat.Html, Zone, Theme.Current, "").Text;

        Assert.Contains("alt=\"pasted-1.png\"", html);
        Assert.DoesNotContain("/Users/me", html);
        Assert.DoesNotContain("C:\\docs", html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(PdfText.DocumentLabel(1, "a.pdf")), html);
    }

    /// <summary>A theme's name can't close the page's style element (2026-10-30, review F40).</summary>
    [Fact]
    public void Html_AThemeNamedToCloseTheStyle_KeepsTheWholeSheet()
    {
        var palette = Theme.Current with { Name = "x</style><h1>y" };

        string html = SessionExport.Build(new SessionRecord(Summary(), [Turn(1, "hi", "hello")], ""), SessionExportFormat.Html, Zone, palette, "").Text;

        Assert.Single(html.Split("</style>")[1..]);
        Assert.DoesNotContain("<h1>y", html);
    }

    /// <summary>A tool name with a backtick and a link stays one code span (2026-10-30, review F89).</summary>
    [Fact]
    public void Markdown_AToolNameWithABacktick_StaysOneCodeSpan()
    {
        Assert.Equal("``a`b](http://x)``", SessionExport.CodeSpan("a`b](http://x)"));
        Assert.Equal("`` `x ``", SessionExport.CodeSpan("`x"));
        Assert.Equal("`read_file`", SessionExport.CodeSpan("read_file"));
        Assert.Equal("`a b`", SessionExport.CodeSpan("a\nb"));

        var doc = SessionExport.Build(new SessionRecord(Summary(), [Turn(1, "go", "done", 1, ["mcp__x`y"])], ""), SessionExportFormat.Markdown, Zone, Theme.Current, "p");
        Assert.Contains(": ``mcp__x`y``\n", doc.Text);
    }
}
