# Environment variables

Every variable Neon Sidekick reads. Back to the [README](../README.md).

Each starts with `NEONSIDEKICK_`, overrides a setting for one launch, and is never saved. Settings are in [SETTINGS.md](SETTINGS.md), command-line options in [COMMANDLINE.md](COMMANDLINE.md), and [HEADLESS.md](HEADLESS.md) shows the variables in scripted runs.

* **Precedence:** command-line flag > variable > saved setting > default. A settings row a variable overrides says so.
* **Values:** blank means unset, and word values ignore case. **on/off** also takes `true`/`false`, `1`/`0` and `yes`/`no`. A value that doesn't parse is logged and ignored.
* **Logging:** with `--log`, the log lists the variables in force, the keys only as `(set)`.
* **Children:** a command or script the shell tools or `/cmd` start inherits the environment, except the five secrets (`NEONSIDEKICK_LLM_API_KEY`, `…_ANTHROPIC_API_KEY`, `…_OPENAI_API_KEY`, `…_HA_TOKEN`, `…_YOUTUBE_API_KEY`), which are withheld.

## Contents

- [Where and who](#where-and-who)
- [LLM](#llm)
- [Embedded LLM](#embedded-llm)
- [Shell](#shell)
- [Claude](#claude)
- [OpenAI](#openai)
- [Speech](#speech)
- [Integrations](#integrations)
- [Set by the app](#set-by-the-app)
- [Test suite](#test-suite)

## Where and who

| Variable | What it does | Accepts |
|---|---|---|
| `NEONSIDEKICK_HOME` | The home folder: settings, profiles, models, llama.cpp, skills and the connection files. | A folder path. Default `%USERPROFILE%\.neonsidekick` (`~/.neonsidekick` on a Mac). |
| `NEONSIDEKICK_PROFILE` | The profile for this launch; the saved choice is left alone. `--profile` wins, and a headless run with neither loads `neon`. An unknown name exits with code 2. | A profile name. |

## LLM

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_LLM_URL` | *LLM URL* (`--url` wins) | A base URL (`http://127.0.0.1:1234/v1`), or `embedded`, `claude-code` or `docker:<container>` (Windows only). |
| `NEONSIDEKICK_LLM_MODEL` | *LLM model* (`--model` wins) | A model id, or an embedded model's id (`gemma-4-e2b`). |
| `NEONSIDEKICK_LLM_API_KEY` | *LLM API key* | The key as issued. Never logged. |
| `NEONSIDEKICK_LLM_REASONING` | *LLM reasoning* | `none`, `low`, `medium`, `high`, `xhigh`. |
| `NEONSIDEKICK_LLM_REQUEST_TIMEOUT` | *LLM request timeout (s)* | Seconds, above 0 and up to 3600. |
| `NEONSIDEKICK_LLM_TURN_TIMEOUT` | *LLM turn timeout (s)* | Seconds, above 0 and up to 21600. |
| `NEONSIDEKICK_LLM_CONTEXT` | *LLM context length* | Tokens, a whole number above 0; used in place of the server's own figure. |
| `NEONSIDEKICK_LLM_SAMPLING` | *LLM sampling*, for every model | A JSON object (`{"temperature":0.6,"top_k":20,"typical_p":0.9}`). Known fields must be in range, and other keys are passed through; saved values fill in the rest. |

## Embedded LLM

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_EMBEDDED_BACKEND` | *Embedded backend* | `auto`, `cuda`, `vulkan`, `cpu`; on a Mac `auto`, `metal`. |
| `NEONSIDEKICK_EMBEDDED_CONTEXT` | *Embedded context length* | Tokens: 0 (fit) or 512–262144. |

## Shell

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_COMMAND_POLICY` | *Shell command policy* (`--yolo` wins) | `off`, `ask`, `yolo`. Headless under `ask`, only allow-listed commands run. |
| `NEONSIDEKICK_SHELL_POLICE` | *Shell police* (`--no-police` wins) | on/off |
| `NEONSIDEKICK_SHELL_NATIVE` | *Shell prefer native tools* | on/off |

## Claude

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_CLAUDE_CODE_EXE` | *Claude Code executable* | The Claude Code CLI's full path. |
| `NEONSIDEKICK_CLAUDE_CODE_PERMISSIONS` | *Claude Code command permissions* | `read-only`, `read-write`. |
| `NEONSIDEKICK_ADVISORS` | *Advisors enabled* (the model's `neon_advisor`) | on/off |
| `NEONSIDEKICK_ANTHROPIC_API` | *Anthropic API enabled* | on/off |
| `NEONSIDEKICK_ANTHROPIC_API_KEY` | *Anthropic API key* | The key as issued. Never logged. Requests are billed to its account. |
| `NEONSIDEKICK_CLAUDE_CODE_SERVER` | *Claude Code server enabled* | on/off |

## OpenAI

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_OPENAI_API` | *OpenAI API enabled* | on/off |
| `NEONSIDEKICK_OPENAI_API_KEY` | *OpenAI API key* | The key as issued. Never logged. Requests are billed to its account. |

## Speech

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_TTS_URL` | *TTS HTTP URL* | A Kokoro HTTP server's URL. |
| `NEONSIDEKICK_TTS_VOICE` | *TTS voice* | A voice name (`af_heart`). |
| `NEONSIDEKICK_TTS_VOICE2` | *TTS voice 2* | A voice name. It can't clear a saved second voice; `NEONSIDEKICK_TTS_MIX=100` plays the first alone. |
| `NEONSIDEKICK_TTS_MIX` | *TTS voice mix (%)* | 0–100. |
| `NEONSIDEKICK_TTS_SPEED` | *TTS speed* | 0.5–2.0. |
| `NEONSIDEKICK_WHISPER_MODEL` | *STT Whisper model* | A model name or a ggml file's path. |
| `NEONSIDEKICK_INTERRUPT_ECHO` | *STT interrupt echo guard (%)* | 50–100. |
| `NEONSIDEKICK_INTERRUPT_CONFIRM` | *STT interrupt hold (ms)* | Milliseconds, 0–2000. |

## Integrations

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_SEARXNG_URL` | *Web SearXNG URL* | The instance's URL; *Web search method* still picks the engine. |
| `NEONSIDEKICK_OBSIDIAN_VAULT` | *Obsidian vault* | The folder holding `.obsidian`. |
| `NEONSIDEKICK_COMFY_URL` | *ComfyUI URL* | The server's URL (`http://gpu-box:8188`). |
| `NEONSIDEKICK_HA_URL` | *Home Assistant URL* | The server's URL (`http://localhost:8123`). |
| `NEONSIDEKICK_HA_TOKEN` | *Home Assistant API key* | A long-lived token as issued. Never logged. |
| `NEONSIDEKICK_YOUTUBE_API_KEY` | *YouTube API key* | A YouTube Data API v3 key as issued. Never logged. Each search costs 100 of its daily quota units. |
| `NEONSIDEKICK_DOCKER_PIPE` | *Docker engine pipe* (*Docker engine socket* on a Mac) | A pipe name, `\\.\pipe\name` or `npipe:////./pipe/name`; on a Mac a socket's path or `unix:///path`. See the notes below. |

On a Mac, with neither `NEONSIDEKICK_DOCKER_PIPE` nor the setting, the app reads `DOCKER_HOST` (but not a `tcp://` or `ssh://` one) and then the Docker CLI's current context (`DOCKER_CONTEXT`, `DOCKER_CONFIG`); see [the order](TOOLS.md#docker-on-a-mac).

## Set by the app

Under *Shell tool bridge*, the app passes `NEONSIDEKICK_BRIDGE_ADDRESS` and `NEONSIDEKICK_BRIDGE_TOKEN` to `execute_code` scripts for the `neon_tools` modules; don't set them yourself.

It also reads a few of the system's own variables:

* `PATH` to find shells and interpreters, with `PATHEXT`, `ProgramFiles`, `ProgramW6432` and `LocalAppData` on Windows.
* `USERPROFILE` (`HOME` on a Mac) to find Claude Code where its installer puts it.
* On a Mac, `TERM_PROGRAM` to tell Terminal.app and iTerm2 apart, and `ITERM_PROFILE` for the iTerm2 profile whose Option key `/keycheck` reads.

## Test suite

Only for running the tests from source. Each live test is skipped unless its resource is there.

* `NEONSIDEKICK_TEST_LLM_URL`, `NEONSIDEKICK_TEST_TTS_URL`, `NEONSIDEKICK_TEST_SQL_CONNECTION`: a server to test against.
* `NEONSIDEKICK_TEST_ORACLE_CONNECTION`: an ODP.NET connection string (`User Id=…;Password=…;Data Source=localhost:1521/FREEPDB1`) for a user that may create tables (`gvenzl/oracle-free` works).
* `NEONSIDEKICK_TEST_MYSQL_CONNECTION`: a MySqlConnector connection string (`Server=127.0.0.1;Port=3306;User ID=…;Password=…;Database=…`) to a database the user owns (`mysql:8.4` and `mariadb:11` work).
* `NEONSIDEKICK_TEST_POSTGRES_CONNECTION`: an Npgsql connection string (`Host=127.0.0.1;Port=5432;Username=…;Password=…;Database=…`).
* `NEONSIDEKICK_TEST_UNC_SHARE`: a readable `\\server\share` path (`\\localhost\C$\Windows`); on a Mac, `smb://server/share` (your Public share with File Sharing on works), mounted and unmounted by each test. `NEONSIDEKICK_TEST_UNC_USER` and `NEONSIDEKICK_TEST_UNC_PASSWORD` add a second account for the runas path. Reads only.
* `NEONSIDEKICK_TEST_DOCKER_CONTAINER`: a running container to read (`mysql_dev`); `NEONSIDEKICK_TEST_DOCKER_PIPE` names another pipe (a socket's path on a Mac). Reads only.
* `NEONSIDEKICK_TEST_EVENTLOG_COMPUTER`: a computer's host whose event logs are read over RPC as you (`localhost` is a loopback session). Windows only; reads only.
* `NEONSIDEKICK_TEST_NETWORK_HOST`: a host to look up, ping and (when 443 answers) shake hands with (`example.com`). Reads only.
* `NEONSIDEKICK_TEST_SERVICES_COMPUTER`: a computer whose services are read as you (`localhost` works). With `NEONSIDEKICK_TEST_SERVICE_CONTROL` naming a harmless service on this PC, the test restarts it, only when run elevated. Windows only.
* `NEONSIDEKICK_TEST_TASKS_COMPUTER`: a computer whose scheduled tasks are read as you (`localhost` needs an elevated run: UAC filters a local administrator's network sign-in). With `NEONSIDEKICK_TEST_TASK_CONTROL` naming a task you made on this PC, the test runs it, disables it and enables it again. Windows only.
* `NEONSIDEKICK_TEST_AD_DOMAIN`: `Domain=…;Server=…;User=DOMAIN\name;Password=…`, written into a scratch home's `ad.json` and read through it (the `samba_ad_dev` container: `Domain=lab.neon.test;Server=localhost;User=LAB\reader` and its password). Reads only. Windows only.
* `NEONSIDEKICK_TEST_CAMERA`: `1` or a camera's name (the light comes on); `NEONSIDEKICK_TEST_CAMERA_OUT`: a folder to keep the test photo in.
* `NEONSIDEKICK_TEST_RECYCLE`: `1` runs the real Recycle bin test (the Trash on a Mac), which leaves a temp file in it.
* `NEONSIDEKICK_TEST_PRINTER`: a printer's CUPS queue name on a Mac (`Brother_HL_L2340D_series`). Jobs are held and canceled, so nothing prints.
* `NEONSIDEKICK_TEST_HA_URL` with `NEONSIDEKICK_TEST_HA_TOKEN`: a Home Assistant to read from.
* `NEONSIDEKICK_TEST_YOUTUBE_API_KEY`: a YouTube Data API key for the live search and playlist tests (about 103 quota units a run).
* `NEONSIDEKICK_TEST_WHISPER_MODEL`, `NEONSIDEKICK_TEST_SILERO_MODEL`, `NEONSIDEKICK_TEST_VOSK_MODEL`, `NEONSIDEKICK_TEST_KOKORO_MODEL`: a model outside the home's `models` folder.
* `NEONSIDEKICK_TEST_CLAUDE=1`: the live Claude Code tests (Haiku, a few cents a run).
* `NEONSIDEKICK_TEST_CLAUDE_API_KEY`: the live Anthropic API tests (a few cents a run).
* `NEONSIDEKICK_TEST_OPENAI_API_KEY`: the live OpenAI API tests (cheap models, a few cents a run); `NEONSIDEKICK_TEST_OPENAI_SWEEP=1` also checks every listed chat model's reasoning levels.
* `NEONSIDEKICK_TEST_EMBEDDED_MODEL`: the model id the live embedded test runs (else the first installed; it needs llama.cpp and a model installed).
* `NEONSIDEKICK_TEST_LLAMA_EXE` with `NEONSIDEKICK_TEST_TINY_GGUF`: any `llama-server` and a small GGUF (`stories15M-q4_0.gguf`, 19 MB) for the server host's test.
