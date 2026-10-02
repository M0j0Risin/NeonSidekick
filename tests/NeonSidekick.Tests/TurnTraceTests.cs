using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests;

public class TurnTraceTests
{
    private static TurnEvent Call(string name, string id) => new TurnEvent.ToolCall(name, id, "{}");
    private static TurnEvent Result(string name, string id, string text) => new TurnEvent.ToolResult(name, id, text);

    [Fact]
    public void Fresh_CountsNothing()
    {
        var trace = new TurnTrace();

        Assert.Equal(0, trace.ToolCalls);
        Assert.Equal(0, trace.Errors);
        Assert.False(trace.Recovered);
        Assert.False(trace.WroteSkill);
        Assert.Empty(trace.LoadedSkills);
        Assert.Equal("0 tool calls, 0 errors", trace.ToString());
    }

    [Fact]
    public void TheModelsOwnCalls_AreCounted_TheOpeningPairsAreNot()
    {
        var trace = new TurnTrace();
        trace.Observe(Call("get_current_time", Assistant.OpeningClockCallId));
        trace.Observe(Result("get_current_time", Assistant.OpeningClockCallId, "2026-09-11"));
        trace.Observe(Call("get_working_directory", Assistant.OpeningCwdCallId));
        trace.Observe(Result("get_working_directory", Assistant.OpeningCwdCallId, "Error: none"));   // an opening error is no error either
        trace.Observe(Call("recall_memory", Assistant.OpeningMemoryCallId));
        trace.Observe(Result("recall_memory", Assistant.OpeningMemoryCallId, "(no memories)"));
        trace.Observe(new TurnEvent.TextDelta("hi"));
        trace.Observe(new TurnEvent.Notice("x", IsError: true));
        trace.Observe(Call("read_file", "call_1"));
        trace.Observe(Result("read_file", "call_1", "1: text"));
        trace.Observe(Call(LoadSkillTool.ToolName, "call_2"));   // the model's own load counts (a /skill activation was the app's until later on 2026-09-18)
        trace.Observe(Result(LoadSkillTool.ToolName, "call_2", "<skill_content name=\"docker\">\nbody\n</skill_content>"));

        Assert.Equal(2, trace.ToolCalls);
        Assert.Equal(0, trace.Errors);
        Assert.Equal(["docker"], trace.LoadedSkills);
        Assert.Equal("2 tool calls, 0 errors, loaded: docker", trace.ToString());
    }

    [Fact]
    public void ErrorsAfterLoad_CountOnlyTheErrorsAfterEachSkillsFirstLoad_AndAbsorbAddsThemUp()
    {
        // 2026-10-02, the skill records' uses: an error before the load says nothing about the skill.
        var trace = new TurnTrace();
        trace.Observe(Call("read_file", "c1"));
        trace.Observe(Result("read_file", "c1", "Error: before any load"));
        trace.Observe(Call(LoadSkillTool.ToolName, "c2"));
        trace.Observe(Result(LoadSkillTool.ToolName, "c2", "<skill_content name=\"docker\">\nbody\n</skill_content>"));
        trace.Observe(Call("run_command", "c3"));
        trace.Observe(Result("run_command", "c3", "Error: exit 1"));
        trace.Observe(Call(LoadSkillTool.ToolName, "c4"));
        trace.Observe(Result(LoadSkillTool.ToolName, "c4", "<skill_content name=\"git-push\">\nbody\n</skill_content>"));
        trace.Observe(Call(LoadSkillTool.ToolName, "c5"));
        trace.Observe(Result(LoadSkillTool.ToolName, "c5", "<skill_content name=\"docker\">\nbody\n</skill_content>"));   // a second load: its count goes on
        trace.Observe(Call("run_command", "c6"));
        trace.Observe(Result("run_command", "c6", "Error: exit 2"));

        Assert.Equal(new Dictionary<string, int> { ["docker"] = 2, ["git-push"] = 1 }, trace.ErrorsAfterLoad);
        Assert.Equal(3, trace.Errors);

        var tally = new TurnTrace();
        tally.Absorb(trace);
        tally.Absorb(trace);
        Assert.Equal(new Dictionary<string, int> { ["docker"] = 4, ["git-push"] = 2 }, tally.ErrorsAfterLoad);
        Assert.Empty(new TurnTrace().ErrorsAfterLoad);
    }

