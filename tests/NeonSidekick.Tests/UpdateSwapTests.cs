using NeonSidekick.Files;
using NeonSidekick.Updates;

namespace NeonSidekick.Tests;

public sealed class UpdateSwapTests : IDisposable
{
    private const string Exe = "NeonSidekick.exe";
    private readonly string _root = Directory.CreateTempSubdirectory("neon-swap-").FullName;
    private readonly UpdateFolder _folder;

    public UpdateSwapTests()
    {
        _folder = new UpdateFolder(_root);
        Directory.CreateDirectory(_folder.Root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class KilledException : Exception
    {
        public KilledException()
            : base("killed")
        {
        }
    }

    /// <summary>Real moves, with one that kills the process at a given count, or stays locked through every retry. An undo's moves are other operations, so they go through.</summary>
    private sealed class Stopping(int at, Func<Exception> fail) : IUpdateFiles
    {
        private readonly UpdateFiles _real = new();
        private int _count;
        private string? _stuck;

        private void Check(string operation)
        {
            if (++_count == at)
            {
                _stuck = operation;
            }

            if (_stuck == operation)
            {
                throw fail();
            }
        }

        public void Move(string from, string to)
        {
            Check("move " + from + " " + to);
            _real.Move(from, to);
        }

        public void MakeFolder(string folder)
        {
            Check("make " + folder);
            _real.MakeFolder(folder);
        }

        public void RemoveFolder(string folder)
        {
            Check("remove " + folder);
            _real.RemoveFolder(folder);
        }
    }

    private string Install(string path, string content = "old")
    {
        string full = _folder.Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private void Stage(string path, string content = "new")
    {
        string full = Path.Combine(_folder.Staging, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Read(string path) => File.ReadAllText(_folder.Full(path));

    private bool Has(string path) => File.Exists(_folder.Full(path));

    private UpdateJournal Plan(string[] moved, string[] dropped, UpdateSwap? swap = null)
    {
        Install(UpdateManifest.FileName, "old manifest");
        Stage(UpdateManifest.FileName, "new manifest");
        foreach (string path in moved)
        {
            Stage(path);
        }

        return (swap ?? new UpdateSwap(_folder)).PlanUpdate("0.5.6", "0.5.7", Exe, Exe, moved, dropped);
    }

    private void AssertOldVersionWhole()
    {
        Assert.Equal("old", Read(Exe));
        Assert.Equal("old manifest", Read(UpdateManifest.FileName));
        Assert.False(File.Exists(_folder.Journal));
    }

    private static UpdateManifest Listing(params (string Path, string Hash)[] files) => new()
    {
        Format = UpdateManifest.CurrentFormat,
        Version = "0.5.7",
        Rid = "win-x64",
        Exe = Exe,
        Files = [.. files.Select(f => new UpdateManifestFile { Path = f.Path, Sha256 = f.Hash })],
    };

    [Fact]
    public void Changed_LeavesWhatMatches_AndTakesWhatDiffers()
    {
        var installed = Listing(("same", "aa"), ("differs", "aa"), ("altered", "aa"), ("missing", "aa"));
        var staged = Listing(("same", "aa"), ("differs", "bb"), ("altered", "aa"), ("missing", "aa"), ("new", "cc"));
        var onDisk = new Dictionary<string, string> { ["same"] = "AA", ["differs"] = "aa", ["altered"] = "ff" };

        var changed = UpdateSwap.Changed(installed, staged, onDisk, StringComparer.Ordinal);

        Assert.Equal(["differs", "altered", "missing", "new"], changed);
    }

    [Fact]
    public void Changed_MoveAll_TakesEverything()
    {
        var both = Listing(("same", "aa"));
        Assert.Equal(["same"], UpdateSwap.Changed(both, both, new Dictionary<string, string> { ["same"] = "aa" }, StringComparer.Ordinal, moveAll: true));
    }

    [Fact]
    public void Dropped_IsWhatOnlyTheOldListed()
    {
        Assert.Equal(["gone"], UpdateSwap.Dropped(Listing(("kept", "aa"), ("gone", "aa")), Listing(("kept", "bb")), StringComparer.Ordinal));
    }

    [Fact]
    public void ACleanSwap_MovesTheChanged_LeavesTheRest_AndKeepsTheOldAside()
    {
        Install(Exe);
        Install("a.dll");
        Install("same.dll", "untouched");
        Install("gone.dll");
        Install("logs/session.log", "mine");
        var swap = new UpdateSwap(_folder);

        var result = swap.Run(Plan([Exe, "a.dll", "docs/new.md"], ["gone.dll"]));

        Assert.True(result.Done);
        Assert.Equal("new", Read(Exe));
        Assert.Equal("new", Read("a.dll"));
        Assert.Equal("new", Read("docs/new.md"));
        Assert.Equal("new manifest", Read(UpdateManifest.FileName));
        Assert.Equal("untouched", Read("same.dll"));
        Assert.False(Has("gone.dll"));
        Assert.Equal("mine", Read("logs/session.log"));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_folder.Previous, Exe)));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_folder.Previous, "gone.dll")));
        Assert.Equal("old manifest", File.ReadAllText(_folder.PreviousManifest));
        Assert.False(File.Exists(Path.Combine(_folder.Previous, "same.dll")));
        Assert.False(Directory.Exists(_folder.Staging));
        Assert.False(Directory.Exists(_folder.Staged));
    }

    [Fact]
    public void TheExe_MovesLast_WithTheOldOneJustBeforeIt()
    {
        Install(Exe);
        Install("a.dll");
        var journal = Plan([Exe, "a.dll"], []);
        var moves = journal.Steps.Where(s => s.Kind == UpdateStepKind.Move).ToList();
        Assert.Equal(Exe, moves[^1].To);
        Assert.EndsWith("staging/" + Exe, moves[^1].From, StringComparison.Ordinal);
        Assert.Equal(Exe, moves[^2].From);
    }

    [Fact]
    public void TheJournal_MarksFinished_AndCleansUp()
    {
        Install(Exe);
        File.WriteAllText(_folder.Ready, "");
        File.WriteAllText(_folder.Rollback, "");
        var swap = new UpdateSwap(_folder);
        swap.Run(Plan([Exe], []));

        var journal = UpdateJournal.TryRead(_folder);
        Assert.True(journal!.Finished);
        Assert.Equal("0.5.7", journal.Target);
        Assert.False(File.Exists(_folder.Ready));
        Assert.False(File.Exists(_folder.Rollback));
    }

    [Fact]
    public void TheProofs_Move_SoEachVersionKeepsItsOwn()
    {
        Install(Exe);
        Directory.CreateDirectory(_folder.Release);
        File.WriteAllText(Path.Combine(_folder.Release, UpdateFolder.ShaName), "old proof");
        Directory.CreateDirectory(_folder.Staged);
        File.WriteAllText(_folder.StagedSha, "new proof");
        File.WriteAllText(_folder.StagedSig, "new sig");
        File.WriteAllText(_folder.StagedNotes, "new notes");
        File.WriteAllText(_folder.StagedInstallHashes, "{}");

        Assert.True(new UpdateSwap(_folder).Run(Plan([Exe], [])).Done);

        Assert.Equal("new proof", File.ReadAllText(Path.Combine(_folder.Release, UpdateFolder.ShaName)));
        Assert.Equal("new sig", File.ReadAllText(Path.Combine(_folder.Release, UpdateFolder.SigName)));
        Assert.Equal("new notes", File.ReadAllText(Path.Combine(_folder.Release, UpdateFolder.NotesName)));
        Assert.Equal("old proof", File.ReadAllText(Path.Combine(_folder.PreviousRelease, UpdateFolder.ShaName)));
        Assert.False(File.Exists(Path.Combine(_folder.Release, UpdateFolder.InstallHashesName)));
    }

    [Fact]
    public void TheOldPrevious_IsReplaced_OnASuccess()
    {
        Install(Exe);
        Directory.CreateDirectory(_folder.Previous);
        File.WriteAllText(Path.Combine(_folder.Previous, "older.dll"), "older");

        Assert.True(new UpdateSwap(_folder).Run(Plan([Exe], [])).Done);

        Assert.False(File.Exists(Path.Combine(_folder.Previous, "older.dll")));
        Assert.False(Directory.Exists(_folder.PreviousOld));
    }

    [Fact]
    public void AFailureMidway_IsUndone_WithTheOldPreviousRestored_AndTheStageKept()
    {
        Install(Exe);
        Install("a.dll");
        Install("b/c.dll");
        Directory.CreateDirectory(_folder.Previous);
        File.WriteAllText(Path.Combine(_folder.Previous, "older.dll"), "older");
        var clean = new UpdateSwap(_folder);
        var journal = Plan([Exe, "a.dll", "b/c.dll", "new/d.dll"], []);
        int total = journal.Steps.Count;

        for (int at = 1; at <= total; at++)
        {
            var files = new Stopping(at, () => new IOException("locked"));
            var result = new UpdateSwap(_folder, files, _ => { }).Run(clean.PlanUpdate("0.5.6", "0.5.7", Exe, Exe, [Exe, "a.dll", "b/c.dll", "new/d.dll"], []));

            Assert.False(result.Done);
            Assert.True(result.Undone, $"step {at}");
            AssertOldVersionWhole();
            Assert.Equal("old", Read("a.dll"));
            Assert.Equal("old", Read("b/c.dll"));
            Assert.False(Directory.Exists(_folder.Full("new")), $"step {at}");
            Assert.Equal("older", File.ReadAllText(Path.Combine(_folder.Previous, "older.dll")));
            Assert.False(Directory.Exists(_folder.PreviousOld), $"step {at}");
            Assert.Equal("new", File.ReadAllText(Path.Combine(_folder.Staging, "a.dll")));
            Assert.Equal("new manifest", File.ReadAllText(_folder.StagingManifest));
        }
    }

    [Fact]
    public void AKill_LeavesTheJournal_AndTheNextUndoRestoresEverything()
    {
        Install(Exe);
        Install("a.dll");
        var clean = new UpdateSwap(_folder);
        var moved = new[] { Exe, "a.dll", "new/d.dll" };
        int total = Plan(moved, []).Steps.Count;

        for (int at = 1; at <= total; at++)
        {
            var files = new Stopping(at, () => new KilledException());
            var killed = new UpdateSwap(_folder, files);
            Assert.Throws<KilledException>(() => killed.Run(clean.PlanUpdate("0.5.6", "0.5.7", Exe, Exe, moved, [])));

            var journal = UpdateJournal.TryRead(_folder);
            Assert.NotNull(journal);
            Assert.False(journal.Finished);
            Assert.True(clean.Undo(journal));
            AssertOldVersionWhole();
            Assert.Equal("old", Read("a.dll"));
            Assert.Equal("new", File.ReadAllText(Path.Combine(_folder.Staging, "a.dll")));
        }
    }

    [Fact]
    public void AnUndoStoppedAtEachStep_RunsAgain_AndFinishes()
    {
        Install(Exe);
        Install("a.dll");
        Install("b/c.dll");
        var clean = new UpdateSwap(_folder);
        var moved = new[] { Exe, "a.dll", "b/c.dll", "new/d.dll" };
        var journal = Plan(moved, []);
        int steps = journal.Steps.Count;

        for (int stopAt = 1; stopAt <= steps; stopAt++)
        {
            // Killed at the last step: the swap is as far along as it gets with the old exe still running.
            var run = clean.PlanUpdate("0.5.6", "0.5.7", Exe, Exe, moved, []);
            var swapKilled = new UpdateSwap(_folder, new Stopping(run.Steps.Count, () => new KilledException()));
            Assert.Throws<KilledException>(() => swapKilled.Run(run));

            try
            {
                new UpdateSwap(_folder, new Stopping(stopAt, () => new KilledException())).Undo(UpdateJournal.TryRead(_folder)!);
            }
            catch (KilledException)
            {
                // Killed during the undo; the start after it finishes.
            }

            if (UpdateJournal.TryRead(_folder) is { } again)
            {
                Assert.True(clean.Undo(again), $"stopped at {stopAt}");
            }

            AssertOldVersionWhole();
            Assert.Equal("old", Read("a.dll"));
            Assert.Equal("old", Read("b/c.dll"));
            Assert.False(Directory.Exists(_folder.Full("new")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(_folder.Staging, "a.dll")));
        }
    }

    [Fact]
    public void Strays_AreKept()
    {
        Install(Exe);
        Install("mine.txt", "stray");
        Install("logs/a.log", "stray");
        Assert.True(new UpdateSwap(_folder).Run(Plan([Exe], [])).Done);
        Assert.Equal("stray", Read("mine.txt"));
        Assert.Equal("stray", Read("logs/a.log"));
    }

    [Fact]
    public void AStrayAtAPathTheNewVersionAdds_MovesAside_AndAnUndoPutsItBack()
    {
        Install(Exe);
        Install("added.dll", "stray");
        var clean = new UpdateSwap(_folder);
        var journal = Plan([Exe, "added.dll"], []);

        var files = new Stopping(journal.Steps.Count, () => new IOException("locked"));
        var failed = new UpdateSwap(_folder, files, _ => { }).Run(journal);
        Assert.False(failed.Done);
        Assert.Equal("stray", Read("added.dll"));

        Assert.True(clean.Run(clean.PlanUpdate("0.5.6", "0.5.7", Exe, Exe, [Exe, "added.dll"], [])).Done);
        Assert.Equal("new", Read("added.dll"));
        Assert.Equal("stray", File.ReadAllText(Path.Combine(_folder.Previous, "added.dll")));
    }

    [Fact]
    public void AStrayFileWhereItNeedsAFolder_MovesAside()
    {
        Install(Exe);
        Install("plugins", "a stray file");
        Assert.True(new UpdateSwap(_folder).Run(Plan([Exe, "plugins/a.dll"], [])).Done);
        Assert.Equal("new", Read("plugins/a.dll"));
        Assert.Equal("a stray file", File.ReadAllText(Path.Combine(_folder.Previous, "plugins")));
    }

    [Fact]
    public void ADroppedFile_LeavesItsEmptiedFolder_AndAnUndoBringsItBack()
    {
        Install(Exe);
        Install("old/deep/gone.dll");
        var swap = new UpdateSwap(_folder);
        var journal = Plan([Exe], ["old/deep/gone.dll"]);

        Assert.True(swap.Run(journal).Done);

        Assert.False(Directory.Exists(_folder.Full("old")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_folder.Previous, "old", "deep", "gone.dll")));
    }

    [Fact]
    public void AFolderKeepingAStray_Stays()
    {
        Install(Exe);
        Install("old/gone.dll");
        Install("old/mine.txt", "stray");
        Assert.True(new UpdateSwap(_folder).Run(Plan([Exe], ["old/gone.dll"])).Done);
        Assert.Equal("stray", Read("old/mine.txt"));
    }

    [Fact]
    public void ANewFile_CanTakeADroppedFoldersName_AndTheUndoRestoresIt()
    {
        Install(Exe);
        Install("thing/gone.dll");
        var clean = new UpdateSwap(_folder);
        var moved = new[] { Exe, "thing" };
        var journal = Plan(moved, ["thing/gone.dll"]);

        var files = new Stopping(journal.Steps.Count, () => new IOException("locked"));
        var failed = new UpdateSwap(_folder, files, _ => { }).Run(journal);
        Assert.False(failed.Done);
        Assert.True(failed.Undone);
        Assert.Equal("old", Read("thing/gone.dll"));

        Assert.True(clean.Run(clean.PlanUpdate("0.5.6", "0.5.7", Exe, Exe, moved, ["thing/gone.dll"])).Done);
        Assert.Equal("new", Read("thing"));
    }

    [Fact]
    public void ANewFile_TakesAFoldersNameEvenWithStraysAndEmptySubfoldersInIt()
    {
        Install(Exe);
        Install("thing/stray.txt", "stray");
        Directory.CreateDirectory(_folder.Full("thing/empty"));
        Assert.True(new UpdateSwap(_folder).Run(Plan([Exe, "thing"], [])).Done);
        Assert.Equal("new", Read("thing"));
        Assert.Equal("stray", File.ReadAllText(Path.Combine(_folder.Previous, "thing", "stray.txt")));
    }

    [Fact]
    public void AnEmptySubfolder_DoesNotStopItsEmptiedParentGoing()
    {
        Install(Exe);
        Install("old/gone.dll");
        Directory.CreateDirectory(_folder.Full("old/empty"));
        Assert.True(new UpdateSwap(_folder).Run(Plan([Exe], ["old/gone.dll"])).Done);
        Assert.False(Directory.Exists(_folder.Full("old")));
    }

    [Fact]
    public void Rollback_PutsTheOldFilesBack_AndLeavesTheSharedOnesAlone()
    {
        Install(Exe);
        Install("changed.dll");
        Install("shared.dll", "shared");
        Install("dropped/by-new.dll");
        var swap = new UpdateSwap(_folder);
        Assert.True(swap.Run(Plan([Exe, "changed.dll", "added/new.dll"], ["dropped/by-new.dll"])).Done);
        File.Delete(_folder.Journal);

        var back = swap.PlanRollback("0.5.7", "0.5.6", Exe, Exe, ["added/new.dll"]);
        Assert.True(back.Rollback);
        Assert.True(swap.Run(back).Done);

        Assert.Equal("old", Read(Exe));
        Assert.Equal("old", Read("changed.dll"));
        Assert.Equal("old", Read("dropped/by-new.dll"));
        Assert.Equal("shared", Read("shared.dll"));
        Assert.False(Has("added/new.dll"));
        Assert.False(Directory.Exists(_folder.Full("added")));
        Assert.Equal("old manifest", Read(UpdateManifest.FileName));
        Assert.False(Directory.Exists(_folder.Previous));
        Assert.False(Directory.Exists(_folder.Outgoing));
    }

    [Fact]
    public void Rollback_BringsTheStrayBack()
    {
        Install(Exe);
        Install("added.dll", "stray");
        var swap = new UpdateSwap(_folder);
        Assert.True(swap.Run(Plan([Exe, "added.dll"], [])).Done);
        File.Delete(_folder.Journal);

        Assert.True(swap.Run(swap.PlanRollback("0.5.7", "0.5.6", Exe, Exe, ["added.dll"])).Done);

        Assert.Equal("stray", Read("added.dll"));
    }

    [Fact]
    public void Rollback_SwapsTheProofsBack()
    {
        Install(Exe);
        Directory.CreateDirectory(_folder.Release);
        File.WriteAllText(Path.Combine(_folder.Release, UpdateFolder.ShaName), "old proof");
        Directory.CreateDirectory(_folder.Staged);
        File.WriteAllText(_folder.StagedSha, "new proof");
        var swap = new UpdateSwap(_folder);
        Assert.True(swap.Run(Plan([Exe], [])).Done);
        File.Delete(_folder.Journal);

        Assert.True(swap.Run(swap.PlanRollback("0.5.7", "0.5.6", Exe, Exe, [])).Done);

        Assert.Equal("old proof", File.ReadAllText(Path.Combine(_folder.Release, UpdateFolder.ShaName)));
    }

    [Fact]
    public void ARollbackFailingMidway_IsUndone_AndTheNewVersionStands()
    {
        Install(Exe);
        Install("changed.dll");
        var swap = new UpdateSwap(_folder);
        Assert.True(swap.Run(Plan([Exe, "changed.dll", "added.dll"], [])).Done);
        File.Delete(_folder.Journal);

        int steps = swap.PlanRollback("0.5.7", "0.5.6", Exe, Exe, ["added.dll"]).Steps.Count;
        for (int at = 1; at <= steps; at++)
        {
            var files = new Stopping(at, () => new IOException("locked"));
            var result = new UpdateSwap(_folder, files, _ => { }).Run(swap.PlanRollback("0.5.7", "0.5.6", Exe, Exe, ["added.dll"]));
            Assert.False(result.Done);
            Assert.True(result.Undone, $"step {at}");
            Assert.Equal("new", Read(Exe));
            Assert.Equal("new", Read("added.dll"));
            Assert.Equal("new manifest", Read(UpdateManifest.FileName));
            Assert.Equal("old", File.ReadAllText(Path.Combine(_folder.Previous, Exe)));
            Assert.False(Directory.Exists(_folder.Outgoing), $"step {at}");
        }
    }

    [Fact]
    public void ALockedMove_IsRetried_BeforeItGivesUp()
    {
        Install(Exe);
        var pauses = new List<TimeSpan>();
        var journal = Plan([Exe], []);
        var files = new FailingTimes(UpdateSwap.MoveTries - 1);
        var result = new UpdateSwap(_folder, files, pauses.Add).Run(journal);
        Assert.True(result.Done);
        Assert.Equal(UpdateSwap.MoveTries - 1, pauses.Count);
    }

    private sealed class FailingTimes(int times) : IUpdateFiles
    {
        private readonly UpdateFiles _real = new();
        private int _left = times;

        public void Move(string from, string to)
        {
            if (_left-- > 0)
            {
                throw new IOException("locked");
            }

            _real.Move(from, to);
        }

        public void MakeFolder(string folder) => _real.MakeFolder(folder);

        public void RemoveFolder(string folder) => _real.RemoveFolder(folder);
    }

    [Fact]
    public void ALeftoverStillInUse_StopsTheSwap_WithNothingMoved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // A Unix delete doesn't care about an open file.
        }

        Install(Exe);
        Directory.CreateDirectory(_folder.PreviousOld);
        string held = Path.Combine(_folder.PreviousOld, "held.dll");
        using var open = new FileStream(held, FileMode.Create, FileAccess.Write, FileShare.None);

        var result = new UpdateSwap(_folder).Run(Plan([Exe], []));

        Assert.False(result.Done);
        Assert.Equal("old", Read(Exe));
        Assert.False(File.Exists(_folder.Journal));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"format":1,"source":"0.5.6","target":"0.5.7","steps":[{"kind":"Move","from":"../outside","to":"a"}]}""")]
    [InlineData("""{"format":1,"source":"0.5.6","target":"0.5.7","steps":[{"kind":"Move","from":"a","to":"C:/Windows/evil"}]}""")]
    [InlineData("""{"format":1,"source":"0.5.6","target":"0.5.7","steps":[{"kind":"Move","from":"a"}]}""")]
    [InlineData("""{"format":1,"source":"0.5.6","target":"0.5.7","steps":[{"kind":"RemoveFolder","from":"a/../.."}]}""")]
    [InlineData("""{"format":2,"source":"0.5.6","target":"0.5.7","steps":[]}""")]
    [InlineData("""{"format":1,"source":"nope","target":"0.5.7","steps":[]}""")]
    [InlineData("""{"format":1,"source":"0.5.6","target":"0.5.7","steps":[{"kind":"Bogus","from":"a","to":"b"}]}""")]
    public void APlantedJournal_IsRejected_AndDeleted_WithNothingMoved(string json)
    {
        string kept = Install("a", "a file");
        File.WriteAllText(_folder.Journal, json);

        Assert.Null(UpdateJournal.TryRead(_folder));

        Assert.False(File.Exists(_folder.Journal));
        Assert.Equal("a file", File.ReadAllText(kept));
    }

    [Fact]
    public void NoJournal_IsNull() => Assert.Null(UpdateJournal.TryRead(_folder));

    [Fact]
    public void TheJournalFormat_IsPinned()
    {
        // A lasting contract: an old exe's journal can meet a new exe at the next start.
        File.WriteAllText(_folder.Journal, """
            {
              "format": 1,
              "source": "0.5.6",
              "target": "0.5.7",
              "rollback": false,
              "finished": false,
              "steps": [
                { "kind": "MakeFolder", "to": ".update/previous" },
                { "kind": "Move", "from": "a.dll", "to": ".update/previous/a.dll" },
                { "kind": "RemoveFolder", "from": "old" }
              ]
            }
            """);
        var journal = UpdateJournal.TryRead(_folder);
        Assert.NotNull(journal);
        Assert.Equal(3, journal.Steps.Count);
        Assert.Equal(UpdateStepKind.Move, journal.Steps[1].Kind);
    }

    [Fact]
    public void TheCaseRule_LeavesATrueCaseChangeToOneMove()
    {
        // A changed name's case on a case-blind volume is one file, not a dropped one and a new one.
        var comparer = CaseRule.Comparer(_root);
        if (comparer != StringComparer.OrdinalIgnoreCase)
        {
            return;
        }

        var installed = Listing(("A.dll", "aa"));
        var staged = Listing(("a.dll", "bb"));
        Assert.Empty(UpdateSwap.Dropped(installed, staged, comparer));
        Assert.Equal(["a.dll"], UpdateSwap.Changed(installed, staged, new Dictionary<string, string> { ["A.dll"] = "aa" }, comparer));
    }
}
