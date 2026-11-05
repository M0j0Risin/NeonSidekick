using NeonSidekick.App;
using NeonSidekick.Updates;

namespace NeonSidekick.Tests;

public sealed class UpdateProbeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("neon-probe-").FullName;
    private readonly string _result;

    public UpdateProbeTests()
    {
        _result = Path.Combine(_root, "result");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Make(params string[] files)
    {
        foreach (string file in files)
        {
            string full = Path.Combine(_root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
    }

    [Fact]
    public void AManifestThatDoesntRead_FailsTheRun_AsAMissingFile()
    {
        // A package whose manifest this version's reader refuses would install and leave no way to update (2026-11-04, review P2-14).
        Make("a.dll");
        int code = UpdateProbe.Run(_result, _root, "0.5.7", ["a.dll"], [], _ => true, manifestGood: false);

        Assert.Equal(UpdateProbe.Failed, code);
        Assert.Contains("missing " + UpdateManifest.FileName + "\n", File.ReadAllText(_result), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWatchdog_EndsARunPastItsLimit()
    {
        // A hung probe outlived a killed app (2026-11-04, review P5-13). The ending is a test's own here: the real one ends the process.
        using var ended = new ManualResetEventSlim();
        UpdateProbe.Watchdog(TimeSpan.FromMilliseconds(10), ended.Set);
        Assert.True(ended.Wait(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AKilledProbeThatWontEnd_IsLeftAfterItsLimit()
    {
        // A process stuck in the kernel held the staging lock for good (2026-11-04, review P6-4).
        await UpdateProbeProcess.WaitAfterKillAsync(new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(10)).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Asked_IsTheFlagAndOneArgument()
    {
        Assert.True(UpdateProbe.Asked(["--update-probe", "result"]));
        Assert.False(UpdateProbe.Asked(["--update-probe"]));
        Assert.False(UpdateProbe.Asked(["--update-probe", "a", "b"]));
        Assert.False(UpdateProbe.Asked(["--smoke", "a"]));
        Assert.False(UpdateProbe.Asked([]));
    }

    [Fact]
    public void ACleanRun_WritesTheVersion_AndThePinnedLines()
    {
        // A lasting contract: the old exe reads what the new one writes here.
        Make("a.dll", "b.dll", "voice.bin");
        int code = UpdateProbe.Run(_result, _root, "0.5.7", ["a.dll", "b.dll"], ["voice.bin"], _ => true);

        Assert.Equal(UpdateProbe.Passed, code);
        Assert.Equal(0, UpdateProbe.Passed);
        Assert.Equal("version 0.5.7\nloading a.dll\nloaded a.dll\nloading b.dll\nloaded b.dll\ndone\n", File.ReadAllText(_result));
        var read = UpdateProbeResult.ParseFile(_result);
        Assert.Equal("0.5.7", read.Version);
        Assert.True(read.Finished);
        Assert.Empty(read.Missing);
        Assert.Empty(read.Suspects);
    }

    [Fact]
    public void AMissingFileOrLibrary_IsReported_AndFails()
    {
        Make("a.dll");
        int code = UpdateProbe.Run(_result, _root, "0.5.7", ["a.dll", "gone.dll"], ["voice.bin"], _ => true);

        Assert.Equal(UpdateProbe.Failed, code);
        var read = UpdateProbeResult.ParseFile(_result);
        Assert.Equal(["voice.bin", "gone.dll"], read.Missing);
        Assert.True(read.Finished);
    }

    [Fact]
    public void AFailedLoad_NamesTheLibrary_AndTheOnesAfterItAreStillTried()
    {
        Make("a.dll", "b.dll", "c.dll");
        var tried = new List<string>();
        int code = UpdateProbe.Run(_result, _root, "0.5.7", ["a.dll", "b.dll", "c.dll"], [], path =>
        {
            tried.Add(Path.GetFileName(path));
            return !path.EndsWith("b.dll", StringComparison.Ordinal);
        });

        Assert.Equal(UpdateProbe.Failed, code);
        Assert.Equal(["a.dll", "b.dll", "c.dll"], tried);
        var read = UpdateProbeResult.ParseFile(_result);
        Assert.Equal(["b.dll"], read.FailedLoads);
        Assert.Null(read.CrashedOn);
    }

    [Fact]
    public void ACrash_NamesTheLibraryItWasLoading()
    {
        var read = UpdateProbeResult.Parse("version 0.5.7\nloading a.dll\nloaded a.dll\nloading b.dll\n");
        Assert.Equal("b.dll", read.CrashedOn);
        Assert.False(read.Finished);
        Assert.Equal(["b.dll"], read.Suspects);
    }

    [Fact]
    public void AMissingResultFile_IsAnEmptyAnswer_NeverACleanOne()
    {
        var read = UpdateProbeResult.ParseFile(Path.Combine(_root, "nope"));
        Assert.Null(read.Version);
        Assert.False(read.Finished);
    }

    [Fact]
    public void WhisperOnWindows_IsOnlyLookedFor_AndEveryOtherLibraryLoads()
    {
        string[] loads = UpdateProbe.Libraries(out string[] existOnly);

        Assert.Equal(SmokeChecks.RequiredNativeLibraries.Length, loads.Length + existOnly.Length);
        if (OperatingSystem.IsWindows())
        {
            Assert.All(existOnly, library => Assert.Contains("whisper", library, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(loads, library => library.Contains("whisper", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("onnxruntime.dll", loads);
        }
        else
        {
            Assert.Empty(existOnly);
        }
    }
}
