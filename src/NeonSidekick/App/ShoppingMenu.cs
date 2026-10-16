using NeonSidekick.Shopping;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>/shopping</c>'s pane (2026-10-11, the user's ask: a checklist to shop from): the list walked through the active store on a
/// <see cref="MenuPane"/> page — each aisle or zone a heading (<see cref="SectionRule"/>) over its lines in the store's order, the
/// unplaced after, the ticked last and dim. Enter or Space ticks or unticks the highlighted line; the buttons (a click or their key)
/// add lines (typed, or the recently bought, <see cref="AddAsync"/>), place the highlighted one (<see cref="PlaceAsync"/>), rename it,
/// remove it, tick (or untick) every line, clear the ticked lines and open the stores' manager; + and - change its quantity. Nothing
/// asks: a line removed is added again, a bought one is ticked. The list is read again after every act (the model may change it
/// meanwhile), the cursor kept on the same line. An empty list shows its one dim row with the same bar, so add works on it. The
/// <see cref="YouTubeSavedMenu"/> shape; without the pane the screen prints the list instead.
/// </summary>
internal sealed class ShoppingMenu
{
    /// <summary>
    /// The page's buttons, in the order drawn, each with its glyph (2026-10-11, the user's asks and picks): add, place, rename, del, tick
    /// all (<c>☐  none</c> once every line is ticked, <paramref name="anyOpen"/> false: one key, <c>a</c>, either way), clear, the stores.
    /// + and - change the quantity with no button drawn (<see cref="MenuButton.Hidden"/>, last: the title row ran out of room). Pinned.
    /// </summary>
    public static IReadOnlyList<MenuButton> ButtonsFor(bool anyOpen) =>
    [
        new(ShoppingText.AddButton, ShoppingText.AddKey),
        new(ShoppingText.PlaceButton, ShoppingText.PlaceKey),
        new(ShoppingText.RenameButton, ShoppingText.RenameKey),
        new(ShoppingText.RemoveButton, ShoppingText.RemoveKey),
        new(anyOpen ? ShoppingText.TickAllButton : ShoppingText.UntickAllButton, ShoppingText.TickAllKey),
        new(ShoppingText.ClearButton, ShoppingText.ClearKey),
        new(ShoppingText.StoreButton, ShoppingText.StoreKey),
        new(ShoppingText.MoreButton, ShoppingText.MoreKey) { Hidden = true },
        new(ShoppingText.LessButton, ShoppingText.LessKey) { Hidden = true },
    ];

    private const int Add = 0;
    private const int Place = 1;
    private const int Rename = 2;
    private const int Remove = 3;
    private const int TickAll = 4;
    private const int ClearTicked = 5;
    private const int Store = 6;
    private const int More = 7;
    private const int Less = 8;

    /// <summary>The add page's button: forget a line bought before. Pinned.</summary>
    public static readonly IReadOnlyList<MenuButton> AddButtons = [new(ShoppingText.ForgetButton, ShoppingText.ForgetKey)];

    private readonly ShoppingList _list;
    private readonly MenuPane _pane;
    private readonly Func<CancellationToken, Task<bool>>? _manageStores;
    private readonly bool _closeWhenDone;
    private readonly InputLine? _input;
    private readonly TimeZoneInfo _zone;
    private readonly TimeProvider? _time;

    /// <summary>How often an open list looks for a change made elsewhere (2026-10-15).</summary>
    public static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(1);

    /// <param name="list">The list's operations over the loaded profile's store.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="manageStores">The stores' manager the store button opens (2026-10-11, <see cref="SettingsMenu.ManageShoppingStoresAsync"/>); null: a plain picker of the stores.</param>
    /// <param name="closeWhenDone">Whether ESC closes the pane (the <c>/shopping</c> pane's own); false under a host that drew it, the <c>Shopping list</c> row of <c>/tools</c> (2026-10-11), whose page comes back.</param>
    /// <param name="input">The input row a rename or a new item is typed on (2026-10-11); null: they say so.</param>
    /// <param name="zone">The zone the recently bought's dates are shown in.</param>
    /// <param name="time">
    /// The clock the open list is re-read on (2026-10-15): once a second it compares the store and the list with what it drew, and on a change from
    /// elsewhere (the Web UI's page, the model's tools) draws again where it stood. Null watches nothing.
    /// </param>
    public ShoppingMenu(ShoppingList list, MenuPane pane, Func<CancellationToken, Task<bool>>? manageStores = null, bool closeWhenDone = true, InputLine? input = null, TimeZoneInfo? zone = null, TimeProvider? time = null)
    {
        _time = time;
        _input = input;
        _manageStores = manageStores;
        _closeWhenDone = closeWhenDone;
        _zone = zone ?? TimeZoneInfo.Local;
        _list = list ?? throw new ArgumentNullException(nameof(list));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
    }

