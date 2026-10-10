using NeonSidekick.Advisors;
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

        string output = await Headless("speed up the joins\n");

        Assert.Contains("[tool] neon_advisor -> " + AdvisorText.NotAskedError, output);
        Assert.Equal(2, _chat.Requests.Count);
    }

    [Fact]
    public async Task Headless_AdvisorCommand_Lists_AndAnswersUnderTheAdvisorsName()
    {
        ServerOn1234("llama");
        HeadlessAdvisor();
        _chat.EnqueueText("The foreign key.");

        string output = await Headless("/advisor\n/advisor dba Which index?\n/advisor nobody q\n");

        Assert.Contains("[notice] " + AdvisorText.ListHeading, output);
        Assert.Contains("[notice] " + AdvisorText.ListLine(new AdvisorEntry("dba", "database architect"), new AdvisorServerFacts(AdvisorServer.Scan, AdvisorServer.DefaultModel), exists: true), output);
        Assert.Contains("dba: The foreign key.", output);
        Assert.Contains("[error] \"nobody\" names no single advisor", output);
    }
}
