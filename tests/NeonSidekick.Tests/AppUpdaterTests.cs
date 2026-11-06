using System.Net;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Updates;

namespace NeonSidekick.Tests;

/// <summary>The screen's door over a real <c>.update/</c> in a temp folder (2026-11-04, self-update). Its stage starts a real probe, so <see cref="UpdateStage"/>'s own tests hold that part.</summary>
public sealed class AppUpdaterTests : IDisposable
{
    private const string Latest = "https://api.github.com/repos/M0j0Risin/NeonSidekick/releases/latest";

    private readonly UpdateFixture _f = new();

    public void Dispose() => _f.Dispose();

    private AppUpdater Updater(string? startLine = null, string running = "0.5.6") => new(_f.Context(running), new UpdateStartResult(null, startLine), _f.Client);

    private async Task Ready()
    {
        var result = await _f.Stage().StageAsync(_f.Usual(), null, CancellationToken.None);
        Assert.True(result.Ok, result.Reason);
    }

    private void Swapped() => new UpdateExit(_f.Context(), null, null).Run(_ => { });

    [Fact]
    public void AFolderWithNoManifest_IsNoPackage_AndItsCheckAsksNothing()
    {
        var updater = Updater("a line");
        Assert.False(updater.IsPackage);
        Assert.Equal("a line", updater.StartLine);
        Assert.Equal("0.5.6", updater.Running);
        Assert.Equal(new UpdateDiskState(null, null), updater.ReadDisk());
        Assert.Null(updater.RunningNotes());
    }

