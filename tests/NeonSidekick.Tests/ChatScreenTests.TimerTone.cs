using NeonSidekick.Tests.Fakes;
using NeonSidekick.Timers;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The timer tone on the screen (2026-10-18, the user's ask): <i>Timer tone</i> rings as a countdown goes off at the idle line and at
/// each repeat, mid-reply too, with TTS off or before the spoken alert; <c>silent</c>, the default, plays nothing.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The timer tone's device: every play opens it, writes its clip and stops it; it drains at once.</summary>
    private readonly FakeAudioPlayback _tonePlayback = new();

    private static byte[] Clip(string tone) => TimerTones.Render(tone, TimerTonePlayer.Format);

    /// <summary>A 10-minute timer that goes off at the idle line, rings once more a minute later, and is silenced with Esc.</summary>
    private async Task<string> RingTwiceAsync()
    {
        var input = new ScriptedInput();
        PushLine(input, "/timer 10m cooking");
        int waits = 0;
        input.OnWait = () =>
        {
            switch (++waits)
            {
                case 1:
                    _time.Advance(TimeSpan.FromMinutes(10));
                    break;
                case 2:
                    _time.Advance(TimeSpan.FromSeconds(60));
                    break;
                case 3:
                    input.Push(Keys.Escape);
                    break;
                case 4:
                    _time.Advance(TimeSpan.FromMinutes(5));   // silenced: no third ring
                    PushLine(input, "/exit");
                    break;
            }
        };

        return await RunAsync(input);
    }

    [Fact]
    public async Task TimerTone_Chime_RingsAtTheIdleLine_AndAtTheRepeat_WithTtsOff()
    {
        _settings.Update(d => { d.TtsOutput = false; d.TimerTone = TimerTones.Chime; });

        string output = await RingTwiceAsync();

        Assert.Equal(2, output.Split("  ⏰ ").Length - 1);
        Assert.Equal([Clip(TimerTones.Chime), Clip(TimerTones.Chime)], _tonePlayback.Writes);
        Assert.Equal(2, _tonePlayback.Stopped);
        Assert.Empty(_synth.Spoken);
    }

    [Fact]
    public async Task TimerTone_Silent_TheDefault_PlaysNothing()
    {
        _settings.Update(d => d.TtsOutput = false);

        await RingTwiceAsync();

        Assert.Equal(TimerTones.Silent, _settings.Current.TimerTone);
        Assert.Equal(0, _tonePlayback.Started);
        Assert.Empty(_tonePlayback.Writes);
    }

    [Fact]
    public async Task TimerTone_MidReply_RingsAtOnce()
    {
        _settings.Update(d => d.TimerTone = TimerTones.Beep);
        _chat.EnqueueText("One. ", "Two. ", "Three.");
        _chat.BeforeUpdate = (i, _) =>
        {
            if (i == 1)
            {
                _time.Advance(TimeSpan.FromMinutes(10));
            }

            return Task.CompletedTask;
        };
        LinesWhenIdle("/timer 10m cooking", "hi", "/exit");

        string output = await RunAsync();

        Assert.Contains("⏰ " + CookingAlert, output);
        Assert.Equal(Clip(TimerTones.Beep), Assert.Single(_tonePlayback.Writes));
        Assert.Equal(new[] { "One.", "Two.", "Three." }, _synth.SpokenText);   // the reply's speech as before
    }

    [Fact]
    public async Task TimerTone_WithTtsOn_RingsAndTheAlertIsStillSpoken()
    {
        _settings.Update(d => d.TimerTone = TimerTones.Bell);
        _playback.HoldBytes = true;   // the spoken alert is a tail under the read: held, so only Enter ends it
        var input = new ScriptedInput();
        PushLine(input, "/timer 10m cooking");
        int waits = 0;
        input.OnWait = () =>
        {
            switch (++waits)
            {
                case 1:
                    _time.Advance(TimeSpan.FromMinutes(10));
                    break;
                case 2:
                    _ = Task.Run(async () =>
                    {
                        while (_synth.Spoken.Count == 0)
                        {
                            await Task.Delay(5);
                        }

                        input.Push(Keys.Enter);
                    });
                    break;
                case 3:
                    PushLine(input, "/exit");
                    break;
            }
        };

        await RunAsync(input);

        Assert.Equal(Clip(TimerTones.Bell), Assert.Single(_tonePlayback.Writes));
        Assert.Equal(new[] { "The cooking timer is done." }, _synth.SpokenText);
    }
}
