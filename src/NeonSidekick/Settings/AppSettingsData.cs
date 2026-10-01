namespace NeonSidekick.Settings;

/// <summary>
/// Everything the user can change, as one flat object with one concrete property per knob and a
/// default on every one.
///
/// <para><b>Not a <c>Dictionary&lt;string, object&gt;</c>.</b> Source-generated JSON needs concrete
/// types; a dictionary of boxed values is the reflection path NativeAOT turns off. It would
/// compile, pass under the JIT, and throw in the published binary.</para>
///
/// <para><b>The key is the label (2026-09-17, the user's call):</b> the tab's prefix as the row shows
/// it (<c>Llm</c>, <c>Tts</c>, <c>Stt</c>, <c>Ask</c>, <c>File</c>/<c>Tree</c>, <c>Web</c>,
/// <c>Skill</c>/<c>Reflection</c>, <c>Session</c>, <c>GitLib</c> for the GitLib tab (<c>GitNative</c> 2026-09-21 to 2026-09-30); none on General) and the label's words, no <c>Enabled</c> suffix on a
/// switch; a relabelled row is renamed with it. <see cref="SchemaVersion"/> first, then the five
/// <c>/settings</c> tabs' blocks in the tabs' order, then the <c>/skills</c> pane's Options tab (Skills),
/// then the <c>/tools</c> pane's (Tools, Ask, Files, Git, Shell, Web — the strip's order until 2026-09-21, when it became
/// Web, Files, Shell, Ask, Git (native), the user's order; the blocks stayed put) —,
/// then the <c>/mcp</c> pane's (MCP, 2026-09-20), alphabetical within — the file reads like the four
/// panes; a new field goes into its block, with a default, and into <c>AppSettings.Copy</c>. Three keys
/// are not settings rows: <see cref="ToolsDisabled"/>, a list flipped on <c>/tools</c>' Offered tab (the
/// Tools block holds it and the Options tab's one row, <see cref="ToolsDollarMention"/>),
/// <see cref="McpServersDisabled"/>, the second list, flipped on <c>/mcp</c>' Servers tab, and
/// <see cref="ProjectFile"/>, flipped on <c>/skills</c>' Project tab. The deserializer supplies
/// the default when a key is absent and skips one it does not know, so a file written by an older
/// build loads — a renamed or retired key simply takes its default (no migration, the user's call
/// over keeping every old spelling alive).</para>
///
/// <para>Any of these may be overridden per launch by an environment variable; see
/// <see cref="EnvironmentOverrides"/>. A variable always outranks the saved value.</para>
/// </summary>
public sealed class AppSettingsData
{
    /// <summary>Bumped when a field changes meaning rather than merely being added: 1 until 2026-09-17, 2 since the keys were renamed to follow their labels and regrouped by tab (no migration: an old key is skipped, its default stands).</summary>
    public int SchemaVersion { get; set; } = 2;

    // ─── General ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a sent line that is exactly a command's name without its slash (<c>clear</c>) is
    /// held on the pane with <c>/clear</c> offered first: Enter puts the command on the line, ESC
    /// sends the text as typed (2026-09-18). Pane only, read at each Enter. No variable.
    /// </summary>
    public bool CommandTypoIntercept { get; set; } = true;

    /// <summary>
    /// Whether the input line's Up/Down history outlives the app (2026-09-25, the user's ask): on, every line the
    /// history gains is stored in the profile's <c>sessions.db</c> (<c>command_history</c>, the newest
    /// <see cref="Sessions.SessionStore.CommandHistoryCap"/>) and loaded again at startup and after a switch to the
    /// profile; a line holding a collapsed paste or a picture is not stored (its block is the session's). Off, nothing
    /// is stored and what is stored goes the next time the profile loads. <c>/cmdclear</c> empties it either way.
    /// Read at each remembered line and at each load. No variable.
    /// </summary>
    public bool KeepCommandHistory { get; set; } = true;

    /// <summary>
    /// Whether <c>/copy</c> puts the user's own prompt (as a blockquote) above each reply it
    /// copies, or the replies alone. A toggle like <see cref="Memory"/>: no variable.
    /// </summary>
    public bool CopyUserPrompt { get; set; } = true;

    /// <summary>
    /// The command line <c>/draft</c> opens its temporary file with (2026-09-19), the file's path
    /// appended, run through <c>cmd.exe</c> so a word on the <c>PATH</c> works (<c>code --wait</c>,
    /// <c>notepad</c>); empty = whatever Windows opens a <c>.txt</c> with. An editor that hands the
    /// file to a window already open returns at once and nothing is sent — this row names one that
    /// waits. Read at each <c>/draft</c>, no reconnect. No variable.
    /// </summary>
    public string DraftEditor { get; set; } = "";

    /// <summary>
    /// The command line a double-clicked picture in the transcript opens in (later on 2026-09-24, the user's ask; the menu's
    /// <c>Image viewer</c> row since later still that day, <c>Image editor</c> before, the name kept so saved profiles read), the
    /// file's path appended, run through <c>cmd.exe</c> as <see cref="DraftEditor"/> is (<c>mspaint</c>,
    /// <c>"C:\Program Files\GIMP 3\bin\gimp-3.exe"</c>); empty = the built-in picture viewer on the picture's folder (later
    /// on 2026-09-27, the user's call; the registered app off Windows), <c>system</c> (<see cref="Viewer.ViewerText.SystemViewerWord"/>)
    /// = the image editor Windows registers for the type (Paint), else its viewer — what empty meant before. Read at each
    /// double-click, no reconnect. No variable.
    /// </summary>
    public string ImageEditor { get; set; } = "";

    /// <summary>
    /// Whether the built-in picture viewer wears the theme (later on 2026-09-27, the user's ask; the menu's <c>Themed image
    /// viewer</c> row): on (the default), a dark title bar in the theme's colours and its background round the picture
    /// (<see cref="Viewer.ViewerStyle"/>); off, a black bar and black round the picture, the look before the theme reached it.
    /// Read each time the viewer opens or is focused, no reconnect. No variable.
    /// </summary>
    public bool ThemedViewer { get; set; } = true;

    /// <summary>
    /// Where the built-in picture viewer last closed (2026-09-28, the user's ask: it opens there the next time; position
    /// only, always on — the user's calls): the top-left corner of its restored placement in workspace coordinates, set by
    /// the viewer itself as it closes (<see cref="Viewer.PictureWindow.Placed"/>), never by a menu row. Null, as until the
    /// first close, is Windows' own default place; a corner on a monitor since unplugged is brought back into view by
    /// Windows as the window opens. No variable.
    /// </summary>
    public int? ViewerLeft { get; set; }

    /// <summary>The top of that corner; see <see cref="ViewerLeft"/>. Either one null and the viewer opens where Windows puts it.</summary>
    public int? ViewerTop { get; set; }

    /// <summary>
    /// Whether the <c>/</c> completion list leaves <c>/exit</c> out (on by default, 2026-09-18) so a
    /// pick never ends the app by mistake; typed in full it exits as ever. Read at each keystroke. No variable.
    /// </summary>
    public bool HideExitAutocomplete { get; set; } = true;

    /// <summary>
    /// How big the thumbnail under a sent picture is drawn (<see cref="ShowImageThumbnails"/>):
    /// <c>tiny</c> (32 columns × 8 rows), <c>small</c> (48 × 12), <c>medium</c> (64 × 16), <c>large</c> (80 × 20), <c>xlarge</c> (96 × 24) or <c>fullsize</c>
    /// (2026-09-24: each picture as large as the transcript window allows, more than one wrapping below). One of
    /// <see cref="UI.ThumbnailSize.Names"/>; anything else reads as <see cref="UI.ThumbnailSize.Default"/>. No variable.
    /// </summary>
    public string ImageThumbnailSize { get; set; } = UI.ThumbnailSize.Default;

    /// <summary>
    /// Whether long-term memory is on: the model is offered <c>save_memory</c> and sees what is
    /// remembered on every turn, and <c>/remember</c> works. Off leaves <c>memory.json</c>
    /// untouched; <c>/memory forget</c> erases it. The toolbar wears 💾 while it is on (2026-09-22), whose
    /// double-click is <c>/memory</c> — the typed word works either way. No environment variable,
    /// like the other switches.
    /// </summary>
    public bool Memory { get; set; } = true;

    /// <summary>
    /// What <c>/profile add</c> copies from the current profile: <c>basic</c> (the default: the settings and
    /// the memories) or <c>advanced</c> (the settings plus the memories, persona, operating rules and voice
    /// directive files, each when it exists). One of <see cref="Settings.NewProfileMode.Names"/>; anything
    /// else reads as <see cref="Settings.NewProfileMode.Default"/>. No variable.
    /// </summary>
    public string NewProfileMode { get; set; } = Settings.NewProfileMode.Default;

    /// <summary>
    /// How many lines of a collapsed paste the transcript shows under the sent line (2026-09-16),
    /// dim, closed by <c>[… +K more lines]</c> when the block is longer: 0 to
    /// <see cref="UI.PasteBlocks.MaxPreviewLines"/>, 0 = the <c>[Pasted text #n +L lines]</c>
    /// label alone. Read at each idle read, no reconnect; the model and <c>/copy</c> get the whole
    /// block whatever this says. No variable.
    /// </summary>
    public int PastePreviewLines { get; set; } = UI.PasteBlocks.DefaultPreviewLines;

    /// <summary>
    /// What a cancelled reply does to the messages queued behind it (2026-09-18): <c>hold</c>
    /// (nothing sent by itself until the next reply ends normally), <c>drain</c> (the next one at
    /// once) or <c>empty</c> (all dropped, with a notice) — <see cref="App.QueueCancelMode"/>.
    /// Read when a turn ends, no reconnect. No variable.
    /// </summary>
    public string QueueCancelMode { get; set; } = App.QueueCancelMode.Default;

    /// <summary>
    /// Whether a message sent while a reply runs waits in the queue (<see cref="App.MessageQueue"/>,
    /// sent when the reply ends, listed by <c>/queue</c>) or stays type-ahead on the input line as
    /// before (2026-09-18) — since the row became a live editor under the reply (2026-09-25), off means the line waits
    /// for the idle line unlisted and is sent as the reply ends all the same; off, <c>/queue</c> leaves the input line's
    /// <c>/</c> list too (later on 2026-09-18). Pane only, read at each mid-turn Enter and each keystroke. No variable.
    /// </summary>
    public bool QueueMessages { get; set; } = true;

    /// <summary>
    /// Whether each picture sent with a message is drawn under the user's line as a thumbnail
    /// (<see cref="UI.ImageStrip"/>); off, it is attached and labelled just the same and nothing is
    /// drawn. A toggle like <see cref="Memory"/>: no variable.
    /// </summary>
    public bool ShowImageThumbnails { get; set; } = true;

    /// <summary>
    /// Whether the working directory in force (the resolved full path, what <c>/cwd</c> prints)
    /// sits at the right edge of the banner's title line, cut from the front to fit (2026-09-18).
    /// Read at each banner draw — startup, <c>/clear</c>, a profile switch, the splash dismissal —
    /// so a <c>/cwd</c> change or a flip of this shows at the next of those. Off by default since
    /// 2026-09-21 (the user's call: the toolbar carries the path now). No variable.
    /// </summary>
    public bool ShowWorkingDirectory { get; set; }

    /// <summary>
    /// What the toolbar under the hint row shows (2026-09-21, the user's ask; a checklist since 2026-09-29, the user's
    /// ask, in place of the <c>ShowToolbar</c> switch — a saved <c>false</c> there is dropped as any retired key, so the
    /// row comes back once): ids from <see cref="App.ToolbarItems.Names"/>. The pane glyphs sit at its left (a
    /// double-click opens <c>/settings</c>, <c>/tools</c>, <c>/mcp</c>, <c>/skills</c>, <c>/sys</c>, <c>/sessions</c>,
    /// <c>/usage</c> (📊, since 2026-09-29), then 💾 <c>/memory</c> while <see cref="Memory"/> is on, the lock
    /// <c>/cmdlist</c> that follows <see cref="ShellCommandPolicy"/>, and 👮 while <see cref="ShellPoliceOutsidePaths"/>
    /// is on — the lock since later that day, the disk and the officer since 2026-09-22), the working directory in force
    /// (<c>/cwd browse</c>) at its right. Null is <see cref="App.ToolbarItems.Defaults"/> — Settings, Tools, Skills, Sessions
    /// and the path since later on 2026-09-29 (the user's pick; every item, one added later too, before); an empty list draws
    /// no row at all.
    /// Read on every pane draw and on its tick, so a change shows when the settings pane closes. No variable.
    /// </summary>
    public List<string>? ToolbarItems { get; set; }

    /// <summary>
    /// The items a bare <c>/tb</c> (or Ctrl+Alt+B, or <c>/tb on</c>) brings back after it hid the toolbar (later on 2026-09-30,
    /// the user's ask: "same as how /perf works for the perfbar"): what showed when it hid it, as <see cref="ToolbarItems"/>
    /// saves it; null until then, or when that was <see cref="App.ToolbarItems.Defaults"/>, which come back. No settings row,
    /// no variable.
    /// </summary>
    public List<string>? ToolbarLastItems { get; set; }

    /// <summary>
    /// The performance bar's meters (2026-09-30, the user's ask: <c>Show performance bar</c> a checklist like Show toolbar,
    /// none by default, in place of the one word — <c>ShowPerformanceBar</c>, off or a look, from 2026-09-29 — that was both
    /// the switch and the look; that key is retired, so a profile saved with it shows no bar until a meter is checked): the
    /// ids of <see cref="App.PerfBarItems.Names"/> — CPU, RAM, GPU, VRAM, and the network's NET, NET↓ and NET↑ — in that order.
    /// Null (the default) is none: no row, nothing sampled. The GPU meters read NVIDIA's NVML where an NVIDIA GPU answers, else
    /// Windows' own counters (PDH) for the adapter with the most dedicated memory (DXGI); the network's, .NET's own counters of
    /// the adapters with a gateway. A meter the machine cannot read is left out. Read on every pane tick, so a change shows at
    /// once; sampled once a second while any is checked. The row keeps the label <c>Show performance bar</c> (the key differs,
    /// as <see cref="ToolbarItems"/>' does). No variable.
    /// </summary>
    public List<string>? PerformanceBarItems { get; set; }

    /// <summary>
    /// The meters a bare <c>/perf</c> (or the toolbar's 📈) brings back after it hid the bar (2026-09-30): what was checked when
    /// it hid it; null until then, when <see cref="App.PerfBarItems.Restored"/> (CPU, RAM, GPU and VRAM) come back. No settings
    /// row, no variable.
    /// </summary>
    public List<string>? PerformanceBarLastItems { get; set; }

    /// <summary>
    /// The performance bar's look (later on 2026-09-29, the user's ask: "the last look used"; the look itself since
    /// 2026-09-30): one of <see cref="App.PerfBarMode.Names"/> — <c>text</c> (the default), <c>gauge</c>, <c>spark</c> or
    /// <c>led</c> — picked on the <c>Show performance bar</c> page's title row or by <c>/perf &lt;look&gt;</c>. Anything else
    /// reads as <c>text</c>. No row of its own, no variable.
    /// </summary>
    public string PerformanceBarLook { get; set; } = App.PerfBarMode.Default;

    /// <summary>
    /// The look (2026-09-23, the user's ask): <c>synthwave</c> (the default), <c>netrunner</c>,
    /// <c>nostromo</c>, <c>noir</c>, <c>cyberpunk</c>, <c>vaporwave</c>, <c>mainframe</c>, <c>grid</c>,
    /// <c>replicant</c> or <c>abyssal</c> (the last four 2026-09-27) — one of
    /// <see cref="UI.ThemeName.Names"/>; anything else reads as <see cref="UI.ThemeName.Default"/>.
    /// Put in force at startup; a change on the row or with <c>/theme</c> starts over the way
    /// <c>/splash</c> does (the user's call: a fresh session and the splash in the new colours,
    /// <c>/sessions</c> brings the old one back). No variable.
    /// </summary>
    public string Theme { get; set; } = UI.ThemeName.Default;

    /// <summary>
    /// Whether the assistant's reply is shown as styled Markdown (2026-09-16): bold, lists, code
    /// blocks, headings rendered in the pane's live slot, the markers consumed, and the model asked
    /// for light Markdown instead of plain text on a turn that is not spoken. Off = the plain
    /// streamed text and the plain-text rule, as before. Read at each turn, no reconnect; nothing
    /// without the pane (headless keeps plain text whatever this says). No variable.
    /// </summary>
    public bool TranscriptMarkdown { get; set; } = true;

