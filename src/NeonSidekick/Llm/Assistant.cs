using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;

namespace NeonSidekick.Llm;

/// <summary>
/// The single-agent turn loop: stream one model response, run whatever tools it asked for, feed
/// the results back, repeat — at most <see cref="MaxToolIterations"/> times — yielding text as it
/// arrives. Hand-written rather than <c>FunctionInvokingChatClient</c> so every branch is visible
/// and the loop stays free of reflection.
///
/// <para>Rules this loop keeps:</para>
/// <list type="bullet">
/// <item>Every tool result goes back as a <see cref="FunctionResultContent"/> carrying the
/// originating <c>CallId</c> — on the unknown-tool, bad-arguments and exception paths too. A call
/// without a result makes the server reject the next request.</item>
/// <item>Calls are collected from <em>every</em> assistant message in the response, not the
/// first. A reasoning model routinely thinks in one message and calls in the next.</item>
/// <item>The turn budget is a deadline checked at the loop boundary, never a linked
/// <see cref="CancellationTokenSource"/>: cancelling the token would surface as
/// <see cref="OperationCanceledException"/>, which means barge-in on the voice path.</item>
/// <item>An empty tool list is sent as <c>null</c>, not <c>[]</c>; several local servers reject
/// an empty <c>tools</c> array.</item>
/// <item>A picture a tool fetched (<see cref="Tools.ViewImageTool"/>, a <see cref="Tools.ToolImageResult"/>)
/// goes in a user-role <em>carrier</em> message right after the iteration's tool results
/// (<see cref="ConversationHistory.AddToolImages"/>): an OpenAI-format tool message is text only,
/// and the adapter drops any image part in one. The result text itself still carries the <c>CallId</c>.</item>
/// </list>
/// </summary>
public sealed class Assistant
{
    /// <summary>
    /// The identity sentence: the one part of the prompt <c>persona.md</c> (<see cref="PersonaFile"/>)
    /// replaces. Short on purpose; the voice-response directive (M3+) is appended <em>last</em>
    /// so it wins against anything here.
    /// </summary>
    public const string DefaultPersona = "You are Neon, a friendly and concise terminal sidekick.";

    /// <summary>
    /// The one operating rule that names no tool: the whole of the default rules while the
    /// setting <c>LLM offer tools</c> is off (<see cref="PlainTextRule"/>), the first
    /// sentence of <see cref="OperatingRules"/> otherwise. Pinned.
    /// </summary>
    public const string PlainTextRule = "Reply in plain text: no markdown headings, tables or code fences unless the user asks for code.";

    /// <summary>
    /// The sentence that takes <see cref="PlainTextRule"/>'s place while the transcript renders
    /// Markdown (the setting <c>Transcript markdown</c> on, the pane on the screen, the turn not
    /// spoken; 2026-09-16): light Markdown the styled transcript shows well, no headings or tables
    /// unasked. A custom <c>operata.md</c> stands verbatim either way. Pinned.
    /// </summary>
    public const string MarkdownRule = "Reply in light Markdown: **bold** for emphasis, a bulleted or numbered list where one helps, and a fenced code block with its language for code; no headings or tables unless the user asks for them.";

    /// <summary>The reply-format sentence for a turn: <see cref="MarkdownRule"/> or <see cref="PlainTextRule"/>.</summary>
    public static string TextRule(bool markdown) => markdown ? MarkdownRule : PlainTextRule;

    /// <summary>
    /// The tool, clock and timer sentences of the default rules — everything of
    /// <see cref="OperatingRulesWithoutFiles"/> after its reply-format sentence:
    /// <see cref="ToolRulesWithoutTimers"/> and <see cref="TimerRule"/> (byte-identical to the one const
    /// they were until 2026-09-20). Pinned.
    /// </summary>
    public const string ToolRules = ToolRulesWithoutTimers + " " + TimerRule;

    /// <summary>
    /// The tool and clock sentences of <see cref="ToolRules"/> without its timer sentence: the default
    /// rules while no timer tool is offered (2026-09-20) — headless, which has no timers since nothing
    /// could ring the alert, and the pane with all three timer rows switched off on <c>/tools</c> (the
    /// group emptied drops its rule, as every other group does). Pinned.
    /// </summary>
    public const string ToolRulesWithoutTimers =
        "Use a tool when it helps; otherwise answer directly. " +
        "You do not know the current date or time; call " + NeonSidekick.Llm.Tools.GetCurrentTimeTool.ToolName + " when a question depends on it, " +
        "and use " + NeonSidekick.Llm.Tools.ShiftDateTool.ToolName + " or " + NeonSidekick.Llm.Tools.DaysBetweenTool.ToolName + " for calendar arithmetic instead of counting yourself.";

    /// <summary>
    /// The timer sentence of <see cref="ToolRules"/>: in the default rules while any timer tool is offered,
    /// gone with them (<see cref="ToolRulesWithoutTimers"/>); a custom <c>operata.md</c> stands verbatim
    /// either way. Pinned.
    /// </summary>
    public const string TimerRule =
        "For a countdown, use " + NeonSidekick.Llm.Tools.StartTimerTool.ToolName + ", " + NeonSidekick.Llm.Tools.StopTimerTool.ToolName + " and " + NeonSidekick.Llm.Tools.ListTimersTool.ToolName + "; never guess what is left on a timer.";

    /// <summary>
    /// The default operating rules for a turn without the file tools (the setting <c>File tools</c>
    /// off, 2026-09-15): the plain-text, tool, clock and timer sentences — <see cref="OperatingRules"/>
    /// less <see cref="FileRule"/>. Nothing stands in for the working directory. Pinned.
    /// </summary>
    public const string OperatingRulesWithoutFiles = PlainTextRule + " " + ToolRules;

    /// <summary>
    /// The two sentences of <see cref="OperatingRules"/> that name the file tools and the working
    /// directory: in the default rules while the setting <c>File tools</c> is on (the file tools
    /// offered), gone with them; a custom <c>operata.md</c> stands verbatim either way. Pinned.
    /// </summary>
    public const string FileRule =
        "The user's working directory — also called the cwd, the current directory or the current working directory — is a folder on this computer where you may read, search, write and organise files with the file tools " +
        "(" + NeonSidekick.Llm.Tools.GetWorkingDirectoryTool.ToolName + " gives its path); every path you pass is relative to it and nothing outside it is reachable; " +
        NeonSidekick.Llm.Tools.DeleteTool.ToolName + " only moves to its .trash folder and " + NeonSidekick.Llm.Tools.RestoreTool.ToolName + " brings things back. " +
        "To look at a picture (png, jpg, gif, webp, bmp) in the working directory call " + NeonSidekick.Llm.Tools.ViewImageTool.ToolName + " (several at once with " + NeonSidekick.Llm.Tools.ViewImageTool.PathsArgument + "); " + NeonSidekick.Llm.Tools.ReadFileTool.ToolName + " cannot read one.";

    /// <summary>
    /// <see cref="FileRule"/> with its <c>delete</c> / <c>restore</c> clause telling the truth under
    /// <c>File safe edits</c> off (2026-09-20, the user's call): <c>delete</c> removes for good, a folder with
    /// everything in it — and, since later still that day (the user's ask), neither <c>restore</c> nor
    /// <c>.trash</c> is named: the tool is not offered while the setting is off (<c>ChatScreen.FileToolsFor</c>),
    /// so the prompt never mentions a trash — nor, since 2026-09-21 (the user's ask), the setting itself:
    /// told <c>File safe edits is off</c>, the model reasoned about a switch it cannot reach. The default
    /// rules while <c>delete</c> is offered and the setting is off; <see cref="OperatingRules"/> stays byte-identical. Pinned.
    /// </summary>
    public const string FileRuleDeleteInPlace =
        "The user's working directory — also called the cwd, the current directory or the current working directory — is a folder on this computer where you may read, search, write and organise files with the file tools " +
        "(" + NeonSidekick.Llm.Tools.GetWorkingDirectoryTool.ToolName + " gives its path); every path you pass is relative to it and nothing outside it is reachable; " +
        NeonSidekick.Llm.Tools.DeleteTool.ToolName + " removes a file or a folder for good, with everything in it. " +
        "To look at a picture (png, jpg, gif, webp, bmp) in the working directory call " + NeonSidekick.Llm.Tools.ViewImageTool.ToolName + " (several at once with " + NeonSidekick.Llm.Tools.ViewImageTool.PathsArgument + "); " + NeonSidekick.Llm.Tools.ReadFileTool.ToolName + " cannot read one.";

    /// <summary>
    /// <see cref="FileRule"/> without its <c>delete</c> / <c>restore</c> clause: the default rules while
    /// <c>delete</c> is switched off on <c>/tools</c> (2026-09-20 — off in a fresh profile until later on
    /// 2026-09-21, when the user asked for it on out of the box), the <see cref="DownloadRule"/> shape; <see cref="OperatingRules"/> stays
    /// byte-identical. Pinned.
    /// </summary>
    public const string FileRuleWithoutDelete =
        "The user's working directory — also called the cwd, the current directory or the current working directory — is a folder on this computer where you may read, search, write and organise files with the file tools " +
        "(" + NeonSidekick.Llm.Tools.GetWorkingDirectoryTool.ToolName + " gives its path); every path you pass is relative to it and nothing outside it is reachable. " +
        "To look at a picture (png, jpg, gif, webp, bmp) in the working directory call " + NeonSidekick.Llm.Tools.ViewImageTool.ToolName + " (several at once with " + NeonSidekick.Llm.Tools.ViewImageTool.PathsArgument + "); " + NeonSidekick.Llm.Tools.ReadFileTool.ToolName + " cannot read one.";

    /// <summary>
    /// The operating rules that follow the persona, default or custom: <see cref="OperatingRulesWithoutFiles"/>
    /// and <see cref="FileRule"/> (byte-identical to the one const they were until 2026-09-15). The date
    /// sentence is load-bearing: without it a small model states a date from its training data.
    /// </summary>
    public const string OperatingRules = OperatingRulesWithoutFiles + " " + FileRule;

    /// <summary>
    /// The sentence the default rules gain while the setting <c>Web tools</c> is on (the web tools
    /// offered): appended after <see cref="OperatingRules"/> by <see cref="SystemPrompt(bool, IReadOnlyList{string}?, string?, string?, string?, bool, bool, bool, AskLimits?)"/>;
    /// a custom <c>operata.md</c> stands verbatim and names the tools itself or not at all. Pinned.
    /// </summary>
    public const string WebRule =
        "For anything on the web — current facts, news, prices, documentation, a page the user names — search with " + NeonSidekick.Llm.Tools.WebSearchTool.ToolName +
        " and read a page with " + NeonSidekick.Llm.Tools.WebFetchTool.ToolName + " (" + NeonSidekick.Llm.Tools.WebFetchTool.OffsetArgument + " continues a long one); say what you looked at. " +
        "To open a page for the user in their browser — when they ask to open, show or see a link rather than have it read out — call " + NeonSidekick.Llm.Tools.OpenUrlTool.ToolName + ".";

