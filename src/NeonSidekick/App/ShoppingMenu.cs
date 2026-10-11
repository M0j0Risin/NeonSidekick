using NeonSidekick.Shopping;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>/shopping</c>'s pane (2026-10-11, the user's ask: a checklist to shop from): the list walked through the active store on a
/// <see cref="MenuPane"/> page — each aisle or zone a heading (<see cref="SectionRule"/>) over its lines in the store's order, the
/// unplaced after, the ticked last and dim. Enter or Space ticks or unticks the highlighted line; the buttons (a click or their key)
/// add or take one off its quantity, remove it, tick (or untick) every line, clear the ticked lines and pick the store the list walks through. Nothing asks: a line
/// removed is added again, a bought one is ticked. The list is read again after every act (the model may change it meanwhile), the
/// cursor kept on the same line. The <see cref="YouTubeSavedMenu"/> shape; without the pane the screen prints the list instead.
/// </summary>
internal sealed class ShoppingMenu
{
    /// <summary>
    /// The page's buttons, in the order drawn, each with its glyph (2026-10-11, the user's asks): rename, remove, tick all (untick all,
    /// <c>☐  none</c>, once every line is ticked, <paramref name="anyOpen"/> false: one key, <c>a</c>, either way), clear, the stores.
    /// + and - change the quantity with no button drawn (<see cref="MenuButton.Hidden"/>, last: the title row ran out of room). Pinned.
    /// </summary>
    public static IReadOnlyList<MenuButton> ButtonsFor(bool anyOpen) =>
    [
        new(ShoppingText.RenameButton, ShoppingText.RenameKey),
        new(ShoppingText.RemoveButton, ShoppingText.RemoveKey),
        new(anyOpen ? ShoppingText.TickAllButton : ShoppingText.UntickAllButton, ShoppingText.TickAllKey),
        new(ShoppingText.ClearButton, ShoppingText.ClearKey),
        new(ShoppingText.StoreButton, ShoppingText.StoreKey),
        new(ShoppingText.MoreButton, ShoppingText.MoreKey) { Hidden = true },
        new(ShoppingText.LessButton, ShoppingText.LessKey) { Hidden = true },
    ];

    private const int Rename = 0;
    private const int Remove = 1;
    private const int TickAll = 2;
    private const int ClearTicked = 3;
    private const int Store = 4;
    private const int More = 5;
    private const int Less = 6;

    private readonly ShoppingList _list;
    private readonly MenuPane _pane;
    private readonly Func<CancellationToken, Task<bool>>? _manageStores;
    private readonly bool _closeWhenDone;
    private readonly InputLine? _input;

    /// <param name="list">The list's operations over the loaded profile's store.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="manageStores">The stores' manager the store button opens (2026-10-11, <see cref="SettingsMenu.ManageShoppingStoresAsync"/>); null: a plain picker of the stores.</param>
    /// <param name="closeWhenDone">Whether ESC closes the pane (the <c>/shopping</c> pane's own); false under a host that drew it, the <c>Shopping list</c> row of <c>/tools</c> (2026-10-11), whose page comes back.</param>
    /// <param name="input">The input row a rename is typed on (2026-10-11); null: rename says so.</param>
    public ShoppingMenu(ShoppingList list, MenuPane pane, Func<CancellationToken, Task<bool>>? manageStores = null, bool closeWhenDone = true, InputLine? input = null)
    {
        _input = input;
        _manageStores = manageStores;
        _closeWhenDone = closeWhenDone;
        _list = list ?? throw new ArgumentNullException(nameof(list));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
    }

    /// <summary>The page's rows over <paramref name="view"/>: markup, the headings' indexes, and the line each row stands for (null on a heading).</summary>
    public static (List<string> Rows, HashSet<int> Headings, List<ShopItem?> Items) Rows(ShopView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var rows = new List<string>();
        var headings = new HashSet<int>();
        var items = new List<ShopItem?>();
        foreach (var group in view.Groups)
        {
            headings.Add(rows.Count);
            rows.Add(SectionRule.Markup(ShoppingText.Heading(group)));
            items.Add(null);
            foreach (var placed in group.Items)
            {
                rows.Add(Markup.Escape(ShoppingText.PaneRow(placed.Item, placed.Place.IsGuess)));
                items.Add(placed.Item);
            }
        }

        if (view.Ticked.Count > 0)
        {
            headings.Add(rows.Count);
            rows.Add(SectionRule.Markup(ShoppingText.TickedHeading));
            items.Add(null);
            foreach (var item in view.Ticked)
            {
                rows.Add(Theme.DimMarkup(ShoppingText.PaneRow(item, guess: false)));
                items.Add(item);
            }
        }

        return (rows, headings, items);
    }

