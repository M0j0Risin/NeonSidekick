using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>Voice input during a <c>/botchat</c> (2026-10-02): push-to-talk cuts the bot short, listens, and the next bot answers the user.</summary>
public partial class ChatScreenTests
{
    /// <summary>F4 under the first bot's reply, as it streams; ESC twice under the third request ends the chat.</summary>
    private void PushToTalkUnderTheFirstBot()
    {
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == 1 && i == 1)
            {
                _console.Input.PushKey(Keys.F4);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
            else if (_chat.Requests.Count == 3 && i == 1)
            {
                _console.Input.PushKey(Keys.Escape);
                _console.Input.PushKey(Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
    }

    [Fact]
    public async Task BotChat_PushToTalk_CutsTheBotShort_Listens_AndTheNextBotAnswersTheUser()
    {
        BotChatFixture();
        VoiceOn();
        SpeakTwoBuffers();
        _chat.EnqueueText("Hello ", "from ", "Neon.");
        _chat.EnqueueText("Hello from Ada.");
        _chat.EnqueueText("Neon ", "again");
        PushToTalkUnderTheFirstBot();
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("› hello", output);
        Assert.Equal(3, _chat.Requests.Count);
        var ada = _chat.Requests[1];
        Assert.Contains(AdaMarker, SystemText(ada));
        Assert.Contains(BotChat.UserName + ": hello", ada[^1].Text);   // the user's spoken line, the newest the next bot sees
        Assert.Equal(new[] { "start", "stop" }, _capture.Log);   // one listen
    }

    [Fact]
    public async Task BotChat_PushToTalk_WhileABotSpeaks_StopsItsVoice_ThenListens()
    {
        BotChatFixture();
        VoiceOn();
        SpeakTwoBuffers();
        _settings.Update(d => d.TtsOutput = true);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _playback.HoldBytes = true;   // the voice never ends on its own: only the key can end the wait
        bool pressed = false;
        _synth.OnSynthesize = async (text, _) =>
        {
            if (!pressed)
            {
                pressed = true;
                await Task.Delay(200);   // the text has ended: the chat is in the speech wait
                _console.Input.PushKey(Keys.F4);
            }
        };
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Hello from Ada.");
        _chat.BeforeUpdate = (i, _) =>
        {
            if (i == 0 && _chat.Requests.Count == 2)
            {
                PushLine("/exit");   // ends the chat under the second reply
            }

            return Task.CompletedTask;
        };
        PushLine("/botchat");

        string output = await RunAsync();

        Assert.True(_playback.Stopped >= 1);
        Assert.Contains("› hello", output);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Contains(BotChat.UserName + ": hello", _chat.Requests[1][^1].Text);
        Assert.True(output.IndexOf("› hello", StringComparison.Ordinal) < output.IndexOf(TranscriptRenderer.SpeakerGlyph + "ada", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BotChat_PushToTalk_WithVoiceOff_SaysHowToTurnItOn_AndTheChatGoesOn()
    {
        BotChatFixture();
        _chat.EnqueueText("Hello ", "from ", "Neon.");
        _chat.EnqueueText("Hello from Ada.");
        _chat.EnqueueText("Neon ", "again");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == 1 && i == 1)
            {
                _console.Input.PushKey(Keys.F4);
                await Task.Delay(40, CancellationToken.None);
            }
            else if (_chat.Requests.Count == 3 && i == 1)
            {
                _console.Input.PushKey(Keys.Escape);
                _console.Input.PushKey(Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.VoiceOffHint, output);
        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains("Neon.", output);   // the first bot was not cut short
        Assert.Empty(_capture.Log);
    }

    [Fact]
    public async Task BotChat_TheWakePhrase_WithTtsOff_CutsTheBotShort_AndTheUserIsHeard()
    {
        BotChatFixture();   // TTS off
        WakeOn();
        bool armedUnderTheReply = false;
        _wake.FinalAfterBuffers = 2;
        _wake.Text = "neon";
        _recognizer.Text = "Neon. Hello bots.";
        _vad.EndAfterBuffers = 2;
        _capture.OnStart = (c, _) =>
        {
            // Only the listen after the phrase hears the request; the chat's own arm hears nothing until the reply runs.
            if (_chat.Requests.Count == 1 && !_voice.WakeArmed)
            {
                c.Deliver(c.Silence(50), 1600);
                c.Deliver(c.Silence(50), 1600);
            }

            return Task.CompletedTask;
        };
        _chat.EnqueueText("Hello ", "from ", "Neon.");
        _chat.EnqueueText("Hello from Ada.");
        _chat.EnqueueText("Neon ", "again");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if ((_chat.Requests.Count == 1 && i == 1) || (_chat.Requests.Count == 3 && i == 1))
            {
                if (_chat.Requests.Count == 1)
                {
                    armedUnderTheReply = _voice.WakeArmed;
                    await DeliverHeardAsync();   // the phrase, heard under the first bot's reply: two buffers (FinalAfterBuffers)
                    await DeliverHeardAsync();
                }
                else
                {
                    _console.Input.PushKey(Keys.Escape);
                    _console.Input.PushKey(Keys.Escape);
                }

                // Bounded: a phrase that never lands fails the test rather than hanging it.
                for (int wait = 0; wait < 1000 && !ct.IsCancellationRequested; wait++)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.True(armedUnderTheReply, "the chat's wake phrase was not armed under the reply");
        Assert.Contains("› Hello bots.", output);
        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains(BotChat.UserName + ": Hello bots.", _chat.Requests[1][^1].Text);
    }

    [Fact]
    public void BotWake_OnlyWithNoVoicePlaying_AndTheWakeWordReady()
    {
        Assert.True(ChatScreen.BotWakeAllowed(speaking: false, wakeReady: true));
        Assert.False(ChatScreen.BotWakeAllowed(speaking: true, wakeReady: true));   // TTS on: push-to-talk alone
        Assert.False(ChatScreen.BotWakeAllowed(speaking: false, wakeReady: false));
    }

    [Fact]
    public void ALadderSkip_ByTheUsersTalk_ArmsNothing_SoTheNextEscIsAFirstPress()
    {
        var ladder = new BotEscLadder();
        int first = ladder.BeginBot();
        ladder.Skip(first);
        int second = ladder.BeginBot();

        Assert.True(ladder.Skipped(first));
        Assert.False(ladder.Skipped(second));
        Assert.Equal(BotPress.SkipBot, ladder.Press(second, shown: false, voiceAudible: false, responding: true));   // not the chat's end
        Assert.False(ladder.EndRequested);
    }
}
