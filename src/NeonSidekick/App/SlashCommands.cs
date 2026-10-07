namespace NeonSidekick.App;

/// <summary>What a typed line turned out to be.</summary>
public enum SlashCommand
{
    /// <summary>Not a command: a message for the model.</summary>
    None,
    Help,

    /// <summary><c>/clear</c>: forget the conversation and wipe the screen back to the banner.</summary>
    Clear,

    /// <summary><c>/new</c>: forget the conversation and keep the screen — a rule and a notice mark the boundary in the transcript (2026-09-16).</summary>
    New,

    /// <summary><c>/splash</c>: forget the conversation, wipe the screen and show the welcome splash again — the startup view, whatever <c>Welcome splash</c> says (2026-09-19, the user's ask): its tiled pages when it says <c>tiled</c>, else one picture, <c>disabled</c> included (2026-09-24). No argument.</summary>
    Splash,

    /// <summary>
    /// <c>/rewind</c> (2026-09-30, the user's ask): go back to an earlier message. It and everything after it leave the
    /// conversation and the session, and its text returns to the input. <c>/rewind &lt;n&gt;</c> starts the picker n messages
    /// back. A double ESC on an empty line opens it too.
    /// </summary>
    Rewind,
    /// <summary><c>/theme</c>: pick the colour theme from a list, or <c>/theme &lt;name&gt;</c> (2026-09-23, the user's ask); a change starts over the way <c>/splash</c> does — the conversation forgotten, the screen wiped, the splash shown in the new colours. Refused while a reply runs.</summary>
    Theme,

    /// <summary><c>/compact</c>: shrink the history the model re-reads (a summary and the recent turns, or pruned tool results), or <c>/compact &lt;focus&gt;</c>.</summary>
    Compact,

    /// <summary><c>/server</c>: pick an LLM server from the ones that answer, or <c>/server &lt;url&gt;</c>; then its model.</summary>
    Server,
    Model,

    /// <summary><c>/reasoning</c>: pick the LLM reasoning effort from a list, or <c>/reasoning &lt;level&gt;</c>.</summary>
    Reasoning,

    /// <summary><c>/sampling</c> (2026-09-28, the user's ask): the sampling overrides per model on a pane, or <c>/sampling &lt;field&gt; &lt;value|clear&gt;</c>, <c>/sampling extra {…}</c> and <c>/sampling clear</c> for the connected model's.</summary>
    Sampling,
    Settings,

    /// <summary><c>/tools</c>: the Tools pane (2026-09-19, the user's ask) — every tool the model can be offered, on or off one by one (the Offered tab), then its Options tab (the <c>$</c>-mention switch, later that day) and the Ask, Files and Web settings rows that sat on <c>/settings</c> until then. Bare like <see cref="Skills"/> until 2026-10-03, when <c>/tools &lt;group&gt;</c> came with the toolbar's tool switches (the user's ask): a group's switch opened straight (<see cref="ToolsText.SwitchWords"/>), as <see cref="Police"/> opens its row. <c>///</c> is its alias (2026-09-21, the user's ask, beside <c>//</c>).</summary>
    Tools,

    /// <summary><c>/mcp</c>: the MCP pane (2026-09-20, the user's ask) — the servers the two <c>mcp.json</c> files name, on or off one by one and connected or not (the Servers tab), their tools on or off by their prefixed names (the Tools tab), then its Options tab (the master switch and the connect timeout). No argument: bare like <see cref="Tools"/>.</summary>
    Mcp,
    Tts,
    Voice,
    Wake,
    Interrupt,

    /// <summary><c>/remember &lt;text&gt;</c>: keep one fact across sessions.</summary>
    Remember,

    /// <summary><c>/memory</c>: list what is remembered, Enter removing one; <c>/memory on|off</c> switches Memory (2026-10-03, the user's ask); <c>/memory forget</c> erases every one, after a confirmation; <c>/memory copy &lt;profile&gt; [overwrite]</c> copies them into another profile — appended, the duplicates skipped, or in place of its own — after a confirmation too (2026-09-22, the user's ask, twice: the wipe was <c>/forget</c>, its own command, until that morning, and the copy was <c>/memcopy</c> (2026-09-17) until later that day; both words are unknown commands now).</summary>
    Memory,

    /// <summary><c>/cmdcopy &lt;profile&gt; [--history] [overwrite]</c>: copy this profile's allowed shell commands (the <c>Shell allowed commands</c> prefixes) into another's — appended, the duplicates skipped, or in place of them — after a confirmation (2026-09-21, the user's ask: what was <c>/memcopy</c> then — <c>/memory copy</c> since 2026-09-22 — for the approval pane's list); with <c>--history</c> (2026-09-25, the user's ask) its command history instead, into the other profile's <c>sessions.db</c>.</summary>
    CmdCopy,

    /// <summary><c>/keycopy &lt;profile&gt;</c> (2026-09-28, the user's ask): this profile's <c>LLM API key</c>, <c>Anthropic API key</c> and <c>Home Assistant API key</c> (it joined the same day, the user's ask) into another's <c>profile.json</c>, after a confirmation — <c>/cmdcopy</c>'s shape without its switches. All mirrored (the user's call): a key not set here clears the target's, so the target ends with exactly this profile's keys. The stored values, as stored (a <c>dpapi:</c> key stays one); a key that comes only from a variable is not copied.</summary>
    KeyCopy,

    /// <summary>
    /// <c>/srvcopy &lt;profile&gt;</c> (2026-10-07, the user's ask, <see cref="KeyCopy"/>'s twin): this profile's server settings — the LLM
    /// URL, model and scan mode, the embedded servers and their tuning, the Docker servers and their timings, the Anthropic API, OpenAI
    /// API and Claude CLI server switches (<c>ChatScreen.SrvCopyFields</c>) — into another profile's, after a confirmation.
    /// </summary>
    SrvCopy,

    /// <summary><c>/cmdclear</c> (2026-09-25, the user's ask): this profile's command history — the input line's Up/Down recall, stored in <c>sessions.db</c> under <c>Keep command history</c> — emptied, stored and in memory, after a confirmation. No argument.</summary>
    CmdClear,