    /// <summary>
    /// The sentence the default rules gain while the web tools AND the file tools are offered
    /// (2026-09-18, <c>download_file</c> rides the web list only then): appended after <see cref="WebRule"/>
    /// by <see cref="DefaultRules"/>; a custom <c>operata.md</c> stands verbatim. Pinned.
    /// </summary>
    public const string DownloadRule =
        "To save a file or a picture from the web into the working directory call " + NeonSidekick.Llm.Tools.DownloadFileTool.ToolName +
        " with its URL (and a " + NeonSidekick.Llm.Tools.DownloadFileTool.PathArgument + " when the user names one); it saves without reading — " +
        NeonSidekick.Llm.Tools.ViewImageTool.ToolName + " or " + NeonSidekick.Llm.Tools.ReadFileTool.ToolName + " look at the result.";

    /// <summary>
    /// The sentence the default rules gain while the git tools are offered (the setting <c>Git native tools</c> on,
    /// 2026-09-20): appended after <see cref="DownloadRule"/>, with the sandbox's sentences, by <see cref="DefaultRules"/>.
    /// It names nine of the tools a fresh profile offers and neither of the two that lose work (<c>git_discard</c>,
    /// on out of the box since 2026-09-23, and <c>git_delete</c>, off by name in <c>ToolsDisabled</c>), so no variant is needed whichever is off; a custom
    /// <c>operata.md</c> stands verbatim. Pinned.
    /// </summary>
    public const string GitRule =
        "The working directory may be a git repository (or hold one): " + NeonSidekick.Llm.Tools.GitStatusTool.ToolName + " shows its state, " +
        NeonSidekick.Llm.Tools.GitLogTool.ToolName + " its history, " + NeonSidekick.Llm.Tools.GitShowTool.ToolName + " a commit or a file at a commit, " +
        NeonSidekick.Llm.Tools.GitDiffTool.ToolName + " the changes, " + NeonSidekick.Llm.Tools.GitBlameTool.ToolName + " who wrote a line; " +
        NeonSidekick.Llm.Tools.GitStageTool.ToolName + ", " + NeonSidekick.Llm.Tools.GitCommitTool.ToolName + ", " + NeonSidekick.Llm.Tools.GitBranchTool.ToolName + " and " + NeonSidekick.Llm.Tools.GitStashTool.ToolName +
        " change it — commit only what the user asked to commit, with the message they gave or a short imperative one, and never remove or overwrite work the user did not name.";

    /// <summary>
    /// The sentence the default rules gain while the vault tools are offered (the setting <c>Obsidian tools</c> on and
    /// <c>Obsidian vault</c> naming a vault, 2026-09-22): appended after the shell sentence by <see cref="DefaultRules"/>.
    /// It says the vault is the user's notes, that a note is named as Obsidian names it, that a rename goes through
    /// <c>vault_move</c> (the file tools would break every link to it), and that the app's own folder is not the
    /// model's; a custom <c>operata.md</c> stands verbatim. Pinned.
    /// </summary>
    public const string ObsidianRule =
        "The user's Obsidian vault holds their notes: name a note as Obsidian does (its name, a [[wikilink]] or its path in the vault); " +
        NeonSidekick.Llm.Tools.VaultSearchTool.ToolName + " and " + NeonSidekick.Llm.Tools.VaultListTool.ToolName + " find notes, " +
        NeonSidekick.Llm.Tools.VaultReadTool.ToolName + " reads one, " + NeonSidekick.Llm.Tools.VaultLinksTool.ToolName + " shows its links and backlinks, " +
        NeonSidekick.Llm.Tools.VaultDailyTool.ToolName + " opens a day's daily note; " +
        NeonSidekick.Llm.Tools.VaultWriteTool.ToolName + " and " + NeonSidekick.Llm.Tools.VaultPropertiesTool.ToolName + " change notes — write Obsidian Markdown ([[links]], #tags) — and " +
        NeonSidekick.Llm.Tools.VaultMoveTool.ToolName + " renames or moves one with its links kept; the vault's .obsidian folder is the app's own.";

    /// <summary>
    /// The sentence <see cref="ObsidianRule"/> gains while <c>vault_delete</c> is offered (later on 2026-09-22: the setting
    /// <c>Obsidian allow delete (.trash)</c> on, on by default since 2026-09-23, and the tool not switched off): what the delete does and that it
    /// waits for the user's word. Appended right after the vault sentence by <see cref="DefaultRules"/>. Pinned.
    /// </summary>
    public const string ObsidianDeleteRule =
        NeonSidekick.Llm.Tools.VaultDeleteTool.ToolName + " moves a note or attachment into the vault's .trash, where Obsidian can restore it; use it only when the user asks for a deletion.";

    /// <summary>
    /// The sentence the default rules gain while the SQL tools are offered (the setting <c>SQL tools</c> on and a
    /// connection in <c>sql.json</c>, 2026-09-23): appended after the vault sentences by <see cref="DefaultRules"/>.
    /// It says the dialect is T-SQL, that the tools only read, the order a question is worked in (find the table or the
    /// column, describe it, follow the keys, then one bounded SELECT; the indexes for a performance question — both
    /// named later that day) and that values go in as parameters. Pinned.
    /// </summary>
    public const string SqlRule =
        "The SQL tools read SQL Server (T-SQL: TOP, not LIMIT) on the user's named connections and never change data: " +
        NeonSidekick.Llm.Tools.SqlConnectionsTool.ToolName + " lists the connections and " + NeonSidekick.Llm.Tools.SqlDatabasesTool.ToolName + " a server's databases; " +
        NeonSidekick.Llm.Tools.SqlTablesTool.ToolName + " finds a table and " + NeonSidekick.Llm.Tools.SqlColumnsTool.ToolName + " a column, " +
        NeonSidekick.Llm.Tools.SqlDescribeTool.ToolName + " shows a table's columns, keys and constraints, " + NeonSidekick.Llm.Tools.SqlRelationshipsTool.ToolName + " the joins and " +
        NeonSidekick.Llm.Tools.SqlIndexesTool.ToolName + " the indexes with their use — look before you query, never guess a column; " +
        NeonSidekick.Llm.Tools.SqlQueryTool.ToolName + " runs one SELECT per call, kept small with WHERE and TOP, values bound as @name through params.";

    /// <summary>
    /// The sentence the default rules gain while the shell tools are offered (the setting <c>Shell command
    /// policy</c> not <c>off</c>, 2026-09-21): appended after <see cref="GitRule"/> by <see cref="DefaultRules"/>. It
    /// says what the tool is for, that the sandbox is only where a command starts, that the user stands between
    /// the call and the shell, that a denial is final, and (phase B) how a background job and the process tool
    /// go together; a custom <c>operata.md</c> stands verbatim. Its last sentence, on <c>execute_code</c>, says the
    /// script can call the tools through <c>neon_tools</c> — only while the setting <c>Shell tool bridge</c> is on;
    /// off (later on 2026-09-21) the rules carry <see cref="ShellRuleWithoutBridge"/>, which does not, so the prompt
    /// never promises a module the run does not write. Since 2026-09-22 (the user's ask) the head follows the setting
    /// <c>Shell police outside paths</c> too: on (the default), it says the shell may only name paths under the working
    /// directory (<see cref="ShellRuleHeadPoliced"/>); off, it says only that a command starts there (<see cref="ShellRuleHeadUnpoliced"/>,
    /// the <c>…Unpoliced</c> variants) — until that day the head said a command "can reach the whole computer", and
    /// neither variant says so now, so the model does not try to leave unless asked. Pinned.
    /// </summary>
    public const string ShellRule = ShellRuleHeadPoliced + ShellRuleBridgeTail;

    /// <summary><see cref="ShellRule"/> with the bridge off: the same head, and <c>execute_code</c> runs a script that does everything itself. Pinned.</summary>
    public const string ShellRuleWithoutBridge = ShellRuleHeadPoliced + ShellRulePlainTail;

    /// <summary><see cref="ShellRule"/> with the setting <c>Shell police outside paths</c> off (2026-09-22): the head says a command starts in the working directory and no more. Pinned.</summary>
    public const string ShellRuleUnpoliced = ShellRuleHeadUnpoliced + ShellRuleBridgeTail;

    /// <summary><see cref="ShellRuleWithoutBridge"/> with the police off (2026-09-22): neither the bridge nor the confinement is named. Pinned.</summary>
    public const string ShellRuleWithoutBridgeUnpoliced = ShellRuleHeadUnpoliced + ShellRulePlainTail;

    /// <summary>What every shell rule opens with: <c>run_command</c> and its two picks.</summary>
    private const string ShellRuleOpening =
        "To run a program, a build, a test or a script the user asks for, call " + NeonSidekick.Llm.Tools.RunCommandTool.ToolName + " with the command line " +
        "(" + NeonSidekick.Llm.Tools.RunCommandTool.ShellArgument + " picks powershell, cmd or bash when the user's default will not do; " + NeonSidekick.Llm.Tools.RunCommandTool.WorkdirArgument + " a folder under the working directory); ";

    /// <summary>The head with the police on: the shell stays under the working directory, and a refused path is final like a denied command.</summary>
    private const string ShellRuleHeadPoliced = ShellRuleOpening +
        "it runs in the working directory and may only name paths under it (relative, or absolute under it), and the user approves each command before it runs and may deny it — " +
        "never retry or work around a denied or refused command, and say what you ran. " + ShellRuleProcess;

    /// <summary>The head with the police off: a command starts in the working directory; nothing said about where it may reach.</summary>
    private const string ShellRuleHeadUnpoliced = ShellRuleOpening +
        "it starts in the working directory, and the user approves each command before it runs and may deny it — " +
        "never retry or work around a denied command, and say what you ran. " + ShellRuleProcess;

    /// <summary>What every head closes with: the background job and <c>process</c>.</summary>
    private const string ShellRuleProcess =
        "For a server or a long job pass " + NeonSidekick.Llm.Tools.RunCommandTool.BackgroundArgument + " and use " + NeonSidekick.Llm.Tools.ProcessTool.ToolName + " to poll, read, wait for, write to or kill it; " +
        "with " + NeonSidekick.Llm.Tools.RunCommandTool.NotifyArgument + " you are told at your next turn when it exits. ";

    /// <summary>The <c>execute_code</c> sentence with the bridge on.</summary>
    private const string ShellRuleBridgeTail =
        "For a task with several steps or many tool calls, " + NeonSidekick.Llm.Tools.ExecuteCodeTool.ToolName + " runs a python, node or powershell script that can call these same tools through its neon_tools module and returns what it printed.";

    /// <summary>The <c>execute_code</c> sentence with the bridge off.</summary>
    private const string ShellRulePlainTail =
        "For a task with several steps, " + NeonSidekick.Llm.Tools.ExecuteCodeTool.ToolName + " runs a python, node or powershell script and returns what it printed.";

