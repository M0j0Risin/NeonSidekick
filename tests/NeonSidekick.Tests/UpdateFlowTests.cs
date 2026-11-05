using NeonSidekick.Diagnostics;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Updates;

namespace NeonSidekick.Tests;

/// <summary>The recheck, the exit and the next start, over a real stage in a temp folder (2026-11-04, self-update).</summary>
public sealed class UpdateFlowTests : IDisposable
{
    private readonly UpdateFixture _f = new();

    public void Dispose() => _f.Dispose();

    private async Task Ready()
    {
        var result = await _f.Stage().StageAsync(_f.Usual(), null, CancellationToken.None);
        Assert.True(result.Ok, result.Reason);
    }

    private string Installed(string path) => File.ReadAllText(_f.Folder.Full(path));

    private string Staged(string path) => Path.Combine(_f.Folder.Staging, path);

    private UpdatePlanned? Recheck(out string? reason, string running = "0.5.6") => UpdateRecheck.Check(_f.Context(running), CancellationToken.None, out reason);

    private List<string> Exit(string running = "0.5.6", string exeName = UpdateFixture.Exe, IUpdateFiles? files = null)
    {
        var lines = new List<string>();
        new UpdateExit(_f.Context(running, exeName), null, files).Run(lines.Add);
        return lines;
    }

    // ---- the recheck

    [Fact]
    public async Task TheRecheck_PassesAGoodStage_AndListsWhatMoves()
    {
        await Ready();
        var planned = Recheck(out string? reason);
        Assert.Null(reason);
        Assert.Equal([UpdateFixture.Exe, "a.dll", "docs/new.md"], planned!.Moved);
        Assert.Empty(planned.Dropped);
    }

    [Fact]
    public async Task AStagedFileChangedAfterVerifying_IsCaught()
    {
        await Ready();
        File.WriteAllText(Staged("a.dll"), "evil");
        Assert.Null(Recheck(out string? reason));
        Assert.Equal(UpdateText.StageChanged, reason);
    }

    [Fact]
    public async Task AStagedFileChangedWithItsManifest_IsCaught()
    {
        await Ready();
        File.WriteAllText(Staged("a.dll"), "evil");
        File.WriteAllText(_f.Folder.StagingManifest, UpdateFixture.Manifest("0.5.7", new Dictionary<string, string> { [UpdateFixture.Exe] = "new exe", ["a.dll"] = "evil", ["same.dll"] = "same", ["docs/new.md"] = "new doc" }));
        Assert.Null(Recheck(out string? reason));
        Assert.Equal(UpdateText.StageChanged, reason);
    }

    [Fact]
    public async Task APlantedSha_IsCaughtByTheSignature()
    {
        await Ready();
        File.AppendAllText(_f.Folder.StagedSha, " ");
        Assert.Null(Recheck(out string? reason));
        Assert.Equal(UpdateText.NotSigned, reason);
    }

