using System.Globalization;
using System.Text.RegularExpressions;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Shopping;

/// <summary>What <see cref="ShoppingList.Add"/> did to one line: added it, or bumped the quantity of the line already there (unticking it).</summary>
public sealed record ShopAdded(ShopItem Item, bool Bumped, ShopPlace Place);

/// <summary>
/// The list walked through a store (<paramref name="Store"/> null: none set up, the lines in the order added). <paramref name="Numbered"/>
/// when it is the active store's walk, whose numbers <see cref="ShoppingList.Find"/> reads (2026-10-11 review: a number named the line's
/// place in the order added, which no output showed, so a model counting the grouped bullets reached another line).
/// </summary>
public sealed record ShopView(ShopStore? Store, IReadOnlyList<ShopGroup> Groups, IReadOnlyList<ShopItem> Ticked, IReadOnlyList<IReadOnlyList<ShopItem>> Duplicates, bool Numbered = false)
{
    /// <summary>The lines as shown: the groups' in walk order, then the ticked ones. A line's number is its place here, from 1.</summary>
    public IReadOnlyList<ShopItem> Shown => [.. Groups.SelectMany(g => g.Items.Select(p => p.Item)), .. Ticked];
}

/// <summary>What <see cref="ShoppingList.Locate"/> found for a term at a store: where the list would put it, and the store's other facts that share a word with it.</summary>
public sealed record ShopLocated(string Term, ShopStore Store, ShopPlace Place, IReadOnlyList<ShopPlace> Related, IReadOnlyList<string> ReferenceFiles);

/// <summary>
/// The shopping list's operations (2026-10-11), shared by the model's <c>shopping_*</c> tools and the user's <c>/shopping</c>: the
/// store read through <paramref name="store"/> at every call (the profile's, so a profile switch needs no rebuild), every change one
/// audit line (<see cref="ShoppingStore.Category"/>, the memory store's "Remembered:" shape). Answers are records; the words are
/// <see cref="ShoppingText"/>'s.
/// </summary>
public sealed partial class ShoppingList(Func<ShoppingStore> store)
{
    private readonly Func<ShoppingStore> _store = store ?? throw new ArgumentNullException(nameof(store));

    public ShoppingStore Store => _store();

    /// <summary>
    /// The store <paramref name="name"/> names, or the active one when it is blank. Null with <paramref name="error"/> set when the name
    /// is no store; null with no error when there is no store at all and none was named.
    /// </summary>
    public ShopStore? StoreFor(string? name, out string? error)
    {
        error = null;
        var store = _store();
        if (string.IsNullOrWhiteSpace(name))
        {
            return store.ActiveStore();
        }

        if (store.FindStore(name) is { } found)
        {
            return found;
        }

        error = ShoppingText.NoSuchStore(name, store.Stores());
        return null;
    }

    /// <summary>The list walked through <paramref name="shop"/> (none: one unplaced group in the order added), the ticked lines apart.</summary>
    public ShopView View(ShopStore? shop)
    {
        var store = _store();
        var items = store.Items();
        var open = items.Where(i => !i.Checked).ToList();
        var ticked = items.Where(i => i.Checked).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        IReadOnlyList<ShopGroup> groups;
        if (shop is null)
        {
            groups = open.Count == 0 ? [] : [new ShopGroup(null, null, open.Select(i => new PlacedItem(i, ShopPlace.Unplaced)).ToList())];
        }
        else
        {
            groups = ShoppingPlacer.Walk(open, store.Placements(shop.Id), store.Locations(shop.Id), store.Sections(shop.Id));
        }

        return new ShopView(shop, groups, ticked, ShoppingPlacer.Duplicates(items), shop?.Id == store.ActiveStore()?.Id);
    }

    /// <summary>
    /// <paramref name="name"/> on the list: the line already there by the same folded name (<see cref="ShoppingPlacer.Key"/>) gets the
    /// quantity added (or set, when it was ticked: bought, and wanted again), else a new line. Its place at <paramref name="shop"/> comes
    /// with it. Null when the write failed.
    /// </summary>
    public ShopAdded? Add(string name, int quantity, ShopStore? shop, string note = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var store = _store();
        quantity = Math.Max(1, quantity);
        string key = ShoppingPlacer.Key(name);
        var existing = store.Items().FirstOrDefault(i => ShoppingPlacer.Key(i.Name) == key);
        ShopItem? item;
        bool bumped = existing is not null;
        if (existing is not null)
        {
            int total = existing.Checked ? quantity : existing.Quantity + quantity;
            item = store.SetQuantity(existing.Id, total) ? existing with { Quantity = total, Checked = false } : null;
        }
        else
        {
            item = store.AddItem(name, quantity, note);
        }

        if (item is null)
        {
            return null;
        }

        Audit(bumped ? ShoppingText.AuditBumped(item) : ShoppingText.AuditAdded(item));
        return new ShopAdded(item, bumped, PlaceOf(item.Name, shop));
    }

