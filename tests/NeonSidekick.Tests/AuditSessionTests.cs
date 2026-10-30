using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The report's lock as <see cref="AuditSession"/> holds it (2026-10-29: two apps auditing one report each saved the whole file, and one overwrote
/// the other's findings): taken for a start, kept or let go by <see cref="AuditSession.Start"/>, a pending hold let go when its start fails, the
/// running audit's never by another's refusal, refused while another live NeonSidekick holds it, and told apart from a lock that can't be written.
/// </summary>
public sealed class AuditSessionTests : IDisposable
{
    private const int OtherPid = 424242;
    private readonly AuditSandbox _box = new();
    private readonly AuditSession _audit;
    private readonly AuditScope _scope;
    private readonly Func<LockOwner, bool> _alive = OwnerLock.LiveOwner;

    public AuditSessionTests()
    {
        _box.Write("src/x.cs", "class X { }\n");
        _audit = new AuditSession(_box.Files);
        _scope = AuditScopes.Resolve(_box.Files, _box.Git, new AuditScopeRequest(AuditScopeKind.Tree, []), CancellationToken.None).Scope!;
        // Another live NeonSidekick is one whose lock names OtherPid; any other owner but this process is gone.
        OwnerLock.LiveOwner = owner => owner.ProcessId == OtherPid || owner == OwnerLock.Me;
    }

    public void Dispose()
    {
        OwnerLock.LiveOwner = _alive;
        _box.Dispose();
    }

    private const string A = ".neon/audits/a.md";
    private const string B = ".neon/audits/b.md";

    private bool Locked(string report, string? root = null) => File.Exists(Path.Combine(root ?? _box.Root, AuditFiles.LockPathFor(report).Replace('/', Path.DirectorySeparatorChar)));

    private void Start(string report) => _audit.Start(report, _scope, AuditFocus.Dimensions, "", PlanReviewer.Model, 5, 2);

    private void WriteOtherLock(string report, string? host = null) =>
        _box.Write(AuditFiles.LockPathFor(report), "pid: " + OtherPid + "\nstarted: 2026-10-29T09:00:00.0000000+00:00\n" + (host is null ? "" : "host: " + host + "\n"));

    private void Hold(string report)
    {
        Assert.True(_audit.Hold(report, out string? error, out string? notice));
        Assert.Null(error);
        Assert.Null(notice);
    }

    [Fact]
    public void Hold_ThenStart_KeepsTheLock_AndStop_LetsItGo()
    {
        Hold(A);
        Assert.True(Locked(A));
        Start(A);
        Assert.True(Locked(A));
        Assert.Equal(AuditFiles.LockPathFor(A), _audit.HeldLockPath);
        Assert.True(_audit.StillHolds());

        _audit.Stop();
        Assert.False(Locked(A));
        Assert.Null(_audit.HeldLockPath);
    }

    [Fact]
    public void Release_AfterAFailedStep_LeavesNoLock()
    {
        Hold(A);
        _audit.Release();
        Assert.False(Locked(A));
        Assert.False(_audit.Active);
    }

    [Fact]
    public void Hold_OfAReportThisProcessHolds_Succeeds()
    {
        Hold(A);
        Start(A);
        Hold(A);   // resuming the audit on screen: no self-refusal
        Assert.True(Locked(A));
    }

    [Fact]
    public void TwoHolds_KeepTheRunningAuditsLock_UntilANewStartReplacesIt()
    {
        Hold(A);
        Start(A);

        Hold(B);
        _audit.Release();   // B's start failed
        Assert.True(Locked(A));
        Assert.False(Locked(B));

        Hold(A);
        _audit.Release();   // a refused resume of the audit on screen never unlocks it
        Assert.True(Locked(A));

        Hold(B);
        Start(B);           // B replaces A
        Assert.False(Locked(A));
        Assert.True(Locked(B));
        _audit.Stop();
        Assert.False(Locked(B));
    }

    [Fact]
    public void ARefusedHold_LeavesTheRunningAuditAsItWas()
    {
        Hold(A);
        Start(A);
        WriteOtherLock(B);

        Assert.False(_audit.Hold(B, out string? error, out _));
        Assert.Equal(AuditText.LockedError(B), error);
        Assert.True(_audit.Active);
        Assert.True(Locked(A));
        Assert.True(Locked(B));   // the other's, untouched
    }