    /// <summary>Shows the list until ESC; an empty list is <see cref="ShoppingText.EmptyList"/> on an empty pane.</summary>
    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        long? on = null;
        try
        {
            while (true)
            {
                var shop = _list.StoreFor(null, out _);
                var view = _list.View(shop);
                var (rows, headings, items) = Rows(view);
                if (rows.Count == 0)
                {
                    if (_closeWhenDone)
                    {
                        _pane.Close();
                        await _pane.ShowEmptyAsync(ShoppingText.PaneLabel(shop), ShoppingText.EmptyList, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    // Under a host: the empty list's page (ShowEmptyAsync's, which closes the pane) until ESC, the host's page coming back.
                    var empty = new MenuPage(ShoppingText.PaneLabel(shop), [Theme.DimMarkup(ShoppingText.EmptyList)], MenuPane.EmptyKeys);
                    while (await _pane.PickAsync(empty, 0, cancellationToken).ConfigureAwait(false) is not null)
                    {
                    }

                    return;
                }

                int cursor = on is { } id ? items.FindIndex(i => i?.Id == id) : -1;
                cursor = cursor >= 0 ? cursor : items.FindIndex(i => i is not null);
                var page = new MenuPage(ShoppingText.PaneLabel(shop), rows, ShoppingText.PaneKeys)
                {
                    Buttons = ButtonsFor(view.Groups.Count > 0),
                    Headings = headings,
                    SpaceToggles = true,
                    Caption = ShoppingText.PaneCaption(view.Groups.Sum(g => g.Items.Count), view.Ticked.Count),
                };
                if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return;
                }

                var item = pick.Row >= 0 && pick.Row < items.Count ? items[pick.Row] : null;
                on = item?.Id ?? on;
                switch (pick.Button)
                {
                    case TickAll:
                    {
                        // Tick all while a line is left open, untick all once none is (the button's title said which).
                        bool tick = view.Groups.Count > 0;
                        Say(_list.CheckAll(tick) is { } changed ? ShoppingText.CheckedAll(changed, tick) : "Error: " + ShoppingText.WriteFailed);
                        break;
                    }

                    case ClearTicked:
                        Say(_list.Clear(all: false) is { } cleared ? ShoppingText.Cleared(cleared, all: false) : "Error: " + ShoppingText.WriteFailed);
                        break;
                    case Store when _manageStores is not null:
                        await _manageStores(cancellationToken).ConfigureAwait(false);
                        break;
                    case Store:
                        await PickStoreAsync(shop, cancellationToken).ConfigureAwait(false);
                        break;
                    case More or Less when item is not null:
                        int quantity = Math.Max(1, item.Quantity + (pick.Button == More ? 1 : -1));
                        Say(_list.SetQuantity(item, quantity) ? ShoppingText.Line(item with { Quantity = quantity }) : "Error: " + ShoppingText.WriteFailed);
                        break;
                    case Rename when item is not null:
                        if (_input is null)
                        {
                            _pane.Error(ShoppingText.RenameNeedsInput);
                            break;
                        }

                        // The name typed on the input row under the list, the line's own to start from; ESC or a blank line keeps it.
                        var typed = await _pane.EditAsync(page with { Hint = SettingsMenu.EditKeys }, pick.Row, _input, item.Name, allowEmpty: false, cancellationToken).ConfigureAwait(false);
                        if (typed is InputResult.Submitted { Text: var text } && text.Trim().Length > 0 && text.Trim() != item.Name)
                        {
                            Say(_list.RenameItem(item, text));
                        }

                        break;
                    case Remove when item is not null:
                        Say(_list.Remove(item) ? ShoppingText.Removed(item) : "Error: " + ShoppingText.WriteFailed);
                        // The cursor to the line after it (or before, at the end).
                        int next = items.FindIndex(pick.Row + 1, i => i is not null);
                        on = next >= 0 ? items[next]!.Id : items.Take(pick.Row).LastOrDefault(i => i is not null)?.Id;
                        break;
                    case < 0 when item is not null:
                        Say(_list.Check(item, !item.Checked) ? ShoppingText.Checked(item, !item.Checked) : "Error: " + ShoppingText.WriteFailed);
                        break;
                }
            }
        }
        finally
        {
            if (_closeWhenDone)
            {
                _pane.Close();
            }
        }
    }

    /// <summary>The stores on a picker, the active one under the cursor; a pick makes it the list's store. None set up says so.</summary>
    private async Task PickStoreAsync(ShopStore? active, CancellationToken cancellationToken)
    {
        var stores = _list.Store.Stores();
        if (stores.Count == 0)
        {
            _pane.Notice(ShoppingText.NoStoresYet);
            return;
        }

        var page = new MenuPage(ShoppingText.StorePickTitle, stores.Select(s => Markup.Escape(s.Name)).ToList(), SettingsMenu.PickKeys);
        int at = Math.Max(0, stores.ToList().FindIndex(s => s.Id == active?.Id));
        if (await _pane.PickAsync(page, at, cancellationToken).ConfigureAwait(false) is { Row: >= 0 } picked && picked.Row < stores.Count)
        {
            Say(_list.UseStore(stores[picked.Row]));
        }
    }

    private const string ErrorPrefix = "Error: ";

    /// <summary>An answer on the pane's status line: an error as one, anything else a notice.</summary>
    private void Say(string result)
    {
        if (result.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            _pane.Error(result[ErrorPrefix.Length..]);
        }
        else
        {
            _pane.Notice(result);
        }
    }

}
