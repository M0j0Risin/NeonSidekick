using System.Text.RegularExpressions;
using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.UI;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// The panes' consistency pass (2026-10-07, the user's ask, phase 3 of the UI round): every remove asks, key letters lowercase in
/// every hint, ESC = close at a pane's top level, empty lists open their pane, and a filter's hint drops the letter keys it takes.
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
        Assert.Equal("ESC = close", MenuPane.EmptyKeys);
        Assert.Equal("d / o / s = pick · v = view · Enter = choose · ESC = deny", CameraMenu.AllowHintWithView);
    }

    /// <summary>The hints a pane shows: no key letter in capitals (<c>W = watch</c> was), and the top-level panes' ESC closes.</summary>
    public static TheoryData<string> Hints() =>
    [
        MemoryMenu.Keys, MemoryMenu.SwitchKeys, MemoryMenu.EmptySwitchKeys, QueueMenu.Keys, QueueMenu.KeysWithSend, RewindText.Keys,
        SettingsMenu.SwitchKeys, SettingsMenu.ToggleKeys, SettingsMenu.DefaultToggleKeys, SettingsMenu.PerfBarToggleKeys,
        SettingsMenu.AllowedCommandsKeys, SettingsMenu.AllowedCommandsEmptyKeys, SettingsMenu.PoliceToggleKeys, SettingsMenu.OfferedToggleKeys,
        SettingsMenu.WebToggleKeys, SettingsMenu.CameraToggleKeys, SettingsMenu.ChangedKeys, SettingsMenu.ProfileKeys,
        SettingsMenu.ThemeKeepKeys, SettingsMenu.ThemePickKeys, EmbeddedModelFilter.Keys, EmbeddedModelFilter.CatalogKeys,
        CameraText.AllowHint, CameraMenu.AllowHintWithView, YouTubeText.SavedKeys, MenuPane.EmptyKeys,
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

    [Fact]
    public void TheTopLevelPanes_EscCloses()
    {
        foreach (string hint in (string[])[MemoryMenu.Keys, MemoryMenu.SwitchKeys, MemoryMenu.EmptySwitchKeys, QueueMenu.Keys, QueueMenu.KeysWithSend, RewindText.Keys, MenuPane.EmptyKeys])
        {
            Assert.EndsWith("ESC = close", hint);
        }
    }

    /// <summary>While a filter is typed the letter keys go on it, so the hint stops offering them (<c>c = clear all</c> on /youtube saved).</summary>
    [Fact]
    public void AFiltersHint_DropsTheLetterKeys_ItTakes()
    {
        Assert.Equal("Enter = play · c = clear all · " + MenuFilter.TypeAndCloseKeys, MenuFilter.Hint(YouTubeText.SavedKeys, ""));
        Assert.Equal("Enter = play · " + MenuFilter.FilteringKeys, MenuFilter.Hint(YouTubeText.SavedKeys, "c"));
        Assert.Equal("Enter = remove · " + MenuFilter.FilteringKeys, MenuFilter.HintBeforeEsc(SettingsMenu.AllowedCommandsKeys, "git"));
        Assert.Equal("Enter = choose · " + MenuFilter.FilteringKeys, MenuFilter.HintBeforeEsc("Enter = choose · d / h / c = browser mode · ESC = keep", "x"));
        Assert.Equal(MenuFilter.FilteringKeys, MenuFilter.HintBeforeEsc("r = refresh · ESC = close", "x"));
    }
}
