using System.Globalization;
using System.Text.RegularExpressions;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Shopping;

/// <summary>What <see cref="ShoppingList.Add"/> did to one line: added it, or bumped the quantity of the line already there (unticking it).</summary>
public sealed record ShopAdded(ShopItem Item, bool Bumped, ShopPlace Place);

/// <summary>The list walked through a store (<paramref name="Store"/> null: none set up, the lines in the order added).</summary>
public sealed record ShopView(ShopStore? Store, IReadOnlyList<ShopGroup> Groups, IReadOnlyList<ShopItem> Ticked, IReadOnlyList<IReadOnlyList<ShopItem>> Duplicates);

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

        return new ShopView(shop, groups, ticked, ShoppingPlacer.Duplicates(items));
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
    /// The lines <paramref name="text"/> names: its number on the list (1-based, the order added), else every line with the same folded
    /// name, else every line whose name holds it at word boundaries. Empty for none.
    /// </summary>
    public IReadOnlyList<ShopItem> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var items = _store().Items();
        string trimmed = text.Trim().TrimStart('#');
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return number >= 1 && number <= items.Count ? [items[number - 1]] : [];
        }

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

    /// <summary>Takes <paramref name="item"/> off the list. False when the write failed.</summary>
    public bool Remove(ShopItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_store().RemoveItem(item.Id))
        {
            return false;
        }

        Audit(ShoppingText.AuditRemoved(item));
        return true;
    }

    /// <summary>Removes the ticked lines (every line with <paramref name="all"/>): how many went, null when the write failed.</summary>
    public int? Clear(bool all)
    {
        int? cleared = _store().Clear(all);
        if (cleared is > 0)
        {
            Audit(ShoppingText.AuditCleared(cleared.Value, all));
        }

        return cleared;
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
