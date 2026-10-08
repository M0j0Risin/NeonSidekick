using System.Buffers.Binary;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The Mac's clipboard over the pasteboard (2026-10-07): what is taken off it (<see cref="PasteboardPick"/>, on any OS) and the TIFF's
/// largest frame made a PNG through ImageIO (on a Mac). Nothing here writes the pasteboard: a test run never touches the user's
/// clipboard (the smoke reads only its change count).
/// </summary>
public class MacClipboardTests
{
    /// <summary>Finder's file URLs as a drop's paste: one POSIX path a line, decoded, a folder's slash dropped; nothing else is a file.</summary>
    [Fact]
    public void FilePaths_AreADropsPaste_OneALine()
    {
        Assert.Equal("/Users/me/odd dir/ä 'q.png\n/Users/me/Pictures",
            PasteboardPick.FilePaths(["file:///Users/me/odd%20dir/%C3%A4%20'q.png", null, "file:///Users/me/Pictures/"]));
        Assert.Null(PasteboardPick.FilePaths([null, "https://example.com/a.png", "not a url"]));
        Assert.Null(PasteboardPick.FilePaths([]));
        Assert.Equal("/", PasteboardPick.FilePaths(["file:///"]));
    }

    /// <summary>The TIFF frame pasted: the most pixels, the first of equals; none for none.</summary>
    [Fact]
    public void Largest_IsTheFrameWithTheMostPixels()
    {
        Assert.Equal(1, PasteboardPick.Largest([(64, 48), (128, 96), (100, 100)]));
        Assert.Equal(0, PasteboardPick.Largest([(10, 10), (10, 10)]));
        Assert.Equal(0, PasteboardPick.Largest([(0, 0)]));
        Assert.Equal(-1, PasteboardPick.Largest([]));
    }

    /// <summary>
    /// A TIFF with the picture at two sizes, the smaller first (Preview's copy of a Retina screen's selection), becomes a PNG of the
    /// larger; bytes ImageIO cannot read are no picture.
    /// </summary>
    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void ATiff_BecomesAPngOfItsLargestFrame()
    {
        byte[] tiff = MacClipboard.TiffOf((64, 48), (128, 96))!;
        byte[] png = MacClipboard.PngFromImage(tiff)!;

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.Equal(128, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));   // IHDR's width
        Assert.Equal(96, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));    // and height
        Assert.Null(MacClipboard.PngFromImage([1, 2, 3, 4]));
    }

    /// <summary>The pasteboard answers in the test host too (its change count, read without reading or writing the user's clipboard).</summary>
    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void ThePasteboard_Answers()
    {
        Assert.NotNull(MacClipboard.ChangeCount());
    }
}
