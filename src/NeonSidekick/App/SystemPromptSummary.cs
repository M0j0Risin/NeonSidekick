using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Mcp;
using NeonSidekick.Memory;
using NeonSidekick.Skills;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.App;

/// <summary>
/// What <c>/sys</c> shows: the live state a turn is prepared from, read the way
/// <see cref="ChatScreen.PrepareTurn"/> and <see cref="Assistant.RunTurnAsync"/> read it.
/// </summary>
/// <param name="Persona">The <c>persona.md</c> text, null for the default persona.</param>
/// <param name="OperatingRules">The <c>operata.md</c> text, null for the default operating rules.</param>
/// <param name="VoiceDirective">The <c>vocalia.md</c> text, null for the default voice directive; only in the prompt while the turn speaks.</param>
/// <param name="Memory">The memory switch; off means no memory section, no <c>save_memory</c> / <c>recall_memory</c> tool and no opening memory call.</param>
/// <param name="Memories">What is remembered, oldest first (empty when memory is off).</param>
/// <param name="TtsOutput">The speech-output switch.</param>
/// <param name="SpeechReady">Whether the TTS server answered: the directive goes in only when both are true.</param>
/// <param name="TurnCount">The user turns in the history: zero means the opening calls are still to come.</param>
/// <param name="OpeningResult">What the seeded <c>get_current_time</c> result would say now.</param>
/// <param name="Reasoning">The reasoning effort the request carries.</param>
/// <param name="OpeningCwdResult">What the seeded <c>get_working_directory</c> result says now — the text kept current in the history.</param>
/// <param name="ToolsEnabled">The setting <c>LLM offer tools</c>: off means no tool offered, no opening call, and the tool-free defaults.</param>
/// <param name="FilesEnabled">The setting <c>File tools</c>: off means no file tool offered, no opening working-directory call, and the default rules without <see cref="Assistant.FileRule"/>.</param>
/// <param name="SkillsEnabled">The setting <c>Agent skills</c> (2026-09-16): off means no skills block, no project notes and no skill tool.</param>
/// <param name="Skills">The catalog as of the last scan (empty when off or none installed).</param>
/// <param name="Project">The working directory's <c>NEON.md</c> / <c>AGENTS.md</c> notes, or null when neither is there (or skills or the project file are off).</param>
/// <param name="DisabledTools">The tools switched off one by one on <c>/tools</c> (2026-09-19, <c>ToolsDisabled</c>): an opening call whose tool is here is not sent, and <c>recall_memory</c> here puts the list back into the prompt.</param>
/// <param name="ProjectFile">The setting <c>Project file</c> (later on 2026-09-19, the Project tab of <c>/skills</c>): off means the notes are not read, whatever the working directory holds.</param>
/// <param name="McpEnabled">The setting <c>MCP servers</c> (2026-09-20, the Options tab of <c>/mcp</c>): off means no server is started and no MCP tool offered.</param>
/// <param name="McpServers">How many MCP servers are connected.</param>
/// <param name="McpTools">How many of their tools the next turn offers (the ones switched off on <c>/mcp</c> left out).</param>
/// <param name="FileSafeEdits">The setting <c>File safe edits</c> (2026-09-20): off with <c>delete</c> offered puts <see cref="Assistant.FileRuleDeleteInPlace"/> into the default rules — <c>delete</c> removes for good then — and (later still that day) drops <c>restore</c> from the offer, so neither the rules nor the Tools tab name it.</param>
/// <param name="GitEnabled">The setting <c>Git native tools</c> (2026-09-20, the Git (native) tab of <c>/tools</c>; <c>Git tools</c> on the Git tab until 2026-09-21).</param>
/// <param name="GitTools">How many git tools the next turn offers (the ones switched off on <c>/tools</c> left out); the rules carry <see cref="Assistant.GitRule"/> while any is.</param>
/// <param name="ShellEnabled">Whether the setting <c>Shell command policy</c> is not <c>off</c> (2026-09-21, the Shell tab of <c>/tools</c>): the group's switch.</param>
/// <param name="ShellTools">How many shell tools the next turn offers (the ones switched off on <c>/tools</c> left out); the rules carry <see cref="Assistant.ShellRule"/> while any is.</param>
/// <param name="ShellBridge">The setting <c>Shell tool bridge</c> (later on 2026-09-21): off, the shell rule is <see cref="Assistant.ShellRuleWithoutBridge"/>, which never says a script can call tools.</param>
/// <param name="ShellPolice">The setting <c>Shell police outside paths</c> (2026-09-22): off, the shell rule is an <c>…Unpoliced</c> variant, which says a command starts in the working directory and no more.</param>
/// <param name="ObsidianEnabled">Whether the vault tools may be offered (2026-09-22): the setting <c>Obsidian tools</c> on and <c>Obsidian vault</c> naming a folder with <c>.obsidian</c> — the group's switch (<see cref="ChatScreen.ObsidianOffered"/>).</param>
/// <param name="ObsidianTools">How many vault tools the next turn offers (the ones switched off on <c>/tools</c> left out); the rules carry <see cref="Assistant.ObsidianRule"/> while any is.</param>
/// <param name="ObsidianAllowDelete">The setting <c>Obsidian allow delete</c> (later on 2026-09-22; on by default since 2026-09-23, the parameter's false the bare facts' default): on, and <c>vault_delete</c> not switched off, the vault rule gains <see cref="Assistant.ObsidianDeleteRule"/>.</param>
/// <param name="SqlEnabled">Whether the SQL tools may be offered (2026-09-23): the setting <c>SQL tools</c> on and a usable connection in <c>sql.json</c> — the group's switch (<see cref="ChatScreen.SqlOffered"/>).</param>
/// <param name="SqlTools">How many SQL tools the next turn offers (the ones switched off on <c>/tools</c> left out); the rules carry <see cref="Assistant.SqlRule"/> while any is.</param>
/// <param name="ShellNative">The setting <c>Shell prefer native tools</c> (2026-09-26): on, with a shell rule, the rules gain <see cref="Assistant.ShellNativeRule"/> after it.</param>
public sealed record SystemPromptFacts(
    string? Persona,
    string? OperatingRules,
    string? VoiceDirective,
    bool Memory,
    IReadOnlyList<string> Memories,
    bool TtsOutput,
    bool SpeechReady,
    int TurnCount,
    string OpeningResult,
    ReasoningEffort Reasoning,
    string OpeningCwdResult,
    bool ToolsEnabled = true,
    bool FilesEnabled = true,
    bool SkillsEnabled = true,
    IReadOnlyList<Skill>? Skills = null,
    ProjectNotes? Project = null,
    bool TranscriptMarkdown = false,
    bool PaneOn = true,
    IReadOnlySet<string>? DisabledTools = null,
    bool ProjectFile = true,
    bool McpEnabled = true,
    int McpServers = 0,
    int McpTools = 0,
    bool FileSafeEdits = true,
    bool GitEnabled = true,
    int GitTools = 0,
    bool ShellEnabled = true,
    int ShellTools = 0,
    bool ShellBridge = false,
    bool ShellPolice = true,
    bool ObsidianEnabled = false,
    int ObsidianTools = 0,
    bool ObsidianAllowDelete = false,
    bool SqlEnabled = false,
    int SqlTools = 0,
    bool ShellNative = false)
{
    /// <summary>Whether the rules carry <see cref="Assistant.McpRule"/>: tools on, the MCP switch on and at least one MCP tool offered.</summary>
    public bool Mcp => ToolsEnabled && McpEnabled && McpTools > 0;

    /// <summary>Whether the rules carry <see cref="Assistant.GitRule"/>: tools on, the Git switch on and at least one git tool offered (2026-09-20).</summary>
    public bool Git => ToolsEnabled && GitEnabled && GitTools > 0;

    /// <summary>Whether the rules carry a shell rule: tools on, the policy not off and at least one shell tool offered (2026-09-21).</summary>
    public bool Shell => ToolsEnabled && ShellEnabled && ShellTools > 0;

    /// <summary>Whether that rule is <see cref="Assistant.ShellRule"/> (the bridge on) rather than <see cref="Assistant.ShellRuleWithoutBridge"/>.</summary>
    public bool Bridge => Shell && ShellBridge;

    /// <summary>Whether that rule's head says the shell stays under the working directory (the police on, 2026-09-22); true without a shell rule, so the default holds (<see cref="Assistant.ShellRuleFor"/>).</summary>
    public bool Police => !Shell || ShellPolice;

    /// <summary>Whether the shell rule is followed by <see cref="Assistant.ShellNativeRule"/> (2026-09-26): a shell rule, and the setting on.</summary>
    public bool Native => Shell && ShellNative;

    /// <summary>Whether the rules carry <see cref="Assistant.ObsidianRule"/>: tools on, a vault set with its switch on, and at least one vault tool offered (2026-09-22).</summary>
    public bool Obsidian => ToolsEnabled && ObsidianEnabled && ObsidianTools > 0;

    /// <summary>Whether that rule is followed by <see cref="Assistant.ObsidianDeleteRule"/>: <c>vault_delete</c> offered — the setting <c>Obsidian allow delete</c> on and the tool not switched off (later on 2026-09-22).</summary>
    public bool ObsidianDelete => Obsidian && ObsidianAllowDelete && !Off(NeonSidekick.Llm.Tools.VaultDeleteTool.ToolName);

    /// <summary>Whether the rules carry <see cref="Assistant.SqlRule"/>: tools on, a connection defined with the switch on, and at least one SQL tool offered (2026-09-23).</summary>
    public bool Sql => ToolsEnabled && SqlEnabled && SqlTools > 0;

    /// <summary>The next turn's reply is styled Markdown and asked for as such (<see cref="ChatScreen.MarkdownTurn"/>): the setting, the pane, and the turn not spoken.</summary>
    public bool Markdown => ChatScreen.MarkdownTurn(TranscriptMarkdown, PaneOn, TtsOutput && SpeechReady);

    /// <summary>Whether <paramref name="tool"/> is switched off on <c>/tools</c>.</summary>
    public bool Off(string tool) => DisabledTools is not null && DisabledTools.Contains(tool);

    /// <summary>Whether the opening <c>recall_memory</c> call rides: memory on and the tool not switched off — else the list is in the prompt (2026-09-19).</summary>
    public bool Recall => Memory && !Off(RecallMemoryTool.ToolName);

    /// <summary>Whether the rules carry <see cref="Assistant.TimerRule"/>: any of the three timer tools not switched off on <c>/tools</c> (2026-09-20; the emptied group drops its sentence).</summary>
    public bool Timers => !(Off(StartTimerTool.ToolName) && Off(StopTimerTool.ToolName) && Off(ListTimersTool.ToolName));
}

