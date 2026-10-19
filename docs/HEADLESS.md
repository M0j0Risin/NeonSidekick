# Headless mode

How to run Neon Sidekick from scripts with `--headless`. Back to the [README](../README.md).

`NeonSidekick.exe --headless` is a plain stdin/stdout prompt: no TUI, no voice, no timers and nothing that can ask you a question. It answers each line of input on stdout.

It exits at `/exit` or the end of input with code 0, or 3 if a shell command was refused ([When a command is refused](#when-a-command-is-refused)). From a checkout, use `dotnet run --project src/NeonSidekick -- --headless …`.

```powershell
NeonSidekick.exe --headless                                               # type messages yourself (over SSH, or where the TUI misbehaves)
"What day of the week is it?" | NeonSidekick.exe --headless > answer.txt  # one question, the answer to a file
NeonSidekick.exe --headless < job.txt > run_output.txt                    # one message per line of job.txt
```

## Contents

- [Before you start](#before-you-start)
- [What the output looks like](#what-the-output-looks-like)
- [Slash commands in headless mode](#slash-commands-in-headless-mode)
- [Flags in headless runs](#flags-in-headless-runs)
- [Running "yolo": every shell command allowed](#running-yolo-every-shell-command-allowed)
- [When a command is refused](#when-a-command-is-refused)
- [Putting the flags together](#putting-the-flags-together)
- [Environment variables that matter in headless runs](#environment-variables-that-matter-in-headless-runs)
- [Flags that don't combine with `--headless`](#flags-that-dont-combine-with---headless)

## Before you start

- **A fresh profile does little headless.** The model gets only the clock (*Clock tools*), help (*Help tools*), memory, past-session and skill tools, and no server is looked for (*LLM server scan mode* `off`).
  - Set it up first in the TUI (`/settings`, `/tools`) or `profile.json`: a server (or `--url`) and the tool groups the job needs.
- **Tick the "… offered" lists first.** The SQL, Oracle, MySQL, PostgreSQL, SQLite, UNC and ComfyUI tools offer nothing until entries are ticked on `/tools`; SQLite files in the working directory need no ticking.
- **In `profile.json`** the lists are `SqlConnectionsOffered`, `OracleConnectionsOffered`, `MySqlConnectionsOffered`, `PostgresConnectionsOffered`, `SqliteDatabasesOffered`, `UncSharesOffered` and `ComfyWorkflowsOffered`.
- **`delete` follows *File deletes to Recycle bin*** (on by default): the `delete` tool moves files to the Recycle bin (the Trash on a Mac), as in the TUI.
- **Tools that need the screen are left out.** The database write tools (`sqlite_execute`, `sql_execute`, `oracle_execute`, `mysql_execute`, `postgres_execute`), `camera_capture`, `screen_capture`, `screen_list`, `ask_user`, the timer tools and the YouTube playing tools are never offered.
- **Calls that would ask you first are refused:** the model's Docker changes, `print_file` under *Print action policy* `ask`, `neon_advisor` under *Advisor confirm*, and `shopping_clear` with `all`.
  - So are Home Assistant calls outside the safe services under *Home Assistant action policy* `ask`. The service and task changes aren't offered.
- **No Browser window:** a web tool that meets a bot check reports it.
- **On a Mac** ([On a Mac](MAC.md)) the executable is `NeonSidekick`, and `run_command` uses `zsh` unless the profile names another shell. The examples carry over: `echo "Ping." | NeonSidekick --headless --cwd ~/src/myapp`.

## What the output looks like

```
NeonSidekick 0.5.2
Headless mode
LLM: http://127.0.0.1:1234/v1 model=qwen3-30b-a3b (configured)
You: Neon: The capital of France is Paris.
You: Neon: 
[tool] web_search {"query":"…"}
[tool] web_search -> 1. …
Here is what I found…
You:
```

- The first lines are the version, `Headless mode` and the server (or `LLM: no server …` and what to do), then a line on the MCP servers if any are set up.
- `You: ` is printed before each line is read, even from a pipe, so a reply often starts `You: Neon: `.
- `[tool]` lines trace tool calls and their results, which may run over several lines. A conversation's opening calls (the clock while *Clock tools* is on, the working directory, memory) print before `Neon: `.
- `[notice]`, `[warning]`, `[error]` and `[category] message` lines are notices and diagnostics (warnings and errors only; `--log` has the rest). Thinking is never printed.
- Each **line** of input is one message; blank lines are skipped. Ctrl+C ends the run, and a reply cut short ends with `(canceled)`.

## Slash commands in headless mode

### The ones that work

They need no server unless the row says so.

| Command | What it does headless |
|---|---|
| `/exit` | Ends the run (exit code 0), as the end of input does. |
| `/new`, `/clear`, `/splash` | Forget the conversation; the next message starts a new stored session. |
| `/rewind [n]` | Goes back n messages (1 by default) at once and prints the dropped message to send again; what tools changed stays changed. |
| `/compact [focus]` | Shrinks the conversation now. Needs a server. |
| `/plan …` | [Plan mode](COMMANDS.md#plan-mode), approved by typing (below). Needs a server and *LLM offer tools*. |
| `/code-audit …` | [Code audit](COMMANDS.md#code-audit), every pass run without waiting (below). Needs a server, *LLM offer tools* and `read_file`. |
| `/claude <message>` | Asks [Claude Code](COMMANDS.md#claude-code-from-the-chat); the reply follows `Claude: `, then the cost. Nothing is asked: what *Claude Code command permissions* doesn't allow is denied. |
| `/advisor [<name> <question>]` | Lists the advisors, or prints one advisor's answer and adds it to the conversation. |
| `/skills add <source> [--global\|--profile] [--yes]` | [Installs a skill](SETTINGS.md#installing-skills); without `--yes` it only previews, and several matches are listed as ids to type back. |
| `/ha …`, `/docker …` | Drive Home Assistant and Docker directly; *Home Assistant action policy* and *Docker writes* don't apply. |
| `/eventlog …` | Prints a log's events, newest first, with the [event-log tools](TOOLS.md#event-logs)' filter words; full text, ticked computers only. On a Mac: `system`, `crashes`, `install` or a `.logarchive`. |
| `/net …` | Runs a [network](TOOLS.md#network) check and prints it; ignores *Network tools reach*, the host lists and the per-turn budget, with a line when the model's own call would be refused. |
| `/services …`, `/tasks …` | Print a computer's [services](TOOLS.md#services) or [scheduled tasks](TOOLS.md#scheduled-tasks); ticked computers only. No changes: they need an on-screen yes. Windows only. |
| `/ad …` | Prints a card, an OU, a find or the domain from [Active Directory](TOOLS.md#active-directory), in columns; ticked domains only. `to:<file.csv or folder>` saves a list as a CSV in the working directory. |
| `/shopping …` | The [shopping list](TOOLS.md#shopping-list); the bare word prints it, and `clear all` and `store remove` act without asking. |
| `/print …`, `/pdf …` | [Print](TOOLS.md#printing) or [make a PDF](TOOLS.md#making-pdfs) of a file, a web page (`/pdf`) or the last reply, and `/pdf merge\|extract\|delete\|rotate` [edit PDFs](TOOLS.md#editing-pdfs); *Print action policy* doesn't apply. On a Mac, `/pdf` has only the `browser` engine. |
| `/test …` | Runs the [benchmark tests](COMMANDS.md#benchmark-tests), a `[notice]` per test. The listing and `history` need no server; a run does. |
| `/prompts …`, `/browser save\|list\|unsave …` | The saved prompts and saved links: listed, printed, added (`/browser save` needs the address) or removed; `list --clear` empties a list without asking. Other `/browser` forms are errors. |
| `/youtube …` | Searches (needs a *YouTube API key*) and lists playlists and saved videos (bare `/youtube` lists the saved ones); `list --clear` empties the list without asking. Playing a video is an error. |
| `/camera list`, `/screen list` | List the cameras, or the monitors and windows; other forms are errors. On a Mac they need macOS 14 or later. |
| `/avatar …` | Prints or sets the *Model avatar* (`/avatar user …` your *User avatar*); a description picks the closest emoji. |
| `/help <words>` | Asks the model to look the words up in the app's manual (`neon_help`). With no server, or *Help tools* or `neon_help` off, it prints the manual's own answer (after a `[notice]` when a server is there). |
| `/webui …` | An error: the [Web UI](INTERFACE.md#web-ui) runs only with the full-screen app. |

- Case doesn't matter. `/exit`, `/new`, `/clear` and `/splash` must be alone on the line: `/new please` goes to the model as text.
- With no server, messages and the commands that need one get `Neon: [error] No LLM server. Set NEONSIDEKICK_LLM_URL and restart.`
- Automatic compaction (*LLM auto compact (%)*) and the mid-reply guard (*LLM tool compact mode*) run headless too, as `[notice]` lines.

### Plans, gap reviews, audits and Ralph loops

- **Plan mode:** a presented plan is saved as `.neon/plans/<name>.md`, named in a `[notice]`. Then type `/plan approve [--fresh | --ralph [<passes>]]`, `cancel`, `show`, `save [<name>]`, `open [<name>] [--chat]` (alone, it lists the plans), `gaps [model|advisor|any|confirm]`, `chat` or `ready`.
- **`/plan chat`** sends nothing; your next line opens the conversation, and the model writes the plan when you ask. `/plan ready` asks for it, or, once one is presented, prints where it stands.
- **`/plan gaps` and `/code-audit`** run every pass without waiting, each echoed as a `You:` line. A count (`/plan gaps 3`) caps the run; 0 (no limit) means 50, as there's no Esc. Under *Advisor confirm* the model works alone.
- **A gap review** stops as on the screen; a clean pass cut short at the limit names `/plan approve`.
- **An audit** prints each `present_audit` call as any tool's, and at its end the way on: `/code-audit fix <name>` (plan mode on the open findings) and `/code-audit open <name>`. `open`, `fix` and `resume` work as on the screen.
- **`/plan ralph [<name>] [<passes>]`** runs a [Ralph loop](COMMANDS.md#ralph-loop): typing it is the consent. Each pass is sent without waiting, echoed as its short line, its checks and notices as `[notice]` lines. A plan that may not suit a loop gets a `[notice]`, and the loop goes on.
- **A Ralph loop names its checks** in a `[notice]` before running any. Under *Shell command policy* `ask` a check runs only if it's on the allow list, as `run_command`'s would; `NEONSIDEKICK_COMMAND_POLICY=yolo` runs it.
- **A Ralph loop refuses to start** when a check fails on the project first, printing its output. There's no checkpoint and no `ask_user`. Bare `/plan ralph` resumes a loop stopped earlier in the run.

### Everything else goes to the model as text

Any other `/…` line is sent to the model as a message, so `/model gemma` switches nothing. What to use instead:

| Interactive command | Headless equivalent |
|---|---|
| `/server`, `/model`, `/cwd`, `/profile` | `--url`, `--model`, `--cwd`, `--profile` (or their variables). |
| `/reasoning`, `/sampling` | `NEONSIDEKICK_LLM_REASONING`, `NEONSIDEKICK_LLM_SAMPLING`. |
| `/settings`, `//`, `/tools`, `/skills`, `/mcp`, `/profile push\|pull`, `/cmd`, `/msgclear` | Set things up in the TUI, or edit `profile.json` and `mcp.json`. An API key typed there in plain text is encrypted at the next load. |
| `/remember`, `/memory`, `/sessions`, `/imagine`, `/process`, `/tree`, `/vault` | Ask the model: it has the memory, session, ComfyUI, `process`, file and Obsidian tools when they're on. |
| `/loop` | Repeat the line, or loop in the script. |
| `/usage` | `--log` records the run. |
| `/learn`, `/timer`, `/tts`, `/stt`, `/read`, `/echo`, `/sys`, `/keycheck`, `/copy`, `/draft`, `/view`, `/explore`, `/open`, `/browse`, `/terminal`, `/shortcut`, `/theme`, `/mark`, `/perfbar`, `/homebar`, `/toolbar`, `/header`, `/window`, `/expand`, `/collapse`, `/find`, `/splash view\|thumbs\|purge`, `/pasted`, `/queue`, `/rename`, `/help` alone, `/about`, `/log`, `/comfy`, `/gituser`, `/botchat` | Not available: they need the screen, sound, an editor, the clipboard or a confirmation. |

## Flags in headless runs

Each flag lasts one run and outranks its variable and the saved setting. Each example follows `NeonSidekick.exe --headless`, with the job on stdin. [COMMANDLINE.md](COMMANDLINE.md) lists every option.

| Flag | In a headless run | Example |
|---|---|---|
| `--url <url>` | The LLM server: a URL, or `embedded`, `claude-code` or `docker:<container>` (below). Its key goes in `NEONSIDEKICK_LLM_API_KEY`, never on the command line. | `--url http://gpu-box:8080/v1` |
| `--model <id>` | The model; with `--url` it pins both for a reproducible run. | `--url http://127.0.0.1:1234/v1 --model gemma-3-27b-it` |
| `--cwd <path>` | The working directory, where the file, GitLib, shell and download tools work. | `--cwd D:\Repo\MyApp` |
| `--profile <name>` | The profile; with neither the flag nor `NEONSIDEKICK_PROFILE` it is `neon`. An unknown name exits with code 2. | `--profile automation` |
| `--log <path>` | Every diagnostic line to a file, starting with the flags, profile and variables in force; `{ts}` becomes the start time. | `--log C:\Logs\neon_{ts}.log` |
| `--yolo` | Every shell command runs without asking ([below](#running-yolo-every-shell-command-allowed)). | `--yolo --cwd D:\Repo\MyApp` |
| `--no-police` | The path police off ([below](#turning-off-the-path-police)). | `--no-police --cwd D:\Repo\MyApp` |

A profile of its own (`--profile automation`) keeps scripted runs' memory, sessions and allow list out of your everyday one.

Special servers for `--url`:

- **The Anthropic or OpenAI API:** `--url https://api.anthropic.com` (or `https://api.openai.com`) with `NEONSIDEKICK_ANTHROPIC_API=on` and its key (or the `OPENAI` pair); without them the URL is ignored. Requests are billed to the key's account.
- **The embedded model:** `--url embedded`, with `--model` naming an installed model (else the first). A run never downloads a model, but fetches the llama.cpp runtime the first time. On a Mac it runs on Metal.
- **The Claude Code server:** `--url claude-code` with `NEONSIDEKICK_CLAUDE_CODE_SERVER=on` runs your logged-in Claude Code as the model ([Anthropic](SETTINGS.md#anthropic)); `--model` is `fable`, `opus`, `sonnet` (the default) or `haiku`.
- **A Docker server (Windows only):** `--url docker:<container>` stops the profile's other chosen containers and starts this one ([Docker servers](TOOLS.md#docker-servers)). It keeps running after the app exits unless *Docker server stop on exit* is on.

## Running "yolo": every shell command allowed

The run's shell policy comes from `--yolo`, then `NEONSIDEKICK_COMMAND_POLICY` (`off`, `ask` or `yolo`), then the profile's *Shell command policy*. Neither the flag nor the variable is saved.

- `off` (the default): no shell tools.
- `ask`: with no screen to ask on, only commands on the profile's *Shell allowed commands* run.
- `yolo`: every `run_command` command and `execute_code` script runs without asking. Use it only when you trust the job and the folder.
- **`yolo` does not turn off the shell police.** With *Shell police* on (the default), these are still refused and count toward exit code 3:
  - a path outside the working directory;
  - the *Shell police forbidden strings* (disk wipes, shutdown, `push --force`, `reset --hard`…; see [Shell guards](SETTINGS.md#shell-guards));
  - reaching a database some other way while its tools are on.

```powershell
"Run the unit tests and fix any that fail." | NeonSidekick.exe --headless --yolo --cwd D:\Repo\MyApp
NEONSIDEKICK_COMMAND_POLICY=yolo NeonSidekick --headless --cwd ~/src/myapp < job.txt   # bash or zsh: the variable for one command
```

- In PowerShell, `$env:` lasts for the session, so remove the variable afterwards (`try { … } finally { Remove-Item Env:NEONSIDEKICK_COMMAND_POLICY }`).
- `--yolo` works in the TUI too: for that launch the app never asks before running a command, and the toolbar shows 🔓.

### Turning off the path police

- The path police refuses any command, script or `process` write that names a path outside the working directory (`C:\…`, `/…`, `..`, `~`, `$HOME`…).
- `--no-police` or `NEONSIDEKICK_SHELL_POLICE=off` turns it off for a run, with the forbidden strings and the database checks; `on` brings it back over a saved `off`. The flag outranks the variable, and neither is saved.
- For a profile that always runs without it, save *Shell police* off there (`/tools shell police`).

> **Warning:** `--yolo --no-police` together leave no guard at all: the model can run any command against any path, with your account's rights. Use it only for jobs and machines you would trust with that.

**To check it took effect**, run with `--log`: the startup line lists the flag (or `Overrides in force: … NEONSIDEKICK_COMMAND_POLICY=yolo`), and each command is logged as `approval: yolo`.

```powershell
Select-String -Path yolo.log -Pattern "--yolo|COMMAND_POLICY|approval: yolo"
```

### A safer alternative

Stay on `ask` and allow only the prefixes the job needs (`dotnet build`, `git status`…): run the job once in the TUI on the same profile and answer *Allow … always*, or copy a list with `/profile pull allowed <name>`. Exit code 3 then tells you when the job needed more.

## When a command is refused

Under `ask`, a command not on the allow list is not run; nothing waits or crashes. The model is told the command was refused, that it must not work around the refusal, and which prefixes are allowed:

```
[tool] run_command {"command":"npm install"}
[tool] run_command -> Error: the command was not run: there was no screen to ask the user on (...)
```

The run carries on. At the end, if anything was refused, it prints a summary and exits with code **3**:

```
[notice] 2 commands were not run: "npm install", "python script"
```

Shell police refusals count too, under `--yolo` as well. **Exit codes:** **0** nothing was refused, **2** a bad argument or an unknown profile (nothing ran), **3** at least one command was refused.

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --cwd D:\Repo\MyApp | Tee-Object run.txt
if ($LASTEXITCODE -eq 3) { Select-String -Path run.txt -Pattern "commands? (was|were) not run" }
```

## Putting the flags together

A multi-step job: each line is the next message in one conversation. `/new` gives a clean slate, and `/compact` keeps a long job inside the context window.

```powershell
@"
Fetch https://example.com/changelog and list every breaking change since v2.0.
/compact keep only the list of breaking changes
Now check which of those affect files in this folder.
/new
Summarize README.md in five bullets.
"@ | NeonSidekick.exe --headless --cwd D:\Repo\MyApp
```

A fully pinned job:

```powershell
Get-Content D:\Jobs\release_notes.txt |
    NeonSidekick.exe --headless --url http://127.0.0.1:1234/v1 --model qwen3-30b-a3b `
        --profile automation --cwd D:\Repo\MyApp --log D:\Jobs\logs\release_notes_{ts}.log |
    Out-File D:\Jobs\out\release_notes.txt
```

From Python:

```python
import subprocess

proc = subprocess.run(
    ["NeonSidekick.exe", "--headless", "--profile", "automation", "--cwd", r"D:\Repo\MyApp"],
    input="Summarize the open TODOs.\nNow rank them by effort.\n/exit\n", capture_output=True, text=True, encoding="utf-8",
)
replies = [line.split("Neon: ", 1)[1] for line in proc.stdout.splitlines() if "Neon: " in line]
```

For a scheduled job, run a script like these from Task Scheduler (`cron` or `launchd` on a Mac).

## Environment variables that matter in headless runs

Flags beat variables, and variables beat the profile's saved values. [ENVIRONMENT.md](ENVIRONMENT.md) says what each one takes.

- **Profile and home:** `NEONSIDEKICK_PROFILE`, `NEONSIDEKICK_HOME`.
- **Server and model:** `NEONSIDEKICK_LLM_URL`, `NEONSIDEKICK_LLM_MODEL`, `NEONSIDEKICK_LLM_API_KEY`, `NEONSIDEKICK_LLM_REASONING`, `NEONSIDEKICK_LLM_SAMPLING`, `NEONSIDEKICK_LLM_CONTEXT`, `NEONSIDEKICK_LLM_TURN_TIMEOUT` and `NEONSIDEKICK_LLM_REQUEST_TIMEOUT` (raise these two for long jobs).
- **Hosted and embedded models:** `NEONSIDEKICK_ANTHROPIC_API`, `NEONSIDEKICK_ANTHROPIC_API_KEY`, `NEONSIDEKICK_OPENAI_API`, `NEONSIDEKICK_OPENAI_API_KEY`, `NEONSIDEKICK_CLAUDE_CODE_SERVER`, `NEONSIDEKICK_EMBEDDED_BACKEND`, `NEONSIDEKICK_EMBEDDED_CONTEXT`.
- **Shell:** `NEONSIDEKICK_COMMAND_POLICY`, `NEONSIDEKICK_SHELL_POLICE`, `NEONSIDEKICK_SHELL_NATIVE`.
- **Claude Code and advisors:** `NEONSIDEKICK_CLAUDE_CODE_EXE`, `NEONSIDEKICK_CLAUDE_CODE_PERMISSIONS`, `NEONSIDEKICK_ADVISORS`.
- **Integrations:** `NEONSIDEKICK_SEARXNG_URL`, `NEONSIDEKICK_OBSIDIAN_VAULT`, `NEONSIDEKICK_COMFY_URL`, `NEONSIDEKICK_DOCKER_PIPE`, `NEONSIDEKICK_HA_URL`, `NEONSIDEKICK_HA_TOKEN`, `NEONSIDEKICK_YOUTUBE_API_KEY` (a search costs 100 of the key's daily quota units).

## Flags that don't combine with `--headless`

`--smoke`, `--audio-check`, `--voice-check`, `--sql-check`, `--oracle-check`, `--mysql-check`, `--sqlite-check`, `--postgres-check`, `--unc-check`, `--eventlog-check`, `--network-check`, `--services-check`, `--tasks-check`, `--ad-check`, `--docker-check` and `--camera-check` are separate modes; with `--headless`, headless wins and they are ignored. `--help` and `--version` print and exit first.

`--mcp-relay`, `--llama-guard` and `--shell-guard` are the app's own internal modes, not for you to run.
