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

    /// <summary><c>/splash</c>: forget the conversation, wipe the screen and show the welcome splash again — the startup view, whatever <c>Welcome splash</c> says (2026-09-19, the user's ask): the robot when it says <c>robot</c> (2026-10-20; the tiled pages of <c>tiled</c> until then), else one picture, <c>off</c> included (2026-09-24). No argument until 2026-10-18 (the user's ask): <c>view</c>, <c>thumbs</c> and <c>purge</c> open or empty the profile's <c>splash_images</c> folder (<c>UI.SplashCommand</c>); another word is the usage error, nothing wiped.</summary>
    Splash,

    /// <summary>
    /// <c>/rewind</c> (2026-09-30, the user's ask): go back to an earlier message. It and everything after it leave the
    /// conversation and the session, and its text returns to the input. <c>/rewind &lt;n&gt;</c> starts the picker n messages
    /// back. A double ESC on an empty line opens it too.
    /// </summary>
    Rewind,
    /// <summary><c>/theme</c>: pick the colour theme from a list, or <c>/theme &lt;name&gt;</c> (2026-09-23, the user's ask); a change starts over the way <c>/splash</c> does — the conversation forgotten, the screen wiped, the splash shown in the new colours. Refused while a reply runs.</summary>
    Theme,

    /// <summary>
    /// <c>/mark &lt;color&gt;|default</c> (2026-10-18, the user's ask): the rules around the draft line in one of ten colors, kept per session, so a
    /// window is told apart at a glance; bare, the mark in force and the choices. The PC's alone; quick under a reply.
    /// </summary>
    Mark,

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

    /// <summary><c>/tools</c>: the Tools pane (2026-09-19, the user's ask) — every tool the model can be offered, on or off one by one (the Offered tab), then its Options tab (the <c>$</c>-mention switch, later that day) and the Ask, Files and Web settings rows that sat on <c>/settings</c> until then. Bare like <see cref="Skills"/> until 2026-10-03, when <c>/tools &lt;group&gt;</c> came with the toolbar's tool switches (the user's ask): a group's switch opened straight (<see cref="ToolsText.SwitchWords"/>); <c>/tools shell allowed|police</c> since 2026-10-09 (<c>/cmdlist</c> and <c>/police</c> folded in, the user's call). <c>///</c> is its alias (2026-09-21, the user's ask, beside <c>//</c>).</summary>
    Tools,

    /// <summary><c>/mcp</c>: the MCP pane (2026-09-20, the user's ask) — the servers the two <c>mcp.json</c> files name, on or off one by one and connected or not (the Servers tab), their tools on or off by their prefixed names (the Tools tab), then its Options tab (the master switch and the connect timeout). No argument: bare like <see cref="Tools"/>.</summary>
    Mcp,
    Tts,

    /// <summary><c>/stt [on|off]</c>: voice input; <c>/stt wake [on|off]</c> the wake word and <c>/stt interrupt [on|off]</c> its interrupt since 2026-10-09 (the user's call: <c>/wake</c> and <c>/interrupt</c>, their own commands until then, folded in; unknown commands now).</summary>
    Voice,

    /// <summary><c>/remember &lt;text&gt;</c>: keep one fact across sessions.</summary>
    Remember,

    /// <summary><c>/memory</c>: list what is remembered, Enter removing one; <c>/memory on|off</c> switches Memory (2026-10-03, the user's ask); <c>/memory forget</c> erases every one, after a confirmation (2026-09-22, the user's ask, twice: the wipe was <c>/forget</c>, its own command, until that morning, and <c>/memory copy &lt;profile&gt; [overwrite]</c> was <c>/memcopy</c> (2026-09-17) until later that day; both words are unknown commands now, and the copy itself went on 2026-10-13, <c>/profile push|pull memory</c> since).</summary>
    Memory,

    /// <summary><c>/msgclear</c> (<c>/cmdclear</c> from 2026-09-25, the user's ask, until 2026-10-10, the user's rename; the old word is an unknown command now): this profile's message history — the input line's Up/Down recall, stored in <c>sessions.db</c> under <c>Keep message history</c> — emptied, stored and in memory, after a confirmation. No argument.</summary>
    MsgClear,

    /// <summary>
    /// <c>/cmd [--shell &lt;shell&gt;] &lt;command&gt;</c> (2026-10-09, the user's ask): a shell line the user types, run in the working
    /// directory and drawn live, through what <c>run_command</c>'s line passes — the policy, the police, the allow list and the approval
    /// pane — then into the history as a tagged pair, so the model sees it. Waits for a reply's end.
    /// </summary>
    Cmd,

    /// <summary>
    /// <c>/sys</c>: the system prompt the next turn sends and the tools it offers, in the info pane. Named <c>/sysprompt</c> until
    /// 2026-09-21, when the retired short alias became the one word. Since 2026-10-09 (the user's ask) <c>/sys persona|operata|vocalia
    /// [reset]</c> edits or resets the three files the prompt is made from (and <c>copy &lt;profile&gt; [force]</c> copied them until 2026-10-10,
    /// <c>/profile push|pull persona|operata|vocalia &lt;name&gt; [overwrite]</c> since) — <c>/persona</c>,
    /// <c>/operata</c> and <c>/vocalia</c> until then (2026-09-16), unknown commands now (<see cref="ChatScreen.SysFileWords"/>).
    /// </summary>
    Sys,

    /// <summary><c>/usage</c>: the tokens used and the speed — the last reply, this conversation, since launch.</summary>
    Usage,

    /// <summary>
    /// <c>/perfbar [off|text|gauge|led]</c> (2026-09-29, the user's ask): bare, the performance bar hidden, or shown again
    /// with the meters it last had (<see cref="Settings.AppSettingsData.PerformanceBarLastItems"/>; CPU, RAM, GPU and VRAM the
    /// first time, 2026-09-30); <c>off</c> hides it; a look sets it and shows the bar. Display only, so it runs at once under a
    /// reply; Ctrl+F's word until 2026-10-18 (the toolbar's 📈 too until 2026-10-14), typed alone since.
    /// </summary>
    Perf,

    /// <summary>
    /// <c>/homebar [on|off|edit]</c> (2026-10-12, the user's ask: a bar of Home Assistant rooms and devices under the performance
    /// bar): bare, the home bar hidden, or shown again with the items it last had (the picker opens when it never had any);
    /// <c>on</c>/<c>off</c> say which; <c>edit</c> opens the picker. <c>tap &lt;item&gt;</c> and <c>show &lt;item&gt;</c> are the bar's
    /// own clicks (a confirm, a chip's pane), the lines a click makes. <c>name &lt;item&gt; = &lt;alias&gt;</c> labels an item's chip. Ctrl+W (Ctrl+Alt+W until later that day) until 2026-10-18, no chord since.
    /// </summary>
    HomeBar,

    /// <summary>
    /// <c>/toolbar [on|off]</c> (later on 2026-09-30, the user's ask: "same as how /perf works for the perfbar"): bare, the toolbar
    /// hidden while it shows — its items kept in <see cref="Settings.AppSettingsData.ToolbarLastItems"/> — else shown again with
    /// them (<see cref="ToolbarItems.Defaults"/> the first time); <c>on</c> and <c>off</c> say which. Display only, so it runs at
    /// once under a reply; Ctrl+T (Ctrl+Alt+B until later still on 2026-10-01) until 2026-10-18, no chord since.
    /// </summary>
    Toolbar,

    /// <summary>
    /// <c>/header [on|off]</c> (2026-10-01, the user's ask, with Ctrl+Alt+H until 2026-10-18): <c>Show header</c> flipped, or set by the word, and
    /// saved; the banner comes or goes at the next wipe, nothing redrawn now (<see cref="HeaderToggle"/>). A setting alone, so it
    /// runs at once under a reply, and a pane stays open over it.
    /// </summary>
    Header,

    /// <summary><c>/profile</c>: pick, switch to, add, delete, rename or reset a profile; since 2026-09-21 <c>edit</c> opens its <c>profile.json</c> and <c>reload</c> reads it back; since 2026-09-28 <c>push</c> and <c>pull</c> copy the settings between profiles, and since 2026-10-09 one section of them (<c>keys</c>, <c>servers</c>, <c>commands</c>, <c>history</c>, <c>policestrings</c>: the user's ask, <c>/keycopy</c>, <c>/srvcopy</c> and <c>/cmdcopy</c> folded in, each gaining a pull; <c>prompts</c> on 2026-10-13, the saved prompts).</summary>
    Profile,

    /// <summary><c>/timer</c>: list the timers, start one or a stopwatch (2026-10-18), stop one or all.</summary>
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
    /// <c>/open &lt;file&gt;</c> (2026-10-09, the user's ask: "similar to /view but limits the choices to files"): a file under the
    /// working directory opened in its default app, as the model's <c>open</c> tool opens one; a folder is refused (<c>/explore</c> opens those).
    /// </summary>
    Open,

    /// <summary>
    /// <c>/browse [&lt;folder&gt;]</c> (2026-10-09, the user's ask: "similar to /tree", the pane like <c>/cwd browse</c>'s): the working
    /// directory's folders and files as a tree on the pane, from its root or a folder under it; Enter on a file opens it in its default
    /// app and the pane stays.
    /// </summary>
    Browse,

    /// <summary>
    /// <c>/browser [url]</c> (2026-10-13, the user's ask, with the bot checks): the Browser window (WebView2) on a page, or the blank
    /// page, for the user to browse; its cookies go to the web tools when they close it (<c>ChatScreen.HandleBrowser</c>). <c>/browser clear
    /// history|all</c> (later on 2026-10-13) clears what the window keeps for the loaded profile. The saved links (later still,
    /// <c>BrowserCommand</c>): <c>save [url [name]]</c>, <c>list [--clear]</c>, <c>unsave &lt;n|url&gt;</c> and <c>/browser &lt;n&gt;</c>; they
    /// need no window, so they work headless and off Windows. The window itself is Windows only, never headless (and a Mac's on macOS 14
    /// or later, WebKit, since 2026-10-18, mac-pass2).
    /// </summary>
    Browser,

    /// <summary>
    /// <c>/prompts</c> (2026-10-13, the user's ask: messages sent often, kept per profile and put on the draft line): the saved prompts'
    /// pane, <c>/prompts &lt;n&gt;</c> (that prompt on the draft), <c>save &lt;text&gt;</c>, <c>remove &lt;n&gt;</c> and <c>list [--clear]</c>
    /// (<c>PromptsCommand</c>). The model has no tool for them. Headless, the list and a prompt are printed.
    /// </summary>
    Prompts,

    /// <summary>
    /// <c>/terminal</c> (2026-10-03, the user's ask: "similar to /explore"): a new Windows Terminal window in the working
    /// directory, or <c>/terminal &lt;folder&gt;</c> in a folder under it (<see cref="Llm.PersonaFile.OpenTerminal"/>). Ctrl+. runs it.
    /// </summary>
    Terminal,

    /// <summary>
    /// <c>/shortcut [profile] [--log]</c> (2026-10-07, the user's ask): a desktop shortcut that starts this exe on the loaded profile,
    /// or the one named, with <c>--log</c> a stamped log beside the exe too (<see cref="Shortcuts.DesktopShortcut"/>). Windows, and a Mac
    /// since 2026-10-08 (a <c>.command</c> file).
    /// </summary>
    Shortcut,

    /// <summary><c>/copy</c>: copy the last exchange to the clipboard as markdown, or <c>/copy &lt;n&gt;</c> | <c>all</c>.</summary>
    Copy,

    /// <summary><c>/draft</c>: a temporary text file in your editor; saved and closed, its text is the next message — sent through the input line as a paste, so a long one lands as a token — and the file goes; blank, or closed unsaved, nothing is sent (2026-09-19). No argument.</summary>
    Draft,

    /// <summary><c>/loop &lt;count&gt; [delay] &lt;message&gt;</c> | <c>/loop infinite [delay] &lt;message&gt;</c>: the message sent that many times, or until ESC or Ctrl+C, each reply waited for as if typed again (2026-09-21, the user's ask), and with a delay (<c>30s</c>, <c>5m</c>; 2026-09-24) that long a gap after each reply. Refused mid-turn like <see cref="Draft"/>: it sends what the running turn cannot take.</summary>
    Loop,

    /// <summary><c>/botchat [profile …] [topic]</c> (2026-09-24, the user's ask): the profiles talk to each other — the named ones, or every profile when none is named — each reply in the speaker's own persona and voice, on this profile's LLM server and model (or, under <c>Botchat LLM mode</c> <c>multi</c>, each on its own profile's), one turn after another, a random speaker each time but never the same one twice running, until ESC or Ctrl+C. The tools are the Botchat tab's (none by default). A line typed meanwhile joins the chat as the user's; the chat is saved as a session. Refused mid-turn like <see cref="Loop"/>.</summary>
    BotChat,

    /// <summary><c>/plan &lt;requirement&gt;</c> (2026-09-26, the user's ask): plan mode — the model researches with read-only tools, asks what it needs and presents a plan saved as <c>.neon/plans/&lt;name&gt;.md</c>; nothing changes until the user approves it on the pane or with <c>/plan approve [--fresh]</c>. While planning, <c>/plan</c> or <c>/plan show</c> says where it stands, <c>/plan cancel</c> leaves, anything else is more detail. Outside it, <c>/plan ralph [name] [passes]</c> (and <c>/plan approve --ralph</c> while planning, 2026-10-18) runs a plan as a Ralph loop, one step a fresh pass. Refused mid-turn like <see cref="Loop"/>.</summary>
    Plan,

    /// <summary>
    /// <c>/code-audit [model|advisor|any|confirm] [passes] [diff|staged|branch [base]|path …] [focus …] [--] [brief]</c> (2026-10-18, the user's idea:
    /// <c>/plan gaps</c>' engine for code): read-only passes over the tree, a path or the git changes, each reporting through <c>present_audit</c> into
    /// <c>.neon/audits/&lt;name&gt;.md</c>, until enough passes in a row find nothing new; <c>open</c>, <c>fix</c> and <c>resume</c> work on a kept report.
    /// Waits for the idle line under a reply; refused under a running <c>/botchat</c> (but <c>open</c>).
    /// </summary>
    CodeAudit,

    /// <summary><c>/expand</c>: every folded tool run, code block, diff (2026-10-04) and thinking block in the transcript unfolded, and the ones to come (2026-09-22, the user's ask: what <c>/tools expand</c> did that morning, as a root word). No argument; Ctrl+O flips the same state.</summary>
    Expand,

    /// <summary><c>/collapse</c>: every tool run, code block and diff past its collapse count, and every thinking block, folded again (2026-09-22, the user's ask: what <c>/tools collapse</c> did, as a root word). No argument.</summary>
    Collapse,

    /// <summary>
    /// <c>/find [words]</c> (2026-10-07, the user's ask, phase 5 of the UI round; Ctrl+F the same, bare, since 2026-10-18, Ctrl+Shift+F until then): the transcript's find —
    /// a find row over the input row, the transcript scrolled to the newest match, Enter older, Shift+Enter newer, ESC back at the
    /// bottom (<c>UI/TranscriptFind</c>). The words start it with that text, and open the folds that hold it at once. Needs the
    /// screen; under a reply it waits for the idle line.
    /// </summary>
    Find,


    /// <summary><c>/gituser [force]</c> (2026-09-21): the <c>GitLib email</c> and <c>GitLib name</c> settings (<c>Git native …</c> until 2026-09-30) written into the working directory's repository config as <c>user.email</c> / <c>user.name</c>; a <c>[user]</c> section already there is kept unless <c>force</c>, and <c>GitLib tools</c> off refuses (later that day). <c>/git user [force]</c> until 2026-09-26 (the user's call: the one verb was noise); <c>/git</c> is an unknown command now.</summary>
    GitUser,

    /// <summary><c>/window</c> (<c>/windowsize</c> until later on 2026-09-19): the terminal window's width and height, for information.</summary>
    Window,

    /// <summary><c>/keycheck</c> (2026-10-04, the user's ask): every key chord of the app asked of Windows — free, or held by another program as a global hotkey — on an info pane. No argument; a pane under a reply.</summary>
    KeyCheck,

    /// <summary>
    /// <c>/log</c> (2026-09-22, the user's ask): the <c>--log</c> file opened in the editor Windows associates with it, and only
    /// a command under <c>--log</c>. Since 2026-10-02 (the user's ask) a command in every run: the bare word opens the log window
    /// (<see cref="Viewer.LogWindow"/>, over the run's lines in memory), and <c>/log file</c> the <c>--log</c> file in the
    /// editor as before. <c>/log path</c> (2026-10-11, the user's ask) copies that file's path.
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

    /// <summary><c>/about</c>: the app's version, runtime, folders, servers, third-party components, the embedded models' Hugging Face repositories (the Models tab, 2026-10-09) and licence, in the info pane.</summary>
    About,

    /// <summary><c>/skills</c>: the Skills pane listing the skills the model can load, the project notes file and the skill folders (<c>/skills</c> again since 2026-09-19, the plural beside <c>/tools</c>, the user's call — <c>/skill</c> is an unknown command now; a bare <c>/skill</c> from later on 2026-09-18, <c>/skill list</c> earlier that day, <c>/skills</c> from 2026-09-16 until then). No argument: <c>/skill &lt;name&gt; [message]</c> loaded a skill into the next reply from 2026-09-16 until later on 2026-09-18, when the <c>#</c>-mention made it redundant (the user's call); an argument was <see cref="Overloaded"/> until 2026-09-21, when <c>/skills edit &lt;name&gt;</c> came (the user's ask: the skill's <c>SKILL.md</c> in your editor). Since 2026-09-26 it takes <c>add &lt;source&gt;</c> (the user's ask: search skills.sh and install from GitHub, <see cref="Skills.SkillInstallFlow"/>); any other argument is its usage error. <c>////</c> was its alias for part of that day (gone later on 2026-09-21, the user's ask, with <c>///</c> for <c>/tools</c>).</summary>
    Skills,

    /// <summary><c>/learn [note]</c>: a skill-learning reflection over the last turn whatever its shape, the note steering it (2026-09-17, the explicit signal of <c>Skills auto learn</c>); <c>/learn sessions [N | text]</c> a pass over the stored sessions (2026-09-19).</summary>
    Learn,
    /// <summary><c>/read &lt;file&gt; [n]</c>: a text file from the working directory printed as a reply and read aloud when speech is on, from sentence <em>n</em> when given — the model never sees it; <c>/read</c> alone resumes a stopped reading, <c>/read &lt;n&gt;</c> starts the last file at sentence <em>n</em> (2026-09-17). <c>/speak</c> until 2026-10-09 (the user's call: it reads a file, which <c>/read</c> says better); the old word is unknown now, as <c>/quit</c> is.</summary>
    Read,

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
    /// <c>/eventlog</c> (2026-10-15, the event-log plan): a computer's events on a pane (<see cref="EventLogMenu"/>) — the logs as tabs, an
    /// event's page with copy and ask —, <c>/eventlog &lt;log|file.evtx&gt; [computer:x] [level:…] [id:…] [provider:…] [field:N=v]
    /// [since:…] [until:…] [audit:…] [text]</c> narrowing it (<see cref="EventLog.EventLogCommand"/>). The user's own hand: it works with
    /// <c>Event log tools</c> off, reaches only the ticked computers, and shows full text. Windows only (a Mac's own logs too since
    /// 2026-10-18, mac-pass2: the unified log, crashes and install.log, no remote computer).
    /// </summary>
    EventLog,

    /// <summary><c>/net ping|trace|port|dns|tls|certs …</c> (2026-10-15, the admin-tools plan): the network checks by the user's own hand (<see cref="Network.NetworkCommand"/>), never judged by the reach, the lists or the budget.</summary>
    Net,

    /// <summary><c>/services [computer:x] [state:…] [words]</c> (2026-10-15): a computer's services on a pane, start/stop/restart/start type under <c>readwrite</c>. Windows only.</summary>
    Services,

    /// <summary><c>/tasks [computer:x] [folder:\x] [state:…] [words]</c> (2026-10-15): a computer's scheduled tasks on a pane, run/end/enable/disable under <c>readwrite</c>. Windows only.</summary>
    Tasks,

    /// <summary><c>/ad &lt;words&gt;</c> or <c>/ad user|group|computer|find|domain …</c> (2026-10-15): the directory, read-only. Windows only (a Mac's too since 2026-10-18, mac-pass2, over Kerberos).</summary>
    Ad,

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
    /// <c>/youtube</c> (2026-10-05, the YouTube plan): <c>/youtube &lt;words&gt;</c> searches and picks a video on the pane to play where
    /// <c>YouTube open links in</c> says (2026-10-16; the video window until then); <c>play &lt;id|link&gt; [&lt;time&gt;] | pause | resume | seek | volume | mute | unmute | close | status</c>
    /// drive it (<see cref="YouTube.YouTubeCommand"/>). The user's own hand: never judged by <c>YouTube tools</c>.
    /// </summary>
    YouTube,

    /// <summary>
    /// <c>/shopping</c> (2026-10-11, the user's ask): the shopping list's pane, or <c>add | check | uncheck | remove | list | clear |
    /// store | export | print</c> (<see cref="Shopping.ShoppingCommand"/>). The user's own hand: never judged by <c>Shopping tools</c>.
    /// </summary>
    Shopping,

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

    /// <summary><c>/echo &lt;text&gt;</c>: the line printed as a reply and read aloud when speech is on — <c>/read</c>'s block and voice over typed text, never resumed (2026-09-17).</summary>
    Echo,

    /// <summary><c>/queue</c>: the messages queued while a reply runs, on a pane where Enter removes one and a button drops them all (2026-09-18, behind <c>Queue mode</c>; its send under <c>manual</c>, 2026-10-09); <c>/queue clear</c> (2026-09-21) drops them all without the pane; <c>/queue mode …</c> and <c>/queue cancel …</c> (2026-10-13, the user's ask) set Queue mode and Queue cancel mode.</summary>
    Queue,

    /// <summary><c>/sessions</c>: this profile's stored sessions on a pane (restore, rename, purge), or <c>/sessions &lt;id&gt; | purge &lt;id&gt; | purge older &lt;age&gt; | purge all | title &lt;text&gt;</c> typed (2026-09-18; <c>/session</c> until later on 2026-09-21, the user's call — the singular reads as an unknown command now).</summary>
    Session,

    /// <summary>
    /// <c>/rename [name]</c> (2026-10-05, the user's ask): the session on screen renamed from the draft line — a front door onto
    /// <c>/sessions title</c>, the name the title (waits for a reply as that does) and the bare word the rename box (a pane under
    /// a reply as that is). Ctrl+N sends the bare word (Ctrl+Alt+R until 2026-10-18).
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
    /// <c>/advisor [&lt;name&gt; &lt;question&gt;]</c> (2026-10-10, the profile advisors): bare, the advisors listed; with a name and a
    /// question, the question put to that advisor (another profile, on its own server) and the pair added to the history, tagged.
    /// </summary>
    Advisor,

    /// <summary>
    /// <c>/test</c> (2026-09-28, the user's ask: LLMTester's tests run from here): <c>/test &lt;id | reasoning | structured |
    /// long | all&gt;</c> runs them against the connected model (<see cref="Bench.BenchRunner"/>), a line per test and a
    /// table at the end, saved in the profile's <c>tests.json</c>; <c>/test history</c> lists the saved runs, and a bare
    /// <c>/test</c> the tests with their last verdicts. Never in the conversation. ESC stops a run.
    /// </summary>
    Test,

    /// <summary>
    /// <c>/avatar [&lt;emoji&gt;|&lt;words&gt;|clear]</c> (2026-10-10, the user's pick): the profile's avatar — bare, the emoji picker
    /// (<c>/settings</c> › Client › Chat › Model avatar's); an emoji sets it, <c>clear</c> clears it, words open the picker searching for them.
    /// A leading <c>user</c> or <c>model</c> (2026-10-12, the user's ask: <c>/avatar user [clear]</c>, <c>/avatar model [clear]</c>) names whose:
    /// the user's (User avatar, their sent lines) or the model's, the one a bare line means.
    /// </summary>
    Avatar,

    /// <summary>
    /// <c>/webui [on|off|reset]</c> (2026-10-14, the user's ask): the Web UI — bare, its pane (on or off, the port, the reach and the sign-in
    /// links); <c>on</c> and <c>off</c> the switch; <c>reset</c> a new sign-in token after a yes. Waits for the idle line under a reply.
    /// </summary>
    WebUi,

    /// <summary>
    /// <c>/pasted view|thumbs|purge</c> (2026-10-18, the user's ask: every picture folder under the profile's <c>images</c> folder with
    /// <c>/comfy</c>'s three words): the <c>Pasted images folder</c> in the picture viewer or the thumbnail browser, or emptied after a yes
    /// (<c>Images.PastedCommand</c>). Bare or another word is the usage error. Not headless.
    /// </summary>
    Pasted,

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
/// <c>/stt</c> (its <c>wake</c> and <c>interrupt</c> since 2026-10-09: <c>/wake</c> and <c>/interrupt</c> folded in, unknown commands now), <c>/read</c>, <c>/echo</c>, <c>/view</c>, <c>/learn</c>, <c>/remember</c>, <c>/memory</c> (2026-09-22, the user's ask, twice: <c>forget</c>, the standalone <c>/forget</c> folded in, then <c>copy &lt;profile&gt; [overwrite]</c>, the standalone <c>/memcopy</c> folded in the same way until 2026-10-13, <c>/profile push|pull memory</c> since — <c>/forget</c> and <c>/memcopy</c> are unknown commands now), <c>/profile</c> (its push and pull sections since 2026-10-09: <c>/cmdcopy</c>, <c>/keycopy</c> and <c>/srvcopy</c> folded in, unknown commands now; the prompt files since 2026-10-10), <c>/timer</c>, <c>/cwd</c>, <c>/tree</c>, <c>/explore</c>, <c>/copy</c>, <c>/compact</c>, <c>/gituser</c>, <c>/loop</c>, <c>/skills</c> (2026-09-21), <c>/test</c> (2026-09-28) and <c>/sys</c> (since 2026-10-09: <c>persona|operata|vocalia</c> and, as <c>/persona</c>, <c>/operata</c> and <c>/vocalia</c> took on their own from 2026-09-16 until then, <c>reset</c>, and <c>copy &lt;profile&gt; [force]</c> until 2026-10-10, when it became <c>/profile push|pull persona</c> and its siblings; those three are unknown commands now) take an argument (<see cref="TakesArgument"/>); any other command given one is <see cref="SlashCommand.Overloaded"/>, so <c>/about me</c> is told the command takes nothing rather than called unknown (2026-09-17). <c>/draft</c> takes nothing (2026-09-19: the editor is the argument); nor did <c>/cmdlist</c> and <c>/police</c> (2026-09-21 and 2026-09-22: the allowed-commands list and the police page on a pane), which became <c>/tools shell allowed</c> and <c>/tools shell police</c> on 2026-10-09 (the user's call) and read as unknown commands now. <c>/skills</c> opens the Skills pane, or <c>/skills edit &lt;name&gt;</c> the skill's file (2026-09-21; it took nothing before) (the plural since 2026-09-19, beside <c>/tools</c>; a bare <c>/skill</c> from later on 2026-09-18, in place of <c>/skill list</c>; <c>/skills</c> before that; <c>/skill &lt;name&gt; [message]</c> took a name until later that day; <c>/skill</c> is unknown now). The three tool switches <c>/web</c>, <c>/files</c>, <c>/ask</c> went later on 2026-09-18 (the user's call: the settings rows <c>Web tools</c>, <c>File tools</c>, <c>Ask user</c> are the one place now) and read as unknown commands.
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
            new("/avatar", "pick the model's or your emoji avatar"),
            new(Advisors.AdvisorText.Word, Advisors.AdvisorText.HelpSummary),
            new("/botchat", "let the profiles talk to each other"),
            new("/browse", "browse the working directory's files"),
            new("/browser", "the Browser window and its saved links"),
            new("/claude", "ask Claude Code from the chat"),
            new(Camera.CameraText.Word, Camera.CameraText.HelpSummary),
            new(NeonSidekick.Screen.ScreenText.Word, NeonSidekick.Screen.ScreenText.HelpSummary),
            new("/clear", "start over and clear the screen"),
            new(RewindText.Word, RewindText.HelpSummary),
            new("/cmd", "run a shell line yourself"),
            new(Audits.AuditText.Word, Audits.AuditText.HelpSummary),
            new("/collapse", "fold every item in the transcript"),
            new("/comfy", "manage ComfyUI and its workflows"),
            new("/compact", "shrink the conversation's context"),
            new("/copy", "copy replies to the clipboard"),
            new("/cwd", "show or change the working directory"),
            new("/docker", "manage the Docker engine's containers"),
            new("/draft", "write the next message in your editor"),
            new("/echo", "print a line as a reply"),
            new(EventLog.EventLogCommand.Word, OperatingSystem.IsMacOS() ? "read this Mac's logs" : "read Windows' event logs"),   // a Mac's own since 2026-10-18
            new(Network.NetworkCommand.Word, "check hosts, ports, DNS and certificates"),
            new("/services", "list, start and stop Windows services"),
            new("/tasks", "list and run scheduled tasks"),
            new("/ad", "look up Active Directory accounts"),
            new("/exit", "exit the app"),
            new("/expand", "unfold every item in the transcript"),
            new("/find", "find words in the transcript"),
            new("/explore", "open a folder in your file browser"),
            new("/gituser", "set the repository's git user"),
            new("/ha", "control Home Assistant"),
            new("/header", HeaderToggle.HelpSummary),
            new("/help", "show commands and keys or look things up"),
            new("/imagine", "make a picture on ComfyUI"),
            new("/keycheck", "check which key chords are held"),
            new("/learn", "write or improve a skill"),
            new("/log", "show the diagnostic log"),
            new("/loop", "repeat a message or command"),
            new("/mcp", "manage MCP servers and their tools"),
            new("/memory", "manage the model's memory"),
            new("/model", "pick or set the model"),
            new("/msgclear", "clear the profile's message history"),
            new("/new", "start over, keeping the screen"),
            new("/open", "open a file in its default app"),
            new(Images.PastedCommand.Word, "view, browse or purge pasted pictures"),   // 2026-10-18
            new("/perfbar", "show or hide the performance bar"),
            new(HomeAssistant.HomeBarText.Command, "show, hide or edit the home bar"),
            new("/plan", "plan before doing"),
            new("/pdf", "make PDFs, or merge, cut or turn them"),
            new(Viewer.ProcessWindowText.Word, "watch the model's background processes"),
            new("/print", "print a file or the last reply"),
            new("/profile", "switch or manage profiles"),
            new("/prompts", "saved prompts to send"),
            new("/queue", "manage the queued messages"),
            new("/reasoning", "pick or set the reasoning effort"),
            new("/remember", "add a memory"),
            new("/rename", "rename the current session"),
            new("/sampling", "tune the model's sampling"),
            new("/server", "pick or set the LLM server"),
            new("/sessions", "restore, rename or purge sessions"),
            new("/settings", "edit, search and save settings", "//"),
            new("/shortcut", "put a launch shortcut on the desktop"),
            new("/skills", "manage and install skills"),
            new("/read", "read a text file or PDF aloud"),
            new("/splash", "start over with the splash screen"),
            new("/stt", "toggle voice input and the wake word"),
            new("/sys", "show or edit the system prompt"),
            new("/terminal", "open a terminal in a folder"),
            new("/test", "benchmark the connected model"),
            new("/theme", "switch or export the color theme"),
            new(UI.MarkText.Word, UI.MarkText.HelpSummary),
            new("/timer", "set, list and stop timers, stopwatches"),
            new("/toolbar", ToolbarItems.HelpSummary),
            new("/tools", "switch the model's tools on or off"),
            new("/tree", "show the working directory's tree"),
            new("/tts", "toggle speech output"),
            new("/usage", "show token usage and statistics"),
            new("/vault", "show the Obsidian vault's tree"),
            new("/view", "view images, a folder of them, or a PDF"),
            new("/webui", "serve the chat to a browser"),
            new("/window", "show the terminal window's size"),
            new(YouTube.YouTubeCommand.Word, YouTube.YouTubeText.HelpSummary),
            new(Shopping.ShoppingCommand.Word, Shopping.ShoppingText.HelpSummary),
        }.OrderBy(entry => entry.Command, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The commands on <c>/help</c>'s <see cref="BasicTabTitle"/> tab (2026-09-27, the user's list): the everyday ones, so the
    /// first tab is a short list (it fit without scrolling until 2026-10-03, when <c>/about</c>, <c>/explore</c>, <c>/perfbar</c>,
    /// <c>/stt</c>, <c>/toolbar</c>, <c>/tts</c> and <c>/wake</c> came over from the advanced tab, the user's pick; <c>/wake</c> folded into <c>/stt</c> on 2026-10-09; <c>/msgclear</c> came over on 2026-10-10 and <c>/avatar</c> on 2026-10-11, the user's picks; <c>/prompts</c> on 2026-10-13, offered in its review and kept). Every other command (<c>/log</c> among them) is on
    /// <see cref="AdvancedTabTitle"/>. Only the pane is split: <see cref="HelpText"/>, <see cref="Completions"/> and
    /// <see cref="LabelWidth"/> stay the one A-to-Z list. Pinned.
    /// </summary>
    public static readonly IReadOnlySet<string> BasicCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "/about", "/avatar", "/clear", "/compact", "/copy", "/cwd", "/draft", "/exit", "/explore", "/help", "/memory", "/model", "/msgclear", "/new", "/perfbar", "/profile", "/prompts",
        "/queue", "/reasoning", "/remember", "/rename", "/rewind", "/server", "/sessions", "/settings", "/skills", "/stt", "/sys", "/terminal", "/toolbar", "/tools", "/tree", "/tts",
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
    public const string KeysLine = "Keys: Enter = send   Esc = stop the speech / clear the line / cancel the reply   Up/Down = history, or the draft's rows when it wraps   push-to-talk key (F4 unless changed) = talk";

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
    public static readonly string[] Words = { "/help", "/clear", "/new", "/splash", "/rewind", "/theme", "/mark", "/avatar", "/queue", "/sessions", "/rename", "/compact", "/server", "/model", "/reasoning", "/sampling", "/settings", "//", "/tools", "/mcp", "/tts", "/stt", "/read", "/remember", "/memory", "/keycheck", "/cmd", "/msgclear", "/sys", "/usage", "/perfbar", "/homebar", "/toolbar", "/header", "/profile", "/prompts", "/timer", "/cwd", "/tree", "/vault", "/explore", "/browse", "/browser", "/open", "/terminal", "/shortcut", "/log", "/process", "/view", "/imagine", "/comfy", "/pasted", "/ha", "/docker", "/eventlog", "/net", "/services", "/tasks", "/ad", "/camera", "/screen", "/youtube", "/shopping", "/print", "/pdf", "/echo", "/gituser", "/copy", "/draft", "/loop", "/plan", "/code-audit", "/botchat", "/claude", "/advisor", "/test", "/expand", "/collapse", "/find", "/window", "/skills", "/learn", "/about", "/webui", "/exit" };

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
    public const string RenameWord = "/rename";       // 2026-10-05, what Ctrl+N sends (Ctrl+Alt+R until 2026-10-18): the rename box
    public const string MemoryWord = "/memory";       // 2026-09-22, the disk between the balloon and the lock, while Memory is on
    public const string ProfileWord = "/profile";     // later on 2026-09-29, the ID card after the gear: the profile picker
    public const string ThemeWord = "/theme";         // 2026-10-04, the abacus after the ID card: the theme picker
    public const string WebUiWord = "/webui";         // 2026-10-14, the phone after the abacus: the Web UI's pane
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
            Images.PastedCommand.Word => SlashCommand.Pasted,
            "/rewind" => SlashCommand.Rewind,
            "/theme" => SlashCommand.Theme,
            "/mark" => SlashCommand.Mark,
            "/avatar" => SlashCommand.Avatar,
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
            "/read" => SlashCommand.Read,
            "/remember" => SlashCommand.Remember,
            "/memory" => SlashCommand.Memory,
            "/keycheck" => SlashCommand.KeyCheck,
            "/cmd" => SlashCommand.Cmd,
            "/msgclear" => SlashCommand.MsgClear,
            "/sys" => SlashCommand.Sys,
            "/usage" => SlashCommand.Usage,
            "/perfbar" => SlashCommand.Perf,
            "/homebar" => SlashCommand.HomeBar,
            "/toolbar" => SlashCommand.Toolbar,
            "/header" => SlashCommand.Header,
            "/profile" => SlashCommand.Profile,
            "/prompts" => SlashCommand.Prompts,
            "/timer" => SlashCommand.Timer,
            "/cwd" => SlashCommand.Cwd,
            "/tree" => SlashCommand.Tree,
            "/vault" => SlashCommand.Vault,
            "/explore" => SlashCommand.Explore,
            "/open" => SlashCommand.Open,
            "/browse" => SlashCommand.Browse,
            "/browser" => SlashCommand.Browser,
            "/terminal" => SlashCommand.Terminal,
            "/shortcut" => SlashCommand.Shortcut,
            "/view" => SlashCommand.View,
            "/imagine" => SlashCommand.Imagine,
            "/comfy" => SlashCommand.Comfy,
            "/ha" => SlashCommand.HomeAssistant,
            "/docker" => SlashCommand.Docker,
            "/eventlog" => SlashCommand.EventLog,
            "/net" => SlashCommand.Net,
            "/services" => SlashCommand.Services,
            "/tasks" => SlashCommand.Tasks,
            "/ad" => SlashCommand.Ad,
            "/camera" => SlashCommand.Camera,
            "/screen" => SlashCommand.Screen,
            "/youtube" => SlashCommand.YouTube,
            "/webui" => SlashCommand.WebUi,
            "/shopping" => SlashCommand.Shopping,
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
            "/code-audit" => SlashCommand.CodeAudit,
            "/botchat" => SlashCommand.BotChat,
            "/claude" => SlashCommand.Claude,
            "/advisor" => SlashCommand.Advisor,
            "/test" => SlashCommand.Test,
            "/expand" => SlashCommand.Expand,
            "/collapse" => SlashCommand.Collapse,
            "/find" => SlashCommand.Find,
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
    /// <see cref="SlashCommand.Unknown"/> and <see cref="SlashCommand.Overloaded"/> take nothing. <c>/help &lt;words&gt;</c> since 2026-10-14
    /// (the user's ask): the model looks the words up with <c>neon_help</c>, or the manual answers itself.
    /// </summary>
    public static bool TakesArgument(SlashCommand command) => command is
        SlashCommand.Help or SlashCommand.Compact or SlashCommand.Server or SlashCommand.Model or SlashCommand.Reasoning or SlashCommand.Sampling or SlashCommand.Theme or SlashCommand.Mark or SlashCommand.Avatar
        or SlashCommand.Tts or SlashCommand.Voice or SlashCommand.Read or SlashCommand.View or SlashCommand.Imagine or SlashCommand.Comfy or SlashCommand.Echo
        or SlashCommand.Learn
        or SlashCommand.Sys
        or SlashCommand.Remember or SlashCommand.Memory or SlashCommand.Profile or SlashCommand.Prompts or SlashCommand.Timer
        or SlashCommand.Cwd or SlashCommand.Tree or SlashCommand.Vault or SlashCommand.Explore or SlashCommand.Open or SlashCommand.Browse or SlashCommand.Browser or SlashCommand.Terminal or SlashCommand.Shortcut or SlashCommand.Copy or SlashCommand.Session or SlashCommand.Rename or SlashCommand.GitUser
        or SlashCommand.Loop or SlashCommand.Plan or SlashCommand.CodeAudit or SlashCommand.BotChat or SlashCommand.Claude or SlashCommand.Advisor or SlashCommand.Cmd or SlashCommand.Queue or SlashCommand.Skills or SlashCommand.Test
        or SlashCommand.HomeAssistant or SlashCommand.Docker or SlashCommand.EventLog or SlashCommand.Net or SlashCommand.Services or SlashCommand.Tasks or SlashCommand.Ad or SlashCommand.Camera or SlashCommand.Screen or SlashCommand.YouTube or SlashCommand.Shopping or SlashCommand.Print or SlashCommand.Pdf or SlashCommand.Perf or SlashCommand.HomeBar or SlashCommand.Toolbar or SlashCommand.Header or SlashCommand.Rewind
        or SlashCommand.Log or SlashCommand.Process or SlashCommand.Tools or SlashCommand.Settings or SlashCommand.Find or SlashCommand.WebUi
        or SlashCommand.Splash or SlashCommand.Pasted;   // 2026-10-18: view|thumbs|purge
}