/// <summary>Where a part of the summary goes: in the system message, or elsewhere on the request.</summary>
public enum SystemPromptPart
{
    /// <summary>A section of the system message (its text is empty when the section is left out).</summary>
    Prompt,

    /// <summary>Sent with the request but not as system-prompt text: the opening calls, the reasoning fields.</summary>
    Request,
}

/// <summary>One part of the summary: a status heading, the text under it (empty when there is none), and where it goes.</summary>
public sealed record SystemPromptSection(string Heading, string Body, SystemPromptPart Part)
{
    /// <summary>True for a section whose text is in the system message.</summary>
    public bool InPrompt => Part == SystemPromptPart.Prompt && Body.Length > 0;
}

/// <summary>A group of the Tools tab: its name, why it is not offered (if it is not), its tools, and whether the next turn offers them.</summary>
/// <param name="Name">The group's name and count: <c>Files (15)</c>.</param>
/// <param name="Note">Why the group is not offered (<c>not offered: file tools is off</c>), empty while it is — dim in the description column on the pane, so a long reason never widens the name column.</param>
/// <param name="Tools">The tools of the group, in the order the turn offers them.</param>
/// <param name="Offered">Whether the next turn offers the group.</param>
public sealed record ToolGroup(string Name, string Note, IReadOnlyList<AIFunction> Tools, bool Offered)
{
    /// <summary>The plain-line heading: the name, then <c> — </c> and the note when there is one (<c>Files (15) — not offered: file tools is off</c>). Pinned.</summary>
    public string Title => Note.Length > 0 ? $"{Name} — {Note}" : Name;

