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
        Assert.Equal("", _settings.Current.Avatar);
        Assert.Equal(["🐙"], EmojiPrefs.Load(_dir).Recent);
    }
}
