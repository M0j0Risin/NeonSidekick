using NeonSidekick.Emojis;

namespace NeonSidekick.Tests;

/// <summary>The avatar picker's <c>&lt;home&gt;/configs/emoji.json</c> (2026-10-10; in <c>configs</c> since 2026-10-16): recent picks and the last skin tone.</summary>
public sealed class EmojiPrefsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "neon-emoji-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public void NoFile_IsNoHistory()
    {
        var prefs = EmojiPrefs.Load(_home);
        Assert.Empty(prefs.Recent);
        Assert.Equal(0, prefs.Tone);
    }

    [Fact]
    public void Picks_RoundTrip_NewestFirst_NoDuplicates()
    {
        var prefs = EmojiPrefs.Load(_home);
        prefs.Picked("🦊");
        prefs.Picked("🐙");
        prefs.Picked("👍🏽", tone: 3);
        prefs.Picked("🦊");
        Assert.Equal(["🦊", "👍🏽", "🐙"], prefs.Recent);

        var again = EmojiPrefs.Load(_home);
        Assert.Equal(["🦊", "👍🏽", "🐙"], again.Recent);
        Assert.Equal(3, again.Tone);
        Assert.True(File.Exists(Path.Combine(_home, "configs", EmojiPrefs.FileName)));   // the home's configs since 2026-10-16
        Assert.False(File.Exists(Path.Combine(_home, EmojiPrefs.FileName)));
    }

    [Fact]
    public void Recent_IsCapped()
    {
        var prefs = EmojiPrefs.Load(_home);
        foreach (var entry in EmojiCatalog.Shipped.Entries.Take(EmojiPrefs.RecentLimit + 5))
        {
            prefs.Picked(entry.Emoji);
        }

        Assert.Equal(EmojiPrefs.RecentLimit, EmojiPrefs.Load(_home).Recent.Count);
    }

    [Fact]
    public void ACorruptFile_IsNoHistory()
    {
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_home));
        File.WriteAllText(EmojiPrefs.PathFor(_home), "{ not json");
        Assert.Empty(EmojiPrefs.Load(_home).Recent);

        File.WriteAllText(EmojiPrefs.PathFor(_home), "{ \"recent\": [\"🦊\", \"word\", \"🦊\"], \"tone\": 9 }");
        var prefs = EmojiPrefs.Load(_home);
        Assert.Equal(["🦊"], prefs.Recent);
        Assert.Equal(5, prefs.Tone);
    }
}