    /// <summary>
    /// Why a single tool of an offered group is not offered (2026-09-19), by name: <c>not offered: switched off in /tools</c>,
    /// <c>download_file</c> under <c>File tools</c> off, <c>load_skill</c> with no skill installed. Empty for every tool the turn offers.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToolNotes { get; init; } = EmptyNotes;

    /// <summary>The settings row that switches the whole group (<c>File tools</c> for Files …); null for the standing clock and timer groups.</summary>
    public SettingsField? Switch { get; init; }

    /// <summary>Whether the next turn offers <paramref name="tool"/>: the group offered and no note on the tool.</summary>
    public bool Offers(string tool) => Offered && !ToolNotes.ContainsKey(tool);

    private static readonly IReadOnlyDictionary<string, string> EmptyNotes = new Dictionary<string, string>(0, StringComparer.Ordinal);
}

/// <summary>
/// The <c>/sys</c> content: pure builders with pinned wording over <see cref="SystemPromptFacts"/>
/// and the tool lists. The Prompt tab is the system message the next turn sends, section by section
/// under a status heading, in the order <see cref="Assistant.SystemPrompt(bool, IReadOnlyList{string}, string, string, string)"/>
/// joins them — the bodies of the sections marked in the prompt, joined by a blank line, ARE that
/// string, and a test pins it — then two parts that go on the request but not in the system message:
/// the opening clock, working-directory and memory calls and the reasoning fields. The Tools tab lists every tool the turn offers,
/// grouped, name and description, in one grid. Every heading in <see cref="Theme.SectionHeading"/> over rows
/// labelled in <see cref="Theme.AccentSecondary"/>. <c>Text</c> cells everywhere, never <c>Markup</c>: a persona or a
/// remembered fact may hold brackets.
/// </summary>
public static class SystemPromptSummary
{
    /// <summary>The info pane's strip label: the toolbar's glyph, then the name (the glyph since later on 2026-09-21).</summary>
    public const string Label = ChatScreen.SysToolGlyph + " System prompt";

    /// <summary>The tab titles.</summary>
    public const string PromptTabTitle = "Prompt";
    public const string ToolsTabTitle = "Tools";

    /// <summary>Above the parts that are sent but are not system-prompt text.</summary>
    public const string AlsoSentHeading = "Also sent, outside the system prompt";

    /// <summary>Under the opening clock call, whichever way it stands.</summary>
    public const string OpeningNote = "The date is never in the prompt: it reaches the model as this tool result, and get_current_time again when a question needs it.";

    /// <summary>Under the opening working-directory call, whichever way it stands.</summary>
    public const string OpeningCwdNote = "Replaced in place when /cwd or the settings pane changes the path, so the model never holds a stale one.";

    /// <summary>Under the opening memory call, whichever way it stands (2026-09-17).</summary>
    public const string OpeningMemoryNote = "Replaced in place at every turn, so a fact saved, typed or forgotten since the first message is in the next request; the list is not in the prompt while a tool can carry it.";

    /// <summary>The tail of every heading that the setting <c>LLM offer tools</c> turned off. Pinned.</summary>
    public const string ToolsOffSuffix = "LLM offer tools is off";

    /// <summary>The tail of the opening memory call while the setting <c>Memory</c> is off (2026-09-17). Pinned.</summary>
    public const string MemoryOffSuffix = "memory is off";

    /// <summary>The tail of the Files group and the opening working-directory call while the setting <c>File tools</c> is off (2026-09-15). Pinned.</summary>
    public const string FilesOffSuffix = "file tools is off";

    /// <summary>The note on <c>restore</c> while the setting <c>File safe edits</c> is off (later still on 2026-09-20: nothing lands in <c>.trash</c> then, so the tool is not offered). Pinned.</summary>
    public const string SafeEditsOffSuffix = "File safe edits is off";

    /// <summary>The tail of the Questions group while the setting <c>Ask user</c> is off (2026-09-15). Pinned.</summary>
    public const string AskOffSuffix = "ask user is off";

    /// <summary>The tail of the Questions group while the bottom pane is off (<c>ask_user</c> has nowhere to draw, 2026-09-15). Pinned.</summary>
    public const string NoPaneSuffix = "no pane";

    /// <summary>The tail of the Skills group, the skills section and the project notes while the setting <c>Agent skills</c> is off (2026-09-16). Pinned.</summary>
    public const string SkillsOffSuffix = "agent skills is off";

    /// <summary>The Project notes row's reason while the toggle on <c>/skills</c>' Project tab is off (later on 2026-09-19). Pinned.</summary>
    public const string ProjectFileOffSuffix = "Project file is off on the Project tab of /skills";

    /// <summary>The tail of the Sessions group while the setting <c>Session tool</c> is off (2026-09-18). Pinned.</summary>
    public const string SessionsOffSuffix = "session tool is off";

    /// <summary>The tail of the Git (native) group and its Prompt-tab heading while the setting <c>Git native tools</c> is off (2026-09-20; the setting's new name since 2026-09-21). Pinned.</summary>
    public const string GitOffSuffix = "git native tools is off";

    /// <summary>The tail of the Shell group and its Prompt-tab heading while the setting <c>Shell command policy</c> is <c>off</c> (2026-09-21). Pinned.</summary>
    public const string ShellOffSuffix = "Shell command policy is off";

    /// <summary>The tail of the Obsidian group and its Prompt-tab heading while the vault tools cannot be offered: the switch off, or no vault set (2026-09-22). Pinned.</summary>
    public const string ObsidianOffSuffix = "Obsidian tools is off or no vault is set";

    /// <summary>The tail of the SQL group while the SQL tools cannot be offered: the switch off, or no connection in <c>sql.json</c> (2026-09-23). Pinned.</summary>
    public const string SqlOffSuffix = "SQL tools is off or no connection is set in sql.json";

    /// <summary>The tail of the ComfyUI group while the image tools cannot be offered (2026-09-24). Pinned.</summary>
    public const string ComfyOffSuffix = "ComfyUI tools is off, no ComfyUI URL is set or no workflow is in a comfy folder";