    /// <summary><c>/cmdlist</c>: this profile's allowed shell commands (the <c>Shell allowed commands</c> prefixes) on a pane, Enter removing one — the Tools pane's row opened straight, ESC closing the pane (2026-09-21, the user's ask: the toolbar's lock glyph's word, typed). No argument.</summary>
    CmdList,

    /// <summary><c>/police</c> (2026-09-22, the user's ask): the <c>Shell police</c> on/off page on a pane — the Tools pane's row opened straight, ESC closing the pane, as <see cref="CmdList"/> opens the allowed list; the toolbar officer's word. No argument.</summary>
    Police,

    /// <summary><c>/persona</c>: open <c>persona.md</c> in the editor Windows associates with it, <c>/persona reset</c> to remove it (2026-09-16), or <c>/persona copy &lt;profile&gt; [force]</c> to copy it into another profile (2026-09-21).</summary>
    Persona,

    /// <summary><c>/operata</c>: open <c>operata.md</c> (the operating rules) in the editor Windows associates with it (created, when missing, with the rules the next reply would send, its tool sentences included, 2026-10-04), <c>/operata reset</c> to remove it (2026-09-16), or <c>/operata copy &lt;profile&gt; [force]</c> to copy it into another profile (2026-09-21).</summary>
    Operata,

    /// <summary><c>/vocalia</c>: open <c>vocalia.md</c> (the spoken-reply directive) in the editor Windows associates with it, <c>/vocalia reset</c> to remove it (2026-09-16), or <c>/vocalia copy &lt;profile&gt; [force]</c> to copy it into another profile (2026-09-21).</summary>
    Vocalia,

    /// <summary><c>/sys</c>: the system prompt the next turn sends and the tools it offers, in the info pane. Named <c>/sysprompt</c> until 2026-09-21, when the retired short alias became the one word.</summary>
    Sys,

    /// <summary><c>/usage</c>: the tokens used and the speed — the last reply, this conversation, since launch.</summary>
    Usage,

    /// <summary>
    /// <c>/perfbar [off|text|gauge|spark|led]</c> (2026-09-29, the user's ask): bare, the performance bar hidden, or shown again
    /// with the meters it last had (<see cref="Settings.AppSettingsData.PerformanceBarLastItems"/>; CPU, RAM, GPU and VRAM the
    /// first time, 2026-09-30); <c>off</c> hides it; a look sets it and shows the bar. Display only, so it runs at once under a
    /// reply; the toolbar's 📈 word.
    /// </summary>
    Perf,

    /// <summary>
    /// <c>/toolbar [on|off]</c> (later on 2026-09-30, the user's ask: "same as how /perf works for the perfbar"): bare, the toolbar
    /// hidden while it shows — its items kept in <see cref="Settings.AppSettingsData.ToolbarLastItems"/> — else shown again with
    /// them (<see cref="ToolbarItems.Defaults"/> the first time); <c>on</c> and <c>off</c> say which. Display only, so it runs at
    /// once under a reply; Ctrl+T (Ctrl+Alt+B until later still on 2026-10-01).
    /// </summary>
    Toolbar,

    /// <summary>
    /// <c>/header [on|off]</c> (2026-10-01, the user's ask, with Ctrl+Alt+H): <c>Show header</c> flipped, or set by the word, and
    /// saved; the banner comes or goes at the next wipe, nothing redrawn now (<see cref="HeaderToggle"/>). A setting alone, so it
    /// runs at once under a reply, and a pane stays open over it.
    /// </summary>
    Header,

    /// <summary><c>/profile</c>: pick, switch to, add, delete, rename or reset a profile; since 2026-09-21 <c>edit</c> opens its <c>profile.json</c> and <c>reload</c> reads it back.</summary>
    Profile,

    /// <summary><c>/timer</c>: list the timers, start one, stop one or all.</summary>
    Timer,

    /// <summary><c>/cwd</c>: show the working directory, or <c>/cwd &lt;path&gt;</c> | <c>~</c> (<c>default</c> went 2026-09-16) | <c>browse</c> (the folder picker on the pane, 2026-09-21).</summary>
    Cwd,

    /// <summary><c>/tree</c>: a tree of the working directory's folders and files, or <c>/tree &lt;path&gt;</c> for a folder under it.</summary>
    Tree,

    /// <summary><c>/vault</c> (2026-09-22, the user's ask: "similar to tree"): the <c>Obsidian vault</c>'s folders and notes as <c>/tree</c> prints the working directory's — the dot-folders (<c>.obsidian</c>, <c>.trash</c>, <c>.git</c>) left out, as the vault tools leave them, and the same <c>File /tree max length</c> and <c>File /tree show sizes</c>. An error while <c>Obsidian tools</c> is off, no vault is set, or the folder cannot be reached or is no vault. Since 2026-09-23 (the user's ask: "work like /tree") <c>/vault &lt;path&gt;</c> walks a folder under the vault instead, and the argument list offers the vault's folders as <c>/tree</c>'s offers the sandbox's.</summary>
    Vault,

    /// <summary><c>/explore</c>: open the working directory in the system's file browser (Explorer, Finder, …), or <c>/explore &lt;path&gt;</c> for a folder under it.</summary>
    Explore,

    /// <summary>
    /// <c>/terminal</c> (2026-10-03, the user's ask: "similar to /explore"): a new Windows Terminal window in the working
    /// directory, or <c>/terminal &lt;folder&gt;</c> in a folder under it (<see cref="Llm.PersonaFile.OpenTerminal"/>). Ctrl+. runs it.
    /// </summary>
    Terminal,

    /// <summary><c>/copy</c>: copy the last exchange to the clipboard as markdown, or <c>/copy &lt;n&gt;</c> | <c>all</c>.</summary>
    Copy,

    /// <summary><c>/draft</c>: a temporary text file in your editor; saved and closed, its text is the next message — sent through the input line as a paste, so a long one lands as a token — and the file goes; blank, or closed unsaved, nothing is sent (2026-09-19). No argument.</summary>
    Draft,

    /// <summary><c>/loop &lt;count&gt; [delay] &lt;message&gt;</c> | <c>/loop infinite [delay] &lt;message&gt;</c>: the message sent that many times, or until ESC or Ctrl+C, each reply waited for as if typed again (2026-09-21, the user's ask), and with a delay (<c>30s</c>, <c>5m</c>; 2026-09-24) that long a gap after each reply. Refused mid-turn like <see cref="Draft"/>: it sends what the running turn cannot take.</summary>
    Loop,

