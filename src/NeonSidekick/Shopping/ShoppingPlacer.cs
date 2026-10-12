using System.Globalization;
using System.Text;

namespace NeonSidekick.Shopping;

/// <summary>Where an item is at a store, as <see cref="ShoppingPlacer.Place"/> worked it out: the placement that matched (null: unplaced), its location and section.</summary>
public sealed record ShopPlace(ShopPlacement? Placement, ShopLocation? Location, ShopSection? Section)
{
    public static readonly ShopPlace Unplaced = new(null, null, null);

    public bool IsPlaced => Placement is not null;

    /// <summary>Whether the placement is the model's guess (<see cref="ShoppingSources.Guess"/>), said so wherever it is shown.</summary>
    public bool IsGuess => Placement?.Source == ShoppingSources.Guess;
}

/// <summary>A line of the list with where it is at the store shown.</summary>
public sealed record PlacedItem(ShopItem Item, ShopPlace Place);

/// <summary>
/// One heading of the list walked through a store: a location (an aisle or a zone, its section beside it), a section alone (an item placed
/// in a department with no aisle), or the unplaced lines (<see cref="Location"/> and <see cref="Section"/> both null).
/// </summary>
public sealed record ShopGroup(ShopLocation? Location, ShopSection? Section, IReadOnlyList<PlacedItem> Items);

/// <summary>
/// The pure half of the shopping list (2026-10-11): where an item is at a store, and the order a store is walked in. What the old
/// skill told the model to do by hand is here in code — a brand outranks a category ("Goya guava nectar" is where Goya is, not where
/// juice is), the longest term wins among the rest, a term is matched at word boundaries (<c>ham</c> never places "shampoo"), plurals
/// fold (<c>eggs</c> is <c>egg</c>), a term with slashes is its alternatives (<c>Jam/Jelly</c>), and numbered aisles sort as numbers
/// (Aisle 2 before Aisle 10).
/// </summary>
public static class ShoppingPlacer
{
    /// <summary>
    /// Where <paramref name="name"/> is, from <paramref name="placements"/> (one store's): the placement whose term is the whole name
    /// first, else the best whose term is inside it — brands first, then the longest term, an item's before a category's. Its location
    /// and section (the location's section when the placement names none) are read from <paramref name="locations"/> and
    /// <paramref name="sections"/>. <see cref="ShopPlace.Unplaced"/> for none.
    /// </summary>
    public static ShopPlace Place(string name, IReadOnlyList<ShopPlacement> placements, IReadOnlyList<ShopLocation> locations, IReadOnlyList<ShopSection> sections)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(placements);
        var words = Words(name);
        if (words.Count == 0)
        {
            return ShopPlace.Unplaced;
        }

        ShopPlacement? best = null;
        (int Exact, int Brand, int Length, int Item) bestRank = default;
        foreach (var placement in placements)
        {
            if (placement.LocationId is null && placement.SectionId is null)
            {
                continue;
            }

            foreach (string alternative in Alternatives(placement.Term))
            {
                var term = Words(alternative);
                if (term.Count == 0 || IndexOf(words, term) < 0)
                {
                    continue;
                }

                var rank = (term.Count == words.Count ? 1 : 0, placement.Kind == ShoppingKinds.Brand ? 1 : 0, term.Sum(w => w.Length) + term.Count, placement.Kind == ShoppingKinds.Item ? 1 : 0);
                if (best is null || rank.CompareTo(bestRank) > 0)
                {
                    best = placement;
                    bestRank = rank;
                }
            }
        }

