# Settings & menus

Every setting on the `/settings`, `/skills`, `/tools` and `/mcp` panes, with what it does and its default, and what `/sys` shows. How to move around the panes, the keys, the bars and the app's windows are in [Using the app](INTERFACE.md); the basics are in the [README](../README.md#settings--menus).

Inside the app, the highlighted row of any pane explains itself, and the model's `neon_help` tool answers questions about any setting. Settings an environment variable or flag can override for one launch are listed in [Environment variables](ENVIRONMENT.md). Paths here use Windows' `\`; on a Mac read `/`.

## Contents

- [Panes](#panes)
- [App settings (`/settings`)](#app-settings-settings): [Client](#client) ([Chat](#chat), [Sessions](#sessions), [Botchat](#botchat), [Web UI](#web-ui)) · [Servers](#servers) ([Common](#common), [Http](#http), [Embedded](#embedded), [Docker](#docker), [Anthropic](#anthropic), [OpenAI](#openai)) · [Advisors](#advisors) · [Voice](#voice) ([TTS](#tts), [STT](#stt))
- [Skills settings (`/skills`)](#skills-settings-skills): Offered, Reflection, Options, and managing skills
- [Tools settings (`/tools`)](#tools-settings-tools): Offered · [Core](#core) · [Files](#files) · [Apps](#apps) · [Databases](#databases) · [Devices](#devices) · [Home](#home)
- [MCP servers (`/mcp`)](#mcp-servers-mcp)
- [System prompt (`/sys`)](#system-prompt-sys)

## Panes

* `/settings`: the app (Client), the LLM and its servers (Servers), the profile's advisors (Advisors) and voice (Voice).
* `/skills`: agent skills and self-reflection.
* `/tools`: the model's tools, grouped as the tabs below.
* `/mcp`: external MCP servers.
* `/sys`: a read-only view of what the model is sent.
* `/usage`: LLM usage statistics (tok/s, time to first token…); it has no settings.

## App settings (`/settings`)

### Client

The app itself, this side of the connection.

#### Chat

| Setting | What it does | Default |
|---|---|---|
| Profile | Switches to another profile, each with its own settings, persona, memory, skills and sessions. | `neon` |
| Working directory (cwd) | The folder the file and GitLib tools work in. Enter opens the [folder picker](INTERFACE.md#folder-picker); `/cwd <path>` sets a typed path. | empty (the profile's `files\` folder) |
| Memory mode | `read-write`: the model saves and recalls memories, and each conversation opens with them. `read-only`: it recalls only (your `/remember` still saves). `off`: no memory, and `/remember` is refused. | `read-write` |
| Model avatar | The profile's emoji, at the start of its replies, its `/botchat` lines and the window title. Enter (or `/avatar`) opens the [avatar picker](INTERFACE.md#the-avatar-picker). | 🤖 |
| User avatar | Your emoji, at the start of each of your lines in the transcript in place of `›`. Enter opens the same [picker](INTERFACE.md#the-avatar-picker); 🚫 goes back to `›`. | 🙊 |
| Queue mode | `automatic`: a message sent during a reply waits in `/queue` and goes when the reply ends. `manual`: every message waits until you press `➤ send` (`s`) on `/queue`. `off`: no list. | `automatic` |
| Queue cancel mode | What a canceled reply does with the queue: `hold` keeps it until your next message, `drain` sends the next one at once, `empty` drops them all. | `empty` |
| Keep message history | Saves the ↑/↓ history (the newest 1,000 lines) in the profile's `sessions.db`. Off deletes it at the next profile load; `/msgclear` empties it. | on |
| Command typo intercept | A command typed without its slash (`clear`), or with extra ones, asks *Did you mean /clear?* before it is sent as text. | on |
| Show /exit in autocomplete | Lists `/exit` in the `/` list. Typed in full, it works either way. | off |
| Emoji picker | Shows a dim ☺ at the right end of the input row; a click opens the emoji picker at the cursor. Ctrl+J opens it too, shown or not (not on a Mac). | on |
| Transcript markdown | Renders replies as Markdown, with code blocks highlighted for common languages. | on |
| Paste preview lines | How many lines of a long paste show dimmed under its `[Pasted text #n]` placeholder (0–200). | 25 |
| Show image thumbnails | Draws a small thumbnail of each picture you send, a tool fetches or makes, or `/botchat` draws. | on |
| Image thumbnail size | In columns × rows: `tiny` 32×8, `small` 48×12, `medium` 64×16, `large` 80×20, `xlarge` 96×24, or `fullsize`. | `small` |
| Copy user prompt | `/copy` puts your prompt above the reply. | on |
| User line style | How your sent line looks: `quiet` (plain), `slab` (on a faint fill) or `bold` (bold in the user color). | `bold` |
| Theme | One of the sixty built-in themes, or your own from the home folder's `themes` folder (see [Custom themes](COMMANDS.md#custom-themes)). A wide window previews the highlighted one. | `collider` |
| Themed background | The terminal takes the theme's background while the app runs. | on |
| Themed external windows | The [app's own windows](INTERFACE.md#the-apps-windows) wear the theme. Off, they stay black. | on |
| Welcome splash | Pictures under the banner at startup until your first line: `fullsize`, `tiled` or `off`. See [The welcome splash](INTERFACE.md#the-welcome-splash). | `fullsize` |
| Show header | Shows the banner at startup and after `/clear`, `/splash`, `/theme` and a profile switch. `/header` and Ctrl+Alt+H flip it. | on |
| Working directory in header | Prints the working directory at the right of the banner's title line. | off |
| Menus max height | How much of the window a pane may take: `half`, `three-quarters` or `full`. Longer lists scroll. | `full` |
| Hints bar | Whether the keys' hints get a bar of their own over the status bar: `auto` (a row while there is a hint, held until the next send), `always` or `off` (on the status bar). See [The hints bar](INTERFACE.md#the-hints-bar). | `auto` |
| Status bar | Which sections the status bar shows, their order, and which side each is pinned to. Enter opens the checklist; see [The status bar](INTERFACE.md#the-status-bar) and [Bar checklists](INTERFACE.md#bar-checklists). | `default`: every section but Working directory; Model and Working directory pinned right |
| Tool bar | Which glyphs the toolbar shows, their order, and which side each is pinned to. Enter (or `/toolbar edit`) opens the checklist; see [The toolbar](INTERFACE.md#the-toolbar). | Settings, Tools, Skills, Memory, Saved prompts, Queue, Shell allowed commands, Shell police, Shell, Files, Image, Web, path (13 of 42), with three dividers |
| Performance bar | Which meters the performance bar shows, and where. Enter (or `/perfbar edit`) opens the checklist, whose **look:** button (`l`) steps the look; `/perfbar text`, `gauge` or `led` picks it too. See [The performance bar](INTERFACE.md#the-performance-bar). | CPU, RAM, GPU, VRAM, `led` |
| Home bar | Home Assistant rooms and devices, and the shopping list's 🛒, as chips on a row of their own. Enter (or `/homebar edit`) opens the [picker](INTERFACE.md#the-home-bar). | off |
| Toolbar order | The order of the five bars under the input line (**Order** on the pane). Enter opens the list; Shift+↑/↓ moves a bar. | `default` (hints bar, status bar, toolbar, performance bar, home bar) |
| Draft editor | The program `/draft` opens (`code --wait`, `notepad`…); it must wait for the file to close. | empty (the system's editor for `.txt` files) |
| Image viewer | Where a double-clicked picture opens: empty for the built-in [picture viewer](INTERFACE.md#the-picture-viewer), `system` for the system's app, or a command the path is added to (`mspaint`). | empty (the picture viewer) |

#### Sessions

| Setting | What it does | Default |
|---|---|---|
| Session logging | Saves every turn to the profile's `sessions.db`, for `/sessions` to list, search and restore. | on |
| Session save thinking | Saves each reply's thinking, so a resumed session can send it back under *LLM preserve thinking*. | off |
| Session retention (days) | Deletes sessions older than this at startup (0–3650; 0 keeps them forever). | 0 |
| Session naming mode | `model-written` asks the model for a title after the first turn; `first-line` uses your first line. | `model-written` |
| Session show name | Which titles show on the rule above the input row: `all`, `model-written` (a name the model wrote or you typed) or `none`. | `all` |
| Session tool | Offers the model `session_manager`, to search, list and read earlier sessions (never to restore or purge them). | on |
| Session search max results | Sessions one search or list returns (1–20). | 10 |

#### Botchat

What `/botchat` bots get. See [Bot conversations](COMMANDS.md#bot-conversations).

| Setting | What it does | Default |
|---|---|---|
| Botchat LLM mode | `single`: every bot uses this profile's server, model and reasoning. `multi`: each bot uses its own profile's (a blank URL borrows this one's). | `single` |
| Botchat embedded mode | Under `multi`, for bots that want another embedded model. `parent-server`: they share the running model. `multi-server`: one extra `llama-server` per model (more memory). | `parent-server` |
| Botchat stop extra servers | Stops the `multi-server` extras when the chat ends. Off, they run until `/botchat --kill` or you quit. | on |
| Botchat ComfyUI enabled | Gives the bots this profile's *ComfyUI workflows offered* for pictures. Needs *ComfyUI tools* and a *ComfyUI URL*. | off |
| Botchat ComfyUI limited workflows | With *Botchat ComfyUI enabled* off, the workflows the bots get (a checklist). None ticked: no pictures. | none |
| Botchat image mode | `automatic`: the app writes a prompt from each reply and draws it. `autonomous`: the bots get `generate_image` and draw when they choose. See [Botchat pictures](#botchat-pictures). | `automatic` |
| Botchat rework source | Which pictures a rework may start from: the `latest`, or any in `chat-history` (the last 8). Needs an image → image workflow. | `latest` |
| Botchat image mention mode | Shown as *Image mention mode*. Under `autonomous`: `generate` draws a picture a bot mentions but never drew; `off` ignores it. | `off` |
| Botchat image async | On: the next bot speaks while a picture renders. Off: each reply waits for its picture. | on |
| Botchat reading pause (s) | A pause after each reply when *TTS output* is off, to read it (0–30). A line you send ends it. | 5 |
| Botchat tools enabled | Offers every bot the tools a turn of this chat would get, with the same approvals. Off, *Botchat limited tools* decides. | off |
| Botchat limited tools | With *Botchat tools enabled* off, the tools the bots get (a checklist grouped as on `/tools`). None ticked: no tools. | none |
| Botchat skills enabled | Offers every bot `load_skill` over the skills this chat sees. Needs *Agent skills*. Off, *Botchat limited skills* decides. | off |
| Botchat limited skills | With *Botchat skills enabled* off, the skills the bots may load (a checklist). None ticked: no skill tool. | none |
| Botchat memory enabled | Gives every bot memories (`save_memory`, `recall_memory`). Inside `/botchat` this overrides every profile's *Memory mode*. | on |
| Botchat memory mode | Whose memories: `shared-parent` (the starting profile's) or `independent` (each bot's own profile's). | `shared-parent` |
| Botchat vision enabled | Shows each bot the newest 4 pictures since it last spoke. Needs models that read images. | off |
| Botchat camera | Each bot turn gets a fresh camera picture of you, kept in memory only. Needs models that read images. | off |
| Botchat recent context | Tells every bot the last 10 messages from before `/botchat` began, so the topic can point at them. | off |

##### Botchat pictures

* **Workflows:** the bots use this profile's offered workflows (*Botchat ComfyUI enabled*) or the ticked ones (*Botchat ComfyUI limited workflows*). Text → image workflows draw new pictures; image → image ones rework the chat's pictures.
* **`automatic`:** after each reply the model writes an image prompt and the app draws it. The bots get no tool. With skills on, the prompt writer may load a skill first ("use the pony-prompts skill for pictures").
* **`autonomous`:** the bots get `generate_image`, limited to those workflows. A call a bot writes out as text still runs, and a call that fails is drawn by the app.
* **Reworks:** once there's a picture and an image → image workflow, the prompt writer chooses between a new picture and a rework.
* **Async on:** 🖼️ (🎨 for a rework) shows on the status bar while a picture renders. **Async off:** ESC on a held reply cuts that bot short; ESC under the picture's spinner skips the picture.

#### Web UI

A browser front end for this profile: the chat, its questions and the shopping list, from a phone on the home network. See [the Web UI](INTERFACE.md#web-ui).

| Setting | What it does | Default |
|---|---|---|
| Web UI | Serves the pages for this profile. `/webui` shows the sign-in link. A profile switch hands the server to the other profile's settings. Refused while a reply runs. | off |
| Web UI port | The TCP port it listens on (1024–65535). A port in use, or one Windows reserves, keeps it off with a notice. Changing it signs browsers out. | 8765 |
| Web UI reach | `local` (localhost only) or `lan` (your home network and your tailnet). Either way a browser signs in with `/webui`'s link. | `lan` |

### Servers

The LLM connection and every kind of server it can be.

#### Common

Settings every server reads, whatever its kind.

| Setting | What it does | Default |
|---|---|---|
| LLM URL | The server's OpenAI-compatible base URL (`http://127.0.0.1:1234/v1`), `embedded`, or `docker:<container>`; `/server` fills it in. Empty: the app scans, then lets you pick a server, model and reasoning at startup. | empty |
| LLM model | The model id. Empty takes the first the server lists; `/model` picks one. | empty (first listed) |
| LLM reasoning | How hard the model thinks: `none`, `low`, `medium`, `high` or `xhigh`. `/reasoning` opens the same list. | `none` |
| LLM show thinking | Streams a reasoning model's thinking as a dim block, folded to one line when the answer starts (click it, Ctrl+O or `/expand`). Thinking is never spoken or logged. | on |
| LLM offer tools | Whether the model gets any tools. Turn it off for chat templates with no tool role. A change starts a new conversation. | on |
| LLM max tool iterations | Tool round trips one message may make (1–10000). | 10000 |
| LLM request timeout (s) | The longest one HTTP request may take (up to 3600). | 3600 |
| LLM turn timeout (s) | The longest one whole turn may take, tool calls included (up to 21600). | 21600 |
| LLM plan gap passes | The most passes [plan mode](COMMANDS.md#plan-mode)'s **Close gaps…** review runs before the plan comes back with gaps left (0–20; 0 is no cap, or 20 headless). ESC stops it. | 5 |
| LLM context length | The context window in tokens, for the usage percentage. 0 takes the server's figure. | 0 (the server's) |
| LLM mid-turn usage | The status bar's usage during a reply: `estimate` (live, marked `~`) or `last-known` (the last finished request's figures). | `last-known` |
| LLM max turns | User turns the model sees before the oldest drop off (1–500). `auto` keeps them all while auto compact can run, else 24. | `auto` |
| LLM auto compact (%) | How full the context may get before the next message compacts it first (1–100; 0 is off). | 90 |
| LLM compact mode | What `/compact` does: `summary` folds older turns into a summary the model writes; `prune` swaps their bulky tool results for stubs. | `summary` |
| LLM compact keep recent | Recent user turns a compact keeps word for word (0–24). | 2 |
| LLM compact show summary | After a compact, shows the summary (or the pruned results) and how many messages were kept. | off |
| LLM tool compact mode | What happens when one turn's tool calls fill the context to the auto-compact share: `compact` (prune, then summarize), `prune`, `stop` the turn, or `off` (no check). | `compact` |
| LLM max images | The most pictures one request carries (0–500; 0 is no cap). Past it the oldest leave the conversation, and `view_image` can show one again. | 20 |
| LLM max images (MB) | The most megabytes of pictures one request carries (0–1000; 0 is no cap). Past it the oldest leave. | 24 |
| LLM fun verbs | The thinking spinner shows a random verb in place of `thinking` / `writing`. | off |

#### Http

Settings only an OpenAI-compatible HTTP server reads: a local or LAN server (LM Studio, llama.cpp, vLLM, SGLang, Ollama), the embedded model and Docker containers. The Anthropic API, the OpenAI API and the Claude Code server ignore them.

| Setting | What it does | Default |
|---|---|---|
| LLM server scan mode | Where to look for a server while *LLM URL* is blank: `local` (the usual ports here), `lan` (the same ports across the LAN), `both`, or `off`. | `off` |
| LLM API key | The bearer token the server expects (`empty` for none), saved encrypted for your Windows account, or on a Mac under a key kept in the Keychain. | `empty` |
| LLM preserve thinking | Sends earlier turns' thinking back to a local server and asks the chat template to keep it. It costs context. | off |
| LLM reasoning estimate | How `/usage` counts thinking a server streams but doesn't count: `chars` (characters ÷ 4), `tokenize` (llama.cpp's exact count) or `off`. | `chars` |
| LLM sampling | Sampling overrides per model; Enter opens the `/sampling` pane. See [Sampling per model](#sampling-per-model). | empty (the server's defaults) |
| LLM sampling from Hugging Face | For servers that don't report their defaults (vLLM, SGLang, LM Studio), shows the model card's values in `/sampling`. Display only; your API key is never sent. | off |

#### Sampling per model

Until you set something, sampling is left to the server and the model's defaults.

* `/sampling` (or the *LLM sampling* row) has one tab per model: the connected model, then **any model (`*`)**, then every other model you have set something for.
* Each field comes from the model's tab, else from `*`, else isn't sent. Changes apply at the next turn. A blank value clears a field; an out-of-range one is refused.

| Field | Accepts | Sent as | Servers that honor it |
|---|---|---|---|
| temperature | 0 to 5 | `temperature` | all |
| top_p | above 0, up to 1 | `top_p` | all |
| top_k | a whole number from -1 (0 or -1 is off, depending on the server) | `top_k` | vLLM, SGLang, llama.cpp, LM Studio |
| min_p | 0 to 1 | `min_p` | vLLM, SGLang, llama.cpp |
| presence_penalty | -2 to 2 | `presence_penalty` | all |
| frequency_penalty | -2 to 2 | `frequency_penalty` | all |
| repetition_penalty | above 0, up to 2 (1 is none) | `repetition_penalty` **and** `repeat_penalty` | vLLM and SGLang read the first, llama.cpp and LM Studio the second |
| extra body | a JSON object | its fields, at the top level | whatever the server knows: `{"typical_p":0.9,"dry_multiplier":0.8,"seed":42}` |

* **Server defaults** show dim on the connected model's tab (`0.8 (server)`) where the server reports them (llama.cpp, Ollama).
* **The extra body** may not set fields the app writes (`model`, `messages`, `tools`…) or the named fields above.
* **Without the pane:** `/sampling temperature 0.6`, `/sampling top_k clear`, `/sampling extra {"seed":42}` and `/sampling clear`. `NEONSIDEKICK_LLM_SAMPLING` overrides every model for one run.
* The Anthropic API, the OpenAI API and the Claude Code server are not affected.

#### Embedded

A model the app downloads and runs itself on llama.cpp's `llama-server`, for when no other server is around. It runs on Windows x64 and on Apple Silicon Macs.

* **Install:** pick *Install* on **Embedded models**. It downloads in the background (📥 on the status bar; a double-click pauses it), then switches to the model. With no URL, no scan and nothing installed, the app opens straight to this list.
* **Use:** `/server` and the startup picker list one **Embedded** row per installed model; `/model` picks between them.
* **Loading:** the status bar shows `🦙 loading <model>`; Ctrl+C or a double-click on the spinner cancels.
* **Each row** shows the size, the state (`installed`, `download`, `partial · 42%`) and the quantization, then ⚡ a drafter, 👁️ images, 🛠️ tool calls, and ⛓️‍💥 an uncensored build.
* **The title row's buttons** combine: **size** (`s`: any, up to 8GB, up to 16GB), **show** (`i`: all, installed, uninstalled), **drafter** (`d`), **sort** (`o`: name or size) and **uncensored** (`x`: only the uncensored builds, hidden otherwise).

The catalog, one row per model; the pane lists each build with its own size. An id's suffix names the quantization: none is UD-Q4_K_XL, `-q5`, `-q6` and `-q8` are UD-Q5_K_XL, UD-Q6_K_XL and UD-Q8_K_XL, `-bf16` is BF16, and `-qat` is a QAT build at UD-Q4_K_XL (all Unsloth's). The uncensored builds are HauhauCS's.

| Model | Ids | Download | Drafter |
|---|---|---|---|
| Gemma 4 E2B | `gemma-4-e2b`, `-q5`, `-q6`, `-q8`, `-bf16` | 4.3–10.4 GB | MTP (file) |
| Gemma 4 E2B Uncensored | `gemma-4-e2b-uncensored` (Q4_K_P) | 4.4 GB | — |
| Gemma 4 E4B | `gemma-4-e4b`, `-q5`, `-q6`, `-q8`, `-bf16`, `-qat` | 5.3–16.1 GB | MTP (file) |
| Gemma 4 E4B Uncensored | `gemma-4-e4b-uncensored` (Q4_K_P) | 6.4 GB | — |
| Gemma 4 12B | `gemma-4-12b`, `-q5`, `-q6`, `-bf16`, `-qat` | 7.1–24.5 GB | MTP (file) |
| Gemma 4 12B QAT Uncensored | `gemma-4-12b-qat-uncensored` (Q4_K_M) | 7.8 GB | MTP (file) |
| Gemma 4 26B A4B | `gemma-4-26b-a4b`, `-q5`, `-q6`, `-qat` | 15.7–25 GB | MTP (file) |
| Gemma 4 26B A4B QAT Uncensored | `gemma-4-26b-a4b-qat-uncensored` (Q4_K_M) | 18.2 GB | MTP (file) |
| Gemma 4 26B A4B Uncensored | `gemma-4-26b-a4b-uncensored` (Q4_K_P), `-q5` (Q5_K_P), `-q6` (Q6_K_P) | 18.1–24 GB | — |
| Gemma 4 31B | `gemma-4-31b`, `-q5`, `-qat` | 18.8–23.6 GB | MTP (file) |
| Gemma 4 31B QAT Uncensored | `gemma-4-31b-qat-uncensored` (Q4_K_M) | 20.2 GB | MTP (file) |
| Muse Glimmer 30B | `muse-glimmer-30b`, `-q5` | 19.6–25.5 GB | DFlash (file) |
| Qwen3.6 35B A3B | `qwen3.6-35b-a3b`, `-q5` | 23.3–27.5 GB | — |
| Qwen3.6 35B A3B Uncensored | `qwen3.6-35b-a3b-uncensored` (Q4_K_P) | 24.3 GB | — |
| Qwen3.8 27B | `qwen3.8-27b`, `-q5`, `-q6` | 18.5–26.2 GB | MTP (built in) |
| Qwen3.8 27B NVFP4 (esatapedico) | `qwen3.8-27b-nvfp4-` and `very-low`, `compact-low`, `low`, `medium`, `mid-high`, `high`, `very-high` or `highest` | 15.8–24.1 GB | MTP (built in) |
| Qwen3.8 27B Uncensored | `qwen3.8-27b-uncensored` (Q4_K_P), `-q5` (Q5_K_P) | 18.9–21.1 GB | MTP (built in) |

**Drafter:** **MTP (file)** is a small file downloaded with the model; **MTP (built in)** is part of the model's weights; **DFlash (file)** is a separate 1.6 GB drafter; **—** has none.

| Setting | What it does | Default |
|---|---|---|
| Embedded servers enabled | Offers the embedded models. Off, `/server` lists none and a running embedded server stops; installed models stay on disk. | on |
| Embedded models | The catalog. Enter on an installed model offers *Use now* and *Remove*; on any other, *Install*. *Remove* asks first, then deletes the model's folder. | — |
| Embedded download mode | `parallel` fetches each file over 8 connections (about twice as fast); `single` uses one. | `parallel` |
| Embedded backend | The llama.cpp build: `auto` (CUDA with NVIDIA driver 580 or newer, else Vulkan, else CPU), `cuda`, `vulkan` or `cpu`. On a Mac, `auto` and `metal` both pick Metal. | `auto` |
| Embedded context length | The context window in tokens. 0 (**fit**) is the largest that fits on the GPU within *Embedded VRAM budget (%)*; otherwise 512–262,144. | 0 (fit) |
| Embedded GPU layers | Layers on the GPU: `auto` (as many as free VRAM holds), `all`, or a number (0 runs on the CPU). | `auto` |
| Embedded VRAM budget (%) | How much GPU memory the server may fill: `off` (llama.cpp leaves 1 GiB free) or 50–99 %. It shrinks the context first, then moves layers to the CPU, which slows replies sharply. A change restarts the server. | 91 % |
| Embedded VRAM only | Keeps the whole model on the GPU. If any of it lands in system RAM, the server stops and says what to lower. Refused on the CPU backend. | on |
| Embedded vision | Loads the vision projector so the model reads images (about 1 GB more for most models). Off, images sent to it are refused. | on |
| Embedded drafter | Speeds up replies by drafting tokens ahead and checking them; the text is the same. Turn it off if a model misbehaves with it. | on |

* **NVFP4 builds** need the CUDA backend on an NVIDIA Blackwell GPU (RTX 50 series or newer). A Mac's catalog leaves them out.
* **Downloads** are checked against Hugging Face's SHA-256. A stopped download resumes when you pick the model again.
* **llama.cpp** downloads on first start (577 MB for CUDA, 33 MB for Vulkan, 19 MB for CPU, 12 MB for Metal). If CUDA won't start under `auto`, the app tries Vulkan.
* **The server** listens on `127.0.0.1` only, with a fresh key, and stops when you pick another server or quit. One runs at a time, apart from a `multi-server` botchat's or advisor's extras.
* **Files:** the weights in `models\llm\<id>\`, shared projectors and drafters in `models\llm\_shared\`, llama.cpp in `llama\`, all under the home folder. `/about` shows them.
* **Sampling** follows each model card; `/sampling` overrides it.
* **Memory:** a model needs roughly its download size in VRAM, plus the context. The 26B A4B and Qwen3.6 35B A3B are mixture-of-experts, so they run faster than their size suggests. The small models (E2B, E4B) call tools less reliably.
* **Never in system RAM:** in NVIDIA Control Panel › *Manage 3D settings* › *Program Settings*, add `llama-server.exe` (under `llama\`) and set *CUDA - Sysmem Fallback Policy* to *Prefer No Sysmem Fallback*.

#### Docker

Your own LLM containers (vLLM, SGLang, anything serving `/v1/models`) as `/server` choices, one running at a time; see [Docker servers](TOOLS.md#docker-servers). Windows only: on a Mac, Docker Desktop passes no GPU to containers.

| Setting | What it does | Default |
|---|---|---|
| Docker servers enabled | Offers the chosen containers in `/server`. | off |
| Docker server containers | A checklist of the LLM servers the engine lists: containers matching *Docker server match words* or labeled `neonsidekick.llm=true`. | empty |
| Docker server match words | Words, matched in a container's image or command, that mark it as an LLM server. `r` restores the defaults; empty lists every container. | `aphrodite`, `koboldcpp`, `llama-server`, `llama.cpp`, `lmdeploy`, `localai`, `nvcr.io/nim`, `ollama`, `sglang`, `text-generation`, `trtllm`, `vllm` |
| Docker server stop timeout (s) | How long a stopping container gets before the engine kills it (0–120). | 30 |
| Docker server post-stop delay (s) | The wait between stopping the others and starting this one, so GPU memory frees up (0–60). | 2 |
| Docker server ready timeout (s) | How long a started container may take to answer on `/v1/models` (30–3600). | 900 |
| Docker server stop on exit | Stops the container in use when the app exits. | off |

#### Anthropic

Claude Code (for `/claude` and as a `/server` choice), then the Anthropic API.

| Setting | What it does | Default |
|---|---|---|
| Claude Code executable | The Claude Code CLI, for `/claude` and the Claude Code server. Blank looks on the PATH, then in `%USERPROFILE%\.local\bin`. | empty (looked up) |
| Claude Code server enabled | Offers Claude Code on `/server` while it is found. | off |
| Claude Code command permissions | What `/claude` may do: `read-only` (read, search, web) or `read-write` (file edits too, no commands). It works in the working directory, outside the app's sandbox and approvals. | `read-only` |
| Claude Code command model | `/claude`'s `--model`: Claude Code's default, `fable`, `opus`, `sonnet`, `haiku`, or *Other…*. | empty (Claude Code's default) |
| Claude Code command effort | `/claude`'s `--effort`: Claude Code's default, `low`, `medium`, `high`, `xhigh` or `max`. | empty (Claude Code's default) |
| Anthropic API enabled | Offers the Anthropic API on `/server` while a key is set. | off |
| Anthropic API key | Your Anthropic key (`sk-ant-…`), saved encrypted. An empty entry clears it. | empty |
| Anthropic API max tokens | The output cap per request, thinking included (1,024–128,000). | 32,000 |
| Anthropic API prompt caching | Uses Anthropic's prompt cache, so each request re-reads the last one's content cheaply. | on |

**The Anthropic API** is the **Anthropic API** row in `/server`, with the account's models.
* Every message is billed to the key's account. The key goes only to Anthropic.
* *LLM reasoning* `low` to `xhigh` turns on thinking at that effort; `none` turns it off where the model allows.
* `/usage` adds *Cache* and *Cost* rows (an estimate at list price).

**Claude Code** is last in `/server` (or `/server claude-code`), with `fable`, `opus`, `sonnet` and `haiku`.
* One `claude` process runs for the session; `/clear`, `/new` and a profile switch start a new one. Messages count against your Claude Code plan.
* It runs with its own tools off and gets the app's tools instead, so approvals and `ask_user` apply as for any model.
* ESC interrupts the reply. Claude Code compacts its own conversation (a 200,000-token window), so *LLM auto compact* does nothing.

#### OpenAI

The OpenAI API as a `/server` choice, over the Responses API (nothing is stored on OpenAI's side).

| Setting | What it does | Default |
|---|---|---|
| OpenAI API enabled | Offers the OpenAI API on `/server` while a key is set. | off |
| OpenAI API key | Your OpenAI key (`sk-…`), saved encrypted. An empty entry clears it. | empty |
| OpenAI API max tokens | The output cap per request, reasoning included: 0 for the model's own limit, else 1,024–128,000. | 0 (the model's own) |
| OpenAI API organization | The `OpenAI-Organization` header, for an account in several organizations. Empty sends none. | empty |
| OpenAI API project | The `OpenAI-Project` header. Empty sends none. | empty |

**The OpenAI API** is the **OpenAI API** row in `/server`, with the account's chat models, newest first.
* Every message is billed to the key's account. The key goes only to api.openai.com.
* *LLM reasoning* is sent as the model's reasoning effort; the thinking shows as OpenAI's summary.
* *LLM sampling* is not sent. *LLM context length* sets the window the app counts for the model.
* `/usage` adds *Cache* and *Cost* rows (an estimate at list price).

### Advisors

Other profiles this one consults, each in a role you give it: `neon_advisor` for the model, `/advisor` for you (see [Advisors](TOOLS.md#advisors) and [Advisor](COMMANDS.md#advisor)). An advisor answers on its own profile's server, model and persona, may use this chat's read-only tools, and remembers its earlier questions for the session.

| Setting | What it does | Default |
|---|---|---|
| Advisors enabled | Offers the model `neon_advisor` while the list holds an advisor. `/advisor` works either way. | off |
| Advisor list | The advisors, each a profile and its role. *+ Add an advisor…* picks a profile and asks its role; Enter on one changes its role or removes it. | empty |
| Advisor context | What the model's calls send. `brief`: the question and context. `recent`: the last 10 messages too. `/advisor` always sends the last 10. | `brief` |
| Advisor max calls per turn | The most advisor calls one reply may make (1–10). | 2 |
| Advisor max tool iterations | Tool round trips an advisor may make before answering (0–10; 0 is text only). | 4 |
| Advisor confirm | Each call waits for your yes. Headless runs refuse the calls. | off |
| Advisor thread length | Earlier questions and answers each advisor remembers (1–50). `/new` starts them over. | 10 |
| Advisor embedded mode | For an advisor on another embedded model than the one running. `parent-server`: it answers on the running model. `multi-server`: an extra `llama-server` for its model. | `parent-server` |

* **Servers:** a blank *LLM URL* in the advisor's profile borrows this chat's server. An embedded advisor needs *Embedded servers enabled*.
* **On the Claude Code server** each advisor runs in a `claude` process of its own, beside this chat's. It stays until the conversation ends or this chat's server or model changes; the next question resumes it.

### Voice

Speech out (TTS) and voice in (STT).

#### TTS

Speech output sets up in the background (🔈 on the status bar); replies are text only until it's ready.

| Setting | What it does | Default |
|---|---|---|
| TTS output | Reads replies aloud (`/tts`). Code blocks and tables are never read. | off |
| TTS source | `in-process` runs Kokoro inside the app (downloaded on first use); `http` uses a Kokoro-FastAPI server. | `in-process` |
| TTS HTTP URL | The Kokoro-FastAPI base URL (`http://localhost:8880/v1`), for `http`. | empty |
| TTS voice preview | The voice pickers speak the highlighted voice. | on |
| TTS voice preset | Sets the voice, second voice, mix and speed in one go. Built in: `amanda`, `neon`, `richard`, `hunter`, `larry`, `jack`, `willow`; see [Voice presets](COMMANDS.md#voice-presets). | `neon` |
| TTS voice | The Kokoro voice. | `af_heart` |
| TTS voice 2 | A second voice blended in; `(none)` for the first alone. | `am_eric` |
| TTS voice mix (%) | The first voice's share of the blend (0–100). | 80 |
| TTS speed | How fast the voice speaks (0.5–2.0). | 1.2 |

#### STT

Voice input sets up in the background (🎙️ on the status bar).

| Setting | What it does | Default |
|---|---|---|
| STT input | Turns the microphone on: the push-to-talk key records a message (`/stt`). On a Mac, macOS asks once whether your terminal may use the microphone. | off |
| STT destination | `chat` sends what you say straight to the model; `draft` puts it on the input line for you to edit. | `chat` |
| STT push-to-talk key | `F1`–`F8`, `Insert`, `Home`, `End`, `PageUp` or `PageDown`. | `F4` |
| STT Whisper model | `ggml-tiny.en.bin`, `ggml-base.en.bin` or `ggml-small.en.bin` (downloaded on first use). | `ggml-base.en.bin` |
| STT wake | Saying the wake phrase at the idle line starts listening (`/stt wake`). | off |
| STT wake phrase | One to three words; also the interrupt phrase. | `hey neon` |
| STT Vosk model | The wake-word model: `vosk-model-small-en-us-0.15`, `vosk-model-en-us-0.22-lgraph` or `vosk-model-small-en-in-0.4`. | `vosk-model-small-en-us-0.15` |
| STT interrupt | Saying the wake phrase while a reply is spoken cuts it short and listens (`/stt interrupt`). | off |
| STT interrupt echo guard (%) | Ignores the assistant's own voice saying something close to the wake phrase (50–100; 100 is the exact phrase only). | 100 |
| STT interrupt hold (ms) | How long the phrase must hold before it counts (0–2000). | 200 |

## Skills settings (`/skills`)

Skills are folders of instructions the model can load (see [Skills](TOOLS.md#skills)).

### Offered

The loaded skills with their scope (`profile`, `global` or `external`), version, last change and description, then any shadowed duplicates and skipped folders with the reason. The second strip narrows the list to one scope. Enter on a skill can:

* move it between the profile and global folders (while *Global skills* is on), move a profile skill to another profile, or rename it;
* edit its `SKILL.md` in your editor;
* revert it to a kept version (see [Skill history](#skill-history));
* lock or unlock it (shown as `🔒 v4`; see [Locking a skill](#locking-a-skill));
* disable or enable it (shown as `⛔ v4` on a dim row; see [Disabling a skill](#disabling-a-skill));
* delete it, after a yes.

### Reflection

| Setting | What it does | Default |
|---|---|---|
| Reflection (auto-learn) | After enough tool calls, or a tool error the model recovered from, a background reflection writes or improves a skill. | on |
| Reflection reasoning | The reflection's reasoning effort: `none`, `low`, `medium`, `high`, `xhigh`, or `profile` for the profile's level. | `none` |
| Reflection window | How many recent turns a reflection reads (1–5). | 3 |
| Reflection min tool calls | The model's own tool calls since the last reflection that make a task worth a skill (3–20). | 4 |
| Reflection max requests | Model requests one reflection may spend (1–20). | 4 |
| Reflection include sessions | The reflection starts from earlier sessions that match the turn, and can search them. | on |
| Reflection cooldown (min) | How long automatic reflections wait after one wrote a skill (0–1440; 0 is off). | 5 |
| Reflection cooldown mode | `last-written-skill`: only turns that used the skill just written wait. `all-skills`: every automatic reflection waits. | `last-written-skill` |
| Reflection yield to turns | A message sent during a reflection pauses it, so the reply gets the server. Turn it off if your server handles parallel requests. | on |
| Reflection confirm mode | Whether a reflection asks before it writes: `none` never, `all` always, `new` before a new skill, `edit` before an edit. It asks at the idle line: **Keep**, **View** or **Discard**. | `none` |
| Reflection edit supporting files | Lets a reflection change a skill's supporting files too, not just its `SKILL.md`. | off |
| Reflection edit downloaded skills | For skills from `/skills add`: `read-only` makes the reflection write a companion skill instead; `allow-and-mark` lets it change the skill, and a later update warns first. | `allow-and-mark` |

### Options

| Setting | What it does | Default |
|---|---|---|
| Agent skills | Lists the skills in the prompt and offers `load_skill` and `skill_editor`. Off also turns off *Project file*. | on |
| Global skills | Reads the global skills, the home folder's `skills` folder every profile shares. Off, this profile sees and writes only its own, and `/skills add --global` is refused. | on |
| External skills (.agents\skills) | Also reads `%USERPROFILE%\.agents\skills` (`~/.agents/skills` on a Mac), read-only. | off |
| Project file | Reads `NEON.md` (or `AGENTS.md`) in the working directory into the prompt as project notes. Needs *Agent skills*. | on |
| Skill compact mode | `protected` keeps a loaded skill's instructions through a prune; `unprotected` prunes them like any tool result. | `protected` |
| #-mention enabled | `#` and part of a name on the input line lists the skills; a pick writes `#name`. | on |

### Installing skills

`/skills add` installs skills in the [Agent Skills](https://agentskills.io) format, the same folders other agents use.

* **Sources:** search words (`/skills add pdf`) look on [skills.sh](https://skills.sh); `owner/repo` offers every skill in a GitHub repository and `owner/repo/skill` names one; a GitHub `tree/…` or `blob/…/SKILL.md` link narrows to a folder; any `https://…/*.zip` link is downloaded as is.
* **Network:** *Web browser network mode* applies (nothing is fetched under `lan`); the *Web tools* switch doesn't.
* **Preview first:** you see the description, source, file list and any scripts before installing. Unsafe paths and very large skills (over 200 files or 20 MB) are refused.
* **Where it goes:** the profile's or the global `skills` folder (global only while *Global skills* is on), with a `.neon-source.json` that records where it came from. Bundled scripts run only through `run_command` and its approval.
* **Name clashes** are refused, except the same source installed again, which updates it in place.

### Skill records and purging unused skills

* `skills.db`, in the home folder's `db\skills`, records every global and profile skill: its scope and when it was created, last changed and last used. External skills are not recorded and never purged.
* `/skills purge list <age>` lists skills unused for that long; `/skills purge commit <age>` deletes them after a yes. The age is days (`30`) or a duration (`12h`, `1d 6h`).
* Only the global skills (while *Global skills* is on) and the loaded profile's count. A global skill used in any profile counts as used.

### Skill history

`skills.db` also keeps each skill's history, which reflection reads: who changed it (the model, a reflection, an install, a revert or you), and each turn that loaded it, with any errors after.

* **Earlier versions:** before a skill file is overwritten, the old text is kept (the last 10 per skill).
* **Reverting:** Enter on a skill, then `revert`, lists the kept versions, newest first. Enter puts one back after a yes; the current text is kept first, so you can go back and forth.
* **Removing versions:** on the version list, `d` removes one and `c` clears them all, each after a yes.

### Locking a skill

Enter on a skill, then `lock`, keeps it as it is until you choose `unlock`. While it is locked:

* The model and reflection can load it but not change it; a reflection writes a companion skill instead.
* Rename, move, delete and revert refuse with "unlock it first"; `edit` still opens it. `/skills add` won't update it, and `/skills purge` leaves it out.
* The lock is a `.neon-lock` file in the skill's folder: the app's refusal, not a file-system guard, so a shell command can still reach the folder.

### Disabling a skill

Enter on a skill, then `disable`, switches it off without removing it; `enable` switches it back on. While it is disabled:

* The model is never offered it: not in the prompt's list, not loadable, not in `#` mentions or to `/botchat` bots. Reflection sees it as off, so it writes no second skill for the topic.
* Rename, move, delete and `edit` still work; revert refuses with "enable it first". `/skills purge` leaves it out.
* It keeps its name, so a global skill of the same name stays shadowed.
* The switch is a `.neon-disabled` file in the skill's folder; a disabled global skill is off in every profile.

## Tools settings (`/tools`)

Each group's tools and what they do are in [Tools](TOOLS.md); this section covers their settings.

### Offered

Every tool, grouped, with the description the model reads. Enter or Space switches one; a group whose switch is off is dim. The Help group (`neon_help`) has no switch of its own, so turn it off here. In a new profile, `gitlib_delete`, `zip`, `unzip`, `unc_delete`, `docker_remove`, `docker_prune` and `ha_todo` start off.

### Core

The tools most profiles use, and the pane's own options.

#### Ask

| Setting | What it does | Default |
|---|---|---|
| Ask user | Offers `ask_user`: multiple-choice questions on the pane. `/tools ask` and the toolbar's 🙋 open its on/off page. | on |
| Ask max questions | Questions per call (1–10). | 10 |
| Ask max choices per question | Options per question (2–15). | 10 |

#### Web

| Setting | What it does | Default |
|---|---|---|
| Web tools | Offers `web_search`, `web_fetch`, `open_url`, `web_links`, `web_save_link` and `download_file`. The link tools read and change the profile's saved links (see [Web](TOOLS.md#web)). | off |
| Web browser mode | `auto` uses HTTP and falls back to a headless browser for a blocked or empty page; `http` never falls back; `browser` uses the browser for every page. | `auto` |
| Web browser path | The Chromium-based browser for headless fetches and for making and reading PDFs. Empty finds Edge, Chrome or Brave. | empty (found) |
| Web browser botcheck window | A bot check the web tools can't pass: `ask` asks, then opens it in the Browser window for you to solve; `automatic` opens it unasked; `off` never does. Windows only. | `ask` |
| Web browser home page | Where the Browser window's home button and a bare `/browser` go: a web address (`example.com`, `localhost:3000`), or empty for a blank page. Windows only. | empty |
| Web browser privacy | `keep-history` keeps the pages the Browser window visited, for its history menu; `clear-history` clears them when it closes. Cookies and sign-ins stay either way (`/browser clear all` removes them). Windows only. | `clear-history` |
| Web open links in | Where `open_url` links go: `default-browser`, or `browser-window` (the app's Browser window, one page at a time). With no window to open, as headless or on a Mac, they go to your default browser. | `default-browser` |
| Web browser network mode | Where a fetch may reach: `internet` (public addresses), `lan` (this machine and the LAN) or `both`. | `internet` |
| Web search method | `duckduckgo` (built in) or `searxng`. | `duckduckgo` |
| Web SearXNG URL | The SearXNG instance, for `searxng`. | empty |
| Web search max results | Hits per search (1–20). | 20 |
| Web download max (MB) | The largest file `download_file` saves (1–102,400). | 50 |

#### Shell

| Setting | What it does | Default |
|---|---|---|
| Shell command policy | How the model may run shell commands: `off` (no shell tools), `ask` (anything not on the allowed list asks first) or `yolo` (everything runs). See [Shell guards](#shell-guards). | `off` |
| Shell allowed commands | Command prefixes allowed for good (`git status`, `dotnet build`). The approval pane's *Allow … always* adds one; Enter removes one; `p` steps *Shell command policy* (`yolo` after a yes). | empty |
| Shell police | Refuses a command, script or process input that names a path outside the working directory. Turning it off asks first, and also stops the forbidden strings and the database rules. | on |
| Shell police forbidden strings | Strings the police refuses outright, ignoring case and spacing. A new profile starts with a list of harmful actions; `r` restores it. `/profile push policestrings` copies the list. | the starter list |
| Shell prefer native tools | Sends back a lone shell command one of the app's own tools covers, once a turn. | on |
| Shell default shell | The shell when a call names none: `powershell` (pwsh if installed, else 5.1), `cmd` or `bash` (Git Bash). On a Mac: `zsh`, `bash` or `powershell`. | `powershell` (`zsh` on a Mac) |
| Shell default timeout (s) | How long a foreground command without its own `timeout` may run (1–3600). | 180 |
| Shell max timeout (s) | The longest any foreground command may run (10–3600). | 600 |
| Shell output max chars | The output one result carries (2000–500000). Past that, the start and end are kept and the whole text goes to `.shell\<id>.log` in the working directory. | 30000 |
| Shell code languages | What `execute_code` may run: `powershell`, `python` and `node`, each when its interpreter is found. | all three |
| Shell code default timeout (s) | How long a script without its own `timeout` may run (1–3600). | 300 |
| Shell tool bridge | Lets an `execute_code` script call the app's other tools through its `neon_tools` module. | off |
| Shell tool bridge max calls | Tool calls one script may make through the bridge (1–500). | 50 |

##### Shell guards

* **Approval (`ask`):** the pane offers Deny, Allow once, Allow for this session, or Allow always; **≡ view** (`v`) shows the whole command. `--yolo` and `NEONSIDEKICK_COMMAND_POLICY` set the policy for one launch.
* **Prefixes:** a command is allowed by its program, plus the verb for programs that take one (`git status`, `npm run`). Every command in the line must be allowed: each part of a pipe or `&&` chain, and each command a substitution runs. A substitution the app can't read always asks.
* **Path police:** refuses an absolute path (`C:\…`, a UNC share, `/etc/hosts`), a `..` that climbs out, `~` or a folder variable (`%USERPROFILE%`, `$HOME`), in switches and URLs too. The transcript shows 👮.
* **What the police sees:** it reads text, not what runs, so a computed path gets past it. `--no-police` and `NEONSIDEKICK_SHELL_POLICE` override it; `--yolo` doesn't.
* **Forbidden strings:** while the police is on, anything containing a listed string is refused, even under `yolo`, searching for one included. Case is ignored and runs of spaces count as one (`rm -rf` catches `RM   -RF`). It is a tripwire, not a sandbox.
  * **The starter list** holds actions that do harm anywhere, such as `reset --hard`, `push --force`, `| iex` and `.ssh/id_`; on Windows also `Format-Volume` and `diskpart`, and on a Mac `diskutil erase` and `dump-keychain`.
  * **Stricter entries you can add:** `rm -rf`, `Remove-Item -Recurse -Force`, `git clean`, `taskkill /f`, upload sites (`pastebin.com`, `ngrok`), and on a Mac `osascript` or `.zshrc`.
* **Databases:** while the police and a database family's tools are both on, a command or script that reaches that family is refused, even under `yolo`. For SQLite that is the word `sqlite`, a `.db` or `.sqlite` file, or a file `sqlite.json` names; for the server families, their clients and drivers (`sqlcmd`, `sqlplus`, `mysql`, `psql`…) and their connections' hosts.
* **Package commands:** a command that only installs, removes or lists packages (`pip install …`, `npm install …`) isn't judged by the database rules.
* **Prefer native tools:** a lone command an offered tool replaces comes back once a turn naming the tool (`cat` → `read_file`, `curl` → `web_fetch`, `sqlcmd` → `sql_query`…). Sent again, it goes to the approval pane.

#### Options

| Setting | What it does | Default |
|---|---|---|
| $-mention enabled | `$` and part of a name lists the tools the next turn offers; a pick writes `$name`. | on |
| Tool collapse count | A run of more tool calls than this folds to one summary line (0–100; 0 never folds). | 2 |
| Code collapse count | A finished code block longer than this folds to its label (0–100; 0 never folds). Needs *Transcript markdown*. | 20 |
| Show file diffs | A file edit shows its diff under its line, added lines on green and removed on red. The model's result is the same either way. | on |
| Diff max lines | The most rows of an edit's diff shown (0–500; 0 shows the header line alone). | 10 |
| Diff collapse count | A diff longer than this folds once its tool run is over (0–500; 0 never folds). | 10 |

To open a folded block or diff, click it, press Ctrl+O, click **⤡** or use `/expand`.

### Files

The file tools and the places beyond the working directory they reach.

#### File

| Setting | What it does | Default |
|---|---|---|
| File tools | Offers the sandboxed file tools (read, write, patch, search, move, copy, zip, read_pdf, convert_to_pdf…), which reach only the working directory. | off |
| File /tree max entries | Entries `/tree` prints before it stops (1–10000). | 500 |
| File /tree show sizes | `/tree` shows file sizes. | on |
| File @-mention folder mode | Picking a folder from the `@` list: `remain` opens it in the list; `apply` writes `@folder/` and closes the list. | `remain` |
| File browser/tree mode | `hide-hidden` leaves hidden, system and dot entries out of the folder browsers, `/tree` and `/browse`; `show-hidden` lists them. | `hide-hidden` |
| File view image max images | Pictures one `view_image` call may load, and pages one `read_pdf` call with pictures draws (1–100). | 10 |
| File search max results | The most rows one `search_files` or `unc_search` call returns (1–5000). | 200 |

#### Image

| Setting | What it does | Default |
|---|---|---|
| Image tools | Offers `view_image`, `image_info`, `image_edit` and `set_splash_image`, whether *File tools* is on or not. They reach pictures in the working directory and the profile's `images` folder. | off |
| Image edit quality | The JPEG (or JPEG XL, HEIF) quality `image_edit` uses when the model gives none (1–100). On a Mac it applies to JPEG and HEIF (a Mac writes no JPEG XL). | 90 |
| Image edit metadata | What metadata `image_edit` keeps: `none`, `basic` (author, date taken, camera…) or `all` (basic plus the GPS position). | `none` |
| Image edit mode | Where an edited picture goes: `beside-original` writes a new file beside the source; `overwrite-original` replaces the source, without asking. It rules `image_edit` and the picture windows' right-click menu. | `beside-original` |
| Image edit output folder | Where `image_edit` writes when the model names no `to`: a folder under the profile's `images` folder. Empty writes beside the source. | empty (beside the source) |
| Pasted images folder | Where a picture pasted from the clipboard is saved, under the profile's `images` folder, so the model's picture tools can use it. | `pasted_images` |

#### UNC

Network shares and folders outside the working directory; see [UNC shares](TOOLS.md#unc-shares-and-outside-folders) (on a Mac, [UNC shares on a Mac](TOOLS.md#unc-shares-on-a-mac)). The rows work like the [SQL](#sql) tab's, over `unc.json`.

| Setting | What it does | Default |
|---|---|---|
| UNC tools | Offers `unc_shares`, `unc_search`, `unc_info`, `unc_read`, and `unc_fetch` while *File tools* is on. | off |
| UNC writes | The master key for changes: on, a share with `access: readwrite` also gets the write, move, copy and delete tools. Changes are permanent. | off |
| UNC shares offered | As *SQL connections offered*. | empty |
| UNC set password | As *SQL set password*, for runas shares. | — |
| UNC add/edit share | The wizard for a new or saved share; its test lists the share's root under its account. | — |
| UNC *-mention enabled | `*` and part of a name lists the offered shares; a pick writes `*name`. | on |
| UNC shares (profile) | As *SQL connections (profile)*. | — |
| UNC shares (global) | As *SQL connections (global)*. | — |

#### GitLib

| Setting | What it does | Default |
|---|---|---|
| GitLib tools | Offers the built-in git tools (status, log, diff, blame, branch, stage, commit, stash…) over the working directory's repository. Off, git goes through the shell. | off |
| GitLib diff max lines | Where a `gitlib_diff` patch is cut (20–5000). | 500 |
| GitLib log max commits | Commits `gitlib_log` returns by default (1–200). | 20 |
| GitLib user email | The `user.email` `/gituser` writes into the repository's config. | empty |
| GitLib user name | The `user.name` `/gituser` writes beside it. | empty |

#### Obsidian

| Setting | What it does | Default |
|---|---|---|
| Obsidian tools | Offers the vault tools (search, list, read, links, daily, write, properties, move) once a vault is set. | off |
| Obsidian vault | The vault's folder (the one holding `.obsidian`), separate from the working directory. | empty |
| Obsidian allow delete (.trash) | Offers `vault_delete`, which moves a note or attachment into the vault's `.trash`. | on |

### Apps

The apps and services the tools reach. `/claude`'s settings are on `/settings` › Servers › [Anthropic](#anthropic).

#### Docker

| Setting | What it does | Default |
|---|---|---|
| Docker tools | Offers the read-only Docker tools (containers, logs, inspect, stats, resources, compose). `/docker` works either way. See [Docker](TOOLS.md#docker) and [Docker on a Mac](TOOLS.md#docker-on-a-mac). | off |
| Docker writes | The master key for the model's changes: `docker_lifecycle`, `docker_pull`, `docker_remove` and `docker_prune`. Every call asks first; headless refuses them. | off |
| Docker engine pipe | The engine's named pipe (`docker_engine` is Docker Desktop's). On a Mac the row is *Docker engine socket*, the engine's Unix socket; empty finds it as the `docker` command does. | `\\.\pipe\docker_engine` (on a Mac, found) |

#### ComfyUI

| Setting | What it does | Default |
|---|---|---|
| ComfyUI tools | Offers `generate_image` once *ComfyUI URL* is set and a workflow is installed. | off |
| ComfyUI URL | The ComfyUI server, often on your LAN (`http://gpu-box:8188`). | empty |
| ComfyUI workflows offered | A checklist of the workflows the model is offered; none until ticked. `/imagine <name>` can still use a hidden one. | none |
| ComfyUI add workflow | A wizard that **builds** a workflow from your server's checkpoints or **imports** a ComfyUI export. See [Adding a workflow](TOOLS.md#adding-a-workflow). | — |
| ComfyUI ^-mention enabled | `^` and part of a name lists the offered workflows; a pick writes `^name`. | on |
| ComfyUI timeout (s) | How long to wait for one generation, queue included (10–3600). | 300 |
| ComfyUI max images per call | The most pictures one `generate_image` call or `/imagine --count` makes (1–16). | 5 |
| ComfyUI reinforce negatives | When the model writes the prompt, adds a few tags to the negative where the image model tends to drift. | on |
| ComfyUI show prompts | Shows the `prompt:`, `negative:` and `params:` lines under each picture. | on |
| ComfyUI picture strip | Keeps the session's pictures as thumbnails above the input line. With the line empty, ←/→ highlight one and Enter opens it; **×** hides the strip until **🎞️** on the rule brings it back. | on |
| ComfyUI output folder | Where pictures are saved, under the profile's `images` folder. | `comfy_images` |

#### YouTube

| Setting | What it does | Default |
|---|---|---|
| YouTube tools | Offers the [YouTube tools](TOOLS.md#youtube): search (with an API key), and playback in the app's [video window](INTERFACE.md#the-video-window) on Windows with WebView2 or macOS 14 or later. Playing by id or link needs no key. | off |
| YouTube API key | A YouTube Data API v3 key, for searching; create one in the [Google Cloud Console](https://console.cloud.google.com/) with *YouTube Data API v3* enabled. About 100 free searches a day. Saved encrypted. | empty |
| YouTube search max results | How many results a search lists (1–20). | 8 |
| YouTube search order | `relevance`, `date` (newest first), `views`, `rating` or `title`, unless a search names one. | `relevance` |
| YouTube safe search | How much restricted content a search leaves out: `none`, `moderate` or `strict`. | `moderate` |
| YouTube region | A two-letter country code (`US`, `GB`) a search asks for. Empty for any region. | empty (any region) |
| YouTube language | A language code (`en`, `es`) whose results rank higher. Empty for any language. | empty (any language) |
| YouTube autoplay | Whether a played video starts at once or waits for play. | on |
| YouTube while speaking | What a playing video does while the app speaks or listens: `pause`, `duck` (to 15 %) or `none`. | `pause` |

### Databases

The five database families. Each is read-only unless you choose `read-write` and the connection's entry allows it; see [How the database tools work](TOOLS.md#how-the-database-tools-work).

**Statement kinds** (the *… statements allowed* checklists): changing data, deleting, creating, changing structure, dropping, upkeep, procedures and triggers (settings, for SQLite), and reading. A statement needs the kind of every change in it, and with none ticked the `_execute` tool isn't offered.

#### SQL

| Setting | What it does | Default |
|---|---|---|
| SQL tools | Offers the SQL Server tools (connections, tables, columns, describe, relationships, indexes, query) over `sql.json`'s connections, each held to its own database. | off |
| SQL mode | `read-only`: the tools only read. `read-write`: `sql_execute` is offered too, on connections marked `"access": "readwrite"`, and each change asks you. See [Read-write mode](TOOLS.md#read-write-mode). | `read-only` |
| SQL statements allowed | Under `read-write`, the kinds of statement `sql_execute` may run (a checklist). | changing data, creating, reading |
| SQL connections offered | A checklist of the connections in both `sql.json` files; none is offered until ticked. A hidden connection is invisible to every tool. | empty |
| SQL set password | Pick a connection and type its password, masked; it is stored encrypted. | — |
| SQL add/edit connection | A wizard for a new or saved connection, which can **test** it before saving. The database is required. See [Managing connections](TOOLS.md#managing-connections). | — |
| SQL %-mention enabled | `%` and part of a name lists the connections; a pick writes `%name`. | on |
| SQL max rows | Rows `sql_query` returns unless the call says otherwise (1–100000). | 100 |
| SQL query timeout (s) | How long one batch may run on the server (1–600). | 30 |
| SQL query result max chars | The most characters of table one `sql_query`, `oracle_query` or `mysql_query` returns (1000–1000000). | 32,000 |
| SQL connections (profile) | Enter opens the profile's `sql.json` in your editor (created with commented examples). | — |
| SQL connections (global) | The same for the home folder's `sql.json`, which every profile reads. The profile's wins a name clash. | — |

The other database tabs work like this one, over `mysql.json`, `sqlite.json`, `postgres.json` and `oracle.json`.

#### MySQL

| Setting | What it does | Default |
|---|---|---|
| MySQL tools | Offers the MySQL tools, for MySQL 8.0.16+ and MariaDB 10.2+, each connection held to its own database. | off |
| MySQL mode | As *SQL mode*, with `mysql_execute`. | `read-only` |
| MySQL statements allowed | As *SQL statements allowed*, for `mysql_execute`. | changing data, creating, reading |
| MySQL connections offered | As *SQL connections offered*. | empty |
| MySQL set password | As *SQL set password*. | — |
| MySQL add/edit connection | The wizard; its test warns when the account could change data. See [MySQL and MariaDB](TOOLS.md#mysql-and-mariadb). | — |
| MySQL %-mention enabled | Lists the MySQL connections in the `%` list too. | on |
| MySQL max rows | As *SQL max rows*, for `mysql_query`. | 100 |
| MySQL query timeout (s) | How long one statement may run (1–600). | 30 |
| MySQL connections (profile) | As *SQL connections (profile)*. | — |
| MySQL connections (global) | As *SQL connections (global)*. | — |

#### SQLite

| Setting | What it does | Default |
|---|---|---|
| SQLite tools | Offers the SQLite tools (databases, tables, describe, query) over the databases `sqlite.json` names. While on, the shell and file tools keep out of SQLite files (see [SQLite](TOOLS.md#sqlite)). | off |
| SQLite mode | `read-only`: the tools only read. `read-write`: `sqlite_execute` is offered too, one change per call, each allowed on a pane. | `read-only` |
| SQLite statements allowed | Under `read-write`, the kinds of statement `sqlite_execute` may run (a checklist). | changing data, creating, reading |
| SQLite databases offered | Which databases of `sqlite.json` the model sees; none until ticked. | empty |
| SQLite sandbox files | The model may also open any SQLite file in the working directory by its path, and create one there. | on |
| SQLite add/edit database | The wizard for a new or saved database; its test opens the file read-only and counts the tables. | — |
| SQLite %-mention enabled | Lists the SQLite databases in the `%` list too. | on |
| SQLite max rows | As *SQL max rows*, for `sqlite_query`. | 100 |
| SQLite query timeout (s) | How long one statement may run before it is stopped (1–600). | 30 |
| SQLite databases (profile) | Opens this profile's `sqlite.json` in your editor. | — |
| SQLite databases (global) | Opens the global `sqlite.json` in your editor. | — |

#### PostgreSQL

| Setting | What it does | Default |
|---|---|---|
| PostgreSQL tools | Offers the PostgreSQL tools, each connection held to its own database (and to one schema when its entry names one). | off |
| PostgreSQL mode | As *SQL mode*, with `postgres_execute`. | `read-only` |
| PostgreSQL statements allowed | As *SQL statements allowed*, for `postgres_execute`. | changing data, creating, reading |
| PostgreSQL connections offered | As *SQL connections offered*. | empty |
| PostgreSQL set password | As *SQL set password*. | — |
| PostgreSQL add/edit connection | The wizard; the database is required, a schema optional. Its test warns when the role could change data. See [PostgreSQL](TOOLS.md#postgresql). | — |
| PostgreSQL %-mention enabled | Lists the PostgreSQL connections in the `%` list too. | on |
| PostgreSQL max rows | As *SQL max rows*, for `postgres_query`. | 100 |
| PostgreSQL query timeout (s) | How long one statement may run (1–600). | 30 |
| PostgreSQL connections (profile) | As *SQL connections (profile)*. | — |
| PostgreSQL connections (global) | As *SQL connections (global)*. | — |

#### Oracle

| Setting | What it does | Default |
|---|---|---|
| Oracle tools | Offers the Oracle tools, each connection held to its one schema. | off |
| Oracle mode | As *SQL mode*, with `oracle_execute`. | `read-only` |
| Oracle statements allowed | As *SQL statements allowed*, for `oracle_execute`. | changing data, creating, reading |
| Oracle connections offered | As *SQL connections offered*. | empty |
| Oracle set password | As *SQL set password*. | — |
| Oracle add/edit connection | The wizard; its *Schema* is the one the connection is held to (the user's own when empty). Its test warns when the account could change data. See [Oracle](TOOLS.md#oracle). | — |
| Oracle %-mention enabled | Lists the Oracle connections in the `%` list too. | on |
| Oracle max rows | As *SQL max rows*, for `oracle_query`. | 100 |
| Oracle query timeout (s) | How long one statement may run on the server (1–600). | 30 |
| Oracle connections (profile) | As *SQL connections (profile)*. | — |
| Oracle connections (global) | As *SQL connections (global)*. | — |

### Devices

The printer, camera and screen tools.

#### Print

| Setting | What it does | Default |
|---|---|---|
| Print tools | Offers `list_printers` and `print_file`. `/print` works either way. On a Mac they print through CUPS, to the printers set up in System Settings. | off |
| Print action policy | `off`: list printers only. `ask`: each print waits for your yes (refused headless). `allow`: prints without asking. `/print` never asks. | `ask` |
| Print default printer | Where a print goes when none is named. | empty (the system default) |
| Print font size (pt) | The body text size for printed listings and Markdown (6–24). | 10 |
| PDF engine | What makes PDFs for `convert_to_pdf` and `/pdf`: `auto` (a Chromium browser, else Microsoft Print to PDF), `browser` or `printer`. On a Mac only the browser makes PDFs. See [Making PDFs](TOOLS.md#making-pdfs). | `auto` |

#### Camera

| Setting | What it does | Default |
|---|---|---|
| Camera tool | Offers `camera_capture`, so the model can ask you for a photo; never headless or to an embedded model without vision. `/camera` works either way. On a Mac: macOS 14 or later. | off |
| Camera shutter | `user`: the camera pane shows the request; Space takes the photo, `r` retakes, Enter sends, ESC declines. `model`: you allow or deny, and the app takes it. | `user` |
| Camera preview | `live`: a [camera window](INTERFACE.md#the-camera-window) shows the picture while you frame it. `post`: the picture viewer opens on the photo. `off`: no window. | `live` |
| Camera device | The camera, by its system name. `(first camera)`, or one that isn't connected, uses the first. | empty (the first camera) |
| Camera resolution | The size asked for (`640x480`, `1280x720` or `1920x1080`); the camera uses its nearest. | `1280x720` |
| Camera output folder | Where photos are saved, under the profile's `images` folder. | `camera_images` |
| Camera keep in sessions | Keeps photos in stored sessions. Off, a stored session keeps a line naming the photo instead. | off |
| Camera watch interval (s) | How often `/camera watch` looks (2–3600). | 10 |
| Camera watch change (%) | How much of the picture must change to count (1–100). `--camera-check` prints your camera's noise. | 8% |
| Camera watch unprompted | On, the model is shown a changed picture unasked when nothing else is going on. Off, it rides your next message. | off |
| Camera watch min gap (s) | The least time between unprompted watch turns (30–3600). | 120 |

#### Screen

| Setting | What it does | Default |
|---|---|---|
| Screen tools | Offers `screen_capture` and `screen_list`, so the model can see a monitor or a window; never headless or to an embedded model without vision. On a Mac: macOS 14 or later, with the [Screen Recording permission](COMMANDS.md#screen-capture). | off |
| Screen policy | `ask`: a pane names what would be captured and why, and waits for your answer. `allow`: taken without asking. | `ask` |
| Screen preview | The picture viewer opens on each screenshot, so you see what was sent. | on |
| Screen output folder | Where screenshots are saved, under the profile's `images` folder. | `screen_images` |
| Screen keep in sessions | Keeps screenshots in stored sessions. Off, a stored session keeps a line naming the screenshot instead. | off |

### Home

The household's tools.

#### HomeAssistant

| Setting | What it does | Default |
|---|---|---|
| Home Assistant tools | Offers the Home Assistant tools (`ha_overview`, `ha_states`, `ha_history`, `ha_lights`, `ha_scene`, `ha_media`, `ha_todo`, `ha_call_service`, `ha_assist`) once a URL and token are set. | off |
| Home Assistant URL | Your server (`http://localhost:8123`, or one on your LAN). | empty |
| Home Assistant API key | A long-lived access token (your Home Assistant profile › Security), saved encrypted. Empty clears it. | empty |
| Home Assistant test connection | Asks the server for its version with the saved URL and token. | — |
| Home Assistant action policy | `off`: read only. `ask`: lights, scenes, media and to-do lists run, and anything else (a switch, a script, an automation, a sentence for Assist) asks first, refused headless. `allow`: everything runs. `/ha` never asks. | `ask` |
| Home Assistant Assist agent | The conversation agent `ha_assist` and `/ha say` use. | empty (Home Assistant's default) |
| Home Assistant timeout (s) | How long one request may take (2–60). | 10 |

The services that run unasked under `ask` can be changed in `profile.json` (`HomeAssistantSafeServices`); `conversation.process` there lets `ha_assist` run unasked.

#### Shopping

| Setting | What it does | Default |
|---|---|---|
| Shopping tools | Offers the shopping list's tools (see [Shopping list](TOOLS.md#shopping-list)). The list and its stores live in the profile's `db\shopping\shopping.db`. | off |
| Shopping list | Enter opens the shopping list, the same pane as `/shopping` and the toolbar's 🛒. | — |
| Shopping stores | Enter opens the stores' manager: add, rename or remove a store, choose the one the list is walked through, and edit its layout (`l`). | — |

## MCP servers (`/mcp`)

MCP servers are outside programs that give the model more tools.

### Servers

One row per server in `mcp.json` (the profile's, then the home folder's; the profile's wins a name clash), with its state: `connected · N tools`, `connecting`, `failed: …` or `off`. Enter or Space turns one on or off, and Enter on a failed one retries. Below them: `edit profile mcp.json`, `edit global mcp.json`, `reload`, and any skipped entries.

### Tools

Every connected server's tools, as `<server>__<tool>`, with their descriptions. Enter or Space switches one. With two or more servers connected, the second strip shows one server's tools alone.

### Options

| Setting | What it does | Default |
|---|---|---|
| MCP servers enabled | The master switch: on, every enabled server starts at launch and its tools are offered. | off |
| MCP connect timeout (s) | How long a server gets to connect and list its tools (5–300). | 30 |

## System prompt (`/sys`)

A read-only view of exactly what the next reply sends. `/sys persona`, `/sys operata` and `/sys vocalia` open its three files in your editor (`reset` puts one back to the default); `/profile push|pull persona|operata|vocalia <name> [overwrite]` copies one to or from another profile.

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
