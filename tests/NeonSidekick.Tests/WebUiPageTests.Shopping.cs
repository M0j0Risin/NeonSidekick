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

        // The re-add reads the list again and redraws it, which replaces the rows and disarms (2026-10-19, the CI-like runs): a forget clicked
        // before that redraw was armed on a row the redraw then swapped out. The page's list object before the re-add is kept, so the wait below
        // knows the re-read landed (it assigns a new one and draws in the same step).
        await page.EvalAsync("window.neonBought = bought");
        await page.EvalAsync("document.querySelector('.shop-past-add').click()");
        Assert.Equal(1, (await page.EvalAsync(PastRows)).GetInt32());   // the row leaves at once
        await WaitForAsync(() => !_backend.ShopCalls.IsEmpty);
        Assert.True(_backend.ShopCalls.TryDequeue(out string? readd));
        Assert.Equal("readd 4", readd);
        Assert.True((await page.EvalAsync(Sheet)).GetBoolean());   // the sheet stays for the next
        await page.WaitAsync("bought !== window.neonBought && " + PastRows + " === 1", "the past items read again after the re-add");

        await page.EvalAsync("document.querySelector('.shop-forget').click()");
        Assert.True((await page.EvalAsync("document.querySelector('.shop-forget').classList.contains('armed')")).GetBoolean());
        Assert.Empty(_backend.ShopCalls);
        await page.EvalAsync("document.querySelector('.shop-forget').click()");
        await page.WaitAsync(PastRows + " === 0", "the forgotten one gone");
        Assert.Equal("forget 5", Assert.Single(_backend.ShopCalls));
    }

    private const string PlaceRows = "document.querySelectorAll('#place-list .shop-place').length";

    private async Task<HeadlessPage?> PlaceSheetAsync(int rows)
    {
        if (await ShoppingPageAsync() is not { } page)
        {
            return null;
        }

        await OpenMilkAsync(page);
        await page.EvalAsync("document.getElementById('place').click()");
        await page.WaitAsync(PlaceRows + " === " + rows.ToString(System.Globalization.CultureInfo.InvariantCulture), "the place rows");
        return page;
    }

    /// <summary>⌖ place (2026-11-06, the user's ask): the rows from the GET, the line's marked; a pick takes one tap, posts, closes the sheet and reads the list again, and a label with markup in it shows as text.</summary>
    [WindowsFact]
    public async Task Shopping_ThePlaceSheet_ListsThePlaces_APickPosts_AndALabelIsText()
    {
        const string Hostile = "<img src=x onerror=\"window.hit=1\">Aisle 3";
        _backend.PlacesList = new WebShopPlaces("⌖ Where is Milk?", 2, [new WebShopPlaceChoice("Aisle 2", 3, null), new WebShopPlaceChoice(Hostile, 4, null), new WebShopPlaceChoice("Section: Bakery", null, 5)], 1, false, null);
        if (await PlaceSheetAsync(3) is not { } page)
        {
            return;
        }

        Assert.Equal("⌖ Where is Milk?", await page.TextAsync("document.getElementById('sheet-title').textContent"));
        Assert.Equal(Hostile, await page.TextAsync("document.querySelectorAll('#place-list .shop-place')[1].textContent"));
        Assert.Equal(0, (await page.EvalAsync("document.querySelectorAll('#place-list img').length")).GetInt32());
        Assert.True((await page.EvalAsync("document.querySelectorAll('#place-list .shop-place')[1].classList.contains('here')")).GetBoolean());
        Assert.True((await page.EvalAsync("document.getElementById('place-filter').classList.contains('hidden')")).GetBoolean());   // 8 rows or fewer
        Assert.True((await page.EvalAsync("document.getElementById('line-body').classList.contains('hidden')")).GetBoolean());

        await page.EvalAsync("document.querySelectorAll('#place-list .shop-place')[2].click()");   // one tap
        await page.WaitAsync("document.getElementById('sheet-back').classList.contains('hidden')", "the sheet closed");
        await WaitForAsync(() => _backend.ShopCalls.Contains("place 7 store 2 section 5"));
    }

    /// <summary>The forget row takes two taps, as the page's other removals; a store with no layout shows its note and no rows.</summary>
    [WindowsFact]
    public async Task Shopping_ThePlaceSheet_ForgetTakesTwoTaps_AndANoteStandsInForNoRows()
    {
        _backend.PlacesList = new WebShopPlaces("⌖ Where is Milk?", 2, [new WebShopPlaceChoice("Aisle 2", 3, null)], 0, true, null);
        if (await PlaceSheetAsync(2) is not { } page)
        {
            return;
        }

        const string Forget = "document.querySelectorAll('#place-list .shop-place')[1]";
        Assert.Equal(WebUiText.ShopWords.ForgetPlace, await page.TextAsync(Forget + ".textContent"));
        await page.EvalAsync(Forget + ".click()");
        Assert.True((await page.EvalAsync(Forget + ".classList.contains('armed')")).GetBoolean());
        Assert.DoesNotContain(_backend.ShopCalls, c => c.StartsWith("place ", StringComparison.Ordinal));
        await page.EvalAsync(Forget + ".click()");
        await WaitForAsync(() => _backend.ShopCalls.Contains("place 7 store 2 forget"));

        _backend.PlacesList = new WebShopPlaces("⌖ Where is Milk?", 2, [], -1, false, "No aisles yet.");
        await page.WaitAsync("document.getElementById('sheet-back').classList.contains('hidden')", "the sheet closed");
        await OpenMilkAsync(page);
        await page.EvalAsync("document.getElementById('place').click()");
        await page.WaitAsync("document.getElementById('sheet-note').textContent === 'No aisles yet.'", "the note");
        Assert.Equal(0, (await page.EvalAsync(PlaceRows)).GetInt32());
    }

    /// <summary>A line gone while the sheet opens says so and closes.</summary>
    [WindowsFact]
    public async Task Shopping_ThePlaceSheet_ForALineGone_ClosesWithTheSentence()
    {
        _backend.PlacesList = null;
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        await OpenMilkAsync(page);
        await page.EvalAsync("document.getElementById('place').click()");
        await page.WaitAsync("document.getElementById('sheet-back').classList.contains('hidden')", "the sheet closed");
        await page.WaitAsync("document.getElementById('message').textContent === " + JsonSerializer.Serialize(WebUiText.ShopWords.Gone), "the sentence");
    }

    /// <summary>
    /// The place sheet's note stays in the sheet, never under the page once it closes; a read that fails (not a line gone) keeps the sheet open and
    /// says so; an unavailable list's sheet, with no title, keeps the line's (2026-11-07, the code review).
    /// </summary>
    [WindowsFact]
    public async Task Shopping_ThePlaceSheet_KeepsItsNoteToItself_AndAFailedReadSaysSo()
    {
        _backend.PlacesList = new WebShopPlaces("⌖ Where is Milk?", 2, [], -1, false, "No aisles yet.");
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        await OpenMilkAsync(page);
        await page.EvalAsync("document.getElementById('place').click()");
        await page.WaitAsync("document.getElementById('sheet-note').textContent === 'No aisles yet.'", "the note");
        Assert.NotEqual("No aisles yet.", await page.TextAsync("document.getElementById('message').textContent"));
        await KeyAsync(page, "Escape", "document");
        await page.WaitAsync("!(" + Sheet + ")", "closed");
        Assert.NotEqual("No aisles yet.", await page.TextAsync("document.getElementById('message').textContent"));

        _backend.PlacesFail = true;
        await OpenMilkAsync(page);
        await page.EvalAsync("document.getElementById('place').click()");
        await page.WaitAsync("document.getElementById('sheet-note').textContent === " + JsonSerializer.Serialize(WebUiText.ShopWords.PlaceFailed), "the failed read's sentence");
        Assert.True((await page.EvalAsync(Sheet)).GetBoolean());
        Assert.False((await page.EvalAsync("document.getElementById('place-body').classList.contains('hidden')")).GetBoolean());

        _backend.PlacesFail = false;
        _backend.PlacesList = WebShoppingView.UnavailablePlaces;
        await KeyAsync(page, "Escape", "document");
        await page.WaitAsync("!(" + Sheet + ")", "closed again");
        await OpenMilkAsync(page);
        await page.EvalAsync("document.getElementById('place').click()");
        await page.WaitAsync("document.getElementById('sheet-note').textContent === " + JsonSerializer.Serialize(Shopping.ShoppingText.Unavailable), "the unavailable list's note");
        Assert.Equal("Milk", await page.TextAsync("document.getElementById('sheet-title').textContent"));
    }

    /// <summary>
    /// ⌖ with a name typed and not saved (2026-11-07, the code review: it was dropped): the name is saved first and the place sheet opens; a refused
    /// rename leaves the line's sheet open with the reason, and nothing is read.
    /// </summary>
    [WindowsFact]
    public async Task Shopping_ThePlaceButton_SavesATypedName_AndStopsWhenTheRenameIsRefused()
    {
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        await OpenMilkAsync(page);
        await SetInputAsync(page, "rename-text", "Oat milk");
        await page.EvalAsync("document.getElementById('place').click()");
        await page.WaitAsync("!document.getElementById('place-body').classList.contains('hidden')", "the place sheet");
        await WaitForAsync(() => _backend.ShopCalls.Contains("places 7"));
        Assert.Equal(["rename 7 Oat milk", "places 7"], _backend.ShopCalls);

        await KeyAsync(page, "Escape", "document");
        await page.WaitAsync("!(" + Sheet + ")", "closed");
        _backend.ShopCalls.Clear();
        _backend.ShopReply = new WebReply(WebOutcome.Refused, "That name is taken.");
        await OpenMilkAsync(page);
        await SetInputAsync(page, "rename-text", "Eggs");
        await page.EvalAsync("document.getElementById('place').click()");
        await page.WaitAsync("document.getElementById('sheet-note').textContent === 'That name is taken.'", "the refusal");
        Assert.False((await page.EvalAsync("document.getElementById('line-body').classList.contains('hidden')")).GetBoolean());
        await Task.Delay(300);
        Assert.Equal(["rename 7 Eggs"], _backend.ShopCalls);
    }

    /// <summary>The sheet closed while ⌖'s save is on its way stays closed (2026-11-07, the code review: the place sheet came back over the list).</summary>
    [WindowsFact]
    public async Task Shopping_ThePlaceButton_ClosedWhileSaving_StaysClosed()
    {
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        using var arrived = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _backend.OnShop = call =>
        {
            if (call.StartsWith("rename ", StringComparison.Ordinal))
            {
                arrived.Set();
                release.Wait(TimeSpan.FromSeconds(20));
            }
        };
        await OpenMilkAsync(page);
        await SetInputAsync(page, "rename-text", "Oat milk");
        await page.EvalAsync("document.getElementById('place').click()");
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(10)), "the rename never posted");
        await KeyAsync(page, "Escape", "document");
        await page.WaitAsync("!(" + Sheet + ")", "closed mid-save");
        release.Set();
        await Task.Delay(500);
        Assert.False((await page.EvalAsync(Sheet)).GetBoolean());
        Assert.DoesNotContain("places 7", _backend.ShopCalls);
    }

    private const string Renamed = "Renamed it to Oat milk.";

    private static string MessageIs(string text) => "document.getElementById('message').textContent === " + JsonSerializer.Serialize(text);

    private void ListWith(params WebShopItem[] lines) =>
        _backend.List = _backend.List! with { Groups = [new WebShopGroup("Unplaced", lines)] };

    /// <summary>
    /// ⌖ after a typed name (2026-11-07, the second review: showing the place sheet wiped the rename's sentence): the sentence leads the sheet's
    /// note, and the failed read's sentence too.
    /// </summary>
    [WindowsFact]
    public async Task Shopping_ThePlaceButton_KeepsTheRenamesSentence_AheadOfTheNoteOrTheFailure()
    {
        _backend.ShopReply = new WebReply(WebOutcome.Ok, Renamed);
        _backend.PlacesList = new WebShopPlaces("⌖ Where is Milk?", 2, [], -1, false, "No aisles yet.");
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        await OpenMilkAsync(page);
        await SetInputAsync(page, "rename-text", "Oat milk");
        await page.EvalAsync("document.getElementById('place').click()");
        await page.WaitAsync("document.getElementById('sheet-note').textContent === " + JsonSerializer.Serialize(Renamed + " No aisles yet."), "the rename's sentence, then the note");

        await KeyAsync(page, "Escape", "document");
        await page.WaitAsync("!(" + Sheet + ")", "closed");
        _backend.PlacesFail = true;
        await OpenMilkAsync(page);
        await SetInputAsync(page, "rename-text", "Oat milk");
        await page.EvalAsync("document.getElementById('place').click()");
        await page.WaitAsync("document.getElementById('sheet-note').textContent === " + JsonSerializer.Serialize(Renamed + " " + WebUiText.ShopWords.PlaceFailed), "the rename's sentence, then the failure");
    }

    /// <summary>
    /// A rename through ⌖ that kept the line (2026-11-07, the second review: the sheet stayed quiet for good after any rename): the line removed
    /// later by another device closes the place sheet with the sentence.
    /// </summary>
    [WindowsFact]
    public async Task Shopping_ThePlaceSheet_AfterARenameThatKeptTheLine_SaysWhenTheLineGoes()
    {
        _backend.ShopReply = new WebReply(WebOutcome.Ok, Renamed);
        _backend.PlacesList = new WebShopPlaces("⌖ Where is Oat milk?", 2, [new WebShopPlaceChoice("Aisle 2", 3, null)], 0, false, null);
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        _backend.OnShop = call =>
        {
            if (call.StartsWith("rename ", StringComparison.Ordinal))
            {
                ListWith(ShopLine(7, "Oat milk", 2), ShopLine(8, "Eggs"));
            }
        };
        await OpenMilkAsync(page);
        await SetInputAsync(page, "rename-text", "Oat milk");
        await page.EvalAsync("document.getElementById('place').click()");
        await page.WaitAsync(PlaceRows + " === 1", "the place rows");

        ListWith(ShopLine(8, "Eggs"));
        await page.WaitAsync("!(" + Sheet + ")", "closed for the line gone", 15);
        await page.WaitAsync(MessageIs(WebUiText.ShopWords.Gone), "the sentence");
    }

    /// <summary>
    /// A rename to the line's own name writes nothing, so every read after it is a 304 (2026-11-07, the second review): once one has come back, the
    /// line removed by another device closes the line's sheet with the sentence.
    /// </summary>
    [WindowsFact]
    public async Task Shopping_TheLinesSheet_AfterARenameToItsOwnName_SaysWhenTheLineGoes()
    {
        _backend.ShopReply = new WebReply(WebOutcome.Ok, "Milk is named Milk already.");
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        await OpenMilkAsync(page);
        await SetInputAsync(page, "rename-text", "Milk");
        await page.EvalAsync("document.getElementById('rename-save').click()");
        await WaitForAsync(() => _backend.ShopCalls.Contains("rename 7 Milk"));
        await page.WaitAsync("openSheet !== null && openSheet.quietFrom !== undefined && shownFrom >= openSheet.quietFrom", "a read started after the rename");

        ListWith(ShopLine(8, "Eggs"));
        await page.WaitAsync("!(" + Sheet + ")", "closed for the line gone", 15);
        await page.WaitAsync(MessageIs(WebUiText.ShopWords.Gone), "the sentence");
    }

    /// <summary>A rename that merged the line into another closes its sheet quietly at the next read: the rename's sentence said what happened.</summary>
    [WindowsFact]
    public async Task Shopping_TheLinesSheet_AfterARenameThatMerged_ClosesQuietly()
    {
        const string Merged = "Merged Milk into Eggs.";
        _backend.ShopReply = new WebReply(WebOutcome.Ok, Merged);
        if (await ShoppingPageAsync() is not { } page)
        {
            return;
        }

        _backend.OnShop = call =>
        {
            if (call.StartsWith("rename ", StringComparison.Ordinal))
            {
                ListWith(ShopLine(8, "Eggs", 3));
            }
        };
        await OpenMilkAsync(page);
        await SetInputAsync(page, "rename-text", "Eggs");
        await page.EvalAsync("document.getElementById('rename-save').click()");
        await page.WaitAsync("!(" + Sheet + ")", "closed for the merge", 15);
        await page.WaitAsync("document.querySelectorAll('.shop-row').length === 1", "the merged list");
        Assert.Equal(Merged, await page.TextAsync("document.getElementById('message').textContent"));
    }

    /// <summary>A $ pattern typed in the place filter, and one in a line's name, are text (2026-11-07, the code review: replace() read $& as its match).</summary>
    [WindowsFact]
    public async Task Shopping_DollarPatterns_InTheFilterAndAName_AreText()
    {
        WebShopPlaceChoice[] nine = [.. Enumerable.Range(1, 9).Select(i => new WebShopPlaceChoice("Aisle " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), i, null))];
        _backend.PlacesList = new WebShopPlaces("⌖ Where is Milk?", 2, nine, -1, false, null);
        if (await PlaceSheetAsync(9) is not { } page)
        {
            return;
        }

        await SetInputAsync(page, "place-filter", "a $& b $1");
        await page.WaitAsync("document.getElementById('place-none').textContent.includes('a $& b $1')", "the filter as typed");
        Assert.Equal(WebUiText.ShopWords.NoMatch.Replace("{0}", "a $& b $1", StringComparison.Ordinal), await page.TextAsync("document.getElementById('place-none').textContent"));

        const string Named = "Tea $& cakes";
        _backend.List = _backend.List! with { Groups = [new WebShopGroup("Unplaced", [ShopLine(7, Named)])] };
        await SignedInAsync("/shopping");
        await page.WaitAsync("document.querySelectorAll('.shop-row .shop-more').length === 1", "the one line");
        Assert.Equal(WebUiText.ShopWords.Options.Replace("{0}", Named, StringComparison.Ordinal), await page.TextAsync("document.querySelector('.shop-more').getAttribute('aria-label')"));
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