    /// <summary>
    /// How the splash pictures (<c>UI.SplashImages</c>, the repo's <c>assets\splash</c> or the profile's
    /// own folder) greet the user under the banner at startup, until the first sent line wipes the
    /// screen back to the banner (2026-09-18): one of <see cref="UI.SplashMode.Names"/> — <c>fullsize</c>
    /// (one picture filling the transcript region, the default), <c>tiled</c> (thumbnails at
    /// <see cref="ImageThumbnailSize"/>, a screenful at a time, Left / Right page) or <c>disabled</c>.
    /// Anything else reads as <see cref="UI.SplashMode.Default"/>. <c>/clear</c> never brings it back.
    /// Read at each show; nothing without the pane. Replaced the on/off <c>WelcomeSplash</c> on
    /// 2026-09-24, no migration (the user's call: the old key is skipped, its default stands). No variable.
    /// </summary>
    public string WelcomeSplashMode { get; set; } = UI.SplashMode.Default;

    /// <summary>
    /// The folder the file tools may read and write, a full path. Empty means the profile's own
    /// <c>files\</c> folder (<c>WorkingDirectory.Resolve</c>), so every profile has a sandbox
    /// from its first turn and the setting only ever points it somewhere else. No environment
    /// variable, by design: a variable is not per-profile, and <c>--cwd</c> covers "this launch".
    /// </summary>
    public string WorkingDirectory { get; set; } = "";

    // ─── Sessions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether every completed turn is written to this profile's <c>sessions.db</c>
    /// (<see cref="Sessions.SessionStore"/>, 2026-09-18): a session per conversation, restorable with
    /// <c>/sessions</c>. Off writes nothing; what is stored still lists, restores and purges. Read at
    /// each turn, no reconnect; the Sessions tab's first row. No variable.
    /// </summary>
    public bool SessionLogging { get; set; } = true;

    /// <summary>
    /// Where a new session's title comes from: one of <see cref="Sessions.SessionNamingMode.Names"/> —
    /// <c>first-line</c> (the first sent line, cut) or <c>model-written</c> (a background request after
    /// the first turn asks the model; the first line stands until it answers). Anything else reads
    /// as <see cref="Sessions.SessionNamingMode.Default"/> with a warning. No variable.
    /// </summary>
    public string SessionNamingMode { get; set; } = Sessions.SessionNamingMode.Default;

    /// <summary>
    /// Days a session is kept after its last turn: at startup and after a profile switch, sessions
    /// last updated longer ago are purged without a word (the <c>/sessions purge older</c> act run by
    /// the app). <c>0</c> keeps every session forever. <see cref="MinSessionRetentionDays"/> to
    /// <see cref="MaxSessionRetentionDays"/>. No variable.
    /// </summary>
    public int SessionRetentionDays { get; set; } = DefaultSessionRetentionDays;

    public const int DefaultSessionRetentionDays = 0;

    public const int MinSessionRetentionDays = 0;

    public const int MaxSessionRetentionDays = 3650;

    /// <summary>
    /// How many sessions a <c>session_manager</c> search or list without <c>max_results</c> returns:
    /// <see cref="MinSessionSearchMaxResults"/> to <see cref="MaxSessionSearchMaxResults"/>; the
    /// argument overrides it up to the same cap, and a hand-edited value is clamped. No variable.
    /// </summary>
    public int SessionSearchMaxResults { get; set; } = DefaultSessionSearchMaxResults;

    public const int MinSessionSearchMaxResults = 1;

    public const int MaxSessionSearchMaxResults = 20;

    public const int DefaultSessionSearchMaxResults = 10;

    /// <summary>
    /// Which session names the rule above the input row shows at its right edge (2026-09-18): one
    /// of <see cref="Sessions.SessionShowName.Names"/> — <c>all-names</c> (the first line, then the
    /// model's slug in its place), <c>model-written</c> (a model-written or typed name alone, never
    /// the first line) or <c>none</c> (the rule stays bare). Anything else reads as
    /// <see cref="Sessions.SessionShowName.Default"/> with a warning. Read at each draw; no reconnect. No variable.
    /// </summary>
    public string SessionShowName { get; set; } = Sessions.SessionShowName.Default;

    /// <summary>
    /// Whether <c>session_manager</c> is offered to the model (the <c>Ask user</c> shape: a per-group
    /// offer, no clear): search, list and read earlier sessions of this profile. Read at each turn,
    /// no reconnect. No variable.
    /// </summary>
    public bool SessionTool { get; set; } = true;

    /// <summary>
    /// Whether a reply's thinking is saved with the session (2026-09-28, the user's ask, with thinking sent back): a
    /// <c>reasoning</c> part beside the reply's text (<see cref="Sessions.SessionHistory"/>), so a resumed session with
    /// <see cref="LlmPreserveThinking"/> sends it back as the live one did. Off, the thinking stays in memory for the
    /// conversation and <c>sessions.db</c> stays as small as before; a part saved earlier is read back either way. Read at
    /// each save, no reconnect. Off by default. No variable.
    /// </summary>
    public bool SessionSaveThinking { get; set; }

    // ─── LLM ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bearer token. Keyless local servers are happy with the literal <c>empty</c>. Kept encrypted with DPAPI for this
    /// Windows user on this machine since 2026-09-28 (<c>dpapi:</c> and the blob, as <see cref="ClaudeApiKey"/>; the user's
    /// call), a plain one encrypted when the profile loads (<see cref="SettingsSecrets.ProtectAtRest"/>); the <c>empty</c>
    /// placeholder stays plain. Read through <see cref="Llm.LlmEndpoint.KeyOf"/>. Never logged (<see cref="SettingsDiff.Secrets"/>).
    /// </summary>
    public string LlmApiKey { get; set; } = "empty";

    /// <summary>
    /// The share of the context window (percent) at which the next message compacts first, once
    /// the last reply's context reached it; 0 turns the automatic compact off. Needs a known window
    /// (<see cref="LlmContextLength"/> or the server's figure). No variable.
    /// </summary>
    public int LlmAutoCompactPercent { get; set; } = 85;

    /// <summary>
    /// How many user turns the model sees before the oldest drop off (2026-09-27, the user's call; a fixed 24
    /// before): 0 is <c>auto</c> — no cap while <see cref="LlmAutoCompactPercent"/> can act (above 0, a known
    /// window), else <see cref="Llm.ConversationHistory.DefaultMaxTurns"/> — and 1 to
    /// <see cref="App.SettingsMenu.LlmMaxTurnsLimit"/> a fixed cap. Resolved before every message
    /// (<see cref="App.ChatScreen.TurnCapFor"/>); the LLM tab, under <see cref="LlmAutoCompactPercent"/>. No variable.
    /// </summary>
    public int LlmMaxTurns { get; set; }

    /// <summary>How many of the most recent user turns a compact keeps word for word (0 to <see cref="Llm.ConversationHistory.DefaultMaxTurns"/>). No variable.</summary>
    public int LlmCompactKeepRecent { get; set; } = 2;

    /// <summary>
    /// After a compact the transcript shows what it did (2026-09-21, the user's call): in summary mode
    /// the summary's lines dim under the compact notice, in prune mode one line per pruned result
    /// (the tool's name and the size), and last (later that day) how many messages were protected at
    /// the start (the opening call pairs) and at the end (the recent turns kept). Off = the one notice line, as before. The LLM tab's row right
    /// under <c>LLM compact keep recent</c>; read at each compact, no reconnect. No variable.
    /// </summary>
    public bool LlmCompactShowSummary { get; set; }

    /// <summary>
    /// What <c>/compact</c> does: <c>summary</c> (the older turns become one summary the model
    /// writes) or <c>prune</c> (their bulky tool results become stubs). One of
    /// <see cref="Llm.CompactType.Names"/>; anything else reads as <see cref="Llm.CompactType.Default"/>. No variable.
    /// </summary>
    public string LlmCompactType { get; set; } = Llm.CompactType.Default;

    /// <summary>
    /// The loaded model's context window in tokens, for the <c>/usage</c> percentage; 0 means the
    /// server's own figure (<see cref="Llm.ContextLengthProbe"/>). Set it for a server that publishes
    /// none (llama.cpp behind a proxy, an Ollama whose ceiling is not its runtime window). Variable
    /// <see cref="EnvironmentOverrides.LlmContextVariable"/>.
    /// </summary>
    public int LlmContextLength { get; set; }

    /// <summary>
    /// What the busy row's token tally shows while a turn runs (2026-09-25, the user's ask: the tally used to vanish under
    /// the spinner): one of <see cref="App.MidTurnUsageMode.Names"/> — <c>estimate</c> (the context and tok/s
    /// tick with the stream, marked <c>~</c>, the streamed chunks counted as tokens until the server's report lands) or
    /// <c>last-known</c> (the default since later on 2026-09-25, the user's call: the server's figures as of the last
    /// completed request). Read on every draw. The LLM tab of
    /// <c>/settings</c>, under <see cref="LlmContextLength"/>. No variable.
    /// </summary>
    public string LlmMidTurnUsage { get; set; } = App.MidTurnUsageMode.Default;

    /// <summary>
    /// Model round trips one message may spend on tool calls before the turn stops with a notice
    /// (<see cref="Llm.Assistant.MaxToolIterations"/>): a tool call is one, a picture fetched by
    /// <c>view_image</c> one more. <see cref="MinToolIterations"/> to <see cref="MaxToolIterationsCap"/>;
    /// the turn budget stays the guard against a runaway. No variable.
    /// </summary>
    public int LlmMaxToolIterations { get; set; } = Llm.Assistant.DefaultMaxToolIterations;

    /// <summary>The least and the most <see cref="LlmMaxToolIterations"/> may be set to.</summary>
    public const int MinToolIterations = 1;
    public const int MaxToolIterationsCap = 10000;

    /// <summary>
    /// Model id sent with every request. Empty means "the first chat model the server lists".
    /// vLLM and SGLang require an exact match with <c>--served-model-name</c>.
    /// </summary>
    public string LlmModel { get; set; } = "";

    /// <summary>
    /// Whether a turn offers the model its tools at all. Off, the request carries no <c>tools</c>,
    /// no opening call/result pairs are seeded and the default rules lose their tool sentences —
    /// nothing takes their place: the model then has no clock and no path, on purpose. For a model
    /// whose chat template renders no <c>tool</c> role (a Mistral-family template answers
    /// <c>Only user, system and assistant roles are supported!</c> with HTTP 400). A change clears the
    /// conversation. No variable.
    /// </summary>
    public bool LlmOfferTools { get; set; } = true;

    /// <summary>
    /// How hard the model thinks before it answers: one of <c>ReasoningLevel.Levels</c>
    /// (<c>none</c>, <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>), sent as
    /// <c>reasoning_effort</c>; <c>none</c> also switches Qwen-style templates' thinking off.
    /// Anything else resolves to <c>none</c> with a warning.
    /// </summary>
    public string LlmReasoning { get; set; } = "none";

    /// <summary>
    /// Ceiling on one HTTP request, seconds, up to <see cref="Llm.LlmTimeouts.MaxRequestSeconds"/>
    /// (an hour, the default too since 2026-09-15 — <see cref="Llm.LlmTimeouts.DefaultRequest"/>).
    /// Interlocked with <see cref="LlmTurnTimeoutSeconds"/>: the request ceiling must be lower than the
    /// turn ceiling or the turn check is unreachable.
    /// </summary>
    public double LlmRequestTimeoutSeconds { get; set; } = 3600;

    /// <summary>
    /// Where discovery looks when <see cref="LlmUrl"/> is blank: one of <see cref="Llm.LlmScanMode.Names"/>
    /// (<c>local</c> = the usual ports on 127.0.0.1, <c>remote</c> = the same ports on every other
    /// machine of the local network, <c>both</c>, <c>disabled</c> = no scan at all: a blank URL
    /// connects nothing and a bare <c>/server</c> refuses, 2026-09-15). <c>disabled</c> by default since 2026-09-29 (the
    /// user's call; <c>local</c> before): a fresh profile asks no port until the user picks a server or a mode. Read at
    /// each scan (<c>/server</c>, a blank-URL connect), never a reconnect. No variable.
    /// </summary>
    public string LlmScanMode { get; set; } = Llm.LlmScanMode.Default;

    /// <summary>
    /// What the tool loop does when a request's usage reaches <see cref="LlmAutoCompactPercent"/> of the
    /// window mid-turn: <c>compact</c> (a prune, then a summary when that was not enough — the default
    /// since 2026-09-28), <c>prune</c> (this turn's older tool results become stubs), <c>stop</c> (the
    /// turn ends with a notice) or <c>nothing</c>. One of <see cref="Llm.ToolCompactType.Names"/>;
    /// anything else reads as <see cref="Llm.ToolCompactType.Default"/>. No variable.
    /// </summary>
    public string LlmToolCompactType { get; set; } = Llm.ToolCompactType.Default;

    /// <summary>
    /// Whole-turn budget in seconds, checked at tool-loop boundaries, never a CTS; up to
    /// <see cref="Llm.LlmTimeouts.MaxTurnSeconds"/>. Six hours by default (<see cref="Llm.LlmTimeouts.DefaultTurn"/>).
    /// </summary>
    public double LlmTurnTimeoutSeconds { get; set; } = 21600;

    /// <summary>
    /// Base URL of the OpenAI-compatible server (LM Studio, vLLM, SGLang, llama.cpp, Ollama).
    /// Normalised to end in <c>/v1</c> when used. Empty means "probe the usual local ports".
    /// </summary>
    public string LlmUrl { get; set; } = "";

    /// <summary>
    /// Whether the thinking spinner reads a random verb from <see cref="App.ThinkingVerbs.All"/>
    /// (<c>bafflering</c>, <c>zonkerating</c>, …) picked each time it starts, instead of
    /// <see cref="App.ChatScreen.ThinkingLabel"/>. No variable.
    /// </summary>
    public bool LlmUseFunVerbs { get; set; }

    /// <summary>
    /// Whether the model's thinking streams into the transcript (2026-09-26, the user's ask): the
    /// server's reasoning deltas and a <c>&lt;think&gt;</c> block streamed as content drawn live as a
    /// dim block on the code-block panel, folded to one <c>▸ 💭 thought for 4.2s</c> line when the
    /// answer starts (a click, Ctrl+O or <c>/expand</c> unfolds it); on the styled path only, as <see cref="CodeCollapseCount"/>. Display only: the thinking is
    /// never spoken, never in the session log, in <c>/copy</c> only with <c>--thinking</c> (shown or
    /// not), and the request is the same either
    /// way. Off, nothing of it is shown. On by default. No variable.
    /// </summary>
    public bool LlmShowThinking { get; set; } = true;

    /// <summary>
    /// Whether every turn's thinking goes back to a local server, not only the turn in flight's (2026-09-28, the user's
    /// question: "what about preserve thinking?"). The turn in flight's always does, as <c>reasoning_content</c> on the
    /// model's own messages (a Qwen3 template renders it back between tool steps; DeepSeek and Kimi expect it). On, the
    /// earlier turns' goes too and the request asks the chat template to keep it: <c>chat_template_kwargs</c>
    /// <c>preserve_thinking=true</c> (Qwen3.6) and <c>clear_thinking=false</c> (GLM). A template that knows neither drops
    /// or renders it by its own rule. Costs context: the prune of <c>/compact</c> and the automatic compact drop the older
    /// turns' thinking first. The Claude API is not affected (it gets its signed thinking back inside the turn in flight
    /// alone). Read at each turn, no reconnect. Off by default. No variable.
    /// </summary>
    public bool LlmPreserveThinking { get; set; }

    /// <summary>
    /// How <c>/usage</c> counts the thinking a server streamed but did not count (2026-09-29, the user's ask: llama.cpp, the
    /// embedded LLM's server and Ollama report no reasoning count, so the Reasoning row read <c>—</c>): one of
    /// <see cref="Llm.ReasoningEstimates.Names"/> — <c>chars</c> (the default: the thinking's characters over four),
    /// <c>tokenize</c> (llama.cpp's <c>/tokenize</c> counts it exactly, one short request after each reply; the characters
    /// where it does not answer) or <c>off</c>. An estimate shows as <c>~</c>; a server's own count always wins. The Claude
    /// API is not affected. Read at each request, no reconnect. The LLM tab, under <see cref="LlmPreserveThinking"/>. No variable.
    /// </summary>
    public string LlmReasoningEstimate { get; set; } = Llm.ReasoningEstimates.Default;