    /// <summary><c>/botchat [profile …] [topic]</c> (2026-09-24, the user's ask): the profiles talk to each other — the named ones, or every profile when none is named — each reply in the speaker's own persona and voice, all on this profile's LLM server and model, one turn after another, a random speaker each time but never the same one twice running, until ESC or Ctrl+C. No tools. A line typed meanwhile joins the chat as the user's; the chat is saved as a session. Refused mid-turn like <see cref="Loop"/>.</summary>
    BotChat,

    /// <summary><c>/plan &lt;requirement&gt;</c> (2026-09-26, the user's ask): plan mode — the model researches with read-only tools, asks what it needs and presents a plan saved as <c>.neon/plans/&lt;name&gt;.md</c>; nothing changes until the user approves it on the pane or with <c>/plan approve [--fresh]</c>. While planning, <c>/plan</c> or <c>/plan show</c> says where it stands, <c>/plan cancel</c> leaves, anything else is more detail. Refused mid-turn like <see cref="Loop"/>.</summary>
    Plan,

    /// <summary><c>/expand</c>: every folded tool run, code block, diff (2026-10-04) and thinking block in the transcript unfolded, and the ones to come (2026-09-22, the user's ask: what <c>/tools expand</c> did that morning, as a root word). No argument; Ctrl+O flips the same state.</summary>
    Expand,

    /// <summary><c>/collapse</c>: every tool run, code block and diff past its collapse count, and every thinking block, folded again (2026-09-22, the user's ask: what <c>/tools collapse</c> did, as a root word). No argument.</summary>
    Collapse,


    /// <summary><c>/gituser [force]</c> (2026-09-21): the <c>GitLib email</c> and <c>GitLib name</c> settings (<c>Git native …</c> until 2026-09-30) written into the working directory's repository config as <c>user.email</c> / <c>user.name</c>; a <c>[user]</c> section already there is kept unless <c>force</c>, and <c>GitLib tools</c> off refuses (later that day). <c>/git user [force]</c> until 2026-09-26 (the user's call: the one verb was noise); <c>/git</c> is an unknown command now.</summary>
    GitUser,

    /// <summary><c>/window</c> (<c>/windowsize</c> until later on 2026-09-19): the terminal window's width and height, for information.</summary>
    Window,

    /// <summary><c>/keycheck</c> (2026-10-04, the user's ask): every key chord of the app asked of Windows — free, or held by another program as a global hotkey — on an info pane. No argument; a pane under a reply.</summary>
    KeyCheck,

    /// <summary>
    /// <c>/log</c> (2026-09-22, the user's ask): the <c>--log</c> file opened in the editor Windows associates with it, and only
    /// a command under <c>--log</c>. Since 2026-10-02 (the user's ask) a command in every run: the bare word opens the log window
    /// (<see cref="Viewer.LogWindow"/>, over the run's lines in memory), and <c>/log --file</c> the <c>--log</c> file in the
    /// editor as before.
    /// </summary>
    Log,

    /// <summary>
    /// <c>/process</c> (2026-10-05, the user's pick from the external windows brainstorm): the background processes the model
    /// started (<c>run_command</c>'s <c>background</c>) listed on a pane (<see cref="ProcessMenu"/> since later that day: a row opens,
    /// the kill button stops; the info pane until then; the toolbar's ⚡ too); <c>/process &lt;id&gt;</c> (any unique prefix) shows
    /// one's output live in the process window (<see cref="Viewer.ProcessWindow"/>), switching the open window to it. A pane
    /// under a reply bare, quick with an id.
    /// </summary>
    Process,

    /// <summary><c>/about</c>: the app's version, runtime, folders, servers, third-party components and licence, in the info pane.</summary>
    About,

    /// <summary><c>/skills</c>: the Skills pane listing the skills the model can load, the project notes file and the skill folders (<c>/skills</c> again since 2026-09-19, the plural beside <c>/tools</c>, the user's call — <c>/skill</c> is an unknown command now; a bare <c>/skill</c> from later on 2026-09-18, <c>/skill list</c> earlier that day, <c>/skills</c> from 2026-09-16 until then). No argument: <c>/skill &lt;name&gt; [message]</c> loaded a skill into the next reply from 2026-09-16 until later on 2026-09-18, when the <c>#</c>-mention made it redundant (the user's call); an argument was <see cref="Overloaded"/> until 2026-09-21, when <c>/skills edit &lt;name&gt;</c> came (the user's ask: the skill's <c>SKILL.md</c> in your editor). Since 2026-09-26 it takes <c>add &lt;source&gt;</c> (the user's ask: search skills.sh and install from GitHub, <see cref="Skills.SkillInstallFlow"/>); any other argument is its usage error. <c>////</c> was its alias for part of that day (gone later on 2026-09-21, the user's ask, with <c>///</c> for <c>/tools</c>).</summary>
    Skills,

    /// <summary><c>/learn [note]</c>: a skill-learning reflection over the last turn whatever its shape, the note steering it (2026-09-17, the explicit signal of <c>Skills auto learn</c>); <c>/learn sessions [N | text]</c> a pass over the stored sessions (2026-09-19).</summary>
    Learn,
    /// <summary><c>/speak &lt;file&gt; [n]</c>: a text file from the working directory printed as a reply and read aloud when speech is on, from sentence <em>n</em> when given — the model never sees it; <c>/speak</c> alone resumes a stopped reading, <c>/speak &lt;n&gt;</c> starts the last file at sentence <em>n</em> (2026-09-17).</summary>
    Speak,

    /// <summary><c>/view &lt;image or folder&gt; [--chat | --thumbs]</c>: a picture from the working directory in the picture viewer (2026-09-27), drawn in the transcript with <c>--chat</c> (the 2026-09-17 form), or its folder in the thumbnail browser with <c>--thumbs</c> (2026-10-04, <c>/thumbs</c> folded in) — the model never sees it.</summary>
    View,


