using NeonSidekick.Emojis;

namespace NeonSidekick.Tests;

/// <summary><c>/avatar</c> headless (2026-10-10): no picker, so words set the best match; it needs no LLM server.</summary>
public partial class SidekickAppTests
{
    [Fact]
    public async Task Headless_Avatar_SaysIt_SetsTheBestMatch_AndClearsIt_WithNoServer()
    {
        string output = await Headless("/avatar\n/avatar octopus\n/avatar\n/avatar zzqqxx\n/avatar clear\n/avatar clear\n");

        var catalog = EmojiCatalog.Shipped;
        Assert.Contains(EmojiText.Current("", catalog), output);
        Assert.Contains(EmojiText.Current("🐙", catalog), output);
        Assert.Contains("[error] " + EmojiText.NoEmojiFor("zzqqxx"), output);
        Assert.Contains(EmojiText.Cleared, output);
        Assert.Contains(EmojiText.AlreadyNone, output);
        Assert.Equal("", _settings.Current.ModelAvatar);
        Assert.Equal(["🐙"], EmojiPrefs.Load(_dir).Recent);
    }

    /// <summary><c>/avatar user</c> headless (2026-10-12, the user's ask): the user avatar said, set and cleared in its own words; the model's untouched.</summary>
    [Fact]
    public async Task Headless_AvatarUser_SaysSetsAndClearsTheUserAvatar_LeavingTheModels()
    {
        string output = await Headless("/avatar user\n/avatar user owl\n/avatar user\n/avatar user clear\n/avatar user clear\n/avatar model octopus\n");

        var catalog = EmojiCatalog.Shipped;
        Assert.Contains(EmojiText.UserCurrent("", catalog), output);
        Assert.Contains(EmojiText.UserCurrent("🦉", catalog), output);
        Assert.Contains(EmojiText.UserCleared, output);
        Assert.Contains(EmojiText.UserAlreadyNone, output);
        Assert.Contains(EmojiText.Current("🐙", catalog), output);
        Assert.Null(_settings.Current.UserAvatar);
        Assert.Equal("🐙", _settings.Current.ModelAvatar);
    }
}
