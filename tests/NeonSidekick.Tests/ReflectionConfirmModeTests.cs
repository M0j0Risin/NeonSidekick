using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

/// <summary><c>Reflection confirm mode</c> (2026-10-12, the user's ask): the setting's words, and <c>skill_editor</c>'s ask before a reflection's write.</summary>
public sealed class ReflectionConfirmModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly SkillRoots _roots;
    private readonly List<SkillProposal> _asked = [];
    private bool _answer;

    public ReflectionConfirmModeTests()
    {
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SkillEditorTool Tool(ReflectionWriteGuard? guard = null) => new(() => _roots, () => false, new SkillFileAccess(TimeProvider.System), guard: guard,
        confirm: (proposal, _) => { _asked.Add(proposal); return Task.FromResult(_answer); });

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values) => new(values.ToDictionary(v => v.Name, v => v.Value));

    private static async Task<string> Invoke(SkillEditorTool tool, params (string Name, object? Value)[] values) =>
        (string)(await tool.InvokeAsync(Args(values), CancellationToken.None))!;

    private string SkillMd(string name) => Path.Combine(_roots.Profile, name, SkillCatalog.FileName);

    [Fact]
    public void Names_Default_AndDescriptions_ArePinned()
    {
        Assert.Equal(["none", "all", "new", "edit"], ReflectionConfirmMode.Names);
        Assert.Equal("new", ReflectionConfirmMode.Default);   // none until 2026-10-18, the user's call
        Assert.Equal(ReflectionConfirmMode.Default, new AppSettingsData().ReflectionConfirmMode);
        Assert.Equal("a reflection never asks", ReflectionConfirmMode.Describe("none"));
        Assert.Equal("a reflection asks before a new skill and before an edit", ReflectionConfirmMode.Describe("all"));
        Assert.Equal("a reflection asks before a new skill, not before an edit", ReflectionConfirmMode.Describe("new"));
        Assert.Equal("a reflection asks before an edit, not before a new skill", ReflectionConfirmMode.Describe("edit"));
        Assert.Equal("", ReflectionConfirmMode.Describe("x"));
    }

    [Theory]
    [InlineData("none", ReflectionConfirmScope.None, true)]
    [InlineData(" Confirm-All ", ReflectionConfirmScope.All, true)]
    [InlineData("new", ReflectionConfirmScope.New, true)]
    [InlineData("edit", ReflectionConfirmScope.Edit, true)]
    [InlineData("always", ReflectionConfirmScope.None, false)]
    [InlineData(null, ReflectionConfirmScope.None, false)]
    public void TryParse_TrimsAndIgnoresCase(string? text, ReflectionConfirmScope expected, bool parsed)
    {
        Assert.Equal(parsed, ReflectionConfirmMode.TryParse(text, out var scope));
        Assert.Equal(expected, scope);
    }

    [Fact]
    public void Asks_FollowsTheScope()
    {
        Assert.False(ReflectionConfirmMode.Asks(ReflectionConfirmScope.None, SkillChangeKind.New));
        Assert.False(ReflectionConfirmMode.Asks(ReflectionConfirmScope.None, SkillChangeKind.Edit));
        Assert.True(ReflectionConfirmMode.Asks(ReflectionConfirmScope.All, SkillChangeKind.New));
        Assert.True(ReflectionConfirmMode.Asks(ReflectionConfirmScope.All, SkillChangeKind.Edit));
        Assert.True(ReflectionConfirmMode.Asks(ReflectionConfirmScope.New, SkillChangeKind.New));
        Assert.False(ReflectionConfirmMode.Asks(ReflectionConfirmScope.New, SkillChangeKind.Edit));
        Assert.False(ReflectionConfirmMode.Asks(ReflectionConfirmScope.Edit, SkillChangeKind.New));
        Assert.True(ReflectionConfirmMode.Asks(ReflectionConfirmScope.Edit, SkillChangeKind.Edit));
    }

    [Fact]
    public void Resolve_ReadsTheSavedWord_AndAnUnknownOne_WarnsAndUsesTheDefault()
    {
        Assert.Equal(ReflectionConfirmScope.New, ReflectionConfirmMode.Resolve(new AppSettingsData { ReflectionConfirmMode = "new" }));
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == SkillCatalog.Category && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(ReflectionConfirmScope.New, ReflectionConfirmMode.Resolve(new AppSettingsData { ReflectionConfirmMode = "always" }));   // the parsed default since 2026-10-18
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal("ReflectionConfirmMode='always' is not one of none, all, new, edit. Using new.", Assert.Single(warnings).Message);
    }

    /// <summary>A create the user says no to writes nothing and answers Declined; yes writes it as ever. The ask carries what it would write.</summary>
    [Fact]
    public async Task ACreate_AsksFirst_NoWritesNothing_YesWritesIt()
    {
        var tool = Tool();
        string no = await Invoke(tool, ("action", "create"), ("name", "haiku"), ("description", "Writes haiku."), ("instructions", "Count syllables."), ("summary", "A haiku helper."));

        Assert.Equal(SkillText.Declined("haiku", SkillChangeKind.New), no);
        Assert.Equal(SkillEditOutcome.Declined, tool.LastResult?.Outcome);
        Assert.False(File.Exists(SkillMd("haiku")));
        var asked = Assert.Single(_asked);
        Assert.Equal(new SkillProposal(SkillChangeKind.New, "create", "haiku", "Writes haiku.", "Count syllables.", Summary: "A haiku helper."), asked);

        _answer = true;
        Assert.StartsWith("created skill 'haiku'", await Invoke(tool, ("action", "create"), ("name", "haiku"), ("description", "Writes haiku."), ("instructions", "Count syllables.")));
        Assert.True(File.Exists(SkillMd("haiku")));
    }

    /// <summary>An update asks with the SKILL.md as it stands; a create for a name that is taken is the editor's refusal, unasked.</summary>
    [Fact]
    public async Task AnUpdate_AsksWithTheSkillAsItStands_AndARefusedCallIsNeverAsked()
    {
        _answer = true;
        var tool = Tool();
        await Invoke(tool, ("action", "create"), ("name", "haiku"), ("description", "Writes haiku."), ("instructions", "Count syllables."));
        string before = File.ReadAllText(SkillMd("haiku"));
        _asked.Clear();
        _answer = false;

        Assert.Equal(SkillText.Declined("haiku", SkillChangeKind.Edit), await Invoke(tool, ("action", "update"), ("name", "haiku"), ("instructions", "Count syllables twice.")));
        var asked = Assert.Single(_asked);
        Assert.Equal(SkillChangeKind.Edit, asked.Kind);
        Assert.Null(asked.Description);
        Assert.Equal("Count syllables twice.", asked.Instructions);
        Assert.Equal(before, asked.Before);
        Assert.Equal(before, File.ReadAllText(SkillMd("haiku")));

        _asked.Clear();
        Assert.StartsWith("Error:", await Invoke(tool, ("action", "create"), ("name", "haiku"), ("description", "Again."), ("instructions", "Again.")));
        Assert.StartsWith("Error:", await Invoke(tool, ("action", "update"), ("name", "nothing-here"), ("instructions", "x")));
        Assert.Empty(_asked);
    }

    /// <summary>A file write asks with its path and content; the guard's refusal (load it first) comes before any ask.</summary>
    [Fact]
    public async Task AFileWrite_AsksWithItsPath_AndTheGuardRefusesFirst()
    {
        _answer = true;
        await Invoke(Tool(), ("action", "create"), ("name", "haiku"), ("description", "Writes haiku."), ("instructions", "Count syllables."));
        _asked.Clear();
        _answer = false;
        var guarded = Tool(new ReflectionWriteGuard(() => _roots, false, ReflectionInstalledPolicy.AllowAndMark));

        Assert.StartsWith("Error:", await Invoke(guarded, ("action", "write_file"), ("name", "haiku"), ("path", "notes.md"), ("content", "hi")));
        Assert.Empty(_asked);

        Assert.Equal(SkillText.Declined("haiku", SkillChangeKind.Edit), await Invoke(Tool(), ("action", "write_file"), ("name", "haiku"), ("path", "notes.md"), ("content", "hi")));
        var asked = Assert.Single(_asked);
        Assert.Equal(("write_file", "notes.md", "hi"), (asked.Action, asked.Path, asked.Content));
        Assert.False(File.Exists(Path.Combine(_roots.Profile, "haiku", "notes.md")));
    }

    [Fact]
    public void TheWords_ArePinned()
    {
        Assert.Equal("the user declined the new skill 'haiku'; nothing was written", SkillText.Declined("haiku", SkillChangeKind.New));
        Assert.Equal("the user declined the change to skill 'haiku'; nothing was written", SkillText.Declined("haiku", SkillChangeKind.Edit));
        Assert.Equal("🧠 A reflection wants to create skill “haiku”", NeonSidekick.App.ChatScreen.LearnAskTitle(new SkillProposal(SkillChangeKind.New, "create", "haiku")));
        Assert.Equal("🧠 A reflection wants to update skill “haiku”", NeonSidekick.App.ChatScreen.LearnAskTitle(new SkillProposal(SkillChangeKind.Edit, "update", "haiku")));
        Assert.Equal("🧠 A reflection wants to change notes.md in skill “haiku”", NeonSidekick.App.ChatScreen.LearnAskTitle(new SkillProposal(SkillChangeKind.Edit, "write_file", "haiku", Path: "notes.md")));
        Assert.Equal("(🧠 discarded: the new skill 'haiku')", NeonSidekick.App.ChatScreen.LearnDeclinedNotice(new SkillEditResult(SkillEditOutcome.Declined, "haiku", SkillScope.Profile, Detail: "create")));
        Assert.Equal("(🧠 discarded: the change to skill 'haiku')", NeonSidekick.App.ChatScreen.LearnDeclinedNotice(new SkillEditResult(SkillEditOutcome.Declined, "haiku", SkillScope.Profile, Detail: "update")));
    }
}
