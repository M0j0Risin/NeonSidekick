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
    /// <summary>The console's output with the strips' badges taken off (<see cref="TabStrips.Unbadged"/>, 2026-10-07): the fixture's own rows move them.</summary>
    private string Output => TabStrips.Unbadged(_console.Output);
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
        _console.Profile.Width = 240;   // 220 until 2026-10-05, when "YouTube" took the strip to 220 cells; 190 until 2026-10-04, when "ClaudeCLI", "Screen" and "SQLite" took the strip to 196 cells (220 leaves room for Postgres); 180 until 2026-10-02, when "Docker" took the strip past it; 170 until later on 2026-09-30, when "MySQL" took the strip to 170 cells; 160 until 2026-09-30, when "Oracle" took the strip to 161 cells; 150 until later on 2026-09-28, when "Print" took the strip to 151 cells; 130 until 2026-09-28, when "Home Assistant" took the strip to 142 cells; 120 until later on 2026-09-27, when "Claude (CLI)" took the strip to 124 cells (the user's call: it takes a second strip row, lined up under Offered, below 128 columns since the tab strip's own layout on the same day); 100 until 2026-09-24, when the Images tab took the ten-tab strip to 107 cells
        _settings = new AppSettings(_dir);
        _settings.Update(d => { d.TtsOutput = true; d.TtsSource = "http"; d.TtsHttpUrl = "http://localhost:8880/v1"; d.ToolsDisabled = []; d.GitLibTools = true; });   // delete off by default (2026-09-20), GitLib tools off by default (2026-09-21): the Offered-tab scripts start from every tool on
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
        return new ToolsFacts(SystemPromptSummary.ToolGroups(_clock, _timers, _files, _memory, MemoryMode.Enabled(e), e.LlmOfferTools, _web, e.WebTools, e.FileTools, _questions, e.AskUser, _paneOn, disabled: disabled), e.LlmOfferTools, disabled);
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
        Assert.Matches(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(rows) + "(?:(?: |  [^\n]*)\n)*" + System.Text.RegularExpressions.Regex.Escape(Rule(width))), Output);

    /// <summary>A title or strip row as the pane prints it: the text, then the × close glyph in column width − 2.</summary>
    // The pane's own rule (ScreenPane.Draw): a first row with no room for the gap and the glyph goes without. The nine-tab
    // strip (SQL, 2026-09-23) is 97 cells, so on the fixture's 100 columns the strip row carries no ×; a narrower title still does.
    private string Titled(string row) =>
        TextCells.Width(row) + ScreenPane.TrailerGap + TextCells.Width(ScreenPane.CloseGlyph) > _console.Profile.Width - 1
            ? row
            : row + new string(' ', _console.Profile.Width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    /// <summary>The strip as the pane prints it: the label, then every tab title with a space either side, two spaces between. Pinned.</summary>
    private const string Strip = ToolsText.Label + " │ Offered · Core · Files · Apps · Admin · Databases · Devices · Home ";   // Home (HomeAssistant and Shopping) and Apps (Integrations until then) since 2026-10-11 (the user's asks); Standard (Ask, Web, Shell, Options) and Databases (Database until then) since later on 2026-10-10 (the user's asks); Media and Database since 2026-10-09 (the user's call: the five media and five database tabs as menus on one tab each); the user's order since 2026-10-03; before it Camera after Ask and Docker after UNC since 2026-10-02; HA second to last since later on 2026-10-01 (the user's ask); Print since later on 2026-09-28, Home Assistant since 2026-09-28, Images since 2026-09-24, SQL since 2026-09-23, Obsidian since 2026-09-22, Options last since later that day (second from later on 2026-09-19); the user's order (Web, Files, Shell, Ask, Git (native)) since later on 2026-09-21, alphabetical before

    /// <summary>
    /// The rows of the settings menu titled <paramref name="title"/>: <see cref="SettingsMenu.ToolsTabFields"/> at <see cref="ToolsText.MenuIndex"/>,
    /// so a pin follows the menu, not its place (2026-10-03; a menu on Media or Database since 2026-10-09).
    /// </summary>
    internal static IReadOnlyList<SettingsField> TabFields(string title) => SettingsMenu.ToolsTabFields[ToolsText.MenuIndex(title) is var i and >= 0 ? i : throw new ArgumentException("no menu " + title, nameof(title))];

    /// <summary>Where <paramref name="title"/> sits on the strip: a tab's own place, or the place of the group tab that holds the menu (2026-10-09).</summary>
    internal static int TabIndex(string title) =>
        ToolsText.TabTitles.ToList().IndexOf(title) is var i and >= 0 ? i
        : ToolsText.MenuIndex(title) is var m and >= 0 ? ToolsText.TabOfMenu(m)
        : throw new ArgumentException("no tab " + title, nameof(title));

    /// <summary>Whether menu <paramref name="title"/> sits on Media or Database (2026-10-09): a sub-tab of the group since 2026-10-10.</summary>
    internal static bool InGroup(string title) => ToolsText.TabTitles.ToList().IndexOf(title) < 0 && ToolsText.IsGroupTab(TabIndex(title));

    /// <summary>
    /// The keys that walk from Offered to <paramref name="title"/>: Right presses along the strip, one per tab and, since 2026-10-10, one
    /// per menu of Media and Database (→ walks their sub-tabs), so a group's menu is its place among them and a group tab is its first menu.
    /// </summary>
    private static ConsoleKeyInfo[] ToTab(string title) => Enumerable.Repeat(Keys.Right, LeafIndex(title)).ToArray();

    /// <summary>How many → presses from Offered reach <paramref name="title"/>, a tab or a group's menu (2026-10-10).</summary>
    internal static int LeafIndex(string title)
    {
        int tab = TabIndex(title);
        int leaves = 0;
        for (int t = 1; t < tab; t++)
        {
            leaves += ToolsText.ShownMenus(t).Count;   // this system's menus (2026-10-22): a Mac's Admin has three
        }

        int within = InGroup(title) ? ToolsText.ShownMenus(tab).ToList().IndexOf(ToolsText.MenuIndex(title)) : 0;
        return tab == 0 ? 0 : leaves + 1 + within;
    }

    /// <summary>A tool row as the pane prints it at width 120 (the markup rendered): the name padded to 22, the state to 5, then the description, cut to 119 cells and an ellipsis (FittedMarkup; every description is longer).</summary>
    private string Row(string name, bool on, string mark = "  ") => Fitted(mark + name.PadRight(22) + (on ? "on" : "off").PadRight(5) + Description(name, on));

    /// <summary>A heading row as the pane prints it (2026-10-03): <paramref name="text"/> (<c>── Clock · 3</c>), a space and the rule to the fixture's width.</summary>
    /// <summary>The crumb a picker opened from a Core menu (Web, Shell, Clock, Timers, Ask, Help, Options) is titled under since later on 2026-10-10: <c>🛠️ Tools › Core</c> (Standard until later that day).</summary>
    private static string CoreCrumb => ToolsMenu.GroupCrumb(TabIndex(ToolsText.CoreTabTitle));

    private string Heading(string text) => text + " " + Rule(_console.Profile.Width - TextCells.Width(text) - 1);

    /// <summary>
    /// The heading row over the menu titled <paramref name="title"/>'s field <paramref name="field"/>, newline and all, or "" when no section
    /// starts there (2026-10-10, <see cref="SettingsMenu.ToolsMenuSections"/>).
    /// </summary>
    private string HeadingOver(string title, SettingsField field) =>
        SettingsMenu.ToolsMenuSections[ToolsText.MenuIndex(title)].FirstOrDefault(s => s.First == field).Title is { } heading ? Heading("── " + heading) + "\n" : "";

    /// <summary>
    /// The menu titled <paramref name="title"/>'s rows as the pane prints them (2026-10-10): <paramref name="rows"/>, one line per field from its
    /// first, each heading's rule put before the row its section starts at.
    /// </summary>
    private string Headed(string title, string rows)
    {
        var fields = TabFields(title);
        var lines = rows.Split('\n');
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i == lines.Length - 1 && lines[i].Length == 0)
            {
                break;   // the rows' last newline
            }

            text.Append(i < fields.Count ? HeadingOver(title, fields[i]) : "").Append(lines[i]).Append(i < lines.Length - 1 ? "\n" : "");
        }

        return text.ToString();
    }

    private static string Fitted(string row) => row.Length <= 240 ? row : row[..239] + "…";   // the fixture's width (220 until 2026-10-05; 190 until 2026-10-04; 180 until the Docker tab, 2026-10-02; 170 until the MySQL tab, 160 until the Oracle tab, 2026-09-30; 150 until the Print tab, later on 2026-09-28)

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
        Assert.Equal(14, SettingsMenu.TabFields.Count);   // Automation since 2026-10-21; Web UI since 2026-10-14; the LLM tab two (Common and Http) and Advisors since 2026-10-10; /settings' Claude and OpenAI tabs since 2026-10-03; Docker tab since 2026-10-02; the Claude (API) tab went to /tools' Claude tab on 2026-09-29; Embedded model since 2026-09-29; Claude (API) since later on 2026-09-27; Claude on 2026-09-27 until later that day (to /tools); Botchat since 2026-09-25
        Assert.Equal(30, SettingsMenu.ToolsTabFields.Count);   // Clock and Help 2026-10-18; Network, Services, Tasks and Directory later still on 2026-10-15 (the admin-tools plan); Timers later on 2026-10-15; EventLog 2026-10-15; Shopping 2026-10-11; ClaudeCode gone later on 2026-10-10 (its /claude rows to /settings' Anthropic tab); Image 2026-10-10; YouTube 2026-10-05; Screen, SQLite and Postgres 2026-10-04; Camera and Docker 2026-10-02; UNC later still on 2026-09-30; MySQL and Oracle since 2026-09-30; Print since later on 2026-09-28; Home Assistant since 2026-09-28; Claude since 2026-09-27; Images since 2026-09-24; SQL since 2026-09-23   // Obsidian since 2026-09-22   // Git since 2026-09-20, Shell since 2026-09-21; the user's order (Web, Files, Shell, Ask, Git (native)) since later on 2026-09-21, alphabetical before
        Assert.Equal(["Offered", "Core", "Files", "Apps", "Admin", "Databases", "Devices", "Home"], ToolsText.TabTitles);   // Admin later still on 2026-10-15 (the admin-tools plan); Home last and Integrations renamed Apps on 2026-10-11 (the user's asks); Core and Devices (Standard and Media until then) later still on 2026-10-10 (the user's calls); Standard (Ask, Web, Shell and Options) and Databases since later on 2026-10-10 (the user's asks); Files and Integrations since 2026-10-10 (the user's pick);   // the user's order later on 2026-10-09; Media and Database since 2026-10-09; the user's order since 2026-10-03 (ClaudeCLI "Claude" until 2026-10-04)
        Assert.Equal(["Ask", "Web", "Shell", "File", "Image", "UNC", "Print", "Camera", "Screen", "Obsidian", "SQL", "MySQL", "SQLite", "PostgreSQL", "Oracle", "Docker", "HomeAssistant", "ComfyUI", "YouTube", "GitLib", "Options", "Shopping", "EventLog", "Timers", "Network", "Services", "Tasks", "Directory", "Clock", "Help"], ToolsText.MenuTitles);   // one per ToolsTabFields list, every one a tab until 2026-10-09
        Assert.Equal(ToolsText.TabTitles.Count - 1, ToolsText.TabMenus.Count);
        Assert.Equal(Enumerable.Range(0, SettingsMenu.ToolsTabFields.Count), ToolsText.TabMenus.SelectMany(m => m).Order());   // every menu on one tab
        Assert.Equal(["Web", "Shell", "Clock", "Timers", "Ask", "Help", "Options"], ToolsText.TabMenus[TabIndex(ToolsText.CoreTabTitle) - 1].Select(m => ToolsText.MenuTitles[m]));   // Clock and Help 2026-10-18 (the user's asks); the user's order since 2026-10-16 (Network to Admin); later on 2026-10-10 (the user's asks); Timers before Options later on 2026-10-15; Shopping here on 2026-10-11 until later that day (Home)
        Assert.Equal(["Camera", "Screen", "Print"], ToolsText.TabMenus[TabIndex(ToolsText.DevicesTabTitle) - 1].Select(m => ToolsText.MenuTitles[m]));   // the user's order since 2026-10-16; ComfyUI and YouTube on Integrations since 2026-10-10
        Assert.Equal(["File", "Image", "UNC", "GitLib"], ToolsText.TabMenus[TabIndex(ToolsText.FilesTabTitle) - 1].Select(m => ToolsText.MenuTitles[m]));   // 2026-10-10; Image later that day; Obsidian to Apps on 2026-10-17
        Assert.Equal(["Docker", "ComfyUI", "YouTube", "Obsidian"], ToolsText.TabMenus[TabIndex(ToolsText.AppsTabTitle) - 1].Select(m => ToolsText.MenuTitles[m]));   // EventLog to Admin later still on 2026-10-15; Obsidian last since 2026-10-17 (the user's order)
        Assert.Equal(["Network", "EventLog", "Services", "Tasks", "Directory"], ToolsText.TabMenus[TabIndex(ToolsText.AdminTabTitle) - 1].Select(m => ToolsText.MenuTitles[m]));   // Network first since 2026-10-16 (the user's order); later still on 2026-10-15 (the admin-tools plan)   // 2026-10-10; ClaudeCode first until later that day; HomeAssistant until 2026-10-11 (Home)
        Assert.Equal(["HomeAssistant", "Shopping"], ToolsText.TabMenus[TabIndex(ToolsText.HomeTabTitle) - 1].Select(m => ToolsText.MenuTitles[m]));   // 2026-10-11 (the user's ask)
        Assert.Equal(["SQL", "MySQL", "SQLite", "PostgreSQL", "Oracle"], ToolsText.TabMenus[TabIndex(ToolsText.DatabaseTabTitle) - 1].Select(m => ToolsText.MenuTitles[m]));
        Assert.Equal([TabIndex(ToolsText.CoreTabTitle), TabIndex(ToolsText.FilesTabTitle), TabIndex(ToolsText.AppsTabTitle), TabIndex(ToolsText.AdminTabTitle), TabIndex(ToolsText.DatabaseTabTitle), TabIndex(ToolsText.DevicesTabTitle), TabIndex(ToolsText.HomeTabTitle)], Enumerable.Range(0, ToolsText.TabTitles.Count).Where(ToolsText.IsGroupTab));   // every tab but Offered since later on 2026-10-10   // Database before Media since later on 2026-10-09
        Assert.Equal([SettingsField.ToolsDollarMention, SettingsField.ToolCollapseCount, SettingsField.CodeCollapseCount, SettingsField.ShowFileDiffs, SettingsField.DiffMaxLines, SettingsField.DiffCollapseCount], TabFields(ToolsText.OptionsTabTitle));
        Assert.Equal([SettingsField.HomeAssistantTools, SettingsField.HomeAssistantUrl, SettingsField.HomeAssistantToken, SettingsField.HomeAssistantTest, SettingsField.HomeAssistantActionPolicy, SettingsField.HomeAssistantAssistAgent, SettingsField.HomeAssistantTimeoutSeconds], TabFields(ToolsText.HomeAssistantTabTitle));   // the switch, the server and its token, the test, the policy, Assist's agent, the timeout (2026-09-28)   // the fold's count under the switch (2026-09-22, the user's place), the code fold's under it
        Assert.Equal([SettingsField.PrintTools, SettingsField.PrintActionPolicy, SettingsField.PrintDefaultPrinter, SettingsField.PrintFontSize, SettingsField.PdfEngine], TabFields(ToolsText.PrintTabTitle));   // the switch, the policy, the printer, the size (later on 2026-09-28); the PDF engine (2026-10-03)
        Assert.Equal([SettingsField.WebTools, SettingsField.WebBrowserMode, SettingsField.WebBrowserPath, SettingsField.WebBrowserBotcheckWindow, SettingsField.WebBrowserHomePage, SettingsField.BrowserPrivacy, SettingsField.WebOpenLinksIn, SettingsField.WebBrowserSignInMode, SettingsField.WebBrowserNetworkMode, SettingsField.WebSearchMethod, SettingsField.WebSearxngUrl, SettingsField.WebSearchMaxResults, SettingsField.WebDownloadMaxMegabytes], TabFields(ToolsText.WebTabTitle));   // the download cap last (2026-10-01); the botcheck window under the path (2026-10-13), the home page under it (later that day)
        Assert.Equal([SettingsField.FileTools, SettingsField.FileDeletesToRecycleBin, SettingsField.FileTreeMaxLength, SettingsField.FileTreeShowSizes, SettingsField.FileMentionFolderMode, SettingsField.FileBrowserMode, SettingsField.FileViewImageMaxPerCall, SettingsField.FileSearchMaxResults], TabFields(ToolsText.FileMenuTitle));   // the five picture rows on the Image menu since 2026-10-10; the pasted pictures' folder last (2026-10-09); image_edit's three before it (2026-10-04); the search cap (2026-10-01), the view_image cap before it (2026-09-19); the browser mode under the folder mode, 2026-09-21
        Assert.Equal([SettingsField.ShellCommandPolicy, SettingsField.ShellCommandAllowed, SettingsField.ShellPolice, SettingsField.ShellPoliceForbiddenStrings, SettingsField.ShellPreferNative, SettingsField.ShellDefault, SettingsField.ShellTimeoutSeconds, SettingsField.ShellForegroundCapSeconds, SettingsField.ShellOutputMaxChars, SettingsField.ShellCodeLanguages, SettingsField.ShellCodeTimeoutSeconds, SettingsField.ShellToolBridge, SettingsField.ShellCodeMaxToolCalls], TabFields(ToolsText.ShellTabTitle));   // the policy (the switch) first, then the list, the shell, the caps, then execute_code's four (2026-09-21; the bridge switch later that day; the police toggle third, 2026-09-22; prefer native under it, 2026-09-26; the forbidden strings under the police, 2026-10-03)
        Assert.Equal([SettingsField.AskUser, SettingsField.AskMaxQuestions, SettingsField.AskMaxChoices], TabFields(ToolsText.AskTabTitle));
        Assert.Equal([SettingsField.GitLibTools, SettingsField.GitLibDiffMaxLines, SettingsField.GitLibLogMaxCommits, SettingsField.GitLibEmail, SettingsField.GitLibName], TabFields(ToolsText.GitTabTitle));   // the switch first, then the limits, then the identity pair (2026-09-21); the GitLib labels later that day
        Assert.Equal([SettingsField.ObsidianTools, SettingsField.ObsidianVault, SettingsField.ObsidianAllowDelete], TabFields(ToolsText.ObsidianTabTitle));   // the switch, then the vault (2026-09-22), then the delete switch (later that day)
        Assert.Equal([SettingsField.SqlTools, SettingsField.SqlMode, SettingsField.SqlStatementsAllowed, SettingsField.SqlConnectionsOffered, SettingsField.SqlSetPassword, SettingsField.SqlAddConnection, SettingsField.SqlPercentMention, SettingsField.SqlQueryMaxRows, SettingsField.SqlQueryTimeoutSeconds, SettingsField.QueryResultMaxChars, SettingsField.SqlConnectionsProfile, SettingsField.SqlConnectionsGlobal], TabFields(ToolsText.SqlTabTitle));   // the switch, the offered list (later that day), the default, the password prompt, the add-connection wizard and the %-mention switch (later that day), the two caps, the three engines' text cap (2026-10-01), the two edit rows (2026-09-23)
        Assert.Equal([SettingsField.OracleTools, SettingsField.OracleMode, SettingsField.OracleStatementsAllowed, SettingsField.OracleConnectionsOffered, SettingsField.OracleSetPassword, SettingsField.OracleAddConnection, SettingsField.OraclePercentMention, SettingsField.OracleQueryMaxRows, SettingsField.OracleQueryTimeoutSeconds, SettingsField.OracleConnectionsProfile, SettingsField.OracleConnectionsGlobal], TabFields(ToolsText.OracleTabTitle));   // the SQL tab's rows, in its order (2026-09-30)
        Assert.Equal([SettingsField.MySqlTools, SettingsField.MySqlMode, SettingsField.MySqlStatementsAllowed, SettingsField.MySqlConnectionsOffered, SettingsField.MySqlSetPassword, SettingsField.MySqlAddConnection, SettingsField.MySqlPercentMention, SettingsField.MySqlQueryMaxRows, SettingsField.MySqlQueryTimeoutSeconds, SettingsField.MySqlConnectionsProfile, SettingsField.MySqlConnectionsGlobal], TabFields(ToolsText.MySqlTabTitle));   // the Oracle tab's rows, in its order (later on 2026-09-30)
        Assert.Equal([SettingsField.UncTools, SettingsField.UncWrites, SettingsField.UncSharesOffered, SettingsField.UncSetPassword, SettingsField.UncAddShare, SettingsField.UncStarMention, SettingsField.UncSharesProfile, SettingsField.UncSharesGlobal], TabFields(ToolsText.UncTabTitle));   // the two switches, the offered list, the default, the password prompt, the wizard, the %-mention switch, the two edit rows (later still on 2026-09-30)
        Assert.Equal([SettingsField.DockerTools, SettingsField.DockerWrites, SettingsField.DockerEnginePipe], TabFields(ToolsText.DockerTabTitle));   // the two switches, the pipe (2026-10-02)
        Assert.Equal(OperatingSystem.IsMacOS() ? 22 : 20, SettingsMenu.LabelWidthOf(TabFields(ToolsText.DockerTabTitle)));   // "Docker engine pipe"; a Mac's "Docker engine socket" (2026-10-11)
        Assert.Equal(Enum.GetValues<SettingsField>().Order(), SettingsMenu.TabFields.Concat(SettingsMenu.SkillsTabFields).Concat(SettingsMenu.ToolsTabFields).Concat(SettingsMenu.McpTabFields).SelectMany(t => t).Order());
        Assert.Equal(21, SettingsMenu.LabelWidthOf(TabFields(ToolsText.OptionsTabTitle)));   // "Tool collapse count" (2026-09-22; "$-mention enabled", 19, before)
        Assert.Equal(29, SettingsMenu.LabelWidthOf(TabFields(ToolsText.WebTabTitle)));   // "Web browser botcheck window" (2026-10-13; "Web browser network mode", 26, from later still on 2026-09-19, the Web-prefixed labels; "Web search max results", 24, before)
        Assert.Equal([SettingsField.ImageTools, SettingsField.ImageEditQuality, SettingsField.ImageEditMetadata, SettingsField.ImageEditMode, SettingsField.ImageEditOutputFolder, SettingsField.PastedImagesFolder], TabFields(ToolsText.ImageMenuTitle));   // the switch first (2026-10-10, the picture tools' own group)   // the File menu's last five until 2026-10-10 (the user's ask), after File
        Assert.Equal(26, SettingsMenu.LabelWidthOf(TabFields(ToolsText.ImageMenuTitle)));   // "Image edit output folder"
        Assert.Equal(Math.Max(28, SettingsMenu.FieldName(SettingsField.FileDeletesToRecycleBin).Length + 2), SettingsMenu.LabelWidthOf(TabFields(ToolsText.FileMenuTitle)));   // "File deletes to Recycle bin" since later on 2026-10-15 (29; "Trash" on a Mac, 23, leaves it 28); "File view image max images" (later still on 2026-09-19; "Stale line number guard", 25, that morning; "Always return line numbers", 28, before)
        Assert.Equal(32, SettingsMenu.LabelWidthOf(TabFields(ToolsText.ShellTabTitle)));   // "Shell police forbidden strings" (2026-10-03; "Shell tool bridge max calls", 29, before) (the Shell tab, 2026-09-21; the row was "Shell code max tool calls", 27, until later that day)
        Assert.Equal(30, SettingsMenu.LabelWidthOf(TabFields(ToolsText.AskTabTitle)));   // "Ask max choices per question"
        Assert.Equal(24, SettingsMenu.LabelWidthOf(TabFields(ToolsText.GitTabTitle)));   // "GitLib log max commits" (2026-09-30; "Git native log max commits", 28, from later on 2026-09-21; "Git log max commits", 21, from 2026-09-20)
        Assert.Equal(28, SettingsMenu.LabelWidthOf(TabFields(ToolsText.SqlTabTitle)));   // "SQL query result max chars" (later on 2026-10-03; "SQL connections (profile)", 27, from 2026-09-23)
        Assert.Equal(30, SettingsMenu.LabelWidthOf(TabFields(ToolsText.OracleTabTitle)));   // "Oracle connections (profile)" (2026-09-30)
        Assert.Equal(29, SettingsMenu.LabelWidthOf(TabFields(ToolsText.MySqlTabTitle)));   // "MySQL connections (profile)" (later on 2026-09-30)
        Assert.Equal(23, SettingsMenu.LabelWidthOf(TabFields(ToolsText.UncTabTitle)));   // "UNC %-mention enabled" (later still on 2026-09-30)
        Assert.Equal(32, SettingsMenu.LabelWidthOf(TabFields(ToolsText.ObsidianTabTitle)));   // "Obsidian allow delete (.trash)" (2026-09-23; "Obsidian allow delete" from later on 2026-09-22, "Obsidian tools" that morning)
        // None refused under a reply since 2026-10-03, when the Anthropic API's four reconnect rows (on the Claude tab since 2026-09-29) and the Claude Code server's (2026-09-30) went to /settings' Claude tab.
        Assert.DoesNotContain(SettingsMenu.ToolsTabFields.SelectMany(t => t), SettingsMenu.RefusedMidTurn);
        Assert.Equal("⚙️ Settings", SettingsMenu.Title);
    }

    /// <summary>
    /// The Offered tab's footer (2026-10-05, the user's ask: the band under the list stood blank there): the cursor's tool's whole
    /// description, following the cursor; a filter keeps it on the tool shown.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheOfferedTabsFooter_SaysTheCursorsToolWhole()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Down);                                       // get_current_time → shift_date
        Push("read_file".Select(Keys.Char).ToArray());          // the filter: read_file alone
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var facts = Facts();
        foreach (string tool in new[] { GetCurrentTimeTool.ToolName, ShiftDateTool.ToolName, ReadFileTool.ToolName })
        {
            var footer = ToolsText.ToolFooter(facts, tool);
            Assert.NotNull(footer);
            Assert.Contains("\n" + MenuLayout.Footer(footer, 240), Output);   // the padding to the tallest tab goes under it
        }

        pane.Dispose();
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
        Assert.Contains("\n" + Titled(Strip) + "\n \n" + Heading("── Clock · 3") + "\n" + Row(GetCurrentTimeTool.ToolName, true, "▸ ") + "\n" + Row(ShiftDateTool.ToolName, true) + "\n" + Row(DateDifferenceTool.ToolName, true) + "\n  \n" + Heading("── Files · 13") + "\n", Output);   // Files next: alphabetical since 2026-10-04
        Assert.Contains("\n" + ToolsText.OfferedKeys + "\n", Output);
        Assert.Contains("\n" + Heading("── Files · 13") + "\n" + Row(GetWorkingDirectoryTool.ToolName, true) + "\n", Output);
        Assert.Matches(@"[ ▲]▼ \d+–\d+ of \d+", Output);   // 39 rows over 30: the list scrolls
        Assert.False(pane.OverlayOpen);
        Assert.Equal(0, pane.FlowRow);
        Assert.Empty(_settings.Current.ToolsDisabled);
        pane.Dispose();
    }

    /// <summary>The Unix twin of <see cref="OnThePane_OpensOnTheOfferedTab_OnTheFirstTool_AndEscClosesIt"/> (2026-10-06, the macOS build): the Mac's shells, languages, picture-less file tools and Keychain.</summary>
    [UnixFact]
    public async Task OnThePane_OpensOnTheOfferedTab_OnTheFirstTool_AndEscClosesIt_Unix()
    {
        var (menu, pane, _) = PaneMenu();
        _console.Profile.Height = 30;   // 39 rows since 2026-09-19 (43 with the nineteen file tools) no longer overflow the fixture's 40: shorter, so the scroll hint is still exercised
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        // The strip, the Clock heading, the cursor on get_current_time (the first tool row, past its heading), the hint with the flip keys; nothing reached the transcript.
        // The headings are rules with a gap before each but the first (2026-10-03).
        Assert.Contains("\n" + Titled(Strip) + "\n \n" + Heading("── Clock · 3") + "\n" + Row(GetCurrentTimeTool.ToolName, true, "▸ ") + "\n" + Row(ShiftDateTool.ToolName, true) + "\n" + Row(DateDifferenceTool.ToolName, true) + "\n  \n" + Heading("── Files · 13") + "\n", Output);   // Files next: alphabetical since 2026-10-04
        Assert.Contains("\n" + ToolsText.OfferedKeys + "\n", Output);
        Assert.Contains("\n" + Heading("── Files · 13") + "\n" + Row(GetWorkingDirectoryTool.ToolName, true) + "\n", Output);
        Assert.Matches(@"[ ▲]▼ \d+–\d+ of \d+", Output);   // 39 rows over 30: the list scrolls
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
        Assert.Contains("\n" + Titled(Strip) + "\n  · shift_date: off\n" + Heading("── Clock · 2 of 3") + "\n" + Row(GetCurrentTimeTool.ToolName, true) + "\n" + Row(ShiftDateTool.ToolName, false, "▸ ") + "\n", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · shift_date: on\n" + Heading("── Clock · 3") + "\n" + Row(GetCurrentTimeTool.ToolName, true) + "\n" + Row(ShiftDateTool.ToolName, true, "▸ ") + "\n", Output);
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
        Assert.Contains(MenuFilter.Caption("shift_d", 1, total), Output);
        Assert.Contains("\n" + Heading("── Clock · 3") + "\n" + Row(ShiftDateTool.ToolName, true, "▸ ") + "\n", Output);   // the one tool under its heading
        Assert.Contains("\n" + Heading("── Clock · 2 of 3") + "\n" + Row(ShiftDateTool.ToolName, false, "▸ ") + "\n", Output);
        Assert.Contains(MenuFilter.Hint(ToolsText.OfferedKeys, "shift_d"), Output);
        // Cleared: the whole tab again, the cursor back on the first tool.
        Assert.Contains("\n" + Heading("── Clock · 2 of 3") + "\n" + Row(GetCurrentTimeTool.ToolName, true, "▸ ") + "\n" + Row(ShiftDateTool.ToolName, false) + "\n", Output);
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

        Assert.DoesNotContain(MenuFilter.Caption("z", 0, Facts().Groups.Sum(g => g.Tools.Count)), Output);
        Assert.DoesNotContain(MenuFilter.NoMatchLine("z"), Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheCursorNeverRestsOnAHeadingOrAGap()
    {
        // A heading is no stop (2026-10-03): Up from the first tool wraps past the Clock heading to the last tool, Down from
        // the last clock row steps over the gap and the Files heading (the groups alphabetical since 2026-10-04), Home is the first tool again.
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Up, Keys.Enter);                // download_file, the last row (Web last)
        Push(Keys.Home, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // get_working_directory
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([DownloadFileTool.ToolName, GetWorkingDirectoryTool.ToolName], _settings.Current.ToolsDisabled);
        Assert.DoesNotContain("▸ ──", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AGroupOff_ShowsDimWithTheSwitchNamed_AndStillFlips()
    {
        _settings.Update(d => d.FileTools = false);
        var (menu, pane, _) = PaneMenu();
        Down(3);                                  // past the two other clock rows (the gap and heading no stops, 2026-10-03; Files next since 2026-10-04): get_working_directory
        Push(Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([GetWorkingDirectoryTool.ToolName], _settings.Current.ToolsDisabled);
        Assert.Contains("\n" + Heading("── Files · 13 ── off: File tools is off") + "\n", Output);
        Assert.Contains("  · get_working_directory: off\n", Output);
        Assert.Contains("\n" + Heading("── Files · 12 of 13 ── off: File tools is off") + "\n" + Row(GetWorkingDirectoryTool.ToolName, false, "▸ ") + "\n", Output);
        pane.Dispose();
    }

    /// <summary>The Unix twin of <see cref="OnThePane_AGroupOff_ShowsDimWithTheSwitchNamed_AndStillFlips"/> (2026-10-06, the macOS build): the Mac's shells, languages, picture-less file tools and Keychain.</summary>
    [UnixFact]
    public async Task OnThePane_AGroupOff_ShowsDimWithTheSwitchNamed_AndStillFlips_Unix()
    {
        _settings.Update(d => d.FileTools = false);
        var (menu, pane, _) = PaneMenu();
        Down(3);                                  // past the two other clock rows (the gap and heading no stops, 2026-10-03; Files next since 2026-10-04): get_working_directory
        Push(Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([GetWorkingDirectoryTool.ToolName], _settings.Current.ToolsDisabled);
        Assert.Contains("\n" + Heading("── Files · 13 ── off: File tools is off") + "\n", Output);
        Assert.Contains("  · get_working_directory: off\n", Output);
        Assert.Contains("\n" + Heading("── Files · 12 of 13 ── off: File tools is off") + "\n" + Row(GetWorkingDirectoryTool.ToolName, false, "▸ ") + "\n", Output);
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
        Assert.Contains("\n" + Titled(Strip) + "\n \n" + Fitted("  " + ToolsText.OffLine) + "\n" + Heading("── Clock · 3") + "\n" + Row(GetCurrentTimeTool.ToolName, true, "▸ ") + "\n", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · get_current_time: off\n" + Fitted("  " + ToolsText.OffLine) + "\n" + Heading("── Clock · 2 of 3") + "\n" + Row(GetCurrentTimeTool.ToolName, false, "▸ ") + "\n", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheOptionsTab_IsTheLastTab_ItsToggleSaves()
    {
        // The $-mention switch (later on 2026-09-19): the pane's own row, last on the strip in the /skills shape (second until later on 2026-09-22).
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.OptionsTabTitle));                 // Standard's last menu since later on 2026-10-10 (the strip's last tab, Offered ← wrapping to it, until then)
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // $-mention enabled: the page, off picked
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.ToolsDollarMention);
        AssertTabEnds(Strips(ToolsText.CoreTabTitle) + Headed(ToolsText.OptionsTabTitle, "▸ $-mention enabled    on\n  Tool collapse count  2 lines\n  Code collapse count  20 lines\n  Show file diffs      on\n  Diff max lines       10 lines\n  Diff collapse count  10 lines\n"), 100);
        Assert.Contains(CoreCrumb + " › $-mention enabled", Output);
        Assert.Contains("$ is ordinary text", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n" + SubStrip(ToolsText.CoreTabTitle) + "\n  · $-mention enabled: off\n" + Heading("── Mentions") + "\n▸ $-mention enabled    off\n", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheTimersTab_SitsAfterClock_ItsToggleSaves()
    {
        // Timer tools (later on 2026-10-15, the user's ask): Core's third menu since 2026-10-16 (the user's order), after Shell; after Clock since 2026-10-18; on by default.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.TimersMenuTitle));
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // Timer tools: the page, off picked
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.TimerTools);
        Assert.Contains("timer tools disabled", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n" + SubStrip(ToolsText.CoreTabTitle) + "\n  · Timer tools: off\n" + Heading("── Timers") + "\n▸ Timer tools  off\n", Output);
        Assert.Contains("↳ Web · Shell · Clock · Timers · Ask · Help · Options", SubStrip(ToolsText.CoreTabTitle));   // Clock and Help since 2026-10-18; the user's order since 2026-10-16 (Network on Admin)
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheTimersTab_TimerTone_ListsTheFive_PreviewsTheRowUnderTheCursor_AndEnterSaves()
    {
        // Timer tone (2026-10-18, the user's ask): under Timer tools; silent by default; a cursor move plays the row, opening and Enter play nothing.
        var (menu, pane, settings) = PaneMenu();
        var previewed = new List<string>();
        settings.PreviewTone = (name, _) => { previewed.Add(name); return Task.CompletedTask; };
        Push(ToTab(ToolsText.TimersMenuTitle));
        Push(Keys.Down, Keys.Enter);                            // Timer tone: the picker, silent under the cursor
        Push(Keys.Down, Keys.Down, Keys.Up, Keys.Enter);        // chime, bell, chime: picked
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(TimerTones.Chime, _settings.Current.TimerTone);
        Assert.Equal(TimerTones.Chime, previewed[^1]);                 // the row the cursor stopped on (a burst of arrows moves once)
        Assert.DoesNotContain(TimerTones.Silent, previewed);            // never the opening row
        Assert.Contains("\n▸ Timer tools  on\n  Timer tone   silent\n", Output);
        Assert.Contains("\n  Timer tone   chime\n", Output.Replace("▸ Timer tone ", "  Timer tone ", StringComparison.Ordinal));
        foreach (string name in TimerTones.Names)
        {
            Assert.Contains(name.PadRight(9) + TimerTones.Describe(name), Output);
        }

        Assert.Contains("\n▸ silent   " + TimerTones.Describe(TimerTones.Silent), Output);   // the saved one under the cursor
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheClockTab_SitsAfterShell_ItsToggleSaves()
    {
        // Clock tools (2026-10-18, the user's ask): Core's third menu, after Shell, before Timers; on by default.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ClockMenuTitle));
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // Clock tools: the page, off picked
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.ClockTools);
        Assert.Contains("clock tools disabled", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n" + SubStrip(ToolsText.CoreTabTitle) + "\n  · Clock tools: off\n" + Heading("── Clock") + "\n▸ Clock tools  off\n", Output);
        Assert.Equal(LeafIndex(ToolsText.ShellTabTitle) + 1, LeafIndex(ToolsText.ClockMenuTitle));
        Assert.Equal(LeafIndex(ToolsText.ClockMenuTitle) + 1, LeafIndex(ToolsText.TimersMenuTitle));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheHelpTab_SitsAfterAsk_ItsToggleSaves()
    {
        // Help tools (2026-10-18, the user's ask): Core's sixth menu, after Ask, before Options; on by default.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.HelpMenuTitle));
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // Help tools: the page, off picked
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.HelpTools);
        Assert.Contains("help tools disabled", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n" + SubStrip(ToolsText.CoreTabTitle) + "\n  · Help tools: off\n" + Heading("── Help") + "\n▸ Help tools  off\n", Output);
        Assert.Equal(LeafIndex(ToolsText.AskTabTitle) + 1, LeafIndex(ToolsText.HelpMenuTitle));
        Assert.Equal(LeafIndex(ToolsText.HelpMenuTitle) + 1, LeafIndex(ToolsText.OptionsTabTitle));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheAskTab_SitsAfterTimers_ItsToggleSaves()
    {
        // The user's order since 2026-10-16: Core is Web, Shell, Timers, Ask, Options (Clock after Shell and Help after Ask since 2026-10-18; Ask right after Offered from 2026-10-03; between Shell and Git (native) from later on 2026-09-21).
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.AskTabTitle));
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // Ask user: the page, off picked
        Push(Enumerable.Repeat(Keys.Right, LeafIndex(ToolsText.FileMenuTitle) - LeafIndex(ToolsText.AskTabTitle)).ToArray());   // Options, then Files (File)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.AskUser);
        // The three rows padded to the tab's own column (30), the toggle's notice on the status line under the strip.
        AssertTabEnds(Strips(ToolsText.CoreTabTitle) + Headed(ToolsText.AskTabTitle, "▸ Ask user                      on\n  Ask max questions             10 questions\n  Ask max choices per question  10 choices\n"), 100);
        Assert.Contains("\n" + Titled(Strip) + "\n" + SubStrip(ToolsText.CoreTabTitle) + "\n  · Ask user: off\n" + Heading("── Questions") + "\n▸ Ask user                      off\n", Output);
        Assert.Contains("\n▸ " + FileRow("File tools", "on") + "\n", Output);
        pane.Dispose();
    }

    /// <summary>A File menu row: its label padded to the menu's column (the widest label and two), then its value.</summary>
    private static string FileRow(string label, string value) =>
        label.PadRight(TabFields(ToolsText.FileMenuTitle).Max(f => SettingsMenu.FieldName(f).Length) + 2) + value;

    [Fact]
    public async Task OnThePane_TheFilesTab_SitsBetweenShellAndUnc()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.FilesTabTitle));                   // Ask, Web, Shell, Files (the user's order since 2026-10-03)
        Push(Keys.Enter, Keys.Down, Keys.Enter);                // File tools: the page, off picked
        Push(Keys.Right, Keys.Right, Keys.Right);               // Image, UNC, GitLib (the Files group's menus since 2026-10-10, Image later that day; after UNC since later on 2026-10-09; six left, wrapping past Options, until then)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.FileTools);
        // The six rows (the view_image cap last, 2026-09-19; the @-mention folder mode before it, 2026-09-17, and the browser mode under that, 2026-09-21; Safe edits gone since 2026-10-01, folder-remain the default since then, Always return line numbers gone later that day and the stale line number guard later still, with edit_lines) padded to the tab's own column (32), the toggle's notice on the status line under the strip.
        // File deletes to Recycle bin second since later on 2026-10-15, the column as wide as its name ("Trash" on a Mac).
        AssertTabEnds(Strips(ToolsText.FilesTabTitle) + Headed(ToolsText.FileMenuTitle, "▸ " + FileRow("File tools", "on") + "\n  " + FileRow(SettingsMenu.FieldName(SettingsField.FileDeletesToRecycleBin), "on") + "\n  " + FileRow("File /tree max entries", "500 entries") + "\n  " + FileRow("File /tree show sizes", "on") + "\n  " + FileRow("File @-mention folder mode", "remain") + "\n  " + FileRow("File browser/tree mode", "hide-hidden") + "\n  " + FileRow("File view image max images", "10 pictures") + "\n  " + FileRow("File search max results", "200 results") + "\n"), 100);
        Assert.Contains("\n" + Titled(Strip) + "\n" + SubStrip(ToolsText.FilesTabTitle) + "\n  · File tools: off\n" + Heading("── Files") + "\n▸ " + FileRow("File tools", "off") + "\n", Output);
        Assert.Contains("\n▸ GitLib tools            on\n", Output);
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
        Assert.Contains("Obsidian vault " + SettingsMenu.ObsidianVaultError[..40], Output);   // the status line, cut at the pane's width
        Assert.Contains(Strips(ToolsText.AppsTabTitle) + Headed(ToolsText.ObsidianTabTitle, "▸ Obsidian tools                  on\n  Obsidian vault                  (not set)\n  Obsidian allow delete (.trash)  on\n"), Output);
    }

    /// <summary>
    /// <c>SQL set password</c> (later on 2026-09-23): the connections that take a password with their store, then a masked
    /// slot under the picked one; the password lands encrypted in the connection's <c>sql.json</c> and never on screen.
    /// </summary>
    [WindowsFact]
    public async Task OnThePane_TheSqlTab_SetsAPassword_Masked_IntoTheConnectionsStore()
    {
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, """{ "connections": { "prod": { "server": "x", "database": "db", "auth": "runas", "user": "CONTOSO\\svc-test" }, "mine": { "server": "y", "database": "db", "auth": "windows" } } }""");
        var (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter]);   // SQL, the set-password row (right under the offered list since the default row went, 2026-10-09): the pick
        Push(Keys.Enter);                                               // prod, the one connection that takes a password
        Push([.. "s3cret".Select(Keys.Char), Keys.Enter]);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var loaded = NeonSidekick.Sql.SqlConfigFile.Load(path);
        Assert.StartsWith(NeonSidekick.Sql.WindowsCredentials.ProtectedPrefix, loaded.Connections[0].Config.Password);
        Assert.Equal("s3cret", NeonSidekick.Sql.SqlSecrets.Resolve(loaded.Connections[0]).Value);
        Assert.Contains("▸ " + SettingsMenu.SqlPasswordRow(loaded.Connections[0]), Output);
        Assert.Contains("••••••", Output);
        Assert.DoesNotContain("s3cret", Output);
        Assert.Contains("Saved the password of 'prod', encrypted, in ", Output);
        Assert.Equal("prod  (runas CONTOSO\\svc-test · encrypted in sql.json)", SettingsMenu.SqlPasswordRow(loaded.Connections[0]));
    }

    /// <summary>The Unix twin of <see cref="OnThePane_TheSqlTab_SetsAPassword_Masked_IntoTheConnectionsStore"/> (2026-10-06, the macOS build): a <c>keychain:</c> value, where Windows writes <c>dpapi:</c>.</summary>
    [UnixFact]
    public async Task OnThePane_TheSqlTab_SetsAPassword_Masked_IntoTheConnectionsStore_Unix()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, """{ "connections": { "prod": { "server": "x", "database": "db", "auth": "runas", "user": "CONTOSO\\svc-test" }, "mine": { "server": "y", "database": "db", "auth": "windows" } } }""");
        var (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter]);   // SQL, the set-password row (right under the offered list since the default row went, 2026-10-09): the pick
        Push(Keys.Enter);                                               // prod, the one connection that takes a password
        Push([.. "s3cret".Select(Keys.Char), Keys.Enter]);
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

        await menu.ShowAsync(CancellationToken.None);

        var loaded = NeonSidekick.Sql.SqlConfigFile.Load(path);
        Assert.StartsWith(NeonSidekick.Sql.WindowsCredentials.KeychainPrefix, loaded.Connections[0].Config.Password);
        Assert.Equal("s3cret", NeonSidekick.Sql.SqlSecrets.Resolve(loaded.Connections[0]).Value);
        Assert.Contains("▸ " + SettingsMenu.SqlPasswordRow(loaded.Connections[0]), Output);
        Assert.Contains("••••••", Output);
        Assert.DoesNotContain("s3cret", Output);
        Assert.Contains("Saved the password of 'prod', encrypted, in ", Output);
        Assert.Equal("prod  (runas CONTOSO\\svc-test · encrypted in sql.json)", SettingsMenu.SqlPasswordRow(loaded.Connections[0]));
    }

    /// <summary>The Oracle tab, row <paramref name="row"/> (2026-09-30): the switch 0, mode 1, statements 2 (2026-10-05), offered 3, password 4, wizard 5 (the default row went 2026-10-09).</summary>
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
        OpenOracleRow(5);
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
        Push(Keys.Enter);                         // read, the default access (2026-10-05)
        Type("the container");
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows, always both since 2026-10-01)
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

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
        Assert.Contains("\"user\" is 'sys'; SYS", Output);
        Assert.Contains(NeonSidekick.Oracle.OracleText.TestOk("free", "NEON", "FREEPDB1", "23.0.0.0.0"), Output);
        Assert.Contains("This account can change data (CREATE TABLE, owns 3 tables)", Output);
        Assert.Contains("Added 'free' to ", Output);
        Assert.Contains(SettingsMenu.SqlWizardMasked, Output);
        Assert.DoesNotContain("s3cret", Output);
        Assert.Equal(["free"], _settings.Current.OracleConnectionsOffered);   // offered by the wizard into a list nothing was in
    }

    /// <summary>The wizard's ESC on its first page writes nothing; a bad schema asks again; a narrowed profile's first save row offers the new one.</summary>
    [Fact]
    public async Task OnThePane_TheOracleWizard_ChecksTheSchema_OffersIntoANarrowedProfile_AndEscWritesNothing()
    {
        _settings.Update(d => d.OracleConnectionsOffered = ["other"]);
        string home = NeonSidekick.Oracle.OracleConfigFile.GlobalPath(_settings.StorageDirectory);
        var (menu, _, _) = PaneMenu(testOracle: (c, _) => Task.FromResult(NeonSidekick.Sql.SqlRun.Refused(NeonSidekick.Sql.SqlOutcome.ConnectFailed, "ORA-12541: no listener", c.Name)));
        OpenOracleRow(5);
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
        Push(Keys.Down, Keys.Enter);              // readwrite (2026-10-05), while Oracle mode is read-only
        Push(Keys.Enter);                         // no description
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test: the failure on the status line, still saveable
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

        string target = "NeonSidekick/oracle/ledger";
        try
        {
            await menu.ShowAsync(CancellationToken.None);

            Assert.Contains(SettingsMenu.SqlWizardCancelledNotice, Output);
            Assert.Contains(NeonSidekick.Oracle.OracleText.BadSchemaKey("not a name"), Output);
            Assert.Contains("Error: could not connect to ledger: ORA-12541: no listener", Output);
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
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, """{ "connections": { "free": { "dataSource": "x:1521/y", "user": "neon" } } }""");
        var (menu, _, _) = PaneMenu();
        OpenOracleRow(4);
        Push(Keys.Enter);                                               // free
        Push([.. "s3cret".Select(Keys.Char), Keys.Enter]);
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

        await menu.ShowAsync(CancellationToken.None);

        var loaded = NeonSidekick.Oracle.OracleConfigFile.Load(path);
        Assert.Equal("s3cret", NeonSidekick.Oracle.OracleSecrets.Resolve(loaded.Connections[0]).Value);
        Assert.Contains("▸ " + SettingsMenu.OraclePasswordRow(loaded.Connections[0]), Output);
        Assert.DoesNotContain("s3cret", Output);
        Assert.Contains("Saved the password of 'free', encrypted, in ", Output);
        Assert.Equal("free  (user neon · encrypted in oracle.json)", SettingsMenu.OraclePasswordRow(loaded.Connections[0]));
    }

    [Fact]
    public async Task OnThePane_TheOracleTab_NarrowsTheOfferedConnections()
    {
        string path = NeonSidekick.Oracle.OracleConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, """{ "connections": { "free": { "dataSource": "x:1521/y", "user": "u" }, "ledger": { "dataSource": "z:1521/l", "user": "u" } } }""");
        var (menu, _, _) = PaneMenu();
        OpenOracleRow(3);
        Push(Keys.Down, Keys.Enter);                         // ledger on (nothing is ticked until the user ticks it)
        Push(Keys.Escape);
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["ledger"], _settings.Current.OracleConnectionsOffered);
        Assert.Contains("Oracle connections offered    none of 2", Output);
        Assert.Contains("[ ] free", Output);
        Assert.Equal("1 of 2", SettingsMenu.OracleOfferedValue(_settings.Current.OracleConnectionsOffered, NeonSidekick.Oracle.OracleConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory)));
    }

    /// <summary>The MySQL tab, row <paramref name="row"/> (later on 2026-09-30): the switch 0, mode 1, statements 2 (2026-10-05), offered 3, password 4, wizard 5 (the default row went 2026-10-09).</summary>
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
        OpenMySqlRow(5);
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
        Push(Keys.Enter);                         // read, the default access (2026-10-05)
        Type("the sample shop");
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows, always both since 2026-10-01)
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("s3cret", Assert.Single(tested).Config.Password);
        var shop = Assert.Single(NeonSidekick.MySql.MySqlConfigFile.Load(path).Connections);
        Assert.Equal(("localhost", 3307, "shop", "neon", "verify-full"), (shop.Config.Host, shop.Config.Port, shop.Config.Database, shop.Config.User, shop.Config.SslMode));
        Assert.Equal("s3cret", NeonSidekick.MySql.MySqlSecrets.Resolve(shop).Value);
        Assert.Contains(SettingsMenu.MySqlWizardPortError("99999"), Output);
        Assert.Contains(NeonSidekick.MySql.MySqlText.TestOk("shop", "neon@%", "8.4.11"), Output);
        Assert.Contains("This account can change data (ALL PRIVILEGES ON `shop`.*); the MySQL tools only read", Output);
        Assert.Null(shop.Config.Access);   // read is the default, left out of the file
        Assert.Contains("Added 'shop' to ", Output);
        Assert.Equal(["shop"], _settings.Current.MySqlConnectionsOffered);
        Assert.DoesNotContain("s3cret", Output);
    }

    [Fact]
    public async Task OnThePane_TheMySqlTab_SetsAPassword_AndNarrowsTheOfferedConnections()
    {
        string path = NeonSidekick.MySql.MySqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, """{ "connections": { "shop": { "host": "localhost", "database": "db", "user": "reader" }, "billing": { "host": "db", "database": "db", "user": "ro" } } }""");
        var (menu, _, _) = PaneMenu();
        OpenMySqlRow(4);
        Push(Keys.Down, Keys.Enter);                         // shop, under billing (A to Z since 2026-10-11)
        Push([.. "s3cret".Select(Keys.Char), Keys.Enter]);
        Push(Keys.Up, Keys.Enter);                           // the offered row, right above: the checklist
        Push(Keys.Enter);                                    // billing on, the first row
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var loaded = NeonSidekick.MySql.MySqlConfigFile.Load(path);
        Assert.Equal("s3cret", NeonSidekick.MySql.MySqlSecrets.Resolve(loaded.Connections[0]).Value);
        Assert.Equal("shop  (localhost:3306, user reader · encrypted in mysql.json)", SettingsMenu.MySqlPasswordRow(loaded.Connections[0]));
        Assert.Equal(["billing"], _settings.Current.MySqlConnectionsOffered);
        Assert.DoesNotContain("s3cret", Output);
    }

    /// <summary>Types <paramref name="text"/> into the open slot and submits it.</summary>
    private void Type(string text) => Push([.. text.Select(Keys.Char), Keys.Enter]);

    /// <summary>The UNC tab, row <paramref name="row"/> (later still on 2026-09-30): the switch 0, writes 1, offered 2, password 3, wizard 4 (the default row went 2026-10-09).</summary>
    private void OpenUncRow(int row) => Push([.. ToTab(ToolsText.UncTabTitle), .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    /// <summary>
    /// <c>UNC add share</c> (later still on 2026-09-30): a share walked through every page into the profile's file — a relative path
    /// asked again, windows sign-in skipping the account pages, readwrite noted while UNC writes is off — the test listing the root
    /// of the unsaved draft, then saved, the defaults left out of the file.
    /// </summary>
    [WindowsFact]
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
        OpenUncRow(4);
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
        Assert.Contains(NeonSidekick.Unc.UncText.NotAbsolute(@"specs\only"), Output);
        Assert.Contains(SettingsMenu.UncWizardWritesOffNotice, Output);
        Assert.Contains(SettingsMenu.UncWizardTestOkNotice("eng", 7), Output);
        Assert.Contains("Added 'eng' to ", Output);
        Assert.Equal(["eng"], _settings.Current.UncSharesOffered);
        Assert.Equal("Reached 'eng': 7 entries at its root.", SettingsMenu.UncWizardTestOkNotice("eng", 7));
    }

    /// <summary><c>UNC add share</c> as another account: an account without a domain asked again, the password typed masked, kept encrypted in the file; a failed test still lets it be saved; ESC out of the first page writes nothing.</summary>
    [WindowsFact]
    public async Task OnThePane_TheUncWizard_RunAs_KeepsThePasswordEncrypted_AndEscWritesNothing()
    {
        string path = NeonSidekick.Unc.UncConfigFile.ProfilePath(_settings.ProfileDirectory);
        var (menu, _, _) = PaneMenu(testUnc: (s, _) => Task.FromResult(NeonSidekick.Unc.UncResult<int>.Failed(NeonSidekick.Unc.UncText.BadAccount(s.Name, s.Config.User!))));
        OpenUncRow(4);
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
        Assert.Contains(NeonSidekick.Sql.SqlText.RunAsNeedsDomain("svc"), Output);
        Assert.Contains(NeonSidekick.Unc.UncText.BadAccount("fin", @"CORP\svc"), Output);
        Assert.Contains(SettingsMenu.UncWizardCancelledNotice, Output);
        Assert.DoesNotContain("s3cret", Output);
        Assert.Null(_settings.Current.UncSharesOffered);   // saved hidden: nothing offered
    }

    [WindowsFact]
    public async Task OnThePane_TheUncTab_FlipsWrites_SetsARunAsPassword_AndNarrowsTheOfferedShares()
    {
        string path = NeonSidekick.Unc.UncConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, """{ "shares": { "eng": { "path": "//fs01/eng" }, "fin": { "path": "//fs02/fin", "auth": "runas", "user": "CORP\\svc" } } }""");
        var (menu, _, _) = PaneMenu();
        OpenUncRow(3);                                       // set password: only fin takes one
        Push(Keys.Enter);                                    // fin
        Push([.. "s3cret".Select(Keys.Char), Keys.Enter]);
        Push(Keys.Up, Keys.Enter);                           // the offered row, right above: the checklist
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
        Assert.DoesNotContain("s3cret", Output);
        Assert.Equal("1 of 2", SettingsMenu.UncOfferedValue(["fin"], NeonSidekick.Unc.UncConfigFile.Load(path)));
        Assert.Equal("none of 2", SettingsMenu.UncOfferedValue(null, NeonSidekick.Unc.UncConfigFile.Load(path)));
        Assert.Equal("2 shares · Enter edits unc.json", SettingsMenu.UncSharesLabel(path));
        Assert.Equal("Enter to set password for a runas share", SettingsMenu.UncSetPasswordLabel);
        Assert.Equal("Enter to start share wizard", SettingsMenu.UncAddShareLabel);
    }

    /// <summary><c>UNC shares offered</c> and <c>UNC set password</c> list A to Z (2026-10-11, the user's ask), not the file's order: <c>Alpha</c> first in both.</summary>
    [Fact]
    public async Task OnThePane_TheUncTab_ListsTheSharesAToZ()
    {
        string path = NeonSidekick.Unc.UncConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, """{ "shares": { "zeta": { "path": "//fs01/zeta", "auth": "runas", "user": "CORP\\svc" }, "Alpha": { "path": "//fs02/alpha", "auth": "runas", "user": "CORP\\svc" } } }""");
        var (menu, _, _) = PaneMenu();
        OpenUncRow(3);                                       // set password: both take one
        Push(Keys.Down, Keys.Enter);                         // zeta, under Alpha
        Push([.. "s3cret".Select(Keys.Char), Keys.Enter]);
        Push(Keys.Up, Keys.Enter);                           // the offered row, right above: the checklist
        Push(Keys.Enter);                                    // Alpha on, the first row
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var zeta = NeonSidekick.Unc.UncConfigFile.Load(path).Shares.Single(s => s.Name == "zeta");
        Assert.Equal("s3cret", NeonSidekick.Unc.UncSecrets.Resolve(zeta).Value);
        Assert.Equal(["Alpha"], _settings.Current.UncSharesOffered);
    }

    /// <summary>The SQL tab, the add-connection row (the sixth, under the password prompt; the default row went 2026-10-09): the wizard.</summary>
    private void OpenSqlWizard() => Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter]);

    /// <summary>
    /// <c>SQL add connection</c> (later on 2026-09-23, the user's ask): a SQL login walked through every page into the profile's
    /// file, the test run over the unsaved draft (its password handed over plain, nothing written), then saved — the entry in the
    /// file, the password encrypted after it, never on screen.
    /// </summary>
    [WindowsFact]
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
        Push(Keys.Enter);                         // read, the default access (2026-10-05)
        Type("the sample");
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows, always both since 2026-10-01)
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

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
        Assert.Contains(SettingsMenu.SqlWizardTestOkNotice("aw", "Microsoft SQL Server 2022 (RTM) - 16.0"), Output);
        Assert.Contains("Added 'aw' to ", Output);   // the status line cuts the temp path at the pane's width
        Assert.Equal("Added 'aw' to " + path + ".", NeonSidekick.Sql.SqlText.ConnectionAdded("aw", path));
        Assert.Contains(SettingsMenu.SqlWizardSummaryCaption, Output);
        Assert.Contains(SettingsMenu.SqlWizardMasked, Output);
        Assert.DoesNotContain("s3cret", Output);
        Assert.Equal(["aw"], _settings.Current.SqlConnectionsOffered);   // offered by the wizard (nothing is until ticked, 2026-10-01)
    }

    /// <summary>
    /// The test's reach report (2026-10-09, the lock to the connection's database): the other databases the login could open, as
    /// <c>SqlCatalogQueries.OtherDatabases</c>' second result set brings them, warned of by name; none, and the notice says the server
    /// holds the login to its database too.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheSqlWizardsTest_SaysWhatElseTheLoginCouldOpen()
    {
        var others = new List<string[]> { new[] { "HR" }, new[] { "Payroll" } };
        var (menu, _, _) = PaneMenu(testSql: (c, _) => Task.FromResult(new NeonSidekick.Sql.SqlRun(NeonSidekick.Sql.SqlOutcome.Ok, "", c.Name, "", [new NeonSidekick.Sql.SqlGrid(["v"], [["v1"]], false), new NeonSidekick.Sql.SqlGrid(["database"], others.ToList(), false)], TimeSpan.Zero)));
        OpenSqlWizard();
        Push(Keys.Enter);                         // the profile's file
        Type("aw");
        Type("sqlhost01");
        Type("Sales");
        Push(Keys.Down, Keys.Enter);              // windows
        Push(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);   // mandatory, no, the default timeout, read, no description
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test: two others
        Push(Keys.Down, Keys.Enter);              // Cancel
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("The login could also open HR, Payroll (and", Output);
        Assert.Equal("The login could also open HR, Payroll (and the server's own master, tempdb and msdb). The tools keep to Sales; a login that opens only it is the lock the server keeps too.", NeonSidekick.Sql.SqlText.Reach("Sales", ["HR", "Payroll"]));
        Assert.Equal("The login opens no other user database than Sales (the server's own master, tempdb and msdb aside).", NeonSidekick.Sql.SqlText.Reach("Sales", []));
        Assert.EndsWith(" and 2 more (and the server's own master, tempdb and msdb). The tools keep to Sales; a login that opens only it is the lock the server keeps too.", NeonSidekick.Sql.SqlText.Reach("Sales", Enumerable.Range(1, 12).Select(i => "db" + i).ToList()));
    }

    /// <summary>The Unix twin of <see cref="OnThePane_TheSqlWizard_WalksASqlLogin_TestsTheDraft_AndSavesIt"/> (2026-10-06, the macOS build): a <c>keychain:</c> value, where Windows writes <c>dpapi:</c>.</summary>
    [UnixFact]
    public async Task OnThePane_TheSqlWizard_WalksASqlLogin_TestsTheDraft_AndSavesIt_Unix()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

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
        Push(Keys.Enter);                         // read, the default access (2026-10-05)
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
        Assert.StartsWith(NeonSidekick.Sql.WindowsCredentials.KeychainPrefix, aw.Config.Password);
        Assert.Equal("s3cret", NeonSidekick.Sql.SqlSecrets.Resolve(aw).Value);
        Assert.StartsWith(NeonSidekick.Sql.SqlConfigFile.EmptyText[..40], File.ReadAllText(path));   // made with its commented shape
        Assert.Contains(SettingsMenu.SqlWizardTestOkNotice("aw", "Microsoft SQL Server 2022 (RTM) - 16.0"), Output);
        Assert.Contains("Added 'aw' to ", Output);   // the status line cuts the temp path at the pane's width
        Assert.Equal("Added 'aw' to " + path + ".", NeonSidekick.Sql.SqlText.ConnectionAdded("aw", path));
        Assert.Contains(SettingsMenu.SqlWizardSummaryCaption, Output);
        Assert.Contains(SettingsMenu.SqlWizardMasked, Output);
        Assert.DoesNotContain("s3cret", Output);
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
        Type("Reports");                          // the database: required since 2026-10-09, Enter on a blank one does nothing
        Push(Keys.Down, Keys.Enter);              // windows: no user, store or password page follows
        Push(Keys.Enter);                         // mandatory
        Push(Keys.Enter);                         // no
        Type("abc");
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        Type("30");
        Push(Keys.Down, Keys.Enter);              // readwrite (2026-10-05), while SQL mode is read-only
        Push(Keys.Enter);                         // no description
        Push(Keys.Enter);                         // Save, and offer it
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var me = Assert.Single(NeonSidekick.Sql.SqlConfigFile.Load(path).Connections);
        Assert.Equal("windows", me.Config.Auth);
        Assert.Null(me.Config.User);
        Assert.Null(me.Config.PasswordStore);
        Assert.Null(me.Config.Password);
        Assert.Equal("Reports", me.Config.Database);
        Assert.Equal(30, me.Config.ConnectTimeoutSeconds);
        Assert.Equal("readwrite", me.Config.Access);
        Assert.Contains(SettingsMenu.DatabaseWizardModeOffNotice(NeonSidekick.Sql.SqlStatementKinds.Family), Output);
        Assert.Equal(["other", "me"], _settings.Current.SqlConnectionsOffered);
        Assert.Contains(SettingsMenu.SqlWizardTimeoutError("abc"), Output);
        Assert.Contains(SettingsMenu.SqlWizardSaveHiddenRow, Output);
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

        Assert.Contains(SettingsMenu.SqlWizardCancelledNotice, Output);
        Assert.DoesNotContain(SettingsMenu.SqlWizardNameRequired, Output);
        Assert.False(File.Exists(NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory)));
    }

    /// <summary>
    /// A name the file has is refused, a runas account without a domain too; a failed test says the server's words and still
    /// leaves the choice; Cancel writes nothing (a Credential Manager draft never reaches the store).
    /// </summary>
    [WindowsFact]   // Windows' account forms; a Mac's runas takes name@REALM alone: the _Mac twin (2026-10-11)
    public async Task OnThePane_TheSqlWizard_RefusesATakenName_AndARunAsWithoutDomain_AndCancelWritesNothing()
    {
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        const string Before = """{ "connections": { "aw": { "server": "x", "database": "db", "auth": "windows" } } }""";
        File.WriteAllText(path, Before);
        var (menu, _, _) = PaneMenu(testSql: (c, _) => Task.FromResult(NeonSidekick.Sql.SqlRun.Refused(NeonSidekick.Sql.SqlOutcome.ConnectFailed, "Login failed for user.", c.Name)));
        OpenSqlWizard();
        Push(Keys.Enter);                         // + New connection: "aw" is saved, so the first page lists it (2026-10-05)
        Push(Keys.Enter);
        Type("AW");                               // taken, whatever the case
        Push(Keys.Backspace, Keys.Backspace);
        Type("rep");
        Type("sqlhost01");
        Type("Reports");
        Push(Keys.Down, Keys.Down, Keys.Enter);   // runas
        Type("svc");                              // no domain
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        Type(@"CONTOSO\svc");
        Push(Keys.Down, Keys.Enter);              // credman
        Type("pw");
        Push(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);   // mandatory, no, the default timeout, read, no description
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows): refused
        Push(Keys.Down, Keys.Enter);              // Cancel
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("'AW' is already in ", Output);
        Assert.Contains(NeonSidekick.Sql.SqlText.RunAsNeedsDomain("svc"), Output);
        Assert.Contains("Error: could not connect to rep: Login failed for user.", Output);
        Assert.Contains(SettingsMenu.SqlWizardCancelledNotice, Output);
        Assert.Equal(Before, File.ReadAllText(path));
    }

    /// <summary>
    /// The Mac's runas (2026-10-11, <see cref="NeonSidekick.Sql.KerberosSignIn"/>): the wizard offers it, asks for <c>name@REALM</c>, and
    /// refuses a bare name and a <c>DOMAIN\name</c> (which Windows takes) with the form to use; a failed test still leaves the choice,
    /// and Cancel writes nothing (a Keychain draft never reaches the store).
    /// </summary>
    [MacFact]
    public async Task OnThePane_TheSqlWizard_RefusesATakenName_AndARunAsWithoutDomain_AndCancelWritesNothing_Mac()
    {
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        const string Before = """{ "connections": { "aw": { "server": "x", "database": "db", "auth": "windows" } } }""";
        File.WriteAllText(path, Before);
        NeonSidekick.Sql.SqlConnectionConfig? tested = null;
        var (menu, _, _) = PaneMenu(testSql: (c, _) =>
        {
            tested = c.Config;
            return Task.FromResult(NeonSidekick.Sql.SqlRun.Refused(NeonSidekick.Sql.SqlOutcome.ConnectFailed, "Login failed for user.", c.Name));
        });
        OpenSqlWizard();
        Push(Keys.Enter);                         // + New connection: "aw" is saved, so the first page lists it
        Push(Keys.Enter);
        Type("rep");
        Type("sqlhost01");
        Type("Reports");
        Push(Keys.Down, Keys.Down, Keys.Enter);   // runas
        Type("svc");                              // no realm
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        Type(@"CONTOSO\svc");                     // Windows' form, no realm
        Push(Enumerable.Repeat(Keys.Backspace, @"CONTOSO\svc".Length).ToArray());
        Type("svc@ad.example.edu");
        Push(Keys.Down, Keys.Enter);              // credman: the Keychain
        Type("pw");
        Push(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);   // mandatory, no, the default timeout, read, no description
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows): refused
        Push(Keys.Down, Keys.Enter);              // Cancel
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.SqlWizardRunAsUserQuestion, Output);
        Assert.Contains(NeonSidekick.Sql.SqlText.RunAsNeedsRealm("svc"), Output);
        Assert.Contains(NeonSidekick.Sql.SqlText.RunAsNeedsRealm(@"CONTOSO\svc"), Output);
        Assert.Equal(("runas", "svc@ad.example.edu"), (tested?.Auth, tested?.User));
        Assert.Contains("Error: could not connect to rep: Login failed for user.", Output);
        Assert.Contains(SettingsMenu.SqlWizardCancelledNotice, Output);
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
        Push(Keys.Enter, Keys.Enter, Keys.Enter); // the default timeout, read, no description
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows since 2026-10-01; this live test had not followed)
        Push(Keys.Down, Keys.Enter);              // Cancel
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("Connected to 'live': Microsoft SQL Server", Output);
        Assert.DoesNotContain(LiveSql.Password, Output);
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
        Type("Reports");
        Push(Keys.Down, Keys.Enter);              // windows
        Push(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);   // mandatory, no, the default timeout, read, no description
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
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, """{ "connections": { "aw": { "server": "x", "database": "db", "auth": "windows" }, "prod": { "server": "y", "database": "db", "auth": "windows" } } }""");
        var (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Down, Keys.Down, Keys.Enter]);   // SQL, the offered row: the checklist
        Push(Keys.Enter);                                    // aw on
        Push(Keys.Char(' '));                                // and off again (Space flips too)
        Push(Keys.Enter);                                    // and on
        Push(Keys.Escape, Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["aw"], _settings.Current.SqlConnectionsOffered);
        Assert.Contains("SQL connections offered     none of 2", Output);
        Assert.Contains("SQL connections offered     1 of 2", Output);
        Assert.Contains("[x] aw    SQL Server · x", Output);

        File.WriteAllText(path, """{ "connections": { "aw": { "server": "x", "database": "db", "auth": "windows" }, "prod": { "server": "y", "database": "db", "auth": "windows" }, "new": { "server": "z", "database": "db", "auth": "windows" } } }""");
        var loaded = NeonSidekick.Sql.SqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
        Assert.Equal("1 of 3", SettingsMenu.SqlOfferedValue(_settings.Current.SqlConnectionsOffered, loaded));   // the new one hidden until ticked
        Assert.Equal("none of 3", SettingsMenu.SqlOfferedValue(null, loaded));   // null offers none (2026-10-01)
        Assert.Equal("none of 3", SettingsMenu.SqlOfferedValue([], loaded));
    }

    /// <summary>
    /// The database panes' order (2026-10-11, the user's ask): A to Z, case folded, names equal but for case kept in their order, so the
    /// profile's lists before the home's (the 2026-10-11 review: an exact-spelling tie-break put the home's <c>Mid</c> first).
    /// </summary>
    [Fact]
    public void ByName_IsAToZ_CaseFolded_AndStable()
    {
        Assert.Equal(["Alpha", "beta", "mid", "Mid", "zeta"], SettingsMenu.ByName(["zeta", "mid", "Alpha", "Mid", "beta"], n => n));
    }

    /// <summary>
    /// <c>SQL connections offered</c> lists A to Z (2026-10-11, the user's ask), not the files' order (the profile's, then the
    /// home's): the second row is <c>mid</c>, the home's, and Select all saves the names A to Z.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheSqlOffered_ListsTheConnectionsAToZ_AcrossBothFiles()
    {
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "connections": { "zeta": { "server": "x", "database": "db", "auth": "windows" }, "Alpha": { "server": "y", "database": "db", "auth": "windows" } } }""");
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.StorageDirectory));
        File.WriteAllText(NeonSidekick.Sql.SqlConfigFile.GlobalPath(_settings.StorageDirectory), """{ "connections": { "mid": { "server": "z", "database": "db", "auth": "windows" } } }""");
        var (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Down, Keys.Down, Keys.Enter]);   // SQL, the offered row: the checklist
        Push(Keys.Down, Keys.Enter);                         // mid on, the second row
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["mid"], _settings.Current.SqlConnectionsOffered);

        (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Down, Keys.Down, Keys.Enter]);
        Push(Keys.Char('a'));
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["Alpha", "mid", "zeta"], _settings.Current.SqlConnectionsOffered);
    }

    /// <summary><c>SQL set password</c> lists the connections that take one A to Z (2026-10-11, the user's ask): the second row is <c>zeta</c>.</summary>
    [Fact]
    public async Task OnThePane_TheSqlSetPassword_ListsTheConnectionsAToZ()
    {
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, """{ "connections": { "zeta": { "server": "x", "database": "db", "auth": "sql", "user": "z" }, "mine": { "server": "y", "database": "db", "auth": "windows" }, "Alpha": { "server": "w", "database": "db", "auth": "sql", "user": "a" } } }""");
        var (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter]);   // SQL, the set-password row: the pick
        Push(Keys.Down, Keys.Enter);                         // zeta, under Alpha (mine takes no password)
        Push([.. "s3cret".Select(Keys.Char), Keys.Enter]);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var zeta = NeonSidekick.Sql.SqlConfigFile.Load(path).Connections.Single(c => c.Name == "zeta");
        Assert.Equal("s3cret", NeonSidekick.Sql.SqlSecrets.Resolve(zeta).Value);
        Assert.Contains("Saved the password of 'zeta', encrypted, in ", Output);
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
        Push(ToTab(ToolsText.GitTabTitle));                                // after UNC since later on 2026-10-09 (before Options until then), its first row
        Push(Keys.Down, Keys.Enter);                            // Git diff max lines: the typed slot, pre-filled with 500
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Char('1'), Keys.Char('0'), Keys.Char('0'), Keys.Char('0'), Keys.Enter);
        Push(Keys.Down, Keys.Enter);                            // Git log max commits: the slot, pre-filled with 20; 500 is out of range, kept
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('5'), Keys.Char('0'), Keys.Char('0'), Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(1000, _settings.Current.GitLibDiffMaxLines);
        Assert.Equal(20, _settings.Current.GitLibLogMaxCommits);
        // The five rows padded to the tab's own column (24 since the GitLib labels, 2026-09-30; 28 before), the diff cap's notice then the log cap's refusal on the status line.
        AssertTabEnds(Strips(ToolsText.FilesTabTitle) + Headed(ToolsText.GitTabTitle, "▸ GitLib tools            on\n  GitLib diff max lines   500 lines\n  GitLib log max commits  20 commits\n  GitLib user email       (not set)\n  GitLib user name        (not set)\n"), 100);
        Assert.Contains("\n  GitLib diff max lines   1000 lines\n", Output);
        Assert.Contains("GitLib log max commits must be 1 to 200 commits; keeping 20.", Output);
        pane.Dispose();
    }

    /// <summary>The Shell tab (2026-09-21; between Files and Ask since later that day): the policy picker first (its switch), the allowed list, the shell picker, then the three typed rows.</summary>
    [WindowsFact]
    public async Task OnThePane_TheShellTab_SitsBetweenFilesAndAsk_PolicyAndShellArePickers_TheListRemoves()
    {
        _settings.Update(d => d.ShellPoliceForbiddenStrings = []);   // from empty: a fresh profile has the starter list since 2026-10-08
        _settings.Update(d => d.ShellCommandAllowed = ["git push", "dotnet build"]);
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ShellTabTitle));   // Ask, Web, Shell
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Char('y'), Keys.Enter);   // Shell command policy: the picker opens on ask, yolo picked and confirmed (2026-10-03)
        Push(Keys.Down, Keys.Enter, Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // Shell allowed commands: the list, dotnet build removed (Yes), back
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);      // past Shell police (2026-09-22), its forbidden strings (2026-10-03) and Shell prefer native tools (2026-09-26); Shell default: the picker, cmd picked
        Push(Keys.Down, Keys.Enter);                                        // Shell default timeout (s): the typed slot, pre-filled with 180; 0 is out of range, kept
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("yolo", _settings.Current.ShellCommandPolicy);
        Assert.Equal(["git push"], _settings.Current.ShellCommandAllowed);
        Assert.Equal("cmd", _settings.Current.ShellDefault);
        Assert.Equal(180, _settings.Current.ShellTimeoutSeconds);
        // The ten rows padded to the tab's own column (27), then the picker's rows, the list's, and the notices on the status line.
        AssertTabEnds(Strips(ToolsText.CoreTabTitle) + Headed(ToolsText.ShellTabTitle, "▸ Shell command policy            ask\n  Shell allowed commands          2 prefixes\n  Shell police                    on\n  Shell police forbidden strings  none\n  Shell prefer native tools       on\n  Shell default shell             powershell\n  Shell default timeout (s)       180\n  Shell max timeout (s)           600\n  Shell output max chars          30,000 chars\n  Shell code languages            powershell, python, node\n  Shell code default timeout (s)  300\n  Shell tool bridge               off\n  Shell tool bridge max calls     50 tool calls\n"), 100);
        Assert.Contains("\n" + Titled(CoreCrumb + " › Shell command policy │ " + string.Join(" · ", SettingsMenu.CommandPolicyPageButtons(2, police: true).Select(b => b.Title)) + " ") + "\n \n  off  no shell or script tool is offered\n▸ ask  you approve each command not on the allow list\n  yolo every command runs, nothing is asked\n", Output);
        Assert.Contains("  · Shell command policy: yolo\n", Output);
        Assert.Contains("\n" + Titled(CoreCrumb + " › Shell allowed commands │ " + SettingsMenu.PolicyButtonTitle(_settings.Current.ShellCommandPolicy) + " ") + "\n \n▸ dotnet build\n  git push\n", Output);
        Assert.Contains("  · Shell allowed commands: dotnet build removed\n▸ git push\n", Output);
        Assert.Contains("\n" + Titled(CoreCrumb + " › Shell default shell") + "\n \n▸ powershell pwsh when installed, else Windows PowerShell 5.1\n  cmd        cmd.exe: batch syntax\n  bash       Git Bash, when bash.exe is found\n", Output);
        Assert.Contains("  · Shell default shell: cmd\n", Output);
        Assert.Contains("Shell default timeout (s) must be 1 to 3600 seconds; keeping 180.", Output);
        pane.Dispose();
    }

    /// <summary>The Unix twin of <see cref="OnThePane_TheShellTab_SitsBetweenFilesAndAsk_PolicyAndShellArePickers_TheListRemoves"/> (2026-10-06, the macOS build): the Mac's shells, languages, picture-less file tools and Keychain.</summary>
    [UnixFact]
    public async Task OnThePane_TheShellTab_SitsBetweenFilesAndAsk_PolicyAndShellArePickers_TheListRemoves_Unix()
    {
        _settings.Update(d => d.ShellPoliceForbiddenStrings = []);   // from empty: a fresh profile has the starter list since 2026-10-08
        _settings.Update(d => d.ShellCommandAllowed = ["git push", "dotnet build"]);
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ShellTabTitle));   // Ask, Web, Shell
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Char('y'), Keys.Enter);   // Shell command policy: the picker opens on ask, yolo picked and confirmed (2026-10-03)
        Push(Keys.Down, Keys.Enter, Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // Shell allowed commands: the list, dotnet build removed (Yes), back
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);      // past Shell police (2026-09-22), its forbidden strings (2026-10-03) and Shell prefer native tools (2026-09-26); Shell default: the picker, cmd picked
        Push(Keys.Down, Keys.Enter);                                        // Shell default timeout (s): the typed slot, pre-filled with 180; 0 is out of range, kept
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("yolo", _settings.Current.ShellCommandPolicy);
        Assert.Equal(["git push"], _settings.Current.ShellCommandAllowed);
        Assert.Equal("bash", _settings.Current.ShellDefault);   // the second of the Mac's three
        Assert.Equal(180, _settings.Current.ShellTimeoutSeconds);
        // The ten rows padded to the tab's own column (27), then the picker's rows, the list's, and the notices on the status line.
        AssertTabEnds(Strips(ToolsText.CoreTabTitle) + Headed(ToolsText.ShellTabTitle, "▸ Shell command policy            ask\n  Shell allowed commands          2 prefixes\n  Shell police                    on\n  Shell police forbidden strings  none\n  Shell prefer native tools       on\n  Shell default shell             zsh\n  Shell default timeout (s)       180\n  Shell max timeout (s)           600\n  Shell output max chars          30,000 chars\n  Shell code languages            powershell, python, node\n  Shell code default timeout (s)  300\n  Shell tool bridge               off\n  Shell tool bridge max calls     50 tool calls\n"), 100);
        Assert.Contains("\n" + Titled(CoreCrumb + " › Shell command policy │ " + string.Join(" · ", SettingsMenu.CommandPolicyPageButtons(2, police: true).Select(b => b.Title)) + " ") + "\n \n  off  no shell or script tool is offered\n▸ ask  you approve each command not on the allow list\n  yolo every command runs, nothing is asked\n", Output);
        Assert.Contains("  · Shell command policy: yolo\n", Output);
        Assert.Contains("\n" + Titled(CoreCrumb + " › Shell allowed commands │ " + SettingsMenu.PolicyButtonTitle(_settings.Current.ShellCommandPolicy) + " ") + "\n \n▸ dotnet build\n  git push\n", Output);
        Assert.Contains("  · Shell allowed commands: dotnet build removed\n▸ git push\n", Output);
        Assert.Contains("\n" + Titled(CoreCrumb + " › Shell default shell") + "\n \n▸ zsh        the macOS shell\n  bash       bash from the PATH, else /bin/bash\n  powershell pwsh, when installed\n", Output);
        Assert.Contains("  · Shell default shell: bash\n", Output);
        Assert.Contains("Shell default timeout (s) must be 1 to 3600 seconds; keeping 180.", Output);
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
        Assert.Contains("\n" + Titled(CoreCrumb + " › Shell tool bridge") + "\n \n  on  a script may call this app's other tools through its neon_tools module\n▸ off a script does everything itself: no neon_tools module, no tool calls\n", Output);
        Assert.Contains("  · Shell tool bridge: on", Output);
        Assert.Contains("\n▸ Shell tool bridge               on\n  Shell tool bridge max calls     50 tool calls\n", Output);
        pane.Dispose();
    }

    /// <summary>The outside-paths police row (2026-09-22): the Shell tab's third row, a toggle that opens on the saved on; off is the row below; no reconnect.</summary>
    [WindowsFact]
    public async Task OnThePane_ThePoliceRow_IsAPicker_NoReconnect()
    {
        _settings.Update(d => d.ShellPoliceForbiddenStrings = []);   // from empty: a fresh profile has the starter list since 2026-10-08
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ShellTabTitle));   // Shell
        Push(Keys.Down, Keys.Down, Keys.Enter);                 // Shell police: the picker opens on on
        Push(Keys.Down, Keys.Enter);                            // off is the row below
        Push(Keys.Char('y'), Keys.Enter);                       // yes to the question (2026-10-02)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.ShellPolice);
        Assert.Contains("\n" + Titled(SettingsMenu.PoliceOffConfirmQuestion) + "\n", Output);
        Assert.Contains("\n" + Titled(CoreCrumb + " › Shell police │ ≡ strings (none) ") + "\n \n▸ on  shell police enabled\n  off shell police disabled\n", Output);
        Assert.Contains("  · Shell police: off", Output);
        Assert.Contains("\n▸ Shell police                    off\n  Shell police forbidden strings  none\n" + Heading("── Commands") + "\n  Shell prefer native tools       on\n  Shell default shell             powershell\n", Output);
        pane.Dispose();
    }

    /// <summary>The Unix twin of <see cref="OnThePane_ThePoliceRow_IsAPicker_NoReconnect"/> (2026-10-06, the macOS build): the Mac's shells, languages, picture-less file tools and Keychain.</summary>
    [UnixFact]
    public async Task OnThePane_ThePoliceRow_IsAPicker_NoReconnect_Unix()
    {
        _settings.Update(d => d.ShellPoliceForbiddenStrings = []);   // from empty: a fresh profile has the starter list since 2026-10-08
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ShellTabTitle));   // Shell
        Push(Keys.Down, Keys.Down, Keys.Enter);                 // Shell police: the picker opens on on
        Push(Keys.Down, Keys.Enter);                            // off is the row below
        Push(Keys.Char('y'), Keys.Enter);                       // yes to the question (2026-10-02)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.ShellPolice);
        Assert.Contains("\n" + Titled(SettingsMenu.PoliceOffConfirmQuestion) + "\n", Output);
        Assert.Contains("\n" + Titled(CoreCrumb + " › Shell police │ ≡ strings (none) ") + "\n \n▸ on  shell police enabled\n  off shell police disabled\n", Output);
        Assert.Contains("  · Shell police: off", Output);
        Assert.Contains("\n▸ Shell police                    off\n  Shell police forbidden strings  none\n" + Heading("── Commands") + "\n  Shell prefer native tools       on\n  Shell default shell             zsh\n", Output);
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

        Assert.True(_settings.Current.ShellPolice);
        Assert.Contains("\n" + Titled(SettingsMenu.PoliceOffConfirmQuestion) + "\n", Output);
        Assert.DoesNotContain("Shell police: off", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task ShowPolice_OffConfirmed_Saves()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Down, Keys.Enter, Keys.Char('y'), Keys.Enter);   // off picked; yes

        await menu.ShowPoliceAsync(CancellationToken.None);

        Assert.False(_settings.Current.ShellPolice);
        Assert.Contains(SettingsMenu.PoliceOffConfirmQuestion, Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>Back on never asks (2026-10-02).</summary>
    [Fact]
    public async Task ShowPolice_OnAsksNothing()
    {
        _settings.Update(d => d.ShellPolice = false);
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Up, Keys.Enter);   // on is the row above

        await menu.ShowPoliceAsync(CancellationToken.None);

        Assert.True(_settings.Current.ShellPolice);
        Assert.DoesNotContain(SettingsMenu.PoliceOffConfirmQuestion, Output);
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
        Assert.Contains("\n" + Titled(ToolsText.Label + " › " + SettingsMenu.FieldName(SettingsField.DockerTools)) + "\n", Output);
        Assert.DoesNotContain(ToolsText.OfferedTabTitle + "    Web", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>
    /// The Web page's browser-mode button (2026-10-04, the user's ask; one cycling button since later on 2026-10-13, the user's call):
    /// M steps auto → http → browser, each press saved at once with the picker's own notice and the page back with the new mode shown;
    /// ESC leaves with no "unchanged" over it, the switch untouched. No open-browser button without a Browser window.
    /// </summary>
    [Fact]
    public async Task ShowSwitch_Web_TheModeButton_StepsTheBrowserMode()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('m'), Keys.Char('m'), Keys.Escape);

        await menu.ShowSwitchAsync(SettingsField.WebTools, CancellationToken.None);

        Assert.Equal("browser", _settings.Current.WebBrowserMode);
        Assert.True(_settings.Current.WebTools);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Web tools │ ⇄ auto ") + "\n", Output);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Web tools │ ↓ http ") + "\n", Output);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Web tools │ ◎ browser ") + "\n", Output);
        Assert.DoesNotContain(SettingsMenu.OpenBrowserButton, Output);

        Assert.Contains(SettingsMenu.WebToggleKeys, Output);
        Assert.Contains("  · Web browser mode: http\n", Output);
        Assert.Contains("  · Web browser mode: browser\n", Output);
        Assert.DoesNotContain("  · " + SettingsMenu.UnchangedNotice + "\n", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>A mode and the switch both change in one visit (2026-10-04); browser steps back round to auto (later on 2026-10-13).</summary>
    [Fact]
    public async Task ShowSwitch_Web_TheModeButtonWraps_AndTheSwitchStillWorks()
    {
        _settings.Update(d => d.WebBrowserMode = "browser");
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('m'), Keys.Down, Keys.Enter);  // auto, then off

        await menu.ShowSwitchAsync(SettingsField.WebTools, CancellationToken.None);

        Assert.Equal("auto", _settings.Current.WebBrowserMode);
        Assert.False(_settings.Current.WebTools);
        Assert.Contains("  · Web browser mode: auto\n", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>
    /// The open-browser button (later on 2026-10-13, the user's ask): with a Browser window to open, O runs the hook once, the status
    /// line says so, nothing is saved and the page stays; the hint names the key.
    /// </summary>
    [Fact]
    public async Task ShowSwitch_Web_TheOpenBrowserButton_OpensTheWindow_AndThePageStays()
    {
        var (menu, pane, settings) = PaneMenu();
        int opened = 0;
        settings.OpenBrowser = () => opened++;
        Push(Keys.Char('o'), Keys.Escape);

        await menu.ShowSwitchAsync(SettingsField.WebTools, CancellationToken.None);

        Assert.Equal(1, opened);
        Assert.Equal("auto", _settings.Current.WebBrowserMode);
        Assert.True(_settings.Current.WebTools);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Web tools │ ⇄ auto · ↗ Open browser ") + "\n", Output);
        Assert.Contains("Enter = choose · m = browser mode · o = open browser · Esc = back", Output);
        Assert.Contains("  · " + SettingsMenu.OpenedBrowserNotice + "\n", Output);
        Assert.DoesNotContain("Web browser mode:", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public void WebModeButtons_ShowTheModeInForce_OneLitButton_ThenOpenBrowser()
    {
        // 2026-10-04 a radio group; later on 2026-10-13 (the user's call) one button showing the mode, key M, always lit; a hand-edited case
        // or a retired word shows its mode, an unknown word itself after a ?.
        Assert.Equal(["⇄ auto"], SettingsMenu.WebModeButtons("auto").Select(b => b.Title));
        Assert.Equal(['m'], SettingsMenu.WebModeButtons("auto").Select(b => b.Key!.Value));
        Assert.Equal(["◎ browser"], SettingsMenu.WebModeButtons("browser").Select(b => b.Title));
        Assert.Equal(["↓ http"], SettingsMenu.WebModeButtons(" HttpClient ").Select(b => b.Title));
        Assert.Equal(["? firefox"], SettingsMenu.WebModeButtons(" firefox ").Select(b => b.Title));
        Assert.All(SettingsMenu.WebModeButtons("firefox"), b => Assert.True(b.On));
        Assert.All(new[] { "auto", "http", "browser" }, mode => Assert.Equal(1, TextCells.Width(SettingsMenu.WebModeButtons(mode)[0].Title[..1])));
        Assert.Equal([("⇄ auto", 'm', true), ("↗ Open browser", 'o', false)], SettingsMenu.WebModeButtons("auto", openBrowser: true).Select(b => (b.Title, b.Key!.Value, b.On)));
        Assert.Equal("Enter = choose · m = browser mode · Esc = back", SettingsMenu.WebToggleKeys);
        Assert.Equal("Enter = choose · m = browser mode · o = open browser · Esc = back", SettingsMenu.WebToggleOpenKeys);
        Assert.Equal("Opened the Browser window", SettingsMenu.OpenedBrowserNotice);

        Assert.Equal(["http", "browser", "auto", "auto", "auto", "browser"], new[] { "auto", "http", "browser", "firefox", "", "HttpClient" }.Select(BrowserMode.Next));
    }

    /// <summary>The shell's is the policy picker (2026-10-03): a move into yolo asks first, No keeps the policy; off asks nothing.</summary>
    [Fact]
    public async Task ShowSwitch_TheShell_IsThePolicyPicker_YoloAsksFirst()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Down, Keys.Enter, Keys.Enter);   // yolo picked; Enter on No

        await menu.ShowSwitchAsync(SettingsField.ShellCommandPolicy, CancellationToken.None);

        Assert.Equal("ask", _settings.Current.ShellCommandPolicy);
        Assert.Contains("\n" + Titled(SettingsMenu.YoloConfirmQuestion) + "\n", Output);

        Push(Keys.Down, Keys.Enter, Keys.Char('y'), Keys.Enter);   // yolo picked; yes
        await menu.ShowSwitchAsync(SettingsField.ShellCommandPolicy, CancellationToken.None);
        Assert.Equal("yolo", _settings.Current.ShellCommandPolicy);

        int mark = Output.Length;
        Push(Keys.Up, Keys.Up, Keys.Enter);   // off: nothing asked
        await menu.ShowSwitchAsync(SettingsField.ShellCommandPolicy, CancellationToken.None);
        Assert.Equal("off", _settings.Current.ShellCommandPolicy);
        Assert.DoesNotContain(SettingsMenu.YoloConfirmQuestion, Output[mark..]);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>The code-languages list (2026-09-21): Enter or Space flips and saves at once, the last one on refuses to go.</summary>
    [WindowsFact]
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
        Assert.Contains("\n" + Titled(CoreCrumb + " › Shell code languages │ " + SettingsMenu.SelectAllButton + " · " + SettingsMenu.SelectNoneButton + " ") + "\n \n▸ [x] powershell a .ps1 through pwsh or Windows PowerShell; Invoke-NeonTool calls a tool\n  [x] python     a .py through python.exe; from neon_tools import …\n  [x] node       a .js through node.exe; require('neon_tools')\n", Output);
        Assert.Contains("  · Shell code languages: python, node\n", Output);
        Assert.Contains("  · Shell code languages: node\n", Output);
        Assert.Contains("At least one language stays on.", Output);
        Assert.Contains("\n▸ Shell code languages            node\n  Shell code default timeout (s)  300\n", Output);
        pane.Dispose();
    }

    /// <summary>The Unix twin of <see cref="OnThePane_TheCodeLanguagesRow_IsACheckboxList_TheLastOneStays"/> (2026-10-06, the macOS build): the Mac's shells, languages, picture-less file tools and Keychain.</summary>
    [UnixFact]
    public async Task OnThePane_TheCodeLanguagesRow_IsACheckboxList_TheLastOneStays_Unix()
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
        Assert.Contains("\n" + Titled(CoreCrumb + " › Shell code languages │ " + SettingsMenu.SelectAllButton + " · " + SettingsMenu.SelectNoneButton + " ") + "\n \n▸ [x] powershell a .ps1 through pwsh; Invoke-NeonTool calls a tool\n  [x] python     a .py through python3; from neon_tools import …\n  [x] node       a .js through node; require('neon_tools')\n", Output);
        Assert.Contains("  · Shell code languages: python, node\n", Output);
        Assert.Contains("  · Shell code languages: node\n", Output);
        Assert.Contains("At least one language stays on.", Output);
        Assert.Contains("\n▸ Shell code languages            node\n  Shell code default timeout (s)  300\n", Output);
        pane.Dispose();
    }

    [WindowsFact]
    public async Task OnThePane_TheWebTab_SitsAfterOptions()
    {
        // The last tab until later on 2026-09-21 (Left wrapped to it); third since, the user's order.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.WebTabTitle));     // Web
        Push(Keys.Enter, Keys.Down, Keys.Enter);   // Web tools: the page, off picked
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));   // read at the next turn: nothing to reconnect

        Assert.False(_settings.Current.WebTools);
        // The twelve rows padded to the tab's own column (29 since the botcheck window's row, 2026-10-13), the toggle's notice on the status line under the strip.
        AssertTabEnds(Strips(ToolsText.CoreTabTitle) + Headed(ToolsText.WebTabTitle, "▸ Web tools                    on\n  Web browser mode             auto\n  Web browser path             (auto: msedge.exe)\n  Web browser botcheck window  ask\n  Web browser home page        (blank page)\n  Web browser privacy          clear-history\n  Web open links in            default-browser\n  Web browser sign in mode     one-time-link\n  Web browser network mode     internet\n  Web search method            duckduckgo\n  Web SearXNG URL              (not set)\n  Web search max results       20 results\n  Web download max (MB)        50 MB\n"), 100);
        Assert.Contains("\n" + Titled(Strip) + "\n" + SubStrip(ToolsText.CoreTabTitle) + "\n  · Web tools: off\n" + Heading("── Browser") + "\n▸ Web tools                    off\n", Output);
        pane.Dispose();
    }

    /// <summary>The Unix twin of <see cref="OnThePane_TheWebTab_SitsAfterOptions"/> (2026-10-06, the macOS build): the Mac's shells, languages, picture-less file tools and Keychain.</summary>
    [UnixFact]
    public async Task OnThePane_TheWebTab_SitsAfterOptions_Unix()
    {
        // The last tab until later on 2026-09-21 (Left wrapped to it); third since, the user's order.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.WebTabTitle));     // Web
        Push(Keys.Enter, Keys.Down, Keys.Enter);   // Web tools: the page, off picked
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));   // read at the next turn: nothing to reconnect

        Assert.False(_settings.Current.WebTools);
        // The twelve rows padded to the tab's own column (29 since the botcheck window's row, 2026-10-13), the toggle's notice on the status line under the strip.
        AssertTabEnds(Strips(ToolsText.CoreTabTitle) + Headed(ToolsText.WebTabTitle, "▸ Web tools                    on\n  Web browser mode             auto\n  Web browser path             " + SettingsMenu.AutoBrowserLabel(FakeBrowserPath) + "\n  Web browser botcheck window  ask\n  Web browser home page        (blank page)\n  Web browser privacy          clear-history\n  Web open links in            default-browser\n  Web browser sign in mode     one-time-link\n  Web browser network mode     internet\n  Web search method            duckduckgo\n  Web SearXNG URL              (not set)\n  Web search max results       20 results\n  Web download max (MB)        50 MB\n"), 100);
        Assert.Contains("\n" + Titled(Strip) + "\n" + SubStrip(ToolsText.CoreTabTitle) + "\n  · Web tools: off\n" + Heading("── Browser") + "\n▸ Web tools                    off\n", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_APickerFromTheWebTab_IsTitledUnderTools_AndTheRootComesBack()
    {
        var (menu, pane, settings) = PaneMenu();
        Push(ToTab(ToolsText.WebTabTitle));     // Web
        Push(Keys.Down, Keys.Enter);               // Browser mode: the picker
        Push(Keys.Down, Keys.Enter);               // http (the second name)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("http", _settings.Current.WebBrowserMode);
        Assert.Contains("\n" + Titled(CoreCrumb + " › Web browser mode") + "\n", Output);
        Assert.DoesNotContain(SettingsMenu.Title + " › Web browser mode", Output);
        Assert.Contains("  · Web browser mode: http\n", Output);
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
        Assert.Contains("  · Ask max questions: 3 questions\n", Output);
        Assert.Contains("  · " + SettingsMenu.UnchangedNotice + "\n", Output);
        Assert.Contains(Rule(100) + "\n" + SettingsMenu.EditKeys, Output);   // the typed slot under the list (pre-filled with 10: two Backspaces and a 3 made 3), the edit keys in the hint row
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_DiffMaxLines_IsTheOptionsTabsFifthRow_Typed_ZeroIsTheHeaderAlone_OutOfRangeRefused()
    {
        // Later on 2026-10-03: under Show file diffs; 0 to 500, 10 by default (40 until later that day), 0 = the header line alone.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.OptionsTabTitle));                                              // Options, Standard's last menu (the strip wrapped to it until later on 2026-10-10)
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);                        // Diff max lines: the typed slot with "10"
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('5'), Keys.Char('0'), Keys.Char('1'), Keys.Enter);   // refused: 501
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);        // 0: the header alone
        Push(Keys.Enter, Keys.Backspace, Keys.Char('1'), Keys.Char('2'), Keys.Enter);        // 12
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(12, _settings.Current.DiffMaxLines);
        Assert.Contains("Diff max lines " + SettingsMenu.DiffMaxLinesRangeError + "; keeping 10.", Output);
        Assert.Contains("  · Diff max lines: header only\n", Output);
        Assert.Contains("  · Diff max lines: 12 lines\n", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_DiffCollapseCount_IsTheOptionsTabsLastRow_Typed_ZeroIsOff_OutOfRangeRefused()
    {
        // 2026-10-04, the user's ask and place: under Diff max lines; 0 to 500, 10 by default, 0 = off.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.OptionsTabTitle));                                              // Options, Standard's last menu (the strip wrapped to it until later on 2026-10-10)
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);             // Diff collapse count: the typed slot with "10"
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('5'), Keys.Char('0'), Keys.Char('1'), Keys.Enter);   // refused: 501
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);        // 0: off
        Push(Keys.Enter, Keys.Backspace, Keys.Char('5'), Keys.Enter);                        // 5
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(5, _settings.Current.DiffCollapseCount);
        Assert.Contains("Diff collapse count " + SettingsMenu.DiffCollapseCountRangeError + "; keeping 10.", Output);
        Assert.Contains("  · Diff collapse count: off\n", Output);
        Assert.Contains("  · Diff collapse count: 5 lines\n", Output);
        Assert.Equal("must be 0 to 500 lines (0 = off)", SettingsMenu.DiffCollapseCountRangeError);
        Assert.Equal("Diff collapse count", SettingsMenu.FieldName(SettingsField.DiffCollapseCount));
        Assert.Equal(10, new AppSettingsData().DiffCollapseCount);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ToolCollapseCount_IsTheOptionsTabsSecondRow_Typed_ZeroIsOff_OutOfRangeRefused()
    {
        // 2026-09-22, the user's place: under $-mention enabled; 0 to 100, 2 by default, 0 = off.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.OptionsTabTitle));                   // Options, Standard's last menu (the strip wrapped to it until later on 2026-10-10)
        Push(Keys.Down, Keys.Enter);                              // Tool collapse count: the typed slot with "2"
        Push(Keys.Backspace, Keys.Char('1'), Keys.Char('0'), Keys.Char('1'), Keys.Enter);   // refused: 101
        Push(Keys.Enter, Keys.Backspace, Keys.Char('0'), Keys.Enter);                        // 0: off
        Push(Keys.Enter, Keys.Backspace, Keys.Char('5'), Keys.Enter);                        // 5
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(5, _settings.Current.ToolCollapseCount);
        Assert.Contains("Tool collapse count " + SettingsMenu.ToolCollapseCountRangeError + "; keeping 2.", Output);
        Assert.Contains("  · Tool collapse count: off\n", Output);
        Assert.Contains("  · Tool collapse count: 5 lines\n", Output);
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
        Push(ToTab(ToolsText.OptionsTabTitle));                   // Options, Standard's last menu (the strip wrapped to it until later on 2026-10-10)
        Push(Keys.Down, Keys.Down, Keys.Enter);                   // Code collapse count: the typed slot with "20"
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('1'), Keys.Char('0'), Keys.Char('1'), Keys.Enter);   // refused: 101
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);                      // 0: off
        Push(Keys.Enter, Keys.Backspace, Keys.Char('3'), Keys.Char('0'), Keys.Enter);                      // 30
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(30, _settings.Current.CodeCollapseCount);
        Assert.Contains("Code collapse count " + SettingsMenu.CodeCollapseCountRangeError + "; keeping 20.", Output);
        Assert.Contains("  · Code collapse count: off\n", Output);
        Assert.Contains("  · Code collapse count: 30 lines\n", Output);
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
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // View image max per call (the seventh row since later on 2026-10-15, the bin's row second; the sixth since 2026-10-01, Safe edits gone; the seventh from 2026-09-21): the typed slot with "10"
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);        // refused: 0
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('1'), Keys.Char('0'), Keys.Char('1'), Keys.Enter);   // refused: 101
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('2'), Keys.Char('5'), Keys.Enter);   // 25
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(25, _settings.Current.FileViewImageMaxPerCall);
        Assert.Contains("File view image max images " + SettingsMenu.ViewImageMaxPerCallRangeError + "; keeping 10.", Output);
        Assert.Contains("  · File view image max images: 25 pictures\n", Output);
        Assert.Contains("\n▸ " + FileRow("File view image max images", "25 pictures") + "\n", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_FileSearchMaxResults_IsTheFilesTabsLastRow_Typed_OutOfRangeRefused()
    {
        // 2026-10-01, the user's ask: 1 to 5000, 200 by default (the constant until then); search_files and unc_search read it at their next call.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.FilesTabTitle));   // Files
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the eighth row (the bin's second since later on 2026-10-15), after the view_image cap: the typed slot with "200"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);   // refused: 0
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Char('1'), Keys.Char('0'), Keys.Char('0'), Keys.Char('0'), Keys.Enter);   // 1000
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(1000, _settings.Current.FileSearchMaxResults);
        Assert.Contains("File search max results " + SettingsMenu.FileSearchMaxResultsRangeError + "; keeping 200.", Output);
        Assert.Contains("  · File search max results: 1000 results\n", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ImageEditRows_AreTheImageMenu_QualityTyped_MetadataPicked_FolderKeptUnderTheImagesFolder()
    {
        // 2026-10-04, the user's pick: image_edit's three defaults after the search cap; the tool reads them at its next call. Their own menu, Image, since 2026-10-10.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ImageMenuTitle));
        Push(Keys.Down, Keys.Enter);                                                                    // the second row (Image tools first, 2026-10-10): the quality's typed slot with "90"
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);                                // refused: 0
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('5'), Keys.Char('5'), Keys.Enter);   // 55
        Push(Keys.Down, Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);                                  // the metadata picker: none, basic, all
        Push(Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);                                             // the mode picker (later on 2026-10-04): beside-original, overwrite-original
        Push(Keys.Down, Keys.Enter, Keys.Char('.'), Keys.Char('.'), Keys.Char('/'), Keys.Char('x'), Keys.Enter);   // refused: outside the images folder (the sandbox until 2026-10-10)
        Push(Keys.Enter, Keys.Char('e'), Keys.Char('d'), Keys.Enter);                                   // ed
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(55, _settings.Current.ImageEditQuality);
        Assert.Equal("all", _settings.Current.ImageEditMetadata);
        Assert.Equal("overwrite-original", _settings.Current.ImageEditMode);
        Assert.Equal("ed", _settings.Current.ImageEditOutputFolder);
        Assert.Contains("Image edit quality " + SettingsMenu.ImageEditQualityRangeError + "; keeping 90.", Output);
        Assert.Contains("Image edit output folder " + SettingsMenu.ImageEditOutputFolderError + "; keeping " + SettingsMenu.ImageEditBesideLabel + ".", Output);
        Assert.Contains("  · Image edit quality: 55\n", Output);
        Assert.Equal("must be 1 to 100", SettingsMenu.ImageEditQualityRangeError);
        pane.Dispose();
    }

    [Fact]
    public void ImageEditRows_ReadTheirValues_AndTheMetadataPickerDescribesEach()
    {
        var data = new AppSettingsData();
        Assert.Equal(("Image edit quality", "90", "90"), (SettingsMenu.FieldName(SettingsField.ImageEditQuality), SettingsMenu.FieldValue(SettingsField.ImageEditQuality, data, ""), SettingsMenu.EditableValue(SettingsField.ImageEditQuality, data)));
        Assert.Equal(("Image edit metadata", "none"), (SettingsMenu.FieldName(SettingsField.ImageEditMetadata), SettingsMenu.FieldValue(SettingsField.ImageEditMetadata, data, "")));
        Assert.Equal("basic", SettingsMenu.FieldValue(SettingsField.ImageEditMetadata, new AppSettingsData { ImageEditMetadata = "Basic" }, ""));
        Assert.Equal("none", SettingsMenu.FieldValue(SettingsField.ImageEditMetadata, new AppSettingsData { ImageEditMetadata = "bogus" }, ""));
        Assert.Equal(("Image edit output folder", "(beside the source)", ""), (SettingsMenu.FieldName(SettingsField.ImageEditOutputFolder), SettingsMenu.FieldValue(SettingsField.ImageEditOutputFolder, data, ""), SettingsMenu.EditableValue(SettingsField.ImageEditOutputFolder, data)));
        Assert.Equal("thumbs", SettingsMenu.FieldValue(SettingsField.ImageEditOutputFolder, new AppSettingsData { ImageEditOutputFolder = "thumbs" }, ""));
        // The pasted pictures' folder (2026-10-09): pasted under the profile's images folder; empty is that folder itself.
        Assert.Equal(("Pasted images folder", "pasted", "pasted"), (SettingsMenu.FieldName(SettingsField.PastedImagesFolder), SettingsMenu.FieldValue(SettingsField.PastedImagesFolder, data, ""), SettingsMenu.EditableValue(SettingsField.PastedImagesFolder, data)));
        Assert.Equal(SettingsMenu.CameraOutputHereLabel, SettingsMenu.FieldValue(SettingsField.PastedImagesFolder, new AppSettingsData { PastedImagesFolder = "" }, ""));
        Assert.Equal("(the profile's images folder)", SettingsMenu.CameraOutputHereLabel);
        Assert.Equal("must be a folder under the profile's images folder (a relative path), or empty", SettingsMenu.CameraOutputFolderError);
        Assert.Equal("must be a folder under the profile's images folder (a relative path), or empty", SettingsMenu.ImageEditOutputFolderError);   // the working directory's until 2026-10-10
        Assert.False(SettingsMenu.IsToggle(SettingsField.ImageEditMetadata));
        Assert.All(Images.ImageWords.MetadataNames, name => Assert.Contains(Images.ImageWords.DescribeMetadata(name), Markup.Remove(SettingsMenu.ImageEditMetadataLabel(name))));
        Assert.Contains("GPS", Images.ImageWords.DescribeMetadata("all"));

        // Image edit mode (later on 2026-10-04): a picker over the two words, an unknown value reading as beside-original.
        Assert.Equal(("Image edit mode", "beside-original"), (SettingsMenu.FieldName(SettingsField.ImageEditMode), SettingsMenu.FieldValue(SettingsField.ImageEditMode, data, "")));
        Assert.Equal("overwrite-original", SettingsMenu.FieldValue(SettingsField.ImageEditMode, new AppSettingsData { ImageEditMode = "Overwrite_Original" }, ""));
        Assert.Equal("beside-original", SettingsMenu.FieldValue(SettingsField.ImageEditMode, new AppSettingsData { ImageEditMode = "bogus" }, ""));
        Assert.False(SettingsMenu.IsToggle(SettingsField.ImageEditMode));
        Assert.All(Images.ImageWords.EditModeNames, name => Assert.Contains(Images.ImageWords.DescribeEditMode(name), Markup.Remove(SettingsMenu.ImageEditModeLabel(name))));
    }

    [Fact]
    public async Task OnThePane_WebBrowserHomePage_IsUnderTheBotcheckWindow_Typed_NoAddressRefused_EmptyTheBlankPage()
    {
        // Later on 2026-10-13, with the Browser window's navigation bar: a web address as the address box reads one, or empty.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.WebTabTitle));
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the fifth row: the typed slot, empty
        Push("my start page".Select(Keys.Char).ToArray());
        Push(Keys.Enter);                                                // refused: words, not an address
        Push(Keys.Enter);
        Push("example.com".Select(Keys.Char).ToArray());
        Push(Keys.Enter);                                                // kept as typed
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("example.com", _settings.Current.WebBrowserHomePage);
        Assert.Contains("Web browser home page " + SettingsMenu.HomePageError + "; keeping " + SettingsMenu.BlankHomePageLabel + ".", Output);
        Assert.Contains("  · Web browser home page: example.com\n", Output);
        Assert.Equal("must be a web address, or empty for the blank page", SettingsMenu.HomePageError);
        Assert.Equal("(blank page)", SettingsMenu.FieldValue(SettingsField.WebBrowserHomePage, new AppSettingsData(), ""));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.WebBrowserHomePage, new AppSettingsData()));
        Assert.False(SettingsMenu.IsToggle(SettingsField.WebBrowserHomePage));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_BrowserPrivacy_IsUnderTheHomePage_APicker()
    {
        // Later on 2026-10-13 (the user's ask and name): keep-history or clear-history, the default.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.WebTabTitle));
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the sixth row: the picker, on clear-history
        Push(Keys.Up, Keys.Enter);                                                  // keep-history
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("keep-history", _settings.Current.BrowserPrivacy);
        Assert.Contains("\n  keep-history  keeps the pages it visited, in its history menu too\n▸ clear-history clears its history when the window closes; cookies and sign-ins stay\n", Output);
        Assert.Contains("  · Web browser privacy: keep-history\n", Output);
        Assert.Equal("Web browser privacy", SettingsMenu.FieldName(SettingsField.BrowserPrivacy));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BrowserPrivacy));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_WebOpenLinksIn_IsUnderThePrivacy_APicker()
    {
        // Later still on 2026-10-13 (the user's ask and name): default-browser, the default, or browser-window.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.WebTabTitle));
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the seventh row: the picker, on default-browser
        Push(Keys.Down, Keys.Enter);                                                           // browser-window
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(NeonSidekick.Web.OpenLinksIn.BrowserWindow, _settings.Current.WebOpenLinksIn);
        Assert.Contains("▸ " + Spectre.Console.Markup.Remove(SettingsMenu.WebOpenLinksInLabel(NeonSidekick.Web.OpenLinksIn.DefaultBrowser)) + "\n", Output);
        Assert.Contains("  · Web open links in: browser-window\n", Output);
        Assert.Equal("Web open links in", SettingsMenu.FieldName(SettingsField.WebOpenLinksIn));
        Assert.False(SettingsMenu.IsToggle(SettingsField.WebOpenLinksIn));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_WebDownloadMax_IsTheWebTabsLastRow_Typed_OutOfRangeRefused()
    {
        // 2026-10-01, the user's ask: 1 to 102400 MB, 50 by default (the constant until then).
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.WebTabTitle));   // Web
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the thirteenth row (Web browser sign in mode's since later on 2026-10-15; the botcheck window's, the home page's, Web browser privacy's and Web open links in's since 2026-10-13), after the search count: the typed slot with "50"
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('0'), Keys.Enter);   // refused: 0
        Push(Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('2'), Keys.Char('0'), Keys.Char('0'), Keys.Char('0'), Keys.Enter);   // 2000
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(2000, _settings.Current.WebDownloadMaxMegabytes);
        Assert.Contains("Web download max (MB) " + SettingsMenu.WebDownloadMaxMegabytesRangeError + "; keeping 50.", Output);
        Assert.Contains("  · Web download max (MB): 2,000 MB\n", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_QueryResultMaxChars_IsOnTheSqlTab_UnderTheTimeout_Typed_OutOfRangeRefused()
    {
        // 2026-10-01, the user's ask: 1,000 to 1,000,000, 32,000 by default (the file tools' cap until then); the three query tools read it at their next call.
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.SqlTabTitle));   // SQL
        Push(Enumerable.Repeat(Keys.Down, 9).Append(Keys.Enter).ToArray());   // the tenth row (the mode and the statements above since 2026-10-05, the default row gone 2026-10-09), under the timeout: the typed slot with "32000"
        Push(Enumerable.Repeat(Keys.Backspace, 5).Append(Keys.Char('9')).Append(Keys.Enter).ToArray());   // refused: 9
        Push(new[] { Keys.Enter }.Concat(Enumerable.Repeat(Keys.Backspace, 5)).Concat("200,000".Select(Keys.Char)).Append(Keys.Enter).ToArray());   // 200,000, the separator allowed
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(200_000, _settings.Current.QueryResultMaxChars);
        Assert.Contains("SQL query result max chars " + SettingsMenu.QueryResultMaxCharsRangeError + "; keeping 32000.", Output);
        Assert.Contains("  · SQL query result max chars: 200,000 chars\n", Output);
        pane.Dispose();
    }

    /// <summary>Space on a settings row (2026-10-04, the UI review; nothing until then): a switch flips and saves at once, no page; any other row does nothing.</summary>
    [Fact]
    public async Task OnThePane_SpaceOnASettingsRow_FlipsASwitch_AndIsNothingOnAnotherRow()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.AskTabTitle));   // Ask
        Push(Keys.Char(' '));                 // Ask user: off
        Push(Keys.Down, Keys.Char(' '));      // Ask max questions: no switch, nothing
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.AskUser);
        Assert.Equal(10, _settings.Current.AskMaxQuestions);
        Assert.Empty(_settings.Current.ToolsDisabled);
        Assert.Contains("  · Ask user: off\n", Output);
        Assert.DoesNotContain(SettingsMenu.PickKeys, Output);
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
        Assert.DoesNotContain(SettingsMenu.NotWhileReplyRunsNotice, Output);
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
        Assert.Contains("  · shift_date: off\n", Output);
        Assert.False(pane.OverlayOpen);
        Assert.False(pane.Dismissed);
        Assert.True(input.IsAvailable);
        pane.Dispose();
    }

    [WindowsFact]
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

        Assert.Contains("  · Offered\n  ·   Clock (3)\n  ·     get_current_time      on   ", Output);
        Assert.Contains("  ·   Files (12 of 13)\n", Output);   // 15 of 16 until the picture tools went to Image, 2026-10-10
        Assert.Contains("  ·     read_file             off  Reads a text file", Output);   // the console wraps the long line
        Assert.Contains("switched off in /tools", Output);
        Assert.Contains("  ·   Questions (1) (off: no pane)\n", Output);
        // The tabs in strip order (Core Web, Shell, Timers, Ask, Options; Admin Network first; Devices Camera, Screen, Print since 2026-10-16, the user's order); the user's since 2026-10-03: Offered, Ask, Web, Shell, Files, UNC, Print, Camera, Obsidian, SQL, MySQL, Oracle, Claude, Docker, HA, ComfyUI, GitLib, Options. Before it Web right after Offered, then Files, Shell, Ask (the user's order, later on 2026-09-21), Claude (Claude (CLI) until 2026-09-29, the Anthropic API's four rows last since), Obsidian, ComfyUI, SQL, Git (native) (the user's order, 2026-09-27), HA (2026-09-28; second to last since later on 2026-10-01, the user's ask) and Options last (2026-09-22).
        Assert.Contains("  ·   Web\n  ·     Web tools: on\n  ·     Web browser mode: auto\n  ·     Web browser path: (auto: msedge.exe)\n", Output);
        Assert.Contains("  ·     Web search max results: 20 results\n  ·     Web download max (MB): 50 MB\n  ·   Shell\n  ·     Shell command policy: ask\n", Output);   // Shell after Web again since 2026-10-16 (the user's order; Network between them from later still on 2026-10-15)
        Assert.Contains("  · Admin\n  ·   Network\n  ·     Network tools: off\n", Output);   // Admin first since 2026-10-16 (the user's order; on Core after Web from later still on 2026-10-15)
        Assert.Contains("  ·     Network certificate stores: off\n  ·   EventLog\n  ·     Event log tools: off\n", Output);
        Assert.Contains("  ·     File view image max images: 10 pictures\n  ·     File search max results: 200 results\n  ·   Image\n  ·     Image tools: off\n  ·     Image edit quality: 90\n  ·     Image edit metadata: none\n  ·     Image edit mode: beside-original\n  ·     Image edit output folder: (beside the source)\n  ·     Pasted images folder: pasted\n  ·   UNC\n", Output);
        Assert.Contains("  ·   Shell\n  ·     Shell command policy: ask\n", Output);
        Assert.Contains("  · Core\n  ·   Web\n  ·     Web tools: on\n", Output);   // Web first since 2026-10-16 (the user's order; Ask first until then)
        Assert.Contains("  ·     Shell tool bridge max calls: 50 tool calls\n  ·   Clock\n  ·     Clock tools: on\n  ·   Timers\n  ·     Timer tools: on\n  ·     Timer tone: silent\n  ·   Ask\n  ·     Ask user: on\n  ·     Ask max questions: 10 questions\n  ·     Ask max choices per question: 10 choices\n  ·   Help\n  ·     Help tools: on\n  ·   Options\n  ·     $-mention enabled: on\n", Output);   // Clock after Shell and Help after Ask since 2026-10-18 (the user's asks); Ask after Timers since 2026-10-16 (the user's order); Timers between Shell and Options since later on 2026-10-15   // Shopping between them on 2026-10-11 until it went to Home later that day
        Assert.Contains("  ·     File search max results: 200 results\n  ·   Image\n  ·     Image tools: off\n  ·     Image edit quality: 90\n  ·     Image edit metadata: none\n  ·     Image edit mode: beside-original\n  ·     Image edit output folder: (beside the source)\n  ·     Pasted images folder: pasted\n  ·   UNC\n  ·     UNC tools: off\n  ·     UNC writes: off\n  ·     UNC shares offered: none of 0\n  ·     UNC set password: Enter to set password for a runas share\n  ·     UNC add/edit share: Enter to start share wizard\n  ·     UNC *-mention enabled: on\n  ·     UNC shares (profile): (none) · Enter edits unc.json\n  ·     UNC shares (global): (none) · Enter edits unc.json\n  ·   GitLib\n  ·     GitLib tools: on\n  ·     GitLib diff max lines: 500 lines\n  ·     GitLib log max commits: 20 commits\n  ·     GitLib user email: (not set)\n  ·     GitLib user name: (not set)\n  · Apps\n  ·   Docker\n  ·     Docker tools: off\n  ·     Docker writes: off\n  ·     Docker engine pipe: \\\\.\\pipe\\docker_engine\n  ·   ComfyUI\n  ·     ComfyUI tools: on\n  ·     ComfyUI URL: (not set)\n  ·     ComfyUI workflows offered: none of 0\n  ·     ComfyUI add workflow: Enter to start workflow wizard\n  ·     ComfyUI ^-mention enabled: on\n  ·     ComfyUI timeout (s): 300\n  ·     ComfyUI max images per call: 5 pictures\n  ·     ComfyUI reinforce negatives: on\n  ·     ComfyUI show prompts: on\n  ·     ComfyUI picture strip: on\n  ·     ComfyUI output folder: comfy\n  ·   YouTube\n  ·     YouTube tools: off\n  ·     YouTube API key: (none)\n  ·     YouTube search max results: 8 results\n  ·     YouTube search order: relevance\n  ·     YouTube safe search: moderate\n  ·     YouTube region: (any region)\n  ·     YouTube language: (any language)\n  ·     YouTube open links in: youtube-window\n  ·     YouTube autoplay: on\n  ·     YouTube while speaking: pause\n  ·   Obsidian\n  ·     Obsidian tools: on\n  ·     Obsidian vault: (not set)\n  ·     Obsidian allow delete (.trash): on\n", Output);   // Admin (EventLog, Services, Tasks, Directory) after Apps later still on 2026-10-15 (the admin-tools plan); Obsidian after YouTube since 2026-10-17 (the user's order)
        Assert.Contains("  ·   EventLog\n  ·     Event log tools: off\n  ·     Event log computers offered: 1 of 1\n  ·     Event log set password: Enter to set password for a runas computer\n  ·     Event log add/edit computer: Enter to start computer wizard\n  ·     Event log max events: 20\n  ·     Event log scan limit: 20000\n  ·     Event log computers (profile): (none) · Enter edits eventlog.json\n  ·     Event log computers (global): (none) · Enter edits eventlog.json\n  ·   Services\n  ·     Service tools: off\n", Output);
        Assert.Contains("  · Databases\n  ·   SQL\n  ·     SQL tools: on\n  ·     SQL mode: read-only\n  ·     SQL statements allowed: changing data, creating, reading (used under read-write)\n  ·     SQL connections offered: none of 0\n  ·     SQL set password: Enter to set password for a connection\n  ·     SQL add/edit connection: Enter to start the connection wizard\n  ·     SQL %-mention enabled: on\n  ·     SQL max rows: 100 rows\n  ·     SQL query timeout (s): 30\n  ·     SQL query result max chars: 32,000 chars\n  ·     SQL connections (profile): (none) · Enter edits sql.json\n  ·     SQL connections (global): (none) · Enter edits sql.json\n  ·   MySQL\n  ·     MySQL tools: off\n  ·     MySQL mode: read-only\n  ·     MySQL statements allowed: changing data, creating, reading (used under read-write)\n  ·     MySQL connections offered: none of 0\n  ·     MySQL set password: Enter to set password for a connection\n  ·     MySQL add/edit connection: Enter to start the connection wizard\n  ·     MySQL %-mention enabled: on\n  ·     MySQL max rows: 100 rows\n  ·     MySQL query timeout (s): 30\n  ·     MySQL connections (profile): (none) · Enter edits mysql.json\n  ·     MySQL connections (global): (none) · Enter edits mysql.json\n  ·   SQLite\n  ·     SQLite tools: off\n  ·     SQLite mode: read-only\n  ·     SQLite statements allowed: changing data, creating, reading (used under read-write)\n  ·     SQLite databases offered: none of 0\n  ·     SQLite sandbox files: on\n  ·     SQLite add/edit database: Enter to start database wizard\n  ·     SQLite %-mention enabled: on\n  ·     SQLite max rows: 100 rows\n  ·     SQLite query timeout (s): 30\n  ·     SQLite databases (profile): (none) · Enter edits sqlite.json\n  ·     SQLite databases (global): (none) · Enter edits sqlite.json\n  ·   PostgreSQL\n  ·     PostgreSQL tools: off\n  ·     PostgreSQL mode: read-only\n  ·     PostgreSQL statements allowed: changing data, creating, reading (used under read-write)\n  ·     PostgreSQL connections offered: none of 0\n  ·     PostgreSQL set password: Enter to set password for a connection\n  ·     PostgreSQL add/edit connection: Enter to start the connection wizard\n  ·     PostgreSQL %-mention enabled: on\n  ·     PostgreSQL max rows: 100 rows\n  ·     PostgreSQL query timeout (s): 30\n  ·     PostgreSQL connections (profile): (none) · Enter edits postgres.json\n  ·     PostgreSQL connections (global): (none) · Enter edits postgres.json\n  ·   Oracle\n  ·     Oracle tools: off\n  ·     Oracle mode: read-only\n  ·     Oracle statements allowed: changing data, creating, reading (used under read-write)\n  ·     Oracle connections offered: none of 0\n  ·     Oracle set password: Enter to set password for a connection\n  ·     Oracle add/edit connection: Enter to start the connection wizard\n  ·     Oracle %-mention enabled: on\n  ·     Oracle max rows: 100 rows\n  ·     Oracle query timeout (s): 30\n  ·     Oracle connections (profile): (none) · Enter edits oracle.json\n  ·     Oracle connections (global): (none) · Enter edits oracle.json\n  · Devices\n  ·   Camera\n  ·     Camera tool: off\n  ·     Camera shutter: user\n  ·     Camera preview: live\n  ·     Camera device: (first camera)\n  ·     Camera resolution: 1280x720\n  ·     Camera output folder: camera\n  ·     Camera keep in sessions: off\n  ·     Camera watch interval (s): 10\n  ·     Camera watch change (%): 8%\n  ·     Camera watch unprompted: off\n  ·     Camera watch min gap (s): 120\n  ·   Screen\n  ·     Screen tools: off\n  ·     Screen policy: ask\n  ·     Screen preview: on\n  ·     Screen output folder: screen\n  ·     Screen keep in sessions: off\n  ·   Print\n  ·     Print tools: off\n  ·     Print action policy: ask\n  ·     Print default printer: (Windows default)\n  ·     Print font size (pt): 10 pt\n  ·     PDF engine: auto\n  · Home\n  ·   HomeAssistant\n  ·     Home Assistant tools: on\n  ·     Home Assistant URL: (not set)\n  ·     Home Assistant API key: (none)\n  ·     Home Assistant test connection: Enter to ask the server for its version\n  ·     Home Assistant action policy: ask\n  ·     Home Assistant Assist agent: (Home Assistant's default)\n  ·     Home Assistant timeout (s): 10\n  ·   Shopping\n  ·     Shopping tools: off\n  ·     Shopping list: Enter to manage the list\n  ·     Shopping stores: Enter to manage stores\n", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>The Unix twin of <see cref="WithoutThePane_PrintsTheFiveTabsAsLines_ToTheTranscript"/> (2026-10-06, the macOS build): the Mac's shells, languages, picture-less file tools and Keychain.</summary>
    [UnixFact]
    public async Task WithoutThePane_PrintsTheFiveTabsAsLines_ToTheTranscript_Unix()
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

        Assert.Contains("  · Offered\n  ·   Clock (3)\n  ·     get_current_time      on   ", Output);
        Assert.Contains("  ·   Files (12 of 13)\n", Output);   // the picture tools on Image since 2026-10-10
        Assert.Contains("  ·     read_file             off  Reads a text file", Output);   // the console wraps the long line
        Assert.Contains("switched off in /tools", Output);
        Assert.Contains("  ·   Questions (1) (off: no pane)\n", Output);
        // The tabs in strip order (Core Web, Shell, Timers, Ask, Options; Admin Network first; Devices Camera, Screen, Print since 2026-10-16, the user's order); the user's since 2026-10-03: Offered, Ask, Web, Shell, Files, UNC, Print, Camera, Obsidian, SQL, MySQL, Oracle, Claude, Docker, HA, ComfyUI, GitLib, Options. Before it Web right after Offered, then Files, Shell, Ask (the user's order, later on 2026-09-21), Claude (Claude (CLI) until 2026-09-29, the Anthropic API's four rows last since), Obsidian, ComfyUI, SQL, Git (native) (the user's order, 2026-09-27), HA (2026-09-28; second to last since later on 2026-10-01, the user's ask) and Options last (2026-09-22).
        Assert.Contains("  ·   Web\n  ·     Web tools: on\n  ·     Web browser mode: auto\n  ·     Web browser path: " + SettingsMenu.AutoBrowserLabel(FakeBrowserPath) + "\n", Output);
        Assert.Contains("  ·     Web search max results: 20 results\n  ·     Web download max (MB): 50 MB\n  ·   Shell\n  ·     Shell command policy: ask\n", Output);   // Shell after Web again since 2026-10-16 (the user's order; Network between them from later still on 2026-10-15)
        Assert.Contains("  · Admin\n  ·   Network\n  ·     Network tools: off\n", Output);   // Admin first since 2026-10-16 (the user's order; on Core after Web from later still on 2026-10-15)
        Assert.Contains("  ·     Network certificate stores: off\n  ·   EventLog\n  ·     Event log tools: off\n", Output);
        Assert.DoesNotContain("  ·   Services\n", Output);   // off Windows no Services or Tasks menu (2026-10-22, the user's ask)
        Assert.DoesNotContain("  ·   Tasks\n", Output);
        Assert.Contains("  ·     File view image max images: 10 pictures\n  ·     File search max results: 200 results\n  ·   Image\n  ·     Image tools: off\n  ·     Image edit quality: 90\n  ·     Image edit metadata: none\n  ·     Image edit mode: beside-original\n  ·     Image edit output folder: (beside the source)\n  ·     Pasted images folder: pasted\n  ·   UNC\n", Output);
        Assert.Contains("  ·   Shell\n  ·     Shell command policy: ask\n", Output);
        Assert.Contains("  · Core\n  ·   Web\n  ·     Web tools: on\n", Output);   // Web first since 2026-10-16 (the user's order; Ask first until then)
        Assert.Contains("  ·     Shell tool bridge max calls: 50 tool calls\n  ·   Clock\n  ·     Clock tools: on\n  ·   Timers\n  ·     Timer tools: on\n  ·     Timer tone: silent\n  ·   Ask\n  ·     Ask user: on\n  ·     Ask max questions: 10 questions\n  ·     Ask max choices per question: 10 choices\n  ·   Help\n  ·     Help tools: on\n  ·   Options\n  ·     $-mention enabled: on\n", Output);   // Clock after Shell and Help after Ask since 2026-10-18 (the user's asks); Ask after Timers since 2026-10-16 (the user's order); Timers between Shell and Options since later on 2026-10-15   // Shopping between them on 2026-10-11 until it went to Home later that day
        Assert.Contains("  ·     File search max results: 200 results\n  ·   Image\n  ·     Image tools: off\n  ·     Image edit quality: 90\n  ·     Image edit metadata: none\n  ·     Image edit mode: beside-original\n  ·     Image edit output folder: (beside the source)\n  ·     Pasted images folder: pasted\n  ·   UNC\n  ·     UNC tools: off\n  ·     UNC writes: off\n  ·     UNC shares offered: none of 0\n  ·     UNC set password: Enter to set password for a runas share\n  ·     UNC add/edit share: Enter to start share wizard\n  ·     UNC *-mention enabled: on\n  ·     UNC shares (profile): (none) · Enter edits unc.json\n  ·     UNC shares (global): (none) · Enter edits unc.json\n  ·   GitLib\n  ·     GitLib tools: on\n  ·     GitLib diff max lines: 500 lines\n  ·     GitLib log max commits: 20 commits\n  ·     GitLib user email: (not set)\n  ·     GitLib user name: (not set)\n  · Apps\n  ·   Docker\n  ·     Docker tools: off\n  ·     Docker writes: off\n  ·     " + SettingsMenu.FieldName(SettingsField.DockerEnginePipe) + ": " + SettingsMenu.FieldValue(SettingsField.DockerEnginePipe, new AppSettingsData(), "") + "\n  ·   ComfyUI\n  ·     ComfyUI tools: on\n  ·     ComfyUI URL: (not set)\n  ·     ComfyUI workflows offered: none of 0\n  ·     ComfyUI add workflow: Enter to start workflow wizard\n  ·     ComfyUI ^-mention enabled: on\n  ·     ComfyUI timeout (s): 300\n  ·     ComfyUI max images per call: 5 pictures\n  ·     ComfyUI reinforce negatives: on\n  ·     ComfyUI show prompts: on\n  ·     ComfyUI picture strip: on\n  ·     ComfyUI output folder: comfy\n  ·   YouTube\n  ·     YouTube tools: off\n  ·     YouTube API key: (none)\n  ·     YouTube search max results: 8 results\n  ·     YouTube search order: relevance\n  ·     YouTube safe search: moderate\n  ·     YouTube region: (any region)\n  ·     YouTube language: (any language)\n  ·     YouTube open links in: youtube-window\n  ·     YouTube autoplay: on\n  ·     YouTube while speaking: pause\n  ·   Obsidian\n  ·     Obsidian tools: on\n  ·     Obsidian vault: (not set)\n  ·     Obsidian allow delete (.trash): on\n  · Admin\n  ·   Network\n  ·     Network tools: off\n", Output);   // Admin after Apps later still on 2026-10-15 (the admin-tools plan); Obsidian after YouTube since 2026-10-17 (the user's order)
        Assert.Contains("  · Databases\n  ·   SQL\n  ·     SQL tools: on\n  ·     SQL mode: read-only\n  ·     SQL statements allowed: changing data, creating, reading (used under read-write)\n  ·     SQL connections offered: none of 0\n  ·     SQL set password: Enter to set password for a connection\n  ·     SQL add/edit connection: Enter to start the connection wizard\n  ·     SQL %-mention enabled: on\n  ·     SQL max rows: 100 rows\n  ·     SQL query timeout (s): 30\n  ·     SQL query result max chars: 32,000 chars\n  ·     SQL connections (profile): (none) · Enter edits sql.json\n  ·     SQL connections (global): (none) · Enter edits sql.json\n  ·   MySQL\n  ·     MySQL tools: off\n  ·     MySQL mode: read-only\n  ·     MySQL statements allowed: changing data, creating, reading (used under read-write)\n  ·     MySQL connections offered: none of 0\n  ·     MySQL set password: Enter to set password for a connection\n  ·     MySQL add/edit connection: Enter to start the connection wizard\n  ·     MySQL %-mention enabled: on\n  ·     MySQL max rows: 100 rows\n  ·     MySQL query timeout (s): 30\n  ·     MySQL connections (profile): (none) · Enter edits mysql.json\n  ·     MySQL connections (global): (none) · Enter edits mysql.json\n  ·   SQLite\n  ·     SQLite tools: off\n  ·     SQLite mode: read-only\n  ·     SQLite statements allowed: changing data, creating, reading (used under read-write)\n  ·     SQLite databases offered: none of 0\n  ·     SQLite sandbox files: on\n  ·     SQLite add/edit database: Enter to start database wizard\n  ·     SQLite %-mention enabled: on\n  ·     SQLite max rows: 100 rows\n  ·     SQLite query timeout (s): 30\n  ·     SQLite databases (profile): (none) · Enter edits sqlite.json\n  ·     SQLite databases (global): (none) · Enter edits sqlite.json\n  ·   PostgreSQL\n  ·     PostgreSQL tools: off\n  ·     PostgreSQL mode: read-only\n  ·     PostgreSQL statements allowed: changing data, creating, reading (used under read-write)\n  ·     PostgreSQL connections offered: none of 0\n  ·     PostgreSQL set password: Enter to set password for a connection\n  ·     PostgreSQL add/edit connection: Enter to start the connection wizard\n  ·     PostgreSQL %-mention enabled: on\n  ·     PostgreSQL max rows: 100 rows\n  ·     PostgreSQL query timeout (s): 30\n  ·     PostgreSQL connections (profile): (none) · Enter edits postgres.json\n  ·     PostgreSQL connections (global): (none) · Enter edits postgres.json\n  ·   Oracle\n  ·     Oracle tools: off\n  ·     Oracle mode: read-only\n  ·     Oracle statements allowed: changing data, creating, reading (used under read-write)\n  ·     Oracle connections offered: none of 0\n  ·     Oracle set password: Enter to set password for a connection\n  ·     Oracle add/edit connection: Enter to start the connection wizard\n  ·     Oracle %-mention enabled: on\n  ·     Oracle max rows: 100 rows\n  ·     Oracle query timeout (s): 30\n  ·     Oracle connections (profile): (none) · Enter edits oracle.json\n  ·     Oracle connections (global): (none) · Enter edits oracle.json\n  · Devices\n  ·   Camera\n  ·     Camera tool: off\n  ·     Camera shutter: user\n  ·     Camera preview: live\n  ·     Camera device: (first camera)\n  ·     Camera resolution: 1280x720\n  ·     Camera output folder: camera\n  ·     Camera keep in sessions: off\n  ·     Camera watch interval (s): 10\n  ·     Camera watch change (%): 8%\n  ·     Camera watch unprompted: off\n  ·     Camera watch min gap (s): 120\n  ·   Screen\n  ·     Screen tools: off\n  ·     Screen policy: ask\n  ·     Screen preview: on\n  ·     Screen output folder: screen\n  ·     Screen keep in sessions: off\n  ·   Print\n  ·     Print tools: off\n  ·     Print action policy: ask\n  ·     Print default printer: " + SettingsMenu.WindowsDefaultPrinterLabel + "\n  ·     Print font size (pt): 10 pt\n  ·     PDF engine: auto\n  · Home\n  ·   HomeAssistant\n  ·     Home Assistant tools: on\n  ·     Home Assistant URL: (not set)\n  ·     Home Assistant API key: (none)\n  ·     Home Assistant test connection: Enter to ask the server for its version\n  ·     Home Assistant action policy: ask\n  ·     Home Assistant Assist agent: (Home Assistant's default)\n  ·     Home Assistant timeout (s): 10\n  ·   Shopping\n  ·     Shopping tools: off\n  ·     Shopping list: Enter to manage the list\n  ·     Shopping stores: Enter to manage stores\n", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>
    /// The allowed-commands list straight (later still on 2026-09-21, /cmdlist — /tools shell allowed since 2026-10-09 — and the toolbar's lock):
    /// the same list under the same crumb as the Shell tab's row, Enter removing, ESC closing the
    /// pane with the crumb's root put back — the Tools tabs never drawn.
    /// </summary>
    [Fact]
    public async Task ShowAllowedCommands_OpensTheListUnderTheToolsCrumb_EnterRemoves_EscClosesThePane()
    {
        _settings.Update(d => d.ShellCommandAllowed = ["git push", "dotnet build"]);
        var (menu, pane, settings) = PaneMenu();
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // dotnet build removed (Yes, asked since 2026-10-07), then the pane closed

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal(["git push"], _settings.Current.ShellCommandAllowed);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell allowed commands │ " + SettingsMenu.PolicyButtonTitle(_settings.Current.ShellCommandPolicy) + " ") + "\n \n▸ dotnet build\n  git push\n", Output);
        Assert.Contains("  · Shell allowed commands: dotnet build removed\n▸ git push\n", Output);
        Assert.DoesNotContain(Strip, Output);
        Assert.Equal(SettingsMenu.Title, settings.Root);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>
    /// The list's policy button (2026-10-02, the user's ask, as three buttons; one cycling button on P since later on 2026-10-13): from
    /// ask the press is yolo, which asks on the same pane before it saves, the save on the list's status line.
    /// </summary>
    [Fact]
    public async Task ShowAllowedCommands_YoloAsksFirst()
    {
        _settings.Update(d => d.ShellCommandAllowed = ["git push"]);
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('p'), Keys.Char('y'), Keys.Enter, Keys.Escape);   // ask → yolo, yes; close

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal("yolo", _settings.Current.ShellCommandPolicy);
        Assert.Equal(["git push"], _settings.Current.ShellCommandAllowed);
        Assert.Contains(MenuFilter.HintBeforeEsc(SettingsMenu.AllowedCommandsKeys, "", letteredButtons: true), Output);   // type to filter since 2026-10-07; Shift+letter since 2026-10-13
        Assert.Contains("\n" + Titled(SettingsMenu.YoloConfirmQuestion) + "\n", Output);
        Assert.Contains("  · Shell command policy: yolo\n▸ git push\n", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>From yolo the press is off, at once with nothing asked (2026-10-12 as its own button, the user's ask; the cycle since later on 2026-10-13).</summary>
    [Fact]
    public async Task ShowAllowedCommands_FromYolo_OffSavesAtOnce()
    {
        _settings.Update(d => d.ShellCommandPolicy = "yolo");
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('p'), Keys.Escape);

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal("off", _settings.Current.ShellCommandPolicy);
        Assert.DoesNotContain(SettingsMenu.YoloConfirmQuestion, Output);
        Assert.Contains("  · Shell command policy: off\n", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>From off the press is ask, at once with nothing asked (2026-10-02; the cycle since later on 2026-10-13).</summary>
    [Fact]
    public async Task ShowAllowedCommands_FromOff_AskSavesAtOnce()
    {
        _settings.Update(d => d.ShellCommandPolicy = "off");
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('p'), Keys.Escape);

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal("ask", _settings.Current.ShellCommandPolicy);
        Assert.DoesNotContain(SettingsMenu.YoloConfirmQuestion, Output);
        Assert.Contains("  · Shell command policy: ask\n", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>A no to yolo moves on to off, the next in the cycle (later on 2026-10-13): ask → off is a press and a no, never stuck.</summary>
    [Fact]
    public async Task ShowAllowedCommands_YoloRefused_MovesOnToOff()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('p'), Keys.Escape);                       // ask → yolo, ESC: off
        Push(Keys.Char('p'), Keys.Char('p'), Keys.Enter);        // off → ask, ask → yolo, Enter on No: off
        Push(Keys.Escape);                                       // close

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal("off", _settings.Current.ShellCommandPolicy);
        Assert.Contains(SettingsMenu.AllowedCommandsEmptyKeys, Output);   // the button works on the empty list
        Assert.Contains(SettingsMenu.YoloConfirmQuestion, Output);
        Assert.Equal(2, Output.Split("  · " + SettingsMenu.YoloDeclinedNotice + "\n").Length - 1);
        Assert.Contains("  · Shell command policy: ask\n", Output);
        Assert.DoesNotContain("Shell command policy: yolo", Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task ShowAllowedCommands_FromOff_YoloAsksToo()
    {
        _settings.Update(d => d.ShellCommandPolicy = "off");
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('p'), Keys.Char('p'), Keys.Char('y'), Keys.Enter, Keys.Escape);   // off → ask, ask → yolo, yes; close

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal("yolo", _settings.Current.ShellCommandPolicy);
        Assert.Contains(SettingsMenu.YoloConfirmQuestion, Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>A hand-edited policy shows as itself after a ?, and the press goes to off (later on 2026-10-13).</summary>
    [Fact]
    public async Task ShowAllowedCommands_AHandEditedPolicy_GoesToOff()
    {
        _settings.Update(d => d.ShellCommandPolicy = "never");
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('p'), Keys.Escape);

        await menu.ShowAllowedCommandsAsync(CancellationToken.None);

        Assert.Equal("off", _settings.Current.ShellCommandPolicy);
        Assert.Contains(" │ ? never ", Output);
        Assert.DoesNotContain(SettingsMenu.YoloConfirmQuestion, Output);
        Assert.Contains("  · Shell command policy: off\n", Output);
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

        Assert.Contains("  · Shell allowed commands\n  ·   dotnet build\n  ·   git push\n", Output);
        Assert.Contains("  · Shell allowed commands\n  ·   " + SettingsMenu.NoAllowedCommandsRow + "\n", Output);
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
        _settings.Update(d => d.ShellPoliceForbiddenStrings = []);   // from empty: a fresh profile has the starter list since 2026-10-08
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ShellTabTitle));
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // under Shell police: the list, on its add row
        Push(Keys.Enter);
        Push(Typed("rm  -rf"));                              // added as "rm -rf"
        Push(Keys.Enter);
        Push(Typed("Format"));                               // added; A to Z ignoring case puts it first
        Push(Keys.Enter);
        Push(Typed("RM -RF"));                               // already there
        Push(Keys.Enter, Keys.Escape);                       // the slot, ESC: back on the list
        Push(Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);   // rm -rf removed (Yes)
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["Format"], _settings.Current.ShellPoliceForbiddenStrings);
        Assert.Contains("\n" + Titled(ForbiddenListTitle.Replace(ToolsText.Label, CoreCrumb, StringComparison.Ordinal)) + "\n \n▸ " + SettingsMenu.AddForbiddenRow + "\n", Output);   // from the pane: under Standard's crumb (later on 2026-10-10)
        Assert.Contains(SettingsMenu.ForbiddenPaneKeys, Output);
        Assert.Contains("  · " + SettingsMenu.ForbiddenAddedNotice("rm -rf") + "\n▸ " + SettingsMenu.AddForbiddenRow + "\n  rm -rf\n", Output);
        Assert.Contains("  · " + SettingsMenu.ForbiddenAddedNotice("Format") + "\n▸ " + SettingsMenu.AddForbiddenRow + "\n  Format\n  rm -rf\n", Output);
        Assert.Contains("  · Shell police forbidden strings: RM -RF is already in the list\n", Output);
        Assert.Contains("  · Shell police forbidden strings: rm -rf removed\n", Output);
        Assert.Contains("\n▸ Shell police forbidden strings  1 string\n" + Heading("── Commands") + "\n  Shell prefer native tools       on\n", Output);
        pane.Dispose();
    }

    /// <summary>Delete on a forbidden string is Enter's remove (2026-10-16, the user's ask), asked; on the add row it is nothing, no slot opened.</summary>
    [Fact]
    public async Task OnThePane_TheForbiddenStrings_DeleteRemovesAfterAYes_AndIsNothingOnTheAddRow()
    {
        _settings.Update(d => d.ShellPoliceForbiddenStrings = ["Format", "rm -rf"]);
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.ShellTabTitle));
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // under Shell police: the list, on its add row
        Push(Keys.Delete);                                   // nothing
        Push(Keys.Down, Keys.Delete, Keys.Down, Keys.Enter); // Format removed (Yes)
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["rm -rf"], _settings.Current.ShellPoliceForbiddenStrings);
        Assert.Contains(SettingsMenu.RemoveForbiddenQuestion("Format"), Output);
        Assert.Contains("  · Shell police forbidden strings: Format removed\n", Output);
        pane.Dispose();
    }

    /// <summary>Delete on a network host is Enter's remove (2026-10-16, the user's ask), asked; on the add row it is nothing, no slot opened.</summary>
    [Fact]
    public async Task OnThePane_TheNetworkHostList_DeleteRemovesAfterAYes_AndIsNothingOnTheAddRow()
    {
        _settings.Update(d => d.NetworkAllowedHosts = ["example.com", "10.0.0.0/8"]);
        var (menu, pane, _) = PaneMenu();
        Push(ToTab("Network"));
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the allowed hosts: the list, on its add row
        Push(Keys.Delete);                                              // nothing
        Push(Keys.Down, Keys.Delete, Keys.Down, Keys.Enter);            // example.com removed (Yes)
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["10.0.0.0/8"], _settings.Current.NetworkAllowedHosts);
        Assert.Contains(SettingsMenu.RemoveHostQuestion(SettingsField.NetworkAllowedHosts, "example.com"), Output);
        Assert.Contains(SettingsMenu.HostRemovedNotice(SettingsField.NetworkAllowedHosts, "example.com"), Output);
        pane.Dispose();
    }

    /// <summary>The forbidden-strings list's title row on the pane: the crumb, then its restore-defaults button (2026-10-08).</summary>
    private static string ForbiddenListTitle => ToolsText.Label + " › Shell police forbidden strings │ " + SettingsMenu.RestoreForbiddenButton + " ";

    /// <summary>
    /// The command-policy picker's buttons (2026-10-09, the user's call: /cmdlist and /police folded under /tools shell): C opens the
    /// allowed-commands list and P the police page, ESC on either coming back to the picker, whose buttons say the list's count and
    /// the police's state as they stand; ESC there closes the pane with the policy unchanged.
    /// </summary>
    [Fact]
    public async Task ShowSwitch_ThePolicyPicker_COpensTheAllowedList_POpensThePolice_EscComesBack()
    {
        _settings.Update(d => { d.ShellCommandPolicy = "ask"; d.ShellCommandAllowed = ["git push"]; });
        var (menu, pane, settings) = PaneMenu();
        Push(Keys.Char('c'), Keys.Escape);                      // the list, back
        Push(Keys.Char('p'), Keys.Down, Keys.Enter, Keys.Char('y'), Keys.Enter);   // the police page: off picked, yes to the question, back
        Push(Keys.Escape);                                      // the picker closed

        await menu.ShowSwitchAsync(SettingsField.ShellCommandPolicy, CancellationToken.None);

        string picker(bool police) => Titled(ToolsText.Label + " › Shell command policy │ " + string.Join(" · ", SettingsMenu.CommandPolicyPageButtons(1, police).Select(b => b.Title)) + " ");
        Assert.True(Output.Split(picker(police: true)).Length - 1 >= 2, Output);   // before the list and after it
        Assert.Contains(picker(police: false), Output);                         // after the police went off
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Shell allowed commands │ " + SettingsMenu.PolicyButtonTitle(_settings.Current.ShellCommandPolicy) + " ") + "\n", Output);
        Assert.Contains("\n" + ToolsText.Label + " › Shell police │ ", Output);
        Assert.Contains(SettingsMenu.CommandPolicyPageKeys, Output);
        Assert.False(_settings.Current.ShellPolice);
        Assert.Equal("ask", _settings.Current.ShellCommandPolicy);
        Assert.Equal(SettingsMenu.Title, settings.Root);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>The picker's button wording (2026-10-09): the list's count or none, the police's state, each lit while it holds.</summary>
    [Fact]
    public void CommandPolicyPageButtons_Wording()
    {
        Assert.Equal(["≡ allowed (none)", "≡ police (on)"], SettingsMenu.CommandPolicyPageButtons(0, police: true).Select(b => b.Title));
        Assert.Equal(["≡ allowed (3)", "≡ police (off)"], SettingsMenu.CommandPolicyPageButtons(3, police: false).Select(b => b.Title));
        Assert.Equal([false, true], SettingsMenu.CommandPolicyPageButtons(0, police: true).Select(b => b.On));
        Assert.Equal(['c', 'p'], SettingsMenu.CommandPolicyPageButtons(1, police: false).Select(b => b.Key!.Value));
    }

    /// <summary>
    /// The restore-defaults button (2026-10-08, the user's ask): R asks first and No keeps the list; Yes replaces it with the
    /// defaults whole (the user's own string gone); R on the defaults says so and asks nothing.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheForbiddenStringsList_RestoreDefaults_AsksFirst_ReplacesTheList_SaysWhenAlreadyThere()
    {
        _settings.Update(d => d.ShellPoliceForbiddenStrings = ["rm -rf"]);
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('s'));                        // /police' strings button: the list
        Push(Keys.Char('r'), Keys.Char('n'), Keys.Enter);   // asked, No: kept
        Push(Keys.Char('r'), Keys.Char('y'), Keys.Enter);   // asked, Yes: the defaults
        Push(Keys.Char('r'));                        // already the defaults: nothing asked
        Push(Keys.Escape, Keys.Escape);              // the list, then the police page

        await menu.ShowPoliceAsync(CancellationToken.None);

        var defaults = NeonSidekick.Shell.ForbiddenStrings.Defaults;
        Assert.Equal(defaults, _settings.Current.ShellPoliceForbiddenStrings);
        Assert.DoesNotContain("rm -rf", _settings.Current.ShellPoliceForbiddenStrings);
        Assert.Contains("\n" + Titled(ForbiddenListTitle) + "\n", Output);
        Assert.Contains(SettingsMenu.ForbiddenPaneKeys, Output);
        string question = SettingsMenu.RestoreForbiddenQuestion(1, defaults.Count);
        int asked = Output.IndexOf(question, StringComparison.Ordinal);
        int kept = Output.IndexOf("  · " + ChatScreen.KeptNotice + "\n", StringComparison.Ordinal);
        int restored = Output.IndexOf("  · " + SettingsMenu.ForbiddenRestoredNotice(defaults.Count) + "\n", StringComparison.Ordinal);
        int already = Output.IndexOf("  · " + SettingsMenu.ForbiddenAlreadyDefaultsNotice + "\n", StringComparison.Ordinal);
        Assert.True(asked >= 0 && asked < kept && kept < restored && restored < already, Output);
        Assert.True(Output.LastIndexOf(question, StringComparison.Ordinal) < restored, Output);   // never asked once the list is the defaults
        pane.Dispose();
    }

    /// <summary>The restore-defaults wording (2026-10-08): an empty list is filled, a list with strings is replaced and says so.</summary>
    [Fact]
    public void RestoreForbiddenDefaults_Wording()
    {
        Assert.Equal("Restore the 49 default forbidden strings?", SettingsMenu.RestoreForbiddenQuestion(0, 49));
        Assert.Equal("Replace the list (1 string) with the 49 default strings? Any you added are removed.", SettingsMenu.RestoreForbiddenQuestion(1, 49));
        Assert.Equal("Replace the list (12 strings) with the 48 default strings? Any you added are removed.", SettingsMenu.RestoreForbiddenQuestion(12, 48));
        Assert.Equal("Shell police forbidden strings: 49 defaults restored", SettingsMenu.ForbiddenRestoredNotice(49));
        Assert.Equal("Shell police forbidden strings: already the defaults", SettingsMenu.ForbiddenAlreadyDefaultsNotice);
        Assert.Equal([new MenuButton("↺ restore defaults", 'r')], SettingsMenu.ForbiddenButtons);
    }

    /// <summary>
    /// <c>/police</c>' strings button (2026-10-03, the user's pick): S opens the forbidden-strings list over the police page, ESC
    /// there comes back to the page, the switch untouched.
    /// </summary>
    /// <summary>The strings button counts the list and is lit while it holds any (2026-10-04, the user's ask), dim while empty.</summary>
    [Fact]
    public void PoliceButtons_CountTheStrings_LitWhenThereAreAny()
    {
        Assert.Equal("≡ strings (none)", SettingsMenu.PoliceStringsTitle(0));
        Assert.Equal("≡ strings (1)", SettingsMenu.PoliceStringsTitle(1));
        Assert.Equal("≡ strings (12)", SettingsMenu.PoliceStringsTitle(12));
        Assert.Equal([new MenuButton("≡ strings (none)", 's', false)], SettingsMenu.PoliceButtons(0));
        Assert.Equal([new MenuButton("≡ strings (3)", 's', true)], SettingsMenu.PoliceButtons(3));
    }

    [Fact]
    public async Task ShowPolice_TheStringsButton_OpensTheList_AndComesBack()
    {
        _settings.Update(d => d.ShellPoliceForbiddenStrings = []);   // from empty: a fresh profile has the starter list since 2026-10-08
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('s'), Keys.Enter);
        Push(Typed("shutdown"));
        Push(Keys.Escape, Keys.Escape);   // the list, then the police page

        await menu.ShowPoliceAsync(CancellationToken.None);

        Assert.Equal(["shutdown"], _settings.Current.ShellPoliceForbiddenStrings);
        Assert.True(_settings.Current.ShellPolice);
        // The button counts the strings (2026-10-04): none before, one when the page comes back.
        string police = "\n" + Titled(ToolsText.Label + " › Shell police │ ≡ strings (none) ") + "\n";
        string policeAfter = "\n" + Titled(ToolsText.Label + " › Shell police │ ≡ strings (1) ") + "\n";
        string list = "\n" + Titled(ForbiddenListTitle) + "\n";
        Assert.Contains(SettingsMenu.PoliceToggleKeys, Output);
        Assert.True(Output.IndexOf(police, StringComparison.Ordinal) < Output.IndexOf(list, StringComparison.Ordinal), Output);
        Assert.True(Output.IndexOf(list, StringComparison.Ordinal) < Output.LastIndexOf(policeAfter, StringComparison.Ordinal), Output);   // back on the page
        Assert.Contains("  · " + SettingsMenu.ForbiddenAddedNotice("shutdown") + "\n", Output);
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

        Assert.Contains("  · Shell police: on\n  · Shell police forbidden strings\n  ·   Format\n  ·   rm -rf\n", Output);
        Assert.Equal(["Shell police forbidden strings", "  " + SettingsMenu.NoAllowedCommandsRow], ToolsMenu.ForbiddenStringLines(new AppSettingsData { ShellPoliceForbiddenStrings = [] }));
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
        Assert.Contains(SettingsMenu.LastLanguageError, Output);
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
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, """{ "connections": { "aw": { "server": "x", "database": "db", "auth": "windows" }, "prod": { "server": "y", "database": "db", "auth": "windows" } } }""");
        var (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Down, Keys.Down, Keys.Enter]);   // SQL, the offered row
        Push(Keys.Char('a'));
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["aw", "prod"], _settings.Current.SqlConnectionsOffered);

        (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Down, Keys.Down, Keys.Enter]);
        Push(Keys.Char('n'));
        Push(Keys.Escape, Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

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
        Push(Keys.Escape, Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

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
        Push(Keys.Escape, Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["pony-txt2img"], _settings.Current.ComfyWorkflowsOffered);
        Assert.Matches(@"ComfyUI workflows offered +none of 2", Output);   // the column is the tab's widest label
        Assert.Matches(@"ComfyUI workflows offered +1 of 2", Output);
        Assert.Contains("[ ] juggernaut-xl  ", Output);

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
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

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
        Assert.Contains("Tested 'ponydiffusionv6xl' in ", Output);
        Assert.Contains("Added workflow 'ponydiffusionv6xl' to ", Output);
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
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

        await menu.ShowAsync(CancellationToken.None);

        string json = Path.Combine(_settings.GlobalComfyDirectory, "my-export.json");
        Assert.True(NeonSidekick.Comfy.ComfyWorkflow.TryLoad(json, out var workflow, out string? problem), problem);
        Assert.Equal(new NeonSidekick.Comfy.ComfyDefaults(832, 1216, 20, 6.5, "score_4, blurry"), workflow!.Defaults);
        Assert.Contains("Found {{prompt}} → node 6 CLIPTextEncode.text", Output);
        Assert.Equal(["other", "my-export"], _settings.Current.ComfyWorkflowsOffered);
        Assert.Contains(SettingsMenu.ComfyWizardSaveHiddenRow[..40], Output);
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
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.ComfyWizardCancelledNotice, Output);
        Assert.Contains("a workflow named 'juggernautxl_ragnarok' is already in", Output);
        Assert.Equal(["juggernautxl_ragnarok.json"], Directory.GetFiles(_settings.GlobalComfyDirectory).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(_settings.ProfileComfyDirectory));
    }
}
