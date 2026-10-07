# Settings & menus

The full reference for the panes, keys and settings. The [README](../README.md#settings--menus) has the basics; inside the app, the highlighted row of any pane explains itself, `/help` lists every command and key, and the model's `neon_help` tool answers questions about any setting.

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
* **Keyboard:** ←/→ switch tabs, ↑/↓ move, Enter edits or toggles, Space flips an on/off row at once, ESC closes.
* **What a row does:** under the list, the highlighted setting's description and its default. On the Offered tabs of `/tools` and `/skills`, and `/mcp`'s Tools tab, it is the highlighted tool's or skill's whole description, with why it isn't offered (a tool's) or its warning (a skill's) on the last line. A long tab is split into sections (General, LLM, Botchat), and a tab whose rows all start with its name drops the name (the Botchat tab's *Botchat LLM mode* reads *LLM mode*; notices and this file keep the full name).
* **Under a list:** the other list panes show the highlighted row whole under the list too, with what the row has no room for: `/sessions` (when it started and last ran, its turns and model), `/queue` and `/rewind` (the whole message), `/process` (the whole command, its state and run time), `/docker` (image, project, ports, networks, status), `/youtube saved` and a YouTube search's results, the `/server` and `/model` pickers (the URL, the probe's detail, the whole model id), `/mcp`'s Servers tab (a failed server's whole error, a connected one's tools, the file an edit row opens), and the allowed commands and police strings.
* **Typing to filter:** on `/sessions`, `/server`, `/docker`, `/youtube saved` and the allowed commands, as on `/model` and the Offered tabs, typing narrows the list (the caption counts what is left); Backspace erases, the first ESC clears it. A pane's own key letters (`/docker`'s `r`, `/youtube saved`'s `d`) still work until you start typing.
* **Keys:** the hint row names each key in lowercase (`w = watch`), and ESC reads *close* on a pane's top level and *back* on a page opened from it. Removing anything from a list asks first, with No on the cursor, and an empty list opens its pane on one dim line. Every pane's keys are listed under [Pane keys](COMMANDS.md#pane-keys).
* **Finding in an info pane:** on the read-only panes (`/help`, `/sys`, `/about`, `/tree`, `/docker logs`, an approval's `v` view…), typing finds: every match is marked, the view goes to the first from where it was, and the hint row reads `find: tab · 3 of 12`. Enter or F3 goes to the next (wrapping), Shift+Enter or Shift+F3 to the one before, Backspace erases, and the first ESC clears the find (the next closes the pane). A tab switch keeps the find and looks in the new tab. It finds what is on the screen, so a word the window wraps across two rows is not found.
* **Tabs:** the tab you are on is underlined. A small number after a tab's name (`General³`) counts its settings that differ from the default; those settings' values show in the same colour.
* **Scrolling:** a list cut by the window ends in a row like `▲▼ 13–25 of 58`, an arrow for each way there is more.
* **`/tools` Offered:** a group that is off says why on its heading (the one reason that holds), and a tool switched on under it reads `(on)`.
* **Mouse:** a click moves the cursor; a double-click picks a row or tab (on the input line, selects a word). The × at the top right works like ESC, and a double-click outside an open pane closes it. A double-click on a picture in the transcript opens it in the [picture viewer](COMMANDS.md#picture-viewer).

## The input line
* It is always a full editor, even while a reply streams or `/botchat` runs: ←/→, Home/End (the line's ends, pressed again the message's), Delete; Ctrl+←/→ move a word, Ctrl+Backspace / Ctrl+Delete delete one; Shift+arrows (Ctrl+Shift+←/→ by words), Ctrl+A or a drag to select; a double-click selects a word (letters, digits and `_`; all of a password field); Ctrl+C / Ctrl+X copy / cut (the hint row says how much was copied); right-click or Alt+V pastes, Nerd Font glyphs included; ↑/↓ walk the history.
* `/`, `@`, `#`, `$`, `%`, `^` and `*` open their lists. The mention lists work inside a command's text too (`/loop infinite 1s append the time to @notes.txt`), except for `/speak`, `/view`, `/print` and `/pdf`, which complete their own path.
* Drag a picture from the ComfyUI picture strip or the transcript onto the input row to attach it, as if dropped from the desktop. The hint row reads **🖼️ drop on line** while you drag; letting go elsewhere attaches nothing.
* Enter during a reply queues the message. A draft left on the row survives the reply.
* ESC during a reply stops the speech, then closes an open list, then cancels the reply; it never clears your draft there (ESC at the idle line does, and keeps it in this session's history: ↑ brings it back, but *Keep command history* never stores a draft you cleared unless you send it). While a reply runs the hint row ends with **esc to stop**, and a running tool shows its own time beside the reply's.
* A tool call reads as its values (`🛠️ read_file notes.md`), its result under it (`→ 4 lines`); a result of several lines shows them while the run goes and folds to its first line and `(+N lines)` after (Ctrl+O or `/expand` shows them again).
* A tool result that failed is marked ✗ in the warning colour, and a folded tool run counts them (`· 1 failed`).
* A reply keeps one left edge: the ● sits on its first text (never on a tool line) and every stretch after a tool keeps its indent; a notice or tool line that wraps continues under its text. Headings: level 1 in the accent, level 2 in the tertiary colour, level 3 and below bold in the body colour.
* ESC twice on an empty line opens `/rewind` (the hint row prompts for the second press).

## Keyboard shortcuts
Each shortcut runs its command as if typed on its own; a draft on the row stays.

| Shortcut | Runs | During a reply |
|---|---|---|
| Ctrl+. | `/terminal` | at once |
| Ctrl+/ | `/settings` | opens over the reply |
| Ctrl+E | `/explore` | at once |
| Ctrl+F | `/perfbar` (performance bar on/off) | at once |
| Ctrl+Shift+F | `/find` (find in the transcript; Windows Terminal keeps Ctrl+Shift+F for its own find until you unbind it) | waits for the reply to end |
| Ctrl+H | `/help` | opens over the reply |
| Ctrl+L | cancels the background learning (🧠) | at once |
| Ctrl+M | `/model` | waits for the reply |
| Ctrl+P | `/profile` | waits for the reply |
| Ctrl+Q | `/queue` | opens over the reply |
| Ctrl+R | `/reasoning` | opens over the reply |
| Ctrl+S | `/server` | waits for the reply |
| Ctrl+T | `/toolbar` (toolbar on/off) | at once |
| Ctrl+U | `/usage` | opens over the reply |
| Ctrl+Y | `/sys` | opens over the reply |
| Ctrl+Z | `/theme` | waits for the reply |
| Ctrl+Alt+C | `/clear` | stops the reply first |
| Ctrl+Alt+N | `/new` | stops the reply first |
| Ctrl+Alt+P | `/splash` | stops the reply first |
| Ctrl+Alt+Q | `/queue clear` | at once |
| Ctrl+Alt+D | `/mcp` | opens over the reply |
| Ctrl+Alt+E | `/sessions` | opens over the reply |
| Ctrl+Alt+H | `/header` | at once |
| Ctrl+Alt+L | `/cmdlist` | opens over the reply |
| Ctrl+Alt+M | `/memory` | opens over the reply |
| Ctrl+Alt+O | `/police` | opens over the reply |
| Ctrl+Alt+R | `/rename`: the rename box | opens over the reply |
| Ctrl+Alt+S | `/skills` | opens over the reply |
| Ctrl+Alt+T | `/tools` | opens over the reply |
| Ctrl+Alt+G | `/log`: opens or closes the log window | at once |
| Ctrl+Alt+U | `/comfy view`: opens or closes the picture viewer | at once |
| Ctrl+Alt+V | `/camera live`: opens or closes the camera's window | at once |
| Ctrl+Alt+X ×2 | unloads the embedded model (see below) | at once |
| F9 | `/camera snap`: a photo, attached to the line | waits for the reply |
| F10 | `/screen`: a capture of the app's monitor, attached to the line | waits for the reply |

* **Inside a pane** (a menu, `/help`, the folder picker, a value being typed), a shortcut closes every level of the pane and runs, so Ctrl+Alt+S in `/tools` opens `/skills`. A pane's own shortcut just closes it. Ctrl+E, Ctrl+., Ctrl+F, Ctrl+L, Ctrl+T, the three window chords and Ctrl+Alt+X act and leave the pane open.
* **Panes that ask you something** (a command's approval, `ask_user`, the plan's approval, a confirmation, *Did you mean /clear?*) ignore the shortcuts, so none can answer them by accident.
* **Window chords** close their window when pressed again (Ctrl+Alt+V only the window `/camera live` opened); the typed command only opens it or brings it forward.
* **Ctrl+Alt+X**, pressed twice within two seconds, unloads the embedded model at once and frees its memory, cancelling any reply, load or botchat using it. No server is connected until you pick one with `/server`; the saved *LLM URL* is kept, so the next start loads the model again. The first press only shows a reminder. With any other server it does nothing.
* **Ctrl+L** cancels a running skill-learning reflection (the 🧠 on the hint row), as a double-click on the 🧠 does; *(🧠 learning cancelled)* prints once the reply, if any, ends. With none running it does nothing.
* An AltGr key that types a character on your layout still types it.
* **F9 and F10** are chords only with no modifier held, so they can't be the push-to-talk key. Inside the picture viewer and the thumbnail browser they keep their own meanings (the slide show and the shuffle).
* **A chord that does nothing** may be held by another program as a global hotkey (a graphics card's overlay, say), so it never reaches the app. `/keycheck` lists every chord here and whether another program holds it; free it in that program.

## Commands typed during a reply

| Behaviour | Commands |
|---|---|
| Open their pane over the reply | `/help`, `/settings`, `/tools`, `/mcp`, `/sys`, `/usage`, `/about`, `/memory`, `/queue`, `/sessions`, `/sessions title`, `/rename`, `/skills`, `/reasoning`, `/sampling`, `/cmdlist`, `/police`, `/cmdclear`, `/tree`, `/vault`, `/camera list`, `/docker logs`, `/ha states`, `/cmdcopy`, `/keycopy`, `/srvcopy`, `/keycheck`, `/process`, `/persona`, `/operata`, `/vocalia` |
| Run at once | `/ha`, `/camera live`, `/camera watch`, `/camera off`, `/camera use`, `/tts`, `/stt`, `/wake`, `/interrupt`, `/perfbar`, `/toolbar`, `/reasoning <level>`, `/sampling <field> <value>`, `/queue clear`, `/copy`, `/remember`, `/explore`, `/terminal`, `/log`, `/process <id>`, `/timer`, `/expand`, `/collapse`, `/window`, `/cwd`, `/comfy view`, `/comfy thumbs`, `/view <path>`, `/view <path> --thumbs` |
| Stop the reply first | `/clear`, `/new`, `/splash`, `/rewind`, `/exit` |
| Everything else | Waits for the reply to end, queued behind earlier messages (*Queue cancel mode* applies) |

## Hint row, rule and toolbar

**Long setups run in the background:** an embedded model's download, the MCP servers connecting, and voice input's and speech output's first-use download and load. Past half a second, the input line stays yours: the glyph (📥, 🔌, 🎙️ or 🔈) shows progress on the hint row, and a status line prints when it's done.

| Double-click | Does |
|---|---|
| the model name | `/server` (server, then model, then reasoning) |
| the reasoning glyph | `/reasoning` |
| the tokens or spinner | `/usage` |
| 🖼️/🎨 and its timer (ComfyUI rendering) | cancels the pictures; the reply goes on |
| 📥 / 🔌 / 🎙️ / 🔈 (setting up) | cancels that download or setup |
| the queued count | `/queue` |
| 📷 (camera on) | `/camera off` |
| blank space on the hint row | `/settings` |
| the session name on the rule above the input row (a model-written name reads as words, `Summary of notes file`) | renames it, like `/sessions title` (during `/botchat` the rule shows the cast instead) |

**⤡** at the left end of that rule appears when something can fold; a click does what Ctrl+O does (unfold all if anything is folded, else fold all).

**The toolbar** (*Show toolbar*) sits under the hint row. A glyph opens or closes its pane (or switches to it from another); a window glyph opens or closes its window. A tool switch, and 💾 while *Memory mode* is `disabled`, sits on a dark slab. By default it shows ⚙️, 🛠️, 🎓, 💾, the lock, 👮, 🐚, 📁, 🌐 and the working directory. One click on a glyph says what it is on the hint row (`🐚 Shell: ask · double-click to open`); a double-click opens it. A strip too wide for the window ends in `+N` for the glyphs it leaves off, and the working directory shows your profile folder as `~` (`~\Repo\app`), cut from the front when it does not fit.

| Toolbar item | Shown | Opens |
|---|---|---|
| ⚙️ 🪪 🧮 🛠️ 🔌 🎓 🎭 💬 📊 | always | `/settings`, `/profile` (the profile picker), `/theme` (the theme picker), `/tools`, `/mcp`, `/skills`, `/sys`, `/sessions`, `/usage` |
| 💾 | always; on the slab while *Memory mode* is `disabled` | `/memory`: the memories, with **read-write** (`w`), **read-only** (`o`) and **disabled** (`x`) on its title row |
| 🔒 / 🔓 | *Shell command policy* is `ask` / `yolo` (none under `off`) | `/cmdlist` |
| 👮 / 🥷 | *Shell police* is on / off, and the policy isn't `off` | `/police` |
| 🐚 | always; on the slab under `off` | `/tools shell`: the *Shell command policy* picker (yolo asks first) |
| 📁 🌐 ✴️ 🐳 💎 🛢️ 🔮 🐬 🪶 🐘 🔗 🏠 🎨 📸 🖨️ | always; on the slab while off | `/tools files`, `web`, `claude`, `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `sqlite`, `postgres`, `unc`, `ha`, `comfy`, `camera`, `print`: that group's on/off page. 🛢️ 🔮 🐬 🪶 🐘 🔗 🎨's has an **offered** button (`o`) showing how many are offered (`☑  offered (2 of 5)`) that opens the group's *… offered* checklist. 📸's has **watch** (W, `/camera watch` on or off), **live** (`l`, the camera's window), **snap** (S, `/camera snap`) and **screen** (C, `/screen`); snap and screen close the pane first |
| 📄 | always | `/log`, the log window (Ctrl+Alt+G) |
| ⚡ | always | `/process`, the background processes' list |
| 📺 | always | `/camera live`, the camera's window (Ctrl+Alt+V) |
| 🎞️ | always | `/comfy view`, the picture viewer (Ctrl+Alt+U) |
| 🪟 | always | `/comfy thumbs`, the thumbnail browser on the ComfyUI output folder |
| 📈 | always | `/perfbar`: hides or shows the performance bar |
| working directory (right edge) | always | `/cwd browse` |
| blank space | — | `/settings` |
| performance bar (anywhere on it) | *Show performance bar* has a meter checked | `/settings` |

## Panes
* `/settings`: the app, LLM, the embedded model, Docker, Anthropic and OpenAI servers, voice, sessions and `/botchat`
* `/skills`: agent skills and self-reflection
* `/tools`: the model's tools, and Claude (`/claude` and the advisor)
* `/mcp`: external MCP servers
* `/sys`: a read-only view of what the model is sent
* `/usage`: LLM usage statistics (tok/s, time to first token…)

Settings that an environment variable or flag can override for one launch are listed under [Environment variables](ENVIRONMENT.md).

## App settings (`/settings`)

### General

| Setting | What it does | Default |
|---|---|---|
| Profile | Switches profile (each has its own settings, persona, memory, skills and sessions); a typed letter jumps to the next profile starting with it. | `default` |
| New profile mode | What `/profile add` copies: `basic` the settings and memories; `advanced` also the persona, operating-rules and voice-directive files. | `basic` |
| Working directory (cwd) | The folder the file and GitLib tools work in; empty is the profile's `files\` folder. The row opens the `/cwd browse` folder picker; `/cwd <path>` sets one by hand. | profile's `files\` |
| Memory mode | `read-write` offers `save_memory` / `recall_memory` and opens every conversation with what is remembered; `read-only` offers `recall_memory` alone, so the model reads but never saves (your `/remember` still does); `disabled` turns memory off and refuses `/remember`. | read-write |
| Queue messages | Lists messages sent during a reply (a count, and `/queue`). Off, they are still sent when the reply ends, just not listed. | on |
| Queue cancel mode | What a cancelled reply does with the queue: `hold` keeps it until your next message (or `➤ send` on `/queue`), `drain` sends the next one at once, `empty` drops them all. | `empty` |
| Keep command history | Saves the ↑/↓ history (newest 1,000 lines, no collapsed pastes or pictures) in `sessions.db`. Off deletes it at the next profile load; `/cmdclear` empties it either way. | on |
| Command typo intercept | A command name without its slash (`clear`) or with extra ones (`//profile work`) asks *Did you mean /clear?* first. A bare `//` is still `/settings`. | on |
| Hide /exit autocomplete | Leaves `/exit` out of the `/` list; typing it in full still works. | on |
| Transcript markdown | Renders replies as Markdown, with code fences highlighted (C#, JS/TS, Python, Bash, PowerShell, JSON, YAML, TOML/INI, SQL, C/C++, Java, Kotlin, Go, Rust, CSS, XML/HTML, diff). | on |
| Paste preview lines | How many lines of a long paste show dimmed under its `[Pasted text #n]` placeholder (0–200). More than five fold to `▸ 📋 N lines of the paste` once the next thing is said (Ctrl+O or `/expand` shows them). A one-line paste's placeholder gives its size, `[Pasted text #1 · 840 chars]`. | 25 |
| Show image thumbnails | Draws a small thumbnail of each picture you send, each one a tool fetches or makes, and each `/botchat` picture. `/view` and `/imagine` always draw theirs. | on |
| Image thumbnail size | `tiny` (32×8), `small` (48×12), `medium` (64×16), `large` (80×20), `xlarge` (96×24) columns × rows, or `fullsize` (as large as the transcript allows). | `small` |
| Copy user prompt | `/copy` includes your prompt above the reply. | on |
| User line style | How your sent line looks in the transcript: `quiet` (the › in the user colour, your words in the body colour), `slab` (your line on a faint fill) or `bold` (the whole line bold in the user colour). | bold |
| Theme | One of the sixty built-in themes (see [Themes](COMMANDS.md#custom-themes)) or your own, sorted by name. A wide enough window previews the highlighted theme (on the terminal's own background when *Themed background* is off); a typed letter jumps to the next theme starting with it. | `collider` |
| Themed background | Gives the terminal the theme's background while the app runs. Off, the terminal profile's own background (colour, acrylic or picture) stays. | on |
| Themed external windows | The picture viewer, the camera's window and the log window wear the theme (dark title bar and theme colours). Off, they stay black. | on |
| Welcome splash | Pictures under the banner at startup until your first line: `fullsize`, `tiled` or `disabled`. See Welcome splash below. | `fullsize` |
| Show header | Shows the banner at startup and after `/clear`, `/splash`, `/theme` and a profile switch. `/header` and Ctrl+Alt+H flip it, shown at the next clear. | on |
| Working directory in header | Prints the working directory at the right of the banner's title line. | off |
| Show toolbar | A checklist of the toolbar's items: every glyph and the working-directory path (📂). `a` / `n` / `d` pick all, none or the default ten; none hides the row. | Settings, Tools, Skills, Memory, Shell allowed commands, Shell police, Shell, Files, Web, path (10 of 35) |
| Show performance bar | A checklist of the bar's meters, updated each second: **CPU**, **RAM**, **GPU**, **VRAM**, **NET** (share of link speed), **NET↓** and **NET↑** (rates), **PROC** (background processes running). `a` / `n` / `d` pick all, none or the default four; none hides the bar. The title row picks the look: **text** (`t`), **gauge** (`g`), **spark** (`s`, the last ten seconds) or **led** (`l`). See Performance bar below. | CPU, RAM, GPU, VRAM, `led` |
| Menus max height | How much of the window a menu or info pane may take: `half-screen`, `three-quarters` or `full-screen` (all but one row). Longer lists scroll; every tab keeps the tallest tab's height. | `full-screen` |
| Draft editor | The program `/draft` opens with (`code --wait`, `notepad`…). Empty uses Windows' `.txt` editor. | (default .txt editor) |
| Image viewer | Where a double-clicked picture opens: empty for the built-in viewer, `system` for Windows' app for the file type, or a command the path is appended to (`mspaint`, `"C:\Program Files\GIMP 3\bin\gimp-3.exe"`). | (built-in viewer) |

#### Welcome splash

* `fullsize` fills the screen with one picture; ←/→ step through the set. Delete twice on an empty line moves one of the profile's own pictures to the folder's `.trash` (move it back to restore it).
* `tiled` shows thumbnails at *Image thumbnail size*; ←/→ page and a double-click opens one.
* Pictures in the profile's `splash\` folder replace the built-in ones.
* `/splash` shows the splash whatever the setting (tiled under `tiled`, otherwise one picture). A theme change restarts the screen like `/clear`.

#### Performance bar

* Values turn amber from 60 % and red from 85 %. A meter the machine can't read (no GPU, no network) is left out.
* An NVIDIA GPU is read through its driver (NVML); any other through Windows' GPU counters, for the card with the most memory.
* The network meters follow the busiest adapter that is up and has a gateway, so a VPN over Wi-Fi isn't counted twice. NET↓ and NET↑ show bits per second (`850K`, `12.4M`, `1.2G`); their gauges show the share of the link.
* PROC counts the model's background processes still running (`/process` lists them). It is a number in every look, with no gauge, sparkline or LEDs, dim at none.
* `/perfbar` or the toolbar's 📈 hides the bar, or brings it back with the meters it last had.

### LLM

| Setting | What it does | Default |
|---|---|---|
| LLM server scan mode | Where to look for a server while *LLM URL* is blank: `local` (the usual ports here), `remote` (the same ports across the LAN), `both`, or `disabled`. | `disabled` |
| LLM URL | The server's OpenAI-compatible base URL (`http://127.0.0.1:1234/v1`), `embedded`, or `docker:<container>`. `/server` fills it in. Empty: the app scans and, at startup, lets you pick a server, model and reasoning level and saves all three (ESC takes the first server without saving). | (none) |
| LLM model | The model id. Empty takes the first the server lists; a Docker server serving one model saves its id here when it connects; `/model` picks one. | (first listed) |
| LLM API key | The bearer token the server expects (`empty` for none). Saved encrypted for your Windows account (DPAPI) and shown as `(set, encrypted)`. | `empty` |
| LLM reasoning | How hard the model thinks: `none`, `low`, `medium`, `high` or `xhigh`. `/reasoning` opens the same list. | `none` |
| LLM show thinking | Streams a reasoning model's thinking as a dim block (its last five lines), folded to `▸ 💭 thought for 4.2s — <its first sentence>` when the answer starts. Click it, Ctrl+O or `/expand` to see it again. Needs *Transcript markdown*. Thinking is never spoken or logged; only `/copy --thinking` copies it. | on |
| LLM preserve thinking | Sends earlier turns' thinking back to a local server (`reasoning_content`) and asks the chat template to keep it (`preserve_thinking` for Qwen3.6, `clear_thinking: false` for GLM). The current turn's thinking is always sent back between tool calls. Costs context; a prune drops old thinking first. | off |
| LLM reasoning estimate | How `/usage` counts reasoning when the server streams thinking but doesn't count it (llama.cpp, the embedded LLM, Ollama): `chars` (characters ÷ 4), `tokenize` (llama.cpp's exact `/tokenize`, else `chars`) or `off` (`—`). Estimates show as `~1,234`; a server's own count always wins. | `chars` |
| LLM sampling | Per-model sampling overrides; Enter opens the `/sampling` pane. See Sampling per model. | (server defaults) |
| LLM sampling from Hugging Face | For servers that don't report their defaults (vLLM, SGLang, LM Studio) and a Hugging Face model id (`Qwen/Qwen3-8B`), shows the model card's `generation_config.json` values in `/sampling` as `(Hugging Face)`. One request per model and connect, never with your key; display only. | off |
| LLM offer tools | Whether the model gets any tools. Turn it off for chat templates with no tool role. A change starts a new conversation. | on |
| LLM max tool iterations | Tool round trips one message may make (1–10000). | 10000 |
| LLM request timeout (s) | The longest one HTTP request may take (up to 3600). | 3600 |
| LLM turn timeout (s) | The longest one whole turn may take, tool calls included (up to 21600). | 21600 |
| LLM context length | The context window in tokens, for the usage percentage. 0 takes the server's figure. | 0 (server) |
| LLM mid-turn usage | The hint row's usage during a reply: `estimate` (live, marked `~`, one token per chunk) or `last-known` (the last completed request's figures). | `last-known` |
| LLM max turns | User turns the model sees before the oldest drop off (1–500). `auto` keeps them all while auto compact can run, else 24. | `auto` |
| LLM auto compact (%) | How full the context may get before the next message compacts it (1–100; 0 = off). *LLM tool compact type* acts at the same share during a reply. | 85 |
| LLM compact type | What `/compact` does: `summary` folds older turns into a model-written summary; `prune` swaps their bulky tool results for stubs. An empty summary is retried once on a plainer transcript; if that fails too, the compact says why. | `summary` |
| LLM compact keep recent | Recent user turns a compact keeps word for word (0–24). | 2 |
| LLM compact show summary | After a compact, shows the summary (or one line per pruned result) and how many messages were kept. | off |
| LLM tool compact type | What happens when one turn's tool calls reach the auto-compact share. `compact` prunes, then if needed summarises earlier turns and then this turn's earlier calls. `prune` stubs this turn's older results. `stop` ends the turn. `nothing` does nothing. | `compact` |
| LLM picture keep | The most pictures one request carries (0–500; 0 = no cap). Past it the oldest leave the conversation until half the cap is left, each a line naming its file (`view_image` shows it again); the newest message's pictures always stay. Every picture is sent again with every request, so a long picture session can outgrow what a server takes. | 20 |
| LLM picture megabytes | The most megabytes of pictures one request carries, as sent (0–1000; 0 = no cap); past it the oldest leave until half is left. A request with pictures that the server drops as it is sent is tried once more with half of them. | 24 |
| LLM use fun verbs | The thinking spinner shows a random verb instead of `thinking` / `writing`. | off |

### Sampling per model

Until you override them, sampling is left to the server and the model's defaults.

* `/sampling` (or the *LLM sampling* row) opens one tab per model: the connected model, then **any model (`*`)**, then every other model you have set something for.
* Each field comes from the model's tab, else from `*`, else isn't sent. Switching models picks up the other model's values.
* Changes apply at the next turn, so nothing reconnects; the pane also opens during a reply.
* A blank value clears a field. Out-of-range values are refused, never clamped.

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

* **Server defaults** show dim on the connected model's tab (`0.8 (server)`): llama.cpp reports them on `/props`, Ollama on `/api/show`. vLLM, SGLang and LM Studio report none; for vLLM and SGLang, *LLM sampling from Hugging Face* reads the model card instead.
* **Unknown fields** are ignored by a server; Ollama's `/v1` takes only the four OpenAI ones.
* **The extra body** may not set fields the app writes (`model`, `messages`, `tools`, `stream`, `reasoning_effort`…) or the named fields above. Its `chat_template_kwargs` merge with the app's, whose `enable_thinking` and `preserve_thinking` win.
* **Without the pane:** `/sampling temperature 0.6`, `/sampling top_k clear`, `/sampling extra {"seed":42}` and `/sampling clear` change the connected model's values; `NEONSIDEKICK_LLM_SAMPLING` overrides every model for one run.
* The Anthropic API and the OpenAI API are not affected.

### Embedded

A model the app downloads and runs itself on llama.cpp's `llama-server`, for when no other server is around: Gemma 4 from E2B to 31B, Meta's Muse Glimmer 30B, Qwen3.6 35B A3B and Qwen3.8 27B (also in NVFP4 for NVIDIA Blackwell GPUs).

* **Install:** open **Embedded models** on this tab and pick *Install*. It downloads in the background (📥 and a percentage on the hint row; double-click 📥 to pause) and switches to the model when done, unless you picked another server meanwhile.
* **Use:** `/server` and the startup picker list one **Embedded** row per installed model; `/server embedded` lists only those, and `/model` on the embedded server picks between them.
* **First start:** with no URL, scan mode `disabled` and nothing installed, the app opens straight to **Embedded models** (ESC twice returns to the chat).
* **Loading:** the hint row shows `🦙 starting <model>`, then `🦙 loading <model>`. Ctrl+C or a double-click on the spinner cancels; ESC does nothing. Meanwhile the input line works as during a reply: panes open over the load, `/clear`, `/new`, `/splash`, `/rewind` and `/exit` cancel it first, and commands like `/server`, `/model`, `/profile`, `/theme` and `/sampling` wait. A `/reasoning` level picked meanwhile applies once it has loaded; a cancelled load treats the queue as a cancelled reply does.
* **Reading a row:** each row shows its size and state (`installed · 4.2 GB`, `download · 4.2 GB`, or a paused download's progress) and its quantisation (lower numbers are smaller and a little less accurate). Then ⚡ a drafter, 👁️ images and 🛠️ tool calls, and ⛓️‍💥 for an uncensored build (⛓️‍💥⚔️ aggressive). Every model reads images and calls tools.

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

**Drafter:** **MTP (file)** is a small multi-token-prediction file downloaded with the model; **MTP (built in)** is part of the model's own weights; **DFlash (file)** is a separate 1.6 GB drafter that drafts 16 tokens at a time; **—** has none.

| Setting | What it does | Default |
|---|---|---|
| Embedded servers enabled | Offers the embedded models. Off, `/server` lists none, `/server embedded` refuses, a saved embedded URL counts as blank and a running embedded server stops. Installed models stay on disk. | on |
| Embedded models | The catalog. Enter on an installed model offers *Use now* and *Remove*; on any other, *Install* (and *Remove* for a partial download). Remove asks first, stops its download and any server running it, and deletes its folder (and its projector and drafter unless another model on disk shares them), clearing the LLM URL and model if they named it. Using or installing closes the settings and connects. The buttons are listed below. | |
| Embedded HF download type | `parallel` fetches each file over 8 connections (about twice as fast); `single` uses one. A download under way keeps its mode. | `parallel` |
| Embedded backend | The llama.cpp build: `auto` (CUDA with NVIDIA driver 580+, else Vulkan, else CPU), `cuda`, `vulkan` or `cpu`. The row shows what `auto` picked and why. | `auto` |
| Embedded context size | The context window in tokens. 0 (**fit**) is the largest that fits beside the model with every layer on the GPU, within *Embedded VRAM budget*: from the model's own window (128K for E2B/E4B, 256K for the rest) down to 4,096. Otherwise 512–262,144, never shrunk. | 0 (fit) |
| Embedded GPU layers | Layers on the GPU: `auto` (as many as free VRAM holds), `all`, or a number (0 runs on the CPU). | `auto` |
| Embedded VRAM budget | How much of the GPU's memory the server may fill: `off` (llama.cpp leaves 1 GiB free per GPU) or 50–99 % of the card with the most memory. It shrinks the context first (with context 0), then moves layers to the CPU (with GPU layers `auto`); a fixed context too big for it can only push layers out, which slows replies sharply. Measured at server start; CUDA and Vulkan only; a change restarts the server. | 91 % |
| Embedded VRAM only | Keeps the whole model in GPU memory: every layer on the GPU (with context 0 the context shrinks instead). If any of it lands in system RAM (layers on the CPU, a failed allocation, or the NVIDIA driver's shared-memory fallback), the server stops and the connect says what to lower. Refused on the CPU backend, on Vulkan with an integrated GPU, and when llama-server doesn't report where the layers went. | on |
| Embedded vision | Loads the vision projector so the model reads images (about 1 GB more for most models, under 200 MB for the 12Bs, 2 GB for Muse Glimmer). Off, images sent to it are refused. | on |
| Embedded drafter | Speeds up replies with multi-token prediction: the model drafts tokens ahead and checks them, so the text is the same, just faster (see the Drafter column). Off, no drafter is loaded or downloaded. Turn it off if a model misbehaves with it. | on |

**Catalog buttons** (on the title row of *Embedded models*; each group combines with the others and starts cleared at each visit):
* **8GB**, **16GB** (`1`, `2`): models up to that size (weights, projector and drafter). One at a time; press the lit one to clear it.
* **installed** (`i`) / **uninstalled** (`u`): one at a time; a paused download counts as uninstalled.
* **drafter** (`d`): models with a drafter.
* **sort** (`s`): name order or size order (smallest first).
* **uncensored** (`x`): shows only the uncensored builds, which are hidden otherwise. It starts lit when the model in use is one.
* `/server` and the startup picker have the size, drafter, sort and uncensored buttons for their embedded rows.

**Notes**
* **NVFP4 builds** need the CUDA backend on an NVIDIA Blackwell GPU (RTX 50 series or newer); elsewhere they are slow or don't load.
* **Downloads** are checked against Hugging Face's SHA-256 for each file. A paused or interrupted download resumes when you pick the model again; the drive needs room for the rest plus 1 GB.
* **llama.cpp** itself downloads on first start (build `b11258`: 577 MB for CUDA, 33 MB for Vulkan, 19 MB for CPU). If `auto` picked CUDA and it won't start, the app tries Vulkan.
* **The server** listens on `127.0.0.1` only, on a random port with a fresh key. It restarts only when a setting it depends on changes, and stops when you pick another server, turn *Embedded servers enabled* off, or quit (Windows stops it if the app crashes). Only one runs at a time, apart from a `multi-server` botchat's extras (see Botchat).
* **Files:** each model's weights in `models\llm\<id>\`, the vision projectors and drafters in `models\llm\_shared\` (one copy for every build that uses it, so a second build of a model downloads its weights alone; copies older versions kept per model are merged in), llama.cpp in `llama\`, all under the home folder; `/about` shows them.
* **Sampling** follows each model card (Gemma 4: temperature 1.0, top-p 0.95, top-k 64; HauhauCS's QAT Balanced builds: 0.6, 0.9, 64; Qwen: 1.0, 0.95, 20; Muse Glimmer: 1.0, 0.95, 64); `/sampling` overrides it.
* **Memory:** the 12Bs need about 8 GB of VRAM plus the context at Q4 (10 and 12 GB at Q5 and Q6, 25 GB at BF16). The larger models need roughly their download size plus the context, or a partial CPU offload. E2B and E4B fit in less. The 26B A4B and Qwen3.6 35B A3B are mixture-of-experts (4B and 3B active per token), so they run faster than their size suggests.
* **Never in system RAM:** besides *Embedded VRAM only*, you can tell the NVIDIA driver never to fall back for llama.cpp: NVIDIA Control Panel › *Manage 3D settings* › *Program Settings*, add `llama-server.exe` (under `llama\` in the home folder) and set *CUDA - Sysmem Fallback Policy* to *Prefer No Sysmem Fallback*.
* Windows x64 only. The small models (E2B, E4B) call tools less reliably.

### Docker

Your own LLM containers (vLLM, SGLang, anything serving `/v1/models`) as `/server` choices, one running at a time. See [Docker servers](TOOLS.md#docker-servers).

| Setting | What it does | Default |
|---|---|---|
| Docker servers enabled | Offers the chosen containers in `/server`. Turning it off while one is in use stops it at the reconnect. | off |
| Docker server containers | A checklist of every container the engine lists, with state, image and ports. A ticked name the engine no longer lists is dropped when the checklist opens, and the status line names it. | none |
| Docker server stop timeout (s) | How long a stopping container gets before the engine kills it (0–120). | 30 |
| Docker server post-stop delay (s) | The wait between stopping the others and starting this one, so the GPU's memory frees up (0–60). | 2 |
| Docker server ready timeout (s) | How long a started container may take to answer on `/v1/models` (30–3600). Past it, the switch fails and the container keeps running. | 900 |
| Docker server stop on exit | Stops the container in use when the app exits (the window's close button too). | off |

### Anthropic

The Anthropic API and the Claude CLI as `/server` choices. `/claude` and `claude_advisor_cli` are on `/tools` › ClaudeCLI.

| Setting | What it does | Default |
|---|---|---|
| Anthropic API | Offers the Anthropic API on `/server` while a key is set. Off (or keyless) while it is the saved URL, the app scans as if the URL were blank. | off |
| Anthropic API key | Your Anthropic key (`sk-ant-…`), typed masked and saved encrypted (DPAPI). An empty entry clears it. | (none) |
| Anthropic API max tokens | The output cap per request, thinking included (1,024–128,000). | 32,000 |
| Anthropic API prompt caching | Marks the tools, system prompt and conversation for Anthropic's prompt cache, so each request re-reads the last one's content cheaply. | on |
| Claude CLI server | Offers Claude Code on `/server` while it is found (*Claude CLI executable* on `/tools` › ClaudeCLI, or the PATH). Off (or missing) while it is the saved URL, the app scans as if the URL were blank, and a running Claude CLI stops. | off |

**The Anthropic API** appears in `/server` as an **Anthropic API** row. Picking it sets *LLM URL* to `https://api.anthropic.com/v1`, then offers the account's models and the reasoning level.

* Every message is billed to the key's account. Each key goes only to its own server.
* Changing one of these settings reconnects when `/settings` closes.
* *LLM reasoning*: `low`…`xhigh` turn on adaptive thinking at that effort (`xhigh` is `high` on the 4.6 models; Haiku 4.5 and older take a budget). `none` turns thinking off where allowed; Opus 5.5 and Fable always think.
* The context window is the model's `max_input_tokens`. `/usage` adds *Cache* and *Cost* rows (an estimate at list price).

**The Claude CLI** appears last in `/server` (or `/server claude-cli`). Picking it sets *LLM URL* to `claude-cli` and offers `fable`, `opus`, `sonnet` and `haiku`, then the reasoning level (`--effort`).

* **One open session:** the first message starts `claude` and keeps it running; later messages send only the new text. A change of model, reasoning, system prompt or tools, or a crash, restarts it on the same session. `/clear`, `/new` and a profile switch start a new one; restoring an app session resumes its Claude session.
* **The app's tools, not Claude Code's:** Claude Code runs with its own tools, skills, hooks, settings files and MCP servers off, the app's system prompt, and this turn's tools through an MCP server the app hosts (the app's own exe runs as the relay, `--mcp-relay`). Calls run as any model's do: approvals, `ask_user` and *LLM max tool iterations* apply.
* **ESC** interrupts the reply; if Claude Code doesn't stop within 3 seconds it is closed, and the next message resumes on a new process.
* Side requests (titles, reflections, summaries, bots) each run as a one-off `claude -p` with no tools.
* Claude Code compacts its own conversation, so *LLM auto compact* does nothing here; the window is 200,000 tokens.
* Messages count against your Claude Code plan; the log records each turn's cost. Switching servers stops the CLI; the session stays for a switch back.

### OpenAI

The OpenAI API as a `/server` choice, over the Responses API (stateless: nothing is stored on OpenAI's side).

| Setting | What it does | Default |
|---|---|---|
| OpenAI API | Offers the OpenAI API on `/server` while a key is set. Off (or keyless) while it is the saved URL, the app scans as if the URL were blank. | off |
| OpenAI API key | Your OpenAI key (`sk-…`), typed masked and saved encrypted (DPAPI). An empty entry clears it. | (none) |
| OpenAI API max tokens | The output cap per request, reasoning included (`max_output_tokens`). 0 sends none: the model's own limit. Else 1,024–128,000. | the model's own |
| OpenAI API organization | The `OpenAI-Organization` header, for an account in several organizations. Empty sends none. | (none) |
| OpenAI API project | The `OpenAI-Project` header. Empty sends none (a project key names its project already). | (none) |

**The OpenAI API** appears in `/server` as an **OpenAI API** row, after the Anthropic API's. Picking it sets *LLM URL* to `https://api.openai.com/v1`, then offers the account's chat models, newest first (not the audio, realtime, image, embedding, moderation, `-pro` or `codex` ones), and the reasoning level.

* Every message is billed to the key's account. The key goes only to api.openai.com: never to a local server or the Anthropic API, and the *LLM API key* never goes to OpenAI. A saved `https://api.openai.com` URL now needs this switch and key.
* Changing one of these settings reconnects when `/settings` closes.
* *LLM reasoning* is sent as `reasoning.effort`, shaped per model: `none` is `minimal` on GPT-5 and `low` where a model has neither (o-series, GPT-6 Astra and Sol); `xhigh` is `high` before GPT-5.2; GPT-4.1 and GPT-4o get none. The thinking shows as OpenAI's summary of it, and its encrypted form goes back within a tool loop, so the model keeps its reasoning across calls.
* Every model takes tools at every level here; over Chat Completions, GPT-5.4 and newer refuse tools beside any reasoning, which is why this is the Responses API.
* *LLM sampling* is not sent (the reasoning models refuse a temperature), nor is another server's thinking.
* The context window comes from the app's model table (the model list names none); *LLM context length* overrides it. `/usage` adds *Cache* and *Cost* rows: an estimate at OpenAI's list prices of 2026-10-03, long-context prices past 272K prompt tokens, cache writes from GPT-5.6 on.

### TTS

Speech output sets up in the background (🔈 on the hint row); replies are text-only until it's ready.

| Setting | What it does | Default |
|---|---|---|
| TTS output | Reads replies aloud (`/tts`). Code blocks and tables are never read, not even by `/speak`. | off |
| TTS source | `in-process` runs Kokoro inside the app (downloaded on first use); `http` uses a Kokoro-FastAPI server. | `in-process` |
| TTS HTTP URL | The Kokoro-FastAPI base URL (`http://localhost:8880/v1`), needed for `http`. | (not set) |
| TTS voice preview | The voice and preset pickers speak the highlighted voice. | on |
| TTS voice preset | Sets the voice, second voice, mix and speed in one go; the row shows the matching preset or `(custom)`. Built in: `amanda`, `neon`, `richard`, `hunter`, `larry`, `jack`, `willow`. See [Voice presets](COMMANDS.md#voice-presets). | `neon` |
| TTS voice | The Kokoro voice. | `af_heart` |
| TTS voice 2 | A second voice blended in; `(none)` for the first alone. | `am_eric` |
| TTS voice mix | The first voice's share of the blend, 0–100 %. | 80 |
| TTS speed | Speaking speed, 0.5–2.0. | 1.2 |

### STT

Voice input sets up in the background (🎙️ on the hint row); until it's ready, push-to-talk says so.

| Setting | What it does | Default |
|---|---|---|
| STT input | Turns the microphone on: the push-to-talk key records a message (`/stt`). | off |
| STT destination | `chat` sends what you say straight to the model; `draft` adds it to the input line for you to edit and send. Under `draft`, push-to-talk and the wake word work with text on the line (except a push-to-talk key of `Home`, `End`, `PageUp` or `PageDown`, which still move the cursor). | `chat` |
| STT wake | Saying the wake phrase at the idle line starts listening (`/wake`). | off |
| STT wake phrase | One to three words; also the interrupt phrase. | `hey neon` |
| STT interrupt | Saying the wake phrase while a reply is spoken cuts it short and listens (`/interrupt`). | off |
| STT interrupt echo guard | Ignores the assistant's own voice: speech this close to the wake phrase that it just spoke is an echo (50–100 %; 100 = the exact phrase only). | 100 |
| STT interrupt confirm | How long the phrase must hold in the recogniser's interim results before it counts (0–2000 ms). | 200 |
| STT push-to-talk key | `F1`–`F8`, `Insert`, `Home`, `End`, `PageUp` or `PageDown`. | `F4` |
| STT whisper model | `ggml-tiny.en.bin`, `ggml-base.en.bin` or `ggml-small.en.bin` (downloaded on first use). | `ggml-base.en.bin` |
| STT vosk model | The wake-word model: `vosk-model-small-en-us-0.15`, `vosk-model-en-us-0.22-lgraph` or `vosk-model-small-en-in-0.4`. | `vosk-model-small-en-us-0.15` |

### Sessions

| Setting | What it does | Default |
|---|---|---|
| Session logging | Saves every turn to the profile's `sessions.db` for `/sessions` to list, search and restore. | on |
| Session retention (days) | Deletes sessions older than this at startup (0–3650; 0 = keep forever). | 0 |
| Session naming mode | `model-written` asks the model for a title after the first turn; `first-line` uses your first line. | `model-written` |
| Session show name | Which titles show on the rule above the input row: `all-names`, `model-written` (a model-written or typed name only) or `none`. | `all-names` |
| Session tool | Offers `session_manager`, to search, list and read earlier sessions (never restore or purge). | on |
| Session search max results | Sessions one search or list returns (1–20). | 10 |
| Session save thinking | Saves each reply's thinking, so a resumed session can send it back under *LLM preserve thinking*. | off |

### Botchat

| Setting | What it does | Default |
|---|---|---|
| Botchat LLM mode | `single`: every bot uses this profile's server, model and reasoning. `multi`: each bot uses its own profile's (a blank URL borrows this one's). A bot whose server doesn't answer sits the chat out. Read when a chat starts or resumes. | `single` |
| Botchat multi-embedded | Under `multi`, for bots wanting a different embedded model from the one running. `parent-server`: they share the running model, with a warning. `multi-server`: one extra `llama-server` per model, started in turn under that bot's profile's Embedded settings so each fits in what's left. Bots on one model share its server. | `parent-server` |
| Botchat multi-embedded kill | Stops `multi-server`'s extra servers when the chat ends. Off, they run until `/botchat --kill` or you quit, and a later chat reuses them. | on |
| Botchat ComfyUI enabled | Gives the bots this profile's *ComfyUI workflows offered* for pictures. Off, *Botchat ComfyUI limited workflows* says; with neither, the chat is talk alone. Needs *ComfyUI tools* and a *ComfyUI URL*. | off |
| Botchat ComfyUI limited workflows | With *Botchat ComfyUI enabled* off, the workflows the bots get: tick them (`a` / `n`), any installed workflow, offered to this chat or not. None ticked: no pictures. A ticked workflow no longer installed is dropped when the checklist opens. | (none) |
| Botchat image mode | `automatic`: the app writes a prompt from each reply and draws it. `autonomous`: the bots get `generate_image` over the botchat workflows and draw when they choose. See Botchat pictures. | `automatic` |
| Botchat img2img mode | Which pictures a rework may start from: the `latest`, or any in `chat-history` (the last 8). Only with an image → image workflow among the botchat workflows. | `latest` |
| Botchat image async | On: the next bot speaks while a picture renders. Off: each reply waits for its picture and appears with it. | on |
| Botchat non-TTS delay | A reading pause after each reply when *TTS output* is off (0–30 s). A line you send meanwhile, or one queued while the bot was replying, ends the pause and goes to the bots at once; ESC ends the chat. | 5 |
| Botchat tools enabled | Offers every bot the tools a turn of this chat would get: the same switches, `/tools` list and panes (the shell's approval, the Docker, Home Assistant and print confirms, `ask_user`, the camera's shutter); while you plan, only plan mode's read-only tools. Not memory, skills or the ComfyUI tools, which have their own rows. Off, *Botchat limited tools* says. | off |
| Botchat limited tools | With *Botchat tools enabled* off, the tools the bots get: tick them, grouped as on `/tools` (`a` / `n`). Each is offered only while this chat would offer it. None ticked: no tools. The ComfyUI tools aren't listed. A ticked tool not listed now (an MCP server not connected) shows at the end under *Not available now*; untick it there, or `n` clears it. | (none) |
| Botchat skills enabled | Offers every bot `load_skill` over the starting profile's, the global and (with *Use external skills*) the external skills, never a bot's own profile's. The `automatic` prompt writer gets them too. Needs *Agent skills*; switching `load_skill` off in `/tools` turns this off. Off, *Botchat limited skills* says. | off |
| Botchat limited skills | With *Botchat skills enabled* off, the skills the bots (and the `automatic` prompt writer) may load: tick them (`a` / `n`), and `load_skill` is offered for those alone. None ticked: no skill tool. Needs *Agent skills*. A ticked skill not listed now shows after the others as *not available now*; untick it there, or `n` clears it. | (none) |
| Botchat memory enabled | Gives every bot memories: the list in its prompt, plus `save_memory` and `recall_memory`. Inside `/botchat` this alone decides, over every profile's *Memory mode*. | on |
| Botchat memory mode | Whose memories: `shared-parent` (every bot uses the starting profile's) or `independent` (each bot its own profile's). | `shared-parent` |
| Botchat vision enabled | Shows each bot, on its turn, the newest 4 pictures since it last spoke (not its own), captioned with whose they are. Needs models that read images. Not kept for `--resume` or the session. | off |
| Botchat camera | The bots see you: each turn gets a fresh camera picture, captioned as a photo of you. Kept in memory only. Needs models that read images; a failing camera is one warning. Read when a chat starts. | off |

#### Botchat pictures

* **The botchat workflows** are *Botchat ComfyUI enabled*'s (this profile's offered ones) or *Botchat ComfyUI limited workflows*' (any installed ones). Their text → image workflows draw new pictures and their image → image ones rework the chat's pictures; other kinds are skipped. The bots never get the main chat's own `generate_image`, so these rows alone say which workflows they use. Pictures are drawn at *Image thumbnail size*.
* **`automatic`:** after each reply, the model writes an image prompt in the workflow family's style and the app draws it. With several workflows of a kind, the writer is shown each one's style and names its pick (`WORKFLOW name`). The bots get no tool.
* **`autonomous`:** the bots get `generate_image`, limited to the botchat workflows. A reply that describes a picture the bot never drew gets it drawn anyway. A tool call written out as text (`<tool_call>` markup included) runs as a real call and is never shown or spoken.
* **Reworks:** once there's a picture and an image → image workflow, the next prompt's writer chooses between a new picture and a rework (`REWORK` or `REWORK n` in `automatic`; an `image` path in `autonomous`). A picture still rendering can't be reworked.
* **Async on:** 🖼️ (🎨 for a rework) shows on the hint row while a picture renders, with a count when several are pending. Each picture is labelled with whose reply it shows. Without speech, pictures go to ComfyUI one at a time.
* **Async off:** ESC on a held reply cuts that bot short; ESC under the picture's spinner skips just that picture.
* **Skills:** with *Botchat skills enabled*, the `automatic` prompt writer may load a skill first, so "use the pony-prompts skill for pictures" holds from the first picture. With it off, *Botchat limited skills* offers the writer the ticked skills alone.

## Skills settings (`/skills`)

### Offered

The loaded skills with their scope (`profile`, `global` or `external`), their version (`v1` as written, one more for each older text kept; blank for an external skill) and description, then any shadowed duplicates and skipped folders (with the reason). Enter on a skill can move it between the profile and global folders, rename it (lower-case-with-hyphens; a taken name is refused), edit its `SKILL.md` in your editor, revert it to a kept version you pick (see Skill history), or delete it after a confirmation.

### Reflection

| Setting | What it does | Default |
|---|---|---|
| Reflection (auto-learn) | After enough tool calls, or a tool error the model recovered from, a background reflection writes or improves a skill. | on |
| Reflection reasoning | The reflection's reasoning effort: `none`, `low`, `medium`, `high`, `xhigh`, or `profile` for the profile's level. | `none` |
| Reflection window | How many recent turns a reflection reads (1–5; the last in full, earlier ones trimmed). | 3 |
| Reflection min tool calls | The model's own tool calls since the last reflection that make a task worth a skill (3–20). | 4 |
| Reflection max requests | Model requests one reflection may spend (1–20). | 4 |
| Reflection cooldown (minutes) | How long automatic reflections wait after one wrote a skill (0–1440; 0 = off). A turn where that skill was loaded and then hit an error never waits. | 5 |
| Reflection cooldown mode | `last-written-skill`: only turns that used the skill just written wait. `all-skills`: every automatic reflection waits. | `last-written-skill` |
| Reflection includes sessions | The reflection starts from earlier sessions that match the turn, and can search them. | on |
| Reflection yields to turns | A message sent during a reflection pauses it so the reply gets the server; it reruns afterwards. Turn off if your server handles parallel requests. | on |
| Reflection edit supporting files | Lets a reflection change a skill's supporting files too (with `skill_editor`'s `write_file` and `edit_file`), not just `SKILL.md`. The main chat always may. | off |
| Reflection downloaded skills | For skills from `/skills add`: `read-only` makes the reflection write a companion skill instead, so updates stay clean; `allow-and-mark` lets it change the skill, and a later update warns first. | `allow-and-mark` |

A reflection must `load_skill` a skill before rewriting its instructions or a supporting file, and is refused if the skill changed since that load. A description-only change needs no load.

### Options

| Setting | What it does | Default |
|---|---|---|
| Agent skills | Lists the skills in the prompt and offers `load_skill` and `skill_editor`. Off also stops the project file being read. | on |
| Use external skills (.agents\skills) | Also reads `%USERPROFILE%\.agents\skills`, read-only. On a Mac the row reads *Use external skills (.agents/skills)* and the folder is `~/.agents/skills`. | off |
| Project file | Reads `NEON.md` (or `AGENTS.md`) in the working directory into the prompt as project notes. Needs *Agent skills*. | on |
| Skill compact mode | `protected` keeps a loaded skill's instructions through a prune; `unprotected` prunes them like any tool result. | `protected` |
| #-mention enabled | `#` and part of a name on the input line lists the skills; a pick writes `#name`. | on |

### Installing skills

`/skills add` installs skills in the [Agent Skills](https://agentskills.io) format, the same folders other agents use.

- **Sources:**
  - Search words (`/skills add pdf`) look the skill up on [skills.sh](https://skills.sh), the public directory.
  - `owner/repo` offers every skill in a GitHub repository; `owner/repo/skill` names one (the id a search shows).
  - A `github.com/…/tree/<branch>/<path>` or `…/blob/<branch>/<path>/SKILL.md` link narrows to a folder.
  - Any `https://…/*.zip` link is downloaded as is.
- **Fetching:** the app lists the repository through the GitHub API (two requests; 60 an hour without a token) and fetches only the `SKILL.md` files, then the chosen skill's files, from `raw.githubusercontent.com` pinned to that commit. A repository with more than 100 skills must be narrowed to one. If the API fails, it falls back to GitHub's zip of the branch (up to 50 MB).
- **Network:** *Web browser network mode* applies (nothing is fetched under `local_area_network`); the *Web tools* switch doesn't.
- **Preview first:** you see the description, source and commit, other frontmatter (`allowed-tools`…), the file list, any scripts and the start of the instructions. The install is refused for a path that could escape the folder or isn't valid on Windows, two files differing only by case, or more than 200 files, 20 MB in all or 5 MB in one file. Symbolic links are left out.
- **Where it goes:** the profile's or the global `skills` folder, named after the skill, with a `.neon-source.json` recording the repository, path, commit and date. Bundled scripts run only through `run_command` and its approval.
- **Name collisions** are refused, except the same repository and path installed again, which updates it in place.

### Skill records and purging unused skills

`skills.db` in the home folder records every global and profile skill: its folder, scope, when it was created, last modified and last used (loaded), and a category (empty for now).

- `skill_editor`, reflections, `/skills add` and the `/skills` pane update the record as they act.
- At startup and every profile load, the app reconciles the folders: a new folder gets a record (dated from its `SKILL.md`), a newer `SKILL.md` moves the modified date, and a record whose folder is gone is removed.
- External skills (`.agents\skills`) are not recorded and never purged.

`/skills purge list <age>` lists skills unused for that long; `/skills purge commit <age>` deletes them (folder and record) after a yes/no. The age is days (`30`) or a duration (`12h`, `90m`, `1d 6h`). A never-used skill counts from its last change. Only global skills and the loaded profile's are considered, and a global skill used in any profile counts as used. There is no `purge all`.

### Skill history

`skills.db` also keeps each skill's history, which the reflection reads:

- **Changes:** each one is logged with who made it (the model, a reflection, an install or a revert). A newer `SKILL.md` the app didn't write counts as a hand edit.
- **Use:** each turn that loaded a skill is logged with the tool errors that came *after* the load.
- **The reflection's view:** each skill carries a usage line (`loaded 12 times across 6 sessions, 3 followed by errors; …; edited by hand …; installed from owner/repo`), also the caption of its `/skills` page. The reflection is asked to fix skills often followed by errors, keep your wording in hand-edited ones, and prefer a companion skill over changing an installed one.
- **Earlier versions:** before overwriting `SKILL.md` or a supporting file, the old text is kept (the last 10 per skill, up to 256 KB each). An update through `/skills add` keeps only the old `SKILL.md`.
- **Hand edits:** when the app finds a hand-edited `SKILL.md`, it keeps a copy of your text, one per skill, until the app next writes that file.
- **Reverting:** on `/skills`, Enter or a double-click on a skill, then `revert`, lists every kept version, newest first: `before the model's change at …`, `not there before …` (putting it back removes the file), `your edit of …`, `before a revert at …`, the one the file holds now marked `· current`. Each kept text carries its version number, counted as the skill list's (the oldest kept `v1`; the caption says which version the skill is at now), and a `not there before …` row has none. Enter (or a double-click) puts the pick back after a yes, No on the cursor. The file's current text is kept first as a version, so nothing is lost and you can go back and forth. Only a current text over 256 KB, which can't be kept, refuses. A skill keeps its newest 10 versions; the oldest goes when another is kept.
- **Removing versions:** on the version list, `d` (or **✖ remove**) removes the highlighted version and `c` (or **⊠ clear all**) removes every kept version, each after a yes with No on the cursor. The text in place stays, and so do the skill's record and use; a removed version can no longer be put back.
- The history lives in `skills.db`, so it survives purged sessions, renamed skills and *Session logging* off.

## Tools settings (`/tools`)

### Offered

Every tool, grouped, the groups in alphabetical order, with the description the model reads. Enter or Space switches one; a group whose switch is off is dim. The Help group (`neon_help`) has no switch of its own, so turn it off here. In a new profile, `gitlib_delete`, `zip`, `unzip`, `unc_delete`, `docker_remove`, `docker_prune` and `ha_todo` start off.

### Web

| Setting | What it does | Default |
|---|---|---|
| Web tools | Offers `web_search`, `web_fetch`, `open_url` and `download_file`. | off |
| Web browser mode | `default` uses HTTP and falls back to a headless browser for a blocked or empty page; `httpclient` never falls back; `chromium` uses the browser for every page. The buttons at the top of the *Web tools* on/off page (D, H, C; the toolbar's 🌐 opens it) switch it too. | `default` |
| Web browser path | The Chromium-based browser for headless fetches and for making PDFs; empty finds Edge, Chrome or Brave. | (auto) |
| Web browser network mode | Where a fetch may reach: `internet` (public addresses), `local_area_network` (this machine and the LAN) or `both`. | `internet` |
| Web search method | `duckduckgo` (built in) or `searxng`. | `duckduckgo` |
| Web SearXNG URL | The SearXNG instance, for `searxng`. | (not set) |
| Web search max results | Hits per search (1–20). | 20 |
| Web download max (MB) | The largest file `download_file` saves (1–102400), streamed to disk. A download silent for 60 s is dropped, and no partial file stays. | 50 |

### Files

| Setting | What it does | Default |
|---|---|---|
| File tools | Offers the sandboxed file tools (read, write, patch, search, move, copy, zip, view_image, image_edit…), which reach only the working directory (a junction or symlink in it that leads outside is refused; deleting or moving the link itself is allowed, its target untouched). | off |
| File /tree max length | Entries `/tree` prints before it stops (1–10000). | 500 |
| File /tree show sizes | `/tree` shows file sizes. | on |
| File @-mention folder mode | Picking a folder from the `@` list: `folder-remain` opens it in the list; `folder-apply` writes `@folder/` and closes. | `folder-remain` |
| File browser/tree mode | `default` hides hidden, system and dot entries in the folder browsers and `/tree`; `show-hidden` lists them (`/tree` still leaves out `.git`). | `default` |
| File view image max (per call) | Pictures one `view_image` call may load (1–100). | 10 |
| File search max results | The most rows one `search_files` or `unc_search` call returns, whatever its `limit` (1–5000). Without `limit`: 50 hits, 200 entries, 100 names or 10 recent files. | 200 |
| Image edit quality | The quality `image_edit` writes a JPEG (or JPEG XL, HEIF) at when the model gives none (1–100), and where `max_kb` starts lowering it from. | 90 |
| Image edit metadata | What metadata `image_edit` keeps unless the model asks otherwise: `none` (no camera, date or place), `basic` (author, copyright, title, comment, date taken, camera, exposure) or `all` (basic and the GPS position). | `none` |
| Image edit mode | Where an edited picture goes, for the picture windows' right-click menu and `image_edit` (when the model names no `to`): `beside-original` writes a new file beside the source (`image_edit` uses *Image edit output folder* when set), `overwrite-original` replaces the source (a format change writes the new file and deletes the old). No confirmation. | `beside-original` |
| Image edit output folder | Where `image_edit` writes when the model names no `to`: a folder under the working directory, made on first use. Empty writes beside the source. | (beside the source) |

### Shell

| Setting | What it does | Default |
|---|---|---|
| Shell command policy | How the model may run shell commands: `off` (no shell tools), `ask` (anything not on the allowed list goes to the approval pane; refused with no pane) or `yolo` (everything runs). See Shell guards. | `off` |
| Shell allowed commands | Command prefixes allowed for good (`git status`, `dotnet build`, `python`). Enter removes one after a yes; the pane's *Allow … always* adds one. The ask and yolo buttons (`a`, `y`) switch the policy. `/cmdlist` opens it; `/cmdcopy` copies it to another profile. | none |
| Shell police | Refuses a command, script or process input naming a path outside the working directory, before it runs or asks. Turning it off asks first, and also stops the forbidden strings and the SQLite rule; `/police` opens it. See Shell guards. | on |
| Shell police forbidden strings | Strings the police refuses outright in a command, script or process input, case and spacing ignored. Enforced only while *Shell police* is on. The top row adds one, Enter removes one after a yes; `/police`'s strings button (`s`) opens it too. See Shell guards. | none |
| Shell prefer native tools | Steers the model to the app's own tools: a lone shell command one of them covers is sent back (once a turn). See Shell guards. | on |
| Shell default | The shell when a call names none: `powershell` (pwsh if installed, else 5.1), `cmd`, or `bash` (Git Bash). On macOS: `zsh`, `bash` or `powershell` (pwsh, if installed). | `powershell` (`zsh` on macOS) |
| Shell timeout (s) | How long a foreground command without its own `timeout` may run (1–3600). | 180 |
| Shell foreground cap (s) | The longest any foreground command may run (10–3600). | 600 |
| Shell output max chars | Output one result carries (2000–500000). Past that, the start and end are kept and the whole text goes to `.shell\<id>.log` in the working directory. | 30000 |
| Shell code languages | What `execute_code` may run: `powershell`, `python`, `node` (each only when its interpreter is found). `a` turns all on; the last one can't be turned off. | all three |
| Shell code timeout (s) | How long a script without its own `timeout` may run (1–3600). | 300 |
| Shell tool bridge | Lets an `execute_code` script call the app's other tools through its `neon_tools` module (a loopback socket with a per-run token). | off |
| Shell tool bridge max calls | Tool calls one script may make through the bridge (1–500). | 50 |

#### Shell guards

* **Approval (`ask`):** the pane offers Deny, Allow once, Allow the prefixes for this session, or Allow them always. The command shows above them, cut to three rows (a script by its first line); `v` or the **≡ view** button shows the whole of it, a script's lines numbered, and ESC comes back to the question. A prefix is the program plus its subcommand for git, dotnet, npm, pip, gh, docker, cargo, go, winget, net and the like, otherwise the program alone. `--yolo` and `NEONSIDEKICK_COMMAND_POLICY` override the policy for one launch.
* **Path police** reads the text of a `run_command` line, an `execute_code` script, or `process` input, and refuses an absolute path outside the working directory (`C:\…`, a UNC share, `/etc/hosts`), a `..` that climbs out, `~`, or a folder variable (`%USERPROFILE%`, `$env:TEMP`, `$HOME`, `Path.home()`…). The model gets `Error: outside the working directory: '…'` and the transcript shows 👮.
  * It also refuses a path inside a switch or a URL (`-out:C:\x`, `@C:\x.rsp`, `7z -oC:\x`, `-I..\x`, `file:///C:/x`), a bare drive (`C:`, `cd /d E:`), a `cd` to a folder at the drive's root (`cd /etc`; in bash `/c` is the C drive), a bare `cd` or `Set-Location` in PowerShell or bash, also with only options or a redirect (`cd -P`, `cd >/dev/null`; it goes home there; cmd's only prints the folder), and a junction or symlink in the working directory that leads outside.
  * A quoted path with a space that stays inside passes (`"D:\My Projects\app\a.txt"`), and a `cd` earlier in the line moves where later relative paths start (`cd src && type ..\README.md`) when the next command surely runs there: after `&&` or `;` (cmd's `&` too), outside a subshell, to a folder that exists. After a pipe, `||` or a `cd` that may fail, a later path must stay inside from both folders.
  * It reads text, not what runs: a computed path isn't seen, and a cmd switch (`dir /s`), a URL or a device (`>nul`, `/dev/null`) isn't a path.
  * `--no-police` and `NEONSIDEKICK_SHELL_POLICE` override it; `--yolo` never does.
* **Forbidden strings:** while the path police is on, a `run_command` line, `execute_code` script or `process` input that contains a string on *Shell police forbidden strings* is refused before it runs or asks, even under `--yolo`. Case is ignored and any run of spaces, tabs or line breaks counts as one space (`rm -rf` catches `RM   -RF`). The model gets `Error: forbidden by the shell police — the user does not allow this command or script…`, never the string; the 👮 line shows it to you (`forbidden string 'rm -rf' — not run`) and the log names it. It reads text, so a command built in pieces gets past it: a tripwire, not a sandbox.
* **SQLite:** while the path police and *SQLite tools* are both on, a `run_command` line, `execute_code` script or `process` input that reaches SQLite is refused before it runs or asks, even under `--yolo`, so a database is reached only through the sqlite_ tools (whose read-only mode, statement kinds and Allow pane would otherwise be walked around). Reaching SQLite means:
  * the word `sqlite` anywhere (the `sqlite3` CLI, Python's `sqlite3`, `System.Data.SQLite`, `Microsoft.Data.Sqlite`, `better-sqlite3`, `node:sqlite`);
  * a file *sqlite.json* names, by path or file name;
  * a `.db`, `.db3`, `.sqlite` or `.sqlite3` file name (with `-journal`, `-wal` or `-shm` too). In a script it counts only quoted or after a slash, so `self.db` passes.

  A script file a `run_command` line runs (`python insert.py`) is read and judged too. The model gets `Error: refused by the shell police — SQLite…`, sent to the sqlite_ tools; the 👮 line shows what tripped it (`SQLite: 'sqlite3' in insert.py — not run`). It reads text, so a script that builds the word in pieces gets past it; only `ask` shows you every command.
* **Server databases:** the same rule for SQL Server, Oracle, MySQL and PostgreSQL, each while its tools switch (*SQL tools*, *Oracle tools*, *MySQL tools*, *PostgreSQL tools*) is on, in either mode, so their `_execute` tools' modes, statement kinds, access keys and Allow pane can't be walked around. Reaching one means:
  * its clients and drivers, found inside a longer name too (`pymysql`, `Microsoft.Data.SqlClient`, `psycopg2`): SQL Server's `sqlcmd`, `osql`, `bcp`, `sqlpackage`, `Invoke-Sqlcmd`, `SqlServer`, `SqlClient`, `pyodbc`, `pymssql`, `mssql`; Oracle's `sqlplus`, `sqlcl`, `oracledb`, `cx_Oracle`, `ManagedDataAccess`, `tnsping`, `expdp`, `impdp`, `rman`; `mysql` and `mariadb`; `postgres`, `psql`, `pg_dump`, `pg_dumpall`, `pg_restore`, `pgcli`, `psycopg`, `asyncpg`, `npgsql`, `pg8000`. The short ones (`bcp`, `osql`, `psql`, `rman`, `sqlcl`) count only standing alone;
  * a host one of its connections names (offered or not), as a whole name; a loopback host (`localhost`, `127.0.0.1`, `.`) only with its port (`localhost:5432`, `localhost,1433`), so `curl localhost:8080` passes.

  Script files a line runs are judged too. The model gets `Error: refused by the shell police — database…`, sent to that family's tools; the 👮 line shows what tripped it (`PostgreSQL: 'psql' — not run`). A tripwire, as above.
* **Prefer native tools:** the operating rules name the tools offered that turn and the commands each replaces:
  * `cat`/`type`/`Get-Content`/`dir`/`ls`/`grep` → `read_file`/`search_files` (on a Mac `type` is sent back only in PowerShell: zsh's and bash's `type` describes a command, so the rule leaves out `type` and `dir` there)
  * `git status`/`log`/`diff`/`add`/`commit` → the GitLib tools
  * `curl`/`Invoke-WebRequest` → `web_fetch`
  * `sqlcmd` → `sql_query`, `sqlplus` → `oracle_query`, `mysql` / `mariadb` → `mysql_query`
  * `net use` / `net share` / `net view` / `Get-SmbShare` / `Get-SmbMapping` → `unc_shares`; `dir \\server`, `copy \\server` → the UNC tools

  A lone command such a tool covers comes back as `Not run: 'cat' has a tool of its own — call read_file instead…`, once a turn (sent again, it goes to the pane). Pipes and compound lines, commands with no tool (`git push`) and tools switched off are never sent back.

### Ask

| Setting | What it does | Default |
|---|---|---|
| Ask user | Offers `ask_user`: multiple-choice questions on the pane. | on |
| Ask max questions | Questions per call (1–10). | 10 |
| Ask max choices per question | Options per question (2–15). | 10 |

### Camera

| Setting | What it does | Default |
|---|---|---|
| Camera tool | Offers `camera_capture`, so the model can ask you for a photo. Never offered headless or to an embedded model without vision. `/camera` works either way. Its on/off page (the toolbar's 📸) has a **watch** button (`w`) that starts or stops `/camera watch`, **live** (`l`) that opens or closes the camera's window, and **snap** (`s`) and **screen** (`c`), which close the pane and run `/camera snap` or `/screen`. | off |
| Camera shutter | `user`: the camera pane shows the request; Space takes the photo, R retakes, Enter sends, ESC declines. `model`: a pane asks Deny / Allow once / Allow for this session, and on a yes the app takes it. | `user` |
| Camera preview | `live`: a camera window of its own shows the camera mirrored while you frame, then the photo. `post`: the picture viewer opens on the photo. `disabled`: no window. Neither takes the keyboard. | `live` |
| Camera device | The camera, by its Windows name. `(first camera)`, or a camera that isn't connected, uses the first. | (first camera) |
| Camera resolution | The size asked for (`640x480`, `1280x720` or `1920x1080`); the camera uses its nearest. | `1280x720` |
| Camera output folder | Where photos are saved, under the working directory (empty = the working directory). Watch mode's saved pictures go in its `.watch` subfolder. | `camera_images` |
| Camera keep in sessions | Off, a stored session keeps a line naming the photo instead of the picture. | off |
| Camera watch interval (s) | How often `/camera watch` looks (2–3600). | 10 |
| Camera watch change (%) | How much of the picture must change to count (1–100); brightness shifts don't. `--camera-check` prints your camera's noise. | 8% |
| Camera watch speaks up | Off, a changed picture rides your next message. On, the model is shown it unasked, at most once per gap below, when nothing else is going on. | off |
| Camera watch min gap (s) | The least time between unprompted watch turns (30–3600). | 120 |

### Screen

| Setting | What it does | Default |
|---|---|---|
| Screen capture tool | Offers `screen_capture` and `screen_list`, so the model can see a monitor, every monitor or one window. Never offered headless or to an embedded model without vision. `/screen` works either way. | off |
| Screen capture ask | `ask`: a pane names what would be captured and why; Deny, Allow once, or Allow for this session. `allow`: taken without asking. | `ask` |
| Screen capture preview | The picture viewer opens on each screenshot (without the keyboard), so you see what was sent. | on |
| Screen capture output folder | Where screenshots are saved, under the working directory (empty = the working directory). | `screen_images` |
| Screen capture keep in sessions | Off, a stored session keeps a line naming the screenshot instead of the picture. | off |

### ClaudeCLI

The Claude Code CLI, for `/claude` (you message it) and `claude_advisor_cli` (the model asks it). The Anthropic API and the Claude CLI as servers are on `/settings` › Anthropic.

| Setting | What it does | Default |
|---|---|---|
| Claude CLI executable | The Claude Code CLI. Blank looks on the PATH and in `%USERPROFILE%\.local\bin` (`~/.local/bin` on a Mac); a path you set must exist. | (looked up) |
| Claude CLI slash command permissions | What `/claude` may do: `read-only` (`Read`, `Grep`, `Glob`, `WebSearch`, `WebFetch`), `edit` (its usual tools with file edits, no commands) or `full` (everything, `bypassPermissions`). Anything else is denied, never asked. It works in the working directory but outside the app's sandbox and approvals. | `read-only` |
| Claude CLI slash command model | `/claude`'s `--model`: Claude Code's default, `fable`, `opus`, `sonnet`, `haiku`, or *Other…*. | (Claude Code's default) |
| Claude CLI slash command effort | `/claude`'s `--effort`: Claude Code's default, `low`, `medium`, `high`, `xhigh` or `max`. | (Claude Code's default) |
| Claude CLI advisor tool | Offers `claude_advisor_cli`: read-only advice from Claude Code when the model is stuck. Each call costs money on your Claude account. | off |
| Claude CLI advisor tool context | `brief` sends the question and context; `recent` adds the last 10 messages (tool results cut to 500 characters). | `brief` |
| Claude CLI advisor tool calls per turn | Advisor calls one reply may make (1–10). | 2 |
| Claude CLI advisor tool model | The advisor's `--model`; the first row follows *Claude CLI slash command model*. | (as Claude CLI slash command model) |
| Claude CLI advisor tool effort | The advisor's `--effort`; the first row follows *Claude CLI slash command effort*. | (as Claude CLI slash command effort) |
| Claude CLI advisor tool confirm | Each call waits for your yes (the cursor starts on No). Refused headless. | off |

### Home Assistant

| Setting | What it does | Default |
|---|---|---|
| Home Assistant tools | Offers the Home Assistant tools (`ha_overview`, `ha_states`, `ha_history`, `ha_lights`, `ha_scene`, `ha_media`, `ha_todo`, `ha_call_service`, `ha_assist`) once a URL and token are set. | off |
| Home Assistant URL | Your server (`http://localhost:8123`, or on your LAN). The web tools' network mode never blocks it. | (not set) |
| Home Assistant API key | A long-lived access token (your HA profile → Security). Typed masked, saved encrypted (DPAPI), never logged; empty clears it. | (none) |
| Home Assistant test connection | Asks the server for its version with the saved URL and token. | — |
| Home Assistant action policy | `off`: read only. `ask`: lights, scenes, the TV's power, volume, source and playback, and to-do lists run; anything else (a remote key, a button, a switch, a script, an automation, a restart) asks first (refused headless). `allow`: everything runs. `/ha` never asks. | `ask` |
| Home Assistant Assist agent | The conversation agent `ha_assist` and `/ha say` use (e.g. `conversation.google_generative_ai`); empty for the default. | (Home Assistant's default) |
| Home Assistant timeout (s) | How long one request may take (2–60). | 10 |

The services that run unasked under `ask` can be changed in `profile.json` (`homeAssistantSafeServices`: `light.*`, `remote.send_command`…).

### Print

| Setting | What it does | Default |
|---|---|---|
| Print tools | Offers `list_printers` and `print_file`. `/print` works either way. Printing needs Windows: on a Mac the group is never offered and `/print` says so. | off |
| Print action policy | `off`: list printers only. `ask`: each print shows the file, printer, pages and copies and waits for your yes (refused headless). `allow`: prints without asking. `/print` never asks. | `ask` |
| Print default printer | Where a print goes when none is named. On a Mac the row reads *(none: printing needs Windows)*. | (Windows default) |
| Print font size (pt) | Body text size for printed listings and Markdown (6–24); headings scale from it. | 10 |
| PDF engine | What makes a PDF for `convert_to_pdf` and `/pdf`. `auto`: Edge, Chrome or Brave, else Microsoft Print to PDF, which also takes over when the browser fails. `browser`: the browser only. `printer`: Microsoft Print to PDF only (Markdown, text and pictures). See [Making PDFs](TOOLS.md#making-pdfs). On a Mac only the browser makes PDFs (Edge, Chrome, Brave or Chromium); `printer` needs Windows. | `auto` |

### Obsidian

| Setting | What it does | Default |
|---|---|---|
| Obsidian tools | Offers the vault tools (search, list, read, links, daily, write, properties, move) once a vault is set. | off |
| Obsidian vault | The vault's folder (holding `.obsidian`), separate from the working directory. The row opens the folder picker. | (not set) |
| Obsidian allow delete (.trash) | Offers `vault_delete`, which moves a note or attachment into the vault's `.trash`. | on |

### ComfyUI

| Setting | What it does | Default |
|---|---|---|
| ComfyUI tools | Offers `generate_image` and `set_splash_image` once *ComfyUI URL* is set and a workflow is installed. | off |
| ComfyUI URL | The ComfyUI server, often on your LAN (`http://gpu-box:8188`). The web tools' network mode never blocks it. | (not set) |
| ComfyUI workflows offered | A checklist of the workflows the model is offered; nothing until ticked (here or in the wizard). `a` / `n` tick all or none. With one ticked, every plain request and plain `/imagine` uses it. `/imagine <name>` can still use a hidden one. A ticked workflow no longer installed is dropped when the checklist opens (not one whose file failed to load). The checklist lines up family, shape and size in columns. | none |
| ComfyUI add workflow | A wizard that **builds** a standard workflow from your server's checkpoints, or **imports** a ComfyUI export. See Adding a workflow. | — |
| ComfyUI ^-mention enabled | `^` and part of a name lists the offered workflows; a pick writes `^name`, which `generate_image` uses. | on |
| ComfyUI timeout (s) | How long to wait for one generation, queue included (10–3600). | 300 |
| ComfyUI max pictures per call | Most pictures one `generate_image` call or `/imagine --count` makes (1–16). All go to the model in the next request. | 5 |
| ComfyUI reinforce negatives | When the model writes the prompt, adds a few opposite tags to the negative where the image model tends to drift (a solo figure → `multiple girls`). Skipped for verbatim prompts, a negative given for the call, `/imagine`, families without a negative and a workflow whose `.md` says `reinforce: false`. | on |
| ComfyUI show prompts | Shows the `prompt:`, `negative:` and `params:` lines under each picture. | on |
| ComfyUI picture strip | Keeps the session's pictures as thumbnails above the input line, newest left by when each file was made, the viewer's order (a late Botchat image async picture lands in its place). With the line empty, ←/→ highlight one (moving an open viewer to it) and Enter opens it; a double-click opens any, and a drag onto the input row attaches it. **🎞️** opens the viewer on the output folder; **×** hides the strip until the next picture; meanwhile **🎞️** on the rule over the input row (right of ⤡) brings it back. `/clear`, `/new` and a session switch empty it. | on |
| ComfyUI output folder | Where pictures are saved, under the working directory (`comfy_images\pony-txt2img-1234.png`); empty = the working directory. | `comfy_images` |

### SQL

| Setting | What it does | Default |
|---|---|---|
| SQL tools | Offers the SQL tools (connections, databases, tables, columns, describe, relationships, indexes, query) over `sql.json`'s connections. | off |
| SQL mode | `read-only`: the tools only read. `read-write`: `sql_execute` is offered too, on connections whose entry says `"access": "readwrite"`, each change allowed by you. See SQL › Changes. | read-only |
| SQL statements allowed | Under `read-write`: the kinds of statement `sql_execute` may run (a checklist; A all, N none, D the default). See SQL › Changes. | changing data, creating, reading |
| SQL connections offered | A checklist of the connections in both `sql.json` files; nothing is offered until ticked (here or in the wizard). `a` / `n` tick all or none. A hidden connection is invisible to every tool, the rules and the `%`-mention. A ticked name no longer in the files is dropped when the checklist opens, and the status line names it; not while a file can't be read, nor a name an entry with a problem still holds. | none |
| SQL default connection | The connection a call uses when it names none: an offered one, or the first. | (the first connection) |
| SQL set password | Pick a `sql` or `runas` connection and type its password, masked; it goes to that connection's store. | — |
| SQL add/edit connection | A wizard for a new connection, or to edit a saved one, which can **test** it (`SELECT @@VERSION`) before saving. See Managing connections. | — |
| SQL %-mention enabled | `%` and part of a name lists the connections; a pick writes `%name`. | on |
| SQL max rows | Rows `sql_query` returns unless the call says otherwise (1–100000). | 100 |
| SQL query timeout (s) | How long one batch may run on the server (1–600). | 30 |
| SQL query result max chars | The most characters of table one `sql_query`, `oracle_query` or `mysql_query` returns (1000–1000000); the header says how many rows fit. | 32,000 |
| SQL connections (profile) | Enter opens the profile's `sql.json` in your editor (created with commented examples). | (none) |
| SQL connections (global) | The same for the home folder's `sql.json`, which every profile reads. The profile's wins a name clash. | (none) |

### Oracle

The Oracle, MySQL and UNC tabs work like the SQL tab, over `oracle.json`, `mysql.json` and `unc.json`.

| Setting | What it does | Default |
|---|---|---|
| Oracle tools | Offers the Oracle tools (connections, schemas, tables, columns, describe, relationships, indexes, query). | off |
| Oracle mode | `read-only`: the tools only read. `read-write`: `oracle_execute` is offered too, on connections whose entry says `"access": "readwrite"`, each change allowed by you. See Oracle › Changes. | read-only |
| Oracle statements allowed | Under `read-write`: the kinds of statement `oracle_execute` may run (a checklist; A all, N none, D the default). See Oracle › Changes. | changing data, creating, reading |
| Oracle connections offered | As *SQL connections offered*. | none |
| Oracle default connection | As *SQL default connection*; `schema` works in another schema. | (the first connection) |
| Oracle set password | As *SQL set password*. | — |
| Oracle add/edit connection | The wizard (new or edit); its test shows who it signs in as, the version, and a warning when the account could change data. See Oracle. | — |
| Oracle %-mention enabled | Lists the Oracle connections in the `%` list too, marked `Oracle ·`. | on |
| Oracle max rows | As *SQL max rows*, for `oracle_query`. | 100 |
| Oracle query timeout (s) | How long one statement may run on the server (1–600). | 30 |
| Oracle connections (profile) | As *SQL connections (profile)*. | (none) |
| Oracle connections (global) | As *SQL connections (global)*. | (none) |

### MySQL

| Setting | What it does | Default |
|---|---|---|
| MySQL tools | Offers the MySQL tools (connections, databases, tables, columns, describe, relationships, indexes, query), for MySQL 8.0.16+ and MariaDB 10.2+. | off |
| MySQL mode | `read-only`: the tools only read. `read-write`: `mysql_execute` is offered too, on connections whose entry says `"access": "readwrite"`, each change allowed by you. See MySQL › Changes. | read-only |
| MySQL statements allowed | Under `read-write`: the kinds of statement `mysql_execute` may run (a checklist; A all, N none, D the default). See MySQL › Changes. | changing data, creating, reading |
| MySQL connections offered | As *SQL connections offered*. | none |
| MySQL default connection | As *SQL default connection*. | (the first connection) |
| MySQL set password | As *SQL set password*. | — |
| MySQL add/edit connection | The wizard (new or edit); its test shows who it signs in as, the version, and a warning when `SHOW GRANTS` allows changes. See MySQL. | — |
| MySQL %-mention enabled | Lists the MySQL connections in the `%` list too, marked `MySQL ·`. | on |
| MySQL max rows | As *SQL max rows*, for `mysql_query`. | 100 |
| MySQL query timeout (s) | How long one statement may run (1–600), enforced by the server and the driver. | 30 |
| MySQL connections (profile) | As *SQL connections (profile)*. | (none) |
| MySQL connections (global) | As *SQL connections (global)*. | (none) |

### SQLite

| Setting | What it does | Default |
|---|---|---|
| SQLite tools | Offers the SQLite tools (databases, tables, describe, query) over the databases named in `sqlite.json` and, below, the working directory's files. While on, the shell police and the file tools keep out of SQLite databases (see *SQLite* › *Shell and files*). | off |
| SQLite mode | `read-only`: the tools only read. `read-write`: `sqlite_execute` is offered too, one change per call of the kinds below, or a new database file in the working directory, each allowed on a pane (Deny, Allow once, Allow for this session). See *SQLite* › *Changes*. | read-only |
| SQLite statements allowed | Under `read-write`, the kinds of statement `sqlite_execute` may run, as a checklist: changing data, deleting, creating, changing structure, dropping, upkeep, settings, reading (see *SQLite* › *Changes*). `a` / `n` / `d` pick all, none or the default. With none ticked, `sqlite_execute` isn't offered. | changing data, creating, reading |
| SQLite databases offered | Which databases of `sqlite.json` the model sees. None until you tick them. Otherwise as *SQL connections offered*. | none |
| SQLite default database | The database a call uses when it names none. | (the first database) |
| SQLite sandbox files | The model may also open any SQLite file inside the working directory by its path (`data/app.db`), and `sqlite_execute`'s `create` may make one there. | off |
| SQLite add/edit database | The wizard (new or edit): the file to save in, the name, the database file, a description; its test opens the file read-only and counts the tables. | — |
| SQLite %-mention enabled | Lists the SQLite databases in the `%` list too, marked `SQLite ·`. | on |
| SQLite max rows | As *SQL max rows*, for `sqlite_query`. | 100 |
| SQLite query timeout (s) | How long one statement may run before it is interrupted (1–600). | 30 |
| SQLite databases (profile) | Opens this profile's `sqlite.json` in your editor. | (none) |
| SQLite databases (global) | Opens the global `sqlite.json` in your editor. | (none) |

### Postgres

| Setting | What it does | Default |
|---|---|---|
| PostgreSQL tools | Offers the PostgreSQL tools (connections, databases, schemas, tables, columns, describe, relationships, indexes, query). | off |
| PostgreSQL mode | `read-only`: the tools only read. `read-write`: `postgres_execute` is offered too, on connections whose entry says `"access": "readwrite"`, each change allowed by you. See PostgreSQL › Changes. | read-only |
| PostgreSQL statements allowed | Under `read-write`: the kinds of statement `postgres_execute` may run (a checklist; A all, N none, D the default). See PostgreSQL › Changes. | changing data, creating, reading |
| PostgreSQL connections offered | As *SQL connections offered*. | none |
| PostgreSQL default connection | As *SQL default connection*; `database` works in another database on the same server. | (the first connection) |
| PostgreSQL set password | As *SQL set password*. | — |
| PostgreSQL add/edit connection | The wizard (new or edit); its test shows who it signs in as, the version, and a warning when the role could change data (a superuser is warned of, not refused). See PostgreSQL. | — |
| PostgreSQL %-mention enabled | Lists the PostgreSQL connections in the `%` list too, marked `PostgreSQL ·`. | on |
| PostgreSQL max rows | As *SQL max rows*, for `postgres_query`. | 100 |
| PostgreSQL query timeout (s) | How long one statement may run (1–600): the server's own `statement_timeout`. | 30 |
| PostgreSQL connections (profile) | As *SQL connections (profile)*. | (none) |
| PostgreSQL connections (global) | As *SQL connections (global)*. | (none) |

### UNC

| Setting | What it does | Default |
|---|---|---|
| UNC tools | Offers `unc_shares`, `unc_search`, `unc_info`, `unc_read`, and `unc_fetch` while the File tools are on. See UNC shares. Never on a Mac (`/tools`: *off: it needs Windows*). | off |
| UNC writes | The master key for changes. On, a share with `access: readwrite` also gets `unc_write`, `unc_patch`, `unc_create_directory`, `unc_move`, `unc_copy`, `unc_delete` (off by default in Offered) and `unc_put`. Changes are permanent. | off |
| UNC shares offered | As *SQL connections offered*. | none |
| UNC default share | The share a call uses when it names none and gives no full path. | (the first share) |
| UNC set password | As *SQL set password*, for runas shares. | — |
| UNC add/edit share | The wizard (new or edit); its test lists the share's root under its account. | — |
| UNC *-mention enabled | `*` and part of a name lists the offered shares; a pick writes `*name`. | on |
| UNC shares (profile) | As *SQL connections (profile)*. | (none) |
| UNC shares (global) | As *SQL connections (global)*. | (none) |

### Docker

| Setting | What it does | Default |
|---|---|---|
| Docker tools | Offers `docker_containers`, `docker_logs`, `docker_inspect`, `docker_stats`, `docker_resources` and `docker_compose`, whether Docker Desktop runs or not. `/docker` works either way. See Docker. Never on a Mac (`/tools`: *off: it needs Windows*). | off |
| Docker writes | The master key for the model's changes: `docker_lifecycle`, `docker_pull`, `docker_remove` and `docker_prune` (the last two off by default in Offered). Every call asks first; headless refuses them. | off |
| Docker engine pipe | The engine's named pipe: a name (`docker_engine` is Docker Desktop's), `\\.\pipe\name` or `npipe:////./pipe/name`. Blank is the default. | `\\.\pipe\docker_engine` |

### YouTube

| Setting | What it does | Default |
|---|---|---|
| YouTube tools | Offers the YouTube tools: `youtube_search` (with a key below), and `youtube_play`, `youtube_control` and `youtube_status`, which play a video in the app's own video window (Windows, with the WebView2 Runtime Windows 11 has). Playing by a video's id or link needs no key. Headless offers the search alone. | off |
| YouTube API key | A YouTube Data API v3 key, for searching: in the [Google Cloud Console](https://console.cloud.google.com/), create a project, enable *YouTube Data API v3* (APIs & Services › Library), create an API key under Credentials and restrict it to that API, with no application restriction. A search costs 100 of the project's 10,000 free units a day (about 100 searches). Typed masked and saved encrypted (DPAPI); sent only to www.googleapis.com, in a header, never in a URL. `/keycopy` copies it; a plain `/profile reset` keeps it. | (none) |
| YouTube search max results | How many videos a search lists (1–20), for the model and in `/youtube`'s picker. The quota cost is the same whatever the count. | 8 |
| YouTube autoplay | Whether a played video starts at once, sound included, or waits cued for a play. | on |
| YouTube while speaking | What a playing video does while the app speaks a reply (first word to last) or listens to you (push-to-talk, or after the wake phrase): `pause` and play on after, `duck` to 15% and back up, or `none`. Only what the app did is undone: a video you paused stays paused. | `pause` |

### GitLib

| Setting | What it does | Default |
|---|---|---|
| GitLib tools | Offers the in-process git tools (status, log, show, diff, blame, branch, stage, commit, stash, discard, delete) over the working directory's repository. Off, git goes through the shell and `/gituser` does nothing. | off |
| GitLib diff max lines | Where a `gitlib_diff` patch is cut (20–5000). | 500 |
| GitLib log max commits | Commits `gitlib_log` returns by default (1–200). | 20 |
| GitLib email | The `user.email` `/gituser` writes into the repository's config. | (not set) |
| GitLib name | The `user.name` `/gituser` writes beside it. | (not set) |

### Options

| Setting | What it does | Default |
|---|---|---|
| $-mention enabled | `$` and part of a name lists the tools the next turn offers; a pick writes `$name`. | on |
| Tool collapse count | A run of more tool calls than this folds to one summary line (`▸ 🛠️ 7 tool calls — read_file ×3, …`); 0 never folds (0–100). | 2 |
| Code collapse count | A code block longer than this folds to its label (`▸ 📜 csharp · 57 lines`) once complete; while streaming, only its last lines show (0–100; 0 never folds). Needs *Transcript markdown*. | 20 |
| Show file diffs | A file edit (`patch_file`, `write_file`, `unc_patch`, `unc_write`) shows its diff under its line: `└ Added 3 lines, removed 1 line`, then the changed lines numbered with three of context, added ones on a green slab, removed ones on a red, coloured by the file's language. It folds with the tool run (the note and its diff count as one). The model's result is the same either way. | on |
| Diff max lines | The most rows of an edit's diff shown; past it `… 12 more lines` ends it (0–500; 0 = the header line alone). | 10 |
| Diff collapse count | An edit's diff of more rows than this (counted over the whole diff, past *Diff max lines* too) shows open while its tool run goes on, then folds to `▸ Added 3 lines, removed 1 line · 14 rows` once the run is over (0–500; 0 never folds). | 10 |

To see a folded block or diff, click it, press Ctrl+O, click **⤡** or use `/expand`.

## MCP servers (`/mcp`)

### Servers

One row per server in `mcp.json` (the profile's, then the home folder's; the profile's wins a name clash), with its transport and state: `connected · N tools`, `connecting`, `failed: …` or `off`. Enter or Space turns one on or off (Enter on a failed one retries). Below them: `edit profile mcp.json`, `edit global mcp.json`, `reload`, and any skipped entries.

### Tools

Every connected server's tools, as `<server>__<tool>`, with the server's description. Enter or Space switches one.

### Options

| Setting | What it does | Default |
|---|---|---|
| MCP servers | The master switch: on, every enabled server starts at launch (and on a profile switch) and its tools are offered. | off |
| MCP connect timeout (s) | How long a server gets to finish the handshake and list its tools (5–300). | 30 |

## System prompt (`/sys`)

A read-only view of exactly what the next reply sends.

### Prompt

The system prompt section by section, each with its status:

* **Persona** (built in, from the repo's `assets/prompts/persona.md`, or `persona.md`)
* **Operating rules** (built in, or `operata.md`; the reply-format and tool rules)
* **Project notes** (`NEON.md` / `AGENTS.md`)
* **Memory**
* **Skills** (the catalog)
* **Voice directive** (`vocalia.md`, when it has text; spoken turns only, always last)

### Tools

Every tool the reply may call, grouped as on `/tools` in alphabetical order (plus one group per MCP server, and Plan in plan mode), with the description the model reads. Switched-off tools are left out.
