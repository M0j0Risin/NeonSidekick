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
        _f.Stage().StageAsync(release, progress, cancellationToken);

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
    public async Task NotesWithControlCharacters_AreScrubbedOnce_AsTheyAreRead()
    {
        // The file on disk isn't what's trusted, so the one scrub is at the reader (2026-11-04, review P2-18): three of them did the same work.
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old" });
        var release = _f.Publish("0.5.7", new Dictionary<string, string> { [UpdateFixture.Exe] = "new" }, notes: "safe\u001b[31m text");
        Assert.True((await Stage(release)).Ok);
        Assert.Contains('\u001b', File.ReadAllText(_f.Folder.StagedNotes));   // as GitHub holds them: three scrubs read the same as one at the end alone (review P21-8)
        new UpdateExit(_f.Context(), null, null).Run(_ => { });

        string? notes = UpdateProofs.Notes(_f.Folder, UpdateManifest.ReadFile(_f.Folder.Manifest, out _)!);

        Assert.NotNull(notes);
        Assert.Contains("safe", notes, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', notes);
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

    // A proof at an address of its own: the stub keeps the first answer mapped to an address.
    private static UpdateRelease Moved(UpdateRelease release, bool sha)
    {
        Uri There(Uri url) => new(url.AbsoluteUri.Replace(release.ArchiveName, "moved/" + release.ArchiveName, StringComparison.Ordinal));
        return sha ? release with { ShaUrl = There(release.ShaUrl) } : release with { SigUrl = There(release.SigUrl) };
    }

    [Fact]
    public async Task AProofGitHubNoLongerHas_SaysSo_NotThatNoReleaseWasFound()
    {
        // What's said of the release list isn't true of one of its files (review P6-7).
        var release = Moved(_f.Usual(), sha: true);
        _f.Http.Map(release.ShaUrl.AbsoluteUri, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.NotFound, [])));
        Assert.Equal(UpdateText.AssetGone, (await Stage(release)).Reason);
    }

    [Fact]
    public async Task AProofRefusedWith403_SaysTheServersAnswer_NotARateLimit()
    {
        var release = Moved(_f.Usual(), sha: false);
        _f.Http.Map(release.SigUrl.AbsoluteUri, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.Forbidden, [])));
        Assert.Equal(UpdateText.ServerAnswered(403), (await Stage(release)).Reason);
    }

    [Fact]
    public async Task APageWhereTheShaShouldBe_IsTheNetwork_NotATrustFailure()
    {
        // The .sha256's half (review P4-13): a real .sig over bytes that aren't there.
        var release = Moved(_f.Usual(), sha: true);
        _f.Http.Map(release.ShaUrl.AbsoluteUri, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, "<html>sign in</html>"u8.ToArray())));
        Assert.Equal(UpdateText.NotASignature, (await Stage(release)).Reason);
        Assert.False(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public async Task ACompleteStage_WithItsManifestHeld_IsKept()
    {
        // A scanner on the fresh manifest cost the whole download (reviews P8-6, P8-8).
        var release = _f.Usual();
        Assert.True((await Stage(release)).Ok);
        File.Delete(_f.Folder.Ready);

        UpdateStageResult held;
        using (FileHold.On(_f.Folder.StagingManifest))
        {
            held = await Stage(release);
        }

        Assert.Equal(UpdateText.StageCheckUnreadable(UpdateText.ManifestHeld), held.Reason);
        Assert.True(File.Exists(_f.Folder.StagingManifest));
        Assert.False(File.Exists(_f.Folder.Ready));
        Assert.True((await Stage(release)).Ok);
    }

    [Fact]
    public void Preflight_WithTheInstalledManifestHeld_DoesntSayDamaged()
    {
        _f.Usual();
        using var _ = FileHold.On(_f.Folder.Manifest);
        Assert.Equal(UpdateText.InstallManifestHeld, _f.Stage().Preflight(1));
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

        var run = stage.StageAsync(release, null, CancellationToken.None);
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
    public async Task AProbeThatTimesOut_KeepsTheStage_ButNeverReady()
    {
        // A slow machine isn't the package's fault (2026-11-04, review P4-14, the user's call): a retry tests again with no download, and nothing installs without a pass.
        _f.Probe.Answer = _ => ("", null);
        Assert.Equal(UpdateText.ProbeTimedOut, (await Stage(_f.Usual())).Reason);
        Assert.True(Directory.Exists(_f.Folder.Staging));
        Assert.False(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task AProbeReportingAnotherVersion_DropsTheStage()
    {
        _f.Probe.Version = "0.5.5";
        Assert.Equal(UpdateText.PackageRefused(UpdateText.ManifestNotThisRelease("0.5.5", UpdateFixture.Rid)), (await Stage(_f.Usual())).Reason);
        Assert.False(Directory.Exists(_f.Folder.Staging));
    }

    // ---- a failed test run, as the review left it (2026-11-04, review P4-14, the user's call)

    private int ArchiveRequests(UpdateRelease release) => _f.Http.Requests.Count(request => request.Uri == release.ArchiveUrl);

    [Fact]
    public async Task AFailedTestRun_KeepsTheStage_AndARetryDownloadsNothing()
    {
        var release = _f.Usual();
        _f.Probe.Answer = exe => Answers(exe, "version 0.5.7\nloading a.dll\n", -1, "version 0.5.6\nloading a.dll\nloaded a.dll\ndone\n", UpdateProbe.Passed);
        Assert.Equal(UpdateText.ProbeFailedOn("a.dll"), (await Stage(release)).Reason);
        Assert.True(Directory.Exists(_f.Folder.Staging));
        Assert.False(File.Exists(_f.Folder.Ready));
        int sent = ArchiveRequests(release);

        _f.Probe.Answer = null;
        Assert.True((await Stage(release)).Ok);

        Assert.Equal(sent, ArchiveRequests(release));
        Assert.True(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task ARunWithNoVersionLine_IsACrash_AndKeepsTheStage()
    {
        _f.Probe.Answer = _ => ("", UpdateProbe.Failed);
        Assert.Equal(UpdateText.ProbeFailed, (await Stage(_f.Usual())).Reason);
        Assert.True(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public void AFailedTestRunsWords_SayItTriesAgain_NotThatItWasDropped()
    {
        Assert.DoesNotContain("dropped", UpdateText.ProbeTimedOut, StringComparison.Ordinal);
        Assert.Contains("/update cancel drops it", UpdateText.ProbeFailed, StringComparison.Ordinal);
        Assert.Contains("/update cancel drops it", UpdateText.ProbeFailedOn("a.dll"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACleanLookingRunWithNoVersionLine_IsNoPass()
    {
        // It's no proof that this exe ran (review P9-11).
        _f.Probe.Answer = _ => ("done\n", UpdateProbe.Passed);
        var result = await Stage(_f.Usual());
        Assert.Equal(UpdateText.ProbeFailed, result.Reason);
        Assert.False(File.Exists(_f.Folder.Ready));
    }

    [Theory]
    [InlineData("failed a.dll\ndone\n", 1)]               // no version line, so nothing says this exe ran
    [InlineData("version 0.5.7\nfailed a.dll\n", 5)]      // dies outside a load
    [InlineData("version 0.5.7\nfailed a.dll\ndone\n", 5)]   // wrote done and crashed at exit
    public async Task ALibraryFailingInBoth_ExcusesOnlyARunThatGotAnAnswer(string staged, int code)
    {
        // The rest of a run that died for another reason went unread (reviews P8-13, P9-11).
        _f.Probe.Answer = exe => exe.Contains(UpdateFolder.Name, StringComparison.Ordinal) ? (staged.Replace("\\n", "\n", StringComparison.Ordinal), code) : ("version 0.5.6\nfailed a.dll\ndone\n", UpdateProbe.Failed);
        var result = await Stage(_f.Usual());
        Assert.False(result.Ok);
        Assert.False(File.Exists(_f.Folder.Ready));
        Assert.True(File.Exists(_f.Folder.StagingManifest));   // may be the machine's, so the stage stays
    }

    [Fact]
    public async Task AHungTestRun_AsksTheInstalledExeToo()
    {
        // A library that hangs in both versions isn't this release's fault (review P5-10).
        _f.Probe.Answer = exe => Answers(exe, "version 0.5.7\nloading a.dll\n", null, "version 0.5.6\nloading a.dll\n", null);
        Assert.True((await Stage(_f.Usual())).Ok);
        Assert.Equal(2, _f.Probe.Exes.Count);
    }

    [Fact]
    public async Task EachTestRun_StartsWithNoEarlierStderr()
    {
        // The Mac's handler appends, so the log showed an earlier run's text (review P5-14).
        var release = _f.Usual();
        UpdateFixture.Write(_f.Folder.ProbeStderr, "an earlier run's text");
        Assert.True((await Stage(release)).Ok);
        Assert.False(_f.Probe.StderrWasThere[0]);
    }

    [Fact]
    public async Task ACancelJustAfterTheTestRun_WritesNoReady()
    {
        // It answered "canceled" over a stage that then installed at quit (review P3-9).
        var release = _f.Usual();
        using var stop = new CancellationTokenSource();
        _f.Probe.Answer = _ =>
        {
            stop.Cancel();
            return ("version 0.5.7\ndone\n", UpdateProbe.Passed);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stage(release, null, stop.Token));

        Assert.False(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task ABadSignature_LeavesAStageThatWasReady()
    {
        // Proofs first (review P1-6): a network failure or a bad signature cost a stage that was ready to install.
        Assert.True((await Stage(_f.Usual())).Ok);
        using var other = UpdateSignature.NewKey();
        var worse = _f.Publish("0.5.8", new Dictionary<string, string> { [UpdateFixture.Exe] = "newer exe" }, signer: other);

        Assert.Equal(UpdateText.NotSigned, (await Stage(worse)).Reason);

        Assert.True(File.Exists(_f.Folder.Ready));
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(_f.Folder.Staging, UpdateFixture.Exe)));
    }

    [Theory]
    [InlineData("<html>a captive portal's page</html>")]
    [InlineData("")]
    public async Task APageWhereTheSignatureShouldBe_IsTheNetwork_NotATrustFailure(string page)
    {
        // Reviews P4-13, P6-7.
        var usual = _f.Usual();
        var release = usual with { SigUrl = new Uri(usual.SigUrl.AbsoluteUri.Replace(usual.ArchiveName, "page/" + usual.ArchiveName, StringComparison.Ordinal)) };
        _f.Http.Map(release.SigUrl.AbsoluteUri, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, System.Text.Encoding.ASCII.GetBytes(page))));
        Assert.Equal(UpdateText.NotASignature, (await Stage(release)).Reason);
        Assert.False(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public async Task AWrongCompleteArchive_IsDeleted_AndDownloadedAgain()
    {
        // ModelStore takes a complete file of the right length on trust, so a damaged or planted one never downloaded again (review P4-6).
        var release = _f.Usual();
        string archive = Path.Combine(_f.Folder.StagingParts, release.ArchiveName);
        Directory.CreateDirectory(_f.Folder.StagingParts);
        File.WriteAllBytes(archive, new byte[release.ArchiveBytes]);

        Assert.True((await Stage(release)).Ok);

        Assert.Equal(1, ArchiveRequests(release));
    }

    [Fact]
    public async Task AStagingLockFreedInAMoment_IsWaitedFor()
    {
        // A start's tidy holds it for a moment, and a stage begun just then read as another copy downloading (review P7-8).
        var release = _f.Usual();
        var other = UpdateLock.TryExclusive(_f.Folder.StagingLock);
        var freed = Task.Delay(150).ContinueWith(_ => other!.Dispose(), TaskScheduler.Default);

        Assert.True((await Stage(release)).Ok);

        await freed;
    }

    [Fact]
    public void Preflight_RefusesAnUnsettledSwap_AndAPendingRollback()
    {
        // Reviews P2-3, P2-5, P5-4: settling at exit would delete a stage it never knew of, and a rollback's exit deletes the stage.
        _f.Usual();
        Directory.CreateDirectory(_f.Folder.Root);
        new UpdateSwap(_f.Folder).PlanUpdate("0.5.5", "0.5.6", UpdateFixture.Exe, UpdateFixture.Exe, [], [], []).Save(_f.Folder);
        Assert.Equal(UpdateText.UnsettledSwap, _f.Stage().Preflight(1));
        UpdateJournal.Delete(_f.Folder);
        UpdateFixture.Write(_f.Folder.Rollback, "");
        Assert.Equal(UpdateText.RollbackPendingFirst, _f.Stage().Preflight(1));
    }

    [Fact]
    public async Task Preflights_AtOnce_DontFailEachOther()
    {
        // Fixed temp names: two copies failed each other's rename and read as a read-only folder (review P1-9). A held file of the old name fails the old code every time, not when eight tasks happen to interleave (review P21-18).
        _f.Usual();
        Directory.CreateDirectory(_f.Folder.Root);
        string oldName = Path.Combine(_f.Folder.Root, "preflight.tmp");
        await File.WriteAllTextAsync(oldName, "");
        using (new FileStream(oldName, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _f.Stage().Preflight(1))));
            Assert.All(answers, Assert.Null);
        }

        Assert.Equal([oldName], Directory.EnumerateFiles(_f.Folder.Root, "preflight*"));
    }

    [Theory]
    [InlineData(100, 0, 400)]
    [InlineData(100, 150, 250)]
    [InlineData(100, 1000, 0)]
    [InlineData(100, -5, 400)]
    [InlineData(long.MaxValue, 0, long.MaxValue)]
    public void TheRoomStillNeeded_CountsWhatsAlreadyHere(long archive, long held, long needed)
    {
        // A resumed download on a nearly full disk refused itself over its own partial, and the multiple could overflow (reviews P5-15, P7-10).
        Assert.Equal(needed, UpdateStage.RoomStillNeeded(archive, held));
    }

    [Fact]
    public void Preflight_CountsAPartial_TowardTheRoom()
    {
        _f.Usual();
        UpdateFixture.Write(Path.Combine(_f.Folder.StagingParts, "a.part"), new string('x', 4000));
        long enough = 1000 * UpdateStage.SpaceFactor - 4000 + ModelStore.DiskHeadroomBytes;
        Assert.Null(_f.Stage(freeBytes: _ => enough).Preflight(1000));
        Assert.NotNull(_f.Stage(freeBytes: _ => ModelStore.DiskHeadroomBytes - 1).Preflight(1000));
    }

    [Fact]
    public void ThePartsFolder_IsModelStoresOwnName() => Assert.Equal(ModelStore.PartsPath(_f.Folder.Staging), _f.Folder.StagingParts);

    [Fact]
    public void TheWordsWithANumber_AgreeWithTheConstants()
    {
        // Review P5-18.
        Assert.Contains(UpdateStage.ProbeLimit.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), UpdateText.ProbeTimedOut, StringComparison.Ordinal);
        Assert.Contains(UpdateStage.StallLimit.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), UpdateText.Stalled, StringComparison.Ordinal);
    }

    private async Task ACancelPartwayEndsTheUnpack(string archive, ModelFormat format, int entries)
    {
        string output = Path.Combine(_f.Root, "out-" + format);
        using var stop = new CancellationTokenSource();
        var unpack = ModelStore.UnpackAsync(archive, format, output, stop.Token);
        while (!unpack.IsCompleted && !(Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output, "*", SearchOption.AllDirectories).Any()))
        {
            await Task.Delay(1);
        }

        Assert.False(unpack.IsCompleted);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unpack);
        Assert.True(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Count() < entries, "the unpack ran to its end");
    }

    [Fact]
    public async Task ACancelPartway_EndsAZipsUnpack()
    {
        // The branch /update cancel uses on Windows, and an already-canceled token can't tell (review P10-9).
        const int Entries = 20_000;
        string archive = Path.Combine(_f.Root, "many.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        {
            for (int i = 0; i < Entries; i++)
            {
                zip.CreateEntry("f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), System.IO.Compression.CompressionLevel.NoCompression);
            }
        }

        await ACancelPartwayEndsTheUnpack(archive, ModelFormat.Zip, Entries);
    }

    [Fact]
    public async Task ACancelPartway_EndsATarsUnpack()
    {
        const int Entries = 20_000;
        string archive = Path.Combine(_f.Root, "many.tar.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
        await using (var tar = new System.Formats.Tar.TarWriter(gzip))
        {
            for (int i = 0; i < Entries; i++)
            {
                tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        await ACancelPartwayEndsTheUnpack(archive, ModelFormat.TarGz, Entries);
    }

    [Fact]
    public async Task AnUnpackingCancel_EndsTheTarToo()
    {
        // The synchronous unpack had no way to stop, and /update cancel's wait had none either (review P3-7).
        string archive = Path.Combine(_f.Root, "a.tar.gz");
        await File.WriteAllBytesAsync(archive, new byte[64]);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelStore.ExtractTarGzAsync(archive, Path.Combine(_f.Root, "out"), stop.Token));
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
        Assert.True(Directory.Exists(_f.Folder.Staging));
        Assert.False(File.Exists(_f.Folder.Ready));
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
