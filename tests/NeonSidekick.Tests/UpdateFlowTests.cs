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
        var result = await _f.Stage().StageAsync(_f.Usual(), null, "the notes", CancellationToken.None);
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
        await Ready();
        File.WriteAllText(_f.Folder.Full("same.dll"), "altered");
        // The hashes were taken at staging, so a change after that isn't seen. One before is:
        await _f.Stage().StageAsync(_f.Publish("0.5.7", new Dictionary<string, string> { [UpdateFixture.Exe] = "new exe", ["a.dll"] = "new a", ["same.dll"] = "same", ["docs/new.md"] = "new doc" }), null, null, CancellationToken.None);

        var planned = Recheck(out _);

        Assert.Contains("same.dll", planned!.Moved);
    }

    [Fact]
    public async Task AMissingListedFile_CountsAsChanged()
    {
        _f.Usual();
        File.Delete(_f.Folder.Full("same.dll"));
        await _f.Stage().StageAsync(_f.Publish("0.5.7", new Dictionary<string, string> { [UpdateFixture.Exe] = "new exe", ["a.dll"] = "new a", ["same.dll"] = "same", ["docs/new.md"] = "new doc" }), null, null, CancellationToken.None);
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
        Assert.Empty(Exit(exeName: "Copy.exe"));
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

        Assert.Equal(UpdateText.UpdatedLine("0.5.7"), Start("0.5.7").Line);
        Assert.False(File.Exists(_f.Folder.Journal));
        Assert.Null(Start("0.5.7").Line);
    }

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
        Assert.Throws<KilledException>(() => swap.Run(swap.PlanUpdate("0.5.6", "0.5.7", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped)));
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
        var journal = new UpdateSwap(_f.Folder).PlanUpdate("0.5.6", "0.5.7", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped);
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
        Assert.Throws<KilledException>(() => swap.Run(swap.PlanUpdate("0.5.6", "0.5.7", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped)));

        var lines = Exit();

        Assert.Empty(lines);
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
        var journal = swap.PlanUpdate("0.5.6", "0.5.7", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped);
        Assert.Throws<KilledException>(() => swap.Run(journal));
        string before = File.ReadAllText(_f.Folder.Journal);

        var lines = Exit(files: new KillAllMoves());

        Assert.Empty(lines);
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

        Assert.Empty(Exit("0.5.7"));

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
        var journal = swap.PlanUpdate("0.5.5", "0.5.8", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped);
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
        Assert.Throws<KilledException>(() => swap.Run(swap.PlanUpdate("0.5.6", "0.5.7", planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped)));

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
        Start("0.5.7");
    }

    private void AskRollback() => File.WriteAllText(_f.Folder.Rollback, "");

    [Fact]
    public async Task ARollback_PutsTheOldVersionBack_AndSaysSoNextLaunch()
    {
        await Updated();
        AskRollback();

        var lines = Exit("0.5.7");

        Assert.Equal([UpdateText.Installing("0.5.6"), UpdateText.InstalledLine("0.5.6")], lines);
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
        Assert.Equal(UpdateText.Installing("0.5.6"), Exit("0.5.7")[0]);
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
