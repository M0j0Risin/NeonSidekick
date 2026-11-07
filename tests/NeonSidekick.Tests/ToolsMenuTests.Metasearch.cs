using NeonSidekick.App;
using NeonSidekick.UI;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>The built-in metasearch's two rows on <c>/tools</c>' Web tab (2026-11-06).</summary>
public partial class ToolsMenuTests
{
    private const int MetasearchEnginesRow = 12;

    /// <summary>The Web tab (<see cref="ToTab"/>), then the row and Enter.</summary>
    private void OpenWebRow(int row) => Push([.. ToTab(ToolsText.WebTabTitle), .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    [Fact]
    public void TheEngineRows_AreTheirOwnSection_BetweenSearchAndDownloads()
    {
        var rows = TabFields(ToolsText.WebTabTitle).ToList();

        Assert.Equal(MetasearchEnginesRow, rows.IndexOf(SettingsField.WebMetasearchEngines));
        Assert.Equal(rows.IndexOf(SettingsField.WebSearchMaxResults) + 1, rows.IndexOf(SettingsField.WebMetasearchEngines));
        Assert.Equal(rows.IndexOf(SettingsField.WebDownloadMaxMegabytes) - 1, rows.IndexOf(SettingsField.WebMetasearchSafeSearch));
        Assert.Equal("Web metasearch", SettingsMenu.ToolsMenuSections[ToolsText.MenuIndex(ToolsText.WebTabTitle)].Single(s => s.First == SettingsField.WebMetasearchEngines).Title);
        Assert.Equal("Web metasearch engines", SettingsMenu.FieldName(SettingsField.WebMetasearchEngines));
        Assert.Equal("Web metasearch safe search", SettingsMenu.FieldName(SettingsField.WebMetasearchSafeSearch));
    }

    [Theory]
    [InlineData(null, "all")]
    [InlineData(new string[0], "none")]
    [InlineData(new[] { "google" }, "none")]
    [InlineData(new[] { "wikipedia", " BING " }, "bing, wikipedia")]
    [InlineData(new[] { "bing", "brave", "startpage" }, "bing, brave, startpage")]
    [InlineData(new[] { "bing", "brave", "startpage", "wikipedia" }, "4 of 5")]
    [InlineData(new[] { "bing", "brave", "duckduckgo", "startpage", "wikipedia" }, "all")]
    public void TheEnginesValue_ReadsTheSavedList(string[]? saved, string expected) =>
        Assert.Equal(expected, SettingsMenu.MetasearchEnginesValue(saved));

    [Fact]
    public async Task OnThePane_TheEngines_TurningOneOff_SavesTheOthers()
    {
        var (menu, pane, _) = PaneMenu();
        OpenWebRow(MetasearchEnginesRow);
        Push(Keys.Enter);    // bing, under the cursor
        Push(Keys.Escape);   // out of the checklist
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["brave", "duckduckgo", "startpage", "wikipedia"], _settings.Current.WebMetasearchEngines);
        Assert.Contains("  · Web metasearch engines: 4 of 5", Output);
        Assert.Contains(SettingsMenu.DefaultToggleKeys, Output);
        pane.Dispose();
    }

    /// <summary>Null rather than five names, so an engine added later starts on.</summary>
    [Fact]
    public async Task OnThePane_TheEngines_TickingTheLastOne_SavesNull()
    {
        _settings.Update(d => d.WebMetasearchEngines = ["brave", "duckduckgo", "startpage", "wikipedia"]);
        var (menu, pane, _) = PaneMenu();
        OpenWebRow(MetasearchEnginesRow);
        Push(Keys.Enter);
        Push(Keys.Escape);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_settings.Current.WebMetasearchEngines);
        pane.Dispose();
    }

    [Theory]
    [InlineData('a')]
    [InlineData('d')]
    public async Task OnThePane_TheEngines_AllAndDefault_SaveNull(char key)
    {
        _settings.Update(d => d.WebMetasearchEngines = ["bing"]);
        var (menu, pane, _) = PaneMenu();
        OpenWebRow(MetasearchEnginesRow);
        Push(Keys.Char(key));
        Push(Keys.Escape);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_settings.Current.WebMetasearchEngines);
        pane.Dispose();
    }

    /// <summary>None is allowed, unlike a null, and a search then says so.</summary>
    [Fact]
    public async Task OnThePane_TheEngines_None_SavesAnEmptyList()
    {
        var (menu, pane, _) = PaneMenu();
        OpenWebRow(MetasearchEnginesRow);
        Push(Keys.Char('n'));
        Push(Keys.Escape);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Empty(_settings.Current.WebMetasearchEngines!);
        Assert.Contains("  · Web metasearch engines: none", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheSafeSearch_IsAPicker()
    {
        Assert.Equal(BuiltInMetaSearch.DefaultSafeSearch, _settings.Current.WebMetasearchSafeSearch);
        var (menu, pane, _) = PaneMenu();
        OpenWebRow(MetasearchEnginesRow + 1);
        Push(Keys.Down, Keys.Enter);   // moderate, under off
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));   // read at the next search: nothing to reconnect

        Assert.Equal("moderate", _settings.Current.WebMetasearchSafeSearch);
        Assert.Contains("  · Web metasearch safe search: moderate", Output);
        Assert.Contains(BuiltInMetaSearch.DescribeSafeSearch("off"), Output);
        pane.Dispose();
    }

    [Fact]
    public void TheSearchMethodHint_LeavesTheEnginesToTheirRow() =>
        Assert.Equal("built in, no setup: asks the engines in Web metasearch engines at once, all five by default", SearchMethod.Describe("metasearch"));
}
