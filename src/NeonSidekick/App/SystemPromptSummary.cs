using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Help;
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
/// What <c>/sys</c>' Prompt tab shows: the live state a turn's system message is built from, read the way
/// <see cref="ChatScreen.PrepareTurn"/> and <see cref="Assistant.RunTurnAsync"/> read it.
/// </summary>
/// <param name="Persona">The <c>persona.md</c> text, null for the default persona.</param>
/// <param name="OperatingRules">The <c>operata.md</c> text, null for the default operating rules.</param>
/// <param name="VoiceDirective">The <c>vocalia.md</c> text, null for none (there is no default since 2026-10-03); only in the prompt while the turn speaks.</param>
/// <param name="Memory">The memory switch; off means no memory section, no <c>save_memory</c> / <c>recall_memory</c> tool and no opening memory call.</param>
/// <param name="Memories">What is remembered, oldest first (empty when memory is off).</param>
/// <param name="TtsOutput">The speech-output switch.</param>
/// <param name="SpeechReady">Whether the TTS server answered: the directive goes in only when both are true.</param>
/// <param name="ToolsEnabled">The setting <c>LLM offer tools</c>: off means no tool offered, no opening call, and the tool-free defaults.</param>
/// <param name="FilesEnabled">The setting <c>File tools</c>: off means no file tool offered, no opening working-directory call, and the default rules without <see cref="Assistant.FileRule"/>.</param>
/// <param name="SkillsEnabled">The setting <c>Agent skills</c> (2026-09-16): off means no skills block, no project notes and no skill tool.</param>
/// <param name="Skills">The catalog as of the last scan (empty when off or none installed).</param>
/// <param name="Project">The working directory's <c>NEON.md</c> / <c>AGENTS.md</c> notes, or null when neither is there (or skills or the project file are off).</param>
/// <param name="DisabledTools">The tools switched off one by one on <c>/tools</c> (2026-09-19, <c>ToolsDisabled</c>): an opening call whose tool is here is not sent, and <c>recall_memory</c> here puts the list back into the prompt.</param>
/// <param name="ProjectFile">The setting <c>Project file</c> (later on 2026-09-19, the Project tab of <c>/skills</c>; its Options tab since 2026-10-01): off means the notes are not read, whatever the working directory holds.</param>
/// <param name="McpEnabled">The setting <c>MCP servers</c> (2026-09-20, the Options tab of <c>/mcp</c>): off means no server is started and no MCP tool offered.</param>
/// <param name="McpTools">How many of their tools the next turn offers (the ones switched off on <c>/mcp</c> left out).</param>
/// <param name="GitEnabled">The setting <c>GitLib tools</c> (2026-09-20, the GitLib tab of <c>/tools</c>; <c>Git tools</c> on the Git tab until 2026-09-21, <c>Git native tools</c> until 2026-09-30).</param>
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
/// <param name="ClaudeAdvisorEnabled">The setting <c>Claude CLI advisor tool</c> (2026-09-27): the group's switch.</param>
/// <param name="ClaudeAdvisorTools">How many advisor tools the next turn offers (0 while switched off on <c>/tools</c>); the rules carry <see cref="Assistant.ClaudeAdvisorRule"/> while it is.</param>
/// <param name="HomeAssistantEnabled">Whether the Home Assistant tools may be offered (2026-09-28): the setting <c>Home Assistant tools</c> on, a URL and a readable token — the group's switch (<see cref="ChatScreen.HomeAssistantOffered"/>).</param>
/// <param name="HomeAssistantTools">How many Home Assistant tools the next turn offers (the ones switched off on <c>/tools</c> left out); the rules carry <see cref="Assistant.HomeAssistantRule"/> while any is.</param>
/// <param name="OracleEnabled">Whether the Oracle tools may be offered (2026-09-30): the setting <c>Oracle tools</c> on and a usable connection in <c>oracle.json</c> — the group's switch (<see cref="ChatScreen.OracleOffered"/>).</param>
/// <param name="OracleTools">How many Oracle tools the next turn offers (the ones switched off on <c>/tools</c> left out); the rules carry <see cref="Assistant.OracleRule"/> while any is.</param>
/// <param name="MySqlEnabled">Whether the MySQL tools may be offered (2026-09-30): the setting <c>MySQL tools</c> on and a usable connection in <c>mysql.json</c> (<see cref="ChatScreen.MySqlOffered"/>).</param>
/// <param name="MySqlTools">How many MySQL tools the next turn offers; the rules carry <see cref="Assistant.MySqlRule"/> while any is.</param>
/// <param name="UncEnabled">Whether the UNC tools may be offered (2026-09-30): the setting <c>UNC tools</c> on and a usable share in <c>unc.json</c> (<see cref="ChatScreen.UncOffered"/>).</param>
/// <param name="UncTools">How many UNC tools the next turn offers (<see cref="ChatScreen.UncToolsFor"/>, the ones switched off on <c>/tools</c> left out); the rules carry <see cref="Assistant.UncRule"/> while any is.</param>
/// <param name="UncFetch">Whether <c>unc_fetch</c> is among them: the rules add <see cref="Assistant.UncFetchRule"/>.</param>
/// <param name="UncWrite">Whether a change on a share is among them (<c>UNC writes</c> on, a <c>readwrite</c> share): the rules add <see cref="Assistant.UncWriteRule"/>.</param>
/// <param name="DockerEnabled">Whether the Docker tools may be offered (2026-10-02): the setting <c>Docker tools</c> on, on Windows (<see cref="ChatScreen.DockerOffered"/>).</param>
/// <param name="DockerTools">How many Docker tools the next turn offers (<see cref="ChatScreen.DockerToolsFor"/>, the ones switched off on <c>/tools</c> left out); the rules carry <see cref="Assistant.DockerRule"/> while any is.</param>
/// <param name="DockerWrite">Whether a Docker change is among them (<c>Docker writes</c> on): the rules add <see cref="Assistant.DockerWriteRule"/>.</param>
/// <param name="HelpTools">How many of the app's manual tools the next turn offers (<c>neon_help</c>, 2026-10-02, the one switched off on <c>/tools</c> left out); the rules carry <see cref="Assistant.HelpRule"/> while it is.</param>
/// <param name="PlanDirective">Plan mode's directive while planning (2026-09-26, <see cref="Plans.PlanText.Directive"/>), else null: its own section, after the skills.</param>
public sealed record SystemPromptFacts(
    string? Persona,
    string? OperatingRules,
    string? VoiceDirective,
    bool Memory,
    IReadOnlyList<string> Memories,
    bool TtsOutput,
    bool SpeechReady,
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
    int McpTools = 0,
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
    bool ShellNative = false,
    string? PlanDirective = null,
    bool ClaudeAdvisorEnabled = false,
    int ClaudeAdvisorTools = 0,
    bool HomeAssistantEnabled = false,
    int HomeAssistantTools = 0,
    bool OracleEnabled = false,
    int OracleTools = 0,
    bool MySqlEnabled = false,
    int MySqlTools = 0,
    bool UncEnabled = false,
    int UncTools = 0,
    bool UncFetch = false,
    bool UncWrite = false,
    bool DockerEnabled = false,
    int DockerTools = 0,
    bool DockerWrite = false,
    int HelpTools = 0,
    bool SqliteEnabled = false,
    int SqliteTools = 0,
    bool PostgresEnabled = false,
    int PostgresTools = 0)
{
    /// <summary>Whether the rules carry <see cref="Assistant.HomeAssistantRule"/>: tools on, the server set with the switch on, and at least one Home Assistant tool offered (2026-09-28).</summary>
    public bool HomeAssistant => ToolsEnabled && HomeAssistantEnabled && HomeAssistantTools > 0;

    /// <summary>Whether the rules carry <see cref="Assistant.ClaudeAdvisorRule"/>: tools on, the switch on and the tool offered (2026-09-27).</summary>
    public bool Advisor => ToolsEnabled && ClaudeAdvisorEnabled && ClaudeAdvisorTools > 0;

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

    /// <summary>Whether the rules carry <see cref="Assistant.OracleRule"/>: tools on, a connection defined with the switch on, and at least one Oracle tool offered (2026-09-30).</summary>
    public bool Oracle => ToolsEnabled && OracleEnabled && OracleTools > 0;

    /// <summary>Whether the rules carry <see cref="Assistant.MySqlRule"/>: tools on, a connection defined with the switch on, and at least one MySQL tool offered (2026-09-30).</summary>
    public bool MySql => ToolsEnabled && MySqlEnabled && MySqlTools > 0;

    /// <summary>Whether the rules carry <see cref="Assistant.SqliteRule"/>: tools on, something to open with the switch on, and at least one SQLite tool offered (2026-10-04).</summary>
    public bool Sqlite => ToolsEnabled && SqliteEnabled && SqliteTools > 0;

    /// <summary>Whether the rules carry <see cref="Assistant.PostgresRule"/>: tools on, a connection defined with the switch on, and at least one PostgreSQL tool offered (2026-10-04).</summary>
    public bool Postgres => ToolsEnabled && PostgresEnabled && PostgresTools > 0;

    /// <summary>Whether the rules carry <see cref="Assistant.UncRule"/>: tools on, a share defined with the switch on, and at least one UNC tool offered (2026-09-30).</summary>
    public bool Unc => ToolsEnabled && UncEnabled && UncTools > 0;

    /// <summary>Whether the rules carry <see cref="Assistant.DockerRule"/>: tools on, the switch on, and at least one Docker tool offered (2026-10-02).</summary>
    public bool Docker => ToolsEnabled && DockerEnabled && DockerTools > 0;

    /// <summary>Whether the rules carry <see cref="Assistant.HelpRule"/>: tools on and <c>neon_help</c> offered (2026-10-02).</summary>
    public bool Help => ToolsEnabled && HelpTools > 0;

    /// <summary>The next turn's reply is styled Markdown and asked for as such (<see cref="ChatScreen.MarkdownTurn"/>): the setting, the pane, and the turn not spoken.</summary>
    public bool Markdown => ChatScreen.MarkdownTurn(TranscriptMarkdown, PaneOn, TtsOutput && SpeechReady);

    /// <summary>Whether <paramref name="tool"/> is switched off on <c>/tools</c>.</summary>
    public bool Off(string tool) => DisabledTools is not null && DisabledTools.Contains(tool);

    /// <summary>Whether the opening <c>recall_memory</c> call rides: memory on and the tool not switched off — else the list is in the prompt (2026-09-19).</summary>
    public bool Recall => Memory && !Off(RecallMemoryTool.ToolName);

    /// <summary>Whether the rules carry <see cref="Assistant.TimerRule"/>: any of the three timer tools not switched off on <c>/tools</c> (2026-09-20; the emptied group drops its sentence).</summary>
    public bool Timers => !(Off(StartTimerTool.ToolName) && Off(StopTimerTool.ToolName) && Off(ListTimersTool.ToolName));
}

