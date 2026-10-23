using NeonSidekick.ActiveDirectory;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The findings of a high-effort review of the directory family (2026-10-20): a member by primary group is privileged, a PSO without readable
/// settings falls back to the domain's policy, the finds read each PSO once and <c>password_expiring</c> honors it, the cards run under the call's
/// deadline, the model's text keeps its quote note past a cut, abandoned calls are capped per server, a locked card finds its PDC once, and a
/// membership cycle never lists a group in itself.
/// </summary>
public sealed partial class ActiveDirectoryTests
{
    private const string Pat = "CN=Pat Prime," + Neon;
    private const string Hidden = "CN=Hidden,CN=Password Settings Container,CN=System," + Base;
    private const string ShortPasswords = "CN=ShortPasswords,CN=Password Settings Container,CN=System," + Base;

    [Fact]
    public void Privileged_APrimaryGroupMember_IsListed_NotALeftover()
    {
        Gaps();
        _system.Add(Pat, ("sAMAccountName", "pat"), ("displayName", "Pat Prime"), ("userAccountControl", "512"), ("primaryGroupID", "512"), ("objectSid", Rid(1350)), ("adminCount", "1"));
        Classes(Pat, UserChain);
        string text = Model(FindOf(new FindOptions("privileged", 0, null, 50)));
        Assert.StartsWith("privileged accounts in lab.example: 3\n", text);
        Assert.Contains("\n  ⟪pat⟫  user  ⟪Pat Prime⟫  ⟪Domain Admins⟫, ⟪Administrators⟫\n", text);
        Assert.DoesNotContain("⟪Pat Prime⟫  " + DirectoryText.LeftoverNote, text);
        Assert.Contains("\n" + DirectoryText.LeftoversTitle + " (2):\n", text);   // krbtgt and oldadmin, as before
    }

    [Fact]
    public void Policy_APsoWithoutReadableSettings_IsTheDomainsNamed()
    {
        Gaps();
        var policy = Run(s => DirectoryQueries.Policy(s, Base, Hidden));
        Assert.Null(policy.Name);
        Assert.Equal("Hidden", policy.UnreadPso);
        Assert.Equal(TimeSpan.FromMinutes(30), policy.LockoutDuration!.Value.Span);
        Assert.Equal(TimeSpan.FromDays(42), policy.MaxPasswordAge!.Value.Span);
        Assert.Equal("ShortPasswords", Run(s => DirectoryQueries.Policy(s, Base, ShortPasswords)).Name);
        _system.Entries["CN=Carol King,OU=Staff," + Base]["msDS-ResultantPSO"] = [FakeDirectorySystem.B(Hidden)];
        _system.Entries["CN=Alice Smith,OU=Staff," + Base]["msDS-ResultantPSO"] = [FakeDirectorySystem.B(Hidden)];
        string locked = Model(FindOf(new FindOptions("locked", 0, null, 50)));
        Assert.StartsWith("locked users in lab.example: 1\n", locked);   // carol's lockout of two hours ago ran out under the domain's 30 minutes
        Assert.DoesNotContain("carol", locked);
        string card = Run(s => DirectoryQueries.User(s, Source(), "alice", _time.GetUtcNow(), TimeZoneInfo.Utc));
        Assert.Contains("\n  policy: " + DirectoryText.PolicyUnreadOver("⟪Hidden⟫") + "\n", card);
        Assert.Contains(", unlocks ", card);
    }

    [Fact]
    public void Find_LockedAndExpiring_ReadEachPsoOnce_AndExpiringHonorsIt()
    {
        Gaps();
        var now = _time.GetUtcNow();
        string Ft(DateTimeOffset moment) => AdValues.ToFileTime(moment).ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (string dn in (string[])["CN=Alice Smith,OU=Staff," + Base, "CN=Carol King,OU=Staff," + Base, Grace])
        {
            _system.Entries[dn]["msDS-ResultantPSO"] = [FakeDirectorySystem.B(ShortPasswords)];
        }

        _system.Entries[Grace]["lockoutTime"] = [FakeDirectorySystem.B(Ft(now.AddMinutes(-3)))];
        _system.Entries[Grace]["pwdLastSet"] = [FakeDirectorySystem.B(Ft(now.AddDays(-25)))];
        _system.Filters.Clear();
        string locked = Model(FindOf(new FindOptions("locked", 0, null, 50)));
        Assert.Equal(2, _system.Filters.Count(f => f == "(objectClass=*)"));   // the domain's policy and the one PSO, for three accounts
        Assert.Contains("\n  ⟪grace⟫  ⟪Grace Hu⟫  locked ", locked);          // three minutes into the PSO's ten
        Assert.DoesNotContain("alice", locked);                              // ten minutes in: the PSO's lockout is over
        _system.Filters.Clear();
        string expiring = Model(FindOf(new FindOptions("password_expiring", 14, null, 50)));
        Assert.Equal(2, _system.Filters.Count(f => f == "(objectClass=*)"));
        Assert.Contains("\n  ⟪grace⟫  ⟪Grace Hu⟫  expires ", expiring);       // 25 days into the PSO's 30; the domain's 42 had left her out
    }

