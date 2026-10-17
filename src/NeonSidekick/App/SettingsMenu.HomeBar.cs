using NeonSidekick.Emojis;
using NeonSidekick.HomeAssistant;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

// ── The home bar's picker: /settings › Client › Chat › Home bar and /homebar edit (2026-10-12) ──

internal sealed partial class SettingsMenu
{
    /// <summary>
    /// The registries and the states for the home bar's picker (2026-10-12, the screen's live feed, <see cref="IHaLive.CatalogAsync"/>);
    /// null (tests, headless) and the picker says Home Assistant is not set up.
    /// </summary>
    public Func<CancellationToken, Task<(HaCatalog? Catalog, string? Error)>>? HomeCatalog { get; set; }

    /// <summary>The domains each picker tab lists, after On the bar and Rooms; Other takes every domain none of these names.</summary>
    internal static readonly (string Title, string[] Domains)[] HomeDomainTabs =
    [
        (HomeBarText.LightsTab, ["light"]),
        (HomeBarText.SwitchesTab, ["switch", "input_boolean", "fan"]),
        (HomeBarText.SensorsTab, ["sensor", "binary_sensor"]),
        (HomeBarText.ClimateTab, ["climate"]),
        (HomeBarText.MediaTab, ["media_player"]),
        (HomeBarText.ScenesTab, ["scene", "script"]),
        (HomeBarText.SafetyTab, ["lock", "cover", "alarm_control_panel"]),
        (HomeBarText.ZonesTab, ["zone"]),
        (HomeBarText.OtherTab, []),
    ];

    /// <summary>The domains the picker never lists: Home Assistant's own machinery, nothing a chip could show or switch.</summary>
    internal static readonly string[] HomeHiddenDomains = ["persistent_notification", "sun", "conversation", "tts", "stt", "wake_word", "event", "assist_satellite", "tag", "image", "camera", "update", "todo", "calendar", "weather", "ai_task"];

    // One tab as built: its strip title and rows, and the reference behind each row (null for a heading, a gap or a note).
    private sealed record HomeTab(MenuTab Tab, List<string?> Refs);

    // The bar tab's last row (2026-10-13, the user's pick: "+ Add divider" under the items): no reference, this word instead.
    private const string AddDividerRef = "+divider";

