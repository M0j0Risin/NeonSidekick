using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/webui browser [--external]</c> and the pane's link (later on 2026-10-15, the user's asks): where it opens (<i>Web open links in</i>, the
/// window unavailable, <c>--external</c>), how it signs in (<i>Web browser sign in mode</i>; the window's usual link, or a one-time one under
/// <c>keep-history</c>), no token in the transcript or the log, the refusals, a server still starting, under a reply at once, the pane's copy
/// button and a double-click on the pane's link.
/// </summary>
public partial class ChatScreenTests
{
    private string Token => SettingsSecrets.Reveal(_settings.Current.WebUiToken) ?? "";

    private async Task<string> BrowserRunAsync(Action<NeonSidekick.Settings.AppSettingsData> set, params string[] lines)
    {
        _webUiHost = _ => new RecordingHost();
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = true; d.WebUiReach = "local"; set(d); });
        foreach (string line in lines)
        {
            PushLine(line);
        }

        PushLine("/exit");
        return await RunAsync();
    }

    [Fact]
    public async Task WebUiBrowser_DefaultBrowser_OneTimeLink_OnLoopback_AndNoTokenAnywhere()
    {
        var log = new List<string>();
        void Collect(DiagnosticEvent e) { lock (log) { log.Add(e.Message); } }
        DiagnosticLog.Emitted += Collect;
        try
        {
            string output = await BrowserRunAsync(_ => { }, "/webui browser");

            string url = Assert.Single(_openedUrls);
            Assert.StartsWith("http://127.0.0.1:", url, StringComparison.Ordinal);
            Assert.Contains("/signin?once=code1", url, StringComparison.Ordinal);
            Assert.Contains(WebUiText.BrowserOpened(window: false), output);
            Assert.DoesNotContain(Token, output, StringComparison.Ordinal);
            Assert.DoesNotContain("once=", output, StringComparison.Ordinal);
            lock (log)
            {
                Assert.DoesNotContain(log, l => l.Contains(Token, StringComparison.Ordinal) || l.Contains("code1", StringComparison.Ordinal));
            }
        }
        finally
        {
            DiagnosticLog.Emitted -= Collect;
        }
    }

    [Fact]
    public async Task WebUiBrowser_UrlWithToken_OpensTheUsualLink()
    {
        await BrowserRunAsync(d => d.WebBrowserSignInMode = NeonSidekick.Web.WebSignInMode.UrlWithToken, "/webui browser");

        Assert.EndsWith("/signin?t=" + Token, Assert.Single(_openedUrls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebUiBrowser_BrowserWindow_TheUsualLink_OrAOneTimeOneUnderKeepHistory_AndExternalTheDefault()
    {
        var window = new FakeBrowserWindow();
        _browserWindow = window;
        string output = await BrowserRunAsync(d => d.WebOpenLinksIn = NeonSidekick.Web.OpenLinksIn.BrowserWindow, "/webui browser", "/webui browser --external");

        Assert.EndsWith("/signin?t=" + Token, Assert.Single(window.Browses)!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("/signin?once=", Assert.Single(_openedUrls), StringComparison.Ordinal);   // --external: the default browser, one-time
        Assert.Contains(WebUiText.BrowserOpened(window: true), output);
        Assert.DoesNotContain(Token, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebUiBrowser_InTheWindowUnderKeepHistory_IsAOneTimeLink_AndAWindowUnavailable_TheDefaultBrowser()
    {
        var window = new FakeBrowserWindow();
        _browserWindow = window;
        await BrowserRunAsync(d => { d.WebOpenLinksIn = NeonSidekick.Web.OpenLinksIn.BrowserWindow; d.BrowserPrivacy = NeonSidekick.Viewer.BrowserPrivacy.KeepHistory; }, "/webui browser");
        Assert.Contains("/signin?once=", Assert.Single(window.Browses)!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebUiBrowser_TheWindowUnavailable_GoesToTheDefaultBrowser()
    {
        _browserWindow = new FakeBrowserWindow { UnavailableReason = "a bot check is waiting" };
        string output = await BrowserRunAsync(d => d.WebOpenLinksIn = NeonSidekick.Web.OpenLinksIn.BrowserWindow, "/webui browser");

        Assert.Single(_openedUrls);
        Assert.Contains(WebUiText.BrowserOpened(window: false), output);
    }

    [Fact]
    public async Task WebUiBrowser_Off_IsTheError_AndABadFormTheUsage()
    {
        _webUiHost = _ => new RecordingHost();
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = false; });
        PushLine("/webui browser");
        PushLine("/webui browser --nope");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + WebUiText.BrowserWebUiOff, output);
        Assert.Contains("  ✗ " + WebUiText.UsageError, output);
        Assert.Empty(_openedUrls);
        Assert.Equal((bool?)true, ChatScreen.WebUiBrowserArgs(" BROWSER --External "));
        Assert.Equal((bool?)false, ChatScreen.WebUiBrowserArgs("browser"));
        Assert.Null(ChatScreen.WebUiBrowserArgs("browse"));
        Assert.Equal(MidTurnClass.Quick, ChatScreen.MidTurnPolicy(SlashCommand.WebUi, "browser --external"));
    }

    [Fact]
    public async Task WebUiBrowser_RightAfterWebUiOn_WaitsForTheServer()
    {
        _webUiHost = _ => new RecordingHost { StartDelay = TimeSpan.FromMilliseconds(300) };
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = false; d.WebUiReach = "local"; });
        PushLine("/webui on");
        PushLine("/webui browser");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Single(_openedUrls);
        Assert.DoesNotContain(WebUiText.BrowserNotServing(_settings.Current.WebUiPort), output);
    }

    [Fact]
    public async Task WebUiBrowser_UnderAReply_OpensAtOnce()
    {
        _webUiHost = _ => new RecordingHost();
        int openedDuringTheReply = -1;
        WebUiUnderAReply(i =>
        {
            if (i == 0)
            {
                PushLine("/webui browser");
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

    [Fact]
    public async Task ThePanesCopy_CopiesTheShownLink_AndTheLinksLine_OpensItsHost_AnotherLineNothing()
    {
        _webUiHost = _ => new RecordingHost();
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = true; d.WebUiReach = "local"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        string link = "";
        var input = WhenIdle(
            i =>
            {
                PushLine(i, "/webui");
                i.Push(Keys.Char(WebUiText.CopyKey), Keys.Escape);   // c copies the link; the pane opens again, ESC closes it
            },
            i =>
            {
                link = Assert.Single(_copied);
                _running!.OpenLinkLine(link, WebUiText.TokenWarning, _running.FlowForTests);   // not the link: nothing
                _running.OpenLinkLine(link, link[..30], _running.FlowForTests);              // a wrapped piece of it: opened
                WaitUntil(() => _openedUrls.Count == 1);
                PushLine(i, "/exit");
            });

        string output = await RunAsync(input);

        Assert.Contains("/signin?t=", link, StringComparison.Ordinal);
        Assert.Contains(WebUiText.CopiedNotice, output);
        Assert.StartsWith("http://localhost:", Assert.Single(_openedUrls), StringComparison.Ordinal);   // the tab's host
        Assert.Contains("/signin?once=", _openedUrls[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQrCodeRow_OpensTheTabsHost_ABlankOrMixedLineNothing()
    {
        // 2026-10-20, the user's ask: a double-click anywhere on the QR code opens the link, as one on the link does.
        _webUiHost = _ => new RecordingHost();
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = true; d.WebUiReach = "local"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        string link = "";
        var input = WhenIdle(
            i =>
            {
                PushLine(i, "/webui");
                i.Push(Keys.Char(WebUiText.CopyKey), Keys.Escape);
            },
            i =>
            {
                link = Assert.Single(_copied);
                _running!.OpenLinkLine(link, "   ", _running.FlowForTests);                                  // blank: nothing
                _running.OpenLinkLine(link, new string(QrView.Cell, 20) + " x", _running.FlowForTests);   // not all cells: nothing
                _running.OpenLinkLine(link, "  " + new string(QrView.Cell, 41) + "  ", _running.FlowForTests);   // a row of the code: opened
                WaitUntil(() => _openedUrls.Count == 1);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.StartsWith("http://localhost:", Assert.Single(_openedUrls), StringComparison.Ordinal);
    }
}
