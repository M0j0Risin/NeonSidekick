using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// Message jumps and new rows while scrolled up (2026-11-01, feature idea #3): the store keeps each message's first line, a jump moves the
/// view's top to the previous or next one (the bottom past the last window), the scroll row counts what was written since the view left the
/// bottom and names the message on top; the renderer marks a sent line, a reply's first committed row, and a speaker's line once.
/// </summary>
public partial class ScreenPaneTests
{
    /// <summary>Twelve lines, messages starting on L02, L06 and L10 (store rows 1, 5 and 9); the scrolled region is five rows (the bottom window's top is row 7).</summary>
    private ScreenPane Messages()
    {
        var pane = Pane();
        pane.Open();
        pane.Show();
        for (int i = 1; i <= 12; i++)
        {
            if (i is 2 or 6 or 10)
            {
                pane.MarkMessageStart();
            }

            pane.Write(new Markup(Line(i) + Environment.NewLine));
        }

        return pane;
    }

    [Fact]
    public void JumpMessage_UpAndDown_ThroughTheStarts_ThePastTheLastWindowIsTheBottom()
    {
        using var pane = Messages();
        Assert.Equal([1, 5, 9], pane.MessageStartRows());

        pane.JumpMessage(-1);   // from the bottom window's top (row 7): the start above it
        Assert.Equal(5, pane.ScrollTop);
        Assert.Contains("message 2 of 3", Output, StringComparison.Ordinal);
        pane.JumpMessage(-1);
        Assert.Equal(1, pane.ScrollTop);
        pane.JumpMessage(-1);   // none above: nothing moves
        Assert.Equal(1, pane.ScrollTop);

        pane.JumpMessage(1);
        Assert.Equal(5, pane.ScrollTop);
        pane.JumpMessage(1);    // row 9 is inside the last window: the bottom
        Assert.False(pane.Scrolled);
        pane.JumpMessage(1);    // at the bottom, nothing below
        Assert.False(pane.Scrolled);
    }

    [Fact]
    public void JumpMessage_AboveTheFirstMessage_TheScrollRowNamesNone()
    {
        using var pane = Messages();
        pane.ScrollToTop();
        Assert.Equal(0, pane.ScrollTop);
        Assert.DoesNotContain("message ", Output, StringComparison.Ordinal);   // the top row is above the first start
        pane.JumpMessage(1);
        Assert.Equal(1, pane.ScrollTop);
        Assert.Contains("message 1 of 3", Output, StringComparison.Ordinal);
    }

    [Fact]
    public void JumpMessage_AcrossAResize_FollowsTheRowsThatMoved()
    {
        using var pane = Pane();
        pane.Open();
        pane.Show();
        for (int i = 1; i <= 12; i++)
        {
            pane.MarkMessageStart();
            pane.Write(new Markup((i == 6 ? new string('w', 50) + " " : "") + Line(i) + Environment.NewLine));   // L06 wraps at 40 columns
        }

        var before = pane.MessageStartRows();
        Assert.Equal(12, before.Count);
        Assert.Equal(before[5] + 2, before[6]);   // the wrapped line takes two rows
        _console.Profile.Width = 80;
        var after = pane.MessageStartRows();
        Assert.Equal(12, after.Count);
        Assert.Equal(after[5] + 1, after[6]);     // one at 80
    }

    [Fact]
    public void ANoMarkWrite_IsNoStart_AndABlankLineNeverTakesTheMark()
    {
        using var pane = Pane();
        pane.Open();
        pane.Show();
        pane.Write(new Markup("banner" + Environment.NewLine));
        pane.MarkMessageStart();
        pane.Write(new Markup(Environment.NewLine));                 // a blank line ahead of the message
        pane.Write(new Markup("› hi" + Environment.NewLine));
        pane.Write(new Markup("a notice" + Environment.NewLine));

        Assert.Equal([2], pane.MessageStartRows());
    }

