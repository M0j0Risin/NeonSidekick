using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The owner lock (2026-10-29, <see cref="OwnerLock"/>, lifted out of the Ralph loop's lock so the code audit's reports take one too): taken whole by
/// one of many racers, never read half written, a stale one taken over, the volume's errnos read, and a lock that can't be written told apart from
/// one another process holds.
/// </summary>
public sealed class OwnerLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly WorkingDirectory _files;
    private const string Lock = ".neon/audits/report.lock";
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 9, 0, 0, TimeSpan.FromHours(1));

    public OwnerLockTests()
    {
        _files = new WorkingDirectory(() => _dir, new ManualTimeProvider());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Folder => Path.Combine(_dir, ".neon", "audits");

    private string LockFile => Path.Combine(Folder, "report.lock");

    [Fact]
    public void TheLock_RefusesAnotherLiveProcess_TakesOverAStaleOne_AndIsLetGo()
    {
        var me = new LockOwner(100, T0);
        var other = new LockOwner(200, T0.AddHours(-1));

        Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_files, Lock, other, _ => true));   // makes .neon/audits on the way
        Assert.Equal(LockTake.Held, OwnerLock.TryLock(_files, Lock, me, _ => true));
        Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_files, Lock, me, owner => owner != other));
        Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_files, Lock, me, _ => true));
        OwnerLock.Unlock(_files, Lock, other);
        Assert.True(File.Exists(LockFile));
        OwnerLock.Unlock(_files, Lock, me);
        Assert.False(File.Exists(LockFile));
    }

    [Fact]
    public void ALockWhoseFolderCantBeMade_IsUnwritable_NotHeld()
    {
        // 2026-10-29, the audit lock: a first audit makes .neon/audits through its lock, and a folder that couldn't be made read as "another holds it".
        Directory.CreateDirectory(Path.Combine(_dir, ".neon"));
        File.WriteAllText(Folder, "a file where the folder would go");

        Assert.Equal(LockTake.Unwritable, OwnerLock.TryLock(_files, Lock, new LockOwner(100, T0), _ => true));
    }

    [Fact]
    public void HeldElsewhere_NamesALiveOtherOwner_AndNotAStaleOne_OrThisProcess()
    {
        var other = new LockOwner(424242, T0, "OTHER-PC");
        Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_files, Lock, other, _ => false));
        var alive = OwnerLock.LiveOwner;
        try
        {
            OwnerLock.LiveOwner = owner => owner == other;
            Assert.True(OwnerLock.HeldElsewhere(_files, Lock, out var owner));
            Assert.Equal(other, owner);
            Assert.Equal(other, OwnerLock.ReadOwner(_files, Lock));

            OwnerLock.LiveOwner = _ => false;
            Assert.False(OwnerLock.HeldElsewhere(_files, Lock, out _));
        }
        finally
        {
            OwnerLock.LiveOwner = alive;
        }

        File.Delete(LockFile);
        Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_files, Lock, OwnerLock.Me, _ => true));
        Assert.False(OwnerLock.HeldElsewhere(_files, Lock, out _));   // this process's own
        OwnerLock.Unlock(_files, Lock, OwnerLock.Me);
        Assert.False(File.Exists(LockFile));
    }

    [Fact]
    public async Task TheLock_RacedFor_IsTakenByOne_AndLeavesNoTempFiles()
    {
        // 2026-10-19, the review: read-then-write let two instances starting together both take it.
        var owners = Enumerable.Range(1, 8).Select(i => new LockOwner(1000 + i, T0)).ToList();
        using var start = new ManualResetEventSlim();
        var takes = owners.Select(owner => Task.Run(() => { start.Wait(); return OwnerLock.TryLock(_files, Lock, owner, _ => true); })).ToList();
        start.Set();
        LockTake[] taken = await Task.WhenAll(takes);

        Assert.Single(taken, t => t == LockTake.Taken);
        Assert.All(taken, t => Assert.NotEqual(LockTake.Unwritable, t));
        Assert.Equal(["report.lock"], Directory.EnumerateFiles(Folder).Select(Path.GetFileName));

        File.WriteAllText(LockFile, "not a lock");   // garbage: no owner, taken over (a lock is never half written)
        Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_files, Lock, owners[0], _ => true));
    }

    [Fact]
    public async Task TheLock_IsNeverReadHalfWritten_WhileItIsRacedFor()
    {
        // 2026-10-20, the code review: created in place, a lock could be read empty or half written, which counted as held for a few seconds
        // and then as garbage a second taker removed. Published whole now, by a hard link (a move on Windows), so a reader finds it whole or
        // not at all, and the racers' temp files never outlive their takes.
        Directory.CreateDirectory(Folder);
        var torn = new List<string>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var stream = new FileStream(LockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    string text = new StreamReader(stream).ReadToEnd();
                    if (!text.StartsWith("pid: ", StringComparison.Ordinal) || !text.Contains("\nstarted: ", StringComparison.Ordinal) || !text.EndsWith('\n'))
                    {
                        torn.Add(text);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not there (between a take and an unlock), or held a moment on Windows: never a torn read.
                }
            }
        });

        for (int round = 0; round < 30; round++)
        {
            var owners = Enumerable.Range(1, 6).Select(i => new LockOwner(round * 10 + i, T0)).ToList();
            using var start = new ManualResetEventSlim();
            var takes = owners.Select(owner => Task.Run(() => { start.Wait(); return (owner, OwnerLock.TryLock(_files, Lock, owner, _ => true)); })).ToList();
            start.Set();
            var taken = await Task.WhenAll(takes);
            var holder = Assert.Single(taken, t => t.Item2 == LockTake.Taken).owner;
            OwnerLock.Unlock(_files, Lock, holder);
        }

        await stop.CancelAsync();
        await reader;
        Assert.Empty(torn);
        Assert.Empty(Directory.EnumerateFiles(Folder));   // every temp file gone, the last lock let go
    }

    /// <summary>
    /// What a publishing call's errno means (later on 2026-10-20, the code review: a volume with no hard links fell back to a racy move): EEXIST and
    /// the rest are not taken, the volume's "no such call" answers are unsupported.
    /// </summary>
    [Theory]
    [InlineData(0, (int)OwnerLock.PublishOutcome.Published)]
    [InlineData(17, (int)OwnerLock.PublishOutcome.NotTaken)]      // EEXIST: held
    [InlineData(13, (int)OwnerLock.PublishOutcome.NotTaken)]      // EACCES
    [InlineData(2, (int)OwnerLock.PublishOutcome.NotTaken)]       // ENOENT
    [InlineData(1, (int)OwnerLock.PublishOutcome.Unsupported)]    // EPERM (Linux's link on FAT)
    [InlineData(45, (int)OwnerLock.PublishOutcome.Unsupported)]   // macOS's ENOTSUP (exFAT, FAT32's link)
    [InlineData(102, (int)OwnerLock.PublishOutcome.Unsupported)]  // macOS's EOPNOTSUPP
    [InlineData(95, (int)OwnerLock.PublishOutcome.Unsupported)]   // Linux's EOPNOTSUPP
    public void ThePublishsErrno_IsRead(int errno, int outcome) => Assert.Equal((OwnerLock.PublishOutcome)outcome, OwnerLock.PublishOutcomeOf(errno));

    /// <summary>A volume that takes neither a hard link nor an exclusive rename (exFAT): <see cref="LockTake.Unsupported"/>, no file left.</summary>
    [UnixFact]
    public void TheLock_OnAVolumeWithNeitherCall_IsUnsupported()
    {
        var link = OwnerLock.LinkCall;
        var rename = OwnerLock.RenameExclusiveCall;
        try
        {
            OwnerLock.LinkCall = (_, _) => 45;
            OwnerLock.RenameExclusiveCall = (_, _) => 45;
            Assert.Equal(LockTake.Unsupported, OwnerLock.TryLock(_files, Lock, new LockOwner(100, T0), _ => true));
            Assert.Empty(Directory.EnumerateFiles(Folder));
        }
        finally
        {
            OwnerLock.LinkCall = link;
            OwnerLock.RenameExclusiveCall = rename;
        }
    }

    /// <summary>The Mac's exclusive rename itself, on the temp volume: a free name taken (the temp file moved), a taken one left as it was (EEXIST).</summary>
    [MacFact]
    public void TheExclusiveRename_TakesAFreeName_AndNeverReplacesOne()
    {
        string a = Path.Combine(_dir, "a.tmp"), b = Path.Combine(_dir, "b.tmp"), target = Path.Combine(_dir, "x.lock");
        File.WriteAllText(a, "first");
        File.WriteAllText(b, "second");

        Assert.Equal(0, OwnerLock.RenameExclusive(a, target));
        Assert.False(File.Exists(a));
        Assert.Equal(17, OwnerLock.RenameExclusive(b, target));   // EEXIST
        Assert.Equal("first", File.ReadAllText(target));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void ALockWithNoStart_IsTakenOverAtOnce_WhenItsOwnerIsGone()
    {
        // Whole as written (no lock is read before its text is there), so a "pid: 12" alone is a lock its writer meant: its owner gone, it goes.
        Directory.CreateDirectory(Folder);
        File.WriteAllText(LockFile, "pid: 12");
        Assert.Equal(LockTake.Held, OwnerLock.TryLock(_files, Lock, new LockOwner(34, T0), _ => true));
        Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_files, Lock, new LockOwner(34, T0), _ => false));
        Assert.Equal(["report.lock"], Directory.EnumerateFiles(Folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AStaleLock_TakenOverByMany_IsTakenByOne_AndLeavesNoStrayFiles()
    {
        // 2026-10-19, the third review: the stale lock was read, then deleted, so one taker could delete another's fresh lock and both ran.
        var stale = new LockOwner(999, T0.AddHours(-1));
        Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_files, Lock, stale, _ => true));
        for (int round = 0; round < 20; round++)
        {
            var owners = Enumerable.Range(1, 8).Select(i => new LockOwner(round * 100 + i, T0)).ToList();
            using var start = new ManualResetEventSlim();
            // Only the stale owner is gone: every taker sees it as stale, and every other taker as alive.
            var takes = owners.Select(owner => Task.Run(() => { start.Wait(); return OwnerLock.TryLock(_files, Lock, owner, held => held != stale); })).ToList();
            start.Set();
            LockTake[] taken = await Task.WhenAll(takes);

            Assert.Single(taken, t => t == LockTake.Taken);
            Assert.Equal(["report.lock"], Directory.EnumerateFiles(Folder).Select(Path.GetFileName));
            File.WriteAllText(LockFile, "pid: 999\nstarted: " + stale.Started.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }
    }

    [Fact]
    public void OwnerAlive_ThisProcess_IsAlive_AndAGoneOne_IsNot()
    {
        Assert.True(OwnerLock.OwnerAlive(OwnerLock.Me));
        Assert.False(OwnerLock.OwnerAlive(OwnerLock.Me with { Started = OwnerLock.Me.Started.AddMinutes(-5) }));   // its id, reused
        Assert.False(OwnerLock.OwnerAlive(new LockOwner(int.MaxValue - 7, T0)));
        Assert.True(OwnerLock.OwnerAlive(new LockOwner(int.MaxValue - 7, T0, "OTHER-PC")));   // another machine's: never taken for gone
    }
}
