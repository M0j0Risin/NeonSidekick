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
}
