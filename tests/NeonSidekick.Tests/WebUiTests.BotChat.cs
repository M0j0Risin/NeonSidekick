using System.Net;
using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A botchat on the Web UI's page (2026-11-12, plan what-would-it-take-tingly-ullman): the status row's ⏭️ reaches the screen's hook with the
/// address and with none is not found; the state carries the flag and the words its label; the sheet sends the six speaker colors in the
/// terminal's order, whether or not the theme lends its grounds; a speaker entry carries its slot.
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
