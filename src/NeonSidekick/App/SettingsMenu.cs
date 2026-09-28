using System.Globalization;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// Every knob the settings menu edits, in menu order: on the flat list (no pane) the row order IS
/// the declaration order (<c>Enum.GetValues</c>); on the pane the rows sit under the six
/// <see cref="SettingsTab"/>s in <see cref="SettingsMenu.TabFields"/>'s order (the STT tab
/// follows this order, the others are spelled out) — and, since 2026-09-19, the Options, Ask, Files and Web
/// rows a member's summary names are <c>/tools</c>' tabs (<see cref="SettingsMenu.ToolsTabFields"/>),
/// edited through the same menu. The tests navigate by row, so moving a member changes
/// every <c>Down(n)</c> and row-number assertion in <c>SettingsMenuTests</c> / <c>ChatScreenTests</c>
/// and the rows quoted in the docs. The values are never persisted.
/// </summary>
public enum SettingsField
{
    /// <summary>Which profile is loaded: a picker over <see cref="Settings.Profiles.List"/>. Not a field of the data; the first row.</summary>
    Profile,

    /// <summary>Long-term memory on/off; a toggle that needs no reconnect (every turn reads it). Second row, under the profile it belongs to.</summary>
    Memory,
    LlmUrl,
    LlmModel,
    LlmApiKey,

    /// <summary>A picker over <see cref="Llm.ReasoningLevel.Levels"/>.</summary>
    LlmReasoning,
    LlmRequestTimeoutSeconds,
    LlmTurnTimeoutSeconds,
    /// <summary>The speech-output switch; first of the TTS rows so the block reads top-down.</summary>
    TtsOutput,
    TtsHttpUrl,

    /// <summary>
    /// A picker over <see cref="Speech.VoicePresets"/> (2026-09-27, the user's ask): one pick writes <see cref="TtsVoice"/>,
    /// <see cref="TtsVoice2"/>, <see cref="TtsVoiceMix"/> and <see cref="TtsSpeed"/>. Nothing of its own is saved: the row shows
    /// the preset those four match, or <see cref="SettingsMenu.CustomPreset"/>.
    /// </summary>
    TtsVoicePreset,
    TtsVoice,

    /// <summary>The optional second voice of a Kokoro mix; the picker offers "(none)" first.</summary>
    TtsVoice2,

    /// <summary>The primary voice's share of the mix in percent.</summary>
    TtsVoiceMix,
    TtsSpeed,
    SttInput,
    SttWake,
    SttWakePhrase,
    SttInterrupt,
    /// <summary>The interrupt's echo guard in percent: how close the assistant's own just-played speech must be to the phrase to be ignored.</summary>
    SttInterruptEchoGuard,

    /// <summary>Milliseconds the phrase must persist in the interrupt recogniser's interim results before a hit counts.</summary>
    SttInterruptConfirmMs,

    /// <summary>A picker over <see cref="SettingsMenu.PushToTalkKeys"/>; never typed.</summary>
    SttPushToTalkKey,

    /// <summary>A picker over <see cref="ModelStore.WhisperModelNames"/>; never typed (the path form is the variable's alone).</summary>
    SttWhisperModel,

    /// <summary>The folder the file tools work under: a full path, or empty for the profile's own <c>files\</c> folder.</summary>
    WorkingDirectory,

    /// <summary>Whether <c>/copy</c> puts the user's prompt above each reply (<see cref="Settings.AppSettingsData.CopyUserPrompt"/>; <c>Copy user text</c> until 2026-09-18); a toggle that needs no reconnect.</summary>
    CopyUserPrompt,

    /// <summary>The model's context window in tokens for the <c>/usage</c> percentage; 0 = the server's own figure. Last of the LLM rows (enum order within the tab).</summary>
    LlmContextLength,

    /// <summary>Whether a sent picture is drawn under the user's line; a toggle that needs no reconnect (read at each turn).</summary>
    ShowImageThumbnails,

    /// <summary>A picker over <see cref="Llm.CompactType.Names"/>: what <c>/compact</c> does. On the LLM tab; no reconnect (read at each compact).</summary>
    LlmCompactType,
    /// <summary>How many recent user turns a compact keeps verbatim (0 to <see cref="Llm.ConversationHistory.DefaultMaxTurns"/>). On the LLM tab; no reconnect.</summary>
    LlmCompactKeepRecent,

    /// <summary>The share of the window at which the next message compacts first; 0 = off. On the LLM tab; no reconnect.</summary>
    LlmAutoCompactPercent,
    /// <summary>How many user turns the model sees (<see cref="Settings.AppSettingsData.LlmMaxTurns"/>): 0 = auto. On the LLM tab under <see cref="LlmAutoCompactPercent"/> (2026-09-27); no reconnect (resolved before each message).</summary>
    LlmMaxTurns,
    /// <summary>A picker over <see cref="Llm.ToolCompactType.Names"/>: what the tool loop does at the <see cref="CompactAt"/> share mid-turn. On the LLM tab under <see cref="CompactAt"/>; no reconnect (read at each turn).</summary>
    LlmToolCompactType,
    /// <summary>Whether a turn offers the model its tools at all (<see cref="Settings.AppSettingsData.LlmOfferTools"/>). On the LLM tab above <see cref="MaxToolIterations"/>; no reconnect (read at each turn), but a change clears the conversation (<see cref="SettingsChanges.Conversation"/>).</summary>
    LlmOfferTools,

    /// <summary>Model round trips a message may spend on tools (<see cref="Settings.AppSettingsData.LlmMaxToolIterations"/>). On the LLM tab, last; no reconnect (read at each turn).</summary>
    LlmMaxToolIterations,
    /// <summary>A picker over <see cref="UI.ThumbnailSize.Names"/>: how big the thumbnail under a sent picture is. On the General tab beside <see cref="ShowImageThumbnails"/>; no reconnect (read at each turn).</summary>
    ImageThumbnailSize,

    /// <summary>Entries a <c>/tree</c> lists before it stops (<see cref="Settings.AppSettingsData.FileTreeMaxLength"/>). On the Files tab under <see cref="FileTools"/> (on General until 2026-09-15); no reconnect (read at each <c>/tree</c>).</summary>
    FileTreeMaxLength,

    /// <summary>Whether a <c>/tree</c> line carries the file's size (<see cref="Settings.AppSettingsData.FileTreeShowSizes"/>). The Files tab's row under Tree max length (on General until 2026-09-15; the last until the @-mention folder mode, 2026-09-17); no reconnect.</summary>
    FileTreeShowSizes,

    /// <summary>A picker over <see cref="Settings.NewProfileMode.Names"/>: what <c>/profile add</c> copies. On the General tab under <see cref="Profile"/>; no reconnect (read at each <c>/profile add</c>).</summary>
    NewProfileMode,

    /// <summary>Whether the thinking spinner reads a random <see cref="ThinkingVerbs"/> entry (<see cref="Settings.AppSettingsData.LlmUseFunVerbs"/>). Labelled <c>LLM use fun verbs</c> since 2026-09-15 (the member and the JSON key keep their name so a saved profile still loads); the LLM tab's last row; no reconnect (read at each spinner start).</summary>
    LlmUseFunVerbs,

    /// <summary>A picker over <see cref="Llm.LlmScanMode.Names"/>: where a blank URL's discovery looks, or <c>disabled</c> for no scan at all (2026-09-15). On the LLM tab FIRST, above <see cref="LlmUrl"/>; no reconnect (read at each scan).</summary>
    LlmScanMode,

    /// <summary>Whether a turn offers <c>web_search</c> / <c>web_fetch</c> (<see cref="Settings.AppSettingsData.WebTools"/>). The Web tab's first row; a toggle, no reconnect (read at each turn).</summary>
    WebTools,

    /// <summary>A picker over <see cref="Web.BrowserMode.Names"/>: how <c>web_fetch</c> gets a page (<see cref="Settings.AppSettingsData.WebBrowserMode"/>). On the Web tab under <see cref="WebTools"/>; no reconnect (read at each fetch).</summary>
    WebBrowserMode,

    /// <summary>The headless browser's executable, or empty for auto-detection (<see cref="Settings.AppSettingsData.WebBrowserPath"/>). On the Web tab; typed, no reconnect.</summary>
    WebBrowserPath,

    /// <summary>A picker over <see cref="Web.NetworkMode.Names"/>: where <c>web_fetch</c> may reach (<see cref="Settings.AppSettingsData.WebBrowserNetworkMode"/>; the on/off <c>Browser allow LAN</c> until 2026-09-18, this slot kept). On the Web tab; no reconnect (read at each connect).</summary>
    WebBrowserNetworkMode,

    /// <summary>A SearXNG instance's URL, read while <see cref="WebSearchMethod"/> is <c>searxng</c>, or empty (<see cref="Settings.AppSettingsData.WebSearxngUrl"/>). On the Web tab under <see cref="WebSearchMethod"/>; typed, no reconnect (read at each search).</summary>
    WebSearxngUrl,

    /// <summary>How many hits a <c>web_search</c> without <c>max_results</c> returns (<see cref="Settings.AppSettingsData.WebSearchMaxResults"/>). The Web tab's last row; typed, no reconnect.</summary>
    WebSearchMaxResults,

    /// <summary>Whether the voice pickers speak <see cref="SettingsMenu.VoicePreviewText"/> in the highlighted voice, and the mix and speed rows the blend at the value they just saved (<see cref="Settings.AppSettingsData.TtsVoicePreview"/>). On the TTS tab, last; a toggle on by default, no reconnect (read at the next pick or save).</summary>
    TtsVoicePreview,

    /// <summary>Whether a turn offers the twenty-one file tools (<see cref="Settings.AppSettingsData.FileTools"/>). The Files tab's first row (2026-09-15); a toggle, no reconnect (read at each turn).</summary>
    FileTools,

    /// <summary>A picker over <see cref="Web.SearchMethod.Names"/>: which engine <c>web_search</c> asks (<see cref="Settings.AppSettingsData.WebSearchMethod"/>). On the Web tab above <see cref="WebSearxngUrl"/> (2026-09-15); no reconnect (read at each search).</summary>
    WebSearchMethod,

    /// <summary>Whether a turn offers the question tool <c>ask_user</c> (<see cref="Settings.AppSettingsData.AskUser"/>). The Ask tab's first row (2026-09-15); a toggle, no reconnect (read at each turn), the pane still a gate.</summary>
    AskUser,

    /// <summary>The most questions one <c>ask_user</c> call may put (<see cref="Settings.AppSettingsData.AskMaxQuestions"/>). On the Ask tab under <see cref="AskUser"/>; typed, no reconnect (read at each use).</summary>
    AskMaxQuestions,

    /// <summary>The most options one <c>ask_user</c> question may offer (<see cref="Settings.AppSettingsData.AskMaxChoices"/>). The Ask tab's last row; typed, no reconnect (read at each use).</summary>
    AskMaxChoices,

    /// <summary>A picker over <see cref="Speech.TtsSource.Names"/>: the server over HTTP or Kokoro in this process (<see cref="Settings.AppSettingsData.TtsSource"/>). On the TTS tab under <see cref="SpeechOutput"/> (2026-09-16); a reconnect (<see cref="SettingsMenu.IsTtsField"/>).</summary>
    TtsSource,

    /// <summary>A picker over <see cref="ModelStore.VoskModelNames"/>: the Vosk model the wake word and the interrupt listen with (<see cref="Settings.AppSettingsData.SttVoskModel"/>). The STT tab's last row (2026-09-16); never typed, no path form; a voice reconnect (<see cref="SettingsMenu.IsVoiceField"/>).</summary>
    SttVoskModel,

    /// <summary>A picker over <see cref="Files.MentionFolderMode.Names"/>: what applying a folder from the line's @-mention list does (<see cref="Settings.AppSettingsData.FileMentionFolderMode"/>). The Files tab's last row (2026-09-17; General's row under Image thumbnail size before); no reconnect (read at each idle read).</summary>
    FileMentionFolderMode,

    /// <summary>A toggle: whether the model gets the skills, the two skill tools and the working directory's <c>NEON.md</c> / <c>AGENTS.md</c> (<see cref="Settings.AppSettingsData.AgentSkills"/>). The Options tab of <c>/skills</c>' first row (2026-09-16); no reconnect (read at each turn).</summary>
    AgentSkills,

    /// <summary>A toggle: whether <c>%USERPROFILE%\.agents\skills</c> is scanned too (<see cref="Settings.AppSettingsData.ExternalSkills"/>), read only while <see cref="AgentSkills"/> is on. The Options tab of <c>/skills</c>' second row (2026-09-16); no reconnect.</summary>
    ExternalSkills,

    /// <summary>A picker over <see cref="Skills.SkillCompactMode.Names"/>: whether a loaded skill survives a prune (<see cref="Settings.AppSettingsData.SkillCompactMode"/>). The Options tab of <c>/skills</c>' third row (2026-09-16); no reconnect.</summary>
    SkillCompactMode,

    /// <summary>A toggle: whether replies are shown as styled Markdown and asked for as such (<see cref="Settings.AppSettingsData.TranscriptMarkdown"/>). The General tab's row under Image thumbnail size (2026-09-16, once under @-mention folder mode; the last until Paste preview lines); no reconnect (read at each turn).</summary>
    TranscriptMarkdown,

    /// <summary>Lines of a collapsed paste the transcript shows under the sent line (<see cref="Settings.AppSettingsData.PastePreviewLines"/>); 0 = the label alone. The General tab's last row from 2026-09-16 until the two switches of 2026-09-18; no reconnect (read at each idle read).</summary>
    PastePreviewLines,

    /// <summary>A toggle: whether an edit copies the previous version into <c>.trash</c> first and <c>delete</c> moves there (<see cref="Settings.AppSettingsData.FileSafeEdits"/>); off, edits land in place and <c>delete</c> removes for good (2026-09-20). The Files tab's second row (2026-09-17); no reconnect (read at each tool call).</summary>
    FileSafeEdits,

    /// <summary>A toggle: whether <c>#</c> and part of a name lists the loaded skills on the chat line (<see cref="Settings.AppSettingsData.SkillHashMention"/>). The Options tab of <c>/skills</c>' fourth row (2026-09-17; fifth until later on 2026-09-18, when Skill slash commands went); no reconnect (read at each keystroke).</summary>
    SkillHashMention,

    /// <summary>A toggle: whether a turn of work is followed by a background reflection that writes or improves a skill (<see cref="Settings.AppSettingsData.ReflectionAutoLearn"/>). The Options tab of <c>/skills</c>' sixth row (2026-09-17); no reconnect (read at each turn's end).</summary>
    ReflectionAutoLearn,

    /// <summary>A picker: the reflection's reasoning level (<see cref="Settings.AppSettingsData.ReflectionReasoning"/>, <c>Skills.ReflectionReasoning</c>). The Options tab of <c>/skills</c>' seventh row (2026-09-17); no reconnect (read at each reflection).</summary>
    ReflectionReasoning,

    /// <summary>Typed: how many of the last turns the reflection reads, 1 to 5 (<see cref="Settings.AppSettingsData.ReflectionWindow"/>, <c>Skills.ReflectionWindow</c>). The Options tab of <c>/skills</c>' eighth row (2026-09-17); no reconnect (read at each reflection).</summary>
    ReflectionWindow,

    /// <summary>Typed: the model's own tool calls that make a task worth a reflection, 3 to 20 (<see cref="Settings.AppSettingsData.ReflectionMinToolCalls"/>, <c>Skills.ReflectionMinToolCalls</c>). The Options tab of <c>/skills</c>' ninth row (2026-09-17, its last until later that day); no reconnect (read at each reply's end).</summary>
    ReflectionMinToolCalls,

    /// <summary>Typed: the model requests one reflection may make before it is given up, 1 to 20 (<see cref="Settings.AppSettingsData.ReflectionMaxRequests"/>, <c>Skills.ReflectionMaxRequests</c>). The Options tab of <c>/skills</c>' tenth row (2026-09-17; the verbose switch under it until later still on 2026-09-19); no reconnect (read when a reflection starts).</summary>
    ReflectionMaxRequests,

    /// <summary>A toggle: whether the <c>/</c> completion list leaves <c>/exit</c> out (<see cref="Settings.AppSettingsData.HideExitAutocomplete"/>). The General tab's row after Paste preview lines (2026-09-18); no reconnect (read at each keystroke).</summary>
    HideExitAutocomplete,

    /// <summary>A toggle: whether a sent line that is a command's bare name offers the command first (<see cref="Settings.AppSettingsData.CommandTypoIntercept"/>). The General tab's row before Welcome splash (2026-09-18); no reconnect (read at each Enter).</summary>
    CommandTypoIntercept,

    /// <summary>A picker: how the splash greets you under the banner at startup — <c>fullsize</c> / <c>tiled</c> / <c>disabled</c> (<see cref="Settings.AppSettingsData.WelcomeSplashMode"/>; a toggle until 2026-09-24). The General tab's row before Show working directory (2026-09-18, the user's order); no reconnect (read at each show).</summary>
    WelcomeSplash,

    /// <summary>A toggle: whether the working directory sits at the right edge of the banner's title line (<see cref="Settings.AppSettingsData.ShowWorkingDirectory"/>). The General tab's row before Draft editor (2026-09-18, its last row until 2026-09-19); no reconnect (read at each banner draw).</summary>
    ShowWorkingDirectory,

    /// <summary>A toggle: whether a message sent while a reply runs is queued rather than only left for the idle line (<see cref="Settings.AppSettingsData.QueueMessages"/>). The General tab's row after Working directory (2026-09-18, the user's order); no reconnect (read at each mid-turn Enter).</summary>
    QueueMessages,

    /// <summary>A picker: what a cancelled reply does to the queue — <c>hold</c> / <c>drain</c> / <c>empty</c> (<see cref="Settings.AppSettingsData.QueueCancelMode"/>). The General tab's row after Queue messages (2026-09-18); no reconnect (read when a turn ends).</summary>
    QueueCancelMode,

    /// <summary>A toggle: whether every completed turn is written to the profile's <c>sessions.db</c> (<see cref="Settings.AppSettingsData.SessionLogging"/>). The Sessions tab's first row (2026-09-18); no reconnect (read at each turn's end).</summary>
    SessionLogging,

    /// <summary>Typed: days a session is kept after its last turn, 0 = forever (<see cref="Settings.AppSettingsData.SessionRetentionDays"/>). The Sessions tab's second row (2026-09-18, the user's order; third that morning); no reconnect (read at startup and after a profile switch).</summary>
    SessionRetentionDays,

    /// <summary>A picker over <see cref="Sessions.SessionNamingMode.Names"/>: where a new session's title comes from (<see cref="Settings.AppSettingsData.SessionNamingMode"/>). The Sessions tab's third row (2026-09-18; second that morning); no reconnect (read at the first turn's end).</summary>
    SessionNamingMode,

    /// <summary>A picker over <see cref="Sessions.SessionShowName.Names"/>: which session names the rule above the input row shows (<see cref="Settings.AppSettingsData.SessionShowName"/>). The Sessions tab's fourth row, under the naming mode (2026-09-18); no reconnect (read at each draw).</summary>
    SessionShowName,

    /// <summary>A toggle: whether <c>session_manager</c> is offered to the model (<see cref="Settings.AppSettingsData.SessionTool"/>). The Sessions tab's fifth row (2026-09-18, the user's order; last that morning, fourth until the show-name row); no reconnect (read at each turn).</summary>
    SessionTool,

    /// <summary>Typed: how many sessions a <c>session_manager</c> search or list without <c>max_results</c> returns, 1 to 20 (<see cref="Settings.AppSettingsData.SessionSearchMaxResults"/>). The Sessions tab's last row (2026-09-18; fourth that morning); no reconnect (read at each call).</summary>
    SessionSearchMaxResults,

    /// <summary>A toggle: whether <c>$</c> and part of a name lists the tools the next turn offers on the chat line (<see cref="Settings.AppSettingsData.ToolsDollarMention"/>). The Options tab of <c>/tools</c>' one row (2026-09-19); no reconnect (read at each keystroke).</summary>
    ToolsDollarMention,

    /// <summary>Typed: the minutes an automatic reflection waits after one wrote a skill, 0 (off) to 1440 (<see cref="Settings.AppSettingsData.ReflectionCooldownMinutes"/>, <c>Skills.ReflectionCooldown</c>). The Options tab of <c>/skills</c>' row after Reflection max requests (2026-09-19); no reconnect (read at each reply's end).</summary>
    ReflectionCooldownMinutes,

    /// <summary>A toggle: whether a reflection is handed the earlier sessions that match the turn and <c>session_manager</c> (<see cref="Settings.AppSettingsData.ReflectionIncludesSessions"/>). The Options tab of <c>/skills</c>' last row (2026-09-19); no reconnect (read when a reflection is decided).</summary>
    ReflectionIncludesSessions,

    /// <summary>A picker over <see cref="Skills.ReflectionCooldownMode.Names"/>: what the cooldown holds back (<see cref="Settings.AppSettingsData.ReflectionCooldownMode"/>). The Options tab of <c>/skills</c>' row after the cooldown minutes (2026-09-19); no reconnect (read at each reply's end).</summary>
    ReflectionCooldownMode,

    /// <summary>Typed: the command line <c>/draft</c> opens its file with, or empty for the shell's default (<see cref="Settings.AppSettingsData.DraftEditor"/>). The General tab's last row (2026-09-19); no reconnect (read at each <c>/draft</c>).</summary>
    DraftEditor,

    /// <summary>Typed: the most pictures one <c>view_image</c> call loads, 1 to 100 (<see cref="Settings.AppSettingsData.FileViewImageMaxPerCall"/>). The Files tab of <c>/tools</c>' last row (2026-09-19); no reconnect (read at each call).</summary>
    FileViewImageMaxPerCall,

    /// <summary>A toggle: the master switch over the MCP servers <c>mcp.json</c> names (<see cref="Settings.AppSettingsData.McpServers"/>). The Options tab of <c>/mcp</c>' first row (2026-09-20); a flip connects or disconnects them at once (<see cref="SettingsChanges.Mcp"/>), so it is refused mid-turn.</summary>
    McpServers,

    /// <summary>Typed: the seconds one MCP server gets to connect and list its tools, 5 to 300 (<see cref="Settings.AppSettingsData.McpConnectTimeoutSeconds"/>). The Options tab of <c>/mcp</c>' second row (2026-09-20); no reconnect (read at the next connect).</summary>
    McpConnectTimeoutSeconds,

    /// <summary>Whether a turn offers the eleven git tools (<see cref="Settings.AppSettingsData.GitNativeTools"/>). The Git (native) tab of <c>/tools</c>' first row (2026-09-20; <c>Git native tools</c>, off by default, since 2026-09-21); a toggle, no reconnect (read at each turn).</summary>
    GitNativeTools,

    /// <summary>Typed: the most patch lines one <c>git_diff</c> shows, 20 to 5000 (<see cref="Settings.AppSettingsData.GitNativeDiffMaxLines"/>). The Git (native) tab's second row; no reconnect (read at each call).</summary>
    GitNativeDiffMaxLines,

    /// <summary>Typed: how many commits a <c>git_log</c> without <c>max_commits</c> lists, 1 to 200 (<see cref="Settings.AppSettingsData.GitNativeLogMaxCommits"/>). The Git (native) tab's third row; no reconnect (read at each call).</summary>
    GitNativeLogMaxCommits,

    /// <summary>A picker over <see cref="Shell.CommandPolicy.Names"/>: what stands between <c>run_command</c> and the shell (<see cref="Settings.AppSettingsData.ShellCommandPolicy"/>) — <c>off</c> is the Shell group's switch. The Shell tab of <c>/tools</c>' first row (2026-09-21); no reconnect (read at each call).</summary>
    ShellCommandPolicy,

    /// <summary>A list: the prefixes allowed for good on the approval pane (<see cref="Settings.AppSettingsData.ShellCommandAllowed"/>); Enter on one removes it. The Shell tab's second row (2026-09-21); no reconnect.</summary>
    ShellCommandAllowed,

    /// <summary>A picker over <see cref="Shell.ShellKinds.Names"/>: the shell a <c>run_command</c> without <c>shell</c> runs in (<see cref="Settings.AppSettingsData.ShellDefault"/>). The Shell tab's third row (2026-09-21); no reconnect (read at each call).</summary>
    ShellDefault,

    /// <summary>Typed: the seconds a foreground <c>run_command</c> without <c>timeout</c> waits, 1 to 3600 (<see cref="Settings.AppSettingsData.ShellTimeoutSeconds"/>). The Shell tab's fourth row (2026-09-21); no reconnect (read at each call).</summary>
    ShellTimeoutSeconds,

    /// <summary>Typed: the most seconds a foreground <c>run_command</c> may wait, 10 to 3600 (<see cref="Settings.AppSettingsData.ShellForegroundCapSeconds"/>). The Shell tab's fifth row (2026-09-21); no reconnect (read at each call).</summary>
    ShellForegroundCapSeconds,

    /// <summary>Typed: the most chars of output one result carries, 2000 to 500000 (<see cref="Settings.AppSettingsData.ShellOutputMaxChars"/>). The Shell tab's sixth row (2026-09-21); no reconnect (read at each call).</summary>
    ShellOutputMaxChars,

    /// <summary>A checkbox list over <see cref="Shell.CodeLanguages.Names"/>: the languages <c>execute_code</c> may run, at least one (<see cref="Settings.AppSettingsData.ShellCodeLanguages"/>). The Shell tab's seventh row (2026-09-21); no reconnect (read at each turn).</summary>
    ShellCodeLanguages,

    /// <summary>Typed: the seconds an <c>execute_code</c> script without <c>timeout</c> may run, 1 to 3600 (<see cref="Settings.AppSettingsData.ShellCodeTimeoutSeconds"/>). The Shell tab's eighth row (2026-09-21); no reconnect (read at each call).</summary>
    ShellCodeTimeoutSeconds,

    /// <summary>Typed: the most tool calls one <c>execute_code</c> script may make, 1 to 500 (<see cref="Settings.AppSettingsData.ShellCodeMaxToolCalls"/>). The Shell tab's last row (2026-09-21); no reconnect (read at each call).</summary>
    ShellCodeMaxToolCalls,

    /// <summary>A toggle: whether a compact's summary, or its pruned results, follow the compact notice in the transcript (<see cref="Settings.AppSettingsData.LlmCompactShowSummary"/>). The LLM tab, right under <see cref="LlmCompactKeepRecent"/> (2026-09-21); no reconnect (read at each compact).</summary>
    LlmCompactShowSummary,

    /// <summary>Typed: the <c>user.email</c> <c>/gituser</c> writes into the working directory's repository (<see cref="Settings.AppSettingsData.GitNativeEmail"/>); empty = not set. The Git (native) tab's fourth row (2026-09-21); no reconnect (read at each <c>/gituser</c>).</summary>
    GitNativeEmail,

    /// <summary>Typed: the <c>user.name</c> <c>/gituser</c> writes beside the email (<see cref="Settings.AppSettingsData.GitNativeName"/>); empty = not set. The Git (native) tab's last row (2026-09-21); no reconnect.</summary>
    GitNativeName,

    /// <summary>A toggle: whether an <c>execute_code</c> script may call the app's other tools through its <c>neon_tools</c> module (<see cref="Settings.AppSettingsData.ShellToolBridge"/>). The Shell tab, right above the tool-call cap it governs (later on 2026-09-21); no reconnect (read at each call and each turn).</summary>
    ShellToolBridge,

    /// <summary>A picker over <see cref="Files.FileBrowserMode.Names"/>: what the <c>/cwd browse</c> tree and <c>/tree</c> (2026-09-23) list (<see cref="Settings.AppSettingsData.FileBrowserMode"/>). The Files tab's row under the @-mention folder mode (2026-09-21); no reconnect (read when the pane opens).</summary>
    FileBrowserMode,

    /// <summary>A toggle: whether the toolbar is drawn under the hint row (<see cref="Settings.AppSettingsData.ShowToolbar"/>). The General tab's row after Show working directory (2026-09-21); no reconnect (read at each pane draw).</summary>
    ShowToolbar,

    /// <summary>A toggle: whether a command line, a script or text to a background process may name a path outside the working directory (<see cref="Settings.AppSettingsData.ShellPoliceOutsidePaths"/>). The Shell tab's third row (2026-09-22), under the list it guards beside; no reconnect (read at each call and each turn). Last in the enum, as every newcomer: the flat no-pane list's row numbers are pinned.</summary>
    ShellPoliceOutsidePaths,

    /// <summary>Typed: how many lines of a tool run stay while it runs before it folds under its summary, 0 (off) to 100 (<see cref="Settings.AppSettingsData.ToolCollapseCount"/>). The Options tab of <c>/tools</c>, under <see cref="ToolsDollarMention"/> (2026-09-22, the user's place); no reconnect (read when a run opens). Last in the enum, as every newcomer.</summary>
    ToolCollapseCount,

    /// <summary>Typed: how many lines a top-level code block of a styled reply may have before it folds to its label line, 0 (off) to 100 (<see cref="Settings.AppSettingsData.CodeCollapseCount"/>). The Options tab of <c>/tools</c>, under <see cref="ToolCollapseCount"/> (2026-09-22, the user's place); no reconnect (read when a reply opens). Last in the enum, as every newcomer.</summary>
    CodeCollapseCount,

    /// <summary>A toggle: whether a turn offers the eight vault tools (<see cref="Settings.AppSettingsData.ObsidianTools"/>). The Obsidian tab of <c>/tools</c>' first row (2026-09-22); no reconnect (read at each turn). Last in the enum, as every newcomer.</summary>
    ObsidianTools,

    /// <summary>The Obsidian vault's folder (<see cref="Settings.AppSettingsData.ObsidianVault"/>): the <c>/cwd browse</c> folder picker, or a typed full path; it must hold <c>.obsidian</c>, and empty clears it. The Obsidian tab's second row (2026-09-22); no reconnect (read at each call). Last in the enum, as every newcomer.</summary>
    ObsidianVault,

    /// <summary>A toggle: whether a turn offers <c>vault_delete</c> (<see cref="Settings.AppSettingsData.ObsidianAllowDelete"/>), on by default since 2026-09-23 (off before). The Obsidian tab's third row (2026-09-22); no reconnect (read at each turn and call). Last in the enum, as every newcomer.</summary>
    ObsidianAllowDelete,

    /// <summary>A toggle: whether a turn offers the eight SQL tools (<see cref="Settings.AppSettingsData.SqlTools"/>). The SQL tab of <c>/tools</c>' first row (2026-09-23); no reconnect (read at each turn). Last in the enum, as every newcomer.</summary>
    SqlTools,

    /// <summary>A pick: the connection a SQL tool uses when the call names none (<see cref="Settings.AppSettingsData.SqlDefaultConnection"/>), from the names in <c>sql.json</c> or the first. The SQL tab's second row (2026-09-23); no reconnect (read at each call).</summary>
    SqlDefaultConnection,

    /// <summary>Typed: how many rows a <c>sql_query</c> without <c>max_rows</c> returns, 1 to 1000 (<see cref="Settings.AppSettingsData.SqlQueryMaxRows"/>). The SQL tab's third row (2026-09-23); no reconnect.</summary>
    SqlQueryMaxRows,

    /// <summary>Typed: seconds a SQL tool's batch may run, 1 to 600 (<see cref="Settings.AppSettingsData.SqlQueryTimeoutSeconds"/>). The SQL tab's fourth row (2026-09-23); no reconnect.</summary>
    SqlQueryTimeoutSeconds,

    /// <summary>An edit row, no setting behind it: Enter opens the profile's <c>sql.json</c> in the editor (made with <see cref="Sql.SqlConfigFile.EmptyText"/> when missing); the value is its connection count. The SQL tab's fifth row (2026-09-23).</summary>
    SqlConnectionsProfile,

    /// <summary>The same for the home's <c>sql.json</c>, every profile's connections. The SQL tab's last row (2026-09-23).</summary>
    SqlConnectionsGlobal,

    /// <summary>An action row, no setting behind it (later on 2026-09-23): Enter picks a connection that takes a password (<c>sql</c> or <c>runas</c>) and asks for it in a masked slot, saved to the connection's store — DPAPI-encrypted into its <c>sql.json</c>, or Windows Credential Manager (<see cref="Sql.SqlSecrets.Save"/>). The SQL tab's third row.</summary>
    SqlSetPassword,

    /// <summary>A toggle: whether <c>%</c> and part of a name lists the SQL connections on the chat line (<see cref="Settings.AppSettingsData.SqlPercentMention"/>). The SQL tab's fourth row (later on 2026-09-23); no reconnect (read at each keystroke). Last in the enum, as every newcomer.</summary>
    SqlPercentMention,

    /// <summary>A checklist: which connections of <c>sql.json</c> this profile offers (<see cref="Settings.AppSettingsData.SqlConnectionsOffered"/>). The SQL tab's second row (later on 2026-09-23); no reconnect (read at each call). Last in the enum, as every newcomer.</summary>
    SqlConnectionsOffered,

    /// <summary>An action row, no setting behind it (later on 2026-09-23, the user's ask): Enter walks a new connection through every choice — the file, the name, the server, the sign-in, the password (masked), the TLS pair — tests it and adds it to that <c>sql.json</c> (<c>SettingsMenu.SqlWizard.cs</c>). The SQL tab's fifth row, under <see cref="SqlSetPassword"/>. Last in the enum, as every newcomer.</summary>
    SqlAddConnection,

    /// <summary>A pick among <see cref="UI.ThemeName.Names"/>: the look (<see cref="Settings.AppSettingsData.Theme"/>, 2026-09-23). The General tab's last row; a change puts the theme in force at once (the pane re-colours) and raises <see cref="SettingsChanges.Theme"/>, so the screen starts over as <c>/splash</c> does when the pane closes. No reconnect. Last in the enum, as every newcomer.</summary>
    Theme,

    /// <summary>A toggle: whether a turn offers the image tools (<see cref="Settings.AppSettingsData.ComfyTools"/>). The ComfyUI tab of <c>/tools</c>' first row (2026-09-24); no reconnect (read at each turn). Last in the enum, as every newcomer.</summary>
    ComfyTools,

    /// <summary>The ComfyUI server's URL, or empty (<see cref="Settings.AppSettingsData.ComfyUrl"/>). The ComfyUI tab's second row (2026-09-24); typed, no reconnect (read at each call).</summary>
    ComfyUrl,

    /// <summary>Typed: seconds one generation may take, 10 to 3600 (<see cref="Settings.AppSettingsData.ComfyTimeoutSeconds"/>). The ComfyUI tab's third row (2026-09-24); no reconnect.</summary>
    ComfyTimeoutSeconds,

    /// <summary>Typed: the folder under the working directory the pictures are saved in (<see cref="Settings.AppSettingsData.ComfyOutputFolder"/>). The ComfyUI tab's last row (2026-09-24); no reconnect.</summary>
    ComfyOutputFolder,

    /// <summary>A checklist: which installed ComfyUI workflows the model is offered (<see cref="Settings.AppSettingsData.ComfyWorkflowsOffered"/>). The ComfyUI tab's third row (later on 2026-09-24); no reconnect (read at each call). Last in the enum, as every newcomer.</summary>
    ComfyWorkflowsOffered,

