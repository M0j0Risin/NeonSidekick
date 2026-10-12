using System.Diagnostics;
using System.Globalization;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The Mac's crash watcher for background commands (2026-10-12): the records (<see cref="ShellRecords"/>, pure over fakes on every
/// system), the app's tracker (<see cref="ShellGroups"/>, over real zsh groups on a Mac) and the guard (<see cref="ShellGuard"/>, the
/// app host beside the test assembly run as one, the <see cref="EmbeddedLlmMacTests"/> way).
/// </summary>
public sealed class ShellGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly ManualTimeProvider _time = new();
    private readonly Interpreters _interpreters = new(_ => null);

    public ShellGuardTests()
    {
        Directory.CreateDirectory(_dir);
        _folder = ShellRecords.FolderIn(_dir);
    }

    public void Dispose()
    {
        // Every Mac test sets its own tracker; none outlives it.
        if (OperatingSystem.IsMacOS())
        {
            ShellGroups.Current = null;
        }

        GitAccessTests.DeleteTree(_dir);
    }

    private ProcessLaunch Zsh(string command) => ShellCommandLine.For(ShellKind.Zsh, command, _interpreters.Locate(ShellKind.Zsh)!, _dir);

    private static async Task<int> ExitAsync(ProcessSession session) => await session.Exited.WaitAsync(TimeSpan.FromSeconds(60));

    // ── The records ─────────────────────────────────────────────────────────

    [Fact]
    public void ARecord_IsTwoLines_ReadBackWhole_OrNotAtAll()
    {
        var record = new ShellRecord(4321, 638000000000000000, 5555, 638000000010000000);
        string text = ShellRecords.Format(record);

        Assert.Equal("owner 4321 638000000000000000\nleader 5555 638000000010000000\n", text);
        Assert.Equal(record, ShellRecords.Parse(text));
        Assert.Equal(record, ShellRecords.Parse(text.Replace("\n", "\r\n", StringComparison.Ordinal)));
        Assert.Null(ShellRecords.Parse("owner 4321 1\n"));
        Assert.Null(ShellRecords.Parse("owner 4321 1\nleader 1 2\n"));   // launchd is never a leader of ours
        Assert.Null(ShellRecords.Parse("owner x 1\nleader 5555 2\n"));
        Assert.Equal(Path.Combine("/h", "shell", "running"), ShellRecords.FolderIn("/h"));
        Assert.Equal(Path.Combine("/f", "5555.txt"), ShellRecords.PathOf("/f", 5555));
    }

    private void Put(ShellRecord record) => File.WriteAllText(ShellRecords.PathOf(_folder, record.LeaderPid), ShellRecords.Format(record));

    [Fact]
    public void TheSweep_KillsOnlyWhatADeadOwnerLeft_WithItsLeaderStillTheRecordedProcess()
    {
        Directory.CreateDirectory(_folder);
        const long Second = TimeSpan.TicksPerSecond;
        var running = new Dictionary<int, long> { [100] = 10 * Second, [201] = 21 * Second, [301] = 31 * Second };
        Put(new ShellRecord(100, 10 * Second, 200, 20 * Second));   // its app is alive: kept, whatever its leader
        Put(new ShellRecord(101, 11 * Second, 201, 21 * Second));   // owner gone, leader the recorded one: killed
        Put(new ShellRecord(102, 12 * Second, 301, 99 * Second));   // owner gone, the pid now someone else's: left, unrecorded
        Put(new ShellRecord(103, 13 * Second, 401, 41 * Second));   // owner gone, leader gone, members maybe: named, not killed
        File.WriteAllText(Path.Combine(_folder, "junk.txt"), "not a record");
        var killed = new List<int>();

        var result = ShellRecords.Sweep(_folder, pid => running.TryGetValue(pid, out long t) ? t : null, killed.Add, pgid => pgid == 401);

        Assert.Equal([201], result);
        Assert.Equal([201], killed);
        Assert.Equal(["200.txt"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void TheGuardsKill_TakesEveryGroupItsOwnerStarted_AndOnlyThose()
    {
        Directory.CreateDirectory(_folder);
        Put(new ShellRecord(100, 10, 200, 20));
        Put(new ShellRecord(100, 10, 201, 21));
        Put(new ShellRecord(777, 70, 300, 30));
        var killed = new List<int>();

        Assert.Equal([200, 201], ShellRecords.KillOwned(_folder, 100, killed.Add).Order());
        Assert.Equal([200, 201], killed.Order());
        Assert.Equal(["300.txt"], Directory.GetFiles(_folder).Select(Path.GetFileName));
        Assert.Empty(ShellRecords.KillOwned(Path.Combine(_dir, "none"), 100, killed.Add));
    }

    [Fact]
    public void TheGuard_IsAskedByItsFlag_AndCarriesTheOwner_AndItsWordsArePinned()
    {
        Assert.True(ShellGuard.Asked(["--shell-guard", "/r", "1"]));
        Assert.False(ShellGuard.Asked(["--shell-guard", "/r"]));
        Assert.False(ShellGuard.Asked(["--llama-guard", "/r", "1"]));
        Assert.Equal(["--shell-guard", "/r", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)], ShellGuard.Arguments("/r"));
        Assert.Equal("A background command left running by NeonSidekick 12, which is gone: its process group 345 killed", ShellText.GroupLeftBehind(345, 12));
        Assert.Equal("A background command's process group 345, left by NeonSidekick 12, may still run; its leader is gone, so it was not killed (pgrep -l -g 345 lists it)", ShellText.GroupUnproven(345, 12));
    }

    [MacFact]
    public void TheSmokesProbe_WritesAndReadsARecord_AndKeepsALiveOwners()
    {
        var check = App.SmokeChecks.ProbeShellRecords();

        Assert.True(check.Passed, check.Detail);
        Assert.Equal("shell:records", check.Name);
    }

    // ── The tracker over real groups ────────────────────────────────────────

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public async Task ABackgroundCommand_IsRecordedWhileItsLeaderIsUnreaped_AForegroundOneNever()
    {
        var groups = new ShellGroups(_folder, guardExecutable: null);
        ShellGroups.Current = groups;
        var runner = new ShellRunner(_time);
        using var registry = new ProcessRegistry(runner, new Random(5), () => { });

        var background = registry.Start(Zsh("sleep 30"), notify: false);
        Assert.Equal([background.Pid], groups.Tracked);
        var record = ShellRecords.Parse(File.ReadAllText(ShellRecords.PathOf(_folder, background.Pid)));
        Assert.Equal(Environment.ProcessId, record!.OwnerPid);
        Assert.Equal(background.Pid, record.LeaderPid);
        Assert.False(groups.GuardRunning);   // a test host is no guard

        using (var foreground = runner.Start(Zsh("sleep 30"), "run_000001"))
        {
            Assert.DoesNotContain(foreground.Pid, groups.Tracked);
        }

        background.Kill();
        await ExitAsync(background);
        Assert.True(registry.Close(background));
        Assert.Empty(groups.Tracked);
        Assert.Empty(Directory.GetFiles(_folder));

        // One that ends leaving nothing behind is reaped at its exit, and unrecorded with it.
        var quick = registry.Start(Zsh("exit 0"), notify: false);
        await ExitAsync(quick);
        Assert.Empty(groups.Tracked);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public async Task TheExitSignalsKill_EndsEveryTrackedGroup()
    {
        var groups = new ShellGroups(_folder, guardExecutable: null);
        ShellGroups.Current = groups;
        using var registry = new ProcessRegistry(new ShellRunner(_time), new Random(6), () => { });
        var a = registry.Start(Zsh("sleep 30"), notify: false);
        var b = registry.Start(Zsh("sleep 30"), notify: false);

        Assert.Equal(2, groups.KillTracked());

        Assert.Equal(137, await ExitAsync(a));
        Assert.Equal(137, await ExitAsync(b));
    }

    // ── The guard, run for real ─────────────────────────────────────────────

    private static string AppHost => Path.Combine(AppContext.BaseDirectory, "NeonSidekick");

    private Process StartGuard()
    {
        var start = new ProcessStartInfo(AppHost) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var argument in ShellGuard.Arguments(_folder))
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)!;
    }

    private async Task<int> OrphanPidAsync(string pidFile)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            string path = Path.Combine(_dir, pidFile);
            if (File.Exists(path) && File.ReadAllText(path) is var text && text.EndsWith('\n') && int.TryParse(text.Trim(), out int pid))
            {
                return pid;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("no " + pidFile);
    }

    private static async Task<bool> GoneAsync(int pid)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            if (LlamaRecords.StartTicks(pid) is null)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    /// <summary>
    /// The app's death as the guard sees it (its stdin closed): the background command and what it left after its own inner shell
    /// quit — an orphan launchd adopted, still in the group — are killed, the record goes; the terminal's hangup and a TERM do not end it.
    /// </summary>
    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public async Task TheGuard_KillsItsOwnersGroups_AtTheEndOfItsStdin_AndSitsOutTheSignals()
    {
        ShellGroups.Current = new ShellGroups(_folder, guardExecutable: null);
        using var registry = new ProcessRegistry(new ShellRunner(_time), new Random(7), () => { });
        File.WriteAllText(Path.Combine(_dir, "leave.sh"), "sleep 120 </dev/null >/dev/null 2>&1 &\necho $! > orphan.pid\n");
        var background = registry.Start(Zsh("zsh ./leave.sh; sleep 120"), notify: false);
        int orphan = await OrphanPidAsync("orphan.pid");
        using var guard = StartGuard();
        try
        {
            await Task.Delay(500);   // the guard up and its signals registered
            foreach (string signal in new[] { "-HUP", "-TERM" })
            {
                using var kill = Process.Start("/bin/kill", [signal, guard.Id.ToString(CultureInfo.InvariantCulture)])!;
                await kill.WaitForExitAsync();
            }

            await Task.Delay(300);
            Assert.False(guard.HasExited, "a signal ended the guard");

            guard.StandardInput.Close();

            await guard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, guard.ExitCode);
            Assert.Equal(137, await ExitAsync(background));
            Assert.True(await GoneAsync(orphan), "the orphan outlived the guard's kill");
            Assert.Empty(Directory.GetFiles(_folder));
        }
        finally
        {
            if (!guard.HasExited)
            {
                guard.Kill();
            }

            if (LlamaRecords.StartTicks(orphan) is not null)
            {
                using var left = Process.GetProcessById(orphan);
                left.Kill();
            }
        }
    }

    /// <summary>
    /// The app's own path (2026-10-12): the tracker starts the guard from the app's executable at the first background command, and the
    /// guard's stdin closing (as the app's death closes it) ends that command's group.
    /// </summary>
    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public async Task TheTracker_StartsTheGuardAtTheFirstBackgroundCommand_WhichEndsItsGroupAtTheEnd()
    {
        var groups = new ShellGroups(_folder, AppHost);
        ShellGroups.Current = groups;
        using var registry = new ProcessRegistry(new ShellRunner(_time), new Random(9), () => { });
        Assert.False(groups.GuardRunning);

        var background = registry.Start(Zsh("sleep 120"), notify: false);
        Assert.True(groups.GuardRunning);
        await Task.Delay(500);   // the guard up and reading

        Assert.True(groups.ReleaseGuard(TimeSpan.FromSeconds(30)));

        Assert.Equal(137, await ExitAsync(background));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    /// <summary>The start's sweep over a real group whose recorded owner is gone (a start time that is not this process's): killed.</summary>
    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public async Task TheSweep_KillsARealGroupADeadOwnerLeft()
    {
        using var registry = new ProcessRegistry(new ShellRunner(_time), new Random(8), () => { });
        var left = registry.Start(Zsh("sleep 30"), notify: false);   // no tracker: recorded by hand below
        Directory.CreateDirectory(_folder);
        Put(new ShellRecord(Environment.ProcessId, 1, left.Pid, LlamaRecords.StartTicks(left.Pid)!.Value));

        Assert.Equal([left.Pid], new ShellGroups(_folder, guardExecutable: null).Sweep());

        Assert.Equal(137, await ExitAsync(left));
        Assert.Empty(Directory.GetFiles(_folder));
    }
}
