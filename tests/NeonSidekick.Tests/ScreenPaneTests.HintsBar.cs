using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// The Hints bar (2026-10-14, the user's ask: the hint row split into a Status bar and a bar of its own for the keys' hints, shown
/// over it while there is something to say): a row while there is a hint, held blank until a natural redraw (a send, a turn's end, a
/// pane opening or closing, a resize) or the idle timeout, so typing never moves the input line; the ladder's one hint; <c>always</c>
/// and <c>off</c>; the short window; its clicks.
/// </summary>
public partial class ScreenPaneTests
{
    private const string Arrows = "←/→ = pictures";

    /// <summary>A pane with the Hints bar on <paramref name="mode"/>, the Status bar's own text <c>idle</c>, and <paramref name="keys"/> as the keys' lead.</summary>
    private ScreenPane HintsPane(string mode, Func<string?> keys)
    {
        _cursorTop = 100;   // the upper rule at 99, the input at 100, the lower rule at 101, the first bar at 102
        var pane = Pane();
        pane.HintsBar = () => mode;
        pane.Hint = () => "idle";
        pane.KeysHint = keys;
        pane.Show();
        return pane;
    }

    [Fact]
    public void HintsBarMode_ResolvesItsWords_AndTheLadderPicksOne()
    {
        Assert.Equal(["auto", "always", "off"], HintsBarMode.Names);
        Assert.Equal("auto", HintsBarMode.Default);
        Assert.Equal(HintsBarStyle.Auto, HintsBarMode.Resolve(null));
        Assert.Equal(HintsBarStyle.Auto, HintsBarMode.Resolve("sometimes"));   // a display setting: no warning, the default
        Assert.Equal(HintsBarStyle.Always, HintsBarMode.Resolve(" ALWAYS "));
        Assert.Equal(HintsBarStyle.Off, HintsBarMode.Resolve("off"));
        Assert.All(HintsBarMode.Names, name => Assert.Equal(name, HintsBarMode.Name(HintsBarMode.Resolve(name))));

        Assert.Equal("alert", HintsBarMode.Pick("alert", "armed", "pane", "keys"));
        Assert.Equal("armed", HintsBarMode.Pick(null, "armed", "pane", "keys"));
        Assert.Equal("pane", HintsBarMode.Pick("", null, "pane", "keys"));
        Assert.Equal("keys", HintsBarMode.Pick(null, "", null, "keys"));
        Assert.Null(HintsBarMode.Pick(null, "", "", null));
    }

    [Fact]
    public void HintsBar_Off_ByDefault_ThePaneAsItWas()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.KeysHint = () => Arrows;
        pane.Show();

        Assert.False(pane.HintsBarShown);
        Assert.Equal(0, pane.HintsRows);
        Assert.Equal(_console.Profile.Height, pane.LayoutHeight);
        Assert.Equal(0, pane.ReservedBarRows);
        Assert.EndsWith(Rule(40) + "\nidle", Output);
        Assert.DoesNotContain(Arrows, Output);   // the screen puts the keys on the Status bar itself when the bar is off

