using Microsoft.Extensions.AI;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Files;
using NeonSidekick.Help;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// An account's groups (2026-11-12, the memberof plan, the user's report: "I'm unable to get a complete list of groups for a user … they have
/// many nested groups"): <c>ad_memberof</c> and <c>/ad memberof</c> over the gaps' fixture — direct groups and the primary group, the upward walk
/// with its via, distribution groups, cycles, a ranged <c>memberOf</c>, groups unread or in another domain, the cap and the time budget, a name
/// several kinds share, the save, the refusals before any search, the card's pointer, and the wording.
/// </summary>
public sealed partial class ActiveDirectoryTests
{
    private DirectoryAnswer MemberOfOf(string name, bool nested = false, string? type = null, int limit = 200, int cap = DirectoryLimits.MaxCounted) =>
        Run(s => DirectoryQueries.MemberOfAnswer(s, Source(), name, type, nested, limit, cap: cap));

    private static IReadOnlyList<string> Accounts(DirectoryAnswer answer) => answer.Tables[0].Rows.Select(r => (string)r.Cells[2]!.Text!).ToList();

    [Fact]
    public void MemberOf_Direct_IsItsMemberOfAndItsPrimaryGroup()
    {
        Gaps();
        var answer = MemberOfOf("grace");
        string text = Model(answer);
        Assert.StartsWith("Groups of user ⟪grace⟫ in lab.example: 3 groups, direct (2 in its member-of list, 1 by primary group)\n", text);
        Assert.Equal(["Domain Users", "Loop B", "Tier0"], Accounts(answer));
        Assert.Contains("\n  ⟪Domain Users⟫ (security global, primary group)\n", text);
        Assert.Contains("\n  ⟪Tier0⟫ (group)\n", text);   // no groupType read: its kind; the account left out, the same as the name
        Assert.Equal("memberof", answer.Kind);
        Assert.Equal(Grace, answer.Self!.Dn);
        Assert.False(answer.Partial);
    }

    [Fact]
    public void MemberOf_Nested_WalksUp_WithVia_AndEndsTheCycle()
    {
        Gaps();
        var answer = MemberOfOf("grace", nested: true);
        string text = Model(answer);
        Assert.StartsWith("Groups of user ⟪grace⟫ in lab.example: 6 groups, nested (2 direct, 1 by primary group, 3 through other groups)\n", text);
        Assert.Equal(["Administrators", "Domain Admins", "Domain Users", "Loop A", "Loop B", "Tier0"], Accounts(answer));
        Assert.Contains("\n  ⟪Tier0⟫ (group) direct\n", text);
        Assert.Contains("\n  ⟪Domain Admins⟫ (security global) via ⟪Tier0⟫\n", text);
        Assert.Contains("\n  ⟪Administrators⟫ (group) via ⟪Domain Admins⟫\n", text);
        Assert.Contains("\n  ⟪Loop A⟫ (group) via ⟪Loop B⟫\n", text);   // Loop A holds Loop B back: listed once
        var rows = answer.Tables[0].Rows;
        Assert.All(rows, r => Assert.True(r.Opens));
        Assert.Equal<string?>(["Tier0", "group", "Tier0", DirectoryText.DirectWord], rows.Single(r => r.Dn == Tier0).Cells.Select(c => c!.Text));
    }

    [Fact]
    public void MemberOf_DistributionGroups_AreWalkedToo()
    {
        Gaps();
        _system.Add("CN=Everyone List," + Neon, ("sAMAccountName", "Everyone List"), ("cn", "Everyone List"), ("groupType", "8"));
        Classes("CN=Everyone List," + Neon, "top", "group");
        _system.AddMember("CN=Everyone List," + Neon, AllHands);
        string text = Model(MemberOfOf("erin", nested: true));
        Assert.Contains("\n  ⟪All-Hands⟫ (distribution universal) direct\n", text);
        Assert.Contains("\n  ⟪Everyone List⟫ (distribution universal) via ⟪All-Hands⟫\n", text);   // tokenGroups never held either
    }

    [Fact]
    public void MemberOf_AGroup_NeverListsItself_ThroughACycle()
    {
        Gaps();
        var answer = MemberOfOf("Loop A", nested: true, type: "group");
        Assert.StartsWith("Groups of group ⟪Loop A⟫ in lab.example: 1 group, nested (1 direct, 0 through other groups)", Model(answer));
        Assert.Equal(["Loop B"], Accounts(answer));
    }

