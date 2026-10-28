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
        _time.Advance(TimeSpan.FromSeconds(5));
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
    public void TheSweep_DeletesAGoneFoldersStore_AndAnEmptyOne()
    {
        string id = _rig.Turn(files => files.WriteText("a.txt", "x", overwrite: false));
        string store = StorePath;
        GitAccessTests.DeleteTree(_rig.Root);

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
    public void RemoveSessions_RemovesOnlyThoseSessionsRefs()
    {
        File.WriteAllText(_rig.PathOf("a.txt"), "0");
        string one = _rig.Turn(files => files.WriteText("a.txt", "1", overwrite: true), session: "1");
        string two = _rig.Turn(files => files.WriteText("a.txt", "2", overwrite: true), session: "2");

        _rig.Store.RemoveSessions(_rig.Profile, ["1"]);

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
        Assert.Single(_rig.Store.Find(renamed, [id]));
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
