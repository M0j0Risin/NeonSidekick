using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// Clickable links and paths on the pane (2026-11-01, plan memoized-dancing-horizon item #2): the hit test reads a stored segment's link,
/// hover is asked for while a link is stored (one owner of the switch with the splash's robot), and the address under the pointer is
/// worked out on the tick without asking the terminal where its cursor is.
/// </summary>
public partial class ScreenPaneTests
{
    private const string WebUrl = "https://a.b/c";

    /// <summary>"see " then <see cref="WebUrl"/> linked, then " ok" and a line break.</summary>
    private static Paragraph LinkedLine(string url = WebUrl, string text = WebUrl) =>
        new Paragraph().Append("see ").Append(text, Style.Plain, new Link(url)).Append(" ok\n");

    // The window's rows as the toggle test lays them out: the cursor on buffer row 100, rule 99, the region 93–98, the window's top 93.
    private int _cursorTopCalls;

    private ScreenPane CountingPane()
    {
        _cursorTop = 100;
        return new ScreenPane(_console, new ScreenGeometry(() => _cursorRow, () =>
        {
            _cursorTopCalls++;
            return _cursorTop;
        }, () => 93), _time);
    }

    [Fact]
    public void TryHitLink_ReadsTheLink_UnderTheCell_AndMissesEitherSide()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("a\n"));
        pane.Write(LinkedLine());   // row 94: "see https://a.b/c ok"

        Assert.True(pane.TryHitLink(4, 94, out string target));
        Assert.Equal(WebUrl, target);
        Assert.True(pane.TryHitLink(4 + WebUrl.Length - 1, 94, out _));
        Assert.False(pane.TryHitLink(3, 94, out _));
        Assert.False(pane.TryHitLink(4 + WebUrl.Length, 94, out _));
        Assert.False(pane.TryHitLink(4, 93, out _));   // "a"
        Assert.False(pane.TryHitLink(4, 95, out _));   // under the flow
    }

    [Fact]
    public void TryHitLink_FindsAWrappedLink_OnItsSecondRow()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        string url = "https://example.com/" + new string('x', 40);   // 60 cells: "see " on row 93, the address's first 40 on 94, the rest on 95
        pane.Write(LinkedLine(url, url));

        Assert.False(pane.TryHitLink(2, 93, out _));
        Assert.True(pane.TryHitLink(30, 94, out string first));
        Assert.True(pane.TryHitLink(2, 95, out string second));
        Assert.Equal(url, first);
        Assert.Equal(url, second);
    }

    [Fact]
    public void APathLink_IsNeverWrittenAsOsc8_YetTheHitTestReadsIt()
    {
        _console.EmitAnsiSequences = true;
        _console.Profile.Capabilities.Links = true;
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        string path = LinkText.PathLink(@"C:\My Docs\a[1].txt");
        pane.Write(new Markup("a\n"));
        pane.Write(LinkedLine(path, "a[1].txt"));

        Assert.DoesNotContain("]8;", Output, StringComparison.Ordinal);
        Assert.True(pane.TryHitLink(5, 94, out string target));
        Assert.Equal(path, target);   // the store keeps it unfiltered, the space and brackets as they are

        pane.Write(LinkedLine());
        Assert.Contains("\u001b]8;", Output, StringComparison.Ordinal);   // a web link is written as one
    }

    [Fact]
    public void Hover_IsAskedWhileALinkIsStored_AndOffWithTheLast()
    {
        _cursorTop = 100;
        using var pane = Pane();
        var hover = new List<bool>();
        pane.Hover = hover.Add;
        pane.Show();
        pane.Write(new Markup("plain\n"));
        _time.Advance(ScreenPane.Tick);
        Assert.Empty(hover);

        pane.Write(LinkedLine());
        _time.Advance(ScreenPane.Tick);
        Assert.Equal([true], hover);
        _time.Advance(ScreenPane.Tick);
        Assert.Equal([true], hover);   // asked once, not every tick

        pane.Clear(home: true);   // the last link gone with the screen
        Assert.Equal([true, false], hover);
    }

    [Fact]
    public void TheSplashEnding_KeepsHoverOn_WhileLinksAreStored()
    {
        var (pane, _, hover) = PaneWithCompanion();
        using (pane)
        {
            pane.Write(LinkedLine());
            pane.HideCompanion();
            _time.Advance(ScreenPane.Tick);
            Assert.Equal([true], hover);   // never turned off between the robot and the link
        }

        Assert.Equal([true, false], hover);   // the pane's end turns it off for good
    }

    [Fact]
    public void TheAddressUnderThePointer_IsWorkedOutOnTheTick_WithoutAskingTheTerminal()
    {
        using var pane = CountingPane();
        pane.LinkHint = target => "goes to " + target;
        pane.Show();
        pane.Write(new Markup("a\n"));
        pane.Write(LinkedLine());   // buffer row 94
        _time.Advance(ScreenPane.Tick);   // hover asked
        Assert.Null(pane.LinkHintNow);   // no move yet

        int calls = _cursorTopCalls;
        pane.PointerMoved(6, 94);
        for (int i = 0; i < 20; i++)
        {
            _time.Advance(ScreenPane.Tick);
        }

        Assert.Equal("goes to " + WebUrl, pane.LinkHintNow);
        Assert.Equal(calls, _cursorTopCalls);   // the hover never asks where the cursor is
        Assert.Contains("goes to " + WebUrl, Output, StringComparison.Ordinal);   // no layout: the status row's standing hint shows it

        pane.PointerMoved(1, 94);   // "see": off the link
        _time.Advance(ScreenPane.Tick);
        Assert.Null(pane.LinkHintNow);
    }

    [Fact]
    public void TheAddressUnderThePointer_NeverShows_UnderAReply()
    {
        using var pane = CountingPane();
        pane.LinkHint = target => "goes to " + target;
        pane.Show();
        pane.Write(new Markup("a\n"));
        pane.Write(LinkedLine());
        _time.Advance(ScreenPane.Tick);
        pane.PointerMoved(6, 94);
        _time.Advance(ScreenPane.Tick);
        Assert.NotNull(pane.LinkHintNow);

        using (pane.BeginBusy("thinking"))
        {
            _time.Advance(ScreenPane.Tick);
            Assert.Null(pane.LinkHintNow);
        }
    }

    [Fact]
    public void TheHintsBar_TakesTheAddress_WhileItsRowIsUp_AfterAPanesKeys()
    {
        _cursorTop = 100;
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => _cursorRow, () => _cursorTop, () => 93), _time)
        {
            HintsBar = () => HintsBarMode.Always,
        };
        pane.LinkHint = target => "goes to " + target;
        pane.Show();
        for (int i = 0; i < 6; i++)
        {
            pane.Write(LinkedLine());   // every row of the region a link's, wherever the bar puts it
        }

        _time.Advance(ScreenPane.Tick);
        int mark = Output.Length;
        pane.PointerMoved(6, 94);
        _time.Advance(ScreenPane.Tick);
        Assert.Equal("goes to " + WebUrl, pane.LinkHintNow);
        Assert.Contains("goes to " + WebUrl, Output[mark..], StringComparison.Ordinal);
    }
}