    /// <summary><c>/imagine [workflow] &lt;prompt&gt; [-- &lt;negative&gt;] [--seed N] …</c> (2026-09-24, the user's ask): the prompt sent to ComfyUI exactly as typed — no model in between — the picture drawn in the transcript and saved under the working directory, and handed to the model with the next message.</summary>
    Imagine,

    /// <summary><c>/comfy</c> (2026-09-24): the ComfyUI server's status, the workflows found and the folders they go in; <c>/comfy edit json|markdown &lt;workflow&gt;</c> (later still that day, the user's ask) opens a workflow's <c>.json</c> or <c>.md</c> in the editor, its argument list completing the verb, the kind and the names; <c>/comfy offered</c> (2026-10-04) lists the offered workflows.</summary>
    Comfy,

    /// <summary>
    /// <c>/ha</c> (2026-09-28, the user's call: a direct command beside the model's Home Assistant tools): the house driven
    /// without the model — <c>/ha</c> the overview, <c>/ha on|off|toggle &lt;name&gt; [n%]</c>, <c>/ha scene &lt;name&gt;</c>,
    /// <c>/ha tv …</c>, <c>/ha states [filter]</c>, <c>/ha say &lt;sentence&gt;</c> (<see cref="HomeAssistant.HaCommand"/>).
    /// </summary>
    HomeAssistant,

    /// <summary>
    /// <c>/docker</c> (2026-10-02, the user's ask: control Docker Desktop from the app): the bare word opens the containers on a
    /// pane (<see cref="DockerMenu"/>) — start, stop, restart, pause, logs, open a published port, copy the id —, and
    /// <c>/docker ps | status | logs &lt;container&gt; [lines] | stats [container] | start|stop|restart|pause|unpause &lt;container&gt;</c>
    /// runs typed (<see cref="Docker.DockerCommand"/>). The user's own hand: never judged by <c>Docker writes</c>.
    /// </summary>
    Docker,

    /// <summary>
    /// <c>/camera</c> (2026-10-02, the user's ask: a USB camera's pictures for the model): the bare word opens the shutter pane and
    /// puts the photo on the input line; <c>/camera snap | list | use &lt;n|name&gt; | live | watch [seconds|off] | off</c>
    /// (<see cref="Camera.CameraCommand"/>). The user's own hand: never judged by <c>Camera tool</c>.
    /// </summary>
    Camera,

    /// <summary>
    /// <c>/screen [screen | all | monitor:N | window:&lt;id or title words&gt; | behind | list]</c> (2026-10-04): a screenshot onto the
    /// input line, or the targets listed. The user's own hand: never judged by <c>Screen capture tool</c> or its ask.
    /// </summary>
    Screen,

    /// <summary>
    /// <c>/youtube</c> (2026-10-05, the YouTube plan): <c>/youtube &lt;words&gt;</c> searches and picks a video on the pane to play in
    /// the video window; <c>play &lt;id|link&gt; [&lt;time&gt;] | pause | resume | seek | volume | mute | unmute | close | status</c>
    /// drive it (<see cref="YouTube.YouTubeCommand"/>). The user's own hand: never judged by <c>YouTube tools</c>.
    /// </summary>
    YouTube,

    /// <summary>
    /// <c>/print</c> (2026-09-28, the user's ask): a file of the working directory on paper — <c>/print &lt;file&gt;
    /// [printer=&lt;name&gt;] [copies=N] [pages=1-3] [landscape]</c>, <c>/print reply</c> for the last reply, <c>/print printers</c>
    /// (<see cref="Printing.PrintCommand"/>). The user's own hand: never judged by <c>Print action policy</c>.
    /// </summary>
    Print,

    /// <summary>
    /// <c>/pdf</c> (2026-10-03, the user's ask): a PDF in the working directory — <c>/pdf &lt;file|https://url&gt; [to=&lt;out.pdf&gt;]
    /// [paper=letter|a4|legal] [landscape] [overwrite]</c>, <c>/pdf reply</c> for the last reply (<see cref="Pdf.PdfCommand"/>).
    /// The user's own hand: <c>convert_to_pdf</c> without its <c>File tools</c> and <c>Web tools</c> switches.
    /// </summary>
    Pdf,

    /// <summary><c>/echo &lt;text&gt;</c>: the line printed as a reply and read aloud when speech is on — <c>/speak</c>'s block and voice over typed text, never resumed (2026-09-17).</summary>
    Echo,

    /// <summary><c>/queue</c>: the messages queued while a reply runs, on a pane where Enter removes one and a button drops them all (2026-09-18, behind <c>Queue messages</c>); <c>/queue clear</c> (2026-09-21) drops them all without the pane.</summary>
    Queue,

    /// <summary><c>/sessions</c>: this profile's stored sessions on a pane (restore, rename, purge), or <c>/sessions &lt;id&gt; | purge &lt;id&gt; | purge older &lt;age&gt; | purge all | title &lt;text&gt;</c> typed (2026-09-18; <c>/session</c> until later on 2026-09-21, the user's call — the singular reads as an unknown command now).</summary>
    Session,

    /// <summary>
    /// <c>/rename [name]</c> (2026-10-05, the user's ask): the session on screen renamed from the draft line — a front door onto
    /// <c>/sessions title</c>, the name the title (waits for a reply as that does) and the bare word the rename box (a pane under
    /// a reply as that is). Ctrl+Alt+R sends the bare word.
    /// </summary>
    Rename,

    /// <summary>
    /// <c>/claude &lt;message&gt;</c> (2026-09-27, the user's ask): the message sent to Claude Code, run headless as a child
    /// (<see cref="Claude.ClaudeProcess"/>), the reply streamed into the transcript under Claude's name, spoken when speech
    /// is on, and the pair added to the local model's history, tagged. One Claude conversation per session, resumed by the id
    /// the session stores; <c>/claude new</c> starts another. Refused mid-turn.
    /// </summary>
    Claude,

    /// <summary>
    /// <c>/test</c> (2026-09-28, the user's ask: LLMTester's tests run from here): <c>/test &lt;id | reasoning | structured |
    /// long | all&gt;</c> runs them against the connected model (<see cref="Bench.BenchRunner"/>), a line per test and a
    /// table at the end, saved in the profile's <c>tests.json</c>; <c>/test history</c> lists the saved runs, and a bare
    /// <c>/test</c> the tests with their last verdicts. Never in the conversation. ESC stops a run.
    /// </summary>
    Test,

