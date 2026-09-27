using System.Net;
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
}
