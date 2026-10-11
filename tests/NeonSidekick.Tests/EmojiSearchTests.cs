using NeonSidekick.Emojis;

namespace NeonSidekick.Tests;

/// <summary>The avatar picker's search, its "For you" suggestions and what an avatar may be (2026-10-10).</summary>
public sealed class EmojiSearchTests
{
    private static IReadOnlyList<EmojiEntry> All => EmojiCatalog.Shipped.Entries;

    [Fact]
    public void AWholeName_ComesFirst()
    {
        Assert.Equal("🦊", EmojiSearch.Rank("fox", All)[0].Emoji);
        Assert.Equal("🐈", EmojiSearch.Rank("cat", All)[0].Emoji);
        Assert.Equal("🐱", EmojiSearch.Rank("cat face", All)[0].Emoji);
    }

    [Fact]
    public void EveryWord_MustMatch()
    {
        var results = EmojiSearch.Rank("red heart", All);
        Assert.Equal("❤️", results[0].Emoji);
        Assert.All(results, e => Assert.Contains(e, EmojiSearch.Rank("heart", All)));
        Assert.Empty(EmojiSearch.Rank("fox zzzzqq", All));
        Assert.Empty(EmojiSearch.Rank("  ", All));
    }

    [Fact]
    public void APlural_AndAPrefix_AndAKeyword_Match()
    {
        Assert.Contains(EmojiSearch.Rank("cats", All), e => e.Emoji == "🐈");
        Assert.Equal("👍", EmojiSearch.Rank("thumbs", All)[0].Emoji);   // as typed before its singular: not the crossed fingers' "thumb"
        Assert.Contains(EmojiSearch.Rank("octo", All), e => e.Emoji == "🐙");
        // "chef" is the cook's keyword, not its name.
        Assert.Equal("🧑‍🍳", EmojiSearch.Rank("chef", All)[0].Emoji);
    }

    [Fact]
    public void Ties_KeepCatalogOrder()
    {
        var results = EmojiSearch.Rank("face", All);
        Assert.True(results.Count > 50);
        Assert.Equal(results.Where(e => e.Name.StartsWith("face ", StringComparison.Ordinal)).Select(e => e.Order).Order(), results.Where(e => e.Name.StartsWith("face ", StringComparison.Ordinal)).Select(e => e.Order));
        Assert.Equal("😀", results.First(e => e.Group == "Smileys & Emotion").Emoji);
    }

    [Theory]
    [InlineData("🦊", "🦊")]
    [InlineData(" 👍🏽 ", "👍🏽")]
    [InlineData("#️⃣", "#️⃣")]
    [InlineData("⭐", "⭐")]
    [InlineData("🪿", "🪿")]
    [InlineData("fox", null)]
    [InlineData("🦊🦊", null)]
    [InlineData("猫", null)]
    [InlineData("#", null)]
    [InlineData("", null)]
    public void AQuery_ThatIsOneEmoji(string query, string? emoji)
    {
        Assert.Equal(emoji, EmojiSearch.AsEmoji(query));
    }

    [Theory]
    [InlineData("🦊", "🦊")]
    [InlineData(" 🦊 ", "🦊")]
    [InlineData("fox", "")]
    [InlineData("🦊🐙", "")]
    [InlineData(null, "")]
    public void AvatarRules_Normalize(string? avatar, string expected)
    {
        Assert.Equal(expected, AvatarRules.Normalize(avatar));
    }

    [Fact]
    public void Suggestions_FromTheName_ComeBeforeThePersonas()
    {
        var chef = EmojiSuggest.For("chef", null, All);
        Assert.Contains(chef, e => e.Emoji == "🧑‍🍳");
        Assert.True(chef.Count <= EmojiSuggest.Limit);

        var mixed = EmojiSuggest.For("ChefBot", "You love the ocean and octopus facts.", All);
        Assert.Equal("🧑‍🍳", mixed.First(e => e.Emoji is "🧑‍🍳" or "🐙").Emoji);
        Assert.Contains(mixed, e => e.Emoji == "🐙");
    }

    [Fact]
    public void Suggestions_GiveEveryWordItsTurn_NotOneWordTheWholeTab()
    {
        // The first look in Windows Terminal: "loves" filled the tab with hearts and pushed the rest past the cut.
        var suggested = EmojiSuggest.For("neon", "You are a friendly ship's cat who loves the ocean, fish and coffee.", All);
        Assert.Contains(suggested, e => e.Emoji == "☕");
        Assert.Contains(suggested, e => e.Emoji == "🐟");
        Assert.Contains(suggested, e => e.Emoji == "🐈");
        Assert.Contains(suggested, e => e.Emoji == "🚢");
        Assert.True(suggested.Count(e => e.Subgroup == "heart") < 10);
    }

    [Fact]
    public void Suggestions_IgnoreTheWordsEveryPersonaSays()
    {
        Assert.Empty(EmojiSuggest.For("neon", "You are a helpful assistant. Answer the user's questions.", All));
        Assert.Empty(EmojiSuggest.For("", "", All));
    }

    [Fact]
    public void AProfileName_SplitsIntoWords()
    {
        Assert.Equal(["chef", "bot", "home"], EmojiSuggest.SplitName("ChefBot-2_home"));
        Assert.Equal(["doug"], EmojiSuggest.SplitName("doug"));
    }
}
