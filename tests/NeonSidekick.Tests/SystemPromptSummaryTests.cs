using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Timers;
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
        int turnCount = 0,
        ReasoningEffort reasoning = ReasoningEffort.None,
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
        bool safeEdits = true,
        int gitTools = 0,
        int shellTools = 0,
        bool shellBridge = false,
        bool shellPolice = true) =>
        new(persona, operatingRules, voiceDirective, memoryEnabled, memories ?? [], speechOutput, speechReady, turnCount, "Friday 11 September 2026, 14:05 (Pacific Daylight Time, UTC-07:00)", reasoning, @"The working directory is 'D:\files' (the profile's default folder); every path you pass to a file tool is relative to it.", tools, files, skills, catalog, project, markdown, pane, disabled is null ? null : ToolsText.DisabledSet(disabled), projectFile, FileSafeEdits: safeEdits, GitTools: gitTools, ShellTools: shellTools, ShellBridge: shellBridge, ShellPolice: shellPolice);

    /// <summary>The section heading for a working directory with neither notes file. Pinned.</summary>
    private const string NoNotesHeading = "Project notes — none (NEON.md / AGENTS.md not in the working directory)";

    /// <summary>The section heading with Agent skills on and nothing installed. Pinned.</summary>
    private const string NoSkillsHeading = "Skills — on, none installed";

    /// <summary>The section heading with MCP servers on and none connected (2026-09-20). Pinned.</summary>
    private const string NoMcpHeading = "MCP servers — none connected";

    /// <summary>The section heading with Git native tools on and none offered — the fixture passes no git tool, as it passes no web tool (2026-09-20; the setting's new name since 2026-09-21). Pinned.</summary>
    private const string GitHeading = "Git native tools — on, none offered (every git tool is switched off in /tools)";

    /// <summary>The section heading with the Shell command policy not off and no shell tool offered — the fixture passes none, as with git (2026-09-21). Pinned.</summary>
    private const string ShellHeading = "Shell tools — on, none offered (every shell tool is switched off in /tools)";

    private static string[] Headings(SystemPromptFacts facts) => SystemPromptSummary.PromptSections(facts).Select(s => s.Heading).ToArray();

    [Fact]
    public void DefaultProfile_TheFourteenSections_InOrder()
    {
        var sections = SystemPromptSummary.PromptSections(Facts());

        // The project notes after the rules and the skills after the memory since 2026-09-16 (seven sections before); the Reply format row under the rules the same day; the opening memory call since 2026-09-17.
        Assert.Equal(
            [
                "Persona — default",
                "Operating rules — default",
                "Reply format — plain text: transcript markdown is off",
                NoNotesHeading,
                "Memory — on, directive (the list rides the opening recall_memory call)",
                NoSkillsHeading,
                GitHeading,
                ShellHeading,
                NoMcpHeading,
                "Voice directive — not included: speech output is off",
                "Opening clock call — seeded with the first message",
                "Opening working-directory call — seeded with the first message",
                "Opening memory call — seeded with the first message, 0 facts remembered",
                "Request — reasoning_effort none · chat_template_kwargs.enable_thinking=false",
            ],
            sections.Select(s => s.Heading));
        Assert.Equal(Assistant.DefaultPersona, sections[0].Body);
        Assert.Equal(Assistant.OperatingRules, sections[1].Body);
        Assert.Equal("", sections[3].Body);
        Assert.Equal(MemoryPrompt.Directive, sections[4].Body);
        Assert.Equal(SkillsPrompt.DirectiveWithoutSkills, sections[5].Body);
        Assert.Equal("", sections[9].Body);
        Assert.Equal("get_current_time → Friday 11 September 2026, 14:05 (Pacific Daylight Time, UTC-07:00)\n" + SystemPromptSummary.OpeningNote, sections[10].Body);
        Assert.Equal(@"get_working_directory → The working directory is 'D:\files' (the profile's default folder); every path you pass to a file tool is relative to it." + "\n" + SystemPromptSummary.OpeningCwdNote, sections[11].Body);
        Assert.Equal("recall_memory → " + MemoryPrompt.NothingRemembered + "\n" + SystemPromptSummary.OpeningMemoryNote, sections[12].Body);
        Assert.Equal("", sections[13].Body);
        Assert.Equal([true, true, false, false, true, true, false, false, false, false, false, false, false, false], sections.Select(s => s.InPrompt));
        Assert.Equal([SystemPromptPart.Prompt, SystemPromptPart.Prompt, SystemPromptPart.Prompt, SystemPromptPart.Prompt, SystemPromptPart.Prompt, SystemPromptPart.Prompt, SystemPromptPart.Prompt, SystemPromptPart.Prompt, SystemPromptPart.Prompt, SystemPromptPart.Prompt, SystemPromptPart.Request, SystemPromptPart.Request, SystemPromptPart.Request, SystemPromptPart.Request], sections.Select(s => s.Part));
    }

    [Fact]
    public void ProjectNotes_AndSkills_HaveTheirOwnSections_AndSayWhyWhenLeftOut()
    {
        var haiku = new Skill("haiku", "Writes haiku. Use when asked for one.", SkillScope.Profile, @"D:\home\profiles\default\skills\haiku");
        var shared = new Skill("deploy", "Deploys the site.", SkillScope.External, @"C:\Users\x\.agents\skills\deploy");
        var notes = new ProjectNotes("NEON.md", "This folder is a .NET solution.");
        var sections = SystemPromptSummary.PromptSections(Facts(catalog: [haiku, shared], project: notes));

        Assert.Equal("Project notes — NEON.md (31 chars)", sections[3].Heading);
        Assert.Equal(Assistant.ProjectNotesSection(notes), sections[3].Body);
        Assert.Equal("Skills — on, 2 skills (1 external)", sections[5].Heading);
        Assert.Equal(SkillsPrompt.Section([haiku, shared]), sections[5].Body);
        Assert.Equal("Skills — on, 1 skill", Headings(Facts(catalog: [haiku]))[5]);
        Assert.Equal(Assistant.SystemPrompt(false, [], project: notes, skills: [haiku, shared]), SystemPromptSummary.SystemPrompt(Facts(catalog: [haiku, shared], project: notes)));

        // Agent skills off: neither goes in, and both headings say so; the tools off keeps the notes (plain context) and drops the skills.
        var off = SystemPromptSummary.PromptSections(Facts(skills: false, catalog: [haiku], project: notes));
        Assert.Equal("Project notes — off (agent skills is off)", off[3].Heading);
        Assert.Equal("", off[3].Body);
        Assert.Equal("Skills — off (agent skills is off)", off[5].Heading);
        Assert.Equal("", off[5].Body);
        Assert.Equal(Assistant.SystemPrompt(false, []), SystemPromptSummary.SystemPrompt(Facts(skills: false, catalog: [haiku], project: notes)));
        // The Project file toggle off (later on 2026-09-19): the row says why, the skills stand.
        var fileOff = SystemPromptSummary.PromptSections(Facts(catalog: [haiku], projectFile: false));
        Assert.Equal("Project notes — off (Project file is off on the Project tab of /skills)", fileOff[3].Heading);
        Assert.Equal("", fileOff[3].Body);
        Assert.Equal("Skills — on, 1 skill", fileOff[5].Heading);
        Assert.Equal("Project notes — off (agent skills is off)", Headings(Facts(skills: false, projectFile: false))[3]);   // the skills switch first
        var noTools = SystemPromptSummary.PromptSections(Facts(tools: false, catalog: [haiku], project: notes));
        Assert.Equal("Project notes — NEON.md (31 chars)", noTools[3].Heading);
        Assert.Equal("Skills — not included (LLM offer tools is off)", noTools[5].Heading);
        Assert.Equal("", noTools[5].Body);
        Assert.Equal(Assistant.SystemPrompt(false, [], tools: false, project: notes), SystemPromptSummary.SystemPrompt(Facts(tools: false, catalog: [haiku], project: notes)));
    }

    [Fact]
    public void Persona_MemoryList_VoiceDirective_AndTheOtherBranches()
    {
        var facts = Facts(persona: "  You are Rex.\n", memories: ["Their name is Chris.", "They like tea."], speechOutput: true, speechReady: true, turnCount: 3, reasoning: ReasoningEffort.High);
        var sections = SystemPromptSummary.PromptSections(facts);

        Assert.Equal("Persona — persona.md (12 chars)", sections[0].Heading);
        Assert.Equal("You are Rex.", sections[0].Body);
        // The list rides the opening memory call (2026-09-17): the prompt section is the directive, the facts are under Also sent.
        Assert.Equal("Memory — on, directive (the list rides the opening recall_memory call)", sections[4].Heading);
        Assert.Equal(MemoryPrompt.Directive, sections[4].Body);
        Assert.DoesNotContain("Their name is Chris.", sections[4].Body);
        Assert.Equal("Voice directive — default, included (speech output on, TTS ready), always last", sections[9].Heading);
        Assert.Equal(Assistant.VoiceDirective, sections[9].Body);
        Assert.Equal("Opening clock call — already sent with the first message", sections[10].Heading);
        Assert.Equal("Opening working-directory call — already sent with the first message, kept current", sections[11].Heading);
        Assert.Equal("Opening memory call — already sent with the first message, kept current, 2 facts remembered", sections[12].Heading);
        Assert.Equal("recall_memory → " + MemoryPrompt.Heading + "\n- Their name is Chris.\n- They like tea.\n" + SystemPromptSummary.OpeningMemoryNote, sections[12].Body);
        Assert.Equal(SystemPromptPart.Request, sections[12].Part);
        Assert.Equal("Request — reasoning_effort high", sections[13].Heading);

        Assert.Equal("Opening memory call — seeded with the first message, 1 fact remembered", Headings(Facts(memories: ["one"]))[12]);
        Assert.Equal("Memory — off, not included", Headings(Facts(memoryEnabled: false))[4]);
        Assert.Equal("", SystemPromptSummary.PromptSections(Facts(memoryEnabled: false))[4].Body);
        Assert.Equal("Opening memory call — not sent: memory is off", Headings(Facts(memoryEnabled: false))[12]);
        Assert.Equal("", SystemPromptSummary.PromptSections(Facts(memoryEnabled: false))[12].Body);
        Assert.Equal("Voice directive — not included: TTS is not ready", Headings(Facts(speechOutput: true, speechReady: false))[9]);
        Assert.Equal("Voice directive — not included: speech output is off", Headings(Facts(speechOutput: false, speechReady: true))[9]);
        Assert.Equal("Request — reasoning_effort xhigh", Headings(Facts(reasoning: ReasoningEffort.ExtraHigh))[13]);
    }

    [Fact]
    public void VocaliaFile_IsTheVoiceSection_WhenSpeaking_TrimmedAndCounted()
    {
        var speaking = SystemPromptSummary.PromptSections(Facts(speechOutput: true, speechReady: true, voiceDirective: "  Speak like a [pirate].\n"));
        Assert.Equal("Voice directive — vocalia.md (22 chars), included (speech output on, TTS ready), always last", speaking[9].Heading);
        Assert.Equal("Speak like a [pirate].", speaking[9].Body);
        Assert.Equal("Voice directive — default, included (speech output on, TTS ready), always last", Headings(Facts(speechOutput: true, speechReady: true, voiceDirective: " \n"))[9]);

        // The file changes what is appended, never whether: a silent turn shows the same not-included heading and nothing under it.
        var silent = SystemPromptSummary.PromptSections(Facts(speechOutput: false, voiceDirective: "Speak like a pirate."));
        Assert.Equal("Voice directive — not included: speech output is off", silent[9].Heading);
        Assert.Equal("", silent[9].Body);
        Assert.Equal("Voice directive — not included: TTS is not ready", Headings(Facts(speechOutput: true, speechReady: false, voiceDirective: "Speak like a pirate."))[9]);
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

        // The default persona and the default rules are one paragraph in the prompt; a custom one is its own block.
        string joined = persona is null && rules is null
            ? included[0] + " " + string.Join("\n\n", included.Skip(1))
            : string.Join("\n\n", included);
        string expected = Assistant.SystemPrompt(speaking, memoryEnabled ? facts.Memories : null, persona, rules, voice, tools, skills: []);
        Assert.Equal(expected, joined);
        Assert.Equal(expected, SystemPromptSummary.SystemPrompt(facts));
    }

    [Fact]
    public void FileToolsOff_TheRulesLoseTheFileSentences_AndTheCwdCallIsNotSent_TheClocksStill()
    {
        var sections = SystemPromptSummary.PromptSections(Facts(memories: ["Their name is Chris."], files: false));

        Assert.Equal(
            [
                "Persona — default",
                "Operating rules — default (file tools is off)",
                "Reply format — plain text: transcript markdown is off",
                NoNotesHeading,
                "Memory — on, directive (the list rides the opening recall_memory call)",
                NoSkillsHeading,
                GitHeading,
                ShellHeading,
                NoMcpHeading,
                "Voice directive — not included: speech output is off",
                "Opening clock call — seeded with the first message",
                "Opening working-directory call — not sent: file tools is off",
                "Opening memory call — seeded with the first message, 1 fact remembered",
                "Request — reasoning_effort none · chat_template_kwargs.enable_thinking=false",
            ],
            sections.Select(s => s.Heading));
        Assert.Equal(Assistant.OperatingRulesWithoutFiles, sections[1].Body);
        Assert.Equal("", sections[11].Body);
        Assert.Equal(Assistant.SystemPrompt(false, ["Their name is Chris."], files: false, skills: []), SystemPromptSummary.SystemPrompt(Facts(memories: ["Their name is Chris."], files: false)));

        // LLM offer tools off says so first, whatever File tools reads.
        var none = SystemPromptSummary.PromptSections(Facts(tools: false, files: false));
        Assert.Equal("Operating rules — default (LLM offer tools is off)", none[1].Heading);
        Assert.Equal("Opening working-directory call — not sent: LLM offer tools is off", none[11].Heading);
    }

    [Fact]
    public void RecallMemoryOff_PutsTheListInThePrompt_AndSkipsTheMemoryCall()
    {
        // recall_memory switched off on /tools (2026-09-19) while memory is on: nothing carries the list, so the section holds it as under LLM offer tools off.
        var facts = Facts(memories: ["Their name is Chris."], disabled: ["recall_memory"]);
        var sections = SystemPromptSummary.PromptSections(facts);

        Assert.Equal("Memory — on, 1 fact remembered (in the prompt: recall_memory is off in /tools)", sections[4].Heading);
        Assert.Equal(MemoryPrompt.Section(["Their name is Chris."], tools: false), sections[4].Body);
        Assert.Equal("Opening memory call — not sent: recall_memory is off in /tools", sections[12].Heading);
        Assert.Equal("Opening clock call — seeded with the first message", sections[10].Heading);
        Assert.Equal(Assistant.SystemPrompt(false, ["Their name is Chris."], skills: [], recall: false), SystemPromptSummary.SystemPrompt(facts));
        Assert.False(facts.Recall);
        Assert.True(Facts(memories: ["x"]).Recall);
        Assert.False(Facts(memoryEnabled: false).Recall);
        Assert.Equal("recall_memory is off in /tools", SystemPromptSummary.ToolOff(RecallMemoryTool.ToolName));
    }

    [Fact]
    public void ClockOrCwdOff_SaysNotSent_TheRulesUntouched()
    {
        var sections = SystemPromptSummary.PromptSections(Facts(disabled: ["get_current_time", "get_working_directory"]));

        Assert.Equal("Operating rules — default", sections[1].Heading);   // the file group stands: the screen passes FilesEnabled false only when every file tool is off
        Assert.Equal("Opening clock call — not sent: get_current_time is off in /tools", sections[10].Heading);
        Assert.Equal("", sections[10].Body);
        Assert.Equal("Opening working-directory call — not sent: get_working_directory is off in /tools", sections[11].Heading);
        Assert.Equal("Opening memory call — seeded with the first message, 0 facts remembered", sections[12].Heading);
        // LLM offer tools off and File tools off say so first, whatever the list holds.
        Assert.Equal("Opening clock call — not sent: LLM offer tools is off", SystemPromptSummary.PromptSections(Facts(tools: false, disabled: ["get_current_time"]))[10].Heading);
        Assert.Equal("Opening working-directory call — not sent: file tools is off", SystemPromptSummary.PromptSections(Facts(files: false, disabled: ["get_working_directory"]))[11].Heading);
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
    public void SafeEditsOff_DeleteOn_TheRulesSayDeleteRemovesForGood()
    {
        // File safe edits off with delete offered (2026-09-20): the Prompt tab and the composed prompt carry FileRuleDeleteInPlace; delete off still wins its own rule.
        var sections = SystemPromptSummary.PromptSections(Facts(safeEdits: false));
        Assert.Equal("Operating rules — default", sections[1].Heading);
        Assert.Equal(Assistant.DefaultRules(false, tools: true, safeEdits: false), sections[1].Body);
        Assert.Contains(Assistant.FileRuleDeleteInPlace, sections[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("delete only moves", sections[1].Body, StringComparison.Ordinal);
        Assert.Contains(Assistant.FileRuleDeleteInPlace, SystemPromptSummary.SystemPrompt(Facts(safeEdits: false)), StringComparison.Ordinal);
        Assert.Contains(Assistant.FileRuleWithoutDelete, SystemPromptSummary.PromptSections(Facts(safeEdits: false, disabled: ["delete"]))[1].Body, StringComparison.Ordinal);
        Assert.Equal(Assistant.OperatingRulesWithoutFiles, SystemPromptSummary.PromptSections(Facts(files: false, safeEdits: false))[1].Body);
    }

    [Fact]
    public void ToolGroups_WithDisabledTools_CountTheOffered_AndNoteEachRow()
    {
        var (clock, timers, files, memory) = Tools();
        var disabled = ToolsText.DisabledSet(["read_file", "zip", "recall_memory", "shift_date"]);

        var groups = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, disabled: disabled);

        Assert.Equal(["Clock (2 of 3)", "Timers (3)", "Files (13 of 15)", "Memory (1 of 2)"], groups.Select(g => g.Title));
        Assert.All(groups, g => Assert.True(g.Offered));
        Assert.Equal("not offered: switched off in /tools", groups[2].ToolNotes[ReadFileTool.ToolName]);
        Assert.Equal("not offered: switched off in /tools", groups[2].ToolNotes[ZipTool.ToolName]);
        Assert.Equal(2, groups[2].ToolNotes.Count);
        Assert.False(groups[2].Offers(ReadFileTool.ToolName));
        Assert.True(groups[2].Offers(WriteFileTool.ToolName));
        Assert.Equal(SettingsField.FileTools, groups[2].Switch);
        Assert.Equal(SettingsField.Memory, groups[3].Switch);
        Assert.Null(groups[0].Switch);
        Assert.Empty(groups[1].ToolNotes);
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
        Assert.Equal("Files (13 of 15) — not offered: file tools is off", off[2].Title);
        Assert.False(off[2].Offered);
        Assert.False(off[2].Offers(WriteFileTool.ToolName));
        // Nothing disabled: every string as before (the same instances of the note dictionary are not required, the counts are).
        Assert.Equal(["Clock (3)", "Timers (3)", "Files (15)", "Memory (2)"], SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, disabled: ToolsText.DisabledSet([])).Select(g => g.Title));
    }

    [Fact]
    public void ToolGroups_NoSkillInstalled_NotesLoadSkill_OnlyWhenAsked()
    {
        var (clock, timers, files, memory) = Tools();
        var catalog = new SkillCatalog(() => new SkillRoots(Path.Combine(_dir, "p"), Path.Combine(_dir, "g"), Path.Combine(_dir, "x")));
        var skills = ChatScreen.SkillTools(catalog, () => catalog.Roots, () => false);

        var noted = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, skills: skills, skillInstalled: false);
        var plain = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, skills: skills);

        Assert.Equal("Skills (2)", noted[4].Title);   // the count is the /tools list's alone
        Assert.Equal("not offered: no skill installed", noted[4].ToolNotes[LoadSkillTool.ToolName]);
        Assert.False(noted[4].Offers(LoadSkillTool.ToolName));
        Assert.True(noted[4].Offers(SkillEditorTool.ToolName));
        Assert.Empty(plain[4].ToolNotes);
        // download_file under File tools off, over the whole web list (the /tools list; /sys passes the list already cut).
        var web = ChatScreen.WebTools(new WebAccess(new HttpClient(new StubHttpMessageHandler()), new FakeHeadlessBrowser(), _time), new Files.WorkingDirectory(() => Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "unused"), _time), () => new AppSettingsData());
        var filesOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, web: web, filesEnabled: false);
        Assert.Equal("Web (4)", filesOff[3].Title);
        Assert.Equal("not offered: file tools is off", filesOff[3].ToolNotes[DownloadFileTool.ToolName]);
        Assert.True(filesOff[3].Offers(WebSearchTool.ToolName));
        // restore under File safe edits off (later still on 2026-09-20), over the whole file list the same way: the row noted, the count the /tools list's alone.
        var safeOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, safeEdits: false);
        Assert.Equal("Files (15)", safeOff[2].Title);
        Assert.Equal("not offered: File safe edits is off", safeOff[2].ToolNotes[RestoreTool.ToolName]);
        Assert.False(safeOff[2].Offers(RestoreTool.ToolName));
        Assert.True(safeOff[2].Offers(DeleteTool.ToolName));
        Assert.Empty(plain[2].ToolNotes);
        // The safe-edits reason wins over the /tools one on the same row, as download_file's does.
        Assert.Equal("not offered: File safe edits is off", SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, disabled: new HashSet<string>(StringComparer.Ordinal) { RestoreTool.ToolName }, safeEdits: false)[2].ToolNotes[RestoreTool.ToolName]);
        // The list already cut (what /sys and the $ list pass): no note, Files (14).
        var cut = SystemPromptSummary.ToolGroups(clock, timers, ChatScreen.FileToolsFor(files, safeEdits: false), memory, memoryEnabled: true, safeEdits: false);
        Assert.Equal("Files (14)", cut[2].Title);
        Assert.Empty(cut[2].ToolNotes);
        Assert.DoesNotContain(cut[2].Tools, t => t is RestoreTool);
    }

    [Fact]
    public void LlmToolsOff_TheDefaultsAreTheToolFreeOnes_AndTheOpeningCallsAreNotSent()
    {
        var sections = SystemPromptSummary.PromptSections(Facts(memories: ["Their name is Chris."], speechOutput: true, speechReady: true, tools: false));

        Assert.Equal(
            [
                "Persona — default",
                "Operating rules — default (LLM offer tools is off)",
                "Reply format — plain text: transcript markdown is off",
                NoNotesHeading,
                "Memory — on, 1 fact remembered",
                "Skills — not included (LLM offer tools is off)",
                "Git native tools — not offered (LLM offer tools is off)",
                "Shell tools — not offered (LLM offer tools is off)",
                "MCP servers — not offered (LLM offer tools is off)",
                "Voice directive — default (LLM offer tools is off), included (speech output on, TTS ready), always last",
                "Opening clock call — not sent: LLM offer tools is off",
                "Opening working-directory call — not sent: LLM offer tools is off",
                "Opening memory call — not sent: LLM offer tools is off",
                "Request — reasoning_effort none · chat_template_kwargs.enable_thinking=false",
            ],
            sections.Select(s => s.Heading));
        Assert.Equal(Assistant.PlainTextRule, sections[1].Body);
        // No pair can carry the list, so the prompt does (the one case it still holds the facts, 2026-09-17).
        Assert.Equal(MemoryPrompt.Section(["Their name is Chris."], tools: false), sections[4].Body);
        Assert.Contains("- Their name is Chris.", sections[4].Body);
        Assert.Equal("", sections[5].Body);
        Assert.Equal(Assistant.VoiceDirectiveWithoutTools, sections[9].Body);
        Assert.Equal("", sections[10].Body);
        Assert.Equal("", sections[11].Body);
        Assert.Equal("", sections[12].Body);
        Assert.Equal([SystemPromptPart.Request, SystemPromptPart.Request, SystemPromptPart.Request], sections.Skip(10).Take(3).Select(s => s.Part));

        // A custom file keeps its own heading and text; the turn count changes nothing (nothing was seeded).
        var custom = Headings(Facts(operatingRules: "Answer in haiku.", voiceDirective: "Speak like a pirate.", speechOutput: true, speechReady: true, turnCount: 3, tools: false));
        Assert.Equal("Operating rules — operata.md (16 chars)", custom[1]);
        Assert.Equal("Voice directive — vocalia.md (20 chars), included (speech output on, TTS ready), always last", custom[9]);
        Assert.Equal("Opening clock call — not sent: LLM offer tools is off", custom[10]);
    }

    [Fact]
    public void ReplyFormat_SaysMarkdownOrWhyNot_AndTheRulesFollow()
    {
        // On, the pane on, not spoken: the Markdown sentence opens the default rules.
        var on = SystemPromptSummary.PromptSections(Facts(markdown: true));
        Assert.Equal("Reply format — markdown (transcript markdown on, the pane on, the turn not spoken)", on[2].Heading);
        Assert.Equal("", on[2].Body);
        Assert.Equal(Assistant.DefaultRules(markdown: true, tools: true), on[1].Body);
        Assert.StartsWith(Assistant.MarkdownRule + " ", on[1].Body);
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], markdown: true), SystemPromptSummary.SystemPrompt(Facts(markdown: true)));

        // The three reasons for plain text, in the order they are checked.
        Assert.Equal("Reply format — plain text: transcript markdown is off", Headings(Facts(markdown: false))[2]);
        Assert.Equal("Reply format — plain text: no pane", Headings(Facts(markdown: true, pane: false))[2]);
        Assert.Equal("Reply format — plain text: the turn speaks", Headings(Facts(markdown: true, speechOutput: true, speechReady: true))[2]);
        Assert.Equal("Reply format — markdown (transcript markdown on, the pane on, the turn not spoken)", Headings(Facts(markdown: true, speechOutput: true, speechReady: false))[2]);
        Assert.Equal(Assistant.OperatingRules, SystemPromptSummary.PromptSections(Facts(markdown: true, pane: false))[1].Body);

        // A custom operata.md stands whatever the switch says.
        var custom = SystemPromptSummary.PromptSections(Facts(markdown: true, operatingRules: "Answer in haiku."));
        Assert.Equal("Reply format — operata.md stands", custom[2].Heading);
        Assert.Equal("Answer in haiku.", custom[1].Body);

        // Tools off: the Markdown sentence alone, like the plain-text one.
        Assert.Equal(Assistant.MarkdownRule, SystemPromptSummary.PromptSections(Facts(markdown: true, tools: false))[1].Body);

        Assert.Equal("operata.md stands", SystemPromptSummary.OperataStandsSuffix);
    }

    [Fact]
    public void PromptLines_HeadingsThenIndentedText_WithTheDividerOnce()
    {
        string[] lines = SystemPromptSummary.PromptLines(Facts(memories: ["Their name is Chris."])).ToArray();

        Assert.Equal("Persona — default", lines[0]);
        Assert.Equal("  " + Assistant.DefaultPersona, lines[1]);
        Assert.Equal("Operating rules — default", lines[2]);
        Assert.Equal("  " + Assistant.OperatingRules, lines[3]);
        Assert.Equal("Reply format — plain text: transcript markdown is off", lines[4]);
        Assert.Equal(NoNotesHeading, lines[5]);
        Assert.Equal("Memory — on, directive (the list rides the opening recall_memory call)", lines[6]);
        Assert.Equal("  " + MemoryPrompt.Directive, lines[7]);
        Assert.Equal(NoSkillsHeading, lines[8]);
        Assert.Equal("  " + SkillsPrompt.DirectiveWithoutSkills, lines[9]);
        Assert.Equal(ShellHeading, lines[11]);
        Assert.Equal(NoMcpHeading, lines[12]);
        Assert.Equal("Voice directive — not included: speech output is off", lines[13]);
        Assert.Equal(SystemPromptSummary.AlsoSentHeading, lines[14]);
        Assert.Equal("Opening clock call — seeded with the first message", lines[15]);
        Assert.StartsWith("  get_current_time → Friday", lines[16]);
        Assert.Equal("  " + SystemPromptSummary.OpeningNote, lines[17]);
        Assert.Equal("Opening working-directory call — seeded with the first message", lines[18]);
        Assert.Equal(@"  get_working_directory → The working directory is 'D:\files' (the profile's default folder); every path you pass to a file tool is relative to it.", lines[19]);
        Assert.Equal("  " + SystemPromptSummary.OpeningCwdNote, lines[20]);
        // The list under the memory call (2026-09-17), one bullet a line, the note last.
        Assert.Equal("Opening memory call — seeded with the first message, 1 fact remembered", lines[21]);
        Assert.Equal("  recall_memory → " + MemoryPrompt.Heading, lines[22]);
        Assert.Equal("  - Their name is Chris.", lines[23]);
        Assert.Equal("  " + SystemPromptSummary.OpeningMemoryNote, lines[24]);
        Assert.Equal("Request — reasoning_effort none · chat_template_kwargs.enable_thinking=false", lines[25]);
        Assert.Equal(26, lines.Length);   // the Git tools heading since 2026-09-20, the Shell tools heading since 2026-09-21
        Assert.Single(lines, SystemPromptSummary.AlsoSentHeading);
    }

    [Fact]
    public void PromptTab_RendersTheHeadingsAndTheText_WithTheDivider()
    {
        using var console = new TestConsole();
        console.Profile.Width = 2000;
        console.Write(SystemPromptSummary.PromptTab(Facts(persona: "You are [Rex].")));

        string[] lines = console.Output.Split('\n');
        Assert.Equal("Persona — persona.md (14 chars)", lines[0]);
        Assert.Equal("You are [Rex].", lines[1]);
        Assert.Equal(" ", lines[2]);
        Assert.Equal("Operating rules — default", lines[3]);
        Assert.Equal(Assistant.OperatingRules, lines[4]);
        Assert.Contains(SystemPromptSummary.AlsoSentHeading, lines);
        Assert.Contains("Request — reasoning_effort none · chat_template_kwargs.enable_thinking=false", lines);
    }

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

    [Fact]
    public void ToolGroups_AreTheTurnsTools_InOrder_MemoryMarkedWhenOff()
    {
        var (clock, timers, files, memory) = Tools();

        var on = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true);
        Assert.Equal(["Clock (3)", "Timers (3)", "Files (15)", "Memory (2)"], on.Select(g => g.Title));
        Assert.All(on, g => Assert.True(g.Offered));
        // An offered group's title is its name alone: no note for the pane's description column.
        Assert.Equal(on.Select(g => g.Name), on.Select(g => g.Title));
        Assert.All(on, g => Assert.Equal("", g.Note));
        Assert.Equal(["get_current_time", "shift_date", "days_between"], on[0].Tools.Select(t => t.Name));
        Assert.Equal(["start_timer", "stop_timer", "list_timers"], on[1].Tools.Select(t => t.Name));
        Assert.Equal(FileToolNames.All, on[2].Tools.Select(t => t.Name));
        Assert.Equal([SaveMemoryTool.ToolName, RecallMemoryTool.ToolName], on[3].Tools.Select(t => t.Name));

        var off = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: false);
        Assert.Equal("Memory (2) — not offered: memory is off", off[3].Title);
        // The title is the name and the note joined: the pane draws the two apart (2026-09-16).
        Assert.Equal("Memory (2)", off[3].Name);
        Assert.Equal(SystemPromptSummary.NotOffered("memory is off"), off[3].Note);
        Assert.False(off[3].Offered);
        Assert.Equal(on.Take(3).Select(g => g.Title), off.Take(3).Select(g => g.Title));

        // LLM offer tools off: every group not offered; memory's own reason first when it is off too.
        var none = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false);
        Assert.Equal(
            ["Clock (3) — not offered: LLM offer tools is off", "Timers (3) — not offered: LLM offer tools is off", "Files (15) — not offered: LLM offer tools is off", "Memory (2) — not offered: LLM offer tools is off"],
            none.Select(g => g.Title));
        Assert.All(none, g => Assert.False(g.Offered));
        Assert.Equal(on.Select(g => g.Tools), none.Select(g => g.Tools));
        var neither = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: false, toolsEnabled: false);
        Assert.Equal("Memory (2) — not offered: memory is off", neither[3].Title);
        Assert.Equal(none.Take(3).Select(g => g.Title), neither.Take(3).Select(g => g.Title));

        // The web group (2026-09-15): after the files, before memory, marked when the setting Web tools is off; nothing when no list is given.
        var web = ChatScreen.WebTools(new WebAccess(new HttpClient(new StubHttpMessageHandler()), new FakeHeadlessBrowser(), _time), new Files.WorkingDirectory(() => Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "unused"), _time), () => new AppSettingsData());
        var withWeb = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true);
        Assert.Equal(["Clock (3)", "Timers (3)", "Files (15)", "Web (4)", "Memory (2)"], withWeb.Select(g => g.Title));
        Assert.Equal(["web_search", "web_fetch", "open_url", "download_file"], withWeb[3].Tools.Select(t => t.Name));
        Assert.All(withWeb, g => Assert.True(g.Offered));
        var webOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: false);
        Assert.Equal("Web (4) — not offered: web is off", webOff[3].Title);
        Assert.False(webOff[3].Offered);
        Assert.True(webOff[4].Offered);
        var webNone = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: false);
        Assert.Equal("Web (4) — not offered: web is off", webNone[3].Title);
        Assert.Equal("Web (4) — not offered: LLM offer tools is off", SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true)[3].Title);

        // The files group (2026-09-15): marked when the setting File tools is off, its own reason first like memory's and the web's.
        var filesOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: false);
        Assert.Equal(["Clock (3)", "Timers (3)", "Files (15) — not offered: file tools is off", "Web (4)", "Memory (2)"], filesOff.Select(g => g.Title));
        Assert.False(filesOff[2].Offered);
        Assert.True(filesOff[0].Offered && filesOff[3].Offered && filesOff[4].Offered);
        Assert.Equal(FileToolNames.All, filesOff[2].Tools.Select(t => t.Name));
        Assert.Equal("Files (15) — not offered: file tools is off", SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: false)[2].Title);

        // The questions group (2026-09-15): last, after memory, marked when the setting Ask user is off, else when the bottom pane is (its own reasons first); nothing when no list is given.
        var questions = ChatScreen.AskTools((_, _) => Task.FromResult<IReadOnlyList<AskAnswer>?>(null), () => new AppSettingsData());
        var withQuestions = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true);
        Assert.Equal(["Clock (3)", "Timers (3)", "Files (15)", "Web (4)", "Memory (2)", "Questions (1)"], withQuestions.Select(g => g.Title));
        Assert.Equal([AskUserTool.ToolName], withQuestions[5].Tools.Select(t => t.Name));
        Assert.All(withQuestions, g => Assert.True(g.Offered));
        var noPane = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: false);
        Assert.Equal("Questions (1) — not offered: no pane", noPane[5].Title);
        Assert.False(noPane[5].Offered);
        Assert.True(noPane[4].Offered);
        var askOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: false, paneOn: true);
        Assert.Equal("Questions (1) — not offered: ask user is off", askOff[5].Title);
        Assert.False(askOff[5].Offered);
        Assert.True(askOff[4].Offered);
        // The setting's reason first, then the pane's, then LLM offer tools.
        Assert.Equal("Questions (1) — not offered: ask user is off", SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: false, paneOn: false)[5].Title);
        Assert.Equal("Questions (1) — not offered: no pane", SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: false)[5].Title);
        Assert.Equal("Questions (1) — not offered: LLM offer tools is off", SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true)[5].Title);
        Assert.Equal(5, SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true).Count);

        // The skills group (2026-09-16): after memory, before the questions, marked when the setting Agent skills is off (its own reason first); nothing when no list is given.
        var catalog = new SkillCatalog(() => new SkillRoots(Path.Combine(_dir, "p"), Path.Combine(_dir, "g"), Path.Combine(_dir, "x")));
        var skills = ChatScreen.SkillTools(catalog, () => catalog.Roots, () => false);
        var withSkills = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true);
        Assert.Equal(["Clock (3)", "Timers (3)", "Files (15)", "Web (4)", "Memory (2)", "Skills (2)", "Questions (1)"], withSkills.Select(g => g.Title));
        Assert.Equal([LoadSkillTool.ToolName, SkillEditorTool.ToolName], withSkills[5].Tools.Select(t => t.Name));
        Assert.All(withSkills, g => Assert.True(g.Offered));
        var skillsOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: false);
        Assert.Equal("Skills (2) — not offered: agent skills is off", skillsOff[5].Title);
        Assert.False(skillsOff[5].Offered);
        Assert.True(skillsOff[4].Offered && skillsOff[6].Offered);
        Assert.Equal("Skills (2) — not offered: agent skills is off", SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: false)[5].Title);
        Assert.Equal("Skills (2) — not offered: LLM offer tools is off", SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true)[5].Title);

        // The sessions group (2026-09-18): after the skills, before the questions, marked when the setting Session tool is off (its own reason first); nothing when no list is given.
        using var store = new NeonSidekick.Sessions.SessionStore(Path.Combine(_dir, "sessions"));
        var sessions = ChatScreen.SessionTools(store, () => new AppSettingsData(), () => null, TimeProvider.System);
        var withSessions = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: true);
        Assert.Equal(["Clock (3)", "Timers (3)", "Files (15)", "Web (4)", "Memory (2)", "Skills (2)", "Sessions (1)", "Questions (1)"], withSessions.Select(g => g.Title));
        Assert.Equal([SessionManagerTool.ToolName], withSessions[6].Tools.Select(t => t.Name));
        Assert.All(withSessions, g => Assert.True(g.Offered));
        var sessionsOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: false);
        Assert.Equal("Sessions (1) — not offered: session tool is off", sessionsOff[6].Title);
        Assert.False(sessionsOff[6].Offered);
        Assert.True(sessionsOff[5].Offered && sessionsOff[7].Offered);
        Assert.Equal("Sessions (1) — not offered: LLM offer tools is off", SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: true)[6].Title);

        // The MCP groups (2026-09-20): one per connected server after the sessions and before the questions, the setting MCP servers their switch, a disabled row naming /mcp.
        var echo = new EchoTool();
        var mcp = new List<McpServerTools> { new("docker", [new McpEchoStandIn("docker__echo"), new McpEchoStandIn("docker__fail")]), new("chrome", [new McpEchoStandIn("chrome__navigate")]) };
        var withMcp = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: true, disabled: ToolsText.DisabledSet(["docker__fail"]), mcp: mcp, mcpEnabled: true);
        Assert.Equal(["Clock (3)", "Timers (3)", "Files (15)", "Web (4)", "Memory (2)", "Skills (2)", "Sessions (1)", "MCP docker (1 of 2)", "MCP chrome (1)", "Questions (1)"], withMcp.Select(g => g.Title));
        Assert.Equal(SettingsField.McpServers, withMcp[7].Switch);
        Assert.Equal("not offered: switched off in /mcp", withMcp[7].ToolNotes["docker__fail"]);
        Assert.False(withMcp[7].Offers("docker__fail"));
        Assert.True(withMcp[7].Offers("docker__echo"));
        Assert.True(withMcp[7].Offered && withMcp[8].Offered);
        var mcpOff = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: true, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: true, mcp: mcp, mcpEnabled: false);
        Assert.Equal("MCP docker (2) — not offered: MCP servers is off", mcpOff[7].Title);
        Assert.False(mcpOff[7].Offered && mcpOff[8].Offered);
        Assert.True(mcpOff[6].Offered && mcpOff[9].Offered);
        Assert.Equal("MCP chrome (1) — not offered: LLM offer tools is off", SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true, toolsEnabled: false, web, webEnabled: true, filesEnabled: true, questions, askEnabled: true, paneOn: true, skills, skillsEnabled: true, sessions, sessionsEnabled: true, mcp: mcp, mcpEnabled: true)[8].Title);
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

    /// <summary>The Obsidian row (2026-09-22): absent while no vault is offered — /sys as it was for every profile that never names one — else on / none / not offered, the rule riding while any vault tool is.</summary>
    [Fact]
    public void PromptSections_TheObsidianRow_OnlyWithAVault_AndTheRuleRidesWhileAnyToolIsOffered()
    {
        Assert.DoesNotContain(Headings(Facts()), h => h.StartsWith("Obsidian", StringComparison.Ordinal));
        var on = Facts() with { ObsidianEnabled = true, ObsidianTools = 8 };
        Assert.Contains("Obsidian tools — on, 8 tools offered", Headings(on));
        Assert.Contains("Obsidian tools — on, none offered (every vault tool is switched off in /tools)", Headings(on with { ObsidianTools = 0 }));
        Assert.Contains("Obsidian tools — not offered (LLM offer tools is off)", Headings(on with { ToolsEnabled = false }));
        Assert.Equal(Headings(Facts()).Length + 1, Headings(on).Length);
        Assert.Contains(Assistant.ObsidianRule, SystemPromptSummary.PromptSections(on)[1].Body);
        Assert.DoesNotContain(Assistant.ObsidianRule, SystemPromptSummary.PromptSections(on with { ObsidianTools = 0 })[1].Body);
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], obsidian: true), SystemPromptSummary.SystemPrompt(on));
    }

    /// <summary>The SQL row (2026-09-23), the Obsidian shape: absent while no connection is offered, else on / none / not offered, the rule riding while any SQL tool is.</summary>
    [Fact]
    public void PromptSections_TheSqlRow_OnlyWithAConnection_AndTheRuleRidesWhileAnyToolIsOffered()
    {
        Assert.DoesNotContain(Headings(Facts()), h => h.StartsWith("SQL", StringComparison.Ordinal));
        var on = Facts() with { SqlEnabled = true, SqlTools = 6 };
        Assert.Contains("SQL tools — on, 6 tools offered", Headings(on));
        Assert.Contains("SQL tools — on, none offered (every SQL tool is switched off in /tools)", Headings(on with { SqlTools = 0 }));
        Assert.Contains("SQL tools — not offered (LLM offer tools is off)", Headings(on with { ToolsEnabled = false }));
        Assert.Equal(Headings(Facts()).Length + 1, Headings(on).Length);
        Assert.Contains(Assistant.SqlRule, SystemPromptSummary.PromptSections(on)[1].Body);
        Assert.DoesNotContain(Assistant.SqlRule, SystemPromptSummary.PromptSections(on with { SqlTools = 0 })[1].Body);
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], sql: true), SystemPromptSummary.SystemPrompt(on));
    }

    [Fact]
    public void PromptSections_TheGitRow_SaysOnOffOrNone_AndTheRuleRidesWhileAnyToolIsOffered()
    {
        Assert.Equal("Git native tools — on, 11 tools offered", Headings(Facts(gitTools: 11))[6]);
        Assert.Equal("Git native tools — on, 9 tools offered", Headings(Facts(gitTools: 9))[6]);
        Assert.Equal("Git native tools — on, 1 tool offered", Headings(Facts(gitTools: 1))[6]);
        Assert.Equal(GitHeading, Headings(Facts())[6]);
        Assert.Equal("Git native tools — off (git native tools is off)", Headings(Facts() with { GitEnabled = false })[6]);
        Assert.Equal("Git native tools — not offered (LLM offer tools is off)", Headings(Facts(tools: false))[6]);
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
    public void PromptSections_TheShellRow_SaysOnOffOrNone_AndTheRuleRidesWhileTheToolIsOffered()
    {
        Assert.Equal("Shell tools — on, 1 tool offered", Headings(Facts(shellTools: 1))[7]);
        Assert.Equal(ShellHeading, Headings(Facts())[7]);
        Assert.Equal("Shell tools — off (Shell command policy is off)", Headings(Facts() with { ShellEnabled = false })[7]);
        Assert.Equal("Shell tools — not offered (LLM offer tools is off)", Headings(Facts(tools: false))[7]);
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
    }

    [Fact]
    public void PromptSections_TheMcpRow_SaysOnOffOrNone()
    {
        Assert.Equal("MCP servers — none connected", Headings(Facts())[8]);
        Assert.Equal("MCP servers — on, 2 servers, 14 tools offered", Headings(Facts() with { McpServers = 2, McpTools = 14 })[8]);
        Assert.Equal("MCP servers — off (MCP servers is off)", Headings(Facts() with { McpEnabled = false, McpServers = 2, McpTools = 14 })[8]);
        Assert.Equal("MCP servers — not offered (LLM offer tools is off)", Headings(Facts(tools: false) with { McpServers = 2, McpTools = 14 })[8]);
        // The rule rides the defaults only while something is offered.
        Assert.Contains(Assistant.McpRule, SystemPromptSummary.PromptSections(Facts() with { McpServers = 1, McpTools = 1 })[1].Body);
        Assert.DoesNotContain(Assistant.McpRule, SystemPromptSummary.PromptSections(Facts() with { McpServers = 1, McpTools = 0 })[1].Body);
        Assert.DoesNotContain(Assistant.McpRule, SystemPromptSummary.PromptSections(Facts() with { McpEnabled = false, McpServers = 1, McpTools = 1 })[1].Body);
        Assert.Equal(SystemPromptSummary.PromptSections(Facts() with { McpServers = 1, McpTools = 1 })[1].Body, Assistant.DefaultRules(false, true, mcp: true));
        Assert.Equal(Assistant.SystemPrompt(false, [], skills: [], mcp: true), SystemPromptSummary.SystemPrompt(Facts() with { McpServers = 1, McpTools = 1 }));
    }

    [Fact]
    public void ToolLines_AndToolsTab_NameEveryTool_WithItsDescription()
    {
        var (clock, timers, files, memory) = Tools();
        var groups = SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: true);

        string[] lines = SystemPromptSummary.ToolLines(groups).ToArray();
        Assert.Equal(4 + 23, lines.Length);
        Assert.Equal("Clock (3)", lines[0]);
        Assert.Equal("  " + "get_current_time".PadRight(22) + clock[0].Description, lines[1]);
        Assert.Equal("Memory (2)", lines[^3]);
        Assert.Equal("  " + "save_memory".PadRight(22) + memory[0].Description, lines[^2]);
        Assert.Equal("  " + "recall_memory".PadRight(22) + memory[1].Description, lines[^1]);

        using var console = new TestConsole();
        console.Profile.Width = 900;   // wide enough that no description wraps (patch_file and search_files are the longest, 2026-09-19)
        console.Write(SystemPromptSummary.ToolsTab(groups));
        string output = console.Output;
        foreach (var tool in clock.Concat(timers).Concat(files).Concat(memory))
        {
            Assert.Contains("\n" + tool.Name + "  ", "\n" + output);
            Assert.Contains(tool.Description, output);
        }

        // One grid for every group (2026-09-16): the description column starts where the longest
        // tool name puts it, under every heading alike — measured, never a literal width.
        int column = groups.SelectMany(g => g.Tools).Max(t => t.Name.Length) + SlashCommands.HelpColumnGap;
        string[] rendered = output.TrimEnd('\n').Split('\n').Select(l => l.TrimEnd()).ToArray();
        Assert.Equal("Clock (3)", rendered[0]);
        Assert.Equal("get_current_time".PadRight(column) + clock[0].Description, rendered[1]);
        Assert.Equal("", rendered[4]);   // a one-space row parts the groups
        Assert.Equal("Timers (3)", rendered[5]);
        Assert.Equal("Files (15)", rendered[10]);
        Assert.Equal("get_working_directory".PadRight(column) + files[0].Description, rendered[11]);
        Assert.Equal("Memory (2)", rendered[^3]);
        Assert.Equal("save_memory".PadRight(column) + memory[0].Description, rendered[^2]);
        Assert.Equal("recall_memory".PadRight(column) + memory[1].Description, rendered[^1]);
        foreach (var line in rendered.Where(l => l.Length > column))
        {
            Assert.Equal(' ', line[column - 1]);
            Assert.NotEqual(' ', line[column]);
        }

        // A group that is not offered: its reason dim in the description column, not on the name.
        using var off = new TestConsole();
        off.Profile.Width = 400;
        off.Write(SystemPromptSummary.ToolsTab(SystemPromptSummary.ToolGroups(clock, timers, files, memory, memoryEnabled: false)));
        Assert.Equal("Memory (2)".PadRight(column) + SystemPromptSummary.NotOffered("memory is off"), off.Output.TrimEnd('\n').Split('\n').Select(l => l.TrimEnd()).ToArray()[^3]);

        // The heading keeps the section colour whether the group is offered or not (later on 2026-09-20,
        // the user's call, the /tools rule): the same escape sequence leads "Memory (2)" on and off.
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
        Assert.Equal(Lead(ansiOn.Output, "Memory (2)"), Lead(dimmed, "Memory (2)"));
        Assert.Equal(Lead(ansiOn.Output, "Clock (3)"), Lead(dimmed, "Memory (2)"));
        Assert.NotEqual(Lead(ansiOn.Output, "save_memory"), Lead(dimmed, "save_memory"));   // the rows under it still dim
    }
}
