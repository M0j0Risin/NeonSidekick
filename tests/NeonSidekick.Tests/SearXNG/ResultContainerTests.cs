// SPDX-License-Identifier: AGPL-3.0-or-later

using NeonSidekick.Tests.Fakes;
using SearXNG.Net.Results;

namespace SearXNG.Net.Tests;

/// <summary>Expected scores and orders are worked out by hand from searx/results.py.</summary>
public class ResultContainerTests
{
    private static ResultContainer Container(params IEngine[] engines) =>
        new(engines.ToDictionary(e => e.Name, StringComparer.Ordinal));

    private static EngineResult Hit(string path, string title = "title", string content = "content") =>
        new("https://example.com/" + path, title, content);

    [Fact]
    public void Score_IsWeightOverPosition_ForOneEngine()
    {
        var container = Container(new FakeEngine("a"));
        container.Extend("a", [Hit("1"), Hit("2"), Hit("3"), Hit("4")]);

        var results = container.GetOrderedResults();

        Assert.Equal(["https://example.com/1", "https://example.com/2", "https://example.com/3", "https://example.com/4"], results.Select(r => r.Url));
        Assert.Equal(1.0, results[0].Score, 10);
        Assert.Equal(0.5, results[1].Score, 10);
        Assert.Equal(0.25, results[3].Score, 10);
    }

