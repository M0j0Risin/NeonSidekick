using System.Text.RegularExpressions;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Prompts;
using NeonSidekick.Shopping;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// The panes' consistency pass (2026-10-07, the user's ask, phase 3 of the UI round): every remove asks, key letters lowercase in
/// every hint, Esc = close at a pane's top level, empty lists open their pane, and a filter's hint drops the letter keys it takes.
/// </summary>
public class ConsistencyPassTests
{
    /// <summary>The removal questions and the empty pane's hint, pinned.</summary>
    [Fact]
    public void TheRemovalQuestions_AndTheEmptyHint_ArePinned()
    {
        Assert.Equal("💾 Forget \"Their name is Chris.\"?", MemoryMenu.RemovePrompt("Their name is Chris."));
        Assert.Equal("⏳ Remove \"fix the tests\" from the queue?", QueueMenu.RemovePrompt("fix the tests"));
        Assert.Equal("Remove \"git push\" from the allowed commands? It will ask again.", SettingsMenu.RemovePrefixQuestion("git push"));
        Assert.Equal("Remove \"rm -rf\" from the forbidden strings? Commands holding it are no longer refused for it.", SettingsMenu.RemoveForbiddenQuestion("rm -rf"));
        Assert.Equal("Esc = close", MenuPane.EmptyKeys);
        Assert.Equal("d / o / s = pick · v = view · Enter = choose · Esc = deny", CameraMenu.AllowHintWithView);
    }

    /// <summary>The hints a pane shows: no key letter in capitals (<c>W = watch</c> was), and the top-level panes' Esc closes.</summary>
    public static TheoryData<string> Hints() =>
    [
        MemoryMenu.Keys, MemoryMenu.SwitchKeys, MemoryMenu.EmptySwitchKeys, QueueMenu.Keys, QueueMenu.KeysWithSend, RewindText.Keys,
        SettingsMenu.SwitchKeys, SettingsMenu.ToggleKeys, SettingsMenu.DefaultToggleKeys, SettingsMenu.ToolbarToggleKeys, SettingsMenu.PerfBarToggleKeys,
        SettingsMenu.AllowedCommandsKeys, SettingsMenu.AllowedCommandsEmptyKeys, SettingsMenu.PoliceToggleKeys, SettingsMenu.OfferedToggleKeys,
        SettingsMenu.WebToggleKeys, SettingsMenu.WebToggleOpenKeys, SettingsMenu.CameraToggleKeys, SettingsMenu.ChangedKeys, SettingsMenu.ProfileKeys,
        SettingsMenu.ThemeKeepKeys, SettingsMenu.ThemePickKeys, EmbeddedModelFilter.Keys, EmbeddedModelFilter.CatalogKeys,
        CameraText.AllowHint, CameraMenu.AllowHintWithView, YouTubeText.SavedKeys, MenuPane.EmptyKeys,
        SavedPromptText.PaneKeys, BrowserLinkText.PaneKeys, ShoppingText.PaneKeys, ShoppingText.StoresKeys,   // 2026-10-16, the 120-column pass
        MenuFilter.HintBeforeEsc(ShoppingText.AddKeys, "", letteredButtons: true), TasksMenu.Keys, ServicesMenu.Keys, EventLogMenu.Keys, HomeBarText.BarHint,
        .. new[] { SettingsField.FileTools, SettingsField.ImageTools, SettingsField.PrintTools, SettingsField.ScreenTools, SettingsField.ObsidianTools, SettingsField.YouTubeTools }
            .Select(field => SettingsMenu.SettingButtonKeys(SettingsMenu.SettingButtonsFor(field)!)),   // the setting buttons' pages (2026-10-09)
    ];

    [Theory]
    [MemberData(nameof(Hints))]
    public void NoHint_SpellsAKeyLetter_InCapitals(string hint)
    {
        foreach (string piece in hint.Split(" · "))
        {
            int equals = piece.IndexOf(" = ", StringComparison.Ordinal);
            if (equals > 0)
            {
                Assert.DoesNotMatch(new Regex(@"^[A-Z]( / [A-Z])*$"), piece[..equals]);
            }
        }
    }

    /// <summary>A named key is written with a capital first letter only, as Enter, Space and Backspace are (2026-10-16, the user's
    /// call: <c>ESC</c> was the one key in capitals; <c>DEL</c> and <c>TAB</c> went with it).</summary>
    [Theory]
    [MemberData(nameof(Hints))]
    public void NoHint_SpellsAKeyNameInCapitals(string hint)
    {
        Assert.DoesNotMatch(new Regex(@"\b(ESC|DEL|TAB)\b"), hint);
    }