    /// <summary>The shell rule for a turn: the bridge picks the <c>execute_code</c> sentence, the police (2026-09-22) the head.</summary>
    public static string ShellRuleFor(bool bridge, bool police) => (bridge, police) switch
    {
        (true, true) => ShellRule,
        (false, true) => ShellRuleWithoutBridge,
        (true, false) => ShellRuleUnpoliced,
        (false, false) => ShellRuleWithoutBridgeUnpoliced,
    };

    /// <summary>
    /// The sentence the default rules gain while <c>ask_user</c> is offered (the setting <c>Ask user</c>
    /// on, the bottom pane on): appended after <see cref="WebRule"/> by
    /// <see cref="SystemPrompt(bool, IReadOnlyList{string}?, string?, string?, string?, bool, bool, bool, AskLimits?)"/>;
    /// a custom <c>operata.md</c> stands verbatim. Quotes the caps the tool the model sees works under
    /// (<see cref="AskLimits"/>, the two Ask-tab rows; 2026-09-15). Pinned.
    /// </summary>
    public static string AskRule(AskLimits limits) =>
        "When you need the user to decide between a few possibilities — a choice, a preference, a clarification with a short list of answers — call " + AskUserTool.ToolName +
        " with the options and wait for the result instead of guessing or asking in prose; up to " + limits.MaxQuestions.ToString(CultureInfo.InvariantCulture) +
        " questions in one call, " + AskUserTool.MinOptions.ToString(CultureInfo.InvariantCulture) + " to " + limits.MaxChoices.ToString(CultureInfo.InvariantCulture) +
        " options each, and the user can type their own answer. " +
        "If the result says the questions were not answered, go on without them.";

    /// <summary>
    /// The sentence the default rules gain while <c>session_manager</c> is offered (the setting
    /// <c>Session tool</c> on, 2026-09-18): appended by <see cref="DefaultRules"/> after the ask
    /// sentence (last until <see cref="McpRule"/>, 2026-09-20); a custom <c>operata.md</c> stands verbatim. Pinned.
    /// </summary>
    public const string SessionRule =
        "Your earlier conversations with the user are stored: when they ask what was said, decided or done before, or refer to something you cannot see in this conversation, call " +
        NeonSidekick.Llm.Tools.SessionManagerTool.ToolName + " — " + NeonSidekick.Llm.Tools.SessionManagerTool.SearchAction + " with the words they remember, " +
        NeonSidekick.Llm.Tools.SessionManagerTool.ListAction + " for the newest, then " + NeonSidekick.Llm.Tools.SessionManagerTool.ReadAction + " a session by its id for the turns; never guess at them. " +
        "The user restores, renames or removes a session with /sessions, not you.";

    /// <summary>
    /// The sentence the default rules gain while any MCP server's tools are offered (2026-09-20):
    /// appended after <see cref="SessionRule"/>, so the model knows the prefixed names are external and
    /// described by their servers, not by these rules. Pinned.
    /// </summary>
    public const string McpRule =
        "Tools named <server>" + NeonSidekick.Mcp.McpToolName.Separator + "<tool> belong to external MCP servers the user connected; each does what its own description says — " +
        "read it before calling, pass exactly the arguments its schema names, and answer from its result.";

    /// <summary>The system prompt with the default persona: one paragraph, the persona and the rules.</summary>
    public const string DefaultSystemPrompt = DefaultPersona + " " + OperatingRules;

    /// <summary>
    /// Appended <em>last</em> to the system prompt while speech output is in force, so it wins
    /// against everything above it.
    ///
    /// <para><b>The first sentence exempts the tool channel</b>, and it is not padding. The rest
    /// forbids markdown, code and file paths — a precise description of what a tool call looks
    /// like — and a small model obeys the strongest instruction in the prompt. Without the
    /// exemption the failure is a spoken answer <em>invented</em> instead of looked up.</para>
    /// </summary>
    public const string VoiceDirective = ToolChannelExemption + " " + VoiceDirectiveWithoutTools;

    /// <summary>The first sentence of <see cref="VoiceDirective"/>: the tool channel is exempt. Only with tools to exempt.</summary>
    public const string ToolChannelExemption =
        "The rules that follow apply to what you say to the user and never to tool calls, which are not spoken; " +
        "call tools exactly as instructed above whenever a question concerns real state.";

    /// <summary>
    /// The default voice directive for a turn that offers no tools (<c>LLM offer tools</c> off):
    /// <see cref="VoiceDirective"/> without its tool-channel sentence, which would name something the
    /// turn does not have. Pinned.
    /// </summary>
    public const string VoiceDirectiveWithoutTools =
        "Your reply is shown on screen and also read aloud by a text-to-speech engine, so keep it short and in plain spoken English. " +
        "Do not use markdown, headings, bullet points, numbered lists, tables, code blocks or emoji, " +
        "and do not recite file paths, command lines or code unless the user asked for that exact text.";

    /// <summary>The system prompt for a turn: the persona alone, or with <see cref="VoiceDirective"/> last.</summary>
    public static string SystemPrompt(bool speechOutput) => SystemPrompt(speechOutput, null);

    /// <summary>
    /// The default operating rules for a turn, the one composition behind <see cref="SystemPrompt(bool, IReadOnlyList{string}?, string?, string?, string?, bool, bool, bool, AskLimits?, ProjectNotes?, IReadOnlyList{Skills.Skill}?, bool)"/>
    /// and <c>/sys</c>: the reply-format sentence (<see cref="TextRule"/>), then with <paramref name="tools"/>
    /// the <see cref="ToolRules"/>, <see cref="FileRule"/> with <paramref name="files"/>, <see cref="WebRule"/>
    /// with <paramref name="web"/> (and <see cref="DownloadRule"/> with both, unless <paramref name="download"/> is false —
    /// <c>download_file</c> switched off by name on <c>/tools</c>, 2026-09-19), <see cref="AskRule"/> with <paramref name="ask"/>,
    /// <see cref="SessionRule"/> with <paramref name="sessions"/> and, last, <see cref="McpRule"/> with <paramref name="mcp"/> (2026-09-20). The file rule is
    /// <see cref="FileRuleWithoutDelete"/> with <paramref name="delete"/> false and <see cref="FileRuleDeleteInPlace"/> with <paramref name="safeEdits"/> false
    /// (<c>File safe edits</c> off while <c>delete</c> is offered, 2026-09-20 — neither names <c>restore</c> or <c>.trash</c>, since the
    /// tool is not offered then); the tool rules are
    /// <see cref="ToolRulesWithoutTimers"/> with <paramref name="timers"/> false (no timer tool offered — headless, or the
    /// Timers group emptied on <c>/tools</c>, 2026-09-20); <see cref="ShellRule"/> rides after the git sentence with
    /// <paramref name="shell"/> (the shell tools offered: <c>Shell command policy</c> not off, 2026-09-21), as
    /// <see cref="ShellRuleWithoutBridge"/> unless <paramref name="bridge"/> (the setting <c>Shell tool bridge</c>, off by
    /// default, later that day), and as the <c>…Unpoliced</c> variant with <paramref name="police"/> false (the setting <c>Shell police
    /// outside paths</c> off, 2026-09-22; <see cref="ShellRuleFor"/>). <see cref="ObsidianDeleteRule"/> follows <see cref="ObsidianRule"/>
    /// with <paramref name="obsidianDelete"/> (<c>vault_delete</c> offered, later on 2026-09-22); <see cref="SqlRule"/> after them with <paramref name="sql"/> (2026-09-23). With <paramref name="markdown"/> false it is <see cref="OperatingRules"/> and its variants byte for byte.
    /// </summary>
    public static string DefaultRules(bool markdown, bool tools, bool files = true, bool web = false, AskLimits? ask = null, bool sessions = false, bool download = true, bool delete = true, bool mcp = false, bool safeEdits = true, bool timers = true, bool git = false, bool shell = false, bool bridge = false, bool police = true, bool obsidian = false, bool obsidianDelete = false, bool sql = false) =>
        tools
            ? TextRule(markdown) + " " + (timers ? ToolRules : ToolRulesWithoutTimers) + (files ? " " + (delete ? (safeEdits ? FileRule : FileRuleDeleteInPlace) : FileRuleWithoutDelete) : "") + (web ? " " + WebRule : "") + (web && files && download ? " " + DownloadRule : "") + (git ? " " + GitRule : "") + (shell ? " " + ShellRuleFor(bridge, police) : "") + (obsidian ? " " + ObsidianRule + (obsidianDelete ? " " + ObsidianDeleteRule : "") : "") + (sql ? " " + SqlRule : "") + (ask is { } limits ? " " + AskRule(limits) : "") + (sessions ? " " + SessionRule : "") + (mcp ? " " + McpRule : "")
            : TextRule(markdown);

