using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Timers;
using NeonSidekick.UI;
using NeonSidekick.Web;
using Spectre.Console;

namespace NeonSidekick.Tests;

public class ToolsTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Every group the screen has, as <see cref="ChatScreen"/> builds them: clock, timers, the 15 file tools, the 4 web tools, memory, the 2 skill tools, the session tool, ask_user.</summary>
    private IReadOnlyList<ToolGroup> Groups(bool toolsEnabled = true, bool filesEnabled = true, bool webEnabled = true, bool memoryEnabled = true, bool askEnabled = true, bool paneOn = true, bool skillsEnabled = true, bool sessionsEnabled = true, IReadOnlySet<string>? disabled = null, bool skillInstalled = true)
    {
        Directory.CreateDirectory(_dir);
        var files = new NeonSidekick.Files.WorkingDirectory(() => _dir, _time);
        var web = ChatScreen.WebTools(new WebAccess(new HttpClient(new StubHttpMessageHandler()), new FakeHeadlessBrowser(), _time), files, () => new AppSettingsData());
        var questions = ChatScreen.AskTools((_, _) => Task.FromResult<IReadOnlyList<AskAnswer>?>(null), () => new AppSettingsData());
        var catalog = new SkillCatalog(() => new SkillRoots(Path.Combine(_dir, "p"), Path.Combine(_dir, "g"), Path.Combine(_dir, "x")));
        var skills = ChatScreen.SkillTools(catalog, () => catalog.Roots, () => false);
        using var store = new NeonSidekick.Sessions.SessionStore(Path.Combine(_dir, "sessions"));
        var sessions = ChatScreen.SessionTools(store, () => new AppSettingsData(), () => null, TimeProvider.System);
        return SystemPromptSummary.ToolGroups(
            ChatScreen.ClockTools(_time),
            ChatScreen.TimerTools(new TimerBoard(_time, () => { })),
            ChatScreen.FileTools(files, () => true, _ => { }, () => new AppSettingsData()),
            ChatScreen.MemoryTools(new MemoryStore(_dir, _time)),
            memoryEnabled, toolsEnabled, web, webEnabled, filesEnabled, questions, askEnabled, paneOn, skills, skillsEnabled, sessions, sessionsEnabled, disabled, skillInstalled);
    }

    private ToolsFacts Facts(IReadOnlyList<string>? disabled = null, bool toolsEnabled = true, bool filesEnabled = true, bool webEnabled = true, bool memoryEnabled = true, bool askEnabled = true, bool paneOn = true, bool skillsEnabled = true, bool sessionsEnabled = true, bool skillInstalled = true)
    {
        var set = ToolsText.DisabledSet(disabled ?? []);
        return new ToolsFacts(Groups(toolsEnabled, filesEnabled, webEnabled, memoryEnabled, askEnabled, paneOn, skillsEnabled, sessionsEnabled, set, skillInstalled), toolsEnabled, set);
    }

    private static string Cyan(string text) => $"[{Theme.AccentSecondary.ToMarkup()}]{Markup.Escape(text)}[/]";
    private static string Heading(string label, string count, string? note = null) => SectionRule.Markup(label, count, note);
    private static string Ink(string text) => Theme.ColorMarkup(Theme.Ink, text);

    [Fact]
    public void Labels_ArePinned()
    {
        Assert.Equal("🛠️ Tools", ToolsText.Label);
        Assert.Equal(["Offered", "Ask", "Web", "Shell", "Files", "UNC", "Print", "Camera", "Screen", "YouTube", "Obsidian", "SQL", "MySQL", "SQLite", "Postgres", "Oracle", "ClaudeCLI", "Docker", "HA", "ComfyUI", "GitLib", "Options"], ToolsText.TabTitles);   // YouTube after Screen 2026-10-05; the user's order since 2026-10-03 (ClaudeCLI "Claude" until 2026-10-04); before it Camera after Ask and Docker after UNC 2026-10-02; HA second to last, before Options, later on 2026-10-01 (the user's ask), Print after Claude with it; UNC after MySQL later still on 2026-09-30; Oracle after SQL and MySQL after Oracle since 2026-09-30; Print after Home Assistant since later on 2026-09-28; Home Assistant after Claude (CLI) since 2026-09-28; the user's order since 2026-09-27 (Git (native), Obsidian, SQL, ComfyUI, Claude after Ask before); SQL 2026-09-23, Obsidian 2026-09-22, Options last later that day   // Options second since later on 2026-09-19; Git since 2026-09-20, Shell since 2026-09-21; the user's order and Git (native) since later on 2026-09-21 (alphabetical before)
        Assert.Equal("(off: File tools is off)", ToolsText.GroupOffSuffix("File tools"));
        Assert.Equal("read_file: off", ToolsText.FlippedNotice("read_file", false));
        Assert.Equal("read_file: on", ToolsText.FlippedNotice("read_file", true));
        Assert.Equal(22, ToolsText.NameWidth);
        Assert.Equal(SystemPromptSummary.ToolNameWidth, ToolsText.NameWidth);
        Assert.Equal(5, ToolsText.StateWidth);
        // The summary's per-tool notes (2026-09-19).
        Assert.Equal("recall_memory is off in /tools", SystemPromptSummary.ToolOff(RecallMemoryTool.ToolName));
        Assert.Equal("Files (15)", SystemPromptSummary.GroupName("Files", 15, 15));
        Assert.Equal("Files (13 of 15)", SystemPromptSummary.GroupName("Files", 13, 15));
        Assert.Equal("15", SystemPromptSummary.GroupCount(15, 15));
        Assert.Equal("13 of 15", SystemPromptSummary.GroupCount(13, 15));
        // The rule shows a suffix without its brackets (2026-10-03); anything else as it is.
        Assert.Equal("off: File tools is off", ToolsText.Bare("(off: File tools is off)"));
        Assert.Equal("off", ToolsText.Bare("off"));
        Assert.Equal("", ToolsText.Bare(""));
    }

    [Fact]
    public void Flip_AddsAndRemoves_SortedOrdinal_NoDuplicates()
    {
        Assert.Equal(["read_file"], ToolsText.Flip([], "read_file"));
        Assert.Equal(["delete", "read_file"], ToolsText.Flip(["read_file"], "delete"));
        Assert.Equal(["delete"], ToolsText.Flip(["delete", "read_file"], "read_file"));
        Assert.Empty(ToolsText.Flip(["read_file"], "read_file"));
        // A hand-edited file's doubles go at the first flip; the order is ordinal (upper before lower).
        Assert.Equal(["Zip", "copy", "read_file"], ToolsText.Flip(["read_file", "read_file", "Zip"], "copy"));
    }

    [Fact]
    public void DisabledSet_IsOrdinal_AndEmptyForAnEmptyList()
    {
        var set = ToolsText.DisabledSet(["read_file", "Zip"]);
        Assert.True(set.Contains("read_file"));
        Assert.False(set.Contains("Read_File"));
        Assert.True(set.Contains("Zip"));
        Assert.Empty(ToolsText.DisabledSet([]));
        Assert.True(ToolsText.IsOn(Facts(["read_file"]), "write_file"));
        Assert.False(ToolsText.IsOn(Facts(["read_file"]), "read_file"));
    }

    /// <summary>A tool row the turn offers: the name in the label colour, the state in ink, the description dim.</summary>
    private static string OnRow(AIFunction tool, bool on) => Cyan(tool.Name.PadRight(22)) + Ink((on ? "on" : "off").PadRight(5)) + Theme.DimMarkup(tool.Description);

    /// <summary>A tool row the turn does not offer: the whole row dim, the note after the description.</summary>
    private static string DimRow(AIFunction tool, bool on, string note = "", bool groupOff = false) => Theme.DimMarkup(tool.Name.PadRight(22) + (on ? groupOff ? "(on)" : "on" : "off").PadRight(5) + tool.Description + (note.Length > 0 ? "  " + note : ""));   // "(on)" under an off group since 2026-10-04

    private static AIFunction ToolNamed(ToolsFacts facts, string name) => facts.Groups.SelectMany(g => g.Tools).Single(t => t.Name == name);

    /// <summary>
    /// The Offered tab's footer (2026-10-05, the user's ask: the band stood blank there): the cursor's tool's whole description,
    /// and on its last line why the turn does not offer it — the tool's own note, else its off group's; null for no such tool.
    /// </summary>
    [Fact]
    public void ToolFooter_IsTheWholeDescription_WithWhyItIsNotOffered()
    {
        var facts = Facts();
        var read = ToolNamed(facts, ReadFileTool.ToolName);
        Assert.Equal(new MenuFooter(read.Description), ToolsText.ToolFooter(facts, ReadFileTool.ToolName));
        Assert.Null(ToolsText.ToolFooter(facts, "no_such_tool"));

        var webOff = Facts(webEnabled: false);
        var web = webOff.Groups.Single(g => g.Tools.Any(t => t.Name == DownloadFileTool.ToolName));
        Assert.False(web.Offered);
        Assert.Equal(new MenuFooter(ToolNamed(webOff, DownloadFileTool.ToolName).Description, web.Note), ToolsText.ToolFooter(webOff, DownloadFileTool.ToolName));

        // A tool's own note wins (load_skill with no skill installed, in the offered Skills group).
        var noSkill = Facts(skillInstalled: false);
        var notes = noSkill.Groups.Where(g => g.Offered).SelectMany(g => g.ToolNotes).ToList();
        Assert.NotEmpty(notes);
        Assert.All(notes, n => Assert.Equal(n.Value, ToolsText.ToolFooter(noSkill, n.Key)!.Last));
    }

    [Fact]
    public void OfferedRows_ListEveryGroup_WithHeadingsAndOnOff_TheToolBesideItsRow()
    {
        var facts = Facts();
        var rows = ToolsText.OfferedRows(facts);

        // Eight headings, a gap before each but the first (2026-10-03) and 30 tools (31 until restore went, 2026-10-01) (the screen's count: the timers and ask_user included), every tool row on, the name beside it.
        Assert.Equal(8, rows.Count(r => r.Heading));
        Assert.Equal(7, rows.Count(r => r.Markup.Length == 0 && r.Tool is null && !r.Heading));
        Assert.Equal(32, rows.Count(r => r.Tool is not null));   // 30 until the image tools, 2026-10-04
        // Alphabetical since 2026-10-04 (the user's ask).
        Assert.Equal(["── Clock · 3", "── Files · 16", "── Memory · 2", "── Questions · 1", "── Sessions · 1", "── Skills · 2", "── Timers · 3", "── Web · 4"], rows.Where(r => r.Heading).Select(r => Markup.Remove(r.Markup)));
        Assert.Equal((Heading("Clock", "3"), (string?)null, true), rows[0]);
        Assert.Equal((OnRow(ToolNamed(facts, GetCurrentTimeTool.ToolName), true), GetCurrentTimeTool.ToolName, false), rows[1]);
        Assert.Equal(("", (string?)null, false), rows[4]);   // the gap before Files
        Assert.Equal((Heading("Files", "16"), (string?)null, true), rows[5]);
        Assert.Equal((OnRow(ToolNamed(facts, ReadFileTool.ToolName), true), ReadFileTool.ToolName, false), rows.Single(r => r.Tool == ReadFileTool.ToolName));
        Assert.Equal((OnRow(ToolNamed(facts, AskUserTool.ToolName), true), AskUserTool.ToolName, false), rows.Single(r => r.Tool == AskUserTool.ToolName));
        Assert.Equal(DownloadFileTool.ToolName, rows[^1].Tool);   // Web last
        Assert.Equal(1, ToolsText.FirstToolRow(rows));
        Assert.Equal(Enumerable.Range(0, rows.Count).Where(i => rows[i].Heading), ToolsText.HeadingRows(rows).Order());
        Assert.Equal(facts.Groups.SelectMany(g => g.Tools).Select(t => t.Name), rows.Where(r => r.Tool is not null).Select(r => r.Tool));
    }

    [Fact]
    public void OfferedRows_UnderAFilter_KeepTheToolsWhoseNameOrDescriptionHoldsIt_TheEmptyGroupsGo()
    {
        // 2026-10-03 (the user's ask): case folded, the name or the description; a group with no match goes with its gap and
        // heading, a kept one's heading still counts the whole group.
        var facts = Facts();
        var rows = ToolsText.OfferedRows(facts, "READ_FILE");

        Assert.Contains(rows, r => r.Tool == ReadFileTool.ToolName);
        Assert.All(rows.Where(r => r.Tool is not null), r => Assert.True(MenuFilter.Matches("read_file", r.Tool!, ToolNamed(facts, r.Tool!).Description)));
        Assert.True(rows[0].Heading);
        Assert.Equal(rows.Count(r => r.Heading) - 1, rows.Count(r => r.Markup.Length == 0 && !r.Heading));
        Assert.Contains((Heading("Files", "16"), (string?)null, true), rows);
        Assert.Equal(ToolsText.ToolCount(rows), rows.Count(r => r.Tool is not null));
        Assert.Equal(ToolsText.FirstToolRow(rows), rows.ToList().FindIndex(r => r.Tool is not null));

        // A word of the description alone keeps the tool.
        string description = ToolNamed(facts, GetCurrentTimeTool.ToolName).Description;
        string word = description.Split(' ').First(w => w.Length >= 5 && !GetCurrentTimeTool.ToolName.Contains(w, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ToolsText.OfferedRows(facts, word), r => r.Tool == GetCurrentTimeTool.ToolName);

        // Nothing kept: the one no-match row; nothing typed: every row.
        Assert.Equal([(MenuFilter.NoMatchRow("zzzz"), (string?)null, false)], ToolsText.OfferedRows(facts, "zzzz"));
        Assert.Equal(ToolsText.OfferedRows(facts), ToolsText.OfferedRows(facts, ""));
        Assert.Equal(Theme.DimMarkup(ToolsText.OffLine), ToolsText.OfferedRows(Facts(toolsEnabled: false), "zzzz")[0].Markup);
        Assert.EndsWith(MenuFilter.TypeAndCloseKeys, ToolsText.OfferedKeys, StringComparison.Ordinal);
    }

    [Fact]
    public void OfferedRows_ADisabledTool_ReadsOff_DimWithItsNote_AndTheHeadingCountsTheRest()
    {
        var facts = Facts(["read_file", "web_search"]);
        var rows = ToolsText.OfferedRows(facts);

        Assert.Equal(Heading("Files", "15 of 16"), rows.First(r => r.Heading && r.Markup.Contains("Files", StringComparison.Ordinal)).Markup);
        Assert.Equal(Heading("Web", "3 of 4"), rows.First(r => r.Heading && r.Markup.Contains("Web", StringComparison.Ordinal)).Markup);
        Assert.Equal(DimRow(ToolNamed(facts, ReadFileTool.ToolName), false, "not offered: switched off in /tools"), rows.Single(r => r.Tool == ReadFileTool.ToolName).Markup);
        Assert.Equal(DimRow(ToolNamed(facts, WebSearchTool.ToolName), false, "not offered: switched off in /tools"), rows.Single(r => r.Tool == WebSearchTool.ToolName).Markup);
        Assert.Equal(OnRow(ToolNamed(facts, WriteFileTool.ToolName), true), rows.Single(r => r.Tool == WriteFileTool.ToolName).Markup);
    }

    [Fact]
    public void OfferedRows_AGroupOff_SuffixesTheHeading_DimsItsRows_NotTheHeading_AndTheValuesStillRead()
    {
        var facts = Facts(["copy"], filesEnabled: false, askEnabled: false);
        var rows = ToolsText.OfferedRows(facts);

        // The heading keeps the section colour with the group off (later on 2026-09-20, the user's call); the suffix (on the rule, bare, 2026-10-03) and the rows are dim.
        Assert.Equal(Heading("Files", "15 of 16", "off: File tools is off"), rows.First(r => r.Heading && r.Markup.Contains("Files", StringComparison.Ordinal)).Markup);
        Assert.Equal(DimRow(ToolNamed(facts, ReadFileTool.ToolName), true, groupOff: true), rows.Single(r => r.Tool == ReadFileTool.ToolName).Markup);        // still on, dim, "(on)": its group is off (2026-10-04)
        Assert.Equal(DimRow(ToolNamed(facts, CopyTool.ToolName), false, "not offered: switched off in /tools"), rows.Single(r => r.Tool == CopyTool.ToolName).Markup);   // off, its own note first
        // Questions off by its switch: the same shape; download_file under File tools off carries the file-tools reason, its group's count untouched.
        Assert.Equal(Heading("Questions", "1", "off: Ask user is off"), rows.First(r => r.Heading && r.Markup.Contains("Questions", StringComparison.Ordinal)).Markup);
        Assert.Equal(DimRow(ToolNamed(facts, DownloadFileTool.ToolName), true, "not offered: file tools is off"), rows.Single(r => r.Tool == DownloadFileTool.ToolName).Markup);
        Assert.Equal(Heading("Web", "4"), rows.First(r => r.Heading && r.Markup.Contains("Web", StringComparison.Ordinal)).Markup);
        Assert.Equal("(off: Ask user is off)", ToolsText.HeadingSuffix(Facts(askEnabled: false).Groups.Single(g => g.Label == "Questions"), toolsEnabled: true));
        Assert.Equal("(off: no pane)", ToolsText.HeadingSuffix(Facts(paneOn: false).Groups.Single(g => g.Label == "Questions"), toolsEnabled: true));
        Assert.Equal("(off: Memory mode is disabled)", ToolsText.HeadingSuffix(Facts(memoryEnabled: false).Groups.Single(g => g.Label == "Memory"), toolsEnabled: true));
        Assert.Equal("", ToolsText.HeadingSuffix(Facts().Groups[0], toolsEnabled: true));
        Assert.Equal("", ToolsText.HeadingSuffix(Facts(toolsEnabled: false).Groups[2], toolsEnabled: false));   // the off line says it once
    }

    [Fact]
    public void OfferedRows_LlmToolsOff_OpenWithTheOffLine_EveryToolRowDim_TheHeadingsNot_TheValuesStillRead()
    {
        var facts = Facts(["zip"], toolsEnabled: false);
        var rows = ToolsText.OfferedRows(facts);

        Assert.Equal((Theme.DimMarkup(ToolsText.OffLine), (string?)null, false), rows[0]);
        Assert.Equal(1 + 8 + 7 + 32, rows.Count);
        Assert.Equal(Heading("Clock", "3"), rows[1].Markup);   // a heading never dims (later on 2026-09-20)
        Assert.Equal(DimRow(ToolNamed(facts, GetCurrentTimeTool.ToolName), true), rows[2].Markup);
        Assert.Equal(DimRow(ToolNamed(facts, ZipTool.ToolName), false, "not offered: switched off in /tools"), rows.Single(r => r.Tool == ZipTool.ToolName).Markup);
        Assert.Equal(2, ToolsText.FirstToolRow(rows));
    }

    [Fact]
    public void OfferedRows_LoadSkillWithNoSkill_IsNotedNotHidden()
    {
        var facts = Facts(skillInstalled: false);
        var rows = ToolsText.OfferedRows(facts);

        Assert.Equal(DimRow(ToolNamed(facts, LoadSkillTool.ToolName), true, "not offered: no skill installed"), rows.Single(r => r.Tool == LoadSkillTool.ToolName).Markup);
        Assert.Equal(OnRow(ToolNamed(facts, SkillEditorTool.ToolName), true), rows.Single(r => r.Tool == SkillEditorTool.ToolName).Markup);
        Assert.Equal(Heading("Skills", "2"), rows.First(r => r.Heading && r.Markup.Contains("Skills", StringComparison.Ordinal)).Markup);
    }

    [Fact]
    public void OfferedLines_AreTheGroupsAsPlainLines_WithOnOffAndTheNotes()
    {
        var facts = Facts(["read_file"], askEnabled: false);
        var lines = ToolsText.OfferedLines(facts).ToList();

        Assert.Equal("Clock (3)", lines[0]);
        Assert.Equal("  get_current_time      on   " + ToolNamed(facts, GetCurrentTimeTool.ToolName).Description, lines[1]);
        Assert.Contains("Files (15 of 16)", lines);
        Assert.Contains("  read_file             off  " + ToolNamed(facts, ReadFileTool.ToolName).Description + " — not offered: switched off in /tools", lines);
        Assert.Contains("Questions (1) (off: Ask user is off)", lines);
        Assert.Equal(8 + 32, lines.Count);
        Assert.Equal(ToolsText.OffLine, ToolsText.OfferedLines(Facts(toolsEnabled: false)).First());
    }
}
