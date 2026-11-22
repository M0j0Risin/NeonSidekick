using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary><c>neon_advisor</c> headless (2026-10-10): the advisor's footer a <c>[notice]</c>, its answer the tool's line, a confirm refused.</summary>
public partial class SidekickAppTests
{
    private void HeadlessAdvisor(bool confirm = false)
    {
        Profiles.Create(_dir, "dba", new AppSettingsData());   // a blank URL: it borrows the run's server, so _chat answers for it too
        _settings.Update(d => { d.Advisors = true; d.AdvisorConfirm = confirm; d.AdvisorList = [new AdvisorEntry("dba", "database architect")]; });
    }

    [Fact]
    public async Task Headless_Advisors_TheFooterIsANotice_TheAnswerIsTheResult()
    {
        ServerOn1234("llama");
        HeadlessAdvisor();
        _chat.Enqueue(FakeChatClient.Call("c1", NeonAdvisorTool.ToolName, new Dictionary<string, object?> { ["advisor"] = "dba", ["question"] = "Which index?" }));
        _chat.EnqueueText("The foreign key.");
        _chat.EnqueueText("Adding it.");

        string output = await Headless("speed up the joins\n");

        Assert.Contains("[notice] dba · llama · ", output);
        Assert.Contains("[tool] neon_advisor -> The foreign key.", output);
        Assert.Contains("Adding it.", output);
        Assert.Contains(AdvisorText.Framing("neon", "database architect", tools: true), _chat.Requests[1][0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Headless_Advisors_UnderConfirm_AreRefused_NoOneToAsk()
    {
        ServerOn1234("llama");
        HeadlessAdvisor(confirm: true);
        _chat.Enqueue(FakeChatClient.Call("c1", NeonAdvisorTool.ToolName, new Dictionary<string, object?> { ["advisor"] = "dba", ["question"] = "Which index?" }));
        _chat.EnqueueText("Alone, then.");

        string output = await Headless("speed up the joins\n", exitCode: SidekickApp.HeadlessRefusedExitCode);

        Assert.Contains("[tool] neon_advisor -> " + AdvisorText.NotAskedError + AllowText.NotAskedTail(AllowKind.Advisor), output);
        Assert.Equal(2, _chat.Requests.Count);
        // Counted since 2026-11-19 (feature idea #45): the summary names its flag, and the run exits 3.
        Assert.Contains("[notice] " + AllowText.RefusedSummary(AllowKind.Advisor, 1), output);
    }

    /// <summary><c>--allow advisor</c> (2026-11-19, feature idea #45): the call under Advisor confirm runs, logged, and nothing is counted.</summary>
    [Fact]
    public async Task Headless_Advisors_UnderConfirm_RunWithAllowAdvisor()
    {
        ServerOn1234("llama");
        HeadlessAdvisor(confirm: true);
        _chat.Enqueue(FakeChatClient.Call("c1", NeonAdvisorTool.ToolName, new Dictionary<string, object?> { ["advisor"] = "dba", ["question"] = "Which index?" }));
        _chat.EnqueueText("Add an index on orders.customer_id.");
        _chat.EnqueueText("Adding it.");
        var lines = new List<string>();
        void Capture(NeonSidekick.Diagnostics.DiagnosticEvent e) => lines.Add(e.Message);
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += Capture;
        string output;
        try
        {
            output = await Headless("speed up the joins\n", options: SidekickOptions.None with { AllowList = ["advisor"] });
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= Capture;
        }

        Assert.DoesNotContain(AdvisorText.NotAskedError, output);
        Assert.Contains(lines, l => l.StartsWith(AllowText.AllowedBy("advisor") + ": dba: Which index?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Headless_AdvisorCommand_Lists_AndAnswersUnderTheAdvisorsName()
    {
        ServerOn1234("llama");
        HeadlessAdvisor();
        _chat.EnqueueText("The foreign key.");

        string output = await Headless("/advisor\n/advisor dba Which index?\n/advisor nobody q\n", exitCode: 4);

        Assert.Contains("[notice] " + AdvisorText.ListHeading, output);
        Assert.Contains("[notice] " + AdvisorText.ListLine(new AdvisorEntry("dba", "database architect"), new AdvisorServerFacts(AdvisorServer.Scan, AdvisorServer.DefaultModel), exists: true), output);
        Assert.Contains("dba: The foreign key.", output);
        Assert.Contains("[error] \"nobody\" names no single advisor", output);
    }

    [Fact]
    public async Task Headless_AdvisorCommand_SendsTheRecentMessages_EvenUnderBrief()
    {
        ServerOn1234("llama");
        HeadlessAdvisor();
        _settings.Update(d => d.AdvisorContext = AdvisorContextMode.Brief);   // the model's calls' setting, not /advisor's (2026-10-10, the user's call)
        _chat.EnqueueText("Postgres 16.");
        _chat.EnqueueText("The foreign key.");

        await Headless("which database do we run?\n/advisor dba Which index?\n");

        string prompt = _chat.Requests[1][^1].Text;
        Assert.StartsWith("Question: Which index?\n\nIts conversation so far", prompt, StringComparison.Ordinal);
        Assert.Contains("\nassistant: Postgres 16.", prompt, StringComparison.Ordinal);
    }
}
