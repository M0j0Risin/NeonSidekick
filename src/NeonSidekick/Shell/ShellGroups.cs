using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Shell;

/// <summary>
/// The app's side of the crash watcher on a Mac (2026-10-12, the user's ask): the shell command groups this run has alive, the records
/// of its background ones (<see cref="ShellRecords"/>) and the guard (<see cref="ShellGuard"/>) that kills those should the app die. Set
/// once by <c>Program</c> as <see cref="Current"/>; null elsewhere and in the tests that do not make their own, so a run with none tracks
/// nothing.
/// <list type="bullet">
/// <item><see cref="Track"/>, as a command starts (<see cref="GroupChild"/>): every group is tracked, foreground ones too, for the exit
/// signals; a background one (<c>watched</c>) also gets its record, and the guard is started if it is not running — once, at the first,
/// and again should it have died. Only when this process is the app's own executable
/// (<see cref="EmbeddedLlm.LlamaGuard.DefaultExecutable"/>); a test host records without a guard. A group is tracked even when its
/// record could not be written (the code review, 2026-10-12): the exit signals still find it, only the guard does not.</item>
/// <item><see cref="Untrack"/>, before the leader is reaped: the record goes while the group id is still certainly ours.</item>
/// <item><see cref="KillTracked"/>, the exit signals' (SIGTERM, SIGQUIT, SIGHUP: no <c>finally</c> runs after them, so the registries
/// are never disposed): every tracked group killed at once, a foreground command's too — it leads a session of its own, so the
/// terminal's hangup no longer reaches it as it did while it shared the app's process group (the code review). The records stay for the
/// guard, which finds the groups gone. A crash still ends only the background ones, as on Windows (a foreground job is not kill-on-close).</item>
/// <item><see cref="Sweep"/>, the start's, under the same lock as <see cref="Track"/>, so a record the sweep reads is never one this run
/// is writing under the same pid (the code review).</item>
/// </list>
/// Starting the guard is a counted process-start site (the app's own executable; the user's call, 2026-10-12).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class ShellGroups
{
    private readonly string _folder;
    private readonly string? _guardExecutable;
    private readonly Func<int, long?>? _startTicks;
    private readonly object _lock = new();
    private readonly Dictionary<int, string?> _tracked = new();
    private Process? _guard;
    private bool _guardFailed;

    /// <param name="folder">The records' folder (<see cref="ShellRecords.FolderIn"/>).</param>
    /// <param name="guardExecutable">The app's own executable to run as the guard; null for none (a test host).</param>
    /// <param name="startTicks">Reads a process's start; the real one when null.</param>
    public ShellGroups(string folder, string? guardExecutable, Func<int, long?>? startTicks = null)
    {
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _guardExecutable = guardExecutable;
        _startTicks = startTicks;
    }

    /// <summary>The run's tracker; <c>Program</c> sets it on a Mac.</summary>
    public static ShellGroups? Current { get; set; }

    /// <summary>The groups tracked now, by leader pid (the tests').</summary>
    public IReadOnlyList<int> Tracked
    {
        get
        {
            lock (_lock)
            {
                return _tracked.Keys.Order().ToList();
            }
        }
    }

    /// <summary>Whether the guard runs (the tests').</summary>
    public bool GuardRunning
    {
        get
        {
            lock (_lock)
            {
                return _guard is { HasExited: false };
            }
        }
    }

    /// <summary>Closes the guard's stdin as the app's death would, and waits up to <paramref name="timeout"/> for it to finish (the tests'). Whether it did.</summary>
    internal bool ReleaseGuard(TimeSpan timeout)
    {
        Process? guard;
        lock (_lock)
        {
            guard = _guard;
            guard?.StandardInput.Close();
        }

        return guard is null || guard.WaitForExit((int)timeout.TotalMilliseconds);
    }

    /// <summary>
    /// A command's group, led by <paramref name="pgid"/>, started: tracked; with <paramref name="watched"/> (a background command)
    /// recorded too, and the guard made sure of. Never throws: a failure is logged and the command runs on, tracked.
    /// </summary>
    internal void Track(int pgid, bool watched)
    {
        lock (_lock)
        {
            _tracked[pgid] = null;
            if (!watched)
            {
                return;
            }

            try
            {
                EnsureGuard();
                _tracked[pgid] = ShellRecords.Write(_folder, pgid, _startTicks);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DiagnosticLog.Warn(ShellKinds.Category, ShellText.GroupNotRecorded(pgid, ex.Message));
            }
        }
    }

    /// <summary>The group's leader about to be reaped: untracked, its record removed.</summary>
    internal void Untrack(int pgid)
    {
        lock (_lock)
        {
            if (_tracked.Remove(pgid, out var path))
            {
                ShellRecords.Remove(path);
            }
        }
    }

    /// <summary>Every tracked group killed (the exit signals', see the class); how many. Never throws.</summary>
    public int KillTracked()
    {
        lock (_lock)
        {
            foreach (int pgid in _tracked.Keys)
            {
                DiagnosticLog.Info(ShellKinds.Category, ShellText.GroupKilledAtExit(pgid));
                ShellGuard.KillGroup(pgid);
            }

            return _tracked.Count;
        }
    }

    /// <summary>The start's sweep over this tracker's folder (<see cref="ShellRecords.Sweep"/>), under the tracker's lock; the groups killed. Never throws.</summary>
    public IReadOnlyList<int> Sweep()
    {
        lock (_lock)
        {
            return ShellRecords.Sweep(_folder, _startTicks ?? Files.RecordFiles.StartTicks, ShellGuard.KillGroup, ShellGuard.GroupAlive);
        }
    }

    /// <summary>Starts the guard when there is none running; under the lock. A failure is logged once and the run goes on unwatched.</summary>
    private void EnsureGuard()
    {
        if (_guardExecutable is null || _guardFailed || _guard is { HasExited: false })
        {
            return;
        }

        _guard?.Dispose();
        _guard = null;
        var start = new ProcessStartInfo(_guardExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,    // held, never written: its end is the app's
            RedirectStandardOutput = true,   // the guard says nothing; the TUI's terminal must not get what it might
            RedirectStandardError = true,
        };
        foreach (string argument in ShellGuard.Arguments(_folder))
        {
            start.ArgumentList.Add(argument);
        }

        // The guard lives as long as the app and needs none of its keys (the fourth shell review, 2026-10-19: e6f76814 cut them from
        // every other child that is not the shell's, this one was missed and held every NEONSIDEKICK_* secret for the session).
        ChildEnvironment.Withhold(start.Environment);

        try
        {
            var guard = Process.Start(start) ?? throw new InvalidOperationException("no process");
            guard.OutputDataReceived += (_, e) => Relay(e.Data);
            guard.ErrorDataReceived += (_, e) => Relay(e.Data);
            guard.BeginOutputReadLine();
            guard.BeginErrorReadLine();
            _guard = guard;
            DiagnosticLog.Debug(ShellKinds.Category, ShellText.GuardStarted(guard.Id));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _guardFailed = true;
            DiagnosticLog.Warn(ShellKinds.Category, ShellText.NoGuard(ex.Message));
        }
    }

    private static void Relay(string? line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            DiagnosticLog.Debug(ShellKinds.Category, ShellText.GuardSaid(line));
        }
    }
}
