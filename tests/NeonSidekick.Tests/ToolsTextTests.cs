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
    private static string Heading(string text) => $"[{Theme.SectionHeading.ToMarkup()}]{Markup.Escape(text)}[/]";
    private static string Ink(string text) => Theme.ColorMarkup(Theme.Ink, text);

    [Fact]
    public void Labels_ArePinned()
    {
        Assert.Equal("🛠️ Tools", ToolsText.Label);
        Assert.Equal(["Offered", "Web", "Files", "Shell", "Ask", "Claude", "Home Assistant", "Print", "Obsidian", "ComfyUI", "SQL", "Git (native)", "Options"], ToolsText.TabTitles);   // Print after Home Assistant since later on 2026-09-28; Home Assistant after Claude (CLI) since 2026-09-28; the user's order since 2026-09-27 (Git (native), Obsidian, SQL, ComfyUI, Claude after Ask before); SQL 2026-09-23, Obsidian 2026-09-22, Options last later that day   // Options second since later on 2026-09-19; Git since 2026-09-20, Shell since 2026-09-21; the user's order and Git (native) since later on 2026-09-21 (alphabetical before)
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
    private static string DimRow(AIFunction tool, bool on, string note = "") => Theme.DimMarkup(tool.Name.PadRight(22) + (on ? "on" : "off").PadRight(5) + tool.Description + (note.Length > 0 ? "  " + note : ""));

    private static AIFunction ToolNamed(ToolsFacts facts, string name) => facts.Groups.SelectMany(g => g.Tools).Single(t => t.Name == name);

    [Fact]
    public void OfferedRows_ListEveryGroup_WithHeadingsAndOnOff_TheToolBesideItsRow()
    {
        var facts = Facts();
        var rows = ToolsText.OfferedRows(facts);

        // Eight headings and 31 tools (the screen's count: the timers and ask_user included), every tool row on, the name beside it.
        Assert.Equal(8, rows.Count(r => r.Tool is null));
        Assert.Equal(31, rows.Count(r => r.Tool is not null));
        Assert.Equal(["Clock (3)", "Timers (3)", "Files (15)", "Web (4)", "Memory (2)", "Skills (2)", "Sessions (1)", "Questions (1)"], rows.Where(r => r.Tool is null).Select(r => Markup.Remove(r.Markup)));
        Assert.Equal((Heading("Clock (3)"), (string?)null), rows[0]);
        Assert.Equal((OnRow(ToolNamed(facts, GetCurrentTimeTool.ToolName), true), GetCurrentTimeTool.ToolName), rows[1]);
        Assert.Equal((OnRow(ToolNamed(facts, ReadFileTool.ToolName), true), ReadFileTool.ToolName), rows.Single(r => r.Tool == ReadFileTool.ToolName));
        Assert.Equal((OnRow(ToolNamed(facts, AskUserTool.ToolName), true), AskUserTool.ToolName), rows[^1]);
        Assert.Equal(1, ToolsText.FirstToolRow(rows));
        Assert.Equal(facts.Groups.SelectMany(g => g.Tools).Select(t => t.Name), rows.Where(r => r.Tool is not null).Select(r => r.Tool));
    }

    [Fact]
    public void OfferedRows_ADisabledTool_ReadsOff_DimWithItsNote_AndTheHeadingCountsTheRest()
    {
        var facts = Facts(["read_file", "web_search"]);
        var rows = ToolsText.OfferedRows(facts);

        Assert.Equal("Files (14 of 15)", Markup.Remove(rows.First(r => r.Markup.Contains("Files (", StringComparison.Ordinal)).Markup));
        Assert.Equal("Web (3 of 4)", Markup.Remove(rows.First(r => r.Markup.Contains("Web (", StringComparison.Ordinal)).Markup));
        Assert.Equal(DimRow(ToolNamed(facts, ReadFileTool.ToolName), false, "not offered: switched off in /tools"), rows.Single(r => r.Tool == ReadFileTool.ToolName).Markup);
        Assert.Equal(DimRow(ToolNamed(facts, WebSearchTool.ToolName), false, "not offered: switched off in /tools"), rows.Single(r => r.Tool == WebSearchTool.ToolName).Markup);
        Assert.Equal(OnRow(ToolNamed(facts, WriteFileTool.ToolName), true), rows.Single(r => r.Tool == WriteFileTool.ToolName).Markup);
    }

    [Fact]
    public void OfferedRows_AGroupOff_SuffixesTheHeading_DimsItsRows_NotTheHeading_AndTheValuesStillRead()
    {
        var facts = Facts(["copy"], filesEnabled: false, askEnabled: false);
        var rows = ToolsText.OfferedRows(facts);

        // The heading keeps the section colour with the group off (later on 2026-09-20, the user's call); the suffix and the rows are dim.
        Assert.Equal(Heading("Files (14 of 15)") + Theme.DimMarkup(" (off: File tools is off)"), rows.First(r => r.Markup.Contains("Files (", StringComparison.Ordinal)).Markup);
        Assert.Equal(DimRow(ToolNamed(facts, ReadFileTool.ToolName), true), rows.Single(r => r.Tool == ReadFileTool.ToolName).Markup);        // still on, dim
        Assert.Equal(DimRow(ToolNamed(facts, CopyTool.ToolName), false, "not offered: switched off in /tools"), rows.Single(r => r.Tool == CopyTool.ToolName).Markup);   // off, its own note first
        // Questions off by its switch: the same shape; download_file under File tools off carries the file-tools reason, its group's count untouched.
        Assert.Equal(Heading("Questions (1)") + Theme.DimMarkup(" (off: Ask user is off)"), rows.First(r => r.Markup.Contains("Questions (", StringComparison.Ordinal)).Markup);
        Assert.Equal(DimRow(ToolNamed(facts, DownloadFileTool.ToolName), true, "not offered: file tools is off"), rows.Single(r => r.Tool == DownloadFileTool.ToolName).Markup);
        Assert.Equal(Heading("Web (4)"), rows.First(r => r.Markup.Contains("Web (", StringComparison.Ordinal)).Markup);
        Assert.Equal("(off: Ask user is off)", ToolsText.HeadingSuffix(Facts(askEnabled: false).Groups[^1], toolsEnabled: true));
        Assert.Equal("(off: no pane)", ToolsText.HeadingSuffix(Facts(paneOn: false).Groups[^1], toolsEnabled: true));
        Assert.Equal("(off: Memory is off)", ToolsText.HeadingSuffix(Facts(memoryEnabled: false).Groups[4], toolsEnabled: true));
        Assert.Equal("", ToolsText.HeadingSuffix(Facts().Groups[0], toolsEnabled: true));
        Assert.Equal("", ToolsText.HeadingSuffix(Facts(toolsEnabled: false).Groups[2], toolsEnabled: false));   // the off line says it once
    }

    [Fact]
    public void OfferedRows_LlmToolsOff_OpenWithTheOffLine_EveryToolRowDim_TheHeadingsNot_TheValuesStillRead()
    {
        var facts = Facts(["zip"], toolsEnabled: false);
        var rows = ToolsText.OfferedRows(facts);

        Assert.Equal((Theme.DimMarkup(ToolsText.OffLine), (string?)null), rows[0]);
        Assert.Equal(1 + 8 + 31, rows.Count);
        Assert.Equal(Heading("Clock (3)"), rows[1].Markup);   // a heading never dims (later on 2026-09-20)
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
        Assert.Equal(Heading("Skills (2)"), rows.First(r => r.Markup.Contains("Skills (", StringComparison.Ordinal)).Markup);
    }

    [Fact]
    public void OfferedLines_AreTheGroupsAsPlainLines_WithOnOffAndTheNotes()
    {
        var facts = Facts(["read_file"], askEnabled: false);
        var lines = ToolsText.OfferedLines(facts).ToList();

        Assert.Equal("Clock (3)", lines[0]);
        Assert.Equal("  get_current_time      on   " + ToolNamed(facts, GetCurrentTimeTool.ToolName).Description, lines[1]);
        Assert.Contains("Files (14 of 15)", lines);
        Assert.Contains("  read_file             off  " + ToolNamed(facts, ReadFileTool.ToolName).Description + " — not offered: switched off in /tools", lines);
        Assert.Contains("Questions (1) (off: Ask user is off)", lines);
        Assert.Equal(8 + 31, lines.Count);
        Assert.Equal(ToolsText.OffLine, ToolsText.OfferedLines(Facts(toolsEnabled: false)).First());
    }
}