    [Fact]
    public void MemberOf_ARangedMemberOf_IsReadWhole()
    {
        Gaps();
        for (int i = 1; i <= 5; i++)
        {
            string dn = "CN=Team" + i + "," + Neon;
            _system.Add(dn, ("sAMAccountName", "Team" + i), ("cn", "Team" + i), ("groupType", "-2147483646"));
            Classes(dn, "top", "group");
            _system.AddMember(dn, Frank);
        }

        _system.AddMember(Tier0, "CN=Team1," + Neon);
        _system.AddMember(DomainAdmins, "CN=Team1," + Neon);
        _system.AddMember(AllHands, "CN=Team1," + Neon);
        _system.AddMember("CN=Loop A," + Neon, "CN=Team1," + Neon);
        _system.MaxValRange = 3;   // a plain read hands back the first three; the rest by range, in the fake's pages of two
        var answer = MemberOfOf("frank", nested: true);
        var accounts = Accounts(answer);
        Assert.Equal(5, accounts.Count(a => a.StartsWith("Team", StringComparison.Ordinal)));
        Assert.Contains("Loop A", accounts);   // Team1's fourth parent, past its own first range
        Assert.Contains(_system.Filters, f => f == "(objectClass=*)");   // the range reads
        Assert.DoesNotContain(DirectoryText.FirstRangeNote, Model(answer));
        Assert.False(answer.Partial);
    }

    [Fact]
    public void MemberOf_GroupsUnreadOrElsewhere_AreListed_NotOpened()
    {
        Gaps();
        _system.Entries[Frank]["memberOf"] = [FakeDirectorySystem.B("CN=Gone,OU=Groups," + Base), FakeDirectorySystem.B("CN=Far,CN=Users,DC=other,DC=example")];
        var answer = MemberOfOf("frank", nested: true);
        string text = Model(answer);
        Assert.Contains("\n  ⟪Gone⟫ (" + DirectoryText.NotReadable + ") direct\n", text);
        Assert.Contains("\n  ⟪Far⟫ (" + DirectoryText.OtherDomain + ") direct\n", text);
        Assert.Contains("\n" + DirectoryText.OtherDomainGroupsNote + "\n", text);
        Assert.All(answer.Tables[0].Rows.Where(r => r.Name is "Gone" or "Far"), r => Assert.Null(r.Target));   // a double-click opens nothing
    }

    [Fact]
    public void MemberOf_TheCapAndTheTimeLimit_AreSaid_AndPartial()
    {
        Gaps();
        var cut = MemberOfOf("grace", nested: true, cap: 2);
        Assert.Contains(": 5+ groups, nested", Model(cut));   // the three it starts from always held; two above them (the code review's 2)
        Assert.Contains("; " + DirectoryText.GroupsCapNote(2), Model(cut));
        Assert.Equal("the 2-group cap stopped the walk; the list is partial", DirectoryText.GroupsCapNote(2));
        Assert.True(cut.Partial);
        _system.TimesOut = f => f.Contains("(distinguishedName=", StringComparison.Ordinal);
        var stopped = MemberOfOf("grace", nested: true);
        string text = Model(stopped);
        Assert.Contains("; " + DirectoryText.StoppedAtTimeLimit, text);
        Assert.Contains("(" + DirectoryText.NotRead + ")", text);
        Assert.Contains("\n" + DirectoryText.GroupsUnreadNote(2) + "\n", text);   // Tier0 and Loop B: nothing above them listed (the code review's 19)
        Assert.True(stopped.Partial);
    }

    [Fact]
    public void MemberOf_ALongList_IsCut_AndSaysHowToGetTheRest()
    {
        Gaps();
        string text = Model(MemberOfOf("grace", nested: true, limit: 2));
        Assert.Contains("; " + DirectoryText.FirstGroupsListed(2, true, false), text);
        Assert.Equal("the first 2 listed (raise limit up to 1,000)", DirectoryText.FirstGroupsListed(2, true, false));
        Assert.Equal("the first 2 listed (raise limit up to 1,000, or save them all with to)", DirectoryText.FirstGroupsListed(2, true, true));
        Assert.Equal("the first 1,000 listed (save them all with to)", DirectoryText.FirstGroupsListed(1000, false, true));   // /ad: no limit to raise (the code review's 6)
        Assert.Equal("the first 1,000 listed", DirectoryText.FirstGroupsListed(1000, false, false));
        Assert.DoesNotContain("members", DirectoryText.FirstGroupsListed(2, true, true), StringComparison.Ordinal);
        Assert.Equal("… 4 more; lower limit to fit, or save them all with to", DirectoryText.MoreGroups(4, true));   // the code review's 27
        Assert.Equal("… 4 more; lower limit to fit", DirectoryText.MoreGroups(4, false));
    }