    /// <summary>An action row, no setting behind it (later on 2026-09-24, the user's ask): Enter walks a new workflow — built from the server's models, or imported from a ComfyUI export — through every choice, tests it and saves it into a comfy folder (<c>SettingsMenu.ComfyWizard.cs</c>). The ComfyUI tab's fourth row. Last in the enum, as every newcomer.</summary>
    ComfyAddWorkflow,

    /// <summary>Typed (the <c>Image viewer</c> row, <c>Image editor</c> until later still on 2026-09-24): where a double-clicked picture opens: empty for the built-in viewer (later on 2026-09-27; the app Windows registers before), <c>system</c> for that app, else a command line (<see cref="Settings.AppSettingsData.ImageEditor"/>). The General tab, under <see cref="DraftEditor"/> (later on 2026-09-24); no reconnect (read at each double-click). Last in the enum, as every newcomer.</summary>
    ImageEditor,

    /// <summary>Typed: the most pictures one <c>generate_image</c> call or <c>/imagine --count</c> makes, 1 to 16 (<see cref="Settings.AppSettingsData.ComfyMaxPicturesPerCall"/>). The ComfyUI tab, under the timeout (later on 2026-09-24); no reconnect. Last in the enum, as every newcomer.</summary>
    ComfyMaxPicturesPerCall,

    /// <summary>A toggle: whether the model appends reinforcing tags to a workflow's negative (<see cref="Settings.AppSettingsData.ComfyReinforceNegatives"/>). The ComfyUI tab, under the pictures cap (later still on 2026-09-24); no reconnect. Last in the enum, as every newcomer.</summary>
    ComfyReinforceNegatives,

    /// <summary>A toggle: whether the prompt and negative sent to ComfyUI show under a picture's line (<see cref="Settings.AppSettingsData.ComfyShowPrompts"/>). The ComfyUI tab, under reinforce negatives (later still on 2026-09-24); no reconnect.</summary>
    ComfyShowPrompts,

    /// <summary>A toggle: whether <c>^</c> and part of a name lists the offered ComfyUI workflows on the chat line (<see cref="Settings.AppSettingsData.ComfyCaretMention"/>). The ComfyUI tab, under the add-workflow wizard (later still on 2026-09-24); no reconnect (read at each keystroke).</summary>
    ComfyCaretMention,

    /// <summary>A toggle: whether the session's ComfyUI pictures stand in a strip over the input line (<see cref="Settings.AppSettingsData.ComfyPictureStrip"/>). The ComfyUI tab, under show prompts (later still on 2026-09-24); no reconnect (read at each draw).</summary>
    ComfyPictureStrip,

    /// <summary>A toggle: whether a turn's start pauses a running reflection, which runs again after the reply (<see cref="Settings.AppSettingsData.ReflectionYieldsToTurns"/>). The Reflection tab of <c>/skills</c>' last row (2026-09-24); no reconnect (read at each turn's start). Last in the enum, as every newcomer.</summary>
    ReflectionYieldsToTurns,

    /// <summary>A toggle: whether <c>/botchat</c> has pictures (<see cref="Settings.AppSettingsData.BotChatImages"/>). The Botchat tab's first row (2026-09-25); no reconnect (read per reply).</summary>
    BotChatImages,

    /// <summary>A picker: who draws a <c>/botchat</c> picture — <c>automatic</c> / <c>autonomous</c> (<see cref="Settings.AppSettingsData.BotChatImageMode"/>). The Botchat tab, under the switch (2026-09-25); no reconnect.</summary>
    BotChatImageMode,

    /// <summary>A picker: the text → image workflow of <c>/botchat</c>'s fresh pictures, or none (<see cref="Settings.AppSettingsData.BotChatTxt2ImgWorkflow"/>). The Botchat tab, under the mode (2026-09-25; renamed and none since 2026-09-27); no reconnect.</summary>
    BotChatTxt2ImgWorkflow,

    /// <summary>A toggle: whether the next bot answers while the app's <c>/botchat</c> picture renders (<see cref="Settings.AppSettingsData.BotChatImageAsync"/>). The Botchat tab's last row (2026-09-25); no reconnect.</summary>
    BotChatImageAsync,

    /// <summary>A picker: whose LLM the <c>/botchat</c> bots talk through — <c>single</c> / <c>multi</c> (<see cref="Settings.AppSettingsData.BotChatLlmMode"/>). The Botchat tab's first row (later on 2026-09-25); no reconnect (read at a chat's start).</summary>
    BotChatLlmMode,

    /// <summary>A picker: what the busy row's token tally shows while a turn runs — <c>estimate</c> / <c>last-known</c> (<see cref="Settings.AppSettingsData.LlmMidTurnUsage"/>). On the LLM tab under <see cref="LlmContextLength"/> (2026-09-25); no reconnect (read on every draw).</summary>
    LlmMidTurnUsage,

    /// <summary>A toggle: whether the input line's Up/Down history is stored in <c>sessions.db</c> and recalled after a restart (<see cref="Settings.AppSettingsData.KeepCommandHistory"/>). The General tab's row under Command typo intercept (2026-09-25, the user's ask); no reconnect (read at each remembered line and each load).</summary>
    KeepCommandHistory,

    /// <summary>Typed: the seconds <c>/botchat</c> rests after a reply when no voice plays, 0 (off) to 30 (<see cref="Settings.AppSettingsData.BotChatNonTtsDelaySeconds"/>). The Botchat tab's last row (2026-09-26, the user's ask); no reconnect (read per reply). Last in the enum, as every newcomer.</summary>
    BotChatNonTtsDelaySeconds,

    /// <summary>A toggle: whether <c>run_command</c> steps aside for a native tool (<see cref="Settings.AppSettingsData.ShellPreferNative"/>). The Shell tab's row under Shell police outside paths (2026-09-26, the user's ask); no reconnect (read at each call and each turn). Last in the enum, as every newcomer.</summary>
    ShellPreferNative,

    /// <summary>A toggle: whether the model's thinking streams into the transcript and folds when the answer starts (<see cref="Settings.AppSettingsData.LlmShowThinking"/>). The LLM tab's last row (2026-09-26, the user's ask); no reconnect (read at each turn). Last in the enum, as every newcomer.</summary>
    LlmShowThinking,

    /// <summary>Typed: the Claude Code CLI <c>/claude</c> starts (<see cref="Settings.AppSettingsData.ClaudeExecutable"/>); empty = looked up. The <c>/tools</c> Claude (CLI) tab's first row (2026-09-27; on <c>/settings</c> that morning); read at each <c>/claude</c> and advisor call.</summary>
    ClaudeExecutable,

    /// <summary>A picker over <see cref="Claude.ClaudePermission.Names"/>: what the Claude Code child may do on its own (<see cref="Settings.AppSettingsData.ClaudePermissions"/>). The Claude (CLI) tab's second row (2026-09-27).</summary>
    ClaudePermissions,

    /// <summary>Typed: the <c>--model</c> of a <c>/claude</c> run (<see cref="Settings.AppSettingsData.ClaudeModel"/>); empty = the CLI's own. The Claude (CLI) tab's third row (2026-09-27).</summary>
    ClaudeModel,

    /// <summary>A picker over <see cref="Claude.ClaudeEffort.Names"/>: the <c>--effort</c> of a <c>/claude</c> run (<see cref="Settings.AppSettingsData.ClaudeEffort"/>). The Claude (CLI) tab's last row (2026-09-27).</summary>
    ClaudeEffort,

    /// <summary>A toggle: whether the model is offered <c>claude_advisor</c> (<see cref="Settings.AppSettingsData.ClaudeAdvisor"/>). The <c>/tools</c> Claude (CLI) tab, under the four Claude command rows (2026-09-27); the group's switch, no reconnect (read at each turn).</summary>
    ClaudeAdvisor,

    /// <summary>A picker over <see cref="Claude.ClaudeAdvisorContext.Names"/>: what an advisor call sends besides the question (<see cref="Settings.AppSettingsData.ClaudeAdvisorContext"/>). Under the switch (2026-09-27).</summary>
    ClaudeAdvisorContext,

    /// <summary>Typed: the most advisor calls a turn, 1 to 10 (<see cref="Settings.AppSettingsData.ClaudeAdvisorCallsPerTurn"/>). Under the context (2026-09-27).</summary>
    ClaudeAdvisorCallsPerTurn,

    /// <summary>Typed: the <c>--model</c> of an advisor call (<see cref="Settings.AppSettingsData.ClaudeAdvisorModel"/>); empty = the Claude slash command model. Under the cap (2026-09-27).</summary>
    ClaudeAdvisorModel,

    /// <summary>A picker over <see cref="Claude.ClaudeEffort.Names"/>: the <c>--effort</c> of an advisor call (<see cref="Settings.AppSettingsData.ClaudeAdvisorEffort"/>); empty = the Claude slash command effort. Under the model (2026-09-27).</summary>
    ClaudeAdvisorEffort,

    /// <summary>A toggle: whether each advisor call waits for the user's yes (<see cref="Settings.AppSettingsData.ClaudeAdvisorConfirm"/>). The Claude (CLI) tab's last row (2026-09-27).</summary>
    ClaudeAdvisorConfirm,

    /// <summary>A toggle: whether the Claude API is offered as a server (<see cref="Settings.AppSettingsData.ClaudeApi"/>). The <c>/settings</c> Claude (API) tab's first row (2026-09-27); a reconnect.</summary>
    ClaudeApi,

    /// <summary>Typed: the Claude API key, saved DPAPI-encrypted and shown masked (<see cref="Settings.AppSettingsData.ClaudeApiKey"/>); empty clears it. Under the switch (2026-09-27); a reconnect.</summary>
    ClaudeApiKey,

    /// <summary>Typed: the <c>max_tokens</c> of every Claude API request (<see cref="Settings.AppSettingsData.ClaudeApiMaxTokens"/>). Under the key (2026-09-27); a reconnect.</summary>
    ClaudeApiMaxTokens,

    /// <summary>A toggle: whether Claude API requests carry prompt-cache breakpoints (<see cref="Settings.AppSettingsData.ClaudeApiPromptCaching"/>). The Claude (API) tab's last row (2026-09-27); a reconnect. Last in the enum, as every newcomer.</summary>
    ClaudeApiPromptCaching,

    /// <summary>A toggle: whether the <c>/botchat</c> bots get the main chat's skills and <c>load_skill</c> (<see cref="Settings.AppSettingsData.BotChatSkills"/>). The Botchat tab's last row (2026-09-27, the user's ask); no reconnect (read per reply). Last in the enum, as every newcomer.</summary>
    BotChatSkills,

    /// <summary>A toggle: whether the <c>/botchat</c> bots are shown the chat's pictures (<see cref="Settings.AppSettingsData.BotChatVision"/>). The Botchat tab's last row (2026-09-27, the user's ask); no reconnect (read per reply). Last in the enum, as every newcomer.</summary>
    BotChatVision,

    /// <summary>A toggle (the <c>Themed image viewer</c> row): whether the built-in picture viewer wears the theme or stays black (<see cref="Settings.AppSettingsData.ThemedViewer"/>). The General tab's last row, under <see cref="ImageEditor"/> (later on 2026-09-27, the user's ask and name); no reconnect (read when the viewer opens or is focused). Last in the enum, as every newcomer.</summary>
    ThemedViewer,

    /// <summary>A toggle: whether a reflection may write a skill's supporting files with <c>skill_editor</c>'s <c>write_file</c> / <c>edit_file</c> (<see cref="Settings.AppSettingsData.ReflectionEditsSupportingFiles"/>). The Reflection tab of <c>/skills</c>' last row (2026-09-27, the user's ask and name); no reconnect (read when a reflection is decided). Last in the enum, as every newcomer.</summary>
    ReflectionEditsSupportingFiles,

    /// <summary>A picker: the image → image workflow <c>/botchat</c> may rework a picture with, or none (<see cref="Settings.AppSettingsData.BotChatImg2ImgWorkflow"/>). The Botchat tab, under the txt2img row (2026-09-27, the user's ask); no reconnect (read per reply). Last in the enum, as every newcomer.</summary>
    BotChatImg2ImgWorkflow,

    /// <summary>A picker: which pictures a <c>/botchat</c> rework may start from — <c>latest</c> / <c>chat-history</c> (<see cref="Settings.AppSettingsData.BotChatImg2ImgMode"/>). The Botchat tab, under the img2img row (2026-09-27, the user's ask); no reconnect (read per reply). Last in the enum, as every newcomer.</summary>
    BotChatImg2ImgMode,

    /// <summary>A checklist: the skills <c>/botchat</c> loads itself (<see cref="Settings.AppSettingsData.BotChatPreloadedSkills"/>). The Botchat tab, under the skills switch (2026-09-27, the user's ask); no reconnect (read per reply). Last in the enum, as every newcomer.</summary>
    BotChatPreloadedSkills,

    /// <summary>A picker: where the preloaded skills go — <c>prompt-writer-only</c> / <c>prompt-writer-and-bots</c> (<see cref="Settings.AppSettingsData.BotChatSkillMode"/>). The Botchat tab, under the checklist (2026-09-27, the user's ask); no reconnect (read per reply). Last in the enum, as every newcomer.</summary>
    BotChatSkillMode,

    /// <summary>A toggle: whether every turn's thinking goes back to a local server and the chat template is asked to keep it (<see cref="Settings.AppSettingsData.LlmPreserveThinking"/>). The LLM tab, under Show thinking (2026-09-28, the user's question); no reconnect (read at each turn). Last in the enum, as every newcomer.</summary>
    LlmPreserveThinking,

    /// <summary>A toggle: whether a reply's thinking is saved with the session (<see cref="Settings.AppSettingsData.SessionSaveThinking"/>). The Sessions tab's last row (2026-09-28, the user's ask); no reconnect (read at each save). Last in the enum, as every newcomer.</summary>
    SessionSaveThinking,
}

/// <summary>The tabs of <c>/settings</c> on the pane, in strip order (Sessions right after General — the user's order, 2026-09-18; STT last since 2026-09-19, when the Ask, Files and Web tabs moved to <c>/tools</c> — <see cref="SettingsMenu.ToolsTabFields"/> — and, later that day, the Skills tab to <c>/skills</c> as its Options tab — <see cref="SettingsMenu.SkillsTabFields"/>); the value is the index into <see cref="SettingsMenu.TabTitles"/> and <see cref="SettingsMenu.TabFields"/>.</summary>
public enum SettingsTab
{
    General,

    /// <summary>The session store's five rows (2026-09-18), second since later that day (last that morning).</summary>
    Sessions,

    /// <summary>Third since 2026-09-19 (the skills' rows sat between, 2026-09-18 until then).</summary>
    Llm,
    Tts,

    /// <summary>The voice rows, last since 2026-09-19 (Ask, Files and Web after it until then) until the Botchat tab came after them.</summary>
    Stt,

    /// <summary>The Claude API's four rows (2026-09-27, the user's ask and place: after STT, before Botchat) — the switch that offers it on <c>/server</c>, its key, its output cap and its prompt caching.</summary>
    ClaudeApi,

    /// <summary>The <c>/botchat</c> picture rows (2026-09-25, the user's ask: a Botchat tab on <c>/settings</c>), last (a Claude tab followed it on 2026-09-27 until later that day, when its rows moved to <c>/tools</c>' Claude tab, the user's call).</summary>
    BotChat,
}

/// <summary>What <see cref="SettingsMenu.ShowAsync"/> changed, so the screen rebuilds only what depends on it.</summary>
[Flags]
public enum SettingsChanges
{
    None = 0,
    Llm = 1,
    Tts = 2,
    Voice = 4,

    /// <summary>Another profile was loaded: everything may differ, and the screen rebinds memory and persona too.</summary>
    Profile = 8,

    /// <summary>
    /// <see cref="SettingsField.LlmOfferTools"/> was flipped: the history's shape depends on it (off, its
    /// tool messages would keep a template that has no tool role failing; on, the opening calls seed
    /// only at the first turn), so the screen clears the conversation. No reconnect.
    /// </summary>
    Conversation = 16,

    /// <summary><see cref="SettingsField.McpServers"/> was flipped: the screen connects or disconnects the MCP servers (2026-09-20). Set by the pane-less flat list of <c>/settings</c> and by <c>/mcp</c>' Options tab.</summary>
    Mcp = 32,

    /// <summary>
    /// <see cref="SettingsField.Theme"/> was changed to another theme (2026-09-23): the screen starts
    /// over the way <c>/theme</c> and <c>/splash</c> do — a fresh session, the banner and splash in
    /// the new colours — since what is already drawn keeps the old ones. No reconnect.
    /// </summary>
    Theme = 64,
}

/// <summary>
/// The <c>/settings</c> and <c>/model</c> screens and every other picker: a list in the bottom pane
/// (<see cref="MenuPane"/>: <c>/settings</c> is one level — its rows under the General / LLM / TTS /
/// STT tabs of <see cref="TabFields"/>, the strip and keys the info pane's — a row's picker or typed
/// edit a second, ESC backs out one level, the notices on the pane's status line) — or, on a
/// console without the pane, a themed <see cref="SelectionPrompt{T}"/> over
/// <see cref="PromptResult{T}"/> at the flow end (one flat list: tabs are a pane thing) with the
/// fields edited on the <see cref="InputLine"/> and the notices in the transcript. One flow, two
/// hosts (<see cref="PickSettingAsync"/>, <see cref="PickAsync"/>, <see cref="EditTextAsync"/>,
/// <see cref="Sink"/>); every accepted change one <see cref="AppSettings.Update"/>.
///
/// <para>The menu is rebuilt from <see cref="AppSettings.Current"/> every time it is shown, never
/// from a snapshot captured earlier.</para>
///
/// <para>A field that a variable or flag overrides for this launch is labelled so, and saving it
/// says the override still wins. The model list is never empty: the current id is always offered,
/// so a server that lists nothing (or wants a key) still lets the user type one.</para>
/// </summary>
internal sealed partial class SettingsMenu
{
    // The labels and the key hints: the pane shows the label as its title and the keys in its hint
    // row; the prompt host joins them (PromptTitle). Every pane's title leads with its glyph since
    // later on 2026-09-21 (the user's ask; the toolbar's where the pane has one), so the crumbs
    // (Breadcrumb) carry it too. Pinned.
    public const string Title = ChatScreen.SettingsToolGlyph + " Settings";
    public const string TitleKeys = "Enter = edit · ESC = close";

    /// <summary>The settings list's hint on the pane, where the rows sit under tabs.</summary>
    public const string TabKeys = "Enter = edit · ←/→ tabs · ESC = close";
    public const string EditKeys = "Enter = save · ESC = back";
    public const string PickKeys = "Enter = choose · ESC = back";

    /// <summary>The hint of a picker with a <c>(none)</c> row (<c>TTS voice 2</c>): Backspace moves the cursor to it.</summary>
    public const string NoneKeys = "Enter = choose · Backspace = none · ESC = back";
    public const string SwitchKeys = "Enter = switch · ESC = back";
    public const string KeepKeys = "Enter = choose · ESC = keep";

    /// <summary>The yes/no pane's hint (<see cref="ConfirmAsync"/>). Pinned.</summary>
    public const string ConfirmKeys = "y / n = pick · Enter = choose · ESC = no";

    /// <summary>The yes/no pane's rows, <c>No</c> first — the row the cursor opens on. Pinned.</summary>
    public static readonly IReadOnlyList<string> ConfirmRows = ["No", "Yes"];

    /// <summary>The yes/no pane's <see cref="MenuPage.Hotkeys"/>: <c>n</c> moves the cursor to <c>No</c>, <c>y</c> to <c>Yes</c> (Enter still picks). Pinned.</summary>
    public static readonly IReadOnlyDictionary<char, int> ConfirmHotkeys = new Dictionary<char, int> { ['n'] = 0, ['y'] = 1 };

    /// <summary>The status line for a settings row that cannot change while a reply runs (a reconnect, the profile, the sandbox, the tools flip). Pinned.</summary>
    public const string NotWhileReplyRunsNotice = "(not while a reply runs)";
    public const string ModelTitle = "🤖 Model";

    /// <summary>The <c>/reasoning</c> picker's label; ESC keeps the level in use. The settings row's level is <see cref="Breadcrumb"/> over <see cref="FieldName"/>.</summary>
    public const string ReasoningTitle = "💭 LLM reasoning";

    /// <summary>The title of <c>/theme</c>'s picker (2026-09-23). Pinned.</summary>
    public const string ThemeTitle = "🎨 Theme";

    /// <summary>What <c>/theme &lt;name&gt;</c> answers to a word that is not one of <see cref="ThemeName.Names"/>. Pinned.</summary>
    public static string ThemeNameError(string name) => $"No theme named \"{name}\". /theme takes " + string.Join(", ", ThemeName.Names[..^1]) + " or " + ThemeName.Names[^1] + ", or nothing to pick from a list.";

    /// <summary>What <c>/theme</c> says when the theme picked is the one already in force: nothing is cleared. Pinned.</summary>
    public static string ThemeAlreadyNotice(string name) => $"Theme: {name} (already in force)";

    /// <summary>What <c>/reasoning &lt;level&gt;</c> answers to a word that is not one of <see cref="Llm.ReasoningLevel.Levels"/>. Pinned.</summary>
    public static readonly string ReasoningLevelError = "/reasoning takes " + string.Join(", ", Llm.ReasoningLevel.Levels[..^1]) + " or " + Llm.ReasoningLevel.Levels[^1] + ", or nothing to pick from a list.";
    public const string MenusNeedTerminalError = "Menus need an interactive ANSI terminal; edit profile.json instead.";
    public const string NoUrlError = "No LLM endpoint. Set the URL in /settings first.";
    public const string NoModelsListedError = "The server lists no models; use /model <id>.";
    public const string UnchangedNotice = "unchanged";
    public const string TtsSpeedRangeError = "must be a speed multiplier between 0.5 and 2";
    public const string TtsVoiceMixRangeError = "must be a whole number from 0 to 100 (the primary voice's share)";
    public const string SttInterruptEchoGuardRangeError = "must be a whole number from 50 to 100 (100 = only the exact phrase counts as an echo)";
    public const string SttInterruptConfirmRangeError = "must be a whole number of milliseconds from 0 to 2000";
    public const string ContextLengthRangeError = "must be 0 (the server's figure) or a whole number of tokens";
    public const string PushToTalkKeyError = "must be one of F1–F10, Insert, Home, End, PageUp or PageDown";

    /// <summary>How the menu shows <see cref="AppSettingsData.LlmContextLength"/> at 0: the server's figure is used.</summary>
    public const string DetectedContextLengthLabel = "(from the server)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ReflectionMinToolCalls"/>. Pinned.</summary>
    public static readonly string ReflectionMinToolCallsRangeError = "must be " + AppSettingsData.MinReflectionMinToolCalls.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxReflectionMinToolCalls.ToString(CultureInfo.InvariantCulture) + " tool calls";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ReflectionCooldownMinutes"/>. Pinned.</summary>
    public static readonly string ReflectionCooldownMinutesRangeError = "must be 0 (off) or 1 to " + AppSettingsData.MaxReflectionCooldownMinutes.ToString(CultureInfo.InvariantCulture) + " minutes";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ReflectionMaxRequests"/>. Pinned.</summary>
    public static readonly string ReflectionMaxRequestsRangeError = "must be " + AppSettingsData.MinReflectionMaxRequests.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxReflectionMaxRequests.ToString(CultureInfo.InvariantCulture) + " requests";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ReflectionWindow"/>. Pinned.</summary>
    public static readonly string ReflectionWindowRangeError = "must be " + AppSettingsData.MinReflectionWindow.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxReflectionWindow.ToString(CultureInfo.InvariantCulture) + " turns";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.LlmCompactKeepRecent"/>. Pinned.</summary>
    public static readonly string LlmCompactKeepRecentRangeError = "must be 0 to " + Llm.ConversationHistory.DefaultMaxTurns.ToString(CultureInfo.InvariantCulture) + " turns";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.LlmAutoCompactPercent"/>. Pinned.</summary>
    public const string LlmAutoCompactPercentRangeError = "must be 0 (off) or 1 to 100 percent";

    /// <summary>The highest fixed <see cref="SettingsField.LlmMaxTurns"/>.</summary>
    public const int LlmMaxTurnsLimit = 500;

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.LlmMaxTurns"/>. Pinned.</summary>
    public static readonly string LlmMaxTurnsRangeError = "must be " + LlmMaxTurnsAutoLabel + " (0) or 1 to " + LlmMaxTurnsLimit.ToString(CultureInfo.InvariantCulture) + " turns";

    /// <summary>How the menu shows <see cref="AppSettingsData.LlmMaxTurns"/> at 0, and the word its edit accepts for it.</summary>
    public const string LlmMaxTurnsAutoLabel = "auto";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.BotChatNonTtsDelaySeconds"/>. Pinned.</summary>
    public static readonly string BotChatNonTtsDelayRangeError = "must be 0 (off) or 1 to " + AppSettingsData.MaxBotChatNonTtsDelaySeconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.LlmRequestTimeoutSeconds"/> / <see cref="SettingsField.LlmTurnTimeoutSeconds"/>: each field its own ceiling. Pinned.</summary>
    public static readonly string LlmRequestTimeoutRangeError = TimeoutRangeError(Llm.LlmTimeouts.MaxRequestSeconds);
    public static readonly string LlmTurnTimeoutRangeError = TimeoutRangeError(Llm.LlmTimeouts.MaxTurnSeconds);

    private static string TimeoutRangeError(double max) =>
        "must be a number of seconds in (0, " + max.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.LlmMaxToolIterations"/>. Pinned.</summary>
    public static readonly string MaxToolIterationsRangeError =
        "must be " + AppSettingsData.MinToolIterations.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxToolIterationsCap.ToString(CultureInfo.InvariantCulture) + " round trips";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.FileTreeMaxLength"/>. Pinned.</summary>
    public static readonly string TreeMaxLengthRangeError =
        "must be " + WorkingDirectory.MinTreeLength.ToString(CultureInfo.InvariantCulture) + " to " + WorkingDirectory.MaxTreeLength.ToString(CultureInfo.InvariantCulture) + " entries";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.PastePreviewLines"/>. Pinned.</summary>
    public static readonly string PastePreviewLinesRangeError =
        "must be 0 to " + PasteBlocks.MaxPreviewLines.ToString(CultureInfo.InvariantCulture) + " lines";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ToolCollapseCount"/>. Pinned.</summary>
    public static readonly string ToolCollapseCountRangeError =
        "must be " + AppSettingsData.MinToolCollapseCount.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxToolCollapseCount.ToString(CultureInfo.InvariantCulture) + " lines (0 = off)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.CodeCollapseCount"/>. Pinned.</summary>
    public static readonly string CodeCollapseCountRangeError =
        "must be " + AppSettingsData.MinCodeCollapseCount.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxCodeCollapseCount.ToString(CultureInfo.InvariantCulture) + " lines (0 = off)";

    /// <summary>How the menu shows <see cref="AppSettingsData.LlmAutoCompactPercent"/> at 0.</summary>
    public const string CompactAtOffLabel = "off";
    public const string WorkingDirectoryError = "must be a full path, or empty for the profile's files folder";

    /// <summary>
    /// How the menu shows an empty <see cref="AppSettingsData.WorkingDirectory"/>: the folder it
    /// resolves to (<see cref="WorkingDirectory.Resolve"/>) in parentheses, so the row says where
    /// the files go rather than that it is the default. Pinned.
    /// </summary>
    public static string DefaultWorkingDirectoryLabel(string profileDirectory) =>
        "(" + WorkingDirectory.Resolve("", profileDirectory) + ")";

    /// <summary>The <c>/cwd</c> line's tag on the default (the path is already on the line).</summary>
    public const string ProfileFolderNote = "(profile folder)";
    public const string ListingModelsLabel = "listing models";
    public const string ListingVoicesLabel = "listing voices";

    /// <summary>The first row of the secondary-voice picker, and how the menu shows an empty secondary voice.</summary>
    public const string NoSecondaryVoice = "(none)";

    /// <summary>The <see cref="SettingsField.TtsVoicePreset"/> row's value when the four TTS voice settings match no preset (2026-09-27).</summary>
    public const string CustomPreset = "(custom)";

    /// <summary>The notice when there is no preset to pick (a home <c>voice_presets.json</c> with none that passes).</summary>
    public static readonly string NoVoicePresetsNotice = $"{NoticeGlyphs.Tts}No voice presets to pick; see the log for the skipped entries.";

    /// <summary>The <see cref="SettingsField.TtsVoicePreset"/> row's value: the preset the saved four match (<see cref="Speech.VoicePresets.Match"/>), or <see cref="CustomPreset"/>.</summary>
    private static string PresetValue(AppSettingsData data, string profileDirectory) =>
        Speech.VoicePresets.Match(Speech.VoicePresets.Load(Profiles.HomeOf(profileDirectory)), data)?.Name ?? CustomPreset;

    /// <summary>The status line after a preset is picked: <c>TTS voice preset: neon · af_heart + am_eric · 80 % / 20 % · 1.2</c>.</summary>
    public static string PresetSavedNotice(Speech.VoicePreset preset) => $"{FieldName(SettingsField.TtsVoicePreset)}: {PresetLabel(preset)}";

    /// <summary>One preset picker row: <c>neon · af_heart + am_eric · 80 % / 20 % · 1.2</c> (the second voice and the mix left out when there is none).</summary>
    public static string PresetLabel(Speech.VoicePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        string voices = preset.Voice2.Length == 0 ? preset.Voice : $"{preset.Voice} + {preset.Voice2} · {Mix(preset.Mix)}";
        return $"{preset.Name} · {voices} · {Speed(preset.Speed)}";
    }

    /// <summary>
    /// The picker's rows as a table (2026-09-27, the user's ask: the <see cref="PresetLabel"/> rows did not line up):
    /// name, voice, <c>+ voice 2</c>, mix and speed, each column padded to its widest cell and two spaces apart; a preset
    /// with no second voice leaves that column and the mix blank. Trailing blanks trimmed.
    /// </summary>
    public static IReadOnlyList<string> PresetRows(IReadOnlyList<Speech.VoicePreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var cells = presets.Select(p => new[]
        {
            p.Name,
            p.Voice,
            p.Voice2.Length == 0 ? "" : "+ " + p.Voice2,
            p.Voice2.Length == 0 ? "" : Mix(p.Mix),
            Speed(p.Speed),
        }).ToList();
        var widths = Enumerable.Range(0, 5).Select(c => cells.Count == 0 ? 0 : cells.Max(r => TextCells.Width(r[c]))).ToArray();
        return cells.Select(r => string.Join("  ", r.Select((cell, c) => cell + new string(' ', widths[c] - TextCells.Width(cell)))).TrimEnd()).ToList();
    }

    /// <summary>What the voice pickers speak in the highlighted voice, and the mix and speed rows in the saved blend at the saved speed, while <see cref="SettingsField.TtsVoicePreview"/> is on. Pinned.</summary>
    public const string VoicePreviewText = "Hello. I am Neon, your friendly and concise terminal sidekick.";

    /// <summary><c>/profile</c>'s picker label; ESC keeps the loaded profile. The settings row's level is <see cref="Breadcrumb"/> over <see cref="FieldName"/> (no glyph under the crumb) + <see cref="SwitchKeys"/>.</summary>
    public const string ProfileTitle = "🪪 Profile";
    public const string ProfileKeys = "Enter = switch · ESC = keep";

    /// <summary>The <c>/server</c> picker's label; ESC keeps the server in use.</summary>
    public const string ServerTitle = "🖥️ LLM server";

    /// <summary>The startup picker's label, when several servers answered a blank URL, or any did at the app's start (2026-09-23; "Several LLM servers answered" until then); ESC takes the first listed, as before.</summary>
    public const string StartupServerTitle = "🖥️ Pick an LLM server";
    public const string StartupServerKeys = "Enter = choose · ESC = the first listed";

    private static readonly SettingsField[] Fields = Enum.GetValues<SettingsField>();

    /// <summary>The strip titles, one per <see cref="SettingsTab"/> (five since 2026-09-19: Ask, Files and Web are <c>/tools</c>' tabs, <see cref="ToolsText.TabTitles"/>, and Skills is <c>/skills</c>' Options tab, <see cref="SkillsText.OptionsTabTitle"/>). Pinned.</summary>
    public static readonly IReadOnlyList<string> TabTitles = ["General", "Sessions", "LLM", "TTS", "STT", ClaudeApiTabTitle, "Botchat"];

    /// <summary>The Claude API tab's strip title (2026-09-27, the user's name for it). Pinned.</summary>
    public const string ClaudeApiTabTitle = "Claude (API)";

