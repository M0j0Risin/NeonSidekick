# Neon Sidekick
![License](https://img.shields.io/github/license/M0j0Risin/NeonSidekick)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512bd4?logo=dotnet)
![Windows](https://img.shields.io/badge/Windows-0078D6?style=flat&logo=windows&logoColor=white)

Neon Sidekick brings privacy-first, local LLM inference to your terminal. Powered by .NET 10 and inspired by tools like Claude Code, Hermes Agent, and Cline, this lightweight agentic TUI harness pairs many of my favorite features from those tools with my own toolsets for a 100% locally executed workflow.

**Current Status:** A stable, Windows-first foundation for agentic tool development.

**Roadmap:** Expanding core coding capabilities and delivering official macOS/Linux support.

## Contents

- [Features](#features)
- [Settings & menus](#settings--menus)
- [Slash commands](#slash-commands)
- [Tools](#tools-2)
- [Environment variables](#environment-variables)
- [Screenshots](#screenshots)
- [Components & Libraries](#components--libraries)
- [Why "Neon"](#why-neon)

## Features
[↑ Back to top](#neon-sidekick)

### Core Architecture & UI
* Built on **.NET 10 NativeAOT** for lightweight, high-performance execution.
* **Rich Terminal UI (TUI):** Powered by Spectre.Console, featuring robust support for menus, mouse input, Markdown, and images.
* **Vision & Media Input:** Full vision model support with seamless drag-and-drop and clipboard pasting for images directly into the terminal.
* **Quality of Life:** Intuitive auto-complete for commands, files, folders, skills, and tools.

### AI Connectivity & Context Management
* **Local AI Auto-Discovery:** Automatically detects and connects to most OpenAI-compatible servers on your local network (LM Studio, vLLM, SGLang, Ollama, Unsloth, etc.), while allowing full manual configuration for custom endpoints.
* **Claude API (optional):** Anthropic's Claude models as one more `/server` choice, with your own API key (stored encrypted), thinking levels, prompt caching and the cost shown in `/usage`. Off until you turn it on in the *Claude (API)* tab of `/settings`.
* **Smart Context Handling:** Configurable automatic context compaction to optimize token usage and prevent window overflow.
* **Prompt Transparency:** Visually inspect exactly what is being fed into the system prompt and see detailed compaction summaries—no black boxes.
* **Persistent Memory:** A UI-editable memory system that automatically injects essential, recurring details directly into context.
* **Message Queue:** Built-in queue for stacking and executing sequential messages.
* **Plan Mode:** `/plan <requirement>` has the model research with read-only tools, ask what it needs and present a plan, saved as `.neon/plans/<name>.md` in the working directory; nothing is changed until you approve it.

### Profiles, Sessions & Skills
* **Multi-Profile Support:** Switch between configurations, each with its own working directory, settings, persona, memory and sessions. A name starting with `_` (`_test`) makes a temporary profile; `--profile <name>` opens one for a single launch.
* **Session Management:** Resume, search and reflect on past sessions.
* **Hierarchical Skills System:** Define and manage agent skills at the global, profile, project, or machine (`.agents\skills`) level.
* **Self-Learning:** A background reflection writes new skills and improves existing ones from your interactions and tool outcomes.

### Built-In Tooling & Voice
* **Essential Tools:** Sandboxed file I/O, shell integration (powershell/cmd/bash), scripting (powershell/python/node), Git management, read-only SQL Server queries, web search (DuckDuckGo/SearXNG), web browsing (httpClient/Chromium), graphical clarification prompts, and clock/timers.
* **MCP Server Support:** Connect Model Context Protocol (MCP) servers for more tools and external data sources.
* **Native Voice Stack:** In-process Whisper STT, push-to-talk, and a Vosk wake word.
* **Text-to-Speech:** In-process Kokoro TTS, or an external Kokoro HTTP server.

## Settings & menus
[↑ Back to top](#neon-sidekick)

**Navigation**
* **Keyboard:** ←/→ (switch tabs), ↑/↓ (move), Enter (edit/toggle), ESC (close).
* **Mouse:** Click moves the cursor; double-click selects rows and tabs. The top-right × acts as ESC, and double-clicking outside an open pane closes it. Double-clicking a picture in the transcript opens it in the [picture viewer](#picture-viewer).

**Input line**
* The input row is always a full editor, even while a reply streams or `/botchat` runs: ←/→, Home/End, Delete, Shift+arrows or Ctrl+A to select, Ctrl+C / Ctrl+X to copy / cut, right-click or Alt+V to paste, click to place the cursor, ↑/↓ for history, and the `/`, `@`, `#`, `$`, `%` and `^` lists.
* Enter while a reply runs queues the message. Nothing typed is lost, and a draft left on the row stays there after the reply ends.
* ESC while a reply runs stops the speech first, then closes an open list, then cancels the reply. It never clears your draft; ESC at the idle line does.

Commands typed while a reply runs:

| Behaviour | Commands |
|---|---|
| Open their pane over the reply | `/help`, `/settings`, `/tools`, `/mcp`, `/sys`, `/usage`, `/about`, `/memory`, `/queue`, `/sessions`, `/skills`, `/reasoning`, `/cmdlist`, `/police`, `/emptytrash`, `/cmdclear`, `/tree`, `/vault`, `/cmdcopy`, `/persona`, `/operata`, `/vocalia` |
| Run at once | `/tts`, `/stt`, `/wake`, `/interrupt`, `/reasoning <level>`, `/queue clear`, `/copy`, `/remember`, `/explore`, `/log`, `/timer`, `/expand`, `/collapse`, `/window`, `/cwd`, `/comfy view`, `/view <path>` |
| Stop the reply first | `/clear`, `/new`, `/splash`, `/exit` |
| Everything else | Waits for the reply to end, queued behind any earlier messages (so *Queue cancel mode* applies) |

**Double-Click Shortcuts**
* **Hint row:** model name → `/server` (server, then model, then reasoning) · reasoning glyph → `/reasoning` · tokens/spinner → `/usage` · queued count → `/queue` · blank space → `/settings`.
* **Toolbar** (*Show toolbar*): a glyph toggles its pane, or switches to it from another pane.

| Toolbar item | Shown | Opens |
|---|---|---|
| ⚙️ 🛠️ 🔌 🎓 🎭 💬 | always | `/settings`, `/tools`, `/mcp`, `/skills`, `/sys`, `/sessions` |
| 💾 | while *Memory* is on | `/memory` |
| 🔒 / 🔓 | *Shell command policy* is `ask` / `yolo` (none under `off`) | `/cmdlist` |
| 👮 | *Shell police outside paths* is on and the policy isn't `off` | `/police` |
| working directory (right edge) | always | `/cwd browse` |
| blank space | — | `/settings` |

**Available Panes**
* `/settings`: App, sessions, LLM, voice stack, and `/botchat` pictures
* `/skills`: Agent skills and self-reflection
* `/tools`: Callable model tools, and Claude Code (`/claude` and the advisor)
* `/mcp`: External MCP servers
* `/sys`: Read-only view of the outgoing model payload
* `/usage`: Show LLM usage statistics (tok/s, ttft, etc.)

Settings that an environment variable or flag can override for one launch are listed under [Environment variables](#environment-variables).

<details>
<summary><b>⚙️ App Settings (`/settings`)</b></summary>

#### General

| Setting | What it does | Default |
|---|---|---|
| Profile | Switches to another profile (each has its own settings, persona, memory, skills and sessions). | `default` |
| New profile mode | What `/profile add` copies from the current profile: `basic` copies the settings and memories; `advanced` also copies the persona, operating-rules and voice-directive files. | `basic` |
| Working directory (cwd) | The folder the file and git tools work in; empty means the profile's own `files\` folder. Editing the row opens the `/cwd browse` folder picker; `/cwd <path>` takes a typed path. | profile's `files\` |
| Queue messages | Messages sent while a reply streams are listed (the count and `/queue`) and sent when it ends. Off, they are still sent then, just not listed. | on |
| Queue cancel mode | What a cancelled reply does with the queue: `hold` keeps it until your next message, `drain` sends the next message at once, `empty` drops them all. | `empty` |
| Memory | Offers the model `save_memory` / `recall_memory` and opens every conversation with what it remembers. | on |
| Copy user prompt | `/copy` includes your prompt above the reply; off copies the reply alone. | on |
| Show image thumbnails | Draws a small colour block of each picture you send under your line. | on |
| Image thumbnail size | `tiny` (32×8), `small` (48×12), `medium` (64×16), `large` (80×20) or `xlarge` (96×24) columns × rows, or `fullsize`: each picture as large as the transcript allows, stacked. | `small` |
| Transcript markdown | Renders replies as Markdown instead of plain text. Code fences in C#, JavaScript/TypeScript, Python, Bash, PowerShell, JSON, YAML, TOML/INI, SQL, C/C++, Java, Kotlin, Go, Rust, CSS, XML/HTML and diff are syntax-highlighted. | on |
| Paste preview lines | How many lines of a long paste show dimmed under its `[Pasted text #n]` placeholder (0–200; 0 = placeholder only). | 25 |
| Hide /exit autocomplete | Leaves `/exit` out of the `/` list so a stray pick can't close the app; typing it in full still works. | on |
| Command typo intercept | A command name without its slash (`clear`) or with extra slashes (`//profile work`) asks *Did you mean /clear?* before sending it as text. A bare `//` is still `/settings`. | on |
| Keep command history | Saves the ↑/↓ history (newest 1,000 lines) in the profile's `sessions.db`, so it survives restarts and profile switches. Lines holding a collapsed paste or a picture aren't saved. Off, the saved lines are deleted at the next profile load. `/cmdclear` empties it either way. | on |
| Welcome splash | Pictures under the banner at startup, until the first line is sent: `fullsize`, `tiled` or `disabled`. See [below](#welcome-splash). | `fullsize` |
| Working directory in header | Prints the working directory at the right of the banner's title line. | off |
| Show toolbar | Draws the toolbar under the hint row (see *Double-Click Shortcuts* above). | on |
| Theme | `synthwave`, `netrunner` (green phosphor), `nostromo` (amber phosphor), `noir` (greyscale), `cyberpunk` (colourful), `vaporwave` (pastel), `mainframe` (blue phosphor), `grid` (light cycle), `replicant` (smog and sodium) or `abyssal` (bioluminescent). | `synthwave` |
| Draft editor | What `/draft` opens its temporary file with (`code --wait`, `notepad`…); empty uses the app Windows opens `.txt` files with. | (default .txt editor) |
| Image viewer | Where a double-clicked picture opens. Empty: the built-in [picture viewer](#picture-viewer). `system`: the app Windows registers for the file type (Paint for png, jpg and bmp). Anything else is a command, with the file's path appended (`mspaint`, `"C:\Program Files\GIMP 3\bin\gimp-3.exe"`). | (built-in viewer) |
| Themed image viewer | The built-in viewer wears the theme: a themed dark title bar (Windows 11; Windows 10 gets a plain dark one) and the theme's background. Off keeps it black. | on |

##### Welcome splash

* `fullsize` shows one picture filling the screen; ←/→ walk the set. Delete twice on an empty line moves one of the profile's own pictures into the folder's `.trash` (never shown; move it back to restore it).
* `tiled` shows thumbnails at *Image thumbnail size*, as many as fit; ←/→ page through them and a double-click opens one.
* Pictures in the profile's `splash\` folder (created for you) replace the built-in ones.
* `/splash` shows the splash whatever this says: tiled when the setting is `tiled`, otherwise one picture.

#### Sessions

| Setting | What it does | Default |
|---|---|---|
| Session logging | Writes every completed turn to the profile's `sessions.db`, so `/sessions` can list, search and restore it. | on |
| Session retention (days) | Sessions whose last turn is older than this are purged at startup (0–3650; 0 = keep forever). | 0 |
| Session naming mode | How a session gets its title: `model-written` asks the model for a short slug after the first turn; `first-line` uses the first line you sent. | `model-written` |
| Session show name | Which titles show on the rule above the input row: `all-names`, `model-written` (a model-written or typed name only) or `none`. | `all-names` |
| Session tool | Offers the model `session_manager` to search, list and read this profile's earlier sessions (never restore or purge). | on |
| Session search max results | How many sessions a `session_manager` search or list returns (1–20). | 10 |

#### LLM

| Setting | What it does | Default |
|---|---|---|
| LLM scan mode | Where a blank URL looks for a server: `local` (the usual ports on this machine), `remote` (the same ports across the local network), `both`, or `disabled` (no scan; set the URL by hand). | `local` |
| LLM URL | The OpenAI-compatible base URL (`http://127.0.0.1:1234/v1`). Empty scans per *LLM scan mode* and, at startup, lets you pick a server, model and reasoning level, all saved (ESC takes the first server, unsaved). `/server` fills it in. | (scan) |
| LLM model | The model id; empty takes the first the server lists. `/model` picks one. | (first listed) |
| LLM API key | The bearer token; `empty` for keyless local servers. | `empty` |
| LLM reasoning | The reasoning effort sent with every request: `none` (thinking off), `low`, `medium`, `high` or `xhigh`. `/reasoning` opens the same list. | `none` |
| LLM request timeout (s) | The most one HTTP request may take (up to 3600). | 3600 |
| LLM turn timeout (s) | The most one whole turn — every tool round trip included — may take (up to 21600). | 21600 |
| LLM context length | The model's context window in tokens, for the usage percentage; 0 takes the server's own figure. | 0 (server) |
| LLM mid-turn usage | What the hint row's token usage shows during a reply. `estimate`: context and tok/s update live, marked `~` (one token per streamed chunk), until the server's figures arrive at the end of each request. `last-known`: the last completed request's figures. | `last-known` |
| LLM compact type | What `/compact` does: `summary` folds the older turns into one model-written summary; `prune` stubs their bulky tool results and keeps every turn. | `summary` |
| LLM compact keep recent | How many recent user turns a compact keeps word for word (0–24). | 2 |
| LLM compact show summary | After a compact, shows what it did: the summary as dim lines, or one line per pruned tool result, then how many messages were kept at the start and end. | off |
| LLM auto compact (%) | The share of the context window at which the next message compacts first (1–100; 0 = off). | 85 |
| LLM max turns | How many user turns the model sees before the oldest drop off (1–500). `auto` keeps them all while auto compact can run (a known context window and a share above 0), otherwise 24. | `auto` |
| LLM offer tools | Whether the model gets any tools. Off suits chat templates with no tool role; flipping it starts a new conversation. | on |
| LLM tool compact type | What happens when a single turn's tool calls approach the window: `prune` stubs this turn's older results and carries on, `stop` ends the turn with a notice, `nothing`. | `prune` |
| LLM max tool iterations | How many tool round trips one message may make before the turn stops (1–10000). | 10000 |
| LLM use fun verbs | The thinking spinner reads a random verb instead of `thinking` / `writing`. | off |
| LLM show thinking | Streams a reasoning model's thinking as a dim block (its last five lines), folded to `▸ 💭 thought for 4.2s` when the answer starts; needs Transcript markdown. Click the line, press Ctrl+O or use `/expand` to see it again. Thinking is never spoken or logged, and copied only by `/copy --thinking`. | on |

#### TTS

| Setting | What it does | Default |
|---|---|---|
| TTS output | Reads replies aloud (`/tts`). Fenced code blocks and tables are shown but never read aloud (nor by `/speak`). | off |
| TTS source | `in-process` runs Kokoro in this process over ONNX Runtime (the model downloads on first use); `http` uses a Kokoro-FastAPI server. | `in-process` |
| TTS HTTP URL | The Kokoro-FastAPI base URL, read while the source is `http`. | `http://localhost:8880/v1` |
| TTS voice preview | The voice pickers (and the preset picker) speak the highlighted voice as you move through them. | on |
| TTS voice preset | Sets TTS voice, voice 2, voice mix and speed in one go. Not saved itself: the row shows the preset those four match, or `(custom)`. Built in: `amanda`, `neon`, `richard`, `hunter`, `larry`, `jack`, `willow`. A `voice_presets.json` in the home folder replaces the list (`{ "name": { "TtsVoice": "af_heart", "TtsVoice2": "am_eric", "TtsVoiceMix": 80, "TtsSpeed": 1.2 } }`, as in `assets/voices/voice_presets.json`; out-of-range entries are skipped). | `neon` |
| TTS voice | The Kokoro voice. | `af_heart` |
| TTS voice 2 | A second voice blended in; `(none)` for the primary voice alone. | `am_eric` |
| TTS voice mix | The primary voice's share of the blend, 0–100 %. | 80 |
| TTS speed | The speech rate multiplier, 0.5–2.0. | 1.2 |

#### STT

| Setting | What it does | Default |
|---|---|---|
| STT input | Turns the microphone on: the push-to-talk key records a spoken message (`/stt`). | off |
| STT wake | Saying the wake phrase at the idle line starts a listen without a key (`/wake`). | off |
| STT wake phrase | One to three words; also the interrupt phrase. | `hey neon` |
| STT interrupt | Saying the wake phrase during a spoken reply cuts it short and listens (`/interrupt`). | off |
| STT interrupt echo guard | How close the assistant's own just-spoken text must be to the wake phrase to be ignored as an echo, 50–100 % (100 = the exact phrase only). | 100 |
| STT interrupt confirm | How long the phrase must persist in the recogniser's interim results before it counts, 0–2000 ms. | 200 |
| STT push-to-talk key | The key that records: `F1`–`F10`, `Insert`, `Home`, `End`, `PageUp` or `PageDown`. | `F4` |
| STT whisper model | The Whisper model that transcribes: `ggml-tiny.en.bin`, `ggml-base.en.bin` or `ggml-small.en.bin` (downloaded on first use). | `ggml-base.en.bin` |
| STT vosk model | The Vosk model the wake word and interrupt listen with: `vosk-model-small-en-us-0.15`, `vosk-model-en-us-0.22-lgraph` or `vosk-model-small-en-in-0.4`. | `vosk-model-small-en-us-0.15` |

#### Claude (API)

Anthropic's Claude API as a server. With the switch on and a key set, `/server` (and the startup picker) lists a **Claude API** row after the local servers. Picking it sets *LLM URL* to `https://api.anthropic.com/v1` and offers the account's models, then the reasoning level. Every message is billed to the key's account. Each key goes only to its own server: local servers never see this one, and the Claude API never sees *LLM API key*.

| Setting | What it does | Default |
|---|---|---|
| Claude API | Offers the Claude API on `/server` while a key is set. If it's off (or keyless) while the Claude API is the saved LLM URL, the app scans for a server as if the URL were blank. | off |
| Claude API key | Your Anthropic API key (`sk-ant-…`), saved encrypted for your Windows account (DPAPI) and shown as `(set, encrypted)`. Typing replaces it; an empty entry clears it. | (none) |
| Claude API max tokens | The output cap per request, thinking included (1,024–128,000). A reply that hits it stops short, and the log says so. | 32,000 |
| Claude API prompt caching | Marks the tools, system prompt and conversation for Anthropic's prompt cache, so each request re-reads the previous one's content at a fraction of the price. | on |

* *LLM reasoning* per model: `low`…`xhigh` turn on adaptive thinking at that effort (`xhigh` is `high` on the 4.6 models; Haiku 4.5 and older take a thinking budget instead). `none` turns thinking off where the model allows it; Opus 5.5 and Fable always think, so there `none` is the lowest effort.
* The context window is the model's `max_input_tokens`.
* `/usage` adds *Cache* and *Cost* rows. Cost is an estimate at list price, not the bill.

#### Botchat

| Setting | What it does | Default |
|---|---|---|
| Botchat LLM mode | Whose LLM the bots use. `single`: every bot uses this profile's server, model and reasoning. `multi`: each bot uses its own profile's URL, model, API key, timeouts and reasoning (a blank URL borrows this profile's server). A bot whose server doesn't answer at the start sits the chat out, with a notice. Read when a chat starts or resumes. | `single` |
| Botchat images enabled | Adds pictures to `/botchat`; needs *ComfyUI tools* on and a *ComfyUI URL*. Off, the chat is talk only, with no tools (except `load_skill` under *Botchat skills enabled*). | off |
| Botchat image mode | Who draws. `automatic`: the app writes a prompt from each reply and draws it. `autonomous`: the bots get `generate_image` and draw when they choose. See [Botchat pictures](#botchat-pictures). | `automatic` |
| Botchat txt2img workflow | The text → image workflow for new pictures. Blank means no new pictures. | (none) |
| Botchat img2img workflow | The image → image workflow for reworking one of the chat's pictures. Blank means no reworks. | (none) |
| Botchat img2img mode | Which pictures a rework may start from: `latest`, or any in `chat-history` (the last 8, numbered). | `latest` |
| Botchat image async | On: the next bot speaks while the picture renders, and the picture appears when nothing is streaming. Off: each reply is held back until its picture is drawn, then appears under it. | on |
| Botchat non-TTS delay | Seconds to pause after each reply when no voice plays (*TTS output* off), so it can be read (0–30; 0 = no pause). A line typed meanwhile joins the chat; ESC during the pause ends it. | 5 |
| Botchat skills enabled | Offers every bot `load_skill` (never `skill_editor`) over the skills this chat sees: the starting profile's, the global ones and, with *Use external skills*, the external ones, but never a bot's own profile's. In `automatic` mode the prompt writer gets them too. Needs *Agent skills*; switching `load_skill` off in `/tools` turns this off too. | off |
| Botchat preloaded skills | Skills the app loads itself, so no `load_skill` call is needed: tick them in the checklist, or name one as a whole word in the topic (`/botchat use pony-prompts for the pictures`). Needs *Agent skills*, but not *Botchat skills enabled*. The chat says which were loaded. | none |
| Botchat skill mode | Who gets the preloaded skills: `prompt-writer-and-bots` (the picture prompt writer and every bot's system prompt) or `prompt-writer-only`. | `prompt-writer-and-bots` |
| Botchat vision enabled | Shows each bot, on its turn, the newest 4 pictures since it last spoke, each captioned with whose it is (not the ones it drew itself). Only for models that read images: a text-only server fails the turn (in `multi` mode, every bot's model counts). Pictures aren't kept for `/botchat --resume` or the saved session. | off |

##### Botchat pictures

* The two botchat workflows are picked from every installed workflow of their kind, ticked in *ComfyUI workflows offered* or not (that list is for the main chat only). Pictures are drawn at *Image thumbnail size*. With no txt2img workflow set, a notice says so once per chat.
* **`automatic`**: after each reply, the model writes an image prompt from it in the workflow family's style, and the app draws it. The bots get no tool.
* **`autonomous`**: the bots get `generate_image`, limited to the two botchat workflows. If a reply talks about a picture the bot never drew (or its call failed), the app draws it. A tool call a bot writes out as text is run as a real call and never shown or spoken.
* **Reworks**: once the chat has a picture and an img2img workflow is set, whoever writes the next prompt chooses between a new picture and a rework. In `automatic` the prompt writer answers `REWORK` (or `REWORK n`); in `autonomous` the bot sees the pictures' paths in its turn and passes one to `generate_image` as `image`. A picture still rendering can't be reworked yet.
* **Async on**: 🖼️ (🎨 for image-to-image) sits on the hint row's glyph strip while a picture renders, with a count when several are pending (`🖼️ 2`). Each picture is labelled with whose reply it shows. With no voice (*TTS output* off), pictures go to ComfyUI one at a time, each a second after the previous one is made.
* **Async off**: ESC while a reply is held back cuts that bot short, as it would a streaming reply; ESC under the picture's spinner skips just that picture.
* Either way, the image prompt is written before the next turn.
* **Skills**: with *Botchat skills enabled*, the `automatic` prompt writer may load a skill (and a file it bundles) before writing, so a topic like "use the pony-prompts skill for pictures" is followed from the first picture. Each load shows as a skill line. To use a skill without relying on the model to load it, use *Botchat preloaded skills*.

</details>

<details>
<summary><b>🎓 Skills Settings (`/skills`)</b></summary>

#### Offered

The loaded skills with their scope (`profile`, `global` or `external`) and description, then any shadowed duplicates and skipped folders (with the reason). Enter on a skill lets you:
* move it between the profile and global roots;
* rename it (forced to lower-case-with-hyphens; a name already taken is refused);
* edit its `SKILL.md` in your editor (the change applies the next time the skill loads);
* delete it (after a confirmation).

#### Reflection

| Setting | What it does | Default |
|---|---|---|
| Reflection (auto-learn) | After enough tool calls, or a tool error the model recovered from, a background reflection writes or improves a skill. | on |
| Reflection reasoning | The reasoning effort of the reflection alone: `none`, `low`, `medium`, `high`, `xhigh`, or `profile` for the profile's own level. | `none` |
| Reflection window | How many of the last turns a reflection reads (1–5; the last in full, the earlier ones trimmed). | 3 |
| Reflection min tool calls | How many of the model's own tool calls, added up since the last reflection, make a task worth a skill (3–20). | 4 |
| Reflection max requests | How many model requests one reflection may spend before it gives up (1–20). | 4 |
| Reflection cooldown (minutes) | How long after a skill was written an automatic reflection waits (0–1440; 0 = off). | 5 |
| Reflection cooldown mode | `last-written-skill` makes only a turn that used the skill just written wait; `all-skills` makes every automatic reflection wait. | `last-written-skill` |
| Reflection includes sessions | The reflection opens with the earlier sessions that match the turn, and can search them. | on |
| Reflection yields to turns | A message sent while a reflection runs pauses it, so the reply gets the server; the same reflection runs again once the reply and any queued messages are done. Turn off if your server serves requests in parallel. | on |
| Reflection edit supporting files | A reflection may also change a skill's supporting files (the data, examples or scripts beside its `SKILL.md`) with `skill_editor`'s `write_file` and `edit_file`. Off, a reflection writes the `SKILL.md` alone; the main chat may always write them. | off |

#### Project

One row, **Project file**: whether `NEON.md` (or `AGENTS.md`) in the working directory is read into the prompt as project notes. The row shows which file is found and its size. Default on.

#### Options

| Setting | What it does | Default |
|---|---|---|
| Agent skills | Lists the skills in the prompt and offers `load_skill` and `skill_editor`; off also stops reading the project file. | on |
| Use external skills (.agents\skills) | Also reads `%USERPROFILE%\.agents\skills`, read-only. | off |
| Skill compact mode | `protected` keeps a loaded skill's instructions through a prune; `unprotected` prunes them like any tool result. | `protected` |
| #-mention enabled | `#` and part of a name on the input line lists the loaded skills; a pick writes `#name` as text. | on |

#### Installing skills

`/skills add` brings in skills written in the [Agent Skills](https://agentskills.io) format, the same folders other agents use.

- **What it takes:**
  - Search words (`/skills add pdf`) look the skill up on [skills.sh](https://skills.sh), the public directory. agentskills.io itself hosts only the specification.
  - `owner/repo` offers every skill in a GitHub repository.
  - `owner/repo/skill` names one skill. This is the id a search shows, so a result can be typed back.
  - A `github.com/…/tree/<branch>/<path>` or `…/blob/<branch>/<path>/SKILL.md` link narrows the search to a folder.
  - Any `https://…/*.zip` link is downloaded as it is.
- **Where it comes from:** the repository isn't downloaded whole. The app gets its file list from the GitHub API at the branch's latest commit (two requests; GitHub allows 60 an hour without a token) and finds every `SKILL.md`, wherever it sits (`skills/`, `.claude/skills/`, `.agents/skills/`, the root). It fetches only those files, then the chosen skill's files once you confirm, from `raw.githubusercontent.com`, pinned to that commit. A repository with more than 100 skills must be narrowed to one (`owner/repo/skill` or a folder link). If the API is rate-limited, unreachable or returns a truncated list, the app falls back to GitHub's zip of the branch (up to 50 MB).
- **Network rules:** *Web browser network mode* applies (under `local_area_network` nothing is fetched). The *Web tools* switch doesn't, because you typed the command, not the model.
- **Before anything is written:** the skill is previewed — its description, source and commit, other frontmatter such as `allowed-tools`, the file list, any scripts, and the start of its instructions. The install is refused when:
  - a path could escape the folder, or is not a valid Windows path;
  - two files differ only by case;
  - it has more than 200 files, over 20 MB in total, or any file over 5 MB.

  Symbolic links are left out.
- **Where it goes:** the folder lands under the profile's or the global `skills` folder, named after the skill, with a `.neon-source.json` beside its `SKILL.md` recording the repository, path, commit and date. Bundled scripts run only through `run_command` and its approval, like any other command.
- **Name collisions:** a name already in use is refused; rename or delete the existing skill on `/skills` first. The exception is a skill installed earlier from the same repository and path — adding it again replaces it where it is (an update).

</details>

<details>
<summary><b>🛠️ Tools Settings (`/tools`)</b></summary>

#### Offered

Every tool, grouped (Clock, Timers, Files, Git, Shell, Obsidian, SQL, ComfyUI, Claude, Web, Memory, Skills, Sessions, Questions), with the description the model reads. Enter or Space flips one tool on or off; a group whose switch is off is shown dim. In a new profile, `git_delete` (loses branches, tags and stashes), `zip` and `unzip` start off.

#### Web

| Setting | What it does | Default |
|---|---|---|
| Web tools | Offers `web_search`, `web_fetch`, `open_url` and `download_file`. | on |
| Web browser mode | `default` fetches with the HTTP client and falls back to a headless browser when a page is blocked or empty; `httpclient` never falls back; `chromium` uses the browser for every page. | `default` |
| Web browser path | The Chromium executable for the headless leg; empty finds Edge, Chrome or Brave in their standard folders. | (auto) |
| Web browser network mode | Where a fetch may reach: `internet` (public addresses only), `local_area_network` (this machine and the LAN only) or `both`. | `internet` |
| Web search method | `duckduckgo` (built in, no setup) or `searxng` (the instance below). | `duckduckgo` |
| Web SearXNG URL | A SearXNG instance's base URL, used while the method is `searxng`. | (not set) |
| Web search max results | How many hits a search returns (1–20). | 20 |

#### Files

| Setting | What it does | Default |
|---|---|---|
| File tools | Offers the sandboxed file tools (read, write, patch, search, move, copy, zip, view_image…) under the working directory. | on |
| File safe edits | Every edit keeps the previous version in `.trash` first and `delete` moves there, with `restore` as the undo; off writes in place and `delete` removes for good. Best when working a directory without Git. | off |
| File /tree max length | How many entries `/tree` prints before it stops (1–10000). | 500 |
| File /tree show sizes | `/tree` carries each file's size. | on |
| File @-mention folder mode | Picking a folder from the `@` list: `folder-remain` keeps the list open inside it; `folder-apply` writes `@folder/` and closes. | `folder-remain` |
| File browser/tree mode | What the folder browsers (`/cwd browse`, the *Obsidian vault* row) and `/tree` list: `default` hides hidden and system entries and dot-folders (and, in `/tree`, dot-files); `show-hidden` lists them too. | `default` |
| File view image max (per call) | How many pictures one `view_image` call may load (1–100). | 10 |

#### Shell

| Setting | What it does | Default |
|---|---|---|
| Shell command policy | What stands between `run_command` and the shell: `off` (no shell tools offered), `ask` (a command not on the allowed list goes to the approval pane first; with no pane it is refused) or `yolo` (everything runs, nothing is asked). See [Shell guards](#shell-guards). | `ask` |
| Shell allowed commands | The prefixes allowed for good (`git status`, `dotnet build`, `python`). Enter on one removes it; the pane's *Allow … always* adds one. `/cmdlist` opens the list; `/cmdcopy` copies it to another profile. | none |
| Shell police outside paths | Refuses a shell command, script or background-process input that names a path outside the working directory, before it runs or the pane asks. `/police` opens this row. See [Shell guards](#shell-guards). | on |
| Shell prefer native tools | Steers the model to the app's own tools, and sends back a single shell command that one of them covers (once a turn). See [Shell guards](#shell-guards). | on |
| Shell default | The shell a `run_command` without `shell` runs in: `powershell` (pwsh when installed, else Windows PowerShell 5.1), `cmd`, or `bash` (Git Bash, when found). | `powershell` |
| Shell timeout (s) | How long a foreground command without `timeout` may run before it is killed (1–3600). | 180 |
| Shell foreground cap (s) | The most a foreground command may wait, whatever its `timeout` says (10–3600). | 600 |
| Shell output max chars | The most output one result carries back (2000–500000); over it the head and tail are kept and the whole text goes to `.shell\<id>.log` under the working directory, where `read_file` reaches it. | 30000 |
| Shell code languages | The languages `execute_code` may run — `powershell`, `python`, `node`; one or more, and a language is offered only while its interpreter is found. Enter or Space flips one; the last one on stays. | all three |
| Shell code timeout (s) | How long an `execute_code` script without `timeout` may run before it is killed (1–3600). | 300 |
| Shell tool bridge | Lets an `execute_code` script call the app's other tools through its `neon_tools` module (a loopback socket with a per-run token). Off, no module is written and nothing mentions it, so the script does everything itself. | off |
| Shell tool bridge max calls | How many tool calls one script may make through the bridge (1–500). | 50 |

##### Shell guards

* **Approval (`ask`)**: the pane offers Deny, Allow once, Allow the prefixes for this session, or Allow them always (added to *Shell allowed commands*). A prefix is the program plus its subcommand for git, dotnet, npm, pip, gh, docker, cargo, go, winget and the like, otherwise the program alone. `--yolo` (one launch) and `NEONSIDEKICK_COMMAND_POLICY` override the policy, so a scripted `--headless` run can use `yolo`.
* **Path police** reads the text of a `run_command` line, an `execute_code` script, or what `process` writes to a background process. It refuses an absolute path not under the working directory (`C:\…`, a UNC share, a rooted `/etc/hosts`), a `..` that climbs out, `~`, or a folder variable (`%USERPROFILE%`, `$env:TEMP`, `$HOME`, `Path.home()`…). The model gets `Error: outside the working directory: '…'` and the transcript line shows 👮. The tool descriptions and operating rules tell the model the shell stays inside the working directory.
  * It reads text, not what runs: a computed path isn't seen, and a cmd switch (`dir /s`) or a URL isn't a path.
  * Off, any path goes, and nothing tells the model it may leave the working directory, so it doesn't try unless asked.
  * `--no-police` (one launch) and `NEONSIDEKICK_SHELL_POLICE` override it; `--yolo` never turns it off.
* **Prefer native tools**: the operating rules tell the model to use `run_command` only when no other tool does the job, naming the tools offered that turn and the shell commands each replaces (`cat`/`type`/`Get-Content`/`dir`/`ls`/`grep` → `read_file`/`search_files`, `git status`/`log`/`diff`/`add`/`commit` → the git tools, `curl`/`Invoke-WebRequest` → `web_fetch`, `sqlcmd` → `sql_query`). A single command such a tool covers comes back `Not run: 'cat' has a tool of its own — call read_file instead…` before the pane asks.
  * Only once a turn: the same line sent again goes to the pane as usual, so a real need (an option the tool lacks) still reaches you.
  * Never sent back: pipes and compound lines (`cat x | sort`, `a && b`), commands with no tool (`git push`), tools that are switched off, and (with the police off) a line naming a path outside the working directory.

#### Ask

| Setting | What it does | Default |
|---|---|---|
| Ask user | Offers `ask_user`, which puts multiple-choice questions on the pane. | on |
| Ask max questions | How many questions one call may put (1–10). | 10 |
| Ask max choices per question | How many options one question may offer (2–15). | 10 |

#### Claude (CLI)

The Claude Code CLI, for `/claude` (you send it a message) and `claude_advisor` (the model asks it for advice).

| Setting | What it does | Default |
|---|---|---|
| Claude executable | The Claude Code CLI to run. Blank looks for `claude.exe` on the PATH, then npm's `claude.cmd`, then `%USERPROFILE%\.local\bin\claude.exe` (the native installer's location). A path you set must exist; it is never swapped for another. | (looked up) |
| Claude slash command permissions | What Claude may do during a `/claude` run. `read-only`: `Read`, `Grep`, `Glob`, `WebSearch`, `WebFetch`. `edit`: its usual tools with file edits accepted, commands denied. `full`: everything, commands included (`bypassPermissions`). Nothing is ever asked: anything outside the level is denied, and the reply lists what was. Claude works in the working directory, but outside the app's sandbox, shell policy and approval pane. The advisor is always `read-only`. | `read-only` |
| Claude slash command model | The `--model` for `/claude`: Claude Code's default, `fable`, `opus`, `sonnet` or `haiku` (the latest of each), or *Other…* to type a model name. | (Claude Code's default) |
| Claude slash command effort | The `--effort` for `/claude`: Claude Code's default, `low`, `medium`, `high`, `xhigh` or `max`. | (Claude Code's default) |
| Claude advisor tool | Offers the model `claude_advisor`, to ask Claude Code for read-only advice when it is stuck. Each call costs money on your Claude account. | off |
| Claude advisor tool context | What a call sends. `brief`: the model's question and context. `recent`: also the last 10 messages (tool results cut to 500 characters). Claude can read the working directory itself either way. | `brief` |
| Claude advisor tool calls per turn | The most advisor calls one reply may make (1–10); past it, the model carries on alone. | 2 |
| Claude advisor tool model | The `--model` for the advisor; the first row follows *Claude slash command model*. | (as Claude slash command model) |
| Claude advisor tool effort | The `--effort` for the advisor; the first row follows *Claude slash command effort*. | (as Claude slash command effort) |
| Claude advisor tool confirm | Each call waits for your yes on the pane (the cursor starts on No; ESC is no). A no tells the model to carry on without it. Headless, calls are refused. | off |

#### Obsidian

| Setting | What it does | Default |
|---|---|---|
| Obsidian tools | Offers the vault tools (search, list, read, links, daily, write, properties, move) over the vault below, once one is set. | on |
| Obsidian vault | The vault's folder (the one holding `.obsidian`). It is separate from the working directory. Editing the row opens the `/cwd browse` folder picker. | (not set) |
| Obsidian allow delete (.trash) | Offers `vault_delete`, which moves a note or attachment into the vault's `.trash` (never deleted for good). | on |

#### ComfyUI

| Setting | What it does | Default |
|---|---|---|
| ComfyUI tools | Offers the image tools (`generate_image`, `set_splash_image`), once *ComfyUI URL* is set and a workflow is in a `comfy` folder. | on |
| ComfyUI URL | The ComfyUI server, often another machine on your LAN (`http://gpu-box:8188`). Like the LLM server, the web tools' network mode never blocks it. | (not set) |
| ComfyUI workflows offered | A checklist of the installed workflows the model is offered. Until you narrow it, all are, new ones included; after that only ticked ones are. With one ticked, every plain request and a plain `/imagine` go to it. `/imagine <name>` can still use a hidden one. | all (not narrowed) |
| ComfyUI add workflow | A wizard that **builds** a standard workflow from your server's checkpoints, or **imports** one you exported from ComfyUI. See [Adding a workflow](#adding-a-workflow). | — |
| ComfyUI ^-mention enabled | `^` and part of a name on the input line lists the offered workflows (family, shape, size); a pick writes `^name`, which `generate_image` reads as the workflow to use. | on |
| ComfyUI timeout (s) | How long the tool waits for one generation, queue included (10–3600); the job may still finish in ComfyUI. | 300 |
| ComfyUI max pictures per call | The most pictures one `generate_image` call or `/imagine --count` makes (1–16). Each is a full job, and all of them go to the model in the next request, where a local vision server has its own limit. | 5 |
| ComfyUI reinforce negatives | When the model writes the prompt, it also adds a few opposite tags where the image model tends to drift (a solo figure → `multiple girls`, night → `daylight`) to the workflow's negative. Skipped for a verbatim prompt, a negative set for the call, `/imagine`, families without a negative (Flux, FLUX.2, Klein, Krea 2, Z-Image, Ernie Turbo, Boogu, Ideogram 4) and a workflow whose `.md` says `reinforce: false`. | on |
| ComfyUI show prompts | Shows what was sent under each picture: the `prompt:`, the `negative:` and a `params:` line (size, steps, cfg, denoise, seed, sampler, scheduler). Off, only the picture's line. The model sees the same either way. | on |
| ComfyUI picture strip | Keeps the session's ComfyUI pictures as thumbnails in a strip over the input line, newest at the left. With the input line empty, ←/→ highlight one and Enter opens it; a double-click opens any. The strip's **🖼 viewer** button opens the [picture viewer](#picture-viewer) on the output folder. `/clear`, `/new` and a session switch empty it; it hides while a menu is open or the window is too short. | on |
| ComfyUI output folder | The folder under the working directory the pictures are saved in (`comfy_images\pony-txt2img-1234.png`); empty = the working directory itself. | `comfy_images` |

#### SQL

| Setting | What it does | Default |
|---|---|---|
| SQL tools | Offers the SQL tools (connections, databases, tables, columns, describe, relationships, indexes, query) over the connections in `sql.json`, once one is defined. | on |
| SQL connections offered | A checklist of the connections in both `sql.json` files. Until you narrow it, all are offered, new ones included; after that only ticked ones are, and new ones stay hidden until ticked. A hidden connection is invisible to every SQL tool, the rules, the `%`-mention and the default (`sql_connections` says how many are hidden, never which). | all (not narrowed) |
| SQL default connection | The connection used when a call names none: one of the offered connections, or the first. A call can still name another offered connection, and `database` can open other databases on the same server. | (the first connection) |
| SQL set password | Pick a connection that takes a password (`sql` or `runas`) and type it, masked. It is saved to that connection's store: encrypted in its `sql.json`, or in Windows Credential Manager. | — |
| SQL add connection | A wizard for a new connection, one page per choice, which can **test** the draft (`SELECT @@VERSION`) before saving it. See [Managing connections](#managing-connections). | — |
| SQL %-mention enabled | `%` and part of a name on the input line lists the connections (server, database, description); a pick writes `%name` as text. | on |
| SQL max rows | How many rows `sql_query` returns unless the call says otherwise (1–1000); past it the header says more exist. | 100 |
| SQL query timeout (s) | How long one SQL tool's batch may run on the server (1–600). | 30 |
| SQL connections (profile) | Enter opens the profile's `sql.json` in your editor (created with a commented example of each sign-in kind); the value counts its connections. | (none) |
| SQL connections (global) | The same for the home folder's `sql.json`, which every profile reads; the profile's wins on a name clash. | (none) |

#### Git (native)

| Setting | What it does | Default |
|---|---|---|
| Git native tools | Offers the git tools (status, log, show, diff, blame, branch, stage, commit, stash, discard, delete) over the repository in the working directory, in-process with no `git.exe`. Off, the model reaches git through the shell only, and `/gituser` does nothing. | off |
| Git native diff max lines | Where a `git_diff` patch is cut (20–5000). | 500 |
| Git native log max commits | How many commits `git_log` returns unless the call says otherwise (1–200). | 20 |
| Git native email | The `user.email` that `/gituser` writes into the repository's config. Never read by the git tools. | (not set) |
| Git native name | The `user.name` that `/gituser` writes beside it. | (not set) |

#### Options

| Setting | What it does | Default |
|---|---|---|
| $-mention enabled | `$` and part of a name on the input line lists the tools the next turn offers; a pick writes `$name` as text. | on |
| Tool collapse count | A run of more tool calls than this folds under one summary line (`▸ 🛠️ 7 tool calls — read_file ×3, …`): only its last lines show while it runs, and only the summary afterwards (0–100; 0 = never fold). | 2 |
| Code collapse count | A top-level code block longer than this folds to its label (`▸ 📜 csharp · 57 lines`) once its closing fence arrives; while streaming, only its last this-many lines show (0–100; 0 = never fold). Needs Transcript markdown. | 20 |

Click a folded line, press Ctrl+O or use `/expand` to see it in full.

</details>

<details>
<summary><b>🔌 MCP Servers & System (`/mcp` & `/sys`)</b></summary>

### MCP servers (`/mcp`)

#### Servers

One row per server in `mcp.json` (the profile's, then the home folder's; the profile's wins on a name clash) with its transport and state: `connected · N tools`, `connecting`, `failed: …` or `off`. Enter or Space switches a server on or off, connecting or disconnecting at once; Enter on a failed server retries. Below: `edit profile mcp.json`, `edit global mcp.json`, `reload`, and any skipped entries with the reason.

#### Tools

Every connected server's tools as `<server>__<tool>` with the description the server gives; Enter or Space flips one on or off.

#### Options

| Setting | What it does | Default |
|---|---|---|
| MCP servers | The master switch. On, every enabled server starts at launch (and after a profile switch) and its tools are offered; off, nothing starts. | off |
| MCP connect timeout (s) | How long a server gets to finish the handshake and list its tools before it is marked failed (5–300). | 30 |

### System prompt (`/sys`)

Read-only: exactly what the next reply will send, nothing paraphrased.

#### Prompt

The system prompt section by section, each with its status: **Persona** (default or `persona.md`), **Operating rules** (default or `operata.md`; the reply-format and tool sentences live here), **Project notes** (`NEON.md` / `AGENTS.md`), **Memory**, **Skills** (the catalog), and **Voice directive** (default or `vocalia.md`; spoken turns only, always last).

#### Tools

Every tool the reply may call, grouped (Clock, Timers, Files, Git, Shell, Obsidian, SQL, ComfyUI, Claude, Web, Memory, Skills, Sessions, one group per connected MCP server, Plan in plan mode, Questions), with the description the model reads. Tools and groups that are switched off are left out; `/tools` lists everything.

</details>

## Slash commands
[↑ Back to top](#neon-sidekick)

Type `/` to list every command with its summary; after a command and a space, its arguments are listed where they can be. `//` is an unlisted alias for `/settings`.

<details>
<summary><b>Click to expand all Slash Commands</b></summary>

| Command | What it does |
|---|---|
| `/about` | Show the app's version, runtime, folders, components and licence. |
| `/claude <message>` | Send the message to Claude Code (the `claude` CLI) and stream its reply into the transcript. See [Claude Code from the chat](#claude-code-from-the-chat). |
| `/clear` | Start a new conversation and clear the screen. |
| `/cmdcopy <profile> [--history] [overwrite]` | Copy this profile's *Shell allowed commands* into another profile, added to its list or (`overwrite`) replacing it. `--history` copies the command history instead (refused while that profile has *Keep command history* off). |
| `/cmdclear` | Clear this profile's command history, stored and in memory, after a confirmation. |
| `/cmdlist` | Open the *Shell allowed commands* list: Enter removes a prefix, ESC closes. |
| `/police` | Open the *Shell police outside paths* on/off page. |
| `/compact [focus]` | Shrink the current context; a focus steers the summary. |
| `/copy [n \| all] [--thinking]` | Copy the last reply (or the last *n*, or the whole transcript) to the clipboard as Markdown. `--thinking` includes the model's thinking, quoted under `💭 **Thinking**` where it happened. |
| `/cwd [path \| ~ \| browse]` | Show or change the working directory. `~` returns to the profile's `files\` folder; `browse` opens the [folder picker](#folder-picker). |
| `/draft` | Write the next message in your editor; it is sent when the file is saved and closed. |
| `/echo <text>` | Print a line as a reply, and read it aloud when speech is on. |
| `/emptytrash` | Empty the working directory's `.trash` permanently (asks first). |
| `/exit` | Exit the app. |
| `/explore [path]` | Open the working directory in your file browser. |
| `/gituser [force]` | Write *Git native email* and *Git native name* into the repository's config as `user.email` / `user.name`. An existing `[user]` section is kept unless `force`. Does nothing while *Git native tools* is off. |
| `/help` | Show the commands and keys: everyday commands on Commands (basic), the rest on Commands (advanced), then Keys. |
| `/interrupt [on\|off]` | Toggle the wake-word interrupt during a spoken reply. |
| `/learn [note \| sessions [N \| text]]` | Write or improve a skill in the background from the last turn, or from stored sessions. |
| `/log` | Open the diagnostic log in your editor. Exists only when the app was started with `--log <path>`. |
| `/loop <count> [delay] <message>`, `/loop infinite [delay] <message>` | Send the message that many times, or until ESC or Ctrl+C, waiting for each reply. See [Loops](#loops). |
| `/plan <requirement>` | Have the model research with read-only tools and present a plan before anything changes. See [Plan mode](#plan-mode). |
| `/botchat [profile ...] [topic]` | Let profiles talk to each other until you stop them. See [Bot conversations](#bot-conversations). |
| `/expand` | Unfold every folded tool run, code block and thinking block, now and from here on (Ctrl+O toggles this and `/collapse`). |
| `/collapse` | Fold the tool runs, code blocks and thinking again. |
| `/mcp` | Connect external MCP servers and switch their tools on or off. |
| `/memory [forget \| edit \| copy <profile> [overwrite]]` | List memories on a pane (Enter removes one). `forget` forgets them all. `edit` opens `memory.json` in your editor (invalid JSON is ignored with a warning). `copy` appends them to another profile's memory, skipping duplicates, or replaces it with `overwrite`. `forget` and `copy` ask first. |
| `/model [id]` | Pick a model from the server's list, or set one. |
| `/new` | Start a new conversation without clearing the screen. |
| `/operata [reset \| copy <profile> [force]]` | Edit `operata.md` (the operating rules) in your editor, reset it to the default, or copy it to another profile (`force` replaces theirs). |
| `/persona [reset \| copy <profile> [force]]` | The same for `persona.md` (the personality). |
| `/profile [name \| add <name> \| delete <name> \| rename <name> <new> \| reset [name] [--all] \| edit \| reload]` | Switch, create, delete, rename or reset a profile. `edit` opens `profile.json` in your editor; `reload` reads it back, reconnecting only what changed. See [Profiles](#profiles). |
| `/queue [clear]` | List and prune the messages queued during a reply (`⊠ clear all` or `c` drops them all); `/queue clear` drops them without the pane. |
| `/reasoning [level]` | Pick the reasoning effort (`none`, `low`, `medium`, `high`, `xhigh`). |
| `/remember <text>` | Add a memory. |
| `/server [url]` | Pick an LLM server found on the usual ports (or the Claude API, when it's on and has a key), or set one. The model and reasoning pickers follow, and one reconnect applies all three. |
| `/sessions [id \| purge <id> \| purge older <age> \| purge all \| title <text>]` | List, restore, rename and purge stored sessions. An age is a number of days (`30`) or a duration (`12h`, `90m`, `2 hours`, `1d 6h`). |
| `/settings`, `//` | Edit and save the settings. |
| `/skills` | List the skills (Enter moves, renames, edits or deletes one) and edit the skill, reflection and project-file settings. |
| `/skills add <search words \| owner/repo[/skill] \| github url \| zip url> [--global \| --profile]` | Install an [Agent Skill](https://agentskills.io) from the web, previewed first; a pane asks where it goes (the cursor starts on Cancel). See [Installing skills](#installing-skills). Refused while a reply runs. |
| `/speak [file [n] \| n]` | Read a text file from the working directory aloud as a reply; alone it resumes, a number starts from that sentence. |
| `/splash` | Start a new conversation and show the splash screen. |
| `/stt [on\|off]` | Toggle speech input. |
| `/sys` | Show the system prompt and the tools sent to the model. |
| `/theme [name]` | Switch the colour theme (the *Theme* setting). Mid-reply, it runs when the reply ends. |
| `/timer [duration [name] \| stop <name> \| stop all]` | List the timers, start one (`10m`, `90s`, `1h30m`), or stop one. |
| `/tools` | Switch the model's tools on or off and edit their settings (Web, Files, Shell, Ask, Claude, Obsidian, ComfyUI, SQL, Git). |
| `/tree [path]` | Print a tree of the working directory; hidden, system and dot entries only under *File browser/tree mode* `show-hidden`. |
| `/tts [on\|off]` | Toggle speech output. |
| `/usage` | Show token usage and performance statistics. |
| `/vault [path]` | Print a tree of the *Obsidian vault* (or a folder in it), like `/tree`: dot-folders left out, capped by *File /tree max length*, sizes per *File /tree show sizes*. Fails if *Obsidian tools* is off, no vault is set, or the folder is unreachable or has no `.obsidian`. |
| `/view <image or folder> [--chat]` | Open an image from the working directory in the [picture viewer](#picture-viewer), or a folder there on its newest picture. `--chat` (first or last word) draws it in the transcript instead. Works while a reply runs. |
| `/imagine [workflow] <prompt> [-- <negative> \| --no-negative] [--seed N] [--size WxH] [--steps N] [--cfg X] [--denoise X] [--image <path>] [--image2 <path>] [--image3 <path>] [--count N]` | Generate a picture on ComfyUI from your own prompt, sent exactly as typed, with no model in between. See [Imagine options](#imagine-options). |
| `/comfy` | Show the ComfyUI server's status, the workflows found (family, input, size, placeholders), skipped files and where workflows go. |
| `/comfy edit json <workflow>`, `/comfy edit markdown <workflow>` | Open a workflow's graph, or its `.md` (`md` works too; one is created with the family filled in), in your editor. |
| `/comfy view` | Open the [picture viewer](#picture-viewer) on the output folder. Works while a reply runs. |
| `/comfy purge` | Permanently delete everything in the output folder, `.pasted` inputs included, after a yes/no (refused when it is the working directory). |
| `/vocalia [reset \| copy <profile> [force]]` | The same as `/operata` for `vocalia.md` (the spoken-reply directive). |
| `/wake [on\|off]` | Toggle the speech-input wake word. |
| `/window` | Show the terminal window's width and height. |

</details>

### Command details

<details>
<summary><b>Click to expand the longer commands</b></summary>

#### Plan mode

`/plan <requirement>` has the model research and present a plan before anything changes. Needs *LLM offer tools*; refused while a reply runs.

* **Tools**: only the read-only ones (reading and searching files, git status/log/diff, the web, SQL, the vault, recall, skills, sessions, `ask_user`) plus `present_plan`. Every tool that writes, runs or starts something, and every MCP tool, is withheld until the plan is approved.
* **Presenting**: the model asks what it needs (your later messages add detail), then presents the plan. It is printed and saved as `.neon/plans/<kebab-name>.md` under the working directory; a new name never overwrites an older plan, and each revision overwrites its own file. 📝 shows on the status strip while planning.
* **Approving**: a pane offers **Approve & run** (`a`), **Approve, clear context & run** (`f`), **Keep refining…** (`r`, with what should change) or **Cancel plan** (`c`). The cursor starts on Keep refining, and ESC picks it too. Approving marks the file `status: approved` and sends a turn with every tool to carry it out, ticking the plan's checkboxes. The fresh-context choice starts a new conversation with the plan's text in the message.
* **Tracking**: once every checkbox is ticked the file is marked `done`; while some are left it is `incomplete` with a `progress: 3/7` line (reported again when the count moves or the reply is stopped).
* `/new`, `/clear` and a profile switch leave plan mode; a restored session is still planning.

| Command | What it does |
|---|---|
| `/plan`, `/plan show` | Say where the plan stands. |
| `/plan <text>` | Add detail. |
| `/plan approve [--fresh]` | Approve the presented plan by typing (you may edit the file first). |
| `/plan cancel` | Leave plan mode; the file is kept, marked `cancelled`. |
| `/plan save [name]` | When a reply looks like a plan but the model never called `present_plan` (a notice says so), keep it as the plan and bring up the approval pane. |
| `/plan open <name>` | Pick a plan up again, in or out of plan mode (names complete from `.neon/plans/`): plan mode turns on over that file, it goes back to `draft`, and the model is asked to read it and ask what should change. `/plan approve` then carries out only the unticked steps. |
| `/plan open` | List the plans with their status and progress. |

#### Bot conversations

`/botchat [profile ...] [topic]` lets profiles talk to each other until you stop them.

* **Cast**: the profiles named, or every profile when none is. This one always joins and speaks first.
* **Topic**: a leading word that isn't a profile starts the topic (`/botchat ada max the best pizza`); after `--` the rest is always the topic (`/botchat ada -- max speed of light`). Without one, the bots pick their own.
* **Turns**: each reply is in the speaker's persona (`persona.md`) and, with speech on, its own voice. A bot named in the last line (yours or the last reply) speaks next; otherwise the next speaker is random, but never the one who just spoke. All bots run on this profile's LLM, or each on its own under *Botchat LLM mode* `multi`.
* **Tools**: none, except pictures (see [Botchat pictures](#botchat-pictures)) and `load_skill` (*Botchat skills enabled*).
* **Joining in**: a line you type joins the chat before the next reply.
* **ESC**: the first press stops the speaking bot's voice (with speech on). The next cuts the replying bot short (its words so far stay) and the next bot answers. One more, before that bot has shown or said anything, ends the chat; a further ESC at the idle line clears your draft. `/exit`, `/clear` and `/new` end the chat, then run.
* **Resume**: `/botchat --resume [line]` continues this run's last chat (same cast, topic, lines and session row); a line after it joins as yours.
* **Pronouns**: each bot is told the others' pronouns from their profile's first TTS voice: `am_`, `bm_`… are male, anything else female.
* **Saved**: with *Session logging* on, the chat is saved as a session of its own; the current conversation is left as it was.

#### Claude Code from the chat

`/claude <message>` runs the `claude` CLI headless in the working directory.

* Its reply streams in under Claude's name, with each tool it uses on a dim line and a footer with the cost and tokens. With speech on, the reply is spoken.
* The question and reply join the conversation, tagged `[to Claude]` and `[Claude]`, so the local model can build on them. Claude doesn't see the local conversation.
* Each session has one Claude conversation: the next `/claude` resumes it (after a restart too, once the session is restored). `/claude new`, `/clear`, `/new` and a profile switch start another.
* What Claude may do is *Claude slash command permissions* (the Claude (CLI) tab of `/tools`); anything beyond it is denied, never asked.
* ESC or Ctrl+C stops Claude, keeping the reply so far. Works with no LLM server; refused while a reply runs.
* Your own Claude Code setup applies: its sign-in, `CLAUDE.md`, skills, MCP servers and hooks. `/usage` shows what the runs cost.

#### Loops

* An optional delay after the count (`30s`, `5m`, `1h30m`, one word, up to 24 hours) waits after each reply: `/loop infinite 1m check the build`. ESC or Ctrl+C during the wait stops the loop.
* A cancelled, withdrawn or failed turn ends the loop.
* The message may be `/imagine …` or `/speak …`, which the loop runs itself with no model in between: `/loop infinite 5s /imagine score_9, 1girl`. A failed generation, a bad path or ESC ends it; `/speak` waits for each reading to be heard. Only the last pass's pictures go with your next message.
* No other commands can be looped.

#### Imagine options

* The picture is drawn in the transcript, saved in *ComfyUI output folder*, and handed to the model with your next message.
* The first word names the workflow when it matches one (the argument list completes the names). Without a name, an offered workflow is used.
* `-- <negative>` sets the negative prompt; `--no-negative` sends none, not even the workflow's default.
* `--count` is capped by *ComfyUI max pictures per call*.
* `--image2` / `--image3` feed a workflow that takes several pictures. One with no prompt (a face swap) runs on its pictures alone: `/imagine faceswap --image a.png --image2 b.png`.
* `/loop` repeats it: `/loop 10 30s /imagine …`.

#### Folder picker

`/cwd browse`, and the *Working directory* and *Obsidian vault* rows, open a folder tree on the pane.

* `⌂ profile` (the profile's `files\` folder) and `▣ splash` (its `splash\` folder) sit above the drives; the tree opens on the directory in use.
* Space, → and ← open and close folders; `-` collapses all. Clicking a folder's glyph, or double-clicking its name, opens or closes it.
* Only Enter chooses. Choosing `⌂ profile` saves the default, like `/cwd ~`.

#### Picture viewer

A picture window (Windows only; elsewhere the app registered for the file opens instead, and `/view` always draws in the transcript). It opens from:

* a double-click on a picture in the transcript (a sent one, one a tool fetched or generated, `/view --chat`, `/imagine`, the splash): the viewer opens on the picture's folder, showing that picture. A pasted picture or the built-in splash has no file, so it is written to `%TEMP%\NeonSidekick\pictures` first. Only a failure prints anything. *Image viewer* can send these elsewhere.
* `/view <image or folder>`, held on the image, or on a folder's newest picture while following new ones.
* `/comfy view` or the picture strip's **🖼 viewer** button: the ComfyUI output folder (created if missing), following new pictures as they are generated. It works while a reply runs.

| Key | Action |
|---|---|
| ← / → | Browse; reaching the newest follows new pictures again |
| Home / End | First / newest picture |
| F11 or double-click | Toggle full screen |
| Del, Del (within 3 s) | Permanently delete the shown picture (the title says "Del again to delete" after the first) |
| F9 | Start or stop a looping slide show (5 s a slide; the title shows `▶ 5 s`) |
| ↑ / ↓ | Slide show: a second more or less per slide (1–60) |
| F10 | Slide show: switch between the folder's order and a random one |
| Esc | Stop the slide show, then leave full screen, then close |

The window follows the theme unless *Themed image viewer* is off: a dark title bar in the theme's colours with an accent edge on Windows 11 (Windows 10 gets a plain dark bar), and the theme's background. A `/theme` change reaches an open viewer the next time it is focused. There is one window per app, and it closes with the app.

#### Profiles

* A name is 1 to 32 letters, digits, `-` or `_`, and not `neon` or one of the verbs.
* A name starting with `_` is temporary: it loads as usual, but the next launch opens `default` (the profile is kept).
* `--profile <name>` (or `NEONSIDEKICK_PROFILE`) opens a profile for one launch, temporary ones included, without changing which one the next launch opens. An unknown name exits with code 2. A `--headless` run with neither opens `default`.
* A reset keeps LLM URL, LLM model, LLM API key, TTS HTTP URL, Claude API key, Web browser path, Web search method, Web SearXNG URL, Claude executable, Obsidian vault and ComfyUI URL; `--all` resets those too. `default` can only be reset while it is loaded.

</details>

## Tools
[↑ Back to top](#neon-sidekick)

What the model can call, in the groups `/tools` and `/sys` show. A group's switch (`File tools`, `Git native tools`, `Shell command policy`, `Obsidian tools`, `SQL tools`, `ComfyUI tools`, `Claude advisor tool`, `Web tools`, `Memory`, `Agent skills`, `Session tool`, `Ask user`, `MCP servers`) offers or withholds the whole group; a single tool can be switched on or off on `/tools`' Offered tab.

<details>
<summary><b>🕒 Clock & Timers</b></summary>

### Clock

| Tool | Arguments | What it does |
|---|---|---|
| `get_current_time` | `zone?` | The current date, time, weekday and time zone; seeded at the start of every conversation. |
| `shift_date` | `date, days?, weeks?, months?, years?` | Adds or subtracts days, weeks, months or years to a date and returns it with its weekday. |
| `days_between` | `from, to` | Counts the days from one date to another (negative when the second is earlier). |

### Timers

| Tool | Arguments | What it does |
|---|---|---|
| `start_timer` | `name?, hours?, minutes?, seconds?` | Starts a named countdown; the user is alerted when it ends. Several can run at once. |
| `stop_timer` | `name` | Stops a running timer by name, or silences one that has gone off. |
| `list_timers` | — | Every running timer and how long each has left. |

</details>

<details>
<summary><b>📁 Files & Git (native)</b></summary>

### Files

All paths are relative to the working directory; nothing outside it is reachable.

| Tool | Arguments | What it does |
|---|---|---|
| `get_working_directory` | — | The working directory's path; seeded at the start of every conversation. |
| `search_files` | `text?, path?, files?, regex?, context?, output?, order?, limit?, depth?` | Searches the text files for a word, phrase or regex (`file:line: text`, with context lines when asked); without `text` it lists a folder, a tree (`depth` 2–4), the files matching a name pattern, or the most recently changed files. |
| `file_info` | `path` | Size, modified time, line and word count, line ending and BOM of a file; counts and total size of a folder; the way to check that something exists. |
| `read_file` | `path, start_line?, max_lines?` | Reads a text file or part of it (a negative `start_line` counts from the end); a partial read names the line to continue from. |
| `view_image` | `path?, paths?` | Attaches image files to the next message so the model can see them — one, or up to *File view image max (per call)* at once. |
| `write_file` | `path, content, mode?` | Writes a text file: `create` (the default, an existing file left alone), `overwrite`, or `append` on a new line; the result reports the size, lines and words. |
| `patch_file` | `path, old_text, new_text, replace_all?` | Replaces one occurrence of `old_text` (or every one with `replace_all`), matched exactly first and then with spacing, indentation, escapes and typographic quotes tolerated; the result shows the edited lines. |
| `create_directory` | `path` | Creates a folder and any missing parents. |
| `move` | `from, to, overwrite?` | Renames or moves a file or folder; refuses to replace anything at the new path unless `overwrite` is true. |
| `copy` | `from, to, overwrite?` | Copies a file or folder to a new path under the same overwrite rule; a folder copied over a folder merges into it. |
| `delete` | `path` | Deletes a file or folder — into `.trash` while *File safe edits* is on, for good when it is off. `.git`, anything in it, and a folder holding one are always refused. |
| `restore` | `path, overwrite?` | Puts back the newest `.trash` copy of a file or folder; with `overwrite` it undoes the last edit of a file. Offered only while *File safe edits* is on. |
| `zip` | `path, to?, overwrite?` | Packs a file or folder into a `.zip` archive, by default beside the original. |
| `unzip` | `path, to?, overwrite?` | Extracts a `.zip` archive into a folder, all or nothing. |
| `open` | `path?` | Opens a file in the user's own editor or viewer, or a folder in Explorer; no path opens the working directory. |

### Git (native)

In-process git (LibGit2Sharp), for when the shell is off or the model should never run `git.exe`. Turn *Git native tools* off to leave git to the shell.

* Local only: no `fetch`, `pull`, `push` or `clone`.
* The repository's root must be the working directory or a folder under it. Every tool takes an optional `path`, the file or folder it targets, which also locates the repository.
* `git_delete` starts off; switch it on in the Offered tab of `/tools`.
* Commits need an identity: set *Git native email* and *Git native name* on the Git (native) tab of `/tools`, then run `/gituser` to write them into the repository's config.

| Tool | Arguments | What it does |
|---|---|---|
| `git_status` | `path?` | The branch, how far ahead or behind its upstream it is, and every staged, modified, untracked or conflicted path. |
| `git_log` | `path?, ref?, max_commits?` | The commits reachable from `ref` (HEAD by default), newest first; with a file, only the commits that changed it. |
| `git_show` | `ref, path?` | One commit: author, date, message and the files it changed; with a file, its text at that commit; with a folder, its entries. |
| `git_diff` | `path?, ref?, from?, to?, staged?, max_lines?` | A unified diff of the unstaged changes, the staged ones, one commit against its parent, or everything between two commits. |
| `git_blame` | `path, from_line?, to_line?, ref?` | Who last changed each line of a file and in which commit, a window of lines at a time. |
| `git_branch` | `action, name?, new_name?, start_point?, switch_to?, path?` | `list`, `create`, `switch` or `rename` branches; a switch never overwrites local changes. |
| `git_stage` | `action, paths, path?` | `stage` or `unstage` the paths named, or `.` for everything changed under `path`. |
| `git_commit` | `message, amend?, allow_empty?, path?` | Commits what is staged, signed with the identity in git config (`user.name` / `user.email`). |
| `git_stash` | `action, message?, index?, include_untracked?, path?` | `push` saves the working tree's changes aside, `pop` or `apply` brings a stash back, `list` shows them. |
| `git_discard` | `paths?, ref?, path?` | Throws uncommitted changes away: the paths named back to `ref`, or with none a hard reset of the whole tree (untracked files left alone). |
| `git_delete` | `kind, name?, index?, path?` | Removes a local `branch` (never the one checked out), a `tag`, or a `stash` by index. |

</details>

<details>
<summary><b>📓 Obsidian</b></summary>

### Obsidian

The vault tools work on the vault's files directly: no plugin, no network, and Obsidian needn't be running.

Notes are found by name, `[[wikilink]]`, alias or path. Inline tags and frontmatter properties both count, dot-folders (`.obsidian`) are ignored, and line endings are kept as they were. An overwritten note's old version goes to the vault's `.trash`.

| Tool | Arguments | What it does |
|---|---|---|
| `vault_search` | `query, tag?, folder?, max_results?` | Every line holding the text (any case) as `path:line`, and every note whose name or alias holds it; narrowed to a tag (or one nested under it) or a folder. |
| `vault_list` | `what?, folder?, tag?, property?, value?, max_results?` | The notes by folder, tag or property (`property: status, value: draft`), or with `what` `tags` / `properties` every tag or property key with how many notes carry it. |
| `vault_read` | `note, heading?, start_line?, max_lines?` | The note with its properties, one heading's section, or a window of lines; a partial read names the line to continue from. |
| `vault_links` | `note` | Its outgoing links and embeds with the note each resolves to (or *unresolved*), and every backlink with its line. |
| `vault_daily` | `date?, append?` | The daily note for a day (`today`, `yesterday`, `+3`, `2026-09-22`) in the folder and date format of the vault's Daily notes settings, created from its template when missing; `append` adds to its end. |
| `vault_write` | `note, content, mode?, heading?` | `create` (a bare name goes where Obsidian puts new notes), `overwrite`, `append` or `prepend` — at the note's end or top, or within one heading's section. |
| `vault_properties` | `note, set?, remove?` | Lists the note's properties, or sets and removes them in one write; only the named keys' lines change. |
| `vault_move` | `note, to` | Renames it (a bare name), moves it into a folder (`Archive/`), or to a new path, and rewrites every link that pointed at it. |
| `vault_delete` | `note` | Offered only while *Obsidian allow delete (.trash)* is on (the default). Moves one note (named as Obsidian names it) or one attachment (by its path) into the vault's `.trash`, where Obsidian can restore it, and lists the notes whose links still point at it; never a folder or anything under a dot-folder. |

</details>

<details>
<summary><b>🗄️ SQL</b></summary>

### SQL

Read-only SQL Server queries over named connections, in-process (`Microsoft.Data.SqlClient`, no ODBC driver). Connections live in `sql.json`: the home folder's is read by every profile, and a profile's own wins on a name clash.

#### Connection settings

* **`server`**: `host`, `host,port` or `host\instance`.
* **`auth`**:
  * `sql`: a SQL login (`user` and `password`).
  * `windows`: your own Windows account.
  * `runas`: another Windows account (`user` as `DOMAIN\name` or `name@domain`, plus `password`). It works like `runas /netonly`: the app runs as you locally but signs in to the server as that account. `SELECT SUSER_SNAME()` shows which account the server sees.
* **`encrypt`**: `strict`, `mandatory` (default) or `optional`.
* **`trustServerCertificate`**: `true` accepts a self-signed certificate.
* **`connectTimeoutSeconds`**: 1–120 (default 15).
* **`passwordStore`**:
  * `file` (default): a password typed into the file is encrypted in place with DPAPI the next time the app reads it, readable only by your Windows account on this machine.
  * `credman`: kept in Windows Credential Manager (`NeonSidekick/sql/<connection_name>`); the file holds no password.

#### Managing connections

* **SQL add connection** (the SQL tab of `/tools`) walks a new connection through one page per choice: the file (profile or global), name, server, database, sign-in, account, password store and password (masked), encryption, certificate trust, connect timeout and description. The summary can **test** the draft (`SELECT @@VERSION`, nothing written) and saves it into the file, comments kept. On a profile that narrowed *SQL connections offered*, it can offer the new one too. ESC steps back a page; Enter on a summary row changes that choice. It only adds; edit existing entries in the file.
* **SQL set password** updates a connection's password.
* Or edit the files directly (comments and trailing commas are allowed): `%USERPROFILE%\.neonsidekick\sql.json` (global) and `%USERPROFILE%\.neonsidekick\profiles\<profile>\sql.json`.

```jsonc
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

`sql_query` runs one read-only statement:

* It is parsed with SQL Server's ScriptDom parser first. Only a single `SELECT` (or a `WITH` CTE ending in one) passes; multi-statement batches, DDL, `EXEC`, `INTO`, `DELETE` and linked servers are refused before reaching the server.
* It runs in a read-only-intent transaction that is always rolled back. Still give the login read-only permissions on the database.
* Values go in as `@name` parameters.
* Results come back as a Markdown table, floating-point numbers at full precision. CLR types (`geography`, `hierarchyid`) need `.ToString()` in the query.

| Tool | Arguments | What it does |
|---|---|---|
| `sql_connections` | — | The named connections: server, database, sign-in and description, the default marked. Touches no server. |
| `sql_databases` | `connection?` | The databases on the connection's server that its login may open, with state, compatibility level and collation. |
| `sql_tables` | `connection?, database?, schema?, pattern?` | The tables and views as `schema.name` with their kind, approximate row count and description (`MS_Description`, shown when the database has any); `pattern` is text anywhere in the name, or a `LIKE` pattern (`%`, `_`, `*`). |
| `sql_columns` | `pattern, connection?, database?, schema?` | Every table and view column whose name matches (`EmailAddress`, `%CustomerID`): where it lives, its type, whether it allows NULL, and its description. |
| `sql_describe` | `table, connection?, database?` | One table or view: its description, its columns (type as declared, nullability, identity, computed, default, primary key, description), the foreign keys out of and into it, its indexes (UNIQUE constraints marked), its CHECK constraints and its triggers. A bare name finds the one schema that has it. |
| `sql_relationships` | `connection?, database?, table?` | The foreign-key join paths `from_table.from_column -> to_table.to_column`, every one or those touching a table. |
| `sql_indexes` | `connection?, database?, table?, schema?, missing?` | The indexes of a table, a schema or the whole database: kind (clustered, PK, unique, unique constraint, disabled), key and included columns, filter and size, then seeks, scans, lookups and updates since the server started (an unread nonclustered index is marked *(no reads since restart)*). `missing: true` adds the optimizer's missing-index suggestions. Usage and suggestions need `VIEW SERVER STATE`; without it the indexes still list, with a line saying why the rest is missing. |
| `sql_query` | `sql, connection?, database?, params?, max_rows?` | One read-only `SELECT`; `params` is an object (`{"id": 43659}` for `@id`), `max_rows` 1–1000 (*SQL max rows* by default). |

</details>

<details>
<summary><b>🎨 Images (ComfyUI)</b></summary>

### Images (ComfyUI)

The image tools run **your own ComfyUI workflows** on your server (*ComfyUI URL*), save the pictures under the working directory, and show them to the model in the next message so it can describe or refine them.

#### Adding a workflow

**With the wizard** (*ComfyUI add workflow* on the ComfyUI tab of `/tools`, one page per choice):

* **Build** makes a standard text → image or image → image workflow from your server's lists: checkpoint, family, folder (this profile's or every profile's), name, CLIP skip, sampler and scheduler, default size, steps, CFG, denoise, negative and description.
* **Import** takes a workflow exported from ComfyUI (*Workflow → Export (API)*). It finds the prompt, negative, seed, steps, CFG, size and input-image nodes, puts the placeholders in, and keeps the export's values as defaults. It reads both a plain `KSampler` graph and the custom-sampler graph FLUX.2 uses (`SamplerCustomAdvanced` with its `RandomNoise`, scheduler and guider; CFG sets the `FluxGuidance`). A value fed by a primitive node gets its placeholder there; one set by another node (a switch, a resolution picker) is left as built, and the wizard says so.
* FLUX.2, Krea 2, Z-Image, Qwen Image, Ernie Image, Boogu, LongCat Image, HiDream I1 and Ideogram 4 load as separate model files, which **Build** can't wire: export ComfyUI's own template for them and **Import** it. (Copybara has no family yet.)
* The summary can **test** the draft (one small run of at most 512 px and 8 steps; nothing saved) and saves it as `<name>.json` + `<name>.md`. On a profile that narrowed *ComfyUI workflows offered*, it can offer the new one too. ESC steps back a page.

**By hand:**

1. Build the workflow in ComfyUI and export it in the **API format**: *Workflow → Export (API)* (or enable dev mode and use *Save (API)*). The regular save, with `nodes` and `links`, is refused with a note saying so.
2. Put placeholders where the call's values go, then drop the file into `<profile>\comfy\` (this profile) or `<home>\comfy\` (every profile; the profile's wins on a name clash). The file name is the workflow's name. Workflows are re-read at every call.

| Placeholder | Becomes |
|---|---|
| `{{prompt}}` | The positive prompt. Required unless the workflow takes an input picture (a face swap or plain upscale has nothing to say; it is listed as *no prompt*). |
| `{{negative}}` | The negative prompt. |
| `{{seed}}`, `{{width}}`, `{{height}}`, `{{steps}}`, `{{cfg}}`, `{{denoise}}` | Numbers when the placeholder is the whole value (`"seed": "{{seed}}"`); text inside a longer string (`"neon-{{seed}}"`). |
| `{{image}}` | The uploaded input picture's name, for a `LoadImage` node; a workflow with it takes an image (img2img, upscale, inpaint). |
| `{{image2}}`, `{{image3}}` | The second and third input pictures (a face swap's face, Qwen-Image-Edit's pictures to compose). They fill in order: `{{image2}}` needs `{{image}}` and `{{image3}}` needs `{{image2}}`, or the workflow is skipped. |
| `{{!name}}` | A literal `{{name}}`, for a workflow whose own nodes use double braces (Ideogram 4's `StringReplace` searches for `{{width}}`). **Import** escapes such text for you. |

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

- **Describe what you want** ("a cozy neon ramen stall at night") and the model writes the prompt in the style of the workflow's family (below). The family's default negative is added unless the sidecar names one.
- **Give your own prompt** ("use this prompt: score_9, …") and the model passes it through unchanged with `verbatim: true`: nothing is added, not even the family's default negative.
- **Skip the model**: `/imagine score_9, score_8_up, source_anime, 1girl -- score_4, blurry --seed 42` sends it straight to ComfyUI. `--no-negative` in place of `-- …` sends no negative at all.
- **Choose what the model may use** with *ComfyUI workflows offered*. With one ticked, the model has no choice to make.

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

A workflow may take up to three pictures (`{{image}}`, `{{image2}}`, `{{image3}}`). For a face swap:

1. Install the ReActor node pack on your ComfyUI server and build the swap there (two `LoadImage` nodes → `ReActorFaceSwap` → `SaveImage`).
2. Export it with *Export (API)* and **Import** it. The `LoadImage` nodes become `{{image}}` and `{{image2}}` in node-id order (the wizard says which got which); a graph with no sampler is fine.
3. Say which picture is which in its `.md`, so the model puts them the right way round:

```markdown
---
description: Face swap (ReActor)
image: the picture whose face is replaced
image2: the face to put in
---
```

Then "put my face from [Image #2] on the person in [Image #1]" works in chat, or `/imagine faceswap --image target.png --image2 face.png` without the model.

Import still expects a `CLIPTextEncode` prompt when there is a sampler. A Qwen-Image-Edit graph, whose prompt goes into `TextEncodeQwenImageEditPlus`, needs `{{prompt}}` put in by hand.

#### The tools

| Tool | Arguments | What it does |
|---|---|---|
| `generate_image` | `prompt?, workflow?, negative?, negative_extra?, verbatim?, width?, height?, seed?, steps?, cfg?, denoise?, image?, image2?, image3?, count?` | Runs a workflow (the only fitting one when none is named) and saves 1 to *ComfyUI max pictures per call* pictures, each with the next seed. The result names the files and the seed; the pictures follow in the next message. `prompt` may be left out only for a workflow without `{{prompt}}`. |
| `set_splash_image` | `path, name?` | Copies a picture from the working directory into the profile's `splash` folder, so it shows at start and on `/splash`. The first picture there replaces the bundled set until more are added, and the result says so. |

* ComfyUI's refusal names the node and input at fault (a missing checkpoint, a bad value).
* `image` is a path under the working directory, or a pasted picture's `[Image #N]` label. A paste is sent at its original size (not the 2048 px copy the model saw) and saved, the first time it is used, into the output folder's `.pasted\` subfolder (`comfy_images\.pasted\pasted-20260924-153012.png`; a dropped file keeps its own name). The result names the saved file.
* `image2` and `image3` are the further pictures, in the roles the workflow's `.md` names. Without a workflow name, the one that takes that many pictures is used.

</details>

<details>
<summary><b>💻 Shell & Web</b></summary>

### Shell

Runs commands on your machine, starting in the working directory (`workdir` picks a folder under it). *Shell command policy* and *Shell police outside paths* guard it (see [Shell guards](#shell-guards)). A denied command returns an error telling the model not to work around it.

* Child processes run hidden, with no window. Output is read as UTF-8, with colours and pagers turned off.
* stdin is closed, except for background processes, which keep it open for `process` to write to.
* A command that times out is killed along with everything it started. Background processes stop when the app closes; if the app crashes, running commands are left running.

#### Headless runs

[HEADLESS.md](HEADLESS.md) has worked examples of every flag, the slash commands that work headless, yolo runs and scheduled jobs.

* `--headless` has no approval pane, so under `ask` only allow-listed commands run. Anything else is refused, and the model is told to say what couldn't run instead of retrying or working around it.
* If any command was refused (by the allow list or the path police), the run ends with a `[notice] N commands were not run: …` line and **exit code 3**. 0 means nothing was refused; 2 means a bad argument or an unknown profile.
* `--yolo` (or `NEONSIDEKICK_COMMAND_POLICY=yolo`; the flag wins) allows every command for one launch, never saved. The path police still applies; `--no-police` (or `NEONSIDEKICK_SHELL_POLICE=off`) lifts it. Together they leave no guard at all: any command, on any path, with your account's rights.
* A headless run loads `default`, not the profile the TUI last used, unless `--profile <name>` (or `NEONSIDEKICK_PROFILE`; the flag wins) names another. That profile's settings, memory, sessions and allow list apply for this launch only, and `settings.json` is left alone. An unknown name lists the profiles and exits with code 2.

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --yolo --cwd D:\Repo\MyApp
Get-Content job.txt | NeonSidekick.exe --headless --profile work
```

| Tool | Arguments | What it does |
|---|---|---|
| `run_command` | `command, shell?, workdir?, timeout?, background?, notify?` | Runs a command in `powershell` (default), `cmd` or `bash` and returns `exit N in T s (shell)…`, then stdout and stderr. `background` (or a long timeout) runs it asynchronously and returns a `proc_…` id; `notify` shows `⚡` when it exits and queues a `process poll` for the model's next turn. |
| `execute_code` | `language, code, timeout?` | Runs a one-off script in `python`, `node` or `powershell` (approved once per language per session; no persistent kernel). With *Shell tool bridge* on, the script can call the app's tools (Python `from neon_tools import call`, Node `await neon.call(...)`, PowerShell `Invoke-NeonTool`), except `execute_code` and `ask_user`; a `run_command` through the bridge still needs approval and can't run in the background. |
| `process` | `action, session_id?, data?, timeout?, offset?, limit?` | Manages up to 16 background processes (and remembers the last 64 finished), addressed by any unique prefix of their id. Actions: `list`, `poll` (state and new output), `log` (a window of the last 5,000 lines), `wait` (up to `timeout`), `kill` (with its children), `write` / `submit` (send text to stdin; `submit` adds a newline), `close` (forget a finished process). |

### Web

| Tool | Arguments | What it does |
|---|---|---|
| `web_search` | `query, max_results?` | Searches the web (DuckDuckGo or SearXNG) and returns the top results: title, URL, snippet. |
| `web_fetch` | `url, offset?` | Fetches a page and returns its readable content as Markdown, 32,000 characters at a time; also reads plain text, JSON, XML and CSV. |
| `open_url` | `url?, urls?` | Opens a link — or up to five — in the user's own browser. |
| `download_file` | `url, path?, overwrite?` | Downloads a file (a picture, a PDF, an archive…) into the working directory, up to 50 MB; needs *File tools* on too. |

</details>

<details>
<summary><b>🧠 Memory, Skills & Sessions</b></summary>

### Memory

| Tool | Arguments | What it does |
|---|---|---|
| `save_memory` | `text` | Saves one lasting fact about the user to long-term memory, known in every later session. |
| `recall_memory` | — | Everything remembered about the user, oldest first; seeded at the start of every conversation. |

### Skills

| Tool | Arguments | What it does |
|---|---|---|
| `load_skill` | `name, file?` | Loads a skill's full instructions by name (the catalog is in the system prompt), or one of its bundled files, up to 64,000 characters (twice `read_file`'s, since it can't page). Offered only while a skill is installed. |
| `skill_editor` | `action, scope?, name, description?, instructions?, path?, content?, old_text?, new_text?, replace_all?, summary?` | `create` or `update` a skill under the `profile` (default) or `global` root. `write_file` (whole file, `content`) and `edit_file` (`old_text` → `new_text`, matched as `patch_file` does) maintain an existing skill's supporting files, `path` relative to the skill folder; never `SKILL.md` itself, `.neon-source.json`, or anything in `.git`, `node_modules` or `.trash`. With *File safe edits* on, the previous version goes to the skill's own `.trash`. External skills are read-only; it never deletes. |

### Sessions

| Tool | Arguments | What it does |
|---|---|---|
| `session_manager` | `action, query?, id?, max_results?, from_turn?, to_turn?` | `search`, `list` or `read` this profile's earlier conversations; the one on screen is left out, and nothing is restored or purged. |

### Claude advisor

| Tool | Arguments | What it does |
|---|---|---|
| `claude_advisor` | `question, context?` | Offered while *Claude advisor tool* is on. Asks Claude Code for read-only advice (it can read and search the working directory and the web). The transcript shows the question, each tool Claude uses, the answer and a cost footer. It keeps its own Claude conversation per session, separate from `/claude`'s; `/new`, `/clear` and a profile switch start another. ESC stops it along with the reply; `/usage` counts it. |

### Questions

| Tool | Arguments | What it does |
|---|---|---|
| `ask_user` | `questions` | Puts up to *Ask max questions* multiple-choice questions on the pane (each with 2 to *Ask max choices per question* options, `single` or `multi`, plus an *Other…* row) and waits for the answers; ESC declines them all. |

### Plan

| Tool | Arguments | What it does |
|---|---|---|
| `present_plan` | `title, markdown, name?` | Offered only in [plan mode](#plan-mode). Saves the plan as `.neon/plans/<name>.md` (kebab-cased; the first presentation fixes the file, revisions overwrite it) with a `status` / `revision` / `requirement` header, prints it and asks for approval. The result is your verdict: approved, changes wanted (with your words), or cancelled. Headless, it is saved and `/plan approve` starts it. `status` is `draft`, `approved`, `cancelled`, `done` or `incomplete`. |

</details>

<details>
<summary><b>🔌 MCP servers</b></summary>

### MCP servers

Each connected server is its own tool group.

* Its tools are named `<server>__<tool>` (`gateway__get_current_time`), so they never collide with the app's own tools.
* Each keeps the description its server gives.
* Switching a server off on the Servers tab of `/mcp` removes its whole group; the Tools tab switches single tools.

</details>

## Environment variables
[↑ Back to top](#neon-sidekick)

Every variable the app reads starts with `NEONSIDEKICK_`. They override a setting for one launch and are never saved.

* **Precedence:** command-line flag > variable > the profile's saved setting > default. A settings row that a variable (or flag) overrides says so.
* **Values:** blank means unset; values are trimmed and words match in any case. A value that doesn't parse is logged as a warning and ignored, and the launch goes on.
* **Logging:** with `--log`, the startup lines list the variables in force; the API keys show only as `(set)`.

[HEADLESS.md](HEADLESS.md) shows them in use for scripted runs.

### Where and who

| Variable | What it does | Accepts |
|---|---|---|
| `NEONSIDEKICK_HOME` | The home folder: `settings.json`, `profiles\`, `models\`, `mcp.json`, `sql.json`. | A folder path. Default `%USERPROFILE%\.neonsidekick`. |
| `NEONSIDEKICK_PROFILE` | The profile for this launch; `settings.json` is left pointing where it was. An unknown name exits with code 2. `--profile` wins. A headless run with neither loads `default`. | A profile name. |

### LLM

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_LLM_URL` | LLM URL (`--url` wins) | A base URL, e.g. `http://127.0.0.1:1234/v1`. |
| `NEONSIDEKICK_LLM_MODEL` | LLM model (`--model` wins) | A model id from the server. |
| `NEONSIDEKICK_LLM_API_KEY` | LLM API key | The key. Never written to the log. |
| `NEONSIDEKICK_LLM_REASONING` | LLM reasoning | `none`, `low`, `medium`, `high`, `xhigh`. |
| `NEONSIDEKICK_LLM_REQUEST_TIMEOUT` | LLM request timeout (s) | Seconds, above 0 and up to 3600. |
| `NEONSIDEKICK_LLM_TURN_TIMEOUT` | LLM turn timeout (s) | Seconds, above 0 and up to 21600. |
| `NEONSIDEKICK_LLM_CONTEXT` | LLM context length | Tokens, a positive whole number. For servers that don't report their context window. |

### Shell

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_COMMAND_POLICY` | Shell command policy (`--yolo` wins) | `off`, `ask`, `yolo`. Under `ask` with no screen (headless), only allow-listed commands run. |
| `NEONSIDEKICK_SHELL_POLICE` | Shell police outside paths (`--no-police` wins) | `on`/`off` (also `true`/`false`, `1`/`0`, `yes`/`no`). |
| `NEONSIDEKICK_SHELL_NATIVE` | Shell prefer native tools | `on`/`off` (also `true`/`false`, `1`/`0`, `yes`/`no`). |

### Claude

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_CLAUDE_EXE` | Claude executable | The full path of the Claude Code CLI. |
| `NEONSIDEKICK_CLAUDE_PERMISSIONS` | Claude slash command permissions | `read-only`, `edit`, `full`. |
| `NEONSIDEKICK_CLAUDE_ADVISOR` | Claude advisor tool | `on`/`off` (also `true`/`false`, `1`/`0`, `yes`/`no`). |
| `NEONSIDEKICK_CLAUDE_API` | Claude API | `on`/`off` (also `true`/`false`, `1`/`0`, `yes`/`no`). |
| `NEONSIDEKICK_CLAUDE_API_KEY` | Claude API key | The key, as issued (not encrypted). Never written to the log. |

### Speech

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_TTS_URL` | TTS HTTP URL | A Kokoro HTTP server's URL. |
| `NEONSIDEKICK_TTS_VOICE` | TTS voice | A voice name, e.g. `af_heart`. |
| `NEONSIDEKICK_TTS_VOICE2` | TTS voice 2 | A voice name. It can't clear a saved second voice; `NEONSIDEKICK_TTS_MIX=100` plays the primary alone. |
| `NEONSIDEKICK_TTS_MIX` | TTS voice mix | The primary voice's share, 0–100. |
| `NEONSIDEKICK_TTS_SPEED` | TTS speed | A multiplier, 0.5–2.0. |
| `NEONSIDEKICK_WHISPER_MODEL` | STT whisper model | A model name or a path to a ggml file. |
| `NEONSIDEKICK_INTERRUPT_ECHO` | STT interrupt echo guard | A percentage, 50–100. |
| `NEONSIDEKICK_INTERRUPT_CONFIRM` | STT interrupt confirm | Milliseconds, 0–2000. |

### Integrations

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_SEARXNG_URL` | Web SearXNG URL | The instance's URL. *Web search method* still picks the engine. |
| `NEONSIDEKICK_OBSIDIAN_VAULT` | Obsidian vault | The folder holding `.obsidian`. |
| `NEONSIDEKICK_COMFY_URL` | ComfyUI URL | The ComfyUI server's URL, e.g. `http://gpu-box:8188`. |

### Set by the app

`NEONSIDEKICK_BRIDGE_ADDRESS` and `NEONSIDEKICK_BRIDGE_TOKEN` are passed to an `execute_code` script while *Shell tool bridge* is on, for the bundled `neon_tools` modules. Don't set them yourself. To find shells and interpreters, the app also reads the standard `PATH`, `PATHEXT`, `ProgramFiles`, `ProgramW6432` and `LocalAppData`.

### Test suite

These only matter when running the test suite from source; each live test is skipped unless its resource is there.

* `NEONSIDEKICK_TEST_LLM_URL`, `NEONSIDEKICK_TEST_TTS_URL`, `NEONSIDEKICK_TEST_SQL_CONNECTION`: a server to test against.
* `NEONSIDEKICK_TEST_WHISPER_MODEL`, `NEONSIDEKICK_TEST_SILERO_MODEL`, `NEONSIDEKICK_TEST_VOSK_MODEL`, `NEONSIDEKICK_TEST_KOKORO_MODEL`: a model, when it isn't already under `%USERPROFILE%\.neonsidekick\models`.
* `NEONSIDEKICK_TEST_CLAUDE=1`: the live Claude Code tests, on your own sign-in (Haiku; a few cents a run).
* `NEONSIDEKICK_TEST_CLAUDE_API_KEY`: the live Claude API tests, with that key (Sonnet 5 and Opus 5.5; a few cents a run).

## Screenshots
[↑ Back to top](#neon-sidekick)

Explore the UI and features of Neon Sidekick by expanding the panel below.

<details>
<summary><b>✨ Interface Examples</b></summary><br>
<table>
  <tr>
    <td><img src="./assets/screenshots/screen_markdown.png" alt="Markdown rendering"><br><center><b>Markdown rendering</b></center></td>
    <td><img src="./assets/screenshots/screen_vision.png" alt="Vision support"><br><center><b>Vision support</b></center></td>
  </tr>
  <tr>
    <td><img src="./assets/screenshots/screen_code.png" alt="Tool calls and code blocks"><br><center><b>Tool calls and code blocks</b></center></td>
    <td><img src="./assets/screenshots/screen_splash.png" alt="Welcome splash screen"><br><center><b>Welcome splash screen</b></center></td>
  </tr>
  <tr>
    <td><img src="./assets/screenshots/screen_ask.png" alt="Ask user"><br><center><b>Ask user</b></center></td>
    <td><img src="./assets/screenshots/screen_menus.png" alt="Intuitive menu panes"><br><center><b>Intuitive menu panes</b></center></td>
  </tr>
</table>
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
* `Microsoft.ML.OnnxRuntime`
* `KokoroSharp`
* `Whisper.net`
* `Silero VAD`
* `Vosk`
* `PhotoSauce.MagicScaler`
* `Markdig`
* `LibGit2Sharp`

## Why "Neon"
[↑ Back to top](#neon-sidekick)

During early development, I was experimenting with synthwave-style themes in Spectre.Console while simultaneously testing the Vosk voice integration. I needed a short, punchy wake word, and "Neon" fit the aesthetic perfectly. The name stuck for the project. Today, while the default profile is still named "Neon," the system is completely configurable—allowing you to create as many custom profiles, personas, and wake words as you like.