    /// <summary>What the open list shows, as a key (2026-10-15): the active store and every line's state; another means it changed elsewhere.</summary>
    private string ListKey()
    {
        var store = _list.Store;
        return store.ActiveStore()?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
            + string.Join(",", store.Items().Select(i => i.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + (i.Checked ? "+" : "-") + i.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture) + i.Name));
    }

    /// <summary>
    /// Re-reads the list every <see cref="WatchEvery"/> while a pick waits, and ends it (the page's <c>stale</c> token) when the key differs from
    /// <paramref name="drawn"/>; nothing without a clock. Ends quietly with the pick.
    /// </summary>
    private async Task Watch(string drawn, CancellationTokenSource stale, CancellationToken cancellationToken)
    {
        if (_time is null)
        {
            return;
        }

        try
        {
            using var both = CancellationTokenSource.CreateLinkedTokenSource(stale.Token, cancellationToken);
            while (true)
            {
                await Task.Delay(WatchEvery, _time, both.Token).ConfigureAwait(false);
                if (!string.Equals(ListKey(), drawn, StringComparison.Ordinal))
                {
                    await stale.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The pick ended, or the change was found.
        }
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

    /// <summary>Shows the list until ESC.</summary>
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
                    // The empty list: one dim row and the same bar, so add (and the stores) work on it (2026-10-11).
                    rows = [Theme.DimMarkup(ShoppingText.EmptyList)];
                    items = [null];
                }

                int cursor = on is { } id ? items.FindIndex(i => i?.Id == id) : -1;
                cursor = cursor >= 0 ? cursor : Math.Max(0, items.FindIndex(i => i is not null));
                int guesses = view.Groups.Sum(g => g.Items.Count(p => p.Place.IsGuess));
                var page = new MenuPage(ShoppingText.PaneLabel(shop), rows, ShoppingText.PaneKeys)
                {
                    Buttons = ButtonsFor(view.Groups.Count > 0),
                    Headings = headings,
                    SpaceToggles = true,
                    Caption = ShoppingText.PaneCaption(view.Groups.Sum(g => g.Items.Count), view.Ticked.Count, guesses),
                };
                MenuPick? picked;
                using (var stale = new CancellationTokenSource())
                {
                    var watching = Watch(ListKey(), stale, cancellationToken);
                    try
                    {
                        picked = await _pane.PickAsync(page, cursor, cancellationToken, stale: stale.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        await stale.CancelAsync().ConfigureAwait(false);
                        await watching.ConfigureAwait(false);
                    }
                }

                if (picked is not { } pick)
                {
                    return;
                }

                var item = pick.Row >= 0 && pick.Row < items.Count ? items[pick.Row] : null;
                if (pick.Refresh)
                {
                    on = item?.Id ?? on;   // changed elsewhere: drawn again where it stood
                    continue;
                }

                on = item?.Id ?? on;
                switch (pick.Button)
                {
                    case Add:
                        await AddAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case Place when item is not null:
                        await PlaceAsync(shop, item, cancellationToken).ConfigureAwait(false);
                        break;
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
                        if (await TypeAsync(page, pick.Row, item.Name, cancellationToken).ConfigureAwait(false) is { } renamed && renamed != item.Name)
                        {
                            Say(_list.RenameItem(item, renamed));
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

    /// <summary>
    /// The add page (2026-10-11, the user's ask: re-add what is bought every week): <see cref="ShoppingText.AddItemRow"/> types new lines
    /// (commas between, a quantity leading or trailing), then what was bought before and is not on the list now, the most often first,
    /// filtered by typing; Enter adds the line at its last quantity and the page stays, ESC back. The forget button drops a bought line.
    /// <c>/shopping add</c> with nothing after opens it straight. The pane stays open for the caller.
    /// </summary>
    public async Task AddAsync(CancellationToken cancellationToken)
    {
        string filter = "";
        int cursor = 0;
        while (true)
        {
            var shop = _list.StoreFor(null, out _);
            var bought = _list.Bought();
            var shown = Enumerable.Range(0, bought.Count).Where(i => filter.Length == 0 || bought[i].Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            List<string> rows = [Markup.Escape(ShoppingText.AddItemRow), .. shown.Select(i => Markup.Escape(ShoppingText.BoughtRow(bought[i], _zone)))];
            if (shown.Count == 0 && filter.Length > 0)
            {
                rows.Add(MenuFilter.NoMatchRow(filter));
            }

            var page = new MenuPage(ShoppingText.AddTitle, rows, MenuFilter.HintBeforeEsc(ShoppingText.AddKeys, filter, letteredButtons: true))
            {
                Buttons = AddButtons,
                Filter = filter,
                Caption = bought.Count == 0 ? ShoppingText.NothingBoughtYet : ShoppingText.BoughtCaption(bought.Count),
            };
            if (await _pane.PickAsync(page, Math.Min(cursor, rows.Count - 1), cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return;
            }

            if (pick.Filter is { } typed)
            {
                filter = typed;
                cursor = shown.Count > 0 || typed.Length == 0 ? 1 : 0;
                continue;
            }

            cursor = pick.Row;
            var line = pick.Row > 0 && pick.Row - 1 < shown.Count ? bought[shown[pick.Row - 1]] : null;
            if (pick.Button == 0)
            {
                if (line is not null)
                {
                    Say(_list.ForgetBought(line));
                }

                continue;
            }

            if (line is not null)
            {
                Say(_list.Add(line.Name, line.Quantity, shop) is { } one ? ShoppingText.AddedForUser([one], shop) : "Error: " + ShoppingText.WriteFailed);
                continue;
            }

            if (pick.Row == 0 && await TypeAsync(page, 0, filter, cancellationToken).ConfigureAwait(false) is { } text)
            {
                var added = new List<ShopAdded>();
                foreach (string entry in ShoppingCommand.Items(text))
                {
                    var (name, quantity) = ShoppingList.ParseLine(entry);
                    if (_list.Add(name, quantity, shop) is { } a)
                    {
                        added.Add(a);
                    }
                }

                Say(added.Count > 0 ? ShoppingText.AddedForUser(added, shop) : "Error: " + ShoppingText.WriteFailed);
                filter = "";
            }
        }
    }

    /// <summary>
    /// The place picker (2026-10-11, the user's ask: a misplaced line fixed without the model): the active store's locations in walk order,
    /// then its sections alone, then <see cref="ShoppingText.ForgetPlaceRow"/> when an item fact of the line's name exists; the cursor on
    /// where the line is now, typing filters. The pick records an item fact of the line's name (the user's), so it lands there now and every
    /// time (<see cref="ShoppingList.PlaceItem"/>).
    /// </summary>
    private async Task PlaceAsync(ShopStore? shop, ShopItem item, CancellationToken cancellationToken)
    {
        if (shop is null)
        {
            Say(ShoppingText.NoStore);
            return;
        }

        var store = _list.Store;
        var locations = store.Locations(shop.Id);
        var sections = store.Sections(shop.Id);
        if (locations.Count == 0 && sections.Count == 0)
        {
            _pane.Notice(ShoppingText.NoLayoutYet(shop));
            return;
        }

        string key = ShoppingPlacer.Key(item.Name);
        var fact = store.Placements(shop.Id).FirstOrDefault(p => p.Kind == ShoppingKinds.Item && ShoppingPlacer.Key(p.Term) == key);
        var now = _list.PlaceOf(item.Name, shop);
        var choices = new List<(string Label, ShopLocation? Location, ShopSection? Section, bool Forget)>();
        var places = ShoppingList.PlaceChoices(locations, sections);
        choices.AddRange(places.Select(c => (c.Label, c.Location, c.Section, false)));
        if (fact is not null)
        {
            choices.Add((ShoppingText.ForgetPlaceRow, null, null, true));
        }

        string filter = "";
        int cursor = Math.Max(0, ShoppingList.PlaceChoiceIndex(places, sections, now.Location?.Id, now.Section?.Id));
        while (true)
        {
            var shown = Enumerable.Range(0, choices.Count).Where(i => filter.Length == 0 || choices[i].Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            List<string> rows = shown.Count > 0 ? shown.Select(i => Markup.Escape(choices[i].Label)).ToList() : [MenuFilter.NoMatchRow(filter)];
            var page = new MenuPage(ShoppingText.PlaceTitle(item), rows, MenuFilter.Hint(SettingsMenu.PickKeys, filter)) { Filter = filter };
            if (await _pane.PickAsync(page, Math.Max(0, shown.IndexOf(cursor)), cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return;
            }

            if (pick.Filter is { } typed)
            {
                filter = typed;
                cursor = -1;
                continue;
            }

            if (pick.Row < 0 || pick.Row >= shown.Count)
            {
                continue;
            }

            var choice = choices[shown[pick.Row]];
            Say(choice.Forget ? _list.RemoveFact(shop, fact!) : _list.PlaceItem(shop, item, choice.Location, choice.Section));
            return;
        }
    }

    /// <summary>Text typed on the input row under the list (<paramref name="initial"/> to start from); null on ESC or a blank line, or with no input row (said so).</summary>
    private async Task<string?> TypeAsync(MenuPage page, int row, string initial, CancellationToken cancellationToken)
    {
        if (_input is null)
        {
            _pane.Error(ShoppingText.RenameNeedsInput);
            return null;
        }

        var typed = await _pane.EditAsync(page with { Hint = SettingsMenu.EditKeys }, row, _input, initial, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        return typed is InputResult.Submitted { Text: var text } && text.Trim().Length > 0 ? text.Trim() : null;
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
