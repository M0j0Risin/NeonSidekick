using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/skills add</c> on the screen (2026-09-26, the user's ask): the skill downloaded over the stub, previewed as a
/// reply, the scope asked — on the pane with the cursor on Cancel, or as the yes/no question where no pane opens —
/// and the folder written only on a yes.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The pane on and the input scripted (PlanFixture's first lines).</summary>
    private void PaneOn()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        Scripted();
    }

    private void SkillRepoOnCodeload() =>
        _http.Map("https://codeload.github.com/anthropics/skills/zip/", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, SkillZip.Repo("pdf", "docx"), "application/zip")));

    [Fact]
    public async Task SkillsAdd_OnThePane_PreviewsAsks_AndInstallsToTheScopePicked_EscKeepsItOut()
    {
        SkillRepoOnCodeload();
        // The pane on: g then Enter on the first question, ESC on the second, each queued behind its line.
        PaneOn();
        StepsWhenIdle(
            input => { PushLine(input, "/skills add anthropics/skills/pdf"); input.Push(Keys.Char('g'), Keys.Enter); },
            input => { PushLine(input, "/skills add anthropics/skills/docx"); input.Push(Keys.Escape); },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("Does a thing. Use when asked.", output);   // the preview
        Assert.Contains(SkillInstallText.InstallTitle("pdf"), output);
        Assert.Contains(SkillInstallText.ProfileRow, output);
        string folder = Path.Combine(Path.GetFullPath(_settings.GlobalSkillsDirectory), "pdf");
        Assert.True(File.Exists(Path.Combine(folder, "scripts", "run.py")));
        Assert.NotNull(SkillProvenance.Read(folder));
        Assert.Contains(SkillInstallText.KeptNotice("docx"), output);
        Assert.False(Directory.Exists(Path.Combine(_settings.GlobalSkillsDirectory, "docx")));
        Assert.False(Directory.Exists(Path.Combine(ProfileSkills, "docx")));
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task SkillsAdd_OnThePane_AnEnterAloneIsCancel()
    {
        SkillRepoOnCodeload();
        PaneOn();
        StepsWhenIdle(input => { PushLine(input, "/skills add anthropics/skills/pdf"); input.Push(Keys.Enter); }, Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(SkillInstallText.KeptNotice("pdf"), output);
        Assert.False(Directory.Exists(Path.Combine(ProfileSkills, "pdf")));
        Assert.False(Directory.Exists(Path.Combine(_settings.GlobalSkillsDirectory, "pdf")));
    }

    [Fact]
    public async Task SkillsAdd_WithoutThePane_AsksYesOrNo_InTheScopeTheFlagNames()
    {
        _settings.Update(d => d.TtsOutput = false);
        SkillRepoOnCodeload();
        PushLine("/skills add anthropics/skills/pdf");
        PickYes();
        PushLine("/skills add anthropics/skills/docx --global");
        _console.Input.PushKey(Keys.Enter);   // No is on the cursor
        PushLine("/exit");

        string output = await RunAsync();

        string folder = Path.Combine(Path.GetFullPath(ProfileSkills), "pdf");
        Assert.True(File.Exists(Path.Combine(folder, SkillCatalog.FileName)));
        Assert.Contains(SkillInstallText.TypedQuestion("pdf", SkillScope.Profile), output);
        Assert.Contains(SkillInstallText.TypedQuestion("docx", SkillScope.Global), output);
        Assert.Contains(SkillInstallText.KeptNotice("docx"), output);
        Assert.False(Directory.Exists(Path.Combine(_settings.GlobalSkillsDirectory, "docx")));
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// Global skills off (2026-10-13): the install page has no global row and no g; Enter is still Cancel (it sits one row up now), g picks
    /// nothing, and p installs to the profile. Nothing reaches the global folder.
    /// </summary>
    [Fact]
    public async Task SkillsAdd_GlobalOff_ThePageHasNoGlobalRow_EnterIsStillCancel_AndPInstallsToTheProfile()
    {
        SkillRepoOnCodeload();
        PaneOn();
        _settings.Update(d => d.GlobalSkills = false);
        StepsWhenIdle(
            input => { PushLine(input, "/skills add anthropics/skills/pdf"); input.Push(Keys.Enter); },
            input => { PushLine(input, "/skills add anthropics/skills/pdf"); input.Push(Keys.Char('g'), Keys.Escape); },
            input => { PushLine(input, "/skills add anthropics/skills/docx"); input.Push(Keys.Char('p'), Keys.Enter); },
            Line("/exit"));

        string output = await RunAsync();

        Assert.DoesNotContain(SkillInstallText.GlobalRow, output);
        Assert.Contains(SkillInstallText.InstallKeysProfileOnly, output);
        Assert.Contains(SkillInstallText.KeptNotice("pdf"), output);
        Assert.False(Directory.Exists(Path.Combine(_settings.GlobalSkillsDirectory, "pdf")));
        Assert.False(Directory.Exists(Path.Combine(ProfileSkills, "pdf")));
        Assert.True(File.Exists(Path.Combine(ProfileSkills, "docx", SkillCatalog.FileName)));
        Assert.False(Directory.Exists(Path.Combine(_settings.GlobalSkillsDirectory, "docx")));
    }

    /// <summary>Global skills off: --global is refused before anything is fetched.</summary>
    [Fact]
    public async Task SkillsAdd_GlobalOff_DashDashGlobal_IsRefusedBeforeAnyFetch()
    {
        _settings.Update(d => { d.TtsOutput = false; d.GlobalSkills = false; });
        SkillRepoOnCodeload();
        PushLine("/skills add anthropics/skills/pdf --global");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SkillInstallText.GlobalOffError, output);
        Assert.DoesNotContain(_http.Requests, r => r.Uri.Host is "codeload.github.com" or "api.github.com" or "raw.githubusercontent.com" or "skills.sh");   // the LLM probes aside, nothing fetched
        Assert.False(Directory.Exists(Path.Combine(_settings.GlobalSkillsDirectory, "pdf")));
    }

    /// <summary>Global skills off (2026-10-13): the turn lists the profile's skills alone, the editor sentence names no global scope, and skill_editor offers the profile alone.</summary>
    [Fact]
    public async Task Turn_GlobalSkillsOff_ListsTheProfilesSkillsAlone_AndOffersNoGlobalScope()
    {
        _settings.Update(d => { d.TtsOutput = false; d.GlobalSkills = false; });
        string directory = PutSkill(ProfileSkills, "haiku");
        PutSkill(GlobalSkills, "haiku", "The global twin.");
        PutSkill(GlobalSkills, "deploy", "Not read: Global skills is off.");
        _chat.EnqueueText("Hi.");
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        var haiku = new Skill("haiku", "Writes haiku. Use when asked for one.", SkillScope.Profile, directory);
        Assert.Equal(Assistant.SystemPrompt(false, [], web: true, skills: [haiku], sessions: true, git: true, shell: true, native: true, help: true, globalSkills: false), _chat.Requests[0][0].Text);
        Assert.DoesNotContain("deploy", _chat.Requests[0][0].Text);
        Assert.Contains(SkillsPrompt.EditorSentenceProfileOnly, _chat.Requests[0][0].Text);
        var editor = _chat.Options[0]!.Tools!.Cast<AIFunction>().Single(t => t.Name == SkillEditorTool.ToolName);
        Assert.Equal(["profile"], editor.JsonSchema.GetProperty("properties").GetProperty("scope").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }
}
