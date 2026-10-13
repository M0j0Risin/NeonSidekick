# Environment variables

Every variable Neon Sidekick reads. Each starts with `NEONSIDEKICK_`, overrides a setting for one launch, and is never saved. Settings are in [SETTINGS.md](SETTINGS.md), command-line options in [COMMANDLINE.md](COMMANDLINE.md).

* **Precedence:** command-line flag > variable > saved setting > default. A settings row a variable overrides says so.
* **Values:** blank means unset; case doesn't matter. **on/off** also accepts `true`/`false`, `1`/`0` and `yes`/`no`. A value that doesn't parse is logged and ignored.
* **Logging:** with `--log`, the log lists the variables in force (API keys only as `(set)`).
* **Children:** a command or script the shell tools or `/cmd` start inherits the environment, except the five secrets (`NEONSIDEKICK_LLM_API_KEY`, `…_ANTHROPIC_API_KEY`, `…_OPENAI_API_KEY`, `…_HA_TOKEN`, `…_YOUTUBE_API_KEY`), which are withheld.

[HEADLESS.md](HEADLESS.md) shows them in scripted runs.

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
| `NEONSIDEKICK_PROFILE` | The profile for this launch (the saved choice is left alone). An unknown name exits with code 2; `--profile` wins; a headless run with neither loads `neon`. | A profile name. |

## LLM

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_LLM_URL` | LLM URL (`--url` wins) | A base URL (`http://127.0.0.1:1234/v1`), or `embedded`, `claude-code` or `docker:<container>` (Windows only). |
| `NEONSIDEKICK_LLM_MODEL` | LLM model (`--model` wins) | A model id, or an embedded model's id (`gemma-4-e2b`). |
| `NEONSIDEKICK_LLM_API_KEY` | LLM API key | The key as issued. Never logged. |
| `NEONSIDEKICK_LLM_REASONING` | LLM reasoning | `none`, `low`, `medium`, `high`, `xhigh`. |
| `NEONSIDEKICK_LLM_REQUEST_TIMEOUT` | LLM request timeout (s) | Seconds, up to 3600. |
| `NEONSIDEKICK_LLM_TURN_TIMEOUT` | LLM turn timeout (s) | Seconds, up to 21600. |
| `NEONSIDEKICK_LLM_CONTEXT` | LLM context length | Tokens, for servers that don't report their window. |
| `NEONSIDEKICK_LLM_SAMPLING` | LLM sampling, for every model | A JSON object (`{"temperature":0.6,"top_k":20,"typical_p":0.9}`). Known fields must be in range; other keys are passed through. Saved values fill in the rest. |

## Embedded LLM

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_EMBEDDED_BACKEND` | Embedded backend | `auto`, `cuda`, `vulkan`, `cpu`; on a Mac `auto`, `metal`. |
| `NEONSIDEKICK_EMBEDDED_CONTEXT` | Embedded context length | Tokens: 0 (fit) or 512–262144. |

## Shell

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_COMMAND_POLICY` | Shell command policy (`--yolo` wins) | `off`, `ask`, `yolo`. Headless under `ask`, only allow-listed commands run. |
| `NEONSIDEKICK_SHELL_POLICE` | Shell police (`--no-police` wins) | on/off |
| `NEONSIDEKICK_SHELL_NATIVE` | Shell prefer native tools | on/off |

## Claude

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_CLAUDE_CODE_EXE` | Claude Code executable | The Claude Code CLI's full path. |
| `NEONSIDEKICK_CLAUDE_CODE_PERMISSIONS` | Claude Code command permissions | `read-only`, `read-write`. |
| `NEONSIDEKICK_ADVISORS` | Advisors enabled (the profile advisors' `neon_advisor`) | on/off |
| `NEONSIDEKICK_ANTHROPIC_API` | Anthropic API enabled | on/off |
| `NEONSIDEKICK_ANTHROPIC_API_KEY` | Anthropic API key | The key as issued. Never logged. |
| `NEONSIDEKICK_CLAUDE_CODE_SERVER` | Claude Code server enabled | on/off |

## OpenAI

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_OPENAI_API` | OpenAI API enabled | on/off |
| `NEONSIDEKICK_OPENAI_API_KEY` | OpenAI API key | The key as issued. Never logged. |

