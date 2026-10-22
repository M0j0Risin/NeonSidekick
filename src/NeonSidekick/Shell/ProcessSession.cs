using NeonSidekick.Diagnostics;

namespace NeonSidekick.Shell;

/// <summary>
/// One child the runner started (2026-09-21): its output as it arrives (<see cref="Output"/>, both
/// streams tagged, pumped on the pool by <see cref="ShellRunner"/>), its exit
/// (<see cref="Exited"/> completes with the code once the process is gone <em>and</em> both pumps
/// have drained — a result never misses a trailing line), its stdin (<see cref="WriteAsync"/>,
/// <see cref="CloseInput"/>) and its kill (<see cref="Kill"/>, the whole tree, parent first as .NET
/// does it, and on Windows its job, on a Mac its process group: <see cref="ShellChild"/>; <see cref="Dispose"/> kills and waits for the exit before the handle goes). Time is the
/// <see cref="TimeProvider"/>'s: <see cref="Elapsed"/> freezes at exit, and
/// <see cref="WaitAsync"/> races the exit against <c>Task.Delay</c> on the same clock, so a manual
/// clock drives a timeout in tests. A foreground run and a background one are the same object;
/// only the registry's bookkeeping (<see cref="Notify"/>, <see cref="PollCursor"/>) differs.
/// </summary>
public sealed class ProcessSession : IDisposable, IAsyncDisposable
{
    private readonly ShellChild _child;
    private readonly TimeProvider _time;
    private readonly long _started;
    /// <summary>How long <see cref="Dispose"/> waits for a killed child to be gone before its handle goes; the tools' kill grace.</summary>
    public static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _ended;
    private bool _inputClosed;
    private bool _disposed;
    private bool _userStop;

    internal ProcessSession(string id, ProcessLaunch launch, ShellChild child, TimeProvider time)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _child = child ?? throw new ArgumentNullException(nameof(child));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _started = time.GetTimestamp();
        Pid = child.Pid;
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

    /// <summary>
    /// The user's own run (2026-10-11, <c>/cmd --bg</c>, the user's call): <c>/process</c> and its window show it, the model's
    /// <c>process</c> tool never does (<see cref="ProcessRegistry.ListForModel"/>, <see cref="ProcessRegistry.FindForModel"/>), and its
    /// exit is always alerted (<see cref="ProcessAlert.Mine"/>) for the screen's report, never a seeded poll. Set before the watch starts.
    /// </summary>
    public bool Mine { get; internal set; }

    /// <summary>How many lines the last <c>poll</c> had seen; the registry's cursor into <see cref="Output"/>.</summary>
    public long PollCursor { get; set; }

    /// <summary>The streams' unfinished lines that poll showed as it set <see cref="PollCursor"/> (2026-10-19, the second shell review): what <c>wait</c>'s <c>until</c> passes over.</summary>
    public IReadOnlyList<OutputLine> ShownUnfinished { get; set; } = [];

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

    /// <summary>
    /// Whether the child runs in a job of its own (2026-10-12, the shell review; Windows, <see cref="ProcessChild"/>): <see cref="Kill"/>
    /// ends it whole, and <see cref="Dispose()"/> closes it — which, for a background command's kill-on-close job, ends what is left in it.
    /// </summary>
    public bool InJob => _child.InJob;

    /// <summary>
    /// Whether the child itself has exited, its pumps perhaps still draining (2026-10-12): on a Mac a background command's leader
    /// stays a zombie while its group has members (<see cref="GroupChild"/>), so its pid is no way to ask.
    /// </summary>
    internal bool ChildHasExited => _child.HasExited;

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

    /// <summary>
    /// True once it exited within <paramref name="timeout"/> — or <paramref name="orWhen"/> completed (2026-10-12, <c>process wait</c>'s
    /// <c>until</c>: a line that matched); false on the timeout. The token's cancellation throws.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken, Task? orWhen = null)
    {
        if (HasExited || orWhen?.IsCompleted == true)
        {
            return true;
        }

        using var stop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        var delay = Task.Delay(timeout, _time, linked.Token);
        var done = await (orWhen is null ? Task.WhenAny(_exited.Task, delay) : Task.WhenAny(_exited.Task, orWhen, delay)).ConfigureAwait(false);
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
            await _child.StandardInput.WriteAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _child.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
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
            _child.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// Ends the process and everything it started, parent first; a no-op once it has exited. True when the kill was sent
    /// (2026-10-05, the code review: <see cref="ProcessRegistry.StopByUser"/> reads it, since a child gone on its own while its pumps
    /// still drain is not <see cref="HasExited"/> yet but is past killing).
    /// </summary>
    public bool Kill()
    {
        try
        {
            // On a Mac also after the child's own exit while what it left is still running and its output still drains (the code review,
            // 2026-10-12: a background `server & exit` could not be stopped): the group kill reaches it.
            if (!_child.HasExited || (!HasExited && _child.HoldsLeftovers))
            {
                Killed = true;
                _child.Kill();
                return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
        {
            // Already gone, or not ours to kill: the wait ends either way.
        }

        return false;
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
    public void Dispose() => Dispose(DisposeGrace);

    /// <summary>
    /// <see cref="Dispose()"/> without blocking a thread on the grace (2026-10-12, the shell review: a tool's ESC and timeout paths
    /// disposed on the turn's thread, up to <see cref="DisposeGrace"/> blocked there for a child slow to die): the kill, then the
    /// exit awaited (the pumps drained too) for up to the grace on the real clock, then the handle — <see cref="Dispose(TimeSpan)"/>
    /// with no wait left, whose <c>WaitForExit(0)</c> still raises a missed exit as the comment above needs.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        Kill();
        CloseInput();
        if (!HasExited)
        {
            // The grace's timer let go as soon as the child is gone (2026-10-22, review F6: each dispose left a 5 s timer behind).
            using var grace = new CancellationTokenSource();
            var delay = Task.Delay(DisposeGrace, grace.Token);
            await Task.WhenAny(_exited.Task, delay).ConfigureAwait(false);
            grace.Cancel();
        }

        Dispose(TimeSpan.Zero);
    }

    /// <summary><see cref="Dispose()"/> with <paramref name="grace"/> for the kill to land: <see cref="ProcessRegistry.Dispose"/> kills all, waits for all at once, then disposes each with none.</summary>
    internal void Dispose(TimeSpan grace)
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
                _child.WaitForExit((int)grace.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone: the handle is invalid, the wait moot.
            }
        }

        _child.Dispose();
        if (!HasExited)
        {
            DiagnosticLog.Debug(ShellKinds.Category, $"{Id}: still running after the kill's grace; completed as -1");   // or its pumps still draining
            Complete(-1);
        }
    }
}
