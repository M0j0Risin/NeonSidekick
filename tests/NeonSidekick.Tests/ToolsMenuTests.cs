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
public partial class ToolsMenuTests : IDisposable
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
        _console.Profile.Width = 220;   // 190 until 2026-10-04, when "ClaudeCLI", "Screen" and "SQLite" took the strip to 196 cells (220 leaves room for Postgres); 180 until 2026-10-02, when "Docker" took the strip past it; 170 until later on 2026-09-30, when "MySQL" took the strip to 170 cells; 160 until 2026-09-30, when "Oracle" took the strip to 161 cells; 150 until later on 2026-09-28, when "Print" took the strip to 151 cells; 130 until 2026-09-28, when "Home Assistant" took the strip to 142 cells; 120 until later on 2026-09-27, when "Claude (CLI)" took the strip to 124 cells (the user's call: it takes a second strip row, lined up under Offered, below 128 columns since the tab strip's own layout on the same day); 100 until 2026-09-24, when the Images tab took the ten-tab strip to 107 cells
        _settings = new AppSettings(_dir);
        _settings.Update(d => { d.TtsOutput = true; d.TtsSource = "http"; d.ToolsDisabled = []; d.GitLibTools = true; });   // delete off by default (2026-09-20), GitLib tools off by default (2026-09-21): the Offered-tab scripts start from every tool on
        // Eight settings went off by default on 2026-09-29 (the user's call): the scripts here were written with every tool group
        // offered, the shell under ask and the local scan, so the fixture puts them back; the fresh-profile tests start from new ones.
        _settings.Update(PreFlipDefaults.Apply);
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
    private (ToolsMenu Menu, ScreenPane Pane, SettingsMenu Settings) PaneMenu(Func<string, CancellationToken, Task<string?>>? browseVault = null, Func<NeonSidekick.Sql.SqlNamedConnection, CancellationToken, Task<NeonSidekick.Sql.SqlRun>>? testSql = null, Func<NeonSidekick.Comfy.ComfyClient?>? comfy = null, Func<NeonSidekick.Oracle.OracleNamedConnection, CancellationToken, Task<NeonSidekick.Sql.SqlRun>>? testOracle = null, Func<NeonSidekick.MySql.MySqlNamedConnection, CancellationToken, Task<NeonSidekick.Sql.SqlRun>>? testMySql = null, Func<NeonSidekick.Unc.UncNamedShare, CancellationToken, Task<NeonSidekick.Unc.UncResult<int>>>? testUnc = null)
    {
        _console.Profile.Height = 40;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null), new ManualTimeProvider()) { Hint = () => "idle" };
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var settings = new SettingsMenu(new ConsoleWithInput(pane, keys), _settings, _ => null, new InputLine(pane, keys), new TranscriptRenderer(pane), _speech, menuPane, _ => FakeBrowserPath, browseVault: browseVault, testSqlConnection: testSql, comfyClient: comfy, testOracleConnection: testOracle, testMySqlConnection: testMySql, testUncShare: testUnc);
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

    /// <summary>A tab's rows, then the blank rows that hold every tab at its pane's tallest tab's height (2026-10-01), then the rule under the list.</summary>
    private void AssertTabEnds(string rows, int width) =>
        Assert.Matches(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(rows) + "(?: \n)*" + System.Text.RegularExpressions.Regex.Escape(Rule(width))), _console.Output);

    /// <summary>A title or strip row as the pane prints it: the text, then the × close glyph in column width − 2.</summary>
    // The pane's own rule (ScreenPane.Draw): a first row with no room for the gap and the glyph goes without. The nine-tab
    // strip (SQL, 2026-09-23) is 97 cells, so on the fixture's 100 columns the strip row carries no ×; a narrower title still does.
    private string Titled(string row) =>
        TextCells.Width(row) + ScreenPane.TrailerGap + TextCells.Width(ScreenPane.CloseGlyph) > _console.Profile.Width - 1
            ? row
            : row + new string(' ', _console.Profile.Width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    /// <summary>The strip as the pane prints it: the label, then every tab title with a space either side, two spaces between. Pinned.</summary>
    private const string Strip = ToolsText.Label + "   Offered    Ask    Web    Shell    Files    UNC    Print    Camera    Screen    Obsidian    SQL    MySQL    SQLite    Postgres    Oracle    ClaudeCLI    Docker    HA    ComfyUI    GitLib    Options ";   // the user's order since 2026-10-03; before it Camera after Ask and Docker after UNC since 2026-10-02; HA second to last since later on 2026-10-01 (the user's ask); Print since later on 2026-09-28, Home Assistant since 2026-09-28, Images since 2026-09-24, SQL since 2026-09-23, Obsidian since 2026-09-22, Options last since later that day (second from later on 2026-09-19); the user's order (Web, Files, Shell, Ask, Git (native)) since later on 2026-09-21, alphabetical before

    /// <summary>The rows of the settings tab titled <paramref name="title"/>: <see cref="SettingsMenu.ToolsTabFields"/> one down from <see cref="ToolsText.TabTitles"/>, so a pin follows the tab, not its place (2026-10-03).</summary>
    internal static IReadOnlyList<SettingsField> TabFields(string title) => SettingsMenu.ToolsTabFields[TabIndex(title) - 1];

    /// <summary>Where <paramref name="title"/> sits on the strip.</summary>
    internal static int TabIndex(string title) => ToolsText.TabTitles.ToList().IndexOf(title) is var i and >= 0 ? i : throw new ArgumentException("no tab " + title, nameof(title));

    /// <summary>The Right presses that walk the strip from Offered to <paramref name="title"/>.</summary>
    private static ConsoleKeyInfo[] ToTab(string title) => Enumerable.Repeat(Keys.Right, TabIndex(title)).ToArray();

    /// <summary>A tool row as the pane prints it at width 120 (the markup rendered): the name padded to 22, the state to 5, then the description, cut to 119 cells and an ellipsis (FittedMarkup; every description is longer).</summary>
    private string Row(string name, bool on, string mark = "  ") => Fitted(mark + name.PadRight(22) + (on ? "on" : "off").PadRight(5) + Description(name, on));

    /// <summary>A heading row as the pane prints it (2026-10-03): <paramref name="text"/> (<c>── Clock · 3</c>), a space and the rule to the fixture's width.</summary>
    private string Heading(string text) => text + " " + Rule(_console.Profile.Width - TextCells.Width(text) - 1);

    private static string Fitted(string row) => row.Length <= 220 ? row : row[..219] + "…";   // the fixture's width (190 until 2026-10-04; 180 until the Docker tab, 2026-10-02; 170 until the MySQL tab, 160 until the Oracle tab, 2026-09-30; 150 until the Print tab, later on 2026-09-28)

    /// <summary>
    /// The description, and — on a row the turn would not offer — its note after two spaces, as <see cref="ToolsText.OfferedRows"/> draws it
    /// (in view since the fixture's 150 columns, 2026-09-28; 160 since the Print tab later that day). The switched-off note only on a row drawn <paramref name="on"/> false: the facts
    /// are read at the assertion, after the flip, and a row drawn before it had none.
    /// </summary>
    private string Description(string name, bool on = true)
    {
        var facts = Facts();
        var group = facts.Groups.Single(g => g.Tools.Any(t => t.Name == name));
        bool offered = facts.ToolsEnabled && group.Offers(name);
        string note = !offered && group.ToolNotes.TryGetValue(name, out var why) && (!on || !why.Contains(SystemPromptSummary.DisabledSuffix, StringComparison.Ordinal)) ? "  " + why : "";
        return group.Tools.Single(t => t.Name == name).Description + note;
    }

    [Fact]
    public void Labels_ArePinned()
    {
        // The three tabs that left /settings (2026-09-19): their rows unchanged, every field on exactly one tab of the three panes (Skills left for /skills later that day);
        // the Options tab ahead of them (later on 2026-09-19): the pane's own $-mention switch.
        Assert.Equal(10, SettingsMenu.TabFields.Count);   // /settings' Claude and OpenAI tabs since 2026-10-03; Docker tab since 2026-10-02; the Claude (API) tab went to /tools' Claude tab on 2026-09-29; Embedded model since 2026-09-29; Claude (API) since later on 2026-09-27; Claude on 2026-09-27 until later that day (to /tools); Botchat since 2026-09-25
        Assert.Equal(20, SettingsMenu.ToolsTabFields.Count);   // Screen, SQLite and Postgres 2026-10-04; Camera and Docker 2026-10-02; UNC later still on 2026-09-30; MySQL and Oracle since 2026-09-30; Print since later on 2026-09-28; Home Assistant since 2026-09-28; Claude since 2026-09-27; Images since 2026-09-24; SQL since 2026-09-23   // Obsidian since 2026-09-22   // Git since 2026-09-20, Shell since 2026-09-21; the user's order (Web, Files, Shell, Ask, Git (native)) since later on 2026-09-21, alphabetical before
        Assert.Equal(["Offered", "Ask", "Web", "Shell", "Files", "UNC", "Print", "Camera", "Screen", "Obsidian", "SQL", "MySQL", "SQLite", "Postgres", "Oracle", "ClaudeCLI", "Docker", "HA", "ComfyUI", "GitLib", "Options"], ToolsText.TabTitles);   // the user's order since 2026-10-03 (ClaudeCLI "Claude" until 2026-10-04)
        Assert.Equal([SettingsField.ToolsDollarMention, SettingsField.ToolCollapseCount, SettingsField.CodeCollapseCount, SettingsField.ShowFileDiffs, SettingsField.DiffMaxLines], TabFields(ToolsText.OptionsTabTitle));
        Assert.Equal([SettingsField.HomeAssistantTools, SettingsField.HomeAssistantUrl, SettingsField.HomeAssistantToken, SettingsField.HomeAssistantTest, SettingsField.HomeAssistantActionPolicy, SettingsField.HomeAssistantAssistAgent, SettingsField.HomeAssistantTimeoutSeconds], TabFields(ToolsText.HomeAssistantTabTitle));   // the switch, the server and its token, the test, the policy, Assist's agent, the timeout (2026-09-28)   // the fold's count under the switch (2026-09-22, the user's place), the code fold's under it
        Assert.Equal([SettingsField.PrintTools, SettingsField.PrintActionPolicy, SettingsField.PrintDefaultPrinter, SettingsField.PrintFontSize, SettingsField.PdfEngine], TabFields(ToolsText.PrintTabTitle));   // the switch, the policy, the printer, the size (later on 2026-09-28); the PDF engine (2026-10-03)
        Assert.Equal([SettingsField.WebTools, SettingsField.WebBrowserMode, SettingsField.WebBrowserPath, SettingsField.WebBrowserNetworkMode, SettingsField.WebSearchMethod, SettingsField.WebSearxngUrl, SettingsField.WebSearchMaxResults, SettingsField.WebDownloadMaxMegabytes], TabFields(ToolsText.WebTabTitle));   // the download cap last (2026-10-01)
        Assert.Equal([SettingsField.FileTools, SettingsField.FileTreeMaxLength, SettingsField.FileTreeShowSizes, SettingsField.FileMentionFolderMode, SettingsField.FileBrowserMode, SettingsField.FileViewImageMaxPerCall, SettingsField.FileSearchMaxResults], TabFields(ToolsText.FilesTabTitle));   // the search cap last (2026-10-01), the view_image cap before it (2026-09-19); the browser mode under the folder mode, 2026-09-21
        Assert.Equal([SettingsField.ShellCommandPolicy, SettingsField.ShellCommandAllowed, SettingsField.ShellPoliceOutsidePaths, SettingsField.ShellPoliceForbiddenStrings, SettingsField.ShellPreferNative, SettingsField.ShellDefault, SettingsField.ShellTimeoutSeconds, SettingsField.ShellForegroundCapSeconds, SettingsField.ShellOutputMaxChars, SettingsField.ShellCodeLanguages, SettingsField.ShellCodeTimeoutSeconds, SettingsField.ShellToolBridge, SettingsField.ShellCodeMaxToolCalls], TabFields(ToolsText.ShellTabTitle));   // the policy (the switch) first, then the list, the shell, the caps, then execute_code's four (2026-09-21; the bridge switch later that day; the police toggle third, 2026-09-22; prefer native under it, 2026-09-26; the forbidden strings under the police, 2026-10-03)
        Assert.Equal([SettingsField.AskUser, SettingsField.AskMaxQuestions, SettingsField.AskMaxChoices], TabFields(ToolsText.AskTabTitle));
        Assert.Equal([SettingsField.GitLibTools, SettingsField.GitLibDiffMaxLines, SettingsField.GitLibLogMaxCommits, SettingsField.GitLibEmail, SettingsField.GitLibName], TabFields(ToolsText.GitTabTitle));   // the switch first, then the limits, then the identity pair (2026-09-21); the GitLib labels later that day
        Assert.Equal([SettingsField.ObsidianTools, SettingsField.ObsidianVault, SettingsField.ObsidianAllowDelete], TabFields(ToolsText.ObsidianTabTitle));   // the switch, then the vault (2026-09-22), then the delete switch (later that day)
        Assert.Equal([SettingsField.SqlTools, SettingsField.SqlConnectionsOffered, SettingsField.SqlDefaultConnection, SettingsField.SqlSetPassword, SettingsField.SqlAddConnection, SettingsField.SqlPercentMention, SettingsField.SqlQueryMaxRows, SettingsField.SqlQueryTimeoutSeconds, SettingsField.QueryResultMaxChars, SettingsField.SqlConnectionsProfile, SettingsField.SqlConnectionsGlobal], TabFields(ToolsText.SqlTabTitle));   // the switch, the offered list (later that day), the default, the password prompt, the add-connection wizard and the %-mention switch (later that day), the two caps, the three engines' text cap (2026-10-01), the two edit rows (2026-09-23)
        Assert.Equal([SettingsField.OracleTools, SettingsField.OracleConnectionsOffered, SettingsField.OracleDefaultConnection, SettingsField.OracleSetPassword, SettingsField.OracleAddConnection, SettingsField.OraclePercentMention, SettingsField.OracleQueryMaxRows, SettingsField.OracleQueryTimeoutSeconds, SettingsField.OracleConnectionsProfile, SettingsField.OracleConnectionsGlobal], TabFields(ToolsText.OracleTabTitle));   // the SQL tab's rows, in its order (2026-09-30)
        Assert.Equal([SettingsField.MySqlTools, SettingsField.MySqlConnectionsOffered, SettingsField.MySqlDefaultConnection, SettingsField.MySqlSetPassword, SettingsField.MySqlAddConnection, SettingsField.MySqlPercentMention, SettingsField.MySqlQueryMaxRows, SettingsField.MySqlQueryTimeoutSeconds, SettingsField.MySqlConnectionsProfile, SettingsField.MySqlConnectionsGlobal], TabFields(ToolsText.MySqlTabTitle));   // the Oracle tab's rows, in its order (later on 2026-09-30)
        Assert.Equal([SettingsField.UncTools, SettingsField.UncWrites, SettingsField.UncSharesOffered, SettingsField.UncDefaultShare, SettingsField.UncSetPassword, SettingsField.UncAddShare, SettingsField.UncStarMention, SettingsField.UncSharesProfile, SettingsField.UncSharesGlobal], TabFields(ToolsText.UncTabTitle));   // the two switches, the offered list, the default, the password prompt, the wizard, the %-mention switch, the two edit rows (later still on 2026-09-30)
        Assert.Equal([SettingsField.DockerTools, SettingsField.DockerWrites, SettingsField.DockerEnginePipe], TabFields(ToolsText.DockerTabTitle));   // the two switches, the pipe (2026-10-02)
        Assert.Equal(20, SettingsMenu.LabelWidthOf(TabFields(ToolsText.DockerTabTitle)));   // "Docker engine pipe"
        Assert.Equal(Enum.GetValues<SettingsField>().Order(), SettingsMenu.TabFields.Concat(SettingsMenu.SkillsTabFields).Concat(SettingsMenu.ToolsTabFields).Concat(SettingsMenu.McpTabFields).SelectMany(t => t).Order());
        Assert.Equal(21, SettingsMenu.LabelWidthOf(TabFields(ToolsText.OptionsTabTitle)));   // "Tool collapse count" (2026-09-22; "$-mention enabled", 19, before)
        Assert.Equal(26, SettingsMenu.LabelWidthOf(TabFields(ToolsText.WebTabTitle)));   // "Web browser network mode" (the Web-prefixed labels, later still on 2026-09-19; "Web search max results", 24, before)
        Assert.Equal(32, SettingsMenu.LabelWidthOf(TabFields(ToolsText.FilesTabTitle)));   // "File view image max (per call)" (later still on 2026-09-19; "Stale line number guard", 25, that morning; "Always return line numbers", 28, before)
        Assert.Equal(32, SettingsMenu.LabelWidthOf(TabFields(ToolsText.ShellTabTitle)));   // "Shell police forbidden strings" (2026-10-03; "Shell tool bridge max calls", 29, before) (the Shell tab, 2026-09-21; the row was "Shell code max tool calls", 27, until later that day)
        Assert.Equal(30, SettingsMenu.LabelWidthOf(TabFields(ToolsText.AskTabTitle)));   // "Ask max choices per question"
        Assert.Equal(24, SettingsMenu.LabelWidthOf(TabFields(ToolsText.GitTabTitle)));   // "GitLib log max commits" (2026-09-30; "Git native log max commits", 28, from later on 2026-09-21; "Git log max commits", 21, from 2026-09-20)
        Assert.Equal(28, SettingsMenu.LabelWidthOf(TabFields(ToolsText.SqlTabTitle)));   // "SQL query result max chars" (later on 2026-10-03; "SQL connections (profile)", 27, from 2026-09-23)
        Assert.Equal(30, SettingsMenu.LabelWidthOf(TabFields(ToolsText.OracleTabTitle)));   // "Oracle connections (profile)" (2026-09-30)
        Assert.Equal(29, SettingsMenu.LabelWidthOf(TabFields(ToolsText.MySqlTabTitle)));   // "MySQL connections (profile)" (later on 2026-09-30)
        Assert.Equal(23, SettingsMenu.LabelWidthOf(TabFields(ToolsText.UncTabTitle)));   // "UNC %-mention enabled" (later still on 2026-09-30)
        Assert.Equal(32, SettingsMenu.LabelWidthOf(TabFields(ToolsText.ObsidianTabTitle)));   // "Obsidian allow delete (.trash)" (2026-09-23; "Obsidian allow delete" from later on 2026-09-22, "Obsidian tools" that morning)
        // None refused under a reply since 2026-10-03, when the Anthropic API's four reconnect rows (on the Claude tab since 2026-09-29) and the Claude CLI server's (2026-09-30) went to /settings' Claude tab.
        Assert.DoesNotContain(SettingsMenu.ToolsTabFields.SelectMany(t => t), SettingsMenu.RefusedMidTurn);
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
        // The headings are rules with a gap before each but the first (2026-10-03).
        Assert.Contains("\n" + Titled(Strip) + "\n \n" + Heading("── Clock · 3") + "\n" + Row(GetCurrentTimeTool.ToolName, true, "▸ ") + "\n" + Row(ShiftDateTool.ToolName, true) + "\n" + Row(DaysBetweenTool.ToolName, true) + "\n  \n" + Heading("── Timers · 3") + "\n", _console.Output);
        Assert.Contains("\n" + ToolsText.OfferedKeys + "\n", _console.Output);
        Assert.Contains("\n" + Heading("── Files · 14") + "\n" + Row(GetWorkingDirectoryTool.ToolName, true) + "\n", _console.Output);
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
        Assert.Contains("\n" + Titled(Strip) + "\n  · shift_date: off\n" + Heading("── Clock · 2 of 3") + "\n" + Row(GetCurrentTimeTool.ToolName, true) + "\n" + Row(ShiftDateTool.ToolName, false, "▸ ") + "\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · shift_date: on\n" + Heading("── Clock · 3") + "\n" + Row(GetCurrentTimeTool.ToolName, true) + "\n" + Row(ShiftDateTool.ToolName, true, "▸ ") + "\n", _console.Output);
        Assert.Equal(0, pane.FlowRow);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TypingFiltersTheOfferedRows_EnterFlipsTheToolShown_EscClearsTheFilter_ThenCloses()
    {
        // 2026-10-03 (the user's ask): the typed text narrows the tab (the caption says how far), the cursor on the first tool
        // left; Enter flips that tool; the first ESC brings every row back, the second closes.
        var (menu, pane, _) = PaneMenu();
        Push("shift_d".Select(Keys.Char).ToArray());
        Push(Keys.Enter);                         // shift_date: off
        Push(Keys.Escape);                        // the filter cleared
        Push(Keys.Escape);                        // closed

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["shift_date"], _settings.Current.ToolsDisabled);
        int total = Facts().Groups.Sum(g => g.Tools.Count);
        Assert.Contains(MenuFilter.Caption("shift_d", 1, total), _console.Output);
        Assert.Contains("\n" + Heading("── Clock · 3") + "\n" + Row(ShiftDateTool.ToolName, true, "▸ ") + "\n", _console.Output);   // the one tool under its heading
        Assert.Contains("\n" + Heading("── Clock · 2 of 3") + "\n" + Row(ShiftDateTool.ToolName, false, "▸ ") + "\n", _console.Output);
        Assert.Contains(MenuFilter.Hint(ToolsText.OfferedKeys, "shift_d"), _console.Output);
        // Cleared: the whole tab again, the cursor back on the first tool.
        Assert.Contains("\n" + Heading("── Clock · 2 of 3") + "\n" + Row(GetCurrentTimeTool.ToolName, true, "▸ ") + "\n" + Row(ShiftDateTool.ToolName, false) + "\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ASettingsTab_TakesNoFilter()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.OptionsTabTitle));
        Push("zz".Select(Keys.Char).ToArray());
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.DoesNotContain(MenuFilter.Caption("z", 0, Facts().Groups.Sum(g => g.Tools.Count)), _console.Output);
        Assert.DoesNotContain(MenuFilter.NoMatchLine("z"), _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheCursorNeverRestsOnAHeadingOrAGap()
    {
        // A heading is no stop (2026-10-03): Up from the first tool wraps past the Clock heading to the last tool, Down from
        // the last clock row steps over the gap and the Timers heading, Home is the first tool again.
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Up, Keys.Enter);                // ask_user, the last row
        Push(Keys.Home, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // start_timer
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([AskUserTool.ToolName, StartTimerTool.ToolName], _settings.Current.ToolsDisabled);
        Assert.DoesNotContain("▸ ──", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AGroupOff_ShowsDimWithTheSwitchNamed_AndStillFlips()
    {
        _settings.Update(d => d.FileTools = false);
        var (menu, pane, _) = PaneMenu();
        Down(6);                                  // past the two other clock rows and the three timers (the gaps and headings no stops, 2026-10-03): get_working_directory
        Push(Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([GetWorkingDirectoryTool.ToolName], _settings.Current.ToolsDisabled);
        Assert.Contains("\n" + Heading("── Files · 14 ── off: File tools is off") + "\n", _console.Output);
        Assert.Contains("  · get_working_directory: off\n", _console.Output);
        Assert.Contains("\n" + Heading("── Files · 13 of 14 ── off: File tools is off") + "\n" + Row(GetWorkingDirectoryTool.ToolName, false, "▸ ") + "\n", _console.Output);
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
        Assert.Contains("\n" + Titled(Strip) + "\n \n" + Fitted("  " + ToolsText.OffLine) + "\n" + Heading("── Clock · 3") + "\n" + Row(GetCurrentTimeTool.ToolName, true, "▸ ") + "\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · get_current_time: off\n" + Fitted("  " + ToolsText.OffLine) + "\n" + Heading("── Clock · 2 of 3") + "\n" + Row(GetCurrentTimeTool.ToolName, false, "▸ ") + "\n", _console.Output);
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
        AssertTabEnds("\n" + Titled(Strip) + "\n \n▸ $-mention enabled    on\n  Tool collapse count  2 lines\n  Code collapse count  20 lines\n  Show file diffs      on\n  Diff max lines       10 lines\n", 100);
        Assert.Contains(ToolsText.Label + " › $-mention enabled", _console.Output);
        Assert.Contains("$ is ordinary text", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · $-mention enabled: off\n▸ $-mention enabled    off\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheAskTab_SitsRightAfterOffered_ItsToggleSaves()
    {
        // The user's order since 2026-10-03: Offered, Ask, Web, Shell, Files, … (between Shell and Git (native) from later on 2026-09-21).
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right);                                       // Ask
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // Ask user: the page, off picked
        Push(Keys.Right, Keys.Right, Keys.Right);               // Web, Shell, Files
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.AskUser);
        // The three rows padded to the tab's own column (30), the toggle's notice on the status line under the strip.
        AssertTabEnds("\n" + Titled(Strip) + "\n \n▸ Ask user                      on\n  Ask max questions             10 questions\n  Ask max choices per question  10 choices\n", 100);
        Assert.Contains("\n" + Titled(Strip) + "\n  · Ask user: off\n▸ Ask user                      off\n", _console.Output);
        Assert.Contains("\n▸ File tools                      on\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheFilesTab_SitsBetweenShellAndUnc()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.FilesTabTitle));                   // Ask, Web, Shell, Files (the user's order since 2026-10-03)
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // File tools: the page, off picked
        Push(Keys.Left, Keys.Left, Keys.Left, Keys.Left, Keys.Left, Keys.Left);   // Shell, Web, Ask, Offered, Options (the strip wraps), GitLib
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.FileTools);
        // The six rows (the view_image cap last, 2026-09-19; the @-mention folder mode before it, 2026-09-17, and the browser mode under that, 2026-09-21; Safe edits gone since 2026-10-01, folder-remain the default since then, Always return line numbers gone later that day and the stale line number guard later still, with edit_lines) padded to the tab's own column (32), the toggle's notice on the status line under the strip.
        AssertTabEnds("\n" + Titled(Strip) + "\n \n▸ File tools                      on\n  File /tree max length           500 entries\n  File /tree show sizes           on\n  File @-mention folder mode      folder-remain\n  File browser/tree mode          default\n  File view image max (per call)  10 pictures\n  File search max results         200 results\n", 100);
        Assert.Contains("\n" + Titled(Strip) + "\n  · File tools: off\n▸ File tools                      off\n", _console.Output);
        Assert.Contains("\n▸ GitLib tools            on\n", _console.Output);
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
        Push([.. ToTab(ToolsText.ObsidianTabTitle), Keys.Down, Keys.Enter]);   // Obsidian, the vault row's typed slot
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
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Down, Keys.Down, Keys.Enter]);   // SQL, the set-password row (the third since the offered list): the pick
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

    /// <summary>The Oracle tab, row <paramref name="row"/> (2026-09-30): the switch 0, offered 1, default 2, password 3, wizard 4.</summary>
    private void OpenOracleRow(int row) => Push([.. ToTab(ToolsText.OracleTabTitle), .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    /// <summary>
    /// <c>Oracle add connection</c> (2026-09-30, the user's ask): a connection walked through every page into the profile's file,
    /// the test run over the unsaved draft (its password handed over plain, nothing written) naming who it signed in as and
    /// warning of an account that can write, then saved — the entry in the file, the password encrypted after it, never on screen.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheOracleWizard_WalksAConnection_TestsTheDraft_WarnsOfWrites_AndSavesIt()
    {
        string path = NeonSidekick.Oracle.OracleConfigFile.ProfilePath(_settings.ProfileDirectory);
        var tested = new List<(NeonSidekick.Oracle.OracleNamedConnection Connection, bool FileThere)>();
        var (menu, _, _) = PaneMenu(testOracle: (c, _) =>
        {
            tested.Add((c, File.Exists(path)));
            return Task.FromResult(new NeonSidekick.Sql.SqlRun(NeonSidekick.Sql.SqlOutcome.Ok, "", c.Name, "NEON",
                [new NeonSidekick.Sql.SqlGrid(["user", "container", "version"], [["NEON", "FREEPDB1", "23.0.0.0.0"]], false), new NeonSidekick.Sql.SqlGrid(["what"], [["CREATE TABLE"], ["owns 3 tables"]], false)], TimeSpan.Zero));
        });
        OpenOracleRow(4);
        Push(Keys.Enter);                         // the profile's file
        Type("free");
        Type("localhost:1521/FREEPDB1");
        Push(Keys.Enter);                         // no default schema: the user's own
        Type("sys");                              // refused: SYS is never held to a read-only transaction
        Push([.. Enumerable.Repeat(Keys.Backspace, 3)]);
        Type("neon");
        Push(Keys.Enter);                         // file
        Type("s3cret");
        Push(Keys.Enter);                         // the default timeout
        Type("the container");
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows, always both since 2026-10-01)
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var (connection, fileThere) = Assert.Single(tested);
        Assert.False(fileThere);
        Assert.Equal("s3cret", connection.Config.Password);
        var loaded = NeonSidekick.Oracle.OracleConfigFile.Load(path);
        var free = Assert.Single(loaded.Connections);
        Assert.Equal("free", free.Name);
        Assert.Equal("localhost:1521/FREEPDB1", free.Config.DataSource);
        Assert.Equal("neon", free.Config.User);
        Assert.Null(free.Config.Schema);
        Assert.Equal("the container", free.Config.Description);
        Assert.Equal("s3cret", NeonSidekick.Oracle.OracleSecrets.Resolve(free).Value);
        Assert.StartsWith(NeonSidekick.Oracle.OracleConfigFile.EmptyText[..40], File.ReadAllText(path));
        Assert.Contains("\"user\" is 'sys'; SYS", _console.Output);
        Assert.Contains(NeonSidekick.Oracle.OracleText.TestOk("free", "NEON", "FREEPDB1", "23.0.0.0.0"), _console.Output);
        Assert.Contains("This account can change data (CREATE TABLE, owns 3 tables)", _console.Output);
        Assert.Contains("Added 'free' to ", _console.Output);
        Assert.Contains(SettingsMenu.SqlWizardMasked, _console.Output);
        Assert.DoesNotContain("s3cret", _console.Output);
        Assert.Equal(["free"], _settings.Current.OracleConnectionsOffered);   // offered by the wizard into a list nothing was in
    }

    /// <summary>The wizard's ESC on its first page writes nothing; a bad schema asks again; a narrowed profile's first save row offers the new one.</summary>
    [Fact]
    public async Task OnThePane_TheOracleWizard_ChecksTheSchema_OffersIntoANarrowedProfile_AndEscWritesNothing()
    {
        _settings.Update(d => d.OracleConnectionsOffered = ["other"]);
        string home = NeonSidekick.Oracle.OracleConfigFile.GlobalPath(_settings.StorageDirectory);
        var (menu, _, _) = PaneMenu(testOracle: (c, _) => Task.FromResult(NeonSidekick.Sql.SqlRun.Refused(NeonSidekick.Sql.SqlOutcome.ConnectFailed, "ORA-12541: no listener", c.Name)));
        OpenOracleRow(4);
        Push(Keys.Escape);                        // the first page: nothing written
        Push(Keys.Enter);                         // again
        Push(Keys.Down, Keys.Enter);              // the home's file
        Type("ledger");
        Type("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=db)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=LEDGER)))");
        Type("not a name");                       // refused
        Push([.. Enumerable.Repeat(Keys.Backspace, 10)]);
        Type("ledger");
        Type("ledger_ro");
        Push(Keys.Down, Keys.Enter);              // credman
        Type("pw");
        Type("9");
        Push(Keys.Enter);                         // no description
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test: the failure on the status line, still saveable
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape);

        string target = "NeonSidekick/oracle/ledger";
        try
        {
            await menu.ShowAsync(CancellationToken.None);

            Assert.Contains(SettingsMenu.SqlWizardCancelledNotice, _console.Output);
            Assert.Contains(NeonSidekick.Oracle.OracleText.BadSchemaKey("not a name"), _console.Output);
            Assert.Contains("Error: could not connect to ledger: ORA-12541: no listener", _console.Output);
            var ledger = Assert.Single(NeonSidekick.Oracle.OracleConfigFile.Load(home).Connections);
            Assert.Equal("ledger", ledger.Config.Schema);
            Assert.True(ledger.Config.InCredentialManager);
            Assert.Null(ledger.Config.Password);
            Assert.Equal(9, ledger.Config.ConnectTimeoutSeconds);
            Assert.Equal("pw", NeonSidekick.Sql.WindowsCredentials.ReadGeneric(target).Value);
            Assert.Equal(["other", "ledger"], _settings.Current.OracleConnectionsOffered);
        }
        finally
        {
            NeonSidekick.Sql.WindowsCredentials.DeleteGeneric(target);
        }
    }

    [Fact]
    public async Task OnThePane_TheOracleTab_SetsAPassword_Masked_IntoTheConnectionsStore()
    {
        string path = NeonSidekick.Oracle.OracleConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(_settings.ProfileDirectory);
        File.WriteAllText(path, """{ "connections": { "free": { "dataSource": "x:1521/y", "user": "neon" } } }""");
        var (menu, _, _) = PaneMenu();
        OpenOracleRow(3);
        Push(Keys.Enter);                                               // free
        Push([.. "s3cret".Select(Keys.Char), Keys.Enter]);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var loaded = NeonSidekick.Oracle.OracleConfigFile.Load(path);
        Assert.Equal("s3cret", NeonSidekick.Oracle.OracleSecrets.Resolve(loaded.Connections[0]).Value);
        Assert.Contains("▸ " + SettingsMenu.OraclePasswordRow(loaded.Connections[0]), _console.Output);
        Assert.DoesNotContain("s3cret", _console.Output);
        Assert.Contains("Saved the password of 'free', encrypted, in ", _console.Output);
        Assert.Equal("free  (user neon · encrypted in oracle.json)", SettingsMenu.OraclePasswordRow(loaded.Connections[0]));
    }

    [Fact]
    public async Task OnThePane_TheOracleTab_NarrowsTheOfferedConnections_AndPicksTheDefault()
    {
        string path = NeonSidekick.Oracle.OracleConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(_settings.ProfileDirectory);
        File.WriteAllText(path, """{ "connections": { "free": { "dataSource": "x:1521/y", "user": "u" }, "ledger": { "dataSource": "z:1521/l", "user": "u" } } }""");
        var (menu, _, _) = PaneMenu();
        OpenOracleRow(1);
        Push(Keys.Down, Keys.Enter);                         // ledger on (nothing is ticked until the user ticks it)
        Push(Keys.Escape);
        Push(Keys.Down, Keys.Enter);                         // the default row: the pick
        Push(Keys.Down, Keys.Enter);                         // ledger (the first row is "the first connection")
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["ledger"], _settings.Current.OracleConnectionsOffered);
        Assert.Equal("ledger", _settings.Current.OracleDefaultConnection);
        Assert.Contains("Oracle connections offered    none of 2", _console.Output);
        Assert.Contains("[ ] free", _console.Output);
        Assert.Equal("1 of 2", SettingsMenu.OracleOfferedValue(_settings.Current.OracleConnectionsOffered, NeonSidekick.Oracle.OracleConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory)));
    }

    /// <summary>The MySQL tab, row <paramref name="row"/> (later on 2026-09-30): the switch 0, offered 1, default 2, password 3, wizard 4.</summary>
    private void OpenMySqlRow(int row) => Push([.. ToTab(ToolsText.MySqlTabTitle), .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    /// <summary>
    /// <c>MySQL add connection</c> (later on 2026-09-30): a connection walked through every page into the profile's file, a bad port
    /// asked again, the test run over the unsaved draft naming who it signed in as and warning from the grants of an account that
    /// can write, then saved — the entry in the file, the password encrypted after it, never on screen.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheMySqlWizard_WalksAConnection_TestsTheDraft_WarnsOfWrites_AndSavesIt()
    {
        string path = NeonSidekick.MySql.MySqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        var tested = new List<NeonSidekick.MySql.MySqlNamedConnection>();
        var (menu, _, _) = PaneMenu(testMySql: (c, _) =>
        {
            tested.Add(c);
            return Task.FromResult(new NeonSidekick.Sql.SqlRun(NeonSidekick.Sql.SqlOutcome.Ok, "", c.Name, "shop",
                [new NeonSidekick.Sql.SqlGrid(["user", "database", "version"], [["neon@%", "shop", "8.4.11"]], false),
                 new NeonSidekick.Sql.SqlGrid(["grants"], [["GRANT USAGE ON *.* TO `neon`@`%`"], ["GRANT ALL PRIVILEGES ON `shop`.* TO `neon`@`%`"]], false)], TimeSpan.Zero));
        });
        OpenMySqlRow(4);
        Push(Keys.Enter);                         // the profile's file
        Type("shop");
        Type("localhost");
        Type("99999");                            // refused: no port
        Push([.. Enumerable.Repeat(Keys.Backspace, 5)]);
        Type("3307");
        Type("shop");
        Type("neon");
        Push(Keys.Enter);                         // file
        Type("s3cret");
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // verify-full
        Push(Keys.Enter);                         // the default timeout
        Type("the sample shop");
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows, always both since 2026-10-01)
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("s3cret", Assert.Single(tested).Config.Password);
        var shop = Assert.Single(NeonSidekick.MySql.MySqlConfigFile.Load(path).Connections);
        Assert.Equal(("localhost", 3307, "shop", "neon", "verify-full"), (shop.Config.Host, shop.Config.Port, shop.Config.Database, shop.Config.User, shop.Config.SslMode));
        Assert.Equal("s3cret", NeonSidekick.MySql.MySqlSecrets.Resolve(shop).Value);
        Assert.Contains(SettingsMenu.MySqlWizardPortError("99999"), _console.Output);
        Assert.Contains(NeonSidekick.MySql.MySqlText.TestOk("shop", "neon@%", "8.4.11"), _console.Output);
        Assert.Contains("This account can change data (ALL PRIVILEGES ON `shop`.*)", _console.Output);
        Assert.Contains("Added 'shop' to ", _console.Output);
        Assert.Equal(["shop"], _settings.Current.MySqlConnectionsOffered);
        Assert.DoesNotContain("s3cret", _console.Output);
    }

    [Fact]
    public async Task OnThePane_TheMySqlTab_SetsAPassword_AndNarrowsTheOfferedConnections()
    {
        string path = NeonSidekick.MySql.MySqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(_settings.ProfileDirectory);
        File.WriteAllText(path, """{ "connections": { "shop": { "host": "localhost", "user": "reader" }, "billing": { "host": "db", "user": "ro" } } }""");
        var (menu, _, _) = PaneMenu();
        OpenMySqlRow(3);
        Push(Keys.Enter);                                    // shop
        Push([.. "s3cret".Select(Keys.Char), Keys.Enter]);
        Push(Keys.Up, Keys.Up, Keys.Enter);                  // the offered row: the checklist
        Push(Keys.Down, Keys.Enter);                         // billing on
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var loaded = NeonSidekick.MySql.MySqlConfigFile.Load(path);
        Assert.Equal("s3cret", NeonSidekick.MySql.MySqlSecrets.Resolve(loaded.Connections[0]).Value);
        Assert.Equal("shop  (localhost:3306, user reader · encrypted in mysql.json)", SettingsMenu.MySqlPasswordRow(loaded.Connections[0]));
        Assert.Equal(["billing"], _settings.Current.MySqlConnectionsOffered);
        Assert.DoesNotContain("s3cret", _console.Output);
    }

    /// <summary>Types <paramref name="text"/> into the open slot and submits it.</summary>
    private void Type(string text) => Push([.. text.Select(Keys.Char), Keys.Enter]);

    /// <summary>The UNC tab, row <paramref name="row"/> (later still on 2026-09-30): the switch 0, writes 1, offered 2, default 3, password 4, wizard 5.</summary>
    private void OpenUncRow(int row) => Push([.. ToTab(ToolsText.UncTabTitle), .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    /// <summary>
    /// <c>UNC add share</c> (later still on 2026-09-30): a share walked through every page into the profile's file — a relative path
    /// asked again, windows sign-in skipping the account pages, readwrite noted while UNC writes is off — the test listing the root
    /// of the unsaved draft, then saved, the defaults left out of the file.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheUncWizard_WalksAWindowsShare_TestsTheDraft_AndSavesIt()
    {
        string path = NeonSidekick.Unc.UncConfigFile.ProfilePath(_settings.ProfileDirectory);
        string folder = Path.Combine(_dir, "eng-share");
        Directory.CreateDirectory(folder);
        var tested = new List<NeonSidekick.Unc.UncNamedShare>();
        var (menu, _, _) = PaneMenu(testUnc: (s, _) =>
        {
            tested.Add(s);
            return Task.FromResult(NeonSidekick.Unc.UncResult<int>.Ok(7));
        });
        OpenUncRow(5);
        Push(Keys.Enter);                         // the profile's file
        Type("eng");
        Type(@"specs\only");                      // refused: not a full path
        Push([.. Enumerable.Repeat(Keys.Backspace, 10)]);
        Type(folder);
        Push(Keys.Enter);                         // windows: the account pages skipped
        Push(Keys.Down, Keys.Enter);              // readwrite, while UNC writes is off
        Type("engineering specs");
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows, always both since 2026-10-01)
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var draft = Assert.Single(tested);
        Assert.Equal(folder, draft.Config.Root);
        Assert.False(draft.Config.IsRunAs);
        var eng = Assert.Single(NeonSidekick.Unc.UncConfigFile.Load(path).Shares);
        Assert.Equal(("eng", folder, true, "engineering specs"), (eng.Name, eng.Config.Root, eng.Config.IsReadWrite, eng.Config.Description));
        Assert.Null(eng.Config.Auth);                        // windows is the default: left out of the entry
        Assert.Contains("\"access\": \"readwrite\"", File.ReadAllText(path));
        Assert.Contains(NeonSidekick.Unc.UncText.NotAbsolute(@"specs\only"), _console.Output);
        Assert.Contains(SettingsMenu.UncWizardWritesOffNotice, _console.Output);
        Assert.Contains(SettingsMenu.UncWizardTestOkNotice("eng", 7), _console.Output);
        Assert.Contains("Added 'eng' to ", _console.Output);
        Assert.Equal(["eng"], _settings.Current.UncSharesOffered);
        Assert.Equal("Reached 'eng': 7 entries at its root.", SettingsMenu.UncWizardTestOkNotice("eng", 7));
    }

    /// <summary><c>UNC add share</c> as another account: an account without a domain asked again, the password typed masked, kept encrypted in the file; a failed test still lets it be saved; ESC out of the first page writes nothing.</summary>
    [Fact]
    public async Task OnThePane_TheUncWizard_RunAs_KeepsThePasswordEncrypted_AndEscWritesNothing()
    {
        string path = NeonSidekick.Unc.UncConfigFile.ProfilePath(_settings.ProfileDirectory);
        var (menu, _, _) = PaneMenu(testUnc: (s, _) => Task.FromResult(NeonSidekick.Unc.UncResult<int>.Failed(NeonSidekick.Unc.UncText.BadAccount(s.Name, s.Config.User!))));
        OpenUncRow(5);
        Push(Keys.Enter);                         // the profile's file
        Type("fin");
        Type(@"\\fs02\finance");
        Push(Keys.Down, Keys.Enter);              // runas
        Type("svc");                              // refused: no domain
        Push([.. Enumerable.Repeat(Keys.Backspace, 3)]);
        Type(@"CORP\svc");
        Push(Keys.Enter);                         // file
        Type("s3cret");
        Push(Keys.Enter);                         // read
        Push(Keys.Enter);                         // no description
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test: refused, but the summary stays
        Push(Keys.Up, Keys.Enter);                // Save, hidden: back on the wizard's row
        Push(Keys.Enter);                         // the wizard again
        Push(Keys.Escape);                        // out of the first page: nothing
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var fin = Assert.Single(NeonSidekick.Unc.UncConfigFile.Load(path).Shares);
        Assert.Equal((@"\\fs02\finance", true, @"CORP\svc", false), (fin.Config.Root, fin.Config.IsRunAs, fin.Config.User, fin.Config.IsReadWrite));
        Assert.StartsWith(NeonSidekick.Sql.WindowsCredentials.ProtectedPrefix, fin.Config.Password);
        Assert.Equal("s3cret", NeonSidekick.Unc.UncSecrets.Resolve(fin).Value);
        Assert.Contains(NeonSidekick.Sql.SqlText.RunAsNeedsDomain("svc"), _console.Output);
        Assert.Contains(NeonSidekick.Unc.UncText.BadAccount("fin", @"CORP\svc"), _console.Output);
        Assert.Contains(SettingsMenu.UncWizardCancelledNotice, _console.Output);
        Assert.DoesNotContain("s3cret", _console.Output);
        Assert.Null(_settings.Current.UncSharesOffered);   // saved hidden: nothing offered
    }

    [Fact]
    public async Task OnThePane_TheUncTab_FlipsWrites_SetsARunAsPassword_AndNarrowsTheOfferedShares()
    {
        string path = NeonSidekick.Unc.UncConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(_settings.ProfileDirectory);
        File.WriteAllText(path, """{ "shares": { "eng": { "path": "//fs01/eng" }, "fin": { "path": "//fs02/fin", "auth": "runas", "user": "CORP\\svc" } } }""");
        var (menu, _, _) = PaneMenu();
        OpenUncRow(4);                                       // set password: only fin takes one
        Push(Keys.Enter);                                    // fin
        Push([.. "s3cret".Select(Keys.Char), Keys.Enter]);
        Push(Keys.Up, Keys.Up, Keys.Enter);                  // the offered row: the checklist
        Push(Keys.Down, Keys.Enter);                         // fin on (nothing is ticked until the user ticks it, 2026-10-01)
        Push(Keys.Escape);
        Push(Keys.Up, Keys.Enter, Keys.Up, Keys.Enter);      // UNC writes: the page, on picked
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.UncWrites);
        var fin = NeonSidekick.Unc.UncConfigFile.Load(path).Shares[1];
        Assert.Equal("s3cret", NeonSidekick.Unc.UncSecrets.Resolve(fin).Value);
        Assert.Equal(@"fin  (\\fs02\fin, runas CORP\svc · encrypted in unc.json)", SettingsMenu.UncPasswordRow(fin));
        Assert.Equal(["fin"], _settings.Current.UncSharesOffered);
        Assert.DoesNotContain("s3cret", _console.Output);
        Assert.Equal("1 of 2", SettingsMenu.UncOfferedValue(["fin"], NeonSidekick.Unc.UncConfigFile.Load(path)));
        Assert.Equal("none of 2", SettingsMenu.UncOfferedValue(null, NeonSidekick.Unc.UncConfigFile.Load(path)));
        Assert.Equal("2 shares · Enter edits unc.json", SettingsMenu.UncSharesLabel(path));
        Assert.Equal("Enter to set password for a runas share", SettingsMenu.UncSetPasswordLabel);
        Assert.Equal("Enter to start share wizard", SettingsMenu.UncAddShareLabel);
    }

    /// <summary>The SQL tab, the add-connection row (the fifth, under the password prompt): the wizard.</summary>
    private void OpenSqlWizard() => Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter]);

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
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows, always both since 2026-10-01)
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
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
        Assert.Equal(["aw"], _settings.Current.SqlConnectionsOffered);   // offered by the wizard (nothing is until ticked, 2026-10-01)
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
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows): refused
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
        // The summary: the two save rows, Test, Cancel, then the rows File, Name, Server, Database, Sign-in (the ninth).
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
        Push(Keys.Up, Keys.Enter);                // sql: the user page, then the password page, then the summary
        Type("reader");
        Type("pw");
        Push(Keys.Home, Keys.Enter);              // Save, and offer it
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var me = Assert.Single(NeonSidekick.Sql.SqlConfigFile.Load(path).Connections);
        Assert.Equal("sql", me.Config.Auth);
        Assert.Equal("reader", me.Config.User);
        Assert.Equal("pw", NeonSidekick.Sql.SqlSecrets.Resolve(me).Value);
    }

    /// <summary>
    /// <c>SQL connections offered</c> (later on 2026-09-23): a checklist of every connection, none ticked at first (2026-10-01);
    /// a flip offers one, a flip back hides it again, and a connection added to the file afterwards starts unticked.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheSqlTab_NarrowsTheOfferedConnections_AndANewOneStartsHidden()
    {
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(_settings.ProfileDirectory);
        File.WriteAllText(path, """{ "connections": { "aw": { "server": "x", "auth": "windows" }, "prod": { "server": "y", "auth": "windows" } } }""");
        var (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Enter]);   // SQL, the offered row: the checklist
        Push(Keys.Enter);                                    // aw on
        Push(Keys.Char(' '));                                // and off again (Space flips too)
        Push(Keys.Enter);                                    // and on
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["aw"], _settings.Current.SqlConnectionsOffered);
        Assert.Contains("SQL connections offered     none of 2", _console.Output);
        Assert.Contains("SQL connections offered     1 of 2", _console.Output);
        Assert.Contains("[x] aw    x", _console.Output);

        File.WriteAllText(path, """{ "connections": { "aw": { "server": "x", "auth": "windows" }, "prod": { "server": "y", "auth": "windows" }, "new": { "server": "z", "auth": "windows" } } }""");
        var loaded = NeonSidekick.Sql.SqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
        Assert.Equal("1 of 3", SettingsMenu.SqlOfferedValue(_settings.Current.SqlConnectionsOffered, loaded));   // the new one hidden until ticked
        Assert.Equal("none of 3", SettingsMenu.SqlOfferedValue(null, loaded));   // null offers none (2026-10-01)
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
        Push([.. ToTab(ToolsText.ObsidianTabTitle), Keys.Down, Keys.Enter]);   // Obsidian, the vault row: the picker, the vault picked
        Push(Keys.Enter);                                       // again: nothing picked
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["", vault], opened);
        Assert.Equal(vault, _settings.Current.ObsidianVault);
        pane.Dispose();
    }

    /// <summary>The Git (native) tab (2026-09-20; its name since later on 2026-09-21, the last but one since 2026-09-22, after SQL since 2026-09-27): the switch first, the two caps typed.</summary>
    [Fact]
    public async Task OnThePane_TheGitTab_SitsBeforeOptions_ItsCapsAreTyped()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Left, Keys.Left);                                        // the strip wraps: Offered → Options → GitLib, its first row
        Push(Keys.Down, Keys.Enter);                            // Git diff max lines: the typed slot, pre-filled with 500
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Char('1'), Keys.Char('0'), Keys.Char('0'), Keys.Char('0'), Keys.Enter);
        Push(Keys.Down, Keys.Enter);                            // Git log max commits: the slot, pre-filled with 20; 500 is out of range, kept
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('5'), Keys.Char('0'), Keys.Char('0'), Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(1000, _settings.Current.GitLibDiffMaxLines);
        Assert.Equal(20, _settings.Current.GitLibLogMaxCommits);
        // The five rows padded to the tab's own column (24 since the GitLib labels, 2026-09-30; 28 before), the diff cap's notice then the log cap's refusal on the status line.
        AssertTabEnds("\n" + Titled(Strip) + "\n \n▸ GitLib tools            on\n  GitLib diff max lines   500 lines\n  GitLib log max commits  20 commits\n  GitLib email            (not set)\n  GitLib name             (not set)\n", 100);
        Assert.Contains("\n  GitLib diff max lines   1000 lines\n", _console.Output);
        Assert.Contains("GitLib log max commits must be 1 to 200 commits; keeping 20.", _console.Output);
        pane.Dispose();
    }

    /// <summary>The Shell tab (2026-09-21; between Files and Ask since later that day): the policy picker first (its switch), the allowed list, the shell picker, then the three typed rows.</summary>
    [Fact]
    public async Task OnThePane_TheShellTab_SitsBetweenFilesAndAsk_PolicyAndShellArePickers_TheListRemoves()
    {
        _settings.Update(d => d.ShellCommandAllowed = ["git push", "dotnet build"]);
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ShellTabTitle));   // Ask, Web, Shell
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Char('y'), Keys.Enter);   // Shell command policy: the picker opens on ask, yolo picked and confirmed (2026-10-03)
        Push(Keys.Down, Keys.Enter, Keys.Enter, Keys.Escape);               // Shell allowed commands: the list, dotnet build removed, back
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);      // past Shell police outside paths (2026-09-22), its forbidden strings (2026-10-03) and Shell prefer native tools (2026-09-26); Shell default: the picker, cmd picked
        Push(Keys.Down, Keys.Enter);                                        // Shell timeout (s): the typed slot, pre-filled with 180; 0 is out of range, kept
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("yolo", _settings.Current.ShellCommandPolicy);
        Assert.Equal(["git push"], _settings.Current.ShellCommandAllowed);
        Assert.Equal("cmd", _settings.Current.ShellDefault);
        Assert.Equal(180, _settings.Current.ShellTimeoutSeconds);
        // The ten rows padded to the tab's own column (27), then the picker's rows, the list's, and the notices on the status line.
        AssertTabEnds("\n" + Titled(Strip) + "\n \n▸ Shell command policy            ask\n  Shell allowed commands          2 prefixes\n  Shell police outside paths      on\n  Shell police forbidden strings  none\n  Shell prefer native tools       on\n  Shell default                   powershell\n  Shell timeout (s)               180\n  Shell foreground cap (s)        600\n  Shell output max chars          30,000 chars\n  Shell code languages            powershell, python, node\n  Shell code timeout (s)          300\n  Shell tool bridge               off\n  Shell tool bridge max calls     50 tool calls\n", 100);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell command policy") + "\n \n  off  no shell or script tool is offered\n▸ ask  you approve each command not on the allow list\n  yolo every command runs, nothing is asked\n", _console.Output);
        Assert.Contains("  · Shell command policy: yolo\n", _console.Output);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell allowed commands   " + SettingsMenu.PolicyAskButton + "    " + SettingsMenu.PolicyYoloButton + " ") + "\n \n▸ dotnet build\n  git push\n", _console.Output);
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
        Push(ToTab(ToolsText.ShellTabTitle));   // Shell
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Shell tool bridge: the picker opens on off (one more Down since the police row, 2026-09-22, one more since prefer native, 2026-09-26, one more since the forbidden strings, 2026-10-03)
        Push(Keys.Up, Keys.Enter);                                          // on is the row above
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.ShellToolBridge);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell tool bridge") + "\n \n  on  a script may call this app's other tools through its neon_tools module\n▸ off a script does everything itself: no neon_tools module, no tool calls\n", _console.Output);
        Assert.Contains("  · Shell tool bridge: on", _console.Output);
        Assert.Contains("\n▸ Shell tool bridge               on\n  Shell tool bridge max calls     50 tool calls\n", _console.Output);
        pane.Dispose();
    }

    /// <summary>The outside-paths police row (2026-09-22): the Shell tab's third row, a toggle that opens on the saved on; off is the row below; no reconnect.</summary>
    [Fact]
    public async Task OnThePane_ThePoliceRow_IsAPicker_NoReconnect()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ShellTabTitle));   // Shell
        Push(Keys.Down, Keys.Down, Keys.Enter);                 // Shell police outside paths: the picker opens on on
        Push(Keys.Down, Keys.Enter);                            // off is the row below
        Push(Keys.Char('y'), Keys.Enter);                       // yes to the question (2026-10-02)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.ShellPoliceOutsidePaths);
        Assert.Contains("\n" + Titled(SettingsMenu.PoliceOffConfirmQuestion) + "\n", _console.Output);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell police outside paths   " + SettingsMenu.PoliceStringsButton + " ") + "\n \n▸ on  shell police enabled\n  off shell police disabled\n", _console.Output);
        Assert.Contains("  · Shell police outside paths: off", _console.Output);
        Assert.Contains("\n▸ Shell police outside paths      off\n  Shell police forbidden strings  none\n  Shell prefer native tools       on\n  Shell default                   powershell\n", _console.Output);
        pane.Dispose();
    }

    /// <summary>
    /// <c>/police</c>'s page (2026-10-02, the user's ask): off asks <see cref="SettingsMenu.PoliceOffConfirmQuestion"/> on the
    /// same pane first; No (or ESC) leaves the police on.
    /// </summary>
    [Fact]
    public async Task ShowPolice_OffRefused_KeepsItOn()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Down, Keys.Enter, Keys.Enter);   // off picked; Enter on No

        await menu.ShowPoliceAsync(CancellationToken.None);

        Assert.True(_settings.Current.ShellPoliceOutsidePaths);
        Assert.Contains("\n" + Titled(SettingsMenu.PoliceOffConfirmQuestion) + "\n", _console.Output);
        Assert.DoesNotContain("Shell police outside paths: off", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task ShowPolice_OffConfirmed_Saves()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Down, Keys.Enter, Keys.Char('y'), Keys.Enter);   // off picked; yes

        await menu.ShowPoliceAsync(CancellationToken.None);

        Assert.False(_settings.Current.ShellPoliceOutsidePaths);
        Assert.Contains(SettingsMenu.PoliceOffConfirmQuestion, _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>Back on never asks (2026-10-02).</summary>
    [Fact]
    public async Task ShowPolice_OnAsksNothing()
    {
        _settings.Update(d => d.ShellPoliceOutsidePaths = false);
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Up, Keys.Enter);   // on is the row above

        await menu.ShowPoliceAsync(CancellationToken.None);

        Assert.True(_settings.Current.ShellPoliceOutsidePaths);
        Assert.DoesNotContain(SettingsMenu.PoliceOffConfirmQuestion, _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>
    /// <c>/tools &lt;group&gt;</c> (2026-10-03, the toolbar's tool switches): a group's on/off page straight under the Tools
    /// crumb, the save on the status line, ESC closing the pane — never the tabs.
    /// </summary>
    [Fact]
    public async Task ShowSwitch_ATool_IsItsOnOffPage_PickingSaves()
    {
        _settings.Update(d => d.DockerTools = false);
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Up, Keys.Enter);   // on is the row above

        await menu.ShowSwitchAsync(SettingsField.DockerTools, CancellationToken.None);

        Assert.True(_settings.Current.DockerTools);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › " + SettingsMenu.FieldName(SettingsField.DockerTools)) + "\n", _console.Output);
        Assert.DoesNotContain(ToolsText.OfferedTabTitle + "    Web", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>The shell's is the policy picker (2026-10-03): a move into yolo asks first, No keeps the policy; off asks nothing.</summary>
    [Fact]
    public async Task ShowSwitch_TheShell_IsThePolicyPicker_YoloAsksFirst()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Down, Keys.Enter, Keys.Enter);   // yolo picked; Enter on No

        await menu.ShowSwitchAsync(SettingsField.ShellCommandPolicy, CancellationToken.None);

        Assert.Equal("ask", _settings.Current.ShellCommandPolicy);
        Assert.Contains("\n" + Titled(SettingsMenu.YoloConfirmQuestion) + "\n", _console.Output);

        Push(Keys.Down, Keys.Enter, Keys.Char('y'), Keys.Enter);   // yolo picked; yes
        await menu.ShowSwitchAsync(SettingsField.ShellCommandPolicy, CancellationToken.None);
        Assert.Equal("yolo", _settings.Current.ShellCommandPolicy);

        int mark = _console.Output.Length;
        Push(Keys.Up, Keys.Up, Keys.Enter);   // off: nothing asked
        await menu.ShowSwitchAsync(SettingsField.ShellCommandPolicy, CancellationToken.None);
        Assert.Equal("off", _settings.Current.ShellCommandPolicy);
        Assert.DoesNotContain(SettingsMenu.YoloConfirmQuestion, _console.Output[mark..]);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>The code-languages list (2026-09-21): Enter or Space flips and saves at once, the last one on refuses to go.</summary>
    [Fact]
    public async Task OnThePane_TheCodeLanguagesRow_IsACheckboxList_TheLastOneStays()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ShellTabTitle));   // Shell
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Shell code languages: the list (one more Down since the police row, 2026-09-22, one more since prefer native, 2026-09-26, one more since the forbidden strings, 2026-10-03)
        Push(Keys.Char(' '));                                               // powershell off
        Push(Keys.Down, Keys.Enter);                                        // python off
        Push(Keys.Down, Keys.Enter);                                        // node: the last one, refused
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["node"], _settings.Current.ShellCodeLanguages);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell code languages   " + SettingsMenu.SelectAllButton + "    " + SettingsMenu.SelectNoneButton + " ") + "\n \n▸ [x] powershell a .ps1 through pwsh or Windows PowerShell; Invoke-NeonTool calls a tool\n  [x] python     a .py through python.exe; from neon_tools import …\n  [x] node       a .js through node.exe; require('neon_tools')\n", _console.Output);
        Assert.Contains("  · Shell code languages: python, node\n", _console.Output);
        Assert.Contains("  · Shell code languages: node\n", _console.Output);
        Assert.Contains("At least one language stays on.", _console.Output);
        Assert.Contains("\n▸ Shell code languages            node\n  Shell code timeout (s)          300\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheWebTab_SitsAfterOptions()
    {
        // The last tab until later on 2026-09-21 (Left wrapped to it); third since, the user's order.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.WebTabTitle));     // Web
        Push(Keys.Enter, Keys.Down, Keys.Enter);   // Web tools: the page, off picked
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));   // read at the next turn: nothing to reconnect

        Assert.False(_settings.Current.WebTools);
        // The seven rows padded to the tab's own column (26), the toggle's notice on the status line under the strip.
        AssertTabEnds("\n" + Titled(Strip) + "\n \n▸ Web tools                 on\n  Web browser mode          default\n  Web browser path          (auto: msedge.exe)\n  Web browser network mode  internet\n  Web search method         duckduckgo\n  Web SearXNG URL           (not set)\n  Web search max results    20 results\n  Web download max (MB)     50 MB\n", 100);
        Assert.Contains("\n" + Titled(Strip) + "\n  · Web tools: off\n▸ Web tools                 off\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheClaudeTab_HoldsNoReconnectRow_SinceItsServerRowsLeft()
    {
        // The Anthropic API's rows were here from 2026-09-29 until 2026-10-03 (the user's call: /settings' own Claude tab, where
        // SettingsMenuTests.OnThePane_TheClaudeApiSwitch_OnTheClaudeTab_AsksForAReconnect follows them); a flip here is read
        // at the next turn and asks for nothing.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ClaudeCliTabTitle));

        Down(TabFields(ToolsText.ClaudeCliTabTitle).ToList().IndexOf(SettingsField.ClaudeCliAdvisor));
        Push(Keys.Enter, Keys.Up, Keys.Enter);     // Claude CLI advisor tool: the page opens on off, on picked
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.True(_settings.Current.ClaudeCliAdvisor);
        Assert.DoesNotContain(TabFields(ToolsText.ClaudeCliTabTitle), SettingsMenu.IsLlmField);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_APickerFromTheWebTab_IsTitledUnderTools_AndTheRootComesBack()
    {
        var (menu, pane, settings) = PaneMenu();
        Push(ToTab(ToolsText.WebTabTitle));     // Web
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
    public async Task OnThePane_ATypedRow_EditsUnderTheList_AndEscKeepsTheSavedValue()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.AskTabTitle));     // Ask
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
    public async Task OnThePane_DiffMaxLines_IsTheOptionsTabsLastRow_Typed_ZeroIsTheHeaderAlone_OutOfRangeRefused()
    {
        // Later on 2026-10-03: under Show file diffs; 0 to 500, 10 by default (40 until later that day), 0 = the header line alone.
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Left);                                                                     // Options, the strip wrapped
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);                        // Diff max lines: the typed slot with "10"
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('5'), Keys.Char('0'), Keys.Char('1'), Keys.Enter);   // refused: 501
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);        // 0: the header alone
        Push(Keys.Enter, Keys.Backspace, Keys.Char('1'), Keys.Char('2'), Keys.Enter);        // 12
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(12, _settings.Current.DiffMaxLines);
        Assert.Contains("Diff max lines " + SettingsMenu.DiffMaxLinesRangeError + "; keeping 10.", _console.Output);
        Assert.Contains("  · Diff max lines: header only\n", _console.Output);
        Assert.Contains("  · Diff max lines: 12 lines\n", _console.Output);
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
        Push(ToTab(ToolsText.FilesTabTitle));   // Files
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // View image max per call (the sixth row since 2026-10-01, Safe edits gone; the seventh from 2026-09-21): the typed slot with "10"
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
    public async Task OnThePane_FileSearchMaxResults_IsTheFilesTabsLastRow_Typed_OutOfRangeRefused()
    {
        // 2026-10-01, the user's ask: 1 to 5000, 200 by default (the constant until then); search_files and unc_search read it at their next call.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.FilesTabTitle));   // Files
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the seventh row, after the view_image cap: the typed slot with "200"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);   // refused: 0
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Char('1'), Keys.Char('0'), Keys.Char('0'), Keys.Char('0'), Keys.Enter);   // 1000
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(1000, _settings.Current.FileSearchMaxResults);
        Assert.Contains("File search max results " + SettingsMenu.FileSearchMaxResultsRangeError + "; keeping 200.", _console.Output);
        Assert.Contains("  · File search max results: 1000 results\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_WebDownloadMax_IsTheWebTabsLastRow_Typed_OutOfRangeRefused()
    {
        // 2026-10-01, the user's ask: 1 to 102400 MB, 50 by default (the constant until then).
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.WebTabTitle));   // Web
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the eighth row, after the search count: the typed slot with "50"
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);   // refused: 0
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('2'), Keys.Char('0'), Keys.Char('0'), Keys.Char('0'), Keys.Enter);   // 2000
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(2000, _settings.Current.WebDownloadMaxMegabytes);
        Assert.Contains("Web download max (MB) " + SettingsMenu.WebDownloadMaxMegabytesRangeError + "; keeping 50.", _console.Output);
        Assert.Contains("  · Web download max (MB): 2,000 MB\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_QueryResultMaxChars_IsOnTheSqlTab_UnderTheTimeout_Typed_OutOfRangeRefused()
    {
        // 2026-10-01, the user's ask: 1,000 to 1,000,000, 32,000 by default (the file tools' cap until then); the three query tools read it at their next call.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.SqlTabTitle));   // SQL
        Push(Enumerable.Repeat(Keys.Down, 8).Append(Keys.Enter).ToArray());   // the ninth row, under the timeout: the typed slot with "32000"
        Push(Enumerable.Repeat(Keys.Backspace, 5).Append(Keys.Char('9')).Append(Keys.Enter).ToArray());   // refused: 9
        Push(new[] { Keys.Enter }.Concat(Enumerable.Repeat(Keys.Backspace, 5)).Concat("200,000".Select(Keys.Char)).Append(Keys.Enter).ToArray());   // 200,000, the separator allowed
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(200_000, _settings.Current.QueryResultMaxChars);
        Assert.Contains("SQL query result max chars " + SettingsMenu.QueryResultMaxCharsRangeError + "; keeping 32000.", _console.Output);
        Assert.Contains("  · SQL query result max chars: 200,000 chars\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_SpaceOnASettingsRow_IsNothing()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.AskTabTitle));   // Ask
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
        Push([.. ToTab(ToolsText.AskTabTitle), Keys.Enter, Keys.Down, Keys.Enter]);   // Ask user off
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
        _console.Profile.Width = 180;   // the width the lines below were wrapped at: at 190 (the strip's, 2026-10-02) the note breaks across the wrap
        var pane = new ScreenPane(_console, geometry: null, _time);
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var settings = new SettingsMenu(_console, _settings, _ => null, new InputLine(_console, keys), new TranscriptRenderer(_console), _speech, menuPane, _ => FakeBrowserPath);
        var menu = new ToolsMenu(Facts, _settings, settings, new TranscriptRenderer(_console), menuPane);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  · Offered\n  ·   Clock (3)\n  ·     get_current_time      on   ", _console.Output);
        Assert.Contains("  ·   Files (13 of 14)\n", _console.Output);
        Assert.Contains("  ·     read_file             off  Reads a text file", _console.Output);   // the console wraps the long line
        Assert.Contains("switched off in /tools", _console.Output);
        Assert.Contains("  ·   Questions (1) (off: no pane)\n", _console.Output);
        // The tabs in strip order, the user's since 2026-10-03: Offered, Ask, Web, Shell, Files, UNC, Print, Camera, Obsidian, SQL, MySQL, Oracle, Claude, Docker, HA, ComfyUI, GitLib, Options. Before it Web right after Offered, then Files, Shell, Ask (the user's order, later on 2026-09-21), Claude (Claude (CLI) until 2026-09-29, the Anthropic API's four rows last since), Obsidian, ComfyUI, SQL, Git (native) (the user's order, 2026-09-27), HA (2026-09-28; second to last since later on 2026-10-01, the user's ask) and Options last (2026-09-22).
        Assert.Contains("  · Web\n  ·   Web tools: on\n  ·   Web browser mode: default\n  ·   Web browser path: (auto: msedge.exe)\n", _console.Output);
        Assert.Contains("  ·   Web search max results: 20 results\n  ·   Web download max (MB): 50 MB\n  · Shell\n  ·   Shell command policy: ask\n", _console.Output);
        Assert.Contains("  ·   File view image max (per call): 10 pictures\n  ·   File search max results: 200 results\n  · UNC\n", _console.Output);
        Assert.Contains("  · Shell\n  ·   Shell command policy: ask\n", _console.Output);
        Assert.Contains("  · Ask\n  ·   Ask user: on\n  ·   Ask max questions: 10 questions\n  ·   Ask max choices per question: 10 choices\n  · Web\n", _console.Output);
        Assert.Contains("  ·   Shell tool bridge max calls: 50 tool calls\n  · Files\n  ·   File tools: on\n", _console.Output);
        Assert.Contains("  ·   File search max results: 200 results\n  · UNC\n  ·   UNC tools: off\n  ·   UNC writes: off\n  ·   UNC shares offered: none of 0\n  ·   UNC default share: (the first share)\n  ·   UNC set password: Enter to set password for a runas share\n  ·   UNC add share: Enter to start share wizard\n  ·   UNC *-mention enabled: on\n  ·   UNC shares (profile): (none) · Enter edits unc.json\n  ·   UNC shares (global): (none) · Enter edits unc.json\n  · Print\n  ·   Print tools: off\n  ·   Print action policy: ask\n  ·   Print default printer: (Windows default)\n  ·   Print font size (pt): 10 pt\n  ·   PDF engine: auto\n  · Camera\n  ·   Camera tool: off\n  ·   Camera shutter: user\n  ·   Camera preview: live\n  ·   Camera device: (first camera)\n  ·   Camera resolution: 1280x720\n  ·   Camera output folder: camera_images\n  ·   Camera keep in sessions: off\n  ·   Camera watch interval (s): 10\n  ·   Camera watch change (%): 8%\n  ·   Camera watch speaks up: off\n  ·   Camera watch min gap (s): 120\n  · Screen\n  ·   Screen capture tool: off\n  ·   Screen capture ask: ask\n  ·   Screen capture preview: on\n  ·   Screen capture output folder: screen_images\n  ·   Screen capture keep in sessions: off\n  · Obsidian\n  ·   Obsidian tools: on\n  ·   Obsidian vault: (not set)\n  ·   Obsidian allow delete (.trash): on\n  · SQL\n  ·   SQL tools: on\n  ·   SQL connections offered: none of 0\n  ·   SQL default connection: (the first connection)\n  ·   SQL set password: Enter to set password for a connection\n  ·   SQL add connection: Enter to start connection wizard\n  ·   SQL %-mention enabled: on\n  ·   SQL max rows: 100 rows\n  ·   SQL query timeout (s): 30\n  ·   SQL query result max chars: 32,000 chars\n  ·   SQL connections (profile): (none) · Enter edits sql.json\n  ·   SQL connections (global): (none) · Enter edits sql.json\n  · MySQL\n  ·   MySQL tools: off\n  ·   MySQL connections offered: none of 0\n  ·   MySQL default connection: (the first connection)\n  ·   MySQL set password: Enter to set password for a connection\n  ·   MySQL add connection: Enter to start connection wizard\n  ·   MySQL %-mention enabled: on\n  ·   MySQL max rows: 100 rows\n  ·   MySQL query timeout (s): 30\n  ·   MySQL connections (profile): (none) · Enter edits mysql.json\n  ·   MySQL connections (global): (none) · Enter edits mysql.json\n  · SQLite\n  ·   SQLite tools: off\n  ·   SQLite databases offered: none of 0\n  ·   SQLite default database: (the first database)\n  ·   SQLite sandbox files: on\n  ·   SQLite add database: Enter to start database wizard\n  ·   SQLite %-mention enabled: on\n  ·   SQLite max rows: 100 rows\n  ·   SQLite query timeout (s): 30\n  ·   SQLite databases (profile): (none) · Enter edits sqlite.json\n  ·   SQLite databases (global): (none) · Enter edits sqlite.json\n  · Postgres\n  ·   PostgreSQL tools: off\n  ·   PostgreSQL connections offered: none of 0\n  ·   PostgreSQL default connection: (the first connection)\n  ·   PostgreSQL set password: Enter to set password for a connection\n  ·   PostgreSQL add connection: Enter to start connection wizard\n  ·   PostgreSQL %-mention enabled: on\n  ·   PostgreSQL max rows: 100 rows\n  ·   PostgreSQL query timeout (s): 30\n  ·   PostgreSQL connections (profile): (none) · Enter edits postgres.json\n  ·   PostgreSQL connections (global): (none) · Enter edits postgres.json\n  · Oracle\n  ·   Oracle tools: off\n  ·   Oracle connections offered: none of 0\n  ·   Oracle default connection: (the first connection)\n  ·   Oracle set password: Enter to set password for a connection\n  ·   Oracle add connection: Enter to start connection wizard\n  ·   Oracle %-mention enabled: on\n  ·   Oracle max rows: 100 rows\n  ·   Oracle query timeout (s): 30\n  ·   Oracle connections (profile): (none) · Enter edits oracle.json\n  ·   Oracle connections (global): (none) · Enter edits oracle.json\n  · ClaudeCLI\n  ·   Claude CLI executable: (looked up)\n  ·   Claude CLI slash command permissions: read-only\n  ·   Claude CLI slash command model: (Claude Code's default)\n  ·   Claude CLI slash command effort: (Claude Code's default)\n  ·   Claude CLI advisor tool: off\n  ·   Claude CLI advisor tool context: brief\n  ·   Claude CLI advisor tool calls per turn: 2 calls\n  ·   Claude CLI advisor tool model: (as Claude CLI slash command model)\n  ·   Claude CLI advisor tool effort: (as Claude CLI slash command effort)\n  ·   Claude CLI advisor tool confirm: off\n  · Docker\n  ·   Docker tools: off\n  ·   Docker writes: off\n  ·   Docker engine pipe: \\\\.\\pipe\\docker_engine\n  · HA\n  ·   Home Assistant tools: on\n  ·   Home Assistant URL: (not set)\n  ·   Home Assistant API key: (none)\n  ·   Home Assistant test connection: Enter to ask the server for its version\n  ·   Home Assistant action policy: ask\n  ·   Home Assistant Assist agent: (Home Assistant's default)\n  ·   Home Assistant timeout (s): 10\n  · ComfyUI\n  ·   ComfyUI tools: on\n  ·   ComfyUI URL: (not set)\n  ·   ComfyUI workflows offered: none of 0\n  ·   ComfyUI add workflow: Enter to start workflow wizard\n  ·   ComfyUI ^-mention enabled: on\n  ·   ComfyUI timeout (s): 300\n  ·   ComfyUI max pictures per call: 5 pictures\n  ·   ComfyUI reinforce negatives: on\n  ·   ComfyUI show prompts: on\n  ·   ComfyUI picture strip: on\n  ·   ComfyUI output folder: comfy_images\n  · GitLib\n  ·   GitLib tools: on\n  ·   GitLib diff max lines: 500 lines\n  ·   GitLib log max commits: 20 commits\n  ·   GitLib email: (not set)\n  ·   GitLib name: (not set)\n  · Options\n  ·   $-mention enabled: on\n  ·   Tool collapse count: 2 lines\n  ·   Code collapse count: 20 lines\n", _console.Output);
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
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell allowed commands   " + SettingsMenu.PolicyAskButton + "    " + SettingsMenu.PolicyYoloButton + " ") + "\n \n▸ dotnet build\n  git push\n", _console.Output);
        Assert.Contains("  · Shell allowed commands: dotnet build removed\n▸ git push\n", _console.Output);
        Assert.DoesNotContain(Strip, _console.Output);
        Assert.Equal(SettingsMenu.Title, settings.Root);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>
    /// The list's policy buttons (2026-10-02, the user's ask): Y asks on the same pane before yolo saves, the save on the
    /// list's status line.
    /// </summary>
    [Fact]
    public async Task ShowAllowedCommands_YoloAsksFirst()
    {
        _settings.Update(d => d.ShellCommandAllowed = ["git push"]);
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('y'), Keys.Char('y'), Keys.Enter, Keys.Escape);   // yolo, yes; close

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal("yolo", _settings.Current.ShellCommandPolicy);
        Assert.Equal(["git push"], _settings.Current.ShellCommandAllowed);
        Assert.Contains(SettingsMenu.AllowedCommandsKeys, _console.Output);
        Assert.Contains("\n" + Titled(SettingsMenu.YoloConfirmQuestion) + "\n", _console.Output);
        Assert.Contains("  · Shell command policy: yolo\n▸ git push\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>From yolo, A goes back to ask at once with nothing asked (2026-10-02).</summary>
    [Fact]
    public async Task ShowAllowedCommands_AskSavesAtOnce()
    {
        _settings.Update(d => d.ShellCommandPolicy = "yolo");
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('a'), Keys.Escape);

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal("ask", _settings.Current.ShellCommandPolicy);
        Assert.DoesNotContain(SettingsMenu.YoloConfirmQuestion, _console.Output);
        Assert.Contains("  · Shell command policy: ask\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task ShowAllowedCommands_YoloRefused_KeepsAsk()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('y'), Keys.Escape, Keys.Char('y'), Keys.Enter, Keys.Escape);   // yolo, ESC; yolo, Enter on No; close

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal("ask", _settings.Current.ShellCommandPolicy);
        Assert.Contains(SettingsMenu.AllowedCommandsEmptyKeys, _console.Output);   // the buttons work on the empty list
        Assert.Contains(SettingsMenu.YoloConfirmQuestion, _console.Output);
        Assert.Contains("  · " + SettingsMenu.UnchangedNotice + "\n", _console.Output);
        Assert.DoesNotContain("Shell command policy: yolo", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task ShowAllowedCommands_FromOff_YoloAsksToo()
    {
        _settings.Update(d => d.ShellCommandPolicy = "off");
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('y'), Keys.Char('y'), Keys.Enter, Keys.Escape);   // yolo, yes; close

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal("yolo", _settings.Current.ShellCommandPolicy);
        Assert.Contains(SettingsMenu.YoloConfirmQuestion, _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task ShowAllowedCommands_TheLitButtonSavesNothing()
    {
        _settings.Update(d => d.ShellCommandPolicy = "yolo");
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('y'), Keys.Escape);   // yolo again (lit): nothing asked, nothing saved; close

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal("yolo", _settings.Current.ShellCommandPolicy);
        Assert.DoesNotContain(SettingsMenu.YoloConfirmQuestion, _console.Output);
        Assert.DoesNotContain("Shell command policy: yolo", _console.Output);
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

    private static ConsoleKeyInfo[] Typed(string text) => [.. text.Select(Keys.Char), Keys.Enter];

    /// <summary>
    /// The forbidden-strings row (2026-10-03, the user's idea): under the police row on the Shell tab; the list's top row opens the
    /// slot and a typed string saves at once (spacing collapsed, case kept), a duplicate ignoring case and spacing is refused, ESC in
    /// the slot is back on the list, Enter on a string removes it.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheForbiddenStringsRow_AddsTyped_RefusesADuplicate_EnterRemoves()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ShellTabTitle));
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // under Shell police outside paths: the list, on its add row
        Push(Keys.Enter);
        Push(Typed("rm  -rf"));                              // added as "rm -rf"
        Push(Keys.Enter);
        Push(Typed("Format"));                               // added; A to Z ignoring case puts it first
        Push(Keys.Enter);
        Push(Typed("RM -RF"));                               // already there
        Push(Keys.Enter, Keys.Escape);                       // the slot, ESC: back on the list
        Push(Keys.Down, Keys.Down, Keys.Enter);              // rm -rf removed
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["Format"], _settings.Current.ShellPoliceForbiddenStrings);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell police forbidden strings") + "\n \n▸ " + SettingsMenu.AddForbiddenRow + "\n", _console.Output);
        Assert.Contains(SettingsMenu.ForbiddenKeys, _console.Output);
        Assert.Contains("  · " + SettingsMenu.ForbiddenAddedNotice("rm -rf") + "\n▸ " + SettingsMenu.AddForbiddenRow + "\n  rm -rf\n", _console.Output);
        Assert.Contains("  · " + SettingsMenu.ForbiddenAddedNotice("Format") + "\n▸ " + SettingsMenu.AddForbiddenRow + "\n  Format\n  rm -rf\n", _console.Output);
        Assert.Contains("  · Shell police forbidden strings: RM -RF is already in the list\n", _console.Output);
        Assert.Contains("  · Shell police forbidden strings: rm -rf removed\n", _console.Output);
        Assert.Contains("\n▸ Shell police forbidden strings  1 string\n  Shell prefer native tools       on\n", _console.Output);
        pane.Dispose();
    }

    /// <summary>
    /// <c>/police</c>' strings button (2026-10-03, the user's pick): S opens the forbidden-strings list over the police page, ESC
    /// there comes back to the page, the switch untouched.
    /// </summary>
    [Fact]
    public async Task ShowPolice_TheStringsButton_OpensTheList_AndComesBack()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('s'), Keys.Enter);
        Push(Typed("shutdown"));
        Push(Keys.Escape, Keys.Escape);   // the list, then the police page

        await menu.ShowPoliceAsync(CancellationToken.None);

        Assert.Equal(["shutdown"], _settings.Current.ShellPoliceForbiddenStrings);
        Assert.True(_settings.Current.ShellPoliceOutsidePaths);
        string police = "\n" + Titled(ToolsText.Label + " › Shell police outside paths   " + SettingsMenu.PoliceStringsButton + " ") + "\n";
        string list = "\n" + Titled(ToolsText.Label + " › Shell police forbidden strings") + "\n";
        Assert.Contains(SettingsMenu.PoliceToggleKeys, _console.Output);
        Assert.True(_console.Output.IndexOf(police, StringComparison.Ordinal) < _console.Output.IndexOf(list, StringComparison.Ordinal), _console.Output);
        Assert.True(_console.Output.IndexOf(list, StringComparison.Ordinal) < _console.Output.LastIndexOf(police, StringComparison.Ordinal), _console.Output);   // back on the page
        Assert.Contains("  · " + SettingsMenu.ForbiddenAddedNotice("shutdown") + "\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task ShowPolice_WithoutThePane_PrintsTheSwitchAndTheStrings()
    {
        _settings.Update(d => d.ShellPoliceForbiddenStrings = ["rm -rf", "Format"]);
        _paneOn = false;
        var pane = new ScreenPane(_console, geometry: null, _time);
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var settings = new SettingsMenu(_console, _settings, _ => null, new InputLine(_console, keys), new TranscriptRenderer(_console), _speech, menuPane, _ => FakeBrowserPath);
        var menu = new ToolsMenu(Facts, _settings, settings, new TranscriptRenderer(_console), menuPane);

        await menu.ShowPoliceAsync(CancellationToken.None);

        Assert.Contains("  · Shell police outside paths: on\n  · Shell police forbidden strings\n  ·   Format\n  ·   rm -rf\n", _console.Output);
        Assert.Equal(["Shell police forbidden strings", "  " + SettingsMenu.NoAllowedCommandsRow], ToolsMenu.ForbiddenStringLines(new AppSettingsData()));
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

    /// <summary>The ComfyUI tab, then the row: 2 the offered checklist, 3 the add-workflow wizard.</summary>
    /// <summary>The code-languages list's buttons (2026-09-29, the user's ask): N is refused — one stays — and A ticks every language.</summary>
    [Fact]
    public async Task OnThePane_TheCodeLanguagesRow_SelectNone_IsRefused_SelectAll_TicksEvery()
    {
        _settings.Update(d => d.ShellCodeLanguages = ["node"]);
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ShellTabTitle));   // Shell
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Shell code languages
        Push(Keys.Char('n'));                       // refused
        Push(Keys.Char('a'));                       // all three
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["powershell", "python", "node"], _settings.Current.ShellCodeLanguages);
        Assert.Contains(SettingsMenu.LastLanguageError, _console.Output);
        pane.Dispose();
    }

    /// <summary>
    /// SQL connections offered's buttons (2026-09-29, the user's ask): A saves the connections listed now — one added later
    /// starts hidden (the user's call) — and N saves an empty list, none offered.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheSqlOffered_SelectAll_SavesTodaysList_SelectNone_AnEmptyOne()
    {
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(_settings.ProfileDirectory);
        File.WriteAllText(path, """{ "connections": { "aw": { "server": "x", "auth": "windows" }, "prod": { "server": "y", "auth": "windows" } } }""");
        var (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Enter]);   // SQL, the offered row
        Push(Keys.Char('a'));
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["aw", "prod"], _settings.Current.SqlConnectionsOffered);

        (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Enter]);
        Push(Keys.Char('n'));
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([], _settings.Current.SqlConnectionsOffered);
    }

    /// <summary>ComfyUI workflows offered's buttons (2026-09-29, the user's ask): A the workflows installed now, N none.</summary>
    [Fact]
    public async Task OnThePane_TheComfyOffered_SelectAll_SavesTodaysList_SelectNone_AnEmptyOne()
    {
        ComfyWorkflowFile(_settings.ProfileComfyDirectory, "pony-txt2img");
        ComfyWorkflowFile(_settings.GlobalComfyDirectory, "juggernaut-xl");
        var (menu, _, _) = PaneMenu();
        OpenImagesRow(2);
        Push(Keys.Char('a'));
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(2, _settings.Current.ComfyWorkflowsOffered!.Count);
        Assert.Contains("pony-txt2img", _settings.Current.ComfyWorkflowsOffered);

        (menu, _, _) = PaneMenu();
        OpenImagesRow(2);
        Push(Keys.Char('n'));
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([], _settings.Current.ComfyWorkflowsOffered);
    }

    private void OpenImagesRow(int row) => Push([.. ToTab(ToolsText.ComfyTabTitle), .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    [Fact]
    public async Task OnThePane_TheComfyUITab_NarrowsTheOfferedWorkflows_AndANewOneStartsHidden()
    {
        ComfyWorkflowFile(_settings.ProfileComfyDirectory, "pony-txt2img");
        ComfyWorkflowFile(_settings.GlobalComfyDirectory, "juggernaut-xl");
        var (menu, _, _) = PaneMenu();
        OpenImagesRow(2);
        Push(Keys.Down, Keys.Enter);                         // pony-txt2img on (the list is by name; none ticked at first)
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["pony-txt2img"], _settings.Current.ComfyWorkflowsOffered);
        Assert.Matches(@"ComfyUI workflows offered +none of 2", _console.Output);   // the column is the tab's widest label
        Assert.Matches(@"ComfyUI workflows offered +1 of 2", _console.Output);
        Assert.Contains("[ ] juggernaut-xl  ", _console.Output);

        ComfyWorkflowFile(_settings.ProfileComfyDirectory, "new-one");
        var installed = new NeonSidekick.Comfy.ComfyWorkflowCatalog(() => [_settings.ProfileComfyDirectory, _settings.GlobalComfyDirectory]).Workflows;
        Assert.Equal("1 of 3", SettingsMenu.ComfyOfferedValue(_settings.Current.ComfyWorkflowsOffered, installed));   // the new one hidden until ticked
        Assert.Equal("none of 3", SettingsMenu.ComfyOfferedValue(null, installed));   // null offers none (2026-10-01)
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
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows)
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
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
        Assert.Equal(["ponydiffusionv6xl"], _settings.Current.ComfyWorkflowsOffered);
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
