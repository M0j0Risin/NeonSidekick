using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.Web;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// The Browser window's saved links on the screen (later on 2026-10-13, the user's ask): <c>/browser save</c>, <c>list</c> (its pane and
/// the toolbar's ⭐), <c>list --clear</c>, <c>unsave</c> and <c>/browser &lt;n&gt;</c>, one close notice for a visit opened twice, and
/// <c>/youtube list</c>'s reordering beside them.
/// </summary>
public sealed partial class ChatScreenTests
{
    private BrowserLinks SavedLinks() => new(_settings.ProfileDirectory);

    /// <summary>
    /// <c>/profile delete</c> with browser data the window could not remove (2026-10-20, the code review's C5: a Mac's WebKit store, found only
    /// by the id in the profile's folder): the profile is kept, and the transcript says why.
    /// </summary>
    [Fact]
    public async Task Profile_Delete_KeepsTheProfile_WhenItsBrowserDataCouldNotGo()
    {
        _browserWindow = new FakeBrowserWindow { ForgetAnswer = BrowserText.MacStoreNotRemoved(BrowserText.MacRemoveTimedOut) };
        WorkProfile();
        PushLine("/profile delete work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Single(_browserWindow.Forgotten);
        Assert.True(Directory.Exists(ProfileDir("work")));
        Assert.Contains(ChatScreen.ProfileDeleteFailedError(BrowserText.MacStoreNotRemoved(BrowserText.MacRemoveTimedOut))[..60], output);
        Assert.DoesNotContain(ChatScreen.ProfileDeletedNotice("work"), output);
    }

    /// <summary>Save by address (a name after it) and from the window's page (its title the name), open by number, a number past the list, unsave.</summary>
    [Fact]
    public async Task BrowserLinks_SaveOpenAndUnsave_FromTheCommandLine()
    {
        _browserWindow = new FakeBrowserWindow { Current = ("https://page.example/", "The Page") };
        PushLine("/browser save example.com Example");
        PushLine("/browser save");
        PushLine("/browser save example.com New name");
        PushLine("/browser 2");
        PushLine("/browser 9");
        PushLine("/browser unsave 1");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("Saved \"Example\" (https://example.com/).", output);
        Assert.Contains("Saved \"The Page\" (https://page.example/).", output);
        Assert.Contains("https://example.com/ is saved already, as \"Example\"; ✎ name on /browser list renames it.", output);
        Assert.Equal([new Uri("https://page.example/")], _browserWindow.Browses);
        Assert.Contains(BotCheckText.Opened("https://page.example/"), output);
        Assert.Contains("There is no saved link 9: the list runs 1 to 2.", output);
        Assert.Contains("Removed \"Example\" from the saved links.", output);
        Assert.Equal(["https://page.example/"], SavedLinks().List().Select(l => l.Url));
        Assert.Equal(4, _browserWindow.LinksChangedCount);   // three saves and the unsave
    }

    /// <summary>A bare save with no web page in the window, or a window that never answers (the screen's own 2 s cap), says so.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserLinks_ABareSave_WithNoPage_SaysSo(bool hold)
    {
        _browserWindow = new FakeBrowserWindow { CurrentHold = hold };
        var window = _browserWindow;
        if (hold)
        {
            // The window never answers: the screen's wait runs out on the fake clock once the page was asked for.
            _ = Task.Run(async () =>
            {
                while (window.CurrentAsks == 0)
                {
                    await Task.Delay(20);
                }

                await Task.Delay(100);
                _time.Advance(NeonSidekick.Viewer.BrowserLinks.CurrentPageWait + TimeSpan.FromSeconds(1));
            });
        }

        PushLine("/browser save");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BrowserLinkText.NothingToSave, output);
        Assert.Empty(SavedLinks().List());
    }

