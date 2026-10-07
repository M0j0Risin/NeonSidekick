namespace NeonSidekick.Help;

/// <summary>One way to type a command and what it does: <c>/camera watch [&lt;seconds&gt;|off]</c>, then its sentence.</summary>
public sealed record CommandForm(string Syntax, string Meaning);

/// <summary>A slash command with every form it takes, the first its plainest.</summary>
public sealed record CommandHelp(string Command, IReadOnlyList<CommandForm> Forms);

/// <summary>
/// Every slash command's forms (2026-10-02, for <c>neon_help</c>, <see cref="NeonHelp"/>), A to Z by command: seeded from README's
/// Slash commands table, a subcommand a form of its own. <see cref="App.SlashCommands.HelpEntries"/> holds each command's short
/// description; <c>NeonHelpCatalogTests</c> pins that the two name the same commands, so a new command needs its forms here (and
/// its docs/COMMANDS.md row, README's until 2026-10-05). Since 2026-10-05 (the user's ask: <c>/help</c>'s Commands tabs were too tight) these are also the forms
/// <c>/help</c> shows in its third column (<see cref="HelpSyntax.Forms"/>), so every syntax keeps <see cref="HelpSyntax"/>'s
/// notation — the command, then subcommands, keywords and <c>--flags</c> as typed, <c>&lt;lowercase-hyphenated&gt;</c>
/// placeholders, <c>[optional]</c> parts, <c>a|b</c> choices with no blanks, <c>...</c> for a repeat — one shape per form, and
/// <c>HelpSyntaxTests</c> holds every one to it. The same day every command's parser was read against this list and the forms it
/// lacked were added (<c>/botchat --resume</c> and <c>--kill</c>, <c>/docker ps &lt;filter&gt;</c>, <c>/ha tv volume</c>,
/// <c>/sampling extra clear</c>, <c>/screen desktop</c>, <c>/theme export</c> and others).
/// </summary>
public static class HelpCommands
{
    /// <summary>The forms as written here, every system's but where <see cref="HelpMac.Forms"/> has a Mac text.</summary>
    internal static readonly IReadOnlyList<CommandHelp> AsWritten =
    [
        new("/about",
        [
            new("/about", "Show the app's version, runtime, folders, components and licence."),
        ]),
        new("/botchat",
        [
            new("/botchat [<profile> ...] [--] [<topic>]", "Let profiles talk to each other, each in its own persona, until you stop them. Leading words that name profiles are the cast (every profile when none is named; the loaded one always speaks first); the first other word starts the topic, and `--` makes everything after it the topic. See Bot conversations."),
            new("/botchat --resume [<line>]", "Carry on the last bot conversation, with an optional line of your own to it first."),
            new("/botchat --kill", "Stop the extra embedded servers the bots started."),
        ]),
        new("/camera",
        [
            new("/camera", "Open the camera pane: frame the shot (live in a camera window of its own under *Camera preview* `live`), Space takes it, R takes it again, Enter puts it on the input line as `[Image #N]`, ESC drops it. The photo is saved in the *Camera output folder* (`camera_images` by default). Without the pane it takes one at once. See Camera."),
            new("/camera snap", "Take a photo at once and put it on the input line."),
            new("/camera list", "List the cameras Windows sees in a pane, numbered, the chosen one marked."),
            new("/camera use <n>|<name>", "Choose the camera by its number in the list, its name, or the start of its name (*Camera device*)."),
            new("/camera live", "Show the camera live in its own window until you close the window or `/camera off`."),
            new("/camera watch [<seconds>|off]", "Watch mode: the camera looks every *Camera watch interval* seconds (or the seconds given, 2 to 3600), and a picture that changed rides your next message (with *Camera watch speaks up*, the model may also be shown it unasked). Never on at startup; `/camera watch off` stops it."),
            new("/camera off", "Let go of `/camera live` and watch mode; the camera closes a few seconds later."),
        ]),
        new("/claude",
        [
            new("/claude <message>", "Send the message to Claude Code (the `claude` CLI) and stream its reply into the transcript. See Claude Code from the chat."),
            new("/claude new", "Start a new Claude conversation; the next `/claude` begins it. `/clear`, `/new` and a profile switch start one too."),
        ]),
        new("/clear",
        [
            new("/clear", "Start a new conversation and clear the screen."),
        ]),
        new("/cmdclear",
        [
            new("/cmdclear", "Clear this profile's command history, both stored and in memory, after you confirm."),
        ]),
        new("/cmdcopy",
        [
            new("/cmdcopy <profile> [overwrite] [--history]", "Copy this profile's *Shell allowed commands* into another profile. They are added to its list, or replace it with `overwrite`. `--history` copies the command history instead; this is refused while that profile has *Keep command history* off."),
        ]),
        new("/cmdlist",
        [
            new("/cmdlist", "Open the *Shell allowed commands* list. Enter removes a prefix; the ask and yolo buttons at the top (A, Y) switch *Shell command policy*, yolo after a yes; ESC closes it."),
        ]),
        new("/collapse",
        [
            new("/collapse", "Fold the tool runs, code blocks, diffs and thinking again."),
        ]),
        new("/comfy",
        [
            new("/comfy", "Show the ComfyUI server's status, the workflows found (family, input, size, placeholders), skipped files and where workflows go, in a pane. A server that does not answer is an error line in the chat."),
            new("/comfy offered", "List the workflows currently offered to the model in a pane, one bullet each (name, family, input, size, description), without asking the server."),
            new("/comfy edit json|markdown|md <workflow>", "Open a workflow's graph, or its `.md`, in your editor (the `.md` is created with the family filled in if it does not exist)."),
            new("/comfy view", "Open the picture viewer on the output folder. Works while a reply runs."),
            new("/comfy thumbs", "Open the thumbnail browser on the output folder. Works while a reply runs."),
            new("/comfy purge", "Permanently delete everything in the output folder, `.pasted` inputs included, after a yes/no. Refused when the output folder is the working directory."),
        ]),
        new("/compact",
        [
            new("/compact [<focus>]", "Shrink the current context. A focus tells the summary what to concentrate on (under *Compact type* `prune` there is no summary, so it is not used)."),
        ]),
        new("/copy",
        [
            new("/copy [<n>|all] [--thinking]", "Copy the last reply (or the last *n*, or the whole transcript) to the clipboard as Markdown. `--thinking` (before or after) includes the model's thinking, quoted under `💭 **Thinking**` where it happened."),
        ]),
        new("/cwd",
        [
            new("/cwd [<path>|~|browse]", "Show or change the working directory. A path is a full one, unquoted, and a folder that does not exist is created; once it starts with a drive (`D:\\`) the list completes that folder's subfolders. `~` returns to the profile's `files\\` folder; `browse` opens the folder picker."),
        ]),
        new("/docker",
        [
            new("/docker", "Docker Desktop's containers on a pane, running first, with their state, health and ports. Enter on one offers what fits its state: stop, restart or pause (each asks first), start or unpause, its last 50 log lines, open a published port in the browser, copy the id. Without the pane it lists them."),
            new("/docker ps [<filter>]", "List the containers, or those whose name or image holds the filter."),
            new("/docker status", "Docker Desktop's and the engine's versions, with the container counts."),
            new("/docker logs <container> [<lines>]", "A container's last lines in a pane: 50 by default, up to 2000."),
            new("/docker stats [<container>]", "The CPU, memory, network and disk use of one or every running container."),
            new("/docker start|stop|restart|pause|unpause <container>", "Act on one container, by its name, a unique part of its name, or its id (or the id's first 4 or more characters). Your own hand: *Docker writes* never applies, nothing is asked, every change is logged. Runs under a reply too."),
        ]),
        new("/draft",
        [
            new("/draft", "Write the next message in your editor. It is sent when you save and close the file."),
        ]),
        new("/echo",
        [
            new("/echo <text>", "Print a line as a reply, and read it aloud when speech is on."),
        ]),
        new("/exit",
        [
            new("/exit", "Exit the app."),
        ]),
        new("/expand",
        [
            new("/expand", "Unfold every folded tool run, code block, diff and thinking block, now and from here on. Ctrl+O switches between this and `/collapse`, and so does ⤡ on the rule over the input row (without a notice)."),
        ]),
        new("/explore",
        [
            new("/explore [<folder>]", "Open the working directory, or a folder in it, in your file browser. Ctrl+E runs it too."),
        ]),
        new("/gituser",
        [
            new("/gituser [force]", "Write *GitLib email* and *GitLib name* into the repository's config as `user.email` / `user.name`. An existing `[user]` section is kept unless you add `force`. Does nothing while *GitLib tools* is off."),
        ]),
        new("/ha",
        [
            new("/ha", "Home Assistant at a glance: the server, the lights on in each room, the TV, temperatures, motion, low batteries and to-do lists."),
            new("/ha on|off|toggle <name> [<brightness>]", "Switch a room (its group light), a light, a switch or the TV (`/ha on den 40%`, `/ha off kitchen and hallway`). A brightness (0 to 100, the `%` optional) is only for `on` and lights."),
            new("/ha scene <name>", "Activate a scene (`/ha scene den relax`)."),
            new("/ha tv on|off|mute|unmute|up|down", "Control the only media player."),
            new("/ha tv vol|volume <level>", "Set the media player's volume, 0 to 100 (the `%` optional)."),
            new("/ha tv source <name>", "Switch the media player's source (`/ha tv source hdmi 2`)."),
            new("/ha states [<filter>]", "List entities with their ids and states in a pane: a domain, words of their names, or an entity id (which shows all of its attributes)."),
            new("/ha say <sentence>", "Hand a sentence to Home Assistant's Assist agent."),
        ]),
        new("/header",
        [
            new("/header [on|off]", "Show or hide the header (*Show header*). On its own it flips the setting; `on` and `off` say which. The banner comes or goes at the next `/clear`, `/splash`, `/theme` or profile switch; nothing is redrawn now. Works while a reply runs; Ctrl+Alt+H runs it too."),
        ]),
        new("/help",
        [
            new("/help", "Show the commands and keys: everyday commands on Basic, the rest on Advanced, then Keys. Ctrl+H runs it too."),
        ]),
        new("/imagine",
        [
            new("/imagine [<workflow>] <prompt> [-- <negative>|--no-negative]", "Generate a picture on ComfyUI from your own prompt, sent exactly as typed, with no model in between. The first word is the workflow when it names one; a workflow that takes no prompt runs without one. It runs behind the input line and is drawn when done; double-click 🖼️ on the hint row to cancel. See Imagine options."),
            new("/imagine <prompt> [--seed <n>] [--size <w>x<h>] [--steps <n>] [--count <n>]", "The size's `x` may be `X` or `*`. Flags go anywhere on the line, any case; a value with spaces goes in double quotes."),
            new("/imagine <prompt> [--cfg <x>] [--denoise <x>] [--image <path>] [--image2 <path>] [--image3 <path>]", "The guidance and denoise strength, and up to three input pictures (a path, or a pasted picture's label such as `\"[Image #1]\"`); `--image2` needs `--image`, `--image3` needs `--image2`."),
        ]),
        new("/interrupt",
        [
            new("/interrupt [on|off]", "Toggle the wake-word interrupt during a spoken reply. It needs the wake word on."),
        ]),
        new("/keycheck",
        [
            new("/keycheck", "List the app's key chords (the Keys tab of `/help`) and whether another program holds each as a global hotkey, the held ones first. A held chord never reaches the app: free it in the program that holds it, such as a graphics card's overlay. Only hotkeys registered with Windows show; a keyboard hook or a Windows Terminal key binding can still take a key."),
        ]),
        new("/keycopy",
        [
            new("/keycopy <profile>", "Copy this profile's *LLM API key*, *Anthropic API key*, *OpenAI API key*, *Home Assistant API key* and *YouTube API key* into another profile, replacing its own, after you confirm. The keys are mirrored: a key that is not set here clears that profile's. Only saved keys are copied, and encrypted ones are copied as they are. A key set only by `NEONSIDEKICK_LLM_API_KEY`, `NEONSIDEKICK_ANTHROPIC_API_KEY`, `NEONSIDEKICK_OPENAI_API_KEY`, `NEONSIDEKICK_HA_TOKEN` or `NEONSIDEKICK_YOUTUBE_API_KEY` is not copied."),
        ]),
        new("/learn",
        [
            new("/learn [<note>]", "Write or improve a skill in the background from the last turn, a note steering what to keep. Needs *Agent skills* and *LLM offer tools* on."),
            new("/learn sessions [<n>|<search>]", "The same from stored sessions: the newest 5, the newest n (1 to 20), or those a search finds."),
        ]),
        new("/log",
        [
            new("/log", "Open the log window: this run's diagnostic lines, coloured by level, following the newest while it is at the bottom. Scrolling away pauses it; Ctrl+E, Ctrl+End or scrolling back to the bottom follows again, Ctrl+Home goes to the top. Drag to select, Ctrl+A selects all, Ctrl+C copies. F11 or a double-click is full screen, Esc leaves full screen and then closes it. TAB brings the terminal forward, and any other Ctrl or Alt chord runs in the chat as if pressed there (Ctrl+Alt+G closes the window)."),
            new("/log --file", "Open the diagnostic log file in your editor. Only available when the app was started with `--log <path>`."),
        ]),
        new("/loop",
        [
            new("/loop <count>|infinite [<delay>] <message>", "Send the message that many times, or until ESC or Ctrl+C, waiting for each reply. A delay is one word such as `30s`, `5m` or `1h30m` (never a bare number), up to 24 hours. See Loops."),
            new("/loop <count>|infinite [<delay>] /imagine|/speak <arguments>", "Run `/imagine` or `/speak` itself each pass, with no model in between. No other command can be looped."),
        ]),
        new("/mcp",
        [
            new("/mcp", "Connect external MCP servers and switch their tools on or off. On the Tools tab, type to narrow the list to the tools whose name or description holds the text; Backspace erases, ESC clears it, the next ESC closes."),
        ]),
        new("/memory",
        [
            new("/memory", "List memories on a pane; Enter removes one, and **read-write** (W), **read-only** (R) and **disabled** (D) on its title row set *Memory mode*."),
            new("/memory read-write|read-only|disabled|on|off", "Set *Memory mode* (`on` is read-write, `off` disabled)."),
            new("/memory forget|edit", "`forget` forgets them all, after you confirm; `edit` opens `memory.json` in your editor (invalid JSON is ignored with a warning)."),
            new("/memory copy <profile> [overwrite]", "Add them to another profile's memory, skipping duplicates, or replace it with `overwrite`, after you confirm."),
        ]),
        new("/model",
        [
            new("/model [<id>]", "Pick a model from the server's list, or set one by its id (as typed; the server is not asked). The list is A to Z, the cursor on the model in use; type to narrow it to the ids that hold the text, Backspace erases, ESC clears it, the next ESC keeps the model. On the embedded LLM, this lists the installed embedded models. Ctrl+M runs it too."),
        ]),
        new("/new",
        [
            new("/new", "Start a new conversation without clearing the screen."),
        ]),
        new("/operata",
        [
            new("/operata [reset]", "Edit `operata.md` (the operating rules) in your editor — created, when missing, with the rules in use now, the sentences for the tools that are on included — or reset it to the default."),
            new("/operata copy <profile> [force]", "Copy it to another profile (`force` replaces theirs)."),
        ]),
        new("/pdf",
        [
            new("/pdf <file> [to=<path>] [paper=letter|a4|legal] [landscape] [overwrite]", "Make a PDF in the working directory from a file: Markdown keeps its headings, tables, lists, links, coloured code and the pictures beside it; text and code become a coloured listing; HTML is printed as the page, its scripts and anything outside the working directory left out; a picture is fitted to one page. The PDF goes beside the file unless `to=` names a file or folder; an existing one is replaced only with `overwrite`. Options go anywhere. Edge, Chrome or Brave makes it; without one, Microsoft Print to PDF (Markdown, text and pictures, in black and white). *PDF engine* chooses. On its own, `/pdf` shows how to use it. See Making PDFs."),
            new("/pdf <url> [to=<path>] [overwrite]", "Make a PDF of a web page (`http://` or `https://`) as the browser shows it, after *Web browser network mode* allows its address. The page keeps its own layout, so `paper=` and `landscape` are not taken."),
            new("/pdf reply [<options>]", "Make a PDF of the last reply, formatted as Markdown."),
        ]),
        new("/perfbar",
        [
            new("/perfbar [off|text|gauge|spark|led]", "Show or hide the performance bar (*Show performance bar*). On its own it hides the bar, or shows it again with the meters it last had (CPU, RAM, GPU and VRAM the first time); `off` hides it; a look name sets that look and shows the bar. Works while a reply runs; the toolbar's 📈 and Ctrl+F run it too."),
        ]),
        new("/persona",
        [
            new("/persona [reset]", "Edit `persona.md` (the personality) in your editor, or reset it to the default."),
            new("/persona copy <profile> [force]", "Copy it to another profile (`force` replaces theirs)."),
        ]),
        new("/plan",
        [
            new("/plan <requirement>", "Have the model research with read-only tools and present a plan before anything changes. See Plan mode."),
            new("/plan [show]", "While planning: say where the plan stands."),
            new("/plan <detail>", "While planning: add detail."),
            new("/plan approve [--fresh]", "While planning: approve the presented plan by typing instead of using the pane (you may edit the file first)."),
            new("/plan cancel", "While planning: leave plan mode. The file is kept, marked `cancelled`."),
            new("/plan save [<name>]", "While planning: when a reply looks like a plan but the model never called `present_plan` (a notice says so), keep it as the plan and bring up the approval pane."),
            new("/plan open <name>", "Pick a plan up again, in or out of plan mode (names complete from `.neon/plans/`). Plan mode turns on over that file, the file goes back to `draft`, and the model is asked to read it and ask what should change. `/plan approve` then carries out only the unticked steps."),
            new("/plan open", "List the plans with their status and progress in a pane."),
        ]),
        new("/police",
        [
            new("/police", "Open the on/off page for *Shell police*. Turning it off asks first."),
        ]),
        new("/print",
        [
            new("/print <file> [printer=<name>] [copies=<n>] [pages=<range>] [landscape]", "Print a file from the working directory. Text and code print as a listing, Markdown prints formatted, and a picture is fitted to one page; each page is headed with the file's name, the time and *page N of M*. Anything else (a PDF, a Word or Excel file) goes to the program Windows has for it, on the default printer, without the options. The printer is matched by its name or part of it (`printer=color`); put a name with spaces in quotes. Copies are 1 to 10; pages are `3`, `1-3`, `4-` or a list such as `1,3-5`. Options go anywhere. *Print action policy* never applies to this command. See Printing."),
            new("/print reply [<options>]", "Print the last reply, formatted as Markdown."),
            new("/print printers", "List the installed printers in a pane, marking the Windows default and *Print default printer*. On its own, `/print` shows how to use it and lists them too."),
        ]),
        new("/process",
        [
            new("/process", "List the background processes the model started with `run_command`'s `background` option on a pane: the id, running or how it ended, how long it has run, the shell and the command. Enter or a double-click on a row opens it in the process window; the *kill* button (or **K**) stops the highlighted one after a yes/no. The toolbar's ⚡ opens it too."),
            new("/process <id>", "Open the process window on one (any unique start of its id; Tab completes it): its output live, following the newest line while at the bottom, stderr in the warning colour, the title its state. Scroll, select and copy as in the log window (`/log`); TAB brings the terminal forward. `/process` with another id switches the window to that process in the same place. Ctrl+K twice within 3 seconds stops the process: the chat says it was stopped by you, and the model hears of it on its next turn."),
        ]),
        new("/profile",
        [
            new("/profile [<name>]", "Switch profiles: a picker on its own (a typed letter jumps to the next profile starting with it), or straight to the one named. Profiles whose name starts with `_` are left off the picker and the name list (unless loaded); `/profile _name` still switches to one. See Profiles. Ctrl+P runs the bare `/profile` too."),
            new("/profile add|delete <name>", "Create or delete a profile."),
            new("/profile rename <name> <new-name>", "Rename a profile."),
            new("/profile reset [<name>] [--all]", "Reset a profile to the defaults (the loaded one when no name is given), keeping its URLs, paths and keys; `--all` resets those too."),
            new("/profile push|pull <name>", "Copy this profile's settings to another (`push`) or another's into this one (`pull`)."),
            new("/profile edit|reload", "`edit` opens `profile.json` in your editor; `reload` reads it back and reconnects only what changed."),
        ]),
        new("/queue",
        [
            new("/queue [clear]", "List and prune the messages queued during a reply (`⊠ clear all` or `c` drops them all). While the queue is held (*Queue cancel mode* `hold`, after a cancelled reply), `➤ send` or `s` sends the next one now; its reply's end releases the hold. `/queue clear` drops them without opening the pane. Ctrl+Q runs `/queue`, Ctrl+Alt+Q `/queue clear`."),
        ]),
        new("/reasoning",
        [
            new("/reasoning [none|low|medium|high|xhigh]", "Pick the reasoning effort, or set it. Ctrl+R runs it too."),
        ]),
        new("/remember",
        [
            new("/remember <text>", "Add a memory. Refused while *Memory mode* is disabled."),
        ]),
        new("/rename",
        [
            new("/rename [<name>]", "Rename the current session, as `/sessions title` does: with a name it is set at once (after the reply, under one); on its own it opens the rename box with the current name in it, and works while a reply runs. Nothing to rename before the first reply. Ctrl+Alt+R runs it too."),
        ]),
        new("/rewind",
        [
            new("/rewind [<n>]", "Go back to an earlier message. A list of the messages you sent opens (the cursor on the last, or n back), and after a yes the picked message and everything after it leave the conversation and its text returns to the input row, pictures and pasted blocks included, to edit and send again. A stored session loses the same turns. Only the conversation rewinds: what a tool changed (files written, commands run, commits) stays, and the yes/no names those tools. Messages compacted into a summary cannot be picked. Double ESC on an empty input line opens it too."),
        ]),
        new("/sampling",
        [
            new("/sampling", "Edit the per-model sampling overrides on a pane (see Sampling per model)."),
            new("/sampling <field> <value>|clear", "Set or clear one of the connected model's values: `temperature`, `top_p`, `top_k`, `min_p`, `presence_penalty`, `frequency_penalty` or `repetition_penalty` (`repeat_penalty` too)."),
            new("/sampling extra <json>|clear", "Set the extra request body as a JSON object, or clear it (`{}` clears it too)."),
            new("/sampling clear", "Clear every override of the connected model."),
        ]),
        new("/screen",
        [
            new("/screen", "Capture the monitor the app is on and put the screenshot on the input line as `[Image #N]`. It is saved in the *Screen capture output folder* (`screen_images` by default) and shown in the picture viewer under *Screen capture preview*. Your own command: *Screen capture tool* and *Screen capture ask* never apply. See Screen capture."),
            new("/screen all|desktop", "Capture every monitor as one picture."),
            new("/screen monitor:<n>", "Capture monitor n (`/screen list` numbers them; `monitor <n>` works too)."),
            new("/screen window:<id>|<title-words>", "Capture one window, even when another covers it: by its id from `/screen list`, or words of its title (or its process name)."),
            new("/screen behind", "Capture the window right behind the app's own: the one you were just in."),
            new("/screen list", "List the monitors and the windows (front to back) in a pane, with the target that names each. The argument list after `/screen ` offers them too."),
        ]),
        new("/server",
        [
            new("/server [<url>|embedded|claude-cli|docker|docker:<container>]", "Pick an LLM server found on the usual ports, or set one by URL (with its `http://` or `https://`). The list also offers the Anthropic API and the OpenAI API (each when it is on and has a key), the Claude CLI (when *Claude CLI server* is on and Claude Code is found), the installed embedded models and the chosen Docker containers (when *Docker servers enabled* is on). The model and reasoning pickers follow, and one reconnect applies all three. To add an embedded model, install it from `/settings` › Embedded. `embedded` lists only the installed embedded models (see Embedded); `claude-cli` picks the Claude CLI (see `/settings` › Anthropic). `docker` lists only the chosen containers, and `docker:<container>` switches to one (see Docker servers); a container's model is the one it serves, so no model picker follows. Ctrl+S runs it too."),
        ]),
        new("/sessions",
        [
            new("/sessions [<id>]", "List the stored sessions on a pane, or restore one by its id (`#12` works too). Ctrl+Alt+E runs it too."),
            new("/sessions purge <id>|all", "Delete one stored session, or every one."),
            new("/sessions purge older <age>", "Delete the sessions older than an age: a number of days (`30`) or a duration (`12h`, `90m`, `2 hours`, `1d 6h`)."),
            new("/sessions title [<text>]", "Rename the current session; on its own it opens a box with the current name in it (as double-clicking the name on the rule does), and works while a reply runs."),
        ]),
        new("/settings",
        [
            new("/settings", "Edit and save the settings (`//` too). Ctrl+/ runs it too. A letter typed on a tab searches every setting."),
            new("/settings <words>", "Search every setting (/settings, /tools, /skills, /mcp) by its name, tab or description; Enter edits the row found."),
            new("/settings changed", "The settings that are not their defaults, with each default; Enter edits, R puts the row back to its default."),
        ]),
        new("/skills",
        [
            new("/skills", "List the skills (Enter moves, renames, edits, reverts or deletes one; revert lists the kept versions to pick one to put back) and edit the skill, reflection and project-file settings. On the Offered tab, type to narrow the list to the skills whose name or description holds the text; Backspace erases, ESC clears it, the next ESC closes."),
            new("/skills add <source> [--global|--profile]", "Install an Agent Skill from the web, with a preview first: search words, `owner/repo[/skill]`, a GitHub URL (the repository, a `/tree/…` folder or a `SKILL.md`) or a `.zip` URL. A pane asks where it goes (the cursor starts on Cancel). Refused while a reply runs. See Installing skills."),
            new("/skills purge list|commit <age>", "`list` lists the skills not used for that long (`30` days, `12h`, `90m`) and deletes nothing; `commit` deletes them, folder and record, after a yes/no that lists them. See Skill records."),
        ]),
        new("/speak",
        [
            new("/speak [<file> [<n>]|<n>]", "Read a text file from the working directory aloud as a reply. On its own it resumes; a number starts from that sentence (a file named only with digits is typed `./5`)."),
        ]),
        new("/splash",
        [
            new("/splash", "Start a new conversation and show the splash screen."),
        ]),
        new("/stt",
        [
            new("/stt [on|off]", "Toggle voice input."),
        ]),
        new("/sys",
        [
            new("/sys", "Show the system prompt and the tools sent to the model."),
        ]),
        new("/terminal",
        [
            new("/terminal [<folder>]", "Open a new Windows Terminal window in the working directory, or in a folder under it (Tab completes the folder). Without Windows Terminal it opens a console window there. Ctrl+. runs it too."),
        ]),
        new("/test",
        [
            new("/test [<id>|reasoning|structured|long|all|history]", "Run benchmark tests against the connected model and save the results: one by its id (`grid`, `rule`, `mind`, `sycophancy`, `json`, `state`, `needle`, `multihop`, `saturation`), a group, or all; `history` shows the results so far. On its own it lists the tests with their last verdicts. See Benchmark tests."),
        ]),
        new("/theme",
        [
            new("/theme [<name>]", "Switch the colour theme (the *Theme* setting), built-in or custom. On its own it opens a list of the themes, with a preview of the highlighted one beside it when the window is wide enough; a typed letter jumps to the next theme starting with it, and nothing changes until Enter. Ctrl+Z runs it too. During a reply, it runs when the reply ends."),
            new("/theme export <name> [<new-name>]", "Write a theme to the `themes` folder as a file to edit (see Custom themes)."),
        ]),
        new("/timer",
        [
            new("/timer [<duration> [<name>]]", "List the timers, or start one: `10m`, `90s`, `1h30m`, `5 min` (a bare number is minutes), up to 24 hours."),
            new("/timer stop <name>|all", "Stop one timer, or every one."),
        ]),
        new("/toolbar",
        [
            new("/toolbar [on|off]", "Show or hide the toolbar (*Show toolbar*). On its own it hides the toolbar, or shows it again with the items it last had (the default ten the first time); `on` and `off` say which. Works while a reply runs; Ctrl+T runs it too."),
        ]),
        new("/tools",
        [
            new("/tools", "Switch the model's tools on or off, and edit each tool group's settings on its own tab. On the Offered tab, type to narrow the list to the tools whose name or description holds the text; Backspace erases, ESC clears it, the next ESC closes."),
            new("/tools <group>", "Open one group's switch on its own: `shell` (the *Shell command policy* picker; yolo asks first), `files`, `web`, `claude` (*Claude CLI advisor tool*), `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `sqlite`, `postgres`, `unc`, `ha`, `comfy`, `camera` or `print` (each tool group's on/off page; `web`'s carries default, httpclient and chromium buttons (D, H, C) that switch *Web browser mode*). The toolbar's tool items run it."),
        ]),
        new("/tree",
        [
            new("/tree [<path>]", "Show a tree of the working directory in a pane. Hidden, system and dot entries appear only when *File browser/tree mode* is `show-hidden`. `.git` folders are always left out unless you name one as the path."),
        ]),
        new("/tts",
        [
            new("/tts [on|off]", "Toggle speech output."),
        ]),
        new("/usage",
        [
            new("/usage", "Show token usage and performance statistics. A `~` marks a reasoning count the app estimated (see *LLM reasoning estimate*). Ctrl+U runs it too."),
        ]),
        new("/vault",
        [
            new("/vault [<path>]", "Show a tree of the *Obsidian vault* (or a folder in it) in a pane, like `/tree`. Dot-folders are left out, the length is capped by *File /tree max length*, and sizes follow *File /tree show sizes*. Fails if *Obsidian tools* is off, no vault is set, or the folder cannot be reached or has no `.obsidian`."),
        ]),
        new("/view",
        [
            new("/view <image>|<folder>", "Open an image from the working directory in the picture viewer, or a folder there on its newest picture. In the viewer the arrows or the mouse wheel step through the pictures, and a right-click opens the picture menu (rotate, flip, colour, resize, convert, shrink, copy the path, show in Explorer, attach, print, delete; edits go where *Image edit mode* says). Works while a reply runs."),
            new("/view <image> --chat", "Draw the image in the transcript instead. `--chat` can be the first or last word."),
            new("/view <image>|<folder> --thumbs", "Open the folder (an image's folder, with the image selected) as thumbnails in a window of its own, in step with the picture viewer and the picture strip: a click shows the picture in the viewer, a double-click or Enter opens it there, and the viewer's own moves select it here. New pictures go on the end, so nothing moves. The tiles fit the window; + and − or Ctrl+wheel resize them, F5 lists and fits again. A right-click opens the picture menu, as in the viewer. `--thumbs` can be the first or last word. Works while a reply runs."),
        ]),
        new("/vocalia",
        [
            new("/vocalia [reset]", "Edit `vocalia.md` (the spoken-reply directive: empty by default, its text added last to every spoken reply) in your editor, or remove it."),
            new("/vocalia copy <profile> [force]", "Copy it to another profile (`force` replaces theirs)."),
        ]),
        new("/wake",
        [
            new("/wake [on|off]", "Toggle the speech-input wake word (turning it off turns the interrupt off too)."),
        ]),
        new("/window",
        [
            new("/window", "Show the terminal window's width and height."),
        ]),
        new("/youtube",
        [
            new("/youtube", "Say what the video window is playing: the video, playing or paused, where it is and the volume."),
            new("/youtube <words>", "Search YouTube for the words (it needs a *YouTube API key*; each search costs 100 of its 10,000 daily quota units) and pick a video on the pane; Enter plays it in the video window. Only videos that play embedded are listed. Words that start with a verb below search too, unless what follows fits the verb."),
            new("/youtube search <words>", "Search for words whatever they start with."),
            new("/youtube play <id>|<link> [<time>]", "Play a video by its id or a YouTube link, from the time (`90` or `1:30`) or the link's own. A link alone plays too. Needs no key. The window opens where it last closed without taking the keyboard; F11 is full screen, Esc leaves it and then closes the window."),
            new("/youtube play|resume", "Carry on playing."),
            new("/youtube pause", "Pause the video."),
            new("/youtube seek <time>", "Go to a time in the video."),
            new("/youtube volume <level>", "Set the volume, 0 to 100."),
            new("/youtube mute|unmute", "Mute or unmute the video."),
            new("/youtube close", "Close the video window."),
            new("/youtube status", "The bare word's status line."),
            new("/youtube save [<id>|<link>]", "Save the video playing, or the one named, to this profile's saved videos; one named is looked up for its title (YouTube's oEmbed, no key needed, then the Data API with a *YouTube API key*). A saved video resumes where it was left the next time it plays (a time you give wins); seen to the end, it starts over. `s` in a search's picker saves the highlighted hit."),
            new("/youtube saved", "The saved videos on the pane, each with where it was left; Enter plays one from there, `d` removes one. Titles still missing are looked up first."),
            new("/youtube unsave <n>|<id>|<link>", "Take a saved video off the list, by its number in `/youtube saved`, its id or a link."),
        ]),
    ];

    /// <summary>Every command's forms: <see cref="AsWritten"/>, with <see cref="HelpMac.Forms"/>' texts on macOS (2026-10-06). Declared after it, so it is set first.</summary>
    public static readonly IReadOnlyList<CommandHelp> Commands = OperatingSystem.IsMacOS() ? HelpMac.Apply(AsWritten) : AsWritten;
}
