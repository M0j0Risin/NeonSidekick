# <img src="assets/icon.svg" alt="" height="32"> Neon Sidekick
[![Release](https://img.shields.io/github/v/release/M0j0Risin/NeonSidekick)](https://github.com/M0j0Risin/NeonSidekick/releases/latest)
![License](https://img.shields.io/github/license/M0j0Risin/NeonSidekick)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512bd4?logo=dotnet)
![Windows](https://img.shields.io/badge/Windows-0078D6?style=flat&logo=windows&logoColor=white)
![macOS](https://img.shields.io/badge/macOS-Apple%20Silicon-000000?logo=apple&logoColor=white)

Neon Sidekick is an agentic terminal client built first and foremost for local LLMs. Point it at LM Studio, Ollama, vLLM, SGLang or llama.cpp, or let it download a model and run it itself. When you want a frontier model too, it connects to the OpenAI and Anthropic APIs with your own key, or to your Claude Code install.

It's inspired by tools like Claude Code, Hermes Agent and Cline, bringing together the features I liked most in each, plus many they don't have. It runs on Windows and Apple Silicon Macs and is built on .NET 10. Use it as a Swiss Army knife for IT and creative work, or as a stable base for building and testing new agentic tools.

<p align="center">
  <img src="assets/screenshots/screen_splash.png" alt="The model writing a Rust Hello World and then editing it: folded thinking and tool calls, the edit's highlighted diff, then its reply" width="50%">
</p>

<p align="center"><b><a href="https://github.com/M0j0Risin/NeonSidekick/releases/latest">Download the latest release</a></b> · Windows x64 · macOS on Apple Silicon · unpack and run</p>

## Contents

- [Features](#features)
- [Getting started](#getting-started)
- [Settings & menus](#settings--menus)
- [About](#about)
- More documentation:
  - [Using the app](docs/INTERFACE.md): the screen, keys, bars and windows
  - [Slash commands](docs/COMMANDS.md)
  - [Tools](docs/TOOLS.md)
  - [Settings reference](docs/SETTINGS.md)
  - [On a Mac](docs/MAC.md)
  - [Headless and scripted runs](docs/HEADLESS.md)
  - [Command-line options](docs/COMMANDLINE.md)
  - [Environment variables](docs/ENVIRONMENT.md)
  - [Components & Libraries](docs/COMPONENTS.md)
  - [Building from source](docs/BUILD.md)

## Features

### The app
* **No installer:** unzip and run. It is compiled to native code (NativeAOT), so it starts fast and needs no .NET runtime.
* **A rich terminal UI:** Markdown, code highlighting, pictures in the transcript, mouse support, and sixty color themes (or your own).
* **Pictures in:** paste or drop one on the input line, or take one with the webcam (`/camera`) or of the screen (`/screen`).
* **Windows of its own:** a picture viewer and thumbnail browser (`/view`), a live camera view, the log (`/log`), a background process's output (`/process`) and a YouTube player.
* **A browser window** (Windows only): `/browser` lets you sign in to a site or pass its bot check, then lends its cookies to the web tools.
* **Auto-complete** for commands, files, folders, skills and tools, and **find** in the transcript (`/find`).
* **Message queue:** keep typing while the model works; messages go out in order. `/prompts` keeps the ones you send often.
* **Headless mode:** `--headless` runs over stdin/stdout for scripts and scheduled jobs ([HEADLESS.md](docs/HEADLESS.md)).
* **Web UI:** the chat, its questions and the shopping list in a browser, from a phone on your home network or your tailnet (`/webui`, [the Web UI](docs/INTERFACE.md#web-ui)).

### Models
* **Local servers:** finds OpenAI-compatible servers on this machine or your network, or takes a URL.
* **Embedded model:** a model the app downloads and runs itself, with no server to set up. Gemma, Qwen and Muse models, all reading pictures and calling tools, run on a built-in llama.cpp (CUDA, Vulkan or CPU; Metal on a Mac).
* **Model sizes:** Q4 and larger builds for 8–32 GB of VRAM, plus NVFP4 builds for NVIDIA's newest (Blackwell) cards, not offered on a Mac.
* **Docker servers (Windows only):** your vLLM or SGLang containers as `/server` choices, one running at a time ([vLLM](docs/DOCKER_WINDOWS_VLLM.md) and [SGLang](docs/DOCKER_WINDOWS_SGLANG.md) examples).
* **Cloud models:** the Anthropic and OpenAI APIs with your own key, or your Claude Code install; all off until you turn them on.
* **Advisors:** other profiles (named setups, each with its own model and persona) in a role you give them. The model or `/advisor` asks one, which answers with the chat's read-only tools.
* **Context control:** automatic compaction keeps the conversation inside the model's window; `/compact` and `/rewind` do it by hand. `/sys` shows exactly what the model is sent.

### Profiles, memory & skills
* **Profiles:** each has its own settings, persona, memory, sessions and working directory (the folder the model works in).
* **Sessions:** resume, search and learn from past conversations. **Memory** you can edit is added to every conversation.
* **Skills:** the profile's own, global ones every profile shares, and the read-only `~/.agents/skills` other agent tools use. `/skills add` installs more from [skills.sh](https://skills.sh) or GitHub.
* **Managing skills:** on `/skills`, edit, move, lock or disable a skill, or revert it to one of its last 10 versions. The model's `skill_editor` tool never deletes one.
* **Self-learning:** a background reflection writes new skills and improves existing ones from your work.

### Tools & safety
* **Built-in tools:** files, Git, shell (PowerShell, cmd, bash; zsh on a Mac), scripts (PowerShell, Python, Node), web search and pages, questions on a pane (a panel at the bottom of the screen), clock, timers and `neon_help`, the app's own manual.
* **Integrations** below are tools too, offered once you switch them on in `/tools` ([TOOLS.md](docs/TOOLS.md)).
* **Sandboxed:** file tools reach only the working directory; shell commands go through an approval pane (once, this session, or always).
* **Shell police (👮):** refuses a command that names a path outside the working directory or holds a forbidden string (disk wipes, shutdowns…), even under `--yolo`. It keeps the shell off the database tools' databases too ([Shell guards](docs/SETTINGS.md#shell-guards)).
* **Plan mode:** `/plan <requirement>` researches with read-only tools and saves a plan; nothing changes until you approve.
* **MCP servers** (Model Context Protocol: add-on servers with more tools and data).

### Voice
* **Voice input:** Whisper speech-to-text in-process, with push-to-talk or a wake word ("hey neon").
* **Speech output:** Kokoro text-to-speech in-process (or a Kokoro server), with voice presets you can mix.

### Integrations
* **Databases:** SQL Server, Oracle, MySQL/MariaDB, PostgreSQL and SQLite. Read-only by default; writes are opt-in per connection and ask first. Passwords are stored encrypted.
* **Files elsewhere:** `\\server\share` paths (`smb://server/share` on a Mac) and folders outside the working directory, as you or another account.
* **Obsidian:** search, read, write and link notes in your vault.
* **Docker:** Docker Desktop's containers (on a Mac, OrbStack, Colima and Rancher Desktop too): logs, health and resource use. Starting, stopping and cleaning up are opt-in and ask first; `/docker` does it by hand.
* **Home Assistant:** lights, scenes, the TV, to-do lists and sensors ("dim the den to 30%"); `/ha` drives the house directly.
* **ComfyUI:** pictures from your own workflows (text-to-image, image-to-image, face swaps), or `/imagine` with your own prompt.
* **Picture editing:** resize, crop, rotate, recolor, convert or strip metadata, by the model or with a right-click in the viewer.
* **YouTube:** search (with your API key) and play videos in the app's video window; saved videos resume where you left off.
* **Printing & PDFs:** `/print` to any installed printer, on Windows or a Mac, and `/pdf` from Markdown, text, pictures or a web page.
* **Claude Code:** `/claude` messages your installed Claude Code, or use it as the chat's server.
* **Bot chat:** `/botchat` lets your profiles talk to each other in their own voices, optionally illustrated by ComfyUI.

### Extras
* `/loop` repeats a message, `/test` benchmarks the model, and the performance bar shows CPU, RAM, GPU and network.

## Getting started

### What you need
* **A 64-bit Windows PC** (or an Apple Silicon Mac; see below) with a processor from about 2015 or later (Intel or AMD with AVX2). There's nothing else to install.
* **Windows Terminal**, which comes with Windows 11; the app is designed for it. On a Mac, Terminal or iTerm2 (iTerm2 shows pictures in full color).
* **A language model:** the app can download one for you, or use one you already run.

**On a Mac?** It runs on Apple Silicon (M1 and later). Docker servers, the Browser window, the `printer` PDF engine and the NVFP4 models need Windows, and the camera, screen capture and YouTube playback need macOS 14 or later. [On a Mac](docs/MAC.md) covers setup and every difference.

Only for some features:
* **Running a model inside the app:** an NVIDIA card (driver 580 or newer) or any Vulkan card; without one it works, but slowly. On a Mac it runs on the GPU; a 16 GB Mac suits the smaller Gemma models.
* **Web pages that need a real browser, and PDFs:** Edge, Chrome or Brave (or Chromium on a Mac).
* **`/claude` and the Claude Code server:** Claude Code installed.
* **Scripts in Python or Node:** Python or Node installed.
* **Better web search:** a SearXNG server ([setup with Docker](docs/DOCKER_WINDOWS_SEARXNG.md)), or an MCP web-search server.

### Install
1. Download `NeonSidekick-v<version>-win-x64.zip` from the [Releases page](https://github.com/M0j0Risin/NeonSidekick/releases).
2. Unzip it into any folder and run `NeonSidekick.exe`. Keep everything in the folder together.

On a Mac:
1. Download `NeonSidekick-v<version>-osx-arm64.tar.gz` from the same page and unpack it (double-click, or `tar -xzf`).
2. The build isn't notarized, so clear macOS's quarantine once: `xattr -dr com.apple.quarantine NeonSidekick-v<version>-osx-arm64`.
3. In Terminal or iTerm2, run `./NeonSidekick` in that folder. Keep everything in the folder together.
4. When macOS asks whether the app may use its Keychain key, choose **Always Allow**. For the Ctrl+Option shortcuts, let Option act as Meta ([how](docs/MAC.md#install-and-first-run)).

Each download's `.sha256` file lets you check it arrived intact.

### First launch
The app first offers, once, to put a shortcut on the desktop (with or without a log; `/shortcut` makes one later).

If no model server answers when the app starts, it opens **Connect a model**:

| Choice | When to pick it |
|---|---|
| Scan for local servers | You run LM Studio, Ollama, llama.cpp or vLLM on this machine or your network. |
| Enter a server's URL | You know the address of an OpenAI-compatible server. |
| Download an embedded model | You have no server: the app runs a model itself. |
| Connect Docker container | You run vLLM or SGLang in Docker Desktop (Windows only). |
| Use the Anthropic API / Use the OpenAI API | You have an API key (billed per message). |
| Use Claude Code | You have Claude Code and a Claude subscription. |

Pick one and the app walks you through it; ESC skips, and `/server` brings the page back. Then type a message and press Enter.

Good to know:
* Settings, memories and conversations are saved in `%USERPROFILE%\.neonsidekick` (`~/.neonsidekick` on a Mac).
* The model can't touch your files, the web or the shell until you allow it in `/tools`.
* Voice is off at first. `/tts` turns on spoken replies and `/stt` voice input; each first downloads what it needs (about 330 MB and 150 MB).

### Useful first commands
| Command | What it does |
|---|---|
| `/server` | Connects to a model server, or switches. |
| `/model` | Picks a model on the current server. |
| `/tools` | Chooses what the model may do: files, web, shell and more. |
| `/cwd` | Changes the working directory, the folder the model works in. |
| `/tts` / `/stt` | Turns spoken replies / voice input on or off. |

Type `/` to list every command; [COMMANDS.md](docs/COMMANDS.md) has them all, and [COMMANDLINE.md](docs/COMMANDLINE.md) the options to start the app with.

## Settings & menus

Everything is set from panes, and each explains itself: the highlighted row's description and default show under the list. [Using the app](docs/INTERFACE.md) covers keys, bars and windows; [SETTINGS.md](docs/SETTINGS.md) lists every setting.

| Pane | Opens with | What's in it |
|---|---|---|
| `/settings` | Ctrl+/ or `//` | The app: chat, sessions and botchat (Client), the model servers (Servers), advisors, and voice. |
| `/tools` | Ctrl+Alt+T | Which tools the model may use, and their options. |
| `/skills` | Ctrl+Alt+S | Agent skills and self-learning. |
| `/mcp` | Ctrl+Alt+D | External MCP servers. |
| `/sys` | Ctrl+Y | What the model is sent. |
| `/usage` | Ctrl+U | Speed and token statistics. |

### Getting around
* **Keyboard:** ←/→ switch tabs, ↑/↓ move, Enter edits or toggles, Space flips an on/off row, ESC closes. ←/→ also walk a tab's own tabs on the second row; Tab jumps a whole tab.
* **Mouse:** a double-click picks a row or tab; the × at the top right closes the pane.
* **Lost?** Ask the model where a setting is; its `neon_help` tool knows them all.

### The input line
* **Enter** sends; during a reply it queues the message.
* **ESC** stops a reply; twice on an empty line it opens `/rewind`.
* **↑/↓** bring back earlier messages.
* **`/`** lists commands, **`@`** files and folders, **`#`** skills and **`$`** tools.
* **Ctrl+O** opens or folds tool calls, code and thinking.
* **☺** at the line's right end opens an emoji picker (Ctrl+J too, on Windows); the *Emoji picker* setting hides it.

### Handy shortcuts

| Shortcut | Does |
|---|---|
| Ctrl+H | `/help`: every command and key |
| Ctrl+S | `/server`: pick the LLM server |
| Ctrl+M | `/model`: pick the model |
| Ctrl+R | `/reasoning`: how hard the model thinks |
| Ctrl+P | `/profile`: switch profile |
| Ctrl+Z | `/theme`: pick a color theme |
| Ctrl+Alt+N | `/new`: start a new conversation |

On a Mac, Ctrl+D picks the model (a Mac terminal sends Ctrl+M as Enter). `/help` › Keys and [Keyboard shortcuts](docs/INTERFACE.md#keyboard-shortcuts) list every key.

### Status bar, hints and toolbar
The [hints bar](docs/INTERFACE.md#the-hints-bar) under the input line shows the keys that apply just now, while there are any. The [status bar](docs/INTERFACE.md#the-status-bar) under it shows the model, the context used and what runs in the background; double-click the model to switch. The [toolbar](docs/INTERFACE.md#the-toolbar) below has a glyph per pane and tool group: click to see what it is, double-click to open it. Ctrl+T hides it.

### Settings worth knowing first

| Setting | Where | What it does | Default |
|---|---|---|---|
| Working directory (cwd) | `/settings` › Client › Chat | The folder the file and Git tools work in. | the profile's `files` folder |
| LLM URL, LLM model | `/settings` › Servers › Common | The server and model; `/server` and `/model` set them. | (none) |
| Theme | `/settings` › Client › Chat | One of sixty color themes, or your own. | `collider` |
| Memory mode | `/settings` › Client › Chat | Whether the model remembers facts about you. | `read-write` |
| TTS output, STT input | `/settings` › Voice › TTS, STT | Spoken replies; voice input on a push-to-talk key (F4). | off |
| File tools | `/tools` › Files › File | The model reads and writes files in the working directory. | off |
| Image tools | `/tools` › Files › Image | The model looks at and edits pictures. | off |
| Web tools | `/tools` › Core › Web | The model searches the web and reads pages. | off |
| Shell command policy | `/tools` › Core › Shell | Whether the model may run commands: `off`, `ask` (you approve any not on the allowed list) or `yolo` (no asking). | `off` |

A [command-line option](docs/COMMANDLINE.md) or an [environment variable](docs/ENVIRONMENT.md) can override a few settings for one launch.

## About

**Why "Neon":** early on I was trying synthwave-style themes in Spectre.Console while testing the Vosk voice integration. I needed a short, punchy wake word, and "Neon" fit the look. The name stuck.

**What's next:** stronger coding tools and Linux.

**License:** GPLv3; see [LICENSE](LICENSE). Built on the libraries in [COMPONENTS.md](docs/COMPONENTS.md).