    [Fact]
    public void APlantedReady_OverAnUnsignedStage_IsCaught()
    {
        _f.Usual();
        UpdateFixture.Write(Staged("a.dll"), "evil");
        UpdateFixture.Write(_f.Folder.StagingManifest, "{}");
        UpdateFixture.Write(_f.Folder.Ready, "");

        var lines = Exit();

        Assert.Single(lines);
        Assert.StartsWith("The update didn't install", lines[0], StringComparison.Ordinal);
        Assert.Equal("old a", Installed("a.dll"));
        Assert.False(File.Exists(_f.Folder.Ready));
        Assert.False(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public async Task AStageNoLongerNewer_IsCaught()
    {
        await Ready();
        Assert.Null(Recheck(out string? reason, running: "0.5.7"));
        Assert.Equal(UpdateText.NoLongerNewer("0.5.7"), reason);
    }

    [Fact]
    public async Task AStageOverADifferentInstalledManifest_IsCaught()
    {
        await Ready();
        File.WriteAllText(_f.Folder.Manifest, UpdateFixture.Manifest("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "unpacked by hand" }));
        Assert.Null(Recheck(out string? reason));
        Assert.Equal(UpdateText.InstallChanged, reason);
    }

    [Fact]
    public async Task AnInstalledFileAlteredByHand_CountsAsChanged()
    {
        // After staging too (2026-11-04, review P4-8): its stamp changed, so the recheck hashes it again.
        await Ready();
        File.WriteAllText(_f.Folder.Full("same.dll"), "altered");

        var planned = Recheck(out _);

        Assert.Contains("same.dll", planned!.Moved);
    }

    [Fact]
    public async Task AMissingListedFile_CountsAsChanged()
    {
        _f.Usual();
        File.Delete(_f.Folder.Full("same.dll"));
        await _f.Stage().StageAsync(_f.Publish("0.5.7", new Dictionary<string, string> { [UpdateFixture.Exe] = "new exe", ["a.dll"] = "new a", ["same.dll"] = "same", ["docs/new.md"] = "new doc" }), null, CancellationToken.None);
        Assert.Contains("same.dll", Recheck(out _)!.Moved);
    }

    [Fact]
    public async Task ARecheckOutOfTime_MovesNothing_AndKeepsTheStage()
    {
        await Ready();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => UpdateRecheck.Check(_f.Context(), cancel.Token, out _));
        Assert.True(File.Exists(_f.Folder.Ready));
    }

    // ---- the exit

    [Fact]
    public async Task TheExit_Installs_AndSaysSo()
    {
        await Ready();

        var lines = Exit();

        Assert.Equal([UpdateText.Installing("0.5.7"), UpdateText.InstalledLine("0.5.7")], lines);
        Assert.Equal("new exe", Installed(UpdateFixture.Exe));
        Assert.Equal("new a", Installed("a.dll"));
        Assert.Equal("new doc", Installed("docs/new.md"));
        Assert.Equal("same", Installed("same.dll"));
        Assert.False(File.Exists(Path.Combine(_f.Folder.Previous, "same.dll")));
        Assert.Equal("the notes", File.ReadAllText(Path.Combine(_f.Folder.Release, UpdateFolder.NotesName)));
    }

    [Fact]
    public void NoStage_NoLines_AndNothingMoves()
    {
        _f.Usual();
        Assert.Empty(Exit());
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
    }

    [Fact]
    public async Task AStageWithNoReady_StaysAndDoesNotInstall()
    {
        await Ready();
        File.Delete(_f.Folder.Ready);
        Assert.Empty(Exit());
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
        Assert.True(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public async Task ARenamedCopysExit_DoesNotSwap()
    {
        await Ready();
        Assert.Equal([UpdateText.FailedLine(UpdateText.RenamedCopy(UpdateFixture.Exe))], Exit(exeName: "Copy.exe"));
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
        Assert.True(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task AnotherCopyHoldingTheLock_LeavesTheStageForLater()
    {
        await Ready();
        using var other = UpdateLock.TryShared(_f.Folder.InUseLock);

        var lines = Exit();

        Assert.Equal([UpdateText.OtherCopyOpen], lines);
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
        Assert.True(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task ThisCopysOwnShareOfTheLock_IsClosedFirst()
    {
        await Ready();
        var own = UpdateLock.TryShared(_f.Folder.InUseLock);
        var lines = new List<string>();

        new UpdateExit(_f.Context(), own).Run(lines.Add);

        Assert.Equal("new exe", Installed(UpdateFixture.Exe));
    }

    [Fact]
    public async Task TwoCallers_RunTheSwapOnce()
    {
        await Ready();
        var exit = new UpdateExit(_f.Context(), null);
        var lines = new List<string>();
        var gate = new object();
        void Say(string line)
        {
            lock (gate)
            {
                lines.Add(line);
            }
        }

        await Task.WhenAll(Task.Run(() => exit.Run(Say)), Task.Run(() => exit.Run(Say)));
        exit.Run(Say);

        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task ASwapFailure_Says_AndLeavesTheStageForNextExit()
    {
        await Ready();

        var lines = Exit(files: new AlwaysLocked());

        Assert.Equal(UpdateText.Installing("0.5.7"), lines[0]);
        Assert.StartsWith("The update didn't install", lines[1], StringComparison.Ordinal);
        Assert.Contains(UpdateText.ReleasesUrl, lines[1], StringComparison.Ordinal);
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
        Assert.True(File.Exists(_f.Folder.Ready));
        Assert.False(File.Exists(_f.Folder.Journal));
    }

    private sealed class AlwaysLocked : IUpdateFiles
    {
        private readonly UpdateFiles _real = new();

        public void Move(string from, string to)
        {
            if (from.EndsWith("a.dll", StringComparison.Ordinal))
            {
                throw new IOException("locked");
            }

            _real.Move(from, to);
        }

        public void MakeFolder(string folder) => _real.MakeFolder(folder);

        public void RemoveFolder(string folder) => _real.RemoveFolder(folder);
    }

    [Fact]
    public async Task AWindowClosesFailure_GoesInAFile_ForTheNextStart()
    {
        await Ready();
        new UpdateExit(_f.Context(), null, new AlwaysLocked()).Run(null);
        Assert.True(File.Exists(_f.Folder.SwapFailed));
    }

    [Fact]
    public async Task TheExit_KeepsItsLogLinesOffTheConsole_AndPutsTheEchoBack()
    {
        await Ready();
        bool before = DiagnosticLog.EchoToConsole;
        DiagnosticLog.EchoToConsole = true;
        bool during = true;
        try
        {
            new UpdateExit(_f.Context(), null).Run(_ => during = DiagnosticLog.EchoToConsole);
            Assert.False(during);
            Assert.True(DiagnosticLog.EchoToConsole);
        }
        finally
        {
            DiagnosticLog.EchoToConsole = before;
        }
    }

    // ---- the next start

    private UpdateStartResult Start(string running, bool interactive = true, List<string>? said = null, Action<TimeSpan>? pause = null)
    {
        var result = UpdateStartup.Run(_f.Context(running), interactive, said is null ? null : said.Add, pause ?? (_ => { }));
        result.Held?.Dispose();
        return result;
    }

    [Fact]
    public async Task AFinishedSwap_IsTold_OnceByAnInteractiveStart()
    {
        await Ready();
        Exit();

        var told = Start("0.5.7");
        Assert.Equal(UpdateText.UpdatedLine("0.5.7"), told.Line);
        Printed(told);
        Assert.False(File.Exists(_f.Folder.Journal));
        Assert.Null(Start("0.5.7").Line);
    }

    /// <summary>What the screen does once the line is on it.</summary>
    private void Printed(UpdateStartResult result) => UpdateStartup.Acknowledge(_f.Folder, result.JournalMayGo, result.FailureShown);

    [Fact]
    public async Task AHeadlessStart_LeavesAFinishedJournal_ForTheScreen()
    {
        await Ready();
        Exit();

        Assert.Null(Start("0.5.7", interactive: false).Line);
        Assert.True(File.Exists(_f.Folder.Journal));
        Assert.Equal(UpdateText.UpdatedLine("0.5.7"), Start("0.5.7").Line);
    }

    [Fact]
    public async Task AnUnfinishedJournal_UnderTheOldExe_IsUndone_AndTheStageStays()
    {
        await Ready();
        var planned = Recheck(out _)!;
        var swap = new UpdateSwap(_f.Folder, new KillAt(5));
        Assert.Throws<KilledException>(() => swap.Run(swap.PlanUpdate("0.5.6", "0.5.7", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped, planned.Verified)));
        Assert.True(File.Exists(_f.Folder.Journal));

        var start = Start("0.5.6", interactive: false);

        Assert.False(start.StartAgain);
        Assert.False(File.Exists(_f.Folder.Journal));
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
        Assert.Equal("old a", Installed("a.dll"));
        Assert.True(File.Exists(_f.Folder.Ready));
        Assert.DoesNotContain(Exit(), l => l.StartsWith("The update didn't", StringComparison.Ordinal));
        Assert.Equal("new exe", Installed(UpdateFixture.Exe));
    }

    [Fact]
    public async Task AKillWhileTheManifestIsAside_IsStillUndone()
    {
        // The swap moves the old manifest aside before the new one comes in, so in between the folder holds none.
        await Ready();
        var planned = Recheck(out _)!;
        var journal = new UpdateSwap(_f.Folder).PlanUpdate("0.5.6", "0.5.7", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped, planned.Verified);
        int manifestMove = journal.Steps.FindIndex(s => s.From == UpdateManifest.FileName);
        var swap = new UpdateSwap(_f.Folder, new KillAt(manifestMove + 2));
        Assert.Throws<KilledException>(() => swap.Run(journal));
        Assert.False(_f.Folder.IsPackage);

        var start = Start("0.5.6", interactive: false);

        Assert.False(start.StartAgain);
        Assert.False(File.Exists(_f.Folder.Journal));
        Assert.True(_f.Folder.IsPackage);
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
    }

    [Fact]
    public void AFolderWithNoManifestAndNoJournal_IsNotTouched()
    {
        _f.Usual();
        File.Delete(_f.Folder.Manifest);
        Assert.False(_f.Folder.IsPackageOrMidSwap);
        Assert.Null(UpdateStartup.Run(_f.Context("0.5.6"), true, null).Held);
    }

    [Fact]
    public async Task AnExit_OverAnUndoTheStartCouldntFinish_UndoesItFirst_AndKeepsTheStage()
    {
        await Ready();
        var planned = Recheck(out _)!;
        var swap = new UpdateSwap(_f.Folder, new KillAt(5));
        Assert.Throws<KilledException>(() => swap.Run(swap.PlanUpdate("0.5.6", "0.5.7", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped, planned.Verified)));

        var lines = Exit();

        Assert.Equal([UpdateText.FailedLine(UpdateText.SettledInstead)], lines);
        Assert.False(File.Exists(_f.Folder.Journal));
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
        Assert.Equal("old a", Installed("a.dll"));
        Assert.True(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task AnExit_OverAnUndoThatStillCantFinish_MovesNothingMore()
    {
        await Ready();
        var planned = Recheck(out _)!;
        var swap = new UpdateSwap(_f.Folder, new KillAt(5));
        var journal = swap.PlanUpdate("0.5.6", "0.5.7", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped, planned.Verified);
        Assert.Throws<KilledException>(() => swap.Run(journal));
        string before = File.ReadAllText(_f.Folder.Journal);

        var lines = Exit(files: new KillAllMoves());

        Assert.Equal([UpdateText.FailedLine(UpdateText.MixedReason)], lines);
        Assert.Equal(before, File.ReadAllText(_f.Folder.Journal));
    }

    private sealed class KillAllMoves : IUpdateFiles
    {
        private readonly UpdateFiles _real = new();

        public void Move(string from, string to) => throw new IOException("locked");

        public void MakeFolder(string folder) => _real.MakeFolder(folder);

        public void RemoveFolder(string folder) => _real.RemoveFolder(folder);
    }

    [Fact]
    public async Task AnExit_UnderTheNewExe_OverAnUnfinishedCleanup_FinishesIt()
    {
        await Ready();
        Exit();
        var journal = UpdateJournal.TryRead(_f.Folder)!;
        journal.Finished = false;
        journal.Save(_f.Folder);
        File.WriteAllText(_f.Folder.Ready, "");

        Assert.Equal([UpdateText.FailedLine(UpdateText.SettledInstead)], Exit("0.5.7"));

        Assert.False(File.Exists(_f.Folder.Ready));
        Assert.True(UpdateJournal.TryRead(_f.Folder)!.Finished);
    }

    private sealed class KilledException : Exception
    {
        public KilledException()
            : base("killed")
        {
        }
    }

    private sealed class KillAt(int at) : IUpdateFiles
    {
        private readonly UpdateFiles _real = new();
        private int _count;

        private void Check()
        {
            if (++_count >= at)
            {
                throw new KilledException();
            }
        }

        public void Move(string from, string to)
        {
            Check();
            _real.Move(from, to);
        }

        public void MakeFolder(string folder)
        {
            Check();
            _real.MakeFolder(folder);
        }

        public void RemoveFolder(string folder)
        {
            Check();
            _real.RemoveFolder(folder);
        }
    }

    [Fact]
    public async Task AnUnfinishedJournal_UnderTheNewExe_FinishesTheCleanup()
    {
        await Ready();
        Exit();
        var journal = UpdateJournal.TryRead(_f.Folder)!;
        journal.Finished = false;
        journal.Save(_f.Folder);
        Directory.CreateDirectory(_f.Folder.PreviousOld);
        Directory.CreateDirectory(_f.Folder.Staging);
        File.WriteAllText(_f.Folder.Ready, "");

        Assert.Equal(UpdateText.UpdatedLine("0.5.7"), Start("0.5.7").Line);

        Assert.False(Directory.Exists(_f.Folder.PreviousOld));
        Assert.False(Directory.Exists(_f.Folder.Staging));
        Assert.False(File.Exists(_f.Folder.Ready));
        Assert.Equal("new exe", Installed(UpdateFixture.Exe));
    }

    [Fact]
    public async Task AJournalOfNeitherVersion_MovesNothing()
    {
        await Ready();
        var planned = Recheck(out _)!;
        var swap = new UpdateSwap(_f.Folder);
        var journal = swap.PlanUpdate("0.5.5", "0.5.8", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped, planned.Verified);
        journal.Save(_f.Folder);

        Start("0.5.6");

        Assert.False(File.Exists(_f.Folder.Journal));
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
    }

    [Fact]
    public async Task TwoStartsTogether_UndoOnce_TheOtherLeavesTheJournal()
    {
        await Ready();
        var planned = Recheck(out _)!;
        var swap = new UpdateSwap(_f.Folder, new KillAt(5));
        Assert.Throws<KilledException>(() => swap.Run(swap.PlanUpdate("0.5.6", "0.5.7", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped, planned.Verified)));

        using var other = UpdateLock.TryShared(_f.Folder.InUseLock);
        var start = UpdateStartup.Run(_f.Context("0.5.6"), false, null, _ => { });
        start.Held?.Dispose();

        Assert.True(File.Exists(_f.Folder.Journal));
    }

    [Fact]
    public async Task ACopyThatWaitsOutASwap_UnderTheOldExe_ExitsToStartAgain()
    {
        await Ready();
        var said = new List<string>();
        var holder = UpdateLock.TryExclusive(_f.Folder.InUseLock);

        var result = Start("0.5.6", said: said, pause: _ =>
        {
            if (holder is not null)
            {
                holder.Dispose();
                holder = null;
                Exit();
            }
        });

        Assert.True(result.StartAgain);
        Assert.Equal([UpdateText.WaitingLine, UpdateText.StartAgainLine("0.5.7")], said);
    }

    [Fact]
    public async Task ACopyThatWaitsOutAnUndoneSwap_StartsNormally()
    {
        await Ready();
        var said = new List<string>();
        var holder = UpdateLock.TryExclusive(_f.Folder.InUseLock);

        var result = Start("0.5.6", said: said, pause: _ =>
        {
            holder?.Dispose();
            holder = null;
        });

        Assert.False(result.StartAgain);
        Assert.Equal([UpdateText.WaitingLine], said);
    }

    [Fact]
    public async Task AWaitThatRunsOut_StartsAnyway_WithNoLock()
    {
        await Ready();
        using var holder = UpdateLock.TryExclusive(_f.Folder.InUseLock);
        var pauses = new List<TimeSpan>();

        var result = UpdateStartup.Run(_f.Context("0.5.6"), true, null, pauses.Add);

        Assert.False(result.StartAgain);
        Assert.Null(result.Held);
        Assert.Equal(UpdateStartup.WaitLimit, TimeSpan.FromMilliseconds(pauses.Sum(p => p.TotalMilliseconds)));
    }

    [Fact]
    public async Task ANormalStart_ShowsNoWaitingLine()
    {
        await Ready();
        var said = new List<string>();
        Start("0.5.6", said: said);
        Assert.Empty(said);
    }

    [Fact]
    public void AFolderWithNoManifest_DoesNothing()
    {
        var result = UpdateStartup.Run(_f.Context("0.5.6"), true, null);
        Assert.Null(result.Held);
        Assert.False(Directory.Exists(_f.Folder.Root));
    }

    [Fact]
    public async Task AStageNoNewerThanTheRunningVersion_IsDropped()
    {
        await Ready();
        Start("0.5.7", interactive: false);
        Assert.False(Directory.Exists(_f.Folder.Staging));
        Assert.False(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task AStageOverADifferentInstalledManifest_IsDroppedAtStart()
    {
        await Ready();
        File.WriteAllText(_f.Folder.Manifest, UpdateFixture.Manifest("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "unpacked" }));
        Start("0.5.6");
        Assert.False(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public async Task AGoodStage_SurvivesAStart()
    {
        await Ready();
        Start("0.5.6");
        Assert.True(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public void AKilledRunsExtractingFolder_IsRemovedAtStart_ButNotUnderAnotherCopysLock()
    {
        _f.Usual();
        string leftover = Path.Combine(_f.Folder.Root, "staging.abc.extracting");
        UpdateFixture.Write(Path.Combine(leftover, "x"), "x");

        using (var other = UpdateLock.TryExclusive(_f.Folder.StagingLock))
        {
            Start("0.5.6");
            Assert.True(Directory.Exists(leftover));
        }

        Start("0.5.6");
        Assert.False(Directory.Exists(leftover));
    }

    [Fact]
    public void AWindowClosesFailure_IsShownOnce_Scrubbed_ThenGone()
    {
        _f.Usual();
        UpdateFixture.Write(_f.Folder.SwapFailed, "a reason\u001b[31m");

        var first = Start("0.5.6");

        Assert.Contains("a reason", first.Line, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', first.Line!);
        Assert.True(File.Exists(_f.Folder.SwapFailed));
        Printed(first);
        Assert.False(File.Exists(_f.Folder.SwapFailed));
        Assert.Null(Start("0.5.6").Line);
    }

    [Fact]
    public void AHeadlessStart_LeavesAWindowClosesFailure()
    {
        _f.Usual();
        UpdateFixture.Write(_f.Folder.SwapFailed, "a reason");
        Assert.Null(Start("0.5.6", interactive: false).Line);
        Assert.True(File.Exists(_f.Folder.SwapFailed));
    }

    [Fact]
    public void APlantedJournal_IsRejected_AndTheAppStillStarts()
    {
        _f.Usual();
        UpdateFixture.Write(_f.Folder.Journal, """{"format":1,"source":"0.5.6","target":"0.5.7","steps":[{"kind":"Move","from":"../x","to":"y"}]}""");
        Assert.False(Start("0.5.6").StartAgain);
        Assert.False(File.Exists(_f.Folder.Journal));
    }

    [Fact]
    public void TheInUseLock_IsHeldShared_ForTheRun()
    {
        _f.Usual();
        var start = UpdateStartup.Run(_f.Context("0.5.6"), true, null);
        using (start.Held)
        {
            Assert.NotNull(start.Held);
            using var another = UpdateLock.TryShared(_f.Folder.InUseLock);
            using var exclusive = UpdateLock.TryExclusive(_f.Folder.InUseLock);
            Assert.NotNull(another);
            Assert.Null(exclusive);
        }
    }

    // ---- going back

    private async Task Updated()
    {
        await Ready();
        Exit();
        Printed(Start("0.5.7"));
    }

    private void AskRollback() => File.WriteAllText(_f.Folder.Rollback, "");

    [Fact]
    public async Task ARollback_PutsTheOldVersionBack_AndSaysSoNextLaunch()
    {
        await Updated();
        AskRollback();

        var lines = Exit("0.5.7");

        Assert.Equal([UpdateText.GoingBackNow("0.5.6"), UpdateText.WentBackLine("0.5.6")], lines);
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
        Assert.Equal("old a", Installed("a.dll"));
        Assert.False(File.Exists(_f.Folder.Full("docs/new.md")));
        Assert.False(File.Exists(_f.Folder.Rollback));
        Assert.Equal(UpdateText.BackOnLine("0.5.6"), Start("0.5.6").Line);
    }

    [Fact]
    public async Task AFinishedRollback_LeavesNothingToGoBackTo()
    {
        await Updated();
        AskRollback();
        Exit("0.5.7");
        Start("0.5.6");

        Assert.Null(UpdateRollback.Check(_f.Context("0.5.6"), CancellationToken.None, out string? reason));
        Assert.Equal(UpdateText.NothingToGoBackTo, reason);
    }

    [Fact]
    public void NoPrevious_IsNothingToGoBackTo()
    {
        _f.Usual();
        Assert.Null(UpdateRollback.Check(_f.Context("0.5.6"), CancellationToken.None, out string? reason));
        Assert.Equal(UpdateText.NothingToGoBackTo, reason);
    }

    [Fact]
    public async Task AnAlteredFileInPrevious_RefusesTheRollback_AndKeepsPrevious()
    {
        await Updated();
        File.WriteAllText(Path.Combine(_f.Folder.Previous, "a.dll"), "altered");

        Assert.Null(UpdateRollback.Check(_f.Context("0.5.7"), CancellationToken.None, out string? reason));

        Assert.Equal(UpdateText.OldVersionAltered("a.dll"), reason);
        Assert.True(Directory.Exists(_f.Folder.Previous));
    }

    [Fact]
    public async Task APreviousMissingANeededFile_RefusesTheRollback()
    {
        await Updated();
        File.Delete(Path.Combine(_f.Folder.Previous, "a.dll"));
        Assert.Null(UpdateRollback.Check(_f.Context("0.5.7"), CancellationToken.None, out string? reason));
        Assert.Equal(UpdateText.OldVersionIncomplete("a.dll"), reason);
    }

    [Fact]
    public async Task AVersionSignedOnlyByADroppedKey_IsRefused()
    {
        await Updated();
        var releaseOf056 = _f.Folder.PreviousRelease;
        using var dropped = UpdateSignature.NewKey();
        string manifestHash = UpdateHash.OfFile(_f.Folder.PreviousManifest);
        string sha = UpdateShaFile.Text(new string('a', 64), UpdateCheck.ArchiveName(UpdateFixture.V("0.5.6"), UpdateFixture.Rid), manifestHash);
        UpdateFixture.Write(Path.Combine(releaseOf056, UpdateFolder.ShaName), sha);
        UpdateFixture.Write(Path.Combine(releaseOf056, UpdateFolder.SigName), UpdateSignature.SignatureLine(System.Text.Encoding.ASCII.GetBytes(sha), dropped) + "\n");

        Assert.Null(UpdateRollback.Check(_f.Context("0.5.7"), CancellationToken.None, out string? reason));

        Assert.Equal(UpdateText.OldVersionNotSigned("0.5.6"), reason);
    }

    [Fact]
    public async Task ASignedOldVersion_Passes()
    {
        await Updated();
        string manifestHash = UpdateHash.OfFile(_f.Folder.PreviousManifest);
        string sha = UpdateShaFile.Text(new string('a', 64), UpdateCheck.ArchiveName(UpdateFixture.V("0.5.6"), UpdateFixture.Rid), manifestHash);
        UpdateFixture.Write(Path.Combine(_f.Folder.PreviousRelease, UpdateFolder.ShaName), sha);
        UpdateFixture.Write(Path.Combine(_f.Folder.PreviousRelease, UpdateFolder.SigName), UpdateSignature.SignatureLine(System.Text.Encoding.ASCII.GetBytes(sha), _f.Key) + "\n");

        Assert.NotNull(UpdateRollback.Check(_f.Context("0.5.7"), CancellationToken.None, out _));
    }

    [Fact]
    public async Task AVersionInstalledByHand_ChecksItsManifestOnly()
    {
        await Updated();
        Assert.False(Directory.Exists(_f.Folder.PreviousRelease));
        var plan = UpdateRollback.Check(_f.Context("0.5.7"), CancellationToken.None, out string? reason);
        Assert.Null(reason);
        Assert.Equal(["docs/new.md"], plan!.Added);
    }

    [Fact]
    public async Task ARollbackFailingAtExit_IsDropped_WithItsReason_AndPreviousKept()
    {
        await Updated();
        AskRollback();
        File.WriteAllText(Path.Combine(_f.Folder.Previous, "a.dll"), "altered");

        var lines = Exit("0.5.7");

        Assert.Single(lines);
        Assert.Contains("altered", lines[0], StringComparison.Ordinal);
        Assert.False(File.Exists(_f.Folder.Rollback));
        Assert.True(Directory.Exists(_f.Folder.Previous));
        Assert.Equal("new exe", Installed(UpdateFixture.Exe));
        Assert.Empty(Exit("0.5.7"));
    }

    [Fact]
    public async Task ARollback_LeavesASharedFileAlteredByHand_AsItIs()
    {
        await Updated();
        File.WriteAllText(_f.Folder.Full("same.dll"), "altered by hand");
        AskRollback();
        Exit("0.5.7");
        Assert.Equal("altered by hand", Installed("same.dll"));
    }

    [Fact]
    public async Task APendingRollback_SurvivesARestart()
    {
        await Updated();
        AskRollback();
        Start("0.5.7");
        Assert.True(File.Exists(_f.Folder.Rollback));
        Assert.Equal(UpdateText.GoingBackNow("0.5.6"), Exit("0.5.7")[0]);
    }

    [Fact]
    public async Task TheProofs_ReadAsNone_WhenTheyNameAnotherVersion()
    {
        await Updated();
        var installed = UpdateManifest.ReadFile(_f.Folder.Manifest, out _)!;
        Assert.NotNull(UpdateProofs.Read(_f.Folder.Release, installed));
        Assert.Equal("the notes", UpdateProofs.Notes(_f.Folder, installed));

        // A package unpacked by hand over the folder: its manifest names another version than the kept proofs.
        File.WriteAllText(_f.Folder.Manifest, UpdateFixture.Manifest("0.5.8", new Dictionary<string, string> { [UpdateFixture.Exe] = "hand" }));
        var unpacked = UpdateManifest.ReadFile(_f.Folder.Manifest, out _)!;
        Assert.Null(UpdateProofs.Read(_f.Folder.Release, unpacked));
        Assert.Null(UpdateProofs.Notes(_f.Folder, unpacked));
    }

    // ---- the start, as the review left it (2026-11-04)

    private async Task KilledMidSwap()
    {
        await Ready();
        var planned = Recheck(out _)!;
        var swap = new UpdateSwap(_f.Folder, new KillAt(5));
        Assert.Throws<KilledException>(() => swap.Run(swap.PlanUpdate("0.5.6", "0.5.7", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped, planned.Verified)));
    }

    [Fact]
    public async Task AnOldCopyStartedJustAfterASwap_IsToldToStartAgain_WithNoWait()
    {
        // Nothing held the lock, so it never waited, and the old exe still sat over new files (review P3-1).
        await Ready();
        Exit();
        var pauses = new List<TimeSpan>();

        var start = Start("0.5.6", interactive: false, pause: pauses.Add);

        Assert.True(start.StartAgain);
        Assert.Equal(UpdateText.StartAgainLine("0.5.7"), start.StartAgainLine);
        Assert.Empty(pauses);
        Assert.True(File.Exists(_f.Folder.Journal));
    }

    [Fact]
    public async Task AnInteractiveStartAgain_SaysIt_AndStaysLongEnoughToRead()
    {
        // A double-clicked window closed with the line unread (review P3-11).
        await Ready();
        Exit();
        var said = new List<string>();
        var pauses = new List<TimeSpan>();

        Start("0.5.6", said: said, pause: pauses.Add);

        Assert.Equal([UpdateText.StartAgainLine("0.5.7")], said);
        Assert.Equal([UpdateStartup.StartAgainPause], pauses);
    }

    [Fact]
    public async Task AStaleJournalAfterAHandUnpack_DoesNotLockTheAppOut()
    {
        await Ready();
        Exit();
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old exe", ["a.dll"] = "old a", ["same.dll"] = "same" });

        var start = Start("0.5.6");

        Assert.False(start.StartAgain);
        Assert.Null(start.Line);
        Printed(start);
        Assert.False(File.Exists(_f.Folder.Journal));
    }

    [Fact]
    public async Task ARenamedOldCopy_LeavesTheJournalForTheListedExe()
    {
        await Ready();
        Exit();

        var start = UpdateStartup.Run(_f.Context("0.5.6", "Copy.exe"), true, null, _ => { });
        start.Held?.Dispose();

        Assert.False(start.StartAgain);
        Assert.False(start.JournalMayGo);
        Printed(start);
        Assert.Equal(UpdateText.UpdatedLine("0.5.7"), Start("0.5.7").Line);
    }

    [Fact]
    public async Task ALineNeverPrinted_IsToldAgain()
    {
        // --profile typo or a crash before the idle line lost it, when the step deleted as it built (review P5-3).
        await Ready();
        Exit();
        Assert.Equal(UpdateText.UpdatedLine("0.5.7"), Start("0.5.7").Line);
        Assert.Equal(UpdateText.UpdatedLine("0.5.7"), Start("0.5.7").Line);
    }

    [Fact]
    public async Task AStepThatFails_KeepsItsOneLock_AndTheAppStarts()
    {
        // A second hold from the catch left the first open for the run's life, and this run's own exit then read it as another copy (review P1-7).
        await Ready();
        Exit();

        var start = UpdateStartup.Run(_f.Context("0.5.6"), true, _ => throw new InvalidOperationException("console gone"), _ => { });

        Assert.NotNull(start.Held);
        Assert.False(start.StartAgain);
        start.Held.Dispose();
        using var only = UpdateLock.TryExclusive(_f.Folder.InUseLock);
        Assert.NotNull(only);
    }

    [Fact]
    public async Task AnotherCopysBriefHold_IsWaitedOut_AndTheSwapSettled()
    {
        // Two copies started together each dropped their shared hold in turn, so a single try left the undo to the other (review P3-2).
        await KilledMidSwap();
        var other = UpdateLock.TryShared(_f.Folder.InUseLock);

        Start("0.5.6", pause: _ => other?.Dispose());

        Assert.False(File.Exists(_f.Folder.Journal));
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
    }

    [Fact]
    public async Task ASwapThisCopyCouldntSettle_SaysTheInstallMayBeMixed_AndTidiesNothing()
    {
        // Reviews P4-2, P3-3: the undo may still need what a tidy would take.
        await KilledMidSwap();
        Directory.CreateDirectory(_f.Folder.PreviousOld);
        using var other = UpdateLock.TryShared(_f.Folder.InUseLock);

        var start = Start("0.5.6");

        Assert.Contains(UpdateText.MixedLine, start.Line, StringComparison.Ordinal);
        Assert.True(File.Exists(_f.Folder.Journal));
        Assert.True(Directory.Exists(_f.Folder.Staging));
        Assert.True(Directory.Exists(_f.Folder.PreviousOld));
    }

    [Fact]
    public void ALockThatCantBeOpened_ReadsAsNotHeld_AndNeverWaits()
    {
        // Read-only media or an ACL isn't an update in progress (review P5-2).
        _f.Usual();
        Directory.CreateDirectory(_f.Folder.InUseLock);
        var pauses = new List<TimeSpan>();

        Assert.Null(UpdateLock.TryShared(_f.Folder.InUseLock, out bool held));
        Assert.False(held);
        Assert.Null(UpdateLock.TryExclusive(_f.Folder.InUseLock, out bool heldToo));
        Assert.False(heldToo);
        Assert.Null(Start("0.5.6", pause: pauses.Add).Held);
        Assert.Empty(pauses);
    }

    [Fact]
    public void ALockAnotherHolds_ReadsAsHeld()
    {
        string path = Path.Combine(_f.Root, ".update", "in-use.lock");
        using var only = UpdateLock.TryExclusive(path);
        Assert.Null(UpdateLock.TryShared(path, out bool held));
        Assert.True(held);
        Assert.Null(UpdateLock.TryExclusive(path, out bool heldToo));
        Assert.True(heldToo);
    }

    [Fact]
    public void AStageWithNoReadableManifest_IsCleared()
    {
        // What a cleanup couldn't delete: the swap took its manifest, so nothing else would ever retry it (review P2-10).
        _f.Usual();
        UpdateFixture.Write(Path.Combine(_f.Folder.Staging, "left.dll"), "x");
        Start("0.5.6");
        Assert.False(Directory.Exists(_f.Folder.Staging));
    }

    [Fact]
    public void AnOrphanReady_IsCleared()
    {
        // It failed the next exit with "the update changed", though none was pending (review P5-8).
        _f.Usual();
        UpdateFixture.Write(_f.Folder.Ready, "");
        Start("0.5.6");
        Assert.False(File.Exists(_f.Folder.Ready));
        Assert.Empty(Exit());
    }

    [Fact]
    public void PartsOfAVersionNoNewer_Go_AndANewerOnesStay()
    {
        // Review P6-8: 0.5.8 half down, then 0.5.9 unpacked by hand, left hundreds of MB for good.
        _f.Usual();
        string older = Path.Combine(_f.Folder.StagingParts, UpdateCheck.ArchiveName(UpdateFixture.V("0.5.6"), UpdateFixture.Rid));
        string newer = Path.Combine(_f.Folder.StagingParts, UpdateCheck.ArchiveName(UpdateFixture.V("0.5.8"), UpdateFixture.Rid));
        string junk = Path.Combine(_f.Folder.StagingParts, "not-ours.bin");
        UpdateFixture.Write(older, "x");
        UpdateFixture.Write(newer, "x");
        UpdateFixture.Write(junk, "x");

        Start("0.5.6");

        Assert.False(File.Exists(older));
        Assert.False(File.Exists(junk));
        Assert.True(File.Exists(newer));
    }

    [Fact]
    public void ANothingToTidyStart_TakesNoStagingLock()
    {
        // Every start in every mode took it, and one starting just as the user pressed Install read as another copy downloading (review P7-8).
        _f.Usual();
        Start("0.5.6");
        Assert.False(File.Exists(_f.Folder.StagingLock));
    }

    [Fact]
    public async Task AJournalHeldOpen_StaysAndCountsAsUnfinished_Everywhere()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // A Unix read doesn't care about an open file.
        }

        // A failure to read deleted the only record of an interrupted swap (review P1-2).
        await Ready();
        new UpdateSwap(_f.Folder).PlanUpdate("0.5.6", "0.5.7", UpdateFixture.Exe, UpdateFixture.Exe, [], [], []).Save(_f.Folder);
        using var held = new FileStream(_f.Folder.Journal, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Null(UpdateJournal.TryRead(_f.Folder, out bool unreadable));
        Assert.True(unreadable);
        Assert.True(File.Exists(_f.Folder.Journal));
        Assert.True(UpdateJournal.IsUnsettled(_f.Folder));
        Assert.Equal(UpdateText.UnsettledSwap, _f.Stage().Preflight(1));
        Assert.Contains(UpdateText.MixedLine, Start("0.5.6").Line, StringComparison.Ordinal);
        Assert.Equal([UpdateText.FailedLine(UpdateText.UnfinishedSwap)], Exit());
        Assert.True(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task TheJournalsTarget_IsTheParsedVersion()
    {
        // The start compares it with the running version, so the manifest's own spelling read as "neither" (review P4-16).
        var files = new Dictionary<string, string> { [UpdateFixture.Exe] = "new exe" };
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old exe" });
        var result = await _f.Stage().StageAsync(_f.Publish("0.5.7", files, manifestText: UpdateFixture.Manifest("0.5.07", files)), null, CancellationToken.None);
        Assert.True(result.Ok, result.Reason);

        Exit();

        Assert.Equal("0.5.7", UpdateJournal.TryRead(_f.Folder)!.Target);
    }

    [Fact]
    public void TheInstallHashes_RefuseNullFiles_AndBadJson()
    {
        _f.Usual();
        UpdateFixture.Write(_f.Folder.StagedInstallHashes, """{"manifest":"x","files":null}""");
        Assert.Null(UpdateInstallHashes.TryRead(_f.Folder));
        UpdateFixture.Write(_f.Folder.StagedInstallHashes, "not json");
        Assert.Null(UpdateInstallHashes.TryRead(_f.Folder));
    }

    [Fact]
    public void TheHashing_StopsAtItsToken()
    {
        // A window close's recheck that ran out moves nothing, and a big exe's hash has to notice (reviews P1-3, P2-12).
        _f.Usual();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var installed = UpdateManifest.ReadFile(_f.Folder.Manifest, out _)!;
        Assert.ThrowsAny<OperationCanceledException>(() => UpdateHash.OfFile(_f.Folder.Manifest, stop.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => UpdateInstallHashes.Take(_f.Folder.Install, installed, "x", stop.Token));
    }

    [Fact]
    public void ARunStartedByItsShortName_IsTheListedExe_AndARenameIsnt()
    {
        _f.Usual();
        var installed = UpdateManifest.ReadFile(_f.Folder.Manifest, out _)!;
        Assert.True(_f.Context().IsListedExe(installed));
        Assert.False(_f.Context("0.5.6", "Copy.exe").IsListedExe(installed));
        Assert.False(_f.Context("0.5.6", "ABSENT~1.EXE").IsListedExe(installed));

        // Short names can be off on a volume, and then there's nothing to hold (review P6-16).
        string shortName = "NEONSI~1.EXE";
        if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(_f.Root, shortName)))
        {
            Assert.True(_f.Context("0.5.6", shortName).IsListedExe(installed));
        }
    }

    // ---- the exit, as the review left it (2026-11-04)

    private sealed class ThrowsOnMove(Exception thrown) : IUpdateFiles
    {
        private readonly UpdateFiles _real = new();

        public void Move(string from, string to) => throw thrown;

        public void MakeFolder(string folder) => _real.MakeFolder(folder);

        public void RemoveFolder(string folder) => _real.RemoveFolder(folder);
    }

    private sealed class LockedAfter(int moves) : IUpdateFiles
    {
        private readonly UpdateFiles _real = new();
        private int _left = moves;

        public void Move(string from, string to)
        {
            if (_left-- <= 0)
            {
                throw new IOException("locked");
            }

            _real.Move(from, to);
        }

        public void MakeFolder(string folder) => _real.MakeFolder(folder);

        public void RemoveFolder(string folder) => _real.RemoveFolder(folder);
    }

    [Fact]
    public async Task AJournalOfNeitherVersion_AtExit_GoesAndInstallsNothing()
    {
        // It stayed for good at the exit, and the two readers had drifted (reviews P6-9, P3-12).
        await Ready();
        new UpdateSwap(_f.Folder).PlanUpdate("0.5.5", "0.5.8", UpdateFixture.Exe, UpdateFixture.Exe, [], [], []).Save(_f.Folder);

        Assert.Equal([UpdateText.FailedLine(UpdateText.SettledInstead)], Exit());

        Assert.False(File.Exists(_f.Folder.Journal));
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
        Assert.True(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task ADeadConsole_DoesntStopTheSwap()
    {
        // A Mac terminal closed at quit (review P2-8).
        await Ready();
        new UpdateExit(_f.Context(), null, null).Run(_ => throw new IOException("gone"));
        Assert.Equal("new exe", Installed(UpdateFixture.Exe));
    }

    [Fact]
    public async Task AnUnexpectedFailure_EndsInALine_NotACrash()
    {
        await Ready();
        var lines = new List<string>();

        new UpdateExit(_f.Context(), null, new ThrowsOnMove(new InvalidOperationException("odd"))).Run(lines.Add);

        Assert.Contains(lines, line => line.Contains("odd", StringComparison.Ordinal));
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
    }

    [Fact]
    public async Task ADamagedManifest_SaysSoAtQuit_AndTheStageStays()
    {
        await Ready();
        File.WriteAllText(_f.Folder.Manifest, "not json");

        var line = Assert.Single(Exit());

        Assert.Equal(UpdateText.FailedLine(UpdateText.DamagedInstall(UpdateText.ManifestUnreadable)), line);
        Assert.True(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task AnUndoThatStopsToo_SaysTheInstallMayBeMixed()
    {
        // It isn't the same news as a clean undo (review P5-6).
        await Ready();

        var lines = Exit(files: new LockedAfter(3));

        Assert.Equal(UpdateText.Installing("0.5.7"), lines[0]);
        Assert.Equal(UpdateText.FailedLine(UpdateText.UndoAlsoStopped("locked")), lines[^1]);
        Assert.True(File.Exists(_f.Folder.Journal));
    }

    [Fact]
    public async Task InstallingIsSaid_BeforeTheRecheck()
    {
        // Seconds of silence on a slow disk read as a hang (review P4-18).
        await Ready();
        File.WriteAllText(Staged("a.dll"), "evil");
        Assert.Equal([UpdateText.Installing("0.5.7"), UpdateText.FailedLine(UpdateText.StageChanged)], Exit());
    }

    [Theory]
    [InlineData(3, 1, 1)]
    [InlineData(3, 2, 0)]
    [InlineData(3, 5, 0)]
    public void TheCloseBudget_IsWhatsLeft_LessTheMoves(int deadline, int used, int left) =>
        Assert.Equal(TimeSpan.FromSeconds(left), UpdateExit.CloseBudget(TimeSpan.FromSeconds(deadline), TimeSpan.FromSeconds(used)));

    [Fact]
    public async Task ACloseWithNoTimeLeft_MovesNothing_AndKeepsTheStage()
    {
        await Ready();

        new UpdateExit(_f.Context(), null, null).Run(null, TimeSpan.Zero);

        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
        Assert.True(File.Exists(_f.Folder.Ready));
        Assert.False(File.Exists(_f.Folder.Journal));
    }

    // ---- kept files (2026-11-04, reviews P4-1, P2-7, P4-8)

    private string KeptPath(string version, string path) => Path.Combine(_f.Folder.Kept, version, path.Replace('/', Path.DirectorySeparatorChar));

    private string KeptLine(string version) => UpdateText.KeptFilesLine(Path.Combine(_f.Folder.Kept, version));

    [Fact]
    public async Task AFileAlteredAfterStaging_IsRepaired_ItsEditKept_AndTheNextStartSays()
    {
        await Ready();
        File.WriteAllText(_f.Folder.Full("same.dll"), "altered");

        Exit();

        Assert.Equal("same", Installed("same.dll"));
        Assert.Equal("altered", File.ReadAllText(KeptPath("0.5.6", "same.dll")));
        Assert.False(File.Exists(Path.Combine(_f.Folder.Previous, "same.dll")));
        var start = Start("0.5.7");
        Assert.Contains(UpdateText.UpdatedLine("0.5.7"), start.Line, StringComparison.Ordinal);
        Assert.Contains(KeptLine("0.5.6"), start.Line, StringComparison.Ordinal);
        Printed(start);
        Assert.Null(Start("0.5.7").Line);
    }

    [Fact]
    public async Task AKeptFolderInTheWay_GetsANewOne_SoNothingIsOverwritten()
    {
        await Ready();
        UpdateFixture.Write(KeptPath("0.5.6", "same.dll"), "an earlier edit");
        File.WriteAllText(_f.Folder.Full("same.dll"), "altered");

        Exit();

        Assert.Equal("an earlier edit", File.ReadAllText(KeptPath("0.5.6", "same.dll")));
        Assert.Equal("altered", File.ReadAllText(KeptPath("0.5.6-2", "same.dll")));
    }

    [Fact]
    public async Task AFileTouchedButUnchanged_StaysVerified()
    {
        await Ready();
        File.SetLastWriteTimeUtc(_f.Folder.Full("same.dll"), DateTime.UtcNow.AddHours(1));

        var planned = Recheck(out _)!;

        Assert.DoesNotContain("same.dll", planned.Moved);
        Assert.Contains("same.dll", planned.Verified);
    }

    [Fact]
    public async Task AFileHeldAtStaging_IsHashedAtTheRecheck()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // A Unix read doesn't care about an open file.
        }

        var release = _f.Usual();
        using (new FileStream(_f.Folder.Full("same.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True((await _f.Stage().StageAsync(release, null, CancellationToken.None)).Ok);
        }

        var planned = Recheck(out _)!;

        Assert.DoesNotContain("same.dll", planned.Moved);
        Assert.Contains("same.dll", planned.Verified);
    }

    // ---- the recheck, as the review left it (2026-11-04)

    [Fact]
    public async Task AValidlySignedStageForAnotherPlatform_IsDropped()
    {
        // It passes every signature check, swapped into the stage after its probe (review P1-10).
        await Ready();
        var files = new Dictionary<string, string> { [UpdateFixture.Exe] = "new exe", ["a.dll"] = "new a", ["same.dll"] = "same", ["docs/new.md"] = "new doc" };
        string manifest = UpdateFixture.Manifest("0.5.7", files, rid: "osx-arm64");
        byte[] sha = System.Text.Encoding.ASCII.GetBytes(UpdateShaFile.Text(new string('a', 64), UpdateCheck.ArchiveName(UpdateFixture.V("0.5.7"), "osx-arm64"), UpdateFixture.HashOf(manifest)));
        File.WriteAllText(_f.Folder.StagingManifest, manifest);
        File.WriteAllBytes(_f.Folder.StagedSha, sha);
        File.WriteAllText(_f.Folder.StagedSig, UpdateSignature.SignatureLine(sha, _f.Key) + "\n");

        Assert.Null(UpdateRecheck.Check(_f.Context(), CancellationToken.None, out string? reason, out var failure));

        Assert.Equal(UpdateText.StageChanged, reason);
        Assert.Equal(UpdateRecheckFailure.Drop, failure);
    }

    [Theory]
    [InlineData("a.dll")]
    [InlineData("update-manifest.json")]
    public async Task AStagedFileHeldForAMoment_KeepsTheStage_ForTheNextExit(string held)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // A Unix read doesn't care about an open file.
        }

        // A scanner's brief sharing violation cost a whole download (review P2-4).
        await Ready();
        List<string> lines;
        using (new FileStream(Staged(held), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            lines = Exit();
            Assert.Null(UpdateRecheck.Check(_f.Context(), CancellationToken.None, out _, out var failure));
            Assert.Equal(UpdateRecheckFailure.Keep, failure);
        }

        Assert.StartsWith("The update didn't install", lines[^1], StringComparison.Ordinal);
        Assert.True(File.Exists(_f.Folder.Ready));
        Exit();
        Assert.Equal("new exe", Installed(UpdateFixture.Exe));
    }

    [Fact]
    public async Task AnInstalledManifestHeldForAMoment_KeepsTheStage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // A Unix read doesn't care about an open file.
        }

        await Ready();
        using var held = new FileStream(_f.Folder.Manifest, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Null(UpdateRecheck.Check(_f.Context(), CancellationToken.None, out _, out var failure));

        Assert.Equal(UpdateRecheckFailure.Keep, failure);
    }

    // ---- going back, as the review left it (2026-11-04)

    private UpdateRollbackPlan? BackCheck(out string? reason, string running = "0.5.7") => UpdateRollback.Check(_f.Context(running), CancellationToken.None, out reason);

    private string InPrevious(string path) => Path.Combine(_f.Folder.Previous, path.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task ARollbackToAVersionNotOlder_IsRefused()
    {
        // A package of the kept version unpacked by hand would journal A to A, and a kill mid-swap would read as finished (review P1-5).
        await Updated();
        Assert.Null(BackCheck(out string? reason, running: "0.5.6"));
        Assert.Equal(UpdateText.NothingToGoBackTo, reason);
    }

    [Fact]
    public async Task ProofsThatDontVouch_LeaveWithTheSwap_SoProofsInPreviousAlwaysDo()
    {
        // Reviews P4-20, P2-1.
        await Ready();
        UpdateFixture.Write(Path.Combine(_f.Folder.Release, UpdateFolder.ShaName), "junk");
        UpdateFixture.Write(Path.Combine(_f.Folder.Release, UpdateFolder.SigName), "junk");
        Assert.False(Recheck(out _)!.ProofsVouch);

        Exit();

        Assert.False(Directory.Exists(_f.Folder.PreviousRelease));
        Assert.False(Directory.Exists(_f.Folder.Outgoing));
        Assert.NotNull(BackCheck(out _));
    }

    [Fact]
    public async Task TamperedProofsInPrevious_Refuse()
    {
        // Only an absent folder reads as installed by hand: the rollback's half of the chain failed open (review P2-1).
        await Updated();
        UpdateFixture.Write(Path.Combine(_f.Folder.PreviousRelease, UpdateFolder.ShaName), "junk");
        UpdateFixture.Write(Path.Combine(_f.Folder.PreviousRelease, UpdateFolder.SigName), "junk");
        Assert.Null(BackCheck(out string? reason));
        Assert.Equal(UpdateText.OldVersionNotSigned("0.5.6"), reason);
    }

    [Fact]
    public async Task AFileNobodyListed_InPrevious_Refuses()
    {
        // previous/ holds only verified old files now, and a planted DLL would come in beside the exe (reviews P4-1, P2-7).
        await Updated();
        UpdateFixture.Write(InPrevious("planted.dll"), "evil");
        Assert.Null(BackCheck(out string? reason));
        Assert.Equal(UpdateText.OldVersionUnlisted("planted.dll"), reason);
    }

    [Fact]
    public async Task ASharedFileGoneSinceTheUpdate_Refuses()
    {
        // The manifests agreed on it, and the swap then took away the only way back (review P7-4).
        await Updated();
        File.Delete(_f.Folder.Full("same.dll"));
        Assert.Null(BackCheck(out string? reason));
        Assert.Equal(UpdateText.OldVersionIncomplete("same.dll"), reason);
    }

    [Fact]
    public async Task ARollbackRefusedAtExit_UsesItsOwnWords()
    {
        // Review P7-11.
        await Updated();
        AskRollback();
        File.WriteAllText(InPrevious("a.dll"), "altered");

        Assert.Equal([UpdateText.WentBackFailedLine(UpdateText.OldVersionAltered("a.dll"))], Exit("0.5.7"));

        Assert.False(File.Exists(_f.Folder.Rollback));
        Assert.Equal("new a", Installed("a.dll"));
    }

    [Fact]
    public async Task AnEditedOrMissingDoc_DoesntBlockGoingBack_AndItsCurrentCopyStays()
    {
        // An edit to a doc shouldn't cost the way back (review P2-2, the user's call).
        _f.Install("0.5.6", new Dictionary<string, string> { [UpdateFixture.Exe] = "old exe", ["docs/a.md"] = "old a", ["docs/b.md"] = "old b" });
        var release = _f.Publish("0.5.7", new Dictionary<string, string> { [UpdateFixture.Exe] = "new exe", ["docs/a.md"] = "new a", ["docs/b.md"] = "new b" });
        Assert.True((await _f.Stage().StageAsync(release, null, CancellationToken.None)).Ok);
        Exit();
        Printed(Start("0.5.7"));
        File.WriteAllText(InPrevious("docs/a.md"), "edited");
        File.Delete(InPrevious("docs/b.md"));

        var plan = BackCheck(out string? reason);

        Assert.Null(reason);
        Assert.Equal(["docs/a.md", "docs/b.md"], plan!.DocsStaying);
        Assert.DoesNotContain("docs/a.md", plan.Incoming);
        AskRollback();
        Exit("0.5.7");
        Assert.Equal("old exe", Installed(UpdateFixture.Exe));
        Assert.Equal("new a", Installed("docs/a.md"));
        Assert.Equal("new b", Installed("docs/b.md"));
        Assert.False(Directory.Exists(_f.Folder.Previous));
    }

    [Theory]
    [InlineData("docs/a.md", true)]
    [InlineData("A.MD", true)]
    [InlineData("LICENSE", true)]
    [InlineData("docs/LICENSE.txt", true)]
    [InlineData("README", true)]
    [InlineData("docs/README.txt", true)]
    [InlineData("a.dll", false)]
    [InlineData("NeonSidekick.exe", false)]
    [InlineData("md/a.dll", false)]
    public void WhatCountsAsADoc(string path, bool doc) => Assert.Equal(doc, UpdateRollback.IsDoc(path));

    [Fact]
    public async Task AnEditedFileLeavingInARollback_GoesToKept_AndTheNextStartSays()
    {
        // outgoing/ is deleted at the end, and took a hand-edited copy with it.
        await Updated();
        File.WriteAllText(_f.Folder.Full("docs/new.md"), "mine");
        AskRollback();

        Exit("0.5.7");

        Assert.False(File.Exists(_f.Folder.Full("docs/new.md")));
        Assert.Equal("mine", File.ReadAllText(KeptPath("0.5.7", "docs/new.md")));
        var start = Start("0.5.6");
        Assert.Contains(UpdateText.BackOnLine("0.5.6"), start.Line, StringComparison.Ordinal);
        Assert.Contains(KeptLine("0.5.7"), start.Line, StringComparison.Ordinal);
    }

    // ---- the locks

    [Fact]
    public void TheLock_SharedAllowsShared_RefusesExclusive_AndExclusiveRefusesBoth()
    {
        string path = Path.Combine(_f.Root, ".update", "in-use.lock");
        using (var shared = UpdateLock.TryShared(path))
        using (var again = UpdateLock.TryShared(path))
        using (var exclusive = UpdateLock.TryExclusive(path))
        {
            Assert.NotNull(shared);
            Assert.NotNull(again);
            Assert.Null(exclusive);
        }

        using var only = UpdateLock.TryExclusive(path);
        using var refusedShared = UpdateLock.TryShared(path);
        using var refusedExclusive = UpdateLock.TryExclusive(path);
        Assert.NotNull(only);
        Assert.Null(refusedShared);
        Assert.Null(refusedExclusive);
    }

    [Fact]
    public void Locate_FindsTheFolder_AndTheName()
    {
        string exe = Path.Combine(_f.Root, UpdateFixture.Exe);
        File.WriteAllText(exe, "x");
        Assert.Equal(_f.Root, UpdateInstall.Locate(exe));
        Assert.Equal(UpdateFixture.Exe, UpdateInstall.ExeName(exe));
        Assert.Null(UpdateInstall.Locate(null));
    }

    [UnixFact]
    public void Locate_ResolvesASymlinkOnThePath()
    {
        string real = Path.Combine(_f.Root, "real");
        Directory.CreateDirectory(real);
        string exe = Path.Combine(real, "NeonSidekick");
        File.WriteAllText(exe, "x");
        string link = Path.Combine(_f.Root, "link");
        File.CreateSymbolicLink(link, exe);

        Assert.Equal(real, UpdateInstall.Locate(link));
        Assert.Equal("NeonSidekick", UpdateInstall.ExeName(link));
    }
}
