using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The Advisors tab of <c>/settings</c> (2026-10-10, the profile advisors): its rows, their values and ranges, and the list editor.</summary>
public partial class SettingsMenuTests
{
    [Fact]
    public void TheAdvisorsTab_IsBetweenServersAndVoice_WithItsEightRows()
    {
        int tab = (int)SettingsTab.Advisors;
        Assert.Equal("Advisors", SettingsMenu.TabTitles[tab]);
        Assert.Equal("OpenAI", SettingsMenu.TabTitles[tab - 1]);   // after the Servers group, before Voice, later still on 2026-10-10 (the user's order); after STT once Sessions and Botchat went under Client; after Botchat until then
        Assert.Equal("TTS", SettingsMenu.TabTitles[tab + 1]);
        Assert.Equal((2, 0), SettingsMenu.PlaceOf(SettingsTab.Advisors));
        // The confirm under the iterations, in the Each call run, since later on 2026-10-10 (the user's ask; last until then).
        Assert.Equal(["Advisors enabled", "Advisor list", "Advisor context", "Advisor max calls per turn", "Advisor max tool iterations", "Advisor confirm", "Advisor thread length", "Advisor embedded mode"], SettingsMenu.TabFields[tab].Select(SettingsMenu.FieldName));
        Assert.Equal(["The panel", "Each call", "Threads and servers"], SettingsMenu.TabSections[SettingsTab.Advisors].Select(s => s.Title));
        Assert.Equal([SettingsField.Advisors, SettingsField.AdvisorContext, SettingsField.AdvisorThreadPairs], SettingsMenu.TabSections[SettingsTab.Advisors].Select(s => s.First));
        Assert.Equal("Enabled", SettingsMenu.RowName(SettingsField.Advisors, "Advisors"));
        Assert.All(SettingsMenu.TabFields[tab], f => Assert.False(SettingsMenu.IsLlmField(f), f.ToString()));   // read at each call
        Assert.True(SettingsMenu.IsToggle(SettingsField.Advisors) && SettingsMenu.IsToggle(SettingsField.AdvisorConfirm));
        Assert.False(SettingsMenu.IsToggle(SettingsField.AdvisorList));

        var data = new AppSettingsData();
        Assert.Equal(["off", "none", "brief", "2 calls", "4 round trips", "off", "10 questions", "parent-server"],SettingsMenu.TabFields[tab].Select(f => SettingsMenu.FieldValue(f, data, "C:\\p")));
        Assert.Equal("none (text only)", SettingsMenu.AdvisorIterations(0));
        Assert.Equal("1 round trip", SettingsMenu.AdvisorIterations(1));
        Assert.Equal("1 question", SettingsMenu.AdvisorPairs(1));
        Assert.Equal("must be 1 to 10 calls", SettingsMenu.AdvisorCallsRangeError);
        Assert.Equal("must be 0 to 10 round trips", SettingsMenu.AdvisorIterationsRangeError);
        Assert.Equal("must be 1 to 50 questions", SettingsMenu.AdvisorPairsRangeError);

        var copy = AppSettings.Copy(new AppSettingsData
        {
            Advisors = true, AdvisorList = [new("dba", "database architect")], AdvisorContext = "recent", AdvisorCallsPerTurn = 3,
            AdvisorMaxToolIterations = 0, AdvisorThreadPairs = 5, AdvisorEmbedded = "multi-server", AdvisorConfirm = true,
        });
        Assert.Equal((true, "recent", 3, 0, 5, "multi-server", true), (copy.Advisors, copy.AdvisorContext, copy.AdvisorCallsPerTurn, copy.AdvisorMaxToolIterations, copy.AdvisorThreadPairs, copy.AdvisorEmbedded, copy.AdvisorConfirm));
        Assert.Equal([new AdvisorEntry("dba", "database architect")], copy.AdvisorList);
    }