    /// <summary>
    /// Sampling overrides per model (2026-09-28, the user's ask: temperature, top_p, top_k, min_p, presence_penalty and
    /// repetition_penalty over the model's defaults, "dynamic enough to handle different LLM servers, models"; per model
    /// inside the profile and a free-form extra body, the user's calls). Keyed by the model id as the server reports it
    /// (compared ignoring case), or <see cref="Llm.LlmSampling.AnyModel"/> (<c>*</c>) for every model without a value
    /// of its own: a field resolves from the model's entry, then <c>*</c>, else it is not sent and the server's own
    /// default stands (<see cref="Llm.LlmSampling.Resolve"/>). So a <c>/model</c> switch picks up the other model's
    /// values by itself. temperature, top_p and the two OpenAI penalties go out as the standard fields; top_k, min_p,
    /// the repetition penalty (as <c>repetition_penalty</c> for vLLM and SGLang and <c>repeat_penalty</c> for llama.cpp
    /// and LM Studio, both every time) and the extra body as extra top-level fields a server that does not know them
    /// ignores (Ollama's <c>/v1</c> takes none of them). The Claude API is not affected. Null or empty (the default)
    /// is the server's defaults everywhere. Edited on <c>/sampling</c>; <c>NEONSIDEKICK_LLM_SAMPLING</c> overlays every
    /// entry. Read at each turn, no reconnect.
    /// </summary>
    public Dictionary<string, LlmSamplingEntry>? LlmSampling { get; set; }

    /// <summary>
    /// Whether the <c>/sampling</c> pane may read a model's sampling defaults from its Hugging Face model card
    /// (2026-09-28, the user's call, off by default): when the server itself says nothing (vLLM, SGLang and LM Studio
    /// expose none; llama.cpp's <c>/props</c> and Ollama's <c>/api/show</c> do) and the model id reads as a repo
    /// (<c>Qwen/Qwen3-8B</c>), <c>generation_config.json</c> is fetched from huggingface.co — the file vLLM and SGLang
    /// take their defaults from unless started otherwise — and its values shown as <c>(Hugging Face)</c>. One request per
    /// model and connect, never with the LLM's key; display only. Read when the pane opens. No variable.
    /// </summary>
    public bool LlmSamplingFromHuggingFace { get; set; }

    // ─── TTS ────────────────────────────────────────────────────────────────────

    /// <summary>Base URL of the Kokoro-FastAPI server (OpenAI-compatible <c>/v1/audio/speech</c>).</summary>
    public string TtsHttpUrl { get; set; } = "http://localhost:8880/v1";

    /// <summary>
    /// Whether replies are spoken. Independent of <see cref="SttInput"/>: all four
    /// combinations of the two are valid. Off by default (since 2026-09-12): a fresh install
    /// probes nothing on port 8880 until <c>/tts</c>. When the TTS server is unreachable the app
    /// degrades to text and says so; it never refuses to start.
    /// </summary>
    public bool TtsOutput { get; set; }

    /// <summary>
    /// Where replies are synthesised (2026-09-16): <c>in-process</c> (KokoroSharp over ONNX Runtime
    /// in this process, <c>kokoro.onnx</c> downloaded on first use; the default) or <c>http</c> (a
    /// Kokoro-FastAPI server at <see cref="TtsHttpUrl"/>). One of <c>TtsSource.Names</c>;
    /// a flip reconnects speech. <see cref="TtsHttpUrl"/> keeps its value and is not read while this is
    /// <c>in-process</c>. No variable.
    /// </summary>
    public string TtsSource { get; set; } = Speech.TtsSource.Default;

    /// <summary>Kokoro speed multiplier, sent as <c>speed</c> with every synthesis request. 1.2 by default (2026-09-18), a shade brisker than the voice's natural 1.0.</summary>
    public double TtsSpeed { get; set; } = 1.2;

    /// <summary>The accepted <see cref="TtsSpeed"/> range; outside it a saved or environment value is refused, never clamped.</summary>
    public const double MinTtsSpeed = 0.5;

    public const double MaxTtsSpeed = 2.0;

    /// <summary>Kokoro voice name. First letter is accent (a=American, b=British), second is f/m.</summary>
    public string TtsVoice { get; set; } = "af_heart";

    /// <summary>
    /// A second Kokoro voice blended into <see cref="TtsVoice"/> (Kokoro-FastAPI's
    /// <c>voice(w)+voice(w)</c> form, built by <c>VoiceMix.Spec</c>). Empty means no mix: the
    /// primary voice alone. <c>am_eric</c> by default since 2026-09-16 (the user's call; empty before).
    /// </summary>
    public string TtsVoice2 { get; set; } = DefaultTtsVoice2;

    /// <summary>The fresh profile's second voice (2026-09-16, the user's call); blank was the default before.</summary>
    public const string DefaultTtsVoice2 = "am_eric";

    /// <summary>
    /// The primary voice's share of the mix in percent, 0–100; the secondary voice gets the rest.
    /// Only matters when <see cref="TtsVoice2"/> is set; 100 sends the primary alone, 0 the secondary alone.
    /// 80 by default since 2026-09-16 (the user's call; 50 before).
    /// </summary>
    public int TtsVoiceMix { get; set; } = DefaultTtsVoiceMix;

    /// <summary>The fresh profile's mix (2026-09-16, the user's call); 50 before.</summary>
    public const int DefaultTtsVoiceMix = 80;

    /// <summary>The accepted <see cref="TtsVoiceMix"/> range; outside it a saved or environment value is refused, never clamped.</summary>
    public const int MinTtsVoiceMix = 0;

    public const int MaxTtsVoiceMix = 100;

    /// <summary>Whether the <c>TTS voice</c> / <c>TTS voice 2</c> pickers speak a test phrase in the highlighted voice (speech output on, the pane). No variable.</summary>
    public bool TtsVoicePreview { get; set; } = true;

    // ─── STT ────────────────────────────────────────────────────────────────────

    /// <summary>Whether the microphone path (push-to-talk, and the wake word if enabled) is on.</summary>
    public bool SttInput { get; set; }

    /// <summary>
    /// Whether saying the wake phrase while a reply is being spoken interrupts it: playback
    /// stops and the app listens for the request. Needs voice input and speech output.
    /// </summary>
    public bool SttInterrupt { get; set; }

    /// <summary>
    /// How long, in milliseconds, the wake phrase must persist in the interrupt recogniser's
    /// interim results before a hit counts (<c>WakeListener</c>'s confirm window). The window
    /// rides out the decoder briefly labelling a fragment of the assistant's own speech as the
    /// phrase; the echo probe now catches those where they happen, so the default stays short —
    /// 200 ms (150 until 2026-09-17, the user's call), about what the decoder holds a real
    /// "velora" for under the assistant's voice. 0 fires on the first interim result; raise it
    /// if the assistant interrupts itself.
    /// </summary>
    public int SttInterruptConfirmMs { get; set; } = DefaultSttInterruptConfirmMs;

    /// <summary>The accepted <see cref="SttInterruptConfirmMs"/> range; outside it a saved or environment value is refused, never clamped.</summary>
    public const int MinSttInterruptConfirmMs = 0;

    public const int MaxSttInterruptConfirmMs = 2000;

    public const int DefaultSttInterruptConfirmMs = 200;

    /// <summary>
    /// The interrupt's text echo guard: how similar, in percent, a stretch of the assistant's
    /// own just-played text must be to the wake phrase for a hit to be ignored as an echo
    /// (<c>WakeWordMatch.SoundsLike</c>). 100, the default, ignores only the exact phrase: the
    /// echo probe listens to the assistant's own audio with the real recogniser and needs no
    /// spelling proxy, and at 65 the field log showed every real "velora" thrown away for a
    /// "velop" (as in "develop") somewhere in the sentence. Lower it only on a setup without the probe.
    /// </summary>
    public int SttInterruptEchoGuard { get; set; } = DefaultSttInterruptEchoGuard;

    /// <summary>The accepted <see cref="SttInterruptEchoGuard"/> range; outside it a saved or environment value is refused, never clamped.</summary>
    public const int MinSttInterruptEchoGuard = 50;

    public const int MaxSttInterruptEchoGuard = 100;

    public const int DefaultSttInterruptEchoGuard = 100;

    /// <summary>Name of the <see cref="ConsoleKey"/> that starts a push-to-talk capture.</summary>
    public string SttPushToTalkKey { get; set; } = "F4";

    /// <summary>
    /// The Vosk model the wake word and the interrupt listen with (2026-09-16): one of
    /// <c>ModelStore.VoskModelNames</c> — <c>vosk-model-small-en-us-0.15</c> (the default),
    /// <c>vosk-model-en-us-0.22-lgraph</c> or <c>vosk-model-small-en-in-0.4</c> — downloaded once
    /// into <c>&lt;settings dir&gt;\models\&lt;name&gt;</c>. A picker, never a path; a hand-edited
    /// value outside the list leaves the wake word unavailable. No variable.
    /// </summary>
    public string SttVoskModel { get; set; } = "vosk-model-small-en-us-0.15";

    /// <summary>Whether the always-on wake-word listener runs while voice input is enabled.</summary>
    public bool SttWake { get; set; }

    /// <summary>
    /// The phrase that starts a turn. Matching is open-vocabulary substring against the
    /// recogniser's transcript, not a decoder grammar, so changing it needs no restart. Two
    /// words by default (since 2026-09-12): the interrupt's keyword grammar then needs both in
    /// order, and the assistant saying its own name no longer counts as an echo.
    /// </summary>
    public string SttWakePhrase { get; set; } = "hey neon";

    /// <summary>
    /// The Whisper model for voice input: <c>ggml-tiny.en.bin</c>, <c>ggml-base.en.bin</c> or
    /// <c>ggml-small.en.bin</c> (the file names, downloaded once into <c>&lt;settings dir&gt;\models</c>;
    /// the short <c>base.en</c> form was retired 2026-09-16 and is refused), or an absolute path to
    /// a ggml <c>.bin</c> file that is used as-is.
    /// </summary>
    public string SttWhisperModel { get; set; } = "ggml-base.en.bin";

    // ─── Botchat ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whose LLM the <c>/botchat</c> bots talk through (2026-09-25, the user's ask): one of <see cref="App.BotChatLlmMode.Names"/> —
    /// <c>single</c> (the default, what the chat always did: every bot on this profile's server, model and reasoning effort) or
    /// <c>multi</c> (each bot on its own profile's <see cref="LlmUrl"/>, <see cref="LlmModel"/>, <see cref="LlmApiKey"/>,
    /// timeouts and <see cref="LlmReasoning"/>; a blank URL borrows this profile's server, and a bot whose server does not
    /// answer sits the chat out — the user's calls). Read when a chat starts (or resumes). The Botchat tab of <c>/settings</c>. No variable.
    /// </summary>
    public string BotChatLlmMode { get; set; } = App.BotChatLlmMode.Default;

    /// <summary>
    /// What a <c>/botchat</c> bot naming another embedded model than the running one gets, under <see cref="BotChatLlmMode"/>
    /// <c>multi</c> (later on 2026-09-29, the user's ask and name): one of <see cref="App.BotChatMultiEmbedded.Names"/> —
    /// <c>parent-server</c> (the default: the one embedded server, the running model — this profile's, or with none running
    /// the first embedded bot's — with a warning; the bot sat the chat out until then) or <c>multi-server</c> (an extra
    /// <c>llama-server</c> per other model, under that bot's profile's Embedded settings). Read when a chat starts. No variable.
    /// </summary>
    public string BotChatMultiEmbedded { get; set; } = App.BotChatMultiEmbedded.Default;

    /// <summary>
    /// Whether a multi-server botchat's extra embedded servers stop when the chat ends, however it ends (later on 2026-09-29,
    /// the user's ask and name; on by default); off, they keep running — a later botchat naming the same models reuses them —
    /// until <c>/botchat --kill</c> or the app's exit. Read when the chat ends. No variable.
    /// </summary>
    public bool BotChatMultiEmbeddedKill { get; set; } = true;

    /// <summary>
    /// Whether <c>/botchat</c> has pictures (2026-09-25, the user's ask): while on and the ComfyUI image tools are offered
    /// (<c>ChatScreen.ComfyOffered</c>), <see cref="BotChatImageMode"/> says who draws — the app a picture of every reply,
    /// the bots with <c>generate_image</c>, or both. Off (the default), the chat is talk alone, no tool at all. Read per
    /// reply, no reconnect. The Botchat tab of <c>/settings</c>. No variable.
    /// </summary>
    public bool BotChatImages { get; set; }

    /// <summary>
    /// Who draws a <c>/botchat</c> picture (2026-09-25, the user's three words): one of <see cref="App.BotChatImageMode.Names"/> —
    /// <c>automatic</c> (the default: after every reply the model writes an image prompt from it, in the workflow's family
    /// style, and <see cref="BotChatTxt2ImgWorkflow"/> draws it — or <see cref="BotChatImg2ImgWorkflow"/> reworks an earlier
    /// picture, the prompt writer's choice; the bots are offered no tool), <c>autonomous</c> (the bots are
    /// offered <c>generate_image</c> over those same two workflows alone and draw when they choose; the app draws only a picture a bot talked about but did not
    /// draw — a call that failed counts as not drawn, and a call written out as text runs as a real one — later on 2026-09-25,
    /// so the reply is true). Only while
    /// <see cref="BotChatImages"/> is on. The Botchat tab of <c>/settings</c>. No variable.
    /// </summary>
    public string BotChatImageMode { get; set; } = App.BotChatImageMode.Default;

    /// <summary>
    /// The text → image workflow of <c>/botchat</c>'s fresh pictures (2026-09-25 as <c>Botchat image workflow</c>, which blank
    /// meant the first of them; renamed 2026-09-27, the user's call, beside <see cref="BotChatImg2ImgWorkflow"/>): a name among
    /// the installed workflows that take a prompt and no input picture — offered or not: <c>ComfyUI workflows offered</c> is the
    /// main chat's list alone (later on 2026-09-27, the user's call). Both modes use it alone — the app's picture of a reply
    /// and, since the same day, the bots' own <c>generate_image</c>. Blank (the default), or a name no longer offered of that
    /// kind, or no longer installed, is none: no fresh picture at all. The old key is not carried over (no migration, as ever): a profile that named one
    /// names none until it is picked again. The Botchat tab of <c>/settings</c>. No variable.
    /// </summary>
    public string? BotChatTxt2ImgWorkflow { get; set; }

    /// <summary>
    /// The image → image workflow <c>/botchat</c> may rework an earlier picture with (2026-09-27, the user's ask): a name among
    /// the installed workflows that take a prompt and exactly one input picture, offered or not (later that day). Once the chat has a picture, the prompt writer
    /// (<c>automatic</c>) or the bot (<c>autonomous</c>) chooses between a fresh picture and a rework of one that
    /// <see cref="BotChatImg2ImgMode"/> allows. Blank (the default) is none: no rework. The Botchat tab of <c>/settings</c>. No variable.
    /// </summary>
    public string? BotChatImg2ImgWorkflow { get; set; }

    /// <summary>
    /// Which pictures a <c>/botchat</c> rework may start from (2026-09-27, the user's words): one of
    /// <see cref="App.BotChatImg2ImgMode.Names"/> — <c>latest</c> (the default: the chat's latest picture alone) or
    /// <c>chat-history</c> (any of its pictures so far, the last few, numbered). A picture still rendering is none of them yet.
    /// Only while <see cref="BotChatImg2ImgWorkflow"/> names one. The Botchat tab of <c>/settings</c>. No variable.
    /// </summary>
    public string BotChatImg2ImgMode { get; set; } = App.BotChatImg2ImgMode.Default;

    /// <summary>
    /// Whether the chat waits for the app's <c>/botchat</c> picture (2026-09-25, the user's call, on by default): on, the
    /// picture renders while the next bot answers and is drawn — labelled with whose it is — at the first moment nothing
    /// streams: before a turn, or while a voice plays. Off (later on 2026-09-25, the user's ask: the picture before the
    /// words), the reply is written unseen, its picture made, and then the name, the picture and the reply are shown — the
    /// reply spoken — ESC under the picture skipping it alone. The image prompt is written before the next turn either way,
    /// so the LLM server is never asked two things at once. The Botchat tab of <c>/settings</c>. No variable.
    /// </summary>
    public bool BotChatImageAsync { get; set; } = true;

    /// <summary>
    /// The seconds <c>/botchat</c> rests after a reply when no voice plays (2026-09-26, the user's ask: with TTS off the
    /// replies came back-to-back, too fast to read): the next bot answers after this pause, as it would after a voice.
    /// Lines typed meanwhile join the chat at once, and ESC there ends the chat. <see cref="MinBotChatNonTtsDelaySeconds"/>
    /// (0, off) to <see cref="MaxBotChatNonTtsDelaySeconds"/>; a hand-edited value outside is clamped at use. Read per
    /// reply. The Botchat tab of <c>/settings</c>. No variable.
    /// </summary>
    public int BotChatNonTtsDelaySeconds { get; set; } = DefaultBotChatNonTtsDelaySeconds;

    /// <summary>The accepted <see cref="BotChatNonTtsDelaySeconds"/> range; 0 is no pause.</summary>
    public const int MinBotChatNonTtsDelaySeconds = 0;

    public const int MaxBotChatNonTtsDelaySeconds = 30;