    [Fact]
    public void MemberOf_ANameTwoKindsShare_TheUserAnswers_AndTheOtherIsSaid()
    {
        Gaps();
        _system.Add("CN=grace,OU=Groups," + Base, ("sAMAccountName", "grace"), ("cn", "grace"), ("groupType", "-2147483646"));
        Classes("CN=grace,OU=Groups," + Base, "top", "group");
        _system.AddMember(AllHands, "CN=grace,OU=Groups," + Base);
        var user = MemberOfOf("grace");
        Assert.StartsWith("Groups of user ⟪grace⟫", Model(user));
        Assert.Contains("\n" + DirectoryText.AlsoNamed("grace", ["group"]) + "\n", Model(user));
        Assert.Equal("a group is named ⟪grace⟫ too; give type group for its groups", DirectoryText.AlsoNamed("grace", ["group"]));
        var group = MemberOfOf("grace", type: "group");
        Assert.StartsWith("Groups of group ⟪grace⟫", Model(group));
        Assert.Equal(["All-Hands"], Accounts(group));
        Assert.Equal("a group or a service account is named ⟪x⟫ too; give type group for its groups", DirectoryText.AlsoNamed("x", ["group", "service"]));
    }

    [Fact]
    public void MemberOf_AServiceAccount_ByTypeAndByDn()
    {
        Gaps();
        _system.AddMember(Tier0, Gmsa);
        Assert.StartsWith("Groups of service account ⟪svc_web$⟫", Model(MemberOfOf("svc_web", type: "service")));
        var byDn = Run(s => DirectoryQueries.MemberOfAnswer(s, Source(), "svc_web", null, false, 200, dn: Gmsa));
        Assert.Equal(["All-Hands", "Tier0"], Accounts(byDn));
        Assert.Equal(DirectoryText.NotFound("computer", "svc_web", "lab.example"), Model(MemberOfOf("svc_web", type: "computer")));
        Assert.Equal(DirectoryText.Gone("x", "lab.example"), Run(s => DirectoryQueries.MemberOfAnswer(s, Source(), "x", null, false, 200, dn: "CN=Nobody," + Base)).Error);
        Assert.Equal(DirectoryText.NotFound("account", "nobody", "lab.example"), Model(MemberOfOf("nobody")));
    }

    // ── The code review's fixes (2026-11-12) ───────────────────────────────

    [Fact]
    public void Review1_GroupsOutsideAnOuBase_AreReadUnderTheDomainsHead()
    {
        Gaps();
        _config = new() { Domain = "lab.example", Server = "localhost", BaseDn = Neon };   // grace and Tier0 inside; Domain Admins and Domain Users in CN=Users
        string text = Model(MemberOfOf("grace", nested: true));
        Assert.Contains("\n  ⟪Domain Admins⟫ (security global) via ⟪Tier0⟫\n", text);
        Assert.Contains("\n  ⟪Domain Users⟫ (security global, primary group)\n", text);
        Assert.DoesNotContain(DirectoryText.NotReadable, text);
    }

    [Fact]
    public void Review3_AFailingBatch_IsNotRead_NeverTheWholeAnswerFailed()
    {
        Gaps();
        _system.Throws = f => f.Contains("(distinguishedName=", StringComparison.Ordinal) && !f.Contains("Domain Users", StringComparison.Ordinal);
        var answer = MemberOfOf("grace", nested: true);
        Assert.False(answer.Failed);
        Assert.Contains("(" + DirectoryText.NotRead + ")", Model(answer));
        Assert.True(answer.Partial);
        // The group card's upward walk too (the members walk below it is another matter): its batch naming Ops fails, the card still answers.
        _system.Throws = f => f.StartsWith("(|(distinguishedName=", StringComparison.Ordinal) && f.Contains(LdapFilter.Escape(Ops), StringComparison.Ordinal);
        var group = GroupOf("Inner", nested: true);
        Assert.False(group.Failed);
        Assert.Contains("\n  member of (1, nested): ⟪Ops⟫", Model(group));
        Assert.Contains("\n" + DirectoryText.GroupsUnreadNote(1) + "\n", Model(group));
    }

