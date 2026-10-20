# Slash commands

Every command and its forms, grouped by topic, then the details of the longer ones. Back to the [README](../README.md#useful-first-commands); in the app, `/help` lists them all.

Type `/` to list the commands; after a command and a space, the app offers its arguments. `//` is a shortcut for `/settings`. Forms are written as: words typed as they are, `<placeholders>` to fill in, `[optional]` parts, and `a|b` for a choice.

Elsewhere:

* **Each pane's keys:** [Pane keys](INTERFACE.md#pane-keys).
* **What works while a reply runs:** [Commands typed during a reply](INTERFACE.md#commands-typed-during-a-reply).
* **What each command does headless:** [HEADLESS.md](HEADLESS.md#slash-commands-in-headless-mode).

## Contents

- [All commands](#all-commands)
  - [Help and the app](#help-and-the-app) · [Conversation](#conversation) · [Models and servers](#models-and-servers) · [Profiles and settings](#profiles-and-settings)
  - [Files and folders](#files-and-folders) · [Pictures, camera and screen](#pictures-camera-and-screen) · [Voice](#voice) · [Web and video](#web-and-video)
  - [Integrations](#integrations) · [Admin](#admin) · [Display and windows](#display-and-windows) · [Automation](#automation)
- [Command details](#command-details)
  - [Plan mode](#plan-mode) · [Ralph loop](#ralph-loop) · [Code audit](#code-audit) · [Bot conversations](#bot-conversations) · [Claude Code from the chat](#claude-code-from-the-chat)
  - [Advisor](#advisor) · [Your own shell line](#your-own-shell-line) · [Loops](#loops) · [Benchmark tests](#benchmark-tests) · [Imagine options](#imagine-options) · [Camera](#camera)
  - [Screen capture](#screen-capture) · [Profiles](#profiles) · [Desktop shortcuts](#desktop-shortcuts) · [Custom themes](#custom-themes) · [Voice presets](#voice-presets)
- The app's windows, pasting a picture, the folder picker and the file browser: [Using the app](INTERFACE.md).

## All commands

### Help and the app

| Command | What it does |
|---|---|
| `/help` | The commands (everyday ones on Basic, the rest on Advanced) and the keys; on the Keys tab, `k` swaps in `/keycheck`'s list. |
| `/help <words>` | Asks the model to look the words up in the app's manual (`neon_help`); with no model, or `neon_help` off, the manual answers. Waits during a reply. |
| `/about` | The version, runtime, folders, components, the embedded models' Hugging Face pages and the license. |
| `/keycheck` | Lists the app's key chords and whether another program holds each (a held chord never reaches the app). On a Mac, also macOS shortcuts and an Option key that types characters. |
| `/exit` | Exits the app. |

### Conversation

| Command | What it does |
|---|---|
| `/new` | Starts a new conversation, keeping the screen. |
| `/clear` | Starts a new conversation and clears the screen. |
| `/splash` | Starts a new conversation and shows the splash screen. |
| `/splash view`, `/splash thumbs` | The picture viewer or thumbnail browser on the profile's `images\splash` folder; while it's empty, the built-in pictures show. |
| `/splash purge` | Deletes every splash picture of the profile, after a yes, per *File deletes to Recycle bin*; the built-in pictures show again. |
| `/compact [<focus>]` | Shrinks the context (*LLM compact mode*); a focus tells the summary what to keep. |
| `/rewind [<n>]` | Goes back to an earlier message after a yes: it and all after it leave the conversation and stored session, its text back on the input row. What tools changed stays. |
| `/copy [<n>\|all] [--thinking]` | Copies the last reply, the last *n*, or the whole transcript as Markdown; `--thinking` adds the thinking. |
| `/draft` | Writes the next message in your editor; it is sent when you save and close. |
| `/echo <text>` | Prints a line as a reply, spoken when *TTS output* is on. |
| `/find [<words>]` | Finds words in the transcript, every match marked. |
| `/expand`, `/collapse` | Unfold every tool run, code block, diff and thinking block from here on, or fold them again. |
| `/queue [clear]` | Lists the messages queued during a reply, to remove, clear or send them; `clear` drops them all. |
| `/queue mode automatic\|manual\|off` | Sets *Queue mode*: queued messages go when the reply ends, wait for **➤ send**, or aren't kept. |
| `/queue cancel hold\|drain\|empty` | Sets *Queue cancel mode*: after a canceled reply, keep the queue, send the next, or drop them all. |
| `/prompts` | Your saved prompts (`prompts.json`, per profile, never shown to the model): a pick sends one as if typed (queued during a reply) or puts it on the draft line, the old draft going to the input history. Keys: [Pane keys](INTERFACE.md#pane-keys). |
| `/prompts <n>`, `/prompts save <text>`, `/prompts remove <n>` | Sends prompt *n* as if typed, saves the rest of the line (a pasted block too), or removes prompt *n*. |
| `/prompts list [--clear]` | Prints the prompts, numbered; `--clear` removes them all after a yes. |
| `/sessions [<id>]` | Lists the stored sessions, or restores one with its pictures (`#12` works too). |
| `/sessions purge <id>\|all`, `/sessions purge older <age>` | Deletes one stored session, every one, or those older than an age (`30` days, `12h`, `1d 6h`). |
| `/sessions title [<text>]`, `/rename [<name>]` | Renames the current session; alone, opens a box with its name. |
| `/memory` | Lists the memories, to edit, disable (kept, never offered to the model), forget or add one. |
| `/memory read-write\|read-only\|off\|on` | Sets *Memory mode* (`on` is read-write). |
| `/memory forget\|edit` | Forgets every memory after a yes, or opens `memory.json` in your editor. |
| `/remember <text>` | Adds a memory; refused while *Memory mode* is `off`. |
| `/msgclear` | Clears this profile's message history (what ↑/↓ recall) after a yes; from the Web UI too, asking on the page and the PC. |
| `/usage` | Token usage and performance, with rows for `/claude`, `/botchat` and the advisors; `~` marks an estimate. |

### Models and servers

| Command | What it does |
|---|---|
| `/server [<url>\|embedded\|claude-code\|docker\|docker:<container>]` | Picks a server (found ones, the Anthropic and OpenAI APIs, Claude Code, embedded models, Docker containers) or sets one by URL, then the model and reasoning. A word picks that kind; Docker servers need Windows. |
| `/model [<id>]` | Picks a model from the server's list, or sets one by id; on the embedded model, the installed ones. |
| `/reasoning [none\|low\|medium\|high\|xhigh]` | Picks or sets the reasoning effort (*LLM reasoning*). |
| `/sampling` | Edits the per-model sampling in a pane; see [Sampling per model](SETTINGS.md#sampling-per-model). |
| `/sampling <field> <value>\|clear` | Sets or clears one of the connected model's values: `temperature`, `top_p`, `top_k`, `min_p`, `presence_penalty`, `frequency_penalty` or `repetition_penalty`. |
| `/sampling extra <json>\|clear`, `/sampling clear` | Sets or clears the extra request body, or clears every override of the connected model. |
| `/advisor` | Lists the advisors with their roles, server kinds and models. See [Advisor](#advisor). |
| `/advisor <name> <question>` | Asks one advisor, by profile name or a word of its role; the answer joins the conversation. |

### Profiles and settings

The push and pull forms copy to (`push`) or from (`pull`) another profile, after a yes. With `overwrite`, a list replaces the target's instead of adding to it, and a file replaces one already there.

| Command | What it does |
|---|---|
| `/profile [<name>]` | Switches profiles for this run: a picker, or the one named. See [Profiles](#profiles). |
| `/profile add\|delete <name>`, `/profile rename <name> <new-name>` | Creates a profile (from this one's settings, memories and saved prompts), deletes one, or renames one. |
| `/profile reset [<name>] [--all]` | Resets a profile to the defaults, keeping its URLs, paths, keys and avatars unless `--all`. |
| `/profile push\|pull <name>` | Copies every setting but the working directory; a pull clears the conversation. |
| `/profile push\|pull keys <name>` | Copies the five API keys; one the source lacks is cleared in the target, and one set only by a variable isn't copied. |
| `/profile push\|pull servers <name>` | Copies the server settings (server, model, reasoning, sampling, the Embedded, Docker, Anthropic and OpenAI tabs but the keys), replacing the target's. |
| `/profile push\|pull allowed <name> [overwrite]`, `/profile push\|pull policestrings <name> [overwrite]` | Copies *Shell allowed commands* or *Shell police forbidden strings*. |
| `/profile push\|pull history <name> [overwrite]`, `/profile push\|pull memory <name> [overwrite]`, `/profile push\|pull prompts <name> [overwrite]` | Copies the message history, the memories or the saved prompts; history is refused while the target has *Keep message history* off. |
| `/profile push\|pull persona <name> [overwrite]`, `/profile push\|pull operata <name> [overwrite]`, `/profile push\|pull vocalia <name> [overwrite]` | Copies `persona.md`, `operata.md` (the operating rules) or `vocalia.md` (the spoken-reply directive). |
| `/profile push\|pull avatar user\|model <name>` | Copies the *User avatar* or the *Model avatar*. |
| `/profile edit\|reload` | Opens `profile.json` in your editor, or reads it back and reconnects what changed. |
| `/settings`, `//` | Opens the settings; a letter typed on a tab searches them all. |
| `/settings <words>` | Searches every setting (`/settings`, `/tools`, `/skills`, `/mcp`) by name, tab or description. |
| `/settings changed` | Lists the settings that differ from their defaults, to edit one or put it back. |
| `/tools` | Switches the model's tools on or off, each group's settings on its own tab. See [TOOLS.md](TOOLS.md). |
| `/tools <group>` | Opens one group's page: `ask`, `shell`, `files`, `image`, `web`, `advisors`, `docker`, `eventlog`, `network`, `services`, `tasks`, `directory`, `obsidian`, `sql`, `oracle`, `mysql`, `sqlite`, `postgres`, `unc`, `ha`, `comfy`, `camera`, `print`, `shopping`, `timers`, `clock` or `help`. |
| `/tools shell allowed`, `/tools shell police` | Opens *Shell allowed commands* (which also steps *Shell command policy*), or *Shell police* and its forbidden strings. |
| `/skills` | Lists the skills, to move, rename, edit, revert, lock, disable or delete one, and their settings; its **loaded** tab shows the skills in this conversation. |
| `/skills add <source> [--global\|--profile]` | Installs an [Agent Skill](https://agentskills.io) after a preview: search words, `owner/repo[/skill]`, a GitHub URL or a `.zip` URL. See [Installing skills](SETTINGS.md#installing-skills). |
| `/skills purge list\|commit <age>` | Lists the skills unused for that long, or deletes them after a yes. See [Skill records](SETTINGS.md#skill-records-and-purging-unused-skills). |
| `/mcp` | Connects [MCP servers](INTERFACE.md#words-used-here) (outside programs that offer the model more tools) and switches their tools on or off. |
| `/sys` | Shows the system prompt and the tools sent to the model. |
| `/sys persona [reset]`, `/sys operata [reset]`, `/sys vocalia [reset]` | Edits `persona.md`, `operata.md` (the operating rules) or `vocalia.md` (added to every spoken reply) in your editor, or resets it after a yes. |
| `/avatar` | Opens the emoji picker for the *Model avatar*; see [The avatar picker](INTERFACE.md#the-avatar-picker). |
| `/avatar <emoji>`, `/avatar <words>`, `/avatar clear` | Sets that emoji, opens the picker searching for the words, or clears it (replies open with ● again). |
| `/avatar model [<emoji>\|<words>\|clear]` | The same, naming the *Model avatar* outright. |
| `/avatar user`, `/avatar user <emoji>\|<words>`, `/avatar user clear` | The same for your *User avatar*, the emoji that opens each line you send (› when cleared). |
| `/shortcut [<profile>] [--log]` | Puts a desktop shortcut that starts the app on this profile, or the one named; `--log` adds a log. See [Desktop shortcuts](#desktop-shortcuts). |

### Files and folders

| Command | What it does |
|---|---|
| `/cwd [<path>\|~\|browse]` | Shows or changes the working directory (a missing folder is created, quotes around the path dropped); `~` is the profile's `files` folder, `browse` the [folder picker](INTERFACE.md#folder-picker). |
| `/tree [<path>]` | A tree of the working directory in a pane; hidden entries per *File browser/tree mode*, `.git` only when named. |
| `/browse [<folder>]` | Browses the working directory as a tree, opening a file in its default app. See [File browser](INTERFACE.md#file-browser). |
| `/explore [<folder>]` | Opens the working directory, or a folder in it, in your file browser. |
| `/open <file>` | Opens a file from the working directory in its default app. |
| `/vault [<path>]` | A tree of the *Obsidian vault*, like `/tree`; needs *Obsidian tools* on. |
| `/terminal [<folder>]` | Opens a terminal there: Windows Terminal (else a console window), or on a Mac Terminal or iTerm2. |
| `/cmd <command>` | Runs a shell line yourself, in the *Shell default shell*, under the model's checks; the model sees it. See [Your own shell line](#your-own-shell-line). |
| `/cmd --shell <shell> <command>` | The same in another shell: `powershell`, `cmd` or `bash` (on a Mac `zsh`, `bash` or `powershell`). |
| `/cmd --bg <command>` | The same in the background; its output is shown when it ends. |
| `/process`, `/process <id>` | Lists the background processes (the model's and your `/cmd --bg` ones), or opens one in the [process window](INTERFACE.md#the-process-window). |

### Pictures, camera and screen

Each purge asks first and follows *File deletes to Recycle bin*: to the Recycle bin (the Trash on a Mac), else for good.

| Command | What it does |
|---|---|
| `/view <image>\|<folder>` | Opens an image, or a folder's newest picture, in the [picture viewer](INTERFACE.md#the-picture-viewer). A PDF opens in the [Browser window](INTERFACE.md#the-browser-window) (on a Mac before macOS 14, in Preview). |
| `/view <image> --chat` | Draws it in the transcript instead. |
| `/view <image>\|<folder> --thumbs` | Opens the folder in the [thumbnail browser](INTERFACE.md#the-thumbnail-browser), in step with the viewer. |
| `/camera`, `/camera snap` | Opens the camera pane to take a photo, or takes one at once, and puts it on the input line. See [Camera](#camera). |
| `/camera list`, `/camera use <n>\|<name>` | Lists the cameras, or chooses one by number or name (*Camera device*). |
| `/camera live` | Shows the camera live in the [camera window](INTERFACE.md#the-camera-window) until you close it. |
| `/camera watch [<seconds>\|off]` | Checks the camera every *Camera watch interval (s)* (or the 2–3600 seconds given); a changed picture goes with your next message. |
| `/camera off` | Ends live view and watch mode. |
| `/camera view`, `/camera thumbs` | The picture viewer or thumbnail browser on *Camera output folder*. |
| `/camera purge` | Deletes everything in *Camera output folder*. |
| `/screen` | Captures the app's monitor onto the input line; *Screen tools* and *Screen policy* don't apply. See [Screen capture](#screen-capture). |
| `/screen all\|desktop`, `/screen monitor:<n>` | Captures every monitor as one picture, or monitor *n*. |
| `/screen window:<id>\|<title-words>` | Captures one window, even when covered, by id or words of its title or program. |
| `/screen behind` | Captures the window right behind the app's own. |
| `/screen list` | Lists the monitors and windows, front to back, with their targets. |
| `/screen view`, `/screen thumbs` | The picture viewer or thumbnail browser on *Screen output folder*. |
| `/screen purge` | Deletes everything in *Screen output folder*. |
| `/pasted view`, `/pasted thumbs` | The picture viewer or thumbnail browser on *Pasted images folder*, where pasted pictures are saved as their messages are sent. |
| `/pasted purge` | Deletes everything in *Pasted images folder*. |

### Voice

| Command | What it does |
|---|---|
| `/tts [on\|off]` | Turns speech output on or off (*TTS output*). |
| `/stt [on\|off]` | Turns voice input on or off (*STT input*). |
| `/stt wake [on\|off]` | Turns the wake word on or off; off turns the interrupt off too. |
| `/stt interrupt [on\|off]` | Turns the wake-word interrupt during a spoken reply on or off; needs the wake word. |
| `/read [<file> [<n>]\|<n>]` | Reads a text file or a PDF aloud; alone it resumes, a number starts at that sentence ([keys](INTERFACE.md#the-input-line)). A PDF's first 40 pages are read with your browser, a repeated header or footer left out. |

### Web and video

* **The Browser window** (macOS 14+ on a Mac): its bar, keys, history and saved links are in [Using the app](INTERFACE.md#the-browser-window), its bot checks in [TOOLS.md](TOOLS.md#the-browser-window).
* **YouTube** (playing needs macOS 14+ on a Mac): saved videos and quota are in [TOOLS.md](TOOLS.md#youtube), the window's keys in [The video window](INTERFACE.md#the-video-window).

| Command | What it does |
|---|---|
| `/browser [<url>]` | Opens a page (or *Web browser home page*) in the Browser window, to sign in or pass a bot check; on close its cookies go to the web tools. |
| `/browser clear history\|all` | Clears its history, or after a yes everything it keeps (cookies and sign-ins too); saved links stay. |
| `/browser <n>` | Opens saved link *n* in the Browser window; with no window (headless, a Mac before macOS 14), in your default browser. |
| `/browser save [<url> [<name>]]` | Saves a link (the words after the address name it), or with no address the page the window shows. |
| `/browser list`, `/browser list --clear` | The saved links in a pane (with no window, they open in your default browser); `--clear` removes them all after a yes. |
| `/browser unsave <n>\|<url>` | Removes one saved link. |
| `/youtube` | The saved videos in a pane, as `/youtube list`. |
| `/youtube status` | Says what the video player is playing. |
| `/youtube <words>`, `/youtube search <words>` | Searches YouTube and opens a picker (needs a *YouTube API key*; 100 quota units a search); use `search` when the words start with one of `/youtube`'s own (`play`, `list`…). |
| `/youtube search [--switches] <words>` | Searches with `--playlists`, `--channels`, `--order relevance\|date\|views\|rating\|title`, `--length short\|medium\|long`, `--since today\|week\|month\|year\|<date>`, `--region <cc>`, `--lang <code>`, `--live`, `--captions`, `--safe none\|moderate\|strict`. |
| `/youtube play <id>\|<link> [<time>]` | Plays a video from the time given or the link's own, where *YouTube open links in* says; no key needed. |
| `/youtube play <playlist>\|<link> [<n>]` | Plays a public playlist, or a channel's uploads, from entry *n*, the same way. |
| `/youtube next\|previous`, `/youtube entry <n>` | Plays the playlist's next, previous or *n*th video. |
| `/youtube playlist [<id>\|<link>]` | A playlist's videos in a pane (with a key: 1 unit, plus 1 per 50 videos); alone, the one playing. |
| `/youtube play\|resume`, `/youtube pause`, `/youtube seek <time>`, `/youtube volume <level>`, `/youtube mute\|unmute`, `/youtube close` | Resumes, pauses, seeks, sets the volume (0–100), mutes or unmutes, or closes the player. |
| `/youtube save [<id>\|<link>]` | Saves the video playing, or the one named, to this profile's saved videos. |
| `/youtube list`, `/youtube list --clear` | The saved videos in a pane, as on the Web UI's YouTube tab (each follows the other's changes); `--clear` removes them all after a yes. |
| `/youtube unsave <n>\|<id>\|<link>` | Removes one saved video. |
| `/webui` | Shows the [Web UI](INTERFACE.md#web-ui) (the app's page for a phone or browser): on or off, port, reach, its buttons, and a tab per address with a QR code and sign-in link. Opens over a reply too. |
| `/webui on\|off` | Turns the Web UI on or off for this profile; the server starts or stops at once. |
| `/webui copy` | Puts the first sign-in link (Tailscale's when there is one) on the clipboard. It holds the token: paste it only where you trust. |
| `/webui reset` | Makes a new sign-in token after a yes; every browser signed in is signed out. |
| `/webui browser [--external]` | Opens the Web UI on this PC, already signed in ([how](INTERFACE.md#web-ui)); `--external` always uses the default browser. The Web UI must be on. |

Which commands the Web UI's page may send, and how each shows there: [Commands from the page](INTERFACE.md#commands-from-the-page).

### Integrations

| Command | What it does |
|---|---|
| `/docker` | The containers in a pane, to stop, restart or pause (each asks), start, read logs and more. On a Mac: Docker Desktop, OrbStack, Colima or Rancher Desktop. See [Docker](TOOLS.md#docker). |
| `/docker ps [<filter>]`, `/docker status` | Lists the containers (or those whose name or image holds the filter), or the versions and container counts. |
| `/docker logs <container> [<lines>]` | A container's last lines (50, up to 2000). |
| `/docker stats [<container>]` | CPU, memory, network and disk use of one or every running container. |
| `/docker start\|stop\|restart\|pause\|unpause <container>` | Acts on one container without asking; *Docker writes* doesn't apply, and every change is logged. |
| `/ha` | Home Assistant at a glance: lights on per room, the TV, temperatures, motion, low batteries, to-do lists. See [Home Assistant](TOOLS.md#home-assistant). |
| `/ha on\|off\|toggle <name> [<brightness>]` | Switches a room, light, switch or the TV (`/ha on den 40%`, `/ha off kitchen and hallway`). |
| `/ha scene <name>` | Activates a scene. |
| `/ha tv on\|off\|mute\|unmute\|up\|down`, `/ha tv vol\|volume <level>`, `/ha tv source <name>` | Controls the only media player, sets its volume (0–100) or its source. |
| `/ha states [<filter>]` | Lists entities and their states; an entity id shows all its attributes. |
| `/ha say <sentence>` | Hands a sentence to *Home Assistant Assist agent*. |
| `/imagine [<workflow>] <prompt> [-- <negative>\|--no-negative]` | Makes a picture on ComfyUI from your prompt as typed. See [Imagine options](#imagine-options). |
| `/imagine <prompt> [--seed <n>] [--size <w>x<h>] [--steps <n>] [--count <n>]`, `/imagine <prompt> [--cfg <x>] [--denoise <x>] [--image <path>] [--image2 <path>] [--image3 <path>]` | Sets the seed, size, steps, count, guidance, denoise strength and up to three input pictures. |
| `/comfy` | Opens the ComfyUI tools' page, as `/tools comfy` does. |
| `/comfy status`, `/comfy offered` | The ComfyUI server's status and workflows, or the workflows offered to the model. See [ComfyUI](TOOLS.md#comfyui). |
| `/comfy edit json\|markdown\|md <workflow>` | Opens a workflow's graph, or its `.md` (made if missing), in your editor. |
| `/comfy view`, `/comfy thumbs` | The picture viewer or thumbnail browser on the output folder. |
| `/comfy purge` | Deletes everything in the output folder after a yes, per *File deletes to Recycle bin*. |
| `/print <file> [printer=<name>] [copies=<n>] [pages=<range>] [landscape]` | Prints a file (*Print action policy* never applies). See [Printing](TOOLS.md#printing); a Mac's differences are in [MAC.md](MAC.md#what-differs). |
| `/print reply [<options>]`, `/print printers` | Prints the last reply, or lists the printers. |
| `/pdf <file> [to=<path>] [paper=letter\|a4\|legal] [landscape] [overwrite]` | Makes a PDF of a file, beside it unless `to=` says (*PDF engine*; on a Mac, a browser). See [Making PDFs](TOOLS.md#making-pdfs). |
| `/pdf <url> [to=<path>] [overwrite]`, `/pdf reply [<options>]` | Makes a PDF of a web page (*Web browser network mode* applies; a link that is a PDF already is refused) or of the last reply. |
| `/pdf merge <file> <file>… [to=<path>] [overwrite]`, `/pdf extract\|delete <file> pages=<pages> [to=<path>] [overwrite]`, `/pdf rotate <file> angle=90\|180\|270 [pages=<pages>] [to=<path>] [overwrite]` | Joins PDFs, copies pages out, writes a copy without them, or rotates pages, into a new PDF beside the first. Needs a browser whatever *PDF engine* says. See [Editing PDFs](TOOLS.md#editing-pdfs). |
| `/claude <message>`, `/claude new` | Asks Claude Code and streams its reply, or starts a new Claude Code conversation. See [Claude Code from the chat](#claude-code-from-the-chat). |
| `/gituser [force]` | Writes *GitLib user email* and *GitLib user name* into the repository's config; `force` replaces a `[user]` already there. Needs *GitLib tools*. |
| `/shopping` | The shopping list in a pane, in the active store's aisle order. See [Shopping list](TOOLS.md#shopping-list). |
| `/shopping add [<items>]` | Adds comma-separated items (`milk, 2 eggs, bread x2`); alone, the add page with past purchases. |
| `/shopping check\|uncheck\|remove <item>`, `/shopping rename <item> to <new-name>` | Ticks, unticks, removes or renames an item, by name or number. |
| `/shopping list [<store>]`, `/shopping print [<store>] [verbose]` | Prints the list in a store's order to the screen, or to the printer. |
| `/shopping export [<store>] [verbose]` | Writes what's left to buy to a new `shopping-list-<time>.md` in the working directory. |
| `/shopping clear [all]` | Removes the ticked items; `all` empties the list after a yes. |
| `/shopping store [<name>]`, `/shopping store rename <name> to <new-name>`, `/shopping store add\|remove <name>` | Lists the stores or makes one active, renames one, or adds or removes one (after a yes; its folder stays). |
| `/shopping reindex [<store>]` | Reads a store's reference documents again. |

### Admin

Run the [Admin](TOOLS.md#admin) tools yourself, the [network checks](TOOLS.md#network) among them.

* **Reach:** each command works with its tools off and reaches only the computers or domains ticked for them.
* **The `/net` checks** ignore *Network tools reach*, the host lists and the per-turn budget; a dim line says when the model's own call would be refused.
* **On a Mac:** `/services` and `/tasks` are Windows only; `/eventlog` reads the Mac's own logs ([MAC.md](MAC.md#what-works)).
* **Keys:** the `/eventlog`, `/services`, `/tasks` and `/ad` panes' keys are in [Pane keys](INTERFACE.md#pane-keys).

| Command | What it does |
|---|---|
| `/net ping <host> [<count>]` | Pings one host (4 echoes, 10 at most): answered or not, the loss and the round trips. |
| `/net trace <host> [names]` | The route, hop by hop (30 at most), with each hop's name when `names` is given. |
| `/net port <host> <port>...` | Whether TCP ports (16 at most) are open, refused or silent on each of the host's addresses. |
| `/net dns <name> [<type>] [<server>]` | Looks a name up: A (the default) and AAAA through this machine's resolver; CNAME, MX, TXT, NS, SOA, PTR, SRV and CAA through the app's own client, or the server named. |
| `/net tls <host> [<port>] [smtp\|imap\|pop3\|ldap\|rdp\|none]` | A TLS handshake (443 by default): protocol, cipher, the certificate's names, issuer, days left and chain problems; ports 25, 587, 143, 110, 389 and 3389 speak their preamble first. |
| `/net certs [<store>] [user\|machine] [<words>]` | One of this machine's certificate stores (`my` by default), soonest to end first; nothing exported. On a Mac, `root` reads the system's trust unless `user` is named. |
| `/eventlog` | This PC's event logs (or the only ticked computer's) in a pane, a tab per log, newest first; an event opens with **copy** and **ask** (a draft asking about it). Full text, with *Event log tools* off too. See [Event logs](TOOLS.md#event-logs). |
| `/eventlog <log>\|<evtx-file> [<term>…] [<words>]` | Opens on a log (`app`, `sys`, `sec`, `setup`, `ps`) or an `.evtx` in the working directory, narrowed by `computer:`, `level:`, `id:`, `provider:`, `field:<Name>=<value>`, `since:`, `until:`, `audit:` and words. On a Mac: `system`, `crashes`, `install` or a `.logarchive`, with `level:`, `process:`, `subsystem:`, `category:`, `pid:`, `exclude:`, `since:`, `until:` ([on a Mac](TOOLS.md#event-logs-on-a-mac)). Without the pane, and from the Web UI, it lists the events. |
| `/services [computer:<name>] [state:running\|stopped\|failed\|all] [<words>]` | A computer's services in a pane, a tab per state. Each opens with **copy**, **ask** and, on a `readwrite` computer, the actions `controlActions` allow, each asking; the [checks](TOOLS.md#services) but *Service writes* apply. Elsewhere (the Web UI, no pane), a list. |
| `/tasks [computer:<name>] [folder:<path>] [state:ready\|running\|disabled\|all] [microsoft] [<words>]` | A computer's scheduled tasks, a tab per state; `microsoft` adds Windows' own. Each opens (triggers, actions, account, last result) with **copy**, **ask**, **history** (Task Scheduler's log on) and the allowed actions, each asking; privileged tasks guarded. |
| `/ad <name> [domain:<name>]` | Looks a name up in Active Directory (account, display name, cn, mail, UPN or description) and opens the matches as a card with **copy** and **ask**. Read-only. See [Active Directory](TOOLS.md#active-directory). |
| `/ad user <name>` | A user's card: enabled, locked and when (**Search 4740** when the PDC emulator is a ticked event-log computer), bad passwords, password expiry, last sign-in, groups, manager, direct reports, title, department and OU. |
| `/ad group <name> [nested] [members:users\|computers\|groups] [to:<file-or-folder>]` | A group's card and members, direct or nested (each with the group it comes through). |
| `/ad computer <name>` | A computer's card, or a managed service account's. |
| `/ad ou [<path>] [to:<file-or-folder>]` | The OU tree, or one OU (`Neon/Engineering`, a DN or a name): its owner, linked GPOs and objects. |
| `/ad find <kind> [<days>] [<words>] [type:<type>] [field:<field>] [ou:<path>] [to:<file-or-folder>]` | The kinds of `ad_find`: `locked`, `password_expiring`, `disabled`, `stale_users`, `stale_computers`, `name`, `field`, `os`, `password_never_expires`, `account_expiring`, `created`, `privileged`, `empty_groups`. |
| `/ad domain` | The domain controllers, functional levels, password and lockout policy, fine-grained policies, and the role holders. |
| `domain:<name>`, `to:<file-or-folder>` | Anywhere on an `/ad` line: the domain (a ticked name or its DNS name), and a CSV of the list saved in the working directory (never over a file). Quote a value with spaces: `to:"my list.csv"`. |

### Display and windows

| Command | What it does |
|---|---|
| `/theme [<name>]` | Switches the color theme (*Theme*); alone, a picker with a live preview. |
| `/theme export <name> [<new-name>]` | Writes a theme to a file to edit. See [Custom themes](#custom-themes). |
| `/mark`, `/mark <color>`, `/mark default` | Colors the rules around the input line for this conversation (red, orange, yellow, green, cyan, blue, purple, pink, white or gray), kept with the session; `default` takes it off, and alone it lists the choices. |
| `/header [on\|off]` | Shows or hides the banner (*Show header*) from the next clear. |
| `/toolbar [on\|off]`, `/toolbar edit` | Hides or shows the [toolbar](INTERFACE.md#the-toolbar), or opens its checklist (*Tool bar*). |
| `/perfbar [off\|text\|gauge\|led]`, `/perfbar edit` | Hides or shows the [performance bar](INTERFACE.md#the-performance-bar) in a look, or opens its checklist (*Performance bar*). |
| `/homebar [on\|off]`, `/homebar edit` | Hides or shows the [home bar](INTERFACE.md#the-home-bar), or opens its picker. |
| `/homebar tap <item>`, `/homebar show <item>` | Does what a double-click or a right-click on a chip does; unlocking and opening ask first. |
| `/homebar name <item> = <alias>`, `/homebar emoji <item> = <emoji>` | Gives a chip your own name or emoji (nothing after `=` shows the emoji alone); `default` puts its own back. |
| `/log` | Opens the [log window](INTERFACE.md#the-log-window). |
| `/log file`, `/log path` | Opens the `--log` file in your editor, or copies its path (only when started with `--log`). |
| `/window` | Shows the terminal window's size. |

### Automation

| Command | What it does |
|---|---|
| `/plan <requirement>` | Has the model research with read-only tools and present a plan before anything changes. See [Plan mode](#plan-mode). |
| `/plan [show]`, `/plan <detail>` | While planning, says where the plan stands, or adds detail. |
| `/plan approve [--fresh\|--ralph [passes]]` | Approves the plan by typing (you may edit the file first); `--fresh` clears the context first, `--ralph` runs a [Ralph loop](#ralph-loop) of at most that many passes. |
| `/plan cancel` | Leaves plan mode; the file is kept, marked `canceled`. |
| `/plan save [<name>]` | Keeps a reply that looks like a plan as the plan. |
| `/plan gaps [model\|advisor\|any\|confirm] [passes]` | Reviews the plan pass after pass until passes in a row find no gaps; a count caps this run. See [Closing gaps](#closing-gaps). |
| `/plan chat` | Starts plan mode to talk a plan through, nothing sent; the model writes the plan when you ask. While planning, it turns talking back on. |
| `/plan ready` | Asks for the plan now; once one is presented, opens its approval pane on the current revision without a new turn. |
| `/plan open <name>` | Picks a plan up again; approving runs only the unticked steps. |
| `/plan open <name> --chat`, `/plan open [--chat]` | Picks a plan up to talk it through (nothing sent); with no name, lists the plans to open, talk through or run as a Ralph loop ([keys](INTERFACE.md#pane-keys)). |
| `/plan edit [<name>]` | Opens a plan file in its default app to change it by hand; plan mode is left as it is. Alone, lists the plans and the template to pick from. At the PC only. |
| `/plan template` | Writes an example plan in the Ralph loop's shape to `.neon/plans/plan-template.md`, replacing an older copy. Copy it to a new name to use it. |
| `/plan ralph [<name>] [passes]` | Runs a plan as a Ralph loop, a step a pass, each checked before it stands; alone, it resumes a paused loop, else lists the plans. See [Ralph loop](#ralph-loop). |
| `/code-audit [model\|advisor\|any\|confirm] [<passes>] [diff\|staged\|branch [<base>]\|<path> ...] [<focus> ...] [--] [<brief>]` | Audits the code in read-only passes until passes in a row find nothing new, into a report under `.neon/audits/`. See [Code audit](#code-audit). |
| `/code-audit open [<name>]` | Lists the reports, or shows one. |
| `/code-audit fix [<name>]` | Starts plan mode on a report's open findings (the newest report without a name). |
| `/code-audit resume [<name>] [model\|advisor\|any\|confirm] [<passes>]` | Runs more passes on a kept report (the newest without a name). |
| `/botchat [<profile> ...] [--] [<topic>]` | Lets profiles talk to each other until you stop them. See [Bot conversations](#bot-conversations). |
| `/botchat --resume [<line>]`, `/botchat --kill` | Carries on the last bot conversation, or stops the extra embedded servers the bots started. |
| `/loop <count>\|infinite [<delay>] <message>`, `/loop <count>\|infinite [<delay>] /imagine\|/read <arguments>` | Sends a message, or runs `/imagine` or `/read`, that many times or until Esc. See [Loops](#loops). |
| `/test [<id>\|reasoning\|structured\|long\|all\|history]` | Runs benchmark tests against the connected model. See [Benchmark tests](#benchmark-tests). |
| `/learn [<note>]`, `/learn sessions [<n>\|<search>]` | Writes or improves a skill in the background from the last turn, or stored sessions (the newest 5, *n* up to 20, or a search); *Reflection mode* `update-only` only improves one. Needs *Agent skills* and *LLM offer tools*. |
| `/timer [<duration> [<name>]]`, `/timer stopwatch [<name>]`, `/timer stop <name>\|all` | Lists or starts timers (`10m`, `1h30m`; a bare number is minutes; up to 24 hours), starts a stopwatch (again, it says its time), or stops one or all. A timer rings *Timer tone*, a stopwatch never; works with *Timer tools* off. |

## Command details

### Plan mode

`/plan <requirement>` has the model research and present a plan before anything changes. It needs *LLM offer tools* and is refused during a reply; `/new`, `/clear` and a profile switch leave plan mode.

* **Tools:** only read-only ones (files, git reads, web, database reads, sessions, `ask_user`…) plus `present_plan`. Anything that writes, runs or starts something, and every MCP tool, waits for approval.
  * With `neon_advisor` offered, the model consults the relevant [advisors](#advisor) first.
  * With the web tools, it checks the APIs and versions the plan depends on, and names its sources.
* **The file:** `.neon/plans/<name>.md` in the working directory; 📝 shows while planning. Pictures in the conversation are saved beside it (`<name>/ref-1.png`…, under `## References`). `plan-template.md` there is `/plan template`'s example, never opened or run as a plan.
  * A run sends them with its first message when the model can see pictures (a Ralph pass, the ones its step names).
* **Steps:** each is a `- [ ]` checkbox under `## Steps`, one change a fresh conversation can make and check, with a `Done when:` line (a command where one can tell). The plan stays under 20,000 characters.
  * Its header may carry a `verify:` command (run after each Ralph step) and a `final:` one (run once at the end).
* **Approving:** **Approve & run**, **Approve, clear context & run**, **Approve & Ralph loop**, **Keep refining…**, **Close gaps…** or **Cancel plan** ([keys](INTERFACE.md#pane-keys)).
  * The run ticks the steps; the file ends `done` ("📝 I ❤️ it when a plan comes together.") or `incomplete`.
* **Talking it through:** `/plan chat`, or `/plan open <name> --chat`, opens plan mode with nothing sent. The model discusses, asks and researches, and presents a plan only when you ask (in your words or with `/plan ready`); its title then becomes the requirement.

#### Closing gaps

**Close gaps…** or `/plan gaps` reviews the plan pass by pass. Each pass is one turn that fixes what would make the work stall (a missing step, an open choice, an unread file, an unchecked source, a step too big, a plan too long) and saves a revision.

* **Clean passes:** a review ends after *LLM plan gap clean passes* in a row find no gaps. Small fixes (wording, formatting) don't break a streak.
  * Each pass after a clean one looks at another area (the steps' order, verification, risks, assumptions, size) and must say what it checked.
* **Who reviews** (with advisors offered): the model alone (`m`), an advisor whose role fits (`a`), the model consulting advisors (`y`), or `confirm` (`c`): the model closes gaps and an advisor checks once it finds none (at least 2 clean passes).
* **Unattended:** with *LLM plan gap ask user* off, a pass asks nothing, takes no photo and captures no screen. A decision only you can make goes into `## Risks` as an "Open question (your call):" bullet, which isn't a gap.
* **When it stops:** after the clean passes (the approval pane returns); when a pass changes nothing or repeats itself; on a typed message or Esc.
  * At the limit, the count given or *LLM plan gap passes*; a clean pass there returns the pane unconfirmed. A limit below the clean count lowers that count for the run.
* **The record:** a stop lists every pass (gaps, minor fixes, focus, time, tokens). A review that ends on its own writes a `review:` line into the header.
  * `/plan open` then marks the plan `confirmed` (two clean passes or more), `reviewed` (one) or `edited since review`. Ticking steps is no edit; a new revision drops the line.

### Ralph loop

`/plan ralph <name>` carries out a plan one step at a time, each step a pass in a fresh conversation, held together by the plan file and a log. **Approve & Ralph loop**, `/plan approve --ralph` and the `/plan open` list start one too. It needs *LLM offer tools*; you confirm it once, its checks named (the typed `/plan approve --ralph` too).

* **A pass:** the model gets the plan, its step and `Done when:` line, what earlier passes learned, every tool, and `finish_step` to report `done`, `partial`, `blocked` or `split` (2 to 6 sub-steps, once per top-level step). Each pass is its own session; 🔁 on the Status bar shows the step and pass.
* **Whether a step stands** is the app's call: the `verify:` command runs after each step, then the evidence (files changed or committed) and a review (*LLM Ralph loop reviewer*).
  * The diff the reviewer reads is redacted, and a secrets file (`.env`, a key) is named, never shown.
  * Only the app ticks boxes. A box, a step's words or its `Done when:` line a pass changed are put back, as are the `## Steps` heading and the header's `verify:` and `final:` lines, and a step it added is taken out; a stopped pass decides nothing.
  * The plan as the pass left it is kept in `as-left.md` beside the log, so an edit of your own made during a pass isn't lost. Type a message to pause the loop before you edit the plan.
  * A run that ends mid-pass (a crash, a profile switch) has the plan put back the same way before the next pass. If that pass deleted unfinished steps, the loop won't start until you put them back.
  * If the plan was revised, approved or run since, nothing is put back, and the start says so: check that step's box.
  * The command is approved once with the loop, on one line as the pane shows it; nothing runs under *Shell command policy* `off`, and a resume with it on runs the checks again.
  * A resume runs only the commands its run was approved with, never a `verify:` line written into the plan since.
  * The shell police judge it again before every run, so a script a pass rewrote can't slip past them; a refusal, or the policy turned `off`, stops the loop.
  * A failed check unticks the step, and the next pass gets its output.
* **Before it starts:** the app asks "Start anyway?" on the PC and the Web UI when a plan doesn't suit a loop; a no leaves it presented, to revise.
  * That's one step in a long plan, steps with no `Done when:`, no check with the reviewer off, or a plan too long to send whole (24,000 characters, or a quarter of a smaller window).
* **The baseline:** the checks run once on the project as it is. If `verify:` fails you may start anyway; the steps then go unchecked and verify runs once at the end.
  * If only `final:` fails (say, a test of the work still to do), starting anyway keeps the steps checked.
* **The log:** each pass is an entry in `.neon/plans/<name>/ralph.md` (step, outcome, redacted check output, review, files changed, time, tokens, what it learned); the oldest entries go as it grows. A `ralph.lock` keeps a second Neon Sidekick off the plan: while the loop runs, the other can't run, open, revise or approve it. Mind that folder before you commit `.neon/`.
  * A lock another PC wrote (a shared or synced folder) is never taken over: the error names that PC and the file to delete if its run is gone.
* **Checkpoint:** with *LLM Ralph loop checkpoint* above 0, every N steps that stand ask **Keep**, **Redo with a note…**, **View the changes** or **Stop the loop**, at the PC and on the [Web UI](INTERFACE.md#web-ui).
  * Only then may a pass ask you anything (`ask_user`, the camera, the screen). With neither the pane nor the Web UI to ask on, a run has no checkpoint.
  * Each answer goes in the log, so a Redo's note reaches the next pass after a pause or a restart too.
* **Under *Shell command policy* `ask`,** a pass's own commands still ask, so an unattended loop waits on each; allow the commands it needs, or use `yolo`.

#### When a loop stops

* **When the work is done:** every step standing, and the `final:` command passing (with one pass to fix it).
* **When it can't go on:** a blocked step, two passes with no progress, a step failing its checks twice or using *LLM Ralph loop passes per step*, a pass that deleted unfinished steps, a check refused as it was due, or an error.
* **At a limit or by hand:** the pass cap (*LLM Ralph loop passes*, or the count given), the token budget, Esc, the Web UI's Stop, the wake phrase (its request runs once the loop has stopped), or a working directory change.
* **The stop tone:** a stop you didn't cause rings *LLM Ralph loop stop tone*.
* **A message typed** during a pass, at the PC or on the Web UI, pauses the loop after it. `/plan ralph` resumes (the checks first), `/plan show` says where it stands, and a summary lists every pass. A step that kept failing leaves its edits in place, named.
* **A paused loop resumes only in its own folder.** After a working directory change, `/plan ralph <name>` starts that folder's plan afresh, its checks confirmed again.
* **A plan done or canceled while paused** is never resumed; `/plan ralph` says so and forgets the loop.

### Code audit

`/code-audit` has the model audit the code with read-only tools, pass after pass, until passes in a row find nothing new. It needs *LLM offer tools* and `read_file` (*File tools* on), and is refused in plan mode and during a reply.

* **Scope:** the working directory, or a path (quote one with a blank); `diff` the working changes (unstaged, staged, untracked), `staged` the staged ones, `branch [<base>]` the commits since its base (`main`, else `master`). Past 5,000 files the list is cut, and the start says so.
* **Left out:** git's ignored files (outside a repository, `bin`, `obj`, `node_modules`, `dist` and the like); minified, map and lock files, generated C# and `vendor/` folders unless a named path holds them; secret-looking files (`.env`, keys, certificates) always. `.neon/` is refused.
* **Focus:** security, correctness, performance, maintainability and tests, any mix (`all`; none means *LLM code audit focus*). A pass after a clean one takes one in turn (with one chosen, an angle: edge cases, error paths, concurrency, outside input).
* **The brief:** the first other word, or all after `--`, starts the brief (`/code-audit src -- tests are flaky`); a path-like word that names nothing is refused.
* **Who reviews:** `model` (the default), `advisor` (an advisor finds, the model verifies each line it cites), `any` (asking advisors where a role fits) or `confirm` (an advisor on each pass after a clean one; at least 2 clean passes).
  * A number (0–50, 0 for no cap) replaces *LLM code audit passes* for the run.
* **Tools:** the file, picture, PDF and git reads, advisors, the manual, the clock, skills, memory's recall, `ask_user` and `present_audit`. The web's reads only under *LLM code audit web research* (off: audited code may carry instructions, and the web could send code out).
* **Unattended:** with *LLM code audit ask user* off, a pass asks nothing, takes no photo and captures no screen; a question only you can answer goes into the report's Open questions.

#### The report

* **What it holds:** `.neon/audits/<name>.md`, written by the app. Findings come by severity with stable ids (`A3`, never reused), each with its file and line, fix and quoted evidence the app checked against the file.
  * Then nits, open questions, coverage, what clean passes checked, and the retracted findings.
* **Its rules:** a finding needs its file read this session (a window is enough, a diff is not). You may edit the report during an audit: a ticked box settles a finding; reporting it again reopens it. `<name>.coverage.txt` keeps the files read, so `resume` carries on.
* **Clean passes:** an audit ends after *LLM code audit clean passes* in a row find nothing new; a pass after a clean one reads unread files first.
  * *LLM code audit pass tool calls* is guidance, never a cut-off. The pane says how much of the scope was read.
* **When it stops:** after the clean passes, at the pass limit, when the same findings come back, on a pass that never calls `present_audit`, on a message typed at the PC or sent from the Web UI, on Esc, `/new`, `/clear`, a profile switch or a working directory change (its report stays in its own folder). Each stop says why; `/code-audit resume <name>` runs more passes.
* **The pane:** the open findings, then its choices ([keys](INTERFACE.md#pane-keys)); the Web UI page gets the same ones.
  * **Fix via plan:** plan mode on the open findings, the report marked `fixing`.
  * **More passes…:** the reviewer page when advisors are offered.
  * **Keep report**, or **Discard report** after a yes (to the Recycle bin, the Trash on a Mac, when *File deletes to Recycle bin* is on).
* **Picking up again:** `/code-audit open` lists the reports (`stopped`, `fixing 3/7`, `fixed`…); `fix` and `resume` take the newest without a name, and say which. While `/botchat` runs, a form that would start passes is refused (`open` waits for the chat's end).

### Bot conversations

`/botchat` lets profiles talk to each other until you stop them. Its settings are on `/settings` › Client › [Botchat](SETTINGS.md#botchat).

* **Cast and topic:** the profiles named, or all of them; the loaded one speaks first. The first word that isn't a profile starts the topic (`/botchat ada max the best pizza`), or the rest after `--`; without one, the bots pick.
* **Turns:** each bot speaks in its persona and, with speech on, its own voice; its pronouns come from its first TTS voice (`am_`, `bm_`… male, else female). A bot named in the last line speaks next, else a random one.
* **Servers:** bots use this profile's server, or their own under *Botchat LLM mode* `multi`. `/botchat --kill` stops the extra embedded servers (*Botchat embedded mode*).
* **Tools:** pictures ([Botchat pictures](SETTINGS.md#botchat-pictures)), tools, skills and memory, per the Botchat settings. Advisors are never offered; bots on the Claude Code server get no tools.
* **Joining in:** a line you type joins before the next reply; with *STT input* on, push-to-talk cuts in. Esc stops the voice, then the current bot, then the chat; `/botchat --resume` carries on.
* The chat is saved as its own session under *Session logging*.

### Claude Code from the chat

`/claude <message>` runs the `claude` CLI headless in the working directory. Its settings are on `/settings` › Servers › [Anthropic](SETTINGS.md#anthropic).

* The reply streams in under Claude's name, each tool on a dim line, with a cost footer.
* The pair joins the conversation tagged `[to Claude]` and `[Claude]`, so the local model can build on it; Claude doesn't see the local conversation.
* Each session keeps one Claude conversation; `/claude new`, `/clear`, `/new` and a profile switch start another.
* *Claude Code command permissions* sets what Claude may do; anything more is denied, never asked.
* It works with no LLM server; Esc stops it. Your own Claude Code setup (sign-in, `CLAUDE.md`, skills, MCP servers, hooks) applies.

### Advisor

`/advisor <name> <question>` asks one of the profiles under `/settings` › [Advisors](SETTINGS.md#advisors), as the model's `neon_advisor` does.

* It answers on its own profile's server and model, in its persona and role, with the read-only tools for up to *Advisor max tool iterations* rounds.
* Your question goes with the conversation's last 10 messages, whatever *Advisor context* says.
* The pair joins the conversation tagged with its name (`[to dba]`, `[dba]`), and the advisor remembers it for next time.
* It works whether *Advisors enabled* is on or off and never asks first. Headless, the answer prints as `dba: …`.
* A bare `/advisor` shows each one's server kind (`claudecode`, `embedded`, `docker`, `anthropic`, `openai`, `http` or `scan`); a deleted profile is `(missing)`.

### Your own shell line

`/cmd <command>` runs a line you type in the working directory, as the model's `run_command` would.

* **The same checks:** *Shell command policy* `off` refuses it, the shell police refuse what they always refuse, and under `ask` a line not on *Shell allowed commands* asks.
* **Time:** *Shell default timeout (s)*, at most *Shell max timeout (s)*. Esc stops the line and everything it started.
* **Output** past *Shell output max chars* isn't drawn; the whole goes to a log in the working directory's `.shell` folder.
* **The model sees it**, tagged `[I ran in the shell (<shell>)]` and `[shell output]`. Plan mode doesn't stop `/cmd`.
* **In the background:** `/cmd --bg` gives the chat back at once, with no timeout, up to 16 at a time. Ctrl+K twice in its process window stops it.
* Closing the app stops a background line and all it started, even after a crash (on a Mac, see [Shell](TOOLS.md#shell)).

### Loops

* A delay waits after each reply: `/loop infinite 1m check the build`. It is one word (`30s`, `5m`, `1h30m`, never a bare number), up to 24 hours.
* Esc or Ctrl+C stops the loop; a canceled or failed turn ends it.
* `/imagine …` and `/read …` run with no model in between (`/loop infinite 5s /imagine score_9, 1girl`). No other command can be looped.
* A loop runs from the Web UI's page too (with *Web UI commands* on); the page's Stop ends it, and `/read` can't be looped from there.

### Benchmark tests

`/test <id>` runs one of nine benchmark tests, each one request graded in code.

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

* `/test reasoning`, `structured` and `long` run a group; `/test all` runs everything, saturation last. Alone, `/test` lists the tests and their last verdicts.
* A test sends only its own messages (no system prompt, history or tools). The structured tests are skipped over the Anthropic API.
* The last 50 runs are saved in the profile's `tests.json` (`/test history`). Nothing enters the conversation.

### Imagine options

* It runs behind the input line, so you can keep chatting; double-click 🖼️ on the status bar to cancel.
* The picture is drawn in the transcript, saved in *ComfyUI output folder*, and sent to the model with your next message.
* The first word names the workflow when it matches one; otherwise an offered workflow is used.
* `-- <negative>` sets the negative prompt; `--no-negative` sends none, not even the workflow's own.
* Flags go anywhere; a value with spaces goes in double quotes. `--count` is capped by *ComfyUI max images per call*.
* `--image` takes a path or a pasted picture's label (`"[Image #1]"`); `--image2` and `--image3` feed several-picture workflows (`/imagine faceswap --image a.png --image2 b.png`).

### Camera

A USB or built-in webcam, nothing to install (macOS 14 or later on a Mac). The model's camera tool is in [TOOLS.md](TOOLS.md#camera).

* **The pane** takes, retakes, attaches (`[Image #N]`) or drops a photo ([keys](INTERFACE.md#pane-keys)). Under *Camera preview* `live`, a window shows the shot as you frame it.
* **One stream:** the pane, live view, botchat and watch mode share the camera, which closes a few seconds after the last lets go; 📷 shows on the status bar while it's on.
* **Photos** are JPEGs in *Camera output folder* under the profile's `images` folder; a retaken or dropped one is deleted. Botchat and watch pictures aren't saved. Stored sessions keep a line instead of the picture unless *Camera keep in sessions* is on.
* **Watching** compares each picture, on your machine, with the last one the model saw, and sends it only when enough changed.
* **When it fails** the app says why: the camera privacy setting, another app holding it, unplugged, or a Windows N edition without the Media Feature Pack.
* **On a Mac:** the camera permission, a MacBook's lid and the iPhone camera are in [MAC.md](MAC.md).
* **Known issue:** see [Known issues](TOOLS.md#known-issues).

### Screen capture

A monitor, every monitor or one window, nothing to install (macOS 14 or later on a Mac). The model's screen tools are in [TOOLS.md](TOOLS.md#screen).

* **Targets:** `screen` (the app's monitor, the default), `all`, `monitor:N`, `window:<id or title words>` and `behind` (the window right behind the app's).
* **A window** comes out whole even when covered; a minimized one must be restored first. Protected video comes out black.
* **Screenshots** are JPEGs in *Screen output folder* under the profile's `images` folder, at most 2048 pixels on the longer side.
* **Asking:** under *Screen policy* `ask`, the app asks before each of the model's captures; **Allow for this session** lasts until the session ends.
* **Stored sessions** keep a line instead of the picture unless *Screen keep in sessions* is on: a screenshot can hold anything.
* **On a Mac:** the Screen Recording permission and which windows are listed are in [MAC.md](MAC.md#what-works); `behind` is the window behind the terminal.

### Profiles

* **Names:** 1 to 32 letters, digits, `-` or `_`, and not one of `/profile`'s words (`add`, `push`, `keys`…). A name starting with `_` is temporary, off the picker. The default profile is `neon`.
* **Files:** each profile is a folder in the home's `profiles` folder: `profile.json`, `persona.md`, `operata.md` and `vocalia.md` at its top, its other settings files (memories, MCP servers, connections, saved prompts, videos and links) in `configs`.
* **Starting:** every start opens `neon`, and a switch lasts until the app closes. A desktop shortcut, `--profile <name>` or `NEONSIDEKICK_PROFILE` opens another for a launch; an unknown name exits with code 2.
* **Reset:** keeps the URLs, model, API keys, paths, *Web search method*, *Claude Code executable* and avatars unless `--all`. `neon` can only be reset while loaded.
* **Push and pull** ask first; the bare forms copy only `profile.json`.
* **API keys** in `profile.json` are encrypted (DPAPI on Windows, a key in the Keychain on a Mac), one typed in by hand at the next load. Only you, on this machine, can read them.

### Desktop shortcuts

`/shortcut` puts `NeonSidekick (<profile>).lnk` on your desktop, starting this `NeonSidekick.exe` in its own folder:

| Typed | The shortcut runs |
|---|---|
| `/shortcut` | `NeonSidekick.exe --profile <loaded profile>` |
| `/shortcut jason --log` | `NeonSidekick.exe --profile jason --log "<exe folder>\logs\neon-jason-{ts}.log"` |

* One already there is replaced. `{ts}` becomes each launch's start time, so every run gets its own log.
* It points at the running exe, so make it from the copy you mean to keep.
* **Each profile offers one** (**No**, **Yes** or **Yes, with a log (--log)**) once, at start or on switching to it. Set `"ShortcutOffered": false` in that profile's `profile.json` (`/profile edit`) to be asked again.
* **On a Mac** it is `NeonSidekick (<profile>).command`, with the app's icon: a zsh script Finder opens in iTerm2 when the app runs there, else Terminal. The log goes to `logs/` beside the app.

### Custom themes

The sixty built-in themes are JSON files in [`assets/themes`](../assets/themes), one folder per category; the default is `collider`. [`Theme Atlas.html`](../assets/themes/Theme%20Atlas.html) previews them all.

| Folder | Themes |
|--------|--------|
| `art` | bauhaus, deco, inkwash, kaleidoscope, lapis, stainedglass, ukiyoe, vaporwave |
| `cinema` | akira, arrakis, ghostshell, grid, hal, noir, replicant, starbase, twinsuns |
| `cosmos` | aurora, blackhole, bloodmoon, nebula, orbit, solaris, supernova |
| `elements` | abyssal, magma, opal, oxide, prism, radium, temper |
| `machines` | circuit, collider, commodore, glitch, heartbeat, infrared, mainframe, netrunner, nixie, nostromo, vhs, vinyl |
| `nights` | cyberpunk, kowloon, lighthouse, miami, sakura, synthwave, witchhour |
| `solid` | blueprint, carbon, chalkboard, espresso, fieldradio, glacier, matcha, signal, ultraviolet, velvet (one color, no gradient) |

Your own are JSON files in the home's `themes` folder (and subfolders one level deep), shared by every profile and read again whenever a theme list opens; one named like a built-in replaces it.

Start with `/theme export <name> [<new-name>]`: it writes `themes/<new-name>.json` (`<name>-custom` by default) with every color filled in, and never overwrites a file.

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

* **Colors:** all fifteen are required, as `#RRGGBB` or `#RGB`; a file missing one is skipped. Comments and trailing commas are allowed.
* **Names:** 1 to 32 lower-case letters, digits, `-` or `_`, starting with a letter or digit, and not `export`.
* **Gradient** (the banner title and its rule, 2 to 16 stops) is optional; left out, it comes from the theme's colors.
* **Problems** show as warnings when a theme list opens: a broken file is skipped, a misspelled key or a bad style ignored.

**Color roles** (`colors`):

| Role | What it colors |
|------|-----------------|
| `primary` | The main accent: headings, borders, keywords. |
| `secondary` | The counter-accent: your lines, the spinner, table headers, inline code, the selection. |
| `tertiary` | The third accent: section headings, bullets, the quote bar, function calls. |
| `deep` | Structure: the pane rule, table borders, the dark background under a switched-off toolbar item. |
| `highlight` | String literals. |
| `warm` | Numbers. |
| `tint` | A soft accent: `$variables`. |
| `ink` | Body text. |
| `dim` | Dim text, the status bar and the hints bar. |
| `dimmer` | A step darker: the input row's ghost text. |
| `bg` | The background (unless *Themed background* is off), the picture viewer. |
| `panelBg` | Code blocks, the highlighted menu row. |
| `good` | Success, enabled, connected. |
| `bad` | Failure, error. |
| `warn` | A warning. |

**Styles** (`styles`): each can set `fg` and `bg` (a hex color or a role name like `"dim"`) and turn `bold`, `italic`, `underline`, `dim` or `strikethrough` on or off. Some follow another unless set (`user` follows `accentSecondary`, `assistant` follows `body`).

* **Text:** `body`, `dimText`, `accent`, `accentSecondary`, `accentTertiary`, `label`, `errorText`, `goodText`, `warnText`.
* **Screen:** `user`, `assistant`, `systemText`, `sectionHeading`, `border`, `tableHeader`, `spinner`, `paneRule`, `hint`, `trailerMark`, `pasteLabel`, `placeholder`, `selectedText`.
* **Menus and help:** `menuHighlight`, `menuHighlightDim`, `menuDisabled`, `menuFooter`, `helpForm` (`/help`'s forms), `helpSlot` (their placeholders).
* **Replies:** `markdownBold`, `markdownItalic`, `markdownCode`, `markdownCodeBlock`, `markdownCodeLabel`, `thinking`, `markdownHeading1`, `markdownHeading`, `markdownHeading3`, `markdownBullet`, `markdownQuoteBar`, `markdownQuote`, `markdownLinkUrl`, `markdownRule`.
* **Code highlighting:** `codeKeyword`, `codeType`, `codeString`, `codeNumber`, `codeComment`, `codePunctuation`, `codeFunction`, `codeVariable`, `codeAttribute`, `codeTag`, `codeHeading`, `codeInserted`, `codeDeleted`.
* **File diffs:** `diffAdded`, `diffRemoved` (the background colors the whole line). Keep these fills and `menuHighlight`'s dark: a diff's code, and a menu row's value or check mark, keep their own colors on them.

### Voice presets

*TTS voice preset* (`/settings` › Voice › [TTS](SETTINGS.md#tts)) picks from the seven built-ins (amanda, neon, richard, hunter, larry, jack, willow) and your own.

Yours are JSON files in the home's `voices` folder (and subfolders one level deep), named for the preset. One named like a built-in replaces it; changes apply without a restart.

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

Forty-one more come with the repo in [`assets/voices`](../assets/voices): copy a file, or a whole folder, into your `voices` folder. [`Voice Atlas.html`](../assets/voices/Voice%20Atlas.html) plays a sample of each.

| Folder | Presets |
|--------|---------|
| `narrators` | ada, edmund, iris, margot, silas, walter (unhurried, for long answers) |
| `brisk` | dash, kit, pepper, rex, sloane, zara (quick, 1.3–1.45×) |
| `british` | alfie, beatrice, ellis, harriet, poppy, rupert |
| `characters` | aria, atlas, jinx, maven, nick, wren |
| `duets` | ash, morgan, quinn, river, rowan, sage (a female and a male voice blended) |
| `accents` | amelie, arjun, beatriz, giulia, kenji, lucia, marco, mateo, mei, priya, yuki (an English voice with another language's accent) |
