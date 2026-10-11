using System.Globalization;
using System.Text;

namespace NeonSidekick.Shopping;

/// <summary>
/// The shopping list's words (2026-10-11): what the <c>shopping_*</c> tools answer, the audit lines and the refusals. The
/// <see cref="YouTube.YouTubeText"/> shape: pure statics, one source for the tools and <c>/shopping</c>.
/// </summary>
public static class ShoppingText
{
    /// <summary>The multiplication sign before a quantity above one: <c>Soy milk ×2</c>.</summary>
    public const string Times = "×";

    /// <summary>The mark after a line placed by a guess. Pinned.</summary>
    public const string GuessMark = "(guess)";

    /// <summary>The heading over the lines no fact of the store places. Pinned.</summary>
    public const string UnplacedHeading = "Unplaced";

    /// <summary>The heading over the ticked lines. Pinned.</summary>
    public const string TickedHeading = "Ticked";

    public const string WriteFailed = "the shopping list could not be written (see /log)";
    public const string NoTerm = "name the product, brand or category to place";
    public const string NoPlaceGiven = "give a location (an aisle or zone such as \"Aisle 7\" or \"Dairy\"), a section, or both";
    public const string NoStoreName = "give the store a name";
    public const string NoItems = "name at least one item";

    /// <summary>The answer while the file cannot be opened (another schema, a locked or broken file). Pinned.</summary>
    public const string Unavailable = "Error: the shopping list is unavailable (" + ShoppingStore.FileName + " could not be opened; see /log).";

    /// <summary>A line's text: its name, <c>×n</c> above one, its note in brackets.</summary>
    public static string Line(ShopItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Name + (item.Quantity > 1 ? " " + Times + item.Quantity.ToString(CultureInfo.InvariantCulture) : "") + (item.Note.Length > 0 ? " (" + item.Note + ")" : "");
    }

    /// <summary>Where a place is, in words: <c>Aisle 2 (International Foods)</c>, <c>Dairy &amp; Eggs</c> for a section alone, <c>unplaced</c>.</summary>
    public static string Where(ShopPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (place.Location is { } location)
        {
            var section = place.Section;
            return location.Label + (section is not null && !string.Equals(section.Name, location.Label, StringComparison.OrdinalIgnoreCase) ? " (" + section.Name + ")" : "");
        }

        return place.Section?.Name ?? "unplaced";
    }

    /// <summary>A group's heading: the location with its section, the section alone, or <see cref="UnplacedHeading"/>.</summary>
    public static string Heading(ShopGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Location is { } location
            ? location.Label + (group.Section is { } s && !string.Equals(s.Name, location.Label, StringComparison.OrdinalIgnoreCase) ? " · " + s.Name : "")
            : group.Section?.Name ?? UnplacedHeading;
    }

    /// <summary>
    /// The list as the model reads it: the store, the count, each group under its heading in walk order, a guess marked, the ticked lines,
    /// and the likely duplicates. Plain text, one line per item.
    /// </summary>
    public static string List(ShopView view, bool withTicked = true)
    {
        ArgumentNullException.ThrowIfNull(view);
        int open = view.Groups.Sum(g => g.Items.Count);
        var sb = new StringBuilder();
        sb.Append("Shopping list").Append(view.Store is { } s ? " at " + s.Name : " (no store set up: lines in the order added)")
          .Append(": ").Append(Count(open, "item")).Append(" to get");
        if (view.Ticked.Count > 0)
        {
            sb.Append(", ").Append(view.Ticked.Count.ToString(CultureInfo.InvariantCulture)).Append(" ticked");
        }

        sb.Append('.');
        if (open == 0 && view.Ticked.Count == 0)
        {
            return sb.Append(" The list is empty.").ToString();
        }

        foreach (var group in view.Groups)
        {
            sb.Append('\n').Append(Heading(group));
            foreach (var placed in group.Items)
            {
                sb.Append("\n- ").Append(Line(placed.Item));
                if (placed.Place.IsGuess)
                {
                    sb.Append(' ').Append(GuessMark);
                }
            }
        }

        if (withTicked && view.Ticked.Count > 0)
        {
            sb.Append('\n').Append(TickedHeading);
            foreach (var item in view.Ticked)
            {
                sb.Append("\n- [x] ").Append(Line(item));
            }
        }

        foreach (var lines in view.Duplicates)
        {
            sb.Append("\nPossible duplicate: ").Append(string.Join(" and ", lines.Select(i => "\"" + Line(i) + "\""))).Append(" — offer to merge them.");
        }

        return sb.ToString();
    }

