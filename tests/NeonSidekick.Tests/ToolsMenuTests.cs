using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Timers;
using NeonSidekick.UI;
using NeonSidekick.Web;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The <c>/tools</c> pane (2026-09-19): the Offered tab's flips and the three settings tabs that moved
/// off <c>/settings</c> that day — the tab-position and row tests came from <c>SettingsMenuTests</c> with them.
/// </summary>
public class ToolsMenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly AppSettings _settings;
    private readonly ManualTimeProvider _time = new();
    private readonly FakeSynthesizer _synth = new();
    private readonly SpeechSession _speech;
    private readonly IReadOnlyList<AIFunction> _clock;
    private readonly IReadOnlyList<AIFunction> _timers;
    private readonly IReadOnlyList<AIFunction> _files;
    private readonly IReadOnlyList<AIFunction> _web;
    private readonly IReadOnlyList<AIFunction> _memory;
    private readonly IReadOnlyList<AIFunction> _questions;
    private bool _paneOn = true;

    public ToolsMenuTests()
    {
        _console.Profile.Width = 120;   // 100 until 2026-09-24, when the Images tab took the ten-tab strip to 107 cells
        _settings = new AppSettings(_dir);
        _settings.Update(d => { d.TtsOutput = true; d.TtsSource = "http"; d.ToolsDisabled = []; d.GitNativeTools = true; });   // delete off by default (2026-09-20), Git native tools off by default (2026-09-21): the Offered-tab scripts start from every tool on
        _speech = new SpeechSession(_ => _synth, _ => new FakeAudioPlayback(), new ModelStore(Path.Combine(_dir, "models"), new HttpClient(new StubHttpMessageHandler())));
        var root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(root);
        var files = new NeonSidekick.Files.WorkingDirectory(() => root, _time);
        _clock = ChatScreen.ClockTools(_time);
        _timers = ChatScreen.TimerTools(new TimerBoard(_time, () => { }));
        _files = ChatScreen.FileTools(files, () => true, _ => { }, () => _settings.Current);
        _web = ChatScreen.WebTools(new WebAccess(new HttpClient(new StubHttpMessageHandler()), new FakeHeadlessBrowser(), _time), files, () => _settings.Current);
        _memory = ChatScreen.MemoryTools(new MemoryStore(Path.Combine(_dir, "memory"), _time));
        _questions = ChatScreen.AskTools((_, _) => Task.FromResult<IReadOnlyList<AskAnswer>?>(null), () => _settings.Current);
    }

    /// <summary>What the fixture's browser auto-detection "finds", so the empty <c>Browser path</c> row reads the same on every machine.</summary>
    private const string FakeBrowserPath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    public void Dispose()
    {
        _speech.Dispose();
        _settings.Dispose();
        _console.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The facts as the screen reads them: the six groups over the live settings (no skills or sessions here — the screen's own list is pinned by ToolsTextTests).</summary>
    private ToolsFacts Facts()
    {
        var e = _settings.Current;
        var disabled = ToolsText.DisabledSet(e.ToolsDisabled);
        return new ToolsFacts(SystemPromptSummary.ToolGroups(_clock, _timers, _files, _memory, e.Memory, e.LlmOfferTools, _web, e.WebTools, e.FileTools, _questions, e.AskUser, _paneOn, disabled: disabled), e.LlmOfferTools, disabled);
    }

    private void Push(params ConsoleKeyInfo[] keys)
    {
        foreach (var key in keys)
        {
            _console.Input.PushKey(key);
        }
    }

    private void Down(int times)
    {
        for (int i = 0; i < times; i++)
        {
            Push(Keys.Down);
        }
    }

    /// <summary>The menu over a pane with geometry: every list is a level of the pane, the notices its status line.</summary>
    private (ToolsMenu Menu, ScreenPane Pane, SettingsMenu Settings) PaneMenu(Func<string, CancellationToken, Task<string?>>? browseVault = null, Func<NeonSidekick.Sql.SqlNamedConnection, CancellationToken, Task<NeonSidekick.Sql.SqlRun>>? testSql = null, Func<NeonSidekick.Comfy.ComfyClient?>? comfy = null)
    {
        _console.Profile.Height = 40;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null), new ManualTimeProvider()) { Hint = () => "idle" };
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var settings = new SettingsMenu(new ConsoleWithInput(pane, keys), _settings, _ => null, new InputLine(pane, keys), new TranscriptRenderer(pane), _speech, menuPane, _ => FakeBrowserPath, browseVault: browseVault, testSqlConnection: testSql, comfyClient: comfy);
        var menu = new ToolsMenu(Facts, _settings, settings, new TranscriptRenderer(pane), menuPane);
        pane.Show();
        return (menu, pane, settings);
    }

    /// <summary><see cref="PaneMenu"/> over a scripted source that carries clicks, the overlay's first row at buffer row <paramref name="cursorTop"/> (the strip; the spacer under it, the rows from +2).</summary>
    private (ToolsMenu Menu, ScreenPane Pane, ScriptedInput Input) ClickablePaneMenu(int cursorTop)
    {
        _console.Profile.Height = 40;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => cursorTop), new ManualTimeProvider()) { Hint = () => "idle" };
        var input = new ScriptedInput();
        var keys = new KeySource(input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var settings = new SettingsMenu(new ConsoleWithInput(pane, keys), _settings, _ => null, new InputLine(pane, keys), new TranscriptRenderer(pane), _speech, menuPane, _ => FakeBrowserPath);
        var menu = new ToolsMenu(Facts, _settings, settings, new TranscriptRenderer(pane), menuPane);
        pane.Show();
        return (menu, pane, input);
    }

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    /// <summary>A title or strip row as the pane prints it: the text, then the × close glyph in column width − 2.</summary>
    // The pane's own rule (ScreenPane.Draw): a first row with no room for the gap and the glyph goes without. The nine-tab
    // strip (SQL, 2026-09-23) is 97 cells, so on the fixture's 100 columns the strip row carries no ×; a narrower title still does.
    private string Titled(string row) =>
        TextCells.Width(row) + ScreenPane.TrailerGap + TextCells.Width(ScreenPane.CloseGlyph) > _console.Profile.Width - 1
            ? row
            : row + new string(' ', _console.Profile.Width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    /// <summary>The strip as the pane prints it: the label, then every tab title with a space either side, two spaces between. Pinned.</summary>
    private const string Strip = ToolsText.Label + "   Offered    Web    Files    Shell    Ask    Git (native)    Obsidian    SQL    ComfyUI    Options ";   // Images since 2026-09-24, SQL since 2026-09-23, Obsidian since 2026-09-22, Options last since later that day (second from later on 2026-09-19); the user's order (Web, Files, Shell, Ask, Git (native)) since later on 2026-09-21, alphabetical before

    /// <summary>A tool row as the pane prints it at width 120 (the markup rendered): the name padded to 22, the state to 5, then the description, cut to 119 cells and an ellipsis (FittedMarkup; every description is longer).</summary>
    private string Row(string name, bool on, string mark = "  ") => Fitted(mark + name.PadRight(22) + (on ? "on" : "off").PadRight(5) + Description(name));

    private static string Fitted(string row) => row.Length <= 120 ? row : row[..119] + "…";

    private string Description(string name) => Facts().Groups.SelectMany(g => g.Tools).Single(t => t.Name == name).Description;

    [Fact]
    public void Labels_ArePinned()
    {
        // The three tabs that left /settings (2026-09-19): their rows unchanged, every field on exactly one tab of the three panes (Skills left for /skills later that day);
        // the Options tab ahead of them (later on 2026-09-19): the pane's own $-mention switch.
        Assert.Equal(6, SettingsMenu.TabFields.Count);   // Botchat since 2026-09-25
        Assert.Equal(9, SettingsMenu.ToolsTabFields.Count);   // Images since 2026-09-24; SQL since 2026-09-23   // Obsidian since 2026-09-22   // Git since 2026-09-20, Shell since 2026-09-21; the user's order (Web, Files, Shell, Ask, Git (native)) since later on 2026-09-21, alphabetical before
        Assert.Equal(["Offered", "Web", "Files", "Shell", "Ask", "Git (native)", "Obsidian", "SQL", "ComfyUI", "Options"], ToolsText.TabTitles);
        Assert.Equal([SettingsField.ToolsDollarMention, SettingsField.ToolCollapseCount, SettingsField.CodeCollapseCount], SettingsMenu.ToolsTabFields[8]);   // the fold's count under the switch (2026-09-22, the user's place), the code fold's under it
        Assert.Equal([SettingsField.WebTools, SettingsField.WebBrowserMode, SettingsField.WebBrowserPath, SettingsField.WebBrowserNetworkMode, SettingsField.WebSearchMethod, SettingsField.WebSearxngUrl, SettingsField.WebSearchMaxResults], SettingsMenu.ToolsTabFields[0]);
        Assert.Equal([SettingsField.FileTools, SettingsField.FileSafeEdits, SettingsField.FileTreeMaxLength, SettingsField.FileTreeShowSizes, SettingsField.FileMentionFolderMode, SettingsField.FileBrowserMode, SettingsField.FileViewImageMaxPerCall], SettingsMenu.ToolsTabFields[1]);   // the view_image cap last, 2026-09-19; the browser mode under the folder mode, 2026-09-21
        Assert.Equal([SettingsField.ShellCommandPolicy, SettingsField.ShellCommandAllowed, SettingsField.ShellPoliceOutsidePaths, SettingsField.ShellDefault, SettingsField.ShellTimeoutSeconds, SettingsField.ShellForegroundCapSeconds, SettingsField.ShellOutputMaxChars, SettingsField.ShellCodeLanguages, SettingsField.ShellCodeTimeoutSeconds, SettingsField.ShellToolBridge, SettingsField.ShellCodeMaxToolCalls], SettingsMenu.ToolsTabFields[2]);   // the policy (the switch) first, then the list, the shell, the caps, then execute_code's four (2026-09-21; the bridge switch later that day; the police toggle third, 2026-09-22)
        Assert.Equal([SettingsField.AskUser, SettingsField.AskMaxQuestions, SettingsField.AskMaxChoices], SettingsMenu.ToolsTabFields[3]);
        Assert.Equal([SettingsField.GitNativeTools, SettingsField.GitNativeDiffMaxLines, SettingsField.GitNativeLogMaxCommits, SettingsField.GitNativeEmail, SettingsField.GitNativeName], SettingsMenu.ToolsTabFields[4]);   // the switch first, then the limits, then the identity pair (2026-09-21); the Git native labels later that day
        Assert.Equal([SettingsField.ObsidianTools, SettingsField.ObsidianVault, SettingsField.ObsidianAllowDelete], SettingsMenu.ToolsTabFields[5]);   // the switch, then the vault (2026-09-22), then the delete switch (later that day)
        Assert.Equal([SettingsField.SqlTools, SettingsField.SqlConnectionsOffered, SettingsField.SqlDefaultConnection, SettingsField.SqlSetPassword, SettingsField.SqlAddConnection, SettingsField.SqlPercentMention, SettingsField.SqlQueryMaxRows, SettingsField.SqlQueryTimeoutSeconds, SettingsField.SqlConnectionsProfile, SettingsField.SqlConnectionsGlobal], SettingsMenu.ToolsTabFields[6]);   // the switch, the offered list (later that day), the default, the password prompt, the add-connection wizard and the %-mention switch (later that day), the two caps, the two edit rows (2026-09-23)
        Assert.Equal(Enum.GetValues<SettingsField>().Order(), SettingsMenu.TabFields.Concat(SettingsMenu.SkillsTabFields).Concat(SettingsMenu.ToolsTabFields).Concat(SettingsMenu.McpTabFields).SelectMany(t => t).Order());
        Assert.Equal(21, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[8]));   // "Tool collapse count" (2026-09-22; "$-mention enabled", 19, before)
        Assert.Equal(26, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[0]));   // "Web browser network mode" (the Web-prefixed labels, later still on 2026-09-19; "Web search max results", 24, before)
        Assert.Equal(32, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[1]));   // "File view image max (per call)" (later still on 2026-09-19; "Stale line number guard", 25, that morning; "Always return line numbers", 28, before)
        Assert.Equal(29, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[2]));   // "Shell tool bridge max calls" (the Shell tab, 2026-09-21; the row was "Shell code max tool calls", 27, until later that day)
        Assert.Equal(30, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[3]));   // "Ask max choices per question"
        Assert.Equal(28, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[4]));   // "Git native log max commits" (later on 2026-09-21; "Git log max commits", 21, from 2026-09-20)
        Assert.Equal(27, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[6]));   // "SQL connections (profile)" (2026-09-23)
        Assert.Equal(32, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[5]));   // "Obsidian allow delete (.trash)" (2026-09-23; "Obsidian allow delete" from later on 2026-09-22, "Obsidian tools" that morning)
        Assert.All(SettingsMenu.ToolsTabFields.SelectMany(t => t), f => Assert.False(SettingsMenu.RefusedMidTurn(f)));
        Assert.Equal("⚙️ Settings", SettingsMenu.Title);
    }

    [Fact]
    public async Task OnThePane_OpensOnTheOfferedTab_OnTheFirstTool_AndEscClosesIt()
    {
        var (menu, pane, _) = PaneMenu();
        _console.Profile.Height = 30;   // 39 rows since 2026-09-19 (43 with the nineteen file tools) no longer overflow the fixture's 40: shorter, so the scroll hint is still exercised
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        // The strip, the Clock heading, the cursor on get_current_time (the first tool row, past its heading), the hint with the flip keys; nothing reached the transcript.
        Assert.Contains("\n" + Titled(Strip) + "\n \n  Clock (3)\n" + Row(GetCurrentTimeTool.ToolName, true, "▸ ") + "\n" + Row(ShiftDateTool.ToolName, true) + "\n" + Row(DaysBetweenTool.ToolName, true) + "\n  Timers (3)\n", _console.Output);
        Assert.Contains("\n" + ToolsText.OfferedKeys + "\n", _console.Output);
        Assert.Contains("  Files (15)\n" + Row(GetWorkingDirectoryTool.ToolName, true) + "\n", _console.Output);
        Assert.Contains(MenuPane.MoreHint, _console.Output);   // 39 rows over 30: the list scrolls
        Assert.False(pane.OverlayOpen);
        Assert.Equal(0, pane.FlowRow);
        Assert.Empty(_settings.Current.ToolsDisabled);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_EnterOnATool_FlipsItInPlace_SavesAndShowsTheNotice_CursorKept_SpaceFlipsItBack()
    {
        var (menu, pane, _) = PaneMenu();
        Down(1);                                  // shift_date
        Push(Keys.Enter);                         // off
        Push(Keys.Char(' '));                     // on again
        Push(Keys.Enter);                         // off again
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["shift_date"], _settings.Current.ToolsDisabled);
        // The row reads off where it stands, the cursor on it, the notice on the status line under the strip.
        Assert.Contains("\n" + Titled(Strip) + "\n  · shift_date: off\n  Clock (2 of 3)\n" + Row(GetCurrentTimeTool.ToolName, true) + "\n" + Row(ShiftDateTool.ToolName, false, "▸ ") + "\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · shift_date: on\n  Clock (3)\n" + Row(GetCurrentTimeTool.ToolName, true) + "\n" + Row(ShiftDateTool.ToolName, true, "▸ ") + "\n", _console.Output);
        Assert.Equal(0, pane.FlowRow);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_EnterOnAHeading_DoesNothing()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Up);                            // the Clock heading
        Push(Keys.Enter, Keys.Char(' '));
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Empty(_settings.Current.ToolsDisabled);
        Assert.DoesNotContain("  · ", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AGroupOff_ShowsDimWithTheSwitchNamed_AndStillFlips()
    {
        _settings.Update(d => d.FileTools = false);
        var (menu, pane, _) = PaneMenu();
        Down(8);                                  // past the two other clock rows, the Timers heading and its three rows, the Files heading: get_working_directory
        Push(Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([GetWorkingDirectoryTool.ToolName], _settings.Current.ToolsDisabled);
        Assert.Contains("  Files (15) (off: File tools is off)\n", _console.Output);
        Assert.Contains("  · get_working_directory: off\n", _console.Output);
        Assert.Contains("  Files (14 of 15) (off: File tools is off)\n" + Row(GetWorkingDirectoryTool.ToolName, false, "▸ ") + "\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_LlmToolsOff_OpensWithTheOffLine_AndStillFlips()
    {
        _settings.Update(d => d.LlmOfferTools = false);
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Enter);                         // the cursor opens on the first tool row, past the off line and the heading
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([GetCurrentTimeTool.ToolName], _settings.Current.ToolsDisabled);
        Assert.Contains("\n" + Titled(Strip) + "\n \n" + Fitted("  " + ToolsText.OffLine) + "\n  Clock (3)\n" + Row(GetCurrentTimeTool.ToolName, true, "▸ ") + "\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · get_current_time: off\n" + Fitted("  " + ToolsText.OffLine) + "\n  Clock (2 of 3)\n" + Row(GetCurrentTimeTool.ToolName, false, "▸ ") + "\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheOptionsTab_IsTheLastTab_ItsToggleSaves()
    {
        // The $-mention switch (later on 2026-09-19): the pane's own row, last on the strip in the /skills shape (second until later on 2026-09-22).
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Left);                                        // the strip wraps: Offered → Options
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // $-mention enabled: the page, off picked
        Push(Keys.Right);                                       // back round to Offered
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.ToolsDollarMention);
        Assert.Contains("\n" + Titled(Strip) + "\n \n▸ $-mention enabled    on\n  Tool collapse count  2 lines\n  Code collapse count  20 lines\n" + Rule(100), _console.Output);
        Assert.Contains(ToolsText.Label + " › $-mention enabled", _console.Output);
        Assert.Contains("$ is ordinary text", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · $-mention enabled: off\n▸ $-mention enabled    off\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheAskTab_SitsBetweenShellAndGit_ItsToggleSaves()
    {
        // The user's order (later on 2026-09-21): Offered, Web, Files, Shell, Ask, Git (native) — Obsidian and Options after it since 2026-09-22.
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Right);   // Web, Files, Shell, Ask
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // Ask user: the page, off picked
        Push(Keys.Left, Keys.Left);                             // Shell, Files
        Push(Keys.Left, Keys.Left);                             // Web, Offered
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.AskUser);
        // The three rows padded to the tab's own column (30), the toggle's notice on the status line under the strip.
        Assert.Contains("\n" + Titled(Strip) + "\n \n▸ Ask user                      on\n  Ask max questions             10 questions\n  Ask max choices per question  10 choices\n" + Rule(100), _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · Ask user: off\n▸ Ask user                      off\n", _console.Output);
        Assert.Contains("\n▸ File tools                      on\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheFilesTab_SitsBetweenWebAndShell()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right, Keys.Right);                           // Web, Files
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // File tools: the page, off picked
        Push(Keys.Right, Keys.Right, Keys.Right);               // Shell, Ask, Git (native)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.FileTools);
        // The seven rows (the view_image cap last, 2026-09-19; the @-mention folder mode before it, 2026-09-17, and the browser mode under that, 2026-09-21; Safe edits off by default since 2026-09-19, folder-remain the default since then, Always return line numbers gone later that day and the stale line number guard later still, with edit_lines) padded to the tab's own column (32), the toggle's notice on the status line under the strip.
        Assert.Contains("\n" + Titled(Strip) + "\n \n▸ File tools                      on\n  File safe edits                 off\n  File /tree max length           500 entries\n  File /tree show sizes           on\n  File @-mention folder mode      folder-remain\n  File browser/tree mode          default\n  File view image max (per call)  10 pictures\n" + Rule(100), _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · File tools: off\n▸ File tools                      off\n", _console.Output);
        Assert.Contains("\n▸ Git native tools            on\n", _console.Output);
        pane.Dispose();
    }

    /// <summary>The Obsidian tab (2026-09-22): before Options, the last but one; the vault is typed here (the fixture has no folder picker), a folder without <c>.obsidian</c> refused, a vault saved.</summary>
    [Fact]
    public async Task OnThePane_TheObsidianTab_SitsBeforeOptions_AndAVaultMustHoldDotObsidian()
    {
        string plain = Path.Combine(_dir, "plain");
        string vault = Path.Combine(_dir, "vault");
        Directory.CreateDirectory(plain);
        Directory.CreateDirectory(Path.Combine(vault, ".obsidian"));
        var (menu, _, _) = PaneMenu();
        Push(Keys.Left, Keys.Left, Keys.Left, Keys.Left, Keys.Down, Keys.Enter);   // Offered → Options → Images → SQL → Obsidian, the vault row's typed slot
        Push([.. plain.Select(Keys.Char), Keys.Enter]);         // no .obsidian: refused, kept
        Push(Keys.Enter);
        Push([.. vault.Select(Keys.Char), Keys.Enter]);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(vault, _settings.Current.ObsidianVault);
        Assert.Contains("Obsidian vault " + SettingsMenu.ObsidianVaultError[..40], _console.Output);   // the status line, cut at the pane's width
        Assert.Contains("\n" + Titled(Strip) + "\n \n▸ Obsidian tools                  on\n  Obsidian vault                  (not set)\n  Obsidian allow delete (.trash)  on\n", _console.Output);
    }

    /// <summary>
    /// <c>SQL set password</c> (later on 2026-09-23): the connections that take a password with their store, then a masked
    /// slot under the picked one; the password lands encrypted in the connection's <c>sql.json</c> and never on screen.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheSqlTab_SetsAPassword_Masked_IntoTheConnectionsStore()
    {
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(_settings.ProfileDirectory);
        File.WriteAllText(path, """{ "connections": { "prod": { "server": "x", "auth": "runas", "user": "CONTOSO\\svc-test" }, "mine": { "server": "y", "auth": "windows" } } }""");
        var (menu, _, _) = PaneMenu();
        Push(Keys.Left, Keys.Left, Keys.Left, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Offered → Options → Images → SQL, the set-password row (the third since the offered list): the pick
        Push(Keys.Enter);                                               // prod, the one connection that takes a password
        Push([.. "s3cret".Select(Keys.Char), Keys.Enter]);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var loaded = NeonSidekick.Sql.SqlConfigFile.Load(path);
        Assert.StartsWith(NeonSidekick.Sql.WindowsCredentials.ProtectedPrefix, loaded.Connections[0].Config.Password);
        Assert.Equal("s3cret", NeonSidekick.Sql.SqlSecrets.Resolve(loaded.Connections[0]).Value);
        Assert.Contains("▸ " + SettingsMenu.SqlPasswordRow(loaded.Connections[0]), _console.Output);
        Assert.Contains("••••••", _console.Output);
        Assert.DoesNotContain("s3cret", _console.Output);
        Assert.Contains("Saved the password of 'prod', encrypted, in ", _console.Output);
        Assert.Equal("prod  (runas CONTOSO\\svc-test · encrypted in sql.json)", SettingsMenu.SqlPasswordRow(loaded.Connections[0]));
    }

    /// <summary>Types <paramref name="text"/> into the open slot and submits it.</summary>
    private void Type(string text) => Push([.. text.Select(Keys.Char), Keys.Enter]);

    /// <summary>Offered → Options → SQL, the add-connection row (the fifth, under the password prompt): the wizard.</summary>
    private void OpenSqlWizard() => Push(Keys.Left, Keys.Left, Keys.Left, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);

    /// <summary>
    /// <c>SQL add connection</c> (later on 2026-09-23, the user's ask): a SQL login walked through every page into the profile's
    /// file, the test run over the unsaved draft (its password handed over plain, nothing written), then saved — the entry in the
    /// file, the password encrypted after it, never on screen.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheSqlWizard_WalksASqlLogin_TestsTheDraft_AndSavesIt()
    {
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        var tested = new List<(NeonSidekick.Sql.SqlNamedConnection Connection, bool FileThere)>();
        var (menu, _, _) = PaneMenu(testSql: (c, _) =>
        {
            tested.Add((c, File.Exists(path)));
            return Task.FromResult(new NeonSidekick.Sql.SqlRun(NeonSidekick.Sql.SqlOutcome.Ok, "", c.Name, "", [new NeonSidekick.Sql.SqlGrid(["v"], [["Microsoft SQL Server 2022 (RTM) - 16.0\n\tCopyright"]], false)], TimeSpan.Zero));
        });
        OpenSqlWizard();
        Push(Keys.Enter);                         // the profile's file
        Type("aw");
        Type("127.0.0.1,1433");
        Type("AdventureWorks2022");
        Push(Keys.Enter);                         // sql
        Type("reader");
        Push(Keys.Enter);                         // file
        Type("s3cret");
        Push(Keys.Enter);                         // mandatory
        Push(Keys.Char('y'), Keys.Enter);         // trust the certificate
        Push(Keys.Enter);                         // the default timeout
        Type("the sample");
        Push(Keys.Down, Keys.Enter);              // Test
        Push(Keys.Up, Keys.Enter);                // Save
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var (connection, fileThere) = Assert.Single(tested);
        Assert.False(fileThere);
        Assert.Equal("s3cret", connection.Config.Password);
        Assert.Equal("file", connection.Config.PasswordStore);
        var loaded = NeonSidekick.Sql.SqlConfigFile.Load(path);
        var aw = Assert.Single(loaded.Connections);
        Assert.Equal("aw", aw.Name);
        Assert.Equal("127.0.0.1,1433", aw.Config.Server);
        Assert.Equal("AdventureWorks2022", aw.Config.Database);
        Assert.Equal("reader", aw.Config.User);
        Assert.True(aw.Config.TrustServerCertificate);
        Assert.Null(aw.Config.ConnectTimeoutSeconds);
        Assert.Equal("the sample", aw.Config.Description);
        Assert.StartsWith(NeonSidekick.Sql.WindowsCredentials.ProtectedPrefix, aw.Config.Password);
        Assert.Equal("s3cret", NeonSidekick.Sql.SqlSecrets.Resolve(aw).Value);
        Assert.StartsWith(NeonSidekick.Sql.SqlConfigFile.EmptyText[..40], File.ReadAllText(path));   // made with its commented shape
        Assert.Contains(SettingsMenu.SqlWizardTestOkNotice("aw", "Microsoft SQL Server 2022 (RTM) - 16.0"), _console.Output);
        Assert.Contains("Added 'aw' to ", _console.Output);   // the status line cuts the temp path at the pane's width
        Assert.Equal("Added 'aw' to " + path + ".", NeonSidekick.Sql.SqlText.ConnectionAdded("aw", path));
        Assert.Contains(SettingsMenu.SqlWizardSummaryCaption, _console.Output);
        Assert.Contains(SettingsMenu.SqlWizardMasked, _console.Output);
        Assert.DoesNotContain("s3cret", _console.Output);
        Assert.Null(_settings.Current.SqlConnectionsOffered);   // not narrowed: offered as it is
    }

    /// <summary>
    /// The wizard's other paths: Windows sign-in into the home's file skips the account and password pages; a bad timeout
    /// asks again; on a profile that narrowed its offered list the first save row offers the new one too.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheSqlWizard_WindowsSignIn_IntoTheHomesFile_OfferedToTheModel()
    {
        _settings.Update(d => d.SqlConnectionsOffered = ["other"]);
        string path = NeonSidekick.Sql.SqlConfigFile.GlobalPath(_settings.StorageDirectory);
        var (menu, _, _) = PaneMenu();
        OpenSqlWizard();
        Push(Keys.Down, Keys.Enter);              // the home's file
        Type("me");
        Type("sqlhost01");
        Push(Keys.Enter);                         // the login's default database
        Push(Keys.Down, Keys.Enter);              // windows: no user, store or password page follows
        Push(Keys.Enter);                         // mandatory
        Push(Keys.Enter);                         // no
        Type("abc");
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        Type("30");
        Push(Keys.Enter);                         // no description
        Push(Keys.Enter);                         // Save, and offer it
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var me = Assert.Single(NeonSidekick.Sql.SqlConfigFile.Load(path).Connections);
        Assert.Equal("windows", me.Config.Auth);
        Assert.Null(me.Config.User);
        Assert.Null(me.Config.PasswordStore);
        Assert.Null(me.Config.Password);
        Assert.Null(me.Config.Database);
        Assert.Equal(30, me.Config.ConnectTimeoutSeconds);
        Assert.Equal(["other", "me"], _settings.Current.SqlConnectionsOffered);
        Assert.Contains(SettingsMenu.SqlWizardTimeoutError("abc"), _console.Output);
        Assert.Contains(SettingsMenu.SqlWizardSaveHiddenRow, _console.Output);
        Assert.False(File.Exists(NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory)));
    }

    /// <summary>ESC steps back a page at a time, the answers kept; before the first page it ends with nothing written.</summary>
    [Fact]
    public async Task OnThePane_TheSqlWizard_EscStepsBack_AndOutOfTheFirstPage_WritesNothing()
    {
        var (menu, _, _) = PaneMenu();
        OpenSqlWizard();
        Push(Keys.Enter);
        Type("aw");
        Push(Keys.Escape);                        // the server page: back to the name, "aw" in its slot
        Push(Keys.Enter);                         // kept: forward again
        Push(Keys.Escape, Keys.Escape, Keys.Escape);   // server → name → file → out
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.SqlWizardCancelledNotice, _console.Output);
        Assert.DoesNotContain(SettingsMenu.SqlWizardNameRequired, _console.Output);
        Assert.False(File.Exists(NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory)));
    }

    /// <summary>
    /// A name the file has is refused, a runas account without a domain too; a failed test says the server's words and still
    /// leaves the choice; Cancel writes nothing (a Credential Manager draft never reaches the store).
    /// </summary>
    [Fact]
    public async Task OnThePane_TheSqlWizard_RefusesATakenName_AndARunAsWithoutDomain_AndCancelWritesNothing()
    {
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(_settings.ProfileDirectory);
        const string Before = """{ "connections": { "aw": { "server": "x", "auth": "windows" } } }""";
        File.WriteAllText(path, Before);
        var (menu, _, _) = PaneMenu(testSql: (c, _) => Task.FromResult(NeonSidekick.Sql.SqlRun.Refused(NeonSidekick.Sql.SqlOutcome.ConnectFailed, "Login failed for user.", c.Name)));
        OpenSqlWizard();
        Push(Keys.Enter);
        Type("AW");                               // taken, whatever the case
        Push(Keys.Backspace, Keys.Backspace);
        Type("rep");
        Type("sqlhost01");
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Down, Keys.Enter);   // runas
        Type("svc");                              // no domain
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        Type(@"CONTOSO\svc");
        Push(Keys.Down, Keys.Enter);              // credman
        Type("pw");
        Push(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);   // mandatory, no, the default timeout, no description
        Push(Keys.Down, Keys.Enter);              // Test: refused
        Push(Keys.Down, Keys.Enter);              // Cancel
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("'AW' is already in ", _console.Output);
        Assert.Contains(NeonSidekick.Sql.SqlText.RunAsNeedsDomain("svc"), _console.Output);
        Assert.Contains("Error: could not connect to rep: Login failed for user.", _console.Output);
        Assert.Contains(SettingsMenu.SqlWizardCancelledNotice, _console.Output);
        Assert.Equal(Before, File.ReadAllText(path));
    }

    /// <summary>The summary's real test (no fake): the live server's login walked in, <c>SELECT @@VERSION</c> answered, then Cancel — nothing written.</summary>
    [LiveSqlFact]
    public async Task OnThePane_TheSqlWizard_TestsALiveServer()
    {
        var live = LiveSql.Config!;
        var (menu, _, _) = PaneMenu();
        OpenSqlWizard();
        Push(Keys.Enter);
        Type("live");
        Type(live.Server!);
        Type(live.Database ?? "");
        Push(Keys.Enter);                         // sql
        Type(live.User!);
        Push(Keys.Enter);                         // file
        Type(LiveSql.Password);
        int encrypt = SettingsMenu.SqlWizardEncryptWords.ToList().IndexOf(live.Encrypt!);
        Push([.. Enumerable.Repeat(Keys.Down, encrypt), Keys.Enter]);
        Push(live.TrustServerCertificate ? Keys.Char('y') : Keys.Char('n'), Keys.Enter);
        Push(Keys.Enter, Keys.Enter);             // the default timeout, no description
        Push(Keys.Down, Keys.Enter);              // Test
        Push(Keys.Down, Keys.Enter);              // Cancel
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("Connected to 'live': Microsoft SQL Server", _console.Output);
        Assert.DoesNotContain(LiveSql.Password, _console.Output);
        Assert.False(File.Exists(NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory)));
    }

    /// <summary>Enter on a summary row changes that choice and comes back — by the page the change now asks for (a sign-in that takes a password asks for the account and the password).</summary>
    [Fact]
    public async Task OnThePane_TheSqlWizard_ChangesAChoiceFromTheSummary()
    {
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        var (menu, _, _) = PaneMenu();
        OpenSqlWizard();
        Push(Keys.Enter);
        Type("me");
        Type("sqlhost01");
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter);              // windows
        Push(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);
        // The summary: Save, Test, Cancel, then the rows File, Name, Server, Database, Sign-in (the eighth).
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
        Push(Keys.Up, Keys.Enter);                // sql: the user page, then the password page, then the summary
        Type("reader");
        Type("pw");
        Push(Keys.Home, Keys.Enter);              // Save
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var me = Assert.Single(NeonSidekick.Sql.SqlConfigFile.Load(path).Connections);
        Assert.Equal("sql", me.Config.Auth);
        Assert.Equal("reader", me.Config.User);
        Assert.Equal("pw", NeonSidekick.Sql.SqlSecrets.Resolve(me).Value);
    }

    /// <summary>
    /// <c>SQL connections offered</c> (later on 2026-09-23): a checklist of every connection; the first flip narrows the
    /// profile to the rest, a flip back offers it again, and a connection added to the file afterwards starts unticked.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheSqlTab_NarrowsTheOfferedConnections_AndANewOneStartsHidden()
    {
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(_settings.ProfileDirectory);
        File.WriteAllText(path, """{ "connections": { "aw": { "server": "x", "auth": "windows" }, "prod": { "server": "y", "auth": "windows" } } }""");
        var (menu, _, _) = PaneMenu();
        Push(Keys.Left, Keys.Left, Keys.Left, Keys.Down, Keys.Enter);   // Offered → Options → Images → SQL, the offered row: the checklist
        Push(Keys.Down, Keys.Enter);                         // prod off
        Push(Keys.Char(' '));                                // and on again (Space flips too)
        Push(Keys.Enter);                                    // and off
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["aw"], _settings.Current.SqlConnectionsOffered);
        Assert.Contains("SQL connections offered    all (not narrowed)", _console.Output);
        Assert.Contains("SQL connections offered    1 of 2", _console.Output);
        Assert.Contains("[x] aw    x", _console.Output);

        File.WriteAllText(path, """{ "connections": { "aw": { "server": "x", "auth": "windows" }, "prod": { "server": "y", "auth": "windows" }, "new": { "server": "z", "auth": "windows" } } }""");
        var loaded = NeonSidekick.Sql.SqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
        Assert.Equal("1 of 3", SettingsMenu.SqlOfferedValue(_settings.Current.SqlConnectionsOffered, loaded));   // the new one hidden until ticked
        Assert.Equal(SettingsMenu.SqlNotNarrowedLabel, SettingsMenu.SqlOfferedValue(null, loaded));
        Assert.Equal("none of 3", SettingsMenu.SqlOfferedValue([], loaded));
    }

    /// <summary>The vault row's folder picker opens on the vault saved (later on 2026-09-22, the user's report: it opened on the working directory every time): empty the first time, the picked vault the next; nothing picked keeps it.</summary>
    [Fact]
    public async Task OnThePane_TheVaultPicker_OpensOnTheSavedVault()
    {
        string vault = Path.Combine(_dir, "vault");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidian"));
        var opened = new List<string>();
        var answers = new Queue<string?>([vault, null]);
        var (menu, pane, _) = PaneMenu((openOn, _) => { opened.Add(openOn); return Task.FromResult(answers.Dequeue()); });
        Push(Keys.Left, Keys.Left, Keys.Left, Keys.Left, Keys.Down, Keys.Enter);   // Offered → Options → Images → SQL → Obsidian, the vault row: the picker, the vault picked
        Push(Keys.Enter);                                       // again: nothing picked
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["", vault], opened);
        Assert.Equal(vault, _settings.Current.ObsidianVault);
        pane.Dispose();
    }

    /// <summary>The Git (native) tab (2026-09-20; its name since later on 2026-09-21, the last but one since 2026-09-22): the switch first, the two caps typed.</summary>
    [Fact]
    public async Task OnThePane_TheGitTab_SitsBeforeObsidian_ItsCapsAreTyped()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Left, Keys.Left, Keys.Left, Keys.Left, Keys.Left);   // the strip wraps: Offered → Options → Images → SQL → Obsidian → Git (native), its first row
        Push(Keys.Down, Keys.Enter);                            // Git diff max lines: the typed slot, pre-filled with 500
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Char('1'), Keys.Char('0'), Keys.Char('0'), Keys.Char('0'), Keys.Enter);
        Push(Keys.Down, Keys.Enter);                            // Git log max commits: the slot, pre-filled with 20; 500 is out of range, kept
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('5'), Keys.Char('0'), Keys.Char('0'), Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(1000, _settings.Current.GitNativeDiffMaxLines);
        Assert.Equal(20, _settings.Current.GitNativeLogMaxCommits);
        // The five rows padded to the tab's own column (28), the diff cap's notice then the log cap's refusal on the status line.
        Assert.Contains("\n" + Titled(Strip) + "\n \n▸ Git native tools            on\n  Git native diff max lines   500 lines\n  Git native log max commits  20 commits\n  Git native email            (not set)\n  Git native name             (not set)\n" + Rule(100), _console.Output);
        Assert.Contains("\n  Git native diff max lines   1000 lines\n", _console.Output);
        Assert.Contains("Git native log max commits must be 1 to 200 commits; keeping 20.", _console.Output);
        pane.Dispose();
    }

    /// <summary>The Shell tab (2026-09-21; between Files and Ask since later that day): the policy picker first (its switch), the allowed list, the shell picker, then the three typed rows.</summary>
    [Fact]
    public async Task OnThePane_TheShellTab_SitsBetweenFilesAndAsk_PolicyAndShellArePickers_TheListRemoves()
    {
        _settings.Update(d => d.ShellCommandAllowed = ["git push", "dotnet build"]);
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right);   // Web, Files, Shell
        Push(Keys.Enter, Keys.Down, Keys.Enter);                            // Shell command policy: the picker opens on ask, yolo picked
        Push(Keys.Down, Keys.Enter, Keys.Enter, Keys.Escape);               // Shell allowed commands: the list, dotnet build removed, back
        Push(Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);      // past Shell police outside paths (2026-09-22); Shell default: the picker, cmd picked
        Push(Keys.Down, Keys.Enter);                                        // Shell timeout (s): the typed slot, pre-filled with 180; 0 is out of range, kept
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("yolo", _settings.Current.ShellCommandPolicy);
        Assert.Equal(["git push"], _settings.Current.ShellCommandAllowed);
        Assert.Equal("cmd", _settings.Current.ShellDefault);
        Assert.Equal(180, _settings.Current.ShellTimeoutSeconds);
        // The ten rows padded to the tab's own column (27), then the picker's rows, the list's, and the notices on the status line.
        Assert.Contains("\n" + Titled(Strip) + "\n \n▸ Shell command policy         ask\n  Shell allowed commands       2 prefixes\n  Shell police outside paths   on\n  Shell default                powershell\n  Shell timeout (s)            180\n  Shell foreground cap (s)     600\n  Shell output max chars       30,000 chars\n  Shell code languages         powershell, python, node\n  Shell code timeout (s)       300\n  Shell tool bridge            off\n  Shell tool bridge max calls  50 tool calls\n" + Rule(100), _console.Output);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell command policy") + "\n \n  off  no shell or script tool is offered\n▸ ask  you approve each command not on the allow list\n  yolo every command runs, nothing is asked\n", _console.Output);
        Assert.Contains("  · Shell command policy: yolo\n", _console.Output);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell allowed commands") + "\n \n▸ dotnet build\n  git push\n", _console.Output);
        Assert.Contains("  · Shell allowed commands: dotnet build removed\n▸ git push\n", _console.Output);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell default") + "\n \n▸ powershell pwsh when installed, else Windows PowerShell 5.1\n  cmd        cmd.exe: batch syntax\n  bash       Git Bash, when bash.exe is found\n", _console.Output);
        Assert.Contains("  · Shell default: cmd\n", _console.Output);
        Assert.Contains("Shell timeout (s) must be 1 to 3600 seconds; keeping 180.", _console.Output);
        pane.Dispose();
    }

    /// <summary>The tool bridge row (later on 2026-09-21): the Shell tab's one toggle, a picker that opens on the saved off; no reconnect.</summary>
    [Fact]
    public async Task OnThePane_TheToolBridgeRow_IsAPicker_NoReconnect()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right);   // Shell
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Shell tool bridge: the picker opens on off (one more Down since the police row, 2026-09-22)
        Push(Keys.Up, Keys.Enter);                                          // on is the row above
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.ShellToolBridge);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell tool bridge") + "\n \n  on  a script may call this app's other tools through its neon_tools module\n▸ off a script does everything itself: no neon_tools module, no tool calls\n", _console.Output);
        Assert.Contains("  · Shell tool bridge: on", _console.Output);
        Assert.Contains("\n▸ Shell tool bridge            on\n  Shell tool bridge max calls  50 tool calls\n", _console.Output);
        pane.Dispose();
    }

    /// <summary>The outside-paths police row (2026-09-22): the Shell tab's third row, a toggle that opens on the saved on; off is the row below; no reconnect.</summary>
    [Fact]
    public async Task OnThePane_ThePoliceRow_IsAPicker_NoReconnect()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right);   // Shell
        Push(Keys.Down, Keys.Down, Keys.Enter);                 // Shell police outside paths: the picker opens on on
        Push(Keys.Down, Keys.Enter);                            // off is the row below
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.ShellPoliceOutsidePaths);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell police outside paths") + "\n \n▸ on  paths outside the working directory are denied\n  off paths anywhere on the computer are allowed\n", _console.Output);
        Assert.Contains("  · Shell police outside paths: off", _console.Output);
        Assert.Contains("\n▸ Shell police outside paths   off\n  Shell default                powershell\n", _console.Output);
        pane.Dispose();
    }

    /// <summary>The code-languages list (2026-09-21): Enter or Space flips and saves at once, the last one on refuses to go.</summary>
    [Fact]
    public async Task OnThePane_TheCodeLanguagesRow_IsACheckboxList_TheLastOneStays()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right);   // Shell
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Shell code languages: the list (one more Down since the police row, 2026-09-22)
        Push(Keys.Char(' '));                                               // powershell off
        Push(Keys.Down, Keys.Enter);                                        // python off
        Push(Keys.Down, Keys.Enter);                                        // node: the last one, refused
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["node"], _settings.Current.ShellCodeLanguages);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell code languages") + "\n \n▸ [x] powershell a .ps1 through pwsh or Windows PowerShell; Invoke-NeonTool calls a tool\n  [x] python     a .py through python.exe; from neon_tools import …\n  [x] node       a .js through node.exe; require('neon_tools')\n", _console.Output);
        Assert.Contains("  · Shell code languages: python, node\n", _console.Output);
        Assert.Contains("  · Shell code languages: node\n", _console.Output);
        Assert.Contains("At least one language stays on.", _console.Output);
        Assert.Contains("\n▸ Shell code languages         node\n  Shell code timeout (s)       300\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheWebTab_SitsAfterOptions()
    {
        // The last tab until later on 2026-09-21 (Left wrapped to it); third since, the user's order.
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right);                          // Web
        Push(Keys.Enter, Keys.Down, Keys.Enter);   // Web tools: the page, off picked
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.WebTools);
        // The seven rows padded to the tab's own column (26), the toggle's notice on the status line under the strip.
        Assert.Contains("\n" + Titled(Strip) + "\n \n▸ Web tools                 on\n  Web browser mode          default\n  Web browser path          (auto: msedge.exe)\n  Web browser network mode  internet\n  Web search method         duckduckgo\n  Web SearXNG URL           (not set)\n  Web search max results    20 results\n" + Rule(100), _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · Web tools: off\n▸ Web tools                 off\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_APickerFromTheWebTab_IsTitledUnderTools_AndTheRootComesBack()
    {
        var (menu, pane, settings) = PaneMenu();
        Push(Keys.Right);                          // Web
        Push(Keys.Down, Keys.Enter);               // Browser mode: the picker
        Push(Keys.Down, Keys.Enter);               // httpclient (the second name)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("httpclient", _settings.Current.WebBrowserMode);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Web browser mode") + "\n", _console.Output);
        Assert.DoesNotContain(SettingsMenu.Title + " › Web browser mode", _console.Output);
        Assert.Contains("  · Web browser mode: httpclient\n", _console.Output);
        Assert.Equal(SettingsMenu.Title, settings.Root);   // restored for /settings
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheSafeEditsRow_IsAPicker_NoReconnect()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right, Keys.Right);                                       // Files
        Push(Keys.Down, Keys.Enter, Keys.Up, Keys.Enter);                   // Safe edits: on (the page opens on the saved off; on is the row above)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.FileSafeEdits);
        Assert.Contains("  · File safe edits: on", _console.Output);
        Assert.Contains(SettingsMenu.ToggleDescribe(SettingsField.FileSafeEdits, true), _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ATypedRow_EditsUnderTheList_AndEscKeepsTheSavedValue()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Right);   // Ask
        Push(Keys.Down, Keys.Enter);                // Ask max questions: the typed slot
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('3'), Keys.Enter);
        Push(Keys.Down, Keys.Enter, Keys.Escape);   // Ask max choices: the slot, ESC
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(3, _settings.Current.AskMaxQuestions);
        Assert.Equal(10, _settings.Current.AskMaxChoices);
        Assert.Contains("  · Ask max questions: 3 questions\n", _console.Output);
        Assert.Contains("  · " + SettingsMenu.UnchangedNotice + "\n", _console.Output);
        Assert.Contains(Rule(100) + "\n" + SettingsMenu.EditKeys, _console.Output);   // the typed slot under the list (pre-filled with 10: two Backspaces and a 3 made 3), the edit keys in the hint row
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ToolCollapseCount_IsTheOptionsTabsSecondRow_Typed_ZeroIsOff_OutOfRangeRefused()
    {
        // 2026-09-22, the user's place: under $-mention enabled; 0 to 100, 2 by default, 0 = off.
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Left);                                          // Options, the strip wrapped
        Push(Keys.Down, Keys.Enter);                              // Tool collapse count: the typed slot with "2"
        Push(Keys.Backspace, Keys.Char('1'), Keys.Char('0'), Keys.Char('1'), Keys.Enter);   // refused: 101
        Push(Keys.Enter, Keys.Backspace, Keys.Char('0'), Keys.Enter);                        // 0: off
        Push(Keys.Enter, Keys.Backspace, Keys.Char('5'), Keys.Enter);                        // 5
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(5, _settings.Current.ToolCollapseCount);
        Assert.Contains("Tool collapse count " + SettingsMenu.ToolCollapseCountRangeError + "; keeping 2.", _console.Output);
        Assert.Contains("  · Tool collapse count: off\n", _console.Output);
        Assert.Contains("  · Tool collapse count: 5 lines\n", _console.Output);
        Assert.Equal("must be 0 to 100 lines (0 = off)", SettingsMenu.ToolCollapseCountRangeError);
        Assert.Equal("Tool collapse count", SettingsMenu.FieldName(SettingsField.ToolCollapseCount));
        Assert.False(SettingsMenu.IsToggle(SettingsField.ToolCollapseCount));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.ToolCollapseCount));
        Assert.Equal(2, new AppSettingsData().ToolCollapseCount);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_CodeCollapseCount_IsTheOptionsTabsThirdRow_Typed_ZeroIsOff_OutOfRangeRefused()
    {
        // Later on 2026-09-22, the user's place: under Tool collapse count; 0 to 100, 20 by default, 0 = off.
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Left);                                          // Options, the strip wrapped
        Push(Keys.Down, Keys.Down, Keys.Enter);                   // Code collapse count: the typed slot with "20"
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('1'), Keys.Char('0'), Keys.Char('1'), Keys.Enter);   // refused: 101
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);                      // 0: off
        Push(Keys.Enter, Keys.Backspace, Keys.Char('3'), Keys.Char('0'), Keys.Enter);                      // 30
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(30, _settings.Current.CodeCollapseCount);
        Assert.Contains("Code collapse count " + SettingsMenu.CodeCollapseCountRangeError + "; keeping 20.", _console.Output);
        Assert.Contains("  · Code collapse count: off\n", _console.Output);
        Assert.Contains("  · Code collapse count: 30 lines\n", _console.Output);
        Assert.Equal("must be 0 to 100 lines (0 = off)", SettingsMenu.CodeCollapseCountRangeError);
        Assert.Equal("Code collapse count", SettingsMenu.FieldName(SettingsField.CodeCollapseCount));
        Assert.False(SettingsMenu.IsToggle(SettingsField.CodeCollapseCount));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.CodeCollapseCount));
        Assert.Equal(20, new AppSettingsData().CodeCollapseCount);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ViewImageMaxPerCall_IsTheFilesTabsLastRow_Typed_OutOfRangeRefused()
    {
        // 2026-09-19, the user's ask: 1 to 100, 10 by default; the value the tool reads at its next call.
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right, Keys.Right);   // Files
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // View image max per call (the seventh row since 2026-09-21): the typed slot with "10"
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);        // refused: 0
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('1'), Keys.Char('0'), Keys.Char('1'), Keys.Enter);   // refused: 101
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('2'), Keys.Char('5'), Keys.Enter);   // 25
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(25, _settings.Current.FileViewImageMaxPerCall);
        Assert.Contains("File view image max (per call) " + SettingsMenu.ViewImageMaxPerCallRangeError + "; keeping 10.", _console.Output);
        Assert.Contains("  · File view image max (per call): 25 pictures\n", _console.Output);
        Assert.Contains("\n▸ File view image max (per call)  25 pictures\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_SpaceOnASettingsRow_IsNothing()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Right);   // Ask
        Push(Keys.Char(' '));
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.AskUser);
        Assert.Empty(_settings.Current.ToolsDisabled);
        Assert.DoesNotContain("  · ", _console.Output);
        Assert.DoesNotContain(SettingsMenu.PickKeys, _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task MidTurn_AFlipSaves_AndTheRowsEdit()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Enter);                           // get_current_time off
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Right, Keys.Enter, Keys.Down, Keys.Enter);   // Ask user off
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None, midTurn: true);

        Assert.Equal([GetCurrentTimeTool.ToolName], _settings.Current.ToolsDisabled);
        Assert.False(_settings.Current.AskUser);
        Assert.DoesNotContain(SettingsMenu.NotWhileReplyRunsNotice, _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task ADoubleClickOnAToolRow_FlipsIt_AndTwoClicksOffThePane_CloseIt()
    {
        var (menu, pane, input) = ClickablePaneMenu(100);
        input.PushClick(4, 104);                    // the strip at 100, the spacer at 101, Clock (3) at 102, get_current_time at 103, shift_date at 104
        input.PushClick(4, 104);
        input.PushClick(4, 50);                     // off the pane, twice: close
        input.PushClick(4, 50);
        input.Push(Keys.Escape);                    // never read

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([ShiftDateTool.ToolName], _settings.Current.ToolsDisabled);
        Assert.Contains("  · shift_date: off\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        Assert.False(pane.Dismissed);
        Assert.True(input.IsAvailable);
        pane.Dispose();
    }

    [Fact]
    public async Task WithoutThePane_PrintsTheFiveTabsAsLines_ToTheTranscript()
    {
        _settings.Update(d => d.ToolsDisabled = ["read_file"]);
        _paneOn = false;
        var pane = new ScreenPane(_console, geometry: null, _time);
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var settings = new SettingsMenu(_console, _settings, _ => null, new InputLine(_console, keys), new TranscriptRenderer(_console), _speech, menuPane, _ => FakeBrowserPath);
        var menu = new ToolsMenu(Facts, _settings, settings, new TranscriptRenderer(_console), menuPane);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  · Offered\n  ·   Clock (3)\n  ·     get_current_time      on   ", _console.Output);
        Assert.Contains("  ·   Files (14 of 15)\n", _console.Output);
        Assert.Contains("  ·     read_file             off  Reads a text file", _console.Output);   // the console wraps the long line
        Assert.Contains("switched off in /tools", _console.Output);
        Assert.Contains("  ·   Questions (1) (off: no pane)\n", _console.Output);
        // The tabs in strip order: Web right after Offered, then Files, Shell, Ask, Git (native) (the user's order, later on 2026-09-21), Obsidian, SQL (2026-09-23) and Options last (2026-09-22).
        Assert.Contains("  · Web\n  ·   Web tools: on\n  ·   Web browser mode: default\n  ·   Web browser path: (auto: msedge.exe)\n", _console.Output);
        Assert.Contains("  ·   Web search max results: 20 results\n  · Files\n  ·   File tools: on\n  ·   File safe edits: off\n", _console.Output);
        Assert.Contains("  · Shell\n  ·   Shell command policy: ask\n", _console.Output);
        Assert.Contains("  · Ask\n  ·   Ask user: on\n  ·   Ask max questions: 10 questions\n  ·   Ask max choices per question: 10 choices\n  · Git (native)\n  ·   Git native tools: on\n  ·   Git native diff max lines: 500 lines\n  ·   Git native log max commits: 20 commits\n  ·   Git native email: (not set)\n  ·   Git native name: (not set)\n  · Obsidian\n  ·   Obsidian tools: on\n  ·   Obsidian vault: (not set)\n  ·   Obsidian allow delete (.trash): on\n  · SQL\n  ·   SQL tools: on\n  ·   SQL connections offered: all (not narrowed)\n  ·   SQL default connection: (the first connection)\n  ·   SQL set password: Enter to set password for a connection\n  ·   SQL add connection: Enter to start connection wizard\n  ·   SQL %-mention enabled: on\n  ·   SQL max rows: 100 rows\n  ·   SQL query timeout (s): 30\n  ·   SQL connections (profile): (none) · Enter edits sql.json\n  ·   SQL connections (global): (none) · Enter edits sql.json\n  · ComfyUI\n  ·   ComfyUI tools: on\n  ·   ComfyUI URL: (not set)\n  ·   ComfyUI workflows offered: all (not narrowed)\n  ·   ComfyUI add workflow: Enter to start workflow wizard\n  ·   ComfyUI ^-mention enabled: on\n  ·   ComfyUI timeout (s): 300\n  ·   ComfyUI max pictures per call: 5 pictures\n  ·   ComfyUI reinforce negatives: on\n  ·   ComfyUI show prompts: on\n  ·   ComfyUI picture strip: on\n  ·   ComfyUI output folder: comfy_images\n  · Options\n  ·   $-mention enabled: on\n  ·   Tool collapse count: 2 lines\n  ·   Code collapse count: 20 lines\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>
    /// The allowed-commands list straight (later still on 2026-09-21, /cmdlist and the toolbar's lock):
    /// the same list under the same crumb as the Shell tab's row, Enter removing, ESC closing the
    /// pane with the crumb's root put back — the Tools tabs never drawn.
    /// </summary>
    [Fact]
    public async Task ShowAllowedCommands_OpensTheListUnderTheToolsCrumb_EnterRemoves_EscClosesThePane()
    {
        _settings.Update(d => d.ShellCommandAllowed = ["git push", "dotnet build"]);
        var (menu, pane, settings) = PaneMenu();
        Push(Keys.Enter, Keys.Escape);   // dotnet build removed, then the pane closed

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal(["git push"], _settings.Current.ShellCommandAllowed);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell allowed commands") + "\n \n▸ dotnet build\n  git push\n", _console.Output);
        Assert.Contains("  · Shell allowed commands: dotnet build removed\n▸ git push\n", _console.Output);
        Assert.DoesNotContain(Strip, _console.Output);
        Assert.Equal(SettingsMenu.Title, settings.Root);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task ShowAllowedCommands_WithoutThePane_PrintsTheListAsLines()
    {
        _settings.Update(d => d.ShellCommandAllowed = ["git push", "dotnet build"]);
        _paneOn = false;
        var pane = new ScreenPane(_console, geometry: null, _time);
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var settings = new SettingsMenu(_console, _settings, _ => null, new InputLine(_console, keys), new TranscriptRenderer(_console), _speech, menuPane, _ => FakeBrowserPath);
        var menu = new ToolsMenu(Facts, _settings, settings, new TranscriptRenderer(_console), menuPane);

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);
        _settings.Update(d => d.ShellCommandAllowed = []);
        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Contains("  · Shell allowed commands\n  ·   dotnet build\n  ·   git push\n", _console.Output);
        Assert.Contains("  · Shell allowed commands\n  ·   " + SettingsMenu.NoAllowedCommandsRow + "\n", _console.Output);
        Assert.Equal(["Shell allowed commands", "  dotnet build", "  git push"], ToolsMenu.AllowedCommandLines(new AppSettingsData { ShellCommandAllowed = ["git push", "dotnet build"] }));
        Assert.Equal(["Shell allowed commands", "  " + SettingsMenu.NoAllowedCommandsRow], ToolsMenu.AllowedCommandLines(new AppSettingsData()));
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    // ── The ComfyUI tab (Images until later on 2026-09-24): workflows offered and the add-workflow wizard (later on 2026-09-24) ─

    private const string ComfyServer = "http://comfy.lan:8188";

    private static void ComfyWorkflowFile(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".json"), "{\"6\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"{{prompt}}\"}}}");
    }

    /// <summary>A ComfyUI stand-in: two checkpoints, three samplers, two schedulers, and a job that finishes at once.</summary>
    private (NeonSidekick.Comfy.ComfyClient Client, StubHttpMessageHandler Stub) ComfyStub()
    {
        var stub = new StubHttpMessageHandler()
            .Map(ComfyServer + "/object_info/CheckpointLoaderSimple", System.Net.HttpStatusCode.OK, "{\"CheckpointLoaderSimple\":{\"input\":{\"required\":{\"ckpt_name\":[[\"juggernautXL_ragnarok.safetensors\",\"ponyDiffusionV6XL.safetensors\"]]}}}}")
            .Map(ComfyServer + "/object_info/KSampler", System.Net.HttpStatusCode.OK, "{\"KSampler\":{\"input\":{\"required\":{\"sampler_name\":[[\"euler\",\"euler_ancestral\",\"dpmpp_2m_sde\"]],\"scheduler\":[[\"normal\",\"karras\"]]}}}}")
            .Map(ComfyServer + "/prompt", System.Net.HttpStatusCode.OK, "{\"prompt_id\":\"t-1\"}")
            .Map(ComfyServer + "/history/", System.Net.HttpStatusCode.OK, "{\"t-1\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"neon_00001_.png\",\"subfolder\":\"neon\",\"type\":\"output\"}]}}}}")
            .Map(ComfyServer + "/view", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, [1, 2, 3], "image/png")));
        return (new NeonSidekick.Comfy.ComfyClient(new Uri(ComfyServer), new HttpClient(stub), TimeSpan.FromMilliseconds(1)), stub);
    }

    /// <summary>Offered → Options → ComfyUI, then the row: 2 the offered checklist, 3 the add-workflow wizard.</summary>
    private void OpenImagesRow(int row) => Push([Keys.Left, Keys.Left, .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    [Fact]
    public async Task OnThePane_TheComfyUITab_NarrowsTheOfferedWorkflows_AndANewOneStartsHidden()
    {
        ComfyWorkflowFile(_settings.ProfileComfyDirectory, "pony-txt2img");
        ComfyWorkflowFile(_settings.GlobalComfyDirectory, "juggernaut-xl");
        var (menu, _, _) = PaneMenu();
        OpenImagesRow(2);
        Push(Keys.Enter);                                    // juggernaut-xl off (the list is by name)
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["pony-txt2img"], _settings.Current.ComfyWorkflowsOffered);
        Assert.Matches(@"ComfyUI workflows offered +all \(not narrowed\)", _console.Output);   // the column is the tab's widest label
        Assert.Matches(@"ComfyUI workflows offered +1 of 2", _console.Output);
        Assert.Contains("[ ] juggernaut-xl  ", _console.Output);

        ComfyWorkflowFile(_settings.ProfileComfyDirectory, "new-one");
        var installed = new NeonSidekick.Comfy.ComfyWorkflowCatalog(() => [_settings.ProfileComfyDirectory, _settings.GlobalComfyDirectory]).Workflows;
        Assert.Equal("1 of 3", SettingsMenu.ComfyOfferedValue(_settings.Current.ComfyWorkflowsOffered, installed));   // the new one hidden until ticked
        Assert.Equal(SettingsMenu.ComfyNotNarrowedLabel, SettingsMenu.ComfyOfferedValue(null, installed));
        Assert.Equal("none of 3", SettingsMenu.ComfyOfferedValue([], installed));
    }

    /// <summary>The wizard's build path: the server's checkpoints, the family guessed from the pick, Pony's CLIP skip and sampler as the defaults, a small test run, then both files saved.</summary>
    [Fact]
    public async Task OnThePane_TheComfyWizard_BuildsFromTheServer_TestsIt_AndSavesIt()
    {
        var (client, stub) = ComfyStub();
        var (menu, _, _) = PaneMenu(comfy: () => client);
        OpenImagesRow(3);
        Push(Keys.Enter);                     // build
        Push(Keys.Enter);                     // text → image
        Push(Keys.Down, Keys.Enter);          // ponyDiffusionV6XL: family pony, name ponydiffusionv6xl
        Push(Keys.Enter);                     // pony (the guess)
        Push(Keys.Enter);                     // this profile
        Push(Keys.Enter);                     // the suggested name
        Push(Keys.Enter);                     // CLIP skip 2 (pony's)
        Push(Keys.Enter);                     // euler_ancestral (pony's)
        Push(Keys.Enter);                     // normal
        Push(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);   // 1024, 1024, 25, 7
        Push(Keys.Enter);                     // pony's negative
        Type("anime portraits");
        Push(Keys.Down, Keys.Enter);          // Test
        Push(Keys.Up, Keys.Enter);            // Save
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string json = Path.Combine(_settings.ProfileComfyDirectory, "ponydiffusionv6xl.json");
        Assert.True(NeonSidekick.Comfy.ComfyWorkflow.TryLoad(json, out var workflow, out string? problem), problem);
        Assert.Equal(NeonSidekick.Comfy.ComfyFamily.Pony, workflow!.Family);
        Assert.Equal("anime portraits", workflow.Description);
        Assert.Equal(NeonSidekick.Comfy.ComfyFamilies.PonyNegative, workflow.Defaults.Negative);
        string text = File.ReadAllText(json);
        Assert.Contains("\"CLIPSetLastLayer\"", text);
        Assert.Contains("\"euler_ancestral\"", text);
        Assert.Contains("\"ponyDiffusionV6XL.safetensors\"", text);
        string queued = stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!;
        Assert.Contains("\"width\":512", queued);   // the test is small
        Assert.Contains("\"steps\":8", queued);
        Assert.Contains("Tested 'ponydiffusionv6xl' in ", _console.Output);
        Assert.Contains("Added workflow 'ponydiffusionv6xl' to ", _console.Output);
        Assert.Null(_settings.Current.ComfyWorkflowsOffered);
    }

    /// <summary>The import path: an exported graph placeholdered, its own values the defaults, saved on a narrowed profile and offered to the model.</summary>
    [Fact]
    public async Task OnThePane_TheComfyWizard_ImportsAnExport_AndOffersIt()
    {
        _settings.Update(d => d.ComfyWorkflowsOffered = ["other"]);
        string export = Path.Combine(_dir, "My Export.json");
        File.WriteAllText(export, ComfyTests.Export);
        var (menu, _, _) = PaneMenu();
        OpenImagesRow(3);
        Push(Keys.Down, Keys.Enter);          // import
        Type(export);
        Push(Keys.Enter);                     // pony (guessed from the checkpoint)
        Push(Keys.Down, Keys.Enter);          // the home's folder
        Push(Keys.Enter);                     // my-export
        Push(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);   // 832, 1216, 20, 6.5 from the export
        Push(Keys.Enter);                     // its own negative
        Push(Keys.Enter);                     // no description
        Push(Keys.Enter);                     // Save, and offer it
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string json = Path.Combine(_settings.GlobalComfyDirectory, "my-export.json");
        Assert.True(NeonSidekick.Comfy.ComfyWorkflow.TryLoad(json, out var workflow, out string? problem), problem);
        Assert.Equal(new NeonSidekick.Comfy.ComfyDefaults(832, 1216, 20, 6.5, "score_4, blurry"), workflow!.Defaults);
        Assert.Contains("Found {{prompt}} → node 6 CLIPTextEncode.text", _console.Output);
        Assert.Equal(["other", "my-export"], _settings.Current.ComfyWorkflowsOffered);
        Assert.Contains(SettingsMenu.ComfyWizardSaveHiddenRow[..40], _console.Output);
    }

    /// <summary>ESC out of the first page writes nothing; a name already in a comfy folder is refused.</summary>
    [Fact]
    public async Task OnThePane_TheComfyWizard_EscWritesNothing_AndATakenNameIsRefused()
    {
        ComfyWorkflowFile(_settings.GlobalComfyDirectory, "juggernautxl_ragnarok");
        var (client, _) = ComfyStub();
        var (menu, _, _) = PaneMenu(comfy: () => client);
        OpenImagesRow(3);
        Push(Keys.Escape);                    // out of the first page: cancelled
        Push(Keys.Enter);                     // the wizard again
        Push(Keys.Enter, Keys.Enter);         // build, text → image
        Push(Keys.Enter);                     // juggernautXL_ragnarok: its name is taken
        Push(Keys.Enter, Keys.Enter);         // sdxl, this profile
        Push(Keys.Enter);                     // the suggested name: refused, asked again
        Push(Keys.Escape, Keys.Escape, Keys.Escape, Keys.Escape, Keys.Escape, Keys.Escape);   // back out page by page
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.ComfyWizardCancelledNotice, _console.Output);
        Assert.Contains("a workflow named 'juggernautxl_ragnarok' is already in", _console.Output);
        Assert.Equal(["juggernautxl_ragnarok.json"], Directory.GetFiles(_settings.GlobalComfyDirectory).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(_settings.ProfileComfyDirectory));
    }
}
