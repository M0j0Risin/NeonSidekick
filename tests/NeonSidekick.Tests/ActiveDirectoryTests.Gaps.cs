using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Files;
using NeonSidekick.Help;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The directory's gaps closed (2026-10-15, the AD gaps plan): groups found and their cards' rows, members of every kind and from outside,
/// nested membership walked forward with the group each comes through, primary groups, the walk's cycles, caps, batches and time budget, the
/// upward walk, the new find kinds and arguments with their refusals and one list of kinds, OUs by path, name or DN, the tree and a branch, GPO
/// links, a user's reports and distribution groups, a service account by name, the domain's policies, <c>/ad</c>'s new forms and <c>to</c>.
/// </summary>
public sealed partial class ActiveDirectoryTests
{
    private const string Neon = "OU=Neon," + Base;
    private const string Engineering = "OU=Engineering," + Neon;
    private const string Sales = "OU=Sales," + Neon;
    private const string OldSales = "OU=Sales,OU=Old," + Base;
    private const string SalesEmea = "OU=Sales/EMEA," + Neon;
    private const string Users = "CN=Users," + Base;
    private const string Erin = "CN=Erin Ng," + Engineering;
    private const string Frank = "CN=Frank Ode," + Sales;
    private const string Grace = "CN=Grace Hu," + Neon;
    private const string Henry = "CN=Henry Ash," + Neon;
    private const string AllHands = "CN=All-Hands," + Neon;
    private const string DomainUsers = "CN=Domain Users," + Users;
    private const string DomainAdmins = "CN=Domain Admins," + Users;
    private const string Administrators = "CN=Administrators,CN=Builtin," + Base;
    private const string Tier0 = "CN=Tier0," + Neon;
    private const string Gmsa = "CN=svc_web,CN=Managed Service Accounts," + Base;
    private const string Vendor = "CN=Vendor Rep," + Neon;
    private const string Foreign = "CN=S-1-5-21-9-9-9-1001,CN=ForeignSecurityPrincipals," + Base;
    private const string Remote = "CN=Remote User,CN=Users,DC=other,DC=example";
    private const string DomainSid = "S-1-5-21-1-2-3";

    private static byte[] Sid(string sid) => AdValues.SidBytes(sid)!;

