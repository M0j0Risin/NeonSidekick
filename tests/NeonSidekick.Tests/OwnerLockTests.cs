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
        using var pause = new ManualResetEventSlim();
        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (pause.IsSet)
                {
                    resume.Reset();
                    paused.Set();
                    resume.Wait();
                    continue;
                }

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

            // The reader paused, its handle closed, before the unlock (2026-10-30, review F47): on a volume without POSIX delete an open handle
            // keeps the unlocked file delete-pending, the next round's publish finds the name taken, and every racer reads held.
            paused.Reset();
            pause.Set();
            Assert.True(paused.Wait(TimeSpan.FromSeconds(30)));
            OwnerLock.Unlock(_files, Lock, holder);
            pause.Reset();
            resume.Set();
        }

        await stop.CancelAsync();
        resume.Set();
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
    [InlineData(17, (int)OwnerLock.PublishOutcome.Exists)]        // EEXIST: one there (2026-10-30, review F75)
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

    /// <summary>
    /// A publish refused because a lock stood there, that lock gone by the read, every time (2026-10-30, review F75): another taker's race, held,
    /// never "can't be written"; every publish failing with no lock there is still unwritable.
    /// </summary>
    [Fact]
    public void APublishRefusedByALockGoneByTheRead_IsHeld_NotUnwritable()
    {
        Directory.CreateDirectory(Folder);
        var publish = OwnerLock.PublishCall;
        try
        {
            OwnerLock.PublishCall = (_, _) => OwnerLock.PublishOutcome.Exists;
            Assert.Equal(LockTake.Held, OwnerLock.TryLock(_files, Lock, new LockOwner(100, T0), _ => true));
            OwnerLock.PublishCall = (_, _) => OwnerLock.PublishOutcome.NotTaken;
            Assert.Equal(LockTake.Unwritable, OwnerLock.TryLock(_files, Lock, new LockOwner(100, T0), _ => true));
        }
        finally
        {
            OwnerLock.PublishCall = publish;
        }
    }

    /// <summary>A lock past 4 KB, or whose host or machine isn't a name, is unreadable: held, never taken over as "not a lock" (2026-10-30, review F85).</summary>
    [Theory]
    [InlineData("big")]
    [InlineData("host")]
    [InlineData("machine")]
    public void AnOversizedOrMalformedLock_IsHeld_AndLeftAsItIs(string kind)
    {
        Directory.CreateDirectory(Folder);
        const string head = "pid: 12\nstarted: 2026-10-29T09:00:00.0000000+00:00\n";
        string text = kind switch
        {
            "big" => head + new string('x', 5000) + "\n",
            "host" => head + "host: evil\u001b[2Jhost\n",
            _ => head + "machine: a b\n",
        };
        File.WriteAllText(LockFile, text);

        Assert.Equal(LockTake.Held, OwnerLock.TryLock(_files, Lock, new LockOwner(34, T0), _ => false));
        Assert.True(OwnerLock.HeldElsewhere(_files, Lock, out var owner));
        Assert.Null(owner);
        Assert.Equal(text, File.ReadAllText(LockFile));
    }

    [Fact]
    public void AHostWithLettersBeyondAscii_IsAName() => Assert.True(OwnerLock.IsName("Café-Mac.local"));

    /// <summary>
    /// A take-over guard names its owner (2026-10-30, review F38): a live one of this machine is never removed for its age; one that can't be judged
    /// here (another machine's) goes once minutes old.
    /// </summary>
    [Fact]
    public void ALiveTakersGuard_IsNeverRemovedForItsAge_AndAForeignOne_GoesAfterTheMargin()
    {
        var stale = new LockOwner(77, T0);
        var live = new LockOwner(88, T0);
        Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_files, Lock, stale, _ => true));
        string guard = LockFile + ".takeover";

        File.WriteAllText(guard, OwnerLock.TextOf(live));
        File.SetLastWriteTimeUtc(guard, DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(LockTake.Held, OwnerLock.TryLock(_files, Lock, new LockOwner(99, T0), owner => owner == live));
        Assert.True(File.Exists(guard));

        File.WriteAllText(guard, OwnerLock.TextOf(live with { Host = "OTHER-PC" }));
        File.SetLastWriteTimeUtc(guard, DateTime.UtcNow.AddMinutes(-10));
        Assert.Equal(LockTake.Held, OwnerLock.TryLock(_files, Lock, new LockOwner(99, T0), owner => owner == live));
        Assert.False(File.Exists(guard));
        Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_files, Lock, new LockOwner(99, T0), owner => owner == live));
    }

    /// <summary>
    /// The machine id decides foreign (2026-10-30, review F41: a Mac's host name changes with the network): a lock with this machine's id under
    /// another host name is this machine's, one with another id is foreign, and one with no id falls back to the host.
    /// </summary>
    [Fact]
    public void TheMachineId_DecidesForeign_OverTheHostName()
    {
        const string id = "11111111-2222-3333-4444-555555555555";
        using (OwnerLock.MachineIdForTests(id))
        {
            Assert.Equal(id, OwnerLock.Me.Machine);
            Assert.False(new LockOwner(5, T0, "OLD-NAME", id).Foreign);
            Assert.True(new LockOwner(5, T0, Environment.MachineName, "99999999-2222-3333-4444-555555555555").Foreign);
            Assert.True(new LockOwner(5, T0, "OTHER-PC").Foreign);

            // This process's own lock reads back as itself, machine id and all, so it never locks itself out.
            Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_files, Lock, OwnerLock.Me, _ => true));
            Assert.Equal(OwnerLock.Me, OwnerLock.ReadOwner(_files, Lock));
            Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_files, Lock, OwnerLock.Me, _ => true));
            OwnerLock.Unlock(_files, Lock, OwnerLock.Me);
            Assert.False(File.Exists(LockFile));
        }

        Assert.Null(OwnerLock.Me.Machine);
    }

    /// <summary>The home's machine id is made once and read back the same.</summary>
    [Fact]
    public void TheMachineIdFile_IsMadeOnce()
    {
        string home = Path.Combine(_dir, "home");
        string? first = OwnerLock.ReadOrCreateMachineId(home);
        Assert.NotNull(first);
        Assert.Equal(first, OwnerLock.ReadOrCreateMachineId(home));
        Assert.Equal(first, File.ReadAllText(Path.Combine(home, OwnerLock.MachineIdFileName)).Trim());
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
