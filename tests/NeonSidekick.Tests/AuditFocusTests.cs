using NeonSidekick.Audits;

namespace NeonSidekick.Tests;

/// <summary>The code audit's dimensions (2026-10-18): the words, the default, and the confirming passes' rotation.</summary>
public sealed class AuditFocusTests
{
    [Fact]
    public void Dimensions_AreTheFiveInOrder_AndTheDefaultIsAll()
    {
        Assert.Equal(["security", "correctness", "performance", "maintainability", "tests"], AuditFocus.Dimensions);
        Assert.Equal(AuditFocus.Dimensions, AuditFocus.Default());
        Assert.NotSame(AuditFocus.Default(), AuditFocus.Default());
    }

    [Theory]
    [InlineData("Security", "security")]
    [InlineData(" TESTS ", "tests")]
    [InlineData("all", null)]
    [InlineData("speed", null)]
    public void Parse_TakesADimensionCaseAside(string word, string? expected) => Assert.Equal(expected, AuditFocus.Parse(word));

    [Fact]
    public void Normalize_OrdersAndDedupes_AndReadsNothingAsAll()
    {
        Assert.Equal(["security", "tests"], AuditFocus.Normalize(["tests", "Security", "tests", "nonsense"]));
        Assert.Equal(AuditFocus.Dimensions, AuditFocus.Normalize(null));
        Assert.Equal(AuditFocus.Dimensions, AuditFocus.Normalize([]));
        Assert.Equal(AuditFocus.Dimensions, AuditFocus.Normalize(["nonsense"]));
        Assert.True(AuditFocus.IsAll(AuditFocus.Dimensions));
        Assert.False(AuditFocus.IsAll(["security"]));
    }

    [Fact]
    public void PassFocus_AFindingPassCoversEveryDimension_AConfirmingPassOneInTurn()
    {
        IReadOnlyList<string> two = ["security", "correctness"];
        Assert.Equal("security, correctness", AuditFocus.PassFocus(two, 0).Label);
        Assert.Equal("security", AuditFocus.PassFocus(two, 1).Label);
        Assert.Equal("correctness", AuditFocus.PassFocus(two, 2).Label);
        Assert.Equal("security", AuditFocus.PassFocus(two, 3).Label);
        Assert.Equal(2, AuditFocus.FocusCount(two));
    }

    [Fact]
    public void PassFocus_WithOneDimension_RotatesTheAngles()
    {
        IReadOnlyList<string> one = ["security"];
        Assert.Equal("security", AuditFocus.PassFocus(one, 0).Label);
        Assert.Equal("security: " + AuditText.ConfirmAngles[0], AuditFocus.PassFocus(one, 1).Label);
        Assert.Equal("security: " + AuditText.ConfirmAngles[1], AuditFocus.PassFocus(one, 2).Label);
        Assert.Equal("security: " + AuditText.ConfirmAngles[0], AuditFocus.PassFocus(one, AuditText.ConfirmAngles.Count + 1).Label);
        Assert.Equal(AuditText.ConfirmAngles.Count, AuditFocus.FocusCount(one));
    }

    [Fact]
    public void Label_IsAllForTheFive_ElseTheNames()
    {
        Assert.Equal("all", AuditFocus.Label(AuditFocus.Dimensions));
        Assert.Equal("security, tests", AuditFocus.Label(["tests", "security"]));
    }
}