    /// <summary>What <c>shopping_add</c> did, one sentence per line: added or bumped, and where it is at the store (a guess said so).</summary>
    public static string Added(IReadOnlyList<ShopAdded> added, ShopStore? shop)
    {
        ArgumentNullException.ThrowIfNull(added);
        var sb = new StringBuilder();
        foreach (var a in added)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(a.Bumped ? "Already on the list; now " : "Added ").Append(Line(a.Item));
            if (shop is null)
            {
                sb.Append('.');
            }
            else if (a.Place.IsPlaced)
            {
                sb.Append(" — ").Append(Where(a.Place)).Append(a.Place.IsGuess ? " " + GuessMark : "").Append(" at ").Append(shop.Name).Append('.');
            }
            else
            {
                sb.Append(" — not placed at ").Append(shop.Name).Append(" yet.");
            }
        }

        if (shop is not null && added.Any(a => !a.Place.IsPlaced))
        {
            sb.Append('\n').Append(UnplacedHint);
        }

        return sb.ToString();
    }

    /// <summary>After an add that left a line unplaced: how to place it. Pinned (prompt text).</summary>
    public const string UnplacedHint =
        "To place an unplaced item: shopping_locate it (the store's facts and reference documents), then shopping_place what you find; " +
        "with nothing to go on, place your best guess with source \"guess\" and tell the user it is one.";

    /// <summary>A name that matches no line.</summary>
    public static string NotOnList(string text) => "Error: \"" + text.Trim() + "\" is not on the shopping list.";

    /// <summary>A name that matches several lines when one was meant.</summary>
    public static string Ambiguous(string text, IReadOnlyList<ShopItem> items) =>
        "Error: \"" + text.Trim() + "\" matches " + string.Join(", ", items.Select(i => "\"" + Line(i) + "\"")) + "; name one exactly.";

    public static string Checked(ShopItem item, bool on) => (on ? "Ticked " : "Unticked ") + Line(item) + ".";

    public static string Removed(ShopItem item) => "Removed " + Line(item) + " from the list.";

    /// <summary>What <c>shopping_clear</c> did.</summary>
    public static string Cleared(int count, bool all) =>
        count == 0 ? (all ? "The list was already empty." : "Nothing on the list is ticked.") : (all ? "Cleared the whole list (" : "Cleared the ticked lines (") + Count(count, "line") + ").";

    /// <summary>The question the pane asks before the whole list goes. Pinned.</summary>
    public static string ClearAllQuestion(int count) => "Clear the whole shopping list (" + Count(count, "line") + ")?";

    /// <summary>The whole list's clear declined, or asked with nowhere to ask. Pinned.</summary>
    public const string ClearAllDeclined = "Error: the user declined to clear the whole list; nothing was removed.";
    public const string ClearAllNoPane = "Error: clearing the whole list needs the user's yes on the pane, and there is none here; clear the ticked lines, or remove lines by name.";

    /// <summary>A store name no store has, with the ones there are.</summary>
    public static string NoSuchStore(string name, IReadOnlyList<ShopStore> stores) =>
        "Error: there is no store \"" + name.Trim() + "\"" + (stores.Count == 0 ? "; none is set up yet (shopping_layout add_store makes one)." : "; the stores are " + string.Join(", ", stores.Select(s => "\"" + s.Name + "\"")) + ".");

    /// <summary>A tool that needs a store, with none set up. Pinned (prompt text).</summary>
    public const string NoStore = "Error: no store is set up yet. Ask the user which store they shop at, then shopping_layout add_store with its name.";

    public static string StoreExists(ShopStore store) => "there is a store \"" + store.Name + "\" already";

    public static string StoreAdded(ShopStore store, string folder, bool active) =>
        "Added the store \"" + store.Name + "\"" + (active ? " (the active store)" : "") + "; its reference documents go in " + folder + ".";

    public static string StoreUsed(ShopStore store) => "\"" + store.Name + "\" is the active store; the list now walks through it.";

    public static string NewSection(string name) => "new section \"" + name + "\"";

    public static string NewLocation(string label) => "new location \"" + label + "\"";

    /// <summary>What <c>shopping_place</c> did: the fact, anything it made, and the lines it now places.</summary>
    public static string Placed(ShopStore shop, string term, string kind, ShopPlace place, string source, IReadOnlyList<string> made, IReadOnlyList<ShopItem> moved)
    {
        var sb = new StringBuilder();
        sb.Append("At ").Append(shop.Name).Append(", ").Append(kind).Append(" \"").Append(term).Append("\" is in ").Append(Where(place))
          .Append(source == ShoppingSources.Guess ? " " + GuessMark : "").Append('.');
        if (made.Count > 0)
        {
            sb.Append(" Made: ").Append(string.Join(", ", made)).Append('.');
        }

        if (moved.Count > 0)
        {
            sb.Append(" On the list there now: ").Append(string.Join(", ", moved.Select(Line))).Append('.');
        }

        return sb.ToString();
    }

    public static string NotPlaced(ShopStore shop, string term) => "Nothing places \"" + term.Trim() + "\" at " + shop.Name + ".";

    public static string Unplaced(ShopStore shop, string term) => "Forgot where \"" + term.Trim() + "\" is at " + shop.Name + ".";

    /// <summary>What <c>shopping_locate</c> found: where the list would put the term, the facts that share a word with it, and the store's reference files.</summary>
    public static string Located(ShopLocated found, string referenceFolder)
    {
        ArgumentNullException.ThrowIfNull(found);
        var sb = new StringBuilder();
        sb.Append('"').Append(found.Term).Append("\" at ").Append(found.Store.Name).Append(": ");
        if (found.Place.Placement is { } p)
        {
            sb.Append(Where(found.Place)).Append(" — by the ").Append(p.Kind).Append(" \"").Append(p.Term).Append("\" (").Append(p.Source).Append(')');
            if (p.Note.Length > 0)
            {
                sb.Append(", note: ").Append(p.Note);
            }

            sb.Append('.');
        }
        else
        {
            sb.Append("no fact places it.");
        }

        if (found.Related.Count > 0)
        {
            sb.Append("\nRelated facts: ").Append(string.Join("; ", found.Related.Select(r => r.Placement!.Kind + " \"" + r.Placement.Term + "\" → " + Where(r) + (r.IsGuess ? " " + GuessMark : ""))));
        }

        sb.Append('\n').Append(found.ReferenceFiles.Count == 0
            ? "No reference documents for this store (" + referenceFolder + " is empty)."
            : "Reference documents in " + referenceFolder + ": " + string.Join(", ", found.ReferenceFiles) + ".");
        return sb.ToString();
    }

    /// <summary>What <c>shopping_stores</c> lists: every store (the active one marked), and for one store its layout in walk order.</summary>
    public static string Stores(IReadOnlyList<ShopStore> stores, ShopStore? active)
    {
        ArgumentNullException.ThrowIfNull(stores);
        if (stores.Count == 0)
        {
            return "No store is set up yet.";
        }

        return "Stores: " + string.Join(", ", stores.Select(s => "\"" + s.Name + "\"" + (s.Id == active?.Id ? " (active)" : ""))) + ".";
    }

    /// <summary>One store's layout: its sections, its locations in walk order with their sections, how many facts it holds, its reference files.</summary>
    public static string Layout(ShopStore store, bool active, IReadOnlyList<ShopSection> sections, IReadOnlyList<ShopLocation> locations, int placements, string referenceFolder, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(store);
        var sb = new StringBuilder();
        sb.Append('"').Append(store.Name).Append('"').Append(active ? " (active)" : "");
        if (store.Notes.Length > 0)
        {
            sb.Append(" — ").Append(store.Notes);
        }

        sb.Append("\nWalk: ").Append(locations.Count == 0 ? "no locations yet" : string.Join(", ", locations.Select(l => l.Label + (l.SectionName is { } s && !string.Equals(s, l.Label, StringComparison.OrdinalIgnoreCase) ? " (" + s + ")" : ""))));
        sb.Append("\nSections: ").Append(sections.Count == 0 ? "none yet" : string.Join(", ", sections.Select(s => s.Name)));
        sb.Append("\nPlacement facts: ").Append(placements.ToString(CultureInfo.InvariantCulture));
        sb.Append("\nReference folder: ").Append(referenceFolder).Append(files.Count == 0 ? " (empty)" : " — " + string.Join(", ", files));
        return sb.ToString();
    }

    // ---- /shopping ----

    /// <summary><c>/shopping</c>'s row on <c>/help</c>. Pinned.</summary>
    public const string HelpSummary = "show and edit the shopping list";

    /// <summary><c>/shopping</c> given a line no form takes. Pinned.</summary>
    public const string CommandUsage =
        "/shopping takes add <items>, check|uncheck|remove <item>, list [<store>], clear [all], store [<name>|add <name>|remove <name>], export or print; alone, the list's pane.";

    /// <summary>The file <c>/shopping export</c> writes in the working directory, overwritten each time. Pinned.</summary>
    public const string ExportFileName = "shopping-list.md";

    /// <summary>The title a printed or exported list carries.</summary>
    public static string Title(ShopStore? store) => "Shopping list" + (store is null ? "" : " — " + store.Name);

    /// <summary>
    /// The list as Markdown (<c>/shopping export</c> and <c>print</c>): the title, then each group as a heading over its lines as
    /// unticked boxes, in the store's walk; the ticked lines are left out (bought). A guess keeps its mark.
    /// </summary>
    public static string Markdown(ShopView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var sb = new StringBuilder("# ").Append(Title(view.Store)).Append('\n');
        foreach (var group in view.Groups)
        {
            sb.Append("\n## ").Append(Heading(group)).Append("\n\n");
            foreach (var placed in group.Items)
            {
                sb.Append("- [ ] ").Append(Line(placed.Item)).Append(placed.Place.IsGuess ? " " + GuessMark : "").Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>What <c>/shopping export</c> did. Pinned.</summary>
    public static string Exported(string path, int count) => "Wrote " + Count(count, "item") + " to " + path + ".";

    /// <summary><c>/shopping export</c> or <c>print</c> with nothing left to buy. Pinned.</summary>
    public const string NothingToBuy = "Nothing on the list to buy.";

    /// <summary><c>/shopping print</c> where nothing prints. Pinned.</summary>
    public const string NoPrinting = "Printing needs Windows or a Mac.";

    /// <summary>The yes/no before <c>/shopping store remove</c>. Pinned.</summary>
    public static string RemoveStorePrompt(ShopStore store, int facts) =>
        "Remove the store \"" + store.Name + "\" with its layout (" + Count(facts, "placement fact") + ")? Its reference folder stays.";

    public static string StoreRemoved(ShopStore store) => "Removed the store \"" + store.Name + "\".";

    public static string AuditStoreRemoved(ShopStore store) => "Removed the store \"" + store.Name + "\"";

    // ---- the pane (/shopping) ----

    /// <summary>The pane's title: the toolbar's cart and the store the list walks through. Pinned.</summary>
    public static string PaneLabel(ShopStore? store) => "🛒 " + Title(store);

    /// <summary>The pane's hint row. Pinned.</summary>
    public const string PaneKeys = "Enter / Space = tick or untick · + / - = quantity · r = remove · c = clear ticked · s = store · Esc = close";

    /// <summary>The pane's buttons' titles and keys. Pinned.</summary>
    public const string MoreButton = "+1";
    public const char MoreKey = '+';
    public const string LessButton = "-1";
    public const char LessKey = '-';
    public const string RemoveButton = "remove";
    public const char RemoveKey = 'r';
    public const string ClearButton = "clear ticked";
    public const char ClearKey = 'c';
    public const string StoreButton = "store";
    public const char StoreKey = 's';

    /// <summary>The pane's empty list. Pinned.</summary>
    public const string EmptyList = "The shopping list is empty: /shopping add <items>, or ask the model.";

    /// <summary>The pane's row for a line: its box, its text, a guess marked.</summary>
    public static string PaneRow(ShopItem item, bool guess) => (item.Checked ? "[x] " : "[ ] ") + Line(item) + (guess ? " " + GuessMark : "");

    /// <summary>The pane's caption: the counts. Pinned.</summary>
    public static string PaneCaption(int open, int ticked) => Count(open, "item") + " to get" + (ticked > 0 ? ", " + ticked.ToString(CultureInfo.InvariantCulture) + " ticked" : "");

    /// <summary>The store picker's title. Pinned.</summary>
    public const string StorePickTitle = "Shop at";

    /// <summary>The store picker with no store. Pinned.</summary>
    public const string NoStoresYet = "No store is set up yet: /shopping store add <name>, or ask the model.";

    // ---- shopping_layout's answers ----

    public static string Renamed(string what, string from, string to) => "Renamed the " + what + " \"" + from.Trim() + "\" to \"" + to.Trim() + "\".";

    public static string NameTaken(string what, string name) => "there is a " + what + " \"" + name.Trim() + "\" already";

    public static string SectionAdded(ShopStore shop, ShopSection section) => "\"" + section.Name + "\" is a section of " + shop.Name + ".";

    public static string SectionRemoved(ShopStore shop, ShopSection section) => "Removed the section \"" + section.Name + "\" from " + shop.Name + "; its aisles and facts keep their places with no section.";

    public static string LocationAdded(ShopStore shop, ShopLocation location) =>
        "\"" + location.Label + "\" is a location of " + shop.Name + (location.SectionName is { } s ? " in " + s : "") + (location.Walk is { } w ? ", walked " + Ordinal(w) : "") + ".";

    public static string LocationRemoved(ShopStore shop, ShopLocation location) => "Removed \"" + location.Label + "\" from " + shop.Name + " with the facts that placed things there.";

    public static string WalkSet(ShopStore shop, ShopLocation location, int? walk) =>
        walk is { } w ? "\"" + location.Label + "\" is walked " + Ordinal(w) + " at " + shop.Name + "." : "\"" + location.Label + "\" is walked in its name's order at " + shop.Name + ".";

    public static string NoSuchSection(ShopStore shop, string name, IReadOnlyList<ShopSection> sections) =>
        "Error: " + shop.Name + " has no section \"" + name.Trim() + "\"" + (sections.Count == 0 ? "." : "; its sections are " + string.Join(", ", sections.Select(s => s.Name)) + ".");

    public static string NoSuchLocation(ShopStore shop, string name, IReadOnlyList<ShopLocation> locations) =>
        "Error: " + shop.Name + " has no location \"" + name.Trim() + "\"" + (locations.Count == 0 ? "." : "; its locations are " + string.Join(", ", locations.Select(l => l.Label)) + ".");

    private static string Ordinal(int n) => n.ToString(CultureInfo.InvariantCulture) + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    // ---- the audit lines (DiagnosticLog.Info under ShoppingStore.Category) ----

    public static string AuditAdded(ShopItem item) => "Added to the shopping list: " + Line(item);

    public static string AuditBumped(ShopItem item) => "Shopping list quantity: " + Line(item);

    public static string AuditChecked(ShopItem item, bool on) => (on ? "Ticked on the shopping list: " : "Unticked on the shopping list: ") + Line(item);

    public static string AuditRemoved(ShopItem item) => "Removed from the shopping list: " + Line(item);

    public static string AuditCleared(int count, bool all) => (all ? "Cleared the shopping list: " : "Cleared the ticked lines: ") + Count(count, "line");

    public static string AuditPlaced(ShopStore shop, string term, string kind, ShopPlace place, string source) =>
        "Placed at " + shop.Name + ": " + kind + " \"" + term + "\" in " + Where(place) + " (" + source + ")";

    public static string AuditUnplaced(ShopStore shop, string term) => "Unplaced at " + shop.Name + ": \"" + term.Trim() + "\"";

    public static string AuditStoreAdded(ShopStore store) => "Added the store \"" + store.Name + "\" (folder " + store.Folder + ")";

    public static string AuditStoreUsed(ShopStore store) => "Active store: \"" + store.Name + "\"";

    private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
}
