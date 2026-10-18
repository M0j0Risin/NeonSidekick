using NeonSidekick.Tests.Fakes;
using NeonSidekick.Timers;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/timer stopwatch</c> on the screen (2026-10-18, the user's ask: a simple count-up, no laps): started with its notice and its
/// <c>⏱️</c> on the Status bar, said again with its elapsed time, listed by <c>/timer</c>, stopped with its final time; it never rings.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task Stopwatch_Command_Starts_SaysItsTimeAgain_IsListed_StopsWithItsTime_AndNeverRings()
    {
        _settings.Update(d => { d.TtsOutput = false; d.TimerTone = TimerTones.Chime; d.StatusBarItems = ["timers"]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null);
        var input = new ScriptedInput();
        PushLine(input, "/timer stopwatch");
        int waits = 0;
        input.OnWait = () =>
        {
            if (++waits == 1)
            {
                _time.Advance(TimeSpan.FromSeconds(192));
                PushLine(input, "/timer stopwatch");
                PushLine(input, "/timer");
                PushLine(input, "/timer 5m stopwatch");
                PushLine(input, "/timer stop stopwatch");
                PushLine(input, "/exit");
            }
        };

        string output = await RunAsync(input);

        Assert.Contains("  · (⏰ started the stopwatch at 14:05)", output);
        Assert.Contains(TextCells.Spaced(TimerText.StopwatchGlyph + " stopwatch 00:00"), output);   // the Status bar counts up
        Assert.Equal(2, Count(output, "  · ⏰ stopwatch: 3 minutes 12 seconds elapsed (started at 14:05)"));   // said again, then listed
        Assert.Contains("  ✗ Error: a stopwatch called 'stopwatch' is running (3 minutes 12 seconds elapsed); stop it first or use another name", output);
        Assert.Contains("  · (⏰ stopped the stopwatch at 3 minutes 12 seconds)", output);
        Assert.DoesNotContain("  ⏰ ", output);   // no alert line
        Assert.Empty(_tonePlayback.Writes);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Stopwatch_Command_ANamedOne_AndACountdownOfThatName_IsTheDuplicateError()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/timer 10m cooking");
        PushLine("/timer stopwatch cooking");
        PushLine("/timer stopwatch Tea  Break");
        PushLine("/timer stop tea break");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ Error: a timer called 'cooking' is already running (10 minutes left); stop it first or use another name", output);
        Assert.Contains("  · (⏰ started the Tea Break stopwatch at 14:05)", output);
        Assert.Contains("  · (⏰ stopped the Tea Break stopwatch at 0 seconds)", output);
    }
}
