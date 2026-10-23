using System.Globalization;
using System.Text.RegularExpressions;
using NeonSidekick.ActiveDirectory;

namespace NeonSidekick.Tests;

/// <summary>Phase 7 of the admin review's fixes (2026-10-23): the directory's answers, over this class's fixture.</summary>
public sealed partial class ActiveDirectoryTests
{
    private static DirectoryEntry Entry(string dn, params (string Name, string Value)[] attributes) =>
        new(dn, attributes.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<byte[]>)g.Select(a => System.Text.Encoding.UTF8.GetBytes(a.Value)).ToList(), StringComparer.OrdinalIgnoreCase));

    /// <summary>H1: a configured base (an OU) still reads the policy and the roles at the domain's head.</summary>
    [Fact]
    public void H1_AnOuBase_ReadsThePolicyAtTheHead()
    {
        _config = new() { Domain = "lab.example", Server = "localhost", BaseDn = "OU=Staff," + Base };
        var source = Source(redacted: false);
        Assert.Equal(Base, Run(session => DirectoryQueries.HeadOf(session, source.Domain)));
        var alice = Run(session => DirectoryQueries.UserAnswer(session, source, "alice", _time.GetUtcNow(), TimeZoneInfo.Utc));
        Assert.DoesNotContain("expiry unknown", alice.Fields.Single(f => f.Label == "password").Value);
        var domain = Run(session => DirectoryQueries.DomainAnswer(session, source));
        Assert.Contains("at least 7 characters", domain.Fields.Single(f => f.Label == "password policy").Value);
        Assert.Contains("dc1.lab.example", domain.Fields.Single(f => f.Label == "roles").Value);
    }

    /// <summary>H3: a zero pwdLastSet on an account whose password never expires (or a smart-card one) is no change at next sign-in.</summary>
    [Theory]
    [InlineData(0x200, true)]
    [InlineData(0x10200, false)]
    [InlineData(0x40200, false)]
    [InlineData(0x1000, false)]
    public void H3_MustChange_OnlyWhereAPasswordCanExpire(int uac, bool mustChange)
    {
        var user = Entry("CN=x," + Base, ("userAccountControl", uac.ToString(CultureInfo.InvariantCulture)), ("pwdLastSet", "0"));
        var state = DirectoryQueries.State(user, new AdPolicy(null, null, null, null, null, null, null, false), _time.GetUtcNow());
        Assert.Equal(mustChange, state.MustChange);
    }

    /// <summary>H11: an account past its expiry says so in its head.</summary>
    [Fact]
    public void H11_AnExpiredAccount_SaysSo()
    {
        _system.Entries["CN=Alice Smith,OU=Staff," + Base]["accountExpires"] = [System.Text.Encoding.UTF8.GetBytes(AdValues.ToFileTime(_time.GetUtcNow().AddDays(-1)).ToString(CultureInfo.InvariantCulture))];
        var source = Source();
        var alice = Run(session => DirectoryQueries.UserAnswer(session, source, "alice", _time.GetUtcNow(), TimeZoneInfo.Utc));
        Assert.Contains("enabled, " + DirectoryText.AccountExpiredWord, alice.Head);
    }

    /// <summary>Q6: an unread policy is never stated as a fact.</summary>
    [Fact]
    public void Q6_AnUnreadPolicy_IsNotAFact()
    {
        var unread = new AdPolicy(null, null, null, null, null, null, null, false, Unread: true);
        Assert.Equal(DirectoryText.PolicyNotRead, DirectoryQueries.PolicyWords(unread));
        Assert.Equal(DirectoryText.PolicyNotRead, DirectoryQueries.LockoutWords(unread));
        var partly = new AdPolicy(null, null, null, null, null, null, null, false);
        Assert.Equal("unknown", DirectoryQueries.LockoutWords(partly));
        Assert.StartsWith("minimum length unknown", DirectoryQueries.PolicyWords(partly));
        Assert.StartsWith("never (no threshold)", DirectoryQueries.LockoutWords(partly with { LockoutThreshold = 0 }));
    }

    /// <summary>G9: a read-only DC is a DC.</summary>
    [Fact]
    public void G9_AReadOnlyDc_IsADc()
    {
        Assert.Contains("67108864", LdapFilter.DomainControllers);
        _system.Add("CN=RODC1,OU=Domain Controllers," + Base, ("objectClass", "computer"), ("sAMAccountName", "RODC1$"), ("cn", "RODC1"), ("userAccountControl", (0x4000000 | 0x1000).ToString(CultureInfo.InvariantCulture)));
        var source = Source();
        Assert.EndsWith("a read-only domain controller", Run(session => DirectoryQueries.ComputerAnswer(session, source, "RODC1", TimeZoneInfo.Utc)).Head);
    }

    /// <summary>V2, V8, V9, Q9: the kinds, the paths, the SIDs and the domains, pure.</summary>
    [Fact]
    public void V2_V8_V9_Q9_TheValues()
    {
        Assert.Equal(AdValues.ObjectKind, AdValues.KindOf(["top", "lostAndFound"]));
        Assert.Equal("user", AdValues.KindOf(["top", "person", "organizationalPerson", "user"]));
        Assert.Equal("Eng", AdValues.CanonicalPath("OU=Eng,OU=Neon,DC=lab,DC=example", "OU=Neon, DC=lab, DC=example"));
        byte[] big = [1, 1, 0, 1, 0, 0, 0, 0, 7, 0, 0, 0];
        string text = AdValues.Sid(big)!;
        Assert.Equal("S-1-0x000100000000-7", text);
        Assert.Equal(big, AdValues.SidBytes(text));
        Assert.Null(AdValues.SidBytes("S-1-281474976710656-1"));
        Assert.True(AdValues.SameDomain("CN=x,OU=y," + Base, "DC=lab, DC=example"));
        Assert.False(AdValues.SameDomain("CN=x,DC=child," + Base, Base));
    }

    /// <summary>S15: a cut list's note counts every row left.</summary>
    [Fact]
    public void S15_TheCutNote_CountsEveryRowLeft()
    {
        var answer = new DirectoryAnswer { Head = "h" };
        var table = new DirectoryTable([new DirectoryColumn("x")]) { Shown = 50, CutNote = left => "… " + left.ToString(CultureInfo.InvariantCulture) + " more" };
        for (int i = 0; i < 2000; i++)
        {
            table.Rows.Add(new DirectoryRow(new string('x', 400), ["x"], "CN=x", "x"));
        }

        answer.Tables.Add(table);
        var match = Regex.Match(DirectoryRender.ForModel(answer), "… (\\d+) more");
        Assert.True(match.Success);
        Assert.True(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) > 1900);
    }

    /// <summary>Q13: more candidates than are listed say so.</summary>
    [Fact]
    public void Q13_MoreCandidates_ThanListed()
    {
        for (int i = 0; i < 25; i++)
        {
            _system.Add("CN=Dup" + i.ToString(CultureInfo.InvariantCulture) + ",OU=Staff," + Base, ("objectClass", "user"), ("sAMAccountName", "dup" + i.ToString(CultureInfo.InvariantCulture)), ("displayName", "Dup"));
        }

        var source = Source();
        var answer = Run(session => DirectoryQueries.UserAnswer(session, source, "Dup", _time.GetUtcNow(), TimeZoneInfo.Utc));
        Assert.Contains("matches more than 20 accounts", answer.Error);
    }

    /// <summary>Q11: exactly the cap is not cut.</summary>
    [Fact]
    public void Q11_ExactlyTheCap_IsNotCut() => Assert.False(Run(session => DirectoryQueries.Members(session, Ops, 4)).Truncated);

    /// <summary>V5 (DA6): a stale find under 14 days runs, and says so.</summary>
    [Fact]
    public void V5_AShortStaleFind_SaysTheLag()
    {
        var source = Source();
        var answer = Run(session => DirectoryQueries.FindAnswer(session, source, new FindOptions("stale_users", 7, null, 50), _time.GetUtcNow(), TimeZoneInfo.Utc));
        Assert.Contains(DirectoryText.StaleLagNote, answer.Notes);
    }

    /// <summary>E9: a card's clock is the call's.</summary>
    [Fact]
    public void E9_ACardsClock_IsTheCalls()
    {
        var source = Source();
        var deadline = DirectoryDeadline.From(_time, TimeSpan.FromSeconds(40), CancellationToken.None);
        Assert.Equal(_time.GetUtcNow(), Run(session => DirectoryQueries.GroupAnswer(session, source, "Ops", false, "all", 50, deadline: deadline)).Now);
    }
}