    [Fact]
    public void AnErrorResult_ThenASuccess_IsARecovery_AnotherErrorUndoesIt()
    {
        var trace = new TurnTrace();
        trace.Observe(Call("read_file", "c1"));
        trace.Observe(Result("read_file", "c1", "Error: no such file 'x'."));
        Assert.Equal(1, trace.Errors);
        Assert.False(trace.Recovered);

        trace.Observe(Call("boom", "c2"));
        trace.Observe(Result("boom", "c2", "Error: boom failed: kaboom"));
        Assert.Equal(2, trace.Errors);

        trace.Observe(Call("read_file", "c3"));
        trace.Observe(Result("read_file", "c3", "1: text"));
        Assert.True(trace.Recovered);

        trace.Observe(Call("nope", "c4"));
        trace.Observe(Result("nope", "c4", "Error: unknown tool 'nope'."));
        Assert.False(trace.Recovered);
        Assert.Equal(4, trace.ToolCalls);
        Assert.Equal(3, trace.Errors);
        Assert.Equal("4 tool calls, 3 errors", trace.ToString());
    }

    [Fact]
    public void ASuccessfulSkillWrite_AndTheSkillsTheModelLoaded_AreNoted()
    {
        var trace = new TurnTrace();
        trace.Observe(Call(LoadSkillTool.ToolName, "c1"));
        trace.Observe(Result(LoadSkillTool.ToolName, "c1", "<skill_content name=\"docker-deploy\">\nsteps\n</skill_content>"));
        trace.Observe(Call(LoadSkillTool.ToolName, "c2"));
        trace.Observe(Result(LoadSkillTool.ToolName, "c2", "Error: there is no skill named 'x'; the skills are: docker-deploy"));
        trace.Observe(Call(SkillEditorTool.ToolName, "c3"));
        trace.Observe(Result(SkillEditorTool.ToolName, "c3", "Error: the skill 'docker-deploy' already exists in the profile skills; call again with action update to change it"));
        Assert.False(trace.WroteSkill);

        trace.Observe(Call(SkillEditorTool.ToolName, "c4"));
        trace.Observe(Result(SkillEditorTool.ToolName, "c4", "updated skill 'docker-deploy' (profile, 1,234 bytes)"));

        Assert.True(trace.WroteSkill);
        Assert.True(trace.Recovered);
        Assert.Equal(["docker-deploy"], trace.LoadedSkills);
        Assert.Equal("4 tool calls, 2 errors, recovered, wrote a skill, loaded: docker-deploy", trace.ToString());
    }

    private static TurnTrace Clean(int calls)
    {
        var t = new TurnTrace();
        for (int i = 0; i < calls; i++)
        {
            t.Observe(Call("read_file", "k" + i));
            t.Observe(Result("read_file", "k" + i, "1: ok"));
        }

        return t;
    }

    private static TurnTrace Failing(bool thenRecovered)
    {
        var t = new TurnTrace();
        t.Observe(Call("read_file", "e1"));
        t.Observe(Result("read_file", "e1", "Error: no"));
        if (thenRecovered)
        {
            t.Observe(Call("read_file", "e2"));
            t.Observe(Result("read_file", "e2", "1: ok"));
        }

        return t;
    }

    [Fact]
    public void Absorb_SumsTheTurns_AndReadsRecoveryAcrossThem()
    {
        var tally = new TurnTrace();
        Assert.Equal(0, tally.Turns);

        tally.Absorb(Clean(2));
        tally.Absorb(Clean(2));
        Assert.Equal(4, tally.ToolCalls);
        Assert.Equal(0, tally.Errors);
        Assert.False(tally.Recovered);
        Assert.Equal(2, tally.Turns);
        Assert.Equal("4 tool calls, 0 errors, over 2 turns", tally.ToString());

        // An unrecovered error, then a clean turn with calls: recovered across the turns.
        tally.Absorb(Failing(thenRecovered: false));
        Assert.Equal(1, tally.Errors);
        Assert.False(tally.Recovered);
        tally.Absorb(Clean(0));                 // a turn with no calls says nothing
        Assert.False(tally.Recovered);
        tally.Absorb(Clean(1));
        Assert.True(tally.Recovered);
        Assert.Equal("6 tool calls, 1 error, recovered, over 5 turns", tally.ToString());

        // A later turn with its own error brings its own verdict.
        tally.Absorb(Failing(thenRecovered: false));
        Assert.Equal(2, tally.Errors);
        Assert.False(tally.Recovered);
        tally.Absorb(Failing(thenRecovered: true));
        Assert.Equal(3, tally.Errors);
        Assert.True(tally.Recovered);
    }

