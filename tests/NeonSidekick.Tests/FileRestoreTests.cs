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

        Assert.NotEmpty(report.Files);
        Assert.All(report.Files, f => Assert.NotEqual(RestoreOutcome.Restored, f.Outcome));
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
