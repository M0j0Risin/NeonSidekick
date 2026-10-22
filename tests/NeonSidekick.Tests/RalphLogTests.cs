using NeonSidekick.Files;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

// The fixtures' plan writes go through the app's pass since 2026-10-22 (the write guard refuses a model write into .neon; these stand
// for the app's own writes and the user's edits, which the judge still puts back).

/// <summary>
/// The Ralph log beside the plan (2026-10-18, <see cref="RalphLog"/>): written and read back, tolerant of hand edits, the check's output cut,
/// scrubbed and redacted, the learned facts merged and capped, the notes a pass gets, the pass count, and the lock.
/// </summary>
public sealed class RalphLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly WorkingDirectory _files;
    private const string Plan = ".neon/plans/guide.md";
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 0, 0, TimeSpan.FromHours(1));
    private static readonly RalphLogHead Head = new(Plan, "dotnet build", "dotnet test \"all\"", false, "pwsh", T0);

    public RalphLogTests()
    {
        _files = new WorkingDirectory(() => _dir, new ManualTimeProvider());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static RalphLogEntry Entry(int pass, int step, string text, string outcome = "done", string? tail = null) =>
        new(pass, step, text, outcome, "wrote " + text, ["built: ok"], tail is null ? "passed" : "failed (exit 1)", tail, [], ["docs/a.md"], "pass: fine", null, T0.AddMinutes(pass), TimeSpan.FromSeconds(42), 1200);

    [Fact]
    public void AnEntry_RoundTrips_WithTheHead_AndTheLearnedFacts()
    {
        Assert.Null(RalphLog.Append(_files, Plan, Head, Entry(1, 1, "Write the outline"), ["`dotnet build` takes 20 s", "the docs live in docs/"]));
        Assert.Null(RalphLog.Append(_files, Plan, Head, Entry(2, 2, "Write the body", "check failed", "line 1\nerror CS0001: x"), ["the docs live in docs/"]));

        var log = RalphLog.Read(_files, Plan)!;

        Assert.Equal(Head, log.Head);
        Assert.Equal(["`dotnet build` takes 20 s", "the docs live in docs/"], log.Learned);
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(Entry(1, 1, "Write the outline") with { Verified = log.Entries[0].Verified, Changed = log.Entries[0].Changed, Flags = log.Entries[0].Flags }, log.Entries[0]);
        Assert.Equal(["built: ok"], log.Entries[0].Verified);
        Assert.Equal(["docs/a.md"], log.Entries[0].Changed);
        Assert.Equal("line 1\nerror CS0001: x", log.Entries[1].Tail);
        Assert.Equal(2, RalphLog.PassCount(log));
        Assert.Equal(".neon/plans/guide/ralph.md", RalphLog.PathFor(Plan));
        Assert.True(File.Exists(Path.Combine(_dir, ".neon", "plans", "guide", "ralph.md")));
    }

    [Fact]
    public void ALongLog_KeepsItsNewestPasses_WithinTheCap()
    {
        // 2026-10-19, the second review: the log only grew, was read cut at 200,000 characters (its newest passes lost), and then every write was refused.
        string tail = string.Join('\n', Enumerable.Range(1, RalphLog.TailLines).Select(i => new string('x', 100) + i));
        var entries = Enumerable.Range(1, 120).Select(p => Entry(p, 1, "Write the body", "check failed", tail)).ToList();
        string big = RalphLog.Render(new RalphLogDocument(Head, ["a fact"], entries));
        Assert.True(big.Length > WorkingDirectory.MaxWriteChars);
        Directory.CreateDirectory(Path.Combine(_dir, ".neon", "plans", "guide"));
        File.WriteAllText(Path.Combine(_dir, ".neon", "plans", "guide", "ralph.md"), big);   // as an older build left it

        Assert.Equal(120, RalphLog.PassCount(RalphLog.Read(_files, Plan)));   // read whole
        Assert.Null(RalphLog.Append(_files, Plan, Head, Entry(121, 2, "Write the end"), ["another fact"]));

        var log = RalphLog.Read(_files, Plan)!;
        Assert.True(File.ReadAllText(Path.Combine(_dir, ".neon", "plans", "guide", "ralph.md")).Length <= RalphLog.MaxLogChars);
        Assert.Equal(121, RalphLog.PassCount(log));
        Assert.Equal(121, log.Entries[^1].Pass);
        Assert.True(log.Entries.Count < 121);
        Assert.Equal(["a fact", "another fact"], log.Learned);
        Assert.Equal(Head, log.Head);
    }

    [Fact]
    public void ALogThatCannotBeRead_IsNeverWrittenOver()
    {
        string file = Path.Combine(_dir, ".neon", "plans", "guide", "ralph.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        byte[] binary = [0, 1, 2, 0, 3, 0, 0, 4];
        File.WriteAllBytes(file, binary);

        Assert.Null(RalphLog.Read(_files, Plan));
        Assert.NotNull(RalphLog.Append(_files, Plan, Head, Entry(1, 1, "x"), []));
        Assert.NotNull(RalphLog.SetHead(_files, Plan, Head));
        Assert.Equal(binary, File.ReadAllBytes(file));
    }

    [Fact]
    public void ACheckTail_IsCut_Scrubbed_AndRedacted()
    {
        string tail = string.Join('\n', Enumerable.Range(1, 60).Select(i => "line " + i + (i == 59 ? "\u0007" : ""))) + "\nconnect --password hunter2";

        RalphLog.Append(_files, Plan, Head, Entry(1, 1, "x", "check failed", tail), []);

        string kept = RalphLog.Read(_files, Plan)!.Entries[0].Tail!;
        Assert.Equal(RalphLog.TailLines, kept.Split('\n').Length);
        Assert.DoesNotContain("hunter2", kept);
        Assert.DoesNotContain("line 20\n", kept);
        Assert.Contains("�", kept);
    }

    [Fact]
    public void HandEdits_AreRead_AsFarAsTheyGo()
    {
        string text = "# Ralph log: mine\n\nSome words of my own.\n\n- plan: .neon/plans/guide.md\n- verify: none\n\n## Learned\n- a fact\n\n## Pass 3\n- outcome: done\n- a stray line with no colon\nprose\n\n## Pass x\n- step: 2 · Two\n";

        var log = RalphLog.Parse(text);

        Assert.Equal(".neon/plans/guide.md", log.Head!.Plan);
        Assert.Null(log.Head.Verify);
        Assert.Equal(["a fact"], log.Learned);
        Assert.Equal([3, 0], log.Entries.Select(e => e.Pass));
        Assert.Equal("done", log.Entries[0].Outcome);
        Assert.Equal((2, "Two"), (log.Entries[1].Step, log.Entries[1].StepText));
        Assert.Equal(RalphLogDocument.Empty.Entries, RalphLog.Parse("nothing here").Entries);
        Assert.Null(RalphLog.Parse("nothing here").Head);
    }

    [Fact]
    public void LearnedFacts_AreRedacted_KeptOnce_AndTheNewestWinPastTheCap()
    {
        var merged = RalphLog.MergeLearned(["old fact", "keep me"], ["Keep Me", "password=abc123secret"]);
        Assert.Equal(["old fact", "Keep Me", "password=abc123secret".Replace("abc123secret", NeonSidekick.Llm.Tools.ToolRedaction.Placeholder, StringComparison.Ordinal)], merged);

        var many = RalphLog.MergeLearned([], Enumerable.Range(0, 100).Select(i => $"fact {i:D3} " + new string('x', 40)).ToList());
        Assert.True(many.Sum(f => f.Length + 3) <= RalphLog.LearnedChars);
        Assert.EndsWith("fact 099 " + new string('x', 40), many[^1]);
        Assert.Equal("- a\n- b", RalphLog.LearnedText(new RalphLogDocument(null, ["a", "b"], [])));
        Assert.Null(RalphLog.LearnedText(RalphLogDocument.Empty));
    }

    [Fact]
    public void RecentNotes_PutThisStepsEntriesFirst_ThenTheLatest_WithinTheCap()
    {
        var log = new RalphLogDocument(Head, [], [Entry(1, 1, "One"), Entry(2, 2, "Two", "check failed", "boom"), Entry(3, 1, "One", "partial"), Entry(4, 3, "Three")]);

        string notes = RalphLog.RecentNotes(log, "One")!;

        Assert.Equal(
            "- pass 3 (step 1): partial; wrote One; check passed; review pass: fine\n" +
            "- pass 1 (step 1): done; wrote One; check passed; review pass: fine\n" +
            "- pass 4 (step 3): done; wrote Three; check passed; review pass: fine", notes);
        Assert.Null(RalphLog.RecentNotes(RalphLogDocument.Empty, "One"));
        Assert.Equal(["docs/a.md"], RalphLog.ChangedBy(log, "One"));
    }

    [Fact]
    public void TheLock_RefusesAnotherLiveProcess_TakesOverAStaleOne_AndIsLetGo()
    {
        var me = new RalphLog.LockOwner(100, T0);
        var other = new RalphLog.LockOwner(200, T0.AddHours(-1));

        Assert.True(RalphLog.TryLock(_files, Plan, other, _ => true));
        Assert.False(RalphLog.TryLock(_files, Plan, me, _ => true));          // the other is alive
        Assert.True(RalphLog.TryLock(_files, Plan, me, owner => owner != other));   // it is gone: taken over
        Assert.True(RalphLog.TryLock(_files, Plan, me, _ => true));           // mine already
        RalphLog.Unlock(_files, Plan, other);                                 // not its to let go
        Assert.True(File.Exists(Path.Combine(_dir, ".neon", "plans", "guide", RalphLog.LockName)));
        RalphLog.Unlock(_files, Plan, me);
        Assert.False(File.Exists(Path.Combine(_dir, ".neon", "plans", "guide", RalphLog.LockName)));
    }

    [Fact]
    public async Task TheLock_RacedFor_IsTakenByOne_AndLeavesNoTempFiles()
    {
        // 2026-10-19, the review: read-then-write let two instances starting together both take it.
        var owners = Enumerable.Range(1, 8).Select(i => new RalphLog.LockOwner(1000 + i, T0)).ToList();
        using var start = new ManualResetEventSlim();
        var takes = owners.Select(owner => Task.Run(() => { start.Wait(); return RalphLog.TryLock(_files, Plan, owner, _ => true); })).ToList();
        start.Set();
        bool[] taken = await Task.WhenAll(takes);

        Assert.Single(taken, t => t);
        string folder = Path.Combine(_dir, ".neon", "plans", "guide");
        Assert.Equal([RalphLog.LockName], Directory.EnumerateFiles(folder).Select(Path.GetFileName));

        File.WriteAllText(Path.Combine(folder, RalphLog.LockName), "not a lock");   // garbage: no owner, taken over (a lock is never half written)
        Assert.True(RalphLog.TryLock(_files, Plan, owners[0], _ => true));
    }

    [Fact]
    public async Task TheLock_IsNeverReadHalfWritten_WhileItIsRacedFor()
    {
        // 2026-10-20, the code review: created in place, a lock could be read empty or half written, which counted as held for a few seconds
        // and then as garbage a second taker removed. Published whole now, by a hard link (a move on Windows), so a reader finds it whole or
        // not at all, and the racers' temp files never outlive their takes.
        string folder = Path.Combine(_dir, ".neon", "plans", "guide");
        string lockFile = Path.Combine(folder, RalphLog.LockName);
        Directory.CreateDirectory(folder);
        var torn = new List<string>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var stream = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
            var owners = Enumerable.Range(1, 6).Select(i => new RalphLog.LockOwner(round * 10 + i, T0)).ToList();
            using var start = new ManualResetEventSlim();
            var takes = owners.Select(owner => Task.Run(() => { start.Wait(); return (owner, RalphLog.TryLock(_files, Plan, owner, _ => true)); })).ToList();
            start.Set();
            var taken = await Task.WhenAll(takes);
            var holder = Assert.Single(taken, t => t.Item2).owner;
            RalphLog.Unlock(_files, Plan, holder);
        }

        await stop.CancelAsync();
        await reader;
        Assert.Empty(torn);
        Assert.Empty(Directory.EnumerateFiles(folder));   // every temp file gone, the last lock let go
    }

    /// <summary>
    /// What a publishing call's errno means (later on 2026-10-20, the code review: a volume with no hard links fell back to a racy move): EEXIST and
    /// the rest are not taken, the volume's "no such call" answers are unsupported.
    /// </summary>
    [Theory]
    [InlineData(0, (int)RalphLog.PublishOutcome.Published)]
    [InlineData(17, (int)RalphLog.PublishOutcome.NotTaken)]      // EEXIST: held
    [InlineData(13, (int)RalphLog.PublishOutcome.NotTaken)]      // EACCES
    [InlineData(2, (int)RalphLog.PublishOutcome.NotTaken)]       // ENOENT
    [InlineData(1, (int)RalphLog.PublishOutcome.Unsupported)]    // EPERM (Linux's link on FAT)
    [InlineData(45, (int)RalphLog.PublishOutcome.Unsupported)]   // macOS's ENOTSUP (exFAT, FAT32's link)
    [InlineData(102, (int)RalphLog.PublishOutcome.Unsupported)]  // macOS's EOPNOTSUPP
    [InlineData(95, (int)RalphLog.PublishOutcome.Unsupported)]   // Linux's EOPNOTSUPP
    public void ThePublishsErrno_IsRead(int errno, int outcome) => Assert.Equal((RalphLog.PublishOutcome)outcome, RalphLog.PublishOutcomeOf(errno));

    /// <summary>
    /// A volume with no hard links (later on 2026-10-20, the code review): on a Mac the exclusive rename takes the lock (FAT32 measured so), and one
    /// that takes neither refuses the start with its own sentence, never a move two instances could both win and never "another is running".
    /// </summary>
    [UnixFact]
    public void TheLock_OnAVolumeWithoutHardLinks_TakesTheExclusiveRename_OrIsRefused()
    {
        var link = RalphLog.LinkCall;
        var rename = RalphLog.RenameExclusiveCall;
        string lockFile = Path.Combine(_dir, ".neon", "plans", "guide", RalphLog.LockName);
        try
        {
            RalphLog.LinkCall = (_, _) => 45;   // ENOTSUP, as FAT32 and exFAT answer
            if (OperatingSystem.IsMacOS())
            {
                Assert.True(RalphLog.TryLock(_files, Plan, new RalphLog.LockOwner(100, T0), _ => true, out string? none));   // the real renamex_np
                Assert.Null(none);
                Assert.False(RalphLog.TryLock(_files, Plan, new RalphLog.LockOwner(200, T0), _ => true, out none));          // held: EEXIST
                Assert.Null(none);
                Assert.Equal(RalphText.LockedError(Plan), RalphLog.LockError(_files, Plan));
                RalphLog.Unlock(_files, Plan, new RalphLog.LockOwner(100, T0));
            }

            RalphLog.RenameExclusiveCall = (_, _) => 45;   // exFAT: neither
            Assert.False(RalphLog.TryLock(_files, Plan, new RalphLog.LockOwner(100, T0), _ => true, out string? refusal));
            Assert.Equal(RalphText.LockUnsupportedError(Plan, RalphLog.LockPathFor(Plan)), refusal);
            Assert.False(File.Exists(lockFile));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(lockFile)!));   // the temp file gone too
            Assert.Equal(
                "A Ralph loop can't run .neon/plans/guide.md here: its folder's drive can't create .neon/plans/guide/ralph.lock safely (no hard links or exclusive renames, as on exFAT); move the project to the Mac's own disk",
                refusal);

            // A stale lock there already (an older build moved it in): its take-over guard can't be published either, the same refusal.
            File.WriteAllText(lockFile, "pid: 12\n");
            Assert.False(RalphLog.TryLock(_files, Plan, new RalphLog.LockOwner(100, T0), _ => false, out refusal));
            Assert.Equal(RalphText.LockUnsupportedError(Plan, RalphLog.LockPathFor(Plan)), refusal);
        }
        finally
        {
            RalphLog.LinkCall = link;
            RalphLog.RenameExclusiveCall = rename;
        }
    }

    /// <summary>The Mac's exclusive rename itself, on the temp volume: a free name taken (the temp file moved), a taken one left as it was (EEXIST).</summary>
    [MacFact]
    public void TheExclusiveRename_TakesAFreeName_AndNeverReplacesOne()
    {
        Directory.CreateDirectory(_dir);
        string a = Path.Combine(_dir, "a.tmp"), b = Path.Combine(_dir, "b.tmp"), target = Path.Combine(_dir, "x.lock");
        File.WriteAllText(a, "first");
        File.WriteAllText(b, "second");

        Assert.Equal(0, RalphLog.RenameExclusive(a, target));
        Assert.False(File.Exists(a));
        Assert.Equal(17, RalphLog.RenameExclusive(b, target));   // EEXIST
        Assert.Equal("first", File.ReadAllText(target));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void ALockWithNoStart_IsTakenOverAtOnce_WhenItsOwnerIsGone()
    {
        // Whole as written (no lock is read before its text is there), so a "pid: 12" alone is a lock its writer meant: its owner gone, it goes.
        string folder = Path.Combine(_dir, ".neon", "plans", "guide");
        string lockFile = Path.Combine(folder, RalphLog.LockName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(lockFile, "pid: 12");
        Assert.False(RalphLog.TryLock(_files, Plan, new RalphLog.LockOwner(34, T0), _ => true));   // alive: held
        Assert.True(RalphLog.TryLock(_files, Plan, new RalphLog.LockOwner(34, T0), _ => false));
        Assert.Equal([RalphLog.LockName], Directory.EnumerateFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AStaleLock_TakenOverByMany_IsTakenByOne_AndLeavesNoStrayFiles()
    {
        // 2026-10-19, the third review: the stale lock was read, then deleted, so one taker could delete another's fresh lock and both ran.
        var stale = new RalphLog.LockOwner(999, T0.AddHours(-1));
        Assert.True(RalphLog.TryLock(_files, Plan, stale, _ => true));
        for (int round = 0; round < 20; round++)
        {
            var owners = Enumerable.Range(1, 8).Select(i => new RalphLog.LockOwner(round * 100 + i, T0)).ToList();
            using var start = new ManualResetEventSlim();
            // Only the stale owner is gone: every taker sees it as stale, and every other taker as alive.
            var takes = owners.Select(owner => Task.Run(() => { start.Wait(); return RalphLog.TryLock(_files, Plan, owner, held => held != stale); })).ToList();
            start.Set();
            bool[] taken = await Task.WhenAll(takes);

            Assert.Single(taken, t => t);
            string folder = Path.Combine(_dir, ".neon", "plans", "guide");
            Assert.Equal([RalphLog.LockName], Directory.EnumerateFiles(folder).Select(Path.GetFileName));
            File.WriteAllText(Path.Combine(folder, RalphLog.LockName), "pid: 999\nstarted: " + stale.Started.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }
    }

    [Fact]
    public void OwnerAlive_ThisProcess_IsAlive_AndAGoneOne_IsNot()
    {
        Assert.True(RalphLog.OwnerAlive(RalphLog.Me));
        Assert.False(RalphLog.OwnerAlive(RalphLog.Me with { Started = RalphLog.Me.Started.AddMinutes(-5) }));   // its id, reused
        Assert.False(RalphLog.OwnerAlive(new RalphLog.LockOwner(int.MaxValue - 7, T0)));
    }

    [Fact]
    public void TheInPassMark_IsWritten_ReadBack_AndCleared()
    {
        // 2026-10-19, the third review: a run that ended mid-pass left the model's tick standing unchecked; the mark names the step to put back.
        Assert.Null(RalphLog.SetHead(_files, Plan, Head));
        var step = new PlanStep(1, 7, 0, "Write the outline", false, "outline.md lists every chapter", [], [], []);
        var lines = new[] { new PlanStep(0, 6, 0, "Gather notes", true, null, [], [], []), step };

        Assert.Null(RalphLog.MarkInPass(_files, Plan, step, lines, headed: true));
        var marked = RalphLog.Read(_files, Plan)!.Head!;
        Assert.Equal(7, marked.InPassLine);
        Assert.Equal("Write the outline", marked.InPassStep);
        Assert.True(RalphLog.InPassOf(marked) is { Line: 7, Text: "Write the outline", Ticked: false, IsLeaf: true });
        // The task lines as the pass began (2026-10-19, the fourth review), read back; their Done when: and the scope too (the fifth).
        Assert.Equal([(6, true, "Gather notes", (string?)null), (7, false, "Write the outline", "outline.md lists every chapter")],
            marked.InPassLines!.Select(l => (l.Line, l.Ticked, l.Text, l.DoneWhen)));
        Assert.True(marked.InPassHeaded);
        Assert.True(RalphLog.InPass(marked));

        Assert.Null(RalphLog.ClearInPass(_files, Plan));
        var cleared = RalphLog.Read(_files, Plan)!.Head!;
        Assert.Equal(Head, cleared);
        Assert.Null(RalphLog.InPassOf(cleared));
    }

    [Fact]
    public void FinishStepsNotes_AreKept_Redacted_AndHandedToTheNextPassOnTheStep()
    {
        // 2026-10-19, the third review: finish_step's notes were parsed and dropped.
        var entry = Entry(1, 1, "Write the body", "partial") with { Notes = "the migration is half applied; run X before Y (password=hunter2)" };
        Assert.Null(RalphLog.Append(_files, Plan, Head, entry, []));
        Assert.Null(RalphLog.Append(_files, Plan, Head, Entry(2, 2, "Write the index") with { Notes = new string('n', 2000) }, []));

        var log = RalphLog.Read(_files, Plan)!;
        Assert.Contains("the migration is half applied", log.Entries[0].Notes);
        Assert.DoesNotContain("hunter2", log.Entries[0].Notes);
        string notes = RalphLog.RecentNotes(log, "Write the body")!;
        Assert.Contains("notes for the next pass: the migration is half applied", notes);
        Assert.Contains("pass 2", notes);   // the latest too, its long note cut so both fit
    }

    [Fact]
    public void TheCheckpointsAnswer_IsWrittenIntoItsPass_AndHandedOn()
    {
        // 2026-10-19, the fourth review: the entry's checkpoint was never set, so a Redo's note was lost at a pause, a crash or a resume.
        Assert.Null(RalphLog.Append(_files, Plan, Head, Entry(1, 1, "Write the body"), []));
        Assert.Null(RalphLog.Append(_files, Plan, Head, Entry(2, 2, "Write the index"), []));

        Assert.Null(RalphLog.SetCheckpoint(_files, Plan, 1, "redo: use the existing parser (password=hunter2)"));
        Assert.Null(RalphLog.SetCheckpoint(_files, Plan, 9, "keep"));   // no such pass: nothing

        var log = RalphLog.Read(_files, Plan)!;
        Assert.StartsWith("redo: use the existing parser", log.Entries[0].Checkpoint);
        Assert.DoesNotContain("hunter2", log.Entries[0].Checkpoint);
        Assert.Null(log.Entries[1].Checkpoint);
        Assert.Contains("at the checkpoint the user chose redo: use the existing parser", RalphLog.RecentNotes(log, "Write the body"));
    }

    [Fact]
    public void APlanLockedByAnotherLiveProcess_IsNeverWritten_ThisProcesssOwnOrAStaleOneIs()
    {
        // 2026-10-19, the fourth review: the lock was read only as a loop started, so a second instance's /plan open, revision or approve wrote
        // the plan the first one's loop was running.
        Assert.Equal(FileOutcome.Ok, WriteGuard.AsApp(() => _files.WriteText(Plan, PlanDocument.Render(new PlanHeader(PlanStatus.Incomplete, 1, T0, T0, "Guide"), "Guide", "## Steps\n- [ ] One\n"), overwrite: true)).Outcome);
        var other = new RalphLog.LockOwner(424242, T0);
        Assert.True(RalphLog.TryLock(_files, Plan, other, _ => false));
        var alive = RalphLog.LiveOwner;
        try
        {
            RalphLog.LiveOwner = owner => owner == other;
            Assert.Equal(RalphText.LockedError(Plan), RalphLog.HeldElsewhere(_files, Plan));
            Assert.Equal(RalphText.LockedError(Plan), PlanFiles.MarkFile(_files, Plan, PlanStatus.Draft, T0, "Guide"));
            Assert.Equal(PlanStatus.Incomplete, PlanDocument.TryParse(File.ReadAllText(Path.Combine(_dir, ".neon", "plans", "guide.md")))!.Status);

            RalphLog.LiveOwner = _ => false;   // its owner gone: stale
            Assert.Null(RalphLog.HeldElsewhere(_files, Plan));
            Assert.Null(PlanFiles.MarkFile(_files, Plan, PlanStatus.Draft, T0, "Guide"));
        }
        finally
        {
            RalphLog.LiveOwner = alive;
        }

        File.Delete(Path.Combine(_dir, ".neon", "plans", "guide", RalphLog.LockName));
        Assert.True(RalphLog.TryLock(_files, Plan, RalphLog.Me, _ => true));
        Assert.Null(RalphLog.HeldElsewhere(_files, Plan));   // this process's own
        RalphLog.Unlock(_files, Plan, RalphLog.Me);
    }

    [Fact]
    public void AHugePass_IsCut_SoTheLogStillWrites()
    {
        // 2026-10-20, a review: finish_step's fields and a check's lines had no cap, Bounded keeps the newest pass whole, and one long pass made every
        // write of the log refused, the in-pass mark with it.
        string tail = string.Join('\n', Enumerable.Range(1, RalphLog.TailLines).Select(i => new string('x', 8000) + i));
        var huge = Entry(1, 1, "Write the body", "check failed", tail) with
        {
            Summary = new string('s', 300_000),
            Verified = Enumerable.Range(1, 500).Select(i => new string('v', 1000) + i).ToList(),
            Notes = new string('n', 300_000),
            Changed = Enumerable.Range(1, 5000).Select(i => "src/f" + i + ".cs").ToList(),
        };

        Assert.Null(RalphLog.Append(_files, Plan, Head, huge, []));

        var kept = RalphLog.Read(_files, Plan)!.Entries.Single();
        Assert.True(kept.Summary.Length <= RalphLog.EntryTextChars);
        Assert.Equal(RalphLog.VerifiedLines, kept.Verified.Count);
        Assert.All(kept.Verified, v => Assert.True(v.Length <= RalphLog.VerifiedChars));
        Assert.True(kept.Notes!.Length <= RalphLog.EntryTextChars);
        Assert.True(kept.Tail!.Length <= RalphLog.TailChars);
        Assert.EndsWith("x40", kept.Tail);   // the end is what is kept
        Assert.Equal(RalphLog.ChangedKept + 1, kept.Changed.Count);
        Assert.Equal(RalphText.ChangedMore(5000 - RalphLog.ChangedKept), kept.Changed[^1]);
    }

    [Fact]
    public void StillHeld_TakesBackALockThatWent_AndIsFalseWhileAnotherLiveProcessHoldsIt()
    {
        // 2026-10-20, a review: the lock was read only as the loop began, so a pass that deleted it let a second instance run the same plan.
        string lockFile = Path.Combine(_dir, ".neon", "plans", "guide", RalphLog.LockName);
        Assert.True(RalphLog.TryLock(_files, Plan, RalphLog.Me, _ => true));
        Assert.True(RalphLog.StillHeld(_files, Plan, _ => true));

        File.Delete(lockFile);
        Assert.True(RalphLog.StillHeld(_files, Plan, _ => true));
        Assert.True(File.Exists(lockFile));

        File.Delete(lockFile);
        Assert.True(RalphLog.TryLock(_files, Plan, new RalphLog.LockOwner(4242, T0), _ => true));
        Assert.False(RalphLog.StillHeld(_files, Plan, _ => true));
        Assert.Equal("🔁 Ralph loop stopped: another NeonSidekick took over .neon/plans/guide.md (1 of 3 steps done); it runs the plan now.",
            RalphText.StopNotice(RalphStop.Locked, Plan, 1, 3));
    }

    [Fact]
    public void ALockAnotherMachineWrote_IsNeverTakenForStale_AndItsErrorNamesTheMachine()
    {
        // 2026-10-20, a review: a plan in a shared folder, locked by a pid on another PC, read here as a gone process and taken over.
        var foreign = new RalphLog.LockOwner(int.MaxValue - 7, T0, "OTHER-PC");
        Assert.True(foreign.Foreign);
        Assert.True(RalphLog.OwnerAlive(foreign));
        Assert.True(RalphLog.TryLock(_files, Plan, foreign, _ => true));

        Assert.False(RalphLog.TryLock(_files, Plan, new RalphLog.LockOwner(100, T0), RalphLog.OwnerAlive));
        Assert.Equal(RalphText.LockedOnHostError(Plan, "OTHER-PC", RalphLog.LockPathFor(Plan)), RalphLog.LockError(_files, Plan));
        Assert.False(new RalphLog.LockOwner(1, T0).Foreign);   // a lock the previous build wrote: this machine's
        Assert.False(RalphLog.Me.Foreign);
    }

    [Fact]
    public void ALongSummary_NeverDropsTheNotes_AndTwinStepsReadOnlyTheirOwn()
    {
        // 2026-10-20, a review: a summary past the notes' room ended the list before its first line; and two steps of the same words read each
        // other's notes.
        var longOne = Entry(1, 2, "Run the migrations") with { Summary = new string('s', 1800), Checkpoint = "redo: use the old schema" };
        string notes = RalphLog.RecentNotes(new RalphLogDocument(Head, [], [longOne]), "Run the migrations")!;
        Assert.StartsWith("- pass 1 (step 2): done; sss", notes);
        Assert.True(notes.Length <= RalphLog.NotesChars);
        Assert.Contains("redo: use the old schema", notes);

        var log = new RalphLogDocument(Head, [], [Entry(1, 2, "Run the migrations"), Entry(2, 5, "Run the migrations"), Entry(3, 7, "Other")]);
        string twin = RalphLog.RecentNotes(log, "Run the migrations", 5)!;
        Assert.Contains("pass 2 (step 5)", twin);
        Assert.DoesNotContain("pass 1 ", twin);
        Assert.Contains("pass 1 (step 2)", RalphLog.RecentNotes(log, "Run the migrations")!);
    }

    [Fact]
    public void SetCheckpoint_SaysWhetherTheEntryHoldsTheAnswer()
    {
        // 2026-10-20, a review: a Redo's note went to the next pass from the loop and from this entry both; it now goes from the entry when written.
        Assert.Null(RalphLog.Append(_files, Plan, Head, Entry(1, 1, "One"), []));
        Assert.Null(RalphLog.SetCheckpoint(_files, Plan, 1, "redo: shorter", out bool written));
        Assert.True(written);
        Assert.Null(RalphLog.SetCheckpoint(_files, Plan, 9, "keep", out written));
        Assert.False(written);
    }

    [Theory]
    [InlineData("by hand: works", "; the user checked it by hand: it works")]
    [InlineData("by hand: redo: the frames flicker", "; the user checked it by hand and wants it redone: the frames flicker")]
    [InlineData("by hand: stopped", "; it waited for the user's check")]
    [InlineData("needs you", "; it waited for the user's check")]
    [InlineData("keep", "; at the checkpoint the user chose keep")]
    public void RecentNotes_WordTheByHandAnswer_InItsOwnWords(string checkpoint, string said)
    {
        // 2026-10-21: a by-hand answer read as "at the checkpoint the user chose …", and nobody's answer as a choice.
        var log = new RalphLogDocument(Head, [], [Entry(1, 1, "Animate it") with { Checkpoint = checkpoint }]);

        Assert.EndsWith(said, RalphLog.RecentNotes(log, "Animate it"));
    }

    [Fact]
    public void LastEntryAwaitingHand_IsTheLastEntry_OnThatStep_WaitingForTheUser()
    {
        var step = PlanSteps.Parse("## Steps\n- [ ] Animate it\n  Done when: By hand: it shows\n- [ ] Docs\n")[0];
        RalphLogDocument With(params RalphLogEntry[] entries) => new(Head, [], entries);

        Assert.NotNull(RalphLog.LastEntryAwaitingHand(With(Entry(1, 1, "Animate it") with { Checkpoint = RalphText.ByHandStoppedLog }), step));
        Assert.NotNull(RalphLog.LastEntryAwaitingHand(With(Entry(1, 1, "Animate it") with { Checkpoint = RalphText.NeedsYouLog }), step));
        Assert.Null(RalphLog.LastEntryAwaitingHand(With(Entry(1, 1, "Animate it") with { Checkpoint = RalphText.ByHandWorksLog }), step));
        Assert.Null(RalphLog.LastEntryAwaitingHand(With(Entry(1, 1, "Animate it")), step));   // an older build's entry asks nothing
        Assert.Null(RalphLog.LastEntryAwaitingHand(With(Entry(1, 1, "Animate it") with { Checkpoint = RalphText.NeedsYouLog }, Entry(2, 2, "Docs")), step));
        Assert.Null(RalphLog.LastEntryAwaitingHand(With(Entry(1, 2, "Docs") with { Checkpoint = RalphText.NeedsYouLog }), step));
        Assert.Null(RalphLog.LastEntryAwaitingHand(RalphLogDocument.Empty, step));
        Assert.Null(RalphLog.LastEntryAwaitingHand(With(Entry(1, 1, "Animate it") with { Checkpoint = RalphText.NeedsYouLog }), null));
    }
}