    /// <summary><c>/exit</c>: leave the app (<c>/quit</c> until 2026-09-17, the user's call; the old word is unknown now, as the aliases are).</summary>
    Exit,
    /// <summary>A command we know, given an argument it does not take (<c>/about me</c>): <c>Args</c> carries it. Since 2026-09-17 its own case, so the error names the command rather than calling it unknown.</summary>
    Overloaded,
    /// <summary>Started with <c>/</c> but is not a command we know.</summary>
    Unknown,
}

/// <summary>
/// The slash-command classifier. A line is a command only when it starts with <c>/</c>, and the
/// first token must match exactly: <c>/exit the program please</c> is not <c>/exit</c>, and
/// <c>what does /clear do?</c> is a question for the model. <c>//</c> is <c>/settings</c>, the one alias (<c>///</c> for <c>/tools</c> and <c>////</c> for <c>/skills</c> came and went on 2026-09-21, the user's ask both times); every other one (<c>/?</c>, <c>/cls</c>, <c>/exit</c>, <c>/srv</c>, <c>/prof</c> …) went on 2026-09-16 with the argument completion, the user's call, and reads as an unknown command now (<c>/config</c> had gone the same day). <c>/new</c> is its own command (a new conversation, the screen kept) since 2026-09-16; <c>/splash</c> (a new conversation, the screen wiped and the welcome splash shown) since 2026-09-19. Only <c>/server</c>, <c>/model</c>, <c>/reasoning</c>, <c>/theme</c> (2026-09-23), <c>/tts</c>,
/// <c>/stt</c>, <c>/wake</c>, <c>/interrupt</c>, <c>/speak</c>, <c>/echo</c>, <c>/view</c>, <c>/learn</c>, <c>/remember</c>, <c>/memory</c> (2026-09-22, the user's ask, twice: <c>forget</c>, the standalone <c>/forget</c> folded in, then <c>copy &lt;profile&gt; [overwrite]</c>, the standalone <c>/memcopy</c> folded in the same way — <c>/forget</c> and <c>/memcopy</c> are unknown commands now), <c>/cmdcopy</c> (2026-09-21), <c>/keycopy</c> (2026-09-28), <c>/profile</c>, <c>/timer</c>, <c>/cwd</c>, <c>/tree</c>, <c>/explore</c>, <c>/copy</c>, <c>/compact</c>, <c>/gituser</c>, <c>/loop</c>, <c>/skills</c> (2026-09-21), <c>/test</c> (2026-09-28) and (since 2026-09-16, <c>reset</c>; since 2026-09-21, <c>copy &lt;profile&gt; [force]</c>) <c>/persona</c>, <c>/operata</c>, <c>/vocalia</c> take an argument (<see cref="TakesArgument"/>); any other command given one is <see cref="SlashCommand.Overloaded"/>, so <c>/about me</c> is told the command takes nothing rather than called unknown (2026-09-17). <c>/draft</c> takes nothing (2026-09-19: the editor is the argument); nor does <c>/cmdlist</c> (later on 2026-09-21: the allowed-commands list on a pane, the toolbar lock glyph's word). <c>/skills</c> opens the Skills pane, or <c>/skills edit &lt;name&gt;</c> the skill's file (2026-09-21; it took nothing before) (the plural since 2026-09-19, beside <c>/tools</c>; a bare <c>/skill</c> from later on 2026-09-18, in place of <c>/skill list</c>; <c>/skills</c> before that; <c>/skill &lt;name&gt; [message]</c> took a name until later that day; <c>/skill</c> is unknown now). The three tool switches <c>/web</c>, <c>/files</c>, <c>/ask</c> went later on 2026-09-18 (the user's call: the settings rows <c>Web tools</c>, <c>File tools</c>, <c>Ask user</c> are the one place now) and read as unknown commands.
/// </summary>
public static class SlashCommands
{
    /// <summary>
    /// One command and what it does: a row of the Commands tab of the info pane, and a line of <see cref="HelpText"/>.
    /// An alias goes in the first column with the command (<see cref="Label"/>), never in the summary — <c>//</c> was the one left (2026-09-16; <c>///</c> and <c>////</c> came and went on 2026-09-21).
    /// </summary>
    public sealed record HelpEntry(string Command, string Summary, params string[] Aliases)
    {
        /// <summary>The first column: the command and its aliases, comma-separated (<c>/settings, //</c>). Pinned by tests.</summary>
        public string Label => Aliases.Length == 0 ? Command : Command + ", " + string.Join(", ", Aliases);
    }