    public const int DefaultBotChatNonTtsDelaySeconds = 5;

    /// <summary>
    /// Whether the <c>/botchat</c> bots get skills (2026-09-27, the user's ask): on, and with <see cref="AgentSkills"/> on,
    /// every bot's prompt lists the skills the main chat sees — the starting profile's, the global ones and, with
    /// <see cref="ExternalSkills"/> on, the external ones; never a bot's own profile's (the user's call: the parent's
    /// skills stand for the room) — and each bot is offered <c>load_skill</c>, never <c>skill_editor</c> (a bot must not
    /// write the parent's skills). Off (the default), the chat is talk alone as before. Read per reply, no reconnect.
    /// The Botchat tab of <c>/settings</c>, its last row. No variable.
    /// </summary>
    public bool BotChatSkills { get; set; }

    /// <summary>
    /// The skills <c>/botchat</c> loads itself (2026-09-27, the user's report: told in the topic to load a skill for the picture
    /// prompts, the models mostly did not call <c>load_skill</c>): names among the skills a botchat sees (the starting profile's,
    /// the global and, with <see cref="ExternalSkills"/>, the external ones), their instructions put straight into the requests
    /// <see cref="BotChatSkillMode"/> says — no tool call needed. A skill whose name the <c>/botchat</c> topic spells out is
    /// loaded the same way. Needs <see cref="AgentSkills"/>, not <see cref="BotChatSkills"/> (which offers <c>load_skill</c>).
    /// Null or empty (the default) is none; a name no longer installed is kept and skipped. Read per reply. The Botchat tab of
    /// <c>/settings</c>, a checklist. No variable.
    /// </summary>
    public List<string>? BotChatPreloadedSkills { get; set; }

    /// <summary>
    /// Where <c>/botchat</c>'s preloaded skills go (2026-09-27, the user's words and default): one of
    /// <see cref="App.BotChatSkillMode.Names"/> — <c>prompt-writer-and-bots</c> (the default: the picture prompt writer and every
    /// bot's system prompt) or <c>prompt-writer-only</c> (the writer alone; the bots' replies are left as they were). Read per
    /// reply. The Botchat tab of <c>/settings</c>. No variable.
    /// </summary>
    public string BotChatSkillMode { get; set; } = App.BotChatSkillMode.Default;

    /// <summary>
    /// Whether the <c>/botchat</c> bots see the chat's pictures (2026-09-27, the user's ask: they reacted to each other's
    /// words about a picture, never the picture): on, each bot's turn message carries the pictures shown in the chat since
    /// it last spoke — the app's pictures, and the other bots' own <c>generate_image</c> pictures (not the ones it drew
    /// itself, seen as it drew them) — the newest <see cref="App.BotChat.MaxVisionPictures"/>, with a caption saying whose.
    /// Only for models that read images: a text-only server fails the turn (in <c>multi</c> mode every bot's own model
    /// counts). Not kept for <c>/botchat --resume</c> or the stored session. Off (the default), the bots see text alone.
    /// Read per reply. The Botchat tab of <c>/settings</c>, its last row. No variable.
    /// </summary>
    public bool BotChatVision { get; set; }

    // ─── Skills─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the model gets skills (2026-09-16): the Agent Skills folders under
    /// <c>&lt;profile&gt;\skills</c> and <c>&lt;home&gt;\skills</c> listed in the system prompt with
    /// <c>load_skill</c> and <c>skill_editor</c> offered, and the working directory's <c>NEON.md</c>
    /// (else <c>AGENTS.md</c>) read into the prompt. Off = none of it. Read at each turn like
    /// <see cref="Memory"/>, no reconnect; the Options tab of <c>/skills</c>, first row. No variable.
    /// </summary>
    public bool AgentSkills { get; set; } = true;

    /// <summary>
    /// Whether the cross-client folder <c>%USERPROFILE%\.agents\skills</c> is scanned too
    /// (2026-09-16); read only while <see cref="AgentSkills"/> is on. Off by default: another
    /// client's skills were written for its tools. Read only, never written. No variable.
    /// </summary>
    public bool ExternalSkills { get; set; }

    /// <summary>
    /// Whether the working directory's <c>NEON.md</c> (else <c>AGENTS.md</c>) is read into the
    /// system prompt (later on 2026-09-19, the user's ask; always, under <see cref="AgentSkills"/>,
    /// until then). Off = the file is left alone whatever it holds; nothing either way while
    /// <see cref="AgentSkills"/> is off. Read at each turn, no reconnect, no conversation clear.
    /// Not a settings row: the <c>Project file</c> row of <c>/skills</c>' Project tab flips it in
    /// place (Enter or Space, the <c>/tools</c> Offered tab's shape), the one editor. No variable.
    /// </summary>
    public bool ProjectFile { get; set; } = true;

    /// <summary>
    /// Whether a turn that was work — <c>Reflection min tool calls</c> (4) or more of the model's own tool calls, or an error it
    /// recovered from (<c>Skills.SkillLearner.ShouldLearn</c>) — is followed by a background
    /// reflection that writes or improves a skill (2026-09-17, the user's call). No effect while
    /// <see cref="AgentSkills"/> or <see cref="LlmOfferTools"/> is off; <c>/learn</c> runs the reflection
    /// whatever this says. Read at each turn's end, no reconnect; the Reflection tab of <c>/skills</c> (its Options tab until later on 2026-09-19), sixth row, labelled <c>Reflection (auto-learn)</c> (<c>Skills auto learn</c> until 2026-09-17; the key followed later that day). No variable.
    /// </summary>
    public bool ReflectionAutoLearn { get; set; } = true;

    /// <summary>
    /// The cooldown after a reflection wrote a skill (<c>Skills.ReflectionCooldown</c>, 2026-09-19,
    /// the user's call): an automatic reflection is skipped — its tally kept, so the next qualifying
    /// turn past the cooldown fires — while the newest <c>reflections</c> row that wrote a skill is
    /// younger than this many minutes; 0 = no cooldown. A <c>/learn</c> never waits. Read from the
    /// session store, so nothing while <c>Session logging</c> is off. <see cref="MinReflectionCooldownMinutes"/>
    /// to <see cref="MaxReflectionCooldownMinutes"/>, a value outside warned and replaced by the default,
    /// never clamped. Read at each reply's end, no reconnect; the Reflection tab of <c>/skills</c> (its Options tab until later on 2026-09-19), after
    /// <c>Reflection max requests</c>, labelled <c>Reflection cooldown (minutes)</c>. No variable.
    /// </summary>
    public int ReflectionCooldownMinutes { get; set; } = DefaultReflectionCooldownMinutes;

    public const int DefaultReflectionCooldownMinutes = 5;   // 30 for an hour on 2026-09-19, then the user's call

    /// <summary>
    /// What the cooldown holds back (<c>Skills.ReflectionCooldownMode</c>, 2026-09-19, the user's
    /// call): <c>last-written-skill</c> (the default) skips an automatic reflection only when the
    /// turns since the last reflection loaded the skill the newest reflection wrote — another
    /// lesson reflects at once; <c>all-skills</c> holds every automatic reflection back while the
    /// cooldown runs. Read at each reply's end, no reconnect; the Reflection tab of <c>/skills</c> (its Options tab until later on 2026-09-19),
    /// the row after <c>Reflection cooldown (minutes)</c>, labelled <c>Reflection cooldown mode</c>. No variable.
    /// </summary>
    public string ReflectionCooldownMode { get; set; } = Skills.ReflectionCooldownMode.Default;

    public const int MinReflectionCooldownMinutes = 0;

    public const int MaxReflectionCooldownMinutes = 1440;

    /// <summary>
    /// Whether a reflection is handed the earlier sessions (2026-09-19, the user's ask): the
    /// stored sessions matching the turn's words open its transcript's end as a seeded
    /// <c>session_manager</c> search, the catalog carries each skill's usage across the stored
    /// turns, and <c>session_manager</c> is its third tool. Off = the reflection reads the
    /// conversation on screen alone. Nothing while <c>Session logging</c> is off. Read when a
    /// reflection is decided, no reconnect; the Reflection tab of <c>/skills</c> (its Options tab until later on 2026-09-19), last row, labelled
    /// <c>Reflection includes sessions</c>. No variable.
    /// </summary>
    public bool ReflectionIncludesSessions { get; set; } = true;

    /// <summary>
    /// Whether a turn pauses a running reflection (2026-09-24, the user's ask: on a one-slot local
    /// server a message sent while a reflection runs waits behind it). On = the turn's start
    /// cancels the reflection and the same snapshot runs again once the reply — and every message
    /// queued behind it — is done; the cancelled requests' tokens are spent. Off = the reflection
    /// runs on beside the turn, as before (a server with parallel slots loses nothing to it). Read
    /// at each turn's start, no reconnect; the Reflection tab of <c>/skills</c>, last row, labelled
    /// <c>Reflection yields to turns</c>. No variable.
    /// </summary>
    public bool ReflectionYieldsToTurns { get; set; } = true;

    /// <summary>
    /// Whether a reflection's <c>skill_editor</c> may write a skill's supporting files — the
    /// <c>write_file</c> / <c>edit_file</c> actions, beside the SKILL.md (2026-09-27, the user's call:
    /// the main chat always may; a background pass rewriting a skill's data only when asked). On = the
    /// reflection is offered them too, and a file written ends its pass as a SKILL.md write does. Off (the
    /// default for now) = the reflection writes the SKILL.md alone. Read when a reflection is decided, no
    /// reconnect; the Reflection tab of <c>/skills</c>, last row, labelled <c>Reflection edit supporting files</c>. No variable.
    /// </summary>
    public bool ReflectionEditsSupportingFiles { get; set; }

    /// <summary>
    /// How many model requests one reflection may make before it is given up as exhausted
    /// (<c>Skills.ReflectionMaxRequests</c>; a load or two, then the write — the reflection's own
    /// cap, never <see cref="LlmMaxToolIterations"/>, the turn's). <see cref="MinReflectionMaxRequests"/>
    /// to <see cref="MaxReflectionMaxRequests"/>, a value outside warned and replaced by the default,
    /// never clamped. Read when a reflection starts, no reconnect; the Reflection tab of <c>/skills</c> (its Options tab until later on 2026-09-19), tenth row, labelled
    /// <c>Reflection max requests</c> (2026-09-17, the user's call; a constant until then). No variable.
    /// </summary>
    public int ReflectionMaxRequests { get; set; } = DefaultReflectionMaxRequests;

    public const int DefaultReflectionMaxRequests = 4;

    public const int MinReflectionMaxRequests = 1;

    public const int MaxReflectionMaxRequests = 20;

    /// <summary>
    /// How many of the model's own tool calls, added up across the turns since the last reflection,
    /// make a task worth a skill (<c>Skills.ReflectionMinToolCalls</c>; the other door, an error recovered
    /// from, is not a setting — one recovered error is the lesson). <see cref="MinReflectionMinToolCalls"/>
    /// to <see cref="MaxReflectionMinToolCalls"/>, a value outside warned and replaced by the default,
    /// never clamped. Read at each reply's end, no reconnect; the Reflection tab of <c>/skills</c> (its Options tab until later on 2026-09-19), last row (2026-09-17). No variable.
    /// </summary>
    public int ReflectionMinToolCalls { get; set; } = DefaultReflectionMinToolCalls;

    public const int DefaultReflectionMinToolCalls = 4;   // 5 until 2026-09-17 (the user's call)

    public const int MinReflectionMinToolCalls = 3;

    public const int MaxReflectionMinToolCalls = 20;

    /// <summary>
    /// The reasoning level of the skill-learning reflection (<c>Skills.ReflectionReasoning</c>):
    /// <c>profile</c> for <see cref="ReasoningEffort"/>'s level (<c>profile-default</c> until later on
    /// 2026-09-19, no old spelling kept), or one of its five words outright; <c>none</c> by default
    /// since then (the profile's level before — the user's call). Read at each reflection, no reconnect; the Reflection tab of <c>/skills</c> (its Options tab until later on 2026-09-19), second row. No variable.
    /// </summary>
    public string ReflectionReasoning { get; set; } = Skills.ReflectionReasoning.Default;

    /// <summary>
    /// How many of the last turns the skill-learning reflection reads (<c>Skills.ReflectionWindow</c>):
    /// the last in full, the earlier ones as its lead-up with their tool results shortened; the
    /// trigger adds the model's calls up across the turns since the last reflection. One is the
    /// last turn alone. <see cref="MinReflectionWindow"/> to <see cref="MaxReflectionWindow"/>, a
    /// value outside warned and replaced by the default, never clamped. Read at each reflection,
    /// no reconnect; the Reflection tab of <c>/skills</c> (its Options tab until later on 2026-09-19), last row (2026-09-17). No variable.
    /// </summary>
    public int ReflectionWindow { get; set; } = DefaultReflectionWindow;

    public const int DefaultReflectionWindow = 3;

    public const int MinReflectionWindow = 1;

    public const int MaxReflectionWindow = 5;

    /// <summary>
    /// Whether a loaded skill's instructions survive the prune compact and the mid-turn guard
    /// (2026-09-16): <c>protected</c> (the default) or <c>unprotected</c>. One of
    /// <c>SkillCompactMode.Names</c>; anything else reads as the default with a warning. No variable.
    /// </summary>
    public string SkillCompactMode { get; set; } = Skills.SkillCompactMode.Default;

    /// <summary>
    /// Whether <c>#</c> and part of a name on the chat line lists the loaded skills (2026-09-17, the
    /// user's call), as <c>@</c> lists files: a pick writes <c>#name</c> into the draft — text the model
    /// reads, nothing seeded. Off = <c>#</c> is ordinary text. Read at each keystroke, no reconnect;
    /// the Options tab of <c>/skills</c>, fourth row (fifth until later on 2026-09-18, when <c>Skill slash commands</c> went). No variable.
    /// </summary>
    public bool SkillHashMention { get; set; } = true;

    // ─── Tools ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The model's tools switched off one by one on <c>/tools</c>' Offered tab (2026-09-19): tool
    /// names, sorted ordinal, no duplicates; empty = every tool its group offers. A name here drops
    /// the tool from the next turn whatever its group's switch says (an emptied group loses its rule
    /// too); a name no tool carries is inert and kept. Read at each turn, no reconnect, no
    /// conversation clear. Not a settings row — the one list in the file. No variable.
    /// <c>gitlib_delete</c> / <c>gitlib_discard</c> (later on 2026-09-20, the user's call: the two git tools that lose
    /// work), and <c>unzip</c> / <c>zip</c> since 2026-09-21 (the same call again: a bulk extract and a bulk
    /// pack are opt-in too); a saved list stands — a profile that holds <c>[]</c> or <c>["delete"]</c> keeps the
    /// rest on, so a profile from before keeps zip and unzip. <c>delete</c> was here from the start (2026-09-20,
    /// the user's call: opt-in, flipped on <c>/tools</c>' Offered tab) until later on 2026-09-21,
    /// when the user asked for it on out of the box; a profile saved with it off keeps it off. <c>gitlib_discard</c> left
    /// the list on 2026-09-23 (the user's call: on out of the box, as <c>delete</c> went before it); a profile saved
    /// with it off keeps it off until it is flipped on the Offered tab. <c>unc_delete</c> joined on 2026-09-30 (a delete on a share is
    /// permanent, a folder with everything in it — opt-in like <c>gitlib_delete</c>, even under <c>UNC writes</c>).
    /// </summary>
    public List<string> ToolsDisabled { get; set; } = [Llm.Tools.GitDeleteTool.ToolName, Llm.Tools.UnzipTool.ToolName, Llm.Tools.ZipTool.ToolName, Llm.Tools.UncDeleteTool.ToolName];

    /// <summary>
    /// Whether <c>$</c> and part of a name on the chat line lists the tools the next turn offers
    /// (2026-09-19, the user's call), as <c>#</c> lists the skills: the offered names with their
    /// descriptions, a pick writes <c>$read_file</c> into the draft — text the model reads, nothing
    /// seeded. Off = <c>$</c> is ordinary text. Read at each keystroke, no reconnect; the Options tab
    /// of <c>/tools</c>, its one row. No variable.
    /// </summary>
    public bool ToolsDollarMention { get; set; } = true;

    /// <summary>
    /// How many lines of a run of tool calls the transcript keeps while the run goes on (2026-09-22,
    /// the user's ask): past it the run folds under one summary line (<c>▸ 🛠️ 7 tool calls — …</c>)
    /// with only its last lines under it, and once the reply moves on the summary alone — a click on
    /// it, Ctrl+O or <c>/expand</c> shows every line. <see cref="MinToolCollapseCount"/> to
    /// <see cref="MaxToolCollapseCount"/>; 0 = never fold (every line, as before). Read when a run
    /// opens, no reconnect; the Options tab of <c>/tools</c>, under <see cref="ToolsDollarMention"/>.
    /// Only on the screen's pane (headless keeps every line). No variable.
    /// </summary>
    public int ToolCollapseCount { get; set; } = DefaultToolCollapseCount;

