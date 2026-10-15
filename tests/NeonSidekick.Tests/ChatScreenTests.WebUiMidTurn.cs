using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/webui</c> over a reply (later on 2026-10-15, the user's ask: it waited for the reply's end): its pane on the watcher with the buttons acting
/// at once (the user's pick), its lines and the server's notices written once and never into the streaming reply, <c>copy</c> at once,
/// <c>on</c>/<c>off</c>/<c>reset</c> still waiting, the reply ending under the pane, no server in this build, the toolbar's 📱.
/// </summary>
public partial class ChatScreenTests
{
    private const string ReplyText = "One two three.";

    /// <summary>A reply of three parts, each held 40 ms; <paramref name="duringTheReply"/> runs before each (its index) to type under it.</summary>
    private void WebUiUnderAReply(Action<int> duringTheReply, bool webUi, string reach = "local")
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = webUi; d.WebUiReach = reach; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText("One ", "two ", "three.");
        _chat.BeforeUpdate = async (i, _) =>
        {
            duringTheReply(i);
            await Task.Delay(40, CancellationToken.None);
        };
        StepsWhenIdle(Line("hi"), Line("/exit"));
    }

    /// <summary>How many transcript lines read <paramref name="text"/>: the page's copy of the transcript, each line once (the screen's output redraws).</summary>
    private int Lines(string text) => Web.Feed.Snapshot(m => m).Count(e => e.Text == text);

    /// <summary>
    /// The reply's text whole: nothing written into it from the watcher. A notice posted to the turn task lands as a line of its own at the next act
    /// drain, between two of the reply's stretches (every pane over a reply does so), so the stretches are read together.
    /// </summary>
    private void AssertReplyWhole()
    {
        var feed = Web.Feed.Snapshot(m => m);
        Assert.True(string.Concat(feed.Where(e => e.Kind == "assistant").Select(e => e.Text)).Trim() == ReplyText, string.Join(" | ", feed.Select(e => e.Kind + ": " + e.Text)));
    }

    [Fact]
    public void UnderAReply_WebUiIsAPane_CopyIsQuick_AndItsSwitchesWait()
    {
        Assert.Equal(
            [MidTurnClass.Pane, MidTurnClass.Quick, MidTurnClass.Quick, MidTurnClass.Deferred, MidTurnClass.Deferred, MidTurnClass.Deferred],
            new[] { "", "copy", " COPY ", "on", "off", "reset" }.Select(a => ChatScreen.MidTurnPolicy(SlashCommand.WebUi, a)));
        // The toolbar's 📱 sends the same line, so it opens the pane over a reply too.
        Assert.Equal(SlashCommands.WebUiWord, ChatScreen.ToolbarWord(ChatScreen.WebUiToolGlyph));
        Assert.Equal(SlashCommands.WebUiWord, ChatScreen.OffPaneLine(new ScreenPane.OffPaneHit(null, new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, ChatScreen.WebUiToolGlyph, 0))));
    }

    [Fact]
    public async Task WebUiTyped_UnderAReply_OpensThePane_AndTheReplyEndsAsWritten()
    {
        _webUiHost = _ => new RecordingHost();
        WebUiUnderAReply(i =>
        {
            if (i == 0)
            {
                PushLine("/webui");
            }
            else if (i == 1)
            {
                Scripted().Push(Keys.Escape);
            }
        }, webUi: true);

        string output = await RunAsync();

        Assert.Contains(WebUiText.PaneTitle + " │ enabled │ local │ reset ", output);
        AssertReplyWhole();
        Assert.DoesNotContain(ChatScreen.MidTurnDeferredNotice("/webui"), output);
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task TheEnabledButton_UnderAReply_StartsTheServer_ItsNoticesOnce_TheReplyWhole()
    {
        var made = new List<RecordingHost>();
        _webUiHost = _ => { var host = new RecordingHost(); made.Add(host); return host; };
        WebUiUnderAReply(i =>
        {
            if (i == 0)
            {
                PushLine("/webui");
            }
            else if (i == 1)
            {
                Scripted().Push(Keys.Char(WebUiText.EnabledKey), Keys.Escape);
            }
        }, webUi: false);

        string output = await RunAsync();

        Assert.True(_settings.Current.WebUi);
        var host = Assert.Single(made);
        Assert.Equal(WebUiAudience.Local, host.Started!.Audience);
        AssertReplyWhole();
        Assert.Equal(1, Lines(SettingsMenu.SavedNotice(SettingsField.WebUi, _settings.Current, _settings.ProfileDirectory)));
        Assert.Equal(1, Lines(WebUiText.ServingNotice(WebUiAuth.Address("localhost", host.Port))));
    }

    [Fact]
    public async Task WebUiCopy_UnderAReply_CopiesAtOnce()
    {
        _webUiHost = _ => new RecordingHost();
        int copiedDuringTheReply = -1;
        WebUiUnderAReply(i =>
        {
            if (i == 0)
            {
                PushLine("/webui copy");
            }
            else if (i == 2)
            {
                copiedDuringTheReply = _copied.Count;
            }
        }, webUi: true);

        string output = await RunAsync();

        Assert.Equal(1, copiedDuringTheReply);   // before the reply's last part
        Assert.Contains("/signin", Assert.Single(_copied), StringComparison.Ordinal);
        Assert.Contains(WebUiText.CopiedNotice, output);
    }

    [Fact]
    public async Task WebUiOn_UnderAReply_WaitsForItsEnd()
    {
        var made = new List<RecordingHost>();
        _webUiHost = _ => { var host = new RecordingHost(); made.Add(host); return host; };
        bool onDuringTheReply = true;
        WebUiUnderAReply(i =>
        {
            if (i == 0)
            {
                PushLine("/webui on");
            }
            else if (i == 2)
            {
                onDuringTheReply = _settings.Current.WebUi;
            }
        }, webUi: false);

        string output = await RunAsync();

        Assert.False(onDuringTheReply);
        Assert.Contains(ChatScreen.MidTurnDeferredNotice("/webui"), output);
        Assert.True(_settings.Current.WebUi);
        Assert.Single(made);
    }

    [Fact]
    public async Task AnUnreadableToken_IsMintedOnce_UnderAReply_AndAtTheStart()
    {
        _webUiHost = _ => new RecordingHost();
        _settings.Update(d => d.WebUiToken = "dpapi:not-a-token");
        WebUiUnderAReply(i =>
        {
            if (i == 0)
            {
                PushLine("/webui");
            }
            else if (i == 1)
            {
                Scripted().Push(Keys.Char(WebUiText.EnabledKey), Keys.Escape);
            }
        }, webUi: false);

        string output = await RunAsync();

        Assert.Equal(1, Lines(WebUiText.TokenMintedNotice));
        Assert.True(WebUiAuth.IsWellFormed(SettingsSecrets.Reveal(_settings.Current.WebUiToken) ?? ""));
    }

    [Fact]
    public async Task AnUnreadableToken_AtTheStart_IsMintedOnce()
    {
        _webUiHost = _ => new RecordingHost();
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = true; d.WebUiReach = "local"; d.WebUiToken = "dpapi:not-a-token"; });
        var input = WhenIdle(i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Equal(1, Lines(WebUiText.TokenMintedNotice));
    }

    [Fact]
    public async Task TheReplyEnding_UnderThePane_ItsButtonRestartsTheServerOnce()
    {
        var made = new List<RecordingHost>();
        _webUiHost = _ => { var host = new RecordingHost(); made.Add(host); return host; };
        Task? afterTheReply = null;
        WebUiUnderAReply(i =>
        {
            if (i == 0)
            {
                PushLine("/webui");
                var input = Scripted();
                // The reach button pressed once the reply has ended, the pane still open over the line.
                afterTheReply = Task.Run(async () =>
                {
                    var until = DateTime.UtcNow.AddSeconds(10);
                    while (_session.History.TurnCount == 0 && DateTime.UtcNow < until)
                    {
                        await Task.Delay(20);
                    }

                    await Task.Delay(100);
                    input.Push(Keys.Char(WebUiText.ReachKey), Keys.Escape);
                });
            }
        }, webUi: true);

        string output = await RunAsync();
        await afterTheReply!;

        Assert.Equal("lan", _settings.Current.WebUiReach);
        var starts = made.SelectMany(h => h.Starts).ToList();
        Assert.Equal(2, starts.Count);   // the first start and the reach's restart: the idle line's backstop restarts nothing more
        Assert.Equal(WebUiAudience.LocalAreaNetwork, starts[1].Audience);
        Assert.Equal(1, Lines(SettingsMenu.SavedNotice(SettingsField.WebUiReach, _settings.Current, _settings.ProfileDirectory)));
    }

    [Fact]
    public async Task WebUi_UnderAReply_WithoutAServerInThisBuild_SaysSoOnce_AndOpensNoPane()
    {
        WebUiUnderAReply(i =>
        {
            if (i == 0)
            {
                PushLine("/webui");
            }
        }, webUi: false);

        string output = await RunAsync();

        Assert.Equal(1, Lines(WebUiText.NotAvailableError));
        Assert.DoesNotContain(WebUiText.PaneTitle + " │", output);
        AssertReplyWhole();
    }
}