    /// <summary>A bare save while a bot check waits in the window (2026-10-14, the review): the check's page is never saved.</summary>
    [Fact]
    public async Task BrowserLinks_ABareSave_WhileABotCheckWaits_SavesNothing()
    {
        _browserWindow = new FakeBrowserWindow { Current = ("https://example.com/", "Just a moment..."), UnavailableReason = BrowserText.BotCheckWaiting };
        PushLine("/browser save");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BrowserLinkText.NothingToSave, output);
        Assert.Empty(SavedLinks().List());
        Assert.Equal(0, _browserWindow.CurrentAsks);
    }

    /// <summary>
    /// Off Windows (no Browser window) the list still works, and <c>/browser 1</c> opens the saved link in the default browser through
    /// open_url's opener (2026-10-17, the Mac pass, the user's call: it said there was no window until then); the printed caption names
    /// no window. The pane's Enter: <see cref="BrowserLinks_ThePaneWithNoWindow_EnterOpensInTheDefaultBrowser"/>. Bare <c>/browser</c>
    /// still says there is none (<see cref="BrowserCommand_WithNoWindow_SaysThereIsNone"/>).
    /// </summary>
    [Fact]
    public async Task BrowserLinks_WithNoWindow_TheListWorks_OpeningGoesToTheDefaultBrowser()
    {
        PushLine("/browser save example.com Ex");
        PushLine("/browser list");
        _console.Input.PushKey(Keys.Enter);   // the pane: open
        _console.Input.PushKey(Keys.Escape);
        PushLine("/browser 1");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("Saved \"Ex\" (https://example.com/).", output);
        Assert.Contains(BrowserLinkText.Caption(1, window: false), output);
        Assert.DoesNotContain(BrowserLinkText.Caption(1), output);
        Assert.DoesNotContain(BotCheckText.BrowserNotHere, output);
        Assert.Equal(["https://example.com/"], _openedUrls);   // /browser 1; with no pane the list is printed and the Enter is an empty line
        Assert.Contains(WebText.OpenedOne("https://example.com/"), output);   // /browser 1's line on the transcript
        Assert.Single(SavedLinks().List());
    }

    /// <summary>
    /// The pane with no Browser window (2026-10-17, the Mac pass, the user's call): Enter opens the highlighted link in the default
    /// browser, the caption and nothing else naming a window; the pane stays open as with one.
    /// </summary>
    [Fact]
    public async Task BrowserLinks_ThePaneWithNoWindow_EnterOpensInTheDefaultBrowser()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        SavedLinks().Add("https://a.example/", "A", out _);
        PushLine("/browser list");
        _console.Input.PushKey(Keys.Enter);   // open A
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["https://a.example/"], _openedUrls);
        Assert.Contains(BrowserLinkText.Caption(1, window: false), output);
        Assert.DoesNotContain(BotCheckText.BrowserNotHere, output);
        Assert.DoesNotContain(BrowserLinkText.Caption(1), output);
    }

    /// <summary>
    /// The pane: Enter opens the highlighted link (the pane kept, its answer on the status line, nothing on the transcript), Shift+↑ moves
    /// one, n renames, a adds a typed address, e changes one, d removes after a yes; each change tells the window.
    /// </summary>
    [Fact]
    public async Task BrowserLinks_ThePane_OpensMovesRenamesAddsReaddressesAndRemoves()
    {
        _browserWindow = new FakeBrowserWindow();
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        var links = SavedLinks();
        links.Add("https://a.example/", "A", out _);
        links.Add("https://b.example/", "B", out _);
        PushLine("/browser list");
        _console.Input.PushKey(Keys.Enter);                                                           // open A
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, true, false, false));     // B to the top
        _console.Input.PushKey(Keys.Char('n'));                                                       // rename B
        _console.Input.PushKey(Keys.Backspace);
        foreach (char c in "Bee")
        {
            _console.Input.PushKey(Keys.Char(c));
        }

        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Char('a'));                                                       // add c.example
        foreach (char c in "c.example")
        {
            _console.Input.PushKey(Keys.Char(c));
        }

        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Char('e'));                                                       // c's address: d.example
        for (int i = 0; i < "https://c.example/".Length; i++)
        {
            _console.Input.PushKey(Keys.Backspace);
        }

        foreach (char c in "d.example")
        {
            _console.Input.PushKey(Keys.Char(c));
        }

        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Char('d'));                                                       // remove d, after a yes
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([new Uri("https://a.example/")], _browserWindow.Browses);
        Assert.Contains(BrowserLinkText.PaneTitle, output);
        Assert.Contains(BrowserLinkText.PaneKeys, output);
        Assert.Contains(BotCheckText.Opened("https://a.example/"), output);   // on the pane's status line
        Assert.Contains("Renamed to \"Bee\".", output);
        Assert.Contains("Saved https://c.example/.", output);
        Assert.Contains("https://c.example/ goes to https://d.example/ now.", output);
        Assert.Contains(BrowserLinkText.RemovePrompt(new BrowserLink("https://d.example/", null, default)), output);
        Assert.Equal([("https://b.example/", "Bee"), ("https://a.example/", "A")], SavedLinks().List().Select(l => (l.Url, l.Name)));
        Assert.Equal(5, _browserWindow.LinksChangedCount);   // the move, the rename, the add, the address, the remove
    }

    /// <summary><c>/browser list --clear</c>: a yes/no first; No keeps the list, Yes empties it, and an empty list asks nothing.</summary>
    [Fact]
    public async Task BrowserLinks_ListClear_AsksFirst_ThenEmptiesTheList()
    {
        _browserWindow = new FakeBrowserWindow();
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var links = SavedLinks();
        links.Add("https://a.example/", "A", out _);
        links.Add("https://b.example/", "B", out _);
        PushLine("/browser list --clear");
        _console.Input.PushKey(Keys.Enter);                // No
        PushLine("/browser list --clear");
        _console.Input.PushKey(Keys.Down);                 // Yes
        _console.Input.PushKey(Keys.Enter);
        PushLine("/browser list --clear");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BrowserLinkText.ClearPrompt(2), output);
        Assert.Contains("  · " + ChatScreen.KeptNotice, output);
        Assert.Contains("  · " + BrowserLinkText.Cleared(2), output);
        Assert.Contains("  · " + BrowserLinkText.NoneSaved, output);
        Assert.Empty(SavedLinks().List());
    }

    /// <summary>The toolbar's ⭐ (later on 2026-10-13, the user's ask): a double-click opens the saved links, as <c>/browser list</c>.</summary>
    [Fact]
    public async Task BrowserLinks_TheToolbarsItem_OpensTheSavedLinks()
    {
        _browserWindow = new FakeBrowserWindow();
        SavedLinks().Add("https://example.com/", "Example", out _);
        _settings.Update(d => d.ToolbarItems = [ToolbarItems.BrowserLinks]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);   // an empty line: row 100, the rule 101, the hint row 102, the toolbar 103
        StepsWhenIdle(
            input => { input.PushClick(0, 103); input.PushClick(0, 103); },   // ⭐
            Key(Keys.Enter),
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(BrowserLinkText.Caption(1), output);
        Assert.Contains("Example  example.com/", output);
        Assert.Equal([new Uri("https://example.com/")], _browserWindow.Browses);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>Two opens while a visit runs go to the same window, and its close is reported (and its cookies handed over) once.</summary>
    [Fact]
    public async Task BrowserLinks_TwoOpensInOneVisit_GiveOneCloseNotice()
    {
        _browserWindow = new FakeBrowserWindow { HoldVisit = true };
        var window = _browserWindow;
        StepsWhenIdle(
            Line("/browser a.example"),
            Line("/browser b.example"),
            input =>
            {
                window.EndVisit();
                PushLine(input, "/browser list --clear");   // a quiet line while the close's notice lands
            },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([new Uri("https://a.example/"), new Uri("https://b.example/")], window.Browses);
        Assert.Equal(1, output.Split("The Browser window closed;").Length - 1);
    }

    /// <summary><c>/browser clear all</c> keeps the saved links (later on 2026-10-13): the window's data alone.</summary>
    [Fact]
    public async Task BrowserClear_All_KeepsTheSavedLinks()
    {
        _browserWindow = new FakeBrowserWindow();
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        SavedLinks().Add("https://example.com/", "Example", out _);
        StepsWhenIdle(
            Line("/browser clear all"),
            input => input.Push([Keys.Down, Keys.Enter]),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(BotCheckText.ClearedAll, output);
        Assert.EndsWith("Your saved links stay.", BotCheckText.ClearedAll);
        Assert.Single(SavedLinks().List());
    }

    /// <summary>Delete with a filter typed removes the filtered link, after a yes, and the filter stays (2026-10-16, the user's ask).</summary>
    [Fact]
    public async Task BrowserLinks_ThePane_DeleteWithAFilter_RemovesTheFilteredLink_AndKeepsTheFilter()
    {
        _browserWindow = new FakeBrowserWindow();
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        var links = SavedLinks();
        links.Add("https://a.example/", "A", out _);
        links.Add("https://b.example/", "B", out _);
        PushLine("/browser list");
        _console.Input.PushKey(new ConsoleKeyInfo('B', ConsoleKey.B, true, false, false));   // the filter: B alone
        _console.Input.PushKey(Keys.Delete);                                                  // asked
        _console.Input.PushKey(Keys.Down);                                                    // Yes
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);                                                  // the filter cleared
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["https://a.example/"], SavedLinks().List().Select(l => l.Url));
        Assert.Contains(BrowserLinkText.RemovePrompt(new BrowserLink("https://b.example/", "B", default)), output);
        Assert.Contains(Spectre.Console.Markup.Remove(MenuFilter.NoMatchRow("B")), output);   // the filter kept, nothing left under it
    }

    /// <summary>On <c>/youtube list</c> too (2026-10-16): Delete with a filter typed removes the filtered video after a yes.</summary>
    [Fact]
    public async Task YouTube_TheSavedPane_DeleteWithAFilter_RemovesTheFilteredVideo()
    {
        YouTubeFixture(key: false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var library = new YouTubeLibrary(_settings.ProfileDirectory);
        library.Add("aqz-KE-bpKQ", "Big Buck Bunny", "Blender");
        library.Add("jNQXAC9IVRw", "Me at the zoo", "jawed");
        PushLine("/youtube list");
        _console.Input.PushKey(new ConsoleKeyInfo('M', ConsoleKey.M, true, false, false));   // the filter: the zoo alone ("Me at the zoo")
        _console.Input.PushKey(Keys.Delete);
        _console.Input.PushKey(Keys.Down);                                                    // Yes
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["aqz-KE-bpKQ"], new YouTubeLibrary(_settings.ProfileDirectory).List().Select(v => v.Id));
        Assert.Contains(YouTubeText.RemovePrompt(new YouTubeSaved { Id = "jNQXAC9IVRw", Title = "Me at the zoo" }), output);
    }

    /// <summary><c>/youtube list</c> reorders as the saved links do (later on 2026-10-13, the user's ask): Shift+↓ moves the highlighted video.</summary>
    [Fact]
    public async Task YouTube_TheSavedPane_ShiftDown_MovesAVideo()
    {
        YouTubeFixture(key: false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var library = new YouTubeLibrary(_settings.ProfileDirectory);
        library.Add("aqz-KE-bpKQ", "Big Buck Bunny", "Blender");
        library.Add("jNQXAC9IVRw", "Me at the zoo", "jawed");
        PushLine("/youtube list");
        _console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, true, false, false));   // the bunny below the zoo
        _console.Input.PushKey(new ConsoleKeyInfo('D', ConsoleKey.D, true, false, false));            // Shift+d: a filter, not the remove
        _console.Input.PushKey(Keys.Escape);                                                           // the filter cleared
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["jNQXAC9IVRw", "aqz-KE-bpKQ"], new YouTubeLibrary(_settings.ProfileDirectory).List().Select(v => v.Id));
        Assert.Contains(MenuFilter.Caption("D", 2, 2), output);   // "Blender" and "jawed"
        Assert.DoesNotContain(YouTubeText.RemovePrompt(new YouTubeSaved { Id = "aqz-KE-bpKQ", Title = "Big Buck Bunny" }), output);
        Assert.Empty(_videoPlayer!.Plays);
    }
}
