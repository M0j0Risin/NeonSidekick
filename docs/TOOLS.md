# Tools

Back to the [README](../README.md).

The tools are what the model can call. They come in groups, laid out as the tabs of `/tools`, and each group has a switch that offers or withholds all of it (*File tools*, *Web tools*, *SQL tools*…). Single tools switch on and off on the Offered tab. `/sys` › Tools shows exactly what the model is offered now. A few groups are switched from `/settings` or `/skills` instead: see [Set elsewhere](#set-elsewhere).

## Contents

- [Always offered](#always-offered): [Clock](#clock), [Timers](#timers), [Help](#help)
- [Core](#core): [Ask](#ask), [Web](#web), [Shell](#shell)
- [Files](#files): [File](#file), [Image](#image), [UNC shares and outside folders](#unc-shares-and-outside-folders), [GitLib](#gitlib), [Obsidian](#obsidian)
- [Apps](#apps): [Docker](#docker), [ComfyUI](#comfyui), [YouTube](#youtube)
- [Databases](#databases): [How the database tools work](#how-the-database-tools-work), [SQL Server](#sql-server), [MySQL and MariaDB](#mysql-and-mariadb), [SQLite](#sqlite), [PostgreSQL](#postgresql), [Oracle](#oracle)
- [Devices](#devices): [Printing](#printing), [Camera](#camera), [Screen](#screen)
- [Home](#home): [Home Assistant](#home-assistant), [Shopping list](#shopping-list)
- [Set elsewhere](#set-elsewhere): [Memory](#memory), [Skills](#skills), [Sessions](#sessions), [Advisors](#advisors), [Plan](#plan)
- [MCP servers](#mcp-servers)

## Always offered

These have no group switch. To withhold one, switch it off on the Offered tab.

### Clock

| Tool | Arguments | What it does |
|---|---|---|
| `get_current_time` | `zone?` | The current date, time, weekday and time zone. Seeded at the start of every conversation. |
| `shift_date` | `date, days?, weeks?, months?, years?` | Moves a date by days, weeks, months or years and gives its weekday. |
| `date_difference` | `from, to` | The days between two dates (negative when the second is earlier), plus years, months and days for gaps of a month or more. |

### Timers

| Tool | Arguments | What it does |
|---|---|---|
| `start_timer` | `name?, hours?, minutes?, seconds?` | Starts a named countdown that alerts you when it ends. Several can run at once. |
| `stop_timer` | `name` | Stops a running timer, or silences one that has gone off. |
| `list_timers` | — | Every running timer and its time left. |

### Help

| Tool | Arguments | What it does |
|---|---|---|
| `neon_help` | `query?, kind?` | NeonSidekick's own manual: a command's forms; a setting's effect, default and place (pane › tab › row); a pane's or tab's rows; the keys. `query` is a command (`/camera`), a setting, a pane or tab (`/tools camera`), a key (`Ctrl+H`) or plain words; none gives an overview. `kind` (`command`, `setting`, `pane`, `keys`) narrows it; `kind: command` alone lists every command. |

It is offered on every turn, headless and in plan mode too, and the model is told to use it instead of guessing. It reads only the built-in reference, never your current settings.

## Core

### Ask

Under *Ask user*.

| Tool | Arguments | What it does |
|---|---|---|
| `ask_user` | `questions` | Multiple-choice questions on the pane: up to *Ask max questions*, each with 2 to *Ask max choices per question* options, `single` or `multi`, plus *Other…*. ESC declines them all. |

### Web

Under *Web tools*.

| Tool | Arguments | What it does |
|---|---|---|
| `web_search` | `query, max_results?` | Searches the web (DuckDuckGo or SearXNG): title, URL, snippet. |
| `web_fetch` | `url, offset?` | A page's readable content as Markdown, 32,000 characters at a time; also plain text, JSON, XML and CSV. |
| `open_url` | `url?, urls?` | Opens a link, or up to five, in your browser. |
| `download_file` | `url, path?, overwrite?` | Downloads a file into the working directory, up to *Web download max (MB)*. Needs *File tools* too. |

When a site refuses `web_fetch`, shows a bot check, or needs scripts to fill in, it tries again in your installed Edge, Chrome or Brave, run headless (*Web browser mode*). A bot check that clears by itself is waited on for up to 15 seconds; one that wants a click is reported as blocked. The browser keeps its cookies in its own folder (`%TEMP%\NeonSidekick\browser`; `$TMPDIR/NeonSidekick/browser` on a Mac); if another browser holds that folder, the fetch says so. A rate-limited site is waited out when it asks for 10 seconds or less, and requests to one site are at least a second apart.

### Shell

Under *Shell command policy*. Commands run on your machine, starting in the working directory (`workdir` picks a folder under it), guarded by *Shell command policy* and *Shell police* (see [Shell guards](SETTINGS.md#shell-guards)). A denied command tells the model not to work around it.

* Commands run hidden, with colours and pagers off and stdin closed. A progress bar redrawn with `\r` is kept as its last drawing, and a line over 8,192 characters is cut. A command that times out, is killed or is stopped with ESC goes with everything it started, even what it left behind when the program that started it exited. Background processes stop when the app closes; one closed (or forgotten after 64 newer finish) takes what it left running with it. A program a foreground command opens (`start notepad`, `code .`) stays open. Background commands also stop if the app crashes or is force quit: on Windows each command runs in a job of its own; on a Mac a small watcher (the app's own program, started as `NeonSidekick --shell-guard` with the first background command) stops them when the app ends, and should both be killed at once (`pkill -9 NeonSidekick`), the next start stops a background command still running, and names in its log what such a command left behind but cannot be proven to be the app's. On a Mac a command runs in a session of its own: a program that starts its own session (a daemon) can still slip away, and a command that asks at the terminal (a `sudo` password, an `ssh` host key) fails at once instead of waiting.
* `/process` lists the background processes; `/process <id>` watches one live in the [process window](COMMANDS.md#process-window), where Ctrl+K twice stops it.
* `/cmd <command>` runs a line of your own through the same guards ([Your own shell line](COMMANDS.md#your-own-shell-line)).
* **Headless** there is no approval pane, so under `ask` only allow-listed commands run. If any was refused, the run ends with a notice and exit code 3. `--yolo` allows every command for one launch; `--no-police` also lifts the path police. See [HEADLESS.md](HEADLESS.md).

| Tool | Arguments | What it does |
|---|---|---|
| `run_command` | `command, shell?, workdir?, timeout?, background?, notify?` | Runs a command in `powershell` (default), `cmd` or `bash` (on macOS `zsh` (default), `bash` or `powershell`), returning the exit code, time, stdout and stderr. `background` (or a long timeout) returns a `proc_…` id; `notify` shows `⚡` when it exits and checks on it next turn. |
| `execute_code` | `language, code, timeout?` | Runs a one-off `python` (`python3` on macOS), `node` or `powershell` script, approved once per language per session; nothing carries over between scripts. With *Shell tool bridge*, the script can call the app's tools (Python `from neon_tools import call`, Node `await neon.call(...)`, PowerShell `Invoke-NeonTool`), except `execute_code` and `ask_user`; a `run_command` through the bridge still needs approval and can't run in the background. |
| `process` | `action, session_id?, data?, timeout?, until?, offset?, limit?` | Manages up to 16 background processes (and the last 64 finished), by any unique id prefix: `list`, `poll`, `log` (from the last 5,000 lines), `wait` (with `until`, also stops as soon as a line not yet polled contains that text: a server's "Listening on"), `kill` (with children), `write` / `submit` (to stdin; `submit` adds a newline), `close`. `poll`, `wait` and `log` end with any text still waiting for its newline, such as a prompt. Your own `/cmd --bg` runs are kept apart and never reachable here. |

## Files

### File

Under *File tools*. Every path is relative to the working directory; nothing outside it can be reached, except the profile's `images` folder where noted.

| Tool | Arguments | What it does |
|---|---|---|
| `get_working_directory` | — | The working directory's path. Seeded at the start of every conversation. |
| `search_files` | `text?, path?, files?, regex?, context?, output?, order?, limit?, depth?` | Searches text files for a word, phrase or regex (`file:line: text`, with context lines when asked). Without `text`, lists a folder, a tree (`depth` 2–4), files matching a name pattern, or the newest files. `limit` goes up to *File search max results*. Also lists or searches the profile's `images` folder by full path (read-only). |
| `file_info` | `path` | A file's size, modified time, lines, words, line ending and BOM; a folder's counts and total size. Also checks that something exists. Takes a full path in the `images` folder too. |
| `read_file` | `path, start_line?, max_lines?` | Reads a text file or part of it (a negative `start_line` counts from the end). A partial read names the line to continue from. |
| `write_file` | `path, content, mode?` | Writes a text file: `create` (default; leaves an existing file alone), `overwrite` or `append`. With no mode, it replaces a file only if the model read it in full, wrote or patched it in this conversation, and it hasn't changed since; anything else is refused. An advisor's or bot's reads don't count. `/new`, `/clear`, `/resume` and a profile switch forget what it saw. |
| `patch_file` | `path, old_text, new_text, replace_all?` | Replaces one occurrence of `old_text` (or all with `replace_all`), tolerating differences in spacing, indentation, escapes and typographic quotes. Shows the edited lines. |
| `create_directory` | `path` | Creates a folder and any missing parents. |
| `move` | `from, to, overwrite?` | Renames or moves a file or folder; replaces nothing unless `overwrite`. |
| `copy` | `from, to, overwrite?` | Copies a file or folder, under the same rule; a folder over a folder merges. |
| `delete` | `path` | Deletes a file or folder for good. `.git`, anything in it and a folder holding one are refused. |
| `zip` | `path, to?, overwrite?` | Packs a file or folder into a `.zip`, beside it by default. The same folder makes the same zip on Windows and a Mac. |
| `unzip` | `path, to?, overwrite?` | Extracts a `.zip` into a folder, all or nothing. |
| `open` | `path?, share?` | Opens a file in your own editor or viewer, or a folder in Explorer (Finder on a Mac; the working directory by default). In the `images` folder it opens only pictures and folders. A picture opens where a double-click would (*Image viewer*), without taking the keyboard from the chat. `share` (or a `\\server\share` path) opens one on a UNC share; a network runas share is refused. |
| `convert_to_pdf` | `path? \| url? \| markdown?, title?, to?, overwrite?, landscape?, paper?` | Makes a PDF in the working directory from a file, a web page (needs *Web tools* too) or Markdown it writes (see [Making PDFs](#making-pdfs)). Not in plan mode. |

### Image

Under *Image tools* (`/tools` › Files › Image, `/tools image` or the toolbar's 🌄; off in a fresh profile), whether *File tools* is on or not. These tools reach the working directory and, by full path, the pictures in the profile's `images` folder (photos, screenshots, ComfyUI's pictures, pastes). The model finds those with `search_files` on that folder (`order: modified` for the newest). No other file tool changes anything there.

| Tool | Arguments | What it does |
|---|---|---|
| `view_image` | `path?, paths?` | Attaches images to the next message, up to *File view image max (per call)*, so the model can see them; nothing opens on your screen (`open` does that). On a Mac it also takes HEIC/HEIF, TIFF and AVIF. |
| `image_info` | `path?, paths?` | A picture's format, size, file size, frames, transparency, orientation and the metadata it carries (EXIF, GPS, XMP…), read from its header, plus the formats `image_edit` can write here. |
| `image_edit` | `path, to?, overwrite?, format?, quality?, max_kb?, width?, height?, scale?, fit?, anchor?, interpolation?, crop_x/y/width/height?, rotate?, flip?, filter?, brightness?, contrast?, saturation?, hue?, tint?, tint_amount?, blur?, sharpen?, pad?, background?, metadata?, dpi?, chroma?, colors?, dither?, interlace?, view?` | Resizes, crops, turns, recolours and converts a picture in one pass and writes a new file (see [Editing pictures](#editing-pictures)). `metadata: none` alone strips a JPEG, PNG, WebP or GIF losslessly. `view` attaches the result. Not in plan mode. |
| `set_splash_image` | `path, name?` | Copies a picture (from the working directory, or by full path from the `images` folder) into the profile's `images\splash_images`, so it shows at start and on `/splash`. The first one replaces the bundled set. |

#### Editing pictures

`image_edit` uses the system's own codecs (nothing to install) and does everything in one pass with one encode, so a JPEG loses quality only once.

* **Size:** `width` and/or `height` (one alone keeps the aspect) or `scale`. With both, `fit` is `contain` (default), `cover` (cuts at `anchor`), `pad` (fills with `background`), `stretch` or `shrink` (never enlarges). `interpolation=nearest` keeps pixel art sharp.
* **Geometry and colour:** a crop needs all four `crop_*` values; `rotate` takes 90, 180 or 270. Colour options: `filter` (grey, sepia, negative, polaroid), `brightness`, `contrast`, `saturation` (−100 to 100), `hue`, `tint`, `blur`, `sharpen`, and a `pad` border.
* **Formats on Windows:** writes PNG, JPEG, GIF, BMP and TIFF (JPEG XL and HEIF with Windows' extensions). WebP and AVIF read but don't write. An option the format can't take (`chroma` for PNG, say) is refused.
* **On a Mac:** reads HEIC/HEIF, AVIF, WebP and JPEG XL too; writes PNG, JPEG, GIF, BMP, TIFF and HEIF; refuses `chroma`, `colors` and `dither`.
* **`max_kb`** lowers the quality, then the size, until the file fits.
* **Metadata** is dropped unless asked for (*Image edit metadata*, or `metadata`). `metadata: none` alone strips EXIF (with GPS), XMP and the like without re-encoding, keeping the colour profile.
* **Output:** beside the source (or in *Image edit output folder*) as `photo-edited.png`, or where `to` says. An existing file is replaced only with `overwrite`. With *Image edit mode* `overwrite-original` and no `to`, the result replaces the source.
* **Limits:** up to 200 MB and 100 megapixels in; 32768 pixels a side and 100 megapixels out.

### UNC shares and outside folders

Under *UNC tools*. They reach named network shares (`\\server\share`, or a folder under one) and local folders outside the working directory (`D:\Data`), without mapped drives, as you (`windows`) or as another Windows account (`runas`, like `runas /netonly`). Shares live in `unc.json` (home and profile files, as for the [databases](#how-the-database-tools-work)). **UNC add/edit share** (`/tools` › Files › UNC) walks through one and can test it by listing its root; **UNC set password** updates a runas password; *UNC shares offered* picks which ones the model sees.

* **`path`** (required): `\\server\share`, a folder under it, or a full local path. In JSON, double the backslashes or use `/` (`"//fs01/eng"`). Device paths, relative paths and whole drives (`D:\`) are refused.
* **`auth`**: `windows` (default) or `runas`, with **`user`** (`DOMAIN\name` or `name@domain`) and **`passwordStore`** `file` or `credman` (`NeonSidekick/unc/<share_name>`).
* **`access`**: `read` (default) or `readwrite`. Changes need `readwrite` **and** *UNC writes* on.
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

* A `runas` share signs in fresh for each call and never clashes with your mapped drives. A `windows` share uses what your sign-in has for that server (a mapped drive or a `cmdkey` entry). Use server names, not IP addresses or drive letters; `runas` against `\\localhost` proves nothing.
* Paths stay under the share's root, and walks don't follow reparse points (add a DFS link's target as its own share).
* **Changes are permanent.** `unc_delete` starts off even under *UNC writes*. Every change is logged.
* A share that doesn't answer in 10 seconds is given up on. The shell can't use a runas share's sign-in: `unc_fetch` the file and work on the copy.

| Tool | Arguments | What it does |
|---|---|---|
| `unc_shares` | `check?` | The named shares (path, account, access, description); `check` lists each root now. |
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

`share` may be left out when `path` is a full path under a share, or when there is only one share. `unc_write` through `unc_put` appear only when both keys allow changes. `--unc-check <share>` tests a real share.

#### UNC shares on a Mac

* **Paths:** a network share is `smb://server/share`; `\\server\share` and `//server/share` read the same, so one `unc.json` serves both. A local folder is a full path (`/Users/Shared/Data`). A drive path is skipped, and `unc_shares` says why.
* **The mount:** the app mounts an `smb://` share on first use, hidden from Finder, under `/Volumes`, and **read-only** for an `access: read` share. Your own Finder mount of the same share is left alone. The app's mounts are unmounted when it exits, or at the next start after a crash.
* **While mounted, the share is the machine's:** anything you run can read it as the mounted account until the app exits. Only the shell police keeps the model's shell off it, and it reads only the paths written in a command.
* **As you** means your Kerberos ticket or a password Finder saved in the Keychain. **runas** mounts as the account (`name@REALM`, `DOMAIN\name` or the server's own name) with its password from the Keychain or `unc.json`.

### GitLib

Under *GitLib tools*: git inside the app, for when the shell is off or the model should never run `git`. Turn it off to leave git to the shell.

* Local only: no `fetch`, `pull`, `push` or `clone`.
* The repository must be the working directory or under it. Every tool takes an optional `path`, which also picks the repository.
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

### Obsidian

Under *Obsidian tools*. They work on the vault's files directly: no plugin, no network, and Obsidian needn't be running. Notes are found by name, `[[wikilink]]`, alias or path; inline tags and frontmatter properties both count. Dot-folders are ignored and line endings kept.

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

## Apps

### Docker

Under *Docker tools*. They talk to the Docker engine directly, on Windows over *Docker engine pipe* and on a Mac over its Unix socket ([below](#docker-on-a-mac)). The app never starts `docker`, `docker-compose` or Docker Desktop, and says when the engine isn't running. A container is named by its name, a unique part of one ("mysql" for `mysql_dev`), or an id prefix of four or more characters.

* **Your own hand:** `/docker` (or the **manage** button on the *Docker tools* page) lists the containers on a pane, `l` for LLM servers alone.
* **Changes need two keys:** *Docker writes* offers the changing tools, and every call asks on the pane first; a prune says how much it frees. Headless refuses them.
* **Removals are opt-in:** `docker_remove` and `docker_prune` start off even under *Docker writes*.
* **Secrets:** `docker_inspect` shows environment variable names, never values, and hides secret-looking labels, flags and URL passwords. Logs can't be redacted, so the model is told never to repeat a secret from one.
* **Logged:** every change, the model's or yours.
* **Compose:** `docker_lifecycle` with `scope: project` acts on a compose project's containers in dependency order. `compose up` isn't offered.

| Tool | Arguments | What it does |
|---|---|---|
| `docker_containers` | `all?, filter?, project?` | The containers, running first: name, state and health, status, image, ports, compose project, short id. `all: false` leaves out stopped ones. |
| `docker_logs` | `container, tail?, since?, grep?, stream?, timestamps?` | The last `tail` lines (100, up to 2000), since an age (`10m`, `2h`, `1d`) or moment, stdout or stderr only. `grep` searches the last 5000. At most 16,000 characters, oldest cut first. |
| `docker_inspect` | `container` | One container in detail (state, health, exit code, restarts, image, command, environment names, ports, mounts, networks, restart policy, limits, compose project, labels), secrets hidden. |
| `docker_stats` | `container?` | CPU, memory against its limit, network and disk traffic, processes; one container or all running. |
| `docker_resources` | `kind, filter?, unused?` | `images`, `volumes` (who mounts each), `networks` or `disk` (space per kind and what a prune would free). |
| `docker_compose` | `project?` | The compose projects: folder, files, services and their state. |
| `docker_lifecycle` | `target, action, scope?, timeout_seconds?` | `start`, `stop`, `restart`, `pause` or `unpause` a container or, with `scope: project`, a compose project. A stop waits `timeout_seconds` (10, up to 120) before the kill. Asks first. |
| `docker_pull` | `image, tag?` | Pulls a public image (`nginx`, `postgres:16`, `ghcr.io/owner/app`); no registry credentials are sent. Asks first. |
| `docker_remove` | `kind, name, force?` | Removes one container (keeping its anonymous volumes), image or volume. `force` removes a running container or a used image. Asks first; off by default. |
| `docker_prune` | `kind, all?` | Removes stopped containers, untagged images (every unused one with `all`), empty networks, anonymous unused volumes (named too with `all`) or the build cache. Asks first, with count and size; off by default. |

`--docker-check` tests the tools against the real engine.

#### Docker servers

Containers that serve an OpenAI-compatible API (vLLM, SGLang…) can be `/server` choices, one running at a time so two models never fight over the GPU. Turn on *Docker servers enabled* and tick them in *Docker server containers* on `/settings` › Servers › Docker. The checklist lists only LLM servers: containers whose image or command holds one of *Docker server match words* (an empty list lists every container), or that carry the label `neonsidekick.llm=true`. This needs neither the Docker tools nor *Docker writes*. **Windows only** ([why](#docker-on-a-mac)). Working `docker run` commands: [vLLM](DOCKER_WINDOWS_VLLM.md) and [SGLang](DOCKER_WINDOWS_SGLANG.md).

* **Picking one:** `/server` lists a **Docker** row per chosen container; `/server docker` lists them alone; `docker:<container>` picks one (also for `--url` and `NEONSIDEKICK_LLM_URL`).
* **Switching** stops every other chosen container (up to *Docker server stop timeout* plus 15 s each), waits *Docker server post-stop delay*, then starts this one and polls `/v1/models` until it answers or *Docker server ready timeout* passes. If a stop fails, nothing starts. Ctrl+C cancels.
* **Model:** whatever the container serves (the saved *LLM model* if listed, else its first).
* **Leaving:** moving to another server that uses this machine's GPU (the embedded model, a local server, or a blank *LLM URL*) stops the containers first; hosted APIs and other machines' servers leave them running. *Docker server stop on exit* stops the one in use when the app exits.
* **Bots and advisors** share a running container but never start or stop one.

#### Docker on a Mac

The same tools and `/docker` pane, over the engine's Unix socket (Docker Desktop, OrbStack, Colima or Rancher Desktop). *Docker engine socket* (the row Windows calls *Docker engine pipe*) names one (`/var/run/docker.sock`, or `unix:///path`). Left empty, it is found the way the `docker` command finds it:

1. `DOCKER_HOST`, if set.
2. The Docker CLI's current context (`DOCKER_CONTEXT`, else the one in `~/.docker/config.json`).
3. The usual places: `~/.docker/run/docker.sock`, `/var/run/docker.sock`, `~/.orbstack/run/docker.sock`, `~/.colima/default/docker.sock`, `~/.rd/docker.sock`.

A `tcp://` or `ssh://` engine is refused: unset `DOCKER_HOST`, run `docker context use`, or name a socket. The lookup runs again for each connection, so switching engines needs no restart. `docker_stats` shows the Linux VM's CPU and memory, not the Mac's.

**Docker servers stay Windows only:** Docker Desktop on a Mac passes no GPU to containers, and the vLLM and SGLang images need CUDA. A Mac's local server is the [embedded model](SETTINGS.md#embedded) on Metal.

### ComfyUI

Under *ComfyUI tools*. The image tools run **your own ComfyUI workflows** on your server (*ComfyUI URL*), save the pictures, and show them to the model in the next message.

#### Adding a workflow

**With the wizard** (*ComfyUI add workflow* on `/tools` › Apps › ComfyUI):

* **Build** makes a standard text → image or image → image workflow from your server's checkpoints, samplers and so on.
* **Import** takes a ComfyUI API export (*Workflow → Export (API)*), puts the placeholders in, and keeps the export's values as defaults. Use it for FLUX.2, Krea 2, Z-Image, Qwen Image, Ernie Image, Boogu, LongCat Image, HiDream I1 and Ideogram 4, which **Build** can't wire: export ComfyUI's own template and import that.
* The summary can **test** the draft (one small run, nothing saved), then saves `<name>.json` + `<name>.md`.

**By hand:**

1. Build the workflow in ComfyUI and export it in the **API format** (*Workflow → Export (API)*). A regular save is refused.
2. Put placeholders where the call's values go, and drop the file into `<profile>\comfy\` (this profile) or `<home>\comfy\` (every profile; the profile's wins a name clash). The file name is the workflow's name.

| Placeholder | Becomes |
|---|---|
| `{{prompt}}` | The positive prompt. Required unless the workflow takes an input picture (a face swap or upscale, listed as *no prompt*). |
| `{{negative}}` | The negative prompt. |
| `{{seed}}`, `{{width}}`, `{{height}}`, `{{steps}}`, `{{cfg}}`, `{{denoise}}` | A number when it is the whole value (`"seed": "{{seed}}"`), or text inside a longer string (`"neon-{{seed}}"`). |
| `{{image}}` | The uploaded input picture's name, for a `LoadImage` node (img2img, upscale, inpaint). |
| `{{image2}}`, `{{image3}}` | The second and third input pictures (a face swap's face). They fill in order, or the workflow is skipped. |
| `{{!name}}` | A literal `{{name}}`, for nodes that use double braces themselves (Ideogram 4). **Import** escapes these for you. |

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
reinforce: false      # optional: send this negative as written, nothing appended
image: the picture to restyle   # optional, per input picture: its role, shown to the model (image, image2, image3)
---
Prefer source_anime; keep rating_safe unless asked.
```

#### Prompts

- **Describe what you want** ("a cozy neon ramen stall at night") and the model writes the prompt in the family's style (below), adding the family's default negative unless the sidecar names one.
- **Give your own prompt** ("use this prompt: score_9, …") and the model passes it unchanged (`verbatim: true`).
- **Skip the model:** `/imagine score_9, score_8_up, source_anime, 1girl -- score_4, blurry --seed 42`.
- **Limit the choice** with *ComfyUI workflows offered*.

| Family | Prompt style the model writes |
|---|---|
| Pony Diffusion XL | `score_9, score_8_up, score_7_up`, a `source_*` tag, then Danbooru tags, weighted like `(tag:1.3)` where it helps |
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

A workflow can take up to three pictures. For a face swap, install the ReActor node pack, build the swap (two `LoadImage` nodes → `ReActorFaceSwap` → `SaveImage`), *Export (API)* and **Import** it. The `LoadImage` nodes become `{{image}}` and `{{image2}}` (the wizard says which). Name their roles in the `.md`:

```markdown
---
description: Face swap (ReActor)
image: the picture whose face is replaced
image2: the face to put in
---
```

Then "put my face from [Image #2] on the person in [Image #1]" works in chat, or `/imagine faceswap --image target.png --image2 face.png` without the model. A Qwen-Image-Edit graph needs `{{prompt}}` put in by hand.

#### The ComfyUI tools

| Tool | Arguments | What it does |
|---|---|---|
| `generate_image` | `prompt?, workflow?, negative?, negative_extra?, verbatim?, width?, height?, seed?, steps?, cfg?, denoise?, image?, image2?, image3?, count?` | Runs a workflow (the only fitting one when none is named), saving 1 to *ComfyUI max pictures per call* pictures, each with the next seed. The result names the files and seed; the pictures follow in the next message. `prompt` may be left out only for a workflow without `{{prompt}}`. |

* A refused run names the node and input at fault (a missing checkpoint, a bad value).
* `image` is a path in the working directory, a saved picture's full path, or a pasted picture's `[Image #N]` (sent at its original size).
* The pictures go to *ComfyUI output folder* under the profile's `images` folder; the result gives each full path.
* `image2` and `image3` take the roles the workflow's `.md` names. Without a workflow name, the one taking that many pictures is used.

### YouTube

Under *YouTube tools*. Videos play in the app's own video window, on YouTube's embedded player (on Windows through the WebView2 Runtime, which Windows 11 has). It doesn't take the keyboard from the terminal. F11 is full screen; Esc leaves full screen, then closes it; YouTube's own keys work once you click into the video. A video that can't be embedded (or is private, age-restricted or gone) is refused, and the answer points at `open_url`.

On a Mac (macOS 14 or later, not over SSH) the window uses WebKit. ⌃⌘F is full screen; Esc or ⌘W closes it; Tab brings the terminal forward.

| Tool | Arguments | What it does |
|---|---|---|
| `youtube_search` | `query, max?, kind?, order?, length?, since?, region?, language?, live?, captions?, safe_search?` | Searches YouTube with *YouTube API key* (about 100 quota units a search) and lists videos that play embedded: title, channel, length, views, year and id. `kind` finds playlists or channels instead. `order`, `length` (short, medium, long), `since`, `region`, `language`, `live`, `captions` and `safe_search` filter; one left out uses your saved choice (*YouTube search order*, *safe search*, *region*, *language*). Offered only with a key set, headless too. Allowed in plan mode. |
| `youtube_playlist` | `playlist?, max?` | Lists a public playlist's videos, numbered as `youtube_play`'s `entry` counts them. Named by id, a link with `list=` or a channel (its uploads); left out, the playlist playing. `max` 1–200, 50 by default. Offered only with a key set, headless too. Allowed in plan mode. |
| `youtube_play` | `video?, start?, playlist?, entry?` | Plays a video by id or any YouTube link (its `t=` included), from `start` (`90` or `1:30`). Or a public playlist (an id, a `list=` link, or a channel) from `entry` (1-based); YouTube's player plays it through. A mix link (`list=RD…`) plays just its video. Starts at once, or cued with *YouTube autoplay* off. Needs no key; not headless. |
| `youtube_control` | `action, value?` | `play`, `pause`, `seek` (to a time), `forward` / `back` (by seconds, 10 by default), `volume` (0–100), `mute`, `unmute`, `next` / `previous` / `entry` (the playlist's, 1-based) or `close`, and says where the video then is. |
| `youtube_status` | (none) | What the window is playing: video and channel, state, position and length, volume, and the playlist entry. Allowed in plan mode. |
| `youtube_saved` | (none) | Your saved videos, numbered, with channel, where each was left (or watched) and id. Headless too. Allowed in plan mode. |
| `youtube_save` | `action, video` | `add` saves a video (`current` for the one in the window, with where it is), `remove` takes one off (also by its number in `youtube_saved`). Headless too, by id or link. |

* Playing a playlist needs no key. Watch Later and your private lists are out of reach, since the app never signs in.
* **Saved videos** (your bookmarks, per profile, in `youtube.json`) resume a few seconds before where you left them. One seen to the end starts over and reads `watched`. `/youtube save`, `/youtube list` and `/youtube unsave` are your own hand on the list (see [Commands](COMMANDS.md)).

## Databases

### How the database tools work

The five families (SQL Server, MySQL/MariaDB, SQLite, PostgreSQL, Oracle) share one design. Each has its own switch (*SQL tools*, *MySQL tools*, *SQLite tools*, *PostgreSQL tools*, *Oracle tools*) and its own tab under `/tools` › Databases. They read by default; changes are a separate, opt-in tool.

#### Managing connections

* **The file:** each family keeps its connections in a JSON file: `sql.json`, `mysql.json`, `sqlite.json`, `postgres.json` or `oracle.json`. The home folder's (`%USERPROFILE%\.neonsidekick\`, `~/.neonsidekick/` on a Mac) is read by every profile; the profile's (`…\profiles\<profile>\`) wins a name clash. Comments and trailing commas are allowed.
* **The wizard:** *… add/edit connection* (*SQLite add/edit database*) walks through a new connection one page per choice, or edits a saved one (the stored password is kept unless you type another). Its summary can **test** the draft, writing nothing: it signs in, shows the version, warns when the account could change data, and names the other databases or schemas the account could reach. It saves with the file's comments kept. ESC steps back.
* **Set password:** *… set password* updates a password.
* **Offered:** *… connections offered* (*SQLite databases offered*) is the checklist of what the model sees. **Nothing is offered until you tick it** (here or in the wizard).
* **Passwords:** `passwordStore` is `file` (default: a password typed into the file is encrypted in place with DPAPI on the next read) or `credman` (Windows Credential Manager, target `NeonSidekick/<family>/<connection_name>`, family being `sql`, `mysql`, `postgres` or `oracle`). On a Mac, `file` encrypts with a key kept in the Keychain, and `credman` is a Keychain generic password of the same name (`security add-generic-password -s NeonSidekick/sql/<connection_name> -a <user> -w`).

#### Reading safely

The query tools run one read-only statement behind layers:

1. **The gate** lets through only a single read statement (`SELECT`, or a `WITH` ending in one). A second statement, data or structure changes, locking reads and the engine's ways out (files, links to other servers, dynamic SQL) are refused before the server sees anything.
2. **The session or transaction** is read-only and always rolled back.
3. **The account is the real guard.** Give the login read-only grants.

Values go in as parameters (`params`). Results are cut at *SQL query result max chars*; `max_rows` is 1–100000.

#### The connection's database

Each connection reaches **one database** (Oracle: one schema), and nothing else on the server:

* No tool takes a database or lists the server's databases. To work in another database, add a connection for it.
* A statement that names another database (`payroll.dbo.salaries`), or reads the server's own catalog (other databases, logins, other sessions), is refused before it reaches the server. If a table alias is spelled like another database's name, rename the alias.
* What the text can't show (a view, synonym or routine that reads another database) stays the account's to guard. The wizard's test and the `--*-check` runs name what else the account could reach. A login that opens only its own database is the lock the server keeps too.

#### Read-write mode

Changes go through a fifth tool per family (`sql_execute`, `mysql_execute`, `sqlite_execute`, `postgres_execute`, `oracle_execute`). It needs **two keys**: the family's *… mode* set to `read-write` **and** the connection's `"access": "readwrite"` (SQLite has only the first). Both are checked on every call. It is **never offered headless or in plan mode**.

1. **The kinds.** *… statements allowed* ticks which kinds may run. A statement needs every kind it does (a `WITH` that deletes and inserts needs both), and a refusal names the kinds that are ticked.

   | Kind | Statements | Default |
   |---|---|---|
   | changing data | `INSERT`, `UPDATE`, `MERGE` (`REPLACE` in MySQL and SQLite), upserts and `RETURNING`/`OUTPUT` included | ✓ |
   | deleting | `DELETE`, `TRUNCATE`, a `MERGE` that deletes | |
   | creating | `CREATE TABLE`, `INDEX`, `VIEW`, `SEQUENCE` and the like; SQL Server's `SELECT … INTO`; SQLite's `TRIGGER` and `VIRTUAL TABLE` | ✓ |
   | changing structure | `ALTER`, `RENAME`, `COMMENT ON`; `CREATE OR REPLACE VIEW` needs creating too | |
   | dropping | `DROP` of those, and of routines and triggers | |
   | upkeep | statistics and index upkeep (`UPDATE STATISTICS`, `ANALYZE`, `OPTIMIZE`, `VACUUM`, `REINDEX`…) | |
   | procedures and triggers | server databases: running code (SQL Server's `EXEC`, `CALL`, PostgreSQL's `DO`, an Oracle PL/SQL block) and `CREATE`/`ALTER` of a procedure, function, trigger (Oracle: package, type). Off by default, since code's effects can't be read from the statement | |
   | settings | SQLite only: `PRAGMA` | |
   | reading | `SELECT` (and `VALUES`, `TABLE`, `EXPLAIN` where the engine has them): never asks, and runs on the read-only path | ✓ |

   In SQL Server, a batch's lone first word reads as `EXEC` of it, so a typo there is a procedure call. A SQLite trigger also needs the kinds its body does.
2. **The gate.** One statement per call (a routine's body counts as one), checked against an allow-list. Always refused, for example: transaction words (each call is its own transaction), `SET`/`USE`, `GRANT`/`REVOKE`, users and roles, creating or dropping databases, dynamic SQL, file access (`BULK INSERT`, `LOAD DATA`, `COPY`, `ATTACH`) and links to other servers.
3. **Your allow.** Every change asks on a pane that shows the statement (`v` shows all of it): **Deny**, **Allow once**, or **Allow for this session** (that connection or file only, until `/new`, `/clear` or a profile switch).
4. **The run.** The statement commits on its own; a failed, timed-out or cancelled one changes nothing. Every change is logged with the connection, rows changed and statement.
5. **The account is still the real guard.** Give a `readwrite` connection only the permissions you want the model to use.

`--sql-check`, `--mysql-check`, `--sqlite-check`, `--postgres-check` and `--oracle-check` (each with a connection's or database's name) test a family against a real server or file; see [COMMANDLINE.md](COMMANDLINE.md).

### SQL Server

`sql.json`; no ODBC driver needed.

* **`server`**: `host`, `host,port` or `host\instance`. On a Mac a named-pipe (`np:`) server can't be reached.
* **`database`** (required): the one database the connection reaches.
* **`auth`**: `sql` (a SQL login: `user` and `password`), `windows` (your account; on a Mac, your Kerberos ticket from `kinit`), or `runas` (another Windows account, `DOMAIN\name` or `name@domain`, plus a password; like `runas /netonly`, it signs in only to the server as that account). On a Mac `runas` uses Kerberos with the stored password, so the account is `name@REALM`; see [BUILD.md](BUILD.md#another-account-runas).
* **`encrypt`**: `strict`, `mandatory` (default) or `optional`. **`trustServerCertificate`**: `true` accepts a self-signed certificate.
* **`connectTimeoutSeconds`**: 1–120 (default 15). **`access`**: `read` (default) or `readwrite`. **`passwordStore`**: `file` or `credman`.

```json
{
  "connections": {
    // SQL Auth: password encrypted in place after the first read
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

| Tool | Arguments | What it does |
|---|---|---|
| `sql_connections` | — | The named connections (server, database, sign-in, description). Touches no server. Every other tool takes one by name in `connection`, which may be left out when there is only one. |
| `sql_tables` | `connection?, schema?, pattern?` | Tables and views as `schema.name`, with kind, approximate rows and description. `pattern` is text in the name or a `LIKE` pattern. |
| `sql_columns` | `pattern, connection?, schema?` | Every column whose name matches (`EmailAddress`, `%CustomerID`): table, type, nullability, description. |
| `sql_describe` | `table, connection?` | One table or view in full: columns, keys, foreign keys both ways, indexes, CHECK constraints and triggers. |
| `sql_relationships` | `connection?, table?` | Foreign-key join paths, all or touching a table. |
| `sql_indexes` | `connection?, table?, schema?, missing?` | Indexes with their columns, size and use since restart. `missing: true` adds the optimizer's suggestions. Usage needs `VIEW SERVER STATE`. |
| `sql_query` | `sql, connection?, params?, max_rows?` | One read-only `SELECT`. `params` is an object (`{"id": 43659}` for `@id`); `max_rows` defaults to *SQL max rows*. |
| `sql_execute` | `sql, connection?, params?, max_rows?` | [Read-write mode](#read-write-mode) only (*SQL mode*, *SQL statements allowed*). One statement that may change the database. Answers with the rows changed and any rows returned. |

* The gate is SQL Server's own parser. Refused, for example: batches, `EXEC`, `INTO`, linked servers, `OPENROWSET`.
* Server-wide views and functions (`sys.databases`, `sys.dm_*`, logins) are refused; the database's own catalog (`sys.objects`, `INFORMATION_SCHEMA`) reads fine.
* CLR types (`geography`, `hierarchyid`) need `.ToString()`.

### MySQL and MariaDB

`mysql.json`; MySQL 8.0.16+ and MariaDB 10.2+.

* **`host`**, **`port`** (3306 by default), **`database`** (required).
* **`user`**, **`passwordStore`** `file` or `credman`.
* **`sslMode`**: `preferred` (default), `required`, `verify-ca`, `verify-full` or `none`.
* **`allowPublicKeyRetrieval`**: `true` only for a `caching_sha2_password` account without TLS (off by default; a man in the middle could supply its own key).
* **`connectTimeoutSeconds`**: 1–120 (default 15). **`access`**: `read` (default) or `readwrite`.

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

| Tool | Arguments | What it does |
|---|---|---|
| `mysql_connections` | — | The named connections (host, database, user, description). Touches no server. Every other tool takes one by name in `connection`, which may be left out when there is only one. |
| `mysql_tables` | `connection?, pattern?` | Tables and views as `database.name`, with kind, approximate rows and comment. |
| `mysql_columns` | `pattern, connection?` | Every column whose name matches: table, type, nullability, comment. |
| `mysql_describe` | `table, connection?` | One table or view: columns, keys, foreign keys both ways, indexes, CHECK constraints and triggers. |
| `mysql_relationships` | `connection?, table?` | Foreign-key join paths: the database's or a table's. |
| `mysql_indexes` | `connection?, table?` | Indexes: kind, key columns, cardinality, and use since restart where `performance_schema` allows. |
| `mysql_query` | `sql, connection?, params?, max_rows?` | One read-only `SELECT` (`LIMIT n`). `params` as for SQL Server (`@id`). |
| `mysql_execute` | `sql, connection?, params?, max_rows?` | [Read-write mode](#read-write-mode) only (*MySQL mode*, *MySQL statements allowed*). One statement that may change the database. Answers with the rows changed and any rows returned. |

* Refused, for example: executable comments (`/*! … */`), `INTO`, `LOAD_FILE`, `USE`, and `information_schema`, `performance_schema`, `mysql` and `sys` as qualifiers.
* In a `CREATE PROCEDURE`/`TRIGGER` body, a column or label named `begin` or `end` must be in backticks.

### SQLite

SQLite tools read database **files**: no server, no password. A database is a name from `sqlite.json` (a relative `path` counts from the file's folder) or, with *SQLite sandbox files* on, any file in the working directory by its path. **SQLite add/edit database** can test one: it opens the file read-only and counts its tables. Only *SQLite mode* is needed for changes (no per-database access key).

```json
{
  "databases": {
    "chinook": { "path": "D:\\data\\chinook.db", "description": "The music store sample" }
  }
}
```

| Tool | Arguments | What it does |
|---|---|---|
| `sqlite_databases` | — | The named databases, and whether working-directory files may be named. Opens nothing. Every other tool takes one in `database` (a name or a file's path), which may be left out when only one is named. |
| `sqlite_tables` | `database?, pattern?` | Tables and views with their kind. |
| `sqlite_describe` | `table, database?` | One table or view: columns, foreign keys both ways, indexes and the `CREATE` statement. |
| `sqlite_query` | `sql, database?, params?, max_rows?` | One read-only `SELECT`, `WITH` or `VALUES` (`LIMIT n`). `params` binds `@name`, `:name`, `$name` or `#name` (`{"id": 5}`); positional `?` is refused. |
| `sqlite_execute` | `sql, database?, params?, max_rows?, create?` | [Read-write mode](#read-write-mode) only (*SQLite mode*, *SQLite statements allowed*). One statement that may change the database. `create: true` makes a new database file (see below). Answers with the rows changed and any rows returned. |

* **Reading** opens the file read-only with `query_only` on, whatever the mode. A statement past the timeout (or ESC) is interrupted.
* **Always refused:** `ATTACH`/`DETACH`, `VACUUM INTO`, `writable_schema` and `load_extension()`.
* **The rest of the app keeps out.** While *SQLite tools* is on, the shell police refuses a command or script that reaches SQLite, and the file tools never change a database file (`.db`, `.db3`, `.sqlite`, `.sqlite3`, their journals, or one `sqlite.json` names). Reading and copying from one still work.
* **Creating a database:** `create: true` needs creating ticked and *SQLite sandbox files* on. The file must end `.db`, `.sqlite`, `.sqlite3` or `.db3` in an existing folder of the working directory. If the statement fails, the new file is removed.

### PostgreSQL

`postgres.json`.

* **`host`**, **`port`** (5432 by default), **`database`** (required).
* **`schema`** (optional): holds the connection to that one schema. Its search path is that schema alone, other schemas and `information_schema` are refused, and `postgres_schemas` lists only it. Without it, every schema of the database is open.
* **`user`**, **`passwordStore`** `file` or `credman`.
* **`sslMode`**: `prefer` (default), `require`, `verify-ca`, `verify-full` or `disable`.
* **`connectTimeoutSeconds`**: 1–120 (default 15). **`access`**: `read` (default) or `readwrite`.

```json
{
  "connections": {
    "shop": { "host": "localhost", "database": "shop", "user": "shop_reader", "password": "type-password-here-once", "description": "The sample retail database" },
    "billing": { "host": "db01.example.com", "database": "billing", "schema": "invoices", "user": "billing_ro", "passwordStore": "credman", "sslMode": "verify-full" }
  }
}
```

| Tool | Arguments | What it does |
|---|---|---|
| `postgres_connections` | — | The named connections (host, database, schema lock, user, description). Touches no server. Every other tool takes one by name in `connection`, which may be left out when there is only one. |
| `postgres_schemas` | `connection?` | The schemas the account may use (the locked one alone, under a lock), with table counts and owners. |
| `postgres_tables` | `connection?, schema?, pattern?` | Tables and views as `schema.name`, with kind, approximate rows and comment. |
| `postgres_columns` | `pattern, connection?, schema?` | Every column whose name matches: table, type, nullability, comment. |
| `postgres_describe` | `table, connection?, schema?` | One table or view: columns, foreign keys both ways, indexes and CHECK constraints. |
| `postgres_relationships` | `connection?, schema?, table?` | Foreign keys: a schema's, or a table's either way. |
| `postgres_indexes` | `connection?, schema?, table?` | Indexes: unique, primary, the definition, scans since the statistics reset. |
| `postgres_query` | `sql, connection?, params?, max_rows?` | One read-only `SELECT` (`LIMIT n`). `params` binds `@name` (`id=@id`); positional `$1` is refused. |
| `postgres_execute` | `sql, connection?, params?, max_rows?` | [Read-write mode](#read-write-mode) only (*PostgreSQL mode*, *PostgreSQL statements allowed*). One statement that may change the database. Answers with the rows changed and any rows returned. |

* Refused in queries, for example: `COPY`, `DO`, `dblink`, functions that reach files or other backends, and server-wide views (`pg_database`, `pg_roles`, `pg_stat_activity`).
* The wizard warns of a superuser role but doesn't refuse it. PostgreSQL lets every role connect to a new database unless `CONNECT` is revoked from `PUBLIC`; the test names which ones it could.

### Oracle

`oracle.json`; no Oracle Client needed. An Oracle connection is held to **one schema** rather than a database.

* **`dataSource`**: EZConnect `host:port/service` (`localhost:1521/FREEPDB1`; port 1521 by default) or a whole `(DESCRIPTION=…)`.
* **`user`**: the database user. `SYS` (and any `AS SYSDBA` sign-in) is refused, since Oracle doesn't hold SYS to a read-only transaction.
* **`schema`**: the schema the connection is held to (the user's own by default).
* **`connectTimeoutSeconds`**: 1–120 (default 15). **`access`**: `read` (default) or `readwrite`. **`passwordStore`**: `file` or `credman`.

```json
{
  "connections": {
    // Password encrypted in place after the first read
    "hr": {
      "dataSource": "localhost:1521/FREEPDB1",
      "user": "hr_reader",
      "password": "type-password-here-once",
      "schema": "HR",
      "description": "The sample human-resources schema"
    },
    // A full descriptor, the password in the system's credential store (credman)
    "ledger": {
      "dataSource": "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=dbhost01.example.com)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=LEDGER)))",
      "user": "ledger_ro",
      "passwordStore": "credman"
    }
  }
}
```

| Tool | Arguments | What it does |
|---|---|---|
| `oracle_connections` | — | The named connections (data source, user, schema, description). Touches no server. Every other tool takes one by name in `connection`, which may be left out when there is only one. |
| `oracle_tables` | `connection?, pattern?` | Tables and views as `SCHEMA.NAME`, with kind, the optimizer's row count and comment. `pattern` is text in the name or a `LIKE` pattern. |
| `oracle_columns` | `pattern, connection?` | Every column whose name matches: table, type, nullability, comment. |
| `oracle_describe` | `table, connection?` | One table or view in full: columns, keys, foreign keys both ways, indexes, CHECK constraints and triggers. |
| `oracle_relationships` | `connection?, table?` | Foreign-key join paths: the schema's or a table's. |
| `oracle_indexes` | `connection?, table?` | Indexes: kind, key columns, status, visibility, the optimizer's counts, and recorded use where readable. |
| `oracle_query` | `sql, connection?, params?, max_rows?` | One read-only `SELECT` (`FETCH FIRST n ROWS ONLY`, no trailing `;`). `params` binds `:id`. |
| `oracle_execute` | `sql, connection?, params?, max_rows?` | [Read-write mode](#read-write-mode) only (*Oracle mode*, *Oracle statements allowed*). One statement that may change the schema; a PL/SQL block counts as one. Answers with the rows changed (`RETURNING … INTO` is refused: select the rows after). |

* On 23ai and later the session itself is set read-only for reads, so the server refuses any write. For the account, give `SELECT` grants (or `ALTER USER … READ ONLY` on 23ai).
* Refused, for example: `FOR UPDATE`, database links, `UTL_HTTP`/`UTL_FILE`/`DBMS_SQL`, and the whole-database views (`ALL_*`, `DBA_*`, `CDB_*`, `V$`). `USER_*` views work.
* A CLOB or BLOB shows its start and length; object types (`SDO_GEOMETRY`, `XMLTYPE`) need converting to text.

## Devices

### Printing

Under *Print tools*. `/print` and the print tools send work to any printer Windows has installed, or on a Mac any printer in System Settings › Printers & Scanners (through CUPS).

* **Drawn by the app:** text and code print as a monospace listing; Markdown prints formatted; a picture (PNG, JPEG, GIF, WebP, BMP) is fitted to one page. Every page carries the file's name, the time and *page N of M*.
* **Anything else** (a PDF, a Word or Excel file) goes to the program Windows has for printing it, on the default printer only; a printer, copies, pages or landscape with it is refused.
* **On a Mac** a PDF goes to the printer as it is (printer, copies and pages work; landscape doesn't). Word, Excel and other files are refused: print them from their own program. Line and page breaks can differ slightly from Windows.
* ESC (or Ctrl+C) cancels a print: the job is withdrawn and the chat says *(print cancelled)*.
* **The policy:** under `ask`, `print_file` shows the file, printer and sheets and waits for your yes.

| Tool | Arguments | What it does |
|---|---|---|
| `list_printers` | — | The installed printers, marking the system's default and *Print default printer*. Available in plan mode. |
| `print_file` | `path, printer?, copies?, pages?, landscape?` | Prints a file from the working directory: a printer by name (else *Print default printer*, else the system's default), 1–10 copies, pages like `1-3`, `4-` or `1,3,5`, and landscape. Not in plan mode. |

#### Making PDFs

`/pdf` and the `convert_to_pdf` file tool make a PDF in the working directory, with nothing new to install.

* **Sources:** Markdown (formatted, links clickable, pictures from the working directory only), text and code (a coloured listing), a picture (one page), an HTML file (scripts and embeds taken out), or a web page as the browser shows it (if *Web browser network mode* allows its address; it keeps its own layout, so paper and landscape aren't taken).
* **Pages:** US Letter by default (`paper=` A4 or Legal), portrait unless landscape, with the title and *page N of M* at the top.
* **The engine** (*PDF engine*): Edge, Chrome, Brave or (on a Mac) Chromium (*Web browser path*, else the first found). Without one, or when it fails under `auto`, Windows' **Microsoft Print to PDF** draws Markdown, text and pictures in black and white. HTML and web pages need the browser. On a Mac only the browser works.
* **Output:** beside the source with `.pdf` (a web page or Markdown text gets a name like `example.com-intro.pdf`), or where `to` says. An existing PDF is replaced only with `overwrite`.
* `convert_to_pdf` needs *File tools* (a `url` needs *Web tools* too). `/pdf` needs neither.

### Camera

Under *Camera tool*.

| Tool | Arguments | What it does |
|---|---|---|
| `camera_capture` | `prompt` | Shows the model's request ("Hold the label up to the camera."), then waits for you to take the photo (*Camera shutter* `user`) or for your permission (`model`). The photo is saved in *Camera output folder* under the profile's `images` folder and attached after the result; a decline isn't retried that turn. Allowed in plan mode. |

On a Mac (macOS 14 or later) it can use the built-in camera, a USB webcam or an iPhone. macOS asks once whether your terminal may use the camera (System Settings › Privacy & Security › Camera); until allowed, the tool says so.

### Screen

Under *Screen capture tool*.

| Tool | Arguments | What it does |
|---|---|---|
| `screen_capture` | `target?, prompt` | Captures a monitor, every monitor or one window (see [Screen capture](COMMANDS.md#screen-capture)), after your yes under *Screen capture ask* `ask`. Saved in *Screen capture output folder* under the profile's `images` folder and attached after the result; a denial isn't retried that turn. Allowed in plan mode. |
| `screen_list` | (none) | Lists the monitors and windows with the target that names each. Titles and sizes only. Allowed in plan mode. |

On a Mac (macOS 14 or later) your terminal needs Screen Recording (System Settings › Privacy & Security › Screen & System Audio Recording, then reopen the terminal). Without it no picture is sent, only how to turn it on, and `screen_list` gives monitors without windows.

## Home

### Home Assistant

Under *Home Assistant tools*. They control your own Home Assistant with a long-lived token (*Home Assistant URL*, *Home Assistant API key*): lights and scenes, a TV, sensors, to-do lists.

* **Names, not ids:** "the den", "kitchen and hallway", "Den Corner Lamp" or "all". A room goes to its group light when it has one, else to every light in it. An ambiguous name comes back as a question, so the model never guesses an id.
* **The action policy:** under `ask`, safe services run and anything else shows on the pane first. A no tells the model not to retry.
* **States** are read at most every 30 seconds, and again after any change.
* **The home bar** (*Home bar*, `/homebar`) is your own hand, not the model's: it needs the URL and the key but not *Home Assistant tools*, the action policy never applies to it, and it keeps its own WebSocket while it shows. See [Home bar](SETTINGS.md#home-bar).

| Tool | Arguments | What it does |
|---|---|---|
| `ha_overview` | — | Lights on per room, light groups, media players, temperatures, motion, low batteries (under 20%), to-do lists, the scene count and unavailable lights. |
| `ha_states` | `query?, domain?, area?` | Entities, one line each, narrowed by words, domain and room (at most 80). An exact entity id gives every attribute. |
| `ha_history` | `entity, hours?` | One entity's states over the last 1–336 hours (24 by default), oldest first. |
| `ha_lights` | `target, action?, brightness_pct?, color_name?, color_temp_kelvin?, transition?` | `on` (default; also changes brightness or colour), `off` or `toggle`, with a colour name or white temperature (1500–9000 K) and a 0–300 s fade. |
| `ha_scene` | `scene, transition?` | Activates a scene by name or id. |
| `ha_media` | `action, target?, volume_pct?, source?` | `on`, `off`, `volume`, `volume_up`, `volume_down`, `mute`, `unmute`, `source`, `play`, `pause`, `play_pause`, `stop`, `next`, `previous`. The target may be left out when there's one player. A source matches by name or prefix (`hdmi 3`). |
| `ha_todo` | `action, item?, list?` | `list` (allowed under every policy), or `add`, `complete` or `remove` an item. Off in a new profile; switch it on in `/tools` › Offered. |
| `ha_call_service` | `domain, service, entity?, data?` | Any other service (`remote.send_command`, `button.press`, `script.turn_on`), subject to the policy. |
| `ha_assist` | `text` | Hands a sentence to Home Assistant's Assist agent, as a last resort. Refused under policy `off`. |

### Shopping list

Under *Shopping tools*. One shopping list per profile and any number of stores, kept in the profile's `db\shopping\shopping.db`. The list sorts itself into the walk of the store you shop at (the active one, or one named in the call): aisles and zones in order, then items placed in a department, then unplaced ones, then ticked lines.

* **Layout facts:** a store learns where a product, brand or category is (`Aisle 7`, `Dairy`). A line is placed by its whole name, then a brand, then the longest matching word. A fact the model guessed is marked `(guess)` until you confirm it.
* **Reference documents:** a store's guides and floor maps go in `db\shopping\reference\<store>` (PDFs, `.md`, `.txt`, pictures). They are indexed when first needed and when a file changes (`/shopping reindex` reads them all). PDFs are read through the headless browser (*Web browser path*).
* The model is told to keep the list only here, never in a file, note or Home Assistant to-do list. Every change is logged under `[Shopping]`.

| Tool | Arguments | What it does |
|---|---|---|
| `shopping_list` | `store?` | The list walked through the store, grouped, with the ticked lines and likely duplicates. Allowed in plan mode. |
| `shopping_add` | `items, store?` | Adds lines (`"2 soy milk"`, `"eggs x3"`); a name already there gets the quantity added. Says where each is at the store. |
| `shopping_check` | `items, checked?` | Ticks lines as bought (or unticks them), by name or by the number `shopping_list` shows beside them (the active store's list). |
| `shopping_remove` | `items` | Takes lines off the list. |
| `shopping_clear` | `all?` | Removes the ticked lines; `all` empties the whole list, only after your yes on the pane (refused headless). |
| `shopping_locate` | `term, store?` | Where the list would put a product and by which fact, related facts, and the reference document lines that name it. Allowed in plan mode. |
| `shopping_reference` | `query?, file?, page?, picture?, reread?, store?` | The store's reference documents: searched, one page read (its picture too with `picture`), listed, or all read again with `reread`. Allowed in plan mode. |
| `shopping_place` | `term, kind?, location?, section?, source?, note?, forget?, store?` | Records where a product, brand or category is (a new aisle, zone or section is made), or forgets it; `source` is `user`, `reference` or `guess`. |
| `shopping_stores` | `store?` | The stores, the active one marked, and one store's layout: locations in walk order, sections, fact count and reference documents. Allowed in plan mode. |
| `shopping_layout` | `action, name, to?, section?, walk?, notes?, store?` | Adds a store or makes it active, renames it; adds, renames or removes a section or location; sets a location's place in the walk. |
| `shopping_recent` | `limit?` | What was bought before and isn't on the list now, most often first, with counts and last date. Allowed in plan mode. |

## Set elsewhere

These tools are switched from `/settings` or `/skills`, not `/tools`.

### Memory

Under *Memory mode*.

| Tool | Arguments | What it does |
|---|---|---|
| `save_memory` | `text` | Saves one lasting fact about you, known in every later session. Not offered while *Memory mode* is `read-only`. |
| `recall_memory` | — | Everything remembered, oldest first, but the memories disabled on the `/memory` pane. Seeded at the start of every conversation. |

### Skills

Under *Agent skills*.

| Tool | Arguments | What it does |
|---|---|---|
| `load_skill` | `name, file?` | Loads a skill's instructions (the list is in the system prompt), or one of its files, up to 64,000 characters. Offered only while a skill is installed; never a skill you [disabled](SETTINGS.md#disabling-a-skill). |
| `skill_editor` | `action, scope?, name, description?, instructions?, path?, content?, old_text?, new_text?, replace_all?, summary?` | `create` or `update` a skill under `profile` (default) or `global`. `write_file` writes a supporting file and `edit_file` swaps `old_text` for `new_text`; `path` is relative to the skill folder. Never edits external skills or one you [locked](SETTINGS.md#locking-a-skill) or [disabled](SETTINGS.md#disabling-a-skill), and never deletes. |

### Sessions

Under *Session tool*.

| Tool | Arguments | What it does |
|---|---|---|
| `session_manager` | `action, query?, id?, max_results?, from_turn?, to_turn?` | `search`, `list` or `read` this profile's earlier conversations (not the current one). Never restores or purges. |

### Advisors

Under *Advisors enabled*. Advisors are other profiles of this home, each in a role you give it, set up under `/settings` › Advisors ([SETTINGS.md](SETTINGS.md#advisors)); `/advisor` asks one yourself ([COMMANDS.md](COMMANDS.md#advisor)).

| Tool | Arguments | What it does |
|---|---|---|
| `neon_advisor` | `advisor, question, context?` | Asks one advisor, by profile or role, for its view. It answers on its own server and model, under its own persona and role, and may call this chat's read-only tools for up to *Advisor tool iterations* round trips. The transcript shows the question, its tools, the answer and its model and tokens. Each advisor keeps a thread for the session. Offered while an advisor is listed; plan mode keeps it, and there the model is asked to consult advisors whose role fits before presenting a plan. An advisor on the Claude Code server answers text-only unless it is the first listed there and this chat is on another server. |

### Plan

Offered in [plan mode](COMMANDS.md#plan-mode).

| Tool | Arguments | What it does |
|---|---|---|
| `present_plan` | `title, markdown, name?` | Plan mode only. Saves the plan as `.neon/plans/<name>.md` with a `status` / `revision` / `requirement` header, prints it and asks for approval; revisions overwrite the same file. The result is your verdict. Headless, the plan is saved and `/plan approve` starts it. `status` is `draft`, `approved`, `cancelled`, `done` or `incomplete`. |

## MCP servers

Under *MCP servers*. Each connected server is its own tool group, its tools named `<server>__<tool>` (`gateway__get_current_time`) with the server's descriptions. Servers connect in the background (🔌 and a count on the hint row); a reply started meanwhile gets the ones already connected. The Servers tab of `/mcp` switches whole servers, the Tools tab single tools.
