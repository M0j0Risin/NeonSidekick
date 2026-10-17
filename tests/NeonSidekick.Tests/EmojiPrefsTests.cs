using NeonSidekick.Emojis;

namespace NeonSidekick.Tests;

/// <summary>
/// The avatar picker's <c>emoji.json</c> (2026-10-10; in <c>configs</c> since 2026-10-16): recent picks and the last skin tone, per profile
/// since 2026-10-17 (the user's ask; the home's until then, no longer read). <c>_home</c> is the profile's folder these tests load.
/// </summary>
public sealed class EmojiPrefsTests : IDisposable
{
    private readonly string _root;
    private readonly string _home;

    public EmojiPrefsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "neon-emoji-" + Guid.NewGuid().ToString("N"));
        _home = NeonSidekick.Settings.Profiles.Directory(_root, "neon");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
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
        Assert.True(File.Exists(Path.Combine(_home, "configs", EmojiPrefs.FileName)));   // the profile's configs since 2026-10-17
        Assert.False(File.Exists(Path.Combine(_home, EmojiPrefs.FileName)));
    }

    /// <summary>Each profile its own (2026-10-17, the user's ask): a pick in one is not in another, and the home's old file is not read.</summary>
    [Fact]
    public void EachProfile_KeepsItsOwn_AndTheHomesFileIsNotRead()
    {
        string other = NeonSidekick.Settings.Profiles.Directory(_root, "work");
        EmojiPrefs.Load(_home).Picked("🦊", tone: 2);
        EmojiPrefs.Load(other).Picked("🐙");
        Assert.Equal(["🦊"], EmojiPrefs.Load(_home).Recent);
        Assert.Equal(2, EmojiPrefs.Load(_home).Tone);
        Assert.Equal(["🐙"], EmojiPrefs.Load(other).Recent);
        Assert.Equal(0, EmojiPrefs.Load(other).Tone);

        string homes = NeonSidekick.Settings.Profiles.HomeConfigPath(_root, EmojiPrefs.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(homes)!);
        File.WriteAllText(homes, "{ \"recent\": [\"🐸\"], \"tone\": 4 }");
        string fresh = NeonSidekick.Settings.Profiles.Directory(_root, "fresh");
        Assert.Empty(EmojiPrefs.Load(fresh).Recent);   // no migration: the old file is the user's to delete
        Assert.Equal(0, EmojiPrefs.Load(fresh).Tone);
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