    /// <summary>Where <paramref name="name"/> is at <paramref name="shop"/>; unplaced with no store.</summary>
    public ShopPlace PlaceOf(string name, ShopStore? shop)
    {
        if (shop is null)
        {
            return ShopPlace.Unplaced;
        }

        var store = _store();
        return ShoppingPlacer.Place(name, store.Placements(shop.Id), store.Locations(shop.Id), store.Sections(shop.Id));
    }

    /// <summary>
    /// The lines <paramref name="text"/> names: its number on the list (1-based, as the active store's list shows it numbered,
    /// <see cref="ShopView.Shown"/>), else every line with the same folded name, else every line whose name holds it at word boundaries.
    /// Empty for none.
    /// </summary>
    public IReadOnlyList<ShopItem> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim().TrimStart('#');
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            var shown = View(_store().ActiveStore()).Shown;
            return number >= 1 && number <= shown.Count ? [shown[number - 1]] : [];
        }

        var items = _store().Items();

        string key = ShoppingPlacer.Key(trimmed);
        if (key.Length == 0)
        {
            return [];
        }

        var same = items.Where(i => ShoppingPlacer.Key(i.Name) == key).ToList();
        return same.Count > 0 ? same : items.Where(i => ShoppingPlacer.Contains(i.Name, trimmed)).ToList();
    }

    /// <summary>Ticks (or unticks) <paramref name="item"/>. False when the write failed.</summary>
    public bool Check(ShopItem item, bool on)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_store().SetChecked(item.Id, on))
        {
            return false;
        }

        Audit(ShoppingText.AuditChecked(item, on));
        return true;
    }

    /// <summary>Ticks (or unticks) every line (2026-10-11, the pane's tick all): how many changed, null when the write failed.</summary>
    public int? CheckAll(bool on)
    {
        int? changed = _store().SetAllChecked(on);
        if (changed is > 0)
        {
            Audit(ShoppingText.AuditCheckedAll(changed.Value, on));
        }

        return changed;
    }

    /// <summary>
    /// <paramref name="item"/> renamed (2026-10-11, the user's ask: a line fixed without the model): its quantity, note and tick kept; its
    /// place follows the new name, since a line is placed when it is shown. A name another line already has (folded alike,
    /// <see cref="ShoppingPlacer.Key"/>) merges the two, that line keeping its name, so a product is one line: wanted when either was, with
    /// the wanted quantity (a ticked line's bought quantity is not wanted again, as <see cref="Add"/> has it, and a ticked line merged away
    /// into a wanted one is recorded as bought, as <see cref="Remove"/> would); ticked with both quantities when both were (2026-10-11
    /// review: the merge added every quantity and unticked whatever the ticks). The answer as a sentence; <c>Error: </c> leads a refusal.
    /// </summary>
    public string RenameItem(ShopItem item, string name)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Error: " + ShoppingText.NoItems;
        }

        var store = _store();
        string trimmed = name.Trim();
        string key = ShoppingPlacer.Key(trimmed);
        if (store.Items().FirstOrDefault(i => i.Id != item.Id && ShoppingPlacer.Key(i.Name) == key) is { } other)
        {
            var current = store.Items().FirstOrDefault(i => i.Id == item.Id) ?? item;
            bool ticked = current.Checked && other.Checked;
            int total = current.Checked == other.Checked ? other.Quantity + current.Quantity : current.Checked ? other.Quantity : current.Quantity;
            bool written = store.SetQuantity(other.Id, total) && (!ticked || store.SetChecked(other.Id, true))
                && store.RemoveItem(current.Id, current.Checked && !other.Checked ? current with { Name = other.Name } : null);
            if (!written)
            {
                return "Error: " + ShoppingText.WriteFailed;
            }

            var merged = other with { Quantity = total, Checked = ticked };
            Audit(ShoppingText.AuditItemMerged(item, merged));
            return ShoppingText.ItemMerged(item, merged);
        }

        if (!store.RenameItem(item.Id, trimmed))
        {
            return "Error: " + ShoppingText.WriteFailed;
        }

        Audit(ShoppingText.AuditItemRenamed(item, trimmed));
        return ShoppingText.Renamed("item", item.Name, trimmed);
    }

    /// <summary>Sets <paramref name="item"/>'s quantity. False when the write failed.</summary>
    public bool SetQuantity(ShopItem item, int quantity)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_store().SetQuantity(item.Id, quantity))
        {
            return false;
        }

        Audit(ShoppingText.AuditBumped(item with { Quantity = Math.Max(1, quantity) }));
        return true;
    }

    /// <summary>
    /// Takes <paramref name="item"/> off the list. A ticked line is recorded as bought with it (2026-10-11, <see cref="Bought"/>; in the
    /// same transaction since the review: a delete that failed counted it bought all the same). False when the write failed.
    /// </summary>
    public bool Remove(ShopItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var store = _store();
        var ticked = store.Items().FirstOrDefault(i => i.Id == item.Id) is { Checked: true } t ? t : null;
        if (!store.RemoveItem(item.Id, ticked))
        {
            return false;
        }

        Audit(ShoppingText.AuditRemoved(item));
        return true;
    }

    /// <summary>
    /// Removes the ticked lines (every line with <paramref name="all"/>): how many went, null when the write failed. The ticked ones are
    /// recorded as bought with them, in one transaction (2026-10-11, <see cref="Bought"/>; the same transaction since the review); an
    /// unticked line cleared with all was not bought.
    /// </summary>
    public int? Clear(bool all)
    {
        int? cleared = _store().Clear(all, recordBought: true);
        if (cleared is > 0)
        {
            Audit(ShoppingText.AuditCleared(cleared.Value, all));
        }

        return cleared;
    }

    /// <summary>What was bought before and is not on the list now (2026-10-11, the recently bought): the most often first, then the most recent.</summary>
    public IReadOnlyList<ShopBought> Bought()
    {
        var store = _store();
        var onList = store.Items().Select(i => ShoppingPlacer.Key(i.Name)).ToHashSet(StringComparer.Ordinal);
        return store.Bought().Where(b => !onList.Contains(ShoppingPlacer.Key(b.Name))).ToList();
    }

    /// <summary>Forgets a line bought before (it never comes back on the add page until bought again). The answer as a sentence.</summary>
    public string ForgetBought(ShopBought bought)
    {
        ArgumentNullException.ThrowIfNull(bought);
        if (!_store().ForgetBought(bought.Id))
        {
            return "Error: " + ShoppingText.WriteFailed;
        }

        Audit(ShoppingText.AuditBoughtForgotten(bought));
        return ShoppingText.BoughtForgotten(bought);
    }

    // ---- the layout, by hand (2026-10-11, the layout editor and the pane's place) ----

    /// <summary>
    /// <paramref name="item"/> placed at <paramref name="location"/> (or in <paramref name="section"/> alone): an item fact of its name, the
    /// user's, so it lands there now and every time. An item fact already held under another spelling of the name (folded alike,
    /// <see cref="ShoppingPlacer.Key"/>) is the one moved, its note kept, and any more such are forgotten (2026-10-11 review: a second fact
    /// of the same key lost the tie to the old one, so the line stayed where it was while the answer said it moved). The answer as a sentence.
    /// </summary>
    public string PlaceItem(ShopStore shop, ShopItem item, ShopLocation? location, ShopSection? section)
    {
        ArgumentNullException.ThrowIfNull(shop);
        ArgumentNullException.ThrowIfNull(item);
        var store = _store();
        string key = ShoppingPlacer.Key(item.Name);
        var same = store.Placements(shop.Id).Where(p => p.Kind == ShoppingKinds.Item && ShoppingPlacer.Key(p.Term) == key).ToList();
        string answer = same.Count > 0
            ? Place(shop, same[0].Term, ShoppingKinds.Item, location?.Label, location is null ? section?.Name : null, ShoppingSources.User, same[0].Note)
            : Place(shop, item.Name, ShoppingKinds.Item, location?.Label, location is null ? section?.Name : null, ShoppingSources.User);
        if (!answer.StartsWith("Error: ", StringComparison.Ordinal))
        {
            foreach (var extra in same.Skip(1))
            {
                store.RemovePlacement(extra.Id);
            }
        }

        return answer;
    }

    /// <summary>A fact moved to <paramref name="location"/> (or <paramref name="section"/> alone), its kind and note kept, the user's from then on.</summary>
    public string MoveFact(ShopStore shop, ShopPlacement fact, ShopLocation? location, ShopSection? section)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return Place(shop, fact.Term, fact.Kind, location?.Label, location is null ? section?.Name : null, ShoppingSources.User, fact.Note);
    }

    /// <summary>A fact's kind changed. The answer as a sentence.</summary>
    public string SetFactKind(ShopStore shop, ShopPlacement fact, string kind)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!_store().SetKind(fact.Id, kind))
        {
            return "Error: " + ShoppingText.WriteFailed;
        }

        Audit(ShoppingText.AuditFactKind(shop, fact, kind));
        return ShoppingText.FactKind(fact, kind);
    }

    /// <summary>A guess kept: the fact becomes the user's. The answer as a sentence.</summary>
    public string ConfirmFact(ShopStore shop, ShopPlacement fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!_store().SetSource(fact.Id, ShoppingSources.User))
        {
            return "Error: " + ShoppingText.WriteFailed;
        }

        Audit(ShoppingText.AuditFactConfirmed(shop, fact));
        return ShoppingText.FactConfirmed(fact);
    }

    /// <summary>A fact forgotten. The answer as a sentence.</summary>
    public string RemoveFact(ShopStore shop, ShopPlacement fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return _store().RemovePlacement(fact.Id) ? Logged(ShoppingText.Unplaced(shop, fact.Term)) : "Error: " + ShoppingText.WriteFailed;
    }

    /// <summary>A location added (in <paramref name="section"/> when given, made when missing). The answer as a sentence.</summary>
    public string AddLocation(ShopStore shop, string label, string? section)
    {
        ArgumentNullException.ThrowIfNull(shop);
        var store = _store();
        if (string.IsNullOrWhiteSpace(label))
        {
            return "Error: " + ShoppingText.NoPlaceGiven;
        }

        if (store.Locations(shop.Id).Any(l => string.Equals(l.Label, label.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return "Error: " + ShoppingText.NameTaken("location", label);
        }

        var inSection = string.IsNullOrWhiteSpace(section) ? null : store.EnsureSection(shop.Id, section);
        return store.EnsureLocation(shop.Id, label, inSection?.Id) is { } made ? Logged(ShoppingText.LocationAdded(shop, made)) : "Error: " + ShoppingText.WriteFailed;
    }

    /// <summary>A location renamed. The answer as a sentence.</summary>
    public string RenameLocation(ShopLocation location, string label) =>
        _store().RenameLocation(location.Id, label) ? Logged(ShoppingText.Renamed("location", location.Label, label)) : "Error: " + ShoppingText.NameTaken("location", label);

    /// <summary>A location put in <paramref name="section"/> (null: none). The answer as a sentence.</summary>
    public string SetLocationSection(ShopStore shop, ShopLocation location, ShopSection? section) =>
        _store().SetLocationSection(location.Id, section?.Id)
            ? Logged(ShoppingText.LocationSection(location, section))
            : "Error: " + ShoppingText.WriteFailed;

    /// <summary>
    /// A location moved one place up or down the walk: every location of the store numbered 1..n in the walk as it stands with the two
    /// swapped, so the order is the user's from then on. The answer as a sentence; nothing to do at an end.
    /// </summary>
    public string MoveLocation(ShopStore shop, ShopLocation location, bool up)
    {
        var store = _store();
        var walk = store.Locations(shop.Id).ToList();
        int at = walk.FindIndex(l => l.Id == location.Id);
        int to = at + (up ? -1 : 1);
        if (at < 0 || to < 0 || to >= walk.Count)
        {
            return ShoppingText.WalkEnd(location, up);
        }

        (walk[at], walk[to]) = (walk[to], walk[at]);
        for (int i = 0; i < walk.Count; i++)
        {
            if (!store.SetWalk(walk[i].Id, i + 1) && walk[i].Walk != i + 1)
            {
                return "Error: " + ShoppingText.WriteFailed;
            }
        }

        return Logged(ShoppingText.WalkSet(shop, location, to + 1));
    }

    /// <summary>A location removed with the facts at it. The answer as a sentence.</summary>
    public string RemoveLocation(ShopStore shop, ShopLocation location) =>
        _store().RemoveLocation(location.Id) ? Logged(ShoppingText.LocationRemoved(shop, location)) : "Error: " + ShoppingText.WriteFailed;

    /// <summary>A section added. The answer as a sentence.</summary>
    public string AddSection(ShopStore shop, string name)
    {
        var store = _store();
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Error: " + ShoppingText.NoPlaceGiven;
        }

        if (store.Sections(shop.Id).Any(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return "Error: " + ShoppingText.NameTaken("section", name);
        }

        return store.EnsureSection(shop.Id, name) is { } made ? Logged(ShoppingText.SectionAdded(shop, made)) : "Error: " + ShoppingText.WriteFailed;
    }

    /// <summary>A section renamed. The answer as a sentence.</summary>
    public string RenameSection(ShopSection section, string name) =>
        _store().RenameSection(section.Id, name) ? Logged(ShoppingText.Renamed("section", section.Name, name)) : "Error: " + ShoppingText.NameTaken("section", name);

    /// <summary>A section removed; its locations and facts keep their places with no section. The answer as a sentence.</summary>
    public string RemoveSection(ShopStore shop, ShopSection section) =>
        _store().RemoveSection(section.Id) ? Logged(ShoppingText.SectionRemoved(shop, section)) : "Error: " + ShoppingText.WriteFailed;

    /// <summary>An answer, audited as a layout change.</summary>
    private static string Logged(string answer)
    {
        Audit("Layout: " + answer);
        return answer;
    }

    /// <summary>
    /// What the store knows of <paramref name="term"/>: where the list would put it, the placements sharing a word with it (at most
    /// <paramref name="related"/>, the closest first) and the files in the store's reference folder.
    /// </summary>
    public ShopLocated Locate(string term, ShopStore shop, int related = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        ArgumentNullException.ThrowIfNull(shop);
        var store = _store();
        var placements = store.Placements(shop.Id);
        var locations = store.Locations(shop.Id);
        var sections = store.Sections(shop.Id);
        var place = ShoppingPlacer.Place(term, placements, locations, sections);
        var words = ShoppingPlacer.Words(term).ToHashSet(StringComparer.Ordinal);
        var near = placements
            .Where(p => p.Id != place.Placement?.Id)
            .Select(p => (Placement: p, Shared: ShoppingPlacer.Words(p.Term).Count(words.Contains)))
            .Where(x => x.Shared > 0)
            .OrderByDescending(x => x.Shared).ThenBy(x => x.Placement.Term.Length)
            .Take(related)
            .Select(x => ShoppingPlacer.Resolve(x.Placement, locations, sections))
            .ToList();
        return new ShopLocated(term.Trim(), shop, place, near, ReferenceFiles(shop));
    }

    /// <summary>The files in <paramref name="shop"/>'s reference folder, relative to it, by name; empty when there is none.</summary>
    public IReadOnlyList<string> ReferenceFiles(ShopStore shop)
    {
        ArgumentNullException.ThrowIfNull(shop);
        string folder = _store().ReferenceFolder(shop);
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(ShoppingStore.Category, $"Could not list {folder}: {ex.Message}", ex);
            return [];
        }
    }

    /// <summary>
    /// Records where <paramref name="term"/> is at <paramref name="shop"/>: at the location labelled <paramref name="location"/> (made when
    /// the store has none by that label, in <paramref name="section"/> when given) or, with no location, in <paramref name="section"/>
    /// alone (made when missing). Every line of the list the term covers moves with it, since a line is placed when it is shown. The
    /// answer as a sentence; <c>Error: </c> leads a refusal.
    /// </summary>
    public string Place(ShopStore shop, string term, string kind, string? location, string? section, string source, string note = "")
    {
        ArgumentNullException.ThrowIfNull(shop);
        if (string.IsNullOrWhiteSpace(term))
        {
            return "Error: " + ShoppingText.NoTerm;
        }

        if (string.IsNullOrWhiteSpace(location) && string.IsNullOrWhiteSpace(section))
        {
            return "Error: " + ShoppingText.NoPlaceGiven;
        }

        var store = _store();
        var made = new List<string>();
        ShopSection? sectionRow = null;
        if (!string.IsNullOrWhiteSpace(section))
        {
            bool known = store.Sections(shop.Id).Any(s => string.Equals(s.Name, section.Trim(), StringComparison.OrdinalIgnoreCase));
            sectionRow = store.EnsureSection(shop.Id, section);
            if (sectionRow is null)
            {
                return "Error: " + ShoppingText.WriteFailed;
            }

            if (!known)
            {
                made.Add(ShoppingText.NewSection(sectionRow.Name));
            }
        }

        ShopLocation? locationRow = null;
        if (!string.IsNullOrWhiteSpace(location))
        {
            var existing = store.Locations(shop.Id).FirstOrDefault(l => string.Equals(l.Label, location.Trim(), StringComparison.OrdinalIgnoreCase));
            // A new location takes the section named with it; an existing one without a section takes it too, and keeps its own otherwise.
            locationRow = store.EnsureLocation(shop.Id, location, existing?.SectionId is null ? sectionRow?.Id : null);
            if (locationRow is null)
            {
                return "Error: " + ShoppingText.WriteFailed;
            }

            if (existing is null)
            {
                made.Add(ShoppingText.NewLocation(locationRow.Label));
            }
        }

        // The placement names its section only where it differs from the location's own (or has no location).
        long? sectionId = locationRow is null ? sectionRow?.Id : sectionRow is not null && locationRow.SectionId != sectionRow.Id ? sectionRow.Id : null;
        if (!store.Place(shop.Id, term, kind, locationRow?.Id, sectionId, source, note))
        {
            return "Error: " + ShoppingText.WriteFailed;
        }

        var place = ShoppingPlacer.Resolve(store.Placements(shop.Id).First(p => string.Equals(p.Term, term.Trim(), StringComparison.OrdinalIgnoreCase)), store.Locations(shop.Id), store.Sections(shop.Id));
        var moved = store.Items().Where(i => !i.Checked && PlaceOf(i.Name, shop).Placement?.Id == place.Placement!.Id).ToList();
        Audit(ShoppingText.AuditPlaced(shop, term.Trim(), kind, place, source));
        return ShoppingText.Placed(shop, term.Trim(), kind, place, source, made, moved);
    }

    /// <summary>Forgets where <paramref name="term"/> is at <paramref name="shop"/>. The answer as a sentence.</summary>
    public string Unplace(ShopStore shop, string term)
    {
        ArgumentNullException.ThrowIfNull(shop);
        if (!_store().Unplace(shop.Id, term))
        {
            return ShoppingText.NotPlaced(shop, term);
        }

        Audit(ShoppingText.AuditUnplaced(shop, term));
        return ShoppingText.Unplaced(shop, term);
    }

    /// <summary>
    /// A new store named <paramref name="name"/>, its reference folder made (<see cref="FolderFor"/>); the active store when it is the
    /// first. The answer as a sentence; <c>Error: </c> leads a refusal.
    /// </summary>
    public string AddStore(string name, string notes = "")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Error: " + ShoppingText.NoStoreName;
        }

        var store = _store();
        if (store.FindStore(name) is { } existing)
        {
            return "Error: " + ShoppingText.StoreExists(existing);
        }

        bool first = store.Stores().Count == 0;
        if (store.AddStore(name, FolderFor(name), notes) is not { } made)
        {
            return "Error: " + ShoppingText.WriteFailed;
        }

        if (first)
        {
            store.SetActiveStore(made.Id);
        }

        string folder = store.ReferenceFolder(made);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(ShoppingStore.Category, $"Could not make {folder}: {ex.Message}", ex);
        }

        Audit(ShoppingText.AuditStoreAdded(made));
        return ShoppingText.StoreAdded(made, folder, first);
    }

    /// <summary>
    /// <paramref name="shop"/> renamed (2026-10-11, the store manager; its folder follows the name since later that day, the user's ask):
    /// the folder name the new name makes (<see cref="FolderFor"/>, a number added when another store or a folder on disk has it) is
    /// given to the store's reference and cache folders, moved on disk, and to its row and its pages' picture paths, in one transaction
    /// (<see cref="ShoppingStore.RenameStore"/>). A name that makes the same folder moves nothing. A move that fails (the folder open
    /// in Explorer, a file in it in use) is put back and the store renamed with its folder as it was, the answer saying why; a database
    /// write that fails puts the folders back too. The answer as a sentence; <c>Error: </c> leads a refusal.
    /// </summary>
    public string RenameStore(ShopStore shop, string name)
    {
        ArgumentNullException.ThrowIfNull(shop);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Error: " + ShoppingText.NoStoreName;
        }

        var store = _store();
        string trimmed = name.Trim();
        if (store.FindStore(trimmed) is { } other && other.Id != shop.Id)
        {
            return "Error: " + ShoppingText.NameTaken("store", trimmed);
        }

        string wanted = FolderFor(trimmed);
        if (string.Equals(wanted, shop.Folder, StringComparison.OrdinalIgnoreCase))
        {
            return store.RenameStore(shop.Id, trimmed) ? Renamed(shop, trimmed, null, null) : "Error: " + ShoppingText.NameTaken("store", trimmed);
        }

        string cacheRoot = Path.Combine(store.Folder, ShoppingReference.CacheFolderName);
        string folder = wanted;
        for (int n = 2; Taken(folder); n++)
        {
            folder = wanted + "-" + n.ToString(CultureInfo.InvariantCulture);
        }

        if (string.Equals(folder, shop.Folder, StringComparison.OrdinalIgnoreCase))
        {
            return store.RenameStore(shop.Id, trimmed) ? Renamed(shop, trimmed, null, null) : "Error: " + ShoppingText.NameTaken("store", trimmed);
        }

        var moves = new List<(string From, string To)>
        {
            (Path.Combine(store.ReferenceRoot, shop.Folder), Path.Combine(store.ReferenceRoot, folder)),
            (Path.Combine(cacheRoot, shop.Folder), Path.Combine(cacheRoot, folder)),
        };
        var done = new List<(string From, string To)>();
        string? why = null;
        foreach (var (from, to) in moves.Where(m => Directory.Exists(m.From)))
        {
            try
            {
                Directory.Move(from, to);
                done.Add((from, to));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                why = ex.Message;
                break;
            }
        }

        if (why is null && store.RenameStore(shop.Id, trimmed, folder))
        {
            return Renamed(shop, trimmed, folder, null);
        }

        // Undone, newest first: the store keeps its folder, and only its name changes (when the name itself can).
        foreach (var (from, to) in Enumerable.Reverse(done))
        {
            try
            {
                Directory.Move(to, from);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn(ShoppingStore.Category, $"Could not move {to} back to {from}: {ex.Message}", ex);
            }
        }

        if (!store.RenameStore(shop.Id, trimmed))
        {
            return "Error: " + ShoppingText.NameTaken("store", trimmed);
        }

        return Renamed(shop, trimmed, null, why ?? ShoppingText.WriteFailed);

        // A folder name another store has, or one already on disk under reference or cache; never the store's own (2026-10-11 review:
        // target-2 renamed to a name making target, with target another store's, counted its own folder taken and moved to target-3).
        bool Taken(string candidate) =>
            !string.Equals(candidate, shop.Folder, StringComparison.OrdinalIgnoreCase)
            && (store.Stores().Any(s => s.Id != shop.Id && string.Equals(s.Folder, candidate, StringComparison.OrdinalIgnoreCase))
                || Directory.Exists(Path.Combine(store.ReferenceRoot, candidate))
                || Directory.Exists(Path.Combine(cacheRoot, candidate)));
    }

    /// <summary>A rename's answer, audited: the names, then the folder it moved to or why it stayed.</summary>
    private string Renamed(ShopStore shop, string name, string? folder, string? kept)
    {
        Audit(ShoppingText.AuditStoreRenamed(shop, name) + (folder is null ? "" : " (folder " + folder + ")"));
        return ShoppingText.Renamed("store", shop.Name, name)
            + (folder is not null ? " " + ShoppingText.FolderMoved(Path.Combine(_store().ReferenceRoot, folder))
               : kept is not null ? " " + ShoppingText.FolderKept(shop.Folder, kept)
               : "");
    }

    /// <summary>
    /// <paramref name="shop"/> removed with its layout and its pages' cached pictures (2026-10-11); its reference folder and the documents
    /// in it stay. The answer as a sentence; <c>Error: </c> leads a refusal. The caller asks first.
    /// </summary>
    public string RemoveStore(ShopStore shop)
    {
        ArgumentNullException.ThrowIfNull(shop);
        var store = _store();
        if (!store.RemoveStore(shop.Id))
        {
            return "Error: " + ShoppingText.WriteFailed;
        }

        Files.WorkingDirectory.DeleteDirectoryQuietly(Path.Combine(store.Folder, ShoppingReference.CacheFolderName, shop.Folder));
        Audit(ShoppingText.AuditStoreRemoved(shop));
        return ShoppingText.StoreRemoved(shop);
    }

    /// <summary>Makes <paramref name="shop"/> the active store. The answer as a sentence.</summary>
    public string UseStore(ShopStore shop)
    {
        ArgumentNullException.ThrowIfNull(shop);
        if (!_store().SetActiveStore(shop.Id))
        {
            return "Error: " + ShoppingText.WriteFailed;
        }

        Audit(ShoppingText.AuditStoreUsed(shop));
        return ShoppingText.StoreUsed(shop);
    }

    /// <summary>
    /// The places a place picker offers (the pane's ⌖ place, the layout editor's move): the locations in walk order, then the sections
    /// alone, each section left out when a location bears its name (2026-10-11, the user's catch: the import made <c>Dairy &amp; Eggs</c>
    /// both a location and the section it sits in, so the picker read <c>Dairy &amp; Eggs</c> and <c>Section: Dairy &amp; Eggs</c>, one
    /// place to the user). A fact on such a section alone still finds a row: <see cref="PlaceChoiceIndex"/> lands on the location.
    /// </summary>
    public static IReadOnlyList<(string Label, ShopLocation? Location, ShopSection? Section)> PlaceChoices(IReadOnlyList<ShopLocation> locations, IReadOnlyList<ShopSection> sections)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(sections);
        var named = locations.Select(l => l.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = new List<(string Label, ShopLocation? Location, ShopSection? Section)>();
        choices.AddRange(locations.Select(l => (ShoppingText.LocationChoice(l), (ShopLocation?)l, (ShopSection?)null)));
        choices.AddRange(sections.Where(s => !named.Contains(s.Name)).Select(s => (ShoppingText.SectionChoice(s), (ShopLocation?)null, (ShopSection?)s)));
        return choices;
    }

    /// <summary>
    /// The row of <see cref="PlaceChoices"/> a place is on: its location's, else its section's alone, else the location named as that
    /// section (the section's own row left out); -1 when none.
    /// </summary>
    public static int PlaceChoiceIndex(IReadOnlyList<(string Label, ShopLocation? Location, ShopSection? Section)> choices, IReadOnlyList<ShopSection> sections, long? locationId, long? sectionId)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(sections);
        var list = choices.ToList();
        if (locationId is { } l)
        {
            return list.FindIndex(c => c.Location?.Id == l);
        }

        if (sectionId is not { } s)
        {
            return -1;
        }

        int alone = list.FindIndex(c => c.Location is null && c.Section?.Id == s);
        string? name = sections.FirstOrDefault(x => x.Id == s)?.Name;
        return alone >= 0 || name is null ? alone : list.FindIndex(c => c.Location is { } at && string.Equals(at.Label, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A store's reference folder name made from its name: lower case letters and digits, every other run a hyphen, trimmed; <c>store</c>
    /// for a name with none. <c>H-E-B San Antonio #372</c> → <c>h-e-b-san-antonio-372</c>.
    /// </summary>
    public static string FolderFor(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string slug = NonWord().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "store" : slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }

    /// <summary>
    /// One typed line of the list split into its name and quantity: <c>2 milk</c>, <c>milk x2</c>, <c>milk ×2</c>, <c>2x milk</c> and
    /// <c>milk</c> (1). A quantity of 0 or over 999 is read as part of the name.
    /// </summary>
    public static (string Name, int Quantity) ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string text = line.Trim();
        var match = LeadingQuantity().Match(text);
        if (!match.Success)
        {
            match = TrailingQuantity().Match(text);
        }

        if (match.Success && int.TryParse(match.Groups["q"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int q) && q is >= 1 and <= 999 && match.Groups["name"].Value.Trim().Length > 0)
        {
            return (match.Groups["name"].Value.Trim(), q);
        }

        return (text, 1);
    }

    private static void Audit(string line) => DiagnosticLog.Info(ShoppingStore.Category, line);

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"^(?<q>\d{1,3})\s*[x×]?\s+(?<name>\D.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingQuantity();

    [GeneratedRegex(@"^(?<name>.*?\S)\s*[x×]\s*(?<q>\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingQuantity();
}