    /// <summary>
    /// The rows of each tab on the pane, indexed by <see cref="SettingsTab"/>, in the order shown
    /// (General, Sessions, LLM, TTS, STT — the user's order, 2026-09-18: Sessions right after General; the Ask,
    /// Files and Web tabs are <c>/tools</c>' since 2026-09-19, <see cref="ToolsTabFields"/>, and the Skills tab
    /// <c>/skills</c>' Options tab since later that day, <see cref="SkillsTabFields"/>).
    /// General is spelled out (the profile and what a new one copies, then where its files live, then the message queue's switch and its cancel mode (2026-09-18, the user's place: right under the working directory), then the switches and pickers (<c>Mouse in menus</c> sat among them until 2026-09-21, when the mouse became the pane's for good), the
    /// transcript's Markdown and the paste preview, then the two line conveniences of 2026-09-18 — the hidden <c>/exit</c>, the typo intercept —, the command history's switch (2026-09-25), the welcome splash (the user's order, later that day), the banner's working directory, the theme (2026-09-23; last until later on 2026-09-27, when the user moved it under the toolbar's switch), the draft editor (2026-09-19), the image viewer and the themed-viewer switch last (later on 2026-09-27)); LLM
    /// is spelled out too: the scan mode (where a blank URL looks, so it sits above the URL), the
    /// <see cref="IsLlmField"/> rows, the compact rows, then <see cref="SettingsField.LlmOfferTools"/> ABOVE
    /// <see cref="SettingsField.LlmToolCompactType"/> (the user's order, 2026-09-15), the round-trip cap and the fun
    /// verbs last (none of those a reconnect); TTS is spelled out (the user's order, 2026-09-16): the switch, the
    /// source, the server's URL, the preview toggle (no reconnect), then the voice preset (2026-09-27, just above the voice), the voices, the mix and the speed; STT is the <see cref="IsVoiceField"/>
    /// fields in enum order; Sessions (2026-09-18, last that morning, second since) is the
    /// logging switch, the retention days, the naming mode, the show-name picker under it (later that day), the tool switch and the search cap (the user's order, 2026-09-18). With <see cref="SkillsTabFields"/> and <see cref="ToolsTabFields"/> they are every <see cref="SettingsField"/> once (pinned).
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<SettingsField>> TabFields =
    [
        [SettingsField.Profile, SettingsField.NewProfileMode, SettingsField.WorkingDirectory, SettingsField.QueueMessages, SettingsField.QueueCancelMode, SettingsField.Memory, SettingsField.CopyUserPrompt, SettingsField.ShowImageThumbnails, SettingsField.ImageThumbnailSize, SettingsField.TranscriptMarkdown, SettingsField.PastePreviewLines, SettingsField.HideExitAutocomplete, SettingsField.CommandTypoIntercept, SettingsField.KeepCommandHistory, SettingsField.WelcomeSplash, SettingsField.ShowWorkingDirectory, SettingsField.ShowToolbar, SettingsField.Theme, SettingsField.DraftEditor, SettingsField.ImageEditor, SettingsField.ThemedViewer],
        [SettingsField.SessionLogging, SettingsField.SessionRetentionDays, SettingsField.SessionNamingMode, SettingsField.SessionShowName, SettingsField.SessionTool, SettingsField.SessionSearchMaxResults, SettingsField.SessionSaveThinking],
        [SettingsField.LlmScanMode, SettingsField.LlmUrl, SettingsField.LlmModel, SettingsField.LlmApiKey, SettingsField.LlmReasoning, SettingsField.LlmRequestTimeoutSeconds, SettingsField.LlmTurnTimeoutSeconds, SettingsField.LlmContextLength, SettingsField.LlmMidTurnUsage, SettingsField.LlmCompactType, SettingsField.LlmCompactKeepRecent, SettingsField.LlmCompactShowSummary, SettingsField.LlmAutoCompactPercent, SettingsField.LlmMaxTurns, SettingsField.LlmOfferTools, SettingsField.LlmToolCompactType, SettingsField.LlmMaxToolIterations, SettingsField.LlmUseFunVerbs, SettingsField.LlmShowThinking, SettingsField.LlmPreserveThinking],
        [SettingsField.TtsOutput, SettingsField.TtsSource, SettingsField.TtsHttpUrl, SettingsField.TtsVoicePreview, SettingsField.TtsVoicePreset, SettingsField.TtsVoice, SettingsField.TtsVoice2, SettingsField.TtsVoiceMix, SettingsField.TtsSpeed],
        Fields.Where(IsVoiceField).ToArray(),
        [SettingsField.ClaudeApi, SettingsField.ClaudeApiKey, SettingsField.ClaudeApiMaxTokens, SettingsField.ClaudeApiPromptCaching],
        [SettingsField.BotChatLlmMode, SettingsField.BotChatImages, SettingsField.BotChatImageMode, SettingsField.BotChatTxt2ImgWorkflow, SettingsField.BotChatImg2ImgWorkflow, SettingsField.BotChatImg2ImgMode, SettingsField.BotChatImageAsync, SettingsField.BotChatNonTtsDelaySeconds, SettingsField.BotChatSkills, SettingsField.BotChatPreloadedSkills, SettingsField.BotChatSkillMode, SettingsField.BotChatVision],
    ];

    /// <summary>
    /// The rows of <c>/skills</c>' Options tab (2026-09-19, moved off <c>/settings</c> — its Skills tab, third since 2026-09-18, last from
    /// 2026-09-16 until then — the user's call, right after the Ask, Files and Web move), two lists since later on 2026-09-19 (the user's
    /// ask), indexed by <c>SkillsMenu</c>'s tab one down: the Options tab — the skills switch, the external-folder switch
    /// it governs, the compact mode, then the # list (2026-09-17; the skill slash commands sat beside it until later on 2026-09-18), the delete switch
    /// (2026-09-18, the pane's scope picker) — and the Reflection tab — the reflection's eight rows (2026-09-17: the auto-learn switch, its reasoning level,
    /// window, min calls and max requests; the cooldown, its mode and the sessions switch, 2026-09-19; the verbose switch, 2026-09-17, went later still on 2026-09-19). Edited through <see cref="FieldsTab"/> / <see cref="EditAsync"/>
    /// under the <c>/skills</c> strip (<see cref="SkillsMenu"/>), the pickers titled <c>Skills › …</c> (<see cref="Root"/>); none of
    /// them is <see cref="RefusedMidTurn"/>. With <see cref="TabFields"/> and <see cref="ToolsTabFields"/> they are every
    /// <see cref="SettingsField"/> once (pinned); the flat no-pane list keeps them all.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<SettingsField>> SkillsTabFields =
    [
        [SettingsField.AgentSkills, SettingsField.ExternalSkills, SettingsField.SkillCompactMode, SettingsField.SkillHashMention],
        [SettingsField.ReflectionAutoLearn, SettingsField.ReflectionReasoning, SettingsField.ReflectionWindow, SettingsField.ReflectionMinToolCalls, SettingsField.ReflectionMaxRequests, SettingsField.ReflectionCooldownMinutes, SettingsField.ReflectionCooldownMode, SettingsField.ReflectionIncludesSessions, SettingsField.ReflectionYieldsToTurns, SettingsField.ReflectionEditsSupportingFiles],
    ];

    /// <summary>
    /// The rows of <c>/tools</c>' four settings tabs (2026-09-19, the Ask, Files and Web rows moved off <c>/settings</c> the user's call), indexed by
    /// <see cref="ToolsText.TabTitles"/> one down (Web, Files, Shell, Ask, Claude, Obsidian, ComfyUI, SQL, Git (native), Options — the user's order since 2026-09-27; Web, Files, Shell, Ask, Git (native), Obsidian, SQL, ComfyUI, Claude, Options before; alphabetical before 2026-09-21): Options (later on 2026-09-19) is the <c>$</c>-mention switch, and under it the tool-run fold (<c>Tool collapse count</c>, 2026-09-22, the user's place);
    /// Ask (2026-09-15) is the question tool's switch and its two caps;
    /// Files (2026-09-15) is the file-tools switch, the Safe edits switch (2026-09-17; Stale line number guard beside it until 2026-09-19, Always return
    /// line numbers between them until 2026-09-19), the two <c>/tree</c> rows (once General's last two), the @-mention folder mode
    /// (General's until 2026-09-17), the <c>/cwd browse</c> mode under it (2026-09-21) and the <c>view_image</c> cap last (2026-09-19); Web is the seven web rows (2026-09-15, once on General under Memory; the tab read Browser
    /// until later that day), the search method above the Web SearXNG URL it governs. The group switch stays each tab's first row.
    /// Since later still on 2026-09-19 (the user's ask) every Files and Web row carries its tab's word (<c>File /tree max length</c>, <c>Web SearXNG URL</c>, …) and the six JSON keys that
    /// differed followed (<c>FileTreeMaxLength</c>, <c>FileTreeShowSizes</c>, <c>FileMentionFolderMode</c>, <c>FileViewImageMaxPerCall</c>, <c>WebSearxngUrl</c>) — no migration, the old key skipped on load.
    /// Git (2026-09-20; Git (native) since 2026-09-21, its rows <c>Git native …</c>) is its switch, the diff cap, the log cap and the identity pair;
    /// Shell (2026-09-21) is the policy (its switch), the allowed list, the outside-paths police (2026-09-22), the default shell, the two timeouts and the output cap,
    /// then the script rows: the languages, their timeout, the tool bridge switch (later that day) and the tool-call cap it governs.
    /// Claude (2026-09-27, the user's call: <c>/claude</c>'s four rows off <c>/settings</c>, named <c>Claude command …</c>) is those four, then
    /// the advisor's six (<c>claude_advisor</c>): its switch, the context, the per-turn cap, the model, the effort and the confirm switch.
    /// With <see cref="TabFields"/> and <see cref="SkillsTabFields"/> they are every <see cref="SettingsField"/> once (pinned); the flat no-pane list keeps them all.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<SettingsField>> ToolsTabFields =
    [
        [SettingsField.WebTools, SettingsField.WebBrowserMode, SettingsField.WebBrowserPath, SettingsField.WebBrowserNetworkMode, SettingsField.WebSearchMethod, SettingsField.WebSearxngUrl, SettingsField.WebSearchMaxResults],
        [SettingsField.FileTools, SettingsField.FileSafeEdits, SettingsField.FileTreeMaxLength, SettingsField.FileTreeShowSizes, SettingsField.FileMentionFolderMode, SettingsField.FileBrowserMode, SettingsField.FileViewImageMaxPerCall],
        [SettingsField.ShellCommandPolicy, SettingsField.ShellCommandAllowed, SettingsField.ShellPoliceOutsidePaths, SettingsField.ShellPreferNative, SettingsField.ShellDefault, SettingsField.ShellTimeoutSeconds, SettingsField.ShellForegroundCapSeconds, SettingsField.ShellOutputMaxChars, SettingsField.ShellCodeLanguages, SettingsField.ShellCodeTimeoutSeconds, SettingsField.ShellToolBridge, SettingsField.ShellCodeMaxToolCalls],
        [SettingsField.AskUser, SettingsField.AskMaxQuestions, SettingsField.AskMaxChoices],
        [SettingsField.ClaudeExecutable, SettingsField.ClaudePermissions, SettingsField.ClaudeModel, SettingsField.ClaudeEffort, SettingsField.ClaudeAdvisor, SettingsField.ClaudeAdvisorContext, SettingsField.ClaudeAdvisorCallsPerTurn, SettingsField.ClaudeAdvisorModel, SettingsField.ClaudeAdvisorEffort, SettingsField.ClaudeAdvisorConfirm],
        [SettingsField.ObsidianTools, SettingsField.ObsidianVault, SettingsField.ObsidianAllowDelete],
        [SettingsField.ComfyTools, SettingsField.ComfyUrl, SettingsField.ComfyWorkflowsOffered, SettingsField.ComfyAddWorkflow, SettingsField.ComfyCaretMention, SettingsField.ComfyTimeoutSeconds, SettingsField.ComfyMaxPicturesPerCall, SettingsField.ComfyReinforceNegatives, SettingsField.ComfyShowPrompts, SettingsField.ComfyPictureStrip, SettingsField.ComfyOutputFolder],
        [SettingsField.SqlTools, SettingsField.SqlConnectionsOffered, SettingsField.SqlDefaultConnection, SettingsField.SqlSetPassword, SettingsField.SqlAddConnection, SettingsField.SqlPercentMention, SettingsField.SqlQueryMaxRows, SettingsField.SqlQueryTimeoutSeconds, SettingsField.SqlConnectionsProfile, SettingsField.SqlConnectionsGlobal],
        [SettingsField.GitNativeTools, SettingsField.GitNativeDiffMaxLines, SettingsField.GitNativeLogMaxCommits, SettingsField.GitNativeEmail, SettingsField.GitNativeName],
        [SettingsField.ToolsDollarMention, SettingsField.ToolCollapseCount, SettingsField.CodeCollapseCount],
    ];

    /// <summary>
    /// The rows of <c>/mcp</c>' Options tab (2026-09-20), the <see cref="ToolsTabFields"/> shape with one list:
    /// the master switch <c>MCP servers</c> first, then <c>MCP connect timeout (s)</c>. With <see cref="TabFields"/>,
    /// <see cref="SkillsTabFields"/> and <see cref="ToolsTabFields"/> they are every <see cref="SettingsField"/> once (pinned);
    /// the flat no-pane list keeps them all.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<SettingsField>> McpTabFields =
    [
        [SettingsField.McpServers, SettingsField.McpConnectTimeoutSeconds],
    ];

    private readonly IAnsiConsole _console;
    private readonly AppSettings _settings;
    private readonly Func<SettingsField, string?> _overriddenBy;
    private readonly InputLine _input;
    private readonly TranscriptRenderer _transcript;
    private readonly SpeechSession _speech;
    private readonly MenuPane _pane;
    private readonly Func<CancellationToken, Task<string?>>? _browseFolder;
    private readonly Func<string, CancellationToken, Task<string?>>? _browseVault;
    private readonly Action<string>? _openFile;
    private readonly Func<Sql.SqlNamedConnection, CancellationToken, Task<Sql.SqlRun>> _testSqlConnection;
    private readonly Func<Comfy.ComfyClient?> _comfyClient;
    // The skills a botchat sees, for the preloaded-skills checklist (2026-09-27); none when the host gives no catalog.
    private readonly Func<IReadOnlyList<Skills.Skill>> _botChatSkills;
    private readonly Func<string, string?> _locateBrowser;
    private readonly Func<IReadOnlySet<string>> _installedShells;
    private readonly Func<IReadOnlySet<string>> _installedLanguages;
    // Set for the visit of a /settings opened while a reply runs (ShowAsync's midTurn): the rows
    // that would reconnect, switch the profile, move the sandbox or reshape the history answer
    // NotWhileReplyRunsNotice instead, and no voice preview plays over the reply's speech.
    private bool _midTurn;

    /// <summary>The voice picker's preview chain (see <see cref="TryPickVoiceAsync"/>): one step per highlight, run one after the other.</summary>
    private Task _preview = Task.CompletedTask;

    /// <summary>The latest highlight's number; a step whose number is older is superseded and stays silent.</summary>
    private int _previewSerial;

    /// <param name="overriddenBy">The variable or flag that outranks the saved value of a field this launch, or null.</param>
    /// <param name="speech">Lists the voices for the picker and speaks its preview.</param>
    /// <param name="pane">The menu host in the bottom pane; disabled (no pane), every list is a Spectre prompt.</param>
    /// <param name="locateBrowser">What the empty <c>Web browser path</c> row names: the headless browser auto-detection finds (<see cref="Web.IHeadlessBrowser.Locate"/>); null = the real one.</param>
    /// <param name="installedShells">The shells the <c>Shell default</c> picker marks as found (their <see cref="Shell.ShellKinds.Names"/> words; the screen's <see cref="Shell.Interpreters"/>, 2026-09-21); null = all three marked found.</param>
    /// <param name="installedLanguages">The languages the <c>Shell code languages</c> list marks as found (their <see cref="Shell.CodeLanguages.Names"/> words); null = all three marked found.</param>
    /// <param name="testSqlConnection">What the <c>SQL add connection</c> summary's test runs over the unsaved draft (later on 2026-09-23), its typed password in it as a plain <c>file</c> value; null = a real <see cref="Sql.SqlAccess"/> run of <see cref="SqlTestQuery"/>.</param>
    /// <param name="openFile">What the SQL tab's edit rows open <c>sql.json</c> with (2026-09-23): the screen's editor opener; null = the rows say there is none.</param>
    /// <param name="browseFolder">The folder picker the <c>Working directory (cwd)</c> row opens (2026-09-22, the user's ask): the screen's <c>/cwd browse</c> tree, returning what to save — <c>""</c> for the profile's folder, a full path, or null for nothing chosen. Null (and a console with no pane) falls back to the typed path the row asked for until then.</param>
    public SettingsMenu(IAnsiConsole console, AppSettings settings, Func<SettingsField, string?> overriddenBy, InputLine input, TranscriptRenderer transcript, SpeechSession speech, MenuPane pane, Func<string, string?>? locateBrowser = null, Func<IReadOnlySet<string>>? installedShells = null, Func<IReadOnlySet<string>>? installedLanguages = null, Func<CancellationToken, Task<string?>>? browseFolder = null, Func<string, CancellationToken, Task<string?>>? browseVault = null, Action<string>? openFile = null, Func<Sql.SqlNamedConnection, CancellationToken, Task<Sql.SqlRun>>? testSqlConnection = null, Func<Comfy.ComfyClient?>? comfyClient = null, Func<IReadOnlyList<Skills.Skill>>? botChatSkills = null)
    {
        // The ComfyUI client the add-workflow wizard lists the server's models and runs its test with (later on 2026-09-24): the screen's, so a stub reaches it in tests; null = none, the wizard says there is no server.
        _comfyClient = comfyClient ?? (() => null);
        _botChatSkills = botChatSkills ?? (() => []);
        _testSqlConnection = testSqlConnection ?? TestSqlConnectionAsync;
        _browseFolder = browseFolder;
        _openFile = openFile;
        _browseVault = browseVault;
        _locateBrowser = locateBrowser ?? new Web.HeadlessBrowser().Locate;
        _installedShells = installedShells ?? (() => new HashSet<string>(Shell.ShellKinds.Names, StringComparer.Ordinal));
        _installedLanguages = installedLanguages ?? (() => new HashSet<string>(Shell.CodeLanguages.Names, StringComparer.Ordinal));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _overriddenBy = overriddenBy ?? throw new ArgumentNullException(nameof(overriddenBy));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        Flow = _transcript;
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
    }

    /// <summary>Where a notice goes: the pane's status line while a menu is open there, else the transcript.</summary>
    private INoticeSink Sink => _pane.IsOpen ? _pane : Flow;

    /// <summary>
    /// Where a line goes when no pane is open to take it: the transcript by default; the screen
    /// sets its deferring sink, so a picker that closes with its answer (<c>/reasoning</c> from
    /// a pane opened mid-turn) never writes into a streaming reply from the key-reading task.
    /// </summary>
    public INoticeSink Flow { get; set; }

    /// <summary>When the server lists nothing the voice row falls back to a typed name.</summary>
    public static string VoicesUnavailableNotice(string detail) => $"{NoticeGlyphs.Tts}The TTS server did not list voices ({detail}); type a voice name.";

    // ── Pinned statics ──────────────────────────────────────────────────────

    /// <summary>The prompt host's title: the label and the keys, three spaces apart (<c>Settings   Enter = edit or toggle · ESC = close</c>).</summary>
    public static string PromptTitle(string label, string keys) => label + "   " + keys;

    /// <summary>
    /// The pane this menu is editing for, the first word of every second level's label: <see cref="Title"/>
    /// (<c>Settings › Web browser mode</c>), or <see cref="ToolsText.Label"/> while <see cref="ToolsMenu"/> hosts the
    /// Ask / Files / Web rows (<c>Tools › Web browser mode</c>, 2026-09-19); that host sets it for its run and restores it.
    /// </summary>
    public string Root { get; set; } = Title;

    /// <summary><see cref="Breadcrumb"/> under <see cref="Root"/>.</summary>
    private string Crumb(string label) => Root + " › " + label;

    /// <summary>Whether a change to <paramref name="field"/> needs the LLM session rebuilt.</summary>
    public static bool IsLlmField(SettingsField field) =>
        field is SettingsField.LlmUrl or SettingsField.LlmModel or SettingsField.LlmApiKey
            or SettingsField.LlmRequestTimeoutSeconds or SettingsField.LlmTurnTimeoutSeconds or SettingsField.LlmContextLength or SettingsField.LlmReasoning
            or SettingsField.ClaudeApi or SettingsField.ClaudeApiKey or SettingsField.ClaudeApiMaxTokens or SettingsField.ClaudeApiPromptCaching;

    /// <summary>Whether a change to <paramref name="field"/> needs the speech session re-probed.</summary>
    public static bool IsTtsField(SettingsField field) =>
        field is SettingsField.TtsHttpUrl or SettingsField.TtsVoice or SettingsField.TtsOutput or SettingsField.TtsSpeed
            or SettingsField.TtsVoice2 or SettingsField.TtsVoiceMix or SettingsField.TtsSource or SettingsField.TtsVoicePreset;

    /// <summary>Whether a change to <paramref name="field"/> needs the voice session re-probed (the wake word is part of it).</summary>
    public static bool IsVoiceField(SettingsField field) =>
        field is SettingsField.SttInput or SettingsField.SttPushToTalkKey or SettingsField.SttWhisperModel
            or SettingsField.SttWake or SettingsField.SttWakePhrase or SettingsField.SttInterrupt or SettingsField.SttInterruptEchoGuard
            or SettingsField.SttInterruptConfirmMs or SettingsField.SttVoskModel;

    /// <summary>Whether a change to <paramref name="field"/> connects or disconnects the MCP servers (2026-09-20): the master switch alone — the timeout is read at the next connect.</summary>
    public static bool IsMcpField(SettingsField field) => field is SettingsField.McpServers;

    /// <summary>The settings-menu wording for a bad wake phrase. Pinned.</summary>
    public const string WakePhraseError = "must be one to three words of letters";

