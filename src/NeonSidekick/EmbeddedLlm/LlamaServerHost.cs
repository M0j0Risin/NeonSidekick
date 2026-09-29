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
    public EmbeddedLlmException(string message, bool portInUse = false, bool runtimeMissing = false)
        : base(message)
    {
        PortInUse = portInUse;
        RuntimeMissing = runtimeMissing;
    }

    /// <summary>True when the llama.cpp runtime could not be installed (a download failed): nothing was started, so it says nothing about the backend.</summary>
    public bool RuntimeMissing { get; }

    /// <summary>True when the server could not bind its port — another program took it between the check and the start; a retry on a fresh port helps.</summary>
    public bool PortInUse { get; }
}

/// <summary>The one <c>llama-server</c> process the app runs (2026-09-29); faked in the tests.</summary>
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
/// server.</item>
/// </list>
/// </summary>
public sealed class LlamaServerHost : ILlamaServerHost
{
    private const string Category = "EmbeddedLlm";
    private const int TailLines = 40;
    private const int PortRetries = 2;

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _state = new();
    private readonly Queue<string> _tail = new();

    private Process? _process;
    private LlamaLaunch? _launch;
    private EmbeddedServerInfo? _info;

    /// <param name="http">The client <c>/health</c> is asked through; each request carries its own short deadline.</param>
    public LlamaServerHost(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>How long a start may take to answer <c>/health</c> with 200: loading 5 GB from a cold disk and a first scan of the CUDA DLLs by an antivirus take their time.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(5);

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
        }

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Exited += (_, _) => OnExited(process);

        DiagnosticLog.Info(Category, string.Create(CultureInfo.InvariantCulture, $"Starting {launch.Executable} for {model.Id} on 127.0.0.1:{port} ({LlamaRelease.Name(launch.Backend)}, context {launch.ContextSize}, GPU layers {launch.GpuLayers}, vision {(launch.Vision ? "on" : "off")})."));
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
        }
        catch
        {
            StopCore();
            throw;
        }

        var info = new EmbeddedServerInfo(new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/v1")), port, key, launch.Backend, model.Id, launch.Vision);
        lock (_state)
        {
            _info = info;
        }

        DiagnosticLog.Info(Category, string.Create(CultureInfo.InvariantCulture, $"llama-server is ready on 127.0.0.1:{port} (pid {process.Id})."));
        return info;
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
