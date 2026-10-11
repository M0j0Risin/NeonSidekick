using NeonSidekick.App;
using NeonSidekick.Emojis;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// General's Avatar row and its picker (2026-10-10, the user's ask): the row under Profile (under Memory mode since 2026-10-11, the user's order), the grid's search, a skin tone, the
/// actions, the other profiles' marks, ESC, and the row with no pane.
/// </summary>
public partial class SettingsMenuTests
{
    [Fact]
    public void TheAvatarRow_IsUnderMemoryMode_OnChat_ItsValueTheEmojiAndItsName_TheRobotByDefault()
    {
        Assert.Equal(SettingsField.Avatar, SettingsMenu.TabFields[(int)SettingsTab.Chat][3]);   // Profile, Working directory, Memory mode, Avatar (the user's order, 2026-10-11)
        Assert.Equal("Avatar", SettingsMenu.FieldName(SettingsField.Avatar));
        Assert.False(SettingsMenu.IsToggle(SettingsField.Avatar));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.Avatar));
        Assert.Equal("🤖", Emojis.AvatarRules.DefaultAvatar);   // the user's call, later on 2026-10-10 (none until then)
        Assert.Equal("🤖 robot", SettingsMenu.FieldValue(SettingsField.Avatar, new AppSettingsData(), "C:\\p"));
        Assert.Equal(SettingsMenu.NoAvatarLabel, SettingsMenu.FieldValue(SettingsField.Avatar, new AppSettingsData { Avatar = "" }, "C:\\p"));   // cleared: none
        Assert.Equal("🦊 fox", SettingsMenu.FieldValue(SettingsField.Avatar, new AppSettingsData { Avatar = "🦊" }, "C:\\p"));
        Assert.Equal("👍🏽 thumbs up: medium skin tone", SettingsMenu.FieldValue(SettingsField.Avatar, new AppSettingsData { Avatar = "👍🏽" }, "C:\\p"));
        Assert.Equal(SettingsMenu.NoAvatarLabel, SettingsMenu.FieldValue(SettingsField.Avatar, new AppSettingsData { Avatar = "fox" }, "C:\\p"));   // not an emoji: none
    }

    [Fact]
    public async Task OnThePane_TheAvatarPicker_TypingSearches_AndEnterSaves()
    {
        _console.Profile.Width = 120;
        var (menu, pane) = PaneMenu();
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Avatar, under Memory mode (2026-10-11): the picker
        _console.Input.PushText("fox");       // a search: the results, 🦊 first, under the cursor
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("🦊", _settings.Current.Avatar);
        Assert.Contains("  · Avatar: 🦊 fox", Output);
        Assert.Contains(EmojiText.Caption("fox", EmojiSearch.Rank("fox", EmojiCatalog.Shipped.Entries).Count, EmojiCatalog.Shipped.Entries.Count), Output);
        Assert.Contains(EmojiText.PreviewLine("🦊", "neon"), Output);
        Assert.Equal(["🦊"], EmojiPrefs.Load(_dir).Recent);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AnEmojiThatTakesATone_OpensItsTones_AndTheToneIsKept()
    {
        _console.Profile.Width = 120;
        var (menu, pane) = PaneMenu();
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
        _console.Input.PushText("thumbs up");
        Push(Keys.Enter);                     // 👍: its six tones, the cursor on none
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Enter, Keys.Escape);   // medium

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("👍🏽", _settings.Current.Avatar);
        Assert.Contains(EmojiText.ToneTitle, Output);
        Assert.Equal(3, EmojiPrefs.Load(_dir).Tone);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheNoneAction_ClearsTheAvatar()
    {
        _console.Profile.Width = 120;
        _settings.Update(d => d.Avatar = "🦊");
        var (menu, pane) = PaneMenu();
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the picker opens on Animals, the cursor on 🦊
        Push(Keys.Home, Keys.Right, Keys.Enter, Keys.Escape);   // 🎲, then 🚫

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("", _settings.Current.Avatar);
        Assert.Contains("  · Avatar: " + SettingsMenu.NoAvatarLabel, Output);
        Assert.Contains(EmojiText.GroupTab("Animals & Nature"), Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheRandomAction_LandsOnAResult_AndEnterTakesIt()
    {
        _console.Profile.Width = 120;
        var (menu, pane) = PaneMenu();
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
        _console.Input.PushText("animal");     // no animal takes a skin tone, so Enter takes the one landed on
        Push(Keys.Home, Keys.Enter);          // 🎲: the cursor onto a result
        Push(Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var found = EmojiCatalog.Shipped.Find(_settings.Current.Avatar);
        Assert.NotNull(found);
        Assert.Contains(found.Value.Entry, EmojiSearch.Rank("animal", EmojiCatalog.Shipped.Entries));
        Assert.All(EmojiSearch.Rank("animal", EmojiCatalog.Shipped.Entries), e => Assert.False(e.HasTones, e.Emoji));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AnotherProfilesAvatar_IsMarked_AndSaidUnderTheGrid()
    {
        _console.Profile.Width = 160;
        Profiles.Create(_dir, "ada", new AppSettingsData { Avatar = "🐙" });
        var (menu, pane) = PaneMenu();
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
        _console.Input.PushText("octopus");
        Push(Keys.Escape, Keys.Escape, Keys.Escape);   // the search cleared, the picker closed, the menu closed

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(Emojis.AvatarRules.DefaultAvatar, _settings.Current.Avatar);   // unchanged: the default 🤖
        Assert.Contains("used by ada", Output);
        Assert.Contains(SettingsMenu.UnchangedNotice, Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheRecentTab_HoldsTheAvatarsInUse_AndThePicks()
    {
        _console.Profile.Width = 160;
        Profiles.Create(_dir, "ada", new AppSettingsData { Avatar = "🐙" });
        EmojiPrefs.Load(_dir).Picked("🦊");
        var (menu, pane) = PaneMenu();
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
        Push(Keys.ShiftTab);                  // no For you for "neon" (a word every profile has): Smileys first, Recent before it
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(EmojiText.RecentTab, Output);
        Assert.Contains(EmojiText.InUseHeading, Output);
        pane.Dispose();
    }

    [Fact]
    public void AvatarCommand_ReadsItsArgument()
    {
        Assert.Equal((AvatarVerb.Show, ""), AvatarCommand.Parse(""));
        Assert.Equal((AvatarVerb.Clear, ""), AvatarCommand.Parse(" Clear "));
        Assert.Equal((AvatarVerb.Clear, ""), AvatarCommand.Parse("none"));
        Assert.Equal((AvatarVerb.Set, "🦊"), AvatarCommand.Parse(" 🦊"));
        Assert.Equal((AvatarVerb.Search, "red heart"), AvatarCommand.Parse("red heart"));
        Assert.Equal((AvatarVerb.Search, "🦊🐙"), AvatarCommand.Parse("🦊🐙"));
    }
}
