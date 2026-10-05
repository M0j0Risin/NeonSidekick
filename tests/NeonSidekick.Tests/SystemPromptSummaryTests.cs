using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Timers;
using NeonSidekick.UI;
using NeonSidekick.Web;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class SystemPromptSummaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static SystemPromptFacts Facts(
        string? persona = null,
        bool memoryEnabled = true,
        IReadOnlyList<string>? memories = null,
        bool speechOutput = false,
        bool speechReady = false,
        string? operatingRules = null,
        string? voiceDirective = null,
        bool tools = true,
        bool files = true,
        bool skills = true,
        IReadOnlyList<Skill>? catalog = null,
        ProjectNotes? project = null,
        bool markdown = false,
        bool pane = true,
        IReadOnlyList<string>? disabled = null,
        bool projectFile = true,
        int gitTools = 0,
        int shellTools = 0,
        bool shellBridge = false,
        bool shellPolice = true) =>
        new(persona, operatingRules, voiceDirective, memoryEnabled, memories ?? [], speechOutput, speechReady, tools, files, skills, catalog, project, markdown, pane, disabled is null ? null : ToolsText.DisabledSet(disabled), projectFile, GitTools: gitTools, ShellTools: shellTools, ShellBridge: shellBridge, ShellPolice: shellPolice);

    /// <summary>The section heading for a working directory with neither notes file. Pinned.</summary>
    private const string NoNotesHeading = "Project notes — none (NEON.md / AGENTS.md not in the working directory)";

    /// <summary>The section heading with Agent skills on and nothing installed. Pinned.</summary>
    private const string NoSkillsHeading = "Skills — on, none installed";

    private static string[] Headings(SystemPromptFacts facts) => SystemPromptSummary.PromptSections(facts).Select(s => s.Heading).ToArray();

    [Fact]
    public void DefaultProfile_TheFiveSections_InOrder_AndNothingElse()
    {
        var sections = SystemPromptSummary.PromptSections(Facts());

        // The system message's own sections and nothing else (2026-09-26, the user's call): no Reply format, no per-tool-group heading,
        // no opening call, no Request row, and no voice heading on a silent turn.
        Assert.Equal(
            [
                "Persona — default",
                "Operating rules — default",
                NoNotesHeading,
                "Memory — on, directive (the list rides the opening recall_memory call)",
                NoSkillsHeading,
            ],
            sections.Select(s => s.Heading));
        Assert.Equal(Assistant.DefaultPersona, sections[0].Body);
        Assert.Equal(Assistant.OperatingRules, sections[1].Body);
        Assert.Equal("", sections[2].Body);
        Assert.Equal(MemoryPrompt.Directive, sections[3].Body);
        Assert.Equal(SkillsPrompt.DirectiveWithoutSkills, sections[4].Body);
        Assert.Equal([true, true, false, true, true], sections.Select(s => s.InPrompt));

        // A spoken turn with TTS ready adds vocalia.md's directive, last (there is no default one since 2026-10-03); every tool group, on or off, adds nothing.
        var speaking = Headings(Facts(speechOutput: true, speechReady: true, gitTools: 11, shellTools: 1, voiceDirective: "Speak like a pirate.") with { McpTools = 3, ObsidianEnabled = true, ObsidianTools = 8, SqlEnabled = true, SqlTools = 6 });
        Assert.Equal(6, speaking.Length);
        Assert.Equal("Voice directive — vocalia.md (20 chars), included (speech output on, TTS ready), always last", speaking[5]);
        Assert.Equal(5, Headings(Facts(speechOutput: true, speechReady: true, gitTools: 11, shellTools: 1)).Length);
        Assert.Equal(5, Headings(Facts(tools: false) with { GitEnabled = false, ShellEnabled = false, McpEnabled = false }).Length);
    }

    [Fact]
    public void ProjectNotes_AndSkills_HaveTheirOwnSections_AndSayWhyWhenLeftOut()
    {
        var haiku = new Skill("haiku", "Writes haiku. Use when asked for one.", SkillScope.Profile, @"D:\home\profiles\default\skills\haiku");
        var shared = new Skill("deploy", "Deploys the site.", SkillScope.External, @"C:\Users\x\.agents\skills\deploy");
        var notes = new ProjectNotes("NEON.md", "This folder is a .NET solution.");
        var sections = SystemPromptSummary.PromptSections(Facts(catalog: [haiku, shared], project: notes));

        Assert.Equal("Project notes — NEON.md (31 chars)", sections[2].Heading);
        Assert.Equal(Assistant.ProjectNotesSection(notes), sections[2].Body);
        Assert.Equal("Skills — on, 2 skills (1 external)", sections[4].Heading);
        Assert.Equal(SkillsPrompt.Section([haiku, shared]), sections[4].Body);
        Assert.Equal("Skills — on, 1 skill", Headings(Facts(catalog: [haiku]))[4]);
        Assert.Equal(Assistant.SystemPrompt(false, [], project: notes, skills: [haiku, shared]), SystemPromptSummary.SystemPrompt(Facts(catalog: [haiku, shared], project: notes)));

        // Agent skills off: neither goes in, and both headings say so; the tools off keeps the notes (plain context) and drops the skills.
        var off = SystemPromptSummary.PromptSections(Facts(skills: false, catalog: [haiku], project: notes));
        Assert.Equal("Project notes — off (agent skills is off)", off[2].Heading);
        Assert.Equal("", off[2].Body);
        Assert.Equal("Skills — off (agent skills is off)", off[4].Heading);
        Assert.Equal("", off[4].Body);
        Assert.Equal(Assistant.SystemPrompt(false, []), SystemPromptSummary.SystemPrompt(Facts(skills: false, catalog: [haiku], project: notes)));
        // The Project file toggle off (later on 2026-09-19): the row says why, the skills stand.
        var fileOff = SystemPromptSummary.PromptSections(Facts(catalog: [haiku], projectFile: false));
        Assert.Equal("Project notes — off (Project file is off on the Options tab of /skills)", fileOff[2].Heading);
        Assert.Equal("", fileOff[2].Body);
        Assert.Equal("Skills — on, 1 skill", fileOff[4].Heading);
        Assert.Equal("Project notes — off (agent skills is off)", Headings(Facts(skills: false, projectFile: false))[2]);   // the skills switch first
        var noTools = SystemPromptSummary.PromptSections(Facts(tools: false, catalog: [haiku], project: notes));
        Assert.Equal("Project notes — NEON.md (31 chars)", noTools[2].Heading);
        Assert.Equal("Skills — not included (LLM offer tools is off)", noTools[4].Heading);
        Assert.Equal("", noTools[4].Body);
        Assert.Equal(Assistant.SystemPrompt(false, [], tools: false, project: notes), SystemPromptSummary.SystemPrompt(Facts(tools: false, catalog: [haiku], project: notes)));
    }

    [Fact]
    public void Persona_MemoryList_VoiceDirective_AndTheOtherBranches()
    {
        var facts = Facts(persona: "  You are Rex.\n", memories: ["Their name is Chris.", "They like tea."], speechOutput: true, speechReady: true);
        var sections = SystemPromptSummary.PromptSections(facts);

        Assert.Equal("Persona — persona.md (12 chars)", sections[0].Heading);
        Assert.Equal("You are Rex.", sections[0].Body);
        // The list rides the opening memory call (2026-09-17): the prompt section is the directive alone.
        Assert.Equal("Memory — on, directive (the list rides the opening recall_memory call)", sections[3].Heading);
        Assert.Equal(MemoryPrompt.Directive, sections[3].Body);
        Assert.DoesNotContain("Their name is Chris.", sections[3].Body);
        // No vocalia.md, no voice section, spoken or not (2026-10-03: there is no default directive).
        Assert.Equal(5, sections.Count);
        Assert.DoesNotContain(sections, s => s.Heading.StartsWith("Voice directive", StringComparison.Ordinal));

        Assert.Equal("Memory — off, not included", Headings(Facts(memoryEnabled: false))[3]);
        Assert.Equal("", SystemPromptSummary.PromptSections(Facts(memoryEnabled: false))[3].Body);
        // The voice directive only while it is included (2026-09-26): no heading on a silent turn, nor while TTS is not ready.
        Assert.DoesNotContain(Headings(Facts(speechOutput: true, speechReady: false)), h => h.StartsWith("Voice directive", StringComparison.Ordinal));
        Assert.DoesNotContain(Headings(Facts(speechOutput: false, speechReady: true)), h => h.StartsWith("Voice directive", StringComparison.Ordinal));
    }

    [Fact]
    public void VocaliaFile_IsTheVoiceSection_WhenSpeaking_TrimmedAndCounted()
    {
        var speaking = SystemPromptSummary.PromptSections(Facts(speechOutput: true, speechReady: true, voiceDirective: "  Speak like a [pirate].\n"));
        Assert.Equal("Voice directive — vocalia.md (22 chars), included (speech output on, TTS ready), always last", speaking[5].Heading);
        Assert.Equal("Speak like a [pirate].", speaking[5].Body);
        // A blank file is no directive (2026-10-03): no section.
        Assert.Equal(5, Headings(Facts(speechOutput: true, speechReady: true, voiceDirective: " \n")).Length);

        // The file changes what is appended, never whether: a silent turn has no voice section at all (2026-09-26).
        Assert.Equal(5, Headings(Facts(speechOutput: false, voiceDirective: "Speak like a pirate.")).Length);
        Assert.Equal(5, Headings(Facts(speechOutput: true, speechReady: false, voiceDirective: "Speak like a pirate.")).Length);
    }

    [Fact]
    public void OperataFile_IsTheRulesSection_TrimmedAndCounted()
    {
        var sections = SystemPromptSummary.PromptSections(Facts(operatingRules: "  Answer in [haiku].\n"));

        Assert.Equal("Persona — default", sections[0].Heading);
        Assert.Equal("Operating rules — operata.md (18 chars)", sections[1].Heading);
        Assert.Equal("Answer in [haiku].", sections[1].Body);
        Assert.Equal("Operating rules — default", Headings(Facts(operatingRules: "  \n "))[1]);
        Assert.Equal(Assistant.OperatingRules, SystemPromptSummary.PromptSections(Facts(operatingRules: ""))[1].Body);
    }

    /// <summary>The Prompt tab's sections are the system message: joined, they are what <see cref="Assistant.SystemPrompt(bool, IReadOnlyList{string}, string, string)"/> builds.</summary>
    [Theory]
    [InlineData(null, null, true, false)]
    [InlineData(null, null, false, true)]
    [InlineData("You are Rex.", null, true, true)]
    [InlineData("You are Rex.", null, false, false)]
    [InlineData(null, "Answer in haiku.", true, false)]
    [InlineData("You are Rex.", "Answer in haiku.", true, true)]
    [InlineData(null, null, true, true, "Speak like a pirate.")]
    [InlineData("You are Rex.", "Answer in haiku.", false, true, "Speak like a pirate.")]
    [InlineData(null, null, true, false, "Speak like a pirate.")]
    [InlineData(null, null, true, true, null, false)]
    [InlineData(null, null, false, false, null, false)]
    [InlineData("You are Rex.", "Answer in haiku.", true, true, "Speak like a pirate.", false)]
    [InlineData(null, "Answer in haiku.", true, true, null, false)]
    public void TheIncludedSections_AreTheSystemPrompt(string? persona, string? rules, bool memoryEnabled, bool speaking, string? voice = null, bool tools = true)
    {
        var facts = Facts(persona, memoryEnabled, ["Their name is Chris."], speechOutput: speaking, speechReady: speaking, operatingRules: rules, voiceDirective: voice, tools: tools);
        var included = SystemPromptSummary.PromptSections(facts).Where(s => s.InPrompt).Select(s => s.Body).ToList();

        // Every section its own block, the default persona and rules too since 2026-10-03 (one paragraph before).
        string joined = string.Join("\n\n", included);
        string expected = Assistant.SystemPrompt(speaking, memoryEnabled ? facts.Memories : null, persona, rules, voice, tools, skills: []);
        Assert.Equal(expected, joined);
        Assert.Equal(expected, SystemPromptSummary.SystemPrompt(facts));
    }

    [Fact]
    public void FileToolsOff_TheRulesLoseTheFileSentences()
    {
        var sections = SystemPromptSummary.PromptSections(Facts(memories: ["Their name is Chris."], files: false));

        Assert.Equal("Operating rules — default (file tools is off)", sections[1].Heading);
        Assert.Equal(Assistant.OperatingRulesWithoutFiles, sections[1].Body);
        Assert.Equal(Assistant.SystemPrompt(false, ["Their name is Chris."], files: false, skills: []), SystemPromptSummary.SystemPrompt(Facts(memories: ["Their name is Chris."], files: false)));

        // LLM offer tools off says so first, whatever File tools reads.
        var none = SystemPromptSummary.PromptSections(Facts(tools: false, files: false));
        Assert.Equal("Operating rules — default (LLM offer tools is off)", none[1].Heading);
    }

    [Fact]
    public void RecallMemoryOff_PutsTheListInThePrompt()
    {
        // recall_memory switched off on /tools (2026-09-19) while memory is on: nothing carries the list, so the section holds it as under LLM offer tools off.
        var facts = Facts(memories: ["Their name is Chris."], disabled: ["recall_memory"]);
        var sections = SystemPromptSummary.PromptSections(facts);

        Assert.Equal("Memory — on, 1 fact remembered (in the prompt: recall_memory is off in /tools)", sections[3].Heading);
        Assert.Equal(MemoryPrompt.Section(["Their name is Chris."], tools: false), sections[3].Body);
        Assert.Equal(Assistant.SystemPrompt(false, ["Their name is Chris."], skills: [], recall: false), SystemPromptSummary.SystemPrompt(facts));
        Assert.False(facts.Recall);
        Assert.True(Facts(memories: ["x"]).Recall);
        Assert.False(Facts(memoryEnabled: false).Recall);
        Assert.Equal("recall_memory is off in /tools", SystemPromptSummary.ToolOff(RecallMemoryTool.ToolName));
    }

    [Fact]
    public void DeleteOff_TheRulesLoseTheDeleteClause_ThePromptAgrees()
    {
        // delete is off in a fresh profile (2026-09-20): the Prompt tab's rules and the composed prompt both carry FileRuleWithoutDelete, the heading unchanged.
        var sections = SystemPromptSummary.PromptSections(Facts(disabled: ["delete"]));
        Assert.Equal("Operating rules — default", sections[1].Heading);
        Assert.Equal(Assistant.DefaultRules(false, tools: true, delete: false), sections[1].Body);
        Assert.Contains(Assistant.FileRuleWithoutDelete, sections[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("delete only moves", sections[1].Body, StringComparison.Ordinal);
        Assert.Contains(Assistant.FileRuleWithoutDelete, SystemPromptSummary.SystemPrompt(Facts(disabled: ["delete"])), StringComparison.Ordinal);
        Assert.Equal(Assistant.OperatingRules, SystemPromptSummary.PromptSections(Facts())[1].Body);
        Assert.Equal(Assistant.OperatingRulesWithoutFiles, SystemPromptSummary.PromptSections(Facts(files: false, disabled: ["delete"]))[1].Body);
    }

    [Fact]
    public void WebAskAndSessions_RideTheRules_AsATurnComposesThem_ThePromptAgrees()
    {
        // 2026-10-04: /sys had left the web, download, ask_user and session sentences out of its rules.
        var limits = new AskLimits(2, 5);
        var on = Facts() with { WebEnabled = true, WebTools = 4, Download = true, Ask = limits, SessionsEnabled = true, SessionTools = 1 };
        string rules = SystemPromptSummary.PromptSections(on)[1].Body;
        Assert.Equal(Assistant.DefaultRules(false, tools: true, web: true, ask: limits, sessions: true), rules);
        Assert.Contains(Assistant.WebRule, rules, StringComparison.Ordinal);
        Assert.Contains(Assistant.DownloadRule, rules, StringComparison.Ordinal);
        Assert.Contains(Assistant.AskRule(limits), rules, StringComparison.Ordinal);
        Assert.Contains(Assistant.SessionRule, rules, StringComparison.Ordinal);
        Assert.Contains("\n\n" + rules + "\n\n", SystemPromptSummary.SystemPrompt(on), StringComparison.Ordinal);

        // A switch on with nothing offered, download_file off, or tools off: each sentence goes with its tools.
        Assert.Equal(Assistant.OperatingRules, SystemPromptSummary.PromptSections(Facts() with { WebEnabled = true, SessionsEnabled = true })[1].Body);
        Assert.DoesNotContain(Assistant.DownloadRule, SystemPromptSummary.PromptSections(on with { Download = false })[1].Body, StringComparison.Ordinal);
        Assert.Equal(Assistant.PlainTextRule, SystemPromptSummary.PromptSections(on with { ToolsEnabled = false })[1].Body);
    }

    [Fact]
    public void TimersEmptied_TheRulesLoseTheTimerSentence_OneOffKeepsIt()
    {
        // The three timer tools off on /tools (2026-09-20): the Prompt tab's rules and the composed prompt both carry ToolRulesWithoutTimers; one off is the tolerated case.
        string[] three = [StartTimerTool.ToolName, StopTimerTool.ToolName, ListTimersTool.ToolName];
        var sections = SystemPromptSummary.PromptSections(Facts(disabled: three));
        Assert.Equal("Operating rules — default", sections[1].Heading);
        Assert.Equal(Assistant.DefaultRules(false, tools: true, timers: false), sections[1].Body);
        Assert.DoesNotContain(Assistant.TimerRule, sections[1].Body, StringComparison.Ordinal);
        Assert.Contains(Assistant.ToolRulesWithoutTimers + " " + Assistant.FileRule, sections[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Assistant.TimerRule, SystemPromptSummary.SystemPrompt(Facts(disabled: three)), StringComparison.Ordinal);
        Assert.False(Facts(disabled: three).Timers);
        Assert.True(Facts(disabled: [StartTimerTool.ToolName]).Timers);
        Assert.Equal(Assistant.OperatingRules, SystemPromptSummary.PromptSections(Facts(disabled: [StartTimerTool.ToolName]))[1].Body);
        Assert.Equal(Assistant.PlainTextRule, SystemPromptSummary.PromptSections(Facts(tools: false, disabled: three))[1].Body);
    }

    [Fact]
    public void DeleteOn_TheRulesSayDeleteRemovesForGood()
    {
        // delete offered: the Prompt tab and the composed prompt carry FileRule, whose clause says delete removes for good (since 2026-10-01 the
        // only form, File safe edits gone); delete off still wins its own rule.
        var sections = SystemPromptSummary.PromptSections(Facts());
        Assert.Equal("Operating rules — default", sections[1].Heading);
        Assert.Equal(Assistant.DefaultRules(false, tools: true), sections[1].Body);
        Assert.Contains(Assistant.FileRule, sections[1].Body, StringComparison.Ordinal);
        Assert.Contains("delete removes a file or a folder for good", sections[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("trash", sections[1].Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Assistant.FileRule, SystemPromptSummary.SystemPrompt(Facts()), StringComparison.Ordinal);
        Assert.Contains(Assistant.FileRuleWithoutDelete, SystemPromptSummary.PromptSections(Facts(disabled: ["delete"]))[1].Body, StringComparison.Ordinal);
        Assert.Equal(Assistant.OperatingRulesWithoutFiles, SystemPromptSummary.PromptSections(Facts(files: false))[1].Body);
    }

    [Fact]
    public void ToolGroups_WithDisabledTools_CountTheOffered_AndNoteEachRow()
    {
        var (clock, timers, files, memory) = Tools();
        var disabled = ToolsText.DisabledSet(["read_file", "zip", "recall_memory", "shift_date"]);

        var groups = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, disabled: disabled);

        Assert.Equal(["Clock (2 of 3)", "Files (14 of 16)", "Memory (1 of 2)", "Timers (3)"], groups.Select(g => g.Title));   // alphabetical since 2026-10-04
        Assert.All(groups, g => Assert.True(g.Offered));
        Assert.Equal("not offered: switched off in /tools", groups[1].ToolNotes[ReadFileTool.ToolName]);
        Assert.Equal("not offered: switched off in /tools", groups[1].ToolNotes[ZipTool.ToolName]);
        Assert.Equal(2, groups[1].ToolNotes.Count);
        Assert.False(groups[1].Offers(ReadFileTool.ToolName));
        Assert.True(groups[1].Offers(WriteFileTool.ToolName));
        Assert.Equal(SettingsField.FileTools, groups[1].Switch);
        Assert.Equal(SettingsField.Memory, groups[2].Switch);
        Assert.Null(groups[0].Switch);
        Assert.Empty(groups[3].ToolNotes);
        // The plain lines and the tab carry the note after the description.
        var lines = SystemPromptSummary.ToolLines(groups).ToList();
        Assert.Contains("  read_file             " + files.Single(t => t.Name == ReadFileTool.ToolName).Description + " — not offered: switched off in /tools", lines);
        Assert.Contains("  write_file            " + files.Single(t => t.Name == WriteFileTool.ToolName).Description, lines);
        var console = new TestConsole();
        console.Profile.Width = 400;
        console.Write(SystemPromptSummary.ToolsTab(groups));
        Assert.Contains("read_file", console.Output);
        Assert.Contains("  not offered: switched off in /tools", console.Output);   // after the description (which wraps in a narrow console)
        // A group off by its switch keeps its own reason; a disabled tool inside it is still noted.
        var off = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, filesEnabled: false, disabled: disabled);
        Assert.Equal("Files (14 of 16) — not offered: file tools is off", off[1].Title);
        Assert.False(off[1].Offered);
        Assert.False(off[1].Offers(WriteFileTool.ToolName));
        // Nothing disabled: every string as before (the same instances of the note dictionary are not required, the counts are).
        Assert.Equal(["Clock (3)", "Files (16)", "Memory (2)", "Timers (3)"], SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, disabled: ToolsText.DisabledSet([])).Select(g => g.Title));
    }

    [Fact]
    public void OfferedOnly_LeavesOutEveryToolAndGroupTheTurnDoesNotSend()
    {
        // /sys' Tools tab (2026-09-26): only what the next turn sends — a disabled tool, a noted one, a group off by its switch, a group emptied by /tools.
        var (clock, timers, files, memory) = Tools();
        var disabled = ToolsText.DisabledSet(["read_file", "zip", "get_current_time", "shift_date", "days_between"]);

        var groups = SystemPromptSummary.OfferedOnly(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: false, disabled: disabled));

        Assert.Equal(["Files (14)", "Timers (3)"], groups.Select(g => g.Title));   // Clock emptied, Memory off; read_file and zip gone
        Assert.DoesNotContain(groups[0].Tools, t => t.Name is ReadFileTool.ToolName or ZipTool.ToolName);
        Assert.All(groups, g => { Assert.True(g.Offered); Assert.Empty(g.ToolNotes); Assert.Equal("", g.Note); });
        Assert.Equal(SettingsField.FileTools, groups[0].Switch);
        Assert.Equal("Files", groups[0].Label);
        Assert.DoesNotContain(SystemPromptSummary.ToolLines(groups), l => l.Contains("not offered", StringComparison.Ordinal));
        // Nothing offered: the tab and the lines say so, with the reason while LLM offer tools is off.
        var none = SystemPromptSummary.OfferedOnly(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false));
        Assert.Empty(none);
        Assert.Equal(["No tools offered (LLM offer tools is off)"], SystemPromptSummary.ToolLines(none, toolsEnabled: false));
        Assert.Equal(["No tools offered"], SystemPromptSummary.ToolLines(none));
        var console = new TestConsole();
        console.Write(SystemPromptSummary.ToolsTab(none, toolsEnabled: false));
        Assert.Contains("No tools offered (LLM offer tools is off)", console.Output);
    }

    [Fact]
    public void ToolGroups_NoSkillInstalled_NotesLoadSkill_OnlyWhenAsked()
    {
        var (clock, timers, files, memory) = Tools();
        var catalog = new SkillCatalog(() => new SkillRoots(Path.Combine(_dir, "p"), Path.Combine(_dir, "g"), Path.Combine(_dir, "x")));
        var skills = ChatScreen.SkillTools(catalog, () => catalog.Roots, () => false);

        var noted = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, skills: skills, skillInstalled: false);
        var plain = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, skills: skills);

        Assert.Equal("Skills (2)", noted[3].Title);   // the count is the /tools list's alone
        Assert.Equal("not offered: no skill installed", noted[3].ToolNotes[LoadSkillTool.ToolName]);
        Assert.False(noted[3].Offers(LoadSkillTool.ToolName));
        Assert.True(noted[3].Offers(SkillEditorTool.ToolName));
        Assert.Empty(plain[3].ToolNotes);
        // download_file under File tools off, over the whole web list (the /tools list; /sys passes the list already cut).
        var web = ChatScreen.WebTools(new WebAccess(new HttpClient(new StubHttpMessageHandler()), new FakeHeadlessBrowser(), _time), new Files.WorkingDirectory(() => Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "unused"), _time), () => new AppSettingsData());
        var filesOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, web: web, filesEnabled: false);
        Assert.Equal("Web (4)", filesOff[4].Title);
        Assert.Equal("not offered: file tools is off", filesOff[4].ToolNotes[DownloadFileTool.ToolName]);
        Assert.True(filesOff[4].Offers(WebSearchTool.ToolName));
        // The file list carries no note of its own (restore's under File safe edits off went with them, 2026-10-01).
        Assert.Equal("Files (16)", plain[1].Title);
        Assert.Empty(plain[1].ToolNotes);
        Assert.True(plain[1].Offers(DeleteTool.ToolName));
    }

    [Fact]
    public void LlmToolsOff_TheDefaultsAreTheToolFreeOnes()
    {
        var sections = SystemPromptSummary.PromptSections(Facts(memories: ["Their name is Chris."], speechOutput: true, speechReady: true, tools: false));

        Assert.Equal(
            [
                "Persona — default",
                "Operating rules — default (LLM offer tools is off)",
                NoNotesHeading,
                "Memory — on, 1 fact remembered",
                "Skills — not included (LLM offer tools is off)",
            ],
            sections.Select(s => s.Heading));
        Assert.Equal(Assistant.PlainTextRule, sections[1].Body);
        // No pair can carry the list, so the prompt does (the one case it still holds the facts, 2026-09-17).
        Assert.Equal(MemoryPrompt.Section(["Their name is Chris."], tools: false), sections[3].Body);
        Assert.Contains("- Their name is Chris.", sections[3].Body);
        Assert.Equal("", sections[4].Body);

        // A custom file keeps its own heading and text.
        var custom = Headings(Facts(operatingRules: "Answer in haiku.", voiceDirective: "Speak like a pirate.", speechOutput: true, speechReady: true, tools: false));
        Assert.Equal("Operating rules — operata.md (16 chars)", custom[1]);
        Assert.Equal("Voice directive — vocalia.md (20 chars), included (speech output on, TTS ready), always last", custom[5]);
    }

    [Fact]
    public void Markdown_TheRulesFollow_NoHeadingOfItsOwn()
    {
        // On, the pane on, not spoken: the Markdown sentence opens the default rules; the Reply format heading went on 2026-09-26, the rules say it.
        var on = SystemPromptSummary.PromptSections(Facts(markdown: true));
        Assert.Equal(Assistant.DefaultRules(markdown: true, tools: true), on[1].Body);
        Assert.StartsWith(Assistant.MarkdownRule + " ", on[1].Body);
        Assert.DoesNotContain(on, s => s.Heading.StartsWith("Reply format", StringComparison.Ordinal));
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], markdown: true), SystemPromptSummary.SystemPrompt(Facts(markdown: true)));
        Assert.Equal(Assistant.OperatingRules, SystemPromptSummary.PromptSections(Facts(markdown: true, pane: false))[1].Body);

        // A custom operata.md stands whatever the switch says.
        Assert.Equal("Answer in haiku.", SystemPromptSummary.PromptSections(Facts(markdown: true, operatingRules: "Answer in haiku."))[1].Body);

        // Tools off: the Markdown sentence alone, like the plain-text one.
        Assert.Equal(Assistant.MarkdownRule, SystemPromptSummary.PromptSections(Facts(markdown: true, tools: false))[1].Body);
    }

    [Fact]
    public void PromptLines_HeadingsThenIndentedText()
    {
        string[] lines = SystemPromptSummary.PromptLines(Facts(memories: ["Their name is Chris."])).ToArray();

        Assert.Equal(
            [
                "Persona — default",
                .. Assistant.DefaultPersona.Split('\n').Select(line => "  " + line),   // lines since 2026-10-03
                "Operating rules — default",
                "  " + Assistant.OperatingRules,
                NoNotesHeading,
                "Memory — on, directive (the list rides the opening recall_memory call)",
                "  " + MemoryPrompt.Directive,
                NoSkillsHeading,
                "  " + SkillsPrompt.DirectiveWithoutSkills,
            ],
            lines);   // nothing after the skills since 2026-09-26: no tool-group headings, no Also sent part
    }

    [Fact]
    public void PromptTab_RendersTheHeadingsAndTheText()
    {
        using var console = new TestConsole();
        console.Profile.Width = 2000;
        console.Write(SystemPromptSummary.PromptTab(Facts(persona: "You are [Rex].")));

        // Each heading a rule to the edge, its status between two runs of it, the text two cells in (2026-10-03).
        string[] lines = console.Output.Split('\n');
        Assert.Equal(RuleLine("── Persona ── persona.md (14 chars)", 2000), lines[0]);
        Assert.Equal("  You are [Rex].", lines[1]);
        Assert.Equal(" ", lines[2]);
        Assert.Equal(RuleLine("── Operating rules ── default", 2000), lines[3]);
        Assert.Equal("  " + Assistant.OperatingRules, lines[4]);
        Assert.Contains(RuleLine("── Skills ── on, none installed", 2000), lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Request", StringComparison.Ordinal) || l.StartsWith("Opening", StringComparison.Ordinal) || l.StartsWith("Also sent", StringComparison.Ordinal));
    }

    /// <summary>A heading rule as a console <paramref name="width"/> cells wide prints it (2026-10-03): <paramref name="text"/>, a space, the rule to the edge.</summary>
    private static string RuleLine(string text, int width) => text + " " + new string(ScreenPane.RuleGlyph, width - TextCells.Width(text) - 1);

    private (IReadOnlyList<AIFunction> Clock, IReadOnlyList<AIFunction> Timers, IReadOnlyList<AIFunction> Files, IReadOnlyList<AIFunction> Memory) Tools()
    {
        Directory.CreateDirectory(_dir);
        var files = new NeonSidekick.Files.WorkingDirectory(() => _dir, _time);
        return (
            ChatScreen.ClockTools(_time),
            ChatScreen.TimerTools(new TimerBoard(_time, () => { })),
            ChatScreen.FileTools(files, () => true, _ => { }, () => new AppSettingsData()),
            ChatScreen.MemoryTools(new MemoryStore(_dir, _time)));
    }

    /// <summary>A group by its bare label: the groups are alphabetical since 2026-10-04, so a test names the one it means.</summary>
    private static ToolGroup G(IReadOnlyList<ToolGroup> groups, string label) => groups.Single(g => g.Label == label);

    /// <summary>2026-10-04 (the user's ask): the groups come out alphabetical by their bare label, any case, whatever the turn's order.</summary>
    [Fact]
    public void ToolGroups_AreAlphabetical_ByTheirLabel()
    {
        var (clock, timers, files, memory) = Tools();
        var web = ChatScreen.WebTools(new WebAccess(new HttpClient(new StubHttpMessageHandler()), new FakeHeadlessBrowser(), _time), new Files.WorkingDirectory(() => Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "unused"), _time), () => new AppSettingsData());
        var questions = ChatScreen.AskTools((_, _) => Task.FromResult<IReadOnlyList<AskAnswer>?>(null), () => new AppSettingsData());
        var catalog = new SkillCatalog(() => new SkillRoots(Path.Combine(_dir, "p"), Path.Combine(_dir, "g"), Path.Combine(_dir, "x")));
        var skills = ChatScreen.SkillTools(catalog, () => catalog.Roots, () => false);

        var groups = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, web: web, questions: questions, skills: skills, help: ChatScreen.HelpTools());

        Assert.Equal(["Clock", "Files", NeonSidekick.Help.HelpText.GroupTitle, "Memory", "Questions", "Skills", "Timers", "Web"], groups.Select(g => g.Label));
        Assert.Equal(groups.Select(g => g.Label).Order(StringComparer.OrdinalIgnoreCase), groups.Select(g => g.Label));
    }

    [Fact]
    public void ToolGroups_AreTheTurnsTools_InOrder_MemoryMarkedWhenOff()
    {
        var (clock, timers, files, memory) = Tools();

        var on = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true);
        Assert.Equal(["Clock (3)", "Files (16)", "Memory (2)", "Timers (3)"], on.Select(g => g.Title));   // alphabetical since 2026-10-04 (the user's ask)
        Assert.All(on, g => Assert.True(g.Offered));
        // An offered group's title is its name alone: no note for the pane's description column.
        Assert.Equal(on.Select(g => g.Name), on.Select(g => g.Title));
        Assert.All(on, g => Assert.Equal("", g.Note));
        Assert.Equal(["get_current_time", "shift_date", "days_between"], on[0].Tools.Select(t => t.Name));
        Assert.Equal(["start_timer", "stop_timer", "list_timers"], on[3].Tools.Select(t => t.Name));
        Assert.Equal(FileToolNames.WithoutPdf, on[1].Tools.Select(t => t.Name));
        Assert.Equal([SaveMemoryTool.ToolName, RecallMemoryTool.ToolName], on[2].Tools.Select(t => t.Name));

        var off = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: false);
        Assert.Equal("Memory (2) — not offered: memory is off", off[2].Title);
        // The title is the name and the note joined: the pane draws the two apart (2026-09-16).
        Assert.Equal("Memory (2)", off[2].Name);
        Assert.Equal(SystemPromptSummary.NotOffered("memory is off"), off[2].Note);
        Assert.False(off[2].Offered);
        Assert.Equal(on.Where(g => g.Label != "Memory").Select(g => g.Title), off.Where(g => g.Label != "Memory").Select(g => g.Title));

        // LLM offer tools off: every group not offered; memory's own reason first when it is off too.
        var none = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false);
        Assert.Equal(
            ["Clock (3) — not offered: LLM offer tools is off", "Files (16) — not offered: LLM offer tools is off", "Memory (2) — not offered: LLM offer tools is off", "Timers (3) — not offered: LLM offer tools is off"],
            none.Select(g => g.Title));
        Assert.All(none, g => Assert.False(g.Offered));
        Assert.Equal(on.Select(g => g.Tools), none.Select(g => g.Tools));
        var neither = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: false, toolsEnabled: false);
        Assert.Equal("Memory (2) — not offered: memory is off", neither[2].Title);
        Assert.Equal(none.Where(g => g.Label != "Memory").Select(g => g.Title), neither.Where(g => g.Label != "Memory").Select(g => g.Title));

        // The web group (2026-09-15): after the files, before memory, marked when the setting Web tools is off; nothing when no list is given.
        var web = ChatScreen.WebTools(new WebAccess(new HttpClient(new StubHttpMessageHandler()), new FakeHeadlessBrowser(), _time), new Files.WorkingDirectory(() => Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "unused"), _time), () => new AppSettingsData());
        var withWeb = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true);
        Assert.Equal(["Clock (3)", "Files (16)", "Memory (2)", "Timers (3)", "Web (4)"], withWeb.Select(g => g.Title));
        Assert.Equal(["web_search", "web_fetch", "open_url", "download_file"], withWeb[4].Tools.Select(t => t.Name));
        Assert.All(withWeb, g => Assert.True(g.Offered));
        var webOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: false);
        Assert.Equal("Web (4) — not offered: web is off", G(webOff, "Web").Title);
        Assert.False(G(webOff, "Web").Offered);
        Assert.True(G(webOff, "Memory").Offered);
        var webNone = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: false);
        Assert.Equal("Web (4) — not offered: web is off", G(webNone, "Web").Title);
        Assert.Equal("Web (4) — not offered: LLM offer tools is off", G(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true), "Web").Title);

        // The files group (2026-09-15): marked when the setting File tools is off, its own reason first like memory's and the web's.
        var filesOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: false);
        Assert.Equal(["Clock (3)", "Files (16) — not offered: file tools is off", "Memory (2)", "Timers (3)", "Web (4)"], filesOff.Select(g => g.Title));
        Assert.False(G(filesOff, "Files").Offered);
        Assert.True(G(filesOff, "Clock").Offered && G(filesOff, "Web").Offered && G(filesOff, "Memory").Offered);
        Assert.Equal(FileToolNames.WithoutPdf, G(filesOff, "Files").Tools.Select(t => t.Name));
        Assert.Equal("Files (16) — not offered: file tools is off", G(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: false), "Files").Title);

        // The questions group (2026-09-15): last, after memory, marked when the setting Ask user is off, else when the bottom pane is (its own reasons first); nothing when no list is given.
        var questions = ChatScreen.AskTools((_, _) => Task.FromResult<IReadOnlyList<AskAnswer>?>(null), () => new AppSettingsData());
        var withQuestions = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true);
        Assert.Equal(["Clock (3)", "Files (16)", "Memory (2)", "Questions (1)", "Timers (3)", "Web (4)"], withQuestions.Select(g => g.Title));
        Assert.Equal([AskUserTool.ToolName], G(withQuestions, "Questions").Tools.Select(t => t.Name));
        Assert.All(withQuestions, g => Assert.True(g.Offered));
        var noPane = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: false);
        Assert.Equal("Questions (1) — not offered: no pane", G(noPane, "Questions").Title);
        Assert.False(G(noPane, "Questions").Offered);
        Assert.True(G(noPane, "Memory").Offered);
        var askOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: false, paneOn: true);
        Assert.Equal("Questions (1) — not offered: ask user is off", G(askOff, "Questions").Title);
        Assert.False(G(askOff, "Questions").Offered);
        Assert.True(G(askOff, "Memory").Offered);
        // The setting's reason first, then the pane's, then LLM offer tools.
        Assert.Equal("Questions (1) — not offered: ask user is off", G(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: false, paneOn: false), "Questions").Title);
        Assert.Equal("Questions (1) — not offered: no pane", G(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: false), "Questions").Title);
        Assert.Equal("Questions (1) — not offered: LLM offer tools is off", G(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true), "Questions").Title);
        Assert.Equal(5, SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true).Count);

        // The skills group (2026-09-16): after memory, before the questions, marked when the setting Agent skills is off (its own reason first); nothing when no list is given.
        var catalog = new SkillCatalog(() => new SkillRoots(Path.Combine(_dir, "p"), Path.Combine(_dir, "g"), Path.Combine(_dir, "x")));
        var skills = ChatScreen.SkillTools(catalog, () => catalog.Roots, () => false);
        var withSkills = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true);
        Assert.Equal(["Clock (3)", "Files (16)", "Memory (2)", "Questions (1)", "Skills (2)", "Timers (3)", "Web (4)"], withSkills.Select(g => g.Title));
        Assert.Equal([LoadSkillTool.ToolName, SkillEditorTool.ToolName], G(withSkills, "Skills").Tools.Select(t => t.Name));
        Assert.All(withSkills, g => Assert.True(g.Offered));
        var skillsOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: false);
        Assert.Equal("Skills (2) — not offered: agent skills is off", G(skillsOff, "Skills").Title);
        Assert.False(G(skillsOff, "Skills").Offered);
        Assert.True(G(skillsOff, "Memory").Offered && G(skillsOff, "Questions").Offered);
        Assert.Equal("Skills (2) — not offered: agent skills is off", G(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: false), "Skills").Title);
        Assert.Equal("Skills (2) — not offered: LLM offer tools is off", G(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true), "Skills").Title);

        // The sessions group (2026-09-18): after the skills, before the questions, marked when the setting Session tool is off (its own reason first); nothing when no list is given.
        using var store = new NeonSidekick.Sessions.SessionStore(Path.Combine(_dir, "sessions"));
        var sessions = ChatScreen.SessionTools(store, () => new AppSettingsData(), () => null, TimeProvider.System);
        var withSessions = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: true);
        Assert.Equal(["Clock (3)", "Files (16)", "Memory (2)", "Questions (1)", "Sessions (1)", "Skills (2)", "Timers (3)", "Web (4)"], withSessions.Select(g => g.Title));
        Assert.Equal([SessionManagerTool.ToolName], G(withSessions, "Sessions").Tools.Select(t => t.Name));
        Assert.All(withSessions, g => Assert.True(g.Offered));
        var sessionsOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: false);
        Assert.Equal("Sessions (1) — not offered: session tool is off", G(sessionsOff, "Sessions").Title);
        Assert.False(G(sessionsOff, "Sessions").Offered);
        Assert.True(G(sessionsOff, "Skills").Offered && G(sessionsOff, "Questions").Offered);
        Assert.Equal("Sessions (1) — not offered: LLM offer tools is off", G(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: true), "Sessions").Title);

        // The MCP groups (2026-09-20): one per connected server after the sessions and before the questions, the setting MCP servers their switch, a disabled row naming /mcp.
        var echo = new EchoTool();
        var mcp = new List<McpServerTools> { new("docker", [new McpEchoStandIn("docker__echo"), new McpEchoStandIn("docker__fail")]), new("chrome", [new McpEchoStandIn("chrome__navigate")]) };
        var withMcp = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: true, disabled: ToolsText.DisabledSet(["docker__fail"]), mcp: mcp, mcpEnabled: true);
        Assert.Equal(["Clock (3)", "Files (16)", "MCP chrome (1)", "MCP docker (1 of 2)", "Memory (2)", "Questions (1)", "Sessions (1)", "Skills (2)", "Timers (3)", "Web (4)"], withMcp.Select(g => g.Title));
        Assert.Equal(SettingsField.McpServers, G(withMcp, "MCP docker").Switch);
        Assert.Equal("not offered: switched off in /mcp", G(withMcp, "MCP docker").ToolNotes["docker__fail"]);
        Assert.False(G(withMcp, "MCP docker").Offers("docker__fail"));
        Assert.True(G(withMcp, "MCP docker").Offers("docker__echo"));
        Assert.True(G(withMcp, "MCP docker").Offered && G(withMcp, "MCP chrome").Offered);
        var mcpOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: true, mcp: mcp, mcpEnabled: false);
        Assert.Equal("MCP docker (2) — not offered: MCP servers is off", G(mcpOff, "MCP docker").Title);
        Assert.False(G(mcpOff, "MCP docker").Offered && G(mcpOff, "MCP chrome").Offered);
        Assert.True(G(mcpOff, "Sessions").Offered && G(mcpOff, "Questions").Offered);
        Assert.Equal("MCP chrome (1) — not offered: LLM offer tools is off", G(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: true, mcp: mcp, mcpEnabled: true), "MCP chrome").Title);
        Assert.Equal("MCP ", SystemPromptSummary.McpGroupPrefix);
        Assert.Equal(8, SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: true, mcp: [], mcpEnabled: true).Count);   // no server connected: no group
        _ = echo;
    }

    /// <summary>A stand-in for an MCP tool with a name the groups read (the real adapter needs a connected client).</summary>
    private sealed class McpEchoStandIn(string name) : AIFunction
    {
        public override string Name => name;
        public override string Description => "A stand-in.";
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new("ok");
    }

    /// <summary>The vault rule (2026-09-22) rides while any vault tool is offered; no heading of its own since 2026-09-26 — the rules say it.</summary>
    [Fact]
    public void PromptSections_TheObsidianRule_RidesWhileAnyToolIsOffered_NoHeading()
    {
        var on = Facts() with { ObsidianEnabled = true, ObsidianTools = 8 };
        Assert.DoesNotContain(Headings(on), h => h.StartsWith("Obsidian", StringComparison.Ordinal));
        Assert.Equal(Headings(Facts()).Length, Headings(on).Length);
        Assert.Contains(Assistant.ObsidianRule, SystemPromptSummary.PromptSections(on)[1].Body);
        Assert.DoesNotContain(Assistant.ObsidianRule, SystemPromptSummary.PromptSections(on with { ObsidianTools = 0 })[1].Body);
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], obsidian: true), SystemPromptSummary.SystemPrompt(on));
    }

    /// <summary>The SQL rule (2026-09-23), the Obsidian shape: it rides while any SQL tool is offered; no heading of its own since 2026-09-26.</summary>
    [Fact]
    public void PromptSections_TheSqlRule_RidesWhileAnyToolIsOffered_NoHeading()
    {
        var on = Facts() with { SqlEnabled = true, SqlTools = 6 };
        Assert.DoesNotContain(Headings(on), h => h.StartsWith("SQL", StringComparison.Ordinal));
        Assert.Equal(Headings(Facts()).Length, Headings(on).Length);
        Assert.Contains(Assistant.SqlRule, SystemPromptSummary.PromptSections(on)[1].Body);
        Assert.DoesNotContain(Assistant.SqlRule, SystemPromptSummary.PromptSections(on with { SqlTools = 0 })[1].Body);
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], sql: true), SystemPromptSummary.SystemPrompt(on));
    }

    [Fact]
    public void PromptSections_TheGitRule_RidesWhileAnyToolIsOffered_NoHeading()
    {
        // No GitLib tools heading since 2026-09-26, on or off: the rule in the rules is what the model reads.
        Assert.DoesNotContain(Headings(Facts(gitTools: 11)), h => h.StartsWith("Git", StringComparison.Ordinal));
        Assert.DoesNotContain(Headings(Facts() with { GitEnabled = false }), h => h.StartsWith("Git", StringComparison.Ordinal));
        // The rule rides the defaults only while a git tool is offered; it never names the two opt-in tools, so no variant.
        Assert.Contains(Assistant.GitRule, SystemPromptSummary.PromptSections(Facts(gitTools: 11))[1].Body);
        Assert.DoesNotContain(Assistant.GitRule, SystemPromptSummary.PromptSections(Facts())[1].Body);
        Assert.DoesNotContain(Assistant.GitRule, SystemPromptSummary.PromptSections(Facts(gitTools: 11) with { GitEnabled = false })[1].Body);
        Assert.DoesNotContain(GitDiscardTool.ToolName, Assistant.GitRule);
        Assert.DoesNotContain(GitDeleteTool.ToolName, Assistant.GitRule);
        Assert.Equal(Assistant.DefaultRules(false, true, git: true), SystemPromptSummary.PromptSections(Facts(gitTools: 11))[1].Body);
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], git: true), SystemPromptSummary.SystemPrompt(Facts(gitTools: 11)));
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: []), SystemPromptSummary.SystemPrompt(Facts()));
    }

    [Fact]
    public void PromptSections_TheShellRule_RidesWhileTheToolIsOffered_NoHeading()
    {
        // No Shell tools heading since 2026-09-26, on or off.
        Assert.DoesNotContain(Headings(Facts(shellTools: 1)), h => h.StartsWith("Shell", StringComparison.Ordinal));
        Assert.DoesNotContain(Headings(Facts() with { ShellEnabled = false }), h => h.StartsWith("Shell", StringComparison.Ordinal));
        // The rule rides the defaults only while the tool is offered, after the git sentence; which rule follows the setting Shell tool bridge (later on 2026-09-21).
        Assert.Contains(Assistant.ShellRule, SystemPromptSummary.PromptSections(Facts(shellTools: 1, shellBridge: true))[1].Body);
        Assert.Contains(Assistant.ShellRuleWithoutBridge, SystemPromptSummary.PromptSections(Facts(shellTools: 1))[1].Body);
        Assert.DoesNotContain("neon_tools", SystemPromptSummary.PromptSections(Facts(shellTools: 1))[1].Body);
        Assert.DoesNotContain(Assistant.ShellRuleWithoutBridge, SystemPromptSummary.PromptSections(Facts())[1].Body);
        Assert.DoesNotContain(Assistant.ShellRuleWithoutBridge, SystemPromptSummary.PromptSections(Facts(shellTools: 1) with { ShellEnabled = false })[1].Body);
        Assert.DoesNotContain("neon_tools", SystemPromptSummary.PromptSections(Facts(shellBridge: true))[1].Body);   // the bridge alone, no shell tool: nothing
        Assert.Equal(Assistant.DefaultRules(false, true, git: true, shell: true), SystemPromptSummary.PromptSections(Facts(gitTools: 11, shellTools: 1))[1].Body);
        Assert.Equal(Assistant.DefaultRules(false, true, git: true, shell: true, bridge: true), SystemPromptSummary.PromptSections(Facts(gitTools: 11, shellTools: 1, shellBridge: true))[1].Body);
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], shell: true), SystemPromptSummary.SystemPrompt(Facts(shellTools: 1)));
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], shell: true, bridge: true), SystemPromptSummary.SystemPrompt(Facts(shellTools: 1, shellBridge: true)));
        // The head follows the setting Shell police outside paths (2026-09-22): off, the …Unpoliced variant, which says nothing about where a command may reach.
        Assert.Contains(Assistant.ShellRuleWithoutBridgeUnpoliced, SystemPromptSummary.PromptSections(Facts(shellTools: 1, shellPolice: false))[1].Body);
        Assert.Contains(Assistant.ShellRuleUnpoliced, SystemPromptSummary.PromptSections(Facts(shellTools: 1, shellBridge: true, shellPolice: false))[1].Body);
        Assert.DoesNotContain("under it", SystemPromptSummary.PromptSections(Facts(shellTools: 1, shellPolice: false))[1].Body);
        Assert.Equal(Assistant.DefaultRules(false, true, git: true, shell: true, police: false), SystemPromptSummary.PromptSections(Facts(gitTools: 11, shellTools: 1, shellPolice: false))[1].Body);
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], shell: true, police: false), SystemPromptSummary.SystemPrompt(Facts(shellTools: 1, shellPolice: false)));
        Assert.True(Facts(shellPolice: false).Police);   // the police rides the shell rule alone: no shell tool, the default holds
        Assert.False(Facts(shellTools: 1, shellPolice: false).Police);
        // Shell prefer native tools (2026-09-26): its sentence after the shell rule, as the turn sends it; nothing without a shell tool.
        Assert.Equal(Assistant.DefaultRules(false, true, git: true, shell: true, native: true), SystemPromptSummary.PromptSections(Facts(gitTools: 11, shellTools: 1) with { ShellNative = true })[1].Body);
        Assert.Contains(Assistant.ShellNativeRule(files: true, git: true, web: false, sql: false), SystemPromptSummary.PromptSections(Facts(gitTools: 11, shellTools: 1) with { ShellNative = true })[1].Body);
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], shell: true, native: true), SystemPromptSummary.SystemPrompt(Facts(shellTools: 1) with { ShellNative = true }));
        Assert.False((Facts() with { ShellNative = true }).Native);
    }

    [Fact]
    public void PromptSections_TheMcpRule_RidesWhileAnyToolIsOffered_NoHeading()
    {
        // No MCP servers heading since 2026-09-26, on or off.
        Assert.DoesNotContain(Headings(Facts() with { McpTools = 14 }), h => h.StartsWith("MCP", StringComparison.Ordinal));
        Assert.DoesNotContain(Headings(Facts() with { McpEnabled = false }), h => h.StartsWith("MCP", StringComparison.Ordinal));
        // The rule rides the defaults only while something is offered.
        Assert.Contains(Assistant.McpRule, SystemPromptSummary.PromptSections(Facts() with { McpTools = 1 })[1].Body);
        Assert.DoesNotContain(Assistant.McpRule, SystemPromptSummary.PromptSections(Facts() with { McpTools = 0 })[1].Body);
        Assert.DoesNotContain(Assistant.McpRule, SystemPromptSummary.PromptSections(Facts() with { McpEnabled = false, McpTools = 1 })[1].Body);
        Assert.Equal(SystemPromptSummary.PromptSections(Facts() with { McpTools = 1 })[1].Body, Assistant.DefaultRules(false, true, mcp: true));
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], mcp: true), SystemPromptSummary.SystemPrompt(Facts() with { McpTools = 1 }));
    }

    [Fact]
    public void ToolLines_AndToolsTab_NameEveryTool_WithItsDescription()
    {
        var (clock, timers, files, memory) = Tools();
        var groups = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true);

        string[] lines = SystemPromptSummary.ToolLines(groups).ToArray();
        Assert.Equal(4 + 24, lines.Length);   // 23 tools until restore went, 2026-10-01; two more with the image tools, 2026-10-04
        Assert.Equal("Clock (3)", lines[0]);
        Assert.Equal("  " + "get_current_time".PadRight(22) + clock[0].Description, lines[1]);
        Assert.Equal("Memory (2)", lines[^7]);   // Timers last since the groups went alphabetical (2026-10-04)
        Assert.Equal("  " + "save_memory".PadRight(22) + memory[0].Description, lines[^6]);
        Assert.Equal("  " + "recall_memory".PadRight(22) + memory[1].Description, lines[^5]);
        Assert.Equal("Timers (3)", lines[^4]);

        using var console = new TestConsole();
        console.Profile.Width = 900;   // wide enough that no description wraps (patch_file and search_files are the longest, 2026-09-19)
        console.Write(SystemPromptSummary.ToolsTab(groups));
        string output = console.Output;
        foreach (var tool in clock.Concat(timers).Concat(files).Concat(memory))
        {
            Assert.Contains("\n  " + tool.Name + "  ", "\n" + output);
            Assert.Contains(tool.Description, output);
        }

        // A grid per group, every name column as wide as the longest tool name of all (2026-10-03; one grid for every
        // group since 2026-09-16): the description column starts at the same place under every heading — measured,
        // never a literal width — two cells in under the rule.
        int column = SystemPromptSummary.BodyIndent + groups.SelectMany(g => g.Tools).Max(t => t.Name.Length) + SlashCommands.HelpColumnGap;
        string[] rendered = output.TrimEnd('\n').Split('\n').Select(l => l.TrimEnd()).ToArray();
        Assert.Equal(RuleLine("── Clock · 3", 900), rendered[0]);
        Assert.Equal(("  get_current_time").PadRight(column) + clock[0].Description, rendered[1]);
        Assert.Equal("", rendered[4]);   // a one-space row parts the groups
        Assert.Equal(RuleLine("── Files · 16", 900), rendered[5]);
        Assert.Equal("  get_working_directory".PadRight(column) + files[0].Description, rendered[6]);
        Assert.Equal(RuleLine("── Memory · 2", 900), rendered[23]);   // two rows later with the image tools, 2026-10-04
        Assert.Equal("  save_memory".PadRight(column) + memory[0].Description, rendered[24]);
        Assert.Equal("  recall_memory".PadRight(column) + memory[1].Description, rendered[25]);
        Assert.Equal(RuleLine("── Timers · 3", 900), rendered[^4]);
        foreach (var line in rendered.Where(l => l.Length > column && !l.StartsWith(SectionRule.Lead, StringComparison.Ordinal)))
        {
            Assert.Equal(' ', line[column - 1]);
            Assert.NotEqual(' ', line[column]);
        }

        // A group that is not offered: its reason dim on the rule after the count, never widening the name column.
        using var off = new TestConsole();
        off.Profile.Width = 400;
        off.Write(SystemPromptSummary.ToolsTab(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: false)));
        Assert.Contains(RuleLine("── Memory · 2 ── " + SystemPromptSummary.NotOffered("memory is off"), 400), off.Output.TrimEnd('\n').Split('\n').Select(l => l.TrimEnd()));

        // The heading keeps the section colour whether the group is offered or not (later on 2026-09-20,
        // the user's call, the /tools rule): the same escape sequence leads "Memory" on and off.
        using var ansi = new TestConsole();
        ansi.Profile.Width = 400;
        ansi.EmitAnsiSequences();
        ansi.Write(SystemPromptSummary.ToolsTab(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: false)));
        string dimmed = ansi.Output;
        using var ansiOn = new TestConsole();
        ansiOn.Profile.Width = 400;
        ansiOn.EmitAnsiSequences();
        ansiOn.Write(SystemPromptSummary.ToolsTab(groups));
        static string Lead(string output, string heading) => output[(output.LastIndexOf('\n', output.IndexOf(heading, StringComparison.Ordinal)) + 1)..output.IndexOf(heading, StringComparison.Ordinal)];
        Assert.Equal(Lead(ansiOn.Output, "Memory"), Lead(dimmed, "Memory"));
        Assert.Equal(Lead(ansiOn.Output, "Clock"), Lead(dimmed, "Memory"));
        Assert.NotEqual(Lead(ansiOn.Output, "save_memory"), Lead(dimmed, "save_memory"));   // the rows under it still dim
    }
}
