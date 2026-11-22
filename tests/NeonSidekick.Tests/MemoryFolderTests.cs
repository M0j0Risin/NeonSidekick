using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Project-scoped memories (2026-11-21, feature idea #6): a memory saved for one working folder is offered only while working there, marked so
/// the model doesn't take a project's rule for one about the user; the store, the tools, the pane's choices and the page's marks.
/// </summary>
public class MemoryFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _other;

    public MemoryFolderTests()
    {
        _repo = Path.Combine(_dir, "work", "Repo");
        _other = Path.Combine(_dir, "work", "Other");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private MemoryStore Store() => new(Path.Combine(_dir, "profile"));

    [Fact]
    public void AFolderMemory_RoundTrips_AndIsOfferedOnlyInItsFolder_Marked()
    {
        var store = Store();
        store.Add("Their name is Chris.");
        Assert.Equal(MemoryAddOutcome.Added, store.Add("Tests run with build.ps1.", folder: _repo + Path.DirectorySeparatorChar).Outcome);

        var reloaded = Store();
        Assert.Equal(_repo, reloaded.EntriesSnapshot()[1].Folder);   // made full, the trailing separator gone
        Assert.Equal(["Their name is Chris.", "Tests run with build.ps1." + MemoryPrompt.FolderOnlyMark], reloaded.Snapshot(_repo));
        Assert.Equal(["Their name is Chris."], reloaded.Snapshot(_other));
        Assert.Equal(["Their name is Chris."], reloaded.Snapshot());   // no folder: everywhere's alone
        Assert.Equal(" (this folder only)", MemoryPrompt.FolderOnlyMark);
        Assert.DoesNotContain("Folder", File.ReadAllText(reloaded.FilePath).Split("Their name is Chris.")[1].Split('}')[0]);   // written only when set
    }

    [WindowsFact]
    public void TheFolderMatch_IgnoresCase_OnWindows()
    {
        var store = Store();
        store.Add("Tests run with build.ps1.", folder: _repo);
        Assert.Single(store.Snapshot(_repo.ToUpperInvariant()));
    }

    [Fact]
    public void AnOlderFile_WithNoFolders_ReadsAsEverywhere()
    {
        string file = NeonSidekick.Settings.Profiles.ConfigPath(Path.Combine(_dir, "profile"), MemoryStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{ "SchemaVersion": 1, "Entries": [ { "Text": "They like tea.", "SavedAt": "2026-10-01T00:00:00+00:00" } ] }""");

        Assert.Equal(["They like tea."], Store().Snapshot(_repo));
        Assert.Null(Store().EntriesSnapshot()[0].Folder);
    }

    [Fact]
    public void ADuplicate_NamesTheFolderItsCopyIsFor_WhenThatIsntHere()
    {
        var store = Store();
        store.Add("Tests run with build.ps1.", folder: _repo);

        var again = store.Add("tests run with build.ps1.");

        Assert.Equal(new MemoryAddResult(MemoryAddOutcome.Duplicate, "Tests run with build.ps1.", _repo), again);
        Assert.Equal(MemoryText.DuplicateElsewhere("Tests run with build.ps1.", _repo), ChatScreen.RememberAnswer(again, _other).Text);
        Assert.Contains("for the Repo folder only", ChatScreen.RememberAnswer(again, _other).Text);
        Assert.Equal(ChatScreen.AlreadyRememberedNotice("Tests run with build.ps1."), ChatScreen.RememberAnswer(again, _repo).Text);   // here: as today
        Assert.Equal(MemoryText.DuplicateElsewhereForModel("Tests run with build.ps1.", _repo), SaveMemoryTool.Describe(again, _other));   // no pane for the model (#113)
        Assert.Contains("without here makes it apply everywhere", SaveMemoryTool.Describe(again, _other));
        Assert.Contains("› everywhere widens it", ChatScreen.RememberAnswer(again, _other).Text);
        Assert.Equal("already remembered: Tests run with build.ps1.", SaveMemoryTool.Describe(again, _repo));
    }

    [Fact]
    public void SetFolder_ScopesAndWidens_AndImportCarriesTheFolder()
    {
        var store = Store();
        store.Add("Tests run with build.ps1.");
        Assert.Equal(MemoryScopeOutcome.Moved, store.SetFolder("tests run with build.ps1.", _repo));
        Assert.Equal(MemoryScopeOutcome.Unchanged, store.SetFolder("Tests run with build.ps1.", _repo + Path.DirectorySeparatorChar));   // already so, compared as folders (#92)
        Assert.Equal(_repo, store.EntriesSnapshot()[0].Folder);

        var target = new MemoryStore(Path.Combine(_dir, "pushed"));
        target.Import(store.EntriesSnapshot(), overwrite: false);   // /profile push|pull memory
        Assert.Equal(_repo, target.EntriesSnapshot()[0].Folder);

        Assert.Equal(MemoryScopeOutcome.Moved, store.SetFolder("Tests run with build.ps1.", null));
        Assert.Null(store.EntriesSnapshot()[0].Folder);
        Assert.Equal(MemoryScopeOutcome.Missing, store.SetFolder("nothing like it", null));
        Assert.Equal(MemoryScopeOutcome.Refused, store.SetFolder("Tests run with build.ps1.", "relative"));   // never everywhere (#107)
        Assert.Null(store.EntriesSnapshot()[0].Folder);
    }

    [Fact]
    public async Task SaveMemory_Here_SavesForTheToolsFolder_AndABotsToolHasNoHere()
    {
        var store = Store();
        string folder = _repo;
        var tools = ChatScreen.MemoryTools(store, () => folder);
        var save = Assert.IsType<SaveMemoryTool>(tools[0]);
        var recall = Assert.IsType<RecallMemoryTool>(tools[1]);
        Assert.Contains("\"here\"", save.JsonSchema.GetRawText());

        object? said = await save.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["text"] = "Tests run with build.ps1.", ["here"] = true }));
        object? plain = await save.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["text"] = "They like tea.", ["here"] = "nonsense" }));

        Assert.Equal(MemoryText.RememberedHere("Tests run with build.ps1."), said);
        Assert.Equal("remembered: They like tea.", plain);   // a malformed here saves everywhere
        Assert.Contains("Tests run with build.ps1." + MemoryPrompt.FolderOnlyMark, recall.Describe());
        folder = _other;   // a /cwd: the next recall follows
        Assert.DoesNotContain("build.ps1", recall.Describe());

        var bot = ChatScreen.MemoryTools(store);
        Assert.DoesNotContain("\"here\"", bot[0].JsonSchema.GetRawText());
        Assert.DoesNotContain("build.ps1", ((RecallMemoryTool)bot[1]).Describe());
        object? botSave = await bot[0].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["text"] = "They walk daily.", ["here"] = true }));
        Assert.Equal("remembered: They walk daily.", botSave);
        Assert.Null(store.EntriesSnapshot()[^1].Folder);
    }

    [Fact]
    public void ThePane_MarksTheFolderRows_AndOffersTheRightChoices()
    {
        var everywhere = new MemoryEntry { Text = "They like tea." };
        var here = new MemoryEntry { Text = "Tests run with build.ps1.", Folder = _repo };
        var elsewhere = new MemoryEntry { Text = "Deploys go through CI.", Folder = _other };

        Assert.Equal(MemoryMenu.FolderScope.Everywhere, MemoryMenu.ScopeOf(everywhere, _repo));
        Assert.Equal(MemoryMenu.FolderScope.Here, MemoryMenu.ScopeOf(here, _repo));
        Assert.Equal(MemoryMenu.FolderScope.Elsewhere, MemoryMenu.ScopeOf(elsewhere, _repo));
        Assert.Equal(MemoryMenu.FolderScope.Unknown, MemoryMenu.ScopeOf(here, null));
        Assert.Equal([MemoryMenu.RowAction.ThisFolder], MemoryMenu.FolderActions(MemoryMenu.FolderScope.Everywhere));
        Assert.Equal([MemoryMenu.RowAction.Everywhere], MemoryMenu.FolderActions(MemoryMenu.FolderScope.Here));
        Assert.Equal([MemoryMenu.RowAction.Everywhere, MemoryMenu.RowAction.ThisFolder], MemoryMenu.FolderActions(MemoryMenu.FolderScope.Elsewhere));
        Assert.Empty(MemoryMenu.FolderActions(MemoryMenu.FolderScope.Unknown));

        Assert.Contains("· Repo folder", MemoryMenu.RowMarkup(here, _repo));
        Assert.Contains("· " + MemoryText.OtherFolderMark, MemoryMenu.RowMarkup(elsewhere, _repo));
        Assert.DoesNotContain("·", MemoryMenu.RowMarkup(everywhere, _repo));
        Assert.Equal(MemoryText.FolderFooter(_repo), MemoryMenu.Footer([here], 0)!.Last);
        Assert.Equal(MemoryText.FolderFooter(_other) + " · " + MemoryMenu.DisabledFooter, MemoryMenu.Footer([new MemoryEntry { Text = "x", Folder = _other, Disabled = true }], 0)!.Last);
    }

    [Fact]
    public void ThePage_GetsTheMarks_NeverThePath()
    {
        var memory = new NeonSidekick.WebUi.WebMemory("Tests run with build.ps1.", false, Scoped: true, Here: false);
        string json = System.Text.Json.JsonSerializer.Serialize(memory, NeonSidekick.WebUi.WebUiJsonContext.Default.WebMemory);

        Assert.Contains("\"scoped\":true", json);
        Assert.DoesNotContain(_repo, json);
        Assert.Equal("this folder", NeonSidekick.WebUi.WebUiText.MemoryHere);
        Assert.Equal(MemoryText.OtherFolderMark, NeonSidekick.WebUi.WebUiText.MemoryElsewhere);   // the pane's words (#115)
    }

    private void WriteFile(string json)
    {
        string file = NeonSidekick.Settings.Profiles.ConfigPath(Path.Combine(_dir, "profile"), MemoryStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json);
    }

    /// <summary>A memory saved on the other system (2026-11-22, the review's #78, #92): kept as written, never this folder's, never everywhere.</summary>
    [WindowsFact]
    public void AMacFolder_ReadOnWindows_IsAnotherFolder_NeverOffered()
    {
        WriteFile("""{ "SchemaVersion": 1, "Entries": [ { "Text": "Tests run with make.", "SavedAt": "2026-10-01T00:00:00+00:00", "Folder": "/Users/x/Repo" } ] }""");
        var store = Store();

        Assert.Equal("/Users/x/Repo", store.EntriesSnapshot()[0].Folder);
        Assert.Empty(store.Snapshot(_repo));
        Assert.Empty(store.Snapshot());
        Assert.Equal(MemoryMenu.FolderScope.Elsewhere, MemoryMenu.ScopeOf(store.EntriesSnapshot()[0], _repo));
    }

    [UnixFact]
    public void AWindowsFolder_ReadOnAMac_IsAnotherFolder_NeverOffered()
    {
        WriteFile("""{ "SchemaVersion": 1, "Entries": [ { "Text": "Tests run with build.ps1.", "SavedAt": "2026-10-01T00:00:00+00:00", "Folder": "C:\\Repo\\X" } ] }""");
        var store = Store();

        Assert.Equal(@"C:\Repo\X", store.EntriesSnapshot()[0].Folder);
        Assert.Empty(store.Snapshot(_repo));
        Assert.Empty(store.Snapshot());
    }

    /// <summary>A hand-edited folder with a trailing separator is still this folder (#92), and a relative one given to an add is refused (#107).</summary>
    [Fact]
    public void AStoredFolder_IsNormalized_AndARelativeOneIsRefused()
    {
        WriteFile("""{ "SchemaVersion": 1, "Entries": [ { "Text": "Tests run with build.ps1.", "SavedAt": "2026-10-01T00:00:00+00:00", "Folder": """ + System.Text.Json.JsonSerializer.Serialize(_repo + Path.DirectorySeparatorChar) + """ } ] }""");
        var store = Store();
        Assert.Equal(_repo, store.EntriesSnapshot()[0].Folder);

        var refused = store.Add("They like tea.", folder: "relative");
        Assert.Equal(MemoryAddOutcome.FolderRefused, refused.Outcome);
        Assert.Single(store.EntriesSnapshot());
        Assert.True(ChatScreen.RememberAnswer(refused, "relative").Error);
    }

    /// <summary>An everywhere save of another folder's fact widens it (2026-11-22, the review's #34, #88); in its own folder it's a plain duplicate.</summary>
    [Fact]
    public void AnEverywhereSave_OfAnotherFoldersFact_WidensIt_ButNotInItsOwnFolder()
    {
        var store = Store();
        store.Add("Tests run with build.ps1.", folder: _other);

        var own = store.Remember("Tests run with build.ps1.", _other, scoped: false);
        Assert.Equal(MemoryAddOutcome.Duplicate, own.Outcome);
        Assert.Null(own.WidenedFrom);
        Assert.Equal(_other, store.EntriesSnapshot()[0].Folder);

        var widened = store.Remember("tests run with build.ps1.", _repo, scoped: false);
        Assert.Equal(_other, widened.WidenedFrom);
        Assert.Null(store.EntriesSnapshot()[0].Folder);
        Assert.Equal(MemoryText.Widened("Tests run with build.ps1.", _other), SaveMemoryTool.Describe(widened, _repo));
        Assert.Equal(MemoryText.WidenedNotice("Tests run with build.ps1.", _other), ChatScreen.RememberAnswer(widened, _repo).Text);
    }

    /// <summary>A save for this folder never narrows one offered everywhere (#20); the user's here on a disabled one takes the folder.</summary>
    [Fact]
    public void AHereSave_KeepsAnEverywhereFact_AndReEnablesADisabledOneForTheFolder()
    {
        var store = Store();
        store.Add("They like tea.");
        var kept = store.Remember("They like tea.", _repo, scoped: true);
        Assert.True(kept.CoversHere);
        Assert.Null(store.EntriesSnapshot()[0].Folder);
        Assert.Equal(MemoryText.CoversHereNotice("They like tea."), ChatScreen.RememberAnswer(kept, _repo).Text);
        Assert.Equal(MemoryText.CoversHere("They like tea."), SaveMemoryTool.Describe(kept, _repo));

        store.Add("Tests run with build.ps1.");
        store.SetDisabled("Tests run with build.ps1.", true);
        var enabled = store.Remember("Tests run with build.ps1.", _repo, scoped: true, enableDisabled: true);
        Assert.Equal(MemoryAddOutcome.Enabled, enabled.Outcome);
        Assert.Equal(_repo, store.EntriesSnapshot()[1].Folder);
        Assert.False(store.EntriesSnapshot()[1].Disabled);
    }

    /// <summary>The ceiling is per folder (2026-11-22, the review's #26): 200 in one folder never stop an add in another; an everywhere one counts in all.</summary>
    [Fact]
    public void TheCap_CountsWhatOneFolderIsOffered()
    {
        var store = Store();
        var entries = Enumerable.Range(0, MemoryStore.MaxEntries).Select(i => new MemoryEntry { Text = "other " + i, SavedAt = DateTimeOffset.UnixEpoch, Folder = _other }).ToList();
        Assert.Equal(MemoryStore.MaxEntries, store.Import(entries, overwrite: true).Added);

        Assert.Equal(MemoryAddOutcome.Added, store.Add("for the repo", folder: _repo).Outcome);
        var everywhere = store.Add("for everyone");
        Assert.Equal(MemoryAddOutcome.Full, everywhere.Outcome);
        Assert.False(everywhere.FileFull);
        Assert.Equal(MemoryAddOutcome.Full, store.Add("one more there", folder: _other).Outcome);

        // A widen that would pass the ceiling in the other folder answers full and moves nothing.
        Assert.Equal(MemoryScopeOutcome.Full, store.SetFolder("for the repo", null));
        Assert.Equal(_repo, store.EntriesSnapshot().Single(e => e.Text == "for the repo").Folder);
        var widen = store.Remember("for the repo", _other, scoped: false);
        Assert.Equal(MemoryAddOutcome.Full, widen.Outcome);
    }

    /// <summary>The file's own ceiling (#26): disabled ones count there, so the file stays bounded.</summary>
    [Fact]
    public void TheFileCeiling_CountsEveryEntry_DisabledToo()
    {
        var store = Store();
        var entries = Enumerable.Range(0, 1200).Select(i => new MemoryEntry { Text = "off " + i, SavedAt = DateTimeOffset.UnixEpoch, Disabled = true }).ToList();
        var imported = store.Import(entries, overwrite: true);
        Assert.Equal(MemoryStore.MaxStoredEntries, imported.Added);
        Assert.Equal(200, imported.Dropped);

        var full = store.Add("one more");
        Assert.Equal(MemoryAddOutcome.Full, full.Outcome);
        Assert.True(full.FileFull);
        Assert.Equal((MemoryText.FullFileError, true), ChatScreen.RememberAnswer(full));
    }

    /// <summary>A line copied from the list keeps no folder mark (#35); the tool's prompt sentence for here (#33).</summary>
    [Fact]
    public async Task SaveMemory_DropsTheListsFolderMark_AndThePromptNamesHere_OnlyWithAFolder()
    {
        var store = Store();
        var save = new SaveMemoryTool(store, () => _repo);
        await save.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["text"] = "Tests run with build.ps1." + MemoryPrompt.FolderOnlyMark, ["here"] = true }));
        Assert.Equal("Tests run with build.ps1.", store.EntriesSnapshot()[0].Text);
        Assert.True(save.OffersHere);
        Assert.False(new SaveMemoryTool(store).OffersHere);

        Assert.EndsWith(MemoryPrompt.SaveHereSentence, MemoryPrompt.Section([], tools: true, save: true, here: true));
        Assert.DoesNotContain(MemoryPrompt.SaveHereSentence, MemoryPrompt.Section([], tools: true, save: true));
        Assert.DoesNotContain(MemoryPrompt.SaveHereSentence, MemoryPrompt.Section([], tools: true, save: false, here: true));
    }
}
