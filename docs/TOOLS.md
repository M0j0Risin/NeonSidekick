# Tools

Every tool the model can call, grouped as the tabs of `/tools` show them. Back to the [README](../README.md).

Each group has a switch (*File tools*, *Web tools*…), and the Offered tab of `/tools` switches single tools ([Panes](INTERFACE.md#panes)). `/sys` › Tools shows what the model is offered right now.

Settings in *italics* are in [SETTINGS.md](SETTINGS.md), with their values and defaults.

## Contents

- [Where tools are offered](#where-tools-are-offered)
- [Core](#core): [Web](#web), [Shell](#shell), [Clock](#clock), [Timers](#timers), [Ask](#ask), [Help](#help)
- [Files](#files): [File](#file), [Image](#image), [UNC shares and outside folders](#unc-shares-and-outside-folders), [GitLib](#gitlib)
- [Apps](#apps): [Docker](#docker), [ComfyUI](#comfyui), [YouTube](#youtube), [Obsidian](#obsidian)
- [Admin](#admin): [Network](#network), [Event logs](#event-logs), [Services](#services), [Scheduled tasks](#scheduled-tasks), [Active Directory](#active-directory)
- [Databases](#databases): [How the database tools work](#how-the-database-tools-work), [SQL Server](#sql-server), [MySQL and MariaDB](#mysql-and-mariadb), [SQLite](#sqlite), [PostgreSQL](#postgresql), [Oracle](#oracle)
- [Devices](#devices): [Camera](#camera), [Screen](#screen), [Printing](#printing)
- [Home](#home): [Home Assistant](#home-assistant), [Shopping list](#shopping-list)
- [Set elsewhere](#set-elsewhere): [Memory](#memory), [Skills](#skills), [Sessions](#sessions), [Advisors](#advisors), [Plan](#plan), [Ralph loop](#ralph-loop)
- [MCP servers](#mcp-servers)
- [Known issues](#known-issues)

## Where tools are offered

* **A group appears only when it can work:** Obsidian needs a vault, the databases and UNC a connection or share you ticked, Home Assistant its URL and key, ComfyUI its URL and a workflow.
* **Headless** leaves some tools out, and refuses anything that would stop to ask you: [Before you start](HEADLESS.md#before-you-start).
* **Plan mode** ([COMMANDS.md](COMMANDS.md#plan-mode)) keeps the reading tools, `ask_user`, `camera_capture`, the screen tools and `neon_advisor`, and adds `present_plan`. It drops the MCP tools and every tool that changes or starts something (`start_timer`, `open`, `process`, `image_edit`…).
* **Code audit** ([COMMANDS.md](COMMANDS.md#code-audit)) keeps the file, picture, PDF and git reads, `neon_advisor`, `neon_help`, `get_current_time`, `load_skill`, `recall_memory` and `ask_user`, and adds `present_audit`. The web's reads join only under *Code audit web research*; an advisor asked in a pass gets the same set.
* **On a Mac:** what runs there is in [MAC.md](MAC.md#what-works); a group that behaves differently has its own "… on a Mac" section below.

## Core

### Web

Under *Web tools*.

| Tool | Arguments | What it does |
|---|---|---|
| `web_search` | `query, max_results?` | Searches with *Web search method* (DuckDuckGo or SearXNG): title, URL, snippet. |
| `web_fetch` | `url, offset?` | A page as Markdown, 32,000 characters at a time (a `#anchor` starts at that part). Also text, JSON, XML, CSV, and a PDF's outline and first 40 pages ([Reading PDFs](#reading-pdfs)). |
| `open_url` | `url?, urls?` | Opens up to five links in your browser, or one in the [Browser window](#the-browser-window) under *Web open links in*. A YouTube link goes where *YouTube open links in* says. |
| `web_links` | `filter?` | Your saved links (as `/browser list` shows), at most 100; `filter` keeps those holding all its words. |
| `web_save_link` | `action, url, name?` | `add`, `remove` or `rename` a saved link; `url` may be its `web_links` number, or `current` for the Browser window's page. |
| `download_file` | `url, path?, overwrite?` | Downloads into the working directory, up to *Web download max (MB)*. Needs *File tools*. |

* **A page that refuses a plain fetch**, or needs scripts, is tried again in a headless Edge, Chrome or Brave (Chromium on a Mac), per *Web browser mode*. Its cookies stay in `%TEMP%\NeonSidekick\browser` (`$TMPDIR/NeonSidekick/browser` on a Mac).
* **Bot checks:** one that clears by itself is waited on up to 15 seconds; one that wants a click goes to the Browser window, or is reported blocked.
* **Pacing:** a rate limit of 10 seconds or less is waited out, and one site gets at most a request a second.

#### The Browser window

The app's own browser (macOS 14+ on a Mac), for a bot check the web tools can't pass. Its bar, keys, history and saved links: [The Browser window](INTERFACE.md#the-browser-window).

* **When:** `web_fetch`, `web_search`, `download_file` and `convert_to_pdf` offer it on a 403 or a 503 page, or a check's page. Never for a rate limit (a 403 too), an API's JSON or XML refusal, or headless. *Web browser botcheck window* says whether it asks first.
* **Solving:** Esc, *Give up*, closing the window or 5 minutes gives up. Once the page shows no check, the window closes and the tool goes on.
* **From the phone:** the Web UI's Chat tab can answer it and, with *Web UI bot check control*, drive the window ([A bot check from the page](INTERFACE.md#a-bot-check-from-the-page)).
* **Cookies:** the window hands the site's cookies to the web tools for this run. A page that ends on another site is refused, and none of that site's cookies are read. On a Mac: [The Browser window on a Mac](MAC.md#the-browser-window-on-a-mac).

### Shell

Under *Shell command policy*. Commands run in the working directory, guarded by *Shell command policy* and *Shell police* ([Shell guards](SETTINGS.md#shell-guards)).

* `/cmd` runs your own line through the same guards ([Your own shell line](COMMANDS.md#your-own-shell-line)).
* Commands run hidden, with colors, pagers and input off; a line over 8,192 characters is cut.
* A command that times out, or that you stop with Esc, stops with everything it started. A program a command opens (`code .`) stays open until the app closes, even after a crash; on a Mac a crash leaves it.
* Background commands stop when the app closes, even after a crash; `/process` lists them ([process window](INTERFACE.md#the-process-window)).
* **On a Mac,** a daemon a command starts can slip away, and a command that asks at the terminal (a `sudo` password) fails at once.
* **Headless,** under `ask` only allow-listed commands run, and a run that refused one exits with code 3; `--yolo` allows all ([HEADLESS.md](HEADLESS.md)).

| Tool | Arguments | What it does |
|---|---|---|
| `run_command` | `command, shell?, workdir?, timeout?, background?, notify?` | Runs a command in `powershell` (default), `cmd` or `bash`; on a Mac `zsh` (default), `bash` or `powershell`. `background` (or a long timeout) returns a `proc_…` id; `notify` tells you when it exits. |
| `execute_code` | `language, code, timeout?` | Runs a one-off `python` (`python3` on a Mac), `node` or `powershell` script. It asks like `run_command`; allowing it for the session covers that language. |
| `process` | `action, session_id?, data?, timeout?, until?, offset?, limit?` | Up to 16 background commands (and the last 64 finished), by id prefix: `list`, `poll`, `log` (last 5,000 lines), `wait` (`until` a line holds a text), `kill`, `write` / `submit`, `close`. |

* **The tool bridge:** with *Shell tool bridge* on, a script can call the app's other tools (Python `from neon_tools import call`, Node `neon.call(...)`, PowerShell `Invoke-NeonTool`) but not `execute_code` or `ask_user`. A `run_command` through it still asks, and can't run in the background (nor with a timeout past the foreground cap). A call is at most 2 MB.
* **The shell police also** refuses a line, a script it runs, an `execute_code` script or a `process` write that names `.git`, `.neon`, the project file or the app's own folder (a bare `git` command excepted); a script the line runs before it exists; and a script holding a forbidden string. Like every police rule it reads the text, not what runs.
* **A `cmd` line with an unclosed quote** always asks, whatever the allow list holds.

### Clock

Under *Clock tools* (on by default).

| Tool | Arguments | What it does |
|---|---|---|
| `get_current_time` | `zone?` | The date, time, weekday and time zone, here or in an IANA zone. Called at the start of every conversation while *Clock tools* is on. |
| `shift_date` | `date, days?, weeks?, months?, years?` | Moves a date by days, weeks, months or years and gives its weekday. |
| `date_difference` | `from, to` | The days between two dates (negative when the second is earlier), plus years, months and days for longer gaps. |

### Timers

Under *Timer tools* (on by default); not offered headless. A timer going off rings *Timer tone*, whoever started it. The model can't start a stopwatch (`/timer stopwatch` is yours), but `list_timers` and `stop_timer` include them.

| Tool | Arguments | What it does |
|---|---|---|
| `start_timer` | `name?, hours?, minutes?, seconds?` | Starts a named countdown that alerts you when it ends; several can run at once. |
| `stop_timer` | `name` | Stops a running timer or stopwatch, or silences one that has gone off. |
| `list_timers` | — | Every running timer and its time left, and every stopwatch and its time so far. |

### Ask

Under *Ask user*. Not offered headless.

| Tool | Arguments | What it does |
|---|---|---|
| `ask_user` | `questions` | Asks you multiple-choice questions: up to *Ask max questions*, each with 2 to *Ask max choices per question* options, `single` or `multi`; you can type your own answer. Esc declines them all. |

### Help

Under *Help tools* (on by default).

| Tool | Arguments | What it does |
|---|---|---|
| `neon_help` | `query?, kind?` | Neon Sidekick's own manual: commands, settings (effect, default, place), panes and keys, narrowed by `kind` (`any`, `command`, `setting`, `pane`, `keys`). It never reads your current settings. `/help <words>` asks the model to use it. |

## Files

### File

Under *File tools*. Paths are relative to the working directory; nothing outside it can be reached except, where noted, the profile's `images` folder.

| Tool | Arguments | What it does |
|---|---|---|
| `get_working_directory` | — | The working directory's path. Called at the start of every conversation. |
| `search_files` | `text?, path?, files?, regex?, context?, output?, order?, limit?, depth?` | Searches text files for a word, phrase or regex. Without `text`, lists a folder, a tree (`depth` 2–4), matching names or the newest files (the `images` folder too). |
| `file_info` | `path` | A file's size, time, lines, words, line ending and BOM, or a folder's counts; also whether something exists. |
| `read_file` | `path, start_line?, max_lines?` | Reads a text file or part of it (a negative `start_line` counts from the end), in its own encoding: UTF-8, UTF-16 or UTF-32, else Windows-1252 as a guess the header names. A line over 32,000 characters is cut, and the header says so. |
| `read_pdf` | `path, pages?, pictures?, find?, fields?, share?` | A PDF's text page by page; `find` lists pages holding words, `pictures` sends pages as pictures, `fields` lists a form's fields ([Reading PDFs](#reading-pdfs)). |
| `write_file` | `path, content, mode?` | Writes a text file: `create` (default), `overwrite` or `append`. Without a mode it replaces only a file the model read whole, wrote or patched, unchanged since. An overwrite or append keeps the file's encoding, BOM and line ending; an append stops at 8 MB. |
| `patch_file` | `path, old_text, new_text, replace_all?` | Replaces `old_text` once (or everywhere, with `replace_all`), tolerating spacing, indentation and quote differences. A one-line `old_text` must match closely; an edit already made is not made twice; the result may not pass 8 MB. |
| `create_directory` | `path` | Creates a folder and any missing parents. |
| `move` | `from, to, overwrite?` | Renames or moves a file or folder; replaces nothing unless `overwrite`, and says what it replaced. |
| `copy` | `from, to, overwrite?` | Copies under the same rule; a folder copied over a folder merges. Hidden files come along; every `.git` and guarded file is left out and named. A copy that would leave under 1 GB free is refused. |
| `delete` | `path` | To the Recycle bin (the Trash on a Mac) under *File deletes to Recycle bin*, else for good. With no bin (a share, a removable drive), deleted for good; an item the bin can't take is not deleted, and the tool says so. `.git`, what's in it and a folder holding one are refused. |
| `zip` | `path, to?, overwrite?` | Packs a file or folder into a `.zip`, beside it by default under a free name (`overwrite` is for a named `to`). Hidden files and empty folders come along; up to 5,000 entries and 500 MB. Off in a new profile. |
| `unzip` | `path, to?, overwrite?` | Extracts a `.zip` into a folder, all or nothing: up to 5,000 entries and 500 MB, no zip bomb. Every `.git` and guarded entry is left out and named. A downloaded archive's internet mark goes on every file, so Windows and macOS still warn. Off in a new profile. |
| `open` | `path?, share?` | Opens a document, picture, PDF, audio, video or text file in its own program, a picture where *Image viewer* says, or a folder in Explorer (Finder on a Mac); `share` opens one on a UNC share. Programs, scripts, shortcuts, installers and unlisted types are refused; `/open` opens them. |
| `convert_to_pdf` | `path?, url?, markdown?, title?, to?, overwrite?, landscape?, paper?` | A PDF from a file, a web page (needs *Web tools*) or Markdown ([Making PDFs](#making-pdfs)). |
| `pdf_edit` | `action, path?, paths?, pages?, angle?, fields?, flatten?, to?, overwrite?` | Merges, extracts, deletes or rotates pages, or fills in a form, into a new PDF ([Editing PDFs](#editing-pdfs)). |

* A deletion with no bin says so. In the `images` folder, `open` opens only pictures and folders; a `runas` UNC share can't be opened.
* **Keeping a file's form:** a write in the file's own encoding that can't hold a character is refused, never written as `?`. A read-only file is never written over; a hard-linked one is written in place so its other names see it; a replace keeps the file's permissions.
* **What the file tools never change** (any file tool, the UNC and vault tools too): `.git` and `.neon`, the app's home and profiles, skill folders (`skill_editor` changes a skill), and a SQLite database while the SQLite tools are on. The file is judged, not its spelling: a short `~1` name, a stream after `:` and a link swapped in are refused.
* **The project file** (`NEON.md`, `AGENTS.md`, `CLAUDE.md` in the working directory) changes only after you allow it on the pane; with nobody to ask (headless, an advisor, a script, a pass that may not ask) it is refused.
* **Paths:** a `~/` or `file:` path, a device name (`NUL`, `COM1`), a pipe or socket, and a working directory deleted during the session are refused; the last is never made again.
* **Names with hidden characters** (right-to-left marks, zero-width or control characters) show them as escapes in results and the log, so a name can't disguise itself.
* **Searching:** the first hits come back in path order, the same on every run, at most 32,000 characters in all, then a note. `text` is matched as given, its spaces kept. One file as `path` that isn't searched (too big, not text, missed by `files`) says why. `files` reads `./` and `**.cs`.
* **Cloud files** (OneDrive's online-only ones) are listed and counted; a search leaves them unread and says how many, and reading one fetches it.

#### Reading PDFs

`read_pdf` reads a PDF in a headless Edge, Chrome, Brave or (on a Mac) Chromium, and refuses without one; `read_file` points to it.

* **Pages:** `"3"`, `"2-5"`, `"5-"` or `"1,3,7-9"` (up to 100 parts), physical numbers (a different printed one shows beside it). A page past 32,000 characters is cut; `from_char` with that one page reads on. Left out, it starts at page 1 with the outline; a long read comes back in part, `next` naming what is left of the pages asked.
* **`find`** lists up to 20 pages holding every word (any case or accent, a word split by a hyphen across lines too), searching the first 2,000 pages. **`pictures`** sends pages as pictures (at most *File view image max images*); a saved session keeps a line naming each. **`fields`** lists a form's fields for `pdf_edit`, their values cut at 1,000 characters.
* **Limits:** 32 MB; a locked PDF is refused. Chinese, Japanese and Korean read. `share`, or a `\\server\share` path, reads a PDF on an offered UNC share in place.
* **Kept apart:** the PDF is read in a page cut off from the network, in a profile of its own (`%TEMP%\NeonSidekick\browser-pdf`, `$TMPDIR/…` on a Mac), never the web tools' cookie profile.
* **On the web:** `web_fetch` reads a PDF link the same way (outline and first 40 pages, up to 32 MB); `download_file` then `read_pdf` reads the rest.

#### Editing PDFs

`pdf_edit` and `/pdf merge|extract|delete|rotate` change PDFs in the working directory, in the same network-less headless browser as [reading](#reading-pdfs), whatever *PDF engine* says.

* **`merge`** joins `paths` in order (20 at most, 64 MB together). **`extract`** copies the given pages, in the order given, to a new PDF; **`delete`** makes a copy without them (never without every page). **`rotate`** turns pages by `angle` (90, 180, 270, -90), all when `pages` is left out.
* **`fill_form`** sets `fields` by name: text, `true`/`false`, an option, or a list for a multi-choice list. An unknown name or option is refused with the real ones, and nothing is written. `flatten: true` bakes the values in; an XFA form is refused.
* **Output:** a new PDF beside the first, named for the edit (`report-p2-5.pdf`, `a-merged.pdf`; `-2` when taken), or `to`. The original is replaced only when `to` names it with `overwrite`.
* **Limits:** 32 MB each; a password-locked PDF is refused, never decrypted. A PDF on a UNC share must be fetched first (`unc_fetch`).

### Image

Under *Image tools* (`/tools image`, or the toolbar's 🌄), apart from *File tools*; off in a new profile. They reach the working directory and, by full path, the profile's `images` folder. Every picture sent to a model, by any tool or a paste, goes without its EXIF, GPS and XMP (the file on disk keeps them).

| Tool | Arguments | What it does |
|---|---|---|
| `view_image` | `path?, paths?` | Attaches pictures to the next message (up to *File view image max images*, each once) for the model to see, without their metadata. |
| `image_info` | `path?, paths?` | Format, size, frames, transparency, orientation and metadata (EXIF, GPS, XMP…) of up to 20 files, and what `image_edit` can write. |
| `image_edit` | `path, to?, overwrite?, format?, quality?, max_kb?, width?, height?, scale?, fit?, anchor?, interpolation?, crop_x?, crop_y?, crop_width?, crop_height?, rotate?, flip?, filter?, brightness?, contrast?, saturation?, hue?, tint?, tint_amount?, blur?, sharpen?, pad?, background?, metadata?, dpi?, chroma?, colors?, dither?, interlace?, view?` | Resizes, crops, rotates, recolors or converts a picture into a new file ([Editing pictures](#editing-pictures)). |
| `set_splash_image` | `path, name?` | Copies a picture into the profile's `images\splash` for the welcome splash; the first replaces the bundled set. |

#### Editing pictures

`image_edit` uses the system's codecs and encodes once, so a JPEG loses quality once.

* **Size:** `width` and/or `height`, or `scale`; with both, `fit` is `contain`, `cover` (cut at `anchor`), `pad`, `stretch` or `shrink`. A crop needs all four `crop_*`; `rotate` is a quarter turn (90, 180, 270, or -90); `filter` is `gray`, `sepia`, `negative` or `polaroid`. Colors are names, `#rgb`, `#rrggbb` or `#rrggbbaa`.
* **Formats:** writes PNG, JPEG, GIF, BMP and TIFF (JPEG XL and HEIF with Windows' extensions); WebP and AVIF read only. A Mac's: [MAC.md](MAC.md#what-works).
* **`max_kb`** (1 KB = 1,000 bytes) lowers the quality, then the size, until it fits. **Metadata** is dropped unless kept (*Image edit metadata*, or `metadata`); `metadata: none` alone strips EXIF (GPS too), XMP and a JPEG's own thumbnail without re-encoding, keeping the orientation. A PNG's or WebP's orientation is applied first.
* **Output:** `photo-edited.png` beside the source (or in *Image edit output folder*) under a free name (`-2`, `-3`…), or a named `to`, which replaces a file only with `overwrite`; the source itself under *Image edit mode*, a replaced one to the Recycle bin when that setting is on.
* **Limits:** 200 MB and 100 megapixels in; 32,768 pixels a side and 100 megapixels out.

### UNC shares and outside folders

Under *UNC tools*. They reach named network shares (`\\server\share`, or a folder under one) and local folders outside the working directory (`D:\Data`), as you (`windows`) or another account (`runas`, like `runas /netonly`).

* **Setting up:** shares live in `unc.json`, in the home's or profile's `configs` folder like the [database connections](#managing-connections).
* *UNC add/edit share* adds and tests one, *UNC set password* changes a password, *UNC shares offered* picks what the model sees.
* **Changes need two keys:** *UNC writes* on **and** the share's `access` `readwrite`; only then do the changing tools appear. Changes are permanent (no trash) and logged, one that failed partway too; `unc_delete` starts off even then.
* **Signing in:** `windows` uses what your sign-in has for that server (a mapped drive, a `cmdkey` entry); `runas` signs in fresh each call. Use server names, not IP addresses. The shell can't use a `runas` sign-in: `unc_fetch` the file instead.
* **`path`** (required): a share, a folder under one or a full local path, never a whole drive. In JSON double the backslashes or use `/`.
* **`auth`** `windows` (default) or `runas`, with **`user`** (`DOMAIN\name` or `name@domain`) and **`passwordStore`** `file` or `credman` (`NeonSidekick/unc/<share_name>`). **`access`** `read` (default) or `readwrite`; **`description`** for the model.
* Paths stay under the share's root; a DFS link's target needs its own share. A name given twice in one file (ignoring case) loads neither entry.

```json
{
  "shares": {
    "eng": { "path": "//fs01/eng", "description": "Engineering specs and drawings" },
    "finance": { "path": "//fs02.corp.local/finance/reports", "auth": "runas", "user": "CORP\\svc_reader", "passwordStore": "credman" },
    "data": { "path": "D:/Data", "access": "readwrite" }
  }
}
```

| Tool | Arguments | What it does |
|---|---|---|
| `unc_shares` | `check?` | The named shares; `check` tries each one now. |
| `unc_search` | `share?, text?, path?, files?, regex?, context?, output?, order?, limit?, depth?` | `search_files` on a share. |
| `unc_info` | `share?, path?` | A file's size, dates, lines and words, or a folder's counts. |
| `unc_read` | `share?, path, start_line?, max_lines?` | Reads a text file, whole or in part. |
| `unc_fetch` | `share?, path, to?, overwrite?` | Copies into the working directory (needs *File tools*), up to 5,000 files and 500 MB. Esc stops a folder's copy between files and takes back what it copied. |
| `unc_write` | `share?, path, content, mode?` | Writes a text file: `create` (default), `overwrite` or `append`. |
| `unc_patch` | `share?, path, old_text, new_text, replace_all?` | Changes part of a text file. |
| `unc_create_directory` | `share?, path` | Creates a folder. |
| `unc_move` / `unc_copy` | `share?, from, to, overwrite?` | Moves, renames or copies within one share. |
| `unc_delete` | `share?, path` | Deletes a file or folder: for good on a network share; an outside folder on this PC uses the Recycle bin under *File deletes to Recycle bin*. Off by default. |
| `unc_put` | `from, share?, to?, overwrite?` | Copies from the working directory onto a share (needs *File tools*), up to 5,000 files and 500 MB. |

`share` may be left out for a full path or a single share. `--unc-check <share>` tests a share.

#### UNC shares on a Mac

* **Paths:** `smb://server/share`, `\\server\share` and `//server/share` read the same, so one `unc.json` serves both. A local folder is a full path; a drive path is skipped.
* **The mount:** a share is mounted on first use, hidden from Finder and **read-only** unless a change is being made, and unmounted at exit (or at the next start after a crash). A `runas` share's writable mount goes when the change ends.
* **While mounted, anything you run can read the share.** Only the shell police keeps the model's shell off it, and it reads only the paths written in a command.
* **Signing in:** as you, it uses your Kerberos ticket or a Keychain password Finder saved; `runas` mounts as the account (`name@REALM` or `DOMAIN\name`) with its stored password. A share already mounted is used as you only when it is your own mount, signed in as you.

### GitLib

Under *GitLib tools*: git inside the app, for when the model should never run `git`. Local only (no `fetch`, `pull`, `push` or `clone`), on a repository at or under the working directory; `path` picks it.

* `gitlib_discard` and `gitlib_delete` lose work for good; `gitlib_delete` starts off.
* Commits need *GitLib user email* and *GitLib user name*, which `/gituser` writes into the repository.

| Tool | Arguments | What it does |
|---|---|---|
| `gitlib_status` | `path?` | The branch, ahead/behind, and every staged, modified, untracked or conflicted path. |
| `gitlib_log` | `path?, ref?, max_commits?` | Commits from `ref` (HEAD by default), newest first; with a file, those that changed it. |
| `gitlib_show` | `ref, path?` | One commit's author, date, message and files; with a file, its text then. |
| `gitlib_diff` | `path?, ref?, from?, to?, staged?, max_lines?` | A diff of unstaged or staged changes, a commit, or two commits. |
| `gitlib_blame` | `path, from_line?, to_line?, ref?` | Who last changed each line, and in which commit. |
| `gitlib_branch` | `action, name?, new_name?, start_point?, switch_to?, path?` | `list`, `create`, `switch` or `rename` branches; a switch never overwrites local changes. |
| `gitlib_stage` | `action, paths, path?` | `stage` or `unstage` paths, or `.` for everything under `path`. |
| `gitlib_commit` | `message, amend?, allow_empty?, path?` | Commits what is staged as `user.name` / `user.email`. |
| `gitlib_stash` | `action, message?, index?, include_untracked?, path?` | `push`, `pop`, `apply` or `list` stashes. |
| `gitlib_discard` | `paths?, ref?, path?` | Throws away uncommitted changes in `paths`, or resets hard (untracked files stay). |
| `gitlib_delete` | `kind, name?, index?, path?` | Removes a local `branch` (never the checked-out one), a `tag`, or a `stash` by index. |

## Apps

### Docker

Under *Docker tools*, over *Docker engine pipe* (a socket on a Mac, [below](#docker-on-a-mac)); the app never starts Docker Desktop. A container is named by its name, a unique part of it, or a 4+ character id prefix.

* `/docker` runs them yourself ([COMMANDS.md](COMMANDS.md)); `--docker-check` tests them against the real engine.
* **Changes need two keys:** *Docker writes* offers them, and each call asks you first (headless refuses). `docker_remove` and `docker_prune` start off even then. Every change is logged.
* **Secrets:** `docker_inspect` shows environment variable names, never values, and hides secret-looking labels and passwords. Logs can't be redacted, so the model is told never to repeat a secret from one.

| Tool | Arguments | What it does |
|---|---|---|
| `docker_containers` | `all?, filter?, project?` | The containers, running first, with state, health, image, ports and compose project; `all: false` leaves out stopped ones. |
| `docker_logs` | `container, tail?, since?, grep?, stream?, timestamps?` | The last `tail` lines (100, at most 2,000), or `since` an age (`10m`); `grep` searches the last 5,000. At most 16,000 characters. |
| `docker_inspect` | `container` | One container in detail, secrets hidden. |
| `docker_stats` | `container?` | CPU, memory, network and disk use of one or all running containers. |
| `docker_resources` | `kind, filter?, unused?` | `images`, `volumes`, `networks`, or `disk` (space used and what a prune would free). |
| `docker_compose` | `project?` | The compose projects, with folder, files and services. |
| `docker_lifecycle` | `target, action, scope?, timeout_seconds?` | `start`, `stop`, `restart`, `pause` or `unpause` a container, or a compose project (`scope: project`). A stop waits `timeout_seconds` (10, at most 120). |
| `docker_pull` | `image, tag?` | Pulls a public image (`postgres:16`); no registry credentials are sent. |
| `docker_remove` | `kind, name, force?` | Removes a container, image or volume for good; `force` takes a running or used one. |
| `docker_prune` | `kind, all?` | Removes unused containers, images, networks, volumes or build cache (more with `all`), saying first how much it frees. |

#### Docker servers

Containers that serve an OpenAI-compatible API (vLLM, SGLang…) can be `/server` choices, one running at a time. **Windows only.**

Turn on *Docker servers enabled* and tick them in *Docker server containers* (`/settings` › Servers › Docker); the Docker tools needn't be on.

* The list holds containers matching *Docker server match words* or labeled `neonsidekick.llm=true`. Examples: [vLLM](DOCKER_WINDOWS_VLLM.md), [SGLang](DOCKER_WINDOWS_SGLANG.md).
* **Picking one:** `/server` lists a row per chosen container; `docker:<container>` picks one (also in `--url` and `NEONSIDEKICK_LLM_URL`).
* **Switching** stops the other chosen containers, then starts this one and waits until it's ready (*Docker server stop timeout (s)*, *Docker server post-stop delay (s)*, *Docker server ready timeout (s)*). If a stop fails, nothing starts.
* **Leaving** for a server that uses the GPU of the computer running NeonSidekick (the embedded model, a local server, a blank *LLM URL*) stops the containers; other servers leave them running. *Docker server stop on exit* stops the one in use at exit.
* Bots and advisors share a running container but never start or stop one.

#### Docker on a Mac

The same tools work over the engine's socket (Docker Desktop, OrbStack, Colima or Rancher Desktop), named by *Docker engine socket*. Left empty, it's found as the `docker` command finds it:

1. `DOCKER_HOST`, if set.
2. The Docker CLI's current context (`DOCKER_CONTEXT`, else the one in `~/.docker/config.json`).
3. The usual places: `~/.docker/run/docker.sock`, `/var/run/docker.sock`, `~/.orbstack/run/docker.sock`, `~/.colima/default/docker.sock`, `~/.rd/docker.sock`.

* A `tcp://` or `ssh://` engine is refused. `docker_stats` shows the Linux VM's CPU and memory, not the Mac's.
* **Docker servers stay Windows only:** Docker on a Mac passes no GPU to containers. A Mac's local server is the [embedded model](SETTINGS.md#embedded).

### ComfyUI

Under *ComfyUI tools*. `generate_image` runs **your own ComfyUI workflows** on your server (*ComfyUI URL*) and shows the pictures to the model.

#### Adding a workflow

**With the wizard** (*ComfyUI add workflow* on `/tools` › Apps › ComfyUI):

* **Build** makes a text → image or image → image workflow from your server's checkpoints and samplers.
* **Import** takes an API export (*Workflow → Export (API)*) and puts the placeholders in. For FLUX.2, Krea 2, Z-Image, Qwen Image, Ernie Image, Boogu, LongCat Image, HiDream I1 and Ideogram 4, import ComfyUI's own template.
* The summary can **test** the draft with one small run, then saves `<name>.json` and `<name>.md`.

**By hand:**

1. Export the workflow in the API format (*Workflow → Export (API)*); a regular save is refused.
2. Put placeholders in, and drop the file into `<profile>\comfy\` (this profile) or `<home>\comfy\` (every profile). The file name is the workflow's name.

| Placeholder | Becomes |
|---|---|
| `{{prompt}}` | The positive prompt; required unless the workflow takes an input picture. |
| `{{negative}}` | The negative prompt. |
| `{{seed}}`, `{{width}}`, `{{height}}`, `{{steps}}`, `{{cfg}}`, `{{denoise}}` | A number when it is the whole value (`"seed": "{{seed}}"`), else text. |
| `{{image}}` | The uploaded input picture, for a `LoadImage` node. |
| `{{image2}}`, `{{image3}}` | The second and third input pictures, filled in order. |
| `{{!name}}` | A literal `{{name}}`, for nodes that use double braces themselves. |

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

- **Describe what you want** and the model writes the prompt in the family's style (below), with its default negative; **give your own** ("use this prompt: …") and it passes it unchanged (`verbatim: true`).
- **Skip the model:** `/imagine score_9, score_8_up, source_anime, 1girl -- score_4, blurry --seed 42` ([Imagine options](COMMANDS.md#imagine-options)). *ComfyUI workflows offered* limits the choice.

| Family | Prompt style the model writes |
|---|---|
| Pony Diffusion XL | `score_9, score_8_up, score_7_up`, a `source_*` tag, then Danbooru tags, weighted like `(tag:1.3)` where it helps |
| Illustrious XL, NoobAI | `masterpiece, best quality, amazing quality, very aesthetic, absurdres`, then Danbooru tags |
| Juggernaut XL | A photographic description (subject, setting, light, lens) |
| Flux | Plain sentences |
| FLUX.2 dev | Long structured prose with hex colors |
| FLUX.2 Klein | A few clear sentences |
| Krea 2 | One rich paragraph |
| Z-Image Turbo | Concise prose |
| Qwen Image | Long prose with the exact words in quotes (it renders text well) |
| SD3.5 | Sentences plus style phrases |
| Ernie Image, LongCat Image | Rich prose with the words in quotes |
| Boogu | Descriptive sentences |
| HiDream I1 | Long, detailed prose |
| Ideogram 4 | A JSON prompt (description, style, color palette, laid-out elements) |
| SD 1.5, SDXL | Tags and weights |

#### Several-picture workflows (face swaps)

A workflow can take up to three pictures.

* **A face swap:** install the ReActor node pack, build two `LoadImage` nodes → `ReActorFaceSwap` → `SaveImage`, export and **Import** it. Name the roles in the `.md` (`image: the picture whose face is replaced`, `image2: the face to put in`).
* Then "put my face from [Image #2] on the person in [Image #1]" works in chat, or `/imagine faceswap --image target.png --image2 face.png`.
* A Qwen-Image-Edit graph needs `{{prompt}}` put in by hand.

#### The ComfyUI tools

| Tool | Arguments | What it does |
|---|---|---|
| `generate_image` | `prompt?, workflow?, negative?, negative_extra?, verbatim?, width?, height?, seed?, steps?, cfg?, denoise?, image?, image2?, image3?, count?` | Runs a workflow, saving 1 to *ComfyUI max images per call* pictures in *ComfyUI output folder* under the profile's `images` folder; they follow in the next message. |

* `image` is a working-directory path, a saved picture's full path, or a pasted `[Image #N]`. A refused run names the node and input at fault (a missing checkpoint, a bad value).

### YouTube

Under *YouTube tools*. Videos play where *YouTube open links in* says:

* The app's [video window](INTERFACE.md#the-video-window): it needs the WebView2 Runtime on Windows, macOS 14+ on a Mac.
* The Browser window, the default browser, or the Web UI's [YouTube tab](INTERFACE.md#the-youtube-tab) (with *Web UI YouTube controls* on).
* The app's players refuse a video YouTube won't let be embedded.

| Tool | Arguments | What it does |
|---|---|---|
| `youtube_search` | `query, max?, kind?, order?, length?, since?, region?, language?, live?, captions?, safe_search?` | Searches with *YouTube API key* (about 100 quota units) for embeddable videos, or playlists or channels (`kind`). Filters left out use *YouTube search order*, *YouTube safe search*, *YouTube region* and *YouTube language*. |
| `youtube_playlist` | `playlist?, max?` | A public playlist's videos (`max` 1–200, 50 by default), by id, link or channel; left out, the one playing. |
| `youtube_play` | `video?, start?, playlist?, entry?` | Plays a video by id or link from `start` (`90` or `1:30`), or a playlist from `entry`; cued instead with *YouTube autoplay* off. |
| `youtube_control` | `action, value?` | `play`, `pause`, `seek`, `forward` / `back` (10 s by default), `volume` (0–100), `mute`, `unmute`, `next` / `previous` / `entry`, or `close`. |
| `youtube_status` | — | What the player plays: video, state, position, volume and playlist entry. |
| `youtube_saved` | — | Your saved videos, numbered, with where each was left (or `watched`). |
| `youtube_save` | `action, video` | `add` a video (`current` for the player's), or `remove` one (also by number). |

* **The key:** searches and playlists need one, and each search spends daily quota; playing needs none. Watch Later and private playlists are out of reach (the app never signs in).
* **Saved videos** are your bookmarks, per profile, in `youtube.json`; one resumes a few seconds before where you left it, or starts over once seen to the end. `/youtube` (or `/youtube list`), `/youtube save` and `/youtube unsave` manage them yourself.
* **In a browser** the app can't see or control the video: `youtube_status` says only where it went. In the Web UI's tab, a video waits for a tab in view. A video YouTube won't embed goes to the browser through `open_url`.

### Obsidian

Under *Obsidian tools*, on the vault *Obsidian vault* names, working on its files directly (Obsidian needn't run). Notes are found by name, `[[wikilink]]`, alias or path, as Obsidian resolves a link: the linking note's own folder first, `./` and `../` from it.

| Tool | Arguments | What it does |
|---|---|---|
| `vault_search` | `query, tag?, folder?, max_results?` | Matching lines as `path:line`, and notes whose name or alias matches, within a tag or folder if given. |
| `vault_list` | `what?, folder?, tag?, property?, value?, max_results?` | Notes by folder, tag or property (`status: draft`), or every tag or property key with its count. |
| `vault_read` | `note, heading?, start_line?, max_lines?` | A note with its properties, one heading's section, or a window of lines. |
| `vault_links` | `note` | The note's links and embeds, and every backlink. |
| `vault_daily` | `date?, append?` | The daily note for a day (`today`, `+3`, `2026-09-22`), made from the vault's template if missing; `append` adds to it. Periodic Notes' daily settings win while it makes the daily notes. |
| `vault_write` | `note, content, mode?, heading?` | `create`, `overwrite`, `append` or `prepend`, to the note or one heading's section. |
| `vault_properties` | `note, set?, remove?` | Lists, sets or removes properties in one write; only the named keys change. |
| `vault_move` | `note, to` | Renames or moves a note and rewrites every link to it, in properties and canvases too; a move that fails puts the rewritten notes back. |
| `vault_delete` | `note` | Moves a note or attachment (never a folder) where the vault's *Deleted files* setting says: the system's bin or the vault's `.trash`, never deleted for good. Only while *Obsidian allow delete (.trash)* is on. |

* **Changes find their note strictly:** a write, move, delete or property change takes a path as written or a name, never an alias, and asks for the path when several notes share the name.
* **Limits:** 200,000 characters a write, 2 MB a note; a link or value shows 200 characters, a result 32,000. A link never resolves by an alias, as in Obsidian.

## Admin

Checks on the network, and on computers' own records and machinery.

* **Where they run:** the network tools and Active Directory work everywhere; on a Mac the event logs read the Mac's own logs; services and scheduled tasks are **Windows only**.
* Each family has a command (`/net`, `/eventlog`, `/services`, `/tasks`, `/ad`) to run them yourself, even with the tools off.

* **Where computers live:** in `eventlog.json`, `services.json` and `tasks.json`, and domains in `ad.json`, in the home's or profile's `configs` folder like the [database connections](#managing-connections); the profile's wins a name clash.
* **Nothing is reached until ticked** in its *… offered* setting. Its *add/edit* wizard adds and tests one.

### Network

Under *Network tools*. Small checks on one host at a time: is it up, what does it resolve to, is the port open, when does its certificate end. On a Mac, ping, trace and `net_certs` differ a little ([MAC.md](MAC.md#what-works)).

| Tool | Arguments | What it does |
|---|---|---|
| `net_dns` | `name, type?, server?` | A name's records: A (default) and AAAA through the system's resolver; CNAME, MX, TXT, NS, SOA, PTR, SRV and CAA from its DNS servers, or the `server` named (a host too). |
| `net_ping` | `host, count?` | Echoes (4 by default, 10 at most): answered or not, the loss and the round trips. |
| `net_trace` | `host, max_hops?, names?` | The route, hop by hop (30 at most, about 45 seconds); `names` looks each hop's name up. |
| `net_port` | `host, ports` | Whether TCP ports (16 at most) are open, refused or silent, on every address the host has. |
| `net_tls` | `host, port?, server_name?, starttls?` | A TLS handshake (port 443 by default): protocol, cipher, the certificate's names, issuer and days left, and what's wrong with its chain. Mail, LDAP and RDP ports speak their preamble first. |
| `net_certs` | `store?, location?, filter?, expiring_within_days?, limit?` | One of the certificate stores on the app's computer, soonest to end first; nothing exported. Only with *Network certificate stores* on. |

* **One host per call:** a name or an address. A range, block, list, URL, `host:port` or short-form address (`10.1`) is refused.
* **Where they may go:** *Network tools reach*, *Network tools allowed hosts* and *Network tools denied hosts* ([Network settings](SETTINGS.md#network)). Every address a name resolves to is judged; under `lan` only private ones are used.
* **Lookups too:** a `net_dns` question is judged like a host (a PTR by its address) and counts toward the budget.
* **A denied name** also denies the addresses it resolves to, so another name or the bare address won't reach it.
* **A budget per turn:** *Network tools max hosts per turn* and *Network tools max ports per turn*, spent by scripts and advisors too; bots have their own.
* **Nothing fetched, nothing sent:** a TLS check downloads no certificate and asks for no revocation list, and no credential is ever sent.
* **Server text** (TXT records, certificate names, banners) is fenced as untrusted data (`⟪ ⟫`), an answer held to 16,000 characters.

### Event logs

Under *Event log tools*. **Read-only:** they read what Windows recorded (a crash, a failed service start, a driver error, a failed sign-in, a PowerShell script block) on the computer running NeonSidekick, in `.evtx` files in the working directory, and on remote computers you name.

| Tool | Arguments | What it does |
|---|---|---|
| `eventlog_logs` | `filter?, all?, computer?` | The computers offered; a computer's logs with records, size and last write (`all` adds empty, disabled, Analytic and Debug ones); the `.evtx` files in the working directory. |
| `eventlog_search` | `log, level?, id?, provider?, field?, since?, until?, audit?, text?, before_record?, limit?, computer?` | A log's events newest first, a line each, ending with the exact call for the next page. |
| `eventlog_summary` | `log, since?, level?, computer?` | Counts by provider and id since a time (a day by default), the top 30, each with its newest time and a sample message. |
| `eventlog_event` | `log, record, computer?` | One event in full: system values, message, named fields, and (under `full`) its XML. At most 32,000 characters. |

* **The log:** a name `eventlog_logs` lists (`app`, `sys`, `sec`, `setup`, `ps` for PowerShell/Operational), or an `.evtx` path in the working directory (never with `computer`).
* **Default view:** with no `level` or `audit`, Security shows its audit failures, other logs their critical events, errors and warnings.
* **Filters:** `level` (`critical,error,warning,information,verbose` or `all`), `id` (`41,1074`, `4700-4799`), `provider`, `since`/`until` (`2h`, or a time), `audit`, `field` (`Name=value` in EventData) and `text` (provider, fields, message). At most 20 terms.
* **Limits:** *Event log max events* per page and *Event log scan limit* per call; the answer says how far back it got.
* **Forwarded logs:** in ForwardedEvents each computer keeps its own record numbers, so a record carries a tag (`1200@3f9a2c1b`). Copy it whole into `before_record` or `record`.
* **Security** needs the Event Log Readers group, or the app run as administrator. Analytic and Debug logs read oldest-first only and can't be searched.

#### Event log computers

`local` is the app's computer, built in, and the only one ticked in *Event log computers offered* until you change it; that setting governs the model and `/eventlog` alike. *Event log add/edit computer* adds and tests one; *Event log set password* changes a runas password.

* **`host`** (required): the name or address (`localhost` is a loopback session).
* **`auth`**: `windows` (default, as you) or `runas`, with **`user`** (`DOMAIN\name`, `name@domain`, or `box\admin` for its own account) and **`passwordStore`** `file` or `credman` (`NeonSidekick/eventlog/<name>`). **`authMethod`**: `default`, `negotiate`, `kerberos` or `ntlm`.
* **`logs`**: the logs allowed, case ignored, `*` matching anything but `/` (`Microsoft-Windows-*/Operational`). Absent: all.
* **`detail`**: `redacted` (default) or `full` ([Who sees what](#who-sees-what)). **`description`**: for the model. An entry named `local` may set only `logs`, `detail` and `description`.

```json
{
  "computers": {
    "local": { "logs": ["Application", "System", "Setup", "Microsoft-Windows-*/Operational"] },
    "fs01": { "host": "fs01.corp.example", "description": "file server", "detail": "full" },
    "dc01": { "host": "dc01", "auth": "runas", "user": "CORP\\svc-logreader", "passwordStore": "credman", "logs": ["Security", "System", "Directory Service"] }
  }
}
```

**A remote computer needs** the "Remote Event Log Management" firewall rule on and the account in its Event Log Readers group (UAC filters a local administrator's network sign-in). `--eventlog-check <computer>` tests one.

#### Who sees what

* **`redacted`** hides secret-looking values: fields named like a password, secret, API key, private key or connection string.
* **In free text** it hides URL passwords, password switches and arguments (`password=…`, `-Password x`, `/pass:x`, `-p x`, `net user`/`net use` passwords, `schtasks /RP`), `ConvertTo-SecureString … -AsPlainText`, `PSCredential`, `$password = '…'`, `Authorization:` headers, JWTs and PEM blocks.
* The message is built from the hidden values, and `text` and `field` match only those. **It is best-effort:** no rule knows every secret in free text.
* **`full`** is read only by the main chat. Bots, advisors and `execute_code`'s bridge always read redacted; an advisor's recent lines name an event-log call, never its result.
* **A `.evtx` file takes `local`'s detail, and no `logs` list applies to it:** a log exported from a `redacted` computer and read as a file is read under `local`'s detail.
* **Out of any rule's reach:** what the main model quotes, and saved sessions. Set `full` only where you are content to have the events anywhere the conversation goes.
* **Event text is untrusted:** any program can write an event. Messages and values are fenced as quoted data (`⟪ ⟫`), never instructions, with control characters replaced.
* An event from an unregistered source says "source not registered: any program could have written this".

#### Event logs on a Mac

The same four tools read the Mac's own logs: the unified log and the report files. No remote computers, `runas` or `.evtx`.

| Log | What it is | Default window |
|---|---|---|
| `system` | The unified log. `level` is `fault`, `error`, `default`, `info`, `debug` or `all` (default `fault,error`); `process`, `pid`, `subsystem` (a prefix), `category`, `text` and `exclude_process` narrow it. | the last hour |
| `crashes` | Crash, jetsam and resource reports in `~/Library/Logs/DiagnosticReports` and `/Library/Logs/DiagnosticReports` (with `Retired`). Handle: the file name (`user:` or `system:` before it when both folders hold that name); `eventlog_event` shows the exception and the crashed thread's top 30 frames. A link or a pipe named like a report is never read. | the last 7 days |
| `install` | `/var/log/install.log`: software installs and updates. | the last day |
| `<name>.logarchive` | A saved unified log in the working directory (`log collect` makes one), read like `system`. | the archive's last hour (before its newest entry) |

* **The unified log is vast,** so a read keeps to its window and *Event log scan limit*, says how far back it got, and names a process that filled most of it (with the `exclude_process` to drop it). With `until` and no `since`, the window is the hour (day, week) before `until`.
* **`local` unticked** in *Event log computers offered*: `system`, `crashes` and `install` are refused, and only `.logarchive` folders are read.
* **Detail:** only the `local` entry applies; `redacted` (the default) also writes your home folder as `~`. `crashes` and `install` ignore `level`, `subsystem` and `category`, and say so.
* **Permissions:** the unified log needs an administrator account, the system's report folder the `_analyticsusers` group (administrators are in it). A folder you can't read is named. `--eventlog-check local` tests them.

### Services

Under *Service tools*. The model reads the services on the computer running NeonSidekick and the computers you tick. With *Service writes* it may start, stop or restart one, or change how it starts, asking you each time.

| Tool | Arguments | What it does |
|---|---|---|
| `service_list` | `computer?, state?, start?, filter?, drivers?, limit?, offset?` | A line per service: name, display name, state, process, a failure's exit code. `state` is `running`, `stopped` or `failed`; `start` is `automatic`, `delayed`, `manual` or `disabled`. |
| `service_info` | `name, computer?` | One service whole: state, start type, account, program, recovery actions, what it depends on and what depends on it. |
| `service_control` | `name, action, computer?` | `start`, `stop` or `restart`, after your yes; only the actions every ticked readwrite computer allows. |
| `service_start_type` | `name, start_type, computer?` | `automatic`, `delayed`, `manual` or `disabled`, after your yes. Off in a new profile's Offered tab. |

* **A change passes these checks in order**, the first failure being the answer:
  * *Service writes* → `access` → the visible list → `control` → never a driver → the app's protected list → the computer's `protected` → `controlActions`.
  * Then running dependents: a stop or restart names them and is refused. Services outside the visible list are counted, never named.
  * Last, your yes, showing the service, computer, account and full program path. The model's change is refused when that path is too long to show whole.
* **A restart** whose stop doesn't finish within 60 s, or that you stop waiting for, isn't started again; the answer says so.
* Headless and the bots never get the two changing tools.
* **Protected by the app, always:**
  * RPC and DCOM, the session manager, security accounts, the event log, WMI, the firewall and Defender, the Task Scheduler.
  * File sharing, Netlogon, DNS and DHCP clients, Remote Desktop, crypto and sign-in services, and a few more.
  * They can be started, never stopped, restarted or changed; a computer's `protected` only adds to the list.
* **Rights:** reading needs nothing special on the app's computer; a change needs the app run as administrator, or rights on the service.
* Some services (RPC) hide their dependents from an unelevated account: `service_info` says so, and Windows refuses a stop that would break them.

```json
{
  "computers": {
    "local": { "access": "readwrite", "control": ["Spooler", "MyApp*"], "controlActions": ["start", "restart"] },
    "app01": { "host": "app01.corp.example", "description": "app server", "services": ["MSSQL*", "W3SVC", "MyApp*"],
      "access": "readwrite", "control": ["MyApp*"], "protected": ["MSSQL*"], "detail": "full" }
  }
}
```

The keys of `services.json` and `tasks.json`:

* **`host`**, **`auth`**, **`user`**, **`passwordStore`**, **`credential`**: as `eventlog.json`'s (`NeonSidekick/services/<name>`, `NeonSidekick/tasks/<name>`).
* **`access`**: `read` (default) or `readwrite`; nothing is changed on a `read` computer. **`control`**: what may be changed (`*` matches anything). Absent: nothing.
* **`controlActions`**: the actions allowed (absent: all; `[]`: none). Services: `start`, `stop`, `restart`, `start_type` (`restart` listed on its own). Tasks: `run`, `end`, `enable`, `disable`.
* **`detail`**: `redacted` (default: secret-like values in program paths and command lines hidden) or `full`, read only by the main chat. **`description`**: for the model.
* **Services' own:** `services` (the visible ones; absent: all) and `protected`. **Tasks' own:** `folders` (the visible ones; a pattern ending in `\` means that folder's own tasks) and `privileged`.
* `local` is the app's computer, built in and read-only until its entry says otherwise; it takes every key but the sign-in ones.

**A remote computer needs:**

* An account with rights there. UAC filters a local administrator's network sign-in, so use a domain account in its Administrators group.
* The "Remote Service Management" firewall rule for services, "Remote Scheduled Tasks Management" for tasks. `--services-check <computer>` and `--tasks-check <computer>` test one ([COMMANDLINE.md](COMMANDLINE.md)).

### Scheduled tasks

Under *Task tools*. The model reads the scheduled tasks on the computer running NeonSidekick and the computers you tick. With *Task writes* it may run, end, enable or disable one, asking you each time.

| Tool | Arguments | What it does |
|---|---|---|
| `task_list` | `computer?, folder?, recursive?, state?, filter?, include_microsoft?, limit?, offset?` | A line per task: path, state, last run and result, next run. Windows' own under `\Microsoft\` only with `include_microsoft`. |
| `task_info` | `path, computer?` | One task whole: triggers in words ("every week on Monday at 02:30"), actions, who it runs as and how, last result, time limit. |
| `task_control` | `path, action, computer?` | `run`, `end`, `enable` or `disable`, after your yes. Nothing is ever created, deleted or rewritten. |

* **The order of checks:** *Task writes* → `access` → `folders` → `control` → a task of Windows' own (only when a `control` pattern starting `\Microsoft\` matches it) → `controlActions` → **privileged** → your yes.
* **Privileged:** a task that runs as SYSTEM, a service account, a group, any account but the one the app signs in as, or with highest privileges. Running, ending or enabling one needs the computer's `"privileged": true`; disabling doesn't. A definition that can't be read counts as privileged.
* **A run's question** shows who it runs as, at what level, and every command line in full (never redacted: you decide on them). The model's run is refused when they're too long to show whole.
* **History:** `/tasks`' *history* button opens the runs on `/eventlog`, when Task Scheduler's *Enable All Tasks History* is on. Times are the computer's own clock.

### Active Directory

Under *Directory tools*. **Read-only:** the app's directory client holds no call that writes. The model looks up users, groups, computers and OUs in the domains you tick, and finds locked, expiring, stale and privileged accounts.

| Tool | Arguments | What it does |
|---|---|---|
| `ad_user` | `name, domain?` | A user's card: enabled, locked and when (the source machine: Security event 4740 on the PDC emulator), bad passwords, password and account expiry, last sign-in, title, department, manager, reports, OU, flags, groups. |
| `ad_group` | `name, nested?, members?, limit?, to?, domain?` | A group's card (type, owner, mail, created, its own groups) and its members, direct or `nested`; `members` is `all`, `users`, `computers` or `groups`. |
| `ad_computer` | `name, domain?` | A computer's or managed service account's card: OS, enabled, domain controller or not, last sign-in, machine password, OU, owner, groups. |
| `ad_find` | `kind, days?, words?, type?, field?, ou?, limit?, to?, domain?` | A structured search; the kinds are below. |
| `ad_ou` | `ou?, limit?, to?, domain?` | The OU tree with descriptions, or one OU: owner, linked GPOs, and the objects directly in it, counted by kind. |
| `ad_domain` | `domain?` | Its domain controllers, functional levels, role holders, password and lockout policy, and the fine-grained policies this account may see. |

`ad_find`'s kinds:

| kind | What it finds | Takes |
|---|---|---|
| `name` | names (account, display name, cn, mail, UPN) or descriptions holding `words`; users, groups and computers, or the one `type` | `words`, `type`, `ou` |
| `field` | a `type`'s (user by default) `field` holding `words`: `department`, `title`, `company`, `office` or `description` | `words`, `field`, `type`, `ou` |
| `os` | computers whose operating system holds `words` | `words`, `ou` |
| `locked`, `disabled`, `password_never_expires` | users in that state | `ou` |
| `password_expiring` | passwords ending within `days` (14) | `days`, `ou` |
| `account_expiring` | accounts ending within `days` (30); the already expired listed separately | `days`, `ou` |
| `stale_users`, `stale_computers` | not signed in for `days` (90); the never-signed-in listed separately | `days`, `ou` |
| `created` | objects made in the last `days` (30): users, groups and computers, or the one `type` | `days`, `type`, `ou` |
| `privileged` | the members at any depth of the protected groups | — |
| `empty_groups` | groups with no members that are nobody's primary group and aren't system groups | `ou` |

* **`type`**: `user`, `group`, `computer`, `ou`, `contact` or `service` (a managed service account); a bare `name` search leaves the last three out, and all but users have only the `description` field.
* **`ou`**: a path (`Neon/Engineering`; a `/` in a name as `\/`, a `\` as `\\`), a DN or a name; one several OUs share is refused with their paths.
* **Members:** `nested` lists members at any depth with the group they come through (`via`), primary-group members too; members of nested groups in another domain aren't listed. Contacts, service accounts, foreign principals and unreadable ones show only under `members: all`.
* **`privileged`**: Domain, Schema, Enterprise, Key and Enterprise Key Admins, Administrators, and the Account, Server, Print and Backup Operators and Replicator groups. Listed apart: objects marked `adminCount=1` that no protected group reaches now.
* **Caps:** a search stops at 1,000 entries (10,000 for a group's members and for `to`), a call at 40 seconds, and says which cut it. Last sign-in can be about 14 days stale.
* **Never read:** passwords and their hashes, LAPS and BitLocker recovery values, managed and key credentials, certificates and photos.
* **`redacted`** (the default) also leaves out phones, addresses, employee numbers and home folders, and refuses `office`; `full` is the main chat's alone.
* **`to`** saves the whole list as a UTF-8 CSV: a `.csv` path or folder in the working directory (named from the question, `ad-group-staff.csv`). Never over an existing file; formula-like cells are guarded. Needs *File tools*, never in plan mode.

**Domains:** `domain` takes a ticked entry's name or its DNS name; left out, `current` when ticked, else the only one. **`current`** is the domain the app's computer is joined to, if any (built in, as you, Kerberos). Other domains go in `ad.json`:

```json
{
  "domains": {
    "corp": { "domain": "corp.example", "server": "dc01.corp.example", "description": "the office" },
    "lab": { "domain": "lab.example", "server": "localhost", "auth": "runas", "user": "LAB\\reader", "passwordStore": "credman" }
  }
}
```

* **`domain`** (required): its DNS name. **`server`**: a domain controller (default: the domain's name). **`baseDn`**: where to search (default: the domain).
* **`tls`**: `true` for LDAPS on 636, the certificate checked against the trust store on the app's computer; otherwise 389, signed and sealed.
* **`auth`**, **`user`**, **`passwordStore`** (`NeonSidekick/ad/<name>`), **`detail`**, **`description`**: as `eventlog.json`'s. Outside the domain, `runas` signs in with NTLM.
* **A refused `runas` password is held:** it isn't tried again this run (repeated tries can lock the account), until *Directory set password* saves a new one.
* Configured domains are your own entries: *Network tools reach* doesn't judge them. `--ad-check <domain>` tests one.

#### Active Directory on a Mac

The same tools, `/ad` and `--ad-check`, read-only as on Windows.

* **Kerberos only.** `windows` uses your default ticket: run `kinit you@CORP.EXAMPLE` first (`klist` shows it). `runas` takes `name@REALM`, the realm in capitals; a `DOMAIN\name` is refused when used, so an `ad.json` shared with a PC still loads. Its password is a Keychain item (`NeonSidekick/ad/<name>`).
* **No `current`:** name each domain in `ad.json`. A DC is the `server` when given, else found in DNS (`_ldap._tcp.dc._msdcs.<domain>`), else the domain's own name.
* **Name the DC itself:** the sign-in asks for `ldap/<the name connected to>`, as Windows does. An alias, `localhost` or an IP has no such name, and neither does the domain's own name when DNS lists no DCs, so give `server` the DC's full name (a hosts entry can point it at another address).
* **Sealed:** on 389 always signed and sealed (a DC offering neither is refused); on LDAPS the DC's certificate must be trusted in the Keychain. A DC auditing unsigned binds (event 2889) logs these sealed ones too.
* **A `runas` ticket** goes in its own cache, which macOS may make the default when you have none (`klist -l`); `kdestroy -p <name@REALM>` removes it.
* **A refused password or an unreachable KDC:** macOS doesn't say why a `runas` sign-in got no ticket, so the app asks the DC's own KDC (port 88) about the account, sending no password.
  * It knows the account, or says the account is locked or disabled: the password is held as refused.
  * Its clock is more than 5 minutes off yours: the clock is named. Nothing is held.
  * It doesn't know the account: the account is named. Nothing is held.
  * Nothing answers: the KDC is named unreachable, also when a firewall blocks 88 but not 389. Nothing is held.
  * A failure after the ticket (the DC's service name, its reply) is never held.
* **A test realm** with no DNS records needs its KDC in a `krb5.conf` named by `KRB5_CONFIG`: `[realms] LAB.NEON.TEST = { kdc = tcp/127.0.0.1:1088 }`.

### What the shell may not do while these are on

While *Service tools*, *Task tools* or *Directory tools* is on, with *Shell police*, a `run_command` line, a script file it runs, an `execute_code` script or a `process` write that would change one of them is refused before any approval, whatever *Shell command policy* says.

* **Refused:** changing a service (`sc stop`, `net stop`, `Restart-Service`, a `ServiceController`'s `.Stop()`, WMI's `StopService`, its registry key…), a task (`schtasks /run`, `Disable-ScheduledTask`, the `Schedule.Service` object…) or the directory (`Set-ADUser`, `Unlock-ADAccount`, `dsmod`, `ldapmodify`, a changing `net user … /domain`…). On a Mac also:
  * `dscl` writing (`-create`, `-createpl`, `-delete`, `-passwd`, `-mcxset`…, with or without the dash) anywhere but the local node (`.`, `/Local/…`), or on a line naming `/Active Directory` or `/Search`.
  * `dscl` reading its commands from its input (no command, `< file`, `| dscl`).
  * `dsconfigad` with any option but `-show` and `-xml`.
  * `dseditgroup -o edit|create|delete` on a directory node.
* **Reading passes** (`sc query`, `Get-Service`, `schtasks /query`, `Get-ADUser`); changes go through the tools and your yes. Like the forbidden strings, it's a tripwire, not a sandbox.

## Databases

### How the database tools work

Five families: SQL Server, MySQL/MariaDB, SQLite, PostgreSQL and Oracle. Each has its tab under `/tools` › Databases and its switch (*SQL tools*, *MySQL tools*, *SQLite tools*, *PostgreSQL tools*, *Oracle tools*).

They only read unless you turn on [read-write mode](#read-write-mode).

**Reading** runs one statement behind three layers:

1. **The gate** passes only a single read statement (`SELECT`, or a `WITH` ending in one). A second statement, changes, locking reads and the engine's ways out (files, links to other servers, dynamic SQL) are refused before the server sees anything.
2. **The session or transaction** is read-only and always rolled back.
3. **The account is the real guard.** Give the login read-only grants.

**One place per connection:** each connection reaches **one database** (Oracle: one schema), nothing else on the server.

* No tool takes a database or lists the server's; for another database, add a connection. A statement naming one (`payroll.dbo.salaries`), or the server's own catalog (logins, sessions), is refused before the server; rename an alias spelled like a database.
* A view, synonym or routine that reads another database can't be seen in the text: that stays the account's to guard.

Values go in as `params`. Results are cut at *SQL query result max chars*; `max_rows` is 1–100,000, the family's *… max rows* when left out.

#### Managing connections

* **The file:** `sql.json`, `mysql.json`, `sqlite.json`, `postgres.json` or `oracle.json`. The home's (`%USERPROFILE%\.neonsidekick\configs\`, `~/.neonsidekick/configs/` on a Mac) serves every profile; the profile's (`…\profiles\<profile>\configs\`) wins a name clash. Comments and trailing commas are allowed.
* **The wizard:** *… add/edit connection* (*SQLite add/edit database*). Its test writes nothing: it signs in, warns when the account could change data, and names what else it could reach.
* ***… connections offered*** (*SQLite databases offered*) picks what the model sees: **nothing is offered until you tick it**. *… set password* changes a password.
* **Passwords:** `passwordStore` `file` (default) encrypts a password typed into the file in place (DPAPI) at the next read; `credman` keeps it in Windows Credential Manager as `NeonSidekick/<family>/<connection_name>` (`sql`, `mysql`, `postgres`, `oracle`). On a Mac both use the Keychain ([What differs](MAC.md#what-differs)).

#### Read-write mode

Changes go through each family's `_execute` tool, which needs **two keys**: the family's *… mode* `read-write` **and** the connection's `"access": "readwrite"` (SQLite has only the first), checked on every call. It is **never offered headless or in plan mode**.

1. **The kinds:** *… statements allowed* ticks which kinds may run; a statement needs every kind it does (a `WITH` that deletes and inserts needs both).

   | Kind | Statements |
   |---|---|
   | `data` | `INSERT`, `UPDATE`, `MERGE` (`REPLACE` in MySQL and SQLite), upserts, and `RETURNING`/`OUTPUT` |
   | `delete` | `DELETE`, `TRUNCATE`, a `MERGE` that deletes |
   | `create` | `CREATE TABLE`, `INDEX`, `VIEW`, `SEQUENCE` and the like; SQL Server's `SELECT … INTO`; SQLite's `TRIGGER` and `VIRTUAL TABLE` |
   | `alter` | `ALTER`, `RENAME`, `COMMENT ON`; `CREATE OR REPLACE VIEW` needs `create` too |
   | `drop` | `DROP` of those, and of routines and triggers |
   | `upkeep` | statistics and index upkeep (`UPDATE STATISTICS`, `ANALYZE`, `OPTIMIZE`, `VACUUM`, `REINDEX`…) |
   | `procedures` | server databases: running code (`EXEC`, `CALL`, PostgreSQL's `DO`, an Oracle PL/SQL block) and making or changing a procedure, function or trigger |
   | `pragma` | SQLite only: `PRAGMA` |
   | `read` | `SELECT` (and `VALUES`, `TABLE`, `EXPLAIN` where the engine has them): never asks, runs on the read-only path |

   In SQL Server a batch's lone first word reads as `EXEC` of it, so a typo there is a procedure call. A SQLite trigger also needs its body's kinds.
2. **The gate:** one statement per call (a routine's body counts as one). Always refused, for example: transaction words, `SET`/`USE`, `GRANT`/`REVOKE`, users and roles, creating or dropping databases, dynamic SQL, file access (`BULK INSERT`, `LOAD DATA`, `COPY`, `ATTACH`) and links to other servers.
3. **Your yes:** every change asks first, showing the statement (`v` shows all of it): *Deny*, *Allow once*, or *Allow for this session* (that connection or file only, until `/new`, `/clear` or a profile switch).
4. **The run** commits on its own; a failed, timed-out or canceled one changes nothing. Every change is logged with the connection, rows changed and statement.
5. **The account is still the real guard.** Give a `readwrite` connection only the permissions you want used.

`--sql-check`, `--mysql-check`, `--sqlite-check`, `--postgres-check` and `--oracle-check` test a family against a real server or file ([COMMANDLINE.md](COMMANDLINE.md)).

#### The tools

Server families take a connection in `connection`, SQLite a database in `database` (a name or a file's path); either may be left out when there is only one.

| Tool | Arguments | What it does |
|---|---|---|
| `sql_connections` / `mysql_connections` / `postgres_connections` / `oracle_connections` | — | The named connections and what each holds, touching no server. |
| `sqlite_databases` | — | The named databases, and whether working-directory files may be named; opens nothing. |
| `postgres_schemas` | `connection?` | The schemas the account may use, with table counts and owners. |
| `sql_tables` / `postgres_tables` | `connection?, schema?, pattern?` | Tables and views as `schema.name`, with kind, rows and description; `pattern` may be a `LIKE` pattern. |
| `mysql_tables` / `oracle_tables` | `connection?, pattern?` | The same, as `database.name` (Oracle: `SCHEMA.NAME`). |
| `sqlite_tables` | `database?, pattern?` | Tables and views with their kind. |
| `sql_columns` / `postgres_columns` | `pattern, connection?, schema?` | Every column whose name matches (`%CustomerID`), with table, type and description. |
| `mysql_columns` / `oracle_columns` | `pattern, connection?` | The same. |
| `sql_describe` / `mysql_describe` / `oracle_describe` | `table, connection?` | One table or view: columns, keys, foreign keys both ways, indexes, CHECK constraints and triggers. |
| `postgres_describe` | `table, connection?, schema?` | The same, without triggers. |
| `sqlite_describe` | `table, database?` | Columns, foreign keys both ways, indexes and the `CREATE` statement. |
| `sql_relationships` / `mysql_relationships` / `oracle_relationships` | `connection?, table?` | Foreign-key join paths, all or touching one table. |
| `postgres_relationships` | `connection?, schema?, table?` | The same, for a schema or a table. |
| `sql_indexes` | `connection?, table?, schema?, missing?` | Indexes with columns, size and use (needs `VIEW SERVER STATE`); `missing` adds the optimizer's suggestions. |
| `mysql_indexes` / `oracle_indexes` | `connection?, table?` | Indexes with key columns, estimates, and use where readable. |
| `postgres_indexes` | `connection?, schema?, table?` | Indexes with their definition and scans. |
| `sql_query` / `mysql_query` / `postgres_query` / `oracle_query` | `sql, connection?, params?, max_rows?` | One read-only `SELECT`; `params` is an object of values (`{"id": 43659}`). |
| `sqlite_query` | `sql, database?, params?, max_rows?` | One read-only `SELECT`, `WITH` or `VALUES`. |
| `sql_execute` / `mysql_execute` / `postgres_execute` / `oracle_execute` | `sql, connection?, params?, max_rows?` | [Read-write mode](#read-write-mode) only: one changing statement; answers with the rows changed and returned. |
| `sqlite_execute` | `sql, database?, params?, max_rows?, create?` | The same; `create: true` makes a new database file. |

### SQL Server

`sql.json`; no ODBC driver needed.

* **`server`**: `host`, `host,port` or `host\instance`. **`database`** (required): the one database it reaches.
* **`auth`**: `sql` (a SQL login: `user`, `password`), `windows` (your account) or `runas` (another account, `DOMAIN\name` or `name@domain`, with a password used only to sign in). On a Mac both Windows sign-ins are Kerberos only ([SQL Server sign-in](MAC.md#sql-server-sign-in)), and `np:` servers can't be reached.
* **`encrypt`**: `strict`, `mandatory` (default) or `optional`; **`trustServerCertificate`**: `true` accepts a self-signed certificate.
* **`connectTimeoutSeconds`**: 1–120 (default 15). **`access`**: `read` (default) or `readwrite`. **`passwordStore`**: `file` or `credman`.

```json
{
  "connections": {
    // SQL Auth: password encrypted in place after the first read
    "adventureworks": { "server": "127.0.0.1,1433", "database": "AdventureWorks2022", "auth": "sql", "user": "reader",
      "password": "type-password-here-once", "trustServerCertificate": true, "description": "Sample sales database" },
    // RunAs Auth: another account, its password in Windows Credential Manager
    "reports-admin": { "server": "sqlhost01.example.com,1453", "database": "Reports", "auth": "runas",
      "user": "CONTOSO\\svc-reader", "passwordStore": "credman" }
  }
}
```

* `params` binds `@name`. The gate is SQL Server's own parser; refused, for example: batches, `EXEC`, `INTO`, linked servers, `OPENROWSET`.
* Server-wide views and functions (`sys.databases`, `sys.dm_*`, logins) are refused; the database's own catalog (`sys.objects`, `INFORMATION_SCHEMA`) reads. CLR types (`geography`, `hierarchyid`) need `.ToString()`.

### MySQL and MariaDB

`mysql.json`; MySQL 8.0.16+ and MariaDB 10.2+.

* **`host`**, **`port`** (3306 by default), **`database`** (required), **`user`**, **`passwordStore`** `file` or `credman`, **`connectTimeoutSeconds`** (1–120, default 15), **`access`** (`read` or `readwrite`).
* **`sslMode`**: `preferred` (default), `required`, `verify-ca`, `verify-full` or `none`. **`allowPublicKeyRetrieval`**: `true` only for a `caching_sha2_password` account without TLS (a man in the middle could supply its own key).

```json
{
  "connections": {
    "shop": { "host": "localhost", "database": "shop", "user": "shop_reader", "password": "type-password-here-once", "description": "The sample retail database" },
    "billing": { "host": "db01.example.com", "database": "billing", "user": "billing_ro", "passwordStore": "credman", "sslMode": "verify-full" }
  }
}
```

* Queries end with `LIMIT n`, and `params` binds `@name`.
* Refused, for example: executable comments (`/*! … */`), `INTO`, `LOAD_FILE`, `USE`, and `information_schema`, `performance_schema`, `mysql` and `sys` as qualifiers.
* In a `CREATE PROCEDURE` or `TRIGGER` body, a column or label named `begin` or `end` must be in backticks.

### SQLite

The SQLite tools read database **files**: no server, no password. Changes need only *SQLite mode*.

* **A database** is a name from `sqlite.json` (a relative `path` counts from that file's `configs` folder) or, with *SQLite sandbox files* on, any file in the working directory.
* *SQLite add/edit database* tests one by opening it read-only.

```json
{ "databases": { "chinook": { "path": "D:\\data\\chinook.db", "description": "The music store sample" } } }
```

* `params` binds `@name`, `:name`, `$name` or `#name`; positional `?` is refused.
* **Reading** opens the file read-only, whatever the mode; a statement past *SQLite query timeout (s)*, or stopped with Esc, is interrupted. **Always refused:** `ATTACH`/`DETACH`, `VACUUM INTO`, `writable_schema` and `load_extension()`.
* **The rest of the app keeps out** while *SQLite tools* is on:
  * The shell police refuses a command or script that reaches SQLite.
  * The file tools never change a database file (`.db`, `.db3`, `.sqlite`, `.sqlite3`, their journals, or one `sqlite.json` names); reading and copying one still work.
* **Creating a database:** `create: true` needs `create` ticked and *SQLite sandbox files* on; the file ends `.db`, `.sqlite`, `.sqlite3` or `.db3`, in an existing folder of the working directory. If the statement fails, the file is removed.

### PostgreSQL

`postgres.json`.

* **`host`**, **`port`** (5432 by default), **`database`** (required), **`user`**, **`passwordStore`** `file` or `credman`, **`connectTimeoutSeconds`** (1–120, default 15), **`access`** (`read` or `readwrite`).
* **`schema`** (optional): holds the connection to that schema; other schemas and `information_schema` are refused. Without it, every schema of the database is open.
* **`sslMode`**: `prefer` (default), `require`, `verify-ca`, `verify-full` or `disable`.

```json
{
  "connections": {
    "shop": { "host": "localhost", "database": "shop", "user": "shop_reader", "password": "type-password-here-once", "description": "The sample retail database" },
    "billing": { "host": "db01.example.com", "database": "billing", "schema": "invoices", "user": "billing_ro", "passwordStore": "credman", "sslMode": "verify-full" }
  }
}
```

* Queries end with `LIMIT n`, and `params` binds `@name`; positional `$1` is refused.
* Refused in queries, for example: `COPY`, `DO`, `dblink`, functions that reach files or other backends, and server-wide views (`pg_database`, `pg_roles`, `pg_stat_activity`).
* The wizard warns of a superuser role but doesn't refuse it. Every role can connect to a new database unless `CONNECT` is revoked from `PUBLIC`; the test names the databases it could reach.

### Oracle

`oracle.json`; no Oracle Client needed. A connection is held to **one schema** rather than a database.

* **`dataSource`**: EZConnect `host:port/service` (`localhost:1521/FREEPDB1`) or a whole `(DESCRIPTION=…)`. **`schema`**: the one it is held to (default: the user's own).
* **`user`**: the database user. `SYS` (and any `AS SYSDBA` sign-in) is refused: Oracle doesn't hold SYS to a read-only transaction.
* **`connectTimeoutSeconds`**: 1–120 (default 15). **`access`**: `read` (default) or `readwrite`. **`passwordStore`**: `file` or `credman`.

```json
{
  "connections": {
    "hr": { "dataSource": "localhost:1521/FREEPDB1", "user": "hr_reader", "password": "type-password-here-once", "schema": "HR" },
    "ledger": { "dataSource": "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=dbhost01.example.com)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=LEDGER)))",
      "user": "ledger_ro", "passwordStore": "credman" }
  }
}
```

* Queries end with `FETCH FIRST n ROWS ONLY` and no `;`, and `params` binds `:name`. `oracle_execute` answers with the rows changed only, refuses `RETURNING … INTO`, and counts a PL/SQL block as one statement.
* On 23ai and later the session itself is read-only for reads, so the server refuses any write. Give the account `SELECT` grants (or `ALTER USER … READ ONLY` on 23ai).
* Refused, for example: `FOR UPDATE`, database links, `UTL_HTTP`/`UTL_FILE`/`DBMS_SQL`, and whole-database views (`ALL_*`, `DBA_*`, `CDB_*`, `V$`); `USER_*` views work.
* A CLOB or BLOB shows its start and length; object types (`SDO_GEOMETRY`, `XMLTYPE`) need converting to text.

## Devices

### Camera

Under *Camera tool*. Not offered headless, or when the embedded model can't see pictures. On a Mac it needs macOS 14+ and your terminal's camera permission ([MAC.md](MAC.md#install-and-first-run)).

| Tool | Arguments | What it does |
|---|---|---|
| `camera_capture` | `prompt` | Shows the model's request ("Hold the label up"), then waits for you to take the photo or allow it (*Camera shutter*). Saved in *Camera output folder* under the profile's `images` folder. |

* A declined photo isn't asked for again that turn. See also [Camera](COMMANDS.md#camera).

### Screen

Under *Screen tools*. Not offered headless, or when the embedded model can't see pictures. On a Mac it needs macOS 14+ and your terminal's Screen Recording permission ([MAC.md](MAC.md#install-and-first-run)).

| Tool | Arguments | What it does |
|---|---|---|
| `screen_capture` | `target?, prompt` | Captures a monitor, every monitor or a window ([Screen capture](COMMANDS.md#screen-capture)), asking first per *Screen policy*. Saved in *Screen output folder* under the profile's `images` folder. |
| `screen_list` | — | The monitors and windows with their targets; titles and sizes only. |

* A denied capture isn't asked for again that turn.

### Printing

Under *Print tools*. `/print` and these tools print on any installed printer; *Print action policy* decides whether `print_file` asks first, and Esc cancels a print.

* **Drawn by the app:** text and code as a listing, Markdown formatted, a picture (PNG, JPEG, GIF, WebP, BMP) on one page; each page headed with the file's name and *page N of M*.
* **Anything else on Windows** (a PDF, an Office file) goes to its own program on the default printer, with no choice of printer, copies, pages or landscape. On a Mac: [What differs](MAC.md#what-differs).

| Tool | Arguments | What it does |
|---|---|---|
| `list_printers` | — | The printers, marking the system's default and *Print default printer*. |
| `print_file` | `path, printer?, copies?, pages?, landscape?` | Prints a working-directory file on a printer (else *Print default printer*, else the default), 1–10 copies, pages like `1-3,5`. |

#### Making PDFs

`/pdf` and `convert_to_pdf` make a PDF in the working directory.

* **Sources:** Markdown (pictures from the working directory only), text and code, a picture (PNG, JPEG, GIF, WebP, BMP or SVG; convert HEIC or TIFF with `image_edit` first), an HTML file (scripts removed), or a web page (when *Web browser network mode* allows it). A PDF, or an address serving one, is refused, pointing to `read_pdf`, `pdf_edit`, `web_fetch` or `download_file`.
* **Pages:** US Letter by default (`paper` `a4` or `legal`), portrait unless `landscape`, an HTML file's own page size overridden; a web page keeps its own layout. A PDF made over 100 MB is not kept.
* **What a print reaches:** a web page only its own host, at the address *Web browser network mode* approved; a redirect, frame or picture elsewhere (a CDN) prints missing. A file's page reaches no network. The Browser window's print after a bot check is not held this way.
* **The engine** (*PDF engine*): Edge, Chrome, Brave or (on a Mac) Chromium. Without one, Microsoft Print to PDF draws Markdown, text and pictures in black and white (Windows only).
* **Output:** beside the source (`example.com-intro.pdf` for a page) under a free name (`-2`, `-3`…), or a named `to`, which replaces a PDF only with `overwrite`. A page behind a bot check goes to [the Browser window](#the-browser-window), which prints the page you let through.

## Home

### Home Assistant

Under *Home Assistant tools*, with *Home Assistant URL* and *Home Assistant API key* (a long-lived token). [The home bar](INTERFACE.md#the-home-bar) lets you control the home yourself with just the URL and key.

* **Names, not ids:** "the den", "kitchen and hallway" or "all"; a room uses its group light if it has one. An ambiguous name comes back as a question.
* *Home Assistant action policy* decides what runs unasked; under `ask` only safe services do.

| Tool | Arguments | What it does |
|---|---|---|
| `ha_overview` | — | Lights on per room, media players, temperatures, motion, low batteries (under 20%), to-do lists and scenes. |
| `ha_states` | `query?, domain?, area?` | Entities narrowed by words, domain and room (at most 80); an exact id gives every attribute. |
| `ha_history` | `entity, hours?` | One entity's states over the last 1–336 hours (24 by default). |
| `ha_lights` | `target, action?, brightness_pct?, color_name?, color_temp_kelvin?, transition?` | `on` (default), `off` or `toggle`, with brightness, a color or white (1500–9000 K) and a 0–300 s fade. |
| `ha_scene` | `scene, transition?` | Activates a scene by name or id. |
| `ha_media` | `action, target?, volume_pct?, source?` | Power, volume, mute, `source` (by name or prefix), play, pause, stop, next and previous for a media player. |
| `ha_todo` | `action, item?, list?` | `list`, `add`, `complete` or `remove` to-do items; `list` runs under every policy. Off in a new profile. |
| `ha_call_service` | `domain, service, entity?, data?` | Any other service (`button.press`, `script.turn_on`), under the policy. |
| `ha_assist` | `text` | Hands a sentence to Home Assistant's Assist agent. Under `ask` it waits for your yes, the question showing the whole sentence (one of more than 200 characters, or with a line break or an invisible character, is refused; add `conversation.process` to the safe services to skip asking); refused under `off`. |

### Shopping list

Under *Shopping tools*. One list per profile and any number of stores, in the profile's `db\shopping\shopping.db`. The list sorts itself into the active store's walking order, ticked lines last.

* **Layout facts:** a store learns where a product, brand or category is (`Aisle 7`, `Dairy`); a guess is marked `(guess)` until you confirm it.
* **Reference documents:** guides and floor maps go in `db\shopping\reference\<store>` (PDFs, `.md`, `.txt`, pictures), indexed when needed (`/shopping reindex` rereads them).
* **Never a file:** while these tools are offered, `write_file`, `patch_file`, `copy`, `move`, `vault_write` and `vault_move` won't write a file or note named as a shopping or grocery list (`Groceries.md`), pointing the model to `shopping_add`. `/shopping export` still writes, and an old list file can be read or deleted.

| Tool | Arguments | What it does |
|---|---|---|
| `shopping_list` | `store?` | The list in walking order, with ticked lines and likely duplicates. |
| `shopping_add` | `items, store?` | Adds lines (`"2 soy milk"`); a name already there gets the quantity added. |
| `shopping_check` | `items, checked?` | Ticks (or unticks) lines, by name or number. |
| `shopping_remove` | `items` | Takes lines off the list. |
| `shopping_clear` | `all?` | Removes the ticked lines; `all` empties the whole list, only after your yes. |
| `shopping_locate` | `term, store?` | Where a product would go and why, with matching reference lines. |
| `shopping_reference` | `query?, file?, page?, picture?, reread?, store?` | Searches, lists or reads a page of the store's reference documents; `reread` indexes them again. |
| `shopping_place` | `term, kind?, location?, section?, source?, note?, forget?, store?` | Records or forgets where a product, brand or category is; `source` is `user`, `reference` or `guess`. |
| `shopping_stores` | `store?` | The stores, the active one marked, and one store's layout. |
| `shopping_layout` | `action, name, to?, section?, walk?, notes?, store?` | Adds, activates or renames a store; changes its sections, locations and walking order. |
| `shopping_recent` | `limit?` | What you bought before and isn't on the list, most often first. |

## Set elsewhere

These tools are switched from `/settings` or `/skills`, not `/tools`.

### Memory

Under *Memory mode*.

| Tool | Arguments | What it does |
|---|---|---|
| `save_memory` | `text` | Saves one lasting fact about you, known in every later session. Not offered while *Memory mode* is `read-only`. |
| `recall_memory` | — | Everything remembered, oldest first, except memories turned off on the `/memory` pane. Called at the start of every conversation. |

### Skills

Under *Agent skills*.

| Tool | Arguments | What it does |
|---|---|---|
| `load_skill` | `name, file?` | Loads a skill's instructions or one of its files, up to 64,000 characters; never one you [disabled](SETTINGS.md#disabling-a-skill). |
| `skill_editor` | `action, scope?, name, description?, instructions?, path?, content?, old_text?, new_text?, replace_all?, summary?` | Creates or updates a skill in `profile` (default) or `global` scope (*Global skills*); `write_file` and `edit_file` change a supporting file, `edit_file` on `SKILL.md` part of its instructions. |

* `skill_editor` never deletes, and never touches an external skill or one you [locked](SETTINGS.md#locking-a-skill) or disabled.
* **Writes need their text:** `skill_editor`'s `write_file`, `write_file`, `unc_write` and `vault_write` refuse a call with no `content`; `edit_file`, `patch_file` and `unc_patch` one with no `new_text`. An empty `""` is allowed; a pruned stub copied back from the history is not.
* **A skill's files by full path:** the file tools refuse a path inside a skill folder and name `load_skill` instead.

### Sessions

Under *Session tool*.

| Tool | Arguments | What it does |
|---|---|---|
| `session_manager` | `action, query?, id?, max_results?, from_turn?, to_turn?` | `search`, `list` or `read` this profile's earlier conversations (not the current one). Never restores or deletes one. |

### Advisors

Under *Advisors enabled*. Advisors are other profiles of this home, each in a role you give it ([SETTINGS.md](SETTINGS.md#advisors)); `/advisor` asks one yourself ([COMMANDS.md](COMMANDS.md#advisor)).

| Tool | Arguments | What it does |
|---|---|---|
| `neon_advisor` | `advisor, question, context?` | Asks one advisor, by profile or role, on its own server and model; it may use this chat's read-only tools for up to *Advisor max tool iterations* round trips. |

* Offered while an advisor is listed; in plan mode the model consults fitting advisors before presenting a plan. Each advisor keeps one thread per conversation.
* An advisor on the Claude Code server runs its own `claude` process, until the conversation ends or this chat's server or model changes.

### Plan

Offered only in [plan mode](COMMANDS.md#plan-mode).

| Tool | Arguments | What it does |
|---|---|---|
| `present_plan` | `title, name?, markdown, verify?, final?, gaps?, checked?, minor?` | Saves the plan as `.neon/plans/<name>.md` (revisions overwrite it), prints it and asks for your approval. `verify` and `final` are its check commands, kept in its header. |
| `present_audit` | `findings?, retract?, revise?, minor?, questions?, covered?, checked?, summary?` | Records a code audit pass in its report (`.neon/audits/<name>.md`), which the app writes. At most 20 findings a call; with nothing new, `checked` is required. |

* The header's `status` is `draft`, `approved`, `canceled`, `done` or `incomplete`. A revision without `verify` or `final` keeps the file's; empty clears one. Headless, the plan is saved and `/plan approve` starts it.
* In a gap review (`/plan gaps`), `gaps` lists what a revision closed; an empty `gaps` ends the review. A pruned stub sent back as `markdown` is refused.
* Pictures in the conversation are saved beside the plan (`ref-1.png`…) under `## References`; a step names the ones it needs.
* **An audit finding** must name a file read this session, with its quoted evidence within 5 lines of its line; its text is redacted. A duplicate of an open finding is refused, one of a settled finding reopens it.

### Ralph loop

Offered only in a [Ralph loop](COMMANDS.md#ralph-loop)'s pass, beside every other tool.

| Tool | Arguments | What it does |
|---|---|---|
| `finish_step` | `outcome, summary, verified?, notes?, learned?, substeps?` | Reports the pass once: `done`, `partial`, `blocked` or `split` (2–6 `substeps`, a `By hand:` check kept on one). `notes` go to the next pass, `learned` to every later one. |

## MCP servers

Under *MCP servers enabled*. An MCP (Model Context Protocol) server is an outside program that offers the model more tools.

* Each connected server is a tool group, its tools named `<server>__<tool>` (`gateway__get_current_time`). `/mcp` switches servers and single tools.
* Servers connect in the background (🔌 on the status bar); a reply started meanwhile gets those already connected.

## Known issues

* **An event too large to read:** a remote event too large for one reply even alone is stepped over and noted. That step hasn't been tried against a real host.
* **Remote `runas`:** a remote computer reached with `runas` (event logs, services, scheduled tasks) hasn't been tried against a real second machine; the app's own computer and `localhost` have.
* **YouTube:** `seek` and `volume` on a paused video answer with the old value. The change itself happens, and the next answer shows it.
* **Bot-checked downloads:** a site that ties its clearance to the browser's connection may still refuse a download after the Browser window passed its check.
* **The camera pane:** its "The camera is opening…" line stays until the next key after the camera is on.
* **The Web UI's floating buttons** riding above a phone's keyboard can only be checked by hand on a phone; no test shows it.
