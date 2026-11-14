using System.Net;
using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A botchat on the Web UI's page (2026-11-12, plan what-would-it-take-tingly-ullman): the status row's ⏭️ reaches the screen's hook with the
/// address and with none is not found; the state carries the flag and the words its label; the sheet sends the six speaker colors in the
/// terminal's order, whether or not the theme lends its grounds; a speaker entry carries its slot. The status row's 👥 (2026-11-14): its hook
/// reached with the body and the address (none: not found), its flag and word, and its line queued as a chord — out of the history, counted
/// as waiting, never handed back to a draft when dropped.
/// </summary>
public sealed partial class WebUiTests
{
    [Fact]
    public void TheBotSkip_ReachesItsHook_AndWithNoneIsNotFound()
    {
        Assert.Equal(WebOutcome.NotFound, _session.SkipBot(null).Outcome);

        IPAddress? seen = null;
        _session.BotSkip = remote => { seen = remote; return new WebReply(WebOutcome.Conflict, WebUiText.NothingToSkip); };
        var reply = _session.SkipBot(IPAddress.Loopback);

        Assert.Equal(IPAddress.Loopback, seen);
        Assert.Equal((WebOutcome.Conflict, WebUiText.NothingToSkip), (reply.Outcome, reply.Message));
    }

    [Fact]
    public void TheSkipWord_IsSentWithTheSnapshot_AndTheStateDefaultsToNoBotChat()
    {
        Assert.Equal(WebUiText.Skip, WebUiText.Words.Skip);
        var state = new WebState("p", "", "", "m", false, false, false, "", [], null, "v");
        Assert.False(state.BotChat);
        Assert.Contains("\"botChat\":true", JsonSerializer.Serialize(state with { BotChat = true }, WebUiJsonContext.Default.WebState), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBotResume_ReachesItsHook_WithTheBodyAndAddress_AndWithNoneIsNotFound()
    {
        var request = new WebBotResume { Page = "page-0123456789abcdef", Speak = true };
        Assert.Equal(WebOutcome.NotFound, _session.ResumeBot(request, null).Outcome);

        (WebBotResume Request, IPAddress? Remote)? seen = null;
        _session.BotResume = (r, remote) => { seen = (r, remote); return new WebReply(WebOutcome.Conflict, App.BotChat.NothingToResumeError); };
        var reply = _session.ResumeBot(request, IPAddress.Loopback);

        Assert.Equal((request, IPAddress.Loopback), seen);
        Assert.Equal((WebOutcome.Conflict, App.BotChat.NothingToResumeError), (reply.Outcome, reply.Message));
    }

    [Fact]
    public void TheResumeWord_IsSentWithTheSnapshot_AndTheStateDefaultsToNothingToResume()
    {
        Assert.Equal(WebUiText.BotResume, WebUiText.Words.BotResume);
        var state = new WebState("p", "", "", "m", false, false, false, "", [], null, "v");
        Assert.False(state.BotResume);
        Assert.Contains("\"botResume\":true", JsonSerializer.Serialize(state with { BotResume = true }, WebUiJsonContext.Default.WebState), StringComparison.Ordinal);
        var body = JsonSerializer.Deserialize("{\"page\":\"p\",\"speak\":true}", WebUiJsonContext.Default.WebBotResume)!;
        Assert.Equal(("p", (bool?)true), (body.Page, body.Speak));
    }

    [Fact]
    public void AChordLine_Waits_OutOfTheHistory_WithItsPage_AndWakesTheIdleLine()
    {
        var accepted = new List<string>();
        _session.Accepted = accepted.Add;
        const string Page = "page-0123456789abcdef";

        Assert.Equal(WebOutcome.Ok, _session.QueueChord(App.BotChat.ResumeLine, IPAddress.Loopback, Page, speak: true).Outcome);

        Assert.Empty(accepted);   // never the history
        Assert.Equal(1, _nudges);
        Assert.Equal([App.BotChat.ResumeLine], _session.Waiting);
        Assert.Equal(1, _session.LinesAdded);
        Assert.True(_session.TryTakeLine(out var line));
        Assert.Equal(new WebLine(App.BotChat.ResumeLine, null, IPAddress.Loopback, false, Page, true, Chord: true), line);

        // A page id of the wrong shape is no page: the line goes on, speaking nowhere of its own.
        _session.QueueChord(App.BotChat.ResumeLine, null, "bad id", speak: true);
        Assert.True(_session.TryTakeLine(out var bare));
        Assert.Equal((null, false, true), (bare.Page, bare.Speak, bare.Chord));
    }

    /// <summary>A chord posted without its 🔊 (a hand-made post) tells the voice side nothing, as a send without it does (2026-11-14, the review's #4).</summary>
    [Fact]
    public void AChordLine_WithNoSpeak_LeavesThePagesSpeakAlone()
    {
        var voice = new FakeWebVoice();
        _session.Voice = voice;
        const string Page = "page-0123456789abcdef";

        _session.QueueChord(App.BotChat.ResumeLine, null, Page, speak: null);
        _session.QueueChord(App.BotChat.ResumeLine, null, Page, speak: false);

        Assert.Equal([(Page, (bool?)null), (Page, false)], voice.Seen);
        Assert.True(_session.TryTakeLine(out var line));
        Assert.False(line.Speak);   // no 🔊 said, so no voice for this line
    }

    [Fact]
    public void AChordLine_IsRefusedPastTheWaitingCap()
    {
        for (int i = 0; i < WebUiSession.MaxWaiting; i++)
        {
            _session.QueueChord(App.BotChat.ResumeLine, null, null, speak: false);
        }

        var full = _session.QueueChord(App.BotChat.ResumeLine, null, null, speak: false);
        Assert.Equal((WebOutcome.TooMany, WebUiText.QueueFull(WebUiSession.MaxWaiting)), (full.Outcome, full.Message));
    }

    [Fact]
    public void ADroppedChordLine_IsNeverHandedBackToTheDraft()
    {
        using var subscription = _session.Events.Subscribe();
        _session.Send(new WebSendRequest { Text = "one" }, null);
        _session.QueueChord(App.BotChat.ResumeLine, null, null, speak: false);

        Assert.Equal(2, _session.DropLines());

        var events = Drain(subscription);
        Assert.Equal(["dropped:one"], events.Where(e => e.Name != "state").Select(e => e.Name + ":" + JsonSerializer.Deserialize(e.Json.Value, WebUiJsonContext.Default.WebTextBack)!.Text));
        Assert.False(_session.LinesReady);
    }

    [Fact]
    public void TheSheet_SendsTheSpeakerColors_InTheTerminalsOrder_ReadableOrNot()
    {
        foreach (var palette in UI.ThemeLibrary.All)
        {
            string css = WebTheme.Css(palette, on: true);
            Spectre.Console.Color[] hues = [palette.Primary, palette.Secondary, palette.Tertiary, palette.Highlight, palette.Warm, palette.Tint];
            for (int slot = 0; slot < hues.Length; slot++)
            {
                Assert.Contains("  --speaker-" + slot + ": " + WebTheme.Hex(hues[slot]) + ";\n", css, StringComparison.Ordinal);
            }
        }

        // A theme whose ink doesn't read lends its accents alone: the speakers' colors among them.
        var unreadable = UI.Theme.Current with { Ink = UI.Theme.Current.Bg };
        Assert.False(WebTheme.Readable(unreadable));
        Assert.Contains("  --speaker-2: " + WebTheme.Hex(unreadable.Tertiary) + ";\n", WebTheme.Css(unreadable, on: true), StringComparison.Ordinal);
        Assert.DoesNotContain("--speaker-", WebTheme.Css(UI.Theme.Current, on: false), StringComparison.Ordinal);
    }

    [Fact]
    public void ASpeakerEntry_CarriesItsSlot_AndOthersNone()
    {
        var feed = new TranscriptFeed();
        long seq = feed.Add(FeedKind.Speaker, "🤖 ada", speaker: 4);
        long notice = feed.Add(FeedKind.Notice, "hi");

        var speaker = feed.EntryOf(seq, m => m)!;
        var other = feed.EntryOf(notice, m => m)!;
        Assert.Equal(4, speaker.Speaker);
        Assert.Null(other.Speaker);
        Assert.Contains("\"speaker\":4", JsonSerializer.Serialize(speaker, WebUiJsonContext.Default.WebEntry), StringComparison.Ordinal);
        Assert.DoesNotContain("\"speaker\":", JsonSerializer.Serialize(other, WebUiJsonContext.Default.WebEntry), StringComparison.Ordinal);
    }
}