    /// <summary>The note on <c>execute_code</c> while none of the languages <c>Shell code languages</c> names is installed (2026-09-21). Pinned.</summary>
    public const string NoInterpreterSuffix = "no interpreter found for the languages in Shell code languages";

    /// <summary>The note on a tool switched off by name on <c>/tools</c> (2026-09-19). Pinned.</summary>
    public const string DisabledSuffix = "switched off in /tools";

    /// <summary>The note on an MCP tool switched off by name on <c>/mcp</c>' Tools tab (2026-09-20). Pinned.</summary>
    public const string McpDisabledSuffix = "switched off in /mcp";

    /// <summary>The MCP groups' reason while the setting <c>MCP servers</c> is off. Pinned.</summary>
    public const string McpOffSuffix = "MCP servers is off";

    /// <summary>The MCP groups' name prefix: <c>MCP docker (14)</c>.</summary>
    public const string McpGroupPrefix = "MCP ";

    /// <summary>The note on <c>load_skill</c> while no skill is installed (the tool is dropped then; the <c>/tools</c> list says so, 2026-09-19). Pinned.</summary>
    public const string NoSkillSuffix = "no skill installed";

    /// <summary>The tail of an opening call whose tool is switched off on <c>/tools</c>, and of the memory heading while the list is in the prompt for that reason (2026-09-19): <c>recall_memory is off in /tools</c>. Pinned.</summary>
    public static string ToolOff(string tool) => $"{tool} is off in /tools";

    /// <summary>The Reply format heading while the setting <c>Transcript markdown</c> is off (2026-09-16). Pinned.</summary>
    public const string MarkdownOffSuffix = "transcript markdown is off";

    /// <summary>The Reply format heading while the turn speaks: the voice directive forbids Markdown, so plain text is asked for (2026-09-16). Pinned.</summary>
    public const string SpokenSuffix = "the turn speaks";

    /// <summary>The Reply format heading while <c>operata.md</c> stands: the file says what it says (2026-09-16). Pinned.</summary>
    public const string OperataStandsSuffix = OperataFile.FileName + " stands";

    // ── The Prompt tab ──────────────────────────────────────────────────────

