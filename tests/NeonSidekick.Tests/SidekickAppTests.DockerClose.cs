using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The console window's close (2026-10-02, the user's report: its X button never stopped the container): Program's SIGHUP
/// registration calls <see cref="SidekickApp.ConsoleClosing"/>, which asks for <c>Docker containers stop on exit</c>'s stop while the
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

        Assert.Equal(["switch sglang_a", "clear together"], docker.Calls);   // the close's stop; the exit's finds none in use
        Assert.Contains(SidekickApp.DockerServerClosingLine(["sglang_a"]), log);
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
        Assert.DoesNotContain(SidekickApp.DockerServerClosingLine(["sglang_a"]), log);
    }

    [Fact]
    public async Task Headless_ALocalServer_StopsALeftoverContainer_AndSaysSo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 2026-10-26, the user's report: a chosen container an earlier run left running stays beside a server on this computer; it stops
        // first, with the headless Docker notice before the LLM line.
        ServerOn1234("llama");
        _settings.Update(d =>
        {
            d.DockerServers = true;
            d.DockerServerContainers = ["sglang_a"];
            d.LlmUrl = "http://127.0.0.1:1234/v1";
            d.LlmContextLength = 8192;
        });
        var docker = new FakeDockerServers
        {
            List = new Docker.DockerServerList(
                [new Docker.DockerContainer("id-a", "sglang_a", "lmsysorg/sglang:latest", "sha", "running", "Up", 0, [new Docker.DockerPort("0.0.0.0", 30000, 30000, "tcp")], new Dictionary<string, string>(), [], [])],
                [], null),
        };
        var stdout = new StringWriter();
        var app = App(stdin: new StringReader(""), stdout: stdout, dockerServers: () => docker);
        Assert.Equal(0, await app.RunAsync(SidekickOptions.None with { Headless = true }, CancellationToken.None));

        string output = stdout.ToString();
        string notice = "[notice] " + Docker.DockerText.Glyph + " " + Docker.DockerServerText.LeftForThisComputer(["sglang_a"], embedded: false);
        Assert.Equal(["clear port 1234"], docker.Calls);
        Assert.Contains(notice, output);
        Assert.True(output.IndexOf(notice, StringComparison.Ordinal) < output.IndexOf("LLM: http://127.0.0.1:1234", StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task Headless_ACtrlCMidClear_StillSaysTheStopMade()
    {
        // The review's R23: the notice is said in a finally, so a cancel after a stop still prints it.
        ServerOn1234("llama");
        _settings.Update(d =>
        {
            d.DockerServers = true;
            d.DockerServerContainers = ["sglang_a", "vllm_b"];
            d.LlmUrl = "http://127.0.0.1:1234/v1";
            d.LlmContextLength = 8192;
        });
        using var cts = new CancellationTokenSource();
        var docker = new FakeDockerServers
        {
            List = new Docker.DockerServerList(
                [
                    new Docker.DockerContainer("id-a", "sglang_a", "lmsysorg/sglang:latest", "sha", "running", "Up", 0, [new Docker.DockerPort("0.0.0.0", 30000, 30000, "tcp")], new Dictionary<string, string>(), [], []),
                    new Docker.DockerContainer("id-b", "vllm_b", "vllm/vllm-openai:latest", "sha", "running", "Up", 0, [new Docker.DockerPort("0.0.0.0", 8000, 8000, "tcp")], new Dictionary<string, string>(), [], []),
                ],
                [], null),
            StopGate = (name, _) =>
            {
                if (name == "vllm_b")
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            },
        };
        var stdout = new StringWriter();
        var app = App(stdin: new StringReader(""), stdout: stdout, dockerServers: () => docker);
        try
        {
            await app.RunAsync(SidekickOptions.None with { Headless = true }, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Contains("[notice] " + Docker.DockerText.Glyph + " " + Docker.DockerServerText.LeftForThisComputer(["sglang_a"], embedded: false), stdout.ToString());
    }

    /// <summary>A writer that throws on every write once a line holding <paramref name="trip"/> is asked for; it keeps every text asked for.</summary>
    private sealed class BreakingWriter(string trip) : StringWriter
    {
        public List<string> Asked { get; } = [];

        public bool Broken { get; private set; }

        public override void Write(char value) => Take(value.ToString());

        public override void Write(string? value) => Take(value);

        public override void Write(char[] buffer, int index, int count) => Take(new string(buffer, index, count));

        public override Task WriteAsync(char value)
        {
            Take(value.ToString());
            return Task.CompletedTask;
        }

        public override Task WriteAsync(string? value)
        {
            Take(value);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(string? value)
        {
            Take(value + NewLine);
            return Task.CompletedTask;
        }

        public override Task FlushAsync() => Broken ? throw new IOException("The pipe has been ended.") : Task.CompletedTask;

        private void Take(string? value)
        {
            lock (Asked)
            {
                Asked.Add(value ?? "");
            }

            if (Broken || (value?.Contains(trip, StringComparison.Ordinal) ?? false))
            {
                Broken = true;
                throw new IOException("The pipe has been ended.");
            }

            base.Write(value);
        }
    }

    /// <summary>
    /// A headless exit whose stdout broke at its first notice (2026-11-24, review #63): the run still returns, the Docker stop and the cleanup after
    /// it all run, the echo is restored and the log no longer forwards to the writer.
    /// </summary>
    [Fact]
    public async Task Headless_ABrokenPipeAtTheExitsNotice_StillCleansUp()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _settings.Update(d =>
        {
            d.DockerServers = true;
            d.DockerServerContainers = ["sglang_a"];
            d.DockerServerStopOnExit = true;
            d.LlmUrl = "docker:sglang_a";
            d.LlmContextLength = 8192;
        });
        var docker = new FakeDockerServers();
        var stdout = new BreakingWriter(SidekickApp.DockerServerExitNotice("sglang_a"));
        bool echo = DiagnosticLog.EchoToConsole;
        var app = App(stdin: new StringReader(""), stdout: stdout, dockerServers: () => docker);

        Assert.Equal(0, await app.RunAsync(SidekickOptions.None with { Headless = true }, CancellationToken.None));

        Assert.True(stdout.Broken);   // the exit's Docker stop ran and its notice was asked for
        Assert.Equal(echo, DiagnosticLog.EchoToConsole);
        int asked = stdout.Asked.Count;
        DiagnosticLog.Info(ChatScreen.AppCategory, "after the run (review #63)");
        Assert.Equal(asked, stdout.Asked.Count);
    }

    [Fact]
    public void TheWindowsClose_WithNoRunLive_DoesNothing()
    {
        var docker = new FakeDockerServers();
        App(dockerServers: () => docker).ConsoleClosing();
        Assert.Empty(docker.Calls);
    }

    /// <summary>A signal close's give-back with no screen live (headless, a check mode, before the screen): nothing to leave (2026-11-06).</summary>
    [Fact]
    public void GiveBackTerminal_WithNoScreenLive_DoesNothing()
    {
        var stdout = new StringWriter();
        App(stdout: stdout).GiveBackTerminal();
        Assert.Empty(stdout.ToString());
        Assert.Equal("The terminal wasn't given back in time at the close; `reset` in that window restores it.", SidekickApp.TerminalBackLateLogLine);
    }
}
