using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Advisors;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// An advisor switched off (2026-11-27, the user's ask): out of service with its role, description and threads kept. The entry's
/// <see cref="AdvisorEntry.Off"/>, the roster's order and pick, the row's value and the off texts.
/// </summary>
public class AdvisorOffTests
{
    private static readonly AdvisorEntry Dba = new("dba", "database");
    private static readonly AdvisorEntry Ops = new("ops", "database ops", Off: true);

    [Fact]
    public void Off_IsWrittenOnlyWhenTrue_AndAnOlderFileReadsAsOn()
    {
        var data = new AppSettingsData { AdvisorList = [Dba, Ops] };
        string json = JsonSerializer.Serialize(data, SettingsJsonContext.Default.AppSettingsData);
        using var doc = JsonDocument.Parse(json);
        var list = doc.RootElement.GetProperty(nameof(AppSettingsData.AdvisorList)).EnumerateArray().ToList();
        Assert.False(list[0].TryGetProperty(nameof(AdvisorEntry.Off), out _));
        Assert.True(list[1].GetProperty(nameof(AdvisorEntry.Off)).GetBoolean());

        var back = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettingsData)!;
        Assert.Equal([Dba, Ops], back.AdvisorList);
        var older = JsonSerializer.Deserialize("""{ "AdvisorList": [ { "Profile": "dba", "Role": "x" } ] }""", SettingsJsonContext.Default.AppSettingsData)!;
        Assert.False(older.AdvisorList![0].Off);
    }

    [Fact]
    public void TheRoster_KeepsOff_ActiveFilters_ShownPutsTheOffOnesLast_AndAddTurnsOneBackOn()
    {
        var saved = new List<AdvisorEntry> { new("aaa", "x", Off: true), Dba, Ops with { Profile = "bob" } };
        Assert.True(AdvisorRoster.Entries(saved)[0].Off);
        Assert.Equal(["dba"], AdvisorRoster.Active(saved).Select(e => e.Profile));
        Assert.Equal(["dba", "aaa", "bob"], AdvisorRoster.Shown(saved).Select(e => e.Profile));

        var off = AdvisorRoster.WithOff([Dba], "DBA", true);
        Assert.True(off[0].Off);
        Assert.False(AdvisorRoster.WithOff(off, "dba", false)[0].Off);
        Assert.False(AdvisorRoster.Add(off, new AdvisorEntry("dba", "again"))[0].Off);   // adding is a deliberate act
    }

    [Fact]
    public void Pick_AnActiveRoleWinsOverAnOffOne_ThenTheOffOneIsNamed_ThenAllOff()
    {
        var saved = new List<AdvisorEntry> { Dba, Ops };
        Assert.Equal("dba", AdvisorRoster.Pick(saved, "database").Found?.Profile);   // ops' role holds the word too, but it is off
        var ops = AdvisorRoster.Pick(saved, "ops");
        Assert.Null(ops.Found);
        Assert.Equal("ops", ops.OffOne?.Profile);
        Assert.Equal("ops", AdvisorRoster.Pick(saved, "database ops").OffOne?.Profile);   // by role

        var allOff = new List<AdvisorEntry> { Dba with { Off = true }, Ops };
        Assert.True(AdvisorRoster.Pick(allOff, "nobody").AllOff);
        Assert.Equal("dba", AdvisorRoster.Pick(allOff, "dba").OffOne?.Profile);
        var unknown = AdvisorRoster.Pick(saved, "nobody");
        Assert.False(unknown.AllOff);
        Assert.Equal(["dba"], unknown.Candidates.Select(e => e.Profile));   // the active candidates only

        // An off advisor named whole wins over an active role that merely holds the words (2026-11-27, the last-3 review's #5).
        var named = new List<AdvisorEntry> { new("dbx", "database admin"), new("database", "schemas", Off: true) };
        Assert.Null(AdvisorRoster.Pick(named, "database").Found);
        Assert.Equal("database", AdvisorRoster.Pick(named, "database").OffOne?.Profile);
        var roles = new List<AdvisorEntry> { new("dbx", "sql admin"), new("ops", "sql", Off: true) };
        Assert.Equal("ops", AdvisorRoster.Pick(roles, "sql").OffOne?.Profile);   // its role whole
        Assert.Equal("dbx", AdvisorRoster.Pick(roles, "admin").Found?.Profile);    // a partial role stays the active one's
    }

    [Fact]
    public void Value_CountsTheActiveOnes_AndKeepsTheOffTailWhole()
    {
        Assert.Equal("none", AdvisorRoster.Value(null));
        Assert.Equal("1: dba (database) · 1 off", AdvisorRoster.Value([Dba, Ops]));
        Assert.Equal("2 off", AdvisorRoster.Value([Dba with { Off = true }, Ops]));
        string cut = AdvisorRoster.Value([new AdvisorEntry("dba", new string('r', 80)), Ops], width: 40);
        Assert.EndsWith(" · 1 off", cut, StringComparison.Ordinal);
        Assert.Equal(40, UI.TextCells.Width(cut));
    }

    [Fact]
    public void TheListLines_MarkAnOffAdvisorAtTheEnd()
    {
        Assert.EndsWith(AdvisorText.OffMark, AdvisorText.ListLine(Ops with { Description = "runbooks" }, null, exists: false), StringComparison.Ordinal);
        Assert.Contains(" · runbooks" + AdvisorText.OffMark, AdvisorText.ListLine(Ops with { Description = "runbooks" }, null, exists: false), StringComparison.Ordinal);
        Assert.EndsWith(AdvisorText.OffMark, AdvisorText.Detail(Ops, null, exists: false), StringComparison.Ordinal);
        Assert.DoesNotContain(AdvisorText.OffMark, AdvisorText.ListLine(Dba, null, exists: false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTool_LeavesAnOffAdvisorOut_AndACallNamingItIsRefused_AskingNothing()
    {
        var host = new Fakes.FakeAdvisorHost();
        var ops = host.Add("ops");
        host.Add("dba");
        var settings = new AppSettingsData { Advisors = true, AdvisorList = [Dba, Ops] };
        var tool = new NeonAdvisorTool(host, () => settings, () => "neon", new AdvisorThreads(), () => (Array.Empty<AIFunction>(), "RULES"), () => [], null, null);

        Assert.DoesNotContain("ops", tool.Description, StringComparison.Ordinal);
        Assert.Equal(["dba"], tool.Advisors.Select(e => e.Profile));
        var arguments = new AIFunctionArguments { [NeonAdvisorTool.AdvisorArgument] = "ops", [NeonAdvisorTool.QuestionArgument] = "q" };
        Assert.Equal(AdvisorText.OffError("ops"), await tool.InvokeAsync(arguments));
        Assert.Empty(ops.Requests);

        settings = new AppSettingsData { Advisors = true, AdvisorList = [Dba with { Off = true }, Ops] };
        arguments[NeonAdvisorTool.AdvisorArgument] = "nobody";
        Assert.Equal(AdvisorText.AllOffError, await tool.InvokeAsync(arguments));
        Assert.Empty(tool.Advisors);
    }
}
