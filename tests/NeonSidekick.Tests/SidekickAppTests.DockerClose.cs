using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The console window's close (2026-10-02, the user's report: its X button never stopped the container): Program's SIGHUP
/// registration calls <see cref="SidekickApp.ConsoleClosing"/>, which asks for <c>Docker server stop on exit</c>'s stop while the
/// run is still live — proven here over a headless run whose stdin closes the window at its first read.
/// </summary>
public partial class SidekickAppTests
{
    /// <summary>stdin whose first read is the window closing, then the end of input.</summary>
    private sealed class ClosingReader(Func<SidekickApp> app) : TextReader
    {
        private bool _closed;

        public override string? ReadLine()
        {
            if (!_closed)
            {
                _closed = true;
                app().ConsoleClosing();
            }

            return null;
        }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult(ReadLine());
    }

    private async Task<(FakeDockerServers Docker, List<string> Log)> CloseTheWindowAsync(bool stopOnExit)
    {
        _settings.Update(d =>
        {
            d.DockerServers = true;
            d.DockerServerContainers = ["sglang_a"];
            d.DockerServerStopOnExit = stopOnExit;
            d.LlmUrl = "docker:sglang_a";
            d.LlmContextLength = 8192;
        });
        var docker = new FakeDockerServers();
        var log = new List<string>();
        Action<DiagnosticEvent> capture = e => { lock (log) { log.Add(e.Message); } };
        DiagnosticLog.Emitted += capture;
        try
        {
            SidekickApp? app = null;
            app = App(stdin: new ClosingReader(() => app!), stdout: new StringWriter(), dockerServers: () => docker);
            Assert.Equal(0, await app.RunAsync(SidekickOptions.None with { Headless = true }, CancellationToken.None));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        return (docker, log);
    }

    [Fact]
    public async Task TheWindowsClose_StopsTheContainerInUse_WithStopOnExitOn_AndTheExitDoesNotAskAgain()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (docker, log) = await CloseTheWindowAsync(stopOnExit: true);

        Assert.Equal(["switch sglang_a", "stop-all"], docker.Calls);   // the close's stop; the exit's finds none in use
        Assert.Contains(SidekickApp.DockerServerClosingLine("sglang_a"), log);
        Assert.Contains(SidekickApp.DockerServerExitNotice("sglang_a"), log);
    }

    [Fact]
    public async Task TheWindowsClose_LeavesTheContainer_WithStopOnExitOff()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (docker, log) = await CloseTheWindowAsync(stopOnExit: false);

        Assert.Equal(["switch sglang_a"], docker.Calls);
        Assert.DoesNotContain(SidekickApp.DockerServerClosingLine("sglang_a"), log);
    }

    [Fact]
    public void TheWindowsClose_WithNoRunLive_DoesNothing()
    {
        var docker = new FakeDockerServers();
        App(dockerServers: () => docker).ConsoleClosing();
        Assert.Empty(docker.Calls);
    }
}