    /// <summary>
    /// The home bar's picker (2026-10-12, the user's ask: choose between rooms, lights, devices, and change the order): On the bar
    /// first — the items in the bar's order, numbered, the move keys (<see cref="MenuPage.Moves"/>: Shift+↑↓ a place,
    /// Shift+Home/End to the top or bottom, Delete out) and Enter's actions page for those whose terminal eats the keys — then
    /// Rooms (the areas by floor) and one tab per kind of device (<see cref="HomeDomainTabs"/>; Zones since later that day, the user's ask), each in sections by area, where
    /// Space or Enter puts an item on the bar's end or takes it off, and typing filters by name, id, area or label. Every change
    /// saves at once and the bar under the pane follows. Read from <see cref="HomeCatalog"/> once a visit. <paramref name="own"/>
    /// (<c>/homebar edit</c>): the pane is this picker's and closes as it ends. True when anything changed.
    /// </summary>
    public async Task<bool> EditHomeBarAsync(bool own, CancellationToken cancellationToken)
    {
        if (!_pane.Enabled)
        {
            Sink.Notice(HomeBarText.Headless);
            return false;
        }

        // Without Home Assistant, or with its registries out of reach, the picker still opens (later on 2026-10-12, the user's call):
        // the bar's tab and Shopping, the 🛒 chip being the app's own list.
        HaCatalog? catalog = null;
        if (HomeCatalog is not null && HaSession.Configured(EffectiveNow()))
        {
            (catalog, string? error) = await HomeCatalog(cancellationToken).ConfigureAwait(false);
            if (catalog is null)
            {
                Sink.Warning(HomeBarText.NoCatalog(error ?? HaText.NotConfigured));
            }
        }

        bool home = catalog is not null;
        catalog ??= new HaCatalog([], [], [], [], [], []);

        bool changed = false;
        int tab = 0;
        int cursor = 0;
        string filter = "";
        try
        {
            while (true)
            {
                var saved = _settings.Current;
                var sides = HomeBarItems.Sides(saved.HomeBarItems, saved.HomeBarLeft, saved.HomeBarRight);
                var items = sides.All;
                var built = HomePickerTabs(catalog, sides, filter, saved.HomeBarAliases, saved.HomeBarGlyphs, home);
                var page = MenuPage.Tabbed(Crumb(FieldName(SettingsField.ShowHomeBar)), built.Select(b => b.Tab).ToList(), tab, built[Math.Clamp(tab, 0, built.Count - 1)].Tab.Hint ?? "");
                var picked = await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
                if (picked is not { } pick)
                {
                    if (!changed)
                    {
                        Sink.Notice(UnchangedNotice);
                    }

                    return changed;
                }

                tab = pick.Tab;
                cursor = pick.Row;
                if (pick.Filter is { } typed)
                {
                    filter = typed;
                    cursor = 0;
                    continue;
                }

                if (tab == 0)
                {
                    string? row = pick.Row >= 0 && pick.Row < built[0].Refs.Count ? built[0].Refs[pick.Row] : null;
                    if (row == AddDividerRef)
                    {
                        // + Add divider (2026-10-13, the user's ask): a new one at the center's end (the bar's until the groups, later
                        // that day), the cursor on it to move it.
                        if (!pick.Remove && pick.Move == 0 && pick.Side == 0)
                        {
                            var (added, landed) = HomeBarItems.AddDivider(sides);
                            SaveHomeSides(added);
                            cursor = SidedRowOf(added, landed);
                            changed = true;
                        }

                        continue;
                    }

                    if (row is null || items.IndexOf(row) is not (>= 0 and int index))
                    {
                        continue;   // a heading, or the empty bar's line
                    }

                    int move = pick.Remove ? 0 : pick.Move;
                    int side = pick.Remove ? 0 : pick.Side;
                    int? pin = null;
                    if (!pick.Remove && pick.Move == 0 && pick.Side == 0)
                    {
                        // Enter: the actions page, for a terminal that keeps the move and pin keys to itself, the rename and the emoji.
                        if (await HomeItemActionAsync(row, catalog, GroupOf(sides, index), cancellationToken).ConfigureAwait(false) is not { } chosen)
                        {
                            continue;
                        }

                        switch (chosen.Do)
                        {
                            case HomeItemDo.Rename:
                                changed |= await RenameHomeItemAsync(row, catalog, cancellationToken).ConfigureAwait(false);
                                continue;
                            case HomeItemDo.Emoji:
                                changed |= await ChangeHomeItemEmojiAsync(row, catalog, cancellationToken).ConfigureAwait(false);
                                continue;
                            case HomeItemDo.DefaultEmoji:
                                changed |= SetHomeItemGlyph(row, catalog, null);
                                continue;
                            case HomeItemDo.DefaultName:
                                Apply(SettingsField.ShowHomeBar, d => d.HomeBarAliases = HomeBarItems.Rename(d.HomeBarAliases, row, null, HomeBarItems.NameOf(row, catalog)));
                                changed = true;
                                continue;
                            case HomeItemDo.Pin:
                                pin = chosen.Move;
                                break;
                        }

                        move = chosen.Move;
                    }

                    if (pin is null && side == 0 && move == 0)
                    {
                        // Off the bar, its side with it (later on 2026-10-13): added again, it lands in the center.
                        var without = HomeBarItems.Without(sides, row);
                        SaveHomeSides(without);
                        cursor = without.All.Count == 0 ? 0 : SidedRowOf(without, Math.Min(index, without.All.Count - 1));
                        changed = true;
                        continue;
                    }

                    // Shift+←/→ one group over; a Pin row to its group, a group at a time; else a move within the group, crossing at its edge.
                    var (moved, at) = (sides, index);
                    if (pin is { } group)
                    {
                        for (int step = GroupOf(sides, index); step != group && at >= 0; step += Math.Sign(group - step))
                        {
                            (moved, at) = ItemOrder.ToSide(moved, at, toRight: group > step);
                        }
                    }
                    else
                    {
                        (moved, at) = side != 0 ? ItemOrder.ToSide(sides, index, toRight: side > 0) : ItemOrder.MoveInGroups(sides, index, move);
                    }

                    if (!moved.SameAs(sides))
                    {
                        SaveHomeSides(moved);
                        changed = true;
                    }

                    cursor = SidedRowOf(moved, at);
                    continue;
                }

                if (pick.Move != 0 || pick.Remove || pick.Side != 0 || tab >= built.Count || pick.Row >= built[tab].Refs.Count || built[tab].Refs[pick.Row] is not { } reference)
                {
                    continue;
                }

                SaveHomeSides(HomeBarItems.Flip(sides, reference));
                changed = true;
            }
        }
        finally
        {
            if (own)
            {
                _pane.Close();
            }
        }
    }