    [Fact]
    public void Absorb_KeepsAWrittenSkill_AndTheLoadedNames()
    {
        var wrote = new TurnTrace();
        wrote.Observe(Call(LoadSkillTool.ToolName, "l1"));
        wrote.Observe(Result(LoadSkillTool.ToolName, "l1", "<skill_content name=\"a\">"));
        wrote.Observe(Call(SkillEditorTool.ToolName, "w1"));
        wrote.Observe(Result(SkillEditorTool.ToolName, "w1", "updated skill 'a' (profile, 10 bytes)"));
        var loaded = new TurnTrace();
        loaded.Observe(Call(LoadSkillTool.ToolName, "l2"));
        loaded.Observe(Result(LoadSkillTool.ToolName, "l2", "<skill_content name=\"b\">"));

        var tally = new TurnTrace();
        tally.Absorb(wrote);
        tally.Absorb(loaded);

        Assert.True(tally.WroteSkill);
        Assert.Equal(["a", "b"], tally.LoadedSkills);
        Assert.Equal(3, tally.ToolCalls);
    }

    [Fact]
    public void ToolNames_AreTheModelsOwnDistinctNames_InFirstCallOrder_AndAbsorbMerges()
    {
        // The session store's tool_names cell (2026-09-19): a seeded pair never counts, a repeat never doubles.
        var trace = new TurnTrace();
        trace.Observe(new TurnEvent.ToolCall("get_current_time", Assistant.OpeningClockCallId, "{}"));
        trace.Observe(new TurnEvent.ToolResult("get_current_time", Assistant.OpeningClockCallId, "now"));
        trace.Observe(new TurnEvent.ToolCall("read_file", "c1", "{}"));
        trace.Observe(new TurnEvent.ToolResult("read_file", "c1", "1: ok"));
        trace.Observe(new TurnEvent.ToolCall("edit_file", "c2", "{}"));
        trace.Observe(new TurnEvent.ToolResult("edit_file", "c2", "Error: no"));
        trace.Observe(new TurnEvent.ToolCall("read_file", "c3", "{}"));
        trace.Observe(new TurnEvent.ToolResult("read_file", "c3", "1: ok"));
        Assert.Equal(["read_file", "edit_file"], trace.ToolNames);

        var next = new TurnTrace();
        next.Observe(new TurnEvent.ToolCall("web_search", "c4", "{}"));
        next.Observe(new TurnEvent.ToolCall("read_file", "c5", "{}"));
        var tally = new TurnTrace();
        tally.Absorb(trace);
        tally.Absorb(next);
        Assert.Equal(["read_file", "edit_file", "web_search"], tally.ToolNames);
        Assert.Empty(new TurnTrace().ToolNames);
    }

    [Theory]
    [InlineData("Error: x", true)]
    [InlineData("Error: a failed: b", true)]
    [InlineData("error: lowercase is the model's, not ours", false)]
    [InlineData("1: Error: a file that starts with the word", false)]
    [InlineData("", false)]
    public void IsError_IsTheAppsOwnPrefix(string text, bool expected)
    {
        Assert.Equal(expected, TurnTrace.IsError(text));
    }

    [Theory]
    [InlineData("<skill_content name=\"a-b\">\nx", "a-b")]
    [InlineData("<skill_content name=\"\">", null)]
    [InlineData("<skill_file skill=\"a\" path=\"b\">", null)]
    [InlineData("Error: nope", null)]
    public void LoadedName_ReadsTheOpeningTag(string text, string? expected)
    {
        Assert.Equal(expected, TurnTrace.LoadedName(text));
    }
}
