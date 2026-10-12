# Settings & menus

The full reference for the panes, keys and settings. The [README](../README.md#settings--menus) has the basics. Inside the app, the highlighted row of any pane explains itself, `/help` lists every command and key, and the model's `neon_help` tool answers questions about any setting.

## Contents

- [Navigation](#navigation)
- [The input line](#the-input-line)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Commands typed during a reply](#commands-typed-during-a-reply)
- [Hint row, rule and toolbar](#hint-row-rule-and-toolbar)
- [Panes](#panes)
- [App settings (`/settings`)](#app-settings-settings)
- [Skills settings (`/skills`)](#skills-settings-skills)
- [Tools settings (`/tools`)](#tools-settings-tools)
- [MCP servers (`/mcp`)](#mcp-servers-mcp)
- [System prompt (`/sys`)](#system-prompt-sys)

## Navigation
* **Keys:** ←/→ switch tabs, ↑/↓ move, Enter edits or toggles, Space flips an on/off row, ESC closes (or goes *back* from a page opened inside a pane). Every pane's keys are listed under [Pane keys](COMMANDS.md#pane-keys).
* **Second strip:** some tabs hold tabs of their own, shown as a second row under the first: `/settings`' Client, Servers and Voice, `/tools`' groups, `/skills`' Offered, and `/mcp`'s Tools with two or more servers connected. ←/→ walk through every one and on into the next tab; Tab and Shift+Tab jump a whole tab.
* **Under the list:** the highlighted row's description and default, or on a list pane the details the row has no room for.
* **Tabs:** the current tab is underlined; a small number after a name (`Client³`) counts its settings changed from the default. A tab's rows sit under headings the cursor skips, and may drop the tab's name (*Botchat LLM mode* reads *LLM mode*).
* **Type to filter:** on `/sessions`, `/server`, `/model`, `/docker`, `/youtube list`, the allowed commands and the Offered tabs, typing narrows the list. Backspace erases; the first ESC clears it.
* **Find:** on the read-only panes (`/help`, `/sys`, `/about`, `/tree`, `/docker logs`…) typing finds and marks every match. Enter or F3 goes to the next, Shift+Enter or Shift+F3 to the one before (Shift+F3 on a Mac); the first ESC clears the find. A word wrapped across two rows isn't found.
* **Lists:** removing anything asks first, with No on the cursor. A list cut by the window ends in `▲▼ 13–25 of 58`. On `/tools` Offered, a group that is off says why on its heading.
* **Mouse:** a click moves the cursor; a double-click picks a row or tab (on the input line, selects a word; on the `›` before it, the whole draft). The × at the top right works like ESC, and a double-click outside a pane closes it. A double-click on a picture in the transcript opens the [picture viewer](COMMANDS.md#picture-viewer).

## The input line
* **A full editor**, even during a reply or `/botchat`: ←/→, Home/End (press again for the whole message), Ctrl+←/→ by word, Ctrl+Backspace / Ctrl+Delete delete a word (Option+Delete on a Mac), Shift+arrows, Ctrl+A or a drag to select, Ctrl+C / Ctrl+X copy / cut, right-click or Alt+V pastes (a copied picture too; on a Mac also Ctrl+V or Option+V), ↑/↓ walk the history.
* **Lists:** `/`, `@`, `#`, `$`, `%`, `^` and `*` open their lists. Mentions work inside a command's text too, except in `/read`, `/view`, `/print` and `/pdf`, which complete their own path.
* **Pictures:** drag one from the ComfyUI picture strip or the transcript onto the input row to attach it (the hint row reads **🖼️ drop on line**).
* **During a reply:** Enter queues the message, and a draft on the row survives. ESC stops the speech, then closes an open list, then cancels the reply; it never clears your draft. The hint row ends with **esc to stop**.
* **At the idle line:** ESC clears the draft (↑ brings it back); ESC twice on an empty line opens `/rewind`.
* **Tool calls** read as their values (`🛠️ read_file notes.md`) with the result under them (`→ 4 lines`). A result of several lines folds to its first line once the run ends (Ctrl+O or `/expand` shows it again); a failed one is marked ✗.
* **During `/read`** (📖 on the hint row): Space on an empty line pauses and resumes, and ←/→ step a sentence back or forward.

## Keyboard shortcuts
Each shortcut runs its command as if typed; a draft on the row stays.

| Shortcut | Runs | During a reply |
|---|---|---|
| Ctrl+. | `/terminal` | at once |
| Ctrl+/ | `/settings` | opens over the reply |
| Ctrl+E | `/explore` | at once |
| Ctrl+F | `/perfbar` (performance bar on/off) | at once |
| Ctrl+G | `/log`: opens or closes the log window | at once |
| Ctrl+Shift+F | `/find` (find in the transcript; Windows Terminal keeps this chord until you unbind it) | waits for the reply to end |
| Ctrl+H | `/help` | opens over the reply |
| Ctrl+L | cancels the background learning (🧠) | at once |
| Ctrl+M | `/model` | waits for the reply |
| Ctrl+P | `/profile` | waits for the reply |
| Ctrl+Q | `/queue` | opens over the reply |
| Ctrl+R | `/reasoning` | opens over the reply |
| Ctrl+S | `/server` | waits for the reply |
| Ctrl+T | `/toolbar` (toolbar on/off) | at once |
| Ctrl+U | `/usage` | opens over the reply |
| Ctrl+W | `/homebar` (home bar on/off) | at once |
| Ctrl+Y | `/sys` | opens over the reply |
| Ctrl+Z | `/theme` | waits for the reply |
| Ctrl+Alt+B | `/botchat --resume`: carries on the last botchat | during a botchat, ends it at once; otherwise waits for the reply |
| Ctrl+Alt+C | `/clear` | stops the reply first |
| Ctrl+Alt+N | `/new` | stops the reply first |
| Ctrl+Alt+P | `/splash` | stops the reply first |
| Ctrl+Alt+Q | `/queue clear` | at once |
| Ctrl+Alt+D | `/mcp` | opens over the reply |
| Ctrl+Alt+E | `/sessions` | opens over the reply |
| Ctrl+Alt+H | `/header` | at once |
| Ctrl+Alt+K | `/botchat --kill`: stops the extra embedded servers a botchat left | waits for the reply (or the botchat) to end |
| Ctrl+Alt+L | `/tools shell allowed` | opens over the reply |
| Ctrl+Alt+M | `/memory` | opens over the reply |
| Ctrl+Alt+O | `/tools shell police` | opens over the reply |
| Ctrl+Alt+R | `/rename`: the rename box | opens over the reply |
| Ctrl+Alt+S | `/skills` | opens over the reply |
| Ctrl+Alt+T | `/tools` | opens over the reply |
| Ctrl+Alt+U | `/comfy view`: opens or closes the picture viewer | at once |
| Ctrl+Alt+V | `/camera live`: opens or closes the camera's window | at once |
| Ctrl+Alt+X ×2 | unloads the embedded model (see below) | at once |
| F9 | `/camera snap`: a photo, attached to the line | waits for the reply |
| F10 | `/screen`: a capture of the app's monitor, attached to the line | waits for the reply |

* **Inside a pane**, a shortcut closes the pane and runs, so Ctrl+Alt+S in `/tools` opens `/skills`; a pane's own shortcut just closes it. Ctrl+E, Ctrl+., Ctrl+F, Ctrl+L, Ctrl+T, Ctrl+W, the three window chords and Ctrl+Alt+X act and leave the pane open.
* **Panes that ask you something** (an approval, `ask_user`, a confirmation) ignore the shortcuts, so none can answer them by accident.
* **Window chords** close their window when pressed again; the typed command only opens it.
* **Ctrl+Alt+X**, pressed twice within two seconds, unloads the embedded model and frees its memory, cancelling any reply, load or botchat using it. No server is connected until you pick one with `/server`; the saved *LLM URL* is kept. With any other server it does nothing.
* **Ctrl+L** cancels a running skill-learning reflection (🧠 on the hint row); a double-click on 🧠 does the same.
* **F9 and F10** work only with no modifier held. In the picture viewer and thumbnail browser they keep their own meanings.
* **On a Mac** three chords have stand-ins: **Ctrl+]** for Ctrl+. (`/terminal`), **Ctrl+D** for Ctrl+M (`/model`) and **Ctrl+Option+A** for Ctrl+Alt+M (`/memory`). Ctrl+Shift+F arrives as Ctrl+F, so type `/find`. The Ctrl+Option chords need Option to act as Alt (see [BUILD.md](BUILD.md#building-on-a-mac)).
* **A chord that does nothing** may be held by another program as a global hotkey (a graphics overlay, say). `/keycheck` lists every chord and whether another program holds it; so does the *⌨ key check* button (`k`) at the top of `/help`'s Keys tab, pressed again to show the keys.

## Commands typed during a reply

| Behaviour | Commands |
|---|---|
| Open their pane over the reply | `/help`, `/settings`, `/tools`, `/mcp`, `/sys`, `/usage`, `/about`, `/memory`, `/queue`, `/sessions`, `/sessions title`, `/rename`, `/skills`, `/reasoning`, `/sampling`, `/msgclear`, `/tree`, `/vault`, `/browse`, `/camera list`, `/docker logs`, `/ha states`, `/keycheck`, `/process`, `/sys persona`, `/sys operata`, `/sys vocalia`, `/homebar edit`, a home bar chip's pane and its Yes/No |
| Run at once | `/ha`, `/camera live`, `/camera watch`, `/camera off`, `/camera use`, `/camera view`, `/camera thumbs`, `/screen view`, `/screen thumbs`, `/tts`, `/stt`, `/stt wake`, `/stt interrupt`, `/perfbar`, `/homebar`, `/homebar on`, `/homebar off`, `/toolbar`, `/reasoning <level>`, `/sampling <field> <value>`, `/queue clear`, `/copy`, `/remember`, `/explore`, `/open`, `/terminal`, `/shortcut`, `/log`, `/process <id>`, `/timer`, `/expand`, `/collapse`, `/window`, `/cwd`, `/comfy view`, `/comfy thumbs`, `/view <path>`, `/view <path> --thumbs` |
| Stop the reply first | `/clear`, `/new`, `/splash`, `/rewind`, `/exit` |
| Everything else | Waits for the reply to end, queued behind earlier messages (*Queue cancel mode* applies) |

## Hint row, rule and toolbar

**Long setups run in the background:** an embedded model's download, MCP servers connecting, and the voice models' first download and load. The input line stays yours; a glyph (📥, 🔌, 🎙️ or 🔈) shows progress on the hint row.

| Double-click | Does |
|---|---|
| the model name | `/server` (server, then model, then reasoning) |
| the reasoning glyph | `/reasoning` |
| the tokens or spinner | `/usage` |
| 🖼️/🎨 and its timer (ComfyUI rendering) | cancels the pictures; the reply goes on |
| 📥 / 🔌 / 🎙️ / 🔈 (setting up) | cancels that download or setup |
| the queued count | `/queue` |
| 📷 (camera on) | `/camera off` |
| 👥 (a botchat to resume) | `/botchat --resume` |
| 🧟 (extra embedded servers a botchat left running) | `/botchat --kill` |
| blank space on the hint row | `/settings` |
| the session name on the rule above the input row (a model-written name reads as words, `Summary of notes file`) | renames it, like `/sessions title` |

**⤡** at the left end of that rule appears when something can fold; a click folds or unfolds everything, like Ctrl+O.

**The toolbar** (*Show toolbar*) sits under the hint row by default: *Toolbar order* puts the hint row, the toolbar, the performance bar and the home bar in any order. One click on a glyph says what it is; a double-click opens its pane or window. A tool group that is off sits on a dark slab. A strip too wide for the window ends in `+N`, and the working directory shows your profile folder as `~`.

| Toolbar item | Shown | Opens |
|---|---|---|
| ⚙️ 🪪 🧮 🛠️ 🔌 🎓 🎭 💬 📊 | always | `/settings`, `/profile` (the profile picker), `/theme` (the theme picker), `/tools`, `/mcp`, `/skills`, `/sys`, `/sessions`, `/usage` |
| 💾 | always; on the slab while *Memory mode* is `disabled` | `/memory`, with **read-write** (`w`), **read-only** (`o`) and **disabled** (`x`) on its title row |
| 🙋 | always; on the slab while *Ask user* is off | `/tools ask`: the *Ask user* on/off page |
| 🔒 / 🔓 | *Shell command policy* is `ask` / `yolo` (none under `off`) | `/tools shell allowed` |
| 👮 / 🥷 | *Shell police* is on / off, and the policy isn't `off` | `/tools shell police` |
| 🐚 | always; on the slab under `off` | `/tools shell`: the *Shell command policy* picker (`c` the allowed commands, `p` the police) |
| 📁 🌄 🌐 🦉 🐳 💎 🛢️ 🔮 🐬 🪶 🐘 🔗 🏠 🎨 📸 🖨️ | always; on the slab while off | `/tools files`, `image`, `web`, `advisors`, `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `sqlite`, `postgres`, `unc`, `ha`, `comfy`, `camera`, `print`: that group's on/off page, with its own buttons at the top (an **offered** checklist for the database, UNC and ComfyUI groups; **watch**, **live**, **snap** and **screen** for the camera) |
| 📄 | always | `/log`, the log window (Ctrl+G) |
| ⚡ | always | `/process`, the background processes' list |
| 🎞️ | always | `/comfy view`, the picture viewer (Ctrl+Alt+U) |
| 🪟 | always | `/comfy thumbs`, the thumbnail browser on the ComfyUI output folder |
| 📺 | always | `/youtube list`, the saved videos |
| 🛒 | always | `/shopping`, the shopping list's checklist |
| 📈 | always | `/perfbar`: hides or shows the performance bar |
| working directory (right edge) | always | `/cwd browse` |
| blank space | — | `/settings` |
| performance bar (anywhere on it) | *Show performance bar* has a meter checked | `/settings` |
| home bar: ⌂ | *Show home bar* has an item | a click says how the connection stands; a double-click (or a right-click) opens the picker |
| home bar: a chip | *Show home bar* has an item | a click shows its state; a double-click acts (see [Home bar](#home-bar)); a right-click opens its pane |
| home bar: ‹ › | the chips need more room than the row has | the previous or next page of chips |
| home bar: blank space | *Show home bar* has an item | `/settings` |

## Panes
* `/settings`: the app (Client: Chat, Sessions, Botchat), the LLM and its servers (Servers: Common, Http, Embedded, Docker, Anthropic, OpenAI), the profile advisors (Advisors) and voice (Voice: TTS, STT)
* `/skills`: agent skills and self-reflection
* `/tools`: the model's tools
* `/mcp`: external MCP servers
* `/sys`: a read-only view of what the model is sent; `/sys persona`, `/sys operata` and `/sys vocalia` edit the files it is made from
* `/usage`: LLM usage statistics (tok/s, time to first token…)

Settings an environment variable or flag can override for one launch are listed under [Environment variables](ENVIRONMENT.md). Paths here use Windows' `\`; on a Mac read `/`.

## App settings (`/settings`)

### Client

The app itself, this side of the connection, in three tabs: Chat, Sessions and Botchat.

#### Chat

| Setting | What it does | Default |
|---|---|---|
| Profile | Switches profile (each has its own settings, persona, memory, skills and sessions). | `neon` |
| Working directory (cwd) | The folder the file and GitLib tools work in; empty is the profile's `files\` folder. The row opens the folder picker; `/cwd <path>` sets one by hand. | profile's `files\` |
| Memory mode | `read-write`: the model can save and recall memories, and every conversation opens with them. `read-only`: it can recall but not save (your `/remember` still can). `disabled`: no memory, and `/remember` is refused. | read-write |
| Avatar | The profile's emoji, shown at the start of its replies, its `/botchat` lines and the window title. Enter (or `/avatar`) opens the [picker](#the-avatar-picker); 🚫 or `/avatar clear` leaves none. `/profile push`/`pull` never copy it. | 🤖 |
| Queue mode | `automatic`: messages sent during a reply wait in `/queue` and go when it ends. `manual`: every message is queued, even at the idle line, and goes only when you press `➤ send` (`s`) on `/queue`, one per press. `disabled`: no list, but a line sent during a reply still goes when it ends. | `automatic` |
| Queue cancel mode | What a cancelled reply does with the queue: `hold` keeps it until your next message, `drain` sends the next one at once, `empty` drops them all. | `empty` |
| Keep message history | Saves the ↑/↓ history (newest 1,000 lines) in `sessions.db`. Off deletes it at the next profile load; `/msgclear` empties it. | on |
| Command typo intercept | A command typed without its slash (`clear`) or with extra ones asks *Did you mean /clear?* first. | on |
| Hide /exit autocomplete | Leaves `/exit` out of the `/` list; typing it in full still works. | on |
| Emoji picker | Shows a dim ☺ at the right end of the input row; a click opens the emoji picker and inserts the emoji at the cursor. | on |
| Transcript markdown | Renders replies as Markdown, with code blocks highlighted for common languages. | on |
| Paste preview lines | How many lines of a long paste show dimmed under its `[Pasted text #n]` placeholder (0–200). | 25 |
| Show image thumbnails | Draws a small thumbnail of each picture you send, a tool fetches or makes, or `/botchat` draws. | on |
| Image thumbnail size | `tiny` (32×8), `small` (48×12), `medium` (64×16), `large` (80×20), `xlarge` (96×24) columns × rows, or `fullsize`. | `small` |
| Copy user prompt | `/copy` includes your prompt above the reply. | on |
| User line style | How your sent line looks: `quiet` (plain words), `slab` (on a faint fill) or `bold` (bold in the user colour). | bold |
| Theme | One of the sixty built-in themes (see [Themes](COMMANDS.md#custom-themes)) or your own. A wide window previews the highlighted theme. | `collider` |
| Themed background | Gives the terminal the theme's background while the app runs. Off keeps the terminal's own. | on |
| Themed external windows | The app's own windows (picture viewer, thumbnails, camera, log, video) wear the theme. Off, they stay black. | on |
| Welcome splash | Pictures under the banner at startup until your first line: `fullsize`, `tiled` or `disabled`. See [Welcome splash](#welcome-splash). | `fullsize` |
| Show header | Shows the banner at startup and after `/clear`, `/splash`, `/theme` and a profile switch. `/header` and Ctrl+Alt+H flip it. | on |
| Working directory in header | Prints the working directory at the right of the banner's title line. | off |
| Show toolbar | A checklist of the toolbar's glyphs and the working-directory path. `a` / `n` / `d` pick all, none or the default (`d` puts the order back too); none hides the row. Shift+↑↓ moves the item under the cursor a place and Shift+Home/End to the top or bottom, and the toolbar draws its glyphs in that order (the path stays last). Del unchecks. | Settings, Tools, Skills, Memory, Shell allowed commands, Shell police, Shell, Files, Image, Web, path (11 of 38) |
| Show performance bar | A checklist of the bar's meters: **CPU**, **RAM**, **GPU**, **VRAM** (**GMEM** on a Mac), **NET**, **NET↓**, **NET↑** and **PROC** (background processes). `/perfbar text`, `/perfbar gauge` or `/perfbar led` picks the look. None hides the bar. Shift+↑↓ moves the meter under the cursor a place and Shift+Home/End to the top or bottom, and the bar draws its meters in that order; `d` puts back the default meters and order. Del unchecks. See [Performance bar](#performance-bar). | CPU, RAM, GPU, VRAM, `led` |
| Show home bar | Home Assistant rooms and devices as chips on a row under the performance bar (by default; see *Toolbar order*), in the order you choose. Enter opens the picker. Needs *Home Assistant URL* and *Home Assistant API key*. See [Home bar](#home-bar). | off |
| Toolbar order | The order of the four rows under the input line, top to bottom: **Hint bar** (the hint row, the busy row while a reply runs), **Toolbar**, **Performance bar** and **Home bar**. Shift+↑↓ (or Alt+↑↓) moves the bar under the cursor a place and Shift+Home/End to the top or bottom; the pane redraws in the new order at once. `d` (*default* on the title row) puts the order back. Only the order: each bar's own row shows or hides it, and one that is off keeps its place. A short window still drops the home bar first, then the performance bar, then the toolbar. | `default` (hint, toolbar, performance, home) |
| Menus max height | How much of the window a pane may take: `half-screen`, `three-quarters` or `full-screen`. Longer lists scroll. | `full-screen` |
| Draft editor | The program `/draft` opens (`code --wait`, `notepad`…); it must wait for the file to close. Empty uses the system's `.txt` editor. | (default .txt editor) |
| Image viewer | Where a double-clicked picture opens: empty for the built-in viewer, `system` for the system's app, or a command the path is added to (`mspaint`). | (built-in viewer) |

##### The avatar picker

* Enter on *Avatar* (or `/avatar`) opens a grid of every emoji Windows Terminal draws (Unicode Emoji 15.1, no flags).
* Tabs: **⭐ For you** (emoji matching the profile's name and persona), **🕘 Recent** (avatars in use and your last picks), then one per Unicode group. Tab and Shift+Tab step the tabs; the arrows move in the grid; a double-click takes a cell.
* Type to search by name and keyword (`fox`, `red heart`); you can also paste an emoji. ESC clears the search.
* 🎲 picks one at random; 🚫 clears the avatar.
* Enter on a hand or person opens its six skin tones.
* Other profiles' avatars are marked, so bots in `/botchat` stay easy to tell apart. The band under the grid shows how the title and a reply will read.
* Recent picks and the last skin tone live in `emoji.json` in the home folder.

##### Welcome splash

* `fullsize` fills the screen with one picture; ←/→ step through the set. Delete twice on an empty line moves one of the profile's own pictures to the folder's `.trash`.
* `tiled` shows thumbnails at *Image thumbnail size*; ←/→ page, and a double-click opens one.
* Pictures in the profile's `images\splash_images\` folder replace the built-in ones (`set_splash_image` copies one there).
* `/splash` shows the splash whatever the setting.

##### Performance bar

* Values turn amber from 60 % and red from 85 %. A meter the machine can't read is left out.
* NET is the share of the link speed; NET↓ and NET↑ show bits per second (`850K`, `12.4M`). They follow the busiest adapter with a gateway.
* PROC counts the model's background processes still running (`/process` lists them).
* **On a Mac** RAM is Activity Monitor's *Memory Used*, and **GMEM** is the memory the GPU holds, as a share of what macOS lets it use.
* `/perfbar` or the toolbar's 📈 hides or shows the bar.

##### Home bar

* **What it shows**: each chip is a room or a device. A light's chip takes the light's own colour (its brightness is in the click's note, so the chips keep their widths); a room's counts its lights on (`2/5`); sensors show their value, thermostats current → target, players a play or pause mark (the title is in the click's note). The chips sit on the page and their words carry the state: an on chip's are in the light's (or the state's) colour, an off chip's are dim; an open garage, gate or door, a leak or a jammed lock is bold amber or red, and a low battery turns amber or red. While the connection to Home Assistant is down the chips go dim and the ⌂ at the left turns amber (retrying) or red (refused, or not set up).
* **Clicks**: a click shows a chip's state and what a double-click will do. A double-click acts: a light, switch or fan toggles; a room's lights go off when any is on, else on; a scene or script runs; a player plays or pauses (or turns on); a cover closes, or opens. A right-click opens the chip's pane: its actions (on, off, brightness steps, volume, open, close, stop, lock, warmer, cooler…), or for a room its devices, Space switching one and Enter opening its actions. A chip clicked shows `…` until Home Assistant reports the change, and `!` if none comes within six seconds.
* **What asks first**: unlocking a lock, opening a garage, gate or door, and disarming an alarm ask Yes or No on the pane (the cursor on No). An alarm that wants a code is disarmed in Home Assistant itself.
* **The picker** (Enter on *Show home bar*, `/homebar edit`, or a double-click on ⌂): **On the bar** lists the items in order. **Shift+↑/↓** move the item under the cursor a place, **Shift+Home/End** to the top or bottom, **Delete** takes it off, and Enter offers the same as a list (for a terminal that keeps those keys) plus **Rename on the bar…**: an alias the chip shows in place of Home Assistant's name ("Den PC Monitor" as "PC"; blank takes it off). The alias is the chip's label only: a click's note, the chip's pane and this row keep the real name, and the other tabs show it quoted beside the item. Then **Rooms** (by floor), **Lights**, **Switches** (and fans), **Sensors**, **Climate**, **Media**, **Scenes** (and scripts), **Security** (locks, covers, alarms), **Zones** (how many people each holds) and **Other**, each in sections by room: Space or Enter adds an item at the end of the bar or takes it off; typing filters by name, id, room or label.
* **Live**: while the bar shows, the app keeps one WebSocket to Home Assistant, signed in with the API key and watching only the bar's devices (and an open room pane's), so a change shows at once. Hidden for 15 seconds, it closes. A dropped connection is retried after 1, 2, 4… up to 30 seconds; a refused key is not retried until the URL or the key changes.
* A short window drops it before the other bars, wherever *Toolbar order* puts it. A double-click on the bar's blank space opens `/settings`, as on the other bars. `/homebar` or Ctrl+W hides it, or shows it again with the items it had.

#### Sessions

| Setting | What it does | Default |
|---|---|---|
| Session logging | Saves every turn to the profile's `sessions.db` for `/sessions` to list, search and restore. | on |
| Session save thinking | Saves each reply's thinking, so a resumed session can send it back under *LLM preserve thinking*. | off |
| Session retention (days) | Deletes sessions older than this at startup (0–3650; 0 = keep forever). | 0 |
| Session naming mode | `model-written` asks the model for a title after the first turn; `first-line` uses your first line. | `model-written` |
| Session show name | Which titles show on the rule above the input row: `all-names`, `model-written` (model-written or typed names only) or `none`. | `all-names` |
| Session tool | Offers `session_manager`, to search, list and read earlier sessions (never restore or purge). | on |
| Session search max results | Sessions one search or list returns (1–20). | 10 |

#### Botchat

| Setting | What it does | Default |
|---|---|---|
| Botchat LLM mode | `single`: every bot uses this profile's server, model and reasoning. `multi`: each bot uses its own profile's (a blank URL borrows this one's). | `single` |
| Botchat multi-embedded | Under `multi`, for bots wanting a different embedded model. `parent-server`: they share the running model. `multi-server`: one extra `llama-server` per model. | `parent-server` |
| Botchat multi-embedded kill | Stops `multi-server`'s extra servers when the chat ends. Off, they run until `/botchat --kill` or you quit. | on |
| Botchat ComfyUI enabled | Gives the bots this profile's *ComfyUI workflows offered* for pictures. Needs *ComfyUI tools* and a *ComfyUI URL*. | off |
| Botchat ComfyUI limited workflows | With *Botchat ComfyUI enabled* off, the installed workflows the bots get (a checklist). None ticked: no pictures. | (none) |
| Botchat image mode | `automatic`: the app writes a prompt from each reply and draws it. `autonomous`: the bots get `generate_image` and draw when they choose. See [Botchat pictures](#botchat-pictures). | `automatic` |
| Botchat img2img mode | Which pictures a rework may start from: the `latest`, or any in `chat-history` (the last 8). Needs an image → image workflow. | `latest` |
| Botchat image async | On: the next bot speaks while a picture renders. Off: each reply waits for its picture. | on |
| Botchat non-TTS delay | A reading pause after each reply when *TTS output* is off (0–30 s). A line you send ends the pause. | 5 |
| Botchat tools enabled | Offers every bot the tools a turn of this chat would get, with the same approvals. Off, *Botchat limited tools* decides. | off |
| Botchat limited tools | With *Botchat tools enabled* off, the tools the bots get (a checklist grouped as on `/tools`). None ticked: no tools. | (none) |
| Botchat skills enabled | Offers every bot `load_skill` over this profile's, the global and the external skills. Needs *Agent skills*. Off, *Botchat limited skills* decides. | off |
| Botchat limited skills | With *Botchat skills enabled* off, the skills the bots may load (a checklist). None ticked: no skill tool. | (none) |
| Botchat memory enabled | Gives every bot memories (`save_memory`, `recall_memory`). Inside `/botchat` this overrides every profile's *Memory mode*. | on |
| Botchat memory mode | Whose memories: `shared-parent` (the starting profile's) or `independent` (each bot's own). | `shared-parent` |
| Botchat vision enabled | Shows each bot the newest 4 pictures since it last spoke. Needs models that read images. Off, the bots see text alone. | off |
| Botchat camera | Each bot turn gets a fresh camera picture of you, kept in memory only. Needs models that read images. | off |
| Botchat recent context | Tells every bot the last 10 messages of this chat from before `/botchat` began, so the topic can point at them. | off |

##### Botchat pictures

* **The botchat workflows** are this profile's offered ones (*Botchat ComfyUI enabled*) or the ticked ones (*Botchat ComfyUI limited workflows*). Text → image workflows draw new pictures; image → image ones rework the chat's pictures.
* **`automatic`:** after each reply the model writes an image prompt and the app draws it. The bots get no tool.
* **`autonomous`:** the bots get `generate_image`, limited to the botchat workflows. A tool call a bot writes out as text still runs as a real call.
* **Reworks:** once there's a picture and an image → image workflow, the prompt writer chooses between a new picture and a rework.
* **Async on:** 🖼️ (🎨 for a rework) shows on the hint row while a picture renders. **Async off:** ESC on a held reply cuts that bot short; ESC under the picture's spinner skips that picture.
* **Skills:** with skills on, the `automatic` prompt writer may load a skill first ("use the pony-prompts skill for pictures").

### Servers

The LLM connection and every kind of server it can be, in six tabs: Common, Http, Embedded, Docker, Anthropic and OpenAI.

#### Common

Settings every server reads, whatever its kind.

| Setting | What it does | Default |
|---|---|---|
| LLM URL | The server's OpenAI-compatible base URL (`http://127.0.0.1:1234/v1`), `embedded`, or `docker:<container>`. `/server` fills it in. Empty: the app scans and lets you pick a server, model and reasoning level at startup. | (none) |
| LLM model | The model id. Empty takes the first the server lists; `/model` picks one. | (first listed) |
| LLM reasoning | How hard the model thinks: `none`, `low`, `medium`, `high` or `xhigh`. `/reasoning` opens the same list. | `none` |
| LLM show thinking | Streams a reasoning model's thinking as a dim block, folded to one line when the answer starts (click, Ctrl+O or `/expand` to see it). Thinking is never spoken or logged. | on |
| LLM offer tools | Whether the model gets any tools. Turn it off for chat templates with no tool role. A change starts a new conversation. | on |
| LLM max tool iterations | Tool round trips one message may make (1–10000). | 10000 |
| LLM request timeout (s) | The longest one HTTP request may take (up to 3600). | 3600 |
| LLM turn timeout (s) | The longest one whole turn may take, tool calls included (up to 21600). | 21600 |
| LLM context length | The context window in tokens, for the usage percentage. 0 takes the server's figure. | 0 (server) |
| LLM mid-turn usage | The hint row's usage during a reply: `estimate` (live, marked `~`) or `last-known` (the last completed request's figures). | `last-known` |
| LLM max turns | User turns the model sees before the oldest drop off (1–500). `auto` keeps them all while auto compact can run, else 24. | `auto` |
| LLM auto compact (%) | How full the context may get before the next message compacts it (1–100; 0 = off). | 90 |
| LLM compact type | What `/compact` does: `summary` folds older turns into a model-written summary; `prune` swaps their bulky tool results for stubs. | `summary` |
| LLM compact keep recent | Recent user turns a compact keeps word for word (0–24). | 2 |
| LLM compact show summary | After a compact, shows the summary (or the pruned results) and how many messages were kept. | off |
| LLM tool compact type | What happens when one turn's tool calls fill the context to the auto-compact share: `compact` (prune, then summarise), `prune`, `stop` the turn, or `nothing`. | `compact` |
| LLM picture keep | The most pictures one request carries (0–500; 0 = no cap). Past it the oldest leave the conversation (`view_image` can show one again). Every picture is resent with every request. | 20 |
| LLM picture megabytes | The most megabytes of pictures one request carries (0–1000; 0 = no cap); past it the oldest leave. | 24 |
| LLM use fun verbs | The thinking spinner shows a random verb instead of `thinking` / `writing`. | off |

#### Http

Settings only an OpenAI-compatible HTTP server reads: a local or LAN server (LM Studio, llama.cpp, vLLM, SGLang, Ollama), the embedded LLM and Docker containers. The Anthropic API, the OpenAI API and the Claude Code server ignore them.

| Setting | What it does | Default |
|---|---|---|
| LLM server scan mode | Where to look for a server while *LLM URL* is blank: `local` (the usual ports here), `remote` (the same ports across the LAN), `both`, or `disabled`. | `disabled` |
| LLM API key | The bearer token the server expects (`empty` for none). Saved encrypted (DPAPI on Windows, the Keychain on a Mac). | `empty` |
| LLM preserve thinking | Sends earlier turns' thinking back to a local server and asks the chat template to keep it. Costs context. | off |
| LLM reasoning estimate | How `/usage` counts reasoning a server streams but doesn't count: `chars` (characters ÷ 4), `tokenize` (llama.cpp's exact count) or `off`. | `chars` |
| LLM sampling | Per-model sampling overrides; Enter opens the `/sampling` pane. See [Sampling per model](#sampling-per-model). | (server defaults) |
| LLM sampling from Hugging Face | For servers that don't report their defaults (vLLM, SGLang, LM Studio), shows the model card's values in `/sampling`. Display only; your key is never sent. | off |

#### Sampling per model

Until you override them, sampling is left to the server and the model's defaults.

* `/sampling` (or the *LLM sampling* row) opens one tab per model: the connected model, then **any model (`*`)**, then every other model you have set something for.
* Each field comes from the model's tab, else from `*`, else isn't sent. Changes apply at the next turn.
* A blank value clears a field. Out-of-range values are refused.

| Field | Accepts | Sent as | Servers that honour it |
|---|---|---|---|
| temperature | 0 to 5 | `temperature` | all |
| top_p | above 0, up to 1 | `top_p` | all |
| top_k | a whole number from -1 (0 or -1 is off, depending on the server) | `top_k` | vLLM, SGLang, llama.cpp, LM Studio |
| min_p | 0 to 1 | `min_p` | vLLM, SGLang, llama.cpp |
| presence_penalty | -2 to 2 | `presence_penalty` | all |
| frequency_penalty | -2 to 2 | `frequency_penalty` | all |
| repetition_penalty | above 0, up to 2 (1 is none) | `repetition_penalty` **and** `repeat_penalty` | vLLM and SGLang read the first, llama.cpp and LM Studio the second |
| extra body | a JSON object | its fields, top level | whatever the server knows: `{"typical_p":0.9,"dry_multiplier":0.8,"seed":42}` |

* **Server defaults** show dim on the connected model's tab (`0.8 (server)`) where the server reports them (llama.cpp, Ollama).
* **The extra body** may not set fields the app writes (`model`, `messages`, `tools`…) or the named fields above.
* **Without the pane:** `/sampling temperature 0.6`, `/sampling top_k clear`, `/sampling extra {"seed":42}` and `/sampling clear`; `NEONSIDEKICK_LLM_SAMPLING` overrides every model for one run.
* The Anthropic API, the OpenAI API and the Claude Code server are not affected.

#### Embedded

A model the app downloads and runs itself on llama.cpp's `llama-server`, for when no other server is around: Gemma 4, Muse Glimmer 30B, Qwen3.6 35B A3B and Qwen3.8 27B.

* **Install:** open **Embedded models** on this tab and pick *Install*. It downloads in the background (📥 on the hint row; double-click it to pause) and switches to the model when done.
* **Use:** `/server` and the startup picker list one **Embedded** row per installed model; `/model` picks between them.
* **First start:** with no URL, no scan and nothing installed, the app opens straight to **Embedded models**.
* **Loading:** the hint row shows `🦙 loading <model>`. Ctrl+C or a double-click on the spinner cancels. The input line works meanwhile.
* **Each row** shows its size, state (`installed`, `download`, `partial · 42%`) and quantisation (lower is smaller and a little less accurate), then ⚡ a drafter, 👁️ images, 🛠️ tool calls, and ⛓️‍💥 for an uncensored build.

| Model | Quantisation | Download | Drafter |
|---|---|---|---|
| Gemma 4 12B (`gemma-4-12b`, Unsloth) | UD-Q4_K_XL | 8 GB | MTP (file) |
| Gemma 4 12B (`gemma-4-12b-q5`, Unsloth) | UD-Q5_K_XL | 9.2 GB | MTP (file) |
| Gemma 4 12B (`gemma-4-12b-q6`, Unsloth) | UD-Q6_K_XL | 11.3 GB | MTP (file) |
| Gemma 4 12B (`gemma-4-12b-bf16`, Unsloth) | BF16 | 24.5 GB | MTP (file) |
| Gemma 4 12B QAT (`gemma-4-12b-qat`, Unsloth) | UD-Q4_K_XL | 7.1 GB | MTP (file) |
| Gemma 4 12B QAT Uncensored (`gemma-4-12b-qat-uncensored`, HauhauCS Balanced) | Q4_K_M | 7.8 GB | MTP (file) |
| Gemma 4 26B A4B (`gemma-4-26b-a4b`, Unsloth) | UD-Q4_K_XL | 18.7 GB | MTP (file) |
| Gemma 4 26B A4B (`gemma-4-26b-a4b-q5`, Unsloth) | UD-Q5_K_XL | 22.9 GB | MTP (file) |
| Gemma 4 26B A4B (`gemma-4-26b-a4b-q6`, Unsloth) | UD-Q6_K_XL | 25 GB | MTP (file) |
| Gemma 4 26B A4B QAT (`gemma-4-26b-a4b-qat`, Unsloth) | UD-Q4_K_XL | 15.7 GB | MTP (file) |
| Gemma 4 26B A4B QAT Uncensored (`gemma-4-26b-a4b-qat-uncensored`, HauhauCS Balanced) | Q4_K_M | 18.2 GB | MTP (file) |
| Gemma 4 26B A4B Uncensored (`gemma-4-26b-a4b-uncensored`, HauhauCS Balanced) | Q4_K_P | 18.1 GB | — |
| Gemma 4 26B A4B Uncensored (`gemma-4-26b-a4b-uncensored-q5`, HauhauCS Balanced) | Q5_K_P | 20.5 GB | — |
| Gemma 4 26B A4B Uncensored (`gemma-4-26b-a4b-uncensored-q6`, HauhauCS Balanced) | Q6_K_P | 24 GB | — |
| Gemma 4 31B (`gemma-4-31b`, Unsloth) | UD-Q4_K_XL | 20.5 GB | MTP (file) |
| Gemma 4 31B (`gemma-4-31b-q5`, Unsloth) | UD-Q5_K_XL | 23.6 GB | MTP (file) |
| Gemma 4 31B QAT (`gemma-4-31b-qat`, Unsloth) | UD-Q4_K_XL | 18.8 GB | MTP (file) |
| Gemma 4 31B QAT Uncensored (`gemma-4-31b-qat-uncensored`, HauhauCS Balanced) | Q4_K_M | 20.2 GB | MTP (file) |
| Gemma 4 E2B (`gemma-4-e2b`, Unsloth) | UD-Q4_K_XL | 4.3 GB | MTP (file) |
| Gemma 4 E2B (`gemma-4-e2b-q5`, Unsloth) | UD-Q5_K_XL | 5.4 GB | MTP (file) |
| Gemma 4 E2B (`gemma-4-e2b-q6`, Unsloth) | UD-Q6_K_XL | 5.8 GB | MTP (file) |
| Gemma 4 E2B (`gemma-4-e2b-q8`, Unsloth) | UD-Q8_K_XL | 6.4 GB | MTP (file) |
| Gemma 4 E2B (`gemma-4-e2b-bf16`, Unsloth) | BF16 | 10.4 GB | MTP (file) |
| Gemma 4 E2B Uncensored (`gemma-4-e2b-uncensored`, HauhauCS Aggressive) | Q4_K_P | 4.4 GB | — |
| Gemma 4 E4B (`gemma-4-e4b`, Unsloth) | UD-Q4_K_XL | 6.2 GB | MTP (file) |
| Gemma 4 E4B (`gemma-4-e4b-q5`, Unsloth) | UD-Q5_K_XL | 7.7 GB | MTP (file) |
| Gemma 4 E4B (`gemma-4-e4b-q6`, Unsloth) | UD-Q6_K_XL | 8.5 GB | MTP (file) |
| Gemma 4 E4B (`gemma-4-e4b-q8`, Unsloth) | UD-Q8_K_XL | 9.8 GB | MTP (file) |
| Gemma 4 E4B (`gemma-4-e4b-bf16`, Unsloth) | BF16 | 16.1 GB | MTP (file) |
| Gemma 4 E4B QAT (`gemma-4-e4b-qat`, Unsloth) | UD-Q4_K_XL | 5.3 GB | MTP (file) |
| Gemma 4 E4B Uncensored (`gemma-4-e4b-uncensored`, HauhauCS Aggressive) | Q4_K_P | 6.4 GB | — |
| Muse Glimmer 30B (`muse-glimmer-30b`, Unsloth) | UD-Q4_K_XL | 19.6 GB | DFlash (file) |
| Muse Glimmer 30B (`muse-glimmer-30b-q5`, Unsloth) | UD-Q5_K_XL | 25.5 GB | DFlash (file) |
| Qwen3.6 35B A3B (`qwen3.6-35b-a3b`, Unsloth) | UD-Q4_K_XL | 23.3 GB | — |
| Qwen3.6 35B A3B (`qwen3.6-35b-a3b-q5`, Unsloth) | UD-Q5_K_XL | 27.5 GB | — |
| Qwen3.6 35B A3B Uncensored (`qwen3.6-35b-a3b-uncensored`, HauhauCS Aggressive) | Q4_K_P | 24.3 GB | — |
| Qwen3.8 27B (`qwen3.8-27b`, Unsloth) | UD-Q4_K_XL | 18.5 GB | MTP (built in) |
| Qwen3.8 27B (`qwen3.8-27b-q5`, Unsloth) | UD-Q5_K_XL | 21.8 GB | MTP (built in) |
| Qwen3.8 27B (`qwen3.8-27b-q6`, Unsloth) | UD-Q6_K_XL | 26.2 GB | MTP (built in) |
| Qwen3.8 27B NVFP4 (`qwen3.8-27b-nvfp4-very-low`, esatapedico) | VERY-LOW | 15.8 GB | MTP (built in) |
| Qwen3.8 27B NVFP4 (`qwen3.8-27b-nvfp4-compact-low`, esatapedico) | COMPACT-LOW | 16.1 GB | MTP (built in) |
| Qwen3.8 27B NVFP4 (`qwen3.8-27b-nvfp4-low`, esatapedico) | LOW | 16.5 GB | MTP (built in) |
| Qwen3.8 27B NVFP4 (`qwen3.8-27b-nvfp4-medium`, esatapedico) | MEDIUM | 17.3 GB | MTP (built in) |
| Qwen3.8 27B NVFP4 (`qwen3.8-27b-nvfp4-mid-high`, esatapedico) | MID-HIGH | 17.8 GB | MTP (built in) |
| Qwen3.8 27B NVFP4 (`qwen3.8-27b-nvfp4-high`, esatapedico) | HIGH | 18.5 GB | MTP (built in) |
| Qwen3.8 27B NVFP4 (`qwen3.8-27b-nvfp4-very-high`, esatapedico) | VERY-HIGH | 20.6 GB | MTP (built in) |
| Qwen3.8 27B NVFP4 (`qwen3.8-27b-nvfp4-highest`, esatapedico) | HIGHEST | 24.1 GB | MTP (built in) |
| Qwen3.8 27B Uncensored (`qwen3.8-27b-uncensored`, HauhauCS Aggressive) | Q4_K_P | 18.9 GB | MTP (built in) |
| Qwen3.8 27B Uncensored (`qwen3.8-27b-uncensored-q5`, HauhauCS Aggressive) | Q5_K_P | 21.1 GB | MTP (built in) |

**Drafter:** **MTP (file)** is a small file downloaded with the model; **MTP (built in)** is part of the model's weights; **DFlash (file)** is a separate 1.6 GB drafter; **—** has none.

| Setting | What it does | Default |
|---|---|---|
| Embedded servers enabled | Offers the embedded models. Off, `/server` lists none and a running embedded server stops; installed models stay on disk. | on |
| Embedded models | The catalog. Enter on an installed model offers *Use now* and *Remove*; on any other, *Install*. Remove asks first and deletes the model's folder. | |
| Embedded HF download type | `parallel` fetches each file over 8 connections (about twice as fast); `single` uses one. | `parallel` |
| Embedded backend | The llama.cpp build: `auto` (CUDA with NVIDIA driver 580+, else Vulkan, else CPU), `cuda`, `vulkan` or `cpu`. On a Mac: `auto` or `metal`, both Metal. | `auto` |
| Embedded context size | The context window in tokens. 0 (**fit**) is the largest that fits on the GPU within *Embedded VRAM budget*. Otherwise 512–262,144. | 0 (fit) |
| Embedded GPU layers | Layers on the GPU: `auto` (as many as free VRAM holds), `all`, or a number (0 runs on the CPU). | `auto` |
| Embedded VRAM budget | How much of the GPU's memory the server may fill: `off` (llama.cpp leaves 1 GiB free) or 50–99 %. It shrinks the context first, then moves layers to the CPU (which slows replies sharply). A change restarts the server. | 91 % |
| Embedded VRAM only | Keeps the whole model in GPU memory. If any of it lands in system RAM, the server stops and says what to lower. Refused on the CPU backend. | on |
| Embedded vision | Loads the vision projector so the model reads images (about 1 GB more for most models). Off, images sent to it are refused. | on |
| Embedded drafter | Speeds up replies by drafting tokens ahead and checking them; the text is the same. Turn it off if a model misbehaves with it. | on |

**Catalog buttons** (on the *Embedded models* title row; they combine):
* **8GB**, **16GB** (`1`, `2`): models up to that size.
* **installed** (`i`) / **uninstalled** (`u`): one at a time.
* **drafter** (`d`): models with a drafter.
* **sort** (`s`): name or size order.
* **uncensored** (`x`): shows only the uncensored builds, which are hidden otherwise.

**Notes**
* **NVFP4 builds** need the CUDA backend on an NVIDIA Blackwell GPU (RTX 50 series or newer). A Mac's catalog leaves them out.
* **Downloads** are checked against Hugging Face's SHA-256. A stopped download resumes when you pick the model again.
* **llama.cpp** itself downloads on first start (577 MB for CUDA, 33 MB for Vulkan, 19 MB for CPU, 12 MB for Metal). If CUDA won't start, the app tries Vulkan.
* **The server** listens on `127.0.0.1` only, with a fresh key, and stops when you pick another server or quit. Only one runs at a time, apart from a `multi-server` botchat's extras (see [Botchat](#botchat)).
* **Files:** weights in `models\llm\<id>\`, shared projectors and drafters in `models\llm\_shared\`, llama.cpp in `llama\`, all under the home folder. `/about` shows them.
* **Sampling** follows each model card; `/sampling` overrides it.
* **Memory:** a model needs roughly its download size in VRAM plus the context. The 26B A4B and Qwen3.6 35B A3B are mixture-of-experts, so they run faster than their size suggests.
* **Never in system RAM:** in NVIDIA Control Panel › *Manage 3D settings* › *Program Settings*, add `llama-server.exe` (under `llama\`) and set *CUDA - Sysmem Fallback Policy* to *Prefer No Sysmem Fallback*.
* Runs on Windows x64 and Apple Silicon Macs. The small models (E2B, E4B) call tools less reliably.

#### Docker

Your own LLM containers (vLLM, SGLang, anything serving `/v1/models`) as `/server` choices, one running at a time. Windows only: on a Mac, Docker Desktop passes no GPU to containers. See [Docker servers](TOOLS.md#docker-servers).

| Setting | What it does | Default |
|---|---|---|
| Docker servers enabled | Offers the chosen containers in `/server`. | off |
| Docker server containers | A checklist of the LLM servers the engine lists: containers matching *Docker server match words* or labelled `neonsidekick.llm=true`. | none |
| Docker server match words | Words that mark a container as an LLM server, matched in its image or command. `r` restores the defaults; empty lists every container. | `aphrodite`, `koboldcpp`, `llama-server`, `llama.cpp`, `lmdeploy`, `localai`, `nvcr.io/nim`, `ollama`, `sglang`, `text-generation`, `trtllm`, `vllm` |
| Docker server stop timeout (s) | How long a stopping container gets before the engine kills it (0–120). | 30 |
| Docker server post-stop delay (s) | The wait between stopping the others and starting this one, so GPU memory frees up (0–60). | 2 |
| Docker server ready timeout (s) | How long a started container may take to answer on `/v1/models` (30–3600). | 900 |
| Docker server stop on exit | Stops the container in use when the app exits. | off |

#### Anthropic

Claude Code (as a `/server` choice and for `/claude`), then the Anthropic API.

| Setting | What it does | Default |
|---|---|---|
| Claude Code executable | The Claude Code CLI, for `/claude` and the Claude Code server. Blank looks on the PATH and in `%USERPROFILE%\.local\bin`. | (looked up) |
| Claude Code server | Offers Claude Code on `/server` while it is found. | off |
| Claude Code command permissions | What `/claude` may do: `read-only` (read, search, web) or `read-write` (also file edits, no commands). It works in the working directory, outside the app's sandbox and approvals. | `read-only` |
| Claude Code command model | `/claude`'s `--model`: Claude Code's default, `fable`, `opus`, `sonnet`, `haiku`, or *Other…*. | (Claude Code's default) |
| Claude Code command effort | `/claude`'s `--effort`: Claude Code's default, `low`, `medium`, `high`, `xhigh` or `max`. | (Claude Code's default) |
| Anthropic API | Offers the Anthropic API on `/server` while a key is set. | off |
| Anthropic API key | Your Anthropic key (`sk-ant-…`), saved encrypted. An empty entry clears it. | (none) |
| Anthropic API max tokens | The output cap per request, thinking included (1,024–128,000). | 32,000 |
| Anthropic API prompt caching | Uses Anthropic's prompt cache, so each request re-reads the last one's content cheaply. | on |

**The Anthropic API** appears in `/server` as an **Anthropic API** row and offers the account's models.
* Every message is billed to the key's account. The key goes only to Anthropic.
* *LLM reasoning* `low`…`xhigh` turns on thinking at that effort; `none` turns it off where the model allows.
* `/usage` adds *Cache* and *Cost* rows (an estimate at list price).

**Claude Code** appears last in `/server` (or `/server claude-code`) and offers `fable`, `opus`, `sonnet` and `haiku`.
* One `claude` process runs for the session; `/clear`, `/new` and a profile switch start a new one.
* It runs with its own tools off and gets the app's tools instead, so approvals and `ask_user` apply as for any model.
* ESC interrupts the reply. Claude Code compacts its own conversation (a 200,000-token window), so *LLM auto compact* does nothing.
* Messages count against your Claude Code plan.

#### OpenAI

The OpenAI API as a `/server` choice, over the Responses API (nothing is stored on OpenAI's side).

| Setting | What it does | Default |
|---|---|---|
| OpenAI API | Offers the OpenAI API on `/server` while a key is set. | off |
| OpenAI API key | Your OpenAI key (`sk-…`), saved encrypted. An empty entry clears it. | (none) |
| OpenAI API max tokens | The output cap per request, reasoning included. 0 uses the model's own limit; else 1,024–128,000. | the model's own |
| OpenAI API organization | The `OpenAI-Organization` header, for an account in several organizations. Empty sends none. | (none) |
| OpenAI API project | The `OpenAI-Project` header. Empty sends none. | (none) |

**The OpenAI API** appears in `/server` as an **OpenAI API** row and offers the account's chat models, newest first.
* Every message is billed to the key's account. The key goes only to api.openai.com.
* *LLM reasoning* is sent as the model's reasoning effort; the thinking shows as OpenAI's summary.
* *LLM sampling* is not sent. *LLM context length* overrides the app's figure for the model's window.
* `/usage` adds *Cache* and *Cost* rows (an estimate at list price).

### Advisors

Other profiles this one consults, each in a role you give it: `neon_advisor` for the model, `/advisor` for you (see [TOOLS.md](TOOLS.md#advisors) and [COMMANDS.md](COMMANDS.md#advisor)). An advisor answers on its own profile's server, model and persona. It may use this chat's read-only tools, and remembers its earlier questions for the session.

| Setting | What it does | Default |
|---|---|---|
| Advisors enabled | Offers the model `neon_advisor` while the list holds an advisor. `/advisor` works either way. | off |
| Advisor list | The advisors: a profile and its role. *+ Add an advisor…* picks a profile and asks its role; Enter on one changes its role or removes it. The band below names each one's server kind and model. | (none) |
| Advisor context | What the model's calls send. `brief`: the question and context. `recent`: also the last 10 messages. `/advisor` always sends the last 10. | `brief` |
| Advisor calls per turn | The most advisor calls one reply may make (1–10). | 2 |
| Advisor tool iterations | Tool round trips an advisor may make before answering (0–10; 0 = text only). | 4 |
| Advisor confirm | Each call waits for your yes. Headless runs refuse the calls. | off |
| Advisor thread length | Earlier questions and answers each advisor remembers (1–50). `/new` starts them over. | 10 |
| Advisor embedded mode | For an advisor on another embedded model than the one running. `parent-server`: it answers on the running model. `multi-server`: an extra `llama-server` for its model. | `parent-server` |

* **Servers:** a blank *LLM URL* in the advisor's profile borrows this chat's server. An embedded advisor needs *Embedded servers enabled*.
* **On the Claude Code server** only one advisor at a time gets tools; any other answers text-only. The *Advisor list* marks which.

### Voice

Speech out and voice in, in two tabs: TTS and STT.

#### TTS

Speech output sets up in the background (🔈 on the hint row); replies are text-only until it's ready.

| Setting | What it does | Default |
|---|---|---|
| TTS output | Reads replies aloud (`/tts`). Code blocks and tables are never read. | off |
| TTS source | `in-process` runs Kokoro inside the app (downloaded on first use); `http` uses a Kokoro-FastAPI server. | `in-process` |
| TTS HTTP URL | The Kokoro-FastAPI base URL (`http://localhost:8880/v1`), needed for `http`. | (not set) |
| TTS voice preview | The voice pickers speak the highlighted voice. | on |
| TTS voice preset | Sets the voice, second voice, mix and speed in one go. Built in: `amanda`, `neon`, `richard`, `hunter`, `larry`, `jack`, `willow`. See [Voice presets](COMMANDS.md#voice-presets). | `neon` |
| TTS voice | The Kokoro voice. | `af_heart` |
| TTS voice 2 | A second voice blended in; `(none)` for the first alone. | `am_eric` |
| TTS voice mix | The first voice's share of the blend, 0–100 %. | 80 |
| TTS speed | Speaking speed, 0.5–2.0. | 1.2 |

#### STT

Voice input sets up in the background (🎙️ on the hint row).

| Setting | What it does | Default |
|---|---|---|
| STT input | Turns the microphone on: the push-to-talk key records a message (`/stt`). On a Mac, macOS asks once whether your terminal may use the microphone. | off |
| STT destination | `chat` sends what you say straight to the model; `draft` puts it on the input line for you to edit. | `chat` |
| STT push-to-talk key | `F1`–`F8`, `Insert`, `Home`, `End`, `PageUp` or `PageDown`. | `F4` |
| STT whisper model | `ggml-tiny.en.bin`, `ggml-base.en.bin` or `ggml-small.en.bin` (downloaded on first use). | `ggml-base.en.bin` |
| STT wake | Saying the wake phrase at the idle line starts listening (`/stt wake`). | off |
| STT wake phrase | One to three words; also the interrupt phrase. | `hey neon` |
| STT vosk model | The wake-word model: `vosk-model-small-en-us-0.15`, `vosk-model-en-us-0.22-lgraph` or `vosk-model-small-en-in-0.4`. | `vosk-model-small-en-us-0.15` |
| STT interrupt | Saying the wake phrase while a reply is spoken cuts it short and listens (`/stt interrupt`). | off |
| STT interrupt echo guard | Ignores the assistant's own voice saying something close to the wake phrase (50–100 %; 100 = the exact phrase only). | 100 |
| STT interrupt confirm | How long the phrase must hold before it counts (0–2000 ms). | 200 |

## Skills settings (`/skills`)

### Offered

The loaded skills with their scope (`profile`, `global` or `external`), version, last change (`yyyy-MM-dd HH:mm`, local time: the later of the app's record and the SKILL.md's own write) and description, then any shadowed duplicates and skipped folders (with the reason). The second strip narrows the list to one scope. Enter on a skill can:

* move it between the profile and global folders, or rename it;
* edit its `SKILL.md` in your editor;
* revert it to a kept version (see [Skill history](#skill-history));
* lock or unlock it (see [Locking a skill](#locking-a-skill)), shown as `🔒 v4`;
* disable or enable it (see [Disabling a skill](#disabling-a-skill)), shown as `⛔ v4` on a dim row;
* delete it, after a confirmation.

### Reflection

| Setting | What it does | Default |
|---|---|---|
| Reflection (auto-learn) | After enough tool calls, or a tool error the model recovered from, a background reflection writes or improves a skill. | on |
| Reflection reasoning | The reflection's reasoning effort: `none`, `low`, `medium`, `high`, `xhigh`, or `profile` for the profile's level. | `none` |
| Reflection window | How many recent turns a reflection reads (1–5). | 3 |
| Reflection min tool calls | The model's own tool calls since the last reflection that make a task worth a skill (3–20). | 4 |
| Reflection max requests | Model requests one reflection may spend (1–20). | 4 |
| Reflection includes sessions | The reflection starts from earlier sessions that match the turn, and can search them. | on |
| Reflection cooldown (minutes) | How long automatic reflections wait after one wrote a skill (0–1440; 0 = off). | 5 |
| Reflection cooldown mode | `last-written-skill`: only turns that used the skill just written wait. `all-skills`: every automatic reflection waits. | `last-written-skill` |
| Reflection yields to turns | A message sent during a reflection pauses it so the reply gets the server. Turn off if your server handles parallel requests. | on |
| Reflection edit supporting files | Lets a reflection change a skill's supporting files too, not just `SKILL.md`. | off |
| Reflection downloaded skills | For skills from `/skills add`: `read-only` makes the reflection write a companion skill instead; `allow-and-mark` lets it change the skill, and a later update warns first. | `allow-and-mark` |

### Options

| Setting | What it does | Default |
|---|---|---|
| Agent skills | Lists the skills in the prompt and offers `load_skill` and `skill_editor`. Off also stops the project file being read. | on |
| Use external skills (.agents\skills) | Also reads `%USERPROFILE%\.agents\skills` (`~/.agents/skills` on a Mac), read-only. | off |
| Project file | Reads `NEON.md` (or `AGENTS.md`) in the working directory into the prompt as project notes. Needs *Agent skills*. | on |
| Skill compact mode | `protected` keeps a loaded skill's instructions through a prune; `unprotected` prunes them like any tool result. | `protected` |
| #-mention enabled | `#` and part of a name on the input line lists the skills; a pick writes `#name`. | on |

### Installing skills

`/skills add` installs skills in the [Agent Skills](https://agentskills.io) format, the same folders other agents use.

- **Sources:** search words (`/skills add pdf`) look it up on [skills.sh](https://skills.sh); `owner/repo` offers every skill in a GitHub repository and `owner/repo/skill` names one; a GitHub `tree/…` or `blob/…/SKILL.md` link narrows to a folder; any `https://…/*.zip` link is downloaded as is.
- **Network:** *Web browser network mode* applies (nothing is fetched under `local_area_network`); the *Web tools* switch doesn't.
- **Preview first:** you see the description, source, file list and any scripts before installing. Unsafe paths and very large skills (over 200 files or 20 MB) are refused.
- **Where it goes:** the profile's or the global `skills` folder, with a `.neon-source.json` recording where it came from. Bundled scripts run only through `run_command` and its approval.
- **Name collisions** are refused, except the same source installed again, which updates it in place.

### Skill records and purging unused skills

`skills.db` in the home folder's `db\skills` records every global and profile skill: its scope, when it was created, last modified and last used. The app keeps it in step with the skill folders at every start and profile load. External skills are not recorded and never purged.

`/skills purge list <age>` lists skills unused for that long; `/skills purge commit <age>` deletes them after a yes/no. The age is days (`30`) or a duration (`12h`, `1d 6h`). Only global skills and the loaded profile's count, and a global skill used in any profile counts as used.

### Skill history

`skills.db` also keeps each skill's history, which the reflection reads:

- **Changes and use:** who changed it (the model, a reflection, an install, a revert or you by hand), and each turn that loaded it with any errors after.
- **Earlier versions:** before a skill file is overwritten, the old text is kept (the last 10 per skill).
- **Reverting:** on `/skills`, Enter on a skill, then `revert`, lists every kept version, newest first. Enter puts one back after a yes; the current text is kept first, so you can go back and forth.
- **Removing versions:** on the version list, `d` removes one and `c` clears them all, each after a yes.

### Locking a skill

On `/skills`, Enter on a skill, then `lock`, keeps it as it is until you choose `unlock`. While it is locked:

- The model and reflection can load it but not change it; a reflection writes a companion skill instead.
- Rename, move, delete and revert refuse with "unlock it first". `edit` still opens it in your editor.
- `/skills add` won't update it, and `/skills purge` leaves it out.
- The lock is a `.neon-lock` file in the skill's folder. It is the app's refusal, not a file-system guard: a shell command can still reach the folder.

### Disabling a skill

On `/skills`, Enter on a skill, then `disable`, switches it off without removing it; `enable` switches it back on. While it is disabled:

- The model is never offered it: it isn't in the prompt's skill list, `load_skill` can't load it, and `#` mentions and `/botchat` bots don't list it.
- Reflection sees it marked as off, so it writes no second skill for the topic.
- Rename, move, delete and `edit` still work; revert refuses with "enable it first".
- It still holds its name, so a global skill of the same name stays shadowed. `/skills purge` leaves it out.
- The switch is a `.neon-disabled` file in the skill's folder; a disabled global skill is off in every profile.

## Tools settings (`/tools`)

### Offered

Every tool, grouped, with the description the model reads. Enter or Space switches one; a group whose switch is off is dim. The Help group (`neon_help`) has no switch of its own, so turn it off here. In a new profile, `gitlib_delete`, `zip`, `unzip`, `unc_delete`, `docker_remove`, `docker_prune` and `ha_todo` start off.

### Core

The tools most profiles use and the pane's own options, in four tabs: Ask, Web, Shell and Options.

#### Ask

| Setting | What it does | Default |
|---|---|---|
| Ask user | Offers `ask_user`: multiple-choice questions on the pane. `/tools ask` and the toolbar's 🙋 open its on/off page. | on |
| Ask max questions | Questions per call (1–10). | 10 |
| Ask max choices per question | Options per question (2–15). | 10 |

#### Web

| Setting | What it does | Default |
|---|---|---|
| Web tools | Offers `web_search`, `web_fetch`, `open_url` and `download_file`. | off |
| Web browser mode | `default` uses HTTP and falls back to a headless browser for a blocked or empty page; `httpclient` never falls back; `chromium` uses the browser for every page. | `default` |
| Web browser path | The Chromium-based browser for headless fetches and making PDFs; empty finds Edge, Chrome or Brave. | (auto) |
| Web browser network mode | Where a fetch may reach: `internet` (public addresses), `local_area_network` (this machine and the LAN) or `both`. | `internet` |
| Web search method | `duckduckgo` (built in) or `searxng`. | `duckduckgo` |
| Web SearXNG URL | The SearXNG instance, for `searxng`. | (not set) |
| Web search max results | Hits per search (1–20). | 20 |
| Web download max (MB) | The largest file `download_file` saves (1–102400). | 50 |

#### Shell

| Setting | What it does | Default |
|---|---|---|
| Shell command policy | How the model may run shell commands: `off` (no shell tools), `ask` (anything not on the allowed list asks you first) or `yolo` (everything runs). See [Shell guards](#shell-guards). | `off` |
| Shell allowed commands | Command prefixes allowed for good (`git status`, `dotnet build`). The approval pane's *Allow … always* adds one; Enter removes one. The list's off, ask and yolo buttons (`o`, `a`, `y`) switch *Shell command policy*, yolo after a yes. `/profile push commands` copies the list to another profile. | none |
| Shell police | Refuses a command, script or process input that names a path outside the working directory. Turning it off also stops the forbidden strings and the database rules. See [Shell guards](#shell-guards). | on |
| Shell police forbidden strings | Strings the police refuses outright, case and spacing ignored. A new profile starts with a list of harmful actions; `r` restores it. `/profile push policestrings` copies the list. | the starter list (49 on Windows, 48 on a Mac) |
| Shell prefer native tools | Sends back a lone shell command one of the app's own tools covers (once a turn). | on |
| Shell default | The shell when a call names none: `powershell` (pwsh if installed, else 5.1), `cmd` or `bash` (Git Bash). On macOS: `zsh`, `bash` or `powershell`. | `powershell` (`zsh` on macOS) |
| Shell timeout (s) | How long a foreground command without its own `timeout` may run (1–3600). | 180 |
| Shell foreground cap (s) | The longest any foreground command may run (10–3600). | 600 |
| Shell output max chars | Output one result carries (2000–500000). Past that, the start and end are kept and the whole text goes to `.shell\<id>.log` in the working directory. | 30000 |
| Shell code languages | What `execute_code` may run: `powershell`, `python`, `node` (each when its interpreter is found). | all three |
| Shell code timeout (s) | How long a script without its own `timeout` may run (1–3600). | 300 |
| Shell tool bridge | Lets an `execute_code` script call the app's other tools through its `neon_tools` module. | off |
| Shell tool bridge max calls | Tool calls one script may make through the bridge (1–500). | 50 |

##### Shell guards

* **Approval (`ask`):** the pane offers Deny, Allow once, Allow for this session, or Allow always. `v` shows the whole command or script. `--yolo` and `NEONSIDEKICK_COMMAND_POLICY` override the policy for one launch.
* **Prefixes:** a command is allowed by its program, plus the verb for programs that take one (`git status`, `npm run`; options before the verb are skipped, so `git -C sub push` is `git push`). Every command in the line must be allowed: each part of a pipe or `&&` chain, and each command a substitution runs (`$(…)`, bash's backticks and `<(…)`, PowerShell's `(…)`, `@(…)` and `{…}`). A substitution that can't be read (left open, or one whose output is the command) always asks.
* **Path police** reads the text of a command, script or process input and refuses a path outside the working directory: an absolute path (`C:\…`, a UNC share, `/etc/hosts`), a `..` that climbs out, `~`, or a folder variable (`%USERPROFILE%`, `$HOME`). It also catches paths inside switches and URLs, bare drives, and links that lead outside. The model gets an error and the transcript shows 👮. It reads text, not what runs, so a computed path isn't seen. `--no-police` and `NEONSIDEKICK_SHELL_POLICE` override it; `--yolo` doesn't.
* **Forbidden strings:** while the police is on, any command, script or process input containing a listed string is refused, even under `--yolo`. Case is ignored and runs of spaces count as one (`rm -rf` catches `RM   -RF`). It is a tripwire, not a sandbox: a command built in pieces gets past it.
  * **The starter list** holds actions that do harm anywhere, such as `reset --hard`, `push --force`, `| iex`, `Format-Volume`, `diskpart`, `mimikatz`, `.ssh\id_`, and on a Mac `diskutil erase`, `LaunchAgents` or `dump-keychain`. The list's **↺ restore defaults** button (`r`) puts it back.
  * **Searching for one is refused too:** a `grep` for a listed string, or a script that mentions one, is refused. Remove the entry or turn the police off to get past it.
  * **Stricter entries you can add:** `rm -rf`, `Remove-Item -Recurse -Force`, `git clean`, `taskkill /f`, upload sites (`pastebin.com`, `ngrok`), and on a Mac `osascript` or `.zshrc`.
* **SQLite:** while the police and *SQLite tools* are both on, a command or script that reaches SQLite is refused, even under `--yolo`, so databases are reached only through the sqlite_ tools. That means the word `sqlite`, a file *sqlite.json* names, or a `.db`, `.sqlite` or similar file name. A script file a command runs is checked too.
* **Server databases:** the same rule for SQL Server, Oracle, MySQL and PostgreSQL while each one's tools are on: their clients and drivers (`sqlcmd`, `sqlplus`, `mysql`, `psql`, `psycopg`…) and the hosts their connections name.
* **Package commands pass both:** a command that only installs, removes or lists packages (`dotnet add package …`, `pip install …`, `npm install …`) isn't judged by the database rules; the rest of a chained line still is.
* **Prefer native tools:** a lone command an offered tool replaces comes back once a turn with the tool to use instead (`cat`/`type`/`grep` → `read_file`/`search_files`, `git status` → the GitLib tools, `curl` → `web_fetch`, `sqlcmd` → `sql_query`, `net use` → `unc_shares`…). Sent again, it goes to the approval pane. Pipes, compound lines and commands with no tool are never sent back.

#### Options

| Setting | What it does | Default |
|---|---|---|
| $-mention enabled | `$` and part of a name lists the tools the next turn offers; a pick writes `$name`. | on |
| Tool collapse count | A run of more tool calls than this folds to one summary line; 0 never folds (0–100). | 2 |
| Code collapse count | A code block longer than this folds to its label once complete (0–100; 0 never folds). Needs *Transcript markdown*. | 20 |
| Show file diffs | A file edit shows its diff under its line: added lines on green, removed on red. The model's result is the same either way. | on |
| Diff max lines | The most rows of an edit's diff shown (0–500; 0 = the header line alone). | 10 |
| Diff collapse count | A diff longer than this folds once its tool run is over (0–500; 0 never folds). | 10 |

To see a folded block or diff, click it, press Ctrl+O, click **⤡** or use `/expand`.

### Files

The file tools and the places beyond the working directory they reach, in five tabs: File, Image, UNC, GitLib and Obsidian.

#### File

| Setting | What it does | Default |
|---|---|---|
| File tools | Offers the sandboxed file tools (read, write, patch, search, move, copy, zip, convert_to_pdf…), which reach only the working directory. A link inside it that leads outside is refused. | off |
| File /tree max length | Entries `/tree` prints before it stops (1–10000). | 500 |
| File /tree show sizes | `/tree` shows file sizes. | on |
| File @-mention folder mode | Picking a folder from the `@` list: `folder-remain` opens it in the list; `folder-apply` writes `@folder/` and closes. | `folder-remain` |
| File browser/tree mode | `default` hides hidden, system and dot entries in the folder browsers, `/tree` and `/browse`; `show-hidden` lists them. | `default` |
| File view image max (per call) | Pictures one `view_image` call may load (1–100). | 10 |
| File search max results | The most rows one `search_files` or `unc_search` call returns (1–5000). | 200 |

#### Image

| Setting | What it does | Default |
|---|---|---|
| Image tools | Offers `view_image` (the model looks at pictures), `image_info` and `image_edit`. They reach pictures in the working directory and the profile's `images` folder, whether *File tools* is on or not. | off |
| Image edit quality | The JPEG (or JPEG XL, HEIF) quality `image_edit` uses when the model gives none (1–100). On a Mac: JPEG and HEIF only. | 90 |
| Image edit metadata | What metadata `image_edit` keeps: `none`, `basic` (author, date taken, camera…) or `all` (basic plus the GPS position). | `none` |
| Image edit mode | Where an edited picture goes: `beside-original` writes a new file beside the source, `overwrite-original` replaces the source, with no confirmation. Used by `image_edit` and the picture windows' right-click menu. | `beside-original` |
| Image edit output folder | Where `image_edit` writes when the model names no `to`: a folder under the profile's `images` folder. Empty writes beside the source. | (beside the source) |
| Pasted images folder | Where a picture pasted from the clipboard is saved, under the profile's `images` folder, so the model's picture tools can use it. | `pasted_images` |

#### UNC

| Setting | What it does | Default |
|---|---|---|
| UNC tools | Offers `unc_shares`, `unc_search`, `unc_info`, `unc_read`, and `unc_fetch` while the File tools are on. See [UNC shares](TOOLS.md#unc-shares-and-outside-folders); on a Mac the app mounts `smb://` shares itself ([UNC shares on a Mac](TOOLS.md#unc-shares-on-a-mac)). | off |
| UNC writes | The master key for changes. On, a share with `access: readwrite` also gets the write, move, copy and delete tools. Changes are permanent. | off |
| UNC shares offered | As *SQL connections offered*. | none |
| UNC set password | As *SQL set password*, for runas shares. | — |
| UNC add/edit share | The wizard (new or edit); its test lists the share's root under its account. | — |
| UNC *-mention enabled | `*` and part of a name lists the offered shares; a pick writes `*name`. | on |
| UNC shares (profile) | As *SQL connections (profile)*. | (none) |
| UNC shares (global) | As *SQL connections (global)*. | (none) |

#### GitLib

| Setting | What it does | Default |
|---|---|---|
| GitLib tools | Offers the built-in git tools (status, log, diff, blame, branch, stage, commit, stash…) over the working directory's repository. Off, git goes through the shell. | off |
| GitLib diff max lines | Where a `gitlib_diff` patch is cut (20–5000). | 500 |
| GitLib log max commits | Commits `gitlib_log` returns by default (1–200). | 20 |
| GitLib email | The `user.email` `/gituser` writes into the repository's config. | (not set) |
| GitLib name | The `user.name` `/gituser` writes beside it. | (not set) |

#### Obsidian

| Setting | What it does | Default |
|---|---|---|
| Obsidian tools | Offers the vault tools (search, list, read, links, daily, write, properties, move) once a vault is set. | off |
| Obsidian vault | The vault's folder (holding `.obsidian`), separate from the working directory. | (not set) |
| Obsidian allow delete (.trash) | Offers `vault_delete`, which moves a note or attachment into the vault's `.trash`. | on |

### Apps

The apps and services the tools reach, in three tabs: Docker, ComfyUI and YouTube. `/claude`'s settings are on `/settings` › Servers › Anthropic.

#### Docker

| Setting | What it does | Default |
|---|---|---|
| Docker tools | Offers the read-only Docker tools (containers, logs, inspect, stats, resources, compose). `/docker` works either way. See [Docker](TOOLS.md#docker); on a Mac, [Docker on a Mac](TOOLS.md#docker-on-a-mac). | off |
| Docker writes | The master key for the model's changes: `docker_lifecycle`, `docker_pull`, `docker_remove` and `docker_prune`. Every call asks first; headless refuses them. | off |
| Docker engine pipe | The engine's named pipe (`docker_engine` is Docker Desktop's). Blank is the default. | `\\.\pipe\docker_engine` |
| Docker engine socket | On a Mac, the engine's Unix socket. Empty finds it (`DOCKER_HOST`, the Docker CLI's context, then the usual places of Docker Desktop, OrbStack, Colima and Rancher Desktop). | `auto: ~/.docker/run/docker.sock` |

#### ComfyUI

| Setting | What it does | Default |
|---|---|---|
| ComfyUI tools | Offers `generate_image` and `set_splash_image` once *ComfyUI URL* is set and a workflow is installed. | off |
| ComfyUI URL | The ComfyUI server, often on your LAN (`http://gpu-box:8188`). | (not set) |
| ComfyUI workflows offered | A checklist of the workflows the model is offered; none until ticked. `/imagine <name>` can still use a hidden one. | none |
| ComfyUI add workflow | A wizard that **builds** a workflow from your server's checkpoints or **imports** a ComfyUI export. See [Adding a workflow](TOOLS.md#adding-a-workflow). | — |
| ComfyUI ^-mention enabled | `^` and part of a name lists the offered workflows; a pick writes `^name`. | on |
| ComfyUI timeout (s) | How long to wait for one generation, queue included (10–3600). | 300 |
| ComfyUI max pictures per call | Most pictures one `generate_image` call or `/imagine --count` makes (1–16). | 5 |
| ComfyUI reinforce negatives | When the model writes the prompt, adds a few tags to the negative where the image model tends to drift. | on |
| ComfyUI show prompts | Shows the `prompt:`, `negative:` and `params:` lines under each picture. | on |
| ComfyUI picture strip | Keeps the session's pictures as thumbnails above the input line. With the line empty, ←/→ highlight one and Enter opens it; **×** hides the strip until **🎞️** on the rule brings it back. | on |
| ComfyUI output folder | Where pictures are saved, under the profile's `images` folder. | `comfy_images` |

#### YouTube

| Setting | What it does | Default |
|---|---|---|
| YouTube tools | Offers `youtube_search` and `youtube_playlist` (with an API key), and `youtube_play`, `youtube_control` and `youtube_status`, which play a video in the app's own window (Windows with WebView2, or macOS 14+). Playing by id or link needs no key. | off |
| YouTube API key | A YouTube Data API v3 key, for searching: create one in the [Google Cloud Console](https://console.cloud.google.com/) with *YouTube Data API v3* enabled. About 100 free searches a day. Saved encrypted. | (none) |
| YouTube search max results | How many results a search lists (1–20). | 8 |
| YouTube search order | `relevance`, `date` (newest first), `views`, `rating` or `title`, unless a search names one. | `relevance` |
| YouTube safe search | How much restricted content a search leaves out: `none`, `moderate` or `strict`. | `moderate` |
| YouTube region | A two-letter country code (`US`, `GB`) a search asks for. Empty for any region. | (any region) |
| YouTube language | A language code (`en`, `es`) whose results rank higher. Empty for any language. | (any language) |
| YouTube autoplay | Whether a played video starts at once or waits for a play. | on |
| YouTube while speaking | What a playing video does while the app speaks or listens: `pause`, `duck` (to 15 %) or `none`. | `pause` |

### Databases

The five database families, in five tabs: SQL, MySQL, SQLite, PostgreSQL and Oracle. Every family is read-only unless you choose `read-write` and the connection allows it; see [TOOLS.md](TOOLS.md) for each family's safety layers.

#### SQL

| Setting | What it does | Default |
|---|---|---|
| SQL tools | Offers the SQL Server tools (connections, tables, columns, describe, relationships, indexes, query) over `sql.json`'s connections, each held to its own database. | off |
| SQL mode | `read-only`: the tools only read. `read-write`: `sql_execute` is offered too, on connections marked `"access": "readwrite"`, and each change asks you. See [Read-write mode](TOOLS.md#read-write-mode). | read-only |
| SQL statements allowed | Under `read-write`, the kinds of statement `sql_execute` may run (a checklist). | changing data, creating, reading |
| SQL connections offered | A checklist of the connections in both `sql.json` files; none is offered until ticked. A hidden connection is invisible to every tool. | none |
| SQL set password | Pick a connection and type its password, masked; it is stored encrypted. | — |
| SQL add/edit connection | A wizard for a new or saved connection, which can **test** it before saving. The database is required. See [Managing connections](TOOLS.md#managing-connections). | — |
| SQL %-mention enabled | `%` and part of a name lists the connections; a pick writes `%name`. | on |
| SQL max rows | Rows `sql_query` returns unless the call says otherwise (1–100000). | 100 |
| SQL query timeout (s) | How long one batch may run on the server (1–600). | 30 |
| SQL query result max chars | The most characters of table one `sql_query`, `oracle_query` or `mysql_query` returns (1000–1000000). | 32,000 |
| SQL connections (profile) | Enter opens the profile's `sql.json` in your editor (created with commented examples). | (none) |
| SQL connections (global) | The same for the home folder's `sql.json`, which every profile reads. The profile's wins a name clash. | (none) |

The other database menus and the UNC tab work like this one, over `mysql.json`, `sqlite.json`, `postgres.json`, `oracle.json` and `unc.json`.

#### MySQL

| Setting | What it does | Default |
|---|---|---|
| MySQL tools | Offers the MySQL tools, for MySQL 8.0.16+ and MariaDB 10.2+, each connection held to its own database. | off |
| MySQL mode | As *SQL mode*, with `mysql_execute`. See [Read-write mode](TOOLS.md#read-write-mode). | read-only |
| MySQL statements allowed | As *SQL statements allowed*, for `mysql_execute`. | changing data, creating, reading |
| MySQL connections offered | As *SQL connections offered*. | none |
| MySQL set password | As *SQL set password*. | — |
| MySQL add/edit connection | The wizard; its test warns when the account could change data. See [MySQL](TOOLS.md#mysql-and-mariadb). | — |
| MySQL %-mention enabled | Lists the MySQL connections in the `%` list too. | on |
| MySQL max rows | As *SQL max rows*, for `mysql_query`. | 100 |
| MySQL query timeout (s) | How long one statement may run (1–600). | 30 |
| MySQL connections (profile) | As *SQL connections (profile)*. | (none) |
| MySQL connections (global) | As *SQL connections (global)*. | (none) |

#### SQLite

| Setting | What it does | Default |
|---|---|---|
| SQLite tools | Offers the SQLite tools (databases, tables, describe, query) over the databases named in `sqlite.json`. While on, the shell and file tools keep out of SQLite files (see [SQLite](TOOLS.md#sqlite)). | off |
| SQLite mode | `read-only`: the tools only read. `read-write`: `sqlite_execute` is offered too, one change per call, each allowed on a pane. See [Read-write mode](TOOLS.md#read-write-mode). | read-only |
| SQLite statements allowed | Under `read-write`, the kinds of statement `sqlite_execute` may run (a checklist). With none ticked, it isn't offered. | changing data, creating, reading |
| SQLite databases offered | Which databases of `sqlite.json` the model sees; none until ticked. | none |
| SQLite sandbox files | The model may also open any SQLite file in the working directory by its path, and create one there. | on |
| SQLite add/edit database | The wizard (new or edit); its test opens the file read-only and counts the tables. | — |
| SQLite %-mention enabled | Lists the SQLite databases in the `%` list too. | on |
| SQLite max rows | As *SQL max rows*, for `sqlite_query`. | 100 |
| SQLite query timeout (s) | How long one statement may run before it is stopped (1–600). | 30 |
| SQLite databases (profile) | Opens this profile's `sqlite.json` in your editor. | (none) |
| SQLite databases (global) | Opens the global `sqlite.json` in your editor. | (none) |

#### PostgreSQL

| Setting | What it does | Default |
|---|---|---|
| PostgreSQL tools | Offers the PostgreSQL tools, each connection held to its own database (and to one schema when its entry names one). | off |
| PostgreSQL mode | As *SQL mode*, with `postgres_execute`. See [Read-write mode](TOOLS.md#read-write-mode). | read-only |
| PostgreSQL statements allowed | As *SQL statements allowed*, for `postgres_execute`. | changing data, creating, reading |
| PostgreSQL connections offered | As *SQL connections offered*. | none |
| PostgreSQL set password | As *SQL set password*. | — |
| PostgreSQL add/edit connection | The wizard; the database is required, a schema optional. Its test warns when the role could change data. See [PostgreSQL](TOOLS.md#postgresql). | — |
| PostgreSQL %-mention enabled | Lists the PostgreSQL connections in the `%` list too. | on |
| PostgreSQL max rows | As *SQL max rows*, for `postgres_query`. | 100 |
| PostgreSQL query timeout (s) | How long one statement may run (1–600). | 30 |
| PostgreSQL connections (profile) | As *SQL connections (profile)*. | (none) |
| PostgreSQL connections (global) | As *SQL connections (global)*. | (none) |

#### Oracle

| Setting | What it does | Default |
|---|---|---|
| Oracle tools | Offers the Oracle tools, each connection held to its one schema. | off |
| Oracle mode | As *SQL mode*, with `oracle_execute`. See [Read-write mode](TOOLS.md#read-write-mode). | read-only |
| Oracle statements allowed | As *SQL statements allowed*, for `oracle_execute`. | changing data, creating, reading |
| Oracle connections offered | As *SQL connections offered*. | none |
| Oracle set password | As *SQL set password*. | — |
| Oracle add/edit connection | The wizard; its *Schema* is the one the connection is held to (the user's own when empty). Its test warns when the account could change data. See [Oracle](TOOLS.md#oracle). | — |
| Oracle %-mention enabled | Lists the Oracle connections in the `%` list too. | on |
| Oracle max rows | As *SQL max rows*, for `oracle_query`. | 100 |
| Oracle query timeout (s) | How long one statement may run on the server (1–600). | 30 |
| Oracle connections (profile) | As *SQL connections (profile)*. | (none) |
| Oracle connections (global) | As *SQL connections (global)*. | (none) |

### Devices

The printer, camera and screen tools, in three tabs: Print, Camera and Screen.

#### Print

| Setting | What it does | Default |
|---|---|---|
| Print tools | Offers `list_printers` and `print_file`. `/print` works either way. On a Mac they print through CUPS. | off |
| Print action policy | `off`: list printers only. `ask`: each print waits for your yes (refused headless). `allow`: prints without asking. `/print` never asks. | `ask` |
| Print default printer | Where a print goes when none is named. | (Windows default) |
| Print font size (pt) | Body text size for printed listings and Markdown (6–24). | 10 |
| PDF engine | What makes PDFs for `convert_to_pdf` and `/pdf`: `auto` (a Chromium browser, else Microsoft Print to PDF), `browser` or `printer`. On a Mac only the browser makes PDFs. See [Making PDFs](TOOLS.md#making-pdfs). | `auto` |

#### Camera

| Setting | What it does | Default |
|---|---|---|
| Camera tool | Offers `camera_capture`, so the model can ask you for a photo. Never offered headless or to a model without vision. `/camera` works either way. On a Mac: macOS 14 or later. | off |
| Camera shutter | `user`: the camera pane shows the request; Space takes the photo, `r` retakes, Enter sends, ESC declines. `model`: you allow or deny, and the app takes it. | `user` |
| Camera preview | `live`: a camera window shows the picture while you frame it. `post`: the picture viewer opens on the photo. `disabled`: no window. | `live` |
| Camera device | The camera, by its system name. `(first camera)`, or one that isn't connected, uses the first. | (first camera) |
| Camera resolution | The size asked for (`640x480`, `1280x720` or `1920x1080`); the camera uses its nearest. | `1280x720` |
| Camera output folder | Where photos are saved, under the profile's `images` folder. | `camera_images` |
| Camera keep in sessions | Off, a stored session keeps a line naming the photo instead of the picture. | off |
| Camera watch interval (s) | How often `/camera watch` looks (2–3600). | 10 |
| Camera watch change (%) | How much of the picture must change to count (1–100). `--camera-check` prints your camera's noise. | 8% |
| Camera watch speaks up | Off, a changed picture rides your next message. On, the model is shown it unasked when nothing else is going on. | off |
| Camera watch min gap (s) | The least time between unprompted watch turns (30–3600). | 120 |

#### Screen

| Setting | What it does | Default |
|---|---|---|
| Screen capture tool | Offers `screen_capture` and `screen_list`, so the model can see a monitor or a window. Never offered headless or to a model without vision. On a Mac the terminal needs the Screen Recording permission (see [Screen capture](COMMANDS.md#screen-capture)). | off |
| Screen capture ask | `ask`: a pane names what would be captured and why, and waits for your answer. `allow`: taken without asking. | `ask` |
| Screen capture preview | The picture viewer opens on each screenshot, so you see what was sent. | on |
| Screen capture output folder | Where screenshots are saved, under the profile's `images` folder. | `screen_images` |
| Screen capture keep in sessions | Off, a stored session keeps a line naming the screenshot instead of the picture. | off |

### Home

The household's tools, in two tabs: HomeAssistant and Shopping.

#### HomeAssistant

| Setting | What it does | Default |
|---|---|---|
| Home Assistant tools | Offers the Home Assistant tools (`ha_overview`, `ha_states`, `ha_history`, `ha_lights`, `ha_scene`, `ha_media`, `ha_todo`, `ha_call_service`, `ha_assist`) once a URL and token are set. | off |
| Home Assistant URL | Your server (`http://localhost:8123`, or on your LAN). | (not set) |
| Home Assistant API key | A long-lived access token (your HA profile → Security), saved encrypted. Empty clears it. | (none) |
| Home Assistant test connection | Asks the server for its version with the saved URL and token. | — |
| Home Assistant action policy | `off`: read only. `ask`: lights, scenes, media and to-do lists run; anything else (a switch, a script, an automation) asks first (refused headless). `allow`: everything runs. `/ha` never asks. | `ask` |
| Home Assistant Assist agent | The conversation agent `ha_assist` and `/ha say` use; empty for the default. | (Home Assistant's default) |
| Home Assistant timeout (s) | How long one request may take (2–60). | 10 |

The services that run unasked under `ask` can be changed in `profile.json` (`homeAssistantSafeServices`).

#### Shopping

| Setting | What it does | Default |
|---|---|---|
| Shopping tools | Offers the shopping list's tools (see [Tools](TOOLS.md#shopping-list)). The list and its stores live in the profile's `db\shopping\shopping.db`. | off |
| Shopping list | Enter opens the shopping list, the same pane as `/shopping` and the toolbar's 🛒. | — |
| Shopping stores | Enter opens the stores' manager: add, rename or remove a store, choose the one the list is walked through, and edit its layout (`l`). | — |

## MCP servers (`/mcp`)

### Servers

One row per server in `mcp.json` (the profile's, then the home folder's; the profile's wins a name clash), with its state: `connected · N tools`, `connecting`, `failed: …` or `off`. Enter or Space turns one on or off (Enter on a failed one retries). Below them: `edit profile mcp.json`, `edit global mcp.json`, `reload`, and any skipped entries.

### Tools

Every connected server's tools, as `<server>__<tool>`, with their descriptions. Enter or Space switches one. With two or more servers connected, the second strip shows one server's tools alone.

### Options

| Setting | What it does | Default |
|---|---|---|
| MCP servers | The master switch: on, every enabled server starts at launch and its tools are offered. | off |
| MCP connect timeout (s) | How long a server gets to connect and list its tools (5–300). | 30 |

## System prompt (`/sys`)

A read-only view of exactly what the next reply sends. `/sys persona`, `/sys operata` and `/sys vocalia` open its three files in your editor (`reset` takes one back to the default); `/profile push|pull persona|operata|vocalia <name> [overwrite]` copies one to or from another profile.

### Prompt

The system prompt section by section, each with its status:

* **Persona** (built in, or `persona.md`)
* **Operating rules** (built in, or `operata.md`)
* **Project notes** (`NEON.md` / `AGENTS.md`)
* **Memory**
* **Skills** (the catalog)
* **Voice directive** (`vocalia.md`, when it has text; spoken turns only)

### Tools

Every tool the reply may call, grouped as on `/tools` (plus one group per MCP server, and Plan in plan mode), with the description the model reads. Switched-off tools are left out.
