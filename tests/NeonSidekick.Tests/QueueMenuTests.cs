using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class QueueMenuTests : IDisposable
{
    private readonly TestConsole _console = new();
    private readonly MessageQueue _queue = new();
    private readonly ManualTimeProvider _time = new();

    public QueueMenuTests()
    {
        _console.Interactive();
        _console.Profile.Width = 100;
        _console.Profile.Height = 40;
    }

    public void Dispose() => _console.Dispose();

    private void Push(params ConsoleKeyInfo[] keys)
    {
        foreach (var key in keys)
        {
            _console.Input.PushKey(key);
        }
    }

    private void Seed(params string[] texts)
    {
        foreach (var text in texts)
        {
            _queue.Enqueue(new QueuedMessage(text, new SubmittedLine(text, text, [], text)));
        }
    }

    private IEnumerable<string> Labels() => _queue.Snapshot().Select(m => m.Label);

    /// <summary>The menu over a pane with geometry: the list is a level of the pane, the removal notice its status line.</summary>
    private (QueueMenu Menu, ScreenPane Pane) PaneMenu()
    {
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null), _time) { Hint = () => "idle" };
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menu = new QueueMenu(_queue, new TranscriptRenderer(pane), new MenuPane(pane, keys));
        pane.Show();
        return (menu, pane);
    }

    /// <summary>The menu over a pane whose cursor row the geometry reports (the click's frame) and a scripted source that carries clicks.</summary>
    private (QueueMenu Menu, ScreenPane Pane, ScriptedInput Input) ClickablePaneMenu(int cursorTop)
    {
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => cursorTop), _time) { Hint = () => "idle" };
        var input = new ScriptedInput();
        var keys = new KeySource(input, TimeSpan.FromMilliseconds(1));
        var menu = new QueueMenu(_queue, new TranscriptRenderer(pane), new MenuPane(pane, keys));
        pane.Show();
        return (menu, pane, input);
    }

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    /// <summary>A title row as the pane prints it: the text, then the × close glyph in column width − 2.</summary>
    private static string Titled(string row, int width = 100) => row + new string(' ', width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    /// <summary>A row as the pane prints it: the position, two spaces, the text.</summary>
    private static string Row(int index, string text) => (index + 1) + "  " + text;

    /// <summary>The title row as the pane prints it since 2026-09-21: the label, then the one button as a dim tab (a space either side), two spaces between.</summary>
    private const string Strip = QueueMenu.Title + " │ ⊠ clear all ";

    [Fact]
    public void Strings_ArePinned()
    {
        Assert.Equal('c', QueueMenu.ClearAllKey);
        Assert.Equal(new MenuButton("⊠ clear all", 'c'), Assert.Single(QueueMenu.Buttons));
        Assert.Equal('s', QueueMenu.SendKey);
        Assert.Equal([new MenuButton("➤ send", 's'), new MenuButton("⊠ clear all", 'c')], QueueMenu.ButtonsWithSend);
        Assert.Equal("Enter = remove · s = send · c = clear all · ESC = back", QueueMenu.KeysWithSend);
        Assert.Equal("(⏳ nothing queued)", QueueMenu.EmptyNotice);
        Assert.Equal("(⏳ removed: and then?)", QueueMenu.RemovedNotice("and then?"));
        Assert.Equal("[#9A8BB8]1[/]  a [[b]]", QueueMenu.RowMarkup(0, "a [b]"));
        Assert.Equal("[#9A8BB8]12[/]  x", QueueMenu.RowMarkup(11, "x"));
    }

    [Fact]
    public async Task OnThePane_APastedLine_ShowsItsLabel()
    {
        Seed("summarise [Pasted text +8 lines]");
        var (menu, pane) = PaneMenu();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n▸ " + Row(0, "summarise [Pasted text +8 lines]") + "\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task Empty_PrintsNothingQueued_ToTheTranscript_AndOpensNoPane()
    {
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(pane.OverlayOpen);
        Assert.Contains("  · " + QueueMenu.EmptyNotice + "\n", _console.Output);
        Assert.Equal(flow + 1, pane.FlowRow);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheListIsTheOverlay_AndEscapeClosesIt()
    {
        Seed("one", "two");
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(Rule(100) + "\n" + Titled(Strip) + "\n \n▸ " + Row(0, "one") + "\n  " + Row(1, "two") + "\n" + Rule(100) + "\n" + QueueMenu.Keys + "\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        Assert.Equal(flow, pane.FlowRow);   // nothing reached the transcript
        Assert.Equal(new[] { "one", "two" }, Labels());
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_EnterRemoves_TheNoticeIsTheStatusLine_AndTheListReshows_Renumbered()
    {
        Seed("one", "two", "three");
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;
        Push(Keys.Down, Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        // The re-shown list: the notice where the spacer was, the cursor on the row that slid up, the numbers fresh.
        Assert.Contains("\n" + Titled(Strip) + "\n  · (⏳ removed: two)\n  " + Row(0, "one") + "\n▸ " + Row(1, "three") + "\n" + Rule(100) + "\n" + QueueMenu.Keys + "\n", _console.Output);
        Assert.Equal(flow, pane.FlowRow);   // the notice was a status line, not a transcript line
        Assert.False(pane.OverlayOpen);
        Assert.Equal(new[] { "one", "three" }, Labels());
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_RemovingTheLastRow_ClosesThePane_ThenTheEmptyNoticeGoesToTheTranscript()
    {
        Seed("only");
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;
        Push(Keys.Enter);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(pane.OverlayOpen);
        Assert.Contains("  · " + QueueMenu.EmptyNotice + "\n", _console.Output);
        Assert.DoesNotContain("(⏳ removed: only)", _console.Output);   // said to a status line the close forgot
        Assert.Equal(flow + 1, pane.FlowRow);
        Assert.Equal(0, _queue.Count);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ADoubleClickOnARow_RemovesIt_AsEnterWould()
    {
        Seed("one", "two", "three");
        var (menu, pane, input) = ClickablePaneMenu(cursorTop: 100);
        input.PushClick(7, 104);                         // "three": the highlight moves (strip 100, spacer 101, one 102, two 103)
        input.PushClick(9, 104);                         // again, within the interval: the pair removes it
        input.Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n  · (⏳ removed: three)\n  " + Row(0, "one") + "\n▸ " + Row(1, "two") + "\n", _console.Output);
        Assert.Equal(new[] { "one", "two" }, Labels());
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>The clear-all button (2026-09-21): a click on it drops every message, the pane closes and the transcript gets the loop's dropped notice — as /queue clear prints it.</summary>
    [Fact]
    public async Task OnThePane_AClickOnTheClearAllButton_DropsEveryMessage_ClosesThePane_AndSaysSoOnTheTranscript()
    {
        Seed("one", "two", "three");
        var (menu, pane, input) = ClickablePaneMenu(cursorTop: 100);
        int flow = pane.FlowRow;
        input.PushClick(10, 100);                        // the strip row: "Queue" 0–4, the gap, the button from column 7

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n" + Titled(Strip) + "\n \n▸ " + Row(0, "one") + "\n", _console.Output);
        Assert.Contains("  · " + ChatScreen.QueueDroppedNotice(3) + "\n", _console.Output);
        Assert.DoesNotContain("(removed:", _console.Output);
        Assert.Equal(flow + 1, pane.FlowRow);
        Assert.Empty(Labels());
        Assert.False(pane.OverlayOpen);
        Assert.False(input.IsAvailable);
        pane.Dispose();
    }

    /// <summary>The button's key does the same; a click on the label is nothing (ESC then leaves the queue whole).</summary>
    [Fact]
    public async Task OnThePane_TheClearAllKey_IsTheButton_AndAClickOnTheLabelIsNothing()
    {
        Seed("one", "two");
        var (menu, pane, input) = ClickablePaneMenu(cursorTop: 100);
        input.PushClick(2, 100);                         // the label
        input.Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(new[] { "one", "two" }, Labels());
        Assert.DoesNotContain("dropped", _console.Output);

        input.Push(Keys.Char('c'));

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  · " + ChatScreen.QueueDroppedNotice(2) + "\n", _console.Output);
        Assert.Empty(Labels());
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ARowTheLoopSentMeanwhile_IsNotTakenForAnother()
    {
        Seed("one", "two");
        var (menu, pane, input) = ClickablePaneMenu(cursorTop: 100);
        int waits = 0;
        input.OnWait = () =>
        {
            // The pane has drawn its snapshot and waits: the loop dequeues "one" now, so row 0
            // reads "two" — Enter on row 0 must not take it; the list re-shows, ESC leaves.
            if (waits++ == 0)
            {
                Assert.True(_queue.TryDequeue(out var sent));
                Assert.Equal("one", sent.Label);
                input.Push(Keys.Enter);
            }
            else
            {
                input.Push(Keys.Escape);
            }
        };

        await menu.ShowAsync(CancellationToken.None);

        Assert.DoesNotContain("(removed:", _console.Output);
        Assert.Contains("\n▸ " + Row(0, "two") + "\n", _console.Output);   // the re-shown list, renumbered
        Assert.Equal(new[] { "two" }, Labels());
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>The title row with the send button (2026-10-05): the label, then send and clear all as dim tabs.</summary>
    private const string SendStrip = QueueMenu.Title + " │ ➤ send · ⊠ clear all ";

    /// <summary>
    /// The send button (2026-10-05, the user's ask): over a held queue at the idle line, <c>s</c> takes the front message off the
    /// queue and hands its line back, the pane closed and nothing said on the transcript (the line's own › row is the caller's).
    /// </summary>
    [Fact]
    public async Task OnAHeldQueue_TheSendKey_ReturnsTheFrontLine_TakesItOffTheQueue_AndClosesThePane()
    {
        Seed("one", "two");
        _queue.Held = true;
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;
        Push(Keys.Char('s'));

        var sent = await menu.ShowAsync(CancellationToken.None, offerSend: true);

        Assert.Equal("one", sent?.Text);
        Assert.Contains(Titled(SendStrip) + "\n \n▸ " + Row(0, "one") + "\n", _console.Output);
        Assert.Contains("\n" + QueueMenu.KeysWithSend + "\n", _console.Output);
        Assert.Equal(new[] { "two" }, Labels());
        Assert.True(_queue.Held);   // the hold is the reply's to release, as for a typed message
        Assert.False(pane.OverlayOpen);
        Assert.Equal(flow, pane.FlowRow);
        pane.Dispose();
    }

    [Fact]
    public async Task OnAHeldQueue_AClickOnTheSendButton_IsTheKey()
    {
        Seed("one", "two");
        _queue.Held = true;
        var (menu, pane, input) = ClickablePaneMenu(cursorTop: 100);
        input.PushClick(12, 100);                        // the strip row: the label, the gap, the send button from column 10

        var sent = await menu.ShowAsync(CancellationToken.None, offerSend: true);

        Assert.Equal("one", sent?.Text);
        Assert.Equal(new[] { "two" }, Labels());
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>No send over a queue that drains by itself, nor under a reply (not offered): the old strip, and <c>s</c> is nothing.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task WithoutAHold_OrUnderAReply_NoSendButton_AndTheKeyIsNothing(bool held, bool offerSend)
    {
        Seed("one", "two");
        _queue.Held = held;
        var (menu, pane) = PaneMenu();
        Push(Keys.Char('s'), Keys.Escape);

        var sent = await menu.ShowAsync(CancellationToken.None, offerSend);

        Assert.Null(sent);
        Assert.Contains(Titled(Strip) + "\n", _console.Output);
        Assert.DoesNotContain(QueueMenu.SendButton, _console.Output);
        Assert.Equal(new[] { "one", "two" }, Labels());
        pane.Dispose();
    }
}
