using NeonSidekick.Companion;
using System.Numerics;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests.Companion;

public sealed partial class RobotViewTests
{
    [GeneratedRegex(@"\e\[[0-9;]*m")]
    private static partial Regex Sgr();

    private static string[] Render(PixelBuffer buffer, ColorSystem colors)
    {
        var console = new TestConsole().Colors(colors).EmitAnsiSequences();
        console.Write(new RobotView(buffer));
        return console.Output.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    }

    [Theory]
    [InlineData(ColorSystem.TrueColor)]
    [InlineData(ColorSystem.EightBit)]
    public void TheRobotIsTwentyRowsOfTwentyFourCells(ColorSystem colors)
    {
        var buffer = PixelBuffer.ForRobot();
        RobotPainter.Paint(buffer, HeadPose.Center, Vector2.Zero, 0, RobotPalette.Default);

        var lines = Render(buffer, colors).Take(RobotMetrics.Rows).ToArray();

        Assert.Equal(RobotMetrics.Rows, lines.Length);
        Assert.All(lines, line => Assert.Equal(RobotMetrics.Columns, Sgr().Replace(line, "").Length));
    }

    [Fact]
    public void OnlyTheTopPixelIsAnUpperHalfBlockOnTheTerminalsBackground()
    {
        var buffer = new PixelBuffer(1, 2);
        buffer.Set(0, 0, new Color(255, 0, 0));

        string line = Render(buffer, ColorSystem.TrueColor)[0];

        Assert.Equal("▀", Sgr().Replace(line, ""));
        Assert.Contains("38;2;255;0;0", line);
        Assert.DoesNotContain("48;", line);
    }

    [Fact]
    public void OnlyTheBottomPixelIsALowerHalfBlock()
    {
        var buffer = new PixelBuffer(1, 2);
        buffer.Set(0, 1, new Color(0, 255, 0));

        string line = Render(buffer, ColorSystem.TrueColor)[0];

        Assert.Equal("▄", Sgr().Replace(line, ""));
        Assert.Contains("38;2;0;255;0", line);
        Assert.DoesNotContain("48;", line);
    }

    [Fact]
    public void BothPixelsAreForegroundOverBackground()
    {
        var buffer = new PixelBuffer(1, 2);
        buffer.Set(0, 0, new Color(1, 2, 3));
        buffer.Set(0, 1, new Color(4, 5, 6));

        string line = Render(buffer, ColorSystem.TrueColor)[0];

        Assert.Equal("▀", Sgr().Replace(line, ""));
        Assert.Contains("38;2;1;2;3", line);
        Assert.Contains("48;2;4;5;6", line);
    }

    [Fact]
    public void AnEmptyCellIsAPlainSpace()
    {
        string line = Render(new PixelBuffer(1, 2), ColorSystem.TrueColor)[0];

        Assert.Equal(" ", line);
    }

    [Fact]
    public void ToAnsiProducesTheSameRows()
    {
        var buffer = PixelBuffer.ForRobot();
        RobotPainter.Paint(buffer, new HeadPose(2, -1), Vector2.One, 0, RobotPalette.Default);
        var console = new TestConsole().Colors(ColorSystem.TrueColor).EmitAnsiSequences();

        string ansi = console.ToAnsi(new RobotView(buffer));
        var lines = ansi.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

        Assert.Equal(RobotMetrics.Rows, lines.Length);
        Assert.All(lines, line => Assert.Equal(RobotMetrics.Columns, Sgr().Replace(line, "").Length));
    }

    [Fact]
    public void AnEvenCellIsASpaceOnItsColor()
    {
        var buffer = new PixelBuffer(1, 2);
        buffer.Set(0, 0, new Color(9, 9, 9));
        buffer.Set(0, 1, new Color(9, 9, 9));

        string line = Render(buffer, ColorSystem.TrueColor)[0];

        Assert.Equal(" ", Sgr().Replace(line, ""));
        Assert.Contains("48;2;9;9;9", line);
        Assert.DoesNotContain("38;2;", line);
    }

    [Fact]
    public void JustTheHeadIsTheTopElevenRows()
    {
        var buffer = PixelBuffer.ForRobot();
        RobotPainter.Paint(buffer, HeadPose.Center, Vector2.Zero, 0, RobotPalette.Default);
        var head = new RobotView(buffer, pixelRows: RobotMetrics.HeadPixelRows);

        var lines = Render(buffer, ColorSystem.TrueColor);
        var console = new TestConsole().Colors(ColorSystem.TrueColor).EmitAnsiSequences();
        console.Write(head);
        var headLines = console.Output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

        Assert.Equal((24, 11), (head.Columns, head.Rows));
        Assert.Equal(lines.Take(11), headLines);
    }

    [Fact]
    public void RunsOfTheSameCellAreOneSegmentButLookTheSame()
    {
        var buffer = PixelBuffer.ForRobot();
        RobotPainter.Paint(buffer, new HeadPose(-1, 1), new Vector2(-0.5f, 0.5f), 0, RobotPalette.Default);
        var console = new TestConsole().Colors(ColorSystem.TrueColor).EmitAnsiSequences();

        console.Write(new RobotView(buffer));
        var lines = console.Output.Split('\n').Select(l => l.TrimEnd('\r')).Take(RobotMetrics.Rows).ToArray();

        // Each line is exactly the glyphs the cell rules give, pixel by pixel...
        for (int row = 0; row < RobotMetrics.Rows; row++)
        {
            var expected = new char[RobotMetrics.Columns];
            for (int x = 0; x < RobotMetrics.Columns; x++)
            {
                var top = buffer[x, row * 2];
                var bottom = buffer[x, (row * 2) + 1];
                expected[x] = (top, bottom) switch
                {
                    ({ } t, { } b) when t == b => ' ',
                    ({ }, _) => '▀',
                    (null, { }) => '▄',
                    _ => ' ',
                };
            }

            Assert.Equal(new string(expected), Sgr().Replace(lines[row], ""));
        }

        // ...but fewer color codes than cells (about half, as the robot is drawn: its solid areas share theirs).
        int codes = lines.Sum(l => Sgr().Matches(l).Count);
        int cells = RobotMetrics.Columns * RobotMetrics.Rows;
        Assert.True(codes < cells, $"{codes} color codes for {cells} cells");
    }

    [Fact]
    public void BadSizesThrow()
    {
        var buffer = PixelBuffer.ForRobot();

        Assert.Throws<ArgumentOutOfRangeException>(() => new RobotView(buffer, pixelRows: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RobotView(buffer, pixelRows: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RobotView(buffer, pixelRows: RobotMetrics.Height + 2));
    }
}
