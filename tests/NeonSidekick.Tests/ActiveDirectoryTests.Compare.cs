using NeonSidekick.ActiveDirectory;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests;

/// <summary>
/// Two accounts' groups compared (2026-11-16, plan sharded-hugging-puppy #16, "give Bob what Alice has"): <c>ad_memberof compare</c> and
/// <c>/ad memberof … compare:</c> over the gaps' fixture — the three lists, how each account is in a group, the refusals, the per-list limit, the
/// CSV's sections, the command's parse, run and words, and the card's own kind.
/// </summary>
public sealed partial class ActiveDirectoryTests
{
    private DirectoryAnswer CompareOf(string name, string other, bool nested = false, string? type = null, int limit = 200) =>
        Run(s => DirectoryQueries.MemberOfCompare(s, Source(), name, other, type, nested, limit));

    private static IReadOnlyList<string> Names(DirectoryTable table) => table.Rows.Select(r => (string)r.Cells[0]!.Text!).ToList();

    [Fact]
    public void Compare_Direct_SplitsIntoOnlyEachAndBoth()
    {
        Gaps();
        var answer = CompareOf("grace", "erin");
        Assert.False(answer.Failed);
        Assert.Equal(DirectoryQueries.MemberOfCompareKind, answer.Kind);
        Assert.Equal(Grace, answer.Self!.Dn);
        Assert.Equal(Erin, answer.Other!.Dn);
        Assert.Equal(3, answer.Tables.Count);
        Assert.Equal(["Loop B", "Tier0"], Names(answer.Tables[0]));
        Assert.Equal(["All-Hands"], Names(answer.Tables[1]));
        Assert.Equal(["Domain Users"], Names(answer.Tables[2]));
        Assert.Equal("only ⟪grace⟫ (2)", answer.Tables[0].Title);
        Assert.Equal("only ⟪erin⟫ (1)", answer.Tables[1].Title);
        Assert.Equal("both (1)", answer.Tables[2].Title);
        Assert.All(answer.Tables, t => Assert.True(t.TitleInModel));
        Assert.Equal(["only grace", "only erin", "both"], answer.Tables.Select(t => t.Section));
        string text = Model(answer);
        Assert.StartsWith("Groups of user ⟪grace⟫ and user ⟪erin⟫ in lab.example, direct: 2 only ⟪grace⟫, 1 only ⟪erin⟫, 1 both\n", text);
        Assert.Contains("only ⟪grace⟫ (2):", text);
        Assert.Contains("\n  ⟪Domain Users⟫ (security global) ⟪grace⟫ primary group; ⟪erin⟫ primary group\n", text);
        // The both list's columns name each account's way in.
        Assert.Equal(["name", "type", "account", "via grace", "via erin"], answer.Tables[2].Columns.Select(c => c.Name));
        Assert.Equal<string?>(["Domain Users", "security global", "Domain Users", DirectoryText.PrimaryGroupWord, DirectoryText.PrimaryGroupWord],
            answer.Tables[2].Rows[0].Cells.Select(c => c!.Text));
    }

    [Fact]
    public void Compare_Nested_CarriesEachSidesVia()
    {
        Gaps();
        var answer = CompareOf("grace", "erin", nested: true);
        Assert.Contains("Domain Admins", Names(answer.Tables[0]));
        Assert.Contains("Administrators", Names(answer.Tables[0]));
        Assert.DoesNotContain("Domain Users", Names(answer.Tables[0]));
        Assert.Equal(["Domain Users"], Names(answer.Tables[2]));
        Assert.Contains("\n  ⟪Domain Admins⟫ (security global) via ⟪Tier0⟫\n", Model(answer));
        Assert.StartsWith("Groups of user ⟪grace⟫ and user ⟪erin⟫ in lab.example, nested: ", Model(answer));
    }

    [Fact]
    public void Compare_EmptyList_StillShowsItsTitle()
    {
        Gaps();
        var answer = CompareOf("frank", "erin");
        // Frank is in Domain Users alone, which Erin shares: his own list is empty, and says (0) rather than being left out.
        Assert.Equal(3, answer.Tables.Count);
        Assert.Equal("only ⟪frank⟫ (0)", answer.Tables[0].Title);
        Assert.Empty(answer.Tables[0].Rows);
        Assert.Contains("only ⟪frank⟫ (0):", Model(answer));
        var none = CompareOf("grace", "grace-twin-missing");
        Assert.True(none.Failed);   // a side not found answers alone
        Assert.Equal(DirectoryText.NotFound("account", "grace-twin-missing", "lab.example"), none.Error);
    }

    [Fact]
    public void Compare_TheSameAccountTwice_IsRefused()
    {
        Gaps();
        var answer = CompareOf("grace", "Grace Hu");
        Assert.True(answer.Failed);
        Assert.Equal(DirectoryText.CompareSameAccount("grace", "Grace Hu"), answer.Error);
        Assert.Equal("Error: 'a' and 'b' are the same account; name two different accounts to compare.", DirectoryText.CompareSameAccount("a", "b"));
    }

