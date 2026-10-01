using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>A running embedded server: its <c>/v1</c> base on loopback, port, per-start key, backend, model id, and whether it reads images.</summary>
public sealed record EmbeddedServerInfo(Uri BaseUrl, int Port, string ApiKey, LlamaBackend Backend, string ModelId, bool Vision);

/// <summary>An embedded-model failure the user reads as it is (a start that failed, a runtime that would not install, a model not installed).</summary>
public sealed class EmbeddedLlmException : Exception
{
    public EmbeddedLlmException(string message, bool portInUse = false, bool runtimeMissing = false, bool vramOnlyRefused = false, bool killed = false)
        : base(message)
    {
        PortInUse = portInUse;
        RuntimeMissing = runtimeMissing;
        VramOnlyRefused = vramOnlyRefused;
        Killed = killed;
    }

    /// <summary>
    /// True when the kill switch (Ctrl+Alt+X, 2026-10-01) stopped the start: the user's own act, not a failure, so no CUDA to
    /// Vulkan fallback and no error line.
    /// </summary>
    public bool Killed { get; }

    /// <summary>
    /// True when Embedded VRAM only refused the start (2026-10-01): the model spilled or did not fit in VRAM, the backend is
    /// the CPU, the GPU has no VRAM of its own, or the load could not be checked. Not a broken backend, so <c>auto</c> does
    /// not fall back from CUDA to Vulkan on it — the same model would not fit there either. <c>VramSpill</c> until the same
    /// day's review: that is the record's name too, and not every refusal is a spill.
    /// </summary>
    public bool VramOnlyRefused { get; }

    /// <summary>True when the llama.cpp runtime could not be installed (a download failed): nothing was started, so it says nothing about the backend.</summary>
    public bool RuntimeMissing { get; }

    /// <summary>True when the server could not bind its port — another program took it between the check and the start; a retry on a fresh port helps.</summary>
    public bool PortInUse { get; }
}

/// <summary>
/// One <c>llama-server</c> process (2026-09-29): the service's main one, or since later that day one of a multi-server
/// botchat's extras, each host its own process, port and key; faked in the tests.
/// </summary>
public interface ILlamaServerHost : IAsyncDisposable
{
    /// <summary>The server while one is up and ready; null otherwise.</summary>
    EmbeddedServerInfo? Running { get; }

    /// <summary>
    /// The server for <paramref name="launch"/>: the running one when its launch is equal and it is alive, else the old
    /// one stopped and a new one started and waited for. Throws <see cref="EmbeddedLlmException"/> when it does not start.
    /// </summary>
    Task<EmbeddedServerInfo> EnsureRunningAsync(LlamaLaunch launch, EmbeddedModel model, Action<string>? phase, CancellationToken cancellationToken);

    /// <summary>Stops the server, if any, and waits briefly for it to go (its model files are memory-mapped until then).</summary>
    void Stop();

    /// <summary>
    /// The kill switch (Ctrl+Alt+X, 2026-10-01, the user's ask): <see cref="Stop"/> without waiting for a start in progress —
    /// the process killed whether it is up or still loading, and that start then throws with
    /// <see cref="EmbeddedLlmException.Killed"/>. True when there was a process to kill.
    /// </summary>
    bool Kill();
}

