using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

public class LoadSkillToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly SkillRoots _roots;
    private readonly SkillCatalog _catalog;
    private readonly LoadSkillTool _tool;

    public LoadSkillToolTests()
    {
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
        _catalog = new SkillCatalog(() => _roots);
        _tool = new LoadSkillTool(_catalog);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Put(string name, string body = "# Haiku\n\nFive, seven, five.")
    {
        string directory = Path.Combine(_roots.Profile, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, SkillCatalog.FileName), "---\nname: " + name + "\ndescription: Writes " + name + ".\n---\n" + body + "\n");
        return directory;
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
    public void Schema_NamesTheTwoArguments_TheNameAnEnumOfTheCatalog_RebuiltWhenItChanges()
    {
        Assert.Equal("load_skill", LoadSkillTool.ToolName);
        Assert.Equal(LoadSkillTool.ToolName, _tool.Name);
        Assert.Contains("listed in the system prompt", _tool.Description);
        Assert.Contains("file", _tool.Description);

        var empty = _tool.JsonSchema;
        Assert.Equal("object", empty.GetProperty("type").GetString());
        Assert.Equal(["name", "file"], empty.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["name"], empty.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(empty.GetProperty("properties").GetProperty("name").GetProperty("enum").EnumerateArray());
        Assert.All(empty.GetProperty("properties").EnumerateObject(), p => Assert.False(string.IsNullOrWhiteSpace(p.Value.GetProperty("description").GetString())));

        Put("haiku");
        Put("pdf");
        _catalog.Scan(external: false);
        var two = _tool.JsonSchema;
        Assert.Equal(["haiku", "pdf"], two.GetProperty("properties").GetProperty("name").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(two.GetRawText(), _tool.JsonSchema.GetRawText());   // cached until the set changes

        var quoted = LoadSkillTool.Schema(["a\"b"]);
        Assert.Equal("a\"b", quoted.GetProperty("properties").GetProperty("name").GetProperty("enum")[0].GetString());
    }

    [Fact]
    public async Task ByName_TheBodyWrapped_TheFolderNamed_TheBundledFilesListed()
    {
        string directory = Put("haiku");
        Directory.CreateDirectory(Path.Combine(directory, "references"));
        File.WriteAllText(Path.Combine(directory, "references", "forms.md"), "5-7-5");
        _catalog.Scan(external: false);

        string result = await Invoke(("name", Json("\"haiku\"")));

        Assert.Equal(
            "<skill_content name=\"haiku\">\n# Haiku\n\nFive, seven, five.\n\nSkill directory: " + directory + "\n"
            + "Relative paths in this skill are relative to the skill directory; read a bundled file with load_skill and its file argument.\n"
            + "<skill_resources>\n  <file>references/forms.md</file>\n</skill_resources>\n</skill_content>",
            result);
        Assert.Equal("loaded skill 'haiku' (" + result.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " characters)", LoadSkillTool.Note(result));
    }

    /// <summary>The preloaded form (2026-09-30, /botchat): its files follow it, so the note says so rather than pointing at load_skill; with no file, the plain note.</summary>
    [Fact]
    public void Content_FilesFollow_SaysSo_IsPinned()
    {
        Assert.Equal(
            "<skill_content name=\"haiku\">\nbody\n\nSkill directory: d\n"
            + "Relative paths in this skill are relative to the skill directory; its bundled files follow.\n"
            + "<skill_resources>\n  <file>a.md</file>\n</skill_resources>\n</skill_content>",
            SkillText.Content("haiku", "body", "d", ["a.md"], false, false, filesFollow: true));
        Assert.Equal(
            "<skill_content name=\"haiku\">\nbody\n\nSkill directory: d\nRelative paths in this skill are relative to the skill directory.\n</skill_content>",
            SkillText.Content("haiku", "body", "d", [], false, false, filesFollow: true));
    }

    [Fact]
    public async Task ByName_WithNoBundledFile_NoResourcesBlock()
    {
        string directory = Put("haiku");
        _catalog.Scan(external: false);

        Assert.Equal(
            "<skill_content name=\"haiku\">\n# Haiku\n\nFive, seven, five.\n\nSkill directory: " + directory + "\nRelative paths in this skill are relative to the skill directory.\n</skill_content>",
            await Invoke(("name", "haiku")));
    }

    [Fact]
    public async Task WithFile_TheBundledText_InsideTheFolderOnly()
    {
        string directory = Put("haiku");
        Directory.CreateDirectory(Path.Combine(directory, "references"));
        File.WriteAllText(Path.Combine(directory, "references", "forms.md"), "5-7-5");
        File.WriteAllBytes(Path.Combine(directory, "references", "pic.bin"), [0, 1, 2]);
        _catalog.Scan(external: false);

        string file = await Invoke(("name", "haiku"), ("file", "references/forms.md"));
        Assert.Equal("<skill_file skill=\"haiku\" path=\"references/forms.md\">\n5-7-5\n</skill_file>", file);
        Assert.Equal("read references/forms.md of skill 'haiku'", LoadSkillTool.Note(file));
        Assert.Equal("Error: skill 'haiku' has no file 'references/gone.md'", await Invoke(("name", "haiku"), ("file", "references/gone.md")));
        Assert.Equal("Error: '../../secret.txt' is outside the folder of skill 'haiku'; every path must stay inside it", await Invoke(("name", "haiku"), ("file", "../../secret.txt")));
        Assert.Equal("Error: 'references' in skill 'haiku' is a folder, not a file", await Invoke(("name", "haiku"), ("file", "references")));
        Assert.Equal("Error: 'references/pic.bin' in skill 'haiku' is not a text file", await Invoke(("name", "haiku"), ("file", "references/pic.bin")));
    }

    [Fact]
    public async Task AnUnknownOrMissingName_IsAnErrorSentence_NamingTheCatalog()
    {
        Assert.Equal("Error: there is no skill named 'haiku'; no skill is installed", await Invoke(("name", "haiku")));
        Assert.Equal("Error: name is empty; pass the name of a skill from the list", await Invoke());
        Assert.Equal("Error: name is empty; pass the name of a skill from the list", await Invoke(("name", Json("null"))));

        Put("haiku");
        Put("pdf");
        _catalog.Scan(external: false);
        Assert.Equal("Error: there is no skill named 'deploy'; the skills are: haiku, pdf", await Invoke(("name", " deploy ")));
        Assert.Equal("Error: there is no skill named 'deploy'; the skills are: haiku, pdf", LoadSkillTool.Note("Error: there is no skill named 'deploy'; the skills are: haiku, pdf"));
    }

    [Fact]
    public async Task ASkillGoneSinceTheScan_SaysSo()
    {
        string directory = Put("haiku");
        _catalog.Scan(external: false);
        Directory.Delete(directory, recursive: true);

        Assert.Equal("Error: the SKILL.md of 'haiku' is gone; it was there when the skills were listed", await Invoke(("name", "haiku")));
    }

    [Fact]
    public async Task ALongBody_IsCutWithANote()
    {
        Put("haiku", new string('x', SkillCatalog.MaxBodyChars + 5));
        _catalog.Scan(external: false);

        string result = await Invoke(("name", "haiku"));

        Assert.Contains("\n\n(cut at 48,000 characters)\n\nSkill directory: ", result);
        Assert.Equal("(cut at 48,000 characters)", SkillText.BodyCutNote);
    }

    /// <summary>
    /// A preloaded skill (2026-09-30, code review: a /botchat skill whose content the prompt already carries): its name alone, in
    /// any case, is answered "already loaded", not the content again; its files are still read, another skill loads as ever, and
    /// the schema still names it.
    /// </summary>
    [Fact]
    public async Task APreloadedSkill_ByName_IsAlreadyLoaded_ItsFilesStillRead()
    {
        string directory = Put("haiku");
        File.WriteAllText(Path.Combine(directory, "forms.md"), "5-7-5");
        Put("pdf");
        _catalog.Scan(external: false);
        var tool = new LoadSkillTool(_catalog, ["Haiku"]);
        async Task<string> Call(params (string Name, object? Value)[] values) => (string)(await tool.InvokeAsync(Args(values), CancellationToken.None))!;

        string again = await Call(("name", "haiku"));
        Assert.Equal(SkillText.AlreadyLoaded("haiku"), again);
        Assert.Contains("already loaded", again);
        Assert.Contains("in your system prompt", again);
        Assert.DoesNotContain("Five, seven, five.", again);
        // The sentence is the model's; the transcript's line is short (2026-09-30, code review).
        Assert.Equal("skill 'haiku' already loaded (preloaded)", LoadSkillTool.Note(again));
        Assert.False(SkillText.IsAlreadyLoaded("Skill '' is already loaded", out _));
        Assert.Equal("<skill_file skill=\"haiku\" path=\"forms.md\">\n5-7-5\n</skill_file>", await Call(("name", "haiku"), ("file", "forms.md")));
        Assert.StartsWith("<skill_content name=\"pdf\">", await Call(("name", "pdf")));
        Assert.Contains("\"haiku\"", tool.JsonSchema.GetProperty("properties").GetProperty("name").GetProperty("enum").GetRawText());
    }

    [Fact]
    public void Constructor_RefusesNull()
    {
        Assert.Throws<ArgumentNullException>(() => new LoadSkillTool(null!));
    }
}
