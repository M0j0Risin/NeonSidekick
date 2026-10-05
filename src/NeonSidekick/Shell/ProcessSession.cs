using System.Diagnostics;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Shell;

/// <summary>
/// One child the runner started (2026-09-21): its output as it arrives (<see cref="Output"/>, both
/// streams tagged, pumped on the pool by <see cref="ShellRunner"/>), its exit
/// (<see cref="Exited"/> completes with the code once the process is gone <em>and</em> both pumps
/// have drained — a result never misses a trailing line), its stdin (<see cref="WriteAsync"/>,
/// <see cref="CloseInput"/>) and its kill (<see cref="Kill"/>, the whole tree, parent first as .NET
/// does it; <see cref="Dispose"/> kills and waits for the exit before the handle goes). Time is the
/// <see cref="TimeProvider"/>'s: <see cref="Elapsed"/> freezes at exit, and
/// <see cref="WaitAsync"/> races the exit against <c>Task.Delay</c> on the same clock, so a manual
/// clock drives a timeout in tests. A foreground run and a background one are the same object;
/// only the registry's bookkeeping (<see cref="Notify"/>, <see cref="PollCursor"/>) differs.
/// </summary>
public sealed class ProcessSession : IDisposable
{
    private readonly Process _process;
    private readonly TimeProvider _time;
    private readonly long _started;
    /// <summary>How long <see cref="Dispose"/> waits for a killed child to be gone before its handle goes; the tools' kill grace.</summary>
    public static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _ended;
    private bool _inputClosed;
    private bool _disposed;
    private bool _userStop;

    internal ProcessSession(string id, ProcessLaunch launch, Process process, TimeProvider time)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _started = time.GetTimestamp();
        Pid = process.Id;
        Output = new OutputBuffer();
    }

    /// <summary>The registry's id (<c>proc_3f2a1b</c>) or a foreground run's (<c>run_…</c>): the spill file's name.</summary>
    public string Id { get; }

    public ProcessLaunch Launch { get; }

    /// <summary>The command as the model sent it.</summary>
    public string Label => Launch.Label;

    /// <summary>The header's word: the shell or the language.</summary>
    public string Kind => Launch.Kind;

    public int Pid { get; }

    public OutputBuffer Output { get; }

    /// <summary>Whether the user (and the model) are told when it exits; the registry's flag.</summary>
    public bool Notify { get; set; }

    /// <summary>How many lines the last <c>poll</c> had seen; the registry's cursor into <see cref="Output"/>.</summary>
    public long PollCursor { get; set; }

    /// <summary>Whether <see cref="Kill"/> ended it (a timeout, a <c>kill</c>, the app's exit) rather than its own exit.</summary>
    public bool Killed { get; private set; }

    /// <summary>
    /// Whether the user stopped it by hand (2026-10-05, Ctrl+K twice in <c>/process</c>'s window, <see cref="ProcessRegistry.StopByUser"/>):
    /// the alert and the model's poll say "stopped by you" / "stopped by the user" rather than a bare kill. Only when the kill
    /// landed: a child that exited on its own just before the stop keeps its own exit.
    /// </summary>
    public bool StoppedByUser => Killed && Volatile.Read(ref _userStop);

    /// <summary>The user's stop asked for (<see cref="ProcessRegistry.StopByUser"/>), before the kill.</summary>
    internal void MarkUserStop() => Volatile.Write(ref _userStop, true);

    /// <summary>Completes with the exit code once the process is gone and both pumps have drained.</summary>
    public Task<int> Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>The exit code, or null while running.</summary>
    public int? ExitCode => _exited.Task.IsCompletedSuccessfully ? _exited.Task.Result : null;

    /// <summary>Since the start, to the exit once there is one.</summary>
    public TimeSpan Elapsed => _time.GetElapsedTime(_started, Volatile.Read(ref _ended) is var end && end != 0 ? end : _time.GetTimestamp());

    /// <summary>Called by the runner once the process has exited and both pumps are done.</summary>
    internal void Complete(int exitCode)
    {
        Volatile.Write(ref _ended, _time.GetTimestamp());
        _exited.TrySetResult(exitCode);
    }

    /// <summary>True once it exited within <paramref name="timeout"/>; false on the timeout. The token's cancellation throws.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (HasExited)
        {
            return true;
        }

        using var stop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        var delay = Task.Delay(timeout, _time, linked.Token);
        var done = await Task.WhenAny(_exited.Task, delay).ConfigureAwait(false);
        if (done == delay)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        stop.Cancel();
        return true;
    }

    /// <summary>Text to the child's stdin, as sent (a <c>submit</c> adds its own newline); a closed or exited child is a no-op that returns false.</summary>
    public async Task<bool> WriteAsync(string data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_inputClosed || HasExited)
        {
            return false;
        }

        try
        {
            await _process.StandardInput.WriteAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The child closed its end: what was sent is lost, as on a terminal.
            DiagnosticLog.Debug(ShellKinds.Category, $"{Id}: stdin write failed ({ex.Message})");
            return false;
        }
    }

    /// <summary>Closes the child's stdin (a read there sees end of input); a foreground run does this at once.</summary>
    public void CloseInput()
    {
        if (_inputClosed)
        {
            return;
        }

        _inputClosed = true;
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    /// <summary>Ends the process and everything it started, parent first; a no-op once it has exited.</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                Killed = true;
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone, or not ours to kill: the wait ends either way.
        }
    }

    /// <summary>
    /// Kills what still runs, then waits for the kill to land before the handle goes (2026-09-22):
    /// <c>TerminateProcess</c> returns before the process is gone, and the exit is noticed by a wait
    /// on the handle whose callback runs on the pool. <c>Process.Dispose</c> unregisters that wait,
    /// and a callback already queued then returns without raising <c>Exited</c> — so the runner's
    /// <c>WaitForExitAsync</c> never completes and <see cref="Exited"/> stays pending for good. CI
    /// lost that race (a starved pool under 16 sessions: <c>ProcessToolTests</c> timed out after
    /// 60 s). <c>Process.WaitForExit(int)</c> raises <c>Exited</c> itself when it finds the process
    /// gone, so the pump completes first; a child that ignores the kill past <see cref="DisposeGrace"/>
    /// is completed here as <c>-1</c>, the code a killed child gives, so a dispose never leaves a
    /// pending exit.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Kill();
        CloseInput();
        if (!HasExited)
        {
            try
            {
                _process.WaitForExit((int)DisposeGrace.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone: the handle is invalid, the wait moot.
            }
        }

        _process.Dispose();
        if (!HasExited)
        {
            DiagnosticLog.Debug(ShellKinds.Category, $"{Id}: still running after the kill's grace; completed as -1");
            Complete(-1);
        }
    }
}
