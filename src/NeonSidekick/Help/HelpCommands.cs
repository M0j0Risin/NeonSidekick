namespace NeonSidekick.Help;

/// <summary>One way to type a command and what it does: <c>/camera watch [seconds|off]</c>, then its sentence.</summary>
public sealed record CommandForm(string Syntax, string Meaning);

/// <summary>A slash command with every form it takes, the first its plainest.</summary>
public sealed record CommandHelp(string Command, IReadOnlyList<CommandForm> Forms);

/// <summary>
/// Every slash command's forms for <c>neon_help</c> (2026-10-02, <see cref="NeonHelp"/>), A to Z by command: seeded from README's
/// Slash commands table, a subcommand a form of its own. <see cref="App.SlashCommands.HelpEntries"/> stays the one-line list
/// <c>/help</c> shows; this is the longer one, and <c>NeonHelpCatalogTests</c> pins that the two name the same commands, so a new
/// command needs its forms here (and its README row).
/// </summary>
public static class HelpCommands
{
    public static readonly IReadOnlyList<CommandHelp> Commands =
    [
        new("/about",
        [
            new("/about", "Show the app's version, runtime, folders, components and licence."),
        ]),
        new("/botchat",
        [
            new("/botchat [profile ...] [topic]", "Let profiles talk to each other until you stop them. See Bot conversations."),
        ]),
        new("/camera",
        [
            new("/camera", "Open the camera pane: frame the shot (live in a camera window of its own under *Camera preview* `live`), Space takes it, R takes it again, Enter puts it on the input line as `[Image #N]`, ESC drops it. The photo is saved in the *Camera output folder* (`camera_images` by default). Without the pane it takes one at once. See Camera."),
            new("/camera snap", "Take a photo at once and put it on the input line."),
            new("/camera list", "List the cameras Windows sees, numbered, the chosen one marked."),
            new("/camera use <n|name>", "Choose the camera by its number in the list or its name (*Camera device*)."),
            new("/camera live", "Show the camera live in its own window until you close the window or `/camera off`."),
            new("/camera watch [seconds|off]", "Watch mode: the camera looks every *Camera watch interval* seconds (or the seconds given), and a picture that changed rides your next message (with *Camera watch speaks up*, the model may also be shown it unasked). Never on at startup; `/camera watch off` stops it."),
            new("/camera off", "Let go of `/camera live` and watch mode; the camera closes a few seconds later."),
        ]),
        new("/claude",
        [
            new("/claude <message>", "Send the message to Claude Code (the `claude` CLI) and stream its reply into the transcript. See Claude Code from the chat."),
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
            new("/cmdcopy <profile> [--history] [overwrite]", "Copy this profile's *Shell allowed commands* into another profile. They are added to its list, or replace it with `overwrite`. `--history` copies the command history instead; this is refused while that profile has *Keep command history* off."),
        ]),
        new("/cmdlist",
        [
            new("/cmdlist", "Open the *Shell allowed commands* list. Enter removes a prefix; the ask and yolo buttons at the top (A, Y) switch *Shell command policy*, yolo after a yes; ESC closes it."),
        ]),
        new("/collapse",
        [
            new("/collapse", "Fold the tool runs, code blocks and thinking again."),
        ]),
        new("/comfy",
        [
            new("/comfy", "Show the ComfyUI server's status, the workflows found (family, input, size, placeholders), skipped files and where workflows go."),
            new("/comfy edit json <workflow>, /comfy edit markdown <workflow>", "Open a workflow's graph, or its `.md`, in your editor (`md` works too; the `.md` is created with the family filled in if it doesn't exist)."),
            new("/comfy view", "Open the picture viewer on the output folder. Works while a reply runs."),
            new("/comfy purge", "Permanently delete everything in the output folder, `.pasted` inputs included, after a yes/no. Refused when the output folder is the working directory."),
        ]),
        new("/compact",
        [
            new("/compact [focus]", "Shrink the current context. A focus tells the summary what to concentrate on."),
        ]),
        new("/copy",
        [
            new("/copy [n | all] [--thinking]", "Copy the last reply (or the last *n*, or the whole transcript) to the clipboard as Markdown. `--thinking` includes the model's thinking, quoted under `💭 **Thinking**` where it happened."),
        ]),
        new("/cwd",
        [
            new("/cwd [path | ~ | browse]", "Show or change the working directory. `~` returns to the profile's `files\\` folder; `browse` opens the folder picker."),
        ]),
        new("/docker",
        [
            new("/docker", "Docker Desktop's containers on a pane, running first, with their state, health and ports. Enter on one offers what fits its state: stop, restart or pause (each asks first), start or unpause, its last 50 log lines, open a published port in the browser, copy the id. Without the pane it lists them."),
            new("/docker ps | status | logs <container> [lines] | stats [container]", "The containers; Docker Desktop's and the engine's versions with the counts; a container's last lines (50 by default); the CPU, memory, network and disk use of one or every running container."),
            new("/docker start|stop|restart|pause|unpause <container>", "Act on one container, by name, part of a name or id. Your own hand: *Docker writes* never applies, nothing is asked, every change is logged. Runs under a reply too."),
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
            new("/expand", "Unfold every folded tool run, code block and thinking block, now and from here on. Ctrl+O switches between this and `/collapse`, and so does ⤡ on the rule over the input row (without a notice)."),
        ]),
        new("/explore",
        [
            new("/explore [path]", "Open the working directory in your file browser. Ctrl+E runs it too."),
        ]),
        new("/gituser",
        [
            new("/gituser [force]", "Write *GitLib email* and *GitLib name* into the repository's config as `user.email` / `user.name`. An existing `[user]` section is kept unless you add `force`. Does nothing while *GitLib tools* is off."),
        ]),
        new("/ha",
        [
            new("/ha", "Home Assistant at a glance: the server, the lights on in each room, the TV, temperatures, motion, low batteries and to-do lists."),
            new("/ha on|off|toggle <room or name> [brightness%]", "Switch a room (its group light), a light, a switch or the TV (`/ha on den 40%`, `/ha off kitchen and hallway`)."),
            new("/ha scene <name>", "Activate a scene (`/ha scene den relax`)."),
            new("/ha tv on|off|mute|unmute|up|down|vol <0-100>|source <name>", "Control the only media player (`/ha tv source hdmi 2`)."),
            new("/ha states [domain | words | entity id]", "List entities with their ids and states. An entity id shows all of its attributes."),
            new("/ha say <sentence>", "Hand a sentence to Home Assistant's Assist agent."),
        ]),
        new("/header",
        [
            new("/header [on | off]", "Show or hide the header (*Show header*). On its own it flips the setting; `on` and `off` say which. The banner comes or goes at the next `/clear`, `/splash`, `/theme` or profile switch; nothing is redrawn now. Works while a reply runs; Ctrl+Alt+H runs it too."),
        ]),
        new("/help",
        [
            new("/help", "Show the commands and keys: everyday commands on Commands (basic), the rest on Commands (advanced), then Keys. Ctrl+H runs it too."),
        ]),
        new("/imagine",
        [
            new("/imagine [workflow] <prompt> [-- <negative> | --no-negative] [--seed N] [--size WxH] [--steps N] [--cfg X] [--denoise X] [--image <path>] [--image2 <path>] [--image3 <path>] [--count N]", "Generate a picture on ComfyUI from your own prompt, sent exactly as typed, with no model in between. See Imagine options."),
        ]),
        new("/interrupt",
        [
            new("/interrupt [on|off]", "Toggle the wake-word interrupt during a spoken reply."),
        ]),
        new("/keycopy",
        [
            new("/keycopy <profile>", "Copy this profile's *LLM API key*, *Anthropic API key* and *Home Assistant API key* into another profile, replacing its own, after you confirm. The keys are mirrored: a key that isn't set here clears that profile's. Only saved keys are copied, and encrypted ones are copied as they are. A key set only by `NEONSIDEKICK_LLM_API_KEY`, `NEONSIDEKICK_ANTHROPIC_API_KEY` or `NEONSIDEKICK_HA_TOKEN` is not copied."),
        ]),
        new("/learn",
        [
            new("/learn [note | sessions [N | text]]", "Write or improve a skill in the background, from the last turn or from stored sessions."),
        ]),
        new("/log",
        [
            new("/log", "Open the log window: this run's diagnostic lines, coloured by level, following the newest while it is at the bottom. Scrolling away pauses it; Ctrl+E, Ctrl+End or scrolling back to the bottom follows again, Ctrl+Home goes to the top. Drag to select, Ctrl+A selects all, Ctrl+C copies. F11 or a double-click is full screen, Esc leaves full screen and then closes it. TAB brings the terminal forward, and any other Ctrl or Alt chord runs in the chat as if pressed there (Ctrl+Alt+G closes the window)."),
            new("/log --file", "Open the diagnostic log file in your editor. Only available when the app was started with `--log <path>`."),
        ]),
        new("/loop",
        [
            new("/loop <count> [delay] <message>, /loop infinite [delay] <message>", "Send the message that many times, or until ESC or Ctrl+C, waiting for each reply. See Loops."),
        ]),
        new("/mcp",
        [
            new("/mcp", "Connect external MCP servers and switch their tools on or off. On the Tools tab, type to narrow the list to the tools whose name or description holds the text; Backspace erases, ESC clears it, the next ESC closes."),
        ]),
        new("/memory",
        [
            new("/memory [on | off | forget | edit | copy <profile> [overwrite]]", "List memories on a pane (Enter removes one); **● on** (N) and **○ off** (F) on its title row switch *Memory*, as `on` and `off` do. `forget` forgets them all. `edit` opens `memory.json` in your editor (invalid JSON is ignored with a warning). `copy` adds them to another profile's memory, skipping duplicates, or replaces it with `overwrite`. `forget` and `copy` ask first."),
        ]),
        new("/model",
        [
            new("/model [id]", "Pick a model from the server's list, or set one. The list is A to Z, the cursor on the model in use; type to narrow it to the ids that hold the text, Backspace erases, ESC clears it, the next ESC keeps the model. On the embedded LLM, this lists the installed embedded models. Ctrl+M runs it too."),
        ]),
        new("/new",
        [
            new("/new", "Start a new conversation without clearing the screen."),
        ]),
        new("/operata",
        [
            new("/operata [reset | copy <profile> [force]]", "Edit `operata.md` (the operating rules) in your editor, reset it to the default, or copy it to another profile (`force` replaces theirs)."),
        ]),
        new("/pdf",
        [
            new("/pdf <file> [to=<out.pdf>] [paper=letter|a4|legal] [landscape] [overwrite]", "Make a PDF in the working directory from a file: Markdown keeps its headings, tables, lists, links, coloured code and the pictures beside it; text and code become a coloured listing; HTML is printed as the page, its scripts and anything outside the working directory left out; a picture is fitted to one page. The PDF goes beside the file unless `to=` names a file or folder; an existing one is replaced only with `overwrite`. Edge, Chrome or Brave makes it; without one, Microsoft Print to PDF (Markdown, text and pictures, in black and white). *PDF engine* chooses. See Making PDFs."),
            new("/pdf https://… [to=<out.pdf>] [overwrite]", "Make a PDF of a web page as the browser shows it, after *Web browser network mode* allows its address. The page keeps its own layout, so `paper=` and `landscape` are not taken."),
            new("/pdf reply [options]", "Make a PDF of the last reply, formatted as Markdown."),
        ]),
        new("/perfbar",
        [
            new("/perfbar [off | text | gauge | spark | led]", "Show or hide the performance bar (*Show performance bar*). On its own it hides the bar, or shows it again with the meters it last had (CPU, RAM, GPU and VRAM the first time); `off` hides it; a look name sets that look and shows the bar. Works while a reply runs; the toolbar's 📈 and Ctrl+F run it too."),
        ]),
        new("/persona",
        [
            new("/persona [reset | copy <profile> [force]]", "The same for `persona.md` (the personality)."),
        ]),
        new("/plan",
        [
            new("/plan <requirement>", "Have the model research with read-only tools and present a plan before anything changes. See Plan mode."),
            new("/plan, /plan show", "Say where the plan stands."),
            new("/plan <text>", "Add detail."),
            new("/plan approve [--fresh]", "Approve the presented plan by typing instead of using the pane (you may edit the file first)."),
            new("/plan cancel", "Leave plan mode. The file is kept, marked `cancelled`."),
            new("/plan save [name]", "When a reply looks like a plan but the model never called `present_plan` (a notice says so), keep it as the plan and bring up the approval pane."),
            new("/plan open <name>", "Pick a plan up again, in or out of plan mode (names complete from `.neon/plans/`). Plan mode turns on over that file, the file goes back to `draft`, and the model is asked to read it and ask what should change. `/plan approve` then carries out only the unticked steps."),
            new("/plan open", "List the plans with their status and progress."),
        ]),
        new("/police",
        [
            new("/police", "Open the on/off page for *Shell police outside paths*. Turning it off asks first."),
        ]),
        new("/print",
        [
            new("/print <file> [printer=<name>] [copies=N] [pages=1-3] [landscape]", "Print a file from the working directory. Text and code print as a listing, markdown prints formatted, and a picture is fitted to one page; each page is headed with the file's name, the time and *page N of M*. Anything else (a PDF, a Word or Excel file) goes to the program Windows has for it, on the default printer. The printer is matched by its name or part of it (`printer=color`); put a name with spaces in quotes. *Print action policy* never applies to this command. See Printing."),
            new("/print reply [options]", "Print the last reply, formatted as markdown."),
            new("/print printers", "List the installed printers, marking the Windows default and *Print default printer*."),
        ]),
        new("/profile",
        [
            new("/profile [name | add <name> | delete <name> | rename <name> <new> | reset [name] [--all] | push <name> | pull <name> | edit | reload]", "Switch, create, delete, rename or reset a profile, or copy its settings to another (`push`) or from another (`pull`). `edit` opens `profile.json` in your editor; `reload` reads it back and reconnects only what changed. Profiles whose name starts with `_` are left off the picker and the name list (unless loaded); `/profile _name` still switches to one. See Profiles. Ctrl+P runs the bare `/profile` too."),
        ]),
        new("/queue",
        [
            new("/queue [clear]", "List and prune the messages queued during a reply (`⊠ clear all` or `c` drops them all). `/queue clear` drops them without opening the pane."),
        ]),
        new("/reasoning",
        [
            new("/reasoning [level]", "Pick the reasoning effort (`none`, `low`, `medium`, `high`, `xhigh`). Ctrl+R runs it too."),
        ]),
        new("/remember",
        [
            new("/remember <text>", "Add a memory."),
        ]),
        new("/rewind",
        [
            new("/rewind [n]", "Go back to an earlier message. A list of the messages you sent opens (the cursor on the last, or n back), and after a yes the picked message and everything after it leave the conversation and its text returns to the input row, pictures and pasted blocks included, to edit and send again. A stored session loses the same turns. Only the conversation rewinds: what a tool changed (files written, commands run, commits) stays, and the yes/no names those tools. Messages compacted into a summary can't be picked. Double ESC on an empty input line opens it too."),
        ]),
        new("/sampling",
        [
            new("/sampling [field value]", "Edit the per-model sampling overrides on a pane. To change the connected model's values directly, use `/sampling <field> <value>`, `<field> clear`, `extra <json>` or `clear` (see Sampling per model)."),
        ]),
        new("/screen",
        [
            new("/screen", "Capture the monitor the app is on and put the screenshot on the input line as `[Image #N]`. It is saved in the *Screen capture output folder* (`screen_images` by default) and shown in the picture viewer under *Screen capture preview*. Your own command: *Screen capture tool* and *Screen capture ask* never apply. See Screen capture."),
            new("/screen all", "Capture every monitor as one picture."),
            new("/screen monitor:<n>", "Capture monitor n (`/screen list` numbers them)."),
            new("/screen window:<id or title words>", "Capture one window, even when another covers it: by its id from `/screen list`, or words of its title (or its process name)."),
            new("/screen behind", "Capture the window right behind the app's own: the one you were just in."),
            new("/screen list", "List the monitors and the windows (front to back) with the target that names each."),
        ]),
        new("/server",
        [
            new("/server [url | embedded | claude-cli | docker | docker:<container>]", "Pick an LLM server found on the usual ports, or set one by URL. The list also offers the Anthropic API and the OpenAI API (each when it's on and has a key), the Claude CLI (when *Claude CLI server* is on and Claude Code is found), the installed embedded models and the chosen Docker containers (when *Docker servers enabled* is on). The model and reasoning pickers follow, and one reconnect applies all three. To add an embedded model, install it from `/settings` › Embedded. `embedded` lists only the installed embedded models (see Embedded); `claude-cli` picks the Claude CLI (see `/settings` › Anthropic). `docker` lists only the chosen containers, and `docker:<container>` switches to one (see Docker servers); a container's model is the one it serves, so no model picker follows. Ctrl+S runs it too."),
        ]),
        new("/sessions",
        [
            new("/sessions [id | purge <id> | purge older <age> | purge all | title [<text>]]", "List, restore, rename and purge stored sessions. An age is a number of days (`30`) or a duration (`12h`, `90m`, `2 hours`, `1d 6h`). `title` on its own opens a box with the current name in it (as double-clicking the name on the rule does), and works while a reply runs. Ctrl+Alt+E runs it too."),
        ]),
        new("/settings",
        [
            new("/settings, //", "Edit and save the settings. Ctrl+/ runs it too."),
        ]),
        new("/skills",
        [
            new("/skills", "List the skills (Enter moves, renames, edits, reverts or deletes one; revert lists the kept versions to pick one to put back) and edit the skill, reflection and project-file settings. On the Offered tab, type to narrow the list to the skills whose name or description holds the text; Backspace erases, ESC clears it, the next ESC closes."),
            new("/skills add <search words | owner/repo[/skill] | github url | zip url> [--global | --profile]", "Install an Agent Skill from the web, with a preview first. A pane asks where it goes (the cursor starts on Cancel). Refused while a reply runs. See Installing skills."),
            new("/skills purge list <age>", "List the skills not used for that long (`30` days, `12h`, `90m`). Nothing is deleted. See Skill records."),
            new("/skills purge commit <age>", "Delete the skills not used for that long, folder and record, after a yes/no that lists them."),
        ]),
        new("/speak",
        [
            new("/speak [file [n] | n]", "Read a text file from the working directory aloud as a reply. On its own it resumes; a number starts from that sentence."),
        ]),
        new("/splash",
        [
            new("/splash", "Start a new conversation and show the splash screen."),
        ]),
        new("/stt",
        [
            new("/stt [on|off]", "Toggle speech input."),
        ]),
        new("/sys",
        [
            new("/sys", "Show the system prompt and the tools sent to the model."),
        ]),
        new("/terminal",
        [
            new("/terminal [folder]", "Open a new Windows Terminal window in the working directory, or in a folder under it (Tab completes the folder). Without Windows Terminal it opens a console window there. Ctrl+. runs it too."),
        ]),
        new("/test",
        [
            new("/test [id | reasoning | structured | long | all | history]", "Run benchmark tests against the connected model and save the results. On its own it lists the tests with their last verdicts. See Benchmark tests."),
        ]),
        new("/theme",
        [
            new("/theme [name]", "Switch the colour theme (the *Theme* setting), built-in or custom. On its own it opens a list of the themes, with a preview of the highlighted one beside it when the window is wide enough; a typed letter jumps to the next theme starting with it, and nothing changes until Enter. During a reply, it runs when the reply ends. `/theme export <name> [new-name]` writes a theme to the `themes` folder as a file to edit (see Custom themes)."),
        ]),
        new("/timer",
        [
            new("/timer [duration [name] | stop <name> | stop all]", "List the timers, start one (`10m`, `90s`, `1h30m`), or stop one."),
        ]),
        new("/toolbar",
        [
            new("/toolbar [on | off]", "Show or hide the toolbar (*Show toolbar*). On its own it hides the toolbar, or shows it again with the items it last had (the default seven the first time); `on` and `off` say which. Works while a reply runs; Ctrl+T runs it too."),
        ]),
        new("/tools",
        [
            new("/tools", "Switch the model's tools on or off and edit their settings (Web, Files, Shell, Ask, Claude, Home Assistant, Print, Obsidian, ComfyUI, SQL, Oracle, MySQL, UNC, Git). On the Offered tab, type to narrow the list to the tools whose name or description holds the text; Backspace erases, ESC clears it, the next ESC closes."),
            new("/tools <group>", "Open one group's switch on its own: `shell` (the *Shell command policy* picker; yolo asks first), `files`, `web`, `claude` (*Claude CLI advisor tool*), `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `sqlite`, `postgres`, `unc`, `ha`, `comfy`, `camera` or `print` (each tool group's on/off page; `web`'s carries default, httpclient and chromium buttons (D, H, C) that switch *Web browser mode*). The toolbar's tool items run it."),
        ]),
        new("/tree",
        [
            new("/tree [path]", "Print a tree of the working directory. Hidden, system and dot entries appear only when *File browser/tree mode* is `show-hidden`. `.git` folders are always left out unless you name one as the path."),
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
            new("/vault [path]", "Print a tree of the *Obsidian vault* (or a folder in it), like `/tree`. Dot-folders are left out, the length is capped by *File /tree max length*, and sizes follow *File /tree show sizes*. Fails if *Obsidian tools* is off, no vault is set, or the folder can't be reached or has no `.obsidian`."),
        ]),
        new("/view",
        [
            new("/view <image or folder> [--chat]", "Open an image from the working directory in the picture viewer, or a folder there on its newest picture. `--chat` (as the first or last word) draws it in the transcript instead. Works while a reply runs."),
        ]),
        new("/vocalia",
        [
            new("/vocalia [reset | copy <profile> [force]]", "Edit `vocalia.md` (the spoken-reply directive: empty by default, its text added last to every spoken reply) in your editor, remove it, or copy it to another profile (`force` replaces theirs)."),
        ]),
        new("/wake",
        [
            new("/wake [on|off]", "Toggle the speech-input wake word."),
        ]),
        new("/window",
        [
            new("/window", "Show the terminal window's width and height."),
        ]),
    ];
}