    // The bar's groups and order saved together (later on 2026-10-13): the whole order, then each side.
    private void SaveHomeSides(ItemOrder.Sided sides) =>
        Apply(SettingsField.ShowHomeBar, d => (d.HomeBarItems, d.HomeBarLeft, d.HomeBarRight) = HomeBarItems.SaveSides(sides));

    // The group of the item at index of sides.All: 0 left, 1 center, 2 right.
    private static int GroupOf(ItemOrder.Sided sides, int index) =>
        index < sides.Left.Count ? 0 : index < sides.Left.Count + (sides.Center?.Count ?? 0) ? 1 : 2;

    // What the bar item's actions page chose beside a move: the rename, the emoji grid, the emoji taken off (later on 2026-10-12), a
    // pin (later on 2026-10-13: its Move the group, 0 left, 1 center, 2 right).
    private enum HomeItemDo
    {
        Move,
        Rename,
        Emoji,
        DefaultEmoji,
        DefaultName,
        Pin,
    }

    // The bar item's actions page: a move (Top/−1/+1/Bottom), the two groups it is not in (later on 2026-10-13; it is in group), 0 for
    // remove, the rename (its alias dim beside it), the emoji (its own dim beside it; later on 2026-10-12, the user's ask) and, while it
    // has one, the default emoji again; null on ESC.
    private async Task<(int Move, HomeItemDo Do)?> HomeItemActionAsync(string reference, HaCatalog catalog, int group, CancellationToken cancellationToken)
    {
        string? alias = HomeBarItems.AliasOf(_settings.Current.HomeBarAliases, reference);
        string? glyph = HomeBarItems.GlyphOf(_settings.Current.HomeBarGlyphs, reference);
        var rows = new List<(string Row, (int, HomeItemDo) Action)>
        {
            (Markup.Escape(HomeBarText.MoveTop), (HomeBarItems.Top, HomeItemDo.Move)),
            (Markup.Escape(HomeBarText.MoveUp), (-1, HomeItemDo.Move)),
            (Markup.Escape(HomeBarText.MoveDown), (1, HomeItemDo.Move)),
            (Markup.Escape(HomeBarText.MoveBottom), (HomeBarItems.Bottom, HomeItemDo.Move)),
        };
        foreach (var (pin, label) in new[] { (0, HomeBarText.PinLeftItem), (1, HomeBarText.PinCenterItem), (2, HomeBarText.PinRightItem) })
        {
            if (pin != group)
            {
                rows.Add((Markup.Escape(label), (pin, HomeItemDo.Pin)));
            }
        }
        if (!HomeBarItems.IsNameless(reference))
        {
            // The 🛒 chip shows no name to change (later on 2026-10-12, the user's call); its emoji still can be.
            rows.Add((Markup.Escape(HomeBarText.RenameItem) + (alias is not null ? Theme.DimMarkup("  " + (alias.Length == 0 ? HomeBarText.EmojiAlone : alias)) : ""), (0, HomeItemDo.Rename)));
            if (alias is not null)
            {
                // The way back from an alias, now that a blank is the emoji alone (later on 2026-10-12).
                rows.Add((Markup.Escape(HomeBarText.DefaultNameItem) + Theme.DimMarkup("  " + HomeBarItems.NameOf(reference, catalog)), (0, HomeItemDo.DefaultName)));
            }
        }

        if (!HomeBarItems.IsDivider(reference))
        {
            // A divider has no emoji either (2026-10-13): its page is the moves and Remove.
            rows.Add((Markup.Escape(HomeBarText.EmojiItem) + (glyph is not null ? Theme.DimMarkup("  " + glyph) : ""), (0, HomeItemDo.Emoji)));
            if (glyph is not null)
            {
                rows.Add((Markup.Escape(HomeBarText.DefaultEmojiItem) + Theme.DimMarkup("  " + HomeChips.DefaultGlyph(reference, catalog)), (0, HomeItemDo.DefaultEmoji)));
            }
        }

        rows.Add((Markup.Escape(HomeBarText.RemoveItem), (0, HomeItemDo.Move)));
        var page = new MenuPage(Crumb(FieldName(SettingsField.ShowHomeBar)) + " › " + HomeBarItems.NameOf(reference, catalog), rows.Select(r => r.Row).ToList(), PickKeys);
        return await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is { } pick && pick.Row >= 0 && pick.Row < rows.Count ? rows[pick.Row].Action : null;
    }

