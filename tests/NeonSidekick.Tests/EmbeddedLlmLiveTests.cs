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

    [Fact]
    public void TheSmokeProbes_PassHere()
    {
        var job = SmokeChecks.ProbeJobObject();
        Assert.True(job.Passed, job.Detail);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("limit flags 0x2000", job.Detail);
        }

        string home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string models = Path.Combine(home, "models");
            var empty = SmokeChecks.ProbeLlamaServer(models);
            Assert.True(empty.Passed);
            Assert.StartsWith("not exercised:", empty.Detail);

            string folder = LlamaRelease.Folder(Path.Combine(home, "llama"), LlamaBackend.Vulkan);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, LlamaRelease.ServerExecutable), "MZ fake");
            var partial = SmokeChecks.ProbeLlamaServer(models);
            Assert.False(partial.Passed);
            Assert.Contains("is missing some of", partial.Detail);

            foreach (var file in LlamaRelease.RequiredFiles(LlamaBackend.Vulkan).Skip(1))
            {
                File.WriteAllText(Path.Combine(folder, file), "x");
            }

            var complete = SmokeChecks.ProbeLlamaServer(models);
            Assert.True(complete.Passed, complete.Detail);
            Assert.Equal("llama.cpp b11258: vulkan complete", complete.Detail);

            File.WriteAllText(Path.Combine(folder, LlamaRelease.ServerExecutable), "not a PE");
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
    public async Task TheServer_Starts_Answers_ReadsAPicture_IsReused_AndStops()
    {
        var model = EmbeddedLlmTestModels.Model!;
        await using var service = Service(out var host);
        var settings = new AppSettingsData { EmbeddedContextSize = 8192 };

        var info = await service.StartAsync(model, settings, null, CancellationToken.None);

        Assert.Equal(model.Id, info.ModelId);
        Assert.True(info.Vision);
        using (var client = Client(info))
        {
            // Gemma 4 thinks by default (llama.cpp's --reasoning auto): a budget of 64 was spent on the thinking alone (finish "length", no answer).
            var reply = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Reply with the single word: pong")], new ChatOptions { MaxOutputTokens = 1024 });
            Assert.Contains("pong", reply.Text, StringComparison.OrdinalIgnoreCase);

            var picture = ImageFile.Load(SmokeChecks.SolidBmp(64, 64), "red.bmp", out string? error);
            Assert.True(picture is not null, error);
            var seen = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, [new TextContent("What one colour fills this picture? Answer with the colour alone."), new DataContent(picture.Bytes, picture.MediaType)])],
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
        Assert.True(ex.VramSpill, ex.Message);
        Assert.StartsWith(EmbeddedLlmText.StartFailed("it "), ex.Message);
        Assert.Null(host.Running);

        var info = await service.StartAsync(model, new AppSettingsData { EmbeddedVramOnly = true, EmbeddedVision = false }, null, CancellationToken.None);
        Assert.Equal(model.Id, info.ModelId);
        service.Stop();
        Assert.Null(host.Running);
    }
}
