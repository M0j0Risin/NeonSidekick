using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The Advisors tab's templates (2026-11-22, the user's ask): adding a profile with a template's persona or its own, a new advisor from a
/// template, and an advisor's description and persona (a template, the editor, the default, saved as your template and deleted).
/// </summary>
public partial class SettingsMenuTests
{
    private string PersonaOf(string profile) => File.ReadAllText(Path.Combine(Profiles.Directory(_dir, profile), PersonaFile.FileName));

    private void WritePersona(string profile, string text) => new PersonaFile(Profiles.Directory(_dir, profile)).WriteText(text);

    private static AdvisorTemplate BuiltInTemplate(string id) => AdvisorTemplates.BuiltIn.Find(id)!;

    /// <summary>Opens <c>Advisor list</c> on the pane: the tab, its second row, Enter.</summary>
    private void OpenAdvisorList()
    {
        GoTo(SettingsTab.Advisors);
        Down(1);
        Push(Keys.Enter);
    }

    [Fact]
    public async Task OnThePane_AddingAProfile_WithATemplate_WritesItsPersona_AndPrefillsTheRoleAndDescription()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData());
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        Push(Keys.Enter);                       // + Add an advisor…
        Push(Keys.Enter);                       // dba
        Push(Keys.Char('d'), Keys.Enter);       // the persona page: d jumps past Keep its persona to dba
        Push(Keys.Enter);                       // the role, prefilled
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var dba = BuiltInTemplate("dba");
        Assert.Equal([new AdvisorEntry("dba", dba.Role, dba.Description)], _settings.Current.AdvisorList);
        Assert.Equal(PersonaFile.Normalize(dba.Persona), PersonaFile.Normalize(PersonaOf("dba")));
        Assert.Contains("dba — database administrator", Output);
        Assert.Contains(SettingsMenu.KeepPersonaRow, Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_KeepingItsPersona_TakesTheMatchedTemplatesRole_OrNone()
    {
        Profiles.Create(_dir, "pm", new AppSettingsData());
        Profiles.Create(_dir, "ux", new AppSettingsData());
        WritePersona("ux", BuiltInTemplate("ux").Persona.ReplaceLineEndings("\r\n"));
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        Push(Keys.Enter, Keys.Down, Keys.Enter);   // + Add: ux
        Push(Keys.Enter);                          // Keep its persona: ux's, the ux template's
        Push(Keys.Enter);                          // the role, the template's
        Push(Keys.Enter, Keys.Enter);              // + Add: pm
        Push(Keys.Enter);                          // Keep its persona: none
        _console.Input.PushText("manager");
        Push(Keys.Enter);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var ux = BuiltInTemplate("ux");
        Assert.Equal([new AdvisorEntry("ux", ux.Role, ux.Description), new AdvisorEntry("pm", "manager")], _settings.Current.AdvisorList);
        Assert.Contains("🧭 ux — UX designer " + SettingsMenu.CurrentTemplateMark, Output);
        Assert.False(File.Exists(Path.Combine(Profiles.Directory(_dir, "pm"), PersonaFile.FileName)));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ATemplateOverAProfilesOwnPersona_AsksFirst_AndANoOrEscAddsNothing()
    {
        Profiles.Create(_dir, "rex", new AppSettingsData());
        WritePersona("rex", "You are Rex.");
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        Push(Keys.Enter, Keys.Enter);           // + Add: rex
        Push(Keys.Escape);                      // the persona page: Esc, nothing
        Push(Keys.Enter, Keys.Enter);           // + Add: rex again
        Push(Keys.Char('q'), Keys.Enter);       // qa
        Push(Keys.Enter);                       // the role
        Push(Keys.Enter);                       // Replace rex's persona.md? No
        Push(Keys.Enter, Keys.Enter);           // + Add: rex again
        Push(Keys.Char('q'), Keys.Enter, Keys.Enter);
        Push(Keys.Char('y'), Keys.Enter);       // yes
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var qa = BuiltInTemplate("qa");
        Assert.Equal([new AdvisorEntry("rex", qa.Role, qa.Description)], _settings.Current.AdvisorList);
        Assert.Contains(SettingsMenu.ReplaceAdvisorPersonaQuestion("rex", "qa"), Output);
        Assert.Contains(ChatScreen.KeptNotice, Output);
        Assert.Equal(PersonaFile.Normalize(qa.Persona), PersonaFile.Normalize(PersonaOf("rex")));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ANewAdvisorFromATemplate_MakesTheProfile_SLandsOnSecurity_AndATakenOrTemporaryNameAsksAgain()
    {
        Profiles.Create(_dir, "security", new AppSettingsData());
        _settings.Update(d => d.LlmModel = "m1");
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        Push(Keys.Down, Keys.Enter);            // + New advisor from a template…
        Push(Keys.Char('s'), Keys.Enter);       // s: security
        Push(Keys.Enter);                       // the name, security: taken
        Push(Keys.Ctrl(ConsoleKey.A), Keys.Backspace);
        _console.Input.PushText("_sec");
        Push(Keys.Enter);                       // temporary: refused
        Push(Keys.Ctrl(ConsoleKey.A), Keys.Backspace);
        _console.Input.PushText("sec");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // the role, the template's
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var security = BuiltInTemplate("security");
        var entry = new AdvisorEntry("sec", security.Role, security.Description);
        Assert.Equal([entry], _settings.Current.AdvisorList);
        Assert.Contains(SettingsMenu.AdvisorNameTakenError("security", listed: false), Output);
        Assert.Contains(AdvisorText.TemporaryNameError, Output);
        Assert.Contains(AdvisorText.CreatedNotice(entry, advisorsOn: false)[..30], Output);
        Assert.Equal("neon", _settings.ProfileName);   // no switch
        var data = Profiles.ReadProfileFile(Profiles.ProfileFile(_dir, "sec"));
        Assert.Equal(("m1", "🛡️"), (data.LlmModel, data.ModelAvatar));
        Assert.Equal(PersonaFile.Normalize(security.Persona), PersonaFile.Normalize(PersonaOf("sec")));
        Assert.Contains(SettingsMenu.NewAdvisorBand, Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ChangeThePersona_ToATemplate_OffersItsRole_AndTakesItsDescriptionOnlyOverABlankOne()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData());
        Profiles.Create(_dir, "ux", new AppSettingsData());
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "my role"), new AdvisorEntry("ux", "designer", "hand-written")]);
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        Push(Keys.Down, Keys.Down, Keys.Enter); // dba
        Push(Keys.Down, Keys.Down, Keys.Enter); // Change the persona
        Push(Keys.Char('q'), Keys.Enter);       // qa
        Push(Keys.Enter);                       // its role, offered
        Push(Keys.Down, Keys.Enter);            // ux
        Push(Keys.Down, Keys.Down, Keys.Enter); // Change the persona
        Push(Keys.Char('d'), Keys.Enter);       // dba
        Push(Keys.Escape);                      // its role: kept as it was
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var qa = BuiltInTemplate("qa");
        Assert.Equal([new AdvisorEntry("dba", qa.Role, qa.Description), new AdvisorEntry("ux", "designer", "hand-written")], _settings.Current.AdvisorList);
        Assert.Contains(SettingsMenu.AdvisorPersonaNotice("dba", "qa"), Output);
        Assert.Equal(PersonaFile.Normalize(BuiltInTemplate("dba").Persona), PersonaFile.Normalize(PersonaOf("ux")));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ChangeTheDescription_SavesIt_Folded_AndTheDeleteShortcutStillRemoves()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData());
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect")]);
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        Push(Keys.Down, Keys.Down, Keys.Enter); // dba
        Push(Keys.Down, Keys.Enter);            // Change the description
        _console.Input.PushText("schema, indexes");
        Push(Keys.Enter);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([new AdvisorEntry("dba", "architect", "schema, indexes")], _settings.Current.AdvisorList);
        Assert.Contains(SettingsMenu.AdvisorDescriptionNotice(new AdvisorEntry("dba", "architect", "schema, indexes")), Output);
        Assert.Contains(SettingsMenu.NoDescriptionBand, Output);   // the row's band before
        Assert.Equal(5, SettingsMenu.RemoveAdvisorChoice);
        Assert.Equal("Remove", SettingsMenu.AdvisorEntryRows[SettingsMenu.RemoveAdvisorChoice]);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_EditDefaultAndSave_ThenDeleteYours_BringsTheBuiltInBack_AndDeleteOnABuiltInDoesNothing()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData { ModelAvatar = "🦉" });
        WritePersona("dba", "You are my DBA.");
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect", "tables")]);
        var opened = new List<string>();
        var (menu, pane) = PaneMenu(openFile: opened.Add);
        int templates = AdvisorTemplates.BuiltIn.Templates.Count;
        OpenAdvisorList();
        // Save as a template, under the name dba (a built-in's): the last row.
        Push(Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);
        for (int i = 0; i < templates + 2; i++)
        {
            Push(Keys.Down);
        }

        Push(Keys.Enter);                       // Save as a template…
        Push(Keys.Enter);                       // its name: dba
        // Edit in your editor: the first row after the templates.
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);
        for (int i = 0; i < templates; i++)
        {
            Push(Keys.Down);
        }

        Push(Keys.Enter);
        // Delete on architect (a built-in) does nothing; on dba (yours now) asks, and removes it.
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);
        Push(Keys.Delete);
        Push(Keys.Down, Keys.Delete);
        Push(Keys.Char('y'), Keys.Enter);
        // Default persona: the row after Edit (the cursor on dba's row, the second).
        for (int i = 0; i < templates; i++)
        {
            Push(Keys.Down);
        }

        Push(Keys.Enter);
        Push(Keys.Char('y'), Keys.Enter);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.TemplateSavedNotice("dba", shadows: true), Output);
        Assert.Contains("dba — architect " + AdvisorText.YoursMark, Output);
        Assert.Equal([Path.Combine(Profiles.Directory(_dir, "dba"), PersonaFile.FileName)], opened);
        Assert.Contains(SettingsMenu.AdvisorPersonaEditingNotice("dba"), Output);
        Assert.Contains(SettingsMenu.DeleteTemplateQuestion("dba"), Output);
        Assert.DoesNotContain(SettingsMenu.DeleteTemplateQuestion("architect"), Output);
        Assert.Contains(SettingsMenu.TemplateDeletedNotice("dba", Files.DeleteWay.Deleted, builtInBack: true), Output);
        Assert.False(AdvisorTemplates.Find(_dir, "dba")!.Yours);
        Assert.Contains(SettingsMenu.AdvisorDefaultPersonaNotice("dba"), Output);
        Assert.False(File.Exists(Path.Combine(Profiles.Directory(_dir, "dba"), PersonaFile.FileName)));
        pane.Dispose();
    }

    [Fact]
    public void SavingAsATemplate_KeepsAPickedAvatar_AndOnlyThat()
    {
        AdvisorTemplates.SaveYours(_dir, "mine", "You are mine.", "role", "when", "🦉");
        Assert.Equal("🦉", AdvisorTemplates.Find(_dir, "mine")!.Avatar);
        Assert.Equal("(saved your qa template; it is used in place of the built-in one)", SettingsMenu.TemplateSavedNotice("qa", shadows: true));
        Assert.Equal("(saved your critic template)", SettingsMenu.TemplateSavedNotice("critic", shadows: false));
    }

    [Fact]
    public async Task OnThePane_SaveWithNoPersona_SaysThereIsNothingToSave_AndAMissingProfileHasNoPersonaToChange()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData());
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect"), new AdvisorEntry("gone", "critic")]);
        var (menu, pane) = PaneMenu();
        int templates = AdvisorTemplates.BuiltIn.Templates.Count;
        OpenAdvisorList();
        Push(Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);
        for (int i = 0; i < templates + 1; i++)   // no editor: Default persona, then Save
        {
            Push(Keys.Down);
        }

        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);   // gone: Change the persona
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.NothingToSaveNotice("dba"), Output);
        Assert.DoesNotContain(SettingsMenu.EditPersonaRow, Output);
        Assert.Contains(SettingsMenu.MissingAdvisorPersonaNotice("gone"), Output);
        Assert.False(Directory.Exists(AdvisorTemplates.Directory(_dir)));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AHomeTemplatesRole_IsEscaped()
    {
        string folder = Path.Combine(AdvisorTemplates.Directory(_dir), "crit");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "persona.md"), "You are a critic.");
        File.WriteAllText(Path.Combine(folder, "advisor.json"), "{\"role\": \"[bold]tester[/]\"}");
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        Push(Keys.Down, Keys.Enter);            // + New advisor from a template…
        Push(Keys.Char('c'));                   // crit: its band
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("crit — [bold]tester[/] " + AdvisorText.YoursMark, Output);
        Assert.Contains("You are a critic.", Output);   // its band: the persona's first sentence
        pane.Dispose();
    }

    /// <summary>With the pane off, a new advisor from a template is the Spectre prompts and the input line.</summary>
    [Fact]
    public async Task WithThePaneOff_ANewAdvisorFromATemplate_Works()
    {
        _console.Input.PushKey(ConsoleKey.DownArrow);   // + New advisor from a template…
        _console.Input.PushKey(ConsoleKey.Enter);
        _console.Input.PushKey(ConsoleKey.DownArrow);   // dba, after architect
        _console.Input.PushKey(ConsoleKey.Enter);
        _console.Input.PushKey(ConsoleKey.Enter);       // its name: dba
        _console.Input.PushKey(ConsoleKey.Enter);       // its role
        _console.Input.PushKey(ConsoleKey.Escape);

        await _menu.EditAdvisorListAsync(CancellationToken.None);

        var dba = BuiltInTemplate("dba");
        Assert.Equal([new AdvisorEntry("dba", dba.Role, dba.Description)], _settings.Current.AdvisorList);
        Assert.True(Profiles.Exists(_dir, "dba"));
    }

    [Fact]
    public void TheAdvisorTexts_ArePinned()
    {
        Assert.Equal("+ New advisor from a template…", SettingsMenu.NewAdvisorRow);
        Assert.Equal(["Change the role", "Change the description", "Change the persona", "Change the server", "Switch to profile", "Remove"], SettingsMenu.AdvisorEntryRows);
        Assert.Equal("Advisor list: dba has no description", SettingsMenu.AdvisorDescriptionNotice(new AdvisorEntry("dba", "x")));
        Assert.Equal("Advisor list: dba: schema", SettingsMenu.AdvisorDescriptionNotice(new AdvisorEntry("dba", "x", "schema")));
        Assert.Equal("(moved your qa template to the " + Files.FileText.RecycleBinName + "; the built-in one is back)", SettingsMenu.TemplateDeletedNotice("qa", Files.DeleteWay.Recycled, builtInBack: true));
        Assert.Equal("(no profile left to add: + New advisor from a template… or /profile add makes one)", SettingsMenu.NoAdvisorProfilesNotice);
        Assert.Equal(
            Spectre.Console.Markup.Escape("🗄️ dba — database administrator") + Theme.DimMarkup(Spectre.Console.Markup.Escape(" (yours) (current)")),
            SettingsMenu.AdvisorTemplateRow(BuiltInTemplate("dba") with { Yours = true }, current: true));
    }

    [Fact]
    public void TheRoster_KeepsADescription_TrimmedFoldedAndCapped_ThroughEveryChange()
    {
        List<AdvisorEntry> saved = [new("dba", "architect", "  schema,\n indexes  "), new("ux", "designer", null!)];
        var entries = AdvisorRoster.Entries(saved);
        Assert.Equal([new AdvisorEntry("dba", "architect", "schema,  indexes"), new AdvisorEntry("ux", "designer")], entries);
        Assert.Equal("schema,  indexes", AdvisorRoster.WithRole(saved, "dba", "data")[0].Description);
        Assert.Equal("tables", AdvisorRoster.WithDescription(saved, "DBA", " tables ")[0].Description);
        Assert.Equal(new AdvisorEntry("dba", "new role", "new desc"), AdvisorRoster.Add(saved, new AdvisorEntry("dba", "new role", "new desc"))[0]);
        string longText = new('x', 200);
        Assert.Equal(AdvisorRoster.DescriptionMax, AdvisorRoster.CleanDescription(longText).Length);
        Assert.EndsWith("…", AdvisorRoster.CleanDescription(longText));

        Assert.Equal("dba (architect): schema", AdvisorRoster.ToolLabel(new AdvisorEntry("dba", "architect", "schema")));
        Assert.Equal("dba: schema", AdvisorRoster.ToolLabel(new AdvisorEntry("dba", "", "schema")));
        Assert.Equal("dba (architect)", AdvisorRoster.ToolLabel(new AdvisorEntry("dba", "architect")));
        Assert.EndsWith("Advisors: dba (architect): schema; ux (designer).", AdvisorText.Description([new AdvisorEntry("dba", "architect", "schema"), new AdvisorEntry("ux", "designer")]));

        var facts = new AdvisorServerFacts(AdvisorServer.Docker, "m");
        Assert.Equal("  dba — architect · docker · m · schema", AdvisorText.ListLine(new AdvisorEntry("dba", "architect", "schema"), facts, exists: true));
        Assert.Equal("  " + AdvisorText.Detail(new AdvisorEntry("dba", "architect"), facts, true), AdvisorText.ListLine(new AdvisorEntry("dba", "architect"), facts, exists: true));

        // An old profile.json with no description reads it blank.
        var data = System.Text.Json.JsonSerializer.Deserialize("{\"AdvisorList\":[{\"Profile\":\"dba\",\"Role\":\"x\"}]}", SettingsJsonContext.Default.AppSettingsData)!;
        Assert.Equal([new AdvisorEntry("dba", "x")], AdvisorRoster.Entries(data.AdvisorList));
    }
}
