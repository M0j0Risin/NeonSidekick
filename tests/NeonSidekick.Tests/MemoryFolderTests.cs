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
        Assert.Equal(MemoryText.DuplicateElsewhere("Tests run with build.ps1.", _repo), SaveMemoryTool.Describe(again, _other));
        Assert.Equal("already remembered: Tests run with build.ps1.", SaveMemoryTool.Describe(again, _repo));
    }

    [Fact]
    public void SetFolder_ScopesAndWidens_AndImportCarriesTheFolder()
    {
        var store = Store();
        store.Add("Tests run with build.ps1.");
        Assert.True(store.SetFolder("tests run with build.ps1.", _repo));
        Assert.False(store.SetFolder("Tests run with build.ps1.", _repo));   // already so
        Assert.Equal(_repo, store.EntriesSnapshot()[0].Folder);

        var target = new MemoryStore(Path.Combine(_dir, "pushed"));
        target.Import(store.EntriesSnapshot(), overwrite: false);   // /profile push|pull memory
        Assert.Equal(_repo, target.EntriesSnapshot()[0].Folder);

        Assert.True(store.SetFolder("Tests run with build.ps1.", null));
        Assert.Null(store.EntriesSnapshot()[0].Folder);
        Assert.False(store.SetFolder("nothing like it", null));
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
        Assert.Equal("one folder", NeonSidekick.WebUi.WebUiText.MemoryElsewhere);
    }
}
