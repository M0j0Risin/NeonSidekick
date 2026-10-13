using System.Globalization;

namespace NeonSidekick.Emojis;

/// <summary>The emoji catalog's and the avatar picker's wording (2026-10-10), kept here so the tests can pin it.</summary>
public static class EmojiText
{
    // ── The catalog's problems ──────────────────────────────────────────────

    public const string Missing = "The emoji catalog is not in this build; the avatar picker has no emoji to show.";

    public const string EmptyField = "an empty emoji, group, subgroup or name";

    public static string BadColumns(int count) => "7 tab-separated fields expected, " + count.ToString(CultureInfo.InvariantCulture) + " found";

    public static string BadTones(int count) => "5 skin tones or none expected, " + count.ToString(CultureInfo.InvariantCulture) + " found";

    public static string DuplicateRow(string emoji) => emoji + " is listed twice";

    public static string Problem(int line, string problem) => "emoji.tsv line " + line.ToString(CultureInfo.InvariantCulture) + ": " + problem;

    public static string PrefsUnreadable(string path, string why) => "Could not read " + path + " (the picker's recent emoji): " + why;

    public static string PrefsUnsaved(string path, string why) => "Could not save " + path + " (the picker's recent emoji): " + why;

    // ── The groups ──────────────────────────────────────────────────────────

    /// <summary>
    /// A Unicode group's tab on the picker's strip: its picture and a short word (<c>😀 Smileys</c>); a group this list does not know
    /// (a newer catalog) keeps its own name.
    /// </summary>
    public static string GroupTab(string group) => group switch
    {
        "Smileys & Emotion" => "😀 Smileys",
        "People & Body" => "👋 People",
        "Animals & Nature" => "🐻 Animals",
        "Food & Drink" => "🍔 Food",
        "Travel & Places" => "✈️ Travel",
        "Activities" => "⚽ Activities",
        "Objects" => "💡 Objects",
        "Symbols" => "🔣 Symbols",
        "Flags" => "🏁 Flags",
        _ => group,
    };

