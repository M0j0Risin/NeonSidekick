using NeonSidekick.Files;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class PasteBlocksTests
{
    private static string Lines(int n) => string.Join('\n', Enumerable.Range(1, n).Select(i => $"line {i}"));

    [Fact]
    public void Collapses_IsPinned()
    {
        Assert.False(PasteBlocks.Collapses(""));
        Assert.False(PasteBlocks.Collapses("one line"));
        Assert.False(PasteBlocks.Collapses(Lines(PasteBlocks.InlineMaxLines)));
        Assert.True(PasteBlocks.Collapses(Lines(PasteBlocks.InlineMaxLines + 1)));
        Assert.False(PasteBlocks.Collapses(new string('x', PasteBlocks.InlineMaxChars)));
        Assert.True(PasteBlocks.Collapses(new string('x', PasteBlocks.InlineMaxChars + 1)));
        Assert.Throws<ArgumentNullException>(() => PasteBlocks.Collapses(null!));
    }

    [Fact]
    public void Lines_AndLabel_ArePinned()
    {
        Assert.Equal(0, PasteBlocks.Lines(""));
        Assert.Equal(1, PasteBlocks.Lines("a"));
        Assert.Equal(2, PasteBlocks.Lines("a\nb"));
        Assert.Equal(3, PasteBlocks.Lines("a\n\n"));
        Assert.Equal("[Pasted text #1 +49 lines]", PasteBlocks.Label(1, Lines(49)));
        Assert.Equal("[Pasted text #12 · 900 chars]", PasteBlocks.Label(12, new string('x', 900)));
    }

    [Fact]
    public void Tokens_ArePrivateUseCharacters_InOrder()
    {
        var blocks = new PasteBlocks();
        char first = blocks.Add(Lines(5));
        char second = blocks.Add(Lines(7));
        Assert.Equal('\uE000', first);
        Assert.Equal('\uE001', second);
        Assert.True(PasteBlocks.IsToken(first));
        Assert.True(PasteBlocks.IsToken('\uF8FF'));
        Assert.False(PasteBlocks.IsToken('a'));
        Assert.False(PasteBlocks.IsToken('\uDFFF'));
        Assert.Equal(2, blocks.Count);
        Assert.Equal(Lines(5), blocks.BlockOf(first));
        Assert.Equal("[Pasted text #2 +7 lines]", blocks.LabelOf(second));
        // A private-use character that is not one of this store's tokens is just a character.
        Assert.Null(blocks.BlockOf('\uE005'));
        Assert.Null(blocks.LabelOf('\uE005'));
        Assert.Null(blocks.LabelOf('x'));
    }

    /// <summary>
    /// A pasted or typed private-use character (a Nerd Font glyph) is a literal token (2026-10-04, the user's report: they were
    /// dropped): one per distinct character, drawn and expanded as itself, never painted as a label, never numbered among the pastes.
    /// </summary>
    [Fact]
    public void Literals_StandForTheirOwnGlyph()
    {
        var blocks = new PasteBlocks();
        char paste = blocks.Add(Lines(5));
        char glyph = blocks.Literal('\uE0B0');                       // the Powerline arrow
        Assert.NotEqual('\uE0B0', glyph);
        Assert.True(PasteBlocks.IsToken(glyph));
        Assert.Equal(glyph, blocks.Literal('\uE0B0'));                // the same character, the same token
        Assert.Equal('a', blocks.Literal('a'));
        Assert.True(blocks.IsLiteral(glyph));
        Assert.False(blocks.IsLiteral(paste));

        string draft = blocks.Literals("git \uE0A0 main") + " " + paste;
        Assert.Equal("git \uE0A0 main " + Lines(5), blocks.Expand(draft));
        Assert.Equal("git \uE0A0 main [Pasted text #1 +5 lines]", blocks.Display(draft));
        Assert.Single(blocks.LabelRanges(draft));                     // the paste's label alone; a glyph is text
        Assert.Empty(blocks.Previews(blocks.Literals("\uE0A0"), 5));
        Assert.Equal("[Pasted text #2 +4 lines]", blocks.LabelOf(blocks.Add(Lines(4))));   // the glyphs took no number
        Assert.Equal("plain", blocks.Literals("plain"));
    }

    [Fact]
    public void Display_AndExpand_ReplaceEveryToken()
    {
        var blocks = new PasteBlocks();
        char a = blocks.Add(Lines(4));
        char b = blocks.Add(Lines(6));
        string draft = $"compare {a} with {b}!";

        Assert.Equal("compare [Pasted text #1 +4 lines] with [Pasted text #2 +6 lines]!", blocks.Display(draft));
        Assert.Equal($"compare {Lines(4)} with {Lines(6)}!", blocks.Expand(draft));
        Assert.Equal("compare " + PasteBlocks.Unbreakable("[Pasted text #1 +4 lines]") + " with " + PasteBlocks.Unbreakable("[Pasted text #2 +6 lines]") + "!", blocks.Display(draft, unbreakable: true));
        Assert.Equal("[Pasted\u00A0text\u00A0#1\u00A0+4\u00A0lines]", PasteBlocks.Unbreakable("[Pasted text #1 +4 lines]"));
        // Nothing to replace: the same string.
        Assert.Same("plain", blocks.Display("plain"));
        Assert.Same("plain", blocks.Expand("plain"));
        Assert.Equal(new[] { (8, "[Pasted text #1 +4 lines]".Length), (8 + "[Pasted text #1 +4 lines]".Length + 6, "[Pasted text #2 +6 lines]".Length) }, blocks.LabelRanges(draft));
        Assert.Empty(blocks.LabelRanges("plain"));
    }

    [Fact]
    public void IndexMapping_CarriesTheCursorBothWays()
    {
        var blocks = new PasteBlocks();
        char t = blocks.Add(Lines(5));
        string draft = $"ab{t}cd";
        int label = "[Pasted text #1 +5 lines]".Length;

        Assert.Equal(0, blocks.ToDisplayIndex(draft, 0));
        Assert.Equal(2, blocks.ToDisplayIndex(draft, 2));            // before the token = the label's start
        Assert.Equal(2 + label, blocks.ToDisplayIndex(draft, 3));    // after it = the label's end
        Assert.Equal(4 + label, blocks.ToDisplayIndex(draft, 5));
        Assert.Equal(4 + label, blocks.ToDisplayIndex(draft, 99));   // clamped

        Assert.Equal(0, blocks.ToDraftIndex(draft, 0));
        Assert.Equal(2, blocks.ToDraftIndex(draft, 2));
        Assert.Equal(2, blocks.ToDraftIndex(draft, 2 + label / 2 - 1));   // the first half: before the token
        Assert.Equal(3, blocks.ToDraftIndex(draft, 2 + label / 2 + 1));   // the second half: after it
        Assert.Equal(3, blocks.ToDraftIndex(draft, 2 + label));
        Assert.Equal(5, blocks.ToDraftIndex(draft, 4 + label));
        Assert.Equal(5, blocks.ToDraftIndex(draft, 999));

        // No tokens: the identity.
        Assert.Equal(3, blocks.ToDisplayIndex("abcd", 3));
        Assert.Equal(3, blocks.ToDraftIndex("abcd", 3));
    }

    // ── Images ─────────────────────────────────────────────────────────────

    private static ImageAttachment Image(string name) => new(@"C:\pics\" + name, [1, 2, 3], ImageFile.Png, 4, 4);

    [Fact]
    public void ImageLabel_IsPinned()
    {
        Assert.Equal("[Image #1]", PasteBlocks.ImageLabel(1));
        Assert.Equal("[Image #12]", PasteBlocks.ImageLabel(12));
    }

    [Fact]
    public void ImagesAndTextBlocks_ShareTheTokenRange_AndCountSeparately()
    {
        var blocks = new PasteBlocks();
        char text1 = blocks.Add(Lines(5));
        char image1 = blocks.AddImage(Image("a.png"));
        char text2 = blocks.Add(Lines(6));
        char image2 = blocks.AddImage(Image("b.png"));

        Assert.Equal(new[] { '\uE000', '\uE001', '\uE002', '\uE003' }, new[] { text1, image1, text2, image2 });
        Assert.Equal(4, blocks.Count);
        Assert.Equal(2, blocks.ImageCount);
        Assert.Equal("[Pasted text #1 +5 lines]", blocks.LabelOf(text1));
        Assert.Equal("[Image #1]", blocks.LabelOf(image1));
        Assert.Equal("[Pasted text #2 +6 lines]", blocks.LabelOf(text2));
        Assert.Equal("[Image #2]", blocks.LabelOf(image2));
        Assert.Null(blocks.ImageOf(text1));
        Assert.Equal(@"C:\pics\b.png", blocks.ImageOf(image2)!.Path);
        Assert.Null(blocks.ImageOf('\uE004'));
        Assert.Throws<ArgumentNullException>(() => blocks.AddImage(null!));
    }

    [Fact]
    public void MoreLabel_AndPreview_ArePinned()
    {
        Assert.Equal("[… +75 more lines]", PasteBlocks.MoreLabel(75));
        Assert.Equal("[… +1 more line]", PasteBlocks.MoreLabel(1));
        Assert.Equal(25, PasteBlocks.DefaultPreviewLines);
        Assert.Equal(200, PasteBlocks.MaxPreviewLines);
        // Shorter than the cap: the whole block; longer: the first lines and the closing label; 0 = nothing.
        Assert.Equal(Lines(4), PasteBlocks.Preview(Lines(4), 25));
        Assert.Equal(Lines(4), PasteBlocks.Preview(Lines(4), 4));
        Assert.Equal(Lines(3) + "\n[… +1 more line]", PasteBlocks.Preview(Lines(4), 3));
        Assert.Equal(Lines(25) + "\n[… +75 more lines]", PasteBlocks.Preview(Lines(100), 25));
        Assert.Equal("", PasteBlocks.Preview(Lines(4), 0));
        Assert.Equal("", PasteBlocks.Preview(Lines(4), -1));
        Assert.Equal("", PasteBlocks.Preview("", 5));
        // A block ending in a line break: the empty last line counts (Lines does).
        Assert.Equal("a\n[… +1 more line]", PasteBlocks.Preview("a\n", 1));
        Assert.Throws<ArgumentNullException>(() => PasteBlocks.Preview(null!, 1));
    }

    [Fact]
    public void Previews_AreTheDraftsTextBlocks_InOrder_ImageTokensSkipped()
    {
        var blocks = new PasteBlocks();
        char text1 = blocks.Add(Lines(5));
        char image = blocks.AddImage(Image("a.png"));
        char text2 = blocks.Add(Lines(40));
        string draft = $"compare {text2} with {text1} and {image}";

        Assert.Equal(
            new[] { ("[Pasted text #2 +40 lines]", Lines(3) + "\n[… +37 more lines]"), ("[Pasted text #1 +5 lines]", Lines(3) + "\n[… +2 more lines]") },
            blocks.Previews(draft, 3));
        Assert.Equal(new[] { ("[Pasted text #1 +5 lines]", Lines(5)) }, blocks.Previews($"{text1} only", 25));
        Assert.Empty(blocks.Previews(draft, 0));
        Assert.Empty(blocks.Previews("no tokens", 25));
        Assert.Empty(blocks.Previews($"{image}", 25));
        Assert.Throws<ArgumentNullException>(() => blocks.Previews(null!, 1));
    }

    [Fact]
    public void AnImageToken_ExpandsToItsLabel_AndDisplaysTheSame()
    {
        var blocks = new PasteBlocks();
        char image = blocks.AddImage(Image("a.png"));
        char text = blocks.Add(Lines(4));
        string draft = $"see {image} and {text}!";

        Assert.Equal("see [Image #1] and " + Lines(4) + "!", blocks.Expand(draft));
        Assert.Equal("see [Image #1] and [Pasted text #1 +4 lines]!", blocks.Display(draft));
        Assert.Equal("[Image #1]", blocks.BlockOf(image));
        Assert.Equal(new[] { (4, "[Image #1]".Length), (19, "[Pasted text #1 +4 lines]".Length) }, blocks.LabelRanges(draft));
    }

    [Fact]
    public void ImagesIn_AreTheDraftsImages_InOrder_TextTokensSkipped()
    {
        var blocks = new PasteBlocks();
        char a = blocks.AddImage(Image("a.png"));
        char text = blocks.Add(Lines(4));
        char b = blocks.AddImage(Image("b.png"));

        Assert.Equal(new[] { "b.png", "a.png", "a.png" }, blocks.ImagesIn($"{b} {text} {a}{a}").Select(i => Path.GetFileName(i.Path)));
        Assert.Empty(blocks.ImagesIn($"plain {text}"));
        Assert.Empty(blocks.ImagesIn(""));
        Assert.Throws<ArgumentNullException>(() => blocks.ImagesIn(null!));
    }

    [Fact]
    public void Original_IsTheClipboardsOwnBytes_NotTheDownscale_NamedByWhatTheyAre()
    {
        // generate_image's input (later still on 2026-09-24): the paste before the 2048 downscale the model was shown.
        byte[] wide = App.SmokeChecks.SolidBmp(3000, 10);
        Assert.True(ImageFile.TryLoad(wide, ImageFile.ClipboardName(1), out var shown, out _));
        Assert.True(shown!.Width <= ImageFile.MaxSide);   // what the model saw was scaled down
        var blocks = new PasteBlocks();
        blocks.Add(Lines(5));   // a text block between: image numbers count images alone
        blocks.AddImage(shown, original: wide);
        blocks.AddImage(Image("plain.png"));   // kept without a source (a test's, a replayed one)

        var original = blocks.Original(1);

        Assert.NotNull(original);
        Assert.Same(wide, original.Bytes);
        Assert.Null(original.FileName);   // a clipboard picture has no name: the saver stamps one
        Assert.Null(blocks.Original(2));   // no source kept
        Assert.Null(blocks.Original(3));   // no such picture
        Assert.Null(blocks.Original(0));
    }

    [Fact]
    public void Original_OfADroppedFile_ReadsItAgain_AndIsNullOnceItIsGone()
    {
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "photo.bmp");
            byte[] bytes = App.SmokeChecks.SolidBmp(4, 2);
            File.WriteAllBytes(path, bytes);
            var blocks = new PasteBlocks();
            blocks.AddImage(Image("photo.bmp"), sourcePath: path);

            var original = blocks.Original(1);
            File.Delete(path);

            Assert.Equal(bytes, original!.Bytes);
            Assert.Equal("photo.bmp", original.FileName);
            Assert.Null(blocks.Original(1));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0 }, ".bmp")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, ".png")]
    [InlineData(new byte[] { }, ".png")]
    public void ExtensionOf_ReadsTheHeader(byte[] bytes, string expected) =>
        Assert.Equal(expected, ImageFile.ExtensionOf(bytes));
}
