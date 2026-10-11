using NeonSidekick.Emojis;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.Tests;

/// <summary>
/// The shipped emoji catalog (2026-10-10, the avatar picker): it parses whole, holds only what Windows Terminal draws, and every
/// emoji in it — toned forms too — measures two cells, the width the reply's glyph and the grid's cells are laid out by.
/// </summary>
public sealed class EmojiCatalogTests
{
    [Fact]
    public void TheShippedCatalog_ParsesWithNoProblem()
    {
        var catalog = EmojiCatalog.Shipped;
        Assert.Empty(catalog.Problems);
        Assert.Equal(1637, catalog.Entries.Count);   // Emoji 15.1 less the flags; docs/SETTINGS.md says so
        Assert.NotNull(catalog.Find("#️⃣"));   // a row that starts with #, not a comment
        Assert.Equal(
            ["Smileys & Emotion", "People & Body", "Animals & Nature", "Food & Drink", "Travel & Places", "Activities", "Objects", "Symbols", "Flags"],
            catalog.Groups.Select(g => g.Name));
    }

    [Fact]
    public void TheShippedCatalog_LeavesOutWhatWindowsDoesNotDraw()
    {
        var catalog = EmojiCatalog.Shipped;
        Assert.DoesNotContain(catalog.Entries, e => e.Group == "Component" || e.Subgroup is "country-flag" or "subdivision-flag");
        Assert.All(catalog.Entries, e => Assert.True(decimal.Parse(e.Version, System.Globalization.CultureInfo.InvariantCulture) <= 15.1m, e.Emoji + " " + e.Version));
        Assert.Null(catalog.Find("🇺🇸"));
        Assert.NotNull(catalog.Find("🏳️‍🌈"));
    }

    [Fact]
    public void EveryEmoji_AndEveryTone_IsOneTwoCellCluster()
    {
        foreach (var entry in EmojiCatalog.Shipped.Entries)
        {
            foreach (string emoji in entry.Tones.Prepend(entry.Emoji))
            {
                Assert.True(TextCells.Width(emoji) == 2, emoji + " " + entry.Name + " measures " + TextCells.Width(emoji));
                Assert.True(AvatarRules.IsEmoji(emoji), emoji + " " + entry.Name);
            }
        }
    }

    [Fact]
    public void Find_TakesAToneOrAMissingSelector_BackToItsEntry()
    {
        var catalog = EmojiCatalog.Shipped;
        var thumbs = catalog.Find("👍🏽");
        Assert.NotNull(thumbs);
        Assert.Equal("👍", thumbs.Value.Entry.Emoji);
        Assert.Equal(3, thumbs.Value.Tone);
        Assert.Equal("👍🏽", thumbs.Value.Entry.InTone(3));
        Assert.Equal("👍", thumbs.Value.Entry.InTone(0));

        // ☺ typed without its U+FE0F is the catalog's ☺️.
        Assert.Equal("☺️", catalog.Find("☺")?.Entry.Emoji);
        Assert.Equal("fox", catalog.Find("🦊")?.Entry.Name);
        Assert.Null(catalog.Find(""));
        Assert.Null(catalog.Find("fox"));
    }

    [Fact]
    public void Parse_LeavesOutABadRow_AndSaysWhy()
    {
        const string tsv = "# a comment\n"
            + "😀\tSmileys & Emotion\tface-smiling\tgrinning face\tgrin|happy\t1.0\t\n"
            + "😀\tSmileys & Emotion\tface-smiling\tagain\t\t1.0\t\n"
            + "👍\tPeople & Body\thand\tthumbs up\tyes\t0.6\t👍🏻 👍🏼\n"
            + "short\trow\n"
            + "🦊\tAnimals & Nature\tanimal-mammal\tfox\tanimal\t3.0\t\n";
        var catalog = EmojiCatalog.Parse(tsv);
        Assert.Equal(["😀", "🦊"], catalog.Entries.Select(e => e.Emoji));
        Assert.Equal([0, 1], catalog.Entries.Select(e => e.Order));
        Assert.Equal(["grin", "happy"], catalog.Entries[0].Keywords);
        Assert.Equal(
            [
                EmojiText.Problem(3, EmojiText.DuplicateRow("😀")),
                EmojiText.Problem(4, EmojiText.BadTones(2)),
                EmojiText.Problem(5, EmojiText.BadColumns(2)),
            ],
            catalog.Problems);
        Assert.Equal(["Smileys & Emotion", "Animals & Nature"], catalog.Groups.Select(g => g.Name));
    }

    [Theory]
    [InlineData("👍🏽", 2)]            // a skin tone draws inside its base
    [InlineData("☝🏽", 2)]            // on a one-cell base it makes the emoji two cells, as a U+FE0F does
    [InlineData("🧑🏽‍🍳", 2)]          // toned, then joined
    [InlineData("👨‍👩‍👧", 2)]
    [InlineData("🇺🇸", 2)]            // a flag: two regional indicators, one glyph
    [InlineData("🇺🇸🇬🇧", 4)]          // two flags
    [InlineData("#️⃣", 2)]            // a keycap: # and U+FE0F, the U+20E3 inside
    [InlineData("🏴󠁧󠁢󠁥󠁮󠁧󠁿", 2)]  // a subdivision flag's tags
    public void TheNewWidthRules(string text, int cells)
    {
        Assert.Equal(cells, TextCells.Width(text));
        Assert.Equal(cells, TextCells.ClusterWidth(text, 0, out int length) + (length < text.Length ? TextCells.Width(text[length..]) : 0));
    }

    [Fact]
    public void ACluster_KeepsItsToneAndKeycap()
    {
        Assert.Equal(2, TextCells.ClusterWidth("👍🏽x", 0, out int length));
        Assert.Equal("👍🏽".Length, length);
        Assert.Equal(2, TextCells.ClusterWidth("#️⃣x", 0, out length));
        Assert.Equal(3, length);
        Assert.Equal(2, TextCells.ClusterWidth("🇺🇸🇬🇧", 0, out length));
        Assert.Equal(4, length);
    }

    /// <summary>
    /// Spectre's own count of the clusters (Spectre.Console 0.57.2): what the pane's line splitter measures by, pinned so a Spectre
    /// update that changes it is seen. It counts a toned or joined cluster as the terminal does, two cells, so a grid line laid out
    /// by <see cref="TextCells"/> is never split by the pane — a one-cell base and its U+FE0F (⚙️) too.
    /// </summary>
    [Theory]
    [InlineData("🦊", 2)]
    [InlineData("👍🏽", 2)]
    [InlineData("🧑‍🍳", 2)]
    [InlineData("👨‍👩‍👧", 2)]
    [InlineData("⚙️", 2)]
    public void SpectresCount_IsPinned(string text, int cells)
    {
        Assert.Equal(cells, new Segment(text).CellCount());
    }
}