    [Fact]
    public async Task Cards_RunUnderTheCallsDeadline()
    {
        _system.TimeLimits.Clear();
        Assert.StartsWith("User ⟪alice⟫", await Call(new AdUserTool(Access()), ("name", "alice")));
        Assert.StartsWith("Computer ", await Call(new AdComputerTool(Access()), ("name", "ws01")));
        Assert.StartsWith("Domain ", await Call(new AdDomainTool(Access())));
        Assert.NotEmpty(_system.TimeLimits);
        Assert.All(_system.TimeLimits, limit => Assert.NotNull(limit));   // each search took what the call's deadline left, never the bare default
    }

    [Fact]
    public void ForModel_ACutKeepsTheQuoteNote_AndClosesTheFence()
    {
        var answer = new DirectoryAnswer { Head = "Group ⟪Big⟫ in lab.example" };
        answer.Fields.Add(new DirectoryField("member of", UntrustedText.Fenced(new string('a', 30_000))));
        answer.Notes.Add(DirectoryText.FirstRangeNote);
        string text = DirectoryRender.ForModel(answer);
        Assert.True(text.Length <= DirectoryQueries.MaxChars, text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.EndsWith("\n" + DirectoryText.FirstRangeNote + "\n" + UntrustedText.QuoteNote, text);
        Assert.Contains("aaa⟫\n(cut at 16,000 characters)\n", text);
        Assert.Equal(text.Count(c => c == '⟪'), text.Count(c => c == '⟫'));
        Assert.Equal("⟪ab⟫", DirectoryRender.CutBody("⟪abcdef⟫", 4));
        Assert.Equal("x\U0001F600", DirectoryRender.CutBody("x\U0001F600yz", 4));   // a pair kept whole: room 3 for the text, the pair fits
        Assert.Equal("x", DirectoryRender.CutBody("x\U0001F600yz", 3));             // room 2: the pair would split, so it goes
    }

    [Fact]
    public async Task Access_AbandonedCalls_AreCappedPerServer()
    {
        var system = new HeldDirectorySystem();
        var config = new DirectoryDomainConfig { Domain = "stuck.example", Server = "stuck-dc.example" };
        var access = new DirectoryAccess(system, () => new DirectoryCatalog([new DirectoryDomain("stuck", config)], []), () => ["stuck"], () => null, _time);
        var source = access.Domain(null, true).Source!;
        for (int i = 0; i < DirectoryAccess.MaxAbandoned; i++)
        {
            using var cancel = new CancellationTokenSource();
            var call = access.RunAsync(source, _ => 1, cancel.Token);
            Assert.True(system.Entered.Wait(TimeSpan.FromSeconds(10)));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        }

        Assert.Equal(DirectoryAccess.MaxAbandoned, DirectoryAccess.AbandonedRunning(source.Target));
        var refused = await access.RunAsync(source, _ => 1, CancellationToken.None);
        Assert.Equal(DirectoryText.StillWaiting("stuck.example", "stuck-dc.example"), refused.Error);
        Assert.Equal(DirectoryAccess.MaxAbandoned, system.Runs);   // never connected
        system.Release.Set();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DirectoryAccess.AbandonedRunning(source.Target) > 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        Assert.Equal(0, DirectoryAccess.AbandonedRunning(source.Target));
        Assert.Equal(1, (await access.RunAsync(source, _ => 1, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task Command_ALockedCard_FindsItsPdcOnce()
    {
        int plain = 0;
        _system.OnSearch = _ => plain++;
        Run(s => DirectoryQueries.UserAnswer(s, Source(), "alice", _time.GetUtcNow(), TimeZoneInfo.Utc));
        int card = 0;
        _system.OnSearch = _ => card++;
        var result = await DirectoryCommand.RunCardAsync(Access(), null, (s, source, deadline) => DirectoryQueries.UserAnswer(s, source, "alice", _time.GetUtcNow(), TimeZoneInfo.Utc, deadline: deadline),
            "the user alice", CancellationToken.None);
        _system.OnSearch = null;
        Assert.Equal("dc1.lab.example", result.LockedPdc);
        Assert.Equal(plain, card);
    }

    [Fact]
    public void Group_ACycle_NeverListsTheGroupInItself()
    {
        Gaps();
        var answer = GroupOf("Loop B", nested: true);
        string text = Model(answer);
        Assert.StartsWith("Group ⟪Loop B⟫ in lab.example: 2 members, nested (2 direct, 0 through other groups)\n", text);
        Assert.DoesNotContain("(group Loop B)", text);
        Assert.DoesNotContain(answer.Tables[0].Rows, r => r.Dn == "CN=Loop B," + Neon);
    }

    /// <summary>A directory whose work never returns until released: a DC that took the connection and never answered the bind.</summary>
    private sealed class HeldDirectorySystem : IDirectorySystem
    {
        private int _runs;

        public ManualResetEventSlim Release { get; } = new();

        public SemaphoreSlim Entered { get; } = new(0);

        public int Runs => Volatile.Read(ref _runs);

        public T Run<T>(DirectoryTarget target, Func<IDirectorySession, T> work)
        {
            Interlocked.Increment(ref _runs);
            Entered.Release();
            Release.Wait();
            return work(null!);
        }
    }
}
