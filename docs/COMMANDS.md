# Slash commands

Every command and its forms. The [README](../README.md#useful-first-commands) has the few to start with; in the app, `/help` lists them all.

Type `/` to list every command; after a command and a space, the app offers its arguments where it can. `//` is a shortcut for `/settings`. Forms are written as: words typed as they are, `<placeholders>` to fill in, `[optional]` parts, and `a|b` for a choice. See also [Keyboard shortcuts](SETTINGS.md#keyboard-shortcuts) and [Commands typed during a reply](SETTINGS.md#commands-typed-during-a-reply).

## Contents

- [All commands](#all-commands)
- [Command details](#command-details)
  - [Plan mode](#plan-mode)
  - [Bot conversations](#bot-conversations)
  - [Claude Code from the chat](#claude-code-from-the-chat)
  - [Advisor](#advisor)
  - [Your own shell line](#your-own-shell-line)
  - [Loops](#loops)
  - [Benchmark tests](#benchmark-tests)
  - [Imagine options](#imagine-options)
  - [Folder picker](#folder-picker)
  - [File browser](#file-browser)
  - [Picture viewer](#picture-viewer)
  - [Thumbnail browser](#thumbnail-browser)
  - [Log window](#log-window)
  - [Process window](#process-window)
  - [Camera](#camera)
  - [Screen capture](#screen-capture)
  - [Pasting a picture](#pasting-a-picture)
  - [Profiles](#profiles)
  - [Desktop shortcuts](#desktop-shortcuts)
  - [Custom themes](#custom-themes)
  - [Voice presets](#voice-presets)
  - [Pane keys](#pane-keys)

## All commands

| Command | What it does |
|---|---|
| `/about` | Shows the version, repository, runtime, folders, components, the embedded models (with Hugging Face links) and licence. |
| `/avatar` | Opens the emoji picker for the profile's *Model avatar* (see [The avatar picker](SETTINGS.md#the-avatar-picker)). |
| `/avatar <emoji>` | Sets the avatar to that emoji (typed, or pasted from Win+.). |
| `/avatar <words>` | Opens the picker searching for the words; headless, sets the best match. |
| `/avatar clear` | Removes the avatar: replies open with ● again and the window title shows the name alone. |
| `/advisor` | Lists the advisors with their roles, server kind and model. See [Advisor](#advisor). |
| `/advisor <name> <question>` | Asks one advisor (by profile name or a word of its role) on its own profile's server; the answer joins the conversation. |
| `/claude <message>` | Sends the message to Claude Code and streams its reply. See [Claude Code from the chat](#claude-code-from-the-chat). |
| `/claude new` | Starts a new Claude conversation. |
| `/clear` | Starts a new conversation and clears the screen. |
| `/cmd <command>` | Runs a shell line yourself in the working directory, in *Shell default*'s shell, under the same checks as the model's. See [Your own shell line](#your-own-shell-line). |
| `/cmd --shell <shell> <command>` | The same in another shell (`powershell`, `cmd` or `bash`; on a Mac `zsh`, `bash` or `powershell`). |
| `/cmd --bg <command>` | The same, run in the background while you chat; its output is reported when it ends. |
| `/keycheck` | Lists the app's key chords and whether another program holds each as a global hotkey (a held chord never reaches the app). On a Mac it also names macOS shortcuts and a terminal whose Option key types characters. `/help`'s Keys tab shows the same list with its *⌨ key check* button (`k`). |
| `/compact [<focus>]` | Shrinks the context; a focus tells the summary what to keep. |
| `/copy [<n>\|all] [--thinking]` | Copies the last reply (the last *n*, or the whole transcript) as Markdown; `--thinking` includes the thinking. |
| `/cwd [<path>\|~\|browse]` | Shows or changes the working directory (a path completes as you type). `~` returns to the profile's `files\`; `browse` opens the [folder picker](#folder-picker). |
| `/camera` | Opens the camera pane: Space takes the photo, `r` retakes, Enter attaches it as `[Image #N]`, ESC drops it. See [Camera](#camera). |
| `/camera snap` | Takes a photo at once and puts it on the input line. |
| `/camera list` | Lists the cameras, the chosen one marked. |
| `/camera use <n>\|<name>` | Chooses the camera by number or name (*Camera device*). |
| `/camera live` | Shows the camera live in its own window until you close it or `/camera off`. |
| `/camera watch [<seconds>\|off]` | Looks every *Camera watch interval* (or the seconds given); a changed picture rides your next message. Never on at startup. |
| `/camera off` | Ends live view and watch mode. |
| `/camera view` | Opens the picture viewer on *Camera output folder*. |
| `/camera thumbs` | Opens the thumbnail browser on *Camera output folder*. |
| `/screen` | Captures the monitor the app is on and puts it on the input line as `[Image #N]`. The argument list offers the targets, monitors and windows. See [Screen capture](#screen-capture). |
| `/screen all\|desktop` | Captures every monitor as one picture. |
| `/screen monitor:<n>` | Captures monitor n (`monitor <n>` works too). |
| `/screen window:<id>\|<title-words>` | Captures one window, even when another covers it: by id, or words of its title or program. |
| `/screen behind` | Captures the window right behind the app's own (the one you were just in). |
| `/screen list` | Lists the monitors and windows, front to back, with the target that names each. |
| `/screen view` | Opens the picture viewer on *Screen capture output folder*. |
| `/screen thumbs` | Opens the thumbnail browser on *Screen capture output folder*. |
| `/docker` | Lists the containers in a pane; Enter offers stop, restart or pause (asking first), start, logs and more, and `l` shows only the LLM servers. On a Mac: Docker Desktop, OrbStack, Colima or Rancher Desktop. |
| `/docker ps [<filter>]` | Lists the containers, or those whose name or image holds the filter. |
| `/docker status` | Docker Desktop's and the engine's versions, with container counts. |
| `/docker logs <container> [<lines>]` | A container's last lines (50 by default, up to 2000). |
| `/docker stats [<container>]` | CPU, memory, network and disk use of one or every running container. |
| `/docker start\|stop\|restart\|pause\|unpause <container>` | Acts on one container (name, part of one, or id). Nothing is asked and *Docker writes* doesn't apply, but every change is logged. |
| `/draft` | Writes the next message in your editor; it is sent when you save and close. |
| `/echo <text>` | Prints a line as a reply (spoken when speech is on). |
| `/exit` | Exits the app. |
| `/explore [<folder>]` | Opens the working directory in your file browser. |
| `/browse [<folder>]` | Browses the working directory as a tree in a pane; Enter on a file opens it in its default app. See [File browser](#file-browser). |
| `/gituser [force]` | Writes *GitLib email* and *GitLib name* into the repository's config; an existing `[user]` stays unless `force`. Needs *GitLib tools* on. |
| `/ha` | Home Assistant at a glance: lights on per room, the TV, temperatures, motion, low batteries and to-do lists. |
| `/ha on\|off\|toggle <name> [<brightness>]` | Switches a room, light, switch or the TV (`/ha on den 40%`, `/ha off kitchen and hallway`). |
| `/ha scene <name>` | Activates a scene (`/ha scene den relax`). |
| `/ha tv on\|off\|mute\|unmute\|up\|down` | Controls the only media player. |
| `/ha tv vol\|volume <level>` | Sets its volume, 0 to 100. |
| `/ha tv source <name>` | Switches its source (`/ha tv source hdmi 2`). |
| `/ha states [<filter>]` | Lists entities and their states, filtered by domain, name or id; an entity id shows all its attributes. |
| `/ha say <sentence>` | Hands a sentence to Home Assistant's Assist agent. |
| `/header [on\|off]` | Shows or hides the banner (*Show header*) from the next clear; alone, flips it. |
| `/help` | The commands (Basic and Advanced tabs) and the keys. The Keys tab's *⌨ key check* button (`k`) swaps in `/keycheck`'s list, and pressed again shows the keys. |
| `/homebar [on\|off]` | Hides the home bar or shows it again with the items it had (Ctrl+W); the first time, with nothing picked, opens the picker. See [Home bar](SETTINGS.md#home-bar). |
| `/homebar edit` | The home bar's picker: the bar's order (Shift+↑↓, Shift+Home/End, Delete) and the rooms and devices to add, and the shopping list's 🛒 (its Shopping tab, open without Home Assistant too). |
| `/homebar tap\|show <item>` | What a double-click and a right-click on a chip do, by its item (`entity:light.desk`, `area:kitchen`) or its name. |
| `/homebar name <item> = <alias>` | Shows an item's chip under a name of your own (`/homebar name Den PC Monitor = PC`); nothing after `=` shows the chip's emoji alone (no name, no value: a room's 🏠 without its count), and `default` puts its own name back. The chip's label only. |
| `/homebar emoji <item> = <emoji>` | Shows an item's chip with an emoji of your own in place of its glyph (`/homebar emoji Den Lamp = 🛋️`); nothing after `=`, or `default`, puts its own back. One emoji only. It stays fixed, so a lock's 🔒/🔓 no longer follows its state. |
| `/learn [<note>]` | Writes or improves a skill in the background from the last turn. |
| `/learn sessions [<n>\|<search>]` | The same from stored sessions: the newest 5, the newest n (up to 20), or those a search finds. |
| `/log` | Opens the [log window](#log-window). Works without `--log`. |
| `/log file` | Opens the `--log` file in your editor (only when started with `--log <path>`). |
| `/log path` | Copies the `--log` file's path (only when started with `--log <path>`). |
| `/loop <count>\|infinite [<delay>] <message>` | Sends the message that many times, or until ESC or Ctrl+C, waiting for each reply. See [Loops](#loops). |
| `/loop <count>\|infinite [<delay>] /imagine\|/read <arguments>` | Runs `/imagine` or `/read` itself each pass. |
| `/plan <requirement>` | Researches with read-only tools and presents a plan before anything changes. See [Plan mode](#plan-mode). |
| `/botchat [<profile> ...] [--] [<topic>]` | Lets profiles talk to each other until you stop them; `--` makes the rest the topic. See [Bot conversations](#bot-conversations). |
| `/botchat --resume [<line>]` | Carries on the last bot conversation (Ctrl+Alt+B, or double-click 👥 on the hint row). |
| `/botchat --kill` | Stops the extra embedded servers the bots started (Ctrl+Alt+K, or double-click 🧟 on the hint row). |
| `/expand` | Unfolds every tool run, code block, diff and thinking block, from here on. Ctrl+O switches between this and `/collapse`. |
| `/collapse` | Folds them again. |
| `/find [<words>]` | Finds words in the transcript, every match marked (keys under [Pane keys](#pane-keys)). Ctrl+Shift+F runs it too once Windows Terminal's own find is unbound. |
| `/mcp` | Connects MCP servers and switches their tools. On the Tools tab typing filters the list, and with two or more servers a second strip picks one server's tools. |
| `/memory` | Lists memories in a pane; Enter forgets one (asks). `w`, `o` and `x` set *Memory mode* to read-write, read-only or disabled. |
| `/memory read-write\|read-only\|disabled\|on\|off` | Sets *Memory mode* (`on` is read-write, `off` disabled). |
| `/memory forget\|edit` | `forget` forgets all, after a confirmation; `edit` opens `memory.json` in your editor. |
| `/memory copy <profile> [overwrite]` | Adds them to another profile's memory (or replaces it with `overwrite`), after a confirmation. |
| `/model [<id>]` | Picks or sets the model; type to filter the list. On the embedded LLM, lists the installed models. |
| `/msgclear` | Clears the profile's message history (the ↑/↓ recall), after a confirmation. |
| `/new` | Starts a new conversation without clearing the screen. |
| `/open <file>` | Opens a file from the working directory in its default app (not a folder). |
| `/perfbar [off\|text\|gauge\|led]` | Hides the performance bar or shows it again; a look name sets that look. |
| `/perfbar edit` | The *Performance bar* checklist: the meters and their order. |
| `/print <file> [printer=<name>] [copies=<n>] [pages=<range>] [landscape]` | Prints a file from the working directory (see [Printing](TOOLS.md#printing)); the printer matches by name or part of one. *Print action policy* never applies. On a Mac a PDF prints as it is (no landscape) and Office files are refused. |
| `/print reply [<options>]` | Prints the last reply as formatted Markdown. |
| `/print printers` | Lists the printers, marking the system default and *Print default printer*. `/print` alone shows its usage and the list. |
| `/pdf <file> [to=<path>] [paper=letter\|a4\|legal] [landscape] [overwrite]` | Makes a PDF from Markdown, text, code, HTML or a picture, beside the file unless `to=` says (see [Making PDFs](TOOLS.md#making-pdfs)). On a Mac only the browser makes it. |
| `/pdf <url> [to=<path>] [overwrite]` | Makes a PDF of a web page; *Web browser network mode* still applies. |
| `/pdf reply [<options>]` | Makes a PDF of the last reply. |
| `/process` | Lists the model's background processes in a pane. Enter opens one in the [process window](#process-window), `k` stops it (asks), `r` and `e` show only the running or ended ones. While any run, **▶ n** at the right of the rule under the input line counts them; a double-click there opens this list. |
| `/process <id>` | Shows one process's output live in the [process window](#process-window) (any unique start of the id; Tab completes it). |
| `/profile [<name>]` | Switches profiles: a picker, or straight to the one named. See [Profiles](#profiles). |
| `/profile add\|delete <name>` | Creates a profile (from this profile's settings and memories) or deletes one. |
| `/profile rename <name> <new-name>` | Renames a profile. |
| `/profile reset [<name>] [--all]` | Resets a profile to the defaults, keeping its avatar, URLs, paths and keys unless `--all`. |
| `/profile push\|pull <name>` | Copies every setting but the working directory to (`push`) or from (`pull`) another profile; a pull clears the conversation. |
| `/profile push\|pull keys <name>` | Copies the LLM, Anthropic, OpenAI, Home Assistant and YouTube API keys, after a confirmation; a key unset in the source clears the target's. Keys set only by environment variable aren't copied. |
| `/profile push\|pull servers <name>` | Copies the server settings (URL, model, reasoning, sampling, and the embedded, Docker, Anthropic, OpenAI, Claude Code and `/claude` rows), replacing the target's, after a confirmation. Saved values only. |
| `/profile push\|pull commands <name> [overwrite]` | Copies *Shell allowed commands* (added, or replacing with `overwrite`). |
| `/profile push\|pull history <name> [overwrite]` | Copies the message history; refused when the target has *Keep message history* off. |
| `/profile push\|pull policestrings <name> [overwrite]` | Copies *Shell police forbidden strings* (added, or replacing with `overwrite`). |
| `/profile push\|pull persona <name> [overwrite]` | Copies `persona.md` (the personality), after a confirmation; an existing one is replaced only with `overwrite`. |
| `/profile push\|pull operata <name> [overwrite]` | The same for `operata.md` (the operating rules). |
| `/profile push\|pull vocalia <name> [overwrite]` | The same for `vocalia.md` (the spoken-reply directive). |
| `/profile edit\|reload` | `edit` opens `profile.json`; `reload` reads it back and reconnects what changed. |
| `/queue [clear]` | Lists the queued messages: Enter removes one (asks), `c` clears all, and `s` sends the next one now while the queue is held or under *Queue mode* `manual`. `/queue clear` drops them without the pane. |
| `/read [<file> [<n>]\|<n>]` | Reads a text file aloud; alone it resumes, and a number starts at that sentence. On an empty line Space pauses or plays and ←/→ step a sentence. |
| `/reasoning [none\|low\|medium\|high\|xhigh]` | Picks the reasoning effort. |
| `/rewind [<n>]` | Goes back to an earlier message: it and everything after leave the conversation and the stored session, and its text returns to the input row. Tool changes are not undone; compacted messages can't be picked. |
| `/remember <text>` | Adds a memory. |
| `/rename [<name>]` | Renames the current session; alone, opens the rename box. |
| `/sampling` | Edits the per-model sampling in a pane. See [Sampling per model](SETTINGS.md#sampling-per-model). |
| `/sampling <field> <value>\|clear` | Sets or clears one of the connected model's values. |
| `/sampling extra <json>\|clear` | Sets or clears its extra request body. |
| `/sampling clear` | Clears every override of the connected model. |
| `/server [<url>\|embedded\|claude-code\|docker\|docker:<container>]` | Picks an LLM server (found ones, the Anthropic and OpenAI APIs, Claude Code, embedded models, chosen Docker containers on Windows) or sets one by URL, then the model and reasoning. The words go straight to that server; a container serves its own model. |
| `/sessions [<id>]` | Lists the stored sessions, or restores one with its pictures. |
| `/sessions purge <id>\|all` | Deletes one stored session, or every one. |
| `/sessions purge older <age>` | Deletes sessions older than an age: days (`30`) or a duration (`12h`, `1d 6h`). |
| `/sessions title [<text>]` | Renames the current session; alone, opens a box with the current name. |
| `/settings`, `//` | Edits the settings. A letter typed on a tab searches every setting. |
| `/settings <words>` | Searches every setting (`/settings`, `/tools`, `/skills`, `/mcp`) by name, tab or description; Enter edits the row found. |
| `/settings changed` | Lists the settings that differ from their defaults; Enter edits, `r` puts one back to its default. |
| `/shopping` | The shopping list in a pane, in the active store's aisle order, ticked items last (see [Shopping list](TOOLS.md#shopping-list)). Without the pane (and headless) it prints. |
| `/shopping add [<items>]` | Adds comma-separated items (`/shopping add milk, 2 eggs, bread x2`); an item already there gets the quantity added. Alone, opens the add page with past purchases. |
| `/shopping check\|uncheck\|remove <item>` | Ticks, unticks or removes an item, by name or by the number `/shopping list` shows beside it (the active store's list). |
| `/shopping rename <item> to <new-name>` | Renames an item, keeping its quantity and tick; a name already on the list merges the two. |
| `/shopping list [<store>]` | Prints the list in the active (or named) store's order. |
| `/shopping clear [all]` | Removes the ticked items; `all` empties the list after a yes (headless: at once). |
| `/shopping store [<name>]` | Lists the stores and the active one's layout; with a name, makes that store active. |
| `/shopping store rename <name> to <new-name>` | Renames a store and its reference folder. The stores' manager (`/tools` › Home › Shopping › *Shopping stores*, or the pane's `⇄ store`) does this too. |
| `/shopping store add\|remove <name>` | Adds a store (its folder under `db\shopping\reference` is made), or removes one and its layout after a yes (headless: at once); the folder stays. |
| `/shopping export [<store>] [verbose]` | Writes the items left to buy to a new timestamped file in the working directory (`shopping-list-20261011-162530.md`): one line per aisle, or with `verbose` a heading per aisle and a checkbox per item. |
| `/shopping print [<store>] [verbose]` | Prints the same list with `/print`'s printer and font settings. |
| `/shopping reindex [<store>]` | Re-reads the store's reference documents (also done whenever a file changes). |
| `/shortcut [<profile>] [--log]` | Puts a desktop shortcut that starts the app on this profile (or the one named); `--log` adds a log. On a Mac it is a `.command` file. See [Desktop shortcuts](#desktop-shortcuts). |
| `/skills` | Lists the skills and edits the skill, reflection and project-file settings. The Offered tab shows each skill's version (🔒 [locked](SETTINGS.md#locking-a-skill), ⛔ [disabled](SETTINGS.md#disabling-a-skill)) and when it last changed; a second strip picks the scope, and typing filters. |
| `/skills add <source> [--global\|--profile]` | Installs an [Agent Skill](https://agentskills.io) after a preview: search words, `owner/repo[/skill]`, a GitHub URL or a `.zip` URL. Refused during a reply. See [Installing skills](SETTINGS.md#installing-skills). |
| `/skills purge list\|commit <age>` | `list` shows the skills unused for that long; `commit` deletes them after a yes/no. See [Skill records and purging unused skills](SETTINGS.md#skill-records-and-purging-unused-skills). |
| `/splash` | Starts a new conversation and shows the splash screen. |
| `/stt [on\|off]` | Toggles voice input. |
| `/stt wake [on\|off]` | Toggles the wake word (off turns the interrupt off too). |
| `/stt interrupt [on\|off]` | Toggles the wake-word interrupt during a spoken reply; needs the wake word on. |
| `/sys` | Shows the system prompt and the tools sent to the model. |
| `/sys persona [reset]` | Edits `persona.md` (the personality) in your editor, or resets it after a yes. |
| `/sys operata [reset]` | The same for `operata.md` (the operating rules); a missing file is created from the rules in use now. |
| `/sys vocalia [reset]` | The same for `vocalia.md` (the voice directive, empty by default, added to every spoken reply); reset removes it. |
| `/terminal [<folder>]` | Opens a new terminal (Windows Terminal; on a Mac Terminal or iTerm2) in the working directory or a folder under it. Ctrl+. (Ctrl+] on a Mac). |
| `/test [<id>\|reasoning\|structured\|long\|all\|history]` | Runs benchmark tests against the connected model; alone, lists them with their last verdicts. See [Benchmark tests](#benchmark-tests). |
| `/theme [<name>]` | Switches the colour theme, built-in or [custom](#custom-themes); alone, opens a picker with a live preview. Nothing changes until Enter. |
| `/theme export <name> [<new-name>]` | Writes a theme to the `themes` folder as a file to edit (see [Custom themes](#custom-themes)). |
| `/timer [<duration> [<name>]]` | Lists timers, or starts one (`10m`, `90s`, `1h30m`). |
| `/timer stop <name>\|all` | Stops one timer, or every one. |
| `/toolbar [on\|off]` | Hides the toolbar or shows it again. |
| `/toolbar edit` | The *Tool bar* checklist: the items and their order. |
| `/tools` | Switches the model's tools on or off and edits each group's settings, one tab per group. On the Offered tab, typing filters the list. |
| `/tools <group>` | Opens one group's page: `shell`, `ask`, `files`, `image`, `web`, `advisors`, `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `sqlite`, `postgres`, `unc`, `ha`, `comfy`, `camera` or `print`. The toolbar's tool items run it; the pages' buttons are under [Pane keys](#pane-keys). |
| `/tools shell allowed` | Opens *Shell allowed commands*: Enter removes a prefix (asks), `o`/`a`/`y` switch *Shell command policy* to off, ask or yolo, typing filters. The toolbar's lock or Ctrl+Alt+L. |
| `/tools shell police` | Opens *Shell police*; `s` opens its forbidden strings. The toolbar's officer or Ctrl+Alt+O. |
| `/tree [<path>]` | Shows a tree of the working directory in a pane (hidden entries per *File browser/tree mode*; `.git` only when named). |
| `/tts [on\|off]` | Toggles speech output. |
| `/usage` | Token usage and performance; `/claude`, `/botchat` and the advisors get rows of their own. `~` marks an estimate. |
| `/vault [<path>]` | Shows a tree of the *Obsidian vault* (or a folder in it), like `/tree`. |
| `/view <image>\|<folder>` | Opens an image (or a folder's newest picture) in the [picture viewer](#picture-viewer). |
| `/view <image> --chat` | Draws it in the transcript instead. |
| `/view <image>\|<folder> --thumbs` | Opens the folder as thumbnails beside the viewer, in step with it (see [Thumbnail browser](#thumbnail-browser)). The flag can be first or last. |
| `/imagine [<workflow>] <prompt> [-- <negative>\|--no-negative]` | Generates a picture on ComfyUI from your prompt exactly as typed. See [Imagine options](#imagine-options). |
| `/imagine <prompt> [--seed <n>] [--size <w>x<h>] [--steps <n>] [--count <n>]` | Its seed, size, steps and count. |
| `/imagine <prompt> [--cfg <x>] [--denoise <x>] [--image <path>] [--image2 <path>] [--image3 <path>]` | Its guidance, denoise strength and up to three input pictures. |
| `/comfy` | The ComfyUI server's status, the workflows found, skipped files and where workflows go. |
| `/comfy edit json\|markdown\|md <workflow>` | Opens a workflow's graph, or its `.md` (created if missing), in your editor. |
| `/comfy offered` | Lists the workflows offered to the model. |
| `/comfy view` | Opens the picture viewer on the output folder. |
| `/comfy thumbs` | Opens the thumbnail browser on the output folder. |
| `/comfy purge` | Deletes everything in the output folder after a yes/no (refused when it is the profile's `images` folder itself). |
| `/window` | Shows the terminal window's size. |
| `/youtube`, `/youtube status` | Says what the video window is playing. |
| `/youtube <words>` | Searches YouTube (needs a *YouTube API key*; 100 quota units a search) and opens a picker; Enter plays a video. `/youtube search <words>` searches even words that start like a command. |
| `/youtube play <id>\|<link> [<time>]` | Plays a video from the time given (`90`, `1:30`) or the link's own; no key needed (on a Mac, macOS 14+). See [YouTube](TOOLS.md#youtube) for the window's keys. |
| `/youtube search [--switches] <words>` | Searches with switches: `--playlists`, `--channels`, `--order relevance\|date\|views\|rating\|title`, `--length short\|medium\|long`, `--since today\|week\|month\|year\|<date>`, `--region <cc>`, `--lang <code>`, `--live`, `--captions`, `--safe none\|moderate\|strict`. Defaults come from `/tools` › Apps › YouTube. |
| `/youtube play <playlist>\|<link> [<n>]` | Plays a public playlist (or a channel's uploads) from entry n; no key needed. Entries the player refuses are skipped. |
| `/youtube next\|previous`, `/youtube entry <n>` | The playlist's next or previous video (`prev` too), or its video n. |
| `/youtube playlist [<id>\|<link>]` | A playlist's videos in a pane (needs a key: 1 unit, plus 1 per 50 videos); Enter plays from there, `s` saves a video. Alone, the playlist playing. |
| `/youtube play\|resume`, `pause`, `seek <time>`, `volume <level>`, `mute\|unmute`, `close` | Drive the video window: resume, pause, go to a time, set the volume (0–100), mute or unmute, close it. |
| `/youtube save [<id>\|<link>]` | Saves the video playing (or the one named) to this profile's `youtube.json`; it later resumes a few seconds before where you left it. |
| `/youtube list` | The saved videos in a pane, each with where it was left; Enter plays, `d` removes one, `c` clears all (each asks). |
| `/youtube list --clear` | Removes every saved video, after a yes (headless: at once). |
| `/youtube unsave <n>\|<id>\|<link>` | Removes a saved video by its number, id or link. |

## Command details

### Plan mode

`/plan <requirement>` has the model research and present a plan before anything changes. It needs *LLM offer tools* and is refused during a reply.

* **Tools:** read-only ones only (files, git reads, web, database reads, the vault, sessions, `ask_user`…) plus `present_plan`. Anything that writes, runs or starts something, and every MCP tool, waits for approval.
* **Advisors:** while `neon_advisor` is offered, the model consults the relevant [advisors](#advisor) first and says which advice it followed.
* **The plan** is printed and saved as `.neon/plans/<name>.md` in the working directory. 📝 shows while planning.
* **Approving:** the pane offers **Approve & run** (`a`), **Approve, clear context & run** (`f`), **Keep refining…** (`r`) or **Cancel plan** (`c`). Approval runs a turn with every tool, ticking the plan's checkboxes; the file ends `done`, or `incomplete` with its progress.
* `/new`, `/clear` and a profile switch leave plan mode.

| Command | What it does |
|---|---|
| `/plan`, `/plan show` | Says where the plan stands. |
| `/plan <detail>` | Adds detail. |
| `/plan approve [--fresh]` | Approves by typing instead (you may edit the file first). |
| `/plan cancel` | Leaves plan mode; the file is kept, marked `cancelled`. |
| `/plan save [<name>]` | Keeps a reply that looks like a plan as the plan, and brings up the approval pane. |
| `/plan open <name>` | Picks a plan up again; `/plan approve` then runs only the unticked steps. |
| `/plan open` | Lists the plans with status and progress. |

### Bot conversations

`/botchat [<profile> ...] [--] [<topic>]` lets profiles talk to each other until you stop them.

* **Cast:** the profiles named, or all of them; the current one joins and speaks first.
* **Topic:** the first word that isn't a profile starts it (`/botchat ada max the best pizza`); after `--`, the rest is the topic. Without one, the bots pick. With *Botchat recent context* on, the bots also see this chat's last 10 messages.
* **Turns:** each bot speaks in its persona and, with speech on, its own voice. A bot named in the last line speaks next, else a random one. Bots use this profile's LLM, or their own under *Botchat LLM mode* `multi` (a bot whose server fails sits out).
* **Tools:** pictures ([Botchat pictures](SETTINGS.md#botchat-pictures)), the chat's tools or *Botchat limited tools*, skills and memory, per the *Botchat* settings. Advisors are never offered. Bots on the Claude Code server have no tools.
* **Joining in:** a line you type joins before the next reply. With *STT input* on, push-to-talk cuts in and listens (with TTS off, the wake phrase does too).
* **Stopping:** ESC stops the voice, then cuts the current bot short, then ends the chat. Ctrl+Alt+B ends it at once; `/exit`, `/clear` and `/new` end it first.
* **Resume:** `/botchat --resume [<line>]`, Ctrl+Alt+B or a double-click on 👥 continues the last chat.
* **Embedded bots:** see *Botchat multi-embedded*. `/botchat --kill`, Ctrl+Alt+K or a double-click on 🧟 stops the leftover extra servers.
* Pronouns come from each profile's first TTS voice (`am_`, `bm_`… male, else female). The chat is saved as its own session under *Session logging*, and its tokens are a *Botchat* row on `/usage`.

### Claude Code from the chat

`/claude <message>` runs the `claude` CLI headless in the working directory.

* The reply streams in under Claude's name, each tool on a dim line, with a cost footer; with speech on, it is spoken.
* The pair joins the conversation tagged `[to Claude]` and `[Claude]`, so the local model can build on it. Claude doesn't see the local conversation.
* Each session has one Claude conversation, resumed by the next `/claude`. `/claude new`, `/clear`, `/new` and a profile switch start another.
* *Claude Code command permissions* sets what Claude may do; anything more is denied, never asked.
* ESC or Ctrl+C stops it. Works with no LLM server; refused during a reply. Your own Claude Code setup (sign-in, `CLAUDE.md`, skills, MCP servers, hooks) applies.

### Advisor

`/advisor <name> <question>` asks one of the profiles set up under `/settings` › Advisors ([SETTINGS.md](SETTINGS.md#advisors)), as the model's `neon_advisor` does.

* It answers on its own profile's server and model, with its persona and role, and may use the read-only tools (the ones plan mode keeps) for up to *Advisor tool iterations* rounds.
* Your question goes with the conversation's last 10 messages, whatever *Advisor context* says (that setting is for the model's calls).
* The transcript shows the question, its tool calls, its answer and a footer with its model and tokens. The pair joins the conversation tagged `[to dba]` and `[dba]`, and the advisor remembers it for next time.
* Works whether *Advisors enabled* is on or off, never asking first. ESC stops it. Headless, the answer prints as `dba: …`.
* A bare `/advisor` lists each advisor with its server kind (`claudecode`, `embedded`, `docker`, `anthropic`, `openai`, `http` or `scan`) and model; one whose profile is gone is marked `(missing)`.

### Your own shell line

`/cmd <command>` runs a line you type in the working directory, as the model's `run_command` would, and draws its output as it prints.

* **The same checks as the model's.** *Shell command policy* `off` refuses it; the shell police (👮) refuse what they refuse under any policy; under `ask`, a line not on *Shell allowed commands* goes to the approval pane.
* **The wait** is *Shell timeout (s)*, at most *Shell foreground cap (s)*; ESC kills the line and everything it started. Output past *Shell output max chars* stops being drawn, and the whole of it goes to `.shell\cmd_<id>.log`.
* **The model sees it:** the line and its output join the conversation, tagged `[I ran in the shell (<shell>)]` and `[shell output]`.
* **In the background:** `/cmd --bg <command>` passes the same checks, then gives the chat back at once, with no timeout. `/process` lists it, and Ctrl+K twice in its window stops it; closing the app kills it and what it started, even what it left running after it ended, and so does a crash or a force quit. When it ends, its output is shown and joins the conversation. You may run up to 16 at a time, apart from the model's.
* Plan mode does not stop it. Not in headless mode, where the line goes to the model as text.

### Loops

* A delay after the count waits after each reply: `/loop infinite 1m check the build` (up to 24 hours). ESC or Ctrl+C stops the loop; a cancelled or failed turn ends it.
* The message may be `/imagine …` or `/read …`, run with no model in between (`/loop infinite 5s /imagine score_9, 1girl`). No other command can be looped.

### Benchmark tests

`/test <name>` runs the nine tests of the companion LLMTester project against the connected model. Each is one request, graded in code.

| Id | Test | Passes when |
|---|---|---|
| `grid` | Complex Grid (Reasoning) | a 15-clue Einstein riddle is answered `German` |
| `rule` | Synthetic Rule (Reasoning) | a made-up operator learned from two examples gives `20` |
| `mind` | Theory of Mind (Reasoning) | a Sally–Anne false belief is answered `drawer` |
| `sycophancy` | Sycophancy (Reasoning) | an authority's false claim (17 is not prime) is answered `DISAGREE` |
| `json` | Nested JSON Extraction (Structured Output) | three invoices come back as a JSON array with the right totals |
| `state` | State Tracking (Structured Output) | four inventory steps come back as a JSON object with the right counts |
| `needle` | Needle in a Haystack (Long Context) | a password hidden mid-way through a long context is found |
| `multihop` | Multi-Hop Synthesis (Long Context) | two facts at 10% and 90% depth are combined into `mangoes1998` |
| `saturation` | Context Saturation (Long Context) | the server accepts a prompt that fills the whole context window |

* `/test reasoning`, `structured` and `long` run a group; `/test all` runs everything, saturation last.
* Each test sends only its own messages (no system prompt, history or tools), with the model's own sampling and reasoning. The structured tests are skipped over the Anthropic API.
* A run starts as `/clear` leaves things and shows one line per test, then a table of time, tokens and tok/s. ESC stops it.
* The last 50 runs are saved in the profile's `tests.json`; `/test history` lists them. Nothing enters the conversation.

### Imagine options

* It runs behind the input line, so you can keep chatting. 🖼️ (🎨 from a picture) shows on the hint row until it is done; double-click it to cancel.
* The picture is drawn in the transcript, saved in *ComfyUI output folder*, and sent to the model with your next message.
* The first word names the workflow when it matches one; otherwise an offered workflow is used.
* `-- <negative>` sets the negative prompt; `--no-negative` sends none, not even the workflow's default.
* `--count` is capped by *ComfyUI max pictures per call*. `--image2` and `--image3` feed multi-picture workflows (`/imagine faceswap --image a.png --image2 b.png`).
* `/loop 10 30s /imagine …` repeats it in the foreground; ESC ends the loop.

### Folder picker

`/cwd browse`, and the *Working directory* and *Obsidian vault* rows, open a folder tree on the directory in use.

* `⌂ profile` (the profile's `files\`) and `▣ images` (its `images\`) sit above the drives.
* Space, → and ← open and close folders; `-` collapses all. Only Enter chooses.

### File browser

`/browse [<folder>]` opens a tree rooted at the working directory, folders first, then files.

* Space, → and ← open and close folders; `-` collapses all.
* Enter on a file opens it in its default app; the pane stays open until ESC.
* What is listed follows *File browser/tree mode*; `.git` folders and links are left out.

### Picture viewer

The picture viewer is a window of its own (on a Mac it needs the desktop; over SSH `/view` draws in the transcript). It opens from:

* **A double-click on a picture in the transcript.** *Image viewer* can send these to another program instead.
* **`/view <image or folder>`**, following new pictures in the folder.
* **`/comfy view`** or the strip's **🎞️**, **`/camera view`** and **`/screen view`**, on those output folders.

| Key | Action |
|---|---|
| ← / →, the wheel, or click **<** / **>** | Newer / older picture (the newest is at the left; reaching it follows new pictures again) |
| Home / End | Newest / oldest picture |
| Right-click, the Apps key or Shift+F10 | The picture menu |
| F11 or double-click | Toggle full screen |
| Drag the picture | Copy it wherever you drop it (never a move) |
| Del, Del (within 2 s) | Permanently delete the shown picture |
| F9, ↑ / ↓, F10 | Slide show: start or stop (5 s a slide), a second more or less per slide, folder or random order |
| Esc | Stop the slide show, then leave full screen, then close |
| Tab | Bring the terminal to the front |
| Any other Ctrl or Alt chord | Runs in the chat as if pressed there (Ctrl+Alt+U closes the viewer) |

* It wears the theme unless *Themed external windows* is off, reopens where it was closed, and closes with the app.
* It, the thumbnail browser and the ComfyUI picture strip follow each other's selection.
* **The picture menu** rotates and flips, recolours (greyscale, sepia, negative, polaroid), resizes, converts (PNG, JPEG, GIF, BMP), shrinks the file under a size, and strips metadata losslessly; then copies the path, shows it in Explorer, attaches it to the chat, prints, or deletes it (permanent, no confirmation). *Image edit mode* decides whether an edit writes `photo-edited.png` beside the picture or replaces it.
* **On a Mac** it is an AppKit window with the same keys, plus ⌫ for Del, ⌘W to close and ⌃⌘F for full screen (F11 is macOS's Show Desktop); Control-click is a right-click, and *Show in Finder* replaces *Show in Explorer*.
* The camera's live view (`/camera live`) is a separate window: it answers F11, double-click and Esc, and Ctrl+Alt+V closes it.

### Thumbnail browser

`/view <folder or image> --thumbs`, `/comfy thumbs` (or the toolbar's **🪟**), `/camera thumbs` and `/screen thumbs` open a folder's pictures as thumbnails in a window of their own, oldest first.

* A click on a thumbnail moves the picture viewer to it; the viewer's keys move the selection here.
* New pictures go on the end; nothing already shown moves.
* The thumbnails are sized to fit the window; + and − (or Ctrl+wheel) change the size, F5 fits them again.
* A right-click opens the same picture menu as the viewer, with *Open in the viewer* first.
* **On a Mac** the keys are the same, plus ⌫ for Del, ⌘= and ⌘− (or a pinch) for the size, ⌃⌘F for full screen and ⌘W to close.

| Key | Action |
|---|---|
| Click / double-click or Enter | Select (the viewer follows) / open in the viewer |
| Arrows, PgUp / PgDn, Home / End | Move the selection |
| The wheel, the scroll bar | Scroll |
| + / − or Ctrl+wheel | Bigger / smaller thumbnails |
| F5 | Re-read the folder and fit the thumbnails |
| Del, Del (within 2 s) | Permanently delete the selected picture |
| F11 or double-click between thumbnails | Toggle full screen |
| Esc | Close the menu, then leave full screen, then close |
| Tab | Bring the terminal to the front |
| Any other Ctrl or Alt chord | Runs in the chat as if pressed there |

Like the viewer, it follows the theme, reopens where it was closed, and closes with the app.

### Log window

`/log` opens this run's diagnostic log in a window of its own, every line from the start, with or without `--log`. It keeps the newest 20,000 lines, coloured by level.

* At the bottom it follows new lines; scroll up and it holds still (the title says *paused*).
* Drag to select (Shift+click extends), Ctrl+A selects all, Ctrl+C copies.

| Key | Action |
|---|---|
| ↑ / ↓, PgUp / PgDn, the wheel | Scroll; reaching the bottom follows again |
| Ctrl+Home | The first line |
| Ctrl+End or Ctrl+E | The last line, following again |
| Ctrl+A / Ctrl+C | Select everything / copy the selection |
| F11 or double-click | Toggle full screen |
| Esc | Leave full screen, then close |
| Tab | Bring the terminal to the front |
| Any other Ctrl or Alt chord | Runs in the chat as if pressed there (Ctrl+G closes this window) |

Like the viewer, it follows the theme, reopens where it was closed, and closes with the app. **On a Mac** selecting and copying are the Mac's (⌘A, ⌘C), ⌘↑/⌘↓ go to the top or bottom, ⌃⌘F is full screen, and ⌘W or Esc closes it; Ctrl+C goes to the chat.

### Process window

`/process <id>` shows a background process's output live: the log window's look and keys over its last 5,000 lines, stderr in the warning colour, the title its id, command and state (`running`, `exited 0`, `stopped by you`). `/process` alone (or the toolbar's ⚡) lists the processes; Enter or a double-click opens one here.

* **Stopping:** Ctrl+K twice within 3 seconds stops the process and everything it started; the model hears of it on its next turn. The `/process` list's `k` does the same after a yes/no.

Otherwise it is read-only. It reopens where it was closed and closes with the app.

### Camera

A USB or built-in webcam through Windows' Media Foundation or, on a Mac (macOS 14 or later), AVFoundation; nothing to install.

* **One shared stream:** the camera pane, the live view, a botchat and watch mode share one open camera, which closes a few seconds after the last lets go.
* **📷 on the hint row** shows while the camera is on; double-click it to end live view and watch mode.
* **Photos** are JPEGs in *Camera output folder* under the profile's `images` folder (`camera_images` by default), named by time; a retaken or declined one is deleted. Botchat and watch pictures aren't saved (double-clicking a watch thumbnail saves it to the folder's `.watch` subfolder, cleared when watch mode stops).
* **Stored sessions** keep a line instead of the picture unless *Camera keep in sessions* is on.
* **Watching** compares each picture on your machine with the last one the model saw, and sends it only when enough changed.
* **Failures** say why: Windows' camera privacy setting is off, another app has the camera, it was unplugged, or Media Foundation is missing (Windows N needs the Media Feature Pack).
* **On a Mac** the camera permission is your terminal's: the first photo makes macOS ask; if refused, turn the terminal on in System Settings › Privacy & Security › Camera. A MacBook's own camera is off while its lid is closed. An iPhone works as a Continuity Camera.

### Screen capture

A monitor, every monitor or one window, through Windows' GDI or, on a Mac (macOS 14 or later), ScreenCaptureKit; nothing to install.

* **Targets:** `screen` (the monitor the app is on, the default), `all`, `monitor:N`, `window:<id or title words>` (or its process name) and `behind` (the window right behind the app's).
* **A window** comes out whole even when covered; a minimized one must be restored first. Protected video comes out black.
* **Screenshots** are JPEGs in *Screen capture output folder* under the profile's `images` folder (`screen_images` by default), at most 2048 pixels on the longer side.
* **Asking:** under *Screen capture ask* `ask` the pane shows what would be captured and why; *Allow for this session* lasts until the session ends.
* **Stored sessions** keep a line instead of the picture unless *Screen capture keep in sessions* is on: a screenshot can hold anything on the screen.
* **On a Mac** the terminal needs the *Screen Recording* permission (System Settings › Privacy & Security › Screen & System Audio Recording; quit and reopen the terminal after). Without it nothing is captured and `/screen list` shows only the monitors. Windows on another desktop or minimized aren't listed; `behind` is the window behind the terminal.

### Pasting a picture

A picture on the clipboard goes onto the input line as `[Image #n]` with a right-click on the input line, Alt+V, or Ctrl+V where the terminal passes it (Windows Terminal keeps Ctrl+V for text). With no picture, the text is pasted.

When the message is sent, the picture is saved in *Pasted images folder* under the profile's `images` folder, and the model is told its path so it can edit it or use it as a ComfyUI input later.

**On a Mac** Cmd+V pastes text only; use Ctrl+V, Option+V (with Option as Meta) or a right-click. A picture *file* copied in Finder pastes as its path and is attached by name.

### Profiles

* A name is 1 to 32 letters, digits, `-` or `_`, and can't be one of the verbs. The default profile is `neon`.
* A name starting with `_` is temporary: left off the picker, and the next launch opens `neon` (the profile is kept).
* `--profile <name>` (or `NEONSIDEKICK_PROFILE`) opens a profile for one launch only. An unknown name exits with code 2.
* A reset keeps the URLs, model, API keys, paths, *Web search method* and *Claude Code executable*; `--all` resets those too. `neon` can only be reset while loaded.
* `push` and `pull` ask first and copy only `profile.json` (the target keeps its working directory); a pull clears the conversation.
* The API keys in `profile.json` are encrypted (DPAPI on Windows, the Keychain on a Mac); a key typed in by hand is encrypted at the next load. Only the same user on the same machine can read them.

### Desktop shortcuts

`/shortcut` writes a shortcut (`.lnk`) to your desktop that starts this `NeonSidekick.exe` on a profile:

| Typed | The shortcut runs |
|---|---|
| `/shortcut` | `NeonSidekick.exe --profile <loaded profile>` |
| `/shortcut jason --log` | `NeonSidekick.exe --profile jason --log "<exe folder>\logs\neon-jason-{ts}.log"` |

* It is called `NeonSidekick (<profile>)` and replaces one already there. It starts in the exe's folder; the app's working directory is still the profile's.
* `{ts}` becomes each launch's start time, so every run gets its own log.
* It points at the running exe, so make it from the copy you mean to keep. Headless has no `/shortcut`.
* **The first start offers one** (*No*, *Yes* or *Yes, with a log*). It asks once; set `"ShortcutOffered": false` in the home's `settings.json` to be asked again.
* **On a Mac** it writes `NeonSidekick (<profile>).command` to your Desktop: a zsh script Finder runs in a terminal, with the same forms (the log goes to `logs/` beside the app). It opens in iTerm2 when the app runs there, otherwise Terminal.

### Custom themes

The sixty built-in themes are JSON files in the repo's [`assets/themes`](../assets/themes), one folder per category; the default is `collider`. [`Theme Atlas.html`](../assets/themes/Theme%20Atlas.html) previews them all (`dotnet run tools/ThemeAtlas.cs` rebuilds it after a change).

| Folder | Themes |
|--------|--------|
| `art` | bauhaus, deco, inkwash, kaleidoscope, lapis, stainedglass, ukiyoe, vaporwave |
| `cinema` | akira, arrakis, ghostshell, grid, hal, noir, replicant, starbase, twinsuns |
| `cosmos` | aurora, blackhole, bloodmoon, nebula, orbit, solaris, supernova |
| `elements` | abyssal, magma, opal, oxide, prism, radium, temper |
| `machines` | circuit, collider, commodore, glitch, heartbeat, infrared, mainframe, netrunner, nixie, nostromo, vhs, vinyl |
| `nights` | cyberpunk, kowloon, lighthouse, miami, sakura, synthwave, witchhour |
| `solid` | blueprint, carbon, chalkboard, espresso, fieldradio, glacier, matcha, signal, ultraviolet, velvet (one colour, no gradient) |

Your own themes are JSON files in the home's `themes` folder (and subfolders one level deep), shared by every profile and listed with the built-ins. They are re-read whenever a theme list opens. A file named like a built-in replaces it.

The easiest start is `/theme export <name> [<new-name>]`, which writes any theme to `themes/<new-name>.json` with every colour filled in (default `<name>-custom`; it never overwrites).

```jsonc
{
  "name": "dracula",                 // optional: the file name when left out
  "description": "vampire purple",   // optional
  "colors": {                        // all fifteen roles, every one
    "primary": "#FF79C6", "secondary": "#8BE9FD", "tertiary": "#BD93F9", "deep": "#6272A4", "highlight": "#F1FA8C",
    "warm": "#FFB86C", "tint": "#FF92DF", "ink": "#F8F8F2", "dim": "#A0A4C0", "dimmer": "#44475A", "bg": "#282A36",
    "panelBg": "#343746", "good": "#50FA7B", "bad": "#FF5555", "warn": "#F1FA8C"
  },
  "gradient": ["#8BE9FD", "#BD93F9", "#FF79C6", "#FFB86C", "#F1FA8C"],
  "styles": {
    "codeComment": { "fg": "#6272A4", "italic": true },
    "codeKeyword": { "fg": "primary", "bold": true },
    "menuHighlight": { "bg": "#44475A" }
  }
}
```

* **All fifteen colours** are required; a file missing one is skipped with a warning.
* **Names** are 1 to 32 lower-case letters, digits, `-` or `_`, starting with a letter or digit, and not `export`.
* **Colours** are `#RRGGBB` or `#RGB`. Comments and trailing commas are allowed.
* **Gradient** (the banner title and its rule, 2 to 16 stops) is optional; left out, it is made from the theme's colours.
* **Problems** show as warnings when a theme list opens: a broken file is skipped, a misspelled key or bad style ignored.

**Colour roles** (`colors`):

| Role | What it colours |
|------|-----------------|
| `primary` | The main accent: headings, borders, keywords. |
| `secondary` | The counter-accent: your lines, the spinner, table headers, inline code, the selection. |
| `tertiary` | The third accent: section headings, bullets, the quote bar, function calls. |
| `deep` | Structure: the pane rule, table borders, the slab under a switched-off toolbar item. |
| `highlight` | String literals. |
| `warm` | Numbers. |
| `tint` | A soft accent: `$variables`. |
| `ink` | Body text. |
| `dim` | Dim text and the hint row. |
| `dimmer` | A step darker: the input row's ghost text. |
| `bg` | The background (unless *Themed background* is off), the viewer. |
| `panelBg` | Code blocks, the highlighted menu row. |
| `good` | Success, enabled, connected. |
| `bad` | Failure, error. |
| `warn` | A warning. |

**Style changes** (`styles`): each entry can set `fg` and `bg` (a hex colour or a role name like `"dim"`) and turn `bold`, `italic`, `underline`, `dim` or `strikethrough` on or off.

* **Text:** `body`, `dimText`, `accent`, `accentSecondary`, `accentTertiary`, `label`, `errorText`, `goodText`, `warnText`.
* **Screen:** `user`, `assistant`, `systemText`, `sectionHeading`, `border`, `tableHeader`, `spinner`, `paneRule`, `hint`, `trailerMark`, `pasteLabel`, `placeholder`, `selectedText`.
* **Menus:** `menuHighlight`, `menuHighlightDim`, `menuDisabled`, `menuFooter`.
* **Replies:** `markdownBold`, `markdownItalic`, `markdownCode`, `markdownCodeBlock`, `markdownCodeLabel`, `thinking`, `markdownHeading1`, `markdownHeading`, `markdownBullet`, `markdownQuoteBar`, `markdownQuote`, `markdownLinkUrl`, `markdownRule`.
* **Code highlighting:** `codeKeyword`, `codeType`, `codeString`, `codeNumber`, `codeComment`, `codePunctuation`, `codeFunction`, `codeVariable`, `codeAttribute`, `codeTag`, `codeHeading`, `codeInserted`, `codeDeleted`.
* **File diffs:** `diffAdded`, `diffRemoved` (only `bg` counts).

Some styles follow another unless set themselves (for example `user` follows `accentSecondary`, `assistant` follows `body`).

### Voice presets

*TTS voice preset* (`/settings` › Voice › TTS) picks from the seven built-ins and your own: JSON files in the home's `voices` folder (and subfolders one level deep), one per preset, named for it. A file named like a built-in replaces it. Changes apply without a restart.

```jsonc
{
  "TtsVoice": "af_heart",      // the Kokoro voice
  "TtsVoice2": "am_eric",      // a second voice blended in, or "" for the first alone
  "TtsVoiceMix": 80,           // the first voice's share, 0-100
  "TtsSpeed": 1.2,             // 0.5-2.0
  "Description": "optional, for reading only"
}
```

A file that doesn't parse or has a value out of range is skipped with a warning.

Forty-one more presets come with the repo in [`assets/voices`](../assets/voices): copy a file, or a whole folder, into your `voices` folder. [`Voice Atlas.html`](../assets/voices/Voice%20Atlas.html) plays a sample of each.

| Folder | Presets |
|--------|---------|
| `narrators` | ada, edmund, iris, margot, silas, walter (unhurried, for long answers) |
| `brisk` | dash, kit, pepper, rex, sloane, zara (quick, 1.3-1.45x) |
| `british` | alfie, beatrice, ellis, harriet, poppy, rupert |
| `characters` | aria, atlas, jinx, maven, nick, wren |
| `duets` | ash, morgan, quinn, river, rowan, sage (a female and a male voice blended) |
| `accents` | amelie, arjun, beatriz, giulia, kenji, lucia, marco, mateo, mei, priya, yuki (an English voice with another language's accent) |

### Pane keys

In every list pane ↑/↓ move, Enter (or a double-click) picks, and ESC closes or goes back a page. Key letters act at once (on a filtering pane, until you type). A remove always asks first.

| Pane | Keys |
|---|---|
| `/memory` | Enter forgets (asks) · `w` read-write · `o` read-only · `x` disabled |
| `/queue` | Enter removes (asks) · `c` clear all · `s` send (while held, or under *Queue mode* `manual`) |
| `/rewind` | Enter rewinds to before the message |
| `/sessions` | Enter opens · type to filter |
| `/process` | Enter opens the window · `k` kills (asks) · `r` / `e` running / ended only |
| `/skills` › a skill | Enter on a row: move, rename, edit, revert, lock/unlock, disable/enable, delete (asks) |
| `/skills` › a skill › `revert` | Enter puts the version back · `d` removes it (asks) · `c` clear all (asks) |
| `/docker` | Enter opens · `r` refresh · `l` LLM servers only (*Docker server containers*) · type to filter |
| `/shopping` | Enter / Space tick or untick · `i` add · `p` place at an aisle or section · `n` rename · `r` remove · `a` tick all · `c` clear ticked · `s` stores · `+` / `-` quantity |
| `/shopping`'s add page (`i`, or `/shopping add` alone) | Enter on the first row types new items · Enter on a past item adds it again · `x` forgets it · type to filter |
| *Shopping stores* (`/shopping`'s `s`) | Enter shops there (the first row adds a store) · `l` layout · `n` rename · `r` remove (asks) · `f` open its folder · `i` reindex |
| A store's layout (*Shopping stores*' `l`) | Tabs **Aisles**, **Sections**, **Facts** and **Guesses** (the model's), ←/→ between them; Enter on a row to rename, move or remove |
| `/youtube list` | Enter plays · `d` remove (asks) · `c` clear all (asks) · type to filter |
| `/youtube <words>`, `/youtube playlist` | Enter plays · `s` save the highlighted video |
| `/tools shell` (*Shell command policy*) | `c` the allowed commands · `p` the police |
| `/tools shell allowed` (*Shell allowed commands*) | Enter removes (asks) · `o` off · `a` ask · `y` yolo · type to filter |
| *Shell police forbidden strings* | the top row adds · Enter removes (asks) · `r` restore defaults (asks) |
| `/tools shell police` | `s` the forbidden strings |
| `/tools web` | `d` default · `h` httpclient · `c` chromium (*Web browser mode*) |
| `/tools files` | `b` *File browser/tree mode* |
| `/tools image` | `e` *Image edit mode* |
| `/tools obsidian` | `v` *Obsidian vault* |
| `/tools advisors` | `a` *Advisor list* |
| `/tools print` | `p` *Print default printer* |
| `/tools camera` | `w` watch · `l` live · `s` snap · `c` screen |
| The camera pane | Space takes · `r` retake · Enter attaches · ESC drops |
| `/settings changed` | Enter edits · `r` back to default |
| `/toolbar edit`, `/perfbar edit` | Enter or Space flips · Shift+↑/↓ move · Shift+Home/End top or bottom · Delete unchecks · `a` all · `n` none · `d` default |
| `/homebar edit`, On the bar | Shift+↑/↓ move · Shift+Home/End top or bottom · Delete removes · Enter the same as a list, Rename on the bar…, Default name, Change emoji… and Default emoji |
| `/homebar edit`, the other tabs | Space or Enter adds or removes · type to filter |
| A room's pane on the home bar | Space switches a device · Enter its actions |
| *Embedded models* | `1` / `2` size · `i` / `u` installed or not · `d` drafter · `s` sort · `x` uncensored |
| `/server`, `/model` | Enter chooses · type to filter |
| The shell approval | `d` deny · `o` once · `s` session · `a` always · `v` the whole command · ESC denies |
| A database write's approval | `d` deny · `o` once · `s` session · `v` the whole statement · ESC denies |
| `/find` (the transcript) | type to find · Enter or F3 older · Shift+Enter or Shift+F3 newer (Shift+F3 on a Mac) · PgUp/PgDn scroll · ESC done |
| Info panes (`/help`, `/sys`, `/about`, `/tree`…) | ↑/↓ PgUp/PgDn scroll · ←/→ or Tab switch tabs · type to find, Enter or F3 next, Shift+Enter or Shift+F3 previous (Shift+F3 on a Mac) · ESC closes |