    /// <summary>
    /// The hints built from parts at their widest (2026-10-16): the info panes' buttons as a 120-column window draws them
    /// (<see cref="InfoPane.ButtonsHint"/> leaving out its scroll lead past <see cref="InfoPane.HintCells"/>), and a filtered list's.
    /// </summary>
    public static TheoryData<string> WidestHints() =>
    [
        InfoPane.ButtonsHint(WebUiButtons, tabs: true, cells: InfoPane.HintCells),   // /webui with two or more addresses
        InfoPane.ButtonsHint(WebUiButtons, tabs: false, cells: InfoPane.HintCells),   // and with one
        InfoPane.ButtonsHint([('c', "copy"), ('a', "ask"), ('n', DirectoryText.NestingWord), ('u', DirectoryText.MembersWord)], tabs: false, cells: InfoPane.HintCells),   // /ad's group card
        InfoPane.ButtonsHint([('c', "copy"), ('a', "ask"), ('s', DirectoryText.LockoutWord)], tabs: false, cells: InfoPane.HintCells),   // a locked user's
        InfoPane.ButtonsHint(Buttons(ServicesMenu.CopyAction, ServicesMenu.AskAction, ServicesMenu.StartAction, ServicesMenu.StopAction, ServicesMenu.RestartAction, ServicesMenu.StartTypeAction), tabs: false, cells: InfoPane.HintCells),
        InfoPane.ButtonsHint(Buttons(TasksMenu.CopyAction, TasksMenu.AskAction, TasksMenu.RunAction, TasksMenu.EndAction, TasksMenu.EnableAction, TasksMenu.DisableAction, TasksMenu.HistoryAction), tabs: false, cells: InfoPane.HintCells),
        InfoPane.ButtonsHint(Buttons(EventLogMenu.CopyAction, EventLogMenu.AskAction), tabs: true, cells: InfoPane.HintCells),
        MenuFilter.Hint(SkillsMenu.LoadedKeys, "x"),
        MenuFilter.HintBeforeEsc(ShoppingText.AddKeys, "x", letteredButtons: true),
    ];

    private static readonly (char Key, string Word)[] WebUiButtons =
        [(WebUiText.EnabledKey, WebUiText.EnabledWord), (WebUiText.ReachKey, WebUiText.ReachWord), (WebUiText.ResetKey, WebUiText.ResetWord), (WebUiText.CopyKey, WebUiText.CopyWord)];

    private static (char Key, string Word)[] Buttons(params InfoAction[] actions) => [.. actions.Select(a => (a.Key, a.Word))];

    /// <summary>Every hint fits the Hints bar of a 120-column window (2026-10-16, the user's ask): past it the bar cuts it with "…".</summary>
    [Theory]
    [MemberData(nameof(Hints))]
    [MemberData(nameof(WidestHints))]
    public void EveryHint_FitsA120ColumnWindow(string hint)
    {
        Assert.True(TextCells.Width(hint) <= InfoPane.HintCells, $"{TextCells.Width(hint)} cells: {hint}");
    }

    [Fact]
    public void TheTopLevelPanes_EscCloses()
    {
        foreach (string hint in (string[])[MemoryMenu.Keys, MemoryMenu.SwitchKeys, MemoryMenu.EmptySwitchKeys, QueueMenu.Keys, QueueMenu.KeysWithSend, RewindText.Keys, MenuPane.EmptyKeys, ShoppingText.PaneKeys])
        {
            Assert.EndsWith("Esc = close", hint);
        }
    }

    /// <summary>While a filter is typed the letter keys go on it, so the hint stops offering them (<c>d = remove</c> and <c>c = clear all</c> on /youtube list).</summary>
    [Fact]
    public void AFiltersHint_DropsTheLetterKeys_ItTakes()
    {
        Assert.Equal("Enter = play · d = remove · c = clear all · Shift+↑↓ = move · " + MenuFilter.TypeOrShiftAndCloseKeys, MenuFilter.Hint(YouTubeText.SavedKeys, ""));
        Assert.Equal("Enter = play · " + MenuFilter.FilteringKeys, MenuFilter.Hint(YouTubeText.SavedKeys, "c"));   // the move keys go too (2026-10-13)
        Assert.Equal("Enter = open · " + MenuFilter.FilteringKeys, MenuFilter.Hint(Viewer.BrowserLinkText.PaneKeys, "n"));
        Assert.Equal("Enter = remove · p = policy · type / Shift+letter = filter · Esc = back", MenuFilter.HintBeforeEsc(SettingsMenu.AllowedCommandsKeys, "", letteredButtons: true));
        Assert.Equal("Enter = remove · " + MenuFilter.FilteringKeys, MenuFilter.HintBeforeEsc(SettingsMenu.AllowedCommandsKeys, "git"));
        Assert.Equal("Enter = choose · " + MenuFilter.FilteringKeys, MenuFilter.HintBeforeEsc("Enter = choose · d / h / c = browser mode · Esc = keep", "x"));
        Assert.Equal(MenuFilter.FilteringKeys, MenuFilter.HintBeforeEsc("r = refresh · Esc = close", "x"));

        // The shopping add page (2026-10-16): x = forget goes while a filter is typed, and the filter's keys come.
        Assert.EndsWith(MenuFilter.TypeOrShiftKeys + " · Esc = back", MenuFilter.HintBeforeEsc(ShoppingText.AddKeys, "", letteredButtons: true));
        string filtering = MenuFilter.HintBeforeEsc(ShoppingText.AddKeys, "milk", letteredButtons: true);
        Assert.EndsWith(MenuFilter.FilteringKeys, filtering);
        Assert.DoesNotContain("x = ", filtering);
    }
}
