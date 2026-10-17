using NeonSidekick.Shopping;

namespace NeonSidekick.WebUi;

/// <summary>
/// The shopping list as the Web UI's page shows it (2026-10-14): <see cref="ShoppingList.View"/> through the active store, every word
/// the app's own (<see cref="ShoppingText"/>) — the header is the first line <see cref="ShoppingText.List"/> gives the model, each
/// group under <see cref="ShoppingText.Heading"/>, each line <see cref="ShoppingText.Line"/> with its note apart (the page dims it), a
/// guessed place marked <see cref="ShoppingText.GuessMark"/>, the ticked lines under <see cref="ShoppingText.TickedHeading"/>. Pure
/// over the list it is given. 2026-10-16 (the user's ask and picks: the <c>/shopping</c> pane's list controls on the page, group A only): one
/// helper per action the page posts — add typed items, re-add or forget a past item, a line's quantity, rename and remove, tick all or none,
/// clear the ticked — each shaped like <see cref="Check"/>: the line found by id (gone meanwhile: not found), an explicit target where there is
/// one (a repeat changes nothing and logs nothing), refused on a failed write or an unavailable list, and an <c>out</c> text for the audit line.
/// Placing a line, the stores beyond the picker and the layout stay at the PC.
/// </summary>
public static class WebShoppingView
{
    /// <summary>
    /// The most items one add takes (2026-10-16): nothing caps <see cref="ShoppingCommand.Items"/>, and the host's 64 KB body still holds
    /// thousands of comma-separated lines in one request, which the per-request Act bucket can't stop. More is refused whole. The PC's typed
    /// add keeps no cap, since a person types it.
    /// </summary>
    public const int MaxAdd = 100;

    /// <summary>The page's list, or the sentence that stands in for it (the list unavailable, or empty).</summary>
    public static WebShopping Of(ShoppingList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (!list.Store.Available)
        {
            return Unavailable();
        }

        var view = list.View(list.Store.ActiveStore());
        string summary = ShoppingText.List(view, withTicked: false);
        int end = summary.IndexOf('\n', StringComparison.Ordinal);
        string header = end < 0 ? summary : summary[..end];
        var groups = view.Groups
            .Select(g => new WebShopGroup(ShoppingText.Heading(g), g.Items.Select(p => Item(p.Item, p.Place.IsGuess)).ToList()))
            .ToList();
        var ticked = view.Ticked.Select(i => Item(i, guess: false)).ToList();
        return new WebShopping(header, null, groups, ShoppingText.TickedHeading, ticked, ShoppingText.GuessMark, WebUiAssets.Current)
        {
            // The picker (2026-10-15): every store by name and the one walked now.
            Stores = list.Store.Stores().Select(s => new WebShopStore(s.Id, s.Name)).ToList(),
            Active = view.Store?.Id,
            StoreLabel = WebUiText.StoreLabel,
            // The list's own controls (2026-10-16): an empty but available list keeps the add box and 🕘, an empty list being when adding
            // matters most; tick all while any line is open, else none.
            Writable = true,
            AnyOpen = groups.Any(g => g.Items.Count > 0),
            Words = WebUiText.ShopWords,
        };
    }

    /// <summary>The unavailable list (no list, or its file could not be opened): the note and nothing to change (<see cref="WebShopping.Writable"/> false).</summary>
    public static WebShopping Unavailable() =>
        new("", ShoppingText.Unavailable, [], ShoppingText.TickedHeading, [], ShoppingText.GuessMark, WebUiAssets.Current) { Words = WebUiText.ShopWords };

