using System.Diagnostics;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// Finds an installed llama.cpp runtime and catalog model under the app's home (2026-09-29), once per assembly:
/// <c>NEONSIDEKICK_TEST_EMBEDDED_MODEL</c> names the catalog model to prefer; otherwise the first installed. Install one by
/// picking it in <c>/server</c> once; nothing here downloads.
/// </summary>
internal static class EmbeddedLlmTestModels
{
    public const string ModelVariable = "NEONSIDEKICK_TEST_EMBEDDED_MODEL";

    /// <summary>
    /// The trait value of the facts that load a model onto the GPU (2026-10-01, the user's call): <c>build.ps1</c>'s test run
    /// leaves them out (<c>--filter Category!=LoadsEmbeddedModel</c>), since the app is routinely open then, its own model
    /// holding VRAM — the VRAM-only fact then fails, and both eat the card meanwhile. A plain <c>dotnet test</c> still runs
    /// them where a model is installed; CI never has one.
    /// </summary>
    public const string LoadsModelCategory = "LoadsEmbeddedModel";

    public static readonly string EmbeddedModelsDirectory;
    public static readonly string LlamaDirectory;
    public static readonly EmbeddedModel? Model;
    public static readonly string Unavailable;

    static EmbeddedLlmTestModels()
    {
        string home = AppSettings.ResolveStorageDirectory(Environment.GetEnvironmentVariable(EnvironmentOverrides.HomeVariable));
        EmbeddedModelsDirectory = Path.Combine(home, "models", "llm");
        LlamaDirectory = Path.Combine(home, "llama");
        var files = new EmbeddedModels(EmbeddedModelsDirectory, LlamaDirectory, new HttpClient());
        bool runtime = Enum.GetValues<LlamaBackend>().Any(files.RuntimeInstalled);
        var wanted = EmbeddedModelCatalog.Find(Environment.GetEnvironmentVariable(ModelVariable));
        Model = runtime ? (wanted is not null && files.State(wanted).IsInstalled ? wanted : files.Installed().FirstOrDefault()) : null;
        Unavailable = Model is null
            ? $"No llama.cpp {LlamaRelease.Tag} runtime and installed embedded model under {home}; pick an Embedded row in /server once."
            : "";
    }
}

/// <summary>A fact that runs only with an embedded runtime and model installed (<see cref="EmbeddedLlmTestModels"/>): an embedded gate, never CI's.</summary>
public sealed class EmbeddedLlmFactAttribute : FactAttribute
{
    public EmbeddedLlmFactAttribute()
    {
        if (EmbeddedLlmTestModels.Model is null)
        {
            Skip = EmbeddedLlmTestModels.Unavailable;
        }
    }
}

