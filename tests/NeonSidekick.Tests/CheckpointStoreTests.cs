using LibGit2Sharp;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The profile's bare git stores of file checkpoints (2026-10-28, the checkpoints plan): where a store lands, what a checkpoint commit holds,
/// retention by count, size and age, the start-up sweep, purges, a session's refs and a profile's rename. The real store on a temp profile,
/// the clock a <see cref="ManualTimeProvider"/> set to now (the markers' and objects' ages are the file system's).
/// </summary>
public sealed class CheckpointStoreTests : IDisposable
{
    private readonly ManualTimeProvider _time = new() { UtcNow = DateTimeOffset.UtcNow };
    private readonly CheckpointRig _rig;

    public CheckpointStoreTests()
    {
        _rig = new CheckpointRig(new CheckpointStore(_time));
    }

    public void Dispose() => _rig.Dispose();

    private string StorePath => CheckpointStore.StoreFor(_rig.Profile, _rig.Root, create: false)!;

    private List<string> Refs()
    {
        using var repo = new Repository(StorePath);
        return repo.Refs.FromGlob(CheckpointStore.RefPrefix + "*").Select(r => r.CanonicalName).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void ATurn_LandsInTheProfilesStore_AsAParentlessCommit_UnderItsSessionsRef_WithAFlatTreeAndItsJson()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "same");
        File.WriteAllText(_rig.PathOf("b.txt"), "same");
        string id = _rig.Turn(
            files =>
            {
                files.WriteText("a.txt", "1", overwrite: true);
                files.WriteText("b.txt", "2", overwrite: true);
            },
            session: "12");