    /// <summary>The sections in prompt order, then the two "also sent" parts. Pinned.</summary>
    public static IReadOnlyList<SystemPromptSection> PromptSections(SystemPromptFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var sections = new List<SystemPromptSection>(10);

        bool custom = !string.IsNullOrWhiteSpace(facts.Persona);
        string persona = custom ? facts.Persona!.Trim() : Assistant.DefaultPersona;
        sections.Add(new(
            custom ? $"Persona — {PersonaFile.FileName} ({persona.Length.ToString(CultureInfo.InvariantCulture)} chars)" : "Persona — default",
            persona,
            SystemPromptPart.Prompt));

        bool customRules = !string.IsNullOrWhiteSpace(facts.OperatingRules);
        string defaultLabel = !facts.ToolsEnabled ? $"default ({ToolsOffSuffix})" : !facts.FilesEnabled ? $"default ({FilesOffSuffix})" : "default";
        string rules = customRules ? facts.OperatingRules!.Trim() : Assistant.DefaultRules(facts.Markdown, facts.ToolsEnabled, facts.FilesEnabled, delete: !facts.Off(DeleteTool.ToolName), mcp: facts.Mcp, safeEdits: facts.FileSafeEdits, timers: facts.Timers, git: facts.Git, shell: facts.Shell, bridge: facts.Bridge, police: facts.Police, obsidian: facts.Obsidian, obsidianDelete: facts.ObsidianDelete, sql: facts.Sql, native: facts.Native);
        sections.Add(new(
            customRules ? $"Operating rules — {OperataFile.FileName} ({rules.Length.ToString(CultureInfo.InvariantCulture)} chars)" : $"Operating rules — {defaultLabel}",
            rules,
            SystemPromptPart.Prompt));

        // The reply format (2026-09-16): which sentence opens the default rules, and why — a heading only, the sentence is above.
        string format = customRules ? $"Reply format — {OperataStandsSuffix}"
            : facts.Markdown ? "Reply format — markdown (transcript markdown on, the pane on, the turn not spoken)"
            : !facts.TranscriptMarkdown ? $"Reply format — plain text: {MarkdownOffSuffix}"
            : !facts.PaneOn ? $"Reply format — plain text: {NoPaneSuffix}"
            : $"Reply format — plain text: {SpokenSuffix}";
        sections.Add(new(format, "", SystemPromptPart.Prompt));

        // The project notes (2026-09-16): after the rules, tools or not, while Agent skills is on.
        if (!facts.SkillsEnabled)
        {
            sections.Add(new($"Project notes — off ({SkillsOffSuffix})", "", SystemPromptPart.Prompt));
        }
        else if (!facts.ProjectFile)
        {
            sections.Add(new($"Project notes — off ({ProjectFileOffSuffix})", "", SystemPromptPart.Prompt));
        }
        else if (facts.Project is { } project)
        {
            sections.Add(new($"Project notes — {project.FileName} ({project.Text.Length.ToString(CultureInfo.InvariantCulture)} chars)", Assistant.ProjectNotesSection(project), SystemPromptPart.Prompt));
        }
        else
        {
            sections.Add(new($"Project notes — none ({string.Join(" / ", ProjectFile.FileNames)} not in the working directory)", "", SystemPromptPart.Prompt));
        }

        string remembered = facts.Memories.Count == 1 ? "1 fact remembered" : $"{facts.Memories.Count.ToString(CultureInfo.InvariantCulture)} facts remembered";
        if (facts.Memory && facts.ToolsEnabled && facts.Recall)
        {
            // The list rides the opening call (2026-09-17): the section is the directive alone, the facts are under Also sent.
            sections.Add(new($"Memory — on, directive (the list rides the opening {RecallMemoryTool.ToolName} call)", MemoryPrompt.Section(facts.Memories, tools: true), SystemPromptPart.Prompt));
        }
        else if (facts.Memory && facts.ToolsEnabled)
        {
            // recall_memory switched off on /tools (2026-09-19): nothing can carry the list, so it rides the prompt as under LLM offer tools off.
            sections.Add(new($"Memory — on, {remembered} (in the prompt: {ToolOff(RecallMemoryTool.ToolName)})", MemoryPrompt.Section(facts.Memories, tools: false), SystemPromptPart.Prompt));
        }
        else if (facts.Memory)
        {
            sections.Add(new($"Memory — on, {remembered}", MemoryPrompt.Section(facts.Memories, tools: false), SystemPromptPart.Prompt));
        }
        else
        {
            sections.Add(new("Memory — off, not included", "", SystemPromptPart.Prompt));
        }

        // The skills block (2026-09-16): after the memory section, only with tools to load one.
        if (!facts.SkillsEnabled)
        {
            sections.Add(new($"Skills — off ({SkillsOffSuffix})", "", SystemPromptPart.Prompt));
        }
        else if (!facts.ToolsEnabled)
        {
            sections.Add(new($"Skills — not included ({ToolsOffSuffix})", "", SystemPromptPart.Prompt));
        }
        else
        {
            var skills = facts.Skills ?? [];
            int external = skills.Count(s => s.Scope == SkillScope.External);
            string count = skills.Count == 0 ? "none installed"
                : (skills.Count == 1 ? "1 skill" : $"{skills.Count.ToString(CultureInfo.InvariantCulture)} skills") + (external > 0 ? $" ({external.ToString(CultureInfo.InvariantCulture)} external)" : "");
            sections.Add(new($"Skills — on, {count}", SkillsPrompt.Section(skills), SystemPromptPart.Prompt));
        }

        // The git tools (2026-09-20; the heading says Git native tools, the setting's name, since 2026-09-21): a heading only — the tools are on the Tools tab, the rule is in the rules above.
        if (!facts.GitEnabled)
        {
            sections.Add(new($"Git native tools — off ({GitOffSuffix})", "", SystemPromptPart.Prompt));
        }
        else if (!facts.ToolsEnabled)
        {
            sections.Add(new($"Git native tools — not offered ({ToolsOffSuffix})", "", SystemPromptPart.Prompt));
        }
        else
        {
            sections.Add(new(facts.GitTools == 0 ? "Git native tools — on, none offered (every git tool is switched off in /tools)" : $"Git native tools — on, {GitText.Count(facts.GitTools, "tool")} offered", "", SystemPromptPart.Prompt));
        }

        // The shell tools (2026-09-21): a heading only, the git shape.
        if (!facts.ShellEnabled)
        {
            sections.Add(new($"Shell tools — off ({ShellOffSuffix})", "", SystemPromptPart.Prompt));
        }
        else if (!facts.ToolsEnabled)
        {
            sections.Add(new($"Shell tools — not offered ({ToolsOffSuffix})", "", SystemPromptPart.Prompt));
        }
        else
        {
            sections.Add(new(facts.ShellTools == 0 ? "Shell tools — on, none offered (every shell tool is switched off in /tools)" : $"Shell tools — on, {GitText.Count(facts.ShellTools, "tool")} offered", "", SystemPromptPart.Prompt));
        }

        // The vault tools (2026-09-22): a heading only, the git shape — and only while a vault is offered: most profiles
        // never name one, and an "off" heading on every /sys would be noise (the Tools tab still lists the group, dim).
        if (facts.ObsidianEnabled)
        {
            sections.Add(new(
                !facts.ToolsEnabled ? $"Obsidian tools — not offered ({ToolsOffSuffix})"
                : facts.ObsidianTools == 0 ? "Obsidian tools — on, none offered (every vault tool is switched off in /tools)"
                : $"Obsidian tools — on, {GitText.Count(facts.ObsidianTools, "tool")} offered",
                "",
                SystemPromptPart.Prompt));
        }

        // The SQL tools (2026-09-23): a heading only, the vault shape — only while a connection is offered.
        if (facts.SqlEnabled)
        {
            sections.Add(new(
                !facts.ToolsEnabled ? $"SQL tools — not offered ({ToolsOffSuffix})"
                : facts.SqlTools == 0 ? "SQL tools — on, none offered (every SQL tool is switched off in /tools)"
                : $"SQL tools — on, {GitText.Count(facts.SqlTools, "tool")} offered",
                "",
                SystemPromptPart.Prompt));
        }

        // The MCP servers (2026-09-20): a heading only — their tools are on the Tools tab, the rule is in the rules above.
        if (!facts.McpEnabled)
        {
            sections.Add(new($"MCP servers — off ({McpOffSuffix})", "", SystemPromptPart.Prompt));
        }
        else if (!facts.ToolsEnabled)
        {
            sections.Add(new($"MCP servers — not offered ({ToolsOffSuffix})", "", SystemPromptPart.Prompt));
        }
        else if (facts.McpServers == 0)
        {
            sections.Add(new("MCP servers — none connected", "", SystemPromptPart.Prompt));
        }
        else
        {
            sections.Add(new($"MCP servers — on, {McpText.Servers(facts.McpServers)}, {McpText.Tools(facts.McpTools)} offered", "", SystemPromptPart.Prompt));
        }

        if (facts.TtsOutput && facts.SpeechReady)
        {
            bool customVoice = !string.IsNullOrWhiteSpace(facts.VoiceDirective);
            string voice = customVoice ? facts.VoiceDirective!.Trim() : facts.ToolsEnabled ? Assistant.VoiceDirective : Assistant.VoiceDirectiveWithoutTools;
            string source = customVoice ? $"{VocaliaFile.FileName} ({voice.Length.ToString(CultureInfo.InvariantCulture)} chars)" : defaultLabel;
            sections.Add(new($"Voice directive — {source}, included (speech output on, TTS ready), always last", voice, SystemPromptPart.Prompt));
        }
        else
        {
            string why = facts.TtsOutput ? "TTS is not ready" : "speech output is off";
            sections.Add(new($"Voice directive — not included: {why}", "", SystemPromptPart.Prompt));
        }

        if (facts.ToolsEnabled)
        {
            if (facts.Off(GetCurrentTimeTool.ToolName))
            {
                sections.Add(new($"Opening clock call — not sent: {ToolOff(GetCurrentTimeTool.ToolName)}", "", SystemPromptPart.Request));
            }
            else
            {
                sections.Add(new(
                    facts.TurnCount == 0 ? "Opening clock call — seeded with the first message" : "Opening clock call — already sent with the first message",
                    $"{GetCurrentTimeTool.ToolName} → {facts.OpeningResult}\n{OpeningNote}",
                    SystemPromptPart.Request));
            }

            if (facts.FilesEnabled && facts.Off(GetWorkingDirectoryTool.ToolName))
            {
                sections.Add(new($"Opening working-directory call — not sent: {ToolOff(GetWorkingDirectoryTool.ToolName)}", "", SystemPromptPart.Request));
            }
            else if (facts.FilesEnabled)
            {
                sections.Add(new(
                    facts.TurnCount == 0 ? "Opening working-directory call — seeded with the first message" : "Opening working-directory call — already sent with the first message, kept current",
                    $"{GetWorkingDirectoryTool.ToolName} → {facts.OpeningCwdResult}\n{OpeningCwdNote}",
                    SystemPromptPart.Request));
            }
            else
            {
                sections.Add(new($"Opening working-directory call — not sent: {FilesOffSuffix}", "", SystemPromptPart.Request));
            }

            // The memory call (2026-09-17): the last pair, the list the model reads.
            if (facts.Memory && !facts.Recall)
            {
                sections.Add(new($"Opening memory call — not sent: {ToolOff(RecallMemoryTool.ToolName)}", "", SystemPromptPart.Request));
            }
            else if (facts.Memory)
            {
                sections.Add(new(
                    facts.TurnCount == 0 ? $"Opening memory call — seeded with the first message, {remembered}" : $"Opening memory call — already sent with the first message, kept current, {remembered}",
                    $"{RecallMemoryTool.ToolName} → {MemoryPrompt.Recalled(facts.Memories)}\n{OpeningMemoryNote}",
                    SystemPromptPart.Request));
            }
            else
            {
                sections.Add(new($"Opening memory call — not sent: {MemoryOffSuffix}", "", SystemPromptPart.Request));
            }
        }
        else
        {
            sections.Add(new($"Opening clock call — not sent: {ToolsOffSuffix}", "", SystemPromptPart.Request));
            sections.Add(new($"Opening working-directory call — not sent: {ToolsOffSuffix}", "", SystemPromptPart.Request));
            sections.Add(new($"Opening memory call — not sent: {ToolsOffSuffix}", "", SystemPromptPart.Request));
        }

        string effort = ReasoningLevel.Name(facts.Reasoning);
        sections.Add(new(
            facts.Reasoning == ReasoningEffort.None
                ? $"Request — reasoning_effort {effort} · chat_template_kwargs.enable_thinking=false"
                : $"Request — reasoning_effort {effort}",
            "",
            SystemPromptPart.Request));

        return sections;
    }