/// <summary>
/// One section of the system message: a status heading and the text under it (empty when the section is
/// left out). Every section is a part of the system message since 2026-09-26 — the opening calls and the
/// request fields are no longer on the tab, so the Prompt / Request split went with them. The heading in two parts
/// since 2026-10-03 — <paramref name="Label"/> (<c>Memory</c>) and <paramref name="Status"/> (<c>on, 3 facts remembered</c>) —
/// so the pane's rule can set them apart; <see cref="Heading"/> joins them as the plain lines always had them.
/// </summary>
public sealed record SystemPromptSection(string Label, string Status, string Body)
{
    /// <summary>The plain heading: <c>Memory — on, 3 facts remembered</c>, the label alone with no status. Pinned.</summary>
    public string Heading => Status.Length > 0 ? $"{Label} — {Status}" : Label;

    /// <summary>True for a section whose text is in the system message.</summary>
    public bool InPrompt => Body.Length > 0;
}

/// <summary>A group of the Tools tab: its name, why it is not offered (if it is not), its tools, and whether the next turn offers them.</summary>
/// <param name="Name">The group's name and count: <c>Files (14)</c>.</param>
/// <param name="Note">Why the group is not offered (<c>not offered: file tools is off</c>), empty while it is — dim in the description column on the pane, so a long reason never widens the name column.</param>
/// <param name="Tools">The tools of the group, in the order the turn offers them.</param>
/// <param name="Offered">Whether the next turn offers the group.</param>
public sealed record ToolGroup(string Name, string Note, IReadOnlyList<AIFunction> Tools, bool Offered)
{
    /// <summary>The plain-line heading: the name, then <c> — </c> and the note when there is one (<c>Files (14) — not offered: file tools is off</c>). Pinned.</summary>
    public string Title => Note.Length > 0 ? $"{Name} — {Note}" : Name;

