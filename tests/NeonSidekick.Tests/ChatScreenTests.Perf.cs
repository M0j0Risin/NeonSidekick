using NeonSidekick.Perf;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The performance bar on the screen (2026-09-29): the setting draws it under the toolbar, the sampler reads the machine only while it shows.</summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task ShowPerformanceBar_DrawsTheReadings_AsTheLastRow_AndOffSamplesNothing()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        _settings.Update(d => d.ShowPerformanceBar = "text");
        _perfSource.Next = new PerfSnapshot(34, 62, null, 91);
        var input = Scripted();
        bool shown = false;
        input.OnWait = () =>
        {
            input.OnWait = null;
            _time.Advance(ScreenPane.Tick);   // the sampler's first reading
            _time.Advance(ScreenPane.Tick);   // the pane's tick draws it
            shown = Output.Contains("CPU 34% · RAM 62% · VRAM 91%", StringComparison.Ordinal);
            _settings.Update(d => d.ShowPerformanceBar = "off");
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
}