    // Change emoji (later on 2026-10-12, the user's ask): the avatar's emoji grid for the item, opening on its emoji's group, 🚫
    // (while it has one) its own glyph again; ESC keeps it. True when it changed.
    private async Task<bool> ChangeHomeItemEmojiAsync(string reference, HaCatalog catalog, CancellationToken cancellationToken)
    {
        string name = HomeBarItems.NameOf(reference, catalog);
        string current = HomeBarItems.GlyphOf(_settings.Current.HomeBarGlyphs, reference) ?? "";
        var context = new AvatarContext(EmojiCatalog.Shipped, EmojiPrefs.Load(_settings.ProfileDirectory), current, _settings.ProfileName,
            new Dictionary<string, List<string>>(StringComparer.Ordinal), [], HomeDefault: HomeChips.DefaultGlyph(reference, catalog));
        string title = Crumb(FieldName(SettingsField.ShowHomeBar)) + " › " + name + " › " + HomeBarText.EmojiTitle;
        return await ChooseEmojiAsync(context, title, "", cancellationToken).ConfigureAwait(false) is { } chosen && SetHomeItemGlyph(reference, catalog, chosen);
    }

    // The item's emoji saved (null or "" for its own glyph again) through Apply, so the row's notice tells; true when it changed.
    private bool SetHomeItemGlyph(string reference, HaCatalog catalog, string? emoji)
    {
        var glyphs = _settings.Current.HomeBarGlyphs;
        var next = HomeBarItems.SetGlyph(glyphs, reference, emoji, HomeChips.DefaultGlyph(reference, catalog));
        if (HomeBarItems.GlyphOf(next, reference) == HomeBarItems.GlyphOf(glyphs, reference))
        {
            return false;
        }

        Apply(SettingsField.ShowHomeBar, d => d.HomeBarGlyphs = next);
        return true;
    }

