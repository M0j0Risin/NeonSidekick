using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// A resize the tick has not handled yet (2026-11-02, the review's part 4): a draw before it climbs the taller window, a key writes
/// nothing at the half-known size, the emoji button is hit where it is drawn, and a reply's ending committed under a menu is stored.
/// </summary>
public partial class ScreenPaneTests
{
    /// <summary>
    /// A taller window, then PgUp before the tick (review P8-1: the lift climbed the old height's rows and the pane drew below the top, and
    /// the build was marked done so the tick never rebuilt): the draw climbs the new height from the top row.
    /// </summary>
    [Fact]
    public void ATallerWindow_ThenAScroll_BeforeTheTick_ClimbsTheNewHeight()
    {
        _console.EmitAnsiSequences();
        using var pane = Scrollable();
        pane.ScrollBy(-2);
        _console.Profile.Height = 14;
        int mark = Output.Length;
        pane.ScrollBy(-1);
        Assert.Contains("\e[14A\e[40D\e[J", Output[mark..]);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Output[mark..], @"\e\[J"));

        mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.DoesNotContain("\e[J", Output[mark..], StringComparison.Ordinal);   // the scroll's draw was the rebuild
    }

    /// <summary>
    /// A key after a narrowing, before the tick (review P8-2: the draft row was drawn at the new width over the old screen's rows): the draft
    /// is remembered and nothing is written until the tick rebuilds the screen, which shows it.
    /// </summary>
    [Fact]
    public void AKey_AfterANarrowing_BeforeTheTick_WritesNothing()
    {
        using var pane = Pane();
        pane.Show();
        pane.ShowInput("hi", 2);
        _console.Profile.Width = 30;
        int mark = Output.Length;
        pane.ShowInput("hi!", 3);
        Assert.Equal(mark, Output.Length);

        _time.Advance(ScreenPane.Tick);
        Assert.Contains("hi!", Output[mark..], StringComparison.Ordinal);
    }

    /// <summary>
    /// The emoji button after a narrowing, before the tick (review P7-8: the hit read the live width, so a click on the ☺ still drawn at
    /// column 38 missed): hit where it is drawn, then where the tick draws it.
    /// </summary>
    [Fact]
    public void TheEmojiButton_AfterANarrowing_IsHitWhereItIsDrawn()
    {
        using var pane = PaneWithDraft(100, "hi", 2);
        pane.EmojiButton = () => true;
        pane.ShowInput("hi!", 3);
        Assert.True(pane.TryHitEmojiButton(38, 100));

        _console.Profile.Width = 30;
        Assert.True(pane.TryHitEmojiButton(38, 100));
        Assert.False(pane.TryHitEmojiButton(28, 100));

        _time.Advance(ScreenPane.Tick);
        Assert.True(pane.TryHitEmojiButton(28, 100));
        Assert.False(pane.TryHitEmojiButton(38, 100));
    }

    /// <summary>
    /// A reply's ending committed under a menu (review P8-6: written raw and never stored, so a resize's rebuild, PgUp, /find and /copy
    /// lost it): stored; the menu's own frames are not.
    /// </summary>
    [Fact]
    public async Task ALiveCommit_UnderAMenu_IsStored_TheMenusFramesAreNot()
    {
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("before" + Environment.NewLine));
        pane.SetLive(new Markup("the reply's end" + Environment.NewLine));
        _time.Advance(ScreenPane.Tick);

        await pane.ModalAsync(() =>
        {
            pane.Write(new Markup("a menu frame" + Environment.NewLine));
            pane.CommitLive();
            return Task.FromResult(0);
        });

        var rows = RowTexts(pane);
        Assert.Contains("the reply's end", rows);
        Assert.DoesNotContain("a menu frame", rows);
    }
}