    /// <summary>The default, the least and the most <see cref="ToolCollapseCount"/> may be (0 = off).</summary>
    public const int DefaultToolCollapseCount = 2;
    public const int MinToolCollapseCount = 0;
    public const int MaxToolCollapseCount = 100;

    /// <summary>
    /// How many lines a code block in a styled reply may have before it folds (2026-09-22, the
    /// user's ask, the tool runs' fold for code): one with more source lines than this shrinks to
    /// its label line (<c>▸ 📜 csharp · 57 lines</c>) the moment its fence closes — a click on it,
    /// Ctrl+O or <c>/expand</c> shows it again. While it streams, the block shows its label and its
    /// last this many rows only, as the thinking shows its tail (2026-09-27, the user's ask: a long
    /// block scrolled the screen and flickered as it came; it streamed at full height before).
    /// Top-level blocks only (not one inside a list item or a quote). <see cref="MinCodeCollapseCount"/>
    /// to <see cref="MaxCodeCollapseCount"/>; 0 = never fold. Read when a reply opens, no reconnect;
    /// the Options tab of <c>/tools</c>, under <see cref="ToolCollapseCount"/>. Only on the screen's
    /// pane with <c>Transcript markdown</c> on. No variable.
    /// </summary>
    public int CodeCollapseCount { get; set; } = DefaultCodeCollapseCount;

    /// <summary>The default, the least and the most <see cref="CodeCollapseCount"/> may be (0 = off).</summary>
    public const int DefaultCodeCollapseCount = 20;
    public const int MinCodeCollapseCount = 0;
    public const int MaxCodeCollapseCount = 100;

    // ─── Ask ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The most options one <c>ask_user</c> question may offer: <see cref="MinAskMaxChoices"/> to
    /// <see cref="MaxAskMaxChoices"/> (the floor is the tool's own — a question needs two answers to
    /// choose between); quoted, refused over and clamped like <see cref="AskMaxQuestions"/>. No variable.
    /// </summary>
    public int AskMaxChoices { get; set; } = DefaultAskMaxChoices;

    public const int MinAskMaxChoices = 2;
    public const int MaxAskMaxChoices = 15;
    public const int DefaultAskMaxChoices = 10;

    /// <summary>
    /// The most questions one <c>ask_user</c> call may put to the user: <see cref="MinAskMaxQuestions"/>
    /// to <see cref="MaxAskMaxQuestions"/>; the tool's schema and rule quote it, a call over it is
    /// refused with an <c>Error:</c> sentence, and a hand-edited value is clamped. No variable.
    /// </summary>
    public int AskMaxQuestions { get; set; } = DefaultAskMaxQuestions;

    public const int MinAskMaxQuestions = 1;
    public const int MaxAskMaxQuestions = 10;
    public const int DefaultAskMaxQuestions = 10;

    /// <summary>
    /// Whether a turn offers the model <c>ask_user</c> (the questions pane, 2026-09-15); read at each
    /// turn like <see cref="WebTools"/>, no reconnect. The bottom pane stays a gate: without it
    /// nothing can draw the questions, whatever this says. The row is the one switch (<c>/ask</c> went 2026-09-18). No variable.
    /// </summary>
    public bool AskUser { get; set; } = true;

    // ─── Files ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// What applying a folder from the line's @-mention list does (2026-09-16): <c>folder-apply</c>
    /// (<c>@folder/</c> and a space go on the line, the list closes — a folder is a mention like a
    /// file; the default until 2026-09-19) or <c>folder-remain</c> (<c>@folder/</c> goes on the line and the list
    /// stays open on the folder's contents; the default since). One of <see cref="Files.MentionFolderMode.Names"/>;
    /// anything else reads as <see cref="Files.MentionFolderMode.Default"/>. No variable.
    /// </summary>
    public string FileMentionFolderMode { get; set; } = Files.MentionFolderMode.Default;

    /// <summary>
    /// What the <c>/cwd browse</c> folder tree lists (2026-09-21, the user's ask): <c>default</c>
    /// leaves out hidden and system folders and dot-folders, Explorer's and Finder's default;
    /// <c>show-hidden</c> lists them too. <c>/tree</c> follows it since 2026-09-23 (the user's call; the row
    /// <c>File browser/tree mode</c> since): <c>default</c> leaves out hidden and system entries and every dot-file
    /// and dot-folder, <c>show-hidden</c> lists them all. One of <see cref="Files.FileBrowserMode.Names"/>; anything
    /// else reads as <see cref="Files.FileBrowserMode.Default"/>. No variable.
    /// </summary>
    public string FileBrowserMode { get; set; } = Files.FileBrowserMode.Default;

    /// <summary>
    /// Whether a turn offers the model the fourteen file tools over the working directory (fifteen until 2026-10-01, when
    /// <c>restore</c> went with File safe edits, the user's call: every edit and delete is in place, and a saved
    /// <c>FileSafeEdits</c> key is skipped on load); read at each
    /// turn like <see cref="WebTools"/>, no reconnect. Off, the default rules lose their file
    /// sentences and no <c>get_working_directory</c> call opens the conversation (the user's own
    /// <c>/cwd</c>, <c>/tree</c> and <c>/explore</c> keep working). The row is the one
    /// switch (<c>/files</c> went 2026-09-18). Off by default since 2026-09-29 (the user's call, with the other tool
    /// groups and the scan: a fresh profile offers the model nothing it did not turn on). No variable.
    /// </summary>
    public bool FileTools { get; set; }

    /// <summary>
    /// Entries a <c>/tree</c> lists before it stops with a tail line: <see cref="Files.WorkingDirectory.MinTreeLength"/>
    /// to <see cref="Files.WorkingDirectory.MaxTreeLength"/>; the walk clamps a hand-edited value. No variable.
    /// </summary>
    public int FileTreeMaxLength { get; set; } = Files.WorkingDirectory.DefaultTreeLength;

    /// <summary>Whether a <c>/tree</c> line carries the file's size after its name. No variable.</summary>
    public bool FileTreeShowSizes { get; set; } = true;

    /// <summary>
    /// The most pictures one <c>view_image</c> call loads (2026-09-19, the user's ask; a constant 4 until
    /// then): <see cref="MinViewImageMaxPerCall"/> to <see cref="MaxViewImageMaxPerCall"/>; the tool's schema
    /// and description quote it, a call over it loads the first that many and names the rest, and the
    /// tool clamps a hand-edited value. Read at each call, no reconnect. No variable.
    /// </summary>
    public int FileViewImageMaxPerCall { get; set; } = DefaultViewImageMaxPerCall;

    public const int MinViewImageMaxPerCall = 1;
    public const int MaxViewImageMaxPerCall = 100;
    public const int DefaultViewImageMaxPerCall = 10;

    // ─── GitLib ─────────────────────────────────────────────────────────────────
    // Renamed Git native … on 2026-09-21 (the user's call): the in-process LibGit2Sharp tools as
    // against git through the shell. The five keys followed their labels (no migration: the old
    // GitTools/GitDiffMaxLines/GitLogMaxCommits/GitEmail/GitName keys are skipped on load).
    // Renamed GitLib … on 2026-09-30 (the user's ask), the tools with them (git_status → gitlib_status …): the keys
    // followed again, with no migration (the user's pick) — the old GitNative* keys are skipped on load, so the five
    // settings start at their defaults, and a saved git_* name in ToolsDisabled no longer matches a tool.

    /// <summary>
    /// The most patch lines one <c>gitlib_diff</c> shows (2026-09-20; <c>Git native diff max lines</c> from 2026-09-21, <c>GitLib diff max lines</c> since 2026-09-30): <see cref="MinGitLibDiffMaxLines"/> to
    /// <see cref="MaxGitLibDiffMaxLines"/>; the argument <c>max_lines</c> overrides it up to the same cap, a cut
    /// patch says so and names <c>path</c> to narrow it, and the tool clamps a hand-edited value. Read at
    /// each call, no reconnect. No variable.
    /// </summary>
    public int GitLibDiffMaxLines { get; set; } = DefaultGitLibDiffMaxLines;

    public const int MinGitLibDiffMaxLines = 20;
    public const int MaxGitLibDiffMaxLines = 5000;
    public const int DefaultGitLibDiffMaxLines = 500;

    /// <summary>
    /// How many commits a <c>gitlib_log</c> without <c>max_commits</c> lists (2026-09-20; <c>Git native log max commits</c> from 2026-09-21, <c>GitLib log max commits</c> since 2026-09-30):
    /// <see cref="MinGitLibLogMaxCommits"/> to <see cref="MaxGitLibLogMaxCommits"/>; the argument overrides it up to
    /// the same cap, and a hand-edited value is clamped. No variable.
    /// </summary>
    public int GitLibLogMaxCommits { get; set; } = DefaultGitLibLogMaxCommits;

    public const int MinGitLibLogMaxCommits = 1;
    public const int MaxGitLibLogMaxCommits = 200;
    public const int DefaultGitLibLogMaxCommits = 20;

    /// <summary>
    /// Whether a turn offers the model the eleven git tools over the repository at or under the working
    /// directory (2026-09-20; <c>Git native tools</c> from 2026-09-21, <c>GitLib tools</c> since 2026-09-30); read at each turn like <see cref="WebTools"/>, no reconnect.
    /// Off — the default since 2026-09-21 (the user's call, like <see cref="McpServers"/>): the model reaches git
    /// through the shell unless the profile opts in — the default rules lose their git sentence and
    /// <c>/gituser</c> refuses. <c>gitlib_delete</c> is off by name in a fresh profile's <see cref="ToolsDisabled"/>
    /// besides (<c>gitlib_discard</c> was too until 2026-09-23). No variable.
    /// </summary>
    public bool GitLibTools { get; set; }

    /// <summary>
    /// The <c>user.email</c> that <c>/gituser</c> writes into the working directory's repository config
    /// (2026-09-21), with <see cref="GitLibName"/>; empty = not set, and the command refuses — as it does
    /// while <see cref="GitLibTools"/> is off (later that day). Never read by the git tools — a commit signs
    /// with whatever git's own config holds. The GitLib tab of <c>/tools</c>, fourth row. No variable.
    /// </summary>
    public string GitLibEmail { get; set; } = "";

    /// <summary>The <c>user.name</c> <c>/gituser</c> writes beside <see cref="GitLibEmail"/> (2026-09-21); empty = not set. The GitLib tab's last row. No variable.</summary>
    public string GitLibName { get; set; } = "";

    // ─── Obsidian ───────────────────────────────────────────────────────────────
    // The vault tools (2026-09-22, the user's ask: "Obsidian integration — accessing and managing files in
    // an Obsidian vault"): the notes read and written on disk, links resolved the way Obsidian resolves them.

    /// <summary>
    /// Whether a turn offers the eight vault tools (<c>vault_read</c>, <c>vault_search</c>, …) over
    /// <see cref="ObsidianVault"/> (2026-09-22); read at each turn like <see cref="GitLibTools"/>, no reconnect.
    /// On by default until 2026-09-29 (it offered nothing until a vault was set); off since, the user's call with
    /// <see cref="FileTools"/>. No variable.
    /// </summary>
    public bool ObsidianTools { get; set; }

    /// <summary>
    /// The Obsidian vault the vault tools work in, a full path to the folder holding <c>.obsidian</c>
    /// (2026-09-22); empty = none, and the tools are not offered. Apart from <see cref="WorkingDirectory"/>
    /// on purpose: a vault is where the notes live, the working directory where the work is. Per profile;
    /// <c>NEONSIDEKICK_OBSIDIAN_VAULT</c> overrides it for a launch. Read at each call, no reconnect.
    /// </summary>
    public string ObsidianVault { get; set; } = "";

    /// <summary>
    /// Whether a turn offers <c>vault_delete</c> (2026-09-22, the user's ask: "disabled by default"): one note or
    /// attachment moved into the vault's <c>.trash</c>, never destroyed. Off by default until 2026-09-23, on since
    /// (the user's call: nothing is lost — Obsidian restores from its <c>.trash</c>); a profile saved with it off keeps
    /// it off. A switch of its own rather than a name in <see cref="ToolsDisabled"/>'s default on purpose: a saved
    /// profile keeps its own list, so a default there would reach only new profiles. On, the tool still has its own
    /// switch on <c>/tools</c>' Offered tab. The Obsidian tab of <c>/tools</c>, third row; read at each turn and again
    /// at the call, no reconnect. No variable.
    /// </summary>
    public bool ObsidianAllowDelete { get; set; } = true;

    // ─── SQL ────────────────────────────────────────────────────────────────────
    // The SQL tools (2026-09-23, the user's ask: "tools for connecting to and querying MSSQL server", after their
    // mcp-mssql-read server): read-only queries on the named connections of sql.json (the profile's and the home's).

    /// <summary>
    /// Whether a turn offers the eight SQL tools (<c>sql_connections</c>, <c>sql_query</c>, …) over the connections
    /// in <c>sql.json</c> (2026-09-23); read at each turn like <see cref="ObsidianTools"/>, no reconnect. On by
    /// default until 2026-09-29 (it offered nothing until a connection was defined); off since, the user's call.
    /// No variable.
    /// </summary>
    public bool SqlTools { get; set; }

    /// <summary>
    /// The connection a SQL tool uses when the call names none (2026-09-23): a name in <c>sql.json</c>; empty, or a
    /// name no longer there (or no longer offered, <see cref="SqlConnectionsOffered"/>), = the first offered connection.
    /// A default, never a limit: a call that names another offered connection gets that one. The SQL tab of
    /// <c>/tools</c>. No variable.
    /// </summary>
    public string SqlDefaultConnection { get; set; } = "";

    /// <summary>
    /// Which connections of <c>sql.json</c> this profile offers the model (later on 2026-09-23, the user's ask: a way
    /// to limit what it can reach, not only choose its default). Null = not narrowed: every connection, a new one
    /// included — every profile's start, so a setup from before keeps working. A list = exactly those names
    /// (case-insensitive), so a connection added to <c>sql.json</c> afterwards stays hidden until ticked (the user's
    /// call); an empty list offers none; a name no longer in the files is ignored. A hidden connection is out of
    /// every tool, the rules, the <c>%</c>-mention and the default; <c>sql_connections</c> says how many are hidden,
    /// never which. Still encrypted at startup and settable with <c>SQL set password</c>. The SQL tab of <c>/tools</c>,
    /// second row. No variable.
    /// </summary>
    public List<string>? SqlConnectionsOffered { get; set; }

    /// <summary>
    /// Whether <c>%</c> and part of a name on the chat line lists the SQL connections of <c>sql.json</c> (later on
    /// 2026-09-23, the user's ask: "similar to the @-, #- and $-mention … to recall configured SQL database connection
    /// names"), as <c>$</c> lists the tools: each name with its server, database and description, a pick writes
    /// <c>%name</c> into the draft — text the model reads (and passes as <c>connection</c>), nothing seeded. Offered only
    /// while <see cref="SqlTools"/> is on, since the names mean nothing to a turn without the tools. Off = <c>%</c> is
    /// ordinary text. Read at each keystroke, no reconnect; the SQL tab of <c>/tools</c>. No variable.
    /// </summary>
    public bool SqlPercentMention { get; set; } = true;

    /// <summary>
    /// The most rows one <c>sql_query</c> returns (2026-09-23): <see cref="MinSqlQueryMaxRows"/> to
    /// <see cref="MaxSqlQueryMaxRows"/>; the argument <c>max_rows</c> overrides it up to the same cap. Past it the
    /// header says more exist and the server stops (the reader never drains the rest).
    /// </summary>
    public int SqlQueryMaxRows { get; set; } = DefaultSqlQueryMaxRows;

    public const int MinSqlQueryMaxRows = 1;
    public const int MaxSqlQueryMaxRows = 1000;
    public const int DefaultSqlQueryMaxRows = 100;

    /// <summary>
    /// Seconds a SQL tool's batch may run on the server before it is stopped (2026-09-23; the <c>mcp-mssql-read</c>
    /// server had no query timeout at all): <see cref="MinSqlQueryTimeoutSeconds"/> to <see cref="MaxSqlQueryTimeoutSeconds"/>.
    /// </summary>
    public int SqlQueryTimeoutSeconds { get; set; } = DefaultSqlQueryTimeoutSeconds;

    public const int MinSqlQueryTimeoutSeconds = 1;
    public const int MaxSqlQueryTimeoutSeconds = 600;
    public const int DefaultSqlQueryTimeoutSeconds = 30;

    // ─── Oracle ─────────────────────────────────────────────────────────────────
    // The Oracle tools (2026-09-30, the user's ask: "mirror what we did for SQL server, but for Oracle", read-only by
    // every means): read-only queries on the named connections of oracle.json (the profile's and the home's).