    /// <summary>
    /// Why a single tool of an offered group is not offered (2026-09-19), by name: <c>not offered: switched off in /tools</c>,
    /// <c>download_file</c> under <c>File tools</c> off, <c>load_skill</c> with no skill installed. Empty for every tool the turn offers.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToolNotes { get; init; } = EmptyNotes;

    /// <summary>The settings row that switches the whole group (<c>File tools</c> for Files …); null for the standing clock and timer groups.</summary>
    public SettingsField? Switch { get; init; }

    /// <summary>The group's bare name, without the count (<c>Files</c>, <c>MCP chrome</c>): what <see cref="SystemPromptSummary.OfferedOnly"/> recounts from (2026-09-26).</summary>
    public string Label { get; init; } = Name;

    /// <summary>The count in <see cref="Name"/> alone (<c>14</c>, <c>12 of 14</c>, <see cref="SystemPromptSummary.GroupCount"/>): what the pane's heading rule shows after the label (2026-10-03). Every tool by default.</summary>
    public string Count { get; init; } = Tools.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether the next turn offers <paramref name="tool"/>: the group offered and no note on the tool.</summary>
    public bool Offers(string tool) => Offered && !ToolNotes.ContainsKey(tool);

    private static readonly IReadOnlyDictionary<string, string> EmptyNotes = new Dictionary<string, string>(0, StringComparer.Ordinal);
}

