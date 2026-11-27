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
        // The model it answers on, the session's llama it borrows (2026-11-22: LinkedModelOf), in the list's columns.
        Assert.Contains("[notice]   dba — database architect · scan · llama ○", output);   // its reasoning disc (2026-11-23)
        Assert.Contains("dba: The foreign key.", output);
        Assert.Contains("[error] \"nobody\" names no single advisor", output);
    }

    /// <summary>Headless <c>/advisor templates</c> and <c>/advisor new</c> (2026-11-22): the list as notices, the first advisor made with none listed, a taken name an error (exit 4).</summary>
    [Fact]
    public async Task Headless_AdvisorTemplatesAndNew_ListAndMake_ATakenNameIsAnError()
    {
        string output = await Headless("/advisor templates\n/advisor new security\n/advisor new security\n", exitCode: 4);

        Assert.Contains("[notice] " + AdvisorText.TemplatesHeading, output);
        Assert.Contains("security — security engineer", output);
        var security = AdvisorTemplates.BuiltIn.Find("security")!;
        var entry = new AdvisorEntry("security", security.Role, security.Description);
        Assert.Contains("[notice] " + AdvisorText.CreatedNotice(entry, advisorsOn: _settings.Current.Advisors), output);
        Assert.Contains("[error] " + AdvisorText.TakenError("security", "security", listed: true), output);
        Assert.Equal([entry], _settings.Current.AdvisorList);
    }

    /// <summary>Headless <c>/advisor list</c> (2026-11-23): the pane is the PC screen's, so an error naming the bare form (exit 4), nothing saved.</summary>
    [Fact]
    public async Task Headless_AdvisorList_NeedsTheScreen_AnError()
    {
        HeadlessAdvisor();

        string output = await Headless("/advisor list\n", exitCode: 4);

        Assert.Contains("[error] " + AdvisorText.ListNeedsScreen, output);
        Assert.Empty(_chat.Requests);
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

    /// <summary>
    /// The saved turn's stamp names the advisor that answered (2026-11-27, the last-3 review's #35): two holding the role whole, one off, so
    /// the pick is the active one where a Find over every advisor found none.
    /// </summary>
    [Fact]
    public async Task Headless_AdvisorCommand_StampsTheTurn_WithThePickedAdvisor()
    {
        ServerOn1234("llama");
        HeadlessAdvisor();
        Profiles.Create(_dir, "dbx", new AppSettingsData());
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "database"), new AdvisorEntry("dbx", "database", Off: true)]);
        _chat.EnqueueText("The foreign key.");

        string output = await Headless("/advisor database Which index?\n");

        Assert.Contains("dba: The foreign key.", output);
        using var store = new NeonSidekick.Sessions.SessionStore(_settings.ProfileDirectory);
        var turn = Assert.Single(store.Load(Assert.Single(store.List(0)).Id)!.Turns);
        Assert.Equal(NeonSidekick.Sessions.SessionText.SideTurnModel(NeonSidekick.Sessions.SessionText.AdvisorSideTurn, "dba"), turn.Model);
    }

    /// <summary>Headless <c>/advisor</c> with the advisor switched off (2026-11-27): refused, exit 4, nothing asked.</summary>
    [Fact]
    public async Task Headless_AdvisorCommand_AnOffAdvisorIsRefused()
    {
        ServerOn1234("llama");
        HeadlessAdvisor();
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "database architect", Off: true)]);

        string output = await Headless("/advisor\n/advisor dba Which index?\n", exitCode: 4);

        Assert.Contains("[error] " + AdvisorText.OffRefusal("dba"), output);
        Assert.Contains(AdvisorText.OffMark, output);
        Assert.Empty(_chat.Requests);
    }
}