    [Fact]
    public void Review4_ADottedName_NeverMeansAComputersFirstLabel()
    {
        Gaps();
        _system.Add("CN=JANE,OU=Workstations," + Base, ("objectClass", "computer"), ("sAMAccountName", "JANE$"), ("cn", "JANE"), ("memberOf", Ops));
        Assert.Equal(DirectoryText.NotFound("account", "jane.doe", "lab.example"), Model(MemberOfOf("jane.doe")));
        Assert.StartsWith("Groups of computer ⟪JANE$⟫", Model(MemberOfOf("JANE")));
    }

    [Fact]
    public void Review16And37_ARangeReadThatStops_KeepsTheFirstRange_AndIsPartial()
    {
        Gaps();
        for (int i = 1; i <= 5; i++)
        {
            string dn = "CN=Team" + i + "," + Neon;
            _system.Add(dn, ("sAMAccountName", "Team" + i), ("cn", "Team" + i), ("groupType", "-2147483646"));
            Classes(dn, "top", "group");
            _system.AddMember(dn, Frank);
        }

        _system.MaxValRange = 3;
        _system.TimesOut = f => f == "(objectClass=*)";   // every range read runs out of time
        var answer = MemberOfOf("frank");
        Assert.Equal(3, Accounts(answer).Count(a => a.StartsWith("Team", StringComparison.Ordinal)));   // the first range in hand, never dropped
        Assert.Contains("; " + DirectoryText.StoppedAtTimeLimit, Model(answer));
        Assert.True(answer.Partial);
    }

    [Fact]
    public void Review38_ALookupThatTimedOut_IsSaid_NeverAnotherKind()
    {
        Gaps();
        _system.Add("CN=grace,OU=Groups," + Base, ("sAMAccountName", "grace"), ("cn", "grace"));
        Classes("CN=grace,OU=Groups," + Base, "top", "group");
        _system.TimesOut = f => f.StartsWith("(&(objectCategory=person)", StringComparison.Ordinal);
        Assert.Equal(DirectoryText.LookupTimedOut("grace", "lab.example"), Model(MemberOfOf("grace")));
    }

    [Fact]
    public void Review44_AFailingProbeForTheNote_NeverFailsTheAnswer()
    {
        Gaps();
        _system.Throws = f => f.StartsWith("(&(objectCategory=group)", StringComparison.Ordinal);
        var answer = MemberOfOf("grace");
        Assert.False(answer.Failed);
        Assert.StartsWith("Groups of user ⟪grace⟫", Model(answer));
    }

    [Fact]
    public void Review33_TheGroupCard_ReadsItsOwnMemberOfWhole()
    {
        Gaps();
        for (int i = 1; i <= 5; i++)
        {
            string dn = "CN=Ring" + i + "," + Neon;
            _system.Add(dn, ("sAMAccountName", "Ring" + i), ("cn", "Ring" + i));
            Classes(dn, "top", "group");
            _system.AddMember(dn, Inner);
        }

        _system.MaxValRange = 3;
        Assert.Contains("\n  member of (6, nested): ", Model(GroupOf("Inner", nested: true)));   // Ops and the five rings, past the first range
    }

    [Fact]
    public void Review34And48_TheCards_PointAtMemberOf_WhenTheirListsAreNotTheWholeStory()
    {
        Gaps();
        Assert.Contains(DirectoryText.MemberOfPointer, Run(s => DirectoryQueries.User(s, Source(), "erin", _time.GetUtcNow(), TimeZoneInfo.Utc)));   // a tokenGroups list
        _system.Entries["CN=WS01,OU=Workstations," + Base]["memberOf"] = [FakeDirectorySystem.B(Ops)];
        Assert.Contains(DirectoryText.MemberOfPointer, Run(s => DirectoryQueries.Computer(s, Source(), "ws01", TimeZoneInfo.Utc)));
        Assert.DoesNotContain(DirectoryText.MemberOfPointer, Run(s => DirectoryQueries.Computer(s, Source(), "ws99", TimeZoneInfo.Utc)));   // no groups at all
    }

    // ── The tool ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MemberOfTool_RefusesBeforeAnySearch()
    {
        Gaps();
        var tool = new AdMemberOfTool(Access());
        _system.Filters.Clear();
        Assert.Equal(DirectoryText.Missing("name"), await Call(tool));
        Assert.Equal(DirectoryText.BadChoice("type", "ou", DirectoryQueries.MemberOfTypes), await Call(tool, ("name", "grace"), ("type", "ou")));
        Assert.StartsWith("Error:", await Call(tool, ("name", "grace"), ("limit", 0)));
        Assert.StartsWith("Error:", await Call(tool, ("name", "grace"), ("nested", "deep")));
        Assert.Empty(_system.Filters);
    }