    /// <summary>
    /// Whether <paramref name="phrase"/> can be the wake phrase: one to three words of letters
    /// only, after <see cref="WakeWordMatch.NormalizePhrase"/>. Digits and punctuation never
    /// come back from the recogniser as such, so a phrase holding them could never be heard.
    /// </summary>
    public static bool IsWakePhraseCandidate(string? phrase)
    {
        string normalized = WakeWordMatch.NormalizePhrase(phrase);
        if (normalized.Length == 0)
        {
            return false;
        }

        var words = normalized.Split(' ');
        if (words.Length > 3)
        {
            return false;
        }

        foreach (var word in words)
        {
            foreach (char c in word)
            {
                if (!char.IsLetter(c))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The keys the push-to-talk picker offers, in row order: the function keys a terminal
    /// actually passes through (F11 is fullscreen, F12 is taken by most of them) and the
    /// navigation cluster the input line does nothing with while it is empty. F4 is the default.
    /// </summary>
    public static readonly ConsoleKey[] PushToTalkKeys =
    {
        ConsoleKey.F1, ConsoleKey.F2, ConsoleKey.F3, ConsoleKey.F4, ConsoleKey.F5,
        ConsoleKey.F6, ConsoleKey.F7, ConsoleKey.F8, ConsoleKey.F9, ConsoleKey.F10,
        ConsoleKey.Insert, ConsoleKey.Home, ConsoleKey.End, ConsoleKey.PageUp, ConsoleKey.PageDown,
    };

    /// <summary>Whether <paramref name="key"/> can be the push-to-talk key: one of <see cref="PushToTalkKeys"/>. A saved name outside the list falls back to F4 (<see cref="VoiceSession.ParsePushToTalk"/>).</summary>
    public static bool IsPushToTalkCandidate(ConsoleKey key) => Array.IndexOf(PushToTalkKeys, key) >= 0;

    /// <summary>The spaces between the longest label and its value.</summary>
    private const int LabelGap = 2;

    /// <summary>
    /// The label column of a settings row: the longest <see cref="FieldName"/> ("LLM request timeout (s)", 23)
    /// plus <see cref="LabelGap"/> before the value — computed, so a renamed row can never close the gap.
    /// </summary>
    public static readonly int LabelWidth = Fields.Max(f => FieldName(f).Length) + LabelGap;

    /// <summary>The label column of one tab on the pane: that tab's longest <see cref="FieldName"/> plus <see cref="LabelGap"/>, so a short tab reads tight.</summary>
    public static int TabLabelWidth(SettingsTab tab) => LabelWidthOf(TabFields[(int)tab]);

    /// <summary>The label column of any list of rows (a <see cref="TabFields"/> or <see cref="ToolsTabFields"/> tab): the longest <see cref="FieldName"/> plus <see cref="LabelGap"/>.</summary>
    public static int LabelWidthOf(IReadOnlyList<SettingsField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return fields.Max(f => FieldName(f).Length) + LabelGap;
    }

    /// <summary>The first row's label: the loaded profile's name, padded like every other row, its directory dim in parentheses after it. Pinned.</summary>
    public static string ProfileLabel(string profileName, string profileDirectory) => ProfileLabel(profileName, profileDirectory, LabelWidth);

    /// <summary>The profile row padded to <paramref name="width"/> (a tab's column on the pane).</summary>
    public static string ProfileLabel(string profileName, string profileDirectory, int width) =>
        Markup.Escape(FieldName(SettingsField.Profile).PadRight(width))
        + Theme.ColorMarkup(Theme.Ink, Markup.Escape(profileName))
        + Theme.DimMarkup(" (" + profileDirectory + ")");

    /// <summary>The list for a console without menus: <c>profiles: default (current), work</c>. Pinned.</summary>
    public static string ProfileListLine(IReadOnlyList<string> names, string current)
    {
        ArgumentNullException.ThrowIfNull(names);
        return "profiles: " + string.Join(", ", names.Select(n => Profiles.NameEquals(n, current) ? n + " (current)" : n));
    }

    public static string AlreadyCurrentNotice(string profileName) => $"({NoticeGlyphs.Profile}already on profile \"{profileName}\")";

    /// <summary>The picker's name column: the longest <see cref="LlmServer.PortNames"/> value ("LM Studio" / "llama.cpp") plus two.</summary>
    public const int ServerNameWidth = 11;

    /// <summary>A server picker row: the name padded, the URL in ink, the probe's detail dimmed. Pinned.</summary>
    public static string ServerLabel(LlmServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Markup.Escape(server.Name.PadRight(ServerNameWidth)) + Theme.ColorMarkup(Theme.Ink, Markup.Escape(server.BaseUrl.ToString()))
            + Theme.DimMarkup("  " + Markup.Escape(server.Result.Detail));
    }

    /// <summary>The list for a console without menus: <c>LLM servers: LM Studio http://127.0.0.1:1234/v1, Ollama http://127.0.0.1:11434/v1</c>. Pinned.</summary>
    public static string ServerListLine(IReadOnlyList<LlmServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        return "LLM servers: " + string.Join(", ", servers.Select(s => s.Name + " " + s.BaseUrl));
    }

    /// <summary><c>/server &lt;url&gt;</c> with something that is not an absolute http(s) URL. Pinned.</summary>
    public static string ServerUrlError(string detail) => $"Not a usable server URL: {detail}";

    /// <summary><c>/server &lt;url&gt;</c> naming a server that did not answer; it is saved anyway, like a configured URL. Pinned.</summary>
    public static string ServerNotAnsweringWarning(Uri baseUrl, string detail) =>
        $"{baseUrl} did not answer /v1/models ({detail}); using it anyway because you asked.";

    public static string SwitchedNotice(string profileName) => $"({NoticeGlyphs.Profile}switched to profile \"{profileName}\"; conversation cleared)";

    public static bool IsToggle(SettingsField field) =>
        field is SettingsField.TtsOutput or SettingsField.SttInput or SettingsField.SttWake or SettingsField.SttInterrupt
            or SettingsField.Memory or SettingsField.CopyUserPrompt or SettingsField.ShowImageThumbnails
            or SettingsField.FileTreeShowSizes or SettingsField.LlmOfferTools or SettingsField.LlmUseFunVerbs or SettingsField.LlmShowThinking or SettingsField.LlmPreserveThinking
            or SettingsField.WebTools or SettingsField.TtsVoicePreview or SettingsField.FileTools or SettingsField.AskUser
            or SettingsField.AgentSkills or SettingsField.ExternalSkills or SettingsField.TranscriptMarkdown
            or SettingsField.FileSafeEdits
            or SettingsField.SkillHashMention or SettingsField.ReflectionAutoLearn
            or SettingsField.HideExitAutocomplete or SettingsField.CommandTypoIntercept or SettingsField.KeepCommandHistory or SettingsField.ShowWorkingDirectory or SettingsField.ShowToolbar or SettingsField.ThemedViewer
            or SettingsField.QueueMessages or SettingsField.SessionLogging or SettingsField.SessionTool or SettingsField.SessionSaveThinking
            or SettingsField.ToolsDollarMention or SettingsField.ReflectionIncludesSessions or SettingsField.ReflectionYieldsToTurns or SettingsField.ReflectionEditsSupportingFiles or SettingsField.McpServers or SettingsField.GitNativeTools
            or SettingsField.LlmCompactShowSummary or SettingsField.ShellToolBridge or SettingsField.ShellPoliceOutsidePaths or SettingsField.ShellPreferNative
            or SettingsField.ObsidianTools or SettingsField.ObsidianAllowDelete or SettingsField.SqlTools or SettingsField.SqlPercentMention or SettingsField.ComfyTools or SettingsField.ComfyReinforceNegatives or SettingsField.ComfyShowPrompts or SettingsField.ComfyCaretMention or SettingsField.ComfyPictureStrip
            or SettingsField.BotChatImages or SettingsField.BotChatImageAsync or SettingsField.BotChatSkills or SettingsField.BotChatVision or SettingsField.ClaudeAdvisor or SettingsField.ClaudeAdvisorConfirm
            or SettingsField.ClaudeApi or SettingsField.ClaudeApiPromptCaching;

    public static string FieldName(SettingsField field) => field switch
    {
        SettingsField.Profile => "Profile",
        SettingsField.LlmUrl => "LLM URL",
        SettingsField.LlmModel => "LLM model",
        SettingsField.LlmApiKey => "LLM API key",
        SettingsField.LlmRequestTimeoutSeconds => "LLM request timeout (s)",
        SettingsField.LlmTurnTimeoutSeconds => "LLM turn timeout (s)",
        SettingsField.LlmContextLength => "LLM context length",
        SettingsField.TtsHttpUrl => "TTS HTTP URL",
        SettingsField.TtsSource => "TTS source",
        SettingsField.TtsVoice => "TTS voice",
        SettingsField.TtsVoicePreset => "TTS voice preset",
        SettingsField.TtsOutput => "TTS output",
        SettingsField.SttInput => "STT input",
        SettingsField.SttWake => "STT wake",
        SettingsField.SttWakePhrase => "STT wake phrase",
        SettingsField.SttPushToTalkKey => "STT push-to-talk key",
        SettingsField.TtsSpeed => "TTS speed",
        SettingsField.SttWhisperModel => "STT whisper model",
        SettingsField.SttVoskModel => "STT vosk model",
        SettingsField.SttInterrupt => "STT interrupt",
        SettingsField.SttInterruptEchoGuard => "STT interrupt echo guard",
        SettingsField.SttInterruptConfirmMs => "STT interrupt confirm",
        SettingsField.LlmReasoning => "LLM reasoning",
        SettingsField.TtsVoice2 => "TTS voice 2",
        SettingsField.TtsVoiceMix => "TTS voice mix",
        SettingsField.Memory => "Memory",
        SettingsField.WorkingDirectory => "Working directory (cwd)",
        SettingsField.CopyUserPrompt => "Copy user prompt",
        SettingsField.DraftEditor => "Draft editor",
        SettingsField.ImageEditor => "Image viewer",   // "Image editor" until later still on 2026-09-24 (the user's call); the field and the setting keep the old name
        SettingsField.FileViewImageMaxPerCall => "File view image max (per call)",
        SettingsField.McpServers => "MCP servers",
        SettingsField.McpConnectTimeoutSeconds => "MCP connect timeout (s)",
        SettingsField.ShowImageThumbnails => "Show image thumbnails",
        SettingsField.LlmCompactType => "LLM compact type",
        SettingsField.LlmCompactKeepRecent => "LLM compact keep recent",
        SettingsField.LlmCompactShowSummary => "LLM compact show summary",
        SettingsField.LlmAutoCompactPercent => "LLM auto compact (%)",
        SettingsField.LlmMaxTurns => "LLM max turns",
        SettingsField.LlmToolCompactType => "LLM tool compact type",
        SettingsField.LlmMaxToolIterations => "LLM max tool iterations",
        SettingsField.ImageThumbnailSize => "Image thumbnail size",
        SettingsField.FileTreeMaxLength => "File /tree max length",
        SettingsField.FileTreeShowSizes => "File /tree show sizes",
        SettingsField.NewProfileMode => "New profile mode",
        SettingsField.LlmOfferTools => "LLM offer tools",
        SettingsField.LlmUseFunVerbs => "LLM use fun verbs",
        SettingsField.LlmShowThinking => "LLM show thinking",
        SettingsField.LlmPreserveThinking => "LLM preserve thinking",
        SettingsField.SessionSaveThinking => "Session save thinking",
        SettingsField.ClaudeExecutable => "Claude executable",
        SettingsField.ClaudePermissions => "Claude slash command permissions",
        SettingsField.ClaudeModel => "Claude slash command model",
        SettingsField.ClaudeEffort => "Claude slash command effort",
        SettingsField.ClaudeAdvisor => "Claude advisor tool",
        SettingsField.ClaudeAdvisorContext => "Claude advisor tool context",
        SettingsField.ClaudeAdvisorCallsPerTurn => "Claude advisor tool calls per turn",
        SettingsField.ClaudeAdvisorModel => "Claude advisor tool model",
        SettingsField.ClaudeAdvisorEffort => "Claude advisor tool effort",
        SettingsField.ClaudeAdvisorConfirm => "Claude advisor tool confirm",
        SettingsField.LlmScanMode => "LLM scan mode",
        SettingsField.WebTools => "Web tools",
        SettingsField.GitNativeTools => "Git native tools",
        SettingsField.GitNativeDiffMaxLines => "Git native diff max lines",
        SettingsField.ShellCommandPolicy => "Shell command policy",
        SettingsField.ShellCommandAllowed => "Shell allowed commands",
        SettingsField.ShellPoliceOutsidePaths => "Shell police outside paths",
        SettingsField.ShellPreferNative => "Shell prefer native tools",
        SettingsField.ShellDefault => "Shell default",
        SettingsField.ShellTimeoutSeconds => "Shell timeout (s)",
        SettingsField.ShellForegroundCapSeconds => "Shell foreground cap (s)",
        SettingsField.ShellOutputMaxChars => "Shell output max chars",
        SettingsField.ShellCodeLanguages => "Shell code languages",
        SettingsField.ShellCodeTimeoutSeconds => "Shell code timeout (s)",
        SettingsField.ShellToolBridge => "Shell tool bridge",
        SettingsField.ShellCodeMaxToolCalls => "Shell tool bridge max calls",   // the bridge's cap, named after the switch above it (later on 2026-09-21, the user's ask; "Shell code max tool calls" before)
        SettingsField.GitNativeLogMaxCommits => "Git native log max commits",
        SettingsField.GitNativeEmail => "Git native email",
        SettingsField.GitNativeName => "Git native name",
        SettingsField.ObsidianTools => "Obsidian tools",
        SettingsField.ObsidianVault => "Obsidian vault",
        SettingsField.SqlTools => "SQL tools",
        SettingsField.ComfyTools => "ComfyUI tools",
        SettingsField.ComfyUrl => "ComfyUI URL",
        SettingsField.ComfyTimeoutSeconds => "ComfyUI timeout (s)",
        SettingsField.ComfyMaxPicturesPerCall => "ComfyUI max pictures per call",
        SettingsField.ComfyReinforceNegatives => "ComfyUI reinforce negatives",
        SettingsField.ComfyShowPrompts => "ComfyUI show prompts",
        SettingsField.ComfyCaretMention => "ComfyUI ^-mention enabled",
        SettingsField.ComfyPictureStrip => "ComfyUI picture strip",
        SettingsField.BotChatLlmMode => "Botchat LLM mode",
        SettingsField.LlmMidTurnUsage => "LLM mid-turn usage",
        SettingsField.BotChatImages => "Botchat images enabled",
        SettingsField.BotChatImageMode => "Botchat image mode",
        SettingsField.BotChatTxt2ImgWorkflow => "Botchat txt2img workflow",
        SettingsField.BotChatImg2ImgWorkflow => "Botchat img2img workflow",
        SettingsField.BotChatImg2ImgMode => "Botchat img2img mode",
        SettingsField.BotChatImageAsync => "Botchat image async",
        SettingsField.BotChatNonTtsDelaySeconds => "Botchat non-TTS delay",
        SettingsField.BotChatSkills => "Botchat skills enabled",
        SettingsField.BotChatVision => "Botchat vision enabled",
        SettingsField.BotChatPreloadedSkills => "Botchat preloaded skills",
        SettingsField.BotChatSkillMode => "Botchat skill mode",
        SettingsField.ComfyOutputFolder => "ComfyUI output folder",
        SettingsField.ComfyWorkflowsOffered => "ComfyUI workflows offered",
        SettingsField.ComfyAddWorkflow => "ComfyUI add workflow",
        SettingsField.SqlDefaultConnection => "SQL default connection",
        SettingsField.SqlConnectionsOffered => "SQL connections offered",
        SettingsField.SqlSetPassword => "SQL set password",
        SettingsField.SqlAddConnection => "SQL add connection",
        SettingsField.SqlPercentMention => "SQL %-mention enabled",
        SettingsField.SqlQueryMaxRows => "SQL max rows",
        SettingsField.SqlQueryTimeoutSeconds => "SQL query timeout (s)",
        SettingsField.SqlConnectionsProfile => "SQL connections (profile)",
        SettingsField.SqlConnectionsGlobal => "SQL connections (global)",
        SettingsField.ObsidianAllowDelete => "Obsidian allow delete (.trash)",   // "Obsidian allow delete" until 2026-09-23 (the user's call: the row says where a delete goes)
        SettingsField.WebBrowserMode => "Web browser mode",
        SettingsField.WebBrowserPath => "Web browser path",
        SettingsField.WebBrowserNetworkMode => "Web browser network mode",
        SettingsField.WebSearxngUrl => "Web SearXNG URL",
        SettingsField.WebSearchMaxResults => "Web search max results",
        SettingsField.TtsVoicePreview => "TTS voice preview",
        SettingsField.FileTools => "File tools",
        SettingsField.WebSearchMethod => "Web search method",
        SettingsField.ClaudeApi => "Claude API",
        SettingsField.ClaudeApiKey => "Claude API key",
        SettingsField.ClaudeApiMaxTokens => "Claude API max tokens",
        SettingsField.ClaudeApiPromptCaching => "Claude API prompt caching",
        SettingsField.AskUser => "Ask user",
        SettingsField.AskMaxQuestions => "Ask max questions",
        SettingsField.AskMaxChoices => "Ask max choices per question",
        SettingsField.FileMentionFolderMode => "File @-mention folder mode",
        SettingsField.FileBrowserMode => "File browser/tree mode",   // "File browser mode" until 2026-09-23, when /tree came to follow it (the user's call)
        SettingsField.AgentSkills => "Agent skills",
        SettingsField.ExternalSkills => ExternalSkillsName,
        SettingsField.TranscriptMarkdown => "Transcript markdown",
        SettingsField.SkillCompactMode => "Skill compact mode",
        SettingsField.PastePreviewLines => "Paste preview lines",
        SettingsField.FileSafeEdits => "File safe edits",
        SettingsField.SkillHashMention => "#-mention enabled",
        SettingsField.ToolsDollarMention => "$-mention enabled",
        SettingsField.ToolCollapseCount => "Tool collapse count",
        SettingsField.CodeCollapseCount => "Code collapse count",
        SettingsField.ReflectionAutoLearn => "Reflection (auto-learn)",
        SettingsField.ReflectionReasoning => "Reflection reasoning",
        SettingsField.ReflectionWindow => "Reflection window",
        SettingsField.ReflectionMinToolCalls => "Reflection min tool calls",
        SettingsField.ReflectionMaxRequests => "Reflection max requests",
        SettingsField.ReflectionCooldownMinutes => "Reflection cooldown (minutes)",
        SettingsField.ReflectionCooldownMode => "Reflection cooldown mode",
        SettingsField.ReflectionIncludesSessions => "Reflection includes sessions",
        SettingsField.ReflectionYieldsToTurns => "Reflection yields to turns",
        SettingsField.ReflectionEditsSupportingFiles => "Reflection edit supporting files",   // the user's name (2026-09-27)
        SettingsField.HideExitAutocomplete => "Hide /exit autocomplete",
        SettingsField.CommandTypoIntercept => "Command typo intercept",
        SettingsField.KeepCommandHistory => "Keep command history",
        SettingsField.WelcomeSplash => "Welcome splash",
        SettingsField.ShowWorkingDirectory => "Working directory in header",
        SettingsField.ShowToolbar => "Show toolbar",
        SettingsField.Theme => "Theme",
        SettingsField.ThemedViewer => "Themed image viewer",   // the user's name, beside "Image viewer" (later on 2026-09-27)
        SettingsField.QueueMessages => "Queue messages",
        SettingsField.QueueCancelMode => "Queue cancel mode",
        SettingsField.SessionLogging => "Session logging",
        SettingsField.SessionNamingMode => "Session naming mode",
        SettingsField.SessionShowName => "Session show name",
        SettingsField.SessionRetentionDays => "Session retention (days)",
        SettingsField.SessionSearchMaxResults => "Session search max results",
        SettingsField.SessionTool => "Session tool",
        _ => field.ToString(),
    };

    /// <summary>
    /// How the menu shows an empty <see cref="AppSettingsData.LlmUrl"/>: where discovery will look,
    /// per the scan mode (<see cref="SettingsField.LlmScanMode"/>). Pinned.
    /// </summary>
    public static string BlankUrlLabel(ScanScope scope) => scope switch
    {
        ScanScope.Disabled => "(not set; scan disabled)",
        ScanScope.Remote => "(scan the local network)",
        ScanScope.Both => "(probe local ports and the network)",
        _ => "(probe local ports)",
    };

    /// <summary>
    /// The saved value as the menu shows it; the API key is masked. <paramref name="profileDirectory"/>
    /// is what an empty working directory resolves under (<see cref="DefaultWorkingDirectoryLabel"/>).
    /// </summary>
    public static string FieldValue(SettingsField field, AppSettingsData data, string profileDirectory) => FieldValue(field, data, profileDirectory, null);

    /// <param name="locatedBrowser">What browser auto-detection found, for an empty <see cref="SettingsField.WebBrowserPath"/> row; null = none.</param>
    public static string FieldValue(SettingsField field, AppSettingsData data, string profileDirectory, string? locatedBrowser)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(profileDirectory);
        return field switch
        {
            SettingsField.LlmUrl => string.IsNullOrWhiteSpace(data.LlmUrl) ? BlankUrlLabel(ScanScopeOf(data)) : data.LlmUrl,
            SettingsField.LlmModel => string.IsNullOrWhiteSpace(data.LlmModel) ? "(first listed)" : data.LlmModel,
            SettingsField.LlmApiKey => Mask(data.LlmApiKey),
            SettingsField.LlmRequestTimeoutSeconds => Seconds(data.LlmRequestTimeoutSeconds),
            SettingsField.LlmTurnTimeoutSeconds => Seconds(data.LlmTurnTimeoutSeconds),
            SettingsField.LlmContextLength => data.LlmContextLength > 0 ? Tokens(data.LlmContextLength) : DetectedContextLengthLabel,
            SettingsField.TtsHttpUrl => data.TtsHttpUrl,
            SettingsField.TtsVoice => data.TtsVoice,
            SettingsField.TtsVoicePreset => PresetValue(data, profileDirectory),
            SettingsField.TtsOutput => OnOff(data.TtsOutput),
            SettingsField.SttInput => OnOff(data.SttInput),
            SettingsField.SttWake => OnOff(data.SttWake),
            SettingsField.SttWakePhrase => data.SttWakePhrase,
            SettingsField.SttPushToTalkKey => data.SttPushToTalkKey,
            SettingsField.TtsSpeed => Speed(data.TtsSpeed),
            SettingsField.SttWhisperModel => data.SttWhisperModel,
            SettingsField.SttVoskModel => data.SttVoskModel,
            SettingsField.SttInterrupt => OnOff(data.SttInterrupt),
            SettingsField.SttInterruptEchoGuard => Percent(data.SttInterruptEchoGuard),
            SettingsField.SttInterruptConfirmMs => Milliseconds(data.SttInterruptConfirmMs),
            SettingsField.LlmReasoning => data.LlmReasoning,
            SettingsField.TtsVoice2 => string.IsNullOrWhiteSpace(data.TtsVoice2) ? NoSecondaryVoice : data.TtsVoice2,
            SettingsField.TtsVoiceMix => Mix(data.TtsVoiceMix),
            SettingsField.Memory => OnOff(data.Memory),
            SettingsField.WorkingDirectory => string.IsNullOrWhiteSpace(data.WorkingDirectory) ? DefaultWorkingDirectoryLabel(profileDirectory) : data.WorkingDirectory,
            SettingsField.CopyUserPrompt => OnOff(data.CopyUserPrompt),
            SettingsField.ShowImageThumbnails => OnOff(data.ShowImageThumbnails),
            SettingsField.LlmCompactType => data.LlmCompactType,
            SettingsField.LlmCompactKeepRecent => Turns(data.LlmCompactKeepRecent),
            SettingsField.LlmCompactShowSummary => OnOff(data.LlmCompactShowSummary),
            SettingsField.LlmAutoCompactPercent => data.LlmAutoCompactPercent > 0 ? Percent(data.LlmAutoCompactPercent) : CompactAtOffLabel,
            SettingsField.LlmMaxTurns => data.LlmMaxTurns > 0 ? Turns(data.LlmMaxTurns) : LlmMaxTurnsAutoLabel,
            SettingsField.LlmToolCompactType => data.LlmToolCompactType,
            SettingsField.LlmMaxToolIterations => RoundTrips(data.LlmMaxToolIterations),
            SettingsField.ImageThumbnailSize => data.ImageThumbnailSize,
            SettingsField.FileTreeMaxLength => Entries(data.FileTreeMaxLength),
            SettingsField.FileTreeShowSizes => OnOff(data.FileTreeShowSizes),
            SettingsField.NewProfileMode => data.NewProfileMode,
            SettingsField.LlmOfferTools => OnOff(data.LlmOfferTools),
            SettingsField.LlmUseFunVerbs => OnOff(data.LlmUseFunVerbs),
            SettingsField.LlmShowThinking => OnOff(data.LlmShowThinking),
            SettingsField.LlmPreserveThinking => OnOff(data.LlmPreserveThinking),
            SettingsField.SessionSaveThinking => OnOff(data.SessionSaveThinking),
            SettingsField.ClaudeExecutable => string.IsNullOrWhiteSpace(data.ClaudeExecutable) ? ClaudeLookedUpLabel : data.ClaudeExecutable,
            SettingsField.ClaudePermissions => data.ClaudePermissions,
            SettingsField.ClaudeModel => string.IsNullOrWhiteSpace(data.ClaudeModel) ? ClaudeDefaultLabel : data.ClaudeModel,
            SettingsField.ClaudeEffort => string.IsNullOrWhiteSpace(data.ClaudeEffort) ? ClaudeDefaultLabel : data.ClaudeEffort,
            SettingsField.ClaudeAdvisor => OnOff(data.ClaudeAdvisor),
            SettingsField.ClaudeAdvisorContext => data.ClaudeAdvisorContext,
            SettingsField.ClaudeAdvisorCallsPerTurn => ClaudeAdvisorCalls(data.ClaudeAdvisorCallsPerTurn),
            SettingsField.ClaudeAdvisorModel => string.IsNullOrWhiteSpace(data.ClaudeAdvisorModel) ? ClaudeAdvisorModelLabel : data.ClaudeAdvisorModel,
            SettingsField.ClaudeAdvisorEffort => string.IsNullOrWhiteSpace(data.ClaudeAdvisorEffort) ? ClaudeAdvisorEffortLabel : data.ClaudeAdvisorEffort,
            SettingsField.ClaudeAdvisorConfirm => OnOff(data.ClaudeAdvisorConfirm),
            SettingsField.ClaudeApi => OnOff(data.ClaudeApi),
            SettingsField.ClaudeApiKey => ClaudeApiKeyLabel(data.ClaudeApiKey),
            SettingsField.ClaudeApiMaxTokens => Tokens(data.ClaudeApiMaxTokens),
            SettingsField.ClaudeApiPromptCaching => OnOff(data.ClaudeApiPromptCaching),
            SettingsField.LlmScanMode => data.LlmScanMode,
            SettingsField.TtsSource => data.TtsSource,
            SettingsField.WebTools => OnOff(data.WebTools),
            SettingsField.GitNativeTools => OnOff(data.GitNativeTools),
            SettingsField.GitNativeDiffMaxLines => Lines(data.GitNativeDiffMaxLines),
            SettingsField.ShellCommandPolicy => data.ShellCommandPolicy,
            SettingsField.ShellCommandAllowed => Prefixes(data.ShellCommandAllowed.Count),
            SettingsField.ShellPoliceOutsidePaths => OnOff(data.ShellPoliceOutsidePaths),
            SettingsField.ShellPreferNative => OnOff(data.ShellPreferNative),
            SettingsField.ShellDefault => data.ShellDefault,
            SettingsField.ShellTimeoutSeconds => Seconds(data.ShellTimeoutSeconds),
            SettingsField.ShellForegroundCapSeconds => Seconds(data.ShellForegroundCapSeconds),
            SettingsField.ShellOutputMaxChars => Chars(data.ShellOutputMaxChars),
            SettingsField.ShellCodeLanguages => string.Join(", ", Shell.CodeLanguages.Resolve(data).Select(Shell.CodeLanguages.Name)),
            SettingsField.ShellCodeTimeoutSeconds => Seconds(data.ShellCodeTimeoutSeconds),
            SettingsField.ShellToolBridge => OnOff(data.ShellToolBridge),
            SettingsField.ShellCodeMaxToolCalls => ToolCalls(data.ShellCodeMaxToolCalls),
            SettingsField.GitNativeLogMaxCommits => Commits(data.GitNativeLogMaxCommits),
            SettingsField.GitNativeEmail => string.IsNullOrWhiteSpace(data.GitNativeEmail) ? NoGitIdentityLabel : data.GitNativeEmail,
            SettingsField.GitNativeName => string.IsNullOrWhiteSpace(data.GitNativeName) ? NoGitIdentityLabel : data.GitNativeName,
            SettingsField.ObsidianTools => OnOff(data.ObsidianTools),
            SettingsField.ObsidianAllowDelete => OnOff(data.ObsidianAllowDelete),
            SettingsField.SqlTools => OnOff(data.SqlTools),
            SettingsField.ComfyTools => OnOff(data.ComfyTools),
            SettingsField.ComfyUrl => string.IsNullOrWhiteSpace(data.ComfyUrl) ? NoComfyUrlLabel : data.ComfyUrl,
            SettingsField.ComfyTimeoutSeconds => Seconds(data.ComfyTimeoutSeconds),
            SettingsField.ComfyMaxPicturesPerCall => ComfyPictures(data.ComfyMaxPicturesPerCall),
            SettingsField.ComfyReinforceNegatives => OnOff(data.ComfyReinforceNegatives),
            SettingsField.ComfyShowPrompts => OnOff(data.ComfyShowPrompts),
            SettingsField.ComfyCaretMention => OnOff(data.ComfyCaretMention),
            SettingsField.ComfyPictureStrip => OnOff(data.ComfyPictureStrip),
            SettingsField.BotChatLlmMode => data.BotChatLlmMode,
            SettingsField.LlmMidTurnUsage => data.LlmMidTurnUsage,
            SettingsField.BotChatImages => OnOff(data.BotChatImages),
            SettingsField.BotChatImageMode => data.BotChatImageMode,
            SettingsField.BotChatTxt2ImgWorkflow => string.IsNullOrWhiteSpace(data.BotChatTxt2ImgWorkflow) ? NoBotChatWorkflowLabel : data.BotChatTxt2ImgWorkflow,
            SettingsField.BotChatImg2ImgWorkflow => string.IsNullOrWhiteSpace(data.BotChatImg2ImgWorkflow) ? NoBotChatWorkflowLabel : data.BotChatImg2ImgWorkflow,
            SettingsField.BotChatImg2ImgMode => data.BotChatImg2ImgMode,
            SettingsField.BotChatImageAsync => OnOff(data.BotChatImageAsync),
            SettingsField.BotChatNonTtsDelaySeconds => SecondsLabel(data.BotChatNonTtsDelaySeconds),
            SettingsField.BotChatSkills => OnOff(data.BotChatSkills),
            SettingsField.BotChatVision => OnOff(data.BotChatVision),
            SettingsField.BotChatPreloadedSkills => PreloadedSkillsValue(data.BotChatPreloadedSkills),
            SettingsField.BotChatSkillMode => data.BotChatSkillMode,
            SettingsField.ComfyOutputFolder => string.IsNullOrWhiteSpace(data.ComfyOutputFolder) ? ComfyOutputHereLabel : data.ComfyOutputFolder,
            SettingsField.ComfyWorkflowsOffered => ComfyOfferedValue(data.ComfyWorkflowsOffered, InstalledComfyWorkflows(profileDirectory)),
            SettingsField.ComfyAddWorkflow => ComfyAddWorkflowLabel,
            SettingsField.SqlDefaultConnection => string.IsNullOrWhiteSpace(data.SqlDefaultConnection) ? FirstSqlConnectionLabel : data.SqlDefaultConnection,
            SettingsField.SqlSetPassword => SqlSetPasswordLabel,
            SettingsField.SqlAddConnection => SqlAddConnectionLabel,
            SettingsField.SqlConnectionsOffered => SqlOfferedValue(data.SqlConnectionsOffered, Sql.SqlConfigFile.LoadCatalog(profileDirectory, Profiles.HomeOf(profileDirectory))),
            SettingsField.SqlPercentMention => OnOff(data.SqlPercentMention),
            SettingsField.SqlQueryMaxRows => SqlRows(data.SqlQueryMaxRows),
            SettingsField.SqlQueryTimeoutSeconds => Seconds(data.SqlQueryTimeoutSeconds),
            SettingsField.SqlConnectionsProfile => SqlConnectionsLabel(Sql.SqlConfigFile.ProfilePath(profileDirectory)),
            SettingsField.SqlConnectionsGlobal => SqlConnectionsLabel(Sql.SqlConfigFile.GlobalPath(Profiles.HomeOf(profileDirectory))),
            SettingsField.ObsidianVault => string.IsNullOrWhiteSpace(data.ObsidianVault) ? NoObsidianVaultLabel : data.ObsidianVault,
            SettingsField.WebBrowserMode => data.WebBrowserMode,
            SettingsField.WebBrowserPath => string.IsNullOrWhiteSpace(data.WebBrowserPath) ? AutoBrowserLabel(locatedBrowser) : data.WebBrowserPath,
            SettingsField.DraftEditor => string.IsNullOrWhiteSpace(data.DraftEditor) ? DefaultDraftEditorLabel : data.DraftEditor,
            SettingsField.ImageEditor => string.IsNullOrWhiteSpace(data.ImageEditor) ? DefaultImageEditorLabel : data.ImageEditor,
            SettingsField.FileViewImageMaxPerCall => Pictures(data.FileViewImageMaxPerCall),
            SettingsField.McpServers => OnOff(data.McpServers),
            SettingsField.McpConnectTimeoutSeconds => Seconds(data.McpConnectTimeoutSeconds),
            SettingsField.WebBrowserNetworkMode => data.WebBrowserNetworkMode,
            SettingsField.WebSearxngUrl => string.IsNullOrWhiteSpace(data.WebSearxngUrl) ? NoSearxngUrlLabel : data.WebSearxngUrl,
            SettingsField.WebSearchMaxResults => Results(data.WebSearchMaxResults),
            SettingsField.TtsVoicePreview => OnOff(data.TtsVoicePreview),
            SettingsField.FileTools => OnOff(data.FileTools),
            SettingsField.WebSearchMethod => data.WebSearchMethod,
            SettingsField.AskUser => OnOff(data.AskUser),
            SettingsField.AskMaxQuestions => Questions(data.AskMaxQuestions),
            SettingsField.AskMaxChoices => Choices(data.AskMaxChoices),
            SettingsField.FileMentionFolderMode => data.FileMentionFolderMode,
            SettingsField.FileBrowserMode => data.FileBrowserMode,
            SettingsField.AgentSkills => OnOff(data.AgentSkills),
            SettingsField.ExternalSkills => OnOff(data.ExternalSkills),
            SettingsField.TranscriptMarkdown => OnOff(data.TranscriptMarkdown),
            SettingsField.SkillCompactMode => data.SkillCompactMode,
            SettingsField.PastePreviewLines => Lines(data.PastePreviewLines),
            SettingsField.FileSafeEdits => OnOff(data.FileSafeEdits),
            SettingsField.SkillHashMention => OnOff(data.SkillHashMention),
            SettingsField.ToolsDollarMention => OnOff(data.ToolsDollarMention),
            SettingsField.ToolCollapseCount => Lines(data.ToolCollapseCount),
            SettingsField.CodeCollapseCount => Lines(data.CodeCollapseCount),
            SettingsField.ReflectionAutoLearn => OnOff(data.ReflectionAutoLearn),
            SettingsField.ReflectionReasoning => data.ReflectionReasoning,
            SettingsField.ReflectionWindow => Turns(data.ReflectionWindow),
            SettingsField.ReflectionMinToolCalls => ToolCalls(data.ReflectionMinToolCalls),
            SettingsField.ReflectionMaxRequests => Requests(data.ReflectionMaxRequests),
            SettingsField.ReflectionCooldownMinutes => Minutes(data.ReflectionCooldownMinutes),
            SettingsField.ReflectionCooldownMode => data.ReflectionCooldownMode,
            SettingsField.ReflectionIncludesSessions => OnOff(data.ReflectionIncludesSessions),
            SettingsField.ReflectionYieldsToTurns => OnOff(data.ReflectionYieldsToTurns),
            SettingsField.ReflectionEditsSupportingFiles => OnOff(data.ReflectionEditsSupportingFiles),
            SettingsField.HideExitAutocomplete => OnOff(data.HideExitAutocomplete),
            SettingsField.CommandTypoIntercept => OnOff(data.CommandTypoIntercept),
            SettingsField.KeepCommandHistory => OnOff(data.KeepCommandHistory),
            SettingsField.WelcomeSplash => data.WelcomeSplashMode,
            SettingsField.SessionLogging => OnOff(data.SessionLogging),
            SettingsField.SessionNamingMode => data.SessionNamingMode,
            SettingsField.SessionShowName => data.SessionShowName,
            SettingsField.SessionRetentionDays => Days(data.SessionRetentionDays),
            SettingsField.SessionSearchMaxResults => Results(data.SessionSearchMaxResults),
            SettingsField.SessionTool => OnOff(data.SessionTool),
            SettingsField.ShowWorkingDirectory => OnOff(data.ShowWorkingDirectory),
            SettingsField.ShowToolbar => OnOff(data.ShowToolbar),
            SettingsField.ThemedViewer => OnOff(data.ThemedViewer),
            SettingsField.Theme => data.Theme,
            SettingsField.QueueMessages => OnOff(data.QueueMessages),
            SettingsField.QueueCancelMode => data.QueueCancelMode,
            _ => "",
        };
    }

    /// <summary>The label of <see cref="SettingsField.ExternalSkills"/>, naming the folder it reads (the user's wording, 2026-09-16); the longest label there is, so it sets <see cref="LabelWidth"/>. Pinned.</summary>
    public const string ExternalSkillsName = "Use external skills (.agents\\skills)";

    /// <summary>
    /// How the menu shows an empty <see cref="AppSettingsData.WebBrowserPath"/>: the browser
    /// auto-detection found (<c>(auto: msedge.exe)</c>), or that it found none. Pinned.
    /// </summary>
    public static string AutoBrowserLabel(string? located) =>
        located is null ? "(auto: none found)" : "(auto: " + Path.GetFileName(located) + ")";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.WebSearxngUrl"/> (the engine is <see cref="SettingsField.WebSearchMethod"/>'s row, not this one's). Pinned.</summary>
    public const string NoSearxngUrlLabel = "(not set)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.GitNativeEmail"/> or <see cref="AppSettingsData.GitNativeName"/> (2026-09-21): <c>/gituser</c> refuses until both are set (and while <see cref="AppSettingsData.GitNativeTools"/> is off). Pinned.</summary>
    public const string NoGitIdentityLabel = "(not set)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ObsidianVault"/> (2026-09-22): no vault, so no vault tool is offered. Pinned.</summary>
    public const string NoObsidianVaultLabel = "(not set)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.SqlDefaultConnection"/> (2026-09-23): a call naming no connection gets the first in <c>sql.json</c>. Pinned.</summary>
    public const string FirstSqlConnectionLabel = "(the first connection)";

    /// <summary>The <c>Botchat txt2img workflow</c> / <c>Botchat img2img workflow</c> value and first picker row while none is named (2026-09-27: none is none, no longer the first). Pinned.</summary>
    public const string NoBotChatWorkflowLabel = "(none)";

    /// <summary>The <c>Botchat preloaded skills</c> value (2026-09-27): <see cref="NoBotChatWorkflowLabel"/> with none, else the names, comma-joined. Pinned.</summary>
    public static string PreloadedSkillsValue(IReadOnlyList<string>? names)
    {
        var kept = names?.Select(n => n.Trim()).Where(n => n.Length > 0).ToList() ?? [];
        return kept.Count == 0 ? NoBotChatWorkflowLabel : string.Join(", ", kept);
    }

    /// <summary>When the <c>Botchat preloaded skills</c> checklist has nothing to list (2026-09-27). Pinned.</summary>
    public const string NoSkillsToPreload = "No skills are installed for the botchat: add one to this profile's or the global skills folder.";

    /// <summary>One <c>Botchat preloaded skills</c> checklist row: the mark, the name, the description cut short. Pinned.</summary>
    public static string PreloadedSkillRow(Skills.Skill skill, bool chosen, int width)
    {
        ArgumentNullException.ThrowIfNull(skill);
        string about = skill.Description.Length > 60 ? skill.Description[..59] + "…" : skill.Description;
        return Markup.Escape((chosen ? "[x] " : "[ ] ") + skill.Name.PadRight(width)) + Theme.DimMarkup(about);
    }

    /// <summary>
    /// The value of an edit row over one <c>sql.json</c> (2026-09-23): how many connections it holds and how many
    /// entries it skips, or <c>(none)</c> — the file read afresh at every render, so an edit shows on return.
    /// </summary>
    public static string SqlConnectionsLabel(string path)
    {
        var loaded = Sql.SqlConfigFile.Load(path);
        string count = loaded.Connections.Count == 0 ? "(none)" : Sql.SqlText.Count(loaded.Connections.Count, "connection");
        return (loaded.Problems.Count == 0 ? count : count + ", " + Sql.SqlText.Count(loaded.Problems.Count, "problem")) + " · Enter edits sql.json";
    }

    /// <summary>
    /// The value of <c>SQL connections offered</c> (later on 2026-09-23): <see cref="SqlNotNarrowedLabel"/> while the
    /// profile never narrowed it, else how many of the loaded connections it offers. Pinned.
    /// </summary>
    public static string SqlOfferedValue(IReadOnlyList<string>? offered, Sql.SqlCatalog loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (offered is null)
        {
            return SqlNotNarrowedLabel;
        }

        int kept = loaded.Offered(offered).Connections.Count;
        return (kept == 0 ? "none" : kept.ToString(CultureInfo.InvariantCulture)) + " of " + loaded.Connections.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The <c>SQL connections offered</c> value before the profile narrows it: every connection, a new one too. Pinned.</summary>
    public const string SqlNotNarrowedLabel = "all (not narrowed)";

    /// <summary>One row of the <c>SQL connections offered</c> checklist: the mark, the name, where it points. Pinned.</summary>
    public static string SqlOfferedRow(Sql.SqlNamedConnection connection, bool offered, int width)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Markup.Escape((offered ? "[x] " : "[ ] ") + connection.Name.PadRight(width)) + Theme.DimMarkup(Sql.SqlText.MentionNote(connection));
    }

    /// <summary>The value column of the <c>SQL set password</c> action row (later on 2026-09-23). Pinned.</summary>
    public const string SqlSetPasswordLabel = "Enter to set password for a connection";

    /// <summary>A connection on the <c>SQL set password</c> pick: its name and where its password goes. Pinned.</summary>
    public static string SqlPasswordRow(Sql.SqlNamedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string store = connection.Config.InCredentialManager ? "Windows Credential Manager" : "encrypted in sql.json";
        string login = connection.Config.IsRunAs ? "runas " + connection.Config.User?.Trim() : "sql login " + connection.Config.User?.Trim();
        return $"{connection.Name}  ({login} · {store})";
    }

    /// <summary>How the menu shows <see cref="AppSettingsData.SqlQueryMaxRows"/>.</summary>
    public static string SqlRows(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " row" : " rows");

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.SqlQueryMaxRows"/>. Pinned.</summary>
    public static readonly string SqlQueryMaxRowsRangeError =
        "must be " + AppSettingsData.MinSqlQueryMaxRows.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxSqlQueryMaxRows.ToString(CultureInfo.InvariantCulture) + " rows";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.SqlQueryTimeoutSeconds"/>. Pinned.</summary>
    public static readonly string SqlQueryTimeoutRangeError =
        "must be " + AppSettingsData.MinSqlQueryTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxSqlQueryTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>The status line after an edit row opened a <c>sql.json</c> (2026-09-23). Pinned.</summary>
    public static string SqlEditingNotice(string path) => $"Opened {path} in the editor; the SQL tools read it at their next call.";

    /// <summary>The status line when a <c>sql.json</c> could not be made or opened (2026-09-23). Pinned.</summary>
    public static string SqlEditFailedError(string path, string detail) => $"Could not open {path}: {detail}.";

    /// <summary>The settings-menu wording for a folder that is no Obsidian vault (2026-09-22). Pinned.</summary>
    public const string ObsidianVaultError = "must be the full path of a folder holding .obsidian (a vault Obsidian has opened), or empty";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.DraftEditor"/>: <c>/draft</c> hands the file to whatever Windows opens a <c>.txt</c> with. Pinned.</summary>
    public const string DefaultDraftEditorLabel = "(default .txt editor)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ImageEditor"/>: a double-clicked picture opens in the built-in viewer (later on 2026-09-27, the user's call; "(default image viewer)", the app Windows registers, before). Pinned.</summary>
    public const string DefaultImageEditorLabel = "(built-in viewer)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.WebBrowserPath"/>. Pinned.</summary>
    public const string BrowserPathError = "must be the full path of an existing executable, or empty to find Edge, Chrome or Brave";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.WebSearxngUrl"/>. Pinned.</summary>
    public const string SearxngUrlError = "must be an http or https URL, or empty";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ClaudeExecutable"/> (2026-09-27): looked for on the PATH and in the installer's folder. Pinned.</summary>
    public const string ClaudeLookedUpLabel = "(looked up)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ClaudeModel"/> or <see cref="AppSettingsData.ClaudeEffort"/> (2026-09-27): the CLI decides. Pinned.</summary>
    public const string ClaudeDefaultLabel = "(Claude Code's default)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ClaudeExecutable"/>. Pinned.</summary>
    public const string ClaudeExecutableError = "must be the full path of an existing file, or empty to look for Claude Code";

    /// <summary>One row of the Claude-permissions picker: the level and its hint, padded to eleven (<c>read-only</c> is nine). Pinned.</summary>
    public static string ClaudePermissionLabel(string name) =>
        Markup.Escape(name.PadRight(11)) + Theme.DimMarkup(Claude.ClaudePermission.Describe(name));

    /// <summary>One row of the Claude-effort picker: the word, or <paramref name="blank"/> (<see cref="ClaudeDefaultLabel"/>, the advisor's <see cref="ClaudeAdvisorEffortLabel"/>) for the blank first row. Pinned.</summary>
    public static string ClaudeEffortLabel(string name, string blank = ClaudeDefaultLabel) => Markup.Escape(name.Length == 0 ? blank : name);

    /// <summary>One alias row of a Claude-model picker: the alias padded to eight and its hint, or <paramref name="blank"/> for the blank first row. Pinned.</summary>
    public static string ClaudeModelLabel(string name, string blank = ClaudeDefaultLabel) =>
        name.Length == 0 ? Markup.Escape(blank) : Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(Claude.ClaudeModels.Describe(name));

    /// <summary>A Claude-model picker's last row: <c>Other…</c>, the saved full name after it when there is one, else what the row does. Pinned.</summary>
    public static string ClaudeModelOtherLabel(string? saved) =>
        Markup.Escape(ClaudeModelOtherWord.PadRight(8)) + Theme.DimMarkup(
            !string.IsNullOrWhiteSpace(saved) && Claude.ClaudeModels.AliasOf(saved) is null ? "(" + saved.Trim() + ")" : "type a full model name");

    /// <summary>The Claude-model pickers' typed row. Pinned.</summary>
    public const string ClaudeModelOtherWord = "Other…";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ClaudeAdvisorModel"/> (2026-09-27): the Claude slash command model's. Pinned.</summary>
    public const string ClaudeAdvisorModelLabel = "(as Claude slash command model)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ClaudeAdvisorEffort"/> (2026-09-27): the Claude slash command effort's. Pinned.</summary>
    public const string ClaudeAdvisorEffortLabel = "(as Claude slash command effort)";

    /// <summary>One row of the advisor-context picker: the word and its hint, padded to eight. Pinned.</summary>
    public static string ClaudeAdvisorContextLabel(string name) =>
        Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(Claude.ClaudeAdvisorContext.Describe(name));

    /// <summary>
    /// How the menu shows <see cref="AppSettingsData.ClaudeApiKey"/> (2026-09-27): <c>(none)</c>, <see cref="ClaudeApiKeyEncryptedLabel"/>
    /// for the DPAPI value the menu saves (its blob says nothing worth masking), else <see cref="Mask"/> of a plain one
    /// (a variable, a hand edit). Pinned.
    /// </summary>
    public static string ClaudeApiKeyLabel(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? "(none)" : Sql.WindowsCredentials.IsProtected(stored) ? ClaudeApiKeyEncryptedLabel : Mask(stored.Trim());

    /// <summary>The Claude API key row's value while an encrypted key is saved. Pinned.</summary>
    public const string ClaudeApiKeyEncryptedLabel = "(set, encrypted)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ClaudeApiMaxTokens"/>. Pinned.</summary>
    public static readonly string ClaudeApiMaxTokensRangeError =
        "must be " + AppSettingsData.MinClaudeApiMaxTokens.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxClaudeApiMaxTokens.ToString(CultureInfo.InvariantCulture) + " tokens";

    /// <summary>The warning when DPAPI could not encrypt the key and it was saved as typed. Pinned.</summary>
    public static string ClaudeApiKeyPlainWarning(string reason) => $"Claude API key saved unencrypted: {reason}.";

    /// <summary>How the menu shows <see cref="AppSettingsData.ClaudeAdvisorCallsPerTurn"/>. Pinned.</summary>
    public static string ClaudeAdvisorCalls(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " call" : " calls");

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ClaudeAdvisorCallsPerTurn"/>. Pinned.</summary>
    public static readonly string ClaudeAdvisorCallsRangeError =
        "must be " + AppSettingsData.MinClaudeAdvisorCallsPerTurn.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxClaudeAdvisorCallsPerTurn.ToString(CultureInfo.InvariantCulture) + " calls";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ComfyUrl"/> (2026-09-24): no server, so no image tool. Pinned.</summary>
    public const string NoComfyUrlLabel = "(not set)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ComfyOutputFolder"/> (2026-09-24): the pictures land in the working directory itself. Pinned.</summary>
    public const string ComfyOutputHereLabel = "(the working directory)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ComfyUrl"/>. Pinned.</summary>
    public const string ComfyUrlError = "must be an http or https URL (http://host:8188), or empty";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ComfyOutputFolder"/>. Pinned.</summary>
    public const string ComfyOutputFolderError = "must be a folder under the working directory (a relative path), or empty";

    /// <summary>How the menu shows <see cref="AppSettingsData.ComfyMaxPicturesPerCall"/>. Pinned.</summary>
    public static string ComfyPictures(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " picture" : " pictures");

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ComfyMaxPicturesPerCall"/>. Pinned.</summary>
    public static readonly string ComfyMaxPicturesRangeError =
        "must be " + AppSettingsData.MinComfyMaxPicturesPerCall.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxComfyMaxPicturesPerCall.ToString(CultureInfo.InvariantCulture) + " pictures";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ComfyTimeoutSeconds"/>. Pinned.</summary>
    public static readonly string ComfyTimeoutRangeError =
        "must be " + AppSettingsData.MinComfyTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxComfyTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.SessionRetentionDays"/>. Pinned.</summary>
    public static readonly string SessionRetentionDaysRangeError =
        "must be " + AppSettingsData.MinSessionRetentionDays.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxSessionRetentionDays.ToString(CultureInfo.InvariantCulture) + " days";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.SessionSearchMaxResults"/>. Pinned.</summary>
    public static readonly string SessionSearchMaxResultsRangeError =
        "must be " + AppSettingsData.MinSessionSearchMaxResults.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxSessionSearchMaxResults.ToString(CultureInfo.InvariantCulture) + " results";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.FileViewImageMaxPerCall"/>. Pinned.</summary>
    public static readonly string ViewImageMaxPerCallRangeError =
        "must be " + AppSettingsData.MinViewImageMaxPerCall.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxViewImageMaxPerCall.ToString(CultureInfo.InvariantCulture) + " pictures";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.McpConnectTimeoutSeconds"/>. Pinned.</summary>
    public static readonly string McpConnectTimeoutRangeError =
        "must be " + AppSettingsData.MinMcpConnectTimeout.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxMcpConnectTimeout.ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ShellTimeoutSeconds"/>. Pinned.</summary>
    public static readonly string ShellTimeoutSecondsRangeError =
        "must be " + AppSettingsData.MinShellTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxShellTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ShellForegroundCapSeconds"/>. Pinned.</summary>
    public static readonly string ShellForegroundCapSecondsRangeError =
        "must be " + AppSettingsData.MinShellForegroundCapSeconds.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxShellForegroundCapSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ShellOutputMaxChars"/>. Pinned.</summary>
    public static readonly string ShellOutputMaxCharsRangeError =
        "must be " + AppSettingsData.MinShellOutputMaxChars.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxShellOutputMaxChars.ToString(CultureInfo.InvariantCulture) + " chars";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ShellCodeTimeoutSeconds"/>. Pinned.</summary>
    public static readonly string ShellCodeTimeoutSecondsRangeError =
        "must be " + AppSettingsData.MinShellCodeTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxShellCodeTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ShellCodeMaxToolCalls"/>. Pinned.</summary>
    public static readonly string ShellCodeMaxToolCallsRangeError =
        "must be " + AppSettingsData.MinShellCodeMaxToolCalls.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxShellCodeMaxToolCalls.ToString(CultureInfo.InvariantCulture) + " tool calls";

    /// <summary>One row of the code-languages list: the mark, the language, its hint and <see cref="NotFoundSuffix"/> when its interpreter is not installed (padded to eleven). Pinned.</summary>
    public static string CodeLanguageLabel(string name, bool enabled, bool installed) =>
        Markup.Escape((enabled ? "[x] " : "[ ] ") + name.PadRight(11)) + Theme.DimMarkup(Shell.CodeLanguages.Describe(name) + (installed ? "" : NotFoundSuffix));

    /// <summary>The code-languages list's hint. Pinned.</summary>
    public const string ToggleKeys = "Enter / Space = on or off · ESC = back";

    /// <summary>The status line when the last language would go: at least one stays (the user's rule, 2026-09-21). Pinned.</summary>
    public const string LastLanguageError = "At least one language stays on.";

    /// <summary>One row of the command-policy picker: the mode and its hint (padded to five: <c>yolo</c> is four). Pinned.</summary>
    public static string CommandPolicyLabel(string name) =>
        Markup.Escape(name.PadRight(5)) + Theme.DimMarkup(Shell.CommandPolicy.Describe(name));

    /// <summary>One row of the default-shell picker: the shell, its hint, and <see cref="NotFoundSuffix"/> when it is not installed (padded to eleven: <c>powershell</c> is ten). Pinned.</summary>
    public static string ShellLabel(string name, bool installed) =>
        Markup.Escape(name.PadRight(11)) + Theme.DimMarkup(Shell.ShellKinds.Describe(name) + (installed ? "" : NotFoundSuffix));

    /// <summary>After a shell's hint on the picker while it is not installed. Pinned.</summary>
    public const string NotFoundSuffix = " — not found";

    /// <summary>The one row of the allowed-commands list while nothing is allowed for good; the bare word since 2026-09-23 (the user's call, the hint on how to add one dropped). Pinned.</summary>
    public const string NoAllowedCommandsRow = "(none)";

    /// <summary>The allowed-commands list's hint. Pinned.</summary>
    public const string RemoveKeys = "Enter = remove · ESC = back";

    /// <summary>The notice after a prefix is removed from the allowed list: <c>Shell allowed commands: git push removed</c>. Pinned.</summary>
    public static string PrefixRemovedNotice(string prefix) => FieldName(SettingsField.ShellCommandAllowed) + ": " + prefix + " removed";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.GitNativeDiffMaxLines"/>. Pinned.</summary>
    public static readonly string GitNativeDiffMaxLinesRangeError =
        "must be " + AppSettingsData.MinGitNativeDiffMaxLines.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxGitNativeDiffMaxLines.ToString(CultureInfo.InvariantCulture) + " lines";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.GitNativeLogMaxCommits"/>. Pinned.</summary>
    public static readonly string GitNativeLogMaxCommitsRangeError =
        "must be " + AppSettingsData.MinGitNativeLogMaxCommits.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxGitNativeLogMaxCommits.ToString(CultureInfo.InvariantCulture) + " commits";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.WebSearchMaxResults"/>. Pinned.</summary>
    public static readonly string WebSearchMaxResultsRangeError =
        "must be " + AppSettingsData.MinWebSearchMaxResults.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxWebSearchMaxResults.ToString(CultureInfo.InvariantCulture) + " results";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.AskMaxQuestions"/>. Pinned.</summary>
    public static readonly string AskMaxQuestionsRangeError =
        "must be " + AppSettingsData.MinAskMaxQuestions.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxAskMaxQuestions.ToString(CultureInfo.InvariantCulture) + " questions";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.AskMaxChoices"/>. Pinned.</summary>
    public static readonly string AskMaxChoicesRangeError =
        "must be " + AppSettingsData.MinAskMaxChoices.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxAskMaxChoices.ToString(CultureInfo.InvariantCulture) + " choices";

    /// <summary>One row of the browser-mode picker: the mode and its hint (padded to eleven: <c>httpclient</c> is ten). Pinned.</summary>
    public static string BrowserModeLabel(string name) =>
        Markup.Escape(name.PadRight(11)) + Theme.DimMarkup(Web.BrowserMode.Describe(name));

    /// <summary>One row of the network-mode picker: the mode and its hint (padded to nineteen: <c>local_area_network</c> is eighteen). Pinned.</summary>
    public static string NetworkModeLabel(string name) =>
        Markup.Escape(name.PadRight(19)) + Theme.DimMarkup(Web.NetworkMode.Describe(name));

    /// <summary>One row of the queue-cancel-mode picker: the mode and its hint (padded to six: <c>drain</c> and <c>empty</c> are five). Pinned.</summary>
    public static string QueueCancelModeLabel(string name) =>
        Markup.Escape(name.PadRight(6)) + Theme.DimMarkup(QueueCancelMode.Describe(name));

    /// <summary>One row of the botchat-LLM-mode picker: the mode and its hint (padded to seven: <c>single</c> is six). Pinned.</summary>
    public static string BotChatLlmModeLabel(string name) =>
        Markup.Escape(name.PadRight(7)) + Theme.DimMarkup(App.BotChatLlmMode.Describe(name));

    /// <summary>One row of the mid-turn-usage picker: the mode and its hint (padded to eleven: <c>last-known</c> is ten). Pinned.</summary>
    public static string MidTurnUsageLabel(string name) =>
        Markup.Escape(name.PadRight(11)) + Theme.DimMarkup(MidTurnUsageMode.Describe(name));

    /// <summary>One row of the botchat-image-mode picker: the mode and its hint (padded to eleven: <c>autonomous</c> is ten). Pinned.</summary>
    public static string BotChatImageModeLabel(string name) =>
        Markup.Escape(name.PadRight(11)) + Theme.DimMarkup(App.BotChatImageMode.Describe(name));

    /// <summary>One row of the botchat-img2img-mode picker: the mode and its hint (padded to thirteen: <c>chat-history</c> is twelve). Pinned.</summary>
    public static string BotChatImg2ImgModeLabel(string name) =>
        Markup.Escape(name.PadRight(13)) + Theme.DimMarkup(App.BotChatImg2ImgMode.Describe(name));

    /// <summary>One row of the botchat-skill-mode picker: the mode and its hint (padded to twenty-three: <c>prompt-writer-and-bots</c> is twenty-two). Pinned.</summary>
    public static string BotChatSkillModeLabel(string name) =>
        Markup.Escape(name.PadRight(23)) + Theme.DimMarkup(App.BotChatSkillMode.Describe(name));

    /// <summary>One row of the welcome-splash picker: the mode and its hint (padded to nine: <c>fullsize</c> and <c>disabled</c> are eight). Pinned.</summary>
    public static string WelcomeSplashModeLabel(string name) =>
        Markup.Escape(name.PadRight(9)) + Theme.DimMarkup(UI.SplashMode.Describe(name));

    /// <summary>One row of the search-method picker: the method and its hint (padded to eleven: <c>duckduckgo</c> is ten). Pinned.</summary>
    public static string SearchMethodLabel(string name) =>
        Markup.Escape(name.PadRight(11)) + Theme.DimMarkup(Web.SearchMethod.Describe(name));

    /// <summary>The saved scan mode as a scope for the row labels — silently the default on a hand-edited value (<see cref="Llm.LlmScanMode.Resolve"/> warns where the scan happens).</summary>
    private static ScanScope ScanScopeOf(AppSettingsData data)
    {
        Llm.LlmScanMode.TryParse(data.LlmScanMode, out var scope);
        return scope;
    }

    /// <summary>One row of the reasoning picker: the level and its hint. Pinned.</summary>
    public static string ReasoningLabel(string level) =>
        Markup.Escape(level.PadRight(8)) + Theme.DimMarkup(Llm.ReasoningLevel.Describe(level));

    /// <summary>One row of the compact-type picker: the type and its hint. Pinned.</summary>
    public static string CompactTypeLabel(string name) =>
        Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(Llm.CompactType.Describe(name));

    /// <summary>One row of the tool-compact-type picker: the type and its hint. Pinned.</summary>
    public static string ToolCompactTypeLabel(string name) =>
        Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(Llm.ToolCompactType.Describe(name));

    /// <summary>One row of the skill-compact-mode picker: the mode and its hint, padded to twelve (<c>unprotected</c> is eleven). Pinned.</summary>
    public static string SkillCompactModeLabel(string name) =>
        Markup.Escape(name.PadRight(12)) + Theme.DimMarkup(Skills.SkillCompactMode.Describe(name));

    /// <summary>One row of the session-naming-mode picker: the word and its hint, padded to fifteen (<c>model-written</c> is thirteen). Pinned.</summary>
    public static string SessionNamingModeLabel(string name) =>
        Markup.Escape(name.PadRight(15)) + Theme.DimMarkup(Sessions.SessionNamingMode.Describe(name));

    /// <summary>One row of the session-show-name picker: the word and its hint, padded to fifteen like the naming mode's. Pinned.</summary>
    public static string SessionShowNameLabel(string name) =>
        Markup.Escape(name.PadRight(15)) + Theme.DimMarkup(Sessions.SessionShowName.Describe(name));

    /// <summary>One row of the reflection-reasoning picker: the word and its hint, padded to eight like <see cref="ReasoningLabel"/> (sixteen while the word was <c>profile-default</c>, until later on 2026-09-19). Pinned.</summary>
    public static string ReflectionReasoningLabel(string name) =>
        Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(Skills.ReflectionReasoning.Describe(name));

    /// <summary>One row of the cooldown-mode picker: the word padded to the longest, the meaning dim. Pinned.</summary>
    public static string ReflectionCooldownModeLabel(string name) =>
        Markup.Escape(name.PadRight(20)) + Theme.DimMarkup(Skills.ReflectionCooldownMode.Describe(name));

    /// <summary>One row of the @-mention-folder-mode picker: the mode and its hint, padded to fourteen (<c>folder-remain</c> is thirteen). Pinned.</summary>
    public static string MentionFolderModeLabel(string name) =>
        Markup.Escape(name.PadRight(14)) + Theme.DimMarkup(Files.MentionFolderMode.Describe(name));

    /// <summary>One row of the file-browser-mode picker: the mode and its hint, padded to twelve (<c>show-hidden</c> is eleven). Pinned.</summary>
    public static string FileBrowserModeLabel(string name) =>
        Markup.Escape(name.PadRight(12)) + Theme.DimMarkup(Files.FileBrowserMode.Describe(name));

    /// <summary>One row of the scan-mode picker: the mode and its hint, padded to nine (<c>disabled</c> is eight). Pinned.</summary>
    public static string LlmScanModeLabel(string name) =>
        Markup.Escape(name.PadRight(9)) + Theme.DimMarkup(Llm.LlmScanMode.Describe(name));

    /// <summary>One row of the TTS-source picker: the source and its hint, padded to eleven (<c>in-process</c> is ten). Pinned.</summary>
    public static string TtsSourceLabel(string name) =>
        Markup.Escape(name.PadRight(11)) + Theme.DimMarkup(Speech.TtsSource.Describe(name));

    /// <summary>One row of the thumbnail-size picker: the size and its box (padded to nine: <c>fullsize</c> is eight). Pinned.</summary>
    public static string ImageThumbnailSizeLabel(string name) =>
        Markup.Escape(name.PadRight(9)) + Theme.DimMarkup(ThumbnailSize.Describe(name));

    /// <summary>One row of the theme picker: the name and its note (padded to ten: the longest names are nine). Pinned.</summary>
    public static string ThemeLabel(string name) =>
        Markup.Escape(name.PadRight(10)) + Theme.DimMarkup(ThemeName.Describe(name));

    /// <summary>One row of the new-profile-mode picker: the mode and its hint (padded to nine: <c>advanced</c> is eight). Pinned.</summary>
    public static string NewProfileModeLabel(string name) =>
        Markup.Escape(name.PadRight(9)) + Theme.DimMarkup(NewProfileMode.Describe(name));

    /// <summary>One row of the push-to-talk picker: the key's name, the default marked. Pinned.</summary>
    public static string PushToTalkLabel(ConsoleKey key) =>
        Markup.Escape(key.ToString()) + (key == ConsoleKey.F4 ? Theme.DimMarkup("  the default") : "");

    /// <summary>One row of the whisper-model picker: the file name (padded to nineteen: <c>ggml-small.en.bin</c> is seventeen), what it trades, and its download size. Pinned.</summary>
    public static string WhisperModelLabel(string name, long bytes)
    {
        ArgumentNullException.ThrowIfNull(name);
        string hint = name switch
        {
            "ggml-tiny.en.bin" => "fastest",
            "ggml-small.en.bin" => "most accurate",
            _ => "the default",
        };
        return Markup.Escape(name.PadRight(19)) + Theme.DimMarkup($"{hint}, {ModelStore.SizeLabel(bytes)}");
    }

    /// <summary>One row of the vosk-model picker: the name (padded to thirty: the lgraph name is twenty-eight), what it is for, and its download size. Pinned.</summary>
    public static string VoskModelLabel(string name, long bytes)
    {
        ArgumentNullException.ThrowIfNull(name);
        string hint = name switch
        {
            "vosk-model-en-us-0.22-lgraph" => "most accurate",
            "vosk-model-small-en-in-0.4" => "Indian English",
            _ => "the default",
        };
        return Markup.Escape(name.PadRight(30)) + Theme.DimMarkup($"{hint}, {ModelStore.SizeLabel(bytes)}");
    }

    /// <summary>The raw text a field edits (unmasked, unformatted).</summary>
    public static string EditableValue(SettingsField field, AppSettingsData data) => field switch
    {
        SettingsField.LlmUrl => data.LlmUrl,
        SettingsField.LlmModel => data.LlmModel,
        SettingsField.LlmApiKey => data.LlmApiKey,
        SettingsField.LlmRequestTimeoutSeconds => Seconds(data.LlmRequestTimeoutSeconds),
        SettingsField.LlmTurnTimeoutSeconds => Seconds(data.LlmTurnTimeoutSeconds),
        SettingsField.LlmContextLength => data.LlmContextLength.ToString(CultureInfo.InvariantCulture),
        SettingsField.TtsHttpUrl => data.TtsHttpUrl,
        SettingsField.TtsVoice => data.TtsVoice,
        SettingsField.SttWakePhrase => data.SttWakePhrase,
        SettingsField.SttPushToTalkKey => data.SttPushToTalkKey,
        SettingsField.TtsSpeed => Speed(data.TtsSpeed),
        SettingsField.SttWhisperModel => data.SttWhisperModel,
        SettingsField.SttVoskModel => data.SttVoskModel,
        SettingsField.TtsVoice2 => data.TtsVoice2,
        SettingsField.TtsVoiceMix => data.TtsVoiceMix.ToString(CultureInfo.InvariantCulture),
        SettingsField.SttInterruptEchoGuard => data.SttInterruptEchoGuard.ToString(CultureInfo.InvariantCulture),
        SettingsField.SttInterruptConfirmMs => data.SttInterruptConfirmMs.ToString(CultureInfo.InvariantCulture),
        SettingsField.LlmCompactKeepRecent => data.LlmCompactKeepRecent.ToString(CultureInfo.InvariantCulture),
        SettingsField.ReflectionWindow => data.ReflectionWindow.ToString(CultureInfo.InvariantCulture),
        SettingsField.ReflectionMinToolCalls => data.ReflectionMinToolCalls.ToString(CultureInfo.InvariantCulture),
        SettingsField.ReflectionMaxRequests => data.ReflectionMaxRequests.ToString(CultureInfo.InvariantCulture),
        SettingsField.ReflectionCooldownMinutes => data.ReflectionCooldownMinutes.ToString(CultureInfo.InvariantCulture),
        SettingsField.LlmAutoCompactPercent => data.LlmAutoCompactPercent.ToString(CultureInfo.InvariantCulture),
        SettingsField.LlmMaxTurns => data.LlmMaxTurns > 0 ? data.LlmMaxTurns.ToString(CultureInfo.InvariantCulture) : LlmMaxTurnsAutoLabel,
        SettingsField.LlmMaxToolIterations => data.LlmMaxToolIterations.ToString(CultureInfo.InvariantCulture),
        SettingsField.FileTreeMaxLength => data.FileTreeMaxLength.ToString(CultureInfo.InvariantCulture),
        SettingsField.WorkingDirectory => data.WorkingDirectory,
        SettingsField.WebBrowserPath => data.WebBrowserPath,
        SettingsField.WebSearxngUrl => data.WebSearxngUrl,
        SettingsField.ComfyUrl => data.ComfyUrl,
        SettingsField.ClaudeExecutable => data.ClaudeExecutable,
        SettingsField.ClaudeModel => data.ClaudeModel,
        SettingsField.ClaudeAdvisorModel => data.ClaudeAdvisorModel,
        SettingsField.ClaudeAdvisorCallsPerTurn => data.ClaudeAdvisorCallsPerTurn.ToString(CultureInfo.InvariantCulture),
        SettingsField.ClaudeApiMaxTokens => data.ClaudeApiMaxTokens.ToString(CultureInfo.InvariantCulture),

        // The key is never put back on the line: typing replaces it, empty clears it.
        SettingsField.ClaudeApiKey => "",
        SettingsField.ComfyOutputFolder => data.ComfyOutputFolder,
        SettingsField.ComfyTimeoutSeconds => data.ComfyTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.ComfyMaxPicturesPerCall => data.ComfyMaxPicturesPerCall.ToString(CultureInfo.InvariantCulture),
        SettingsField.DraftEditor => data.DraftEditor,
        SettingsField.ImageEditor => data.ImageEditor,
        SettingsField.Theme => data.Theme,
        SettingsField.FileViewImageMaxPerCall => data.FileViewImageMaxPerCall.ToString(CultureInfo.InvariantCulture),
        SettingsField.McpConnectTimeoutSeconds => data.McpConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.WebSearchMaxResults => data.WebSearchMaxResults.ToString(CultureInfo.InvariantCulture),
        SettingsField.ToolCollapseCount => data.ToolCollapseCount.ToString(CultureInfo.InvariantCulture),
        SettingsField.CodeCollapseCount => data.CodeCollapseCount.ToString(CultureInfo.InvariantCulture),
        SettingsField.GitNativeDiffMaxLines => data.GitNativeDiffMaxLines.ToString(CultureInfo.InvariantCulture),
        SettingsField.ShellTimeoutSeconds => data.ShellTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.ShellForegroundCapSeconds => data.ShellForegroundCapSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.ShellOutputMaxChars => data.ShellOutputMaxChars.ToString(CultureInfo.InvariantCulture),
        SettingsField.ShellCodeTimeoutSeconds => data.ShellCodeTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.ShellCodeMaxToolCalls => data.ShellCodeMaxToolCalls.ToString(CultureInfo.InvariantCulture),
        SettingsField.GitNativeLogMaxCommits => data.GitNativeLogMaxCommits.ToString(CultureInfo.InvariantCulture),
        SettingsField.SqlQueryMaxRows => data.SqlQueryMaxRows.ToString(CultureInfo.InvariantCulture),
        SettingsField.SqlQueryTimeoutSeconds => data.SqlQueryTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.GitNativeEmail => data.GitNativeEmail,
        SettingsField.GitNativeName => data.GitNativeName,
        SettingsField.ObsidianVault => data.ObsidianVault,
        SettingsField.AskMaxQuestions => data.AskMaxQuestions.ToString(CultureInfo.InvariantCulture),
        SettingsField.AskMaxChoices => data.AskMaxChoices.ToString(CultureInfo.InvariantCulture),
        SettingsField.PastePreviewLines => data.PastePreviewLines.ToString(CultureInfo.InvariantCulture),
        SettingsField.SessionRetentionDays => data.SessionRetentionDays.ToString(CultureInfo.InvariantCulture),
        SettingsField.SessionSearchMaxResults => data.SessionSearchMaxResults.ToString(CultureInfo.InvariantCulture),
        SettingsField.BotChatNonTtsDelaySeconds => data.BotChatNonTtsDelaySeconds.ToString(CultureInfo.InvariantCulture),
        _ => "",
    };

    /// <summary>One menu row as markup: padded name, the value, and the override note when there is one.</summary>
    public static string FieldLabel(SettingsField field, AppSettingsData data, string profileDirectory, string? overriddenBy) =>
        FieldLabel(field, data, profileDirectory, overriddenBy, LabelWidth);

    /// <summary>A row padded to <paramref name="width"/> (a tab's column on the pane).</summary>
    public static string FieldLabel(SettingsField field, AppSettingsData data, string profileDirectory, string? overriddenBy, int width, string? locatedBrowser = null)
    {
        string row = Markup.Escape(FieldName(field).PadRight(width)) + Theme.ColorMarkup(Theme.Ink, FieldValue(field, data, profileDirectory, locatedBrowser));
        return overriddenBy is null ? row : row + Theme.DimMarkup($"  (overridden by {overriddenBy})");
    }

    public static string Mask(string secret) =>
        string.IsNullOrEmpty(secret) ? "(none)" : secret.Length <= 4 ? new string('•', secret.Length) : secret[..2] + new string('•', secret.Length - 2);

    public static string OverrideNotice(string overriddenBy) => $"{overriddenBy} still overrides this launch.";

    /// <summary>
    /// The status line after a row is saved: <c>LLM URL: http://…</c>. A row named <c>LLM …</c> wears
    /// the LLM's glyph ahead of it (2026-09-22, the user's ask: <c>/server</c>'s URL, model and
    /// reasoning lines beside the <c>🖥️ LLM:</c> connected line); every other row goes bare. Pinned.
    /// </summary>
    public static string SavedNotice(SettingsField field, AppSettingsData data, string profileDirectory, string? locatedBrowser = null)
    {
        string name = FieldName(field);
        string glyph = name.StartsWith("LLM ", StringComparison.Ordinal) ? NoticeGlyphs.Llm : "";
        return $"{glyph}{name}: {FieldValue(field, data, profileDirectory, locatedBrowser)}";
    }

    // ── Screens ─────────────────────────────────────────────────────────────

    /// <summary>The settings menu. Returns which sessions the changed fields belong to.</summary>
    public Task<SettingsChanges> ShowAsync(CancellationToken cancellationToken) => ShowAsync(cancellationToken, midTurn: false);

    /// <summary>
    /// <see cref="ShowAsync(CancellationToken)"/> for a pane opened while a reply runs
    /// (<paramref name="midTurn"/>): the rows whose change would reconnect a session (LLM, TTS,
    /// STT), switch the profile, move the working directory or reshape the history (<c>LLM offer tools</c>)
    /// are refused with <see cref="NotWhileReplyRunsNotice"/> on the status line (<see cref="RefusedMidTurn"/>),
    /// so the result never carries a flag the screen would act on mid-turn; the other rows edit as ever.
    /// </summary>
    public async Task<SettingsChanges> ShowAsync(CancellationToken cancellationToken, bool midTurn)
    {
        if (!CanShowMenus())
        {
            Flow.Error(MenusNeedTerminalError);
            return SettingsChanges.None;
        }

        var changes = SettingsChanges.None;
        // The theme is put in force as its row is saved (the pane re-colours); what the screen
        // hears is the net change, so a pick and a pick back is no restart (2026-09-23).
        var themeBefore = Theme.Current;
        int tab = 0;
        int cursor = 0;
        _midTurn = midTurn;
        try
        {
            while (true)
            {
                var saved = _settings.Current;
                var picked = await PickSettingAsync(saved, tab, cursor, cancellationToken).ConfigureAwait(false);
                if (picked is not var (field, page, row))
                {
                    return ReferenceEquals(Theme.Current, themeBefore) ? changes : changes | SettingsChanges.Theme;
                }

                tab = page.Tab;
                cursor = row;
                if (midTurn && RefusedMidTurn(field))
                {
                    Sink.Notice(NotWhileReplyRunsNotice);
                    continue;
                }

                if (field == SettingsField.Profile)
                {
                    if (await PickProfileAsync(Crumb(FieldName(SettingsField.Profile)), SwitchKeys, close: false, cancellationToken).ConfigureAwait(false))
                    {
                        changes |= SettingsChanges.Profile;
                    }

                    continue;
                }

                if (await EditAsync(field, saved, page, row, cancellationToken).ConfigureAwait(false))
                {
                    if (IsLlmField(field))
                    {
                        changes |= SettingsChanges.Llm;
                    }

                    if (IsTtsField(field))
                    {
                        changes |= SettingsChanges.Tts;
                    }

                    if (IsVoiceField(field))
                    {
                        changes |= SettingsChanges.Voice;
                    }

                    if (field == SettingsField.LlmOfferTools)
                    {
                        changes |= SettingsChanges.Conversation;
                    }

                    if (IsMcpField(field))
                    {
                        changes |= SettingsChanges.Mcp;
                    }
                }
            }
        }
        finally
        {
            _midTurn = false;
            _pane.Close();
        }
    }

    /// <summary>Whether <paramref name="field"/> is refused while a reply runs: the profile, the working directory, every reconnecting row, the tools flip and the theme (a change starts the screen over, 2026-09-23). Pure.</summary>
    public static bool RefusedMidTurn(SettingsField field) =>
        field is SettingsField.Profile or SettingsField.WorkingDirectory or SettingsField.LlmOfferTools or SettingsField.Theme
        || IsLlmField(field) || IsTtsField(field) || IsVoiceField(field) || IsMcpField(field);

    /// <summary>
    /// A yes/no question as a pane (or the prompt host without one): <paramref name="question"/> as
    /// the title, <see cref="ConfirmRows"/> with the cursor on <c>No</c>, <see cref="ConfirmKeys"/> in
    /// the hint, <see cref="ConfirmHotkeys"/> moving the cursor. True only for an Enter on <c>Yes</c>;
    /// ESC, the token and no keyboard are no. The pane
    /// closes with the answer, so what follows goes to the transcript. Callers check <see cref="CanShowMenus"/>
    /// first and fall back to a typed confirmation where menus cannot open.
    /// </summary>
    public async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        var page = new MenuPage(question, ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
        int? picked = await PickOnceAsync(page, 0, cancellationToken).ConfigureAwait(false);
        return picked == 1;
    }

    /// <summary>
    /// One pick from the settings list: on the pane the tabbed page (<see cref="SettingsTabs"/>)
    /// opened on <paramref name="tab"/>, elsewhere the flat list (<see cref="SettingsPage"/>) as a
    /// prompt. The field picked, the page it was picked from (its <see cref="MenuPage.Tab"/> is the
    /// tab shown at Enter) and the row within that page; null for ESC.
    /// </summary>
    private async Task<(SettingsField Field, MenuPage Page, int Row)?> PickSettingAsync(AppSettingsData saved, int tab, int cursor, CancellationToken cancellationToken)
    {
        if (_pane.Enabled)
        {
            var tabbed = SettingsTabs(saved, _settings.ProfileName, tab);
            if (await _pane.PickAsync(tabbed, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return null;
            }

            // The page on the tab the pane ended on, so a typed edit under it keeps that tab's rows.
            var shown = pick.Tab == tabbed.Tab ? tabbed : MenuPage.Tabbed(Title, tabbed.Tabs!, pick.Tab, TabKeys);
            return (TabFields[pick.Tab][pick.Row], shown, pick.Row);
        }

        var page = SettingsPage(saved, _settings.ProfileName);
        if (await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return null;
        }

        return (Fields[row], page, row);
    }

    /// <summary>The settings list as the prompt host shows it: one row per field from <paramref name="saved"/>, the profile row from the loaded name.</summary>
    private MenuPage SettingsPage(AppSettingsData saved, string profile)
    {
        string? located = _locateBrowser("");
        var rows = new string[Fields.Length];
        for (int i = 0; i < Fields.Length; i++)
        {
            var f = Fields[i];
            rows[i] = f == SettingsField.Profile ? ProfileLabel(profile, _settings.ProfileDirectory) : FieldLabel(f, saved, _settings.ProfileDirectory, _overriddenBy(f), LabelWidth, located);
        }

        return new MenuPage(Title, rows, TitleKeys);
    }

    /// <summary>The settings list as the pane shows it: the <see cref="TabFields"/> under the <see cref="TabTitles"/> strip, each tab padded to its own column, opened on <paramref name="tab"/>.</summary>
    private MenuPage SettingsTabs(AppSettingsData saved, string profile, int tab)
    {
        var tabs = new MenuTab[TabTitles.Count];
        for (int t = 0; t < tabs.Length; t++)
        {
            tabs[t] = FieldsTab(TabTitles[t], TabFields[t], saved, profile);
        }

        return MenuPage.Tabbed(Title, tabs, tab, TabKeys);
    }

    /// <summary>
    /// One tab of settings rows for a pane: <paramref name="fields"/> as <see cref="FieldLabel"/> rows padded to
    /// <see cref="LabelWidthOf"/>, the profile row from <paramref name="profile"/> (the loaded name; null reads it
    /// from the store). The seam <see cref="ToolsMenu"/> builds its Ask / Files / Web tabs through (2026-09-19).
    /// </summary>
    internal MenuTab FieldsTab(string title, IReadOnlyList<SettingsField> fields, AppSettingsData saved, string? profile = null)
    {
        string? located = _locateBrowser("");
        int width = LabelWidthOf(fields);
        var rows = new string[fields.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var f = fields[i];
            rows[i] = f == SettingsField.Profile ? ProfileLabel(profile ?? _settings.ProfileName, _settings.ProfileDirectory, width) : FieldLabel(f, saved, _settings.ProfileDirectory, _overriddenBy(f), width, located);
        }

        return new MenuTab(title, rows);
    }

    /// <summary>A row as a plain line, for a console without the pane: <c>Web browser mode: chromium</c> (the <see cref="SavedNotice"/> shape). The seam <see cref="ToolsMenu"/> prints its settings tabs through.</summary>
    internal string PlainRow(SettingsField field, AppSettingsData saved) =>
        FieldName(field) + ": " + FieldValue(field, saved, _settings.ProfileDirectory, field == SettingsField.WebBrowserPath ? _locateBrowser("") : null);

    // ── The hosts ───────────────────────────────────────────────────────────

    /// <summary>
    /// One level of a list: in the pane when the screen has one, else a Spectre prompt titled
    /// <see cref="PromptTitle"/> at the flow end. The picked row's index, or null for ESC; a
    /// <paramref name="cursor"/> outside the rows opens on the first. <paramref name="highlighted"/>
    /// is the pane's cursor hook (<see cref="MenuPane.PickAsync"/>); the prompt has none.
    /// </summary>
    private async Task<int?> PickAsync(MenuPage page, int cursor, CancellationToken cancellationToken, Action<int>? highlighted = null)
    {
        if (_pane.Enabled)
        {
            return (await _pane.PickAsync(page, cursor, cancellationToken, highlighted).ConfigureAwait(false))?.Row;
        }

        var rows = page.Rows;
        var prompt = Theme.Selection(new SelectionPrompt<PromptResult<int>>()
            .Title(Theme.AccentMarkup(PromptTitle(page.Title, page.Hint)))
            .AddChoices(Enumerable.Range(0, rows.Count).Select(PromptResult<int>.From))
            .AddCancelResult(() => PromptResult<int>.Canceled)
            .UseConverter(r => r.IsCanceled ? "" : rows[r.Value]));
        if (cursor >= 0 && cursor < rows.Count)
        {
            prompt.DefaultValue(PromptResult<int>.From(cursor));
        }

        var picked = await ScreenPane.ModalAsync(_console, () => prompt.ShowAsync(_console, cancellationToken)).ConfigureAwait(false);
        return picked.IsCanceled ? null : picked.Value;
    }

    /// <summary>A single-level picker: the pane is closed as soon as the pick lands, so what follows goes to the transcript.</summary>
    private async Task<int?> PickOnceAsync(MenuPage page, int cursor, CancellationToken cancellationToken)
    {
        try
        {
            return await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>
    /// A typed value: on the pane, the input row under the list with the field's row marked and
    /// <see cref="EditKeys"/> in the hint; without it, the keys as a notice and the ordinary input
    /// row. One ESC keeps the saved value on both.
    /// </summary>
    private Task<InputResult> EditTextAsync(SettingsField field, MenuPage page, int row, string initial, bool allowEmpty, CancellationToken cancellationToken)
    {
        if (_pane.Enabled)
        {
            return _pane.EditAsync(page with { Hint = EditKeys }, row, _input, initial, allowEmpty, cancellationToken);
        }

        Flow.Notice(PromptTitle(FieldName(field), EditKeys));
        return _input.ReadAsync(initial, remember: false, allowEmpty: allowEmpty, cancellationToken: cancellationToken, escapeCancels: true);
    }

    /// <summary>
    /// <c>/model</c>: sets <paramref name="requestedId"/> directly when given, otherwise lists the
    /// server's models and lets the user pick. Returns true when the saved model changed.
    /// </summary>
    public async Task<bool> PickModelAsync(LlmSession session, string requestedId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            return SaveModel(requestedId.Trim());
        }

        if (!CanShowMenus())
        {
            Flow.Error(MenusNeedTerminalError);
            return false;
        }

        var listed = await _transcript.WithSpinnerAsync(ListingModelsLabel, () => session.ListModelsAsync(cancellationToken)).ConfigureAwait(false);
        if (listed is null)
        {
            Flow.Error(NoUrlError);
            return false;
        }

        string current = session.Endpoint?.ModelId ?? _settings.Current.LlmModel;
        return await PickModelFromListAsync(listed.Value, current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The model picker over a list already in hand (<c>/model</c> after <see cref="LlmSession.ListModelsAsync"/>,
    /// <c>/server</c> after its probe): <paramref name="current"/> is always offered, first when
    /// the server does not list it, and the cursor opens on it. Returns true when the saved model
    /// changed; ESC keeps it (<see cref="UnchangedNotice"/>). Without menus the guard prints.
    /// </summary>
    public async Task<bool> PickModelFromListAsync(ProbeResult listed, string current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!CanShowMenus())
        {
            Flow.Error(MenusNeedTerminalError);
            return false;
        }

        var ids = listed.ModelIds.ToList();
        if (!string.IsNullOrWhiteSpace(current) && !ids.Contains(current, StringComparer.Ordinal))
        {
            ids.Insert(0, current);
        }

        if (ids.Count == 0)
        {
            // Only reachable with no current id at all; a connected session always has one.
            Flow.Error(listed.Exists ? NoModelsListedError : $"The server did not answer ({listed.Detail}). {NoModelsListedError}");
            return false;
        }

        var page = new MenuPage(ModelTitle, ids.Select(Markup.Escape).ToList(), KeepKeys);
        int? picked = await PickOnceAsync(page, ids.IndexOf(current), cancellationToken).ConfigureAwait(false);
        return picked is { } i ? SaveModel(ids[i]) : Unchanged();
    }

    /// <summary>
    /// The server picker: one row per server that answered (<see cref="ServerLabel"/>), the cursor
    /// on <paramref name="current"/> when it is listed. Returns the pick, or null on ESC — with
    /// <see cref="UnchangedNotice"/> under <see cref="ServerTitle"/>, silently under
    /// <see cref="StartupServerTitle"/> (the <c>LLM:</c> line that follows says what happened).
    /// Without menus the list is printed and nothing is picked. Saving is the caller's
    /// (<see cref="SaveServer"/>): the startup path and <c>/server</c> connect differently.
    /// </summary>
    public async Task<LlmServer?> PickServerAsync(IReadOnlyList<LlmServer> servers, Uri? current, string title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(title);
        if (!CanShowMenus())
        {
            Flow.Notice(ServerListLine(servers));
            return null;
        }

        int cursor = -1;
        for (int i = 0; i < servers.Count && cursor < 0; i++)
        {
            if (current is not null && servers[i].BaseUrl == current)
            {
                cursor = i;
            }
        }

        var page = new MenuPage(title, servers.Select(ServerLabel).ToList(), title == StartupServerTitle ? StartupServerKeys : KeepKeys);
        int? picked = await PickOnceAsync(page, cursor, cancellationToken).ConfigureAwait(false);
        if (picked is { } index)
        {
            return servers[index];
        }

        if (title == ServerTitle)
        {
            Unchanged();
        }

        return null;
    }

    /// <summary>
    /// Saves <paramref name="baseUrl"/> as the LLM URL in one write — clearing the model id when
    /// the URL actually changed, because the id belonged to the other server — with one notice
    /// and the override reminder. Returns true when the URL changed.
    /// </summary>
    public bool SaveServer(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        string url = baseUrl.ToString();
        bool changed = !string.Equals(_settings.Current.LlmUrl, url, StringComparison.Ordinal);
        Apply(SettingsField.LlmUrl, d =>
        {
            d.LlmUrl = url;
            if (changed) d.LlmModel = "";
        });
        return changed;
    }

    /// <summary>
    /// The profile picker (<c>/profile</c> with nothing, and the first settings row): every
    /// profile, opened on the loaded one; Enter on another switches it in place
    /// (<see cref="AppSettings.SwitchProfileAsync"/>) and returns true. The caller rebinds what
    /// depends on the profile. Without menus the list is printed and nothing switches.
    /// </summary>
    public Task<bool> PickProfileAsync(CancellationToken cancellationToken) =>
        PickProfileAsync(ProfileTitle, ProfileKeys, close: true, cancellationToken);

    /// <summary>The profile picker under <paramref name="label"/>; <paramref name="close"/> false keeps the pane open for the settings list it came from.</summary>
    private async Task<bool> PickProfileAsync(string label, string keys, bool close, CancellationToken cancellationToken)
    {
        string current = _settings.ProfileName;
        var names = Profiles.List(_settings.StorageDirectory);
        if (!CanShowMenus())
        {
            Flow.Notice(ProfileListLine(names, current));
            return false;
        }

        var page = new MenuPage(label, names.Select(Markup.Escape).ToList(), keys);
        int cursor = names.ToList().IndexOf(current);
        int? picked = close
            ? await PickOnceAsync(page, cursor, cancellationToken).ConfigureAwait(false)
            : await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
        if (picked is not { } i)
        {
            return Unchanged();
        }

        return await SwitchProfileAsync(names[i]).ConfigureAwait(false);
    }

    /// <summary>
    /// Switches to a listed profile and returns true; the loaded one prints
    /// <see cref="AlreadyCurrentNotice"/> and returns false. The caller announces the switch
    /// (<see cref="SwitchedNotice"/>) on the screen it redraws: printed here, the wipe would take
    /// it. <paramref name="name"/> must be resolved (<see cref="Profiles.Resolve"/>). A failed
    /// pointer write is logged, not thrown.
    /// </summary>
    public async Task<bool> SwitchProfileAsync(string name)
    {
        if (Profiles.NameEquals(name, _settings.ProfileName))
        {
            Sink.Notice(AlreadyCurrentNotice(_settings.ProfileName));
            return false;
        }

        await _settings.SwitchProfileAsync(name).ConfigureAwait(false);
        return true;
    }

    // ── Editing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One row's edit: a toggle's on/off page, a picker's list, or a typed value under <paramref name="page"/>'s
    /// <paramref name="row"/>; true when something was saved. Internal since 2026-09-19 for <see cref="ToolsMenu"/>,
    /// which hosts the Ask / Files / Web rows under its own strip.
    /// </summary>
    internal async Task<bool> EditAsync(SettingsField field, AppSettingsData saved, MenuPage page, int row, CancellationToken cancellationToken)
    {
        if (IsToggle(field))
        {
            return await PickToggleAsync(field, saved, cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.TtsVoice or SettingsField.TtsVoice2
            && await TryPickVoiceAsync(field, saved, cancellationToken).ConfigureAwait(false) is { } pickedVoice)
        {
            return pickedVoice;
        }

        if (field == SettingsField.TtsVoicePreset)
        {
            return await PickVoicePresetAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.LlmReasoning)
        {
            return await PickReasoningAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ClaudePermissions)
        {
            return await PickClaudePermissionsAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ClaudeEffort)
        {
            return await PickClaudeEffortAsync(field, saved.ClaudeEffort, ClaudeDefaultLabel, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ClaudeAdvisorEffort)
        {
            return await PickClaudeEffortAsync(field, saved.ClaudeAdvisorEffort, ClaudeAdvisorEffortLabel, cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.ClaudeModel or SettingsField.ClaudeAdvisorModel
            && await PickClaudeModelAsync(field, saved, cancellationToken).ConfigureAwait(false) is { } pickedModel)
        {
            // Null: Other… — the typed edit below, pre-filled with the saved name.
            return pickedModel;
        }

        if (field == SettingsField.ClaudeAdvisorContext)
        {
            return await PickClaudeAdvisorContextAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.LlmCompactType)
        {
            return await PickCompactTypeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.LlmToolCompactType)
        {
            return await PickToolCompactTypeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.LlmScanMode)
        {
            return await PickLlmScanModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.TtsSource)
        {
            return await PickTtsSourceAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.FileMentionFolderMode)
        {
            return await PickMentionFolderModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.FileBrowserMode)
        {
            return await PickFileBrowserModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SkillCompactMode)
        {
            return await PickSkillCompactModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ReflectionReasoning)
        {
            return await PickReflectionReasoningAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ReflectionCooldownMode)
        {
            return await PickReflectionCooldownModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.WebBrowserMode)
        {
            return await PickBrowserModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.WebBrowserNetworkMode)
        {
            return await PickNetworkModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ShellCommandPolicy)
        {
            return await PickCommandPolicyAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ShellDefault)
        {
            return await PickShellAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ShellCommandAllowed)
        {
            return await EditAllowedCommandsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ShellCodeLanguages)
        {
            return await EditCodeLanguagesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SessionNamingMode)
        {
            return await PickSessionNamingModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SessionShowName)
        {
            return await PickSessionShowNameAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.QueueCancelMode)
        {
            return await PickQueueCancelModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatLlmMode)
        {
            return await PickBotChatLlmModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.LlmMidTurnUsage)
        {
            return await PickMidTurnUsageAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatImageMode)
        {
            return await PickBotChatImageModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.BotChatTxt2ImgWorkflow or SettingsField.BotChatImg2ImgWorkflow)
        {
            return await PickBotChatWorkflowAsync(field, saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatImg2ImgMode)
        {
            return await PickBotChatImg2ImgModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatPreloadedSkills)
        {
            return await EditBotChatPreloadedSkillsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatSkillMode)
        {
            return await PickBotChatSkillModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.WelcomeSplash)
        {
            return await PickWelcomeSplashModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.WebSearchMethod)
        {
            return await PickSearchMethodAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.Theme)
        {
            return await PickThemeRowAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ImageThumbnailSize)
        {
            return await PickImageThumbnailSizeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.NewProfileMode)
        {
            return await PickNewProfileModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.WorkingDirectory && _browseFolder is not null && _pane.Enabled)
        {
            return await PickWorkingDirectoryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ObsidianVault && _browseVault is not null && _pane.Enabled)
        {
            // The folder picker (2026-09-22): a vault is a folder, and its path is long to type; it opens on the vault saved (later that day).
            string? picked = await _browseVault(saved.ObsidianVault, cancellationToken).ConfigureAwait(false);
            return picked is not null ? TrySaveObsidianVault(picked) : Unchanged();
        }

        if (field == SettingsField.SqlDefaultConnection)
        {
            return await PickSqlConnectionAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SqlConnectionsOffered)
        {
            return await EditSqlOfferedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SqlAddConnection)
        {
            return await AddSqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SqlSetPassword)
        {
            return await SetSqlPasswordAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ComfyWorkflowsOffered)
        {
            return await EditComfyOfferedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ComfyAddWorkflow)
        {
            return await AddComfyWorkflowAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.SqlConnectionsProfile or SettingsField.SqlConnectionsGlobal)
        {
            // An edit row (2026-09-23): the file in the editor, made with its commented shape first; nothing saved here.
            OpenSqlFile(field == SettingsField.SqlConnectionsProfile
                ? Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory)
                : Sql.SqlConfigFile.GlobalPath(_settings.StorageDirectory));
            return false;
        }

        if (field == SettingsField.SttPushToTalkKey)
        {
            return await PickPushToTalkAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SttWhisperModel)
        {
            return await PickWhisperModelAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SttVoskModel)
        {
            return await PickVoskModelAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        bool allowEmpty = field is SettingsField.LlmUrl or SettingsField.LlmModel or SettingsField.TtsVoice2 or SettingsField.WorkingDirectory or SettingsField.WebBrowserPath or SettingsField.WebSearxngUrl or SettingsField.DraftEditor or SettingsField.ImageEditor or SettingsField.GitNativeEmail or SettingsField.GitNativeName or SettingsField.ObsidianVault or SettingsField.ComfyUrl or SettingsField.ComfyOutputFolder or SettingsField.ClaudeExecutable or SettingsField.ClaudeModel or SettingsField.ClaudeAdvisorModel or SettingsField.ClaudeApiKey;
        var result = await EditTextAsync(field, page, row, EditableValue(field, saved), allowEmpty, cancellationToken).ConfigureAwait(false);
        if (result is not InputResult.Submitted submitted)
        {
            return Unchanged();
        }

        string text = submitted.Text.Trim();
        switch (field)
        {
            case SettingsField.LlmRequestTimeoutSeconds:
            case SettingsField.LlmTurnTimeoutSeconds:
                bool isRequest = field == SettingsField.LlmRequestTimeoutSeconds;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                    || double.IsNaN(seconds) || seconds <= 0 || seconds > (isRequest ? Llm.LlmTimeouts.MaxRequestSeconds : Llm.LlmTimeouts.MaxTurnSeconds))
                {
                    Sink.Error($"{FieldName(field)} {(isRequest ? LlmRequestTimeoutRangeError : LlmTurnTimeoutRangeError)}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => { if (isRequest) d.LlmRequestTimeoutSeconds = seconds; else d.LlmTurnTimeoutSeconds = seconds; });
                return true;

            case SettingsField.LlmContextLength:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tokens) || tokens < 0)
                {
                    Sink.Error($"{FieldName(field)} {ContextLengthRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.LlmContextLength = tokens);
                return true;

            case SettingsField.LlmCompactKeepRecent:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int keep) || keep < 0 || keep > Llm.ConversationHistory.DefaultMaxTurns)
                {
                    Sink.Error($"{FieldName(field)} {LlmCompactKeepRecentRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.LlmCompactKeepRecent = keep);
                return true;

            case SettingsField.ReflectionWindow:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int window) || window < AppSettingsData.MinReflectionWindow || window > AppSettingsData.MaxReflectionWindow)
                {
                    Sink.Error($"{FieldName(field)} {ReflectionWindowRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ReflectionWindow = window);
                return true;

            case SettingsField.ReflectionMinToolCalls:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int calls) || calls < AppSettingsData.MinReflectionMinToolCalls || calls > AppSettingsData.MaxReflectionMinToolCalls)
                {
                    Sink.Error($"{FieldName(field)} {ReflectionMinToolCallsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ReflectionMinToolCalls = calls);
                return true;

            case SettingsField.ReflectionMaxRequests:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int requests) || requests < AppSettingsData.MinReflectionMaxRequests || requests > AppSettingsData.MaxReflectionMaxRequests)
                {
                    Sink.Error($"{FieldName(field)} {ReflectionMaxRequestsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ReflectionMaxRequests = requests);
                return true;

            case SettingsField.ReflectionCooldownMinutes:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) || minutes < AppSettingsData.MinReflectionCooldownMinutes || minutes > AppSettingsData.MaxReflectionCooldownMinutes)
                {
                    Sink.Error($"{FieldName(field)} {ReflectionCooldownMinutesRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ReflectionCooldownMinutes = minutes);
                return true;

            case SettingsField.BotChatNonTtsDelaySeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pause) || pause < AppSettingsData.MinBotChatNonTtsDelaySeconds || pause > AppSettingsData.MaxBotChatNonTtsDelaySeconds)
                {
                    Sink.Error($"{FieldName(field)} {BotChatNonTtsDelayRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.BotChatNonTtsDelaySeconds = pause);
                return true;

            case SettingsField.LlmAutoCompactPercent:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int share) || share < 0 || share > 100)
                {
                    Sink.Error($"{FieldName(field)} {LlmAutoCompactPercentRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.LlmAutoCompactPercent = share);
                return true;

            case SettingsField.LlmMaxTurns:
                int cap = 0;
                if (!string.Equals(text.Trim(), LlmMaxTurnsAutoLabel, StringComparison.OrdinalIgnoreCase)
                    && (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out cap) || cap < 0 || cap > LlmMaxTurnsLimit))
                {
                    Sink.Error($"{FieldName(field)} {LlmMaxTurnsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.LlmMaxTurns = cap);
                return true;

            case SettingsField.LlmMaxToolIterations:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rounds) || rounds < AppSettingsData.MinToolIterations || rounds > AppSettingsData.MaxToolIterationsCap)
                {
                    Sink.Error($"{FieldName(field)} {MaxToolIterationsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.LlmMaxToolIterations = rounds);
                return true;

            case SettingsField.FileTreeMaxLength:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int treeLength) || treeLength < WorkingDirectory.MinTreeLength || treeLength > WorkingDirectory.MaxTreeLength)
                {
                    Sink.Error($"{FieldName(field)} {TreeMaxLengthRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.FileTreeMaxLength = treeLength);
                return true;

            case SettingsField.PastePreviewLines:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int previewLines) || previewLines < 0 || previewLines > PasteBlocks.MaxPreviewLines)
                {
                    Sink.Error($"{FieldName(field)} {PastePreviewLinesRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.PastePreviewLines = previewLines);
                return true;

            case SettingsField.ToolCollapseCount:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int collapse) || collapse < AppSettingsData.MinToolCollapseCount || collapse > AppSettingsData.MaxToolCollapseCount)
                {
                    Sink.Error($"{FieldName(field)} {ToolCollapseCountRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ToolCollapseCount = collapse);
                return true;

            case SettingsField.CodeCollapseCount:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int codeCollapse) || codeCollapse < AppSettingsData.MinCodeCollapseCount || codeCollapse > AppSettingsData.MaxCodeCollapseCount)
                {
                    Sink.Error($"{FieldName(field)} {CodeCollapseCountRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.CodeCollapseCount = codeCollapse);
                return true;

            case SettingsField.WebSearchMaxResults:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hits) || hits < AppSettingsData.MinWebSearchMaxResults || hits > AppSettingsData.MaxWebSearchMaxResults)
                {
                    Sink.Error($"{FieldName(field)} {WebSearchMaxResultsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.WebSearchMaxResults = hits);
                return true;

            case SettingsField.GitNativeDiffMaxLines:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int diffLines) || diffLines < AppSettingsData.MinGitNativeDiffMaxLines || diffLines > AppSettingsData.MaxGitNativeDiffMaxLines)
                {
                    Sink.Error($"{FieldName(field)} {GitNativeDiffMaxLinesRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.GitNativeDiffMaxLines = diffLines);
                return true;

            case SettingsField.GitNativeLogMaxCommits:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int logCommits) || logCommits < AppSettingsData.MinGitNativeLogMaxCommits || logCommits > AppSettingsData.MaxGitNativeLogMaxCommits)
                {
                    Sink.Error($"{FieldName(field)} {GitNativeLogMaxCommitsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.GitNativeLogMaxCommits = logCommits);
                return true;

            case SettingsField.SqlQueryMaxRows:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sqlRows) || sqlRows < AppSettingsData.MinSqlQueryMaxRows || sqlRows > AppSettingsData.MaxSqlQueryMaxRows)
                {
                    Sink.Error($"{FieldName(field)} {SqlQueryMaxRowsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.SqlQueryMaxRows = sqlRows);
                return true;

            case SettingsField.SqlQueryTimeoutSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sqlTimeout) || sqlTimeout < AppSettingsData.MinSqlQueryTimeoutSeconds || sqlTimeout > AppSettingsData.MaxSqlQueryTimeoutSeconds)
                {
                    Sink.Error($"{FieldName(field)} {SqlQueryTimeoutRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.SqlQueryTimeoutSeconds = sqlTimeout);
                return true;

            case SettingsField.ComfyTimeoutSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int comfyTimeout) || comfyTimeout < AppSettingsData.MinComfyTimeoutSeconds || comfyTimeout > AppSettingsData.MaxComfyTimeoutSeconds)
                {
                    Sink.Error($"{FieldName(field)} {ComfyTimeoutRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ComfyTimeoutSeconds = comfyTimeout);
                return true;

            case SettingsField.ComfyMaxPicturesPerCall:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int comfyPictures) || comfyPictures < AppSettingsData.MinComfyMaxPicturesPerCall || comfyPictures > AppSettingsData.MaxComfyMaxPicturesPerCall)
                {
                    Sink.Error($"{FieldName(field)} {ComfyMaxPicturesRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ComfyMaxPicturesPerCall = comfyPictures);
                return true;

            case SettingsField.ShellTimeoutSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int shellTimeout) || shellTimeout < AppSettingsData.MinShellTimeoutSeconds || shellTimeout > AppSettingsData.MaxShellTimeoutSeconds)
                {
                    Sink.Error($"{FieldName(field)} {ShellTimeoutSecondsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ShellTimeoutSeconds = shellTimeout);
                return true;

            case SettingsField.ShellForegroundCapSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int shellCap) || shellCap < AppSettingsData.MinShellForegroundCapSeconds || shellCap > AppSettingsData.MaxShellForegroundCapSeconds)
                {
                    Sink.Error($"{FieldName(field)} {ShellForegroundCapSecondsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ShellForegroundCapSeconds = shellCap);
                return true;

            case SettingsField.ShellOutputMaxChars:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int shellChars) || shellChars < AppSettingsData.MinShellOutputMaxChars || shellChars > AppSettingsData.MaxShellOutputMaxChars)
                {
                    Sink.Error($"{FieldName(field)} {ShellOutputMaxCharsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ShellOutputMaxChars = shellChars);
                return true;

            case SettingsField.ShellCodeTimeoutSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int codeTimeout) || codeTimeout < AppSettingsData.MinShellCodeTimeoutSeconds || codeTimeout > AppSettingsData.MaxShellCodeTimeoutSeconds)
                {
                    Sink.Error($"{FieldName(field)} {ShellCodeTimeoutSecondsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ShellCodeTimeoutSeconds = codeTimeout);
                return true;

            case SettingsField.ShellCodeMaxToolCalls:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int codeCalls) || codeCalls < AppSettingsData.MinShellCodeMaxToolCalls || codeCalls > AppSettingsData.MaxShellCodeMaxToolCalls)
                {
                    Sink.Error($"{FieldName(field)} {ShellCodeMaxToolCallsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ShellCodeMaxToolCalls = codeCalls);
                return true;

            case SettingsField.FileViewImageMaxPerCall:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pictures) || pictures < AppSettingsData.MinViewImageMaxPerCall || pictures > AppSettingsData.MaxViewImageMaxPerCall)
                {
                    Sink.Error($"{FieldName(field)} {ViewImageMaxPerCallRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.FileViewImageMaxPerCall = pictures);
                return true;

            case SettingsField.McpConnectTimeoutSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mcpSeconds) || mcpSeconds < AppSettingsData.MinMcpConnectTimeout || mcpSeconds > AppSettingsData.MaxMcpConnectTimeout)
                {
                    Sink.Error($"{FieldName(field)} {McpConnectTimeoutRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.McpConnectTimeoutSeconds = mcpSeconds);
                return true;

            case SettingsField.SessionRetentionDays:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) || days < AppSettingsData.MinSessionRetentionDays || days > AppSettingsData.MaxSessionRetentionDays)
                {
                    Sink.Error($"{FieldName(field)} {SessionRetentionDaysRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.SessionRetentionDays = days);
                return true;

            case SettingsField.SessionSearchMaxResults:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sessionHits) || sessionHits < AppSettingsData.MinSessionSearchMaxResults || sessionHits > AppSettingsData.MaxSessionSearchMaxResults)
                {
                    Sink.Error($"{FieldName(field)} {SessionSearchMaxResultsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.SessionSearchMaxResults = sessionHits);
                return true;

            case SettingsField.AskMaxQuestions:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int questions) || questions < AppSettingsData.MinAskMaxQuestions || questions > AppSettingsData.MaxAskMaxQuestions)
                {
                    Sink.Error($"{FieldName(field)} {AskMaxQuestionsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.AskMaxQuestions = questions);
                return true;

            case SettingsField.AskMaxChoices:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int choices) || choices < AppSettingsData.MinAskMaxChoices || choices > AppSettingsData.MaxAskMaxChoices)
                {
                    Sink.Error($"{FieldName(field)} {AskMaxChoicesRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.AskMaxChoices = choices);
                return true;

            case SettingsField.WebBrowserPath:
                if (text.Length > 0 && !File.Exists(text))
                {
                    Sink.Error($"{FieldName(field)} {BrowserPathError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory, _locateBrowser(""))}.");
                    return false;
                }

                Apply(field, d => d.WebBrowserPath = text.Length == 0 ? "" : Path.GetFullPath(text));
                return true;

            case SettingsField.ClaudeExecutable:
                text = text.Trim('"');
                if (text.Length > 0 && !File.Exists(text))
                {
                    Sink.Error($"{FieldName(field)} {ClaudeExecutableError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.ClaudeExecutable = text.Length == 0 ? "" : Path.GetFullPath(text));
                return true;

            case SettingsField.ClaudeModel:
                // An alias or a full name: whatever the CLI takes, checked by the CLI at the next /claude.
                Apply(field, d => d.ClaudeModel = text);
                return true;

            case SettingsField.ClaudeAdvisorModel:
                // The same, for the advisor; empty follows the Claude slash command model.
                Apply(field, d => d.ClaudeAdvisorModel = text);
                return true;

            case SettingsField.ClaudeAdvisorCallsPerTurn:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int advisorCalls) || advisorCalls < AppSettingsData.MinClaudeAdvisorCallsPerTurn || advisorCalls > AppSettingsData.MaxClaudeAdvisorCallsPerTurn)
                {
                    Sink.Error($"{FieldName(field)} {ClaudeAdvisorCallsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ClaudeAdvisorCallsPerTurn = advisorCalls);
                return true;

            case SettingsField.ClaudeApiMaxTokens:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int claudeMax) || claudeMax < AppSettingsData.MinClaudeApiMaxTokens || claudeMax > AppSettingsData.MaxClaudeApiMaxTokens)
                {
                    Sink.Error($"{FieldName(field)} {ClaudeApiMaxTokensRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ClaudeApiMaxTokens = claudeMax);
                return true;

            case SettingsField.ClaudeApiKey:
                // Encrypted for this Windows user before it reaches the file (2026-09-27); where DPAPI fails, kept as typed and said so.
                string protectedKey = Llm.Anthropic.ClaudeApi.Protect(text, out string? protectError);
                if (protectError is not null)
                {
                    Sink.Warning(ClaudeApiKeyPlainWarning(protectError));
                }

                Apply(field, d => d.ClaudeApiKey = protectedKey);
                return true;

            case SettingsField.WebSearxngUrl:
                if (text.Length > 0 && !(Uri.TryCreate(text, UriKind.Absolute, out var searxng) && Web.WebFetcher.IsHttp(searxng)))
                {
                    Sink.Error($"{FieldName(field)} {SearxngUrlError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.WebSearxngUrl = text);
                return true;

            case SettingsField.ComfyUrl:
                if (text.Length > 0 && !(Uri.TryCreate(text, UriKind.Absolute, out var comfy) && Web.WebFetcher.IsHttp(comfy)))
                {
                    Sink.Error($"{FieldName(field)} {ComfyUrlError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.ComfyUrl = text);
                return true;

            case SettingsField.ComfyOutputFolder:
                // Under the sandbox only: a rooted path or one climbing out would be refused at every call, so it is refused here once.
                if (Path.IsPathRooted(text) || text.Replace('\\', '/').Split('/').Contains(".."))
                {
                    Sink.Error($"{FieldName(field)} {ComfyOutputFolderError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.ComfyOutputFolder = text);
                return true;

            case SettingsField.ImageEditor:
                // A command line, as the draft editor's: a word cmd cannot find shows at the next double-click.
                Apply(field, d => d.ImageEditor = text);
                return true;

            case SettingsField.DraftEditor:
                // A command line, not a path: nothing to check here — a word cmd cannot find shows at the next /draft.
                Apply(field, d => d.DraftEditor = text);
                return true;

            case SettingsField.GitNativeEmail:
                // Whatever git accepts (2026-09-21): an address is not checked here, and empty is "not set".
                Apply(field, d => d.GitNativeEmail = text);
                return true;

            case SettingsField.GitNativeName:
                Apply(field, d => d.GitNativeName = text);
                return true;

            case SettingsField.TtsSpeed:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed)
                    || double.IsNaN(speed) || speed < AppSettingsData.MinTtsSpeed || speed > AppSettingsData.MaxTtsSpeed)
                {
                    Sink.Error($"{FieldName(field)} {TtsSpeedRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.TtsSpeed = speed);
                await PreviewBlendAsync(saved, saved.TtsVoiceMix, speed, cancellationToken).ConfigureAwait(false);
                return true;

            case SettingsField.TtsVoiceMix:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mix)
                    || mix < AppSettingsData.MinTtsVoiceMix || mix > AppSettingsData.MaxTtsVoiceMix)
                {
                    Sink.Error($"{FieldName(field)} {TtsVoiceMixRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.TtsVoiceMix = mix);
                await PreviewBlendAsync(saved, mix, saved.TtsSpeed, cancellationToken).ConfigureAwait(false);
                return true;

            case SettingsField.SttInterruptEchoGuard:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int echo)
                    || echo < AppSettingsData.MinSttInterruptEchoGuard || echo > AppSettingsData.MaxSttInterruptEchoGuard)
                {
                    Sink.Error($"{FieldName(field)} {SttInterruptEchoGuardRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.SttInterruptEchoGuard = echo);
                return true;

            case SettingsField.SttInterruptConfirmMs:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int confirm)
                    || confirm < AppSettingsData.MinSttInterruptConfirmMs || confirm > AppSettingsData.MaxSttInterruptConfirmMs)
                {
                    Sink.Error($"{FieldName(field)} {SttInterruptConfirmRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.SttInterruptConfirmMs = confirm);
                return true;

            case SettingsField.TtsVoice2:
                // Typed with no server list: an empty line clears the secondary voice, anything else is saved as typed.
                Apply(field, d => d.TtsVoice2 = text);
                return true;

            case SettingsField.WorkingDirectory:
                return TrySaveWorkingDirectory(text);

            case SettingsField.ObsidianVault:
                return TrySaveObsidianVault(text);

            case SettingsField.SttWakePhrase:
                if (!IsWakePhraseCandidate(text))
                {
                    Sink.Error($"{FieldName(field)} {WakePhraseError}; keeping {saved.SttWakePhrase}.");
                    return false;
                }

                Apply(field, d => d.SttWakePhrase = WakeWordMatch.NormalizePhrase(text));
                return true;

            case SettingsField.TtsHttpUrl:
            case SettingsField.TtsVoice:
            case SettingsField.LlmApiKey:
                if (text.Length == 0)
                {
                    Sink.Error($"{FieldName(field)} cannot be empty; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                goto case SettingsField.LlmUrl;

            case SettingsField.LlmUrl:
            case SettingsField.LlmModel:
                Apply(field, d =>
                {
                    switch (field)
                    {
                        case SettingsField.LlmUrl: d.LlmUrl = text; break;
                        case SettingsField.LlmModel: d.LlmModel = text; break;
                        case SettingsField.LlmApiKey: d.LlmApiKey = text; break;
                        case SettingsField.TtsHttpUrl: d.TtsHttpUrl = text; break;
                        case SettingsField.TtsVoice: d.TtsVoice = text; break;
                    }
                });
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The voice picker over the server's list, with the saved voice always offered (first, when
    /// the server does not list it). For <see cref="SettingsField.TtsVoice2"/> the first row is
    /// <see cref="NoSecondaryVoice"/>, which clears it. Null means the server listed nothing:
    /// fall back to typing.
    ///
    /// <para>The preview: while <see cref="SettingsField.TtsVoicePreview"/> and <c>TTS output</c>
    /// are on and the session is ready, every row the cursor lands on (the pane's hook; the
    /// no-pane prompt has none) speaks <see cref="VoicePreviewText"/> in that voice alone —
    /// <see cref="NoSecondaryVoice"/> plays nothing; the <see cref="SettingsField.TtsVoiceMix"/> and
    /// <see cref="SettingsField.TtsSpeed"/> rows speak the blend at the value they just saved the same
    /// way (<see cref="PreviewBlendAsync"/>, on either host, since a typed edit has no cursor). Every
    /// preview plays what the rows say — the saved voices and speed, never the session's connect-time
    /// ones. The steps run one after the other on
    /// <see cref="_preview"/>: each first awaits <see cref="SpeechSession.StopAsync"/> (two speakers
    /// never touch the device at once) and then speaks only when no newer highlight has come, so
    /// scrolling fast sounds only the row the cursor stops on. The picker's return awaits the
    /// chain, so nothing runs behind the list afterwards but the speaker itself: the row the cursor
    /// rested on last (Enter's pick, or the one ESC left) finishes as a tail under the input line,
    /// like a timer alert.</para>
    /// </summary>
    private async Task<bool?> TryPickVoiceAsync(SettingsField field, AppSettingsData saved, CancellationToken cancellationToken)
    {
        var listed = await _transcript.WithSpinnerAsync(ListingVoicesLabel, () => _speech.ListVoicesAsync(_speech.RequestFor(saved), cancellationToken)).ConfigureAwait(false);
        if (listed is null || !listed.Value.Exists || listed.Value.Voices.Count == 0)
        {
            Sink.Notice(VoicesUnavailableNotice(listed?.Detail ?? "not a valid URL"));
            return null;
        }

        bool secondary = field == SettingsField.TtsVoice2;
        string current = secondary ? saved.TtsVoice2 : saved.TtsVoice;
        var voices = listed.Value.Voices.ToList();
        if (!string.IsNullOrWhiteSpace(current) && !voices.Contains(current, StringComparer.Ordinal))
        {
            voices.Insert(0, current);
        }

        if (secondary)
        {
            voices.Insert(0, NoSecondaryVoice);
            if (string.IsNullOrWhiteSpace(current))
            {
                current = NoSecondaryVoice;
            }
        }

        // The secondary picker's "(none)" sits at row 0: Backspace jumps there, Enter still saves.
        var page = new MenuPage(Crumb(FieldName(field)), voices.Select(Markup.Escape).ToList(), secondary ? NoneKeys : PickKeys)
        {
            BackspaceRow = secondary ? 0 : null,
        };
        Action<int>? highlighted = PreviewWanted(saved)
            ? row => QueuePreview(_speech.RequestFor(saved), voices[row] == NoSecondaryVoice ? "" : voices[row], saved.TtsSpeed, cancellationToken)
            : null;
        int? picked;
        try
        {
            picked = await PickAsync(page, voices.IndexOf(current), cancellationToken, highlighted).ConfigureAwait(false);
        }
        finally
        {
            await FinishPreviewAsync().ConfigureAwait(false);
        }

        if (picked is not { } i)
        {
            return Unchanged();
        }

        string voice = voices[i];
        if (secondary)
        {
            Apply(field, d => d.TtsVoice2 = voice == NoSecondaryVoice ? "" : voice);
        }
        else
        {
            Apply(field, d => d.TtsVoice = voice);
        }

        return true;
    }

    /// <summary>The preview's gate: the toggle, <c>TTS output</c> as saved (a flip in the same visit counts) and the session as of the last connect.</summary>
    private bool PreviewWanted(AppSettingsData saved) => !_midTurn && saved.TtsVoicePreview && saved.TtsOutput && _speech.IsReady;

    /// <summary>
    /// The <see cref="SettingsField.TtsVoicePreset"/> picker (2026-09-27, the user's ask): one <see cref="PresetRows"/> row per
    /// <see cref="Speech.VoicePresets.Load"/> entry, the cursor on the preset the saved four match (the first row when they match
    /// none). While <see cref="PreviewWanted"/>, the highlighted row speaks <see cref="VoicePreviewText"/> in its blend at its speed,
    /// the voice pickers' way (<see cref="TryPickVoiceAsync"/>). A pick writes the four settings in one save; the saved notice
    /// names the preset, and every variable that still overrides one of the four is warned about.
    /// </summary>
    private async Task<bool> PickVoicePresetAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var presets = Speech.VoicePresets.Load(_settings.StorageDirectory);
        if (presets.Count == 0)
        {
            Sink.Notice(NoVoicePresetsNotice);
            return false;
        }

        var matched = Speech.VoicePresets.Match(presets, saved);
        int start = matched is null ? 0 : IndexOf(presets, matched);
        var page = new MenuPage(Crumb(FieldName(SettingsField.TtsVoicePreset)), PresetRows(presets).Select(Markup.Escape).ToList(), PickKeys);
        Action<int>? highlighted = PreviewWanted(saved)
            ? row => QueuePreview(_speech.RequestFor(saved), VoiceMix.Spec(presets[row].Voice, presets[row].Voice2, presets[row].Mix), presets[row].Speed, cancellationToken)
            : null;
        int? picked;
        try
        {
            picked = await PickAsync(page, start, cancellationToken, highlighted).ConfigureAwait(false);
        }
        finally
        {
            await FinishPreviewAsync().ConfigureAwait(false);
        }

        if (picked is not { } index)
        {
            return Unchanged();
        }

        var preset = presets[index];
        _settings.Update(d => Speech.VoicePresets.ApplyTo(preset, d));
        Sink.Notice(PresetSavedNotice(preset));
        foreach (var overriddenBy in new[] { SettingsField.TtsVoice, SettingsField.TtsVoice2, SettingsField.TtsVoiceMix, SettingsField.TtsSpeed }
                     .Select(_overriddenBy).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            Sink.Warning(OverrideNotice(overriddenBy));
        }

        return true;

        static int IndexOf(IReadOnlyList<Speech.VoicePreset> list, Speech.VoicePreset item)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], item))
                {
                    return i;
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// The mix and speed rows' preview, when wanted: the blend as the next reply will send it
    /// (<see cref="VoiceMix.Spec"/> over the saved voices with the session's rule for a blank
    /// primary — one voice alone when there is no second) at <paramref name="speed"/>, awaited
    /// before the row returns so nothing runs behind the list.
    /// </summary>
    private async Task PreviewBlendAsync(AppSettingsData saved, int mix, double speed, CancellationToken cancellationToken)
    {
        if (!PreviewWanted(saved))
        {
            return;
        }

        string primary = string.IsNullOrWhiteSpace(saved.TtsVoice) ? new AppSettingsData().TtsVoice : saved.TtsVoice.Trim();
        QueuePreview(_speech.RequestFor(saved), VoiceMix.Spec(primary, saved.TtsVoice2, mix), speed, cancellationToken);
        await FinishPreviewAsync().ConfigureAwait(false);
    }

    /// <summary>Waits for the chain so nothing runs behind the list; the speaker it started, if any, plays on as the tail.</summary>
    private async Task FinishPreviewAsync()
    {
        var chain = _preview;
        _preview = Task.CompletedTask;
        await chain.ConfigureAwait(false);
    }

    /// <summary>One highlight of the voice picker, or a mix / speed save: the next step of the chain (see <see cref="TryPickVoiceAsync"/>). An empty <paramref name="voice"/> only silences the last one.</summary>
    private void QueuePreview(SynthesizerRequest? request, string voice, double speed, CancellationToken cancellationToken)
    {
        int serial = ++_previewSerial;
        _preview = PreviewStepAsync(_preview, serial, request, voice, speed, cancellationToken);
    }

    private async Task PreviewStepAsync(Task previous, int serial, SynthesizerRequest? request, string voice, double speed, CancellationToken cancellationToken)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await _speech.StopAsync().ConfigureAwait(false);
            if (serial != _previewSerial || voice.Length == 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (_speech.BeginPreview(request, voice, speed, cancellationToken) is { } speaker)
            {
                speaker.Feed(VoicePreviewText);
                speaker.CompleteAdding();
            }
        }
        catch (OperationCanceledException)
        {
            // The app is closing; the session stops the speaker.
        }
    }

    /// <summary>The settings row's reasoning picker: the five levels as a second level of the pane, opened on the saved one. Never falls back to typing.</summary>
    private async Task<bool> PickReasoningAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.LlmReasoning)), ReasoningRows(), PickKeys);
        int? picked = await PickAsync(page, ReasoningCursor(saved.LlmReasoning), cancellationToken).ConfigureAwait(false);
        return picked is { } index ? SaveReasoning(Llm.ReasoningLevel.Levels[index]) : Unchanged();
    }

    /// <summary>
    /// <c>/reasoning</c>: saves <paramref name="requestedLevel"/> directly when given (one of
    /// <see cref="Llm.ReasoningLevel.Levels"/>, any case; anything else is <see cref="ReasoningLevelError"/>),
    /// otherwise the five levels as a one-level list opened on <paramref name="current"/> (the level in
    /// force, like <c>/model</c> opens on the model in use). Returns true when the saved level changed;
    /// ESC keeps it (<see cref="UnchangedNotice"/>). Without menus the guard prints.
    /// </summary>
    public async Task<bool> PickReasoningAsync(string requestedLevel, string current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedLevel);
        ArgumentNullException.ThrowIfNull(current);
        if (!string.IsNullOrWhiteSpace(requestedLevel))
        {
            if (!Llm.ReasoningLevel.TryParse(requestedLevel, out var effort))
            {
                Flow.Error(ReasoningLevelError);
                return false;
            }

            return SaveReasoning(Llm.ReasoningLevel.Name(effort));
        }

        if (!CanShowMenus())
        {
            Flow.Error(MenusNeedTerminalError);
            return false;
        }

        var page = new MenuPage(ReasoningTitle, ReasoningRows(), KeepKeys);
        int? picked = await PickOnceAsync(page, ReasoningCursor(current), cancellationToken).ConfigureAwait(false);
        return picked is { } index ? SaveReasoning(Llm.ReasoningLevel.Levels[index]) : Unchanged();
    }

    /// <summary>One <see cref="ReasoningLabel"/> row per level, in <see cref="Llm.ReasoningLevel.Levels"/> order.</summary>
    private static List<string> ReasoningRows() => Llm.ReasoningLevel.Levels.Select(ReasoningLabel).ToList();

    /// <summary>The Claude-permissions picker under the settings list: one <see cref="ClaudePermissionLabel"/> row per <see cref="Claude.ClaudePermission.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickClaudePermissionsAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.ClaudePermissions)), Claude.ClaudePermission.Names.Select(ClaudePermissionLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Claude.ClaudePermission.Names, saved.ClaudePermissions), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Claude.ClaudePermission.Names[index];
        Apply(SettingsField.ClaudePermissions, d => d.ClaudePermissions = name);
        return true;
    }

    /// <summary>
    /// A Claude-effort picker under the settings list (<c>Claude slash command effort</c>, <c>Claude advisor tool effort</c>): <paramref name="blank"/>
    /// first (the CLI's default, or the command's for the advisor), then <c>--effort</c>'s words; the saved one under the cursor.
    /// </summary>
    private async Task<bool> PickClaudeEffortAsync(SettingsField field, string saved, string blank, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(field)), Claude.ClaudeEffort.Names.Select(name => ClaudeEffortLabel(name, blank)).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(Claude.ClaudeEffort.Names, saved.Trim().ToLowerInvariant())), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Claude.ClaudeEffort.Names[index];
        Apply(field, field == SettingsField.ClaudeAdvisorEffort ? d => d.ClaudeAdvisorEffort = name : d => d.ClaudeEffort = name);
        return true;
    }

    /// <summary>
    /// A Claude-model picker under the settings list (2026-09-27, the user's ask: pick, don't type): <paramref name="field"/>'s
    /// blank row first (the CLI's default, or the command's for the advisor), then <see cref="Claude.ClaudeModels.Aliases"/>, then
    /// <c>Other…</c>; the cursor on the saved alias, the blank row for none, <c>Other…</c> for a full name. True/false as any
    /// picker (saved, or ESC); null for <c>Other…</c>, which the caller turns into the typed edit.
    /// </summary>
    private async Task<bool?> PickClaudeModelAsync(SettingsField field, AppSettingsData saved, CancellationToken cancellationToken)
    {
        bool advisor = field == SettingsField.ClaudeAdvisorModel;
        string current = advisor ? saved.ClaudeAdvisorModel : saved.ClaudeModel;
        string blank = advisor ? ClaudeAdvisorModelLabel : ClaudeDefaultLabel;
        var rows = new List<string> { ClaudeModelLabel("", blank) };
        rows.AddRange(Claude.ClaudeModels.Aliases.Select(alias => ClaudeModelLabel(alias, blank)));
        rows.Add(ClaudeModelOtherLabel(current));
        int other = rows.Count - 1;
        int cursor = string.IsNullOrWhiteSpace(current) ? 0
            : Claude.ClaudeModels.AliasOf(current) is { } alias ? 1 + Array.IndexOf(Claude.ClaudeModels.Aliases, alias)
            : other;
        var page = new MenuPage(Crumb(FieldName(field)), rows, PickKeys);
        int? picked = await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        if (index == other)
        {
            return null;
        }

        string name = index == 0 ? "" : Claude.ClaudeModels.Aliases[index - 1];
        Apply(field, advisor ? d => d.ClaudeAdvisorModel = name : d => d.ClaudeModel = name);
        return true;
    }

    /// <summary>The advisor-context picker under the settings list: one <see cref="ClaudeAdvisorContextLabel"/> row per <see cref="Claude.ClaudeAdvisorContext.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickClaudeAdvisorContextAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.ClaudeAdvisorContext)), Claude.ClaudeAdvisorContext.Names.Select(ClaudeAdvisorContextLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(Claude.ClaudeAdvisorContext.Names, saved.ClaudeAdvisorContext.Trim().ToLowerInvariant())), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Claude.ClaudeAdvisorContext.Names[index];
        Apply(SettingsField.ClaudeAdvisorContext, d => d.ClaudeAdvisorContext = name);
        return true;
    }

    /// <summary>The compact-type picker under the settings list: one <see cref="CompactTypeLabel"/> row per <see cref="Llm.CompactType.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickCompactTypeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.LlmCompactType)), Llm.CompactType.Names.Select(CompactTypeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Llm.CompactType.Names, saved.LlmCompactType), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Llm.CompactType.Names[index];
        Apply(SettingsField.LlmCompactType, d => d.LlmCompactType = name);
        return true;
    }

    /// <summary>The tool-compact-type picker under the settings list: one <see cref="ToolCompactTypeLabel"/> row per <see cref="Llm.ToolCompactType.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickToolCompactTypeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.LlmToolCompactType)), Llm.ToolCompactType.Names.Select(ToolCompactTypeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Llm.ToolCompactType.Names, saved.LlmToolCompactType), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Llm.ToolCompactType.Names[index];
        Apply(SettingsField.LlmToolCompactType, d => d.LlmToolCompactType = name);
        return true;
    }

    /// <summary>The @-mention-folder-mode picker under the settings list: one <see cref="MentionFolderModeLabel"/> row per <see cref="Files.MentionFolderMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickMentionFolderModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.FileMentionFolderMode)), Files.MentionFolderMode.Names.Select(MentionFolderModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Files.MentionFolderMode.Names, saved.FileMentionFolderMode), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Files.MentionFolderMode.Names[index];
        Apply(SettingsField.FileMentionFolderMode, d => d.FileMentionFolderMode = name);
        return true;
    }

    /// <summary>The file-browser-mode picker under the settings list (2026-09-21): one <see cref="FileBrowserModeLabel"/> row per <see cref="Files.FileBrowserMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickFileBrowserModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.FileBrowserMode)), Files.FileBrowserMode.Names.Select(FileBrowserModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Files.FileBrowserMode.Names, saved.FileBrowserMode), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Files.FileBrowserMode.Names[index];
        Apply(SettingsField.FileBrowserMode, d => d.FileBrowserMode = name);
        return true;
    }

    /// <summary>The skill-compact-mode picker under the settings list: one <see cref="SkillCompactModeLabel"/> row per <see cref="Skills.SkillCompactMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickSkillCompactModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.SkillCompactMode)), Skills.SkillCompactMode.Names.Select(SkillCompactModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Skills.SkillCompactMode.Names, saved.SkillCompactMode), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Skills.SkillCompactMode.Names[index];
        Apply(SettingsField.SkillCompactMode, d => d.SkillCompactMode = name);
        return true;
    }

    /// <summary>The session-naming-mode picker under the settings list: one <see cref="SessionNamingModeLabel"/> row per <see cref="Sessions.SessionNamingMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickSessionNamingModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.SessionNamingMode)), Sessions.SessionNamingMode.Names.Select(SessionNamingModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Sessions.SessionNamingMode.Names, saved.SessionNamingMode), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Sessions.SessionNamingMode.Names[index];
        Apply(SettingsField.SessionNamingMode, d => d.SessionNamingMode = name);
        return true;
    }

    /// <summary>The session-show-name picker under the settings list: one <see cref="SessionShowNameLabel"/> row per <see cref="Sessions.SessionShowName.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickSessionShowNameAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.SessionShowName)), Sessions.SessionShowName.Names.Select(SessionShowNameLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Sessions.SessionShowName.Names, saved.SessionShowName), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Sessions.SessionShowName.Names[index];
        Apply(SettingsField.SessionShowName, d => d.SessionShowName = name);
        return true;
    }

    /// <summary>The cooldown-mode picker under the settings list: one <see cref="ReflectionCooldownModeLabel"/> row per <see cref="Skills.ReflectionCooldownMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickReflectionCooldownModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.ReflectionCooldownMode)), Skills.ReflectionCooldownMode.Names.Select(ReflectionCooldownModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Skills.ReflectionCooldownMode.Names, saved.ReflectionCooldownMode), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Skills.ReflectionCooldownMode.Names[index];
        Apply(SettingsField.ReflectionCooldownMode, d => d.ReflectionCooldownMode = name);
        return true;
    }

    /// <summary>The reflection-reasoning picker under the settings list: one <see cref="ReflectionReasoningLabel"/> row per <see cref="Skills.ReflectionReasoning.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickReflectionReasoningAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.ReflectionReasoning)), Skills.ReflectionReasoning.Names.Select(ReflectionReasoningLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Skills.ReflectionReasoning.Names, saved.ReflectionReasoning), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Skills.ReflectionReasoning.Names[index];
        Apply(SettingsField.ReflectionReasoning, d => d.ReflectionReasoning = name);
        return true;
    }

    /// <summary>
    /// An on/off setting under the settings list (2026-09-17, the user's call: every switch a picker
    /// like the rest, each choice with a sentence on what it does): two <see cref="ToggleLabel"/>
    /// rows, the saved value under the cursor. ESC, or the saved value picked again, is
    /// <see cref="UnchangedNotice"/> and no change (no reconnect, no conversation cleared). The
    /// interrupt needs the wake word: with it off the page never opens
    /// (<see cref="ChatScreen.InterruptNeedsWakeNotice"/>), and the wake word going off takes the
    /// interrupt with it (<see cref="ChatScreen.InterruptOffWithWakeNotice"/>).
    /// </summary>
    /// <summary>
    /// A toggle's on/off page opened straight, over the saved values (2026-09-22, for <see cref="ToolsMenu.ShowPoliceAsync"/>:
    /// <c>/police</c>, the toolbar's officer) — the page its row's Enter opens, under whatever <see cref="Root"/> the caller set.
    /// True when the value changed.
    /// </summary>
    internal Task<bool> EditToggleAsync(SettingsField field, CancellationToken cancellationToken) =>
        PickToggleAsync(field, _settings.Current, cancellationToken);

    private async Task<bool> PickToggleAsync(SettingsField field, AppSettingsData saved, CancellationToken cancellationToken)
    {
        // The interrupt is the wake phrase during a reply: it needs the wake word on, and goes off with it.
        if (field == SettingsField.SttInterrupt && !saved.SttInterrupt && !saved.SttWake)
        {
            Sink.Notice(ChatScreen.InterruptNeedsWakeNotice);
            return false;
        }

        bool was = IsOn(field, saved);
        var page = new MenuPage(Crumb(FieldName(field)), [ToggleLabel(field, true), ToggleLabel(field, false)], PickKeys);
        int? picked = await PickAsync(page, was ? 0 : 1, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index || (index == 0) == was)
        {
            return Unchanged();
        }

        bool on = index == 0;
        bool takesInterrupt = field == SettingsField.SttWake && !on && saved.SttInterrupt;
        Apply(field, data =>
        {
            SetToggle(field, data, on);
            if (field == SettingsField.SttWake && !on)
            {
                data.SttInterrupt = false;
            }
        });
        if (takesInterrupt)
        {
            Sink.Notice(ChatScreen.InterruptOffWithWakeNotice);
        }

        return true;
    }

    /// <summary>The value of an on/off setting; false for a field that is not one. The one reader, so <see cref="FieldValue"/> and the picker's cursor agree. Pinned.</summary>
    public static bool IsOn(SettingsField field, AppSettingsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return field switch
        {
            SettingsField.TtsOutput => data.TtsOutput,
            SettingsField.SttInput => data.SttInput,
            SettingsField.SttWake => data.SttWake,
            SettingsField.SttInterrupt => data.SttInterrupt,
            SettingsField.Memory => data.Memory,
            SettingsField.CopyUserPrompt => data.CopyUserPrompt,
            SettingsField.ShowImageThumbnails => data.ShowImageThumbnails,
            SettingsField.FileTreeShowSizes => data.FileTreeShowSizes,
            SettingsField.LlmOfferTools => data.LlmOfferTools,
            SettingsField.LlmUseFunVerbs => data.LlmUseFunVerbs,
            SettingsField.LlmShowThinking => data.LlmShowThinking,
            SettingsField.LlmPreserveThinking => data.LlmPreserveThinking,
            SettingsField.SessionSaveThinking => data.SessionSaveThinking,
            SettingsField.WebTools => data.WebTools,
            SettingsField.GitNativeTools => data.GitNativeTools,
            SettingsField.ObsidianTools => data.ObsidianTools,
            SettingsField.ObsidianAllowDelete => data.ObsidianAllowDelete,
            SettingsField.SqlTools => data.SqlTools,
            SettingsField.ComfyTools => data.ComfyTools,
            SettingsField.ComfyReinforceNegatives => data.ComfyReinforceNegatives,
            SettingsField.ComfyShowPrompts => data.ComfyShowPrompts,
            SettingsField.ComfyCaretMention => data.ComfyCaretMention,
            SettingsField.ComfyPictureStrip => data.ComfyPictureStrip,
            SettingsField.ClaudeAdvisor => data.ClaudeAdvisor,
            SettingsField.ClaudeAdvisorConfirm => data.ClaudeAdvisorConfirm,
            SettingsField.ClaudeApi => data.ClaudeApi,
            SettingsField.ClaudeApiPromptCaching => data.ClaudeApiPromptCaching,
            SettingsField.BotChatImages => data.BotChatImages,
            SettingsField.BotChatImageAsync => data.BotChatImageAsync,
            SettingsField.BotChatSkills => data.BotChatSkills,
            SettingsField.BotChatVision => data.BotChatVision,
            SettingsField.SqlPercentMention => data.SqlPercentMention,
            SettingsField.LlmCompactShowSummary => data.LlmCompactShowSummary,
            SettingsField.TtsVoicePreview => data.TtsVoicePreview,
            SettingsField.FileTools => data.FileTools,
            SettingsField.AskUser => data.AskUser,
            SettingsField.AgentSkills => data.AgentSkills,
            SettingsField.ExternalSkills => data.ExternalSkills,
            SettingsField.TranscriptMarkdown => data.TranscriptMarkdown,
            SettingsField.FileSafeEdits => data.FileSafeEdits,
            SettingsField.SkillHashMention => data.SkillHashMention,
            SettingsField.ToolsDollarMention => data.ToolsDollarMention,
            SettingsField.McpServers => data.McpServers,
            SettingsField.ReflectionAutoLearn => data.ReflectionAutoLearn,
            SettingsField.ReflectionIncludesSessions => data.ReflectionIncludesSessions,
            SettingsField.ReflectionYieldsToTurns => data.ReflectionYieldsToTurns,
            SettingsField.ReflectionEditsSupportingFiles => data.ReflectionEditsSupportingFiles,
            SettingsField.HideExitAutocomplete => data.HideExitAutocomplete,
            SettingsField.CommandTypoIntercept => data.CommandTypoIntercept,
            SettingsField.KeepCommandHistory => data.KeepCommandHistory,
            SettingsField.ShowWorkingDirectory => data.ShowWorkingDirectory,
            SettingsField.ShowToolbar => data.ShowToolbar,
            SettingsField.ThemedViewer => data.ThemedViewer,
            SettingsField.QueueMessages => data.QueueMessages,
            SettingsField.SessionLogging => data.SessionLogging,
            SettingsField.SessionTool => data.SessionTool,
            SettingsField.ShellToolBridge => data.ShellToolBridge,
            SettingsField.ShellPoliceOutsidePaths => data.ShellPoliceOutsidePaths,
            SettingsField.ShellPreferNative => data.ShellPreferNative,
            _ => false,
        };
    }

    /// <summary>The one writer of an on/off setting (the picker's pick); nothing for a field that is not one.</summary>
    private static void SetToggle(SettingsField field, AppSettingsData data, bool on)
    {
        switch (field)
        {
            case SettingsField.TtsOutput: data.TtsOutput = on; break;
            case SettingsField.SttInput: data.SttInput = on; break;
            case SettingsField.SttWake: data.SttWake = on; break;
            case SettingsField.SttInterrupt: data.SttInterrupt = on; break;
            case SettingsField.Memory: data.Memory = on; break;
            case SettingsField.CopyUserPrompt: data.CopyUserPrompt = on; break;
            case SettingsField.ShowImageThumbnails: data.ShowImageThumbnails = on; break;
            case SettingsField.FileTreeShowSizes: data.FileTreeShowSizes = on; break;
            case SettingsField.LlmOfferTools: data.LlmOfferTools = on; break;
            case SettingsField.LlmUseFunVerbs: data.LlmUseFunVerbs = on; break;
            case SettingsField.LlmShowThinking: data.LlmShowThinking = on; break;
            case SettingsField.LlmPreserveThinking: data.LlmPreserveThinking = on; break;
            case SettingsField.SessionSaveThinking: data.SessionSaveThinking = on; break;
            case SettingsField.WebTools: data.WebTools = on; break;
            case SettingsField.GitNativeTools: data.GitNativeTools = on; break;
            case SettingsField.ObsidianTools: data.ObsidianTools = on; break;
            case SettingsField.ObsidianAllowDelete: data.ObsidianAllowDelete = on; break;
            case SettingsField.SqlTools: data.SqlTools = on; break;
            case SettingsField.ComfyTools: data.ComfyTools = on; break;
            case SettingsField.ComfyReinforceNegatives: data.ComfyReinforceNegatives = on; break;
            case SettingsField.ComfyShowPrompts: data.ComfyShowPrompts = on; break;
            case SettingsField.ComfyCaretMention: data.ComfyCaretMention = on; break;
            case SettingsField.ComfyPictureStrip: data.ComfyPictureStrip = on; break;
            case SettingsField.ClaudeAdvisor: data.ClaudeAdvisor = on; break;
            case SettingsField.ClaudeAdvisorConfirm: data.ClaudeAdvisorConfirm = on; break;
            case SettingsField.ClaudeApi: data.ClaudeApi = on; break;
            case SettingsField.ClaudeApiPromptCaching: data.ClaudeApiPromptCaching = on; break;
            case SettingsField.BotChatImages: data.BotChatImages = on; break;
            case SettingsField.BotChatImageAsync: data.BotChatImageAsync = on; break;
            case SettingsField.BotChatSkills: data.BotChatSkills = on; break;
            case SettingsField.BotChatVision: data.BotChatVision = on; break;
            case SettingsField.SqlPercentMention: data.SqlPercentMention = on; break;
            case SettingsField.LlmCompactShowSummary: data.LlmCompactShowSummary = on; break;
            case SettingsField.TtsVoicePreview: data.TtsVoicePreview = on; break;
            case SettingsField.FileTools: data.FileTools = on; break;
            case SettingsField.AskUser: data.AskUser = on; break;
            case SettingsField.AgentSkills: data.AgentSkills = on; break;
            case SettingsField.ExternalSkills: data.ExternalSkills = on; break;
            case SettingsField.TranscriptMarkdown: data.TranscriptMarkdown = on; break;
            case SettingsField.FileSafeEdits: data.FileSafeEdits = on; break;
            case SettingsField.SkillHashMention: data.SkillHashMention = on; break;
            case SettingsField.ToolsDollarMention: data.ToolsDollarMention = on; break;
            case SettingsField.McpServers: data.McpServers = on; break;
            case SettingsField.ReflectionAutoLearn: data.ReflectionAutoLearn = on; break;
            case SettingsField.ReflectionIncludesSessions: data.ReflectionIncludesSessions = on; break;
            case SettingsField.ReflectionYieldsToTurns: data.ReflectionYieldsToTurns = on; break;
            case SettingsField.ReflectionEditsSupportingFiles: data.ReflectionEditsSupportingFiles = on; break;
            case SettingsField.HideExitAutocomplete: data.HideExitAutocomplete = on; break;
            case SettingsField.CommandTypoIntercept: data.CommandTypoIntercept = on; break;
            case SettingsField.KeepCommandHistory: data.KeepCommandHistory = on; break;
            case SettingsField.ShowWorkingDirectory: data.ShowWorkingDirectory = on; break;
            case SettingsField.ShowToolbar: data.ShowToolbar = on; break;
            case SettingsField.ThemedViewer: data.ThemedViewer = on; break;
            case SettingsField.QueueMessages: data.QueueMessages = on; break;
            case SettingsField.SessionLogging: data.SessionLogging = on; break;
            case SettingsField.SessionTool: data.SessionTool = on; break;
            case SettingsField.ShellToolBridge: data.ShellToolBridge = on; break;
            case SettingsField.ShellPoliceOutsidePaths: data.ShellPoliceOutsidePaths = on; break;
            case SettingsField.ShellPreferNative: data.ShellPreferNative = on; break;
        }
    }

    /// <summary>One row of an on/off picker: <c>on</c> or <c>off</c>, padded to four, and what it means for the field. Pinned.</summary>
    public static string ToggleLabel(SettingsField field, bool on) =>
        Markup.Escape((on ? "on" : "off").PadRight(4)) + Theme.DimMarkup(ToggleDescribe(field, on));

    /// <summary>
    /// What <c>on</c> and <c>off</c> mean for each on/off setting, one sentence each, after the word on
    /// the picker's row (2026-09-17); empty for a field that is not a toggle. Pinned.
    /// </summary>
    public static string ToggleDescribe(SettingsField field, bool on) => field switch
    {
        SettingsField.Memory => on ? "memory enabled" : "memory disabled",
        SettingsField.CopyUserPrompt => on ? "/copy copies user prompts and model replies" : "/copy copies model replies only",
        SettingsField.ShowImageThumbnails => on ? "image thumbnails are shown in the chat transcript" : "no image thumbnails in the chat transcript",
        SettingsField.TranscriptMarkdown => on ? "replies are styled as Markdown in the pane" : "replies stream as plain text",
        SettingsField.LlmOfferTools => on ? "the model gets the tools; a change starts a new conversation" : "no tools at all; a change starts a new conversation",
        SettingsField.LlmUseFunVerbs => on ? "the thinking spinner reads a random verb" : "the spinner reads thinking",
        SettingsField.LlmShowThinking => on ? "thinking shown in chat" : "thinking not shown in chat",
        SettingsField.LlmPreserveThinking => on ? "every turn's thinking goes back to the server" : "only the current turn's thinking goes back to the server",
        SettingsField.SessionSaveThinking => on ? "thinking is saved with the session" : "thinking is not saved with the session",
        SettingsField.TtsOutput => on ? "replies are read aloud" : "replies are text only",
        SettingsField.TtsVoicePreview => on ? "the voice pickers speak the highlighted voice" : "the voice pickers are silent",
        SettingsField.SttInput => on ? "the push-to-talk key records a spoken message" : "the microphone is off",
        SettingsField.SttWake => on ? "the wake phrase starts a listen at the idle line" : "only the push-to-talk key listens",
        SettingsField.SttInterrupt => on ? "the wake phrase during a spoken reply stops it" : "a spoken reply plays to its end",
        SettingsField.AskUser => on ? "ask user enabled" : "ask user disabled",
        SettingsField.FileTools => on ? "file tools enabled" : "file tools disabled",
        SettingsField.FileSafeEdits => on ? "edit and delete operations move copies to .trash first" : "edit and delete operations function normally",
        SettingsField.FileTreeShowSizes => on ? "/tree carries each file's size" : "/tree names alone",
        SettingsField.WebTools => on ? "web tools enabled" : "web tools disabled",
        SettingsField.GitNativeTools => on ? "git (native) tools enabled" : "git (native) tools disabled",
        SettingsField.ObsidianTools => on ? "Obsidian tools enabled" : "Obsidian tools disabled",
        SettingsField.ObsidianAllowDelete => on ? "vault_delete may move a note or attachment to the vault's .trash" : "vault_delete is disabled",
        SettingsField.SqlTools => on ? "the model reads the SQL Server connections of sql.json" : "no SQL tools",
        SettingsField.ComfyTools => on ? "ComfyUI tools enabled" : "ComfyUI tools disabled",
        SettingsField.ComfyReinforceNegatives => on ? "the model adds a few opposite tags to a workflow's negative" : "the workflow's negative as it is",
        SettingsField.ComfyShowPrompts => on ? "the prompts and params sent to ComfyUI under each picture's line" : "just the picture's line",
        SettingsField.ComfyCaretMention => on ? "^ and part of a name lists the offered workflows on the line" : "^ is ordinary text",
        SettingsField.ComfyPictureStrip => on ? "the session's pictures in a strip above the line" : "no strip",
        SettingsField.ClaudeAdvisor => on ? "the model may ask Claude for advice (claude_advisor, read-only)" : "no claude_advisor",
        SettingsField.ClaudeAdvisorConfirm => on ? "each claude_advisor call waits for your yes" : "claude_advisor runs without asking",
        SettingsField.ClaudeApi => on ? "/server offers the Claude API while a key is set (billed per message)" : "the Claude API is not offered",
        SettingsField.ClaudeApiPromptCaching => on ? "the prompt and conversation are cached between requests (cheaper)" : "every request is billed in full",
        SettingsField.BotChatImages => on ? "/botchat draws pictures while the ComfyUI tools are offered" : "/botchat is talk alone",
        SettingsField.BotChatImageAsync => on ? "the next bot answers while the picture renders" : "the chat waits for each picture",
        SettingsField.BotChatSkills => on ? "the bots get load_skill over this profile's and the global skills" : "/botchat bots get no skills",
        SettingsField.BotChatVision => on ? "each bot sees the pictures shown since it last spoke (vision models)" : "the bots see text alone",
        SettingsField.SqlPercentMention => on ? "% and part of a name lists the SQL connections on the line" : "% is ordinary text",
        SettingsField.LlmCompactShowSummary => on ? "the summary's lines or the pruned results, then the protected counts" : "the one compact notice alone",
        SettingsField.AgentSkills => on ? "the skills catalog, load_skill and skill_editor are offered" : "no skills, no project notes",
        SettingsField.ExternalSkills => on ? "%USERPROFILE%\\.agents\\skills is read too" : "profile and global skills only",
        SettingsField.SkillHashMention => on ? "# and part of a name lists the loaded skills on the line" : "# is ordinary text",
        SettingsField.ToolsDollarMention => on ? "$ and part of a name lists the offered tools on the line" : "$ is ordinary text",
        SettingsField.McpServers => on ? "the configured MCP servers connect and their tools are offered" : "no MCP server is started; the pane still lists the config",
        SettingsField.ReflectionAutoLearn => on ? "automatic reflection enabled" : "automatic reflection disabled",
        SettingsField.ReflectionIncludesSessions => on ? "a reflection can read past sessions for insights" : "a reflection reads the conversation on screen alone",
        SettingsField.ReflectionYieldsToTurns => on ? "a turn pauses a running reflection which may resume after the turn" : "reflections run asynchronously (when the model allows)",
        SettingsField.ReflectionEditsSupportingFiles => on ? "a reflection may write a skill's supporting files" : "a reflection writes a skill's SKILL.md alone",
        SettingsField.HideExitAutocomplete => on ? "hide '/exit' from the autocomplete list" : "show '/exit' in the autocomplete list",
        SettingsField.CommandTypoIntercept => on ? "a command typed without its slash or with extra ones offers the command" : "a command typed without its slash or with extra ones is sent as typed",
        SettingsField.KeepCommandHistory => on ? "command history enabled" : "command history disabled",
        SettingsField.ShowWorkingDirectory => on ? "show the working directory in the header" : "hide the working directory in the header",
        SettingsField.ShowToolbar => on ? "show the toolbar" : "hide the toolbar",
        SettingsField.ThemedViewer => on ? "theme the picture viewer" : "keep the picture viewer black",
        SettingsField.QueueMessages => on ? "a message sent while a reply runs is queued and sent when the reply ends" : "a message sent during a reply goes when it ends, unlisted; no /queue",
        SettingsField.SessionLogging => on ? "every completed turn is written to this profile's session store" : "nothing is written; what is stored still lists, restores and purges",
        SettingsField.SessionTool => on ? "the model can search, list and read this profile's earlier sessions" : "the model never sees an earlier session",
        SettingsField.ShellToolBridge => on ? "a script may call this app's other tools through its neon_tools module" : "a script does everything itself: no neon_tools module, no tool calls",
        SettingsField.ShellPoliceOutsidePaths => on ? "paths outside the working directory are denied" : "paths anywhere on the computer are allowed",
        SettingsField.ShellPreferNative => on ? "a command a native tool covers is sent back to that tool first" : "the shell runs whatever it is given",
        _ => "",
    };

    /// <summary>The TTS-source picker under the settings list: one <see cref="TtsSourceLabel"/> row per <see cref="Speech.TtsSource.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickTtsSourceAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.TtsSource)), Speech.TtsSource.Names.Select(TtsSourceLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Speech.TtsSource.Names, saved.TtsSource), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Speech.TtsSource.Names[index];
        Apply(SettingsField.TtsSource, d => d.TtsSource = name);
        return true;
    }

    /// <summary>The scan-mode picker under the settings list: one <see cref="LlmScanModeLabel"/> row per <see cref="Llm.LlmScanMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickLlmScanModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.LlmScanMode)), Llm.LlmScanMode.Names.Select(LlmScanModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Llm.LlmScanMode.Names, saved.LlmScanMode), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Llm.LlmScanMode.Names[index];
        Apply(SettingsField.LlmScanMode, d => d.LlmScanMode = name);
        return true;
    }

    /// <summary>The browser-mode picker under the settings list: one <see cref="BrowserModeLabel"/> row per <see cref="Web.BrowserMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickBrowserModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.WebBrowserMode)), Web.BrowserMode.Names.Select(BrowserModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Web.BrowserMode.Names, saved.WebBrowserMode), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Web.BrowserMode.Names[index];
        Apply(SettingsField.WebBrowserMode, d => d.WebBrowserMode = name);
        return true;
    }

    /// <summary>The network-mode picker under the settings list: one <see cref="NetworkModeLabel"/> row per <see cref="Web.NetworkMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickNetworkModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.WebBrowserNetworkMode)), Web.NetworkMode.Names.Select(NetworkModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Web.NetworkMode.Names, saved.WebBrowserNetworkMode), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Web.NetworkMode.Names[index];
        Apply(SettingsField.WebBrowserNetworkMode, d => d.WebBrowserNetworkMode = name);
        return true;
    }

    /// <summary>The command-policy picker under the settings list (2026-09-21): one <see cref="CommandPolicyLabel"/> row per <see cref="Shell.CommandPolicy.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickCommandPolicyAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.ShellCommandPolicy)), Shell.CommandPolicy.Names.Select(CommandPolicyLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Shell.CommandPolicy.Names, saved.ShellCommandPolicy), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Shell.CommandPolicy.Names[index];
        Apply(SettingsField.ShellCommandPolicy, d => d.ShellCommandPolicy = name);
        return true;
    }

