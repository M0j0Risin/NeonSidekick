# Neon Sidekick
![License](https://img.shields.io/github/license/M0j0Risin/NeonSidekick)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512bd4?logo=dotnet)
![Windows](https://img.shields.io/badge/Windows-0078D6?style=flat&logo=windows&logoColor=white)


Neon Sidekick is an agentic terminal client for local LLMs, built on .NET 10. It draws on tools like Claude Code, Hermes Agent and Cline, combining my favourite features from them with a few ideas of my own. It's Windows-first and meant as a stable base for building agentic tools. Next on the roadmap: stronger coding capabilities and official macOS and Linux support.

<details>
  <summary>📷 Screenshots</summary>
  <div align="center">
    <table>
      <tr>
        <td style="padding: 10px;">
          <img src="assets/screenshots/screen_splash.png" alt="First Image" width="800">
        </td>
        <td style="padding: 10px;">
          <img src="assets/screenshots/screen_markdown.png" alt="Second Image" width="800">
        </td>
      </tr>
    </table>
  </div>
</details>

## Contents

- [Features](#features)
- [Getting started](#getting-started)
- [Settings & menus](#settings--menus)
- [Slash commands](#slash-commands)
- [Tools](#tools-2)
- [Environment variables](#environment-variables)
- [Components & Libraries](#components--libraries)

## Features
[↑ Back to top](#neon-sidekick)

### Core Architecture & UI
* Built on **.NET 10 NativeAOT** (compiled ahead of time to a single native program) for a fast start and a small footprint.
* **Rich terminal UI:** Spectre.Console menus, mouse input, Markdown and images.
* **Vision:** drag an image into the terminal, or paste one from the clipboard.
* **Auto-complete** for commands, files, folders, skills and tools.

### AI Connectivity & Context Management
* **Local server discovery:** *LLM server scan mode* finds most OpenAI-compatible servers on this machine or your network (LM Studio, vLLM, SGLang, Ollama, Unsloth…), or type a URL.
* **Embedded LLM:** no server? Install a Gemma 4 or Qwen model from `/settings` › Embedded. The app downloads it from Hugging Face and runs it on its own llama.cpp server (CUDA, Vulkan or CPU).
* **Context compaction** at a share you choose keeps the conversation inside the model's window.
* **Prompt transparency:** see exactly what the system prompt holds, and a summary of every compaction.
* **Persistent memory** you can edit, added to the context automatically.
* **Message queue:** type while the model is busy; messages go out in order.

### Profiles, Sessions & Skills
* **Profiles:** each has its own working directory, settings, persona, memory and sessions. A name starting with `_` is a temporary profile, left off the picker; `--profile <name>` opens one for a single launch.
* **Sessions:** resume, search and reflect on past conversations.
* **Skills** at four levels: global, profile, project, or machine (`.agents\skills`).
* **Self-learning:** a background reflection writes new skills and improves existing ones from your work.

### Built-In Tooling & Voice
* **Tools:** sandboxed files, shell (PowerShell/cmd/bash), scripts (PowerShell/Python/Node), Git, web search (DuckDuckGo/SearXNG) and browsing (HTTP/Chromium), questions on a pane, clock and timers, and `neon_help`, the app's own manual.
* **MCP servers** for more tools and data sources.
* **Voice input:** Whisper speech-to-text in-process, push-to-talk and a Vosk wake word.
* **Speech output:** Kokoro TTS in-process, or a Kokoro HTTP server.
* **Plan mode:** `/plan <requirement>` researches with read-only tools, asks what it needs and saves a plan to `.neon/plans/`. Nothing changes until you approve.

### Integrations
* **Obsidian:** search, read, write and link notes in your vault's files; no plugin, and Obsidian needn't be running.
* **SQL Server, Oracle, MySQL and MariaDB:** read-only queries and schema discovery over named connections. Each query must be a single `SELECT` and runs in a transaction that is always rolled back. Passwords are stored encrypted (DPAPI) or in Windows Credential Manager. No client libraries to install.
* **UNC shares and outside folders:** search, read and (when allowed) change files on `\\server\share` paths or local folders outside the working directory, as you or another Windows account, with no mapped drives.
* **Docker Desktop:** containers, logs, health, resource use, images, volumes, networks and compose projects through the engine API. With *Docker writes* on, the model can also start, stop, pull and clean up, each change asking first. `/docker` gives you the same on a pane.
* **Docker servers:** your vLLM or SGLang containers as `/server` choices, one running at a time.
* **Home Assistant:** lights, scenes, the TV, to-do lists and sensors, by room or name ("dim the den to 30%"). Anything outside a safe list asks first; `/ha` drives the house directly.
* **ComfyUI:** pictures from your own workflows (text-to-image, image-to-image, face swaps). The model writes prompts in each family's style, or `/imagine` sends yours as typed.
* **Claude:** the Claude API (your key) and your installed Claude Code (with this app's tools) as `/server` choices (`/settings` › Claude), `/claude` to message Claude Code, and `claude_advisor` for read-only advice (`/tools` › Claude). All off until you turn them on.
* **OpenAI:** the OpenAI API (your key) as a `/server` choice, off until you turn it on in `/settings` › OpenAI.
* **Bot chat:** `/botchat` lets your profiles talk to each other in their own personas and voices, optionally illustrated by ComfyUI.
* **Camera:** a USB webcam through Windows' Media Foundation. `/camera` takes a photo for your next message; the model can ask for one, `/botchat` bots can see you, and `/camera watch` sends a picture when the view changes.

## Getting started
[↑ Back to top](#neon-sidekick)

### Requirements
* **Windows x64** with an **AVX2** CPU (most Intel and AMD CPUs since 2015). Nothing else to install: no .NET runtime is needed.
* **Windows Terminal** is recommended; the interface is tuned for it.
* Optional:
  * Embedded LLM: an NVIDIA GPU with driver 580+ (CUDA) or any Vulkan GPU; otherwise it runs, slowly, on the CPU.
  * Headless web browsing: Edge, Chrome or Brave.
  * `/claude` and the Claude advisor: the Claude Code CLI.
  * Scripting beyond PowerShell: Python and Node.
  * Better web search: SearXNG (in Docker Desktop or elsewhere), or an MCP web-search server.

### Install
1. Download `NeonSidekick-v<version>-win-x64.zip` from the [GitHub Releases page](https://github.com/M0j0Risin/NeonSidekick/releases). The `.sha256` file beside it checks the download.
2. Unzip anywhere and run `NeonSidekick.exe`. Keep the DLLs, `runtimes\`, `espeak\` and `voices\` next to it.

### First launch
* Settings live in `%USERPROFILE%\.neonsidekick` (or `NEONSIDEKICK_HOME`), under the profile `default`.
* With no model installed and *LLM server scan mode* `disabled` (the default), the app opens **Settings › Embedded models**: pick a model to download and run it in-app, or press ESC twice to skip.
* Already running LM Studio, Ollama or vLLM? Set *LLM server scan mode* to `local`, `remote` or `both`, or use `/server <url>`. The startup picker lists what it finds: Enter saves your pick; ESC uses the first server for this run only.
* For Anthropic's models, turn on *Claude API* (or *Claude CLI server* for your Claude Code install) in `/settings` › Claude; for OpenAI's, *OpenAI API* in `/settings` › OpenAI.

### Voice (optional)
Speech output (`/tts`) and voice input (`/stt`) start off. Turning one on downloads its model the first time: Kokoro (about 326 MB) for speech; Whisper base (about 148 MB) and the small Silero detector for input; Vosk (about 41 MB) for the wake word.

### Useful first commands
| Command | What it does |
|---|---|
| `/help` | Lists the commands and keys. |
| `/settings` | Opens the settings. |
| `/server` | Picks the LLM server (found, embedded, Claude API, OpenAI API or Claude CLI). |
| `/model` | Picks the model on the current server. |
| `/tools` | Chooses which tools the model may use. |
| `/tts` / `/stt` | Turns speech output / voice input on or off. |

### Command-line options
Options that set something apply to this launch only. Both `--option value` and `--option=value` work.

| Option | What it does |
|---|---|
| `--url <url>` | Uses this LLM server. |
| `--model <id>` | Uses this model. |
| `--cwd <path>` | Uses this working directory. |
| `--profile <name>` | Opens this profile. |
| `--yolo` | Runs every shell command without asking. |
| `--no-police` | Lets shell commands touch paths outside the working directory. |
| `--log <path>` | Writes every diagnostic line to a file (`/log --file` opens it; `/log`'s window works without it). `{ts}` in the path becomes the start time, so each run gets its own log: `--log logs\NeonSidekick_{ts}.log` → `logs\NeonSidekick_20261003-142530.log`. |
| `--headless` | A plain text prompt over stdin/stdout, no TUI. See [HEADLESS.md](HEADLESS.md). |
| `--smoke` | Checks the native parts load, then exits. |
| `--audio-check` | Plays a test tone through the speech output, then exits. |
| `--voice-check` | Records up to 5 seconds from the microphone and transcribes it, then exits. |
| `--sql-check <connection>` | Proves the SQL tools against a connection of `sql.json`, then exits. |
| `--oracle-check <connection>` | The same for a connection of `oracle.json`. |
| `--mysql-check <connection>` | The same for a connection of `mysql.json`. |
| `--unc-check <share>` | The same for a share of `unc.json`. |
| `--docker-check` | The same for Docker Desktop's engine. |
| `--camera-check` | Opens the camera until the picture settles, reports its brightness and noise and encodes a test photo (nothing is saved; the light comes on briefly), then exits. |
| `--version` / `--help` | Prints the version or the help text. |

The `--*-check` modes keep nothing (`--sql-check` makes one temporary table inside a transaction to prove the rollback); see each tool's section for what they cover.

## Settings & menus
[↑ Back to top](#neon-sidekick)

### Navigation
* **Keyboard:** ←/→ switch tabs, ↑/↓ move, Enter edits or toggles, ESC closes.
* **Mouse:** a click moves the cursor; a double-click picks a row or tab (on the input line, selects a word). The × at the top right works like ESC, and a double-click outside an open pane closes it. A double-click on a picture in the transcript opens it in the [picture viewer](#picture-viewer).

### The input line
* It is always a full editor, even while a reply streams or `/botchat` runs: ←/→, Home/End, Delete; Shift+arrows, Ctrl+A or a drag to select; a double-click selects a word (letters, digits and `_`; all of a password field); Ctrl+C / Ctrl+X copy / cut; right-click or Alt+V pastes; ↑/↓ walk the history.
* `/`, `@`, `#`, `$`, `%`, `^` and `*` open their lists. The mention lists work inside a command's text too (`/loop infinite 1s append the time to @notes.txt`), except for `/speak`, `/view`, `/print` and `/pdf`, which complete their own path.
* Drag a picture from the ComfyUI picture strip or the transcript onto the input row to attach it, as if dropped from the desktop. The hint row reads **🖼️ drop on line** while you drag; letting go elsewhere attaches nothing.
* Enter during a reply queues the message. A draft left on the row survives the reply.
* ESC during a reply stops the speech, then closes an open list, then cancels the reply; it never clears your draft there (ESC at the idle line does).
* ESC twice on an empty line opens `/rewind` (the hint row prompts for the second press).

### Keyboard shortcuts
Each shortcut runs its command as if typed on its own; a draft on the row stays.

| Shortcut | Runs | During a reply |
|---|---|---|
| Ctrl+. | `/terminal` | at once |
| Ctrl+/ | `/settings` | opens over the reply |
| Ctrl+E | `/explore` | at once |
| Ctrl+F | `/perf` (performance bar on/off) | at once |
| Ctrl+H | `/help` | opens over the reply |
| Ctrl+M | `/model` | waits for the reply |
| Ctrl+P | `/profile` | waits for the reply |
| Ctrl+R | `/reasoning` | opens over the reply |
| Ctrl+S | `/server` | waits for the reply |
| Ctrl+T | `/tb` (toolbar on/off) | at once |
| Ctrl+U | `/usage` | opens over the reply |
| Ctrl+Y | `/sys` | opens over the reply |
| Ctrl+Alt+C | `/clear` | stops the reply first |
| Ctrl+Alt+N | `/new` | stops the reply first |
| Ctrl+Alt+P | `/splash` | stops the reply first |
| Ctrl+Alt+D | `/mcp` | opens over the reply |
| Ctrl+Alt+E | `/sessions` | opens over the reply |
| Ctrl+Alt+H | `/header` | at once |
| Ctrl+Alt+L | `/cmdlist` | opens over the reply |
| Ctrl+Alt+M | `/memory` | opens over the reply |
| Ctrl+Alt+O | `/police` | opens over the reply |
| Ctrl+Alt+S | `/skills` | opens over the reply |
| Ctrl+Alt+T | `/tools` | opens over the reply |
| Ctrl+Alt+G | `/log`: opens or closes the log window | at once |
| Ctrl+Alt+U | `/comfy view`: opens or closes the picture viewer | at once |
| Ctrl+Alt+V | `/camera live`: opens or closes the camera's window | at once |
| Ctrl+Alt+X ×2 | unloads the embedded model (see below) | at once |

* **Inside a pane** (a menu, `/help`, the folder picker, a value being typed), a shortcut closes every level of the pane and runs, so Ctrl+Alt+S in `/tools` opens `/skills`. A pane's own shortcut just closes it. Ctrl+E, Ctrl+., Ctrl+F, Ctrl+T, the three window chords and Ctrl+Alt+X act and leave the pane open.
* **Panes that ask you something** (a command's approval, `ask_user`, the plan's approval, a confirmation, *Did you mean /clear?*) ignore the shortcuts, so none can answer them by accident.
* **Window chords** close their window when pressed again (Ctrl+Alt+V only the window `/camera live` opened); the typed command only opens it or brings it forward.
* **Ctrl+Alt+X**, pressed twice within two seconds, unloads the embedded model at once and frees its memory, cancelling any reply, load or botchat using it. No server is connected until you pick one with `/server`; the saved *LLM URL* is kept, so the next start loads the model again. The first press only shows a reminder. With any other server it does nothing.
* An AltGr key that types a character on your layout still types it.

### Commands typed during a reply

| Behaviour | Commands |
|---|---|
| Open their pane over the reply | `/help`, `/settings`, `/tools`, `/mcp`, `/sys`, `/usage`, `/about`, `/memory`, `/queue`, `/sessions`, `/sessions title`, `/skills`, `/reasoning`, `/sampling`, `/cmdlist`, `/police`, `/cmdclear`, `/tree`, `/vault`, `/cmdcopy`, `/keycopy`, `/persona`, `/operata`, `/vocalia` |
| Run at once | `/ha`, `/camera live`, `/camera watch`, `/camera off`, `/camera list`, `/camera use`, `/tts`, `/stt`, `/wake`, `/interrupt`, `/perf`, `/tb`, `/reasoning <level>`, `/sampling <field> <value>`, `/queue clear`, `/copy`, `/remember`, `/explore`, `/terminal`, `/log`, `/timer`, `/expand`, `/collapse`, `/window`, `/cwd`, `/comfy view`, `/view <path>` |
| Stop the reply first | `/clear`, `/new`, `/splash`, `/rewind`, `/exit` |
| Everything else | Waits for the reply to end, queued behind earlier messages (*Queue cancel mode* applies) |

### Hint row, rule and toolbar

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
| the session name on the rule above the input row | renames it, like `/sessions title` (during `/botchat` the rule shows the cast instead) |

**⤡** at the left end of that rule appears when something can fold; a click does what Ctrl+O does (unfold all if anything is folded, else fold all).

**The toolbar** (*Show toolbar*) sits under the hint row. A glyph opens or closes its pane (or switches to it from another); a window glyph opens or closes its window. A tool switch sits on a dark slab while its setting is off. By default it shows 🛠️, the lock, 👮, 🐚, 📁, 🌐 and the working directory.

| Toolbar item | Shown | Opens |
|---|---|---|
| ⚙️ 🪪 🛠️ 🔌 🎓 🎭 💬 📊 | always | `/settings`, `/profile` (the profile picker), `/tools`, `/mcp`, `/skills`, `/sys`, `/sessions`, `/usage` |
| 💾 | while *Memory* is on | `/memory` |
| 🔒 / 🔓 | *Shell command policy* is `ask` / `yolo` (none under `off`) | `/cmdlist` |
| 👮 / 🥷 | *Shell police outside paths* is on / off, and the policy isn't `off` | `/police` |
| 🐚 | always; on the slab under `off` | `/tools shell`: the *Shell command policy* picker (yolo asks first) |
| 📁 🌐 ✴️ 🐳 💎 🪟 🔮 🐬 🔗 🏠 🎨 📸 🖨️ | always; on the slab while off | `/tools files`, `web`, `claude`, `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `unc`, `ha`, `comfy`, `camera`, `print`: that group's on/off page |
| 📄 | always | `/log`, the log window (Ctrl+Alt+G) |
| 📺 | always | `/camera live`, the camera's window (Ctrl+Alt+V) |
| 🎞️ | always | `/comfy view`, the picture viewer (Ctrl+Alt+U) |
| 📈 | always | `/perf`: hides or shows the performance bar |
| working directory (right edge) | always | `/cwd browse` |
| blank space | — | `/settings` |
| performance bar (anywhere on it) | *Show performance bar* has a meter checked | `/settings` |

### Panes
* `/settings`: the app, the embedded model, Docker, Claude and OpenAI servers, sessions, LLM, voice and `/botchat`
* `/skills`: agent skills and self-reflection
* `/tools`: the model's tools, and Claude (`/claude` and the advisor)
* `/mcp`: external MCP servers
* `/sys`: a read-only view of what the model is sent
* `/usage`: LLM usage statistics (tok/s, time to first token…)

Settings that an environment variable or flag can override for one launch are listed under [Environment variables](#environment-variables).

<details>
<summary><b>⚙️ App Settings (`/settings`)</b></summary>

#### General

| Setting | What it does | Default |
|---|---|---|
| Profile | Switches profile (each has its own settings, persona, memory, skills and sessions). | `default` |
| New profile mode | What `/profile add` copies: `basic` the settings and memories; `advanced` also the persona, operating-rules and voice-directive files. | `basic` |
| Working directory (cwd) | The folder the file and GitLib tools work in; empty is the profile's `files\` folder. The row opens the `/cwd browse` folder picker; `/cwd <path>` sets one by hand. | profile's `files\` |
| Memory | Offers `save_memory` / `recall_memory` and opens every conversation with what is remembered. | on |
| Queue messages | Lists messages sent during a reply (a count, and `/queue`). Off, they are still sent when the reply ends, just not listed. | on |
| Queue cancel mode | What a cancelled reply does with the queue: `hold` keeps it until your next message, `drain` sends the next one at once, `empty` drops them all. | `empty` |
| Keep command history | Saves the ↑/↓ history (newest 1,000 lines, no collapsed pastes or pictures) in `sessions.db`. Off deletes it at the next profile load; `/cmdclear` empties it either way. | on |
| Command typo intercept | A command name without its slash (`clear`) or with extra ones (`//profile work`) asks *Did you mean /clear?* first. A bare `//` is still `/settings`. | on |
| Hide /exit autocomplete | Leaves `/exit` out of the `/` list; typing it in full still works. | on |
| Transcript markdown | Renders replies as Markdown, with code fences highlighted (C#, JS/TS, Python, Bash, PowerShell, JSON, YAML, TOML/INI, SQL, C/C++, Java, Kotlin, Go, Rust, CSS, XML/HTML, diff). | on |
| Paste preview lines | How many lines of a long paste show dimmed under its `[Pasted text #n]` placeholder (0–200). | 25 |
| Show image thumbnails | Draws a small thumbnail of each picture you send, each one a tool fetches or makes, and each `/botchat` picture. `/view` and `/imagine` always draw theirs. | on |
| Image thumbnail size | `tiny` (32×8), `small` (48×12), `medium` (64×16), `large` (80×20), `xlarge` (96×24) columns × rows, or `fullsize` (as large as the transcript allows). | `small` |
| Copy user prompt | `/copy` includes your prompt above the reply. | on |
| Theme | `abyssal`, `cyberpunk`, `grid`, `mainframe`, `netrunner`, `noir`, `nostromo`, `replicant`, `synthwave`, `vaporwave`, and your own (see [Custom themes](#custom-themes)), sorted by name. A wide enough window previews the highlighted theme (on the terminal's own background when *Themed background* is off); a typed letter jumps to the next theme starting with it. | `synthwave` |
| Themed background | Gives the terminal the theme's background while the app runs. Off, the terminal profile's own background (colour, acrylic or picture) stays. | on |
| Themed external windows | The picture viewer, the camera's window and the log window wear the theme (dark title bar and theme colours). Off, they stay black. | on |
| Welcome splash | Pictures under the banner at startup until your first line: `fullsize`, `tiled` or `disabled`. See Welcome splash below. | `fullsize` |
| Show header | Shows the banner at startup and after `/clear`, `/splash`, `/theme` and a profile switch. `/header` and Ctrl+Alt+H flip it, shown at the next clear. | on |
| Working directory in header | Prints the working directory at the right of the banner's title line. | off |
| Show toolbar | A checklist of the toolbar's items: every glyph and the working-directory path (📂). **A** / **N** / **D** pick all, none or the default seven; none hides the row. | Tools, Shell allowed commands, Shell police, Shell, Files, Web, path (7 of 30) |
| Show performance bar | A checklist of the bar's meters, updated each second: **CPU**, **RAM**, **GPU**, **VRAM**, **NET** (share of link speed), **NET↓** and **NET↑** (rates). **A** / **N** / **D** pick all, none or the default four; none hides the bar. The title row picks the look: **text** (T), **gauge** (G), **spark** (S, the last ten seconds) or **led** (L). See Performance bar below. | CPU, RAM, GPU, VRAM, `led` |
| Menus max height | How much of the window a menu or info pane may take: `half-screen`, `three-quarters` or `full-screen` (all but one row). Longer lists scroll; every tab keeps the tallest tab's height. | `full-screen` |
| Draft editor | The program `/draft` opens with (`code --wait`, `notepad`…). Empty uses Windows' `.txt` editor. | (default .txt editor) |
| Image viewer | Where a double-clicked picture opens: empty for the built-in viewer, `system` for Windows' app for the file type, or a command the path is appended to (`mspaint`, `"C:\Program Files\GIMP 3\bin\gimp-3.exe"`). | (built-in viewer) |

##### Welcome splash

* `fullsize` fills the screen with one picture; ←/→ step through the set. Delete twice on an empty line moves one of the profile's own pictures to the folder's `.trash` (move it back to restore it).
* `tiled` shows thumbnails at *Image thumbnail size*; ←/→ page and a double-click opens one.
* Pictures in the profile's `splash\` folder replace the built-in ones.
* `/splash` shows the splash whatever the setting (tiled under `tiled`, otherwise one picture). A theme change restarts the screen like `/clear`.

##### Performance bar

* Values turn amber from 60 % and red from 85 %. A meter the machine can't read (no GPU, no network) is left out.
* An NVIDIA GPU is read through its driver (NVML); any other through Windows' GPU counters, for the card with the most memory.
* The network meters follow the busiest adapter that is up and has a gateway, so a VPN over Wi-Fi isn't counted twice. NET↓ and NET↑ show bits per second (`850K`, `12.4M`, `1.2G`); their gauges show the share of the link.
* `/perf` or the toolbar's 📈 hides the bar, or brings it back with the meters it last had.

#### Embedded

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
| Gemma 4 E2B Uncensored (`gemma-4-e2b-uncensored`, HauhauCS Aggressive) | Q4_K_P | 4.4 GB | — |
| Gemma 4 E4B (`gemma-4-e4b`, Unsloth) | UD-Q4_K_XL | 6.2 GB | MTP (file) |
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
| Embedded models | The catalog. Enter on an installed model offers *Use now* and *Remove*; on any other, *Install* (and *Remove* for a partial download). Remove asks first, stops its download and any server running it, and deletes its folder, clearing the LLM URL and model if they named it. Using or installing closes the settings and connects. The buttons are listed below. | |
| Embedded HF download type | `parallel` fetches each file over 8 connections (about twice as fast); `single` uses one. A download under way keeps its mode. | `parallel` |
| Embedded backend | The llama.cpp build: `auto` (CUDA with NVIDIA driver 580+, else Vulkan, else CPU), `cuda`, `vulkan` or `cpu`. The row shows what `auto` picked and why. | `auto` |
| Embedded context size | The context window in tokens. 0 (**fit**) is the largest that fits beside the model with every layer on the GPU, within *Embedded VRAM budget*: from the model's own window (128K for E2B/E4B, 256K for the rest) down to 4,096. Otherwise 512–262,144, never shrunk. | 0 (fit) |
| Embedded GPU layers | Layers on the GPU: `auto` (as many as free VRAM holds), `all`, or a number (0 runs on the CPU). | `auto` |
| Embedded VRAM budget | How much of the GPU's memory the server may fill: `off` (llama.cpp leaves 1 GiB free per GPU) or 50–99 % of the card with the most memory. It shrinks the context first (with context 0), then moves layers to the CPU (with GPU layers `auto`); a fixed context too big for it can only push layers out, which slows replies sharply. Measured at server start; CUDA and Vulkan only; a change restarts the server. | 91 % |
| Embedded VRAM only | Keeps the whole model in GPU memory: every layer on the GPU (with context 0 the context shrinks instead). If any of it lands in system RAM (layers on the CPU, a failed allocation, or the NVIDIA driver's shared-memory fallback), the server stops and the connect says what to lower. Refused on the CPU backend, on Vulkan with an integrated GPU, and when llama-server doesn't report where the layers went. | on |
| Embedded vision | Loads the vision projector so the model reads images (about 1 GB more for most models, under 200 MB for the 12Bs, 2 GB for Muse Glimmer). Off, images sent to it are refused. | on |
| Embedded drafter | Speeds up replies with multi-token prediction: the model drafts tokens ahead and checks them, so the text is the same, just faster (see the Drafter column). Off, no drafter is loaded or downloaded. Turn it off if a model misbehaves with it. | on |

**Catalog buttons** (on the title row of *Embedded models*; each group combines with the others and starts cleared at each visit):
* **8GB**, **16GB**, **32GB** (1, 2, 3): models up to that size (weights, projector and drafter). One at a time; press the lit one to clear it.
* **installed** (I) / **uninstalled** (U): one at a time; a paused download counts as uninstalled.
* **drafter** (D): models with a drafter.
* **sort** (S): name order or size order (smallest first).
* **uncensored** (X): shows only the uncensored builds, which are hidden otherwise. It starts lit when the model in use is one.
* `/server` and the startup picker have the size, drafter, sort and uncensored buttons for their embedded rows.

**Notes**
* **NVFP4 builds** need the CUDA backend on an NVIDIA Blackwell GPU (RTX 50 series or newer); elsewhere they are slow or don't load.
* **Downloads** are checked against Hugging Face's SHA-256 for each file. A paused or interrupted download resumes when you pick the model again; the drive needs room for the rest plus 1 GB.
* **llama.cpp** itself downloads on first start (build `b11258`: 577 MB for CUDA, 33 MB for Vulkan, 19 MB for CPU). If `auto` picked CUDA and it won't start, the app tries Vulkan.
* **The server** listens on `127.0.0.1` only, on a random port with a fresh key. It restarts only when a setting it depends on changes, and stops when you pick another server, turn *Embedded servers enabled* off, or quit (Windows stops it if the app crashes). Only one runs at a time, apart from a `multi-server` botchat's extras (see Botchat).
* **Files:** models in `models\llm\<id>\`, llama.cpp in `llama\`, both under the home folder; `/about` shows them.
* **Sampling** follows each model card (Gemma 4: temperature 1.0, top-p 0.95, top-k 64; HauhauCS's QAT Balanced builds: 0.6, 0.9, 64; Qwen: 1.0, 0.95, 20; Muse Glimmer: 1.0, 0.95, 64); `/sampling` overrides it.
* **Memory:** the 12Bs need about 8 GB of VRAM plus the context at Q4 (10 and 12 GB at Q5 and Q6, 25 GB at BF16). The larger models need roughly their download size plus the context, or a partial CPU offload. E2B and E4B fit in less. The 26B A4B and Qwen3.6 35B A3B are mixture-of-experts (4B and 3B active per token), so they run faster than their size suggests.
* **Never in system RAM:** besides *Embedded VRAM only*, you can tell the NVIDIA driver never to fall back for llama.cpp: NVIDIA Control Panel › *Manage 3D settings* › *Program Settings*, add `llama-server.exe` (under `llama\` in the home folder) and set *CUDA - Sysmem Fallback Policy* to *Prefer No Sysmem Fallback*.
* Windows x64 only. The small models (E2B, E4B) call tools less reliably.

#### Docker

Your own LLM containers (vLLM, SGLang, anything serving `/v1/models`) as `/server` choices, one running at a time. See [Docker servers](#docker-servers).

| Setting | What it does | Default |
|---|---|---|
| Docker servers enabled | Offers the chosen containers in `/server`. Turning it off while one is in use stops it at the reconnect. | off |
| Docker server containers | A checklist of every container the engine lists, with state, image and ports. A ticked name the engine no longer lists is kept. | none |
| Docker server stop timeout (s) | How long a stopping container gets before the engine kills it (0–120). | 30 |
| Docker server post-stop delay (s) | The wait between stopping the others and starting this one, so the GPU's memory frees up (0–60). | 2 |
| Docker server ready timeout (s) | How long a started container may take to answer on `/v1/models` (30–3600). Past it, the switch fails and the container keeps running. | 900 |
| Docker server stop on exit | Stops the container in use when the app exits (the window's close button too). | off |

#### Claude

The Claude API and the Claude CLI as `/server` choices. `/claude` and `claude_advisor` are on `/tools` › Claude.

| Setting | What it does | Default |
|---|---|---|
| Claude API | Offers the Claude API on `/server` while a key is set. Off (or keyless) while it is the saved URL, the app scans as if the URL were blank. | off |
| Claude API key | Your Anthropic key (`sk-ant-…`), typed masked and saved encrypted (DPAPI). An empty entry clears it. | (none) |
| Claude API max tokens | The output cap per request, thinking included (1,024–128,000). | 32,000 |
| Claude API prompt caching | Marks the tools, system prompt and conversation for Anthropic's prompt cache, so each request re-reads the last one's content cheaply. | on |
| Claude CLI server | Offers Claude Code on `/server` while it is found (*Claude executable* on `/tools` › Claude, or the PATH). Off (or missing) while it is the saved URL, the app scans as if the URL were blank, and a running Claude CLI stops. | off |

**The Claude API** appears in `/server` as a **Claude API** row. Picking it sets *LLM URL* to `https://api.anthropic.com/v1`, then offers the account's models and the reasoning level.

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

#### OpenAI

The OpenAI API as a `/server` choice, over the Responses API (stateless: nothing is stored on OpenAI's side).

| Setting | What it does | Default |
|---|---|---|
| OpenAI API | Offers the OpenAI API on `/server` while a key is set. Off (or keyless) while it is the saved URL, the app scans as if the URL were blank. | off |
| OpenAI API key | Your OpenAI key (`sk-…`), typed masked and saved encrypted (DPAPI). An empty entry clears it. | (none) |
| OpenAI API max tokens | The output cap per request, reasoning included (`max_output_tokens`). 0 sends none: the model's own limit. Else 1,024–128,000. | the model's own |
| OpenAI API organization | The `OpenAI-Organization` header, for an account in several organizations. Empty sends none. | (none) |
| OpenAI API project | The `OpenAI-Project` header. Empty sends none (a project key names its project already). | (none) |

**The OpenAI API** appears in `/server` as an **OpenAI API** row, after the Claude API's. Picking it sets *LLM URL* to `https://api.openai.com/v1`, then offers the account's chat models, newest first (not the audio, realtime, image, embedding, moderation, `-pro` or `codex` ones), and the reasoning level.

* Every message is billed to the key's account. The key goes only to api.openai.com: never to a local server or the Claude API, and the *LLM API key* never goes to OpenAI. A saved `https://api.openai.com` URL now needs this switch and key.
* Changing one of these settings reconnects when `/settings` closes.
* *LLM reasoning* is sent as `reasoning.effort`, shaped per model: `none` is `minimal` on GPT-5 and `low` where a model has neither (o-series, GPT-6 Astra and Sol); `xhigh` is `high` before GPT-5.2; GPT-4.1 and GPT-4o get none. The thinking shows as OpenAI's summary of it, and its encrypted form goes back within a tool loop, so the model keeps its reasoning across calls.
* Every model takes tools at every level here; over Chat Completions, GPT-5.4 and newer refuse tools beside any reasoning, which is why this is the Responses API.
* *LLM sampling* is not sent (the reasoning models refuse a temperature), nor is another server's thinking.
* The context window comes from the app's model table (the model list names none); *LLM context length* overrides it. `/usage` adds *Cache* and *Cost* rows: an estimate at OpenAI's list prices of 2026-10-03, long-context prices past 272K prompt tokens, cache writes from GPT-5.6 on.

#### LLM

| Setting | What it does | Default |
|---|---|---|
| LLM server scan mode | Where to look for a server while *LLM URL* is blank: `local` (the usual ports here), `remote` (the same ports across the LAN), `both`, or `disabled`. | `disabled` |
| LLM URL | The server's OpenAI-compatible base URL (`http://127.0.0.1:1234/v1`), `embedded`, or `docker:<container>`. `/server` fills it in. Empty: the app scans and, at startup, lets you pick a server, model and reasoning level and saves all three (ESC takes the first server without saving). | (none) |
| LLM model | The model id. Empty takes the first the server lists; a Docker server serving one model saves its id here when it connects; `/model` picks one. | (first listed) |
| LLM API key | The bearer token the server expects (`empty` for none). Saved encrypted for your Windows account (DPAPI) and shown as `(set, encrypted)`. | `empty` |
| LLM reasoning | How hard the model thinks: `none`, `low`, `medium`, `high` or `xhigh`. `/reasoning` opens the same list. | `none` |
| LLM show thinking | Streams a reasoning model's thinking as a dim block (its last five lines), folded to `▸ 💭 thought for 4.2s` when the answer starts. Click it, Ctrl+O or `/expand` to see it again. Needs *Transcript markdown*. Thinking is never spoken or logged; only `/copy --thinking` copies it. | on |
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

#### Sampling per model

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
* The Claude API and the OpenAI API are not affected.

#### TTS

Speech output sets up in the background (🔈 on the hint row); replies are text-only until it's ready.

| Setting | What it does | Default |
|---|---|---|
| TTS output | Reads replies aloud (`/tts`). Code blocks and tables are never read, not even by `/speak`. | off |
| TTS source | `in-process` runs Kokoro inside the app (downloaded on first use); `http` uses a Kokoro-FastAPI server. | `in-process` |
| TTS HTTP URL | The Kokoro-FastAPI base URL, for `http`. | `http://localhost:8880/v1` |
| TTS voice preview | The voice and preset pickers speak the highlighted voice. | on |
| TTS voice preset | Sets the voice, second voice, mix and speed in one go; the row shows the matching preset or `(custom)`. Built in: `amanda`, `neon`, `richard`, `hunter`, `larry`, `jack`, `willow`. See [Voice presets](#voice-presets). | `neon` |
| TTS voice | The Kokoro voice. | `af_heart` |
| TTS voice 2 | A second voice blended in; `(none)` for the first alone. | `am_eric` |
| TTS voice mix | The first voice's share of the blend, 0–100 %. | 80 |
| TTS speed | Speaking speed, 0.5–2.0. | 1.2 |

#### STT

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
| STT push-to-talk key | `F1`–`F10`, `Insert`, `Home`, `End`, `PageUp` or `PageDown`. | `F4` |
| STT whisper model | `ggml-tiny.en.bin`, `ggml-base.en.bin` or `ggml-small.en.bin` (downloaded on first use). | `ggml-base.en.bin` |
| STT vosk model | The wake-word model: `vosk-model-small-en-us-0.15`, `vosk-model-en-us-0.22-lgraph` or `vosk-model-small-en-in-0.4`. | `vosk-model-small-en-us-0.15` |

#### Sessions

| Setting | What it does | Default |
|---|---|---|
| Session logging | Saves every turn to the profile's `sessions.db` for `/sessions` to list, search and restore. | on |
| Session retention (days) | Deletes sessions older than this at startup (0–3650; 0 = keep forever). | 0 |
| Session naming mode | `model-written` asks the model for a title after the first turn; `first-line` uses your first line. | `model-written` |
| Session show name | Which titles show on the rule above the input row: `all-names`, `model-written` (a model-written or typed name only) or `none`. | `all-names` |
| Session tool | Offers `session_manager`, to search, list and read earlier sessions (never restore or purge). | on |
| Session search max results | Sessions one search or list returns (1–20). | 10 |
| Session save thinking | Saves each reply's thinking, so a resumed session can send it back under *LLM preserve thinking*. | off |

#### Botchat

| Setting | What it does | Default |
|---|---|---|
| Botchat LLM mode | `single`: every bot uses this profile's server, model and reasoning. `multi`: each bot uses its own profile's (a blank URL borrows this one's). A bot whose server doesn't answer sits the chat out. Read when a chat starts or resumes. | `single` |
| Botchat multi-embedded | Under `multi`, for bots wanting a different embedded model from the one running. `parent-server`: they share the running model, with a warning. `multi-server`: one extra `llama-server` per model, started in turn under that bot's profile's Embedded settings so each fits in what's left. Bots on one model share its server. | `parent-server` |
| Botchat multi-embedded kill | Stops `multi-server`'s extra servers when the chat ends. Off, they run until `/botchat --kill` or you quit, and a later chat reuses them. | on |
| Botchat images enabled | Adds pictures to `/botchat`; needs *ComfyUI tools* and a *ComfyUI URL*. Off, the chat is talk only, with no tools (except `load_skill` under *Botchat skills enabled*). | off |
| Botchat image mode | `automatic`: the app writes a prompt from each reply and draws it. `autonomous`: the bots get `generate_image` and draw when they choose. See Botchat pictures. | `automatic` |
| Botchat txt2img workflow | The text → image workflow; blank means no new pictures. | (none) |
| Botchat img2img workflow | The image → image workflow for reworks; blank means none. | (none) |
| Botchat img2img mode | Which pictures a rework may start from: the `latest`, or any in `chat-history` (the last 8). | `latest` |
| Botchat image async | On: the next bot speaks while a picture renders. Off: each reply waits for its picture and appears with it. | on |
| Botchat non-TTS delay | A reading pause after each reply when *TTS output* is off (0–30 s). A line typed meanwhile joins the chat; ESC ends it. | 5 |
| Botchat skills enabled | Offers every bot `load_skill` over the starting profile's, the global and (with *Use external skills*) the external skills, never a bot's own profile's. The `automatic` prompt writer gets them too. Needs *Agent skills*; switching `load_skill` off in `/tools` turns this off. | off |
| Botchat preloaded skills | Skills loaded for the chat, so no `load_skill` call is needed: tick them (**A** / **N**), or name one as a whole word in the topic (`/botchat use pony-prompts for the pictures`). Each brings up to 64,000 characters of its bundled text files; any left out are listed by name and readable only with *Botchat skills enabled*. Needs *Agent skills*. | none |
| Botchat skill mode | Who gets the preloaded skills: `prompt-writer-and-bots` or `prompt-writer-only`. | `prompt-writer-and-bots` |
| Botchat vision enabled | Shows each bot, on its turn, the newest 4 pictures since it last spoke (not its own), captioned with whose they are. Needs models that read images. Not kept for `--resume` or the session. | off |
| Botchat camera | The bots see you: each turn gets a fresh camera picture, captioned as a photo of you. Kept in memory only. Needs models that read images; a failing camera is one warning. Read when a chat starts. | off |

##### Botchat pictures

* Either workflow can be any installed one of its kind, ticked in *ComfyUI workflows offered* or not. Pictures are drawn at *Image thumbnail size*.
* **`automatic`:** after each reply, the model writes an image prompt in the workflow family's style and the app draws it. The bots get no tool.
* **`autonomous`:** the bots get `generate_image`, limited to the two botchat workflows. A reply that describes a picture the bot never drew gets it drawn anyway. A tool call written out as text (`<tool_call>` markup included) runs as a real call and is never shown or spoken.
* **Reworks:** once there's a picture and an img2img workflow, the next prompt's writer chooses between a new picture and a rework (`REWORK` or `REWORK n` in `automatic`; an `image` path in `autonomous`). A picture still rendering can't be reworked.
* **Async on:** 🖼️ (🎨 for a rework) shows on the hint row while a picture renders, with a count when several are pending. Each picture is labelled with whose reply it shows. Without speech, pictures go to ComfyUI one at a time.
* **Async off:** ESC on a held reply cuts that bot short; ESC under the picture's spinner skips just that picture.
* **Skills:** with *Botchat skills enabled*, the `automatic` prompt writer may load a skill first, so "use the pony-prompts skill for pictures" holds from the first picture. *Botchat preloaded skills* doesn't depend on the model choosing to load it.

</details>

<details>
<summary><b>🎓 Skills Settings (`/skills`)</b></summary>

#### Offered

The loaded skills with their scope (`profile`, `global` or `external`) and description, then any shadowed duplicates and skipped folders (with the reason). Enter on a skill can move it between the profile and global folders, rename it (lower-case-with-hyphens; a taken name is refused), edit its `SKILL.md` in your editor, revert it (see Skill history), or delete it after a confirmation.

#### Reflection

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

#### Options

| Setting | What it does | Default |
|---|---|---|
| Agent skills | Lists the skills in the prompt and offers `load_skill` and `skill_editor`. Off also stops the project file being read. | on |
| Use external skills (.agents\skills) | Also reads `%USERPROFILE%\.agents\skills`, read-only. | off |
| Project file | Reads `NEON.md` (or `AGENTS.md`) in the working directory into the prompt as project notes. Needs *Agent skills*. | on |
| Skill compact mode | `protected` keeps a loaded skill's instructions through a prune; `unprotected` prunes them like any tool result. | `protected` |
| #-mention enabled | `#` and part of a name on the input line lists the skills; a pick writes `#name`. | on |

#### Installing skills

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

#### Skill records and purging unused skills

`skills.db` in the home folder records every global and profile skill: its folder, scope, when it was created, last modified and last used (loaded or preloaded), and a category (empty for now).

- `skill_editor`, reflections, `/skills add` and the `/skills` pane update the record as they act.
- At startup and every profile load, the app reconciles the folders: a new folder gets a record (dated from its `SKILL.md`), a newer `SKILL.md` moves the modified date, and a record whose folder is gone is removed.
- External skills (`.agents\skills`) are not recorded and never purged.

`/skills purge list <age>` lists skills unused for that long; `/skills purge commit <age>` deletes them (folder and record) after a yes/no. The age is days (`30`) or a duration (`12h`, `90m`, `1d 6h`). A never-used skill counts from its last change. Only global skills and the loaded profile's are considered, and a global skill used in any profile counts as used. There is no `purge all`.

#### Skill history

`skills.db` also keeps each skill's history, which the reflection reads:

- **Changes:** each one is logged with who made it (the model, a reflection, an install or a revert). A newer `SKILL.md` the app didn't write counts as a hand edit.
- **Use:** each turn that loaded a skill is logged with the tool errors that came *after* the load.
- **The reflection's view:** each skill carries a usage line (`loaded 12 times across 6 sessions, 3 followed by errors; …; edited by hand …; installed from owner/repo`), also the caption of its `/skills` page. The reflection is asked to fix skills often followed by errors, keep your wording in hand-edited ones, and prefer a companion skill over changing an installed one.
- **Earlier versions:** before overwriting `SKILL.md` or a supporting file, the old text is kept (the last 10 per skill, up to 256 KB each). `/skills revert <name>` restores the newest, one step further back each time. It is refused after a hand edit, which was never kept. An update through `/skills add` keeps only the old `SKILL.md`.
- The history lives in `skills.db`, so it survives purged sessions, renamed skills and *Session logging* off.

</details>

<details>
<summary><b>🛠️ Tools Settings (`/tools`)</b></summary>

#### Offered

Every tool, grouped, with the description the model reads. Enter or Space switches one; a group whose switch is off is dim. The Help group (`neon_help`) has no switch of its own, so turn it off here. In a new profile, `gitlib_delete`, `zip`, `unzip`, `unc_delete`, `docker_remove` and `docker_prune` start off.

#### Web

| Setting | What it does | Default |
|---|---|---|
| Web tools | Offers `web_search`, `web_fetch`, `open_url` and `download_file`. | off |
| Web browser mode | `default` uses HTTP and falls back to a headless browser for a blocked or empty page; `httpclient` never falls back; `chromium` uses the browser for every page. | `default` |
| Web browser path | The Chromium-based browser for headless fetches and for making PDFs; empty finds Edge, Chrome or Brave. | (auto) |
| Web browser network mode | Where a fetch may reach: `internet` (public addresses), `local_area_network` (this machine and the LAN) or `both`. | `internet` |
| Web search method | `duckduckgo` (built in) or `searxng`. | `duckduckgo` |
| Web SearXNG URL | The SearXNG instance, for `searxng`. | (not set) |
| Web search max results | Hits per search (1–20). | 20 |
| Web download max (MB) | The largest file `download_file` saves (1–102400), streamed to disk. A download silent for 60 s is dropped, and no partial file stays. | 50 |

#### Files

| Setting | What it does | Default |
|---|---|---|
| File tools | Offers the sandboxed file tools (read, write, patch, search, move, copy, zip, view_image…), which reach only the working directory (a junction or symlink in it that leads outside is refused; deleting or moving the link itself is allowed, its target untouched). | off |
| File /tree max length | Entries `/tree` prints before it stops (1–10000). | 500 |
| File /tree show sizes | `/tree` shows file sizes. | on |
| File @-mention folder mode | Picking a folder from the `@` list: `folder-remain` opens it in the list; `folder-apply` writes `@folder/` and closes. | `folder-remain` |
| File browser/tree mode | `default` hides hidden, system and dot entries in the folder browsers and `/tree`; `show-hidden` lists them (`/tree` still leaves out `.git`). | `default` |
| File view image max (per call) | Pictures one `view_image` call may load (1–100). | 10 |
| File search max results | The most rows one `search_files` or `unc_search` call returns, whatever its `limit` (1–5000). Without `limit`: 50 hits, 200 entries, 100 names or 10 recent files. | 200 |

#### Shell

| Setting | What it does | Default |
|---|---|---|
| Shell command policy | How the model may run shell commands: `off` (no shell tools), `ask` (anything not on the allowed list goes to the approval pane; refused with no pane) or `yolo` (everything runs). See Shell guards. | `off` |
| Shell allowed commands | Command prefixes allowed for good (`git status`, `dotnet build`, `python`). Enter removes one; the pane's *Allow … always* adds one. The ask and yolo buttons (A, Y) switch the policy. `/cmdlist` opens it; `/cmdcopy` copies it to another profile. | none |
| Shell police outside paths | Refuses a command, script or process input naming a path outside the working directory, before it runs or asks. Turning it off asks first, and also stops the forbidden strings; `/police` opens it. See Shell guards. | on |
| Shell police forbidden strings | Strings the police refuses outright in a command, script or process input, case and spacing ignored. Enforced only while *Shell police outside paths* is on. The top row adds one, Enter removes one; `/police`'s strings button (S) opens it too. See Shell guards. | none |
| Shell prefer native tools | Steers the model to the app's own tools: a lone shell command one of them covers is sent back (once a turn). See Shell guards. | on |
| Shell default | The shell when a call names none: `powershell` (pwsh if installed, else 5.1), `cmd`, or `bash` (Git Bash). | `powershell` |
| Shell timeout (s) | How long a foreground command without its own `timeout` may run (1–3600). | 180 |
| Shell foreground cap (s) | The longest any foreground command may run (10–3600). | 600 |
| Shell output max chars | Output one result carries (2000–500000). Past that, the start and end are kept and the whole text goes to `.shell\<id>.log` in the working directory. | 30000 |
| Shell code languages | What `execute_code` may run: `powershell`, `python`, `node` (each only when its interpreter is found). **A** turns all on; the last one can't be turned off. | all three |
| Shell code timeout (s) | How long a script without its own `timeout` may run (1–3600). | 300 |
| Shell tool bridge | Lets an `execute_code` script call the app's other tools through its `neon_tools` module (a loopback socket with a per-run token). | off |
| Shell tool bridge max calls | Tool calls one script may make through the bridge (1–500). | 50 |

##### Shell guards

* **Approval (`ask`):** the pane offers Deny, Allow once, Allow the prefixes for this session, or Allow them always. A prefix is the program plus its subcommand for git, dotnet, npm, pip, gh, docker, cargo, go, winget, net and the like, otherwise the program alone. `--yolo` and `NEONSIDEKICK_COMMAND_POLICY` override the policy for one launch.
* **Path police** reads the text of a `run_command` line, an `execute_code` script, or `process` input, and refuses an absolute path outside the working directory (`C:\…`, a UNC share, `/etc/hosts`), a `..` that climbs out, `~`, or a folder variable (`%USERPROFILE%`, `$env:TEMP`, `$HOME`, `Path.home()`…). The model gets `Error: outside the working directory: '…'` and the transcript shows 👮.
  * It also refuses a path inside a switch or a URL (`-out:C:\x`, `@C:\x.rsp`, `7z -oC:\x`, `-I..\x`, `file:///C:/x`), a bare drive (`C:`, `cd /d E:`), a `cd` to a folder at the drive's root (`cd /etc`; in bash `/c` is the C drive), a bare `cd` or `Set-Location` in PowerShell or bash, also with only options or a redirect (`cd -P`, `cd >/dev/null`; it goes home there; cmd's only prints the folder), and a junction or symlink in the working directory that leads outside.
  * A quoted path with a space that stays inside passes (`"D:\My Projects\app\a.txt"`), and a `cd` earlier in the line moves where later relative paths start (`cd src && type ..\README.md`) when the next command surely runs there: after `&&` or `;` (cmd's `&` too), outside a subshell, to a folder that exists. After a pipe, `||` or a `cd` that may fail, a later path must stay inside from both folders.
  * It reads text, not what runs: a computed path isn't seen, and a cmd switch (`dir /s`), a URL or a device (`>nul`, `/dev/null`) isn't a path.
  * `--no-police` and `NEONSIDEKICK_SHELL_POLICE` override it; `--yolo` never does.
* **Forbidden strings:** while the path police is on, a `run_command` line, `execute_code` script or `process` input that contains a string on *Shell police forbidden strings* is refused before it runs or asks, even under `--yolo`. Case is ignored and any run of spaces, tabs or line breaks counts as one space (`rm -rf` catches `RM   -RF`). The model gets `Error: forbidden by the shell police — the user does not allow this command or script…`, never the string; the 👮 line shows it to you (`forbidden string 'rm -rf' — not run`) and the log names it. It reads text, so a command built in pieces gets past it: a tripwire, not a sandbox.
* **Prefer native tools:** the operating rules name the tools offered that turn and the commands each replaces:
  * `cat`/`type`/`Get-Content`/`dir`/`ls`/`grep` → `read_file`/`search_files`
  * `git status`/`log`/`diff`/`add`/`commit` → the GitLib tools
  * `curl`/`Invoke-WebRequest` → `web_fetch`
  * `sqlcmd` → `sql_query`, `sqlplus` → `oracle_query`, `mysql` / `mariadb` → `mysql_query`
  * `net use` / `net share` / `net view` / `Get-SmbShare` / `Get-SmbMapping` → `unc_shares`; `dir \\server`, `copy \\server` → the UNC tools

  A lone command such a tool covers comes back as `Not run: 'cat' has a tool of its own — call read_file instead…`, once a turn (sent again, it goes to the pane). Pipes and compound lines, commands with no tool (`git push`) and tools switched off are never sent back.

#### Ask

| Setting | What it does | Default |
|---|---|---|
| Ask user | Offers `ask_user`: multiple-choice questions on the pane. | on |
| Ask max questions | Questions per call (1–10). | 10 |
| Ask max choices per question | Options per question (2–15). | 10 |

#### Camera

| Setting | What it does | Default |
|---|---|---|
| Camera tool | Offers `camera_capture`, so the model can ask you for a photo. Never offered headless or to an embedded model without vision. `/camera` works either way. | off |
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

#### Claude

The Claude Code CLI, for `/claude` (you message it) and `claude_advisor` (the model asks it). The Claude API and the Claude CLI as servers are on `/settings` › Claude.

| Setting | What it does | Default |
|---|---|---|
| Claude executable | The Claude Code CLI. Blank looks on the PATH and in `%USERPROFILE%\.local\bin`; a path you set must exist. | (looked up) |
| Claude slash command permissions | What `/claude` may do: `read-only` (`Read`, `Grep`, `Glob`, `WebSearch`, `WebFetch`), `edit` (its usual tools with file edits, no commands) or `full` (everything, `bypassPermissions`). Anything else is denied, never asked. It works in the working directory but outside the app's sandbox and approvals. | `read-only` |
| Claude slash command model | `/claude`'s `--model`: Claude Code's default, `fable`, `opus`, `sonnet`, `haiku`, or *Other…*. | (Claude Code's default) |
| Claude slash command effort | `/claude`'s `--effort`: Claude Code's default, `low`, `medium`, `high`, `xhigh` or `max`. | (Claude Code's default) |
| Claude advisor tool | Offers `claude_advisor`: read-only advice from Claude Code when the model is stuck. Each call costs money on your Claude account. | off |
| Claude advisor tool context | `brief` sends the question and context; `recent` adds the last 10 messages (tool results cut to 500 characters). | `brief` |
| Claude advisor tool calls per turn | Advisor calls one reply may make (1–10). | 2 |
| Claude advisor tool model | The advisor's `--model`; the first row follows *Claude slash command model*. | (as Claude slash command model) |
| Claude advisor tool effort | The advisor's `--effort`; the first row follows *Claude slash command effort*. | (as Claude slash command effort) |
| Claude advisor tool confirm | Each call waits for your yes (the cursor starts on No). Refused headless. | off |

#### Home Assistant

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

#### Print

| Setting | What it does | Default |
|---|---|---|
| Print tools | Offers `list_printers` and `print_file`. `/print` works either way. | off |
| Print action policy | `off`: list printers only. `ask`: each print shows the file, printer, pages and copies and waits for your yes (refused headless). `allow`: prints without asking. `/print` never asks. | `ask` |
| Print default printer | Where a print goes when none is named. | (Windows default) |
| Print font size (pt) | Body text size for printed listings and Markdown (6–24); headings scale from it. | 10 |
| PDF engine | What makes a PDF for `convert_to_pdf` and `/pdf`. `auto`: Edge, Chrome or Brave, else Microsoft Print to PDF, which also takes over when the browser fails. `browser`: the browser only. `printer`: Microsoft Print to PDF only (Markdown, text and pictures). See Making PDFs. | `auto` |

#### Obsidian

| Setting | What it does | Default |
|---|---|---|
| Obsidian tools | Offers the vault tools (search, list, read, links, daily, write, properties, move) once a vault is set. | off |
| Obsidian vault | The vault's folder (holding `.obsidian`), separate from the working directory. The row opens the folder picker. | (not set) |
| Obsidian allow delete (.trash) | Offers `vault_delete`, which moves a note or attachment into the vault's `.trash`. | on |

#### ComfyUI

| Setting | What it does | Default |
|---|---|---|
| ComfyUI tools | Offers `generate_image` and `set_splash_image` once *ComfyUI URL* is set and a workflow is installed. | off |
| ComfyUI URL | The ComfyUI server, often on your LAN (`http://gpu-box:8188`). The web tools' network mode never blocks it. | (not set) |
| ComfyUI workflows offered | A checklist of the workflows the model is offered; nothing until ticked (here or in the wizard). **A** / **N** tick all or none. With one ticked, every plain request and plain `/imagine` uses it. `/imagine <name>` can still use a hidden one. | none |
| ComfyUI add workflow | A wizard that **builds** a standard workflow from your server's checkpoints, or **imports** a ComfyUI export. See Adding a workflow. | — |
| ComfyUI ^-mention enabled | `^` and part of a name lists the offered workflows; a pick writes `^name`, which `generate_image` uses. | on |
| ComfyUI timeout (s) | How long to wait for one generation, queue included (10–3600). | 300 |
| ComfyUI max pictures per call | Most pictures one `generate_image` call or `/imagine --count` makes (1–16). All go to the model in the next request. | 5 |
| ComfyUI reinforce negatives | When the model writes the prompt, adds a few opposite tags to the negative where the image model tends to drift (a solo figure → `multiple girls`). Skipped for verbatim prompts, a negative given for the call, `/imagine`, families without a negative and a workflow whose `.md` says `reinforce: false`. | on |
| ComfyUI show prompts | Shows the `prompt:`, `negative:` and `params:` lines under each picture. | on |
| ComfyUI picture strip | Keeps the session's pictures as thumbnails above the input line, newest left. With the line empty, ←/→ highlight one (moving an open viewer to it) and Enter opens it; a double-click opens any, and a drag onto the input row attaches it. **🎞️** opens the viewer on the output folder; **×** hides the strip until the next picture; meanwhile **🎞️** on the rule over the input row (right of ⤡) brings it back. `/clear`, `/new` and a session switch empty it. | on |
| ComfyUI output folder | Where pictures are saved, under the working directory (`comfy_images\pony-txt2img-1234.png`); empty = the working directory. | `comfy_images` |

#### SQL

| Setting | What it does | Default |
|---|---|---|
| SQL tools | Offers the SQL tools (connections, databases, tables, columns, describe, relationships, indexes, query) over `sql.json`'s connections. | off |
| SQL connections offered | A checklist of the connections in both `sql.json` files; nothing is offered until ticked (here or in the wizard). **A** / **N** tick all or none. A hidden connection is invisible to every tool, the rules and the `%`-mention. | none |
| SQL default connection | The connection a call uses when it names none: an offered one, or the first. | (the first connection) |
| SQL set password | Pick a `sql` or `runas` connection and type its password, masked; it goes to that connection's store. | — |
| SQL add connection | A wizard for a new connection, which can **test** it (`SELECT @@VERSION`) before saving. See Managing connections. | — |
| SQL %-mention enabled | `%` and part of a name lists the connections; a pick writes `%name`. | on |
| SQL max rows | Rows `sql_query` returns unless the call says otherwise (1–100000). | 100 |
| SQL query timeout (s) | How long one batch may run on the server (1–600). | 30 |
| SQL query result max chars | The most characters of table one `sql_query`, `oracle_query` or `mysql_query` returns (1000–1000000); the header says how many rows fit. | 32,000 |
| SQL connections (profile) | Enter opens the profile's `sql.json` in your editor (created with commented examples). | (none) |
| SQL connections (global) | The same for the home folder's `sql.json`, which every profile reads. The profile's wins a name clash. | (none) |

#### Oracle

The Oracle, MySQL and UNC tabs work like the SQL tab, over `oracle.json`, `mysql.json` and `unc.json`.

| Setting | What it does | Default |
|---|---|---|
| Oracle tools | Offers the Oracle tools (connections, schemas, tables, columns, describe, relationships, indexes, query). | off |
| Oracle connections offered | As *SQL connections offered*. | none |
| Oracle default connection | As *SQL default connection*; `schema` works in another schema. | (the first connection) |
| Oracle set password | As *SQL set password*. | — |
| Oracle add connection | The wizard; its test shows who it signs in as, the version, and a warning when the account could change data. See Oracle. | — |
| Oracle %-mention enabled | Lists the Oracle connections in the `%` list too, marked `Oracle ·`. | on |
| Oracle max rows | As *SQL max rows*, for `oracle_query`. | 100 |
| Oracle query timeout (s) | How long one statement may run on the server (1–600). | 30 |
| Oracle connections (profile) | As *SQL connections (profile)*. | (none) |
| Oracle connections (global) | As *SQL connections (global)*. | (none) |

#### MySQL

| Setting | What it does | Default |
|---|---|---|
| MySQL tools | Offers the MySQL tools (connections, databases, tables, columns, describe, relationships, indexes, query), for MySQL 8.0.16+ and MariaDB 10.2+. | off |
| MySQL connections offered | As *SQL connections offered*. | none |
| MySQL default connection | As *SQL default connection*. | (the first connection) |
| MySQL set password | As *SQL set password*. | — |
| MySQL add connection | The wizard; its test shows who it signs in as, the version, and a warning when `SHOW GRANTS` allows changes. See MySQL. | — |
| MySQL %-mention enabled | Lists the MySQL connections in the `%` list too, marked `MySQL ·`. | on |
| MySQL max rows | As *SQL max rows*, for `mysql_query`. | 100 |
| MySQL query timeout (s) | How long one statement may run (1–600), enforced by the server and the driver. | 30 |
| MySQL connections (profile) | As *SQL connections (profile)*. | (none) |
| MySQL connections (global) | As *SQL connections (global)*. | (none) |

#### UNC

| Setting | What it does | Default |
|---|---|---|
| UNC tools | Offers `unc_shares`, `unc_search`, `unc_info`, `unc_read`, and `unc_fetch` while the File tools are on. See UNC shares. | off |
| UNC writes | The master key for changes. On, a share with `access: readwrite` also gets `unc_write`, `unc_patch`, `unc_create_directory`, `unc_move`, `unc_copy`, `unc_delete` (off by default in Offered) and `unc_put`. Changes are permanent. | off |
| UNC shares offered | As *SQL connections offered*. | none |
| UNC default share | The share a call uses when it names none and gives no full path. | (the first share) |
| UNC set password | As *SQL set password*, for runas shares. | — |
| UNC add share | The wizard; its test lists the share's root under its account. | — |
| UNC *-mention enabled | `*` and part of a name lists the offered shares; a pick writes `*name`. | on |
| UNC shares (profile) | As *SQL connections (profile)*. | (none) |
| UNC shares (global) | As *SQL connections (global)*. | (none) |

#### Docker

| Setting | What it does | Default |
|---|---|---|
| Docker tools | Offers `docker_containers`, `docker_logs`, `docker_inspect`, `docker_stats`, `docker_resources` and `docker_compose`, whether Docker Desktop runs or not. `/docker` works either way. See Docker. | off |
| Docker writes | The master key for the model's changes: `docker_lifecycle`, `docker_pull`, `docker_remove` and `docker_prune` (the last two off by default in Offered). Every call asks first; headless refuses them. | off |
| Docker engine pipe | The engine's named pipe: a name (`docker_engine` is Docker Desktop's), `\\.\pipe\name` or `npipe:////./pipe/name`. Blank is the default. | `\\.\pipe\docker_engine` |

#### GitLib

| Setting | What it does | Default |
|---|---|---|
| GitLib tools | Offers the in-process git tools (status, log, show, diff, blame, branch, stage, commit, stash, discard, delete) over the working directory's repository. Off, git goes through the shell and `/gituser` does nothing. | off |
| GitLib diff max lines | Where a `gitlib_diff` patch is cut (20–5000). | 500 |
| GitLib log max commits | Commits `gitlib_log` returns by default (1–200). | 20 |
| GitLib email | The `user.email` `/gituser` writes into the repository's config. | (not set) |
| GitLib name | The `user.name` `/gituser` writes beside it. | (not set) |

#### Options

| Setting | What it does | Default |
|---|---|---|
| $-mention enabled | `$` and part of a name lists the tools the next turn offers; a pick writes `$name`. | on |
| Tool collapse count | A run of more tool calls than this folds to one summary line (`▸ 🛠️ 7 tool calls — read_file ×3, …`); 0 never folds (0–100). | 2 |
| Code collapse count | A code block longer than this folds to its label (`▸ 📜 csharp · 57 lines`) once complete; while streaming, only its last lines show (0–100; 0 never folds). Needs *Transcript markdown*. | 20 |
| Show file diffs | A file edit (`patch_file`, `write_file`, `unc_patch`, `unc_write`) shows its diff under its line: `└ Added 3 lines, removed 1 line`, then the changed lines numbered with three of context, added ones on a green slab, removed ones on a red, coloured by the file's language. It folds with the tool run (the note and its diff count as one). The model's result is the same either way. | on |
| Diff max lines | The most rows of an edit's diff shown; past it `… 12 more lines` ends it (0–500; 0 = the header line alone). | 10 |

To see a folded block, click it, press Ctrl+O, click **⤡** or use `/expand`.

</details>

<details>
<summary><b>🔌 MCP Servers & System (`/mcp` & `/sys`)</b></summary>

### MCP servers (`/mcp`)

#### Servers

One row per server in `mcp.json` (the profile's, then the home folder's; the profile's wins a name clash), with its transport and state: `connected · N tools`, `connecting`, `failed: …` or `off`. Enter or Space turns one on or off (Enter on a failed one retries). Below them: `edit profile mcp.json`, `edit global mcp.json`, `reload`, and any skipped entries.

#### Tools

Every connected server's tools, as `<server>__<tool>`, with the server's description. Enter or Space switches one.

#### Options

| Setting | What it does | Default |
|---|---|---|
| MCP servers | The master switch: on, every enabled server starts at launch (and on a profile switch) and its tools are offered. | off |
| MCP connect timeout (s) | How long a server gets to finish the handshake and list its tools (5–300). | 30 |

### System prompt (`/sys`)

A read-only view of exactly what the next reply sends.

#### Prompt

The system prompt section by section, each with its status:

* **Persona** (built in, from the repo's `assets/prompts/persona.md`, or `persona.md`)
* **Operating rules** (built in, or `operata.md`; the reply-format and tool rules)
* **Project notes** (`NEON.md` / `AGENTS.md`)
* **Memory**
* **Skills** (the catalog)
* **Voice directive** (`vocalia.md`, when it has text; spoken turns only, always last)

#### Tools

Every tool the reply may call, grouped as on `/tools` (plus one group per MCP server, and Plan in plan mode), with the description the model reads. Switched-off tools are left out.

</details>

## Slash commands
[↑ Back to top](#neon-sidekick)

Type `/` to list every command with a summary; after a command and a space, its arguments are listed where the app can offer them. `//` is an unlisted shortcut for `/settings`. See [Keyboard shortcuts](#keyboard-shortcuts) for the keys that run commands, and [Commands typed during a reply](#commands-typed-during-a-reply) for what runs mid-reply.

<details>
<summary><b>⌨️ Click to expand all Slash Commands</b></summary>

| Command | What it does |
|---|---|
| `/about` | Shows the version, runtime, folders, components and licence. |
| `/claude <message>` | Sends the message to Claude Code and streams its reply into the transcript. See Claude Code from the chat. |
| `/clear` | Starts a new conversation and clears the screen. |
| `/cmdcopy <profile> [--history] [overwrite]` | Copies *Shell allowed commands* into another profile (added, or replacing with `overwrite`). `--history` copies the command history instead (refused when that profile has *Keep command history* off). |
| `/keycopy <profile>` | Copies the *LLM API key*, *Claude API key*, *OpenAI API key* and *Home Assistant API key* into another profile after a confirmation, mirrored: a key unset here clears theirs. Keys set only by environment variable aren't copied. |
| `/cmdclear` | Clears the command history, stored and in memory, after a confirmation. |
| `/cmdlist` | Opens *Shell allowed commands*: Enter removes a prefix; the ask and yolo buttons (A, Y) switch *Shell command policy*. |
| `/police` | Opens the on/off page for *Shell police outside paths*; its strings button (S) opens *Shell police forbidden strings*. |
| `/compact [focus]` | Shrinks the context; a focus tells the summary what to concentrate on. |
| `/copy [n \| all] [--thinking]` | Copies the last reply (or the last *n*, or the whole transcript) as Markdown. `--thinking` includes the thinking, quoted under `💭 **Thinking**`. |
| `/cwd [path \| ~ \| browse]` | Shows or changes the working directory. `~` returns to the profile's `files\`; `browse` opens the [folder picker](#folder-picker). |
| `/camera` | Opens the camera pane: Space takes the photo, R retakes, Enter puts it on the input line as `[Image #N]`, ESC drops it. Without the pane it snaps at once. See Camera. |
| `/camera snap` | Takes a photo at once and puts it on the input line. |
| `/camera list` | Lists the cameras, numbered, the chosen one marked. |
| `/camera use <n\|name>` | Chooses the camera by number or name (*Camera device*). |
| `/camera live` | Shows the camera live in its own window until you close it or `/camera off`. |
| `/camera watch [seconds\|off]` | Looks every *Camera watch interval* (or the seconds given); a picture that changed rides your next message. Never on at startup. |
| `/camera off` | Ends `/camera live` and watch mode; the camera closes a few seconds later. |
| `/docker` | Docker Desktop's containers on a pane, with state, health and ports. Enter offers what fits: stop, restart or pause (asking first), start or unpause, the last 50 log lines, open a port in the browser, copy the id. |
| `/docker ps \| status \| logs <container> [lines] \| stats [container]` | The containers; the versions and counts; a container's last lines (50 by default); CPU, memory, network and disk use. |
| `/docker start\|stop\|restart\|pause\|unpause <container>` | Acts on one container by name, part of a name or id. Your own hand: *Docker writes* doesn't apply and nothing is asked, but every change is logged. |
| `/draft` | Writes the next message in your editor; it is sent when you save and close. |
| `/echo <text>` | Prints a line as a reply (spoken when speech is on). |
| `/exit` | Exits the app. |
| `/explore [path]` | Opens the working directory in your file browser. |
| `/gituser [force]` | Writes *GitLib email* and *GitLib name* into the repository's config. An existing `[user]` section stays unless `force`. Does nothing while *GitLib tools* is off. |
| `/ha` | Home Assistant at a glance: lights on per room, the TV, temperatures, motion, low batteries and to-do lists. |
| `/ha on\|off\|toggle <room or name> [brightness%]` | Switches a room, light, switch or the TV (`/ha on den 40%`, `/ha off kitchen and hallway`). |
| `/ha scene <name>` | Activates a scene (`/ha scene den relax`). |
| `/ha tv on\|off\|mute\|unmute\|up\|down\|vol <0-100>\|source <name>` | Controls the only media player (`/ha tv source hdmi 2`). |
| `/ha states [domain \| words \| entity id]` | Lists entities with ids and states; an entity id shows all its attributes. |
| `/ha say <sentence>` | Hands a sentence to Home Assistant's Assist agent. |
| `/header [on \| off]` | Shows or hides the banner (*Show header*), from the next clear. Alone, it flips the setting. |
| `/help` | The commands (basic and advanced) and keys. |
| `/interrupt [on\|off]` | Toggles the wake-word interrupt during a spoken reply. |
| `/learn [note \| sessions [N \| text]]` | Writes or improves a skill in the background, from the last turn or stored sessions. |
| `/log` | Opens the [log window](#log-window). Works without `--log`. |
| `/log --file` | Opens the `--log` file in your editor (only when started with `--log <path>`). |
| `/loop <count> [delay] <message>`, `/loop infinite [delay] <message>` | Sends the message that many times, or until ESC or Ctrl+C, waiting for each reply. See Loops. |
| `/plan <requirement>` | Researches with read-only tools and presents a plan before anything changes. See Plan mode. |
| `/botchat [profile ...] [topic]` | Lets profiles talk to each other until you stop them. See Bot conversations. |
| `/expand` | Unfolds every tool run, code block and thinking block, now and from here on. Ctrl+O switches between this and `/collapse`. |
| `/collapse` | Folds them again. |
| `/mcp` | Connects MCP servers and switches their tools. On the Tools tab, typing narrows the list to the tools whose name or description holds the text; Backspace erases, ESC clears it, the next ESC closes. |
| `/memory [forget \| edit \| copy <profile> [overwrite]]` | Lists memories on a pane (Enter removes one). `forget` forgets all; `edit` opens `memory.json` in your editor; `copy` adds them to another profile's (or replaces with `overwrite`). `forget` and `copy` ask first. |
| `/model [id]` | Picks or sets the model. On the embedded LLM, lists the installed models. |
| `/new` | Starts a new conversation without clearing the screen. |
| `/operata [reset \| copy <profile> [force]]` | Edits `operata.md` (the operating rules) in your editor, resets it, or copies it to another profile (`force` replaces theirs). |
| `/perf [off \| text \| gauge \| spark \| led]` | Hides the performance bar, or brings it back with its last meters; a look name sets that look and shows it. |
| `/persona [reset \| copy <profile> [force]]` | The same for `persona.md` (the personality; seeded with the built-in persona). |
| `/print <file> [printer=<name>] [copies=N] [pages=1-3] [landscape]` | Prints a file from the working directory (see Printing). The printer matches by name or part of it; quote a name with spaces. *Print action policy* never applies. |
| `/print reply [options]` | Prints the last reply as formatted Markdown. |
| `/print printers` | Lists the printers, marking the Windows default and *Print default printer*. |
| `/pdf <file> [to=<out.pdf>] [paper=letter\|a4\|legal] [landscape] [overwrite]` | Makes a PDF in the working directory from Markdown, text or code, HTML or a picture, beside the file unless `to=` says (see Making PDFs). |
| `/pdf https://… [to=<out.pdf>] [overwrite]` | Makes a PDF of a web page as the browser shows it; *Web browser network mode* still applies. |
| `/pdf reply [options]` | Makes a PDF of the last reply as formatted Markdown. |
| `/profile [name \| add <name> \| delete <name> \| rename <name> <new> \| reset [name] [--all] \| push <name> \| pull <name> \| edit \| reload]` | Switches, creates, deletes, renames or resets a profile, or copies its settings to (`push`) or from (`pull`) another. `edit` opens `profile.json`; `reload` reads it back and reconnects what changed. See Profiles. |
| `/queue [clear]` | Lists and prunes the queued messages (`⊠ clear all` or `c` drops them); `/queue clear` drops them without the pane. |
| `/reasoning [level]` | Picks the reasoning effort (`none`, `low`, `medium`, `high`, `xhigh`). |
| `/rewind [n]` | Goes back to an earlier message: pick one (n back), confirm, and it and everything after leave the conversation (and the stored session), its text returning to the input row, pictures and pastes included. Only the conversation rewinds; the confirmation names the tools whose changes stay. Compacted messages can't be picked. |
| `/remember <text>` | Adds a memory. |
| `/sampling [field value]` | Edits the per-model sampling on a pane, or the connected model's directly with `<field> <value>`, `<field> clear`, `extra <json>` or `clear`. See Sampling per model. |
| `/server [url \| embedded \| claude-cli \| docker \| docker:<container>]` | Picks an LLM server (found, Claude API, OpenAI API, Claude CLI, installed embedded models, chosen Docker containers) or sets one by URL, then the model and reasoning, with one reconnect. `embedded`, `claude-cli`, `docker` and `docker:<container>` go straight to those. A container serves its own model, so no model picker follows. |
| `/sessions [id \| purge <id> \| purge older <age> \| purge all \| title [<text>]]` | Lists, restores, renames and purges stored sessions. An age is days (`30`) or a duration (`12h`, `90m`, `2 hours`, `1d 6h`). `title` alone opens a box with the current name. |
| `/settings`, `//` | Edits and saves the settings. |
| `/skills` | Lists the skills and edits the skill, reflection and project-file settings. On the Offered tab, typing narrows the list to the skills whose name or description holds the text; Backspace erases, ESC clears it, the next ESC closes. |
| `/skills add <search words \| owner/repo[/skill] \| github url \| zip url> [--global \| --profile]` | Installs an [Agent Skill](https://agentskills.io) from the web after a preview. Refused during a reply. See Installing skills. |
| `/skills purge list <age>` | Lists the skills unused for that long. See Skill records and purging unused skills. |
| `/skills purge commit <age>` | Deletes them, folder and record, after a yes/no. |
| `/skills revert <name>` | Puts a skill back as it was before the app's last change; each revert goes one version further. Refused after a hand edit. See Skill history. |
| `/speak [file [n] \| n]` | Reads a text file from the working directory aloud. Alone it resumes; a number starts at that sentence. |
| `/splash` | Starts a new conversation and shows the splash screen. |
| `/stt [on\|off]` | Toggles voice input. |
| `/sys` | Shows the system prompt and the tools sent to the model. |
| `/tb [on \| off]` | Hides the toolbar, or brings it back with its last items (the default seven the first time). |
| `/terminal [folder]` | Opens a new Windows Terminal window in the working directory, or in a folder under it (Tab completes the folder). Without Windows Terminal it opens a console window there. |
| `/test [id \| reasoning \| structured \| long \| all \| history]` | Runs benchmark tests against the connected model. Alone, lists them with their last verdicts. See Benchmark tests. |
| `/theme [name]` | Switches the colour theme, built-in or [custom](#custom-themes); alone, opens a picker with a live preview (79+ columns); a typed letter jumps to the next theme starting with it. Nothing changes until Enter; during a reply it waits. `/theme export <name> [new-name]` writes a theme to the `themes` folder to edit. |
| `/timer [duration [name] \| stop <name> \| stop all]` | Lists, starts (`10m`, `90s`, `1h30m`) or stops timers. |
| `/tools` | Switches the model's tools and edits their settings (Web, Files, Shell, Ask, Camera, Claude, Home Assistant, Print, Obsidian, ComfyUI, SQL, Oracle, MySQL, UNC, Docker, GitLib). On the Offered tab, typing narrows the list to the tools whose name or description holds the text (`haiku`); Backspace erases, ESC clears it, the next ESC closes. |
| `/tools <group>` | Opens one group's switch: `shell` (the *Shell command policy* picker), `files`, `web`, `claude`, `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `unc`, `ha`, `comfy`, `camera` or `print`. The toolbar's tool items run it. |
| `/tree [path]` | Prints a tree of the working directory (hidden entries only under *File browser/tree mode* `show-hidden`; `.git` only when named). |
| `/tts [on\|off]` | Toggles speech output. |
| `/usage` | Token usage and performance; `~` marks an estimated reasoning count. |
| `/vault [path]` | Prints a tree of the *Obsidian vault* (or a folder in it), like `/tree`. |
| `/view <image or folder> [--chat]` | Opens an image (or a folder's newest picture) in the picture viewer; `--chat` draws it in the transcript instead. |
| `/imagine [workflow] <prompt> [-- <negative> \| --no-negative] [--seed N] [--size WxH] [--steps N] [--cfg X] [--denoise X] [--image <path>] [--image2 <path>] [--image3 <path>] [--count N]` | Generates a picture on ComfyUI from your prompt exactly as typed. See Imagine options. |
| `/comfy` | The ComfyUI server's status, the workflows found, skipped files and where workflows go. |
| `/comfy edit json <workflow>`, `/comfy edit markdown <workflow>` | Opens a workflow's graph, or its `.md` (created if missing), in your editor. |
| `/comfy view` | Opens the picture viewer on the output folder. |
| `/comfy purge` | Deletes everything in the output folder, `.pasted` included, after a yes/no. Refused when it is the working directory. |
| `/vocalia [reset \| copy <profile> [force]]` | Edits `vocalia.md` (the voice directive, empty by default, added last to every spoken reply), removes it, or copies it to another profile. |
| `/wake [on\|off]` | Toggles the wake word. |
| `/window` | Shows the terminal window's size. |

</details>

### Command details

<details>
<summary><b>📖 Click to expand the longer commands</b></summary>

#### Plan mode

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
| `/plan open` | Lists the plans with status and progress. |

#### Bot conversations

`/botchat [profile ...] [topic]` lets profiles talk to each other until you stop them.

* **Cast:** the profiles you name, or all of them. The current profile always joins and speaks first.
* **Topic:** the first word that isn't a profile starts it (`/botchat ada max the best pizza`); after `--`, the rest is always the topic (`/botchat ada -- max speed of light`). Without one, the bots pick.
* **Turns:** each reply is in the speaker's persona and, with speech on, its own voice. A bot named in the last line speaks next; otherwise a random one, never the last speaker. Bots use this profile's LLM, or their own under *Botchat LLM mode* `multi`.
* **Tools:** none, except pictures (see Botchat pictures) and `load_skill` (*Botchat skills enabled*).
* **Joining in:** a line you type joins before the next reply.
* **Talking:** with *STT input* on, push-to-talk stops the speaking bot, cuts the replying one short, and listens; what you say follows *STT destination*. With *TTS output* off, the wake phrase (*STT wake*) does the same. The wake phrase during speech (*STT interrupt*) ends the chat.
* **ESC** steps: stop the voice, then cut the replying bot short (the next one answers), then, before the next bot has said anything, end the chat. `/exit`, `/clear` and `/new` end the chat, then run.
* **Resume:** `/botchat --resume [line]` continues this run's last chat; a line after it joins as yours.
* **Embedded bots:** see *Botchat multi-embedded*. `/botchat --kill` stops leftover extra servers (never this profile's own).
* **Pronouns** come from each profile's first TTS voice (`am_`, `bm_`… male, anything else female).
* **Saved** as a session of its own under *Session logging*; the current conversation is left alone.

#### Claude Code from the chat

`/claude <message>` runs the `claude` CLI headless in the working directory.

* The reply streams in under Claude's name, with each tool on a dim line and a cost footer; with speech on, it is spoken.
* The pair joins the conversation tagged `[to Claude]` and `[Claude]`, so the local model can build on it. Claude doesn't see the local conversation.
* Each session has one Claude conversation, resumed by the next `/claude` (even after a restart). `/claude new`, `/clear`, `/new` and a profile switch start another.
* *Claude slash command permissions* sets what Claude may do; anything more is denied, never asked.
* ESC or Ctrl+C stops it, keeping the reply so far. Works with no LLM server; refused during a reply.
* Your own Claude Code setup applies (sign-in, `CLAUDE.md`, skills, MCP servers, hooks). `/usage` shows the cost.

#### Loops

* A delay after the count waits after each reply: `/loop infinite 1m check the build` (one word, up to 24 hours). ESC or Ctrl+C stops the loop.
* A cancelled, withdrawn or failed turn ends it.
* The message may be `/imagine …` or `/speak …`, run with no model in between (`/loop infinite 5s /imagine score_9, 1girl`). `/speak` waits for each reading; only the last pass's pictures go with your next message. No other command can be looped.

#### Benchmark tests

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
* The structured tests send their schema as a strict `json_schema` `response_format`; a fenced reply fails. They are skipped over the Claude API.
* The long-context tests size themselves from the context window (the haystack up to half of it, at most ~66k tokens; ~66k when unknown). *LLM request timeout* applies; a refused or timed-out request is an error.
* A run starts as `/clear` leaves things. Results show one line per test, then a table of time, tokens and tok/s. ESC stops the run, keeping what finished; the input line works as during a reply.
* The last 50 runs are saved in the profile's `tests.json` with their reasoning and sampling; `/test history` lists them. Nothing enters the conversation.

#### Imagine options

* The picture is drawn in the transcript, saved in *ComfyUI output folder*, and handed to the model with your next message. The model gets it as a JPEG (a transparent picture stays PNG), so every request after carries a fraction of the bytes; the file saved is the server's own.
* The first word names the workflow when it matches one; otherwise an offered workflow is used.
* `-- <negative>` sets the negative; `--no-negative` sends none, not even the workflow's default.
* `--count` is capped by *ComfyUI max pictures per call*.
* `--image2` / `--image3` feed multi-picture workflows; a workflow with no prompt runs on its pictures alone (`/imagine faceswap --image a.png --image2 b.png`).
* `/loop` repeats it: `/loop 10 30s /imagine …`.

#### Folder picker

`/cwd browse`, and the *Working directory* and *Obsidian vault* rows, open a folder tree headed **📂 Folders**, on the directory in use.

* `⌂ profile` (the profile's `files\`) and `▣ splash` (its `splash\`) sit above the drives.
* Space, → and ← open and close folders; `-` collapses all. A click on a folder's glyph or a double-click on its name opens or closes it.
* Only Enter chooses. Choosing `⌂ profile` saves the default, like `/cwd ~`.

#### Picture viewer

The picture viewer is a window of its own (Windows only; elsewhere the file's registered app opens, and `/view` draws in the transcript). It opens from:

* **A double-click on a picture in the transcript**, on that picture's folder. A picture with no file (a paste, the built-in splash) is written to `%TEMP%\NeonSidekick\pictures` first. *Image viewer* can send these to another program.
* **`/view <image or folder>`**: the image, or a folder's newest picture, following new ones.
* **`/comfy view`** or the strip's **🎞️**: the ComfyUI output folder, following new pictures.

| Key | Action |
|---|---|
| ← / → | Newer / older picture, the strip's way (the newest is at the left and counts 1 in the title); reaching the newest follows new pictures again |
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
* It and the ComfyUI picture strip follow each other: browsing the viewer highlights the same picture in the strip, and picking one on the strip moves the viewer to it without bringing it forward.
* The camera's live view (*Camera preview* `live`, `/camera live`) is a separate window, so both can be open. It shows the camera mirrored, then the photo, never takes the keyboard, answers only F11, a double-click and Esc (plus Tab and the chords that go to the chat, as in the viewer; Ctrl+Alt+V closes it), and remembers its own place.

#### Log window

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

#### Camera

A USB or built-in webcam through Windows' Media Foundation; nothing to install. Windows only.

* **One shared stream:** the camera pane, the live view, a botchat and watch mode share one open camera, which closes a few seconds after the last lets go. A photo waits about a second for the exposure to settle.
* **📷 on the hint row** shows whenever the camera is on (with its light and Windows' indicator); double-click it to end `/camera live` and watch mode.
* **Photos** are JPEGs in *Camera output folder* (`camera_images` by default), named by time (`20261002-140203.jpg`); a retaken or declined one is deleted. Botchat and watch pictures aren't saved, but double-clicking a watch thumbnail writes it to the folder's `.watch` subfolder, which is cleared when watch mode stops and on every profile load.
* **Stored sessions** keep a line instead of the picture unless *Camera keep in sessions* is on.
* **Failures** say why: Windows' *Let desktop apps access your camera* is off (Settings › Privacy & security › Camera), another app has the camera, it was unplugged, or Media Foundation is missing (Windows N needs the Media Feature Pack).
* **Watching** uses stills: each picture is compared on your machine with the last one the model saw, and sent only when enough changed.

#### Profiles

* A name is 1 to 32 letters, digits, `-` or `_`, and can't be `neon` or one of the verbs.
* A name starting with `_` is temporary: left off the picker and the name list (unless loaded), and the next launch opens `default` (the profile is kept). `/profile _name` still switches to one.
* `--profile <name>` (or `NEONSIDEKICK_PROFILE`) opens a profile for one launch without changing the next launch's. An unknown name exits with code 2; a headless run with neither opens `default`.
* A reset keeps the LLM URL, LLM model, LLM API key, TTS HTTP URL, Claude API key, OpenAI API key, Web browser path, Web search method, Web SearXNG URL, Claude executable, Obsidian vault, ComfyUI URL, Home Assistant URL and Home Assistant API key; `--all` resets those too. `default` can only be reset while loaded.
* `push <name>` copies the loaded profile's settings over another's; `pull <name>` the other way. Both ask first. Only `profile.json` is copied (the target keeps its working directory); a pull clears the conversation.
* The API keys in `profile.json` are encrypted for your Windows account (DPAPI, `dpapi:…`); a key typed into the file by hand is encrypted at the next load. Only the same Windows user on the same machine can read them.

#### Custom themes

Your themes are JSON files in the home's `themes` folder, shared by every profile, and subfolders one level deep (not ones starting with `.`). They are listed among the built-ins, sorted by name, and re-read whenever a theme list opens (pick the theme again to see an edit). On a name clash, a file loose in the folder beats one in a subfolder, then the first subfolder by name wins; the loser gets a warning. A file named like a built-in replaces it, including as the base other themes build on.

Fifty ready-made themes come with the repo in [`assets/themes`](assets/themes), one folder per category: copy a file (or a whole category folder, `themes\cosmos`) into your `themes` folder and pick it. [`Theme Atlas.html`](assets/themes/Theme%20Atlas.html) previews them all with the ten built-ins, which are in the `built-in` folder as files to copy, edit or rename (the file and its `"name"`).

| Folder | Themes |
|--------|--------|
| `art` | bauhaus, deco, inkwash, kaleidoscope, lapis, stainedglass, ukiyoe |
| `cinema` | akira, arrakis, ghostshell, hal, starbase, twinsuns |
| `cosmos` | aurora, blackhole, bloodmoon, nebula, orbit, solaris, supernova |
| `elements` | magma, opal, oxide, prism, radium, temper |
| `machines` | circuit, collider, commodore, glitch, heartbeat, infrared, nixie, vhs, vinyl |
| `nights` | kowloon, lighthouse, miami, sakura, witchhour |
| `solid` | blueprint, carbon, chalkboard, espresso, fieldradio, glacier, matcha, signal, ultraviolet, velvet (one-colour banner and rule, no gradient) |

The easiest start is `/theme export <name> [new-name]`, which writes any theme to `themes\<new-name>.json` with every colour filled in (default name `<name>-custom`; it never overwrites). Export a built-in under its own name (`/theme export noir noir`) to replace it.

```jsonc
{
  "name": "dracula",                 // optional: the file name when left out
  "description": "vampire purple",   // optional: the note beside the name ("custom theme" when left out)
  "base": "synthwave",               // optional: a built-in or another of your themes (synthwave when left out, or the built-in a file replaces)
  "colors": { "primary": "#FF79C6", "secondary": "#8BE9FD", "ink": "#F8F8F2", "bg": "#282A36", "panelBg": "#44475A" },
  "gradient": ["#8BE9FD", "#BD93F9", "#FF79C6", "#FFB86C", "#F1FA8C"],
  "styles": {
    "codeComment": { "fg": "#6272A4", "italic": true },
    "codeKeyword": { "fg": "primary", "bold": true },
    "menuHighlight": { "bg": "#44475A" }
  }
}
```

* **A file only says what it changes;** the rest comes from `base`, which can itself be a file (as long as the chain doesn't loop). A file replacing a built-in builds on the original, so `noir.json` with only a `primary` is noir with that primary.
* **Names** are 1 to 32 lower-case letters, digits, `-` or `_`, starting with a letter or digit, and not `export`.
* **Colours** are `#RRGGBB` or `#RGB`. Comments and trailing commas are allowed.
* **Derived colours:** on bases that derive `warn` and the gradient from the accents (synthwave and most built-ins), a file setting neither gets them worked out from its own colours.
* **Problems:** an unreadable file, bad JSON, a bad name or a missing or looping base skips the file; a misspelled key or bad colour is ignored. Each shows as a warning when a theme list opens.

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
* **Menus:** `menuHighlight`, `menuHighlightDim`, `menuDisabled`.
* **Replies:** `markdownBold`, `markdownItalic`, `markdownCode`, `markdownCodeBlock`, `markdownCodeLabel`, `thinking`, `markdownHeading1`, `markdownHeading`, `markdownBullet`, `markdownQuoteBar`, `markdownQuote`, `markdownLinkUrl`, `markdownRule`.
* **Code highlighting:** `codeKeyword`, `codeType`, `codeString`, `codeNumber`, `codeComment`, `codePunctuation`, `codeFunction`, `codeVariable`, `codeAttribute`, `codeTag`, `codeHeading`, `codeInserted`, `codeDeleted`.
* **File diffs:** `diffAdded`, `diffRemoved` (the added and removed lines' slabs under a file edit; the text keeps its code colours, only `bg` counts).

Some styles copy another unless changed themselves: `user`, `spinner` and `markdownHeading` copy `accentSecondary`; `assistant` copies `body`; `systemText`, `hint`, `markdownCodeLabel`, `markdownQuote` and `markdownLinkUrl` copy `dimText`; `sectionHeading` copies `accentTertiary`; `markdownHeading1` copies `accent`; `markdownBullet` and `markdownQuoteBar` copy `trailerMark`; `markdownRule` copies `paneRule`; `codeAttribute` copies `codeType`; `codeTag` copies `codeKeyword`.

#### Voice presets

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

Forty-one more presets come with the repo in [`assets/voices`](assets/voices), one folder per kind (the built-ins are in `built-in`): copy a file, or a whole folder (`voices\accents`), into your `voices` folder. [`Voice Atlas.html`](assets/voices/Voice%20Atlas.html) plays every preset saying one line (samples in `assets/voices/samples`). The blends lean on Kokoro's stronger voices ([`VOICES.md`](https://huggingface.co/hexgrad/Kokoro-82M/blob/main/VOICES.md)).

| Folder | Presets |
|--------|---------|
| `narrators` | ada, edmund, iris, margot, silas, walter (unhurried, for long answers) |
| `brisk` | dash, kit, pepper, rex, sloane, zara (quick, 1.3-1.45x) |
| `british` | alfie, beatrice, ellis, harriet, poppy, rupert |
| `characters` | aria, atlas, jinx, maven, nick, wren |
| `duets` | ash, morgan, quinn, river, rowan, sage (a female and a male voice blended) |
| `accents` | amelie, arjun, beatriz, giulia, kenji, lucia, marco, mateo, mei, priya, yuki (an English voice leads, and a Spanish, French, Italian, Hindi, Portuguese, Japanese or Mandarin voice adds its accent) |

`dotnet run tools/VoiceSamples.cs` regenerates the samples and the atlas (it needs the downloaded Kokoro model); rerun it after changing a preset in `assets/voices`.

</details>

## Tools
[↑ Back to top](#neon-sidekick)

The tools the model can call, grouped as `/tools` and `/sys` show them. Each group has a switch that offers or withholds all of it: *File tools*, *GitLib tools*, *Shell command policy*, *Obsidian tools*, *SQL tools*, *Oracle tools*, *MySQL tools*, *UNC tools*, *Docker tools*, *ComfyUI tools*, *Home Assistant tools*, *Print tools*, *Camera tool*, *Claude advisor tool*, *Web tools*, *Memory*, *Agent skills*, *Session tool*, *Ask user* and *MCP servers*. Single tools switch on the Offered tab of `/tools`.

<details>
<summary><b>🕒 Clock & Timers</b></summary>

### Clock

| Tool | Arguments | What it does |
|---|---|---|
| `get_current_time` | `zone?` | The current date, time, weekday and time zone. Seeded at the start of every conversation. |
| `shift_date` | `date, days?, weeks?, months?, years?` | Moves a date by days, weeks, months or years and gives its weekday. |
| `days_between` | `from, to` | The days from one date to another (negative when the second is earlier). |

### Timers

| Tool | Arguments | What it does |
|---|---|---|
| `start_timer` | `name?, hours?, minutes?, seconds?` | Starts a named countdown that alerts you when it ends. Several can run at once. |
| `stop_timer` | `name` | Stops a running timer, or silences one that has gone off. |
| `list_timers` | — | Every running timer and its time left. |

</details>

<details>
<summary><b>❓ Help</b></summary>

### Help

| Tool | Arguments | What it does |
|---|---|---|
| `neon_help` | `query?, kind?` | NeonSidekick's own manual: every form of a command; a setting's effect, default and place (pane › tab › row); a pane's or tab's rows; the keys. `query` is a command (`/camera`), a setting, a pane or tab (`/tools camera`), a key (`Ctrl+H`) or plain words; none gives an overview. `kind` (`command`, `setting`, `pane`, `keys`) narrows it; `kind: command` alone lists every command. |

It is offered on every turn, headless and in plan mode too, and the rules tell the model to use it instead of guessing. It reads only the built-in reference, never your current settings. To withhold it, switch it off on the Offered tab.

</details>

<details>
<summary><b>📁 Files & Git</b></summary>

### Files

Every path is relative to the working directory; nothing outside it can be reached.

| Tool | Arguments | What it does |
|---|---|---|
| `get_working_directory` | — | The working directory's path. Seeded at the start of every conversation. |
| `search_files` | `text?, path?, files?, regex?, context?, output?, order?, limit?, depth?` | Searches text files for a word, phrase or regex (`file:line: text`, with context lines when asked). Without `text`, lists a folder, a tree (`depth` 2–4), files matching a name pattern, or the most recently changed files. `limit` goes up to *File search max results*. |
| `file_info` | `path` | A file's size, modified time, lines, words, line ending and BOM; a folder's counts and total size. Also checks that something exists. |
| `read_file` | `path, start_line?, max_lines?` | Reads a text file or part of it (a negative `start_line` counts from the end). A partial read names the line to continue from. |
| `view_image` | `path?, paths?` | Attaches images to the next message, up to *File view image max (per call)*. |
| `write_file` | `path, content, mode?` | Writes a text file: `create` (default; leaves an existing file alone), `overwrite` or `append`. Reports size, lines and words. |
| `patch_file` | `path, old_text, new_text, replace_all?` | Replaces one occurrence of `old_text` (or all with `replace_all`), exactly or tolerating differences in spacing, indentation, escapes and typographic quotes. Shows the edited lines. |
| `create_directory` | `path` | Creates a folder and any missing parents. |
| `move` | `from, to, overwrite?` | Renames or moves a file or folder; replaces nothing unless `overwrite`. |
| `copy` | `from, to, overwrite?` | Copies a file or folder, under the same rule; a folder over a folder merges. |
| `delete` | `path` | Deletes a file or folder for good. `.git`, anything in it and a folder holding one are refused. |
| `zip` | `path, to?, overwrite?` | Packs a file or folder into a `.zip`, beside it by default. |
| `unzip` | `path, to?, overwrite?` | Extracts a `.zip` into a folder, all or nothing. |
| `open` | `path?, share?` | Opens a file in your own editor or viewer, or a folder in Explorer (the working directory by default). `share` (or a full `\\server\share` path) opens one on a UNC share; a network runas share is refused. |
| `convert_to_pdf` | `path? \| url? \| markdown?, title?, to?, overwrite?, landscape?, paper?` | Makes a PDF in the working directory from a file, a web page (needs *Web tools* too) or Markdown it writes (see Making PDFs). Not in plan mode. |

### GitLib

Git inside the app (LibGit2Sharp), for when the shell is off or the model should never run `git.exe`. Turn *GitLib tools* off to leave git to the shell.

* Local only: no `fetch`, `pull`, `push` or `clone`.
* The repository's root must be the working directory or under it. Every tool takes an optional `path`, the file or folder it targets, which also picks the repository.
* `gitlib_delete` starts off; switch it on in the Offered tab.
* Commits need *GitLib email* and *GitLib name*, written into the repository by `/gituser`.

| Tool | Arguments | What it does |
|---|---|---|
| `gitlib_status` | `path?` | The branch, ahead/behind its upstream, and every staged, modified, untracked or conflicted path. |
| `gitlib_log` | `path?, ref?, max_commits?` | Commits reachable from `ref` (HEAD by default), newest first; with a file, only those that changed it. |
| `gitlib_show` | `ref, path?` | One commit's author, date, message and changed files; with a file, its text at that commit; with a folder, its entries. |
| `gitlib_diff` | `path?, ref?, from?, to?, staged?, max_lines?` | A unified diff of unstaged or staged changes, one commit against its parent, or two commits. |
| `gitlib_blame` | `path, from_line?, to_line?, ref?` | Who last changed each line, and in which commit, a window at a time. |
| `gitlib_branch` | `action, name?, new_name?, start_point?, switch_to?, path?` | `list`, `create`, `switch` or `rename` branches. A switch never overwrites local changes. |
| `gitlib_stage` | `action, paths, path?` | `stage` or `unstage` paths, or `.` for everything under `path`. |
| `gitlib_commit` | `message, amend?, allow_empty?, path?` | Commits what is staged as `user.name` / `user.email`. |
| `gitlib_stash` | `action, message?, index?, include_untracked?, path?` | `push`, `pop`, `apply` or `list` stashes. |
| `gitlib_discard` | `paths?, ref?, path?` | Throws away uncommitted changes: the paths named go back to `ref`; with none, a hard reset (untracked files stay). |
| `gitlib_delete` | `kind, name?, index?, path?` | Removes a local `branch` (never the checked-out one), a `tag`, or a `stash` by index. |

</details>

<details>
<summary><b>💎 Obsidian</b></summary>

### Obsidian

The vault tools work on the vault's files directly: no plugin, no network, and Obsidian needn't be running. Notes are found by name, `[[wikilink]]`, alias or path; inline tags and frontmatter properties both count. Dot-folders are ignored, line endings are kept, and only `vault_delete` uses `.trash`.

| Tool | Arguments | What it does |
|---|---|---|
| `vault_search` | `query, tag?, folder?, max_results?` | Every line holding the text (any case), as `path:line`, and every note whose name or alias holds it, optionally within a tag or folder. |
| `vault_list` | `what?, folder?, tag?, property?, value?, max_results?` | Notes by folder, tag or property (`property: status, value: draft`); `what: tags` / `properties` lists every tag or key with its count. |
| `vault_read` | `note, heading?, start_line?, max_lines?` | A note with its properties, one heading's section, or a window of lines. |
| `vault_links` | `note` | The note's outgoing links and embeds (resolved or *unresolved*) and every backlink with its line. |
| `vault_daily` | `date?, append?` | The daily note for a day (`today`, `yesterday`, `+3`, `2026-09-22`), per the vault's Daily notes settings, created from its template if missing; `append` adds to its end. |
| `vault_write` | `note, content, mode?, heading?` | Writes a note: `create` (a bare name goes where Obsidian puts new notes), `overwrite`, `append` or `prepend`, to the note or one heading's section. |
| `vault_properties` | `note, set?, remove?` | Lists, sets or removes properties in one write; only the named keys change. |
| `vault_move` | `note, to` | Renames or moves a note and rewrites every link to it. |
| `vault_delete` | `note` | Moves a note or attachment into the vault's `.trash` and lists notes still linking to it. Never a folder or anything in a dot-folder. Only while *Obsidian allow delete (.trash)* is on. |

</details>

<details>
<summary><b>🪟 SQL</b></summary>

### SQL

Read-only queries against SQL Server over named connections, with no ODBC driver (`Microsoft.Data.SqlClient`). Connections live in `sql.json`: the home folder's is read by every profile, and the profile's wins a name clash.

#### Connection settings

* **`server`**: `host`, `host,port` or `host\instance`.
* **`auth`**: `sql` (a SQL login: `user` and `password`), `windows` (your account), or `runas` (another Windows account, `DOMAIN\name` or `name@domain`, plus `password`; like `runas /netonly`, it signs in to the server only as that account).
* **`encrypt`**: `strict`, `mandatory` (default) or `optional`. **`trustServerCertificate`**: `true` accepts a self-signed certificate.
* **`connectTimeoutSeconds`**: 1–120 (default 15).
* **`passwordStore`**: `file` (default; a password typed into the file is encrypted in place with DPAPI on the next read) or `credman` (Windows Credential Manager, `NeonSidekick/sql/<connection_name>`).

#### Managing connections

* **SQL add connection** (the SQL tab of `/tools`) walks through a new connection one page per choice. Its summary can **test** the draft (`SELECT @@VERSION`, nothing written) and saves it with the file's comments kept, offered or hidden until ticked. ESC steps back. It only adds; edit the file to change one.
* **SQL set password** updates a password.
* Or edit `%USERPROFILE%\.neonsidekick\sql.json` (global) or `…\profiles\<profile>\sql.json` directly (comments and trailing commas allowed).

```json
{
  "connections": {
    // SQL Auth: Password encrypted in-place by DPAPI after first read
    "adventureworks": {
      "server": "127.0.0.1,1433",
      "database": "AdventureWorks2022",
      "auth": "sql",
      "user": "reader",
      "password": "type-password-here-once",
      "encrypt": "mandatory",
      "trustServerCertificate": true,
      "description": "Sample sales database"
    },
    // Windows Auth: Current account, no password required
    "reports-me": {
      "server": "sqlhost01.example.com,1453",
      "database": "Reports",
      "auth": "windows",
      "encrypt": "mandatory"
    },
    // RunAs Auth: Alternate account with password in Windows Credential Manager
    "reports-admin": {
      "server": "sqlhost01.example.com,1453",
      "database": "Reports",
      "auth": "runas",
      "user": "CONTOSO\\svc-reader",
      "passwordStore": "credman",
      "encrypt": "mandatory"
    }
  }
}
```

#### Safety

* SQL Server's own parser (ScriptDom) lets through only a single `SELECT` (or a `WITH` ending in one); batches, DDL, `EXEC`, `INTO`, `DELETE` and linked servers are refused before reaching the server.
* It runs in a read-only-intent transaction that is always rolled back. Still give the login read-only permissions.
* Values go in as `@name` parameters. Results come back as a Markdown table, floats at full precision; CLR types (`geography`, `hierarchyid`) need `.ToString()`.

| Tool | Arguments | What it does |
|---|---|---|
| `sql_connections` | — | The named connections (server, database, sign-in, description), the default marked. Touches no server. |
| `sql_databases` | `connection?` | The databases the login may open, with state, compatibility level and collation. |
| `sql_tables` | `connection?, database?, schema?, pattern?` | Tables and views as `schema.name`, with kind, approximate rows and `MS_Description`. `pattern` is text in the name or a `LIKE` pattern (`%`, `_`, `*`). |
| `sql_columns` | `pattern, connection?, database?, schema?` | Every column whose name matches (`EmailAddress`, `%CustomerID`): table, type, nullability, description. |
| `sql_describe` | `table, connection?, database?` | One table or view in full: description, columns (type, nullability, identity, computed, default, key), foreign keys both ways, indexes, CHECK constraints and triggers. A bare name finds the one schema with it. |
| `sql_relationships` | `connection?, database?, table?` | Foreign-key join paths as `from_table.from_column -> to_table.to_column`, all or touching a table. |
| `sql_indexes` | `connection?, database?, table?, schema?, missing?` | The indexes of a table, schema or database: kind, key and included columns, filter, size, and seeks, scans, lookups and updates since restart (unread ones marked). `missing: true` adds the optimizer's suggestions. Usage needs `VIEW SERVER STATE`. |
| `sql_query` | `sql, connection?, database?, params?, max_rows?` | One read-only `SELECT`. `params` is an object (`{"id": 43659}` for `@id`); `max_rows` is 1–100000 (*SQL max rows* by default). Cut at *SQL query result max chars*. |

`--sql-check <connection>` proves the tools against a real server on the published exe (the sign-in, every type, the gate, the rollback, a cancel and a timeout).

</details>

<details>
<summary><b>🔮 Oracle</b></summary>

### Oracle

The SQL tools' twin for Oracle, through ODP.NET Core (fully managed; no Oracle Client needed), over `oracle.json` (home and profile files, as for SQL).

#### Connection settings

* **`dataSource`**: EZConnect `host:port/service` (`localhost:1521/FREEPDB1`; port 1521 by default) or a whole `(DESCRIPTION=…)`.
* **`user`**: the database user. `SYS` (and any `AS SYSDBA` sign-in) is refused, since Oracle doesn't hold SYS to a read-only transaction.
* **`schema`**: the default schema for calls (the user's own by default).
* **`connectTimeoutSeconds`**: 1–120 (default 15).
* **`passwordStore`**: `file` or `credman` (`NeonSidekick/oracle/<connection_name>`), as for SQL.

#### Managing connections

**Oracle add connection** (the Oracle tab of `/tools`) walks through a new connection. Its test (nothing written) shows who it signed in as, the container and the version, and warns when the account could change data: the tools never write, but a read-only account is the real guard. **Oracle set password** updates a password; or edit `oracle.json` directly.

```json
{
  "connections": {
    // Password encrypted in place by DPAPI after first read
    "hr": {
      "dataSource": "localhost:1521/FREEPDB1",
      "user": "hr_reader",
      "password": "type-password-here-once",
      "schema": "HR",
      "description": "The sample human-resources schema"
    },
    // A full descriptor, the password in Windows Credential Manager
    "ledger": {
      "dataSource": "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=dbhost01.example.com)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=LEDGER)))",
      "user": "ledger_ro",
      "passwordStore": "credman"
    }
  }
}
```

#### Safety

`oracle_query` runs one read-only statement behind four layers:

1. **The gate.** The text is lexed (comments, literals, quoted names and binds understood) and only one `SELECT` or `WITH … SELECT` passes. Refused: a second statement, PL/SQL, `FOR UPDATE`, `INTO`, `NEXTVAL`, database links, external tables and `BFILENAME`, DML and DDL words, and packages that reach outside (`UTL_HTTP`, `UTL_FILE`, `DBMS_SQL`…).
2. **The session.** On 23ai and later, `ALTER SESSION SET READ_ONLY = TRUE`: the server refuses any write.
3. **The transaction.** `SET TRANSACTION READ ONLY`, always rolled back.
4. **The account.** Give the user only `SELECT` grants (or `ALTER USER … READ ONLY` on 23ai).

Values go in as `:name` parameters. A `NUMBER` past 28 digits keeps every digit; a CLOB or BLOB shows its start and length; object types (`SDO_GEOMETRY`, `XMLTYPE`) need converting to text. Oracle's own schemas are left out of the listings.

| Tool | Arguments | What it does |
|---|---|---|
| `oracle_connections` | — | The named connections (data source, user, schema, description), the default marked. Touches no server. |
| `oracle_schemas` | `connection?` | The schemas the account can see, with table and view counts; its own is marked. |
| `oracle_tables` | `connection?, schema?, pattern?` | Tables and views as `SCHEMA.NAME`, with kind, the optimizer's row count and comment. `pattern` is text in the name (any case) or a `LIKE` pattern with `%` or `*`. |
| `oracle_columns` | `pattern, connection?, schema?` | Every column whose name matches: table, type, nullability, comment. |
| `oracle_describe` | `table, connection?, schema?` | One table or view in full: comment, columns (type, nullability, identity, virtual, default, key, comment), foreign keys both ways, indexes, CHECK constraints and triggers. |
| `oracle_relationships` | `connection?, schema?, table?` | Foreign-key join paths: all, a schema's or a table's. |
| `oracle_indexes` | `connection?, table?, schema?` | Indexes: kind, key columns, status, visibility, the optimizer's counts, and recorded use where `DBA_INDEX_USAGE` is readable. |
| `oracle_query` | `sql, connection?, schema?, params?, max_rows?` | One read-only `SELECT` (`FETCH FIRST n ROWS ONLY`, no trailing `;`). `params` as for SQL (`:id`); `max_rows` 1–100000. Cut at *SQL query result max chars*. |

`--oracle-check <connection>` proves the tools against a real database on the published exe (every type, the read-only layers, a cancel and a timeout).

</details>

<details>
<summary><b>🐬 MySQL and MariaDB</b></summary>

### MySQL and MariaDB

The same tools for MySQL 8.0.16+ and MariaDB 10.2+, through MySqlConnector (fully managed, MIT), over `mysql.json` (home and profile files, as for SQL).

#### Connection settings

* **`host`**, **`port`** (3306 by default), **`database`** (the default for calls; without one, the listings cover every database the user sees).
* **`user`**, and **`passwordStore`** `file` or `credman` (`NeonSidekick/mysql/<connection_name>`), as for SQL.
* **`sslMode`**: `preferred` (default), `required`, `verify-ca`, `verify-full` or `none`.
* **`allowPublicKeyRetrieval`**: `true` only for a `caching_sha2_password` account without TLS (off by default; a man in the middle could supply its own key).
* **`connectTimeoutSeconds`**: 1–120 (default 15).

```json
{
  "connections": {
    "shop": {
      "host": "localhost",
      "port": 3306,
      "database": "shop",
      "user": "shop_reader",
      "password": "type-password-here-once",
      "description": "The sample retail database"
    },
    "billing": {
      "host": "db01.example.com",
      "database": "billing",
      "user": "billing_ro",
      "passwordStore": "credman",
      "sslMode": "verify-full"
    }
  }
}
```

**MySQL add connection** (the MySQL tab of `/tools`) walks through a new one and can **test** it: who it signs in as, the version, and a warning when `SHOW GRANTS` allows changes. **MySQL set password** updates a password.

#### Safety

1. **The gate.** The text is lexed by MySQL's rules and only one `SELECT` or `WITH … SELECT` passes. Refused: a second statement, executable comments (`/*! … */`), `INTO`, locking reads, `LOAD_FILE`, named locks, MariaDB sequence moves, and DML and DDL words.
2. **The session.** `sql_mode` drops `NO_BACKSLASH_ESCAPES` and `ANSI_QUOTES`, so the server reads strings as the gate did, and the server caps each statement's run time. The driver never runs `LOAD DATA LOCAL`.
3. **The transaction.** `START TRANSACTION READ ONLY`, always rolled back.
4. **The account.** Give the user `SELECT` grants alone.

| Tool | Arguments | What it does |
|---|---|---|
| `mysql_connections` | — | The named connections (host, database, user, description), the default marked. Touches no server. |
| `mysql_databases` | `connection?` | The databases the account can see, with table and view counts and character set. |
| `mysql_tables` | `connection?, database?, pattern?` | Tables and views as `database.name`, with kind, approximate rows and comment. |
| `mysql_columns` | `pattern, connection?, database?` | Every column whose name matches: table, type, nullability, comment. |
| `mysql_describe` | `table, connection?, database?` | One table or view: comment, columns (type, nullability, auto_increment, default, key, comment), foreign keys both ways, indexes, CHECK constraints and triggers. |
| `mysql_relationships` | `connection?, database?, table?` | Foreign-key join paths: a database's or a table's. |
| `mysql_indexes` | `connection?, database?, table?` | Indexes: kind, key columns, cardinality, and reads and writes since restart where `performance_schema` allows. |
| `mysql_query` | `sql, connection?, database?, params?, max_rows?` | One read-only `SELECT` (`LIMIT n`). `params` as for SQL (`@id`); `max_rows` 1–100000. Cut at *SQL query result max chars*. |

`--mysql-check <connection>` proves the tools against a real server on the published exe (every type, the gate, the session, the transaction, a cancel and a timeout).

</details>

<details>
<summary><b>🔗 UNC shares and outside folders</b></summary>

### UNC shares and outside folders

The UNC tools reach named network shares (`\\server\share`, or a folder under one) and local folders outside the working directory (`D:\Data`), without mapped drives, as you (`windows`) or as another Windows account (`runas`, like `runas /netonly`). Shares are read-only unless two keys say otherwise. They live in `unc.json` (home and profile files, as for SQL).

#### Share settings

* **`path`** (required): `\\server\share`, a folder under it, or a full local path. In JSON, double the backslashes (`"\\\\fs01\\eng"`) or use `/` (`"//fs01/eng"`). Device paths (`\\?\…`), relative paths and whole drives (`D:\`) are refused.
* **`auth`**: `windows` (default) or `runas`, with **`user`** (`DOMAIN\name` or `name@domain`) and **`passwordStore`** `file` or `credman` (`NeonSidekick/unc/<share_name>`).
* **`access`**: `read` (default) or `readwrite`. Changes need `readwrite` **and** *UNC writes* on, both checked at every call.
* **`description`**: what the share holds; the model reads it to choose.

```json
{
  "shares": {
    "eng": { "path": "//fs01/eng", "description": "Engineering specs and drawings" },
    "finance": {
      "path": "//fs02.corp.local/finance/reports",
      "auth": "runas",
      "user": "CORP\\svc_reader",
      "passwordStore": "credman"
    },
    "data": { "path": "D:/Data", "access": "readwrite" }
  }
}
```

**UNC add share** (the UNC tab of `/tools`) walks through a new one and can **test** it by listing its root under its account. **UNC set password** updates a runas password.

#### How it signs in

* A `runas` share gets its own logon session for each call, so it never collides with your mapped drives and leaves nothing in `net use`. A wrong password shows only when the server is reached ("unknown account or wrong password").
* A `windows` share uses whatever your sign-in has for that server (a mapped drive's credentials, or a `cmdkey /add:server` entry).
* Use UNC paths, not mapped drive letters (a runas token or an elevated app may not see the mapping), and server names, not IP addresses (an address falls back from Kerberos to NTLM).
* `runas` against `\\localhost` proves nothing: Windows signs a loopback session in as you.

#### Safety

* **Paths** are relative to the share's root (or full paths under it), never above it. Streams (`file.txt:secret`) are refused, and walks don't follow reparse points, so add a DFS link's target as its own share.
* **Changes are permanent:** an overwrite replaces the file in place (keeping its permissions), and `unc_delete` deletes for good. `unc_delete` starts off even under *UNC writes*. Every change is logged with the share, the account and the path.
* **Budgets:** a search reads at most 256 MB with four readers and 100,000 entries, then says it stopped. A share that doesn't answer in 10 seconds is given up on.
* The shell can't use a runas share's sign-in, and `open` refuses a network runas share: `unc_fetch` the file, then open the copy.

| Tool | Arguments | What it does |
|---|---|---|
| `unc_shares` | `check?` | The named shares (path, account, access, description), the default marked; `check` lists each root now. |
| `unc_search` | `share?, text?, path?, files?, regex?, context?, output?, order?, limit?, depth?` | `search_files` on a share. |
| `unc_info` | `share?, path?` | A file's size, dates, lines and words; a folder's counts. |
| `unc_read` | `share?, path, start_line?, max_lines?` | Reads a text file, whole or in part. |
| `unc_fetch` | `share?, path, to?, overwrite?` | Copies a file or folder into the working directory (with the File tools on), up to 5,000 files and 500 MB. |
| `unc_write` | `share?, path, content, mode?` | Writes a text file: `create` (default), `overwrite` or `append`. |
| `unc_patch` | `share?, path, old_text, new_text, replace_all?` | Changes part of a text file. |
| `unc_create_directory` | `share?, path` | Creates a folder. |
| `unc_move` / `unc_copy` | `share?, from, to, overwrite?` | Moves, renames or copies within one share. |
| `unc_delete` | `share?, path` | Deletes a file or folder, permanently. Off by default. |
| `unc_put` | `from, share?, to?, overwrite?` | Copies a file or folder from the working directory onto a share (with the File tools on). |

`share` may be left out when `path` is a full path under a share; otherwise the default share is used. `unc_write` through `unc_put` appear only under both keys.

`--unc-check <share>` proves the tools against a real share on the published exe (its reach, the runas token, a listing and a search).

</details>

<details>
<summary><b>🐳 Docker</b></summary>

### Docker

The Docker tools use the engine's own API on its named pipe (*Docker engine pipe*); the app never starts `docker.exe` or Docker Desktop, and says when Desktop isn't running. A container is named by its name, a unique part of one ("mysql" for `mysql_dev`), or an id prefix of four or more characters; an ambiguous name comes back as a question.

* **Changes need two keys:** *Docker writes* offers the changing tools, and every call asks on the pane ("Stop container mysql_dev (mysql:8.4, Up 3 days)?"); a prune says first how much it frees. Headless refuses them; `/docker` still works.
* **Removals are opt-in:** `docker_remove` and `docker_prune` start off even under *Docker writes*.
* **Secrets:** `docker_inspect` shows environment variable names, never values, and hides the values of secret-sounding labels and flags and passwords in URLs. Logs can't be redacted, so the model is told never to repeat a secret from one.
* **Audit:** every change, the model's or yours, is logged.
* **Compose:** a project is the containers with its `com.docker.compose.project` label. `docker_lifecycle` with `scope: project` acts on them all in dependency order. `compose up` needs the CLI and isn't offered.

| Tool | Arguments | What it does |
|---|---|---|
| `docker_containers` | `all?, filter?, project?` | The containers, running first: name, state and health, status, image, ports, compose project, short id. `all: false` leaves out stopped ones. |
| `docker_logs` | `container, tail?, since?, grep?, stream?, timestamps?` | The last `tail` lines (100, up to 2000), since an age (`10m`, `2h`, `1d`) or moment, stdout or stderr only. `grep` searches the last 5000. Colour codes removed; at most 16,000 characters, oldest cut first. |
| `docker_inspect` | `container` | One container in detail (state, health, exit code, restarts, image, command, environment names, ports, mounts, networks, restart policy, limits, compose project, labels), secrets hidden. |
| `docker_stats` | `container?` | CPU (as `docker stats` shows it), memory against its limit, network and disk traffic, processes; one container or all running. |
| `docker_resources` | `kind, filter?, unused?` | `images` (tags, size, age, users), `volumes` (who mounts each), `networks` (subnets, members) or `disk` (space per kind and what a prune would free). |
| `docker_compose` | `project?` | The compose projects: folder, files, services and their state. |
| `docker_lifecycle` | `target, action, scope?, timeout_seconds?` | `start`, `stop`, `restart`, `pause` or `unpause` a container or, with `scope: project`, a compose project. A stop waits `timeout_seconds` (10, up to 120) before the kill. Asks first. |
| `docker_pull` | `image, tag?` | Pulls a public image (`nginx`, `postgres:16`, `ghcr.io/owner/app`); no registry credentials are sent. Asks first. |
| `docker_remove` | `kind, name, force?` | Removes one container (keeping its anonymous volumes), image or volume. `force` removes a running container or a used image. Asks first; off by default. |
| `docker_prune` | `kind, all?` | Removes stopped containers, untagged images (every unused one with `all`), empty networks, anonymous unused volumes (named too with `all`) or the build cache. Asks first, with count and size; off by default. |

#### Docker servers

Containers serving an OpenAI-compatible API (vLLM, SGLang…) can be `/server` choices, one running at a time so two models never fight over the GPU. Tick them in *Docker server containers* on `/settings` › Docker and turn on *Docker servers enabled*. This needs neither the Docker tools nor *Docker writes*, and touches only the ticked containers.

* **Rows:** `/server` lists one **Docker** row per chosen container with its state, image and ports (`running · vllm/vllm-openai:latest · :8000`). `/server docker` lists them alone; `docker:<container>` (also for `--url` and `NEONSIDEKICK_LLM_URL`) picks one.
* **Switching:** picking one stops every other chosen container still running (waiting up to *Docker server stop timeout* plus 15 s each), waits *Docker server post-stop delay*, then starts or unpauses it. If a stop fails, nothing starts. The spinner shows each step; Ctrl+C or a double-click on it cancels.
* **Readiness:** the app polls `/v1/models` on the container's published TCP ports every second until one answers (minutes, for a large model). A container that exits while loading reports its exit code and last log lines; past *Docker server ready timeout* the switch fails and the container keeps running.
* **Model:** whatever the container serves (the saved *LLM model* if listed, else its first), with the context window it reports. A container serving just one model has its id saved as *LLM model* at each connect, so the setting names it; `--model` or `NEONSIDEKICK_LLM_MODEL` wins and is never saved over, and a headless run saves nothing.
* **Leaving:** picking any other server stops the chosen containers first (before an embedded model loads), including one started outside the app. A profile switch does the same. *Docker server stop on exit* stops the one in use when the app exits.
* **Bots:** a `/botchat` bot pointing at a container shares it if it's the one running, and never starts or stops one.

`--docker-check` proves the tools against the real engine on the published exe (the API version, the containers, a redacted inspect, a log and a stats sample).

</details>

<details>
<summary><b>🏠 Home Assistant</b></summary>

### Home Assistant

The Home Assistant tools control your own Home Assistant over its REST API with a long-lived token (*Home Assistant URL*, *Home Assistant API key*): lights and scenes, a TV, sensors, to-do lists, whatever it has connected.

* **Names, not ids:** "the den", "kitchen and hallway", "Den Corner Lamp" or "all". A room goes to its group light when it has one, else to every light in it. An ambiguous name comes back as a question, an unknown one with close matches, so the model never guesses an id.
* **The action policy:** under `ask`, safe services run and anything else shows the service, device and data on the pane first. A no tells the model not to retry.
* **Fresh states:** read at most every 30 seconds, and again after any change.

| Tool | Arguments | What it does |
|---|---|---|
| `ha_overview` | — | Lights on per room, light groups, media players, temperatures, motion, low batteries (under 20%), to-do lists, the scene count and unavailable lights. |
| `ha_states` | `query?, domain?, area?` | Entities, one line each (id, name, state, and what matters for the domain), narrowed by words, domain and room (at most 80). An exact entity id gives every attribute. |
| `ha_history` | `entity, hours?` | One entity's states over the last 1–336 hours (24 by default), oldest first. |
| `ha_lights` | `target, action?, brightness_pct?, color_name?, color_temp_kelvin?, transition?` | `on` (default; also changes brightness or colour), `off` or `toggle`, with a colour name or white temperature (1500–9000 K) and a 0–300 s fade. |
| `ha_scene` | `scene, transition?` | Activates a scene by name or id. |
| `ha_media` | `action, target?, volume_pct?, source?` | `on`, `off`, `volume`, `volume_up`, `volume_down`, `mute`, `unmute`, `source`, `play`, `pause`, `play_pause`, `stop`, `next`, `previous`. The target may be left out when there's one player. A source matches by name or prefix (`hdmi 3` → `HDMI 3 (eARC/ARC)`). |
| `ha_todo` | `action, item?, list?` | `list` (allowed under every policy), or `add`, `complete` or `remove` an item. |
| `ha_call_service` | `domain, service, entity?, data?` | Any other service (`remote.send_command` with `{"command": "Home"}`, `button.press`, `script.turn_on`), subject to the policy. |
| `ha_assist` | `text` | Hands a sentence to Home Assistant's Assist agent, as a last resort. Refused under policy `off`; Assist reaches only entities exposed to it. |

</details>

<details>
<summary><b>🖨️ Printing</b></summary>

### Printing

`/print` and the print tools send work to any printer Windows has installed.

* **Drawn by the app:** text and code print as a monospace listing (tabs as four columns, long lines wrapped, form feeds start pages); Markdown prints formatted (headings, emphasis, lists, quotes, code blocks, rules, links with their address, tables cut to fit); a picture (PNG, JPEG, GIF, WebP, BMP) is fitted to one page, never enlarged. Every page carries the file's name, the time and *page N of M*; paper, tray and quality are the printer's.
* **Anything else** (a PDF, a Word or Excel file) goes to the program Windows has for printing it, on the default printer only; a printer, copies, pages or landscape given with it is refused. A type nothing can print is refused.
* **The policy:** under `ask`, `print_file` shows the file, printer and sheets and waits for your yes. A no tells the model not to retry.

| Tool | Arguments | What it does |
|---|---|---|
| `list_printers` | — | The installed printers, marking the Windows default and *Print default printer*. Available in plan mode. |
| `print_file` | `path, printer?, copies?, pages?, landscape?` | Prints a file from the working directory: a printer by name (else *Print default printer*, else the Windows default), 1–10 copies, pages like `1-3`, `4-` or `1,3,5`, and landscape. Not in plan mode. |

#### Making PDFs

`/pdf` and the `convert_to_pdf` file tool make a PDF in the working directory. Nothing new is installed: the browser you already have makes it, or Windows' own PDF printer.

* **Markdown** keeps its headings, emphasis, lists and task lists, quotes, tables, links (clickable), footnotes and code blocks, coloured as in the transcript. Its pictures come from the working directory, relative to the file; a picture outside it or on the web shows as `[image: alt]`. Raw HTML in it is shown as text.
* **Text and code** become one listing, coloured by the file's extension. **A picture** is fitted to one page, never enlarged.
* **An HTML file** is printed as the page, after its scripts, frames and embeds are taken out; its stylesheets and pictures come from the working directory only.
* **A web page** is printed as the browser shows it, after *Web browser network mode* allows its address (the page's own requests are not checked beyond that). It keeps its own layout, so paper and landscape are not taken.
* **The pages:** US Letter by default (`paper=` A4 or Legal), portrait unless landscape, with the title at the top left and *page N of M* at the top right.
* **The engine** (*PDF engine*): Edge, Chrome or Brave (*Web browser path*, else the first found) prints the page; it gets a minute. Without one, or when it fails under `auto`, **Microsoft Print to PDF** draws Markdown, text and pictures as `/print` would: black and white, two fonts, on the driver's paper. HTML and web pages need the browser.
* **The output** goes beside the source with `.pdf` (a web page or Markdown text at the top: `example.com-intro.pdf`, `reply-2026-10-03-1405.pdf`), or where `to` says (a file, or a folder). An existing PDF is replaced only with `overwrite`. A PDF, or a file that is neither text nor a picture, is refused.
* `convert_to_pdf` is a file tool, so *File tools* decides; a `url` needs *Web tools* as well. `/pdf` needs neither.

</details>

<details>
<summary><b>🎨 Images (ComfyUI)</b></summary>

### Images (ComfyUI)

The image tools run **your own ComfyUI workflows** on your server (*ComfyUI URL*), save the pictures under the working directory, and show them to the model in the next message.

#### Adding a workflow

**With the wizard** (*ComfyUI add workflow* on the ComfyUI tab of `/tools`):

* **Build** makes a standard text → image or image → image workflow from your server's lists: checkpoint, family, folder (this profile's or every profile's), name, CLIP skip, sampler, scheduler, size, steps, CFG, denoise, negative and description.
* **Import** takes a ComfyUI API export (*Workflow → Export (API)*). It finds the prompt, negative, seed, steps, CFG, size and input-image nodes, puts the placeholders in, and keeps the export's values as defaults. It reads both a plain `KSampler` graph and FLUX.2's custom-sampler graph. A value fed by a primitive node gets its placeholder there; one set by another node (a switch, a resolution picker) is left alone, and the wizard says so.
* FLUX.2, Krea 2, Z-Image, Qwen Image, Ernie Image, Boogu, LongCat Image, HiDream I1 and Ideogram 4 load separate model files that **Build** can't wire; export ComfyUI's own template and **Import** it.
* The summary can **test** the draft (one small run, at most 512 px and 8 steps, nothing saved) and saves `<name>.json` + `<name>.md`, offered or hidden until ticked. ESC steps back.

**By hand:**

1. Build the workflow in ComfyUI and export it in the **API format** (*Workflow → Export (API)*, or *Save (API)* in dev mode). A regular save, with `nodes` and `links`, is refused.
2. Put placeholders where the call's values go, and drop the file into `<profile>\comfy\` (this profile) or `<home>\comfy\` (every profile; the profile's wins a name clash). The file name is the workflow's name; workflows are re-read at every call.

| Placeholder | Becomes |
|---|---|
| `{{prompt}}` | The positive prompt. Required unless the workflow takes an input picture (a face swap or upscale, listed as *no prompt*). |
| `{{negative}}` | The negative prompt. |
| `{{seed}}`, `{{width}}`, `{{height}}`, `{{steps}}`, `{{cfg}}`, `{{denoise}}` | A number when it is the whole value (`"seed": "{{seed}}"`), or text inside a longer string (`"neon-{{seed}}"`). |
| `{{image}}` | The uploaded input picture's name, for a `LoadImage` node (img2img, upscale, inpaint). |
| `{{image2}}`, `{{image3}}` | The second and third input pictures (a face swap's face, Qwen-Image-Edit's pictures). They fill in order, or the workflow is skipped. |
| `{{!name}}` | A literal `{{name}}`, for nodes that use double braces themselves (Ideogram 4's `StringReplace`). **Import** escapes these for you. |

3. Optionally, a sidecar `<name>.md` beside it sets the defaults and tips:

```markdown
---
description: Anime portraits on Pony Diffusion XL
family: pony          # pony, illustrious, juggernaut, sdxl, flux, flux2, flux2klein, krea2, zimage, qwenimage, sd35, ernie, boogu, longcat, hidream, ideogram4, sd15 or other (guessed from the file name otherwise)
width: 832
height: 1216
steps: 25
cfg: 7
negative: score_6, score_5, score_4, blurry
reinforce: false      # optional: send this negative as written, nothing appended (ComfyUI reinforce negatives)
image: the picture to restyle   # optional, per input picture: its role, shown to the model (image, image2, image3)
---
Prefer source_anime; keep rating_safe unless asked.
```

#### Prompts

- **Describe what you want** ("a cozy neon ramen stall at night") and the model writes the prompt in the family's style (below), adding the family's default negative unless the sidecar names one.
- **Give your own prompt** ("use this prompt: score_9, …") and the model passes it unchanged (`verbatim: true`), with nothing added.
- **Skip the model:** `/imagine score_9, score_8_up, source_anime, 1girl -- score_4, blurry --seed 42`.
- **Limit the choice** with *ComfyUI workflows offered*.

| Family | Prompt style the model writes |
|---|---|
| Pony Diffusion XL | `score_9, score_8_up, score_7_up`, a `source_*` tag, then well-known Danbooru tags, weighted like `(tag:1.3)` where it helps; a short phrase only where no tag is specific enough |
| Illustrious XL, NoobAI | `masterpiece, best quality, amazing quality, very aesthetic, absurdres`, then Danbooru tags |
| Juggernaut XL | A photographic description (subject, setting, light, lens) |
| Flux | Plain sentences |
| FLUX.2 dev | Long structured prose with hex colours |
| FLUX.2 Klein | A few clear sentences |
| Krea 2 | One rich paragraph |
| Z-Image Turbo | Concise prose |
| Qwen Image | Long prose with the exact words in quotes (it renders text well) |
| SD3.5 | Sentences plus style phrases |
| Ernie Image, LongCat Image | Rich prose with the words in quotes |
| Boogu | Descriptive sentences |
| HiDream I1 | Long, detailed prose |
| Ideogram 4 | A JSON prompt (description, style, colour palette, laid-out elements) |
| SD 1.5, SDXL | Tags and weights |

#### Several-picture workflows (face swaps)

A workflow can take up to three pictures. For a face swap:

1. Install the ReActor node pack on your ComfyUI server and build the swap (two `LoadImage` nodes → `ReActorFaceSwap` → `SaveImage`).
2. *Export (API)* and **Import** it. The `LoadImage` nodes become `{{image}}` and `{{image2}}` in node-id order (the wizard says which); no sampler is needed.
3. Say which picture is which in its `.md`:

```markdown
---
description: Face swap (ReActor)
image: the picture whose face is replaced
image2: the face to put in
---
```

Then "put my face from [Image #2] on the person in [Image #1]" works in chat, or `/imagine faceswap --image target.png --image2 face.png` without the model. A Qwen-Image-Edit graph (prompt in `TextEncodeQwenImageEditPlus`) needs `{{prompt}}` put in by hand.

#### The tools

| Tool | Arguments | What it does |
|---|---|---|
| `generate_image` | `prompt?, workflow?, negative?, negative_extra?, verbatim?, width?, height?, seed?, steps?, cfg?, denoise?, image?, image2?, image3?, count?` | Runs a workflow (the only fitting one when none is named), saving 1 to *ComfyUI max pictures per call* pictures, each with the next seed. The result names the files and seed; the pictures follow in the next message, as JPEG unless transparent. `prompt` may be left out only for a workflow without `{{prompt}}`. |
| `set_splash_image` | `path, name?` | Copies a picture from the working directory into the profile's `splash` folder, so it shows at start and on `/splash`. The first one replaces the bundled set. |

* A refused run names the node and input at fault (a missing checkpoint, a bad value).
* `image` is a path under the working directory or a pasted picture's `[Image #N]`. A pasted picture goes at its original size and is saved first into the output folder's `.pasted\` subfolder.
* `image2` and `image3` take the roles the workflow's `.md` names. Without a workflow name, the one taking that many pictures is used.

</details>

<details>
<summary><b>💻 Shell & Web</b></summary>

### Shell

Runs commands on your machine, starting in the working directory (`workdir` picks a folder under it), guarded by *Shell command policy* and *Shell police outside paths* (see Shell guards). A denied command tells the model not to work around it.

* Commands run hidden, output read as UTF-8 with colours and pagers off, and stdin closed (background processes keep it for `process`).
* A command that times out is killed with everything it started. Background processes stop when the app closes; a crash leaves running commands running.

#### Headless runs

[HEADLESS.md](HEADLESS.md) has worked examples of every flag, the commands that work headless, yolo runs and scheduled jobs.

* `--headless` has no approval pane, so under `ask` only allow-listed commands run; the model is told to say what couldn't run.
* If any command was refused, the run ends with `[notice] N commands were not run: …` and **exit code 3**. 0 means nothing was refused; 2 means a bad argument or unknown profile.
* `--yolo` (or `NEONSIDEKICK_COMMAND_POLICY=yolo`; the flag wins) allows every command for one launch, never saved. The path police still applies unless `--no-police` (or `NEONSIDEKICK_SHELL_POLICE=off`) lifts it; together they leave no guard at all.
* A headless run loads `default`, not the TUI's last profile; `--profile <name>` (or `NEONSIDEKICK_PROFILE`) names another for this launch only.

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --yolo --cwd D:\Repo\MyApp
Get-Content job.txt | NeonSidekick.exe --headless --profile work
```

| Tool | Arguments | What it does |
|---|---|---|
| `run_command` | `command, shell?, workdir?, timeout?, background?, notify?` | Runs a command in `powershell` (default), `cmd` or `bash`, returning `exit N in T s (shell)…`, stdout and stderr. `background` (or a long timeout) returns a `proc_…` id; `notify` shows `⚡` when it exits and queues a `process poll` for the next turn. |
| `execute_code` | `language, code, timeout?` | Runs a one-off `python`, `node` or `powershell` script, approved once per language per session; nothing carries over between scripts. With *Shell tool bridge*, the script can call the app's tools (Python `from neon_tools import call`, Node `await neon.call(...)`, PowerShell `Invoke-NeonTool`), except `execute_code` and `ask_user`; `run_command` through the bridge still needs approval and can't run in the background. |
| `process` | `action, session_id?, data?, timeout?, offset?, limit?` | Manages up to 16 background processes (and the last 64 finished), named by any unique id prefix: `list`, `poll`, `log` (a window of the last 5,000 lines), `wait`, `kill` (with children), `write` / `submit` (to stdin; `submit` adds a newline), `close`. |

### Web

| Tool | Arguments | What it does |
|---|---|---|
| `web_search` | `query, max_results?` | Searches the web (DuckDuckGo or SearXNG): title, URL, snippet. |
| `web_fetch` | `url, offset?` | A page's readable content as Markdown, 32,000 characters at a time; also plain text, JSON, XML and CSV. |
| `open_url` | `url?, urls?` | Opens a link, or up to five, in your browser. |
| `download_file` | `url, path?, overwrite?` | Downloads a file into the working directory, up to *Web download max (MB)*, streamed to disk. Needs *File tools* too. |

</details>

<details>
<summary><b>🧠 Memory, Skills & Sessions</b></summary>

### Memory

| Tool | Arguments | What it does |
|---|---|---|
| `save_memory` | `text` | Saves one lasting fact about you, known in every later session. |
| `recall_memory` | — | Everything remembered, oldest first. Seeded at the start of every conversation. |

### Skills

| Tool | Arguments | What it does |
|---|---|---|
| `load_skill` | `name, file?` | Loads a skill's instructions (the list is in the system prompt), or one of its files, up to 64,000 characters. Offered only while a skill is installed. |
| `skill_editor` | `action, scope?, name, description?, instructions?, path?, content?, old_text?, new_text?, replace_all?, summary?` | `create` or `update` a skill under `profile` (default) or `global`. For supporting files, `write_file` writes a whole file and `edit_file` swaps `old_text` for `new_text`; `path` is relative to the skill folder. Never touches `.neon-source.json`, `.git` or `node_modules`, never edits external skills, and never deletes. |

### Sessions

| Tool | Arguments | What it does |
|---|---|---|
| `session_manager` | `action, query?, id?, max_results?, from_turn?, to_turn?` | `search`, `list` or `read` this profile's earlier conversations (not the current one). Never restores or purges. |

### Claude advisor

| Tool | Arguments | What it does |
|---|---|---|
| `claude_advisor` | `question, context?` | Asks Claude Code for read-only advice; it can read the working directory and the web. The transcript shows the question, Claude's tools, the answer and a cost footer. One advisor conversation per session, separate from `/claude`'s. ESC stops it with the reply; `/usage` counts it. |

### Camera

| Tool | Arguments | What it does |
|---|---|---|
| `camera_capture` | `prompt` | Shows the model's request ("Hold the label up to the camera."), then waits for you to take the photo (*Camera shutter* `user`) or for your permission (`model`). The photo is saved in *Camera output folder* and attached after the result; a decline isn't retried that turn. Allowed in plan mode. |

### Questions

| Tool | Arguments | What it does |
|---|---|---|
| `ask_user` | `questions` | Multiple-choice questions on the pane: up to *Ask max questions*, each with 2 to *Ask max choices per question* options, `single` or `multi`, plus *Other…*. ESC declines them all. |

### Plan

| Tool | Arguments | What it does |
|---|---|---|
| `present_plan` | `title, markdown, name?` | Plan mode only. Saves the plan as `.neon/plans/<name>.md` with a `status` / `revision` / `requirement` header, prints it and asks for approval; revisions overwrite the same file. The result is your verdict. Headless, the plan is saved and `/plan approve` starts it. `status` is `draft`, `approved`, `cancelled`, `done` or `incomplete`. |

</details>

<details>
<summary><b>🔌 MCP servers</b></summary>

### MCP servers

Each connected server is its own tool group, named `<server>__<tool>` (`gateway__get_current_time`) with its server's descriptions. Servers connect in the background (🔌 and a count on the hint row); a reply started meanwhile gets the ones already connected. The Servers tab of `/mcp` switches whole servers, the Tools tab single tools.

</details>

## Environment variables
[↑ Back to top](#neon-sidekick)

Every variable starts with `NEONSIDEKICK_`. Each overrides a setting for one launch and is never saved.

* **Precedence:** command-line flag > variable > saved setting > default. A settings row a variable overrides says so.
* **Values:** blank means unset; values are trimmed and words match in any case. **on/off** also accepts `true`/`false`, `1`/`0` and `yes`/`no`. A value that doesn't parse is logged as a warning and ignored.
* **Logging:** with `--log`, the startup lines list the variables in force (API keys only as `(set)`).

[HEADLESS.md](HEADLESS.md) shows them in use for scripted runs.

<details>
<summary><b>🔧 Click to expand all Environment Variables</b></summary>

### Where and who

| Variable | What it does | Accepts |
|---|---|---|
| `NEONSIDEKICK_HOME` | The home folder: settings, profiles, models, llama.cpp, skills and the connection files. | A folder path. Default `%USERPROFILE%\.neonsidekick`. |
| `NEONSIDEKICK_PROFILE` | The profile for this launch (`settings.json` is left alone). An unknown name exits with code 2; `--profile` wins; a headless run with neither loads `default`. | A profile name. |

### LLM

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_LLM_URL` | LLM URL (`--url` wins) | A base URL (`http://127.0.0.1:1234/v1`), or `embedded`. |
| `NEONSIDEKICK_LLM_MODEL` | LLM model (`--model` wins) | A model id, or an embedded model's id (`gemma-4-e2b`). |
| `NEONSIDEKICK_LLM_API_KEY` | LLM API key | The key as issued. Never logged. |
| `NEONSIDEKICK_LLM_REASONING` | LLM reasoning | `none`, `low`, `medium`, `high`, `xhigh`. |
| `NEONSIDEKICK_LLM_REQUEST_TIMEOUT` | LLM request timeout (s) | Seconds, up to 3600. |
| `NEONSIDEKICK_LLM_TURN_TIMEOUT` | LLM turn timeout (s) | Seconds, up to 21600. |
| `NEONSIDEKICK_LLM_CONTEXT` | LLM context length | Tokens, for servers that don't report their window. |
| `NEONSIDEKICK_LLM_SAMPLING` | LLM sampling, for every model | A JSON object of wire names (`{"temperature":0.6,"top_k":20,"typical_p":0.9}`). Known fields must be in range; other keys go into the extra body. Saved values stand for the rest. |

### Embedded LLM

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_EMBEDDED_BACKEND` | Embedded backend | `auto`, `cuda`, `vulkan`, `cpu`. |
| `NEONSIDEKICK_EMBEDDED_CONTEXT` | Embedded context size | Tokens: 0 (fit) or 512–262144. |

### Shell

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_COMMAND_POLICY` | Shell command policy (`--yolo` wins) | `off`, `ask`, `yolo`. Headless under `ask`, only allow-listed commands run. |
| `NEONSIDEKICK_SHELL_POLICE` | Shell police outside paths (`--no-police` wins) | on/off |
| `NEONSIDEKICK_SHELL_NATIVE` | Shell prefer native tools | on/off |

### Claude

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_CLAUDE_EXE` | Claude executable | The Claude Code CLI's full path. |
| `NEONSIDEKICK_CLAUDE_PERMISSIONS` | Claude slash command permissions | `read-only`, `edit`, `full`. |
| `NEONSIDEKICK_CLAUDE_ADVISOR` | Claude advisor tool | on/off |
| `NEONSIDEKICK_CLAUDE_API` | Claude API | on/off |
| `NEONSIDEKICK_CLAUDE_API_KEY` | Claude API key | The key as issued. Never logged. |
| `NEONSIDEKICK_CLAUDE_CLI_SERVER` | Claude CLI server | on/off |

### OpenAI

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_OPENAI_API` | OpenAI API | on/off |
| `NEONSIDEKICK_OPENAI_API_KEY` | OpenAI API key | The key as issued. Never logged. |

### Speech

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_TTS_URL` | TTS HTTP URL | A Kokoro HTTP server's URL. |
| `NEONSIDEKICK_TTS_VOICE` | TTS voice | A voice name (`af_heart`). |
| `NEONSIDEKICK_TTS_VOICE2` | TTS voice 2 | A voice name. It can't clear a saved second voice; `NEONSIDEKICK_TTS_MIX=100` plays the first alone. |
| `NEONSIDEKICK_TTS_MIX` | TTS voice mix | 0–100. |
| `NEONSIDEKICK_TTS_SPEED` | TTS speed | 0.5–2.0. |
| `NEONSIDEKICK_WHISPER_MODEL` | STT whisper model | A model name or a ggml file's path. |
| `NEONSIDEKICK_INTERRUPT_ECHO` | STT interrupt echo guard | 50–100. |
| `NEONSIDEKICK_INTERRUPT_CONFIRM` | STT interrupt confirm | Milliseconds, 0–2000. |

### Integrations

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_SEARXNG_URL` | Web SearXNG URL | The instance's URL (*Web search method* still picks the engine). |
| `NEONSIDEKICK_OBSIDIAN_VAULT` | Obsidian vault | The folder holding `.obsidian`. |
| `NEONSIDEKICK_COMFY_URL` | ComfyUI URL | The server's URL (`http://gpu-box:8188`). |
| `NEONSIDEKICK_HA_URL` | Home Assistant URL | The server's URL (`http://localhost:8123`). |
| `NEONSIDEKICK_HA_TOKEN` | Home Assistant API key | A long-lived token as issued. Never logged. |
| `NEONSIDEKICK_DOCKER_PIPE` | Docker engine pipe | A bare name, `\\.\pipe\name` or `npipe:////./pipe/name` (what `DOCKER_HOST` holds on Windows). |

### Set by the app

Under *Shell tool bridge*, the app passes `NEONSIDEKICK_BRIDGE_ADDRESS` and `NEONSIDEKICK_BRIDGE_TOKEN` to `execute_code` scripts for the `neon_tools` modules; don't set them yourself. To find shells and interpreters it also reads `PATH`, `PATHEXT`, `ProgramFiles`, `ProgramW6432` and `LocalAppData`.

### Test suite

Only for running the tests from source; each live test is skipped unless its resource is there.

* `NEONSIDEKICK_TEST_LLM_URL`, `NEONSIDEKICK_TEST_TTS_URL`, `NEONSIDEKICK_TEST_SQL_CONNECTION`: a server to test against.
* `NEONSIDEKICK_TEST_ORACLE_CONNECTION`: an ODP.NET connection string (`User Id=…;Password=…;Data Source=localhost:1521/FREEPDB1`) for a user that may create tables (the tests make `NS_*` fixtures once; `gvenzl/oracle-free` works).
* `NEONSIDEKICK_TEST_MYSQL_CONNECTION`: a MySqlConnector connection string (`Server=127.0.0.1;Port=3306;User ID=…;Password=…;Database=…`) to a database the user owns (`ns_*` fixtures; `mysql:8.4` and `mariadb:11` work).
* `NEONSIDEKICK_TEST_UNC_SHARE`: a readable `\\server\share` path (`\\localhost\C$\Windows`); with `NEONSIDEKICK_TEST_UNC_USER` and `NEONSIDEKICK_TEST_UNC_PASSWORD`, a second account for the runas path. Read only.
* `NEONSIDEKICK_TEST_DOCKER_CONTAINER`: a running container to read (`mysql_dev`); `NEONSIDEKICK_TEST_DOCKER_PIPE` names another pipe. Read only.
* `NEONSIDEKICK_TEST_CAMERA`: `1` or a camera's name (the light comes on); `NEONSIDEKICK_TEST_CAMERA_OUT`, a folder to keep the test photo in.
* `NEONSIDEKICK_TEST_HA_URL` with `NEONSIDEKICK_TEST_HA_TOKEN`: a Home Assistant to read from.
* `NEONSIDEKICK_TEST_WHISPER_MODEL`, `NEONSIDEKICK_TEST_SILERO_MODEL`, `NEONSIDEKICK_TEST_VOSK_MODEL`, `NEONSIDEKICK_TEST_KOKORO_MODEL`: a model not under `%USERPROFILE%\.neonsidekick\models`.
* `NEONSIDEKICK_TEST_CLAUDE=1`: the live Claude Code tests (Haiku, a few cents a run).
* `NEONSIDEKICK_TEST_CLAUDE_API_KEY`: the live Claude API tests (Sonnet 5 and Opus 5.5, a few cents a run).
* `NEONSIDEKICK_TEST_OPENAI_API_KEY`: the live OpenAI API tests (the nano models the account lists, a few cents a run); with `NEONSIDEKICK_TEST_OPENAI_SWEEP=1`, every listed chat model's reasoning words too.
* `NEONSIDEKICK_TEST_EMBEDDED_MODEL`: the catalog id the live embedded test runs (else the first installed; needs llama.cpp and a model already installed).
* `NEONSIDEKICK_TEST_LLAMA_EXE` with `NEONSIDEKICK_TEST_TINY_GGUF`: any `llama-server.exe` and small GGUF (`stories15M-q4_0.gguf`, 19 MB), for the process host's test.

</details>

## Components & Libraries
[↑ Back to top](#neon-sidekick)

* `.NET 10 (NativeAOT)`
* `Spectre.Console`
* `Microsoft.Extensions.AI`
* `Microsoft.Extensions.AI.OpenAI`
* `Microsoft.Data.Sqlite`
* `Microsoft.Data.SqlClient`
* `Microsoft.SqlServer.TransactSql.ScriptDom`
* `Oracle.ManagedDataAccess.Core`
* `MySqlConnector`
* `Microsoft.ML.OnnxRuntime`
* `KokoroSharp`
* `Whisper.net`
* `Silero VAD`
* `Vosk`
* `PhotoSauce.MagicScaler`
* `Markdig`
* `LibGit2Sharp`
* `llama.cpp` (`llama-server`, downloaded on first use of the embedded LLM)

## Why "Neon"

Early on I was trying synthwave-style themes in Spectre.Console while testing the Vosk voice integration. I needed a short, punchy wake word, and "Neon" fit the look. The name stuck. Today you can create as many themes, profiles, personas and wake words as you like.