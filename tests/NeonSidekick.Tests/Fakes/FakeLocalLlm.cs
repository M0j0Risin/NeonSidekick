using NeonSidekick.LocalLlm;
using NeonSidekick.Settings;
using NeonSidekick.Speech;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// An <see cref="ILocalLlm"/> with no files and no process (2026-09-29): the real catalog by default, the installed set
/// as the test says, a "server" on <see cref="LiveUrl"/> (map its <c>/props</c> on a stub handler for the context
/// probe), and a record of every install, start, stop and removal. <see cref="InstallGate"/> holds an install open so a
/// test can cancel it; <see cref="StartFailure"/> and <see cref="InstallFailure"/> make them fail.
/// </summary>
public sealed class FakeLocalLlm : ILocalLlm
{
    public const int Port = 59999;
    public const string Key = "local-key";
    public static readonly Uri LiveUrl = new("http://127.0.0.1:59999/v1");

    private readonly Dictionary<string, LocalModelState> _states = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LocalModel> Catalog { get; init; } = LocalModelCatalog.Models;

    public string ModelsDirectory { get; init; } = @"C:\home\models\llm";

    public string LlamaDirectory { get; init; } = @"C:\home\llama";

    public LocalServerInfo? Running { get; private set; }

    public List<string> Installs { get; } = new();

    public List<string> Starts { get; } = new();

    public List<string> Removes { get; } = new();

    public List<AppSettingsData> StartSettings { get; } = new();

    public int Stops { get; private set; }

    public bool Disposed { get; private set; }

    public string? StartFailure { get; set; }

    public string? InstallFailure { get; set; }

    public long RuntimeBytes { get; set; }

    /// <summary>Awaited inside an install after its first label, before it lands: a test holds it open to cancel it.</summary>
    public Func<CancellationToken, Task>? InstallGate { get; set; }

    public FakeLocalLlm Installed(params string[] ids)
    {
        foreach (var id in ids)
        {
            _states[id] = LocalModelState.Installed;
        }

        return this;
    }

    public FakeLocalLlm Partial(string id, int percent)
    {
        _states[id] = new LocalModelState(LocalModelStateKind.Partial, percent);
        return this;
    }

    public LocalModelState State(LocalModel model) => _states.TryGetValue(model.Id, out var state) ? state : LocalModelState.Absent;

    public BackendChoice Backend(AppSettingsData effective) => new(LlamaBackend.Cuda, "fake driver");

    public long RuntimeBytesToDownload(AppSettingsData effective) => RuntimeBytes;

    public async Task<ModelResult> InstallAsync(LocalModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        Installs.Add(model.Id);
        phase?.Invoke("downloading " + model.Display + "…");
        if (InstallGate is { } gate)
        {
            await gate(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (InstallFailure is { } failure)
        {
            return ModelResult.Failed("", LocalLlmText.InstallFailed(model, failure));
        }

        _states[model.Id] = LocalModelState.Installed;
        return new ModelResult(true, "", LocalLlmText.Installed(model));
    }

    public Task<LocalServerInfo> StartAsync(LocalModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        Starts.Add(model.Id);
        StartSettings.Add(effective);
        phase?.Invoke(LocalLlmText.StartingLabel(model));
        if (StartFailure is { } failure)
        {
            throw new LocalLlmException(LocalLlmText.StartFailed(failure));
        }

        if (!State(model).IsInstalled)
        {
            throw new LocalLlmException(LocalLlmText.NotInstalled(model));
        }

        Running = new LocalServerInfo(LiveUrl, Port, Key, LlamaBackend.Cuda, model.Id, effective.LocalVision);
        return Task.FromResult(Running);
    }

    public string? Remove(LocalModel model)
    {
        Removes.Add(model.Id);
        if (Running?.ModelId == model.Id)
        {
            Stop();
        }

        _states.Remove(model.Id);
        return null;
    }

    public void Stop()
    {
        Stops++;
        Running = null;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Running = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>An <see cref="ILlamaServerHost"/> that starts nothing: it records each launch and answers on a made-up port, or throws what <see cref="Fail"/> says.</summary>
public sealed class FakeLlamaServerHost : ILlamaServerHost
{
    public List<LlamaLaunch> Launches { get; } = new();

    public Func<LlamaLaunch, Exception?>? Fail { get; set; }

    public int Stops { get; private set; }

    public bool Disposed { get; private set; }

    public LocalServerInfo? Running { get; private set; }

    public Task<LocalServerInfo> EnsureRunningAsync(LlamaLaunch launch, LocalModel model, Action<string>? phase, CancellationToken cancellationToken)
    {
        Launches.Add(launch);
        if (Fail?.Invoke(launch) is { } failure)
        {
            return Task.FromException<LocalServerInfo>(failure);
        }

        Running = new LocalServerInfo(new Uri("http://127.0.0.1:59998/v1"), 59998, "host-key", launch.Backend, model.Id, launch.Vision);
        return Task.FromResult(Running);
    }

    public void Stop()
    {
        Stops++;
        Running = null;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
