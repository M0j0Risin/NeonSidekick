using NeonSidekick.App;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class SessionsMenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly TestConsole _console = new();
    private readonly ManualTimeProvider _time = new();
    private readonly SessionStore _store;
    private readonly List<long> _purged = new();
    private long? _current;

    public SessionsMenuTests()
    {
        _console.Interactive();
        _console.Profile.Width = 100;
        _store = new SessionStore(_dir, _time);
    }

    public void Dispose()
    {
        _store.Dispose();
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

    private long Seed(string title, int turns = 1)
    {
        long id = _store.Begin(title, "llama")!.Value;
        for (int i = 0; i < turns; i++)
        {
            _store.AppendTurn(id, "q" + i, "r" + i, 0, [], [], 0, 1, 1, false);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        return id;
    }

    /// <summary>The menu over a console without geometry: the pane is disabled, so the list prints as lines.</summary>
    private SessionsMenu NoPaneMenu()
    {
        var pane = new ScreenPane(_console, null, _time);
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        return new SessionsMenu(_store, () => _current, new TranscriptRenderer(_console), new MenuPane(pane, keys), new InputLine(pane, keys), _time, _purged.Add);
    }

    /// <summary>The menu over a pane with geometry: the list is a level of the pane, every notice its status line.</summary>
    private (SessionsMenu Menu, ScreenPane Pane) PaneMenu()
    {
        _console.Profile.Height = 40;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null), _time) { Hint = () => "idle" };
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menu = new SessionsMenu(_store, () => _current, new TranscriptRenderer(pane), new MenuPane(pane, keys), new InputLine(pane, keys), _time, _purged.Add);
        pane.Show();
        return (menu, pane);
    }

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    private static string Titled(string row, int width = 100) => row + new string(' ', width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    /// <summary>A list row as the pane prints it: the id and the count padded to the list's widths, the moment, two spaces, the title.</summary>
    private string Row(long id, string moment, int turns, string title, bool current = false, int idWidth = 0, int turnsWidth = 0) =>
        ("#" + id).PadRight(idWidth) + "  " + moment + "  " + SessionText.Turns(turns).PadRight(turnsWidth) + "  " + title + (current ? "  " + SessionsMenu.CurrentNote : "");

    [Fact]
    public async Task Empty_SaysSo_AndOpensNothing()
    {
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;

        Assert.Null(await menu.ShowAsync(CancellationToken.None));

        Assert.Contains("  · " + SessionsMenu.EmptyNotice, _console.Output);
        Assert.False(pane.OverlayOpen);
        Assert.Equal(flow + 1, pane.FlowRow);
        pane.Dispose();
    }

    [Fact]
    public async Task WithoutThePane_TheListPrints_AndNothingElse()
    {
        long a = Seed("first");
        long b = Seed("second");
        _current = b;
        var menu = NoPaneMenu();

        Assert.Null(await menu.ShowAsync(CancellationToken.None));

        Assert.Contains("  · 1. #" + b + " · 2026-09-11 14:06 · 1 turn · second (this conversation)", _console.Output);
        Assert.Contains("  · 2. #" + a + " · 2026-09-11 14:05 · 1 turn · first", _console.Output);
        Assert.DoesNotContain(SessionsMenu.Keys, _console.Output);
    }

    [Fact]
    public async Task OnThePane_TheListIsTheOverlay_NewestFirst_TheCurrentMarked_AndEscapeClosesIt()
    {
        long a = Seed("first", 3);
        long b = Seed("second");
        _current = b;
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;
        Push(Keys.Escape);

        Assert.Null(await menu.ShowAsync(CancellationToken.None));

        // The columns line up: `1 turn` padded to `3 turns`' width (2026-09-18, the user's ask).
        Assert.Contains(Rule(100) + "\n" + Titled(SessionsMenu.Title) + "\n \n▸ " + Row(b, "2026-09-11 14:06", 1, "second", current: true, turnsWidth: 7) + "\n  " + Row(a, "2026-09-11 14:05", 3, "first") + "\n" + Rule(100) + "\n" + SessionsMenu.Keys + "\n", _console.Output);
        Assert.Contains("\n▸ #" + b + "  2026-09-11 14:06  1 turn   second  " + SessionsMenu.CurrentNote + "\n  #" + a + "  2026-09-11 14:05  3 turns  first\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        Assert.Equal(flow, pane.FlowRow);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_EnterOpensTheRowPage_AndRestoreHandsTheIdBack()
    {
        long a = Seed("first");
        long b = Seed("second");
        var (menu, pane) = PaneMenu();
        Push(Keys.Down, Keys.Enter);   // first's row page
        Push(Keys.Enter);              // restore

        Assert.Equal(a, await menu.ShowAsync(CancellationToken.None));

        Assert.Contains("\n" + Titled(SessionsMenu.RowTitle(_store.Load(a)!.Summary)) + "\n \n▸ restore  load it into the transcript and go on from there\n  rename   give it a new title\n  purge    remove it and its turns for good\n" + Rule(100) + "\n" + SessionsMenu.RowKeys + "\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        Assert.Equal(b, _store.List(0)[0].Id);   // nothing changed in the store
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_RestoreOfTheCurrent_IsANotice_AndEscBacksOutOfTheRowPage()
    {
        long a = Seed("first");
        _current = a;
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Enter);   // the row page, restore
        Push(Keys.Enter, Keys.Escape);  // the row page again, back to the list
        Push(Keys.Escape);

        Assert.Null(await menu.ShowAsync(CancellationToken.None));

        Assert.Contains("\n" + Titled(SessionsMenu.Title) + "\n  · " + SessionsMenu.CurrentNotice + "\n▸ " + Row(a, "2026-09-11 14:05", 1, "first", current: true) + "\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_RenameReadsTheTitleInTheSlot_AndTheListReshowsIt()
    {
        long a = Seed("first");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Down, Keys.Enter);   // the row page, rename
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("Vosk notes");
        Push(Keys.Enter, Keys.Escape);

        Assert.Null(await menu.ShowAsync(CancellationToken.None));

        // The slot under the row page with the edit keys (the pre-filled title is typed into it in place, not part of the draw).
        Assert.Contains("\n▸ rename   give it a new title\n  purge    remove it and its turns for good\n› \n" + Rule(100) + "\n" + SettingsMenu.EditKeys, _console.Output);
        Assert.Contains("\n" + Titled(SessionsMenu.Title) + "\n  · " + SessionsMenu.RenamedNotice("Vosk notes") + "\n▸ " + Row(a, "2026-09-11 14:05", 1, "Vosk notes") + "\n", _console.Output);
        var summary = _store.Load(a)!.Summary;
        Assert.Equal(("Vosk notes", TitleSource.User), (summary.Title, summary.TitleSource));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_RenameEscKeepsTheTitle()
    {
        long a = Seed("first");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Null(await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("first", _store.Load(a)!.Summary.Title);
        Assert.DoesNotContain("(renamed:", _console.Output);
        pane.Dispose();
    }

    /// <summary>
    /// The rename box alone (2026-09-28, the upper rule's session name double-clicked): the row page's rename row on its own,
    /// the title in the slot; a new one is stored as the user's, the pane closes and the notice lands in the transcript.
    /// </summary>
    [Fact]
    public async Task Rename_Alone_ReadsTheTitleInTheSlot_AndClosesThePane()
    {
        long a = Seed("first");
        var (menu, pane) = PaneMenu();
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("Vosk notes");
        Push(Keys.Enter);

        Assert.True(await menu.RenameAsync(a, CancellationToken.None));

        Assert.Contains("\n" + Titled(SessionsMenu.RowTitle(new SessionSummary(a, default, default, "first", TitleSource.FirstLine, "llama", 1))) + "\n \n▸ rename   give it a new title\n› \n" + Rule(100) + "\n" + SettingsMenu.EditKeys, _console.Output);
        Assert.DoesNotContain("restore  load it", _console.Output);
        Assert.Contains("  · " + SessionsMenu.RenamedNotice("Vosk notes"), _console.Output);
        Assert.False(pane.OverlayOpen);
        var summary = _store.Load(a)!.Summary;
        Assert.Equal(("Vosk notes", TitleSource.User), (summary.Title, summary.TitleSource));
        pane.Dispose();
    }

    [Fact]
    public async Task Rename_Alone_EscKeepsTheTitle_AndClosesThePane()
    {
        long a = Seed("first");
        var (menu, pane) = PaneMenu();
        Push(Keys.Escape);

        Assert.False(await menu.RenameAsync(a, CancellationToken.None));

        Assert.Equal(("first", TitleSource.FirstLine), (_store.Load(a)!.Summary.Title, _store.Load(a)!.Summary.TitleSource));
        Assert.DoesNotContain("(renamed:", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task Rename_Alone_OfASessionGone_IsTheError_AndOpensNothing()
    {
        long a = Seed("first");
        Assert.True(_store.Purge(a));
        var (menu, pane) = PaneMenu();

        Assert.False(await menu.RenameAsync(a, CancellationToken.None));

        Assert.Contains("  ✗ " + SessionsMenu.RenameFailedError(a), _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_PurgeAsksUnderTheList_YesRemoves_TheScreenIsTold()
    {
        long a = Seed("first");
        long b = Seed("second");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.End, Keys.Enter);   // second's row page, purge
        Push(Keys.Down, Keys.Enter);              // Yes
        Push(Keys.Escape);

        Assert.Null(await menu.ShowAsync(CancellationToken.None));

        Assert.Contains("\n" + Titled(SessionsMenu.PurgePrompt(new SessionSummary(b, _time.GetUtcNow(), _time.GetUtcNow(), "second", TitleSource.FirstLine, "llama", 1))) + "\n \n▸ No\n  Yes\n" + Rule(100) + "\n" + SettingsMenu.ConfirmKeys, _console.Output);
        Assert.Contains("\n" + Titled(SessionsMenu.Title) + "\n  · " + SessionsMenu.PurgedNotice(b) + "\n▸ " + Row(a, "2026-09-11 14:05", 1, "first") + "\n", _console.Output);
        Assert.Equal([b], _purged);
        Assert.Null(_store.Load(b));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_PurgeNo_Keeps_AndPurgingTheLastRow_ClosesWithTheEmptyNotice()
    {
        long a = Seed("only");
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;
        Push(Keys.Enter, Keys.End, Keys.Enter, Keys.Enter);   // purge → No
        Push(Keys.Enter, Keys.End, Keys.Enter, Keys.Char('y'), Keys.Enter);   // purge → y, Yes

        Assert.Null(await menu.ShowAsync(CancellationToken.None));

        Assert.Contains("\n  · " + SessionsMenu.KeptNotice + "\n▸ " + Row(a, "2026-09-11 14:05", 1, "only") + "\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        Assert.Contains("  · " + SessionsMenu.EmptyNotice + "\n", _console.Output);
        Assert.Equal(flow + 1, pane.FlowRow);
        Assert.Equal(0, _store.Count);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_MidTurn_TheListShows_AndAPickIsRefused()
    {
        long a = Seed("first");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Escape);

        Assert.Null(await menu.ShowAsync(CancellationToken.None, midTurn: true));

        Assert.Contains("\n" + Titled(SessionsMenu.Title) + "\n  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n▸ " + Row(a, "2026-09-11 14:05", 1, "first") + "\n", _console.Output);
        Assert.DoesNotContain(SessionsMenu.RowKeys, _console.Output);
        pane.Dispose();
    }

    [Fact]
    public void Labels_ArePinned()
    {
        var summary = new SessionSummary(12, ManualTimeProvider.DefaultUtcNow, ManualTimeProvider.DefaultUtcNow, "x [y]", TitleSource.FirstLine, "llama", 12);

        Assert.Equal("💬 Sessions", SessionsMenu.Title);
        Assert.Equal(SettingsMenu.PickKeys, SessionsMenu.RowKeys);
        Assert.Equal("(💬 no sessions)", SessionsMenu.EmptyNotice);
        Assert.Equal("(💬 that is this conversation)", SessionsMenu.CurrentNotice);
        Assert.Equal(new[] { "restore", "rename", "purge" }, SessionsMenu.RowWords);
        Assert.Equal(SessionsMenu.Title + " › #12 x [y]", SessionsMenu.RowTitle(summary));
        Assert.Equal("[#9A8BB8]#12  2026-09-11 14:05  12 turns[/]  x [[y]]", SessionsMenu.RowMarkup(summary, false, ManualTimeProvider.DefaultZone));
        Assert.Equal("[#9A8BB8]#12  2026-09-11 14:05  12 turns[/]  x [[y]]  [#9A8BB8]this conversation[/]", SessionsMenu.RowMarkup(summary, true, ManualTimeProvider.DefaultZone));
        // The widths pad the id and the count (the widest of the list), so a one-digit id and `1 turn` line up.
        var one = new SessionSummary(3, ManualTimeProvider.DefaultUtcNow, ManualTimeProvider.DefaultUtcNow, "one", TitleSource.FirstLine, "llama", 1);
        Assert.Equal((3, 8), SessionsMenu.Widths([summary, one]));
        Assert.Equal((0, 0), SessionsMenu.Widths([]));
        Assert.Equal("[#9A8BB8]#3   2026-09-11 14:05  1 turn  [/]  one", SessionsMenu.RowMarkup(one, false, ManualTimeProvider.DefaultZone, 3, 8));
        Assert.Equal("[#9A8BB8]#12  2026-09-11 14:05  12 turns[/]  x [[y]]", SessionsMenu.RowMarkup(summary, false, ManualTimeProvider.DefaultZone, 3, 8));
        Assert.Equal("restore  [#9A8BB8]load it into the transcript and go on from there[/]", SessionsMenu.RowPageRow("restore"));
        Assert.Equal("rename   [#9A8BB8]give it a new title[/]", SessionsMenu.RowPageRow("rename"));
        Assert.Equal("purge    [#9A8BB8]remove it and its turns for good[/]", SessionsMenu.RowPageRow("purge"));
        Assert.Equal("💬 Purge session #12 \"x [y]\" (12 turns)?", SessionsMenu.PurgePrompt(summary));
        Assert.Equal("(🗑️ purged session #12)", SessionsMenu.PurgedNotice(12));
        Assert.Equal("Could not purge session #12; it may be gone already", SessionsMenu.PurgeFailedError(12));
        Assert.Equal("(💬 renamed: New)", SessionsMenu.RenamedNotice("New"));
        Assert.Equal("Could not rename session #12; it may be gone already", SessionsMenu.RenameFailedError(12));
        Assert.Equal(ChatScreen.KeptNotice, SessionsMenu.KeptNotice);
        Assert.Equal(new[] { "1. #12 · 2026-09-11 14:05 · 12 turns · x [y] (this conversation)" }, SessionsMenu.ListLines([summary], 12, ManualTimeProvider.DefaultZone));
    }
}