    [Fact]
    public void Score_RewardsTwoEnginesFindingTheSamePage()
    {
        var container = Container(new FakeEngine("a"), new FakeEngine("b"));
        container.Extend("a", [Hit("first"), Hit("second"), Hit("shared")]);
        container.Extend("b", [Hit("shared")]);

        var results = container.GetOrderedResults();

        // shared: positions 3 and 1, so weight = 1 * 2 and score = 2/3 + 2/1.
        Assert.Equal("https://example.com/shared", results[0].Url);
        Assert.Equal((2.0 / 3) + 2.0, results[0].Score, 10);
        Assert.Equal([3, 1], results[0].Positions);
        Assert.Equal(["a", "b"], results[0].Engines.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Score_MultipliesEngineWeights()
    {
        var container = Container(new FakeEngine("a") { Weight = 2 }, new FakeEngine("b") { Weight = 0.5 }, new FakeEngine("c") { Weight = 3 });
        container.Extend("a", [Hit("light")]);
        container.Extend("b", [Hit("light")]);
        container.Extend("c", [Hit("heavy")]);

        var results = container.GetOrderedResults();

        // light: 2 * 0.5 * 2 sightings = 2, then 2/1 + 2/1 = 4. heavy: 3/1 = 3.
        Assert.Equal(4.0, results[0].Score, 10);
        Assert.Equal(3.0, results[1].Score, 10);
    }

    [Fact]
    public void Merge_KeepsTheLongerTitleAndContent()
    {
        var container = Container(new FakeEngine("a"), new FakeEngine("b"));
        container.Extend("a", [Hit("x", "short", "a long enough snippet")]);
        container.Extend("b", [Hit("x", "a longer title", "brief")]);

        var result = Assert.Single(container.GetOrderedResults());

        Assert.Equal("a longer title", result.Title);
        Assert.Equal("a long enough snippet", result.Content);
    }

    [Fact]
    public void Merge_TreatsHttpAndHttpsAsTheSamePage_AndPrefersHttps()
    {
        var container = Container(new FakeEngine("a"), new FakeEngine("b"));
        container.Extend("a", [new EngineResult("http://example.com/x", "t", "c")]);
        container.Extend("b", [new EngineResult("https://example.com/x", "t", "c")]);

        Assert.Equal("https://example.com/x", Assert.Single(container.GetOrderedResults()).Url);
    }

    [Fact]
    public void Merge_KeepsHttpsWhenTheLaterSightingIsHttp()
    {
        var container = Container(new FakeEngine("a"), new FakeEngine("b"));
        container.Extend("a", [new EngineResult("https://example.com/x", "t", "c")]);
        container.Extend("b", [new EngineResult("http://example.com/x", "t", "c")]);

        Assert.Equal("https://example.com/x", Assert.Single(container.GetOrderedResults()).Url);
    }

    [Theory]
    [InlineData("https://example.com/x", "https://www.example.com/x")]
    [InlineData("https://example.com/x", "https://example.com/x/")]
    [InlineData("https://example.com/x", "https://example.com/x?a=1")]
    [InlineData("https://example.com/x", "https://example.com/x#part")]
    public void Merge_KeepsPagesApartWhenAnythingButTheSchemeDiffers(string one, string other)
    {
        var container = Container(new FakeEngine("a"));
        container.Extend("a", [new EngineResult(one, "t", "c"), new EngineResult(other, "t", "c")]);

        Assert.Equal(2, container.GetOrderedResults().Count);
    }

    [Fact]
    public void Extend_CountsARepeatWithinOneEngineAsAnotherSighting()
    {
        var container = Container(new FakeEngine("a"));
        container.Extend("a", [Hit("x"), Hit("y"), Hit("x")]);

        var x = container.GetOrderedResults().Single(r => r.Url.EndsWith("/x", StringComparison.Ordinal));

        Assert.Equal([1, 3], x.Positions);
        Assert.Equal((2.0 / 1) + (2.0 / 3), x.Score, 10);
    }

    [Fact]
    public void Extend_DropsAContentEqualToTheTitle()
    {
        var container = Container(new FakeEngine("a"));
        container.Extend("a", [Hit("x", "same", "same")]);

        Assert.Equal("", Assert.Single(container.GetOrderedResults()).Content);
    }

    [Fact]
    public void Extend_SkipsAnUnusableUrl_WithoutUsingUpAPosition()
    {
        var container = Container(new FakeEngine("a"));
        container.Extend("a", [new EngineResult("javascript:alert(1)", "bad", ""), Hit("good")]);

        var result = Assert.Single(container.GetOrderedResults());

        Assert.Equal("https://example.com/good", result.Url);
        Assert.Equal([1], result.Positions);
    }

    [Fact]
    public void Order_IsStable_ForEqualScores()
    {
        var container = Container(new FakeEngine("a"), new FakeEngine("b"));
        container.Extend("a", [Hit("from-a")]);
        container.Extend("b", [Hit("from-b")]);

        Assert.Equal(["https://example.com/from-a", "https://example.com/from-b"], container.GetOrderedResults().Select(r => r.Url));
    }

    [Fact]
    public void Order_PullsALaterResultUpToItsCategoryGroup()
    {
        // By score: g1 (1), i1 (0.9), g2 (0.5). Grouping puts g2 straight after g1, ahead of i1.
        var container = Container(new FakeEngine("g"), new FakeEngine("i") { Category = "images", Weight = 0.9 });
        container.Extend("g", [Hit("g1"), Hit("g2")]);
        container.Extend("i", [Hit("i1")]);

        Assert.Equal(["https://example.com/g1", "https://example.com/g2", "https://example.com/i1"], container.GetOrderedResults().Select(r => r.Url));
    }

    [Fact]
    public void Order_StopsGroupingAfterEightJoiners()
    {
        // i1 scores 0.9, between g1 and the rest. g2..g9 join g1 (eight joiners), which uses the group up, so g10 lands after i1.
        var container = Container(new FakeEngine("g"), new FakeEngine("i") { Category = "images", Weight = 0.9 });
        container.Extend("g", Enumerable.Range(1, 10).Select(n => Hit("g" + n)));
        container.Extend("i", [Hit("i1")]);

        var paths = container.GetOrderedResults().Select(r => r.Url["https://example.com/".Length..]);

        Assert.Equal(["g1", "g2", "g3", "g4", "g5", "g6", "g7", "g8", "g9", "i1", "g10"], paths);
    }
}
