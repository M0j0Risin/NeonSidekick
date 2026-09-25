using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/botchat</c> on the screen (2026-09-24): the profiles take turns, each request carrying the speaker's own
/// persona and no other bot's, every request on the starting profile's one client whatever the other profiles'
/// LLM settings say, ESC ending the chat, the chat stored as a session of its own, and the refusals.
/// </summary>
public partial class ChatScreenTests
{
    private const string NeonMarker = "PERSONA-NEON-7Q";
    private const string AdaMarker = "PERSONA-ADA-3K";

    /// <summary>A second profile, <c>ada</c>, with its own persona and an LLM server and model of its own that must never be asked.</summary>
    private void BotChatFixture()
    {
        _settings.Update(d => d.TtsOutput = false);
        File.WriteAllText(Path.Combine(_settings.ProfileDirectory, PersonaFile.FileName), "You are Neon. " + NeonMarker);
        Profiles.Create(_dir, "ada", new AppSettingsData { LlmUrl = "http://ada-only-server:9999", LlmModel = "ada-only-model", TtsVoice = "bm_george" });
        File.WriteAllText(Path.Combine(ProfileDir("ada"), PersonaFile.FileName), "You are Ada. " + AdaMarker);
    }

    private static string SystemText(IReadOnlyList<ChatMessage> request) => request.Single(m => m.Role == ChatRole.System).Text;

