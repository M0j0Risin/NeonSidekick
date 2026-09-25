using NeonSidekick.App;

namespace NeonSidekick.Tests;

/// <summary>The <c>/botchat</c> ESC ladder (2026-09-25): the voice, then the bot replying, then the chat.</summary>
public class BotEscLadderTests
{
    [Fact]
    public void SpeechOn_TheVoice_ThenTheBot_ThenTheChat_BeforeTheNextBotShows()
    {
        var ladder = new BotEscLadder();
        int a = ladder.BeginBot();
        Assert.Equal(BotPress.StopVoice, ladder.Press(a, shown: true, voiceAudible: true, responding: true));
        Assert.Equal(BotPress.SkipBot, ladder.Press(a, shown: true, voiceAudible: false, responding: true));
        Assert.True(ladder.Skipped(a));
        Assert.False(ladder.EndRequested);

        int b = ladder.BeginBot();
        Assert.False(ladder.Skipped(b));
        Assert.Equal(BotPress.EndChat, ladder.Press(b, shown: false, voiceAudible: false, responding: true));
        Assert.True(ladder.EndRequested);
    }

    [Fact]
    public void SpeechOff_TheBot_ThenTheChat()
    {
        var ladder = new BotEscLadder();
        int a = ladder.BeginBot();
        Assert.Equal(BotPress.SkipBot, ladder.Press(a, shown: true, voiceAudible: false, responding: true));
        int b = ladder.BeginBot();
        Assert.Equal(BotPress.EndChat, ladder.Press(b, shown: false, voiceAudible: false, responding: true));
    }

    [Fact]
    public void TheArm_EndsOnceTheNextBotShows_ThenAPressCutsThatBot()
    {
        var ladder = new BotEscLadder();
        int a = ladder.BeginBot();
        ladder.Press(a, shown: true, voiceAudible: false, responding: true);
        int b = ladder.BeginBot();

        Assert.Equal(BotPress.SkipBot, ladder.Press(b, shown: true, voiceAudible: false, responding: true));
        Assert.True(ladder.Skipped(b));
        Assert.False(ladder.EndRequested);
    }

    [Fact]
    public void AVoiceStoppedInTheSpeechWait_ThenAPressThere_OrAtTheNextBotBeforeItShows_EndsTheChat()
    {
        var ladder = new BotEscLadder();
        int a = ladder.BeginBot();
        // The reply is written; its voice is heard out in the wait.
        Assert.Equal(BotPress.StopVoice, ladder.Press(a, shown: true, voiceAudible: true, responding: false));
        Assert.Equal(BotPress.EndChat, ladder.Press(a, shown: true, voiceAudible: false, responding: false));

        var other = new BotEscLadder();
        int c = other.BeginBot();
        other.Press(c, shown: true, voiceAudible: true, responding: false);
        int d = other.BeginBot();
        Assert.Equal(BotPress.EndChat, other.Press(d, shown: false, voiceAudible: false, responding: true));
    }

    [Fact]
    public void AFirstPress_BeforeAnythingShows_CutsThatBot()
    {
        // The held reply (Botchat image async off): nothing on the screen yet, nothing armed.
        var ladder = new BotEscLadder();
        int a = ladder.BeginBot();
        Assert.Equal(BotPress.SkipBot, ladder.Press(a, shown: false, voiceAudible: false, responding: true));
    }

    [Fact]
    public void TheNextBotsVoice_IsStoppedFirst_EvenArmed()
    {
        var ladder = new BotEscLadder();
        int a = ladder.BeginBot();
        ladder.Press(a, shown: true, voiceAudible: false, responding: true);
        int b = ladder.BeginBot();
        Assert.Equal(BotPress.StopVoice, ladder.Press(b, shown: true, voiceAudible: true, responding: true));
    }
}