    [Fact]
    public void Hold_WhileAnotherMachineHoldsIt_NamesThatMachine()
    {
        WriteOtherLock(A, "OTHER-PC");
        OwnerLock.LiveOwner = _alive;   // another machine's lock is never taken for gone

        Assert.False(_audit.Hold(A, out string? error, out _));
        Assert.Equal(AuditText.LockedOnHostError(A, "OTHER-PC", AuditFiles.LockPathFor(A)), error);
    }

    [Fact]
    public void AStaleLock_IsTakenOver()
    {
        _box.Write(AuditFiles.LockPathFor(A), "pid: 77\nstarted: 2026-10-29T09:00:00.0000000+00:00\n");
        Hold(A);
        Assert.Equal(OwnerLock.Me, OwnerLock.ReadOwner(_box.Files, AuditFiles.LockPathFor(A)));
    }

    [Fact]
    public void ALockThatCantBeWritten_IsRefusedWithItsOwnSentence_NeverAsLocked()
    {
        _box.Write(".neon/audits", "a file where the folder would go");

        Assert.False(_audit.Hold(A, out string? error, out _));
        Assert.Equal(AuditText.LockWriteError(AuditFiles.LockPathFor(A)), error);
        Assert.DoesNotContain("Another NeonSidekick", error, StringComparison.Ordinal);
    }

    private void SaveReport(string report) =>
        Assert.Null(_audit.Save(report, new AuditReport { Label = "x", Header = new AuditHeader(AuditStatus.Auditing, "", "", AuditFocus.Dimensions, "", "model", 1, null, null) }, _box.Time.GetLocalNow()));

