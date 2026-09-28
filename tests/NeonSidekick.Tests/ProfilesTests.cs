using System.Text.Json;
using NeonSidekick.Llm;
using NeonSidekick.Mcp;
using NeonSidekick.Memory;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class ProfilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("travel", true)]
    [InlineData("Work-2", true)]
    [InlineData("a_b", true)]
    [InlineData("neon2", true)]        // the sidekick's name with more is fine (2026-09-21)
    [InlineData("neon_debug", true)]
    [InlineData("neon-test", true)]
    [InlineData("neon", false)]        // the word alone is the sidekick's (2026-09-21)
    [InlineData("Neon", false)]
    [InlineData("NEON", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("add", false)]
    [InlineData("Delete", false)]
    [InlineData("RESET", false)]
    [InlineData("rename", false)]
    [InlineData("Rename", false)]
    [InlineData("with space", false)]
    [InlineData("dots.not", false)]
    [InlineData("..", false)]
    [InlineData("slash/name", false)]
    [InlineData("café", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]   // 33 characters
    public void IsValidName_IsPinned(string? name, bool valid)
    {
        Assert.Equal(valid, Profiles.IsValidName(name));
    }

    [Theory]
    [InlineData("_test", true)]
    [InlineData("__", true)]
    [InlineData("_", true)]
    [InlineData("test_", false)]
    [InlineData("te_st", false)]
    [InlineData("default", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTemporary_IsTheLeadingUnderscore(string? name, bool temporary)
    {
        Assert.Equal(temporary, Profiles.IsTemporary(name));
        Assert.Equal('_', Profiles.TemporaryPrefix);
    }

    [Fact]
    public void Strings_ArePinned()
    {
        Assert.Equal("profiles", Profiles.DirectoryName);
        Assert.Equal("default", Profiles.DefaultName);
        Assert.Equal("profile.json", Profiles.FileName);
        Assert.Equal("neon", Profiles.SidekickName);
        Assert.False(Profiles.IsValidName("edit"));
        Assert.False(Profiles.IsValidName("Reload"));
        Assert.Equal("\"work\" is the current profile; switch to another (/profile <name>) before deleting it.", Profiles.CurrentUndeletable("work"));
        Assert.Equal("\"work\" is the current profile; switch to another (/profile <name>) before renaming it.", Profiles.CurrentUnrenamable("work"));
    }

    [Fact]
    public void Paths_AreUnderTheHome()
    {
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), "profiles", "work"), Profiles.Directory(_dir, "work"));
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), "profiles", "work", "profile.json"), Profiles.ProfileFile(_dir, "work"));
    }

    [Fact]
    public void List_WithNoProfilesDirectory_IsTheDefaultAlone()
    {
        Assert.Equal(new[] { "default" }, Profiles.List(_dir));
        Assert.Equal("default", Profiles.Resolve(_dir, "DEFAULT"));
        Assert.True(Profiles.Exists(_dir, "default"));
        Assert.Null(Profiles.Resolve(_dir, "work"));
    }

    [Fact]
    public void List_DefaultFirst_ThenSortedIgnoringCase_SkippingBadNames()
    {
        foreach (var name in new[] { "zeta", "Alpha", "default", "bad name", "mid" })
        {
            Directory.CreateDirectory(Path.Combine(_dir, "profiles", name));
        }

        File.WriteAllText(Path.Combine(_dir, "profiles", "not-a-dir"), "");   // a file, not a profile

        Assert.Equal(new[] { "default", "Alpha", "mid", "zeta" }, Profiles.List(_dir));
    }

    [Fact]
    public void Resolve_ReturnsTheDirectorysSpelling()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "profiles", "WoRk"));

        Assert.Equal("WoRk", Profiles.Resolve(_dir, "work"));
        Assert.True(Profiles.Exists(_dir, "WORK"));
        Assert.Null(Profiles.Resolve(_dir, "add"));
        Assert.Null(Profiles.Resolve(_dir, null));
    }

    [Fact]
    public void ReadProfileFile_MissingIsTheDefaults_AndAWrittenFileRoundTrips()
    {
        // 2026-09-21: the reader /cmdcopy edits another profile's settings with.
        string path = Profiles.ProfileFile(_dir, "work");
        var missing = Profiles.ReadProfileFile(path);
        Assert.Equal(new AppSettingsData().LlmModel, missing.LlmModel);
        Assert.Empty(missing.ShellCommandAllowed);
        Assert.False(File.Exists(path));

        Profiles.WriteProfileFile(path, new AppSettingsData { LlmModel = "seeded", ShellCommandAllowed = ["echo", "git status"] });
        var read = Profiles.ReadProfileFile(path);
        Assert.Equal("seeded", read.LlmModel);
        Assert.Equal(new[] { "echo", "git status" }, read.ShellCommandAllowed);
    }

    [Fact]
    public void ReadProfileFile_CorruptThrows_RatherThanFallingBack()
    {
        // Unlike AppSettings.Load: a copy that fell back would overwrite the file with the defaults.
        string path = Profiles.ProfileFile(_dir, "work");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        Assert.Throws<System.Text.Json.JsonException>(() => Profiles.ReadProfileFile(path));
        File.WriteAllText(path, "null");
        Assert.Throws<System.Text.Json.JsonException>(() => Profiles.ReadProfileFile(path));
        Assert.Throws<ArgumentException>(() => Profiles.ReadProfileFile(" "));
    }

    [Fact]
    public void Create_WritesTheSeed_AndNothingElse()
    {
        Profiles.Create(_dir, "work", new AppSettingsData { LlmModel = "seeded", TtsVoice = "bm_george" });

        string dir = Profiles.Directory(_dir, "work");
        Assert.Equal(new[] { Profiles.FileName }, Directory.GetFiles(dir).Select(Path.GetFileName));
        string json = File.ReadAllText(Path.Combine(dir, Profiles.FileName));
        Assert.Contains("\"LlmModel\": \"seeded\"", json);
        Assert.Contains("\"TtsVoice\": \"bm_george\"", json);
        Assert.Equal(new[] { "default", "work" }, Profiles.List(_dir));
    }

    [Fact]
    public void Create_RefusesABadName()
    {
        Assert.Throws<ArgumentException>(() => Profiles.Create(_dir, "add", new AppSettingsData()));
        Assert.Throws<ArgumentException>(() => Profiles.Delete(_dir, "../x"));
        Assert.Throws<ArgumentException>(() => Profiles.CopySidekickFiles(_dir, _dir, "add"));
    }

    [Fact]
    public void SidekickFiles_AreTheOwnersFileNames_InOrder_EachWithAWord()
    {
        Assert.Equal(
            new[] { MemoryStore.FileName, PersonaFile.FileName, OperataFile.FileName, VocaliaFile.FileName, McpConfigFile.FileName },
            Profiles.SidekickFiles);
        Assert.Equal("memories", Profiles.Describe("memory.json"));
        Assert.Equal("persona", Profiles.Describe("persona.md"));
        Assert.Equal("operating rules", Profiles.Describe("operata.md"));
        Assert.Equal("voice directive", Profiles.Describe("vocalia.md"));
        Assert.Equal("MCP servers", Profiles.Describe("mcp.json"));
        Assert.Equal("", Profiles.Describe(Profiles.FileName));
    }

    [Fact]
    public void CopySidekickFiles_CopiesThePresentOnes_InOrder_AndNothingElse()
    {
        Profiles.Create(_dir, "default", new AppSettingsData { LlmModel = "old" });
        string source = Profiles.Directory(_dir, "default");
        new MemoryStore(source).Add("They like tea.");
        File.WriteAllText(Path.Combine(source, VocaliaFile.FileName), "Speak like a pirate.");
        File.WriteAllText(Path.Combine(source, PersonaFile.FileName), "You are Rex.");
        Directory.CreateDirectory(Path.Combine(source, "files"));
        File.WriteAllText(Path.Combine(source, "files", "notes.txt"), "not a sidekick file");
        Profiles.Create(_dir, "work", new AppSettingsData { LlmModel = "new" });

        var copied = Profiles.CopySidekickFiles(source, _dir, "work");

        Assert.Equal(new[] { MemoryStore.FileName, PersonaFile.FileName, VocaliaFile.FileName }, copied);
        string target = Profiles.Directory(_dir, "work");
        Assert.Equal(
            new[] { MemoryStore.FileName, VocaliaFile.FileName, PersonaFile.FileName, Profiles.FileName }.Order(),
            Directory.GetFiles(target).Select(Path.GetFileName).Order());
        Assert.Empty(Directory.GetDirectories(target));
        Assert.Contains("\"LlmModel\": \"new\"", File.ReadAllText(Path.Combine(target, Profiles.FileName)));   // the seed, not the source's settings
        Assert.Equal("You are Rex.", File.ReadAllText(Path.Combine(target, PersonaFile.FileName)));
        Assert.Equal("Speak like a pirate.", File.ReadAllText(Path.Combine(target, VocaliaFile.FileName)));
        Assert.Equal(new[] { "They like tea." }, new MemoryStore(target).Snapshot());
        Assert.Equal("You are Rex.", File.ReadAllText(Path.Combine(source, PersonaFile.FileName)));   // a copy, not a move
    }

    [Fact]
    public void CopySidekickFiles_WithOnly_TakesJustTheNamedOnes()
    {
        Profiles.Create(_dir, "default", new AppSettingsData());
        string source = Profiles.Directory(_dir, "default");
        new MemoryStore(source).Add("They like tea.");
        File.WriteAllText(Path.Combine(source, PersonaFile.FileName), "You are Rex.");
        Profiles.Create(_dir, "work", new AppSettingsData());

        var copied = Profiles.CopySidekickFiles(source, _dir, "work", Profiles.BasicSidekickFiles);

        Assert.Equal(new[] { MemoryStore.FileName }, copied);
        Assert.Equal(
            new[] { MemoryStore.FileName, Profiles.FileName }.Order(),
            Directory.GetFiles(Profiles.Directory(_dir, "work")).Select(Path.GetFileName).Order());
        Assert.Empty(Profiles.CopySidekickFiles(source, _dir, "work2", Array.Empty<string>()));   // nothing named, nothing copied — the directory is made
        Assert.True(Directory.Exists(Profiles.Directory(_dir, "work2")));
    }

    [Fact]
    public void CopySidekickFiles_WithNothingToCopy_IsEmpty()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());

        Assert.Empty(Profiles.CopySidekickFiles(Profiles.Directory(_dir, "default"), _dir, "work"));   // the default's directory does not even exist
        Assert.Equal(new[] { Profiles.FileName }, Directory.GetFiles(Profiles.Directory(_dir, "work")).Select(Path.GetFileName));
    }

    [Fact]
    public void Delete_RemovesTheWholeTree_AndAMissingOneIsNotAnError()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());
        File.WriteAllText(Path.Combine(Profiles.Directory(_dir, "work"), "memory.json"), "{}");

        Profiles.Delete(_dir, "work");
        Profiles.Delete(_dir, "work");

        Assert.False(Directory.Exists(Profiles.Directory(_dir, "work")));
        Assert.Equal(new[] { "default" }, Profiles.List(_dir));
    }

    [Fact]
    public void PresentSidekickFiles_ListsTheOnesThere_InOrder_AndNoneForAMissingDirectory()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());
        string dir = Profiles.Directory(_dir, "work");
        File.WriteAllText(Path.Combine(dir, VocaliaFile.FileName), "Speak like a pirate.");
        File.WriteAllText(Path.Combine(dir, PersonaFile.FileName), "You are Rex.");
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        File.WriteAllText(Path.Combine(dir, "files", MemoryStore.FileName), "not the sidekick's");

        Assert.Equal(new[] { PersonaFile.FileName, VocaliaFile.FileName }, Profiles.PresentSidekickFiles(dir));
        Assert.Empty(Profiles.PresentSidekickFiles(Profiles.Directory(_dir, "default")));
    }

    [Fact]
    public void Reset_WritesTheDefaults_KeepsEverySidekickFile_AndTheSandbox()
    {
        Profiles.Create(_dir, "work", new AppSettingsData { WorkingDirectory = @"C:\elsewhere", TtsOutput = true });
        string dir = Profiles.Directory(_dir, "work");
        new MemoryStore(dir).Add("They like tea.");
        File.WriteAllText(Path.Combine(dir, PersonaFile.FileName), "You are Rex.");
        File.WriteAllText(Path.Combine(dir, OperataFile.FileName), "Answer in haiku.");
        File.WriteAllText(Path.Combine(dir, VocaliaFile.FileName), "Speak like a pirate.");
        File.WriteAllText(Path.Combine(dir, McpConfigFile.FileName), McpConfigFile.EmptyText);
        Directory.CreateDirectory(Path.Combine(dir, "files", ".trash"));
        File.WriteAllText(Path.Combine(dir, "files", "notes.txt"), "kept");
        File.WriteAllText(Path.Combine(dir, "files", ".trash", "old.txt"), "kept too");

        Profiles.Reset(_dir, "work");

        // The settings alone go back (2026-09-20, the user's call): the memories and the three prompt files stay, like the sandbox.
        Assert.Equal("You are Rex.", File.ReadAllText(Path.Combine(dir, PersonaFile.FileName)));
        Assert.Equal("Answer in haiku.", File.ReadAllText(Path.Combine(dir, OperataFile.FileName)));
        Assert.Equal("Speak like a pirate.", File.ReadAllText(Path.Combine(dir, VocaliaFile.FileName)));
        Assert.Equal(new[] { "They like tea." }, new MemoryStore(dir).Snapshot());
        Assert.Equal(McpConfigFile.EmptyText, File.ReadAllText(Path.Combine(dir, McpConfigFile.FileName)));
        Assert.Equal(Profiles.SidekickFiles, Profiles.PresentSidekickFiles(dir));
        string json = File.ReadAllText(Path.Combine(dir, Profiles.FileName));
        Assert.Contains("\"LlmModel\": \"\"", json);
        Assert.Contains("\"WorkingDirectory\": \"\"", json);
        Assert.Contains("\"TtsOutput\": false", json);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(dir, "files", "notes.txt")));
        Assert.Equal("kept too", File.ReadAllText(Path.Combine(dir, "files", ".trash", "old.txt")));
    }

    [Fact]
    public void Reset_KeepsTheUrlsPathsAndKeys_UnlessAll()
    {
        // 2026-09-27, the user's call: the ten settings that describe this machine survive a plain reset; --all takes them too.
        var mine = new AppSettingsData
        {
            LlmUrl = "http://llm:1234/v1",
            LlmModel = "work-model",
            TtsHttpUrl = "http://tts:8880/v1",
            ClaudeApiKey = "dpapi:abc",
            WebBrowserPath = @"C:\Chrome\chrome.exe",
            WebSearchMethod = "searxng",
            WebSearxngUrl = "http://searx:8080",
            ClaudeExecutable = @"C:\bin\claude.exe",
            ObsidianVault = @"D:\Vault",
            ComfyUrl = "http://comfy:8188",
            LlmApiKey = "not-kept",
            TtsOutput = !new AppSettingsData().TtsOutput,
            WorkingDirectory = @"C:\elsewhere",
        };
        Profiles.Create(_dir, "work", mine);
        string file = Profiles.ProfileFile(_dir, "work");

        Profiles.Reset(_dir, "work");

        var kept = Profiles.ReadProfileFile(file);
        var expected = new AppSettingsData();
        Profiles.KeepOnReset(mine, expected);
        Assert.Equal(JsonSerializer.Serialize(expected, SettingsJsonContext.Default.AppSettingsData), JsonSerializer.Serialize(kept, SettingsJsonContext.Default.AppSettingsData));
        Assert.Equal("http://comfy:8188", kept.ComfyUrl);
        Assert.Equal("dpapi:abc", kept.ClaudeApiKey);   // as stored
        Assert.Equal(new AppSettingsData().LlmApiKey, kept.LlmApiKey);   // not on the list
        Assert.Equal("", kept.WorkingDirectory);
        Assert.Equal(10, Profiles.ResetKeptSettings.Length);

        Profiles.Create(_dir, "home", mine);
        Profiles.Reset(_dir, "home", all: true);
        Assert.Equal(JsonSerializer.Serialize(new AppSettingsData(), SettingsJsonContext.Default.AppSettingsData), File.ReadAllText(Profiles.ProfileFile(_dir, "home")));
    }

    [Fact]
    public void Reset_ACorruptFile_GoesToTheDefaultsWhole()
    {
        Directory.CreateDirectory(Profiles.Directory(_dir, "work"));
        File.WriteAllText(Profiles.ProfileFile(_dir, "work"), "{ not json");

        Profiles.Reset(_dir, "work");

        Assert.Equal(JsonSerializer.Serialize(new AppSettingsData(), SettingsJsonContext.Default.AppSettingsData), File.ReadAllText(Profiles.ProfileFile(_dir, "work")));
    }

    [Fact]
    public void Reset_WithNoDirectory_CreatesItWithTheDefaults()
    {
        Profiles.Reset(_dir, "default");

        string dir = Profiles.Directory(_dir, "default");
        Assert.Equal(new[] { Profiles.FileName }, Directory.GetFiles(dir).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(dir));
        Assert.Equal(new[] { "default" }, Profiles.List(_dir));
    }

    [Fact]
    public void Reset_RefusesABadName()
    {
        Assert.Throws<ArgumentException>(() => Profiles.Reset(_dir, "reset"));
        Assert.False(Directory.Exists(Profiles.Root(_dir)));
    }

    [Fact]
    public void DeleteRefusal_GuardsTheDefaultAndTheCurrent()
    {
        Assert.Equal(Profiles.DefaultUndeletable, Profiles.DeleteRefusal("Default", "work"));
        Assert.Equal(Profiles.CurrentUndeletable("Work"), Profiles.DeleteRefusal("Work", "work"));
        Assert.Null(Profiles.DeleteRefusal("other", "work"));
    }

    [Fact]
    public void Rename_MovesTheWholeTree()
    {
        Profiles.Create(_dir, "work", new AppSettingsData { LlmModel = "work-model" });
        string old = Profiles.Directory(_dir, "work");
        File.WriteAllText(Path.Combine(old, "memory.json"), "{}");
        File.WriteAllText(Path.Combine(old, "persona.md"), "You are Rex.");
        Directory.CreateDirectory(Path.Combine(old, "files", ".trash"));
        File.WriteAllText(Path.Combine(old, "files", "doc.txt"), "kept");
        File.WriteAllText(Path.Combine(old, "files", ".trash", "old.txt"), "binned");

        Profiles.Rename(_dir, "work", "office");

        string moved = Profiles.Directory(_dir, "office");
        Assert.False(Directory.Exists(old));
        Assert.Contains("\"LlmModel\": \"work-model\"", File.ReadAllText(Profiles.ProfileFile(_dir, "office")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(moved, "memory.json")));
        Assert.Equal("You are Rex.", File.ReadAllText(Path.Combine(moved, "persona.md")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(moved, "files", "doc.txt")));
        Assert.Equal("binned", File.ReadAllText(Path.Combine(moved, "files", ".trash", "old.txt")));
        Assert.Equal(new[] { "default", "office" }, Profiles.List(_dir));
    }

    [Fact]
    public void Rename_RefusesABadName_EitherSide()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());

        Assert.Throws<ArgumentException>(() => Profiles.Rename(_dir, "../work", "office"));
        Assert.Throws<ArgumentException>(() => Profiles.Rename(_dir, "work", "rename"));
        Assert.Throws<ArgumentException>(() => Profiles.Rename(_dir, "work", "bad name"));
        Assert.True(Directory.Exists(Profiles.Directory(_dir, "work")));
    }

    [Fact]
    public void Rename_OntoAnExistingDirectory_ThrowsIO_AndMovesNothing()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());
        Profiles.Create(_dir, "office", new AppSettingsData());

        Assert.Throws<IOException>(() => Profiles.Rename(_dir, "work", "office"));
        Assert.Equal(new[] { "default", "office", "work" }, Profiles.List(_dir));
    }

    [Fact]
    public void RenameRefusal_GuardsTheDefaultAndTheCurrent()
    {
        Assert.Equal(Profiles.DefaultUnrenamable, Profiles.RenameRefusal("Default", "work"));
        Assert.Equal(Profiles.CurrentUnrenamable("Work"), Profiles.RenameRefusal("Work", "work"));
        Assert.Null(Profiles.RenameRefusal("other", "work"));
    }

    [Fact]
    public void ResetRefusal_GuardsTheDefaultFromAnotherProfile_Only()
    {
        Assert.Equal(Profiles.DefaultUnresettable, Profiles.ResetRefusal("Default", "work"));
        Assert.Null(Profiles.ResetRefusal("DEFAULT", "default"));   // the default resets itself
        Assert.Null(Profiles.ResetRefusal("Work", "work"));         // the loaded one may reset
        Assert.Null(Profiles.ResetRefusal("work", "default"));      // and any other from anywhere
    }
    [Fact]
    public void CreateDeleteRename_EachLogOneLine()
    {
        var lines = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Category == AppSettings.Category && e.Level == NeonSidekick.Diagnostics.DiagnosticLevel.Info && e.Message.EndsWith(".", StringComparison.Ordinal) && (e.Message.StartsWith("Created profile", StringComparison.Ordinal) || e.Message.StartsWith("Deleted profile", StringComparison.Ordinal) || e.Message.StartsWith("Renamed profile", StringComparison.Ordinal))) lines.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            Profiles.Create(_dir, "work", new AppSettingsData());
            Profiles.Rename(_dir, "work", "play");
            Profiles.Delete(_dir, "play");
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(["Created profile \"work\".", "Renamed profile \"work\" to \"play\".", "Deleted profile \"play\"."], lines);
    }
}
