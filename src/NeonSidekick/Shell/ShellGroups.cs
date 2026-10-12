using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Shell;

/// <summary>
/// The app's side of the crash watcher on a Mac (2026-10-12, the user's ask): the background command groups this run has alive, their
/// records (<see cref="ShellRecords"/>) and the guard (<see cref="ShellGuard"/>) that kills them should the app die. Set once by
/// <c>Program</c> as <see cref="Current"/>; null elsewhere and in the tests that do not make their own, so a run with none tracks nothing.
/// <list type="bullet">
/// <item><see cref="Track"/>, as a background command starts (<see cref="GroupChild"/>, <c>tiedToApp</c>): its record, and the guard
/// started if it is not running — once, at the first, and again should it have died. Only when this process is the app's own
/// executable (<see cref="EmbeddedLlm.LlamaGuard.DefaultExecutable"/>); a test host records without a guard.</item>
/// <item><see cref="Untrack"/>, before the leader is reaped: the record goes while the group id is still certainly ours.</item>
/// <item><see cref="KillTracked"/>, the exit signals' (SIGTERM, SIGQUIT, SIGHUP: no <c>finally</c> runs after them, so the registry is
/// never disposed): every tracked group killed at once. The records stay for the guard, which finds the groups gone.</item>
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
    private readonly Dictionary<int, string> _tracked = new();
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

    /// <summary>A background command's group, led by <paramref name="pgid"/>, started: recorded, and the guard made sure of.</summary>
    internal void Track(int pgid)
    {
        lock (_lock)
        {
            EnsureGuard();
            if (ShellRecords.Write(_folder, pgid, _startTicks) is { } path)
            {
                _tracked[pgid] = path;
            }
        }
    }

    /// <summary>The group's leader about to be reaped: its record removed.</summary>
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
                DiagnosticLog.Info(ShellKinds.Category, "group " + pgid.ToString(CultureInfo.InvariantCulture) + ": killed at exit");
                ShellGuard.KillGroup(pgid);
            }

            return _tracked.Count;
        }
    }

    /// <summary>The start's sweep over this tracker's folder (<see cref="ShellRecords.Sweep"/>); the groups killed. Never throws.</summary>
    public IReadOnlyList<int> Sweep() =>
        ShellRecords.Sweep(_folder, _startTicks ?? EmbeddedLlm.LlamaRecords.StartTicks, ShellGuard.KillGroup, ShellGuard.GroupAlive);

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

        try
        {
            var guard = Process.Start(start) ?? throw new InvalidOperationException("no process");
            guard.OutputDataReceived += (_, e) => Relay(e.Data);
            guard.ErrorDataReceived += (_, e) => Relay(e.Data);
            guard.BeginOutputReadLine();
            guard.BeginErrorReadLine();
            _guard = guard;
            DiagnosticLog.Debug(ShellKinds.Category, "shell guard: pid " + guard.Id.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _guardFailed = true;
            DiagnosticLog.Warn(ShellKinds.Category, "No shell guard (background commands are not stopped should the app crash): " + ex.Message);
        }
    }

    private static void Relay(string? line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            DiagnosticLog.Debug(ShellKinds.Category, "shell guard: " + line);
        }
    }
}
