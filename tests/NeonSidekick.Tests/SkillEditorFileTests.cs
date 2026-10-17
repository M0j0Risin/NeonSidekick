using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary><c>skill_editor</c>'s <c>write_file</c> / <c>edit_file</c> (2026-09-27): a skill's supporting files, never its SKILL.md; the reflection's gate.</summary>
public class SkillEditorFileTests : IDisposable
{
    private static readonly LlmTimeouts Timeouts = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly SkillRoots _roots;
    private readonly SkillEditorTool _tool;
    private bool _external;

    public SkillEditorFileTests()
    {
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
        _tool = new SkillEditorTool(() => _roots, () => _external, new SkillFileAccess(TimeProvider.System));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Put(string root, string name)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, SkillCatalog.FileName), SkillFrontmatter.Write(name, "Maps words to emoji.", [], "1. Read mapping.json."));
        return folder;
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

    private async Task<string> Invoke(SkillEditorTool tool, params (string Name, object? Value)[] values) =>
        (string)(await tool.InvokeAsync(Args(values), CancellationToken.None))!;

    private Task<string> Invoke(params (string Name, object? Value)[] values) => Invoke(_tool, values);

    [Fact]
    public void Schema_OffersTheFileActions_OnlyWithAccess()
    {
        var schema = _tool.JsonSchema.GetProperty("properties");
        Assert.True(_tool.OffersFiles);
        Assert.Equal(["create", "update", "write_file", "edit_file"], schema.GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["action", "scope", "name", "description", "instructions", "path", "content", "old_text", "new_text", "replace_all", "summary"], schema.EnumerateObject().Select(p => p.Name));
        Assert.EndsWith(SkillEditorTool.FilesSentence, _tool.Description, StringComparison.Ordinal);
        Assert.Contains("Never deletes anything", _tool.Description, StringComparison.Ordinal);

        var plain = new SkillEditorTool(() => _roots, () => false);
        Assert.False(plain.OffersFiles);
        Assert.Equal(["create", "update"], plain.JsonSchema.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(SkillEditorTool.FilesSentence, plain.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteFile_CreatesThenReplaces_BesideTheSkillMd()
    {
        string folder = Put(_roots.Profile, "emojese");
        string skillMd = File.ReadAllText(Path.Combine(folder, SkillCatalog.FileName));

        string wrote = await Invoke(("action", "write_file"), ("name", "emojese"), ("path", "data/mapping.json"), ("content", "{\"cat\": \"🐱\"}"), ("summary", "Added cat."));
        Assert.StartsWith("skill 'emojese' (profile): wrote data", wrote, StringComparison.Ordinal);
        Assert.Equal("{\"cat\": \"🐱\"}", File.ReadAllText(Path.Combine(folder, "data", "mapping.json")));
        Assert.Equal(SkillEditOutcome.FileWritten, _tool.LastResult!.Outcome);
        Assert.Equal("Added cat.", _tool.LastResult.Summary);

        string replaced = await Invoke(("action", "write_file"), ("name", "emojese"), ("path", "data/mapping.json"), ("content", "{}"));
        Assert.StartsWith("skill 'emojese' (profile): replaced data", replaced, StringComparison.Ordinal);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(folder, "data", "mapping.json")));
        Assert.Equal(skillMd, File.ReadAllText(Path.Combine(folder, SkillCatalog.FileName)));

        var skill = new SkillCatalog(() => _roots);
        skill.Scan(false);
        Assert.Equal(["data/mapping.json"], SkillCatalog.Resources(skill.Skills.Single(), out _));
    }

    [Fact]
    public async Task EditFile_ReplacesOneOrEvery_AndRefusesAnAmbiguousOne()
    {
        string folder = Put(_roots.Global, "emojese");
        File.WriteAllText(Path.Combine(folder, "mapping.json"), "{\n  \"cat\": \"x\",\n  \"dog\": \"x\"\n}\n");

        string ambiguous = await Invoke(("action", "edit_file"), ("name", "emojese"), ("path", "mapping.json"), ("old_text", "\"x\""), ("new_text", "\"y\""));
        Assert.StartsWith("Error: skill 'emojese' (global): ", ambiguous, StringComparison.Ordinal);
        Assert.Equal(SkillEditOutcome.FileRefused, _tool.LastResult!.Outcome);

        string one = await Invoke(("action", "edit_file"), ("name", "emojese"), ("path", "mapping.json"), ("old_text", "\"cat\": \"x\""), ("new_text", "\"cat\": \"🐱\""));
        Assert.StartsWith("skill 'emojese' (global): edited mapping.json", one, StringComparison.Ordinal);
        Assert.Equal(SkillEditOutcome.FileEdited, _tool.LastResult!.Outcome);

        await Invoke(("action", "edit_file"), ("name", "emojese"), ("path", "mapping.json"), ("old_text", "\"x\""), ("new_text", "\"🐶\""), ("replace_all", true));
        Assert.Equal("{\n  \"cat\": \"🐱\",\n  \"dog\": \"🐶\"\n}\n", File.ReadAllText(Path.Combine(folder, "mapping.json")));

        Assert.Equal("Error: 'maybe' is not true or false for 'replace_all'", await Invoke(("action", "edit_file"), ("name", "emojese"), ("path", "mapping.json"), ("old_text", "a"), ("new_text", "b"), ("replace_all", "maybe")));
    }

    /// <summary>
    /// The toki-pona session's call (2026-10-17): write_file with path, old_text and new_text and no content emptied a 27 KB vocabulary file.
    /// A missing content (or an edit's missing new_text) is refused now, pointing at edit_file; the file is untouched.
    /// </summary>
    [Fact]
    public async Task AMissingContentOrNewText_IsRefused_TheFileUntouched()
    {
        string folder = Put(_roots.Profile, "toki-pona");
        string vocab = Path.Combine(folder, "references", "vocabulary.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(vocab)!);
        File.WriteAllText(vocab, "# Use the highest-weight gloss as the default meaning.\na: [ah 94]\n");

        string answer = await Invoke(("action", "write_file"), ("name", "toki-pona"), ("old_text", "# Use the highest-weight gloss as the default meaning."),
            ("new_text", "# Use the highest-weight gloss as the default meaning.\n\n## Number values"), ("path", "references/vocabulary.txt"));
        Assert.Equal(SkillText.NoContent(sentEdit: true), answer);
        Assert.Equal("Error: content is required: the whole file (\"\" for an empty one); to change part of it use action edit_file", answer);
        Assert.Equal("Error: content is required: the whole file (\"\" for an empty one)", await Invoke(("action", "write_file"), ("name", "toki-pona"), ("path", "references/vocabulary.txt")));
        Assert.Equal("Error: new_text is required: the text to put in place of old_text (\"\" removes it)", await Invoke(("action", "edit_file"), ("name", "toki-pona"), ("path", "references/vocabulary.txt"), ("old_text", "a: [ah 94]")));
        Assert.Equal("# Use the highest-weight gloss as the default meaning.\na: [ah 94]\n", File.ReadAllText(vocab));
        Assert.Null(_tool.LastResult);

        // An explicit "" is still an empty file.
        Assert.StartsWith("skill 'toki-pona' (profile): wrote empty.txt", await Invoke(("action", "write_file"), ("name", "toki-pona"), ("path", "empty.txt"), ("content", "")), StringComparison.Ordinal);

        // A pruned stub copied back from the history is no text (2026-10-17): refused for a file's content, an edit's new_text and an update's instructions.
        string stub = ConversationCompactor.PrunedArgumentStub(27_610);
        Assert.Equal(Files.FileText.PrunedStubSent("content"), await Invoke(("action", "write_file"), ("name", "toki-pona"), ("path", "references/vocabulary.txt"), ("content", stub)));
        Assert.Equal(Files.FileText.PrunedStubSent("new_text"), await Invoke(("action", "edit_file"), ("name", "toki-pona"), ("path", "references/vocabulary.txt"), ("old_text", "a: [ah 94]"), ("new_text", stub)));
        Assert.Equal(Files.FileText.PrunedStubSent("instructions"), await Invoke(("action", "update"), ("name", "toki-pona"), ("instructions", stub)));
        Assert.Equal("# Use the highest-weight gloss as the default meaning.\na: [ah 94]\n", File.ReadAllText(vocab));
    }

    /// <summary>
    /// edit_file on the SKILL.md itself (2026-10-17, the toki-pona session: a one-line change was refused and the whole 10 KB body resent
    /// through update): part of the body, the frontmatter kept, an Updated result with the replaced text as its revision.
    /// </summary>
    [Fact]
    public async Task EditFile_OnTheSkillMd_ChangesPartOfTheBody_ThroughUpdatesRules()
    {
        string folder = Put(_roots.Profile, "toki-pona");
        string skillMd = Path.Combine(folder, SkillCatalog.FileName);
        File.WriteAllText(skillMd, SkillFrontmatter.Write("toki-pona", "Speaks toki pona.", ["license: MIT"], "# Speaking Toki Pona\n\nEstablished words: majuna, soko.\n\nUse li after a subject."));
        string before = File.ReadAllText(skillMd);

        string answer = await Invoke(("action", "edit_file"), ("name", "toki-pona"), ("path", "SKILL.md"), ("old_text", "majuna, soko."), ("new_text", "majuna, soko, epiku."));
        Assert.StartsWith("updated skill 'toki-pona' (profile, ", answer, StringComparison.Ordinal);
        Assert.Equal(SkillEditOutcome.Updated, _tool.LastResult!.Outcome);
        Assert.Equal(before, _tool.LastResult.Previous);
        Assert.True(_tool.LastResult.Existed);
        Assert.Equal(SkillFrontmatter.Write("toki-pona", "Speaks toki pona.", ["license: MIT"], "# Speaking Toki Pona\n\nEstablished words: majuna, soko, epiku.\n\nUse li after a subject."), File.ReadAllText(skillMd));

        // The frontmatter is out of reach: its text is not in the body the edit searches.
        string notFound = await Invoke(("action", "edit_file"), ("name", "toki-pona"), ("path", "./skill.md"), ("old_text", "description: Speaks toki pona."), ("new_text", "description: x"));
        Assert.Equal("Error: skill 'toki-pona' (profile): " + Files.FileText.EditNotFound("SKILL.md")["Error: ".Length..], notFound);
        Assert.Equal(SkillEditOutcome.FileRefused, _tool.LastResult!.Outcome);
        // An edit that leaves no instructions is refused as an empty body.
        Assert.Equal("Error: instructions is empty; write the steps the skill follows",
            await Invoke(("action", "edit_file"), ("name", "toki-pona"), ("path", "SKILL.md"), ("old_text", "# Speaking Toki Pona\n\nEstablished words: majuna, soko, epiku.\n\nUse li after a subject."), ("new_text", "")));
        Assert.Contains("epiku", File.ReadAllText(skillMd), StringComparison.Ordinal);

        // A SKILL.md in a subfolder is a supporting file, edited as one.
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "SKILL.md"), "notes: old");
        Assert.StartsWith("skill 'toki-pona' (profile): edited sub", await Invoke(("action", "edit_file"), ("name", "toki-pona"), ("path", "sub/SKILL.md"), ("old_text", "old"), ("new_text", "new")), StringComparison.Ordinal);
        Assert.True(SkillEditorTool.IsSkillMd(" .\\SKILL.md "));
        Assert.False(SkillEditorTool.IsSkillMd("sub/SKILL.md"));

        // A locked skill refuses it as it refuses update.
        File.WriteAllText(Path.Combine(folder, SkillLock.FileName), "");
        Assert.StartsWith("Error: ", await Invoke(("action", "edit_file"), ("name", "toki-pona"), ("path", "SKILL.md"), ("old_text", "epiku"), ("new_text", "x")), StringComparison.Ordinal);
        Assert.Equal(SkillEditOutcome.Locked, _tool.LastResult!.Outcome);
    }

    [Theory]
    [InlineData("SKILL.md", true)]
    [InlineData("skill.md", true)]
    [InlineData("./SKILL.md", true)]
    [InlineData(".neon-source.json", false)]
    [InlineData(".git/config", false)]
    [InlineData("data/node_modules/x.js", false)]
    public async Task FileActions_NeverWriteTheSkillMdOrTheAppsFiles(string path, bool skillMd)
    {
        string folder = Put(_roots.Profile, "emojese");
        string before = File.ReadAllText(Path.Combine(folder, SkillCatalog.FileName));

        string answer = await Invoke(("action", "write_file"), ("name", "emojese"), ("path", path), ("content", "oops"));

        Assert.Equal(SkillEditOutcome.ProtectedFile, _tool.LastResult!.Outcome);
        Assert.Equal(skillMd
            ? $"Error: '{path}' is the skill itself; change skill 'emojese' with action update (description, instructions), or edit_file for part of its instructions"
            : $"Error: '{path}' in skill 'emojese' is the app's, not the skill's; pick another path", answer);
        Assert.Equal(before, File.ReadAllText(Path.Combine(folder, SkillCatalog.FileName)));
        Assert.False(File.Exists(Path.Combine(folder, SkillProvenance.FileName)));
    }

    [Fact]
    public async Task FileActions_StayInsideTheSkillFolder_AndNeedAnExistingWritableSkill()
    {
        Put(_roots.Profile, "emojese");
        Put(_roots.Profile, "other");

        Assert.StartsWith("Error: skill 'emojese' (profile): ", await Invoke(("action", "write_file"), ("name", "emojese"), ("path", "../other/x.txt"), ("content", "x")), StringComparison.Ordinal);
        Assert.StartsWith("Error: skill 'emojese' (profile): ", await Invoke(("action", "write_file"), ("name", "emojese"), ("path", Path.Combine(_dir, "x.txt")), ("content", "x")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_roots.Profile, "other", "x.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "x.txt")));

        Assert.Equal(SkillText.NoPath, await Invoke(("action", "write_file"), ("name", "emojese"), ("content", "x")));
        Assert.StartsWith("Error: there is no skill 'nope' in the profile skills", await Invoke(("action", "write_file"), ("name", "nope"), ("path", "a.txt"), ("content", "x")), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "nope")));
        Assert.StartsWith("Error: 'Bad Name' is not a valid skill name", await Invoke(("action", "write_file"), ("name", "Bad Name"), ("path", "a.txt"), ("content", "x")), StringComparison.Ordinal);
        Assert.StartsWith("Error: skill 'emojese' (profile): the content is over", await Invoke(("action", "write_file"), ("name", "emojese"), ("path", "big.txt"), ("content", new string('x', Files.WorkingDirectory.MaxWriteChars + 1))), StringComparison.Ordinal);

        _external = true;
        Put(_roots.External, "outside");
        Assert.StartsWith("Error: skill 'outside' exists in the external skills", await Invoke(("action", "write_file"), ("name", "outside"), ("path", "a.txt"), ("content", "x")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_roots.External, "outside", "a.txt")));
    }

    [Fact]
    public async Task WriteFile_ReplacesInPlace_NothingKept()
    {
        // The previous version is gone (its copy into the skill's .trash went with File safe edits, 2026-10-01, the user's call).
        string folder = Put(_roots.Profile, "emojese");
        File.WriteAllText(Path.Combine(folder, "mapping.json"), "old");

        string answer = await Invoke(("action", "write_file"), ("name", "emojese"), ("path", "mapping.json"), ("content", "new"));

        Assert.Contains("replaced mapping.json", answer, StringComparison.Ordinal);
        Assert.Equal("new", File.ReadAllText(Path.Combine(folder, "mapping.json")));
        Assert.False(Directory.Exists(Path.Combine(folder, ".trash")));
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(false);
        Assert.Equal(["mapping.json"], SkillCatalog.Resources(catalog.Skills.Single(), out _));
    }

    [Fact]
    public async Task WithoutAccess_TheFileActionsAreUnknown()
    {
        string folder = Put(_roots.Profile, "emojese");
        var plain = new SkillEditorTool(() => _roots, () => false);

        Assert.Equal("Error: 'write_file' is not an action; use create or update", await Invoke(plain, ("action", "write_file"), ("name", "emojese"), ("path", "a.txt"), ("content", "x")));
        Assert.Equal("Error: 'edit_file' is not an action; use create or update", await Invoke(plain, ("action", "edit_file"), ("name", "emojese"), ("path", "a.txt"), ("old_text", "x"), ("new_text", "y")));
        Assert.Equal("Error: 'write_file' is not an action; use create or update", plain.DescribeWrite("emojese", "a.txt", "x"));
        Assert.False(File.Exists(Path.Combine(folder, "a.txt")));
        Assert.Equal("Error: 'drop' is not an action; use create, update, write_file or edit_file", await Invoke(("action", "drop"), ("name", "emojese")));
    }

    [Fact]
    public void TheTranscriptLine_IsTheResultsFirstLine_WithoutTheColon()
    {
        Assert.Equal("skill 'emojese' (profile): edited mapping.json (line 2, now 4 lines, 6 words)", App.ChatScreen.SkillNoteLine("skill 'emojese' (profile): edited mapping.json (line 2, now 4 lines, 6 words):\n1 | {\n2 | \"cat\""));
        Assert.Equal("updated skill 'x' (profile, 12 bytes)", App.ChatScreen.SkillNoteLine("updated skill 'x' (profile, 12 bytes)"));
        Assert.Equal("a", App.ChatScreen.SkillNoteLine("a\r\nb"));
    }

    // ── The reflection ──────────────────────────────────────────────────────

    private static Dictionary<string, object?> WriteArgs(string name) => new()
    {
        [SkillEditorTool.ActionArgument] = SkillEditorTool.WriteFileAction,
        [SkillEditorTool.NameArgument] = name,
        [SkillEditorTool.PathArgument] = "mapping.json",
        [SkillEditorTool.ContentArgument] = "{}",
    };

    private static List<ChatMessage> Turn() =>
    [
        new ChatMessage(ChatRole.User, "add dog to the mapping"),
        new ChatMessage(ChatRole.Assistant, "Done."),
    ];

    [Fact]
    public async Task AReflection_WithFilesOff_IsRefusedTheFileActions_AndIsNotToldOfThem()
    {
        string folder = Put(_roots.Profile, "emojese");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Call("r1", SkillEditorTool.ToolName, WriteArgs("emojese")));
        client.EnqueueText("nothing");
        var assistant = new Assistant(client, new ConversationHistory("sys"), Timeouts);

        var result = await SkillLearner.RunAsync(assistant, new ReflectionMaterial.Turn(Turn(), null), _roots, false, ReasoningEffort.None, CancellationToken.None, 4, null);

        Assert.Equal(SkillLearnOutcome.Nothing, result.Outcome);
        Assert.False(File.Exists(Path.Combine(folder, "mapping.json")));
        Assert.DoesNotContain(SkillLearner.SupportingFilesInstruction, client.Requests[0][0].Text, StringComparison.Ordinal);
        var answer = client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == "r1");
        Assert.Equal("Error: 'write_file' is not an action; use create or update", answer.Result);
    }

    [Fact]
    public async Task AReflection_WithFilesOn_WritesTheFile_AndThatEndsThePass()
    {
        string folder = Put(_roots.Profile, "emojese");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Call("r1", SkillEditorTool.ToolName, WriteArgs("emojese")));
        var assistant = new Assistant(client, new ConversationHistory("sys"), Timeouts);

        var result = await SkillLearner.RunAsync(assistant, new ReflectionMaterial.Turn(Turn(), null), _roots, false, ReasoningEffort.None, CancellationToken.None, 4, null, new SkillFileAccess(TimeProvider.System));

        Assert.Equal(SkillLearnOutcome.Learned, result.Outcome);
        Assert.Equal(SkillEditOutcome.FileWritten, result.Edit!.Outcome);
        Assert.Equal("emojese", result.Edit.Name);
        Assert.Equal(1, result.Requests);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(folder, "mapping.json")));
        Assert.Contains(SkillLearner.SupportingFilesInstruction, client.Requests[0][0].Text, StringComparison.Ordinal);
        var tool = Assert.Single(client.Options)!.Tools!.OfType<SkillEditorTool>().Single();
        Assert.True(tool.OffersFiles);
    }
}
