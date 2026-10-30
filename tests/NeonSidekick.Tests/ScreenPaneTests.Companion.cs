using NeonSidekick.Companion;
using NeonSidekick.Diagnostics;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// The welcome splash's robot on the pane (2026-10-20): <see cref="ScreenPane.ShowCompanion"/> stands it in the padding just above the
/// draft line (round 2; over a block of blank rows under the banner until then), fitted at every draw, and paints it between draws.
/// </summary>
public partial class ScreenPaneTests
{
    private static readonly string CompanionRow = new('▀', 24);

    /// <summary>
    /// A 40-wide pane <paramref name="height"/> rows tall with "banner" on its first row and a robot-sized companion (24 × 20, its head
    /// the top 11 rows) shown: the pane's four rows at the bottom, the padding the rest.
    /// </summary>
    private (ScreenPane Pane, FakeCompanion Companion, List<bool> Hover) PaneWithCompanion(int height = 30)
    {
        _console.Profile.Height = height;
        var pane = Pane();
        var hover = new List<bool>();
        pane.Hover = hover.Add;
        pane.Show();
        pane.Write(new Text("banner\n"));
        var companion = new FakeCompanion(rows: 20, headRows: 11);
        pane.ShowCompanion(companion);
        return (pane, companion, hover);
    }

    private void Frame() => _time.Advance(ScreenPane.CompanionFrame);

    [Fact]
    public void ACompanion_StandsOnThePaddingsLastRows_JustAboveTheDraftLine_Centered()
    {
        var (pane, companion, hover) = PaneWithCompanion();
        using (pane)
        {
            Assert.Equal(20, Count(Output, CompanionRow));   // the show's own draw put its 20 rows in
            Assert.Contains(new string(' ', 8) + CompanionRow + "\n" + Rule(40) + "\n" + InputLine.PromptGlyph, Output);   // its last row on the upper rule
            Frame();

            Assert.Equal((8, 6), companion.Origins[^1]);   // (40 − 24) / 2 across; 30 − 4 − 20 down: its feet on the upper rule
            Assert.False(companion.HeadOnly);
            Assert.Equal([true], hover);
            Assert.True(pane.HasCompanion);
        }
    }

    [Fact]
    public void ACompanion_IsPaintedEachFrameAPixelChanged_AndOnlyThen()
    {
        var (pane, companion, _) = PaneWithCompanion();
        using (pane)
        {
            int before = Count(Output, CompanionRow);
            Frame();
            Assert.Equal(before + 20, Count(Output, CompanionRow));

            companion.Changes = false;
            Frame();
            Assert.Equal(before + 20, Count(Output, CompanionRow));
            Assert.Equal(2, companion.Updates);
        }
    }

    [Fact]
    public void AShortWindow_ShowsTheHeadAlone_AndATallerOne_TheWholeAgain()
    {
        var (pane, companion, _) = PaneWithCompanion(height: 20);   // 15 rows of padding: the head's 11 fit, the whole 20 don't
        using (pane)
        {
            Assert.True(companion.HeadOnly);
            Assert.Equal(11, Count(Output, CompanionRow));
            Frame();
            Assert.Equal((8, 5), companion.Origins[^1]);

            _console.Profile.Height = 30;
            _time.Advance(ScreenPane.Tick);   // the tick sees the resize and draws again

            Assert.False(companion.HeadOnly);
            Frame();
            Assert.Equal((8, 6), companion.Origins[^1]);
        }
    }