    /// <summary>
    /// Whether a turn offers the eight Oracle tools (<c>oracle_connections</c>, <c>oracle_query</c>, …) over the connections
    /// in <c>oracle.json</c> (2026-09-30); read at each turn like <see cref="SqlTools"/>, no reconnect. Off by default, as
    /// <see cref="SqlTools"/> is. No variable.
    /// </summary>
    public bool OracleTools { get; set; }

    /// <summary>
    /// The connection an Oracle tool uses when the call names none (2026-09-30), <see cref="SqlDefaultConnection"/>'s twin: a
    /// name in <c>oracle.json</c>; empty, or a name no longer there (or no longer offered), = the first offered connection.
    /// The Oracle tab of <c>/tools</c>. No variable.
    /// </summary>
    public string OracleDefaultConnection { get; set; } = "";

    /// <summary>
    /// Which connections of <c>oracle.json</c> this profile offers the model (2026-09-30), <see cref="SqlConnectionsOffered"/>'s
    /// twin: null = not narrowed (every connection, a new one included); a list = exactly those names, so one added
    /// afterwards stays hidden until ticked; an empty list offers none. The Oracle tab of <c>/tools</c>. No variable.
    /// </summary>
    public List<string>? OracleConnectionsOffered { get; set; }

    /// <summary>
    /// Whether <c>%</c> and part of a name on the chat line lists the Oracle connections too (2026-09-30), beside the SQL
    /// ones <see cref="SqlPercentMention"/> lists, each marked <c>Oracle ·</c>; a pick writes <c>%name</c> into the draft.
    /// Offered only while <see cref="OracleTools"/> is on. Read at each keystroke, no reconnect; the Oracle tab of
    /// <c>/tools</c>. No variable.
    /// </summary>
    public bool OraclePercentMention { get; set; } = true;

    /// <summary>
    /// The most rows one <c>oracle_query</c> returns (2026-09-30): <see cref="MinSqlQueryMaxRows"/> to
    /// <see cref="MaxSqlQueryMaxRows"/>, the SQL tools' range; the argument <c>max_rows</c> overrides it up to the same cap.
    /// Past it the header says more exist and the cursor is closed (the rest is never fetched).
    /// </summary>
    public int OracleQueryMaxRows { get; set; } = DefaultSqlQueryMaxRows;

    /// <summary>
    /// Seconds an Oracle tool's statement may run on the server before it is stopped (2026-09-30, ORA-01013):
    /// <see cref="MinSqlQueryTimeoutSeconds"/> to <see cref="MaxSqlQueryTimeoutSeconds"/>, the SQL tools' range.
    /// </summary>
    public int OracleQueryTimeoutSeconds { get; set; } = DefaultSqlQueryTimeoutSeconds;

    // ─── MySQL ──────────────────────────────────────────────────────────────────
    // The MySQL tools (2026-09-30, the user's ask: "the same sort of thing for MySQL, very similar to Oracle"): read-only
    // queries on MySQL and MariaDB over the named connections of mysql.json (the profile's and the home's).

    /// <summary>Whether a turn offers the eight MySQL tools over the connections in <c>mysql.json</c> (2026-09-30); read at each turn like <see cref="OracleTools"/>, no reconnect. Off by default. No variable.</summary>
    public bool MySqlTools { get; set; }

    /// <summary>The connection a MySQL tool uses when the call names none (2026-09-30), <see cref="OracleDefaultConnection"/>'s twin; empty = the first offered. No variable.</summary>
    public string MySqlDefaultConnection { get; set; } = "";

    /// <summary>Which connections of <c>mysql.json</c> this profile offers the model (2026-09-30), <see cref="OracleConnectionsOffered"/>'s twin: null = all, a list = exactly those names. No variable.</summary>
    public List<string>? MySqlConnectionsOffered { get; set; }

    /// <summary>Whether <c>%</c> and part of a name lists the MySQL connections too (2026-09-30), each marked <c>MySQL ·</c>; offered only while <see cref="MySqlTools"/> is on. No variable.</summary>
    public bool MySqlPercentMention { get; set; } = true;

    /// <summary>The most rows one <c>mysql_query</c> returns (2026-09-30): the SQL tools' range; <c>max_rows</c> overrides it up to the same cap.</summary>
    public int MySqlQueryMaxRows { get; set; } = DefaultSqlQueryMaxRows;

    /// <summary>Seconds a MySQL tool's statement may run, on the server (<c>max_execution_time</c> / <c>max_statement_time</c>) and in the driver (2026-09-30): the SQL tools' range.</summary>
    public int MySqlQueryTimeoutSeconds { get; set; } = DefaultSqlQueryTimeoutSeconds;

    // ─── UNC ────────────────────────────────────────────────────────────────────
    // The UNC tools (2026-09-30, the user's ask: SQL's integrated auth and run-as for UNC paths, "gated access to file systems on
    // UNC paths outside the working directory for searching files, researching files"): the named shares of unc.json (the
    // profile's and the home's), each a \\server\share path or an outside folder, reached as the user or as another account.

    /// <summary>
    /// Whether a turn offers the UNC tools over the shares in <c>unc.json</c> (2026-09-30): <c>unc_shares</c>, <c>unc_search</c>,
    /// <c>unc_info</c>, <c>unc_read</c> and — with the File tools on — <c>unc_fetch</c>; the changing tools only under
    /// <see cref="UncWrites"/>. Read at each turn. Off by default. No variable.
    /// </summary>
    public bool UncTools { get; set; }

    /// <summary>
    /// The master key of every change on a share (2026-09-30, the user's call: "force it to be read-only by default with an option
    /// for read-write"): off (the default), every share is read-only whatever its <c>access</c>; on, a share whose <c>access</c> is
    /// <c>readwrite</c> gets <c>unc_write</c>, <c>unc_patch</c>, <c>unc_create_directory</c>, <c>unc_move</c>, <c>unc_copy</c>,
    /// <c>unc_delete</c> and <c>unc_put</c>. Changes and deletes there are permanent — nothing is kept (the user's call). Both keys
    /// are checked again at every call. Read at each turn. No variable.
    /// </summary>
    public bool UncWrites { get; set; }

    /// <summary>The share a UNC tool uses when the call names none and gives no full path (2026-09-30), <see cref="SqlDefaultConnection"/>'s twin; empty = the first offered. No variable.</summary>
    public string UncDefaultShare { get; set; } = "";

    /// <summary>Which shares of <c>unc.json</c> this profile offers the model (2026-09-30), <see cref="SqlConnectionsOffered"/>'s twin: null = all, a list = exactly those names. No variable.</summary>
    public List<string>? UncSharesOffered { get; set; }

    /// <summary>Whether <c>%</c> and part of a name lists the UNC shares too (2026-09-30), each marked <c>UNC ·</c>; offered only while <see cref="UncTools"/> is on. No variable.</summary>
    public bool UncPercentMention { get; set; } = true;

    // ─── Images (ComfyUI) ───────────────────────────────────────────────────────
    // The image tools (2026-09-24, the user's ask: "what can we do with comfyui?" — text to image, img2img, their own
    // exported workflows, splash art, and prompts written for Pony Diffusion XL and the other families, or sent as typed).

    /// <summary>
    /// Whether a turn offers the image tools (<c>generate_image</c>, <c>set_splash_image</c>) over the ComfyUI server
    /// at <see cref="ComfyUrl"/> (2026-09-24); read at each turn like <see cref="SqlTools"/>, no reconnect. On by
    /// default until 2026-09-29 (it offered nothing until a URL was set and a workflow was in a <c>comfy</c> folder);
    /// off since, the user's call. No variable.
    /// </summary>
    public bool ComfyTools { get; set; }

    /// <summary>
    /// The ComfyUI server (2026-09-24): <c>http://host:8188</c>, often another machine on the LAN — the user's own
    /// server like <see cref="LlmUrl"/>, so never judged by the web tools' network mode. Empty = no image tool.
    /// Variable: <c>NEONSIDEKICK_COMFY_URL</c>.
    /// </summary>
    public string ComfyUrl { get; set; } = "";

    /// <summary>
    /// Which installed ComfyUI workflows the model is offered (later on 2026-09-24, the user's ask: "similar to how
    /// we've done it for the SQL connections … so we could possibly limit it down to one"). Null = not narrowed: every
    /// workflow, a new one included. A list = exactly those names (any case), so a workflow added later stays hidden
    /// until ticked; an empty list offers none. <c>generate_image</c>'s description and its unnamed pick see only these,
    /// as does a bare <c>/imagine</c>; <c>/imagine &lt;name&gt;</c> may still name any installed one. The ComfyUI tab of
    /// <c>/tools</c>. No variable.
    /// </summary>
    public List<string>? ComfyWorkflowsOffered { get; set; }

    /// <summary>
    /// Whether <c>^</c> and part of a name on the chat line lists the ComfyUI workflows (later still on 2026-09-24, the
    /// user's ask: "similar to the @-mention or $-mention … autocomplete comfyui workflow names to mention in chat to
    /// prompt the model which workflow to use"), as <c>%</c> lists the SQL connections: each name with its family,
    /// shape and size, a pick writes <c>^name</c> into the draft — text the model reads (and passes as <c>workflow</c>),
    /// nothing seeded. Only the offered ones (<see cref="ComfyWorkflowsOffered"/>) and only while the image tools are
    /// offered at all, since a hidden workflow is one the model cannot run. Off = <c>^</c> is ordinary text. Read at each
    /// keystroke, no reconnect; the ComfyUI tab of <c>/tools</c>. No variable.
    /// </summary>
    public bool ComfyCaretMention { get; set; } = true;

    /// <summary>
    /// Seconds one generation may take, queue wait included, before the tool gives up waiting (2026-09-24; the job
    /// itself runs on in ComfyUI): <see cref="MinComfyTimeoutSeconds"/> to <see cref="MaxComfyTimeoutSeconds"/>.
    /// </summary>
    public int ComfyTimeoutSeconds { get; set; } = DefaultComfyTimeoutSeconds;

    public const int MinComfyTimeoutSeconds = 10;
    public const int MaxComfyTimeoutSeconds = 3600;
    public const int DefaultComfyTimeoutSeconds = 300;

    /// <summary>
    /// The most pictures one <c>generate_image</c> call or <c>/imagine --count</c> makes (later on 2026-09-24, the user's
    /// ask: a setting, as <see cref="FileViewImageMaxPerCall"/> is for <c>view_image</c>; a fixed 4 before;
    /// the default 5 since later on 2026-09-24, the user's call): <see cref="MinComfyMaxPicturesPerCall"/> to <see cref="MaxComfyMaxPicturesPerCall"/>. Why a cap at all: each picture
    /// is a full job the server runs in turn, while the model's turn waits, and every one rides to the model in the next
    /// request, where a local vision server has a ceiling of its own (LM Studio + Gemma 4 fell over at six, 2026-09-14).
    /// A hand-edited value is clamped. The ComfyUI tab of <c>/tools</c>. No variable.
    /// </summary>
    public int ComfyMaxPicturesPerCall { get; set; } = DefaultComfyMaxPicturesPerCall;

    /// <summary>
    /// Whether the model is asked to reinforce its prompts through the negative (later still on 2026-09-24, the user's ask:
    /// "append to the negative prompt tags to help reinforce the positive prompt"): <c>generate_image</c>'s
    /// <c>negative_extra</c>, a few tags opposite to what the prompt asks (night → daylight, solo → multiple girls), appended to
    /// the workflow's own negative — never for a prompt the user gave verbatim, a negative set for the call, <c>/imagine</c>,
    /// a family that runs without a negative, or a workflow whose sidecar says <c>reinforce: false</c>. The ComfyUI tab of
    /// <c>/tools</c>. No variable.
    /// </summary>
    public bool ComfyReinforceNegatives { get; set; } = true;

    /// <summary>
    /// Whether the transcript shows what was sent to ComfyUI (later still on 2026-09-24, the user's ask): the <c>prompt:</c>
    /// and <c>negative:</c> lines of a generation's result — the prompt the model wrote, the negative after the workflow's or
    /// the family's and any reinforcing tags — each in full under the picture's line, then (2026-09-25, the user's ask) the
    /// <c>params:</c> line (<see cref="Comfy.ComfyText.Parameters"/>: size, steps, cfg, denoise, seed, sampler, scheduler), for
    /// <c>generate_image</c> and <c>/imagine</c> alike. Off, the picture's line alone. The model's result is the same either way. On by default since
    /// later still on 2026-09-24 (the user's call); a profile saved with it off keeps it off. The ComfyUI tab of
    /// <c>/tools</c>. No variable.
    /// </summary>
    public bool ComfyShowPrompts { get; set; } = true;

    /// <summary>
    /// Whether the session's ComfyUI pictures stand in a strip over the pane's upper rule (later still on 2026-09-24, the
    /// user's ask): every picture <c>generate_image</c> or <c>/imagine</c> made this session as a small thumbnail
    /// (<see cref="UI.PictureStrip"/>), the newest at the left pushing the older ones right; with the draft empty ← / →
    /// walk a highlight over them, Enter opens the highlighted one in the image viewer, a double-click opens any. Per
    /// session: <c>/clear</c>, <c>/new</c> and a switch empty it. Read at each draw, no reconnect. On by default (the
    /// user's call). The ComfyUI tab of <c>/tools</c>. No variable.
    /// </summary>
    public bool ComfyPictureStrip { get; set; } = true;

    public const int MinComfyMaxPicturesPerCall = 1;
    public const int MaxComfyMaxPicturesPerCall = 16;
    public const int DefaultComfyMaxPicturesPerCall = 5;

    /// <summary>
    /// The folder under the working directory the generated pictures are saved in (2026-09-24), made on first use;
    /// empty = the working directory itself. A path that leaves the sandbox is refused at the call, as any file tool's.
    /// <c>comfy_images</c> since later still on 2026-09-24 (the user's call: <c>images</c> said nothing of where they came
    /// from); a profile saved with the old <c>images</c> keeps it — no migration, no files moved.
    /// </summary>
    public string ComfyOutputFolder { get; set; } = DefaultComfyOutputFolder;

    public const string DefaultComfyOutputFolder = "comfy_images";

    // ─── Shell ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The prefixes (<see cref="Shell.CommandPrefix"/>: <c>git status</c>, <c>dotnet build</c>, <c>python</c>)
    /// the user allowed for good on the approval pane (2026-09-21, <c>Allow … always</c>): lower case,
    /// sorted ordinal, no duplicates, the <see cref="ToolsDisabled"/> shape. A command every one of whose
    /// prefixes is here runs without asking under <c>Shell command policy</c> = <c>ask</c>; the session's own
    /// allows live in the process, not here. Read at each judgement; the Shell tab's list row removes one.
    /// No variable.
    /// </summary>
    public List<string> ShellCommandAllowed { get; set; } = [];

    /// <summary>
    /// What stands between the model's <c>run_command</c> and the shell (2026-09-21, the user's three
    /// words): one of <see cref="Shell.CommandPolicy.Names"/> — <c>off</c> (no shell tool is offered: this is
    /// the Shell group's switch, and the default rules lose their shell sentence), <c>ask</c> (a command
    /// whose prefixes are not all in <see cref="ShellCommandAllowed"/> or allowed for the session is put to
    /// the user on the pane first; with no pane to ask on it is refused), <c>yolo</c> (everything runs).
    /// Anything else reads as <see cref="Shell.CommandPolicy.Default"/>, <c>off</c> since 2026-09-29 (the user's call;
    /// <c>ask</c> before). Read at each call, no reconnect.
    /// <see cref="EnvironmentOverrides.CommandPolicyVariable"/> outranks it, so a scripted headless run can say yolo.
    /// </summary>
    public string ShellCommandPolicy { get; set; } = Shell.CommandPolicy.Default;

    /// <summary>
    /// Whether a <c>run_command</c> line, an <c>execute_code</c> script or the text <c>process</c> writes to
    /// a background process may name a path outside the working directory (2026-09-22, the user's ask;
    /// on by default). On, <see cref="Shell.PathPolice"/> reads the text before the gate is asked: an
    /// absolute path not under the working directory (<c>C:\…</c>, a UNC share, a rooted <c>/…</c>), a
    /// <c>..</c> that climbs out, <c>~</c> or a folder variable (<c>%USERPROFILE%</c>, <c>$env:TEMP</c>,
    /// <c>$HOME</c>…) refuses the call with an <c>Error:</c> the model is told not to work around, the
    /// transcript line wears 👮, and the tool descriptions and the operating rules say the shell stays
    /// under the working directory. It is a lexical guard — the text the model sends, not what runs: a
    /// script that computes a path is not seen. Off, any path goes — and nothing tells the model it
    /// may leave (neither wording says a command can reach outside), so it does not try unless asked.
    /// The toolbar wears 👮 while it is on (later that day); its double-click is <c>/police</c>, this row's on/off page (later still that day).
    /// Read at each call and at each turn's prompt, no reconnect. No variable.
    /// </summary>
    public bool ShellPoliceOutsidePaths { get; set; } = true;

