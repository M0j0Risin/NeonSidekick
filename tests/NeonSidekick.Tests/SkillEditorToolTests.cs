using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

public class SkillEditorToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly SkillRoots _roots;
    private readonly SkillEditorTool _tool;
    private bool _external;

    public SkillEditorToolTests()
    {
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
        _tool = new SkillEditorTool(() => _roots, () => _external);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var (name, value) in values)
        {
            dictionary[name] = value;
        }

        return new AIFunctionArguments(dictionary);
    }

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<string> Invoke(params (string Name, object? Value)[] values) =>
        (string)(await _tool.InvokeAsync(Args(values), CancellationToken.None))!;

    [Fact]
    public void Schema_IsPinned()
    {
        Assert.Equal("skill_editor", SkillEditorTool.ToolName);
        Assert.Equal(SkillEditorTool.ToolName, _tool.Name);
        Assert.Contains("Creates or updates a skill", _tool.Description);
        Assert.Contains("Never deletes anything", _tool.Description);
        Assert.Contains("use update — never create a copy under another scope", _tool.Description);
        Assert.Contains("wherever it lives", _tool.JsonSchema.GetProperty("properties").GetProperty("action").GetProperty("description").GetString());
        Assert.Contains("profile (the default)", _tool.JsonSchema.GetProperty("properties").GetProperty("scope").GetProperty("description").GetString());
        var schema = _tool.JsonSchema;
        Assert.Equal(["action", "scope", "name", "description", "instructions", "summary"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["action", "name"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["create", "update"], schema.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["profile", "global"], schema.GetProperty("properties").GetProperty("scope").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.All(schema.GetProperty("properties").EnumerateObject(), p => Assert.False(string.IsNullOrWhiteSpace(p.Value.GetProperty("description").GetString())));
        // The summary argument (2026-09-19): optional, for the reflection's transcript line, never the skill.
        Assert.Equal("summary", SkillEditorTool.SummaryArgument);
        Assert.Equal("One or two sentences on what you changed and why, for the user to read; never part of the skill. Optional.", schema.GetProperty("properties").GetProperty("summary").GetProperty("description").GetString());
        Assert.Equal(["action", "name"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Create_WithASummary_KeepsItOnTheResult_NeverInTheAnswerOrTheFile()
    {
        // The reflection reads LastResult.Summary (2026-09-19): cleaned, the answer and the file unchanged; none = empty.
        string result = await Invoke(("action", "create"), ("scope", "profile"), ("name", "haiku"), ("description", "Writes haiku."), ("instructions", "Five, seven, five."), ("summary", "  Added the\n syllable rule.\n\nKept the example. "));

        Assert.Equal("created skill 'haiku' (profile, 67 bytes); it is in the list from the next reply on", result);
        Assert.Equal("---\nname: haiku\ndescription: Writes haiku.\n---\n\nFive, seven, five.\n", File.ReadAllText(Path.Combine(_roots.Profile, "haiku", "SKILL.md")));
        Assert.Equal("Added the syllable rule. Kept the example.", _tool.LastResult!.Summary);

        await Invoke(("action", "update"), ("scope", "profile"), ("name", "haiku"), ("instructions", "Five, seven, five, again."));
        Assert.Equal("", _tool.LastResult!.Summary);
        Assert.Equal(SkillEditOutcome.Updated, _tool.LastResult.Outcome);
    }

    [Fact]
    public async Task Create_WritesUnderTheScope_AndAnswersWithTheBytes()
    {
        string result = await Invoke(("action", Json("\"create\"")), ("scope", "profile"), ("name", "haiku"), ("description", "Writes haiku."), ("instructions", "Five, seven, five."));

        string file = Path.Combine(_roots.Profile, "haiku", "SKILL.md");
        Assert.True(File.Exists(file));
        Assert.Equal("---\nname: haiku\ndescription: Writes haiku.\n---\n\nFive, seven, five.\n", File.ReadAllText(file));
        Assert.Equal("created skill 'haiku' (profile, 67 bytes); it is in the list from the next reply on", result);
        Assert.Equal(67, new FileInfo(file).Length);

        Assert.Equal("Error: skill 'haiku' already exists in the profile skills; call again with action update to change it", await Invoke(("action", "create"), ("scope", "profile"), ("name", "haiku"), ("description", "x"), ("instructions", "y")));
        // The other scope is no way round it (2026-09-16): a global copy would sit under the profile's, and the reverse would shadow it.
        Assert.Equal("Error: skill 'haiku' already exists in the profile skills; a second copy would hide it — call again with action update to change it (whatever scope you pass, the update goes to the profile copy)", await Invoke(("action", "CREATE"), ("scope", " Global "), ("name", "haiku"), ("description", "x"), ("instructions", "y")));
        Assert.False(Directory.Exists(Path.Combine(_roots.Global, "haiku")));
        Assert.Equal("created skill 'tanka' (global, 38 bytes); it is in the list from the next reply on", await Invoke(("action", "CREATE"), ("scope", " Global "), ("name", "tanka"), ("description", "x"), ("instructions", "y")));
    }

    [Fact]
    public async Task Create_RefusesANameFromTheExternalFolder_WhileItIsRead()
    {
        Directory.CreateDirectory(Path.Combine(_roots.External, "weather-info"));
        File.WriteAllText(Path.Combine(_roots.External, "weather-info", "SKILL.md"), "---\nname: weather-info\ndescription: d\n---\n\ni\n");

        _external = true;
        Assert.Equal(@"Error: skill 'weather-info' exists in the external skills (.agents\skills), which this app never writes; edit it by hand or pick another name", await Invoke(("action", "create"), ("scope", "profile"), ("name", "weather-info"), ("description", "x"), ("instructions", "y")));
        Assert.Equal(@"Error: skill 'weather-info' exists in the external skills (.agents\skills), which this app never writes; edit it by hand or pick another name", await Invoke(("action", "update"), ("scope", "profile"), ("name", "weather-info"), ("description", "x")));
        Assert.False(Directory.Exists(_roots.Profile));

        _external = false;   // the folder unread: the name is free
        Assert.Equal("created skill 'weather-info' (profile, 45 bytes); it is in the list from the next reply on", await Invoke(("action", "create"), ("scope", "profile"), ("name", "weather-info"), ("description", "x"), ("instructions", "y")));
    }

    [Fact]
    public async Task Update_ChangesWhatWasGiven()
    {
        await Invoke(("action", "create"), ("scope", "global"), ("name", "haiku"), ("description", "Writes haiku."), ("instructions", "Five, seven, five."));

        Assert.Equal("updated skill 'haiku' (global, 69 bytes)", await Invoke(("action", "update"), ("scope", "global"), ("name", "haiku"), ("instructions", "Seventeen syllables.")));
        Assert.Equal("---\nname: haiku\ndescription: Writes haiku.\n---\n\nSeventeen syllables.\n", File.ReadAllText(Path.Combine(_roots.Global, "haiku", "SKILL.md")));
        Assert.Equal("Error: nothing to change; give a new description, new instructions or both", await Invoke(("action", "update"), ("scope", "global"), ("name", "haiku")));
        // The wrong scope (2026-09-16): the skill is changed where it lives and the answer names that scope — never a hint to create a copy.
        Assert.Equal("updated skill 'haiku' (global, 57 bytes)", await Invoke(("action", "update"), ("scope", "profile"), ("name", "haiku"), ("description", "x")));
        Assert.Equal("---\nname: haiku\ndescription: x\n---\n\nSeventeen syllables.\n", File.ReadAllText(Path.Combine(_roots.Global, "haiku", "SKILL.md")));
        Assert.False(Directory.Exists(_roots.Profile));
        // A name that is a skill nowhere: create is the right next call, as before.
        Assert.Equal("Error: there is no skill 'tanka' in the profile skills; call again with action create to write it", await Invoke(("action", "update"), ("scope", "profile"), ("name", "tanka"), ("description", "x")));
    }

    [Fact]
    public async Task ABlankScope_IsTheProfile_AndUpdateTakesAnyScope()
    {
        // 2026-09-24, the user's report: models sent "" more often than not and retried until a word stuck.
        Assert.Equal("created skill 'haiku' (profile, 38 bytes); it is in the list from the next reply on", await Invoke(("action", "create"), ("scope", ""), ("name", "haiku"), ("description", "x"), ("instructions", "y")));
        Assert.Equal("created skill 'tanka' (profile, 38 bytes); it is in the list from the next reply on", await Invoke(("action", "create"), ("name", "tanka"), ("description", "x"), ("instructions", "y")));
        Assert.Equal("created skill 'renga' (global, 38 bytes); it is in the list from the next reply on", await Invoke(("action", "create"), ("scope", "global"), ("name", "renga"), ("description", "x"), ("instructions", "y")));

        // An update is changed where the skill lives, whatever the scope says, blank or not a scope at all.
        Assert.Equal("updated skill 'renga' (global, 38 bytes)", await Invoke(("action", "update"), ("scope", ""), ("name", "renga"), ("instructions", "z")));
        Assert.Equal("updated skill 'renga' (global, 38 bytes)", await Invoke(("action", "update"), ("scope", "external"), ("name", "renga"), ("instructions", "w")));
        Assert.Equal("updated skill 'haiku' (profile, 38 bytes)", await Invoke(("action", "update"), ("name", "haiku"), ("instructions", "v")));
        Assert.Equal("---\nname: renga\ndescription: x\n---\n\nw\n", File.ReadAllText(Path.Combine(_roots.Global, "renga", "SKILL.md")));
    }

    [Fact]
    public async Task TheArgumentErrors_ArePinned()
    {
        Assert.Equal("Error: 'delete' is not an action; use create or update", await Invoke(("action", "delete"), ("scope", "global"), ("name", "x")));
        Assert.Equal("Error: '' is not an action; use create or update", await Invoke(("scope", "global"), ("name", "x")));
        Assert.Equal("Error: 'external' is not a scope; use profile (this profile only) or global (every profile)", await Invoke(("action", "create"), ("scope", "external"), ("name", "x"), ("description", "d"), ("instructions", "i")));
        Assert.Equal("Error: 'My Skill' is not a valid skill name; use 1 to 64 lowercase letters, digits and hyphens, not starting or ending with a hyphen and with no two hyphens in a row", await Invoke(("action", "create"), ("scope", "global"), ("name", "My Skill"), ("description", "d"), ("instructions", "i")));
        Assert.Equal("Error: description is empty; say what the skill does and when to use it", await Invoke(("action", "create"), ("scope", "global"), ("name", "x"), ("instructions", "i")));
        Assert.Equal("Error: instructions is empty; write the steps the skill follows", await Invoke(("action", "create"), ("scope", "global"), ("name", "x"), ("description", "d")));
        Assert.Equal("Error: the description is 1,025 characters; the limit is 1,024", await Invoke(("action", "create"), ("scope", "global"), ("name", "x"), ("description", new string('d', 1025)), ("instructions", "i")));
        Assert.Equal("Error: the instructions are 64,001 characters; the limit is 64,000", await Invoke(("action", "create"), ("scope", "global"), ("name", "x"), ("description", "d"), ("instructions", new string('i', 64_001))));
        Assert.False(Directory.Exists(_roots.Global));
        Assert.False(Directory.Exists(_roots.External));
    }

    [Fact]
    public void Constructor_RefusesNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SkillEditorTool(null!, () => false));
        Assert.Throws<ArgumentNullException>(() => new SkillEditorTool(() => _roots, null!));
    }

    // ── Global skills (2026-10-13, the profile's switch) ────────────────────

    private static string[] ScopeEnum(SkillEditorTool tool) =>
        [.. tool.JsonSchema.GetProperty("properties").GetProperty("scope").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!)];

    /// <summary>Off, the schema offers the profile alone (with the file actions or without), a create there is refused, and an update naming global never writes the global file.</summary>
    [Fact]
    public void GlobalOff_TheSchemaOffersTheProfileAlone_ACreateThereIsRefused_AndAnUpdateNeverReachesIt()
    {
        var off = _roots with { GlobalOn = false };
        var tool = new SkillEditorTool(() => off, () => false);
        var withFiles = new SkillEditorTool(() => off, () => false, new SkillFileAccess(TimeProvider.System));

        Assert.Equal(["profile"], ScopeEnum(tool));
        Assert.Equal(["profile"], ScopeEnum(withFiles));
        Assert.DoesNotContain("global", tool.JsonSchema.GetProperty("properties").GetProperty("scope").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(["create", "update", "write_file", "edit_file"], withFiles.JsonSchema.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["profile", "global"], ScopeEnum(_tool));
        Assert.Equal(["profile", "global"], ScopeEnum(new SkillEditorTool(() => _roots, () => false, new SkillFileAccess(TimeProvider.System))));

        Assert.Equal(SkillText.GlobalSkillsOff, tool.Describe("create", "global", "haiku", "Writes haiku.", "Five, seven, five."));
        Assert.False(Directory.Exists(Path.Combine(_roots.Global, "haiku")));
        Assert.Equal("Error: 'team' is not a scope; use profile", tool.Describe("create", "team", "haiku", "d", "i"));
        Assert.Equal(SkillText.BadScope("team"), _tool.Describe("create", "team", "haiku", "d", "i"));

        string file = Path.Combine(_roots.Global, "pdf", SkillCatalog.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "---\nname: pdf\ndescription: d\n---\n\ni\n");
        Assert.Equal(SkillText.Missing("pdf", SkillScope.Profile), tool.Describe("update", "global", "pdf", null, "Rewritten."));
        Assert.Equal("---\nname: pdf\ndescription: d\n---\n\ni\n", File.ReadAllText(file));
    }

    // ── Reflection mode update-only (2026-10-16) ────────────────────────────

    private SkillEditorTool UpdateOnly(bool files = false, Func<SkillProposal, CancellationToken, Task<bool>>? confirm = null) =>
        new(() => _roots, () => _external, files ? new SkillFileAccess(TimeProvider.System) : null, confirm: confirm, create: false);

    private string PutSkill(string name)
    {
        string dir = Path.Combine(_roots.Profile, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, SkillCatalog.FileName), "---\nname: " + name + "\ndescription: Writes " + name + ".\n---\n\nCount the syllables.\n");
        return dir;
    }

    private static string[] ActionEnum(SkillEditorTool tool) =>
        [.. tool.JsonSchema.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!)];

    /// <summary>The schema offers no create and no scope (with the file actions or without, Global skills on or off), and no description names a create.</summary>
    [Fact]
    public void UpdateOnly_TheSchemaOffersNoCreate_AndNoScope_AndTheDescriptionNeverNamesIt()
    {
        var plain = UpdateOnly();
        var withFiles = UpdateOnly(files: true);
        var globalOff = new SkillEditorTool(() => _roots with { GlobalOn = false }, () => false, create: false);
        foreach (var tool in new[] { plain, withFiles, globalOff })
        {
            var properties = tool.JsonSchema.GetProperty("properties");
            Assert.False(properties.TryGetProperty("scope", out _));
            Assert.DoesNotContain("creat", properties.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("creat", tool.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("companion", tool.Description, StringComparison.OrdinalIgnoreCase);
            Assert.False(tool.OffersCreate);
            Assert.Equal(["action", "name"], tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal("The name of a listed skill to change, as the list shows it.", properties.GetProperty("name").GetProperty("description").GetString());
            Assert.All(properties.EnumerateObject(), p => Assert.False(string.IsNullOrWhiteSpace(p.Value.GetProperty("description").GetString())));
        }

        Assert.Equal(["update"], ActionEnum(plain));
        Assert.Equal(["update"], ActionEnum(globalOff));
        Assert.Equal(["update", "write_file", "edit_file"], ActionEnum(withFiles));
        Assert.Equal(["action", "name", "description", "instructions", "summary"], plain.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.StartsWith("Updates a skill that exists: ", plain.Description, StringComparison.Ordinal);
        Assert.EndsWith("Never deletes anything.", plain.Description, StringComparison.Ordinal);
        Assert.EndsWith(" " + SkillEditorTool.UpdateOnlyFilesSentence, withFiles.Description, StringComparison.Ordinal);
        Assert.Equal("A skill's supporting files (the ones load_skill lists beside its instructions) are kept current with write_file (the whole file) or edit_file (old_text to new_text), paths relative to the skill folder; the SKILL.md itself only through update.", SkillEditorTool.UpdateOnlyFilesSentence);

        // The main chat's editor is unchanged.
        Assert.True(_tool.OffersCreate);
        Assert.StartsWith("Creates or updates a skill", _tool.Description, StringComparison.Ordinal);
        Assert.Equal(["create", "update"], ActionEnum(_tool));
    }

    /// <summary>A create sent anyway is refused before the scope, the ask and the write; its word is pinned.</summary>
    [Fact]
    public async Task UpdateOnly_ACreateIsRefused_BeforeAnyAsk_AndNothingIsWritten()
    {
        bool asked = false;
        var tool = UpdateOnly(files: true, confirm: (_, _) => { asked = true; return Task.FromResult(true); });

        Assert.Equal(SkillText.CreateRefused, await tool.DescribeAsync("create", "", "haiku", "Writes haiku.", "Five, seven, five.", null, CancellationToken.None));
        Assert.Equal(SkillText.CreateRefused, (string)(await tool.InvokeAsync(Args(("action", " Create "), ("scope", "team"), ("name", "haiku"), ("description", "d"), ("instructions", "i")), CancellationToken.None))!);
        Assert.False(asked);
        Assert.Null(tool.LastResult);
        Assert.False(Directory.Exists(_roots.Profile));
        Assert.False(Directory.Exists(_roots.Global));
        Assert.Equal("Error: this reflection only updates a skill that exists (Reflection mode is update-only); answer nothing if no listed skill covers the topic", SkillText.CreateRefused);
    }

    /// <summary>An update goes through; a missing name, an unknown action and a locked skill each answer without pointing to a create.</summary>
    [Fact]
    public async Task UpdateOnly_AnUpdateGoesThrough_AndItsErrorsNeverPointToACreate()
    {
        string dir = PutSkill("haiku");
        var tool = UpdateOnly(files: true);

        Assert.StartsWith("updated skill 'haiku'", tool.Describe("update", "", "haiku", null, "Five, seven, five."), StringComparison.Ordinal);
        Assert.Contains("Five, seven, five.", File.ReadAllText(Path.Combine(dir, SkillCatalog.FileName)), StringComparison.Ordinal);
        Assert.StartsWith("skill 'haiku' (profile): ", tool.DescribeWrite("haiku", "data.txt", "x"), StringComparison.Ordinal);

        Assert.Equal(SkillText.MissingUpdateOnly("tanka"), tool.Describe("update", "", "tanka", null, "x"));
        Assert.Equal(SkillText.MissingUpdateOnly("tanka"), tool.DescribeWrite("tanka", "data.txt", "x"));
        Assert.Equal(SkillText.MissingUpdateOnly("tanka"), tool.DescribeEdit("tanka", "data.txt", "a", "b", false));
        Assert.Equal("Error: there is no skill 'tanka'; this reflection only updates a listed skill — pick one from the list or answer nothing", SkillText.MissingUpdateOnly("tanka"));
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "tanka")));

        Assert.Equal("Error: 'delete' is not an action; use update, write_file or edit_file", (string)(await tool.InvokeAsync(Args(("action", "delete"), ("name", "x")), CancellationToken.None))!);
        Assert.Equal("Error: 'delete' is not an action; use update", UpdateOnly().Describe("delete", "", "x", null, null));
        Assert.Equal("Error: 'write_file' is not an action; use update", UpdateOnly().DescribeWrite("haiku", "data.txt", "x"));

        SkillLock.Set(dir, true, DateTimeOffset.UnixEpoch);
        Assert.Equal(SkillText.LockedUpdateOnly("haiku"), tool.Describe("update", "", "haiku", null, "Locked out."));
        Assert.Equal("Error: skill 'haiku' is locked by the user; it cannot be changed until they unlock it on /skills; leave it as it is, and answer nothing if no other listed skill covers the topic", SkillText.LockedUpdateOnly("haiku"));

        // The default editor keeps its words.
        Assert.Equal(SkillText.Locked("haiku"), _tool.Describe("update", "", "haiku", null, "Locked out."));
        Assert.Equal(SkillText.Missing("tanka", SkillScope.Profile), _tool.Describe("update", "", "tanka", null, "x"));
        Assert.Equal("Error: 'delete' is not an action; use create or update", _tool.Describe("delete", "", "x", null, null));
    }
}