    /// <summary>The system message the next turn sends for <paramref name="facts"/>: the Prompt tab's sections, joined.</summary>
    public static string SystemPrompt(SystemPromptFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return Assistant.SystemPrompt(
            facts.TtsOutput && facts.SpeechReady,
            facts.Memory ? facts.Memories : null,
            facts.Persona,
            facts.OperatingRules,
            facts.VoiceDirective,
            facts.ToolsEnabled,
            files: facts.FilesEnabled,
            project: facts.SkillsEnabled ? facts.Project : null,
            skills: facts.SkillsEnabled ? facts.Skills ?? [] : null,
            markdown: facts.Markdown,
            recall: facts.Recall,
            delete: !facts.Off(DeleteTool.ToolName),
            mcp: facts.Mcp,
            safeEdits: facts.FileSafeEdits,
            timers: facts.Timers,
            git: facts.Git,
            shell: facts.Shell,
            bridge: facts.Bridge,
            police: facts.Police,
            obsidian: facts.Obsidian,
            obsidianDelete: facts.ObsidianDelete,
            sql: facts.Sql,
            native: facts.Native);
    }

    /// <summary>The Prompt tab: every section's heading and, when it has one, its text.</summary>
    public static IRenderable PromptTab(SystemPromptFacts facts)
    {
        var rows = new List<IRenderable>();
        bool divided = false;
        foreach (var section in PromptSections(facts))
        {
            if (section.Part == SystemPromptPart.Request && !divided)
            {
                divided = true;
                rows.Add(new Text(AlsoSentHeading, Theme.Label));
                rows.Add(new Text(" "));
            }

            rows.Add(new Text(section.Heading, Theme.SectionHeading));
            if (section.Body.Length > 0)
            {
                rows.Add(new Text(section.Body, Theme.Body));
            }

            // A space, not an empty Text: Rows adds a line break only after a child that rendered something.
            rows.Add(new Text(" "));
        }

        return new Rows(rows);
    }

    /// <summary>The Prompt tab as plain lines, for a console without the pane.</summary>
    public static IEnumerable<string> PromptLines(SystemPromptFacts facts)
    {
        bool divided = false;
        foreach (var section in PromptSections(facts))
        {
            if (section.Part == SystemPromptPart.Request && !divided)
            {
                divided = true;
                yield return AlsoSentHeading;
            }

            yield return section.Heading;
            if (section.Body.Length > 0)
            {
                foreach (var line in section.Body.Split('\n'))
                {
                    yield return "  " + line;
                }
            }
        }
    }

    // ── The Tools tab ───────────────────────────────────────────────────────

