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

        Assert.True(_rig.Journal!.Logs.All(l => l.Entries.Count == 0));
        Assert.Empty(((FakeCheckpointStore)_rig.Store).Records);
    }

    [Fact]
    public void APendingWriteNeverCommitted_LeavesNoEntry()
    {
        // 2026-10-29, review P2-8: an export refused or stopped before its commit left a checkpoint step for a file never written.
        File.WriteAllText(_rig.PathOf("kept.txt"), "old");
        _rig.Turn(files =>
        {
            Assert.Equal(FileOutcome.Ok, files.BeginWrite("gone.txt", overwrite: false, out var pending).Outcome);
            pending!.Dispose();
            Assert.Equal(FileOutcome.Ok, files.BeginWrite("kept.txt", overwrite: true, out var replace).Outcome);
            replace!.Dispose();
            Assert.Equal(FileOutcome.Ok, files.BeginWrite("late.txt", overwrite: false, out var raced).Outcome);
            File.WriteAllText(_rig.PathOf("late.txt"), "someone else's");
            Assert.Equal(FileOutcome.Exists, raced!.Commit().Outcome);
        });

        Assert.True(_rig.Journal!.Logs.All(l => l.Entries.Count == 0));
        Assert.Equal("old", File.ReadAllText(_rig.PathOf("kept.txt")));
    }

    [Fact]
    public void ACommittedPendingWrite_IsJournaled()
    {
        _rig.Turn(files =>
        {
            Assert.Equal(FileOutcome.Ok, files.BeginWrite("made.txt", overwrite: false, out var pending).Outcome);
            pending!.Stream.WriteByte((byte)'x');
            Assert.Equal(FileOutcome.Ok, pending.Commit().Outcome);
        });

        Assert.Contains(Assert.Single(_rig.Journal!.Logs).Entries, e => e is { Op: CheckpointOp.Absent, Path: "made.txt" });
    }

    [Fact]
    public void TheAppsOwnWrites_AreNotJournaled()
    {
        _rig.Turn(files => WriteGuard.AsApp(() => files.WriteText("app.txt", "x", overwrite: false)));

        Assert.True(_rig.Journal!.Logs.All(l => l.Entries.Count == 0));
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

        Assert.True(journal.Logs.All(l => l.Entries.Count == 0));
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
    public void ARootWhoseStoreWontOpen_MarksTheJournalIncomplete()
    {
        // 2026-10-28, review P4-01.
        ((FakeCheckpointStore)_rig.Store).FailBeginFor.Add(_rig.Root);
        _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));

        Assert.True(_rig.Journal!.Incomplete);
    }

    [Fact]
    public void AnUnzipOfManyFilesIntoANewFolder_RecordsTheFolderOnce()
    {
        // 2026-10-28, review P1-03: one Absent per file's folder, fifty of them.
        string zip = _rig.PathOf("pack.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            for (int i = 0; i < 50; i++)
            {
                using var writer = new StreamWriter(archive.CreateEntry("f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt").Open());
                writer.Write("x");
            }
        }

        _rig.Turn(files => Assert.Equal(FileOutcome.Ok, files.Unzip("pack.zip", "out", overwrite: false).Outcome));

        var entries = Assert.Single(_rig.Journal!.Logs).Entries;
        Assert.Single(entries, e => e is { Op: CheckpointOp.Absent, Path: "out", Directory: true });
        Assert.Equal(51, entries.Count);
    }

    [Fact]
    public void ThreeWritesOfOneFile_KeepOneCopy()
    {
        // 2026-10-28, review P1-08.
        File.WriteAllText(_rig.PathOf("a.txt"), "0");
        _rig.Turn(files =>
        {
            for (int i = 1; i <= 3; i++)
            {
                files.WriteText("a.txt", i.ToString(System.Globalization.CultureInfo.InvariantCulture), overwrite: true);
            }
        });

        var log = Assert.Single(_rig.Journal!.Logs);
        Assert.Single(log.Entries);
        Assert.Equal(1, log.SavedBytes);
    }

    /// <summary>
    /// An export's write held open while another write is recorded, then refused: only the export's entry goes, the other's and its bytes
    /// stay (2026-10-30, review F104: the take-back cut every log to where it stood at the export's start).
    /// </summary>
    [Fact]
    public void ATakeBack_LeavesAWriteRecordedAfterItsMark()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "aa");
        File.WriteAllText(_rig.PathOf("b.txt"), "bbb");
        _rig.Turn(files =>
        {
            Assert.Equal(FileOutcome.Ok, files.BeginWrite("a.txt", overwrite: true, out var export).Outcome);
            Assert.Equal(FileOutcome.Ok, files.WriteText("b.txt", "new", overwrite: true).Outcome);
            export!.Dispose();
        });

        var log = Assert.Single(_rig.Journal!.Logs);
        Assert.Equal("b.txt", Assert.Single(log.Entries).Path);
        Assert.Equal(3, log.SavedBytes);
    }

    /// <summary>Two held writes taken back out of order (F104): both entries go, a write between them stays, and the bytes add up.</summary>
    [Fact]
    public void TwoTakeBacks_OutOfOrder_RemoveTheirOwnEntriesOnly()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "a");
        File.WriteAllText(_rig.PathOf("b.txt"), "bb");
        File.WriteAllText(_rig.PathOf("c.txt"), "cccc");
        _rig.Turn(files =>
        {
            Assert.Equal(FileOutcome.Ok, files.BeginWrite("a.txt", overwrite: true, out var first).Outcome);
            Assert.Equal(FileOutcome.Ok, files.BeginWrite("b.txt", overwrite: true, out var second).Outcome);
            Assert.Equal(FileOutcome.Ok, files.WriteText("c.txt", "new", overwrite: true).Outcome);
            first!.Dispose();
            second!.Dispose();
        });

        var log = Assert.Single(_rig.Journal!.Logs);
        Assert.Equal("c.txt", Assert.Single(log.Entries).Path);
        Assert.Equal(4, log.SavedBytes);
    }

    /// <summary>
    /// A held write's path written again meanwhile, which recorded nothing of its own (F104): the held write taken back keeps the path's
    /// entry, so the undo still puts back what was there before both.
    /// </summary>
    [Fact]
    public void ATakeBack_KeepsAnEntryALaterWriteReliedOn()
    {
        File.WriteAllText(_rig.PathOf("p.txt"), "original");
        string id = _rig.Turn(files =>
        {
            Assert.Equal(FileOutcome.Ok, files.BeginWrite("p.txt", overwrite: true, out var export).Outcome);
            Assert.Equal(FileOutcome.Ok, files.WriteText("p.txt", "changed", overwrite: true).Outcome);
            export!.Dispose();
        });

        Assert.Equal("p.txt", Assert.Single(Assert.Single(_rig.Journal!.Logs).Entries).Path);
        _rig.Undo(false, id);
        Assert.Equal("original", File.ReadAllText(_rig.PathOf("p.txt")));
    }

    [Fact]
    public void AWriteAMoveAndAWriteAgain_UndoToTheOriginal_AndNoMovedFile()
    {
        // 2026-10-28, review P13-01: the move took the destination's entry back by index and lost the wrong one.
        File.WriteAllText(_rig.PathOf("a.txt"), "original");
        string id = _rig.Turn(files =>
        {
            files.WriteText("a.txt", "1", overwrite: true);
            files.Move("a.txt", "b.txt", overwrite: false);
            files.WriteText("a.txt", "2", overwrite: false);
        });

        _rig.Undo(false, id);

        Assert.Equal("original", File.ReadAllText(_rig.PathOf("a.txt")));
        Assert.False(File.Exists(_rig.PathOf("b.txt")));
    }

    [Fact]
    public void PastTheEntryCap_TheJournalIsIncomplete_AndKeepsWhatFit()
    {
        // 2026-10-28, review P1-13.
        var journal = new FileJournal("cap", [_rig.Root], r => _rig.Store.Begin(_rig.Profile, r, "cap"), CheckpointLimits.Default.LargestFileBytes, null, maxEntries: 2);
        using (FileJournal.Enter(journal))
        {
            for (int i = 0; i < 3; i++)
            {
                _rig.Files.WriteText("n" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt", "x", overwrite: false);
            }
        }

        journal.Seal();
        Assert.Equal(2, Assert.Single(journal.Logs).Entries.Count);
        Assert.True(journal.Incomplete);
    }

    [Fact]
    public void AtTheCap_AMoveIntoARecordedPath_KeepsItsAbsent()
    {
        // 2026-10-28, review P13-01.
        File.WriteAllText(_rig.PathOf("a.txt"), "a");
        var journal = new FileJournal("cap", [_rig.Root], r => _rig.Store.Begin(_rig.Profile, r, "cap"), CheckpointLimits.Default.LargestFileBytes, null, maxEntries: 2);
        using (FileJournal.Enter(journal))
        {
            _rig.Files.WriteText("b.txt", "b", overwrite: false);
            _rig.Files.Delete("b.txt");
            _rig.Files.Move("a.txt", "b.txt", overwrite: false);
        }

        journal.Seal();
        var entries = Assert.Single(journal.Logs).Entries;
        Assert.Contains(entries, e => e is { Op: CheckpointOp.Absent, Path: "b.txt" });
        Assert.Contains(entries, e => e is { Op: CheckpointOp.Moved, Path: "a.txt", To: "b.txt" });
    }

    [Fact]
    public void APathInANestedRoot_GoesToTheInnerLog_AndAMoveAcrossThem_IsUndone()
    {
        // 2026-10-28, review P10-04 and P11-02: the first root containing the path took it.
        string inner = _rig.PathOf("inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(_rig.PathOf("a.txt"), "a");
        string id = _rig.Turn(
            files =>
            {
                files.WriteText("inner/new.txt", "n", overwrite: false);
                files.Move("a.txt", "inner/a.txt", overwrite: false);
            },
            roots: [_rig.Root, inner]);

        var outer = _rig.Journal!.Logs.Single(l => l.Root == _rig.Root);
        var nested = _rig.Journal!.Logs.Single(l => l.Root == inner);
        Assert.DoesNotContain(outer.Entries, e => e.Path.StartsWith("inner", StringComparison.Ordinal));
        Assert.Contains(nested.Entries, e => e is { Op: CheckpointOp.Absent, Path: "new.txt" });

        _rig.Undo(false, id);

        Assert.Equal("a", File.ReadAllText(_rig.PathOf("a.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(inner));
    }

    [Fact]
    public void AFolderRenamedMidTurn_LaterEntriesUseItsNewSpelling()
    {
        // 2026-10-28, review P2-13: the cached spelling outlived the rename.
        Directory.CreateDirectory(_rig.PathOf("Docs"));
        File.WriteAllText(_rig.PathOf("Docs/x.txt"), "x");
        File.WriteAllText(_rig.PathOf("Docs/y.txt"), "y");
        _rig.Turn(files =>
        {
            files.WriteText("Docs/x.txt", "x2", overwrite: true);
            files.Move("Docs", "Papers", overwrite: false);
            files.WriteText("papers/y.txt", "y2", overwrite: true);
        });

        var entries = Assert.Single(_rig.Journal!.Logs).Entries;
        Assert.Contains(entries, e => e is { Op: CheckpointOp.Content, Path: "Papers/y.txt" });
    }

    [Fact]
    public void ACopiedFolderOverAFile_IsUndone_ToTheFile()
    {
        // 2026-10-28, review P3-02: the file in the copy's way had no entry.
        Directory.CreateDirectory(_rig.PathOf("src"));
        File.WriteAllText(_rig.PathOf("src/in.txt"), "in");
        File.WriteAllText(_rig.PathOf("a.txt"), "the file");
        string id = _rig.Turn(files => Assert.Equal(FileOutcome.Ok, files.Copy("src", "a.txt", overwrite: true).Outcome));
        Assert.True(Directory.Exists(_rig.PathOf("a.txt")));

        _rig.Undo(false, id);

        Assert.Equal("the file", File.ReadAllText(_rig.PathOf("a.txt")));
    }

    [Fact]
    public async Task AWriteFromATaskOutlivingItsTurn_IsDroppedQuietly()
    {
        // 2026-10-28, review P4-11: a task started in the turn kept its sealed journal and marked it incomplete.
        var journal = new FileJournal("late", [_rig.Root], r => _rig.Store.Begin(_rig.Profile, r, "late"), CheckpointLimits.Default.LargestFileBytes);
        using var gate = new ManualResetEventSlim();
        Task late;
        using (FileJournal.Enter(journal))
        {
            late = Task.Run(() =>
            {
                gate.Wait(TimeSpan.FromSeconds(30));
                _rig.Files.WriteText("late.txt", "x", overwrite: false);
            });
        }

        journal.Seal();
        gate.Set();
        await late;

        Assert.False(journal.Incomplete);
        Assert.True(File.Exists(_rig.PathOf("late.txt")));
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