/// <summary>The embedded model for real (2026-09-29): llama-server started, asked, shown a picture, swapped, stopped; and the job object's promise.</summary>
public class EmbeddedLlmLiveTests
{
    [Fact]
    public void ClosingTheJob_KillsWhatIsInIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // job objects are Windows'
        }

        var (job, error) = ChildJob.CreateKillOnClose();
        Assert.True(job != 0, error);
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.True(ChildJob.AssignTo(job, child));
            Assert.False(child.HasExited);

            ChildJob.Close(job);

            Assert.True(child.WaitForExit(10_000), "the child outlived its job");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// Code review, 2026-10-11: a child the headless browser started before it joined the job (its crash handler, a relaunched copy)
    /// was outside it, and a snapshot swept in after could still miss what such a child started. Started in the job, the process is
    /// in it before it runs: <c>cmd</c>'s <c>ping</c> is born there, and closing the job ends both.
    /// </summary>
    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Start_PutsTheProcessInTheJobBeforeItRuns_SoItsChildrenAreToo()
    {
        var (job, error) = ChildJob.CreateKillOnClose();
        Assert.True(job != 0, error);
        string log = Path.Combine(Path.GetTempPath(), "NeonSidekick.tests." + Guid.NewGuid().ToString("N") + ".log");
        var before = DateTime.Now.AddSeconds(-1);
        using var parent = ChildJob.Start(job, Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "echo started 1>&2 & ping -n 60 127.0.0.1 >nul"], log, out bool inJob, out string startError);
        var pings = new List<Process>();
        try
        {
            Assert.True(parent is not null, startError);
            Assert.True(inJob);

            // Until ping is running: cmd says so on stderr first.
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(10) && pings.Count == 0)
            {
                Thread.Sleep(50);
                pings.AddRange(Process.GetProcessesByName("PING").Where(ping => Started(ping) >= before));
            }

            Assert.NotEmpty(pings);
            Assert.Contains("started", ReadShared(log), StringComparison.Ordinal);

            ChildJob.Close(job);

            Assert.True(parent!.WaitForExit(10_000), "the process outlived its job");
            Assert.All(pings, ping => Assert.True(ping.WaitForExit(10_000), "a child it started outlived the job"));
        }
        finally
        {
            foreach (var process in pings.Append(parent).OfType<Process>())
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }

            pings.ForEach(ping => ping.Dispose());
            WorkingDirectory.DeleteQuietly(log);
        }

        static DateTime Started(Process process)
        {
            try
            {
                return process.StartTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return DateTime.MinValue;
            }
        }
    }

    /// <summary>What a start in the job hands back works as <c>Process.Start</c>'s did: the exit code after it ends, stderr in its file (cmd splits its line its own way, so the quoting is <see cref="CommandLine_QuotesEachArgument_AsTheCRuntimeSplitsIt"/>'s).</summary>
    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Start_TheProcess_ItsExitCode_AndItsStderr()
    {
        string log = Path.Combine(Path.GetTempPath(), "NeonSidekick.tests." + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var process = ChildJob.Start(0, Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "echo oops 1>&2 & exit 7"], log, out bool inJob, out string error);
            Assert.True(process is not null, error);
            Assert.False(inJob);   // no job given
            Assert.True(process!.WaitForExit(10_000));
            Assert.Equal(7, process.ExitCode);
            Assert.Contains("oops", ReadShared(log), StringComparison.Ordinal);
        }
        finally
        {
            WorkingDirectory.DeleteQuietly(log);
        }

        Assert.Null(ChildJob.Start(0, Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".exe"), [], log, out _, out string missing));
        Assert.Contains("CreateProcess failed", missing, StringComparison.Ordinal);
        WorkingDirectory.DeleteQuietly(log);
    }

    /// <summary>
    /// Code review, later on 2026-10-11: the stderr file the caller already holds delete-on-close (the headless browser's, so a crash
    /// leaves none behind) is written through and gone with the caller's handle; and the start shares .NET's process-start lock, which
    /// the smoke's preparation reaches too.
    /// </summary>
    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Start_IntoAHeldDeleteOnCloseFile_UnderTheProcessStartLock()
    {
        Assert.NotNull(ChildJob.ProcessStartLock());
        var (prepared, why) = ChildJob.ProbeAttributeList();
        Assert.True(prepared, why);

        string log = Path.Combine(Path.GetTempPath(), "NeonSidekick.tests." + Guid.NewGuid().ToString("N") + ".log");
        var held = new FileStream(log, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
        using (held)
        {
            using var process = ChildJob.Start(0, Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "echo kept 1>&2"], log, out _, out string error);
            Assert.True(process is not null, error);
            Assert.True(process!.WaitForExit(10_000));
            using var reader = new StreamReader(held, leaveOpen: true);
            Assert.Contains("kept", reader.ReadToEnd(), StringComparison.Ordinal);
        }

        Assert.False(File.Exists(log));
    }

    /// <summary>The command line as the C runtime splits it back into the arguments (code review, 2026-10-11): <c>ProcessStartInfo.ArgumentList</c>'s quoting.</summary>
    [Fact]
    public void CommandLine_QuotesEachArgument_AsTheCRuntimeSplitsIt()
    {
        Assert.Equal("\"C:\\b r\\x.exe\"", ChildJob.CommandLine(@"C:\b r\x.exe", []));
        Assert.Equal(
            "\"x.exe\" --a=1 \"two words\" \"\" \"say \\\"hi\\\"\" \"C:\\dir with space\\\\\" C:\\plain\\ \"a\\\\\\\"b\"",
            ChildJob.CommandLine("x.exe", ["--a=1", "two words", "", "say \"hi\"", @"C:\dir with space\", @"C:\plain\", "a\\\"b"]));
    }

    /// <summary>A process gone before it could join the job runs nowhere: no warning that it runs outside (code review, 2026-10-11).</summary>
    [WindowsFact]
    public void AssignTo_AProcessAlreadyGone_WarnsOfNothing()
    {
        var (job, error) = ChildJob.CreateKillOnClose();
        Assert.True(job != 0, error);
        var warnings = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Category == "EmbeddedLlm" && e.Level >= NeonSidekick.Diagnostics.DiagnosticLevel.Warning) warnings.Add(e.Message); };
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            Assert.True(gone.WaitForExit(10_000));
            ChildJob.AssignTo(job, gone);
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
            ChildJob.Close(job);
        }

        Assert.Empty(warnings);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void TheSmokeProbes_PassHere()
    {
        var job = SmokeChecks.ProbeJobObject();
        Assert.True(job.Passed, job.Detail);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("limit flags 0x2000", job.Detail);
        }

        // The Mac's (2026-10-07): the tar unpack, Metal's working set and the records; each says skipped where it does not apply.
        foreach (var probe in new[] { SmokeChecks.ProbeTarUnpack(), SmokeChecks.ProbeMetal(), SmokeChecks.ProbeRecords() })
        {
            Assert.True(probe.Passed, probe.Name + ": " + probe.Detail);
        }

        string home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string models = Path.Combine(home, "models");
            var empty = SmokeChecks.ProbeLlamaServer(models);
            Assert.True(empty.Passed);
            Assert.StartsWith("not exercised:", empty.Detail);

            // A Mac looks at its Metal folder alone, a Mach-O executable (2026-10-07); Windows at its own, a PE one.
            var backend = OperatingSystem.IsMacOS() ? LlamaBackend.Metal : LlamaBackend.Vulkan;
            string folder = LlamaRelease.Folder(Path.Combine(home, "llama"), backend);
            Directory.CreateDirectory(folder);
            string server = Path.Combine(folder, LlamaRelease.ServerFile(backend));
            if (backend == LlamaBackend.Metal)
            {
                File.WriteAllBytes(server, [0xCF, 0xFA, 0xED, 0xFE, 0x0C]);
            }
            else
            {
                File.WriteAllText(server, "MZ fake");
            }

            var partial = SmokeChecks.ProbeLlamaServer(models);
            Assert.False(partial.Passed);
            Assert.Contains("is missing some of", partial.Detail);

            foreach (var file in LlamaRelease.RequiredFiles(backend).Skip(1))
            {
                File.WriteAllText(Path.Combine(folder, file), "x");
            }

            var complete = SmokeChecks.ProbeLlamaServer(models);
            Assert.True(complete.Passed, complete.Detail);
            Assert.Equal("llama.cpp b11258: " + LlamaRelease.Name(backend) + " complete", complete.Detail);

            File.WriteAllText(server, "not a PE");
            Assert.False(SmokeChecks.ProbeLlamaServer(models).Passed);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The host alone over any llama-server and any GGUF (2026-09-29): <c>NEONSIDEKICK_TEST_LLAMA_EXE</c> and
    /// <c>NEONSIDEKICK_TEST_TINY_GGUF</c> (llama.cpp's <c>stories15M-q4_0.gguf</c>, 19 MB, is enough), so the process
    /// half — arguments, port, key, health, reuse, restart, stop — is proven without a catalog model's gigabytes.
    /// </summary>
    [Fact]
    public async Task TheHost_RunsAnyGguf_WithItsKey_AndStopsIt()
    {
        string? exe = Environment.GetEnvironmentVariable("NEONSIDEKICK_TEST_LLAMA_EXE");
        string? gguf = Environment.GetEnvironmentVariable("NEONSIDEKICK_TEST_TINY_GGUF");
        if (!File.Exists(exe) || !File.Exists(gguf))
        {
            return;   // an embedded gate: set both variables to run it
        }

        var model = EmbeddedModelCatalog.Models[0] with { Id = "tiny" };
        var launch = new LlamaLaunch(exe, LlamaBackend.Cpu, gguf, null, "tiny", 512, "0", model.Sampling);
        await using var host = new LlamaServerHost();
        var labels = new List<string>();

        var info = await host.EnsureRunningAsync(launch, model, labels.Add, CancellationToken.None);

        Assert.Equal("tiny", info.ModelId);
        Assert.Equal(info, host.Running);
        using (var http = new HttpClient())
        {
            var anonymous = await http.GetAsync(LlmEndpoint.ModelsUrl(info.BaseUrl));
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);   // the per-start key guards it
        }

        using (var client = Client(info))
        {
            var reply = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Once upon a time")], new ChatOptions { MaxOutputTokens = 8 });
            Assert.False(string.IsNullOrWhiteSpace(reply.Text));
        }

        // The reasoning estimate's /tokenize (2026-09-29): this build answers it with the per-start key.
        using (var tokenizing = new OpenAICompatibleChatClient(new LlmEndpoint(EmbeddedEndpoint.BaseUrl, info.ModelId, info.ApiKey, "test") { LiveUrl = info.BaseUrl }, TimeSpan.FromSeconds(30), new HttpClient()))
        {
            long counted = await tokenizing.EstimateReasoningAsync("Once upon a time, there was a little dog named Spot.", ReasoningEstimate.Tokenize, CancellationToken.None);
            Assert.InRange(counted, 5, 30);
            Assert.Equal(counted, await tokenizing.EstimateReasoningAsync("Once upon a time, there was a little dog named Spot.", ReasoningEstimate.Tokenize, CancellationToken.None));   // asked again: it answered
        }

        Assert.Same(info, await host.EnsureRunningAsync(launch, model, null, CancellationToken.None));   // reused
        var bigger = await host.EnsureRunningAsync(launch with { ContextSize = 1024 }, model, null, CancellationToken.None);
        Assert.NotEqual(info.Port, bigger.Port);                                                            // restarted

        host.Stop();
        Assert.Null(host.Running);
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await Assert.ThrowsAnyAsync<Exception>(() => probe.GetAsync(new Uri($"http://127.0.0.1:{bigger.Port}/health")));
    }

    [Fact]
    public async Task TheHost_ReportsAnExeThatExitsAtOnce()
    {
        string? exe = Environment.GetEnvironmentVariable("NEONSIDEKICK_TEST_LLAMA_EXE");
        if (!File.Exists(exe))
        {
            return;
        }

        var model = EmbeddedModelCatalog.Models[0];
        await using var host = new LlamaServerHost();

        var ex = await Assert.ThrowsAsync<EmbeddedLlmException>(() => host.EnsureRunningAsync(
            new LlamaLaunch(exe, LlamaBackend.Cpu, Path.Combine(Path.GetTempPath(), "no-such-model.gguf"), null, "x", 512, "0", model.Sampling), model, null, CancellationToken.None));

        Assert.StartsWith("the embedded model did not start: llama-server exited with code ", ex.Message);
        Assert.Null(host.Running);
    }

    private static EmbeddedLlmService Service(out LlamaServerHost host)
    {
        host = new LlamaServerHost();
        return new EmbeddedLlmService(new EmbeddedModels(EmbeddedLlmTestModels.EmbeddedModelsDirectory, EmbeddedLlmTestModels.LlamaDirectory, new HttpClient()), host);
    }

    private static OpenAICompatibleChatClient Client(EmbeddedServerInfo info) =>
        new(new LlmEndpoint(EmbeddedEndpoint.BaseUrl, info.ModelId, info.ApiKey, EmbeddedLlmText.Source(info)) { LiveUrl = info.BaseUrl }, TimeSpan.FromMinutes(2), reasoningEstimate: () => ReasoningEstimate.Tokenize);

    [EmbeddedLlmFact]
    [Trait("Category", EmbeddedLlmTestModels.LoadsModelCategory)]
    public async Task TheServer_Starts_Answers_ReadsAPicture_IsReused_AndStops()
    {
        var model = EmbeddedLlmTestModels.Model!;
        await using var service = Service(out var host);
        var settings = new AppSettingsData { EmbeddedContextSize = 8192 };

        var info = await service.StartAsync(model, settings, null, CancellationToken.None);

        Assert.Equal(model.Id, info.ModelId);
        Assert.Equal(Files.ImageCodecs.Available, info.Vision);   // a Mac loads no projector while it cannot read pictures (2026-10-07)
        using (var client = Client(info))
        {
            // Gemma 4 thinks by default (llama.cpp's --reasoning auto): a budget of 64 was spent on the thinking alone (finish "length", no answer).
            var reply = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Reply with the single word: pong")], new ChatOptions { MaxOutputTokens = 1024 });
            Assert.Contains("pong", reply.Text, StringComparison.OrdinalIgnoreCase);
        }

        if (Files.ImageCodecs.Available)
        {
            using var client = Client(info);

            var picture = ImageFile.Load(SmokeChecks.SolidBmp(64, 64), "red.bmp", out string? error);
            Assert.True(picture is not null, error);
            var seen = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, [new TextContent("What one color fills this picture? Answer with the color alone."), new DataContent(picture.Bytes, picture.MediaType)])],
                new ChatOptions { MaxOutputTokens = 1024 });
            string thought = string.Concat(seen.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().Select(r => r.Text));
            Assert.False(string.IsNullOrWhiteSpace(seen.Text), $"no answer: finish {seen.FinishReason}, output {seen.Usage?.OutputTokenCount}, thinking {thought.Length} chars: {thought[..Math.Min(200, thought.Length)]}");

            // llama.cpp counts no reasoning (2026-09-29): the thinking's count is the app's, through the server's /tokenize.
            if (thought.Length > 0)
            {
                Assert.InRange(seen.Usage!.ReasoningTokenCount!.Value, 1, seen.Usage.OutputTokenCount!.Value);
                Assert.True(TokenUsage.From(seen.Usage, TimeSpan.Zero, TimeSpan.Zero).ReasoningEstimated);
            }
        }

        // The same launch again: the same server, no reload.
        var again = await service.StartAsync(model, settings, null, CancellationToken.None);
        Assert.Equal(info, again);

        // Another context size: a restart on another port.
        var restarted = await service.StartAsync(model, new AppSettingsData { EmbeddedContextSize = 4096, EmbeddedVision = false }, null, CancellationToken.None);
        Assert.NotEqual(info.Port, restarted.Port);
        Assert.False(restarted.Vision);

        service.Stop();
        Assert.Null(host.Running);
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await Assert.ThrowsAnyAsync<Exception>(() => probe.GetAsync(new Uri($"http://127.0.0.1:{restarted.Port}/health")));
    }

    /// <summary>
    /// Embedded VRAM only for real (2026-10-01, the user's ask), on the model the spike that day measured: Qwen3.8 27B NVFP4
    /// on a 32 GB card does not fit with 262144 tokens and every layer on the GPU, and does with the context left to fit
    /// (137984 tokens). Runs where that model and CUDA are installed and the card is at most 32 GB; else returns.
    /// </summary>
    [EmbeddedLlmFact]
    [Trait("Category", EmbeddedLlmTestModels.LoadsModelCategory)]
    public async Task VramOnly_RefusesALoadThatDoesNotFit_AndKeepsOneThatDoes()
    {
        var model = EmbeddedModelCatalog.Find("qwen3.8-27b-nvfp4-highest")!;
        var files = new EmbeddedModels(EmbeddedLlmTestModels.EmbeddedModelsDirectory, EmbeddedLlmTestModels.LlamaDirectory, new HttpClient());
        if (!files.State(model).IsInstalled || !files.RuntimeInstalled(LlamaBackend.Cuda) || NeonSidekick.Perf.GpuMemory.DedicatedBytes() is not (> 0 and <= 34L << 30))
        {
            return;   // an embedded gate: the model, CUDA and a card it overflows
        }

        var host = new LlamaServerHost();
        await using var service = new EmbeddedLlmService(files, host, _ => new BackendChoice(LlamaBackend.Cuda, "test"));

        var ex = await Assert.ThrowsAsync<EmbeddedLlmException>(() => service.StartAsync(model, new AppSettingsData { EmbeddedVramOnly = true, EmbeddedContextSize = 262_144, EmbeddedVision = false }, null, CancellationToken.None));
        Assert.True(ex.VramOnlyRefused, ex.Message);
        Assert.StartsWith(EmbeddedLlmText.StartFailed("it "), ex.Message);
        Assert.Null(host.Running);

        var info = await service.StartAsync(model, new AppSettingsData { EmbeddedVramOnly = true, EmbeddedVision = false }, null, CancellationToken.None);
        Assert.Equal(model.Id, info.ModelId);
        service.Stop();
        Assert.Null(host.Running);
    }
}