    // Rename on the bar (2026-10-12, the user's ask): the input row under the crumb, from the alias or else the name; the name takes
    // the alias off, blank is the emoji alone (later that day; Default name takes it off now), ESC keeps it. True when it changed.
    private async Task<bool> RenameHomeItemAsync(string reference, HaCatalog catalog, CancellationToken cancellationToken)
    {
        string name = HomeBarItems.NameOf(reference, catalog);
        var aliases = _settings.Current.HomeBarAliases;
        string? alias = HomeBarItems.AliasOf(aliases, reference);
        var page = new MenuPage(Crumb(FieldName(SettingsField.ShowHomeBar)) + " › " + name + " › " + HomeBarText.RenameTitle, [], HomeBarText.RenameKeys);
        if (await _pane.EditAsync(page, 0, _input, alias ?? name, allowEmpty: true, cancellationToken).ConfigureAwait(false) is not InputResult.Submitted submitted)
        {
            return false;
        }

        var next = HomeBarItems.Rename(aliases, reference, submitted.Text, name);
        if (HomeBarItems.AliasOf(next, reference) == alias)
        {
            return false;
        }

        Apply(SettingsField.ShowHomeBar, d => d.HomeBarAliases = next);
        return true;
    }

    /// <summary>
    /// The picker's tabs over <paramref name="catalog"/> for the bar's items in their groups (<paramref name="sides"/>), the kinds' tabs under
    /// <paramref name="filter"/>; an item with an alias (<paramref name="aliases"/>) shows it, quoted on the kinds' and rooms' tabs
    /// and matched by the filter; one with an emoji of its own (<paramref name="glyphs"/>) shows it on the bar's tab. Shopping last
    /// (later on 2026-10-12), and the bar's tab and it alone without <paramref name="home"/> (no Home Assistant). Pure over its data.
    /// </summary>
    private static List<HomeTab> HomePickerTabs(HaCatalog catalog, ItemOrder.Sided sides, string filter, IReadOnlyDictionary<string, string>? aliases, IReadOnlyDictionary<string, string>? glyphs, bool home = true)
    {
        var items = sides.All;
        if (!home)
        {
            return [BarTab(catalog, sides, aliases, glyphs), ShoppingTab(items, filter, glyphs)];
        }

        var tabs = new List<HomeTab> { BarTab(catalog, sides, aliases, glyphs), RoomsTab(catalog, items, filter, aliases) };
        var named = HomeDomainTabs.SelectMany(t => t.Domains).ToHashSet(StringComparer.Ordinal);
        foreach (var (title, domains) in HomeDomainTabs)
        {
            var entities = catalog.States
                .Where(e => !catalog.Disabled(e.Id) && catalog.Entry(e.Id) is not { Hidden: true })
                .Where(e => domains.Length > 0 ? domains.Contains(e.Domain, StringComparer.Ordinal) : !named.Contains(e.Domain) && !HomeHiddenDomains.Contains(e.Domain, StringComparer.Ordinal))
                .ToList();
            tabs.Add(DomainTab(title, catalog, items, entities, filter, aliases));
        }

        tabs.Add(ShoppingTab(items, filter, glyphs));
        return tabs;
    }

    // The app's own items (later on 2026-10-12): the shopping list's 🛒, its emoji as the bar draws it.
    private static HomeTab ShoppingTab(IReadOnlyList<string> items, string filter, IReadOnlyDictionary<string, string>? glyphs)
    {
        string glyph = HomeBarItems.GlyphOf(glyphs, HomeBarItems.Shopping) ?? HomeChips.ShoppingGlyph;
        var rows = MenuFilter.Matches(filter, HomeBarText.ShoppingName, HomeBarItems.Shopping + " cart")
            ? new List<(string Ref, string Name, string Note)> { (HomeBarItems.Shopping, glyph + " " + HomeBarText.ShoppingName, HomeBarText.ShoppingRowNote) }
            : [];
        return Sectioned(HomeBarText.ShoppingTab, [("", null, rows)], items, filter);
    }