    /// <summary>The default-shell picker under the settings list (2026-09-21): one <see cref="ShellLabel"/> row per <see cref="Shell.ShellKinds.Names"/> entry, a shell not installed noted, the saved one under the cursor. A shell not found can still be picked: the row is the wish, the tool says what is missing.</summary>
    /// <summary>
    /// The <c>SQL default connection</c> pick (2026-09-23): <see cref="FirstSqlConnectionLabel"/>, then every connection
    /// <c>sql.json</c> holds now (the profile's, then the home's), the cursor on the one saved.
    /// </summary>
    private async Task<bool> PickSqlConnectionAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = Sql.SqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Offered(saved.SqlConnectionsOffered).Connections.Select(c => c.Name).ToList();
        var rows = new List<string> { Markup.Escape(FirstSqlConnectionLabel) };
        rows.AddRange(names.Select(Markup.Escape));
        int current = names.FindIndex(n => string.Equals(n, saved.SqlDefaultConnection, StringComparison.OrdinalIgnoreCase));
        var page = new MenuPage(Crumb(FieldName(SettingsField.SqlDefaultConnection)), rows, PickKeys);
        int? picked = await PickAsync(page, current + 1, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = index == 0 ? "" : names[index - 1];
        Apply(SettingsField.SqlDefaultConnection, d => d.SqlDefaultConnection = name);
        return true;
    }