        pane.ShowOverlay(new Markup("a"), "ESC closes");
        Assert.EndsWith(Rule(40) + "\nESC closes", Output);   // a pane's keys on the Status bar, as ever
        Assert.Equal(0, pane.HintsRows);
    }

    [Fact]
    public void HintsBar_Auto_ComesWithAHint_IsHeldBlankThroughTyping_GoesAtTheSend()
    {
        string? keys = null;
        using var pane = HintsPane(HintsBarMode.Auto, () => keys);
        Assert.True(pane.HintsBarShown);
        Assert.Equal(0, pane.HintsRows);
        Assert.Equal(_console.Profile.Height - 1, pane.LayoutHeight);   // the row reserved for a pane's keys
        Assert.Equal(1, pane.ReservedBarRows);
        Assert.EndsWith(Rule(40) + "\nidle", Output);

        // A hint: the whole pane again, the bar's row over the Status bar's.
        int draws = Draws;
        keys = Arrows;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(draws + 1, Draws);
        Assert.Equal(1, pane.HintsRows);
        Assert.EndsWith(Rule(40) + "\n" + Arrows + "\nidle", Output);
        Assert.True(pane.TryHitHint(5, 102, out var hit));
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);   // plain text: /settings, as the Status bar's own
        Assert.True(pane.TryHitHint(5, 103, out _));        // the Status bar under it

        // The hint goes as the draft fills: the row stays, blank, written in place.
        keys = null;
        pane.ShowInput("a", 1);
        int mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(draws + 1, Draws);
        Assert.Equal(1, pane.HintsRows);
        Assert.DoesNotContain(Arrows, Output[mark..]);

        // A notice written meanwhile is no collapse: the flow write draws the pane, the row with it.
        pane.Write(new Markup("a notice\n"));
        Assert.Equal(1, pane.HintsRows);
        pane.ShowInput("ab", 2);
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(1, pane.HintsRows);

        // The send: a natural redraw, the held row goes with it.
        pane.CommitInput("ab");
        Assert.Equal(0, pane.HintsRows);
        Assert.EndsWith(Rule(40) + "\nidle", Output);
    }

    [Fact]
    public void HintsBar_Auto_AHeldRow_GoesAfterTheIdleTimeout_AKeyStartsItAgain()
    {
        string? keys = Arrows;
        using var pane = HintsPane(HintsBarMode.Auto, () => keys);
        Assert.Equal(1, pane.HintsRows);
        keys = null;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(1, pane.HintsRows);

        _time.Advance(ScreenPane.HintsIdle - TimeSpan.FromSeconds(1));
        pane.ShowInput("a", 1);   // a key: the clock starts again
        _time.Advance(TimeSpan.FromSeconds(2));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(1, pane.HintsRows);

        _time.Advance(ScreenPane.HintsIdle);
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(0, pane.HintsRows);
        Assert.EndsWith(Rule(40) + "\nidle", Output);
    }

    [Fact]
    public void HintsBar_Always_HoldsItsRow_ANewHintIsWrittenInPlace()
    {
        string? keys = null;
        using var pane = HintsPane(HintsBarMode.Always, () => keys);
        Assert.Equal(1, pane.HintsRows);
        Assert.EndsWith(Rule(40) + "\n\nidle", Output);

        int draws = Draws;
        keys = Arrows;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(draws, Draws);   // the row in place: no reshape
        Assert.Contains(Arrows, Output);

        pane.CommitInput("x");
        _time.Advance(ScreenPane.HintsIdle);
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(1, pane.HintsRows);
    }

    [Fact]
    public void HintsBar_APanesKeys_TakeTheBar_TheStatusBarKeepsItsOwn_ClosingTakesTheRow()
    {
        using var pane = HintsPane(HintsBarMode.Auto, () => null);
        pane.ShowOverlay(new Markup("a"), "ESC closes");
        Assert.Equal(1, pane.HintsRows);
        Assert.EndsWith(Rule(40) + "\nESC closes\nidle", Output);

        pane.CloseOverlay();
        Assert.Equal(0, pane.HintsRows);
        Assert.EndsWith(Rule(40) + "\nidle", Output);
    }

    [Fact]
    public void HintsBar_TheLadder_TheArmedPromptOverThePanesKeys_TheAlertOverAll_AFlashStaysOnTheStatusBar()
    {
        string? armed = null;
        _cursorTop = 100;
        using var pane = Pane();
        pane.HintsBar = () => HintsBarMode.Auto;
        pane.Hint = () => "idle";
        pane.ArmedHint = () => armed;
        pane.KeysHint = () => Arrows;
        pane.Show();
        Assert.EndsWith("\n" + Arrows + "\nidle", Output);

        // At once, as the screen's first Ctrl+C asks: the bar's row rewritten in place, the Status bar left alone.
        armed = "Ctrl+C again to exit";
        int draws = Draws;
        int mark = Output.Length;
        pane.RefreshHint();
        Assert.Equal(draws, Draws);
        Assert.Equal(armed, Output[mark..]);

        mark = Output.Length;
        pane.SetAlertHint("press again to unload", TimeSpan.FromSeconds(3));
        Assert.EndsWith("press again to unload", Output[mark..]);   // the warning on the bar, over the armed prompt
        Assert.Equal("press again to unload", pane.AlertHint);

        pane.SetAlertHint(null, TimeSpan.Zero);
        mark = Output.Length;
        pane.Flash("copied 3 characters");
        Assert.Equal("copied 3 characters", Output[mark..]);   // the flash on the Status bar, the bar's hint kept
        Assert.Equal(draws, Draws);
    }

    [Fact]
    public void HintsBar_UnderAReply_TheKeysStandAside_TheStopHintStaysWithTheSpinner_APanesKeysTakeTheBar()
    {
        string? keys = Arrows;
        using var pane = HintsPane(HintsBarMode.Auto, () => keys);
        pane.StopHintShown = () => true;
        Assert.Equal(1, pane.HintsRows);
        int draws = Draws;
        int mark = Output.Length;
        using (pane.BeginBusy("thinking"))
        {
            Assert.Equal(1, pane.HintsRows);   // held, blank: no jump at the turn's start
            Assert.Equal(draws, Draws);
            Assert.DoesNotContain(Arrows, Output[mark..]);
            Assert.Contains(ScreenPane.StopHint, Output[mark..]);

            mark = Output.Length;
            pane.ShowOverlay(new Markup("a"), "ESC closes");
            Assert.Contains("\nESC closes\n", Output[mark..]);
            Assert.EndsWith(ScreenPane.StopHint, Output.TrimEnd());   // the way out stays with the spinner on the Status bar
            pane.CloseOverlay();
        }

        Assert.Equal(1, pane.HintsRows);   // the keys back at the turn's end, in the held row
        Assert.EndsWith("\n" + Arrows + "\nidle", TrimmedOutput());

        // The turn's end was the natural moment and the row was wanted then: the keys going later (the first letter typed) keep it.
        keys = null;
        pane.ShowInput("a", 1);
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(1, pane.HintsRows);
    }

    [Fact]
    public void HintsBar_AShortWindow_HasNoRowForIt_TheToolbarGivesWayFirst()
    {
        _console.Profile.Height = ScreenPane.PaneRows + 1;
        using (var tiny = HintsPane(HintsBarMode.Auto, () => Arrows))
        {
            Assert.False(tiny.HintsBarShown);
            Assert.Equal(0, tiny.HintsRows);
        }

        _console.Profile.Height = ScreenPane.PaneRows + 2;
        string? keys = null;
        using var pane = HintsPane(HintsBarMode.Auto, () => keys);
        pane.Toolbar = () => new ScreenPane.ToolbarParts("🔧", "");
        _time.Advance(ScreenPane.Tick);
        Assert.Equal((0, 1), (pane.HintsRows, pane.ToolbarRows));
        keys = Arrows;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal((1, 0), (pane.HintsRows, pane.ToolbarRows));   // one bar for the other: the pane's height kept
    }

    /// <summary>The output with each line's trailing blanks cut (an in-place rewrite pads the row it shortens).</summary>
    private string TrimmedOutput() => string.Join("\n", Output.Split('\n').Select(line => line.TrimEnd()));

    /// <summary>The last <paramref name="count"/> rows the console holds, top first.</summary>
    private string[] LastRows(int count) => Output.Split('\n')[^count..];
}