    /// <summary>
    /// A subgroup's heading in the grid: Unicode's slug in words, the first capitalised (<c>animal-mammal</c> → <c>Animal mammal</c>,
    /// <c>face-hand</c> → <c>Face hand</c>). Pure.
    /// </summary>
    public static string Heading(string subgroup)
    {
        ArgumentNullException.ThrowIfNull(subgroup);
        string words = subgroup.Replace('-', ' ').Trim();
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    // ── The picker ──────────────────────────────────────────────────────────

    public const string ForYouTab = "⭐ For you";
    public const string RecentTab = "🕘 Recent";
    public const string InUseHeading = "In use";
    public const string RecentHeading = "Recent";
    public const string ActionsHeading = "Actions";
    public const string ResultsHeading = "Results";
    public const string ForYouHeading = "From the profile's name and persona";
    public const string NothingRecent = "Nothing picked yet: the emoji you pick show here.";
    public const string NoMatch = "No emoji matches; Backspace edits, ESC clears.";

    /// <summary>The action that picks at random. Pinned.</summary>
    public const string RandomGlyph = "🎲";

    /// <summary>The action that clears the avatar. Pinned.</summary>
    public const string NoneGlyph = "🚫";

    public const string RandomFooter = "🎲  Random: an emoji at random from this tab, or from the results of what is typed.";
    public const string NoneFooter = "🚫  No avatar: replies open with ● again and the window title shows the name alone.";

    /// <summary>The user avatar's 🚫 band (2026-10-12): the sent lines' own glyph back. Pinned.</summary>
    public const string UserNoneFooter = "🚫  No avatar: your lines open with › again.";

    /// <summary>The picker's hint row while nothing is typed (<see cref="UI.MenuFilter.Hint"/> swaps its end while a search is). Pinned.</summary>
    public const string Keys = "arrows = move · Tab = category · Enter = pick · " + UI.MenuFilter.TypeAndKeepKeys;

    /// <summary>The tone grid's hint row. Pinned.</summary>
    public const string ToneKeys = "←/→ = tone · Enter = pick · ESC = back";

    /// <summary>The tone grid's title after the crumb.</summary>
    public const string ToneTitle = "Skin tone";

    /// <summary>The draft line's emoji picker's title (2026-10-11, <c>SettingsMenu.PickEmojiAsync</c>).</summary>
    public const string PickerTitle = "😀 Emoji";

    /// <summary>The tone names, 0 (none) to 5.</summary>
    public static readonly IReadOnlyList<string> ToneNames = ["no skin tone", "light skin tone", "medium-light skin tone", "medium skin tone", "medium-dark skin tone", "dark skin tone"];

    /// <summary>The filter's caption: what was typed and how many of the catalog match (<c>Filter: fox · 6 of 1,637</c>).</summary>
    public static string Caption(string filter, int shown, int total) =>
        "Filter: " + filter + " · " + shown.ToString("N0", CultureInfo.InvariantCulture) + " of " + total.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The footer's first line: the emoji, its name and where it lives (<c>🦊  fox · Animals &amp; Nature › Animal mammal</c>).</summary>
    public static string NameLine(string emoji, EmojiEntry? entry) =>
        entry is null ? emoji + "  " + NotInCatalog : emoji + "  " + entry.Name + " · " + entry.Group + " › " + Heading(entry.Subgroup);

    public const string NotInCatalog = "an emoji the catalog does not list";

    /// <summary>The footer's keyword line; empty for none.</summary>
    public static string KeywordLine(EmojiEntry? entry) =>
        entry is null || entry.Keywords.Count == 0 ? "" : "keywords: " + string.Join(", ", entry.Keywords);

    /// <summary>The footer's code line: the code points, the version, and whose avatar it is (<c>U+1F98A · Emoji 3.0 · current · used by ada</c>).</summary>
    public static string CodeLine(string emoji, EmojiEntry? entry, bool current, IReadOnlyList<string> usedBy)
    {
        var parts = new List<string> { CodePoints(emoji) };
        if (entry is not null)
        {
            parts.Add("Emoji " + entry.Version);
        }

        if (current)
        {
            parts.Add("this profile's avatar");
        }

        if (usedBy.Count > 0)
        {
            parts.Add("used by " + string.Join(", ", usedBy));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The footer's preview: the window title and a reply as they would read (<c>Title: 🦊 neon   Reply: 🦊 Hello…</c>).</summary>
    public static string PreviewLine(string emoji, string profile) => "Title: " + emoji + " " + profile + "   Reply: " + emoji + " Hello…";

    /// <summary>The user avatar's preview (2026-10-12): a sent line as it would read (<c>You: 🦊 Hello…</c>).</summary>
    public static string UserPreviewLine(string emoji) => "You: " + emoji + " Hello…";

    /// <summary>The footer's tone line for an emoji that takes one.</summary>
    public const string ToneLine = "Enter opens its skin tones.";

    /// <summary><paramref name="emoji"/>'s code points, <c>U+1F98A U+200D …</c>.</summary>
    public static string CodePoints(string emoji)
    {
        var points = new List<string>();
        for (int i = 0; i < emoji.Length; i += char.IsSurrogatePair(emoji, i) ? 2 : 1)
        {
            points.Add("U+" + char.ConvertToUtf32(emoji, i).ToString("X4", CultureInfo.InvariantCulture));
        }

        return string.Join(" ", points);
    }

    /// <summary>An avatar's value on the settings row and in notices: the emoji and its name (<c>🦊 fox</c>), or the emoji alone outside the catalog.</summary>
    public static string Value(string avatar, EmojiCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return avatar.Length == 0 ? "" : catalog.Find(avatar) is { } found ? avatar + " " + found.Entry.Name + (found.Tone > 0 ? ": " + ToneNames[found.Tone] : "") : avatar;
    }

    /// <summary><c>/avatar</c>'s refusal of a value that is not one emoji.</summary>
    public static string NotAnEmoji(string text) =>
        "Not one emoji: \"" + text + "\". An avatar is a single emoji, two cells wide; /avatar opens the picker.";

    /// <summary>A bare <c>/avatar</c> where no picker can open (headless). "Model avatar" since 2026-10-12, the setting's new name.</summary>
    public static string Current(string avatar, EmojiCatalog catalog) =>
        avatar.Length == 0 ? "No model avatar. /avatar <emoji> sets one; /avatar clear clears it." : "Model avatar: " + Value(avatar, catalog);

    /// <summary>Headless <c>/avatar &lt;words&gt;</c> when nothing matches them.</summary>
    public static string NoEmojiFor(string words) => "No emoji matches \"" + words + "\"; try other words, or paste the emoji.";

    public const string Cleared = "Model avatar cleared.";

    public const string AlreadyNone = "No model avatar to clear.";
}
