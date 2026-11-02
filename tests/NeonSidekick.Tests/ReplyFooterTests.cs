using NeonSidekick.App;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The reply footer (2026-11-01, feature idea #4): its words (<see cref="UsageText.Footer"/>, <see cref="UsageText.ToolTime"/>) and where the
/// renderer writes them — the footer under the reply's last line ahead of its blank one, never on the page; a slow tool's time after its cut
/// result, on that line alone (an argument of the writer since 2026-11-02, the review: no slot a later line could take).
/// </summary>
public class ReplyFooterTests : IDisposable
{
    private readonly TestConsole _console = new();

    public ReplyFooterTests() => _console.Profile.Width = 80;

    public void Dispose() => _console.Dispose();

    [Fact]
    public void Footer_TheParts_MostUsefulFirst_TheOnesWithNoDataLeftOut()
    {
        Assert.Equal("12.4s · 812 tok · 48 tok/s · 3 tool calls · qwen3-32b", UsageText.Footer(new FooterFacts(TimeSpan.FromSeconds(12.4), 812, 48.3, 3, "Qwen/qwen3-32b")));
        Assert.Equal("1m 05s · 1 tool call", UsageText.Footer(new FooterFacts(TimeSpan.FromSeconds(65), 0, null, 1, null)));   // no usage report, no model
        Assert.Equal("0.4s · 12.5k tok · llama", UsageText.Footer(new FooterFacts(TimeSpan.FromSeconds(0.4), 12_500, null, 0, "llama")));   // untimed: no speed
        Assert.Equal("0.0s", UsageText.Footer(new FooterFacts(TimeSpan.FromSeconds(-1), 0, null, 0, "")));
    }

    [Fact]
    public void ToolTime_FromTwoSeconds_AsTheThinkingFoldWritesIt()
    {
        Assert.Null(UsageText.ToolTime(TimeSpan.FromSeconds(1.99)));
        Assert.Equal(" · 2.0s", UsageText.ToolTime(TimeSpan.FromSeconds(2)));
        Assert.Equal(" · 2m 03s", UsageText.ToolTime(TimeSpan.FromSeconds(123)));
        Assert.Equal(TimeSpan.FromSeconds(2), UsageText.SlowTool);
    }

    [Fact]
    public void EndAssistant_TheFooter_UnderTheReply_AheadOfTheBlankLine_AndNeverOnThePage()
    {
        var feed = new WebUi.TranscriptFeed();
        var t = new TranscriptRenderer(_console) { Feed = feed };

        t.BeginAssistant();
        t.AppendDelta("Hello.");
        t.EndAssistant("1.0s · llama");

        Assert.EndsWith("● Hello.\n1.0s · llama\n\n", _console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(feed.Snapshot(s => s), e => e.Text.Contains("llama", StringComparison.Ordinal));
    }

    [Fact]
    public void EndAssistant_AnEmptyReply_StillSaysNoReply_TheFooterUnderIt()
    {
        var t = new TranscriptRenderer(_console);

        t.BeginAssistant();
        t.EndAssistant("1.0s");

        Assert.EndsWith("● " + TranscriptRenderer.NoReplyText + "\n1.0s\n\n", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void EndAssistant_WithoutAFooter_IsAsBefore()
    {
        var t = new TranscriptRenderer(_console);

        t.BeginAssistant();
        t.AppendDelta("Hello.");
        t.EndAssistant();

        Assert.EndsWith("● Hello.\n\n", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolTime_GoesOnTheLineOfTheWriterGivenIt_AfterItsCutText_Once()
    {
        var t = new TranscriptRenderer(_console);
        t.BeginAssistant();

        t.ToolResult("read_file", new string('a', 300), " · 2.5s");   // cut at the limit: the time after the ellipsis, never cut itself
        t.ToolResult("read_file", "next");
        t.ToolNote("remembered: a fact", " · 3.0s");
        t.SkillNote("loaded skill 'haiku'", " · 4.0s");
        t.ToolNote("a note given none");
        t.EndAssistant();

        string output = _console.Output;
        Assert.Contains("… · 2.5s", output, StringComparison.Ordinal);
        Assert.Equal(1, Count(output, " · 2.5s"));
        Assert.Contains("remembered: a fact · 3.0s", output, StringComparison.Ordinal);
        Assert.Contains("loaded skill 'haiku' · 4.0s", output, StringComparison.Ordinal);
        Assert.DoesNotContain("next ·", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a note given none ·", output, StringComparison.Ordinal);   // a writer with no time to pass shows none
    }

    [Fact]
    public void ToolDiff_TheTime_GoesOnItsNoteLine()
    {
        var t = new TranscriptRenderer(_console);
        t.BeginAssistant();

        var diff = Files.FileDiff.Of("a.txt", "one\n", "one\ntwo\n")!;
        t.ToolDiff("edited a.txt", diff, 20, 0, " · 2.5s");
        t.EndAssistant();

        Assert.Contains("edited a.txt · 2.5s", _console.Output, StringComparison.Ordinal);
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
