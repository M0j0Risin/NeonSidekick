using NeonSidekick.Files;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

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
}
