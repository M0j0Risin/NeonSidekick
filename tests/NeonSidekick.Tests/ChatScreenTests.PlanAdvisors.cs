using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// Plan mode's advisors bullet (2026-10-10, the user's ask): while the turn offers <c>neon_advisor</c>, the directive asks the model to
/// consult the advisors whose role fits before presenting, and to say so in the plan's Context; otherwise the directive is as ever.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public void PlanDirective_CarriesTheAdvisorsLine_OnlyWithAdvisors()
    {
        IReadOnlyList<AdvisorEntry> advisors = [new("dba", "database architect"), new("ux", "UX designer")];
        string line = PlanText.AdvisorsLine(advisors);

        Assert.Equal(
            "- You have advisors with neon_advisor: dba (database architect); ux (UX designer). Before presenting the plan, consult each one whose role covers part of it, " +
            "with one self-contained question about that part, and weigh the answer. Consult only those whose role fits; skip the rest. " +
            "In ## Context, say which advisors you consulted and what advice you followed or set aside.",
            line);
        string plain = PlanText.Directive("Add a guide", null, 0);
        string withThem = PlanText.Directive("Add a guide", null, 0, advisors);
        Assert.DoesNotContain("advisors", plain);
        Assert.Equal(plain, PlanText.Directive("Add a guide", null, 0, []));
        Assert.Contains("follow its result.\n" + line + "\nNo plan has been presented yet.", withThem);

        var turn = new PlanSession();
        Assert.Null(turn.Turn(new NeonSidekick.Tests.Fakes.EchoTool()));   // plan mode off: no turn
    }

    [Fact]
    public async Task Plan_WithAdvisorsOffered_TheDirectiveNamesThem_ElseNot()
    {
        AdvisorProfiles();
        _chat.EnqueueText("A question first.");
        PushLine("/plan Add a guide");
        PushLine("/exit");
        await RunAsync();
        Assert.Contains(PlanText.AdvisorsLine([new AdvisorEntry("dba", "database architect")]), _chat.Requests[0][0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanDirective_CarriesTheWebLine_OnlyWithTheResearchTools()
    {
        // 2026-10-17, the user's ask: with the web tools on, plan mode looks up the APIs and specs a plan rests on.
        IReadOnlyList<string> research = [WebSearchTool.ToolName, WebFetchTool.ToolName];
        string line = PlanText.WebResearchLine(research);

        Assert.Equal(
            "- You can research on the web with web_search and web_fetch: where the plan depends on something outside the working directory " +
            "(a library's API or version, a specification or standard, a service's limits, a setting's meaning), look it up and read the source rather than guess. " +
            "In ## Context, name the pages the plan relies on.",
            line);
        string plain = PlanText.Directive("Add a guide", null, 0);
        Assert.Equal(plain, PlanText.Directive("Add a guide", null, 0, null, null, []));
        Assert.DoesNotContain("on the web", plain);
        Assert.Contains("use them to research what the plan needs.\n" + line + "\n- Ask what you need", PlanText.Directive("Add a guide", null, 0, null, null, research));

        var gap = new PlanGapState(1, 5, PlanReviewer.Model);
        Assert.Contains("a file, API or setting assumed but not read,", PlanText.GapReviewLines(gap, advisorsOffered: false));
        Assert.Contains("a file, API, specification, version or setting assumed but not read or looked up,", PlanText.Directive("r", ".neon/plans/x.md", 1, null, gap, research));
        Assert.Contains("and on the web for whatever the plan rests on", PlanText.Directive("r", ".neon/plans/x.md", 1, null, gap, research));
        Assert.Equal(PlanText.GapReviewLines(gap, false), PlanText.GapReviewLines(gap, false, web: false));

        Assert.Equal([WebFetchTool.ToolName], PlanTurn.ResearchOf([new NeonSidekick.Tests.Fakes.EchoTool(), new NeonSidekick.Tests.Fakes.FixedTool(WebFetchTool.ToolName, "")]));
    }

    /// <summary>The directive a screen turn sends while planning: the fixture's web tools are on, so it names both research tools (2026-10-17).</summary>
    private static string ScreenPlanDirective(string requirement, string? path, int revision) =>
        PlanText.Directive(requirement, path, revision, null, null, PlanText.ResearchTools);

    [Fact]
    public async Task Plan_WithTheWebToolsOn_TheDirectiveSaysToResearch()
    {
        _settings.Update(d => d.WebTools = true);
        _chat.EnqueueText("A question first.");
        PushLine("/plan Add a guide");
        PushLine("/exit");
        await RunAsync();
        string system = _chat.Requests[0][0].Text;
        Assert.Contains("- You can research on the web with ", system, StringComparison.Ordinal);
        Assert.Contains(PlanText.WebResearchLine(PlanTurn.ResearchOf(_chat.Options[0]!.Tools!.OfType<AIFunction>())), system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_WithTheWebToolsOff_TheDirectiveLeavesTheWebOut()
    {
        _settings.Update(d => d.WebTools = false);
        _chat.EnqueueText("A question first.");
        PushLine("/plan Add a guide");
        PushLine("/exit");
        await RunAsync();
        Assert.Contains(PlanText.Directive("Add a guide", null, 0), _chat.Requests[0][0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("You can research on the web", _chat.Requests[0][0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_WithTheAdvisorToolOffOnTools_TheDirectiveLeavesThemOut()
    {
        AdvisorProfiles();
        _settings.Update(d => d.ToolsDisabled = [NeonAdvisorTool.ToolName]);
        _chat.EnqueueText("A question first.");
        PushLine("/plan Add a guide");
        PushLine("/exit");
        await RunAsync();
        Assert.Contains("PLAN MODE is on.", _chat.Requests[0][0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("You have advisors with", _chat.Requests[0][0].Text, StringComparison.Ordinal);
    }
}