    [Fact]
    public async Task MemberOfTool_Answers_TheTwinAlike_AndSaves()
    {
        Gaps();
        var tool = new AdMemberOfTool(Access());
        string answer = await Call(tool, ("name", "grace"), ("nested", true));
        Assert.StartsWith("Groups of user ⟪grace⟫ in lab.example: 6 groups, nested", answer);
        Assert.Equal(answer, await Call(tool.AsRedacted(), ("name", "grace"), ("nested", true)));   // names, types and DNs alone: nothing to redact
        Assert.True(((DirectoryTool)tool.AsRedacted()).ForceRedacted);
        string root = ToRoot();
        try
        {
            var saving = new AdMemberOfTool(Access(), saveRoot: Root(root));
            string saved = await Call(saving, ("name", "grace"), ("nested", true), ("limit", 1), ("to", "."));
            Assert.EndsWith("\n" + DirectoryText.Saved(6, "ad-memberof-grace-nested.csv", false), saved);
            var lines = File.ReadAllLines(Path.Combine(root, "ad-memberof-grace-nested.csv"));
            Assert.Equal("name,type,account,via,dn", lines[0]);
            Assert.Equal(7, lines.Length);   // every group, not the one listed
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── The cards' pointers ────────────────────────────────────────────────

    [Fact]
    public void Cards_PointAtMemberOf_WhenTheirListIsCut()
    {
        Gaps();
        _system.Add("CN=Wide,OU=Staff," + Base, ("objectClass", "user"), ("sAMAccountName", "wide"), ("displayName", "Wide"), ("userAccountControl", "512"));
        for (int i = 0; i < 201; i++)
        {
            string dn = "CN=G" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",OU=Groups," + Base;
            _system.Add(dn, ("objectClass", "group"), ("sAMAccountName", "G" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            _system.AddMember(dn, "CN=Wide,OU=Staff," + Base);
        }

        Assert.EndsWith("\n" + DirectoryText.MemberOfPointer + "\n" + UntrustedText.QuoteNote,
            Run(s => DirectoryQueries.User(s, Source(), "wide", _time.GetUtcNow(), TimeZoneInfo.Utc)));
        Assert.DoesNotContain(DirectoryText.MemberOfPointer, Run(s => DirectoryQueries.User(s, Source(), "alice", _time.GetUtcNow(), TimeZoneInfo.Utc)));
        Assert.Equal("for every group, direct or nested: ad_memberof, or /ad memberof <name> nested", DirectoryText.MemberOfPointer);
    }

    [Fact]
    public void GroupCard_UpwardWalk_PastItsCap_IsSaid()
    {
        Gaps();
        string previous = Inner;
        for (int i = 0; i < 205; i++)
        {
            string dn = "CN=Up" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",OU=Groups," + Base;
            _system.Add(dn, ("objectClass", "group"), ("sAMAccountName", "Up" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            _system.AddMember(dn, previous);
            previous = dn;
        }

        string text = Model(GroupOf("Inner", nested: true));
        Assert.Contains("\n  member of (202+, nested): ", text);   // Ops and Up0, its own, always; 200 above them (the code review's 2 and 21)
        Assert.Contains("\n" + DirectoryText.GroupsAboveNotWalked(DirectoryLimits.MaxWalkedGroups) + "\n", text);
        Assert.Contains("\n" + DirectoryText.MemberOfPointer + "\n", text);
        Assert.Equal("more than 200 groups above; the rest not walked", DirectoryText.GroupsAboveNotWalked(200));
    }

    // ── /ad memberof ───────────────────────────────────────────────────────

    [Fact]
    public void Command_MemberOf_ParsesAndRefuses()
    {
        var (line, _) = DirectoryCommand.Parse("memberof grace nested");
        Assert.Equal(("memberof", "grace", true, (string?)null), (line!.Verb, line.Argument, line.Nested, line.Type));
        Assert.True(DirectoryCommand.Parse("memberof nested grace").Args!.Nested);
        Assert.Equal("computer", DirectoryCommand.Parse("memberof ws01 type:computer").Args!.Type);
        Assert.Equal("type 'ou' is not user, group, computer or service.", DirectoryCommand.Parse("memberof grace type:ou").Error);
        Assert.Equal(DirectoryCommand.Usage, DirectoryCommand.Parse("memberof").Error);
        Assert.Equal("name", DirectoryCommand.Parse("Memberof grace").Args!.Verb);
        Assert.Equal(DirectoryCommand.KeyNotTaken("type", "user"), DirectoryCommand.Parse("user grace type:user").Error);
        Assert.Equal("x.csv", DirectoryCommand.Parse("memberof grace to:x.csv").Args!.To);
        Assert.Equal(["memberof grace type:computer", "memberof grace type:group", "memberof grace type:service", "memberof grace type:user"],
            DirectoryCommand.Complete("memberof grace type:", ["lab"]).Select(c => c.Text).Order(StringComparer.Ordinal));
        Assert.Equal(6, DirectoryCommand.Complete("find name type:", ["lab"]).Count);
        Assert.Contains("memberof grace nested", DirectoryCommand.Complete("memberof grace ne", ["lab"]).Select(c => c.Text));
    }

    [Fact]
    public void Command_MemberOf_Words()
    {
        Assert.Equal("the groups grace belongs to", DirectoryCommand.DescribeMemberOf("grace", false));
        Assert.Equal("the groups grace belongs to at any depth", DirectoryCommand.DescribeMemberOf("grace", true));
        Assert.Equal("Look up the groups grace belongs to at any depth in Active Directory, domain lab (lab.example), and explain what you find",
            DirectoryCommand.AskDraft(DirectoryCommand.DescribeMemberOf("grace", true), "lab", "lab.example"));
        Assert.Equal("memberof grace nested group", DirectoryCommand.MemberOfQuestion("grace", true, "group"));
        Assert.Equal("ad-memberof-grace.csv", DirectoryCsv.NameFor(DirectoryCommand.MemberOfQuestion("grace", false, null)));
        Assert.NotEqual(DirectoryCsv.NameFor(DirectoryCommand.MemberOfQuestion("grace", false, null)), DirectoryCsv.NameFor(DirectoryCommand.MemberOfQuestion("grace", true, null)));
        Assert.Equal("the groups grace belongs to at any depth", DirectoryCommand.Describe(DirectoryCommand.Parse("memberof grace nested").Args!));
    }

    [Fact]
    public async Task Command_MemberOf_RunsAndSaves()
    {
        Gaps();
        var result = await DirectoryCommand.RunAsync(Access(), "memberof grace nested", CancellationToken.None);
        Assert.False(result.Failed);
        Assert.Equal("memberof", result.Answer!.Kind);
        Assert.Contains(result.Lines, l => l.Contains("Domain Admins", StringComparison.Ordinal));
        Assert.Equal("the groups the user grace belongs to at any depth", result.Title);   // the kind read (the code review's 47)
        var again = await DirectoryCommand.MemberOfAsync(Access(), null, new DirectoryRowTarget(Grace, "grace"), false, CancellationToken.None);
        Assert.Equal("the groups the user grace belongs to", again.Title);
        Assert.Equal(3, again.Answer!.Tables[0].Rows.Count);
    }

    // ── One list, the rule and the help ────────────────────────────────────

    [Fact]
    public void MemberOf_TheTypesHaveOneList_AndTheRuleAndHelpNameIt()
    {
        var types = new AdMemberOfTool(Access()).JsonSchema.GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(DirectoryQueries.MemberOfTypes, types);
        Assert.Contains("memberof <name> [nested] [type:" + string.Join('|', DirectoryQueries.MemberOfTypes) + "]", DirectoryCommand.Usage);
        Assert.Contains(HelpCommands.AsWritten.Single(c => c.Command == "/ad").Forms,
            f => f.Syntax == "/ad memberof <name> [nested] [type:" + string.Join('|', DirectoryQueries.MemberOfTypes) + "] [to:<file-or-folder>]");
        Assert.Contains(AdMemberOfTool.ToolName, Assistant.DirectoryRule);
        Assert.Contains("members: users; every group an account belongs to, direct or at any depth, is ad_memberof (with nested); groups are found with ad_find", Assistant.DirectoryRule);
        Assert.Contains(AdMemberOfTool.ToolName, HelpSettings.Describe(NeonSidekick.App.SettingsField.DirectoryTools));
        Assert.Contains(AdMemberOfTool.ToolName, NeonSidekick.App.ChatScreen.DirectoryToolNames);
        Assert.Contains(AdMemberOfTool.ToolName, NeonSidekick.Plans.PlanTools.ReadOnly);
        Assert.Contains(AdMemberOfTool.ToolName, CheckpointTools.Tracked);
    }
}
