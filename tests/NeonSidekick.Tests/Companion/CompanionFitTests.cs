using NeonSidekick.Companion;
namespace NeonSidekick.Tests.Companion;

public sealed class CompanionFitTests
{
    private static readonly CompanionCanvas Robot = new(RobotMetrics.Width, RobotMetrics.Height, RobotMetrics.HeadPixelRows);

    [Theory]
    [InlineData(24, 20)]
    [InlineData(100, 27)]
    [InlineData(500, 500)] // however much room there is, it never grows
    public void TheWholeRobotWhenItFits(int columns, int rows)
    {
        Assert.Equal(CompanionFit.Full(Robot), CompanionFit.For(Robot, columns, rows));
    }

    [Theory]
    [InlineData(100, 19)]
    [InlineData(100, 12)]
    [InlineData(100, 11)]
    [InlineData(24, 11)]
    public void TooShortForTheBodyIsJustTheHead(int columns, int rows)
    {
        var fit = CompanionFit.For(Robot, columns, rows);

        Assert.Equal(CompanionFit.HeadOnly(Robot), fit);
        Assert.True(fit!.Value.IsHeadOnly);
        Assert.Equal((24, 11), (fit.Value.Columns, fit.Value.Rows));
    }

    [Theory]
    [InlineData(23, 40)]
    [InlineData(100, 10)]
    [InlineData(0, 0)]
    public void TooSmallForEvenThatIsNothing(int columns, int rows)
    {
        Assert.Null(CompanionFit.For(Robot, columns, rows));
    }

    [Fact]
    public void BadArgumentsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CompanionFit.For(Robot, -1, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompanionFit.For(Robot, 24, -1));
    }

    [Fact]
    public void TheSizes()
    {
        Assert.Equal((24, 20), (CompanionFit.Full(Robot).Columns, CompanionFit.Full(Robot).Rows));
        Assert.False(CompanionFit.Full(Robot).IsHeadOnly);
        Assert.Equal((24, 11), (CompanionFit.HeadOnly(Robot).Columns, CompanionFit.HeadOnly(Robot).Rows));
    }

    [Fact]
    public void AnyCharactersCanvasFitsTheSameWay()
    {
        // A made-up character, 30 pixels wide and 50 tall, whose head is its top 20 rows.
        var other = new CompanionCanvas(30, 50, 20);

        Assert.Equal(new CompanionFit(other, 50), CompanionFit.For(other, 30, 25));
        Assert.Equal(new CompanionFit(other, 20), CompanionFit.For(other, 30, 24));
        Assert.Equal((30, 10), (CompanionFit.HeadOnly(other).Columns, CompanionFit.HeadOnly(other).Rows));
        Assert.Null(CompanionFit.For(other, 29, 100));
        Assert.Null(CompanionFit.For(other, 30, 9));
    }
}
