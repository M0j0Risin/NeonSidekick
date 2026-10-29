using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// A turn's file changes journaled, kept and replayed backwards (2026-10-28, the checkpoints plan): the bytes come back exactly, a file a
/// turn made goes to the bin, a file changed since is a conflict the user answers. The store is <see cref="FakeCheckpointStore"/> (the real
/// one has <see cref="CheckpointStoreTests"/>); the bin is <see cref="FakeRecycleBin"/>, never the system's.
/// </summary>
public sealed class FileRestoreTests : IDisposable
{
    private readonly CheckpointRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void AWriteANewFileAndADelete_ComeBack_ToTheExactBytes_TheNewFileToTheBin()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF, (byte)'o', (byte)'n', (byte)'e', (byte)'\r', (byte)'\n', (byte)'t', (byte)'w', (byte)'o', (byte)'\n', 0xFF];   // a BOM, mixed endings, an invalid UTF-8 byte
        File.WriteAllBytes(_rig.PathOf("notes.txt"), bom);
        File.WriteAllBytes(_rig.PathOf("old.bin"), Encoding.Unicode.GetBytes("utf-16 text"));
        string id = _rig.Turn(files =>
        {
            Assert.Equal(FileOutcome.Ok, files.WriteText("notes.txt", "replaced\n", overwrite: true).Outcome);
            Assert.Equal(FileOutcome.Ok, files.WriteText("new.txt", "made\n", overwrite: false).Outcome);
            Assert.Equal(FileOutcome.Ok, files.Delete("old.bin").Outcome);
        });

        var plan = _rig.Plan(id);
        Assert.Empty(plan.Conflicts);
        var report = _rig.Undo(false, id);

        Assert.False(report.Stopped);
        Assert.Equal(bom, File.ReadAllBytes(_rig.PathOf("notes.txt")));
        Assert.Equal(Encoding.Unicode.GetBytes("utf-16 text"), File.ReadAllBytes(_rig.PathOf("old.bin")));
        Assert.False(File.Exists(_rig.PathOf("new.txt")));
        Assert.Equal([_rig.PathOf("new.txt")], _rig.Bin.Moved);
        Assert.Equal([id], report.Spent);
        Assert.Empty(((FakeCheckpointStore)_rig.Store).Records);
    }

    [Fact]
    public void AFileMadeDeepInAnEmptyFolder_IsUndoneToNothing_NotToEmptyFolders()
    {
        string id = _rig.Turn(files => Assert.Equal(FileOutcome.Ok, files.WriteText("a/b/c.txt", "deep\n", overwrite: false).Outcome));

        _rig.Undo(false, id);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_rig.Root));
    }

    [Fact]
    public void AMoveThenAWrite_ReplaysBackwards()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "first\n");
        string id = _rig.Turn(files =>
        {
            Assert.Equal(FileOutcome.Ok, files.Move("a.txt", "b.txt", overwrite: false).Outcome);
            Assert.Equal(FileOutcome.Ok, files.WriteText("b.txt", "second\n", overwrite: true).Outcome);
        });

        _rig.Undo(false, id);

        Assert.Equal("first\n", File.ReadAllText(_rig.PathOf("a.txt")));
        Assert.False(File.Exists(_rig.PathOf("b.txt")));
    }

    [Fact]
    public void ADeletedFolder_ComesBack_WithItsFiles()
    {
        Directory.CreateDirectory(_rig.PathOf("dir/sub"));
        File.WriteAllText(_rig.PathOf("dir/one.txt"), "1");
        File.WriteAllText(_rig.PathOf("dir/sub/two.txt"), "2");
        string id = _rig.Turn(files => Assert.Equal(FileOutcome.Ok, files.Delete("dir").Outcome));
        Assert.False(Directory.Exists(_rig.PathOf("dir")));

        _rig.Undo(false, id);

        Assert.Equal("1", File.ReadAllText(_rig.PathOf("dir/one.txt")));
        Assert.Equal("2", File.ReadAllText(_rig.PathOf("dir/sub/two.txt")));
    }

    [Fact]
    public void AFileChangedSince_IsAConflict_SkippedLeavesYours_RestoreOverBinsYoursFirst()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "before\n");
        string id = _rig.Turn(files => files.WriteText("a.txt", "the model's\n", overwrite: true));
        File.WriteAllText(_rig.PathOf("a.txt"), "yours\n");

        var plan = _rig.Plan(id);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal("a.txt", conflict.Where.Path);
        Assert.False(conflict.KindChanged);

        var skipped = FileRestore.Run(plan, _rig.Store, _rig.Files.PinnedAt, restoreOver: false, _rig.Bin, CancellationToken.None);
        Assert.Equal(1, skipped.Count(RestoreOutcome.Skipped));
        Assert.Equal("yours\n", File.ReadAllText(_rig.PathOf("a.txt")));
        Assert.Empty(_rig.Bin.Moved);

        var over = FileRestore.Run(_rig.Plan(id), _rig.Store, _rig.Files.PinnedAt, restoreOver: true, _rig.Bin, CancellationToken.None);
        Assert.Equal(1, over.Count(RestoreOutcome.Restored));
        Assert.Equal("before\n", File.ReadAllText(_rig.PathOf("a.txt")));
        Assert.Equal([_rig.PathOf("a.txt")], _rig.Bin.Moved);
    }

    [Fact]
    public void AFolderWhereAFileWas_IsSkipped_EvenUnderRestoreOver_ItsContentsUntouched()
    {
        File.WriteAllText(_rig.PathOf("a"), "a file\n");
        string id = _rig.Turn(files => files.Delete("a"));
        Directory.CreateDirectory(_rig.PathOf("a"));
        File.WriteAllText(_rig.PathOf("a/keep.txt"), "mine");

        var plan = _rig.Plan(id);
        Assert.True(Assert.Single(plan.Conflicts).KindChanged);
        var report = FileRestore.Run(plan, _rig.Store, _rig.Files.PinnedAt, restoreOver: true, _rig.Bin, CancellationToken.None);

        Assert.Equal(1, report.Count(RestoreOutcome.Skipped));
        Assert.Equal("mine", File.ReadAllText(_rig.PathOf("a/keep.txt")));
        Assert.Empty(_rig.Bin.Moved);
    }

    [Fact]
    public void TwoTurnsOnOneFile_UndoneOneByOne_RaiseNoConflict_OverTheFirstUndosOwnRestore()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "0\n");
        string first = _rig.Turn(files => files.WriteText("a.txt", "1\n", overwrite: true));
        string second = _rig.Turn(files => files.WriteText("a.txt", "2\n", overwrite: true));

        _rig.Undo(false, second);
        Assert.Equal("1\n", File.ReadAllText(_rig.PathOf("a.txt")));
        Assert.Empty(_rig.Plan(first).Conflicts);
        _rig.Undo(false, first);

        Assert.Equal("0\n", File.ReadAllText(_rig.PathOf("a.txt")));
    }

    [Fact]
    public void TwoTurnsUndoneAtOnce_GoBackToTheFirstsStart()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "0\n");
        string first = _rig.Turn(files => files.WriteText("a.txt", "1\n", overwrite: true));
        string second = _rig.Turn(files =>
        {
            files.WriteText("a.txt", "2\n", overwrite: true);
            files.WriteText("b.txt", "new\n", overwrite: false);
        });

        var report = _rig.Undo(false, second, first);

        Assert.Equal("0\n", File.ReadAllText(_rig.PathOf("a.txt")));
        Assert.False(File.Exists(_rig.PathOf("b.txt")));
        Assert.Equal(new[] { first, second }.Order(), report.Spent.Order());
    }

    [Fact]
    public void AFileOverThePerFileSetting_IsNotSaved_TheWriteStillHappens_AndTheRestoreLeavesIt()
    {
        _rig.Limits = CheckpointLimits.Default with { LargestFileBytes = 4 };
        File.WriteAllText(_rig.PathOf("big.txt"), "more than four bytes");
        File.WriteAllText(_rig.PathOf("small.txt"), "abc");
        string id = _rig.Turn(files =>
        {
            Assert.Equal(FileOutcome.Ok, files.WriteText("big.txt", "changed", overwrite: true).Outcome);
            Assert.Equal(FileOutcome.Ok, files.WriteText("small.txt", "xyz", overwrite: true).Outcome);
        });
        var log = Assert.Single(_rig.Journal!.Logs);
        Assert.Equal(FileJournal.TooBigReason, log.Entries.Single(e => e.Path == "big.txt").NotSaved);
        Assert.Null(log.Entries.Single(e => e.Path == "small.txt").NotSaved);

        var report = _rig.Undo(false, id);

        Assert.Equal("changed", File.ReadAllText(_rig.PathOf("big.txt")));
        Assert.Equal("abc", File.ReadAllText(_rig.PathOf("small.txt")));
        Assert.Equal(1, report.Count(RestoreOutcome.NotSaved));
    }

    [Fact]
    public void AnOnlineOnlyFile_IsNotSaved_WithoutBeingRead()
    {
        File.WriteAllText(_rig.PathOf("cloud.txt"), "in the cloud");
        WorkingDirectory.OfflineOverride = path => path.EndsWith("cloud.txt", StringComparison.Ordinal);
        try
        {
            _rig.Turn(files => files.WriteText("cloud.txt", "local now", overwrite: true));
        }
        finally
        {
            WorkingDirectory.OfflineOverride = null;
        }

        Assert.Equal(FileJournal.OfflineReason, Assert.Single(Assert.Single(_rig.Journal!.Logs).Entries).NotSaved);
        Assert.Equal("local now", File.ReadAllText(_rig.PathOf("cloud.txt")));
    }

    [Fact]
    public void ASaveShortOfItsSizePlusTheReserve_IsNotSaved_WhileASmallerOneIsKept()
    {
        File.WriteAllText(_rig.PathOf("big.txt"), new string('b', 100));
        File.WriteAllText(_rig.PathOf("small.txt"), new string('s', 10));
        _rig.Turn(
            files =>
            {
                files.WriteText("big.txt", "x", overwrite: true);
                files.WriteText("small.txt", "y", overwrite: true);
            },
            freeSpace: _ => WorkingDirectory.FreeSpaceReserve + 50);

        var entries = Assert.Single(_rig.Journal!.Logs).Entries;
        Assert.Equal(FileJournal.NoRoomReason, entries.Single(e => e.Path == "big.txt").NotSaved);
        Assert.Null(entries.Single(e => e.Path == "small.txt").NotSaved);
    }

    [Fact]
    public void AGoneRoot_IsReportedFailed()
    {
        string id = _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));
        GitAccessTests.DeleteTree(_rig.Root);

        var report = _rig.Undo(false, id);

        // 2026-10-28, review P9-03: the gone root's paths read Absent, which "already back" took for put back and spent.
        Assert.NotEmpty(report.Files);
        Assert.All(report.Files, f => Assert.Equal(RestoreOutcome.Failed, f.Outcome));
        Assert.Empty(report.Spent);
        Assert.True(report.Kept);
    }

    [Fact]
    public void AStateSealedPastTheHashBudget_MatchesAnUntouchedFile_AnUnreadableOneNothing()
    {
        // 2026-10-28, review P1-06 and P7-06.
        var sealedBySize = new CheckpointState { Kind = CheckpointKind.File, Size = 3, Written = 42 };
        var hashed = new CheckpointState { Kind = CheckpointKind.File, Size = 3, Written = 42, Hash = "AB" };
        var unreadable = new CheckpointState { Kind = CheckpointKind.File, Size = 3, Written = CheckpointState.UnknownWritten };

        Assert.True(sealedBySize.Matches(hashed));
        Assert.True(hashed.Matches(sealedBySize));
        Assert.False(hashed.Matches(new CheckpointState { Kind = CheckpointKind.File, Size = 3, Written = 43 }));
        Assert.False(unreadable.Matches(unreadable));
        Assert.True(new CheckpointState { Kind = CheckpointKind.Link }.Matches(new CheckpointState { Kind = CheckpointKind.Link, Target = "" }));
    }

    [Fact]
    public void RestoreOver_WithTheSavedBytesGone_LeavesYoursInPlace()
    {
        // 2026-10-28, review P1-04: yours went to the bin before the saved bytes were read, then nothing came back.
        File.WriteAllText(_rig.PathOf("a.txt"), "before\n");
        string id = _rig.Turn(files => files.WriteText("a.txt", "the model's\n", overwrite: true));
        File.WriteAllText(_rig.PathOf("a.txt"), "yours\n");
        ((FakeCheckpointStore)_rig.Store).DropBlobs();

        var report = FileRestore.Run(_rig.Plan(id), _rig.Store, _rig.Files.PinnedAt, restoreOver: true, _rig.Bin, CancellationToken.None);

        Assert.Equal(1, report.Count(RestoreOutcome.Failed));
        Assert.Equal("yours\n", File.ReadAllText(_rig.PathOf("a.txt")));
        Assert.Empty(_rig.Bin.Moved);
    }

    [Fact]
    public void AFileTwoTurnsMadeAndWrote_GoesToTheBinOnce()
    {
        // 2026-10-28, review P6-07: each newer content went to the bin as a phantom copy.
        string first = _rig.Turn(files => files.WriteText("new.txt", "1\n", overwrite: false));
        File.WriteAllText(_rig.PathOf("new.txt"), "1\n");
        string second = _rig.Turn(files => files.WriteText("new.txt", "2\n", overwrite: true));

        var report = _rig.Undo(false, second, first);

        Assert.False(File.Exists(_rig.PathOf("new.txt")));
        Assert.Equal([_rig.PathOf("new.txt")], _rig.Bin.Moved);
        Assert.Equal(RestoreOutcome.Restored, Assert.Single(report.Files).Outcome);
    }

    [Fact]
    public void AFileWhoseOldestBytesWerentKept_IsLeftWhole_NotHalfReplayed()
    {
        // 2026-10-28, review P9-04: the newer turn's bytes were put back, then the report said "not kept".
        File.WriteAllText(_rig.PathOf("big.txt"), "more than four bytes");
        _rig.Limits = CheckpointLimits.Default with { LargestFileBytes = 4 };
        string first = _rig.Turn(files => files.WriteText("big.txt", "ab", overwrite: true));
        _rig.Limits = CheckpointLimits.Default;
        string second = _rig.Turn(files => files.WriteText("big.txt", "cd", overwrite: true));

        var report = _rig.Undo(false, second, first);

        Assert.Equal("cd", File.ReadAllText(_rig.PathOf("big.txt")));
        Assert.Equal(RestoreOutcome.NotSaved, Assert.Single(report.Files).Outcome);
    }

    [Fact]
    public void AnEditSavedAfterThePlan_IsSkipped_WhateverRestoreOverSaid()
    {
        // 2026-10-28, review P4-02: an edit saved while the question was open was written over.
        File.WriteAllText(_rig.PathOf("a.txt"), "before\n");
        string id = _rig.Turn(files => files.WriteText("a.txt", "the model's\n", overwrite: true));
        var plan = _rig.Plan(id);
        Assert.Empty(plan.Conflicts);
        File.WriteAllText(_rig.PathOf("a.txt"), "saved meanwhile\n");

        var report = FileRestore.Run(plan, _rig.Store, _rig.Files.PinnedAt, restoreOver: true, _rig.Bin, CancellationToken.None);

        Assert.Equal(RestoreOutcome.Skipped, Assert.Single(report.Files).Outcome);
        Assert.Equal("saved meanwhile\n", File.ReadAllText(_rig.PathOf("a.txt")));
        Assert.Empty(_rig.Bin.Moved);
    }

    [Fact]
    public void AMadeFolderWithSomethingNewInIt_IsSkipped_AndKept()
    {
        // 2026-10-28, review P1-14.
        string id = _rig.Turn(files => files.CreateDirectory("made"));
        File.WriteAllText(_rig.PathOf("made/yours.txt"), "y");

        var report = _rig.Undo(false, id);

        Assert.Equal(RestoreOutcome.Skipped, Assert.Single(report.Files).Outcome);
        Assert.True(File.Exists(_rig.PathOf("made/yours.txt")));
    }

    [Fact]
    public void TheDiffs_GoToTextFiles_PastMadeFolders()
    {
        // 2026-10-28, review P7-07: the first 20 paths took the slots whether or not they had text.
        File.WriteAllText(_rig.PathOf("a.txt"), "before\n");
        string id = _rig.Turn(files =>
        {
            for (int i = 0; i < FileRestore.ListedFiles + 5; i++)
            {
                files.CreateDirectory("d" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            files.WriteText("a.txt", "after\n", overwrite: true);
        });

        var report = _rig.Undo(false, id);

        Assert.NotNull(report.Files.Single(f => f.Where.Path == "a.txt").Diff);
    }

    [Fact]
    public void AMoveWhoseSourceIsTakenAgain_SkipsBothEnds()
    {
        // 2026-10-28, review P2-01: the skipped move's destination was reported restored.
        File.WriteAllText(_rig.PathOf("a.txt"), "a\n");
        string id = _rig.Turn(files => files.Move("a.txt", "b.txt", overwrite: false));
        File.WriteAllText(_rig.PathOf("a.txt"), "yours\n");

        var report = _rig.Undo(false, id);

        Assert.DoesNotContain(report.Files, f => f.Outcome == RestoreOutcome.Restored);
        Assert.Equal("yours\n", File.ReadAllText(_rig.PathOf("a.txt")));
        Assert.Equal("a\n", File.ReadAllText(_rig.PathOf("b.txt")));
        Assert.Empty(report.Spent.Except([id]));
    }

    [Fact]
    public void AMovedFileEditedSince_IsSkipped_EvenUnderRestoreOver()
    {
        // 2026-10-28, review P8-02: its old bytes were never saved, so moving it back would pass your edit off as the old file.
        File.WriteAllText(_rig.PathOf("a.txt"), "a\n");
        string id = _rig.Turn(files => files.Move("a.txt", "b.txt", overwrite: false));
        File.WriteAllText(_rig.PathOf("b.txt"), "edited\n");

        var plan = _rig.Plan(id);
        Assert.True(Assert.Single(plan.Conflicts).KindChanged);
        var report = FileRestore.Run(plan, _rig.Store, _rig.Files.PinnedAt, restoreOver: true, _rig.Bin, CancellationToken.None);

        Assert.DoesNotContain(report.Files, f => f.Outcome == RestoreOutcome.Restored);
        Assert.Equal("edited\n", File.ReadAllText(_rig.PathOf("b.txt")));
        Assert.False(File.Exists(_rig.PathOf("a.txt")));
    }

    [Fact]
    public void ACaseOnlyRename_IsUndone_ToTheOldCase()
    {
        // 2026-10-28, review P8-01: case-blind, the new name read as the old one, "already back", and stayed.
        if (CaseRule.IsSensitive(_rig.Root))
        {
            return;
        }

        File.WriteAllText(_rig.PathOf("Readme.txt"), "r\n");
        string id = _rig.Turn(files => Assert.Equal(FileOutcome.Ok, files.Move("Readme.txt", "README.txt", overwrite: false).Outcome));
        Assert.Equal("README.txt", Path.GetFileName(Directory.GetFiles(_rig.Root).Single()));

        var report = _rig.Undo(false, id);

        Assert.Equal("Readme.txt", Path.GetFileName(Directory.GetFiles(_rig.Root).Single()));
        Assert.Equal(RestoreOutcome.Restored, Assert.Single(report.Files).Outcome);
    }

    [WindowsFact]
    public void ARenameThenAWrite_PutsBackTheOldNamesAttributes()
    {
        // 2026-10-28, review P12-04 and P8-06: a rename then an overwrite lost the file's flags; a flag gained since stayed.
        File.WriteAllText(_rig.PathOf("a.txt"), "a\n");
        File.SetAttributes(_rig.PathOf("a.txt"), FileAttributes.Hidden);
        string id = _rig.Turn(files =>
        {
            files.Move("a.txt", "b.txt", overwrite: false);
            files.WriteText("b.txt", "b\n", overwrite: true);
        });
        File.SetAttributes(_rig.PathOf("b.txt"), FileAttributes.Hidden | FileAttributes.System);

        _rig.Undo(false, id);

        var attributes = File.GetAttributes(_rig.PathOf("a.txt"));
        Assert.True(attributes.HasFlag(FileAttributes.Hidden));
        Assert.False(attributes.HasFlag(FileAttributes.System));
        Assert.Equal("a\n", File.ReadAllText(_rig.PathOf("a.txt")));
    }

    [WindowsFact]
    public void AFileOpenElsewhere_KeepsTheCheckpoint_AndASecondUndoFinishes()
    {
        // 2026-10-28, review P1-09: a file open in Word spent the checkpoint with the file never put back.
        File.WriteAllText(_rig.PathOf("a.txt"), "a0\n");
        File.WriteAllText(_rig.PathOf("b.txt"), "b0\n");
        string id = _rig.Turn(files =>
        {
            files.WriteText("a.txt", "a1\n", overwrite: true);
            files.WriteText("b.txt", "b1\n", overwrite: true);
        });

        RestoreReport first;
        using (new FileStream(_rig.PathOf("b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))   // as Word holds a document: readable, not writable
        {
            first = _rig.Undo(false, id);
        }

        Assert.True(first.Kept);
        Assert.Empty(first.Spent);
        Assert.Equal(RestoreOutcome.Failed, first.Files.Single(f => f.Where.Path == "b.txt").Outcome);
        Assert.Equal("a0\n", File.ReadAllText(_rig.PathOf("a.txt")));

        var second = _rig.Undo(false, id);

        Assert.Equal([id], second.Spent);
        Assert.Equal("b0\n", File.ReadAllText(_rig.PathOf("b.txt")));
        Assert.Equal(RestoreOutcome.AlreadyBack, second.Files.Single(f => f.Where.Path == "a.txt").Outcome);
    }

    [Fact]
    public void TheLists_NameAFileInNewFolders_Once_AndAMoveByItsSource()
    {
        // 2026-10-28, review P7-04: one file in two new folders read "3 files".
        File.WriteAllText(_rig.PathOf("m.txt"), "m\n");
        string id = _rig.Turn(files =>
        {
            files.WriteText("x/y/new.txt", "n\n", overwrite: false);
            files.Move("m.txt", "moved.txt", overwrite: false);
            files.CreateDirectory("empty");
        });

        var listed = _rig.Plan(id).Listed;

        Assert.Equal(["empty", "m.txt", "x/y/new.txt"], listed.Select(p => p.Path).Order(StringComparer.Ordinal));
        Assert.True(listed.Single(p => p.Path == "empty").Folder);
    }

    [SymlinkFact]
    public void AWriteThatReplacedALink_IsUndone_ToTheLinkAgain()
    {
        // 2026-10-28, review P3-03: the link's replay found the file the write left and gave up.
        string outside = Path.Combine(_rig.Dir, "target.txt");
        File.WriteAllText(outside, "target\n");
        File.CreateSymbolicLink(_rig.PathOf("ln.txt"), outside);
        string id = _rig.Turn(files => files.Delete("ln.txt"));
        File.WriteAllText(_rig.PathOf("ln.txt"), "a file now\n");

        var report = FileRestore.Run(_rig.Plan(id), _rig.Store, _rig.Files.PinnedAt, restoreOver: true, _rig.Bin, CancellationToken.None);

        Assert.Equal(RestoreOutcome.Restored, Assert.Single(report.Files).Outcome);
        Assert.Equal(outside, new FileInfo(_rig.PathOf("ln.txt")).LinkTarget);
        Assert.Equal("target\n", File.ReadAllText(outside));
    }

    [Fact]
    public void ACheckpointNoLongerKept_IsNamedByThePlan()
    {
        string id = _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));
        _rig.Store.Remove(_rig.Profile, [id]);

        var plan = _rig.Plan(id);

        Assert.True(plan.IsEmpty);
        Assert.Equal([id], plan.NoLongerKept);
    }

    [Fact]
    public void Summary_CountsEachOutcome()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "a");
        string id = _rig.Turn(files => files.WriteText("a.txt", "b", overwrite: true));

        Assert.Equal("1 restored, 0 skipped, 0 not saved, 0 failed", FileRestore.Summary(_rig.Undo(false, id)));
    }
}
