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
}