/// <summary>
/// Runs llama.cpp's <c>llama-server</c> as the app's child (2026-09-29, the embedded model; one of the counted
/// process-start sites). One process at a time, behind a gate:
/// <list type="bullet">
/// <item>a free loopback port per start (the OS picks it), a random API key, the arguments from
/// <see cref="LlamaArguments"/>, the runtime folder as the working directory, no window — so Ctrl+C in the TUI never
/// reaches it;</item>
/// <item>the process joins the app's kill-on-close job (<see cref="ChildJob"/>) at once, so it dies with the app
/// however the app dies;</item>
/// <item>both output streams are drained for the process's whole life (a full pipe would stall the server) into
/// <see cref="DiagnosticLog"/> at Debug, the last lines kept for error messages;</item>
/// <item>ready is <c>GET /health</c> answering 200 (503 while the model loads — the spinner then says "loading"),
/// raced against the process exiting and a <see cref="ReadyTimeout"/>; a port another program grabbed in between is
/// retried on a fresh one, twice;</item>
/// <item>an exit nobody asked for, once ready, is an Error line (the TUI shows it), and the next connect starts a new
/// server;</item>
/// <item>with <see cref="LlamaLaunch.VramOnly"/> (2026-10-01, the user's ask), a load that put part of the model in system
/// memory is refused: the server is stopped and the start throws with <see cref="EmbeddedLlmException.VramOnlyRefused"/>. A
/// load that exited before ready after a failed allocation says it did not fit; one that came up is checked against its
/// load lines (<see cref="LlamaLoadReport"/>) and its shared GPU memory (<see cref="VramSpill.Check"/>) — the NVIDIA
/// driver's sysmem fallback, which llama.cpp never sees, shows only there. Measured once at ready, when every buffer is
/// allocated, after the load's last line has come through the pipe (<see cref="LoadLinesWait"/>); a load with no layer line
/// is refused as not checked (<see cref="VramRefusal"/>). The report is fed only then (the same day's review): before and
/// after, lines go to the log and the tail alone. The <c>-lv 4</c> the launch passed stays for the server's life — llama-server
/// has no way to lower it at run time — so its request lines stay in the Debug log.</item>
/// <item>the kill switch (Ctrl+Alt+X, later on 2026-10-01, <see cref="Kill"/>) stops the process outside the gate, so a load in
/// progress never holds it up; that start finds its process gone and throws with <see cref="EmbeddedLlmException.Killed"/>,
/// and a server killed between ready and its record is never reported running.</item>
/// </list>
/// </summary>
public sealed class LlamaServerHost : ILlamaServerHost
{
    private const string Category = "EmbeddedLlm";
    private const int TailLines = 40;
    private const int PortRetries = 2;

    private readonly HttpClient _http;
    private readonly Func<int, long?> _sharedMemory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _state = new();
    private readonly Queue<string> _tail = new();

    private Process? _process;
    private LlamaLaunch? _launch;
    private EmbeddedServerInfo? _info;
    private LlamaLoadReport? _report;   // a VRAM-only start's, until its check
    private TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="http">The client <c>/health</c> is asked through; each request carries its own short deadline.</param>
    /// <param name="sharedMemory">A process's shared GPU memory in bytes, for Embedded VRAM only (2026-10-01); <see cref="Perf.GpuMemory.ProcessSharedBytes"/> when null.</param>
    public LlamaServerHost(HttpClient? http = null, Func<int, long?>? sharedMemory = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _sharedMemory = sharedMemory ?? Perf.GpuMemory.ProcessSharedBytes;
    }

    /// <summary>How long a start may take to answer <c>/health</c> with 200: loading 5 GB from a cold disk and a first scan of the CUDA DLLs by an antivirus take their time.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a VRAM-only start waits, after <c>/health</c> answered 200, for the load's last line (<c>model loaded</c>) to come
    /// through the pipe (2026-10-01, the review's finding): checked sooner, a host buffer still in the pipe is missing from the
    /// allowance and a clean load reads as a spill. Past it the check runs on what came.
    /// </summary>
    public TimeSpan LoadLinesWait { get; init; } = TimeSpan.FromSeconds(10);

    public EmbeddedServerInfo? Running
    {
        get
        {
            lock (_state)
            {
                return _info;
            }
        }
    }

    public async Task<EmbeddedServerInfo> EnsureRunningAsync(LlamaLaunch launch, EmbeddedModel model, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(model);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_state)
            {
                if (_info is { } info && _launch == launch && _process is { HasExited: false })
                {
                    return info;
                }
            }

