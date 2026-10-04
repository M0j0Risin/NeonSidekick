using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class ImageStripTests
{
    private static ImageThumbnail Tile(int width, int height) => new(width, height, Enumerable.Repeat(Color.Red, width * height).ToArray());

    private static List<string> Lines(int consoleWidth, params ImageThumbnail[] tiles)
    {
        using var console = new TestConsole();
        console.Profile.Width = consoleWidth;
        console.Write(new ImageStrip(tiles));
        return console.Lines.ToList();
    }

    private static string Row(int cells) => new('▀', cells);

    /// <summary>The strip's margin (2026-10-03): the prompt glyph's two cells.</summary>
    private const string In = "  ";

    [Fact]
    public void TwoTiles_ShareARow_WithTheGapBetween()
    {
        var lines = Lines(240, Tile(48, 24), Tile(48, 24));

        Assert.Equal(12, lines.Count);
        Assert.All(lines, line => Assert.Equal(In + Row(48) + "  " + Row(48), line));
    }

    [Fact]
    public void Tiles_WrapAtTheWidth_WithABlankRowBetween()
    {
        var lines = Lines(100, Tile(48, 24), Tile(48, 24), Tile(48, 24));

        Assert.Equal(25, lines.Count);
        Assert.All(lines.Take(12), line => Assert.Equal(In + Row(48) + "  " + Row(48), line));
        Assert.Equal(" ", lines[12]);
        Assert.All(lines.Skip(13), line => Assert.Equal(In + Row(48), line));
    }

    [Fact]
    public void AShortTileLast_LeavesNothingTrailing()
    {
        var lines = Lines(240, Tile(48, 24), Tile(12, 8));

        Assert.Equal(12, lines.Count);
        Assert.All(lines.Take(4), line => Assert.Equal(In + Row(48) + "  " + Row(12), line));
        Assert.All(lines.Skip(4), line => Assert.Equal(In + Row(48), line));
    }

    [Fact]
    public void AShortTileFirst_IsPaddedBeneath_SoTheNextStaysAligned()
    {
        var lines = Lines(240, Tile(12, 8), Tile(48, 24));

        Assert.Equal(12, lines.Count);
        Assert.All(lines.Take(4), line => Assert.Equal(In + Row(12) + "  " + Row(48), line));
        Assert.All(lines.Skip(4), line => Assert.Equal(new string(' ', 16) + Row(48), line));
    }

    /// <summary>
    /// The margin (2026-10-03, the user's ask: the picture under the <c>[</c> of <c>[Image #1]</c>): the prompt glyph's width,
    /// the tiles packing in what is left; a tile as wide as the window stands in only as far as it still fits.
    /// </summary>
    [Fact]
    public void TheStrip_StandsInByThePromptGlyph_AsFarAsTheWidestTileFits()
    {
        Assert.Equal(TextCells.Width(InputLine.PromptGlyph), ImageStrip.Indent);
        Assert.Equal(2, ImageStrip.Indent);

        Assert.All(Lines(48, Tile(48, 4)), line => Assert.Equal(Row(48), line));
        Assert.All(Lines(49, Tile(48, 4)), line => Assert.Equal(" " + Row(48), line));
        Assert.Equal(2, Lines(100, Tile(48, 4), Tile(48, 4)).Count);   // 2 + 48 + 2 + 48 = 100: one row of two lines
        Assert.Equal(5, Lines(99, Tile(48, 4), Tile(48, 4)).Count);    // one cell short: two rows and the blank between
    }

    [Fact]
    public void ATileWiderThanTheWidth_TakesARowAlone()
    {
        var rows = ImageStrip.Pack([Tile(48, 2), Tile(60, 2), Tile(10, 2)], 50);

        Assert.Equal(3, rows.Count);
        Assert.Equal(48, Assert.Single(rows[0]).Width);
        Assert.Equal(60, Assert.Single(rows[1]).Width);
        Assert.Equal(10, Assert.Single(rows[2]).Width);
    }

    [Fact]
    public void Pack_FitsExactly_CountsTheGap()
    {
        var rows = ImageStrip.Pack([Tile(20, 2), Tile(20, 2), Tile(20, 2)], 64);   // 20 + 2 + 20 + 2 + 20 = 64

        Assert.Equal(3, Assert.Single(rows).Count);
        Assert.Equal(2, ImageStrip.Pack([Tile(20, 2), Tile(20, 2), Tile(20, 2)], 63).Count);
    }

    [Fact]
    public void NoTiles_RendersNothing()
    {
        using var console = new TestConsole();
        console.Write(new ImageStrip([]));

        Assert.Equal("", console.Output);
    }
}