    [Fact]
    public void TheRoster_ReadsClean_ChangesByCopy_AndFindsByNameOrRole()
    {
        List<AdvisorEntry> saved = [new(" dba ", " database architect "), new("DBA", "dup"), new("", "nobody"), new("ux", "UX designer")];
        var entries = AdvisorRoster.Entries(saved);
        Assert.Equal([new AdvisorEntry("dba", "database architect"), new AdvisorEntry("ux", "UX designer")], entries);
        Assert.Equal("2: dba (database architect), ux (UX designer)", AdvisorRoster.Value(saved));
        Assert.Equal("none", AdvisorRoster.Value(null));
        Assert.EndsWith("…", AdvisorRoster.Value(saved, 20));
        Assert.True(AdvisorRoster.Contains(saved, "UX"));

        Assert.Equal([new AdvisorEntry("dba", "data"), new AdvisorEntry("ux", "UX designer")], AdvisorRoster.Add(saved, new AdvisorEntry("Dba ", "data")).Select(e => e with { Profile = e.Profile.ToLowerInvariant() }));
        Assert.Equal(3, AdvisorRoster.Add(saved, new AdvisorEntry("qa", "tester")).Count);
        Assert.Equal("critic", AdvisorRoster.WithRole(saved, "ux", " critic ")[1].Role);
        Assert.Equal([new AdvisorEntry("ux", "UX designer")], AdvisorRoster.Without(saved, "DBA"));
        Assert.Null(AdvisorRoster.Without([new("dba", "x")], "dba"));

        Assert.Equal("dba", AdvisorRoster.Find(entries, "DBA").Found?.Profile);
        Assert.Equal("ux", AdvisorRoster.Find(entries, "ux designer").Found?.Profile);
        Assert.Equal("dba", AdvisorRoster.Find(entries, "architect").Found?.Profile);   // the one role holding the words
        Assert.Null(AdvisorRoster.Find(entries, "nobody").Found);
        Assert.Equal(2, AdvisorRoster.Find(entries, "nobody").Candidates.Count);
        Assert.Null(AdvisorRoster.Find(entries, "e").Found);   // both roles hold it
        Assert.Equal("ux (UX designer)", AdvisorRoster.Label(entries[1]));
        Assert.Equal("qa", AdvisorRoster.Label(new AdvisorEntry("qa")));
    }

