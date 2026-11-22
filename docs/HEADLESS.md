# Headless mode

How to run Neon Sidekick from scripts with `--headless`. Back to the [README](../README.md).

`NeonSidekick.exe --headless` is a plain stdin/stdout prompt: no TUI, no voice, no timers and nothing that can ask you a question. It answers each line of input on stdout.

It exits at `/exit` or the end of input with code 0, 3 if a shell command or a call `--allow` covers was refused, or 4 if a message or a command failed ([Exit codes](#exit-codes)). `--prompt "…"` asks one question and prints the answer alone. From a checkout: `dotnet run --project src/NeonSidekick -- --headless …`.

```powershell
NeonSidekick.exe --headless                                               # type messages yourself (over SSH, or where the TUI misbehaves)
"What day of the week is it?" | NeonSidekick.exe --headless > answer.txt  # one question, the answer to a file
Get-Content job.txt | NeonSidekick.exe --headless > run_output.txt        # one message per line of job.txt
NeonSidekick.exe --prompt "What day of the week is it?" > answer.txt      # the answer alone, everything else on stderr
Get-Content error.log | NeonSidekick.exe --prompt "Why did this fail?"   # the log goes with the question
```

## Contents

- [Before you start](#before-you-start)
- [What the output looks like](#what-the-output-looks-like)
- [Slash commands in headless mode](#slash-commands-in-headless-mode)
- [Flags in headless runs](#flags-in-headless-runs)
- [Running "yolo": every shell command allowed](#running-yolo-every-shell-command-allowed)
- [When a command is refused](#when-a-command-is-refused)
- [Exit codes](#exit-codes)
- [Putting the flags together](#putting-the-flags-together)
- [Carrying on a session](#carrying-on-a-session)
- [Environment variables that matter in headless runs](#environment-variables-that-matter-in-headless-runs)
- [Flags that don't combine with `--headless`](#flags-that-dont-combine-with---headless)

## Before you start

- **A headless run never checks for updates or installs one.** A run started during an [update](COMMANDS.md#updating) waits for it, with nothing added to stdout.
  - Its start still settles the install folder: it undoes an interrupted install and tidies a leftover download. A finished install's line and a failure's report wait for the next interactive start.
  - An old copy started during or just after an update runs nothing, and says on stderr to start it again ([exit codes](#exit-codes)).
- **A fresh profile does little headless.** The model gets only the clock (*Clock tools*), help (*Help tools*), memory reading (*Memory mode* `read-only`), past-session and skill tools, and no server is looked for (*LLM server scan mode* `off`).
  - Set up a server (or `--url`) and the tool groups the job needs first, in the TUI (`/settings`, `/tools`) or `profile.json`.
- **Tick the "… offered" lists first.** The SQL, Oracle, MySQL, PostgreSQL, SQLite, DuckDB, UNC and ComfyUI tools offer nothing until entries are ticked on `/tools`.
  - SQLite and DuckDB files, and DuckDB's data files, in the working directory need no ticking.
- **In `profile.json`** the lists are `SqlConnectionsOffered`, `OracleConnectionsOffered`, `MySqlConnectionsOffered`, `PostgresConnectionsOffered`, `SqliteDatabasesOffered`, `DuckDbDatabasesOffered`, `UncSharesOffered` and `ComfyWorkflowsOffered`.
- **`delete` follows *File deletes to Recycle bin*** (on by default), moving files to the Recycle bin (the Trash on a Mac) as in the TUI.
- **Tools that need the screen are never offered:** the database write tools (`sqlite_execute`, `duckdb_execute`, `sql_execute`, `oracle_execute`, `mysql_execute`, `postgres_execute`), `camera_capture`, `screen_capture`, `screen_list`, `ask_user`, the timer tools, the YouTube playing tools, the video tools and the monitor tools.
- **Calls that would ask you first are refused** unless `--allow` names them ([below](#flags-in-headless-runs); service and task changes aren't offered):
  - the model's Docker changes, `print_file` under *Print action policy* `ask`, `neon_advisor` under *Advisor confirm*;
  - Home Assistant calls outside the safe services under *Home Assistant action policy* `ask`;
  - MCP tools that would ask, by their server's `"ask"` ([TOOLS.md](TOOLS.md#mcp-servers));
  - `shopping_clear` with `all`, which `--allow` doesn't cover.
- **No Browser window:** a web tool that meets a bot check reports it.
- **The project file** (`NEON.md`, `AGENTS.md`, `CLAUDE.md`) is never changed; the model is told you can make the change.
- **`open` starts only safe types** (documents, pictures, PDFs, audio, video, text, folders), as in the TUI; a program a shell command opened closes when the run exits.
- **On a Mac** the executable is `NeonSidekick` and `run_command`'s shell is [zsh](MAC.md#setup). The examples here are PowerShell; the flags are the same in zsh (a pipe, a job file, the exit-code check, a multi-line job):

```zsh
echo "What day of the week is it?" | NeonSidekick --headless > answer.txt
NeonSidekick --headless --cwd ~/src/myapp < job.txt > run.txt
[[ $? -eq 3 ]] && grep -E "commands? (was|were) not run" run.txt
NeonSidekick --headless --cwd ~/src/myapp <<'EOF'
Summarize README.md in five bullets.
/new
List the TODO comments in src.
EOF
```

## What the output looks like

```
NeonSidekick 0.5.8
Headless mode
LLM: http://127.0.0.1:1234/v1 model=qwen3-30b-a3b (configured)
You: Neon: The capital of France is Paris.
You: Neon: 
[tool] web_search {"query":"…"}
[tool] web_search -> 1. …
Here is what I found…
You:
```

- First the version, `Headless mode` and the server (or `LLM: no server …` and what to do), then a line on the MCP servers if any are set up.
- A dropped MCP server reconnects at its next call, with a `[notice]`; one that doesn't come back stays down until the next start.
- A server that needs a sign-in can't sign in here: sign in from the interactive app first, and headless runs use that sign-in. A call to one still needing it fails the run.
- `mcp_read` reads the servers' resources too; a read asks only for a server whose `ask` is `always`, so `--allow mcp` covers it.
- `You: ` is printed before each line is read, even from a pipe, so a reply often starts `You: Neon: `.
- `[tool]` lines trace tool calls and their results as the model reads them, fences included. A conversation's opening calls (clock, working directory, memory) print before `Neon: `.
- `[notice]`, `[warning]`, `[error]` and `[category] message` lines are notices and diagnostics (warnings and errors only; `--log` has the rest). Thinking and the 📚 sources line are never printed.
- Each **line** of input is one message; blank lines are skipped. Ctrl+C ends the run; a reply cut short ends with `(canceled)`.
- Input is read as UTF-8 (a file with a byte-order mark says its own encoding).
- **`--quiet`** puts only the replies on stdout, a blank line between them, and everything else on stderr:

  ```powershell
  Get-Content job.txt | NeonSidekick.exe --headless --quiet 2> run.log > replies.txt
  ```

### One question, one answer

`--prompt "<text>"` runs one line and exits; it implies `--headless`. stdout gets the reply alone, with no banner, `You: ` or `Neon: `; the rest goes to stderr. The line can be a slash command, and the passes it queues (a gap review, a Ralph loop) still run.

- **Piped stdin is context:** a message gets it under `Piped input:` in a fenced block. It's cut, start and end kept, past half the model's window (100,000 characters with the window unknown), with a `[notice]`.
- **A command takes no piped text:** it's read and left out, with a `[notice]`. A custom command is a message once expanded, so it takes it.
- **A silent pipe:** if nothing comes on stdin within 3 seconds, the prompt runs alone with a `[notice]`. Pass slow input with `--prompt-file`, or skip stdin with `< NUL` (`< /dev/null` on a Mac).
- **`--prompt-file <file>`** sends the whole file as one message, never as a command, even when it starts with `/`. A command goes in `--prompt`.

```powershell
$answer = Get-Content build.log -Raw | NeonSidekick.exe --prompt "Which test failed, and why?" 2> $null
if ($LASTEXITCODE -eq 4) { "No answer: a message or a command failed." }
```

## Slash commands in headless mode

### The ones that work

They need no server unless the row says so.

| Command | What it does headless |
|---|---|
| `/exit` | Ends the run, as the end of input does ([Exit codes](#exit-codes)). |
| `/new`, `/clear`, `/splash` | Forget the conversation; the next message starts a new stored session. |
| `/rewind [n] [files]` | Goes back n messages (1 by default) at once, printing the dropped message to send again; `files` puts back the files those turns changed too. |
| `/undo [n]`, `/undo purge [all]` | Puts back the files the last n turns changed, or deletes the file checkpoints, nothing asked; files changed since are skipped and named. |
| `/compact [focus]` | Shrinks the conversation now. Needs a server. |
| `/plan …` | [Plan mode](COMMANDS.md#plan-mode), approved by typing (below). Needs a server and *LLM offer tools*. |
| `/code-audit …` | [Code audit](COMMANDS.md#code-audit), every pass run without waiting (below). Needs a server, *LLM offer tools* and `read_file`. |
| `/claude <message>` | Asks [Claude Code](COMMANDS.md#claude-code-from-the-chat): `Claude: ` and the reply, then the cost. Nothing is asked; what *Claude Code command permissions* doesn't allow is denied. |
| `/advisor [<name> <question>]` | Lists the advisors, or prints one advisor's answer and adds it to the conversation. |
| `/skills add <source> [--global\|--profile] [--yes]` | [Installs a skill](SETTINGS.md#installing-skills); without `--yes` it only previews, listing several matches as ids to type back. |
| `/ha …`, `/docker …` | Drive Home Assistant and Docker directly; *Home Assistant action policy* and *Docker writes* don't apply. `/ha camera [<name>]` saves the still in `home-cameras` and prints its path. |
| `/eventlog …` | Prints a log's events, newest first, in full text, with the [event-log tools](TOOLS.md#event-logs)' filter words; ticked computers only. A Mac's logs: [`/eventlog`](COMMANDS.md#admin). |
| `/net …` | Prints a [network](TOOLS.md#network) check; ignores *Network tools reach*, the host lists and the per-turn budget, with a line when the model's own call would be refused. |
| `/services …`, `/tasks …` | Print a ticked computer's [services](TOOLS.md#services) or [scheduled tasks](TOOLS.md#scheduled-tasks); no changes (they need an on-screen yes). Windows only. |
| `/monitor list`, `/monitor <monitor> input\|pc\|only\|primary …`, `/monitor extend\|duplicate` | Prints the [monitors](TOOLS.md#monitors), sets this PC's input, or changes an input or the layout without asking. Bare `/monitor` needs the screen. Windows only. |
| `/ad …` | Prints a card, an OU, a find, an account's groups or the domain from [Active Directory](TOOLS.md#active-directory), in columns; ticked domains only. `to:<file.csv or folder>` saves a list as a CSV in the working directory. |
| `/shopping …` | The [shopping list](TOOLS.md#shopping-list); bare, it prints it. `clear all` and `store remove` act without asking. |
| `/print …`, `/pdf …` | [Print](TOOLS.md#printing) or [make a PDF](TOOLS.md#making-pdfs) of a file, a web page (`/pdf`) or the last reply; `/pdf merge\|extract\|delete\|rotate` [edit PDFs](TOOLS.md#editing-pdfs). *Print action policy* doesn't apply. On a Mac, `/pdf` has only the `browser` engine. |
| `/settings export [<path>]`, `/settings import [<zip>]` | [Moves settings](COMMANDS.md#moving-settings-to-another-computer) through a zip in Downloads; bare `import` lists the zips there. |
| `/docx …` | [Makes a Word document](TOOLS.md#writing-office-files) of a Markdown or `.txt` file, or (with a server) of the last reply. |
| `/test …` | Runs the [benchmark tests](COMMANDS.md#benchmark-tests), a `[notice]` per test. A run needs a server; the listing and `history` don't. |
| `/commands`, `/commands new\|save <name> [--global]` | Prints your [custom commands](COMMANDS.md#custom-commands), or writes one and prints its path (no editor). `save` takes the last message you typed, never a custom command's. |
| `/<name> [<words>]` | Sends a custom command's prompt after a `[notice]` naming its folder. The prompt is a message: a body starting `/plan` isn't run as `/plan`. |
| `/prompts …`, `/browser save\|list\|unsave …` | Lists, prints, adds (`/browser save` needs the address) or removes saved prompts and links; `list --clear` empties a list without asking. Other `/browser` forms are errors. |
| `/youtube …` | Searches (needs a *YouTube API key*) and lists playlists and saved videos (bare, the saved ones); `save <id\|link>` saves a video or playlist; `list --clear` empties the list without asking. Playing is an error. |
| `/video …` | An error: video files play only on the screen. |
| `/camera list`, `/screen list` | List the cameras, or the monitors and windows (macOS 14+ on a Mac); other forms are errors. |
| `/avatar …` | Prints or sets the *Model avatar* (`/avatar user …` your *User avatar*); a description picks the closest emoji. |
| `/help <your command>` | Prints the custom command's card, or why a skipped file doesn't run; no server needed. |
| `/help <words>` | Asks the model to look it up in the manual (`neon_help`); with no server, or *Help tools* or `neon_help` off, prints the manual's answer (after a `[notice]` if a server is there). A tool's answer says if headless offers it, and why not. |
| `/webui …` | An error: the [Web UI](INTERFACE.md#web-ui) runs only with the full-screen app. |
| `/midi …` | An error: MIDI buttons are heard only by the full-screen app. |
| `/update …` | An error: [updates](COMMANDS.md#updating) run only in the full-screen app. |

- Case doesn't matter. `/exit`, `/new`, `/clear` and `/splash` must be alone on the line: `/new please` goes to the model as text.
- With no server, messages and the commands that need one get `Neon: [error] No LLM server. Set NEONSIDEKICK_LLM_URL and restart.`
- Automatic compaction (*LLM auto compact (%)*) and the mid-reply guard (*LLM tool compact mode*) run headless too, as `[notice]` lines; a smaller window the server names is a `[notice]` too, held for the run ([Servers](SETTINGS.md#servers)).

### Plans, gap reviews, audits and Ralph loops

- **Plan mode:** a presented plan is saved as `.neon/plans/<name>.md`, named in a `[notice]`. Then type `/plan approve [--fresh | --ralph [<passes>]]`, `cancel`, `show`, `save [<name>]`, `open [<name>] [--chat]` (alone, it lists the plans), `gaps [model|advisor|any|confirm]`, `chat` or `ready`. `/plan template` writes the example plan; `/plan edit` needs the screen.
- **`/plan chat`** sends nothing; your next line opens the talk, and the model writes the plan when you ask. `/plan ready` asks for it or, once one is presented, prints where it stands.
- **`/plan gaps` and `/code-audit`** run every pass without waiting, each echoed as a `You:` line. A count (`/plan gaps 3`) caps the run; 0 (no limit) means 100, as there's no Esc. Under *Advisor confirm* the model works alone, and a Ralph loop's advisor review falls back to the model, unless the run has `--allow advisor`.
  - A server failure is tried once more, and a pass that reports nothing is asked once, as on the screen.
- **A gap review** stops as on the screen but never pauses: no line is read during passes, so `/plan gaps resume` has nothing to resume. A clean pass cut short at the limit names `/plan approve`. Your calls go into `## Risks` as open questions.
- **An audit** prints each `present_audit` call as any tool's, and at its end the way on: `/code-audit fix <name>` (plan mode on the open findings) and `/code-audit open <name>`. `open`, `fix` and `resume` work as on the screen.
- **`/plan ralph [<name>] [<passes>]`** runs a [Ralph loop](COMMANDS.md#ralph-loop); typing it is the consent. Each pass is sent without waiting, echoed as its short line, with `[notice]` lines for its checks.
- **A plan that may not suit a loop** gets a `[notice]`, and the loop goes on. One with no `- [ ]` steps under `## Steps` is refused with an `[error]`.
- **A Ralph loop names its checks** in a `[notice]` before running any. Under *Shell command policy* `ask` a check runs only if it's on the allow list, as `run_command`'s would (on the PC, approving the plan is the yes); `NEONSIDEKICK_COMMAND_POLICY=yolo` runs it.
- **A Ralph loop refuses to start** when its `verify:` check fails on the project first:
  - It prints the check's output. A check whose script isn't there yet is skipped.
  - A failing `final:` check alone gets a `[notice]`, and the loop goes on.
- **No one to ask:** a headless loop has no checkpoint and no `ask_user`.
- **Bare `/plan ralph`** resumes a loop stopped earlier in the run, with the checks it started with.
- **A Ralph loop stops at a `By hand:` step** before checking it, since nobody can be asked: the step stays unticked, named with its check. A resume stops there again, before any check or pass, until you tick its box.

### Everything else goes to the model as text

Any other `/…` line is sent to the model as a message, so `/model gemma` switches nothing. What to use instead:

| Interactive command | Headless equivalent |
|---|---|
| `/server`, `/model`, `/cwd`, `/profile` | `--url`, `--model`, `--cwd`, `--profile` (or their variables). |
| `/reasoning`, `/sampling` | `NEONSIDEKICK_LLM_REASONING`, `NEONSIDEKICK_LLM_SAMPLING`. |
| `/settings` (other forms), `//`, `/tools`, `/skills`, `/mcp`, `/profile push\|pull`, `/cmd`, `/msgclear` | Set things up in the TUI, or edit `profile.json` and `mcp.json` (an API key typed there in plain text is encrypted at the next load). |
| `/sessions <id>` | `--session <id>\|last` ([Carrying on a session](#carrying-on-a-session)). |
| `/memory`, `/sessions`, `/imagine`, `/process`, `/tree`, `/vault` | Ask the model: it has the memory, session, ComfyUI, `process`, file and Obsidian tools when they're on ([which](#before-you-start)). `/sessions export` is an `[error]`: it works on the screen only. |
| `/loop` | Repeat the line, or loop in the script. |
| `/usage` | `--log` records the run. |
| `/learn`, `/timer`, `/tts`, `/stt`, `/read`, `/echo`, `/sys`, `/keycheck`, `/copy`, `/draft`, `/view`, `/explore`, `/open`, `/browse`, `/terminal`, `/shortcut`, `/theme`, `/mark`, `/perfbar`, `/homebar`, `/toolbar`, `/header`, `/window`, `/expand`, `/collapse`, `/find`, `/splash view\|thumbs\|purge`, `/pasted`, `/ha stills`, `/queue`, `/rename`, `/help` alone, `/about`, `/log`, `/comfy`, `/gituser`, `/botchat` | Not available: they need the screen, sound, an editor, the clipboard or a confirmation. |

## Flags in headless runs

Each flag lasts one run and outranks its variable and the saved setting. Each example follows `NeonSidekick.exe --headless`, with the job on stdin. [COMMANDLINE.md](COMMANDLINE.md) lists every option.

| Flag | In a headless run | Example |
|---|---|---|
| `--url <url>` | The LLM server: a URL, or `embedded`, `claude-code` or `docker:<container>` (below). Its key goes in `NEONSIDEKICK_LLM_API_KEY`, never on the command line. | `--url http://gpu-box:8080/v1` |
| `--model <id>` | The model; with `--url`, pins both for a reproducible run. | `--url http://127.0.0.1:1234/v1 --model gemma-3-27b-it` |
| `--cwd <path>` | The working directory, for the file, GitLib, shell and download tools. | `--cwd D:\Repo\MyApp` |
| `--profile <name>` | The profile (else `NEONSIDEKICK_PROFILE`, else `neon`). An unknown name exits with code 2. | `--profile automation` |
| `--import-settings <zip>` | Adds a [settings zip](COMMANDS.md#moving-settings-to-another-computer) before the run; its report goes to stderr. A refused zip exits with code 2; a zip already imported is skipped. | `--import-settings settings.zip` |
| `--log <path>` | Every diagnostic line to a file, first the flags, profile and variables in force; `{ts}` becomes the start time. | `--log C:\Logs\neon_{ts}.log` |
| `--yolo` | Every shell command runs without asking ([below](#running-yolo-every-shell-command-allowed)). | `--yolo --cwd D:\Repo\MyApp` |
| `--no-police` | The path police off ([below](#turning-off-the-path-police)). | `--no-police --cwd D:\Repo\MyApp` |
| `--allow <list>` | Runs the calls that would ask: `docker`, `print`, `ha`, `advisor`, `mcp` (every server) or `mcp:<server>`. Repeats join; each allowed call is logged with `--log`. | `--allow mcp:github,docker` |
| `--prompt "<text>"` | One line, then the end ([One question, one answer](#one-question-one-answer)); implies `--headless`. | `--prompt "Summarize README.md"` |
| `--prompt-file <file>` | As `--prompt`, the file one message; a relative path starts from where you launch. A missing or blank file is exit 2. | `--prompt-file D:\Jobs\ask.md` |
| `--quiet` | Only the replies on stdout; everything else on stderr. | `--quiet 2> run.log` |
| `--session <id>\|last` | Carries on a stored session ([below](#carrying-on-a-session)). An unknown id is exit 2. | `--session last` |

`mcp:<server>` names a server as `mcp.json` spells it; another case matches only when one server fits. A name that matches no server, or several, is exit 2 before anything starts. An entry that changes nothing under the run's settings gets a `[notice]`.

A profile of its own (`--profile automation`) keeps scripted runs' memory, sessions and allow list out of your everyday one.

Special servers for `--url`:

- **The Anthropic or OpenAI API:** `--url https://api.anthropic.com` (or `https://api.openai.com`) with `NEONSIDEKICK_ANTHROPIC_API=on` and its key (or the `OPENAI` pair); without them the URL is ignored. Requests are billed to the key's account.
- **The embedded model:** `--url embedded`, `--model` naming an installed model (else the first). A run never downloads a model, but fetches the llama.cpp runtime the first time. On a Mac it runs on Metal.
  - *Embedded idle unload* doesn't apply, and a crashed server isn't restarted.
- **The Claude Code server:** `--url claude-code` with `NEONSIDEKICK_CLAUDE_CODE_SERVER=on` runs your logged-in Claude Code as the model ([Anthropic](SETTINGS.md#anthropic)); `--model` is `fable`, `opus`, `sonnet` (the default) or `haiku`.
- **A Docker server (Windows only):** `--url docker:<container>` starts that container as the model's server, each stop of another chosen one a `[notice]` ([Docker servers](TOOLS.md#docker-servers)).

## Running "yolo": every shell command allowed

The run's shell policy comes from `--yolo`, then `NEONSIDEKICK_COMMAND_POLICY` (`off`, `ask` or `yolo`), then the profile's *Shell command policy*. Neither the flag nor the variable is saved.

- `off` (the default): no shell tools.
- `ask`: with no screen to ask on, only commands on the profile's *Shell allowed commands* run.
- `yolo`: every `run_command` command and `execute_code` script runs without asking. Use it only when you trust the job and the folder.
- **`yolo` does not turn off the shell police.** With *Shell police* on (the default), these are still refused and count toward exit code 3:
  - a path outside the working directory;
  - `.git`, `.neon`, `.claude`, a `.mcp.json`, the project file, the app's home or a release package's install folder, and deleting or moving a folder holding the install or the home ([TOOLS.md](TOOLS.md));
  - the *Shell police forbidden strings* (disk wipes, shutdown, `push --force`, `reset --hard`…; see [Shell guards](SETTINGS.md#shell-guards));
  - reaching a database some other way while its tools are on.

```powershell
"Run the unit tests and fix any that fail." | NeonSidekick.exe --headless --yolo --cwd D:\Repo\MyApp
```

- In bash or zsh, a variable before the command lasts for that command only:

  ```zsh
  NEONSIDEKICK_COMMAND_POLICY=yolo NeonSidekick --headless --cwd ~/src/myapp < job.txt
  ```

- In PowerShell, `$env:` lasts for the session, so remove the variable afterwards (`try { … } finally { Remove-Item Env:NEONSIDEKICK_COMMAND_POLICY }`).
- `--yolo` works in the TUI too: for that launch the app never asks before running a command, and the toolbar shows 🔓.

### Turning off the path police

- The path police refuses any command, script or `process` write naming a path outside the working directory (`C:\…`, `/…`, `..`, `~`, `$HOME`…).
- `--no-police` or `NEONSIDEKICK_SHELL_POLICE=off` turns it off for a run, with the forbidden strings and the database checks; `on` brings it back over a saved `off`. The flag outranks the variable; neither is saved.
- For a profile that always runs without it, save *Shell police* off there (`/tools shell police`).

> **Warning:** `--yolo --no-police` together leave no guard at all: the model can run any command against any path, with your account's rights. Use it only for jobs and machines you would trust with that.

**To check it took effect**, run with `--log`: the startup line lists the flag (or `Overrides in force: … NEONSIDEKICK_COMMAND_POLICY=yolo`), and each command is logged as `approval: yolo`.

```powershell
Select-String -Path yolo.log -Pattern "--yolo|COMMAND_POLICY|approval: yolo"
```

### A safer alternative

Stay on `ask` and allow only the prefixes the job needs (`dotnet build`, `git status`…). Run the job once in the TUI on the same profile and answer *Allow … always*, or copy a list with `/profile pull allowed <name>`. Exit code 3 then tells you when the job needed more.

## When a command is refused

Under `ask`, a command not on the allow list isn't run; nothing waits or crashes. The model is told it was refused, not to work around it, and which prefixes are allowed:

```
[tool] run_command {"command":"npm install"}
[tool] run_command -> Error: the command was not run: there was no screen to ask the user on (...)
```

The run carries on. At the end, if a command was refused, it prints a summary and exits with **3**:

```
[notice] 2 commands were not run: "npm install", "python script"
```

A call that would ask (MCP, Docker, print, Home Assistant, an advisor) is refused the same way, with a line per kind naming its `--allow`:

```
[notice] 2 MCP calls were refused: github__create_issue, github__push_files (--allow mcp:github allows them)
[notice] 1 Docker change was refused (--allow docker allows it)
```

Shell police refusals count too, under `--yolo` as well.

## Exit codes

- **0:** nothing failed, and nothing `--allow` covers was refused.
- **2:** nothing ran: a bad argument, an unknown profile, an `--allow mcp:<server>` that names no server or several, a missing or blank `--prompt-file`, a `--session` naming no stored session, a settings zip `--import-settings` refused, or an old copy started during or just after an [update](COMMANDS.md#updating).
- **3:** at least one command or call `--allow` covers was refused.
- **4:** a message or a command failed: a turn that failed after its retries, a message with no server, a command's `[error]` line, a plan review, audit or Ralph loop that stopped short, or a call to an MCP server that needs a sign-in. 4 beats 3.

A run of commands alone with no server still exits 0. Before it exits, a failed run prints one line naming what failed:

```
[notice] 2 failed: a message (Model error: …), /ha (nothing named 'Shed' among camera; …)
```

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --cwd D:\Repo\MyApp | Tee-Object run.txt
if ($LASTEXITCODE -eq 3) { Select-String -Path run.txt -Pattern "commands? (was|were) not run" }
```

## Putting the flags together

A multi-step job: each line is the next message in one conversation. `/new` starts clean; `/compact` keeps a long job inside the context window.

```powershell
@"
Fetch https://example.com/changelog and list every breaking change since v2.0.
/compact keep only the list of breaking changes
Now check which of those affect files in this folder.
/new
Summarize README.md in five bullets.
"@ | NeonSidekick.exe --headless --cwd D:\Repo\MyApp
```

A job that may write to GitHub over its MCP server and restart a container, nothing else that asks:

```powershell
Get-Content D:\Jobs\triage.txt | NeonSidekick.exe --headless --profile automation --allow mcp:github,docker
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
    ["NeonSidekick.exe", "--headless", "--quiet", "--profile", "automation", "--cwd", r"D:\Repo\MyApp"],
    input="Summarize the open TODOs.\nNow rank them by effort.\n/exit\n", capture_output=True, text=True, encoding="utf-8",
)
replies = proc.stdout.split("\n\n")   # --quiet: the replies alone, a blank line between them
```

For a scheduled job, run a script like these from Task Scheduler (`cron` or `launchd` on a Mac).

## Carrying on a session

`--session <id>` carries on a stored session: its conversation, plan and Claude threads come back, and the run's turns join its row. `--session last` takes the one updated most recently, by any copy of the app, a bot chat included.

- **Its folder:** without `--cwd`, the run works in the session's folder, for this run only; a folder that's gone gets a `[notice]`.
- **Its id:** every run that wrote a session ends with `[notice] session #12` on stderr. Pass that id rather than `last` when another copy may be writing.
- With *Session logging* off, the session still loads, but the run's turns aren't saved to it.

```powershell
NeonSidekick.exe --prompt "Read src\Parser.cs and list its public methods." --cwd D:\Repo\MyApp
NeonSidekick.exe --prompt "Which of those have no tests?" --session last
```

## Environment variables that matter in headless runs

Flags beat variables, and variables beat the profile's saved values. [ENVIRONMENT.md](ENVIRONMENT.md) says what each one takes.

- **Profile and home:** `NEONSIDEKICK_PROFILE`, `NEONSIDEKICK_HOME`.
- **Server and model:** `NEONSIDEKICK_LLM_URL`, `NEONSIDEKICK_LLM_MODEL`, `NEONSIDEKICK_LLM_API_KEY`, `NEONSIDEKICK_LLM_REASONING`, `NEONSIDEKICK_LLM_SAMPLING`, `NEONSIDEKICK_LLM_CONTEXT`, `NEONSIDEKICK_LLM_TURN_TIMEOUT` and `NEONSIDEKICK_LLM_REQUEST_TIMEOUT` (raise these two for long jobs).
- **Hosted and embedded models:** `NEONSIDEKICK_ANTHROPIC_API`, `NEONSIDEKICK_ANTHROPIC_API_KEY`, `NEONSIDEKICK_OPENAI_API`, `NEONSIDEKICK_OPENAI_API_KEY`, `NEONSIDEKICK_CLAUDE_CODE_SERVER`, `NEONSIDEKICK_EMBEDDED_BACKEND`, `NEONSIDEKICK_EMBEDDED_CONTEXT`.
- **Shell:** `NEONSIDEKICK_COMMAND_POLICY`, `NEONSIDEKICK_SHELL_POLICE`, `NEONSIDEKICK_SHELL_NATIVE`.
- **Claude Code and advisors:** `NEONSIDEKICK_CLAUDE_CODE_EXE`, `NEONSIDEKICK_CLAUDE_CODE_PERMISSIONS`, `NEONSIDEKICK_ADVISORS`.
- **Integrations:** `NEONSIDEKICK_SEARXNG_URL`, `NEONSIDEKICK_OBSIDIAN_VAULT`, `NEONSIDEKICK_COMFY_URL`, `NEONSIDEKICK_DOCKER_PIPE`, `NEONSIDEKICK_HA_URL`, `NEONSIDEKICK_HA_TOKEN`, `NEONSIDEKICK_YOUTUBE_API_KEY` (a search costs 100 of the key's daily quota units), `NEONSIDEKICK_BRAVE_API_KEY`.

## Flags that don't combine with `--headless`

`--smoke`, `--audio-check`, `--voice-check`, `--sql-check`, `--oracle-check`, `--mysql-check`, `--sqlite-check`, `--duckdb-check`, `--postgres-check`, `--unc-check`, `--eventlog-check`, `--network-check`, `--services-check`, `--tasks-check`, `--ad-check`, `--docker-check`, `--camera-check`, `--monitor-check` and `--midi-check` are separate modes; with `--headless` or `--prompt`, headless wins and they're ignored. `--help` and `--version` print and exit first.

`--mcp-relay`, `--llama-guard`, `--shell-guard` and `--update-probe` are the app's internal modes, not for you to run.