    [Fact]
    public void NoRoomForTheHead_PlacesNothing_ButTheClockRuns()
    {
        var log = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Splash") log.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        try
        {
            var (pane, companion, _) = PaneWithCompanion(height: 14);   // 9 rows of padding
            using (pane)
            {
                Frame();
                Frame();

                Assert.DoesNotContain(CompanionRow, Output);
                Assert.Empty(companion.Origins);
                Assert.Equal(2, companion.Updates);
            }
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(["Robot shown.", "Robot fits: none"], log);
    }

    [Fact]
    public void TheCompanion_IsNeverInTheStore()
    {
        var (pane, _, _) = PaneWithCompanion();
        using (pane)
        {
            var mark = pane.StoreMark;
            Frame();
            pane.Write(new Text("a startup line\n"));

            Assert.Equal(2, pane.FlowRow);   // the banner and the line: nothing of the companion in the flow
            var copied = pane.LinesSince(mark);
            Assert.NotNull(copied);
            Assert.DoesNotContain(copied.Segments, s => s.Text.Contains('▀'));
        }
    }

    [Fact]
    public void ThePointer_FocusAndCaret_ReachTheCompanionAtTheNextFrame_InScreenCells()
    {
        var (pane, companion, _) = PaneWithCompanion();
        using (pane)
        {
            Frame();
            Assert.Empty(companion.Carets);   // the draft as it stood at the show is not news

            pane.PointerMoved(30, 3);
            pane.PointerMoved(31, 4);       // moves between two frames: the last one counts
            pane.FocusChanged(false);
            pane.ShowInput("ab", 2);
            Frame();

            Assert.Equal([(31, 4)], companion.Pointers);
            Assert.Equal([false], companion.Focus);
            var caret = Assert.Single(companion.Carets);
            Assert.Equal((TextCells.Width(InputLine.PromptGlyph) + 2, 27, TextCells.Width(InputLine.PromptGlyph), 40), caret);   // the input row: the second of the pane's four at the bottom

            Frame();
            Assert.Single(companion.Pointers);
            Assert.Single(companion.Carets);
        }
    }

    [Fact]
    public void NothingIsPainted_InABatch_AndTheBatchsDrawStandsItAgain()
    {
        var (pane, companion, _) = PaneWithCompanion();
        using (pane)
        {
            Frame();
            int painted = Count(Output, CompanionRow);
            using (pane.Batch())
            {
                Frame();
                Assert.Equal(painted, Count(Output, CompanionRow));
            }

            Assert.Equal(painted + 20, Count(Output, CompanionRow));   // the batch's closing draw wrote its rows
            Assert.Equal(2, companion.Updates);
        }
    }

    [Fact]
    public void BetweenAResizeAndTheTicksRedraw_NothingIsPainted()
    {
        var (pane, _, _) = PaneWithCompanion();
        using (pane)
        {
            Frame();
            int painted = Count(Output, CompanionRow);
            _console.Profile.Width = 60;   // the tick has not seen it yet
            Frame();

            Assert.Equal(painted, Count(Output, CompanionRow));
        }
    }

    [Fact]
    public void HideCompanion_StopsIt_AndTheNextDrawLeavesItsRowsBlank()
    {
        var (pane, companion, hover) = PaneWithCompanion();
        using (pane)
        {
            Frame();
            pane.HideCompanion();
            int before = Count(Output, CompanionRow);
            pane.Write(new Text("› hi\n"));   // the sent line's commit draws the pane
            Frame();

            Assert.False(pane.HasCompanion);
            Assert.Equal(before, Count(Output, CompanionRow));
            Assert.Equal([true, false], hover);
            Assert.Equal(1, companion.Updates);
        }
    }

    [Fact]
    public void Clear_DropsTheCompanion_AndItsHover()
    {
        var (pane, companion, hover) = PaneWithCompanion();
        using (pane)
        {
            Frame();
            pane.Clear(home: true);
            Frame();

            Assert.False(pane.HasCompanion);
            Assert.Equal([true, false], hover);
            Assert.Equal(1, companion.Updates);
        }
    }

    [Fact]
    public void ACompanionThatThrows_IsDropped_WithAWarning_AndNeverCrashesTheTimer()
    {
        var (pane, companion, hover) = PaneWithCompanion();
        using (pane)
        {
            var warnings = new List<DiagnosticEvent>();
            Action<DiagnosticEvent> capture = e => { if (e.Category == "Splash" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
            DiagnosticLog.Emitted += capture;
            try
            {
                companion.Throws = true;
                Frame();
                Frame();
            }
            finally
            {
                DiagnosticLog.Emitted -= capture;
            }

            Assert.False(pane.HasCompanion);
            Assert.Equal(1, companion.Updates);
            Assert.Contains("the fake companion threw", Assert.Single(warnings).Message);
            Assert.Equal([true, false], hover);
        }
    }

    [Fact]
    public void ANarrowWindow_PlacesNothing()
    {
        _console.Profile.Width = ScreenPane.MinCompanionWidth - 1;
        var (pane, companion, _) = PaneWithCompanion();
        using (pane)
        {
            Frame();
            Frame();

            Assert.DoesNotContain(new string('▀', 20), Output);
            Assert.Empty(companion.Origins);
            Assert.Equal(2, companion.Updates);
        }
    }

    [Fact]
    public void Dispose_StopsTheFrames_AndTheHover()
    {
        var (pane, companion, hover) = PaneWithCompanion();
        pane.Dispose();
        Frame();

        Assert.Equal(0, companion.Updates);
        Assert.Equal([true, false], hover);
    }
}
