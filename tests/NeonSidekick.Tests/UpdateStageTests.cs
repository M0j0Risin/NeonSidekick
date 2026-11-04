using System.Net;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Updates;

namespace NeonSidekick.Tests;

public sealed class UpdateStageTests : IDisposable
{
    private readonly UpdateFixture _f = new();

    public void Dispose() => _f.Dispose();

    private Task<UpdateStageResult> Stage(UpdateRelease release, IProgress<UpdateStagePhase>? progress = null, CancellationToken cancellationToken = default) =>
        _f.Stage().StageAsync(release, progress, release.Notes, cancellationToken);

    [Fact]
    public async Task AGoodRelease_EndsReady_WithItsProofsKept()
    {
        var result = await Stage(_f.Usual());

        Assert.True(result.Ok, result.Reason);
        Assert.True(File.Exists(_f.Folder.Ready));
        Assert.True(File.Exists(_f.Folder.StagedSha));
        Assert.True(File.Exists(_f.Folder.StagedSig));
        Assert.Equal("the notes", File.ReadAllText(_f.Folder.StagedNotes));
        Assert.True(File.Exists(_f.Folder.StagedInstallHashes));
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(_f.Folder.Staging, UpdateFixture.Exe)));
        Assert.Equal("old exe", File.ReadAllText(_f.Folder.Full(UpdateFixture.Exe)));
        Assert.False(Directory.Exists(_f.Folder.StagingParts));
    }

    [Fact]
    public async Task TheStagedExe_IsWhatGetsProbed()
    {
        await Stage(_f.Usual());
        Assert.Equal([Path.Combine(_f.Folder.Staging, UpdateFixture.Exe)], _f.Probe.Exes);
    }

    [Fact]
    public async Task ProgressReports_DownloadingThenChecking()
    {
        var phases = new List<UpdateStagePhase>();
        var progress = new Inline<UpdateStagePhase>(phases.Add);
        await Stage(_f.Usual(), progress);

        Assert.Contains(phases, p => p is UpdateStagePhase.Downloading { Percent: 100 });
        Assert.IsType<UpdateStagePhase.Checking>(phases[^1]);
    }

    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public async Task NotesWithControlCharacters_AreSavedScrubbed()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        var release = _f.Publish("0.5.7", new Dictionary<string, string> { [UpdateFixture.Exe] = "new" }, notes: "safe\u001b[31m text");
        Assert.True((await Stage(release)).Ok);
        Assert.DoesNotContain('\u001b', File.ReadAllText(_f.Folder.StagedNotes));
    }

    [Fact]
    public async Task AWrongSignature_IsRefused_InstallUntouched()
    {
        _f.Usual();
        using var other = UpdateSignature.NewKey();
        var release = _f.Publish("0.5.8", new Dictionary<string, string> { [UpdateFixture.Exe] = "new" }, signer: other);

        var result = await Stage(release);

        Assert.Equal(UpdateText.NotSigned, result.Reason);
        Assert.False(File.Exists(_f.Folder.Ready));
        Assert.False(Directory.Exists(_f.Folder.Staging));
        Assert.Equal("old exe", File.ReadAllText(_f.Folder.Full(UpdateFixture.Exe)));
    }

    [Fact]
    public async Task AnOlderReleasesSignedFile_IsRefused()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        var release = _f.Publish("0.5.7", new Dictionary<string, string> { [UpdateFixture.Exe] = "new" }, shaArchiveName: "NeonSidekick-v0.5.5-win-x64.zip");
        Assert.Equal(UpdateText.NotThisRelease(release.ArchiveName), (await Stage(release)).Reason);
    }

    [Fact]
    public async Task AnAlteredPackage_IsRefused()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        var release = _f.Publish("0.5.7", new Dictionary<string, string> { [UpdateFixture.Exe] = "new" });
        var altered = release with { ArchiveBytes = release.ArchiveBytes + 1 };
        Assert.False((await Stage(altered)).Ok);
        Assert.False(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task AManifestNotMatchingTheSignedLine_IsRefused_AndDropped()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        var files = new Dictionary<string, string> { [UpdateFixture.Exe] = "new" };
        var release = _f.Publish("0.5.7", files, tamper: entries => entries[UpdateManifest.FileName] = UpdateFixture.Manifest("0.5.7", new Dictionary<string, string> { [UpdateFixture.Exe] = "evil" }));

        var result = await Stage(release);

        Assert.Equal(UpdateText.PackageBad(UpdateManifest.FileName), result.Reason);
        Assert.False(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public async Task AManifestWithTheWrongVersion_IsRefused()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        var files = new Dictionary<string, string> { [UpdateFixture.Exe] = "new" };
        var release = _f.Publish("0.5.7", files, manifestText: UpdateFixture.Manifest("0.5.9", files));
        Assert.Contains("0.5.9", (await Stage(release)).Reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public async Task AnotherPlatformsManifest_IsRefused()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        var files = new Dictionary<string, string> { [UpdateFixture.Exe] = "new" };
        var release = _f.Publish("0.5.7", files, manifestText: UpdateFixture.Manifest("0.5.7", files, rid: "osx-arm64"));
        Assert.Contains("osx-arm64", (await Stage(release)).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownManifestFormat_IsRefused()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        var files = new Dictionary<string, string> { [UpdateFixture.Exe] = "new" };
        var release = _f.Publish("0.5.7", files, manifestText: UpdateFixture.Manifest("0.5.7", files, format: 99));
        Assert.Equal(UpdateText.PackageRefused(UpdateText.ManifestUnknownFormat(99)), (await Stage(release)).Reason);
    }

    [Fact]
    public async Task AFileNotMatchingItsHash_IsRefused_AndDropped()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        var files = new Dictionary<string, string> { [UpdateFixture.Exe] = "new", ["a.dll"] = "a" };
        var release = _f.Publish("0.5.7", files, tamper: entries => entries["a.dll"] = "altered");
        Assert.Equal(UpdateText.PackageBad("a.dll"), (await Stage(release)).Reason);
        Assert.False(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public async Task AMissingListedFile_IsRefused()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        var files = new Dictionary<string, string> { [UpdateFixture.Exe] = "new", ["a.dll"] = "a" };
        var release = _f.Publish("0.5.7", files, tamper: entries => entries.Remove("a.dll"));
        Assert.Equal(UpdateText.PackageBad("a.dll"), (await Stage(release)).Reason);
    }

    [Fact]
    public async Task AReleaseFailingTheSignatureFetch_SaysWhy()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        var unmapped = new UpdateRelease(UpdateFixture.V("0.5.7"), null, "", "a.zip", new Uri("https://github.com/x/y/releases/download/a.zip"), 10, new Uri("https://github.com/x/y/releases/download/a.zip.sha256"), new Uri("https://github.com/x/y/releases/download/a.zip.sha256.sig"));
        Assert.False((await Stage(unmapped)).Ok);
    }

    [Fact]
    public async Task AStageOfAnotherVersion_IsReplaced_WithReadyGoneFirst()
    {
        var first = _f.Usual();
        Assert.True((await Stage(first)).Ok);

        var newer = _f.Publish("0.5.8", new Dictionary<string, string> { [UpdateFixture.Exe] = "newer exe" });
        _f.Probe.Version = "0.5.8";
        bool readyDuringProbe = true;
        _f.Probe.Answer = _ =>
        {
            readyDuringProbe = File.Exists(_f.Folder.Ready);
            return ("version 0.5.8\ndone\n", UpdateProbe.Passed);
        };

        Assert.True((await Stage(newer)).Ok);

        Assert.False(readyDuringProbe);
        Assert.Equal("newer exe", File.ReadAllText(Path.Combine(_f.Folder.Staging, UpdateFixture.Exe)));
        Assert.False(File.Exists(Path.Combine(_f.Folder.Staging, "a.dll")));
    }

    [Fact]
    public async Task APartialOfAnotherVersion_IsDeleted()
    {
        var release = _f.Usual();
        string other = Path.Combine(_f.Folder.StagingParts, "NeonSidekick-v0.5.5-win-x64.zip.partial");
        UpdateFixture.Write(other, "half of it");
        await Stage(release);
        Assert.False(File.Exists(other));
    }

    [Fact]
    public async Task ACompleteStageWithNoReady_IsCheckedAndProbedAgain_NotDownloadedAgain()
    {
        var release = _f.Usual();
        Assert.True((await Stage(release)).Ok);
        File.Delete(_f.Folder.Ready);
        int downloads = _f.Http.Requests.Count(r => r.Uri == release.ArchiveUrl);

        Assert.True((await Stage(release)).Ok);

        Assert.Equal(downloads, _f.Http.Requests.Count(r => r.Uri == release.ArchiveUrl));
        Assert.Equal(2, _f.Probe.Exes.Count);
        Assert.True(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task AKilledRunsExtractingFolder_IsRemoved()
    {
        var release = _f.Usual();
        string leftover = Path.Combine(_f.Folder.Root, "staging.abc.extracting");
        UpdateFixture.Write(Path.Combine(leftover, "x"), "x");
        await Stage(release);
        Assert.False(Directory.Exists(leftover));
    }

    [Fact]
    public async Task AnotherCopyHoldingTheLock_IsARefusal()
    {
        var release = _f.Usual();
        Directory.CreateDirectory(_f.Folder.Root);
        using var other = UpdateLock.TryExclusive(_f.Folder.StagingLock);
        Assert.Equal(UpdateText.AnotherCopyStaging, (await Stage(release)).Reason);
    }

    [Fact]
    public async Task AStalledDownload_FailsAfterTheLimit_AndKeepsThePartial()
    {
        var release = _f.Usual();
        var stalled = new StubHttpMessageHandler();
        var unblock = new TaskCompletionSource();
        var time = new ManualTimeProvider();

        // Half the archive, then nothing until the watch cancels it.
        var client = new HttpClient(_f.Http);
        _f.Http.Requests.Clear();
        var blocking = new HangingAfter(new byte[(int)release.ArchiveBytes / 2]);
        var stub = new StubHttpMessageHandler();
        stub.Map(release.SigUrl.ToString(), (r, c) => Forward(r, c));
        stub.Map(release.ShaUrl.ToString(), (r, c) => Forward(r, c));
        stub.Map(release.ArchiveUrl.ToString(), (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(blocking) { Headers = { ContentLength = release.ArchiveBytes } },
        }));

        Task<HttpResponseMessage> Forward(HttpRequestMessage request, CancellationToken cancellationToken) => client.SendAsync(new HttpRequestMessage(HttpMethod.Get, request.RequestUri), cancellationToken);

        using var http = new HttpClient(stub);
        var context = _f.Context();
        var stage = new UpdateStage(context, new ModelStore(_f.Folder.Root, http, UpdateJournal.Category) { AvailableBytes = _ => null }, new UpdateCheck(http, UpdateSource.GitHub(App.AboutText.RepositoryUrl), context.Running, UpdateFixture.Rid), _f.Probe, time, _ => null);

        var run = stage.StageAsync(release, null, null, CancellationToken.None);
        await blocking.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        while (!run.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(5);
        }

        var result = await run;
        Assert.Equal(UpdateText.Stalled, result.Reason);
        Assert.True(ModelStore.PartialBytes(Path.Combine(_f.Folder.StagingParts, release.ArchiveName)) > 0);
        Assert.False(File.Exists(_f.Folder.Ready));
        _ = stalled;
        _ = unblock;
    }

    private sealed class HangingAfter(byte[] first) : Stream
    {
        private int _sent;
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent < first.Length)
            {
                int count = Math.Min(buffer.Length, first.Length - _sent);
                first.AsMemory(_sent, count).CopyTo(buffer);
                _sent += count;
                return count;
            }

            _reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ACancel_KeepsThePartial_AndNoReady()
    {
        var release = _f.Usual();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stage(release, cancellationToken: cancel.Token));
        Assert.False(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public void Preflight_RefusesAFolderWithNoManifest()
    {
        Assert.Equal(UpdateText.NotAPackage, _f.Stage().Preflight(100));
    }

    [Fact]
    public void Preflight_RefusesADamagedManifest_WithTheGuardStillOn()
    {
        UpdateFixture.Write(_f.Folder.Manifest, "not json");
        Assert.Equal(UpdateText.DamagedInstall(UpdateText.ManifestUnreadable), _f.Stage().Preflight(100));
        Assert.True(_f.Folder.IsPackage);
    }

    [Fact]
    public void Preflight_RefusesARenamedCopy()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        Assert.Equal(UpdateText.RenamedCopy(UpdateFixture.Exe), _f.Stage(exeName: "Copy.exe").Preflight(100));
    }

    [Fact]
    public void Preflight_PassesTheExeLaunchedInOtherCasing_OnACaseBlindVolume()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Null(_f.Stage(exeName: "NEONSIDEKICK.EXE").Preflight(100));
        }
    }

    [Fact]
    public void Preflight_GoesOn_WhenFreeSpaceCantBeRead()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        Assert.Null(_f.Stage(freeBytes: _ => null).Preflight(100));
    }

    [Fact]
    public void Preflight_RefusesTooLittleSpace_AndNamesTheFolder()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        string? reason = _f.Stage(freeBytes: _ => ModelStore.DiskHeadroomBytes).Preflight(100);
        Assert.Contains(_f.Root, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Preflight_LeavesNoFileBehind()
    {
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        _f.Stage().Preflight(100);
        Assert.Empty(Directory.GetFiles(_f.Folder.Root));
    }

    [Fact]
    public void FreeBytes_AsksTheFolderItself_ThroughTheNearestThatExists()
    {
        Assert.NotNull(ModelStore.FreeBytesOn(_f.Root));
        Assert.NotNull(ModelStore.FreeBytesOn(Path.Combine(_f.Root, "not", "made", "yet")));
    }

    [Fact]
    public async Task AProbeThatTimesOut_DropsTheStage()
    {
        _f.Probe.Answer = _ => ("", null);
        Assert.Equal(UpdateText.ProbeTimedOut, (await Stage(_f.Usual())).Reason);
        Assert.False(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public async Task AProbeReportingAnotherVersion_DropsTheStage()
    {
        _f.Probe.Version = "0.5.5";
        Assert.Equal(UpdateText.ProbeFailed, (await Stage(_f.Usual())).Reason);
        Assert.False(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public async Task AProbeWithAMissingFile_Refuses_WithNoInstalledRun()
    {
        _f.Probe.Answer = _ => ("version 0.5.7\nmissing voices/a.bin\ndone\n", UpdateProbe.Failed);
        Assert.Equal(UpdateText.PackageBad("voices/a.bin"), (await Stage(_f.Usual())).Reason);
        Assert.Single(_f.Probe.Exes);
    }

    [Fact]
    public async Task AnExeThatWontStart_Refuses()
    {
        _f.Probe.Answer = _ => ("", UpdateProbe.Failed);
        Assert.Equal(UpdateText.ProbeFailed, (await Stage(_f.Usual())).Reason);
    }

    private (string, int?) Answers(string exe, string staged, int? stagedCode, string installed, int? installedCode) =>
        exe.StartsWith(_f.Folder.Staging, StringComparison.Ordinal) ? (staged, stagedCode) : (installed, installedCode);

    [Fact]
    public async Task ALibraryFailingInBoth_IsIgnored()
    {
        const string fails = "loading onnxruntime.dll\nfailed onnxruntime.dll\ndone\n";
        _f.Probe.Answer = exe => Answers(exe, "version 0.5.7\n" + fails, UpdateProbe.Failed, "version 0.5.6\n" + fails, UpdateProbe.Failed);
        Assert.True((await Stage(_f.Usual())).Ok);
        Assert.Equal(2, _f.Probe.Exes.Count);
    }

    [Fact]
    public async Task ALibraryFailingOnlyInTheStage_IsRefused()
    {
        _f.Probe.Answer = exe => Answers(exe, "version 0.5.7\nloading a.dll\nfailed a.dll\ndone\n", UpdateProbe.Failed, "version 0.5.6\nloading a.dll\nloaded a.dll\ndone\n", UpdateProbe.Passed);
        Assert.Equal(UpdateText.ProbeFailedOn("a.dll"), (await Stage(_f.Usual())).Reason);
        Assert.False(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public async Task ALibraryNewInThisRelease_FailingToLoad_IsRefused()
    {
        _f.Probe.Answer = exe => Answers(exe, "version 0.5.7\nloading new.dll\nfailed new.dll\ndone\n", UpdateProbe.Failed, "version 0.5.6\ndone\n", UpdateProbe.Passed);
        Assert.Equal(UpdateText.ProbeFailedOn("new.dll"), (await Stage(_f.Usual())).Reason);
    }

    [Fact]
    public async Task ACrashInBoth_LetsTheUpdateGoOn()
    {
        _f.Probe.Answer = exe => Answers(exe, "version 0.5.7\nloading a.dll\n", -1, "version 0.5.6\nloading a.dll\n", -1);
        Assert.True((await Stage(_f.Usual())).Ok);
    }

    [Fact]
    public async Task ACrashOnlyInTheStage_IsRefused()
    {
        _f.Probe.Answer = exe => Answers(exe, "version 0.5.7\nloading a.dll\n", -1, "version 0.5.6\nloading a.dll\nloaded a.dll\ndone\n", UpdateProbe.Passed);
        Assert.Equal(UpdateText.ProbeFailedOn("a.dll"), (await Stage(_f.Usual())).Reason);
    }

    [Fact]
    public async Task AnInstalledProbeThatHangs_CountsAsNotFailing_SoTheStageIsRefused()
    {
        _f.Probe.Answer = exe => Answers(exe, "version 0.5.7\nloading a.dll\nfailed a.dll\ndone\n", UpdateProbe.Failed, "", null);
        Assert.Equal(UpdateText.ProbeFailedOn("a.dll"), (await Stage(_f.Usual())).Reason);
    }

    [Fact]
    public async Task AnOlderRunsResult_IsNeverReadAsACrashsAnswer()
    {
        var release = _f.Usual();
        UpdateFixture.Write(_f.Folder.ProbeResult, "version 0.5.7\ndone\n");
        _f.Probe.Answer = _ => ("", -1);
        Assert.Equal(UpdateText.ProbeFailed, (await Stage(release)).Reason);
    }
}