            StopCore();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await StartAsync(launch, model, phase, cancellationToken).ConfigureAwait(false);
                }
                catch (EmbeddedLlmException ex) when (ex.PortInUse && attempt < PortRetries)
                {
                    DiagnosticLog.Info(Category, "The port was taken before llama-server bound it; trying another.");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        _gate.Wait();
        try
        {
            StopCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <see cref="StopCore"/> outside <c>_gate</c>, which a start holds for its whole load (2026-10-01, the kill switch): the
    /// state is read and cleared under <c>_state</c>, so the start in progress finds its process gone and reports the kill
    /// (<see cref="StartAsync"/>).
    /// </summary>
    public bool Kill()
    {
        bool had;
        lock (_state)
        {
            had = _process is not null;
        }

        StopCore();
        return had;
    }

    public ValueTask DisposeAsync()
    {
        StopCore();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<EmbeddedServerInfo> StartAsync(LlamaLaunch launch, EmbeddedModel model, Action<string>? phase, CancellationToken cancellationToken)
    {
        int port = FreeLoopbackPort();
        string key = RandomNumberGenerator.GetHexString(32, lowercase: true);
        var start = new ProcessStartInfo(launch.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = launch.WorkingDirectory,
        };
        foreach (var argument in LlamaArguments.Build(launch, port, key))
        {
            start.ArgumentList.Add(argument);
        }

        lock (_state)
        {
            _tail.Clear();
            _report = launch.VramOnly ? new LlamaLoadReport() : null;
            _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Exited += (_, _) => OnExited(process);

        DiagnosticLog.Info(Category, string.Create(CultureInfo.InvariantCulture, $"Starting {launch.Executable} for {model.Id} on 127.0.0.1:{port} ({LlamaRelease.Name(launch.Backend)}, context {launch.ContextSize}, GPU layers {launch.GpuLayers}, fit target {(launch.FitTargetMiB is { } fit ? fit.ToString(CultureInfo.InvariantCulture) + " MiB" : "default")}, vision {(launch.Vision ? "on" : "off")}, VRAM only {(launch.VramOnly ? "on" : "off")})."));
        try
        {
            if (!process.Start())
            {
                throw new EmbeddedLlmException(EmbeddedLlmText.StartFailed("llama-server.exe did not start"));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new EmbeddedLlmException(EmbeddedLlmText.StartFailed(ex.Message));
        }

        ChildJob.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_state)
        {
            _process = process;
            _launch = launch;
        }

        try
        {
            await WaitReadyAsync(process, port, model, phase, cancellationToken).ConfigureAwait(false);
            if (launch.VramOnly)
            {
                await CheckVramAsync(process.Id, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Killed(process))
        {
            // The kill switch took the process mid-load (2026-10-01): whatever the load then saw is the kill's.
            throw new EmbeddedLlmException(EmbeddedLlmText.KilledStart(model), killed: true);
        }
        catch
        {
            StopCore();
            throw;
        }
        finally
        {
            lock (_state)
            {
                _report = null;   // checked or failed: later lines go to the log and the tail alone
            }
        }

        var info = new EmbeddedServerInfo(new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/v1")), port, key, launch.Backend, model.Id, launch.Vision);
        lock (_state)
        {
            // A kill between ready and here leaves no server to report: Running would name a dead process.
            if (ReferenceEquals(_process, process))
            {
                _info = info;
            }
        }

        if (Killed(process))
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.KilledStart(model), killed: true);
        }

        DiagnosticLog.Info(Category, string.Create(CultureInfo.InvariantCulture, $"llama-server is ready on 127.0.0.1:{port} (pid {process.Id})."));
        return info;
    }

    /// <summary>
    /// Embedded VRAM only's check of a server that came up: waits for the load's last line (<see cref="LoadLinesWait"/>),
    /// then throws when the load spilled into system memory or could not be checked.
    /// </summary>
    private async Task CheckVramAsync(int pid, CancellationToken cancellationToken)
    {
        Task loaded;
        lock (_state)
        {
            loaded = _loaded.Task;
        }

        try
        {
            await loaded.WaitAsync(LoadLinesWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            DiagnosticLog.Warn(Category, EmbeddedLlmText.VramLoadLineLate(LoadLinesWait));
        }

        long? shared = _sharedMemory(pid);
        LlamaLoadReport report;
        lock (_state)
        {
            report = _report ?? new LlamaLoadReport();
        }

        if (shared is null)
        {
            DiagnosticLog.Warn(Category, EmbeddedLlmText.VramSharedNotRead);
        }

        DiagnosticLog.Info(Category, EmbeddedLlmText.VramCheckSummary(report, shared));
        if (VramRefusal(report, shared) is { } refusal)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.StartFailed(refusal), vramOnlyRefused: true);
        }
    }

    /// <summary>
    /// Why Embedded VRAM only refuses a load that came up, or null to keep it (2026-10-01): no layer line at all is a load
    /// that could not be checked (the review's finding — it used to pass as 0 of 0 layers on the CPU); else what
    /// <see cref="VramSpill.Check"/> finds.
    /// </summary>
    internal static string? VramRefusal(LlamaLoadReport report, long? sharedBytes)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.TotalLayers == 0)
        {
            return EmbeddedLlmText.VramNotChecked;
        }

        return VramSpill.Check(report, sharedBytes) is { } spill ? EmbeddedLlmText.VramSpilled(spill) : null;
    }

    /// <summary>Whether <paramref name="process"/> is no longer this host's: <see cref="Kill"/> took it (a start's own failures stop it only after this is asked).</summary>
    private bool Killed(Process process)
    {
        lock (_state)
        {
            return !ReferenceEquals(_process, process);
        }
    }

    /// <summary>Whether a VRAM-only start's load lines include a failed device allocation.</summary>
    private bool OutOfMemory()
    {
        lock (_state)
        {
            return _report?.OutOfMemory == true;
        }
    }

    private async Task WaitReadyAsync(Process process, int port, EmbeddedModel model, Action<string>? phase, CancellationToken cancellationToken)
    {
        var health = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/health"));
        var watch = Stopwatch.StartNew();
        bool loadingSaid = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                // Let the output pumps flush the last lines, which say why.
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                // A VRAM-only load that failed an allocation and then exited did not fit (2026-10-01). Only an exit
                // before ready (the same day's review): a timeout or a crash with a non-fatal allocation line in the log is
                // what it is, and auto's CUDA to Vulkan fallback still sees it.
                if (OutOfMemory())
                {
                    throw new EmbeddedLlmException(EmbeddedLlmText.StartFailed(EmbeddedLlmText.VramDidNotFit), vramOnlyRefused: true);
                }

                string tail = ErrorTail();
                bool portInUse = tail.Contains("bind", StringComparison.OrdinalIgnoreCase);
                throw new EmbeddedLlmException(EmbeddedLlmText.StartFailed(EmbeddedLlmText.ExitedEarly(process.ExitCode, tail)), portInUse);
            }

            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                using var response = await _http.GetAsync(health, deadline.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && !loadingSaid)
                {
                    loadingSaid = true;
                    phase?.Invoke(EmbeddedLlmText.LoadingLabel(model));
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // This one request timed out; the loop decides.
            }

            if (watch.Elapsed > ReadyTimeout)
            {
                throw new EmbeddedLlmException(EmbeddedLlmText.StartFailed(EmbeddedLlmText.StartTimeout(ReadyTimeout)));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private void StopCore()
    {
        Process? process;
        lock (_state)
        {
            process = _process;
            _process = null;
            _launch = null;
            _info = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                DiagnosticLog.Info(Category, "Stopped llama-server.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            DiagnosticLog.Debug(Category, "Stopping llama-server: " + ex.Message);
        }
        finally
        {
            process.Dispose();
        }
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        DiagnosticLog.Debug(Category, line);
        lock (_state)
        {
            if (_report is { } report)
            {
                report.Add(line);
                if (report.Loaded)
                {
                    _loaded.TrySetResult();
                }
            }

            _tail.Enqueue(line.Trim());
            while (_tail.Count > TailLines)
            {
                _tail.Dequeue();
            }
        }
    }

    private void OnExited(Process process)
    {
        EmbeddedServerInfo? lost;
        lock (_state)
        {
            if (!ReferenceEquals(process, _process) || _info is null)
            {
                return;   // a stop we asked for, or a start that fails on its own path
            }

            lost = _info;
            _info = null;
            _launch = null;
        }

        int code;
        try
        {
            code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }

        DiagnosticLog.Error(Category, EmbeddedLlmText.Exited(code, ErrorTail()) + $" ({lost.ModelId})");
    }

    /// <summary>The lines that say what went wrong: those mentioning an error or failure among the last kept, else the last three.</summary>
    private string ErrorTail()
    {
        string[] lines;
        lock (_state)
        {
            lines = _tail.ToArray();
        }

        var errors = lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("fail", StringComparison.OrdinalIgnoreCase)).TakeLast(3).ToArray();
        return string.Join(" / ", errors.Length > 0 ? errors : lines.TakeLast(3));
    }

    /// <summary>A loopback port the OS has free right now; the server binds it a moment later (a race <see cref="PortRetries"/> covers).</summary>
    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
