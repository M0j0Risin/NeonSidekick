using System.Globalization;
using NeonSidekick.Emojis;
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
/// <see cref="SettingsTab"/>s in <see cref="SettingsMenu.TabFields"/>'s order (every tab spelled out; the STT
/// tab followed this order until 2026-10-10) — and, since 2026-09-19, the Options, Ask, Files and Web
/// rows a member's summary names are <c>/tools</c>' tabs (<see cref="SettingsMenu.ToolsTabFields"/>),
/// edited through the same menu. The tests navigate by row, so moving a member changes
/// every <c>Down(n)</c> and row-number assertion in <c>SettingsMenuTests</c> / <c>ChatScreenTests</c>
/// and the rows quoted in the docs. The values are never persisted.
/// </summary>
public enum SettingsField
{
    /// <summary>Which profile is loaded: a picker over <see cref="Settings.Profiles.List"/>. Not a field of the data; the first row.</summary>
    Profile,

    /// <summary>
    /// What the model may do with long-term memory: a picker over <see cref="App.MemoryMode.Names"/> (2026-10-04, the user's ask;
    /// the toggle <c>Memory</c> until then). No reconnect (every turn reads it). Under the profile it belongs to.
    /// </summary>
    MemoryMode,
    LlmUrl,
    LlmModel,

    /// <summary>Typed: the key an OpenAI-compatible server gets, saved DPAPI-encrypted and shown masked (<see cref="Settings.AppSettingsData.LlmApiKey"/>, 2026-09-28, like the Anthropic API key and the Home Assistant API key); the <c>empty</c> placeholder stays plain. Never put back on the edit line; empty is refused. The Http tab, under the scan mode (2026-10-10, the user's call: the hosted APIs
    /// have keys of their own and the Claude Code server none; the LLM tab's fourth row until then).</summary>
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

    /// <summary>A picker over <see cref="Llm.CompactType.Names"/>: what <c>/compact</c> does. On the Common tab (the LLM tab until 2026-10-10) under <see cref="LlmAutoCompactPercent"/> (2026-10-01, the user's call); no reconnect (read at each compact).</summary>
    LlmCompactType,
    /// <summary>How many recent user turns a compact keeps verbatim (0 to <see cref="Llm.ConversationHistory.DefaultMaxTurns"/>). On the Common tab under <see cref="LlmCompactType"/>; no reconnect.</summary>
    LlmCompactKeepRecent,

    /// <summary>The share of the window at which the next message compacts first; 0 = off. On the Common tab under <see cref="LlmMaxTurns"/>, ahead of the compact rows (2026-10-01, the user's call: when, then how); no reconnect.</summary>
    LlmAutoCompactPercent,
    /// <summary>How many user turns the model sees (<see cref="Settings.AppSettingsData.LlmMaxTurns"/>): 0 = auto. On the Common tab under <see cref="LlmMidTurnUsage"/> (2026-10-01, the user's call; under <see cref="LlmAutoCompactPercent"/> from 2026-09-27); no reconnect (resolved before each message).</summary>
    LlmMaxTurns,
    /// <summary>A picker over <see cref="Llm.ToolCompactType.Names"/>: what the tool loop does at the <see cref="CompactAt"/> share mid-turn. The Common tab's context run's last row, under <see cref="LlmCompactShowSummary"/> (2026-10-01, the user's call; under <see cref="CompactAt"/> before); no reconnect (read at each turn).</summary>
    LlmToolCompactType,
    /// <summary>Whether a turn offers the model its tools at all (<see cref="Settings.AppSettingsData.LlmOfferTools"/>). The first row of the Common tab's tools-and-limits run, above <see cref="MaxToolIterations"/> (2026-10-01, the user's call; still above <see cref="LlmToolCompactType"/>, the user's order of 2026-09-15); no reconnect (read at each turn), but a change clears the conversation (<see cref="SettingsChanges.Conversation"/>).</summary>
    LlmOfferTools,

    /// <summary>Model round trips a message may spend on tools (<see cref="Settings.AppSettingsData.LlmMaxToolIterations"/>). On the Common tab under <see cref="LlmOfferTools"/>, above the two timeouts (2026-10-01, the user's call); no reconnect (read at each turn).</summary>
    LlmMaxToolIterations,
    /// <summary>A picker over <see cref="UI.ThumbnailSize.Names"/>: how big the thumbnail under a sent picture is. On the Chat tab beside <see cref="ShowImageThumbnails"/>; no reconnect (read at each turn).</summary>
    ImageThumbnailSize,

    /// <summary>Entries a <c>/tree</c> lists before it stops (<see cref="Settings.AppSettingsData.FileTreeMaxLength"/>). On the Files tab under <see cref="FileTools"/> (on General until 2026-09-15); no reconnect (read at each <c>/tree</c>).</summary>
    FileTreeMaxLength,

    /// <summary>Whether a <c>/tree</c> line carries the file's size (<see cref="Settings.AppSettingsData.FileTreeShowSizes"/>). The Files tab's row under Tree max length (on General until 2026-09-15; the last until the @-mention folder mode, 2026-09-17); no reconnect.</summary>
    FileTreeShowSizes,

    /// <summary>Whether the thinking spinner reads a random <see cref="ThinkingVerbs"/> entry (<see cref="Settings.AppSettingsData.LlmUseFunVerbs"/>). Labelled <c>LLM use fun verbs</c> since 2026-09-15 (the member and the JSON key keep their name so a saved profile still loads); the Common tab's last row (the LLM tab's until 2026-10-10), alone as the cosmetic one (2026-10-01, the user's call); no reconnect (read at each spinner start).</summary>
    LlmUseFunVerbs,

    /// <summary>A picker over <see cref="Llm.LlmScanMode.Names"/>: where a blank URL's discovery looks, or <c>disabled</c> for no scan at all (2026-09-15). The Http tab's first row (2026-10-10, the user's call: only an OpenAI-compatible server is found by a scan; the LLM tab's first, above <see cref="LlmUrl"/>, until then); no reconnect (read at each scan).</summary>
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

    /// <summary>Whether a turn offers the file tools (seventeen with convert_to_pdf since the image tools, 2026-10-04; <see cref="Settings.AppSettingsData.FileTools"/>). The Files tab's first row (2026-09-15); a toggle, no reconnect (read at each turn).</summary>
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

    /// <summary>A picker over <see cref="ModelStore.VoskModelNames"/>: the Vosk model the wake word and the interrupt listen with (<see cref="Settings.AppSettingsData.SttVoskModel"/>). The STT tab's row under the wake phrase, last of the Wake word run (2026-10-10, the user's ask; the tab's last row from 2026-09-16); never typed, no path form; a voice reconnect (<see cref="SettingsMenu.IsVoiceField"/>).</summary>
    SttVoskModel,

    /// <summary>A picker over <see cref="Files.MentionFolderMode.Names"/>: what applying a folder from the line's @-mention list does (<see cref="Settings.AppSettingsData.FileMentionFolderMode"/>). The Files tab's last row (2026-09-17; General's row under Image thumbnail size before); no reconnect (read at each idle read).</summary>
    FileMentionFolderMode,

    /// <summary>A toggle: whether the model gets the skills, the two skill tools and the working directory's <c>NEON.md</c> / <c>AGENTS.md</c> (<see cref="Settings.AppSettingsData.AgentSkills"/>). The Options tab of <c>/skills</c>' first row (2026-09-16); no reconnect (read at each turn).</summary>
    AgentSkills,

    /// <summary>A toggle: whether <c>%USERPROFILE%\.agents\skills</c> is scanned too (<see cref="Settings.AppSettingsData.ExternalSkills"/>), read only while <see cref="AgentSkills"/> is on. The Options tab of <c>/skills</c>' second row (2026-09-16); no reconnect.</summary>
    ExternalSkills,

    /// <summary>A picker over <see cref="Skills.SkillCompactMode.Names"/>: whether a loaded skill survives a prune (<see cref="Settings.AppSettingsData.SkillCompactMode"/>). The Options tab of <c>/skills</c>' third row (2026-09-16); no reconnect.</summary>
    SkillCompactMode,

    /// <summary>A toggle: whether replies are shown as styled Markdown and asked for as such (<see cref="Settings.AppSettingsData.TranscriptMarkdown"/>). The Chat tab's first transcript row, above Paste preview lines (2026-10-01; under Image thumbnail size from 2026-09-16, once under @-mention folder mode; the last until Paste preview lines); no reconnect (read at each turn).</summary>
    TranscriptMarkdown,

    /// <summary>Lines of a collapsed paste the transcript shows under the sent line (<see cref="Settings.AppSettingsData.PastePreviewLines"/>); 0 = the label alone. The Chat tab's row under Transcript markdown (2026-10-01; its last row from 2026-09-16 until the two switches of 2026-09-18); no reconnect (read at each idle read).</summary>
    PastePreviewLines,

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

    /// <summary>A toggle: whether the <c>/</c> completion list shows <c>/exit</c>, the inverse of <see cref="Settings.AppSettingsData.HideExitAutocomplete"/> ("Hide /exit autocomplete" until 2026-10-13, the settings verbiage pass: a row reads on for what it shows). The Chat tab's input-line run's last row, under Command typo intercept (2026-10-01; after Paste preview lines from 2026-09-18); no reconnect (read at each keystroke).</summary>
    HideExitAutocomplete,

    /// <summary>A toggle: whether a sent line that is a command's bare name offers the command first (<see cref="Settings.AppSettingsData.CommandTypoIntercept"/>). The Chat tab's row under Keep message history (2026-10-01; before Welcome splash from 2026-09-18); no reconnect (read at each Enter).</summary>
    CommandTypoIntercept,

    /// <summary>A picker: how the splash greets you under the banner at startup — <c>fullsize</c> / <c>tiled</c> / <c>disabled</c> (<see cref="Settings.AppSettingsData.WelcomeSplashMode"/>; a toggle until 2026-09-24). The Chat tab's row under Theme (2026-10-01; before Show working directory from 2026-09-18, the user's order); no reconnect (read at each show).</summary>
    WelcomeSplash,

    /// <summary>A toggle: whether the banner is drawn at all — startup, <c>/clear</c>, <c>/splash</c>, <c>/theme</c>, a profile switch (<see cref="Settings.AppSettingsData.ShowHeader"/>, on by default). The Chat tab's row under Welcome splash (2026-10-01, the user's ask); no reconnect (read at each banner draw). <c>/header</c> and Ctrl+Alt+H flip it too (later that day).</summary>
    ShowHeader,

    /// <summary>A toggle: whether the working directory sits at the right edge of the banner's title line (<see cref="Settings.AppSettingsData.ShowWorkingDirectory"/>). The Chat tab's row under Show header (2026-10-01; before Draft editor from 2026-09-19, its last row until then); no reconnect (read at each banner draw).</summary>
    ShowWorkingDirectory,

    /// <summary>A picker: how messages meet the queue — <c>automatic</c> / <c>manual</c> / <c>disabled</c> (<see cref="Settings.AppSettingsData.QueueMode"/>; the toggle <c>Queue messages</c> until 2026-10-09, the user's call). The Chat tab's input-line run's first row, after Memory (2026-10-01; after Working directory from 2026-09-18, the user's order); no reconnect (read at each Enter).</summary>
    QueueMode,

    /// <summary>A picker: what a cancelled reply does to the queue — <c>hold</c> / <c>drain</c> / <c>empty</c> (<see cref="Settings.AppSettingsData.QueueCancelMode"/>). The Chat tab's row after Queue mode (2026-09-18); no reconnect (read when a turn ends).</summary>
    QueueCancelMode,

    /// <summary>A toggle: whether every completed turn is written to the profile's <c>sessions.db</c> (<see cref="Settings.AppSettingsData.SessionLogging"/>). The Sessions tab's first row (2026-09-18); no reconnect (read at each turn's end).</summary>
    SessionLogging,

    /// <summary>Typed: days a session is kept after its last turn, 0 = forever (<see cref="Settings.AppSettingsData.SessionRetentionDays"/>). The Sessions tab's third row, under Session save thinking (2026-10-10, the Saving run; the second from 2026-09-18, the user's order; third that morning); no reconnect (read at startup and after a profile switch).</summary>
    SessionRetentionDays,

    /// <summary>A picker over <see cref="Sessions.SessionNamingMode.Names"/>: where a new session's title comes from (<see cref="Settings.AppSettingsData.SessionNamingMode"/>). The Sessions tab's fourth row, first of the Titles run (2026-10-10; the third from 2026-09-18; second that morning); no reconnect (read at the first turn's end).</summary>
    SessionNamingMode,

    /// <summary>A picker over <see cref="Sessions.SessionShowName.Names"/>: which session names the rule above the input row shows (<see cref="Settings.AppSettingsData.SessionShowName"/>). The Sessions tab's fifth row, under the naming mode (2026-09-18; fourth until 2026-10-10); no reconnect (read at each draw).</summary>
    SessionShowName,

    /// <summary>A toggle: whether <c>session_manager</c> is offered to the model (<see cref="Settings.AppSettingsData.SessionTool"/>). The Sessions tab's sixth row, first of the Search tool run (2026-10-10; fifth from 2026-09-18, the user's order; last that morning, fourth until the show-name row); no reconnect (read at each turn).</summary>
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

    /// <summary>Typed: the command line <c>/draft</c> opens its file with, or empty for the shell's default (<see cref="Settings.AppSettingsData.DraftEditor"/>). The Chat tab's outside-apps run's first row, after Menus max height (2026-10-01; its last row from 2026-09-19); no reconnect (read at each <c>/draft</c>).</summary>
    DraftEditor,

    /// <summary>Typed: the most pictures one <c>view_image</c> call loads, 1 to 100 (<see cref="Settings.AppSettingsData.FileViewImageMaxPerCall"/>). The Files tab of <c>/tools</c>' last row (2026-09-19); no reconnect (read at each call).</summary>
    FileViewImageMaxPerCall,

    /// <summary>A toggle: the master switch over the MCP servers <c>mcp.json</c> names (<see cref="Settings.AppSettingsData.McpServers"/>). The Options tab of <c>/mcp</c>' first row (2026-09-20); a flip connects or disconnects them at once (<see cref="SettingsChanges.Mcp"/>), so it is refused mid-turn.</summary>
    McpServers,

    /// <summary>Typed: the seconds one MCP server gets to connect and list its tools, 5 to 300 (<see cref="Settings.AppSettingsData.McpConnectTimeoutSeconds"/>). The Options tab of <c>/mcp</c>' second row (2026-09-20); no reconnect (read at the next connect).</summary>
    McpConnectTimeoutSeconds,

    /// <summary>Whether a turn offers the eleven git tools (<see cref="Settings.AppSettingsData.GitLibTools"/>). The GitLib tab of <c>/tools</c>' first row (2026-09-20; off by default since 2026-09-21; <c>Git native tools</c> from 2026-09-21, <c>GitLib tools</c> since 2026-09-30); a toggle, no reconnect (read at each turn).</summary>
    GitLibTools,

    /// <summary>Typed: the most patch lines one <c>gitlib_diff</c> shows, 20 to 5000 (<see cref="Settings.AppSettingsData.GitLibDiffMaxLines"/>). The GitLib tab's second row; no reconnect (read at each call).</summary>
    GitLibDiffMaxLines,

    /// <summary>Typed: how many commits a <c>gitlib_log</c> without <c>max_commits</c> lists, 1 to 200 (<see cref="Settings.AppSettingsData.GitLibLogMaxCommits"/>). The GitLib tab's third row; no reconnect (read at each call).</summary>
    GitLibLogMaxCommits,

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

    /// <summary>A toggle: whether a compact's summary, or its pruned results, follow the compact notice in the transcript (<see cref="Settings.AppSettingsData.LlmCompactShowSummary"/>). The Common tab, right under <see cref="LlmCompactKeepRecent"/> (2026-09-21; above <see cref="LlmToolCompactType"/> since 2026-10-01); no reconnect (read at each compact).</summary>
    LlmCompactShowSummary,

    /// <summary>Typed: the <c>user.email</c> <c>/gituser</c> writes into the working directory's repository (<see cref="Settings.AppSettingsData.GitLibEmail"/>); empty = not set. The GitLib tab's fourth row (2026-09-21); no reconnect (read at each <c>/gituser</c>).</summary>
    GitLibEmail,

    /// <summary>Typed: the <c>user.name</c> <c>/gituser</c> writes beside the email (<see cref="Settings.AppSettingsData.GitLibName"/>); empty = not set. The GitLib tab's last row (2026-09-21); no reconnect.</summary>
    GitLibName,

    /// <summary>A toggle: whether an <c>execute_code</c> script may call the app's other tools through its <c>neon_tools</c> module (<see cref="Settings.AppSettingsData.ShellToolBridge"/>). The Shell tab, right above the tool-call cap it governs (later on 2026-09-21); no reconnect (read at each call and each turn).</summary>
    ShellToolBridge,

    /// <summary>A picker over <see cref="Files.FileBrowserMode.Names"/>: what the <c>/cwd browse</c> tree and <c>/tree</c> (2026-09-23) list (<see cref="Settings.AppSettingsData.FileBrowserMode"/>). The Files tab's row under the @-mention folder mode (2026-09-21); no reconnect (read when the pane opens).</summary>
    FileBrowserMode,

    /// <summary>What the toolbar under the hint row shows (<see cref="Settings.AppSettingsData.ToolbarItems"/>): a toggle until 2026-09-29, a checklist since (the user's ask). The Chat tab's row after Show working directory (2026-09-21); no reconnect (read at each pane draw).</summary>
    ToolbarItems,

    /// <summary>A toggle: whether a command line, a script or text to a background process may name a path outside the working directory (<see cref="Settings.AppSettingsData.ShellPolice"/>). The Shell tab's third row (2026-09-22), under the list it guards beside; no reconnect (read at each call and each turn). Last in the enum, as every newcomer: the flat no-pane list's row numbers are pinned.</summary>
    ShellPolice,

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

    /// <summary>A picker: what the SQL tools may do — <c>read-only</c> / <c>read-write</c> (<see cref="Settings.AppSettingsData.SqlMode"/>). The SQL tab's second row (2026-10-05).</summary>
    SqlMode,

    /// <summary>A checklist: the kinds of statement <c>sql_execute</c> may run under read-write (<see cref="Settings.AppSettingsData.SqlStatementsAllowed"/>). The SQL tab's third row (2026-10-05).</summary>
    SqlStatementsAllowed,


    /// <summary>Typed: how many rows a <c>sql_query</c> without <c>max_rows</c> returns, 1 to 100,000 (1000 until 2026-10-01) (<see cref="Settings.AppSettingsData.SqlQueryMaxRows"/>). The SQL tab's third row (2026-09-23); no reconnect.</summary>
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

    /// <summary>A pick among <see cref="UI.ThemeName.Names"/>: the look (<see cref="Settings.AppSettingsData.Theme"/>, 2026-09-23). The Chat tab's screen run's first row, after Copy user prompt (2026-10-01; its last row before); a change puts the theme in force at once (the pane re-colours) and raises <see cref="SettingsChanges.Theme"/>, so the screen starts over as <c>/splash</c> does when the pane closes. No reconnect. Last in the enum, as every newcomer.</summary>
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

    /// <summary>Typed (the <c>Image viewer</c> row, <c>Image editor</c> until later still on 2026-09-24): where a double-clicked picture opens: empty for the built-in viewer (later on 2026-09-27; the app Windows registers before), <c>system</c> for that app, else a command line (<see cref="Settings.AppSettingsData.ImageEditor"/>). The Chat tab, under <see cref="DraftEditor"/> (later on 2026-09-24); no reconnect (read at each double-click). Last in the enum, as every newcomer.</summary>
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

    /// <summary>A picker: who draws a <c>/botchat</c> picture — <c>automatic</c> / <c>autonomous</c> (<see cref="Settings.AppSettingsData.BotChatImageMode"/>). The Botchat tab, under the switch (2026-09-25); no reconnect.</summary>
    BotChatImageMode,

    /// <summary>A toggle: whether the next bot answers while the app's <c>/botchat</c> picture renders (<see cref="Settings.AppSettingsData.BotChatImageAsync"/>). The Botchat tab's last row (2026-09-25); no reconnect.</summary>
    BotChatImageAsync,

    /// <summary>A picker: whose LLM the <c>/botchat</c> bots talk through — <c>single</c> / <c>multi</c> (<see cref="Settings.AppSettingsData.BotChatLlmMode"/>). The Botchat tab's first row (later on 2026-09-25); no reconnect (read at a chat's start).</summary>
    BotChatLlmMode,

    /// <summary>A picker: what the busy row's token tally shows while a turn runs — <c>estimate</c> / <c>last-known</c> (<see cref="Settings.AppSettingsData.LlmMidTurnUsage"/>). On the Common tab under <see cref="LlmContextLength"/> (2026-09-25); no reconnect (read on every draw).</summary>
    LlmMidTurnUsage,

    /// <summary>A toggle: whether the input line's Up/Down history is stored in <c>sessions.db</c> and recalled after a restart (<see cref="Settings.AppSettingsData.KeepMessageHistory"/>). The Chat tab's row under Queue cancel mode (2026-10-01; under Command typo intercept from 2026-09-25, the user's ask); no reconnect (read at each remembered line and each load).</summary>
    KeepMessageHistory,

    /// <summary>Typed: the seconds <c>/botchat</c> rests after a reply when no voice plays, 0 (off) to 30 (<see cref="Settings.AppSettingsData.BotChatNonTtsDelaySeconds"/>). The Botchat tab's last row (2026-09-26, the user's ask); no reconnect (read per reply). Last in the enum, as every newcomer.</summary>
    BotChatNonTtsDelaySeconds,

    /// <summary>A toggle: whether <c>run_command</c> steps aside for a native tool (<see cref="Settings.AppSettingsData.ShellPreferNative"/>). The Shell tab's row under Shell police (2026-09-26, the user's ask); no reconnect (read at each call and each turn). Last in the enum, as every newcomer.</summary>
    ShellPreferNative,

    /// <summary>A toggle: whether the model's thinking streams into the transcript and folds when the answer starts (<see cref="Settings.AppSettingsData.LlmShowThinking"/>). The Common tab's row under <see cref="LlmReasoning"/> (2026-10-01, the user's call: the thinking rows together; the last row from 2026-09-26, the user's ask); no reconnect (read at each turn). Last in the enum, as every newcomer.</summary>
    LlmShowThinking,

    /// <summary>Typed: the Claude Code CLI <c>/claude</c> starts (<see cref="Settings.AppSettingsData.ClaudeCodeExecutable"/>); empty = looked up. <c>/settings</c>' Anthropic tab's first row, alone under its Claude Code heading, as both the server and <c>/claude</c> read it (later on 2026-10-10, the user's ask; the second, under the Claude Code server's, from earlier that day, when <c>/tools</c>' ClaudeCode tab went; its first row from 2026-09-27, on <c>/settings</c> that morning); read at each <c>/claude</c> and by the Claude Code server.</summary>
    ClaudeCodeExecutable,

    /// <summary>A picker over <see cref="Claude.ClaudePermission.Names"/>: what the Claude Code child may do on its own (<see cref="Settings.AppSettingsData.ClaudeCodePermissions"/>). The first of the Anthropic tab's <c>/claude</c> command run, under the Claude Code server's switch (later on 2026-10-10; under the executable earlier that day;<c>/tools</c>' ClaudeCode tab's second row from 2026-09-27).</summary>
    ClaudeCodePermissions,

    /// <summary>Typed: the <c>--model</c> of a <c>/claude</c> run (<see cref="Settings.AppSettingsData.ClaudeCodeModel"/>); empty = the CLI's own. Under the permissions on the Anthropic tab (2026-10-10; <c>/tools</c>' ClaudeCode tab's third row from 2026-09-27).</summary>
    ClaudeCodeModel,

    /// <summary>A picker over <see cref="Claude.ClaudeEffort.Names"/>: the <c>--effort</c> of a <c>/claude</c> run (<see cref="Settings.AppSettingsData.ClaudeCodeEffort"/>). Under the model on the Anthropic tab, the last of <c>/claude</c>'s four (2026-10-10; <c>/tools</c>' ClaudeCode tab's fourth row from 2026-09-27).</summary>
    ClaudeCodeEffort,

    /// <summary>A toggle: whether the Anthropic API is offered as a server (<see cref="Settings.AppSettingsData.AnthropicApi"/>). <c>/settings</c>' Anthropic tab's first API row, under <c>/claude</c>'s (2026-10-10; its first row from 2026-10-03; on <c>/tools</c>' ClaudeCode tab under the advisor's from 2026-09-29, the first row of <c>/settings</c>' Claude (API) tab from 2026-09-27); a reconnect.</summary>
    AnthropicApi,

    /// <summary>Typed: the Anthropic API key, saved DPAPI-encrypted and shown masked (<see cref="Settings.AppSettingsData.AnthropicApiKey"/>); empty clears it. Under the switch (2026-09-27); a reconnect.</summary>
    AnthropicApiKey,

    /// <summary>Typed: the <c>max_tokens</c> of every Anthropic API request (<see cref="Settings.AppSettingsData.AnthropicApiMaxTokens"/>). Under the key (2026-09-27); a reconnect.</summary>
    AnthropicApiMaxTokens,

    /// <summary>A toggle: whether Anthropic API requests carry prompt-cache breakpoints (<see cref="Settings.AppSettingsData.AnthropicApiPromptCaching"/>). Under the output cap on <c>/settings</c>' Anthropic tab (2026-10-03; <c>/tools</c>' ClaudeCode tab from 2026-09-29, the Claude (API) tab's from 2026-09-27); a reconnect.</summary>
    AnthropicApiPromptCaching,

    /// <summary>A toggle: whether the <c>/botchat</c> bots get the main chat's skills and <c>load_skill</c> (<see cref="Settings.AppSettingsData.BotChatSkills"/>). The Botchat tab (2026-09-27, the user's ask); no reconnect (read per reply).</summary>
    BotChatSkills,

    /// <summary>A toggle: whether the <c>/botchat</c> bots are shown the chat's pictures (<see cref="Settings.AppSettingsData.BotChatVision"/>). The Botchat tab's last row (2026-09-27, the user's ask); no reconnect (read per reply). Last in the enum, as every newcomer.</summary>
    BotChatVision,

    /// <summary>A toggle (the <c>Themed external windows</c> row): whether the app's own windows — the built-in picture viewer, the camera's live window and the log window — wear the theme or stay black (<see cref="Settings.AppSettingsData.ThemedExternalWindows"/>). The Chat tab, under <see cref="ThemedBackground"/> since 2026-10-03 (the user's ask, name and place; <c>ThemedViewer</c>, <c>Themed image viewer</c>, the tab's last row under <see cref="ImageEditor"/> from later on 2026-09-27); no reconnect (read when a window opens or is focused). Last in the enum, as every newcomer.</summary>
    ThemedExternalWindows,

    /// <summary>A toggle: whether a reflection may write a skill's supporting files with <c>skill_editor</c>'s <c>write_file</c> / <c>edit_file</c> (<see cref="Settings.AppSettingsData.ReflectionEditsSupportingFiles"/>). The Reflection tab of <c>/skills</c>' last row (2026-09-27, the user's ask and name); no reconnect (read when a reflection is decided). Last in the enum, as every newcomer.</summary>
    ReflectionEditsSupportingFiles,

    /// <summary>A picker: which pictures a <c>/botchat</c> rework may start from — <c>latest</c> / <c>chat-history</c> (<see cref="Settings.AppSettingsData.BotChatImg2ImgMode"/>). The Botchat tab, under the img2img row (2026-09-27, the user's ask); no reconnect (read per reply). Last in the enum, as every newcomer.</summary>
    BotChatImg2ImgMode,

    /// <summary>A checklist: the skills the <c>/botchat</c> bots may load while <see cref="BotChatSkills"/> is off (<see cref="Settings.AppSettingsData.BotChatLimitedSkills"/>). The Botchat tab, under the skills switch (2026-10-04, the user's ask; <c>BotChatPreloadedSkills</c> from 2026-09-27, whose <c>BotChatSkillMode</c> picker went the same day); no reconnect (read per reply).</summary>
    BotChatLimitedSkills,

    /// <summary>A toggle: whether every turn's thinking goes back to a local server and the chat template is asked to keep it (<see cref="Settings.AppSettingsData.LlmPreserveThinking"/>). The Http tab's thinking run, first (2026-10-10, the user's call: only an OpenAI-compatible server reads it; the LLM tab, under Show thinking, from 2026-09-28, the user's question); no reconnect (read at each turn). Last in the enum, as every newcomer.</summary>
    LlmPreserveThinking,

    /// <summary>A toggle: whether a reply's thinking is saved with the session (<see cref="Settings.AppSettingsData.SessionSaveThinking"/>). The Sessions tab's second row, under Session logging in the Saving run (2026-10-10, the user's ask; the last row from 2026-09-28, the user's ask); no reconnect (read at each save). Last in the enum, as every newcomer.</summary>
    SessionSaveThinking,

    /// <summary>A door: the models with sampling overrides (<see cref="Settings.AppSettingsData.LlmSampling"/>); Enter opens the <c>/sampling</c> pane (<see cref="SettingsMenu.SamplingPane"/>). The Http tab's sampling run, first (2026-10-10, the user's call: no sampling goes to the Anthropic API, the OpenAI API or the Claude Code server; the LLM tab's row under <see cref="LlmReasoningEstimate"/> from 2026-10-01, the last row from 2026-09-28, the user's ask); no reconnect (read at each turn). Last in the enum, as every newcomer.</summary>
    LlmSampling,

    /// <summary>A toggle: whether the <c>/sampling</c> pane reads a model's defaults from its Hugging Face card when the server says nothing (<see cref="Settings.AppSettingsData.LlmSamplingFromHuggingFace"/>). The Http tab, under LLM sampling (2026-09-28, the user's call; the LLM tab until 2026-10-10); no reconnect (read when the pane opens). Last in the enum, as every newcomer.</summary>
    LlmSamplingFromHuggingFace,

    /// <summary>A toggle: whether a turn offers the Home Assistant tools (<see cref="Settings.AppSettingsData.HomeAssistantTools"/>). The Home Assistant tab of <c>/tools</c>' first row (2026-09-28); no reconnect (read at each turn). Last in the enum, as every newcomer.</summary>
    HomeAssistantTools,

    /// <summary>Typed: the Home Assistant server's URL, or empty (<see cref="Settings.AppSettingsData.HomeAssistantUrl"/>). The Home Assistant tab's second row (2026-09-28); no reconnect (read at each call).</summary>
    HomeAssistantUrl,

    /// <summary>Typed, masked: the long-lived access token, saved DPAPI-encrypted (<see cref="Settings.AppSettingsData.HomeAssistantToken"/>); empty clears it. The Home Assistant tab's third row (2026-09-28); no reconnect.</summary>
    HomeAssistantToken,

    /// <summary>An action row, no setting behind it: Enter asks the server for its version with the URL and token as saved, the answer as the status line (2026-09-28). The Home Assistant tab's fourth row.</summary>
    HomeAssistantTest,

    /// <summary>A picker over <see cref="HomeAssistant.HaPolicy.Names"/>: what the model may switch (<see cref="Settings.AppSettingsData.HomeAssistantActionPolicy"/>). The Home Assistant tab (2026-09-28); no reconnect.</summary>
    HomeAssistantActionPolicy,

    /// <summary>Typed: the conversation agent <c>ha_assist</c> and <c>/ha say</c> talk to, empty for Home Assistant's default (<see cref="Settings.AppSettingsData.HomeAssistantAssistAgent"/>). The Home Assistant tab (2026-09-28); no reconnect.</summary>
    HomeAssistantAssistAgent,

    /// <summary>Typed: seconds one Home Assistant request may take, 2 to 60 (<see cref="Settings.AppSettingsData.HomeAssistantTimeoutSeconds"/>). The Home Assistant tab's last row (2026-09-28); no reconnect.</summary>
    HomeAssistantTimeoutSeconds,

    /// <summary>A toggle: whether a turn offers <c>print_file</c> and <c>list_printers</c> (<see cref="Settings.AppSettingsData.PrintTools"/>). The Print tab of <c>/tools</c>' first row (2026-09-28, the user's ask); no reconnect (read at each turn). Last in the enum, as every newcomer.</summary>
    PrintTools,

    /// <summary>A picker over <see cref="Printing.PrintPolicy.Names"/>: what the model may print (<see cref="Settings.AppSettingsData.PrintActionPolicy"/>). The Print tab (2026-09-28); no reconnect.</summary>
    PrintActionPolicy,

    /// <summary>A picker over the installed printers and the Windows default (<see cref="Settings.AppSettingsData.PrintDefaultPrinter"/>). The Print tab (2026-09-28); no reconnect (read at each print).</summary>
    PrintDefaultPrinter,

    /// <summary>Typed: the body size in points a listing or markdown prints at, 6 to 24 (<see cref="Settings.AppSettingsData.PrintFontSize"/>). The Print tab's last row (2026-09-28); no reconnect.</summary>
    PrintFontSize,

    /// <summary>
    /// A door: the embedded model's catalog (2026-09-29) — each model installed, part-way or not, Enter for use / install /
    /// remove (<see cref="SettingsMenu.PickEmbeddedModelAsync"/>). No stored value; the Embedded model tab's first row. A use
    /// or an install closes the pane and hands the model to the screen (<see cref="SettingsMenu.TakePendingEmbeddedModel"/>).
    /// </summary>
    EmbeddedModels,

    /// <summary>A picker: which llama.cpp build runs the embedded model (<see cref="Settings.AppSettingsData.EmbeddedBackend"/>, 2026-09-29); a reconnect.</summary>
    EmbeddedBackend,

    /// <summary>Typed: the embedded server's context window, 0 (fit, the default since later on 2026-09-29) or 512 to 262144 tokens (<see cref="Settings.AppSettingsData.EmbeddedContextSize"/>, 2026-09-29); a reconnect.</summary>
    EmbeddedContextSize,

    /// <summary>Typed: the model layers the embedded server puts on the GPU — auto, all or a count (<see cref="Settings.AppSettingsData.EmbeddedGpuLayers"/>, 2026-09-29); a reconnect.</summary>
    EmbeddedGpuLayers,

    /// <summary>A toggle: whether the embedded server loads the vision projector (<see cref="Settings.AppSettingsData.EmbeddedVision"/>, 2026-09-29); a reconnect.</summary>
    EmbeddedVision,

    /// <summary>
    /// A picker: how a reasoning count the server did not report is estimated — <c>off</c>, <c>chars</c>, <c>tokenize</c>
    /// (<see cref="Settings.AppSettingsData.LlmReasoningEstimate"/>, 2026-09-29). The Http tab, under <c>LLM preserve
    /// thinking</c> (the LLM tab until 2026-10-10); no reconnect (read at each request).
    /// </summary>
    LlmReasoningEstimate,

    /// <summary>
    /// A toggle: whether the embedded model is offered at all (<see cref="Settings.AppSettingsData.EmbeddedLlmServer"/>,
    /// 2026-09-29, the user's ask); a reconnect, which stops a running embedded server when it goes off. The Embedded tab's first row.
    /// </summary>
    EmbeddedLlmServer,

    /// <summary>
    /// A toggle: whether the embedded server drafts ahead with its drafter (<see cref="Settings.AppSettingsData.EmbeddedDrafter"/>,
    /// 2026-09-29; <c>Embedded MTP</c> until later that day, the user's name); a reconnect. The Embedded tab's last row.
    /// </summary>
    EmbeddedDrafter,

    /// <summary>
    /// A checklist: the performance bar's meters (<see cref="Settings.AppSettingsData.PerformanceBarItems"/>, 2026-09-30, the
    /// user's ask; a picker of <c>off</c> or a look from 2026-09-29); the look
    /// (<see cref="Settings.AppSettingsData.PerformanceBarLook"/>) shown in its value, set by <c>/perfbar</c> (on the page's title row until 2026-10-12). The Chat tab's row after Show toolbar; no reconnect (the
    /// pane reads it at every tick).
    /// </summary>
    ShowPerformanceBar,

    /// <summary>
    /// The home bar's items (<see cref="Settings.AppSettingsData.HomeBarItems"/>, 2026-10-12, the user's ask): a picker of Home
    /// Assistant's rooms and devices, with the bar's order changed on its first tab (<see cref="EditHomeBarAsync"/>). The Chat
    /// tab's row after Show performance bar; no reconnect (the pane reads it at every tick).
    /// </summary>
    ShowHomeBar,

    /// <summary>
    /// The order of the rows under the pane's lower rule (<see cref="Settings.AppSettingsData.BarOrder"/>, 2026-10-12, the user's
    /// ask): the hint bar, the toolbar, the performance bar and the home bar moved with the move keys (<see cref="EditBarOrderAsync"/>).
    /// <c>Toolbar order</c>, the user's name. The Chat tab's row after Show home bar; no reconnect (the pane reads it at every tick).
    /// </summary>
    BarOrder,

    /// <summary>
    /// The Status bar's sections and their order (<see cref="Settings.AppSettingsData.StatusBarItems"/>, <see cref="Settings.AppSettingsData.StatusBarOrder"/>;
    /// 2026-10-12, the user's ask; <c>Hint bar</c> until 2026-10-14): a checklist with the move keys (<see cref="EditStatusBarAsync"/>).
    /// The second row under Tool bars, after Hints bar; no reconnect (read at each pane draw).
    /// </summary>
    StatusBarItems,

    /// <summary>
    /// Picked: <c>auto</c>, <c>always</c> or <c>off</c> (<see cref="Settings.AppSettingsData.HintsBar"/>, 2026-10-14, the user's ask:
    /// the keys' hints on a bar of their own; <see cref="UI.HintsBarMode"/>). The first row under Tool bars, as the bar is first by
    /// default; no reconnect (the pane reads it at every tick).
    /// </summary>
    HintsBar,

    /// <summary>
    /// Typed: off or 50 to 99 % of the biggest GPU's dedicated memory for the embedded server
    /// (<see cref="Settings.AppSettingsData.EmbeddedVramBudget"/>, later on 2026-09-29, the user's ask); a reconnect. The
    /// Embedded tab's row after Embedded GPU layers.
    /// </summary>
    EmbeddedVramBudget,

    /// <summary>
    /// A picker: <c>single</c> / <c>parallel</c>, how the embedded models' files come down from Hugging Face
    /// (<see cref="Settings.AppSettingsData.EmbeddedHfDownloadType"/>, 2026-09-30, the user's ask). The Embedded tab's row
    /// after Embedded models (after Embedded filter type until 2026-10-02, when that setting went, the user's call); no
    /// reconnect (read as each download starts).
    /// </summary>
    EmbeddedHfDownloadType,

    /// <summary>
    /// A picker: <c>parent-server</c> / <c>multi-server</c>, what a multi-mode botchat's bot naming another embedded model gets
    /// (<see cref="Settings.AppSettingsData.BotChatMultiEmbedded"/>, later on 2026-09-29, the user's ask). The Botchat tab, under
    /// Botchat LLM mode; no reconnect (read at a chat's start).
    /// </summary>
    BotChatMultiEmbedded,

    /// <summary>
    /// A toggle: whether a multi-server botchat's extra embedded servers stop when it ends
    /// (<see cref="Settings.AppSettingsData.BotChatMultiEmbeddedKill"/>, later on 2026-09-29, the user's ask). The Botchat tab,
    /// under Botchat multi-embedded; no reconnect (read at a chat's end).
    /// </summary>
    BotChatMultiEmbeddedKill,

    /// <summary>
    /// A toggle: whether the Claude Code CLI is offered as a server (<see cref="Settings.AppSettingsData.ClaudeCodeServer"/>,
    /// 2026-09-30, the user's ask). <c>/settings</c>' Anthropic tab's first row (2026-10-10; its last, under the Anthropic API's, from 2026-10-03;
    /// <c>/tools</c>' Claude tab's until then); a reconnect.
    /// </summary>
    ClaudeCodeServer,

    /// <summary>A toggle: whether a turn offers the eight Oracle tools (<see cref="Settings.AppSettingsData.OracleTools"/>). The Oracle tab of <c>/tools</c>' first row (2026-09-30); no reconnect (read at each turn).</summary>
    OracleTools,

    /// <summary>A picker: what the Oracle tools may do — <c>read-only</c> / <c>read-write</c> (<see cref="Settings.AppSettingsData.OracleMode"/>). The Oracle tab's second row (2026-10-05).</summary>
    OracleMode,

    /// <summary>A checklist: the kinds of statement <c>oracle_execute</c> may run under read-write (<see cref="Settings.AppSettingsData.OracleStatementsAllowed"/>). The Oracle tab's third row (2026-10-05).</summary>
    OracleStatementsAllowed,

    /// <summary>A checklist: which connections of <c>oracle.json</c> this profile offers (<see cref="Settings.AppSettingsData.OracleConnectionsOffered"/>). The Oracle tab's second row (2026-09-30); no reconnect.</summary>
    OracleConnectionsOffered,


    /// <summary>An action row, no setting behind it (2026-09-30): Enter picks a connection and asks for its password in a masked slot, saved to its store (<see cref="Oracle.OracleSecrets.Save"/>). The Oracle tab's fourth row.</summary>
    OracleSetPassword,

    /// <summary>An action row, no setting behind it (2026-09-30, the user's ask: a wizard): Enter walks a new connection through every choice, tests it and adds it to that <c>oracle.json</c> (<c>SettingsMenu.OracleWizard.cs</c>). The Oracle tab's fifth row.</summary>
    OracleAddConnection,

    /// <summary>A toggle: whether <c>%</c> and part of a name lists the Oracle connections too (<see cref="Settings.AppSettingsData.OraclePercentMention"/>). The Oracle tab's sixth row (2026-09-30); no reconnect.</summary>
    OraclePercentMention,

    /// <summary>Typed: how many rows an <c>oracle_query</c> without <c>max_rows</c> returns, 1 to 100,000 (<see cref="Settings.AppSettingsData.OracleQueryMaxRows"/>). The Oracle tab (2026-09-30); no reconnect.</summary>
    OracleQueryMaxRows,

    /// <summary>Typed: seconds an Oracle tool's statement may run, 1 to 600 (<see cref="Settings.AppSettingsData.OracleQueryTimeoutSeconds"/>). The Oracle tab (2026-09-30); no reconnect.</summary>
    OracleQueryTimeoutSeconds,

    /// <summary>An edit row, no setting behind it: Enter opens the profile's <c>oracle.json</c> in the editor (made with <see cref="Oracle.OracleConfigFile.EmptyText"/> when missing). The Oracle tab (2026-09-30).</summary>
    OracleConnectionsProfile,

    /// <summary>An edit row, no setting behind it: Enter opens the home's <c>oracle.json</c>, every profile's. The Oracle tab's last row (2026-09-30).</summary>
    OracleConnectionsGlobal,

    /// <summary>A toggle: whether a turn offers the eight MySQL tools (<see cref="Settings.AppSettingsData.MySqlTools"/>). The MySQL tab's first row (2026-09-30); no reconnect.</summary>
    MySqlTools,

    /// <summary>A picker: what the MySQL tools may do — <c>read-only</c> / <c>read-write</c> (<see cref="Settings.AppSettingsData.MySqlMode"/>). The MySQL tab's second row (2026-10-05).</summary>
    MySqlMode,

    /// <summary>A checklist: the kinds of statement <c>mysql_execute</c> may run under read-write (<see cref="Settings.AppSettingsData.MySqlStatementsAllowed"/>). The MySQL tab's third row (2026-10-05).</summary>
    MySqlStatementsAllowed,

    /// <summary>A checklist: which connections of <c>mysql.json</c> this profile offers (<see cref="Settings.AppSettingsData.MySqlConnectionsOffered"/>). The MySQL tab (2026-09-30).</summary>
    MySqlConnectionsOffered,


    /// <summary>An action row (2026-09-30): Enter picks a connection and asks for its password in a masked slot, saved to its store (<see cref="MySql.MySqlSecrets.Save"/>). The MySQL tab.</summary>
    MySqlSetPassword,

    /// <summary>An action row (2026-09-30): Enter walks a new connection through every choice, tests it and adds it to that <c>mysql.json</c> (<c>SettingsMenu.MySqlWizard.cs</c>). The MySQL tab.</summary>
    MySqlAddConnection,

    /// <summary>A toggle: whether <c>%</c> and part of a name lists the MySQL connections too (<see cref="Settings.AppSettingsData.MySqlPercentMention"/>). The MySQL tab (2026-09-30).</summary>
    MySqlPercentMention,

    /// <summary>Typed: how many rows a <c>mysql_query</c> without <c>max_rows</c> returns, 1 to 100,000 (<see cref="Settings.AppSettingsData.MySqlQueryMaxRows"/>). The MySQL tab (2026-09-30).</summary>
    MySqlQueryMaxRows,

    /// <summary>Typed: seconds a MySQL tool's statement may run, 1 to 600 (<see cref="Settings.AppSettingsData.MySqlQueryTimeoutSeconds"/>). The MySQL tab (2026-09-30).</summary>
    MySqlQueryTimeoutSeconds,

    /// <summary>An edit row: Enter opens the profile's <c>mysql.json</c> in the editor (made with <see cref="MySql.MySqlConfigFile.EmptyText"/> when missing). The MySQL tab (2026-09-30).</summary>
    MySqlConnectionsProfile,

    /// <summary>An edit row: Enter opens the home's <c>mysql.json</c>, every profile's. The MySQL tab's last row (2026-09-30).</summary>
    MySqlConnectionsGlobal,

    /// <summary>A toggle: whether a turn offers the UNC tools over the shares of <c>unc.json</c> (<see cref="Settings.AppSettingsData.UncTools"/>). The UNC tab's first row (2026-09-30).</summary>
    UncTools,

    /// <summary>A toggle: the master key of every change on a share (<see cref="Settings.AppSettingsData.UncWrites"/>); off, every share is read-only. The UNC tab (2026-09-30).</summary>
    UncWrites,

    /// <summary>A checklist: which shares of <c>unc.json</c> this profile offers (<see cref="Settings.AppSettingsData.UncSharesOffered"/>). The UNC tab (2026-09-30).</summary>
    UncSharesOffered,


    /// <summary>An action row (2026-09-30): Enter picks a runas share and asks for its password in a masked slot, saved to its store (<see cref="Unc.UncSecrets.Save"/>). The UNC tab.</summary>
    UncSetPassword,

    /// <summary>An action row (2026-09-30): Enter walks a new share through every choice, tests it and adds it to that <c>unc.json</c> (<c>SettingsMenu.UncWizard.cs</c>). The UNC tab.</summary>
    UncAddShare,

    /// <summary>A toggle: whether <c>*</c> and part of a name lists the UNC shares (<see cref="Settings.AppSettingsData.UncStarMention"/>). The UNC tab (2026-09-30; its own <c>*</c> since 2026-10-01, was <c>%</c>).</summary>
    UncStarMention,

    /// <summary>An edit row: Enter opens the profile's <c>unc.json</c> in the editor (made with <see cref="Unc.UncConfigFile.EmptyText"/> when missing). The UNC tab (2026-09-30).</summary>
    UncSharesProfile,

    /// <summary>An edit row: Enter opens the home's <c>unc.json</c>, every profile's. The UNC tab's last row (2026-09-30).</summary>
    UncSharesGlobal,

    /// <summary>
    /// A toggle: whether the embedded server must stay in VRAM, every layer on the GPU and a load that spilled into system
    /// memory refused (<see cref="Settings.AppSettingsData.EmbeddedVramOnly"/>, 2026-10-01, the user's ask); a reconnect. The
    /// Embedded tab's row after Embedded VRAM budget.
    /// </summary>
    EmbeddedVramOnly,

    /// <summary>
    /// A picker: the most of the window a menu, info or folder pane takes — <c>half-screen</c> / <c>three-quarters</c> /
    /// <c>full-screen</c> (<see cref="Settings.AppSettingsData.MenuMaxHeight"/>, 2026-10-01, the user's ask, with every tab
    /// held at its tallest tab's height). The Chat tab's screen run's last row, after Show performance bar (2026-10-01; after Theme before); no reconnect (read at each pane draw). Last in
    /// the enum until <see cref="ProjectFile"/>.
    /// </summary>
    MenuMaxHeight,

    /// <summary>A toggle: whether the working directory's <c>NEON.md</c> / <c>AGENTS.md</c> is read into the prompt as project notes (<see cref="Settings.AppSettingsData.ProjectFile"/>), read only while <see cref="AgentSkills"/> is on. The Options tab of <c>/skills</c>' third row since 2026-10-01 (the user's ask: the one row of the Project tab from later on 2026-09-19 until then, which went); no reconnect (read at each turn). Last in the enum until <see cref="FileSearchMaxResults"/>.</summary>
    ProjectFile,

    /// <summary>Typed: the most rows one <c>search_files</c> or <c>unc_search</c> call returns, 1 to 5000 (<see cref="Settings.AppSettingsData.FileSearchMaxResults"/>). The Files tab of <c>/tools</c>' last row (2026-10-01, the user's ask; a constant 200 until then), <c>unc_search</c>'s too; no reconnect (read at each call).</summary>
    FileSearchMaxResults,

    /// <summary>Typed: the largest file one <c>download_file</c> saves, 1 to 102400 MB (<see cref="Settings.AppSettingsData.WebDownloadMaxMegabytes"/>). The Web tab's last row (2026-10-01, the user's ask; a constant 50 MB until then); no reconnect (read at each call).</summary>
    WebDownloadMaxMegabytes,

    /// <summary>Typed: the most characters of table a <c>sql_query</c>, <c>oracle_query</c> or <c>mysql_query</c> answer carries, 1,000 to 1,000,000 (<see cref="Settings.AppSettingsData.QueryResultMaxChars"/>). On the SQL tab under the timeout, the three engines' one row (2026-10-01, the user's ask; the file tools' 32,000 until then); no reconnect (read at each call).</summary>
    QueryResultMaxChars,

    /// <summary>A picker: what a reflection may do to a skill installed with <c>/skills add</c> — <c>read-only</c> / <c>allow-and-mark</c> (<see cref="Settings.AppSettingsData.ReflectionInstalledSkills"/>). The Reflection tab of <c>/skills</c>' last row (2026-10-02, the user's call in the reflection audit); no reconnect (read when a reflection is decided). Last in the enum until <see cref="DockerTools"/>.</summary>
    ReflectionInstalledSkills,

    /// <summary>A toggle: whether a turn offers the Docker tools (<see cref="Settings.AppSettingsData.DockerTools"/>). The Docker tab of <c>/tools</c>' first row (2026-10-02, the user's ask); no reconnect (read at each turn).</summary>
    DockerTools,

    /// <summary>A toggle: the master key of the model's Docker changes (<see cref="Settings.AppSettingsData.DockerWrites"/>); off, the model may only look. The Docker tab (2026-10-02).</summary>
    DockerWrites,

    /// <summary>Typed: the Docker engine's named pipe, blank for <c>docker_engine</c>; on a Mac (2026-10-11) its socket, <see cref="DockerEngineSocketLabel"/>, blank to find it (<see cref="Settings.AppSettingsData.DockerEnginePipe"/>). The Docker tab's last row (2026-10-02); no reconnect (read at each call). Last in the enum until <see cref="DockerServers"/>.</summary>
    DockerEnginePipe,

    /// <summary>A toggle: whether <c>/server</c> offers the chosen containers (<see cref="Settings.AppSettingsData.DockerServers"/>). The Docker tab of <c>/settings</c>' first row (2026-10-02, the user's ask); a reconnect.</summary>
    DockerServers,

    /// <summary>A checklist: which of the engine's containers are servers (<see cref="Settings.AppSettingsData.DockerServerContainers"/>). The Docker tab (2026-10-02); no reconnect (read at the next connect).</summary>
    DockerServerContainers,

    /// <summary>Typed: seconds a stopping container gets (<see cref="Settings.AppSettingsData.DockerServerStopTimeoutSeconds"/>). The Docker tab (2026-10-02).</summary>
    DockerServerStopTimeoutSeconds,

    /// <summary>Typed: seconds between the stops and the start (<see cref="Settings.AppSettingsData.DockerServerPostStopDelaySeconds"/>). The Docker tab (2026-10-02).</summary>
    DockerServerPostStopDelaySeconds,

    /// <summary>Typed: seconds a started container may take to answer (<see cref="Settings.AppSettingsData.DockerServerReadyTimeoutSeconds"/>). The Docker tab (2026-10-02).</summary>
    DockerServerReadyTimeoutSeconds,

    /// <summary>A toggle: whether the exit stops the container in use (<see cref="Settings.AppSettingsData.DockerServerStopOnExit"/>). The Docker tab's last row (2026-10-02).</summary>
    DockerServerStopOnExit,

    /// <summary>A picker: where a spoken request goes — <c>chat</c> / <c>draft</c> (<see cref="Settings.AppSettingsData.SttDestination"/>). The STT tab's second row, under <see cref="SttInput"/> (later on 2026-10-02, the user's ask); no reconnect (read at each listen), so not an <see cref="SettingsMenu.IsVoiceField"/>. Last in the enum, as every newcomer.</summary>
    SttDestination,

    /// <summary>A toggle: whether a turn offers <c>camera_capture</c> (<see cref="Settings.AppSettingsData.CameraTools"/>). The Camera tab of <c>/tools</c> (2026-10-02); no reconnect.</summary>
    CameraTools,

    /// <summary>A picker: who takes the model's photo — <c>user</c> / <c>model</c> (<see cref="Settings.AppSettingsData.CameraShutter"/>). The Camera tab (2026-10-02).</summary>
    CameraShutter,

    /// <summary>A picker: how a shot is previewed — <c>live</c> / <c>post</c> / <c>disabled</c> (<see cref="Settings.AppSettingsData.CameraPreview"/>). The Camera tab (2026-10-02).</summary>
    CameraPreview,

    /// <summary>A picker over the cameras Windows lists (<see cref="Settings.AppSettingsData.CameraDevice"/>), <c>(first)</c> for none. The Camera tab (2026-10-02).</summary>
    CameraDevice,

    /// <summary>A picker: the size the camera is asked for (<see cref="Settings.AppSettingsData.CameraResolution"/>). The Camera tab (2026-10-02).</summary>
    CameraResolution,

    /// <summary>A toggle: whether a stored session keeps the camera's pictures (<see cref="Settings.AppSettingsData.CameraKeepInSessions"/>). The Camera tab (2026-10-02).</summary>
    CameraKeepInSessions,

    /// <summary>Typed: watch mode's interval in seconds (<see cref="Settings.AppSettingsData.CameraWatchSeconds"/>). The Camera tab (2026-10-02).</summary>
    CameraWatchSeconds,

    /// <summary>Typed: watch mode's change threshold in percent (<see cref="Settings.AppSettingsData.CameraWatchThreshold"/>). The Camera tab (2026-10-02).</summary>
    CameraWatchThreshold,

    /// <summary>A toggle: whether watch mode may start a turn itself (<see cref="Settings.AppSettingsData.CameraWatchUnprompted"/>). The Camera tab (2026-10-02).</summary>
    CameraWatchUnprompted,

    /// <summary>Typed: the least seconds between two unprompted watch turns (<see cref="Settings.AppSettingsData.CameraWatchMinGapSeconds"/>). The Camera tab (2026-10-02).</summary>
    CameraWatchMinGapSeconds,

    /// <summary>A toggle: whether the <c>/botchat</c> bots see the user's camera (<see cref="Settings.AppSettingsData.BotChatCamera"/>). The Botchat tab's last row (2026-10-02); no reconnect (read at the chat's start). Last in the enum, as every newcomer.</summary>
    BotChatCamera,

    /// <summary>Typed: the folder under the working directory the camera's photos are saved in (<see cref="Settings.AppSettingsData.CameraOutputFolder"/>). The Camera tab (2026-10-02), the ComfyUI output folder's shape; no reconnect. Last in the enum, as every newcomer.</summary>
    CameraOutputFolder,

    /// <summary>A toggle (the <c>Themed background</c> row): whether the terminal's page wears the theme's background (<see cref="Settings.AppSettingsData.ThemedBackground"/>). The Chat tab, under <see cref="Theme"/> (2026-10-03, the user's ask, name and place); no reconnect (read on every frame). Last in the enum, as every newcomer.</summary>
    ThemedBackground,

    /// <summary>A toggle: whether a file edit shows its diff under its 🛠️ line (<see cref="Settings.AppSettingsData.ShowFileDiffs"/>). The Options tab of <c>/tools</c>, under <see cref="CodeCollapseCount"/> (2026-10-03, the user's ask); no reconnect (read at each result). Last in the enum, as every newcomer.</summary>
    ShowFileDiffs,

    /// <summary>Typed: the most rows of an edit's diff the transcript shows, 0 (the header alone) to 500 (<see cref="Settings.AppSettingsData.DiffMaxLines"/>). The Options tab of <c>/tools</c>, under <see cref="ShowFileDiffs"/> (2026-10-03); no reconnect. Last in the enum, as every newcomer.</summary>
    DiffMaxLines,

    /// <summary>A list the user types into: the strings the shell police refuses (<see cref="Settings.AppSettingsData.ShellPoliceForbiddenStrings"/>), its top row adding one and Enter on one removing it. The Shell tab, under <see cref="ShellPolice"/>, whose switch it rides (2026-10-03, the user's idea); also the police page's strings button. No reconnect (read at each call). Last in the enum, as every newcomer.</summary>
    ShellPoliceForbiddenStrings,

    /// <summary>A picker over <see cref="Pdf.PdfEngine.Names"/>: what makes a PDF (<see cref="Settings.AppSettingsData.PdfEngine"/>). The Print tab of <c>/tools</c>, after <see cref="PrintFontSize"/> (2026-10-03, the user's ask); no reconnect (read at each PDF). Last in the enum, as every newcomer.</summary>
    PdfEngine,

    /// <summary>Typed: the most pictures a request carries, 0 (no cap) to 500 (<see cref="Settings.AppSettingsData.LlmPictureKeep"/>). The Common tab, after <see cref="LlmToolCompactType"/> (2026-10-03, the user's report); no reconnect (read at each request). Last in the enum, as every newcomer.</summary>
    LlmPictureKeep,

    /// <summary>Typed: the most megabytes of pictures a request carries, 0 (no cap) to 1000 (<see cref="Settings.AppSettingsData.LlmPictureMegabytes"/>). The Common tab, under <see cref="LlmPictureKeep"/> (2026-10-03); no reconnect.</summary>
    LlmPictureMegabytes,

    /// <summary>A toggle: whether the OpenAI API is offered as a server (<see cref="Settings.AppSettingsData.OpenAIApi"/>). <c>/settings</c>' OpenAI tab's first row (2026-10-03); a reconnect.</summary>
    OpenAIApi,

    /// <summary>Typed, masked: the OpenAI API key, saved DPAPI-encrypted (<see cref="Settings.AppSettingsData.OpenAIApiKey"/>); empty clears it. Under the switch (2026-10-03); a reconnect.</summary>
    OpenAIApiKey,

    /// <summary>Typed: the <c>max_output_tokens</c> of every OpenAI API request, 0 for none (<see cref="Settings.AppSettingsData.OpenAIApiMaxTokens"/>). Under the key (2026-10-03); a reconnect.</summary>
    OpenAIApiMaxTokens,

    /// <summary>Typed: the <c>OpenAI-Organization</c> header, empty for none (<see cref="Settings.AppSettingsData.OpenAIApiOrganization"/>). Under the output cap (2026-10-03); a reconnect.</summary>
    OpenAIApiOrganization,

    /// <summary>Typed: the <c>OpenAI-Project</c> header, empty for none (<see cref="Settings.AppSettingsData.OpenAIApiProject"/>). The OpenAI tab's last row (2026-10-03); a reconnect.</summary>
    OpenAIApiProject,

    /// <summary>A toggle: whether the <c>/botchat</c> bots get the main chat's tools (<see cref="Settings.AppSettingsData.BotChatTools"/>). The Botchat tab, after the non-TTS delay (2026-10-04, the user's ask); no reconnect (read per reply).</summary>
    BotChatTools,

    /// <summary>A checklist: the tools the <c>/botchat</c> bots get while <see cref="BotChatTools"/> is off (<see cref="Settings.AppSettingsData.BotChatLimitedTools"/>). The Botchat tab, under the tools switch (2026-10-04, the user's ask); no reconnect (read per reply).</summary>
    BotChatLimitedTools,

    /// <summary>A toggle: whether the <c>/botchat</c> bots remember (<see cref="Settings.AppSettingsData.BotChatMemory"/>). The Botchat tab, under the limited skills (2026-10-04, the user's ask); no reconnect (read per reply).</summary>
    BotChatMemory,

    /// <summary>A picker: whose memories the bots use — <c>shared-parent</c> / <c>independent</c> (<see cref="Settings.AppSettingsData.BotChatMemoryMode"/>). The Botchat tab, under the memory switch (2026-10-04, the user's ask); no reconnect (read per reply).</summary>
    BotChatMemoryMode,

    /// <summary>A toggle: whether a turn offers <c>screen_capture</c> and <c>screen_list</c> (<see cref="Settings.AppSettingsData.ScreenTools"/>). The Screen tab's first row (2026-10-04); no reconnect (read at each turn).</summary>
    ScreenTools,

    /// <summary>A picker: whether the model's screenshot waits for the user — <c>ask</c> / <c>allow</c> (<see cref="Settings.AppSettingsData.ScreenAsk"/>). The Screen tab (2026-10-04).</summary>
    ScreenAsk,

    /// <summary>A toggle: whether the viewer opens on each screenshot (<see cref="Settings.AppSettingsData.ScreenPreview"/>). The Screen tab (2026-10-04).</summary>
    ScreenPreview,

    /// <summary>Typed: the folder under the working directory the screenshots are saved in (<see cref="Settings.AppSettingsData.ScreenOutputFolder"/>). The Screen tab (2026-10-04), the Camera output folder's shape.</summary>
    ScreenOutputFolder,

    /// <summary>A toggle: whether a stored session keeps the screenshots (<see cref="Settings.AppSettingsData.ScreenKeepInSessions"/>). The Screen tab's last row (2026-10-04).</summary>
    ScreenKeepInSessions,

    /// <summary>A toggle: whether a turn offers the four SQLite tools (<see cref="Settings.AppSettingsData.SqliteTools"/>). The SQLite tab's first row (2026-10-04); no reconnect.</summary>
    SqliteTools,

    /// <summary>A picker: what the SQLite tools may do — <c>read-only</c> / <c>read-write</c> (<see cref="Settings.AppSettingsData.SqliteMode"/>). The SQLite tab's second row (2026-10-05).</summary>
    SqliteMode,

    /// <summary>A checklist: the kinds of statement <c>sqlite_execute</c> may run under read-write (<see cref="Settings.AppSettingsData.SqliteStatementsAllowed"/>). The SQLite tab's third row (later on 2026-10-05).</summary>
    SqliteStatementsAllowed,

    /// <summary>A checklist: which databases of <c>sqlite.json</c> this profile offers (<see cref="Settings.AppSettingsData.SqliteDatabasesOffered"/>). The SQLite tab (2026-10-04).</summary>
    SqliteDatabasesOffered,


    /// <summary>A toggle: whether a file in the working directory may be named by its path (<see cref="Settings.AppSettingsData.SqliteSandboxFiles"/>). The SQLite tab (2026-10-04).</summary>
    SqliteSandboxFiles,

    /// <summary>An action row (2026-10-04): Enter walks a new database through its name, file and description, opens it and adds it to that <c>sqlite.json</c> (<c>SettingsMenu.SqliteWizard.cs</c>). The SQLite tab.</summary>
    SqliteAddDatabase,

    /// <summary>A toggle: whether <c>%</c> and part of a name lists the SQLite databases too (<see cref="Settings.AppSettingsData.SqlitePercentMention"/>). The SQLite tab (2026-10-04).</summary>
    SqlitePercentMention,

    /// <summary>Typed: how many rows a <c>sqlite_query</c> without <c>max_rows</c> returns, 1 to 100,000 (<see cref="Settings.AppSettingsData.SqliteQueryMaxRows"/>). The SQLite tab (2026-10-04).</summary>
    SqliteQueryMaxRows,

    /// <summary>Typed: seconds a SQLite tool's statement may run, 1 to 600 (<see cref="Settings.AppSettingsData.SqliteQueryTimeoutSeconds"/>). The SQLite tab (2026-10-04).</summary>
    SqliteQueryTimeoutSeconds,

    /// <summary>An edit row: Enter opens the profile's <c>sqlite.json</c> in the editor (made with <see cref="Sqlite.SqliteConfigFile.EmptyText"/> when missing). The SQLite tab (2026-10-04).</summary>
    SqliteDatabasesProfile,

    /// <summary>An edit row: Enter opens the home's <c>sqlite.json</c>, every profile's. The SQLite tab's last row (2026-10-04).</summary>
    SqliteDatabasesGlobal,

    /// <summary>A toggle: whether a turn offers the nine PostgreSQL tools (<see cref="Settings.AppSettingsData.PostgresTools"/>). The PostgreSQL tab's first row (2026-10-04); no reconnect.</summary>
    PostgresTools,

    /// <summary>A picker: what the PostgreSQL tools may do — <c>read-only</c> / <c>read-write</c> (<see cref="Settings.AppSettingsData.PostgresMode"/>). The PostgreSQL tab's second row (2026-10-05).</summary>
    PostgresMode,

    /// <summary>A checklist: the kinds of statement <c>postgres_execute</c> may run under read-write (<see cref="Settings.AppSettingsData.PostgresStatementsAllowed"/>). The PostgreSQL tab's third row (2026-10-05).</summary>
    PostgresStatementsAllowed,

    /// <summary>A checklist: which connections of <c>postgres.json</c> this profile offers (<see cref="Settings.AppSettingsData.PostgresConnectionsOffered"/>). The PostgreSQL tab (2026-10-04).</summary>
    PostgresConnectionsOffered,


    /// <summary>An action row (2026-10-04): Enter picks a connection and asks for its password in a masked slot, saved to its store (<see cref="Postgres.PostgresSecrets.Save"/>). The PostgreSQL tab.</summary>
    PostgresSetPassword,

    /// <summary>An action row (2026-10-04): Enter walks a new connection through every choice, tests it and adds it to that <c>postgres.json</c> (<c>SettingsMenu.Postgres.cs</c>). The PostgreSQL tab.</summary>
    PostgresAddConnection,

    /// <summary>A toggle: whether <c>%</c> and part of a name lists the PostgreSQL connections too (<see cref="Settings.AppSettingsData.PostgresPercentMention"/>). The PostgreSQL tab (2026-10-04).</summary>
    PostgresPercentMention,

    /// <summary>Typed: how many rows a <c>postgres_query</c> without <c>max_rows</c> returns, 1 to 100,000 (<see cref="Settings.AppSettingsData.PostgresQueryMaxRows"/>). The PostgreSQL tab (2026-10-04).</summary>
    PostgresQueryMaxRows,

    /// <summary>Typed: seconds a PostgreSQL tool's statement may run, 1 to 600 (<see cref="Settings.AppSettingsData.PostgresQueryTimeoutSeconds"/>). The PostgreSQL tab (2026-10-04).</summary>
    PostgresQueryTimeoutSeconds,

    /// <summary>An edit row: Enter opens the profile's <c>postgres.json</c> in the editor (made with <see cref="Postgres.PostgresConfigFile.EmptyText"/> when missing). The PostgreSQL tab (2026-10-04).</summary>
    PostgresConnectionsProfile,

    /// <summary>An edit row: Enter opens the home's <c>postgres.json</c>, every profile's. The PostgreSQL tab's last row (2026-10-04).</summary>
    PostgresConnectionsGlobal,

    /// <summary>A toggle: whether the <c>/botchat</c> bots get this profile's offered ComfyUI workflows (<see cref="Settings.AppSettingsData.BotChatComfy"/>). The Botchat tab, after the multi-embedded rows (later on 2026-10-04, the user's ask: in place of <c>Botchat images enabled</c> and the two workflow pickers); no reconnect (read per reply).</summary>
    BotChatComfy,

    /// <summary>A checklist: the installed ComfyUI workflows the <c>/botchat</c> bots get while <see cref="BotChatComfy"/> is off (<see cref="Settings.AppSettingsData.BotChatLimitedComfyWorkflows"/>). The Botchat tab, under the switch (later on 2026-10-04, the user's ask); no reconnect (read per reply).</summary>
    BotChatLimitedComfyWorkflows,

    /// <summary>Typed: how many rows an edit's diff may have before it folds once its tool run is over, 0 (off) to 500 (<see cref="Settings.AppSettingsData.DiffCollapseCount"/>). The Options tab of <c>/tools</c>, under <see cref="DiffMaxLines"/> (2026-10-04, the user's ask and place); no reconnect (read at each result). Last in the enum, as every newcomer.</summary>
    DiffCollapseCount,

    /// <summary>Typed: the quality <c>image_edit</c> writes a JPEG, JPEG XL or HEIF at when the call gives none, 1 to 100 (<see cref="Settings.AppSettingsData.ImageEditQuality"/>). The Files tab of <c>/tools</c>, after File search max results (2026-10-04, the user's pick, with the image tools); the Image menu's first row since 2026-10-10 (<c>/tools</c> › Files › Image, the user's ask); no reconnect (read at each call).</summary>
    ImageEditQuality,

    /// <summary>A picker over none, basic and all: the metadata <c>image_edit</c> keeps when the call does not say (<see cref="Settings.AppSettingsData.ImageEditMetadata"/>). The Files tab, under Image edit quality (2026-10-04); the Image menu since 2026-10-10; no reconnect.</summary>
    ImageEditMetadata,

    /// <summary>Typed: the folder under the profile's images folder (the working directory's until 2026-10-10) <c>image_edit</c> writes into when the call gives no <c>to</c>, empty = beside the source (<see cref="Settings.AppSettingsData.ImageEditOutputFolder"/>). The Files tab's last row (2026-10-04), the Image menu's under Image edit mode since 2026-10-10; the Camera output folder's rule; no reconnect.</summary>
    ImageEditOutputFolder,

    /// <summary>A picker over beside-original and overwrite-original: where an edited picture goes, for the picture windows' right-click menu and <c>image_edit</c> (<see cref="Settings.AppSettingsData.ImageEditMode"/>). The Files tab, under Image edit metadata (later on 2026-10-04, the user's ask); the Image menu since 2026-10-10; no reconnect. Last in the enum, as every newcomer.</summary>
    ImageEditMode,

    /// <summary>How the transcript draws the user's sent line (<see cref="Settings.AppSettingsData.UserLineStyle"/>, 2026-10-04): a picker, no reconnect. Last, so the flat list's rows above keep their places.</summary>
    UserLineStyle,

    /// <summary>A toggle: whether a turn offers the YouTube tools (<see cref="Settings.AppSettingsData.YouTubeTools"/>). The YouTube tab of <c>/tools</c>' first row (2026-10-05, the YouTube plan); no reconnect (read at each turn).</summary>
    YouTubeTools,

    /// <summary>Typed, masked: the YouTube Data API key, saved DPAPI-encrypted (<see cref="Settings.AppSettingsData.YouTubeApiKey"/>); empty clears it. The YouTube tab's second row (2026-10-05); no reconnect (read at each search).</summary>
    YouTubeApiKey,

    /// <summary>Typed: how many videos a search lists, 1 to 20 (<see cref="Settings.AppSettingsData.YouTubeSearchMaxResults"/>). The YouTube tab (2026-10-05); no reconnect.</summary>
    YouTubeSearchMaxResults,

    /// <summary>A toggle: whether a played video starts at once or waits cued (<see cref="Settings.AppSettingsData.YouTubeAutoplay"/>). The YouTube tab (2026-10-05); no reconnect.</summary>
    YouTubeAutoplay,

    /// <summary>A picker over pause, duck and none: what a playing video does while the app speaks or listens (<see cref="Settings.AppSettingsData.YouTubeVoice"/>). The YouTube tab (2026-10-05); no reconnect.</summary>
    YouTubeVoice,

    /// <summary>A picker over relevance, date, views, rating and title: a search's order unless it names one (<see cref="Settings.AppSettingsData.YouTubeSearchOrder"/>). The YouTube tab (2026-10-08); no reconnect.</summary>
    YouTubeSearchOrder,

    /// <summary>A picker over none, moderate and strict: a search's safe search unless it names one (<see cref="Settings.AppSettingsData.YouTubeSafeSearch"/>). The YouTube tab (2026-10-08); no reconnect.</summary>
    YouTubeSafeSearch,

    /// <summary>Typed: a search's region, two letters, empty for none (<see cref="Settings.AppSettingsData.YouTubeRegion"/>). The YouTube tab (2026-10-08); no reconnect.</summary>
    YouTubeRegion,

    /// <summary>Typed: the language a search favours, empty for none (<see cref="Settings.AppSettingsData.YouTubeLanguage"/>). The YouTube tab's last row (2026-10-08); no reconnect.</summary>
    YouTubeLanguage,

    /// <summary>Typed: the folder under the profile's images folder a clipboard picture is saved in when its message is sent (<see cref="Settings.AppSettingsData.PastedImagesFolder"/>). The Files tab's last row (2026-10-09), the Image menu's since 2026-10-10; the Camera output folder's rule; no reconnect. Last in the enum, as every newcomer.</summary>
    PastedImagesFolder,

    /// <summary>Whether a turn offers the picture tools, view_image, image_info and image_edit (<see cref="Settings.AppSettingsData.ImageTools"/>): their own group since 2026-10-10 (the user's ask; on File tools until then), the Image menu's first row on <c>/tools</c> › Files; a toggle, no reconnect (read at each turn). Last in the enum until <see cref="Advisors"/>.</summary>
    ImageTools,

    /// <summary>A toggle: whether a turn offers <c>neon_advisor</c> (<see cref="Settings.AppSettingsData.Advisors"/>). The Advisors tab of <c>/settings</c>' first row (2026-10-10, the user's ask); no reconnect (read at each turn).</summary>
    Advisors,

    /// <summary>A list: the advisors, each a profile and its role (<see cref="Settings.AppSettingsData.AdvisorList"/>). The Advisors tab (2026-10-10); Enter opens its editor.</summary>
    AdvisorList,

    /// <summary>A picker over <see cref="NeonSidekick.Advisors.AdvisorContextMode.Names"/>: what an advisor call sends besides the question (<see cref="Settings.AppSettingsData.AdvisorContext"/>). The Advisors tab (2026-10-10).</summary>
    AdvisorContext,

    /// <summary>Typed: the most advisor calls a turn, 1 to 10 (<see cref="Settings.AppSettingsData.AdvisorCallsPerTurn"/>). The Advisors tab (2026-10-10).</summary>
    AdvisorCallsPerTurn,

    /// <summary>Typed: an advisor's tool round trips, 0 to 10 (<see cref="Settings.AppSettingsData.AdvisorMaxToolIterations"/>). The Advisors tab (2026-10-10).</summary>
    AdvisorMaxToolIterations,

    /// <summary>Typed: the questions and answers each advisor's thread keeps, 1 to 50 (<see cref="Settings.AppSettingsData.AdvisorThreadPairs"/>). The Advisors tab (2026-10-10).</summary>
    AdvisorThreadPairs,

    /// <summary>A picker over <see cref="App.BotChatMultiEmbedded.Names"/>: an embedded advisor naming another model (<see cref="Settings.AppSettingsData.AdvisorEmbedded"/>). The Advisors tab (2026-10-10).</summary>
    AdvisorEmbedded,

    /// <summary>A toggle: whether each advisor call asks first (<see cref="Settings.AppSettingsData.AdvisorConfirm"/>). The Advisors tab's row under the tool iterations, last of the Each call run (later on 2026-10-10, the user's ask; the tab's last row earlier that day). Last in the enum until <see cref="DockerServerMatchWords"/>.</summary>
    AdvisorConfirm,

    /// <summary>A list the user types into: the words that make a container an LLM server in <see cref="DockerServerContainers"/>' checklist (<see cref="Settings.AppSettingsData.DockerServerMatchWords"/>), its top row adding one, Enter on one removing it, a button restoring the defaults. The Docker tab, under the containers (2026-10-10, the user's ask); no reconnect. Last in the enum until <see cref="ModelAvatar"/>.</summary>
    DockerServerMatchWords,

    /// <summary>A grid picker over the emoji catalog: the profile's avatar (<see cref="Settings.AppSettingsData.ModelAvatar"/>), opening its replies, its /botchat speaker line and the window title (2026-10-10, the user's ask). The Chat tab, right under Profile; no reconnect. "Avatar" until 2026-10-12 (the user's call, once <see cref="UserAvatar"/> came beside it: the row, the field and the key renamed, no migration). Last in the enum until <see cref="BotChatRecentContext"/>.</summary>
    ModelAvatar,

    /// <summary>A toggle: whether the <c>/botchat</c> bots are told the main chat's last messages (<see cref="Settings.AppSettingsData.BotChatRecentContext"/>). The Botchat tab's last row (2026-10-10, the advisors' lesson); no reconnect (read when a chat starts). Last in the enum, as every newcomer.</summary>
    BotChatRecentContext,

    /// <summary>A toggle: whether the draft row shows the emoji button (<see cref="Settings.AppSettingsData.EmojiPicker"/>). The Chat tab's input-line run's last row, under Hide /exit autocomplete (2026-10-11, the user's ask); no reconnect (read at each draw). Last in the enum, as every newcomer.</summary>
    EmojiPicker,

    /// <summary>A toggle: whether a turn offers the shopping list's tools (<see cref="Settings.AppSettingsData.ShoppingTools"/>). The Shopping menu's first row on <c>/tools</c> › Core (2026-10-11, the user's ask); no reconnect (read at each turn). Last in the enum, as every newcomer.</summary>
    ShoppingTools,

    /// <summary>An action row: Enter opens the shopping stores' manager (<see cref="ManageShoppingStoresAsync"/>), the stores added, renamed, removed and reindexed. The Shopping menu's second row (2026-10-11, the user's ask). Last in the enum, as every newcomer.</summary>
    ShoppingStores,

    /// <summary>An action row: Enter opens the shopping list's pane, the bare <c>/shopping</c>'s (<see cref="ManageShoppingListAsync"/>). The Shopping menu's second row, above Shopping stores (2026-10-11, the user's ask). Last in the enum, as every newcomer.</summary>
    ShoppingList,

    /// <summary>The model avatar's grid picker again, for the user's own (<see cref="Settings.AppSettingsData.UserAvatar"/>): it opens each of the user's lines in the transcript in place of <c>›</c>. The Chat tab, right under <see cref="ModelAvatar"/> (2026-10-12, the user's ask); no reconnect (read at each sent line). Last in the enum, as every newcomer.</summary>
    UserAvatar,

    /// <summary>A picker over <see cref="Skills.ReflectionConfirmMode.Names"/>: which of a reflection's writes wait for the user (<see cref="Settings.AppSettingsData.ReflectionConfirmMode"/>). The Reflection tab of <c>/skills</c>, the first row of What it may change (2026-10-12, the user's ask and place); no reconnect (read when a reflection is decided). Last in the enum, as every newcomer.</summary>
    ReflectionConfirmMode,

    /// <summary>A picker: what a <c>/botchat</c> bot's mention of a picture it never drew does — <c>disabled</c> / <c>generate</c> (<see cref="Settings.AppSettingsData.BotChatImageMentionMode"/>). The Botchat tab, right under <see cref="BotChatImg2ImgMode"/> (2026-10-13, the user's ask and place); no reconnect (read per reply). Last in the enum, as every newcomer.</summary>
    BotChatImageMentionMode,

    /// <summary>A picker over <see cref="Web.BotcheckWindow.Names"/>: what a bot check the web tools cannot get past does — <c>ask</c> / <c>automatic</c> / <c>disabled</c> (<see cref="Settings.AppSettingsData.WebBrowserBotcheckWindow"/>). The Web tab, right under <see cref="WebBrowserPath"/> (2026-10-13, the user's ask and name); no reconnect (read at each bot check). Last in the enum, as every newcomer.</summary>
    WebBrowserBotcheckWindow,

    /// <summary>The Browser window's home page, a web address or empty for the blank page (<see cref="Settings.AppSettingsData.WebBrowserHomePage"/>). Typed; the Web tab, right under <see cref="WebBrowserBotcheckWindow"/> (2026-10-13, the navigation bar); no reconnect (read at each home). Last in the enum, as every newcomer.</summary>
    WebBrowserHomePage,

    /// <summary>A picker over <see cref="Viewer.BrowserPrivacy.Names"/>: whether the Browser window keeps its history or clears it as it closes (<see cref="Settings.AppSettingsData.BrowserPrivacy"/>). The Web tab, right under <see cref="WebBrowserHomePage"/> (later on 2026-10-13, the user's ask and name); no reconnect (read at each close). Last in the enum, as every newcomer.</summary>
    BrowserPrivacy,

    /// <summary>A toggle: whether this profile reads the global skills, <c>&lt;home&gt;\skills</c> (<see cref="Settings.AppSettingsData.GlobalSkills"/>); off, it sees and writes only its own. The Options tab of <c>/skills</c>, right under <see cref="AgentSkills"/> (2026-10-13, the user's ask, name and default); no reconnect (read at each scan). Last in the enum, as every newcomer.</summary>
    GlobalSkills,

    /// <summary>A picker over <see cref="Web.OpenLinksIn.Names"/>: where a link the model opens goes, the default browser or the Browser window (<see cref="Settings.AppSettingsData.WebOpenLinksIn"/>). The Web tab, right under <see cref="BrowserPrivacy"/> (later still on 2026-10-13, the user's ask and name); no reconnect (read at each call). Last in the enum, as every newcomer.</summary>
    WebOpenLinksIn,

    /// <summary>Typed: the most passes plan mode's gap-closing loop runs, 0 to 20, 0 for no limit (<see cref="Settings.AppSettingsData.PlanGapPasses"/>). On the Common tab under <see cref="LlmMaxToolIterations"/> (2026-10-13), last here so the flat list's rows above keep their places; read when a loop starts.</summary>
    PlanGapPasses,
}

/// <summary>The tabs of <c>/settings</c> on the pane, in strip order (Chat, Sessions, Botchat, Common, Http, Embedded, Docker, Anthropic, OpenAI, TTS, STT, Advisors — General renamed Chat and Sessions and Botchat moved beside it under the Client group, the user's call, later on 2026-10-10; General, Common, Http, Embedded, Docker, Anthropic, OpenAI, TTS, STT, Sessions, Botchat, Advisors — the LLM tab split in two, Common and Http, the user's call, 2026-10-10; General, LLM, Embedded, Docker, Anthropic, OpenAI, TTS, STT, Sessions, Botchat — LLM second, the user's order, 2026-10-04; General, Embedded, Docker, Claude, OpenAI, LLM, TTS, STT, Sessions, Botchat — the Claude and OpenAI tabs after Docker, the user's place, 2026-10-03; General, Embedded, LLM, TTS, STT, Sessions, Botchat — the user's order, 2026-09-29; Sessions right after General — the user's order, 2026-09-18 — until then; STT last since 2026-09-19, when the Ask, Files and Web tabs moved to <c>/tools</c> — <see cref="SettingsMenu.ToolsTabFields"/> — and, later that day, the Skills tab to <c>/skills</c> as its Options tab — <see cref="SettingsMenu.SkillsTabFields"/>); the value is the index into <see cref="SettingsMenu.TabTitles"/> and <see cref="SettingsMenu.TabFields"/>.</summary>
public enum SettingsTab
{
    /// <summary>
    /// The profile, the input line, the transcript, the screen and the outside apps: what the chat itself looks and behaves like.
    /// "General" until 2026-10-10, renamed Chat (the user's call: "General" said nothing) and made the first tab of the Client group,
    /// Sessions and Botchat beside it (<see cref="SettingsMenu.ClientTabTitle"/>).
    /// </summary>
    Chat,

    /// <summary>The session store's rows (2026-09-18): second, under Client after Chat, since 2026-10-10 (the user's order); after STT from 2026-09-29 (the user's order; second from later on 2026-09-18, last that morning).</summary>
    Sessions,

    /// <summary>The <c>/botchat</c> rows (2026-09-25, the user's ask: a Botchat tab on <c>/settings</c>): third, under Client after Sessions, since 2026-10-10 (the user's order); after Sessions on the root strip until then (a Claude tab followed it on 2026-09-27 until later that day, when its rows moved to <c>/tools</c>' ClaudeCode tab, the user's call; <c>/claude</c>'s four came back to the Anthropic tab on 2026-10-10).</summary>
    BotChat,

    /// <summary>
    /// The LLM rows every server reads, whatever its kind: the URL and model, the reasoning, the tools and limits, the context, the
    /// pictures and verbs. "LLM" until 2026-10-10, when its six rows only an OpenAI-compatible HTTP server reads went to
    /// <see cref="Http"/> (the user's call: the Anthropic API, the OpenAI API and the Claude Code server never read them). Second
    /// since 2026-10-04 (the user's order: General, LLM, Embedded…); sixth from 2026-10-03 (the Claude and OpenAI tabs before it),
    /// fourth from 2026-10-02, third from 2026-09-19 (the skills' rows sat between, 2026-09-18 until then).
    /// </summary>
    Common,

    /// <summary>
    /// The LLM rows only an OpenAI-compatible HTTP server reads (2026-10-10, the user's call, out of the LLM tab): the scan mode and the
    /// key, the thinking sent back and the reasoning estimate, the <c>/sampling</c> door and its Hugging Face switch. A local or LAN
    /// server reads them, and the embedded LLM and Docker's containers, which are such servers too; the Anthropic API, the OpenAI API
    /// and the Claude Code server have clients of their own that never do. "Http", not "Local" (the user's pick): Embedded, Docker
    /// and the Claude Code server run here too. Right after <see cref="Common"/>.
    /// </summary>
    Http,

    /// <summary>
    /// The embedded model's rows (2026-09-29): the <c>Embedded servers enabled</c> switch, the catalog door, the backend, the
    /// context size, the GPU layers, the vision switch and the MTP switch — third since 2026-10-04 (LLM before it, the user's order),
    /// second from later on 2026-09-29 (the user's order: General, Embedded, LLM, TTS, STT, Sessions, Botchat); after STT until then,
    /// where the Anthropic API's tab had stood.
    /// </summary>
    Embedded,

    /// <summary>The Docker servers' rows (2026-10-02, the user's ask: a Docker tab next to Embedded): the switch, the containers, the three waits and the exit stop.</summary>
    Docker,

    /// <summary>
    /// The Claude servers' rows (2026-10-03, the user's ask: off <c>/tools</c>' ClaudeCode tab, which kept <c>/claude</c>'s and the
    /// advisor's): the Claude Code server's switch, <c>/claude</c>'s executable, permissions, model and effort (2026-10-10, the user's call:
    /// the advisor tool retired and that tab gone), then the Anthropic API's switch, key, output cap and prompt caching (the Claude Code
    /// server last until 2026-10-10, the user's call). "Claude" until 2026-10-04 (the user's call: Anthropic, so it no longer shares a
    /// name with <c>/tools</c>' tab). Since later on 2026-10-10 the executable leads, under its own heading (both the server and
    /// <c>/claude</c> read it), then the server's switch, <c>/claude</c>'s three and the Anthropic API's four, each run headed
    /// (<see cref="SettingsMenu.TabSections"/>, the user's ask).
    /// </summary>
    Anthropic,

    /// <summary>The OpenAI API's rows (2026-10-03, the user's ask): the switch, the key, the output cap, the organization and the project.</summary>
    OpenAI,

    /// <summary>The profile advisors' rows (2026-10-10, the user's ask: a tab of its own under <c>/settings</c>, after Botchat; after STT once Sessions and Botchat moved under Client, later that day; after OpenAI and before TTS later still, the user's order): the switch, the list, then how a call goes.</summary>
    Advisors,

    Tts,

    /// <summary>The voice rows, last since 2026-09-19 (Ask, Files and Web after it until then) until the Sessions and Botchat tabs came after them; before Advisors since Sessions and Botchat moved under Client (2026-10-10); last again since later that day (Advisors before Voice, the user's order).</summary>
    Stt,
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
/// (<see cref="MenuPane"/>: <c>/settings</c> is one level — its rows under the Client / Servers / Advisors / Voice
/// groups' tabs of <see cref="TabFields"/>, the strip and keys the info pane's — a row's picker or typed
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
    public const string TabKeys = "Enter = edit · Space = flip · ←/→ tabs · ESC = close";   // Space since 2026-10-04

    /// <summary>The hint row of <c>/settings</c>' own tabs (2026-10-04): <see cref="TabKeys"/> and the search a typed letter starts. Pinned.</summary>
    public const string SettingsTabKeys = "Enter = edit · Space = flip · type = search · ←/→ tabs · ESC = close";
    public const string EditKeys = "Enter = save · ESC = back";

    /// <summary>The Model avatar and User avatar rows' value while there is none (2026-10-10). Pinned.</summary>
    public const string NoAvatarLabel = "(none)";
    public const string PickKeys = "Enter = choose · ESC = back";

    /// <summary>The hint of a picker with a <c>(none)</c> row (<c>TTS voice 2</c>): Backspace moves the cursor to it.</summary>
    public const string NoneKeys = "Enter = choose · Backspace = none · ESC = back";
    public const string SwitchKeys = "Enter = switch · a–z = jump · ESC = back";
    public const string KeepKeys = "Enter = choose · ESC = keep";

    /// <summary>The model picker's hint on the pane (2026-10-03, the user's ask): <see cref="KeepKeys"/> with the type-to-filter. Pinned.</summary>
    public const string ModelKeys = "Enter = choose · " + MenuFilter.TypeAndKeepKeys;

    /// <summary>
    /// The theme pickers' hints (2026-10-03, the user's ask): <see cref="KeepKeys"/> and <see cref="PickKeys"/> with the letter
    /// jump (<see cref="MenuPage.JumpNames"/>).
    /// </summary>
    public const string ThemeKeepKeys = "Enter = choose · a–z = jump · ESC = keep";
    public const string ThemePickKeys = "Enter = choose · a–z = jump · ESC = back";

    /// <summary>The yes/no pane's hint (<see cref="ConfirmAsync"/>). Pinned.</summary>
    public const string ConfirmKeys = "y / n = pick · Enter = choose · ESC = no";

    /// <summary>The yes/no pane's rows, <c>No</c> first — the row the cursor opens on. Pinned.</summary>
    public static readonly IReadOnlyList<string> ConfirmRows = ["No", "Yes"];

    /// <summary>The yes/no pane's <see cref="MenuPage.Hotkeys"/>: <c>n</c> moves the cursor to <c>No</c>, <c>y</c> to <c>Yes</c> (Enter still picks). Pinned.</summary>
    public static readonly IReadOnlyDictionary<char, int> ConfirmHotkeys = new Dictionary<char, int> { ['n'] = 0, ['y'] = 1 };

    /// <summary>The status line for a settings row that cannot change while a reply runs (a reconnect, the profile, the sandbox, the tools flip). Pinned.</summary>
    public const string NotWhileReplyRunsNotice = "(not while a reply runs)";

    /// <summary>
    /// The status line when the reply a pane was opened under ends with the pane still open (2026-10-12, the user's ask): the rows
    /// <see cref="NotWhileReplyRunsNotice"/> held back are open now, in the same pane; what they change is carried out as the pane closes. Pinned.
    /// </summary>
    public const string ReplyEndedNotice = "(the reply ended: every row is open now)";
    public const string ModelTitle = "🤖 Model";

    /// <summary>The <c>/reasoning</c> picker's label; ESC keeps the level in use. The settings row's level is <see cref="Breadcrumb"/> over <see cref="FieldName"/>.</summary>
    public const string ReasoningTitle = "💭 LLM reasoning";

    /// <summary>The title of <c>/theme</c>'s picker (2026-09-23); the abacus since 2026-10-04, the toolbar's Themes glyph (the palette until then, ComfyUI's too). Pinned.</summary>
    public const string ThemeTitle = "🧮 Theme";

    /// <summary>What <c>/theme &lt;name&gt;</c> answers to a word that is not one of <see cref="ThemeName.Names"/>. Pinned.</summary>
    public static string ThemeNameError(string name) => ThemeNameError(name, ThemeName.Names);

    /// <summary>As <see cref="ThemeNameError(string)"/> over <paramref name="names"/>, the built-ins and the user's themes (2026-10-01).</summary>
    public static string ThemeNameError(string name, IReadOnlyList<string> names) =>
        $"No theme named \"{name}\". /theme takes " + string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1] + ", or nothing to pick from a list.";

    /// <summary>What <c>/theme</c> says when the theme picked is the one already in force: nothing is cleared. Pinned.</summary>
    public static string ThemeAlreadyNotice(string name) => $"Theme: {name} (already in force)";

    /// <summary>What <c>/reasoning &lt;level&gt;</c> answers to a word that is not one of <see cref="Llm.ReasoningLevel.Levels"/>. Pinned.</summary>
    public static readonly string ReasoningLevelError = "/reasoning takes " + string.Join(", ", Llm.ReasoningLevel.Levels[..^1]) + " or " + Llm.ReasoningLevel.Levels[^1] + ", or nothing to pick from a list.";
    public const string MenusNeedTerminalError = "Menus need an interactive ANSI terminal; edit profile.json instead.";
    public const string NoUrlError = "No LLM server. Set the LLM URL in /settings first.";
    public const string NoModelsListedError = "The server lists no models; use /model <id>.";
    public const string UnchangedNotice = "unchanged";
    public const string TtsSpeedRangeError = "must be a speed multiplier between 0.5 and 2";
    public const string TtsVoiceMixRangeError = "must be a whole number from 0 to 100 (the primary voice's share)";
    public const string SttInterruptEchoGuardRangeError = "must be a whole number from 50 to 100 (100 = only the exact phrase counts as an echo)";
    public const string SttInterruptConfirmRangeError = "must be a whole number of milliseconds from 0 to 2000";
    public const string ContextLengthRangeError = "must be 0 (the server's figure) or a whole number of tokens";
    public const string PushToTalkKeyError = "must be one of F1–F8, Insert, Home, End, PageUp or PageDown";

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

    /// <summary>How the menu shows a 0 <see cref="SettingsField.LlmPictureKeep"/> or <see cref="SettingsField.LlmPictureMegabytes"/> (2026-10-03). Pinned.</summary>
    public const string NoCapLabel = "no cap";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.LlmPictureKeep"/> (2026-10-03). Pinned.</summary>
    public static readonly string LlmPictureKeepRangeError =
        "must be " + AppSettingsData.MinLlmPictureKeep.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxLlmPictureKeep.ToString(CultureInfo.InvariantCulture) + " pictures (0 = no cap)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.LlmPictureMegabytes"/> (2026-10-03). Pinned.</summary>
    public static readonly string LlmPictureMegabytesRangeError =
        "must be " + AppSettingsData.MinLlmPictureMegabytes.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxLlmPictureMegabytes.ToString(CultureInfo.InvariantCulture) + " MB (0 = no cap)";

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
        "must be more than 0 and at most " + max.ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.PlanGapPasses"/> (2026-10-13). Pinned.</summary>
    public static readonly string PlanGapPassesRangeError =
        "must be " + AppSettingsData.MinPlanGapPasses.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxPlanGapPasses.ToString(CultureInfo.InvariantCulture) + " passes (0 for no limit)";

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

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.DiffMaxLines"/> (2026-10-03).</summary>
    public static readonly string DiffMaxLinesRangeError =
        "must be " + AppSettingsData.MinDiffMaxLines.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxDiffMaxLines.ToString(CultureInfo.InvariantCulture) + " lines (0 = the header alone)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.DiffCollapseCount"/> (2026-10-04).</summary>
    public static readonly string DiffCollapseCountRangeError =
        "must be " + AppSettingsData.MinDiffCollapseCount.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxDiffCollapseCount.ToString(CultureInfo.InvariantCulture) + " lines (0 = off)";

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

    /// <summary>The notice when there is no preset to pick (a build that lost the built-ins; the home <c>voices</c> folder only adds to them since 2026-10-02).</summary>
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
    public const string ProfileTitle = ChatScreen.ProfileToolGlyph + " Profile";   // the glyph the toolbar wears for the picker (later on 2026-09-29)
    public const string ProfileKeys = "Enter = switch · a–z = jump · ESC = keep";

    /// <summary>The <c>/server</c> picker's label; ESC keeps the server in use.</summary>
    public const string ServerTitle = "🖥️ LLM server";

    /// <summary>The startup picker's label, when several servers answered a blank URL, or any did at the app's start (2026-09-23; "Several LLM servers answered" until then); ESC takes the first listed, as before.</summary>
    public const string StartupServerTitle = "🖥️ Pick an LLM server";
    public const string StartupServerKeys = "Enter = choose · ESC = the first listed";

    private static readonly SettingsField[] Fields = Enum.GetValues<SettingsField>();

    /// <summary>The leaf tabs' titles, one per <see cref="SettingsTab"/>, each a sub-tab of <see cref="StripTitles"/>' groups (five since 2026-09-19: Ask, Files and Web are <c>/tools</c>' tabs, <see cref="ToolsText.TabTitles"/>, and Skills is <c>/skills</c>' Options tab, <see cref="SkillsText.OptionsTabTitle"/>). Pinned.</summary>
    public static readonly IReadOnlyList<string> TabTitles = [ChatTabTitle, "Sessions", "Botchat", CommonTabTitle, HttpTabTitle, EmbeddedTabTitle, DockerTabTitle, AnthropicTabTitle, OpenAITabTitle, AdvisorsTabTitle, "TTS", "STT"];

    /// <summary>The title of the tab of the chat's own rows: the profile, the input line, the transcript, the screen, the outside apps (2026-10-10, the user's pick; "General" until then). Pinned.</summary>
    public const string ChatTabTitle = "Chat";

    /// <summary>The title of the tab of LLM rows every server reads (2026-10-10, the user's call; "LLM" until then). Pinned.</summary>
    public const string CommonTabTitle = "Common";

    /// <summary>The title of the tab of LLM rows only an OpenAI-compatible HTTP server reads (2026-10-10, the user's pick and casing). Pinned.</summary>
    public const string HttpTabTitle = "Http";

    /// <summary>The embedded model tab's strip title (2026-09-29). Pinned.</summary>
    public const string EmbeddedTabTitle = "Embedded";

    /// <summary>
    /// The rows of each tab on the pane, indexed by <see cref="SettingsTab"/>, in the order shown
    /// (Chat, Sessions, Botchat, Common, Http, Embedded, Docker, Anthropic, OpenAI, Advisors, TTS, STT — Advisors before Voice, the user's order,
    /// later still on 2026-10-10; Chat, Sessions, Botchat, Common, Http, Embedded, Docker, Anthropic, OpenAI, TTS, STT, Advisors — General renamed Chat, and Sessions
    /// and Botchat beside it under the Client group, the user's call, later on 2026-10-10; General, Common, Http, Embedded, Docker, Anthropic,
    /// OpenAI, TTS, STT, Sessions, Botchat, Advisors — the LLM tab split in two, the user's call, 2026-10-10; General, LLM, Embedded, Docker, Anthropic, OpenAI, TTS, STT, Sessions, Botchat — LLM second, the user's order, 2026-10-04;
    /// General, Embedded, Docker, Claude, OpenAI, LLM, TTS, STT, Sessions, Botchat — the Claude and OpenAI tabs after Docker, the user's place, 2026-10-03;
    /// General, Embedded, LLM, TTS, STT, Sessions, Botchat — the user's order, 2026-09-29; General, Sessions, LLM, TTS, STT from 2026-09-18, Sessions right after General; the Ask,
    /// Files and Web tabs are <c>/tools</c>' since 2026-09-19, <see cref="ToolsTabFields"/>, and the Skills tab
    /// <c>/skills</c>' Options tab since later that day, <see cref="SkillsTabFields"/>).
    /// Chat (General until 2026-10-10) is spelled out in five runs (2026-10-01, the user's call: 24 rows grown by "under X" and "last" had scattered their
    /// kin — the working directory at the top and its header switch far below, the theme apart from the themed viewer): who and where
    /// (the profile, what a new one copies, where its files live, the memory it carries; since 2026-10-11 the avatar under them, the user's
    /// order: profile, working directory, memory mode, avatar), the input line (the message queue's switch and
    /// its cancel mode, the message history, the typo intercept, the hidden <c>/exit</c>), the transcript (its Markdown, the paste preview,
    /// the thumbnails' switch and size, what <c>/copy</c> takes), the screen (the theme, then top to bottom: the welcome splash, the header
    /// and the working directory in it, the toolbar, the performance bar, the menus' max height), and the outside apps last (the draft
    /// editor, the image viewer and the themed-viewer switch). Since 2026-10-12 (the user's ask) the bars have a run of their own,
    /// Tool bars, under the screen's: the toolbar, the performance bar, the home bar and their order, the menus' max height staying
    /// the screen's last row. Each dependent row stays right under the one it hangs on; LLM
    /// was spelled out too, in five runs (2026-10-01, the user's call, as General's that day), and since 2026-10-10 is two tabs (the
    /// user's call): Common keeps the runs every server reads — the connection (the URL, the model), how it answers (the reasoning
    /// level, the thinking's show), tools and limits (<see cref="SettingsField.LlmOfferTools"/>, the round-trip cap, the two timeouts),
    /// the context (its length, the mid-turn usage, the max turns, the auto-compact share ahead of the compact rows it times, then
    /// <see cref="SettingsField.LlmToolCompactType"/>, still under the offer-tools switch as the user ordered on 2026-09-15), and the
    /// pictures and fun verbs last; Http holds what only an OpenAI-compatible HTTP server reads, in the order they had there — the
    /// connection (the scan mode, where a blank URL looks; the key), the thinking (the preserve and estimate rows), the sampling door
    /// and its Hugging Face switch; TTS is spelled out (the user's order, 2026-09-16): the switch, the
    /// source, the server's URL, the preview toggle (no reconnect), then the voice preset (2026-09-27, just above the voice), the voices, the mix and the speed; STT was the <see cref="IsVoiceField"/>
    /// fields in enum order, <see cref="SettingsField.SttDestination"/> (2026-10-02, no reconnect, so not a voice field) under the first, the switch; Sessions (2026-09-18, last that morning, second since) is the
    /// logging switch, the retention days, the naming mode, the show-name picker under it (later that day), the tool switch and the search cap (the user's order, 2026-09-18).
    /// Since later on 2026-10-10 every tab is headed (<see cref="TabSections"/>, the user's ask) and four moved a row to sit under its
    /// heading (the user's call): Anthropic leads with <see cref="SettingsField.ClaudeCodeExecutable"/>, the one row the Claude Code server
    /// and <c>/claude</c> both read, above the server's switch; STT is spelled out — listening (the switch, the destination, the
    /// push-to-talk key, the Whisper model), the wake word (its switch, the phrase, the Vosk model it listens with), the interruptions (the
    /// switch, the echo guard, the confirm time); Sessions' save-thinking switch went from last to under the logging switch, the Saving run
    /// with the retention days; Advisors' confirm switch from last to under the tool iterations, the Each call run.
    /// With <see cref="SkillsTabFields"/> and <see cref="ToolsTabFields"/> they are every <see cref="SettingsField"/> once (pinned).
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<SettingsField>> TabFields =
    [
        [SettingsField.Profile, SettingsField.WorkingDirectory, SettingsField.MemoryMode, SettingsField.ModelAvatar, SettingsField.UserAvatar,
         SettingsField.QueueMode, SettingsField.QueueCancelMode, SettingsField.KeepMessageHistory, SettingsField.CommandTypoIntercept, SettingsField.HideExitAutocomplete, SettingsField.EmojiPicker,
         SettingsField.TranscriptMarkdown, SettingsField.PastePreviewLines, SettingsField.ShowImageThumbnails, SettingsField.ImageThumbnailSize, SettingsField.CopyUserPrompt, SettingsField.UserLineStyle,
         SettingsField.Theme, SettingsField.ThemedBackground, SettingsField.ThemedExternalWindows, SettingsField.WelcomeSplash, SettingsField.ShowHeader, SettingsField.ShowWorkingDirectory, SettingsField.MenuMaxHeight,
         SettingsField.HintsBar, SettingsField.StatusBarItems, SettingsField.ToolbarItems, SettingsField.ShowPerformanceBar, SettingsField.ShowHomeBar, SettingsField.BarOrder,
         SettingsField.DraftEditor, SettingsField.ImageEditor],
        [SettingsField.SessionLogging, SettingsField.SessionSaveThinking, SettingsField.SessionRetentionDays,
         SettingsField.SessionNamingMode, SettingsField.SessionShowName,
         SettingsField.SessionTool, SettingsField.SessionSearchMaxResults],
        [SettingsField.BotChatLlmMode, SettingsField.BotChatMultiEmbedded, SettingsField.BotChatMultiEmbeddedKill, SettingsField.BotChatComfy, SettingsField.BotChatLimitedComfyWorkflows, SettingsField.BotChatImageMode, SettingsField.BotChatImg2ImgMode, SettingsField.BotChatImageMentionMode, SettingsField.BotChatImageAsync, SettingsField.BotChatNonTtsDelaySeconds, SettingsField.BotChatTools, SettingsField.BotChatLimitedTools, SettingsField.BotChatSkills, SettingsField.BotChatLimitedSkills, SettingsField.BotChatMemory, SettingsField.BotChatMemoryMode, SettingsField.BotChatVision, SettingsField.BotChatCamera, SettingsField.BotChatRecentContext],
        [SettingsField.LlmUrl, SettingsField.LlmModel,
         SettingsField.LlmReasoning, SettingsField.LlmShowThinking,
         SettingsField.LlmOfferTools, SettingsField.LlmMaxToolIterations, SettingsField.LlmRequestTimeoutSeconds, SettingsField.LlmTurnTimeoutSeconds, SettingsField.PlanGapPasses,
         SettingsField.LlmContextLength, SettingsField.LlmMidTurnUsage, SettingsField.LlmMaxTurns, SettingsField.LlmAutoCompactPercent, SettingsField.LlmCompactType, SettingsField.LlmCompactKeepRecent, SettingsField.LlmCompactShowSummary, SettingsField.LlmToolCompactType,
         SettingsField.LlmPictureKeep, SettingsField.LlmPictureMegabytes, SettingsField.LlmUseFunVerbs],
        [SettingsField.LlmScanMode, SettingsField.LlmApiKey,
         SettingsField.LlmPreserveThinking, SettingsField.LlmReasoningEstimate,
         SettingsField.LlmSampling, SettingsField.LlmSamplingFromHuggingFace],
        [SettingsField.EmbeddedLlmServer, SettingsField.EmbeddedModels, SettingsField.EmbeddedHfDownloadType, SettingsField.EmbeddedBackend, SettingsField.EmbeddedContextSize, SettingsField.EmbeddedGpuLayers, SettingsField.EmbeddedVramBudget, SettingsField.EmbeddedVramOnly, SettingsField.EmbeddedVision, SettingsField.EmbeddedDrafter],
        [SettingsField.DockerServers, SettingsField.DockerServerContainers, SettingsField.DockerServerMatchWords, SettingsField.DockerServerStopTimeoutSeconds, SettingsField.DockerServerPostStopDelaySeconds, SettingsField.DockerServerReadyTimeoutSeconds, SettingsField.DockerServerStopOnExit],
        [SettingsField.ClaudeCodeExecutable, SettingsField.ClaudeCodeServer, SettingsField.ClaudeCodePermissions, SettingsField.ClaudeCodeModel, SettingsField.ClaudeCodeEffort,
         SettingsField.AnthropicApi, SettingsField.AnthropicApiKey, SettingsField.AnthropicApiMaxTokens, SettingsField.AnthropicApiPromptCaching],
        [SettingsField.OpenAIApi, SettingsField.OpenAIApiKey, SettingsField.OpenAIApiMaxTokens, SettingsField.OpenAIApiOrganization, SettingsField.OpenAIApiProject],
        [SettingsField.Advisors, SettingsField.AdvisorList,
         SettingsField.AdvisorContext, SettingsField.AdvisorCallsPerTurn, SettingsField.AdvisorMaxToolIterations, SettingsField.AdvisorConfirm,
         SettingsField.AdvisorThreadPairs, SettingsField.AdvisorEmbedded],
        [SettingsField.TtsOutput, SettingsField.TtsSource, SettingsField.TtsHttpUrl, SettingsField.TtsVoicePreview, SettingsField.TtsVoicePreset, SettingsField.TtsVoice, SettingsField.TtsVoice2, SettingsField.TtsVoiceMix, SettingsField.TtsSpeed],
        [SettingsField.SttInput, SettingsField.SttDestination, SettingsField.SttPushToTalkKey, SettingsField.SttWhisperModel,
         SettingsField.SttWake, SettingsField.SttWakePhrase, SettingsField.SttVoskModel,
         SettingsField.SttInterrupt, SettingsField.SttInterruptEchoGuard, SettingsField.SttInterruptConfirmMs],
    ];

    /// <summary>
    /// The rows of <c>/skills</c>' Options tab (2026-09-19, moved off <c>/settings</c> — its Skills tab, third since 2026-09-18, last from
    /// 2026-09-16 until then — the user's call, right after the Ask, Files and Web move), two lists since later on 2026-09-19 (the user's
    /// ask), indexed by <c>SkillsMenu</c>'s tab one down: the Options tab — the skills switch, the external-folder and project-file switches
    /// it governs (the latter 2026-10-01, the user's ask: off the Project tab, which went), the compact mode, then the # list (2026-09-17; the skill slash commands sat beside it until later on 2026-09-18), the delete switch
    /// (2026-09-18, the pane's scope picker) — and the Reflection tab — the reflection's eight rows (2026-09-17: the auto-learn switch, its reasoning level,
    /// window, min calls and max requests; the cooldown, its mode and the sessions switch, 2026-09-19; the verbose switch, 2026-09-17, went later still on 2026-09-19). Edited through <see cref="FieldsTab"/> / <see cref="EditAsync"/>
    /// under the <c>/skills</c> strip (<see cref="SkillsMenu"/>), the pickers titled <c>Skills › …</c> (<see cref="Root"/>); none of
    /// them is <see cref="RefusedMidTurn"/>. With <see cref="TabFields"/> and <see cref="ToolsTabFields"/> they are every
    /// <see cref="SettingsField"/> once (pinned); the flat no-pane list keeps them all. Both headed since 2026-10-10 (<see cref="SkillsMenuSections"/>, the user's ask),
    /// the sessions switch moved up under the reflection's window rows.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<SettingsField>> SkillsTabFields =
    [
        [SettingsField.AgentSkills, SettingsField.GlobalSkills, SettingsField.ExternalSkills, SettingsField.ProjectFile, SettingsField.SkillCompactMode, SettingsField.SkillHashMention],
        [SettingsField.ReflectionAutoLearn, SettingsField.ReflectionReasoning, SettingsField.ReflectionWindow, SettingsField.ReflectionMinToolCalls, SettingsField.ReflectionMaxRequests, SettingsField.ReflectionIncludesSessions, SettingsField.ReflectionCooldownMinutes, SettingsField.ReflectionCooldownMode, SettingsField.ReflectionYieldsToTurns, SettingsField.ReflectionConfirmMode, SettingsField.ReflectionEditsSupportingFiles, SettingsField.ReflectionInstalledSkills],
    ];

    /// <summary>
    /// The rows of <c>/tools</c>' settings menus (2026-09-19, the Ask, Files and Web rows moved off <c>/settings</c> the user's call), titled by
    /// <see cref="ToolsText.MenuTitles"/> and put on the strip by <see cref="ToolsText.TabMenus"/> since 2026-10-09 (the user's call: the five
    /// database lists on one Database tab, the five media lists on one Media tab); each a tab of its own until then, indexed by
    /// <see cref="ToolsText.TabTitles"/> one down (Ask, Web, Shell, Files, UNC, Print, Camera, Obsidian, SQL, MySQL, Oracle, Claude, Docker, HA, ComfyUI, GitLib, Options — the user's order since 2026-10-03; before it the Home Assistant list second to last, before Options, since later on 2026-10-01, the user's ask; after Claude's before)
    /// (Web, Files, Shell, Ask, Claude, Obsidian, ComfyUI, SQL, Git (native), Options — the user's order since 2026-09-27; Web, Files, Shell, Ask, Git (native), Obsidian, SQL, ComfyUI, Claude, Options before; alphabetical before 2026-09-21): Options (later on 2026-09-19) is the <c>$</c>-mention switch, and under it the tool-run fold (<c>Tool collapse count</c>, 2026-09-22, the user's place);
    /// Ask (2026-09-15) is the question tool's switch and its two caps;
    /// Files (2026-09-15) is the file-tools switch (the Safe edits switch under it from 2026-09-17 until 2026-10-01, when File safe edits
    /// went, the user's call), the two <c>/tree</c> rows (once General's last two), the @-mention folder mode
    /// (General's until 2026-09-17), the <c>/cwd browse</c> mode under it (2026-09-21) and the <c>view_image</c> cap last (2026-09-19); Web is the seven web rows (2026-09-15, once on General under Memory; the tab read Browser
    /// until later that day), the search method above the Web SearXNG URL it governs. The group switch stays each tab's first row.
    /// Since later still on 2026-09-19 (the user's ask) every Files and Web row carries its tab's word (<c>File /tree max length</c>, <c>Web SearXNG URL</c>, …) and the six JSON keys that
    /// differed followed (<c>FileTreeMaxLength</c>, <c>FileTreeShowSizes</c>, <c>FileMentionFolderMode</c>, <c>FileViewImageMaxPerCall</c>, <c>WebSearxngUrl</c>) — no migration, the old key skipped on load.
    /// Git (2026-09-20; Git (native) from 2026-09-21, its rows <c>Git native …</c>; GitLib since 2026-09-30, the tab and its rows) is its switch, the diff cap, the log cap and the identity pair;
    /// Shell (2026-09-21) is the policy (its switch), the allowed list, the outside-paths police (2026-09-22), the default shell, the two timeouts and the output cap,
    /// then the script rows: the languages, their timeout, the tool bridge switch (later that day) and the tool-call cap it governs.
    /// Claude (2026-09-27, the user's call: <c>/claude</c>'s four rows off <c>/settings</c>, named <c>Claude command …</c>) is those four, then
    /// the advisor's six (<c>claude_code_advisor</c>): its switch, the context, the per-turn cap, the model, the effort and the confirm switch;
    /// from 2026-09-29 (the user's call: one tab named <c>Claude</c>, <c>Claude (CLI)</c> until then) the Anthropic API's four rows and
    /// later the Claude Code server's followed, until 2026-10-03 (the user's call), when the five went to <c>/settings</c>' Anthropic tab.
    /// On 2026-10-10 (the user's call) the advisor's six were retired with <c>claude_code_advisor</c> and the four <c>/claude</c> rows joined
    /// them there, so the tab went.
    /// Image (2026-10-10, the user's ask, after File) is the File list's last five until then: <c>image_edit</c>'s quality, metadata, the edit mode, its output folder and the pasted pictures' folder.
    /// Since later still on 2026-10-10 each list is headed (<see cref="ToolsMenuSections"/>, the user's ask), YouTube's autoplay and voice moved after its search rows to sit under Playing.
    /// With <see cref="TabFields"/> and <see cref="SkillsTabFields"/> they are every <see cref="SettingsField"/> once (pinned); the flat no-pane list keeps them all.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<SettingsField>> ToolsTabFields =
    [
        [SettingsField.AskUser, SettingsField.AskMaxQuestions, SettingsField.AskMaxChoices],
        [SettingsField.WebTools, SettingsField.WebBrowserMode, SettingsField.WebBrowserPath, SettingsField.WebBrowserBotcheckWindow, SettingsField.WebBrowserHomePage, SettingsField.BrowserPrivacy, SettingsField.WebOpenLinksIn, SettingsField.WebBrowserNetworkMode, SettingsField.WebSearchMethod, SettingsField.WebSearxngUrl, SettingsField.WebSearchMaxResults, SettingsField.WebDownloadMaxMegabytes],
        [SettingsField.ShellCommandPolicy, SettingsField.ShellCommandAllowed, SettingsField.ShellPolice, SettingsField.ShellPoliceForbiddenStrings, SettingsField.ShellPreferNative, SettingsField.ShellDefault, SettingsField.ShellTimeoutSeconds, SettingsField.ShellForegroundCapSeconds, SettingsField.ShellOutputMaxChars, SettingsField.ShellCodeLanguages, SettingsField.ShellCodeTimeoutSeconds, SettingsField.ShellToolBridge, SettingsField.ShellCodeMaxToolCalls],
        [SettingsField.FileTools, SettingsField.FileTreeMaxLength, SettingsField.FileTreeShowSizes, SettingsField.FileMentionFolderMode, SettingsField.FileBrowserMode, SettingsField.FileViewImageMaxPerCall, SettingsField.FileSearchMaxResults],
        [SettingsField.ImageTools, SettingsField.ImageEditQuality, SettingsField.ImageEditMetadata, SettingsField.ImageEditMode, SettingsField.ImageEditOutputFolder, SettingsField.PastedImagesFolder],
        [SettingsField.UncTools, SettingsField.UncWrites, SettingsField.UncSharesOffered, SettingsField.UncSetPassword, SettingsField.UncAddShare, SettingsField.UncStarMention, SettingsField.UncSharesProfile, SettingsField.UncSharesGlobal],
        [SettingsField.PrintTools, SettingsField.PrintActionPolicy, SettingsField.PrintDefaultPrinter, SettingsField.PrintFontSize, SettingsField.PdfEngine],
        [SettingsField.CameraTools, SettingsField.CameraShutter, SettingsField.CameraPreview, SettingsField.CameraDevice, SettingsField.CameraResolution, SettingsField.CameraOutputFolder, SettingsField.CameraKeepInSessions, SettingsField.CameraWatchSeconds, SettingsField.CameraWatchThreshold, SettingsField.CameraWatchUnprompted, SettingsField.CameraWatchMinGapSeconds],
        [SettingsField.ScreenTools, SettingsField.ScreenAsk, SettingsField.ScreenPreview, SettingsField.ScreenOutputFolder, SettingsField.ScreenKeepInSessions],
        [SettingsField.ObsidianTools, SettingsField.ObsidianVault, SettingsField.ObsidianAllowDelete],
        [SettingsField.SqlTools, SettingsField.SqlMode, SettingsField.SqlStatementsAllowed, SettingsField.SqlConnectionsOffered, SettingsField.SqlSetPassword, SettingsField.SqlAddConnection, SettingsField.SqlPercentMention, SettingsField.SqlQueryMaxRows, SettingsField.SqlQueryTimeoutSeconds, SettingsField.QueryResultMaxChars, SettingsField.SqlConnectionsProfile, SettingsField.SqlConnectionsGlobal],
        [SettingsField.MySqlTools, SettingsField.MySqlMode, SettingsField.MySqlStatementsAllowed, SettingsField.MySqlConnectionsOffered, SettingsField.MySqlSetPassword, SettingsField.MySqlAddConnection, SettingsField.MySqlPercentMention, SettingsField.MySqlQueryMaxRows, SettingsField.MySqlQueryTimeoutSeconds, SettingsField.MySqlConnectionsProfile, SettingsField.MySqlConnectionsGlobal],
        [SettingsField.SqliteTools, SettingsField.SqliteMode, SettingsField.SqliteStatementsAllowed, SettingsField.SqliteDatabasesOffered, SettingsField.SqliteSandboxFiles, SettingsField.SqliteAddDatabase, SettingsField.SqlitePercentMention, SettingsField.SqliteQueryMaxRows, SettingsField.SqliteQueryTimeoutSeconds, SettingsField.SqliteDatabasesProfile, SettingsField.SqliteDatabasesGlobal],
        [SettingsField.PostgresTools, SettingsField.PostgresMode, SettingsField.PostgresStatementsAllowed, SettingsField.PostgresConnectionsOffered, SettingsField.PostgresSetPassword, SettingsField.PostgresAddConnection, SettingsField.PostgresPercentMention, SettingsField.PostgresQueryMaxRows, SettingsField.PostgresQueryTimeoutSeconds, SettingsField.PostgresConnectionsProfile, SettingsField.PostgresConnectionsGlobal],
        [SettingsField.OracleTools, SettingsField.OracleMode, SettingsField.OracleStatementsAllowed, SettingsField.OracleConnectionsOffered, SettingsField.OracleSetPassword, SettingsField.OracleAddConnection, SettingsField.OraclePercentMention, SettingsField.OracleQueryMaxRows, SettingsField.OracleQueryTimeoutSeconds, SettingsField.OracleConnectionsProfile, SettingsField.OracleConnectionsGlobal],
        [SettingsField.DockerTools, SettingsField.DockerWrites, SettingsField.DockerEnginePipe],
        [SettingsField.HomeAssistantTools, SettingsField.HomeAssistantUrl, SettingsField.HomeAssistantToken, SettingsField.HomeAssistantTest, SettingsField.HomeAssistantActionPolicy, SettingsField.HomeAssistantAssistAgent, SettingsField.HomeAssistantTimeoutSeconds],
        [SettingsField.ComfyTools, SettingsField.ComfyUrl, SettingsField.ComfyWorkflowsOffered, SettingsField.ComfyAddWorkflow, SettingsField.ComfyCaretMention, SettingsField.ComfyTimeoutSeconds, SettingsField.ComfyMaxPicturesPerCall, SettingsField.ComfyReinforceNegatives, SettingsField.ComfyShowPrompts, SettingsField.ComfyPictureStrip, SettingsField.ComfyOutputFolder],
        [SettingsField.YouTubeTools, SettingsField.YouTubeApiKey, SettingsField.YouTubeSearchMaxResults, SettingsField.YouTubeSearchOrder, SettingsField.YouTubeSafeSearch, SettingsField.YouTubeRegion, SettingsField.YouTubeLanguage, SettingsField.YouTubeAutoplay, SettingsField.YouTubeVoice],
        [SettingsField.GitLibTools, SettingsField.GitLibDiffMaxLines, SettingsField.GitLibLogMaxCommits, SettingsField.GitLibEmail, SettingsField.GitLibName],
        [SettingsField.ToolsDollarMention, SettingsField.ToolCollapseCount, SettingsField.CodeCollapseCount, SettingsField.ShowFileDiffs, SettingsField.DiffMaxLines, SettingsField.DiffCollapseCount],
        [SettingsField.ShoppingTools, SettingsField.ShoppingList, SettingsField.ShoppingStores],
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
    private readonly Func<Oracle.OracleNamedConnection, CancellationToken, Task<Sql.SqlRun>> _testOracleConnection;
    private readonly Func<MySql.MySqlNamedConnection, CancellationToken, Task<Sql.SqlRun>> _testMySqlConnection;
    private readonly Func<Unc.UncNamedShare, CancellationToken, Task<Unc.UncResult<int>>> _testUncShare;
    private readonly Func<Comfy.ComfyClient?> _comfyClient;
    private readonly Func<CancellationToken, Task<(bool Ok, string Text)>> _testHomeAssistant;
    private readonly Func<IReadOnlyList<Printing.PrinterInfo>> _printers;
    // The skills a botchat sees, for the limited-skills checklist (2026-09-27); none when the host gives no catalog.
    private readonly Func<IReadOnlyList<Skills.Skill>> _botChatSkills;
    // The main chat's tool groups less memory and skills, for the limited-tools checklist (2026-10-04); none when the host gives none.
    private readonly Func<IReadOnlyList<ToolGroup>> _botChatTools;
    private readonly Func<string, string?> _locateBrowser;
    private readonly Func<IReadOnlySet<string>> _installedShells;
    private readonly Func<IReadOnlySet<string>> _installedLanguages;
    // Set for the visit of a /settings opened while a reply runs (ShowAsync's midTurn): the rows
    // that would reconnect, switch the profile, move the sandbox or reshape the history answer
    // NotWhileReplyRunsNotice instead, and no voice preview plays over the reply's speech.
    // Since 2026-10-12 (the user's ask) asked live (ReplyRun): a pane the reply ended under opens those rows where it stands.
    private ReplyRun _reply;

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
    /// <param name="testOracleConnection">What the <c>Oracle add connection</c> summary's test runs over the unsaved draft (2026-09-30), its typed password in it as a plain <c>file</c> value; null = a real <see cref="Oracle.OracleAccess"/> run (<see cref="TestOracleConnectionAsync"/>).</param>
    /// <param name="testMySqlConnection">What the <c>MySQL add connection</c> summary's test runs over the unsaved draft (2026-09-30); null = a real <see cref="MySql.MySqlAccess"/> run (<see cref="TestMySqlConnectionAsync"/>).</param>
    /// <param name="testUncShare">What the <c>UNC add share</c> summary's test runs over the unsaved draft (2026-09-30): how many entries the root lists under the draft's account; null = a real <see cref="Unc.UncAccess"/> run (<see cref="TestUncShareAsync"/>). It never writes.</param>
    /// <param name="openFile">What the SQL tab's edit rows open <c>sql.json</c> with (2026-09-23): the screen's editor opener; null = the rows say there is none.</param>
    /// <param name="browseFolder">The folder picker the <c>Working directory (cwd)</c> row opens (2026-09-22, the user's ask): the screen's <c>/cwd browse</c> tree, returning what to save — <c>""</c> for the profile's folder, a full path, or null for nothing chosen. Null (and a console with no pane) falls back to the typed path the row asked for until then.</param>
    public SettingsMenu(IAnsiConsole console, AppSettings settings, Func<SettingsField, string?> overriddenBy, InputLine input, TranscriptRenderer transcript, SpeechSession speech, MenuPane pane, Func<string, string?>? locateBrowser = null, Func<IReadOnlySet<string>>? installedShells = null, Func<IReadOnlySet<string>>? installedLanguages = null, Func<CancellationToken, Task<string?>>? browseFolder = null, Func<string, CancellationToken, Task<string?>>? browseVault = null, Action<string>? openFile = null, Func<Sql.SqlNamedConnection, CancellationToken, Task<Sql.SqlRun>>? testSqlConnection = null, Func<Comfy.ComfyClient?>? comfyClient = null, Func<IReadOnlyList<Skills.Skill>>? botChatSkills = null, Func<CancellationToken, Task<(bool Ok, string Text)>>? testHomeAssistant = null, Func<IReadOnlyList<Printing.PrinterInfo>>? printers = null, Func<Oracle.OracleNamedConnection, CancellationToken, Task<Sql.SqlRun>>? testOracleConnection = null, Func<MySql.MySqlNamedConnection, CancellationToken, Task<Sql.SqlRun>>? testMySqlConnection = null, Func<Unc.UncNamedShare, CancellationToken, Task<Unc.UncResult<int>>>? testUncShare = null, Func<IReadOnlyList<ToolGroup>>? botChatTools = null)
    {
        // Print default printer's picker (2026-09-28): the screen's spooler in the app; none otherwise, so a test never lists the machine's.
        _printers = printers ?? (() => []);
        // The ComfyUI client the add-workflow wizard lists the server's models and runs its test with (later on 2026-09-24): the screen's, so a stub reaches it in tests; null = none, the wizard says there is no server.
        _comfyClient = comfyClient ?? (() => null);
        // Home Assistant test connection (2026-09-28): the screen's session in the app; a session over the saved settings otherwise.
        _testHomeAssistant = testHomeAssistant ?? (token => HomeAssistant.HaSession.TestAsync(() => settings.Current, token));
        _botChatSkills = botChatSkills ?? (() => []);
        _botChatTools = botChatTools ?? (() => []);
        _testSqlConnection = testSqlConnection ?? TestSqlConnectionAsync;
        _testOracleConnection = testOracleConnection ?? TestOracleConnectionAsync;
        _testMySqlConnection = testMySqlConnection ?? TestMySqlConnectionAsync;
        _testUncShare = testUncShare ?? TestUncShareAsync;
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

    /// <summary>
    /// What Enter on the <c>LLM sampling</c> row opens (2026-09-28): the screen's <see cref="SamplingMenu.ShowAsync"/>, set
    /// once the screen has built it; null (a test's menu) opens nothing.
    /// </summary>
    public Func<CancellationToken, Task>? SamplingPane { get; set; }

    /// <summary>
    /// The embedded model's catalog and installs (2026-09-29), set by the screen: the Embedded model tab's live rows and the
    /// catalog picker read it. Null offers the picker nothing (headless, tests without one).
    /// </summary>
    public NeonSidekick.EmbeddedLlm.IEmbeddedLlm? EmbeddedLlm { get; set; }

    /// <summary>The effective settings (saved plus this launch's overrides), set by the screen; the saved ones when unset. What the embedded rows resolve the backend under.</summary>
    public Func<AppSettingsData>? Effective { get; set; }

    private AppSettingsData EffectiveNow() => Effective?.Invoke() ?? _settings.Current;

    /// <summary>
    /// Run before the catalog removes a model (2026-09-29, the user's ask), set by the screen: a download of that model under
    /// way is stopped and awaited, so no file is still open when its folder goes. Null does nothing (tests without one).
    /// </summary>
    public Func<NeonSidekick.EmbeddedLlm.EmbeddedModel, CancellationToken, Task>? BeforeEmbeddedRemove { get; set; }

    /// <summary>Set when a removal cleared the saved LLM URL and model (the model in use went): <see cref="ShowAsync(CancellationToken, bool)"/> then reports an LLM change.</summary>
    private bool _embeddedLlmCleared;

    /// <summary>The model a use or an install on the catalog picker chose, for the screen to download and connect once the pane is closed.</summary>
    private NeonSidekick.EmbeddedLlm.EmbeddedModel? _pendingEmbeddedModel;

    /// <summary>The model the catalog picker handed over (and forgets it), or null: the screen installs it when need be and connects to it.</summary>
    public NeonSidekick.EmbeddedLlm.EmbeddedModel? TakePendingEmbeddedModel()
    {
        var pending = _pendingEmbeddedModel;
        _pendingEmbeddedModel = null;
        return pending;
    }

    /// <summary>The <c>Embedded models</c> row with no embedded model offered, or before the catalog is read. Pinned.</summary>
    public const string EmbeddedModelsDoorLabel = "(Enter to install, use or remove)";

    /// <summary>
    /// How a <c>Embedded context size</c> of 0 shows: the largest context the VRAM budget holds, up to the model's own window
    /// ("the model's own" until later on 2026-09-29, when the user asked for the context to shrink to a budget). Pinned.
    /// </summary>
    public const string EmbeddedContextFitLabel = "fit";

    /// <summary>The catalog picker's title and keys (2026-09-29).</summary>
    public const string EmbeddedModelsKeys = "Enter = choose · " + NeonSidekick.EmbeddedLlm.EmbeddedModelFilter.CatalogKeys + " · ESC = back";
    public const string UseNowRow = "Use now";
    public const string BackRow = "Back";

    /// <summary><see cref="Breadcrumb"/> under <see cref="Root"/>.</summary>
    private string Crumb(string label) => Root + " › " + label;

    /// <summary>Whether a change to <paramref name="field"/> needs the LLM session rebuilt.</summary>
    public static bool IsLlmField(SettingsField field) =>
        field is SettingsField.LlmUrl or SettingsField.LlmModel or SettingsField.LlmApiKey
            or SettingsField.LlmRequestTimeoutSeconds or SettingsField.LlmTurnTimeoutSeconds or SettingsField.LlmContextLength or SettingsField.LlmReasoning
            or SettingsField.AnthropicApi or SettingsField.AnthropicApiKey or SettingsField.AnthropicApiMaxTokens or SettingsField.AnthropicApiPromptCaching or SettingsField.ClaudeCodeServer
            or SettingsField.OpenAIApi or SettingsField.OpenAIApiKey or SettingsField.OpenAIApiMaxTokens or SettingsField.OpenAIApiOrganization or SettingsField.OpenAIApiProject
            or SettingsField.EmbeddedLlmServer or SettingsField.EmbeddedModels or SettingsField.EmbeddedBackend or SettingsField.EmbeddedContextSize or SettingsField.EmbeddedGpuLayers or SettingsField.EmbeddedVramBudget or SettingsField.EmbeddedVision
            or SettingsField.EmbeddedDrafter or SettingsField.EmbeddedVramOnly or SettingsField.DockerServers;

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
    /// F9 and F10 went on 2026-10-05 (the user's pick): they are the <c>/camera snap</c> and <c>/screen</c> chords
    /// (<see cref="Keys.ShortcutLine"/>), and a profile that saved either falls back to F4 with the warning.
    /// </summary>
    public static readonly ConsoleKey[] PushToTalkKeys =
    {
        ConsoleKey.F1, ConsoleKey.F2, ConsoleKey.F3, ConsoleKey.F4, ConsoleKey.F5,
        ConsoleKey.F6, ConsoleKey.F7, ConsoleKey.F8,
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
    public static int LabelWidthOf(IReadOnlyList<SettingsField> fields, string? tabTitle = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return fields.Max(f => PaneName(f, tabTitle is null ? FieldName(f) : RowName(f, tabTitle)).Length) + LabelGap;
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

    /// <summary>The picker's name column: the longest server name, <see cref="Llm.Anthropic.ClaudeApi.ServerName"/> ("Anthropic API", since 2026-10-04), plus two; 11 until then, <see cref="LlmServer.PortNames"/>' "LM Studio" / "llama.cpp" plus two.</summary>
    public const int ServerNameWidth = 15;

    /// <summary>A server picker row on its own: the name padded, the URL in ink, the probe's detail dimmed. Pinned.</summary>
    public static string ServerLabel(LlmServer server) => ServerLabel(server, whereWidth: 0, detailWidth: 0);

    /// <summary>
    /// A server picker row whose URL (or, for an embedded model, the model's name) is padded to
    /// <paramref name="whereWidth"/> cells, so every row's detail starts in one column (2026-09-29, the user's ask: the
    /// embedded rows' names and the servers' URLs made the details ragged), and whose detail is padded to
    /// <paramref name="detailWidth"/> ahead of the capability columns an embedded model carries
    /// (<see cref="NeonSidekick.EmbeddedLlm.EmbeddedLlmText.CapabilityColumns"/>: the drafter, vision and tools marks, later that day, the user's asks). Pinned.
    /// </summary>
    public static string ServerLabel(LlmServer server, int whereWidth, int detailWidth)
    {
        ArgumentNullException.ThrowIfNull(server);
        var (where, quant) = ServerWhereParts(server);
        string pad = new(' ', Math.Max(0, whereWidth - TextCells.Width(where + quant)));
        var model = NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(server.BaseUrl) ? EmbeddedRowModel(server) : null;
        return Markup.Escape(server.Name.PadRight(ServerNameWidth)) + Theme.ColorMarkup(Theme.Ink, Markup.Escape(where))
            + Theme.DimMarkup(Markup.Escape(quant) + pad + "  " + Markup.Escape(server.Result.Detail))
            + Markup.Escape(NeonSidekick.EmbeddedLlm.EmbeddedLlmText.CapabilityColumns(model, server.Result.Detail, detailWidth));
    }

    /// <summary>
    /// The footer under the server picker for <paramref name="server"/> (2026-10-07, the user's ask: the row is the widest of all and
    /// its detail is cut first): <c>vLLM · http://10.0.0.5:8000/v1</c> (an embedded model's whole id after the sentinel), then the
    /// probe's whole detail and how many models it lists. Pinned.
    /// </summary>
    public static MenuFooter ServerFooter(LlmServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var ids = server.Result.ModelIds;
        string where = NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(server.BaseUrl) && ids.Count > 0
            ? server.Name + " · " + ids[0]
            : server.Name + " · " + server.BaseUrl;
        string models = ids.Count switch
        {
            0 => "",
            1 => " · 1 model",
            _ => " · " + ids.Count.ToString(CultureInfo.InvariantCulture) + " models",
        };
        return new MenuFooter(where, server.Result.Detail + models);
    }

    /// <summary>Whether <paramref name="server"/> stays under the typed <paramref name="filter"/>: its name, URL, detail or models hold it (2026-10-07).</summary>
    public static bool ServerMatches(string filter, LlmServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return MenuFilter.Matches(filter, server.Name, server.BaseUrl + " " + server.Result.Detail + " " + string.Join(' ', server.Result.ModelIds));
    }

    /// <summary>The picker's rows with the URL-or-model column as wide as its widest entry, and the details as wide as the widest one.</summary>
    public static IReadOnlyList<string> ServerLabels(IReadOnlyList<LlmServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        int width = servers.Count == 0 ? 0 : servers.Max(s => ServerWhereParts(s) is var (where, quant) ? TextCells.Width(where + quant) : 0);
        int detailWidth = servers.Count == 0 ? 0 : servers.Max(s => TextCells.Width(s.Result.Detail));
        return servers.Select(s => ServerLabel(s, width, detailWidth)).ToList();
    }

    /// <summary>
    /// A row's second column: the server's URL, or an embedded model's name (2026-09-29: the sentinel names no place)
    /// padded to <see cref="EmbeddedModelNameWidth"/> with its quantisation after it, dim, as the catalog shows it (later
    /// that day, the user's call: the 12B's four builds share one name). The quantisation is empty for a URL.
    /// </summary>
    private static (string Where, string Quant) ServerWhereParts(LlmServer server)
    {
        if (NeonSidekick.Docker.DockerEndpoint.ContainerOf(server.BaseUrl) is { } container)
        {
            // A chosen Docker container (2026-10-02): its name, the sentinel naming no place.
            return (NeonSidekick.Docker.DockerServerText.UrlDisplay(container), "");
        }

        if (!NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(server.BaseUrl))
        {
            return (server.BaseUrl.ToString(), "");
        }

        return EmbeddedRowModel(server) is { } model
            ? (model.Display.PadRight(EmbeddedModelNameWidth), model.Quant)
            : (EmbeddedRowId(server), "");
    }

    /// <summary>The list for a console without menus: <c>LLM servers: LM Studio http://127.0.0.1:1234/v1, Ollama http://127.0.0.1:11434/v1</c>. Pinned.</summary>
    public static string ServerListLine(IReadOnlyList<LlmServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        return "LLM servers: " + string.Join(", ", servers.Select(s => s.Name + " " + (NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(s.BaseUrl) ? EmbeddedRowName(s)
            : NeonSidekick.Docker.DockerEndpoint.ContainerOf(s.BaseUrl) is { } container ? NeonSidekick.Docker.DockerServerText.UrlDisplay(container) : s.BaseUrl.ToString())));
    }

    /// <summary>An embedded row's model as one phrase: its display name and quantisation from the catalog, else its id.</summary>
    private static string EmbeddedRowName(LlmServer server) =>
        EmbeddedRowModel(server) is { } model ? model.Display + " " + model.Quant : EmbeddedRowId(server);

    private static NeonSidekick.EmbeddedLlm.EmbeddedModel? EmbeddedRowModel(LlmServer server) => NeonSidekick.EmbeddedLlm.EmbeddedModelCatalog.Find(EmbeddedRowId(server));

    private static string EmbeddedRowId(LlmServer server) => server.Result.ModelIds.Count > 0 ? server.Result.ModelIds[0] : "";

    /// <summary><c>/server &lt;url&gt;</c> with something that is not an absolute http(s) URL. Pinned.</summary>
    public static string ServerUrlError(string detail) => $"Not a usable server URL: {detail}";

    /// <summary><c>/server &lt;url&gt;</c> naming a server that did not answer; it is saved anyway, like a configured URL. Pinned.</summary>
    public static string ServerNotAnsweringWarning(Uri baseUrl, string detail) =>
        $"{baseUrl} did not answer /v1/models ({detail}); using it anyway because you asked.";

    public static string SwitchedNotice(string profileName) => $"({NoticeGlyphs.Profile}switched to profile \"{profileName}\"; conversation cleared)";

    public static bool IsToggle(SettingsField field) =>
        field is SettingsField.TtsOutput or SettingsField.SttInput or SettingsField.SttWake or SettingsField.SttInterrupt
            or SettingsField.CopyUserPrompt or SettingsField.ShowImageThumbnails
            or SettingsField.FileTreeShowSizes or SettingsField.LlmOfferTools or SettingsField.LlmUseFunVerbs or SettingsField.LlmShowThinking or SettingsField.LlmPreserveThinking or SettingsField.LlmSamplingFromHuggingFace
            or SettingsField.WebTools or SettingsField.TtsVoicePreview or SettingsField.FileTools or SettingsField.AskUser
            or SettingsField.AgentSkills or SettingsField.GlobalSkills or SettingsField.ExternalSkills or SettingsField.ProjectFile or SettingsField.TranscriptMarkdown
            or SettingsField.SkillHashMention or SettingsField.ReflectionAutoLearn
            or SettingsField.HideExitAutocomplete or SettingsField.EmojiPicker or SettingsField.CommandTypoIntercept or SettingsField.KeepMessageHistory or SettingsField.ShowHeader or SettingsField.ShowWorkingDirectory or SettingsField.ThemedExternalWindows or SettingsField.ThemedBackground
            or SettingsField.SessionLogging or SettingsField.SessionTool or SettingsField.SessionSaveThinking
            or SettingsField.ToolsDollarMention or SettingsField.ShowFileDiffs or SettingsField.ReflectionIncludesSessions or SettingsField.ReflectionYieldsToTurns or SettingsField.ReflectionEditsSupportingFiles or SettingsField.McpServers or SettingsField.GitLibTools
            or SettingsField.LlmCompactShowSummary or SettingsField.ShellToolBridge or SettingsField.ShellPolice or SettingsField.ShellPreferNative
            or SettingsField.ObsidianTools or SettingsField.ObsidianAllowDelete or SettingsField.SqlTools or SettingsField.SqlPercentMention or SettingsField.ComfyTools or SettingsField.ComfyReinforceNegatives or SettingsField.ComfyShowPrompts or SettingsField.ComfyCaretMention or SettingsField.ComfyPictureStrip
            or SettingsField.BotChatComfy or SettingsField.BotChatImageAsync or SettingsField.BotChatTools or SettingsField.BotChatSkills or SettingsField.BotChatMemory or SettingsField.BotChatVision or SettingsField.BotChatMultiEmbeddedKill
            or SettingsField.AnthropicApi or SettingsField.AnthropicApiPromptCaching or SettingsField.ClaudeCodeServer or SettingsField.OpenAIApi or SettingsField.EmbeddedVision or SettingsField.EmbeddedLlmServer or SettingsField.EmbeddedDrafter or SettingsField.EmbeddedVramOnly
            or SettingsField.HomeAssistantTools or SettingsField.PrintTools or SettingsField.OracleTools or SettingsField.OraclePercentMention or SettingsField.MySqlTools or SettingsField.MySqlPercentMention
            or SettingsField.UncTools or SettingsField.UncWrites or SettingsField.UncStarMention
            or SettingsField.DockerTools or SettingsField.DockerWrites or SettingsField.DockerServers or SettingsField.DockerServerStopOnExit
            or SettingsField.CameraTools or SettingsField.CameraKeepInSessions or SettingsField.CameraWatchUnprompted or SettingsField.BotChatCamera or SettingsField.BotChatRecentContext
            or SettingsField.ScreenTools or SettingsField.ScreenPreview or SettingsField.ScreenKeepInSessions or SettingsField.ImageTools
            or SettingsField.SqliteTools or SettingsField.SqliteSandboxFiles or SettingsField.SqlitePercentMention
            or SettingsField.PostgresTools or SettingsField.PostgresPercentMention
            or SettingsField.YouTubeTools or SettingsField.YouTubeAutoplay
            or SettingsField.Advisors or SettingsField.AdvisorConfirm
            or SettingsField.ShoppingTools;

    public static string FieldName(SettingsField field) => field switch
    {
        SettingsField.Profile => "Profile",
        SettingsField.ModelAvatar => "Model avatar",
        SettingsField.UserAvatar => "User avatar",
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
        SettingsField.SttDestination => "STT destination",
        SettingsField.CameraTools => "Camera tool",
        SettingsField.CameraShutter => "Camera shutter",
        SettingsField.CameraPreview => "Camera preview",
        SettingsField.CameraDevice => "Camera device",
        SettingsField.CameraResolution => "Camera resolution",
        SettingsField.CameraKeepInSessions => "Camera keep in sessions",
        SettingsField.CameraOutputFolder => "Camera output folder",
        SettingsField.ScreenTools => "Screen tools",
        SettingsField.ScreenAsk => "Screen policy",
        SettingsField.ScreenPreview => "Screen preview",
        SettingsField.ScreenOutputFolder => "Screen output folder",
        SettingsField.ScreenKeepInSessions => "Screen keep in sessions",
        SettingsField.CameraWatchSeconds => "Camera watch interval (s)",
        SettingsField.CameraWatchThreshold => "Camera watch change (%)",
        SettingsField.CameraWatchUnprompted => "Camera watch unprompted",
        SettingsField.CameraWatchMinGapSeconds => "Camera watch min gap (s)",
        SettingsField.BotChatCamera => "Botchat camera",
        SettingsField.BotChatRecentContext => "Botchat recent context",
        SettingsField.SttWake => "STT wake",
        SettingsField.SttWakePhrase => "STT wake phrase",
        SettingsField.SttPushToTalkKey => "STT push-to-talk key",
        SettingsField.TtsSpeed => "TTS speed",
        SettingsField.SttWhisperModel => "STT Whisper model",
        SettingsField.SttVoskModel => "STT Vosk model",
        SettingsField.SttInterrupt => "STT interrupt",
        SettingsField.SttInterruptEchoGuard => "STT interrupt echo guard (%)",
        SettingsField.SttInterruptConfirmMs => "STT interrupt hold (ms)",
        SettingsField.LlmReasoning => "LLM reasoning",
        SettingsField.TtsVoice2 => "TTS voice 2",
        SettingsField.TtsVoiceMix => "TTS voice mix (%)",
        SettingsField.MemoryMode => "Memory mode",
        SettingsField.WorkingDirectory => "Working directory (cwd)",
        SettingsField.CopyUserPrompt => "Copy user prompt",
        SettingsField.UserLineStyle => "User line style",
        SettingsField.DraftEditor => "Draft editor",
        SettingsField.ImageEditor => "Image viewer",   // "Image editor" until later still on 2026-09-24 (the user's call); the field and the setting keep the old name
        SettingsField.FileViewImageMaxPerCall => "File view image max images",
        SettingsField.FileSearchMaxResults => "File search max results",
        SettingsField.ImageEditQuality => "Image edit quality",
        SettingsField.ImageEditMetadata => "Image edit metadata",
        SettingsField.ImageEditMode => "Image edit mode",
        SettingsField.ImageEditOutputFolder => "Image edit output folder",
        SettingsField.PastedImagesFolder => "Pasted images folder",
        SettingsField.McpServers => "MCP servers enabled",
        SettingsField.McpConnectTimeoutSeconds => "MCP connect timeout (s)",
        SettingsField.ShowImageThumbnails => "Show image thumbnails",
        SettingsField.LlmCompactType => "LLM compact mode",
        SettingsField.LlmCompactKeepRecent => "LLM compact keep recent",
        SettingsField.LlmCompactShowSummary => "LLM compact show summary",
        SettingsField.LlmAutoCompactPercent => "LLM auto compact (%)",
        SettingsField.LlmMaxTurns => "LLM max turns",
        SettingsField.LlmToolCompactType => "LLM tool compact mode",
        SettingsField.LlmPictureKeep => "LLM max images",
        SettingsField.LlmPictureMegabytes => "LLM max images (MB)",
        SettingsField.LlmMaxToolIterations => "LLM max tool iterations",
        SettingsField.PlanGapPasses => "LLM plan gap passes",
        SettingsField.ImageThumbnailSize => "Image thumbnail size",
        SettingsField.FileTreeMaxLength => "File /tree max entries",
        SettingsField.FileTreeShowSizes => "File /tree show sizes",
        SettingsField.LlmOfferTools => "LLM offer tools",
        SettingsField.LlmUseFunVerbs => "LLM fun verbs",
        SettingsField.LlmShowThinking => "LLM show thinking",
        SettingsField.LlmPreserveThinking => "LLM preserve thinking",
        SettingsField.LlmReasoningEstimate => "LLM reasoning estimate",
        SettingsField.LlmSampling => "LLM sampling",
        SettingsField.LlmSamplingFromHuggingFace => "LLM sampling from Hugging Face",
        SettingsField.SessionSaveThinking => "Session save thinking",
        SettingsField.ClaudeCodeExecutable => "Claude Code executable",
        SettingsField.ClaudeCodePermissions => "Claude Code command permissions",
        SettingsField.ClaudeCodeModel => "Claude Code command model",
        SettingsField.ClaudeCodeEffort => "Claude Code command effort",
        SettingsField.Advisors => "Advisors enabled",
        SettingsField.AdvisorList => "Advisor list",
        SettingsField.AdvisorContext => "Advisor context",
        SettingsField.AdvisorCallsPerTurn => "Advisor max calls per turn",
        SettingsField.AdvisorMaxToolIterations => "Advisor max tool iterations",
        SettingsField.AdvisorThreadPairs => "Advisor thread length",
        SettingsField.AdvisorEmbedded => "Advisor embedded mode",
        SettingsField.AdvisorConfirm => "Advisor confirm",
        SettingsField.LlmScanMode => "LLM server scan mode",
        SettingsField.WebTools => "Web tools",
        SettingsField.GitLibTools => "GitLib tools",
        SettingsField.GitLibDiffMaxLines => "GitLib diff max lines",
        SettingsField.ShellCommandPolicy => "Shell command policy",
        SettingsField.ShellCommandAllowed => "Shell allowed commands",
        SettingsField.ShellPolice => "Shell police",
        SettingsField.ShellPoliceForbiddenStrings => "Shell police forbidden strings",
        SettingsField.ShellPreferNative => "Shell prefer native tools",
        SettingsField.ShellDefault => "Shell default shell",
        SettingsField.ShellTimeoutSeconds => "Shell default timeout (s)",
        SettingsField.ShellForegroundCapSeconds => "Shell max timeout (s)",
        SettingsField.ShellOutputMaxChars => "Shell output max chars",
        SettingsField.ShellCodeLanguages => "Shell code languages",
        SettingsField.ShellCodeTimeoutSeconds => "Shell code default timeout (s)",
        SettingsField.ShellToolBridge => "Shell tool bridge",
        SettingsField.ShellCodeMaxToolCalls => "Shell tool bridge max calls",   // the bridge's cap, named after the switch above it (later on 2026-09-21, the user's ask; "Shell code max tool calls" before)
        SettingsField.GitLibLogMaxCommits => "GitLib log max commits",
        SettingsField.GitLibEmail => "GitLib user email",
        SettingsField.GitLibName => "GitLib user name",
        SettingsField.ObsidianTools => "Obsidian tools",
        SettingsField.ObsidianVault => "Obsidian vault",
        SettingsField.SqlTools => "SQL tools",
        SettingsField.SqlMode => "SQL mode",
        SettingsField.SqlStatementsAllowed => "SQL statements allowed",
        SettingsField.ComfyTools => "ComfyUI tools",
        SettingsField.HomeAssistantTools => "Home Assistant tools",
        SettingsField.YouTubeTools => "YouTube tools",
        SettingsField.YouTubeApiKey => "YouTube API key",
        SettingsField.YouTubeSearchMaxResults => "YouTube search max results",
        SettingsField.YouTubeAutoplay => "YouTube autoplay",
        SettingsField.YouTubeVoice => "YouTube while speaking",
        SettingsField.YouTubeSearchOrder => "YouTube search order",
        SettingsField.YouTubeSafeSearch => "YouTube safe search",
        SettingsField.YouTubeRegion => "YouTube region",
        SettingsField.YouTubeLanguage => "YouTube language",
        SettingsField.HomeAssistantUrl => "Home Assistant URL",
        SettingsField.HomeAssistantToken => "Home Assistant API key",
        SettingsField.HomeAssistantTest => "Home Assistant test connection",
        SettingsField.HomeAssistantActionPolicy => "Home Assistant action policy",
        SettingsField.HomeAssistantAssistAgent => "Home Assistant Assist agent",
        SettingsField.HomeAssistantTimeoutSeconds => "Home Assistant timeout (s)",
        SettingsField.PrintTools => "Print tools",
        SettingsField.PrintActionPolicy => "Print action policy",
        SettingsField.PrintDefaultPrinter => "Print default printer",
        SettingsField.PrintFontSize => "Print font size (pt)",
        SettingsField.PdfEngine => "PDF engine",
        SettingsField.ComfyUrl => "ComfyUI URL",
        SettingsField.ComfyTimeoutSeconds => "ComfyUI timeout (s)",
        SettingsField.ComfyMaxPicturesPerCall => "ComfyUI max images per call",
        SettingsField.ComfyReinforceNegatives => "ComfyUI reinforce negatives",
        SettingsField.ComfyShowPrompts => "ComfyUI show prompts",
        SettingsField.ComfyCaretMention => "ComfyUI ^-mention enabled",
        SettingsField.ComfyPictureStrip => "ComfyUI picture strip",
        SettingsField.BotChatLlmMode => "Botchat LLM mode",
        SettingsField.BotChatMultiEmbedded => "Botchat embedded mode",
        SettingsField.BotChatMultiEmbeddedKill => "Botchat stop extra servers",
        SettingsField.LlmMidTurnUsage => "LLM mid-turn usage",
        SettingsField.BotChatComfy => "Botchat ComfyUI enabled",
        SettingsField.BotChatLimitedComfyWorkflows => "Botchat ComfyUI limited workflows",
        SettingsField.BotChatImageMode => "Botchat image mode",
        SettingsField.BotChatImg2ImgMode => "Botchat rework source",
        SettingsField.BotChatImageMentionMode => "Botchat image mention mode",   // "Image mention mode" on the Botchat tab (RowName)
        SettingsField.BotChatImageAsync => "Botchat image async",
        SettingsField.BotChatNonTtsDelaySeconds => "Botchat reading pause (s)",
        SettingsField.BotChatTools => "Botchat tools enabled",
        SettingsField.BotChatLimitedTools => "Botchat limited tools",
        SettingsField.BotChatSkills => "Botchat skills enabled",
        SettingsField.BotChatVision => "Botchat vision enabled",
        SettingsField.BotChatLimitedSkills => "Botchat limited skills",
        SettingsField.BotChatMemory => "Botchat memory enabled",
        SettingsField.BotChatMemoryMode => "Botchat memory mode",
        SettingsField.ComfyOutputFolder => "ComfyUI output folder",
        SettingsField.ComfyWorkflowsOffered => "ComfyUI workflows offered",
        SettingsField.ComfyAddWorkflow => "ComfyUI add workflow",
        SettingsField.SqlConnectionsOffered => "SQL connections offered",
        SettingsField.SqlSetPassword => "SQL set password",
        SettingsField.SqlAddConnection => "SQL add/edit connection",   // "add" alone until 2026-10-05, when the wizards took to editing
        SettingsField.SqlPercentMention => "SQL %-mention enabled",
        SettingsField.SqlQueryMaxRows => "SQL max rows",
        SettingsField.SqlQueryTimeoutSeconds => "SQL query timeout (s)",
        SettingsField.QueryResultMaxChars => "SQL query result max chars",
        SettingsField.SqlConnectionsProfile => "SQL connections (profile)",
        SettingsField.SqlConnectionsGlobal => "SQL connections (global)",
        SettingsField.OracleTools => "Oracle tools",
        SettingsField.OracleMode => "Oracle mode",
        SettingsField.OracleStatementsAllowed => "Oracle statements allowed",
        SettingsField.OracleConnectionsOffered => "Oracle connections offered",
        SettingsField.OracleSetPassword => "Oracle set password",
        SettingsField.OracleAddConnection => "Oracle add/edit connection",
        SettingsField.OraclePercentMention => "Oracle %-mention enabled",
        SettingsField.OracleQueryMaxRows => "Oracle max rows",
        SettingsField.OracleQueryTimeoutSeconds => "Oracle query timeout (s)",
        SettingsField.OracleConnectionsProfile => "Oracle connections (profile)",
        SettingsField.OracleConnectionsGlobal => "Oracle connections (global)",
        SettingsField.MySqlTools => "MySQL tools",
        SettingsField.MySqlMode => "MySQL mode",
        SettingsField.MySqlStatementsAllowed => "MySQL statements allowed",
        SettingsField.MySqlConnectionsOffered => "MySQL connections offered",
        SettingsField.MySqlSetPassword => "MySQL set password",
        SettingsField.MySqlAddConnection => "MySQL add/edit connection",
        SettingsField.MySqlPercentMention => "MySQL %-mention enabled",
        SettingsField.MySqlQueryMaxRows => "MySQL max rows",
        SettingsField.MySqlQueryTimeoutSeconds => "MySQL query timeout (s)",
        SettingsField.MySqlConnectionsProfile => "MySQL connections (profile)",
        SettingsField.MySqlConnectionsGlobal => "MySQL connections (global)",
        SettingsField.SqliteTools => "SQLite tools",
        SettingsField.SqliteMode => "SQLite mode",
        SettingsField.SqliteStatementsAllowed => "SQLite statements allowed",
        SettingsField.SqliteDatabasesOffered => "SQLite databases offered",
        SettingsField.SqliteSandboxFiles => "SQLite sandbox files",
        SettingsField.SqliteAddDatabase => "SQLite add/edit database",
        SettingsField.SqlitePercentMention => "SQLite %-mention enabled",
        SettingsField.SqliteQueryMaxRows => "SQLite max rows",
        SettingsField.SqliteQueryTimeoutSeconds => "SQLite query timeout (s)",
        SettingsField.SqliteDatabasesProfile => "SQLite databases (profile)",
        SettingsField.SqliteDatabasesGlobal => "SQLite databases (global)",
        SettingsField.PostgresTools => "PostgreSQL tools",
        SettingsField.PostgresMode => "PostgreSQL mode",
        SettingsField.PostgresStatementsAllowed => "PostgreSQL statements allowed",
        SettingsField.PostgresConnectionsOffered => "PostgreSQL connections offered",
        SettingsField.PostgresSetPassword => "PostgreSQL set password",
        SettingsField.PostgresAddConnection => "PostgreSQL add/edit connection",
        SettingsField.PostgresPercentMention => "PostgreSQL %-mention enabled",
        SettingsField.PostgresQueryMaxRows => "PostgreSQL max rows",
        SettingsField.PostgresQueryTimeoutSeconds => "PostgreSQL query timeout (s)",
        SettingsField.PostgresConnectionsProfile => "PostgreSQL connections (profile)",
        SettingsField.PostgresConnectionsGlobal => "PostgreSQL connections (global)",
        SettingsField.UncTools => "UNC tools",
        SettingsField.UncWrites => "UNC writes",
        SettingsField.UncSharesOffered => "UNC shares offered",
        SettingsField.UncSetPassword => "UNC set password",
        SettingsField.UncAddShare => "UNC add/edit share",
        SettingsField.UncStarMention => "UNC *-mention enabled",
        SettingsField.UncSharesProfile => "UNC shares (profile)",
        SettingsField.UncSharesGlobal => "UNC shares (global)",
        SettingsField.DockerTools => "Docker tools",
        SettingsField.DockerWrites => "Docker writes",
        SettingsField.DockerEnginePipe => OperatingSystem.IsMacOS() ? DockerEngineSocketLabel : "Docker engine pipe",   // a Mac's socket, 2026-10-11
        SettingsField.DockerServers => "Docker servers enabled",
        SettingsField.DockerServerContainers => "Docker server containers",
        SettingsField.DockerServerMatchWords => "Docker server match words",
        SettingsField.DockerServerStopTimeoutSeconds => "Docker server stop timeout (s)",
        SettingsField.DockerServerPostStopDelaySeconds => "Docker server post-stop delay (s)",
        SettingsField.DockerServerReadyTimeoutSeconds => "Docker server ready timeout (s)",
        SettingsField.DockerServerStopOnExit => "Docker server stop on exit",
        SettingsField.ObsidianAllowDelete => "Obsidian allow delete (.trash)",   // "Obsidian allow delete" until 2026-09-23 (the user's call: the row says where a delete goes)
        SettingsField.WebBrowserMode => "Web browser mode",
        SettingsField.WebBrowserPath => "Web browser path",
        SettingsField.WebBrowserBotcheckWindow => "Web browser botcheck window",
        SettingsField.WebBrowserHomePage => "Web browser home page",
        SettingsField.BrowserPrivacy => "Web browser privacy",
        SettingsField.WebOpenLinksIn => "Web open links in",
        SettingsField.WebBrowserNetworkMode => "Web browser network mode",
        SettingsField.WebSearxngUrl => "Web SearXNG URL",
        SettingsField.WebSearchMaxResults => "Web search max results",
        SettingsField.WebDownloadMaxMegabytes => "Web download max (MB)",
        SettingsField.TtsVoicePreview => "TTS voice preview",
        SettingsField.FileTools => "File tools",
        SettingsField.ImageTools => "Image tools",
        SettingsField.ShoppingTools => "Shopping tools",
        SettingsField.ShoppingStores => "Shopping stores",
        SettingsField.ShoppingList => "Shopping list",
        SettingsField.WebSearchMethod => "Web search method",
        SettingsField.AnthropicApi => "Anthropic API enabled",
        SettingsField.AnthropicApiKey => "Anthropic API key",
        SettingsField.AnthropicApiMaxTokens => "Anthropic API max tokens",
        SettingsField.AnthropicApiPromptCaching => "Anthropic API prompt caching",
        SettingsField.ClaudeCodeServer => "Claude Code server enabled",
        SettingsField.OpenAIApi => "OpenAI API enabled",
        SettingsField.OpenAIApiKey => "OpenAI API key",
        SettingsField.OpenAIApiMaxTokens => "OpenAI API max tokens",
        SettingsField.OpenAIApiOrganization => "OpenAI API organization",
        SettingsField.OpenAIApiProject => "OpenAI API project",
        SettingsField.EmbeddedModels => "Embedded models",
        SettingsField.EmbeddedBackend => "Embedded backend",
        SettingsField.EmbeddedContextSize => "Embedded context length",
        SettingsField.EmbeddedGpuLayers => "Embedded GPU layers",
        SettingsField.EmbeddedVramBudget => "Embedded VRAM budget (%)",
        SettingsField.EmbeddedVramOnly => "Embedded VRAM only",
        SettingsField.EmbeddedHfDownloadType => "Embedded download mode",
        SettingsField.EmbeddedVision => "Embedded vision",
        SettingsField.EmbeddedLlmServer => "Embedded servers enabled",
        SettingsField.EmbeddedDrafter => "Embedded drafter",
        SettingsField.AskUser => "Ask user",
        SettingsField.AskMaxQuestions => "Ask max questions",
        SettingsField.AskMaxChoices => "Ask max choices per question",
        SettingsField.FileMentionFolderMode => "File @-mention folder mode",
        SettingsField.FileBrowserMode => "File browser/tree mode",   // "File browser mode" until 2026-09-23, when /tree came to follow it (the user's call)
        SettingsField.AgentSkills => "Agent skills",
        SettingsField.GlobalSkills => "Global skills",
        SettingsField.ExternalSkills => ExternalSkillsName,
        SettingsField.ProjectFile => "Project file",
        SettingsField.TranscriptMarkdown => "Transcript markdown",
        SettingsField.SkillCompactMode => "Skill compact mode",
        SettingsField.PastePreviewLines => "Paste preview lines",
        SettingsField.SkillHashMention => "#-mention enabled",
        SettingsField.ToolsDollarMention => "$-mention enabled",
        SettingsField.ToolCollapseCount => "Tool collapse count",
        SettingsField.CodeCollapseCount => "Code collapse count",
        SettingsField.ShowFileDiffs => "Show file diffs",
        SettingsField.DiffMaxLines => "Diff max lines",
        SettingsField.DiffCollapseCount => "Diff collapse count",
        SettingsField.ReflectionAutoLearn => "Reflection (auto-learn)",
        SettingsField.ReflectionReasoning => "Reflection reasoning",
        SettingsField.ReflectionWindow => "Reflection window",
        SettingsField.ReflectionMinToolCalls => "Reflection min tool calls",
        SettingsField.ReflectionMaxRequests => "Reflection max requests",
        SettingsField.ReflectionCooldownMinutes => "Reflection cooldown (min)",
        SettingsField.ReflectionCooldownMode => "Reflection cooldown mode",
        SettingsField.ReflectionConfirmMode => "Reflection confirm mode",   // the user's name (2026-10-12)
        SettingsField.ReflectionIncludesSessions => "Reflection include sessions",
        SettingsField.ReflectionYieldsToTurns => "Reflection yield to turns",
        SettingsField.ReflectionEditsSupportingFiles => "Reflection edit supporting files",   // the user's name (2026-09-27)
        SettingsField.ReflectionInstalledSkills => "Reflection edit downloaded skills",   // the user's name (2026-10-12; "Reflection downloaded skills" until then)
        SettingsField.HideExitAutocomplete => "Show /exit in autocomplete",
        SettingsField.EmojiPicker => "Emoji picker",
        SettingsField.CommandTypoIntercept => "Command typo intercept",
        SettingsField.KeepMessageHistory => "Keep message history",
        SettingsField.WelcomeSplash => "Welcome splash",
        SettingsField.ShowHeader => "Show header",   // the user's name (2026-10-01)
        SettingsField.MenuMaxHeight => "Menus max height",   // the user's name (2026-10-01)
        SettingsField.ShowWorkingDirectory => "Working directory in header",
        SettingsField.HintsBar => "Hints bar",   // 2026-10-14, the user's name
        SettingsField.StatusBarItems => "Status bar",   // "Hint bar" until 2026-10-14 (2026-10-12, the user's name)
        SettingsField.ToolbarItems => "Tool bar",   // the user's names (2026-10-12): "Show toolbar", "Show performance bar" and "Show home bar" until then, this one "Toolbar" for a few hours
        SettingsField.ShowPerformanceBar => "Performance bar",
        SettingsField.ShowHomeBar => "Home bar",
        SettingsField.BarOrder => "Toolbar order",   // "Order" on the pane, under Tool bars (PaneName)
        SettingsField.Theme => "Theme",
        SettingsField.ThemedExternalWindows => "Themed external windows",   // the user's name, under "Themed background" (2026-10-03; "Themed image viewer" beside "Image viewer" from later on 2026-09-27)
        SettingsField.ThemedBackground => "Themed background",   // the user's name, under "Theme" (2026-10-03)
        SettingsField.QueueMode => "Queue mode",   // "Queue messages", a toggle, until 2026-10-09 (the user's call)
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
        ScanScope.Disabled => "(not set; scan off)",
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
            SettingsField.LlmUrl => string.IsNullOrWhiteSpace(data.LlmUrl) ? BlankUrlLabel(ScanScopeOf(data)) : NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(data.LlmUrl) ? NeonSidekick.EmbeddedLlm.EmbeddedLlmText.UrlDisplay
                : NeonSidekick.Docker.DockerEndpoint.ContainerOf(data.LlmUrl) is { } container ? NeonSidekick.Docker.DockerServerText.UrlDisplay(container) : data.LlmUrl,
            SettingsField.LlmModel => string.IsNullOrWhiteSpace(data.LlmModel) ? "(first listed)" : data.LlmModel,
            SettingsField.LlmApiKey => ApiKeyLabel(data.LlmApiKey),
            SettingsField.LlmRequestTimeoutSeconds => Seconds(data.LlmRequestTimeoutSeconds),
            SettingsField.LlmTurnTimeoutSeconds => Seconds(data.LlmTurnTimeoutSeconds),
            SettingsField.LlmContextLength => data.LlmContextLength > 0 ? Tokens(data.LlmContextLength) : DetectedContextLengthLabel,
            SettingsField.TtsHttpUrl => string.IsNullOrWhiteSpace(data.TtsHttpUrl) ? NoTtsUrlLabel : data.TtsHttpUrl,
            SettingsField.TtsVoice => data.TtsVoice,
            SettingsField.TtsVoicePreset => PresetValue(data, profileDirectory),
            SettingsField.TtsOutput => OnOff(data.TtsOutput),
            SettingsField.SttInput => OnOff(data.SttInput),
            SettingsField.SttDestination => data.SttDestination,
            SettingsField.CameraTools => OnOff(data.CameraTools),
            SettingsField.CameraShutter => data.CameraShutter,
            SettingsField.CameraPreview => data.CameraPreview,
            SettingsField.CameraDevice => string.IsNullOrWhiteSpace(data.CameraDevice) ? FirstCameraLabel : data.CameraDevice,
            SettingsField.CameraResolution => data.CameraResolution,
            SettingsField.CameraKeepInSessions => OnOff(data.CameraKeepInSessions),
            SettingsField.CameraOutputFolder => string.IsNullOrWhiteSpace(data.CameraOutputFolder) ? CameraOutputHereLabel : data.CameraOutputFolder,
            SettingsField.ScreenTools => OnOff(data.ScreenTools),
            SettingsField.ScreenAsk => data.ScreenAsk,
            SettingsField.ScreenPreview => OnOff(data.ScreenPreview),
            SettingsField.ScreenOutputFolder => string.IsNullOrWhiteSpace(data.ScreenOutputFolder) ? CameraOutputHereLabel : data.ScreenOutputFolder,
            SettingsField.ScreenKeepInSessions => OnOff(data.ScreenKeepInSessions),
            SettingsField.CameraWatchSeconds => Seconds(data.CameraWatchSeconds),
            SettingsField.CameraWatchThreshold => data.CameraWatchThreshold.ToString(CultureInfo.InvariantCulture) + "%",
            SettingsField.CameraWatchUnprompted => OnOff(data.CameraWatchUnprompted),
            SettingsField.CameraWatchMinGapSeconds => Seconds(data.CameraWatchMinGapSeconds),
            SettingsField.BotChatCamera => OnOff(data.BotChatCamera),
            SettingsField.BotChatRecentContext => OnOff(data.BotChatRecentContext),
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
            SettingsField.MemoryMode => data.MemoryMode,
            SettingsField.WorkingDirectory => string.IsNullOrWhiteSpace(data.WorkingDirectory) ? DefaultWorkingDirectoryLabel(profileDirectory) : data.WorkingDirectory,
            SettingsField.CopyUserPrompt => OnOff(data.CopyUserPrompt),
            SettingsField.UserLineStyle => data.UserLineStyle,
            SettingsField.ShowImageThumbnails => OnOff(data.ShowImageThumbnails),
            SettingsField.LlmCompactType => data.LlmCompactType,
            SettingsField.LlmCompactKeepRecent => Turns(data.LlmCompactKeepRecent),
            SettingsField.LlmCompactShowSummary => OnOff(data.LlmCompactShowSummary),
            SettingsField.LlmAutoCompactPercent => data.LlmAutoCompactPercent > 0 ? Percent(data.LlmAutoCompactPercent) : CompactAtOffLabel,
            SettingsField.LlmMaxTurns => data.LlmMaxTurns > 0 ? Turns(data.LlmMaxTurns) : LlmMaxTurnsAutoLabel,
            SettingsField.LlmToolCompactType => data.LlmToolCompactType,
            SettingsField.LlmPictureKeep => data.LlmPictureKeep > 0 ? ComfyPictures(data.LlmPictureKeep) : NoCapLabel,
            SettingsField.LlmPictureMegabytes => data.LlmPictureMegabytes > 0 ? data.LlmPictureMegabytes.ToString(CultureInfo.InvariantCulture) + " MB" : NoCapLabel,
            SettingsField.LlmMaxToolIterations => RoundTrips(data.LlmMaxToolIterations),
            SettingsField.PlanGapPasses => GapPasses(data.PlanGapPasses),
            SettingsField.ImageThumbnailSize => data.ImageThumbnailSize,
            SettingsField.FileTreeMaxLength => Entries(data.FileTreeMaxLength),
            SettingsField.FileTreeShowSizes => OnOff(data.FileTreeShowSizes),
            SettingsField.LlmOfferTools => OnOff(data.LlmOfferTools),
            SettingsField.LlmUseFunVerbs => OnOff(data.LlmUseFunVerbs),
            SettingsField.LlmShowThinking => OnOff(data.LlmShowThinking),
            SettingsField.LlmPreserveThinking => OnOff(data.LlmPreserveThinking),
            SettingsField.LlmReasoningEstimate => data.LlmReasoningEstimate,
            SettingsField.LlmSampling => SamplingText.Summary(data.LlmSampling),
            SettingsField.LlmSamplingFromHuggingFace => OnOff(data.LlmSamplingFromHuggingFace),
            SettingsField.SessionSaveThinking => OnOff(data.SessionSaveThinking),
            SettingsField.ClaudeCodeExecutable => string.IsNullOrWhiteSpace(data.ClaudeCodeExecutable) ? ClaudeLookedUpLabel : data.ClaudeCodeExecutable,
            SettingsField.ClaudeCodePermissions => data.ClaudeCodePermissions,
            SettingsField.ClaudeCodeModel => string.IsNullOrWhiteSpace(data.ClaudeCodeModel) ? ClaudeDefaultLabel : data.ClaudeCodeModel,
            SettingsField.ClaudeCodeEffort => string.IsNullOrWhiteSpace(data.ClaudeCodeEffort) ? ClaudeDefaultLabel : data.ClaudeCodeEffort,
            SettingsField.Advisors => OnOff(data.Advisors),
            SettingsField.AdvisorList => NeonSidekick.Advisors.AdvisorRoster.Value(data.AdvisorList),
            SettingsField.AdvisorContext => data.AdvisorContext,
            SettingsField.AdvisorCallsPerTurn => AdvisorCalls(data.AdvisorCallsPerTurn),
            SettingsField.AdvisorMaxToolIterations => AdvisorIterations(data.AdvisorMaxToolIterations),
            SettingsField.AdvisorThreadPairs => AdvisorPairs(data.AdvisorThreadPairs),
            SettingsField.AdvisorEmbedded => data.AdvisorEmbedded,
            SettingsField.AdvisorConfirm => OnOff(data.AdvisorConfirm),
            SettingsField.AnthropicApi => OnOff(data.AnthropicApi),
            SettingsField.AnthropicApiKey => ApiKeyLabel(data.AnthropicApiKey),
            SettingsField.AnthropicApiMaxTokens => Tokens(data.AnthropicApiMaxTokens),
            SettingsField.AnthropicApiPromptCaching => OnOff(data.AnthropicApiPromptCaching),
            SettingsField.ClaudeCodeServer => OnOff(data.ClaudeCodeServer),
            SettingsField.OpenAIApi => OnOff(data.OpenAIApi),
            SettingsField.OpenAIApiKey => ApiKeyLabel(data.OpenAIApiKey),
            SettingsField.OpenAIApiMaxTokens => data.OpenAIApiMaxTokens > 0 ? Tokens(data.OpenAIApiMaxTokens) : OpenAIApiMaxTokensNoneLabel,
            SettingsField.OpenAIApiOrganization => string.IsNullOrWhiteSpace(data.OpenAIApiOrganization) ? OpenAIApiHeaderNoneLabel : data.OpenAIApiOrganization.Trim(),
            SettingsField.OpenAIApiProject => string.IsNullOrWhiteSpace(data.OpenAIApiProject) ? OpenAIApiHeaderNoneLabel : data.OpenAIApiProject.Trim(),
            SettingsField.EmbeddedModels => EmbeddedModelsDoorLabel,
            SettingsField.EmbeddedBackend => data.EmbeddedBackend,
            SettingsField.EmbeddedContextSize => data.EmbeddedContextSize == 0 ? EmbeddedContextFitLabel : Tokens(data.EmbeddedContextSize),
            SettingsField.EmbeddedGpuLayers => data.EmbeddedGpuLayers,
            SettingsField.EmbeddedVramBudget => data.EmbeddedVramBudget == NeonSidekick.EmbeddedLlm.EmbeddedVramBudget.Off ? NeonSidekick.EmbeddedLlm.EmbeddedVramBudget.OffWord : Percent(data.EmbeddedVramBudget),
            SettingsField.EmbeddedHfDownloadType => data.EmbeddedHfDownloadType,
            SettingsField.EmbeddedVision => OnOff(data.EmbeddedVision),
            SettingsField.EmbeddedVramOnly => OnOff(data.EmbeddedVramOnly),
            SettingsField.EmbeddedLlmServer => OnOff(data.EmbeddedLlmServer),
            SettingsField.EmbeddedDrafter => OnOff(data.EmbeddedDrafter),
            SettingsField.LlmScanMode => data.LlmScanMode,
            SettingsField.TtsSource => data.TtsSource,
            SettingsField.WebTools => OnOff(data.WebTools),
            SettingsField.GitLibTools => OnOff(data.GitLibTools),
            SettingsField.GitLibDiffMaxLines => Lines(data.GitLibDiffMaxLines),
            SettingsField.ShellCommandPolicy => data.ShellCommandPolicy,
            SettingsField.ShellCommandAllowed => Prefixes(data.ShellCommandAllowed.Count),
            SettingsField.ShellPolice => OnOff(data.ShellPolice),
            SettingsField.ShellPoliceForbiddenStrings => Strings(Shell.ForbiddenStrings.Sorted(data.ShellPoliceForbiddenStrings).Count),
            SettingsField.ShellPreferNative => OnOff(data.ShellPreferNative),
            SettingsField.ShellDefault => data.ShellDefault,
            SettingsField.ShellTimeoutSeconds => Seconds(data.ShellTimeoutSeconds),
            SettingsField.ShellForegroundCapSeconds => Seconds(data.ShellForegroundCapSeconds),
            SettingsField.ShellOutputMaxChars => Chars(data.ShellOutputMaxChars),
            SettingsField.ShellCodeLanguages => string.Join(", ", Shell.CodeLanguages.Resolve(data).Select(Shell.CodeLanguages.Name)),
            SettingsField.ShellCodeTimeoutSeconds => Seconds(data.ShellCodeTimeoutSeconds),
            SettingsField.ShellToolBridge => OnOff(data.ShellToolBridge),
            SettingsField.ShellCodeMaxToolCalls => ToolCalls(data.ShellCodeMaxToolCalls),
            SettingsField.GitLibLogMaxCommits => Commits(data.GitLibLogMaxCommits),
            SettingsField.GitLibEmail => string.IsNullOrWhiteSpace(data.GitLibEmail) ? NoGitIdentityLabel : data.GitLibEmail,
            SettingsField.GitLibName => string.IsNullOrWhiteSpace(data.GitLibName) ? NoGitIdentityLabel : data.GitLibName,
            SettingsField.ObsidianTools => OnOff(data.ObsidianTools),
            SettingsField.ObsidianAllowDelete => OnOff(data.ObsidianAllowDelete),
            SettingsField.SqlTools => OnOff(data.SqlTools),
            SettingsField.SqlMode => data.SqlMode,
            SettingsField.SqlStatementsAllowed => WriteStatementsValue(data.SqlStatementsAllowed, data.SqlMode),
            SettingsField.ComfyTools => OnOff(data.ComfyTools),
            SettingsField.HomeAssistantTools => OnOff(data.HomeAssistantTools),
            SettingsField.YouTubeTools => OnOff(data.YouTubeTools),
            SettingsField.YouTubeApiKey => ApiKeyLabel(data.YouTubeApiKey),
            SettingsField.YouTubeSearchMaxResults => Results(data.YouTubeSearchMaxResults),
            SettingsField.YouTubeAutoplay => OnOff(data.YouTubeAutoplay),
            SettingsField.YouTubeVoice => YouTube.YouTubeVoiceMode.Resolve(data.YouTubeVoice),
            SettingsField.YouTubeSearchOrder => YouTube.YouTubeSearchFilters.Defaults(data).Order,
            SettingsField.YouTubeSafeSearch => YouTube.YouTubeSearchFilters.Defaults(data).SafeSearch,
            SettingsField.YouTubeRegion => YouTube.YouTubeSearchFilters.Defaults(data).Region is { Length: > 0 } region ? region : NoYouTubeRegionLabel,
            SettingsField.YouTubeLanguage => YouTube.YouTubeSearchFilters.Defaults(data).Language is { Length: > 0 } language ? language : NoYouTubeLanguageLabel,
            SettingsField.HomeAssistantUrl => string.IsNullOrWhiteSpace(data.HomeAssistantUrl) ? NoHomeAssistantUrlLabel : data.HomeAssistantUrl,
            SettingsField.HomeAssistantToken => ApiKeyLabel(data.HomeAssistantToken),
            SettingsField.HomeAssistantTest => HomeAssistantTestLabel,
            SettingsField.HomeAssistantActionPolicy => HomeAssistant.HaPolicy.Resolve(data.HomeAssistantActionPolicy),
            SettingsField.HomeAssistantAssistAgent => string.IsNullOrWhiteSpace(data.HomeAssistantAssistAgent) ? DefaultAssistAgentLabel : data.HomeAssistantAssistAgent,
            SettingsField.HomeAssistantTimeoutSeconds => Seconds(data.HomeAssistantTimeoutSeconds),
            SettingsField.PrintTools => OnOff(data.PrintTools),
            SettingsField.PrintActionPolicy => Printing.PrintPolicy.Resolve(data.PrintActionPolicy),
            SettingsField.PrintDefaultPrinter => string.IsNullOrWhiteSpace(data.PrintDefaultPrinter) ? WindowsDefaultPrinterLabel : data.PrintDefaultPrinter,
            SettingsField.PrintFontSize => data.PrintFontSize.ToString(CultureInfo.InvariantCulture) + " pt",
            SettingsField.PdfEngine => Pdf.PdfEngine.Resolve(data.PdfEngine),
            SettingsField.ComfyUrl => string.IsNullOrWhiteSpace(data.ComfyUrl) ? NoComfyUrlLabel : data.ComfyUrl,
            SettingsField.ComfyTimeoutSeconds => Seconds(data.ComfyTimeoutSeconds),
            SettingsField.ComfyMaxPicturesPerCall => ComfyPictures(data.ComfyMaxPicturesPerCall),
            SettingsField.ComfyReinforceNegatives => OnOff(data.ComfyReinforceNegatives),
            SettingsField.ComfyShowPrompts => OnOff(data.ComfyShowPrompts),
            SettingsField.ComfyCaretMention => OnOff(data.ComfyCaretMention),
            SettingsField.ComfyPictureStrip => OnOff(data.ComfyPictureStrip),
            SettingsField.BotChatLlmMode => data.BotChatLlmMode,
            SettingsField.BotChatMultiEmbedded => data.BotChatMultiEmbedded,
            SettingsField.BotChatMultiEmbeddedKill => OnOff(data.BotChatMultiEmbeddedKill),
            SettingsField.LlmMidTurnUsage => data.LlmMidTurnUsage,
            SettingsField.BotChatComfy => OnOff(data.BotChatComfy),
            SettingsField.BotChatLimitedComfyWorkflows => LimitedNamesValue(data.BotChatLimitedComfyWorkflows),
            SettingsField.BotChatImageMode => data.BotChatImageMode,
            SettingsField.BotChatImg2ImgMode => data.BotChatImg2ImgMode,
            SettingsField.BotChatImageMentionMode => data.BotChatImageMentionMode,
            SettingsField.BotChatImageAsync => OnOff(data.BotChatImageAsync),
            SettingsField.BotChatNonTtsDelaySeconds => SecondsLabel(data.BotChatNonTtsDelaySeconds),
            SettingsField.BotChatTools => OnOff(data.BotChatTools),
            SettingsField.BotChatLimitedTools => LimitedNamesValue(data.BotChatLimitedTools),
            SettingsField.BotChatSkills => OnOff(data.BotChatSkills),
            SettingsField.BotChatVision => OnOff(data.BotChatVision),
            SettingsField.BotChatLimitedSkills => LimitedNamesValue(data.BotChatLimitedSkills),
            SettingsField.BotChatMemory => OnOff(data.BotChatMemory),
            SettingsField.BotChatMemoryMode => data.BotChatMemoryMode,
            SettingsField.ComfyOutputFolder => string.IsNullOrWhiteSpace(data.ComfyOutputFolder) ? ComfyOutputHereLabel : data.ComfyOutputFolder,
            SettingsField.ComfyWorkflowsOffered => ComfyOfferedValue(data.ComfyWorkflowsOffered, InstalledComfyWorkflows(profileDirectory)),
            SettingsField.ComfyAddWorkflow => ComfyAddWorkflowLabel,
            SettingsField.SqlSetPassword => SqlSetPasswordLabel,
            SettingsField.SqlAddConnection => SqlAddConnectionLabel,
            SettingsField.SqlConnectionsOffered => SqlOfferedValue(data.SqlConnectionsOffered, Sql.SqlConfigFile.LoadCatalog(profileDirectory, Profiles.HomeOf(profileDirectory))),
            SettingsField.SqlPercentMention => OnOff(data.SqlPercentMention),
            SettingsField.SqlQueryMaxRows => SqlRows(data.SqlQueryMaxRows),
            SettingsField.SqlQueryTimeoutSeconds => Seconds(data.SqlQueryTimeoutSeconds),
            SettingsField.QueryResultMaxChars => Chars(data.QueryResultMaxChars),
            SettingsField.SqlConnectionsProfile => SqlConnectionsLabel(Sql.SqlConfigFile.ProfilePath(profileDirectory)),
            SettingsField.SqlConnectionsGlobal => SqlConnectionsLabel(Sql.SqlConfigFile.GlobalPath(Profiles.HomeOf(profileDirectory))),
            SettingsField.OracleTools => OnOff(data.OracleTools),
            SettingsField.OracleMode => data.OracleMode,
            SettingsField.OracleStatementsAllowed => WriteStatementsValue(data.OracleStatementsAllowed, data.OracleMode),
            SettingsField.OracleSetPassword => SqlSetPasswordLabel,
            SettingsField.OracleAddConnection => SqlAddConnectionLabel,
            SettingsField.OracleConnectionsOffered => OracleOfferedValue(data.OracleConnectionsOffered, Oracle.OracleConfigFile.LoadCatalog(profileDirectory, Profiles.HomeOf(profileDirectory))),
            SettingsField.OraclePercentMention => OnOff(data.OraclePercentMention),
            SettingsField.OracleQueryMaxRows => SqlRows(data.OracleQueryMaxRows),
            SettingsField.OracleQueryTimeoutSeconds => Seconds(data.OracleQueryTimeoutSeconds),
            SettingsField.OracleConnectionsProfile => OracleConnectionsLabel(Oracle.OracleConfigFile.ProfilePath(profileDirectory)),
            SettingsField.OracleConnectionsGlobal => OracleConnectionsLabel(Oracle.OracleConfigFile.GlobalPath(Profiles.HomeOf(profileDirectory))),
            SettingsField.MySqlTools => OnOff(data.MySqlTools),
            SettingsField.MySqlMode => data.MySqlMode,
            SettingsField.MySqlStatementsAllowed => WriteStatementsValue(data.MySqlStatementsAllowed, data.MySqlMode),
            SettingsField.MySqlSetPassword => SqlSetPasswordLabel,
            SettingsField.MySqlAddConnection => SqlAddConnectionLabel,
            SettingsField.MySqlConnectionsOffered => MySqlOfferedValue(data.MySqlConnectionsOffered, MySql.MySqlConfigFile.LoadCatalog(profileDirectory, Profiles.HomeOf(profileDirectory))),
            SettingsField.MySqlPercentMention => OnOff(data.MySqlPercentMention),
            SettingsField.MySqlQueryMaxRows => SqlRows(data.MySqlQueryMaxRows),
            SettingsField.MySqlQueryTimeoutSeconds => Seconds(data.MySqlQueryTimeoutSeconds),
            SettingsField.MySqlConnectionsProfile => MySqlConnectionsLabel(MySql.MySqlConfigFile.ProfilePath(profileDirectory)),
            SettingsField.MySqlConnectionsGlobal => MySqlConnectionsLabel(MySql.MySqlConfigFile.GlobalPath(Profiles.HomeOf(profileDirectory))),
            SettingsField.SqliteTools => OnOff(data.SqliteTools),
            SettingsField.SqliteMode => data.SqliteMode,
            SettingsField.SqliteStatementsAllowed => SqliteStatementsValue(data.SqliteStatementsAllowed, data.SqliteMode),
            SettingsField.SqliteDatabasesOffered => SqliteOfferedValue(data.SqliteDatabasesOffered, Sqlite.SqliteConfigFile.LoadCatalog(profileDirectory, Profiles.HomeOf(profileDirectory))),
            SettingsField.SqliteSandboxFiles => OnOff(data.SqliteSandboxFiles),
            SettingsField.SqliteAddDatabase => SqliteAddDatabaseLabel,
            SettingsField.SqlitePercentMention => OnOff(data.SqlitePercentMention),
            SettingsField.SqliteQueryMaxRows => SqlRows(data.SqliteQueryMaxRows),
            SettingsField.SqliteQueryTimeoutSeconds => Seconds(data.SqliteQueryTimeoutSeconds),
            SettingsField.SqliteDatabasesProfile => SqliteDatabasesLabel(Sqlite.SqliteConfigFile.ProfilePath(profileDirectory)),
            SettingsField.SqliteDatabasesGlobal => SqliteDatabasesLabel(Sqlite.SqliteConfigFile.GlobalPath(Profiles.HomeOf(profileDirectory))),
            SettingsField.PostgresTools => OnOff(data.PostgresTools),
            SettingsField.PostgresMode => data.PostgresMode,
            SettingsField.PostgresStatementsAllowed => WriteStatementsValue(data.PostgresStatementsAllowed, data.PostgresMode),
            SettingsField.PostgresSetPassword => SqlSetPasswordLabel,
            SettingsField.PostgresAddConnection => SqlAddConnectionLabel,
            SettingsField.PostgresConnectionsOffered => PostgresOfferedValue(data.PostgresConnectionsOffered, Postgres.PostgresConfigFile.LoadCatalog(profileDirectory, Profiles.HomeOf(profileDirectory))),
            SettingsField.PostgresPercentMention => OnOff(data.PostgresPercentMention),
            SettingsField.PostgresQueryMaxRows => SqlRows(data.PostgresQueryMaxRows),
            SettingsField.PostgresQueryTimeoutSeconds => Seconds(data.PostgresQueryTimeoutSeconds),
            SettingsField.PostgresConnectionsProfile => PostgresConnectionsLabel(Postgres.PostgresConfigFile.ProfilePath(profileDirectory)),
            SettingsField.PostgresConnectionsGlobal => PostgresConnectionsLabel(Postgres.PostgresConfigFile.GlobalPath(Profiles.HomeOf(profileDirectory))),
            SettingsField.UncTools => OnOff(data.UncTools),
            SettingsField.UncWrites => OnOff(data.UncWrites),
            SettingsField.UncSharesOffered => UncOfferedValue(data.UncSharesOffered, Unc.UncConfigFile.LoadCatalog(profileDirectory, Profiles.HomeOf(profileDirectory))),
            SettingsField.UncSetPassword => UncSetPasswordLabel,
            SettingsField.UncAddShare => UncAddShareLabel,
            SettingsField.UncStarMention => OnOff(data.UncStarMention),
            SettingsField.UncSharesProfile => UncSharesLabel(Unc.UncConfigFile.ProfilePath(profileDirectory)),
            SettingsField.UncSharesGlobal => UncSharesLabel(Unc.UncConfigFile.GlobalPath(Profiles.HomeOf(profileDirectory))),
            SettingsField.DockerTools => OnOff(data.DockerTools),
            SettingsField.DockerWrites => OnOff(data.DockerWrites),
            SettingsField.DockerEnginePipe => OperatingSystem.IsMacOS() ? Docker.DockerSocket.RowValue(data.DockerEnginePipe) : Docker.DockerPipe.Display(data.DockerEnginePipe),
            SettingsField.DockerServers => OnOff(data.DockerServers),
            SettingsField.DockerServerContainers => DockerServerContainersValue(data.DockerServerContainers),
            SettingsField.DockerServerMatchWords => DockerServerWordsValue(data.DockerServerMatchWords),
            SettingsField.DockerServerStopTimeoutSeconds => Seconds(data.DockerServerStopTimeoutSeconds),
            SettingsField.DockerServerPostStopDelaySeconds => Seconds(data.DockerServerPostStopDelaySeconds),
            SettingsField.DockerServerReadyTimeoutSeconds => Seconds(data.DockerServerReadyTimeoutSeconds),
            SettingsField.DockerServerStopOnExit => OnOff(data.DockerServerStopOnExit),
            SettingsField.ObsidianVault => string.IsNullOrWhiteSpace(data.ObsidianVault) ? NoObsidianVaultLabel : data.ObsidianVault,
            SettingsField.WebBrowserMode => data.WebBrowserMode,
            SettingsField.WebBrowserBotcheckWindow => data.WebBrowserBotcheckWindow,
            SettingsField.BrowserPrivacy => data.BrowserPrivacy,
            SettingsField.WebOpenLinksIn => data.WebOpenLinksIn,
            SettingsField.WebBrowserHomePage => string.IsNullOrWhiteSpace(data.WebBrowserHomePage) ? BlankHomePageLabel : data.WebBrowserHomePage,
            SettingsField.WebBrowserPath => string.IsNullOrWhiteSpace(data.WebBrowserPath) ? AutoBrowserLabel(locatedBrowser) : data.WebBrowserPath,
            SettingsField.DraftEditor => string.IsNullOrWhiteSpace(data.DraftEditor) ? DefaultDraftEditorLabel : data.DraftEditor,
            SettingsField.ImageEditor => string.IsNullOrWhiteSpace(data.ImageEditor) ? DefaultImageEditorLabel : data.ImageEditor,
            SettingsField.FileViewImageMaxPerCall => Pictures(data.FileViewImageMaxPerCall),
            SettingsField.FileSearchMaxResults => Results(data.FileSearchMaxResults),
            SettingsField.ImageEditQuality => data.ImageEditQuality.ToString(CultureInfo.InvariantCulture),
            SettingsField.ImageEditMetadata => Images.ImageWords.MetadataName(data.ImageEditMetadata),
            SettingsField.ImageEditMode => Images.ImageWords.EditModeName(data.ImageEditMode),
            SettingsField.ImageEditOutputFolder => string.IsNullOrWhiteSpace(data.ImageEditOutputFolder) ? ImageEditBesideLabel : data.ImageEditOutputFolder,
            SettingsField.PastedImagesFolder => string.IsNullOrWhiteSpace(data.PastedImagesFolder) ? CameraOutputHereLabel : data.PastedImagesFolder,
            SettingsField.McpServers => OnOff(data.McpServers),
            SettingsField.McpConnectTimeoutSeconds => Seconds(data.McpConnectTimeoutSeconds),
            SettingsField.WebBrowserNetworkMode => data.WebBrowserNetworkMode,
            SettingsField.WebSearxngUrl => string.IsNullOrWhiteSpace(data.WebSearxngUrl) ? NoSearxngUrlLabel : data.WebSearxngUrl,
            SettingsField.WebSearchMaxResults => Results(data.WebSearchMaxResults),
            SettingsField.WebDownloadMaxMegabytes => Megabytes(data.WebDownloadMaxMegabytes),
            SettingsField.TtsVoicePreview => OnOff(data.TtsVoicePreview),
            SettingsField.FileTools => OnOff(data.FileTools),
            SettingsField.ImageTools => OnOff(data.ImageTools),
            SettingsField.ShoppingTools => OnOff(data.ShoppingTools),
            SettingsField.ShoppingStores => NeonSidekick.Shopping.ShoppingText.ManageStoresLabel,
            SettingsField.ShoppingList => NeonSidekick.Shopping.ShoppingText.ManageListLabel,
            SettingsField.WebSearchMethod => data.WebSearchMethod,
            SettingsField.AskUser => OnOff(data.AskUser),
            SettingsField.AskMaxQuestions => Questions(data.AskMaxQuestions),
            SettingsField.AskMaxChoices => Choices(data.AskMaxChoices),
            SettingsField.FileMentionFolderMode => data.FileMentionFolderMode,
            SettingsField.FileBrowserMode => data.FileBrowserMode,
            SettingsField.AgentSkills => OnOff(data.AgentSkills),
            SettingsField.GlobalSkills => OnOff(data.GlobalSkills),
            SettingsField.ExternalSkills => OnOff(data.ExternalSkills),
            SettingsField.ProjectFile => OnOff(data.ProjectFile),
            SettingsField.TranscriptMarkdown => OnOff(data.TranscriptMarkdown),
            SettingsField.SkillCompactMode => data.SkillCompactMode,
            SettingsField.PastePreviewLines => Lines(data.PastePreviewLines),
            SettingsField.SkillHashMention => OnOff(data.SkillHashMention),
            SettingsField.ToolsDollarMention => OnOff(data.ToolsDollarMention),
            SettingsField.ToolCollapseCount => Lines(data.ToolCollapseCount),
            SettingsField.CodeCollapseCount => Lines(data.CodeCollapseCount),
            SettingsField.ShowFileDiffs => OnOff(data.ShowFileDiffs),
            SettingsField.DiffMaxLines => DiffLines(data.DiffMaxLines),
            SettingsField.DiffCollapseCount => Lines(data.DiffCollapseCount),
            SettingsField.ReflectionAutoLearn => OnOff(data.ReflectionAutoLearn),
            SettingsField.ReflectionReasoning => data.ReflectionReasoning,
            SettingsField.ReflectionWindow => Turns(data.ReflectionWindow),
            SettingsField.ReflectionMinToolCalls => ToolCalls(data.ReflectionMinToolCalls),
            SettingsField.ReflectionMaxRequests => Requests(data.ReflectionMaxRequests),
            SettingsField.ReflectionCooldownMinutes => Minutes(data.ReflectionCooldownMinutes),
            SettingsField.ReflectionCooldownMode => data.ReflectionCooldownMode,
            SettingsField.ReflectionConfirmMode => data.ReflectionConfirmMode,
            SettingsField.ReflectionInstalledSkills => data.ReflectionInstalledSkills,
            SettingsField.ReflectionIncludesSessions => OnOff(data.ReflectionIncludesSessions),
            SettingsField.ReflectionYieldsToTurns => OnOff(data.ReflectionYieldsToTurns),
            SettingsField.ReflectionEditsSupportingFiles => OnOff(data.ReflectionEditsSupportingFiles),
            SettingsField.HideExitAutocomplete => OnOff(!data.HideExitAutocomplete),   // the row asks the other way round since 2026-10-13 ("Show /exit in autocomplete"); the saved key stays
            SettingsField.EmojiPicker => OnOff(data.EmojiPicker),
            SettingsField.CommandTypoIntercept => OnOff(data.CommandTypoIntercept),
            SettingsField.KeepMessageHistory => OnOff(data.KeepMessageHistory),
            SettingsField.WelcomeSplash => data.WelcomeSplashMode,
            SettingsField.MenuMaxHeight => data.MenuMaxHeight,
            SettingsField.SessionLogging => OnOff(data.SessionLogging),
            SettingsField.SessionNamingMode => data.SessionNamingMode,
            SettingsField.SessionShowName => data.SessionShowName,
            SettingsField.SessionRetentionDays => Days(data.SessionRetentionDays),
            SettingsField.SessionSearchMaxResults => Results(data.SessionSearchMaxResults),
            SettingsField.SessionTool => OnOff(data.SessionTool),
            SettingsField.ShowHeader => OnOff(data.ShowHeader),
            SettingsField.ShowWorkingDirectory => OnOff(data.ShowWorkingDirectory),
            SettingsField.StatusBarItems => App.StatusBarItems.Value(data.StatusBarItems, data.StatusBarOrder, data.StatusBarRight),
            SettingsField.HintsBar => UI.HintsBarMode.Name(UI.HintsBarMode.Resolve(data.HintsBar)),
            SettingsField.ToolbarItems => App.ToolbarItems.Value(data.ToolbarItems),
            SettingsField.ShowPerformanceBar => PerfBarItems.Value(data.PerformanceBarItems, data.PerformanceBarLook, data.PerformanceBarOrder, data.PerformanceBarLeft, data.PerformanceBarRight),
            SettingsField.ShowHomeBar => HomeBarItems.Value(data.HomeBarItems, null),
            SettingsField.BarOrder => PaneBars.Value(data.BarOrder),
            SettingsField.ThemedExternalWindows => OnOff(data.ThemedExternalWindows),
            SettingsField.ThemedBackground => OnOff(data.ThemedBackground),
            SettingsField.Theme => data.Theme,
            SettingsField.QueueMode => data.QueueMode,
            SettingsField.QueueCancelMode => data.QueueCancelMode,
            SettingsField.ModelAvatar => AvatarRules.Normalize(data.ModelAvatar) is { Length: > 0 } avatar ? EmojiText.Value(avatar, EmojiCatalog.Shipped) : NoAvatarLabel,
            SettingsField.UserAvatar => AvatarRules.Normalize(data.UserAvatar) is { Length: > 0 } face ? EmojiText.Value(face, EmojiCatalog.Shipped) : NoAvatarLabel,
            _ => "",
        };
    }

    /// <summary>The label of <see cref="SettingsField.ExternalSkills"/>, naming the folder it reads (the user's wording, 2026-09-16); the longest label there is, so it sets <see cref="LabelWidth"/>. Pinned.</summary>
    public static string ExternalSkillsName => OperatingSystem.IsMacOS() ? "External skills (.agents/skills)" : "External skills (.agents\\skills)";   // a Mac's separator, 2026-10-06

    /// <summary>
    /// How the menu shows an empty <see cref="AppSettingsData.WebBrowserPath"/>: the browser
    /// auto-detection found (<c>(auto: msedge.exe)</c>), or that it found none. Pinned.
    /// </summary>
    public static string AutoBrowserLabel(string? located) =>
        located is null ? "(auto: none found)" : "(auto: " + Path.GetFileName(located) + ")";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.WebSearxngUrl"/> (the engine is <see cref="SettingsField.WebSearchMethod"/>'s row, not this one's). Pinned.</summary>
    public const string NoSearxngUrlLabel = "(not set)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.GitLibEmail"/> or <see cref="AppSettingsData.GitLibName"/> (2026-09-21): <c>/gituser</c> refuses until both are set (and while <see cref="AppSettingsData.GitLibTools"/> is off). Pinned.</summary>
    public const string NoGitIdentityLabel = "(not set)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ObsidianVault"/> (2026-09-22): no vault, so no vault tool is offered. Pinned.</summary>
    public const string NoObsidianVaultLabel = "(not set)";


    /// <summary>A <c>Botchat limited …</c> row's value while it names none (2026-09-27 as the two workflow pickers' none, which went on 2026-10-04; renamed from <c>NoBotChatWorkflowLabel</c> in that day's second code review). Pinned.</summary>
    public const string NoLimitedNamesLabel = "(none)";

    /// <summary>The <c>Botchat limited skills</c>, <c>Botchat limited tools</c> and <c>Botchat ComfyUI limited workflows</c> value (2026-09-27 as the preloaded skills'; all three since 2026-10-04): <see cref="NoLimitedNamesLabel"/> with none, else the names, comma-joined. Pinned.</summary>
    public static string LimitedNamesValue(IReadOnlyList<string>? names)
    {
        var kept = names?.Select(n => n.Trim()).Where(n => n.Length > 0).ToList() ?? [];
        return kept.Count == 0 ? NoLimitedNamesLabel : string.Join(", ", kept);
    }

    /// <summary>When the <c>Botchat limited skills</c> checklist has nothing to list (2026-09-27 as the preloaded skills'). Pinned.</summary>
    public const string NoSkillsToLimit = "No skills are installed for the botchat: add one to this profile's or the global skills folder.";

    /// <summary>When the <c>Botchat limited tools</c> checklist has nothing to list (2026-10-04). Pinned.</summary>
    public const string NoToolsToLimit = "No tools to list for the botchat.";

    /// <summary>The <c>Botchat limited tools</c> checklist's caption while <c>Botchat tools enabled</c> is on (2026-10-04): the list waits. Pinned.</summary>
    public const string LimitedToolsUnusedCaption = "Botchat tools enabled is on: the bots get every tool, and this list is not used until it is off.";

    /// <summary>The <c>Botchat ComfyUI limited workflows</c> checklist's caption while <c>Botchat ComfyUI enabled</c> is on (later on 2026-10-04). Pinned.</summary>
    public const string LimitedComfyUnusedCaption = "Botchat ComfyUI enabled is on: the bots get this profile's offered workflows, and this list is not used until it is off.";

    /// <summary>The <c>Botchat limited skills</c> checklist's caption while <c>Botchat skills enabled</c> is on (2026-10-04). Pinned.</summary>
    public const string LimitedSkillsUnusedCaption = "Botchat skills enabled is on: the bots get every skill, and this list is not used until it is off.";

    /// <summary>
    /// The status line when a checklist dropped saved names it no longer lists (2026-10-04, the user's call: the list keeps what the
    /// checklist shows, <see cref="OfferedNames"/>): <c>ComfyUI workflows offered: dropped 2 no longer listed: a, b</c>. Pinned.
    /// </summary>
    public static string StaleDroppedNotice(SettingsField field, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return FieldName(field) + ": dropped " + names.Count.ToString(CultureInfo.InvariantCulture) + " no longer listed: " + string.Join(", ", names);
    }

    /// <summary>The dim note on a <c>Botchat limited skills</c> row whose skill is ticked but not listed now (2026-10-04). Pinned.</summary>
    public const string NotAvailableNote = "not available now";

    /// <summary>The <c>Botchat limited tools</c> heading over the ticked tools not listed now — an MCP server not connected (2026-10-04). Pinned.</summary>
    public const string NotAvailableHeading = "Not available now";

    /// <summary>A ticked <c>Botchat limited skills</c> name with no skill listed now (2026-10-04): the mark, the name, <see cref="NotAvailableNote"/> dim; Enter unticks it, and then it is gone from the list. Pinned.</summary>
    public static string LimitedGoneRow(string name, int width)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Markup.Escape("[x] " + name.PadRight(width)) + Theme.DimMarkup(NotAvailableNote);
    }

    /// <summary>One <c>Botchat limited skills</c> checklist row: the mark, the name, the description cut short. Pinned.</summary>
    public static string LimitedSkillRow(Skills.Skill skill, bool chosen, int width)
    {
        ArgumentNullException.ThrowIfNull(skill);
        string about = skill.Description.Length > 60 ? skill.Description[..59] + "…" : skill.Description;
        return Markup.Escape((chosen ? "[x] " : "[ ] ") + skill.Name.PadRight(width)) + Theme.DimMarkup(about);
    }

    /// <summary>
    /// The <c>Botchat limited tools</c> checklist's rows (2026-10-04): per group a gap (the first none) and its heading — a
    /// <see cref="SectionRule"/> of the label, the count and why the main chat does not offer it, as <c>/tools</c>' Offered tab
    /// draws it — then a row per tool: the mark, the name padded to <see cref="ToolsText.NameWidth"/>, the description cut short
    /// and dim, with the tool's own note when the main chat would not offer it. The tool's name beside its row, null beside a gap
    /// or a heading; <c>Heading</c> true beside a heading (<see cref="ToolsText.HeadingRows"/>). A group with no tool is left out.
    /// <paramref name="gone"/>, the ticked names not listed now, close the list under <see cref="NotAvailableHeading"/>, each ticked
    /// with <see cref="NotAvailableNote"/> (2026-10-04). Pure.
    /// </summary>
    public static IReadOnlyList<(string Markup, string? Tool, bool Heading)> LimitedToolRows(IReadOnlyList<ToolGroup> groups, IReadOnlySet<string> chosen, IReadOnlyList<string>? gone = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(chosen);
        var rows = new List<(string, string?, bool)>(64);
        foreach (var group in groups.Where(g => g.Tools.Count > 0))
        {
            if (rows.Count > 0)
            {
                rows.Add(("", null, false));
            }

            rows.Add((SectionRule.Markup(group.Label, group.Count, group.Note.Length > 0 ? group.Note : null), null, true));
            foreach (var tool in group.Tools)
            {
                string about = tool.Description.Length > 60 ? tool.Description[..59] + "…" : tool.Description;
                string note = group.Offered && group.ToolNotes.TryGetValue(tool.Name, out var why) ? "  " + why : "";
                rows.Add((Markup.Escape((chosen.Contains(tool.Name) ? "[x] " : "[ ] ") + tool.Name.PadRight(ToolsText.NameWidth)) + Theme.DimMarkup(about + note), tool.Name, false));
            }
        }

        if (gone is { Count: > 0 })
        {
            if (rows.Count > 0)
            {
                rows.Add(("", null, false));
            }

            rows.Add((SectionRule.Markup(NotAvailableHeading, gone.Count.ToString(CultureInfo.InvariantCulture)), null, true));
            rows.AddRange(gone.Select(name => (LimitedGoneRow(name, ToolsText.NameWidth), (string?)name, false)));
        }

        return rows;
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
    /// The value of <c>SQL connections offered</c> (later on 2026-09-23): how many of the loaded connections the profile
    /// offers, <c>none of N</c> before any is ticked (2026-10-01: null offers none; it read "all (not narrowed)" until then). Pinned.
    /// </summary>
    public static string SqlOfferedValue(IReadOnlyList<string>? offered, Sql.SqlCatalog loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        int kept = loaded.Offered(offered).Connections.Count;
        return (kept == 0 ? "none" : kept.ToString(CultureInfo.InvariantCulture)) + " of " + loaded.Connections.Count.ToString(CultureInfo.InvariantCulture);
    }

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
        string store = connection.Config.InCredentialManager ? Sql.SqlText.CredentialStore : "encrypted in sql.json";
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

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.WebBrowserHomePage"/> (2026-10-13). Pinned.</summary>
    public const string HomePageError = "must be a web address, or empty for the blank page";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.WebBrowserHomePage"/>: the blank page. Pinned.</summary>
    public const string BlankHomePageLabel = "(blank page)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ClaudeCodeExecutable"/> (2026-09-27): looked for on the PATH and in the installer's folder. Pinned.</summary>
    public const string ClaudeLookedUpLabel = "(looked up)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ClaudeCodeModel"/> or <see cref="AppSettingsData.ClaudeCodeEffort"/> (2026-09-27): the CLI decides. Pinned.</summary>
    public const string ClaudeDefaultLabel = "(Claude Code's default)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ClaudeCodeExecutable"/>. Pinned.</summary>
    public const string ClaudeExecutableError = "must be the full path of an existing file, or empty to look for Claude Code";

    /// <summary>One row of the Claude-permissions picker: the level and its hint, padded to eleven (<c>read-only</c> is nine). Pinned.</summary>
    public static string ClaudePermissionLabel(string name) =>
        Markup.Escape(name.PadRight(11)) + Theme.DimMarkup(Claude.ClaudePermission.Describe(name));

    /// <summary>One row of the Claude-effort picker: the word, or <paramref name="blank"/> (<see cref="ClaudeDefaultLabel"/>; the Claude Code advisor tool's own until it was retired, 2026-10-10) for the blank first row. Pinned.</summary>
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

    /// <summary>One row of the advisor-context picker (the Advisors tab's <c>Advisor context</c>; the Claude Code advisor tool's until 2026-10-10): the word and its hint, padded to eight. Pinned.</summary>
    public static string AdvisorContextLabel(string name) =>
        Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(NeonSidekick.Advisors.AdvisorContextMode.Describe(name));

    /// <summary>
    /// How the menu shows <see cref="AppSettingsData.AnthropicApiKey"/> (2026-09-27): <c>(none)</c>, <see cref="ApiKeyEncryptedLabel"/>
    /// for the DPAPI value the menu saves (its blob says nothing worth masking), else <see cref="Mask"/> of a plain one
    /// (a variable, a hand edit). Pinned.
    /// </summary>
    public static string ApiKeyLabel(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? "(none)" : Sql.WindowsCredentials.IsProtected(stored) ? ApiKeyEncryptedLabel : Mask(stored.Trim());

    /// <summary>The Anthropic API key row's value while an encrypted key is saved. Pinned.</summary>
    public const string ApiKeyEncryptedLabel = "(set, encrypted)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.AnthropicApiMaxTokens"/>. Pinned.</summary>
    public static readonly string AnthropicApiMaxTokensRangeError =
        "must be " + AppSettingsData.MinAnthropicApiMaxTokens.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxAnthropicApiMaxTokens.ToString(CultureInfo.InvariantCulture) + " tokens";

    /// <summary>The warning when DPAPI could not encrypt the key and it was saved as typed. Pinned.</summary>
    public static string AnthropicApiKeyPlainWarning(string reason) => $"Anthropic API key saved unencrypted: {reason}.";

    /// <summary>The warning when DPAPI could not encrypt the LLM API key (2026-09-28): it is kept as typed. Pinned.</summary>
    public static string LlmApiKeyPlainWarning(string reason) => $"LLM API key saved unencrypted: {reason}.";

    /// <summary>How the menu shows <see cref="AppSettingsData.AdvisorCallsPerTurn"/> (the Claude Code advisor tool's cap's until 2026-10-10). Pinned.</summary>
    public static string AdvisorCalls(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " call" : " calls");

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.HomeAssistantUrl"/> (2026-09-28): no server, so no Home Assistant tool. Pinned.</summary>
    public const string NoHomeAssistantUrlLabel = "(not set)";

    /// <summary>The value column of the <c>Home Assistant test connection</c> action row (2026-09-28). Pinned.</summary>
    public const string HomeAssistantTestLabel = "Enter to ask the server for its version";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.HomeAssistantAssistAgent"/> (2026-09-28). Pinned.</summary>
    public const string DefaultAssistAgentLabel = "(Home Assistant's default)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.HomeAssistantUrl"/>. Pinned.</summary>
    public const string HomeAssistantUrlError = "must be an http or https URL (http://localhost:8123), or empty";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.HomeAssistantTimeoutSeconds"/>. Pinned.</summary>
    public static string HomeAssistantTimeoutRangeError =>
        "must be " + AppSettingsData.MinHomeAssistantTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxHomeAssistantTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>The warning when DPAPI could not encrypt the token (2026-09-28): it is kept as typed. Pinned.</summary>
    public static string HomeAssistantTokenPlainWarning(string reason) => $"Home Assistant API key saved unencrypted: {reason}.";

    /// <summary>The warning when DPAPI could not encrypt the YouTube API key (2026-10-05): it is kept as typed. Pinned.</summary>
    public static string YouTubeApiKeyPlainWarning(string reason) => $"YouTube API key saved unencrypted: {reason}.";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.YouTubeRegion"/> (2026-10-08): no region asked for. Pinned.</summary>
    public const string NoYouTubeRegionLabel = "(any region)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.YouTubeLanguage"/> (2026-10-08): no language favoured. Pinned.</summary>
    public const string NoYouTubeLanguageLabel = "(any language)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.YouTubeRegion"/> (2026-10-08). Pinned.</summary>
    public const string YouTubeRegionError = "must be two letters (US, GB, DE), or empty for any region";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.YouTubeLanguage"/> (2026-10-08). Pinned.</summary>
    public const string YouTubeLanguageError = "must be a language code (en, es, zh-Hans), or empty for any language";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.YouTubeSearchMaxResults"/>. Pinned.</summary>
    public static readonly string YouTubeSearchMaxResultsRangeError =
        "must be " + AppSettingsData.MinYouTubeSearchMaxResults.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxYouTubeSearchMaxResults.ToString(CultureInfo.InvariantCulture) + " results";

    /// <summary>A row of the action-policy picker: the name padded, what it does dim (2026-09-28).</summary>
    public static string HomeAssistantPolicyLabel(string name) =>
        Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(HomeAssistant.HaPolicy.Describe(name));

    /// <summary>A row of the print-policy picker: the name padded, what it does dim (2026-09-28).</summary>
    public static string PrintPolicyLabel(string name) =>
        Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(Printing.PrintPolicy.Describe(name));

    /// <summary>A row of the PDF-engine picker: the name padded, what it does dim (2026-10-03).</summary>
    public static string PdfEngineLabel(string name) =>
        Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(Pdf.PdfEngine.Describe(name));

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.PrintDefaultPrinter"/> (2026-09-28), and the picker's first row.</summary>
    public static string WindowsDefaultPrinterLabel => OperatingSystem.IsMacOS() ? "(system default)" : Printing.PrintText.Supported ? "(Windows default)" : "(none: printing needs Windows or a Mac)";   // a Mac's since 2026-10-08, CUPS' default

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.PrintFontSize"/>.</summary>
    public static string PrintFontSizeRangeError =>
        "must be " + Printing.PrintLayout.MinFontSize.ToString(CultureInfo.InvariantCulture) + " to " + Printing.PrintLayout.MaxFontSize.ToString(CultureInfo.InvariantCulture) + " points";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ComfyUrl"/> (2026-09-24): no server, so no image tool. Pinned.</summary>
    public const string NoComfyUrlLabel = "(not set)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.TtsHttpUrl"/> (2026-10-05, the default since then, as ComfyUI URL's). Pinned.</summary>
    public const string NoTtsUrlLabel = "(not set)";

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ComfyOutputFolder"/> (2026-09-24): the pictures land in the profile's images folder itself (the working directory until 2026-10-09). Pinned.</summary>
    public const string ComfyOutputHereLabel = "(the profile's images folder)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ComfyUrl"/>. Pinned.</summary>
    public const string ComfyUrlError = "must be an http or https URL (http://host:8188), or empty";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ComfyOutputFolder"/> (under the images folder since 2026-10-09). Pinned.</summary>
    public const string ComfyOutputFolderError = "must be a folder under the profile's images folder (a relative path), or empty";

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

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ImageEditQuality"/> (2026-10-04). Pinned.</summary>
    public static readonly string ImageEditQualityRangeError =
        "must be " + AppSettingsData.MinImageEditQuality.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxImageEditQuality.ToString(CultureInfo.InvariantCulture);

    /// <summary>How the menu shows an empty <see cref="AppSettingsData.ImageEditOutputFolder"/> (2026-10-04): the result lands beside its source. Pinned.</summary>
    public const string ImageEditBesideLabel = "(beside the source)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.ImageEditOutputFolder"/>: a folder under the profile's images folder since 2026-10-10 (the user's ask; the working directory's from 2026-10-04, the Camera output folder's words until it moved, 2026-10-09). Pinned.</summary>
    public const string ImageEditOutputFolderError = "must be a folder under the profile's images folder (a relative path), or empty";

    /// <summary>A row of the image-edit metadata picker: the name padded, what it keeps dim (2026-10-04).</summary>
    public static string ImageEditMetadataLabel(string name) =>
        Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(Images.ImageWords.DescribeMetadata(name));

    /// <summary>A row of the image-edit mode picker: the name padded, where the result goes dim (later on 2026-10-04).</summary>
    public static string ImageEditModeLabel(string name) =>
        Markup.Escape(name.PadRight(20)) + Theme.DimMarkup(Images.ImageWords.DescribeEditMode(name));

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.FileSearchMaxResults"/> (2026-10-01). Pinned.</summary>
    public static readonly string FileSearchMaxResultsRangeError =
        "must be " + AppSettingsData.MinFileSearchMaxResults.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxFileSearchMaxResults.ToString(CultureInfo.InvariantCulture) + " results";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.WebDownloadMaxMegabytes"/> (2026-10-01). Pinned.</summary>
    public static readonly string WebDownloadMaxMegabytesRangeError =
        "must be " + AppSettingsData.MinWebDownloadMaxMegabytes.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxWebDownloadMaxMegabytes.ToString(CultureInfo.InvariantCulture) + " MB";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.QueryResultMaxChars"/> (2026-10-01). Pinned.</summary>
    public static readonly string QueryResultMaxCharsRangeError =
        "must be " + AppSettingsData.MinQueryResultMaxChars.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxQueryResultMaxChars.ToString(CultureInfo.InvariantCulture) + " characters";

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

    /// <summary>The checklists' hint (the code-languages list's first; A and N since 2026-09-29, <see cref="ChecklistButtons"/>). Pinned.</summary>
    public const string ToggleKeys = "Enter / Space = on or off · a = all · n = none · ESC = back";

    /// <summary>
    /// The checklists' first title-row button (2026-09-29, the user's ask, the Folders pane's <c>collapse all</c> its
    /// model): every row ticked. On Show toolbar, Botchat limited skills, Botchat limited tools, SQL connections offered, ComfyUI
    /// workflows offered and Shell code languages. Pinned.
    /// </summary>
    public const string SelectAllButton = "⊞ select all";

    /// <summary>The key that is <see cref="SelectAllButton"/>.</summary>
    public const char SelectAllKey = 'a';

    /// <summary>The checklists' second button (2026-09-29): every row unticked — refused on Shell code languages, where one stays. Pinned.</summary>
    public const string SelectNoneButton = "⊠ select none";

    /// <summary>The key that is <see cref="SelectNoneButton"/>.</summary>
    public const char SelectNoneKey = 'n';

    /// <summary>The checklists' buttons: select all (index 0), select none (index 1).</summary>
    public static readonly IReadOnlyList<MenuButton> ChecklistButtons = [new(SelectAllButton, SelectAllKey), new(SelectNoneButton, SelectNoneKey)];

    private const int SelectAllIndex = 0;
    private const int SelectNoneIndex = 1;

    /// <summary>
    /// Show toolbar's third title-row button (2026-09-29, the user's ask, beside select all and select none): the checklist
    /// back to <see cref="App.ToolbarItems.Defaults"/>, which saves as null so the profile follows a later default. Since
    /// 2026-10-05 SQLite statements allowed's too (the user's ask), back to <see cref="Sqlite.SqliteStatementKinds.Default"/>. Pinned.
    /// </summary>
    public const string DefaultsButton = "⊡ default";

    /// <summary>The key that is <see cref="DefaultsButton"/>.</summary>
    public const char DefaultsKey = 'd';

    /// <summary>The buttons of a checklist with a default (Show toolbar, SQLite statements allowed): <see cref="ChecklistButtons"/>, then default (index 2).</summary>
    public static readonly IReadOnlyList<MenuButton> DefaultChecklistButtons = [.. ChecklistButtons, new(DefaultsButton, DefaultsKey)];

    private const int DefaultsIndex = 2;

    /// <summary>The hint of a checklist with a default: <see cref="ToggleKeys"/> with D (2026-09-29, Show toolbar's; SQLite statements allowed's since 2026-10-05). Pinned.</summary>
    public const string DefaultToggleKeys = "Enter / Space = on or off · a = all · n = none · d = default · ESC = back";

    /// <summary>
    /// Show toolbar's hint (2026-10-12, the user's ask: the home bar picker's move keys on this checklist): <see cref="DefaultToggleKeys"/>
    /// with the move. Shift+Home/End (to the top or the bottom) and Delete (unchecks) work too, but the hint leaves them out to stay
    /// on one row of a 120-cell window; the docs and <c>neon_help</c> name them. Pinned.
    /// </summary>
    public const string ToolbarToggleKeys = "Space = on or off · Shift+↑↓ = move · Shift+←→ = side · + = divider · a = all · n = none · d = default · ESC = back";   // the side since 2026-10-13 (the user's ask: pinned left or right); + = divider later that day, Enter left unnamed to keep one row

    /// <summary>The Tool bar checklist's fourth title-row button (2026-10-13, the user's ask: dividers on the tool bar): a new divider after the cursor's item. Pinned.</summary>
    public const string DividerButton = "+ divider";

    /// <summary>The key that is <see cref="DividerButton"/>.</summary>
    public const char DividerKey = '+';

    /// <summary>The Tool bar checklist's buttons: <see cref="DefaultChecklistButtons"/>, then <see cref="DividerButton"/> (index 3).</summary>
    public static readonly IReadOnlyList<MenuButton> DividerChecklistButtons = [.. DefaultChecklistButtons, new(DividerButton, DividerKey)];

    private const int DividerIndex = 3;

    /// <summary>The Performance bar checklist's look button's index (2026-10-14): after <see cref="DefaultChecklistButtons"/>, where the tool bar's divider is.</summary>
    private const int LookIndex = 3;

    /// <summary>
    /// Show performance bar's hint: <see cref="ToolbarToggleKeys"/>' words, the same page since 2026-10-12 (the user's ask), without
    /// the side (2026-10-13: the bar stays centred). From
    /// 2026-09-30 the page's title row also held the looks (<c>t / g / l = look</c> in the hint, a radio button per look after
    /// default); they went on 2026-10-12, and since 2026-10-14 (the user's ask) one button after default steps through them
    /// (<see cref="PerfBarMode.LookButton"/>, <c>l = look</c>), as the Queue pane's mode button does. Pinned.
    /// </summary>
    public const string PerfBarToggleKeys = "Space = on or off · Shift+↑↓ = move · Shift+←→ = side · a = all · n = none · d = default · l = look · ESC = back";   // Enter left unnamed since the look came (2026-10-14), to keep one row, as the tool bar's   // the side since later on 2026-10-13 (the user's ask: pinned left, center or right)

    /// <summary>The Hint bar checklist's hint (2026-10-12): the toolbar's keys, the same string until the tool bar's dividers (2026-10-13). Pinned.</summary>
    public const string StatusBarToggleKeys = "Enter / Space = on or off · Shift+↑↓ = move · Shift+←→ = side · a = all · n = none · d = default · ESC = back";

    /// <summary>The Toolbar order page's hint (2026-10-12): the move keys and default, nothing to check. Pinned.</summary>
    public const string BarOrderKeys = "Shift+↑↓ = move · Shift+Home/End = top or bottom · d = default · ESC = back";

    /// <summary>The Toolbar order page's title row (2026-10-12, the user's ask: a way back to the default order on the page): <see cref="DefaultsButton"/> alone, index 0.</summary>
    public static readonly IReadOnlyList<MenuButton> BarOrderButtons = [new(DefaultsButton, DefaultsKey)];

    private const int BarOrderDefaultIndex = 0;

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

    /// <summary>The allowed-commands list's hint: Enter removes, P steps the policy (<see cref="CommandPolicyButtons"/>, 2026-10-02 as O, A and Y; P since later on 2026-10-13). Pinned.</summary>
    public const string AllowedCommandsKeys = "Enter = remove · p = policy · ESC = back";

    /// <summary>The question before an allowed prefix goes (2026-10-07, every list's remove asks). Pinned.</summary>
    public static string RemovePrefixQuestion(string prefix) => $"Remove \"{prefix}\" from the allowed commands? It will ask again.";

    /// <summary>The question before a forbidden string goes (2026-10-07). Pinned.</summary>
    public static string RemoveForbiddenQuestion(string entry) => $"Remove \"{entry}\" from the forbidden strings? Commands holding it are no longer refused for it.";

    /// <summary>The allowed-commands footer's last line under a prefix (2026-10-07). Pinned.</summary>
    public const string AllowedCommandNote = "saved for good: a command that starts with this runs without asking";

    /// <summary>The forbidden strings' footer's last line under a string (2026-10-07). Pinned.</summary>
    public const string ForbiddenStringNote = "a command that holds this is refused, whatever the policy says";

    /// <summary>The allowed-commands list's hint while nothing is allowed for good: no prefix to remove, the policy button still there (2026-10-02). Pinned.</summary>
    public const string AllowedCommandsEmptyKeys = "p = policy · ESC = back";

    /// <summary>
    /// The allowed-commands list's first title-row button (2026-10-12, the user's ask: the third policy beside ask and yolo, first):
    /// the policy to <c>off</c>, at once and unasked, as ask is — no shell or script tool is offered from the next turn. ⊘, one cell
    /// and monochrome as the checklists' ⊞ ⊠ ⊡. Pinned.
    /// </summary>
    public const string PolicyOffButton = "⊘ off";


    /// <summary>
    /// The allowed-commands list's ask button, the first until 2026-10-12 (2026-10-02, the user's ask: switch <c>Shell command policy</c> from the
    /// list <c>/cmdlist</c> opened — <c>/tools shell allowed</c> since 2026-10-09 —, without going through the Shell tab's picker): the policy to <c>ask</c>, at once. The bare word:
    /// the toolbar lock's glyph here would be a second lock on the screen, a click on it no click on the toolbar's. A check before
    /// it since 2026-10-03 (the user's ask: every header button a glyph to its left, monochrome as the checklists' ⊞ ⊠ ⊡). Pinned.
    /// </summary>
    public const string PolicyAskButton = "✓ ask";


    /// <summary>The allowed-commands list's last button (2026-10-02): the policy to <c>yolo</c>, after a yes to <see cref="YoloConfirmQuestion"/>; the warning sign before it since 2026-10-03, two spaces after it (later that day, the user's ask: Windows Terminal gives ⚠ one cell, as <see cref="UI.TextCells"/> counts it, but draws it from the colour emoji font two cells wide over the space after it, so the second space is the one that shows). Pinned.</summary>
    public const string PolicyYoloButton = "⚠  yolo";

    /// <summary>The key of the allowed-commands list's policy button (later on 2026-10-13; o, a and y for the three before).</summary>
    public const char PolicyKey = 'p';

    private const int PolicyIndex = 0;

    /// <summary>The policy button's title for <paramref name="policy"/>: its own glyph title (<see cref="PolicyOffButton"/>, <see cref="PolicyAskButton"/>, <see cref="PolicyYoloButton"/>), a hand-edited value as <c>? word</c>. Pinned.</summary>
    public static string PolicyButtonTitle(string? policy) => Shell.CommandPolicy.TryParse(policy, out var mode)
        ? Shell.CommandPolicy.Name(mode) switch { "off" => PolicyOffButton, "ask" => PolicyAskButton, _ => PolicyYoloButton }
        : "? " + (policy ?? "").Trim();

    /// <summary>The policy after <paramref name="policy"/>: off, ask, yolo, off again; a hand-edited value goes to off. Pure; pinned.</summary>
    public static string NextPolicy(string? policy)
    {
        if (!Shell.CommandPolicy.TryParse(policy, out var mode))
        {
            return Shell.CommandPolicy.Names[0];
        }

        int at = Array.IndexOf(Shell.CommandPolicy.Names, Shell.CommandPolicy.Name(mode));
        return Shell.CommandPolicy.Names[(at + 1) % Shell.CommandPolicy.Names.Length];
    }

    /// <summary>
    /// The allowed-commands list's button: from 2026-10-02 the policies as a radio group (<see cref="PolicyOffButton"/> first since
    /// 2026-10-12, then <see cref="PolicyAskButton"/> and <see cref="PolicyYoloButton"/>, the saved one lit); since later on 2026-10-13
    /// (the user's call, <c>/queue</c>'s shape) one button, key P, showing the policy in force (<see cref="PolicyButtonTitle"/>), never
    /// lit, a press stepping to the next (<see cref="NextPolicy"/>). Pinned.
    /// </summary>
    public static IReadOnlyList<MenuButton> CommandPolicyButtons(string policy) => [new(PolicyButtonTitle(policy), PolicyKey)];

    /// <summary>The status line when the press that came to yolo was answered no (later on 2026-10-13): the cycle moves on to off. Pinned.</summary>
    public const string YoloDeclinedNotice = "Yolo declined: Shell command policy: off";

    /// <summary>
    /// The yes/no asked before <see cref="PolicyYoloButton"/> saves (2026-10-02, the user's call: a move into <c>yolo</c> asks
    /// first, from <c>ask</c> or from <c>off</c>; a move to <c>ask</c> never does; reworded later that day, the user's). Pinned.
    /// </summary>
    public const string YoloConfirmQuestion = "Change shell command policy to yolo (all commands accepted)?";

    /// <summary>
    /// The yes/no asked before <c>Shell police</c> goes from on to off (2026-10-02, the user's ask), on its
    /// on/off page wherever that opens — <c>/tools shell police</c> (<c>/police</c> until 2026-10-09) and the picker's P button, Ctrl+Alt+O, the toolbar's officer, the Tools pane's Shell tab; a move
    /// to on never asks; "commands and scripts" since 2026-10-12 (the user's ask: the police check run_command's lines too). Pinned.
    /// </summary>
    public const string PoliceOffConfirmQuestion = "Disable shell police (commands and scripts run unchecked)?";

    /// <summary>The forbidden-strings list's first row, always there (2026-10-03): Enter on it opens the slot to type one into. Pinned.</summary>
    public const string AddForbiddenRow = "+ Add a string…";

    /// <summary>The forbidden-strings list's hint: the top row adds, a string's row removes it. Pinned.</summary>
    public const string ForbiddenKeys = "Enter = add or remove · ESC = back";

    /// <summary>The notice after a string is added: <c>Shell police forbidden strings: rm -rf added</c>. Pinned.</summary>
    public static string ForbiddenAddedNotice(string entry) => FieldName(SettingsField.ShellPoliceForbiddenStrings) + ": " + entry + " added";

    /// <summary>The notice when the typed string is in the list already (case and spacing ignored, as the police matches): nothing saved. Pinned.</summary>
    public static string ForbiddenDuplicateNotice(string entry) => FieldName(SettingsField.ShellPoliceForbiddenStrings) + ": " + entry + " is already in the list";

    /// <summary>The notice after a string is removed: <c>Shell police forbidden strings: rm -rf removed</c>. Pinned.</summary>
    public static string ForbiddenRemovedNotice(string entry) => FieldName(SettingsField.ShellPoliceForbiddenStrings) + ": " + entry + " removed";

    /// <summary>
    /// The forbidden-strings list's title-row button (2026-10-08, the user's ask): the list back to
    /// <see cref="Shell.ForbiddenStrings.Defaults"/> after a yes to <see cref="RestoreForbiddenQuestion"/>, for a profile from before
    /// the defaults shipped or one whose list was edited. Replaces, never merges: a string the user added goes with the rest. Pinned.
    /// </summary>
    public const string RestoreForbiddenButton = "↺ restore defaults";

    /// <summary>The key that is <see cref="RestoreForbiddenButton"/>.</summary>
    public const char RestoreForbiddenKey = 'r';

    /// <summary>The forbidden-strings list's buttons (2026-10-08): <see cref="RestoreForbiddenButton"/> alone. Pinned.</summary>
    public static readonly IReadOnlyList<MenuButton> ForbiddenButtons = [new(RestoreForbiddenButton, RestoreForbiddenKey)];

    /// <summary>The forbidden-strings list's hint on the pane, where the button is (2026-10-08); the prompt keeps <see cref="ForbiddenKeys"/>. Pinned.</summary>
    public const string ForbiddenPaneKeys = "Enter = add or remove · r = restore defaults · ESC = back";

    /// <summary>
    /// The yes/no before <see cref="RestoreForbiddenButton"/> replaces the list (2026-10-08): an empty list is filled, a list with
    /// strings is replaced and the question says what goes. Pinned.
    /// </summary>
    public static string RestoreForbiddenQuestion(int count, int defaults) => count == 0
        ? $"Restore the {defaults} default forbidden strings?"
        : $"Replace the list ({Strings(count)}) with the {defaults} default strings? Any you added are removed.";

    /// <summary>The notice after the defaults are back: <c>Shell police forbidden strings: 49 defaults restored</c>. Pinned.</summary>
    public static string ForbiddenRestoredNotice(int defaults) => FieldName(SettingsField.ShellPoliceForbiddenStrings) + ": " + defaults.ToString(CultureInfo.InvariantCulture) + " defaults restored";

    /// <summary>The notice when the list already is the defaults (case ignored): nothing asked, nothing saved. Pinned.</summary>
    public static string ForbiddenAlreadyDefaultsNotice => FieldName(SettingsField.ShellPoliceForbiddenStrings) + ": already the defaults";

    /// <summary>
    /// The police's on/off page's button (2026-10-03, the user's pick): the forbidden-strings list, opened from wherever that page
    /// opens — <c>/tools shell police</c> (<c>/police</c> until 2026-10-09), Ctrl+Alt+O, the toolbar's officer, the Shell tab's row. The list glyph before it since later on
    /// 2026-10-03. The word the button's title starts with since 2026-10-04 (<see cref="PoliceStringsTitle"/>). Pinned.
    /// </summary>
    public const string PoliceStringsButton = "≡ strings";

    /// <summary>
    /// The strings button's title (2026-10-04, the user's ask: tell a list with strings from an empty one at a glance):
    /// <see cref="PoliceStringsButton"/> with the count, <c>≡ strings (3)</c>, or <c>≡ strings (none)</c>. Pinned.
    /// </summary>
    public static string PoliceStringsTitle(int count) =>
        PoliceStringsButton + " (" + (count == 0 ? "none" : count.ToString(CultureInfo.InvariantCulture)) + ")";

    /// <summary>The key that is <see cref="PoliceStringsButton"/>.</summary>
    public const char PoliceStringsKey = 's';

    /// <summary>The police's on/off page's hint: <see cref="PickKeys"/> with the strings button's key. Pinned.</summary>
    public const string PoliceToggleKeys = "Enter = choose · s = strings · ESC = back";

    /// <summary>
    /// The police's on/off page's one button (2026-10-03): <see cref="PoliceStringsTitle"/> for <paramref name="count"/> strings,
    /// lit while the list holds any and dim while it is empty (2026-10-04, the user's call; it still opens the list, never switches). Pinned.
    /// </summary>
    public static IReadOnlyList<MenuButton> PoliceButtons(int count) => [new(PoliceStringsTitle(count), PoliceStringsKey, count > 0)];

    /// <summary>
    /// The command-policy picker's first button (2026-10-09, the user's call: <c>/cmdlist</c> folded under <c>/tools shell</c>):
    /// the allowed-commands list, ESC there coming back to the picker — the police page's strings button's shape. The word the
    /// title starts with (<see cref="PolicyAllowedTitle"/>). Pinned.
    /// </summary>
    public const string PolicyAllowedButton = "≡ allowed";

    /// <summary><see cref="PolicyAllowedButton"/> with the count: <c>≡ allowed (3)</c>, or <c>≡ allowed (none)</c>. Pinned.</summary>
    public static string PolicyAllowedTitle(int count) =>
        PolicyAllowedButton + " (" + (count == 0 ? "none" : count.ToString(CultureInfo.InvariantCulture)) + ")";

    /// <summary>The key that is <see cref="PolicyAllowedButton"/>.</summary>
    public const char PolicyAllowedKey = 'c';

    /// <summary>The command-policy picker's second button (2026-10-09, <c>/police</c> folded in): the police's on/off page, ESC there coming back. Pinned.</summary>
    public const string PolicyPoliceButton = "≡ police";

    /// <summary><see cref="PolicyPoliceButton"/> with the police's state: <c>≡ police (on)</c>. Pinned.</summary>
    public static string PolicyPoliceTitle(bool on) => PolicyPoliceButton + (on ? " (on)" : " (off)");

    /// <summary>The key that is <see cref="PolicyPoliceButton"/>.</summary>
    public const char PolicyPoliceKey = 'p';

    /// <summary>The command-policy picker's hint on the pane, where the two buttons are (2026-10-09). Pinned.</summary>
    public const string CommandPolicyPageKeys = "Enter = choose · c = allowed · p = police · ESC = back";

    /// <summary>
    /// The command-policy picker's buttons (2026-10-09): <see cref="PolicyAllowedTitle"/> for <paramref name="allowed"/> prefixes, lit
    /// while any is allowed, and <see cref="PolicyPoliceTitle"/>, lit while <paramref name="police"/> is on. Each opens its page. Pinned.
    /// </summary>
    public static IReadOnlyList<MenuButton> CommandPolicyPageButtons(int allowed, bool police) =>
    [
        new(PolicyAllowedTitle(allowed), PolicyAllowedKey, allowed > 0),
        new(PolicyPoliceTitle(police), PolicyPoliceKey, police),
    ];

    /// <summary>
    /// The offered button of the ComfyUI, SQL, Oracle, MySQL, SQLite, PostgreSQL and UNC tool pages (2026-10-05, the user's ask:
    /// the police page's strings button for the "… offered" checklists): the checklist, opened from wherever that page opens —
    /// the toolbar's 🎨 🛢️ 🔮 🐬 🪶 🐘 and UNC item, <c>/tools &lt;group&gt;</c>, the tab's switch row. Two spaces after the glyph
    /// (later that day, the user's ask: the one real space still read as "☑offered" — Windows Terminal gives ☑ one cell, as
    /// <see cref="UI.TextCells"/> counts it, but draws it from the colour emoji font two cells wide over the space after it, as
    /// <see cref="PolicyYoloButton"/>'s ⚠, so the second space is the one that shows). Pinned.
    /// </summary>
    public const string OfferedButton = "☑  offered";

    /// <summary>
    /// The offered button's title: <see cref="OfferedButton"/> with the offered row's own value, <c>☑  offered (2 of 5)</c> or
    /// <c>☑  offered (none of 3)</c>. Pinned.
    /// </summary>
    public static string OfferedTitle(string value) => OfferedButton + " (" + value + ")";

    /// <summary>The key that is <see cref="OfferedButton"/>.</summary>
    public const char OfferedKey = 'o';

    /// <summary>The offered tool pages' hint: <see cref="PickKeys"/> with the offered button's key (2026-10-05). Pinned.</summary>
    public const string OfferedToggleKeys = "Enter = choose · o = offered · ESC = back";

    /// <summary>
    /// The offered tool pages' one button (2026-10-05): <see cref="OfferedTitle"/> for the offered row's <paramref name="value"/>,
    /// lit while anything is offered and dim at <c>none of N</c>, as the police's is while its list is empty. Pinned.
    /// </summary>
    public static IReadOnlyList<MenuButton> OfferedButtons(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return [new(OfferedTitle(value), OfferedKey, !value.StartsWith("none", StringComparison.Ordinal))];
    }

    /// <summary>
    /// The "… offered" checklist a tool switch's page carries a button for (2026-10-05), and its editor — the row's own, unchanged.
    /// Null for every other switch.
    /// </summary>
    private (SettingsField Offered, Func<CancellationToken, Task<bool>> Edit)? OfferedFor(SettingsField toolSwitch) => toolSwitch switch
    {
        SettingsField.ComfyTools => (SettingsField.ComfyWorkflowsOffered, EditComfyOfferedAsync),
        SettingsField.SqlTools => (SettingsField.SqlConnectionsOffered, EditSqlOfferedAsync),
        SettingsField.OracleTools => (SettingsField.OracleConnectionsOffered, EditOracleOfferedAsync),
        SettingsField.MySqlTools => (SettingsField.MySqlConnectionsOffered, EditMySqlOfferedAsync),
        SettingsField.SqliteTools => (SettingsField.SqliteDatabasesOffered, EditSqliteOfferedAsync),
        SettingsField.PostgresTools => (SettingsField.PostgresConnectionsOffered, EditPostgresOfferedAsync),
        SettingsField.UncTools => (SettingsField.UncSharesOffered, EditUncOfferedAsync),
        _ => null,
    };

    /// <summary>
    /// One of the Web tools page's title-row buttons (2026-10-04, the user's ask: "actions at the top", as Memory's and
    /// <c>/cmdlist</c>'s): a <see cref="Web.BrowserMode.Names"/> word with a monochrome glyph before it, as every header button
    /// has since 2026-10-03 — ⇄ the client then the browser, ↓ the client alone, ◎ the browser. Pinned.
    /// </summary>
    public static string WebModeButtonTitle(string name) => name switch
    {
        "auto" => "⇄ auto",
        "http" => "↓ http",
        "browser" => "◎ browser",
        _ => name,
    };

    /// <summary>The key of the Web tools page's browser-mode button (later on 2026-10-13: one cycling button, A / H / B before).</summary>
    public const char WebModeKey = 'm';

    /// <summary>The key of the Web tools page's open-browser button (later on 2026-10-13).</summary>
    public const char OpenBrowserKey = 'o';

    /// <summary>The Web tools page's open-browser button (later on 2026-10-13, the user's place and name): an action, never lit. Pinned.</summary>
    public const string OpenBrowserButton = "↗ Open browser";

    /// <summary>The status line after the open-browser button. Pinned.</summary>
    public const string OpenedBrowserNotice = "Opened the Browser window";

    /// <summary>
    /// The Web tools page's buttons. Until later on 2026-10-13 one per <see cref="Web.BrowserMode.Names"/> (2026-10-04, the user's ask),
    /// a radio group; since then (the user's call, to make room) one button, key M, showing the mode in force with its glyph
    /// (<see cref="WebModeButtonTitle"/>) and always drawn lit, a press stepping to the next (<see cref="Web.BrowserMode.Next"/>); a
    /// hand-edited value that is none of them shows as <c>? word</c>. With <paramref name="openBrowser"/> (the screen has a Browser
    /// window) <see cref="OpenBrowserButton"/>, key O, follows. Pinned.
    /// </summary>
    public static IReadOnlyList<MenuButton> WebModeButtons(string mode, bool openBrowser = false)
    {
        string title = Web.BrowserMode.TryParse(mode, out var engine) ? WebModeButtonTitle(Web.BrowserMode.Name(engine)) : "? " + (mode ?? "").Trim();
        return [new MenuButton(title, WebModeKey, On: true), .. openBrowser ? new[] { new MenuButton(OpenBrowserButton, OpenBrowserKey) } : []];
    }

    /// <summary>The Web tools page's hint: <see cref="PickKeys"/> with the browser-mode button's key (2026-10-04; M since later on 2026-10-13). Pinned.</summary>
    public const string WebToggleKeys = "Enter = choose · m = browser mode · ESC = back";

    /// <summary><see cref="WebToggleKeys"/> with the open-browser button's key, where the screen has a Browser window (later on 2026-10-13). Pinned.</summary>
    public const string WebToggleOpenKeys = "Enter = choose · m = browser mode · o = open browser · ESC = back";

    /// <summary>The notice after a prefix is removed from the allowed list: <c>Shell allowed commands: git push removed</c>. Pinned.</summary>
    public static string PrefixRemovedNotice(string prefix) => FieldName(SettingsField.ShellCommandAllowed) + ": " + prefix + " removed";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.GitLibDiffMaxLines"/>. Pinned.</summary>
    public static readonly string GitLibDiffMaxLinesRangeError =
        "must be " + AppSettingsData.MinGitLibDiffMaxLines.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxGitLibDiffMaxLines.ToString(CultureInfo.InvariantCulture) + " lines";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.GitLibLogMaxCommits"/>. Pinned.</summary>
    public static readonly string GitLibLogMaxCommitsRangeError =
        "must be " + AppSettingsData.MinGitLibLogMaxCommits.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxGitLibLogMaxCommits.ToString(CultureInfo.InvariantCulture) + " commits";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.WebSearchMaxResults"/>. Pinned.</summary>
    public static readonly string WebSearchMaxResultsRangeError =
        "must be " + AppSettingsData.MinWebSearchMaxResults.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxWebSearchMaxResults.ToString(CultureInfo.InvariantCulture) + " results";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.AskMaxQuestions"/>. Pinned.</summary>
    public static readonly string AskMaxQuestionsRangeError =
        "must be " + AppSettingsData.MinAskMaxQuestions.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxAskMaxQuestions.ToString(CultureInfo.InvariantCulture) + " questions";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.AskMaxChoices"/>. Pinned.</summary>
    public static readonly string AskMaxChoicesRangeError =
        "must be " + AppSettingsData.MinAskMaxChoices.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxAskMaxChoices.ToString(CultureInfo.InvariantCulture) + " choices";

    /// <summary>One row of the browser-mode picker: the mode and its hint (padded to eight: <c>browser</c> is seven, since the words changed on 2026-10-13). Pinned.</summary>
    public static string BrowserModeLabel(string name) =>
        Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(Web.BrowserMode.Describe(name));

    /// <summary>One row of the botcheck-window picker (2026-10-13): the mode and its hint (padded to ten: <c>automatic</c> is nine). Pinned.</summary>
    public static string BotcheckWindowLabel(string name) =>
        Markup.Escape(name.PadRight(10)) + Theme.DimMarkup(Web.BotcheckWindow.Describe(name));

    /// <summary>One row of the Web browser privacy picker (later on 2026-10-13): the mode and its hint, padded to fourteen (<c>clear-history</c> is thirteen). Pinned.</summary>
    public static string BrowserPrivacyLabel(string name) =>
        Markup.Escape(name.PadRight(14)) + Theme.DimMarkup(Viewer.BrowserPrivacy.Describe(name));

    /// <summary>One row of the Web open links in picker (later still on 2026-10-13): the choice and its hint, padded to sixteen (<c>default-browser</c> is fifteen).</summary>
    public static string WebOpenLinksInLabel(string name) =>
        Markup.Escape(name.PadRight(16)) + Theme.DimMarkup(Web.OpenLinksIn.Describe(name));

    /// <summary>One row of the network-mode picker: the mode and its hint (padded to nine: <c>internet</c> is eight, since <c>local_area_network</c> became <c>lan</c> on 2026-10-13). Pinned.</summary>
    public static string NetworkModeLabel(string name) =>
        Markup.Escape(name.PadRight(9)) + Theme.DimMarkup(Web.NetworkMode.Describe(name));

    /// <summary>One row of the queue-mode picker (2026-10-09): the mode and its hint (padded to ten: <c>automatic</c> is nine). Pinned.</summary>
    public static string QueueModeLabel(string name) =>
        Markup.Escape(name.PadRight(10)) + Theme.DimMarkup(QueueMode.Describe(name));

    /// <summary>One row of the queue-cancel-mode picker: the mode and its hint (padded to six: <c>drain</c> and <c>empty</c> are five). Pinned.</summary>
    public static string QueueCancelModeLabel(string name) =>
        Markup.Escape(name.PadRight(6)) + Theme.DimMarkup(QueueCancelMode.Describe(name));

    /// <summary>One row of the botchat-LLM-mode picker: the mode and its hint (padded to seven: <c>single</c> is six). Pinned.</summary>
    public static string BotChatLlmModeLabel(string name) =>
        Markup.Escape(name.PadRight(7)) + Theme.DimMarkup(App.BotChatLlmMode.Describe(name));

    /// <summary>One row of the Botchat multi-embedded picker (later on 2026-09-29): the mode padded to fourteen (<c>parent-server</c> is thirteen), then its hint, dim. Pinned.</summary>
    public static string BotChatMultiEmbeddedLabel(string name) =>
        Markup.Escape(name.PadRight(14)) + Theme.DimMarkup(App.BotChatMultiEmbedded.Describe(name));

    /// <summary>One row of the reasoning-estimate picker (2026-09-29): the mode and its hint (padded to ten: <c>tokenize</c> is eight). Pinned.</summary>
    public static string ReasoningEstimateLabel(string name) =>
        Markup.Escape(name.PadRight(10)) + Theme.DimMarkup(Markup.Escape(Llm.ReasoningEstimates.Describe(name)));

    /// <summary>One row of the mid-turn-usage picker: the mode and its hint (padded to eleven: <c>last-known</c> is ten). Pinned.</summary>
    public static string MidTurnUsageLabel(string name) =>
        Markup.Escape(name.PadRight(11)) + Theme.DimMarkup(MidTurnUsageMode.Describe(name));

    /// <summary>One row of the STT-destination picker (2026-10-02): the mode and its hint (padded to six: <c>draft</c> is five). Pinned.</summary>
    public static string SttDestinationLabel(string name) =>
        Markup.Escape(name.PadRight(6)) + Theme.DimMarkup(SttDestinationMode.Describe(name));

    /// <summary>One row of the botchat-image-mode picker: the mode and its hint (padded to eleven: <c>autonomous</c> is ten). Pinned.</summary>
    public static string BotChatImageModeLabel(string name) =>
        Markup.Escape(name.PadRight(11)) + Theme.DimMarkup(App.BotChatImageMode.Describe(name));

    /// <summary>One row of the botchat-img2img-mode picker: the mode and its hint (padded to thirteen: <c>chat-history</c> is twelve). Pinned.</summary>
    public static string BotChatImg2ImgModeLabel(string name) =>
        Markup.Escape(name.PadRight(13)) + Theme.DimMarkup(App.BotChatImg2ImgMode.Describe(name));

    /// <summary>One row of the botchat image mention mode picker (2026-10-13): the word, then its hint dimmed. Pinned.</summary>
    public static string BotChatImageMentionModeLabel(string name) =>
        Markup.Escape(name.PadRight(10)) + Theme.DimMarkup(App.BotChatImageMentionMode.Describe(name));

    /// <summary>One row of the memory-mode picker (2026-10-04): the mode and its hint (padded to twelve: <c>read-write</c> is ten). Pinned.</summary>
    public static string MemoryModeLabel(string name) =>
        Markup.Escape(name.PadRight(12)) + Theme.DimMarkup(App.MemoryMode.Describe(name));

    /// <summary>One row of the botchat-memory-mode picker (2026-10-04): the mode and its hint (padded to fifteen: <c>shared-parent</c> is thirteen). Pinned.</summary>
    public static string BotChatMemoryModeLabel(string name) =>
        Markup.Escape(name.PadRight(15)) + Theme.DimMarkup(App.BotChatMemoryMode.Describe(name));

    /// <summary>One row of the welcome-splash picker: the mode and its hint (padded to nine: <c>fullsize</c> and <c>disabled</c> are eight). Pinned.</summary>
    public static string WelcomeSplashModeLabel(string name) =>
        Markup.Escape(name.PadRight(9)) + Theme.DimMarkup(UI.SplashMode.Describe(name));

    /// <summary>One row of the Hints bar picker (2026-10-14): the word and its note (padded to eight: <c>always</c> is six).</summary>
    public static string HintsBarLabel(string name) =>
        Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(UI.HintsBarMode.Describe(name));

    /// <summary>One row of the menus-max-height picker: the height and its hint (padded to fifteen: <c>three-quarters</c> is fourteen). Pinned.</summary>
    public static string MenuMaxHeightLabel(string name) =>
        Markup.Escape(name.PadRight(15)) + Theme.DimMarkup(UI.MenuHeight.Describe(name));

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

    /// <summary>One row of the @-mention-folder-mode picker: the mode and its hint, padded to seven (<c>remain</c> is six, since 2026-10-13). Pinned.</summary>
    public static string MentionFolderModeLabel(string name) =>
        Markup.Escape(name.PadRight(7)) + Theme.DimMarkup(Files.MentionFolderMode.Describe(name));

    /// <summary>One row of the file-browser-mode picker: the mode and its hint, padded to twelve (<c>show-hidden</c> is eleven). Pinned.</summary>
    public static string FileBrowserModeLabel(string name) =>
        Markup.Escape(name.PadRight(12)) + Theme.DimMarkup(Files.FileBrowserMode.Describe(name));

    /// <summary>One row of the scan-mode picker: the mode and its hint, padded to six (<c>local</c> is five, since 2026-10-13). Pinned.</summary>
    public static string LlmScanModeLabel(string name) =>
        Markup.Escape(name.PadRight(6)) + Theme.DimMarkup(Llm.LlmScanMode.Describe(name));

    /// <summary>One row of the TTS-source picker: the source and its hint, padded to eleven (<c>in-process</c> is ten). Pinned.</summary>
    public static string TtsSourceLabel(string name) =>
        Markup.Escape(name.PadRight(11)) + Theme.DimMarkup(Speech.TtsSource.Describe(name));

    /// <summary>One row of the thumbnail-size picker: the size and its box (padded to nine: <c>fullsize</c> is eight). Pinned.</summary>
    public static string ImageThumbnailSizeLabel(string name) =>
        Markup.Escape(name.PadRight(9)) + Theme.DimMarkup(ThumbnailSize.Describe(name));

    /// <summary>One row of the theme picker: the name and its note (padded to ten: the longest names are nine). Pinned.</summary>
    public static string ThemeLabel(string name) => ThemeLabel(name, ThemeLibrary.All);

    /// <summary>As <see cref="ThemeLabel(string)"/> among <paramref name="themes"/> (2026-10-01): padded to ten, or past the longest name when a user theme's is longer.</summary>
    public static string ThemeLabel(string name, IReadOnlyList<ThemePalette> themes)
    {
        ArgumentNullException.ThrowIfNull(themes);
        int width = Math.Max(10, themes.Max(t => t.Name.Length) + 1);
        return Markup.Escape(name.PadRight(width)) + Theme.DimMarkup(ThemeName.Describe(name, themes));
    }

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
        SettingsField.LlmPictureKeep => data.LlmPictureKeep.ToString(CultureInfo.InvariantCulture),
        SettingsField.LlmPictureMegabytes => data.LlmPictureMegabytes.ToString(CultureInfo.InvariantCulture),
        SettingsField.ReflectionWindow => data.ReflectionWindow.ToString(CultureInfo.InvariantCulture),
        SettingsField.ReflectionMinToolCalls => data.ReflectionMinToolCalls.ToString(CultureInfo.InvariantCulture),
        SettingsField.ReflectionMaxRequests => data.ReflectionMaxRequests.ToString(CultureInfo.InvariantCulture),
        SettingsField.ReflectionCooldownMinutes => data.ReflectionCooldownMinutes.ToString(CultureInfo.InvariantCulture),
        SettingsField.LlmAutoCompactPercent => data.LlmAutoCompactPercent.ToString(CultureInfo.InvariantCulture),
        SettingsField.LlmMaxTurns => data.LlmMaxTurns > 0 ? data.LlmMaxTurns.ToString(CultureInfo.InvariantCulture) : LlmMaxTurnsAutoLabel,
        SettingsField.LlmMaxToolIterations => data.LlmMaxToolIterations.ToString(CultureInfo.InvariantCulture),
        SettingsField.PlanGapPasses => data.PlanGapPasses.ToString(CultureInfo.InvariantCulture),
        SettingsField.FileTreeMaxLength => data.FileTreeMaxLength.ToString(CultureInfo.InvariantCulture),
        SettingsField.WorkingDirectory => data.WorkingDirectory,
        SettingsField.WebBrowserPath => data.WebBrowserPath,
        SettingsField.WebSearxngUrl => data.WebSearxngUrl,
        SettingsField.WebBrowserHomePage => data.WebBrowserHomePage,
        SettingsField.ComfyUrl => data.ComfyUrl,
        SettingsField.ClaudeCodeExecutable => data.ClaudeCodeExecutable,
        SettingsField.ClaudeCodeModel => data.ClaudeCodeModel,
        SettingsField.AdvisorCallsPerTurn => data.AdvisorCallsPerTurn.ToString(CultureInfo.InvariantCulture),
        SettingsField.AdvisorMaxToolIterations => data.AdvisorMaxToolIterations.ToString(CultureInfo.InvariantCulture),
        SettingsField.AdvisorThreadPairs => data.AdvisorThreadPairs.ToString(CultureInfo.InvariantCulture),
        SettingsField.AnthropicApiMaxTokens => data.AnthropicApiMaxTokens.ToString(CultureInfo.InvariantCulture),
        SettingsField.OpenAIApiMaxTokens => data.OpenAIApiMaxTokens.ToString(CultureInfo.InvariantCulture),
        SettingsField.OpenAIApiOrganization => data.OpenAIApiOrganization,
        SettingsField.OpenAIApiProject => data.OpenAIApiProject,
        SettingsField.EmbeddedContextSize => data.EmbeddedContextSize.ToString(CultureInfo.InvariantCulture),
        SettingsField.EmbeddedGpuLayers => data.EmbeddedGpuLayers,
        SettingsField.EmbeddedVramBudget => data.EmbeddedVramBudget == NeonSidekick.EmbeddedLlm.EmbeddedVramBudget.Off ? NeonSidekick.EmbeddedLlm.EmbeddedVramBudget.OffWord : data.EmbeddedVramBudget.ToString(CultureInfo.InvariantCulture),

        // The key is never put back on the line: typing replaces it, empty clears it (the LLM API key refuses empty).
        SettingsField.LlmApiKey => "",
        SettingsField.AnthropicApiKey => "",
        SettingsField.OpenAIApiKey => "",
        SettingsField.HomeAssistantToken => "",
        SettingsField.YouTubeApiKey => "",
        SettingsField.YouTubeRegion => data.YouTubeRegion,
        SettingsField.YouTubeLanguage => data.YouTubeLanguage,
        SettingsField.HomeAssistantUrl => data.HomeAssistantUrl,
        SettingsField.HomeAssistantAssistAgent => data.HomeAssistantAssistAgent,
        SettingsField.DockerEnginePipe => data.DockerEnginePipe,
        SettingsField.DockerServerStopTimeoutSeconds => data.DockerServerStopTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.DockerServerPostStopDelaySeconds => data.DockerServerPostStopDelaySeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.DockerServerReadyTimeoutSeconds => data.DockerServerReadyTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.HomeAssistantTimeoutSeconds => data.HomeAssistantTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.PrintFontSize => data.PrintFontSize.ToString(CultureInfo.InvariantCulture),
        SettingsField.ComfyOutputFolder => data.ComfyOutputFolder,
        SettingsField.ComfyTimeoutSeconds => data.ComfyTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.ComfyMaxPicturesPerCall => data.ComfyMaxPicturesPerCall.ToString(CultureInfo.InvariantCulture),
        SettingsField.DraftEditor => data.DraftEditor,
        SettingsField.ImageEditor => data.ImageEditor,
        SettingsField.Theme => data.Theme,
        SettingsField.FileViewImageMaxPerCall => data.FileViewImageMaxPerCall.ToString(CultureInfo.InvariantCulture),
        SettingsField.FileSearchMaxResults => data.FileSearchMaxResults.ToString(CultureInfo.InvariantCulture),
        SettingsField.ImageEditQuality => data.ImageEditQuality.ToString(CultureInfo.InvariantCulture),
        SettingsField.ImageEditOutputFolder => data.ImageEditOutputFolder,
        SettingsField.PastedImagesFolder => data.PastedImagesFolder,
        SettingsField.McpConnectTimeoutSeconds => data.McpConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.WebSearchMaxResults => data.WebSearchMaxResults.ToString(CultureInfo.InvariantCulture),
        SettingsField.YouTubeSearchMaxResults => data.YouTubeSearchMaxResults.ToString(CultureInfo.InvariantCulture),
        SettingsField.WebDownloadMaxMegabytes => data.WebDownloadMaxMegabytes.ToString(CultureInfo.InvariantCulture),
        SettingsField.ToolCollapseCount => data.ToolCollapseCount.ToString(CultureInfo.InvariantCulture),
        SettingsField.CodeCollapseCount => data.CodeCollapseCount.ToString(CultureInfo.InvariantCulture),
        SettingsField.DiffMaxLines => data.DiffMaxLines.ToString(CultureInfo.InvariantCulture),
        SettingsField.DiffCollapseCount => data.DiffCollapseCount.ToString(CultureInfo.InvariantCulture),
        SettingsField.GitLibDiffMaxLines => data.GitLibDiffMaxLines.ToString(CultureInfo.InvariantCulture),
        SettingsField.ShellTimeoutSeconds => data.ShellTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.ShellForegroundCapSeconds => data.ShellForegroundCapSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.ShellOutputMaxChars => data.ShellOutputMaxChars.ToString(CultureInfo.InvariantCulture),
        SettingsField.ShellCodeTimeoutSeconds => data.ShellCodeTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.ShellCodeMaxToolCalls => data.ShellCodeMaxToolCalls.ToString(CultureInfo.InvariantCulture),
        SettingsField.GitLibLogMaxCommits => data.GitLibLogMaxCommits.ToString(CultureInfo.InvariantCulture),
        SettingsField.SqlQueryMaxRows => data.SqlQueryMaxRows.ToString(CultureInfo.InvariantCulture),
        SettingsField.SqlQueryTimeoutSeconds => data.SqlQueryTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.QueryResultMaxChars => data.QueryResultMaxChars.ToString(CultureInfo.InvariantCulture),
        SettingsField.OracleQueryMaxRows => data.OracleQueryMaxRows.ToString(CultureInfo.InvariantCulture),
        SettingsField.OracleQueryTimeoutSeconds => data.OracleQueryTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.MySqlQueryMaxRows => data.MySqlQueryMaxRows.ToString(CultureInfo.InvariantCulture),
        SettingsField.MySqlQueryTimeoutSeconds => data.MySqlQueryTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.SqliteQueryMaxRows => data.SqliteQueryMaxRows.ToString(CultureInfo.InvariantCulture),
        SettingsField.SqliteQueryTimeoutSeconds => data.SqliteQueryTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.PostgresQueryMaxRows => data.PostgresQueryMaxRows.ToString(CultureInfo.InvariantCulture),
        SettingsField.PostgresQueryTimeoutSeconds => data.PostgresQueryTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.GitLibEmail => data.GitLibEmail,
        SettingsField.GitLibName => data.GitLibName,
        SettingsField.ObsidianVault => data.ObsidianVault,
        SettingsField.AskMaxQuestions => data.AskMaxQuestions.ToString(CultureInfo.InvariantCulture),
        SettingsField.CameraWatchSeconds => data.CameraWatchSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsField.CameraOutputFolder => data.CameraOutputFolder,
        SettingsField.ScreenOutputFolder => data.ScreenOutputFolder,
        SettingsField.CameraWatchThreshold => data.CameraWatchThreshold.ToString(CultureInfo.InvariantCulture),
        SettingsField.CameraWatchMinGapSeconds => data.CameraWatchMinGapSeconds.ToString(CultureInfo.InvariantCulture),
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
    public static string FieldLabel(SettingsField field, AppSettingsData data, string profileDirectory, string? overriddenBy, int width, string? locatedBrowser = null, string? name = null, bool changed = false)
    {
        // A value that is not the default reads in the secondary accent (2026-10-04, the UI review; the footer says so in words).
        string row = Markup.Escape((name ?? FieldName(field)).PadRight(width)) + Theme.ColorMarkup(changed ? Theme.Secondary : Theme.Ink, FieldValue(field, data, profileDirectory, locatedBrowser));
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
    /// <paramref name="open"/> (2026-09-30) starts on that row's tab with the cursor on it and runs its edit at once, as Enter
    /// would: the app's start opens Embedded models so. ESC from the edit leaves the pane on the settings list at that row.
    /// </summary>
    public Task<SettingsChanges> ShowAsync(CancellationToken cancellationToken, bool midTurn, SettingsField? open = null, string? words = null) =>
        ShowAsync(cancellationToken, ReplyRun.Of(midTurn), open, words);

    /// <summary>
    /// <see cref="ShowAsync(CancellationToken, bool, SettingsField?, string?)"/> under a reply asked live (2026-10-12, <see cref="ReplyRun"/>): the
    /// rows refused while it runs open when it ends, the pane saying so (<see cref="ReplyEndedNotice"/>), and the changes come back for the
    /// screen to carry out as at the idle line.
    /// </summary>
    public async Task<SettingsChanges> ShowAsync(CancellationToken cancellationToken, ReplyRun reply, SettingsField? open = null, string? words = null)
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
        int tab = 0;   // a SettingsTab: SettingsTabs places it on the strip (2026-10-10)
        int cursor = 0;
        _reply = reply;
        _stripSubs = new int[StripTabs.Count];   // every visit finds each group on its first tab
        try
        {
            if (!string.IsNullOrWhiteSpace(words))
            {
                // /settings <words> (2026-10-04): the search, or the changed settings; ESC there closes the menu.
                changes = string.Equals(words.Trim(), ChangedWord, StringComparison.OrdinalIgnoreCase)
                    ? await ChangedAsync(cancellationToken).ConfigureAwait(false)
                    : await SearchAsync(words.Trim(), cancellationToken).ConfigureAwait(false);
                return ReferenceEquals(Theme.Current, themeBefore) ? changes : changes | SettingsChanges.Theme;
            }

            while (true)
            {
                var saved = _settings.Current;
                var picked = open is { } first && Locate(first, saved) is { } located
                    ? located
                    : await PickSettingAsync(saved, tab, cursor, cancellationToken).ConfigureAwait(false);
                open = null;
                if (picked is not var (field, page, row, flip, search))
                {
                    return ReferenceEquals(Theme.Current, themeBefore) ? changes : changes | SettingsChanges.Theme;
                }

                if (search is not null)
                {
                    // A letter typed on a tab (2026-10-04): the search, then the tab again — unless the profile changed under it.
                    tab = TabOf(page);
                    cursor = row;
                    changes |= await SearchAsync(search, cancellationToken).ConfigureAwait(false);
                    if (changes.HasFlag(SettingsChanges.Profile))
                    {
                        return ReferenceEquals(Theme.Current, themeBefore) ? changes : changes | SettingsChanges.Theme;
                    }

                    continue;
                }

                tab = TabOf(page);
                cursor = row;
                if (_reply.Running && RefusedMidTurn(field))
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

                if (flip && !IsToggle(field))
                {
                    continue;   // Space on a row that is no switch: nothing (2026-10-04); Enter opens its page
                }

                // A group's row picks under its crumb (2026-10-10, /tools' groups' shape): Settings › Servers › LLM reasoning. The pane's
                // tabbed page only: the flat list has no tabs, and its tab 0 is the Client group since later that day.
                string root = Root;
                Root = page.Tabs is not null && GroupCrumb((SettingsTab)tab) is var crumb && crumb != Title ? crumb : root;
                bool edited;
                try
                {
                    edited = flip
                        ? await FlipAsync(field, cancellationToken).ConfigureAwait(false)
                        : await EditAsync(field, saved, page, row, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Root = root;
                }
                if (_embeddedLlmCleared)
                {
                    // The model in use removed from the catalog (2026-09-29): its URL and model cleared, so the LLM reconnects — to none.
                    _embeddedLlmCleared = false;
                    changes |= SettingsChanges.Llm;
                }

                if (edited)
                {
                    if (field == SettingsField.EmbeddedModels)
                    {
                        // A use or an install (2026-09-29): the pane closes and the screen takes the model from here —
                        // a download wants the transcript's spinner, not a pane.
                        return ReferenceEquals(Theme.Current, themeBefore) ? changes : changes | SettingsChanges.Theme;
                    }

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
            _reply = ReplyRun.Idle;
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
    /// closes with the answer, so what follows goes to the transcript — unless <paramref name="close"/> is false (2026-10-14,
    /// the user's ask: removing a store or an embedded model closed the menu it was asked from, and the menu then grew back
    /// from the input line): a question asked from inside a visit, whose host closes the pane, the page held at the visit's
    /// height (<see cref="PickProfileAsync(string, string, bool, CancellationToken)"/>'s split). Callers check
    /// <see cref="CanShowMenus"/> first and fall back to a typed confirmation where menus cannot open.
    /// </summary>
    public async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken, bool close = true)
    {
        ArgumentNullException.ThrowIfNull(question);
        var page = new MenuPage(question, ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
        int? picked = close
            ? await PickOnceAsync(page, 0, cancellationToken).ConfigureAwait(false)
            : await PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
        return picked == 1;
    }

    /// <summary>
    /// <see cref="ConfirmAsync"/> over a page of the caller's own rows (2026-10-09, the shortcut offer's three answers): the row
    /// picked from the top, or null for ESC; on the pane, or as a prompt without it (where the page's hotkeys do nothing).
    /// </summary>
    public Task<int?> ChooseAsync(MenuPage page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        return PickOnceAsync(page, 0, cancellationToken);
    }

    /// <summary>
    /// One pick from the settings list: on the pane the tabbed page (<see cref="SettingsTabs"/>)
    /// opened on <paramref name="tab"/>, elsewhere the flat list (<see cref="SettingsPage"/>) as a
    /// prompt. The field picked, the page it was picked from (its <see cref="MenuPage.Tab"/> is the
    /// tab shown at Enter) and the row within that page; null for ESC.
    /// </summary>
    private async Task<(SettingsField Field, MenuPage Page, int Row, bool Flip, string? Search)?> PickSettingAsync(AppSettingsData saved, int tab, int cursor, CancellationToken cancellationToken)
    {
        if (_pane.Enabled)
        {
            while (true)
            {
                var tabbed = SettingsTabs(saved, _settings.ProfileName, tab);
                if (await _pane.PickAsync(tabbed, cursor, cancellationToken, stale: _reply.Stale).ConfigureAwait(false) is not { } pick)
                {
                    return null;
                }

                // Each group's sub-tab as the pane left it (2026-10-10): ←/→ move them with no pick.
                var subs = _pane.ShownSubs;
                for (int s = 0; s < _stripSubs.Length && s < subs.Count; s++)
                {
                    _stripSubs[s] = subs[s];
                }

                if (pick.Refresh)
                {
                    // The reply under the pane ended (2026-10-12): the page again where it stood, every row open, the status line saying so.
                    Sink.Notice(ReplyEndedNotice);
                    (tab, cursor) = ((int)TabAt(pick.Tab, pick.Sub), pick.Row);
                    continue;
                }

                // The page on the tab (and the group's sub-tab) the pane ended on, so a typed edit under it keeps that tab's rows.
                var shown = pick.Tab == tabbed.Tab && pick.Sub == tabbed.Sub ? tabbed : MenuPage.Tabbed(Title, WithSub(tabbed.Tabs!, pick.Tab, pick.Sub), pick.Tab, TabKeys) with { Footer = tabbed.Footer };
                if (pick.Filter is { Length: > 0 } typed)
                {
                    return (SettingsField.Profile, shown, pick.Row, false, typed);   // a letter typed: the search (2026-10-04), the tab kept to come back to
                }

                int leaf = (int)TabAt(pick.Tab, pick.Sub);
                if (TabRows(leaf)[pick.Row] is { } field)
                {
                    return (field, shown, pick.Row, pick.Toggle, null);
                }

                (tab, cursor) = (leaf, pick.Row);   // a heading is never a stop; nothing to do there
            }
        }

        var page = SettingsPage(saved, _settings.ProfileName);
        if (await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return null;
        }

        return (Fields[row], page, row, false, null);
    }

    /// <summary>
    /// Where <paramref name="field"/> sits as <see cref="PickSettingAsync"/> would hand it back — the pane's tab page and the row in it,
    /// or the prompt host's list and the row there — or null for a field neither shows (2026-09-30, <see cref="ShowAsync(CancellationToken, bool, SettingsField?)"/>'s open).
    /// </summary>
    private (SettingsField Field, MenuPage Page, int Row, bool Flip, string? Search)? Locate(SettingsField field, AppSettingsData saved)
    {
        if (_pane.Enabled)
        {
            for (int tab = 0; tab < TabFields.Count; tab++)
            {
                int row = RowOf(tab, field);
                if (row >= 0)
                {
                    return (field, SettingsTabs(saved, _settings.ProfileName, tab), row, false, null);
                }
            }

            return null;
        }

        int index = Array.IndexOf(Fields, field);
        return index < 0 ? null : (field, SettingsPage(saved, _settings.ProfileName), index, false, null);
    }

    /// <summary>The settings list as the prompt host shows it: one row per field from <paramref name="saved"/>, the profile row from the loaded name.</summary>
    private MenuPage SettingsPage(AppSettingsData saved, string profile)
    {
        string? located = _locateBrowser("");
        var rows = new string[Fields.Length];
        for (int i = 0; i < Fields.Length; i++)
        {
            var f = Fields[i];
            rows[i] = f == SettingsField.Profile ? ProfileLabel(profile, _settings.ProfileDirectory) : LiveLabel(f, saved, LabelWidth, located);
        }

        return new MenuPage(Title, rows, TitleKeys);
    }

    /// <summary>
    /// The settings list as the pane shows it: the <see cref="TabFields"/> under the <see cref="TabTitles"/> strip, each tab padded to its own column, opened on
    /// <paramref name="tab"/> (a <see cref="SettingsTab"/>). Since 2026-10-10 (the user's pick) the strip is <see cref="StripTitles"/>: the six server tabs
    /// are sub-tabs of Servers and TTS and STT of Voice on the second strip (<see cref="MenuTab.SubTabs"/>, <c>/tools</c>' groups' shape), each with its own
    /// band, the group's badge counting all of theirs; a group not opened on shows the sub-tab it was left on (<see cref="_stripSubs"/>).
    /// </summary>
    private MenuPage SettingsTabs(AppSettingsData saved, string profile, int tab)
    {
        var (at, shown) = PlaceOf((SettingsTab)tab);
        var tabs = new MenuTab[StripTitles.Count];
        for (int s = 0; s < tabs.Length; s++)
        {
            var held = StripTabs[s];
            tabs[s] = held.Count == 1
                ? Leaf(held[0])
                : new MenuTab(StripTitles[s], [])
                {
                    SubTabs = held.Select(Leaf).ToList(),
                    Sub = s == at ? shown : Math.Clamp(_stripSubs[s], 0, held.Count - 1),
                    Badge = ChangedBadge(held.Sum(t => TabFields[(int)t].Count(f => IsChanged(f, saved, _settings.ProfileDirectory)))),
                };
        }

        return MenuPage.Tabbed(Title, tabs, at, SettingsTabKeys) with { Footer = (s, row) => TabRows((int)TabAt(s, 0))[row] is { } field ? FieldFooter(field, _settings.Current) : null };

        // A letter typed on a tab starts the settings search (2026-10-04): the tab's filter, never shown, hands it over. Each tab says its own rows under the list.
        MenuTab Leaf(SettingsTab t) => FieldsTab(TabTitles[(int)t], TabFields[(int)t], saved, profile, TabSections.GetValueOrDefault(t), shortNames: true) with
        {
            Filter = "",
            Footer = row => row >= 0 && row < TabRows((int)t).Count && TabRows((int)t)[row] is { } field ? FieldFooter(field, _settings.Current) : null,
        };
    }

    /// <summary>
    /// One tab of settings rows for a pane: <paramref name="fields"/> as <see cref="FieldLabel"/> rows padded to
    /// <see cref="LabelWidthOf"/>, the profile row from <paramref name="profile"/> (the loaded name; null reads it
    /// from the store). The seam <see cref="ToolsMenu"/> builds its Ask / Files / Web tabs through (2026-09-19).
    /// </summary>
    internal MenuTab FieldsTab(string title, IReadOnlyList<SettingsField> fields, AppSettingsData saved, string? profile = null, IReadOnlyList<(SettingsField First, string Title)>? sections = null, bool shortNames = false)
    {
        string? located = _locateBrowser("");
        // Short names (2026-10-04) only where every row carries the tab's title: a tab of /settings' own, never a /tools one,
        // whose "Web tools" or "Ask user" would be left a bare "Tools" or "User".
        string? shortTab = shortNames && !KeepsWholeNames(title) && fields.All(f => RowName(f, title) != FieldName(f)) ? title : null;
        int width = LabelWidthOf(fields, shortTab);
        var rows = new List<string>(fields.Count + (sections?.Count ?? 0));
        HashSet<int>? headings = null;
        foreach (var f in fields)
        {
            if (sections?.FirstOrDefault(s => s.First == f).Title is { } heading)
            {
                (headings ??= []).Add(rows.Count);
                rows.Add(SectionRule.Markup(heading));
            }

            rows.Add(f == SettingsField.Profile ? ProfileLabel(profile ?? _settings.ProfileName, _settings.ProfileDirectory, width) : LiveLabel(f, saved, width, located, PaneName(f, shortTab is null ? FieldName(f) : RowName(f, shortTab))));
        }

        // The strip's badge (2026-10-07): the rows here whose value is not the default, as each such row reads in the secondary accent.
        string? badge = ChangedBadge(fields.Count(f => IsChanged(f, saved, _settings.ProfileDirectory)));
        return new MenuTab(title, rows) { Headings = headings, SpaceToggles = true, Badge = badge };
    }

    /// <summary>A row as a plain line, for a console without the pane: <c>Web browser mode: chromium</c> (the <see cref="SavedNotice"/> shape). The seam <see cref="ToolsMenu"/> prints its settings tabs through.</summary>
    internal string PlainRow(SettingsField field, AppSettingsData saved) =>
        FieldName(field) + ": " + (EmbeddedValue(field, saved) is { } embedded ? Markup.Remove(embedded) : FieldValue(field, saved, _settings.ProfileDirectory, field == SettingsField.WebBrowserPath ? _locateBrowser("") : null));

    /// <summary>
    /// A row as <see cref="FieldLabel(SettingsField, AppSettingsData, string, string?, int, string?)"/> draws it, except the
    /// embedded model's two rows that read the disk and the machine (2026-09-29, <see cref="EmbeddedValue"/>) when an embedded
    /// model is offered.
    /// </summary>
    private string LiveLabel(SettingsField field, AppSettingsData saved, int width, string? located, string? name = null)
    {
        if (EmbeddedValue(field, saved) is not { } embedded)
        {
            return FieldLabel(field, saved, _settings.ProfileDirectory, _overriddenBy(field), width, located, name, IsChanged(field, saved, _settings.ProfileDirectory));
        }

        string row = Markup.Escape((name ?? FieldName(field)).PadRight(width)) + Theme.ColorMarkup(Theme.Ink, Markup.Escape(embedded));
        return _overriddenBy(field) is { } by ? row + Theme.DimMarkup($"  (overridden by {by})") : row;
    }

    /// <summary>
    /// The live value of the <c>Embedded models</c> row (<c>2 of 4 installed (9.4 GB)</c>) and the <c>Embedded backend</c> row
    /// (<c>auto (cuda: NVIDIA driver 610.88)</c>), from <see cref="EmbeddedLlm"/>; null for every other row, or with none.
    /// </summary>
    private string? EmbeddedValue(SettingsField field, AppSettingsData saved)
    {
        if (EmbeddedLlm is not { } embedded)
        {
            return null;
        }

        return field switch
        {
            SettingsField.EmbeddedModels => EmbeddedModelsSummary(embedded),
            SettingsField.EmbeddedBackend => NeonSidekick.EmbeddedLlm.EmbeddedLlmText.BackendRowValue(saved.EmbeddedBackend, embedded.Backend(EffectiveNow())),
            _ => null,
        };
    }

    private static string EmbeddedModelsSummary(NeonSidekick.EmbeddedLlm.IEmbeddedLlm embedded)
    {
        var installed = embedded.Catalog.Where(m => embedded.State(m).IsInstalled).ToList();
        return NeonSidekick.EmbeddedLlm.EmbeddedLlmText.ModelsRowValue(installed.Count, embedded.Catalog.Count, installed.Sum(NeonSidekick.EmbeddedLlm.EmbeddedModelCatalog.TotalBytes));
    }

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

    /// <summary>
    /// <see cref="PickAsync"/> for the checklists (2026-09-29, the user's ask): the page with <see cref="ChecklistButtons"/> on
    /// its title row (or <paramref name="buttons"/>: Show toolbar's add default); the row (the cursor's for a button) and the
    /// button pressed, −1 for Enter or Space on a row; null on ESC. Without the pane (the Spectre prompt) there are no buttons.
    /// </summary>
    private async Task<(int Row, int Button)?> PickChecklistAsync(MenuPage page, int cursor, CancellationToken cancellationToken, IReadOnlyList<MenuButton>? buttons = null)
    {
        if (_pane.Enabled)
        {
            return await _pane.PickAsync(page with { Buttons = buttons ?? ChecklistButtons }, cursor, cancellationToken).ConfigureAwait(false) is { } picked
                ? (picked.Row, picked.Button)
                : null;
        }

        return await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is { } row ? (row, -1) : null;
    }

    /// <summary>
    /// <see cref="PickChecklistAsync"/> for a checklist whose rows are an order (2026-10-12, Show toolbar and Show performance bar,
    /// the home bar picker's move keys): the page's <see cref="MenuPage.Moves"/> on, and a move (<see cref="MenuPick.Move"/>) or a
    /// Delete (<see cref="MenuPick.Remove"/>) handed back beside the row and the button. Without the pane, no buttons and no moves.
    /// </summary>
    private async Task<(int Row, int Button, int Move, bool Remove)?> PickOrderedChecklistAsync(MenuPage page, int cursor, CancellationToken cancellationToken, IReadOnlyList<MenuButton> buttons)
    {
        if (_pane.Enabled)
        {
            return await _pane.PickAsync(page with { Buttons = buttons, Moves = true }, cursor, cancellationToken).ConfigureAwait(false) is { } picked
                ? (picked.Row, picked.Button, picked.Move, picked.Remove)
                : null;
        }

        return await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is { } row ? (row, -1, 0, false) : null;
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
    /// <c>/server</c> after its probe): <paramref name="current"/> is always offered, and the cursor
    /// opens on it. Returns true when the saved model changed; ESC keeps it (<see cref="UnchangedNotice"/>).
    /// Without menus the guard prints.
    ///
    /// <para>A to Z (2026-10-03, the user's ask: "alphabetize the list so it's easier to find things"; <see cref="ModelOrder"/>),
    /// whatever order the server listed them in — <paramref name="current"/> among them in its place, no longer first when the
    /// server does not list it; with no model in use (a server just picked) the cursor opens on the one the server lists first,
    /// so Enter still takes its default. On the pane the list filters as it is typed (<see cref="PickFilteredModelAsync"/>).</para>
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
            ids.Add(current);
        }

        ids = ModelOrder(ids);

        // The cursor on the model in use; with none listed (a new server), on the one the server lists first, its default.
        int cursor = ids.IndexOf(current);
        if (cursor < 0 && listed.ModelIds.Count > 0)
        {
            cursor = ids.IndexOf(listed.ModelIds[0]);
        }

        if (ids.Count == 0)
        {
            // Only reachable with no current id at all; a connected session always has one.
            Flow.Error(listed.Exists ? NoModelsListedError : $"The server did not answer ({listed.Detail}). {NoModelsListedError}");
            return false;
        }

        if (_pane.Enabled)
        {
            return await PickFilteredModelAsync(ids, cursor, cancellationToken).ConfigureAwait(false) is { } chosen ? SaveModel(chosen) : Unchanged();
        }

        var page = new MenuPage(ModelTitle, ids.Select(Markup.Escape).ToList(), KeepKeys);
        int? picked = await PickOnceAsync(page, cursor, cancellationToken).ConfigureAwait(false);
        return picked is { } i ? SaveModel(ids[i]) : Unchanged();
    }

    /// <summary>The footer under the model picker (2026-10-07, the user's ask: a long Hugging Face id is cut at the edge): the whole id, and whether it is the one in use. Pinned.</summary>
    public static MenuFooter ModelFooter(string id, bool inUse) => new(id, inUse ? ModelInUseNote : null);

    /// <summary>The model footer's last line on the model in use. Pinned.</summary>
    public const string ModelInUseNote = "the model in use";

    /// <summary>The model picker's order (2026-10-03, the user's ask): A to Z, case folded, the exact spelling breaking a tie. Pure.</summary>
    public static List<string> ModelOrder(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ThenBy(id => id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Things with a name A to Z, case folded (2026-10-11, the user's ask): the database and UNC panes' checklists, password pickers and
    /// wizards' first pages list their connections and shares so instead of the files' order (the profile's, then the home's). Only the
    /// panes sort; the catalogs, and so the tools, keep the files' order. Stable, so names equal but for case keep their order and the
    /// profile's lists first (the 2026-10-11 review: <see cref="ModelOrder"/>'s exact-spelling tie-break put the home's <c>Prod</c> above
    /// the profile's <c>prod</c>). Pure.
    /// </summary>
    public static List<T> ByName<T>(IEnumerable<T> items, Func<T, string> name)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(name);
        return items.OrderBy(name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The model picker on the pane with the type-to-filter (2026-10-03, the user's ask: "a way to type to filter them down too
    /// like we did in the some other panes"; <see cref="MenuFilter"/>): a typed character narrows the rows to the ids that hold
    /// the text, case folded, the cursor on the first left (Enter takes it); Backspace erases; the first ESC clears the text,
    /// the next keeps the model. The cursor opens on <paramref name="cursor"/>, the model in use. The pick, or null on ESC; the
    /// pane closes as it lands.
    /// </summary>
    private async Task<string?> PickFilteredModelAsync(IReadOnlyList<string> ids, int cursor, CancellationToken cancellationToken)
    {
        string filter = "";
        int inUse = cursor;
        try
        {
            while (true)
            {
                var shown = Enumerable.Range(0, ids.Count).Where(i => MenuFilter.Matches(filter, ids[i], null)).ToList();
                List<string> rows = shown.Count > 0 ? shown.Select(i => Markup.Escape(ids[i])).ToList() : [MenuFilter.NoMatchNameRow(filter)];
                var page = new MenuPage(ModelTitle, rows, MenuFilter.Hint(ModelKeys, filter))
                {
                    Filter = filter,
                    Caption = MenuFilter.CaptionOrNull(filter, shown.Count, ids.Count),
                    Footer = (_, row) => row < shown.Count ? ModelFooter(ids[shown[row]], shown[row] == inUse) : null,   // the whole id (2026-10-07)
                };
                if (await _pane.PickAsync(page, Math.Max(0, shown.IndexOf(cursor)), cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return null;
                }

                if (pick.Filter is { } typed)
                {
                    filter = typed;
                    cursor = -1;   // the first row left
                    continue;
                }

                if (pick.Row >= 0 && pick.Row < shown.Count)
                {
                    return ids[shown[pick.Row]];
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>
    /// The server picker: one row per server that answered (<see cref="ServerLabel"/>), the cursor
    /// on <paramref name="current"/> when it is listed. Returns the pick, or null on ESC — with
    /// <see cref="UnchangedNotice"/> under <see cref="ServerTitle"/>, silently under
    /// <see cref="StartupServerTitle"/> (the <c>LLM:</c> line that follows says what happened).
    /// Without menus the list is printed and nothing is picked. Saving is the caller's
    /// (<see cref="SaveServer"/>): the startup path and <c>/server</c> connect differently.
    /// </summary>
    /// <param name="currentModel">The model in use: every embedded row shares one URL, so the cursor finds the embedded model by its id (2026-09-29).</param>
    public async Task<LlmServer?> PickServerAsync(IReadOnlyList<LlmServer> servers, Uri? current, string title, CancellationToken cancellationToken, string? currentModel = null)
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
            if (current is not null && servers[i].BaseUrl == current
                && (!NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(current) || (servers[i].Result.ModelIds.Count > 0 && string.Equals(servers[i].Result.ModelIds[0], currentModel, StringComparison.OrdinalIgnoreCase))))
            {
                cursor = i;
            }
        }

        string keys = title == StartupServerTitle ? StartupServerKeys : KeepKeys;
        LlmServer? chosen = _pane.Enabled
            ? await PickFilteredServerAsync(servers, title, keys, cursor, cancellationToken).ConfigureAwait(false)
            : await PickOnceAsync(new MenuPage(title, ServerLabels(servers).ToList(), keys), cursor, cancellationToken).ConfigureAwait(false) is { } index ? servers[index] : null;
        if (chosen is null && title == ServerTitle)
        {
            Unchanged();
        }

        return chosen;
    }

    /// <summary>
    /// The server picker with the embedded model filters on its title row (later on 2026-09-29, the user's ask:
    /// <see cref="NeonSidekick.EmbeddedLlm.EmbeddedModelFilter"/>), when it lists an embedded model and the pane is up: a press
    /// thins the embedded rows alone — a server on the network and the Anthropic API always stay (the user's call) — and keeps
    /// the cursor on its row while it is still shown. Every visit starts with none lit. The pane closes as the pick lands.
    /// Sort size (2026-09-30, the user's ask) orders the embedded rows by size among themselves, the others in their places.
    /// </summary>
    private async Task<LlmServer?> PickFilteredServerAsync(IReadOnlyList<LlmServer> servers, string title, string keys, int cursor, CancellationToken cancellationToken)
    {
        // Uncensored lit when the row the pane opens on is an uncensored build (later on 2026-09-30, the user's pick), so it shows.
        var filter = NeonSidekick.EmbeddedLlm.EmbeddedModelFilter.For(
            cursor >= 0 && cursor < servers.Count && NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(servers[cursor].BaseUrl) ? EmbeddedRowModel(servers[cursor]) : null);
        // The embedded filter buttons only while an embedded model is listed; the typed filter always (2026-10-07, the user's ask).
        bool embedded = servers.Any(s => NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(s.BaseUrl));
        var labels = ServerLabels(servers);
        string baseHint = embedded ? keys.Replace(" · ESC", " · " + NeonSidekick.EmbeddedLlm.EmbeddedModelFilter.Keys + " · ESC", StringComparison.Ordinal) : keys;
        string text = "";
        try
        {
            while (true)
            {
                var shown = filter.Arrange(Enumerable.Range(0, servers.Count).Where(i => Passes(servers[i]) && ServerMatches(text, servers[i])).ToList(), i => NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(servers[i].BaseUrl) ? EmbeddedRowModel(servers[i]) : null);
                var page = new MenuPage(title, text.Length > 0 && shown.Count == 0 ? [MenuFilter.NoMatchRow(text)] : FilteredRows(labels, shown), MenuFilter.HintBeforeEsc(baseHint, text, letteredButtons: embedded))
                {
                    Filter = text,
                    Caption = MenuFilter.CaptionOrNull(text, shown.Count, servers.Count),
                    Footer = (_, row) => row < shown.Count ? ServerFooter(servers[shown[row]]) : null,
                };
                if (await _pane.PickAsync(page with { Buttons = embedded ? filter.Buttons() : null }, Math.Max(0, shown.IndexOf(cursor)), cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return null;
                }

                if (pick.Filter is { } typed)
                {
                    text = typed;
                    cursor = -1;   // the first row left
                    continue;
                }

                if (pick.Row >= 0 && pick.Row < shown.Count)
                {
                    cursor = shown[pick.Row];
                }

                if (pick.Button >= 0)
                {
                    filter = filter.Press(pick.Button);
                    continue;
                }

                if (shown.Count > 0 && cursor >= 0)
                {
                    return servers[cursor];
                }
            }
        }
        finally
        {
            _pane.Close();
        }

        bool Passes(LlmServer server) =>
            !NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(server.BaseUrl)
            || (EmbeddedRowModel(server) is { } model ? filter.Matches(model) : !filter.Active);
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
    /// Saves the embedded model <paramref name="id"/> as the LLM URL (the sentinel) and model in one write (2026-09-29): the
    /// URL's notice, then the override reminder when one stands. <see cref="SaveServer"/> would clear the model.
    /// </summary>
    public void SaveEmbeddedModel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Apply(SettingsField.LlmUrl, d =>
        {
            d.LlmUrl = NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.BaseUrl.ToString();
            d.LlmModel = id;
        });
    }

    /// <summary>
    /// The profile picker (<c>/profile</c> with nothing, and the first settings row): every
    /// profile, opened on the loaded one; Enter on another switches it in place
    /// (<see cref="AppSettings.SwitchProfileAsync"/>) and returns true. The caller rebinds what
    /// depends on the profile. Without menus the list is printed and nothing switches.
    /// A temporary profile (<see cref="Profiles.IsTemporary"/>, a <c>_</c> name) is left off unless it is the loaded one
    /// (2026-10-02, the user's ask); <c>/profile _name</c> still switches to it (<see cref="PickerNames"/>). A typed letter or
    /// digit jumps to the next profile starting with it (2026-10-06, the user's ask: as the theme picker does,
    /// <see cref="MenuPage.JumpNames"/>). Each row is led by its profile's avatar since 2026-10-10 (the user's ask,
    /// <see cref="ProfilePickerRow"/>), blanks for none.
    /// </summary>
    public Task<bool> PickProfileAsync(CancellationToken cancellationToken) =>
        PickProfileAsync(ProfileTitle, ProfileKeys, close: true, cancellationToken);

    /// <summary>The profile picker under <paramref name="label"/>; <paramref name="close"/> false keeps the pane open for the settings list it came from.</summary>
    private async Task<bool> PickProfileAsync(string label, string keys, bool close, CancellationToken cancellationToken)
    {
        string current = _settings.ProfileName;
        var names = PickerNames(Profiles.List(_settings.StorageDirectory), current);
        if (!CanShowMenus())
        {
            Flow.Notice(ProfileListLine(names, current));
            return false;
        }

        // Each name led by its profile's avatar (2026-10-10, the user's ask); the type-ahead still reads the bare names.
        var page = new MenuPage(label, ProfilePickerRows(names), keys) { JumpNames = names };
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
    /// The profiles the picker and <c>/profile</c>'s name list offer (2026-10-02, the user's ask): every one but the temporary
    /// ones (<see cref="Profiles.IsTemporary"/>, a <c>_</c> name), the loaded one always (the picker opens on it). Typed by
    /// name, a temporary profile still switches: <see cref="Profiles.Resolve"/> reads the whole list. Pure.
    /// </summary>
    public static IReadOnlyList<string> PickerNames(IReadOnlyList<string> names, string? loaded)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Where(name => !Profiles.IsTemporary(name) || Profiles.NameEquals(name, loaded)).ToList();
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
        if (field == SettingsField.LlmSampling)
        {
            // A door, not a value (2026-09-28): the /sampling pane saves its own edits, so this row reports nothing changed.
            if (SamplingPane is { } open)
            {
                await open(cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        if (field == SettingsField.EmbeddedModels)
        {
            return await PickEmbeddedModelAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ModelAvatar)
        {
            return await PickAvatarAsync("", own: false, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.UserAvatar)
        {
            return await PickUserAvatarAsync("", own: false, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.EmbeddedBackend)
        {
            return await PickEmbeddedBackendAsync(saved, cancellationToken).ConfigureAwait(false);
        }

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

        if (field == SettingsField.ClaudeCodePermissions)
        {
            return await PickClaudePermissionsAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ClaudeCodeEffort)
        {
            return await PickClaudeEffortAsync(saved.ClaudeCodeEffort, cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.ClaudeCodeModel
            && await PickClaudeModelAsync(saved, cancellationToken).ConfigureAwait(false) is { } pickedModel)
        {
            // Null: Other… — the typed edit below, pre-filled with the saved name.
            return pickedModel;
        }

        if (field == SettingsField.AdvisorList)
        {
            return await EditAdvisorListAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.AdvisorContext)
        {
            return await PickAdvisorContextAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.AdvisorEmbedded)
        {
            return await PickAdvisorEmbeddedAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.HomeAssistantActionPolicy)
        {
            return await PickHomeAssistantPolicyAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.PrintActionPolicy)
        {
            return await PickPrintPolicyAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.PrintDefaultPrinter)
        {
            return await PickDefaultPrinterAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.PdfEngine)
        {
            return await PickPdfEngineAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ImageEditMetadata)
        {
            return await PickImageEditMetadataAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ImageEditMode)
        {
            return await PickImageEditModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.HomeAssistantTest)
        {
            return await TestHomeAssistantAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.AnthropicApiKey or SettingsField.OpenAIApiKey)
        {
            return await SetApiKeyAsync(field, page, row, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.HomeAssistantToken)
        {
            return await SetHomeAssistantTokenAsync(page, row, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.YouTubeApiKey)
        {
            return await SetYouTubeApiKeyAsync(page, row, cancellationToken).ConfigureAwait(false);
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

        if (field == SettingsField.ReflectionConfirmMode)
        {
            return await PickReflectionConfirmModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ReflectionInstalledSkills)
        {
            return await PickReflectionInstalledSkillsAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.WebBrowserMode)
        {
            return await PickBrowserModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.WebBrowserBotcheckWindow)
        {
            return await PickBotcheckWindowAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BrowserPrivacy)
        {
            return await PickBrowserPrivacyAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.WebOpenLinksIn)
        {
            return await PickWebOpenLinksInAsync(saved, cancellationToken).ConfigureAwait(false);
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

        if (field == SettingsField.ShellPoliceForbiddenStrings)
        {
            return await EditForbiddenStringsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ShellCodeLanguages)
        {
            return await EditCodeLanguagesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ToolbarItems)
        {
            return await EditToolbarItemsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.StatusBarItems)
        {
            return await EditStatusBarAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SessionNamingMode)
        {
            return await PickSessionNamingModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SessionShowName)
        {
            return await PickSessionShowNameAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.QueueMode)
        {
            return await PickQueueModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.QueueCancelMode)
        {
            return await PickQueueCancelModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.YouTubeVoice)
        {
            return await PickYouTubeVoiceAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.YouTubeSearchOrder or SettingsField.YouTubeSafeSearch)
        {
            return await PickYouTubeFilterAsync(field, saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ShowPerformanceBar)
        {
            return await EditPerfBarAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ShowHomeBar)
        {
            return await EditHomeBarAsync(own: false, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BarOrder)
        {
            return await EditBarOrderAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.EmbeddedHfDownloadType)
        {
            return await PickEmbeddedHfDownloadTypeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatLlmMode)
        {
            return await PickBotChatLlmModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatMultiEmbedded)
        {
            return await PickBotChatMultiEmbeddedAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.LlmReasoningEstimate)
        {
            return await PickReasoningEstimateAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.LlmMidTurnUsage)
        {
            return await PickMidTurnUsageAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SttDestination)
        {
            return await PickSttDestinationAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.CameraShutter or SettingsField.CameraPreview or SettingsField.CameraResolution or SettingsField.CameraDevice or SettingsField.ScreenAsk)
        {
            return await PickCameraAsync(field, saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatImageMode)
        {
            return await PickBotChatImageModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatLimitedComfyWorkflows)
        {
            return await EditBotChatLimitedComfyAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatImageMentionMode)
        {
            return await PickBotChatImageMentionModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatImg2ImgMode)
        {
            return await PickBotChatImg2ImgModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatLimitedSkills)
        {
            return await EditBotChatLimitedSkillsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatLimitedTools)
        {
            return await EditBotChatLimitedToolsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.BotChatMemoryMode)
        {
            return await PickBotChatMemoryModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.MemoryMode)
        {
            return await PickMemoryModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.UserLineStyle)
        {
            // The user line's style (2026-10-04): MemoryMode's picker shape, each row its hint.
            var names = UI.UserLineStyle.Names;
            var styles = new MenuPage(Crumb(FieldName(field)), names.Select(n => Markup.Escape(n.PadRight(8)) + Theme.DimMarkup(UI.UserLineStyle.Describe(n))).ToList(), PickKeys);
            if (await PickAsync(styles, Math.Max(0, Array.IndexOf(names, saved.UserLineStyle)), cancellationToken).ConfigureAwait(false) is not { } index
                || string.Equals(names[index], saved.UserLineStyle, StringComparison.Ordinal))
            {
                return Unchanged();
            }

            string name = names[index];
            Apply(field, d => d.UserLineStyle = name);
            return true;
        }

        if (field == SettingsField.WelcomeSplash)
        {
            return await PickWelcomeSplashModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.HintsBar)
        {
            return await PickHintsBarAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.MenuMaxHeight)
        {
            return await PickMenuMaxHeightAsync(saved, cancellationToken).ConfigureAwait(false);
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


        if (field == SettingsField.PostgresConnectionsOffered)
        {
            return await EditPostgresOfferedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.PostgresAddConnection)
        {
            return await AddPostgresConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.PostgresSetPassword)
        {
            return await SetPostgresPasswordAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.PostgresConnectionsProfile or SettingsField.PostgresConnectionsGlobal)
        {
            // An edit row (2026-10-04), the MySQL tab's.
            OpenPostgresFile(field == SettingsField.PostgresConnectionsProfile
                ? Postgres.PostgresConfigFile.ProfilePath(_settings.ProfileDirectory)
                : Postgres.PostgresConfigFile.GlobalPath(_settings.StorageDirectory));
            return false;
        }


        if (await EditWriteFieldAsync(field, saved, cancellationToken).ConfigureAwait(false) is { } written)
        {
            return written;
        }

        if (field == SettingsField.SqliteMode)
        {
            return await PickSqliteModeAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SqliteStatementsAllowed)
        {
            return await EditSqliteStatementsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SqliteDatabasesOffered)
        {
            return await EditSqliteOfferedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.SqliteAddDatabase)
        {
            return await AddSqliteDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.SqliteDatabasesProfile or SettingsField.SqliteDatabasesGlobal)
        {
            // An edit row (2026-10-04), the MySQL tab's: the file in the editor, made with its commented shape first; nothing saved here.
            OpenSqliteFile(field == SettingsField.SqliteDatabasesProfile
                ? Sqlite.SqliteConfigFile.ProfilePath(_settings.ProfileDirectory)
                : Sqlite.SqliteConfigFile.GlobalPath(_settings.StorageDirectory));
            return false;
        }


        if (field == SettingsField.OracleConnectionsOffered)
        {
            return await EditOracleOfferedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.OracleAddConnection)
        {
            return await AddOracleConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.OracleSetPassword)
        {
            return await SetOraclePasswordAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.OracleConnectionsProfile or SettingsField.OracleConnectionsGlobal)
        {
            // An edit row (2026-09-30), the SQL tab's: the file in the editor, made with its commented shape first; nothing saved here.
            OpenOracleFile(field == SettingsField.OracleConnectionsProfile
                ? Oracle.OracleConfigFile.ProfilePath(_settings.ProfileDirectory)
                : Oracle.OracleConfigFile.GlobalPath(_settings.StorageDirectory));
            return false;
        }


        if (field == SettingsField.MySqlConnectionsOffered)
        {
            return await EditMySqlOfferedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.MySqlAddConnection)
        {
            return await AddMySqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.MySqlSetPassword)
        {
            return await SetMySqlPasswordAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.MySqlConnectionsProfile or SettingsField.MySqlConnectionsGlobal)
        {
            // An edit row (2026-09-30), the Oracle tab's: the file in the editor, made with its commented shape first.
            OpenMySqlFile(field == SettingsField.MySqlConnectionsProfile
                ? MySql.MySqlConfigFile.ProfilePath(_settings.ProfileDirectory)
                : MySql.MySqlConfigFile.GlobalPath(_settings.StorageDirectory));
            return false;
        }


        if (field == SettingsField.UncSharesOffered)
        {
            return await EditUncOfferedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.DockerServerContainers)
        {
            return await EditDockerServerContainersAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.DockerServerMatchWords)
        {
            return await EditDockerServerWordsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.ShoppingList)
        {
            // The list lives in the profile's shopping.db: nothing here saves (2026-10-11).
            await ManageShoppingListAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (field == SettingsField.ShoppingStores)
        {
            // The stores live in the profile's shopping.db, not in the settings: nothing here saves (2026-10-11).
            await ManageShoppingStoresAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (field == SettingsField.UncAddShare)
        {
            return await AddUncShareAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field == SettingsField.UncSetPassword)
        {
            return await SetUncPasswordAsync(cancellationToken).ConfigureAwait(false);
        }

        if (field is SettingsField.UncSharesProfile or SettingsField.UncSharesGlobal)
        {
            // An edit row (2026-09-30), the database tabs': the file in the editor, made with its commented shape first.
            OpenUncFile(field == SettingsField.UncSharesProfile
                ? Unc.UncConfigFile.ProfilePath(_settings.ProfileDirectory)
                : Unc.UncConfigFile.GlobalPath(_settings.StorageDirectory));
            return false;
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

        bool allowEmpty = field is SettingsField.LlmUrl or SettingsField.LlmModel or SettingsField.TtsHttpUrl or SettingsField.TtsVoice2 or SettingsField.WorkingDirectory or SettingsField.WebBrowserPath or SettingsField.WebSearxngUrl or SettingsField.WebBrowserHomePage or SettingsField.DraftEditor or SettingsField.ImageEditor or SettingsField.GitLibEmail or SettingsField.GitLibName or SettingsField.ObsidianVault or SettingsField.ComfyUrl or SettingsField.ComfyOutputFolder or SettingsField.ClaudeCodeExecutable or SettingsField.ClaudeCodeModel
            or SettingsField.OpenAIApiOrganization or SettingsField.OpenAIApiProject
            or SettingsField.HomeAssistantUrl or SettingsField.HomeAssistantAssistAgent or SettingsField.DockerEnginePipe or SettingsField.CameraOutputFolder or SettingsField.ScreenOutputFolder or SettingsField.ImageEditOutputFolder or SettingsField.PastedImagesFolder
            or SettingsField.YouTubeRegion or SettingsField.YouTubeLanguage;
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

            case SettingsField.LlmPictureKeep:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pictureKeep) || pictureKeep < AppSettingsData.MinLlmPictureKeep || pictureKeep > AppSettingsData.MaxLlmPictureKeep)
                {
                    Sink.Error($"{FieldName(field)} {LlmPictureKeepRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.LlmPictureKeep = pictureKeep);
                return true;

            case SettingsField.LlmPictureMegabytes:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pictureMegabytes) || pictureMegabytes < AppSettingsData.MinLlmPictureMegabytes || pictureMegabytes > AppSettingsData.MaxLlmPictureMegabytes)
                {
                    Sink.Error($"{FieldName(field)} {LlmPictureMegabytesRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.LlmPictureMegabytes = pictureMegabytes);
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

            case SettingsField.PlanGapPasses:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int passes) || passes < AppSettingsData.MinPlanGapPasses || passes > AppSettingsData.MaxPlanGapPasses)
                {
                    Sink.Error($"{FieldName(field)} {PlanGapPassesRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.PlanGapPasses = passes);
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

            case SettingsField.DiffMaxLines:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int diffRows) || diffRows < AppSettingsData.MinDiffMaxLines || diffRows > AppSettingsData.MaxDiffMaxLines)
                {
                    Sink.Error($"{FieldName(field)} {DiffMaxLinesRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.DiffMaxLines = diffRows);
                return true;

            case SettingsField.DiffCollapseCount:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int diffKeep) || diffKeep < AppSettingsData.MinDiffCollapseCount || diffKeep > AppSettingsData.MaxDiffCollapseCount)
                {
                    Sink.Error($"{FieldName(field)} {DiffCollapseCountRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.DiffCollapseCount = diffKeep);
                return true;

            case SettingsField.WebSearchMaxResults:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hits) || hits < AppSettingsData.MinWebSearchMaxResults || hits > AppSettingsData.MaxWebSearchMaxResults)
                {
                    Sink.Error($"{FieldName(field)} {WebSearchMaxResultsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.WebSearchMaxResults = hits);
                return true;

            case SettingsField.YouTubeSearchMaxResults:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int videos) || videos < AppSettingsData.MinYouTubeSearchMaxResults || videos > AppSettingsData.MaxYouTubeSearchMaxResults)
                {
                    Sink.Error($"{FieldName(field)} {YouTubeSearchMaxResultsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.YouTubeSearchMaxResults = videos);
                return true;

            case SettingsField.WebDownloadMaxMegabytes:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int megabytes) || megabytes < AppSettingsData.MinWebDownloadMaxMegabytes || megabytes > AppSettingsData.MaxWebDownloadMaxMegabytes)
                {
                    Sink.Error($"{FieldName(field)} {WebDownloadMaxMegabytesRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.WebDownloadMaxMegabytes = megabytes);
                return true;

            case SettingsField.GitLibDiffMaxLines:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int diffLines) || diffLines < AppSettingsData.MinGitLibDiffMaxLines || diffLines > AppSettingsData.MaxGitLibDiffMaxLines)
                {
                    Sink.Error($"{FieldName(field)} {GitLibDiffMaxLinesRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.GitLibDiffMaxLines = diffLines);
                return true;

            case SettingsField.GitLibLogMaxCommits:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int logCommits) || logCommits < AppSettingsData.MinGitLibLogMaxCommits || logCommits > AppSettingsData.MaxGitLibLogMaxCommits)
                {
                    Sink.Error($"{FieldName(field)} {GitLibLogMaxCommitsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.GitLibLogMaxCommits = logCommits);
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

            case SettingsField.QueryResultMaxChars:
                if (!int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int queryChars) || queryChars < AppSettingsData.MinQueryResultMaxChars || queryChars > AppSettingsData.MaxQueryResultMaxChars)
                {
                    Sink.Error($"{FieldName(field)} {QueryResultMaxCharsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.QueryResultMaxChars = queryChars);
                return true;

            case SettingsField.OracleQueryMaxRows:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int oracleRows) || oracleRows < AppSettingsData.MinSqlQueryMaxRows || oracleRows > AppSettingsData.MaxSqlQueryMaxRows)
                {
                    Sink.Error($"{FieldName(field)} {SqlQueryMaxRowsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.OracleQueryMaxRows = oracleRows);
                return true;

            case SettingsField.OracleQueryTimeoutSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int oracleTimeout) || oracleTimeout < AppSettingsData.MinSqlQueryTimeoutSeconds || oracleTimeout > AppSettingsData.MaxSqlQueryTimeoutSeconds)
                {
                    Sink.Error($"{FieldName(field)} {SqlQueryTimeoutRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.OracleQueryTimeoutSeconds = oracleTimeout);
                return true;

            case SettingsField.MySqlQueryMaxRows:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mysqlRows) || mysqlRows < AppSettingsData.MinSqlQueryMaxRows || mysqlRows > AppSettingsData.MaxSqlQueryMaxRows)
                {
                    Sink.Error($"{FieldName(field)} {SqlQueryMaxRowsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.MySqlQueryMaxRows = mysqlRows);
                return true;

            case SettingsField.MySqlQueryTimeoutSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mysqlTimeout) || mysqlTimeout < AppSettingsData.MinSqlQueryTimeoutSeconds || mysqlTimeout > AppSettingsData.MaxSqlQueryTimeoutSeconds)
                {
                    Sink.Error($"{FieldName(field)} {SqlQueryTimeoutRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.MySqlQueryTimeoutSeconds = mysqlTimeout);
                return true;

            case SettingsField.SqliteQueryMaxRows:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sqliteRows) || sqliteRows < AppSettingsData.MinSqlQueryMaxRows || sqliteRows > AppSettingsData.MaxSqlQueryMaxRows)
                {
                    Sink.Error($"{FieldName(field)} {SqlQueryMaxRowsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.SqliteQueryMaxRows = sqliteRows);
                return true;

            case SettingsField.SqliteQueryTimeoutSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sqliteTimeout) || sqliteTimeout < AppSettingsData.MinSqlQueryTimeoutSeconds || sqliteTimeout > AppSettingsData.MaxSqlQueryTimeoutSeconds)
                {
                    Sink.Error($"{FieldName(field)} {SqlQueryTimeoutRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.SqliteQueryTimeoutSeconds = sqliteTimeout);
                return true;

            case SettingsField.PostgresQueryMaxRows:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int postgresRows) || postgresRows < AppSettingsData.MinSqlQueryMaxRows || postgresRows > AppSettingsData.MaxSqlQueryMaxRows)
                {
                    Sink.Error($"{FieldName(field)} {SqlQueryMaxRowsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.PostgresQueryMaxRows = postgresRows);
                return true;

            case SettingsField.PostgresQueryTimeoutSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int postgresTimeout) || postgresTimeout < AppSettingsData.MinSqlQueryTimeoutSeconds || postgresTimeout > AppSettingsData.MaxSqlQueryTimeoutSeconds)
                {
                    Sink.Error($"{FieldName(field)} {SqlQueryTimeoutRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.PostgresQueryTimeoutSeconds = postgresTimeout);
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

            case SettingsField.FileSearchMaxResults:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int searchRows) || searchRows < AppSettingsData.MinFileSearchMaxResults || searchRows > AppSettingsData.MaxFileSearchMaxResults)
                {
                    Sink.Error($"{FieldName(field)} {FileSearchMaxResultsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.FileSearchMaxResults = searchRows);
                return true;

            case SettingsField.ImageEditQuality:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int imageQuality) || imageQuality < AppSettingsData.MinImageEditQuality || imageQuality > AppSettingsData.MaxImageEditQuality)
                {
                    Sink.Error($"{FieldName(field)} {ImageEditQualityRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.ImageEditQuality = imageQuality);
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

            case SettingsField.CameraWatchSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int watchSeconds) || watchSeconds < AppSettingsData.MinCameraWatchSeconds || watchSeconds > AppSettingsData.MaxCameraWatchSeconds)
                {
                    Sink.Error($"{FieldName(field)} {CameraWatchSecondsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.CameraWatchSeconds = watchSeconds);
                return true;

            case SettingsField.CameraWatchThreshold:
                if (!int.TryParse(text.TrimEnd('%', ' '), NumberStyles.Integer, CultureInfo.InvariantCulture, out int watchThreshold) || watchThreshold < AppSettingsData.MinCameraWatchThreshold || watchThreshold > AppSettingsData.MaxCameraWatchThreshold)
                {
                    Sink.Error($"{FieldName(field)} {CameraWatchThresholdRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.CameraWatchThreshold = watchThreshold);
                return true;

            case SettingsField.CameraWatchMinGapSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int watchGap) || watchGap < AppSettingsData.MinCameraWatchMinGapSeconds || watchGap > AppSettingsData.MaxCameraWatchMinGapSeconds)
                {
                    Sink.Error($"{FieldName(field)} {CameraWatchMinGapRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.CameraWatchMinGapSeconds = watchGap);
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

            case SettingsField.ClaudeCodeExecutable:
                text = text.Trim('"');
                if (text.Length > 0 && !File.Exists(text))
                {
                    Sink.Error($"{FieldName(field)} {ClaudeExecutableError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.ClaudeCodeExecutable = text.Length == 0 ? "" : Path.GetFullPath(text));
                return true;

            case SettingsField.ClaudeCodeModel:
                // An alias or a full name: whatever the CLI takes, checked by the CLI at the next /claude.
                Apply(field, d => d.ClaudeCodeModel = text);
                return true;

            case SettingsField.AdvisorCallsPerTurn:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int advisorCallsCap) || advisorCallsCap < AppSettingsData.MinAdvisorCallsPerTurn || advisorCallsCap > AppSettingsData.MaxAdvisorCallsPerTurn)
                {
                    Sink.Error($"{FieldName(field)} {AdvisorCallsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.AdvisorCallsPerTurn = advisorCallsCap);
                return true;

            case SettingsField.AdvisorMaxToolIterations:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int advisorRounds) || advisorRounds < 0 || advisorRounds > AppSettingsData.MaxAdvisorToolIterations)
                {
                    Sink.Error($"{FieldName(field)} {AdvisorIterationsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.AdvisorMaxToolIterations = advisorRounds);
                return true;

            case SettingsField.AdvisorThreadPairs:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int advisorPairs) || advisorPairs < AppSettingsData.MinAdvisorThreadPairs || advisorPairs > AppSettingsData.MaxAdvisorThreadPairs)
                {
                    Sink.Error($"{FieldName(field)} {AdvisorPairsRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.AdvisorThreadPairs = advisorPairs);
                return true;

            case SettingsField.EmbeddedContextSize:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int embeddedContext) || !NeonSidekick.EmbeddedLlm.EmbeddedContextSize.IsValid(embeddedContext))
                {
                    Sink.Error($"{FieldName(field)} {NeonSidekick.EmbeddedLlm.EmbeddedContextSize.Error}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.EmbeddedContextSize = embeddedContext);
                return true;

            case SettingsField.EmbeddedGpuLayers:
                if (NeonSidekick.EmbeddedLlm.EmbeddedGpuLayers.Normalize(text) is not { } layers)
                {
                    Sink.Error($"{FieldName(field)} {NeonSidekick.EmbeddedLlm.EmbeddedGpuLayers.Error}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.EmbeddedGpuLayers = layers);
                return true;

            case SettingsField.EmbeddedVramBudget:
                if (NeonSidekick.EmbeddedLlm.EmbeddedVramBudget.Parse(text) is not { } budget)
                {
                    Sink.Error($"{FieldName(field)} {NeonSidekick.EmbeddedLlm.EmbeddedVramBudget.Error}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.EmbeddedVramBudget = budget);
                return true;

            case SettingsField.AnthropicApiMaxTokens:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int claudeMax) || claudeMax < AppSettingsData.MinAnthropicApiMaxTokens || claudeMax > AppSettingsData.MaxAnthropicApiMaxTokens)
                {
                    Sink.Error($"{FieldName(field)} {AnthropicApiMaxTokensRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.AnthropicApiMaxTokens = claudeMax);
                return true;

            case SettingsField.OpenAIApiMaxTokens:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int openAIMax)
                    || (openAIMax != 0 && (openAIMax < AppSettingsData.MinOpenAIApiMaxTokens || openAIMax > AppSettingsData.MaxOpenAIApiMaxTokens)))
                {
                    Sink.Error($"{FieldName(field)} {OpenAIApiMaxTokensRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.OpenAIApiMaxTokens = openAIMax);
                return true;

            case SettingsField.OpenAIApiOrganization:
                Apply(field, d => d.OpenAIApiOrganization = text);
                return true;

            case SettingsField.OpenAIApiProject:
                Apply(field, d => d.OpenAIApiProject = text);
                return true;

            case SettingsField.WebSearxngUrl:
                if (text.Length > 0 && !(Uri.TryCreate(text, UriKind.Absolute, out var searxng) && Web.WebFetcher.IsHttp(searxng)))
                {
                    Sink.Error($"{FieldName(field)} {SearxngUrlError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.WebSearxngUrl = text);
                return true;

            case SettingsField.WebBrowserHomePage:
                if (text.Length > 0 && Viewer.BrowserAddress.Home(text) is null)
                {
                    Sink.Error($"{FieldName(field)} {HomePageError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.WebBrowserHomePage = text);
                return true;

            case SettingsField.HomeAssistantUrl:
                if (text.Length > 0 && !(Uri.TryCreate(text, UriKind.Absolute, out var home) && Web.WebFetcher.IsHttp(home)))
                {
                    Sink.Error($"{FieldName(field)} {HomeAssistantUrlError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.HomeAssistantUrl = text);
                return true;

            case SettingsField.HomeAssistantAssistAgent:
                Apply(field, d => d.HomeAssistantAssistAgent = text);
                return true;

            case SettingsField.YouTubeRegion:
            case SettingsField.YouTubeLanguage:
                bool isRegion = field == SettingsField.YouTubeRegion;
                if ((isRegion ? YouTube.YouTubeSearchFilters.NormalizeRegion(text) : YouTube.YouTubeSearchFilters.NormalizeLanguage(text)) is not { } code)
                {
                    Sink.Error($"{FieldName(field)} {(isRegion ? YouTubeRegionError : YouTubeLanguageError)}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => { if (isRegion) d.YouTubeRegion = code; else d.YouTubeLanguage = code; });
                return true;

            case SettingsField.DockerEnginePipe:
                // Kept as the bare name whatever form was typed (2026-10-02); blank is the default pipe. On a Mac (2026-10-11) the
                // socket's path (unix:// taken off, ~ kept), blank to find it; anything else typed there (a relative path, tcp://, a
                // pipe name) is refused, not saved as "find it" unsaid (code review of 9c145d1).
                if (OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(text) && Docker.DockerSocket.Normalize(text).Length == 0)
                {
                    Sink.Error($"{FieldName(field)} {DockerEngineSocketError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.DockerEnginePipe = OperatingSystem.IsMacOS() ? Docker.DockerSocket.Normalize(text) : Docker.DockerPipe.Normalize(text));
                return true;

            case SettingsField.DockerServerStopTimeoutSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dockerStop) || dockerStop < AppSettingsData.MinDockerServerStopTimeoutSeconds || dockerStop > AppSettingsData.MaxDockerServerStopTimeoutSeconds)
                {
                    Sink.Error($"{FieldName(field)} {DockerServerStopTimeoutRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.DockerServerStopTimeoutSeconds = dockerStop);
                return true;

            case SettingsField.DockerServerPostStopDelaySeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dockerDelay) || dockerDelay < 0 || dockerDelay > AppSettingsData.MaxDockerServerPostStopDelaySeconds)
                {
                    Sink.Error($"{FieldName(field)} {DockerServerPostStopDelayRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.DockerServerPostStopDelaySeconds = dockerDelay);
                return true;

            case SettingsField.DockerServerReadyTimeoutSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dockerReady) || dockerReady < AppSettingsData.MinDockerServerReadyTimeoutSeconds || dockerReady > AppSettingsData.MaxDockerServerReadyTimeoutSeconds)
                {
                    Sink.Error($"{FieldName(field)} {DockerServerReadyTimeoutRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.DockerServerReadyTimeoutSeconds = dockerReady);
                return true;

            case SettingsField.HomeAssistantTimeoutSeconds:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int homeTimeout) || homeTimeout < AppSettingsData.MinHomeAssistantTimeoutSeconds || homeTimeout > AppSettingsData.MaxHomeAssistantTimeoutSeconds)
                {
                    Sink.Error($"{FieldName(field)} {HomeAssistantTimeoutRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.HomeAssistantTimeoutSeconds = homeTimeout);
                return true;

            case SettingsField.PrintFontSize:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int printSize) || printSize < Printing.PrintLayout.MinFontSize || printSize > Printing.PrintLayout.MaxFontSize)
                {
                    Sink.Error($"{FieldName(field)} {PrintFontSizeRangeError}; keeping {EditableValue(field, saved)}.");
                    return false;
                }

                Apply(field, d => d.PrintFontSize = printSize);
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
                // Under the profile's images folder only (the sandbox until 2026-10-09): a rooted path or one climbing out would be refused at every call, so it is refused here once.
                if (Path.IsPathRooted(text) || text.Replace('\\', '/').Split('/').Contains(".."))
                {
                    Sink.Error($"{FieldName(field)} {ComfyOutputFolderError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.ComfyOutputFolder = text);
                return true;

            case SettingsField.CameraOutputFolder:
                // The ComfyUI output folder's rule (2026-10-02): under the images folder only (the sandbox until 2026-10-09), refused here once rather than at every shot.
                if (Path.IsPathRooted(text) || text.Replace('\\', '/').Split('/').Contains(".."))
                {
                    Sink.Error($"{FieldName(field)} {CameraOutputFolderError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.CameraOutputFolder = text);
                return true;

            case SettingsField.ScreenOutputFolder:
                // The Camera output folder's rule (2026-10-04): under the images folder only (the sandbox until 2026-10-09).
                if (Path.IsPathRooted(text) || text.Replace('\\', '/').Split('/').Contains(".."))
                {
                    Sink.Error($"{FieldName(field)} {CameraOutputFolderError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.ScreenOutputFolder = text);
                return true;

            case SettingsField.ImageEditOutputFolder:
                // The Camera output folder's rule (2026-10-04): under the images folder only since 2026-10-10 (the sandbox until then); empty is beside the source.
                if (Path.IsPathRooted(text) || text.Replace('\\', '/').Split('/').Contains(".."))
                {
                    Sink.Error($"{FieldName(field)} {ImageEditOutputFolderError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.ImageEditOutputFolder = text);
                return true;

            case SettingsField.PastedImagesFolder:
                // The Camera output folder's rule (2026-10-09): under the profile's images folder only.
                if (Path.IsPathRooted(text) || text.Replace('\\', '/').Split('/').Contains(".."))
                {
                    Sink.Error($"{FieldName(field)} {CameraOutputFolderError}; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                Apply(field, d => d.PastedImagesFolder = text);
                return true;

            case SettingsField.ImageEditor:
                // A command line, as the draft editor's: a word cmd cannot find shows at the next double-click.
                Apply(field, d => d.ImageEditor = text);
                return true;

            case SettingsField.DraftEditor:
                // A command line, not a path: nothing to check here — a word cmd cannot find shows at the next /draft.
                Apply(field, d => d.DraftEditor = text);
                return true;

            case SettingsField.GitLibEmail:
                // Whatever git accepts (2026-09-21): an address is not checked here, and empty is "not set".
                Apply(field, d => d.GitLibEmail = text);
                return true;

            case SettingsField.GitLibName:
                Apply(field, d => d.GitLibName = text);
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

            case SettingsField.LlmApiKey:
                if (text.Length == 0)
                {
                    Sink.Error($"{FieldName(field)} cannot be empty; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                // Encrypted for this Windows user before it reaches the file (2026-09-28, as the Anthropic API key); the
                // placeholder a keyless server takes is no secret and stays plain. Where DPAPI fails, kept as typed and said so.
                string protectedLlmKey = text;
                if (text != Llm.LlmEndpoint.DefaultApiKey)
                {
                    protectedLlmKey = Settings.SettingsSecrets.Protect(text, out string? llmProtectError);
                    if (llmProtectError is not null)
                    {
                        Sink.Warning(LlmApiKeyPlainWarning(llmProtectError));
                    }
                }

                Apply(field, d => d.LlmApiKey = protectedLlmKey);
                return true;

            case SettingsField.TtsVoice:
                if (text.Length == 0)
                {
                    Sink.Error($"{FieldName(field)} cannot be empty; keeping {FieldValue(field, saved, _settings.ProfileDirectory)}.");
                    return false;
                }

                goto case SettingsField.LlmUrl;

            case SettingsField.LlmUrl:
            case SettingsField.LlmModel:
            case SettingsField.TtsHttpUrl:   // empty allowed since 2026-10-05 (the default): the http source then says it has no URL
                Apply(field, d =>
                {
                    switch (field)
                    {
                        case SettingsField.LlmUrl: d.LlmUrl = text; break;
                        case SettingsField.LlmModel: d.LlmModel = text; break;
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
    private bool PreviewWanted(AppSettingsData saved) => !_reply.Running && saved.TtsVoicePreview && saved.TtsOutput && _speech.IsReady;

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
        var page = new MenuPage(Crumb(FieldName(SettingsField.ClaudeCodePermissions)), Claude.ClaudePermission.Names.Select(ClaudePermissionLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Claude.ClaudePermission.Names, saved.ClaudeCodePermissions), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Claude.ClaudePermission.Names[index];
        Apply(SettingsField.ClaudeCodePermissions, d => d.ClaudeCodePermissions = name);
        return true;
    }

    /// <summary>
    /// The Claude-effort picker under the settings list (<c>Claude Code command effort</c>; <c>Claude Code advisor tool effort</c>'s too
    /// until 2026-10-10): <see cref="ClaudeDefaultLabel"/> first (the CLI's default), then <c>--effort</c>'s words; the saved one under the cursor.
    /// </summary>
    private async Task<bool> PickClaudeEffortAsync(string saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.ClaudeCodeEffort)), Claude.ClaudeEffort.Names.Select(name => ClaudeEffortLabel(name)).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(Claude.ClaudeEffort.Names, saved.Trim().ToLowerInvariant())), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Claude.ClaudeEffort.Names[index];
        Apply(SettingsField.ClaudeCodeEffort, d => d.ClaudeCodeEffort = name);
        return true;
    }

    /// <summary>
    /// The Claude-model picker under the settings list (2026-09-27, the user's ask: pick, don't type; the advisor tool's model had one too
    /// until 2026-10-10): the blank row first (the CLI's default), then <see cref="Claude.ClaudeModels.Aliases"/>, then
    /// <c>Other…</c>; the cursor on the saved alias, the blank row for none, <c>Other…</c> for a full name. True/false as any
    /// picker (saved, or ESC); null for <c>Other…</c>, which the caller turns into the typed edit.
    /// </summary>
    private async Task<bool?> PickClaudeModelAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        string current = saved.ClaudeCodeModel;
        var rows = new List<string> { ClaudeModelLabel("") };
        rows.AddRange(Claude.ClaudeModels.Aliases.Select(alias => ClaudeModelLabel(alias)));
        rows.Add(ClaudeModelOtherLabel(current));
        int other = rows.Count - 1;
        int cursor = string.IsNullOrWhiteSpace(current) ? 0
            : Claude.ClaudeModels.AliasOf(current) is { } alias ? 1 + Array.IndexOf(Claude.ClaudeModels.Aliases, alias)
            : other;
        var page = new MenuPage(Crumb(FieldName(SettingsField.ClaudeCodeModel)), rows, PickKeys);
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
        Apply(SettingsField.ClaudeCodeModel, d => d.ClaudeCodeModel = name);
        return true;
    }

    /// <summary>The action-policy picker under the settings list (2026-09-28): one <see cref="HomeAssistantPolicyLabel"/> row per <see cref="HomeAssistant.HaPolicy.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickHomeAssistantPolicyAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = HomeAssistant.HaPolicy.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.HomeAssistantActionPolicy)), names.Select(HomeAssistantPolicyLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, HomeAssistant.HaPolicy.Resolve(saved.HomeAssistantActionPolicy))), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.HomeAssistantActionPolicy, d => d.HomeAssistantActionPolicy = name);
        return true;
    }

    /// <summary>The print-policy picker under the settings list (2026-09-28): one <see cref="PrintPolicyLabel"/> row per <see cref="Printing.PrintPolicy.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickPrintPolicyAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = Printing.PrintPolicy.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.PrintActionPolicy)), names.Select(PrintPolicyLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, Printing.PrintPolicy.Resolve(saved.PrintActionPolicy))), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.PrintActionPolicy, d => d.PrintActionPolicy = name);
        return true;
    }

    /// <summary>The PDF-engine picker (2026-10-03): one <see cref="PdfEngineLabel"/> row per <see cref="Pdf.PdfEngine.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickPdfEngineAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = Pdf.PdfEngine.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.PdfEngine)), names.Select(PdfEngineLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, Pdf.PdfEngine.Resolve(saved.PdfEngine))), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.PdfEngine, d => d.PdfEngine = name);
        return true;
    }

    /// <summary>The image-edit metadata picker (2026-10-04): one <see cref="ImageEditMetadataLabel"/> row per <see cref="Images.ImageWords.MetadataNames"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickImageEditMetadataAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = Images.ImageWords.MetadataNames;
        var page = new MenuPage(Crumb(FieldName(SettingsField.ImageEditMetadata)), names.Select(ImageEditMetadataLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, Images.ImageWords.MetadataName(saved.ImageEditMetadata))), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.ImageEditMetadata, d => d.ImageEditMetadata = name);
        return true;
    }

    /// <summary>The image-edit mode picker (later on 2026-10-04): one <see cref="ImageEditModeLabel"/> row per <see cref="Images.ImageWords.EditModeNames"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickImageEditModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = Images.ImageWords.EditModeNames;
        var page = new MenuPage(Crumb(FieldName(SettingsField.ImageEditMode)), names.Select(ImageEditModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, Images.ImageWords.EditModeName(saved.ImageEditMode))), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.ImageEditMode, d => d.ImageEditMode = name);
        return true;
    }

    /// <summary>
    /// The default-printer picker (2026-09-28): <see cref="WindowsDefaultPrinterLabel"/> first, then every installed printer (the
    /// Windows default marked), the saved one under the cursor; a saved printer no longer installed stays as a row of its own, so
    /// the picker never drops it silently.
    /// </summary>
    private async Task<bool> PickDefaultPrinterAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var printers = _printers();
        var names = new List<string> { "" };
        names.AddRange(printers.Select(p => p.Name));
        string current = (saved.PrintDefaultPrinter ?? "").Trim();
        if (current.Length > 0 && !names.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(current);
        }

        var rows = names.Select(name => PrinterRow(name, printers)).ToList();
        var page = new MenuPage(Crumb(FieldName(SettingsField.PrintDefaultPrinter)), rows, PickKeys);
        int at = Math.Max(0, names.FindIndex(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase)));
        int? picked = await PickAsync(page, at, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string chosen = names[index];
        Apply(SettingsField.PrintDefaultPrinter, d => d.PrintDefaultPrinter = chosen);
        return true;
    }

    /// <summary>A row of the default-printer picker: the name, with a dim note for the Windows default or one not installed (2026-09-28).</summary>
    public static string PrinterRow(string name, IReadOnlyList<Printing.PrinterInfo> printers)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(printers);
        if (name.Length == 0)
        {
            return Markup.Escape(WindowsDefaultPrinterLabel);
        }

        var found = printers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        // A Mac's printer shows the name System Settings gives it beside the queue's (2026-10-08).
        string described = string.IsNullOrEmpty(found?.Description) ? "" : "  " + found.Description;
        string note = found is null ? "  not installed" : described + (found.IsDefault ? "  " + Printing.PrintText.DefaultMark : "");
        return Markup.Escape(name) + (note.Length > 0 ? Theme.DimMarkup(note) : "");
    }

    /// <summary>
    /// <c>Home Assistant test connection</c> (2026-09-28): the server's version and place with the URL and token as saved, or
    /// the failure, as the status line. Nothing is saved.
    /// </summary>
    private async Task<bool> TestHomeAssistantAsync(CancellationToken cancellationToken)
    {
        var (ok, text) = await _testHomeAssistant(cancellationToken).ConfigureAwait(false);
        if (ok)
        {
            Sink.Notice(text);
        }
        else
        {
            Sink.Error(text);
        }

        return false;
    }

    /// <summary>
    /// <c>Home Assistant API key</c> (2026-09-28): a masked slot on the pane (<see cref="UI.InputLine"/>'s <c>mask</c>), the token
    /// never put back on the line — typing replaces it, empty clears it — then encrypted for this Windows user before it reaches
    /// the file (<see cref="HomeAssistant.HaSession.Protect"/>); where DPAPI fails, kept as typed and said so.
    /// </summary>
    /// <summary>
    /// The YouTube API key (2026-10-05): <see cref="SetHomeAssistantTokenAsync"/>'s masked field, typed or pasted, never put back
    /// on the line; empty clears it. Saved DPAPI-encrypted, kept as typed (with a warning) when DPAPI refuses.
    /// </summary>
    private async Task<bool> SetYouTubeApiKeyAsync(MenuPage page, int row, CancellationToken cancellationToken)
    {
        InputResult result;
        if (_pane.Enabled)
        {
            result = await _pane.EditAsync(page with { Hint = EditKeys }, row, _input, "", allowEmpty: true, cancellationToken, mask: true).ConfigureAwait(false);
        }
        else
        {
            Flow.Notice(PromptTitle(FieldName(SettingsField.YouTubeApiKey), EditKeys));
            result = await _input.ReadAsync("", remember: false, allowEmpty: true, cancellationToken: cancellationToken, escapeCancels: true, mask: true).ConfigureAwait(false);
        }

        if (result is not InputResult.Submitted submitted)
        {
            return Unchanged();
        }

        string stored = SettingsSecrets.Protect(submitted.Text.Trim(), out string? protectError);
        if (protectError is not null)
        {
            Sink.Warning(YouTubeApiKeyPlainWarning(protectError));
        }

        Apply(SettingsField.YouTubeApiKey, d => d.YouTubeApiKey = stored);
        return true;
    }

    private async Task<bool> SetHomeAssistantTokenAsync(MenuPage page, int row, CancellationToken cancellationToken)
    {
        InputResult result;
        if (_pane.Enabled)
        {
            result = await _pane.EditAsync(page with { Hint = EditKeys }, row, _input, "", allowEmpty: true, cancellationToken, mask: true).ConfigureAwait(false);
        }
        else
        {
            Flow.Notice(PromptTitle(FieldName(SettingsField.HomeAssistantToken), EditKeys));
            result = await _input.ReadAsync("", remember: false, allowEmpty: true, cancellationToken: cancellationToken, escapeCancels: true, mask: true).ConfigureAwait(false);
        }

        if (result is not InputResult.Submitted submitted)
        {
            return Unchanged();
        }

        string stored = HomeAssistant.HaSession.Protect(submitted.Text.Trim(), out string? protectError);
        if (protectError is not null)
        {
            Sink.Warning(HomeAssistantTokenPlainWarning(protectError));
        }

        Apply(SettingsField.HomeAssistantToken, d => d.HomeAssistantToken = stored);
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

    /// <summary>A <c>Reflection confirm mode</c> choice and its hint (2026-10-12), the cooldown mode's shape. Pinned.</summary>
    public static string ReflectionConfirmModeLabel(string name) =>
        Markup.Escape(name.PadRight(6)) + Theme.DimMarkup(Skills.ReflectionConfirmMode.Describe(name));   // fourteen until the confirm- prefix went (2026-10-13)

    /// <summary>The confirm-mode picker under the settings list: one <see cref="ReflectionConfirmModeLabel"/> row per <see cref="Skills.ReflectionConfirmMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickReflectionConfirmModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.ReflectionConfirmMode)), Skills.ReflectionConfirmMode.Names.Select(ReflectionConfirmModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(Skills.ReflectionConfirmMode.Names, saved.ReflectionConfirmMode)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Skills.ReflectionConfirmMode.Names[index];
        Apply(SettingsField.ReflectionConfirmMode, d => d.ReflectionConfirmMode = name);
        return true;
    }

    /// <summary>A <c>Reflection edit downloaded skills</c> choice and its hint (2026-10-02), the cooldown mode's shape. Pinned.</summary>
    public static string ReflectionInstalledSkillsLabel(string name) =>
        Markup.Escape(name.PadRight(20)) + Theme.DimMarkup(Skills.ReflectionInstalledSkills.Describe(name));

    /// <summary>The installed-skills picker under the settings list: one <see cref="ReflectionInstalledSkillsLabel"/> row per <see cref="Skills.ReflectionInstalledSkills.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickReflectionInstalledSkillsAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.ReflectionInstalledSkills)), Skills.ReflectionInstalledSkills.Names.Select(ReflectionInstalledSkillsLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(Skills.ReflectionInstalledSkills.Names, saved.ReflectionInstalledSkills), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Skills.ReflectionInstalledSkills.Names[index];
        Apply(SettingsField.ReflectionInstalledSkills, d => d.ReflectionInstalledSkills = name);
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
    /// <c>/tools shell police</c>, the toolbar's officer) — the page its row's Enter opens, under whatever <see cref="Root"/> the caller set.
    /// Police going off asks <see cref="PoliceOffConfirmQuestion"/> on the same pane first (2026-10-02). The police's page carries
    /// <see cref="PoliceButtons"/> (2026-10-03): S opens the forbidden-strings list, ESC there comes back here. The Web tools page
    /// carries <see cref="WebModeButtons"/> (2026-10-04, the user's ask): M steps <c>Web browser mode</c> at once (later on 2026-10-13), and O opens the Browser window, wherever
    /// the page opens — the toolbar's 🌐, <c>/tools web</c>, the Web tab's row. True when the value (or the browser mode) changed.
    /// </summary>
    internal Task<bool> EditToggleAsync(SettingsField field, CancellationToken cancellationToken) =>
        PickToggleAsync(field, _settings.Current, cancellationToken);

    /// <summary>
    /// Memory mode set straight (2026-10-03 as the Memory switch's on and off, the user's ask: the buttons on the 💾 pane and
    /// <c>/memory on|off</c>; three modes since 2026-10-04): the Memory mode row's own save and notice, on the pane's status line
    /// while one is open. False, with <see cref="UnchangedNotice"/>, when it already is.
    /// </summary>
    internal bool SetMemoryMode(MemoryAccess mode)
    {
        string name = App.MemoryMode.Name(mode);
        if (string.Equals(_settings.Current.MemoryMode, name, StringComparison.Ordinal))
        {
            return Unchanged();
        }

        Apply(SettingsField.MemoryMode, data => data.MemoryMode = name);
        return true;
    }

    /// <summary>
    /// Queue mode set straight (2026-10-13, the user's ask: <c>/queue mode automatic|manual|disabled</c>): the row's own save and
    /// notice. False, with <see cref="UnchangedNotice"/>, when it already is.
    /// </summary>
    internal bool SetQueueMode(Queueing mode)
    {
        string name = App.QueueMode.Name(mode);
        if (string.Equals(_settings.Current.QueueMode, name, StringComparison.Ordinal))
        {
            return Unchanged();
        }

        Apply(SettingsField.QueueMode, data => data.QueueMode = name);
        return true;
    }

    /// <summary>
    /// Queue cancel mode set straight (2026-10-13, the user's ask: <c>/queue cancel hold|drain|empty</c>): the row's own save and
    /// notice. False, with <see cref="UnchangedNotice"/>, when it already is.
    /// </summary>
    internal bool SetQueueCancelMode(QueueCancel mode)
    {
        string name = App.QueueCancelMode.Name(mode);
        if (string.Equals(_settings.Current.QueueCancelMode, name, StringComparison.Ordinal))
        {
            return Unchanged();
        }

        Apply(SettingsField.QueueCancelMode, data => data.QueueCancelMode = name);
        return true;
    }

    private async Task<bool> PickToggleAsync(SettingsField field, AppSettingsData saved, CancellationToken cancellationToken)
    {
        // The interrupt is the wake phrase during a reply: it needs the wake word on, and goes off with it.
        if (field == SettingsField.SttInterrupt && !saved.SttInterrupt && !saved.SttWake)
        {
            Sink.Notice(ChatScreen.InterruptNeedsWakeNotice);
            return false;
        }

        bool was = IsOn(field, saved);
        bool modeChanged = false;
        int? picked;
        if (field == SettingsField.ShellPolice)
        {
            // The police's page carries the forbidden-strings button (2026-10-03, the user's pick): the list, then the page again.
            var police = new MenuPage(Crumb(FieldName(field)), [ToggleLabel(field, true), ToggleLabel(field, false)], _pane.Enabled ? PoliceToggleKeys : PickKeys);
            int cursor = was ? 0 : 1;
            while (true)
            {
                // Read again at each pass: the list may have changed under the button since the page was last drawn.
                int strings = Shell.ForbiddenStrings.Sorted(_settings.Current.ShellPoliceForbiddenStrings).Count;
                var pressed = await PickChecklistAsync(police, cursor, cancellationToken, PoliceButtons(strings)).ConfigureAwait(false);
                if (pressed is { Button: >= 0 } button)
                {
                    cursor = button.Row;
                    await EditForbiddenStringsAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                picked = pressed?.Row;
                break;
            }
        }
        else if (field == SettingsField.DockerTools && ManageDocker is { } manage && _pane.Enabled)
        {
            // The Docker tools page carries the manage button (2026-10-11, the user's ask, the police's shape): the bare /docker's
            // container list, then the page again; a logs pick there backs the whole stack out (its line run at the idle line).
            var docker = new MenuPage(Crumb(FieldName(field)), [ToggleLabel(field, true), ToggleLabel(field, false)], DockerToolsKeys);
            int cursor = was ? 0 : 1;
            while (true)
            {
                var pressed = await PickChecklistAsync(docker, cursor, cancellationToken, DockerToolsButtons).ConfigureAwait(false);
                if (pressed is { Button: >= 0 } button)
                {
                    cursor = button.Row;
                    await manage(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                picked = pressed?.Row;
                break;
            }
        }
        else if (field == SettingsField.WebTools)
        {
            // The Web page carries the browser-mode button (2026-10-04, the user's ask; one cycling button since later on 2026-10-13):
            // a press saves the next mode at once, the picker's own save and notice on the status line, and the page comes back with
            // it shown. The open-browser button (later on 2026-10-13, where the screen has a Browser window) opens it on the home page,
            // the page staying and nothing saved.
            var open = OpenBrowser;
            var web = new MenuPage(Crumb(FieldName(field)), [ToggleLabel(field, true), ToggleLabel(field, false)], !_pane.Enabled ? PickKeys : open is null ? WebToggleKeys : WebToggleOpenKeys);
            int cursor = was ? 0 : 1;
            while (true)
            {
                var buttons = WebModeButtons(_settings.Current.WebBrowserMode, open is not null);
                var pressed = await PickChecklistAsync(web, cursor, cancellationToken, buttons).ConfigureAwait(false);
                if (pressed is { Button: >= 0 } button)
                {
                    cursor = button.Row;
                    if (buttons[button.Button].Key == OpenBrowserKey && open is not null)
                    {
                        open();
                        Sink.Notice(OpenedBrowserNotice);
                        continue;
                    }

                    string name = Web.BrowserMode.Next(_settings.Current.WebBrowserMode);
                    Apply(SettingsField.WebBrowserMode, d => d.WebBrowserMode = name);
                    modeChanged = true;
                    continue;
                }

                picked = pressed?.Row;
                break;
            }
        }
        else if (field == SettingsField.CameraTools && CameraWatching is { } watching && SetCameraWatch is { } setWatch)
        {
            // The Camera tool page carries the watch button (2026-10-04, the user's ask): a press starts or stops /camera watch at
            // once, its line on the status line, and the page comes back with the button lit or not. Where there is no camera
            // layer there is no button (the screen leaves both callbacks null). Live, snap and screen came on 2026-10-05 (the user's
            // ask), each run as its chord would be in a pane (CameraChord): live toggles its window with the page open, snap and
            // screen close the pane for the screen to run them, so the picture attaches at the idle line.
            var title = Crumb(FieldName(field));
            int cursor = was ? 0 : 1;
            while (true)
            {
                var chord = CameraChord;
                var buttons = CameraButtons(watching(), chord is null ? null : CameraLive?.Invoke(), snap: chord is not null, screen: chord is not null && ScreenOffered);
                var camera = new MenuPage(title, [ToggleLabel(field, true), ToggleLabel(field, false)], _pane.Enabled ? CameraButtonKeys(buttons) : PickKeys);
                var pressed = await PickChecklistAsync(camera, cursor, cancellationToken, buttons).ConfigureAwait(false);
                if (pressed is { Button: >= 0 } button)
                {
                    cursor = button.Row;
                    string? line = buttons[button.Button].Key switch
                    {
                        CameraLiveKey => CameraLiveLine,
                        CameraSnapKey => CameraSnapLine,
                        CameraScreenKey => ScreenLine,
                        _ => null,
                    };
                    if (line is not null && chord is not null)
                    {
                        if (!chord(line))
                        {
                            return true;   // the pane is closed; the screen runs the line
                        }

                        continue;
                    }

                    var (ok, text) = setWatch(!watching());
                    if (ok)
                    {
                        Sink.Notice(text);
                        modeChanged = true;
                    }
                    else
                    {
                        Sink.Error(text);
                    }

                    continue;
                }

                picked = pressed?.Row;
                break;
            }
        }
        else if (OfferedFor(field) is { } offered && _pane.Enabled)
        {
            // The ComfyUI, SQL, Oracle, MySQL, SQLite, PostgreSQL and UNC pages carry the offered button (2026-10-05, the user's ask,
            // the police's shape): the row's own checklist, then the page again with the count read afresh.
            var page = new MenuPage(Crumb(FieldName(field)), [ToggleLabel(field, true), ToggleLabel(field, false)], OfferedToggleKeys);
            int cursor = was ? 0 : 1;
            while (true)
            {
                string value = FieldValue(offered.Offered, _settings.Current, _settings.ProfileDirectory);
                var pressed = await PickChecklistAsync(page, cursor, cancellationToken, OfferedButtons(value)).ConfigureAwait(false);
                if (pressed is { Button: >= 0 } button)
                {
                    cursor = button.Row;
                    modeChanged |= await offered.Edit(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                picked = pressed?.Row;
                break;
            }
        }
        else if (SettingButtonsFor(field) is { } settingButtons && _pane.Enabled)
        {
            // The File, Print, Screen capture, Obsidian, YouTube and Advisors enabled pages carry their settings' buttons (2026-10-09,
            // the user's ask, the offered button's shape; Advisors' on 2026-10-12): the setting's own editor, then the page again with
            // the values read afresh.
            var page = new MenuPage(Crumb(FieldName(field)), [ToggleLabel(field, true), ToggleLabel(field, false)], SettingButtonKeys(settingButtons));
            int cursor = was ? 0 : 1;
            while (true)
            {
                var buttons = SettingButtons(settingButtons, _settings.Current, _settings.ProfileDirectory);
                var pressed = await PickChecklistAsync(page, cursor, cancellationToken, buttons).ConfigureAwait(false);
                if (pressed is { Button: >= 0 } button)
                {
                    cursor = button.Row;
                    modeChanged |= await EditFromButtonAsync(settingButtons[button.Button].Target, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                picked = pressed?.Row;
                break;
            }
        }
        else
        {
            var page = new MenuPage(Crumb(FieldName(field)), [ToggleLabel(field, true), ToggleLabel(field, false)], PickKeys);
            picked = await PickAsync(page, was ? 0 : 1, cancellationToken).ConfigureAwait(false);
        }

        if (picked is not { } index || (index == 0) == was)
        {
            // A browser mode switched on the Web page is a change of its own, and so is watch mode on the Camera tool page, a
            // checklist changed from an offered button and a setting saved from a setting button: no "unchanged" over its notice.
            return modeChanged || Unchanged();
        }

        return await SetToggleAsync(field, saved, index == 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A toggle flipped by Space on its row (2026-10-04, the UI review: a flip took Enter, an arrow and Enter): saved at once with its
    /// page's notice, under the page's rules — the interrupt needs the wake word, the police going off asks first, the wake word going
    /// off takes the interrupt with it. False for a field that is no toggle, or a flip refused.
    /// </summary>
    internal async Task<bool> FlipAsync(SettingsField field, CancellationToken cancellationToken)
    {
        if (!IsToggle(field))
        {
            return false;
        }

        var saved = _settings.Current;
        if (field == SettingsField.SttInterrupt && !saved.SttInterrupt && !saved.SttWake)
        {
            Sink.Notice(ChatScreen.InterruptNeedsWakeNotice);
            return false;
        }

        return await SetToggleAsync(field, saved, !IsOn(field, saved), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A toggle set to <paramref name="on"/> (its page's pick, or <see cref="FlipAsync"/>): the police's question first, then the save and its notice.</summary>
    private async Task<bool> SetToggleAsync(SettingsField field, AppSettingsData saved, bool on, CancellationToken cancellationToken)
    {
        if (field == SettingsField.ShellPolice && !on)
        {
            // Police off asks first (2026-10-02, the user's ask), on the same pane as the yolo button's question.
            var question = new MenuPage(PoliceOffConfirmQuestion, ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
            if (await PickAsync(question, 0, cancellationToken).ConfigureAwait(false) != 1)
            {
                return Unchanged();
            }
        }

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
            SettingsField.CopyUserPrompt => data.CopyUserPrompt,
            SettingsField.ShowImageThumbnails => data.ShowImageThumbnails,
            SettingsField.FileTreeShowSizes => data.FileTreeShowSizes,
            SettingsField.LlmOfferTools => data.LlmOfferTools,
            SettingsField.LlmUseFunVerbs => data.LlmUseFunVerbs,
            SettingsField.LlmShowThinking => data.LlmShowThinking,
            SettingsField.LlmPreserveThinking => data.LlmPreserveThinking,
            SettingsField.LlmSamplingFromHuggingFace => data.LlmSamplingFromHuggingFace,
            SettingsField.SessionSaveThinking => data.SessionSaveThinking,
            SettingsField.WebTools => data.WebTools,
            SettingsField.GitLibTools => data.GitLibTools,
            SettingsField.ObsidianTools => data.ObsidianTools,
            SettingsField.ObsidianAllowDelete => data.ObsidianAllowDelete,
            SettingsField.SqlTools => data.SqlTools,
            SettingsField.OracleTools => data.OracleTools,
            SettingsField.OraclePercentMention => data.OraclePercentMention,
            SettingsField.MySqlTools => data.MySqlTools,
            SettingsField.MySqlPercentMention => data.MySqlPercentMention,
            SettingsField.SqliteTools => data.SqliteTools,
            SettingsField.SqliteSandboxFiles => data.SqliteSandboxFiles,
            SettingsField.SqlitePercentMention => data.SqlitePercentMention,
            SettingsField.PostgresTools => data.PostgresTools,
            SettingsField.PostgresPercentMention => data.PostgresPercentMention,
            SettingsField.UncTools => data.UncTools,
            SettingsField.UncWrites => data.UncWrites,
            SettingsField.UncStarMention => data.UncStarMention,
            SettingsField.DockerTools => data.DockerTools,
            SettingsField.DockerWrites => data.DockerWrites,
            SettingsField.CameraTools => data.CameraTools,
            SettingsField.CameraKeepInSessions => data.CameraKeepInSessions,
            SettingsField.ScreenTools => data.ScreenTools,
            SettingsField.ScreenPreview => data.ScreenPreview,
            SettingsField.ScreenKeepInSessions => data.ScreenKeepInSessions,
            SettingsField.CameraWatchUnprompted => data.CameraWatchUnprompted,
            SettingsField.BotChatCamera => data.BotChatCamera,
            SettingsField.BotChatRecentContext => data.BotChatRecentContext,
            SettingsField.DockerServers => data.DockerServers,
            SettingsField.DockerServerStopOnExit => data.DockerServerStopOnExit,
            SettingsField.ComfyTools => data.ComfyTools,
            SettingsField.HomeAssistantTools => data.HomeAssistantTools,
            SettingsField.YouTubeTools => data.YouTubeTools,
            SettingsField.YouTubeAutoplay => data.YouTubeAutoplay,
            SettingsField.PrintTools => data.PrintTools,
            SettingsField.ComfyReinforceNegatives => data.ComfyReinforceNegatives,
            SettingsField.ComfyShowPrompts => data.ComfyShowPrompts,
            SettingsField.ComfyCaretMention => data.ComfyCaretMention,
            SettingsField.ComfyPictureStrip => data.ComfyPictureStrip,
            SettingsField.Advisors => data.Advisors,
            SettingsField.AdvisorConfirm => data.AdvisorConfirm,
            SettingsField.AnthropicApi => data.AnthropicApi,
            SettingsField.AnthropicApiPromptCaching => data.AnthropicApiPromptCaching,
            SettingsField.ClaudeCodeServer => data.ClaudeCodeServer,
            SettingsField.OpenAIApi => data.OpenAIApi,
            SettingsField.EmbeddedVision => data.EmbeddedVision,
            SettingsField.EmbeddedVramOnly => data.EmbeddedVramOnly,
            SettingsField.EmbeddedLlmServer => data.EmbeddedLlmServer,
            SettingsField.EmbeddedDrafter => data.EmbeddedDrafter,
            SettingsField.BotChatComfy => data.BotChatComfy,
            SettingsField.BotChatImageAsync => data.BotChatImageAsync,
            SettingsField.BotChatTools => data.BotChatTools,
            SettingsField.BotChatSkills => data.BotChatSkills,
            SettingsField.BotChatMemory => data.BotChatMemory,
            SettingsField.BotChatVision => data.BotChatVision,
            SettingsField.BotChatMultiEmbeddedKill => data.BotChatMultiEmbeddedKill,
            SettingsField.SqlPercentMention => data.SqlPercentMention,
            SettingsField.LlmCompactShowSummary => data.LlmCompactShowSummary,
            SettingsField.TtsVoicePreview => data.TtsVoicePreview,
            SettingsField.FileTools => data.FileTools,
            SettingsField.ImageTools => data.ImageTools,
            SettingsField.ShoppingTools => data.ShoppingTools,
            SettingsField.AskUser => data.AskUser,
            SettingsField.AgentSkills => data.AgentSkills,
            SettingsField.GlobalSkills => data.GlobalSkills,
            SettingsField.ExternalSkills => data.ExternalSkills,
            SettingsField.ProjectFile => data.ProjectFile,
            SettingsField.TranscriptMarkdown => data.TranscriptMarkdown,
            SettingsField.SkillHashMention => data.SkillHashMention,
            SettingsField.ToolsDollarMention => data.ToolsDollarMention,
            SettingsField.ShowFileDiffs => data.ShowFileDiffs,
            SettingsField.McpServers => data.McpServers,
            SettingsField.ReflectionAutoLearn => data.ReflectionAutoLearn,
            SettingsField.ReflectionIncludesSessions => data.ReflectionIncludesSessions,
            SettingsField.ReflectionYieldsToTurns => data.ReflectionYieldsToTurns,
            SettingsField.ReflectionEditsSupportingFiles => data.ReflectionEditsSupportingFiles,
            SettingsField.HideExitAutocomplete => !data.HideExitAutocomplete,   // shown as "Show /exit in autocomplete" since 2026-10-13
            SettingsField.EmojiPicker => data.EmojiPicker,
            SettingsField.CommandTypoIntercept => data.CommandTypoIntercept,
            SettingsField.KeepMessageHistory => data.KeepMessageHistory,
            SettingsField.ShowHeader => data.ShowHeader,
            SettingsField.ShowWorkingDirectory => data.ShowWorkingDirectory,
            SettingsField.ThemedExternalWindows => data.ThemedExternalWindows,
            SettingsField.ThemedBackground => data.ThemedBackground,
            SettingsField.SessionLogging => data.SessionLogging,
            SettingsField.SessionTool => data.SessionTool,
            SettingsField.ShellToolBridge => data.ShellToolBridge,
            SettingsField.ShellPolice => data.ShellPolice,
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
            case SettingsField.CopyUserPrompt: data.CopyUserPrompt = on; break;
            case SettingsField.ShowImageThumbnails: data.ShowImageThumbnails = on; break;
            case SettingsField.FileTreeShowSizes: data.FileTreeShowSizes = on; break;
            case SettingsField.LlmOfferTools: data.LlmOfferTools = on; break;
            case SettingsField.LlmUseFunVerbs: data.LlmUseFunVerbs = on; break;
            case SettingsField.LlmShowThinking: data.LlmShowThinking = on; break;
            case SettingsField.LlmPreserveThinking: data.LlmPreserveThinking = on; break;
            case SettingsField.LlmSamplingFromHuggingFace: data.LlmSamplingFromHuggingFace = on; break;
            case SettingsField.SessionSaveThinking: data.SessionSaveThinking = on; break;
            case SettingsField.WebTools: data.WebTools = on; break;
            case SettingsField.GitLibTools: data.GitLibTools = on; break;
            case SettingsField.ObsidianTools: data.ObsidianTools = on; break;
            case SettingsField.ObsidianAllowDelete: data.ObsidianAllowDelete = on; break;
            case SettingsField.SqlTools: data.SqlTools = on; break;
            case SettingsField.OracleTools: data.OracleTools = on; break;
            case SettingsField.OraclePercentMention: data.OraclePercentMention = on; break;
            case SettingsField.MySqlTools: data.MySqlTools = on; break;
            case SettingsField.MySqlPercentMention: data.MySqlPercentMention = on; break;
            case SettingsField.SqliteTools: data.SqliteTools = on; break;
            case SettingsField.SqliteSandboxFiles: data.SqliteSandboxFiles = on; break;
            case SettingsField.SqlitePercentMention: data.SqlitePercentMention = on; break;
            case SettingsField.PostgresTools: data.PostgresTools = on; break;
            case SettingsField.PostgresPercentMention: data.PostgresPercentMention = on; break;
            case SettingsField.UncTools: data.UncTools = on; break;
            case SettingsField.UncWrites: data.UncWrites = on; break;
            case SettingsField.UncStarMention: data.UncStarMention = on; break;
            case SettingsField.DockerTools: data.DockerTools = on; break;
            case SettingsField.CameraTools: data.CameraTools = on; break;
            case SettingsField.CameraKeepInSessions: data.CameraKeepInSessions = on; break;
            case SettingsField.ScreenTools: data.ScreenTools = on; break;
            case SettingsField.ScreenPreview: data.ScreenPreview = on; break;
            case SettingsField.ScreenKeepInSessions: data.ScreenKeepInSessions = on; break;
            case SettingsField.CameraWatchUnprompted: data.CameraWatchUnprompted = on; break;
            case SettingsField.BotChatCamera: data.BotChatCamera = on; break;
            case SettingsField.BotChatRecentContext: data.BotChatRecentContext = on; break;
            case SettingsField.DockerWrites: data.DockerWrites = on; break;
            case SettingsField.DockerServers: data.DockerServers = on; break;
            case SettingsField.DockerServerStopOnExit: data.DockerServerStopOnExit = on; break;
            case SettingsField.ComfyTools: data.ComfyTools = on; break;
            case SettingsField.HomeAssistantTools: data.HomeAssistantTools = on; break;
            case SettingsField.YouTubeTools: data.YouTubeTools = on; break;
            case SettingsField.YouTubeAutoplay: data.YouTubeAutoplay = on; break;
            case SettingsField.PrintTools: data.PrintTools = on; break;
            case SettingsField.ComfyReinforceNegatives: data.ComfyReinforceNegatives = on; break;
            case SettingsField.ComfyShowPrompts: data.ComfyShowPrompts = on; break;
            case SettingsField.ComfyCaretMention: data.ComfyCaretMention = on; break;
            case SettingsField.ComfyPictureStrip: data.ComfyPictureStrip = on; break;
            case SettingsField.Advisors: data.Advisors = on; break;
            case SettingsField.AdvisorConfirm: data.AdvisorConfirm = on; break;
            case SettingsField.AnthropicApi: data.AnthropicApi = on; break;
            case SettingsField.AnthropicApiPromptCaching: data.AnthropicApiPromptCaching = on; break;
            case SettingsField.ClaudeCodeServer: data.ClaudeCodeServer = on; break;
            case SettingsField.OpenAIApi: data.OpenAIApi = on; break;
            case SettingsField.EmbeddedVision: data.EmbeddedVision = on; break;
            case SettingsField.EmbeddedVramOnly: data.EmbeddedVramOnly = on; break;
            case SettingsField.EmbeddedLlmServer: data.EmbeddedLlmServer = on; break;
            case SettingsField.EmbeddedDrafter: data.EmbeddedDrafter = on; break;
            case SettingsField.BotChatComfy: data.BotChatComfy = on; break;
            case SettingsField.BotChatImageAsync: data.BotChatImageAsync = on; break;
            case SettingsField.BotChatTools: data.BotChatTools = on; break;
            case SettingsField.BotChatSkills: data.BotChatSkills = on; break;
            case SettingsField.BotChatMemory: data.BotChatMemory = on; break;
            case SettingsField.BotChatVision: data.BotChatVision = on; break;
            case SettingsField.BotChatMultiEmbeddedKill: data.BotChatMultiEmbeddedKill = on; break;
            case SettingsField.SqlPercentMention: data.SqlPercentMention = on; break;
            case SettingsField.LlmCompactShowSummary: data.LlmCompactShowSummary = on; break;
            case SettingsField.TtsVoicePreview: data.TtsVoicePreview = on; break;
            case SettingsField.FileTools: data.FileTools = on; break;
            case SettingsField.ImageTools: data.ImageTools = on; break;
            case SettingsField.ShoppingTools: data.ShoppingTools = on; break;
            case SettingsField.AskUser: data.AskUser = on; break;
            case SettingsField.AgentSkills: data.AgentSkills = on; break;
            case SettingsField.GlobalSkills: data.GlobalSkills = on; break;
            case SettingsField.ExternalSkills: data.ExternalSkills = on; break;
            case SettingsField.ProjectFile: data.ProjectFile = on; break;
            case SettingsField.TranscriptMarkdown: data.TranscriptMarkdown = on; break;
            case SettingsField.SkillHashMention: data.SkillHashMention = on; break;
            case SettingsField.ToolsDollarMention: data.ToolsDollarMention = on; break;
            case SettingsField.ShowFileDiffs: data.ShowFileDiffs = on; break;
            case SettingsField.McpServers: data.McpServers = on; break;
            case SettingsField.ReflectionAutoLearn: data.ReflectionAutoLearn = on; break;
            case SettingsField.ReflectionIncludesSessions: data.ReflectionIncludesSessions = on; break;
            case SettingsField.ReflectionYieldsToTurns: data.ReflectionYieldsToTurns = on; break;
            case SettingsField.ReflectionEditsSupportingFiles: data.ReflectionEditsSupportingFiles = on; break;
            case SettingsField.HideExitAutocomplete: data.HideExitAutocomplete = !on; break;   // on shows /exit (2026-10-13)
            case SettingsField.EmojiPicker: data.EmojiPicker = on; break;
            case SettingsField.CommandTypoIntercept: data.CommandTypoIntercept = on; break;
            case SettingsField.KeepMessageHistory: data.KeepMessageHistory = on; break;
            case SettingsField.ShowHeader: data.ShowHeader = on; break;
            case SettingsField.ShowWorkingDirectory: data.ShowWorkingDirectory = on; break;
            case SettingsField.ThemedExternalWindows: data.ThemedExternalWindows = on; break;
            case SettingsField.ThemedBackground: data.ThemedBackground = on; break;
            case SettingsField.SessionLogging: data.SessionLogging = on; break;
            case SettingsField.SessionTool: data.SessionTool = on; break;
            case SettingsField.ShellToolBridge: data.ShellToolBridge = on; break;
            case SettingsField.ShellPolice: data.ShellPolice = on; break;
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
        SettingsField.CopyUserPrompt => on ? "/copy copies user prompts and model replies" : "/copy copies model replies only",
        SettingsField.ShowImageThumbnails => on ? "image thumbnails are shown in the chat transcript" : "no image thumbnails in the chat transcript",
        SettingsField.TranscriptMarkdown => on ? "replies are styled as Markdown in the pane" : "replies stream as plain text",
        SettingsField.LlmOfferTools => on ? "the model gets the tools; a change starts a new conversation" : "no tools at all; a change starts a new conversation",
        SettingsField.LlmUseFunVerbs => on ? "the thinking spinner reads a random verb" : "the spinner says “thinking”",
        SettingsField.LlmShowThinking => on ? "thinking shown in chat" : "thinking not shown in chat",
        SettingsField.LlmPreserveThinking => on ? "every turn's thinking goes back to the server" : "only the current turn's thinking goes back to the server",
        SettingsField.LlmSamplingFromHuggingFace => on ? "the model card's defaults when the server names none (huggingface.co)" : "only the server's own defaults, nothing fetched",
        SettingsField.SessionSaveThinking => on ? "thinking is saved with the session" : "thinking is not saved with the session",
        SettingsField.TtsOutput => on ? "replies are read aloud" : "replies are text only",
        SettingsField.TtsVoicePreview => on ? "the voice pickers speak the highlighted voice" : "the voice pickers are silent",
        SettingsField.SttInput => on ? "the push-to-talk key records a spoken message" : "the microphone is off",
        SettingsField.SttWake => on ? "the wake phrase starts a listen at the idle line" : "only the push-to-talk key listens",
        SettingsField.SttInterrupt => on ? "the wake phrase during a spoken reply stops it" : "a spoken reply plays to its end",
        SettingsField.AskUser => on ? "ask user enabled" : "ask user disabled",
        SettingsField.FileTools => on ? "file tools enabled" : "file tools disabled",
        SettingsField.ImageTools => on ? "image tools enabled" : "image tools disabled",
        SettingsField.ShoppingTools => on ? "shopping tools enabled" : "shopping tools disabled",
        SettingsField.FileTreeShowSizes => on ? "/tree carries each file's size" : "/tree lists names only",
        SettingsField.WebTools => on ? "web tools enabled" : "web tools disabled",
        SettingsField.GitLibTools => on ? "gitlib tools enabled" : "gitlib tools disabled",
        SettingsField.ObsidianTools => on ? "Obsidian tools enabled" : "Obsidian tools disabled",
        SettingsField.ObsidianAllowDelete => on ? "vault_delete may move a note or attachment to the vault's .trash" : "vault_delete is disabled",
        SettingsField.SqlTools => on ? "SQL tools enabled" : "SQL tools disabled",
        SettingsField.OracleTools => on ? "Oracle tools enabled" : "Oracle tools disabled",
        SettingsField.OraclePercentMention => on ? "% and part of a name lists the Oracle connections on the line" : "% lists no Oracle connection",
        SettingsField.MySqlTools => on ? "MySQL tools enabled" : "MySQL tools disabled",
        SettingsField.MySqlPercentMention => on ? "% and part of a name lists the MySQL connections on the line" : "% lists no MySQL connection",
        SettingsField.SqliteTools => on ? "SQLite tools enabled" : "SQLite tools disabled",
        SettingsField.SqliteSandboxFiles => on ? "a SQLite file in the working directory can be named by its path" : "only the databases of sqlite.json",
        SettingsField.SqlitePercentMention => on ? "% and part of a name lists the SQLite databases on the line" : "% lists no SQLite database",
        SettingsField.PostgresTools => on ? "PostgreSQL tools enabled" : "PostgreSQL tools disabled",
        SettingsField.PostgresPercentMention => on ? "% and part of a name lists the PostgreSQL connections on the line" : "% lists no PostgreSQL connection",
        SettingsField.UncTools => on ? "UNC tools enabled" : "UNC tools disabled",
        SettingsField.UncWrites => on ? "read-write shares may write" : "read-only forced for all shares",
        SettingsField.UncStarMention => on ? "* and part of a name lists the UNC shares on the line" : "* is ordinary text",
        SettingsField.DockerTools => on ? "docker tools enabled" : "docker tools disabled",   // the user's wording, 2026-10-03
        SettingsField.CameraTools => on ? "camera tool enabled" : "camera tool disabled",   // the user's wording, 2026-10-03
        SettingsField.CameraKeepInSessions => on ? "stored sessions keep the camera's pictures" : "stored sessions name the photos; files stay in the output folder",
        SettingsField.ScreenTools => on ? "screen capture tool enabled" : "screen capture tool disabled",
        SettingsField.ScreenPreview => on ? "the viewer shows each screenshot sent" : "screenshots are sent without a preview",
        SettingsField.ScreenKeepInSessions => on ? "stored sessions keep the screenshots" : "stored sessions name the screenshots; files stay in the output folder",
        SettingsField.CameraWatchUnprompted => on ? "watch mode shows the model a change by itself, now and then" : "watch mode's changes ride your next message",
        SettingsField.BotChatCamera => on ? "each bot sees a fresh picture from your camera (vision models)" : "the bots do not see your camera",
        SettingsField.BotChatRecentContext => on ? "the bots are told the last messages of this chat" : "the bots start fresh on the topic",
        SettingsField.DockerWrites => on ? "the model may start, stop, pull and prune, each change asking first" : "the model may only look at Docker",
        SettingsField.DockerServers => OperatingSystem.IsMacOS()   // a Mac says why, 2026-10-11 (Docker Desktop's VM has no GPU)
            ? on ? "needs Windows: Docker Desktop on a Mac passes no GPU to containers" : "no Docker servers; they need Windows (no GPU in Docker on a Mac)"
            : on ? "/server offers the chosen containers, one running at a time" : "no Docker servers; one in use stops at the reconnect",
        SettingsField.DockerServerStopOnExit => on ? "the app's exit stops the container it was using" : "the container keeps running after the app exits",
        SettingsField.ComfyTools => on ? "ComfyUI tools enabled" : "ComfyUI tools disabled",
        SettingsField.HomeAssistantTools => on ? "ha tools enabled" : "ha tools disabled",   // the user's wording, 2026-10-03
        SettingsField.YouTubeTools => on ? "YouTube tools enabled" : "YouTube tools disabled",
        SettingsField.YouTubeAutoplay => on ? "a played video starts at once" : "a played video waits, cued, for a play",
        SettingsField.PrintTools => on ? "print tool enabled" : "print tool disabled",   // the user's wording, 2026-10-03
        SettingsField.ComfyReinforceNegatives => on ? "the model adds a few opposite tags to a workflow's negative" : "the workflow's negative as it is",
        SettingsField.ComfyShowPrompts => on ? "the prompts and params sent to ComfyUI under each picture's line" : "just the picture's line",
        SettingsField.ComfyCaretMention => on ? "^ and part of a name lists the offered workflows on the line" : "^ is ordinary text",
        SettingsField.ComfyPictureStrip => on ? "the session's pictures in a strip above the line" : "no strip",
        SettingsField.Advisors => on ? "the model may consult the advisors; /advisor works either way" : "no advisor tool; /advisor still asks",
        SettingsField.AdvisorConfirm => on ? "each neon_advisor call waits for your yes" : "neon_advisor runs without asking",
        SettingsField.AnthropicApi => on ? "/server offers the Anthropic API while a key is set (billed per message)" : "the Anthropic API is not offered",
        SettingsField.AnthropicApiPromptCaching => on ? "the prompt and conversation are cached between requests (cheaper)" : "every request is billed in full",
        SettingsField.ClaudeCodeServer => on ? "/server offers Claude Code, run with the app's tools, not its own" : "Claude Code is not offered as a server; a running one stops",
        SettingsField.OpenAIApi => on ? "/server offers the OpenAI API while a key is set (billed per message)" : "the OpenAI API is not offered",
        SettingsField.EmbeddedVision => on ? "the embedded model loads its vision projector and reads images" : "the embedded model reads text alone; about 1 GB less memory",
        SettingsField.EmbeddedVramOnly => on ? "every layer on the GPU; a load that spills into system RAM is refused" : "a model too big for VRAM may run partly from system RAM, slowly",
        SettingsField.EmbeddedLlmServer => on ? "/server offers the embedded models" : "no embedded models in /server; a running one stops",
        SettingsField.EmbeddedDrafter => on ? "the embedded model drafts ahead with its drafter (faster, same answers)" : "the embedded model decodes one token at a time, no drafter loaded",
        SettingsField.BotChatComfy => on ? "the bots get this profile's offered ComfyUI workflows" : "the bots get the Botchat ComfyUI limited workflows alone",
        SettingsField.BotChatImageAsync => on ? "the next bot answers while the picture renders" : "the chat waits for each picture",
        SettingsField.BotChatTools => on ? "the bots get every tool this chat would offer" : "the bots get the Botchat limited tools alone",
        SettingsField.BotChatSkills => on ? "the bots get load_skill over the skills this chat sees" : "the bots get the Botchat limited skills alone",
        SettingsField.BotChatMemory => on ? "the bots remember (Botchat memory mode says whose memories)" : "the bots remember nothing",
        SettingsField.BotChatVision => on ? "each bot sees the pictures shown since it last spoke (vision models)" : "the bots see text alone",
        SettingsField.BotChatMultiEmbeddedKill => on ? "the extra servers stop when the botchat ends" : "extra servers stay up for later botchats until /botchat --kill or exit",
        SettingsField.SqlPercentMention => on ? "% and part of a name lists the SQL connections on the line" : "% is ordinary text",
        SettingsField.LlmCompactShowSummary => on ? "the summary's lines or the pruned results, then the protected counts" : "the one compact notice alone",
        SettingsField.AgentSkills => on ? "the skills catalog, load_skill and skill_editor are offered" : "no skills, no project notes",
        SettingsField.GlobalSkills => on ? "every profile's global skills are read too" : "only this profile's skills; the global ones are hidden and left alone",
        SettingsField.ExternalSkills => on ? (OperatingSystem.IsMacOS() ? "~/.agents/skills is read too" : "%USERPROFILE%\\.agents\\skills is read too") : "no external skills",   // the Mac's home, 2026-10-06
        SettingsField.ProjectFile => on ? "use project file (NEON.md or AGENTS.md in the working directory)" : "project files in the working directory are ignored",   // the user's wording, 2026-10-01
        SettingsField.SkillHashMention => on ? "# and part of a name lists the loaded skills on the line" : "# is ordinary text",
        SettingsField.ToolsDollarMention => on ? "$ and part of a name lists the offered tools on the line" : "$ is ordinary text",
        SettingsField.ShowFileDiffs => on ? "a file edit shows its diff under its line" : "a file edit shows its one line",
        SettingsField.McpServers => on ? "the configured MCP servers connect and their tools are offered" : "no MCP server is started; the pane still lists the config",
        SettingsField.ReflectionAutoLearn => on ? "automatic reflection enabled" : "automatic reflection disabled",
        SettingsField.ReflectionIncludesSessions => on ? "a reflection can read past sessions for insights" : "a reflection reads the conversation on screen alone",
        SettingsField.ReflectionYieldsToTurns => on ? "a turn pauses a running reflection which may resume after the turn" : "reflections run asynchronously (when the model allows)",
        SettingsField.ReflectionEditsSupportingFiles => on ? "a reflection may write a skill's supporting files" : "a reflection writes a skill's SKILL.md alone",
        SettingsField.HideExitAutocomplete => on ? "show '/exit' in the autocomplete list" : "hide '/exit' from the autocomplete list",
        SettingsField.EmojiPicker => on ? "the draft row's ☺ opens the emoji picker" : "no emoji button on the draft row",
        SettingsField.CommandTypoIntercept => on ? "a command typed without its slash or with extra ones offers the command" : "a command typed without its slash or with extra ones is sent as typed",
        SettingsField.KeepMessageHistory => on ? "message history enabled" : "message history disabled",
        SettingsField.ShowHeader => on ? "show the header" : "hide the header",
        SettingsField.ShowWorkingDirectory => on ? "show the working directory in the header" : "hide the working directory in the header",
        SettingsField.ThemedExternalWindows => on ? "theme the external windows" : "keep the external windows black",
        SettingsField.ThemedBackground => on ? "theme the terminal's background" : "keep the terminal profile's background",
        SettingsField.SessionLogging => on ? "every completed turn is written to this profile's session store" : "nothing is written; stored sessions can still be restored or purged",
        SettingsField.SessionTool => on ? "the model can search, list and read this profile's earlier sessions" : "the model never sees an earlier session",
        SettingsField.ShellToolBridge => on ? "a script may call this app's other tools through its neon_tools module" : "a script does everything itself: no neon_tools module, no tool calls",
        SettingsField.ShellPolice => on ? "shell police enabled" : "shell police disabled",   // the user's wording, 2026-10-03
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

    /// <summary>The backend picker under the settings list (2026-09-29): one row per <see cref="NeonSidekick.EmbeddedLlm.EmbeddedBackends.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickEmbeddedBackendAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = NeonSidekick.EmbeddedLlm.EmbeddedBackends.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.EmbeddedBackend)), names.Select(EmbeddedBackendLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.FindIndex(names, n => string.Equals(n, saved.EmbeddedBackend, StringComparison.OrdinalIgnoreCase))), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.EmbeddedBackend, d => d.EmbeddedBackend = name);
        return true;
    }

    /// <summary>A backend picker row: the name padded, then what it means, dim. Pinned. A Mac's two rows (2026-10-07) both mean Metal.</summary>
    public static string EmbeddedBackendLabel(string name) => Markup.Escape(name.PadRight(8)) + Theme.DimMarkup(name switch
    {
        "cuda" => "NVIDIA GPUs (driver 580 or newer)",
        "vulkan" => "any GPU: NVIDIA, AMD, Intel",
        "cpu" => "no GPU; slow",
        "metal" => "the Apple Silicon GPU",
        _ when OperatingSystem.IsMacOS() => "Metal, the Apple Silicon GPU",
        _ => "CUDA with an NVIDIA driver, else Vulkan, else the CPU",
    });

    /// <summary>
    /// The embedded model's catalog (2026-09-29): one row per model with its size and state. An installed one offers Use now,
    /// Remove (after a yes) and Back; any other, Install with what it downloads, and Back — and Remove too when part of it
    /// is on disk (later that day, the user's ask: a paused download thrown away). Use now and Install hand the model to the
    /// screen (<see cref="TakePendingEmbeddedModel"/>) and return true, which closes the pane; a removal stays.
    /// The title row carries the filters (later on 2026-09-29, the user's ask: <see cref="NeonSidekick.EmbeddedLlm.EmbeddedModelFilter"/>):
    /// a press thins the rows and keeps the cursor on its model while it is still shown; a model's own page keeps them, and
    /// every visit starts with none lit. Sort size (2026-09-30, the user's ask) orders the rows by size, smallest first.
    /// </summary>
    internal async Task<bool> PickEmbeddedModelAsync(CancellationToken cancellationToken)
    {
        if (EmbeddedLlm is not { } embedded || embedded.Catalog.Count == 0)
        {
            Sink.Notice(NoEmbeddedModelNotice);
            return Unchanged();
        }

        var effective = EffectiveNow();
        // The saved embedded model, when there is one (later on 2026-09-30, the user's pick): the cursor starts on it, and an
        // uncensored build lights uncensored, so its row shows.
        var inUse = NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(effective.LlmUrl) ? NeonSidekick.EmbeddedLlm.EmbeddedModelCatalog.Find(effective.LlmModel, embedded.Catalog) : null;
        var filter = NeonSidekick.EmbeddedLlm.EmbeddedModelFilter.For(inUse);
        int cursor = inUse is null ? 0 : Math.Max(0, embedded.Catalog.ToList().IndexOf(inUse));   // the catalog index under the cursor
        while (true)
        {
            // Every row laid out over the whole catalog, so a filter does not move the columns.
            var labels = EmbeddedModelLabels(embedded.Catalog, embedded.State, embedded.BytesToDownload);
            var shown = filter.Arrange(Enumerable.Range(0, embedded.Catalog.Count).Where(i => filter.Matches(embedded.Catalog[i], embedded.State(embedded.Catalog[i]).IsInstalled)).ToList(), i => embedded.Catalog[i]);
            var page = new MenuPage(Crumb(FieldName(SettingsField.EmbeddedModels)), FilteredRows(labels, shown), EmbeddedModelsKeys);
            if (await PickChecklistAsync(page, Math.Max(0, shown.IndexOf(cursor)), cancellationToken, filter.Buttons(withInstalled: true)).ConfigureAwait(false) is not { } pick)
            {
                return Unchanged();
            }

            if (pick.Row >= 0 && pick.Row < shown.Count)
            {
                cursor = shown[pick.Row];
            }

            if (pick.Button >= 0)
            {
                filter = filter.Press(pick.Button, withInstalled: true);
                continue;
            }

            if (shown.Count == 0)
            {
                continue;   // the no-match row: nothing to open
            }

            int index = cursor;
            var model = embedded.Catalog[index];
            var state = embedded.State(model);
            string crumb = Crumb(FieldName(SettingsField.EmbeddedModels)) + " › " + model.Display;
            if (state.IsInstalled)
            {
                var actions = new[] { UseNowRow, RemoveRow(model, embedded.FreedBytes(model)), BackRow };
                int? action = await PickAsync(new MenuPage(crumb, actions.Select(Markup.Escape).ToList(), PickKeys), 0, cancellationToken).ConfigureAwait(false);
                if (action == 0)
                {
                    _pendingEmbeddedModel = model;
                    return true;
                }

                if (action == 1)
                {
                    await RemoveEmbeddedModelAsync(embedded, model, NeonSidekick.EmbeddedLlm.EmbeddedLlmText.RemoveQuestion(model, embedded.FreedBytes(model)), cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            string install = InstallRow(model, embedded.RuntimeBytesToDownload(EffectiveNow()), embedded.BytesToDownload(model));
            bool partial = state.Kind == NeonSidekick.EmbeddedLlm.EmbeddedModelStateKind.Partial;
            var choices = partial ? new[] { Markup.Escape(install), Markup.Escape(RemovePartialRow), BackRow } : new[] { Markup.Escape(install), BackRow };
            int? choice = await PickAsync(new MenuPage(crumb, choices, PickKeys), 0, cancellationToken).ConfigureAwait(false);
            if (choice == 0)
            {
                _pendingEmbeddedModel = model;
                return true;
            }

            if (partial && choice == 1)
            {
                await RemoveEmbeddedModelAsync(embedded, model, NeonSidekick.EmbeddedLlm.EmbeddedLlmText.RemovePartialQuestion(model), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The catalog's Remove (2026-09-29; the download stop and the LLM clear later that day, the user's ask): after a yes to
    /// <paramref name="question"/>, a download of the model under way is stopped (<see cref="BeforeEmbeddedRemove"/>), the
    /// folder goes (the service stops a server that has it loaded first), and when the saved LLM URL and model named it they
    /// are cleared — <see cref="EmbeddedLlmClearedNotice"/> — so no connect looks for a model that is gone. The model in use
    /// is refused while a reply runs: its server is the one answering.
    /// </summary>
    private async Task RemoveEmbeddedModelAsync(NeonSidekick.EmbeddedLlm.IEmbeddedLlm embedded, NeonSidekick.EmbeddedLlm.EmbeddedModel model, string question, CancellationToken cancellationToken)
    {
        var saved = _settings.Current;
        bool named = NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(saved.LlmUrl) && string.Equals(saved.LlmModel.Trim(), model.Id, StringComparison.OrdinalIgnoreCase);
        if (_reply.Running && (named || string.Equals(embedded.Running?.ModelId, model.Id, StringComparison.Ordinal)
            || embedded.Extras.Any(e => string.Equals(e.ModelId, model.Id, StringComparison.OrdinalIgnoreCase))))   // an extra server's too (later on 2026-09-29)
        {
            Sink.Notice(NotWhileReplyRunsNotice);
            return;
        }

        if (!await ConfirmAsync(question, cancellationToken, close: false).ConfigureAwait(false))
        {
            return;
        }

        if (BeforeEmbeddedRemove is { } stop)
        {
            await stop(model, cancellationToken).ConfigureAwait(false);
        }

        if (embedded.Remove(model) is { } error)
        {
            Sink.Error(NeonSidekick.EmbeddedLlm.EmbeddedLlmText.InstallFailed(model, error));
            return;
        }

        Sink.Notice(NeonSidekick.EmbeddedLlm.EmbeddedLlmText.Removed(model));
        if (named)
        {
            _settings.Update(d =>
            {
                d.LlmUrl = "";
                d.LlmModel = "";
            });
            Sink.Notice(EmbeddedLlmClearedNotice);
            _embeddedLlmCleared = true;
        }
    }

    /// <summary>The notice when the removed model was the saved LLM (2026-09-29): URL and model cleared. Pinned.</summary>
    public const string EmbeddedLlmClearedNotice = "It was the saved LLM, so LLM URL and LLM model are cleared; pick another in /server.";

    /// <summary>A partly downloaded model's removal row (2026-09-29, the user's ask). Pinned.</summary>
    public const string RemovePartialRow = "Remove (the partial download)";

    /// <summary>The notice when the catalog is opened where no embedded model is offered. Pinned.</summary>
    public const string NoEmbeddedModelNotice = "No embedded model is offered here (llama.cpp's Windows x64 builds only).";

    /// <summary>A catalog row on its own: the model's name padded, its quantisation, then its state and size, dim, and the capability columns. Pinned.</summary>
    public static string EmbeddedModelLabel(NeonSidekick.EmbeddedLlm.EmbeddedModel model, NeonSidekick.EmbeddedLlm.EmbeddedModelState state) =>
        EmbeddedModelLabel(model, state, detailWidth: 0);

    /// <summary>
    /// A catalog row whose state-and-size detail is padded to <paramref name="detailWidth"/> cells, so the capability columns
    /// (<see cref="NeonSidekick.EmbeddedLlm.EmbeddedLlmText.CapabilityColumns"/>: ⚡ 👁️ 🛠️, 2026-09-29, the user's asks) line up down the list. Pinned.
    /// </summary>
    public static string EmbeddedModelLabel(NeonSidekick.EmbeddedLlm.EmbeddedModel model, NeonSidekick.EmbeddedLlm.EmbeddedModelState state, int detailWidth, long? download = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        string detail = NeonSidekick.EmbeddedLlm.EmbeddedLlmText.ModelDetail(model, state, download);
        return Markup.Escape(model.Display.PadRight(EmbeddedModelNameWidth)) + Theme.DimMarkup(Markup.Escape(model.Quant.PadRight(EmbeddedModelQuantWidth) + detail))
            + Markup.Escape(NeonSidekick.EmbeddedLlm.EmbeddedLlmText.CapabilityColumns(model, detail, detailWidth));
    }

    /// <summary>The catalog's rows, the details as wide as the widest one so each capability column is one column.</summary>
    public static IReadOnlyList<string> EmbeddedModelLabels(IReadOnlyList<NeonSidekick.EmbeddedLlm.EmbeddedModel> models, Func<NeonSidekick.EmbeddedLlm.EmbeddedModel, NeonSidekick.EmbeddedLlm.EmbeddedModelState> state, Func<NeonSidekick.EmbeddedLlm.EmbeddedModel, long>? download = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(state);
        var states = models.Select(state).ToList();
        var downloads = models.Select(m => download?.Invoke(m)).ToList();   // what an install would still fetch (2026-10-06)
        int width = models.Count == 0 ? 0 : models.Select((m, i) => TextCells.Width(NeonSidekick.EmbeddedLlm.EmbeddedLlmText.ModelDetail(m, states[i], downloads[i]))).Max();
        return models.Select((m, i) => EmbeddedModelLabel(m, states[i], width, downloads[i])).ToList();
    }

    /// <summary>
    /// The rows a filtered list shows (later on 2026-09-29): the labels at <paramref name="shown"/>, or the one dim
    /// <see cref="NeonSidekick.EmbeddedLlm.EmbeddedLlmText.NoFilterMatch"/> row when none passes.
    /// </summary>
    private static List<string> FilteredRows(IReadOnlyList<string> labels, IReadOnlyList<int> shown) =>
        shown.Count > 0 ? shown.Select(i => labels[i]).ToList() : [Theme.DimMarkup(Markup.Escape(NeonSidekick.EmbeddedLlm.EmbeddedLlmText.NoFilterMatch))];

    /// <summary>The catalog's quantisation column: "COMPACT-LOW" (11, esatapedico's NVFP4 tier word, later on 2026-09-29) plus two; 12 before.</summary>
    public const int EmbeddedModelQuantWidth = 13;

    /// <summary>The catalog picker's name column: "Gemma 4 26B A4B QAT Uncensored" (30) plus two (later on 2026-09-29, when the 26B A4B builds joined; 28 for "Gemma 4 12B QAT Uncensored" before, 24 while the longest was 22).</summary>
    public const int EmbeddedModelNameWidth = 32;

    /// <summary>The installed model's removal row: <c>Remove (5.2 GB)</c>, what the removal frees (<paramref name="freed"/>; the whole model when not given, 2026-10-06). Pinned.</summary>
    public static string RemoveRow(NeonSidekick.EmbeddedLlm.EmbeddedModel model, long? freed = null) => $"Remove ({Speech.ModelStore.SizeLabel(freed ?? NeonSidekick.EmbeddedLlm.EmbeddedModelCatalog.TotalBytes(model))})";

    /// <summary>The install row: <c>Install (download 5.2 GB + llama.cpp runtime 577 MB)</c>, the files not yet on disk (<paramref name="download"/>). Pinned.</summary>
    public static string InstallRow(NeonSidekick.EmbeddedLlm.EmbeddedModel model, long runtimeBytes, long? download = null) => $"Install ({NeonSidekick.EmbeddedLlm.EmbeddedLlmText.InstallCost(model, runtimeBytes, download)})";

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

    /// <summary>The botcheck-window picker under the settings list (2026-10-13): one <see cref="BotcheckWindowLabel"/> row per <see cref="Web.BotcheckWindow.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickBotcheckWindowAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = Web.BotcheckWindow.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.WebBrowserBotcheckWindow)), names.Select(BotcheckWindowLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, saved.WebBrowserBotcheckWindow)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.WebBrowserBotcheckWindow, d => d.WebBrowserBotcheckWindow = name);
        return true;
    }

    /// <summary>The Web browser privacy picker (later on 2026-10-13): one <see cref="BrowserPrivacyLabel"/> row per <see cref="Viewer.BrowserPrivacy.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickBrowserPrivacyAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = Viewer.BrowserPrivacy.Names;
        string current = Viewer.BrowserPrivacy.TryParse(saved.BrowserPrivacy, out var kind) ? Viewer.BrowserPrivacy.Name(kind) : Viewer.BrowserPrivacy.Default;
        var page = new MenuPage(Crumb(FieldName(SettingsField.BrowserPrivacy)), names.Select(BrowserPrivacyLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, current)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.BrowserPrivacy, d => d.BrowserPrivacy = name);
        return true;
    }

    /// <summary>The Web open links in picker (later still on 2026-10-13): one <see cref="WebOpenLinksInLabel"/> row per <see cref="Web.OpenLinksIn.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickWebOpenLinksInAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = Web.OpenLinksIn.Names;
        string current = Web.OpenLinksIn.TryParse(saved.WebOpenLinksIn, out var kind) ? Web.OpenLinksIn.Name(kind) : Web.OpenLinksIn.Default;
        var page = new MenuPage(Crumb(FieldName(SettingsField.WebOpenLinksIn)), names.Select(WebOpenLinksInLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, current)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.WebOpenLinksIn, d => d.WebOpenLinksIn = name);
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

    /// <summary>
    /// The command-policy picker opened straight, over the saved values (2026-10-03, for <see cref="ToolsMenu.ShowSwitchAsync"/>:
    /// <c>/tools shell</c>, the toolbar's shell), under whatever <see cref="Root"/> the caller set. True when the value saved.
    /// </summary>
    internal Task<bool> EditCommandPolicyAsync(CancellationToken cancellationToken) =>
        PickCommandPolicyAsync(_settings.Current, cancellationToken);

    /// <summary>
    /// The command-policy picker under the settings list (2026-09-21): one <see cref="CommandPolicyLabel"/> row per
    /// <see cref="Shell.CommandPolicy.Names"/> entry, the saved one under the cursor. A move into <c>yolo</c> asks
    /// <see cref="YoloConfirmQuestion"/> on the same pane first (2026-10-03, with the toolbar's shell: every way into yolo asks,
    /// as the allowed-commands list's button does); No or ESC leaves the policy. Since 2026-10-09 (the user's call:
    /// <c>/cmdlist</c> and <c>/police</c> folded under <c>/tools shell</c>) the title row carries <see cref="CommandPolicyPageButtons"/>
    /// — C the allowed-commands list, P the police's page — each coming back here on ESC, the police page's strings-button shape.
    /// </summary>
    private async Task<bool> PickCommandPolicyAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.ShellCommandPolicy)), Shell.CommandPolicy.Names.Select(CommandPolicyLabel).ToList(), _pane.Enabled ? CommandPolicyPageKeys : PickKeys);
        int cursor = Array.IndexOf(Shell.CommandPolicy.Names, saved.ShellCommandPolicy);
        int? picked;
        while (true)
        {
            // Read again at each pass: a page the buttons open may have changed the list, the police or (the list's off, ask and yolo) the policy.
            var current = _settings.Current;
            var buttons = CommandPolicyPageButtons(Shell.CommandAllowList.Merge(current.ShellCommandAllowed, []).Count, current.ShellPolice);
            var pressed = await PickChecklistAsync(page, cursor, cancellationToken, buttons).ConfigureAwait(false);
            if (pressed is { Button: >= 0 } button)
            {
                cursor = button.Row;
                _ = button.Button == 0
                    ? await EditAllowedCommandsAsync(cancellationToken).ConfigureAwait(false)
                    : await EditToggleAsync(SettingsField.ShellPolice, cancellationToken).ConfigureAwait(false);
                continue;
            }

            picked = pressed?.Row;
            break;
        }

        saved = _settings.Current;
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = Shell.CommandPolicy.Names[index];
        if (name == "yolo" && !string.Equals(saved.ShellCommandPolicy, name, StringComparison.Ordinal))
        {
            var question = new MenuPage(YoloConfirmQuestion, ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
            if (await PickAsync(question, 0, cancellationToken).ConfigureAwait(false) != 1)
            {
                return Unchanged();
            }
        }

        Apply(SettingsField.ShellCommandPolicy, d => d.ShellCommandPolicy = name);
        return true;
    }

    /// <summary>
    /// <c>SQL connections offered</c> (later on 2026-09-23): every connection the two files hold, ticked or not, Enter or
    /// Space flipping one and the list shown again until ESC (the <see cref="EditCodeLanguagesAsync"/> shape). The list is
    /// exact: nothing is ticked until the user ticks it (2026-10-01; until then a never-narrowed profile started all ticked),
    /// and a connection added later stays hidden until ticked (the user's call). A ticked name the files no longer hold is
    /// dropped as the list opens (2026-10-04, the user's call: it used to ride along unseen, kept even by N), unless a file
    /// could not be read or a load problem still names it (<see cref="OfferedNames.StaleConnections"/>); its five twins do the
    /// same. True when anything changed, a prune too.
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

            // A saved name no longer in the files is dropped as the list opens (2026-10-04, the user's call; it was carried along
            // unseen until then), unless a file could not be read or a problem still names it (OfferedNames.StaleConnections).
            changed |= PruneStale(SettingsField.SqlConnectionsOffered, _settings.Current.SqlConnectionsOffered, OfferedNames.StaleConnections(_settings.Current.SqlConnectionsOffered, loaded.Connections.Select(c => c.Name), loaded.Problems), StringComparer.OrdinalIgnoreCase, (d, kept) => d.SqlConnectionsOffered = kept);

            var offered = _settings.Current.SqlConnectionsOffered;
            var on = loaded.Offered(offered).Connections.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var connections = ByName(loaded.Connections, c => c.Name);   // A to Z (2026-10-11, the user's ask)
            int width = connections.Max(c => c.Name.Length) + 2;
            var page = new MenuPage(Crumb(FieldName(SettingsField.SqlConnectionsOffered)), connections.Select(c => SqlOfferedRow(c, on.Contains(c.Name), width)).ToList(), ToggleKeys) { SpaceToggles = true };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, connections.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            string name = connections[pick.Row].Name;
            // Select all ticks the connections listed now, one added later still starting hidden (2026-09-29, the user's call).
            var next = pick.Button == SelectAllIndex ? connections.Select(c => c.Name).ToList()
                : pick.Button == SelectNoneIndex ? []
                : connections.Select(c => c.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (next.Count == on.Count && next.All(on.Contains))
            {
                continue;   // a button that changes nothing saves nothing (null and empty both offer none, 2026-10-01)
            }

            // What is saved but not listed now survived the prune above: a name a problem still names, or every name while a
            // file cannot be read. It stays until the file is mended, and the next opening decides (2026-10-04).
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
        var connections = ByName(Sql.SqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Connections.Where(c => c.Config.NeedsPassword), c => c.Name);
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

    /// <summary>The default-shell picker under the settings list (2026-09-21): one <see cref="ShellLabel"/> row per <see cref="Shell.ShellKinds.Names"/> entry, a shell not installed noted, the saved one under the cursor. A shell not found can still be picked: the row is the wish, the tool says what is missing.</summary>
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
    /// which opens it straight under the Tools crumb (<c>/tools shell allowed</c>, <c>/cmdlist</c> until 2026-10-09; the toolbar lock). Since 2026-10-02 (the user's ask) its
    /// title row carries <see cref="CommandPolicyButtons"/>: since later on 2026-10-13 one button stepping <c>Shell command policy</c> off → ask →
    /// yolo → off, <c>off</c> and <c>ask</c> saved at once, <c>yolo</c> after a yes to <see cref="YoloConfirmQuestion"/> asked on the same pane
    /// (not <see cref="ConfirmAsync"/>, which closes it), a no moving on to <c>off</c> (<see cref="YoloDeclinedNotice"/>) so the cycle never
    /// sticks; the save shows on the list's status line. It works on the empty list too.
    /// </summary>
    internal async Task<bool> EditAllowedCommandsAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        string filter = "";
        while (true)
        {
            var allowed = Shell.CommandAllowList.Merge(_settings.Current.ShellCommandAllowed, []);
            // Typed to filter, each prefix whole under the list (2026-10-07, the user's ask): the rows' indices into the list.
            var shown = Enumerable.Range(0, allowed.Count).Where(i => MenuFilter.Matches(filter, allowed[i], null)).ToList();
            IReadOnlyList<string> rows = allowed.Count == 0 ? [Markup.Escape(NoAllowedCommandsRow)]
                : shown.Count == 0 ? [MenuFilter.NoMatchNameRow(filter)]
                : shown.Select(i => Markup.Escape(allowed[i])).ToList();
            var page = new MenuPage(Crumb(FieldName(SettingsField.ShellCommandAllowed)), rows, allowed.Count == 0 ? AllowedCommandsEmptyKeys : MenuFilter.HintBeforeEsc(AllowedCommandsKeys, filter, letteredButtons: true))
            {
                Filter = allowed.Count == 0 ? null : filter,
                Caption = MenuFilter.CaptionOrNull(filter, shown.Count, allowed.Count),
                Footer = (_, row) => row < shown.Count ? new MenuFooter(allowed[shown[row]], AllowedCommandNote) : null,
            };
            string policy = _settings.Current.ShellCommandPolicy;
            int start = Math.Max(0, shown.IndexOf(cursor));
            MenuPick? pick = _pane.Enabled
                ? await _pane.PickAsync(page with { Buttons = CommandPolicyButtons(policy) }, start, cancellationToken).ConfigureAwait(false)
                : await PickAsync(page, start, cancellationToken).ConfigureAwait(false) is { } plain ? new MenuPick(0, plain) : null;
            if (pick?.Filter is { } typed)
            {
                filter = typed;
                cursor = -1;   // the first row left
                continue;
            }

            (int Row, int Button)? picked = pick is { } p ? (p.Row >= 0 && p.Row < shown.Count ? shown[p.Row] : -1, p.Button) : null;
            if (picked is { Button: PolicyIndex } pressed)
            {
                cursor = pressed.Row;
                string next = NextPolicy(policy);
                if (next == "yolo")
                {
                    var question = new MenuPage(YoloConfirmQuestion, ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
                    if (await PickAsync(question, 0, cancellationToken).ConfigureAwait(false) != 1)
                    {
                        // A no moves on to off, the next in the cycle (later on 2026-10-13): ask → off is one press and a no, never stuck.
                        _settings.Update(d => d.ShellCommandPolicy = "off");
                        Sink.Notice(YoloDeclinedNotice);
                        if (_overriddenBy(SettingsField.ShellCommandPolicy) is { } overriddenBy)
                        {
                            Sink.Warning(OverrideNotice(overriddenBy));
                        }

                        changed = true;
                        continue;
                    }
                }

                Apply(SettingsField.ShellCommandPolicy, d => d.ShellCommandPolicy = next);
                changed = true;
                continue;
            }

            if (picked is not { Row: var index } || allowed.Count == 0)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            if (index < 0)
            {
                continue;   // the no-match row
            }

            string prefix = allowed[index];
            cursor = index;
            // Asked first (2026-10-07, every list's remove asks).
            var removing = new MenuPage(RemovePrefixQuestion(prefix), ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
            if (await PickAsync(removing, 0, cancellationToken).ConfigureAwait(false) != 1)
            {
                Sink.Notice(ChatScreen.KeptNotice);
                continue;
            }

            _settings.Update(d => d.ShellCommandAllowed = Shell.CommandAllowList.Without(d.ShellCommandAllowed, prefix));
            Sink.Notice(PrefixRemovedNotice(prefix));
            changed = true;
            cursor = index;
        }
    }

    /// <summary>
    /// The <c>Shell police forbidden strings</c> list (2026-10-03, the user's idea): <see cref="AddForbiddenRow"/> on top, then each
    /// string (<see cref="Shell.ForbiddenStrings.Sorted"/>). Enter on the top row opens the input slot under it (without the pane, the
    /// prompt line) and a typed string is saved at once (<see cref="ForbiddenAddedNotice"/>, or <see cref="ForbiddenDuplicateNotice"/> and
    /// nothing saved); Enter on a string removes it (<see cref="ForbiddenRemovedNotice"/>). The list is shown again until ESC. The
    /// <c>EditAllowedCommandsAsync</c> shape, opened from the Shell tab's row and the police page's strings button. On the pane the title
    /// row carries <see cref="RestoreForbiddenButton"/> (2026-10-08, the user's ask): the list back to the defaults after a yes, or
    /// <see cref="ForbiddenAlreadyDefaultsNotice"/> and nothing asked when it already is. True when anything changed.
    /// </summary>
    internal async Task<bool> EditForbiddenStringsAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        string title = FieldName(SettingsField.ShellPoliceForbiddenStrings);
        while (true)
        {
            var forbidden = Shell.ForbiddenStrings.Sorted(_settings.Current.ShellPoliceForbiddenStrings);
            IReadOnlyList<string> rows = [Markup.Escape(AddForbiddenRow), .. forbidden.Select(Markup.Escape)];
            // Each string whole under the list (2026-10-07, the user's ask: a long one was cut at the edge).
            var page = new MenuPage(Crumb(title), rows, _pane.Enabled ? ForbiddenPaneKeys : ForbiddenKeys) { Footer = (_, row) => row > 0 && row <= forbidden.Count ? new MenuFooter(forbidden[row - 1], ForbiddenStringNote) : null };
            if (await PickChecklistAsync(page, Math.Min(cursor, rows.Count - 1), cancellationToken, ForbiddenButtons).ConfigureAwait(false) is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            if (pick.Button >= 0)
            {
                cursor = pick.Row;
                changed |= await RestoreForbiddenDefaultsAsync(forbidden, cancellationToken).ConfigureAwait(false);
                continue;
            }

            int index = pick.Row;
            cursor = index;
            if (index > 0)
            {
                string entry = forbidden[index - 1];
                // Asked first (2026-10-07, every list's remove asks).
                var removing = new MenuPage(RemoveForbiddenQuestion(entry), ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
                if (await PickAsync(removing, 0, cancellationToken).ConfigureAwait(false) != 1)
                {
                    Sink.Notice(ChatScreen.KeptNotice);
                    continue;
                }

                _settings.Update(d => d.ShellPoliceForbiddenStrings = Shell.ForbiddenStrings.Without(d.ShellPoliceForbiddenStrings, entry));
                Sink.Notice(ForbiddenRemovedNotice(entry));
                changed = true;
                continue;
            }

            InputResult result;
            if (_pane.Enabled)
            {
                result = await _pane.EditAsync(page with { Hint = EditKeys }, 0, _input, "", allowEmpty: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(title, EditKeys));
                result = await _input.ReadAsync("", remember: false, allowEmpty: false, cancellationToken: cancellationToken, escapeCancels: true).ConfigureAwait(false);
            }

            string typed = result is InputResult.Submitted submitted ? Shell.ForbiddenStrings.Normalize(submitted.Text) : "";
            if (typed.Length == 0)
            {
                continue;   // ESC or a blank line: back on the list, nothing saved
            }

            if (Shell.ForbiddenStrings.Contains(_settings.Current.ShellPoliceForbiddenStrings, typed))
            {
                Sink.Notice(ForbiddenDuplicateNotice(typed));
                continue;
            }

            _settings.Update(d => d.ShellPoliceForbiddenStrings = Shell.ForbiddenStrings.Add(d.ShellPoliceForbiddenStrings, typed));
            Sink.Notice(ForbiddenAddedNotice(typed));
            changed = true;
        }
    }

    /// <summary>
    /// <see cref="RestoreForbiddenButton"/>'s press (2026-10-08): <paramref name="forbidden"/> (the list as shown) already the
    /// defaults, case ignored, is <see cref="ForbiddenAlreadyDefaultsNotice"/> and nothing asked; otherwise
    /// <see cref="RestoreForbiddenQuestion"/> on No, and a yes saves <see cref="Shell.ForbiddenStrings.Defaults"/> whole. True when saved.
    /// </summary>
    private async Task<bool> RestoreForbiddenDefaultsAsync(IReadOnlyList<string> forbidden, CancellationToken cancellationToken)
    {
        var defaults = Shell.ForbiddenStrings.Defaults;
        if (forbidden.SequenceEqual(defaults, StringComparer.OrdinalIgnoreCase))
        {
            Sink.Notice(ForbiddenAlreadyDefaultsNotice);
            return false;
        }

        var restoring = new MenuPage(RestoreForbiddenQuestion(forbidden.Count, defaults.Count), ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
        if (await PickAsync(restoring, 0, cancellationToken).ConfigureAwait(false) != 1)
        {
            Sink.Notice(ChatScreen.KeptNotice);
            return false;
        }

        _settings.Update(d => d.ShellPoliceForbiddenStrings = Shell.ForbiddenStrings.Defaults);
        Sink.Notice(ForbiddenRestoredNotice(defaults.Count));
        return true;
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
            var picked = await PickChecklistAsync(page, cursor, cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            string name = Shell.CodeLanguages.Names[pick.Row];
            if (pick.Button == SelectNoneIndex || (pick.Button < 0 && enabled.Contains(name) && enabled.Count == 1))
            {
                Sink.Error(LastLanguageError);
                continue;
            }

            var next = pick.Button == SelectAllIndex
                ? Shell.CodeLanguages.Names.ToList()
                : Shell.CodeLanguages.Names.Where(n => enabled.Contains(n) != string.Equals(n, name, StringComparison.Ordinal)).ToList();
            if (next.Count == enabled.Count && next.All(enabled.Contains))
            {
                continue;   // select all with every language on: nothing to save
            }

            Apply(SettingsField.ShellCodeLanguages, d => d.ShellCodeLanguages = next);
            changed = true;
        }
    }

    /// <summary>
    /// The toolbar's checklist under the settings list (2026-09-29, the user's ask: <c>Show toolbar</c> a multiple choice in
    /// place of its switch): one <see cref="App.ToolbarItems.Label"/> row per item, Enter or Space flipping it and saving at
    /// once, the list re-shown until ESC. Nothing has to stay: nothing checked is no toolbar. Its buttons are the checklists'
    /// and <see cref="DefaultsButton"/> (2026-09-29). Since 2026-10-12 (the user's ask, the home bar picker's move keys) the rows
    /// are the strip's order (<see cref="App.ToolbarItems.Order"/>): Shift+↑↓ moves the cursor's row a place, Shift+Home/End to the
    /// top or the bottom; Delete unchecks it; default puts the order back with the checks. Since 2026-10-13 (the user's ask) the rows
    /// are two groups, pinned left and pinned right, the path no longer held last (<see cref="EditSidedChecklistAsync"/>).
    /// True when anything changed.
    /// </summary>
    private async Task<bool> EditToolbarItemsAsync(CancellationToken cancellationToken)
    {
        return await EditSidedChecklistAsync(ToolbarChecklist, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The hint bar's checklist under the settings list (2026-10-12, the user's ask: the other bars' checklist for the hint bar):
    /// one <see cref="App.StatusBarItems.Label"/> row per section in the row's order (<see cref="App.StatusBarItems.Order"/>), Enter or
    /// Space flipping it and saving at once; Shift+↑↓ moves the cursor's section a place, Shift+Home/End to the top or the bottom,
    /// Delete unchecks it; select all, select none and default (every section but the working directory, the row's own order) on the title row. The list is
    /// re-shown until ESC, the row redrawing under it. Since 2026-10-13 (the user's ask) two groups, pinned left and pinned right
    /// (<see cref="EditSidedChecklistAsync"/>). True when anything changed.
    /// </summary>
    private async Task<bool> EditStatusBarAsync(CancellationToken cancellationToken)
    {
        return await EditSidedChecklistAsync(StatusBarChecklist, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/toolbar edit</c> (2026-10-12, the user's ask: as <c>/homebar edit</c> opens its picker): the <c>Toolbar</c> row's
    /// checklist (<see cref="EditToolbarItemsAsync"/>) on its own. <paramref name="own"/>: the pane is the checklist's and closes as
    /// it ends. True when anything changed.
    /// </summary>
    public async Task<bool> EditToolbarAsync(bool own, CancellationToken cancellationToken)
    {
        try
        {
            return await EditToolbarItemsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (own)
            {
                _pane.Close();
            }
        }
    }

    /// <summary>
    /// <c>/perfbar edit</c> (2026-10-12, the user's ask, as <see cref="EditToolbarAsync"/>): the <c>Performance bar</c> row's
    /// checklist (<see cref="EditPerfBarAsync(CancellationToken)"/>) on its own; <paramref name="own"/> as there. True when anything changed.
    /// </summary>
    public async Task<bool> EditPerformanceBarAsync(bool own, CancellationToken cancellationToken)
    {
        try
        {
            return await EditPerfBarAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (own)
            {
                _pane.Close();
            }
        }
    }

    /// <summary>The queue-mode picker under the settings list (2026-10-09): one <see cref="QueueModeLabel"/> row per <see cref="QueueMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickQueueModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var page = new MenuPage(Crumb(FieldName(SettingsField.QueueMode)), QueueMode.Names.Select(QueueModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(QueueMode.Names, saved.QueueMode), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = QueueMode.Names[index];
        Apply(SettingsField.QueueMode, d => d.QueueMode = name);
        return true;
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

    /// <summary>
    /// The performance bar's page under the settings list (2026-09-30, the user's ask: the toolbar's checklist): one
    /// <see cref="PerfBarItems.Label"/> row per meter, Enter or Space flipping it and saving at once, nothing checked no bar; on
    /// the title row the checklists' select all and select none, and default (2026-10-02, the user's ask: CPU, RAM, GPU and
    /// VRAM). The looks were radio buttons after default until 2026-10-12 (the user's call); <c>/perfbar &lt;look&gt;</c> sets
    /// the look. The list is re-shown until ESC, the bar redrawing under it. Since 2026-10-12 (the user's ask, the home bar picker's move keys) the rows are the bar's order
    /// (<see cref="PerfBarItems.Order"/>): Shift+↑↓ moves the cursor's meter a place, Shift+Home/End to the top or the bottom;
    /// Delete unchecks it; default puts the order back with the checks (the look stays). Later on 2026-10-13 (the user's ask: the home
    /// bar's pinning) the rows are in three groups, <see cref="PinnedLeftHeading"/>, <see cref="PinnedCenterHeading"/> and
    /// <see cref="PinnedRightHeading"/>, Shift+←/→ moving the cursor's meter a group over (<see cref="EditSidedChecklistAsync"/>, the tool
    /// bar's and the hint bar's), every meter center by default; default puts the sides back too. True when anything changed.
    /// </summary>
    private Task<bool> EditPerfBarAsync(CancellationToken cancellationToken) => EditSidedChecklistAsync(PerfBarChecklist, cancellationToken);

    /// <summary>
    /// The Toolbar order page under the settings list (2026-10-12, the user's ask: the hint bar, the toolbar, the performance bar
    /// and the home bar in any order; the move keys of Show toolbar's checklist): one <see cref="PaneBars.Label"/> row per bar in the
    /// saved order, a bar that is off marked so. Shift+↑↓ (or Alt) moves the cursor's bar a place, Shift+Home/End to the top or the
    /// bottom, saving at once, the pane redrawing in the new order under the page; <c>⊡ default</c> on the title row (D; the user's
    /// ask, later that day: a way back on the page itself) puts <see cref="PaneBars.Names"/>' order back. Nothing to check, so Enter,
    /// Space and Delete do nothing. The list is re-shown until ESC. True when anything changed.
    /// </summary>
    private async Task<bool> EditBarOrderAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var saved = _settings.Current;
            var order = PaneBars.Order(saved.BarOrder);
            var page = new MenuPage(Crumb(BarOrderPaneName), order.Select(id => PaneBars.Label(id, PaneBars.Shown(id, saved))).ToList(), BarOrderKeys);
            var picked = await PickOrderedChecklistAsync(page, cursor, cancellationToken, BarOrderButtons).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            if (pick.Move != 0)
            {
                var (moved, at) = ItemOrder.Move(order, pick.Row, pick.Move);
                cursor = at;
                if (at != pick.Row)
                {
                    Apply(SettingsField.BarOrder, d => d.BarOrder = PaneBars.SaveOrder(moved));
                    changed = true;
                }

                continue;
            }

            if (pick.Button == BarOrderDefaultIndex && PaneBars.SaveOrder(order) is not null)
            {
                Apply(SettingsField.BarOrder, d => d.BarOrder = null);
                changed = true;
            }

            // Enter, Space or Delete on a row, or default with the order already the default: nothing to save.
        }
    }

    /// <summary>The HF download type picker under the settings list (2026-09-30): one <see cref="EmbeddedHfDownloadTypeLabel"/> row per <see cref="NeonSidekick.EmbeddedLlm.EmbeddedHfDownloadTypes.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickEmbeddedHfDownloadTypeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = NeonSidekick.EmbeddedLlm.EmbeddedHfDownloadTypes.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.EmbeddedHfDownloadType)), names.Select(EmbeddedHfDownloadTypeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.FindIndex(names, n => string.Equals(n, saved.EmbeddedHfDownloadType, StringComparison.OrdinalIgnoreCase))), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.EmbeddedHfDownloadType, d => d.EmbeddedHfDownloadType = name);
        return true;
    }

    /// <summary>An HF download type picker row: the name padded, then what it does, dim.</summary>
    public static string EmbeddedHfDownloadTypeLabel(string name) =>
        Markup.Escape(name.PadRight(10)) + Theme.DimMarkup(NeonSidekick.EmbeddedLlm.EmbeddedHfDownloadTypes.Describe(name));

    /// <summary>The Botchat multi-embedded picker (later on 2026-09-29): one <see cref="BotChatMultiEmbeddedLabel"/> row per <see cref="App.BotChatMultiEmbedded.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickBotChatMultiEmbeddedAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = App.BotChatMultiEmbedded.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.BotChatMultiEmbedded)), names.Select(BotChatMultiEmbeddedLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.FindIndex(names, n => string.Equals(n, saved.BotChatMultiEmbedded, StringComparison.OrdinalIgnoreCase))), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.BotChatMultiEmbedded, d => d.BotChatMultiEmbedded = name);
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

    /// <summary>The reasoning-estimate picker under the settings list (2026-09-29): one <see cref="ReasoningEstimateLabel"/> row per <see cref="Llm.ReasoningEstimates.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickReasoningEstimateAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = Llm.ReasoningEstimates.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.LlmReasoningEstimate)), names.Select(ReasoningEstimateLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.FindIndex(names, n => string.Equals(n, saved.LlmReasoningEstimate?.Trim(), StringComparison.OrdinalIgnoreCase))), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.LlmReasoningEstimate, d => d.LlmReasoningEstimate = name);
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

    /// <summary>The STT-destination picker under the settings list (2026-10-02): one <see cref="SttDestinationLabel"/> row per <see cref="SttDestinationMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickSttDestinationAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = SttDestinationMode.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.SttDestination)), names.Select(SttDestinationLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, saved.SttDestination)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.SttDestination, d => d.SttDestination = name);
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
    /// The <c>Botchat ComfyUI limited workflows</c> checklist (later on 2026-10-04, the user's ask; in place of the
    /// <c>Botchat txt2img workflow</c> / <c>Botchat img2img workflow</c> pickers of 2026-09-25 and 2026-09-27):
    /// <see cref="EditWorkflowChecklistAsync"/>, <c>ComfyUI workflows offered</c>'s loop, over every installed workflow — <c>ComfyUI workflows offered</c> has no say (the
    /// user's pick, the pickers' rule) — each row with its kind, family and size (<see cref="ComfyOfferedRow"/>). Enter or Space flips
    /// one, A every one, N none, saved at once; a name ticked before but no longer installed stays in the list. Its caption says the
    /// list waits while <c>Botchat ComfyUI enabled</c> is on.
    /// </summary>
    private Task<bool> EditBotChatLimitedComfyAsync(CancellationToken cancellationToken) =>
        EditWorkflowChecklistAsync(
            SettingsField.BotChatLimitedComfyWorkflows,
            d => d.BotChatLimitedComfyWorkflows,
            (d, next) => d.BotChatLimitedComfyWorkflows = next.Count == 0 ? null : next,
            d => d.BotChatComfy ? LimitedComfyUnusedCaption : null,
            cancellationToken);

    /// <summary>The memory-mode picker under the settings list (2026-10-04): one <see cref="MemoryModeLabel"/> row per <see cref="App.MemoryMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickMemoryModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = App.MemoryMode.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.MemoryMode)), names.Select(MemoryModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, saved.MemoryMode)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        if (string.Equals(name, saved.MemoryMode, StringComparison.Ordinal))
        {
            return Unchanged();   // the mode in force picked again, as a toggle's own value is
        }

        Apply(SettingsField.MemoryMode, d => d.MemoryMode = name);
        return true;
    }

    /// <summary>The botchat-memory-mode picker under the settings list (2026-10-04): one <see cref="BotChatMemoryModeLabel"/> row per <see cref="App.BotChatMemoryMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickBotChatMemoryModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = App.BotChatMemoryMode.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.BotChatMemoryMode)), names.Select(BotChatMemoryModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, saved.BotChatMemoryMode)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.BotChatMemoryMode, d => d.BotChatMemoryMode = name);
        return true;
    }

    /// <summary>
    /// The <c>Botchat limited skills</c> checklist (2026-09-27 as the preloaded skills'), <see cref="EditComfyOfferedAsync"/>'s loop
    /// over the skills a botchat sees: Enter or Space flips one, saved at once. A ticked name with no skill listed now (external skills
    /// off, another folder's own) is a row of its own after them (<see cref="LimitedGoneRow"/>, 2026-10-04, the user's call: until
    /// then it rode along unseen and even N kept it), never dropped by itself since it may come back; Enter unticks it, A keeps it, N
    /// clears it with the rest. Its caption says the list waits while <c>Botchat skills enabled</c> is on (2026-10-04).
    /// </summary>
    private async Task<bool> EditBotChatLimitedSkillsAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var skills = _botChatSkills();
            var chosen = _settings.Current.BotChatLimitedSkills ?? [];
            var gone = OfferedNames.Gone(chosen, skills.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            if (skills.Count == 0 && gone.Count == 0)
            {
                Sink.Error(NoSkillsToLimit);
                return changed;
            }

            var on = chosen.Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var names = skills.Select(s => s.Name).Concat(gone).ToList();
            int width = names.Max(n => n.Length) + 2;
            var rows = skills.Select(s => LimitedSkillRow(s, on.Contains(s.Name), width)).Concat(gone.Select(n => LimitedGoneRow(n, width))).ToList();
            var page = new MenuPage(Crumb(FieldName(SettingsField.BotChatLimitedSkills)), rows, ToggleKeys)
            {
                SpaceToggles = true,
                Caption = _settings.Current.BotChatSkills ? LimitedSkillsUnusedCaption : null,
            };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, names.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            string name = names[pick.Row];
            var next = pick.Button == SelectAllIndex ? [.. names]
                : pick.Button == SelectNoneIndex ? []
                : names.Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (next.Count == names.Count(on.Contains) && next.All(on.Contains))
            {
                continue;   // a button that changes nothing saves nothing
            }

            Apply(SettingsField.BotChatLimitedSkills, d => d.BotChatLimitedSkills = next.Count == 0 ? null : next);
            changed = true;
        }
    }

    /// <summary>
    /// The <c>Botchat limited tools</c> checklist (2026-10-04, the user's ask): the main chat's tools by group
    /// (<see cref="LimitedToolRows"/>, the memory and skill groups left out — their own botchat switches say), headings never a
    /// stop. Enter or Space flips one, A every listed tool, N none, saved at once. A ticked name not listed now (an MCP server not
    /// connected) is a row under <see cref="NotAvailableHeading"/> at the end (2026-10-04, the user's call: until then it rode along
    /// unseen and even N kept it), never dropped by itself since it may come back; Enter unticks it, A keeps it, N clears it with the
    /// rest. Its caption says the list waits while <c>Botchat tools enabled</c> is on.
    /// </summary>
    private async Task<bool> EditBotChatLimitedToolsAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = -1;
        while (true)
        {
            var groups = _botChatTools();
            var listed = groups.SelectMany(g => g.Tools).Select(t => t.Name).Distinct(StringComparer.Ordinal).ToList();
            var chosen = _settings.Current.BotChatLimitedTools ?? [];
            var gone = OfferedNames.Gone(chosen, listed, StringComparer.Ordinal);
            var names = listed.Concat(gone).ToList();
            if (names.Count == 0)
            {
                Sink.Error(NoToolsToLimit);
                return changed;
            }

            var on = chosen.Select(n => n.Trim()).ToHashSet(StringComparer.Ordinal);
            var rows = LimitedToolRows(groups, on, gone);
            if (cursor < 0)
            {
                cursor = Math.Max(0, rows.ToList().FindIndex(r => r.Tool is not null));
            }

            var page = new MenuPage(Crumb(FieldName(SettingsField.BotChatLimitedTools)), rows.Select(r => r.Markup).ToList(), ToggleKeys)
            {
                SpaceToggles = true,
                Headings = ToolsText.HeadingRows(rows),
                Caption = _settings.Current.BotChatTools ? LimitedToolsUnusedCaption : null,
            };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, rows.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            string? name = rows[pick.Row].Tool;
            List<string> next;
            if (pick.Button == SelectAllIndex)
            {
                next = [.. names];
            }
            else if (pick.Button == SelectNoneIndex)
            {
                next = [];
            }
            else if (name is null)
            {
                continue;   // a heading or a gap (the prompt without the pane lists them too) flips nothing
            }
            else
            {
                next = names.Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.Ordinal)).ToList();
            }

            if (next.Count == names.Count(on.Contains) && next.All(on.Contains))
            {
                continue;   // a button that changes nothing saves nothing
            }

            Apply(SettingsField.BotChatLimitedTools, d => d.BotChatLimitedTools = next.Count == 0 ? null : next);
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

    /// <summary>The botchat-image-mention-mode picker under the settings list (2026-10-13): one <see cref="BotChatImageMentionModeLabel"/> row per <see cref="App.BotChatImageMentionMode.Names"/> entry, the saved one under the cursor.</summary>
    private async Task<bool> PickBotChatImageMentionModeAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = App.BotChatImageMentionMode.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.BotChatImageMentionMode)), names.Select(BotChatImageMentionModeLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, saved.BotChatImageMentionMode)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.BotChatImageMentionMode, d => d.BotChatImageMentionMode = name);
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

    /// <summary>
    /// The Hints bar picker under the settings list (2026-10-14): one <see cref="HintsBarLabel"/> row per <see cref="UI.HintsBarMode.Names"/>
    /// entry, the word in force under the cursor (a hand-edited word, the default's). The pane follows at its next tick.
    /// </summary>
    private async Task<bool> PickHintsBarAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        string current = UI.HintsBarMode.Name(UI.HintsBarMode.Resolve(saved.HintsBar));
        var page = new MenuPage(Crumb(FieldName(SettingsField.HintsBar)), UI.HintsBarMode.Names.Select(HintsBarLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(UI.HintsBarMode.Names, current), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = UI.HintsBarMode.Names[index];
        Apply(SettingsField.HintsBar, d => d.HintsBar = name == UI.HintsBarMode.Default ? null : name);
        return true;
    }

    /// <summary>
    /// The menus-max-height picker under the settings list (2026-10-01): one <see cref="MenuMaxHeightLabel"/> row per
    /// <see cref="UI.MenuHeight.Names"/> entry, the height in force under the cursor (a hand-edited word, its default).
    /// The pane re-sizes to the pick at its next draw, this list's included.
    /// </summary>
    private async Task<bool> PickMenuMaxHeightAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        UI.MenuHeight.TryParse(saved.MenuMaxHeight, out var current);
        var page = new MenuPage(Crumb(FieldName(SettingsField.MenuMaxHeight)), UI.MenuHeight.Names.Select(MenuMaxHeightLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(UI.MenuHeight.Names, UI.MenuHeight.Name(current)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = UI.MenuHeight.Names[index];
        Apply(SettingsField.MenuMaxHeight, d => d.MenuMaxHeight = name);
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
        var themes = ScanThemes().Themes;
        var page = new MenuPage(Crumb(FieldName(SettingsField.Theme)), ThemeRows(themes), ThemePickKeys) { Side = ThemeSide(themes), JumpNames = ThemeNames(themes) };
        int? picked = await PickAsync(page, ThemeCursor(saved.Theme, themes), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        var palette = themes[index];
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
    /// Without menus the guard prints. The user's themes (2026-10-01) are read afresh each time, their
    /// files' problems said as warnings; <c>/theme export …</c> is <see cref="ExportTheme"/>.
    /// </summary>
    public async Task<bool> PickThemeAsync(string requestedName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedName);
        string request = requestedName.Trim();
        if (request.Equals(ThemeText.ExportWord, StringComparison.OrdinalIgnoreCase)
            || request.StartsWith(ThemeText.ExportWord + " ", StringComparison.OrdinalIgnoreCase))
        {
            ExportTheme(request[ThemeText.ExportWord.Length..]);
            return false;
        }

        var scan = ScanThemes();
        if (request.Length > 0)
        {
            if (!ThemeName.TryParse(request, scan.Themes, out var named))
            {
                Flow.Error(ThemeNameError(request, scan.Names));
                return false;
            }

            return SaveTheme(named);
        }

        if (!CanShowMenus())
        {
            Flow.Error(MenusNeedTerminalError);
            return false;
        }

        var page = new MenuPage(ThemeTitle, ThemeRows(scan.Themes), ThemeKeepKeys) { Side = ThemeSide(scan.Themes), JumpNames = ThemeNames(scan.Themes) };
        int? picked = await PickOnceAsync(page, ThemeCursor(Theme.Current.Name, scan.Themes), cancellationToken).ConfigureAwait(false);
        return picked is { } index ? SaveTheme(scan.Themes[index]) : Unchanged();
    }

    /// <summary>
    /// <c>/theme export &lt;name&gt; [new-name]</c> (2026-10-01, the user's ask): writes the theme as a full file —
    /// every colour role, the gradient, its style changes (<see cref="ThemeFile.Export"/>) — to
    /// <c>&lt;home&gt;/themes/&lt;new-name&gt;.json</c>, a starting point to edit. The new name defaults to
    /// <c>&lt;name&gt;-custom</c>, so an export never replaces the theme it copies; a built-in's name makes the file its
    /// override (later on 2026-10-01, the user's call: <see cref="ThemeCatalog"/>'s file wins, and the export says so).
    /// Never overwrites a file and never takes a name a user theme already has, an override's included. Nothing is put in force.
    /// </summary>
    private void ExportTheme(string args)
    {
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is 0 or > 2)
        {
            Flow.Error(ThemeText.ExportUsage);
            return;
        }

        var scan = ScanThemes();
        if (!ThemeName.TryParse(words[0], scan.Themes, out var palette))
        {
            Flow.Error(ThemeNameError(words[0], scan.Names));
            return;
        }

        string newName = words.Length == 2 ? words[1].ToLowerInvariant() : palette.Name + "-custom";
        if (!ThemeFile.IsValidName(newName))
        {
            Flow.Error(ThemeText.ExportBadName(newName));
            return;
        }

        bool named = ThemeName.TryParse(newName, scan.Themes, out var taken);
        if (named && !taken.IsBuiltIn)
        {
            Flow.Error(ThemeText.ExportNameTaken(newName));
            return;
        }

        string path = Path.Combine(_settings.ThemesDirectory, newName + ".json");
        try
        {
            Directory.CreateDirectory(_settings.ThemesDirectory);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(ThemeFile.Export(palette, newName));
        }
        catch (IOException) when (File.Exists(path))
        {
            Flow.Error(ThemeText.ExportExists(path));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Flow.Error(ThemeText.ExportFailed(ex.Message));
            return;
        }

        Sink.Notice(named
            ? ThemeText.ExportOverrides(newName, path)
            : ThemeText.ExportDone(newName, path));
    }

    /// <summary>The themes as of now, the built-ins and the user's, each file's problem said as a warning.</summary>
    private ThemeScan ScanThemes()
    {
        var scan = ThemeCatalog.Scan(_settings.ThemesDirectory);
        ThemeCatalog.Report(scan);
        return scan;
    }

    /// <summary>Saves <paramref name="palette"/>'s name (when the saved one differs) and puts it in force; true when the theme in force changed, else <see cref="ThemeAlreadyNotice"/>.</summary>
    private bool SaveTheme(ThemePalette palette)
    {
        bool changed = !Theme.Current.Equals(palette);   // by look: a user theme is a new instance at every scan
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

    /// <summary>
    /// Both theme pickers' column beside the list (2026-10-02, the user's ask): the highlighted theme's
    /// <see cref="ThemePreview"/>, in its own styles while the pane stays in the one in force, so the screen starts over only
    /// on the pick, as before. On the terminal's own background while <c>Themed background</c> is off (2026-10-03), read per draw.
    /// </summary>
    private Func<int, int, int, IReadOnlyList<Spectre.Console.Rendering.IRenderable>> ThemeSide(IReadOnlyList<ThemePalette> themes) =>
        (row, width, rows) => ThemePreview.Lines(themes[row], width, rows, SidekickApp.BannerTitle.Trim(), SidekickApp.Version, EffectiveNow().ThemedBackground, EffectiveNow().DiffCollapseCount);

    /// <summary>One <see cref="ThemeLabel(string, IReadOnlyList{ThemePalette})"/> row per theme of <paramref name="themes"/>, in its order.</summary>
    private static List<string> ThemeRows(IReadOnlyList<ThemePalette> themes) => themes.Select(t => ThemeLabel(t.Name, themes)).ToList();

    /// <summary>The names a typed letter jumps by (<see cref="MenuPage.JumpNames"/>), row for row with <see cref="ThemeRows"/>.</summary>
    private static List<string> ThemeNames(IReadOnlyList<ThemePalette> themes) => themes.Select(t => t.Name).ToList();

    /// <summary>The row of <paramref name="name"/> among <paramref name="themes"/>; an unknown one reads as the default's (<see cref="ThemeName.Default"/>, no longer the first row since the list is A to Z, 2026-10-03), else the first.</summary>
    private static int ThemeCursor(string name, IReadOnlyList<ThemePalette> themes)
    {
        int Row(string wanted) => themes.ToList().FindIndex(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase));
        int row = Row(name.Trim());
        return row >= 0 ? row : Math.Max(0, Row(ThemeName.Default));
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
    /// A checklist's opening prune (2026-10-04, the user's call, <see cref="OfferedNames"/>): with <paramref name="stale"/> not empty,
    /// saves <paramref name="saved"/> without them through <paramref name="write"/> and says which went
    /// (<see cref="StaleDroppedNotice"/>). True when it saved, so the checklist's ESC says no "unchanged" over it.
    /// </summary>
    private bool PruneStale(SettingsField field, IReadOnlyList<string>? saved, IReadOnlyList<string> stale, StringComparer comparer, Action<AppSettingsData, List<string>> write)
    {
        if (stale.Count == 0)
        {
            return false;
        }

        var kept = OfferedNames.Without(saved, stale, comparer);
        Apply(field, d => write(d, kept));
        Sink.Notice(StaleDroppedNotice(field, stale));
        return true;
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

    /// <summary><c>5 passes</c>, <c>1 pass</c>, <see cref="NoCapLabel"/> for 0: <see cref="AppSettingsData.PlanGapPasses"/> as the row shows it (2026-10-13).</summary>
    public static string GapPasses(int value) => value <= 0 ? NoCapLabel : value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " pass" : " passes");

    /// <summary><c>500 entries</c>, <c>1 entry</c>: the <c>/tree</c> cap as the row shows it.</summary>
    public static string Entries(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " entry" : " entries");

    /// <summary><c>25 lines</c>, <c>1 line</c>, <c>off</c> at 0: the paste preview count as the row shows it. Pinned.</summary>
    public static string Lines(int value) => value == 0 ? "off" : value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " line" : " lines");

    /// <summary><c>10 lines</c>, <c>header only</c> at 0: <c>Diff max lines</c> as the row shows it (2026-10-03).</summary>
    public static string DiffLines(int value) => value == 0 ? "header only" : Lines(value);

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

    /// <summary>The <c>Shell police forbidden strings</c> row's value: <c>3 strings</c>, <c>1 string</c>, <c>none</c> at 0 (2026-10-03). Pinned.</summary>
    public static string Strings(int value) => value == 0 ? "none" : value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " string" : " strings");

    /// <summary>The <c>Shell output max chars</c> row's value: <c>30,000 chars</c> (2026-09-21). Pinned.</summary>
    public static string Chars(int value) => value.ToString("N0", CultureInfo.InvariantCulture) + " chars";

    /// <summary>The <c>Web download max (MB)</c> row's value: <c>50 MB</c>, <c>102,400 MB</c> (2026-10-01). Pinned.</summary>
    public static string Megabytes(int value) => value.ToString("N0", CultureInfo.InvariantCulture) + " MB";

    /// <summary><c>20 commits</c> (the GitLib log cap, 2026-09-20).</summary>
    public static string Commits(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " commit" : " commits");

    /// <summary>The <c>Ask max questions</c> row's value: <c>10 questions</c>, <c>1 question</c>. Pinned.</summary>
    public static string Questions(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " question" : " questions");

    /// <summary>The <c>File view image max (per call)</c> row's value: <c>10 pictures</c>, <c>1 picture</c>. Pinned.</summary>
    public static string Pictures(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " picture" : " pictures");

    /// <summary>The <c>Ask max choices per question</c> row's value: <c>10 choices</c>, <c>1 choice</c>. Pinned.</summary>
    public static string Choices(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " choice" : " choices");

    private static string OnOff(bool on) => on ? "on" : "off";
}
