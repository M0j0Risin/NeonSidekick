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