    [Fact]
    public async Task ACheck_WithNoManifest_FailsWithoutARequest()
    {
        var answer = await Updater().CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateCheckKind.Failed, answer.Kind);
        Assert.NotNull(answer.Reason);
    }

    [Fact]
    public async Task ACheck_AsksForTheInstalledPackagesPlatform_AndAFailureIsAnAnswer()
    {
        _f.Usual();
        Uri? asked = null;
        _f.Http.Map(Latest, (request, _) =>
        {
            asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        });

        var answer = await Updater().CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckKind.Failed, answer.Kind);
        Assert.Equal(Latest, asked?.AbsoluteUri);
    }

    [Fact]
    public async Task AReadyStage_IsReadFromDisk_AndDroppedKeepingNothingOfIt()
    {
        await Ready();
        var updater = Updater();
        Assert.Equal("0.5.7", updater.ReadDisk().ReadyVersion);

        Assert.Null(updater.DropStage(alsoParts: false));

        Assert.Null(updater.ReadDisk().ReadyVersion);
        Assert.False(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task ADrop_WhileAnotherCopyHoldsTheStagingLock_IsRefused_AndTheStageStays()
    {
        await Ready();
        using (UpdateLock.TryExclusive(_f.Folder.StagingLock))
        {
            Assert.Equal(UpdateText.AnotherCopyStaging, Updater().DropStage(alsoParts: false));
        }

        Assert.True(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public void Rollback_WithNoPrevious_IsNothingToGoBackTo()
    {
        _f.Usual();
        Assert.Equal(UpdateText.NothingToGoBackTo, Updater().AskRollback(CancellationToken.None, out string? target, out _));
        Assert.Null(target);
    }

    [Fact]
    public async Task Rollback_AfterAnUpdate_IsAsked_ReadFromDisk_AndTakenBack()
    {
        await Ready();
        Swapped();
        var updater = Updater(running: "0.5.7");

        Assert.Null(updater.AskRollback(CancellationToken.None, out string? target, out _));

        Assert.Equal("0.5.6", target);
        Assert.Equal("0.5.6", updater.ReadDisk().RollbackTarget);
        updater.CancelRollback();
        Assert.Null(updater.ReadDisk().RollbackTarget);
    }

    [Fact]
    public async Task ARefusedRollback_LeavesAStageInPlace()
    {
        await Ready();
        Assert.NotNull(Updater().AskRollback(CancellationToken.None, out _, out _));
        Assert.Equal("0.5.7", Updater().ReadDisk().ReadyVersion);
    }

    [Fact]
    public async Task Rollback_KeepsAStageOnDisk_AndCancelBringsItBack()
    {
        // Review P3-13: it was deleted at the command, and /update cancel couldn't bring it back.
        await Ready();
        Swapped();
        _f.Probe.Version = "0.5.8";
        var newer = _f.Publish("0.5.8", new Dictionary<string, string> { [UpdateFixture.Exe] = "newer exe" });
        Assert.True((await _f.Stage("0.5.7").StageAsync(newer, null, CancellationToken.None)).Ok);
        var updater = Updater(running: "0.5.7");

        Assert.Null(updater.AskRollback(CancellationToken.None, out string? target, out _));

        Assert.Equal("0.5.6", target);
        Assert.Equal(new UpdateDiskState("0.5.8", "0.5.6"), updater.ReadDisk());
        Assert.Null(updater.CancelRollback());
        Assert.Equal(new UpdateDiskState("0.5.8", null), updater.ReadDisk());
    }

    [Fact]
    public async Task Rollback_RefusesAnUnsettledSwap_AndARenamedCopy()
    {
        // Reviews P2-3, P4-5: settling at exit would delete what this asks for, and a renamed copy's exit doesn't swap.
        await Ready();
        Swapped();
        Assert.Equal(UpdateText.RenamedCopy(UpdateFixture.Exe), new AppUpdater(_f.Context("0.5.7", "Copy.exe"), null, _f.Client).AskRollback(CancellationToken.None, out _, out _));
        var journal = UpdateJournal.TryRead(_f.Folder)!;
        journal.Finished = false;
        journal.Save(_f.Folder);
        Assert.Equal(UpdateText.UnsettledSwap, Updater(running: "0.5.7").AskRollback(CancellationToken.None, out _, out _));
    }

    [Fact]
    public async Task ARollbackThatCantWrite_IsARefusal_NotACrash()
    {
        // Called on the screen's path: a read-only folder reached the crash catch (review P1-8).
        await Ready();
        Swapped();
        Directory.CreateDirectory(_f.Folder.Rollback);

        Assert.NotNull(Updater(running: "0.5.7").AskRollback(CancellationToken.None, out string? target, out _));

        Assert.Null(target);
    }

    [Fact]
    public async Task ARollbacksCheck_StopsAtItsToken()
    {
        await Ready();
        Swapped();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Updater(running: "0.5.7").AskRollback(stop.Token, out _, out _));
    }

    [Fact]
    public async Task ADeleteThatFails_IsReported_WhileReadyOrRollbackStand()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // A Unix delete doesn't care about an open file.
        }

        // "Update canceled." over a stage that still installs at quit was false (review P3-4).
        await Ready();
        using (new FileStream(_f.Folder.Ready, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(UpdateText.CouldntDelete(_f.Folder.Ready), Updater().DropStage(false));
        }

        File.WriteAllText(_f.Folder.Rollback, "");
        using var held = new FileStream(_f.Folder.Rollback, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(UpdateText.CouldntDelete(_f.Folder.Rollback), Updater().CancelRollback());
    }

    [Fact]
    public async Task Leftovers_AreAPartial_OrAStageWithNoReady()
    {
        // Reviews P4-11, P5-9.
        _f.Usual();
        Assert.False(Updater().ReadDisk(withLeftovers: true).Leftovers);
        UpdateFixture.Write(Path.Combine(_f.Folder.StagingParts, "a.part"), "x");
        Assert.True(Updater().ReadDisk(withLeftovers: true).Leftovers);
        Assert.False(Updater().ReadDisk().Leftovers);   // only when asked: the idle refresh doesn't walk them (review P8-17)
        Directory.Delete(_f.Folder.StagingParts, recursive: true);
        await _f.Stage().StageAsync(_f.Usual(), null, CancellationToken.None);
        Assert.False(Updater().ReadDisk(withLeftovers: true).Leftovers);
        File.Delete(_f.Folder.Ready);
        Assert.True(Updater().ReadDisk(withLeftovers: true).Leftovers);
    }

    [Fact]
    public async Task ReadDisk_ParsesOnlyWhenAFileChanged()
    {
        // Every idle line parsed the stage's manifest (review P8-17). A later read proves it by not noticing the manifest's change; a new stamp on ready does.
        await Ready();
        var updater = Updater();
        Assert.Equal("0.5.7", updater.ReadDisk().ReadyVersion);
        File.WriteAllText(_f.Folder.StagingManifest, "{ not a manifest");
        Assert.Equal("0.5.7", updater.ReadDisk().ReadyVersion);
        File.SetLastWriteTimeUtc(_f.Folder.Ready, DateTime.UtcNow.AddMinutes(5));
        Assert.True(updater.ReadDisk().ReadyUnknown);
    }

    [Fact]
    public async Task ReadDisk_AStageItCantReadRightNow_IsThere_WithItsVersionUnknown_AndIsTriedAgain()
    {
        // The screen flipped to Offered and /update cancel said there was nothing to cancel, over a stage that still installs (reviews P9-6, P11-9, P11-18).
        await Ready();
        var updater = Updater();
        using (FileHold.On(_f.Folder.StagingManifest))
        {
            var held = updater.ReadDisk();
            Assert.Null(held.ReadyVersion);
            Assert.True(held.ReadyUnknown);
        }

        Assert.Equal("0.5.7", updater.ReadDisk().ReadyVersion);
    }

    [Fact]
    public async Task ReadDisk_ARollbackWithItsManifestGone_IsThere_WithItsVersionUnknown()
    {
        await Ready();
        Swapped();
        File.WriteAllText(_f.Folder.Rollback, "");
        File.Delete(_f.Folder.PreviousManifest);
        var disk = new AppUpdater(_f.Context("0.5.7"), null, _f.Client).ReadDisk();
        Assert.Null(disk.RollbackTarget);
        Assert.True(disk.RollbackUnknown);
    }

    [Fact]
    public async Task DropStage_WaitsOutAStartsTidy()
    {
        // It read as another copy downloading (reviews P11-12, P12-15, P13-6).
        await Ready();
        var tidy = UpdateLock.TryExclusive(_f.Folder.StagingLock)!;
        var releasing = Task.Run(async () =>
        {
            await Task.Delay(200);
            tidy.Dispose();
        });

        Assert.Null(Updater().DropStage(alsoParts: false));
        await releasing;
        Assert.False(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task DropStage_UnderALockHeldThroughout_BlamesAnotherCopy_AndOneThatCantOpen_DoesNot()
    {
        await Ready();
        using (UpdateLock.TryExclusive(_f.Folder.StagingLock))
        {
            Assert.Equal(UpdateText.AnotherCopyStaging, Updater().DropStage(alsoParts: false));
        }

        File.Delete(_f.Folder.StagingLock);
        Directory.CreateDirectory(_f.Folder.StagingLock);
        Assert.Equal(UpdateText.StagingLockCantOpen, Updater().DropStage(alsoParts: false));
        Assert.True(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public void DropLeftovers_DeletesAPartial_ButNotUnderAReadyStage()
    {
        // What's left of a pulled release stayed until a next one came (review P18-5).
        _f.Usual();
        UpdateFixture.Write(Path.Combine(_f.Folder.StagingParts, "a.part"), "x");
        Assert.Null(Updater().DropLeftovers());
        Assert.False(Directory.Exists(_f.Folder.StagingParts));

        UpdateFixture.Write(Path.Combine(_f.Folder.StagingParts, "a.part"), "x");
        File.WriteAllText(_f.Folder.Ready, "");
        Assert.Null(Updater().DropLeftovers());
        Assert.True(Directory.Exists(_f.Folder.StagingParts));
    }

    [Fact]
    public async Task TheAcknowledgment_DeletesOnlyWhatTheStartSaidItCould()
    {
        // Review P5-3.
        await Ready();
        Swapped();
        UpdateFixture.Write(_f.Folder.SwapFailed, "a reason");

        new AppUpdater(_f.Context("0.5.7"), null, _f.Client).AcknowledgeStart();
        Assert.True(File.Exists(_f.Folder.Journal));
        Assert.True(File.Exists(_f.Folder.SwapFailed));

        new AppUpdater(_f.Context("0.5.7"), new UpdateStartResult(null, null, JournalMayGo: true, FailureShown: true), _f.Client).AcknowledgeStart();
        Assert.False(File.Exists(_f.Folder.Journal));
        Assert.False(File.Exists(_f.Folder.SwapFailed));
    }

    [Theory]
    [InlineData("a reason")]
    [InlineData("a reason.")]
    public void TheFailureLines_EndASentenceOnce(string reason)
    {
        // Review P1-19.
        Assert.Equal($"The update didn't install: a reason. {UpdateText.ReleasesUrl}", UpdateText.FailedLine(reason));
        Assert.Equal($"The update stopped: a reason. {UpdateText.ReleasesUrl}", UpdateText.StageFailedLine(reason));
        Assert.Equal($"Didn't go back: a reason. {UpdateText.ReleasesUrl}", UpdateText.WentBackFailedLine(reason));
        Assert.StartsWith("a reason, and the undo", UpdateText.UndoAlsoStopped(reason), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningNotes_AreTheInstalledVersions_OnceSwapped()
    {
        await Ready();
        Assert.Null(Updater().RunningNotes());
        Swapped();
        Assert.Equal("the notes", Updater(running: "0.5.7").RunningNotes());
    }

    [Fact]
    public void Preflight_InAFolderWithNoManifest_Refuses()
    {
        Assert.NotNull(Updater().Preflight(1000));
    }
}
