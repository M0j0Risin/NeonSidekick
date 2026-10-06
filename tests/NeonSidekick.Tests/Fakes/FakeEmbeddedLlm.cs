using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;
using NeonSidekick.Speech;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// An <see cref="IEmbeddedLlm"/> with no files and no process (2026-09-29): the real catalog by default, the installed set
/// as the test says, a "server" on <see cref="LiveUrl"/> (map its <c>/props</c> on a stub handler for the context
/// probe), and a record of every install, start, stop and removal. <see cref="InstallGate"/> holds an install open so a
/// test can cancel it; <see cref="StartFailure"/> and <see cref="InstallFailure"/> make them fail. <see cref="StartGate"/>
/// (later on 2026-09-29) is awaited inside every start before it lands, so parallel links interleave as the real ones do;
/// the extras (multi-server botchats) answer on ports under <see cref="Port"/>, each with its own key. <see cref="Kill"/>
/// (2026-10-01, the kill switch) unloads the main server, the extras and a start held at the gate, which then throws as the
/// real one does (<see cref="EmbeddedLlmException.Killed"/>).
/// </summary>
public sealed class FakeEmbeddedLlm : IEmbeddedLlm
{
    public const int Port = 59999;
    public const string Key = "embedded-key";
    public static readonly Uri LiveUrl = new("http://127.0.0.1:59999/v1");

    private readonly Dictionary<string, EmbeddedModelState> _states = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EmbeddedModel> Catalog { get; init; } = EmbeddedModelCatalog.Models;

    public string ModelsDirectory { get; init; } = @"C:\home\models\llm";

    public string LlamaDirectory { get; init; } = @"C:\home\llama";

    public EmbeddedServerInfo? Running { get; private set; }

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

    /// <summary>Awaited inside a start, main or extra, before it lands (later on 2026-09-29): the race the links once had.</summary>
    public Func<CancellationToken, Task>? StartGate { get; set; }

    private readonly List<EmbeddedServerInfo> _extras = new();

    public IReadOnlyList<EmbeddedServerInfo> Extras => _extras.ToList();

    public List<string> ExtraStarts { get; } = new();

    public List<AppSettingsData> ExtraStartSettings { get; } = new();

    public string? ExtraStartFailure { get; set; }

    public int ExtraStops { get; private set; }

    /// <summary>The address of the <paramref name="n"/>th extra (1-based): port 59999 − n.</summary>
    public static Uri ExtraUrl(int n) => new($"http://127.0.0.1:{Port - n}/v1");

    public FakeEmbeddedLlm Installed(params string[] ids)
    {
        foreach (var id in ids)
        {
            _states[id] = EmbeddedModelState.Installed;
        }

        return this;
    }

    public FakeEmbeddedLlm Partial(string id, int percent)
    {
        _states[id] = new EmbeddedModelState(EmbeddedModelStateKind.Partial, percent);
        return this;
    }

    public EmbeddedModelState State(EmbeddedModel model) => _states.TryGetValue(model.Id, out var state) ? state : EmbeddedModelState.Absent;

    /// <summary>What a download would take; the whole model unless a test says (2026-10-06).</summary>
    public Func<EmbeddedModel, long> Download { get; set; } = EmbeddedModelCatalog.TotalBytes;

    /// <summary>What a removal frees; the whole model unless a test says.</summary>
    public Func<EmbeddedModel, long> Freed { get; set; } = EmbeddedModelCatalog.TotalBytes;

    public long BytesToDownload(EmbeddedModel model) => Download(model);

    public long FreedBytes(EmbeddedModel model) => Freed(model);

    public BackendChoice Backend(AppSettingsData effective) => new(LlamaBackend.Cuda, "fake driver");

    public long RuntimeBytesToDownload(AppSettingsData effective) => RuntimeBytes;

    public async Task<ModelResult> InstallAsync(EmbeddedModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
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
            return ModelResult.Failed("", EmbeddedLlmText.InstallFailed(model, failure));
        }

