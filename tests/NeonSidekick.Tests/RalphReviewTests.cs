using System.Text.Json;
using NeonSidekick.Plans;

namespace NeonSidekick.Tests;

/// <summary>A Ralph review's verdict read (2026-10-18, <see cref="RalphReview"/>): the tool call first, then a verdict line or a lone PASS/FAIL, else unclear.</summary>
public sealed class RalphReviewTests
{
    [Fact]
    public void ACall_GivesTheVerdict_AndItsReason()
    {
        var call = new Dictionary<string, object?> { ["verdict"] = JsonDocument.Parse("\"fail\"").RootElement, ["reason"] = "the test was deleted" };

        Assert.Equal(new RalphVerdict(RalphVerdictKind.Fail, "the test was deleted"), RalphReview.FromCall(call));
        Assert.Equal(RalphVerdictKind.Pass, RalphReview.FromCall(new Dictionary<string, object?> { ["verdict"] = "PASS" })!.Kind);
        Assert.Null(RalphReview.FromCall(new Dictionary<string, object?> { ["verdict"] = "maybe" }));
        Assert.Null(RalphReview.FromCall(null));
    }

    [Theory]
    [InlineData("It builds and the heading is there.\nverdict: pass", "Pass", "It builds and the heading is there.")]
    [InlineData("**Verdict:** FAIL — the README still lacks the link", "Fail", "the README still lacks the link")]
    [InlineData("Looks right.\n\nPASS", "Pass", "Looks right.")]
    [InlineData("verdict: pass\nOn second thought:\nverdict: fail because x", "Fail", "because x")]
    [InlineData("I cannot tell.", "Unclear", "")]
    [InlineData("", "Unclear", "")]
    [InlineData("PASS early\nmore words\nmore\nand more", "Unclear", "")]
    public void Text_GivesAVerdictLine_OrALoneWord_ElseUnclear(string text, string kind, string reason)
    {
        var verdict = RalphReview.FromText(text);

        Assert.Equal(kind, verdict.Kind.ToString());
        Assert.Equal(reason, verdict.Reason);
    }

    [Theory]
    [InlineData("needs_user")]
    [InlineData("needs user")]
    [InlineData("Needs-User")]
    public void NeedsUser_ComesFromACall(string word) =>
        Assert.Equal(new RalphVerdict(RalphVerdictKind.NeedsUser, "only a person can see it"),
            RalphReview.FromCall(new Dictionary<string, object?> { ["verdict"] = word, ["reason"] = "only a person can see it" }));

    [Theory]
    [InlineData("The code is right.\nverdict: needs user — only a person can see the animation", "only a person can see the animation")]
    [InlineData("The code is right.\n**Verdict:** needs_user", "The code is right.")]
    [InlineData("Only a person can see the window.\n\nNEEDS_USER", "Only a person can see the window.")]
    [InlineData("Only a person can see the window.\n\n**NEEDS USER**", "Only a person can see the window.")]   // 2026-10-22, a review: read as unclear
    [InlineData("Only a person can see the window.\nNEEDS-USER", "Only a person can see the window.")]
    public void NeedsUser_ComesFromAVerdictLine_OrALoneWord(string text, string reason)
    {
        // 2026-10-21: an advisor answers in text, so the no-tool fallback reads the third verdict too.
        var verdict = RalphReview.FromText(text);

        Assert.Equal(RalphVerdictKind.NeedsUser, verdict.Kind);
        Assert.Equal(reason, verdict.Reason);
    }
}