        string store = StorePath;
        Assert.Equal(Path.Combine(_rig.Profile, CheckpointStore.FolderName, CheckpointStore.BaseName(_rig.Root) + ".git"), store);
        Assert.Equal(_rig.Root, File.ReadAllText(Path.Combine(store, CheckpointStore.FolderFile)));
        Assert.Equal([CheckpointStore.RefPrefix + "12/" + id], Refs());
        using var repo = new Repository(store);
        var commit = repo.Lookup<Commit>(repo.Refs[CheckpointStore.RefPrefix + "12/" + id].TargetIdentifier);
        Assert.Empty(commit.Parents);
        Assert.Equal(["0001", CheckpointStore.JsonName], commit.Tree.Select(e => e.Name).Order(StringComparer.Ordinal));   // identical content stored once
        Assert.Equal(_rig.Root, Assert.Single(_rig.Store.Find(_rig.Profile, [id])).Record!.Root);
    }

    [Fact]
    public void ATurnRoundTrips_ThroughTheRealStore()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "before\r\nmixed\n");
        string id = _rig.Turn(files =>
        {
            files.WriteText("a.txt", "after", overwrite: true);
            files.WriteText("b.txt", "new", overwrite: false);
        });

        var report = _rig.Undo(false, id);

        Assert.Equal("before\r\nmixed\n", File.ReadAllText(_rig.PathOf("a.txt")));   // no autocrlf, whatever the user's git says
        Assert.False(File.Exists(_rig.PathOf("b.txt")));
        Assert.Equal([id], report.Spent);
        Assert.Empty(Refs());
    }

    [Fact]
    public void BaseName_CutsTheFolderName_AndHashesTheFullPath()
    {
        string name = CheckpointStore.BaseName(Path.Combine(_rig.Dir, new string('x', 200)));

        Assert.StartsWith(new string('x', CheckpointStore.MaxNameChars) + "-", name);
        Assert.Equal(CheckpointStore.MaxNameChars + 9, name.Length);
        Assert.NotEqual(CheckpointStore.BaseName(Path.Combine(_rig.Dir, "a")), CheckpointStore.BaseName(Path.Combine(_rig.Dir, "b", "a")));
    }

    [Fact]
    public async Task ANewerVersion_IsLeftAlone()
    {
        var writer = _rig.Store.Begin(_rig.Profile, _rig.Root, "v2")!;
        _rig.Store.Commit(writer, new CheckpointRecord { Id = "v2", Root = _rig.Root, Version = CheckpointRecord.CurrentVersion + 1 }, CheckpointLimits.Default);
        await _rig.Store.FlushAsync();

        var found = Assert.Single(_rig.Store.Find(_rig.Profile, ["v2"]));

        Assert.Equal(CheckpointProblem.NewerVersion, found.Problem);
        Assert.Null(found.Record);
    }

    [Fact]
    public void Retention_PastTheMaxBytes_DropsTheOldest_TheNewestStaysEvenAloneOverIt()
    {
        _rig.Limits = CheckpointLimits.Default with { MaxBytes = 10 };
        File.WriteAllText(_rig.PathOf("a.txt"), new string('a', 8));
        string first = _rig.Turn(files => files.WriteText("a.txt", new string('b', 20), overwrite: true));
        // No clock step (2026-10-28, review P1-11 and P5-06): the ref just committed is kept and counted first, ties by time then name.
        string second = _rig.Turn(files => files.WriteText("a.txt", "c", overwrite: true));

        Assert.Empty(_rig.Store.Find(_rig.Profile, [first]));
        Assert.Single(_rig.Store.Find(_rig.Profile, [second]));   // 20 saved bytes, over 10, but the newest
    }

    [Fact]
    public void TheSweep_ClearsCheckpointsOlderThanTheKeepDays_AndZeroClearsNoneByAge()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "0");
        string old = _rig.Turn(files => files.WriteText("a.txt", "1", overwrite: true));
        _time.Advance(TimeSpan.FromDays(10));
        string fresh = _rig.Turn(files => files.WriteText("a.txt", "2", overwrite: true));

        _rig.Store.Sweep(_rig.Profile, CheckpointLimits.Default with { KeepDays = 0 });
        Assert.Equal(2, _rig.Store.Find(_rig.Profile, [old, fresh]).Count);

        _rig.Store.Sweep(_rig.Profile, CheckpointLimits.Default with { KeepDays = 5 });
        Assert.Equal([fresh], _rig.Store.Find(_rig.Profile, [old, fresh]).Select(f => f.Id));
    }

    [Fact]
    public void TheSweep_KeepsAGoneFoldersStore_ForKeepDays_ThenDeletesIt_AndAnEmptyOne()
    {
        string id = _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));
        string store = StorePath;
        GitAccessTests.DeleteTree(_rig.Root);

        // 2026-10-28, review P6-02: an unplugged drive or an unmounted volume purged its checkpoints at once; kept for keep (days) now.
        _rig.Store.Sweep(_rig.Profile, CheckpointLimits.Default);
        Assert.True(Directory.Exists(store));
        Assert.True(File.Exists(Path.Combine(store, CheckpointStore.GoneFile)));
        _time.UtcNow += TimeSpan.FromDays(CheckpointLimits.Default.KeepDays - 1);
        _rig.Store.Sweep(_rig.Profile, CheckpointLimits.Default);
        Assert.True(Directory.Exists(store));
        _time.UtcNow += TimeSpan.FromDays(2);
        _rig.Store.Sweep(_rig.Profile, CheckpointLimits.Default);

        Assert.False(Directory.Exists(store));
        Directory.CreateDirectory(_rig.Root);
        _rig.Turn(files => files.WriteText("b.txt", "x", overwrite: false));
        _rig.Store.Remove(_rig.Profile, _rig.Store.Find(_rig.Profile, [_rig.Journal!.Id]).Select(f => f.Id).ToList());
        _rig.Store.Sweep(_rig.Profile, CheckpointLimits.Default);
        Assert.Empty(_rig.Store.Stores(_rig.Profile));
        Assert.Empty(_rig.Store.Find(_rig.Profile, [id]));
    }

    [Fact]
    public void TheSweep_SkipsAStoreWithAFreshMarker()
    {
        _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));
        _rig.Store.Remove(_rig.Profile, [_rig.Journal!.Id]);
        Assert.NotNull(_rig.Store.Begin(_rig.Profile, _rig.Root, "open1"));   // a turn still writing

        _rig.Store.Sweep(_rig.Profile, CheckpointLimits.Default);

        Assert.True(Directory.Exists(StorePath));
        Assert.True(Assert.Single(_rig.Store.Stores(_rig.Profile)).Busy);
    }

    [Fact]
    public async Task RemoveSessions_RemovesOnlyThoseSessionsRefs()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "0");
        string one = _rig.Turn(files => files.WriteText("a.txt", "1", overwrite: true), session: "1");
        string two = _rig.Turn(files => files.WriteText("a.txt", "2", overwrite: true), session: "2");

        _rig.Store.RemoveSessions(_rig.Profile, ["1"]);
        await _rig.Store.FlushAsync();   // queued behind the commits (review P4-06)

        Assert.Equal([CheckpointStore.RefPrefix + "2/" + two], Refs());
        Assert.Empty(_rig.Store.Find(_rig.Profile, [one]));
    }

    [Fact]
    public void Purge_DeletesTheChosenFoldersStore_AndNoOther()
    {
        string other = Path.Combine(_rig.Dir, "other");
        Directory.CreateDirectory(other);
        _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));
        _rig.Turn(_ => new WorkingDirectory(() => other, TimeProvider.System).WriteText("b.txt", "y", overwrite: false), roots: [other]);
        Assert.Equal(2, _rig.Store.Stores(_rig.Profile).Count);

        var result = _rig.Store.Purge(_rig.Profile, [_rig.Root], CancellationToken.None);

        Assert.Equal(1, result.Purged);
        Assert.True(result.Bytes > 0);
        Assert.Equal(other, Assert.Single(_rig.Store.Stores(_rig.Profile)).Root);
        Assert.Empty(Directory.EnumerateDirectories(CheckpointStore.FolderOf(_rig.Profile), "*" + CheckpointStore.PurgingSuffix));

        Assert.Equal(1, _rig.Store.Purge(_rig.Profile, null, CancellationToken.None).Purged);
        Assert.Empty(_rig.Store.Stores(_rig.Profile));
    }

    [Fact]
    public void ASweep_FinishesAPurgeCutShort_AndNeverOpensItAsAStore()
    {
        _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));
        string store = StorePath;
        Directory.Move(store, store + CheckpointStore.PurgingSuffix);

        Assert.Empty(_rig.Store.Stores(_rig.Profile));
        _rig.Store.Sweep(_rig.Profile, CheckpointLimits.Default);

        Assert.False(Directory.Exists(store + CheckpointStore.PurgingSuffix));
    }

    [Fact]
    public void RenameProfile_FollowsAFolderInsideTheProfile()
    {
        string inside = Path.Combine(_rig.Profile, "files");
        Directory.CreateDirectory(inside);
        string id = _rig.Turn(_ => new WorkingDirectory(() => inside, TimeProvider.System).WriteText("a.txt", "x", overwrite: false), roots: [inside]);
        string renamed = Path.Combine(_rig.Dir, "renamed");
        Directory.Move(_rig.Profile, renamed);

        _rig.Store.RenameProfile(_rig.Profile, renamed);

        var store = Assert.Single(_rig.Store.Stores(renamed));
        Assert.Equal(Path.Combine(renamed, "files"), store.Root);
        Assert.Equal(Path.Combine(CheckpointStore.FolderOf(renamed), CheckpointStore.BaseName(store.Root) + ".git"), store.Path);
        var found = Assert.Single(_rig.Store.Find(renamed, [id]));

        // 2026-10-28, review P1-05: the record's own root still named the old profile, so the undo wrote there.
        Assert.Equal(Path.Combine(renamed, "files"), found.Root);
        var report = FileRestore.Run(FileRestore.Plan(_rig.Store, renamed, [id]), _rig.Store, _rig.Files.PinnedAt, false, _rig.Bin, CancellationToken.None);
        Assert.Equal(RestoreOutcome.Restored, Assert.Single(report.Files).Outcome);
        Assert.False(File.Exists(Path.Combine(renamed, "files", "a.txt")));
    }

    [Fact]
    public void RenameProfile_LeavesAStoreWhoseNewNameIsTaken()
    {
        // 2026-10-28, review P11-05: Directory.Move threw and the store was mis-keyed.
        string inside = Path.Combine(_rig.Profile, "files");
        Directory.CreateDirectory(inside);
        _rig.Turn(_ => new WorkingDirectory(() => inside, TimeProvider.System).WriteText("a.txt", "x", overwrite: false), roots: [inside]);
        string renamed = Path.Combine(_rig.Dir, "renamed");
        Directory.Move(_rig.Profile, renamed);
        string taken = Path.Combine(CheckpointStore.FolderOf(renamed), CheckpointStore.BaseName(Path.Combine(renamed, "files")) + ".git");
        Repository.Init(taken, isBare: true);
        File.WriteAllText(Path.Combine(taken, CheckpointStore.FolderFile), Path.Combine(renamed, "files"));

        _rig.Store.RenameProfile(_rig.Profile, renamed);

        Assert.Equal(2, _rig.Store.Stores(renamed).Count);
        Assert.Contains(_rig.Store.Stores(renamed), s => s.Root == inside);
    }

    [Fact]
    public void AStoreWithNoFolderFileAndNoRefs_IsAdopted_NotPassedOver()
    {
        // 2026-10-28, review P11-05: a kill between the init and folder.txt left a store every later turn skipped for "-2".
        string store = CheckpointStore.StoreFor(_rig.Profile, _rig.Root, create: true)!;
        File.Delete(Path.Combine(store, CheckpointStore.FolderFile));

        Assert.Equal(store, CheckpointStore.StoreFor(_rig.Profile, _rig.Root, create: true));
        Assert.Equal(_rig.Root, File.ReadAllText(Path.Combine(store, CheckpointStore.FolderFile)));
    }

    [Fact]
    public void ALaterCommit_PrunesTheRemovedCheckpointsBlob_PastALeftoverPruneLock()
    {
        // 2026-10-28, review P1-01 and P8-03: the stores were never pruned, and a crash's lock file blocked every prune after.
        _rig.Limits = CheckpointLimits.Default with { MaxBytes = 10 };
        File.WriteAllText(_rig.PathOf("a.txt"), new string('a', 20));
        _rig.Turn(files => files.WriteText("a.txt", "b", overwrite: true));
        string blob;
        using (var repo = new Repository(StorePath))
        {
            var commit = repo.Lookup<Commit>(repo.Refs.FromGlob(CheckpointStore.RefPrefix + "*").Single().TargetIdentifier);
            blob = commit.Tree["0001"].Target.Sha;
        }

        File.WriteAllText(Path.Combine(StorePath, CheckpointStore.PruneLock), "left by a crash");
        _time.Advance(TimeSpan.FromHours(2));
        File.WriteAllText(_rig.PathOf("a.txt"), new string('c', 20));
        _rig.Turn(files => files.WriteText("a.txt", "d", overwrite: true));

        using (var repo = new Repository(StorePath))
        {
            Assert.False(repo.ObjectDatabase.Contains(new ObjectId(blob)));
            Assert.Single(repo.Refs.FromGlob(CheckpointStore.RefPrefix + "*"));
        }
    }

    [Fact]
    public void TwoWritersOnOneStore_OneAbandoned_StillBusy()
    {
        // 2026-10-28, review P4-05: the first writer's end freed the store under the second.
        _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));
        var one = _rig.Store.Begin(_rig.Profile, _rig.Root, "one")!;
        var two = _rig.Store.Begin(_rig.Profile, _rig.Root, "two")!;

        _rig.Store.Abandon(one);
        Assert.True(Assert.Single(_rig.Store.Stores(_rig.Profile)).Busy);
        _rig.Store.Abandon(two);
        Assert.False(Assert.Single(_rig.Store.Stores(_rig.Profile)).Busy);
    }

    [Fact]
    public void AWriterWhoseMarkerWasDeleted_SurvivesTheSweep()
    {
        // 2026-10-28, review P1-18: the busy test read only the marker files.
        _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));
        _rig.Store.Remove(_rig.Profile, [_rig.Journal!.Id]);
        var writer = _rig.Store.Begin(_rig.Profile, _rig.Root, "open1")!;
        foreach (string marker in Directory.EnumerateFiles(Path.Combine(StorePath, CheckpointStore.OpenFolder)))
        {
            File.Delete(marker);
        }

        _rig.Store.Sweep(_rig.Profile, CheckpointLimits.Default);

        Assert.True(Directory.Exists(StorePath));
        _rig.Store.Abandon(writer);
    }

    [Fact]
    public void AMarkerOfThisProcess_WithNoLease_IsDead()
    {
        // 2026-10-28, review P2-10: a crash's marker held the store a day; one of this process's never let it go.
        _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));
        _rig.Store.Remove(_rig.Profile, [_rig.Journal!.Id]);
        string store = StorePath;
        string open = Path.Combine(store, CheckpointStore.OpenFolder);
        Directory.CreateDirectory(open);
        File.WriteAllText(Path.Combine(open, "stale"), RecordFiles.Line("pid", Environment.ProcessId, RecordFiles.StartTicks(Environment.ProcessId) ?? 0) + _rig.Root);

        _rig.Store.Sweep(_rig.Profile, CheckpointLimits.Default);

        Assert.False(Directory.Exists(store));
    }

    [Fact]
    public async Task ACommitWithAMissingBlob_NeverFaultsTheFlush_AndMarksTheEntryNotSaved()
    {
        // 2026-10-28, review P2-07.
        var writer = _rig.Store.Begin(_rig.Profile, _rig.Root, "gone")!;
        var record = new CheckpointRecord
        {
            Id = "gone",
            Root = _rig.Root,
            Entries = [new CheckpointEntry { Op = CheckpointOp.Content, Path = "a.txt", Blob = new string('0', 40), Size = 3 }],
        };
        _rig.Store.Commit(writer, record, CheckpointLimits.Default);

        await _rig.Store.FlushAsync();

        var entry = Assert.Single(Assert.Single(_rig.Store.Find(_rig.Profile, ["gone"])).Record!.Entries);
        Assert.Equal(FileJournal.FailedReason, entry.NotSaved);
        Assert.Null(entry.Blob);
    }

    [Fact]
    public void TheLimits_AreClampedToTheSettingsBounds_AndAHugeKeepDaysSweeps()
    {
        // 2026-10-28, review P3-05: an edited profile.json overflowed the cutoff.
        var huge = CheckpointLimits.From(new Settings.AppSettingsData { FileCheckpointKeepDays = int.MaxValue, FileCheckpointMaxMegabytes = int.MaxValue, FileCheckpointLargestFileMegabytes = -1 });
        Assert.Equal(Settings.AppSettingsData.MaxFileCheckpointKeepDays, huge.KeepDays);
        Assert.Equal(Settings.AppSettingsData.MaxFileCheckpointMaxMegabytes * 1024L * 1024L, huge.MaxBytes);
        Assert.Equal(Settings.AppSettingsData.MinFileCheckpointLargestFileMegabytes * 1024L * 1024L, huge.LargestFileBytes);

        string id = _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));
        _rig.Store.Sweep(_rig.Profile, huge);
        _rig.Store.Sweep(_rig.Profile, new CheckpointLimits(int.MaxValue, long.MaxValue, long.MaxValue));
        Assert.Single(_rig.Store.Find(_rig.Profile, [id]));
    }

    [Fact]
    public void APurgeOfOneFolder_DropsItsTwins_KeepsAnotherFoldersOwnTurns()
    {
        // 2026-10-28, review P2-09: a purge left the purged turns' picture halves behind (and could target a pictures-only store).
        string images = Path.Combine(_rig.Dir, "images");
        Directory.CreateDirectory(images);
        var pictures = new WorkingDirectory(() => images, TimeProvider.System);
        string pair = _rig.Turn(
            files =>
            {
                files.WriteText("a.txt", "x", overwrite: false);
                pictures.WriteText("p.png", "p", overwrite: false);
            },
            roots: [_rig.Root, images]);
        string alone = _rig.Turn(_ => pictures.WriteText("q.png", "q", overwrite: false), roots: [images]);

        Assert.Single(_rig.Store.Stores(_rig.Profile, [_rig.Root]));
        var result = _rig.Store.Purge(_rig.Profile, [_rig.Root], CancellationToken.None);

        Assert.Equal(1, result.Purged);
        Assert.Empty(_rig.Store.Find(_rig.Profile, [pair]));
        Assert.Single(_rig.Store.Find(_rig.Profile, [alone]));
        Assert.Empty(_rig.Store.Stores(_rig.Profile, [_rig.Root]));
    }

    /// <summary>
    /// With only a picture checkpoint kept, the working folder has nothing to purge (2026-10-28, review P9-13: the question named the folder
    /// and purged the pictures' store), and the list for a folder holds only that folder's store (P3-13).
    /// </summary>
    [Fact]
    public void OnlyAPictureCheckpoint_LeavesTheFolderNothingToPurge()
    {
        string images = Path.Combine(_rig.Dir, "images");
        Directory.CreateDirectory(images);
        var pictures = new WorkingDirectory(() => images, TimeProvider.System);
        _rig.Turn(_ => pictures.WriteText("q.png", "q", overwrite: false), roots: [images]);

        Assert.Empty(_rig.Store.Stores(_rig.Profile, [_rig.Root]));
        Assert.Equal(images, Assert.Single(_rig.Store.Stores(_rig.Profile, [images])).Root);
        Assert.Equal(0, _rig.Store.Purge(_rig.Profile, [_rig.Root], CancellationToken.None).Purged);
        Assert.Single(_rig.Store.Stores(_rig.Profile));
    }

    [Fact]
    public void ACancelledPurge_CountsWhatIsLeftAsPending_NotPurged()
    {
        // 2026-10-28, review P13-02: the bytes of a store never deleted were counted freed.
        _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var result = _rig.Store.Purge(_rig.Profile, null, stop.Token);

        Assert.Equal(0, result.Purged);
        Assert.Equal(0, result.Bytes);
        Assert.Equal([_rig.Root], result.Pending);
        _rig.Store.Sweep(_rig.Profile, CheckpointLimits.Default);
        Assert.Empty(Directory.EnumerateDirectories(CheckpointStore.FolderOf(_rig.Profile)));
    }

    [Fact]
    public void AGitProject_IsUnchanged_ByACheckpointAndARestore()
    {
        Repository.Init(_rig.Root);
        File.WriteAllText(_rig.PathOf("a.txt"), "tracked\n");
        using (var repo = new Repository(_rig.Root))
        {
            GitAccessTests.PinUnbornHead(repo);
            Commands.Stage(repo, "a.txt");
            var who = new Signature("t", "t@example.com", DateTimeOffset.UtcNow);
            repo.Commit("one", who, who);
        }

        string Snapshot()
        {
            using var repo = new Repository(_rig.Root);
            return string.Join("|", repo.Refs.Select(r => r.CanonicalName + "=" + r.TargetIdentifier))
                + "#" + string.Join("|", repo.RetrieveStatus().Select(s => s.FilePath + ":" + s.State))
                + "#" + string.Join("|", repo.Index.Select(e => e.Path + ":" + e.Id));
        }

        string before = Snapshot();
        string id = _rig.Turn(files => files.WriteText("a.txt", "changed\n", overwrite: true));
        _rig.Undo(false, id);

        Assert.Equal(before, Snapshot());
    }
}