    /// <summary>
    /// The groups in the order the turn offers them; the memory group is marked when memory is off,
    /// the web group when the web tools are, the files group when the file tools are, the questions
    /// group when the setting <c>Ask user</c> is (else when there is no pane), the sessions group when
    /// the setting <c>Session tool</c> is, every group when the setting <c>LLM offer tools</c> is (the group's
    /// own reason first). Since 2026-09-19 a tool switched off by name (<paramref name="disabled"/>,
    /// <c>/tools</c>) is noted on its row (<see cref="ToolGroup.ToolNotes"/>) and the group's name counts
    /// what is left — <c>Files (13 of 15)</c>; <paramref name="skillInstalled"/> false notes <c>load_skill</c>
    /// as dropped (the <c>/tools</c> list passes it; <c>/sys</c> keeps the plain names), and <paramref name="safeEdits"/>
    /// false notes <c>restore</c> the same way (later still on 2026-09-20, the <c>download_file</c> shape). Pinned.
    /// </summary>
    public static IReadOnlyList<ToolGroup> ToolGroups(
        IReadOnlyList<AIFunction> clock,
        IReadOnlyList<AIFunction> timers,
        IReadOnlyList<AIFunction> files,
        IReadOnlyList<AIFunction> memory,
        bool memoryEnabled,
        bool toolsEnabled = true,
        IReadOnlyList<AIFunction>? web = null,
        bool webEnabled = true,
        bool filesEnabled = true,
        IReadOnlyList<AIFunction>? questions = null,
        bool askEnabled = true,
        bool paneOn = true,
        IReadOnlyList<AIFunction>? skills = null,
        bool skillsEnabled = true,
        IReadOnlyList<AIFunction>? sessions = null,
        bool sessionsEnabled = true,
        IReadOnlySet<string>? disabled = null,
        bool skillInstalled = true,
        IReadOnlyList<McpServerTools>? mcp = null,
        bool mcpEnabled = true,
        IReadOnlyList<AIFunction>? git = null,
        bool gitEnabled = true,
        bool safeEdits = true,
        IReadOnlyList<AIFunction>? shell = null,
        bool shellEnabled = true,
        bool codeAvailable = true,
        IReadOnlyList<AIFunction>? obsidian = null,
        bool obsidianEnabled = true,
        IReadOnlyList<AIFunction>? sql = null,
        bool sqlEnabled = true,
        IReadOnlyList<AIFunction>? comfy = null,
        bool comfyEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(memory);
        string standing = toolsEnabled ? "" : NotOffered(ToolsOffSuffix);
        string memoryNote = !memoryEnabled ? NotOffered("memory is off") : standing;
        string webNote = !webEnabled ? NotOffered("web is off") : standing;
        string filesNote = !filesEnabled ? NotOffered(FilesOffSuffix) : standing;
        string questionsNote = !askEnabled ? NotOffered(AskOffSuffix) : !paneOn ? NotOffered(NoPaneSuffix) : standing;
        // restore rides only with File safe edits on (later still on 2026-09-20): a whole file list under the setting off notes it (the /tools list; /sys passes the list already cut).
        var fileNotes = !safeEdits && files.Any(t => t is RestoreTool) ? new Dictionary<string, string>(StringComparer.Ordinal) { [RestoreTool.ToolName] = NotOffered(SafeEditsOffSuffix) } : null;
        var groups = new List<ToolGroup>(8)
        {
            Group("Clock", clock, standing, toolsEnabled, null, disabled),
            Group("Timers", timers, standing, toolsEnabled, null, disabled),
            Group("Files", files, filesNote, filesEnabled && toolsEnabled, SettingsField.FileTools, disabled, fileNotes),
        };
        if (git is not null)
        {
            // The git tools (2026-09-20): right after the file tools, the sandbox's two groups together; offered while the setting Git native tools says so.
            string gitNote = !gitEnabled ? NotOffered(GitOffSuffix) : standing;
            groups.Add(Group(ToolsText.GitTabTitle, git, gitNote, gitEnabled && toolsEnabled, SettingsField.GitNativeTools, disabled));   // "Git (native)", the tab's word, since 2026-09-21
        }

        if (shell is not null)
        {
            // The shell tools (2026-09-21): after the git tools; offered while the setting Shell command policy is not off, which is the group's switch row.
            string shellNote = !shellEnabled ? NotOffered(ShellOffSuffix) : standing;
            // execute_code rides only with an interpreter to run (2026-09-21): the row stays, dim, with its reason — the download_file shape.
            var codeNotes = !codeAvailable && shell.Any(t => t is ExecuteCodeTool) ? new Dictionary<string, string>(StringComparer.Ordinal) { [ExecuteCodeTool.ToolName] = NotOffered(NoInterpreterSuffix) } : null;
            groups.Add(Group("Shell", shell, shellNote, shellEnabled && toolsEnabled, SettingsField.ShellCommandPolicy, disabled, codeNotes));
        }

        if (obsidian is not null)
        {
            // The vault tools (2026-09-22): after the shell tools, the last of the tools that act on the disk; offered while the setting Obsidian tools is on and a vault is set.
            string obsidianNote = !obsidianEnabled ? NotOffered(ObsidianOffSuffix) : standing;
            groups.Add(Group(ToolsText.ObsidianTabTitle, obsidian, obsidianNote, obsidianEnabled && toolsEnabled, SettingsField.ObsidianTools, disabled));
        }

        if (sql is not null)
        {
            // The SQL tools (2026-09-23): after the vault tools; offered while the setting SQL tools is on and sql.json holds a connection.
            string sqlNote = !sqlEnabled ? NotOffered(SqlOffSuffix) : standing;
            groups.Add(Group(ToolsText.SqlTabTitle, sql, sqlNote, sqlEnabled && toolsEnabled, SettingsField.SqlTools, disabled));
        }

        if (comfy is not null)
        {
            // The image tools (2026-09-24): after the SQL tools; offered while ComfyUI tools is on, a URL is set and a workflow is installed.
            string comfyNote = !comfyEnabled ? NotOffered(ComfyOffSuffix) : standing;
            groups.Add(Group(ToolsText.ComfyTabTitle, comfy, comfyNote, comfyEnabled && toolsEnabled, SettingsField.ComfyTools, disabled));
        }

        if (web is not null)
        {
            // download_file rides only with the file tools (2026-09-18): a whole web list under File tools off notes it (the /tools list; /sys passes the list already cut).
            var notes = !filesEnabled && web.Any(t => t is DownloadFileTool) ? new Dictionary<string, string>(StringComparer.Ordinal) { [DownloadFileTool.ToolName] = NotOffered(FilesOffSuffix) } : null;
            groups.Add(Group("Web", web, webNote, webEnabled && toolsEnabled, SettingsField.WebTools, disabled, notes));
        }

        groups.Add(Group("Memory", memory, memoryNote, memoryEnabled && toolsEnabled, SettingsField.Memory, disabled));
        if (skills is not null)
        {
            // The skill tools (2026-09-16): after the memory tool, offered while the setting Agent skills says so; load_skill only with a skill to load.
            string skillsNote = !skillsEnabled ? NotOffered(SkillsOffSuffix) : standing;
            var notes = !skillInstalled && skills.Any(t => t is LoadSkillTool) ? new Dictionary<string, string>(StringComparer.Ordinal) { [LoadSkillTool.ToolName] = NotOffered(NoSkillSuffix) } : null;
            groups.Add(Group("Skills", skills, skillsNote, skillsEnabled && toolsEnabled, SettingsField.AgentSkills, disabled, notes));
        }

        if (sessions is not null)
        {
            // The session tool (2026-09-18): after the skills, offered while the setting Session tool says so.
            string sessionsNote = !sessionsEnabled ? NotOffered(SessionsOffSuffix) : standing;
            groups.Add(Group("Sessions", sessions, sessionsNote, sessionsEnabled && toolsEnabled, SettingsField.SessionTool, disabled));
        }

        if (mcp is not null)
        {
            // The MCP servers (2026-09-20): one group per connected server, after the sessions and before the questions, offered while the setting MCP servers says so; a row switched off names /mcp.
            string mcpNote = !mcpEnabled ? NotOffered(McpOffSuffix) : standing;
            foreach (var server in mcp)
            {
                groups.Add(Group(McpGroupPrefix + server.Name, server.Tools, mcpNote, mcpEnabled && toolsEnabled, SettingsField.McpServers, disabled, disabledSuffix: McpDisabledSuffix));
            }
        }

        if (questions is not null)
        {
            // The question tool (2026-09-15): last, offered while the setting Ask user and the bottom pane say so.
            groups.Add(Group("Questions", questions, questionsNote, askEnabled && paneOn && toolsEnabled, SettingsField.AskUser, disabled));
        }

        return groups;
    }

