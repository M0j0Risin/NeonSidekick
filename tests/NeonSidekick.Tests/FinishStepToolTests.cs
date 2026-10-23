using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;

namespace NeonSidekick.Tests;

/// <summary><c>finish_step</c> (2026-10-18, <see cref="FinishStepTool"/>): the schema, each refusal in its order, a recorded report, and no script may call it.</summary>
public sealed class FinishStepToolTests
{
    private RalphLoop? _loop = new(new RalphSnapshot(".neon/plans/g.md", "/w", "G", null, null, false, "pwsh", RalphReviewer.Off, 0, 0, 4, 600, 0, 1), 2);
    private readonly List<FinishReport> _reported = [];
    private readonly FinishStepTool _tool;
    private static readonly IReadOnlyList<PlanStep> Steps = PlanSteps.Parse("## Steps\n- [ ] One\n- [ ] Two\n");

    public FinishStepToolTests()
    {
        _tool = new FinishStepTool(() => _loop, _reported.Add);
        _loop!.SetChildren(Steps);
        _loop.BeginPass(Steps[0], null);
    }

    private async Task<string> Call(string json) =>
        (string)(await _tool.InvokeAsync(new AIFunctionArguments(JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()))))!;

    [Fact]
    public void TheSchema_DeclaresTheKeysRead()
    {
        var properties = _tool.JsonSchema.GetProperty("properties");
        Assert.Equal(FinishStepTool.ToolName, _tool.Name);
        Assert.Equal([FinishStepTool.OutcomeArgument, FinishStepTool.SummaryArgument, FinishStepTool.VerifiedArgument, FinishStepTool.NotesArgument, FinishStepTool.LearnedArgument, FinishStepTool.SubstepsArgument], properties.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["outcome", "summary"], _tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task ADoneReport_IsRecorded_AndSaid()
    {
        string result = await Call("""{"outcome":"done","summary":"Wrote the outline.","verified":["dotnet build: ok"],"learned":["docs live in docs/"],"notes":"none"}""");

        Assert.Equal(RalphText.RecordedResult(RalphOutcome.Done), result);
        var report = Assert.Single(_reported);
        Assert.Equal((RalphOutcome.Done, "Wrote the outline.", "none"), (report.Outcome, report.Summary, report.Notes));
        Assert.Equal(["docs live in docs/"], report.Learned);
        Assert.Same(report, _loop!.Report);
        Assert.Equal(RalphText.AlreadyFinishedResult, await Call("""{"outcome":"done","summary":"again","verified":["x"]}"""));
    }

    [Theory]
    [InlineData("""{"outcome":"finished","summary":"x"}""", "Error: outcome must be done, partial, blocked or split; got finished")]
    [InlineData("""{"outcome":"done","summary":"  "}""", RalphText.SummaryRequiredResult)]
    [InlineData("""{"outcome":"done","summary":"x"}""", RalphText.VerifiedRequiredResult)]
    [InlineData("""{"outcome":"done","summary":"x","verified":[1]}""", "Error: verified must be a list of strings; got [1]")]
    [InlineData("""{"outcome":"split","summary":"x"}""", RalphText.SubstepsRequiredResult)]
    [InlineData("""{"outcome":"split","summary":"x","substeps":[{"text":"a","done_when":"b"}]}""", RalphText.SubstepsCountResult)]
    [InlineData("""{"outcome":"split","summary":"x","substeps":[{"text":"a","done_when":"b"},{"text":"c"}]}""", RalphText.SubstepDoneWhenResult)]
    [InlineData("""{"outcome":"partial","summary":"x","substeps":[{"text":"a","done_when":"b"},{"text":"c","done_when":"d"}]}""", RalphText.SubstepsOnlyOnSplitResult)]
    public async Task EachRefusal_SavesNothing(string json, string expected)
    {
        Assert.Equal(expected, await Call(json));
        Assert.Empty(_reported);
        Assert.Null(_loop!.Report);
    }

    [Fact]
    public async Task ASplit_IsRecorded_WithItsSubsteps()
    {
        string result = await Call("""{"outcome":"split","summary":"too big","substeps":[{"text":"First","done_when":"it builds"},{"text":"Second","done_when":"tests pass"}]}""");

        Assert.Equal(RalphText.RecordedResult(RalphOutcome.Split), result);
        Assert.Equal([new PlanSubstep("First", "it builds"), new PlanSubstep("Second", "tests pass")], _reported[0].Substeps);
    }

    [Fact]
    public async Task OutsideAPass_ItIsRefused()
    {
        _loop = null;
        Assert.Equal(RalphText.NotInPassResult, await Call("""{"outcome":"done","summary":"x","verified":["y"]}"""));
    }
}