        _states[model.Id] = EmbeddedModelState.Installed;
        return new ModelResult(true, "", EmbeddedLlmText.Installed(model));
    }

    public async Task<EmbeddedServerInfo> StartAsync(EmbeddedModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        Starts.Add(model.Id);
        StartSettings.Add(effective);
        phase?.Invoke(EmbeddedLlmText.StartingLabel(model));
        await GateAsync(model, cancellationToken);

        if (StartFailure is { } failure)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.StartFailed(failure));
        }

        if (!State(model).IsInstalled)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.NotInstalled(model));
        }

        Running = new EmbeddedServerInfo(LiveUrl, Port, Key, LlamaBackend.Cuda, model.Id, effective.EmbeddedVision);
        return Running;
    }

    public async Task<EmbeddedServerInfo> StartExtraAsync(EmbeddedModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        if (!State(model).IsInstalled)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.NotInstalled(model));
        }

        if (_extras.FirstOrDefault(e => string.Equals(e.ModelId, model.Id, StringComparison.OrdinalIgnoreCase)) is { } up)
        {
            return up;
        }

        ExtraStarts.Add(model.Id);
        ExtraStartSettings.Add(effective);
        await GateAsync(model, cancellationToken);

        if (ExtraStartFailure is { } failure)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.StartFailed(failure));
        }

        int n = ExtraStarts.Count;
        var info = new EmbeddedServerInfo(ExtraUrl(n), Port - n, "extra-key-" + n, LlamaBackend.Cuda, model.Id, effective.EmbeddedVision);
        _extras.Add(info);
        return info;
    }

    public IReadOnlyList<EmbeddedServerInfo> StopExtras()
    {
        var stopped = _extras.ToList();
        ExtraStops += stopped.Count;
        _extras.Clear();
        return stopped;
    }

    public string? Remove(EmbeddedModel model)
    {
        Removes.Add(model.Id);
        if (Running?.ModelId == model.Id)
        {
            Stop();
        }

        _extras.RemoveAll(e => e.ModelId == model.Id);
        _states.Remove(model.Id);
        return null;
    }

    public void Stop()
    {
        Stops++;
        Running = null;
    }

    private readonly List<string> _starting = new();
    private int _kills;

    public int Kills => _kills;

    public bool Starting
    {
        get
        {
            lock (_starting)
            {
                return _starting.Count > 0;
            }
        }
    }

    public IReadOnlyList<string> Kill()
    {
        Interlocked.Increment(ref _kills);
        var ids = new List<string>();
        lock (_starting)
        {
            ids.AddRange(_starting);
        }

        if (Running is { } running)
        {
            ids.Add(running.ModelId);
        }

        ids.AddRange(_extras.Select(e => e.ModelId));
        Running = null;
        _extras.Clear();
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Awaits <see cref="StartGate"/> with <paramref name="id"/> counted as starting; throws as a killed start does when a kill landed meanwhile.</summary>
    private async Task GateAsync(EmbeddedModel model, CancellationToken cancellationToken)
    {
        int kills = Volatile.Read(ref _kills);
        lock (_starting)
        {
            _starting.Add(model.Id);
        }

        try
        {
            if (StartGate is { } gate)
            {
                await gate(cancellationToken);
            }
        }
        finally
        {
            lock (_starting)
            {
                _starting.Remove(model.Id);
            }
        }

        if (Volatile.Read(ref _kills) != kills)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.KilledStart(model), killed: true);
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Running = null;
        _extras.Clear();
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

    public EmbeddedServerInfo? Running { get; private set; }

    /// <summary>The port it answers on (later on 2026-09-29: a multi-server botchat's extra hosts each their own).</summary>
    public int Port { get; init; } = 59998;

    /// <summary>
    /// Awaited inside a start once its launch is recorded (2026-10-01): a test lands a <see cref="Kill"/> while it "loads", and the
    /// start then fails as the real host's would on its process gone — a plain failure, which the service reads as the kill's.
    /// </summary>
    public Func<CancellationToken, Task>? StartGate { get; set; }

    public async Task<EmbeddedServerInfo> EnsureRunningAsync(LlamaLaunch launch, EmbeddedModel model, Action<string>? phase, CancellationToken cancellationToken)
    {
        Launches.Add(launch);
        int kills = Kills;
        if (StartGate is { } gate)
        {
            await gate(cancellationToken);
        }

        if (Kills != kills)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.StartFailed(EmbeddedLlmText.ExitedEarly(1, "")));
        }

        if (Fail?.Invoke(launch) is { } failure)
        {
            throw failure;
        }

        Running = new EmbeddedServerInfo(new Uri($"http://127.0.0.1:{Port}/v1"), Port, "host-key", launch.Backend, model.Id, launch.Vision);
        return Running;
    }

    public void Stop()
    {
        Stops++;
        Running = null;
    }

    public int Kills { get; private set; }

    public bool Kill()
    {
        Kills++;
        bool had = Running is not null;
        Running = null;
        return had;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