    private static byte[] Rid(uint rid) => Sid(DomainSid + "-" + rid.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static readonly string[] UserChain = ["top", "person", "organizationalPerson", "user"];

    private void Classes(string dn, params string[] chain)
    {
        _system.Entries[dn]["objectClass"] = [.. chain.Select(FakeDirectorySystem.B)];
    }

    /// <summary>The gaps' fixture over the first one: OUs, Domain Users by RID, a distribution group with outsiders, protected groups, a cycle.</summary>
    private void Gaps()
    {
        var now = _time.GetUtcNow();
        string Ft(DateTimeOffset moment) => AdValues.ToFileTime(moment).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _system.Entries[Base]["objectSid"] = [Sid(DomainSid)];
        _system.Add(Neon, ("objectClass", "top"), ("objectClass", "organizationalUnit"), ("ou", "Neon"), ("description", "Neon Corp"));
        _system.Add(Engineering, ("objectClass", "top"), ("objectClass", "organizationalUnit"), ("ou", "Engineering"), ("description", "The builders"), ("managedBy", Erin),
            ("whenCreated", AdValues.ToGeneralizedTime(now.AddDays(-2))),
            ("gPLink", "[LDAP://cn={A1},cn=policies,cn=system," + Base + ";1][LDAP://cn={B2},cn=policies,cn=system," + Base + ";2]"));
        _system.Add(Sales, ("objectClass", "top"), ("objectClass", "organizationalUnit"), ("ou", "Sales"), ("whenCreated", AdValues.ToGeneralizedTime(now.AddDays(-400))));
        _system.Add("OU=Old," + Base, ("objectClass", "top"), ("objectClass", "organizationalUnit"), ("ou", "Old"));
        _system.Add(OldSales, ("objectClass", "top"), ("objectClass", "organizationalUnit"), ("ou", "Sales"), ("description", "The old sales"));
        _system.Add(SalesEmea, ("objectClass", "top"), ("objectClass", "organizationalUnit"), ("ou", "Sales/EMEA"));
        _system.Add(Users, ("objectClass", "top"), ("objectClass", "container"), ("cn", "Users"));
        _system.Add("CN=System," + Base, ("objectClass", "top"), ("objectClass", "container"), ("cn", "System"), ("showInAdvancedViewOnly", "TRUE"));
        _system.Add("CN=Builtin," + Base, ("objectClass", "top"), ("objectClass", "builtinDomain"), ("cn", "Builtin"));
        _system.Add(DomainUsers, ("objectClass", "top"), ("objectClass", "group"), ("sAMAccountName", "Domain Users"), ("cn", "Domain Users"), ("objectSid", Rid(513)),
            ("groupType", "-2147483646"), ("isCriticalSystemObject", "TRUE"));
        _system.Add(Erin, ("sAMAccountName", "erin"), ("displayName", "Erin Ng"), ("userAccountControl", "66048"), ("department", "Engineering"), ("title", "Engineer"),
            ("physicalDeliveryOfficeName", "Austin"), ("primaryGroupID", "513"), ("objectSid", Rid(1101)), ("whenCreated", AdValues.ToGeneralizedTime(now.AddDays(-3))),
            ("lastLogonTimestamp", Ft(now.AddDays(-1))), ("tokenGroups", Rid(513)));
        Classes(Erin, UserChain);
        _system.Add(Frank, ("sAMAccountName", "frank"), ("displayName", "Frank Ode"), ("userAccountControl", "512"), ("primaryGroupID", "513"), ("objectSid", Rid(1102)),
            ("accountExpires", Ft(now.AddDays(10))), ("department", "Sales"), ("whenCreated", AdValues.ToGeneralizedTime(now.AddDays(-90))));
        Classes(Frank, UserChain);
        _system.AddReport(Erin, Frank);
        _system.Add(Grace, ("sAMAccountName", "grace"), ("displayName", "Grace Hu"), ("userAccountControl", "512"), ("primaryGroupID", "513"), ("objectSid", Rid(1103)));
        Classes(Grace, UserChain);
        _system.Add(Henry, ("sAMAccountName", "henry"), ("displayName", "Henry Ash"), ("userAccountControl", "512"), ("primaryGroupID", "1500"), ("objectSid", Rid(1104)),
            ("accountExpires", Ft(now.AddDays(-5))));
        Classes(Henry, UserChain);
        _system.Add(AllHands, ("sAMAccountName", "All-Hands"), ("cn", "All-Hands"), ("description", "Company-wide mail list"), ("groupType", "8"), ("mail", "all@lab.example"),
            ("managedBy", Erin), ("objectSid", Rid(1600)), ("whenCreated", AdValues.ToGeneralizedTime(now.AddDays(-30))));
        Classes(AllHands, "top", "group");
        _system.Add(Vendor, ("cn", "Vendor Rep"), ("displayName", "Vendor Rep"), ("mail", "rep@vendor.example"), ("description", "Our vendor's contact"));
        Classes(Vendor, "top", "person", "organizationalPerson", "contact");
        _system.Add(Foreign, ("cn", "S-1-5-21-9-9-9-1001"));
        Classes(Foreign, "top", "foreignSecurityPrincipal");
        _system.Add(Gmsa, ("sAMAccountName", "svc_web$"), ("cn", "svc_web"), ("userAccountControl", "4096"), ("description", "The web pool's account"), ("objectSid", Rid(1700)));
        Classes(Gmsa, "top", "person", "organizationalPerson", "user", "computer", "msDS-GroupManagedServiceAccount");
        _system.AddMember(AllHands, Erin);
        _system.AddMember(AllHands, Vendor);
        _system.AddMember(AllHands, Foreign);
        _system.AddMember(AllHands, Gmsa);
        _system.Entries[AllHands]["member"].Add(FakeDirectorySystem.B(Remote));
        _system.Add(DomainAdmins, ("sAMAccountName", "Domain Admins"), ("cn", "Domain Admins"), ("objectSid", Rid(512)), ("adminCount", "1"), ("groupType", "-2147483646"));
        Classes(DomainAdmins, "top", "group");
        _system.Add(Tier0, ("sAMAccountName", "Tier0"), ("cn", "Tier0"), ("objectSid", Rid(1200)), ("adminCount", "1"));
        Classes(Tier0, "top", "group");
        _system.Add(Administrators, ("sAMAccountName", "Administrators"), ("cn", "Administrators"), ("objectSid", Sid("S-1-5-32-544")), ("adminCount", "1"));
        Classes(Administrators, "top", "group");
        _system.Add("CN=Administrator," + Users, ("sAMAccountName", "Administrator"), ("displayName", "Administrator"), ("objectSid", Rid(500)), ("adminCount", "1"), ("userAccountControl", "512"));
        Classes("CN=Administrator," + Users, UserChain);
        _system.Add("CN=krbtgt," + Users, ("sAMAccountName", "krbtgt"), ("objectSid", Rid(502)), ("adminCount", "1"), ("userAccountControl", "514"));
        Classes("CN=krbtgt," + Users, UserChain);
        _system.Add("CN=Old Admin," + Neon, ("sAMAccountName", "oldadmin"), ("displayName", "Old Admin"), ("objectSid", Rid(1300)), ("adminCount", "1"), ("userAccountControl", "512"));
        Classes("CN=Old Admin," + Neon, UserChain);
        _system.AddMember(Tier0, Grace);
        _system.AddMember(DomainAdmins, Tier0);
        _system.AddMember(Administrators, DomainAdmins);
        _system.AddMember(Administrators, "CN=Administrator," + Users);
        _system.Add("CN=Unused," + Neon, ("sAMAccountName", "Unused"), ("cn", "Unused"), ("objectSid", Rid(1400)), ("groupType", "-2147483646"));
        Classes("CN=Unused," + Neon, "top", "group");
        _system.Add("CN=Primary Only," + Neon, ("sAMAccountName", "Primary Only"), ("cn", "Primary Only"), ("objectSid", Rid(1500)), ("groupType", "-2147483646"));
        Classes("CN=Primary Only," + Neon, "top", "group");
        _system.Add("CN=Loop A," + Neon, ("sAMAccountName", "Loop A"), ("cn", "Loop A"));
        Classes("CN=Loop A," + Neon, "top", "group");
        _system.Add("CN=Loop B," + Neon, ("sAMAccountName", "Loop B"), ("cn", "Loop B"));
        Classes("CN=Loop B," + Neon, "top", "group");
        _system.AddMember("CN=Loop A," + Neon, "CN=Loop B," + Neon);
        _system.AddMember("CN=Loop B," + Neon, "CN=Loop A," + Neon);
        _system.AddMember("CN=Loop B," + Neon, Grace);
        string settings = "CN=Password Settings Container,CN=System," + Base;
        _system.Add(settings, ("objectClass", "container"), ("cn", "Password Settings Container"));
        _system.Add("CN=ShortPasswords," + settings, ("objectClass", "msDS-PasswordSettings"), ("name", "ShortPasswords"), ("msDS-PasswordSettingsPrecedence", "10"),
            ("msDS-MaximumPasswordAge", "-25920000000000"), ("msDS-MinimumPasswordLength", "12"), ("msDS-LockoutThreshold", "5"), ("msDS-LockoutDuration", "-6000000000"),
            ("msDS-LockoutObservationWindow", "-6000000000"), ("msDS-PSOAppliesTo", AllHands));
        _system.Add("CN=Hidden," + settings, ("objectClass", "msDS-PasswordSettings"));
    }

    private DirectoryAnswer GroupOf(string name, bool nested = false, string members = "all", int limit = 100) =>
        Run(s => DirectoryQueries.GroupAnswer(s, Source(), name, nested, members, limit));

    private DirectoryAnswer FindOf(FindOptions options) => Run(s => DirectoryQueries.FindAnswer(s, Source(), options, _time.GetUtcNow(), TimeZoneInfo.Utc));

    private static string Model(DirectoryAnswer answer) => DirectoryRender.ForModel(answer);

    // ── Values ─────────────────────────────────────────────────────────────

    [Fact]
    public void Values_GroupKindsRidsSidsPathsAndTimes()
    {
        Assert.Equal("security global", AdValues.GroupKind(-2147483646));
        Assert.Equal("distribution universal", AdValues.GroupKind(8));
        Assert.Equal("security domain local, builtin", AdValues.GroupKind(-2147483643));
        Assert.Equal(513u, AdValues.Rid(Rid(513)));
        Assert.Equal("S-1-5-32-544", AdValues.Sid(Sid("S-1-5-32-544")));
        Assert.Null(AdValues.SidBytes("not a sid"));
        Assert.Equal("Neon/Sales\\/EMEA", AdValues.CanonicalPath(SalesEmea, Base));
        Assert.Equal(["Neon", "Sales/EMEA"], AdValues.PathParts("Neon/Sales\\/EMEA"));
        Assert.Equal(["a\\b"], AdValues.PathParts("a\\\\b"));
        Assert.Equal("Neon/Engineering", AdValues.PathOfCanonical("lab.example/Neon/Engineering"));
        Assert.Equal("20261015123456.0Z", AdValues.ToGeneralizedTime(new DateTimeOffset(2026, 10, 15, 12, 34, 56, TimeSpan.Zero)));
    }

    /// <summary>Each kind held to its real <c>objectClass</c> chain: a gMSA's holds computer and user, a computer's user, a contact's person.</summary>
    [Fact]
    public void Kind_EachHeldToItsRealChain()
    {
        Assert.Equal(AdValues.KindServiceAccount, AdValues.KindOf(["top", "person", "organizationalPerson", "user", "computer", "msDS-GroupManagedServiceAccount"]));
        Assert.Equal("computer", AdValues.KindOf(["top", "person", "organizationalPerson", "user", "computer"]));
        Assert.Equal("user", AdValues.KindOf(UserChain));
        Assert.Equal("contact", AdValues.KindOf(["top", "person", "organizationalPerson", "contact"]));
        Assert.Equal("group", AdValues.KindOf(["top", "group"]));
        Assert.Equal("foreign", AdValues.KindOf(["top", "foreignSecurityPrincipal"]));
        Assert.Equal("ou", AdValues.KindOf(["top", "organizationalUnit"]));
    }

    [Fact]
    public void GpLinks_CountsAndFlags()
    {
        Assert.Equal((2, 1, 1), AdValues.GpLinks("[LDAP://cn={A},cn=policies,cn=system,DC=x;1][LDAP://cn={B},cn=policies,cn=system,DC=x;2]"));
        Assert.Equal((1, 0, 0), AdValues.GpLinks("[LDAP://cn={A},cn=policies,cn=system,DC=x;0]"));
        Assert.Equal((0, 0, 0), AdValues.GpLinks(""));
        Assert.Equal((0, 0, 0), AdValues.GpLinks(null));
    }

    [Fact]
    public void Filters_EscapeEveryNewValue()
    {
        const string Evil = "x)(cn=*";
        const string Escaped = @"x\29\28cn=\2a";
        Assert.Contains(Escaped, LdapFilter.NameContains(Evil, "group"));
        Assert.Contains(Escaped, LdapFilter.NameContains(Evil, "ou"));
        Assert.Contains(Escaped, LdapFilter.FieldContains("user", "department", Evil));
        Assert.Contains(Escaped, LdapFilter.OsContains(Evil));
        Assert.Contains(Escaped, LdapFilter.OuNamed(Evil));
        Assert.Contains(Escaped, LdapFilter.ByDn(LdapFilter.Groups, Evil));
        Assert.Contains(Escaped, LdapFilter.ByDns([Evil]));
        Assert.DoesNotContain(Evil, LdapFilter.ByDns([Evil]));
        Assert.Equal("(&(objectCategory=person)(objectClass=user)(|(primaryGroupID=513)(primaryGroupID=1500)))", LdapFilter.PrimaryGroupIn(LdapFilter.Users, [513u, 1500u]));
        Assert.Contains("(objectCategory=computer)", LdapFilter.ComputerByName("svc_web"));
        Assert.Contains("msDS-GroupManagedServiceAccount", LdapFilter.ComputerByName("svc_web"));
        Assert.Equal("(objectCategory=computer)", LdapFilter.Computers);   // machines, still, for the stale and OS kinds
    }

    // ── Groups ─────────────────────────────────────────────────────────────

    [Fact]
    public void Group_TheCardsRows()
    {
        Gaps();
        var answer = GroupOf("All-Hands");
        string text = Model(answer);
        Assert.Contains("\n  description: ⟪Company-wide mail list⟫\n  type: distribution universal\n  managed by: ⟪Erin Ng⟫\n  mail: ⟪all@lab.example⟫\n  created: ", text);
        Assert.Equal(Erin, answer.Fields.Single(f => f.Label == "managed by").Target!.Dn);
        string inner = Model(GroupOf("Inner", nested: true));
        Assert.Contains("\n  member of (1, nested): ⟪Ops⟫", inner);
    }

    [Fact]
    public void Group_MemberOf_Upward()
    {
        Gaps();
        _system.Add("CN=Top," + Neon, ("sAMAccountName", "Top"), ("cn", "Top"));
        Classes("CN=Top," + Neon, "top", "group");
        _system.AddMember("CN=Top," + Neon, Ops);
        Assert.Contains("\n  member of (1): ⟪Ops⟫", Model(GroupOf("Inner")));
        var nested = GroupOf("Inner", nested: true);
        Assert.Contains("\n  member of (2, nested): ⟪Ops⟫, ⟪Top⟫", Model(nested));
        Assert.Equal([Ops, "CN=Top," + Neon], nested.Fields.Single(f => f.Label.StartsWith("member of", StringComparison.Ordinal)).Links!.Rows.Select(r => r.Target!.Dn));
    }

    [Fact]
    public void Group_Direct_KindsAndOutsiders()
    {
        Gaps();
        var answer = GroupOf("All-Hands");
        string text = Model(answer);
        Assert.StartsWith("Group ⟪All-Hands⟫ in lab.example: 5 members, direct\n", text);
        Assert.Contains("\n  ⟪Erin Ng⟫ (user erin)\n", text);
        Assert.Contains("\n  ⟪Vendor Rep⟫ (contact)\n", text);
        Assert.Contains("\n  ⟪S-1-5-21-9-9-9-1001⟫ (foreign)\n", text);
        Assert.Contains("\n  ⟪svc_web⟫ (service account svc_web$)\n", text);
        Assert.Contains("\n  ⟪Remote User⟫ (" + DirectoryText.OtherDomain + ")\n", text);
        var rows = answer.Tables[0].Rows;
        Assert.Null(rows.Single(r => r.Name == "Vendor Rep").Target);
        Assert.Null(rows.Single(r => r.Name == "Remote User").Target);
        Assert.Equal(Gmsa, rows.Single(r => r.Name == "svc_web").Target!.Dn);
        Assert.Contains("\n  ⟪Erin Ng⟫ (user erin)", Model(GroupOf("All-Hands", members: "users")));
        Assert.DoesNotContain("Vendor", Model(GroupOf("All-Hands", members: "users")));
        Assert.DoesNotContain("svc_web", Model(GroupOf("All-Hands", members: "computers")));   // a gMSA's category is its own
        _system.Entries.Remove(Vendor);
        Assert.Contains("\n  ⟪Vendor Rep⟫ (" + DirectoryText.NotReadable + ")\n", Model(GroupOf("All-Hands")));   // under the base but not returned
    }

    [Fact]
    public void Group_Nested_UsersOnly_WithVia()
    {
        Gaps();
        string text = Model(GroupOf("Ops", nested: true, members: "users"));
        Assert.StartsWith("Group ⟪Ops⟫ in lab.example: 4 users, nested (3 direct, 1 through other groups)\n", text);
        Assert.Contains("\n  ⟪Dave Lee⟫ (user dave) via ⟪Inner⟫\n", text);
        Assert.Contains("\n  ⟪Alice Smith⟫ (user alice) direct\n", text);
        Assert.DoesNotContain("(group Inner)", text);
        Assert.Contains("(group Inner)", Model(GroupOf("Ops", members: "groups")));
    }

    [Fact]
    public void Group_PrimaryGroupMembers()
    {
        Gaps();
        var answer = GroupOf("Domain Users");
        string text = Model(answer);
        Assert.StartsWith("Group ⟪Domain Users⟫ in lab.example: 3 members, direct (0 in its member list, 3 by primary group)\n", text);
        Assert.Contains("\n  ⟪Erin Ng⟫ (user erin) via ⟪Domain Users⟫ (" + DirectoryText.PrimaryGroupWord + ")\n", text);
        Assert.Contains(_system.Filters, f => f.Contains("(primaryGroupID=513)", StringComparison.Ordinal));
        Assert.Contains("1 member, direct (0 in its member list, 1 by primary group)", Model(GroupOf("Primary Only")));
    }

    [Fact]
    public void Group_Nested_ACycleEnds_AndTheWalkIsCapped()
    {
        Gaps();
        string loop = Model(GroupOf("Loop A", nested: true));
        Assert.Contains("\n  ⟪Loop B⟫ (group Loop B) direct\n", loop);
        Assert.Contains("\n  ⟪Grace Hu⟫ (user grace) via ⟪Loop B⟫\n", loop);
        Assert.Contains("\n  ⟪Loop A⟫ (group Loop A) via ⟪Loop B⟫\n", loop);   // the group itself, met again, ends the walk
        string previous = Ops;
        for (int i = 0; i < DirectoryLimits.MaxWalkedGroups + 5; i++)
        {
            string chain = "CN=Chain " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + Neon;
            _system.Add(chain, ("sAMAccountName", "Chain" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Classes(chain, "top", "group");
            _system.AddMember(previous, chain);
            previous = chain;
        }

        Assert.Contains(DirectoryText.GroupsNotWalked, Model(GroupOf("Ops", nested: true)).Split('\n')[0]);
    }

    [Fact]
    public void Group_Nested_BatchesFiftyGroups()
    {
        Gaps();
        for (int i = 0; i < 120; i++)
        {
            string user = "CN=Many " + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture) + "," + Neon;
            _system.Add(user, ("sAMAccountName", "many" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture)));
            Classes(user, UserChain);
            _system.AddMember(AllHands, user);
        }

        _system.Filters.Clear();
        var answer = GroupOf("All-Hands", nested: true, limit: 10);
        Assert.Equal(3, _system.Filters.Count(f => f.StartsWith("(|(distinguishedName=", StringComparison.Ordinal)));   // 125 members: 50, 50, 25
        Assert.Contains("125 members, nested", Model(answer));
        Assert.Contains("; " + DirectoryText.FirstListed(10, false), Model(answer));
        Assert.Equal(10, answer.Tables[0].ShownRows.Count());
        Assert.Equal(125, answer.Tables[0].Rows.Count);   // every one read, for the total and a save
    }

    /// <summary>A walk under a deadline: each search's limit shrinks, none starts with under two seconds left, the answer says time; a cancelled token stops it too.</summary>
    [Fact]
    public void Group_Nested_StopsAtTheTimeBudget_OrOnCancel()
    {
        Gaps();
        var deadline = DirectoryDeadline.From(_time, TimeSpan.FromSeconds(20), CancellationToken.None);
        _system.OnSearch = _ => _time.Advance(TimeSpan.FromSeconds(6));
        _system.TimeLimits.Clear();
        var answer = Run(s => DirectoryQueries.GroupAnswer(s, Source(), "Loop A", nested: true, "all", 100, deadline: deadline));
        Assert.Contains(DirectoryText.StoppedAtTimeLimit, Model(answer).Split('\n')[0]);
        Assert.True(answer.Partial);
        var limits = _system.TimeLimits.Select(l => l!.Value).ToList();
        Assert.True(limits.Count >= 3);
        Assert.Equal(limits.OrderByDescending(l => l), limits);
        Assert.All(limits, l => Assert.True(l >= DirectoryLimits.LeastSearchTime && l <= DirectoryLimits.SearchTime, l.ToString()));
        _system.OnSearch = null;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        _system.Filters.Clear();
        var stopped = Run(s => DirectoryQueries.GroupAnswer(s, Source(), "Ops", nested: true, "all", 100, deadline: DirectoryDeadline.From(_time, TimeSpan.FromSeconds(40), cancel.Token)));
        Assert.Contains(DirectoryText.StoppedAtTimeLimit, Model(stopped));
        Assert.Single(_system.Filters);   // the group's own lookup, then nothing
    }

    // ── Finds ──────────────────────────────────────────────────────────────

    [Fact]
    public void Find_GroupsByNameAndDescription()
    {
        Gaps();
        var answer = FindOf(new FindOptions("name", 0, "mail list", 50, Type: "group"));
        string text = Model(answer);
        Assert.StartsWith("groups whose names or descriptions hold \"mail list\" in lab.example: 1\n", text);
        Assert.Contains("\n  All-Hands  ⟪All-Hands⟫  distribution universal  ⟪Company-wide mail list⟫\n", text);
        Assert.Equal([DirectoryText.ColAccount, DirectoryText.ColName, DirectoryText.ColType, DirectoryText.ColDescription], answer.Tables[0].Columns.Select(c => c.Name));
    }

    [Fact]
    public void Find_OusByNameAndDescription_AndEachTypesFields()
    {
        Gaps();
        var answer = FindOf(new FindOptions("name", 0, "eng", 50, Type: "ou"));
        Assert.Contains("\n  ⟪Neon/Engineering⟫  ⟪The builders⟫\n", Model(answer));
        Assert.Equal([DirectoryText.ColPath, DirectoryText.ColDescription], answer.Tables[0].Columns.Select(c => c.Name));
        Assert.Equal(Engineering, answer.Tables[0].Rows.Single().Target!.Dn);
        Assert.Contains("⟪Old/Sales⟫  ⟪The old sales⟫", Model(FindOf(new FindOptions("field", 0, "old sales", 50, Type: "ou", Field: "description"))));
        foreach (string type in (string[])["ou", "group", "computer", "service"])
        {
            Assert.Equal(DirectoryText.FieldNotForType("department", type, ["description"]), DirectoryQueries.FindProblem("field", false, "x", type, "department", null, false));
        }

        foreach (string field in DirectoryQueries.FieldNames)
        {
            Assert.Null(DirectoryQueries.FindProblem("field", false, "x", "contact", field, null, false));
        }

        string bare = Model(FindOf(new FindOptions("name", 0, "e", 50)));
        Assert.DoesNotContain("Engineering⟫  ⟪The", bare);   // a bare name search leaves OUs out,
        Assert.DoesNotContain("Vendor Rep", bare);            // contacts
        Assert.DoesNotContain("svc_web", bare);               // and service accounts
    }

    [Fact]
    public void Find_ContactsAndServiceAccounts()
    {
        Gaps();
        string contacts = Model(FindOf(new FindOptions("name", 0, "vendor", 50, Type: "contact")));
        Assert.Contains("\n  ⟪Vendor Rep⟫  ⟪Our vendor's contact⟫\n", contacts);   // a contact has no account: the name alone
        string services = Model(FindOf(new FindOptions("name", 0, "svc", 50, Type: "service")));
        Assert.Contains("\n  svc_web$  ⟪svc_web⟫  ⟪The web pool's account⟫\n", services);
        Assert.Contains("contacts whose names or descriptions hold", contacts);
    }

    [Fact]
    public void Find_FieldOsAndOu_TheHeadNamesTheOu()
    {
        Gaps();
        Assert.Contains("\n  erin  ⟪Erin Ng⟫  ⟪Engineering⟫\n", Model(FindOf(new FindOptions("field", 0, "engin", 50, Field: "department"))));
        Assert.Contains("\n  WS01$  ⟪ws01.lab.example⟫  ⟪Windows 11 Pro⟫  last ", Model(FindOf(new FindOptions("os", 0, "windows 11", 50))));
        string scoped = Model(FindOf(new FindOptions("password_never_expires", 0, null, 50, Ou: "Neon/Engineering")));
        Assert.StartsWith("users whose password never expires in Neon/Engineering (lab.example): 1\n  erin  ⟪Erin Ng⟫", scoped);
        Assert.DoesNotContain("carol", scoped);
        Assert.Equal("the users whose password never expires in the OU Neon/Engineering", DirectoryCommand.Describe(DirectoryCommand.Parse("find password_never_expires ou:Neon/Engineering").Args!));
    }

    [Fact]
    public async Task Find_AnArgumentTheKindTakesNot_IsRefused()
    {
        Gaps();
        var tool = new AdFindTool(Access());
        Assert.Equal(DirectoryText.NotTaken("days", "locked", DirectoryQueries.DaysKinds), await Call(tool, ("kind", "locked"), ("days", 5)));
        Assert.Equal(DirectoryText.NotTaken("words", "disabled", DirectoryQueries.WordsKinds), await Call(tool, ("kind", "disabled"), ("words", "x")));
        Assert.Equal(DirectoryText.NotTaken("type", "os", DirectoryQueries.TypeKinds), await Call(tool, ("kind", "os"), ("words", "w"), ("type", "user")));
        Assert.Equal(DirectoryText.NotTaken("ou", "privileged", DirectoryQueries.OuKinds), await Call(tool, ("kind", "privileged"), ("ou", "Neon")));
        Assert.Equal(DirectoryText.WordsNeeded("os"), await Call(tool, ("kind", "os")));
        Assert.Equal(DirectoryText.BadChoice("type", "ouu", DirectoryQueries.FindTypes), await Call(tool, ("kind", "name"), ("words", "x"), ("type", "ouu")));
        Assert.Equal("Error: type 'ouu' is not user, group, computer, ou, contact or service.", DirectoryText.BadChoice("type", "ouu", DirectoryQueries.FindTypes));
        Assert.Equal(DirectoryText.Missing("field"), await Call(tool, ("kind", "field"), ("words", "x")));
        Assert.Equal(DirectoryText.BadChoice("members", "people", DirectoryQueries.MemberChoices), await Call(new AdGroupTool(Access()), ("name", "Ops"), ("members", "people")));
        Assert.Empty(_system.Filters);   // every refusal before a search
    }

    /// <summary>One list of kinds: the refusal, <c>/ad</c>'s usage, the help's form and the schema's enum all name exactly <see cref="DirectoryQueries.FindKinds"/>; the other enums their lists.</summary>
    [Fact]
    public void Find_TheKindsHaveOneList()
    {
        static IReadOnlyList<string> Enum(AIFunction tool, string property) =>
            tool.JsonSchema.GetProperty("properties").GetProperty(property).GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToList();
        var find = new AdFindTool(Access());
        Assert.Equal(DirectoryQueries.FindKinds, Enum(find, "kind"));
        Assert.Equal(DirectoryQueries.FindTypes, Enum(find, "type"));
        Assert.Equal(DirectoryQueries.FieldNames, Enum(find, "field"));
        Assert.Equal(DirectoryQueries.MemberChoices, Enum(new AdGroupTool(Access()), "members"));
        Assert.Equal("Error: kind 'x' is not " + DirectoryText.Join(DirectoryQueries.FindKinds) + ".", DirectoryText.BadKind("x"));
        Assert.Contains("find " + string.Join('|', DirectoryQueries.FindKinds) + " ", DirectoryCommand.Usage);
        Assert.Contains(HelpCommands.AsWritten.Single(c => c.Command == "/ad").Forms, f => f.Syntax.StartsWith("/ad find <kind> ", StringComparison.Ordinal)
            && f.Meaning.Contains("the kinds are " + DirectoryText.Join(DirectoryQueries.FindKinds) + ".", StringComparison.Ordinal));
        foreach (string kind in DirectoryQueries.FindKinds)
        {
            Assert.Contains(kind, DirectoryText.BadKind("x"));
        }
    }

    /// <summary>Every <c>/ad</c> verb and key word has a form in the help (a missing <c>/ad ou</c> would pass the general check).</summary>
    [Fact]
    public void Command_EveryVerbHasAHelpForm()
    {
        var forms = HelpCommands.AsWritten.Single(c => c.Command == "/ad").Forms.Select(f => f.Syntax).ToList();
        foreach (string verb in DirectoryCommand.Verbs)
        {
            Assert.Contains(forms, f => f.StartsWith("/ad " + verb, StringComparison.Ordinal));
        }

        foreach (string key in DirectoryCommand.Keys)
        {
            Assert.Contains(forms, f => f.Contains(key + ":", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Find_OfficeRefusedWhenRedacted()
    {
        Gaps();
        _config.Detail = "full";
        var tool = new AdFindTool(Access());
        Assert.Contains("erin", await Call(tool, ("kind", "field"), ("field", "office"), ("words", "austin")));
        Assert.Equal(DirectoryText.OfficeRedacted, await Call(tool.AsRedacted(), ("kind", "field"), ("field", "office"), ("words", "austin")));
        _config.Detail = null;
        Assert.Equal(DirectoryText.OfficeRedacted, await Call(new AdFindTool(Access()), ("kind", "field"), ("field", "office"), ("words", "austin")));   // the domain's own detail
    }

    [Fact]
    public void Find_TheAuditKinds()
    {
        Gaps();
        string never = Model(FindOf(new FindOptions("password_never_expires", 0, null, 50)));
        Assert.Contains("\n  carol  ⟪Carol King⟫\n", never);
        Assert.Contains("\n  erin  ⟪Erin Ng⟫\n", never);
        var expiring = FindOf(new FindOptions("account_expiring", 30, null, 50));
        string text = Model(expiring);
        Assert.StartsWith("accounts expiring within 30 days in lab.example: 1\n  frank  ⟪Frank Ode⟫  expires ", text);
        Assert.Contains("\nalready expired (1):\n  henry  ⟪Henry Ash⟫  expired ", text);
        var created = Model(FindOf(new FindOptions("created", 30, null, 50)));
        Assert.Contains("\n  erin  user  ⟪Erin Ng⟫  created ", created);
        Assert.DoesNotContain("frank", created);   // 90 days ago
        Assert.Contains("\n  ⟪Neon/Engineering⟫  ⟪The builders⟫  created ", Model(FindOf(new FindOptions("created", 30, null, 50, Type: "ou"))));
        Assert.Contains("\n  All-Hands  group  ⟪All-Hands⟫  created ", Model(FindOf(new FindOptions("created", 31, null, 50, Type: "group"))));
    }

    [Fact]
    public void Find_Privileged_BySid_WithVia_AndTheLeftovers()
    {
        Gaps();
        var answer = FindOf(new FindOptions("privileged", 0, null, 50));
        string text = Model(answer);
        Assert.StartsWith("privileged accounts in lab.example: 2\n", text);
        Assert.Contains("\n  grace  user  ⟪Grace Hu⟫  ⟪Domain Admins⟫, ⟪Administrators⟫ via ⟪Tier0⟫\n", text);
        Assert.Contains("\n  Administrator  user  ⟪Administrator⟫  ⟪Administrators⟫\n", text);
        Assert.Contains("\n" + DirectoryText.LeftoversTitle + " (2):\n", text);
        Assert.Contains("\n  krbtgt  user  ⟪krbtgt⟫  " + DirectoryText.ExpectedNote, text);
        Assert.Contains("\n  oldadmin  user  ⟪Old Admin⟫  " + DirectoryText.LeftoverNote, text);
        Assert.DoesNotContain("Tier0  group", text);          // a group nested in a protected one carries adminCount too: no leftover
        Assert.DoesNotContain("Domain Admins  group", text);  // nor the protected groups themselves
        Assert.Contains(DirectoryText.MissingProtected(["Schema Admins", "Enterprise Admins", "Key Admins", "Enterprise Key Admins", "Account Operators", "Server Operators", "Print Operators", "Backup Operators", "Replicator"]), text);
    }

    [Fact]
    public void Find_EmptyGroups_LeavesOutPrimaryGroups()
    {
        Gaps();
        string text = Model(FindOf(new FindOptions("empty_groups", 0, null, 50)));
        Assert.Contains("\n  Unused  ⟪Unused⟫  security global\n", text);
        Assert.DoesNotContain("Primary Only", text);   // henry's primary group
        Assert.DoesNotContain("Domain Users", text);   // a system object
    }

    // ── OUs ────────────────────────────────────────────────────────────────

    [Fact]
    public void Branch_PathNameDn_AndOutsideRefused()
    {
        Gaps();
        Assert.Equal(Engineering, Run(s => DirectoryQueries.Branch(s, Base, "Neon/Engineering")).Found!.Dn);
        Assert.Equal(Engineering, Run(s => DirectoryQueries.Branch(s, Base, "engineering")).Found!.Dn);
        Assert.Equal(Engineering, Run(s => DirectoryQueries.Branch(s, Base, Engineering)).Found!.Dn);
        Assert.Equal(SalesEmea, Run(s => DirectoryQueries.Branch(s, Base, "Neon/Sales\\/EMEA")).Found!.Dn);
        var (none, sales) = Run(s => DirectoryQueries.Branch(s, Base, "Sales"));
        Assert.Null(none);
        Assert.Equal(2, sales.Count);
        Assert.Equal(Sales, Run(s => DirectoryQueries.Branch(s, Base, "Neon/Sales")).Found!.Dn);
        Assert.Null(Run(s => DirectoryQueries.Branch(s, Base, "CN=Partitions,CN=Configuration,DC=other")).Found);   // outside the base: never searched there
        var answer = Run(s => DirectoryQueries.OuAnswer(s, Source(), "Sales", 100));
        Assert.Equal(DirectoryText.AmbiguousOu("Sales", ["⟪Neon/Sales⟫", "⟪Old/Sales⟫"]), Model(answer));
        Assert.Equal([Sales, OldSales], answer.Candidates!.Rows.Select(r => r.Target!.Dn));
        Assert.Equal(DirectoryText.AmbiguousOuTitle("Sales", 2), answer.CandidatesTitle);
        Assert.Equal(AdValues.CanonicalPath(SalesEmea, Base), AdValues.PathOfCanonical(Run(s => s.Search(Neon, DirectoryScope.OneLevel, "(ou=Sales/EMEA)", ["canonicalName"], 5, false)).Entries.Single().Text("canonicalName")!));
    }

    [Fact]
    public void Ou_TheTreeAndOneBranch()
    {
        Gaps();
        var tree = Run(s => DirectoryQueries.OuAnswer(s, Source(), null, 100));
        string text = Model(tree);
        Assert.StartsWith("OU tree of lab.example: ", text);
        Assert.Contains("\n  ⟪Neon⟫  ⟪Neon Corp⟫\n    ⟪Neon/Engineering⟫  ⟪The builders⟫\n", text);   // a branch's OUs follow it, indented
        Assert.Contains("\n  ⟪Builtin⟫", text);
        Assert.Contains("\n  ⟪Users⟫", text);
        Assert.DoesNotContain("⟪System⟫", text);   // advanced view only
        var branch = Run(s => DirectoryQueries.OuAnswer(s, Source(), "Neon", 100));
        string neon = Model(branch);
        Assert.StartsWith("OU ⟪Neon⟫ in lab.example: ", neon);
        Assert.Contains(" OUs", neon.Split('\n')[0]);
        var rows = branch.Tables[0].Rows;
        Assert.Equal("ou", rows[0].Cells[1]!.Text);   // the child OUs first
        Assert.Contains(rows, r => r.Cells[0]?.Text == "grace" && r.Target!.Dn == Grace);
        string engineering = Model(Run(s => DirectoryQueries.OuAnswer(s, Source(), "Neon/Engineering", 100)));
        Assert.Contains("\n  GPOs linked: 2 (1 disabled, 1 enforced)\n", engineering);
        Assert.Contains("\n  managed by: ⟪Erin Ng⟫\n", engineering);
        Assert.Contains("\n  erin  user  ⟪Erin Ng⟫", engineering);
        Assert.Contains("GPOs linked: none", Model(Run(s => DirectoryQueries.OuAnswer(s, Source(), "Neon", 100))));
    }

    // ── Users, computers, the domain ───────────────────────────────────────

    [Fact]
    public void User_DirectReportsAndDistributionGroups()
    {
        Gaps();
        var erin = Run(s => DirectoryQueries.UserAnswer(s, Source(), "erin", _time.GetUtcNow(), TimeZoneInfo.Utc));
        string text = Model(erin);
        Assert.Contains("\n  direct reports (1): ⟪Frank Ode⟫\n", text);
        Assert.Contains("\n  groups (2, nested): ⟪All-Hands⟫, ⟪Domain Users⟫\n", text);   // tokenGroups' security group and the direct distribution one
        Assert.Equal(Frank, erin.Fields.Single(f => f.Label.StartsWith("direct reports", StringComparison.Ordinal)).Links!.Rows.Single().Target!.Dn);
        var frank = Run(s => DirectoryQueries.UserAnswer(s, Source(), "frank", _time.GetUtcNow(), TimeZoneInfo.Utc));
        Assert.Contains("\n  groups (1, direct): ⟪Domain Users⟫\n", Model(frank));   // no tokenGroups: memberOf and the primary group by RID
        Assert.Equal(Erin, frank.Fields.Single(f => f.Label == "manager").Target!.Dn);
    }

    [Fact]
    public void Computer_ManagedByAndGroups_AndAServiceAccountByName()
    {
        Gaps();
        _system.Entries["CN=WS01,OU=Workstations," + Base]["managedBy"] = [FakeDirectorySystem.B(Erin)];
        _system.AddMember(AllHands, "CN=WS01,OU=Workstations," + Base);
        string ws01 = Run(s => DirectoryQueries.Computer(s, Source(), "ws01", TimeZoneInfo.Utc));
        Assert.Contains("\n  managed by: ⟪Erin Ng⟫\n", ws01);
        Assert.Contains("\n  groups (1): ⟪All-Hands⟫\n", ws01);
        string service = Run(s => DirectoryQueries.Computer(s, Source(), "svc_web$", TimeZoneInfo.Utc));
        Assert.StartsWith("Service account ⟪svc_web⟫ in lab.example: enabled", service);
        Assert.StartsWith("Service account ⟪svc_web⟫", Run(s => DirectoryQueries.Computer(s, Source(), "svc_web", TimeZoneInfo.Utc)));
        Assert.Contains("WS01$", Model(FindOf(new FindOptions("stale_computers", 90, null, 50))));
        Assert.DoesNotContain("svc_web", Model(FindOf(new FindOptions("stale_computers", 90, null, 50))));   // machines alone
    }

    [Fact]
    public void Domain_ThePolicies()
    {
        Gaps();
        var answer = Run(s => DirectoryQueries.DomainAnswer(s, Source()));
        string text = Model(answer);
        Assert.Contains("\n" + DirectoryText.PoliciesTitle(2) + ":\n  ⟪ShortPasswords⟫  precedence 10, maximum age 30 days, at least 12 characters, lockout after 5 bad passwords within 10 minutes, for 10 minutes; applies to ⟪All-Hands⟫\n", text);
        Assert.Contains("\n  ⟪Hidden⟫  " + DirectoryText.PolicyUnreadable, text);
        Assert.Equal("CN=DC1,OU=Domain Controllers," + Base, answer.Fields.Single(f => f.Label == "domain controllers").Links!.Rows.Single().Target!.Dn);
    }

    // ── /ad's new forms ────────────────────────────────────────────────────

    [Fact]
    public void Command_TheNewForms()
    {
        DirectoryCommandArgs Parse(string args) => DirectoryCommand.Parse(args).Args!;
        Assert.Equal(("group", "Domain Users"), (Parse("group Domain Users").Verb, Parse("group Domain Users").Argument));   // bare words stay the name
        Assert.Equal("Domain Users", Parse("group \"Domain Users\" nested members:users").Argument);
        Assert.Equal(("users", true), (Parse("group \"Domain Users\" nested members:users").Members, Parse("group \"Domain Users\" nested members:users").Nested));
        Assert.Equal("Neon/Big Team", Parse("find stale_users ou:\"Neon/Big Team\"").Ou);
        Assert.Equal("my list.csv", Parse("group Staff to:\"my list.csv\"").To);
        Assert.Equal(@"D:\work\x.csv", Parse(@"group Staff to:D:\work\x.csv").To);   // the first colon splits
        Assert.Equal("Sales:EMEA", Parse("group Sales:EMEA").Argument);              // an unknown key stays a name
        Assert.Equal(("name", "carol", "lab"), (Parse("domain:lab carol").Verb, Parse("domain:lab carol").Argument, Parse("domain:lab carol").Domain));
        Assert.Equal(("find:locked", "lab"), (Parse("domain:lab find locked").Verb, Parse("domain:lab find locked").Domain));
        Assert.Equal(("find:name", "2024"), (Parse("find name 2024").Verb, Parse("find name 2024").Argument));   // a number is days only for a kind that takes days
        Assert.Equal(30, Parse("find account_expiring").Days);
        Assert.Equal(14, Parse("find password_expiring").Days);
        Assert.Equal(("ou", ""), (Parse("ou").Verb, Parse("ou").Argument));
        Assert.Equal(DirectoryCommand.KeyNotTaken("members", "user"), DirectoryCommand.Parse("user bob members:users").Error);
        Assert.Equal("the users in Staff at any depth", DirectoryCommand.Describe(Parse("group Staff nested members:users")));
        Assert.Equal("the groups whose names hold \"vpn\"", DirectoryCommand.Describe(Parse("find name vpn type:group")));
        Assert.Equal("the OU Neon/Engineering", DirectoryCommand.Describe(Parse("ou Neon/Engineering")));
        Assert.Equal("the privileged accounts", DirectoryCommand.Describe(Parse("find privileged")));
        Assert.Equal("the group Staff", DirectoryCommand.Describe(Parse("group Staff to:x.csv")));   // to is left out
        Assert.Equal(["domain:lab find "], DirectoryCommand.Complete("domain:lab fi", ["lab"]).Select(c => c.Text.TrimEnd() + " "));
        Assert.Contains("domain:lab find privileged", DirectoryCommand.Complete("domain:lab find pri", ["lab"]).Select(c => c.Text));
        Assert.Contains("find stale_users type:group", DirectoryCommand.Complete("find stale_users type:gr", ["lab"]).Select(c => c.Text));
        Assert.Contains("group Ops members:users", DirectoryCommand.Complete("group Ops members:u", ["lab"]).Select(c => c.Text));
        Assert.Equal(90, DirectoryQueries.DefaultDays("stale_users"));
    }

    // ── to ─────────────────────────────────────────────────────────────────

    private string ToRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "neon-ad-to-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private readonly List<string> _roots = [];

    private Func<DirectorySaveRoot> Root(string root, bool fileTools = true, bool plan = false) =>
        () => NeonSidekick.App.ChatScreen.DirectorySaveRootFor(fileTools, plan, new WorkingDirectory(() => root, _time));

    [Fact]
    public async Task To_WritesEveryRow_AndRefusesAnExistingFile()
    {
        Gaps();
        string root = ToRoot();
        try
        {
            var tool = new AdGroupTool(Access(), saveRoot: Root(root));
            string answer = await Call(tool, ("name", "All-Hands"), ("limit", 2), ("to", "hands.csv"));
            Assert.EndsWith("\n" + DirectoryText.Saved(5, "hands.csv", false), answer);
            var lines = File.ReadAllLines(Path.Combine(root, "hands.csv"));
            Assert.Equal(6, lines.Length);   // the header and every member, not the two listed
            Assert.Equal("name,kind,account,via,dn", lines[0]);   // the BOM read away
            _system.Filters.Clear();
            Assert.StartsWith(FileText.Error(FileOutcome.Exists, "hands.csv", "write"), await Call(tool, ("name", "All-Hands"), ("to", "hands.csv")));
            Assert.Empty(_system.Filters);   // refused before any search
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task To_RefusedWithoutFileTools_InPlanMode_AndInTheTwin()
    {
        Gaps();
        string root = ToRoot();
        try
        {
            Assert.Equal(DirectoryText.SaveNeedsFileTools, await Call(new AdFindTool(Access(), saveRoot: Root(root, fileTools: false)), ("kind", "disabled"), ("to", "x.csv")));
            Assert.Equal(DirectoryText.SaveInPlanMode, await Call(new AdOuTool(Access(), saveRoot: Root(root, plan: true)), ("to", "x.csv")));
            Assert.Equal(DirectoryText.SaveNotHere, await Call(new AdGroupTool(Access(), saveRoot: Root(root)).AsRedacted(), ("name", "Ops"), ("to", "x.csv")));
            Assert.Empty(_system.Filters);
            Assert.Empty(Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task To_StopsAtTheCap_AndPastTwoHundredThousandCharacters()
    {
        Gaps();
        for (int i = 0; i < DirectoryLimits.MaxCounted + 5; i++)
        {
            string user = "CN=Bulk " + i.ToString("00000", System.Globalization.CultureInfo.InvariantCulture) + " " + new string('x', 20) + "," + Neon;
            _system.Add(user, ("sAMAccountName", "bulk" + i.ToString("00000", System.Globalization.CultureInfo.InvariantCulture)), ("userAccountControl", "514"));
            Classes(user, UserChain);
        }

        string root = ToRoot();
        try
        {
            string answer = await Call(new AdFindTool(Access(), saveRoot: Root(root)), ("kind", "disabled"), ("to", "bulk.csv"));
            Assert.Contains(DirectoryText.CapNote(DirectoryLimits.MaxCounted, false), answer.Split('\n')[0]);
            Assert.EndsWith(DirectoryText.Saved(DirectoryLimits.MaxCounted, "bulk.csv", true), answer);
            Assert.True(new FileInfo(Path.Combine(root, "bulk.csv")).Length > 200_000);   // WriteText's cap never applies
            string capped = await Call(new AdFindTool(Access(), saveRoot: Root(root)), ("kind", "disabled"));
            Assert.Contains(DirectoryText.CapNote(DirectoryLimits.MaxEntries, true), capped.Split('\n')[0]);   // the save offered: to could read more
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task To_NothingOnARefusalOrNoRows_AndAFolderGetsANameFromTheQuestion_NumberedWhenTaken()
    {
        Gaps();
        string root = ToRoot();
        try
        {
            var tool = new AdFindTool(Access(), saveRoot: Root(root));
            Assert.EndsWith(DirectoryText.NothingSaved, await Call(tool, ("kind", "name"), ("words", "nobody-at-all"), ("to", "none.csv")));
            Assert.False(File.Exists(Path.Combine(root, "none.csv")));
            Assert.Equal(DirectoryText.NotFound("group", "Nope", "lab.example"), await Call(new AdGroupTool(Access(), saveRoot: Root(root)), ("name", "Nope"), ("to", "nope.csv")));
            Assert.Empty(Directory.GetFiles(root));
            Directory.CreateDirectory(Path.Combine(root, "out"));
            Assert.EndsWith(DirectoryText.Saved(2, Path.Combine("out", "ad-find-disabled.csv"), false), await Call(tool, ("kind", "disabled"), ("to", "out")));
            Assert.EndsWith(DirectoryText.Saved(2, Path.Combine("out", "ad-find-disabled-2.csv"), false), await Call(tool, ("kind", "disabled"), ("to", "out")));
            Assert.Equal(DirectoryText.ToNeedsCsv("out.txt"), await Call(tool, ("kind", "disabled"), ("to", "out.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Command_ToWord_WritesThroughTheWorkingDirectory()
    {
        Gaps();
        string root = ToRoot();
        try
        {
            var files = new WorkingDirectory(() => root, _time);
            if (!OperatingSystem.IsWindows())
            {
                return;   // /ad answers "no directory here" off Windows
            }

            var result = await DirectoryCommand.RunAsync(Access(), "group Ops to:ops.csv", CancellationToken.None, () => files);
            Assert.False(result.Failed);
            Assert.Equal(DirectoryText.Saved(4, "ops.csv", false), result.Lines[^1]);
            Assert.True(File.Exists(Path.Combine(root, "ops.csv")));
            Assert.Contains(DirectoryText.ToNeedsCsv("ops.txt").Replace("Error: ", "", StringComparison.Ordinal), (await DirectoryCommand.RunAsync(Access(), "group Ops to:ops.txt", CancellationToken.None, () => files)).Lines[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheRule_NamesOuAndHowToAskForEveryNestedUser()
    {
        Assert.Contains(AdOuTool.ToolName, Assistant.DirectoryRule);
        Assert.Contains("Every user in a group at any depth is ad_group with nested and members: users", Assistant.DirectoryRule);
        Assert.Contains(AdOuTool.ToolName, HelpSettings.Describe(NeonSidekick.App.SettingsField.DirectoryTools));
    }
}
