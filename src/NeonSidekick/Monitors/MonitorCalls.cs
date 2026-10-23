using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Monitors;

/// <summary>
/// The one runner every monitor call goes through (2026-10-23, the monitors plan): the board's, <c>MonitorActions</c>' and so headless's and
/// <c>--monitor-check</c>'s, which run without a board. Calls run one at a time on a pool thread, never the caller's. dxva2 and CCD calls
/// can't be cancelled, so each is awaited at most <see cref="Cap"/> (<see cref="MonitorText.NoAnswer"/>), the call finishing in the
/// background, observed; a cancel (Esc) returns at once the same way. The DDC/CI bus is shared with other programs, and the spike saw every
/// read fail for ~250 ms after an input switch (0xC0262589), so a DDC/CI failure (<see cref="MonitorText.NoDdc"/>) is tried once more
/// <see cref="RetryGap"/> later before it counts. Every exception but out-of-memory comes out as a <see cref="MonitorException"/>, an
/// unexpected one logged (scrubbed) first, so nothing escapes as a throw the screen never expected.
/// </summary>
public sealed class MonitorCalls
{
    /// <summary>The longest a call is waited on.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromSeconds(10);

    /// <summary>The gap before a DDC/CI failure's one retry (the spike: 250 ms of failed reads after a switch).</summary>
    public static readonly TimeSpan RetryGap = TimeSpan.FromMilliseconds(300);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;

    private readonly TimeSpan _retryGap;

    /// <param name="retryGap">The retry's gap; null for <see cref="RetryGap"/> (the tests pass zero).</param>
    public MonitorCalls(IMonitorSystem system, TimeProvider? time = null, TimeSpan? retryGap = null)
    {
        System = system ?? throw new ArgumentNullException(nameof(system));
        _time = time ?? TimeProvider.System;
        _retryGap = retryGap ?? RetryGap;
    }

    public IMonitorSystem System { get; }

    /// <summary>
    /// Runs <paramref name="call"/> on a pool thread under the cap; with <paramref name="retry"/> a DDC/CI failure is tried once more after
    /// <see cref="RetryGap"/>. Throws <see cref="MonitorException"/> (its sentence) or <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<T> RunAsync<T>(string what, Func<IMonitorSystem, T> call, bool retry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                return await OnceAsync(what, call, cancellationToken).ConfigureAwait(false);
            }
            catch (MonitorException e) when (retry && e.Message == MonitorText.NoDdc)
            {
                await Task.Delay(_retryGap, _time, cancellationToken).ConfigureAwait(false);
                return await OnceAsync(what, call, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><see cref="RunAsync{T}"/> for a call with no answer.</summary>
    public Task RunAsync(string what, Action<IMonitorSystem> call, bool retry, CancellationToken cancellationToken) =>
        RunAsync(what, s =>
        {
            call(s);
            return true;
        }, retry, cancellationToken);

    private async Task<T> OnceAsync<T>(string what, Func<IMonitorSystem, T> call, CancellationToken cancellationToken)
    {
        var work = Task.Run(() => call(System), CancellationToken.None);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cap = Task.Delay(Cap, _time, waiting.Token);
        var first = await Task.WhenAny(work, cap).ConfigureAwait(false);
        if (first != work)
        {
            // Still running: it finishes in the background, its failure observed and logged, never thrown.
            _ = work.ContinueWith(t => DiagnosticLog.Debug("Monitors", NameScrub.Display(what) + " finished late: " + (t.Exception?.InnerException?.Message ?? "ok")),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            DiagnosticLog.Warn("Monitors", NameScrub.Display(what) + ": no answer within " + Cap.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
            throw new MonitorException(MonitorText.NoAnswer);
        }

        await waiting.CancelAsync().ConfigureAwait(false);
        try
        {
            return await work.ConfigureAwait(false);
        }
        catch (MonitorException)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn("Monitors", NameScrub.Display(what) + " failed: " + NameScrub.Display(e.Message));
            throw new MonitorException(MonitorText.Failed(NameScrub.Display(e.Message)), e);
        }
    }
}
