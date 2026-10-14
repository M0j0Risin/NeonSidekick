# Headless examples

`NeonSidekick.exe --headless` runs Neon as a plain stdin/stdout REPL for scripts: no TUI, no voice, no timers, no approval
pane. It reads one line at a time, answers each on stdout, and exits at `/exit` or the end of input with code 0, or 3 if a
shell command was refused (see [When a command is refused](#when-a-command-is-refused)). The examples assume the published
executable is on `PATH`; from a checkout, use `dotnet run --project src/NeonSidekick -- --headless …`.

## Before you start

- **A fresh profile does nothing useful headless.** It offers the model no tools (*Shell command policy* `off`, every tool
  group off) and looks for no server (*LLM server scan mode* `off`). Set the profile up first in the TUI (`/tools`,
  `/settings`) or in its `profile.json`: save a server (or pass `--url`) and switch on the tool groups the job needs.
- **Tick the "… offered" lists beforehand.** The SQL, Oracle, MySQL, PostgreSQL, SQLite, UNC and ComfyUI tools offer nothing
  until entries are ticked in their *… offered* lists (on `/tools`, or `SqlConnectionsOffered`, `OracleConnectionsOffered`,
  `MySqlConnectionsOffered`, `PostgresConnectionsOffered`, `SqliteDatabasesOffered`, `UncSharesOffered`,
  `ComfyWorkflowsOffered` in `profile.json`). Headless can't tick them. SQLite files in the working directory need no ticking.
- **Anything that needs a pane is never offered.** The database write tools (`sqlite_execute`, `sql_execute`,
  `oracle_execute`, `mysql_execute`, `postgres_execute`) are left out, so the database tools only read. The model's Docker
  changes, `print_file` under `ask`, `camera_capture`, `screen_capture`, `screen_list` and the YouTube playing tools are
  refused or left out too.
- **The shopping tools work**, except `shopping_clear` with `all`, which is refused (emptying the list needs a yes on a pane).
  Your own `/shopping clear all` and `/shopping store remove` act at once: the typed line is the yes.
- **The shell policy for one run** comes from `--yolo` or `NEONSIDEKICK_COMMAND_POLICY` (see [Running "yolo"](#running-yolo-every-shell-command-allowed)).
- **On a Mac** ([Building on a Mac](BUILD.md#building-on-a-mac)) the executable is `NeonSidekick` (no `.exe`) and
  `run_command` uses `zsh` unless the profile names another shell. The examples carry over:
  `echo "Ping." | NeonSidekick --headless --cwd ~/src/myapp`. In `pwsh` they work as written, without the `.exe`.

## What the output looks like

```
NeonSidekick 0.5.2
Headless mode. Type a message; /clear or /new forgets the conversation; /compact [focus] shrinks it; /plan <requirement> plans before doing (/plan approve [--fresh] | cancel | show | save [name] | open [name]); /skills add <source> [--global] [--yes] installs a skill; /claude <message> asks Claude Code; /rewind [n] goes back n messages; /exit or EOF exits.
LLM: http://127.0.0.1:1234/v1 model=qwen3-30b-a3b (configured)
You: Neon: Here is what I found…
[tool] web_search {"query":"…"}
[tool] web_search -> 1. …
[notice] …
You:
```

- `You: ` is printed before each line is read, even when stdin is piped, so a reply line starts `You: Neon: `.
- `[tool] name {args}` and `[tool] name -> result` trace the model's tool calls. `[notice]`, `[error]` and `[category] message`
  lines are notices and diagnostics (warnings and errors only; use `--log` for the rest).
- Each **line** of input is one message; join a multi-line prompt into one line.

---

## Slash commands in headless mode

### The ones that work

| Command | What it does headless |
|---|---|
| `/exit` | Ends the run (exit code 0), as the end of input does. No server needed. |
| `/new` | Forgets the conversation; the next message starts a new stored session. No server needed. |
| `/clear` | Same as `/new`. Prints `Neon: (conversation cleared)`. |
| `/splash` | Same as `/clear`. |
| `/rewind [n]` | Goes back n messages (1 by default) at once, printing the dropped message as a `[notice]` to send again. Tool changes are not undone (a `[notice]` names those tools). No server needed. |
| `/compact [focus]` | Shrinks the conversation now; a focus steers the summary. Needs a server. |
| `/plan <requirement>` | Plan mode, as in the TUI (see [Plan mode](COMMANDS.md#plan-mode)). With no pane, the plan is saved as `.neon/plans/<name>.md` and a `[notice]` names it; then `/plan approve [--fresh]`, `cancel`, `show`, `save [<name>]` or `open [<name>]`. Needs *LLM offer tools* and a server. |
| `/claude <message>` | Sends the message to Claude Code (see [Claude Code from the chat](COMMANDS.md#claude-code-from-the-chat)); the reply follows `Claude: `, with a `[notice]` for the cost. Nothing is ever asked: what *Claude Code command permissions* doesn't allow is denied. No server needed. |
| `/ha [on\|off\|toggle <name> [n%] \| scene <name> \| tv … \| states [filter] \| say <sentence>]` | Drives Home Assistant, printing each answer line (see `/ha` in [All commands](COMMANDS.md#all-commands)); *Home Assistant action policy* doesn't apply. Needs *Home Assistant URL* and *API key*; no server needed. |
| `/avatar [model\|user] [<emoji>\|<words>\|clear]` | Bare, prints the profile's *Model avatar* (with `user`, your *User avatar*); an emoji or words set it, `clear` clears it. No server needed. |
| `/docker [ps \| status \| logs <container> [lines] \| stats [container] \| start\|stop\|restart\|pause\|unpause <container>]` | Drives Docker directly (see [Docker](TOOLS.md#docker)); the bare word lists the containers. *Docker writes* doesn't apply, and every change is logged. No server needed. |
| `/camera list` | Lists the cameras, the chosen one marked. Every other `/camera` form is an `[error]`: a photo needs the panes. |
| `/youtube <words>` | Searches YouTube (needs a *YouTube API key*) and prints numbered results with ids. The search switches, `/youtube playlist <id\|link>`, `save <id\|link>`, `unsave` and `list [--clear]` work too; playing a video is an `[error]` (no video window). |
| `/screen list` | Lists the monitors and windows with their targets. Every other `/screen` form is an `[error]`. |
| `/browser` | An `[error]`: the Browser window is the screen's. A web tool's bot check is never shown either; the tool reports the refusal. |
| `/print <file> [printer=<name>] [copies=N] [pages=1-3] [landscape] \| reply \| printers` | Prints a file or the last reply, or lists the printers (see [Printing](TOOLS.md#printing)); *Print action policy* doesn't apply. On a Mac it prints through CUPS. No server needed. |
| `/pdf <file> \| https://… \| reply [to=<out.pdf>] [paper=letter\|a4\|legal] [landscape] [overwrite]` | Makes a PDF in the working directory (see [Making PDFs](TOOLS.md#making-pdfs)); *Web browser network mode* still judges a web page. On a Mac only the `browser` engine works. No server needed. |
| `/test [id \| reasoning \| structured \| long \| all \| history]` | Runs the [benchmark tests](COMMANDS.md#benchmark-tests): a `[notice]` per test, then the results table. Alone it lists the tests; `history` prints saved runs. The listing and `history` need no server; a run does. |
| `/skills add <source> [--global \| --profile] [--yes]` | Installs an Agent Skill (see [Installing skills](SETTINGS.md#installing-skills)). Without `--yes` it only previews; several matches print as ids to type back. `--global` is refused while *Global skills* is off. No server needed. |

- Case doesn't matter and spaces around the line are ignored. `/exit`, `/new`, `/clear` and `/splash` must be alone on the
  line (`/new please` goes to the model as text).
- With no server, the commands marked "no server needed" (and `/shopping`) still work; `/compact`, `/plan` and messages get
  a "no assistant" reply.
- Automatic compaction (*LLM auto compact (%)*) and the mid-reply guard (*LLM tool compact mode*) run headless too, each
  reported as `[notice]` lines.

### Everything else goes to the model as text

Any other `/…` line is sent to the model as a message, so `/model gemma` or `/tts on` switches nothing. Avoid them in
scripts. What to use instead:

| Interactive command | Headless equivalent |
|---|---|
| `/server [url]` | `--url <url>` or `NEONSIDEKICK_LLM_URL` |
| `/model [id]` | `--model <id>` or `NEONSIDEKICK_LLM_MODEL` |
| `/reasoning [level]` | `NEONSIDEKICK_LLM_REASONING` |
| `/sampling [field value]` | `NEONSIDEKICK_LLM_SAMPLING` (the profile's saved sampling applies too). |
| `/cwd [path]` | `--cwd <path>` |
| `/profile [name]` | `--profile <name>` or `NEONSIDEKICK_PROFILE` (default `neon`). Managing profiles needs the TUI. |
| `/settings`, `//`, `/tools`, `/skills` (the pane, its revert and `/skills purge`; `/skills add` works headless), `/mcp` | Set things up in the TUI, or edit `profile.json` / `mcp.json`. A key typed in plain text is encrypted at the next load. |
| `/remember <text>` | Ask in a message; the model has `save_memory`. |
| `/memory` | Ask the model to recall; edit `memory.json` or `MemoryMode` in `profile.json`, or use the TUI. |
| `/sessions` | Ask the model to search past sessions (with *Session tool* on). Restoring needs the TUI. |
| `/imagine …` | Ask the model to make the picture (with ComfyUI set up); the prompt is then the model's. |
| `/loop …` | Repeat the line, or loop in the calling script. |
| `/learn` | Not available. |
| `/timer` | Not available (nothing could deliver the alert). |
| `/tts`, `/stt` (and its `wake` and `interrupt`), `/read`, `/echo` | Not available; headless never speaks or listens. |
| `/cmd`, `/msgclear`, `/tools shell allowed`, `/tools shell police` | Not available. Manage the allow list in the TUI; `NEONSIDEKICK_COMMAND_POLICY` sets the policy for a run. |
| `/profile push\|pull servers` | Not available; `--url`, `--model` and `NEONSIDEKICK_LLM_URL` set the server for a run. |
| `/profile push\|pull keys` | Not available; the `NEONSIDEKICK_*_API_KEY` variables and `NEONSIDEKICK_HA_TOKEN` set keys for a run. |
| `/usage` | Not available; `--log` records the run. |
| `/process` | Not available; ask the model (its `process` tool lists, reads and stops background processes). |
| `/sys` | Not available; use `/sys` in the TUI on the same profile. |
| `/copy`, `/draft`, `/view`, `/tree`, `/vault`, `/explore`, `/open`, `/browse`, `/terminal`, `/shortcut`, `/theme`, `/perfbar`, `/homebar`, `/toolbar`, `/header`, `/window`, `/expand`, `/collapse`, `/find`, `/queue`, `/rename`, `/help`, `/about`, `/log`, `/comfy`, `/gituser`, `/botchat` | Not available (they need the screen, an editor, the clipboard or a confirmation). For `/tree` or `/vault`, ask the model to list the folder. |

---

## `--headless` on its own

Interactive in a plain console (over SSH, or wherever the TUI misbehaves):

```powershell
NeonSidekick.exe --headless
```

One question, answer to a file:

```powershell
"What is today's date, and what day of the week is it?" | NeonSidekick.exe --headless > answer.txt
```

A multi-step job: each line is the next message in the same conversation; `/new` gives a clean slate and `/compact` keeps a
long job inside the context window.

```powershell
@"
Fetch https://example.com/changelog and list every breaking change since v2.0.
/compact keep only the list of breaking changes
Now check which of those affect files in this folder.
/new
Summarize README.md in five bullets.
"@ | NeonSidekick.exe --headless
```

From a file (`job.txt`, one message per line):

```cmd
NeonSidekick.exe --headless < job.txt > run_output.txt
```

---

## `--url <url>`: which LLM server

Outranks `NEONSIDEKICK_LLM_URL` and the saved URL. `--url=<url>` works too. A server's key goes only in the environment
(`NEONSIDEKICK_LLM_API_KEY`), never on the command line.

```powershell
"Say hello in French." | NeonSidekick.exe --headless --url http://gpu-box:8080/v1
```

Special servers:

- **The Anthropic or OpenAI API:** switch it on and give its key for the run, then point `--url` at it. Without `--model`
  the run picks one. Without the switch and key, the URL is ignored (with a warning).

  ```powershell
  $env:NEONSIDEKICK_ANTHROPIC_API = "on"
  $env:NEONSIDEKICK_ANTHROPIC_API_KEY = (Get-Secret AnthropicKey -AsPlainText)
  "Summarise this repo's README." | NeonSidekick.exe --headless --url https://api.anthropic.com --model claude-sonnet-5
  ```

  The OpenAI API is the same with `NEONSIDEKICK_OPENAI_API`, `NEONSIDEKICK_OPENAI_API_KEY` and `https://api.openai.com`.
- **The embedded LLM:** `--url embedded` runs a model the app downloaded, with `--model` naming an installed id (else the
  first installed). A headless run never downloads a model (install it once in `/settings` › Servers › Embedded), but it
  does fetch the llama.cpp runtime on a first run. *Embedded servers enabled* must be on; the embedded VRAM and context
  settings apply, and a model that doesn't fit is refused. On a Mac it runs on Metal.

  ```powershell
  Get-Content job.txt | NeonSidekick.exe --headless --url embedded --model gemma-4-e2b
  ```
- **The Claude Code server:** `--url claude-code` runs your installed, logged-in Claude Code as the model, with this app's
  tools (see [Anthropic](SETTINGS.md#anthropic)). Switch it on with `NEONSIDEKICK_CLAUDE_CODE_SERVER=on`; `--model` is
  `fable`, `opus`, `sonnet` (the default) or `haiku`. Without the switch, or with Claude Code not found, the URL is ignored
  (with a warning).
- **A Docker server (Windows only):** `--url docker:<container>` stops the profile's other chosen containers, starts this
  one and waits until it answers (see [Docker servers](TOOLS.md#docker-servers)). *Docker servers enabled* must be on and
  the container ticked in *Docker server containers*. It keeps running after the run unless *Docker server stop on exit* is on.

---

## `--model <id>`: which model on that server

Outranks `NEONSIDEKICK_LLM_MODEL` and the saved model. With `--url`, it pins both for a reproducible run:

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --url http://127.0.0.1:1234/v1 --model gemma-3-27b-it
```

---

## `--cwd <path>`: the working directory (the file sandbox)

The file, GitLib, shell and download tools all work inside this folder. A relative path resolves against where you launch
from. Outranks the saved *Working directory*.

```powershell
foreach ($repo in Get-ChildItem D:\Repo -Directory) {
    "Write a three-line status of this repo to STATUS.md." | NeonSidekick.exe --headless --cwd $repo.FullName
}
```

---

## `--profile <name>`: which profile (settings, memory, sessions, allow list)

Loads the named profile for this launch only; the next normal launch is unchanged. Outranks `NEONSIDEKICK_PROFILE`; with
neither, a headless run loads `neon`, never the one the TUI last used. An unknown name lists the profiles and exits with
code **2**. A dedicated profile keeps scripted runs' memory, sessions and allow list out of your everyday one:

```powershell
"hello" | NeonSidekick.exe --headless --profile automation
if ($LASTEXITCODE -eq 2) { Write-Error "No such profile" }
```

---

## `--log <path>`: every diagnostic line to a file

Appends every diagnostic line to the file (stdout still shows only warnings and errors), starting with the mode, flags,
profile and environment overrides in force. `{ts}` in the path becomes the run's start time (`yyyyMMdd-HHmmss`), and the
folder is created if needed.

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --log C:\Logs\neon_{ts}.log
Select-String -Path C:\Logs\neon_*.log -Pattern "Startup|Llm|error"   # a run that "did nothing"
```

---

## Running "yolo": every shell command allowed

The shell policy for a run comes from `--yolo`, then `NEONSIDEKICK_COMMAND_POLICY` (`off`, `ask` or `yolo`), then the
profile's *Shell command policy*. Neither the flag nor the variable is saved.

- `off` (the default): no shell tools.
- `ask`: nothing can ask, so only commands on the profile's *Shell allowed commands* run.
- `yolo`: every `run_command` command and `execute_code` script runs without asking. Use it only when you trust the job and the folder.

`yolo` does **not** turn off the shell police. With *Shell police* on (the default), these are still refused and count
toward exit code 3: a path outside the working directory, the *Shell police forbidden strings* (disk wipes, shutdown,
`push --force`, `reset --hard`…; see [Shell guards](SETTINGS.md#shell-guards)), and reaching a database some other way
while its tools are on.

```powershell
"Run the unit tests and fix any that fail." | NeonSidekick.exe --headless --yolo --cwd D:\Repo\MyApp
```

With the variable, set it for one command only:

```bash
NEONSIDEKICK_COMMAND_POLICY=yolo NeonSidekick --headless --cwd ~/src/myapp < job.txt   # bash or zsh
```

In PowerShell, `$env:` changes last for the session, so remove it afterwards (`try { … } finally { Remove-Item Env:NEONSIDEKICK_COMMAND_POLICY }`).

`--yolo` works in the TUI too: no approval pane for that launch, and the toolbar shows 🔓.

### Turning off the path police

The path police refuses any command, script or `process` write naming a path outside the working directory (`C:\…`, `/…`,
`..`, `~`, `$HOME`…). `--no-police` or `NEONSIDEKICK_SHELL_POLICE=off` turns it off for a run (`on` turns it back on over a
saved `off`); the flag outranks the variable, and neither is saved. It also stops the forbidden strings.

> **Warning:** `--yolo --no-police` together leave no guard at all: the model can run any command against any path, with
> your account's rights. Use it only for jobs and machines you'd trust with that.

```powershell
"Build the solution and copy the output to D:\Drops\MyApp." | NeonSidekick.exe --headless --no-police --cwd D:\Repo\MyApp
```

For a profile that should always run without it, save *Shell police* off there (`/tools shell police`) and pick it with `--profile`.

### Checking that it took effect

With `--log`, the startup line lists the flag or `Overrides in force: … NEONSIDEKICK_COMMAND_POLICY=yolo`, and each command
is logged as approved by `yolo`:

```powershell
Select-String -Path yolo.log -Pattern "--yolo|COMMAND_POLICY|approval: yolo"
```

### A safer alternative

Stay on `ask` and allow-list only the prefixes the job needs (`dotnet build`, `git status`…): run the job once in the TUI on
the same profile and answer *Allow … always*, or copy a list with `/profile pull allowed <name>`. Exit code 3 then tells
you when the job needed something more.

---

## When a command is refused

Under `ask`, a command not on the allow list is not run; nothing waits or crashes. The model is told it was refused, not to
work around it, and which prefixes are allowed. On stdout:

```
[tool] run_command {"command":"npm install"}
[tool] run_command -> Error: the command was not run: there was no screen to ask the user on (...)
```

The run carries on. At the end, if anything was refused, it prints a summary and exits with code **3**:

```
[notice] 2 commands were not run: "npm install", "python script"
```

Path-police refusals count too, under `--yolo` as well. A refused `execute_code` script shows as `python script`,
`powershell script` and so on.

**Exit codes:** **0** nothing was refused, **2** bad argument or unknown profile (nothing ran), **3** at least one command was refused.

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --cwd D:\Repo\MyApp | Tee-Object run.txt
if ($LASTEXITCODE -eq 3) { Select-String -Path run.txt -Pattern "commands? (was|were) not run" }
```

---

## Putting the flags together

A fully pinned job:

```powershell
Get-Content D:\Jobs\release_notes.txt |
    NeonSidekick.exe --headless --url http://127.0.0.1:1234/v1 --model qwen3-30b-a3b `
        --profile automation --cwd D:\Repo\MyApp --log D:\Jobs\logs\release_notes_{ts}.log |
    Out-File D:\Jobs\out\release_notes.txt
```

From Python, one message per line:

```python
import subprocess

proc = subprocess.run(
    ["NeonSidekick.exe", "--headless", "--profile", "automation", "--cwd", r"D:\Repo\MyApp"],
    input="Summarize the open TODOs.\nNow rank them by effort.\n/exit\n", capture_output=True, text=True, encoding="utf-8",
)
replies = [line.split("Neon: ", 1)[1] for line in proc.stdout.splitlines() if "Neon: " in line]
```

For a scheduled job, run a script like these from Task Scheduler (or `cron`/`launchd` on a Mac) and return `$LASTEXITCODE`.

---

## Environment variables that matter in headless runs

Flags beat variables; variables beat the profile's saved values. [ENVIRONMENT.md](ENVIRONMENT.md) lists every variable.

| Variable | Use in a headless run |
|---|---|
| `NEONSIDEKICK_PROFILE` | The profile, when there is no `--profile`; with neither, `neon`. |
| `NEONSIDEKICK_HOME` | A whole separate home (its own settings, profiles, models and connection files). |
| `NEONSIDEKICK_LLM_URL` / `NEONSIDEKICK_LLM_MODEL` | Server and model, when there is no `--url` / `--model` (`embedded`, `docker:<container>` too). |
| `NEONSIDEKICK_EMBEDDED_BACKEND` / `NEONSIDEKICK_EMBEDDED_CONTEXT` | The embedded LLM's build (`auto`, `cuda`, `vulkan`, `cpu`; on a Mac `auto`, `metal`) and context in tokens (0 fits the GPU). |
| `NEONSIDEKICK_LLM_API_KEY` | The server's key; never put it on the command line. |
| `NEONSIDEKICK_ANTHROPIC_API` / `NEONSIDEKICK_ANTHROPIC_API_KEY` | `on` and a key offer the Anthropic API for the run. Requests are billed to the key's account. |
| `NEONSIDEKICK_OPENAI_API` / `NEONSIDEKICK_OPENAI_API_KEY` | `on` and a key offer the OpenAI API for the run. Requests are billed to the key's account. |
| `NEONSIDEKICK_YOUTUBE_API_KEY` | A YouTube Data API key for searches and playlist listings (100 quota units a search). |
| `NEONSIDEKICK_CLAUDE_CODE_SERVER` | `on` offers the Claude Code server (`--url claude-code`). |
| `NEONSIDEKICK_LLM_TURN_TIMEOUT` / `NEONSIDEKICK_LLM_REQUEST_TIMEOUT` | Seconds; raise them for long agentic jobs. |
| `NEONSIDEKICK_LLM_CONTEXT` | The context window in tokens, when the server doesn't report it. |
| `NEONSIDEKICK_LLM_REASONING` | Reasoning effort for the run. |
| `NEONSIDEKICK_LLM_SAMPLING` | Sampling as JSON in wire names (`{"temperature":0.2,"seed":42}`), laid over the saved values; a bad value is ignored. |
| `NEONSIDEKICK_COMMAND_POLICY` | `off` (default), `ask` (allow-listed commands only) or `yolo` (everything). `--yolo` outranks it. |
| `NEONSIDEKICK_SHELL_POLICE` | `off` lets commands name paths outside the working directory; `on` overrides a saved `off`. `--no-police` outranks it. |
| `NEONSIDEKICK_SHELL_NATIVE` | `off` lets a lone `cat`, `ls`, `git status`… run in the shell instead of being sent to the native tool (*Shell prefer native tools*). Never causes exit 3. |
| `NEONSIDEKICK_CLAUDE_CODE_EXE` / `NEONSIDEKICK_CLAUDE_CODE_PERMISSIONS` | The Claude Code CLI for `/claude`, and what it may do: `read-only` (default) or `read-write`. |
| `NEONSIDEKICK_ADVISORS` | `on` offers `neon_advisor` over the profile's *Advisor list*. With *Advisor confirm* on, every call is refused (nothing can ask). |
| `NEONSIDEKICK_SEARXNG_URL`, `NEONSIDEKICK_OBSIDIAN_VAULT`, `NEONSIDEKICK_COMFY_URL` | The web search instance, notes vault and image server for the run. |
| `NEONSIDEKICK_DOCKER_PIPE` | The Docker engine's pipe (*Docker engine pipe*), or on a Mac its socket. The model's Docker reads work headless; its changes are refused. |
| `NEONSIDEKICK_HA_URL` / `NEONSIDEKICK_HA_TOKEN` | The Home Assistant server and access token. Under *Home Assistant action policy* `ask`, only the safe services (lights, scenes, the TV, to-do lists) run headless. |

## Flags that don't combine with `--headless`

`--smoke`, `--audio-check`, `--voice-check`, `--sql-check <connection>`, `--oracle-check <connection>`, `--mysql-check <connection>`,
`--sqlite-check <database>`, `--postgres-check <connection>`, `--unc-check <share>`, `--docker-check` and `--camera-check` are
separate modes; with `--headless`, headless wins and they are ignored. `--help` and `--version` print and exit first.

`--mcp-relay <address> <key>`, `--llama-guard …` and `--shell-guard …` are the app's own internal modes, not for you to run.
