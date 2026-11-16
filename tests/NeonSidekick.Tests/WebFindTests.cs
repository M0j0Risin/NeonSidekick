using NeonSidekick.Pdf;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary><c>web_fetch</c>'s <c>find</c> (2026-11-15, the web-tools plan's §1): <see cref="WebFind"/>, read_pdf's rule over a page's text.</summary>
public class WebFindTests
{
    private static string Filler(int words, string stem = "w") => string.Join(" ", Enumerable.Range(0, words).Select(i => stem + i));

    [Fact]
    public void APassage_HoldsEveryWord_OrIsNotAMatch()
    {
        string text = Filler(200) + " the warranty covers parts " + Filler(200) + " a warranty alone " + Filler(200);

        var found = WebFind.Passages(text, PdfFind.Words("warranty parts"));

        var passage = Assert.Single(found.Shown);
        Assert.Equal(1, found.Total);
        Assert.Contains("the warranty covers parts", passage.Text, StringComparison.Ordinal);
        Assert.Equal(text.IndexOf(passage.Text, StringComparison.Ordinal), passage.Offset);
        Assert.Empty(WebFind.Passages(text, PdfFind.Words("warranty missing")).Shown);
    }

    [Fact]
    public void SubstringsMatch_AndTheOffsetIndexesTheOriginal()
    {
        string text = "Intro. Our warranty lasts.";

        var found = WebFind.Passages(text, PdfFind.Words("warrant"));

        var passage = Assert.Single(found.Shown);
        Assert.Equal(0, passage.Offset);
        Assert.Equal(text, passage.Text);
    }

    [Fact]
    public void FoldedMatching_ReportsTheOriginalOffsets()
    {
        // A decomposed accent folds to fewer characters: the offset still indexes the page as written.
        string text = Filler(150, "x") + " Résumé of the CAFÉ " + Filler(150, "y");

        var found = WebFind.Passages(text, PdfFind.Words("resume café"));

        var passage = Assert.Single(found.Shown);
        Assert.Equal(text.Substring(passage.Offset, passage.Text.Length), passage.Text);
        Assert.Contains("Résumé of the CAFÉ", passage.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AWordBrokenByALineEndHyphen_IsFound_AtTheOriginalOffset()
    {
        string text = Filler(150) + " the warran-\nty card " + Filler(150);

        var found = WebFind.Passages(text, PdfFind.Words("warranty"));

        var passage = Assert.Single(found.Shown);
        Assert.Contains("warran-\nty", passage.Text, StringComparison.Ordinal);
        Assert.Equal(text.Substring(passage.Offset, passage.Text.Length), passage.Text);
        Assert.Single(WebFind.Passages("a soft war­\nranty", PdfFind.Words("warranty")).Shown);
        Assert.Single(WebFind.Passages("e-\nmail me", PdfFind.Words("e-mail")).Shown);
    }

    [Fact]
    public void ThePassageHoldingTheWords_InTheOrderTyped_RanksFirst()
    {
        string text = "parts then warranty " + Filler(300) + " warranty then parts " + Filler(300);

        var found = WebFind.Passages(text, PdfFind.Words("warranty parts"));

        Assert.Equal(2, found.Total);
        Assert.True(found.Shown[0].InOrder);
        Assert.Contains("warranty then parts", found.Shown[0].Text, StringComparison.Ordinal);
        Assert.False(found.Shown[1].InOrder);
    }

    [Fact]
    public void HitsCloseTogether_MergeIntoOnePassage()
    {
        string text = Filler(200) + " cat " + Filler(20) + " cat " + Filler(20) + " cat " + Filler(200);

        var found = WebFind.Passages(text, PdfFind.Words("cat"));

        var passage = Assert.Single(found.Shown);
        Assert.Equal(3, passage.Text.Split("cat ").Length - 1);
        Assert.True(passage.Text.Length <= 2 * WebFind.PassageChars, passage.Text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Folded a grapheme cluster at a time (2026-11-16, the web-tools review's #1, #8): a page's conjoining Hangul jamo and a split Tamil vowel
    /// sign match the composed word typed.
    /// </summary>
    [Theory]
    [InlineData("\u1112\u1161\u11AB\u1100\u1173\u11AF", "\uD55C\uAE00")]
    [InlineData("\u0B95\u0BC6\u0BBE\u0BB2\u0BCD", "\u0B95\u0BCA\u0BB2\u0BCD")]
    public void AWordTypedComposed_FindsItsDecomposedForm(string onPage, string typed)
    {
        var found = WebFind.Passages(Filler(20) + " " + onPage + " " + Filler(20), PdfFind.Words(typed));

        Assert.Contains(onPage, Assert.Single(found.Shown).Text, StringComparison.Ordinal);
    }

    /// <summary>A link longer than the edge slack is kept whole at a passage's end (2026-11-16, the web-tools review's #186).</summary>
    [Fact]
    public void ALongLinkAtAPassagesEdge_IsKeptWhole()
    {
        string url = "https://example.com/" + new string('a', 200);
        string text = "cat " + string.Join(" ", Enumerable.Repeat("word", 79)) + " " + url + " tail " + Filler(100);

        var passage = Assert.Single(WebFind.Passages(text, PdfFind.Words("cat")).Shown);

        Assert.Contains(url + "", passage.Text, StringComparison.Ordinal);
        Assert.EndsWith(url, passage.Text.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void AtMostTenPassages_TheRestCounted()
    {
        // Hits about 1,400 characters apart, so no two windows touch (ones that overlap merge, HitsCloseTogether_MergeIntoOnePassage).
        string text = string.Join(" ", Enumerable.Range(0, 30).Select(i => "cat " + Filler(300)));

        var found = WebFind.Passages(text, PdfFind.Words("cat"));

        Assert.Equal(WebFind.MaxPassages, found.Shown.Count);
        Assert.Equal(30, found.Total);
        Assert.True(found.Shown.Max(p => p.Offset) < found.NextOffset);
    }

    [Fact]
    public void APassagesEdges_NeverSplitASurrogatePair()
    {
        string emoji = string.Concat(Enumerable.Repeat("😀", 400));
        string text = emoji + "needle" + emoji;

        var passage = Assert.Single(WebFind.Passages(text, PdfFind.Words("needle")).Shown);

        Assert.False(char.IsLowSurrogate(passage.Text[0]));
        Assert.False(char.IsHighSurrogate(passage.Text[^1]));
        Assert.Equal(text.Substring(passage.Offset, passage.Text.Length), passage.Text);
    }

    [Fact]
    public void NoWords_OrAnEmptyText_FindNothing()
    {
        Assert.Equal(0, WebFind.Passages("text", []).Total);
        Assert.Equal(0, WebFind.Passages("", PdfFind.Words("x")).Total);
    }

    [Fact]
    public void Fold_MapsEachCharacterBack()
    {
        var (folded, map) = WebFind.FoldOne("Ée😀");

        Assert.Equal("ee😀", folded);
        Assert.Equal([0, 1, 2, 2, 4], map!);
    }
}