        return best is null ? ShopPlace.Unplaced : Resolve(best, locations, sections);
    }

    /// <summary>The location and section of <paramref name="placement"/>.</summary>
    public static ShopPlace Resolve(ShopPlacement placement, IReadOnlyList<ShopLocation> locations, IReadOnlyList<ShopSection> sections)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var location = placement.LocationId is { } l ? locations.FirstOrDefault(x => x.Id == l) : null;
        long? sectionId = placement.SectionId ?? location?.SectionId;
        var section = sectionId is { } s ? sections.FirstOrDefault(x => x.Id == s) : null;
        return new ShopPlace(placement, location, section);
    }

    /// <summary>
    /// <paramref name="items"/> walked through a store: one group per location in walk order (<see cref="WalkOrder"/>), then one per
    /// section holding items placed in it with no location (by name), then the unplaced. Each group's lines by name.
    /// </summary>
    public static IReadOnlyList<ShopGroup> Walk(IEnumerable<ShopItem> items, IReadOnlyList<ShopPlacement> placements, IReadOnlyList<ShopLocation> locations, IReadOnlyList<ShopSection> sections)
    {
        ArgumentNullException.ThrowIfNull(items);
        var placed = items.Select(i => new PlacedItem(i, Place(i.Name, placements, locations, sections))).ToList();
        var groups = new List<ShopGroup>();
        foreach (var location in WalkOrder(locations))
        {
            var here = placed.Where(p => p.Place.Location?.Id == location.Id).ToList();
            if (here.Count > 0)
            {
                groups.Add(new ShopGroup(location, sections.FirstOrDefault(s => s.Id == location.SectionId), ByName(here)));
            }
        }

        foreach (var section in sections.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var here = placed.Where(p => p.Place.Location is null && p.Place.Section?.Id == section.Id).ToList();
            if (here.Count > 0)
            {
                groups.Add(new ShopGroup(null, section, ByName(here)));
            }
        }

        var loose = placed.Where(p => p.Place.Location is null && p.Place.Section is null).ToList();
        if (loose.Count > 0)
        {
            groups.Add(new ShopGroup(null, null, ByName(loose)));
        }

        return groups;
    }

    /// <summary>
    /// Locations in the order a store is walked: those given a walk number by it, then the rest by label with numbers compared as
    /// numbers (<see cref="NaturalComparer"/>): Aisle 1, Aisle 2, … Aisle 10, then Bakery, Dairy.
    /// </summary>
    public static IEnumerable<ShopLocation> WalkOrder(IEnumerable<ShopLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        return locations.OrderBy(l => l.Walk is null).ThenBy(l => l.Walk ?? 0).ThenBy(l => l.Label, NaturalComparer.Instance);
    }

    /// <summary>
    /// The list's lines whose names are the same once folded (<see cref="Key"/>): likely duplicates, each group's lines oldest first;
    /// the list offers to merge them.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ShopItem>> Duplicates(IEnumerable<ShopItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.GroupBy(i => Key(i.Name), StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => (IReadOnlyList<ShopItem>)g.OrderBy(i => i.Id).ToList()).ToList();
    }

    /// <summary>
    /// A name folded for matching: lower case, accents off, every run of other than letters and digits a single space, each word's
    /// plural ending off (<see cref="Singular"/>). <c>Tortillas, Corn</c> and <c>tortilla corn</c> fold alike.
    /// </summary>
    public static string Key(string name) => string.Join(' ', Words(name));

    /// <summary>The words of <paramref name="text"/> as <see cref="Key"/> folds them.</summary>
    public static IReadOnlyList<string> Words(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (char raw in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark || raw == '\'' || raw == '’')
            {
                continue;
            }

            if (char.IsLetterOrDigit(raw))
            {
                word.Append(char.ToLowerInvariant(raw));
            }
            else if (word.Length > 0)
            {
                words.Add(Singular(word.ToString()));
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(Singular(word.ToString()));
        }

        return words;
    }

    /// <summary>
    /// A word folded to one form for singular and plural alike, near enough for a shopping list: <c>boxes</c> → <c>box</c>,
    /// <c>tomatoes</c> → <c>tomato</c>, <c>eggs</c> → <c>egg</c>. A <c>y</c> after a consonant and an <c>ies</c> both fold to <c>ie</c>, so
    /// <c>berry</c> and <c>berries</c> meet, and <c>cookie</c> and <c>cookies</c> too. It is a key, not a word to show. A word of three letters or
    /// fewer, or ending in <c>ss</c>/<c>us</c>/<c>is</c>, is left.
    /// </summary>
    public static string Singular(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (word.Length > 3 && word[^1] == 'y' && !"aeiouy".Contains(word[^2], StringComparison.Ordinal))
        {
            return word[..^1] + "ie";
        }

        if (word.Length <= 3 || !word.EndsWith('s') || word.EndsWith("ss", StringComparison.Ordinal) || word.EndsWith("us", StringComparison.Ordinal) || word.EndsWith("is", StringComparison.Ordinal))
        {
            return word;
        }

        if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4)
        {
            return word[..^1];
        }

        if (word.EndsWith("oes", StringComparison.Ordinal) || word.EndsWith("xes", StringComparison.Ordinal) || word.EndsWith("ches", StringComparison.Ordinal) || word.EndsWith("shes", StringComparison.Ordinal) || word.EndsWith("sses", StringComparison.Ordinal))
        {
            return word[..^2];
        }

        return word[..^1];
    }

    /// <summary>A term's alternatives: <c>Jam/Jelly</c> is <c>Jam</c> and <c>Jelly</c>; a term without a slash is itself.</summary>
    public static IEnumerable<string> Alternatives(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return term.Contains('/', StringComparison.Ordinal) ? term.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [term];
    }

    /// <summary>Whether <paramref name="term"/> (folded) is inside <paramref name="name"/> (folded) at word boundaries.</summary>
    public static bool Contains(string name, string term) => Alternatives(term).Any(a => Words(a) is { Count: > 0 } t && IndexOf(Words(name), t) >= 0);

    private static int IndexOf(IReadOnlyList<string> words, IReadOnlyList<string> term)
    {
        for (int i = 0; i + term.Count <= words.Count; i++)
        {
            int j = 0;
            while (j < term.Count && words[i + j] == term[j])
            {
                j++;
            }

            if (j == term.Count)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<PlacedItem> ByName(IEnumerable<PlacedItem> items) => items.OrderBy(p => p.Item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Item.Id).ToList();
}

/// <summary>Compares labels with their digit runs as numbers (Aisle 2 before Aisle 10), the rest without case. Pure.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsDigit(y[j]))
                {
                    j++;
                }

                string a = x[si..i].TrimStart('0'), b = y[sj..j].TrimStart('0');
                int byLength = a.Length.CompareTo(b.Length);
                if (byLength != 0)
                {
                    return byLength;
                }

                int byDigits = string.CompareOrdinal(a, b);
                if (byDigits != 0)
                {
                    return byDigits;
                }

                continue;
            }

            int byChar = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
            if (byChar != 0)
            {
                return byChar;
            }

            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
