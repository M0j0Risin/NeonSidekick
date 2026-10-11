using NeonSidekick.Shopping;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

// ── A store's layout editor (2026-10-11, the user's ask) ──────────────────

internal sealed partial class SettingsMenu
{
    private const int AislesTabIndex = 0;
    private const int SectionsTabIndex = 1;
    private const int FactsTabIndex = 2;
    private const int GuessesTabIndex = 3;

    /// <summary>
    /// A store's layout by hand (2026-10-11, the user's ask: the aisles, sections, walk and facts the model alone could change), from the
    /// store manager's <c>▦ layout</c>: four tabs. <b>Aisles</b>: the first row adds an aisle or zone (its name typed, then a section
    /// picked or none), each location in walk order with its facts counted; Enter on one renames it, puts it in a section, moves it up
    /// or down the walk (the whole walk numbered from then on) or removes it after a yes (its facts go). <b>Sections</b>: the first row
    /// adds one; Enter renames or removes (after a yes; its locations and facts keep their places with no section). <b>Facts</b>: every
    /// placement, filtered by typing; Enter moves it (the user's from then on), changes its kind or removes it. <b>Guesses</b>: the
    /// model's guesses with the lines each places now, the tab it opens on while there are any; Enter keeps one (the user's), moves it or
    /// removes it. Tabbed pages carry no buttons, so a row's actions are a page of their own (<c>/skills</c> › a skill's shape).
    /// </summary>
    public async Task EditStoreLayoutAsync(ShopStore shop, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shop);
        if (Shopping is not { } list)
        {
            return;
        }

        if (!_pane.Enabled)
        {
            Sink.Notice(ShoppingText.LayoutNeedsPane);
            return;
        }

