using NeonSidekick.HomeAssistant;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

// ── The home bar's picker: /settings › Client › Chat › Show home bar and /homebar edit (2026-10-12) ──

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
        (HomeBarText.OtherTab, []),
    ];

    /// <summary>The domains the picker never lists: Home Assistant's own machinery, nothing a chip could show or switch.</summary>
    internal static readonly string[] HomeHiddenDomains = ["persistent_notification", "zone", "sun", "conversation", "tts", "stt", "wake_word", "event", "assist_satellite", "tag", "image", "camera", "update", "todo", "calendar", "weather", "ai_task"];

    // One tab as built: its strip title and rows, and the reference behind each row (null for a heading, a gap or a note).
    private sealed record HomeTab(MenuTab Tab, List<string?> Refs);

    /// <summary>
    /// The home bar's picker (2026-10-12, the user's ask: choose between rooms, lights, devices, and change the order): On the bar
    /// first — the items in the bar's order, numbered, the move keys (<see cref="MenuPage.Moves"/>: Shift/Alt+↑↓ a place,
    /// Alt+Home/End to the top or bottom, Delete out) and Enter's actions page for those whose terminal eats the keys — then
    /// Rooms (the areas by floor) and one tab per kind of device (<see cref="HomeDomainTabs"/>), each in sections by area, where
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

        if (HomeCatalog is null || !HaSession.Configured(EffectiveNow()))
        {
            Sink.Warning(HomeBarText.NotConfigured);
            return false;
        }

        var (catalog, error) = await HomeCatalog(cancellationToken).ConfigureAwait(false);
        if (catalog is null)
        {
            Sink.Error(HomeBarText.NoCatalog(error ?? HaText.NotConfigured));
            return false;
        }

        bool changed = false;
        int tab = 0;
        int cursor = 0;
        string filter = "";
        try
        {
            while (true)
            {
                var items = HomeBarItems.Resolve(_settings.Current.HomeBarItems);
                var built = HomePickerTabs(catalog, items, filter);
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
                    if (items.Count == 0 || pick.Row >= items.Count)
                    {
                        continue;
                    }

                    int move = pick.Remove ? 0 : pick.Move;
                    if (!pick.Remove && pick.Move == 0)
                    {
                        // Enter: the actions page, for a terminal that keeps the move keys to itself.
                        if (await HomeItemActionAsync(HomeBarItems.NameOf(items[pick.Row], catalog), cancellationToken).ConfigureAwait(false) is not { } chosen)
                        {
                            continue;
                        }

                        move = chosen;
                    }

                    if (move == 0)
                    {
                        Apply(SettingsField.ShowHomeBar, d => d.HomeBarItems = HomeBarItems.Remove(items, pick.Row));
                        cursor = Math.Min(pick.Row, Math.Max(0, items.Count - 2));
                        changed = true;
                        continue;
                    }

                    var (moved, at) = HomeBarItems.Move(items, pick.Row, move);
                    if (at != pick.Row)
                    {
                        Apply(SettingsField.ShowHomeBar, d => d.HomeBarItems = moved);
                        changed = true;
                    }

                    cursor = at;
                    continue;
                }

                if (pick.Move != 0 || pick.Remove || tab >= built.Count || pick.Row >= built[tab].Refs.Count || built[tab].Refs[pick.Row] is not { } reference)
                {
                    continue;
                }

                Apply(SettingsField.ShowHomeBar, d => d.HomeBarItems = HomeBarItems.Flip(items, reference));
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

    // The bar item's actions page: a move (Top/−1/+1/Bottom), 0 for remove, or null on ESC.
    private async Task<int?> HomeItemActionAsync(string name, CancellationToken cancellationToken)
    {
        string[] rows = [HomeBarText.MoveTop, HomeBarText.MoveUp, HomeBarText.MoveDown, HomeBarText.MoveBottom, HomeBarText.RemoveItem];
        int[] moves = [HomeBarItems.Top, -1, 1, HomeBarItems.Bottom, 0];
        var page = new MenuPage(Crumb(FieldName(SettingsField.ShowHomeBar)) + " › " + name, rows.Select(r => Markup.Escape(r)).ToList(), PickKeys);
        return await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is { } pick && pick.Row >= 0 && pick.Row < moves.Length ? moves[pick.Row] : null;
    }

    /// <summary>The picker's tabs over <paramref name="catalog"/> for the bar's <paramref name="items"/>, the kinds' tabs under <paramref name="filter"/>. Pure over its data.</summary>
    private static List<HomeTab> HomePickerTabs(HaCatalog catalog, IReadOnlyList<string> items, string filter)
    {
        var tabs = new List<HomeTab> { BarTab(catalog, items), RoomsTab(catalog, items, filter) };
        var named = HomeDomainTabs.SelectMany(t => t.Domains).ToHashSet(StringComparer.Ordinal);
        foreach (var (title, domains) in HomeDomainTabs)
        {
            var entities = catalog.States
                .Where(e => !catalog.Disabled(e.Id) && catalog.Entry(e.Id) is not { Hidden: true })
                .Where(e => domains.Length > 0 ? domains.Contains(e.Domain, StringComparer.Ordinal) : !named.Contains(e.Domain) && !HomeHiddenDomains.Contains(e.Domain, StringComparer.Ordinal))
                .ToList();
            tabs.Add(DomainTab(title, catalog, items, entities, filter));
        }

        return tabs;
    }

    private static HomeTab BarTab(HaCatalog catalog, IReadOnlyList<string> items)
    {
        var rows = new List<string>();
        var refs = new List<string?>();
        int number = 1;
        foreach (string item in items)
        {
            HomeBarItems.TryParse(item, out var kind, out string id);
            string glyph = kind == HomeItemKind.Area ? HomeChips.AreaGlyph : HomeChips.GlyphFor(id, null);
            string where = kind == HomeItemKind.Area ? HomeBarText.RoomsTab.ToLowerInvariant() : catalog.AreaOf(id) is { } area ? catalog.Area(area)?.Name ?? id : id;
            rows.Add(Markup.Escape($"{number++.ToString(System.Globalization.CultureInfo.InvariantCulture),2}. {glyph} {HomeBarItems.NameOf(item, catalog)}") + Theme.DimMarkup("  " + where));
            refs.Add(item);
        }

        if (rows.Count == 0)
        {
            rows.Add(Theme.DimMarkup(HomeBarText.EmptyBar));
            refs.Add(null);
        }

        return new HomeTab(new MenuTab(HomeBarText.BarTab, rows) { Moves = true, Hint = HomeBarText.BarHint }, refs);
    }

    private static HomeTab RoomsTab(HaCatalog catalog, IReadOnlyList<string> items, string filter)
    {
        var sections = new List<(string Heading, string? Note, List<(string Ref, string Name, string Note)> Rows)>();
        foreach (var group in catalog.Areas.GroupBy(a => a.FloorId))
        {
            var rows = group
                .Where(a => MenuFilter.Matches(filter, a.Name, a.Id))
                .Select(a => (HomeBarItems.Area(a.Id), a.Name, HomeBarText.Lights(catalog.MembersOf(a.Id, HomeBarItems.AreaDomains).Count)))
                .ToList();
            if (rows.Count > 0)
            {
                sections.Add((catalog.Floor(group.Key)?.Name ?? (catalog.Floors.Count > 0 ? HomeBarText.NoArea : ""), null, rows));
            }
        }

        return Sectioned(HomeBarText.RoomsTab, sections, items, filter);
    }

    private static HomeTab DomainTab(string title, HaCatalog catalog, IReadOnlyList<string> items, IReadOnlyList<HaEntity> entities, string filter)
    {
        var byArea = new Dictionary<string, List<(string Ref, string Name, string Note)>>(StringComparer.Ordinal);
        var noArea = new List<(string Ref, string Name, string Note)>();
        var extra = new List<(string Ref, string Name, string Note)>();
        foreach (var entity in entities.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            string? areaId = catalog.AreaOf(entity.Id);
            string areaName = areaId is null ? "" : catalog.Area(areaId)?.Name ?? areaId;
            string labels = string.Join(" ", catalog.LabelsOf(entity.Id));
            if (!MenuFilter.Matches(filter, entity.Name, entity.Id + " " + areaName + " " + labels))
            {
                continue;
            }

            var row = (HomeBarItems.Entity(entity.Id), entity.Name, entity.Id);
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