    [Fact]
    public void Compare_AnAmbiguousSide_AnswersWithItsCandidates()
    {
        Gaps();
        for (int i = 0; i < 2; i++)
        {
            string n = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _system.Add("CN=Twin" + n + ",OU=Staff," + Base, ("objectClass", "user"), ("sAMAccountName", "twin" + n), ("displayName", "Twin"));
        }

        var answer = CompareOf("grace", "Twin");
        Assert.True(answer.Failed);
        Assert.NotNull(answer.Candidates);   // that side's own answer: its candidates, no compare
        Assert.Null(answer.Other);
    }

    [Fact]
    public void Compare_TheLimit_IsPerList()
    {
        Gaps();
        var answer = CompareOf("grace", "erin", limit: 1);
        Assert.All(answer.Tables, t => Assert.Equal(1, t.Shown));
        Assert.Contains(DirectoryText.FirstGroupsListedEach(1, true, false), answer.Head);
        Assert.Equal("each list's first 1 listed (raise limit up to 1,000)", DirectoryText.FirstGroupsListedEach(1, true, false));
    }

    [Fact]
    public void Compare_Csv_NamesEverySection_TheFirstToo()
    {
        Gaps();
        var lines = DirectoryCsv.Text(CompareOf("grace", "erin"))!.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal("name,type,account,via,via grace,via erin,section,dn", lines[0]);
        Assert.StartsWith("Loop B,", lines[1]);
        Assert.Contains(",only grace,", lines[1]);
        Assert.Contains(",only erin,", lines[3]);
        Assert.Contains(",primary group,primary group,both,", lines[4]);
    }

    [Fact]
    public async Task CompareTool_AnswersAndSavesOneCsv()
    {
        Gaps();
        var tool = new AdMemberOfTool(Access());
        string answer = await Call(tool, ("name", "grace"), ("compare", "erin"));
        Assert.StartsWith("Groups of user ⟪grace⟫ and user ⟪erin⟫ in lab.example, direct: ", answer);
        Assert.Equal(answer, await Call(tool.AsRedacted(), ("name", "grace"), ("compare", "erin")));
        Assert.Contains("\"compare\"", tool.JsonSchema.GetRawText(), StringComparison.Ordinal);
        string root = ToRoot();
        try
        {
            var saving = new AdMemberOfTool(Access(), saveRoot: Root(root));
            string saved = await Call(saving, ("name", "grace"), ("compare", "erin"), ("to", "."));
            Assert.EndsWith("\n" + DirectoryText.Saved(4, "ad-memberof-grace-compare-erin.csv", false), saved);
            Assert.Equal(5, File.ReadAllLines(Path.Combine(root, "ad-memberof-grace-compare-erin.csv")).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Command_Compare_ParsesRunsAndReRunsByDn()
    {
        Assert.Equal("Bob Smith", DirectoryCommand.Parse("memberof grace compare:\"Bob Smith\"").Args!.Compare);
        Assert.Equal(DirectoryCommand.KeyNotTaken("compare", "group"), DirectoryCommand.Parse("group Staff compare:x").Error);
        Assert.Contains("compare", DirectoryCommand.Keys);
        Assert.Contains("memberof grace compare:", DirectoryCommand.Complete("memberof grace co", ["lab"]).Select(c => c.Text));
        Assert.Contains("[compare:<name>]", DirectoryCommand.Usage);
        Assert.Equal("memberof grace compare erin nested", DirectoryCommand.MemberOfQuestion("grace", true, null, "erin"));
        Assert.Equal("the groups grace and erin belong to at any depth, side by side", DirectoryCommand.DescribeCompare("grace", "erin", true));
        Assert.Equal("the groups grace and erin belong to, side by side", DirectoryCommand.Describe(DirectoryCommand.Parse("memberof grace compare:erin").Args!));

        Gaps();
        var result = await DirectoryCommand.RunAsync(Access(), "memberof grace compare:erin", CancellationToken.None);
        Assert.False(result.Failed);
        Assert.Equal(DirectoryQueries.MemberOfCompareKind, result.Answer!.Kind);
        Assert.Equal("the groups the users grace and erin belong to, side by side", result.Title);
        Assert.Contains(result.Lines, l => l.Contains("only ⟪erin⟫ (1)", StringComparison.Ordinal));
        // The card's n: both accounts by DN, nested flipped.
        var again = await DirectoryCommand.CompareAsync(Access(), null, result.Answer.Self!, result.Answer.Other!, true, CancellationToken.None);
        Assert.False(again.Failed);
        Assert.Equal(DirectoryQueries.MemberOfCompareKind, again.Answer!.Kind);
        Assert.Contains("Domain Admins", Names(again.Answer.Tables[0]));
    }

    [Fact]
    public void Csv_AFirstTableWithoutASection_StaysUnnamed()
    {
        var answer = new DirectoryAnswer();
        var first = new DirectoryTable([new(DirectoryText.ColName)]);
        first.Rows.Add(new DirectoryRow("a", ["a"], "CN=a"));
        var second = new DirectoryTable([new(DirectoryText.ColName)]) { Section = "later" };
        second.Rows.Add(new DirectoryRow("b", ["b"], "CN=b"));
        answer.Tables.Add(first);
        answer.Tables.Add(second);
        var lines = DirectoryCsv.Text(answer)!.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(["name,section,dn", "a,,CN=a", "b,later,CN=b"], lines);
    }
}
