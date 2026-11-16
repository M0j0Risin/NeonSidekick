using System.Text.Json;
using NeonSidekick.Llm.Tools;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 📚 line on the page (2026-11-15, the web-tools plan's §2): its plain text while it arrives, then the server's HTML from the end that
/// follows it, real anchors that open a new tab, and a <c>javascript:</c> address never linked.
/// </summary>
public sealed partial class WebUiPageTests
{
    [WindowsFact]
    public async Task TheSourcesLine_ShowsItsTextThenItsLinks_AndNeverLinksAScript()
    {
        string markdown = App.SessionExport.SourcesLineMarkdown(
            [new WebSource(WebSourceKind.Page, "https://en.wikipedia.org/wiki/Mercury_(planet)", "Mercury"), new WebSource(WebSourceKind.Page, "javascript:alert(1)", "Script")]);
        _backend.Snap = new WebSnapshot([new WebEntry(1, "assistant", "hi", null, "<p>hi</p>", false)], [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('.entry.assistant') !== null", "the reply");
        Publish("append", JsonSerializer.Serialize(new WebEntry(2, "sources", markdown, null, null, false), WebUiJsonContext.Default.WebEntry));
        await page.WaitAsync("document.querySelector('.entry.sources') !== null", "the sources line");
        Assert.Equal(markdown, await page.TextAsync("document.querySelector('.entry.sources .body').textContent"));

        Publish("end", JsonSerializer.Serialize(new WebEnd(2, WebUiMarkdown.ToHtml(markdown, TranscriptFeed.IdPrefix(2))), WebUiJsonContext.Default.WebEnd));
        await page.WaitAsync("document.querySelector('.entry.sources a') !== null", "its links");
        Assert.Equal("https://en.wikipedia.org/wiki/Mercury_(planet)", await page.TextAsync("document.querySelector('.entry.sources a').getAttribute('href')"));
        Assert.Equal(1, (await page.EvalAsync("document.querySelectorAll('.entry.sources a').length")).GetInt32());
        Assert.False((await page.EvalAsync("[...document.querySelectorAll('a')].some(a => (a.getAttribute('href') || '').toLowerCase().startsWith('javascript:'))")).GetBoolean());
        Assert.Contains("Script", await page.TextAsync("document.querySelector('.entry.sources').textContent"), StringComparison.Ordinal);
    }
}
