using System.Collections.Concurrent;
using System.Security.Principal;
using NeonSidekick.Files;
using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Unc;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="UncAccess"/> (2026-09-30): which share a call means, the write gate's two keys, the reach refusals named from the
/// Win32 code, and the work run as the share's account — a <c>runas</c> netonly token on the worker, its impersonation reaching a
/// search's parallel readers, a cancelled read abandoned without its token pulled from under it. A local folder stands in for a
/// share: a netonly token reads it as the app's own user (Windows checks the password only at a network sign-in), so these run
/// anywhere; the live share is <c>LiveUncTests</c>' part.
/// </summary>
public sealed class UncAccessTests : IDisposable
{
    private const string Nobody = @"NEONSIDEKICK-TEST\nobody";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private UncCatalog _catalog;

    public UncAccessTests()
    {
        _root = Path.Combine(_dir, "share");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha needle");
        _catalog = new UncCatalog([Windows("eng", _root), RunAs("fin", _root)], []);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static UncNamedShare Windows(string name, string path, string? access = null) => new(name, new UncShareConfig { Path = path, Access = access }, "test");

    private static UncNamedShare RunAs(string name, string path) =>
        new(name, new UncShareConfig { Path = path, Auth = "runas", User = Nobody, Password = "not-checked-locally" }, "test");

    private UncAccess Access() => new(() => _catalog, _time);

    /// <summary>Whether this thread runs under an impersonation token (a netonly one keeps the local name, so the name cannot tell).</summary>
    private static bool Impersonating() => OperatingSystem.IsWindows() && WindowsIdentity.GetCurrent(ifImpersonating: true) is not null;

    [WindowsFact]
    public void Resolve_ByName_ByFullPath_ByDefault_AndEachRefusal()
    {
        var access = Access();
        string full = Path.Combine(_root, "a.txt");

        Assert.Equal("fin", access.Resolve("FIN", "a.txt", null, out string relative, out string? error)!.Name);
        Assert.Equal("a.txt", relative);
        Assert.Null(error);
        Assert.Equal("eng", access.Resolve(null, full, "fin", out relative, out _)!.Name);   // located: the first root it lies under
        Assert.Equal(full, relative);
        Assert.Equal("fin", access.Resolve("fin", full.Replace('\\', '/'), null, out relative, out _)!.Name);
        Assert.Equal(full, relative);   // \-spelled for the sandbox
        Assert.Equal("fin", access.Resolve(null, "a.txt", "fin", out _, out _)!.Name);
        Assert.Equal("eng", access.Resolve(null, "", "gone", out _, out _)!.Name);

        Assert.Null(access.Resolve("hr", "a.txt", null, out _, out error));
        Assert.Equal(UncText.UnknownShare("hr", "eng, fin"), error);
        Assert.Null(access.Resolve("eng", @"C:\Windows\win.ini", null, out _, out error));
        Assert.Equal(UncText.NotInShare(@"C:\Windows\win.ini", _catalog.Shares[0]), error);
        Assert.Null(access.Resolve(null, @"\\elsewhere\x\y.txt", null, out _, out error));
        Assert.Equal(UncText.NotUnderAnyShare(@"\\elsewhere\x\y.txt", "eng, fin"), error);
        Assert.Null(access.Resolve(null, "a.txt:secret", null, out _, out error));
        Assert.Equal(UncText.StreamPath("a.txt:secret"), error);
        Assert.Null(access.Resolve(null, full + ":secret", null, out _, out error));
        Assert.Equal(UncText.StreamPath(full + ":secret"), error);

        _catalog = UncCatalog.Empty;
        Assert.Null(access.Resolve(null, "a.txt", null, out _, out error));
        Assert.Equal(UncText.NoShares, error);
    }

    [Fact]
    public void TheWriteGate_NeedsBothKeys()
    {
        var readOnly = Windows("eng", _root);
        var readWrite = Windows("data", _root, "readwrite");

        Assert.Equal(UncText.WritesOff, UncAccess.WriteRefusal(readWrite, writesOn: false));
        Assert.Equal(UncText.WritesOff, UncAccess.WriteRefusal(readOnly, writesOn: false));
        Assert.Equal(UncText.ReadOnlyShare("eng"), UncAccess.WriteRefusal(readOnly, writesOn: true));
        Assert.Null(UncAccess.WriteRefusal(readWrite, writesOn: true));
    }

    [WindowsFact]
    public async Task AWindowsShare_IsRead_AsTheUser_WithTheSharesOptions()
    {
        var result = await Access().RunAsync(_catalog.Shares[0], write: false, files => files.List(""), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(FileOutcome.Ok, result.Value!.Outcome);
        Assert.Equal("a.txt", Assert.Single(result.Value.Entries).Name);
        Assert.Same(WorkingDirectoryOptions.Share, Access().Files(_catalog.Shares[0]).Options);
    }

    [WindowsFact]
    public async Task ARunAsShare_RunsUnderANetOnlyToken_ThatReachesASearchsParallelReaders()
    {
        for (int i = 0; i < 40; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"f{i:D2}.txt"), "needle");
        }

        var seen = new ConcurrentBag<(int Thread, bool Impersonating)>();
        var result = await Access().RunAsync(_catalog.Shares[1], write: false, files =>
        {
            Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            {
                Thread.Sleep(2);
                seen.Add((Environment.CurrentManagedThreadId, Impersonating()));
            });
            return files.Search("needle", "", null, false, CancellationToken.None, limit: 200);
        }, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(41, result.Value!.FilesMatched);
        Assert.All(seen, s => Assert.True(s.Impersonating));
        Assert.True(seen.Select(s => s.Thread).Distinct().Count() >= 2);
        Assert.False(Impersonating());   // the caller's thread never was
    }