    /// <summary>
    /// Every command with its summary, as <c>/help</c> lists them: one list, A to Z by <see cref="HelpEntry.Command"/>
    /// (ordinal), no groups and no blank rows (2026-09-27, the user's call: the Commands tab had grown cramped, and every new
    /// command was a decision about which group and where in it). From 2026-09-15 until then it was the user's hand-ordered
    /// groups, nine at the end, a blank row between them; the history of each row's place is in git. The literal is written
    /// in order for the reader, and sorted again here so a row added anywhere lands in its place. Pinned by tests.
    /// </summary>
    public static readonly IReadOnlyList<HelpEntry> HelpEntries =
        new HelpEntry[]
        {
            new("/about", "show info about the app and profile"),
            new("/botchat", "let the profiles talk to each other"),
            new("/claude", "ask Claude Code from the chat"),
            new(Camera.CameraText.Word, Camera.CameraText.HelpSummary),
            new(NeonSidekick.Screen.ScreenText.Word, NeonSidekick.Screen.ScreenText.HelpSummary),
            new("/clear", "start over and clear the screen"),
            new(RewindText.Word, RewindText.HelpSummary),
            new("/cmdclear", "clear this profile's command history"),
            new("/cmdcopy", "copy allowed commands to a profile"),
            new("/cmdlist", "list and remove allowed shell commands"),
            new("/collapse", "fold every item in the transcript"),
            new("/comfy", "manage ComfyUI and its workflows"),
            new("/compact", "shrink the conversation's context"),
            new("/copy", "copy replies to the clipboard"),
            new("/cwd", "show or change the working directory"),
            new("/docker", "manage Docker Desktop's containers"),
            new("/draft", "write the next message in your editor"),
            new("/echo", "print a line as a reply"),
            new("/exit", "exit the application"),
            new("/expand", "unfold every item in the transcript"),
            new("/explore", "open a folder in your file browser"),
            new("/gituser", "set the repository's git user"),
            new("/ha", "control Home Assistant"),
            new("/header", HeaderToggle.HelpSummary),
            new("/help", "show the commands and keys"),
            new("/imagine", "make a picture on ComfyUI"),
            new("/interrupt", "toggle the wake-word interrupt"),
            new("/keycheck", "check which key chords are held"),
            new("/keycopy", "copy this profile's API keys"),
            new("/learn", "write or improve a skill"),
            new("/log", "show the diagnostic log"),
            new("/loop", "repeat a message or command"),
            new("/mcp", "manage MCP servers and their tools"),
            new("/memory", "manage the model's memory"),
            new("/model", "pick or set the model"),
            new("/new", "start over, keeping the screen"),
            new("/operata", "edit the operating rules"),
            new("/perfbar", "show or hide the performance bar"),
            new("/persona", "edit the personality"),
            new("/plan", "plan before doing"),
            new("/police", "open the shell police switch"),
            new("/pdf", "make a PDF of a file, page or reply"),
            new(Viewer.ProcessWindowText.Word, "watch the model's background processes"),
            new("/print", "print a file or the last reply"),
            new("/profile", "switch or manage profiles"),
            new("/queue", "manage the queued messages"),
            new("/reasoning", "pick or set the reasoning effort"),
            new("/remember", "add a memory"),
            new("/rename", "rename the current session"),
            new("/sampling", "tune the model's sampling"),
            new("/server", "pick or set the LLM server"),
            new("/sessions", "restore, rename or purge sessions"),
            new("/settings", "edit, search and save settings", "//"),
            new("/skills", "manage and install skills"),
            new("/speak", "read a text file aloud"),
            new("/splash", "start over with the splash screen"),
            new("/srvcopy", "copy this profile's server settings"),
            new("/stt", "toggle voice input"),
            new("/sys", "show the system prompt and tools"),
            new("/terminal", "open a terminal in a folder"),
            new("/test", "benchmark the connected model"),
            new("/theme", "switch or export the colour theme"),
            new("/timer", "set, list and stop timers"),
            new("/toolbar", ToolbarItems.HelpSummary),
            new("/tools", "switch the model's tools on or off"),
            new("/tree", "show the working directory's tree"),
            new("/tts", "toggle speech output"),
            new("/usage", "show token usage and statistics"),
            new("/vault", "show the Obsidian vault's tree"),
            new("/view", "view images or a folder of them"),
            new("/vocalia", "edit the spoken-reply directive"),
            new("/wake", "toggle the wake word"),
            new("/window", "show the terminal window's size"),
            new(YouTube.YouTubeCommand.Word, YouTube.YouTubeText.HelpSummary),
        }.OrderBy(entry => entry.Command, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The commands on <c>/help</c>'s <see cref="BasicTabTitle"/> tab (2026-09-27, the user's list): the everyday ones, so the
    /// first tab is a short list (it fit without scrolling until 2026-10-03, when <c>/about</c>, <c>/explore</c>, <c>/perfbar</c>,
    /// <c>/stt</c>, <c>/toolbar</c>, <c>/tts</c> and <c>/wake</c> came over from the advanced tab, the user's pick). Every other command (<c>/log</c> among them) is on
    /// <see cref="AdvancedTabTitle"/>. Only the pane is split: <see cref="HelpText"/>, <see cref="Completions"/> and
    /// <see cref="LabelWidth"/> stay the one A-to-Z list. Pinned.
    /// </summary>
    public static readonly IReadOnlySet<string> BasicCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "/about", "/clear", "/compact", "/copy", "/cwd", "/draft", "/exit", "/explore", "/help", "/memory", "/model", "/new", "/perfbar", "/profile",
        "/queue", "/reasoning", "/remember", "/rename", "/rewind", "/server", "/sessions", "/settings", "/skills", "/stt", "/sys", "/terminal", "/toolbar", "/tools", "/tree", "/tts",
        "/wake",
    };

    /// <summary>Whether <paramref name="entry"/> is on the <see cref="BasicTabTitle"/> tab (<see cref="BasicCommands"/>).</summary>
    public static bool IsBasic(HelpEntry entry) => BasicCommands.Contains(entry.Command);

    /// <summary>The title of <c>/help</c>'s first tab, <see cref="BasicCommands"/> (2026-09-27; one <c>Commands</c> tab until then; <c>Commands (basic)</c> until 2026-10-05, the user's call). Pinned.</summary>
    public const string BasicTabTitle = "Basic";

    /// <summary>The title of <c>/help</c>'s second tab, every command not in <see cref="BasicCommands"/> (2026-09-27; <c>Commands (advanced)</c> until 2026-10-05). Pinned.</summary>
    public const string AdvancedTabTitle = "Advanced";

    /// <summary>
    /// The input line's command list (<see cref="UI.MentionCompleter.TryFindCommand"/>): every base
    /// command with its summary as the note, sorted by name — never an alias (the user's call,
    /// 2026-09-16). Pinned by tests.
    /// </summary>
    public static readonly IReadOnlyList<UI.CompletionItem> Completions =
        HelpEntries.Select(entry => new UI.CompletionItem(entry.Command, entry.Summary)).OrderBy(item => item.Text, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// <see cref="Completions"/> less <c>/exit</c>: the list under the setting <c>Hide /exit autocomplete</c>
    /// (2026-09-18, on by default), so a pick never ends the app by mistake; typed in full it exits as
    /// ever, and <c>/help</c> keeps the row. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<UI.CompletionItem> CompletionsWithoutExit =
        Completions.Where(item => item.Text != "/exit").ToArray();

    /// <summary>The blank cells between the label column and the summary, on the pane and in <see cref="HelpText"/> alike.</summary>
    public const int HelpColumnGap = 2;

    /// <summary>
    /// The label column's width: the longest <see cref="HelpEntry.Label"/>, measured — never a literal — so a new
    /// command or alias re-fits the column. <see cref="HelpText"/> pads every label to it plus <see cref="HelpColumnGap"/>;
    /// the pane's grid measures its own first column the same way.
    /// </summary>
    public static readonly int LabelWidth = HelpEntries.Max(entry => entry.Label.Length);

    /// <summary>
    /// The description column's width (2026-10-05, the forms in a third column of their own): the longest
    /// <see cref="HelpEntry.Summary"/>, measured as <see cref="LabelWidth"/> is, so the forms start in one column on both tabs.
    /// </summary>
    public static readonly int DescriptionWidth = HelpEntries.Max(entry => entry.Summary.Length);

    /// <summary>The key line of <see cref="HelpText"/>. Pinned by tests.</summary>
    public const string KeysLine = "Keys: Enter = send   ESC = stop the speech / clear the line / cancel the reply   Up/Down = history, or the draft's rows when it wraps   push-to-talk key (F4 unless changed) = talk";

    /// <summary>
    /// Printed by <c>/help</c> when there is no pane to open (a redirected console): the Commands tabs' three columns in plain
    /// text, A to Z, no blank lines (the groups with a blank line between them until 2026-09-27) — the label, the description, the
    /// first form (<see cref="Help.HelpSyntax.Forms"/>), and each further form on a line of its own under it (2026-10-05). Pinned by tests.
    /// </summary>
    public static readonly string HelpText = BuildHelpText(HelpEntries);

    private static string BuildHelpText(IReadOnlyList<HelpEntry> entries)
    {
        var text = new System.Text.StringBuilder("Commands:\n");
        string under = new(' ', 2 + LabelWidth + HelpColumnGap + DescriptionWidth + HelpColumnGap);
        foreach (var entry in entries)
        {
            var forms = Help.HelpSyntax.Forms(entry.Command);
            text.Append("  ").Append(entry.Label.PadRight(LabelWidth + HelpColumnGap));
            if (forms.Count == 0)
            {
                text.Append(entry.Summary).Append('\n');
                continue;
            }

            text.Append(entry.Summary.PadRight(DescriptionWidth + HelpColumnGap)).Append(forms[0]).Append('\n');
            foreach (string form in forms.Skip(1))
            {
                text.Append(under).Append(form).Append('\n');
            }
        }

        return text.Append(KeysLine).ToString();
    }

    /// <summary>Every command word, for help and completion.</summary>
    public static readonly string[] Words = { "/help", "/clear", "/new", "/splash", "/rewind", "/theme", "/queue", "/sessions", "/rename", "/compact", "/server", "/model", "/reasoning", "/sampling", "/settings", "//", "/tools", "/mcp", "/tts", "/stt", "/wake", "/interrupt", "/speak", "/remember", "/memory", "/cmdcopy", "/keycheck", "/keycopy", "/srvcopy", "/cmdclear", "/cmdlist", "/police", "/persona", "/operata", "/vocalia", "/sys", "/usage", "/perfbar", "/toolbar", "/header", "/profile", "/timer", "/cwd", "/tree", "/vault", "/explore", "/terminal", "/log", "/process", "/view", "/imagine", "/comfy", "/ha", "/docker", "/camera", "/screen", "/youtube", "/print", "/pdf", "/echo", "/gituser", "/copy", "/draft", "/loop", "/plan", "/botchat", "/claude", "/test", "/expand", "/collapse", "/window", "/skills", "/learn", "/about", "/exit" };

    /// <summary>The <c>/queue</c> word: what a double-click on the hint row's queued part sends through the mid-turn line hook, so the pane opens exactly as the typed command's does (2026-09-18). Pinned.</summary>
    public const string QueueWord = "/queue";

    /// <summary>The <c>/usage</c> word: what a double-click on the busy row's spinner and label sends through the mid-turn line hook (2026-09-21), the Usage pane under the reply as the typed command's. Pinned.</summary>
    public const string UsageWord = "/usage";

    /// <summary>The words the toolbar's pane glyphs send (2026-09-21) — through the mid-turn line hook under a reply, through the screen's dispatch at idle — so each pane opens exactly as the typed command's does. Pinned.</summary>
    public const string SettingsWord = "/settings";
    public const string SkillsWord = "/skills";
    public const string ToolsWord = "/tools";
    public const string McpWord = "/mcp";
    public const string SysWord = "/sys";
    public const string SessionsWord = "/sessions";   // later on 2026-09-21, the sixth glyph
    public const string RenameWord = "/rename";       // 2026-10-05, what Ctrl+Alt+R sends: the rename box
    public const string CmdListWord = "/cmdlist";     // later still on 2026-09-21, the seventh: the lock, whichever way the policy turns it
    public const string PoliceWord = "/police";       // 2026-09-22, the officer last of all, while Shell police is on
    public const string MemoryWord = "/memory";       // 2026-09-22, the disk between the balloon and the lock, while Memory is on
    public const string ProfileWord = "/profile";     // later on 2026-09-29, the ID card after the gear: the profile picker
    public const string ThemeWord = "/theme";         // 2026-10-04, the abacus after the ID card: the theme picker
    public const string PerfWord = "/perfbar";           // later on 2026-09-29, the rising chart after the Usage chart: the performance bar shown or hidden

    /// <summary>The words the hint row's model name and reasoning mark send through the screen's dispatch at idle (later on 2026-09-21, so a double-click off the pane they open can switch panes); the name is <c>/server</c> since 2026-09-22 (the user's call: the click walks server, model, then reasoning, as the typed command does). Pinned.</summary>
    public const string ServerWord = "/server";
    public const string ReasoningWord = "/reasoning";

    /// <summary>
    /// Classifies <paramref name="line"/>; <c>Args</c> is the trimmed remainder — meaningful for the commands <see cref="TakesArgument"/> names, and carried by <see cref="SlashCommand.Overloaded"/> for the error line.
    /// <c>/log</c> was a command only under <c>--log</c> from 2026-09-22 until 2026-10-02, when it took to the log window.
    /// </summary>
    public static (SlashCommand Command, string Args) Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string trimmed = line.Trim();
        if (!trimmed.StartsWith('/'))
        {
            return (SlashCommand.None, "");
        }

        int split = trimmed.IndexOfAny(new[] { ' ', '\t' });
        string token = split < 0 ? trimmed : trimmed[..split];
        string args = split < 0 ? "" : trimmed[(split + 1)..].Trim();

        var command = token.ToLowerInvariant() switch
        {
            "/help" => SlashCommand.Help,
            "/clear" => SlashCommand.Clear,
            "/new" => SlashCommand.New,
            "/splash" => SlashCommand.Splash,
            "/rewind" => SlashCommand.Rewind,
            "/theme" => SlashCommand.Theme,
            "/compact" => SlashCommand.Compact,
            "/server" => SlashCommand.Server,
            "/model" => SlashCommand.Model,
            "/reasoning" => SlashCommand.Reasoning,
            "/sampling" => SlashCommand.Sampling,
            "/settings" or "//" => SlashCommand.Settings,
            "/tools" => SlashCommand.Tools,
            "/mcp" => SlashCommand.Mcp,
            "/tts" => SlashCommand.Tts,
            "/stt" => SlashCommand.Voice,
            "/wake" => SlashCommand.Wake,
            "/interrupt" => SlashCommand.Interrupt,
            "/speak" => SlashCommand.Speak,
            "/remember" => SlashCommand.Remember,
            "/memory" => SlashCommand.Memory,
            "/cmdcopy" => SlashCommand.CmdCopy,
            "/keycheck" => SlashCommand.KeyCheck,
            "/keycopy" => SlashCommand.KeyCopy,
            "/srvcopy" => SlashCommand.SrvCopy,
            "/cmdclear" => SlashCommand.CmdClear,
            "/cmdlist" => SlashCommand.CmdList,
            "/police" => SlashCommand.Police,
            "/persona" => SlashCommand.Persona,
            "/operata" => SlashCommand.Operata,
            "/vocalia" => SlashCommand.Vocalia,
            "/sys" => SlashCommand.Sys,
            "/usage" => SlashCommand.Usage,
            "/perfbar" => SlashCommand.Perf,
            "/toolbar" => SlashCommand.Toolbar,
            "/header" => SlashCommand.Header,
            "/profile" => SlashCommand.Profile,
            "/timer" => SlashCommand.Timer,
            "/cwd" => SlashCommand.Cwd,
            "/tree" => SlashCommand.Tree,
            "/vault" => SlashCommand.Vault,
            "/explore" => SlashCommand.Explore,
            "/terminal" => SlashCommand.Terminal,
            "/view" => SlashCommand.View,
            "/imagine" => SlashCommand.Imagine,
            "/comfy" => SlashCommand.Comfy,
            "/ha" => SlashCommand.HomeAssistant,
            "/docker" => SlashCommand.Docker,
            "/camera" => SlashCommand.Camera,
            "/screen" => SlashCommand.Screen,
            "/youtube" => SlashCommand.YouTube,
            "/print" => SlashCommand.Print,
            "/pdf" => SlashCommand.Pdf,
            "/echo" => SlashCommand.Echo,
            "/queue" => SlashCommand.Queue,
            "/sessions" => SlashCommand.Session,
            "/rename" => SlashCommand.Rename,
            "/copy" => SlashCommand.Copy,
            "/draft" => SlashCommand.Draft,
            "/loop" => SlashCommand.Loop,
            "/plan" => SlashCommand.Plan,
            "/botchat" => SlashCommand.BotChat,
            "/claude" => SlashCommand.Claude,
            "/test" => SlashCommand.Test,
            "/expand" => SlashCommand.Expand,
            "/collapse" => SlashCommand.Collapse,
            "/gituser" => SlashCommand.GitUser,
            "/window" => SlashCommand.Window,
            "/log" => SlashCommand.Log,
            "/process" => SlashCommand.Process,
            "/about" => SlashCommand.About,
            "/skills" => SlashCommand.Skills,
            "/learn" => SlashCommand.Learn,
            "/exit" => SlashCommand.Exit,
            _ => SlashCommand.Unknown,
        };

        if (command is not SlashCommand.Unknown && !TakesArgument(command) && args.Length > 0)
        {
            command = SlashCommand.Overloaded;
        }

        return (command, args);
    }

    /// <summary>
    /// Whether the command reads what follows it. The one list (pinned): a command not here given an
    /// argument parses as <see cref="SlashCommand.Overloaded"/>. <see cref="SlashCommand.None"/>,
    /// <see cref="SlashCommand.Unknown"/> and <see cref="SlashCommand.Overloaded"/> take nothing.
    /// </summary>
    public static bool TakesArgument(SlashCommand command) => command is
        SlashCommand.Compact or SlashCommand.Server or SlashCommand.Model or SlashCommand.Reasoning or SlashCommand.Sampling or SlashCommand.Theme
        or SlashCommand.Tts or SlashCommand.Voice or SlashCommand.Wake or SlashCommand.Interrupt or SlashCommand.Speak or SlashCommand.View or SlashCommand.Imagine or SlashCommand.Comfy or SlashCommand.Echo
        or SlashCommand.Learn
        or SlashCommand.Persona or SlashCommand.Operata or SlashCommand.Vocalia
        or SlashCommand.Remember or SlashCommand.Memory or SlashCommand.CmdCopy or SlashCommand.KeyCopy or SlashCommand.SrvCopy or SlashCommand.Profile or SlashCommand.Timer
        or SlashCommand.Cwd or SlashCommand.Tree or SlashCommand.Vault or SlashCommand.Explore or SlashCommand.Terminal or SlashCommand.Copy or SlashCommand.Session or SlashCommand.Rename or SlashCommand.GitUser
        or SlashCommand.Loop or SlashCommand.Plan or SlashCommand.BotChat or SlashCommand.Claude or SlashCommand.Queue or SlashCommand.Skills or SlashCommand.Test
        or SlashCommand.HomeAssistant or SlashCommand.Docker or SlashCommand.Camera or SlashCommand.Screen or SlashCommand.YouTube or SlashCommand.Print or SlashCommand.Pdf or SlashCommand.Perf or SlashCommand.Toolbar or SlashCommand.Header or SlashCommand.Rewind
        or SlashCommand.Log or SlashCommand.Process or SlashCommand.Tools or SlashCommand.Settings;
}
