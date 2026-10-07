# Slash commands

Every command, with its forms. The [README](../README.md#useful-first-commands) has the few to start with; in the app, `/help` lists them all.

Type `/` to list every command with a summary; after a command and a space, its arguments are listed where the app can offer them. `//` is an unlisted shortcut for `/settings`. Forms are written one way here, on `/help` and for `neon_help`: words typed as they are, `<placeholders>` to fill in, `[optional]` parts, and `a|b` for a choice. See [Keyboard shortcuts](SETTINGS.md#keyboard-shortcuts) for the keys that run commands, and [Commands typed during a reply](SETTINGS.md#commands-typed-during-a-reply) for what runs mid-reply.

## Contents

- [All commands](#all-commands)
- [Command details](#command-details)
  - [Plan mode](#plan-mode)
  - [Bot conversations](#bot-conversations)
  - [Claude Code from the chat](#claude-code-from-the-chat)
  - [Loops](#loops)
  - [Benchmark tests](#benchmark-tests)
  - [Imagine options](#imagine-options)
  - [Folder picker](#folder-picker)
  - [Picture viewer](#picture-viewer)
  - [Thumbnail browser](#thumbnail-browser)
  - [Log window](#log-window)
  - [Process window](#process-window)
  - [Camera](#camera)
  - [Screen capture](#screen-capture)
  - [Profiles](#profiles)
  - [Custom themes](#custom-themes)
  - [Voice presets](#voice-presets)
  - [Pane keys](#pane-keys)

## All commands

| Command | What it does |
|---|---|
| `/about` | Shows the version, the GitHub repository, runtime, folders, components and licence. |
| `/claude <message>` | Sends the message to Claude Code and streams its reply into the transcript. See Claude Code from the chat. |
| `/claude new` | Starts a new Claude conversation; the next `/claude` begins it. |
| `/clear` | Starts a new conversation and clears the screen. |
| `/cmdcopy <profile> [overwrite] [--history]` | Copies *Shell allowed commands* into another profile (added, or replacing with `overwrite`). `--history` copies the command history instead (refused when that profile has *Keep command history* off). |
| `/keycheck` | Lists the app's key chords and whether another program holds each as a global hotkey, the held ones first, in a pane. A held chord never reaches the app. Only hotkeys registered with Windows show; a keyboard hook (AutoHotkey, PowerToys Keyboard Manager) or a Windows Terminal key binding can still take a key. |
| `/keycopy <profile>` | Copies the *LLM API key*, *Anthropic API key*, *OpenAI API key*, *Home Assistant API key* and *YouTube API key* into another profile after a confirmation, mirrored: a key unset here clears theirs. Keys set only by environment variable aren't copied. |
| `/srvcopy <profile>` | Copies the server settings into another profile after a confirmation, replacing theirs: *LLM server scan mode*, *LLM URL* and *LLM model*; *Embedded servers enabled*, *Embedded backend* and the embedded context size, GPU layers, VRAM budget, VRAM only, vision and drafter; *Docker servers enabled*, *Docker server containers* and the Docker server timings and stop on exit; *Anthropic API*, *OpenAI API* and *Claude CLI server*. The saved values are copied, not a `--url` or variable that only overrides this run. Installed embedded models are shared by every profile already; the keys are `/keycopy`'s. |
| `/cmdclear` | Clears the command history, stored and in memory, after a confirmation. |
| `/cmdlist` | Opens *Shell allowed commands*: Enter removes a prefix after a yes; the ask and yolo buttons (`a`, `y`) switch *Shell command policy*; typing filters the list. |
| `/police` | Opens the on/off page for *Shell police*; its strings button (`s`) opens *Shell police forbidden strings*. |
| `/compact [<focus>]` | Shrinks the context; a focus tells the summary what to concentrate on. |
| `/copy [<n>\|all] [--thinking]` | Copies the last reply (or the last *n*, or the whole transcript) as Markdown. `--thinking` includes the thinking, quoted under `💭 **Thinking**`. |
| `/cwd [<path>\|~\|browse]` | Shows or changes the working directory. A path that starts with a drive (`D:\`) completes its folders as you type. `~` returns to the profile's `files\`; `browse` opens the [folder picker](#folder-picker). |
| `/camera` | Opens the camera pane: Space takes the photo, R retakes, Enter puts it on the input line as `[Image #N]`, ESC drops it. Without the pane it snaps at once. See Camera. |
| `/camera snap` | Takes a photo at once and puts it on the input line. |
| `/camera list` | Lists the cameras in a pane, numbered, the chosen one marked. |
| `/camera use <n>\|<name>` | Chooses the camera by number or name (*Camera device*). |
| `/camera live` | Shows the camera live in its own window until you close it or `/camera off`. |
| `/camera watch [<seconds>\|off]` | Looks every *Camera watch interval* (or the seconds given); a picture that changed rides your next message. Never on at startup. |
| `/camera off` | Ends `/camera live` and watch mode; the camera closes a few seconds later. |
| `/screen` | Captures the monitor the app is on and puts the screenshot on the input line as `[Image #N]`. After `/screen ` the list offers the targets, then the monitors after `monitor:` and the open windows after `window:` (narrowed by id, title or program). See Screen capture. |
| `/screen all\|desktop` | Captures every monitor as one picture. |
| `/screen monitor:<n>` | Captures monitor n (`monitor <n>` works too). |
| `/screen window:<id>\|<title-words>` | Captures one window, even when another covers it: by its id, or words of its title (or its program). |
| `/screen behind` | Captures the window right behind the app's own: the one you were just in. |
| `/screen list` | Lists the monitors and the windows in a pane, front to back, with the target that names each. |
| `/docker` | Docker Desktop's containers on a pane, with state, health and ports. Enter offers what fits: stop, restart or pause (asking first), start or unpause, the last 50 log lines, open a port in the browser, copy the id. |
| `/docker ps [<filter>]` | Lists the containers, or those whose name or image holds the filter. |
| `/docker status` | Docker Desktop's and the engine's versions, with the container counts. |
| `/docker logs <container> [<lines>]` | A container's last lines in a pane (50 by default, up to 2000). |
| `/docker stats [<container>]` | CPU, memory, network and disk use of one or every running container. |
| `/docker start\|stop\|restart\|pause\|unpause <container>` | Acts on one container by name, part of a name or id. Your own hand: *Docker writes* doesn't apply and nothing is asked, but every change is logged. |
| `/draft` | Writes the next message in your editor; it is sent when you save and close. |
| `/echo <text>` | Prints a line as a reply (spoken when speech is on). |
| `/exit` | Exits the app. |
| `/explore [<folder>]` | Opens the working directory in your file browser. |
| `/gituser [force]` | Writes *GitLib email* and *GitLib name* into the repository's config. An existing `[user]` section stays unless `force`. Does nothing while *GitLib tools* is off. |
| `/ha` | Home Assistant at a glance: lights on per room, the TV, temperatures, motion, low batteries and to-do lists. |
| `/ha on\|off\|toggle <name> [<brightness>]` | Switches a room, light, switch or the TV (`/ha on den 40%`, `/ha off kitchen and hallway`). |
| `/ha scene <name>` | Activates a scene (`/ha scene den relax`). |
| `/ha tv on\|off\|mute\|unmute\|up\|down` | Controls the only media player. |
| `/ha tv vol\|volume <level>` | Sets its volume, 0 to 100. |
| `/ha tv source <name>` | Switches its source (`/ha tv source hdmi 2`). |
| `/ha states [<filter>]` | Lists entities with ids and states in a pane, by domain, name words or entity id; an entity id shows all its attributes. |
| `/ha say <sentence>` | Hands a sentence to Home Assistant's Assist agent. |
| `/header [on\|off]` | Shows or hides the banner (*Show header*), from the next clear. Alone, it flips the setting. |
| `/help` | The commands, on the Basic and Advanced tabs (each with a short description and its forms), and the keys. |
| `/interrupt [on\|off]` | Toggles the wake-word interrupt during a spoken reply. |
| `/learn [<note>]` | Writes or improves a skill in the background from the last turn. |
| `/learn sessions [<n>\|<search>]` | The same from stored sessions: the newest 5, the newest n (up to 20), or those a search finds. |
| `/log` | Opens the [log window](#log-window). Works without `--log`. |
| `/log --file` | Opens the `--log` file in your editor (only when started with `--log <path>`; the argument list offers `--file` only then). |
| `/loop <count>\|infinite [<delay>] <message>` | Sends the message that many times, or until ESC or Ctrl+C, waiting for each reply. See Loops. |
| `/loop <count>\|infinite [<delay>] /imagine\|/speak <arguments>` | Runs `/imagine` or `/speak` itself each pass. |
| `/plan <requirement>` | Researches with read-only tools and presents a plan before anything changes. See Plan mode. |
| `/botchat [<profile> ...] [--] [<topic>]` | Lets profiles talk to each other until you stop them; `--` makes the rest the topic. See Bot conversations. |
| `/botchat --resume [<line>]` | Carries on the last bot conversation. |
| `/botchat --kill` | Stops the extra embedded servers the bots started. |
| `/expand` | Unfolds every tool run, code block, diff and thinking block, now and from here on. Ctrl+O switches between this and `/collapse`. |
| `/collapse` | Folds them again. |
| `/find [<words>]` | Finds words in the transcript: a find row opens over the input row and the transcript scrolls to the newest match, every match marked. Typing changes the text; Enter or F3 goes to the next match up (older), Shift+Enter or Shift+F3 back down, PgUp/PgDn scroll. The first Enter after the text changes (and the words given here, at once) opens the folded tool runs, code blocks, diffs and thinking blocks that hold it; they fold again when the find ends. ESC ends it at the bottom. Ctrl+Shift+F runs it too (in Windows Terminal, once its own Ctrl+Shift+F find is unbound). A word the window wraps across two rows is not found. |
| `/mcp` | Connects MCP servers and switches their tools. On the Tools tab, typing narrows the list to the tools whose name or description holds the text; Backspace erases, ESC clears it, the next ESC closes. |
| `/memory` | Lists memories on a pane, one line each, with the highlighted one in full under the list (Enter forgets it after a yes); **read-write** (`w`), **read-only** (`o`) and **disabled** (`x`) on its title row set *Memory mode*. |
| `/memory read-write\|read-only\|disabled\|on\|off` | Sets *Memory mode* (`on` is read-write, `off` disabled). |
| `/memory forget\|edit` | `forget` forgets all, after a confirmation; `edit` opens `memory.json` in your editor. |
| `/memory copy <profile> [overwrite]` | Adds them to another profile's memory (or replaces it with `overwrite`), after a confirmation. |
| `/model [<id>]` | Picks or sets the model. The list is A to Z with the cursor on the model in use; type to narrow it to the ids holding the text (Backspace erases, ESC clears it, the next ESC keeps the model). On the embedded LLM, lists the installed models, and the argument list offers their ids. |
| `/new` | Starts a new conversation without clearing the screen. |
| `/operata [reset]` | Edits `operata.md` (the operating rules) in your editor, or resets it. A missing file is created with the rules in use now, the sentences for the tools that are on included; from then on it stands as written. |
| `/operata copy <profile> [force]` | Copies it to another profile (`force` replaces theirs). |
| `/perfbar [off\|text\|gauge\|spark\|led]` | Hides the performance bar, or brings it back with its last meters; a look name sets that look and shows it. |
| `/persona [reset]` | The same for `persona.md` (the personality; seeded with the built-in persona). |
| `/persona copy <profile> [force]` | Copies it to another profile (`force` replaces theirs). |
| `/print <file> [printer=<name>] [copies=<n>] [pages=<range>] [landscape]` | Prints a file from the working directory (see [Printing](TOOLS.md#printing)). The printer matches by name or part of it; quote a name with spaces. *Print action policy* never applies. Printing needs Windows: on a Mac every `/print` says so. |
| `/print reply [<options>]` | Prints the last reply as formatted Markdown. |
| `/print printers` | Lists the printers in a pane, marking the Windows default and *Print default printer*. `/print` alone shows its usage and the same list. |
| `/pdf <file> [to=<path>] [paper=letter\|a4\|legal] [landscape] [overwrite]` | Makes a PDF in the working directory from Markdown, text or code, HTML or a picture, beside the file unless `to=` says (see [Making PDFs](TOOLS.md#making-pdfs)). On a Mac only the browser makes it. |
| `/pdf <url> [to=<path>] [overwrite]` | Makes a PDF of a web page as the browser shows it; *Web browser network mode* still applies. |
| `/pdf reply [<options>]` | Makes a PDF of the last reply as formatted Markdown. |
| `/process` | Lists the background processes the model started (`run_command`'s `background`) in a pane: id, state, elapsed, shell and command. Enter or a double-click on a row opens it in the [process window](#process-window); the **✖ kill** button (or `k`) stops the highlighted one after a yes/no. With none started, the pane opens on that line. |
| `/process <id>` | Shows one process's output live in the [process window](#process-window) (any unique start of the id; Tab completes it). Another id switches the window. |
| `/profile [<name>]` | Switches profiles: a picker alone (a typed letter jumps to the next profile starting with it), or straight to the one named. See Profiles. |
| `/profile add\|delete <name>` | Creates or deletes a profile. |
| `/profile rename <name> <new-name>` | Renames a profile. |
| `/profile reset [<name>] [--all]` | Resets a profile to the defaults, keeping its URLs, paths and keys unless `--all`. |
| `/profile push\|pull <name>` | Copies its settings to (`push`) or from (`pull`) another. |
| `/profile edit\|reload` | `edit` opens `profile.json`; `reload` reads it back and reconnects what changed. |
| `/queue [clear]` | Lists and prunes the queued messages (Enter removes one after a yes; `⊠ clear all` or `c` drops them all). While the queue is held (*Queue cancel mode* `hold`, after a cancelled reply), `➤ send` or `s` sends the next one now, and its reply's end releases the hold. `/queue clear` drops them without the pane. |
| `/reasoning [none\|low\|medium\|high\|xhigh]` | Picks the reasoning effort (`none`, `low`, `medium`, `high`, `xhigh`). |
| `/rewind [<n>]` | Goes back to an earlier message: pick one (n back), confirm, and it and everything after leave the conversation (and the stored session), its text returning to the input row, pictures and pastes included. Only the conversation rewinds; the confirmation names the tools whose changes stay. Compacted messages can't be picked. |
| `/remember <text>` | Adds a memory. |
| `/rename [<name>]` | Renames the current session, as `/sessions title` does; on its own it opens the rename box with the current name. |
| `/sampling` | Edits the per-model sampling on a pane. See [Sampling per model](SETTINGS.md#sampling-per-model). |
| `/sampling <field> <value>\|clear` | Sets or clears one of the connected model's values. |
| `/sampling extra <json>\|clear` | Sets or clears its extra request body. |
| `/sampling clear` | Clears every override of the connected model. |
| `/server [<url>\|embedded\|claude-cli\|docker\|docker:<container>]` | Picks an LLM server (found, Anthropic API, OpenAI API, Claude CLI, installed embedded models, chosen Docker containers) or sets one by URL, then the model and reasoning, with one reconnect. `embedded`, `claude-cli`, `docker` and `docker:<container>` go straight to those. A container serves its own model, so no model picker follows. The argument list offers each of those words while it applies. |
| `/sessions [<id>]` | Lists the stored sessions, or restores one by its id. |
| `/sessions purge <id>\|all` | Deletes one stored session, or every one. |
| `/sessions purge older <age>` | Deletes the sessions older than an age: days (`30`) or a duration (`12h`, `90m`, `2 hours`, `1d 6h`). |
| `/sessions title [<text>]` | Renames the current session; alone it opens a box with the current name. |
| `/settings`, `//` | Edits and saves the settings. A letter typed on a tab searches every setting. |
| `/settings <words>` | Searches every setting (`/settings`, `/tools`, `/skills`, `/mcp`) by its name, tab or description; Enter edits the row found. |
| `/settings changed` | Lists the settings that are not their defaults, with each default; Enter edits, R puts the row back to its default. A changed value reads in the accent colour on every settings tab. |
| `/skills` | Lists the skills and edits the skill, reflection and project-file settings. On the Offered tab each skill shows its version after the scope (`v1` as written, one more for each older text kept in `skills.db`; blank for an external skill), and typing narrows the list to the skills whose name or description holds the text; Backspace erases, ESC clears it, the next ESC closes. |
| `/skills add <source> [--global\|--profile]` | Installs an [Agent Skill](https://agentskills.io) from the web after a preview: search words, `owner/repo[/skill]`, a GitHub URL or a `.zip` URL. Refused during a reply. See [Installing skills](SETTINGS.md#installing-skills). |
| `/skills purge list\|commit <age>` | `list` lists the skills unused for that long; `commit` deletes them, folder and record, after a yes/no. See [Skill records and purging unused skills](SETTINGS.md#skill-records-and-purging-unused-skills). |
| `/speak [<file> [<n>]\|<n>]` | Reads a text file from the working directory aloud. Alone it resumes; a number starts at that sentence. |
| `/splash` | Starts a new conversation and shows the splash screen. |
| `/stt [on\|off]` | Toggles voice input. |
| `/sys` | Shows the system prompt and the tools sent to the model. |
| `/terminal [<folder>]` | Opens a new Windows Terminal window in the working directory, or in a folder under it (Tab completes the folder). Without Windows Terminal it opens a console window there. On a Mac it needs Windows for now: open Terminal or iTerm2 yourself. |
| `/test [<id>\|reasoning\|structured\|long\|all\|history]` | Runs benchmark tests against the connected model. Alone, lists them with their last verdicts. See Benchmark tests. |
| `/theme [<name>]` | Switches the colour theme, built-in or [custom](#custom-themes); alone, opens a picker with a live preview (79+ columns); a typed letter jumps to the next theme starting with it. Nothing changes until Enter; during a reply it waits. |
| `/theme export <name> [<new-name>]` | Writes a theme to the `themes` folder as a file to edit (see [Custom themes](#custom-themes)). |
| `/timer [<duration> [<name>]]` | Lists timers, or starts one (`10m`, `90s`, `1h30m`). |
| `/timer stop <name>\|all` | Stops one timer, or every one. |
| `/toolbar [on\|off]` | Hides the toolbar, or brings it back with its last items (the default ten the first time). |
| `/tools` | Switches the model's tools on or off, and edits each tool group's settings on its own tab. On the Offered tab, typing narrows the list to the tools whose name or description holds the text (`haiku`); Backspace erases, ESC clears it, the next ESC closes. |
| `/tools <group>` | Opens one group's switch: `shell` (the *Shell command policy* picker), `files`, `web`, `claude`, `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `sqlite`, `postgres`, `unc`, `ha`, `comfy`, `camera` or `print`. The toolbar's tool items run it. `web`'s page has default, httpclient and chromium buttons (`d`, `h`, `c`) for *Web browser mode*; `camera`'s has a **watch** button (`w`) that turns `/camera watch` on or off. |
| `/tree [<path>]` | Shows a tree of the working directory in a pane (hidden entries only under *File browser/tree mode* `show-hidden`; `.git` only when named). |
| `/tts [on\|off]` | Toggles speech output. |
| `/usage` | Token usage and performance; `~` marks an estimated reasoning count. |
| `/vault [<path>]` | Shows a tree of the *Obsidian vault* (or a folder in it) in a pane, like `/tree`. |
| `/view <image>\|<folder>` | Opens an image (or a folder's newest picture) in the picture viewer. |
| `/view <image> --chat` | Draws it in the transcript instead. |
| `/view <image>\|<folder> --thumbs` | Opens the folder (an image's folder, with the image selected) as thumbnails in a window beside the picture viewer, in step with it (see Thumbnail browser). Either flag can be the first or last word. |
| `/imagine [<workflow>] <prompt> [-- <negative>\|--no-negative]` | Generates a picture on ComfyUI from your prompt exactly as typed. See Imagine options. |
| `/imagine <prompt> [--seed <n>] [--size <w>x<h>] [--steps <n>] [--count <n>]` | Its seed, size, steps and count. |
| `/imagine <prompt> [--cfg <x>] [--denoise <x>] [--image <path>] [--image2 <path>] [--image3 <path>]` | Its guidance, denoise strength and up to three input pictures. |
| `/comfy` | The ComfyUI server's status, the workflows found, skipped files and where workflows go, in a pane (a server that doesn't answer is an error line in the chat). |
| `/comfy edit json\|markdown\|md <workflow>` | Opens a workflow's graph, or its `.md` (created if missing), in your editor. |
| `/comfy offered` | Lists the workflows currently offered to the model in a pane, one bullet each. |
| `/comfy view` | Opens the picture viewer on the output folder. |
| `/comfy thumbs` | Opens the thumbnail browser on the output folder. |
| `/comfy purge` | Deletes everything in the output folder, `.pasted` included, after a yes/no. Refused when it is the working directory. |
| `/vocalia [reset]` | Edits `vocalia.md` (the voice directive, empty by default, added last to every spoken reply), or removes it. |
| `/vocalia copy <profile> [force]` | Copies it to another profile (`force` replaces theirs). |
| `/wake [on\|off]` | Toggles the wake word. |
| `/window` | Shows the terminal window's size. |
| `/youtube` | Says what the video window is playing. |
| `/youtube <words>` | Searches YouTube (with a *YouTube API key*; 100 quota units a search) and opens a picker of the videos found; Enter plays one in the video window. Words that start with a verb below search too, unless what follows fits the verb; `/youtube search <words>` searches whatever they are. |
| `/youtube play <id>\|<link> [<time>]` | Plays a video by its id or a YouTube link, from the time (`90`, `1:30`) or the link's own. A link alone plays too. Needs no key. |
| `/youtube play\|resume`, `pause`, `seek <time>`, `volume <level>`, `mute\|unmute`, `close` | Drive the video window: carry on, pause, go to a time, set the volume (0–100), mute or unmute, close it. |
| `/youtube save [<id>\|<link>]` | Saves the video playing, or the one named, to this profile's saved videos (`youtube.json` in the profile folder). One named by its id or link is looked up for its title and channel first: YouTube's oEmbed, which needs no key, then the Data API when that fails and a *YouTube API key* is set (1 quota unit); found nowhere, it is saved by its id and named the first time it plays. A saved video resumes where it was left the next time it plays, a few seconds back; a time you give wins, and one seen to the end starts over. In a search's picker, `s` saves the highlighted video. |
| `/youtube saved` | The saved videos on the pane, each with where it was left (`at 12:34 of 45:00`, `watched`, `not played yet`); Enter plays one from there, `d` removes one after a yes. Any still without a title are looked up first. |
| `/youtube unsave <n>\|<id>\|<link>` | Takes a saved video off the list, by its number in `/youtube saved`, its id or a link. |

## Command details

### Plan mode

`/plan <requirement>` has the model research and present a plan before anything changes. It needs *LLM offer tools* and is refused during a reply.

* **Tools:** only read-only ones (files, git status/log/diff, web, SQL, Oracle, MySQL, UNC reads, the vault, recall, skills, sessions, `ask_user`) plus `present_plan`. Everything that writes, runs or starts something, and every MCP tool, waits for approval.
* **Presenting:** the model asks what it needs, then presents the plan, printed and saved as `.neon/plans/<kebab-name>.md` in the working directory (a new plan never overwrites another; revisions overwrite their own). 📝 shows while planning.
* **Approving:** the pane offers **Approve & run** (`a`), **Approve, clear context & run** (`f`), **Keep refining…** (`r`) or **Cancel plan** (`c`); the cursor and ESC are on Keep refining. Approval marks the file `approved` and runs a turn with every tool, ticking its checkboxes as it goes; the fresh-context choice starts a new conversation with the plan.
* **Tracking:** all ticked marks the file `done`; otherwise it is `incomplete` with a `progress: 3/7` line.
* `/new`, `/clear` and a profile switch leave plan mode; a restored session is still planning.

| Command | What it does |
|---|---|
| `/plan`, `/plan show` | Says where the plan stands. |
| `/plan <text>` | Adds detail. |
| `/plan approve [--fresh]` | Approves by typing instead of the pane (you may edit the file first). |
| `/plan cancel` | Leaves plan mode; the file is kept, marked `cancelled`. |
| `/plan save [name]` | Keeps a reply that looks like a plan (the model never called `present_plan`) as the plan, and brings up the approval pane. |
| `/plan open <name>` | Picks a plan up again: plan mode turns on over that file (back to `draft`) and the model reads it and asks what should change. `/plan approve` then runs only the unticked steps. |
| `/plan open` | Lists the plans with status and progress in a pane. |

### Bot conversations

`/botchat [profile ...] [topic]` lets profiles talk to each other until you stop them.

* **Cast:** the profiles you name, or all of them. The current profile always joins and speaks first.
* **Topic:** the first word that isn't a profile starts it (`/botchat ada max the best pizza`); after `--`, the rest is always the topic (`/botchat ada -- max speed of light`). Without one, the bots pick.
* **Turns:** each reply is in the speaker's persona and, with speech on, its own voice. A bot named in the last line speaks next; otherwise a random one, never the last speaker. Bots use this profile's LLM, or their own under *Botchat LLM mode* `multi`.
* **Tools:** pictures (see [Botchat pictures](SETTINGS.md#botchat-pictures)); the main chat's tools under *Botchat tools enabled*, or the *Botchat limited tools* alone; `load_skill` under *Botchat skills enabled* or *Botchat limited skills*; and memory under *Botchat memory enabled* (on by default; *Botchat memory mode* says whose). A tool's pane (an approval, a question) shows mid-chat as in a normal one.
* **Joining in:** a line you type joins before the next reply.
* **Talking:** with *STT input* on, push-to-talk stops the speaking bot, cuts the replying one short, and listens; what you say follows *STT destination*. With *TTS output* off, the wake phrase (*STT wake*) does the same. The wake phrase during speech (*STT interrupt*) ends the chat.
* **ESC** steps: stop the voice, then cut the replying bot short (the next one answers), then, before the next bot has said anything, end the chat. `/exit`, `/clear` and `/new` end the chat, then run.
* **Resume:** `/botchat --resume [line]` continues this run's last chat; a line after it joins as yours.
* **Embedded bots:** see *Botchat multi-embedded*. `/botchat --kill` stops leftover extra servers (never this profile's own).
* **Pronouns** come from each profile's first TTS voice (`am_`, `bm_`… male, anything else female).
* **Saved** as a session of its own under *Session logging*; the current conversation is left alone.

### Claude Code from the chat

`/claude <message>` runs the `claude` CLI headless in the working directory.

* The reply streams in under Claude's name, with each tool on a dim line and a cost footer; with speech on, it is spoken.
* The pair joins the conversation tagged `[to Claude]` and `[Claude]`, so the local model can build on it. Claude doesn't see the local conversation.
* Each session has one Claude conversation, resumed by the next `/claude` (even after a restart). `/claude new`, `/clear`, `/new` and a profile switch start another.
* *Claude CLI slash command permissions* sets what Claude may do; anything more is denied, never asked.
* ESC or Ctrl+C stops it, keeping the reply so far. Works with no LLM server; refused during a reply.
* Your own Claude Code setup applies (sign-in, `CLAUDE.md`, skills, MCP servers, hooks). `/usage` shows the cost.

### Loops

* A delay after the count waits after each reply: `/loop infinite 1m check the build` (one word, up to 24 hours). ESC or Ctrl+C stops the loop.
* A cancelled, withdrawn or failed turn ends it.
* The message may be `/imagine …` or `/speak …`, run with no model in between (`/loop infinite 5s /imagine score_9, 1girl`). `/speak` waits for each reading; only the last pass's pictures go with your next message. No other command can be looped. Between the passes the input line works as during a reply.

### Benchmark tests

`/test <name>` runs the nine tests of the companion LLMTester project against the connected model. Each is one request, graded in code.

| Id | Test | Passes when |
|---|---|---|
| `grid` | Complex Grid (Reasoning) | a 15-clue Einstein riddle is answered `German` |
| `rule` | Synthetic Rule (Reasoning) | a made-up operator learned from two examples gives `20` (the answer's last number) |
| `mind` | Theory of Mind (Reasoning) | a Sally–Anne false belief is answered `drawer` |
| `sycophancy` | Sycophancy (Reasoning) | an authority's false claim (17 is not prime) is answered `DISAGREE` |
| `json` | Nested JSON Extraction (Structured Output) | three invoices come back as a JSON array with the right totals |
| `state` | State Tracking (Structured Output) | four inventory steps come back as a JSON object with the right counts |
| `needle` | Needle in a Haystack (Long Context) | a password hidden mid-way through a long context is found |
| `multihop` | Multi-Hop Synthesis (Long Context) | two facts at 10% and 90% depth are combined into `mangoes1998` |
| `saturation` | Context Saturation (Long Context) | the server accepts a prompt that fills the whole context window |

* `/test reasoning`, `structured` and `long` run a group; `/test all` runs everything, saturation last.
* Each test sends only its own messages (no system prompt, history or tools), with the model's own sampling and reasoning.
* The structured tests send their schema as a strict `json_schema` `response_format`; a fenced reply fails. They are skipped over the Anthropic API.
* The long-context tests size themselves from the context window (the haystack up to half of it, at most ~66k tokens; ~66k when unknown). *LLM request timeout* applies; a refused or timed-out request is an error.
* A run starts as `/clear` leaves things. Results show one line per test, then a table of time, tokens and tok/s. ESC stops the run, keeping what finished; the input line works as during a reply.
* The last 50 runs are saved in the profile's `tests.json` with their reasoning and sampling; `/test history` lists them. Nothing enters the conversation.

### Imagine options

* It runs behind the input line: the line stays yours while ComfyUI works, so you can chat, open panes or start another. A picture done within half a second is drawn at once; a longer one says so, shows 🖼️ (🎨 from a picture) on the hint row, and is drawn when it is done, in the order sent. Double-click that 🖼️ / 🎨 to cancel; ESC does not reach it.
* The picture is drawn in the transcript, saved in *ComfyUI output folder*, and handed to the model with your next message (a message sent before it is done goes without it). The model gets it as a JPEG (a transparent picture stays PNG), so every request after carries a fraction of the bytes; the file saved is the server's own.
* The first word names the workflow when it matches one; otherwise an offered workflow is used.
* `-- <negative>` sets the negative; `--no-negative` sends none, not even the workflow's default.
* `--count` is capped by *ComfyUI max pictures per call*.
* `--image2` / `--image3` feed multi-picture workflows; a workflow with no prompt runs on its pictures alone (`/imagine faceswap --image a.png --image2 b.png`).
* `/loop` repeats it: `/loop 10 30s /imagine …`. A looped one runs in the foreground under a spinner, and ESC ends the loop; as during a reply, panes open and quick commands run meanwhile, and a message waits for the loop's end.

### Folder picker

`/cwd browse`, and the *Working directory* and *Obsidian vault* rows, open a folder tree headed **📂 Folders**, on the directory in use.

* `⌂ profile` (the profile's `files\`) and `▣ splash` (its `splash\`) sit above the drives.
* Space, → and ← open and close folders; `-` collapses all. A click on a folder's glyph or a double-click on its name opens or closes it.
* Only Enter chooses. Choosing `⌂ profile` saves the default, like `/cwd ~`.

### Picture viewer

The picture viewer is a window of its own (Windows only; elsewhere the file's registered app opens, and `/view` draws in the transcript). It opens from:

* **A double-click on a picture in the transcript**, on that picture's folder. A picture with no file (a paste, the built-in splash) is written to `%TEMP%\NeonSidekick\pictures` first. *Image viewer* can send these to another program.
* **`/view <image or folder>`**: the image, or a folder's newest picture, following new ones.
* **`/comfy view`** or the strip's **🎞️**: the ComfyUI output folder, following new pictures.

| Key | Action |
|---|---|
| ← / → | Newer / older picture, the strip's way (the newest is at the left and counts 1 in the title); reaching the newest follows new pictures again |
| The mouse wheel | Up: newer, down: older, a picture a notch |
| Right-click, the Apps key or Shift+F10 | The picture menu (see below) |
| Click **<** / **>** | The same as ← / →. The two round arrows fade in at the sides while the mouse is over the window; **<** is hidden on the newest picture and **>** on the oldest |
| Home / End | Newest (following again) / oldest picture |
| F11 or double-click | Toggle full screen |
| Drag the picture | Copy it to wherever you drop it: the desktop, an Explorer folder, or any app that takes a dropped file (never a move) |
| Del, Del (within 2 s) | Permanently delete the shown picture (the title says "Del again to delete" after the first) |
| F9 | Start or stop a looping slide show, stepping to the right (older), the oldest wrapping to the newest (5 s a slide; the title shows `▶ 5 s`) |
| ↑ / ↓ | Slide show: a second more or less per slide (1–60) |
| F10 | Slide show: switch between the folder's order and a random one |
| Esc | Stop the slide show, then leave full screen, then close |
| Tab | Bring the terminal to the front |
| Any other Ctrl or Alt chord | Runs in the chat as if pressed there (Ctrl+Alt+T opens `/tools`, Ctrl+Alt+U closes the viewer); the keyboard stays in the window. Ctrl+C there cancels a reply. Alt+F4 still closes the window |

* It wears the theme unless *Themed external windows* is off (a `/theme` change shows when it is next focused). There is one viewer, and it closes with the app.
* It reopens where it was last closed (saved in the profile), at the default size.
* It, the thumbnail browser and the ComfyUI picture strip follow each other: browsing the viewer highlights the same picture in the strip and the browser, and picking one on the strip or in the browser moves the viewer to it without bringing it forward.
* **The picture menu** (a right-click on the picture, here and in the thumbnail browser) is drawn in the theme: *Rotate and flip* (right, left, 180°, horizontally, vertically), *Colour* (greyscale, sepia, negative, polaroid), *Resize* (50%, 25%, or fit in 3840, 1920, 1280, 1024 or 512 px, never enlarging), *Convert to* PNG, JPEG, GIF or BMP, *Shrink the file* under 2 MB, 1 MB, 500 KB or 200 KB, *Strip metadata (lossless)* (JPEG, PNG, WebP and GIF only: EXIF, GPS, XMP, comments and data after the picture go, the pixels are copied untouched, the orientation is kept), then *Copy the path*, *Show in Explorer*, *Attach to the chat* (pasted onto the input line as a picture), *Print*, and *Delete* (permanent, no confirmation). Its last row shows *Image edit mode*: `beside-original` writes `photo-edited.png` beside the picture and shows it, `overwrite-original` replaces the picture (a conversion writes the new file and deletes the old). An edit that would change nothing says so and writes nothing. What each row did is a line in the chat. Edits use *Image edit quality* and *Image edit metadata*, as `image_edit` does; *Strip metadata* always takes it all.
* The camera's live view (*Camera preview* `live`, `/camera live`) is a separate window, so both can be open. It shows the camera mirrored, then the photo, never takes the keyboard, answers only F11, a double-click and Esc (plus Tab and the chords that go to the chat, as in the viewer; Ctrl+Alt+V closes it), and remembers its own place.

### Thumbnail browser

`/view <folder or image> --thumbs` (a folder of the working directory, or an image's folder with the image selected), `/comfy thumbs` or the toolbar's **🪟** (the ComfyUI output folder) opens the folder's pictures as thumbnails in a window of their own (Windows only), oldest first.

* **In step:** a click on a thumbnail moves the picture viewer to it without taking the keyboard (opening the viewer when it is closed); the viewer's own keys and the strip's arrows move the selection here.
* **No jumping:** new pictures go on the end and nothing already shown moves. A view scrolled to the bottom of a long folder stays at the bottom as pictures arrive.
* **Size:** the thumbnails are as large as fits every picture in the window (down to a smallest size, then the window scrolls), chosen when it opens, is resized or goes full screen, and on F5. + and − (or Ctrl+wheel) make them bigger or smaller for the session; F5 fits them again.
* **The picture menu:** a right-click on a thumbnail (or the Apps key, Shift+F10) opens the same menu as in the viewer, with *Open in the viewer* first.

| Key | Action |
|---|---|
| Click / double-click or Enter | Select (the viewer follows) / open in the viewer, brought forward |
| Arrows, PgUp / PgDn, Home / End | Move the selection |
| The wheel, the scroll bar | Scroll |
| + / − or Ctrl+wheel | Bigger / smaller thumbnails |
| F5 | List the folder again and fit the thumbnails to the window |
| Del, Del (within 2 s) | Permanently delete the selected picture (the title says "Del again to delete" after the first) |
| F11 or double-click between thumbnails | Toggle full screen |
| Esc | Close the menu, then leave full screen, then close |
| Tab | Bring the terminal to the front |
| Any other Ctrl or Alt chord | Runs in the chat as if pressed there; the keyboard stays in the window |

Like the viewer, it follows the theme, reopens where it was closed, and closes with the app.

### Log window

`/log` opens this run's diagnostic log in a window of its own (Windows only), every line from the start, Trace and up, with or without `--log`. It keeps the newest 20,000 lines, coloured by level, and wraps long lines.

* **Following:** at the bottom it follows new lines; scroll up and it holds still (the title says *paused*). Back at the bottom, Ctrl+E or Ctrl+End follows again.
* **Copying:** drag to select (Shift+click extends), Ctrl+A selects all, Ctrl+C copies.

| Key | Action |
|---|---|
| ↑ / ↓, PgUp / PgDn, the wheel | Scroll a row or a page; reaching the bottom follows again |
| Ctrl+Home | The first line, following paused |
| Ctrl+End or Ctrl+E | The last line, following again |
| Ctrl+A / Ctrl+C | Select everything / copy the selection |
| F11 or double-click | Toggle full screen |
| Esc | Leave full screen, then close |
| Tab | Bring the terminal to the front |
| Any other Ctrl or Alt chord | Runs in the chat as if pressed there (Ctrl+Alt+G closes this window); the keyboard stays in the window |

Like the viewer, it follows the theme, reopens where it was closed, and closes with the app. A second `/log` brings it forward; `/log --file` opens the `--log` file instead.

### Process window

`/process <id>` shows a background process's output live (Windows only): the log window's look and keys over the process's last 5,000 lines, stderr in the warning colour, the title its id, command and state (`running`, `exited 0`, `stopped by you`). It opens only when you ask; `/process` alone (or the toolbar's ⚡) lists the processes, and Enter or a double-click on one there opens it here.

* **One window:** `/process` with another id switches it to that process, in the same place on screen, without closing it.
* **Stopping:** Ctrl+K arms the stop (the title asks for a second press), and a second Ctrl+K within 3 seconds stops the process and everything it started. The chat prints `proc_… was stopped by you`, and the model hears of it on its next turn. Once the process has ended, Ctrl+K goes on to the chat as any other key. The `/process` list's **✖ kill** button (or `k`) stops the highlighted one the same way, after a yes/no.

Otherwise it is read-only: scroll, follow, select and copy as in the log window, Tab back to the terminal. It reopens where it was closed and closes with the app.

### Camera

A USB or built-in webcam through Windows' Media Foundation; nothing to install. Windows only.

* **One shared stream:** the camera pane, the live view, a botchat and watch mode share one open camera, which closes a few seconds after the last lets go. A photo waits about a second for the exposure to settle.
* **📷 on the hint row** shows whenever the camera is on (with its light and Windows' indicator); double-click it to end `/camera live` and watch mode.
* **Photos** are JPEGs in *Camera output folder* (`camera_images` by default), named by time (`20261002-140203.jpg`); a retaken or declined one is deleted. Botchat and watch pictures aren't saved, but double-clicking a watch thumbnail writes it to the folder's `.watch` subfolder, which is cleared when watch mode stops and on every profile load.
* **Stored sessions** keep a line instead of the picture unless *Camera keep in sessions* is on.
* **Failures** say why: Windows' *Let desktop apps access your camera* is off (Settings › Privacy & security › Camera), another app has the camera, it was unplugged, or Media Foundation is missing (Windows N needs the Media Feature Pack).
* **Watching** uses stills: each picture is compared on your machine with the last one the model saw, and sent only when enough changed.

### Screen capture

A monitor, every monitor or one window, through Windows' own GDI; nothing to install. Windows only.

* **Targets:** `screen` (the monitor the app is on, the default), `all`, `monitor:N`, `window:<id or title words>` (a title's words, or its process name; an id from `screen_list` or `/screen list` when several match) and `behind` (the window right behind the app's).
* **A window** is drawn by itself, so it comes out whole even when another covers it; a minimized one must be restored first. Protected video and some HDR content come out black: Windows keeps it out of every screenshot.
* **Screenshots** are JPEGs in *Screen capture output folder* (`screen_images` by default), named by time, scaled to 2048 pixels on the longer side at most.
* **Asking:** under *Screen capture ask* `ask` the pane says what would be captured and the model's reason; a denial isn't retried that turn, and *Allow for this session* lasts until the session ends.
* **Stored sessions** keep a line instead of the picture unless *Screen capture keep in sessions* is on: a screenshot can hold anything that was on the screen.

### Profiles

* A name is 1 to 32 letters, digits, `-` or `_`, and can't be `neon` or one of the verbs.
* A name starting with `_` is temporary: left off the picker and the name list (unless loaded), and the next launch opens `default` (the profile is kept). `/profile _name` still switches to one.
* `--profile <name>` (or `NEONSIDEKICK_PROFILE`) opens a profile for one launch without changing the next launch's. An unknown name exits with code 2; a headless run with neither opens `default`.
* A reset keeps the LLM URL, LLM model, LLM API key, TTS HTTP URL, Anthropic API key, OpenAI API key, Web browser path, Web search method, Web SearXNG URL, Claude CLI executable, Obsidian vault, ComfyUI URL, Home Assistant URL, Home Assistant API key and YouTube API key; `--all` resets those too. `default` can only be reset while loaded.
* `push <name>` copies the loaded profile's settings over another's; `pull <name>` the other way. Both ask first. Only `profile.json` is copied (the target keeps its working directory); a pull clears the conversation.
* The API keys in `profile.json` are encrypted for your Windows account (DPAPI, `dpapi:…`); a key typed into the file by hand is encrypted at the next load. Only the same Windows user on the same machine can read them.

### Custom themes

The built-in themes are the sixty JSON files in the repo's [`assets/themes`](../assets/themes), one folder per category, compiled into the app; adding, changing or removing a file there changes the built-ins at the next build. The default is `collider`. [`Theme Atlas.html`](../assets/themes/Theme%20Atlas.html) previews them all; `dotnet run tools/ThemeAtlas.cs` writes it again after a change (a test fails until it does).

| Folder | Themes |
|--------|--------|
| `art` | bauhaus, deco, inkwash, kaleidoscope, lapis, stainedglass, ukiyoe, vaporwave |
| `cinema` | akira, arrakis, ghostshell, grid, hal, noir, replicant, starbase, twinsuns |
| `cosmos` | aurora, blackhole, bloodmoon, nebula, orbit, solaris, supernova |
| `elements` | abyssal, magma, opal, oxide, prism, radium, temper |
| `machines` | circuit, collider, commodore, glitch, heartbeat, infrared, mainframe, netrunner, nixie, nostromo, vhs, vinyl |
| `nights` | cyberpunk, kowloon, lighthouse, miami, sakura, synthwave, witchhour |
| `solid` | blueprint, carbon, chalkboard, espresso, fieldradio, glacier, matcha, signal, ultraviolet, velvet (one-colour banner and rule, no gradient) |

Your own themes are JSON files in the home's `themes` folder, shared by every profile, and subfolders one level deep (not ones starting with `.`). They are listed among the built-ins, sorted by name, and re-read whenever a theme list opens (pick the theme again to see an edit). On a name clash, a file loose in the folder beats one in a subfolder, then the first subfolder by name wins; the loser gets a warning. A file named like a built-in replaces it.

The easiest start is `/theme export <name> [new-name]`, which writes any theme to `themes\<new-name>.json` with every colour filled in (default name `<name>-custom`; it never overwrites). Export a built-in under its own name (`/theme export noir noir`) to replace it.

```jsonc
{
  "name": "dracula",                 // optional: the file name when left out
  "description": "vampire purple",   // optional: the note beside the name ("custom theme" when left out)
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

* **A theme stands alone:** it sets all fifteen colours itself; nothing comes from another theme (a `base` was read until 2026-10-05; it is ignored now, with a warning, and a file that leaned on one is skipped naming the colours it leaves out). A file replacing a built-in replaces it whole.
* **Names** are 1 to 32 lower-case letters, digits, `-` or `_`, starting with a letter or digit, and not `export`.
* **Colours** are `#RRGGBB` or `#RGB`. Comments and trailing commas are allowed.
* **Gradient:** optional; left out, it runs secondary → tertiary → primary → warm → highlight from the file's own colours.
* **Problems:** an unreadable file, bad JSON, a bad name or a colour it leaves out (or one that is not a colour) skips the file; a misspelled key or a bad gradient or style is ignored. Each shows as a warning when a theme list opens.

**Colour roles** (`colors`). Every style below is made from these.

| Role | What it colours |
|------|-----------------|
| `primary` | The main accent: headings, borders, keywords, markup tags. |
| `secondary` | The counter-accent: your lines, the spinner, table headers, inline code, types and keys, the selection. |
| `tertiary` | The third accent: section headings, bullets, the quote bar, function calls, the paste label. |
| `deep` | The receding structural colour: the pane rule, a reply table's border. |
| `highlight` | The warm highlight: string literals. |
| `warm` | Numbers. |
| `tint` | A soft accent: `$variables`. |
| `ink` | Body text. |
| `dim` | Secondary, dim text and the hint row. |
| `dimmer` | A step darker than `dim`: the input row's ghost text. |
| `bg` | The page background (unless *Themed background* is off), the selection's text, disabled menu rows, transparent pixels, the viewer. |
| `panelBg` | The lifted fill: code blocks, the highlighted menu row. |
| `good` | Success, enabled, connected. |
| `bad` | Failure, error. |
| `warn` | A warning. |

`gradient` is the banner title and its rule, left to right, with 2 to 16 stops.

**Style changes** (`styles`). Each entry can set `fg` and `bg` (a hex colour or a role name like `"dim"`) and turn `bold`, `italic`, `underline`, `dim` or `strikethrough` on or off; anything left out stays as the theme makes it.

* **Text:** `body`, `dimText`, `accent`, `accentSecondary`, `accentTertiary`, `label`, `errorText`, `goodText`, `warnText`.
* **Screen:** `user` (your lines), `assistant` (the reply), `systemText` (notices), `sectionHeading`, `border`, `tableHeader`, `spinner`, `paneRule`, `hint`, `trailerMark`, `pasteLabel`, `placeholder`, `selectedText`.
* **Menus:** `menuHighlight`, `menuHighlightDim`, `menuDisabled`, `menuFooter` (the slab under a list describing the cursor's row).
* **Replies:** `markdownBold`, `markdownItalic`, `markdownCode`, `markdownCodeBlock`, `markdownCodeLabel`, `thinking`, `markdownHeading1`, `markdownHeading`, `markdownBullet`, `markdownQuoteBar`, `markdownQuote`, `markdownLinkUrl`, `markdownRule`.
* **Code highlighting:** `codeKeyword`, `codeType`, `codeString`, `codeNumber`, `codeComment`, `codePunctuation`, `codeFunction`, `codeVariable`, `codeAttribute`, `codeTag`, `codeHeading`, `codeInserted`, `codeDeleted`.
* **File diffs:** `diffAdded`, `diffRemoved` (the added and removed lines' slabs under a file edit; the text keeps its code colours, only `bg` counts).

Some styles copy another unless changed themselves: `user`, `spinner` and `markdownHeading` copy `accentSecondary`; `assistant` copies `body`; `systemText`, `hint`, `markdownCodeLabel`, `markdownQuote` and `markdownLinkUrl` copy `dimText`; `sectionHeading` copies `accentTertiary`; `markdownHeading1` copies `accent`; `markdownBullet` and `markdownQuoteBar` copy `trailerMark`; `markdownRule` copies `paneRule`; `codeAttribute` copies `codeType`; `codeTag` copies `codeKeyword`.

### Voice presets

*TTS voice preset* (the TTS tab of `/settings`) picks from the seven built-ins and your own: JSON files in the home's `voices` folder, one per preset, named for it. A file named like a built-in replaces it; the rest follow, sorted by name. Changes are picked up without a restart.

```jsonc
{
  "TtsVoice": "af_heart",      // the Kokoro voice
  "TtsVoice2": "am_eric",      // a second voice blended in, or "" for the first alone
  "TtsVoiceMix": 80,           // the first voice's share, 0-100
  "TtsSpeed": 1.2,             // 0.5-2.0
  "Description": "optional, for reading only; the app ignores it"
}
```

A file that doesn't parse or has a value out of range is skipped with a warning. Subfolders one level deep are read as for themes.

Forty-one more presets come with the repo in [`assets/voices`](../assets/voices), one folder per kind (the built-ins are in `built-in`): copy a file, or a whole folder (`voices\accents`), into your `voices` folder. [`Voice Atlas.html`](../assets/voices/Voice%20Atlas.html) plays every preset saying one line (samples in `assets/voices/samples`). The blends lean on Kokoro's stronger voices ([`VOICES.md`](https://huggingface.co/hexgrad/Kokoro-82M/blob/main/VOICES.md)).

| Folder | Presets |
|--------|---------|
| `narrators` | ada, edmund, iris, margot, silas, walter (unhurried, for long answers) |
| `brisk` | dash, kit, pepper, rex, sloane, zara (quick, 1.3-1.45x) |
| `british` | alfie, beatrice, ellis, harriet, poppy, rupert |
| `characters` | aria, atlas, jinx, maven, nick, wren |
| `duets` | ash, morgan, quinn, river, rowan, sage (a female and a male voice blended) |
| `accents` | amelie, arjun, beatriz, giulia, kenji, lucia, marco, mateo, mei, priya, yuki (an English voice leads, and a Spanish, French, Italian, Hindi, Portuguese, Japanese or Mandarin voice adds its accent) |

`dotnet run tools/VoiceSamples.cs` regenerates the samples and the atlas (it needs the downloaded Kokoro model); rerun it after changing a preset in `assets/voices`.

### Pane keys

Every list pane works the same way (2026-10-07): ↑/↓ move, Enter picks the highlighted row, a double-click is Enter, and ESC closes the pane at its top level or goes back from a page opened under it. A key letter is lowercase and acts at once; on a pane that filters, the letters work until you start typing, and then every character goes on the filter (the hint drops them meanwhile). A remove always asks first, with **No** on the cursor. An empty list still opens its pane, on one dim line, so its buttons stay in reach.

| Pane | Keys |
|---|---|
| `/memory` | Enter forgets (asks) · `w` read-write · `o` read-only · `x` disabled |
| `/queue` | Enter removes (asks) · `c` clear all · `s` send (while held) |
| `/rewind` | Enter rewinds to before the message |
| `/sessions` | Enter opens · type to filter |
| `/process` | Enter opens the window · `k` kills (asks) |
| `/skills` › a skill › `revert` | Enter puts the version back · `d` removes it (asks) · `c` clear all (asks) |
| `/docker` | Enter opens · `r` refresh · type to filter |
| `/youtube saved` | Enter plays · `d` removes (asks) · type to filter |
| `/cmdlist` (*Shell allowed commands*) | Enter removes (asks) · `a` ask · `y` yolo · type to filter |
| *Shell police forbidden strings* | the top row adds · Enter removes (asks) |
| `/police` | `s` the forbidden strings |
| `/tools web` | `d` default · `h` httpclient · `c` chromium |
| `/tools camera` | `w` watch · `l` live · `s` snap · `c` screen |
| `/perfbar` | Enter or Space flips · `a` all · `n` none · `d` default · `t` / `g` / `s` / `l` the look |
| *Embedded models* | `1` / `2` size · `i` / `u` installed or not · `d` drafter · `s` sort · `x` uncensored |
| `/server`, `/model` | Enter chooses · type to filter |
| The shell approval | `d` deny · `o` once · `s` session · `a` always · `v` the whole command · ESC denies |
| A database write's approval | `d` deny · `o` once · `s` session · `v` the whole statement · ESC denies |
| `/find` (the transcript) | type to find · Enter or F3 older · Shift+Enter or Shift+F3 newer · PgUp/PgDn scroll · ESC done, back at the bottom |
| Info panes (`/help`, `/sys`, `/about`, `/tree`, a `v` view…) | ↑/↓ PgUp/PgDn scroll · ←/→ or Tab switch tabs · type to find: Enter or F3 the next match, Shift+Enter or Shift+F3 the one before, Backspace erases, the first ESC clears the find · ESC closes |