    /// <summary>One group: the disabled tools noted (<paramref name="notes"/> first — a tool dropped for another reason keeps that reason), the name counting what the /tools list left.</summary>
    private static ToolGroup Group(string name, IReadOnlyList<AIFunction> tools, string note, bool offered, SettingsField? @switch, IReadOnlySet<string>? disabled, Dictionary<string, string>? notes = null, string disabledSuffix = DisabledSuffix)
    {
        if (disabled is { Count: > 0 })
        {
            foreach (var tool in tools)
            {
                if (disabled.Contains(tool.Name) && (notes is null || !notes.ContainsKey(tool.Name)))
                {
                    (notes ??= new Dictionary<string, string>(StringComparer.Ordinal))[tool.Name] = NotOffered(disabledSuffix);
                }
            }
        }

        // The name counts the /tools list alone: a tool dropped for another reason (download_file, load_skill) keeps its group's plain count, as /sys has always shown it.
        int left = disabled is null ? tools.Count : tools.Count(t => !disabled.Contains(t.Name));
        return new ToolGroup(GroupName(name, left, tools.Count), note, tools, offered)
        {
            ToolNotes = notes ?? new Dictionary<string, string>(0, StringComparer.Ordinal),
            Switch = @switch,
        };
    }

    /// <summary>The group's name and count: <c>Files (15)</c> with every tool offered, <c>Files (13 of 15)</c> with some switched off by name (2026-09-19). Pinned.</summary>
    public static string GroupName(string name, int offered, int total) =>
        offered == total
            ? $"{name} ({total.ToString(CultureInfo.InvariantCulture)})"
            : $"{name} ({offered.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>A group's note while it is not offered: <c>not offered: memory is off</c>. Pinned.</summary>
    public static string NotOffered(string why) => $"not offered: {why}";

    /// <summary>
    /// The Tools tab: a heading row per group — its name in the section style whether the group is
    /// offered or not (a heading never dims, later on 2026-09-20, the user's call; the /tools rule), its
    /// note (if any) dim in the description column — then its tools as name and description, all dim
    /// when the group is not offered. One grid for every group, so the description column sits at the same place under
    /// every heading (a grid per group sized its name column to its own longest tool name and the
    /// descriptions jumped between groups, 2026-09-16); a one-space row parts the groups.
    /// </summary>
    public static IRenderable ToolsTab(IReadOnlyList<ToolGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var grid = ChatScreen.TwoColumns();
        for (int i = 0; i < groups.Count; i++)
        {
            if (i > 0)
            {
                // A one-space cell, as the Commands tab: an empty row renders no line and would collapse.
                grid.AddRow(new Text(" "), Text.Empty);
            }

            var group = groups[i];
            grid.AddRow(
                new Text(group.Name, Theme.SectionHeading),
                new Text(group.Note, Theme.DimText));
            foreach (var tool in group.Tools)
            {
                // A tool the group offers but the turn does not (2026-09-19): dim like an unoffered group, its reason after the description.
                bool offered = group.Offers(tool.Name);
                grid.AddRow(
                    new Text(tool.Name, offered ? Theme.AccentSecondary : Theme.DimText),
                    group.ToolNotes.TryGetValue(tool.Name, out var toolNote)
                        ? new Text(tool.Description + "  " + toolNote, Theme.DimText)
                        : new Text(tool.Description, offered ? Theme.Body : Theme.DimText));
            }
        }

        return grid;
    }

    /// <summary>The name column of the plain tool lines (and of the <c>/tools</c> list).</summary>
    public const int ToolNameWidth = 22;

    /// <summary>The Tools tab as plain lines, for a console without the pane.</summary>
    public static IEnumerable<string> ToolLines(IReadOnlyList<ToolGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        foreach (var group in groups)
        {
            yield return group.Title;
            foreach (var tool in group.Tools)
            {
                yield return "  " + tool.Name.PadRight(ToolNameWidth) + tool.Description + (group.ToolNotes.TryGetValue(tool.Name, out var toolNote) ? " — " + toolNote : "");
            }
        }
    }
}