    /// <summary>
    /// The system prompt for a turn, in this order: the persona (<paramref name="persona"/> from
    /// <c>persona.md</c>, or <see cref="DefaultPersona"/> when it is null or blank) followed by the
    /// operating rules (<paramref name="operatingRules"/> from <c>operata.md</c>, or
    /// <see cref="OperatingRules"/> when null or blank) — <see cref="DefaultSystemPrompt"/>'s single
    /// paragraph when both are the default, else each as its own block; the memory section
    /// (<see cref="MemoryPrompt.Section"/>) when <paramref name="memories"/> is not null — null
    /// means memory is off, an empty list means on with nothing stored yet; the list itself is in the
    /// section only without tools, with them it rides the opening <c>recall_memory</c> pair
    /// (<see cref="OpeningMemoryCallId"/>); and the voice directive
    /// (<paramref name="voiceDirective"/> from <c>vocalia.md</c>, or <see cref="VoiceDirective"/> when
    /// null or blank) last when <paramref name="speechOutput"/> is on, so it still wins — the file
    /// changes what is appended, never whether. With <paramref name="tools"/> false (the setting
    /// <c>LLM offer tools</c> off) every <em>default</em> swaps for its tool-free form
    /// (<see cref="PlainTextRule"/>, <see cref="MemoryPrompt.DirectiveWithoutTool"/>,
    /// <see cref="VoiceDirectiveWithoutTools"/>); a custom file stands verbatim either way. With
    /// <paramref name="web"/> true (the web tools offered) the default rules end with <see cref="WebRule"/>;
    /// with <paramref name="files"/> false (the setting <c>File tools</c> off, the file tools not offered)
    /// they lose <see cref="FileRule"/> (<see cref="OperatingRulesWithoutFiles"/>); with <paramref name="ask"/>
    /// set (<c>ask_user</c> offered under those caps: the setting <c>Ask user</c> on, the bottom pane on) they end with
    /// <see cref="AskRule"/>, after the web sentence. With <paramref name="project"/> set (the working
    /// directory's <c>NEON.md</c> / <c>AGENTS.md</c>, <see cref="ProjectFile"/>) its notes follow the
    /// rules as their own block (<see cref="ProjectNotesSection"/>), tools or not; with
    /// <paramref name="skills"/> not null (the setting <c>Agent skills</c> on and tools offered) the skills
    /// block (<see cref="Skills.SkillsPrompt.Section"/>) follows the memory section — null means off,
    /// an empty list on with none installed; it is dropped with <paramref name="tools"/> false, since
    /// nothing could load one. With <paramref name="markdown"/> true (the transcript styles the reply:
    /// the setting <c>Transcript markdown</c> on, the pane on, the turn not spoken) the default rules
    /// open with <see cref="MarkdownRule"/> in place of <see cref="PlainTextRule"/>. With <paramref name="sessions"/>
    /// true (<c>session_manager</c> offered: the setting <c>Session tool</c> on) they close with <see cref="SessionRule"/>.
    /// Since 2026-09-19 a tool switched off by name on <c>/tools</c> leaves the rules that name it alone (a call
    /// answers <c>Error: unknown tool</c>), with two exceptions where the prompt would otherwise lie: <paramref name="download"/>
    /// false drops <see cref="DownloadRule"/>, and <paramref name="recall"/> false (<c>recall_memory</c> off while memory
    /// is on) puts the list into the memory section as under <paramref name="tools"/> false, since no opening call carries it;
    /// the third (2026-09-20) is a whole group: <paramref name="timers"/> false (no timer tool offered — headless, or the
    /// three switched off) drops <see cref="TimerRule"/>.
    /// </summary>
    public static string SystemPrompt(bool speechOutput, IReadOnlyList<string>? memories, string? persona = null, string? operatingRules = null, string? voiceDirective = null, bool tools = true, bool web = false, bool files = true, AskLimits? ask = null, ProjectNotes? project = null, IReadOnlyList<Skills.Skill>? skills = null, bool markdown = false, bool sessions = false, bool download = true, bool recall = true, bool delete = true, bool mcp = false, bool safeEdits = true, bool timers = true, bool git = false, bool shell = false, bool bridge = false, bool police = true, bool obsidian = false, bool obsidianDelete = false, bool sql = false)
    {
        bool customPersona = !string.IsNullOrWhiteSpace(persona);
        bool customRules = !string.IsNullOrWhiteSpace(operatingRules);
        string defaultRules = DefaultRules(markdown, tools, files, web, ask, sessions, download, delete, mcp, safeEdits, timers, git, shell, bridge, police, obsidian, obsidianDelete, sql);
        var sb = new StringBuilder(!customPersona && !customRules
            ? DefaultPersona + " " + defaultRules
            : (customPersona ? persona!.Trim() : DefaultPersona) + "\n\n" + (customRules ? operatingRules!.Trim() : defaultRules));
        if (project is not null)
        {
            sb.Append("\n\n").Append(ProjectNotesSection(project));
        }

        if (memories is not null)
        {
            sb.Append("\n\n").Append(MemoryPrompt.Section(memories, tools && recall));
        }

        if (tools && skills is not null)
        {
            sb.Append("\n\n").Append(Skills.SkillsPrompt.Section(skills));
        }

        if (speechOutput)
        {
            sb.Append("\n\n").Append(string.IsNullOrWhiteSpace(voiceDirective) ? (tools ? VoiceDirective : VoiceDirectiveWithoutTools) : voiceDirective.Trim());
        }

        return sb.ToString();
    }

    /// <summary>The project notes block: <see cref="ProjectNotesHeading"/> naming the file, a newline, the text. Pinned.</summary>
    public static string ProjectNotesSection(ProjectNotes project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ProjectNotesHeading(project.FileName) + "\n" + project.Text;
    }

    /// <summary><c>Notes for the working directory (from NEON.md):</c>. Pinned.</summary>
    public static string ProjectNotesHeading(string fileName) => "Notes for the working directory (from " + fileName + "):";

    /// <summary>The default for <see cref="MaxToolIterations"/>: what the setting <c>LLM max tool iterations</c> starts at — the cap itself since 2026-09-15 (1000 before).</summary>
    public const int DefaultMaxToolIterations = 10000;

    private int _maxToolIterations = DefaultMaxToolIterations;

