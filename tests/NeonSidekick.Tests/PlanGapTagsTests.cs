using NeonSidekick.Plans;

namespace NeonSidekick.Tests;

/// <summary>
/// A plan gap's severity tag (2026-11-17, phase 3 of the gap and audit improvements: logged only, never acted on): the parser's forms, the
/// shadow's log line, and a relabeled gap still the same gap to the stall check.
/// </summary>
public sealed class PlanGapTagsTests
{
    [Theory]
    [InlineData("[high] No step migrates the table", PlanGapSeverity.High, "No step migrates the table")]
    [InlineData("[medium] The port is never chosen", PlanGapSeverity.Medium, "The port is never chosen")]
    [InlineData("[low] Step 3 has no Done when:", PlanGapSeverity.Low, "Step 3 has no Done when:")]
    [InlineData("(high) No step migrates the table", PlanGapSeverity.High, "No step migrates the table")]
    [InlineData("(Medium) The port is never chosen", PlanGapSeverity.Medium, "The port is never chosen")]
    [InlineData("high: No step migrates the table", PlanGapSeverity.High, "No step migrates the table")]
    [InlineData("LOW : Step 3 has no check", PlanGapSeverity.Low, "Step 3 has no check")]
    [InlineData("  [ High ]   spaced out  ", PlanGapSeverity.High, "spaced out")]
    [InlineData("[HIGH]no space", PlanGapSeverity.High, "no space")]
    public void ALeadingTag_IsRead_InEachForm_CaseAndSpacesAside(string gap, PlanGapSeverity severity, string text) =>
        Assert.Equal((severity, text), PlanGapTags.Read(gap));

    [Theory]
    [InlineData("No tag at all")]
    [InlineData("High-level steps are missing")]
    [InlineData("Highly unclear which port")]
    [InlineData("The migration is [high] risk")]
    [InlineData("Step 2: high: a tag in mid-line")]
    [InlineData("[critical] not a plan tag")]
    [InlineData("low-hanging fruit left out")]
    public void AnythingElse_IsUntagged_AndKeptWhole(string gap) =>
        Assert.Equal((PlanGapSeverity.Untagged, gap.Trim()), PlanGapTags.Read(gap));

    [Fact]
    public void Serious_IsAnythingAboveLow_UntaggedIncluded()
    {
        Assert.True(PlanGapTags.Serious(PlanGapSeverity.High));
        Assert.True(PlanGapTags.Serious(PlanGapSeverity.Medium));
        Assert.False(PlanGapTags.Serious(PlanGapSeverity.Low));
        Assert.True(PlanGapTags.Serious(PlanGapSeverity.Untagged));
    }

    [Fact]
    public void ARelabeledGap_IsTheSameGap()
    {
        Assert.True(PlanGapLoop.SameGaps(["[medium] The port is never chosen"], ["[high] the port is never chosen"]));
        Assert.True(PlanGapLoop.SameGaps(["High: The port is never chosen"], ["The port is never chosen"]));
        Assert.False(PlanGapLoop.SameGaps(["[low] The port is never chosen"], ["[low] The host is never chosen"]));
    }

    [Fact]
    public void TheShadowLogLine_HasTheAuditsShape_WithThePlansBuckets()
    {
        Assert.Equal("plan gaps shadow: pass 3: 1 high, 2 low, 1 untagged (serious); shadow streak 0 of 2; real: found something",
            PlanText.ShadowLogLine(3, new GapShadowPass([1, 0, 2, 1], 0, true, true), 2, null, false, 0));
        Assert.Equal("plan gaps shadow: pass 4: 2 low; shadow streak 2 of 2, would converge; real: found something",
            PlanText.ShadowLogLine(4, new GapShadowPass([0, 0, 2, 0], 2, true, false), 2, null, false, 0));
        Assert.Equal("plan gaps shadow: pass 5: no gaps; shadow streak 2 of 2, would converge; real: clean 1 of 2",   // past its target, shown at it (2026-11-24)
            PlanText.ShadowLogLine(5, new GapShadowPass([0, 0, 0, 0], 3, true, false), 2, null, true, 1));
        Assert.Equal("plan gaps shadow: pass 6: no present_plan; shadow streak 2 of 2, would converge; real: no report",
            PlanText.ShadowLogLine(6, new GapShadowPass([0, 0, 0, 0], 3, false, false), 2, PlanGapStop.NoRevision, false, 0));
        Assert.StartsWith(PlanText.ShadowLogPrefix + " pass ", PlanText.ShadowLogLine(1, GapShadow.Empty, 2, PlanGapStop.Converged, true, 2));
    }

    [Fact]
    public void TheShadow_MovesOnceAPass_AndAPassWithNoCallLeavesIt()
    {
        var shadow = new GapShadow();
        shadow.Note(1, serious: true, [(int)PlanGapSeverity.High]);
        Assert.Equal(0, shadow.End(1).Streak);
        shadow.Note(2, serious: false, [(int)PlanGapSeverity.Low]);
        shadow.Note(2, serious: false, []);
        var two = shadow.End(2);
        Assert.Equal((1, false), (two.Streak, two.Serious));
        Assert.Equal([0, 0, 1, 0], two.Severities);
        var three = shadow.End(3);
        Assert.Equal((1, false), (three.Streak, three.Called));
        Assert.Equal([0, 0, 0, 0], three.Severities);
    }
}
