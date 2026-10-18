using NeonSidekick.ActiveDirectory;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/ad</c>'s cards on the pane (2026-10-15, the AD gaps plan, §6 and §6b): columns under a header; a double-click on a row opens that object's
/// card by its DN in the same domain and ESC comes back without a read; a user card's groups and manager open theirs; a group card's
/// <c>n</c> and <c>u</c> read it again in place; an ambiguous name is a card of the candidates; <c>to:</c> writes the file and the card names it.
/// The pane's content lines start two rows under the cursor's (the strip, then the spacer).
/// </summary>
public partial class ChatScreenTests
{
    private const string AdBase = "DC=lab,DC=example";
    private const string AdAlice = "CN=Alice Smith,OU=Staff," + AdBase;
    private const string AdBob = "CN=Bob Jones,OU=Staff," + AdBase;
    private const string AdDave = "CN=Dave Lee,OU=Staff," + AdBase;
    private const string AdOps = "CN=Ops,OU=Groups," + AdBase;
    private const string AdInner = "CN=Inner,OU=Groups," + AdBase;
    private const string AdJohn2 = "CN=John Smith,OU=B," + AdBase;
    private const int AdTop = 100;

    /// <summary>A small directory: alice and bob in Ops, dave through Inner, bob alice's manager, two John Smiths; the pane's cursor row at 100.</summary>
    private FakeDirectorySystem AdFixture()
    {
        var now = _time.GetUtcNow();
        string Ft(DateTimeOffset moment) => AdValues.ToFileTime(moment).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var system = _directorySystem = new FakeDirectorySystem();
        system.Add(AdBase, ("maxPwdAge", "-36288000000000"));
        system.Add(AdAlice, ("objectClass", "user"), ("sAMAccountName", "alice"), ("displayName", "Alice Smith"), ("userAccountControl", "512"), ("lastLogonTimestamp", Ft(now.AddDays(-200))));
        system.Add(AdBob, ("objectClass", "user"), ("sAMAccountName", "bob"), ("displayName", "Bob Jones"), ("userAccountControl", "512"), ("lastLogonTimestamp", Ft(now.AddDays(-100))));
        system.Add(AdDave, ("objectClass", "user"), ("sAMAccountName", "dave"), ("displayName", "Dave Lee"), ("userAccountControl", "512"));
        system.Add(AdOps, ("objectClass", "group"), ("sAMAccountName", "Ops"), ("cn", "Ops"));
        system.Add(AdInner, ("objectClass", "group"), ("sAMAccountName", "Inner"), ("cn", "Inner"));
        system.Add("CN=John Smith,OU=A," + AdBase, ("objectClass", "user"), ("sAMAccountName", "jsmith1"), ("displayName", "John Smith"), ("userAccountControl", "512"));
        system.Add(AdJohn2, ("objectClass", "user"), ("sAMAccountName", "jsmith2"), ("displayName", "John Smith"), ("userAccountControl", "512"));
        system.AddMember(AdOps, AdAlice);
        system.AddMember(AdOps, AdBob);
        system.AddMember(AdOps, AdInner);
        system.AddMember(AdInner, AdDave);
        system.AddReport(AdBob, AdAlice);
        string path = DirectoryConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"domains\": { \"lab\": { \"domain\": \"lab.example\", \"server\": \"localhost\" } } }");
        _settings.Update(d => { d.TtsOutput = false; d.DirectoryDomainsOffered = ["lab"]; });
        _console.Profile.Height = 60;
        _geometry = new ScreenGeometry(() => null, () => AdTop);
        return system;
    }

    /// <summary>The card the pane would draw for <paramref name="read"/>, at the console's width (the test reads the same fake, then forgets those searches).</summary>
    private IReadOnlyList<DirectoryCardLine> AdCard(Func<IDirectorySession, DirectorySource, DirectoryAnswer> read)
    {
        var access = new DirectoryAccess(_directorySystem!, () => new DirectoryCatalog([new DirectoryDomain("lab", new DirectoryDomainConfig { Domain = "lab.example", Server = "localhost" })], []), () => ["lab"], () => null, _time);
        var source = access.Domain(null, forceRedacted: false).Source!;
        var lines = _directorySystem!.Run(source.Target, s => DirectoryRender.ForCard(read(s, source), _console.Profile.Width));
        _directorySystem.Filters.Clear();
        return lines;
    }

    /// <summary>The screen row of the card's line whose target is <paramref name="dn"/>.</summary>
    private static int AdRow(IReadOnlyList<DirectoryCardLine> card, string dn)
    {
        int index = card.ToList().FindIndex(l => l.Target?.Dn == dn);
        Assert.True(index >= 0, "no line opens " + dn);
        return AdTop + 2 + index;
    }

    private static Action<ScriptedInput> AdDoubleClick(int row) => input => { input.PushClick(4, row); input.PushClick(4, row); };

    [Fact]
    public async Task Ad_ADoubleClickedMember_OpensItsCard_EscGoesBack_AndAgainCloses()
    {
        AdFixture();
        int alice = AdRow(AdCard((s, src) => DirectoryQueries.GroupAnswer(s, src, "Ops", false, "all", DirectoryLimits.MaxEntries, _time.LocalTimeZone)), AdAlice);
        int atAlice = -1, back = -1;
        StepsWhenIdle(
            Line("/ad group Ops"),
            AdDoubleClick(alice),
            i => { atAlice = _directorySystem!.Filters.Count; i.Push(Keys.Escape); },   // back to the group's card
            i => { back = _directorySystem!.Filters.Count; i.Push(Keys.Escape); },      // and closed
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("User alice in lab.example: enabled", output);
        Assert.Contains(DirectoryText.Glyph + " Directory · the user alice ‹ the group Ops", output);
        Assert.Contains(_directorySystem!.Filters, f => f == "(distinguishedName=" + LdapFilter.Escape(AdAlice) + ")");
        Assert.Equal(atAlice, back);   // the group's card came back from the trail, not the directory
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Ad_OnAUsersCard_AGroupsRowAndTheManager_OpenByDn()
    {
        AdFixture();
        var card = AdCard((s, src) => DirectoryQueries.UserAnswer(s, src, "alice", _time.GetUtcNow(), _time.LocalTimeZone));
        int ops = AdRow(card, AdOps);
        int manager = AdRow(card, AdBob);
        StepsWhenIdle(
            Line("/ad user alice"),
            AdDoubleClick(ops),
            Key(Keys.Escape),
            AdDoubleClick(manager),
            Key(Keys.Escape),
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("Group Ops in lab.example: 3 members, direct", output);
        Assert.Contains("User bob in lab.example: enabled", output);
        Assert.Contains(_directorySystem!.Filters, f => f == "(distinguishedName=" + LdapFilter.Escape(AdOps) + ")");
        Assert.Contains(_directorySystem.Filters, f => f == "(distinguishedName=" + LdapFilter.Escape(AdBob) + ")");
        Assert.DoesNotContain(_directorySystem.Filters, f => f.Contains("(sAMAccountName=Ops)", StringComparison.Ordinal));   // never a name search
    }

    [Fact]
    public async Task Ad_AGroupCardsButtons_NestedThenUsersOnly_ReadInPlace()
    {
        AdFixture();
        StepsWhenIdle(
            Line("/ad group Ops"),
            Key(Keys.Char('n')),
            Key(Keys.Char('u')),
            Key(Keys.Escape),   // one ESC closes: a switch adds no card to the trail
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(DirectoryText.Glyph + " Directory · " + DirectoryCommand.DescribeGroup("Ops", true, "all"), output);
        Assert.Contains(DirectoryText.Glyph + " Directory · " + DirectoryCommand.DescribeGroup("Ops", true, "users"), output);
        Assert.Contains("the users in Ops at any depth", output);
        Assert.Matches(@"Dave Lee\s+user\s+dave\s+Inner", output);
        Assert.Contains("4 members, nested (3 direct, 1 through other groups)", output);
        Assert.Contains("3 users, nested (2 direct, 1 through other groups)", output);
    }

    [Fact]
    public async Task Ad_ACardTypedNestedAndUsersOnly_NamesItsButtonsDirectAndAllMembers()
    {
        AdFixture();
        StepsWhenIdle(Line("/ad group Ops nested members:users"), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("│ " + DirectoryText.DirectButton + " │ " + DirectoryText.AllMembersButton + " ", output);
        Assert.Contains(InfoPane.ShiftFindKeys, output);
    }

    [Fact]
    public async Task Ad_AnAmbiguousName_IsACardOfTheCandidates_ADoubleClickOpensTheSecond()
    {
        AdFixture();
        int second = AdRow(AdCard((s, src) => DirectoryQueries.UserAnswer(s, src, "John Smith", _time.GetUtcNow(), _time.LocalTimeZone)), AdJohn2);
        StepsWhenIdle(Line("/ad user John Smith"), AdDoubleClick(second), Key(Keys.Escape), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(DirectoryText.Glyph + " Directory · " + DirectoryText.AmbiguousTitle("John Smith", 2), output);
        Assert.Contains("User jsmith2 in lab.example: enabled", output);
        Assert.Contains(_directorySystem!.Filters, f => f == "(distinguishedName=" + LdapFilter.Escape(AdJohn2) + ")");
    }

    [Fact]
    public async Task Ad_FindStaleUsers_OnThePane_ColumnsUnderAHeader()
    {
        AdFixture();
        var card = AdCard((s, src) => DirectoryQueries.FindAnswer(s, src, new FindOptions("stale_users", 90, null, DirectoryLimits.MaxEntries), _time.GetUtcNow(), _time.LocalTimeZone));
        StepsWhenIdle(Line("/ad find stale_users"), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        var header = card.First(l => l.Style == DirectoryCardStyle.Header).Text;
        Assert.StartsWith("  account  name ", header);
        Assert.Contains("\n" + header, output);
        foreach (var row in card.Where(l => l.Target is not null))
        {
            Assert.Contains("\n" + row.Text, output);
        }
    }

    [Fact]
    public async Task Ad_GroupTo_WritesTheFile_AndTheCardNamesIt()
    {
        AdFixture();
        StepsWhenIdle(Line("/ad group Ops to:ops.csv"), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(DirectoryText.Saved(3, "ops.csv", false), output);
        string file = Assert.Single(Directory.GetFiles(_dir, "ops.csv", SearchOption.AllDirectories));
        Assert.Equal(4, File.ReadAllLines(file).Length);   // the header and three members
    }
}