    [WindowsFact]
    public async Task ACancelledRead_IsAbandoned_ItsWorkFinishing_StillImpersonated()
    {
        using var gate = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var after = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Access().RunAsync(_catalog.Shares[1], write: false, files =>
        {
            gate.Wait();
            after.SetResult(Impersonating());
            return files.List("");
        }, cancel.Token);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        gate.Set();

        Assert.True(await after.Task.WaitAsync(TimeSpan.FromSeconds(10)));   // the token was the worker's, not disposed under it
    }

    [WindowsFact]
    public async Task ACancelledSearch_Throws_AndAWriteIsWaitedOut()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Access().RunAsync(_catalog.Shares[0], write: false, files => files.Search("needle", "", null, false, cancelled.Token), CancellationToken.None));

        var write = await Access().RunAsync(_catalog.Shares[0], write: true, files => files.WriteText("b.txt", "beta", overwrite: false), cancelled.Token);
        Assert.Equal(FileOutcome.Ok, write.Value!.Outcome);   // a write is never abandoned, whatever the token says
        Assert.Equal("beta", File.ReadAllText(Path.Combine(_root, "b.txt")));
    }

    [WindowsFact]
    public async Task AMissingRoot_AFileRoot_AndAMissingPassword_AreTheirSentences_NothingCreated()
    {
        string missing = Path.Combine(_dir, "nowhere");
        var gone = await Access().RunAsync(Windows("gone", missing), write: false, files => files.List(""), CancellationToken.None);
        Assert.Equal(UncText.RootMissing("gone", missing), gone.Error);
        Assert.False(Directory.Exists(missing));

        string file = Path.Combine(_root, "a.txt");
        var notFolder = await Access().RunAsync(Windows("file", file), write: false, files => files.List(""), CancellationToken.None);
        Assert.Equal(UncText.NotAFolder("file", file), notFolder.Error);

        var noPassword = new UncNamedShare("fin", new UncShareConfig { Path = _root, Auth = "runas", User = Nobody }, "test");
        var refused = await Access().RunAsync(noPassword, write: false, files => files.List(""), CancellationToken.None);
        Assert.Equal(UncText.CannotSignIn("fin", UncText.NoPassword("fin")), refused.Error);
    }

    [Fact]
    public void TheReachReasons_ComeFromTheWin32Code_NeverTheMessage()
    {
        var share = RunAs("fin", @"\\fs01\fin");
        static IOException Win32(int code) => new("worded in any language", unchecked((int)0x80070000) | code);

        Assert.Equal(UncText.Unreachable("fin", @"\\fs01\fin", "worded in any language"), UncAccess.ReachReason(share, @"\\fs01\fin", Win32(53)));
        Assert.Equal(UncText.Unreachable("fin", @"\\fs01\fin", "worded in any language"), UncAccess.ReachReason(share, @"\\fs01\fin", Win32(67)));
        Assert.Equal(UncText.BadAccount("fin", Nobody), UncAccess.ReachReason(share, @"\\fs01\fin", Win32(1326)));
        Assert.Equal(UncText.NoLogonServer("fin"), UncAccess.ReachReason(share, @"\\fs01\fin", Win32(1311)));
        Assert.Equal(UncText.AccountRefused("fin", Nobody, "worded in any language"), UncAccess.ReachReason(share, @"\\fs01\fin", Win32(1385)));
        Assert.Equal(UncText.RootMissing("fin", @"\\fs01\fin"), UncAccess.ReachReason(share, @"\\fs01\fin", Win32(3)));
        Assert.Equal(UncText.AccessDenied("fin", @"\\fs01\fin", Nobody), UncAccess.ReachReason(share, @"\\fs01\fin", new UnauthorizedAccessException("no")));
        Assert.Equal(UncText.RootMissing("fin", @"\\fs01\fin"), UncAccess.ReachReason(share, @"\\fs01\fin", new DirectoryNotFoundException("no")));
        Assert.Equal(UncText.ReachFailed("fin", @"\\fs01\fin", "odd"), UncAccess.ReachReason(share, @"\\fs01\fin", new IOException("odd")));
    }

    [Fact]
    public void TheAuditLine_AndWhoAShareIsReachedAs_ArePinned()
    {
        Assert.Equal(Nobody, UncAccess.Account(RunAs("fin", _root)));
        Assert.Equal(Environment.UserDomainName + "\\" + Environment.UserName, UncAccess.Account(Windows("eng", _root)));
        Assert.Equal(@"fin (CORP\svc): wrote reports\q3.md", UncText.AuditLogLine("fin", @"CORP\svc", "wrote", @"reports\q3.md"));
        Assert.Equal(TimeSpan.FromSeconds(10), UncAccess.ReachTimeout);
        Assert.Equal("no answer in 10 s", UncText.TimedOut(UncAccess.ReachTimeout));
    }
}
