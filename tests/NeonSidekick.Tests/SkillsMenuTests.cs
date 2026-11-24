using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
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

    /// <summary>A repo's own skills (2026-11-23): null, none read; the trust store the project sub-tab saves to, and every View it opened.</summary>
    private ProjectSkillSource? _projectSource;
    private ProjectSkillTrust? _projectTrust;
    private string? _working;
    private readonly List<string> _viewed = new();
    private readonly List<Spectre.Console.Rendering.IRenderable> _viewedContent = new();   // each View's Source tab (its last)

    // Each View's tabs (2026-11-24): Rendered then Source, or one plain tab.
    private readonly List<IReadOnlyList<InfoTab>> _viewedTabs = new();
    /// <summary>What a View does besides recording it (2026-11-23, review #44: an edit made while the question is open).</summary>
    private Action<string>? _onView;
    /// <summary>The conversation the menu reads loaded skills from (2026-11-23, review #5); null, none.</summary>
    private ConversationHistory? _session;
    /// <summary>The scope page's project row's level (2026-11-23): the working folder unless a test hides the row; the bin a project skill's delete uses.</summary>
    private bool _projectTarget = true;
    private FakeRecycleBin? _bin;

    /// <summary>A repo under the fixture's folder as the working folder, its skills read and trusted through the fixture's store.</summary>
    private string UseRepo()
    {
        _working = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(_working, ".git"));
        _projectTrust = new ProjectSkillTrust(() => Path.Combine(_dir, "profile", "configs"), _time);
        _projectSource = new ProjectSkillSource(() => _working, Path.Combine(_dir, "userhome"), null, _projectTrust);
        return _working;
    }

    private static string PutProject(string level, string name, string description, bool claude = false)
    {
        string folder = Path.Combine(level, claude ? ".claude" : ".agents", "skills", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, SkillCatalog.FileName), $"---\nname: {name}\ndescription: {description}\n---\n\nThe repo's steps.\n");
        return folder;
    }

    /// <summary>Trusts <paramref name="level"/>'s skills as they stand, as the trust question would.</summary>
    private void TrustRepo(string level)
    {
        var state = _catalog.ReadProjectFolder(level, _working);
        Assert.Equal(ProjectTrustSave.Saved, _projectTrust!.Trust(level, state.Fingerprint, () => _catalog.ReadProjectFolder(level, _working)));
    }

    public SkillsMenuTests()
    {
        _console.Interactive();
        _console.Profile.Width = 100;
        _console.Profile.Height = 40;
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
        Directory.CreateDirectory(_roots.Profile);
        Directory.CreateDirectory(_roots.Global);
        _catalog = new SkillCatalog(() => Roots);
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

    /// <summary>The roots with the saved Global skills switch, as <see cref="SkillRoots.For"/> reads it (2026-10-13).</summary>
    private SkillRoots Roots => _roots with { GlobalOn = _settings.Current.GlobalSkills };

    private bool Exists(SkillScope scope, string name) => File.Exists(Path.Combine(_roots.Of(scope), name, SkillCatalog.FileName));

    /// <summary>The facts as the screen builds them: a fresh scan every read, off when the fixture or the saved switch (an Options-tab flip) says so.</summary>
    private SkillsFacts Facts()
    {
        if (!_enabled || !_settings.Current.AgentSkills)
        {
            return new SkillsFacts(false, [], [], [], Roots);
        }

        if (_projectSource is not null)
        {
            _catalog.Scan(new SkillScan(_external, _projectSource));
            return new SkillsFacts(true, _catalog.All, _catalog.Shadowed, _catalog.Problems, Roots, Pending: _catalog.ProjectPending, Folders: _catalog.ProjectFolders, WorkingFolder: _working, RepoRoot: _working,
                ProjectTarget: _projectTarget ? _working : null);
        }

        _catalog.Scan(_external);
        return new SkillsFacts(true, _catalog.All, _catalog.Shadowed, _catalog.Problems, Roots);   // the screen's list: the disabled ones too (2026-10-09)
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
            versions: _versions, restore: _versions is null ? null : Restore, forget: _forget,
            catalog: _projectSource is null ? null : _catalog, trust: _projectTrust, view: (label, tabs, _) => { _viewed.Add(label); _viewedContent.Add(tabs[^1].Content()); _viewedTabs.Add(tabs); _onView?.Invoke(label); return Task.CompletedTask; },
            bin: _bin is null ? null : () => _bin, session: _session);
        pane.Show();
        return (menu, pane, settings);
    }

    /// <summary>The menu over a pane with geometry, a scripted source and the session's history (2026-10-12: the loaded sub-tab).</summary>
    private (SkillsMenu Menu, ScreenPane Pane, ScriptedInput Input) LiveMenu(ConversationHistory session)
    {
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null), _time) { Hint = () => "idle" };
        var input = new ScriptedInput();
        var keys = new KeySource(input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var menu = new SkillsMenu(Facts, _settings, Settings(pane, keys, menuPane), new TranscriptRenderer(pane), menuPane, new InputLine(pane, keys), OpenFile, session: session);
        pane.Show();
        return (menu, pane, input);
    }

    private static int s_loads;

    /// <summary>A turn of <paramref name="session"/> that loads <paramref name="skill"/>, as the turn loop writes it.</summary>
    private static void Load(ConversationHistory session, string skill)
    {
        string id = "load-" + Interlocked.Increment(ref s_loads).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var call = new Microsoft.Extensions.AI.FunctionCallContent(id, LoadSkillTool.ToolName, new Dictionary<string, object?> { ["name"] = skill });
        session.AddUser("use " + skill);
        session.AddMessage(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, [call]));
        session.AddToolResults([Assistant.ResultContent(call, SkillText.Content(skill, "Do it.", Path.Combine(Path.GetTempPath(), skill), [], more: false, truncated: false))]);
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

    /// <summary>The Offered tab's scopes (2026-10-10, the user's ask) as the pane prints the second strip, badges cut as <see cref="Output"/> cuts them.</summary>
    private static readonly string Scopes = Markup.Remove(InfoPane.SubStripRows(SkillsText.Label, ["all", "profile", "global"], 0, 100)[0]);

    private const string Strip = SkillsText.Label + " │ Offered · Reflection · Options ";   // Options last since 2026-09-22 (the user's ask); the Project tab before it went on 2026-10-01, its toggle an Options row; Loaded until 2026-09-19; Options (the settings rows, /settings' Skills tab until then) since later that day; Reflection (the reflection's rows out of Options) later still; the Roots tab after Project until later still that day

    /// <summary>A heading as the pane prints it at the fixture's 100 columns (2026-10-10, the user's ask: the /settings look).</summary>
    private static string Head(string title) => MenuLayout.Heading(title, 100);

    /// <summary>The Offered tab's catalog heading over <paramref name="count"/> skills (2026-10-10).</summary>
    private static string SkillsHead(int count) => Head(SkillsText.CatalogTitle + " · " + count);

    /// <summary>The shadowed skills' heading (2026-10-10; <c>Shadowed (a higher root holds the name):</c> until then), after the gap row.</summary>
    private static string ShadowedHead(int count) => "  \n" + Head(SkillsText.ShadowedTitle + " · " + count + " ── " + SkillsText.ShadowedNote);

    /// <summary>The Options tab's six rows at their defaults, padded to the tab's own column (38: the external-skills label), as the pane prints them; Project file third since 2026-10-01, Global skills second since 2026-10-13. Pinned.</summary>
    private static readonly string OptionsRows = Head("Sources") + "\n▸ Agent skills                      on\n  Global skills                     on\n  " + SettingsMenu.ExternalSkillsName + "  off\n  Project skills                    on\n  Project file                      on\n" + Head("Prompt and mentions") + "\n  Skill compact mode                protected\n  #-mention enabled                 on\n";   // under its two headings since 2026-10-10   // Allow skill delete, the fifth, went on 2026-09-23 (delete always offered)

    /// <summary>The Reflection tab's rows at their defaults (the confirm mode since 2026-10-12) (later on 2026-09-19), padded to its own column (33: the downloaded skills label since 2026-10-12). Pinned.</summary>
    private static readonly string ReflectionRows = Head("Learning") + "\n▸ Reflection (auto-learn)            on\n  Reflection reasoning               none\n" + Head("What it reads") + "\n  Reflection window                  3 turns\n  Reflection min tool calls          4 tool calls\n  Reflection max requests            4 requests\n  Reflection include sessions        on\n" + Head("Pace") + "\n  Reflection cooldown (min)          5 minutes\n  Reflection cooldown mode           last-written-skill\n  Reflection yield to turns          on\n" + Head("What it may change") + "\n  Reflection mode                    create-update\n  Reflection confirm mode            new\n  Reflection edit supporting files   off\n  Reflection edit downloaded skills  allow-and-mark\n";   // under its four headings since 2026-10-10, the sessions switch under What it reads

    /// <summary>A row as the menu prints it at width 100: whole when it fits, else cut to 99 cells and an ellipsis (FittedMarkup; the temp roots are long).</summary>
    private static string Fitted(string row) => row.Length <= 100 ? row : row[..99] + "…";

    [Fact]
    public void Strings_ArePinned()
    {
        Assert.Equal("Enter = choose · Esc = back", SkillsMenu.ScopeKeys);
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
        // The move to another profile (2026-10-13, the user's ask).
        Assert.Equal("move", SkillsMenu.MoveWord);
        Assert.Equal("move     [#9A8BB8]send it to another profile's skills[/]", SkillsMenu.MoveRow);
        Assert.Equal(SkillsText.Label + " › haiku › move", SkillsMenu.MoveToTitle("haiku"));
        Assert.Equal(@"work   [#9A8BB8]C:\h\profiles\work\skills[/]", SkillsMenu.ProfileRow("work", @"C:\h\profiles\work\skills", 5));
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), "profiles", "work", "skills"), SkillsMenu.ProfileSkillsRoot(_dir, "work"));
        Assert.Equal("Move skill 'haiku' from this profile to profile 'work'?", SkillsMenu.MoveToPrompt("haiku", "work"));
        Assert.Equal("(🎓 moved: haiku → profile work)", SkillsMenu.MovedToNotice("haiku", "work"));
        Assert.Equal("Could not move skill 'pdf-processing': the skills of profile 'work' already hold 'pdf'", SkillsMenu.ProfileExistsError("pdf-processing", "pdf", "work"));
        Assert.Equal("(🎓 no other profile to move it to; /profile add makes one)", SkillsMenu.NoOtherProfileNotice);
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

    /// <summary>
    /// A skill's page (2026-11-23, the user's ask): a bare rule between its place rows and its acts. The rule is no stop: Down from the last
    /// place row lands on move, never on a global move.
    /// </summary>
    [Fact]
    public async Task ASkillsPage_ARuleUnderThePlaceRows_NoStop()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);                       // the scope page, on profile
        Push(Keys.Down, Keys.Down, Keys.Enter); // global, then past the rule to move
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var lines = Output.Split('\n');
        int global = Array.FindLastIndex(lines, l => l.StartsWith("  global   ", StringComparison.Ordinal));
        Assert.True(global >= 0, Output);
        Assert.Equal(Rule(100), lines[global + 1]);
        Assert.Contains(Markup.Remove(SkillsMenu.MoveRow), lines[global + 2]);
        Assert.DoesNotContain(SkillsMenu.MovePrompt("haiku", SkillScope.Profile, SkillScope.Global), Output);
    }

    /// <summary>The revert row (2026-10-04): always on the page with the seams, before delete; nothing kept is a notice on the status line, no list.</summary>
    [Fact]
    public async Task RevertRow_IsAlwaysOffered_AndWithNothingKept_SaysSo()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        _versions = _ => [];
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);                                            // the scope page
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // revert
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n  edit     open its SKILL.md in your editor\n  revert   pick an earlier version to put back\n  lock     keep the model, reflection and updates from changing it\n  disable  stop offering it; a reflection still sees it, and changes nothing\n  delete   remove the folder and everything in it\n", Output);
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
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // revert: the list
        Push(Keys.Enter, Keys.Enter);                                // the cursor's: the model's; No (asked since later on 2026-10-07): the list again
        Push(Keys.Enter, Keys.Down, Keys.Enter);                     // the model's again, Yes
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        // No notes.md on disk: the version from before it was there is the file's now too.
        // Numbered as the Offered tab counts (2026-10-07): two kept texts, so the skill is at v3; the revision from before notes.md was there is none.
        Assert.Contains("\n" + Titled(SkillsMenu.VersionsTitle("haiku")) + "\n" + SkillRecordText.VersionsCaptionAt(3) + "\n \n" +
            "  v2  SKILL.md your edit of 2026-10-04 14:05 · current\n▸ v1  SKILL.md before the model's change at 2026-10-04 14:05\n      notes.md not there before a reflection's change at 2026-10-04 14:05 · current\n", Output);
        Assert.Equal(2, Assert.Single(_restored).Id);   // once: the No put nothing back
        Assert.Contains(SkillRecordText.PutBackQuestion("haiku", "v1"), Output);
        Assert.Contains(SkillRecordText.PutBackCaption(kept[1], TimeZoneInfo.Utc), Output);
        Assert.Contains("  · " + ChatScreen.KeptNotice + "\n", Output);
        Assert.Equal("↩️ Put v1 of haiku back?", SkillRecordText.PutBackQuestion("haiku", "v1"));
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
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // revert: the list, the cursor on v2 (the newest not current)
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
        Assert.Equal("Enter = put back · d = remove · c = clear all · Esc = back", SkillsMenu.VersionsKeys);
    }

    /// <summary>
    /// Delete on the revert list is its d (2026-10-16, the user's ask): the highlighted version removed after a yes, No keeping it, and it
    /// never puts a version back (Enter's put back is never asked).
    /// </summary>
    [Fact]
    public async Task RevertList_Delete_RemovesTheHighlightedVersion_NeverRestores()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var kept = ForgettableVersions(File.ReadAllText(Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName)));
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // revert: the list, the cursor on v2 (the newest not current)
        Push(Keys.Delete, Keys.Enter);                               // No: kept
        Push(Keys.Delete, Keys.Down, Keys.Enter);                    // Yes
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillRecordText.RemoveVersionQuestion("haiku", "v2"), Output);
        Assert.Contains(ChatScreen.KeptNotice, Output);
        Assert.Contains(SkillRecordText.VersionRemovedNotice("haiku", "v2"), Output);
        Assert.DoesNotContain(SkillRecordText.PutBackQuestion("haiku", "v2"), Output);
        Assert.Equal([3L, 1L], kept.Select(r => r.Id));
        Assert.Empty(_restored);
    }

    /// <summary>The revert list's c (2026-10-07): every kept version removed after a yes; the list goes, with no "nothing kept" notice after it.</summary>
    [Fact]
    public async Task RevertList_C_ClearsEveryVersion_AfterAYes()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var kept = ForgettableVersions(File.ReadAllText(Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName)));
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
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

    /// <summary>
    /// The project sub-tab (2026-11-23, feature idea #16): a repo whose only skills aren't trusted still gets the strip and the tab; Enter on the
    /// folder's trust row asks, a View shows the skill's SKILL.md, and Trust saves it, after which the skill is offered.
    /// </summary>
    [Fact]
    public async Task TheProjectSubTab_AnUntrustedFolder_EnterAsks_ViewShows_TrustSaves()
    {
        string repo = UseRepo();
        PutProject(repo, "deploy", "Ships [it].");
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right);   // profile, global, project (no external skills, so no external tab)
        Push(Keys.Enter);                           // the folder's trust row: the question, on the skill's View row
        Push(Keys.Enter);                           // View deploy
        Push(Keys.Up, Keys.Up, Keys.Enter);         // Trust (over Cancel; the rule is no stop)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.TrustTitle, Output);
        Assert.Equal([SkillsText.ViewLabel("deploy")], _viewed);
        Assert.Contains(SkillsText.TrustedNotice(SkillsText.ThisFolderLabel, 1), Output);
        Assert.NotNull(_projectTrust!.Trusted(repo));
        _catalog.Scan(new SkillScan(_external, _projectSource));
        Assert.Equal(SkillScope.Project, _catalog.Find("deploy")!.Scope);
        Assert.Equal("Ships [it].", _catalog.Find("deploy")!.Description);
    }

    /// <summary>
    /// The trust question's actions come first (2026-11-23, hand check 1): trust, cancel, a bare rule, the views, then the removed skills.
    /// The cursor starts on the first view, so Enter views; cancel trusts nothing.
    /// </summary>
    [Fact]
    public async Task TheTrustQuestion_TrustAndCancelFirst_ThenARule_ThenTheViews()
    {
        string repo = UseRepo();
        PutProject(repo, "deploy", "Ships it.");
        PutProject(repo, "lint", "Lints.");
        string old = PutProject(repo, "zold", "Old.");
        TrustRepo(repo);
        Directory.Delete(old, recursive: true);
        File.AppendAllText(Path.Combine(repo, ".agents", "skills", "deploy", SkillCatalog.FileName), "\nMore.\n");
        _console.Profile.Width = 200;
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Enter);   // the trust row: the question
        Push(Keys.Enter);                                       // the first view, under the cursor
        Push(Keys.Up, Keys.Enter);                              // cancel, above it (the rule is no stop)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([SkillsText.ViewLabel("deploy")], _viewed);
        Assert.True(_projectTrust!.Trusted(repo)!.Skills.ContainsKey(".agents/skills/zold"));   // cancel saved nothing: the old trust stands
        var lines = Output.Split('\n');
        int trust = Array.FindIndex(lines, l => l.Contains(Markup.Remove(SkillsText.TrustQuestionRow(SkillsText.ThisFolderLabel)), StringComparison.Ordinal));
        int cancel = Array.FindIndex(lines, l => l.Contains(Markup.Remove(SkillsText.CancelRow), StringComparison.Ordinal));
        int first = Array.FindIndex(lines, l => l.Contains("view", StringComparison.Ordinal) && l.Contains("deploy", StringComparison.Ordinal));
        Assert.True(trust >= 0 && cancel == trust + 1, Output);
        Assert.Equal(cancel + 2, first);
        Assert.Contains(new string(ScreenPane.RuleGlyph, 40), lines[cancel + 1]);
    }

    /// <summary>
    /// On a narrow pane (2026-11-23, hand check 1) a long description is what gets cut: the acting mark and the files count still show.
    /// </summary>
    [Fact]
    public async Task TheTrustQuestion_Narrow_ShowsTheActingMarkAndTheFilesCount_TheDescriptionCut()
    {
        string repo = UseRepo();
        string tools = PutProject(repo, "tools", "A long description that runs on and on well past what an eighty column pane can hold in one row.");
        File.WriteAllText(Path.Combine(tools, SkillCatalog.FileName), "---\nname: tools\ndescription: A long description that runs on and on well past what an eighty column pane can hold in one row.\nallowed-tools: Bash\n---\n\nSteps.\n");
        string notes = PutProject(repo, "notes", "Another long description that runs on and on well past what an eighty column pane can hold.");
        Directory.CreateDirectory(Path.Combine(notes, "templates"));
        File.WriteAllText(Path.Combine(notes, "templates", "notes.md"), "x");
        _console.Profile.Width = 80;
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Enter);   // the trust row: the question
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.ActingMark[..20], Output);
        Assert.Contains(SkillsText.FilesNote(1, false), Output);
    }

    /// <summary>A row with neither note starts its tail with the description (2026-11-23).</summary>
    [Fact]
    public void TrustViewRow_NotesFirst_ThenTheDescription()
    {
        string plain = Markup.Remove(SkillsText.TrustViewRow("a", 4, "", "Desc.", "", acting: false));
        Assert.EndsWith("Desc.", plain.TrimEnd());
        string both = Markup.Remove(SkillsText.TrustViewRow("a", 4, "", "Desc.", SkillsText.FilesNote(1, false), acting: true));
        Assert.EndsWith(SkillsText.FilesNote(1, false) + " · " + SkillsText.ActingMark + " · Desc.", both.TrimEnd());
    }

    /// <summary>
    /// A skill whose bundled files alone changed (2026-11-23, hand check 1): its view says SKILL.md is the one trusted, over the text, with no diff.
    /// A changed skill's view doesn't carry the note.
    /// </summary>
    [Fact]
    public async Task TheTrustQuestion_AFilesChangedSkill_ItsViewSaysSo()
    {
        string repo = UseRepo();
        string deploy = PutProject(repo, "deploy", "Ships it.");
        File.WriteAllText(Path.Combine(deploy, "helper.md"), "one");
        string lint = PutProject(repo, "lint", "Lints.");
        TrustRepo(repo);
        File.WriteAllText(Path.Combine(deploy, "helper.md"), "two");
        File.AppendAllText(Path.Combine(lint, SkillCatalog.FileName), "\nMore.\n");
        _console.Profile.Width = 200;
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Enter);   // the trust row: the question
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // view deploy, then lint
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([SkillsText.ViewLabel("deploy"), SkillsText.ViewLabel("lint")], _viewed);
        string Shown(int i)
        {
            var console = new TestConsole();
            console.Profile.Width = 200;
            console.Write(_viewedContent[i]);
            return console.Output;
        }

        Assert.Contains(SkillsText.FilesChangedViewNote, Shown(0));
        Assert.Contains("The repo's steps.", Shown(0));
        Assert.DoesNotContain(SkillsText.FilesChangedViewNote, Shown(1));

        // Rendered first, the exact text on Source (2026-11-24, the user's ask); a changed SKILL.md is its diff, one tab.
        Assert.Equal([SkillsText.RenderedTab, SkillsText.SourceTab], _viewedTabs[0].Select(t => t.Title));
        Assert.Single(_viewedTabs[1]);
        var rendered = new TestConsole();
        rendered.Profile.Width = 200;
        rendered.Write(_viewedTabs[0][0].Content());
        Assert.Contains(SkillsText.FilesChangedViewNote, rendered.Output);
        Assert.Contains("Ships it.", rendered.Output);
        Assert.DoesNotContain("---", rendered.Output.Split('\n')[0]);
    }

    /// <summary>The trust question's Cancel, and Esc, save nothing (2026-11-23).</summary>
    [Fact]
    public async Task TheTrustQuestion_CancelAndEsc_SaveNothing()
    {
        string repo = UseRepo();
        PutProject(repo, "deploy", "Ships it.");
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Enter, Keys.Up, Keys.Enter);     // Cancel, above the first view
        Push(Keys.Enter, Keys.Escape);                                                  // asked again, Esc
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_projectTrust!.Trusted(repo));
        Assert.DoesNotContain(SkillsText.TrustedNotice(SkillsText.ThisFolderLabel, 1), Output);
        Assert.False(File.Exists(_projectTrust.FilePath));
    }

    /// <summary>Under a running reply a trust is refused (2026-11-23): it could race the model's load_skill.</summary>
    [Fact]
    public async Task TheProjectSubTab_UnderARunningReply_RefusesTheTrust()
    {
        string repo = UseRepo();
        PutProject(repo, "deploy", "Ships it.");
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None, midTurn: true);

        Assert.Contains(SettingsMenu.NotWhileReplyRunsNotice, Output);
        Assert.DoesNotContain(SkillsText.TrustTitle, Output);
        Assert.Null(_projectTrust!.Trusted(repo));
    }

    // ── A skill moved into the working folder, and a project skill's delete (2026-11-23, the user's asks) ──

    /// <summary>The scope page's project row: down twice from profile (profile, global, project), then Yes.</summary>
    private void PushMoveToProject() => Push(Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);

    /// <summary>The project sub-tab's first skill's page, its delete row (edit, copy to profile, copy to global, disable, delete), then Yes or No.</summary>
    private void PushDeleteFirstProjectSkill(bool yes = true)
    {
        // Untrust first under the heading (2026-11-23), so Down to the first skill, then its page's delete.
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Down, Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
        Push(yes ? [Keys.Down, Keys.Enter] : [Keys.Enter]);
    }

    /// <summary>The menu at 200 columns, so a delete's question and notice sit on one line each.</summary>
    private SkillsMenu DeleteMenu()
    {
        _console.Profile.Width = 200;
        return PaneMenu().Menu;
    }

    private string MovedRow(string repo) => Fitted("  " + Markup.Remove(SkillsText.ProjectMoveRow(repo)));

    /// <summary>
    /// A profile skill moved into a working folder with no skills (2026-11-23): the row names its .agents\skills, the question says what it becomes; Yes →
    /// the folder there without the app's markers, gone from the profile, the folder trusted and the skill offered, the notice saying so.
    /// </summary>
    [Fact]
    public async Task MoveToProject_IntoAFolderWithNoSkills_MovesIt_TrustsIt_ItIsOffered()
    {
        string repo = UseRepo();
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        File.WriteAllText(Path.Combine(_roots.Profile, "haiku", SkillProvenance.FileName), "{}");
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);
        PushMoveToProject();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string moved = Path.Combine(repo, ".agents", "skills", "haiku");
        Assert.Contains(MovedRow(repo), Output);
        Assert.Contains(SkillsText.MoveToProjectPrompt("haiku", SkillsText.ThisFolderLabel)[..60], Output);
        Assert.True(File.Exists(Path.Combine(moved, SkillCatalog.FileName)));
        Assert.False(File.Exists(Path.Combine(moved, SkillProvenance.FileName)));
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "haiku")));
        Assert.NotNull(_projectTrust!.Trusted(repo));
        Assert.Contains(SkillsText.MovedToProjectNotice("haiku", SkillsText.ThisFolderLabel, ProjectMoveState.Offered), Output);
        _catalog.Scan(new SkillScan(_external, _projectSource));
        Assert.Equal(SkillScope.Project, _catalog.Find("haiku")!.Scope);
    }

    /// <summary>A global skill moved into a trusted folder nothing else changed in (2026-11-23): the trust is kept, both skills offered.</summary>
    [Fact]
    public async Task MoveToProject_AGlobalSkill_IntoATrustedUnchangedFolder_KeepsItTrusted()
    {
        string repo = UseRepo();
        PutProject(repo, "deploy", "Ships it.");
        TrustRepo(repo);
        Put(SkillScope.Global, "pdf", "Reads PDFs.");
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Enter);      // the global sub-tab's pdf, its page on global
        Push(Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);   // project, Yes
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([".agents/skills/deploy", ".agents/skills/pdf"], _projectTrust!.Trusted(repo)!.Skills.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(SkillsText.MovedToProjectNotice("pdf", SkillsText.ThisFolderLabel, ProjectMoveState.Offered), Output);
        Assert.False(Exists(SkillScope.Global, "pdf"));
    }

    /// <summary>A folder holding a skill not trusted yet (2026-11-23): the move goes ahead, nothing is trusted, and the notice sends you to review it.</summary>
    [Fact]
    public async Task MoveToProject_IntoAnUntrustedFolder_TrustsNothing()
    {
        string repo = UseRepo();
        PutProject(repo, "deploy", "Ships it.");
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Enter);   // the profile sub-tab's haiku
        PushMoveToProject();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_projectTrust!.Trusted(repo));
        Assert.Contains(SkillsText.MovedToProjectNotice("haiku", SkillsText.ThisFolderLabel, ProjectMoveState.Waiting), Output);
        Assert.True(File.Exists(Path.Combine(repo, ".agents", "skills", "haiku", SkillCatalog.FileName)));
    }

    /// <summary>A disabled skill arrives disabled (2026-11-23): its name in the folder's disabled list, since the marker isn't copied.</summary>
    [Fact]
    public async Task MoveToProject_ADisabledSkill_StaysDisabled()
    {
        string repo = UseRepo();
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        SkillDisable.Set(Path.Combine(_roots.Profile, "haiku"), true, _time.GetUtcNow());
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);
        PushMoveToProject();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("haiku", _projectTrust!.DisabledIn(repo));
        Assert.False(File.Exists(Path.Combine(repo, ".agents", "skills", "haiku", SkillDisable.FileName)));
        Assert.Contains(SkillsText.MovedToProjectNotice("haiku", SkillsText.ThisFolderLabel, ProjectMoveState.Disabled), Output);
    }

    /// <summary>Past the cap (2026-11-23): fifty skills first, the moved one the fifty-first, trusted but not offered, and the notice says so.</summary>
    [Fact]
    public async Task MoveToProject_PastTheCap_SaysSo()
    {
        string repo = UseRepo();
        for (int i = 0; i < ProjectSkillTrust.ProjectSkillCap; i++)
        {
            PutProject(repo, "a" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), "One of many.");
        }

        TrustRepo(repo);
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Enter);
        PushMoveToProject();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.MovedToProjectNotice("haiku", SkillsText.ThisFolderLabel, ProjectMoveState.OverCap), Output);
    }

    /// <summary>A global twin of the moved skill (2026-11-23): yours win a clash, so the repo's is trusted but hidden, and the shadow notice names it.</summary>
    [Fact]
    public async Task MoveToProject_AGlobalTwin_HidesIt_TheShadowNoticeNamesIt()
    {
        UseRepo();
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        Put(SkillScope.Global, "haiku", "The global one.");
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Enter);
        PushMoveToProject();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.ProjectShadowedNotice(SkillsText.ThisFolderLabel, ["haiku"]), Output);
    }

    /// <summary>Refused ahead of the question (2026-11-23): a twin by the folder's name in .agents\skills, or by the skill's name in .claude\skills.</summary>
    [Fact]
    public async Task MoveToProject_ATwinAtTheLevel_IsRefused_NothingMoves()
    {
        string repo = UseRepo();
        PutProject(repo, "haiku", "The repo's.");
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        Put(SkillScope.Profile, "poem", "Writes poems.");
        string other = Path.Combine(repo, ".claude", "skills", "verse");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, SkillCatalog.FileName), "---\nname: poem\ndescription: The repo's poem.\n---\n\nSteps.\n");
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Enter);           // haiku
        Push(Keys.Down, Keys.Down, Keys.Enter); // refused: the list again
        Push(Keys.Down, Keys.Enter);            // poem
        Push(Keys.Down, Keys.Down, Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.ProjectExistsError("haiku", "haiku", SkillsText.ThisFolderLabel, byName: false), Output);
        Assert.Contains(SkillsText.ProjectExistsError("poem", "poem", SkillsText.ThisFolderLabel, byName: true), Output);
        Assert.True(Exists(SkillScope.Profile, "haiku") && Exists(SkillScope.Profile, "poem"));
    }

    /// <summary>A locked skill is refused at the row (2026-11-23), as the other moves are.</summary>
    [Fact]
    public async Task MoveToProject_ALockedSkill_IsRefused()
    {
        UseRepo();
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        SkillLock.Set(Path.Combine(_roots.Profile, "haiku"), true, _time.GetUtcNow());
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Down, Keys.Enter);   // refused: the list again
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillRecordText.LockedNotice("haiku"), Output);
        Assert.True(Exists(SkillScope.Profile, "haiku"));
    }

    /// <summary>No level to move to (2026-11-23: a home, project skills off, the guard): no project row; an external skill never reaches the page.</summary>
    [Fact]
    public async Task MoveToProject_NoTarget_NoRow()
    {
        string repo = UseRepo();
        _projectTarget = false;
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, _) = PaneMenu();
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsMenu.ScopeTitle("haiku"), Output);
        Assert.Contains("  move     send it to another profile's skills", Output);
        Assert.DoesNotContain(MovedRow(repo), Output);
    }

    /// <summary>
    /// A project skill's delete (2026-11-23, the user's ask): the row and the question say the Recycle bin; Yes → the folder in the bin, the folder's
    /// other skill still offered and the trust kept. No keeps it.
    /// </summary>
    [Fact]
    public async Task ProjectDelete_ToTheBin_KeepsTheFoldersTrust_NoKeepsIt()
    {
        string repo = UseRepo();
        string deploy = PutProject(repo, "deploy", "Ships it.");
        PutProject(repo, "lint", "Lints.");
        TrustRepo(repo);
        _bin = new FakeRecycleBin();
        var menu = DeleteMenu();
        PushDeleteFirstProjectSkill(yes: false);
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);   // the list again: the page, delete, Yes
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsMenu.KeptNotice, Output);
        Assert.Contains("  " + Markup.Remove(SkillsText.ProjectDeleteRow(binned: true)), Output);
        Assert.Contains(SkillsText.ProjectPageCaption(SkillsText.ThisFolderLabel), Output);
        Assert.Contains(SkillsText.ProjectDeletePrompt("deploy", SkillsText.ThisFolderLabel, claude: false, binned: true, why: null)[..60], Output);
        Assert.Equal([deploy], _bin.Moved);
        Assert.False(Directory.Exists(deploy));
        Assert.Equal([".agents/skills/lint"], _projectTrust!.Trusted(repo)!.Skills.Keys);
        Assert.Contains(SkillsText.ProjectDeletedNotice("deploy", SkillsText.ThisFolderLabel, DeleteWay.Recycled, null, link: false, trusted: true, loaded: false), Output);
        _catalog.Scan(new SkillScan(_external, _projectSource));
        Assert.Equal(SkillScope.Project, _catalog.Find("lint")!.Scope);
    }

    /// <summary>With no bin in use (the setting off) the row and question say for good (2026-11-23); a disabled skill's name leaves the folder's list.</summary>
    [Fact]
    public async Task ProjectDelete_NoBin_ForGood_TheDisabledNameGoes()
    {
        string repo = UseRepo();
        string deploy = PutProject(repo, "deploy", "Ships it.");
        TrustRepo(repo);
        _projectTrust!.SetDisabled(repo, "deploy", true);
        var menu = DeleteMenu();
        PushDeleteFirstProjectSkill();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  " + Markup.Remove(SkillsText.ProjectDeleteRow(binned: false)), Output);
        Assert.Contains("This can't be undone.", Output);
        Assert.False(Directory.Exists(deploy));
        Assert.Empty(_projectTrust.DisabledIn(repo));
        Assert.NotNull(_projectTrust.Trusted(repo));
        Assert.Contains(SkillsText.ProjectDeletedNotice("deploy", SkillsText.ThisFolderLabel, DeleteWay.Deleted, null, link: false, trusted: true, loaded: false), Output);
    }

    /// <summary>A change that slips in before the delete (2026-11-23, here while the bin is probed): the folder isn't trusted again; it waits.</summary>
    [Fact]
    public async Task ProjectDelete_AnotherChange_TrustsNothing()
    {
        string repo = UseRepo();
        PutProject(repo, "deploy", "Ships it.");
        string lint = PutProject(repo, "lint", "Lints.");
        TrustRepo(repo);
        _bin = new FakeRecycleBin { OnProbe = _ => File.AppendAllText(Path.Combine(lint, SkillCatalog.FileName), "A stranger's line.\n") };
        var menu = DeleteMenu();
        PushDeleteFirstProjectSkill();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(".agents/skills/deploy", _projectTrust!.Trusted(repo)!.Skills.Keys);   // the old trust, not saved again
        Assert.Contains(SkillsText.ProjectDeletedNotice("deploy", SkillsText.ThisFolderLabel, DeleteWay.Recycled, null, link: false, trusted: false, loaded: false), Output);
    }

    /// <summary>A .claude skill deletes from .claude\skills (2026-11-23), and its question names that folder.</summary>
    [Fact]
    public async Task ProjectDelete_AClaudeSkill_FromItsOwnFolder()
    {
        string repo = UseRepo();
        string deploy = PutProject(repo, "deploy", "Ships it.", claude: true);
        TrustRepo(repo);
        _bin = new FakeRecycleBin();
        var menu = DeleteMenu();
        PushDeleteFirstProjectSkill();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.ProjectDeletePrompt("deploy", SkillsText.ThisFolderLabel, claude: true, binned: true, why: null)[..55], Output);
        Assert.Equal([deploy], _bin.Moved);
        Assert.False(Directory.Exists(deploy));
    }

    /// <summary>A bin that refuses (2026-11-23): the error, the folder still there, the trust as it was.</summary>
    [Fact]
    public async Task ProjectDelete_TheBinRefuses_NothingChanges()
    {
        string repo = UseRepo();
        string deploy = PutProject(repo, "deploy", "Ships it.");
        TrustRepo(repo);
        string fingerprint = _projectTrust!.Trusted(repo)!.Fingerprint;
        _bin = new FakeRecycleBin { Answer = new RecycleResult(RecycleOutcome.Failed, "it is busy") };
        var menu = DeleteMenu();
        PushDeleteFirstProjectSkill();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.ProjectDeleteFailedError("deploy", "it is busy"), Output);
        Assert.True(Directory.Exists(deploy));
        Assert.Equal(fingerprint, _projectTrust.Trusted(repo)!.Fingerprint);
    }

    /// <summary>
    /// The bin's probe refuses (2026-11-23): no bin on the drive, or a reason of its own; the question says for good with that reason, and Yes deletes
    /// for good, nothing handed to the bin, the notice giving the same reason.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProjectDelete_TheProbeRefuses_AsksForGood_WithItsReason(bool noBin)
    {
        string repo = UseRepo();
        string deploy = PutProject(repo, "deploy", "Ships it.");
        TrustRepo(repo);
        string why = noBin ? NeonSidekick.Files.FileText.RecycleNoBinHere : "paths too long";
        _bin = new FakeRecycleBin { ProbeAnswer = noBin ? new RecycleResult(RecycleOutcome.NoBin) : new RecycleResult(RecycleOutcome.Failed, why) };
        var menu = DeleteMenu();
        PushDeleteFirstProjectSkill();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("This can't be undone (" + why + ").", Output);
        Assert.Empty(_bin.Moved);
        Assert.False(Directory.Exists(deploy));
        Assert.Contains(SkillsText.ProjectDeletedNotice("deploy", SkillsText.ThisFolderLabel, DeleteWay.DeletedNoBin, why, link: false, trusted: true, loaded: false), Output);
    }

    /// <summary>A drive with no bin found only at the move (2026-11-23): nothing went; asked again for good, and Yes deletes.</summary>
    [Fact]
    public async Task ProjectDelete_ALateNoBin_AsksAgain()
    {
        string repo = UseRepo();
        string deploy = PutProject(repo, "deploy", "Ships it.");
        TrustRepo(repo);
        _bin = new FakeRecycleBin { Answer = new RecycleResult(RecycleOutcome.NoBin) };
        var menu = DeleteMenu();
        PushDeleteFirstProjectSkill();
        Push(Keys.Down, Keys.Enter);   // the second question: Yes
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([deploy], _bin.Moved);
        Assert.Contains("This can't be undone (" + NeonSidekick.Files.FileText.RecycleNoBinHere + ").", Output);
        Assert.False(Directory.Exists(deploy));
        Assert.Contains(SkillsText.ProjectDeletedNotice("deploy", SkillsText.ThisFolderLabel, DeleteWay.DeletedNoBin, NeonSidekick.Files.FileText.RecycleNoBinHere, link: false, trusted: true, loaded: false), Output);
    }

    /// <summary>A linked project skill (2026-11-23): only the link goes, never to the bin, and what it points at stays.</summary>
    [Fact]
    public async Task ProjectDelete_ALinkedSkill_LosesOnlyTheLink()
    {
        string repo = UseRepo();
        string target = Path.Combine(repo, "shared", "linked");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, SkillCatalog.FileName), "---\nname: linked\ndescription: Shared.\n---\n\nSteps.\n");
        string link = Path.Combine(repo, ".agents", "skills", "linked");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (OperatingSystem.IsWindows())
        {
            Junction.Make(link, target);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        TrustRepo(repo);
        _bin = new FakeRecycleBin();
        var menu = DeleteMenu();
        PushDeleteFirstProjectSkill();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.ProjectDeletePrompt("linked", SkillsText.ThisFolderLabel, claude: false, binned: false, why: null, link: true)[..40], Output);
        Assert.Empty(_bin.Moved);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, SkillCatalog.FileName)));
        Assert.Contains(SkillsText.ProjectDeletedNotice("linked", SkillsText.ThisFolderLabel, DeleteWay.Deleted, null, link: true, trusted: true, loaded: false), Output);
    }

    /// <summary>The pane: the three tabs under the strip, the Offered rows first; Enter or Space on a heading does nothing (the page re-shown); the Project file toggle is an Options row since 2026-10-01 (the one row of a Project tab of its own from later on 2026-09-19 until then) — Enter opens its page, off picked, the status line saying so; ESC closes with nothing in the transcript.</summary>
    [Fact]
    public async Task OnThePane_TheThreeTabs_EnterOnAHeadingDoesNothing_TheProjectFileRowFlips()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        Put(SkillScope.Global, "haiku", "The global one.");
        var (menu, pane) = PaneMenu();
        int flow = pane.FlowRow;
        Push(Keys.Left, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Char(' '));   // Options (the strip wrapped), Project file (third since Global skills, 2026-10-13; fourth since Project skills, 2026-11-23): Space flips it off (2026-10-04; nothing until then)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        AssertPadded("\n" + Titled(Strip) + "\n" + Scopes + "\n \n" + SkillsHead(1) + "\n▸ haiku  profile  Writes haiku.\n" + ShadowedHead(1) + "\n  haiku  global   shadowed by the profile skills\n", Rule(100) + "\n" + SkillsMenu.LoadedKeys + "\n");
        Assert.DoesNotContain("\n" + Titled(SkillsText.Label + " › Project file") + "\n", Output);   // no page: Space saved at once
        Assert.Contains("\n" + Titled(Strip) + "\n  · Project file: off\n" + Head("Sources") + "\n  Agent skills                      on\n  Global skills                     on\n  " + SettingsMenu.ExternalSkillsName + "  off\n  Project skills                    on\n▸ Project file                      off\n", Output);   // the flip on the status line, the row re-read, the cursor kept
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

    /// <summary>
    /// The loaded sub-tab (2026-10-12, the user's ask): last on the Offered strip when the host gives the session; with nothing loaded it
    /// stays and says so, and a load answered while the pane is open shows at once, with no key pressed — the page built again where it stood.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheLoadedSubTab_SaysNoneLoaded_ThenKeepsUpWithALoad()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var session = new ConversationHistory("s");
        var (menu, pane, input) = LiveMenu(session);
        input.Push(Keys.Right, Keys.Right, Keys.Right);   // all → profile → global → loaded
        int waits = 0;
        input.OnWait = () =>
        {
            waits++;
            if (waits == 1)
            {
                Load(session, "haiku");   // the reply's load_skill lands under the open pane
            }
            else if (waits == 2)
            {
                input.Push(Keys.Escape);
            }
        };

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(Markup.Remove(InfoPane.SubStripRows(SkillsText.Label, ["all", "profile", "global", SkillsText.LoadedSubTitle], 3, 100)[0]), Output);
        int none = Output.IndexOf(SkillsText.NoneLoadedLine, StringComparison.Ordinal);
        Assert.True(none >= 0, Output);
        int heading = Output.IndexOf(SkillsText.LoadedTitle + SectionRule.CountSeparator + "1", none, StringComparison.Ordinal);
        Assert.True(heading > none, Output);
        Assert.Contains("\n▸ haiku  profile  Writes haiku.\n", Output[heading..]);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>
    /// A pane opened under a reply (2026-10-12, the user's ask): a skill's page is refused while it runs; when it ends the pane shows again
    /// where it stood, saying so, and the same Enter opens the page — no closing and opening again.
    /// </summary>
    [Fact]
    public async Task OnThePane_UnderAReply_ThePickOpensWhenTheReplyEnds_InTheSamePane()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        using var reply = new CancellationTokenSource();
        var (menu, pane, input) = LiveMenu(new ConversationHistory("s"));
        input.Push(Keys.Down, Keys.Enter);   // the haiku row (under its heading): refused while the reply runs
        int waits = 0;
        input.OnWait = () =>
        {
            waits++;
            if (waits == 1)
            {
                reply.Cancel();   // the reply ends under the open pane
            }
            else if (waits == 2)
            {
                input.Push(Keys.Enter);    // the same row: its page now
            }
            else if (waits == 3)
            {
                input.Push(Keys.Escape, Keys.Escape);
            }
        };

        await menu.ShowAsync(CancellationToken.None, new ReplyRun(reply.Token));

        int refused = Output.IndexOf(SettingsMenu.NotWhileReplyRunsNotice, StringComparison.Ordinal);
        int ended = Output.IndexOf(SettingsMenu.ReplyEndedNotice, StringComparison.Ordinal);
        int page = Output.IndexOf(SkillsMenu.ScopeTitle("haiku"), StringComparison.Ordinal);
        Assert.True(refused >= 0 && ended > refused && page > ended, Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public void LoadedSkillRows_CountRepeats_NameTheOnesTheCatalogLost_AndFilter()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var facts = Facts();
        var rows = SkillsText.LoadedSkillRows(facts, [new("haiku", 2), new("gone", 1)]);
        Assert.True(rows[0].Heading);
        Assert.Equal("haiku", rows[1].Skill?.Name);
        Assert.Contains(SkillsText.LoadsNote(2), rows[1].Markup);
        Assert.Null(rows[2].Skill);
        Assert.Contains(SkillsText.NotInCatalogNote, rows[2].Markup);
        Assert.Equal([(Theme.DimMarkup(SkillsText.NoneLoadedLine), (Skill?)null, false)], SkillsText.LoadedSkillRows(facts, []));
        var filtered = SkillsText.LoadedSkillRows(facts, [new("haiku", 1)], "zzz");
        Assert.Equal(MenuFilter.NoMatchRow("zzz"), filtered[^1].Markup);
        Assert.Equal("×2", SkillsText.LoadsNote(2));
        Assert.Equal(["haiku  ×2", "gone"], SkillsText.LoadedSkillLines([new("haiku", 2), new("gone", 1)]));
        Assert.Equal([SkillsText.NoneLoadedLine], SkillsText.LoadedSkillLines([]));
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
        Assert.Contains(ScreenPane.Fit(MenuFilter.Hint(SkillsMenu.LoadedKeys, "pdf"), 99), Output);   // cut at 100 columns since the lock (2026-10-07)
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
        Push(Keys.Left, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);   // Options (the strip wrapped), Project file (third since 2026-10-13, fourth since 2026-11-23): off
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

        HeldRows.AssertContains("\n" + Titled(SkillsMenu.ScopeTitle("haiku")) + "\n \n" + Fitted("▸ profile  " + _roots.Profile) + "\n" + Fitted("  global   " + _roots.Global) + "\n" + Rule(100) + "\n  move     send it to another profile's skills\n  copy     copy it into another profile's skills\n  rename   give it a new name (letters, digits and hyphens)\n  edit     open its SKILL.md in your editor\n  lock     keep the model, reflection and updates from changing it\n  disable  stop offering it; a reflection still sees it, and changes nothing\n  delete   remove the folder and everything in it\n", Rule(100) + "\n" + SkillsMenu.ScopeKeys + "\n", Output);   // delete always offered since 2026-09-23
        HeldRows.AssertContains("\n" + Titled(SkillsMenu.MovePrompt("haiku", SkillScope.Profile, SkillScope.Global)) + "\n \n▸ No\n  Yes\n", Rule(100) + "\n" + SettingsMenu.ConfirmKeys + "\n", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n" + Scopes + "\n  · " + SkillsMenu.MovedNotice("haiku", SkillScope.Global) + "\n" + SkillsHead(1) + "\n▸ haiku  global   Writes haiku.\n", Output);
        Assert.False(Exists(SkillScope.Profile, "haiku"));
        Assert.True(Exists(SkillScope.Global, "haiku"));
        Assert.Equal(flow, pane.FlowRow);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>
    /// The move row (2026-10-13, the user's ask): a profile skill's page lists the other profiles under it, the loaded one left out; the one
    /// picked, Yes → the folder in that profile's skills, gone from this one, the list read again with the notice on its status line.
    /// </summary>
    [Fact]
    public async Task MoveToAnotherProfile_Confirmed_MovesTheFolderThere()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        Directory.CreateDirectory(Profiles.Directory(_dir, "work"));
        Directory.CreateDirectory(Profiles.Directory(_dir, "ada"));
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter);                            // the skill's page, the cursor on profile
        Push(Keys.Down, Keys.Down, Keys.Enter);      // move
        Push(Keys.Down, Keys.Enter);                 // work, after ada
        Push(Keys.Down, Keys.Enter);                 // Yes
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string work = SkillsMenu.ProfileSkillsRoot(_dir, "work");
        HeldRows.AssertContains("\n" + Titled(SkillsMenu.MoveToTitle("haiku")) + "\n \n" + Fitted("▸ ada   " + SkillsMenu.ProfileSkillsRoot(_dir, "ada")) + "\n" + Fitted("  work  " + work) + "\n", Rule(100) + "\n" + SkillsMenu.ScopeKeys + "\n", Output);
        Assert.Contains("\n" + Titled(SkillsMenu.MoveToPrompt("haiku", "work")) + "\n \n▸ No\n  Yes\n", Output);
        Assert.Contains("\n  · " + SkillsMenu.MovedToNotice("haiku", "work") + "\n", Output);
        Assert.True(File.Exists(Path.Combine(work, "haiku", SkillCatalog.FileName)));
        Assert.False(Exists(SkillScope.Profile, "haiku"));
        Assert.DoesNotContain("default ", Output);   // the loaded profile is not listed
        pane.Dispose();
    }

    /// <summary>The move row's refusals (2026-10-13): no other profile says so; a profile holding the folder already is the error line with no question; No keeps it.</summary>
    [Fact]
    public async Task MoveToAnotherProfile_NoneToPick_AFolderThereAlready_OrNo_Keeps()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);   // move: no other profile yet
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n  · " + SkillsMenu.NoOtherProfileNotice + "\n", Output);
        Assert.DoesNotContain(SkillsMenu.MoveToTitle("haiku"), Output);

        string work = SkillsMenu.ProfileSkillsRoot(_dir, "work");
        Directory.CreateDirectory(Path.Combine(work, "haiku"));
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Enter, Keys.Enter);   // move, work: taken
        Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.Contains("\n  ✗ " + SkillsMenu.ProfileExistsError("haiku", "haiku", "work") + "\n", Output);
        Assert.DoesNotContain(SkillsMenu.MoveToPrompt("haiku", "work"), Output);

        Directory.Delete(Path.Combine(work, "haiku"));
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Enter, Keys.Enter, Keys.Enter);   // move, work, No
        Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.Contains(SkillsMenu.MoveToPrompt("haiku", "work"), Output);
        Assert.Contains("\n  · " + SkillsMenu.KeptNotice + "\n", Output);
        Assert.True(Exists(SkillScope.Profile, "haiku"));
        Assert.False(Directory.Exists(Path.Combine(work, "haiku")));
        pane.Dispose();
    }

    /// <summary>The move row is a profile skill's alone (2026-10-13), and a locked one is refused on it before the list.</summary>
    [Fact]
    public async Task MoveToAnotherProfile_IsAProfileSkillsRow_AndALockedOneIsRefused()
    {
        Put(SkillScope.Global, "verse", "Writes verse.");
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        SkillLock.Set(Path.Combine(_roots.Profile, "haiku"), true, DateTimeOffset.UnixEpoch);
        Directory.CreateDirectory(Profiles.Directory(_dir, "work"));
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);   // haiku (the profile skill sorts first): move, locked
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n  · " + SkillRecordText.LockedNotice("haiku") + "\n", Output);
        Assert.DoesNotContain(SkillsMenu.MoveToTitle("haiku"), Output);
        Assert.True(Exists(SkillScope.Profile, "haiku"));

        Push(Keys.Down, Keys.Enter, Keys.Escape, Keys.Escape);   // verse's page: no move row
        await menu.ShowAsync(CancellationToken.None);
        string verse = Output[Output.LastIndexOf(Titled(SkillsMenu.ScopeTitle("verse")), StringComparison.Ordinal)..];
        Assert.Contains("\n  rename   ", verse);
        Assert.DoesNotContain("\n  move     ", verse);
        pane.Dispose();
    }

    /// <summary>
    /// The copy row (2026-10-25, the user's ask): right after move, the same profile list; Yes → the folder in that profile's skills and still
    /// in this one, its lock carried (a locked skill copies: a copy changes nothing here), the notice on the list's status line.
    /// </summary>
    [Fact]
    public async Task CopyToAnotherProfile_Confirmed_CopiesTheFolder_AndKeepsTheOriginal()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        SkillLock.Set(Path.Combine(_roots.Profile, "haiku"), true, DateTimeOffset.UnixEpoch);
        Directory.CreateDirectory(Profiles.Directory(_dir, "work"));
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter);                                       // the skill's page, the cursor on profile
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);      // copy (past global and move)
        Push(Keys.Enter);                                       // work
        Push(Keys.Down, Keys.Enter);                            // Yes
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string work = SkillsMenu.ProfileSkillsRoot(_dir, "work");
        HeldRows.AssertContains("\n" + Titled(SkillsMenu.CopyToTitle("haiku")) + "\n \n" + Fitted("▸ work  " + work) + "\n", Rule(100) + "\n" + SkillsMenu.ScopeKeys + "\n", Output);
        Assert.Contains("\n" + Titled(SkillsMenu.CopyToPrompt("haiku", "work")) + "\n \n▸ No\n  Yes\n", Output);
        Assert.Contains("\n  · " + SkillsMenu.CopiedToNotice("haiku", "work") + "\n", Output);
        Assert.True(File.Exists(Path.Combine(work, "haiku", SkillCatalog.FileName)));
        Assert.True(SkillLock.IsLocked(Path.Combine(work, "haiku")));
        Assert.True(Exists(SkillScope.Profile, "haiku"));
        pane.Dispose();
    }

    /// <summary>The copy row's refusals (2026-10-25): no other profile says so; a folder or a skill of that name there is the error line with no question; No keeps.</summary>
    [Fact]
    public async Task CopyToAnotherProfile_NoneToPick_ATwinThere_OrNo_CopiesNothing()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // copy: no other profile yet
        Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.Contains("\n  · " + SkillsMenu.NoOtherProfileForCopyNotice + "\n", Output);
        Assert.DoesNotContain(SkillsMenu.CopyToTitle("haiku"), Output);

        string work = SkillsMenu.ProfileSkillsRoot(_dir, "work");
        Directory.CreateDirectory(Path.Combine(work, "haiku"));
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Enter);   // copy, work: the folder's there
        Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.Contains("\n  ✗ " + SkillsMenu.CopyExistsError("haiku", "haiku", "work") + "\n", Output);
        Assert.DoesNotContain(SkillsMenu.CopyToPrompt("haiku", "work"), Output);

        Directory.Delete(Path.Combine(work, "haiku"));
        Directory.CreateDirectory(Path.Combine(work, "verse"));
        File.WriteAllText(Path.Combine(work, "verse", SkillCatalog.FileName), "---\nname: haiku\ndescription: d\n---\n\ni\n");
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Enter);   // copy, work: a skill named haiku there
        Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.Contains("\n  ✗ " + SkillsMenu.CopyExistsError("haiku", "haiku", "work", byName: true) + "\n", Output);
        Assert.DoesNotContain(SkillsMenu.CopyToPrompt("haiku", "work"), Output);

        Directory.Delete(Path.Combine(work, "verse"), recursive: true);
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Enter, Keys.Enter);   // copy, work, No
        Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.Contains(SkillsMenu.CopyToPrompt("haiku", "work"), Output);
        Assert.Contains("\n  · " + SkillsMenu.KeptNotice + "\n", Output);
        Assert.False(Directory.Exists(Path.Combine(work, "haiku")));
        Assert.True(Exists(SkillScope.Profile, "haiku"));
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

        Assert.Equal(2, Output.Split("\n  · " + SkillsMenu.KeptNotice + "\n" + SkillsHead(1) + "\n▸ haiku  global   Writes haiku.\n").Length - 1);
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
        Push(Keys.Down, Keys.Enter, Keys.Up, Keys.Enter);              // the shadowed global one (the headings no stops) → profile: taken too
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n  · " + SettingsMenu.UnchangedNotice + "\n" + SkillsHead(1) + "\n▸ haiku  profile  Writes haiku.\n", Output);
        Assert.Contains("\n  ✗ " + SkillsMenu.ExistsError("haiku", "haiku", SkillScope.Global) + "\n" + SkillsHead(1) + "\n▸ haiku  profile  Writes haiku.\n", Output);
        Assert.Contains("\n  ✗ " + SkillsMenu.ExistsError("haiku", "haiku", SkillScope.Profile) + "\n" + SkillsHead(1) + "\n  haiku  profile  Writes haiku.\n" + ShadowedHead(1) + "\n▸ haiku  global   shadowed by the profile skills\n", Output);
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

        Assert.Contains("\n  · " + SkillsMenu.ExternalReadOnlyNotice + "\n" + SkillsHead(1) + "\n▸ pdf   external Extracts PDF text.\n", Output);
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
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // delete (past rename, 2026-09-21, edit, 2026-09-23, lock, 2026-10-07, and disable, 2026-10-09)
        Push(Keys.Enter);                                       // No: kept
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // delete again
        Push(Keys.Char('y'), Keys.Enter);                       // Yes by hotkey
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n" + Titled(SkillsMenu.ScopeTitle("haiku")) + "\n \n" + Fitted("  profile  " + _roots.Profile) + "\n" + Fitted("▸ global   " + _roots.Global) + "\n" + Rule(100) + "\n  rename   give it a new name (letters, digits and hyphens)\n  edit     open its SKILL.md in your editor\n  lock     keep the model, reflection and updates from changing it\n  disable  stop offering it; a reflection still sees it, and changes nothing\n  delete   remove the folder and everything in it\n", Output);
        Assert.Contains("\n" + Titled(SkillsMenu.DeletePrompt("haiku", SkillScope.Global)) + "\n \n▸ No\n  Yes\n", Output);
        Assert.Contains("\n  · " + SkillsMenu.KeptNotice + "\n" + SkillsHead(1) + "\n▸ haiku  global   Writes haiku.\n", Output);
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
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);      // edit (past global, move, copy, 2026-10-25, and rename)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string path = Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName);
        Assert.Equal([path], _opened);
        Assert.Contains("\n  edit     open its SKILL.md in your editor\n  lock     keep the model, reflection and updates from changing it\n  disable  stop offering it; a reflection still sees it, and changes nothing\n  delete   remove the folder and everything in it\n", Output);   // the delete row after it, always (2026-09-23)
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
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // edit
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

    /// <summary>
    /// The lock row (2026-10-07, the user's ask): between revert and delete, lock at once with its notice; on the locked skill the row
    /// reads unlock, rename is refused before its slot, delete before its question, and the Offered footer says it is locked.
    /// </summary>
    [Fact]
    public async Task LockRow_LocksTheSkill_AndALockedSkillRefusesRenameAndDelete()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        string folder = Path.Combine(_roots.Profile, "haiku");
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);                                            // the page, on profile
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // lock (past global, move, copy, rename, edit)
        Push(Keys.Enter);                                            // the page again: now unlock
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);                      // rename: refused
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // delete: refused (past disable, 2026-10-09)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(SkillLock.IsLocked(folder));
        Assert.True(File.Exists(Path.Combine(folder, SkillCatalog.FileName)));
        Assert.Contains("  · " + SkillRecordText.LockSetNotice("haiku", true) + "\n", Output);
        Assert.Contains("lock     keep the model, reflection and updates from changing it", Output);
        Assert.Contains("unlock   let it be changed again", Output);
        Assert.Contains(SkillRecordText.LockedNotice("haiku"), Output);
        Assert.Contains(SkillRecordText.LockedFooter, Output);
        Assert.DoesNotContain(SkillsMenu.DeletePrompt("haiku", SkillScope.Profile), Output);
        Assert.Equal("lock     [#9A8BB8]keep the model, reflection and updates from changing it[/]", SkillsMenu.LockRow(false));
    }

    /// <summary>
    /// The disable row (2026-10-09, the user's ask): after lock, disable at once with its notice; the skill stays on the list with the
    /// footer saying so, its row reads enable, and enable offers it again.
    /// </summary>
    [Fact]
    public async Task DisableRow_DisablesTheSkill_ItStaysListed_AndEnableBringsItBack()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        string folder = Path.Combine(_roots.Profile, "haiku");
        var (menu, _) = PaneMenu();
        Push(Keys.Enter);                                                       // the page, on profile
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // disable (past global, move, copy, rename, edit, lock)
        Push(Keys.Enter);                                                       // the page again: now enable
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // enable
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(SkillDisable.IsDisabled(folder));
        Assert.Contains("  · " + SkillRecordText.DisableSetNotice("haiku", true) + "\n" + SkillsHead(1) + "\n▸ haiku  profile  Writes haiku.\n", Output);   // still listed
        Assert.Contains(SkillRecordText.DisabledFooter, Output);
        Assert.Contains("\n▸ enable   offer it to the model again\n", Output);
        Assert.Contains("  · " + SkillRecordText.DisableSetNotice("haiku", false) + "\n", Output);
        Assert.True(File.Exists(Path.Combine(folder, SkillCatalog.FileName)));
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
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);                 // rename: the slot with "haiku" in it
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("My Haiku!!");
        Push(Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        HeldRows.AssertContains("\n▸ rename   give it a new name (letters, digits and hyphens)\n  edit     open its SKILL.md in your editor\n  lock     keep the model, reflection and updates from changing it\n  disable  stop offering it; a reflection still sees it, and changes nothing\n  delete   remove the folder and everything in it\n", "› \n" + Rule(100) + "\n" + SettingsMenu.EditKeys, Output);
        Assert.Contains("\n" + Titled(Strip) + "\n" + Scopes + "\n  · " + SkillsMenu.RenamedNotice("haiku", "my-haiku") + "\n" + SkillsHead(1) + "\n▸ my-haiku  profile  Writes haiku.\n", Output);
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
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);     // rename
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("PDF");                         // a global skill's name, kebab-cased
        Push(Keys.Enter);
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);     // rename again
        Push(Keys.Enter);                                       // the name it has
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);     // rename again
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("!!!");                         // nothing survives
        Push(Keys.Enter);
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);     // rename again
        Push(Keys.Escape);                                      // ESC on the slot: nothing said
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n  ✗ " + SkillsMenu.RenameExistsError("haiku", "pdf", SkillScope.Global) + "\n" + SkillsHead(2) + "\n▸ haiku  profile  Writes haiku.\n", Output);
        Assert.Contains("\n  · " + SettingsMenu.UnchangedNotice + "\n" + SkillsHead(2) + "\n▸ haiku  profile  Writes haiku.\n", Output);
        Assert.Contains("\n  ✗ " + SkillsMenu.RenameEmptyError + "\n" + SkillsHead(2) + "\n▸ haiku  profile  Writes haiku.\n", Output);
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

        Assert.Contains("\n  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n" + SkillsHead(1) + "\n▸ haiku  profile  Writes haiku.\n", Output);
        Assert.DoesNotContain(SkillsMenu.ScopeTitle("haiku"), Output);
        pane.Dispose();
    }

    /// <summary>A double-click on a skill row is Enter (the menu's rule); two clicks off the pane close every level, the scope page included.</summary>
    [Fact]
    public async Task ADoubleClickOnARow_OpensTheScopePage_AndTwoClicksOffThePane_CloseEverything()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, pane, input) = ClickablePaneMenu(cursorTop: 100);
        input.PushClick(4, 104);                     // haiku: strip 100, the scopes 101 (2026-10-10), spacer 102, the catalog's heading 103 (later that day)
        input.PushClick(4, 104);
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
        Assert.Contains("  · Offered\n  ·   haiku  profile  Writes haiku.\n  · Reflection\n  ·   Reflection (auto-learn): on\n  ·   Reflection reasoning: none\n  ·   Reflection window: 3 turns\n  ·   Reflection min tool calls: 4 tool calls\n  ·   Reflection max requests: 4 requests\n  ·   Reflection include sessions: on\n  ·   Reflection cooldown (min): 5 minutes\n  ·   Reflection cooldown mode: last-written-skill\n  ·   Reflection yield to turns: on\n  ·   Reflection mode: create-update\n  ·   Reflection confirm mode: new\n  ·   Reflection edit supporting files: off\n  ·   Reflection edit downloaded skills: allow-and-mark\n  · Options\n  ·   Agent skills: on\n  ·   Global skills: on\n  ·   " + SettingsMenu.ExternalSkillsName + ": off\n  ·   Project skills: on\n  ·   Project file: on\n  ·   Skill compact mode: protected\n  ·   #-mention enabled: on\n", Output);
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
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);   // Reflection include sessions (under the max requests since 2026-10-10) (the last row until Reflection yield to turns on 2026-09-24; Reflection verbose there until later still on 2026-09-19): the page (the cursor on the saved on row), off picked under it
        Push(Keys.Left);                                        // Offered (the off line now)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.AgentSkills);
        Assert.False(_settings.Current.ReflectionIncludesSessions);
        AssertPadded("\n" + Titled(Strip) + "\n \n" + OptionsRows, Rule(100) + "\n" + SettingsMenu.TabKeys + "\n");
        AssertPadded("\n" + Titled(Strip) + "\n \n" + ReflectionRows, Rule(100) + "\n" + SettingsMenu.TabKeys + "\n");   // the flip's notice dropped by the tab switch (2026-09-20)
        Assert.DoesNotContain("\n  · Agent skills: off\n" + ReflectionRows, Output);
        Assert.Contains("\n" + Titled(SkillsText.Label + " › Reflection include sessions") + "\n", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · Reflection include sessions: off\n", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · Agent skills: off\n" + Head("Sources") + "\n▸ Agent skills                      off\n", Output);
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
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Skill compact mode (under Project file since 2026-10-01; one further down since Global skills, 2026-10-13, and Project skills, 2026-11-23): the picker
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
        Push(Keys.Left, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);    // Options (the strip wrapped), #-mention enabled (fifth since 2026-10-01, sixth since Global skills on 2026-10-13, seventh since Project skills on 2026-11-23): the page (the cursor on the saved on row, the default), off picked — Allow skill delete, the row this used until it went on 2026-09-23
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None, midTurn: true);

        Assert.False(_settings.Current.SkillHashMention);
        Assert.Contains("  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n", Output);
        Assert.DoesNotContain(SkillsMenu.ScopeTitle("haiku"), Output);
        Assert.Contains("\n" + Titled(SkillsText.Label + " › #-mention enabled") + "\n", Output);
        pane.Dispose();
    }

    // ── Global skills (2026-10-13, the profile's switch) ────────────────────

    /// <summary>Off: no global skill listed and no global sub-tab; a skill's page has the profile's root row alone, and every later row still does its own act (here lock, four down).</summary>
    [Fact]
    public async Task GlobalOff_NoGlobalSkillOrSubTab_ThePageHasOneRootRow_AndItsRowsStillMatchTheirActs()
    {
        _settings.Update(d => d.GlobalSkills = false);
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        Put(SkillScope.Global, "pdf", "Reads PDFs.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter);                                                // the page, on profile
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);    // lock (past move, copy, rename, edit; no global row)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.DoesNotContain("Reads PDFs.", Output);
        Assert.Contains("\n" + Markup.Remove(InfoPane.SubStripRows(SkillsText.Label, ["all", "profile"], 0, 100)[0]) + "\n", Output);
        Assert.DoesNotContain(Scopes, Output);
        Assert.Contains("\n" + Titled(SkillsMenu.ScopeTitle("haiku")) + "\n \n" + Fitted("▸ profile  " + _roots.Profile) + "\n" + Rule(100) + "\n  move     send it to another profile's skills\n", Output);
        Assert.True(SkillLock.IsLocked(Path.Combine(_roots.Profile, "haiku")));
        Assert.Equal([SkillScope.Profile], SkillsMenu.ScopeRowsFor(Roots));
        Assert.Equal([SkillScope.Profile, SkillScope.Global], SkillsMenu.ScopeRows);
        pane.Dispose();
    }

    /// <summary>Flipped off on the Options tab while the global sub-tab was on show: back on Offered the strip is all and profile, on all.</summary>
    [Fact]
    public async Task GlobalOff_FlippedWhileOnTheGlobalSubTab_OfferedComesBackOnAll()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        Put(SkillScope.Global, "pdf", "Reads PDFs.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Right, Keys.Right);                    // the global sub-tab
        Push(Keys.Left, Keys.Left, Keys.Left);           // back past profile and all, the strip wrapping to Options
        Push(Keys.Down, Keys.Char(' '));                 // Global skills: off
        Push(Keys.Right);                                // Offered again (the strip wraps)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.GlobalSkills);
        Assert.Contains("\n  · Global skills: off\n", Output);
        string after = Output[Output.LastIndexOf("Global skills: off", StringComparison.Ordinal)..];
        Assert.Contains("\n" + Markup.Remove(InfoPane.SubStripRows(SkillsText.Label, ["all", "profile"], 0, 100)[0]) + "\n", after);
        Assert.DoesNotContain("Reads PDFs.", after);
        pane.Dispose();
    }

    // ── The review fixes over the project sub-tab (2026-11-23, the last-10 review) ──

    /// <summary>A trust past the cap (review #6/#52): 51 skills, one edited; only that one is marked, and the notice counts the 50 the trust covers.</summary>
    [Fact]
    public async Task TheTrustQuestion_PastTheCap_MarksOnlyTheEditedSkill_AndCountsFifty()
    {
        string repo = UseRepo();
        for (int i = 0; i <= ProjectSkillTrust.ProjectSkillCap; i++)
        {
            PutProject(repo, "a" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), "One of many.");
        }

        TrustRepo(repo);
        File.WriteAllText(Path.Combine(repo, ".agents", "skills", "a00", SkillCatalog.FileName), "---\nname: a00\ndescription: Edited.\n---\n\nNew steps.\n");
        _console.Profile.Width = 200;
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Enter);   // the trust row
        Push(Keys.Up, Keys.Up, Keys.Enter);                     // Trust
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.TrustChangedCaption(SkillsText.ThisFolderLabel, 0, 1, 0), Output);
        Assert.Contains(SkillsText.TrustedNotice(SkillsText.ThisFolderLabel, ProjectSkillTrust.ProjectSkillCap), Output);
    }

    /// <summary>Enter on a removed skill's row (review #75) keeps the question open: the Trust after it still saves.</summary>
    [Fact]
    public async Task TheTrustQuestion_EnterOnARemovedRow_KeepsItOpen()
    {
        string repo = UseRepo();
        PutProject(repo, "deploy", "Ships it.");
        string lint = PutProject(repo, "lint", "Lints.");
        TrustRepo(repo);
        Directory.Delete(lint, recursive: true);
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Enter);   // the trust row: View deploy on the cursor
        Push(Keys.Down, Keys.Enter);                            // lint's removed row: nothing
        Push(Keys.Up, Keys.Up, Keys.Up, Keys.Enter);            // Trust
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.TrustedNotice(SkillsText.ThisFolderLabel, 1), Output);
        Assert.Equal([".agents/skills/deploy"], _projectTrust!.Trusted(repo)!.Skills.Keys);
    }

    /// <summary>A skill that gains acting keys while the question is open (review #44): the save finds the change, and the question asked again shows the acting mark.</summary>
    [Fact]
    public async Task TheTrustQuestion_AskedAgain_ShowsTheActingMarkReadNow()
    {
        string repo = UseRepo();
        string deploy = PutProject(repo, "deploy", "Ships it.");
        _onView = _ => File.WriteAllText(Path.Combine(deploy, SkillCatalog.FileName), "---\nname: deploy\ndescription: Ships it.\nallowed-tools: Bash\n---\n\nThe repo's steps.\n");
        _console.Profile.Width = 200;
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Enter);   // the trust row
        Push(Keys.Enter);                                       // View deploy: the edit lands
        Push(Keys.Up, Keys.Up, Keys.Enter);                     // Trust: changed meanwhile, asked again
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.ChangedWhileReviewingNotice(SkillsText.ThisFolderLabel), Output);
        Assert.Contains(SkillsText.ActingMark, Output);
        Assert.Null(_projectTrust!.Trusted(repo));
    }

    /// <summary>An untrust (review #5) names only the folder's offered skills as still loaded: a shadowed "x" is the profile's, which stays.</summary>
    [Fact]
    public async Task Untrust_AShadowedName_IsNotNamedAsStillLoaded()
    {
        string repo = UseRepo();
        PutProject(repo, "deploy", "Ships it.");
        PutProject(repo, "x", "The repo's x.");
        Put(SkillScope.Profile, "x", "Mine.");
        TrustRepo(repo);
        _session = new ConversationHistory("system");
        Load(_session, "x");
        _console.Profile.Width = 200;
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Enter);                         // untrust, first under the heading
        Push(Keys.Down, Keys.Enter);                                                   // Yes
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_projectTrust!.Trusted(repo));
        Assert.Contains(SkillsText.UntrustedNotice(SkillsText.ThisFolderLabel, []), Output);
        Assert.DoesNotContain(SkillsText.UntrustedNotice(SkillsText.ThisFolderLabel, ["x"]), Output);
    }

    /// <summary>A copy is named by its skill (review #55): <c>.agents/skills/Foo-Dir</c>, named "foo", lands as <c>skills/foo</c>.</summary>
    [Fact]
    public async Task ProjectCopy_NamesTheCopyByItsSkill()
    {
        string repo = UseRepo();
        string folder = Path.Combine(repo, ".agents", "skills", "Foo-Dir");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, SkillCatalog.FileName), "---\nname: foo\ndescription: The repo's foo.\n---\n\nSteps.\n");
        TrustRepo(repo);
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Down, Keys.Enter);   // foo's page, under untrust
        Push(Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);     // copy to profile, Yes
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(Exists(SkillScope.Profile, "foo"));
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "Foo-Dir")));
    }

    /// <summary>A skill edited after the facts were read (review #102) is refused for copying, and nothing is written.</summary>
    [Fact]
    public async Task ProjectCopy_ASkillEditedSinceItsTrust_IsRefused()
    {
        string repo = UseRepo();
        PutProject(repo, "deploy", "Ships it.");
        TrustRepo(repo);
        _openFile = path => File.AppendAllText(path, "\nMore steps.\n");
        _console.Profile.Width = 200;
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Down, Keys.Enter, Keys.Enter);   // deploy's page, edit: the file changes, the list is as it was
        Push(Keys.Enter, Keys.Down, Keys.Enter);                            // deploy's page, copy to profile
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.ProjectCopyChangedError("deploy"), Output);
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "deploy")));
    }

    /// <summary>A nameless <c>.claude</c> skill takes its folder's name (review #49): it blocks a profile "foo" kept in a folder named apart from it.</summary>
    [Fact]
    public async Task MoveToProject_ANamelessClaudeSkill_BlocksItsFoldersName()
    {
        string repo = UseRepo();
        string mine = Path.Combine(_roots.Profile, "foo-dir");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, SkillCatalog.FileName), "---\nname: foo\ndescription: Mine.\n---\n\nbody\n");
        string claude = Path.Combine(repo, ".claude", "skills", "foo");
        Directory.CreateDirectory(claude);
        File.WriteAllText(Path.Combine(claude, SkillCatalog.FileName), "---\ndescription: The repo's.\n---\n\nSteps.\n");
        _console.Profile.Width = 200;
        var (menu, _) = PaneMenu();
        Push(Keys.Right, Keys.Enter);             // the profile sub-tab's foo
        Push(Keys.Down, Keys.Down, Keys.Enter);   // project: refused
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SkillsText.ProjectExistsError("foo", "foo-dir", SkillsText.ThisFolderLabel, byName: true), Output);
        Assert.True(File.Exists(Path.Combine(mine, SkillCatalog.FileName)));
    }

    /// <summary>A changed strip keeps the tab on show (review #51): a scope by its scope, the loaded sub-tab to the new strip's, all when the tab went.</summary>
    [Fact]
    public void RemapSub_KeepsTheTabOnShow()
    {
        SkillScope?[] before = [null, SkillScope.Profile, SkillScope.Global];
        SkillScope?[] after = [null, SkillScope.Profile, SkillScope.Global, SkillScope.Project];

        Assert.Equal(2, SkillsMenu.RemapSub(before, after, 2));
        Assert.Equal(4, SkillsMenu.RemapSub(before, after, 3));   // the loaded sub-tab, past the scopes
        Assert.Equal(1, SkillsMenu.RemapSub(after, before, 1));
        Assert.Equal(0, SkillsMenu.RemapSub(after, before, 3));   // project went
        Assert.Equal(3, SkillsMenu.RemapSub(before, before, 3));
    }

    /// <summary>The kept-both error says where the repo's copy stands (review #92).</summary>
    [Fact]
    public void MovedButKeptError_NamesTheReposTrust()
    {
        string trusted = SkillsText.MovedButKeptError("haiku", SkillScope.Profile, "in use", repoTrusted: true);
        string waiting = SkillsText.MovedButKeptError("haiku", SkillScope.Profile, "in use", repoTrusted: false);

        Assert.NotEqual(trusted, waiting);
        Assert.Contains("/skills › project", waiting);
        Assert.DoesNotContain("/skills › project", trusted);
    }
}