        var store = list.Store;
        int tab = store.Placements(shop.Id).Any(p => p.Source == ShoppingSources.Guess) ? GuessesTabIndex : AislesTabIndex;
        int cursor = -1;
        string filter = "";
        while (true)
        {
            var locations = store.Locations(shop.Id);
            var sections = store.Sections(shop.Id);
            var facts = store.Placements(shop.Id);
            var items = store.Items();
            ShopPlace PlaceOf(ShopPlacement fact) => ShoppingPlacer.Resolve(fact, locations, sections);
            var shownFacts = facts.Where(f => filter.Length == 0 || f.Term.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            var guesses = facts.Where(f => f.Source == ShoppingSources.Guess).ToList();

            List<string> aisleRows = [Markup.Escape(ShoppingText.AddLocationRow), .. locations.Select((l, i) => Markup.Escape(ShoppingText.LayoutLocationRow(i + 1, l, facts.Count(f => f.LocationId == l.Id))))];
            List<string> sectionRows = [Markup.Escape(ShoppingText.AddSectionRow), .. sections.Select(s => Markup.Escape(ShoppingText.LayoutSectionRow(s, locations.Count(l => l.SectionId == s.Id), facts.Count(f => f.SectionId == s.Id || (f.SectionId is null && locations.Any(l => l.Id == f.LocationId && l.SectionId == s.Id))))))];
            List<string> factRows = shownFacts.Count > 0
                ? shownFacts.Select(f => Markup.Escape(ShoppingText.FactRow(f, PlaceOf(f)))).ToList()
                : [filter.Length > 0 ? MenuFilter.NoMatchRow(filter) : Theme.DimMarkup(ShoppingText.NoFactsRow)];
            List<string> guessRows = guesses.Count > 0
                ? guesses.Select(f => Markup.Escape(ShoppingText.GuessRow(f, PlaceOf(f), items.Where(i => !i.Checked && list.PlaceOf(i.Name, shop).Placement?.Id == f.Id).ToList()))).ToList()
                : [Theme.DimMarkup(ShoppingText.NoGuessesRow)];
            var tabs = new MenuTab[]
            {
                new(ShoppingText.AislesTab, aisleRows) { Hint = ShoppingText.LayoutKeys },
                new(ShoppingText.SectionsTab, sectionRows) { Hint = ShoppingText.LayoutKeys },
                new(ShoppingText.FactsTab, factRows)
                {
                    Hint = MenuFilter.Hint(ShoppingText.FactsKeys, filter),
                    Filter = filter,
                    Caption = MenuFilter.CaptionOrNull(filter, shownFacts.Count, facts.Count),
                },
                new(ShoppingText.GuessesTitle(guesses.Count), guessRows) { Hint = ShoppingText.LayoutKeys },
            };
            var page = MenuPage.Tabbed(Crumb(ShoppingText.LayoutTitle(shop)), tabs, tab, ShoppingText.LayoutKeys) with { TabCursors = [locations.Count > 0 ? 1 : 0, sections.Count > 0 ? 1 : 0, 0, 0] };
            if (await _pane.PickAsync(page, cursor < 0 ? (page.TabCursors![tab]) : cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return;
            }

            tab = pick.Tab;
            cursor = pick.Row;

            if (pick.Filter is { } typed)
            {
                filter = typed;
                cursor = -1;
                continue;
            }

            switch (tab)
            {
                case AislesTabIndex when pick.Row == 0:
                    if (await TypeStoreNameAsync(page, 0, "", cancellationToken).ConfigureAwait(false) is { } label)
                    {
                        var (picked, section) = await PickSectionAsync(sections, null, cancellationToken).ConfigureAwait(false);
                        if (picked)
                        {
                            Say(list.AddLocation(shop, label, section?.Name));   // ESC on the section picker: nothing added
                        }
                    }

                    break;
                case AislesTabIndex when pick.Row - 1 < locations.Count:
                    await LocationActionsAsync(list, shop, locations[pick.Row - 1], sections, facts, page, pick.Row, cancellationToken).ConfigureAwait(false);
                    break;
                case SectionsTabIndex when pick.Row == 0:
                    if (await TypeStoreNameAsync(page, 0, "", cancellationToken).ConfigureAwait(false) is { } name)
                    {
                        Say(list.AddSection(shop, name));
                    }

                    break;
                case SectionsTabIndex when pick.Row - 1 < sections.Count:
                {
                    var section = sections[pick.Row - 1];
                    switch (await PickActionAsync(section.Name, ShoppingText.SectionActions, cancellationToken).ConfigureAwait(false))
                    {
                        case 0 when await TypeStoreNameAsync(page, pick.Row, section.Name, cancellationToken).ConfigureAwait(false) is { } renamed && renamed != section.Name:
                            Say(list.RenameSection(section, renamed));
                            break;
                        case 1 when await ConfirmAsync(ShoppingText.RemoveSectionPrompt(section), cancellationToken).ConfigureAwait(false):
                            Say(list.RemoveSection(shop, section));
                            break;
                    }

                    break;
                }

                case FactsTabIndex when pick.Row < shownFacts.Count:
                {
                    var fact = shownFacts[pick.Row];
                    switch (await PickActionAsync(fact.Term, ShoppingText.FactActions, cancellationToken).ConfigureAwait(false))
                    {
                        case 0:
                            await MoveFactAsync(list, shop, fact, locations, sections, cancellationToken).ConfigureAwait(false);
                            break;
                        case 1 when await PickKindAsync(fact, cancellationToken).ConfigureAwait(false) is { } kind && kind != fact.Kind:
                            Say(list.SetFactKind(shop, fact, kind));
                            break;
                        case 2:
                            Say(list.RemoveFact(shop, fact));
                            break;
                    }

                    break;
                }

                case GuessesTabIndex when pick.Row < guesses.Count:
                {
                    var guess = guesses[pick.Row];
                    switch (await PickActionAsync(guess.Term, ShoppingText.GuessActions, cancellationToken).ConfigureAwait(false))
                    {
                        case 0:
                            Say(list.ConfirmFact(shop, guess));
                            break;
                        case 1:
                            await MoveFactAsync(list, shop, guess, locations, sections, cancellationToken).ConfigureAwait(false);
                            break;
                        case 2:
                            Say(list.RemoveFact(shop, guess));
                            break;
                    }

                    break;
                }
            }
        }
    }

    /// <summary>A location's actions: rename (typed on its row), its section, up or down the walk, remove after a yes.</summary>
    private async Task LocationActionsAsync(ShoppingList list, ShopStore shop, ShopLocation location, IReadOnlyList<ShopSection> sections, IReadOnlyList<ShopPlacement> facts, MenuPage page, int row, CancellationToken cancellationToken)
    {
        switch (await PickActionAsync(location.Label, ShoppingText.LocationActions, cancellationToken).ConfigureAwait(false))
        {
            case 0 when await TypeStoreNameAsync(page, row, location.Label, cancellationToken).ConfigureAwait(false) is { } renamed && renamed != location.Label:
                Say(list.RenameLocation(location, renamed));
                break;
            case 1:
            {
                var (picked, section) = await PickSectionAsync(sections, location.SectionId, cancellationToken).ConfigureAwait(false);
                if (picked)
                {
                    Say(list.SetLocationSection(shop, location, section));
                }

                break;
            }
            case 2:
                Say(list.MoveLocation(shop, location, up: true));
                break;
            case 3:
                Say(list.MoveLocation(shop, location, up: false));
                break;
            case 4:
                if (await ConfirmAsync(ShoppingText.RemoveLocationPrompt(location, facts.Count(f => f.LocationId == location.Id)), cancellationToken).ConfigureAwait(false))
                {
                    Say(list.RemoveLocation(shop, location));
                }
                else
                {
                    Sink.Notice(ChatScreen.KeptNotice);
                }

                break;
        }
    }

    /// <summary>A fact moved: the place picker (locations in walk order, then sections alone); the user's from then on.</summary>
    private async Task MoveFactAsync(ShoppingList list, ShopStore shop, ShopPlacement fact, IReadOnlyList<ShopLocation> locations, IReadOnlyList<ShopSection> sections, CancellationToken cancellationToken)
    {
        var choices = new List<(string Label, ShopLocation? Location, ShopSection? Section)>();
        choices.AddRange(locations.Select(l => (ShoppingText.LocationChoice(l), (ShopLocation?)l, (ShopSection?)null)));
        choices.AddRange(sections.Select(s => (ShoppingText.SectionChoice(s), (ShopLocation?)null, (ShopSection?)s)));
        if (choices.Count == 0)
        {
            Sink.Notice(ShoppingText.NoLayoutYet(shop));
            return;
        }

        int at = Math.Max(0, choices.FindIndex(c => fact.LocationId is { } l ? c.Location?.Id == l : c.Location is null && c.Section?.Id == fact.SectionId));
        var page = new MenuPage(Crumb(ShoppingText.PlaceTitle(new ShopItem(0, fact.Term, 1, "", false, DateTimeOffset.UnixEpoch))), choices.Select(c => Markup.Escape(c.Label)).ToList(), PickKeys);
        if (await PickAsync(page, at, cancellationToken).ConfigureAwait(false) is { } picked && picked < choices.Count)
        {
            Say(list.MoveFact(shop, fact, choices[picked].Location, choices[picked].Section));
        }
    }

    /// <summary>
    /// A section picked for a location: <see cref="ShoppingText.NoSectionRow"/>, then the store's sections, the cursor on
    /// <paramref name="current"/>. <c>Picked</c> false on ESC; <c>Section</c> null for no section.
    /// </summary>
    private async Task<(bool Picked, ShopSection? Section)> PickSectionAsync(IReadOnlyList<ShopSection> sections, long? current, CancellationToken cancellationToken)
    {
        List<string> rows = [Markup.Escape(ShoppingText.NoSectionRow), .. sections.Select(s => Markup.Escape(s.Name))];
        int at = current is { } id ? Math.Max(0, sections.ToList().FindIndex(s => s.Id == id) + 1) : 0;
        if (await PickAsync(new MenuPage(Crumb(ShoppingText.SectionsTab), rows, PickKeys), at, cancellationToken).ConfigureAwait(false) is not { } picked)
        {
            return (false, null);
        }

        return (true, picked == 0 ? null : sections[picked - 1]);
    }

    /// <summary>A kind picked for a fact: item, brand or category, the cursor on its own; null on ESC.</summary>
    private async Task<string?> PickKindAsync(ShopPlacement fact, CancellationToken cancellationToken)
    {
        var kinds = ShoppingKinds.All;
        var page = new MenuPage(Crumb(fact.Term), kinds.Select(k => Markup.Escape(k)).ToList(), PickKeys);
        return await PickAsync(page, Math.Max(0, kinds.ToList().IndexOf(fact.Kind)), cancellationToken).ConfigureAwait(false) is { } picked ? kinds[picked] : null;
    }

    /// <summary>A row's actions on a page of their own (tabbed pages carry no buttons): the index picked, null on ESC.</summary>
    private Task<int?> PickActionAsync(string title, IReadOnlyList<string> actions, CancellationToken cancellationToken) =>
        PickAsync(new MenuPage(Crumb(title), actions.Select(a => Markup.Escape(a)).ToList(), PickKeys), 0, cancellationToken);
}