    // The bar's tab (later on 2026-10-13, the user's ask: pinned left, center or right): the groups as the sided checklists draw them — a
    // heading each with its count (SidedRows' rows, so SidedRowOf finds an item's row), the items numbered across all three — then
    // + Add divider; with nothing on the bar, its one dim line instead of the headings.
    private static HomeTab BarTab(HaCatalog catalog, ItemOrder.Sided sides, IReadOnlyDictionary<string, string>? aliases, IReadOnlyDictionary<string, string>? glyphs)
    {
        var rows = new List<string>();
        var refs = new List<string?>();
        var headings = new HashSet<int>();
        int number = 1;
        foreach (var (title, group, empty) in sides.All.Count > 0 ? SidedHeadings(sides) : [])
        {
            headings.Add(rows.Count);
            rows.Add(SectionRule.Markup(title, group.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), group.Count == 0 ? empty : null));
            refs.Add(null);
            foreach (string item in group)
            {
                rows.Add(BarRow(catalog, item, number++, aliases, glyphs));
                refs.Add(item);
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(Theme.DimMarkup(HomeBarText.EmptyBar));
            refs.Add(null);
        }

        rows.Add(Markup.Escape(HomeBarText.AddDividerRow));
        refs.Add(AddDividerRef);
        return new HomeTab(new MenuTab(HomeBarText.BarTab, rows) { Moves = true, Sides = true, Headings = headings.Count > 0 ? headings : null, Hint = HomeBarText.BarHint }, refs);
    }

    // One item's row on the bar's tab: its number, its glyph and its name as the chip has them, and where it is, dim.
    private static string BarRow(HaCatalog catalog, string item, int number, IReadOnlyDictionary<string, string>? aliases, IReadOnlyDictionary<string, string>? glyphs)
    {
        string at = number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(2) + ". ";
        HomeBarItems.TryParse(item, out var kind, out string id);
        if (kind == HomeItemKind.Divider)
        {
            return Markup.Escape($"{at}{ScreenPane.DividerGlyph} {HomeBarText.DividerName}") + Theme.DimMarkup("  " + HomeBarText.DividerWhere);
        }

        // The entity's own glyph, as its chip has (2026-10-12: a TV's 📺 is in its device class, which a bare id lacks), or the
        // emoji of the user's own (later that day), the own one dim at the end.
        string own = HomeChips.DefaultGlyph(item, catalog);
        string? custom = HomeBarItems.GlyphOf(glyphs, item);
        string where = kind == HomeItemKind.Area ? HomeBarText.RoomsTab.ToLowerInvariant() : kind == HomeItemKind.Shopping ? HomeBarText.ShoppingWhere : catalog.AreaOf(id) is { } area ? catalog.Area(area)?.Name ?? id : id;
        // An alias first, as the bar shows it, and the item's own name dim beside it.
        string name = HomeBarItems.NameOf(item, catalog);
        string? alias = HomeBarItems.AliasOf(aliases, item);
        string note = (alias is null ? where : (alias.Length == 0 ? HomeBarText.EmojiAlone + " · " : "") + name + " · " + where) + (custom is null ? "" : " · " + HomeBarText.DefaultGlyphNote(own));
        return Markup.Escape($"{at}{custom ?? own} {alias ?? name}") + Theme.DimMarkup("  " + note);
    }

    private static HomeTab RoomsTab(HaCatalog catalog, IReadOnlyList<string> items, string filter, IReadOnlyDictionary<string, string>? aliases)
    {
        var sections = new List<(string Heading, string? Note, List<(string Ref, string Name, string Note)> Rows)>();
        foreach (var group in catalog.Areas.GroupBy(a => a.FloorId))
        {
            var rows = group
                .Select(a => (Area: a, Alias: HomeBarItems.AliasOf(aliases, HomeBarItems.Area(a.Id))))
                .Where(a => MenuFilter.Matches(filter, a.Area.Name, a.Area.Id + " " + a.Alias))
                .Select(a => (HomeBarItems.Area(a.Area.Id), a.Area.Name, AliasNote(a.Alias, HomeBarText.Lights(catalog.MembersOf(a.Area.Id, HomeBarItems.AreaDomains).Count))))
                .ToList();
            if (rows.Count > 0)
            {
                sections.Add((catalog.Floor(group.Key)?.Name ?? (catalog.Floors.Count > 0 ? HomeBarText.NoArea : ""), null, rows));
            }
        }

        return Sectioned(HomeBarText.RoomsTab, sections, items, filter);
    }

