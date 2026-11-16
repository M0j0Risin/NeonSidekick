using System.Net;
using System.Text;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary><c>web_fetch</c>'s <c>find</c> and <c>outline</c> (2026-11-15, the web-tools plan's §1, feature idea #26).</summary>
public sealed partial class WebToolsTests
{
    private static string Words(int count, string stem) => string.Join(" ", Enumerable.Range(0, count).Select(i => stem + i));

    private void LongManual() =>
        _http.Map("https://example.com/manual", HttpStatusCode.OK,
            "<title>Manual</title><h1>Setup</h1><p>" + Words(3000, "s") + "</p><h2 id=\"care\">Care</h2><p>" + Words(3000, "c") +
            " the warranty covers parts for two years " + Words(3000, "d") + "</p>", "text/html");

    private async Task<string> Fetch(params (string Name, object? Value)[] pairs) => (string)(ToolAnswers.Text(await _fetch.InvokeAsync(Args(pairs), CancellationToken.None)))!;

    [Fact]
    public async Task Outline_ListsTheHeadings_WithOffsetsTheTextReadsFrom_AndFindReusesTheDownload()
    {
        LongManual();

        string outline = await Fetch(("url", "https://example.com/manual#care"), ("outline", true));

        Assert.StartsWith("⟪Manual⟫ — https://example.com/manual (outline: 2 headings in ", outline, StringComparison.Ordinal);
        Assert.DoesNotContain("#care", outline, StringComparison.Ordinal);   // the whole page: the fragment ignored
        string[] lines = outline.Split('\n');
        Assert.Equal("0 · ⟪Setup⟫", lines[2]);
        Assert.StartsWith("  ", lines[3], StringComparison.Ordinal);
        int care = int.Parse(lines[3].Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        string atCare = await Fetch(("url", "https://example.com/manual"), ("offset", care));
        Assert.StartsWith("⟪## Care", atCare[(atCare.IndexOf("\n\n", StringComparison.Ordinal) + 2)..], StringComparison.Ordinal);

        string found = await Fetch(("url", "https://example.com/manual#setup"), ("find", "\"Warranty\" PARTS"));
        Assert.StartsWith("⟪Manual⟫ — https://example.com/manual (find \"" + WebText.FindWordsShown(["warranty", "parts"]) + "\": 1 passage in ", found, StringComparison.Ordinal);
        int at = int.Parse(found.Split('\n')[2][1..], System.Globalization.CultureInfo.InvariantCulture);
        string atFound = await Fetch(("url", "https://example.com/manual"), ("offset", at));
        Assert.Contains("the warranty covers parts", atFound, StringComparison.Ordinal);

        Assert.Single(_http.Requests);   // every call after the first served from the cache
    }

    [Fact]
    public async Task Find_WithNoMatch_SaysSo_AndNamesTheFirstHeadings()
    {
        LongManual();

        string none = await Fetch(("url", "https://example.com/manual"), ("find", "refund"));

        Assert.Contains("(find \"refund\": no passage holds every word in ", none, StringComparison.Ordinal);
        Assert.EndsWith(WebText.FindNoneHint(["⟪Setup⟫", "⟪Care⟫"]) + "\n" + NeonSidekick.Llm.Tools.UntrustedText.QuoteNote, none, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindAndOutline_ExcludeEachOther_ButDefaultsCountAsNotGiven()
    {
        LongManual();

        Assert.Equal(WebText.FindAndOutline, await Fetch(("url", "https://example.com/manual"), ("find", "x"), ("outline", true)));
        Assert.Equal(WebText.FindNoWords, await Fetch(("url", "https://example.com/manual"), ("find", "\" ... \"")));
        Assert.Empty(_http.Requests);

        // A model sending the defaults gets the plain window.
        string plain = await Fetch(("url", "https://example.com/manual"), ("find", "  "), ("outline", false), ("offset", 0));
        Assert.StartsWith("⟪Manual⟫ — https://example.com/manual (chars 1–32,000 of ", plain, StringComparison.Ordinal);
        Assert.Equal("Error: 'maybe' is not true or false for 'outline'", await Fetch(("url", "https://example.com/manual"), ("outline", "maybe")));
    }

    /// <summary>With <c>offset</c>, either list goes on from there (2026-11-16, the web-tools review's #17).</summary>
    [Fact]
    public async Task FindAndOutline_WithAnOffset_ListOnlyWhatComesAfterIt()
    {
        LongManual();
        string outline = await Fetch(("url", "https://example.com/manual"), ("outline", true));
        int care = int.Parse(outline.Split('\n')[3].Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

        string later = await Fetch(("url", "https://example.com/manual"), ("outline", true), ("offset", care));
        Assert.Contains("(outline: 1 heading from offset ", later, StringComparison.Ordinal);
        Assert.DoesNotContain("Setup", later, StringComparison.Ordinal);
        Assert.Contains("⟪Care⟫", later, StringComparison.Ordinal);

        string found = await Fetch(("url", "https://example.com/manual"), ("find", "warranty"));
        int at = int.Parse(found.Split('\n')[2][1..], System.Globalization.CultureInfo.InvariantCulture);
        string past = await Fetch(("url", "https://example.com/manual"), ("find", "warranty"), ("offset", at + 1));
        Assert.Contains("no passage holds every word", past, StringComparison.Ordinal);
        Assert.Single(_http.Requests);
    }

    /// <summary>A word typed with its punctuation still finds (2026-11-16, the web-tools review's #132).</summary>
    [Fact]
    public async Task Find_IgnoresAWordsPunctuation()
    {
        LongManual();

        Assert.Contains(": 1 passage in ", await Fetch(("url", "https://example.com/manual"), ("find", "warranty, (parts).")), StringComparison.Ordinal);
    }

    /// <summary>
    /// A window of bidi controls stays within the cap once each is escaped (2026-11-16, the web-tools review's #112), and the next offset
    /// is where it stopped.
    /// </summary>
    [Fact]
    public async Task AWindowOfEscapedCharacters_StaysWithinTheCap()
    {
        _http.Map("https://example.com/rlo", HttpStatusCode.OK, "<title>R</title><p>" + new string('\u202E', 40_000) + "</p>", "text/html");

        string answer = await Fetch(("url", "https://example.com/rlo"));

        int open = answer.IndexOf('⟪', answer.IndexOf("\n\n", StringComparison.Ordinal));
        int close = answer.LastIndexOf("⟫\n" + NeonSidekick.Llm.Tools.UntrustedText.QuoteNote, StringComparison.Ordinal);   // the note holds a ⟫ of its own
        Assert.InRange(close - open - 1, 1, WebFetchTool.MaxChars);
        Assert.Contains("\\u202E", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outline_OnAFailedFetch_AnswersTheError_AndOnATextAnswer_SaysThereAreNoHeadings()
    {
        _http.Map("https://example.com/gone", HttpStatusCode.NotFound, "<p>nope</p>", "text/html");
        _http.Map("https://example.com/a.txt", HttpStatusCode.OK, "# not a heading\nplain", "text/plain");

        string gone = await Fetch(("url", "https://example.com/gone"), ("outline", true));
        Assert.StartsWith("Error:", gone, StringComparison.Ordinal);

        Assert.Equal(WebText.NoHeadings("", "https://example.com/a.txt", 21, WebText.HttpEngine),
            await Fetch(("url", "https://example.com/a.txt"), ("outline", true)));
    }

    [Fact]
    public async Task APdfCutAtItsPageCap_EndsFindAndOutline_WithTheNoteOnTheRest()
    {
        _http.Map("https://example.com/big.pdf", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF\n"), "application/pdf")));
        for (int i = 1; i <= WebFetcher.MaxPdfPages; i++)
        {
            _browser.PdfReadPages.Add(new PdfPageRead(i, "page text " + i, null, 0, 0));
        }

        _browser.PdfReadCount = 120;
        _browser.PdfOutline.Add(new PdfOutlineItem("Start", 1, 1));
        _browser.PdfOutline.Add(new PdfOutlineItem("Index", 1, 118));

        string outline = await Fetch(("url", "https://example.com/big.pdf"), ("outline", true));
        string none = await Fetch(("url", "https://example.com/big.pdf"), ("find", "zebra"));

        string more = WebText.PdfMore(WebFetcher.MaxPdfPages, 120);
        Assert.Contains("(outline: 2 headings in ", outline, StringComparison.Ordinal);
        Assert.Matches(@"\n[1-9][0-9]* · ⟪Start⟫\n", outline);
        Assert.Contains("(page 118, not read) · ⟪Index⟫", outline, StringComparison.Ordinal);
        Assert.EndsWith(more + "\n" + NeonSidekick.Llm.Tools.UntrustedText.QuoteNote, outline, StringComparison.Ordinal);   // the app's note after the last fence, the quote note last
        Assert.EndsWith(more + "\n" + NeonSidekick.Llm.Tools.UntrustedText.QuoteNote, none, StringComparison.Ordinal);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public void TheNewArguments_PassTheArgumentCheck()
    {
        var check = ToolArgumentCheck.Check(_fetch, _fetch.JsonSchema, new Dictionary<string, object?> { ["url"] = "u", ["find"] = "x", ["outline"] = true });

        Assert.Equal(ArgumentVerdict.Pass, check.Verdict);
    }
}
