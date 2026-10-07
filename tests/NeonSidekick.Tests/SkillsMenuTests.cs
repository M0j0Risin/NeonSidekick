using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class SkillsMenuTests : IDisposable
{
    private readonly TestConsole _console = new();
    /// <summary>The console's output with the strips' badges taken off (<see cref="TabStrips.Unbadged"/>, 2026-10-07): the fixture's own rows move them.</summary>
    private string Output => TabStrips.Unbadged(_console.Output);
    private readonly ManualTimeProvider _time = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly SkillRoots _roots;
    private readonly SkillCatalog _catalog;
    private readonly AppSettings _settings;
    private readonly SpeechSession _speech;
    private bool _enabled = true;
    private bool _external = true;
    private Func<string, string?>? _usage;
    /// <summary>Every file the edit row opened (2026-09-23), and what opening one does: record it, or throw for the failure path.</summary>
    private readonly List<string> _opened = new();
    private Action<string>? _openFile;
    /// <summary>The revert row's seams (2026-10-04): null, no row; the restores the menu asked for.</summary>
    private Func<Skill, IReadOnlyList<SkillRevision>>? _versions;
    private readonly List<SkillRevision> _restored = new();
    /// <summary>The revert list's forgetting (2026-10-07): null, no buttons.</summary>
    private Func<Skill, SkillRevision?, int>? _forget;

    private void OpenFile(string path) => (_openFile ?? _opened.Add)(path);

    public SkillsMenuTests()
    {
        _console.Interactive();
        _console.Profile.Width = 100;
        _console.Profile.Height = 40;
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
        Directory.CreateDirectory(_roots.Profile);
        Directory.CreateDirectory(_roots.Global);
        _catalog = new SkillCatalog(() => _roots);
        _settings = new AppSettings(_dir);
        _speech = new SpeechSession(_ => new FakeSynthesizer { Exists = false }, _ => new FakeAudioPlayback(), new ModelStore(Path.Combine(_dir, "models"), new HttpClient(new StubHttpMessageHandler())));
    }

    public void Dispose()
    {
        _speech.Dispose();
        _settings.Dispose();
        _console.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Push(params ConsoleKeyInfo[] keys)
    {
        foreach (var key in keys)
        {
            _console.Input.PushKey(key);
        }
    }

    /// <summary>A skill written by hand under <paramref name="scope"/>.</summary>
    private void Put(SkillScope scope, string name, string description = "d")
    {
        Directory.CreateDirectory(Path.Combine(_roots.Of(scope), name));
        File.WriteAllText(Path.Combine(_roots.Of(scope), name, SkillCatalog.FileName), $"---\nname: {name}\ndescription: {description}\n---\n\nbody\n");
    }

    private bool Exists(SkillScope scope, string name) => File.Exists(Path.Combine(_roots.Of(scope), name, SkillCatalog.FileName));

    /// <summary>The facts as the screen builds them: a fresh scan every read, off when the fixture or the saved switch (an Options-tab flip) says so.</summary>
    private SkillsFacts Facts()
    {
        if (!_enabled || !_settings.Current.AgentSkills)
        {
            return new SkillsFacts(false, [], [], [], _roots);
        }

        _catalog.Scan(_external);
        return new SkillsFacts(true, _catalog.Skills, _catalog.Shadowed, _catalog.Problems, _roots);
    }

    /// <summary>The settings menu the Options tab's rows are edited through (the /tools fixture's shape).</summary>
    private SettingsMenu Settings(ScreenPane pane, KeySource keys, MenuPane menuPane) =>
        new(new ConsoleWithInput(pane, keys), _settings, _ => null, new InputLine(pane, keys), new TranscriptRenderer(pane), _speech, menuPane, _ => null);

    /// <summary>The menu over a pane with geometry: the list is a level of the pane, the notices its status line.</summary>
    private (SkillsMenu Menu, ScreenPane Pane) PaneMenu()
    {
        var (menu, pane, _) = PaneMenuWithSettings();
        return (menu, pane);
    }

    /// <summary><see cref="PaneMenu"/> with the settings menu the Options tab edits through, for the pickers' root.</summary>
    private (SkillsMenu Menu, ScreenPane Pane, SettingsMenu Settings) PaneMenuWithSettings()
    {
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null), _time) { Hint = () => "idle" };
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var settings = Settings(pane, keys, menuPane);
        var menu = new SkillsMenu(Facts, _settings, settings, new TranscriptRenderer(pane), menuPane, new InputLine(pane, keys), OpenFile, _usage,
            versions: _versions, restore: _versions is null ? null : Restore, forget: _forget);
        pane.Show();
        return (menu, pane, settings);
    }

    /// <summary>The menu over a pane whose cursor row the geometry reports (the click's frame) and a scripted source that carries clicks.</summary>
    private (SkillsMenu Menu, ScreenPane Pane, ScriptedInput Input) ClickablePaneMenu(int cursorTop)
    {
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => cursorTop), _time) { Hint = () => "idle" };
        var input = new ScriptedInput();
        var keys = new KeySource(input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var menu = new SkillsMenu(Facts, _settings, Settings(pane, keys, menuPane), new TranscriptRenderer(pane), menuPane, new InputLine(pane, keys), OpenFile);
        pane.Show();
        return (menu, pane, input);
    }

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    private SkillRevert Restore(Skill skill, SkillRevision revision)
    {
        _restored.Add(revision);
        return new SkillRevert(SkillRevertOutcome.Reverted, revision, null);
    }

    /// <summary><paramref name="before"/>, then the blank rows that hold a tab at its pane's tallest tab's height (2026-10-01) and the footer (its rule since 2026-10-05), then <paramref name="after"/>.</summary>
    private void AssertPadded(string before, string after) =>
        Assert.Matches(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(before) + "(?:(?: |  [^\n]*|" + Rule(100) + ")\n)*" + System.Text.RegularExpressions.Regex.Escape(after)), Output);

    /// <summary>A title row as the pane prints it: the text, then the × close glyph in column width − 2.</summary>
    private static string Titled(string row, int width = 100) => row + new string(' ', width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    private const string Strip = SkillsText.Label + " │ Offered · Reflection · Options ";   // Options last since 2026-09-22 (the user's ask); the Project tab before it went on 2026-10-01, its toggle an Options row; Loaded until 2026-09-19; Options (the settings rows, /settings' Skills tab until then) since later that day; Reflection (the reflection's rows out of Options) later still; the Roots tab after Project until later still that day

    /// <summary>The Options tab's five rows at their defaults, padded to the tab's own column (38: the external-skills label), as the pane prints them; Project file third since 2026-10-01. Pinned.</summary>
    private static readonly string OptionsRows = "▸ Agent skills                          on\n  " + SettingsMenu.ExternalSkillsName + "  off\n  Project file                          on\n  Skill compact mode                    protected\n  #-mention enabled                     on\n";   // Allow skill delete, the fifth, went on 2026-09-23 (delete always offered)

    /// <summary>The Reflection tab's nine rows at their defaults (later on 2026-09-19), padded to its own column (31: the cooldown minutes label). Pinned.</summary>
    private const string ReflectionRows = "▸ Reflection (auto-learn)           on\n  Reflection reasoning              none\n  Reflection window                 3 turns\n  Reflection min tool calls         4 tool calls\n  Reflection max requests           4 requests\n  Reflection cooldown (minutes)     5 minutes\n  Reflection cooldown mode          last-written-skill\n  Reflection includes sessions      on\n  Reflection yields to turns        on\n  Reflection edit supporting files  off\n  Reflection downloaded skills      allow-and-mark\n";

    /// <summary>A row as the menu prints it at width 100: whole when it fits, else cut to 99 cells and an ellipsis (FittedMarkup; the temp roots are long).</summary>
    private static string Fitted(string row) => row.Length <= 100 ? row : row[..99] + "…";

    [Fact]
    public void Strings_ArePinned()
    {
        Assert.Equal("Enter = choose · ESC = back", SkillsMenu.ScopeKeys);
        Assert.Equal("delete", SkillsMenu.DeleteWord);
        Assert.Equal("(kept)", SkillsMenu.KeptNotice);
        Assert.Equal("(🎓 external skills are read only here; move the folder by hand)", SkillsMenu.ExternalReadOnlyNotice);
        Assert.Equal([SkillScope.Profile, SkillScope.Global], SkillsMenu.ScopeRows);
        Assert.Equal(SkillsText.Label + " › haiku", SkillsMenu.ScopeTitle("haiku"));
        Assert.Equal(1, SkillsMenu.ReflectionTab);
        Assert.Equal(2, SkillsMenu.OptionsTab);   // last since 2026-09-22 (second before; fourth until the Project tab went, 2026-10-01)
        Assert.Equal(["Options", "Reflection"], SkillsMenu.SettingsTabTitles);
        Assert.Equal("profile  [#9A8BB8]" + _roots.Profile.Replace("[", "[[", StringComparison.Ordinal) + "[/]", SkillsMenu.ScopeRow(SkillScope.Profile, _roots));
        Assert.Equal("delete   [#9A8BB8]remove the folder and everything in it[/]", SkillsMenu.DeleteRow);
        // The rename (2026-09-21).
        Assert.Equal("rename", SkillsMenu.RenameWord);
        Assert.Equal("rename   [#9A8BB8]give it a new name (letters, digits and hyphens)[/]", SkillsMenu.RenameRow);
        Assert.Equal("(🎓 renamed: haiku → my-haiku)", SkillsMenu.RenamedNotice("haiku", "my-haiku"));
        Assert.Equal("Could not rename skill 'haiku' to 'pdf': the global skills already hold it", SkillsMenu.RenameExistsError("haiku", "pdf", SkillScope.Global));
        Assert.Equal("Could not rename the skill: boom", SkillsMenu.RenameFailedError("boom"));
        // The edit row (2026-09-23): /skills edit <name>'s words, on the status line now.
        Assert.Equal("edit", SkillsMenu.EditWord);
        Assert.Equal("edit     [#9A8BB8]open its SKILL.md in your editor[/]", SkillsMenu.EditRow);
        Assert.Equal(@"(🎓 opened skill ""haiku""'s SKILL.md in your editor: C:\s\haiku\SKILL.md)", SkillsMenu.EditOpenedNotice("haiku", @"C:\s\haiku\SKILL.md"));
        Assert.Equal("Could not open the SKILL.md: boom", SkillsMenu.EditFailedError("boom"));
        Assert.Equal("Move skill 'haiku' from the profile skills to the global skills?", SkillsMenu.MovePrompt("haiku", SkillScope.Profile, SkillScope.Global));
        Assert.Equal("(🎓 moved: haiku → global skills)", SkillsMenu.MovedNotice("haiku", SkillScope.Global));
        Assert.Equal("Could not move skill 'pdf-processing': the global skills already hold 'pdf'", SkillsMenu.ExistsError("pdf-processing", "pdf", SkillScope.Global));
        Assert.Equal("Could not move the skill: boom", SkillsMenu.MoveFailedError("boom"));
        Assert.Equal("Delete skill 'haiku' from the global skills, folder and all?", SkillsMenu.DeletePrompt("haiku", SkillScope.Global));
        Assert.Equal("(🎓 deleted: haiku from the global skills)", SkillsMenu.DeletedNotice("haiku", SkillScope.Global));
        Assert.Equal("Could not delete the skill: boom", SkillsMenu.DeleteFailedError("boom"));
        Assert.Equal("Could not find skill 'haiku' on disk any more; the list was read again", SkillsMenu.MissingError("haiku"));
        // The revert row and its version list (2026-10-04).
        Assert.Equal("revert   [#9A8BB8]pick an earlier version to put back[/]", SkillsMenu.RevertRow);
        Assert.Equal(SkillsText.Label + " › haiku › revert", SkillsMenu.VersionsTitle("haiku"));
        var at = new DateTimeOffset(2026, 10, 4, 14, 5, 0, TimeSpan.Zero);
        Assert.Equal("notes.md [#9A8BB8]not there before a reflection's change at 2026-10-04 14:05[/]", SkillsMenu.VersionRow(new SkillRevision(1, 1, at, "notes.md", null, SkillActors.Reflection), false, 8, TimeZoneInfo.Utc));
        Assert.Equal("SKILL.md   [#9A8BB8]your edit of 2026-10-04 14:05 · current[/]", SkillsMenu.VersionRow(new SkillRevision(1, 1, at, "SKILL.md", "x", SkillActors.User), true, 10, TimeZoneInfo.Utc));
        // The version numbers (2026-10-07): the kept texts counted down from the newest, a revision with no text none; the caption says where the skill is.
        Assert.Equal("v12  SKILL.md [#9A8BB8]your edit of 2026-10-04 14:05[/]", SkillsMenu.VersionRow(new SkillRevision(1, 1, at, "SKILL.md", "x", SkillActors.User), false, 8, TimeZoneInfo.Utc, 12, 3));
        Assert.Equal("     notes.md [#9A8BB8]not there before a reflection's change at 2026-10-04 14:05[/]", SkillsMenu.VersionRow(new SkillRevision(1, 1, at, "notes.md", null, SkillActors.Reflection), false, 8, TimeZoneInfo.Utc, null, 3));
        Assert.Equal(
            [3, 2, null, 1],
            SkillsMenu.VersionNumbers([new SkillRevision(4, 1, at, "SKILL.md", "c", SkillActors.Model), new SkillRevision(3, 1, at, "notes.md", "b", SkillActors.Model), new SkillRevision(2, 1, at, "notes.md", null, SkillActors.Model), new SkillRevision(1, 1, at, "SKILL.md", "a", SkillActors.Model)]));
        Assert.Equal("Now v4. " + SkillRecordText.VersionsCaption, SkillRecordText.VersionsCaptionAt(4));
        Assert.Equal("before a revert at 2026-10-04 14:05", SkillRecordText.VersionText(new SkillRevision(1, 1, at, "SKILL.md", "x", SkillActors.Revert), TimeZoneInfo.Utc));
        Assert.Equal("before the model's change at 2026-10-04 14:05", SkillRecordText.VersionText(new SkillRevision(1, 1, at, "SKILL.md", "x", SkillActors.Model), TimeZoneInfo.Utc));
        Assert.Equal("(↩️ nothing to revert: no earlier version of haiku is kept yet)", SkillRecordText.NoVersionsNotice("haiku"));
    }

    /// <summary>The revert row (2026-10-04): always on the page with the seams, before delete; nothing kept is a notice on the status line, no list.</summary>
    [Fact]
    public async Task RevertRow_IsAlwaysOffered_AndWithNothingKept_SaysSo()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        _versions = _ => [];
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);                                            // the scope page
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // revert
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n  edit     open its SKILL.md in your editor\n  revert   pick an earlier version to put back\n  delete   remove the folder and everything in it\n", Output);
        Assert.Contains("  · " + SkillRecordText.NoVersionsNotice("haiku") + "\n", Output);
        Assert.DoesNotContain(SkillsMenu.VersionsTitle("haiku"), Output);
        Assert.Empty(_restored);
    }

    /// <summary>The version list (2026-10-04, the user's ask): newest first, the one the file holds marked, the cursor on the newest that is not; Enter puts it back, the notice on the list's status line.</summary>
    [Fact]
    public async Task RevertRow_ListsTheVersions_AndEnterPutsThePickBack()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        string current = File.ReadAllText(Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName));
        var at = new DateTimeOffset(2026, 10, 4, 14, 5, 0, TimeSpan.Zero);
        SkillRevision[] kept =
        [
            new(3, 1, at, SkillCatalog.FileName, current, SkillActors.User),
            new(2, 1, at, SkillCatalog.FileName, "old", SkillActors.Model),
            new(1, 1, at, "notes.md", null, SkillActors.Reflection),
        ];
        _versions = _ => kept;
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // revert: the list
        Push(Keys.Enter);                                            // the cursor's: the model's
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        // No notes.md on disk: the version from before it was there is the file's now too.
        // Numbered as the Offered tab counts (2026-10-07): two kept texts, so the skill is at v3; the revision from before notes.md was there is none.
        Assert.Contains("\n" + Titled(SkillsMenu.VersionsTitle("haiku")) + "\n" + SkillRecordText.VersionsCaptionAt(3) + "\n \n" +
            "  v2  SKILL.md your edit of 2026-10-04 14:05 · current\n▸ v1  SKILL.md before the model's change at 2026-10-04 14:05\n      notes.md not there before a reflection's change at 2026-10-04 14:05 · current\n", Output);
        Assert.Equal(2, Assert.Single(_restored).Id);
        Assert.Contains("  · " + SkillRecordText.RevertedNotice("haiku", kept[1], TimeZoneInfo.Utc) + "\n", Output);
    }

    /// <summary>A kept list the forget seam changes, as the records would.</summary>
    private List<SkillRevision> ForgettableVersions(string current)
    {
        var at = new DateTimeOffset(2026, 10, 4, 14, 5, 0, TimeSpan.Zero);
        var kept = new List<SkillRevision>
        {
            new(3, 1, at, SkillCatalog.FileName, current, SkillActors.User),
            new(2, 1, at, SkillCatalog.FileName, "old", SkillActors.Model),
            new(1, 1, at, SkillCatalog.FileName, "older", SkillActors.Model),
        };
        _versions = _ => kept.ToList();
        _forget = (_, revision) =>
        {
            int count = revision is null ? kept.Count : kept.RemoveAll(r => r.Id == revision.Id);
            if (revision is null)
            {
                kept.Clear();
            }

            return count;
        };
        return kept;
    }

    /// <summary>
    /// The revert list's d (2026-10-07, the user's pick): the highlighted version removed after a yes, No on the cursor keeping it; the
    /// list shows again without it, and the buttons' keys are on the hint.
    /// </summary>
    [Fact]
    public async Task RevertList_D_RemovesTheHighlightedVersion_AfterAYes()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var kept = ForgettableVersions(File.ReadAllText(Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName)));
        var removing = kept[1];
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // revert: the list, the cursor on v2 (the newest not current)
        Push(Keys.Char('d'), Keys.Enter);                            // No: kept
        Push(Keys.Char('d'), Keys.Down, Keys.Enter);                 // Yes
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsMenu.VersionsKeys, Output);
        Assert.Contains(SkillRecordText.RemoveVersionQuestion("haiku", "v2"), Output);
        Assert.Contains(SkillRecordText.RemoveVersionCaption(removing, TimeZoneInfo.Utc), Output);
        Assert.Equal("↩️ Remove v2 of haiku?", SkillRecordText.RemoveVersionQuestion("haiku", "v2"));
        Assert.Contains(ChatScreen.KeptNotice, Output);
        Assert.Contains(SkillRecordText.VersionRemovedNotice("haiku", "v2"), Output);
        Assert.Equal([3L, 1L], kept.Select(r => r.Id));
        Assert.Empty(_restored);
        Assert.Equal("(↩️ removed v2 of haiku)", SkillRecordText.VersionRemovedNotice("haiku", "v2"));
        Assert.Equal("Enter = put back · d = remove · c = clear all · ESC = back", SkillsMenu.VersionsKeys);
    }

    /// <summary>The revert list's c (2026-10-07): every kept version removed after a yes; the list goes, with no "nothing kept" notice after it.</summary>
    [Fact]
    public async Task RevertList_C_ClearsEveryVersion_AfterAYes()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var kept = ForgettableVersions(File.ReadAllText(Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName)));
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
        Push(Keys.Char('c'), Keys.Down, Keys.Enter);                 // Yes
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillRecordText.ClearVersionsQuestion("haiku", 3), Output);
        Assert.Contains(SkillRecordText.VersionsClearedNotice("haiku", 3), Output);
        Assert.DoesNotContain(SkillRecordText.NoVersionsNotice("haiku"), Output);
        Assert.Empty(kept);
        Assert.Contains(SkillRecordText.ClearVersionsCaption, Output);
        Assert.Equal("↩️ Remove all 3 kept versions of haiku?", SkillRecordText.ClearVersionsQuestion("haiku", 3));
        Assert.Equal("(↩️ removed 1 kept version of haiku)", SkillRecordText.VersionsClearedNotice("haiku", 1));
    }

    /// <summary>The pane: the three tabs under the strip, the Offered rows first; Enter or Space on a heading does nothing (the page re-shown); the Project file toggle is an Options row since 2026-10-01 (the one row of a Project tab of its own from later on 2026-09-19 until then) — Enter opens its page, off picked, the status line saying so; ESC closes with nothing in the transcript.</summary>
    [Fact]
    public async Task OnThePane_TheThreeTabs_EnterOnAHeadingDoesNothing_TheProjectFileRowFlips()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        Put(SkillScope.Global, "haiku", "The global one.");
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;
        Push(Keys.Down, Keys.Enter, Keys.Char(' '));     // the Shadowed heading: nothing, Enter or Space
        Push(Keys.Left, Keys.Down, Keys.Down, Keys.Char(' '));   // Options (the strip wrapped), Project file: Space flips it off (2026-10-04; nothing until then)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        AssertPadded("\n" + Titled(Strip) + "\n \n▸ haiku  profile  Writes haiku.\n  Shadowed (a higher root holds the name):\n    haiku  global   shadowed by the profile skills\n", Rule(100) + "\n" + SkillsMenu.LoadedKeys + "\n");
        Assert.DoesNotContain("\n" + Titled(SkillsText.Label + " › Project file") + "\n", Output);   // no page: Space saved at once
        Assert.Contains("\n" + Titled(Strip) + "\n  · Project file: off\n  Agent skills                          on\n  " + SettingsMenu.ExternalSkillsName + "  off\n▸ Project file                          off\n", Output);   // the flip on the status line, the row re-read, the cursor kept
        Assert.False(_settings.Current.ProjectFile);
        Assert.DoesNotContain("Project    ", Output);   // no Project tab in the strip
        Assert.DoesNotContain("Roots", Output);
        Assert.DoesNotContain("Working directory", Output);
        Assert.DoesNotContain(SkillsMenu.ScopeTitle("haiku"), Output);
        Assert.False(pane.OverlayOpen);
        Assert.Equal(flow, pane.FlowRow);
        Assert.True(Exists(SkillScope.Profile, "haiku") && Exists(SkillScope.Global, "haiku"));
        pane.Dispose();
    }

    /// <summary>Typing filters the Offered tab (2026-10-03, the user's ask): Enter acts on the skill shown; ESC clears, then closes.</summary>
    [Fact]
    public async Task OnThePane_TypingFiltersTheOfferedTab_EnterOpensTheSkillShown_EscClearsThenCloses()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        Put(SkillScope.Profile, "pdf", "Extracts PDF text.");
        var (menu, pane) = PaneMenu();
        Push("pdf".Select(Keys.Char).ToArray());
        Push(Keys.Enter);                         // pdf's scope page
        Push(Keys.Escape);                        // back to the list, still filtered
        Push(Keys.Escape);                        // the filter cleared
        Push(Keys.Escape);                        // closed

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(MenuFilter.Caption("pdf", 1, 2), Output);
        Assert.Contains("\n▸ pdf    profile  Extracts PDF text.\n", Output);
        Assert.Contains("\n" + Titled(SkillsMenu.ScopeTitle("pdf")) + "\n", Output);
        Assert.DoesNotContain(SkillsMenu.ScopeTitle("haiku"), Output);
        Assert.Contains(MenuFilter.Hint(SkillsMenu.LoadedKeys, "pdf"), Output);
        Assert.Contains("\n▸ haiku  profile  Writes haiku.\n  pdf    profile  Extracts PDF text.\n", Output);   // cleared: both again
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>
    /// The Offered tab's footer (2026-10-05, the user's ask: the band stood blank there): the cursor's skill's whole description,
    /// word-wrapped under the list, following the cursor and the filter.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheOfferedTabsFooter_SaysTheCursorsSkillWhole()
    {
        string longer = "Writes a haiku of seventeen syllables in three lines of five, seven and five about any topic the user names, then explains the season word it chose and why.";
        Put(SkillScope.Profile, "haiku", longer);
        Put(SkillScope.Profile, "pdf", "Extracts PDF text.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Down, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n" + MenuLayout.Footer(new MenuFooter(longer), 100), Output);   // haiku, the first
        Assert.Contains("\n" + MenuLayout.Footer(new MenuFooter("Extracts PDF text."), 100), Output);   // pdf, after Down
        pane.Dispose();
    }

    /// <summary>The toggle saves off and stays off past the pane (the next turn reads it); mid-turn it flips too, as every Options row edits there.</summary>
    [Fact]
    public async Task OnThePane_TheProjectFileRow_SavesOff_MidTurnToo()
    {
        var (menu, pane) = PaneMenu();
        Push(Keys.Left, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);   // Options (the strip wrapped), Project file: off
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None, midTurn: true);

        Assert.False(_settings.Current.ProjectFile);
        Assert.Contains("\n  · Project file: off\n", Output);
        Assert.DoesNotContain(SettingsMenu.NotWhileReplyRunsNotice, Output);
        pane.Dispose();
    }

    /// <summary>The scope page's caption (2026-09-19): the session store's usage line for the skill, under the title, above the rows; none without one.</summary>
    [Fact]
    public async Task ScopePage_CarriesTheUsageCaption_WhenTheHostHasOne()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        _usage = name => name == "haiku" ? "loaded in 2 turns across 1 session; last loaded 2026-09-18 14:05" : null;
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);                            // the scope page
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n" + Titled(SkillsMenu.ScopeTitle("haiku")) + "\nloaded in 2 turns across 1 session; last loaded 2026-09-18 14:05\n \n" + Fitted("▸ profile  " + _roots.Profile) + "\n", Output);
    }

    /// <summary>Enter on a skill row opens the scope page on its scope; the other root, Yes → the folder moved, the list read again with the notice on its status line.</summary>
    [Fact]
    public async Task Move_ToGlobal_Confirmed_MovesTheFolder_AndTheListReReads()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;
        Push(Keys.Enter);                            // the scope page, the cursor on profile
        Push(Keys.Down, Keys.Enter);                 // global
        Push(Keys.Down, Keys.Enter);                 // Yes
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n" + Titled(SkillsMenu.ScopeTitle("haiku")) + "\n \n" + Fitted("▸ profile  " + _roots.Profile) + "\n" + Fitted("  global   " + _roots.Global) + "\n  rename   give it a new name (letters, digits and hyphens)\n  edit     open its SKILL.md in your editor\n  delete   remove the folder and everything in it\n" + Rule(100) + "\n" + SkillsMenu.ScopeKeys + "\n", Output);   // delete always offered since 2026-09-23
        Assert.Contains("\n" + Titled(SkillsMenu.MovePrompt("haiku", SkillScope.Profile, SkillScope.Global)) + "\n \n▸ No\n  Yes\n" + Rule(100) + "\n" + SettingsMenu.ConfirmKeys + "\n", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · " + SkillsMenu.MovedNotice("haiku", SkillScope.Global) + "\n▸ haiku  global   Writes haiku.\n", Output);
        Assert.False(Exists(SkillScope.Profile, "haiku"));
        Assert.True(Exists(SkillScope.Global, "haiku"));
        Assert.Equal(flow, pane.FlowRow);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task Move_No_OrEsc_Keeps()
    {
        Put(SkillScope.Global, "haiku", "Writes haiku.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Enter);       // profile, No
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);      // profile, ESC on the question
        Push(Keys.Enter, Keys.Escape);                           // ESC on the scope page: nothing said
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(2, Output.Split("\n  · " + SkillsMenu.KeptNotice + "\n▸ haiku  global   Writes haiku.\n").Length - 1);
        Assert.True(Exists(SkillScope.Global, "haiku"));
        Assert.False(Exists(SkillScope.Profile, "haiku"));
        pane.Dispose();
    }

    [Fact]
    public async Task TheCurrentScope_IsUnchanged_AndAnExistingDestination_IsTheErrorLine_WithNoQuestion()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        Put(SkillScope.Global, "haiku", "The global one.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Enter);                            // profile again
        Push(Keys.Enter, Keys.Down, Keys.Enter);                 // global: taken
        Push(Keys.Down, Keys.Down, Keys.Enter, Keys.Up, Keys.Enter);   // the shadowed global one (past the heading) → profile: taken too
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n  · " + SettingsMenu.UnchangedNotice + "\n▸ haiku  profile  Writes haiku.\n", Output);
        Assert.Contains("\n  ✗ " + SkillsMenu.ExistsError("haiku", "haiku", SkillScope.Global) + "\n▸ haiku  profile  Writes haiku.\n", Output);
        Assert.Contains("\n  ✗ " + SkillsMenu.ExistsError("haiku", "haiku", SkillScope.Profile) + "\n  haiku  profile  Writes haiku.\n  Shadowed (a higher root holds the name):\n▸   haiku  global   shadowed by the profile skills\n", Output);
        Assert.DoesNotContain("Move skill", Output);
        Assert.True(Exists(SkillScope.Profile, "haiku") && Exists(SkillScope.Global, "haiku"));
        pane.Dispose();
    }

    [Fact]
    public async Task AnExternalSkill_IsReadOnly()
    {
        Put(SkillScope.External, "pdf", "Extracts PDF text.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n  · " + SkillsMenu.ExternalReadOnlyNotice + "\n▸ pdf   external Extracts PDF text.\n", Output);
        Assert.DoesNotContain(SkillsMenu.ScopeTitle("pdf"), Output);
        Assert.True(Exists(SkillScope.External, "pdf"));
        pane.Dispose();
    }

    /// <summary>The delete row is always there (2026-09-23; behind Allow skill delete until then); Yes removes the folder, the list read again (empty here: the none line).</summary>
    [Fact]
    public async Task Delete_IsAlwaysOffered_Confirmed_RemovesTheFolder()
    {
        Put(SkillScope.Global, "haiku", "Writes haiku.");
        Directory.CreateDirectory(Path.Combine(_roots.Global, "haiku", "scripts"));
        File.WriteAllText(Path.Combine(_roots.Global, "haiku", "scripts", "run.py"), "p");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter);                                       // the scope page on global
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);      // delete (past rename, 2026-09-21, and edit, 2026-09-23)
        Push(Keys.Enter);                                       // No: kept
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // delete again
        Push(Keys.Char('y'), Keys.Enter);                       // Yes by hotkey
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n" + Titled(SkillsMenu.ScopeTitle("haiku")) + "\n \n" + Fitted("  profile  " + _roots.Profile) + "\n" + Fitted("▸ global   " + _roots.Global) + "\n  rename   give it a new name (letters, digits and hyphens)\n  edit     open its SKILL.md in your editor\n  delete   remove the folder and everything in it\n", Output);
        Assert.Contains("\n" + Titled(SkillsMenu.DeletePrompt("haiku", SkillScope.Global)) + "\n \n▸ No\n  Yes\n", Output);
        Assert.Contains("\n  · " + SkillsMenu.KeptNotice + "\n▸ haiku  global   Writes haiku.\n", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · " + SkillsMenu.DeletedNotice("haiku", SkillScope.Global) + "\n" + Fitted("▸ " + SkillsText.NoneLine) + "\n", Output);
        Assert.False(Directory.Exists(Path.Combine(_roots.Global, "haiku")));
        pane.Dispose();
    }

    /// <summary>The edit row (2026-09-23, the user's ask; /skills edit &lt;name&gt; until then): the SKILL.md opened in the editor, the status line saying so, nothing moved, the list shown again.</summary>
    [Fact]
    public async Task Edit_OpensTheSkillMd_SaysSoOnTheStatusLine_AndMovesNothing()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter);                                       // the scope page, the cursor on profile
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);      // edit (past global and rename)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string path = Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName);
        Assert.Equal([path], _opened);
        Assert.Contains("\n  edit     open its SKILL.md in your editor\n  delete   remove the folder and everything in it\n", Output);   // the delete row after it, always (2026-09-23)
        Assert.Contains("\n  · " + SkillsMenu.EditOpenedNotice("haiku", "")[..^1], Output);   // the path fitted to the width after it
        Assert.Contains("\n▸ haiku  profile  Writes haiku.\n", Output);
        Assert.True(Exists(SkillScope.Profile, "haiku"));
        pane.Dispose();
    }

    [Fact]
    public async Task Edit_WhenTheEditorFails_IsTheErrorOnTheStatusLine()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        _openFile = _ => throw new System.ComponentModel.Win32Exception("no editor");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // edit
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsMenu.EditFailedError("no editor"), Output);
        Assert.DoesNotContain("opened skill", Output);
        pane.Dispose();
    }

    [Theory]
    [InlineData("My Haiku!!", "my-haiku")]
    [InlineData("  pdf  processing ", "pdf-processing")]
    [InlineData("--a--b--", "a-b")]
    [InlineData("Été_2026", "t-2026")]
    [InlineData("!!!", "")]
    [InlineData("", "")]
    [InlineData("haiku", "haiku")]
    public void KebabName_IsPinned(string typed, string expected)
    {
        Assert.Equal(expected, SkillsMenu.KebabName(typed));
        Assert.True(expected.Length == 0 || SkillFrontmatter.IsValidName(expected));
    }

    [Fact]
    public void KebabName_IsCutToTheLimit_AndNeverEndsOnAHyphen()
    {
        string cut = SkillsMenu.KebabName(new string('x', 63) + "-yy");
        Assert.Equal(63, cut.Length);   // 64 would end on the hyphen: trimmed
        Assert.True(SkillFrontmatter.IsValidName(cut));
        Assert.Equal(64, SkillsMenu.KebabName(new string('x', 70)).Length);
    }

    /// <summary>The rename row (2026-09-21): the slot pre-filled with the name; what is typed is kebab-cased, the folder and the name line follow, the list read again.</summary>
    [Fact]
    public async Task Rename_TypesTheName_KebabCased_MovesTheFolder_RewritesTheNameLine_AndTheListReReads()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        File.WriteAllText(Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName), "---\nname: haiku\ndescription: Writes haiku.\nlicense: MIT\n---\n\nbody\n");
        Directory.CreateDirectory(Path.Combine(_roots.Profile, "haiku", "scripts"));
        File.WriteAllText(Path.Combine(_roots.Profile, "haiku", "scripts", "run.py"), "p");
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;
        Push(Keys.Enter);                                       // the scope page, the cursor on profile
        Push(Keys.Down, Keys.Down, Keys.Enter);                 // rename: the slot with "haiku" in it
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("My Haiku!!");
        Push(Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n▸ rename   give it a new name (letters, digits and hyphens)\n  edit     open its SKILL.md in your editor\n  delete   remove the folder and everything in it\n› \n" + Rule(100) + "\n" + SettingsMenu.EditKeys, Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · " + SkillsMenu.RenamedNotice("haiku", "my-haiku") + "\n▸ my-haiku  profile  Writes haiku.\n", Output);
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "haiku")));
        Assert.True(Exists(SkillScope.Profile, "my-haiku"));
        Assert.Equal("---\nname: my-haiku\ndescription: Writes haiku.\nlicense: MIT\n---\n\nbody\n", File.ReadAllText(Path.Combine(_roots.Profile, "my-haiku", SkillCatalog.FileName)));
        Assert.Equal("p", File.ReadAllText(Path.Combine(_roots.Profile, "my-haiku", "scripts", "run.py")));
        Assert.Equal(flow, pane.FlowRow);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task Rename_AnExistingName_IsRefusedAheadOfTheAct_TheSameName_IsUnchanged_EscKeeps()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        Put(SkillScope.Global, "pdf", "Reads PDFs.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);     // rename
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("PDF");                         // a global skill's name, kebab-cased
        Push(Keys.Enter);
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);     // rename again
        Push(Keys.Enter);                                       // the name it has
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);     // rename again
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("!!!");                         // nothing survives
        Push(Keys.Enter);
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);     // rename again
        Push(Keys.Escape);                                      // ESC on the slot: nothing said
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n  ✗ " + SkillsMenu.RenameExistsError("haiku", "pdf", SkillScope.Global) + "\n▸ haiku  profile  Writes haiku.\n", Output);
        Assert.Contains("\n  · " + SettingsMenu.UnchangedNotice + "\n▸ haiku  profile  Writes haiku.\n", Output);
        Assert.Contains("\n  ✗ " + SkillsMenu.RenameEmptyError + "\n▸ haiku  profile  Writes haiku.\n", Output);
        Assert.DoesNotContain("(renamed:", Output);
        Assert.True(Exists(SkillScope.Profile, "haiku"));
        Assert.True(Exists(SkillScope.Global, "pdf"));
        pane.Dispose();
    }

    /// <summary>A folder that went between the scan and the pick: the error line, the list read again.</summary>
    [Fact]
    public async Task AFolderThatWent_IsTheMissingLine_AndTheListReReads()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        // The folder goes as the scope page opens (its caption is read then; the Allow skill delete switch was, until it went on 2026-09-23): after the scan, before the act.
        _usage = _ => { Directory.Delete(Path.Combine(_roots.Profile, "haiku"), recursive: true); return null; };
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Down, Keys.Enter);     // global
        Push(Keys.Down, Keys.Enter);                 // Yes — but the folder is gone by then
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n  ✗ " + SkillsMenu.MissingError("haiku") + "\n" + Fitted("▸ " + SkillsText.NoneLine) + "\n", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task MidTurn_EnterOnASkill_IsRefused()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None, midTurn: true);

        Assert.Contains("\n  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n▸ haiku  profile  Writes haiku.\n", Output);
        Assert.DoesNotContain(SkillsMenu.ScopeTitle("haiku"), Output);
        pane.Dispose();
    }

    /// <summary>A double-click on a skill row is Enter (the menu's rule); two clicks off the pane close every level, the scope page included.</summary>
    [Fact]
    public async Task ADoubleClickOnARow_OpensTheScopePage_AndTwoClicksOffThePane_CloseEverything()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, pane, input) = ClickablePaneMenu(cursorTop: 100);
        input.PushClick(4, 102);                     // haiku: strip 100, spacer 101
        input.PushClick(4, 102);
        input.PushClick(4, 50);                      // the transcript under the scope page
        input.PushClick(4, 50);
        input.Push(Keys.Escape);                     // never read

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(Titled(SkillsMenu.ScopeTitle("haiku")), Output);
        Assert.False(pane.OverlayOpen);
        Assert.False(pane.Dismissed);
        Assert.True(input.IsAvailable);
        Assert.True(Exists(SkillScope.Profile, "haiku"));
        pane.Dispose();
    }

    [Fact]
    public async Task Off_TheOffLineAlone_EnterDoesNothing()
    {
        _enabled = false;
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n" + Fitted("▸ " + SkillsText.OffLine) + "\n", Output);
        Assert.DoesNotContain(SkillsText.Label + " › ", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task WithoutThePane_PrintsTheThreeTabsAsLines_ToTheTranscript()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var pane = new ScreenPane(_console, geometry: null, _time);
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var settings = new SettingsMenu(_console, _settings, _ => null, new InputLine(_console, keys), new TranscriptRenderer(_console), _speech, menuPane, _ => null);
        var menu = new SkillsMenu(Facts, _settings, settings, new TranscriptRenderer(_console), menuPane, new InputLine(_console, keys), OpenFile);

        await menu.ShowAsync(CancellationToken.None);

        // In the strip's order: Offered, Reflection, Options last (2026-09-22; Options second from 2026-09-19, Reflection later that day), every row as `label: value`; the Project file row among the Options since 2026-10-01 (a Project section of its own until then), no Roots section.
        Assert.Contains("  · Offered\n  ·   haiku  profile  Writes haiku.\n  · Reflection\n  ·   Reflection (auto-learn): on\n  ·   Reflection reasoning: none\n  ·   Reflection window: 3 turns\n  ·   Reflection min tool calls: 4 tool calls\n  ·   Reflection max requests: 4 requests\n  ·   Reflection cooldown (minutes): 5 minutes\n  ·   Reflection cooldown mode: last-written-skill\n  ·   Reflection includes sessions: on\n  ·   Reflection yields to turns: on\n  ·   Reflection edit supporting files: off\n  ·   Reflection downloaded skills: allow-and-mark\n  · Options\n  ·   Agent skills: on\n  ·   " + SettingsMenu.ExternalSkillsName + ": off\n  ·   Project file: on\n  ·   Skill compact mode: protected\n  ·   #-mention enabled: on\n", Output);
        Assert.DoesNotContain("Roots", Output);
    }

    // ── The Options tab (2026-09-19): /settings' Skills tab, hosted here ────

    /// <summary>The Reflection tab is second, after Offered, the Options tab last (2026-09-22; Options second, Reflection third before): the settings rows in the /settings order, each tab padded to its own column; Enter on a toggle opens its page under the strip, the pick saves and shows on the status line.</summary>
    [Fact]
    public async Task OnThePane_TheReflectionTab_IsSecond_TheOptionsTabLast_ItsToggleSaves_AndTheFactsAreReadAgain()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Left);                                        // the strip wraps: Offered → Options
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // Agent skills: the page, off picked
        Push(Keys.Left);                                        // Reflection
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);   // Reflection includes sessions (the last row until Reflection yields to turns on 2026-09-24; Reflection verbose there until later still on 2026-09-19): the page (the cursor on the saved on row), off picked under it
        Push(Keys.Left);                                        // Offered (the off line now)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.AgentSkills);
        Assert.False(_settings.Current.ReflectionIncludesSessions);
        AssertPadded("\n" + Titled(Strip) + "\n \n" + OptionsRows, Rule(100) + "\n" + SettingsMenu.TabKeys + "\n");
        AssertPadded("\n" + Titled(Strip) + "\n \n" + ReflectionRows, Rule(100) + "\n" + SettingsMenu.TabKeys + "\n");   // the flip's notice dropped by the tab switch (2026-09-20)
        Assert.DoesNotContain("\n  · Agent skills: off\n" + ReflectionRows, Output);
        Assert.Contains("\n" + Titled(SkillsText.Label + " › Reflection includes sessions") + "\n", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · Reflection includes sessions: off\n", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · Agent skills: off\n▸ Agent skills                          off\n", Output);
        Assert.Contains("\n" + Fitted("▸ " + SkillsText.OffLine) + "\n", Output);   // the tabs re-read after the flip
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>A picker opened from the Options tab is titled under this pane's word (Skills › …, the scope page's root too), and the settings menu's root is the /settings word again once the pane closes.</summary>
    [Fact]
    public async Task OnThePane_APickerFromTheOptionsTab_IsTitledUnderSkills_AndTheRootComesBack()
    {
        var (menu, pane, settings) = PaneMenuWithSettings();
        Push(Keys.Left);                                        // Options, the strip wrapped
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);      // Skill compact mode (under Project file since 2026-10-01): the picker
        Push(Keys.Down, Keys.Enter);                            // unprotected
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("unprotected", _settings.Current.SkillCompactMode);
        Assert.Contains("\n" + Titled(SkillsText.Label + " › Skill compact mode") + "\n", Output);
        Assert.DoesNotContain(SettingsMenu.Title + " › Skill compact mode", Output);
        Assert.Contains("  · Skill compact mode: unprotected\n", Output);
        Assert.Equal(SettingsMenu.Title, settings.Root);   // restored for /settings
        pane.Dispose();
    }

    /// <summary>Mid-turn the Options rows edit as on /settings (none is refused there) while a scope pick is still refused.</summary>
    [Fact]
    public async Task MidTurn_AnOptionsRowEdits_AScopePickIsRefused()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter);                                       // haiku: the scope page refused
        Push(Keys.Left, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);    // Options (the strip wrapped), #-mention enabled (fifth since 2026-10-01): the page (the cursor on the saved on row, the default), off picked — Allow skill delete, the row this used until it went on 2026-09-23
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None, midTurn: true);

        Assert.False(_settings.Current.SkillHashMention);
        Assert.Contains("  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n", Output);
        Assert.DoesNotContain(SkillsMenu.ScopeTitle("haiku"), Output);
        Assert.Contains("\n" + Titled(SkillsText.Label + " › #-mention enabled") + "\n", Output);
        pane.Dispose();
    }
}