    private static HomeTab DomainTab(string title, HaCatalog catalog, IReadOnlyList<string> items, IReadOnlyList<HaEntity> entities, string filter, IReadOnlyDictionary<string, string>? aliases)
    {
        var byArea = new Dictionary<string, List<(string Ref, string Name, string Note)>>(StringComparer.Ordinal);
        var noArea = new List<(string Ref, string Name, string Note)>();
        var extra = new List<(string Ref, string Name, string Note)>();
        foreach (var entity in entities.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            string? areaId = catalog.AreaOf(entity.Id);
            string areaName = areaId is null ? "" : catalog.Area(areaId)?.Name ?? areaId;
            string labels = string.Join(" ", catalog.LabelsOf(entity.Id));
            string? alias = HomeBarItems.AliasOf(aliases, HomeBarItems.Entity(entity.Id));
            if (!MenuFilter.Matches(filter, entity.Name, entity.Id + " " + areaName + " " + labels + " " + alias))
            {
                continue;
            }

            var row = (HomeBarItems.Entity(entity.Id), entity.Name, AliasNote(alias, entity.Id));
            if (catalog.Entry(entity.Id)?.Category is not null)
            {
                extra.Add(row);
            }
            else if (areaId is null)
            {
                noArea.Add(row);
            }
            else
            {
                (byArea.TryGetValue(areaId, out var list) ? list : byArea[areaId] = []).Add(row);
            }
        }

        var sections = new List<(string Heading, string? Note, List<(string Ref, string Name, string Note)> Rows)>();
        foreach (var area in catalog.Areas)
        {
            if (byArea.TryGetValue(area.Id, out var rows))
            {
                sections.Add((area.Name, catalog.Floor(area.FloorId)?.Name, rows));
            }
        }

        if (noArea.Count > 0)
        {
            sections.Add((HomeBarText.NoArea, null, noArea));
        }

        if (extra.Count > 0)
        {
            sections.Add((HomeBarText.Extra, null, extra));
        }

        return Sectioned(title, sections, items, filter);
    }

    // A row's dim note with the item's alias quoted ahead of it, when it has one.
    private static string AliasNote(string? alias, string note) => alias is null ? note : HomeBarText.AliasNote(alias, note);

    // A tab of check rows in sections (a heading each, a gap between), filtering by filter; one dim line when nothing is left.
    private static HomeTab Sectioned(string title, List<(string Heading, string? Note, List<(string Ref, string Name, string Note)> Rows)> sections, IReadOnlyList<string> items, string filter)
    {
        var rows = new List<string>();
        var refs = new List<string?>();
        var headings = new HashSet<int>();
        int total = 0;
        foreach (var (heading, note, entries) in sections)
        {
            if (heading.Length > 0)
            {
                if (rows.Count > 0)
                {
                    rows.Add("");
                    refs.Add(null);
                }

                headings.Add(rows.Count);
                rows.Add(SectionRule.Markup(heading, null, note));
                refs.Add(null);
            }

            foreach (var (reference, name, detail) in entries)
            {
                bool on = HomeBarItems.Contains(items, reference);
                rows.Add(Markup.Escape((on ? "[x] " : "[ ] ") + name) + Theme.DimMarkup("  " + detail));
                refs.Add(reference);
                total++;
            }
        }

        if (total == 0)
        {
            rows.Clear();
            refs.Clear();
            headings.Clear();
            rows.Add(filter.Length > 0 ? MenuFilter.NoMatchRow(filter) : Theme.DimMarkup(HomeBarText.NothingHere));
            refs.Add(null);
        }

        var tab = new MenuTab(title, rows)
        {
            Headings = headings.Count > 0 ? headings : null,
            Filter = filter,
            SpaceToggles = true,
            Hint = MenuFilter.Hint(HomeBarText.PickHint + " · " + MenuFilter.TypeAndCloseKeys, filter),
        };
        return new HomeTab(tab, refs);
    }
}