    private void DeleteLock(string report) => File.Delete(Path.Combine(_box.Root, AuditFiles.LockPathFor(report).Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void StillHolds_IsFalseOnlyWhileAnotherLiveAppHoldsIt()
    {
        Hold(A);
        SaveReport(A);
        Start(A);
        DeleteLock(A);
        Assert.True(_audit.StillHolds());   // gone, the report as this process saved it: taken back
        Assert.True(Locked(A));

        DeleteLock(A);
        WriteOtherLock(A);
        Assert.False(_audit.StillHolds());
        _audit.Stop();
        Assert.True(Locked(A));   // the other's lock stays
    }

    /// <summary>
    /// Another NeonSidekick took the lock over, wrote the report and let go (2026-10-30, review F72): the lock is gone, but the report changed since
    /// this process's last save, so it isn't taken back, and the other's report stays as written.
    /// </summary>
    [Fact]
    public void ALockTakenOverWrittenAndLetGo_IsNotTakenBack()
    {
        Hold(A);
        SaveReport(A);
        Start(A);

        DeleteLock(A);
        WriteOtherLock(A);
        string full = Path.Combine(_box.Root, ".neon", "audits", "a.md");
        File.AppendAllText(full, "\nthe other's findings\n");
        File.SetLastWriteTimeUtc(full, File.GetLastWriteTimeUtc(full).AddSeconds(5));
        DeleteLock(A);

        Assert.False(_audit.StillHolds());
        Assert.False(Locked(A));
        Assert.EndsWith("the other's findings\n", File.ReadAllText(full), StringComparison.Ordinal);
    }

    /// <summary>A lock deleted right after this process's own save (the stamp follows every save, review F72) is taken back.</summary>
    [Fact]
    public void ALockDeletedAfterThisProcesssOwnSave_IsTakenBack()
    {
        Hold(A);
        SaveReport(A);
        Start(A);
        SaveReport(A);   // present_audit's save, through the session
        DeleteLock(A);
        Assert.True(_audit.StillHolds());
        Assert.Equal(OwnerLock.Me, OwnerLock.ReadOwner(_box.Files, AuditFiles.LockPathFor(A)));
    }

    /// <summary>A lock that can't be read mid-audit counts as another's (review F72): lost, and left as it is.</summary>
    [Fact]
    public void AnUnreadableLockMidAudit_IsLost()
    {
        Hold(A);
        SaveReport(A);
        Start(A);
        _box.Write(AuditFiles.LockPathFor(A), "pid: 5\nhost: not a name\n");
        Assert.False(_audit.StillHolds());
        Assert.True(Locked(A));
    }

    /// <summary>
    /// A fresh start whose chosen name another NeonSidekick locked meanwhile takes the next free one (2026-10-30, review F3); a step that answers
    /// no loop lets the hold go (F80).
    /// </summary>
    [Fact]
    public void Begin_TriesTheNextName_WhenTheChosenOnesLockIsHeld_AndAFailedStepLetsGo()
    {
        WriteOtherLock(A);
        string? begun = null;
        var loop = _audit.Begin(A, chosen =>
        {
            begun = chosen;
            SaveReport(chosen);
            return _audit.Start(chosen, _scope, AuditFocus.Dimensions, "", PlanReviewer.Model, 5, 2);
        }, out string? error, out _, another: () => B);
        Assert.NotNull(loop);
        Assert.Null(error);
        Assert.Equal(B, begun);
        Assert.True(Locked(B));
        _audit.Stop();

        Assert.Null(_audit.Begin(B, _ => null, out error, out _));
        Assert.Null(error);
        Assert.False(Locked(B));

        Assert.Null(_audit.Begin(A, _ => throw new InvalidOperationException("never run"), out error, out _));
        Assert.Equal(AuditText.LockedError(A), error);
    }

    /// <summary>A lock naming a process whose start time can't be read names the lock to delete (2026-10-30, review F76/F86).</summary>
    [Fact]
    public void Hold_OfALockWhoseOwnerCantBeChecked_NamesTheLock()
    {
        WriteOtherLock(A);
        var check = OwnerLock.CannotCheck;
        OwnerLock.CannotCheck = owner => owner.ProcessId == OtherPid;
        try
        {
            Assert.False(_audit.Hold(A, out string? error, out _));
            Assert.Equal(AuditText.UncheckedLockError(A, AuditFiles.LockPathFor(A)), error);
        }
        finally
        {
            OwnerLock.CannotCheck = check;
        }

        _box.Write(AuditFiles.LockPathFor(A), "pid: 5\nhost: not a name\n");
        Assert.False(_audit.Hold(A, out string? unreadable, out _));
        Assert.Equal(AuditText.UncheckedLockError(A, AuditFiles.LockPathFor(A)), unreadable);
    }

    [Fact]
    public void Stop_AfterTheWorkingFolderMoved_LetsGoInTheFolderItWasTakenIn()
    {
        string first = Path.Combine(_box.Dir, "first"), second = Path.Combine(_box.Dir, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        string root = first;
        var files = new WorkingDirectory(() => root, _box.Time);
        var audit = new AuditSession(files);
        Assert.True(audit.Hold(A, out _, out _));
        audit.Start(A, _scope, AuditFocus.Dimensions, "", PlanReviewer.Model, 5, 2);
        Assert.True(Locked(A, first));

        root = second;
        audit.Stop();
        Assert.False(Locked(A, first));
    }

    /// <summary>A drive that can't hold a lock (exFAT, the user's call): the audit runs unlocked with a warning, and the re-checks never stop it.</summary>
    [UnixFact]
    public void ADriveThatHoldsNoLock_RunsUnlocked_WithTheWarning()
    {
        var link = OwnerLock.LinkCall;
        var rename = OwnerLock.RenameExclusiveCall;
        try
        {
            OwnerLock.LinkCall = (_, _) => 45;
            OwnerLock.RenameExclusiveCall = (_, _) => 45;
            Assert.True(_audit.Hold(A, out string? error, out string? notice));
            Assert.Null(error);
            Assert.Equal(AuditText.UnlockedNotice(A), notice);
            Start(A);
            Assert.False(Locked(A));
            Assert.Null(_audit.HeldLockPath);
            Assert.True(_audit.StillHolds());
            _audit.Stop();
        }
        finally
        {
            OwnerLock.LinkCall = link;
            OwnerLock.RenameExclusiveCall = rename;
        }
    }
}