## Speech

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_TTS_URL` | TTS HTTP URL | A Kokoro HTTP server's URL. |
| `NEONSIDEKICK_TTS_VOICE` | TTS voice | A voice name (`af_heart`). |
| `NEONSIDEKICK_TTS_VOICE2` | TTS voice 2 | A voice name. It can't clear a saved second voice; `NEONSIDEKICK_TTS_MIX=100` plays the first alone. |
| `NEONSIDEKICK_TTS_MIX` | TTS voice mix (%) | 0–100. |
| `NEONSIDEKICK_TTS_SPEED` | TTS speed | 0.5–2.0. |
| `NEONSIDEKICK_WHISPER_MODEL` | STT Whisper model | A model name or a ggml file's path. |
| `NEONSIDEKICK_INTERRUPT_ECHO` | STT interrupt echo guard (%) | 50–100. |
| `NEONSIDEKICK_INTERRUPT_CONFIRM` | STT interrupt hold (ms) | Milliseconds, 0–2000. |

## Integrations

| Variable | Overrides | Accepts |
|---|---|---|
| `NEONSIDEKICK_SEARXNG_URL` | Web SearXNG URL | The instance's URL (*Web search method* still picks the engine). |
| `NEONSIDEKICK_OBSIDIAN_VAULT` | Obsidian vault | The folder holding `.obsidian`. |
| `NEONSIDEKICK_COMFY_URL` | ComfyUI URL | The server's URL (`http://gpu-box:8188`). |
| `NEONSIDEKICK_HA_URL` | Home Assistant URL | The server's URL (`http://localhost:8123`). |
| `NEONSIDEKICK_HA_TOKEN` | Home Assistant API key | A long-lived token as issued. Never logged. |
| `NEONSIDEKICK_YOUTUBE_API_KEY` | YouTube API key | A YouTube Data API v3 key as issued. Never logged. |
| `NEONSIDEKICK_DOCKER_PIPE` | Docker engine pipe | A pipe name, `\\.\pipe\name` or `npipe:////./pipe/name`. On a Mac (*Docker engine socket*) a socket's path or `unix:///path`; with neither this nor the setting, the app reads `DOCKER_HOST` (not `tcp://` or `ssh://`) and the Docker CLI's current context (`DOCKER_CONTEXT`/`DOCKER_CONFIG`; [the order](TOOLS.md#docker-on-a-mac)). |

## Set by the app

Under *Shell tool bridge*, the app passes `NEONSIDEKICK_BRIDGE_ADDRESS` and `NEONSIDEKICK_BRIDGE_TOKEN` to `execute_code` scripts for the `neon_tools` modules; don't set them yourself.

It also reads `PATH` to find shells and interpreters (with `PATHEXT`, `ProgramFiles`, `ProgramW6432` and `LocalAppData` on Windows), and on a Mac `TERM_PROGRAM` to tell Terminal.app and iTerm2 apart.

## Test suite

Only for running the tests from source. Each live test is skipped unless its resource is there.

* `NEONSIDEKICK_TEST_LLM_URL`, `NEONSIDEKICK_TEST_TTS_URL`, `NEONSIDEKICK_TEST_SQL_CONNECTION`: a server to test against.
* `NEONSIDEKICK_TEST_ORACLE_CONNECTION`: an ODP.NET connection string (`User Id=…;Password=…;Data Source=localhost:1521/FREEPDB1`) for a user that may create tables (`gvenzl/oracle-free` works).
* `NEONSIDEKICK_TEST_MYSQL_CONNECTION`: a MySqlConnector connection string (`Server=127.0.0.1;Port=3306;User ID=…;Password=…;Database=…`) to a database the user owns (`mysql:8.4` and `mariadb:11` work).
* `NEONSIDEKICK_TEST_POSTGRES_CONNECTION`: an Npgsql connection string (`Host=127.0.0.1;Port=5432;Username=…;Password=…;Database=…`) for the live PostgreSQL tests.
* `NEONSIDEKICK_TEST_UNC_SHARE`: a readable `\\server\share` path (`\\localhost\C$\Windows`); on a Mac also `smb://server/share` (e.g. `smb://localhost/<your Public share>` with File Sharing on), mounted and unmounted by each test. `NEONSIDEKICK_TEST_UNC_USER` and `NEONSIDEKICK_TEST_UNC_PASSWORD` add a second account for the runas path. Read only.
* `NEONSIDEKICK_TEST_DOCKER_CONTAINER`: a running container to read (`mysql_dev`); `NEONSIDEKICK_TEST_DOCKER_PIPE` names another pipe (a socket's path on a Mac). Read only.
* `NEONSIDEKICK_TEST_CAMERA`: `1` or a camera's name (the light comes on); `NEONSIDEKICK_TEST_CAMERA_OUT`, a folder to keep the test photo in.
* `NEONSIDEKICK_TEST_PRINTER`: a printer's CUPS queue name on a Mac (`Brother_HL_L2340D_series`). Jobs are held and cancelled, so nothing prints.
* `NEONSIDEKICK_TEST_HA_URL` with `NEONSIDEKICK_TEST_HA_TOKEN`: a Home Assistant to read from.
* `NEONSIDEKICK_TEST_YOUTUBE_API_KEY`: a YouTube Data API key for the live search and playlist tests (about 103 quota units a run).
* `NEONSIDEKICK_TEST_WHISPER_MODEL`, `NEONSIDEKICK_TEST_SILERO_MODEL`, `NEONSIDEKICK_TEST_VOSK_MODEL`, `NEONSIDEKICK_TEST_KOKORO_MODEL`: a model not in the home's `models` folder.
* `NEONSIDEKICK_TEST_CLAUDE=1`: the live Claude Code tests (Haiku, a few cents a run).
* `NEONSIDEKICK_TEST_CLAUDE_API_KEY`: the live Anthropic API tests (a few cents a run).
* `NEONSIDEKICK_TEST_OPENAI_API_KEY`: the live OpenAI API tests (nano models, a few cents a run); `NEONSIDEKICK_TEST_OPENAI_SWEEP=1` also checks every listed chat model's reasoning levels.
* `NEONSIDEKICK_TEST_EMBEDDED_MODEL`: the model id the live embedded test runs (else the first installed; needs llama.cpp and a model installed).
* `NEONSIDEKICK_TEST_LLAMA_EXE` with `NEONSIDEKICK_TEST_TINY_GGUF`: any `llama-server` and a small GGUF (`stories15M-q4_0.gguf`, 19 MB), for the server host's test.
