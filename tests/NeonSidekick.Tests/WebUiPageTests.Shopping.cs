using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Shopping tab's list controls in headless Edge (2026-10-16, the user's ask): the add box posts (cleared on success, kept on a refusal), › opens
/// the line's sheet whose − / + post the quantity once the taps stop, rename saves, 🗑️ and ⊠ need two taps, ☑ all posts, 🕘 lists the past items
/// (a tap re-adds, 🗑️ forgets), Esc and Back close a sheet, and a tap on a row still ticks.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string Sheet = "!document.getElementById('sheet-back').classList.contains('hidden')";

    private static WebShopItem ShopLine(long id, string name, int qty = 1, bool ticked = false) =>
        new(id, qty > 1 ? name + " ×" + qty.ToString(System.Globalization.CultureInfo.InvariantCulture) : name, "", false, ticked) { Name = name, Qty = qty };

    private async Task<HeadlessPage?> ShoppingPageAsync(bool withTicked = false)
    {
        IReadOnlyList<WebShopItem> ticked = withTicked ? [ShopLine(9, "Bread", ticked: true)] : [];
        _backend.List = new WebShopping("Shopping list: 2 items to get.", null, [new WebShopGroup("Unplaced", [ShopLine(7, "Milk", 2), ShopLine(8, "Eggs")])], "Ticked", ticked, "(guess)", "tag")
        {
            Writable = true,
            AnyOpen = true,
            Words = WebUiText.ShopWords,
        };
        if (await SignedInAsync("/shopping") is not { } page)
        {
            return null;
        }

        await page.WaitAsync("!document.getElementById('add').classList.contains('hidden') && document.querySelectorAll('.shop-row').length === " + (withTicked ? "3" : "2"), "the list and its controls");
        return page;
    }

    private static async Task OpenMilkAsync(HeadlessPage page)
    {
        await page.EvalAsync("document.querySelector('.shop-row .shop-more').click()");
        await page.WaitAsync(Sheet + " && document.getElementById('sheet-title').textContent === 'Milk'", "Milk's sheet");
    }

    private static Task SetInputAsync(HeadlessPage page, string id, string text) =>
        page.EvalAsync("(() => { const t = document.getElementById('" + id + "'); t.value = " + JsonSerializer.Serialize(text) + "; t.dispatchEvent(new Event('input')); })()");

    [WindowsFact]
    public async Task Shopping_TheAddBox_Posts_ClearsOnSuccess_AndKeepsItsTextOnARefusal()
    {
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        Assert.True((await page.EvalAsync("document.getElementById('add-go').disabled")).GetBoolean());   // blank: ⊕ off
        Assert.Equal(WebUiText.ShopWords.AddPlaceholder, await page.TextAsync("document.getElementById('add-text').placeholder"));
        await SetInputAsync(page, "add-text", "2 milk, eggs");
        await page.EvalAsync("document.getElementById('add-go').click()");
        await page.WaitAsync("document.getElementById('add-text').value === ''", "the box cleared");
        Assert.Equal("add 2 milk, eggs", Assert.Single(_backend.ShopCalls));

        _backend.ShopReply = new WebReply(WebOutcome.Refused, WebUiText.TooManyToAdd(100));
        await SetInputAsync(page, "add-text", "a, b");
        await KeyAsync(page, "Enter", "document.getElementById('add-text')");
        await page.WaitAsync("document.getElementById('message').textContent === " + JsonSerializer.Serialize(WebUiText.TooManyToAdd(100)), "the refusal said");
        Assert.Equal("a, b", await page.TextAsync("document.getElementById('add-text').value"));   // kept to fix
        Assert.Equal(2, _backend.ShopCalls.Count);
    }

    [WindowsFact]
    public async Task Shopping_TheMoreButton_OpensTheSheet_AndMinusPlus_PostOneAbsoluteQuantity()
    {
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        Assert.Equal(WebUiText.ShopWords.Options.Replace("{0}", "Milk", StringComparison.Ordinal), await page.TextAsync("document.querySelector('.shop-more').getAttribute('aria-label')"));
        await OpenMilkAsync(page);
        Assert.Equal("2", await page.TextAsync("document.getElementById('qty').textContent"));

        await page.EvalAsync("(() => { const p = document.getElementById('qty-more'); p.click(); p.click(); })()");
        Assert.Equal("4", await page.TextAsync("document.getElementById('qty').textContent"));   // shown at once
        await WaitForAsync(() => !_backend.ShopCalls.IsEmpty);
        await Task.Delay(500);   // the taps were one burst: one post
        Assert.Equal("quantity 7 4", Assert.Single(_backend.ShopCalls));

        // Floored at 1, and a sheet closed inside the wait posts at once.
        _backend.ShopCalls.Clear();
        await page.EvalAsync("(() => { const m = document.getElementById('qty-less'); for (let i = 0; i < 6; i++) m.click(); document.getElementById('sheet-close').click(); })()");
        await WaitForAsync(() => !_backend.ShopCalls.IsEmpty);
        Assert.Equal("quantity 7 1", Assert.Single(_backend.ShopCalls));
        await page.WaitAsync("!(" + Sheet + ")", "the sheet closed");
    }

    [WindowsFact]
    public async Task Shopping_RenameSaves_AndIsOffWhileTheBoxIsBlank()
    {
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        await OpenMilkAsync(page);
        Assert.Equal("Milk", await page.TextAsync("document.getElementById('rename-text').value"));
        await SetInputAsync(page, "rename-text", "  ");
        Assert.True((await page.EvalAsync("document.getElementById('rename-save').disabled")).GetBoolean());
        await SetInputAsync(page, "rename-text", "Oat milk");
        await page.EvalAsync("document.getElementById('rename-save').click()");
        await WaitForAsync(() => !_backend.ShopCalls.IsEmpty);
        Assert.Equal("rename 7 Oat milk", Assert.Single(_backend.ShopCalls));
    }

    [WindowsFact]
    public async Task Shopping_TheSheetsTrash_NeedsTwoTaps_AndClosesTheSheet()
    {
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        await OpenMilkAsync(page);
        await page.EvalAsync("document.getElementById('remove').click()");
        Assert.True((await page.EvalAsync("document.getElementById('remove').classList.contains('armed')")).GetBoolean());
        Assert.Equal(WebUiText.ShopWords.RemoveArmed, await page.TextAsync("document.getElementById('remove').textContent"));
        Assert.Empty(_backend.ShopCalls);

        await page.EvalAsync("document.getElementById('remove').click()");
        await WaitForAsync(() => !_backend.ShopCalls.IsEmpty);
        Assert.Equal("remove 7", Assert.Single(_backend.ShopCalls));
        await page.WaitAsync("!(" + Sheet + ")", "the sheet closed");
    }

    [WindowsFact]
    public async Task Shopping_TickAllPosts_AndClearTicked_TakesTwoTaps()
    {
        if (await ShoppingPageAsync(withTicked: true) is not { } page)
        {
            return;
        }

        Assert.Equal(WebUiText.ShopWords.TickAll, await page.TextAsync("document.getElementById('tick-all').textContent"));
        await page.EvalAsync("document.getElementById('tick-all').click()");
        await WaitForAsync(() => !_backend.ShopCalls.IsEmpty);
        Assert.True(_backend.ShopCalls.TryDequeue(out string? all));
        Assert.Equal("checkall True", all);

        await page.WaitAsync("!document.getElementById('clear').classList.contains('hidden')", "⊠ with a line ticked");
        await page.EvalAsync("document.getElementById('clear').click()");
        Assert.Equal(WebUiText.ShopWords.ClearArmed, await page.TextAsync("document.getElementById('clear').textContent"));
        Assert.Empty(_backend.ShopCalls);
        await page.EvalAsync("document.getElementById('clear').click()");
        await WaitForAsync(() => !_backend.ShopCalls.IsEmpty);
        Assert.Equal("clear", Assert.Single(_backend.ShopCalls));
    }

    [WindowsFact]
    public async Task Shopping_ClearTicked_IsHiddenWithNothingTicked_AndNoneShowsOnceAllAre()
    {
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        Assert.True((await page.EvalAsync("document.getElementById('clear').classList.contains('hidden')")).GetBoolean());
        _backend.List = new WebShopping(_backend.List.Header, null, [], "Ticked", [ShopLine(7, "Milk", 2, ticked: true)], "(guess)", "tag")
        {
            Writable = true,
            AnyOpen = false,
            Words = WebUiText.ShopWords,
        };
        await page.WaitAsync("document.getElementById('tick-all').textContent === " + JsonSerializer.Serialize(WebUiText.ShopWords.UntickAll), "☐ none, read again", 15);
        Assert.False((await page.EvalAsync("document.getElementById('clear').classList.contains('hidden')")).GetBoolean());
    }

    [WindowsFact]
    public async Task Shopping_PastItems_AreListed_ATapReAdds_AndTheTrashForgets()
    {
        _backend.BoughtList = new WebBought([new WebBoughtItem(4, "Bread ×2 · 3 times · last Oct 12", "Bread", 2), new WebBoughtItem(5, "Butter · once · last Oct 1", "Butter", 1)], "2 lines bought before and not on the list");
        _backend.OnShop = call =>
        {
            // The real list leaves a past item out once it is on the list again, or forgotten.
            long gone = call.StartsWith("readd ", StringComparison.Ordinal) || call.StartsWith("forget ", StringComparison.Ordinal) ? long.Parse(call[(call.IndexOf(' ', StringComparison.Ordinal) + 1)..], System.Globalization.CultureInfo.InvariantCulture) : -1;
            if (gone > 0 && _backend.BoughtList is { } b)
            {
                _backend.BoughtList = new WebBought([.. b.Items.Where(i => i.Id != gone)], b.Caption);
            }
        };
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        const string PastRows = "document.querySelectorAll('#past-list .shop-past-row').length";
        await page.EvalAsync("document.getElementById('past').click()");
        await page.WaitAsync(Sheet + " && " + PastRows + " === 2", "the past items");
        Assert.Equal(WebUiText.ShopWords.PastTitle, await page.TextAsync("document.getElementById('sheet-title').textContent"));
        Assert.True((await page.EvalAsync("document.getElementById('past-filter').classList.contains('hidden')")).GetBoolean());   // 8 rows or fewer

        await page.EvalAsync("document.querySelector('.shop-past-add').click()");
        Assert.Equal(1, (await page.EvalAsync(PastRows)).GetInt32());   // the row leaves at once
        await WaitForAsync(() => !_backend.ShopCalls.IsEmpty);
        Assert.True(_backend.ShopCalls.TryDequeue(out string? readd));
        Assert.Equal("readd 4", readd);
        Assert.True((await page.EvalAsync(Sheet)).GetBoolean());   // the sheet stays for the next

        await page.EvalAsync("document.querySelector('.shop-forget').click()");
        Assert.True((await page.EvalAsync("document.querySelector('.shop-forget').classList.contains('armed')")).GetBoolean());
        Assert.Empty(_backend.ShopCalls);
        await page.EvalAsync("document.querySelector('.shop-forget').click()");
        await page.WaitAsync(PastRows + " === 0", "the forgotten one gone");
        Assert.Equal("forget 5", Assert.Single(_backend.ShopCalls));
    }

    [WindowsFact]
    public async Task Shopping_EscAndBack_CloseASheet()
    {
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        await OpenMilkAsync(page);
        await KeyAsync(page, "Escape", "document");
        await page.WaitAsync("!(" + Sheet + ")", "Esc closed it");

        await OpenMilkAsync(page);
        await page.EvalAsync("history.back()");
        await page.WaitAsync("!(" + Sheet + ")", "Back closed it");
        Assert.Empty(_backend.ShopCalls);
    }

    [WindowsFact]
    public async Task Shopping_ATapOnARow_StillTicks()
    {
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        await page.EvalAsync("document.querySelector('.shop-row .shop-item').click()");
        await WaitForAsync(() => !_backend.Checks.IsEmpty);
        Assert.True(_backend.Checks.TryDequeue(out var check));
        Assert.Equal((7L, true), (check.Id, check.On));
        Assert.False((await page.EvalAsync(Sheet)).GetBoolean());
    }

    [WindowsFact]
    public async Task Shopping_ALongPress_OpensTheSheet_AndTheClickAfterItTicksNothing()
    {
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        const string Row = "document.querySelector('.shop-row .shop-item')";
        await page.EvalAsync(Row + ".dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, clientX: 5, clientY: 5 }))");
        await page.WaitAsync(Sheet + " && document.getElementById('sheet-title').textContent === 'Milk'", "the sheet after a long-press");
        await page.EvalAsync("(() => { const r = " + Row + "; r.dispatchEvent(new PointerEvent('pointerup', { bubbles: true })); r.click(); })()");
        await Task.Delay(300);
        Assert.Empty(_backend.Checks);
        Assert.True((await page.EvalAsync("(() => { const e = new Event('contextmenu', { bubbles: true, cancelable: true }); " + Row + ".dispatchEvent(e); return e.defaultPrevented; })()")).GetBoolean());
    }
}
