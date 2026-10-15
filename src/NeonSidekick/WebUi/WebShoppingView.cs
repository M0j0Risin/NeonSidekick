using NeonSidekick.Shopping;

namespace NeonSidekick.WebUi;

/// <summary>
/// The shopping list as the Web UI's page shows it (2026-10-14): <see cref="ShoppingList.View"/> through the active store, every word
/// the app's own (<see cref="ShoppingText"/>) — the header is the first line <see cref="ShoppingText.List"/> gives the model, each
/// group under <see cref="ShoppingText.Heading"/>, each line <see cref="ShoppingText.Line"/> with its note apart (the page dims it), a
/// guessed place marked <see cref="ShoppingText.GuessMark"/>, the ticked lines under <see cref="ShoppingText.TickedHeading"/>. Pure
/// over the list it is given.
/// </summary>
public static class WebShoppingView
{
    /// <summary>The page's list, or the sentence that stands in for it (the list unavailable, or empty).</summary>
    public static WebShopping Of(ShoppingList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (!list.Store.Available)
        {
            return new WebShopping("", ShoppingText.Unavailable, [], ShoppingText.TickedHeading, [], ShoppingText.GuessMark, WebUiAssets.Current);
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
        };
    }

    /// <summary>
    /// Makes store <paramref name="id"/> the active one as the page asks (2026-10-15, the store picker): not found when it went meanwhile, refused when
    /// the write failed, and Ok with nothing done for the store already active. <paramref name="changed"/> is the store's name when it changed
    /// (<see cref="ShoppingList.UseStore"/> wrote its audit line).
    /// </summary>
    public static WebReply UseStore(ShoppingList list, long id, out string? changed)
    {
        ArgumentNullException.ThrowIfNull(list);
        changed = null;
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
            return new WebReply(WebOutcome.Refused, WebUiText.ShoppingWriteFailed);
        }

        changed = shop.Name;
        return WebReply.Ok;
    }

    private static WebShopItem Item(ShopItem item, bool guess) =>
        new(item.Id, ShoppingText.Line(item with { Note = "" }), item.Note, guess, item.Checked);

    /// <summary>
    /// Ticks or unticks the line <paramref name="id"/> as the page asks (an explicit state, so a repeat changes nothing): not found when
    /// the line went meanwhile, refused when the write failed. <paramref name="changed"/> is the line's text for the audit line.
    /// </summary>
    public static WebReply Check(ShoppingList list, long id, bool on, out string? changed)
    {
        ArgumentNullException.ThrowIfNull(list);
        changed = null;
        if (list.Store.Items().FirstOrDefault(i => i.Id == id) is not { } item)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.NoSuchItem);
        }

        if (item.Checked == on)
        {
            return WebReply.Ok;   // as asked already: no change, no audit line
        }

        if (!list.Check(item, on))
        {
            return new WebReply(WebOutcome.Refused, WebUiText.ShoppingWriteFailed);
        }

        changed = ShoppingText.Line(item);
        return WebReply.Ok;
    }
}
