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

    [Fact]
    public void StillHolds_IsFalseOnlyWhileAnotherLiveAppHoldsIt()
    {
        Hold(A);
        Start(A);
        File.Delete(Path.Combine(_box.Root, ".neon", "audits", "a.lock"));
        Assert.True(_audit.StillHolds());   // gone: taken back
        Assert.True(Locked(A));

        File.Delete(Path.Combine(_box.Root, ".neon", "audits", "a.lock"));
        WriteOtherLock(A);
        Assert.False(_audit.StillHolds());
        _audit.Stop();
        Assert.True(Locked(A));   // the other's lock stays
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
