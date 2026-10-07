using NeonSidekick.App;
using NeonSidekick.Memory;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class MemoryMenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly TestConsole _console = new();
    private readonly MemoryStore _store;
    private readonly MemoryMenu _menu;

    public MemoryMenuTests()
    {
        _console.Interactive();
        _console.Profile.Width = 100;
        _store = new MemoryStore(_dir);
        _menu = new MemoryMenu(_console, _store, new TranscriptRenderer(_console), NoPane(_console));
    }

    /// <summary>A menu pane over a console without geometry: disabled, so the list is the Spectre prompt.</summary>
    private static MenuPane NoPane(IAnsiConsole console) =>
        new(new ScreenPane(console, null, new ManualTimeProvider()), new KeySource(console.Input, TimeSpan.FromMilliseconds(1)));

    /// <summary>The prompt host's title: the label and the keys joined.</summary>
    private static string PromptTitle => SettingsMenu.PromptTitle(MemoryMenu.Title, MemoryMenu.Keys);

    public void Dispose()
    {
        _console.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

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
            _store.Add(text);
        }
    }

    [Fact]
    public async Task Empty_SaysSo_AndOpensNoMenu()
    {
        await _menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  · " + MemoryMenu.EmptyNotice, _console.Output);
        Assert.DoesNotContain(PromptTitle, _console.Output);
    }

    [Fact]
    public async Task Escape_RemovesNothing()
    {
        Seed("one", "two");
        Push(Keys.Escape);

        await _menu.ShowAsync(CancellationToken.None);

        Assert.Contains(MemoryMenu.Title + "   Enter = remove · ESC = back", _console.Output);
        Assert.Contains("one", _console.Output);
        Assert.Contains("two", _console.Output);
        Assert.Equal(new[] { "one", "two" }, _store.Snapshot());
        Assert.DoesNotContain("(removed:", _console.Output);
    }

    [Fact]
    public async Task Enter_RemovesTheHighlightedRow_AndShowsTheListAgain()
    {
        Seed("one", "two", "three");
        Push(Keys.Down, Keys.Enter, Keys.Escape);

        await _menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  · (💾 removed: two)", _console.Output);
        Assert.Equal(new[] { "one", "three" }, _store.Snapshot());
        Assert.Equal(new[] { "one", "three" }, new MemoryStore(_dir).Snapshot());
        // The title was drawn twice: once before the removal and once after.
        Assert.True(_console.Output.Split(PromptTitle).Length - 1 >= 2);
    }

    [Fact]
    public async Task RemovingTheLastRow_EndsWithTheEmptyNotice()
    {
        Seed("only");
        Push(Keys.Enter);

        await _menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  · (💾 removed: only)", _console.Output);
        Assert.Contains("  · " + MemoryMenu.EmptyNotice, _console.Output);
        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public async Task TwoRemovals_KeepTheCursorWhereItWas()
    {
        Seed("one", "two", "three");
        Push(Keys.Down, Keys.Enter, Keys.Enter, Keys.Escape);   // remove "two", then the row now under the cursor: "three"

        await _menu.ShowAsync(CancellationToken.None);

        Assert.Equal(new[] { "one" }, _store.Snapshot());
        Assert.Contains("(💾 removed: two)", _console.Output);
        Assert.Contains("(💾 removed: three)", _console.Output);
    }

    [Fact]
    public async Task NonInteractiveConsole_PrintsTheNumberedList_AndRemovesNothing()
    {
        using var plain = new TestConsole();
        plain.Profile.Width = 100;
        Seed("one", "two");
        var menu = new MemoryMenu(plain, _store, new TranscriptRenderer(plain), NoPane(plain));

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  · 1. ", plain.Output);
        Assert.Contains("  one", plain.Output);
        Assert.Contains("  · 2. ", plain.Output);
        Assert.Contains("  two", plain.Output);
        Assert.DoesNotContain(PromptTitle, plain.Output);
        Assert.Equal(2, _store.Count);
    }

    // ── On the pane ─────────────────────────────────────────────────────────

    /// <summary>The menu over a pane with geometry: the list is a level of the pane, the removal notice its status line.</summary>
    private (MemoryMenu Menu, ScreenPane Pane) PaneMenu()
    {
        _console.Profile.Height = 40;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null), new ManualTimeProvider()) { Hint = () => "idle" };
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menu = new MemoryMenu(new ConsoleWithInput(pane, keys), _store, new TranscriptRenderer(pane), new MenuPane(pane, keys));
        pane.Show();
        return (menu, pane);
    }

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    /// <summary>A title or strip row as the pane prints it since 2026-09-18: the text, then the × close glyph in column width − 2.</summary>
    private static string Titled(string row, int width = 100) => row + new string(' ', width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    /// <summary>A row as the pane prints it: the date, two spaces, the text.</summary>
    private static string Row(MemoryEntry entry) => MemoryMenu.DateLabel(entry) + "  " + entry.Text;

    [Fact]
    public async Task OnThePane_TheListIsTheOverlay_AndEscapeClosesIt()
    {
        Seed("one", "two");
        var (menu, pane) = PaneMenu();
        var rows = _store.EntriesSnapshot().Select(Row).ToList();
        int flow = pane.FlowRow;
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(Rule(100) + "\n" + Titled(MemoryMenu.Title) + "\n \n▸ " + rows[0] + "\n  " + rows[1] + "\n" + Rule(100) + "\n" + MemoryMenu.Keys + "\n", _console.Output);
        Assert.DoesNotContain(PromptTitle, _console.Output);
        Assert.False(pane.OverlayOpen);
        Assert.Equal(flow, pane.FlowRow);   // nothing reached the transcript
        Assert.Equal(new[] { "one", "two" }, _store.Snapshot());
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_EnterRemoves_TheNoticeIsTheStatusLine_AndTheListReshows()
    {
        Seed("one", "two", "three");
        var (menu, pane) = PaneMenu();
        var rows = _store.EntriesSnapshot().Select(Row).ToList();
        int flow = pane.FlowRow;
        Push(Keys.Down, Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        // The re-shown list: the notice where the spacer was, the cursor on the row that slid up.
        Assert.Contains("\n" + Titled(MemoryMenu.Title) + "\n  · (💾 removed: two)\n  " + rows[0] + "\n▸ " + rows[2] + "\n" + Rule(100) + "\n" + MemoryMenu.Keys + "\n", _console.Output);
        Assert.Equal(flow, pane.FlowRow);   // the notice was a status line, not a transcript line
        Assert.False(pane.OverlayOpen);
        Assert.Equal(new[] { "one", "three" }, new MemoryStore(_dir).Snapshot());
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
        Assert.Contains("  · " + MemoryMenu.EmptyNotice + "\n", _console.Output);
        Assert.DoesNotContain("(💾 removed: only)", _console.Output);   // said to a status line the close forgot
        Assert.Equal(flow + 1, pane.FlowRow);
        Assert.Equal(0, _store.Count);
        pane.Dispose();
    }

    /// <summary>The menu over a pane with Memory mode's buttons wired (2026-10-03; three modes since 2026-10-04): the setting is <paramref name="mode"/>, each change recorded.</summary>
    private (MemoryMenu Menu, ScreenPane Pane, List<MemoryAccess> Switched) SwitchMenu(MemoryAccess mode)
    {
        _console.Profile.Height = 40;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null), new ManualTimeProvider()) { Hint = () => "idle" };
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var switched = new List<MemoryAccess>();
        var menu = new MemoryMenu(new ConsoleWithInput(pane, keys), _store, new TranscriptRenderer(pane), new MenuPane(pane, keys),
            () => mode, value => { mode = value; switched.Add(value); return true; });
        pane.Show();
        return (menu, pane, switched);
    }

    private static string SwitchTitle(MemoryAccess mode) => MemoryMenu.Title + " │ " + string.Join(" · ", MemoryMenu.Buttons(mode).Select(b => b.Title)) + " ";

    /// <summary>
    /// Memory mode's buttons on the title row (2026-10-03, the user's ask: ask/yolo's shape; three since 2026-10-04): D sets disabled
    /// and the list comes back, D again (the lit one) does nothing, R read-only, W read-write; the hint names the keys; the rows are untouched.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheTitleButtonsSetMemoryMode_TheLitOneDoesNothing()
    {
        Seed("one");
        var (menu, pane, switched) = SwitchMenu(MemoryAccess.ReadWrite);
        Push(Keys.Char('d'), Keys.Char('d'), Keys.Char('r'), Keys.Char('w'), Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([MemoryAccess.Disabled, MemoryAccess.ReadOnly, MemoryAccess.ReadWrite], switched);
        Assert.Contains("\n" + Titled(SwitchTitle(MemoryAccess.ReadWrite)) + "\n", _console.Output);
        Assert.Contains("\n" + Titled(SwitchTitle(MemoryAccess.ReadOnly)) + "\n", _console.Output);
        Assert.Contains("\n" + MemoryMenu.SwitchKeys + "\n", _console.Output);
        Assert.Equal(["one"], _store.Snapshot());
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>With the switch the pane opens with nothing remembered (2026-10-03): one dim row Enter does nothing on, and the last removal stays open on it.</summary>
    [Fact]
    public async Task OnThePane_WithTheSwitch_AnEmptyStoreOpens_AndTheLastRemovalStaysOpen()
    {
        Seed("only");
        var (menu, pane, switched) = SwitchMenu(MemoryAccess.Disabled);
        int flow = pane.FlowRow;
        Push(Keys.Enter, Keys.Enter, Keys.Char('w'), Keys.Escape);   // remove it, Enter on the empty row, read-write, close

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(0, _store.Count);
        Assert.Equal([MemoryAccess.ReadWrite], switched);
        Assert.Contains("\n" + Titled(SwitchTitle(MemoryAccess.Disabled)) + "\n  · (💾 removed: only)\n▸ " + MemoryMenu.EmptyNotice + "\n", _console.Output);
        Assert.Contains("\n" + MemoryMenu.EmptySwitchKeys + "\n", _console.Output);
        Assert.Equal(flow, pane.FlowRow);   // nothing reached the transcript
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task WithTheSwitch_ButNoPane_TheEmptyStoreStillSaysSo()
    {
        var menu = new MemoryMenu(_console, _store, new TranscriptRenderer(_console), NoPane(_console), () => MemoryAccess.ReadWrite, _ => true);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  · " + MemoryMenu.EmptyNotice, _console.Output);
    }

    [Fact]
    public void Labels_ArePinned()
    {
        var dated = new MemoryEntry { Text = "x [y]", SavedAt = new DateTimeOffset(2026, 9, 11, 18, 0, 0, TimeSpan.Zero) };
        var undated = new MemoryEntry { Text = "z" };

        Assert.Equal("💾 Memory", MemoryMenu.Title);
        Assert.Equal(MemoryMenu.Title + "   Enter = remove · ESC = back", SettingsMenu.PromptTitle(MemoryMenu.Title, MemoryMenu.Keys));
        Assert.Equal("(💾 nothing remembered)", MemoryMenu.EmptyNotice);
        Assert.Equal("Enter = remove · W = read-write · R = read-only · D = disabled · ESC = back", MemoryMenu.SwitchKeys);   // 2026-10-03; Memory mode's three since 2026-10-04
        Assert.Equal("W = read-write · R = read-only · D = disabled · ESC = back", MemoryMenu.EmptySwitchKeys);
        Assert.Equal(["● read-write", "○ read-only", "○ disabled"], MemoryMenu.Buttons(MemoryAccess.ReadWrite).Select(b => b.Title));
        Assert.Equal(["○ read-write", "● read-only", "○ disabled"], MemoryMenu.Buttons(MemoryAccess.ReadOnly).Select(b => b.Title));
        Assert.Equal([true, false, false], MemoryMenu.Buttons(MemoryAccess.ReadWrite).Select(b => b.On));
        Assert.Equal([false, false, true], MemoryMenu.Buttons(MemoryAccess.Disabled).Select(b => b.On));
        Assert.Equal(['w', 'r', 'd'], MemoryMenu.Buttons(MemoryAccess.Disabled).Select(b => b.Key!.Value));
        Assert.Equal("(💾 removed: x)", MemoryMenu.RemovedNotice("x"));
        Assert.Equal("Could not remove the memory: locked", MemoryMenu.RemoveFailedError("locked"));
        Assert.Equal("2026-09-11", MemoryMenu.DateLabel(dated));
        Assert.Equal("----------", MemoryMenu.DateLabel(undated));
        Assert.Equal("[#9A8BB8]2026-09-11[/]  x [[y]]", MemoryMenu.RowMarkup(dated));
        Assert.Equal(new[] { "1. 2026-09-11  x [y]", "2. ----------  z" }, MemoryMenu.ListLines(new[] { dated, undated }));
    }
}
