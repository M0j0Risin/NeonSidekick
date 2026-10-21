using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>Welcome splash</c> <c>robot</c> (2026-10-20, the user's ask): the AnimationTrials robot standing just above the draft line in the
/// pane's padding (round 2; over a block under the banner until then), fitted to the room there, gone at the first sent line (round 2;
/// it played a goodbye until then). No picture source, no Web UI entry.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The <c>Splash</c> lines the screen and the pane log while <paramref name="run"/> goes.</summary>
    private static async Task<List<string>> SplashLogAsync(Func<Task> run)
    {
        var lines = new List<string>();
        Action<DiagnosticEvent> capture = e =>
        {
            if (e.Category == "Splash")
            {
                lock (lines)
                {
                    lines.Add(e.Message);
                }
            }
        };
        DiagnosticLog.Emitted += capture;
        try
        {
            await run();
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        return lines;
    }

    /// <summary>The robot mode on the 240-wide pane, <paramref name="height"/> rows tall, the fixture's banner wiping as the app's does, colors on.</summary>
    private void RobotSplashOn(int height = 40)
    {
        _settings.Update(d => { d.TtsOutput = false; d.WelcomeSplashMode = "robot"; });
        _settings.Update(SavedServer);
        PaneOf40Rows();
        _console.Profile.Height = height;
        _console.Profile.Capabilities.ColorSystem = ColorSystem.TrueColor;
    }

    /// <summary>Time for the robot to boot and paint a few frames (its boot is under 0.6 s).</summary>
    private void RobotRuns() => _time.Advance(TimeSpan.FromSeconds(1));

    [Fact]
    public async Task RobotSplash_StandsAboveTheDraftLine_Whole_WithNoPictureAndNoWebEntry()
    {
        RobotSplashOn();
        List<WebEntry> entries = [];
        bool standing = false;
        StepsWhenIdle(
            input =>
            {
                RobotRuns();
                entries = SplashEntries();
                standing = _running!.RobotSplashStanding;
                PushLine(input, "/exit");
            });

        string output = "";
        var log = await SplashLogAsync(async () => output = await RunAsync());

        Assert.True(standing);
        Assert.Contains("Robot shown.", log);
        Assert.Contains("Robot fits: whole", log);
        Assert.Contains("▀", output);   // its half blocks, painted
        Assert.Empty(_splashLoads);       // no picture asked for
        Assert.Empty(entries);            // the page shows nothing for it
    }

    [Theory]
    [InlineData(22, "Robot fits: head")]
    [InlineData(14, "Robot fits: none")]
    public async Task RobotSplash_AShortWindow_ShowsItsHead_OrNothing(int height, string fits)
    {
        RobotSplashOn(height);
        StepsWhenIdle(
            input =>
            {
                RobotRuns();
                PushLine(input, "/exit");
            });

        var log = await SplashLogAsync(async () => await RunAsync());

        Assert.Contains(fits, log);
        Assert.DoesNotContain("Robot fits: whole", log);
    }

    [Fact]
    public async Task RobotSplash_TheFirstLine_TakesItAwayAtOnce_WithNoWipe()
    {
        RobotSplashOn();
        _chat.EnqueueText("Hello.");
        bool gone = false;
        StepsWhenIdle(
            input =>
            {
                RobotRuns();
                PushLine(input, "hi");
            },
            input =>
            {
                gone = !_running!.RobotSplashStanding;   // no time advanced since the send
                PushLine(input, "/exit");
            });

        string output = "";
        var log = await SplashLogAsync(async () => output = await RunAsync());

        Assert.True(gone);
        Assert.Contains("Robot gone at the first line.", log);
        Assert.Equal(0, Refreshes(output));   // no wipe: the startup lines stay as written
        Assert.Equal("hi", UserText(Assert.Single(_chat.Requests)));
    }

    [Fact]
    public async Task RobotSplash_ACommandIsAFirstLineToo()
    {
        RobotSplashOn();
        StepsWhenIdle(
            input =>
            {
                RobotRuns();
                PushLine(input, "/clear");
            },
            input =>
            {
                RobotRuns();
                PushLine(input, "/exit");
            });

        var log = await SplashLogAsync(async () => await RunAsync());

        Assert.False(_running!.RobotSplashStanding);
        Assert.Contains("Robot gone at the first line.", log);
    }

    [Fact]
    public async Task RobotSplash_TheArrowsAndDelete_LeaveItAlone()
    {
        RobotSplashOn();
        StepsWhenIdle(
            input =>
            {
                RobotRuns();
                input.Push(Keys.Right);
            },
            Key(Keys.Left),
            Key(Keys.Delete),
            Key(Keys.Delete),
            input =>
            {
                Assert.True(_running!.RobotSplashStanding);
                PushLine(input, "/exit");
            });

        string output = await RunAsync();

        Assert.Equal(0, Refreshes(output));   // nothing redrew the screen
        Assert.DoesNotContain(ChatScreen.SplashHint, output);
    }

    [Fact]
    public async Task RobotSplash_NoColors_ShowsNothing()
    {
        RobotSplashOn();
        _console.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        PushLine("/exit");

        var log = await SplashLogAsync(async () => await RunAsync());

        Assert.DoesNotContain("Robot shown.", log);
    }

    [Fact]
    public async Task RobotSplash_ASplashCommand_DrawsANewRobot()
    {
        RobotSplashOn();
        StepsWhenIdle(
            input =>
            {
                RobotRuns();
                PushLine(input, "/splash");
            },
            input =>
            {
                RobotRuns();
                PushLine(input, "/exit");
            });

        var log = await SplashLogAsync(async () => await RunAsync());

        Assert.Equal(2, log.Count(l => l == "Robot shown."));
    }
}
