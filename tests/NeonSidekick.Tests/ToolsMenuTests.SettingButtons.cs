using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The setting buttons of the File, Image, Print, Screen capture, Obsidian and YouTube pages (2026-10-09, the
/// user's ask: actions at the top to the group's own settings): each opens its setting as the tab's row does, and ESC there comes
/// back to the page with the values read again. 🙋 Ask's switch page (<c>/tools ask</c>) the same day.
/// </summary>
public partial class ToolsMenuTests
{
    [Fact]
    public void SettingButtons_Wording_IsPinned()
    {
        var data = new AppSettingsData();
        string dir = _settings.ProfileDirectory;
        string Titles(SettingsField toolSwitch) => string.Join(" · ", SettingsMenu.SettingButtons(SettingsMenu.SettingButtonsFor(toolSwitch)!, data, dir).Select(b => b.Title));
        string KeysOf(SettingsField toolSwitch) => string.Concat(SettingsMenu.SettingButtonsFor(toolSwitch)!.Select(b => b.Key));

        Assert.Equal("≡ browser (hide-hidden)", Titles(SettingsField.FileTools));
        Assert.Equal("b", KeysOf(SettingsField.FileTools));
        Assert.Equal("≡ edit mode (beside-original)", Titles(SettingsField.ImageTools));   // File tools' until 2026-10-10, with the picture tools
        Assert.Equal("e", KeysOf(SettingsField.ImageTools));
        Assert.Equal("≡ printer (" + SettingsMenu.WindowsDefaultPrinterLabel.Trim('(', ')') + ")", Titles(SettingsField.PrintTools));
        Assert.Equal("p", KeysOf(SettingsField.PrintTools));
        Assert.Equal("≡ ask (ask) · ≡ preview (on)", Titles(SettingsField.ScreenTools));
        Assert.Equal("ap", KeysOf(SettingsField.ScreenTools));
        Assert.Equal("≡ vault (none)", Titles(SettingsField.ObsidianTools));
        Assert.Equal("v", KeysOf(SettingsField.ObsidianTools));
        Assert.Equal("≡ max (8) · ≡ voice (pause) · ≡ order (relevance) · ≡ safe (moderate)", Titles(SettingsField.YouTubeTools));
        Assert.Equal("mvos", KeysOf(SettingsField.YouTubeTools));
        Assert.Equal("≡ advisors (none)", Titles(SettingsField.Advisors));   // 2026-10-12, the user's ask: Advisor list from /tools advisors
        Assert.Equal("a", KeysOf(SettingsField.Advisors));
        var two = new AppSettingsData { AdvisorList = [new("ada", "critic"), new("bob", "")] };
        Assert.Equal([new MenuButton("≡ advisors (2)", 'a', true)], SettingsMenu.SettingButtons(SettingsMenu.SettingButtonsFor(SettingsField.Advisors)!, two, dir));   // the count alone
        Assert.Null(SettingsMenu.SettingButtonsFor(SettingsField.DockerTools));
        Assert.Null(SettingsMenu.SettingButtonsFor(SettingsField.AskUser));

        // Lit while the setting is not its default, as the tab's row wears the accent; the vault by its folder's name alone.
        Assert.All(SettingsMenu.SettingButtons(SettingsMenu.SettingButtonsFor(SettingsField.YouTubeTools)!, data, dir), b => Assert.False(b.On));
        var changed = new AppSettingsData { FileBrowserMode = "show-hidden", ObsidianVault = Path.Combine(dir, "notes", "Vault") + Path.DirectorySeparatorChar };
        Assert.Equal([true], SettingsMenu.SettingButtons(SettingsMenu.SettingButtonsFor(SettingsField.FileTools)!, changed, dir).Select(b => b.On));
        Assert.Equal([false], SettingsMenu.SettingButtons(SettingsMenu.SettingButtonsFor(SettingsField.ImageTools)!, changed, dir).Select(b => b.On));
        Assert.Equal([new MenuButton("≡ vault (Vault)", 'v', true)], SettingsMenu.SettingButtons(SettingsMenu.SettingButtonsFor(SettingsField.ObsidianTools)!, changed, dir));

        Assert.Equal("Enter = choose · b = browser · ESC = back", SettingsMenu.SettingButtonKeys(SettingsMenu.SettingButtonsFor(SettingsField.FileTools)!));
        Assert.Equal("Enter = choose · e = edit mode · ESC = back", SettingsMenu.SettingButtonKeys(SettingsMenu.SettingButtonsFor(SettingsField.ImageTools)!));
        Assert.Equal("Enter = choose · m = max · v = voice · o = order · s = safe · ESC = back", SettingsMenu.SettingButtonKeys(SettingsMenu.SettingButtonsFor(SettingsField.YouTubeTools)!));
    }

    /// <summary>Every page with setting buttons carries them on its title row and its keys in the hint; ESC leaves the switch as it was.</summary>
    [Theory]
    [InlineData(SettingsField.FileTools)]
    [InlineData(SettingsField.ImageTools)]
    [InlineData(SettingsField.PrintTools)]
    [InlineData(SettingsField.ScreenTools)]
    [InlineData(SettingsField.ObsidianTools)]
    [InlineData(SettingsField.YouTubeTools)]
    [InlineData(SettingsField.Advisors)]
    public async Task ShowSwitch_AGroupWithSettings_HasItsSettingButtons(SettingsField toolSwitch)
    {
        var (menu, pane, _) = PaneMenu();
        bool was = SettingsMenu.IsOn(toolSwitch, _settings.Current);
        Push(Keys.Escape);

        await menu.ShowSwitchAsync(toolSwitch, CancellationToken.None);

        var buttons = SettingsMenu.SettingButtonsFor(toolSwitch)!;
        string titles = string.Join(" · ", SettingsMenu.SettingButtons(buttons, _settings.Current, _settings.ProfileDirectory).Select(b => b.Title));
        Assert.Contains("\n" + Titled(ToolsText.Label + " › " + SettingsMenu.FieldName(toolSwitch) + " │ " + titles + " ") + "\n", Output);
        Assert.Contains(SettingsMenu.SettingButtonKeys(buttons), Output);
        Assert.Equal(was, SettingsMenu.IsOn(toolSwitch, _settings.Current));
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>B opens File browser/tree mode's picker; a pick saves, the page comes back with the new value lit, and no "unchanged" follows.</summary>
    [Fact]
    public async Task ShowSwitch_Files_TheBrowserButton_PicksTheMode_AndThePageComesBack()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('b'), Keys.Down, Keys.Enter, Keys.Escape);   // the button, show-hidden picked, back on the page, out

        await menu.ShowSwitchAsync(SettingsField.FileTools, CancellationToken.None);

        Assert.Equal("show-hidden", _settings.Current.FileBrowserMode);
        string before = "\n" + Titled(ToolsText.Label + " › File tools │ ≡ browser (hide-hidden) ") + "\n";
        string picker = ToolsText.Label + " › " + SettingsMenu.FieldName(SettingsField.FileBrowserMode);
        string after = "\n" + Titled(ToolsText.Label + " › File tools │ ≡ browser (show-hidden) ") + "\n";
        Assert.True(Output.IndexOf(before, StringComparison.Ordinal) < Output.IndexOf(picker, StringComparison.Ordinal), Output);
        Assert.True(Output.IndexOf(picker, StringComparison.Ordinal) < Output.LastIndexOf(after, StringComparison.Ordinal), Output);
        Assert.Contains("  · File browser/tree mode: show-hidden\n", Output);
        Assert.DoesNotContain("  · " + SettingsMenu.UnchangedNotice + "\n", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>M on the YouTube page is a typed value under its own row; the count saves and the button shows it.</summary>
    [Fact]
    public async Task ShowSwitch_YouTube_TheMaxButton_TypesTheCount()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('m'), Keys.Ctrl(ConsoleKey.A), Keys.Backspace);
        _console.Input.PushText("12");
        Push(Keys.Enter, Keys.Escape);

        await menu.ShowSwitchAsync(SettingsField.YouTubeTools, CancellationToken.None);

        Assert.Equal(12, _settings.Current.YouTubeSearchMaxResults);
        Assert.Contains(ToolsText.Label + " › " + SettingsMenu.FieldName(SettingsField.YouTubeSearchMaxResults), Output);   // its own one-row page
        Assert.Contains("≡ max (12)", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>P on the Screen capture page is Screen capture preview's own on/off page; ESC there and on the page leaves both unchanged.</summary>
    [Fact]
    public async Task ShowSwitch_Screen_ThePreviewButton_OpensItsOnOffPage()
    {
        _settings.Update(d => d.ScreenPreview = true);
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('p'), Keys.Down, Keys.Enter, Keys.Escape);   // the button, off picked, back on the page, out

        await menu.ShowSwitchAsync(SettingsField.ScreenTools, CancellationToken.None);

        Assert.False(_settings.Current.ScreenPreview);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › " + SettingsMenu.FieldName(SettingsField.ScreenPreview)) + "\n", Output);
        Assert.Contains("≡ preview (off)", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>V on the Obsidian page opens the vault's folder picker; the folder picked is saved when it holds .obsidian.</summary>
    [Fact]
    public async Task ShowSwitch_Obsidian_TheVaultButton_OpensTheFolderPicker()
    {
        string vault = Path.Combine(_settings.ProfileDirectory, "MyVault");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidian"));
        string? openedOn = null;
        var (menu, pane, _) = PaneMenu(browseVault: (on, _) => { openedOn = on; return Task.FromResult<string?>(vault); });
        Push(Keys.Char('v'), Keys.Escape);

        await menu.ShowSwitchAsync(SettingsField.ObsidianTools, CancellationToken.None);

        Assert.Equal("", openedOn);
        Assert.Equal(vault, _settings.Current.ObsidianVault);
        Assert.Contains("≡ vault (MyVault)", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>
    /// A on the Advisors enabled page (2026-10-12, the user's ask: an advisors button to Advisor list's pane, the toolbar's 🦉 page):
    /// the list's own pane under its name, ESC there back to the page, the switch as it was.
    /// </summary>
    [Fact]
    public async Task ShowSwitch_Advisors_TheAdvisorsButton_OpensTheAdvisorList()
    {
        var (menu, pane, _) = PaneMenu();
        bool was = _settings.Current.Advisors;
        Push(Keys.Char('a'), Keys.Escape, Keys.Escape);   // the button, out of the list, out of the page

        await menu.ShowSwitchAsync(SettingsField.Advisors, CancellationToken.None);

        int page = Output.IndexOf(ToolsText.Label + " › " + SettingsMenu.FieldName(SettingsField.Advisors) + " │ ≡ advisors (none) ", StringComparison.Ordinal);
        int list = Output.IndexOf(ToolsText.Label + " › " + SettingsMenu.FieldName(SettingsField.AdvisorList), StringComparison.Ordinal);
        Assert.True(page >= 0 && list > page, Output);
        Assert.Contains(SettingsMenu.AddAdvisorRow, Output);
        Assert.True(Output.LastIndexOf("≡ advisors (none) ", StringComparison.Ordinal) > list, Output);   // the page again after the list
        Assert.Equal(was, _settings.Current.Advisors);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>A switch with neither checklist nor settings keeps the plain on/off page.</summary>
    [Fact]
    public async Task ShowSwitch_ATool_WithoutSettings_HasNoSettingButtons()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Escape);

        await menu.ShowSwitchAsync(SettingsField.DockerTools, CancellationToken.None);

        Assert.DoesNotContain(SettingsMenu.SettingButtonGlyph + " ", Output);
        pane.Dispose();
    }

    /// <summary><c>/tools ask</c> (2026-10-09, the user's ask: 🙋 on the toolbar): Ask user's on/off page under the Tools crumb, a pick saving.</summary>
    [Fact]
    public async Task ShowSwitch_Ask_IsAskUsersOnOffPage_PickingSaves()
    {
        Assert.Equal(SettingsField.AskUser, ToolsText.SwitchField("ask"));
        Assert.Equal("Ask user", ToolsText.DescribeSwitch("ask"));
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Down, Keys.Enter);   // off is the row below

        await menu.ShowSwitchAsync(SettingsField.AskUser, CancellationToken.None);

        Assert.False(_settings.Current.AskUser);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Ask user") + "\n", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }
}