    /// <summary>
    /// Model round trips allowed per turn before the loop gives up — a tool call is one, and a
    /// picture (<see cref="Tools.ViewImageTool"/>) costs one, since it arrives in the message after
    /// the result. Set per turn by the shell from the setting, like <see cref="Tools"/>; the turn
    /// budget (<see cref="LlmTimeouts.Turn"/>) stays the guard against a runaway. Never below 1.
    /// </summary>
    public int MaxToolIterations
    {
        get => _maxToolIterations;
        set => _maxToolIterations = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "At least one tool iteration.");
    }

    /// <summary>
    /// Whether a call to an offered tool that the model wrote out as text is caught (<see cref="TextToolCallFilter"/>): kept out of
    /// the reply shown and spoken, and run as a real call (2026-09-25, the user's report: a <c>/botchat</c> bot wrote
    /// <c>generate_image(prompt=…)</c> into its line and no picture came). Off by default and on for botchat alone — the user's
    /// call: in the main chat a reply that shows a call as an example would run it.
    /// </summary>
    public bool TextToolCalls { get; set; }

    /// <summary>
    /// The context window the tool loop measures a request's usage against (<see cref="WindowTokens"/>,
    /// the figure behind <c>/usage</c>), the share of it that trips the guard (<see cref="Percent"/>,
    /// the setting <c>LLM auto compact (%)</c>; 0 = never) and what the loop then does (<see cref="Mode"/>,
    /// the setting <c>LLM tool compact type</c>).
    /// </summary>
    public sealed record TurnContextGuard(int WindowTokens, int Percent, ToolCompactMode Mode, bool ProtectSkills = true)
    {
        /// <summary>Whether the guard acts at all: a share above zero and a mode that does something.</summary>
        public bool Acts => Percent > 0 && Mode != ToolCompactMode.Nothing;

        /// <summary>The whole-number share of the window <paramref name="usage"/> takes.</summary>
        public int PercentOf(TokenUsage usage) => (int)(usage.Total * 100L / WindowTokens);

        /// <summary>Whether <paramref name="usage"/> is at or past the share.</summary>
        public bool Tripped(TokenUsage usage) => Acts && usage.Total > 0 && usage.Total * 100L >= (long)WindowTokens * Percent;
    }

    /// <summary>
    /// The mid-turn context guard, set per turn by the shell like <see cref="MaxToolIterations"/>;
    /// null while the window is unknown. The automatic compact checks only at the top of a message,
    /// and one message can walk the context to the ceiling by itself (sixty tool round trips took
    /// a chef's five-recipe request from 7k to 129k tokens of a 151k window, 2026-09-15). After
    /// each request that reported usage at or past the share: <see cref="ToolCompactMode.Prune"/>
    /// stubs this turn's older tool results (<see cref="ConversationCompactor.PruneRecent"/>) and
    /// carries on; <see cref="ToolCompactMode.Stop"/> ends the turn with <see cref="TurnStoppedNotice"/>
    /// after the iteration's results are in, so no call is left unanswered. Kept non-null under
    /// <see cref="ToolCompactMode.Nothing"/> too, so a timeout can name the share.
    /// </summary>
    public TurnContextGuard? ContextGuard { get; set; }

    /// <summary>
    /// What a line where only tool results were pruned opens with (2026-09-19, the user's pick):
    /// the guard's <see cref="TurnPrunedNotice"/> and a prune-only compact's notice (<c>App.CompactionText</c>
    /// reads it; a summary wears the clamp). The scissors U+2702 with the variation selector — bare
    /// it is a one-cell text glyph; with U+FE0F Windows Terminal draws the colour emoji two cells wide,
    /// and <c>UI.TextCells</c> counts the selector as one after a narrow character for that reason —
    /// and a space. Pinned.
    /// </summary>
    public const string PruneGlyph = "✂️ ";

    /// <summary>What the guard's stop line opens with (2026-09-19, the user's pick): the stop sign U+1F6D1, emoji-presentation by itself, and a space. Pinned.</summary>
    public const string StopGlyph = "🛑 ";

    /// <summary>The transcript line after the guard pruned: <c>(✂️ context at 85%: pruned 23 tool results from this turn)</c>. Pinned.</summary>
    public static string TurnPrunedNotice(int percent, int pruned) =>
        "(" + PruneGlyph + "context at " + percent.ToString(CultureInfo.InvariantCulture) + "%: pruned " + pruned.ToString(CultureInfo.InvariantCulture) + (pruned == 1 ? " tool result" : " tool results") + " from this turn)";

    /// <summary>The error line when the guard stopped the turn: <c>🛑 Stopped at 85% …</c>. Pinned.</summary>
    public static string TurnStoppedNotice(int percent) =>
        StopGlyph + "Stopped at " + percent.ToString(CultureInfo.InvariantCulture) + "% of the context window (LLM tool compact type is stop); /compact or /clear before continuing.";

    /// <summary>The stable part of System.ClientModel's network-timeout message, the one the SDK's <c>NetworkTimeout</c> (the request timeout) throws with.</summary>
    public const string NetworkTimeoutMarker = "exceeded the configured timeout";

    /// <summary>
    /// Whether <paramref name="ex"/> is the request timeout: an <see cref="OperationCanceledException"/>
    /// anywhere in the chain carrying <see cref="NetworkTimeoutMarker"/>. The SDK's own sentence names
    /// <c>ClientPipelineOptions.NetworkTimeout</c>, a knob the operator does not have.
    /// </summary>
    public static bool LooksLikeRequestTimeout(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                current = aggregate.InnerExceptions[0];
            }

            if (current is OperationCanceledException && current.Message.Contains(NetworkTimeoutMarker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The request timeout in the operator's words: <c>The server sent nothing for 75s (LLM request
    /// timeout (s), LLM tab)</c>, and with the last request's share of the window known, <c> — the
    /// context was at 85% of the window; a model near its limit can run away (/compact, /clear)</c>.
    /// The idle-read timeout is the right guard for a runaway the server keeps generating into a
    /// buffered tool call; raising it only lets the runaway run longer. Pinned.
    /// </summary>
    public static string RequestTimeoutExplanation(TimeSpan request, int? contextPercent)
    {
        string text = "The server sent nothing for " + LlmTimeouts.Format(request) + " (LLM request timeout (s), LLM tab)";
        if (contextPercent is { } percent)
        {
            text += " — the context was at " + percent.ToString(CultureInfo.InvariantCulture) + "% of the window; a model near its limit can run away (/compact, /clear)";
        }

        return text;
    }

    /// <summary>
    /// <see cref="Explain"/>, except that the request timeout reads as <see cref="RequestTimeoutExplanation"/>
    /// over this assistant's ceiling, with <paramref name="lastUsage"/>'s share of the window when
    /// the guard knows it (the loop passes the last request that reported; a summariser passes none).
    /// </summary>
    public string ExplainFailure(Exception ex, TokenUsage? lastUsage = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (!LooksLikeRequestTimeout(ex))
        {
            return Explain(ex);
        }

        int? percent = ContextGuard is { } guard && lastUsage is { Total: > 0 } usage ? guard.PercentOf(usage) : null;
        return RequestTimeoutExplanation(_timeouts.Request, percent);
    }

    /// <summary>
    /// The <c>CallId</c>s of the opening calls (<see cref="OpeningCalls"/>): the clock's, the
    /// working directory's and the memory's (2026-09-17). Nine alphanumeric characters each on
    /// purpose: Mistral-family chat templates validate a tool call id as exactly that, and every
    /// other server takes any string. One conversation holds each once.
    /// </summary>
    public const string OpeningClockCallId = "neonclock";
    public const string OpeningCwdCallId = "neoncwdir";
    public const string OpeningMemoryCallId = "neonmemry";

    private static readonly IReadOnlySet<string> OpeningCallIds = new HashSet<string>(StringComparer.Ordinal) { OpeningClockCallId, OpeningCwdCallId, OpeningMemoryCallId };

    /// <summary>
    /// The prefix of a pending call's id (2026-09-21): <c>neonp</c> and the last four characters of the
    /// process id (<c>neonp2a1b</c> for <c>proc_3f2a1b</c>) — nine alphanumerics, the opening calls' rule.
    /// </summary>
    public const string PendingCallIdPrefix = "neonp";

    /// <summary>The call id a seeded poll for <paramref name="sessionId"/> carries: <see cref="PendingCallIdPrefix"/> and the id's last four characters.</summary>
    public static string PendingCallId(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        string tail = sessionId.Length >= 4 ? sessionId[^4..] : sessionId.PadLeft(4, '0');
        return PendingCallIdPrefix + tail;
    }

    /// <summary>Whether <paramref name="callId"/> is one of the opening calls' (<see cref="OpeningClockCallId"/>, <see cref="OpeningCwdCallId"/>, <see cref="OpeningMemoryCallId"/>) or a pending call's (<see cref="PendingCallIdPrefix"/>).</summary>
    public static bool IsOpeningCallId(string callId) =>
        OpeningCallIds.Contains(callId) || (callId.Length == 9 && callId.StartsWith(PendingCallIdPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Appended to the <c>Model error:</c> notice when the server refused a request that carried
    /// tools with the Mistral-family template's sentence (<see cref="LooksLikeToolRoleRejection"/>):
    /// the model has no tool role, and the setting that talks to it anyway is named. Pinned.
    /// </summary>
    public const string ToolRoleHint = " — this model's chat template accepts no tool messages; turn the setting LLM offer tools off (the LLM tab of /settings) to talk to it without tools";

    /// <summary>
    /// Whether a failure's explanation is a chat template rejecting the <c>tool</c> role:
    /// <c>Only user, system and assistant roles are supported!</c> (Mistral-family templates; SGLang
    /// and vLLM answer HTTP 400 with the template's own words). Case-insensitive on the stable part.
    /// </summary>
    public static bool LooksLikeToolRoleRejection(string explained)
    {
        ArgumentNullException.ThrowIfNull(explained);
        return explained.Contains("roles are supported", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What <see cref="SummarizeAsync"/> throws when the server answered with no text. Pinned.</summary>
    public const string EmptySummaryError = "The server returned an empty summary.";

    /// <summary>
    /// One opening call: the tool and the fixed id its call/result pair carries; the arguments are
    /// empty for the three openers (a <c>/skill</c> activation carried the skill's name here from
    /// 2026-09-16 until later on 2026-09-18) and the poll's for a pending call (2026-09-21).
    /// </summary>
    public sealed record OpeningCall(AIFunction Tool, string CallId, IReadOnlyDictionary<string, object?>? Arguments = null);

    /// <summary>Logged (Info, so <c>--log</c> only) when a reply carried a <c>&lt;/think&gt;</c> with no opener: the server's parser missed, and the text before it was thinking.</summary>
    public const string ThinkingLeakedNote = "The server streamed thinking as content (a </think> with no <think>); the tag was dropped.";

    /// <summary>Logged (Debug) when a whole think block arrived as content: the server has no reasoning parser.</summary>
    public const string ThinkingBlockNote = "The server streamed a <think> block as content; it was dropped.";

    private const string Category = "Llm";

    private readonly IChatClient _client;
    private readonly ConversationHistory _history;
    private readonly LlmTimeouts _timeouts;
    private IReadOnlyList<AIFunction> _tools;
    private readonly TimeProvider _time;
    private readonly ReasoningEffort? _reasoning;

    /// <param name="client">The seam; production passes <see cref="OpenAICompatibleChatClient"/>, tests a fake.</param>
    /// <param name="history">The transcript the model sees; shared with whoever renders it.</param>
    /// <param name="timeouts">Already interlocked by <see cref="LlmTimeouts.Resolve"/>.</param>
    /// <param name="tools">Hand-written <see cref="AIFunction"/> subclasses with explicit schemas; empty in sprint 1.</param>
    /// <param name="time">The clock behind the turn deadline; tests advance a fake.</param>
    /// <param name="reasoning">The effort to ask for on every request (<see cref="ReasoningLevel.Resolve"/>), or null to leave the server to its default.</param>
    public Assistant(
        IChatClient client,
        ConversationHistory history,
        LlmTimeouts timeouts,
        IReadOnlyList<AIFunction>? tools = null,
        TimeProvider? time = null,
        ReasoningEffort? reasoning = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _timeouts = timeouts;
        _tools = tools ?? Array.Empty<AIFunction>();
        _time = time ?? TimeProvider.System;
        _reasoning = reasoning;
    }

    public ConversationHistory History => _history;

    /// <summary>
    /// The tools the next turn offers. Settable because the shell decides per turn what a turn may
    /// do (memory on or off), the same way it sets <see cref="ConversationHistory.SystemPrompt"/>;
    /// a turn reads it once, at its start.
    /// </summary>
    public IReadOnlyList<AIFunction> Tools
    {
        get => _tools;
        set => _tools = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The tools the assistant calls itself at the start of a conversation — the clock, the
    /// working directory and the memory, in the app — so the first request already carries their answers. Seeded
    /// in list order by the first turn of a conversation (<see cref="ConversationHistory.TurnCount"/>
    /// was zero) right after the user's message and before the first model request, each as its
    /// own <c>assistant(call) → tool</c> pair: the shape every tool-capable chat template accepts,
    /// where a pair with no user message ahead of it is not. Empty (the default) seeds nothing.
    /// Set per turn by the shell, like <see cref="Tools"/>.
    /// </summary>
    public IReadOnlyList<OpeningCall> OpeningCalls { get; set; } = [];

    /// <summary>
    /// The calls seeded at the start of the <em>next</em> turn, whatever its number (2026-09-21): the
    /// <c>process poll</c> pairs for the background processes that exited with <c>notify</c> since the
    /// last turn, so the model learns of an end without asking. Set per turn by the shell after
    /// <see cref="OpeningCalls"/>, and cleared by <see cref="RunTurnAsync(string, IReadOnlyList{ImageAttachment}, CancellationToken)"/>
    /// once seeded, so a retry of the same message does not repeat them. Same shape as the opening
    /// calls: a real pair, no model request, no iteration spent.
    /// </summary>
    public IReadOnlyList<OpeningCall> PendingCalls { get; set; } = [];

    /// <summary>
    /// True for the events the opening calls raise (a <see cref="TurnEvent.ToolCall"/> or
    /// <see cref="TurnEvent.ToolResult"/> carrying <see cref="OpeningClockCallId"/>,
    /// <see cref="OpeningCwdCallId"/> or <see cref="OpeningMemoryCallId"/>). They come first and at
    /// once, before any model request, so a host shows them ahead of the reply and keeps its
    /// thinking indicator over the wait that follows.
    /// </summary>
    public static bool IsOpeningEvent(TurnEvent evt) => evt switch
    {
        TurnEvent.ToolCall call => IsOpeningCallId(call.CallId),
        TurnEvent.ToolResult result => IsOpeningCallId(result.CallId),
        _ => false,
    };

    /// <summary>The reasoning effort every request asks for, or null when the server decides.</summary>
    public ReasoningEffort? Reasoning => _reasoning;

    /// <summary>
    /// Runs one turn for <paramref name="userText"/>. Text arrives as <see cref="TurnEvent.TextDelta"/>
    /// events; the reply is committed to <see cref="History"/> once the stream completes, or as far
    /// as it got if it was cut short. Server failures become a <see cref="TurnEvent.Notice"/>, never
    /// an exception — the shell must survive a dead server. Cancellation propagates.
    /// </summary>
    public IAsyncEnumerable<TurnEvent> RunTurnAsync(string userText, CancellationToken cancellationToken = default) =>
        RunTurnAsync(userText, [], cancellationToken);

    /// <summary>
    /// <see cref="RunTurnAsync(string, CancellationToken)"/> with pictures beside the text
    /// (<see cref="ConversationHistory.AddUser(string, IReadOnlyList{ImageAttachment})"/>); they
    /// stay in the history with the text, so a later turn can ask about them.
    /// </summary>
    public async IAsyncEnumerable<TurnEvent> RunTurnAsync(
        string userText,
        IReadOnlyList<ImageAttachment> images,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userText);
        ArgumentNullException.ThrowIfNull(images);

        // Decided before the user message lands: the first turn of a conversation opens with the
        // opening calls, a retry of it (the user message already committed) does not; the pending
        // calls (a process's exit) ride any turn, once.
        IReadOnlyList<OpeningCall> opening = _history.TurnCount == 0 ? OpeningCalls : [];
        if (PendingCalls.Count > 0)
        {
            opening = [.. opening, .. PendingCalls];
            PendingCalls = [];
        }

        // The turn's two lines in the log (2026-09-19): the opening one now, the closing one with
        // the tally when the scope ends — the reply's end, a notice, a cancel (the consumer's
        // dispose), a throw — through the using declaration's own finally.
        using var log = new TurnLog(_history.TurnCount + 1, userText, images.Count, opening.Count, _time, cancellationToken);

        // Committed up front: a turn that fails still leaves the question in the transcript.
        _history.AddUser(userText, images);

        foreach (var (tool, callId, seededArguments) in opening)
        {
            // A genuine call/result pair, not a prompt line: the same two events the model's own
            // calls raise, so the transcript shows it the same way, and not a model request, so it
            // costs no iteration and no time on the turn budget.
            var call = new FunctionCallContent(callId, tool.Name, seededArguments is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(seededArguments));
            _history.AddMessage(new ChatMessage(ChatRole.Assistant, [call]));
            string openingJson = SerializeArguments(call.Arguments);
            DiagnosticLog.Debug(Category, ToolCallLogLine(call.Name, openingJson) + OpeningSuffix);
            yield return new TurnEvent.ToolCall(call.Name, call.CallId, openingJson);

            // An opening tool answers with text; a picture from one would have no carrier.
            var (result, _) = await InvokeAsync(tool, call, cancellationToken).ConfigureAwait(false);
            _history.AddToolResults([ResultContent(call, result)]);
            yield return new TurnEvent.ToolResult(call.Name, call.CallId, result);
        }

        var options = new ChatOptions
        {
            // ModelId stays null; the OpenAI adapter ignores it and the ChatClient's model applies.
            Tools = _tools.Count > 0 ? new List<AITool>(_tools) : null,

            // Abstract here; OpenAICompatibleChatClient turns it into the wire fields.
            Reasoning = _reasoning is { } effort ? new ReasoningOptions { Effort = effort } : null,
        };

        long started = _time.GetTimestamp();

        // The last request that reported usage, for the guard and for a timeout's context share.
        TokenUsage? lastUsage = null;

        for (int iteration = 1; iteration <= MaxToolIterations; iteration++)
        {
            if (_time.GetElapsedTime(started) >= _timeouts.Turn)
            {
                DiagnosticLog.Warn(Category, $"Turn budget of {LlmTimeouts.Format(_timeouts.Turn)} exhausted after {iteration - 1} tool iteration(s).");
                log.Ending = TurnStopped;
                yield return new TurnEvent.Notice($"Turn budget of {LlmTimeouts.Format(_timeouts.Turn)} exhausted.", IsError: true);
                yield break;
            }

            var updates = new List<ChatResponseUpdate>();
            var partial = new StringBuilder();
            var filter = new ThinkTagFilter();
            // After the think filter: a call written as text, caught when the turn asks for it and offers tools.
            var written = TextToolCalls && _tools.Count > 0 ? new TextToolCallFilter(_tools.Select(t => (t.Name, ParameterNames(t.JsonSchema)))) : null;
            Exception? failure = null;
            bool cancelled = false;

            // The request's two phases, for TurnEvent.Usage: the wait for the first chunk with
            // content (the prefill; a role-only leading chunk does not end it) and the streaming
            // after it. Stamped on this clock so a test can advance them.
            long sent = _time.GetTimestamp();
            long? first = null;

            // A turn cancelled before its request (2026-09-25: the /botchat ESC that ends the chat can land as the next bot's
            // turn opens) asks nothing: the server never sees a request its caller has already given up on.
            cancellationToken.ThrowIfCancellationRequested();
            var request = _history.BuildRequest();
            log.Requests++;
            DiagnosticLog.Debug(Category, RequestLogLine(iteration, request.Count, _tools.Count, _reasoning));

            // Only MoveNextAsync sits inside the try: C# forbids `yield` inside a try with a catch.
            await using (var stream = _client.GetStreamingResponseAsync(request, options, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    try
                    {
                        if (!await stream.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Includes the transport's own TaskCanceledException when the request
                        // timeout fires with our token untouched: that is an error, not barge-in.
                        failure = ex;
                        break;
                    }

                    var update = stream.Current;
                    updates.Add(update);
                    if (first is null && update.Contents.Count > 0)
                    {
                        first = _time.GetTimestamp();
                    }

                    // The decoder can emit "" for the leading bytes of a multi-byte sequence, and
                    // the filter holds back a possible tag start; either way nothing is yielded.
                    string text = filter.Push(update.Text);
                    if (written is not null && text.Length > 0)
                    {
                        text = written.Push(text);
                    }

                    if (text.Length > 0)
                    {
                        partial.Append(text);
                        log.Chars += text.Length;
                        yield return new TurnEvent.TextDelta(text);
                    }
                }
            }

            long ended = _time.GetTimestamp();

            // A reply that ends in "<" was held back as a possible tag; it is text after all.
            string held = filter.Flush();
            if (written is not null)
            {
                held = (held.Length > 0 ? written.Push(held) : "") + written.Flush();
                if (written.SawBroken)
                {
                    DiagnosticLog.Info(Category, TextCallBrokenNote);
                }
            }

            if (held.Length > 0)
            {
                partial.Append(held);
                log.Chars += held.Length;
                yield return new TurnEvent.TextDelta(held);
            }

            if (filter.SawOrphanClose)
            {
                DiagnosticLog.Info(Category, ThinkingLeakedNote);
            }
            else if (filter.SawBlock)
            {
                DiagnosticLog.Debug(Category, ThinkingBlockNote);
            }

            if (cancelled || failure is not null)
            {
                if (partial.Length > 0)
                {
                    _history.AddAssistant(partial.ToString());
                }

                if (cancelled)
                {
                    DiagnosticLog.Debug(Category, RequestCancelledLogLine(iteration, _time.GetElapsedTime(sent), partial.Length));
                    cancellationToken.ThrowIfCancellationRequested();
                }

                string explained = ExplainFailure(failure!, lastUsage);
                DiagnosticLog.Info(Category, "Model call failed: " + explained);
                if (options.Tools is not null && LooksLikeToolRoleRejection(explained))
                {
                    explained += ToolRoleHint;
                }

                yield return new TurnEvent.Notice("Model error: " + explained, IsError: true);
                yield break;
            }

            var response = updates.ToChatResponse();
            if (filter.SawOrphanClose || filter.SawBlock)
            {
                // The model produced the tags, but they go back as content next time and prime it
                // to repeat the pattern (and a whole block would re-send the thinking as answer text).
                ReplaceText(response.Messages, partial.ToString());
            }

            if (written is not null && (written.Calls.Count > 0 || written.SawBroken))
            {
                // The written calls go into the history as real ones, the reply's text without them, so the model sees what ran.
                ReplaceText(response.Messages, partial.ToString());
                AddWrittenCalls(response.Messages, written.Calls, iteration);
            }

            foreach (var message in response.Messages)
            {
                _history.AddMessage(message);
            }

            // Summed from the updates, not response.Usage, so the count does not depend on how
            // ToChatResponse merges. The spans go on the first report only: a server that sent
            // two would otherwise count its time twice. No report (the server does not send
            // usage, or the stream was cut) means no event, never zeros.
            var usage = TokenUsage.Zero;
            foreach (var reported in updates.SelectMany(u => u.Contents).OfType<UsageContent>())
            {
                long firstAt = first ?? ended;
                usage += usage.IsEmpty
                    ? TokenUsage.From(reported.Details, _time.GetElapsedTime(sent, firstAt), _time.GetElapsedTime(firstAt, ended))
                    : TokenUsage.From(reported.Details, TimeSpan.Zero, TimeSpan.Zero);
            }

            if (!usage.IsEmpty)
            {
                DiagnosticLog.Debug(Category, UsageLogLine(usage));
                lastUsage = usage;
                yield return new TurnEvent.Usage(usage);
            }
            else
            {
                DiagnosticLog.Debug(Category, "No usage reported by the server for this request.");
            }

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Where(c => !c.InformationalOnly)
                .ToList();

            if (calls.Count == 0)
            {
                log.Ending = TurnCompleted;
                yield break;
            }

            var results = new List<FunctionResultContent>(calls.Count);
            var fetched = new List<ImageAttachment>();
            var fetchers = new List<string>();
            log.ToolCalls += calls.Count;
            foreach (var call in calls)
            {
                string argumentsJson = SerializeArguments(call.Arguments);
                // The arguments as the model sent them, for the --log file: the transcript shows a
                // quiet tool's result alone, and a shape a tool refuses is otherwise never seen.
                DiagnosticLog.Debug(Category, ToolCallLogLine(call.Name, argumentsJson));
                yield return new TurnEvent.ToolCall(call.Name, call.CallId, argumentsJson);

                var (result, pictures) = await InvokeToolAsync(_tools, call, cancellationToken).ConfigureAwait(false);
                results.Add(ResultContent(call, result));
                fetched.AddRange(pictures);
                if (pictures.Count > 0 && !fetchers.Contains(call.Name, StringComparer.Ordinal))
                {
                    fetchers.Add(call.Name);
                }

                yield return new TurnEvent.ToolResult(call.Name, call.CallId, result, pictures.Count > 0 ? pictures : null);
            }

            _history.AddToolResults(results);

            // The pictures the iteration fetched, in one carrier after the results (none for none), crediting the tools that fetched them (2026-09-24).
            _history.AddToolImages(fetched, string.Join(" and ", fetchers));

            // The mid-turn guard, after the iteration's results are in: they are the last
            // iteration a prune keeps, and a stop leaves no call unanswered.
            if (ContextGuard is { } guard && guard.Tripped(usage))
            {
                int percent = guard.PercentOf(usage);
                if (guard.Mode == ToolCompactMode.Stop)
                {
                    DiagnosticLog.Info(Category, $"Context at {percent}% of {guard.WindowTokens} tokens after {iteration} tool iteration(s); stopping the turn (LLM tool compact type is stop).");
                    log.Ending = TurnStopped;
                    yield return new TurnEvent.Notice(TurnStoppedNotice(percent), IsError: true);
                    yield break;
                }

                var (shrunk, pruned) = ConversationCompactor.PruneRecent(_history.Messages, guard.ProtectSkills);
                if (pruned > 0)
                {
                    _history.Replace(shrunk);
                    DiagnosticLog.Info(Category, $"Context at {percent}% of {guard.WindowTokens} tokens after {iteration} tool iteration(s); pruned {pruned} tool result(s) from this turn.");
                    yield return new TurnEvent.Notice(TurnPrunedNotice(percent, pruned), IsError: false);
                }
                else
                {
                    DiagnosticLog.Debug(Category, $"Context at {percent}% of {guard.WindowTokens} tokens after {iteration} tool iteration(s); nothing in this turn to prune.");
                }
            }
        }

        DiagnosticLog.Warn(Category, $"Stopped after {MaxToolIterations} tool iterations without a final answer.");
        log.Ending = TurnStopped;
        yield return new TurnEvent.Notice($"Stopped after {MaxToolIterations} tool iterations without a final answer.", IsError: true);
    }

    /// <summary>
    /// The turn's pair of log lines (2026-09-19): <see cref="TurnStartedLogLine"/> at construction,
    /// <see cref="TurnEndedLogLine"/> with the tally on dispose. A using declaration in the iterator,
    /// so the closing line runs whichever way the turn ends — including the consumer disposing a
    /// cancelled enumerator — without a try/finally around the loop. The loop counts into it.
    /// </summary>
    private sealed class TurnLog : IDisposable
    {
        private readonly int _number;
        private readonly TimeProvider _time;
        private readonly long _started;
        private readonly CancellationToken _token;

        public TurnLog(int number, string text, int images, int openingCalls, TimeProvider time, CancellationToken token)
        {
            _number = number;
            _time = time;
            _started = time.GetTimestamp();
            _token = token;
            DiagnosticLog.Info(TurnCategory, TurnStartedLogLine(number, text, images, openingCalls));
        }

        public int Requests { get; set; }

        public int ToolCalls { get; set; }

        public int Chars { get; set; }

        /// <summary>How the turn ended; <see cref="TurnFailed"/> until the loop says otherwise (a model error, a throw), <see cref="TurnCancelled"/> whenever the token was cancelled.</summary>
        public string Ending { get; set; } = TurnFailed;

        public void Dispose() =>
            DiagnosticLog.Info(TurnCategory, TurnEndedLogLine(_number, _time.GetElapsedTime(_started), Requests, ToolCalls, Chars, _token.IsCancellationRequested ? TurnCancelled : Ending));
    }

    /// <summary>The log category of the turn's opening and closing lines (the screen's reason line too), so a run reads off <c>Turn:</c>.</summary>
    public const string TurnCategory = "Turn";

    /// <summary>The endings <see cref="TurnEndedLogLine"/> names: the reply ran to its end; a guard, the budget or the iteration cap stopped it; the model's error or a throw failed it; the token cut it.</summary>
    public const string TurnCompleted = "completed";
    public const string TurnStopped = "stopped";
    public const string TurnFailed = "failed";
    public const string TurnCancelled = "cancelled";

    /// <summary>The suffix on an opening call's <c>Tool call</c> line: seeded, not the model's.</summary>
    public const string OpeningSuffix = " (opening)";

    /// <summary>The turn's opening line: <c>Turn 3 started: "why is the build red…" (2 images, 3 opening calls)</c> — the parenthesis only with either count. Pinned.</summary>
    public static string TurnStartedLogLine(int number, string text, int images, int openingCalls)
    {
        var extras = new List<string>(2);
        if (images > 0)
        {
            extras.Add(Plural(images, "image"));
        }

        if (openingCalls > 0)
        {
            extras.Add(Plural(openingCalls, "opening call"));
        }

        string tail = extras.Count == 0 ? "" : " (" + string.Join(", ", extras) + ")";
        return string.Create(CultureInfo.InvariantCulture, $"Turn {number} started: {LogText.Quoted(text)}{tail}");
    }

    /// <summary>The turn's closing line: <c>Turn 3 ended after 12.4 s: 2 requests, 3 tool calls, 512 chars, completed</c>. Pinned.</summary>
    public static string TurnEndedLogLine(int number, TimeSpan elapsed, int requests, int toolCalls, int chars, string ending) =>
        string.Create(CultureInfo.InvariantCulture, $"Turn {number} ended after {elapsed.TotalSeconds:F1} s: {Plural(requests, "request")}, {Plural(toolCalls, "tool call")}, {chars} chars, {ending}");

    /// <summary>One request's line: <c>Request 2: 14 messages, 19 tools, reasoning medium</c> (<c>no tools</c>, <c>reasoning default</c> when unset). Pinned.</summary>
    public static string RequestLogLine(int iteration, int messages, int tools, ReasoningEffort? reasoning) =>
        string.Create(CultureInfo.InvariantCulture, $"Request {iteration}: {Plural(messages, "message")}, {(tools == 0 ? "no tools" : Plural(tools, "tool"))}, reasoning {(reasoning is { } effort ? effort.ToString().ToLowerInvariant() : "default")}");

    /// <summary>A request cut by the token: <c>Request 2 cancelled after 3.2 s: 140 chars received</c>. Pinned.</summary>
    public static string RequestCancelledLogLine(int iteration, TimeSpan elapsed, int chars) =>
        string.Create(CultureInfo.InvariantCulture, $"Request {iteration} cancelled after {elapsed.TotalSeconds:F1} s: {chars} chars received");

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");

    /// <summary>
    /// The summariser behind <c>/compact</c> (<see cref="ConversationCompactor"/>): one request over
    /// <see cref="ConversationCompactor.SummaryInstruction"/>, <paramref name="transcript"/> as it
    /// was sent before, and <see cref="ConversationCompactor.SummaryRequest"/> last — no tools,
    /// thinking off (the adapter turns <see cref="ReasoningEffort.None"/> into the wire fields),
    /// the history untouched. Streamed like a turn so the usage carries the same two spans. A
    /// leaked <c>&lt;think&gt;</c> is filtered like a reply's. The transport's failures and
    /// cancellation propagate; an empty answer is <see cref="EmptySummaryError"/>.
    /// </summary>
    public async Task<(string Text, TokenUsage? Usage)> SummarizeAsync(IReadOnlyList<ChatMessage> transcript, string? focus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var request = new List<ChatMessage>(transcript.Count + 2) { new(ChatRole.System, ConversationCompactor.SummaryInstruction) };
        request.AddRange(transcript);
        request.Add(new ChatMessage(ChatRole.User, ConversationCompactor.SummaryRequest(focus)));
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } };

        var filter = new ThinkTagFilter();
        var text = new StringBuilder();
        var usage = TokenUsage.Zero;
        long sent = _time.GetTimestamp();
        long? first = null;
        await foreach (var update in _client.GetStreamingResponseAsync(request, options, cancellationToken).ConfigureAwait(false))
        {
            if (first is null && update.Contents.Count > 0)
            {
                first = _time.GetTimestamp();
            }

            text.Append(filter.Push(update.Text));
            foreach (var reported in update.Contents.OfType<UsageContent>())
            {
                usage += TokenUsage.From(reported.Details, TimeSpan.Zero, TimeSpan.Zero);
            }
        }

        long ended = _time.GetTimestamp();
        text.Append(filter.Flush());
        string summary = text.ToString().Trim();
        if (summary.Length == 0)
        {
            throw new InvalidOperationException(EmptySummaryError);
        }

        // --log only: what the model kept is the first thing a field report needs.
        DiagnosticLog.Debug(Category, "Summary: " + summary);

        if (usage.IsEmpty)
        {
            DiagnosticLog.Debug(Category, "No usage reported by the server for the summary request.");
            return (summary, null);
        }

        long firstAt = first ?? ended;
        var timed = usage with { ToFirstToken = _time.GetElapsedTime(sent, firstAt), Generating = _time.GetElapsedTime(firstAt, ended) };
        DiagnosticLog.Debug(Category, string.Create(CultureInfo.InvariantCulture,
            $"Summary usage: {timed.Input} in, {timed.Output} out, {timed.Total} total; first token after {timed.ToFirstToken.TotalSeconds:F2}s, streamed {timed.Generating.TotalSeconds:F2}s."));
        return (summary, timed);
    }

    /// <summary>
    /// One model request over <paramref name="request"/> as given (its own system message first),
    /// with <paramref name="tools"/> offered and <paramref name="effort"/> as the reasoning level:
    /// the reply's messages (to append to the caller's own list), its text with a leaked
    /// <c>&lt;think&gt;</c> filtered, the tool calls it asked for (none = the model is done) and the
    /// usage the server reported (null for none), the two spans stamped as a turn's are.
    /// </summary>
    public sealed record SideResponse(IReadOnlyList<ChatMessage> Messages, string Text, IReadOnlyList<FunctionCallContent> Calls, TokenUsage? Usage);

    /// <summary>
    /// The primitive behind a side loop that runs beside a turn (<see cref="Skills.SkillLearner"/>):
    /// one streamed request, the history untouched, nothing of this instance read but the client
    /// and the clock — so it is safe while <see cref="RunTurnAsync"/> streams. The transport's
    /// failures and cancellation propagate; the caller explains them.
    /// </summary>
    public async Task<SideResponse> RequestAsync(IReadOnlyList<ChatMessage> request, IReadOnlyList<AIFunction> tools, ReasoningEffort effort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tools);
        var options = new ChatOptions
        {
            Tools = tools.Count > 0 ? new List<AITool>(tools) : null,
            Reasoning = new ReasoningOptions { Effort = effort },
        };

        var updates = new List<ChatResponseUpdate>();
        var filter = new ThinkTagFilter();
        var text = new StringBuilder();
        long sent = _time.GetTimestamp();
        long? first = null;
        await foreach (var update in _client.GetStreamingResponseAsync(request, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            if (first is null && update.Contents.Count > 0)
            {
                first = _time.GetTimestamp();
            }

            text.Append(filter.Push(update.Text));
        }

        long ended = _time.GetTimestamp();
        text.Append(filter.Flush());

        var response = updates.ToChatResponse();
        if (filter.SawOrphanClose || filter.SawBlock)
        {
            ReplaceText(response.Messages, text.ToString());
        }

        var usage = TokenUsage.Zero;
        foreach (var reported in updates.SelectMany(u => u.Contents).OfType<UsageContent>())
        {
            long firstAt = first ?? ended;
            usage += usage.IsEmpty
                ? TokenUsage.From(reported.Details, _time.GetElapsedTime(sent, firstAt), _time.GetElapsedTime(firstAt, ended))
                : TokenUsage.From(reported.Details, TimeSpan.Zero, TimeSpan.Zero);
        }

        var calls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Where(c => !c.InformationalOnly)
            .ToList();

        return new SideResponse(response.Messages.ToList(), text.ToString().Trim(), calls, usage.IsEmpty ? null : usage);
    }

    /// <summary>
    /// Replaces the text of a response with what the filter let through: every
    /// <see cref="TextContent"/> goes, and the cleaned text (if any) becomes one item at the front
    /// of the first message. Function calls and reasoning items stay where they are.
    /// </summary>
    /// <summary>The <c>--log</c> line when a call written as text never closed and was dropped (2026-09-25).</summary>
    internal const string TextCallBrokenNote = "The model wrote a tool call out as text and never closed it; it was dropped from the reply.";

    /// <summary>A tool schema's parameter names (its <c>properties</c>), for the written call's line form (later on 2026-09-25); none when it has none.</summary>
    internal static IReadOnlyList<string> ParameterNames(System.Text.Json.JsonElement schema) =>
        schema.ValueKind == System.Text.Json.JsonValueKind.Object && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == System.Text.Json.JsonValueKind.Object
            ? properties.EnumerateObject().Select(property => property.Name).ToList()
            : [];

    /// <summary>The <c>--log</c> line for a call the model wrote out as text (2026-09-25): <c>Text tool call generate_image: prompt="…"</c>, cut like <see cref="ToolCallLogLine"/>.</summary>
    internal static string TextCallLogLine(string name, string arguments) =>
        "Text tool call " + name + ": " + (arguments.Length <= ToolCallLogChars ? arguments : arguments[..(ToolCallLogChars - 1)] + "…");

    /// <summary>
    /// The calls <see cref="TextToolCallFilter"/> caught, added to the response's last message as real
    /// <see cref="FunctionCallContent"/>s (2026-09-25) with ids of their own (<c>text-call-{iteration}-{n}</c>), so the loop runs them
    /// as it runs native ones. A call whose arguments do not parse is logged and left out.
    /// </summary>
    internal static void AddWrittenCalls(IList<ChatMessage> messages, IReadOnlyList<(string Name, string Arguments)> calls, int iteration)
    {
        if (messages.Count == 0)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, (string?)null));
        }

        int n = 0;
        foreach (var (name, text) in calls)
        {
            DiagnosticLog.Info(Category, TextCallLogLine(name, text));
            if (TextToolCallFilter.ParseArguments(text) is not { } arguments)
            {
                DiagnosticLog.Warn(Category, "The arguments of a " + name + " call written as text did not parse; it was not run.");
                continue;
            }

            n++;
            string id = "text-call-" + iteration.ToString(CultureInfo.InvariantCulture) + "-" + n.ToString(CultureInfo.InvariantCulture);
            messages[^1].Contents.Add(new FunctionCallContent(id, name, arguments));
        }
    }

    internal static void ReplaceText(IList<ChatMessage> messages, string text)
    {
        foreach (var message in messages)
        {
            for (int i = message.Contents.Count - 1; i >= 0; i--)
            {
                if (message.Contents[i] is TextContent)
                {
                    message.Contents.RemoveAt(i);
                }
            }
        }

        if (text.Length > 0 && messages.Count > 0)
        {
            messages[0].Contents.Insert(0, new TextContent(text));
        }
    }

    /// <summary>
    /// Runs one call against <paramref name="tools"/> and returns the text that goes back to the
    /// model, with the pictures a <see cref="ToolImageResult"/> carried (empty otherwise). Every
    /// branch returns — unknown tool, unparseable arguments, a throwing tool — so the caller can
    /// always attach the <c>CallId</c>. Only cancellation escapes. Static, so a side loop
    /// (<see cref="Skills.SkillLearner"/>) runs its own tools through the same rules.
    /// </summary>
    /// <summary>The result for a tool that threw (2026-09-18: <c>Error: boom failed: kaboom</c>, the <c>Error:</c> prefix every other tool sentence carries; <c>Error executing tool '…'</c> before). Pinned.</summary>
    public static string ToolFailed(string name, string message) => $"Error: {name} failed: {message}";

    public static async Task<(string Text, IReadOnlyList<ImageAttachment> Images)> InvokeToolAsync(IReadOnlyList<AIFunction> tools, FunctionCallContent call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(call);

        // Exact, ordinal: a namespaced or re-cased name is an unknown tool, and the model is told so.
        // A model's mistake is the Error: result, logged at Info — never a Warning on the transcript
        // (the 🛠️ echo already shows it in a turn; a reflection's shows nowhere but the log).
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, call.Name, StringComparison.Ordinal));
        if (tool is null)
        {
            DiagnosticLog.Info(Category, $"The model called unknown tool '{call.Name}'.");
            return ($"Error: unknown tool '{call.Name}'.", []);
        }

        if (call.Exception is not null)
        {
            // The adapter puts a JSON parse failure here instead of throwing.
            DiagnosticLog.Info(Category, $"Arguments for tool '{call.Name}' could not be parsed: {call.Exception.Message}");
            return ($"Error: the arguments for '{call.Name}' could not be parsed: {call.Exception.Message}", []);
        }

        return await InvokeAsync(tool, call, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The invocation itself, shared by the model's calls and the opening call: a throwing tool is a sentence, only cancellation escapes.</summary>
    private static async Task<(string Text, IReadOnlyList<ImageAttachment> Images)> InvokeAsync(AIFunction tool, FunctionCallContent call, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        (string Text, IReadOnlyList<ImageAttachment> Images) answer;
        try
        {
            var arguments = new AIFunctionArguments(call.Arguments ?? new Dictionary<string, object?>());
            object? value = await tool.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            answer = value switch
            {
                null => ("(no result)", []),
                string s => (s, []),
                ToolImageResult pictures => (pictures.Text, pictures.Images),
                _ => (value.ToString() ?? "(no result)", []),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn(Category, $"Tool '{call.Name}' threw.", ex);
            answer = (ToolFailed(call.Name, ex.Message), []);
        }

        // The result's shape for the log (2026-09-19): every call — the model's, the opening
        // ones, a reflection's — and a tool's own refusal at Info, since an Error: sentence never
        // throws and the transcript shows a quiet tool's line alone.
        if (IsToolError(answer.Text))
        {
            DiagnosticLog.Info(Category, ToolErrorLogLine(call.Name, answer.Text));
        }
        else
        {
            DiagnosticLog.Debug(Category, ToolResultLogLine(call.Name, answer.Text, answer.Images.Count, Stopwatch.GetElapsedTime(started)));
        }

        return answer;
    }

    /// <summary>Whether a tool's answer is one of its refusals: the <c>Error:</c> prefix every tool sentence carries.</summary>
    public static bool IsToolError(string result) => result.StartsWith("Error:", StringComparison.Ordinal);

    /// <summary>A tool's answer for the log: <c>Tool result read_file: 1,234 chars in 12 ms — "line one…"</c> (<c>, 2 pictures</c> after the chars when it carried any). Pinned.</summary>
    public static string ToolResultLogLine(string name, string result, int pictures, TimeSpan elapsed)
    {
        string carried = pictures == 0 ? "" : ", " + Plural(pictures, "picture");
        return string.Create(CultureInfo.InvariantCulture, $"Tool result {name}: {result.Length:N0} chars{carried} in {elapsed.TotalMilliseconds:F0} ms — {LogText.Quoted(result)}");
    }

    /// <summary>A tool's refusal for the log: <c>Tool read_file answered an error: Error: no such file…</c>. Pinned.</summary>
    public static string ToolErrorLogLine(string name, string result) =>
        "Tool " + name + " answered an error: " + LogText.Excerpt(result);

    /// <summary>
    /// The result content for a call: a loaded skill's instructions (a <c>load_skill</c> answer that
    /// is no <c>Error:</c>) are tagged <see cref="ConversationHistory.SkillResultKey"/>, the mark the
    /// compactor's protection reads (<see cref="ConversationCompactor.Prune"/>); never on the wire.
    /// </summary>
    public static FunctionResultContent ResultContent(FunctionCallContent call, string result)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(result);
        var content = new FunctionResultContent(call.CallId, result);
        if (string.Equals(call.Name, LoadSkillTool.ToolName, StringComparison.Ordinal) && !result.StartsWith("Error:", StringComparison.Ordinal))
        {
            content.AdditionalProperties = new AdditionalPropertiesDictionary { [ConversationHistory.SkillResultKey] = true };
        }

        return content;
    }

    /// <summary>The most characters of a call's arguments the <c>--log</c> line repeats (a <c>write_file</c> body would swamp the file).</summary>
    public const int ToolCallLogChars = 2000;

    /// <summary>The <c>--log</c> line for a tool call as the model sent it: <c>Tool call ask_user: {"questions": …}</c>, the arguments cut to <see cref="ToolCallLogChars"/> with an ellipsis. Pinned.</summary>
    internal static string ToolCallLogLine(string name, string argumentsJson) =>
        "Tool call " + name + ": " + (argumentsJson.Length <= ToolCallLogChars ? argumentsJson : argumentsJson[..(ToolCallLogChars - 1)] + "…");

    /// <summary>
    /// The <c>--log</c> line for one request's report: <c>Usage: 22 in, 139 out (135 reasoning), 161 total; first token after 0.80s, streamed 2.10s.</c>
    /// — the parenthesis only when the server counted the thinking. Pinned.
    /// </summary>
    internal static string UsageLogLine(TokenUsage usage)
    {
        string reasoning = usage.Reasoning is { } count ? string.Create(CultureInfo.InvariantCulture, $" ({count} reasoning)") : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"Usage: {usage.Input} in, {usage.Output} out{reasoning}, {usage.Total} total; first token after {usage.ToFirstToken.TotalSeconds:F2}s, streamed {usage.Generating.TotalSeconds:F2}s.");
    }

    /// <summary>
    /// Unwraps an exception into something that identifies the failure on its own. Necessary
    /// because stack traces are stripped from the published build: a wrapped SDK failure's outer
    /// message names neither the type nor the cause. An <see cref="AggregateException"/> is
    /// transparent (its message repeats every child's), a link whose message merely repeats the
    /// previous one is dropped, and the first four distinct causes are kept.
    /// </summary>
    internal static string Explain(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var parts = new List<string>();
        string? previousMessage = null;

        for (var current = ex; current is not null;)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                current = aggregate.InnerExceptions[0];
                continue;
            }

            if (current is ClientResultException failed && ServerDetail(failed) is { } detail)
            {
                // The SDK's own message is "Service request failed." over two lines; the server's
                // reason (a rejected reasoning level, an unknown model) is in the body it dropped.
                parts.Add($"{current.GetType().Name}: {detail}");
                previousMessage = current.Message;
            }
            else if (!string.Equals(current.Message, previousMessage, StringComparison.Ordinal))
            {
                parts.Add(current is TypeInitializationException typeInit
                    ? $"{current.GetType().Name} for {typeInit.TypeName}: {current.Message}"
                    : $"{current.GetType().Name}: {current.Message}");
                previousMessage = current.Message;
            }

            if (parts.Count >= 4)
            {
                break;
            }

            current = current.InnerException;
        }

        return string.Join(" -> ", parts);
    }

    /// <summary>Longest server message kept in a notice; the rest is an ellipsis.</summary>
    internal const int MaxServerDetail = 300;

    /// <summary>
    /// A failed request as the server explained it: <c>HTTP 400 (Bad Request): Unexpected reasoning
    /// effort high…</c>. Null when the exception carries no response (a transport failure).
    /// </summary>
    internal static string? ServerDetail(ClientResultException ex)
    {
        var response = ex.GetRawResponse();
        if (response is null)
        {
            return null;
        }

        string status = string.IsNullOrEmpty(response.ReasonPhrase)
            ? $"HTTP {response.Status.ToString(CultureInfo.InvariantCulture)}"
            : $"HTTP {response.Status.ToString(CultureInfo.InvariantCulture)} ({response.ReasonPhrase})";
        string message = ServerMessage(response.Content?.ToString() ?? "");
        return message.Length == 0 ? status : status + ": " + message;
    }

    /// <summary>
    /// The human part of an error body: the <c>message</c> of the OpenAI error shape (top-level
    /// or under <c>error</c>), otherwise the body itself; one line, at most <see cref="MaxServerDetail"/> characters.
    /// </summary>
    internal static string ServerMessage(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string text = body.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    root = error;
                }

                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    text = message.GetString() ?? "";
                }
            }
            catch (JsonException)
            {
                // Not JSON after all; the body is the message.
            }
        }

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > MaxServerDetail ? text[..MaxServerDetail] + "…" : text;
    }

    /// <summary>
    /// Tool arguments as JSON for the transcript, written by hand with <see cref="Utf8JsonWriter"/>.
    ///
    /// <para>Never <c>JsonSerializer.Serialize</c> over an <c>IDictionary&lt;string, object?&gt;</c>
    /// behind an <c>IL2026/IL3050</c> suppression: with <c>PublishAot</c> set, reflection
    /// serialisation is off and that call throws at runtime. Values arrive as <see cref="JsonElement"/> from the server or
    /// plain CLR primitives from a direct caller; anything else falls back to its string form.
    /// This is a diagnostic record, not a round-trip format.</para>
    /// </summary>
    internal static string SerializeArguments(IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        if (arguments is null)
        {
            return "{}";
        }

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments)
            {
                writer.WritePropertyName(name);
                switch (value)
                {
                    case null: writer.WriteNullValue(); break;
                    case JsonElement element: element.WriteTo(writer); break;
                    case string text: writer.WriteStringValue(text); break;
                    case bool flag: writer.WriteBooleanValue(flag); break;
                    case int i: writer.WriteNumberValue(i); break;
                    case long l: writer.WriteNumberValue(l); break;
                    case double d: writer.WriteNumberValue(d); break;
                    case decimal m: writer.WriteNumberValue(m); break;
                    case IFormattable f: writer.WriteStringValue(f.ToString(null, CultureInfo.InvariantCulture)); break;
                    default: writer.WriteStringValue(value.ToString()); break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
