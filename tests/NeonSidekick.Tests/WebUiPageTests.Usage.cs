using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The usage card in headless Edge (2026-10-16, the user's ask: the lines in a text box read as amateurish): the context as a meter, amber past
/// 80% and red past 95%; the sections as tiles that stack on a phone; the window-unknown and nothing-counted forms; and the open card following a
/// status whose usage changed.
/// </summary>
public sealed partial class WebUiPageTests
{
    private static WebUsageCard UsageCard(int? percent, string inUse = "1.2k") => new(
        "📊 Usage",
        new WebUsageContext("Context", inUse, percent is null ? null : "4.1k", percent, percent is null ? "unknown" : "configured"),
        [
            new WebUsageSection("Last reply", "", [new WebUsageRow("Total", "1,240"), new WebUsageRow("Speed", "46.0 tok/s")]),
            new WebUsageSection("This conversation", "", [new WebUsageRow("Total", "12,480")]),
            new WebUsageSection("Since launch", "", [new WebUsageRow("Total", "29,312"), new WebUsageRow("Botchat", "3 requests · 12,506 in · 250 out")]),
        ],
        null);

    private async Task<HeadlessPage?> UsagePageAsync(int width = 390)
    {
        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words, null, new WebStatus(null, null, null, new WebUsage("1.2k / 4.1k", "30%", "46 tok/s")));
        return await SignedInAsync("/", width);
    }

    private static async Task OpenUsageAsync(HeadlessPage page)
    {
        await page.WaitAsync("!document.getElementById('usage').classList.contains('hidden')", "the usage on the status row");
        await page.EvalAsync("document.getElementById('usage').click()");
        await page.WaitAsync("!document.getElementById('usage-card').classList.contains('hidden')", "the usage card");
    }

    [WindowsFact]
    public async Task TheUsageCard_IsAMeterAndTiles()
    {
        _backend.Card = UsageCard(30);
        if (await UsagePageAsync(width: 900) is not { } page)
        {
            return;
        }

        await OpenUsageAsync(page);
        Assert.Equal("1.2k / 4.1k", await page.TextAsync("document.querySelector('.usage-figures').textContent"));
        Assert.Equal("30%", await page.TextAsync("document.querySelector('.usage-meter-fill').style.width"));
        Assert.False((await page.EvalAsync("document.querySelector('.usage-meter').classList.contains('warn')")).GetBoolean());
        Assert.Equal(3, (await page.EvalAsync("document.querySelectorAll('.usage-tile').length")).GetInt32());
        Assert.Equal("Last reply", await page.TextAsync("document.querySelector('.usage-tile h3').textContent"));
        Assert.True((await page.EvalAsync("(() => { const t = document.querySelectorAll('.usage-tile'); return t[0].getBoundingClientRect().top === t[1].getBoundingClientRect().top; })()")).GetBoolean());   // side by side when wide
    }

    [WindowsTheory]
    [InlineData(85, "warn")]
    [InlineData(97, "full")]
    [InlineData(130, "full")]
    public async Task TheMeter_TurnsAmberThenRed_AndAShareOverAHundredFillsIt(int percent, string kind)
    {
        _backend.Card = UsageCard(percent);
        if (await UsagePageAsync() is not { } page)
        {
            return;
        }

        await OpenUsageAsync(page);
        Assert.True((await page.EvalAsync("document.querySelector('.usage-meter').classList.contains('" + kind + "')")).GetBoolean());
        Assert.Equal(Math.Min(percent, 100) + "%", await page.TextAsync("document.querySelector('.usage-meter-fill').style.width"));
        Assert.Equal(percent + "%", await page.TextAsync("document.querySelector('.usage-share').textContent"));
    }

    [WindowsFact]
    public async Task WithTheWindowUnknown_ThereIsNoMeter_AndNothingCountedSaysSo()
    {
        _backend.Card = UsageCard(null) with { Sections = [], Empty = NeonSidekick.App.UsageText.NothingCounted };
        if (await UsagePageAsync() is not { } page)
        {
            return;
        }

        await OpenUsageAsync(page);
        Assert.Equal(0, (await page.EvalAsync("document.querySelectorAll('.usage-meter').length")).GetInt32());
        Assert.Equal("unknown", await page.TextAsync("document.querySelector('.usage-source').textContent"));
        Assert.Equal(NeonSidekick.App.UsageText.NothingCounted, await page.TextAsync("document.getElementById('usage-empty').textContent"));
    }

    [WindowsFact]
    public async Task AtThreeHundredTwentyPixels_TheTilesStack_WithNoSidewaysScroll()
    {
        _backend.Card = UsageCard(30);
        if (await UsagePageAsync(width: 320) is not { } page)
        {
            return;
        }

        await OpenUsageAsync(page);
        Assert.True((await page.EvalAsync("document.documentElement.scrollWidth <= innerWidth")).GetBoolean());
        Assert.True((await page.EvalAsync("(() => { const t = document.querySelectorAll('.usage-tile'); return t[1].getBoundingClientRect().top > t[0].getBoundingClientRect().top; })()")).GetBoolean());
    }

    [WindowsFact]
    public async Task TheOpenCard_FollowsAStatusWhoseUsageChanged()
    {
        _backend.Card = UsageCard(30);
        if (await UsagePageAsync() is not { } page)
        {
            return;
        }

        await OpenUsageAsync(page);
        _backend.Card = UsageCard(31, inUse: "1.3k");
        PublishWhenStreaming(new WebEvent("status", () => JsonSerializer.Serialize(new WebStatus(null, null, null, new WebUsage("1.3k / 4.1k", "31%", "46 tok/s")), WebUiJsonContext.Default.WebStatus)));
        await page.WaitAsync("document.querySelector('.usage-figures').textContent === '1.3k / 4.1k'", "the card read again");
    }
}
