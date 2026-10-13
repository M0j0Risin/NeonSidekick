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
        Assert.Equal(new MenuButton("⊠ clear all", 'c'), Assert.Single(QueueMenu.Buttons(send: false, null)));
        Assert.Equal('s', QueueMenu.SendKey);
        Assert.Equal([new MenuButton("➤ send", 's'), new MenuButton("⊠ clear all", 'c')], QueueMenu.Buttons(send: true, null));
        // The mode buttons (2026-10-13, the user's ask): between send and clear all, the words in force; alone over the empty queue.
        Assert.Equal([new MenuButton("➤ send", 's'), new MenuButton("mode: manual", 'm'), new MenuButton("cancel: hold", 'k'), new MenuButton("⊠ clear all", 'c')], QueueMenu.Buttons(send: true, (Queueing.Manual, QueueCancel.Hold)));
        Assert.Equal([new MenuButton("mode: automatic", 'm'), new MenuButton("cancel: empty", 'k')], QueueMenu.Buttons(send: false, (Queueing.Automatic, QueueCancel.Empty), empty: true));
        Assert.Equal("Enter = remove · s = send · m = mode · k = cancel mode · c = clear all · ESC = close", QueueMenu.KeysWithSend);
        Assert.Equal("(⏳ nothing queued)", QueueMenu.EmptyNotice);
        Assert.Equal("(⏳ removed: and then?)", QueueMenu.RemovedNotice("and then?"));
        Assert.Equal("[#9A8BB8]1[/]  a [[b]]", QueueMenu.RowMarkup(0, "a [b]"));
        Assert.Equal("[#9A8BB8]12[/]  x", QueueMenu.RowMarkup(11, "x"));
    }

    [Theory]
    [InlineData(Queueing.Automatic, Queueing.Manual)]
    [InlineData(Queueing.Manual, Queueing.Disabled)]
    [InlineData(Queueing.Disabled, Queueing.Automatic)]
    public void NextMode_StepsInTheMenusOrder_AndWraps(Queueing from, Queueing to) => Assert.Equal(to, QueueMenu.NextMode(from));

    [Theory]
    [InlineData(QueueCancel.Hold, QueueCancel.Drain)]
    [InlineData(QueueCancel.Drain, QueueCancel.Empty)]
    [InlineData(QueueCancel.Empty, QueueCancel.Hold)]
    public void NextCancel_StepsInTheMenusOrder_AndWraps(QueueCancel from, QueueCancel to) => Assert.Equal(to, QueueMenu.NextCancel(from));

    /// <summary>The menu over a pane with the mode buttons wired (2026-10-13) to <paramref name="modes"/>, each press recorded there.</summary>
    private (QueueMenu Menu, ScreenPane Pane) ModesPaneMenu(List<string> modes)
    {
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null), _time) { Hint = () => "idle" };
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var mode = Queueing.Automatic;
        var cancel = QueueCancel.Empty;
        var menu = new QueueMenu(_queue, new TranscriptRenderer(pane), new MenuPane(pane, keys),
            () => mode, next => { mode = next; modes.Add(QueueMode.Name(next)); return true; },
            () => cancel, next => { cancel = next; modes.Add(QueueCancelMode.Name(next)); return true; });
        pane.Show();
        return (menu, pane);
    }

    [Fact]
    public async Task OnThePane_TheModeButtons_StepEachSetting_AndTheListShowsAgain()
    {
        // The two buttons (2026-10-13, the user's ask): m steps Queue mode, k Queue cancel mode, each wrapping; the list stays up, its messages kept.
        Seed("one", "two");
        var modes = new List<string>();
        var (menu, pane) = ModesPaneMenu(modes);
        Push(Keys.Char('m'), Keys.Char('m'), Keys.Char('m'), Keys.Char('k'), Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["manual", "disabled", "automatic", "hold"], modes);
        Assert.Contains("mode: disabled", _console.Output);
        Assert.Contains("cancel: hold", _console.Output);
        Assert.Contains(QueueMenu.Keys, _console.Output);
        Assert.Equal(new[] { "one", "two" }, Labels());
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task OnTheEmptyPane_TheModeButtons_AreOffered_AndEnterIsNothing()
    {
        // The empty queue's pane carries the buttons too (2026-10-13): no clear all, Enter on the dim row nothing, ESC closes.
        var modes = new List<string>();
        var (menu, pane) = ModesPaneMenu(modes);
        Push(Keys.Enter, Keys.Char('k'), Keys.Char('c'), Keys.Escape);

        Assert.Null(await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(["hold"], modes);   // c is no button here
        Assert.Contains("▸ " + QueueMenu.EmptyNotice + "\n", _console.Output);
        Assert.Contains("\n" + QueueMenu.EmptyKeys + "\n", _console.Output);
        Assert.Contains("mode: automatic", _console.Output);
        Assert.Contains("cancel: hold", _console.Output);
        Assert.DoesNotContain(QueueMenu.ClearAllButton, _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
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
    public async Task Empty_OpensThePane_OnItsOneDimRow_AndEscapeClosesIt()
    {
        // The pane opens on an empty queue too (2026-10-07, the consistency pass), its notice the one row.
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(QueueMenu.Title, _console.Output);
        Assert.Contains("\n" + MenuPane.EmptyKeys + "\n", _console.Output);
        Assert.Contains("▸ " + QueueMenu.EmptyNotice + "\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        Assert.Equal(flow, pane.FlowRow);   // nothing said to the transcript
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

        Assert.Contains(Rule(100) + "\n" + Titled(Strip) + "\n \n▸ " + Row(0, "one") + "\n  " + Row(1, "two") + "\n" + MenuLayout.Footer(new MenuFooter("one", "1 of 2"), 100) + Rule(100) + "\n" + QueueMenu.Keys + "\n", _console.Output);   // the message whole under the list (2026-10-07)
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
        Push(Keys.Down, Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the row, then Yes (asked since 2026-10-07)

        await menu.ShowAsync(CancellationToken.None);

        // The re-shown list: the notice where the spacer was, the cursor on the row that slid up, the numbers fresh.
        Assert.Contains("\n" + Titled(Strip) + "\n  · (⏳ removed: two)\n  " + Row(0, "one") + "\n▸ " + Row(1, "three") + "\n" + MenuLayout.Footer(new MenuFooter("three", "2 of 2"), 100) + Rule(100) + "\n" + QueueMenu.Keys + "\n", _console.Output);
        Assert.Equal(flow, pane.FlowRow);   // the notice was a status line, not a transcript line
        Assert.False(pane.OverlayOpen);
        Assert.Equal(new[] { "one", "three" }, Labels());
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheRemoveAsks_AndNoKeepsTheMessage()
    {
        Seed("one", "two");
        var (menu, pane) = PaneMenu();
        Push(Keys.Down, Keys.Enter, Keys.Enter, Keys.Escape);   // the row, then No (the cursor starts on it), then close

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(QueueMenu.RemovePrompt("two"), _console.Output);
        Assert.Contains(ChatScreen.KeptNotice, _console.Output);
        Assert.Contains("\n▸ " + Row(1, "two") + "\n", _console.Output);   // the list again, the cursor kept
        Assert.Equal(new[] { "one", "two" }, Labels());
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_RemovingTheLastRow_ClosesThePane_ThenTheEmptyNoticeGoesToTheTranscript()
    {
        Seed("only");
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;
        Push(Keys.Enter, Keys.Down, Keys.Enter);   // the row, then Yes

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
        input.PushClick(9, 104);                         // again, within the interval: the pair asks to remove it
        input.Push(Keys.Down, Keys.Enter, Keys.Escape);  // Yes, then close

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

        var sent = await menu.ShowAsync(CancellationToken.None, () => _queue.Held);

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

        var sent = await menu.ShowAsync(CancellationToken.None, () => _queue.Held);

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

        var sent = await menu.ShowAsync(CancellationToken.None, () => offerSend && _queue.Held);

        Assert.Null(sent);
        Assert.Contains(Titled(Strip) + "\n", _console.Output);
        Assert.DoesNotContain(QueueMenu.SendButton, _console.Output);
        Assert.Equal(new[] { "one", "two" }, Labels());
        pane.Dispose();
    }

    /// <summary>
    /// Queue mode manual (2026-10-09, the user's ask): the caller's say-so offers the send with no hold at all, and its press takes
    /// the front message alone — the next stays for the next press.
    /// </summary>
    [Fact]
    public async Task TheCallersSaySo_OffersTheSend_WithoutAHold_AndOnePressTakesOneMessage()
    {
        Seed("one", "two");
        var (menu, pane) = PaneMenu();
        Push(Keys.Char('s'));

        var sent = await menu.ShowAsync(CancellationToken.None, () => true);

        Assert.Equal("one", sent?.Text);
        Assert.Contains(Titled(SendStrip) + "\n", _console.Output);
        Assert.Equal(new[] { "two" }, Labels());
        Assert.False(_queue.Held);
        pane.Dispose();
    }

    /// <summary>The say-so is asked at each showing of the list (2026-10-09): a send that stops being allowed after a removal leaves the strip.</summary>
    [Fact]
    public async Task TheCallersSaySo_IsAskedAtEachShowing()
    {
        Seed("one", "two");
        var (menu, pane) = PaneMenu();
        int asked = 0;
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // remove "one" (then Yes), the list again, then close

        var sent = await menu.ShowAsync(CancellationToken.None, () => ++asked == 1);

        Assert.Null(sent);
        Assert.Equal(2, asked);
        Assert.Equal(new[] { "two" }, Labels());
        pane.Dispose();
    }
}
