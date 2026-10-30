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
    private static SessionRecord PicturedRecord(string title = "Vosk wiring", bool stamp = true)
    {
        byte[] png = ImageFileTests.BlackPng(4, 4);
        var history = new ConversationHistory("");
        history.AddUser("look [Image #1, saved as C:\\Users\\me\\pasted-1.png]", [new ImageAttachment("C:\\Users\\me\\pasted-1.png", png, ImageFile.Png, 4, 4)]);
        history.AddAssistant("A pony.");
        if (stamp)
        {
            ConversationHistory.SetTurnOrdinal(history.LastTurnStart()!, 1);
        }

        history.AddUser("draw a dog");
        history.AddToolImages([new ImageAttachment("comfy\\dog.png", png, ImageFile.Png, 4, 4)], "view_image");
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
        Assert.EndsWith(ChatLog.Quote("go on") + "\n\n🛠️ " + SessionText.ToolCallsNote(3) + ": `read_file`, `web_search`\n\n_(cancelled)_\n", doc.Text);
    }

    [Fact]
    public void Markdown_WithoutAModel_LeavesTheProperty_AndTheFactOut()
    {
        var doc = SessionExport.Build(new SessionRecord(Summary(model: ""), [Turn(1, "hi", "hello")], ""), SessionExportFormat.Markdown, Zone, Theme.Current, "pics");

        Assert.DoesNotContain("model:", doc.Text);
        Assert.Contains("\n\\#12 · started 2026-09-11 14:05 · last 2026-09-11 14:35 · 2 turns\n", doc.Text);
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
            (path, _) => new WriteResult(FileOutcome.Failed, path, 0, false, "denied"));

        Assert.Null(path);
        Assert.Equal(FileText.Error(FileOutcome.Failed, "Vosk-wiring-2026-09-11-1405.md", "write", "denied"), error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }
}
