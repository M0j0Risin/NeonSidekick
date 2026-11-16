using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>web_fetch</c>'s <c>outline</c> (2026-11-15, the web-tools plan's §1): headings recorded where the text is written — by
/// <see cref="HtmlToMarkdown"/> for a page, by <see cref="WebFetcher.PdfAnswerPage"/> for a PDF — and <see cref="WebOutline"/>'s list.
/// </summary>
public class WebOutlineTests
{
    private static PageText Convert(string html) => HtmlToMarkdown.Convert(html, new Uri("https://example.com/"));

    [Fact]
    public void EachHeading_IsRecordedAtItsLine_AfterTidy()
    {
        var page = Convert("<h1>One</h1><p>Some text.</p><h2>Two <a href=\"/x\">link</a></h2><div><h3>Three</h3></div><h6>Six</h6>");

        var headings = page.Headings!;
        // The recorded text is the heading's words, its link markup off (2026-11-16, the web-tools review); the offset is its marker's.
        Assert.Equal(["One", "Two link", "Three", "Six"], headings.Select(h => h.Text));
        Assert.Equal([1, 2, 3, 6], headings.Select(h => h.Level));
        foreach (var heading in headings)
        {
            string line = page.Markdown[heading.Offset!.Value..].Split('\n')[0];
            Assert.StartsWith(new string('#', heading.Level) + " ", line, StringComparison.Ordinal);
            Assert.All(heading.Text.Split(' '), word => Assert.Contains(word, line, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A heading's line stays one line, its text the words (2026-11-16, the web-tools review's #46, #54, #128, #140): a break or a block
    /// inside it is a space, a heading opened inside it joins its line, and a picture is its alt text.
    /// </summary>
    [Fact]
    public void AHeadingsBreaks_Blocks_NestedHeadingsAndPictures_KeepItOneLine()
    {
        var page = Convert("<h2>Line one<br>line two</h2><p>a</p><h2><div>Block</div> after</h2><p>b</p><h2>Outer <h3>Inner</h3></h2><p>c</p><h2><img alt=\"Logo\" src=\"/l.png\"> Products ¶</h2>");

        var headings = page.Headings!;
        Assert.Contains(headings, h => h.Text == "Line one line two");
        Assert.Contains(headings, h => h.Text.Contains("Block", StringComparison.Ordinal) && h.Text.Contains("after", StringComparison.Ordinal));
        Assert.Contains(headings, h => h.Text == "Logo Products");
        Assert.DoesNotContain("## Outer ###", page.Markdown, StringComparison.Ordinal);
        foreach (var heading in headings)
        {
            string line = page.Markdown[heading.Offset!.Value..].Split('\n')[0];
            Assert.Single(line.Split(' '), word => word.Length > 0 && word.All(c => c == '#'));
        }
    }

    /// <summary>A Markdown answer's own <c>#</c> lines are its outline, a fenced block's left out (2026-11-16, the web-tools review's #133).</summary>
    [Fact]
    public void AMarkdownAnswersHeadings_AreItsHashLines_OutsideItsCodeBlocks()
    {
        var headings = WebOutline.MarkdownHeadings("# Title\ntext\n```\n# not one\n```\n## Part two\n#not-a-heading");

        Assert.Equal(["Title", "Part two"], headings.Select(h => h.Text));
        Assert.Equal([1, 2], headings.Select(h => h.Level));
    }

    [Fact]
    public void AParagraphStartingWithAHash_OrHoldingAFence_IsNoHeading_AndTheNextRealOneIsFound()
    {
        var page = Convert("<p># of seats</p><p>a stray ``` mark</p><h2>Real</h2><p>body</p>");

        var heading = Assert.Single(page.Headings!);
        Assert.Equal("Real", heading.Text);
        Assert.StartsWith("## Real", page.Markdown[heading.Offset!.Value..], StringComparison.Ordinal);
    }

    [Fact]
    public void AHeadingInsideALinkCard_OrATableCell_OrAnEmptyOne_IsNoHeading()
    {
        var page = Convert("<a href=\"/c\"><h3>Card</h3><p>teaser</p></a><table><tr><td><h4>Cell</h4></td></tr></table><h2> </h2><h2>Kept</h2>");

        Assert.Equal(["Kept"], page.Headings!.Select(h => h.Text));
    }

    [Fact]
    public void APageWithNoHeadings_HasAnEmptyList_AndAnchorsStillMap()
    {
        var page = Convert("<p id=\"a\">text</p>");

        Assert.Empty(page.Headings!);
        Assert.Equal(0, page.Anchors!["a"]);
    }

    [Fact]
    public void APdfsOutline_ListsItsTitles_AtTheirPagesMarkers_AndOnesPastTheReadPagesHaveNoOffset()
    {
        var read = new PdfRead(true, "", 90, [new PdfPageRead(1, "first", null, 0, 0), new PdfPageRead(2, "second --- page 7 ---", null, 0, 0)])
        {
            Outline = [new PdfOutlineItem("Intro", 1, 1), new PdfOutlineItem("Details", 2, 2), new PdfOutlineItem("Appendix", 1, 90)],
        };

        var page = WebFetcher.PdfAnswerPage(read, "manual.pdf");

        var headings = page.Headings!;
        Assert.Equal(["Intro", "Details", "Appendix"], headings.Select(h => h.Text));
        Assert.StartsWith("--- page 1 ---", page.Markdown[headings[0].Offset!.Value..], StringComparison.Ordinal);
        Assert.StartsWith("--- page 2 ---", page.Markdown[headings[1].Offset!.Value..], StringComparison.Ordinal);
        Assert.Null(headings[2].Offset);
        Assert.Equal("(page 90, not read) · ⟪Appendix⟫", WebOutline.Line(headings[2]));
        Assert.Equal(WebText.PdfMore(2, 90), page.More);
        // The note lives beside the text, never in it, so find never matches it and each answer adds it once (2026-11-16).
        Assert.DoesNotContain(page.More!, page.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void APdfWithNoOutline_FallsBackToItsPageMarkers()
    {
        var read = new PdfRead(true, "", 2, [new PdfPageRead(1, "a", null, 0, 0), new PdfPageRead(2, "b", null, 0, 0)]);

        var page = WebFetcher.PdfAnswerPage(read, "x.pdf");

        Assert.Equal([0, page.Markdown.IndexOf("--- page 2", StringComparison.Ordinal)], page.Headings!.Select(h => h.Offset!.Value));
        Assert.Null(page.More);
    }

    [Fact]
    public void Render_IndentsByLevel_AndCutsAtTheCap_CountingTheRest()
    {
        var headings = Enumerable.Range(0, 100).Select(i => new PageHeading(1 + i % 2, "Heading " + i, i * 100)).ToList();

        Assert.Equal(("0 · ⟪Heading 0⟫\n  100 · ⟪Heading 1⟫", (int?)null), WebOutline.Render(headings.Take(2).ToList(), 1000));
        var (cut, next) = WebOutline.Render(headings, 200);
        Assert.NotNull(next);
        Assert.True(cut.Length <= 200, cut.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.EndsWith(" more)", cut, StringComparison.Ordinal);
        int shown = cut.Split('\n').Length - 1;
        Assert.EndsWith(WebText.OutlineMore(100 - shown), cut, StringComparison.Ordinal);
    }
}