    [Fact]
    public async Task OnThePane_TheAdvisorList_AddsAProfileWithItsRole_ChangesTheRole_AndRemovesAfterAYes()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData());
        Profiles.Create(_dir, "ux", new AppSettingsData());
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Advisors);
        Down(1);
        Push(Keys.Enter);                       // the list: + Add an advisor…
        Push(Keys.Enter);                       // the profiles: dba, ux (not neon, the loaded one)
        Push(Keys.Enter);                       // dba
        _console.Input.PushText("database architect");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // + Add again (the cursor stays on it): ux alone left
        Push(Keys.Enter);
        _console.Input.PushText("UX designer");
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter);            // dba: change the role
        Push(Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);
        _console.Input.PushText("data modeller");
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter);            // ux: remove
        Push(Keys.Down, Keys.Enter);            // Remove
        Push(Keys.Char('y'), Keys.Enter);       // yes
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([new AdvisorEntry("dba", "data modeller")], _settings.Current.AdvisorList);
        Assert.Contains(SettingsMenu.AdvisorAddedNotice(new AdvisorEntry("dba", "database architect")), Output);
        Assert.Contains(SettingsMenu.AdvisorAddedNotice(new AdvisorEntry("ux", "UX designer")), Output);
        Assert.Contains(SettingsMenu.AdvisorRemovedNotice("ux"), Output);
        Assert.Contains(SettingsMenu.RemoveAdvisorQuestion("ux"), Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheAdvisorList_SaysWhenNoProfileIsLeft_AndMarksAMissingOne()
    {
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("gone", "critic")]);
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Advisors);
        Down(1);
        Push(Keys.Enter, Keys.Enter);           // + Add: no other profile
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.NoAdvisorProfilesNotice, Output);
        Assert.Contains("gone  " + "critic  " + SettingsMenu.MissingAdvisorMark, Output);
        Assert.Equal(Markup("gone  ") + Theme.DimMarkup("critic  (missing)"), SettingsMenu.AdvisorRow(new AdvisorEntry("gone", "critic"), exists: false, 6));
        Assert.Equal(Markup("qa  ") + Theme.DimMarkup("(no role)"), SettingsMenu.AdvisorRow(new AdvisorEntry("qa"), exists: true, 4));
        // The avatar leads (later on 2026-10-10), three blanks for none so the names line up, the missing one's too.
        Assert.Equal(Markup("🦉 qa  ") + Theme.DimMarkup("(no role)"), SettingsMenu.AdvisorRow(new AdvisorEntry("qa"), exists: true, 4, avatar: "🦉"));
        Assert.Equal(Markup("   gone  ") + Theme.DimMarkup("critic  (missing)"), SettingsMenu.AdvisorRow(new AdvisorEntry("gone", "critic"), exists: false, 6, avatar: ""));
        Assert.Contains("   gone  critic  " + SettingsMenu.MissingAdvisorMark, Output);
        pane.Dispose();

        static string Markup(string text) => Spectre.Console.Markup.Escape(text);
    }

    [Fact]
    public async Task OnThePane_TheAdvisorListsBand_AndItsEditor_NameEachAdvisorsServerAndModel()
    {
        Profiles.Create(_dir, "doug", new AppSettingsData { LlmUrl = "docker:vllm", LlmModel = "Qwen3.8-27B-NVFP4-RTX5090" });
        Profiles.Create(_dir, "einstein", new AppSettingsData { LlmUrl = "claude-code", ModelAvatar = "🦉" });
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("doug", "expert .NET solution architect"), new AdvisorEntry("einstein", "UX designer")]);
        var doug = new AdvisorServerFacts(AdvisorServer.Docker, "Qwen3.8-27B-NVFP4-RTX5090");
        var einstein = new AdvisorServerFacts(AdvisorServer.ClaudeCode, "sonnet");

        var (menu, pane) = PaneMenu();
        var footer = menu.FieldFooter(SettingsField.AdvisorList, _settings.Current);
        Assert.True(footer.KeepLines);
        Assert.Equal(AdvisorText.Detail(new AdvisorEntry("doug", "expert .NET solution architect"), doug, true) + "\n" + AdvisorText.Detail(new AdvisorEntry("einstein", "UX designer"), einstein, true), footer.Text);
        Assert.Equal(SettingsMenu.FieldFooter(SettingsField.AdvisorList).Last + SettingsMenu.ChangedNote, footer.Last);

        GoTo(SettingsTab.Advisors);
        Down(1);                                // Advisor list: the band
        Push(Keys.Enter);                       // the editor
        Push(Keys.Escape, Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("doug — expert .NET solution architect · docker · Qwen3.8-27B", Output);
        Assert.Contains("einstein — UX designer · claudecode · sonnet", Output);
        Assert.Contains("doug      expert .NET solution architect · docker · Qwen3.8-27B", Output);
        Assert.Contains("einstein  " + "UX designer".PadRight("expert .NET solution architect".Length) + " · claudecode · sonnet", Output);
        Assert.DoesNotContain("· tools", Output);   // no tools/text-only mark since 2026-10-13: every Claude Code advisor has its own process
        // Each led by its profile's avatar (later on 2026-10-10): doug wears the default robot, einstein its owl.
        Assert.Contains(NeonSidekick.Emojis.AvatarRules.DefaultAvatar + " doug      expert", Output);
        Assert.Contains("🦉 einstein  UX designer", Output);
        pane.Dispose();

        // With none listed, the band is the field's description again.
        _settings.Update(d => d.AdvisorList = null);
        Assert.False(menu.FieldFooter(SettingsField.AdvisorList, _settings.Current).KeepLines);
    }

    [Fact]
    public async Task OnThePane_TheAdvisorRows_PickTheContextAndEmbeddedMode_AndRefuseACapOutOfRange()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Advisors);
        Push(Keys.Enter, Keys.Up, Keys.Enter);                       // Advisors enabled: on
        Down(2);
        Push(Keys.Enter, Keys.Down, Keys.Enter);                     // context: recent
        Down(1);
        Push(Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);   // calls per turn
        _console.Input.PushText("11");
        Push(Keys.Enter);                                            // refused
        Down(1);
        Push(Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);   // tool iterations
        _console.Input.PushText("0");
        Push(Keys.Enter);
        Down(3);                                                     // past the confirm and the thread length (the confirm under the iterations since later on 2026-10-10)
        Push(Keys.Enter, Keys.Down, Keys.Enter);                     // embedded: multi-server
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.Advisors);
        Assert.Equal("recent", _settings.Current.AdvisorContext);
        Assert.Equal(2, _settings.Current.AdvisorCallsPerTurn);
        Assert.Equal(0, _settings.Current.AdvisorMaxToolIterations);
        Assert.Equal("multi-server", _settings.Current.AdvisorEmbedded);
        Assert.Contains("Advisor max calls per turn " + SettingsMenu.AdvisorCallsRangeError + "; keeping 2.", Output);
        pane.Dispose();
    }
}