/// <summary>
/// The <c>/sys</c> content: pure builders with pinned wording over <see cref="SystemPromptFacts"/>
/// and the tool lists. The Prompt tab is the system message the next turn sends, section by section
/// under a status heading, in the order <see cref="Assistant.SystemPrompt(bool, IReadOnlyList{string}, string, string, string)"/>
/// joins them — the bodies of the sections marked in the prompt, joined by a blank line, ARE that
/// string, and a test pins it — and nothing else: persona, operating rules, project notes, memory,
/// skills and, on a spoken turn, the voice directive (2026-09-26, the user's call: the reply-format and
/// per-tool-group headings, the opening calls and the request fields went; the rules carry the sentences
/// those headings described, and the Tools tab the tools). The Tools tab lists every tool the turn offers,
/// grouped, name and description, in one grid — and only those: a tool or a group the turn does not send is left out
/// (<see cref="OfferedOnly"/>, 2026-09-26). Every heading in <see cref="Theme.SectionHeading"/> over rows
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

    /// <summary>The tail of every heading that the setting <c>LLM offer tools</c> turned off. Pinned.</summary>
    public const string ToolsOffSuffix = "LLM offer tools is off";

    /// <summary>The tail of the Files group and the rules heading while the setting <c>File tools</c> is off (2026-09-15). Pinned.</summary>
    public const string FilesOffSuffix = "file tools is off";

    /// <summary>The tail of the Questions group while the setting <c>Ask user</c> is off (2026-09-15). Pinned.</summary>
    public const string AskOffSuffix = "ask user is off";

    /// <summary>The tail of the Questions group while the bottom pane is off (<c>ask_user</c> has nowhere to draw, 2026-09-15). Pinned.</summary>
    public const string NoPaneSuffix = "no pane";

    /// <summary>The tail of the Skills group, the skills section and the project notes while the setting <c>Agent skills</c> is off (2026-09-16). Pinned.</summary>
    public const string SkillsOffSuffix = "agent skills is off";

    /// <summary>The Project notes row's reason while the toggle on <c>/skills</c>' Options tab is off (later on 2026-09-19; its Project tab until 2026-10-01). Pinned.</summary>
    public const string ProjectFileOffSuffix = "Project file is off on the Options tab of /skills";

    /// <summary>The tail of the Sessions group while the setting <c>Session tool</c> is off (2026-09-18). Pinned.</summary>
    public const string SessionsOffSuffix = "session tool is off";

    /// <summary>The tail of the Git group while the setting <c>GitLib tools</c> is off (2026-09-20; renamed 2026-09-21 and again 2026-09-30). Pinned.</summary>
    public const string GitOffSuffix = "gitlib tools is off";

    /// <summary>The tail of the Shell group while the setting <c>Shell command policy</c> is <c>off</c> (2026-09-21). Pinned.</summary>
    public const string ShellOffSuffix = "Shell command policy is off";

    /// <summary>The tail of the Obsidian group while the vault tools cannot be offered: the switch off, or no vault set (2026-09-22). Pinned.</summary>
    public const string ObsidianOffSuffix = "Obsidian tools is off or no vault is set";

    /// <summary>The tail of the SQL group while the SQL tools cannot be offered: the switch off, or no connection of <c>sql.json</c> offered (2026-09-23). Pinned.</summary>
    public const string SqlOffSuffix = "SQL tools is off or no connection of sql.json is offered";

    /// <summary>The tail of the Oracle group while the Oracle tools cannot be offered: the switch off, or no connection of <c>oracle.json</c> offered (2026-09-30). Pinned.</summary>
    public const string OracleOffSuffix = "Oracle tools is off or no connection of oracle.json is offered";

    /// <summary>The tail of the MySQL group while the MySQL tools cannot be offered (2026-09-30). Pinned.</summary>
    public const string MySqlOffSuffix = "MySQL tools is off or no connection of mysql.json is offered";

    /// <summary>After the SQLite heading while the group is not offered (2026-10-04). Pinned.</summary>
    public const string SqliteOffSuffix = "SQLite tools is off, or no database of sqlite.json is offered and SQLite sandbox files is off";

    /// <summary>After the Postgres heading while the group is not offered (2026-10-04). Pinned.</summary>
    public const string PostgresOffSuffix = "PostgreSQL tools is off or no connection of postgres.json is offered";

    /// <summary>The tail of the UNC group while the UNC tools cannot be offered: the switch off, or no share of <c>unc.json</c> offered (2026-09-30). Pinned.</summary>
    public const string UncOffSuffix = "UNC tools is off or no share of unc.json is offered";

    /// <summary>The tail of the Docker group while its tools cannot be offered: the switch off (2026-10-02). Pinned.</summary>
    public const string DockerOffSuffix = "Docker tools is off";

    /// <summary>The tail of the ComfyUI group while the image tools cannot be offered (2026-09-24). Pinned.</summary>
    /// <summary>Why the advisor group is not offered (2026-09-27). Pinned.</summary>
    public const string ClaudeAdvisorOffSuffix = "Claude CLI advisor tool is off";

    public const string ComfyOffSuffix = "ComfyUI tools is off, no ComfyUI URL is set or no workflow is offered";

    /// <summary>The tail of the Home Assistant group while its tools cannot be offered (2026-09-28). Pinned.</summary>
    public const string HomeAssistantOffSuffix = "Home Assistant tools is off, or no Home Assistant URL or API key is set";

    /// <summary>Why the print group is not offered (2026-09-28).</summary>
    public const string PrintOffSuffix = "Print tools is off";

    /// <summary>After the Camera heading while <c>camera_capture</c> is not offered (2026-10-02). Pinned.</summary>
    public const string CameraOffSuffix = "Camera tool is off, there is no pane or camera support, or the model reads no pictures";

    /// <summary>After the Screen heading while the screen tools are not offered (2026-10-04). Pinned.</summary>
    public const string ScreenOffSuffix = "Screen capture tool is off, there is no pane or screen capture support, or the model reads no pictures";

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

    /// <summary>The tail of the memory heading while the list is in the prompt because <c>recall_memory</c> is switched off on <c>/tools</c> (2026-09-19): <c>recall_memory is off in /tools</c>. Pinned.</summary>
    public static string ToolOff(string tool) => $"{tool} is off in /tools";

    // ── The Prompt tab ──────────────────────────────────────────────────────

    /// <summary>The sections in prompt order, nothing else (2026-09-26): the voice directive only while it is included. Pinned.</summary>
    public static IReadOnlyList<SystemPromptSection> PromptSections(SystemPromptFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var sections = new List<SystemPromptSection>(6);

        bool custom = !string.IsNullOrWhiteSpace(facts.Persona);
        string persona = custom ? facts.Persona!.Trim() : Assistant.DefaultPersona;
        sections.Add(new(
            "Persona",
            custom ? $"{PersonaFile.FileName} ({persona.Length.ToString(CultureInfo.InvariantCulture)} chars)" : "default",
            persona));

        bool customRules = !string.IsNullOrWhiteSpace(facts.OperatingRules);
        string defaultLabel = !facts.ToolsEnabled ? $"default ({ToolsOffSuffix})" : !facts.FilesEnabled ? $"default ({FilesOffSuffix})" : "default";
        string rules = customRules ? facts.OperatingRules!.Trim() : Assistant.DefaultRules(facts.Markdown, facts.ToolsEnabled, facts.FilesEnabled, delete: !facts.Off(DeleteTool.ToolName), mcp: facts.Mcp, timers: facts.Timers, git: facts.Git, shell: facts.Shell, bridge: facts.Bridge, police: facts.Police, obsidian: facts.Obsidian, obsidianDelete: facts.ObsidianDelete, sql: facts.Sql, native: facts.Native, advisor: facts.Advisor, homeAssistant: facts.HomeAssistant, oracle: facts.Oracle, mysql: facts.MySql, unc: facts.Unc, uncFetch: facts.Unc && facts.UncFetch, uncWrite: facts.Unc && facts.UncWrite, docker: facts.Docker, dockerWrite: facts.Docker && facts.DockerWrite, help: facts.Help, sqlite: facts.Sqlite, postgres: facts.Postgres);
        sections.Add(new(
            "Operating rules",
            customRules ? $"{OperataFile.FileName} ({rules.Length.ToString(CultureInfo.InvariantCulture)} chars)" : defaultLabel,
            rules));

        // The project notes (2026-09-16): after the rules, tools or not, while Agent skills is on.
        if (!facts.SkillsEnabled)
        {
            sections.Add(new(ProjectNotesLabel, $"off ({SkillsOffSuffix})", ""));
        }
        else if (!facts.ProjectFile)
        {
            sections.Add(new(ProjectNotesLabel, $"off ({ProjectFileOffSuffix})", ""));
        }
        else if (facts.Project is { } project)
        {
            sections.Add(new(ProjectNotesLabel, $"{project.FileName} ({project.Text.Length.ToString(CultureInfo.InvariantCulture)} chars)", Assistant.ProjectNotesSection(project)));
        }
        else
        {
            sections.Add(new(ProjectNotesLabel, $"none ({string.Join(" / ", ProjectFile.FileNames)} not in the working directory)", ""));
        }

        string remembered = facts.Memories.Count == 1 ? "1 fact remembered" : $"{facts.Memories.Count.ToString(CultureInfo.InvariantCulture)} facts remembered";
        if (facts.Memory && facts.ToolsEnabled && facts.Recall)
        {
            // The list rides the opening call (2026-09-17): the section is the directive alone, the facts are under Also sent.
            sections.Add(new(MemoryLabel, $"on, directive (the list rides the opening {RecallMemoryTool.ToolName} call)", MemoryPrompt.Section(facts.Memories, tools: true)));
        }
        else if (facts.Memory && facts.ToolsEnabled)
        {
            // recall_memory switched off on /tools (2026-09-19): nothing can carry the list, so it rides the prompt as under LLM offer tools off.
            sections.Add(new(MemoryLabel, $"on, {remembered} (in the prompt: {ToolOff(RecallMemoryTool.ToolName)})", MemoryPrompt.Section(facts.Memories, tools: false)));
        }
        else if (facts.Memory)
        {
            sections.Add(new(MemoryLabel, $"on, {remembered}", MemoryPrompt.Section(facts.Memories, tools: false)));
        }
        else
        {
            sections.Add(new(MemoryLabel, "off, not included", ""));
        }

        // The skills block (2026-09-16): after the memory section, only with tools to load one.
        if (!facts.SkillsEnabled)
        {
            sections.Add(new(SkillsLabel, $"off ({SkillsOffSuffix})", ""));
        }
        else if (!facts.ToolsEnabled)
        {
            sections.Add(new(SkillsLabel, $"not included ({ToolsOffSuffix})", ""));
        }
        else
        {
            var skills = facts.Skills ?? [];
            int external = skills.Count(s => s.Scope == SkillScope.External);
            string count = skills.Count == 0 ? "none installed"
                : (skills.Count == 1 ? "1 skill" : $"{skills.Count.ToString(CultureInfo.InvariantCulture)} skills") + (external > 0 ? $" ({external.ToString(CultureInfo.InvariantCulture)} external)" : "");
            sections.Add(new(SkillsLabel, $"on, {count}", SkillsPrompt.Section(skills)));
        }

        // Plan mode's directive only while planning (2026-09-26), where the prompt carries it: after the skills, ahead of the voice.
        if (facts.ToolsEnabled && !string.IsNullOrWhiteSpace(facts.PlanDirective))
        {
            sections.Add(new(PlanModeLabel, PlanModeStatus, facts.PlanDirective.Trim()));
        }

        // The voice directive only while it is in the prompt (2026-09-26): a "not included" heading on every silent turn was noise.
        // Since 2026-10-03 there is no default one, so it is in the prompt only when vocalia.md has text.
        if (facts.TtsOutput && facts.SpeechReady && !string.IsNullOrWhiteSpace(facts.VoiceDirective))
        {
            string voice = facts.VoiceDirective.Trim();
            sections.Add(new("Voice directive", $"{VocaliaFile.FileName} ({voice.Length.ToString(CultureInfo.InvariantCulture)} chars), included (speech output on, TTS ready), always last", voice));
        }

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
            timers: facts.Timers,
            git: facts.Git,
            shell: facts.Shell,
            bridge: facts.Bridge,
            police: facts.Police,
            obsidian: facts.Obsidian,
            obsidianDelete: facts.ObsidianDelete,
            sql: facts.Sql,
            native: facts.Native,
            plan: facts.ToolsEnabled ? facts.PlanDirective : null,
            advisor: facts.Advisor,
            homeAssistant: facts.HomeAssistant,
            oracle: facts.Oracle,
            mysql: facts.MySql,
            unc: facts.Unc,
            uncFetch: facts.Unc && facts.UncFetch,
            uncWrite: facts.Unc && facts.UncWrite,
            docker: facts.Docker,
            dockerWrite: facts.Docker && facts.DockerWrite,
            help: facts.Help,
            sqlite: facts.Sqlite,
            postgres: facts.Postgres);
    }

    /// <summary>The Prompt tab's heading over plan mode's directive (2026-09-26): <see cref="PlanModeLabel"/> and <see cref="PlanModeStatus"/> as <see cref="SystemPromptSection.Heading"/> joins them. Pinned.</summary>
    public const string PlanModeHeading = PlanModeLabel + " — " + PlanModeStatus;

    /// <summary>The label of plan mode's section, the rule's (2026-10-03).</summary>
    public const string PlanModeLabel = "Plan mode";

    /// <summary>The status of plan mode's section, dim on the rule after <see cref="PlanModeLabel"/> (2026-10-03).</summary>
    public const string PlanModeStatus = "on, read-only tools until the plan is approved";

    private const string ProjectNotesLabel = "Project notes";
    private const string MemoryLabel = "Memory";
    private const string SkillsLabel = "Skills";

    /// <summary>
    /// The Prompt tab: every section's heading as a rule (<see cref="SectionRule"/>, 2026-10-03, the user's ask: the
    /// Tools tab's look) — its label, then its status dim between two runs of the rule — and, when it has one, its text
    /// <see cref="BodyIndent"/> cells in under it.
    /// </summary>
    public static IRenderable PromptTab(SystemPromptFacts facts)
    {
        var rows = new List<IRenderable>();
        foreach (var section in PromptSections(facts))
        {
            rows.Add(new SectionRule(SectionRule.Markup(section.Label, note: section.Status)));
            if (section.Body.Length > 0)
            {
                rows.Add(new Indented(new Text(section.Body, Theme.Body), BodyIndent));
            }

            // A space, not an empty Text: Rows adds a line break only after a child that rendered something.
            rows.Add(new Text(" "));
        }

        return new Rows(rows);
    }

    /// <summary>The Prompt tab as plain lines, for a console without the pane.</summary>
    public static IEnumerable<string> PromptLines(SystemPromptFacts facts)
    {
        foreach (var section in PromptSections(facts))
        {
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
    /// The groups in alphabetical order (<see cref="SortedGroups"/>, 2026-10-04; the comments below that place a group "after" another
    /// tell the order the turn offers them, the list's until then); the memory group is marked when memory is off,
    /// the web group when the web tools are, the files group when the file tools are, the questions
    /// group when the setting <c>Ask user</c> is (else when there is no pane), the sessions group when
    /// the setting <c>Session tool</c> is, every group when the setting <c>LLM offer tools</c> is (the group's
    /// own reason first). Since 2026-09-19 a tool switched off by name (<paramref name="disabled"/>,
    /// <c>/tools</c>) is noted on its row (<see cref="ToolGroup.ToolNotes"/>) and the group's name counts
    /// what is left — <c>Files (12 of 14)</c>; <paramref name="skillInstalled"/> false notes <c>load_skill</c>
    /// as dropped (the <c>/tools</c> list passes it; <c>/sys</c> keeps the plain names). Pinned.
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
        IReadOnlyList<AIFunction>? shell = null,
        bool shellEnabled = true,
        bool codeAvailable = true,
        IReadOnlyList<AIFunction>? obsidian = null,
        bool obsidianEnabled = true,
        IReadOnlyList<AIFunction>? sql = null,
        bool sqlEnabled = true,
        IReadOnlyList<AIFunction>? comfy = null,
        bool comfyEnabled = true,
        IReadOnlyList<AIFunction>? advisor = null,
        bool advisorEnabled = true,
        IReadOnlyList<AIFunction>? homeAssistant = null,
        bool homeAssistantEnabled = true,
        IReadOnlyList<AIFunction>? print = null,
        bool printEnabled = true,
        IReadOnlyList<AIFunction>? oracle = null,
        bool oracleEnabled = true,
        IReadOnlyList<AIFunction>? mysql = null,
        bool mysqlEnabled = true,
        IReadOnlyList<AIFunction>? unc = null,
        bool uncEnabled = true,
        IReadOnlyList<AIFunction>? docker = null,
        bool dockerEnabled = true,
        IReadOnlyList<AIFunction>? camera = null,
        bool cameraEnabled = true,
        IReadOnlyList<AIFunction>? help = null,
        IReadOnlyList<AIFunction>? screen = null,
        bool screenEnabled = true,
        IReadOnlyList<AIFunction>? sqlite = null,
        bool sqliteEnabled = true,
        IReadOnlyList<AIFunction>? postgres = null,
        bool postgresEnabled = true)
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
        var groups = new List<ToolGroup>(8)
        {
            Group("Clock", clock, standing, toolsEnabled, null, disabled),
            Group("Timers", timers, standing, toolsEnabled, null, disabled),
        };
        if (help is not null)
        {
            // The app's own manual (2026-10-02): standing after the timers as the turn offers it, no switch of its own; /tools switches it off by name.
            groups.Add(Group(HelpText.GroupTitle, help, standing, toolsEnabled, null, disabled));
        }

        groups.Add(Group("Files", files, filesNote, filesEnabled && toolsEnabled, SettingsField.FileTools, disabled));
        if (git is not null)
        {
            // The git tools (2026-09-20): right after the file tools, the sandbox's two groups together; offered while the setting GitLib tools says so.
            string gitNote = !gitEnabled ? NotOffered(GitOffSuffix) : standing;
            groups.Add(Group(ToolsText.GitTabTitle, git, gitNote, gitEnabled && toolsEnabled, SettingsField.GitLibTools, disabled));   // "Git", the tab's word ("Git (native)" 2026-09-21 to 2026-09-29)
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

        if (oracle is not null)
        {
            // The Oracle tools (2026-09-30): right after the SQL tools, the two database groups together; offered while the setting Oracle tools is on and oracle.json holds a connection.
            string oracleNote = !oracleEnabled ? NotOffered(OracleOffSuffix) : standing;
            groups.Add(Group(ToolsText.OracleTabTitle, oracle, oracleNote, oracleEnabled && toolsEnabled, SettingsField.OracleTools, disabled));
        }

        if (mysql is not null)
        {
            // The MySQL tools (2026-09-30): after the Oracle tools, the database groups together.
            string mysqlNote = !mysqlEnabled ? NotOffered(MySqlOffSuffix) : standing;
            groups.Add(Group(ToolsText.MySqlTabTitle, mysql, mysqlNote, mysqlEnabled && toolsEnabled, SettingsField.MySqlTools, disabled));
        }

        if (sqlite is not null)
        {
            // The SQLite tools (2026-10-04): after MySQL; offered while SQLite tools is on with a database to open.
            string sqliteNote = !sqliteEnabled ? NotOffered(SqliteOffSuffix) : standing;
            groups.Add(Group(ToolsText.SqliteTabTitle, sqlite, sqliteNote, sqliteEnabled && toolsEnabled, SettingsField.SqliteTools, disabled));
        }

        if (postgres is not null)
        {
            // The PostgreSQL tools (2026-10-04): after SQLite; offered while PostgreSQL tools is on with a connection offered.
            string postgresNote = !postgresEnabled ? NotOffered(PostgresOffSuffix) : standing;
            groups.Add(Group(ToolsText.PostgresTabTitle, postgres, postgresNote, postgresEnabled && toolsEnabled, SettingsField.PostgresTools, disabled));
        }

        if (unc is not null)
        {
            // The UNC tools (2026-09-30): after the database groups; offered while the setting UNC tools is on and unc.json holds a share.
            string uncNote = !uncEnabled ? NotOffered(UncOffSuffix) : standing;
            groups.Add(Group(ToolsText.UncTabTitle, unc, uncNote, uncEnabled && toolsEnabled, SettingsField.UncTools, disabled));
        }

        if (docker is not null)
        {
            // The Docker tools (2026-10-02): after the UNC tools, the outside places together; offered while the setting Docker tools is on.
            string dockerNote = !dockerEnabled ? NotOffered(DockerOffSuffix) : standing;
            groups.Add(Group(ToolsText.DockerTabTitle, docker, dockerNote, dockerEnabled && toolsEnabled, SettingsField.DockerTools, disabled));
        }

        if (comfy is not null)
        {
            // The image tools (2026-09-24): after the SQL tools; offered while ComfyUI tools is on, a URL is set and a workflow is installed.
            string comfyNote = !comfyEnabled ? NotOffered(ComfyOffSuffix) : standing;
            groups.Add(Group(ToolsText.ComfyTabTitle, comfy, comfyNote, comfyEnabled && toolsEnabled, SettingsField.ComfyTools, disabled));
        }

        if (homeAssistant is not null)
        {
            // The Home Assistant tools (2026-09-28): after the image tools; offered while Home Assistant tools is on and a URL and a token are set.
            string homeNote = !homeAssistantEnabled ? NotOffered(HomeAssistantOffSuffix) : standing;
            groups.Add(Group(ToolsText.HomeAssistantGroupTitle, homeAssistant, homeNote, homeAssistantEnabled && toolsEnabled, SettingsField.HomeAssistantTools, disabled));
        }

        if (print is not null)
        {
            // The print tools (2026-09-28): after the Home Assistant tools; offered while Print tools is on.
            string printNote = !printEnabled ? NotOffered(PrintOffSuffix) : standing;
            groups.Add(Group(ToolsText.PrintTabTitle, print, printNote, printEnabled && toolsEnabled, SettingsField.PrintTools, disabled));
        }

        if (camera is not null)
        {
            // The camera (2026-10-02): after the print tools; offered while Camera tool is on, with the pane and a model that reads pictures.
            string cameraNote = !cameraEnabled ? NotOffered(CameraOffSuffix) : standing;
            groups.Add(Group(ToolsText.CameraTabTitle, camera, cameraNote, cameraEnabled && toolsEnabled, SettingsField.CameraTools, disabled));
        }

        if (screen is not null)
        {
            // The screen (2026-10-04): after the camera; offered while Screen capture tool is on, with the pane and a model that reads pictures.
            string screenNote = !screenEnabled ? NotOffered(ScreenOffSuffix) : standing;
            groups.Add(Group(ToolsText.ScreenTabTitle, screen, screenNote, screenEnabled && toolsEnabled, SettingsField.ScreenTools, disabled));
        }

        if (advisor is not null)
        {
            // The advisor (2026-09-27): after the image tools; offered while the setting Claude CLI advisor tool is on.
            string advisorNote = !advisorEnabled ? NotOffered(ClaudeAdvisorOffSuffix) : standing;
            groups.Add(Group(ToolsText.ClaudeCliTabTitle, advisor, advisorNote, advisorEnabled && toolsEnabled, SettingsField.ClaudeCliAdvisor, disabled));
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

        return SortedGroups(groups);
    }

    /// <summary>
    /// The groups in alphabetical order by their bare label (2026-10-04, the user's ask; the order the turn offers them until then), any case;
    /// the sort is stable, so several MCP servers keep their own order on a tie. One source for <c>/tools</c>' Offered tab, <c>/sys</c>' Tools
    /// tab, the <c>$</c> completion and the Botchat limited tools checklist. What a turn sends is built elsewhere and keeps its own order.
    /// </summary>
    public static IReadOnlyList<ToolGroup> SortedGroups(IEnumerable<ToolGroup> groups) =>
        groups.OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase).ToList();

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
            Count = GroupCount(left, tools.Count),
            ToolNotes = notes ?? new Dictionary<string, string>(0, StringComparer.Ordinal),
            Switch = @switch,
            Label = name,
        };
    }

    /// <summary>
    /// The groups cut to what the next turn sends, for <c>/sys</c>' Tools tab (2026-09-26, the user's call): a
    /// group not offered goes, header and all; in an offered one, a tool with a note (switched off on <c>/tools</c>,
    /// <c>load_skill</c> with no skill …) goes; a group left with no tool
    /// goes too. The names recount what is left — <c>Files (13)</c>, never <c>13 of 14</c> — since the tab now
    /// names only what is sent. <c>/tools</c>' Offered tab keeps the whole list: it is where a tool is switched back on.
    /// </summary>
    public static IReadOnlyList<ToolGroup> OfferedOnly(IReadOnlyList<ToolGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var kept = new List<ToolGroup>(groups.Count);
        foreach (var group in groups)
        {
            if (!group.Offered)
            {
                continue;
            }

            var tools = group.Tools.Where(t => group.Offers(t.Name)).ToList();
            if (tools.Count > 0)
            {
                kept.Add(new ToolGroup(GroupName(group.Label, tools.Count, tools.Count), "", tools, true) { Switch = group.Switch, Label = group.Label, Count = GroupCount(tools.Count, tools.Count) });
            }
        }

        return kept;
    }

    /// <summary>The Tools tab's one line when the next turn offers no tool at all (2026-09-26): every group cut by <see cref="OfferedOnly"/>. Pinned.</summary>
    public const string NoToolsOffered = "No tools offered";

    /// <summary>The empty tab's line: <see cref="NoToolsOffered"/>, with the reason while <c>LLM offer tools</c> is off. Pinned.</summary>
    public static string NoToolsLine(bool toolsEnabled) => toolsEnabled ? NoToolsOffered : $"{NoToolsOffered} ({ToolsOffSuffix})";

    /// <summary>The group's name and count: <c>Files (14)</c> with every tool offered, <c>Files (12 of 14)</c> with some switched off by name (2026-09-19). Pinned.</summary>
    public static string GroupName(string name, int offered, int total) => $"{name} ({GroupCount(offered, total)})";

    /// <summary>The count alone: <c>14</c> with every tool offered, <c>12 of 14</c> with some switched off by name — <see cref="GroupName"/>'s brackets and the heading rule's tail (2026-10-03). Pinned.</summary>
    public static string GroupCount(int offered, int total) =>
        offered == total
            ? total.ToString(CultureInfo.InvariantCulture)
            : $"{offered.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A group's note while it is not offered: <c>not offered: memory is off</c>. Pinned.</summary>
    public static string NotOffered(string why) => $"not offered: {why}";

    /// <summary>
    /// The Tools tab: per group a heading rule (<see cref="SectionRule"/>, 2026-10-03, the user's call: the bare
    /// violet name looked poor) — its label in the section style whether the group is offered or not (a heading never
    /// dims, later on 2026-09-20, the user's call; the /tools rule), its count and its note (if any) dim — then its tools
    /// as name and description, <see cref="BodyIndent"/> cells in under the rule, all dim when the group is not offered; a
    /// one-space row parts the groups. A grid per group, every one's name column as wide as the longest tool name of them
    /// all, so the description column sits at the same place under every heading (a grid per group sized to its own
    /// longest name made the descriptions jump between groups, 2026-09-16; one grid for every group until 2026-10-03,
    /// its name column widened by the longest heading).
    /// </summary>
    public static IRenderable ToolsTab(IReadOnlyList<ToolGroup> groups, bool toolsEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0)
        {
            // Nothing offered (2026-09-26): an empty grid renders no line at all, so the tab says so.
            return new Text(NoToolsLine(toolsEnabled), Theme.DimText);
        }

        int names = groups.SelectMany(g => g.Tools).Select(t => TextCells.Width(t.Name)).DefaultIfEmpty(0).Max();
        var rows = new List<IRenderable>(groups.Count * 3);
        for (int i = 0; i < groups.Count; i++)
        {
            if (i > 0)
            {
                // A one-space line, as the Commands tab: an empty Text renders no line and would collapse.
                rows.Add(new Text(" "));
            }

            var group = groups[i];
            rows.Add(new SectionRule(SectionRule.Markup(group.Label, group.Count, group.Note)));
            if (group.Tools.Count == 0)
            {
                continue;
            }

            var grid = new Grid()
                .AddColumn(new GridColumn().NoWrap().Width(names).PadRight(SlashCommands.HelpColumnGap))
                .AddColumn(new GridColumn().PadRight(0));
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

            rows.Add(new Indented(grid, BodyIndent));
        }

        return new Rows(rows);
    }

    /// <summary>The cells a section's lines sit in under its heading rule on the Prompt and Tools tabs (2026-10-03): the menu's pointer gutter, so the tabs line up with <c>/tools</c>' rows.</summary>
    public const int BodyIndent = 2;

    /// <summary>The name column of the plain tool lines (and of the <c>/tools</c> list).</summary>
    public const int ToolNameWidth = 22;

    /// <summary>The Tools tab as plain lines, for a console without the pane.</summary>
    public static IEnumerable<string> ToolLines(IReadOnlyList<ToolGroup> groups, bool toolsEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0)
        {
            yield return NoToolsLine(toolsEnabled);
            yield break;
        }

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
