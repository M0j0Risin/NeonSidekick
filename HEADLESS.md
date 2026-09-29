# Headless examples

`NeonSidekick.exe --headless` runs Neon as a plain stdin/stdout REPL: no TUI, no voice, no timers,
no approval pane. It reads one line at a time from stdin, answers each on stdout, and exits
at `/exit` or at the end of input: code 0, or 3 if a shell command was refused along the way
(see [When a command is refused](#when-a-command-is-refused)). Everything below assumes the published exe
(`publish\output\NeonSidekick.exe`) is on `PATH`; from a checkout, swap in
`dotnet run --project src\NeonSidekick -- --headless …`.

## What the output looks like

```
NeonSidekick 0.3.4
Headless mode. Type a message; /clear or /new forgets the conversation; /compact [focus] shrinks it; /plan <requirement> plans before doing (/plan approve [--fresh] | cancel | show | save [name] | open [name]); /skills add <source> [--global] [--yes] installs a skill; /claude <message> asks Claude Code; /exit or EOF exits.
LLM: http://127.0.0.1:1234/v1 model=qwen3-30b-a3b (configured)
You: Neon: Here is what I found…
[tool] web_search {"query":"…"}
[tool] web_search -> 1. …
[notice] …
You:
```

- `You: ` is printed before each line is read, even when stdin is piped, so a reply line in a log
  starts `You: Neon: `.
- `[tool] name {args}` / `[tool] name -> result` trace the model's tool calls; `[notice]`, `[error]`
  and `[category] message` lines are notices and diagnostics (warnings and errors only; use `--log`
  for the rest).
- Each **line** of input is one message. A multi-line prompt has to be joined into one line.
- Headless understands only nine slash commands; any other `/…` line goes to the model as ordinary
  text. See the next section.

---

## Slash commands in headless mode

### The ones that work

| Command | What it does headless |
|---|---|
| `/exit` | Ends the run (exit code 0), as the end of input does. Works even with no LLM server. |
| `/new` | Forgets the conversation. The next message starts a new stored session. Prints `Neon: ` and the new-conversation notice. |
| `/clear` | Same as `/new` (there is no screen to clear). Prints `Neon: (conversation cleared)`. |
| `/splash` | Same as `/clear` (there is no splash picture to show). |
| `/compact [focus]` | Shrinks the conversation now; a focus steers the summary. Prints the outcome, plus `[notice]` detail lines. Needs a connected server. |
| `/plan <requirement>` | Starts plan mode and sends the requirement: the model gets only the read-only tools and `present_plan`. There is no approval pane, so a presented plan is saved as `.neon/plans/<name>.md` under the working directory and a `[notice]` line names it once the reply ends. While planning: `/plan` or `/plan show` prints where it stands; `/plan approve` marks the file approved and sends the turn that carries it out, with every tool; `/plan approve --fresh` forgets the conversation first and sends the plan's text with that message; `/plan cancel` leaves (the file is kept, marked cancelled); any other `/plan <text>` is sent as more detail. A planning reply that looks like a plan but was never presented gets a `[notice]` hint, and `/plan save [name]` saves it (approve with `/plan approve`). After approval, a `[notice]` line says when every step is ticked (the file marked `done`) or how many are left (`incomplete`, with `progress: d/t`). `/plan open <name>` picks up a plan under `.neon/plans/` and sends a turn asking the model to read it; `/plan open` alone lists them. `/new` and `/clear` leave plan mode. A misused word prints `Neon: [error] …`. Needs *LLM offer tools* on and a connected server. |
| `/claude <message>` | Sends the message to Claude Code, as in the TUI (see the README's `/claude`). The reply streams after `Claude: `; each tool Claude uses is a `[tool] Claude › Read …` line; a `[notice]` says which tools *Claude slash command permissions* denied, and a last `[notice]` gives the cost and tokens. The exchange joins the conversation, so the next message to the local model can build on it, and the next `/claude` resumes the same Claude conversation. `/claude new` starts another (so do `/new` and `/clear`). Nothing is ever asked: whatever the level does not allow is denied (`NEONSIDEKICK_CLAUDE_PERMISSIONS=edit` for a run that may edit files). A missing CLI or a failed run prints `[error] …`. Works with no LLM server. |
| `/ha [on\|off\|toggle <name> [n%] \| scene <name> \| tv … \| states [filter] \| say <sentence>]` | Drives Home Assistant directly, as in the TUI (see the README's `/ha`): each line of the answer is printed as it is (`light.turn_on → Den · 40%`, the overview's lines), a failure as `[error] …`. It is your own command, so *Home Assistant action policy* never applies. Works with no LLM server; needs *Home Assistant URL* and *Home Assistant API key* (or the two variables below). |
| `/print <file> [printer=<name>] [copies=N] [pages=1-3] [landscape] \| reply \| printers` | Prints a file of the working directory, the last reply, or lists the printers, as in the TUI (see the README's *Printing*): the answer's lines are printed as they are (`Printed notes.md: 2 pages to Office Laser`), a failure as `[error] …`. It is your own command, so *Print action policy* never applies (the model's `print_file` is refused headless under `ask`). Works with no LLM server. |
| `/test [id \| reasoning \| structured \| long \| all \| history]` | Runs the benchmark tests against the connected model, as in the TUI (see the README's *Benchmark tests*): a `[notice]` line per test as it finishes (a failure's answer on a second `[notice]` line), then the results table as markdown after `Neon: `. The run is saved in the profile's `tests.json`. Alone it prints the tests with their last verdicts for the connected model; `history` prints the saved runs. An unknown name prints `[error] …`. The listing and `history` work with no LLM server; a run needs one. |
| `/skills add <source> [--global \| --profile] [--yes]` | Installs an Agent Skill, as in the TUI (see the README's *Installing skills*). The source is search words (skills.sh), `owner/repo`, `owner/repo/skill`, a github.com link or an https `.zip` link. A GitHub repository is listed through the GitHub API and only the needed files fetched (the whole zip only as a fallback), so large repositories work; a repository of more than 100 skills needs one named. Several search hits, or several skills in one repository, are printed as `[notice]` lines of ids to type back (`/skills add anthropics/skills/pdf`). One skill is previewed as plain lines. Without `--yes` nothing is written and a `[notice]` says so; with it the skill goes to the profile's skills, or the global ones with `--global`, and a `[notice]` names the folder. Reinstalling from the same source updates it where it is. Errors print as `[error] …`. Works with no LLM server. |

Automatic compaction also runs headless: before a message, if the last reply used more of the
context than the *LLM auto compact (%)* setting allows, the conversation is compacted first and
`[notice]` lines say so. The mid-reply guard (*LLM tool compact type*) runs too: a prune or a
summary during a reply is a `[notice]` line of its own.

Rules for these commands:
- Case doesn't matter (`/EXIT` works). Spaces around the line are ignored.
- `/exit`, `/new`, `/clear` and `/splash` must be alone on the line. `/exit now` or `/new please` is not
  recognised, so it goes to the model as a message.
- With no server connected, `/exit`, `/new`, `/clear`, `/splash`, `/claude`, `/skills add`, `/print` and `/test`'s listing and `history` still work. `/compact`, `/plan` and every
  message get the "no assistant" reply instead.

### Everything else goes to the model as text

No other command runs. Headless has no command parser beyond the ones above, so a line like
`/model gemma` or `/tts on` is sent to the model as a message. The model sees the text and may
answer about it or try to help with its tools, but nothing is switched. Avoid them in scripts.

What to use instead:

| Interactive command | Headless equivalent |
|---|---|
| `/server [url]` | `--url <url>` or `NEONSIDEKICK_LLM_URL` |
| `/model [id]` | `--model <id>` or `NEONSIDEKICK_LLM_MODEL` |
| `/reasoning [level]` | `NEONSIDEKICK_LLM_REASONING` |
| `/sampling [field value]` | `NEONSIDEKICK_LLM_SAMPLING`; the profile's saved sampling for the model applies too. |
| `/cwd [path]` | `--cwd <path>` |
| `/profile [name]` | `--profile <name>` or `NEONSIDEKICK_PROFILE`; with neither, `default`. `add`/`delete`/`rename`/`reset`/`push`/`pull` need the TUI, or the files under `<home>\profiles`. |
| `/settings`, `//`, `/tools`, `/skills` (the pane; `/skills add` works headless), `/mcp` | Set things up in the TUI beforehand, or edit the profile's `profile.json` / `mcp.json`. A key typed into `profile.json` in plain text (LLM, Claude or Home Assistant API key) is encrypted (DPAPI) the next time the profile loads. Environment variables override some values for one run. |
| `/remember <text>` | Ask in a message ("Remember that …"); the model has the `save_memory` tool. |
| `/memory` | Ask the model to recall; to prune or edit, use the TUI or edit `memory.json`. |
| `/sessions` | Ask the model to search past sessions (the sessions tool is offered when *Session tool* is on). Restoring a session needs the TUI. |
| `/imagine …` | Ask the model to make the picture (`generate_image` is offered when ComfyUI is set up). The prompt is then the model's, not sent word for word. |
| `/loop …` | Repeat the line in the input, or loop in the calling script. |
| `/learn` | Not available; the TUI's reflection runs it. |
| `/timer` | Not available; there are no timers headless (nothing could deliver the alert). |
| `/tts`, `/stt`, `/wake`, `/interrupt`, `/speak`, `/echo` | Not available; headless never speaks or listens. |
| `/cmdlist`, `/cmdcopy`, `/cmdclear`, `/police` | Not available. Manage the allow list in the TUI; `NEONSIDEKICK_COMMAND_POLICY` sets the policy for a run. |
| `/keycopy` | Not available; copy the keys in the TUI. `NEONSIDEKICK_LLM_API_KEY`, `NEONSIDEKICK_CLAUDE_API_KEY` and `NEONSIDEKICK_HA_TOKEN` set them for a run. |
| `/usage` | Not available; `--log` records the run. |
| `/sys` | Not available; open `/sys` in the TUI on the same profile to see the prompt. |
| `/copy`, `/draft`, `/view`, `/tree`, `/vault`, `/explore`, `/theme`, `/window`, `/expand`, `/collapse`, `/queue`, `/help`, `/about`, `/log`, `/comfy`, `/gituser`, `/emptytrash`, `/botchat`, `/persona`, `/operata`, `/vocalia` | Not available. They depend on the screen, an editor, the clipboard or a confirmation, or are TUI-only tasks. For `/tree` or `/vault`, ask the model to list the folder with its file or Obsidian tools. |

---

## `--headless` on its own

Interactive in a plain console (useful over SSH, or wherever the TUI misbehaves):

```powershell
NeonSidekick.exe --headless
```

One question, answer to a file:

```powershell
"What is today's date, and what day of the week is it?" | NeonSidekick.exe --headless > answer.txt
```

A multi-step job: each line is the next message in the same conversation.

```powershell
@"
Read CHANGELOG.md and list the entries added this month.
Group them by area and write the result to monthly_summary.md.
/exit
"@ | NeonSidekick.exe --headless
```

From a text file (`job.txt`, one message per line):

```powershell
Get-Content job.txt | NeonSidekick.exe --headless | Tee-Object run_output.txt
```

```cmd
NeonSidekick.exe --headless < job.txt > run_output.txt
```

Two unrelated tasks in one run, with a clean slate between them:

```powershell
@"
Summarize README.md in five bullets.
/new
Draft a commit message for the files changed today (use the git tools).
"@ | NeonSidekick.exe --headless
```

Keeping a long job inside the context window: `/compact` with a focus, mid-script:

```powershell
@"
Fetch https://example.com/changelog and list every breaking change since v2.0.
/compact keep only the list of breaking changes
Now check which of those affect files in this folder.
"@ | NeonSidekick.exe --headless
```

---

## `--url <url>`: which LLM server

Outranks `NEONSIDEKICK_LLM_URL` and the profile's saved URL. `--url=<url>` works too.

```powershell
"Say hello in French." | NeonSidekick.exe --headless --url http://127.0.0.1:1234/v1
```

A server on another machine on the LAN:

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --url=http://gpu-box:8080/v1
```

Same thing through the environment (handy in a wrapper script that runs several jobs):

```powershell
$env:NEONSIDEKICK_LLM_URL = "http://gpu-box:8080/v1"
Get-Content job1.txt | NeonSidekick.exe --headless
Get-Content job2.txt | NeonSidekick.exe --headless
```

A server that needs a key: the key only goes in the environment, never on the command line.

```powershell
$env:NEONSIDEKICK_LLM_API_KEY = (Get-Secret NeonLlmKey -AsPlainText)
"Ping." | NeonSidekick.exe --headless --url https://llm.internal.example/v1
```

The Claude API: switch it on and give it its key for the run, then point `--url` at it. Name the model, or the run takes the first one the account lists. Without the switch and the key, the URL is ignored and the run looks for a local server instead (a warning says so).

```powershell
$env:NEONSIDEKICK_CLAUDE_API = "on"
$env:NEONSIDEKICK_CLAUDE_API_KEY = (Get-Secret AnthropicKey -AsPlainText)
"Summarise this repo's README." | NeonSidekick.exe --headless --url https://api.anthropic.com --model claude-sonnet-5
```

---

## `--model <id>`: which model on that server

Outranks `NEONSIDEKICK_LLM_MODEL` and the saved model.

```powershell
"Explain the difference between a mutex and a semaphore." | NeonSidekick.exe --headless --model qwen3-30b-a3b
```

With `--url`, to pin both for a reproducible run:

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --url http://127.0.0.1:1234/v1 --model gemma-3-27b-it
```

Comparing models on the same prompt:

```powershell
$prompt = "Write a PowerShell one-liner that lists the 10 largest files under the current folder."
foreach ($m in "qwen3-30b-a3b", "gemma-3-27b-it", "mistral-small-3.2") {
    "=== $m" | Out-File compare.txt -Append
    $prompt | NeonSidekick.exe --headless --model $m | Out-File compare.txt -Append
}
```

---

## `--cwd <path>`: the working directory (the file sandbox)

The file, git, shell and download tools all work inside this folder. A relative path resolves
against the directory you launch from. Outranks the saved *Working directory* setting.

Point it at a project:

```powershell
"List the TODO comments in this repo, grouped by file." | NeonSidekick.exe --headless --cwd D:\Repo\MyApp
```

The current folder (the usual case in a script that `cd`s first):

```powershell
Set-Location D:\Repo\MyApp
"What changed since the last commit? Summarize the diff." | NeonSidekick.exe --headless --cwd .
```

The same job over several repos:

```powershell
foreach ($repo in Get-ChildItem D:\Repo -Directory) {
    "Write a three-line status of this repo (branch, last commit, uncommitted files) to STATUS.md." |
        NeonSidekick.exe --headless --cwd $repo.FullName
}
```

A folder of documents rather than code:

```powershell
"Read every .txt file here and write a one-paragraph summary of each to summaries.md." |
    NeonSidekick.exe --headless --cwd "D:\Scans\2026-09"
```

---

## `--profile <name>`: which profile (settings, memory, sessions, allow list)

Without `--profile` or `NEONSIDEKICK_PROFILE`, a headless run loads `default` — never whichever
profile the TUI last switched to.

Loads the named profile for this launch only; `settings.json` is left as it is, so the next
normal launch opens whatever it opened before. Outranks `NEONSIDEKICK_PROFILE`. An unknown name
prints the profiles that exist and exits with code **2**. A temporary profile (`_test`) loads
when named.

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --profile work
```

Keeping scripted runs out of your everyday profile (their memory, sessions and allow-list
entries land in `automation`):

```powershell
"Check https://status.example.com and tell me if anything is degraded." |
    NeonSidekick.exe --headless --profile automation
```

Trying something in a throwaway profile:

```powershell
"Remember that my favourite editor is Helix." | NeonSidekick.exe --headless --profile _test
```

Via the environment, for a wrapper that runs many jobs:

```powershell
$env:NEONSIDEKICK_PROFILE = "automation"
Get-Content morning.txt | NeonSidekick.exe --headless
Get-Content evening.txt | NeonSidekick.exe --headless
```

Failing fast on a typo:

```powershell
"hello" | NeonSidekick.exe --headless --profile wrok
if ($LASTEXITCODE -eq 2) { Write-Error "No such profile" }
# Profile "wrok" does not exist. Profiles: default, automation, work.
```

---

## `--log <path>`: every diagnostic line to a file

Appends every diagnostic line (Trace and up) to the file; stdout still shows only warnings and
errors. The first lines record the mode, the flags, the profile and the environment overrides in
force. A path that can't be opened is reported once and the run carries on without it.

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --log C:\Logs\neon_job.log
```

A dated log per run:

```powershell
$log = "C:\Logs\neon_{0:yyyy-MM-dd_HHmm}.log" -f (Get-Date)
Get-Content job.txt | NeonSidekick.exe --headless --log $log > "$log.out"
```

Debugging a run that "did nothing": look for the connection line and the tool calls.

```powershell
"Fetch https://example.com and summarize it." | NeonSidekick.exe --headless --log debug.log
Select-String -Path debug.log -Pattern "Startup|Llm|Web|error"
```

---

## Running "yolo": every shell command allowed

The shell command policy for a run comes from, in order: the `--yolo` flag, the
`NEONSIDEKICK_COMMAND_POLICY` environment variable (`off`, `ask` or `yolo`, any case), then the
profile's saved *Shell command policy*. Neither the flag nor the variable is ever saved. What each
policy means headless:

- `ask` (the default): nothing can ask, so only commands whose prefixes are on the profile's
  *Shell allowed commands* list run. See [When a command is refused](#when-a-command-is-refused) below.
- `yolo`: every `run_command` command and `execute_code` script runs without asking.
- `off`: the shell tools aren't offered at all.

`yolo` does **not** switch off the path police. With *Shell police outside paths* on (the default),
a command that names a path outside the working directory is still refused, and the run still ends
with exit code 3. Keep `--cwd` narrow, or see [Turning off the path police](#turning-off-the-path-police).

### With the flag

The simplest form; it applies to this launch only and works the same in every shell:

```powershell
"Run the unit tests and fix any that fail." | NeonSidekick.exe --headless --yolo --cwd D:\Repo\MyApp
```

```cmd
NeonSidekick.exe --headless --yolo --cwd D:\Repo\MyApp < job.txt
```

With the other flags:

```powershell
Get-Content job.txt |
    NeonSidekick.exe --headless --yolo --profile automation --cwd D:\Repo\MyApp --log yolo.log
```

It outranks the variable, so a wrapper that exports `NEONSIDEKICK_COMMAND_POLICY=ask` can still
open up one run:

```powershell
$env:NEONSIDEKICK_COMMAND_POLICY = "ask"
Get-Content safe_job.txt  | NeonSidekick.exe --headless            # allow list only
Get-Content build_job.txt | NeonSidekick.exe --headless --yolo     # everything, this run only
```

From Python:

```python
import subprocess

subprocess.run(
    ["NeonSidekick.exe", "--headless", "--yolo", "--cwd", r"D:\Repo\MyApp", "--log", "yolo.log"],
    input="Build the solution and report any warnings.\n", text=True, encoding="utf-8",
)
```

`--yolo` isn't headless-only: `NeonSidekick.exe --yolo` opens the TUI with no approval pane for this
launch. The toolbar shows 🔓, and the *Shell command policy* row names `--yolo` as its override.

### With the environment variable

Useful when a wrapper sets the policy once for many runs, or when you can't change the command line.
Set it for a single command, so it doesn't stay set for the rest of your session:

**Git Bash / WSL-style shells.** The prefix form applies to that one command only:

```bash
NEONSIDEKICK_COMMAND_POLICY=yolo NeonSidekick.exe --headless --cwd /d/Repo/MyApp < job.txt
```

**PowerShell.** `$env:` changes persist in the session, so set it and remove it in `try`/`finally`:

```powershell
$env:NEONSIDEKICK_COMMAND_POLICY = "yolo"
try {
    "Run the unit tests and fix any that fail." | NeonSidekick.exe --headless --cwd D:\Repo\MyApp
}
finally {
    Remove-Item Env:NEONSIDEKICK_COMMAND_POLICY
}
```

In a child PowerShell, which ends with the run, so nothing needs cleaning up:

```powershell
pwsh -NoProfile -Command '$env:NEONSIDEKICK_COMMAND_POLICY = "yolo"; Get-Content job.txt | NeonSidekick.exe --headless --cwd D:\Repo\MyApp'
```

**cmd.exe.** Inside `setlocal`/`endlocal` (in a `.cmd` file), or in a child `cmd /c`:

```cmd
setlocal
set NEONSIDEKICK_COMMAND_POLICY=yolo
NeonSidekick.exe --headless --cwd D:\Repo\MyApp < job.txt
endlocal
```

```cmd
cmd /c "set NEONSIDEKICK_COMMAND_POLICY=yolo&& NeonSidekick.exe --headless --cwd D:\Repo\MyApp < job.txt"
```

**Python**, setting it only in the child's environment:

```python
import os, subprocess

env = {**os.environ, "NEONSIDEKICK_COMMAND_POLICY": "yolo"}
subprocess.run(
    ["NeonSidekick.exe", "--headless", "--cwd", r"D:\Repo\MyApp", "--log", "yolo.log"],
    input="Build the solution and report any warnings.\n", text=True, encoding="utf-8", env=env,
)
```

**A scheduled job** (the variable lives only in the script's process):

```powershell
# D:\Jobs\nightly_build.ps1
$env:NEONSIDEKICK_COMMAND_POLICY = "yolo"
"Run dotnet build and dotnet test, and write the results to build_report.md." |
    NeonSidekick.exe --headless --profile automation --cwd D:\Repo\MyApp --log D:\Jobs\logs\nightly.log
exit $LASTEXITCODE
```

### Turning off the path police

The path police refuses any command, script or `process` write that names a path outside the
working directory (`C:\…`, a `..` that climbs out, `~`, `%USERPROFILE%`, `$env:TEMP`…). It's a
separate switch from the command policy, so `--yolo` leaves it on. Its setting for a run comes
from, in order: the `--no-police` flag, the `NEONSIDEKICK_SHELL_POLICE` variable (`on`/`off`, also
`true`/`false`, `1`/`0`, `yes`/`no`), then the profile's saved *Shell police outside paths*.
Neither the flag nor the variable is saved.

> **Warning:** `--yolo --no-police` together leave no guard at all. The model can run any command
> against any path on the machine, with your account's rights. Use it only for jobs and machines
> you'd trust with that.

Allow-listed commands only, but free to reach outside the working directory (for example, a
build that writes to a shared output folder):

```powershell
"Build the solution and copy the output to D:\Drops\MyApp." |
    NeonSidekick.exe --headless --no-police --cwd D:\Repo\MyApp
```

Everything allowed, anywhere:

```powershell
"Clear the NuGet caches and rebuild." | NeonSidekick.exe --headless --yolo --no-police --cwd D:\Repo\MyApp --log loose.log
```

With the variable, for a wrapper that runs many jobs:

```powershell
$env:NEONSIDEKICK_SHELL_POLICE = "off"
try {
    Get-Content job1.txt | NeonSidekick.exe --headless --cwd D:\Repo\MyApp
    Get-Content job2.txt | NeonSidekick.exe --headless --cwd D:\Repo\Other
}
finally {
    Remove-Item Env:NEONSIDEKICK_SHELL_POLICE
}
```

```bash
NEONSIDEKICK_SHELL_POLICE=off NeonSidekick.exe --headless --cwd /d/Repo/MyApp < job.txt
```

The variable works both ways: `NEONSIDEKICK_SHELL_POLICE=on` turns the police back on for a run
on a profile that saved it off.

A third option, if a profile should always run without it: save *Shell police outside paths* off
in that profile (`/police` in the TUI), and pick it with `--profile`. Your everyday profile keeps
the police on.

### Checking that it took effect

With `--log`, the startup line lists `flags … --yolo` (or `Overrides in force: …
NEONSIDEKICK_COMMAND_POLICY=yolo` for the variable), and every command the model runs is logged
as approved by `yolo`:

```powershell
Select-String -Path yolo.log -Pattern "--yolo|COMMAND_POLICY|approval: yolo"
```

### A safer alternative

Stay on `ask` and allow-list just the prefixes the job needs (`dotnet build`, `dotnet test`,
`git status`…). To build the list, run the job once in the TUI on the same profile and answer
*Allow … always* on the approval pane. Or copy another profile's list over with `/cmdcopy`, and
trim it with `/cmdlist`. The headless run then executes those commands and refuses anything else,
and exit code 3 tells you if the job needed something the list doesn't cover.

---

## When a command is refused

Under `ask`, a command that isn't on the allow list is not run. Nothing waits and nothing crashes.
The model gets this as the tool's result:

```
Error: the command was not approved: no screen to ask on (Shell command policy is ask; --yolo,
NEONSIDEKICK_COMMAND_POLICY=yolo or the profile's Shell allowed commands would let it run);
allowed prefixes: dotnet build, git status; do not retry it or work around the refusal: tell the
user what could not run
```

The model is told not to try another way. It still sees the allowed prefixes, so it can use one
that really does the job. On stdout you see the call and the refusal:

```
[tool] run_command {"command":"npm install"}
[tool] run_command -> Error: the command was not approved: no screen to ask on (...)
```

The run keeps reading lines. At the end, if anything was refused, it prints a summary notice and
exits with code **3**:

```
[notice] 2 commands were not run: "npm install", "python script"
```

- Each command is listed once. A refused `execute_code` script shows as `python script`,
  `powershell script` and so on.
- Path-police refusals count too (a command naming a path outside `--cwd`), under `--yolo` as well.
- Exit codes: **0** nothing was refused, **2** bad argument or unknown profile (nothing ran),
  **3** at least one command was refused.

Acting on it in PowerShell:

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --cwd D:\Repo\MyApp | Tee-Object run.txt
switch ($LASTEXITCODE) {
    0 { "Done." }
    2 { Write-Error "Bad arguments or profile." }
    3 {
        Write-Warning "Some commands were not allowed:"
        Select-String -Path run.txt -Pattern "^\[notice\] \d+ commands? (was|were) not run" | ForEach-Object Line
    }
}
```

Retry with `--yolo` only when the job was refused:

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --cwd D:\Repo\MyApp
if ($LASTEXITCODE -eq 3) {
    Get-Content job.txt | NeonSidekick.exe --headless --yolo --cwd D:\Repo\MyApp
}
```

In cmd:

```cmd
NeonSidekick.exe --headless --cwd D:\Repo\MyApp < job.txt
if %ERRORLEVEL%==3 echo Some commands were not allowed; see the [notice] line above.
```

---

## Putting the flags together

A fully pinned job: server, model, profile, folder and log all named.

```powershell
Get-Content D:\Jobs\release_notes.txt |
    NeonSidekick.exe --headless `
        --url http://127.0.0.1:1234/v1 `
        --model qwen3-30b-a3b `
        --profile automation `
        --cwd D:\Repo\MyApp `
        --log D:\Jobs\logs\release_notes.log |
    Out-File D:\Jobs\out\release_notes.txt
```

A Task Scheduler job (every weekday at 07:30). Save as `D:\Jobs\morning.ps1`:

```powershell
$env:NEONSIDEKICK_COMMAND_POLICY = "ask"          # only allow-listed commands run unattended
$log = "D:\Jobs\logs\morning_{0:yyyy-MM-dd}.log" -f (Get-Date)
@"
Search the web for news about .NET 10 from the last 24 hours and list the top five with links.
Append that list, under today's date as a heading, to news.md.
"@ | NeonSidekick.exe --headless --profile automation --cwd D:\Jobs\notes --log $log
exit $LASTEXITCODE
```

```powershell
$action  = New-ScheduledTaskAction -Execute "pwsh.exe" -Argument "-NoProfile -File D:\Jobs\morning.ps1"
$trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Monday,Tuesday,Wednesday,Thursday,Friday -At 7:30
Register-ScheduledTask -TaskName "Neon morning news" -Action $action -Trigger $trigger
```

Driving it from another program (Python), one message per line:

```python
import subprocess

proc = subprocess.Popen(
    ["NeonSidekick.exe", "--headless", "--profile", "automation", "--cwd", r"D:\Repo\MyApp"],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8",
)
out, _ = proc.communicate("Summarize the open TODOs.\nNow rank them by effort.\n/exit\n")
replies = [line.removeprefix("You: ").removeprefix("Neon: ")
           for line in out.splitlines() if "Neon: " in line]
print(replies)
```

---

## Environment variables that matter in headless runs

Flags beat variables; variables beat the profile's saved values.

| Variable | Use in a headless run |
|---|---|
| `NEONSIDEKICK_PROFILE` | The profile, when there is no `--profile`; with neither, `default`. |
| `NEONSIDEKICK_HOME` | A whole separate home (its own `settings.json`, profiles, models, `mcp.json`, `sql.json`). |
| `NEONSIDEKICK_LLM_URL` / `NEONSIDEKICK_LLM_MODEL` | Server and model, when there is no `--url` / `--model`. |
| `NEONSIDEKICK_LLM_API_KEY` | The server's key; never put it on the command line. |
| `NEONSIDEKICK_CLAUDE_API` / `NEONSIDEKICK_CLAUDE_API_KEY` | `on` and a key offer the Claude API for the run (`--url https://api.anthropic.com`). The key is never logged and never sent to a local server. Every request is billed to the key's account. |
| `NEONSIDEKICK_LLM_TURN_TIMEOUT` / `NEONSIDEKICK_LLM_REQUEST_TIMEOUT` | Seconds; raise them for long agentic jobs. |
| `NEONSIDEKICK_LLM_CONTEXT` | The context window in tokens, when the server doesn't report it. |
| `NEONSIDEKICK_LLM_REASONING` | Reasoning effort for the run. |
| `NEONSIDEKICK_LLM_SAMPLING` | Sampling for the run, a JSON object in wire names: `{"temperature":0.2,"top_k":20,"seed":42}`. It is laid over the profile's saved values for every model; a bad value is logged and the variable ignored. |
| `NEONSIDEKICK_COMMAND_POLICY` | `ask` (default: only allow-listed commands run, since nothing can ask), or `yolo` (every command runs; use only when you trust the job and the folder). `--yolo` outranks it. |
| `NEONSIDEKICK_SHELL_POLICE` | `off` lets shell commands name paths outside the working directory for the run; `on` turns the police back on over a saved `off`. `--no-police` outranks it. |
| `NEONSIDEKICK_SHELL_NATIVE` | `off` lets a single `cat`, `dir`, `git status`, `curl`… go to the shell as written for the run, instead of being sent back once a turn to the native tool that does it (*Shell prefer native tools*, on by default); `on` turns it back on over a saved `off`. A line sent back is not a refusal: it never makes the run exit 3. |
| `NEONSIDEKICK_CLAUDE_EXE` / `NEONSIDEKICK_CLAUDE_PERMISSIONS` | The Claude Code CLI for `/claude`, and what it may do on its own for the run: `read-only` (default), `edit` or `full`. |
| `NEONSIDEKICK_CLAUDE_ADVISOR` | `on` offers the model `claude_advisor` for the run: it may ask Claude Code for advice on its own, read-only. Each tool Claude uses is a `[tool] Claude › …` line, the cost a `[notice]`, and the answer the tool's `[tool] claude_advisor -> …` line. With *Claude advisor tool confirm* on in the profile, every call is refused (`Error: claude_advisor needs the user's yes …`), since nothing can ask. |
| `NEONSIDEKICK_SEARXNG_URL`, `NEONSIDEKICK_OBSIDIAN_VAULT`, `NEONSIDEKICK_COMFY_URL` | The web search instance, notes vault and image server for the run. |
| `NEONSIDEKICK_HA_URL` / `NEONSIDEKICK_HA_TOKEN` | The Home Assistant server and a long-lived access token for the run (the token is never logged). The model's Home Assistant tools follow *Home Assistant action policy*, but there is no pane to ask on: under `ask` the safe services (lights, scenes, the TV, to-do lists) run and anything else is refused, with the model told so. |

## Flags that don't combine with `--headless`

`--smoke`, `--audio-check` and `--voice-check` are separate modes; given together with
`--headless`, headless wins and they are ignored. `--help` and `--version` print and exit before
any mode runs.
