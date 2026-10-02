using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The reflection's write guard (2026-10-02, the reflection audit): read before rewriting, nothing stale, installed skills by the setting.</summary>
public class ReflectionWriteGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly SkillRoots _roots;
    private readonly SkillCatalog _catalog;

    public ReflectionWriteGuardTests()
    {
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
        Put("haiku");
        _catalog = new SkillCatalog(() => _roots);
        _catalog.Scan(external: false);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Put(string name, string body = "Count the syllables.")
    {
        string dir = Path.Combine(_roots.Profile, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, SkillCatalog.FileName), "---\nname: " + name + "\ndescription: Writes " + name + ".\n---\n" + body + "\n");
        return dir;
    }

    private (SkillEditorTool Editor, LoadSkillTool Loader) Tools(ReflectionInstalledPolicy policy = ReflectionInstalledPolicy.ReadOnly)
    {
        var guard = new ReflectionWriteGuard(() => _roots, false, policy);
        return (new SkillEditorTool(() => _roots, () => false, new SkillFileAccess(new ManualTimeProvider()), guard: guard), new LoadSkillTool(_catalog, used: guard.Loaded));
    }

    [Fact]
    public void ARewrite_NeedsALoadFirst_ADescriptionAloneDoesNot()
    {
        var (editor, loader) = Tools();

        Assert.Equal(SkillText.LoadBeforeRewrite("haiku"), editor.Describe("update", "", "haiku", null, "Blind."));
        Assert.Null(editor.LastResult);
        Assert.Equal(SkillText.LoadBeforeRewrite("haiku"), editor.DescribeWrite("haiku", "data.txt", "x"));
        Assert.StartsWith("updated skill 'haiku'", editor.Describe("update", "", "haiku", "A new description.", null), StringComparison.Ordinal);

        loader.Describe("haiku", null);
        Assert.StartsWith("updated skill 'haiku'", editor.Describe("update", "", "haiku", null, "Read first."), StringComparison.Ordinal);
        Assert.Equal(SkillEditOutcome.Updated, editor.LastResult!.Outcome);
        Assert.StartsWith("Error: there is no skill 'tanka'", editor.Describe("update", "", "tanka", null, "x"), StringComparison.Ordinal);   // no skill: the editor answers
        Assert.StartsWith("created skill 'tanka'", editor.Describe("create", "", "tanka", "Writes tanka.", "x"), StringComparison.Ordinal);   // a create is never held
    }

    [Fact]
    public void ARewrite_OfASkillChangedSinceItsLoad_IsRefused_UntilItIsLoadedAgain()
    {
        var (editor, loader) = Tools();
        loader.Describe("haiku", null);
        Put("haiku", "Someone else's change.");

        Assert.Equal(SkillText.ChangedSinceLoad("haiku"), editor.Describe("update", "", "haiku", null, "Stale."));
        Assert.Contains("Someone else's change.", File.ReadAllText(Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName)), StringComparison.Ordinal);

        loader.Describe("haiku", null);
        Assert.StartsWith("updated skill 'haiku'", editor.Describe("update", "", "haiku", null, "Built on the new text."), StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstalledSkill_IsReadOnly_OrWritable_ByTheSetting()
    {
        string dir = Put("pdf");
        File.WriteAllText(Path.Combine(dir, SkillProvenance.FileName), new SkillProvenance { Repo = "owner/repo" }.ToJson());
        _catalog.Scan(external: false);

        var (readOnly, loader) = Tools(ReflectionInstalledPolicy.ReadOnly);
        loader.Describe("pdf", null);
        Assert.Equal(SkillText.InstalledReadOnly("pdf", "owner/repo"), readOnly.Describe("update", "", "pdf", "A new description.", null));
        Assert.Equal(SkillText.InstalledReadOnly("pdf", "owner/repo"), readOnly.DescribeEdit("pdf", "x.txt", "a", "b", false));

        var (allowed, loader2) = Tools(ReflectionInstalledPolicy.AllowAndMark);
        loader2.Describe("pdf", null);
        Assert.StartsWith("updated skill 'pdf'", allowed.Describe("update", "", "pdf", null, "Changed."), StringComparison.Ordinal);

        // The main chat's editor has no guard: the model in a turn may always change it.
        Assert.StartsWith("updated skill 'pdf'", new SkillEditorTool(() => _roots, () => false).Describe("update", "", "pdf", null, "By the model."), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InTheReflection_ABlindRewriteIsRefused_ThenTheLoadAndTheWriteGoThrough()
    {
        var chat = new FakeChatClient();
        var assistant = new Llm.Assistant(chat, new Llm.ConversationHistory("sys"), new Llm.LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));
        var guard = new ReflectionWriteGuard(() => _roots, false, ReflectionInstalledPolicy.ReadOnly);
        var update = new Dictionary<string, object?> { [SkillEditorTool.ActionArgument] = "update", [SkillEditorTool.NameArgument] = "haiku", [SkillEditorTool.InstructionsArgument] = "Learned." };
        chat.Enqueue(FakeChatClient.Call("r1", SkillEditorTool.ToolName, update));
        chat.Enqueue(FakeChatClient.Call("r2", LoadSkillTool.ToolName, new Dictionary<string, object?> { ["name"] = "haiku" }));
        chat.Enqueue(FakeChatClient.Call("r3", SkillEditorTool.ToolName, update));
        var turn = new List<ChatMessage> { new(ChatRole.User, "write a haiku"), new(ChatRole.Assistant, "Done.") };

        var result = await SkillLearner.RunAsync(assistant, new ReflectionMaterial.Turn(turn, null), _roots, false, ReasoningEffort.None, CancellationToken.None, 4, null, guard: guard);

        Assert.Equal((SkillLearnOutcome.Learned, 3), (result.Outcome, result.Requests));
        var refused = chat.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == "r1");
        Assert.Equal(SkillText.LoadBeforeRewrite("haiku"), (string)refused.Result!);
        Assert.Contains("Learned.", File.ReadAllText(Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName)), StringComparison.Ordinal);
    }
}
