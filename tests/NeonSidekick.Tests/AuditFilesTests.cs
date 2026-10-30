using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The code audit's reports on disk (2026-10-18): names, saves, the list newest first, the coverage sidecar, Discard.</summary>
public sealed class AuditFilesTests : IDisposable
{
    private readonly AuditSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private string Save(string label, DateTimeOffset updated, AuditStatus status = AuditStatus.Converged)
    {
        string path = AuditFiles.Choose(_box.Files, label, updated);
        var report = new AuditReport { Label = label, Header = new AuditHeader(status, "1 pass", "", AuditFocus.Dimensions, "", "model", 1, updated, updated) };
        Assert.Null(AuditFiles.Save(_box.Files, path, report, updated));
        return path;
    }

    [Fact]
    public void Choose_NamesByLabelAndDate_ThenNumbers()
    {
        var day = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(".neon/audits/src-web-2026-10-18.md", Save("src/Web", day));
        Assert.Equal(".neon/audits/src-web-2026-10-18-2.md", Save("src/Web", day));
        Assert.Equal(".neon/audits/tree-2026-10-18.md", AuditFiles.Choose(_box.Files, "tree", day));
    }

    [Fact]
    public void List_NewestFirstByUpdated_ThenWriteTime_ATieByName()
    {
        var older = Save("a", new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
        var newer = Save("b", new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        var tie = Save("c", new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        // A hand-made report with no header: its write time decides.
        string bare = Path.Combine(_box.Root, ".neon", "audits", "hand.md");
        File.WriteAllText(bare, "# Code audit: by hand\n\n## Findings\n");
        File.SetLastWriteTime(bare, new DateTime(2026, 10, 1));
        var listed = AuditFiles.List(_box.Files).Select(l => l.Path).ToList();
        Assert.Equal([newer, tie, older, ".neon/audits/hand.md"], listed);
        // A sidecar is never listed.
        AuditFiles.MergeCoverage(_box.Files, newer, ["src/a.cs"]);
        Assert.Equal(4, AuditFiles.List(_box.Files).Count);
    }

    [Fact]
    public void List_AnAuditingReportWithNoLiveLoop_ListsAsStopped()
    {
        string path = Save("x", DateTimeOffset.UtcNow, AuditStatus.Auditing);
        var listing = AuditFiles.List(_box.Files).Single();
        Assert.Equal("stopped", AuditText.StatusLabel(listing));
        Assert.Equal("auditing", AuditText.StatusLabel(AuditFiles.List(_box.Files, path).Single()));
    }

    [Fact]
    public void Resolve_TakesTheNameInItsForms()
    {
        string path = Save("x", DateTimeOffset.UtcNow);
        string name = Path.GetFileNameWithoutExtension(path);
        Assert.Equal(path, AuditFiles.Resolve(_box.Files, name));
        Assert.Equal(path, AuditFiles.Resolve(_box.Files, name + ".md"));
        Assert.Equal(path, AuditFiles.Resolve(_box.Files, "audits/" + name));
        Assert.Equal(path, AuditFiles.Resolve(_box.Files, path));
        Assert.Null(AuditFiles.Resolve(_box.Files, "nope"));
        Assert.Null(AuditFiles.Resolve(_box.Files, "../x"));
    }

    [Fact]
    public void Coverage_RoundTrips_GroupedByFolder_AndMergesAcrossRuns()
    {
        string path = Save("x", DateTimeOffset.UtcNow);
        AuditFiles.MergeCoverage(_box.Files, path, ["src/a.cs", "README.md"]);
        AuditFiles.MergeCoverage(_box.Files, path, ["src/b.cs", "src/a.cs"]);
        var read = AuditFiles.ReadCoverage(_box.Files, path);
        Assert.Equal(3, read.Count);
        Assert.Contains("src/b.cs", read);
        string text = File.ReadAllText(Path.Combine(_box.Root, ".neon", "audits", Path.GetFileNameWithoutExtension(path) + ".coverage.txt"));
        Assert.Equal("./\n  README.md\nsrc/\n  a.cs\n  b.cs\n", text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Coverage_FiveThousandPaths_FitUnderTheCap()
    {
        var paths = Enumerable.Range(0, 5000).Select(i => $"src/Area{i % 40}/Sub{i % 7}/File{i}.cs").ToList();
        string text = AuditFiles.RenderCoverage(paths);
        Assert.True(text.Length < WorkingDirectory.MaxWriteChars, text.Length.ToString());
        Assert.Equal(5000, AuditFiles.ParseCoverage(text).Count);
    }

    [Fact]
    public void Choose_PassesAName_WhoseSidecarWasLeftBehind()
    {
        // 2026-10-20, a review: a report deleted by hand left its coverage, and a new audit of the same name took it as its own.
        var day = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
        string first = Save("tree", day);
        AuditFiles.MergeCoverage(_box.Files, first, ["a.cs"]);
        File.Delete(_box.Full(first));

        Assert.Equal(".neon/audits/tree-2026-10-18-2.md", AuditFiles.Choose(_box.Files, "tree", day));
    }

    [WindowsFact]
    public void Coverage_ASidecarThatCannotBeRead_IsNeverWrittenOver()
    {
        // 2026-10-20, a review: a locked sidecar read as empty, and the merge wrote this call's files over every earlier run's.
        string path = Save("x", DateTimeOffset.UtcNow);
        AuditFiles.MergeCoverage(_box.Files, path, ["a.cs", "b.cs"]);
        string sidecar = _box.Full(AuditFiles.CoveragePath(path));
        using (new FileStream(sidecar, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.NotNull(AuditFiles.MergeCoverage(_box.Files, path, ["c.cs"]));
        }

        Assert.Equal(["a.cs", "b.cs"], AuditFiles.ReadCoverage(_box.Files, path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Discard_RemovesTheReportAndItsSidecar()
    {
        string path = Save("x", DateTimeOffset.UtcNow);
        AuditFiles.MergeCoverage(_box.Files, path, ["a.cs"]);
        Assert.Equal(FileOutcome.Ok, AuditFiles.Discard(_box.Files, path, out _).Outcome);
        Assert.False(AuditFiles.Exists(_box.Files, path));
        Assert.False(AuditFiles.Exists(_box.Files, AuditFiles.CoveragePath(path)));
    }

    // ── The lock (2026-10-29) ──

    private const int OtherPid = 424242;

    private void WriteLock(string report, int pid) =>
        _box.Write(AuditFiles.LockPathFor(report), "pid: " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\nstarted: 2026-10-29T09:00:00.0000000+00:00\n");

    /// <summary>Runs <paramref name="body"/> with <see cref="OtherPid"/> as another live NeonSidekick and every other owner but this one gone.</summary>
    private static void WithOtherAlive(Action body)
    {
        var alive = OwnerLock.LiveOwner;
        OwnerLock.LiveOwner = owner => owner.ProcessId == OtherPid || owner == OwnerLock.Me;
        try
        {
            body();
        }
        finally
        {
            OwnerLock.LiveOwner = alive;
        }
    }

    [Fact]
    public void Choose_SkipsANameAnotherLiveAppHasLocked_AndReusesAStaleOne() => WithOtherAlive(() =>
    {
        var day = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
        WriteLock(".neon/audits/tree-2026-10-29.md", OtherPid);
        Assert.Equal(".neon/audits/tree-2026-10-29-2.md", AuditFiles.Choose(_box.Files, "tree", day));

        WriteLock(".neon/audits/tree-2026-10-29.md", 77);   // its owner gone
        Assert.Equal(".neon/audits/tree-2026-10-29.md", AuditFiles.Choose(_box.Files, "tree", day));
    });

    [Fact]
    public void Discard_RemovesOurLockAndAStaleOne_ButNeverALiveOthers() => WithOtherAlive(() =>
    {
        string ours = Save("ours", DateTimeOffset.UtcNow);
        Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_box.Files, AuditFiles.LockPathFor(ours), OwnerLock.Me, OwnerLock.LiveOwner));
        Assert.Equal(FileOutcome.Ok, AuditFiles.Discard(_box.Files, ours, out _).Outcome);
        Assert.False(AuditFiles.Exists(_box.Files, AuditFiles.LockPathFor(ours)));

        string stale = Save("stale", DateTimeOffset.UtcNow);
        WriteLock(stale, 77);
        Assert.Equal(FileOutcome.Ok, AuditFiles.Discard(_box.Files, stale, out _).Outcome);
        Assert.False(AuditFiles.Exists(_box.Files, AuditFiles.LockPathFor(stale)));

        string held = Save("held", DateTimeOffset.UtcNow);
        AuditFiles.MergeCoverage(_box.Files, held, ["a.cs"]);
        WriteLock(held, OtherPid);
        Assert.Equal(AuditText.LockedError(held), AuditFiles.HeldElsewhere(_box.Files, held));
        // Refused by the discard itself, holding the lock across the deletes (2026-10-30, review F1): nothing goes.
        Assert.NotEqual(FileOutcome.Ok, AuditFiles.Discard(_box.Files, held, out string? refusal).Outcome);
        Assert.Equal(AuditText.LockedError(held), refusal);
        Assert.True(AuditFiles.Exists(_box.Files, held));
        Assert.True(AuditFiles.Exists(_box.Files, AuditFiles.CoveragePath(held)));
        Assert.True(AuditFiles.Exists(_box.Files, AuditFiles.LockPathFor(held)));
    });

    /// <summary>A fix's read, mark and save under the lock (2026-10-30, review F2): refused while another holds it; this process's own lock stays.</summary>
    [Fact]
    public void WithLock_RefusesWhileAnotherHoldsIt_AndLeavesThisProcesssOwnLockHeld() => WithOtherAlive(() =>
    {
        string held = Save("held", DateTimeOffset.UtcNow);
        WriteLock(held, OtherPid);
        bool ran = false;
        Assert.False(AuditFiles.WithLock(_box.Files, held, () => ran = true, out string? refusal));
        Assert.False(ran);
        Assert.Equal(AuditText.LockedError(held), refusal);

        string free = Save("free", DateTimeOffset.UtcNow);
        Assert.True(AuditFiles.WithLock(_box.Files, free, () => ran = OwnerLock.ReadOwner(_box.Files, AuditFiles.LockPathFor(free)) == OwnerLock.Me, out refusal));
        Assert.True(ran);   // held while it ran
        Assert.Null(refusal);
        Assert.False(AuditFiles.Exists(_box.Files, AuditFiles.LockPathFor(free)));   // and let go after

        string ours = Save("ours", DateTimeOffset.UtcNow);
        Assert.Equal(LockTake.Taken, OwnerLock.TryLock(_box.Files, AuditFiles.LockPathFor(ours), OwnerLock.Me, OwnerLock.LiveOwner));
        Assert.True(AuditFiles.WithLock(_box.Files, ours, () => { }, out _));
        Assert.Equal(OwnerLock.Me, OwnerLock.ReadOwner(_box.Files, AuditFiles.LockPathFor(ours)));
    });

    /// <summary>A bare resume or fix picks the newest report no other NeonSidekick is auditing (2026-10-30, review F4).</summary>
    [Fact]
    public void Newest_SkipsAReportRunningElsewhere() => WithOtherAlive(() =>
    {
        string older = Save("older", DateTimeOffset.UtcNow.AddHours(-1), AuditStatus.Stopped);
        string newer = Save("newer", DateTimeOffset.UtcNow, AuditStatus.Auditing);
        WriteLock(newer, OtherPid);
        Assert.Equal(older, AuditFiles.Newest(AuditFiles.List(_box.Files))!.Path);

        File.Delete(_box.Full(older));
        Assert.Null(AuditFiles.Newest(AuditFiles.List(_box.Files)));
    });

    /// <summary>
    /// A lock that can't be read, or that names a process whose start time can't be read, names the lock to delete (2026-10-30, review F76/F86).
    /// </summary>
    [Fact]
    public void ALockThatCantBeChecked_NamesTheLockToDelete() => WithOtherAlive(() =>
    {
        string path = Save("x", DateTimeOffset.UtcNow);
        string lockPath = AuditFiles.LockPathFor(path);
        _box.Write(lockPath, "pid: 5\nhost: bad\u0007host\n");
        Assert.Equal(AuditText.UncheckedLockError(path, lockPath), AuditFiles.HeldElsewhere(_box.Files, path));

        WriteLock(path, OtherPid);
        var check = OwnerLock.CannotCheck;
        OwnerLock.CannotCheck = owner => owner.ProcessId == OtherPid;
        try
        {
            Assert.Equal(AuditText.UncheckedLockError(path, lockPath), AuditFiles.HeldElsewhere(_box.Files, path));
        }
        finally
        {
            OwnerLock.CannotCheck = check;
        }
    });

    /// <summary>
    /// A name typed in another case resolves to the report's own spelling (2026-10-30, review F82: on a case-blind file system the lock held under the
    /// typed spelling and the running audit's read as two). Windows' file system is case-blind too, so the path is reachable here.
    /// </summary>
    [WindowsFact]
    public void Resolve_ANameInAnotherCase_GivesTheSpellingOnDisk()
    {
        const string path = ".neon/audits/Mixed-Case.md";
        _box.Write(path, "# Code audit\n");
        Assert.Equal(path, AuditFiles.Resolve(_box.Files, "mixed-case"));
        Assert.Equal(path, AuditFiles.Resolve(_box.Files, "MIXED-CASE.md"));
    }

    [Fact]
    public void List_AnAuditingReportAnotherAppHolds_ListsAsRunningElsewhere() => WithOtherAlive(() =>
    {
        string path = Save("x", DateTimeOffset.UtcNow, AuditStatus.Auditing);
        WriteLock(path, OtherPid);
        var listing = AuditFiles.List(_box.Files).Single();
        Assert.True(listing.Elsewhere);
        Assert.Equal(AuditText.ElsewhereStatus, AuditText.StatusLabel(listing));
        Assert.StartsWith(AuditText.ElsewhereStatus, AuditText.CompletionNote(listing), StringComparison.Ordinal);
        Assert.Contains(AuditText.ElsewhereStatus, AuditText.ListLine(listing), StringComparison.Ordinal);

        WriteLock(path, 77);   // stale: stopped
        Assert.Equal("stopped", AuditText.StatusLabel(AuditFiles.List(_box.Files).Single()));
    });

    [Fact]
    public void Save_RefusesAReportPastTheCap_BeforeWriting()
    {
        string path = AuditFiles.Choose(_box.Files, "big", DateTimeOffset.UtcNow);
        var report = new AuditReport { Summary = new string('x', WorkingDirectory.MaxWriteChars + 1) };
        Assert.Equal(AuditText.ReportFullResult, AuditFiles.Save(_box.Files, path, report, DateTimeOffset.UtcNow));
        Assert.False(AuditFiles.Exists(_box.Files, path));
    }
}