    /// <summary>
    /// Whether the shell steps aside for a native tool (2026-09-26, the user's ask: the model kept reaching for
    /// <c>cat</c>, <c>dir</c>, <c>git status</c> or <c>curl</c> through <c>run_command</c> when a tool of its own
    /// did the job, which cost the user an approval and the tool's guarantees; on by default). On, the operating
    /// rules gain <see cref="Llm.Assistant.ShellNativeRule"/>, naming the groups offered that turn, and a
    /// <c>run_command</c> line of one command whose prefix a native tool offered that turn covers
    /// (<see cref="Shell.NativeRedirect"/>) is not run: the model is told which tool to call, before the gate —
    /// the pane is never asked. Once a turn per line: the same line again goes on to the gate, so a real need
    /// (an option the tool lacks) still reaches the user. A line naming a path outside the working directory is
    /// never redirected (no native tool reaches there). Off, the shell takes what it is given, as before.
    /// Read at each call and at each turn's prompt, no reconnect. <c>NEONSIDEKICK_SHELL_NATIVE</c> outranks it.
    /// </summary>
    public bool ShellPreferNative { get; set; } = true;

    /// <summary>
    /// The shell a <c>run_command</c> without <c>shell</c> runs in (2026-09-21): one of
    /// <see cref="Shell.ShellKinds.Names"/> — <c>powershell</c> (pwsh when installed, else Windows PowerShell),
    /// <c>cmd</c>, <c>bash</c> (Git Bash, when found). Anything else reads as <see cref="Shell.ShellKinds.Default"/>;
    /// a shell that is not installed refuses the call with a sentence, so the row dims one not found. No variable.
    /// </summary>
    public string ShellDefault { get; set; } = Shell.ShellKinds.Default;

    /// <summary>
    /// How many seconds a foreground <c>run_command</c> without <c>timeout</c> waits before the child is
    /// killed (2026-09-21): <see cref="MinShellTimeoutSeconds"/> to <see cref="MaxShellTimeoutSeconds"/>, never
    /// over <see cref="ShellForegroundCapSeconds"/>; a hand-edited value is clamped. No variable.
    /// </summary>
    public int ShellTimeoutSeconds { get; set; } = DefaultShellTimeoutSeconds;

    public const int MinShellTimeoutSeconds = 1;
    public const int MaxShellTimeoutSeconds = 3600;
    public const int DefaultShellTimeoutSeconds = 180;

    /// <summary>
    /// The most seconds a foreground <c>run_command</c> may wait, whatever its <c>timeout</c> says
    /// (2026-09-21): <see cref="MinShellForegroundCapSeconds"/> to <see cref="MaxShellForegroundCapSeconds"/>.
    /// The turn is held while a foreground command runs; the cap bounds that. No variable.
    /// </summary>
    public int ShellForegroundCapSeconds { get; set; } = DefaultShellForegroundCapSeconds;

    public const int MinShellForegroundCapSeconds = 10;
    public const int MaxShellForegroundCapSeconds = 3600;
    public const int DefaultShellForegroundCapSeconds = 600;

    /// <summary>
    /// The most chars of a command's output one result carries back (2026-09-21):
    /// <see cref="MinShellOutputMaxChars"/> to <see cref="MaxShellOutputMaxChars"/>. Over it the result keeps
    /// its head and tail and the whole text goes to <c>.shell\&lt;id&gt;.log</c> under the working directory,
    /// where <c>read_file</c> reaches it. No variable.
    /// </summary>
    public int ShellOutputMaxChars { get; set; } = DefaultShellOutputMaxChars;

    public const int MinShellOutputMaxChars = 2000;
    public const int MaxShellOutputMaxChars = 500000;
    public const int DefaultShellOutputMaxChars = 30000;

    /// <summary>
    /// The languages <c>execute_code</c> may run (2026-09-21, the user's call: a multiple choice, one or
    /// more): words from <see cref="Shell.CodeLanguages.Names"/> — <c>powershell</c>, <c>python</c>,
    /// <c>node</c>. A language is offered only while its interpreter is found; an unknown word is dropped
    /// with a warning and a list naming nothing usable reads as all three (the Shell tab's editor never
    /// saves an empty one). Read at each turn, no reconnect. No variable.
    /// </summary>
    public List<string> ShellCodeLanguages { get; set; } = [.. Shell.CodeLanguages.Default];

    /// <summary>
    /// How many seconds an <c>execute_code</c> script without <c>timeout</c> may run before it is killed
    /// (2026-09-21): <see cref="MinShellCodeTimeoutSeconds"/> to <see cref="MaxShellCodeTimeoutSeconds"/>. A
    /// script holds the turn like a foreground command, but calls tools on the way, so its default is
    /// longer. No variable.
    /// </summary>
    public int ShellCodeTimeoutSeconds { get; set; } = DefaultShellCodeTimeoutSeconds;

    public const int MinShellCodeTimeoutSeconds = 1;
    public const int MaxShellCodeTimeoutSeconds = 3600;
    public const int DefaultShellCodeTimeoutSeconds = 300;

    /// <summary>
    /// Whether an <c>execute_code</c> script may call this app's other tools through its <c>neon_tools</c>
    /// module — the loopback bridge (later on 2026-09-21, the user's ask; off by default, the user's
    /// call, as zip/unzip and the MCP servers start). Off hides the bridge whole: no server is started,
    /// no address or token goes into the script's environment, no module is written beside it, and
    /// neither the tool's description, its schema nor the operating rules say a script can call tools —
    /// the script does everything itself. Read at each call and at each turn's prompt, no reconnect.
    /// No variable.
    /// </summary>
    public bool ShellToolBridge { get; set; }

    /// <summary>
    /// The most tool calls one <c>execute_code</c> script may make through its bridge (2026-09-21):
    /// <see cref="MinShellCodeMaxToolCalls"/> to <see cref="MaxShellCodeMaxToolCalls"/>; the one over the
    /// cap is answered with an error the script sees. Matters only while <see cref="ShellToolBridge"/>
    /// is on. No variable.
    /// </summary>
    public int ShellCodeMaxToolCalls { get; set; } = DefaultShellCodeMaxToolCalls;

    public const int MinShellCodeMaxToolCalls = 1;
    public const int MaxShellCodeMaxToolCalls = 500;
    public const int DefaultShellCodeMaxToolCalls = 50;

    // ─── Web ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How <c>web_fetch</c> gets a page: one of <see cref="Web.BrowserMode.Names"/> — <c>default</c>
    /// (the HTTP client, then a headless Edge / Chrome / Brave when the page came back blocked or as a
    /// script shell), <c>httpclient</c> (the client alone), <c>chromium</c> (the browser for every
    /// page). Anything else reads as <see cref="Web.BrowserMode.Default"/>. No variable.
    /// </summary>
    public string WebBrowserMode { get; set; } = Web.BrowserMode.Default;

    /// <summary>
    /// Where <c>web_fetch</c> may reach: one of <see cref="Web.NetworkMode.Names"/> — <c>internet</c>
    /// (public addresses alone; this machine and the local network — loopback, 10/8, 172.16/12,
    /// 192.168/16, link-local — refused), <c>local_area_network</c> (those alone; the internet
    /// refused), <c>both</c>. Every hop is judged at the socket by <see cref="Web.LanPolicy"/>; the
    /// app's own SearXNG request is exempt. Anything else reads as <see cref="Web.NetworkMode.Default"/>.
    /// Replaced the on/off <c>Browser allow LAN</c> on 2026-09-18, no migration. No variable.
    /// </summary>
    public string WebBrowserNetworkMode { get; set; } = Web.NetworkMode.Default;

    /// <summary>
    /// The Chromium executable the headless leg runs, or empty to find Edge, Chrome or Brave in their
    /// standard folders (<see cref="Web.HeadlessBrowser.Candidates"/>). No variable.
    /// </summary>
    public string WebBrowserPath { get; set; } = "";

    /// <summary>
    /// How many hits a <c>web_search</c> without <c>max_results</c> returns:
    /// <see cref="MinWebSearchMaxResults"/> to <see cref="MaxWebSearchMaxResults"/>; the argument overrides
    /// it up to the same cap, and a hand-edited value is clamped. No variable.
    /// </summary>
    public int WebSearchMaxResults { get; set; } = DefaultWebSearchMaxResults;

    public const int MinWebSearchMaxResults = 1;
    public const int MaxWebSearchMaxResults = 20;
    public const int DefaultWebSearchMaxResults = 20;

    /// <summary>
    /// Which engine <c>web_search</c> asks: one of <see cref="Web.SearchMethod.Names"/> —
    /// <c>duckduckgo</c> (the built-in scrape) or <c>searxng</c> (the instance <see cref="WebSearxngUrl"/>
    /// names; DuckDuckGo until it holds an http(s) URL). Anything else reads as
    /// <see cref="Web.SearchMethod.Default"/>. No variable.
    /// </summary>
    public string WebSearchMethod { get; set; } = Web.SearchMethod.Default;

    /// <summary>
    /// A SearXNG instance's URL (<c>http://localhost:8080</c>), read only while
    /// <see cref="WebSearchMethod"/> is <c>searxng</c>; blank or not an http(s) URL there = the
    /// built-in DuckDuckGo scrape until one is set. Variable <c>NEONSIDEKICK_SEARXNG_URL</c>
    /// (the URL alone — the method still picks the engine).
    /// </summary>
    public string WebSearxngUrl { get; set; } = "";

    /// <summary>
    /// Whether a turn offers the model <c>web_search</c> and <c>web_fetch</c>; read at each turn like
    /// <see cref="Memory"/>, no reconnect. Off, the default rules lose their web sentence.
    /// The row is the one switch (<c>/web</c> went 2026-09-18). Off by default since 2026-09-29 (the user's call, with
    /// <see cref="FileTools"/>). No variable.
    /// </summary>
    public bool WebTools { get; set; }

    // ─── MCP ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How long one MCP server gets to answer the initialize handshake and list its tools at a
    /// connect: <see cref="MinMcpConnectTimeout"/> to <see cref="MaxMcpConnectTimeout"/> seconds; past
    /// it the server is <c>failed: timed out</c> and the others are unaffected. Read at the next
    /// connect (startup, a profile switch, a flip, <c>reload</c>), no reconnect of its own. The
    /// second row of <c>/mcp</c>' Options tab (2026-09-20). No variable.
    /// </summary>
    public int McpConnectTimeoutSeconds { get; set; } = DefaultMcpConnectTimeout;

    public const int MinMcpConnectTimeout = 5;
    public const int MaxMcpConnectTimeout = 300;
    public const int DefaultMcpConnectTimeout = 30;

    /// <summary>
    /// The master switch over the MCP servers <c>mcp.json</c> names (the profile's, then the home's):
    /// on, every server not in <see cref="McpServersDisabled"/> is started at startup and after a
    /// profile switch and its tools are offered as <c>&lt;server&gt;__&lt;tool&gt;</c>; off, nothing is
    /// started and the <c>/mcp</c> pane still lists the config. A flip reconnects (disconnects) at once,
    /// so it is refused mid-turn. The first row of <c>/mcp</c>' Options tab (2026-09-20). No variable.
    /// Off by default since 2026-09-21 (the user's call): a profile opts in on that row, so a fresh
    /// install starts nothing it was not asked to.
    /// </summary>
    public bool McpServers { get; set; }

    /// <summary>
    /// The MCP servers switched off by name on <c>/mcp</c>' Servers tab (2026-09-20): sorted
    /// ordinal, no duplicates, written by <c>ToolsText.Flip</c> like <see cref="ToolsDisabled"/>.
    /// Not a settings row — the second list in the file. A name no file names any more stays
    /// harmless. No variable, no clear.
    /// </summary>
    public List<string> McpServersDisabled { get; set; } = [];

    /// <summary>
    /// The Claude Code CLI <c>/claude</c> starts (2026-09-27): a full path, or empty to look for <c>claude.exe</c> on the
    /// PATH, then npm's <c>claude.cmd</c>, then <c>%USERPROFILE%\.local\bin</c> (<see cref="Claude.ClaudeExecutable"/>).
    /// The Claude tab's first row; read at each <c>/claude</c>. Variable <see cref="EnvironmentOverrides.ClaudeExeVariable"/>.
    /// </summary>
    public string ClaudeExecutable { get; set; } = "";

    /// <summary>
    /// What the Claude Code child may do on its own (2026-09-27): one of <see cref="Claude.ClaudePermission.Names"/> —
    /// <c>read-only</c> (read, search, fetch), <c>edit</c> (file edits accepted, commands denied), <c>full</c> (everything,
    /// unasked). Nothing past the level is asked about: it is denied (<c>--permission-prompts none</c>) and the reply says
    /// so. Anything else reads as <see cref="Claude.ClaudePermission.Default"/>. Read at each <c>/claude</c>. Variable
    /// <see cref="EnvironmentOverrides.ClaudePermissionsVariable"/>.
    /// </summary>
    public string ClaudePermissions { get; set; } = Claude.ClaudePermission.Default;

    /// <summary>The <c>--model</c> of a <c>/claude</c> run (2026-09-27): an alias (<c>opus</c>, <c>sonnet</c>) or a full name; empty = the CLI's own. No variable.</summary>
    public string ClaudeModel { get; set; } = "";

    /// <summary>The <c>--effort</c> of a <c>/claude</c> run (2026-09-27): one of <see cref="Claude.ClaudeEffort.Names"/>; empty = the CLI's own. No variable.</summary>
    public string ClaudeEffort { get; set; } = Claude.ClaudeEffort.Default;

    /// <summary>
    /// Whether the model is offered <c>claude_advisor</c> (2026-09-27, the user's ask: the local model asks Claude Code
    /// for advice on its own, read-only, when it is stuck): the tool group's switch, the <c>/tools</c> Claude tab's
    /// first advisor row. Off by default — every call costs money on the user's Claude account. Variable
    /// <see cref="EnvironmentOverrides.ClaudeAdvisorVariable"/>.
    /// </summary>
    public bool ClaudeAdvisor { get; set; }

    /// <summary>
    /// What an advisor call sends Claude besides the model's question (2026-09-27, the user's call: a setting): one of
    /// <see cref="Claude.ClaudeAdvisorContext.Names"/> — <c>brief</c>, the question and the model's own context argument
    /// alone; <c>recent</c>, those and the last <see cref="Claude.ClaudeText.AdvisorRecentMessages"/> messages of the
    /// conversation. Anything else reads as <c>brief</c>. No variable.
    /// </summary>
    public string ClaudeAdvisorContext { get; set; } = Claude.ClaudeAdvisorContext.Default;

    /// <summary>The most <c>claude_advisor</c> calls one turn may make (2026-09-27), <see cref="MinClaudeAdvisorCallsPerTurn"/> to <see cref="MaxClaudeAdvisorCallsPerTurn"/>; a call past it is refused, and the model carries on alone. No variable.</summary>
    public int ClaudeAdvisorCallsPerTurn { get; set; } = DefaultClaudeAdvisorCallsPerTurn;

    public const int MinClaudeAdvisorCallsPerTurn = 1;
    public const int MaxClaudeAdvisorCallsPerTurn = 10;
    public const int DefaultClaudeAdvisorCallsPerTurn = 2;

    /// <summary>The <c>--model</c> of an advisor call (2026-09-27); empty = <see cref="ClaudeModel"/> (and, that empty too, the CLI's own). No variable.</summary>
    public string ClaudeAdvisorModel { get; set; } = "";

    /// <summary>The <c>--effort</c> of an advisor call (2026-09-27): one of <see cref="Claude.ClaudeEffort.Names"/>; empty = <see cref="ClaudeEffort"/>. No variable.</summary>
    public string ClaudeAdvisorEffort { get; set; } = "";

    /// <summary>Whether each advisor call waits for the user's yes on the approval pane (2026-09-27, the user's call: opt-in, off by default); headless has no one to ask, so a call there is refused. No variable.</summary>
    public bool ClaudeAdvisorConfirm { get; set; }
    // ─── Claude API (2026-09-27) ────────────────────────────────────────────────

    /// <summary>
    /// Whether the Claude API is offered as a server (2026-09-27, the user's ask: Anthropic's Messages API as one more
    /// <c>/server</c> choice, off by default — every message costs money on the key's account). Offered only while a key
    /// is set too (<see cref="Llm.Anthropic.ClaudeApi.Offered"/>): then <c>/server</c> and the startup picker list a
    /// <c>Claude API</c> row, and picking it saves <see cref="Llm.Anthropic.ClaudeApi.BaseUrl"/> as the LLM URL. The
    /// <c>/tools</c> Claude tab's first API row, under the advisor's (2026-09-29; <c>/settings</c>' Claude (API) tab's first until then); a reconnect. Variable <see cref="EnvironmentOverrides.ClaudeApiVariable"/>.
    /// </summary>
    public bool ClaudeApi { get; set; }