    /// <summary>
    /// <c>SQL connections offered</c> (later on 2026-09-23): every connection the two files hold, ticked or not, Enter or
    /// Space flipping one and the list shown again until ESC (the <see cref="EditCodeLanguagesAsync"/> shape). The first
    /// flip of a profile that never narrowed it saves every name but the flipped one — from then on the list is exact,
    /// and a connection added later stays hidden until ticked (the user's call). True when anything changed.
    /// </summary>
    private async Task<bool> EditSqlOfferedAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var loaded = Sql.SqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
            if (loaded.Connections.Count == 0)
            {
                Sink.Error(Sql.SqlText.NoConnections);
                return changed;
            }

            var offered = _settings.Current.SqlConnectionsOffered;
            var on = loaded.Offered(offered).Connections.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int width = loaded.Connections.Max(c => c.Name.Length) + 2;
            var page = new MenuPage(Crumb(FieldName(SettingsField.SqlConnectionsOffered)), loaded.Connections.Select(c => SqlOfferedRow(c, on.Contains(c.Name), width)).ToList(), ToggleKeys) { SpaceToggles = true };
            int? picked = await PickAsync(page, Math.Min(cursor, loaded.Connections.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } index)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = index;
            string name = loaded.Connections[index].Name;
            var next = loaded.Connections.Select(c => c.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            // A name ticked before but no longer in the files stays in the list: it counts again if the connection comes back.
            if (offered is not null)
            {
                next.AddRange(offered.Where(n => !loaded.Connections.Any(c => string.Equals(c.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))));
            }

            Apply(SettingsField.SqlConnectionsOffered, d => d.SqlConnectionsOffered = next);
            changed = true;
        }
    }

    /// <summary>
    /// <c>SQL set password</c> (later on 2026-09-23, the user's call: a masked prompt, with auto-encryption as the net
    /// under a password typed into the file): the connections that take a password, each with its store; then a masked
    /// slot (<see cref="UI.InputLine"/>'s <c>mask</c>) under the picked one; then <see cref="Sql.SqlSecrets.Save"/> —
    /// nothing in the settings changes, so the answer is false either way and the status line says what happened.
    /// ESC or an empty Enter saves nothing.
    /// </summary>
    private async Task<bool> SetSqlPasswordAsync(CancellationToken cancellationToken)
    {
        var connections = Sql.SqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Connections.Where(c => c.Config.NeedsPassword).ToList();
        if (connections.Count == 0)
        {
            Sink.Error(Sql.SqlText.NoPasswordConnections);
            return false;
        }

        var page = new MenuPage(Crumb(FieldName(SettingsField.SqlSetPassword)), connections.Select(c => Markup.Escape(SqlPasswordRow(c))).ToList(), PickKeys);
        if (await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { } index)
        {
            return Unchanged();
        }

        var connection = connections[index];
        InputResult result;
        if (_pane.Enabled)
        {
            result = await _pane.EditAsync(page with { Hint = EditKeys }, index, _input, "", allowEmpty: false, cancellationToken, mask: true).ConfigureAwait(false);
        }
        else
        {
            Flow.Notice(PromptTitle(FieldName(SettingsField.SqlSetPassword) + " · " + connection.Name, EditKeys));
            result = await _input.ReadAsync("", remember: false, allowEmpty: false, cancellationToken: cancellationToken, escapeCancels: true, mask: true).ConfigureAwait(false);
        }

        if (result is not InputResult.Submitted { Text.Length: > 0 } submitted)
        {
            return Unchanged();
        }

        var (saved, notice) = Sql.SqlSecrets.Save(connection, submitted.Text);
        if (saved)
        {
            Sink.Notice(notice);
        }
        else
        {
            Sink.Error(notice);
        }

        return false;
    }

    /// <summary>Opens one <c>sql.json</c> in the editor, made first when missing; without an opener (tests, headless) or on an IO failure, the status line says so.</summary>
    private void OpenSqlFile(string path)
    {
        try
        {
            Sql.SqlConfigFile.EnsureExists(path);
            if (_openFile is null)
            {
                Sink.Error(SqlEditFailedError(path, "no editor to open it in"));
                return;
            }

            _openFile(path);
            Sink.Notice(SqlEditingNotice(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Sink.Error(SqlEditFailedError(path, Diagnostics.LogText.Excerpt(ex.Message)));
        }
    }

    private async Task<bool> PickShellAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var installed = _installedShells();
        var page = new MenuPage(Crumb(FieldName(SettingsField.ShellDefault)), Shell.ShellKinds.Names.Select(name => ShellLabel(name, installed.Contains(name))).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Shell.ShellKinds.Names, saved.ShellDefault), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Shell.ShellKinds.Names[index];
        Apply(SettingsField.ShellDefault, d => d.ShellDefault = name);
        return true;
    }

    /// <summary>
    /// The allowed-commands list under the settings list (2026-09-21): one row per prefix saved for
    /// good, Enter removing it (the list re-shown until ESC); <see cref="NoAllowedCommandsRow"/> alone
    /// while it is empty. The session's own allows are not here: they live in the process, not the file.
    /// True when anything was removed. Internal since later on 2026-09-21 for <see cref="ToolsMenu.ShowAllowedCommandsAsync"/>,
    /// which opens it straight under the Tools crumb (<c>/cmdlist</c>, the toolbar lock).
    /// </summary>
    internal async Task<bool> EditAllowedCommandsAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var allowed = Shell.CommandAllowList.Merge(_settings.Current.ShellCommandAllowed, []);
            IReadOnlyList<string> rows = allowed.Count == 0 ? [Markup.Escape(NoAllowedCommandsRow)] : allowed.Select(Markup.Escape).ToList();
            var page = new MenuPage(Crumb(FieldName(SettingsField.ShellCommandAllowed)), rows, allowed.Count == 0 ? PickKeys : RemoveKeys);
            int? picked = await PickAsync(page, Math.Min(cursor, rows.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } index || allowed.Count == 0)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            string prefix = allowed[index];
            _settings.Update(d => d.ShellCommandAllowed = Shell.CommandAllowList.Without(d.ShellCommandAllowed, prefix));
            Sink.Notice(PrefixRemovedNotice(prefix));
            changed = true;
            cursor = index;
        }
    }

    /// <summary>
    /// The code-languages list under the settings list (2026-09-21): one <see cref="CodeLanguageLabel"/> row
    /// per <see cref="Shell.CodeLanguages.Names"/> entry, Enter or Space flipping it and saving at once, the
    /// list re-shown until ESC; the last language on refuses to go (<see cref="LastLanguageError"/>).
    /// True when anything was flipped.
    /// </summary>
    private async Task<bool> EditCodeLanguagesAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        var installed = _installedLanguages();
        while (true)
        {
            var enabled = Shell.CodeLanguages.Resolve(_settings.Current).Select(Shell.CodeLanguages.Name).ToHashSet(StringComparer.Ordinal);
            var page = new MenuPage(Crumb(FieldName(SettingsField.ShellCodeLanguages)), Shell.CodeLanguages.Names.Select(name => CodeLanguageLabel(name, enabled.Contains(name), installed.Contains(name))).ToList(), ToggleKeys) { SpaceToggles = true };
            int? picked = await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
            if (picked is not { } index)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = index;
            string name = Shell.CodeLanguages.Names[index];
            if (enabled.Contains(name) && enabled.Count == 1)
            {
                Sink.Error(LastLanguageError);
                continue;
            }

            var next = Shell.CodeLanguages.Names.Where(n => enabled.Contains(n) != string.Equals(n, name, StringComparison.Ordinal)).ToList();
            Apply(SettingsField.ShellCodeLanguages, d => d.ShellCodeLanguages = next);
            changed = true;
        }
    }

    /// <summary>The queue-cancel-mode picker under the settings list: one <see cref="QueueCancelModeLabel"/> row per <see cref="QueueCancelMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickQueueCancelModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.QueueCancelMode)), QueueCancelMode.Names.Select(QueueCancelModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(QueueCancelMode.Names, saved.QueueCancelMode), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = QueueCancelMode.Names[index];
        Apply(SettingsField.QueueCancelMode, d => d.QueueCancelMode = name);
        return true;
    }

    /// <summary>The botchat-LLM-mode picker under the settings list (later on 2026-09-25): one <see cref="BotChatLlmModeLabel"/> row per <see cref="App.BotChatLlmMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickBotChatLlmModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = App.BotChatLlmMode.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.BotChatLlmMode)), names.Select(BotChatLlmModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, saved.BotChatLlmMode)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.BotChatLlmMode, d => d.BotChatLlmMode = name);
        return true;
    }

    /// <summary>The mid-turn-usage picker under the settings list (2026-09-25): one <see cref="MidTurnUsageLabel"/> row per <see cref="MidTurnUsageMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickMidTurnUsageAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = MidTurnUsageMode.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.LlmMidTurnUsage)), names.Select(MidTurnUsageLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, saved.LlmMidTurnUsage)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.LlmMidTurnUsage, d => d.LlmMidTurnUsage = name);
        return true;
    }

    /// <summary>The botchat-image-mode picker under the settings list (2026-09-25): one <see cref="BotChatImageModeLabel"/> row per <see cref="App.BotChatImageMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickBotChatImageModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = App.BotChatImageMode.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.BotChatImageMode)), names.Select(BotChatImageModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, saved.BotChatImageMode)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.BotChatImageMode, d => d.BotChatImageMode = name);
        return true;
    }

    /// <summary>
    /// The <c>Botchat txt2img workflow</c> / <c>Botchat img2img workflow</c> pick (2026-09-25; both kinds 2026-09-27):
    /// <see cref="NoBotChatWorkflowLabel"/>, then every installed workflow of the field's kind (<see cref="BotChat.Txt2ImgWorkflows"/>,
    /// <see cref="BotChat.Img2ImgWorkflows"/>) with its family and size, the cursor on the one saved — <c>ComfyUI workflows
    /// offered</c> has no say since later on 2026-09-27 (the user's call: it is the main chat's list alone).
    /// </summary>
    private async Task<bool> PickBotChatWorkflowAsync(SettingsField field, AppSettingsData saved, CancellationToken cancellationToken)
    {
        bool img2img = field == SettingsField.BotChatImg2ImgWorkflow;
        var installed = InstalledComfyWorkflows(_settings.ProfileDirectory);
        var workflows = img2img ? BotChat.Img2ImgWorkflows(installed) : BotChat.Txt2ImgWorkflows(installed);
        int width = workflows.Count == 0 ? 0 : workflows.Max(w => w.Name.Length) + 2;
        var rows = new List<string> { Markup.Escape(NoBotChatWorkflowLabel) };
        rows.AddRange(workflows.Select(w => Markup.Escape(w.Name.PadRight(width)) + Theme.DimMarkup(Comfy.ComfyFamilies.Name(w.Family) + " · " + Invariant(w.Defaults.Width) + "×" + Invariant(w.Defaults.Height))));
        string? savedName = (img2img ? saved.BotChatImg2ImgWorkflow : saved.BotChatTxt2ImgWorkflow)?.Trim();
        int current = workflows.ToList().FindIndex(w => string.Equals(w.Name, savedName, StringComparison.OrdinalIgnoreCase));
        var page = new MenuPage(Crumb(FieldName(field)), rows, PickKeys);
        int? picked = await PickAsync(page, current + 1, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string? name = index == 0 ? null : workflows[index - 1].Name;
        Apply(field, d =>
        {
            if (img2img)
            {
                d.BotChatImg2ImgWorkflow = name;
            }
            else
            {
                d.BotChatTxt2ImgWorkflow = name;
            }
        });
        return true;
    }

    /// <summary>The botchat-skill-mode picker under the settings list (2026-09-27): one <see cref="BotChatSkillModeLabel"/> row per <see cref="App.BotChatSkillMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickBotChatSkillModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = App.BotChatSkillMode.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.BotChatSkillMode)), names.Select(BotChatSkillModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, saved.BotChatSkillMode)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.BotChatSkillMode, d => d.BotChatSkillMode = name);
        return true;
    }

    /// <summary>
    /// The <c>Botchat preloaded skills</c> checklist (2026-09-27), <see cref="EditComfyOfferedAsync"/>'s loop over the skills a
    /// botchat sees: Enter or Space flips one, saved at once; a name ticked before but no longer installed stays in the list.
    /// </summary>
    private async Task<bool> EditBotChatPreloadedSkillsAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var skills = _botChatSkills();
            if (skills.Count == 0)
            {
                Sink.Error(NoSkillsToPreload);
                return changed;
            }

            var chosen = _settings.Current.BotChatPreloadedSkills ?? [];
            var on = chosen.Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int width = skills.Max(s => s.Name.Length) + 2;
            var page = new MenuPage(Crumb(FieldName(SettingsField.BotChatPreloadedSkills)), skills.Select(s => PreloadedSkillRow(s, on.Contains(s.Name), width)).ToList(), ToggleKeys) { SpaceToggles = true };
            int? picked = await PickAsync(page, Math.Min(cursor, skills.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } index)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = index;
            string name = skills[index].Name;
            var next = skills.Select(s => s.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            next.AddRange(chosen.Where(n => !skills.Any(s => string.Equals(s.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))));
            Apply(SettingsField.BotChatPreloadedSkills, d => d.BotChatPreloadedSkills = next.Count == 0 ? null : next);
            changed = true;
        }
    }

    /// <summary>The botchat-img2img-mode picker under the settings list (2026-09-27): one <see cref="BotChatImg2ImgModeLabel"/> row per <see cref="App.BotChatImg2ImgMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickBotChatImg2ImgModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = App.BotChatImg2ImgMode.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.BotChatImg2ImgMode)), names.Select(BotChatImg2ImgModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, saved.BotChatImg2ImgMode)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.BotChatImg2ImgMode, d => d.BotChatImg2ImgMode = name);
        return true;
    }

    /// <summary>The welcome-splash picker under the settings list: one <see cref="WelcomeSplashModeLabel"/> row per <see cref="UI.SplashMode.Names"/> entry, the saved one under the cursor (2026-09-24).</summary>
    private async Task<bool> PickWelcomeSplashModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.WelcomeSplash)), UI.SplashMode.Names.Select(WelcomeSplashModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(UI.SplashMode.Names, saved.WelcomeSplashMode), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = UI.SplashMode.Names[index];
        Apply(SettingsField.WelcomeSplash, d => d.WelcomeSplashMode = name);
        return true;
    }

    /// <summary>The search-method picker under the settings list: one <see cref="SearchMethodLabel"/> row per <see cref="Web.SearchMethod.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickSearchMethodAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.WebSearchMethod)), Web.SearchMethod.Names.Select(SearchMethodLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Web.SearchMethod.Names, saved.WebSearchMethod), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Web.SearchMethod.Names[index];
        Apply(SettingsField.WebSearchMethod, d => d.WebSearchMethod = name);
        return true;
    }

    /// <summary>The thumbnail-size picker under the settings list: one <see cref="ImageThumbnailSizeLabel"/> row per <see cref="ThumbnailSize.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickImageThumbnailSizeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.ImageThumbnailSize)), ThumbnailSize.Names.Select(ImageThumbnailSizeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(ThumbnailSize.Names, saved.ImageThumbnailSize), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = ThumbnailSize.Names[index];
        Apply(SettingsField.ImageThumbnailSize, d => d.ImageThumbnailSize = name);
        return true;
    }

    /// <summary>The theme picker under the settings list: one <see cref="ThemeLabel"/> row per <see cref="ThemeName.Names"/> entry, the saved one under the cursor. The pick is put in force at once, so the pane wears it; the screen starts over when the pane closes (<see cref="SettingsChanges.Theme"/>).</summary>
    private async Task<bool> PickThemeRowAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.Theme)), ThemeRows(), PickKeys);
        int? picked = await PickAsync(page, ThemeCursor(saved.Theme), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        var palette = ThemePalette.All[index];
        Apply(SettingsField.Theme, d => d.Theme = palette.Name);
        Theme.Use(palette);
        return true;
    }

    /// <summary>
    /// <c>/theme</c> (2026-09-23, the user's ask): saves and puts in force <paramref name="requestedName"/>
    /// when given (one of <see cref="ThemeName.Names"/>, any case; anything else is <see cref="ThemeNameError"/>),
    /// otherwise the themes as a one-level list opened on the one in force. Quiet on a change — the
    /// screen starts over and says so itself — and returns true only when the theme in force changed;
    /// the one already in force says <see cref="ThemeAlreadyNotice"/>, ESC <see cref="UnchangedNotice"/>.
    /// Without menus the guard prints.
    /// </summary>
    public async Task<bool> PickThemeAsync(string requestedName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedName);
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            if (!ThemeName.TryParse(requestedName, out var named))
            {
                Flow.Error(ThemeNameError(requestedName.Trim()));
                return false;
            }

            return SaveTheme(named);
        }

        if (!CanShowMenus())
        {
            Flow.Error(MenusNeedTerminalError);
            return false;
        }

        var page = new MenuPage(ThemeTitle, ThemeRows(), KeepKeys);
        int? picked = await PickOnceAsync(page, ThemeCursor(Theme.Current.Name), cancellationToken).ConfigureAwait(false);
        return picked is { } index ? SaveTheme(ThemePalette.All[index]) : Unchanged();
    }

    /// <summary>Saves <paramref name="palette"/>'s name (when the saved one differs) and puts it in force; true when the theme in force changed, else <see cref="ThemeAlreadyNotice"/>.</summary>
    private bool SaveTheme(ThemePalette palette)
    {
        bool changed = !ReferenceEquals(Theme.Current, palette);
        if (!string.Equals(_settings.Current.Theme, palette.Name, StringComparison.Ordinal))
        {
            _settings.Update(d => d.Theme = palette.Name);
        }

        Theme.Use(palette);
        if (!changed)
        {
            Sink.Notice(ThemeAlreadyNotice(palette.Name));
        }

        return changed;
    }

    /// <summary>One <see cref="ThemeLabel"/> row per theme, in <see cref="ThemeName.Names"/> order.</summary>
    private static List<string> ThemeRows() => ThemeName.Names.Select(ThemeLabel).ToList();

    /// <summary>The row of <paramref name="name"/> (an unknown one reads as the default's, the first).</summary>
    private static int ThemeCursor(string name) => ThemeName.TryParse(name, out var palette) ? Array.IndexOf(ThemeName.Names, palette.Name) : 0;

    /// <summary>The new-profile-mode picker under the settings list: one <see cref="NewProfileModeLabel"/> row per <see cref="NewProfileMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickNewProfileModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.NewProfileMode)), NewProfileMode.Names.Select(NewProfileModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(NewProfileMode.Names, saved.NewProfileMode), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = NewProfileMode.Names[index];
        Apply(SettingsField.NewProfileMode, d => d.NewProfileMode = name);
        return true;
    }

    /// <summary>The row of <paramref name="level"/> in <see cref="Llm.ReasoningLevel.Levels"/>, or -1 when it is not one.</summary>
    private static int ReasoningCursor(string level) => Array.IndexOf(Llm.ReasoningLevel.Levels, level);

    private bool SaveReasoning(string level)
    {
        Apply(SettingsField.LlmReasoning, d => d.LlmReasoning = level);
        return true;
    }

    /// <summary>The settings row's push-to-talk picker: <see cref="PushToTalkKeys"/> as a second level of the pane, opened on the saved key. Never falls back to typing.</summary>
    private async Task<bool> PickPushToTalkAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.SttPushToTalkKey)), PushToTalkRows(), PickKeys);
        int? picked = await PickAsync(page, PushToTalkCursor(saved.SttPushToTalkKey), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        Apply(SettingsField.SttPushToTalkKey, d => d.SttPushToTalkKey = PushToTalkKeys[index].ToString());
        return true;
    }

    /// <summary>One <see cref="PushToTalkLabel"/> row per key, in <see cref="PushToTalkKeys"/> order.</summary>
    private static List<string> PushToTalkRows() => PushToTalkKeys.Select(PushToTalkLabel).ToList();

    /// <summary>The row of the saved key name in <see cref="PushToTalkKeys"/>, or -1 (the first row) when it is not one.</summary>
    private static int PushToTalkCursor(string? saved) =>
        Enum.TryParse<ConsoleKey>((saved ?? "").Trim(), ignoreCase: true, out var key) ? Array.IndexOf(PushToTalkKeys, key) : -1;

    /// <summary>The settings row's whisper-model picker: <see cref="ModelStore.WhisperModelNames"/> as a second level of the pane, opened on the saved name. Never falls back to typing.</summary>
    private async Task<bool> PickWhisperModelAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.SttWhisperModel)), WhisperModelRows(), PickKeys);
        int? picked = await PickAsync(page, WhisperModelCursor(saved.SttWhisperModel), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        Apply(SettingsField.SttWhisperModel, d => d.SttWhisperModel = ModelStore.WhisperModelNames[index]);
        return true;
    }

    /// <summary>One <see cref="WhisperModelLabel"/> row per name, in <see cref="ModelStore.WhisperModelNames"/> order, sized from the download table.</summary>
    private List<string> WhisperModelRows() =>
        ModelStore.WhisperModelNames.Select(name => WhisperModelLabel(name, ModelStore.ResolveWhisper(name, _settings.StorageDirectory)?.ApproxBytes ?? 0)).ToList();

    /// <summary>The row of the saved name in <see cref="ModelStore.WhisperModelNames"/> (any case), or -1 (the first row) for a path or anything else.</summary>
    private static int WhisperModelCursor(string? saved) =>
        Array.FindIndex(ModelStore.WhisperModelNames, name => string.Equals(name, (saved ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The settings row's vosk-model picker: <see cref="ModelStore.VoskModelNames"/> as a second level of the pane, opened on the saved name. Never falls back to typing.</summary>
    private async Task<bool> PickVoskModelAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.SttVoskModel)), VoskModelRows(), PickKeys);
        int? picked = await PickAsync(page, VoskModelCursor(saved.SttVoskModel), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        Apply(SettingsField.SttVoskModel, d => d.SttVoskModel = ModelStore.VoskModelNames[index]);
        return true;
    }

    /// <summary>One <see cref="VoskModelLabel"/> row per name, in <see cref="ModelStore.VoskModelNames"/> order, sized from the download table.</summary>
    private List<string> VoskModelRows() =>
        ModelStore.VoskModelNames.Select(name => VoskModelLabel(name, ModelStore.ResolveVosk(name, _settings.StorageDirectory)?.ApproxBytes ?? 0)).ToList();

    /// <summary>The row of the saved name in <see cref="ModelStore.VoskModelNames"/> (any case), or -1 (the first row) for anything else.</summary>
    private static int VoskModelCursor(string? saved) =>
        Array.FindIndex(ModelStore.VoskModelNames, name => string.Equals(name, (saved ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    private bool SaveModel(string id)
    {
        Apply(SettingsField.LlmModel, d => d.LlmModel = id);
        return true;
    }

    private bool Unchanged()
    {
        Sink.Notice(UnchangedNotice);
        return false;
    }

    /// <summary>
    /// The <c>Working directory (cwd)</c> row (2026-09-22, the user's ask): the <c>/cwd browse</c>
    /// folder picker in place of the typed path it asked for until then — the same tree, the same
    /// one save. Nothing chosen leaves the setting as it was, the pane saying so where the picker
    /// stood; <c>/cwd &lt;path&gt;</c> still types one. Only reached with a picker and a pane: the
    /// prompt host falls back to <see cref="EditTextAsync"/>.
    /// </summary>
    private async Task<bool> PickWorkingDirectoryAsync(CancellationToken cancellationToken)
    {
        string? picked = await _browseFolder!(cancellationToken).ConfigureAwait(false);
        return picked is not null ? TrySaveWorkingDirectory(picked) : Unchanged();
    }

    /// <summary>A working directory the row and <c>/cwd</c> accept: a full (rooted) path, or nothing at all.</summary>
    public static bool IsWorkingDirectoryCandidate(string? text) =>
        text is not null && (text.Trim().Length == 0 || Path.IsPathRooted(text.Trim()));

    /// <summary>
    /// The ONE way the working directory is saved, shared by the settings row and <c>/cwd</c>:
    /// empty clears back to the profile's folder; otherwise a full path, created now so a bad one
    /// fails here rather than in a tool, and saved in its full spelling. False = nothing saved.
    /// </summary>
    public bool TrySaveWorkingDirectory(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        string current = FieldValue(SettingsField.WorkingDirectory, _settings.Current, _settings.ProfileDirectory);
        if (!IsWorkingDirectoryCandidate(trimmed))
        {
            Sink.Error($"{FieldName(SettingsField.WorkingDirectory)} {WorkingDirectoryError}; keeping {current}.");
            return false;
        }

        string value = "";
        if (trimmed.Length > 0)
        {
            try
            {
                value = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
                Directory.CreateDirectory(value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Sink.Error(WorkingDirectoryCreateError(trimmed, ex.Message, current));
                return false;
            }
        }

        Apply(SettingsField.WorkingDirectory, d => d.WorkingDirectory = value);
        return true;
    }

    /// <summary>
    /// The one way the Obsidian vault is saved (2026-09-22): empty clears it; otherwise a full path to a folder
    /// that already holds <c>.obsidian</c> — never created here, since a folder Obsidian has not opened is no
    /// vault — saved in its full spelling. False = nothing saved.
    /// </summary>
    public bool TrySaveObsidianVault(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim().Trim('"');
        string current = FieldValue(SettingsField.ObsidianVault, _settings.Current, _settings.ProfileDirectory);
        string value = "";
        if (trimmed.Length > 0)
        {
            try
            {
                value = Path.IsPathRooted(trimmed) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed)) : "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                value = "";
            }

            if (!Obsidian.ObsidianVault.IsVault(value))
            {
                Sink.Error($"{FieldName(SettingsField.ObsidianVault)} {ObsidianVaultError}; keeping {current}.");
                return false;
            }
        }

        Apply(SettingsField.ObsidianVault, d => d.ObsidianVault = value);
        return true;
    }

    public static string WorkingDirectoryCreateError(string path, string detail, string keeping) =>
        $"Could not create {path} ({detail}); keeping {keeping}.";

    /// <summary>One save, one notice, and the override reminder when the value will not be the one in force.</summary>
    private void Apply(SettingsField field, Action<AppSettingsData> mutate)
    {
        _settings.Update(mutate);
        Sink.Notice(SavedNotice(field, _settings.Current, _settings.ProfileDirectory, field == SettingsField.WebBrowserPath ? _locateBrowser("") : null));
        if (_overriddenBy(field) is { } overriddenBy)
        {
            Sink.Warning(OverrideNotice(overriddenBy));
        }
    }

    /// <summary>Whether a menu can be shown at all: the screen asks before offering a startup pick.</summary>
    public bool CanShowMenus() =>
        _console.Profile.Capabilities.Interactive && _console.Profile.Capabilities.Ansi;

    private static string Seconds(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A speed multiplier as shown everywhere: <c>1.0</c>, <c>1.25</c>. Invariant.</summary>
    public static string Speed(double value) => value.ToString("0.0#", CultureInfo.InvariantCulture);

    /// <summary>A millisecond value as the menu shows it: <c>0 ms</c>, <c>150 ms</c>. Invariant.</summary>
    public static string Milliseconds(int milliseconds) => milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";

    /// <summary>A voice mix as the menu shows it, primary share first: <c>70 % / 30 %</c>. Invariant.</summary>
    public static string Mix(int primaryPercent) =>
        primaryPercent.ToString(CultureInfo.InvariantCulture) + " % / " + (100 - primaryPercent).ToString(CultureInfo.InvariantCulture) + " %";

    /// <summary>A percent as the menu shows it: <c>65 %</c>. Invariant.</summary>
    public static string Percent(int value) => value.ToString(CultureInfo.InvariantCulture) + " %";

    /// <summary>A token count as the menu shows it: <c>32,768 tokens</c>. Invariant.</summary>
    public static string Tokens(int value) => value.ToString("N0", CultureInfo.InvariantCulture) + " tokens";

    /// <summary><c>2 turns</c>, <c>1 turn</c>, <c>0 turns</c>.</summary>
    public static string Turns(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " turn" : " turns");

    /// <summary><c>5 tool calls</c>, <c>1 tool call</c>: the reflection threshold as the row shows it.</summary>
    public static string ToolCalls(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " tool call" : " tool calls");

    /// <summary><c>4 requests</c>, <c>1 request</c>: the reflection's request cap as the row shows it (2026-09-17).</summary>
    public static string Requests(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " request" : " requests");

    /// <summary><c>100 round trips</c>, <c>1 round trip</c>: the tool-iteration cap as the row shows it.</summary>
    public static string RoundTrips(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " round trip" : " round trips");

    /// <summary><c>500 entries</c>, <c>1 entry</c>: the <c>/tree</c> cap as the row shows it.</summary>
    public static string Entries(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " entry" : " entries");

    /// <summary><c>25 lines</c>, <c>1 line</c>, <c>off</c> at 0: the paste preview count as the row shows it. Pinned.</summary>
    public static string Lines(int value) => value == 0 ? "off" : value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " line" : " lines");

    /// <summary><c>30 days</c>, <c>1 day</c>, <c>forever</c> at 0: the session retention as the row shows it. Pinned.</summary>
    public static string Days(int value) => value == 0 ? "forever" : value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " day" : " days");

    /// <summary><c>30 minutes</c>, <c>1 minute</c>, <c>off</c> at 0: the reflection cooldown as the row shows it. Pinned.</summary>
    public static string Minutes(int value) => value == 0 ? "off" : value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " minute" : " minutes");

    /// <summary><c>5 seconds</c>, <c>1 second</c>, <c>off</c> at 0: the botchat non-TTS delay as the row shows it (2026-09-26). Pinned.</summary>
    public static string SecondsLabel(int value) => value == 0 ? "off" : value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " second" : " seconds");

    /// <summary>A hit count as the menu shows it: <c>8 results</c>, <c>1 result</c>. Pinned.</summary>
    public static string Results(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " result" : " results");

    /// <summary>The <c>Shell allowed commands</c> row's value: <c>3 prefixes</c>, <c>1 prefix</c>, <c>none</c> at 0 (2026-09-21). Pinned.</summary>
    public static string Prefixes(int value) => value == 0 ? "none" : value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " prefix" : " prefixes");

    /// <summary>The <c>Shell output max chars</c> row's value: <c>30,000 chars</c> (2026-09-21). Pinned.</summary>
    public static string Chars(int value) => value.ToString("N0", CultureInfo.InvariantCulture) + " chars";

    /// <summary><c>20 commits</c> (the Git native log cap, 2026-09-20).</summary>
    public static string Commits(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " commit" : " commits");

    /// <summary>The <c>Ask max questions</c> row's value: <c>10 questions</c>, <c>1 question</c>. Pinned.</summary>
    public static string Questions(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " question" : " questions");

    /// <summary>The <c>File view image max (per call)</c> row's value: <c>10 pictures</c>, <c>1 picture</c>. Pinned.</summary>
    public static string Pictures(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " picture" : " pictures");

    /// <summary>The <c>Ask max choices per question</c> row's value: <c>10 choices</c>, <c>1 choice</c>. Pinned.</summary>
    public static string Choices(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " choice" : " choices");

    private static string OnOff(bool on) => on ? "on" : "off";
}
