using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.Web;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The chords the map of 2026-10-18 added (the user's picks): Ctrl+B the Browser window on its home page, closed again by a second press as
/// the user closes it (a bot check in it keeps it open); Ctrl+W the Web UI opened on this PC as <c>/webui browser</c> does, each press again;
/// Ctrl+Alt+W the Web UI pane. At the idle line, in a pane, and under a reply.
/// </summary>
public partial class ChatScreenTests
{
    private static readonly Uri BrowserHome = new("https://home.example/");

    private FakeBrowserWindow BrowserForChords(bool holdVisit = true)
    {
        var window = new FakeBrowserWindow { HoldVisit = holdVisit };
        _browserWindow = window;
        _settings.Update(d => { d.TtsOutput = false; d.WebBrowserHomePage = BrowserHome.AbsoluteUri; });
        return window;
    }

    /// <summary>The first press opens the home page (no window shown: the opener runs); the second is the user's close, the visit's own notice after it.</summary>
    [Fact]
    public async Task CtrlB_OpensTheHomePage_AndASecondPress_ClosesItAsTheUser()
    {
        var window = BrowserForChords();
        StepsWhenIdle(
            Key(Keys.CtrlB),
            Key(Keys.CtrlB),
            Line("/browser list --clear"),   // a quiet line while the close's notice lands
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([BrowserHome], window.Browses);
        Assert.Equal(2, window.UserCloses);
        Assert.Equal(0, window.Closes);   // never the quiet close, which hands nothing over
        Assert.Contains(BotCheckText.Opened(BrowserHome.AbsoluteUri), output);
        Assert.Equal(1, output.Split("The Browser window closed;").Length - 1);
        Assert.DoesNotContain(BotCheckText.KeptForBotCheck, output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>A bot check waiting in the window: Ctrl+B leaves it open and says so, nothing opened.</summary>
    [Fact]
    public async Task CtrlB_WithABotCheckWaiting_LeavesTheWindowOpen_AndSaysSo()
    {
        var window = BrowserForChords();
        window.CloseAsUserAnswer = BrowserCloseOutcome.BotCheck;
        StepsWhenIdle(Key(Keys.CtrlB), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(BotCheckText.KeptForBotCheck, output);
        Assert.Empty(window.Browses);
        Assert.Equal(1, window.UserCloses);
    }

    /// <summary>A window hidden in a bot check's linger answers none: Ctrl+B opens the home page in it, as a typed <c>/browser</c> does.</summary>
    [Fact]
    public async Task CtrlB_WithTheWindowHiddenInItsLinger_OpensTheHomePage()
    {
        var window = BrowserForChords(holdVisit: false);
        window.CloseAsUserAnswer = BrowserCloseOutcome.None;
        StepsWhenIdle(Key(Keys.CtrlB), Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([BrowserHome], window.Browses);
        Assert.DoesNotContain(BotCheckText.KeptForBotCheck, output);
    }

    /// <summary>In a pane it acts and the pane stays open: the "x" after it is the pane's (nothing), never a draft the Enter after the Esc would send.</summary>
    [Fact]
    public async Task CtrlB_InAPane_OpensTheWindow_WithThePaneLeftOpen()
    {
        var window = BrowserForChords();
        _settings.Update(d => d.MenuMaxHeight = "full");
        _console.Profile.Height = 112;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(
            input => { PushLine(input, "/help"); input.Push(Keys.CtrlB, Keys.Char('x'), Keys.Escape, Keys.Escape, Keys.Enter); },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(InfoPane.Title + " │ Basic", output);
        Assert.Equal([BrowserHome], window.Browses);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>During a reply it acts at once (<c>/browser</c> is quick): the window opened while the reply still streams.</summary>
    [Fact]
    public async Task MidTurn_CtrlB_OpensTheWindowAtOnce()
    {
        var window = BrowserForChords();
        int openedDuringTheReply = -1;
        MidTurnFixture(i =>
        {
            if (i == 0)
            {
                _scripted!.Push(Keys.CtrlB);
            }
            else if (i == 2)
            {
                openedDuringTheReply = window.Browses.Count;
            }
        });

        string output = await RunAsync();

        Assert.Equal(1, openedDuringTheReply);
        Assert.DoesNotContain(ChatScreen.CancelledNotice, output);
        Assert.Single(_chat.Requests);
    }

    /// <summary>With no Browser window (a Mac before macOS 14, no desktop): what a typed <c>/browser</c> says.</summary>
    [Fact]
    public async Task CtrlB_WithNoWindow_SaysWhatTheTypedCommandSays()
    {
        _browserWindow = null;
        _settings.Update(d => d.TtsOutput = false);
        StepsWhenIdle(Key(Keys.CtrlB), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(BotCheckText.BrowserNotHere, output);
    }

    private void WebUiForChords(Action<NeonSidekick.Settings.AppSettingsData>? set = null)
    {
        _webUiHost = _ => new RecordingHost();
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = true; d.WebUiReach = "local"; set?.Invoke(d); });
    }

    /// <summary>Under <c>browser-window</c> each press opens the Web UI in the window again (no toggle), signed in, as <c>/webui browser</c>.</summary>
    [Fact]
    public async Task CtrlW_OpensTheWebUiInTheBrowserWindow_AgainOnEachPress()
    {
        var window = new FakeBrowserWindow();
        _browserWindow = window;
        WebUiForChords(d => d.WebOpenLinksIn = OpenLinksIn.BrowserWindow);
        StepsWhenIdle(Key(Keys.CtrlW), Key(Keys.CtrlW), Line("/exit"));

        string output = await RunAsync();

        Assert.Equal(2, window.Browses.Count);
        Assert.All(window.Browses, u => Assert.Contains("/signin?", u!.AbsoluteUri, StringComparison.Ordinal));
        Assert.Contains(WebUiText.BrowserOpened(window: true), output);
        Assert.Equal(0, window.UserCloses);   // no toggle: the window is never asked to close
    }

    /// <summary>Under <c>default-browser</c> the default browser gets it.</summary>
    [Fact]
    public async Task CtrlW_OpensTheWebUiInTheDefaultBrowser()
    {
        WebUiForChords();
        StepsWhenIdle(Key(Keys.CtrlW), Line("/exit"));

        string output = await RunAsync();

        Assert.StartsWith("http://127.0.0.1:", Assert.Single(_openedUrls), StringComparison.Ordinal);
        Assert.Contains(WebUiText.BrowserOpened(window: false), output);
    }

    /// <summary>In a pane it acts and leaves the pane open, as the <c>/webui</c> pane's link double-click does.</summary>
    [Fact]
    public async Task CtrlW_InAPane_OpensTheWebUi_WithThePaneLeftOpen()
    {
        WebUiForChords(d => d.MenuMaxHeight = "full");
        _console.Profile.Height = 112;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(
            input => { PushLine(input, "/help"); input.Push(Keys.CtrlW, Keys.Char('x'), Keys.Escape, Keys.Escape, Keys.Enter); },
            input => { WaitUntil(() => _openedUrls.Count == 1); PushLine(input, "/exit"); });

        string output = await RunAsync();

        Assert.Contains(InfoPane.Title + " │ Basic", output);
        Assert.Single(_openedUrls);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>During a reply it opens at once (<c>/webui browser</c> is quick).</summary>
    [Fact]
    public async Task MidTurn_CtrlW_OpensTheWebUiAtOnce()
    {
        _webUiHost = _ => new RecordingHost();
        int openedDuringTheReply = -1;
        WebUiUnderAReply(i =>
        {
            if (i == 0)
            {
                _scripted!.Push(Keys.CtrlW);
            }
            else if (i == 2)
            {
                openedDuringTheReply = _openedUrls.Count;
            }
        }, webUi: true);

        await RunAsync();

        Assert.Equal(1, openedDuringTheReply);
        AssertReplyWhole();
    }

    /// <summary>With the Web UI off, the typed command's error.</summary>
    [Fact]
    public async Task CtrlW_WithTheWebUiOff_IsTheError()
    {
        _webUiHost = _ => new RecordingHost();
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = false; });
        StepsWhenIdle(Key(Keys.CtrlW), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("  ✗ " + WebUiText.BrowserWebUiOff, output);
        Assert.Empty(_openedUrls);
    }

    /// <summary>Ctrl+Alt+W opens the Web UI pane at the idle line; in another pane it closes that one and opens <c>/webui</c>.</summary>
    [Fact]
    public async Task CtrlAltW_OpensTheWebUiPane_AndFromAnotherPane_SwitchesToIt()
    {
        WebUiForChords(d => d.MenuMaxHeight = "full");
        _console.Profile.Height = 112;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(
            input => { input.Push(Keys.CtrlAlt(ConsoleKey.W)); input.Push(Keys.Escape); },
            input => { PushLine(input, "/help"); input.Push(Keys.CtrlAlt(ConsoleKey.W)); input.Push(Keys.Escape); },
            Line("/exit"));

        string output = await RunAsync();

        int first = output.IndexOf(WebUiText.PaneTitle, StringComparison.Ordinal);
        Assert.True(first >= 0, output);
        int help = output.IndexOf(InfoPane.Title + " │ Basic", first, StringComparison.Ordinal);
        Assert.True(help > first, output);
        Assert.True(output.IndexOf(WebUiText.PaneTitle, help, StringComparison.Ordinal) > help, output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>Ctrl+Alt+W during a reply opens the pane over it, the reply running on.</summary>
    [Fact]
    public async Task MidTurn_CtrlAltW_OpensTheWebUiPane_OverTheReply()
    {
        _webUiHost = _ => new RecordingHost();
        WebUiUnderAReply(i =>
        {
            if (i == 0)
            {
                _scripted!.Push(Keys.CtrlAlt(ConsoleKey.W));
            }
            else if (i == 2)
            {
                Scripted().Push(Keys.Escape);
            }
        }, webUi: true);

        string output = await RunAsync();

        Assert.Contains(WebUiText.PaneTitle, output);
        Assert.DoesNotContain(ChatScreen.CancelledNotice, output);
        AssertReplyWhole();
    }
}