    /// <summary>ESC during the <paramref name="n"/>th request (1-based) — the chat's stop.</summary>
    private void EscDuringRequest(int n) =>
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == n && i == 1)
            {
                _console.Input.PushKey(Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };

    [Fact]
    public async Task BotChat_TwoProfiles_TakeTurns_EachInItsOwnPersona_OnTheStartersClient_UntilEsc()
    {
        BotChatFixture();
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Hello from Ada.");
        _chat.EnqueueText("Neon ", "again");
        EscDuringRequest(3);
        PushLine("/botchat the best pizza");
        PushLine("/exit");

        string output = await RunAsync();

        // Three requests, all through the one client the starting profile connected: ada's own server is never asked.
        Assert.Equal(3, _chat.Requests.Count);
        Assert.DoesNotContain(_http.Requests, r => r.Uri.Host.Contains("ada-only", StringComparison.Ordinal));

        // With two bots the turns alternate, the starter first; each request carries its speaker's persona alone.
        Assert.Contains(NeonMarker, SystemText(_chat.Requests[0]));
        Assert.DoesNotContain(AdaMarker, SystemText(_chat.Requests[0]));
        Assert.Contains(AdaMarker, SystemText(_chat.Requests[1]));
        Assert.DoesNotContain(NeonMarker, SystemText(_chat.Requests[1]));
        Assert.Contains(NeonMarker, SystemText(_chat.Requests[2]));
        Assert.Contains(BotChat.Rules("ada", ["default"], "the best pizza", BotChat.PronounsLine([("default", BotGender.Female)])), SystemText(_chat.Requests[1]));

        // Each speaker is told the others' pronouns from their first voices (2026-09-25): ada's bm_ is male, default's af_heart female.
        Assert.Contains("ada is a man (he/him)", SystemText(_chat.Requests[0]));
        Assert.DoesNotContain("default is a", SystemText(_chat.Requests[0]));
        Assert.Contains("default is a woman (she/her)", SystemText(_chat.Requests[1]));
        Assert.DoesNotContain("ada is a", SystemText(_chat.Requests[1]));

        // No tools, and each speaker sees the other's line signed and its own as the assistant's.
        Assert.Equal(BotChat.OpeningText(["ada"], "the best pizza"), _chat.Requests[0][^1].Text);
        Assert.Equal("default: Hello from Neon.", _chat.Requests[1][^1].Text);
        Assert.Equal(ChatRole.Assistant, _chat.Requests[2][^2].Role);
        Assert.Equal("Hello from Neon.", _chat.Requests[2][^2].Text);
        Assert.Equal("ada: Hello from Ada.", _chat.Requests[2][^1].Text);

        Assert.Contains("  · " + BotChat.StartNotice(["default", "ada"], "the best pizza") + "\n", output);
        // Each reply under its speaker's name (the spinner's frames may sit between the two in the raw output).
        int neon = output.IndexOf(TranscriptRenderer.SpeakerGlyph + "default\n", StringComparison.Ordinal);
        int ada = output.IndexOf(TranscriptRenderer.SpeakerGlyph + "ada\n", StringComparison.Ordinal);
        Assert.True(neon >= 0 && ada > neon);
        Assert.InRange(output.IndexOf("● Hello from Neon.", StringComparison.Ordinal), neon, ada);
        Assert.True(output.IndexOf("● Hello from Ada.", StringComparison.Ordinal) > ada);
        Assert.Contains("  · " + ChatScreen.CancelledNotice + "\n", output);
        Assert.Contains("  · " + BotChat.StoppedNotice(3) + "\n", output);

        // The main conversation is untouched: the bots' lines never reach its history.
        Assert.Empty(_session.History.Messages);
    }

    [Fact]
    public async Task BotChat_IsStoredAsASessionOfItsOwn()
    {
        BotChatFixture();
        _settings.Update(d => d.SessionLogging = true);
        _chat.EnqueueText("One.");
        _chat.EnqueueText("Two.");
        _chat.EnqueueText("never");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        using var store = OpenSessions();
        var summary = Assert.Single(store.List(10));
        Assert.Equal(BotChat.SessionTitle(["default", "ada"]), summary.Title);
        var record = store.Load(summary.Id)!;
        Assert.Equal(new[] { "default: One.", "ada: Two." }, record.Turns.Take(2).Select(t => t.ReplyText));
        Assert.Contains("ada: Two.", record.HistoryJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BotChat_NamedProfiles_OnlyThoseJoin_AndACorruptOneSitsItOut()
    {
        BotChatFixture();
        Profiles.Create(_dir, "max", new AppSettingsData());
        Profiles.Create(_dir, "zed", new AppSettingsData());
        File.WriteAllText(Profiles.ProfileFile(_dir, "zed"), "{ not json");
        _chat.EnqueueText("One.");
        _chat.EnqueueText("never");
        EscDuringRequest(2);
        PushLine("/botchat ada zed hello there");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · (botchat: zed sits this one out: ", output);
        Assert.Contains(BotChat.StartNotice(["default", "ada"], "hello there"), output);
        Assert.DoesNotContain("max", SystemText(_chat.Requests[0]));
    }

    /// <summary>
    /// Speech on, the pane up: the first bot's reply is still being heard (its first sentence held in the synthesizer)
    /// when a line is sent. The user's report (later on 2026-09-24): it stayed type-ahead until ESC, then went to the main
    /// chat. Now it shows at once and the next bot answers it; the bot speaking was let finish.
    /// </summary>
    [Fact]
    public async Task BotChat_ALineSentWhileABotSpeaks_JoinsTheChat_BeforeTheNextReply()
    {
        BotChatFixture();
        _settings.Update(d => d.TtsOutput = true);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        bool sent = false;
        _synth.OnSynthesize = async (text, _) =>
        {
            if (!sent && text.Contains("Hello from Neon.", StringComparison.Ordinal))
            {
                sent = true;
                await Task.Delay(200);   // the reply's text has long ended: the chat is in the speech wait
                PushLine("hi bots");
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!Output.Contains("› hi bots", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(10);
                }
            }
        };
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Hello, user.");
        _chat.EnqueueText("never");
        _chat.BeforeUpdate = (i, _) =>
        {
            if (i == 0 && _chat.Requests.Count == 3)
            {
                PushLine("/exit");   // cancels the reply and the chat, then runs at the idle line
            }

            return Task.CompletedTask;
        };
        PushLine("/botchat");

        string output = await RunAsync();

        Assert.True(_chat.Requests.Count >= 2);
        Assert.Equal("User: hi bots", _chat.Requests[1][^1].Text.Split("\n\n")[^1]);
        Assert.Contains("default: Hello from Neon.", _chat.Requests[1][^1].Text);
        Assert.True(output.IndexOf("› hi bots", StringComparison.Ordinal) < output.IndexOf(TranscriptRenderer.SpeakerGlyph + "ada", StringComparison.Ordinal));
        Assert.Empty(_session.History.Messages);   // never the main chat's
    }

    /// <summary>
    /// ESC while a bot's reply is still being heard (2026-09-25, the user's ask): its voice alone stops, the
    /// speech-stopped line under it, and the chat goes on to the next bot. The chat is ended by <c>/exit</c> sent
    /// under the second reply.
    /// </summary>
    [Fact]
    public async Task BotChat_EscWhileABotSpeaks_SkipsItsSpeech_AndTheChatGoesOn()
    {
        BotChatFixture();
        _settings.Update(d => d.TtsOutput = true);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _playback.HoldBytes = true;   // no reply finishes playing on its own; the second is ended by /exit
        bool pressed = false;
        _synth.OnSynthesize = async (text, _) =>
        {
            if (!pressed)
            {
                pressed = true;
                await Task.Delay(200);   // the text has ended: the chat is in the speech wait
                _console.Input.PushKey(Keys.Escape);   // the audio stays held: only the skip can end this wait
            }
        };
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Hello from Ada.");
        _chat.BeforeUpdate = (i, _) =>
        {
            if (i == 0 && _chat.Requests.Count == 2)
            {
                PushLine("/exit");   // cancels the second reply and the chat, then runs at the idle line
            }

            return Task.CompletedTask;
        };
        PushLine("/botchat");

        string output = await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Contains(AdaMarker, SystemText(_chat.Requests[1]));
        Assert.Contains("  · " + ChatScreen.SpeechStoppedNotice + "\n", output);
        Assert.True(output.IndexOf(ChatScreen.SpeechStoppedNotice, StringComparison.Ordinal) < output.IndexOf(TranscriptRenderer.SpeakerGlyph + "ada", StringComparison.Ordinal));
        Assert.DoesNotContain(BotChat.StoppedNotice(1), output);
        Assert.True(_playback.Stopped >= 1);
    }

    /// <summary>ESC twice while a bot's reply is still being heard ends the chat, as one ESC did before (2026-09-25): no second request.</summary>
    [Fact]
    public async Task BotChat_EscTwiceWhileABotSpeaks_StopsTheChat()
    {
        BotChatFixture();
        _settings.Update(d => d.TtsOutput = true);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _playback.HoldBytes = true;   // the reply never finishes playing on its own
        bool pressed = false;
        _synth.OnSynthesize = async (text, _) =>
        {
            if (!pressed)
            {
                pressed = true;
                await Task.Delay(200);
                _console.Input.PushKey(Keys.Escape);
                _console.Input.PushKey(Keys.Escape);
                PushLine("/exit");
            }
        };
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("never");
        PushLine("/botchat");

        string output = await RunAsync();

        Assert.Single(_chat.Requests);
        Assert.Contains(BotChat.StoppedNotice(1), output);
        Assert.True(_playback.Stopped >= 1);
    }

    [Fact]
    public async Task BotChat_WithOneProfile_IsRefused()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BotChat.TooFewError, output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public void BotChatChoices_OfferTheOtherProfiles_AfterTheNamesTyped()
    {
        var sources = new ChatScreen.ArgumentSources(() => ["default", "ada", "max"], "default", [], _ => [], _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false), _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false));

        Assert.Equal(new[] { "ada", "max", BotChat.ResumeSwitch }, ChatScreen.BotChatChoices("", sources).Select(i => i.Text));
        Assert.Equal(new[] { "ada max" }, ChatScreen.BotChatChoices("ada ", sources).Select(i => i.Text));   // --resume only first
        Assert.Empty(ChatScreen.BotChatChoices("pizza ", sources));
        Assert.Empty(ChatScreen.BotChatChoices("ada -- ", sources));   // after the separator the topic is free text
    }

    /// <summary>ESC during each of the <paramref name="requests"/>th requests (1-based): one stop per chat.</summary>
    private void EscDuringRequests(params int[] requests) =>
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (requests.Contains(_chat.Requests.Count) && i == 1)
            {
                _console.Input.PushKey(Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };

    [Fact]
    public async Task BotChat_Resume_CarriesOnTheLastChat_WhereItStopped()
    {
        BotChatFixture();
        _chat.EnqueueText("One.");
        _chat.EnqueueText("Two.");
        _chat.EnqueueText("Neon ", "again");     // cut by ESC: "Neon" is the chat's third line
        _chat.EnqueueText("Four.");
        _chat.EnqueueText("Five ", "more");
        EscDuringRequests(3, 5);
        PushLine("/botchat pizza");
        PushLine("/botchat --resume");
        PushLine("/exit");

        string output = await RunAsync();

        // The resumed chat's first speaker is ada (default spoke last), shown every line so far, on the saved topic.
        Assert.Equal(5, _chat.Requests.Count);
        Assert.Contains(AdaMarker, SystemText(_chat.Requests[3]));
        Assert.Contains(BotChat.Rules("ada", ["default"], "pizza", BotChat.PronounsLine([("default", BotGender.Female)])), SystemText(_chat.Requests[3]));
        Assert.Equal(ChatRole.Assistant, _chat.Requests[3][^2].Role);
        Assert.Equal("Two.", _chat.Requests[3][^2].Text);
        Assert.Equal("default: Neon", _chat.Requests[3][^1].Text);
        Assert.Contains(NeonMarker, SystemText(_chat.Requests[4]));

        // Stopped, resumed with the replies so far, and the stop counts them all.
        int stopped = output.IndexOf(BotChat.StoppedNotice(3), StringComparison.Ordinal);
        int resumed = output.IndexOf(BotChat.ResumeNotice(["default", "ada"], "pizza", 3), StringComparison.Ordinal);
        Assert.True(stopped >= 0 && resumed > stopped, $"stopped {stopped}, resumed {resumed}");
        Assert.True(output.IndexOf(BotChat.StoppedNotice(5), resumed, StringComparison.Ordinal) > resumed);
    }

    [Fact]
    public async Task BotChat_ResumeWithALine_TheLineJoinsAsTheUsers_InTheSameSession()
    {
        BotChatFixture();
        _settings.Update(d => d.SessionLogging = true);
        _chat.EnqueueText("One.");
        _chat.EnqueueText("Two ", "cut");
        _chat.EnqueueText("Three ", "cut");
        EscDuringRequests(2, 3);
        PushLine("/botchat");
        PushLine("/botchat --resume Talk about cats");
        PushLine("/exit");

        string output = await RunAsync();

        // default answers next: it sees ada's line and the user's, signed.
        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains(NeonMarker, SystemText(_chat.Requests[2]));
        Assert.Equal("ada: Two\n\n" + BotChat.UserName + ": Talk about cats", _chat.Requests[2][^1].Text);
        Assert.Contains("Talk about cats", output);

        // One session row holds the turns of both runs.
        using var store = OpenSessions();
        var summary = Assert.Single(store.List(10));
        Assert.Equal(new[] { "default: One.", "ada: Two", "default: Three" }, store.Load(summary.Id)!.Turns.Select(t => t.ReplyText));
    }

    [Fact]
    public async Task BotChat_Resume_WithNothingToResume_IsRefused()
    {
        BotChatFixture();
        PushLine("/botchat --resume");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BotChat.NothingToResumeError, output);
        Assert.Empty(_chat.Requests);
    }
}
