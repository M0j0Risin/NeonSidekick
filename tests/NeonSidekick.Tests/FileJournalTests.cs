using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// A turn's journal (2026-10-28, the checkpoints plan): which writes it records (its roots' only, never the app's own), each as the step a
/// restore undoes, and how it fails soft (a store that won't open, a write after the seal) without stopping the write.
/// </summary>
public sealed class FileJournalTests : IDisposable
{
    private readonly CheckpointRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void EachWriter_RecordsTheStepARestoreUndoes()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "a");
        File.WriteAllText(_rig.PathOf("m.txt"), "m");
        _rig.Turn(files =>
        {
            files.WriteText("a.txt", "b", overwrite: true);
            files.WriteText("new.txt", "n", overwrite: false);
            files.CreateDirectory("dir");
            files.Move("m.txt", "moved.txt", overwrite: false);
        });

        var entries = Assert.Single(_rig.Journal!.Logs).Entries;
        Assert.Contains(entries, e => e is { Op: CheckpointOp.Content, Path: "a.txt", Blob: not null });
        Assert.Contains(entries, e => e is { Op: CheckpointOp.Absent, Path: "new.txt" });
        Assert.Contains(entries, e => e is { Op: CheckpointOp.Absent, Path: "dir", Directory: true });
        Assert.Contains(entries, e => e is { Op: CheckpointOp.Moved, Path: "m.txt", To: "moved.txt" });
    }

    [Fact]
    public void OnlyTheGivenRoots_AreJournaled()
    {
        string elsewhere = Path.Combine(_rig.Dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var other = new WorkingDirectory(() => elsewhere, TimeProvider.System);
        _rig.Turn(_ => Assert.Equal(FileOutcome.Ok, other.WriteText("x.txt", "x", overwrite: false).Outcome));

        Assert.False(_rig.Journal!.HasEntries);
        Assert.Empty(((FakeCheckpointStore)_rig.Store).Records);
    }

    [Fact]
    public void TheAppsOwnWrites_AreNotJournaled()
    {
        _rig.Turn(files => WriteGuard.AsApp(() => files.WriteText("app.txt", "x", overwrite: false)));

        Assert.False(_rig.Journal!.HasEntries);
        Assert.True(File.Exists(_rig.PathOf("app.txt")));
    }

    [Fact]
    public void AWriteAfterTheSeal_IsNotRecorded_AndMarksTheJournalIncomplete()
    {
        var journal = new FileJournal("abc", [_rig.Root], r => _rig.Store.Begin(_rig.Profile, r, "abc"), CheckpointLimits.Default.LargestFileBytes);
        journal.Seal();
        using (FileJournal.Enter(journal))
        {
            Assert.Equal(FileOutcome.Ok, _rig.Files.WriteText("late.txt", "x", overwrite: false).Outcome);
        }

        Assert.False(journal.HasEntries);
        Assert.True(journal.Incomplete);
        Assert.True(File.Exists(_rig.PathOf("late.txt")));
    }

    [Fact]
    public void AStoreThatWontOpen_LeavesTheOldBytesNotSaved_TheWriteGoesAhead()
    {
        var fake = (FakeCheckpointStore)_rig.Store;
        fake.FailBegin = true;
        File.WriteAllText(_rig.PathOf("a.txt"), "old");
        _rig.Turn(files => Assert.Equal(FileOutcome.Ok, files.WriteText("a.txt", "new", overwrite: true).Outcome));

        Assert.Equal("new", File.ReadAllText(_rig.PathOf("a.txt")));
        var log = Assert.Single(_rig.Journal!.Logs);
        Assert.Null(log.Writer);
        Assert.Equal(FileJournal.FailedReason, Assert.Single(log.Entries).NotSaved);
    }

    [Fact]
    public void NoJournalEntered_RecordsNothing()
    {
        Assert.Null(FileJournal.Current);
        Assert.Equal(FileOutcome.Ok, _rig.Files.WriteText("a.txt", "x", overwrite: false).Outcome);
        Assert.Empty(((FakeCheckpointStore)_rig.Store).Begun);
    }

    [Fact]
    public void Enter_PutsThePreviousJournalBack()
    {
        var outer = new FileJournal("1", [_rig.Root], _ => null, 1);
        var inner = new FileJournal("2", [_rig.Root], _ => null, 1);
        using (FileJournal.Enter(outer))
        {
            using (FileJournal.Enter(inner))
            {
                Assert.Same(inner, FileJournal.Current);
            }

            Assert.Same(outer, FileJournal.Current);
        }

        Assert.Null(FileJournal.Current);
    }

    [Fact]
    public void RelativeOf_AndFullOf_RoundTrip_WithSlashes()
    {
        string full = _rig.PathOf("a/b/c.txt");
        string relative = FileJournal.RelativeOf(_rig.Root, full);

        Assert.Equal("a/b/c.txt", relative);
        Assert.Equal(full, FileJournal.FullOf(_rig.Root, relative));
    }
}
