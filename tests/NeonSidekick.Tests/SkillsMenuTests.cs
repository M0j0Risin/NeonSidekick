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
        var menu = new SkillsMenu(Facts, _settings, settings, new TranscriptRenderer(pane), menuPane, new InputLine(pane, keys), OpenFile, _usage);
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

    /// <summary><paramref name="before"/>, then the blank rows that hold a tab at its pane's tallest tab's height (2026-10-01), then <paramref name="after"/>.</summary>
    private void AssertPadded(string before, string after) =>
        Assert.Matches(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(before) + "(?: \n)*" + System.Text.RegularExpressions.Regex.Escape(after)), _console.Output);

    /// <summary>A title row as the pane prints it: the text, then the × close glyph in column width − 2.</summary>
    private static string Titled(string row, int width = 100) => row + new string(' ', width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    private const string Strip = SkillsText.Label + "   Offered    Reflection    Options ";   // Options last since 2026-09-22 (the user's ask); the Project tab before it went on 2026-10-01, its toggle an Options row; Loaded until 2026-09-19; Options (the settings rows, /settings' Skills tab until then) since later that day; Reflection (the reflection's rows out of Options) later still; the Roots tab after Project until later still that day

    /// <summary>The Options tab's five rows at their defaults, padded to the tab's own column (38: the external-skills label), as the pane prints them; Project file third since 2026-10-01. Pinned.</summary>
    private const string OptionsRows = "▸ Agent skills                          on\n  Use external skills (.agents\\skills)  off\n  Project file                          on\n  Skill compact mode                    protected\n  #-mention enabled                     on\n";   // Allow skill delete, the fifth, went on 2026-09-23 (delete always offered)

    /// <summary>The Reflection tab's nine rows at their defaults (later on 2026-09-19), padded to its own column (31: the cooldown minutes label). Pinned.</summary>
    private const string ReflectionRows = "▸ Reflection (auto-learn)           on\n  Reflection reasoning              none\n  Reflection window                 3 turns\n  Reflection min tool calls         4 tool calls\n  Reflection max requests           4 requests\n  Reflection cooldown (minutes)     5 minutes\n  Reflection cooldown mode          last-written-skill\n  Reflection includes sessions      on\n  Reflection yields to turns        on\n  Reflection edit supporting files  off\n  Reflection downloaded skills      read-only\n";

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
        Push(Keys.Left, Keys.Down, Keys.Down, Keys.Char(' '));   // Options (the strip wrapped), Project file: Space is nothing
        Push(Keys.Enter, Keys.Down, Keys.Enter);                 // its page, off picked
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        AssertPadded("\n" + Titled(Strip) + "\n \n▸ haiku  profile  Writes haiku.\n  Shadowed (a higher root holds the name):\n    haiku  global   shadowed by the profile skills\n", Rule(100) + "\n" + SkillsMenu.LoadedKeys + "\n");
        Assert.Contains("\n" + Titled(SkillsText.Label + " › Project file") + "\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · Project file: off\n  Agent skills                          on\n  Use external skills (.agents\\skills)  off\n▸ Project file                          off\n", _console.Output);   // the flip on the status line, the row re-read, the cursor kept
        Assert.False(_settings.Current.ProjectFile);
        Assert.DoesNotContain("Project    ", _console.Output);   // no Project tab in the strip
        Assert.DoesNotContain("Roots", _console.Output);
        Assert.DoesNotContain("Working directory", _console.Output);
        Assert.DoesNotContain(SkillsMenu.ScopeTitle("haiku"), _console.Output);
        Assert.False(pane.OverlayOpen);
        Assert.Equal(flow, pane.FlowRow);
        Assert.True(Exists(SkillScope.Profile, "haiku") && Exists(SkillScope.Global, "haiku"));
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
        Assert.Contains("\n  · Project file: off\n", _console.Output);
        Assert.DoesNotContain(SettingsMenu.NotWhileReplyRunsNotice, _console.Output);
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

        Assert.Contains("\n" + Titled(SkillsMenu.ScopeTitle("haiku")) + "\nloaded in 2 turns across 1 session; last loaded 2026-09-18 14:05\n \n" + Fitted("▸ profile  " + _roots.Profile) + "\n", _console.Output);
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

        Assert.Contains("\n" + Titled(SkillsMenu.ScopeTitle("haiku")) + "\n \n" + Fitted("▸ profile  " + _roots.Profile) + "\n" + Fitted("  global   " + _roots.Global) + "\n  rename   give it a new name (letters, digits and hyphens)\n  edit     open its SKILL.md in your editor\n  delete   remove the folder and everything in it\n" + Rule(100) + "\n" + SkillsMenu.ScopeKeys + "\n", _console.Output);   // delete always offered since 2026-09-23
        Assert.Contains("\n" + Titled(SkillsMenu.MovePrompt("haiku", SkillScope.Profile, SkillScope.Global)) + "\n \n▸ No\n  Yes\n" + Rule(100) + "\n" + SettingsMenu.ConfirmKeys + "\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · " + SkillsMenu.MovedNotice("haiku", SkillScope.Global) + "\n▸ haiku  global   Writes haiku.\n", _console.Output);
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

        Assert.Equal(2, _console.Output.Split("\n  · " + SkillsMenu.KeptNotice + "\n▸ haiku  global   Writes haiku.\n").Length - 1);
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

        Assert.Contains("\n  · " + SettingsMenu.UnchangedNotice + "\n▸ haiku  profile  Writes haiku.\n", _console.Output);
        Assert.Contains("\n  ✗ " + SkillsMenu.ExistsError("haiku", "haiku", SkillScope.Global) + "\n▸ haiku  profile  Writes haiku.\n", _console.Output);
        Assert.Contains("\n  ✗ " + SkillsMenu.ExistsError("haiku", "haiku", SkillScope.Profile) + "\n  haiku  profile  Writes haiku.\n  Shadowed (a higher root holds the name):\n▸   haiku  global   shadowed by the profile skills\n", _console.Output);
        Assert.DoesNotContain("Move skill", _console.Output);
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

        Assert.Contains("\n  · " + SkillsMenu.ExternalReadOnlyNotice + "\n▸ pdf   external Extracts PDF text.\n", _console.Output);
        Assert.DoesNotContain(SkillsMenu.ScopeTitle("pdf"), _console.Output);
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

        Assert.Contains("\n" + Titled(SkillsMenu.ScopeTitle("haiku")) + "\n \n" + Fitted("  profile  " + _roots.Profile) + "\n" + Fitted("▸ global   " + _roots.Global) + "\n  rename   give it a new name (letters, digits and hyphens)\n  edit     open its SKILL.md in your editor\n  delete   remove the folder and everything in it\n", _console.Output);
        Assert.Contains("\n" + Titled(SkillsMenu.DeletePrompt("haiku", SkillScope.Global)) + "\n \n▸ No\n  Yes\n", _console.Output);
        Assert.Contains("\n  · " + SkillsMenu.KeptNotice + "\n▸ haiku  global   Writes haiku.\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · " + SkillsMenu.DeletedNotice("haiku", SkillScope.Global) + "\n" + Fitted("▸ " + SkillsText.NoneLine) + "\n", _console.Output);
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
        Assert.Contains("\n  edit     open its SKILL.md in your editor\n  delete   remove the folder and everything in it\n", _console.Output);   // the delete row after it, always (2026-09-23)
        Assert.Contains("\n  · " + SkillsMenu.EditOpenedNotice("haiku", "")[..^1], _console.Output);   // the path fitted to the width after it
        Assert.Contains("\n▸ haiku  profile  Writes haiku.\n", _console.Output);
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

        Assert.Contains(SkillsMenu.EditFailedError("no editor"), _console.Output);
        Assert.DoesNotContain("opened skill", _console.Output);
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

        Assert.Contains("\n▸ rename   give it a new name (letters, digits and hyphens)\n  edit     open its SKILL.md in your editor\n  delete   remove the folder and everything in it\n› \n" + Rule(100) + "\n" + SettingsMenu.EditKeys, _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · " + SkillsMenu.RenamedNotice("haiku", "my-haiku") + "\n▸ my-haiku  profile  Writes haiku.\n", _console.Output);
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

        Assert.Contains("\n  ✗ " + SkillsMenu.RenameExistsError("haiku", "pdf", SkillScope.Global) + "\n▸ haiku  profile  Writes haiku.\n", _console.Output);
        Assert.Contains("\n  · " + SettingsMenu.UnchangedNotice + "\n▸ haiku  profile  Writes haiku.\n", _console.Output);
        Assert.Contains("\n  ✗ " + SkillsMenu.RenameEmptyError + "\n▸ haiku  profile  Writes haiku.\n", _console.Output);
        Assert.DoesNotContain("(renamed:", _console.Output);
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

        Assert.Contains("\n  ✗ " + SkillsMenu.MissingError("haiku") + "\n" + Fitted("▸ " + SkillsText.NoneLine) + "\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task MidTurn_EnterOnASkill_IsRefused()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None, midTurn: true);

        Assert.Contains("\n  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n▸ haiku  profile  Writes haiku.\n", _console.Output);
        Assert.DoesNotContain(SkillsMenu.ScopeTitle("haiku"), _console.Output);
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

        Assert.Contains(Titled(SkillsMenu.ScopeTitle("haiku")), _console.Output);
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

        Assert.Contains("\n" + Fitted("▸ " + SkillsText.OffLine) + "\n", _console.Output);
        Assert.DoesNotContain(SkillsText.Label + " › ", _console.Output);
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
        Assert.Contains("  · Offered\n  ·   haiku  profile  Writes haiku.\n  · Reflection\n  ·   Reflection (auto-learn): on\n  ·   Reflection reasoning: none\n  ·   Reflection window: 3 turns\n  ·   Reflection min tool calls: 4 tool calls\n  ·   Reflection max requests: 4 requests\n  ·   Reflection cooldown (minutes): 5 minutes\n  ·   Reflection cooldown mode: last-written-skill\n  ·   Reflection includes sessions: on\n  ·   Reflection yields to turns: on\n  ·   Reflection edit supporting files: off\n  ·   Reflection downloaded skills: read-only\n  · Options\n  ·   Agent skills: on\n  ·   Use external skills (.agents\\skills): off\n  ·   Project file: on\n  ·   Skill compact mode: protected\n  ·   #-mention enabled: on\n", _console.Output);
        Assert.DoesNotContain("Roots", _console.Output);
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
        Assert.DoesNotContain("\n  · Agent skills: off\n" + ReflectionRows, _console.Output);
        Assert.Contains("\n" + Titled(SkillsText.Label + " › Reflection includes sessions") + "\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · Reflection includes sessions: off\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · Agent skills: off\n▸ Agent skills                          off\n", _console.Output);
        Assert.Contains("\n" + Fitted("▸ " + SkillsText.OffLine) + "\n", _console.Output);   // the tabs re-read after the flip
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
        Assert.Contains("\n" + Titled(SkillsText.Label + " › Skill compact mode") + "\n", _console.Output);
        Assert.DoesNotContain(SettingsMenu.Title + " › Skill compact mode", _console.Output);
        Assert.Contains("  · Skill compact mode: unprotected\n", _console.Output);
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
        Assert.Contains("  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n", _console.Output);
        Assert.DoesNotContain(SkillsMenu.ScopeTitle("haiku"), _console.Output);
        Assert.Contains("\n" + Titled(SkillsText.Label + " › #-mention enabled") + "\n", _console.Output);
        pane.Dispose();
    }
}
