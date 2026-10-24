using System.Text;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Computers;
using NeonSidekick.EventLog;
using NeonSidekick.Services;
using NeonSidekick.Sql;
using NeonSidekick.Tasks;

namespace NeonSidekick.Tests;

/// <summary>
/// Phase 2 of the admin review's fixes (2026-10-23): the config catalogs (<see cref="ConfigCache"/>, the loaders' checks, DA1, DA2, D2, Q1,
/// C7, F6, H6, K9) and the sign-in guard (<see cref="SignInGuard"/>: F1–F4, B8, Q14, I12, J5).
/// </summary>
public sealed partial class AdminFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "admin-fixes-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _cache = ConfigCache.Scope();
    private readonly IDisposable _guard = SignInGuard.Scope();

    public void Dispose()
    {
        _cache.Dispose();
        _guard.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Write(string name, string text)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    // ── The cache ───────────────────────────────────────────────────────────

    [Fact]
    public void Cache_ReadsAgain_OnlyWhenTheFileChanges_OrTheAppWroteIt()
    {
        string path = Write("services.json", "{ \"computers\": { \"a\": { \"host\": \"a.example\" } } }");
        int loads = 0;
        string Load() => ConfigCache.Get("t", path, () => { loads++; return File.ReadAllText(path); });
        Load();
        Load();
        Assert.Equal(1, loads);
        File.WriteAllText(path, "{ \"computers\": { \"ab\": { \"host\": \"a.example\" } } }");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Contains("\"ab\"", Load());
        Assert.Equal(2, loads);
        ConfigCache.Invalidate(path);
        Load();
        Assert.Equal(3, loads);
    }

    // ── The loaders' checks (DA1, Q1, C7, D2) ───────────────────────────────

    /// <summary>DA1: a misspelled key refuses its entry, naming the key and the one meant; the others load; a key beside "computers" is listed.</summary>
    [Fact]
    public void DA1_AnUnknownKey_RefusesItsEntry_NamingTheNearest()
    {
        string path = Write("services.json", """
            { "computer": 1,
              "computers": {
                "app01": { "host": "app01.example", "access": "readwrite", "control": ["*"], "controlAction": ["start"] },
                "app02": { "host": "app02.example" } } }
            """);
        var (computers, problems) = ServiceConfigFile.Family.Load(path);
        Assert.Equal(["app02"], computers.Select(c => c.Name));
        Assert.Contains(problems, p => p.Name == "app01" && p.Reason == ComputerText.UnknownKey("controlAction", "controlActions"));
        Assert.Contains(problems, p => p.Name is null && p.Reason == ComputerText.UnknownFileKey("computer", "computers"));
        var (logs, logProblems) = EventLogConfigFile.Load(Write("eventlog.json", "{ \"computers\": { \"dc\": { \"host\": \"dc.example\", \"log\": [\"Security\"] } } }"));
        Assert.Empty(logs);
        Assert.Equal(ComputerText.UnknownKey("log", "logs"), Assert.Single(logProblems).Reason);
    }

    /// <summary>Q1, C7: a name given twice (spaces and case aside) loads neither; an entry with a key twice is refused, and its password never written.</summary>
    [Fact]
    public void Q1_C7_ANameOrAKeyGivenTwice_IsRefused()
    {
        string path = Write("tasks.json", """
            { "computers": {
                "fs01": { "host": "fs01.example" },
                " FS01": { "host": "evil.example" },
                "web": { "host": "web.example", "auth": "runas", "user": "CORP\\ops", "password": "dpapi:x", "password": "plain" } } }
            """);
        var (computers, problems) = TaskConfigFile.Family.Load(path);
        Assert.Empty(computers);
        Assert.Equal(2, problems.Count(p => p.Reason == ComputerText.NamedTwice));
        Assert.Contains(problems, p => p.Name == "web" && p.Reason == ComputerText.KeyTwice("password"));
        Assert.Equal(ComputerText.KeyTwice("password"), ConnectionsFileEdit.WritePassword(path, "web", "dpapi:new", ComputerFamily<TaskComputerConfig>.Section, ComputerFamily<TaskComputerConfig>.Noun));
        Assert.Contains("\"plain\"", File.ReadAllText(path));
    }

    /// <summary>D2: a profile's refused entry hides the home's of its name; it never falls back to the home's readwrite one.</summary>
    [Fact]
    public void D2_ARefusedProfileEntry_HidesTheHomes()
    {
        string home = Path.Combine(_dir, "home");
        string profile = Path.Combine(home, "profiles", "neon");
        Directory.CreateDirectory(Path.Combine(profile, "configs"));
        Directory.CreateDirectory(Path.Combine(home, "configs"));
        File.WriteAllText(ServiceConfigFile.Family.ProfilePath(profile), "{ \"computers\": { \"app01\": { \"host\": \"app01\", \"access\": \"read-only\" } } }");
        File.WriteAllText(ServiceConfigFile.Family.GlobalPath(home), "{ \"computers\": { \"app01\": { \"host\": \"app01\", \"access\": \"readwrite\", \"control\": [\"*\"] } } }");
        var catalog = ServiceConfigFile.Family.LoadCatalog(profile, home);
        Assert.DoesNotContain(catalog.Computers, c => c.Name == "app01");
        Assert.Contains(catalog.Problems, p => p.Name == "app01");
    }

    /// <summary>F6: an encryption and a wizard's add on one file, at once, never lose the add (one lock, one temp name each).</summary>
    [Fact]
    public void F6_ConcurrentSplices_KeepEveryEntry()
    {
        string path = Write("eventlog.json", "{ \"computers\": { } }");
        Parallel.For(0, 16, i => Assert.Null(EventLogConfigFile.AddComputer(path, "c" + i, new EventLogComputerConfig { Host = "c" + i + ".example" })));
        var (computers, problems) = EventLogConfigFile.Load(path);
        Assert.Empty(problems);
        Assert.Equal(16, computers.Count);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    // ── DA2: the credential's place and its user ────────────────────────────

    [Fact]
    public void DA2_ACredentialOutsideThePrefix_IsRefused_AndItsUserIsChecked()
    {
        Assert.Equal(ComputerText.CredentialOutsidePrefix("NeonSidekick/ad/"), ComputerConfig.CredentialProblem("MicrosoftAccount:user=me", "NeonSidekick/ad/"));
        Assert.Equal(ComputerText.CredentialOutsidePrefix("NeonSidekick/ad/"), ComputerConfig.CredentialProblem("NeonSidekick/ad/", "NeonSidekick/ad/"));
        Assert.Null(ComputerConfig.CredentialProblem("neonsidekick/AD/lab2", "NeonSidekick/ad/"));
        Assert.Null(ComputerConfig.CredentialProblem(null, "NeonSidekick/ad/"));
        var config = new DirectoryDomainConfig { Domain = "lab.example", Credential = "Git:https://github.com" };
        Assert.Equal(ComputerText.CredentialOutsidePrefix(DirectoryDomainConfig.CredentialPrefix), config.Problem("lab"));

        var mismatch = ComputerConfig.CheckedUser(CredentialResult.Ok("secret", @"LAB\other"), "NeonSidekick/ad/lab", @"LAB\reader");
        Assert.Null(mismatch.Value);
        Assert.Equal(ComputerText.CredentialUserMismatch("NeonSidekick/ad/lab", @"LAB\other", @"LAB\reader"), mismatch.Error);
        Assert.Equal("secret", ComputerConfig.CheckedUser(CredentialResult.Ok("secret", @"lab\READER"), "t", @"LAB\reader").Value);
        Assert.Equal("secret", ComputerConfig.CheckedUser(CredentialResult.Ok("secret", ""), "t", @"LAB\reader").Value);
    }

    /// <summary>K9: a login's printed form never holds its password.</summary>
    [Fact]
    public void K9_ALoginsToString_LeavesThePasswordOut()
    {
        Assert.DoesNotContain("hunter2", new ServiceLogin("ops", "hunter2").ToString());
        Assert.DoesNotContain("hunter2", new TaskTarget("h", new TaskLogin("ops", "hunter2")).ToString());
        Assert.DoesNotContain("hunter2", new DirectoryTarget("dc", 389, false, new DirectoryLogin("ops", "hunter2")).ToString());
    }

    /// <summary>H9: a sealed password goes into the entry's own text, never a second write.</summary>
    [Fact]
    public void H9_ASealedPassword_IsWrittenWithItsEntry()
    {
        string text = ServiceConfigFile.Family.EntryText(new ServiceComputerConfig { Host = "h", Auth = "runas", User = @"CORP\ops", Password = "plain" }, "dpapi:sealed");
        Assert.Contains("dpapi:sealed", text);
        Assert.DoesNotContain("plain", text);
        Assert.DoesNotContain("password", ServiceConfigFile.Family.EntryText(new ServiceComputerConfig { Host = "h", Password = "plain" }), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>H6: a tick whose entry is gone is dropped, but only from a clean load; the built-in names stay.</summary>
    [Fact]
    public void H6_AStaleTick_IsDropped_OnlyFromACleanLoad()
    {
        Assert.Equal(["local", "app02"], OfferedTicks.Pruned(["local", "app01", "app02"], ["app02"], clean: true, "local"));
        Assert.Null(OfferedTicks.Pruned(["local", "app01"], ["app02"], clean: false, "local"));
        Assert.Null(OfferedTicks.Pruned(["local", "app02"], ["app02"], clean: true, "local"));
        Assert.Null(OfferedTicks.Pruned(null, [], clean: true, "local"));
        Assert.Null(OfferedTicks.Pruned(["local", "app01"], ["local"], clean: true, "local"));   // DP7: an empty (or missing) file never prunes
    }

    // ── The sign-in guard ───────────────────────────────────────────────────

    private static readonly Func<Exception, bool> WrongPassword = ex => ex is ServiceException { Code: var code } && SignInGuard.IsWrongPassword(code);

    /// <summary>F2, B8: a wrong password is held and the next call never reaches the server; a locked account is not held.</summary>
    [Fact]
    public async Task Guard_AWrongPassword_IsHeld_ALockedAccountIsNot()
    {
        int calls = 0;
        var call = SignInGuard.ForHost("services", "app01", @"CORP\ops", "old", WrongPassword);
        var first = await SignInGuard.RunAsync<int>(call, _ => { calls++; throw new ServiceException(1326, "logon failure"); }, TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);
        Assert.IsType<ServiceException>(first.Failure);
        await Task.Delay(50);
        var second = await SignInGuard.RunAsync(call, _ => ++calls, TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);
        Assert.Equal(SignInStop.Held, second.Stop);
        Assert.Equal(1, calls);
        var newPassword = await SignInGuard.RunAsync(SignInGuard.ForHost("tasks", "APP01", @"CORP\ops", "new", WrongPassword), _ => ++calls, TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);
        Assert.Equal(SignInStop.None, newPassword.Stop);

        var locked = SignInGuard.ForHost("services", "app02", @"CORP\ops", "pw", WrongPassword);
        await SignInGuard.RunAsync<int>(locked, _ => throw new ServiceException(1909, "locked out"), TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(SignInGuard.IsHeld(locked.HoldKey!));
    }

    /// <summary>F1: while a password is unproven its calls go one at a time; once one signs in they run side by side.</summary>
    [Fact]
    public async Task Guard_AnUnprovenPassword_SignsInOneAtATime()
    {
        int running = 0, most = 0;
        var call = SignInGuard.ForHost("services", "app03", @"CORP\ops", "pw", WrongPassword);
        int Work(bool awaitCompany)
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            if (awaitCompany)
            {
                // Proven, the calls may overlap: each waits (5 s at most) for a second to be running, so a busy 2-core pool injects a
                // thread rather than the 30 ms sleep racing it (2026-10-23, a CiLike whole-suite run saw most == 1). A gate still in
                // force would keep the second out and fail the wait.
                SpinWait.SpinUntil(() => Volatile.Read(ref most) > 1, TimeSpan.FromSeconds(5));
            }

            Thread.Sleep(30);
            Interlocked.Decrement(ref running);
            return now;
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SignInGuard.RunAsync(call, _ => Work(awaitCompany: false), TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None)));
        Assert.Equal(1, most);
        most = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SignInGuard.RunAsync(call, _ => Work(awaitCompany: true), TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None)));
        Assert.True(most > 1);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    /// <summary>F3, F4, I12, Q14: an abandoned call's late refusal is still held; the cap counts at the start; a cancelled call starts nothing.</summary>
    [Fact]
    public async Task Guard_AbandonedCalls_AreCappedAndTheirRefusalsKept()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SignInGuard.RunAsync(SignInGuard.ForHost("services", "x", null, null, WrongPassword), _ => 1, TimeSpan.FromSeconds(30), TimeProvider.System, new CancellationToken(canceled: true)));

        using var release = new ManualResetEventSlim();
        var call = SignInGuard.ForHost("services", "slow", @"CORP\ops", "pw", WrongPassword);
        var timedOut = await SignInGuard.RunAsync<int>(call, _ => { release.Wait(); throw new ServiceException(1326, "late"); }, TimeSpan.FromMilliseconds(50), TimeProvider.System, CancellationToken.None);
        Assert.Equal(SignInStop.TimedOut, timedOut.Stop);
        Assert.Equal(1, SignInGuard.AbandonedRunning(call.Server));
        var plain = SignInGuard.ForHost("services", "slow", null, null, WrongPassword);
        await SignInGuard.RunAsync(plain, _ => { release.Wait(); return 0; }, TimeSpan.FromMilliseconds(50), TimeProvider.System, CancellationToken.None);
        Assert.Equal(SignInStop.StillWaiting, (await SignInGuard.RunAsync(plain, _ => 0, TimeSpan.FromSeconds(1), TimeProvider.System, CancellationToken.None)).Stop);
        release.Set();
        var until = DateTime.UtcNow.AddSeconds(5);
        while ((SignInGuard.AbandonedRunning(call.Server) > 0 || !SignInGuard.IsHeld(call.HoldKey!)) && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, SignInGuard.AbandonedRunning(call.Server));
        Assert.True(SignInGuard.IsHeld(call.HoldKey!));
    }

    /// <summary>B2: a change cancelled before it begins is abandoned with nothing done; once begun it is waited out, and says so.</summary>
    [Fact]
    public async Task Guard_AChangeOnceBegun_IsWaitedOut()
    {
        using var cancel = new CancellationTokenSource();
        bool acted = false;
        var call = SignInGuard.ForHost("services", "c", null, null, WrongPassword);
        var waited = SignInGuard.RunAsync(call, turn =>
        {
            turn.Begin();
            cancel.Cancel();
            Thread.Sleep(100);
            acted = true;
            return 7;
        }, TimeSpan.FromSeconds(30), TimeProvider.System, cancel.Token);
        var run = await waited;
        Assert.True(acted);
        Assert.True(run.WaitedOut);
        Assert.Equal(7, run.Value);

        using var early = new CancellationTokenSource();
        using var gate = new ManualResetEventSlim();
        bool began = false;
        var notBegun = SignInGuard.RunAsync(call, turn =>
        {
            gate.Wait();
            turn.Begin();
            began = true;
            return 0;
        }, TimeSpan.FromSeconds(30), TimeProvider.System, early.Token);
        early.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notBegun);
        gate.Set();
        await Task.Delay(50);
        Assert.False(began);
    }

    /// <summary>B8, J5: AD's server error says why a 0x31 was refused; only a wrong password (or no sub-code) holds.</summary>
    [Theory]
    [InlineData("ldap_bind_s failed (0x31): 80090308: LdapErr: DSID-0C09041C, comment: AcceptSecurityContext error, data 52e, v4563", true)]
    [InlineData("ldap_bind_s failed (0x31): 80090308: LdapErr: DSID-0C09041C, comment: AcceptSecurityContext error, data 775, v4563", false)]
    [InlineData("ldap_bind_s failed (0x31): … data 533, v4563", false)]
    [InlineData("ldap_bind_s failed (0x31)", true)]
    public void B8_OnlyAWrongPassword_IsHeld(string detail, bool held)
    {
        Assert.Equal(held, DirectoryAccess.RefusedPassword(0x31, detail));
        if (!held)
        {
            Assert.DoesNotContain("password is wrong", DirectoryErrors.Sentence(0x31, "lab", "dc", 389, "ops", detail));
        }
    }
}