    /// <summary>
    /// The Claude API key (2026-09-27): kept encrypted with DPAPI for this Windows user on this machine (<c>dpapi:</c>
    /// and the blob, <see cref="Sql.WindowsCredentials"/>), so the profile file never holds it in clear; a plain value
    /// (a hand edit) is used as it is and encrypted at the next save from the menu. Sent only to the Claude API — never
    /// to a local server, which gets <see cref="LlmApiKey"/>. Never logged (<see cref="SettingsDiff.Secrets"/>). Variable
    /// <see cref="EnvironmentOverrides.ClaudeApiKeyVariable"/> (plain).
    /// </summary>
    public string ClaudeApiKey { get; set; } = "";

    /// <summary>
    /// The <c>max_tokens</c> every Claude API request carries (2026-09-27): the API requires one, and a reply that
    /// reaches it stops mid-sentence (logged). Thinking counts inside it. <see cref="MinClaudeApiMaxTokens"/> to
    /// <see cref="MaxClaudeApiMaxTokens"/>; a reconnect. No variable.
    /// </summary>
    public int ClaudeApiMaxTokens { get; set; } = DefaultClaudeApiMaxTokens;

    public const int MinClaudeApiMaxTokens = 1_024;
    public const int MaxClaudeApiMaxTokens = 128_000;
    public const int DefaultClaudeApiMaxTokens = 32_000;

    /// <summary>
    /// Whether Claude API requests carry prompt-cache breakpoints (2026-09-27, on by default): the tools and system
    /// prompt, and the conversation so far, are read back from the cache at a tenth of the price on the next request.
    /// A reconnect. No variable.
    /// </summary>
    public bool ClaudeApiPromptCaching { get; set; } = true;

    // ─── Claude CLI server (2026-09-30) ───────────────────────────────────────────

    /// <summary>
    /// Whether the Claude Code CLI is offered as a server (2026-09-30, the user's ask: the <c>claude</c> CLI kept running as
    /// an open session and used as the chat's server; off by default). Offered only while the CLI is found too
    /// (<see cref="Claude.ClaudeCliEndpoint.Offered"/>: <see cref="ClaudeExecutable"/>, else the PATH): then <c>/server</c>
    /// and the startup picker list a <c>Claude CLI</c> row, and picking it saves <see cref="Claude.ClaudeCliEndpoint.BaseUrl"/>
    /// as the LLM URL and the <c>--model</c> word as the LLM model. Claude Code's own tools are all off there: the model gets
    /// the app's tools, over an MCP server the app hosts. The <c>/tools</c> Claude tab's last row; a reconnect. Variable
    /// <see cref="EnvironmentOverrides.ClaudeCliServerVariable"/>.
    /// </summary>
    public bool ClaudeCliServer { get; set; }

    // ─── Embedded model (2026-09-29) ───────────────────────────────────────────────
    // The embedded model (2026-09-29, the user's ask: a small model the app downloads from Hugging Face and runs itself on
    // llama.cpp's llama-server, a /server choice like any other). Which model runs is the LLM URL (EmbeddedLlm.EmbeddedEndpoint)
    // and the LLM model (a EmbeddedLlm.EmbeddedModelCatalog id); these four say how it runs. Each is a reconnect, and the
    // server restarts only when what it was started with changed (EmbeddedLlm.LlamaLaunch).

    /// <summary>
    /// Whether the embedded model is offered at all (2026-09-29, the user's ask; on by default): off takes its models out of
    /// <c>/server</c> (and <c>/server embedded</c>), a saved embedded URL then reads as none, and a running
    /// <c>llama-server</c> stops at the reconnect the change brings — its memory is free again. Installed models stay on
    /// disk; the <c>Embedded models</c> row still installs and removes them. The Embedded tab's first row. <c>EmbeddedLlmEnabled</c>
    /// / <c>Embedded LLM enabled</c> until later that day, when the user named it <c>Embedded LLM server enabled</c>; the key
    /// follows the label without the <c>Enabled</c> suffix, so a saved off went back to on once. No variable.
    /// </summary>
    public bool EmbeddedLlmServer { get; set; } = true;

    /// <summary>
    /// Which llama.cpp build runs the embedded model (2026-09-29): one of <see cref="EmbeddedLlm.EmbeddedBackends.Names"/> —
    /// <c>auto</c> (the default: CUDA with an NVIDIA driver new enough, else Vulkan, else the CPU;
    /// <see cref="EmbeddedLlm.LlamaBackendDetect"/>), <c>cuda</c>, <c>vulkan</c> or <c>cpu</c>. Anything else reads as
    /// <c>auto</c>. Each build is downloaded on first use. Variable <see cref="EnvironmentOverrides.EmbeddedBackendVariable"/>.
    /// </summary>
    public string EmbeddedBackend { get; set; } = EmbeddedLlm.EmbeddedBackends.Auto;

    /// <summary>
    /// The embedded server's context window in tokens (2026-09-29): 0 to fit — the largest context the GPU holds beside the
    /// model within <see cref="EmbeddedVramBudget"/>, from the model's own window (Gemma 4 E2B/E4B: 128K) down to 4096, as
    /// llama.cpp's fit sizes an unset <c>-c</c> (left out since 2026-09-30: <c>-c 0</c> read as the model's whole window, and
    /// fit moved layers to the CPU instead, a ninth of the speed; "the model's own" until later on 2026-09-29) — else
    /// <see cref="EmbeddedLlm.EmbeddedContextSize.Min"/> to <see cref="EmbeddedLlm.EmbeddedContextSize.Max"/>, which fit never
    /// shrinks. Fit by default since later on 2026-09-29 (the user's call; 32768 until then, which a profile that saved it keeps).
    /// Variable <see cref="EnvironmentOverrides.EmbeddedContextVariable"/>.
    /// </summary>
    public int EmbeddedContextSize { get; set; } = EmbeddedLlm.EmbeddedContextSize.Default;

    /// <summary>
    /// How many of the model's layers the embedded server puts on the GPU (2026-09-29): <c>auto</c> (the default: llama.cpp
    /// fits what the free VRAM holds), <c>all</c>, or a count from 0 (CPU only) to 999. No variable.
    /// </summary>
    public string EmbeddedGpuLayers { get; set; } = EmbeddedLlm.EmbeddedGpuLayers.Auto;

    /// <summary>
    /// How much of the GPU's dedicated memory the embedded server may fill (later on 2026-09-29, the user's ask: "a maximum
    /// VRAM budget, like 92%"): 0 is off (llama.cpp's fit leaves 1 GiB free per device), else
    /// <see cref="EmbeddedLlm.EmbeddedVramBudget.Min"/> to <see cref="EmbeddedLlm.EmbeddedVramBudget.Max"/> % of the adapter
    /// with the most memory — the rest, passed as <c>--fit-target</c>, is left free. The default is 91 since 2026-09-30 (the
    /// user's call; off until then), and a profile saved with it off keeps it off. It sizes only what fit may: an
    /// <see cref="EmbeddedContextSize"/> of 0 and <see cref="EmbeddedGpuLayers"/> <c>auto</c>; with a set context that does not fit
    /// under it, fit's one lever is the layers, which then go to the CPU, slowly. CUDA and Vulkan only; a change restarts the
    /// server. No variable.
    /// </summary>
    public int EmbeddedVramBudget { get; set; } = EmbeddedLlm.EmbeddedVramBudget.Default;

    /// <summary>
    /// Whether the embedded server must stay in VRAM (2026-10-01, the user's ask: "forbid spilling over into system RAM if
    /// VRAM runs out", off by default). llama.cpp has no such switch, and memory reaches system RAM two ways: llama.cpp's fit
    /// moving layers to the CPU (<see cref="EmbeddedGpuLayers"/> <c>auto</c>), and on Windows the NVIDIA driver's CUDA sysmem
    /// fallback, which places what does not fit in shared memory without llama.cpp knowing. On, every layer goes on the GPU
    /// (<c>-ngl all</c>, whatever <see cref="EmbeddedGpuLayers"/> says; fit then shrinks only an unset context), and a load
    /// that still spilled — an allocation that failed, layers on the CPU, shared GPU memory beyond llama.cpp's pinned
    /// buffers — is stopped and the connect refused with what to lower (<see cref="EmbeddedLlm.VramSpill"/>). Refused on the
    /// CPU backend, on Vulkan over a GPU with under 1 GiB of its own memory (an integrated GPU, whose every allocation is
    /// shared), and for a load whose layer line never came, so it could not be checked (the same day's review). The driver's own switch is the NVIDIA Control Panel's per-program "CUDA - Sysmem Fallback Policy"; the
    /// app never changes it. A change restarts the server. No variable.
    /// </summary>
    public bool EmbeddedVramOnly { get; set; }

    /// <summary>
    /// Which size the embedded model lists' 8GB / 16GB / 32GB filter buttons measure (later on 2026-09-29, the user's ask and
    /// names): <c>file</c> (the default "for now": the size the row shows — weights, vision projector and drafter) or
    /// <c>gguf</c> (the weights alone), <see cref="EmbeddedLlm.EmbeddedFilterTypes.Names"/>. Display only: no reconnect, no
    /// variable.
    /// </summary>
    public string EmbeddedFilterType { get; set; } = EmbeddedLlm.EmbeddedFilterTypes.Default;

    /// <summary>
    /// How the embedded models' files come down from Hugging Face (2026-09-30, the user's ask and names): <c>parallel</c> (the
    /// default: each file over <see cref="Speech.ModelStore.ParallelConnections"/> ranged connections, about twice one stream's
    /// speed as measured that day) or <c>single</c> (one connection per file, as before), <see cref="EmbeddedLlm.EmbeddedHfDownloadTypes.Names"/>.
    /// Read as each download starts: no reconnect, no variable. A download already begun resumes in the form it began in.
    /// </summary>
    public string EmbeddedHfDownloadType { get; set; } = EmbeddedLlm.EmbeddedHfDownloadTypes.Default;

    /// <summary>
    /// Whether the embedded server loads the model's vision projector (2026-09-29, on by default): images can then be sent
    /// to it. Off saves about 1 GB of memory; an image sent then is refused with a word. Every install downloads the
    /// projector either way. No variable.
    /// </summary>
    public bool EmbeddedVision { get; set; } = true;

    /// <summary>
    /// Whether the embedded server drafts ahead, MTP multi-token prediction (2026-09-29, the user's ask; on by default):
    /// speculative decoding with the model's drafter (<see cref="EmbeddedLlm.EmbeddedModel.Drafter"/>, fetched at the start
    /// when missing) or the head its weights carry (<see cref="EmbeddedLlm.EmbeddedModel.MtpHead"/>). The model checks
    /// every drafted token, so the answer is the same, only faster; off is the way back if a build misbehaves with it, and
    /// off no drafter is loaded, nor downloaded with an install (the next start with it on fetches one). A model without
    /// MTP runs the same either way. <c>EmbeddedMtp</c> / <c>Embedded MTP</c> until later that day, when the user named it
    /// <c>Embedded drafter</c>; the key follows the label, so a saved off went back to on once. No variable.
    /// </summary>
    public bool EmbeddedDrafter { get; set; } = true;

    // ─── Home Assistant (2026-09-28) ────────────────────────────────────────────
    // The ha_ tools and /ha (2026-09-28, the user's ask: "plan an integration for Home Assistant" — a Docker instance with
    // Hue lights and a Bravia TV; read and act, the risky calls behind the pane; /ha for direct control; Assist as a fallback).

    /// <summary>
    /// Whether a turn offers the Home Assistant tools (<c>ha_overview</c>, <c>ha_lights</c>, …) over the server at
    /// <see cref="HomeAssistantUrl"/> (2026-09-28); read at each turn like <see cref="ComfyTools"/>, no reconnect. On by
    /// default until 2026-09-29 (it offered nothing until a URL and a token were set); off since, the user's call. The
    /// Home Assistant tab of <c>/tools</c>. No variable.
    /// </summary>
    public bool HomeAssistantTools { get; set; }

    /// <summary>
    /// The Home Assistant server (2026-09-28): <c>http://localhost:8123</c> or another machine on the LAN — the user's own
    /// server like <see cref="ComfyUrl"/>, so never judged by the web tools' network mode. Empty = no Home Assistant tool.
    /// Variable: <c>NEONSIDEKICK_HA_URL</c>.
    /// </summary>
    public string HomeAssistantUrl { get; set; } = "";

    /// <summary>
    /// The long-lived access token (2026-09-28; made in Home Assistant under the user's profile, Security): kept encrypted
    /// with DPAPI for this Windows user on this machine (<c>dpapi:</c> and the blob, as <see cref="ClaudeApiKey"/>), so the
    /// profile file never holds it in clear; a plain value (a hand edit) is used as it is and encrypted at the next save from
    /// the menu. Never logged (<see cref="SettingsDiff.Secrets"/>). Variable <c>NEONSIDEKICK_HA_TOKEN</c> (plain).
    /// </summary>
    public string HomeAssistantToken { get; set; } = "";

    /// <summary>
    /// What the model may switch (2026-09-28, the user's call: "read + act, ask for risky"): one of
    /// <see cref="HomeAssistant.HaPolicy.Names"/> — <c>off</c> (reads only), <c>ask</c> (the default: the services in
    /// <see cref="HomeAssistantSafeServices"/> run, anything else waits for the user's yes on the pane; headless refuses
    /// it), <c>allow</c> (everything runs). Anything else reads as <c>ask</c>. <c>/ha</c> is never judged. No variable.
    /// </summary>
    public string HomeAssistantActionPolicy { get; set; } = HomeAssistant.HaPolicy.Default;

    /// <summary>
    /// The services that run without asking under <c>ask</c> (2026-09-28): <c>domain.service</c> entries, <c>domain.*</c>
    /// for a whole domain. Null = <see cref="HomeAssistant.HaPolicy.DefaultSafeServices"/> (the lights, a scene, the TV's
    /// power, volume, source and playback, the to-do lists). profile.json only, no row. No variable.
    /// </summary>
    public List<string>? HomeAssistantSafeServices { get; set; }

    /// <summary>
    /// The conversation agent <c>ha_assist</c> and <c>/ha say</c> talk to (2026-09-28): an agent's id
    /// (<c>conversation.google_generative_ai</c>); empty = Home Assistant's default. No variable.
    /// </summary>
    public string HomeAssistantAssistAgent { get; set; } = "";

    /// <summary>Seconds one Home Assistant request may take (2026-09-28): <see cref="MinHomeAssistantTimeoutSeconds"/> to <see cref="MaxHomeAssistantTimeoutSeconds"/>. No variable.</summary>
    public int HomeAssistantTimeoutSeconds { get; set; } = DefaultHomeAssistantTimeoutSeconds;

    public const int MinHomeAssistantTimeoutSeconds = 2;
    public const int MaxHomeAssistantTimeoutSeconds = 60;
    public const int DefaultHomeAssistantTimeoutSeconds = 10;

    // ─── Printing (2026-09-28) ──────────────────────────────────────────────────
    // /print and print_file (2026-09-28, the user's ask: send a file to a printer; the app draws text, markdown and pictures
    // itself, anything else goes to its own program; the model's prints ask first).

    /// <summary>
    /// Whether a turn offers <c>print_file</c> and <c>list_printers</c> (2026-09-28). Off by default: printing spends paper, and a
    /// profile opts in. <c>/print</c> works either way. The Print tab of <c>/tools</c>. No variable.
    /// </summary>
    public bool PrintTools { get; set; }

    /// <summary>
    /// What the model may print (2026-09-28, the user's call): one of <see cref="Printing.PrintPolicy.Names"/> — <c>off</c> (list
    /// the printers only), <c>ask</c> (the default: every print waits for the user's yes on the pane; headless refuses it),
    /// <c>allow</c> (it prints). Anything else reads as <c>ask</c>. <c>/print</c> is never judged. No variable.
    /// </summary>
    public string PrintActionPolicy { get; set; } = Printing.PrintPolicy.Default;

    /// <summary>The printer a print goes to when none is named (2026-09-28); empty = the Windows default. No variable.</summary>
    public string PrintDefaultPrinter { get; set; } = "";

    /// <summary>
    /// The body size in points a listing or a markdown file prints at (2026-09-28): <see cref="Printing.PrintLayout.MinFontSize"/>
    /// to <see cref="Printing.PrintLayout.MaxFontSize"/>; headings scale from it. No variable.
    /// </summary>
    public int PrintFontSize { get; set; } = Printing.PrintLayout.DefaultFontSize;
}
