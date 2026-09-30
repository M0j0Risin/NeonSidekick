using NeonSidekick.App;
using NeonSidekick.Perf;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>The performance bar on the screen (2026-09-29): the setting draws it under the toolbar, the sampler reads the machine only while it shows; since 2026-09-30 the checked meters alone.</summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task ShowPerformanceBar_DrawsTheCheckedMeters_AsTheLastRow_AndNoneSamplesNothing()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        _settings.Update(d => d.PerformanceBarItems = ["cpu", "gpu", "vram"]);
        _perfSource.Next = new PerfSnapshot(34, 62, null, 91);
        var input = Scripted();
        bool shown = false;
        input.OnWait = () =>
        {
            input.OnWait = null;
            _time.Advance(ScreenPane.Tick);   // the sampler's first reading
            _time.Advance(ScreenPane.Tick);   // the pane's tick draws it
            shown = Output.Contains("CPU  34% · VRAM  91%", StringComparison.Ordinal);   // RAM unchecked, GPU unread
            _settings.Update(d => d.PerformanceBarItems = null);
            _time.Advance(ScreenPane.Tick);   // the row goes, the sampler stops
            PushLine(input, "/exit");
        };

        await RunAsync();

        Assert.True(shown);   // no GPU load read: its meter left out
        Assert.True(_perfSource.Disposed);
        int samples = _perfSource.Samples;
        Assert.True(samples >= 1);
        _time.Advance(PerfSampler.Interval * 3);
        Assert.Equal(samples, _perfSource.Samples);
    }

    /// <summary>
    /// <c>/perf</c> (later on 2026-09-29, the user's ask; the checklist's since 2026-09-30): bare it shows the bar (CPU, RAM,
    /// GPU and VRAM the first time) and hides it again, keeping its meters for the next show; a look sets it; anything else is
    /// the usage error. Never a turn.
    /// </summary>
    [Fact]
    public async Task Perf_Toggles_KeepingTheMeters_AndALookSetsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        var shown = new List<string>();
        Action<ScriptedInput> Then(string line) => input =>
        {
            shown.Add(PerfBarItems.Value(_settings.Current.PerformanceBarItems, _settings.Current.PerformanceBarLook));   // what the line before left
            PushLine(input, line);
        };
        StepsWhenIdle(
            Line("/perf"),
            Then("/perf gauge"),
            Then("/perf"),
            Then("/perf"),
            Then("/perf bars"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal(["CPU, RAM, GPU, VRAM · text", "CPU, RAM, GPU, VRAM · gauge", "off", "CPU, RAM, GPU, VRAM · gauge"], shown);
        Assert.Contains(PerfText.BarNotice("text"), output);
        Assert.Contains(PerfText.BarNotice("off"), output);
        Assert.Contains(PerfText.BarNotice("gauge"), output);
        Assert.Contains(PerfText.UsageError, output);
        Assert.Equal("gauge", _settings.Current.PerformanceBarLook);
        Assert.Equal(["cpu", "ram", "gpu", "vram"], _settings.Current.PerformanceBarItems);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// The toolbar's 🪪 and 📈 (later on 2026-09-29, the user's ask), checked alone: the ID card at column 0 opens the profile
    /// picker, the rising chart at 3 shows the performance bar in its look — no transcript row for either.
    /// </summary>
    [Fact]
    public async Task ToolbarItems_TheIdCard_OpensTheProfilePicker_AndTheRisingChart_TogglesTheBar()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = ["profile", "perf"]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the toolbar at 103
        StepsWhenIdle(
            input => { input.PushClick(0, 103); input.PushClick(1, 103); },   // 🪪: the profile picker
            Key(Keys.Escape),
            input => { input.PushClick(3, 103); input.PushClick(3, 103); },   // 📈: the bar on, its four first meters in text
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("\n" + ScreenPane.ToolbarRow(ChatScreen.ProfileToolGlyph + " " + ChatScreen.PerfToolGlyph, "", 239), output);
        Assert.Contains(Titled(SettingsMenu.ProfileTitle), output);
        Assert.Equal(["cpu", "ram", "gpu", "vram"], _settings.Current.PerformanceBarItems);
        Assert.Contains(PerfText.BarNotice("text"), output);
        Assert.All(new[] { "/profile", "/perf" }, word => Assert.DoesNotContain("› " + word, output));
        Assert.Empty(_chat.Requests);
    }
}
