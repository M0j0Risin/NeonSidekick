using System.Diagnostics;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The embedded LLM's Mac pieces (2026-10-07): the server records and their sweep, the guard, the catalog without the NVFP4
/// builds and Metal's memory. The record format and the sweep's decisions are pure and run everywhere; the guard runs for
/// real on a Unix machine, the app host beside this assembly as the guard and <c>/bin/sleep</c> or <c>/bin/sh</c> as the server.
/// </summary>
public sealed class EmbeddedLlmMacTests : IDisposable
{
    private readonly string _llama;

    public EmbeddedLlmMacTests()
    {
        // Resolved, as the records keep paths (the temp folder is under a link into /private on macOS).
        string llama = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"), "llama");
        Directory.CreateDirectory(llama);
        _llama = Files.RealPath.Of(llama) ?? llama;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_llama)!, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // never made
        }
    }

    // ── The records ─────────────────────────────────────────────────────────

    [Fact]
    public void ARecord_IsPinned_AndReadsBack()
    {
        var record = new LlamaRecord(100, 638_000_000_000_000_000, 200, 638_000_000_010_000_000, "/Users/x/.neonsidekick/llama/b11258-metal/llama-server");

        string text = LlamaRecords.Format(record);

        Assert.Equal("owner 100 638000000000000000\nserver 200 638000000010000000\nexecutable /Users/x/.neonsidekick/llama/b11258-metal/llama-server\n", text);
        Assert.Equal(record, LlamaRecords.Parse(text));
        Assert.Null(LlamaRecords.Parse("owner 100 1\nexecutable /x\n"));   // no server line
        Assert.Null(LlamaRecords.Parse(""));
        Assert.Equal(Path.Combine("home", "llama", "running"), LlamaRecords.Folder(Path.Combine("home", "llama")));
    }

    private string Recorded(int owner, long ownerStart, int server, long serverStart, string? executable = null)
    {
        string folder = LlamaRecords.Folder(_llama);
        Directory.CreateDirectory(folder);
        string path = LlamaRecords.PathOf(folder, server);
        File.WriteAllText(path, LlamaRecords.Format(new LlamaRecord(owner, ownerStart, server, serverStart, executable ?? Server)));
        return path;
    }

    private string Server => Path.Combine(Path.GetFullPath(_llama), "b11258-metal", "llama-server");

    private List<int> Sweep(Dictionary<int, long> starts, Dictionary<int, string>? executables = null)
    {
        var killed = new List<int>();
        LlamaRecords.Sweep(
            _llama,
            pid => starts.TryGetValue(pid, out long ticks) ? ticks : null,
            pid => executables is not null ? executables.GetValueOrDefault(pid) : starts.ContainsKey(pid) ? Server : null,
            killed.Add);
        return killed;
    }

    [Fact]
    public void TheSweep_KillsTheServer_OfAnAppThatIsGone_AndRemovesItsRecord()
    {
        string record = Recorded(owner: 100, ownerStart: 5, server: 200, serverStart: 7);

        var killed = Sweep(new() { [200] = 7 });

        Assert.Equal([200], killed);
        Assert.False(File.Exists(record));
    }

    [Fact]
    public void TheSweep_KeepsALiveAppsServer_ThatOfASecondAppOnTheSameHome()
    {
        string record = Recorded(owner: 100, ownerStart: 5, server: 200, serverStart: 7);

        Assert.Empty(Sweep(new() { [100] = 5, [200] = 7 }));
        Assert.True(File.Exists(record));
    }

    [Fact]
    public void TheSweep_TakesAnOwnerPidReused_ForAnAppThatIsGone()
    {
        // pid 100 is running, but it started at another time: another program, so the owner is gone.
        Recorded(owner: 100, ownerStart: 5, server: 200, serverStart: 7);

        Assert.Equal([200], Sweep(new() { [100] = 5 + TimeSpan.FromMinutes(3).Ticks, [200] = 7 }));
    }

    [Fact]
    public void TheSweep_NeverKills_APidTheSystemGaveToSomethingElse()
    {
        // The server's pid now belongs to another process: started at another time, or another executable.
        string first = Recorded(owner: 100, ownerStart: 5, server: 200, serverStart: 7);
        string second = Recorded(owner: 101, ownerStart: 5, server: 201, serverStart: 7);

        var killed = Sweep(
            new() { [200] = 7 + TimeSpan.FromMinutes(1).Ticks, [201] = 7 },
            new() { [200] = Server, [201] = "/bin/sleep" });

        Assert.Empty(killed);
        Assert.False(File.Exists(first));   // the record of a server that is gone goes
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void TheSweep_NeverKills_AnExecutableOutsideTheLlamaFolder()
    {
        Recorded(owner: 100, ownerStart: 5, server: 200, serverStart: 7, executable: "/usr/bin/llama-server");

        Assert.Empty(Sweep(new() { [200] = 7 }, new() { [200] = "/usr/bin/llama-server" }));
    }

    [Fact]
    public void TheSweep_RemovesAGarbledRecord_AndHasNothingToDo_WithNoFolder()
    {
        Assert.Empty(LlamaRecords.Sweep(_llama, _ => null, _ => null, _ => throw new InvalidOperationException()));
        string folder = LlamaRecords.Folder(_llama);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "300.txt"), "nothing here");

        Assert.Empty(Sweep([]));
        Assert.False(File.Exists(Path.Combine(folder, "300.txt")));
    }

    [UnixFact]
    public void TheRecords_KeepResolvedPaths_SoAHomeBehindALinkStillMatches()
    {
        // The kernel names a process's executable resolved; a home spelled through a link (/tmp, /var on macOS) must match it.
        string link = Path.Combine(Path.GetDirectoryName(_llama)!, "via-link");
        Directory.CreateSymbolicLink(link, _llama);
        string folder = LlamaRecords.Folder(link);
        using var self = Process.GetCurrentProcess();

        Directory.CreateDirectory(Path.GetDirectoryName(Server)!);
        File.WriteAllText(Server, "");   // realpath resolves what exists, as the runtime's executable does
        string? path = LlamaRecords.Write(folder, self.Id, self, Path.Combine(link, "b11258-metal", "llama-server"));

        Assert.Equal(Server, LlamaRecords.Parse(File.ReadAllText(path!))!.Executable);
        File.Delete(path!);
        Recorded(owner: 100, ownerStart: 5, server: 200, serverStart: 7);
        var killed = new List<int>();
        LlamaRecords.Sweep(link, pid => pid == 200 ? 7 : null, _ => Server, killed.Add);
        Assert.Equal([200], killed);   // swept through the link's spelling, matched by the resolved path
    }

    [Fact]
    public void ThePrune_LeavesTheRecordsBe()
    {
        Recorded(owner: 100, ownerStart: 5, server: 200, serverStart: 7);
        Directory.CreateDirectory(Path.Combine(_llama, "b1-metal"));
        var files = new EmbeddedModels(Path.Combine(Path.GetDirectoryName(_llama)!, "models"), _llama, new HttpClient(new StubHttpMessageHandler()));

        files.PruneOldRuntimes();

        Assert.False(Directory.Exists(Path.Combine(_llama, "b1-metal")));
        Assert.True(Directory.Exists(LlamaRecords.Folder(_llama)));
    }

    [Fact]
    public void TheSweepLine_IsPinned()
    {
        Assert.Equal("Killing llama-server 200, left running by NeonSidekick 100, which is gone.", EmbeddedLlmText.LeftBehind(200, 100));
    }

    // ── The guard ───────────────────────────────────────────────────────────

    [Fact]
    public void TheGuard_IsAskedByItsFlag_AndCarriesTheOwner()
    {
        Assert.True(LlamaGuard.Asked(["--llama-guard", "/r", "1", "/s"]));
        Assert.False(LlamaGuard.Asked(["--llama-guard", "/r", "1"]));
        Assert.False(LlamaGuard.Asked(["--headless"]));
        Assert.Equal(
            ["--llama-guard", "/r", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "/s", "-m", "x"],
            LlamaGuard.Arguments("/r", "/s", ["-m", "x"]));
        Assert.Null(LlamaGuard.DefaultExecutable());   // a test host is no guard
    }

    private static string AppHost => Path.Combine(AppContext.BaseDirectory, "NeonSidekick");

    private Process StartGuard(string server, params string[] arguments)
    {
        var start = new ProcessStartInfo(AppHost) { UseShellExecute = false, RedirectStandardInput = true };
        foreach (var argument in LlamaGuard.Arguments(LlamaRecords.Folder(_llama), server, arguments))
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)!;
    }

    private async Task<LlamaRecord> RecordAsync()
    {
        string folder = LlamaRecords.Folder(_llama);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.txt").FirstOrDefault() is { } path
                && LlamaRecords.Parse(File.ReadAllText(path)) is { } record)
            {
                return record;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("the guard wrote no record");
    }

    [UnixFact]
    public async Task TheGuard_KillsTheServer_WhenItsStdinCloses_AsTheAppsDeathWouldCloseIt()
    {
        using var guard = StartGuard("/bin/sleep", "300");
        var record = await RecordAsync();
        Assert.Equal(Environment.ProcessId, record.OwnerPid);
        Assert.Equal("/bin/sleep", LlamaRecords.ExecutableOf(record.ServerPid));

        guard.StandardInput.Close();

        await guard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, guard.ExitCode);
        Assert.Null(LlamaRecords.StartTicks(record.ServerPid));
        Assert.Empty(Directory.EnumerateFiles(LlamaRecords.Folder(_llama)));
    }

    [UnixFact]
    public async Task TheGuard_EndsWithTheServer_AndItsExitCode()
    {
        using var guard = StartGuard("/bin/sh", "-c", "sleep 1; exit 7");
        await RecordAsync();

        await guard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(7, guard.ExitCode);
        Assert.Empty(Directory.EnumerateFiles(LlamaRecords.Folder(_llama)));
    }

    [UnixFact]
    public async Task TheGuard_SaysWhy_AServerThatDoesNotStart()
    {
        var start = new ProcessStartInfo(AppHost) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var argument in LlamaGuard.Arguments(LlamaRecords.Folder(_llama), "/nonexistent/llama-server", []))
        {
            start.ArgumentList.Add(argument);
        }

        using var guard = Process.Start(start)!;
        string error = await guard.StandardError.ReadToEndAsync();
        await guard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(127, guard.ExitCode);
        Assert.StartsWith("llama-guard: llama-server did not start", error, StringComparison.Ordinal);
    }

    // ── The catalog and memory ──────────────────────────────────────────────

    [Fact]
    public void TheNvfp4Builds_AreLeftOutOnAMac_AndFoundEverywhere()
    {
        var nvfp4 = EmbeddedModelCatalog.Models.Where(EmbeddedModelCatalog.IsNvfp4).ToList();
        Assert.Equal(8, nvfp4.Count);
        Assert.All(nvfp4, m => Assert.Contains("NVFP4", m.Display, StringComparison.Ordinal));
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(EmbeddedModelCatalog.Models.Count - 8, EmbeddedModelCatalog.ForThisMachine.Count);
            Assert.DoesNotContain(EmbeddedModelCatalog.ForThisMachine, EmbeddedModelCatalog.IsNvfp4);
        }
        else
        {
            Assert.Same(EmbeddedModelCatalog.Models, EmbeddedModelCatalog.ForThisMachine);
        }

        Assert.Same(nvfp4[0], EmbeddedModelCatalog.Find(nvfp4[0].Id));
    }

    [Fact]
    public void TheEmbeddedModel_IsOffered_OnWindowsX64_AndAppleSilicon()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        bool expected = (OperatingSystem.IsWindows() && arch == System.Runtime.InteropServices.Architecture.X64)
            || (OperatingSystem.IsMacOS() && arch == System.Runtime.InteropServices.Architecture.Arm64);
        Assert.Equal(expected, EmbeddedEndpoint.Offered);
    }

    [Fact]
    public void MetalsWorkingSet_IsTheGpusMemory_OnAMac()
    {
        long? metal = Perf.GpuMemory.MetalWorkingSetBytes();
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Null(metal);
            return;
        }

        // A Mac without a Metal device (a CI virtual machine may be one) reads none.
        if (metal is { } bytes)
        {
            Assert.InRange(bytes, 1, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);   // a CI VM's virtual GPU may report little
            Assert.Equal(metal, Perf.GpuMemory.DedicatedBytes());
        }
    }
}