    /// <summary>
    /// Makes store <paramref name="id"/> the active one as the page asks (2026-10-15, the store picker): not found when it went meanwhile, refused when
    /// the write failed, and Ok with nothing done for the store already active. <paramref name="changed"/> is the store's name when it changed
    /// (<see cref="ShoppingList.UseStore"/> wrote its audit line).
    /// </summary>
    public static WebReply UseStore(ShoppingList list, long id, out string? changed)
    {
        ArgumentNullException.ThrowIfNull(list);
        changed = null;
        if (Closed(list) is { } closed)
        {
            return closed;
        }

        if (list.Store.Stores().FirstOrDefault(s => s.Id == id) is not { } shop)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.NoSuchStore);
        }

        if (list.Store.ActiveStore()?.Id == shop.Id)
        {
            return WebReply.Ok;   // as asked already: no change, no audit line
        }

        if (list.UseStore(shop).StartsWith("Error:", StringComparison.Ordinal))
        {
            return WriteFailed;
        }

        changed = shop.Name;
        return WebReply.Ok;
    }

    private static WebShopItem Item(ShopItem item, bool guess) =>
        new(item.Id, ShoppingText.Line(item with { Note = "" }), item.Note, guess, item.Checked) { Name = item.Name, Qty = item.Quantity };

    /// <summary>
    /// Ticks or unticks the line <paramref name="id"/> as the page asks (an explicit state, so a repeat changes nothing): not found when
    /// the line went meanwhile, refused when the write failed. <paramref name="changed"/> is the line's text for the audit line.
    /// </summary>
    public static WebReply Check(ShoppingList list, long id, bool on, out string? changed)
    {
        ArgumentNullException.ThrowIfNull(list);
        changed = null;
        if (Closed(list) is { } closed)
        {
            return closed;
        }

        if (Line(list, id) is not { } item)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.NoSuchItem);
        }

        if (item.Checked == on)
        {
            return WebReply.Ok;   // as asked already: no change, no audit line
        }

        if (!list.Check(item, on))
        {
            return WriteFailed;
        }

        changed = ShoppingText.Line(item);
        return WebReply.Ok;
    }

    // ── The list's own controls (2026-10-16) ─────────────────────────────────

    /// <summary>
    /// The add box's text on the list, as the PC's add page takes it (<see cref="ShoppingCommand.Items"/>: commas or semicolons between, each
    /// piece through <see cref="ShoppingList.ParseLine"/>, so <c>2 milk, eggs</c>), placed at the active store. A box with no item in it is refused
    /// (<see cref="WebUiText.NothingToAdd"/>; the PC's add page has no such sentence), and more than <see cref="MaxAdd"/> items whole. The reply's
    /// sentence is <see cref="ShoppingText.AddedForUser"/>; <paramref name="added"/> the lines as they now read, for the audit line.
    /// </summary>
    public static WebReply Add(ShoppingList list, string? text, out IReadOnlyList<string> added)
    {
        ArgumentNullException.ThrowIfNull(list);
        added = [];
        if (Closed(list) is { } closed)
        {
            return closed;
        }

        var pieces = ShoppingCommand.Items(text ?? "");
        if (pieces.Count == 0)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.NothingToAdd);
        }

        if (pieces.Count > MaxAdd)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.TooManyToAdd(MaxAdd));
        }

        var shop = list.StoreFor(null, out _);
        var done = new List<ShopAdded>();
        foreach (string piece in pieces)
        {
            var (name, quantity) = ShoppingList.ParseLine(piece);
            if (name.Length > 0 && list.Add(name, quantity, shop) is { } one)
            {
                done.Add(one);
            }
        }

        if (done.Count == 0)
        {
            return WriteFailed;
        }

        added = done.Select(a => ShoppingText.Line(a.Item)).ToList();
        return new WebReply(WebOutcome.Ok, ShoppingText.AddedForUser(done, shop));
    }

    /// <summary>
    /// What was bought before and is not on the list now (<see cref="ShoppingList.Bought"/>, the most often first), for the page's 🕘 sheet: each
    /// past item's id, its row as the PC's add page reads it (<see cref="ShoppingText.BoughtRow"/>, in <paramref name="zone"/>), name and last quantity.
    /// Empty while the list is unavailable.
    /// </summary>
    public static WebBought Bought(ShoppingList list, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(zone);
        IReadOnlyList<ShopBought> bought = list.Store.Available ? list.Bought() : [];
        return new WebBought(
            bought.Select(b => new WebBoughtItem(b.Id, ShoppingText.BoughtRow(b, zone), b.Name, b.Quantity)).ToList(),
            bought.Count == 0 ? ShoppingText.NothingBoughtYet : ShoppingText.BoughtCaption(bought.Count));
    }

    /// <summary>
    /// Past item <paramref name="id"/> added again at its last quantity. Found in <see cref="ShoppingList.Bought"/>, which leaves out anything on the
    /// list now, so a quick double tap adds it once: the second finds nothing (<see cref="WebUiText.NoSuchPastItem"/>), never bumping the quantity
    /// twice; the same for one forgotten, or put back on the list from the PC.
    /// </summary>
    public static WebReply ReAdd(ShoppingList list, long id, out string? added)
    {
        ArgumentNullException.ThrowIfNull(list);
        added = null;
        if (Closed(list) is { } closed)
        {
            return closed;
        }

        if (Past(list, id) is not { } bought)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.NoSuchPastItem);
        }

        var shop = list.StoreFor(null, out _);
        if (list.Add(bought.Name, bought.Quantity, shop) is not { } one)
        {
            return WriteFailed;
        }

        added = ShoppingText.Line(one.Item);
        return new WebReply(WebOutcome.Ok, ShoppingText.AddedForUser([one], shop));
    }

    /// <summary>Past item <paramref name="id"/> forgotten (<see cref="ShoppingList.ForgetBought"/>): it comes back only when bought again. <paramref name="forgotten"/> is its name.</summary>
    public static WebReply Forget(ShoppingList list, long id, out string? forgotten)
    {
        ArgumentNullException.ThrowIfNull(list);
        forgotten = null;
        if (Closed(list) is { } closed)
        {
            return closed;
        }

        if (Past(list, id) is not { } bought)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.NoSuchPastItem);
        }

        string answer = list.ForgetBought(bought);
        if (answer.StartsWith("Error:", StringComparison.Ordinal))
        {
            return WriteFailed;
        }

        forgotten = bought.Name;
        return new WebReply(WebOutcome.Ok, answer);
    }

    /// <summary>
    /// Line <paramref name="id"/>'s quantity set to <paramref name="quantity"/>, floored at 1 (an absolute quantity: the page's −/+ taps post once
    /// they stop, and a repeat changes nothing). <paramref name="changed"/> is the line as it now reads.
    /// </summary>
    public static WebReply Quantity(ShoppingList list, long id, int quantity, out string? changed)
    {
        ArgumentNullException.ThrowIfNull(list);
        changed = null;
        if (Closed(list) is { } closed)
        {
            return closed;
        }

        if (Line(list, id) is not { } item)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.NoSuchItem);
        }

        quantity = Math.Max(1, quantity);
        if (item.Quantity == quantity)
        {
            return WebReply.Ok;
        }

        if (!list.SetQuantity(item, quantity))
        {
            return WriteFailed;
        }

        changed = ShoppingText.Line(item with { Quantity = quantity });
        return WebReply.Ok;
    }

    /// <summary>
    /// Line <paramref name="id"/> renamed (<see cref="ShoppingList.RenameItem"/>): its answer is the reply's sentence, so a name another line has
    /// merges the two as at the PC, and an <c>Error: </c> answer (a blank name) is refused with that text. A name equal to the line's own (the
    /// trimmed text, exactly) is Ok with nothing written or logged, since the rename would rename it to itself and audit it; a change of case
    /// alone still renames. <paramref name="from"/> and <paramref name="to"/> are the names for the audit line.
    /// </summary>
    public static WebReply Rename(ShoppingList list, long id, string? name, out string? from, out string? to)
    {
        ArgumentNullException.ThrowIfNull(list);
        from = null;
        to = null;
        if (Closed(list) is { } closed)
        {
            return closed;
        }

        if (Line(list, id) is not { } item)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.NoSuchItem);
        }

        string trimmed = (name ?? "").Trim();
        if (string.Equals(trimmed, item.Name, StringComparison.Ordinal))
        {
            return WebReply.Ok;
        }

        string answer = list.RenameItem(item, trimmed);
        if (answer.StartsWith("Error:", StringComparison.Ordinal))
        {
            return new WebReply(WebOutcome.Refused, answer);
        }

        from = item.Name;
        to = trimmed;
        return new WebReply(WebOutcome.Ok, answer);
    }

    /// <summary>Line <paramref name="id"/> taken off the list (<see cref="ShoppingList.Remove"/>: a ticked line is recorded as bought, as at the PC).</summary>
    public static WebReply Remove(ShoppingList list, long id, out string? removed)
    {
        ArgumentNullException.ThrowIfNull(list);
        removed = null;
        if (Closed(list) is { } closed)
        {
            return closed;
        }

        if (Line(list, id) is not { } item)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.NoSuchItem);
        }

        if (!list.Remove(item))
        {
            return WriteFailed;
        }

        removed = ShoppingText.Line(item);
        return new WebReply(WebOutcome.Ok, ShoppingText.Removed(item));
    }

    /// <summary>
    /// Every line ticked (or unticked), explicit: <paramref name="changed"/> is how many changed, 0 for a repeat or the PC there first (Ok, and
    /// the session logs, flashes and recounts nothing). The reply's sentence is <see cref="ShoppingText.CheckedAll"/>.
    /// </summary>
    public static WebReply CheckAll(ShoppingList list, bool on, out int changed)
    {
        ArgumentNullException.ThrowIfNull(list);
        changed = 0;
        if (Closed(list) is { } closed)
        {
            return closed;
        }

        if (list.CheckAll(on) is not { } count)
        {
            return WriteFailed;
        }

        changed = count;
        return new WebReply(WebOutcome.Ok, ShoppingText.CheckedAll(count, on));
    }

    /// <summary>
    /// The ticked lines cleared (<c>Clear(all: false)</c>, recorded as bought): <paramref name="cleared"/> is how many went, 0 when none was
    /// ticked. The whole-list clear stays a Chat command (<c>/shopping clear all</c>).
    /// </summary>
    public static WebReply ClearTicked(ShoppingList list, out int cleared)
    {
        ArgumentNullException.ThrowIfNull(list);
        cleared = 0;
        if (Closed(list) is { } closed)
        {
            return closed;
        }

        if (list.Clear(all: false) is not { } count)
        {
            return WriteFailed;
        }

        cleared = count;
        return new WebReply(WebOutcome.Ok, ShoppingText.Cleared(count, all: false));
    }

    private static readonly WebReply WriteFailed = new(WebOutcome.Refused, WebUiText.ShoppingWriteFailed);

    /// <summary>The refusal every post gets while the list is unavailable (2026-10-16: <see cref="WebShopping.Writable"/> false), or null.</summary>
    private static WebReply? Closed(ShoppingList list) =>
        list.Store.Available ? null : new WebReply(WebOutcome.Refused, ShoppingText.Unavailable);

    private static ShopItem? Line(ShoppingList list, long id) => list.Store.Items().FirstOrDefault(i => i.Id == id);

    private static ShopBought? Past(ShoppingList list, long id) => list.Bought().FirstOrDefault(b => b.Id == id);
}