    [Fact]
    public void NewRows_WhileScrolled_AreCounted_StartOverWhenTheViewLeavesTheBottomAgain_AndGoWithAClear()
    {
        using var pane = Scrollable();
        pane.ScrollPage(-1);
        int mark = Output.Length;
        pane.Write(new Markup("L13" + Environment.NewLine + "L14" + Environment.NewLine));
        Assert.Contains("↓ 2 new rows", Output[mark..], StringComparison.Ordinal);

        pane.SetLive(new Markup("live"));
        _time.Advance(ScreenPane.Tick);
        Assert.Contains("↓ 3 new rows", Output[mark..], StringComparison.Ordinal);   // the live block's row too
        pane.CommitLive();
        _time.Advance(ScreenPane.Tick);
        pane.Write(new Markup("L15" + Environment.NewLine));
        Assert.Contains("↓ 4 new rows", Output[mark..], StringComparison.Ordinal);   // the committed block counted once: three, then this line

        pane.ScrollToEnd();
        pane.ScrollPage(-1);
        mark = Output.Length;
        pane.Write(new Markup("L16" + Environment.NewLine));
        Assert.Contains("↓ 1 new row", Output[mark..], StringComparison.Ordinal);   // counted from the new mark

        pane.Clear(home: true);
        Assert.False(pane.Scrolled);
    }

    [Fact]
    public void ScrolledHint_TheNewRowsAndTheMessage_MostUsefulFirst()
    {
        Assert.Equal("⇡ 12 rows below · PgUp/PgDn scroll · Ctrl+End bottom", ScreenPane.ScrolledHint(12));
        Assert.Equal("⇡ 1 row below · message 12 of 30 · PgUp/PgDn scroll · Ctrl+End bottom", ScreenPane.ScrolledHint(1, message: 12, messages: 30));
        Assert.Equal("↓ 42 new rows · message 12 of 30 · Ctrl+End bottom", ScreenPane.ScrolledHint(50, 42, 12, 30));
        Assert.Equal("↓ 1 new row · Ctrl+End bottom", ScreenPane.ScrolledHint(5, 1));
        Assert.True(TextCells.Width(ScreenPane.ScrolledHint(99_999, 0, 9_999, 9_999)) <= 119);
        Assert.StartsWith("↓ 42 new rows · message 12 of 30", ScreenPane.ScrolledRow(50, 40, 42, 12, 30).Trim(), StringComparison.Ordinal);   // cut from the end
    }

    [Fact]
    public void TheRenderer_MarksASentLine_AReplysFirstRow_AndASpeakerOnce()
    {
        using var pane = Pane();
        pane.Open();
        pane.Show();
        var transcript = new TranscriptRenderer(pane);

        transcript.User("hi");
        transcript.BeginAssistant();
        transcript.AppendDelta("Hello.");
        transcript.EndAssistant();
        Assert.Equal(2, pane.MessageStartRows().Count);

        transcript.Speaker("Bot", Color.Aqua);
        transcript.BeginAssistant();
        transcript.AppendDelta("One.");
        transcript.EndAssistant();
        transcript.Speaker("Other", Color.Aqua);
        transcript.BeginAssistant();
        transcript.AppendDelta("Two.");
        transcript.EndAssistant();
        Assert.Equal(4, pane.MessageStartRows().Count);   // one start per bot reply: the speaker's line, never the reply's first row too

        transcript.Notice("a notice");
        Assert.Equal(4, pane.MessageStartRows().Count);   // a notice is never a start
    }

    [Fact]
    public void TheRenderer_AReplyOpeningWithATool_MarksTheToolsRow_NotItsSecond()
    {
        using var pane = Pane();
        pane.Open();
        pane.Show();
        var transcript = new TranscriptRenderer(pane);

        transcript.BeginAssistant(markdown: true);
        transcript.Tool("read_file", """{"path":"a.txt"}""");
        transcript.Tool("read_file", """{"path":"b.txt"}""");
        transcript.AppendDelta("Done.");
        transcript.EndAssistant();

        var starts = pane.MessageStartRows();
        int start = Assert.Single(starts);
        var rows = pane.TranscriptRows();
        Assert.Contains("a.txt", string.Concat(rows[start].Select(s => s.Text)), StringComparison.Ordinal);
    }
}
