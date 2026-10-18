# Tools

Every tool the model can call, grouped as the tabs of `/tools` show them. Back to the [README](../README.md).

Each group has a switch (*File tools*, *Web tools*…), and the Offered tab of `/tools` switches single tools; a menu whose switch is off is dim and italic on the strip, see [INTERFACE.md](INTERFACE.md#panes). `/sys` › Tools shows what the model is offered right now. Settings in *italics* are described, with their values and defaults, in [SETTINGS.md](SETTINGS.md).

## Contents

- [Where tools are offered](#where-tools-are-offered)
- [Core](#core): [Web](#web), [Shell](#shell), [Clock](#clock), [Timers](#timers), [Ask](#ask), [Help](#help)
- [Files](#files): [File](#file), [Image](#image), [UNC shares and outside folders](#unc-shares-and-outside-folders), [GitLib](#gitlib)
- [Apps](#apps): [Docker](#docker), [ComfyUI](#comfyui), [YouTube](#youtube), [Obsidian](#obsidian)
- [Admin](#admin): [Network](#network), [Event logs](#event-logs), [Services](#services), [Scheduled tasks](#scheduled-tasks), [Active Directory](#active-directory)
- [Databases](#databases): [How the database tools work](#how-the-database-tools-work), [SQL Server](#sql-server), [MySQL and MariaDB](#mysql-and-mariadb), [SQLite](#sqlite), [PostgreSQL](#postgresql), [Oracle](#oracle)
- [Devices](#devices): [Camera](#camera), [Screen](#screen), [Printing](#printing)
- [Home](#home): [Home Assistant](#home-assistant), [Shopping list](#shopping-list)
- [Set elsewhere](#set-elsewhere): [Memory](#memory), [Skills](#skills), [Sessions](#sessions), [Advisors](#advisors), [Plan](#plan)
- [MCP servers](#mcp-servers)

## Where tools are offered

* **A group appears only when it can work:** Obsidian needs a vault, the databases and UNC a connection or share you ticked, Home Assistant its URL and key, ComfyUI its URL and a workflow.
* **Headless** ([HEADLESS.md](HEADLESS.md)) has no timers, `ask_user`, camera or screen tools, Browser window or `_execute` database tools, and YouTube can't play.
* **Headless, anything that would stop to ask you is refused:** a shell command off the allow list under `ask`, a Docker change, a service or task change (never offered there), a print under `ask`, a Home Assistant service that needs your yes, clearing the whole shopping list, an advisor under *Advisor confirm*.
* **Plan mode** ([COMMANDS.md](COMMANDS.md#plan-mode)) keeps each group's reading tools plus `ask_user`, `camera_capture`, the screen tools and `neon_advisor`, and adds `present_plan`. It drops every tool that changes or starts something (`start_timer`, `open`, `open_url`, `process`, `image_edit`, `unc_fetch`…) and every MCP tool.
* **On a Mac** the Browser window, Docker servers, services, scheduled tasks and Active Directory are Windows only, the event logs read the Mac's own ([Event logs on a Mac](#event-logs-on-a-mac)), and the camera and screen tools and YouTube playback need macOS 14 or later. Other differences are in each section and [MAC.md](MAC.md#what-works).

## Core

### Web

Under *Web tools*.

| Tool | Arguments | What it does |
|---|---|---|
| `web_search` | `query, max_results?` | Searches the web with *Web search method* (DuckDuckGo or SearXNG): title, URL, snippet. |
| `web_fetch` | `url, offset?` | A page's readable content as Markdown, 32,000 characters at a time; a `#anchor` in the URL starts at that part of the page (or says it is not there), every anchor of a page one download. Also plain text, JSON, XML and CSV, and a PDF's outline and first 40 pages (see [Reading PDFs](#reading-pdfs)). |
| `open_url` | `url?, urls?` | Opens up to five links in your browser. Under *Web open links in* `browser-window` it opens one in the [Browser window](#the-browser-window) when the window can take it. While *YouTube tools* is on, a YouTube video or playlist link goes where *YouTube open links in* says. |
| `web_links` | `filter?` | Your saved links (as `/browser list` shows), numbered, at most 100; `filter` keeps those holding all its words. |
| `web_save_link` | `action, url, name?` | `add`, `remove` or `rename` a saved link. To remove or rename, `url` may be its number in `web_links`; `current` is the Browser window's page. |
| `download_file` | `url, path?, overwrite?` | Downloads a file into the working directory, up to *Web download max (MB)*. Needs *File tools* too. |

* **A site that refuses a plain fetch**, or needs scripts, is tried again in your installed Edge, Chrome or Brave (or Chromium on a Mac), run headless (*Web browser mode* decides when). It keeps cookies in `%TEMP%\NeonSidekick\browser` (`$TMPDIR/NeonSidekick/browser` on a Mac).
* **Bot checks:** one that clears by itself is waited on for up to 15 seconds; one that wants a click goes to the Browser window (below) or is reported as blocked.
* **Pacing:** a rate limit of 10 seconds or less is waited out, and requests to one site are at least a second apart.

#### The Browser window

The app's own browser (Windows only), where you solve a bot check the web tools can't get past.

* **When:** `web_fetch`, `web_search`, `download_file` and `convert_to_pdf` offer it on a 403, a 503 that names no wait, or a check's page; never for a rate limit, and never headless. *Web browser botcheck window* decides whether it asks you first, opens at once or never opens.
* **Solving:** the window comes to the front; pressing Esc or *Give up*, or closing the window, gives up, and so does waiting 5 minutes. Once the page loads with no check showing, the window closes and the tool goes on with that page.
* **From the phone:** the allow question and *Give up* are on the Web UI's Chat tab too, and with *Web UI bot check control* on the card shows the window live to tap, hold, scroll and type into ([A bot check from the page](INTERFACE.md#a-bot-check-from-the-page)). The challenge still runs in the PC's window: its pass is a cookie only that browser can use.
* **Cookies:** the window's cookies for the site go to the web tools for this run, so the next fetch usually needs no window. A site that ties its clearance to the browser's connection may still refuse a download.
* The window's bar, keys, history and saved links: [The Browser window](INTERFACE.md#the-browser-window).

### Shell

Under *Shell command policy*. Commands run on your machine in the working directory, guarded by *Shell command policy* and *Shell police* ([Shell guards](SETTINGS.md#shell-guards)).

* Commands run hidden, with colors, pagers and input turned off. A line longer than 8,192 characters is cut.
* A command that times out, or that you stop with Esc, is stopped along with everything it started.
* Background commands stop when the app closes, even after a crash. On a Mac, a program that starts its own session (a daemon) can slip away.
* A program a command opens (`start notepad`, `code .`) stays open.
* On a Mac, a command that asks something at the terminal (a `sudo` password) fails at once instead of waiting.
* `/process` lists background commands ([process window](INTERFACE.md#the-process-window)); `/cmd` runs your own line through the same guards ([Your own shell line](COMMANDS.md#your-own-shell-line)).
* **Headless,** under `ask` only allow-listed commands run, and a run that refused one ends with exit code 3; `--yolo` allows all ([HEADLESS.md](HEADLESS.md)).

| Tool | Arguments | What it does |
|---|---|---|
| `run_command` | `command, shell?, workdir?, timeout?, background?, notify?` | Runs a command in `powershell` (default), `cmd` or `bash`; on a Mac `zsh` (default), `bash` or `powershell`. `background` (or a long timeout) returns a `proc_…` id; `notify` tells you when it exits. |
| `execute_code` | `language, code, timeout?` | Runs a one-off `python` (`python3` on a Mac), `node` or `powershell` script. It asks like `run_command`; allowing it for the session covers that language. |
| `process` | `action, session_id?, data?, timeout?, until?, offset?, limit?` | Up to 16 background commands (and the last 64 finished), by id prefix: `list`, `poll`, `log` (last 5,000 lines), `wait` (`until` a line holds a text), `kill`, `write` / `submit`, `close`. |

* **The tool bridge:** with *Shell tool bridge* on, a script can call the app's other tools (Python `from neon_tools import call`, Node `neon.call(...)`, PowerShell `Invoke-NeonTool`), except `execute_code` and `ask_user`. A `run_command` through it still asks, and can't run in the background.

### Clock

Under *Clock tools* (on by default). Off, the model has no clock tools, its prompt drops the clock sentence, and a conversation no longer opens with the time.

| Tool | Arguments | What it does |
|---|---|---|
| `get_current_time` | `zone?` | The date, time, weekday and time zone, here or in an IANA zone. Called at the start of every conversation while *Clock tools* is on. |
| `shift_date` | `date, days?, weeks?, months?, years?` | Moves a date by days, weeks, months or years and gives its weekday. |
| `date_difference` | `from, to` | The days between two dates (negative when the second is earlier), plus years, months and days for longer gaps. |

### Timers

Under *Timer tools* (on by default). Off, the model has no timer tools and its prompt drops the timer sentence; `/timer` and timers already running keep working. Not offered headless. A timer going off rings *Timer tone* (silent by default), whoever started it. The model can't start a stopwatch (`/timer stopwatch` is yours), but `list_timers` and `stop_timer` include them.

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

Under *Help tools* (on by default). Off, the model has no `neon_help` and its prompt drops the manual's sentence; `/help <words>` shows the manual's own answer.

| Tool | Arguments | What it does |
|---|---|---|
| `neon_help` | `query?, kind?` | Neon Sidekick's own manual: commands, settings (effect, default, place), panes and keys, narrowed by `kind` (`any`, `command`, `setting`, `pane`, `keys`). It never reads your current settings. `/help <words>` asks the model to use it. |

## Files

### File

Under *File tools*. Paths are relative to the working directory; nothing outside it can be reached except, where noted, the profile's `images` folder.

| Tool | Arguments | What it does |
|---|---|---|
| `get_working_directory` | — | The working directory's path. Called at the start of every conversation. |
| `search_files` | `text?, path?, files?, regex?, context?, output?, order?, limit?, depth?` | Searches text files for a word, phrase or regex. Without `text`, lists a folder, a tree (`depth` 2–4), matching names or the newest files, also in the `images` folder. |
| `file_info` | `path` | A file's size, time, lines, words, line ending and BOM, or a folder's counts and size; also whether something exists. |
| `read_file` | `path, start_line?, max_lines?` | Reads a text file or part of it (a negative `start_line` counts from the end). |
| `read_pdf` | `path, pages?, pictures?, find?, fields?, share?` | Reads a PDF's text page by page; `find` lists the pages holding some words, `pictures` sends the pages as pictures, `fields` lists a form's fields. See [Reading PDFs](#reading-pdfs). |
| `write_file` | `path, content, mode?` | Writes a text file: `create` (default), `overwrite` or `append`. Without a mode it replaces only a file the model read whole, wrote or patched in this conversation, unchanged since. |
| `patch_file` | `path, old_text, new_text, replace_all?` | Replaces `old_text` once (or everywhere, with `replace_all`), tolerating spacing, indentation and quote differences. |
| `create_directory` | `path` | Creates a folder and any missing parents. |
| `move` | `from, to, overwrite?` | Renames or moves a file or folder; replaces nothing unless `overwrite`. |
| `copy` | `from, to, overwrite?` | Copies a file or folder under the same rule; a folder copied over a folder merges. |
| `delete` | `path` | Moves a file or folder to the Recycle bin (the Trash on a Mac) under *File deletes to Recycle bin* (on by default), else deletes it for good. A network share or removable drive has no bin, so it's deleted directly and the result says so; something too big for the bin is deleted for good, as Windows does. `.git`, anything in it and a folder holding one are refused. |
| `zip` | `path, to?, overwrite?` | Packs a file or folder into a `.zip`, beside it by default. Off in a new profile. |
| `unzip` | `path, to?, overwrite?` | Extracts a `.zip` into a folder, all or nothing. Off in a new profile. |
| `open` | `path?, share?` | Opens a file in its own program, a picture where *Image viewer* says, or a folder in Explorer (Finder on a Mac); `share` opens one on a UNC share. |
| `convert_to_pdf` | `path?, url?, markdown?, title?, to?, overwrite?, landscape?, paper?` | Makes a PDF from one of a file, a web page (needs *Web tools*) or Markdown; see [Making PDFs](#making-pdfs). |
| `pdf_edit` | `action, path?, paths?, pages?, angle?, fields?, flatten?, to?, overwrite?` | Merges PDFs, splits pages out into a new one, deletes pages, rotates pages, or fills in a form, into a new PDF; see [Editing PDFs](#editing-pdfs). |

* In the `images` folder, `open` opens only pictures and folders. A `runas` UNC share can't be opened.

#### Reading PDFs

`read_pdf` reads a PDF with pdf.js in Edge, Chrome, Brave or (on a Mac) Chromium, headless; without one it refuses. `read_file` refuses a PDF and points to it.

* **Pages:** physical numbers, `"3"`, `"2-5"`, `"5-"` or `"1,3,7-9"`. Without `pages` it starts at page 1 with the PDF's outline (its bookmarks, two levels). A long PDF comes back in part, the header naming the pages to read next.
* **Printed numbers:** a page whose printed number differs shows it beside its own, `--- page 14 (printed "xii") ---`.
* **`find`:** the pages holding every word (any case or accent), up to 20 with a line each; the first search reads the whole text once.
* **`pictures`:** the pages as pictures, at most *File view image max images* a call, for a scan, a chart or a form; a model that can't see pictures gets nothing from them. A saved session keeps a line naming each page, not the picture.
* **`fields`:** a form's fields instead of its text: each name, kind, value now and the values it takes, which `pdf_edit`'s `fill_form` sets. An XFA form says so.
* **Limits:** 32 MB; a locked PDF is refused. Chinese, Japanese and Korean text reads with pdf.js's own CMaps, built in.
* **On a share:** `share`, or a full `\\server\share` path, reads a PDF on an offered UNC share in place, without copying it.
* The text is cached until the file changes, so a second call on the same PDF starts no browser.
* The browser reads the PDF on a page cut off from the network, in a profile of its own (`%TEMP%\NeonSidekick\browser-pdf`, `$TMPDIR/NeonSidekick/browser-pdf` on a Mac), never the one that keeps the web tools' cookies.
* **On the web:** `web_fetch` reads a PDF link the same way, its outline and first 40 pages, and pages through it as through a long page; `download_file` then `read_pdf` reads the rest or draws its pages. It takes a PDF up to 32 MB and gives its download 2 minutes. Under *Web browser mode* `browser` a PDF is still fetched over HTTP: headless Chromium would only download it. Past a bot check, the Browser window hands its cookies over and the PDF is fetched again.

#### Editing PDFs

`pdf_edit` and `/pdf merge|extract|delete|rotate` change PDFs in the working directory with pdf-lib, in the same headless browser and network-less page as [reading](#reading-pdfs). They need Edge, Chrome, Brave or (on a Mac) Chromium, whatever *PDF engine* is set to.

* **`merge`:** the PDFs in `paths` joined in order (20 at most, 64 MB together).
* **`extract`:** the pages given copied into a new PDF. **`delete`:** a copy without those pages (it never removes every page).
* **`rotate`:** pages rotated by `angle` (90, 180, 270; -90 the other way), every page when `pages` is left out.
* **`fill_form`:** each field in `fields` set by name (`read_pdf` with `fields: true` lists them): text, `true`/`false` for a check box, an option for a choice or radio group, a list for a multi-choice list. A name the form doesn't have, or an option it doesn't offer, is refused with the real ones and nothing is written. `flatten: true` bakes the values into the pages. Only AcroForm fields: an XFA form is refused.
* **Output:** a new PDF beside the first, named for what was done (`report-p2-5.pdf`, `a-merged.pdf`, `report-edited.pdf`, `report-rotated.pdf`, `form-filled.pdf`; `-2`, `-3`… when taken), or `to`. The original is replaced only when `to` names it with `overwrite`.
* **Limits:** each PDF up to 32 MB; a PDF locked with a password is refused, never decrypted. Local only: a PDF on a UNC share is copied in first (`unc_fetch`).

### Image

Under *Image tools* (`/tools image`, or the toolbar's 🌄), whether *File tools* is on or not; off in a new profile. They reach the working directory and, by full path, the profile's `images` folder (photos, screenshots, ComfyUI's pictures, pastes).

| Tool | Arguments | What it does |
|---|---|---|
| `view_image` | `path?, paths?` | Attaches pictures to the next message, up to *File view image max images*, so the model can see them. On a Mac it also takes HEIC/HEIF, TIFF and AVIF. |
| `image_info` | `path?, paths?` | A picture's format, size, frames, transparency, orientation and metadata (EXIF, GPS, XMP…), and what `image_edit` can write here. |
| `image_edit` | `path, to?, overwrite?, format?, quality?, max_kb?, width?, height?, scale?, fit?, anchor?, interpolation?, crop_x?, crop_y?, crop_width?, crop_height?, rotate?, flip?, filter?, brightness?, contrast?, saturation?, hue?, tint?, tint_amount?, blur?, sharpen?, pad?, background?, metadata?, dpi?, chroma?, colors?, dither?, interlace?, view?` | Resizes, crops, rotates, recolors and converts a picture into a new file ([Editing pictures](#editing-pictures)). |
| `set_splash_image` | `path, name?` | Copies a picture into the profile's `images\splash_images` for the welcome splash; the first one replaces the bundled set. |

#### Editing pictures

`image_edit` uses the system's own codecs and encodes once, so a JPEG loses quality only once.

* **Size:** `width` and/or `height`, or `scale`. With both, `fit` is `contain`, `cover` (cuts at `anchor`), `pad`, `stretch` or `shrink` (never enlarges).
* **Geometry and color:** a crop needs all four `crop_*` values; `rotate` takes 90, 180 or 270; `filter` is `gray`, `sepia`, `negative` or `polaroid`.
* **Formats on Windows:** writes PNG, JPEG, GIF, BMP and TIFF (JPEG XL and HEIF with Windows' extensions). WebP and AVIF read but don't write.
* **On a Mac:** reads HEIC/HEIF, TIFF, WebP, AVIF and JPEG XL; writes PNG, JPEG, GIF, BMP, TIFF and HEIF (no JPEG XL); refuses `chroma`, `colors` and `dither`.
* **`max_kb`** lowers the quality, then the size, until the file fits.
* **Metadata** is dropped unless asked for (*Image edit metadata*, or `metadata`). `metadata: none` alone strips EXIF (with GPS) and XMP from a JPEG, PNG, WebP or GIF without re-encoding it.
* **Output:** `photo-edited.png` beside the source (or in *Image edit output folder*), or `to`. An existing file is replaced only with `overwrite`; *Image edit mode* can replace the source.
* **Limits:** up to 200 MB and 100 megapixels in; 32,768 pixels a side and 100 megapixels out.

### UNC shares and outside folders

Under *UNC tools*. They reach named network shares (`\\server\share`, or a folder under one) and local folders outside the working directory (`D:\Data`), as you (`windows`) or another Windows account (`runas`, like `runas /netonly`).

* **Setting up:** shares live in `unc.json`, in the home's or the profile's `configs` folder like the [database connections](#managing-connections). *UNC add/edit share* walks you through adding one and tests it; *UNC set password* changes a password; *UNC shares offered* picks what the model sees.
* **Changes need two keys:** *UNC writes* on **and** the share's `access` `readwrite`. They are permanent (no trash) and logged; `unc_delete` starts off even then.
* A share that doesn't answer within 10 seconds is given up on. The shell can't use a `runas` sign-in: `unc_fetch` the file instead.

A share's fields in `unc.json`:

* **`path`** (required): `\\server\share`, a folder under it, or a full local path; in JSON double the backslashes or use `/`. Whole drives (`D:\`) are refused.
* **`auth`**: `windows` (default) or `runas`, with **`user`** (`DOMAIN\name` or `name@domain`) and **`passwordStore`** `file` or `credman` (`NeonSidekick/unc/<share_name>`).
* **`access`**: `read` (default) or `readwrite`.
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

* A `windows` share uses what your sign-in has for that server (a mapped drive, a `cmdkey` entry); a `runas` share signs in fresh each call. Use server names, not IP addresses.
* Paths stay under the share's root; a DFS link's target needs its own share.

| Tool | Arguments | What it does |
|---|---|---|
| `unc_shares` | `check?` | The named shares; `check` tries each one now. |
| `unc_search` | `share?, text?, path?, files?, regex?, context?, output?, order?, limit?, depth?` | `search_files` on a share. |
| `unc_info` | `share?, path?` | A file's size, dates, lines and words, or a folder's counts. |
| `unc_read` | `share?, path, start_line?, max_lines?` | Reads a text file, whole or in part. |
| `unc_fetch` | `share?, path, to?, overwrite?` | Copies into the working directory (needs *File tools*), up to 5,000 files and 500 MB. |
| `unc_write` | `share?, path, content, mode?` | Writes a text file: `create` (default), `overwrite` or `append`. |
| `unc_patch` | `share?, path, old_text, new_text, replace_all?` | Changes part of a text file. |
| `unc_create_directory` | `share?, path` | Creates a folder. |
| `unc_move` / `unc_copy` | `share?, from, to, overwrite?` | Moves, renames or copies within one share. |
| `unc_delete` | `share?, path` | Deletes a file or folder for good. Off by default. |
| `unc_put` | `from, share?, to?, overwrite?` | Copies from the working directory onto a share (needs *File tools*), up to 5,000 files and 500 MB. |

`share` may be left out for a full path or a single share. The changing tools appear only when both keys allow. `--unc-check <share>` tests a share.

#### UNC shares on a Mac

* **Paths:** `smb://server/share`, `\\server\share` and `//server/share` read the same, so one `unc.json` serves both. A local folder is a full path; a drive path is skipped.
* **The mount:** the app mounts a share on first use, hidden from Finder and **read-only** for `access: read`, and unmounts it at exit (or the next start after a crash).
* **While mounted, anything you run can read the share.** Only the shell police keeps the model's shell off it, and it reads only the paths written in a command.
* **Signing in:** as you, it uses your Kerberos ticket or a Keychain password Finder saved; `runas` mounts as the account (`name@REALM` or `DOMAIN\name`) with its stored password.

### GitLib

Under *GitLib tools*: git inside the app, for when the model should never run `git`. Local only (no `fetch`, `pull`, `push` or `clone`), on a repository at or under the working directory; `path` narrows a call and picks the repository.

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

Under *Docker tools*. They talk to the engine directly, over *Docker engine pipe* (a socket on a Mac, [below](#docker-on-a-mac)); the app never starts `docker` or Docker Desktop. A container is named by its name, a unique part of it, or a 4+ character id prefix. `/docker` lets you run them yourself ([COMMANDS.md](COMMANDS.md)).

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
| `docker_lifecycle` | `target, action, scope?, timeout_seconds?` | `start`, `stop`, `restart`, `pause` or `unpause` a container, or a compose project with `scope: project`. A stop waits `timeout_seconds` (10, at most 120). |
| `docker_pull` | `image, tag?` | Pulls a public image (`postgres:16`); no registry credentials are sent. |
| `docker_remove` | `kind, name, force?` | Removes a container, image or volume for good; `force` takes a running or used one. |
| `docker_prune` | `kind, all?` | Removes unused containers, images, networks, volumes or build cache (more with `all`), saying first how much it frees. |

`--docker-check` tests the tools against the real engine.

#### Docker servers

Containers that serve an OpenAI-compatible API (vLLM, SGLang…) can be `/server` choices, one running at a time. **Windows only.** Turn on *Docker servers enabled* and tick them in *Docker server containers* (`/settings` › Servers › Docker); the Docker tools needn't be on.

* The list holds containers matching *Docker server match words* or labeled `neonsidekick.llm=true`. Examples: [vLLM](DOCKER_WINDOWS_VLLM.md), [SGLang](DOCKER_WINDOWS_SGLANG.md).
* **Picking one:** `/server` lists a row per chosen container; `docker:<container>` picks one (also in `--url` and `NEONSIDEKICK_LLM_URL`).
* **Switching** stops the other chosen containers (*Docker server stop timeout (s)*), waits *Docker server post-stop delay (s)*, starts this one and waits up to *Docker server ready timeout (s)*. If a stop fails, nothing starts.
* **Leaving** for another server that uses this machine's GPU (the embedded model, a local server, a blank *LLM URL*) stops the containers; switching to a hosted API, the Claude Code server or another machine's server leaves them running. *Docker server stop on exit* stops the one in use at exit.
* Bots and advisors share a running container but never start or stop one.

#### Docker on a Mac

The same tools work over the engine's socket (Docker Desktop, OrbStack, Colima or Rancher Desktop), named by *Docker engine socket* (*Docker engine pipe* on Windows). Left empty, it is found as the `docker` command finds it:

1. `DOCKER_HOST`, if set.
2. The Docker CLI's current context (`DOCKER_CONTEXT`, else the one in `~/.docker/config.json`).
3. The usual places: `~/.docker/run/docker.sock`, `/var/run/docker.sock`, `~/.orbstack/run/docker.sock`, `~/.colima/default/docker.sock`, `~/.rd/docker.sock`.

* A `tcp://` or `ssh://` engine is refused. `docker_stats` shows the Linux VM's CPU and memory, not the Mac's.
* **Docker servers stay Windows only:** Docker on a Mac passes no GPU to containers. A Mac's local server is the [embedded model](SETTINGS.md#embedded).

### ComfyUI

Under *ComfyUI tools*. `generate_image` runs **your own ComfyUI workflows** on your server (*ComfyUI URL*) and shows the pictures to the model.

#### Adding a workflow

**With the wizard** (*ComfyUI add workflow* on `/tools` › Apps › ComfyUI):

* **Build** makes a standard text → image or image → image workflow from your server's checkpoints and samplers.
* **Import** takes a ComfyUI API export (*Workflow → Export (API)*) and puts the placeholders in. Use it for FLUX.2, Krea 2, Z-Image, Qwen Image, Ernie Image, Boogu, LongCat Image, HiDream I1 and Ideogram 4: export ComfyUI's own template and import that.
* The summary can **test** the draft (one small run), then saves `<name>.json` and `<name>.md`.

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

- **Describe what you want** and the model writes the prompt in the family's style (below), with the family's default negative.
- **Give your own prompt** ("use this prompt: …") and the model passes it unchanged (`verbatim: true`).
- **Skip the model:** `/imagine score_9, score_8_up, source_anime, 1girl -- score_4, blurry --seed 42` ([Imagine options](COMMANDS.md#imagine-options)).
- **Limit the choice** with *ComfyUI workflows offered*.

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

A workflow can take up to three pictures. For a face swap, install the ReActor node pack, build two `LoadImage` nodes → `ReActorFaceSwap` → `SaveImage`, export and **Import** it, and name the roles in the `.md`:

```markdown
---
description: Face swap (ReActor)
image: the picture whose face is replaced
image2: the face to put in
---
```

Then "put my face from [Image #2] on the person in [Image #1]" works in chat, or `/imagine faceswap --image target.png --image2 face.png`. A Qwen-Image-Edit graph needs `{{prompt}}` put in by hand.

#### The ComfyUI tools

| Tool | Arguments | What it does |
|---|---|---|
| `generate_image` | `prompt?, workflow?, negative?, negative_extra?, verbatim?, width?, height?, seed?, steps?, cfg?, denoise?, image?, image2?, image3?, count?` | Runs a workflow, saving 1 to *ComfyUI max images per call* pictures in *ComfyUI output folder* under the profile's `images` folder; they follow in the next message. |

* `image` is a working-directory path, a saved picture's full path, or a pasted `[Image #N]`.
* A refused run names the node and input at fault (a missing checkpoint, a bad value).

### YouTube

Under *YouTube tools*. Videos play where *YouTube open links in* says: the app's [video window](INTERFACE.md#the-video-window) (as before; on Windows through the WebView2 Runtime, on a Mac macOS 14 or later), the Browser window, the default browser or the Web UI's [YouTube tab](INTERFACE.md#the-youtube-tab) (while *Web UI YouTube controls* is on; otherwise the video window). The app's players use YouTube's embedded player, which refuses a video that can't be embedded.

| Tool | Arguments | What it does |
|---|---|---|
| `youtube_search` | `query, max?, kind?, order?, length?, since?, region?, language?, live?, captions?, safe_search?` | Searches with *YouTube API key* (about 100 quota units a search) for embeddable videos, or playlists or channels (`kind`). Filters left out use *YouTube search order*, *YouTube safe search*, *YouTube region* and *YouTube language*. |
| `youtube_playlist` | `playlist?, max?` | A public playlist's videos (`max` 1–200, 50 by default), by id, link or channel; left out, the one playing. |
| `youtube_play` | `video?, start?, playlist?, entry?` | Plays a video by id or link from `start` (`90` or `1:30`), or a playlist from `entry`. It starts at once, or is cued with *YouTube autoplay* off. |
| `youtube_control` | `action, value?` | `play`, `pause`, `seek`, `forward` / `back` (10 s by default), `volume` (0–100), `mute`, `unmute`, `next` / `previous` / `entry`, or `close`. |
| `youtube_status` | — | What the player plays: video, state, position, volume and playlist entry. |
| `youtube_saved` | — | Your saved videos, numbered, with where each was left (or `watched`). |
| `youtube_save` | `action, video` | `add` a video (`current` for the player's), or `remove` one (also by number). |

* **The key:** `youtube_search` and `youtube_playlist` need one, and each search spends daily quota; playing needs none. Watch Later and private playlists are out of reach (the app never signs in).
* **Saved videos** are your bookmarks, per profile, in `youtube.json`. One resumes a few seconds before where you left it; one seen to the end starts over. `/youtube save`, `list` and `unsave` let you manage the list yourself.
* **Known issue:** `seek` and `volume` on a paused video answer with the old value. The change itself happens, and the next answer shows it.
* **Where it plays:** in a browser the app can't see or control the video, so `youtube_status` says only where it went and `youtube_control` says it can't. In the Web UI's tab a video with no tab in view waits for one. A video YouTube won't embed goes to the browser through `open_url`.

### Obsidian

Under *Obsidian tools*, on the vault *Obsidian vault* names. They work on its files directly; Obsidian needn't be running. Notes are found by name, `[[wikilink]]`, alias or path.

| Tool | Arguments | What it does |
|---|---|---|
| `vault_search` | `query, tag?, folder?, max_results?` | Matching lines as `path:line`, and notes whose name or alias matches, within a tag or folder if given. |
| `vault_list` | `what?, folder?, tag?, property?, value?, max_results?` | Notes by folder, tag or property (`status: draft`), or every tag or property key with its count. |
| `vault_read` | `note, heading?, start_line?, max_lines?` | A note with its properties, one heading's section, or a window of lines. |
| `vault_links` | `note` | The note's links and embeds, and every backlink. |
| `vault_daily` | `date?, append?` | The daily note for a day (`today`, `+3`, `2026-09-22`), made from the vault's template if missing; `append` adds to it. |
| `vault_write` | `note, content, mode?, heading?` | `create`, `overwrite`, `append` or `prepend`, to the note or one heading's section. |
| `vault_properties` | `note, set?, remove?` | Lists, sets or removes properties in one write; only the named keys change. |
| `vault_move` | `note, to` | Renames or moves a note and rewrites every link to it. |
| `vault_delete` | `note` | Moves a note or attachment (never a folder) to the vault's `.trash`. Only while *Obsidian allow delete (.trash)* is on. |

## Admin

Checks on the network, then Windows' own records and machinery: the event logs, the services, the scheduled tasks and Active Directory. The network checks work everywhere; the rest are **Windows only**, all through Windows' own APIs inside the app: nothing is started (no `wevtutil`, `sc`, `schtasks` or PowerShell). Each has its own command (`/net`, `/eventlog`, `/services`, `/tasks`, `/ad`) that lets you run them yourself, even with the tools off.

The services and tasks reach computers you name in `services.json` and `tasks.json`; the directory reaches domains in `ad.json`. Each sits in the home's or the profile's `configs` folder like the [database connections](#managing-connections) (when both name the same one, the profile's wins), each entry is used only after you tick it in its *… offered* setting, and its *add/edit* wizard walks you through adding one and tests it. A remote computer reached with `runas` has not been tried against a real second machine here yet; this PC and `localhost` have.

### Network

Under *Network tools*. Small checks on one host at a time, for "is it up, what does it resolve to, is the port open, when does its certificate end": the app's own clients, nothing started (no `ping.exe`, `nslookup` or `openssl`). `/net` lets you run them yourself ([COMMANDS.md](COMMANDS.md)), even with the tools off.

| Tool | Arguments | What it does |
|---|---|---|
| `net_dns` | `name, type?, server?` | A name's records. A (the default) and AAAA through this machine's resolver; CNAME, MX, TXT, NS, SOA, PTR, SRV and CAA through the app's own DNS client against this machine's DNS servers, or the `server` named (which counts as a host). |
| `net_ping` | `host, count?` | Echoes (4 by default, 10 at most): answered or not, the loss and the round trips. |
| `net_trace` | `host, max_hops?, names?` | The route, hop by hop (30 at most, about 45 seconds); `names` looks each hop's name up. |
| `net_port` | `host, ports` | Whether TCP ports (16 at most) are open, refused or silent, on every address the host has. |
| `net_tls` | `host, port?, server_name?, starttls?` | A TLS handshake (port 443 by default): the protocol, the cipher, the certificate's names, issuer and days left, and what is wrong with its chain. Ports 25, 587, 143, 110, 389 and 3389 speak their STARTTLS or RDP preamble first. |
| `net_certs` | `store?, location?, filter?, expiring_within_days?, limit?` | One of this machine's certificate stores, soonest to end first; nothing exported. Only with *Network certificate stores* on. |

* **One host per call:** a name or an address. A range, a block, a list, a URL or `host:port` is refused, and so is an address spelled the old short way (`10.1`, a bare number).
* **Reach:** *Network tools reach* decides what a host may resolve to: `same-as-web` (the default) follows *Web browser network mode*; `internet`, `lan` or `both` set it for these tools alone. Every address a name resolves to is judged, and under `lan` only the private ones are used.
* **Host lists:** *Network tools denied hosts* always wins; *Network tools allowed hosts*, when not empty, is the only place the tools may go. An entry is a name (`fs01.corp.example`), a wildcard (`*.corp.example`) or an address block (`10.20.0.0/16`, `fd00::/8`).
* **A budget per turn:** *Network tools max hosts per turn* (8) and *Network tools max ports per turn* (32), counted by address, so two names for one machine count once. Scripts and advisors spend the main chat's budget; bots have their own. The answer says when a budget runs out, and the model is told once a turn how much is left.
* **TLS checks never fetch:** no certificate is downloaded and no revocation list is asked for, so the chain is judged from what the server sent and this machine already trusts. No credential is ever sent.
* **What a server wrote is untrusted:** TXT records, certificate names, a banner. Each is fenced as quoted data (`⟪ ⟫`), and an answer is held to 16,000 characters.
* **On a Mac** ping and trace use an unprivileged ICMP socket ([MAC.md](MAC.md)). `net_certs`' `my` is your login keychain and `root` the system's trust, read at the machine location unless `user` is named; the other stores are not kept there.

### Event logs

Under *Event log tools*. They read what Windows itself recorded (a crash, a failed service start, a driver error, a failed sign-in, a PowerShell script block) from this PC, from `.evtx` files in the working directory, and from remote computers you name. Everything is read-only, through Windows' own event log API in the app: nothing is started, no `wevtutil`. `/eventlog` lets you run them yourself ([COMMANDS.md](COMMANDS.md)), even with the tools off.

| Tool | Arguments | What it does |
|---|---|---|
| `eventlog_logs` | `filter?, all?, computer?` | The computers offered, a computer's logs with records, size and last write (200 looked into on this PC, 40 on a remote one; `all` adds the empty, disabled, Analytic and Debug ones), and the `.evtx` files in the working directory. |
| `eventlog_search` | `log, level?, id?, provider?, field?, since?, until?, audit?, text?, before_record?, limit?, computer?` | A log's events newest first, a line each; the answer ends with the exact call that fetches the next page. |
| `eventlog_summary` | `log, since?, level?, computer?` | Counts by provider and id since a time (a day by default), the top 30, each with its newest time and a sample message. |
| `eventlog_event` | `log, record, computer?` | One event in full: system values, message, named fields, and (under `full`) its XML. At most 32,000 characters. |

* **The log** is a name `eventlog_logs` lists (`app`, `sys`, `sec`, `setup` and `ps` for PowerShell/Operational), or an `.evtx` path in the working directory (never with `computer`).
* **Default view:** with no `level` or `audit`, Security shows its audit failures and every other log its critical events, errors and warnings.
* **Filters** go into Windows' own query: `level` (`critical,error,warning,information,verbose` or `all`), `id` (`41,1074` or `4700-4799`), `provider`, `since`/`until` (an age such as `2h`, or a moment in your time zone), `audit`, and `field` (`Name=value`, EventData's named values; `UserData` values are reached by `text`). At most 20 terms; `text` is matched in the app, over the provider, the fields and the message.
* **Limits:** *Event log max events* per page (100 at most); *Event log scan limit* events looked at per call (a quarter of it on a remote computer) and 10 seconds, after which the answer says how far back it got. A read gives up after 30 s (60 s remote).
* **Security** needs the Event Log Readers group, or the app run as administrator. Analytic and Debug logs read oldest-first only and can't be searched.

#### Event log computers

Computers live in `eventlog.json`, in the home's or the profile's `configs` folder like the [database connections](#managing-connections) (when both name the same one, the profile's wins). `local` is this PC, built in. The app reads a computer only after you tick it in *Event log computers offered*, for the model and `/eventlog` alike; until you change it, `local` alone is ticked. *Event log add/edit computer* walks you through adding one and tests it (the newest System event under its account); *Event log set password* changes a runas password.

* **`host`** (required): the computer's name or address (`localhost` is a loopback session).
* **`auth`**: `windows` (default, as you) or `runas`, with **`user`** (`DOMAIN\name` or `name@domain`; `lab-box\admin` for the machine's own account) and **`passwordStore`** `file` or `credman` (`NeonSidekick/eventlog/<name>`). **`authMethod`**: `default`, `negotiate`, `kerberos` or `ntlm`.
* **`logs`**: the logs allowed, case ignored, `*` matches any characters except `/` (`Microsoft-Windows-*/Operational`, `*/*` every slashed name). Absent: all.
* **`detail`**: `redacted` (default) or `full`, what the main chat's model reads (see below).
* **`description`**: what the computer is; the model reads it to choose.
* An entry named `local` may set only `logs`, `detail` and `description`.

```json
{
  "computers": {
    "local": { "logs": ["Application", "System", "Setup", "Microsoft-Windows-*/Operational"] },
    "fs01": { "host": "fs01.corp.example", "description": "file server", "detail": "full" },
    "dc01": {
      "host": "dc01",
      "auth": "runas",
      "user": "CORP\\svc-logreader",
      "passwordStore": "credman",
      "logs": ["Security", "System", "Directory Service"]
    }
  }
}
```

**A remote computer needs:** the "Remote Event Log Management" firewall rule on, and the account in its Event Log Readers group. A local administrator's network sign-in is filtered by UAC, so membership is the dependable way. `--eventlog-check <computer>` tests one.

#### Who sees what

* **`redacted`** hides secret-looking values: fields named like a password, secret, API key, private key or connection string, and in free text URL passwords, `password=…`, `-Password x`, `/pass:x`, `-p x`, `net user u <pw>`, `net use … /user:u <pw>`, `schtasks /RP`, `ConvertTo-SecureString '…' -AsPlainText`, `PSCredential`, `$password = '…'`, `Authorization:` headers, JWTs and PEM blocks. The message is formatted from the hidden values, so a secret doesn't slip in that way, and `text` and `field` match the hidden values only. **It is best-effort:** no rule knows every secret in free text.
* **`full`** is read only by the main chat. Bots, advisors and a script's `execute_code` bridge always read redacted, and an advisor's recent lines name an event-log call, never its result.
* **A `.evtx` file** takes `local`'s detail and no `logs` applies to it: a log exported from a `redacted` computer and read as a file is read under `local`'s detail.
* **Out of any rule's reach:** what the main model quotes, and saved sessions, which keep what the main chat saw. Set `full` only for computers whose events you are content to have anywhere the conversation goes.
* **Event text is untrusted:** any program or user can write an event. Each message and value is fenced as quoted data (`⟪ ⟫`), the model is told it is never an instruction, and an event whose source isn't registered says "source not registered: any program could have written this". Control characters are replaced.

#### Event logs on a Mac

The same four tools read the Mac's own logs, in the app: the unified log through macOS's own log API (no `log show`, nothing started), and the report files directly. No computers, runas or `.evtx`.

| Log | What it is | Default window |
|---|---|---|
| `system` | The unified log: every process's entries. `level` is `fault`, `error`, `default`, `info`, `debug` or `all` (default `fault,error`); `process`, `pid`, `subsystem` (a prefix), `category`, `text` and `exclude_process` narrow it. | the last hour |
| `crashes` | The crash and diagnostic reports in `~/Library/Logs/DiagnosticReports` and `/Library/Logs/DiagnosticReports` (and their `Retired` folders): crashes, jetsams (low memory), resource reports. A report's handle is its file name; `eventlog_event` shows its exception, termination and the crashed thread's top 30 frames. | the last 7 days |
| `install` | `/var/log/install.log`: software installs and updates. | the last day |
| `<name>.logarchive` | A saved unified log in the working directory (`log collect` makes one), read like `system`. | the last hour |

* **The unified log is vast** (tens of thousands of errors in minutes on a busy Mac), so every read is held to its window, stops at *Event log scan limit* entries or 10 seconds, says how far back it got, and names a process that made most of what it read, with the `exclude_process` to leave it out.
* **Handles:** `u<time>.<pid>.<thread>.<n>` for the unified log, a report's file name, `i<offset>.<time>` for install.log. An entry the log has since dropped says so.
* **Detail:** `eventlog.json`'s `local` entry alone applies: `redacted` (the default) also writes your home folder as `~`; `full` is the main chat's alone, as on Windows. A `level`, `subsystem` or `category` given to `crashes` or `install` is ignored, and the answer says so.
* **Permissions:** reading the unified log needs an administrator account. The system's report folder needs the `_analyticsusers` group, which an administrator is in; a folder you can't read is named in the answer, and the other is still read. `--eventlog-check local` tests them all.

### Services

Under *Service tools*. The model reads the services on this PC and the computers you tick, and with *Service writes* may start, stop or restart one, or change how it starts, each time asking you first.

| Tool | Arguments | What it does |
|---|---|---|
| `service_list` | `computer?, state?, start?, filter?, drivers?, limit?, offset?` | The services, a line each: name, display name, state, process, a failure's exit code. `state` is `running`, `stopped` or `failed`; `start` is `automatic`, `delayed`, `manual` or `disabled`. |
| `service_info` | `name, computer?` | One service whole: state, start type, account, program, recovery actions, what it depends on and what depends on it. |
| `service_control` | `name, action, computer?` | `start`, `stop` or `restart`, after your yes. Its list of actions is what every ticked readwrite computer allows. |
| `service_start_type` | `name, start_type, computer?` | `automatic`, `delayed`, `manual` or `disabled`, after your yes. Off in a new profile's Offered tab. |

**A change goes through one order of checks**, and the first that fails is the answer: *Service writes* → the computer's `access` → the visible list → `control` → a driver (never changed) → the app's protected list → the computer's own `protected` list → `controlActions` → running dependents (a stop or restart names them and stops) → asking you, showing the action, the service, the computer, the account and the full program path. Headless and the bots never get the two changing tools.

* **Protected by the app, always:** RPC and DCOM, the session manager, the security accounts, the event log, WMI, the firewall and Defender, the Task Scheduler, file sharing, Netlogon, DNS and DHCP clients, Remote Desktop, the crypto and sign-in services, and a few more (`ServiceGuard`). They can be started, never stopped, restarted or changed in start type. A computer's `protected` adds to the list, never takes from it.
* **Rights:** reading needs nothing special on this PC; a change needs the app run as administrator, or rights granted on the service. Some services (RPC among them) don't let an account that isn't elevated list what depends on them: `service_info` says so and the Service Control Manager then refuses a stop that would break them.

```json
{
  "computers": {
    "local": { "access": "readwrite", "control": ["Spooler", "MyApp*"], "controlActions": ["start", "restart"] },
    "app01": { "host": "app01.corp.example", "description": "app server", "services": ["MSSQL*", "W3SVC", "MyApp*"],
      "access": "readwrite", "control": ["MyApp*"], "protected": ["MSSQL*"], "detail": "full" }
  }
}
```

The keys every computer of `services.json` and `tasks.json` takes:

* **`host`**, **`auth`**, **`user`**, **`passwordStore`**, **`credential`**: as `eventlog.json`'s (`NeonSidekick/services/<name>`, `NeonSidekick/tasks/<name>` in Credential Manager).
* **`access`**: `read` (the default) or `readwrite`. Nothing is changed on a `read` computer, whatever else is set.
* **`control`**: what may be changed (case ignored, `*` matches any characters). Absent: nothing.
* **`controlActions`**: the actions allowed. Absent: all; `[]`: none. Services: `start`, `stop`, `restart`, `start_type` (`restart` must be listed on its own). Tasks: `run`, `end`, `enable`, `disable`.
* **`detail`**: `redacted` (the default: secret-like values in program paths and command lines hidden from the model) or `full`, read only by the main chat, as with the event logs.
* **`description`**: what the computer is; the model reads it to choose.
* **Services' own:** `services`, the services visible (absent: all), and `protected`. **Tasks' own:** `folders`, the folders visible (absent: all; a pattern ending in `\` means that folder's own tasks), and `privileged`.
* `local` is this PC, built in and read-only until its entry says otherwise; it takes every key but the sign-in ones.

**A remote computer needs** the account to have rights there; a local administrator's network sign-in is filtered by UAC's remote restrictions, so a domain account in its Administrators group is the dependable way. Services travel over RPC (the "Remote Service Management" firewall rule), tasks over the Task Scheduler's own RPC ("Remote Scheduled Tasks Management"). `--services-check <computer>` and `--tasks-check <computer>` test one ([COMMANDLINE.md](COMMANDLINE.md)).

### Scheduled tasks

Under *Task tools*. The model reads the scheduled tasks on this PC and the computers you tick, and with *Task writes* may run, end, enable or disable one, each time asking you first.

| Tool | Arguments | What it does |
|---|---|---|
| `task_list` | `computer?, folder?, recursive?, state?, filter?, include_microsoft?, limit?, offset?` | The tasks, a line each: path, state, last run and result, next run. Windows' own under `\Microsoft\` only with `include_microsoft`. |
| `task_info` | `path, computer?` | One task whole: its triggers in words ("every week on Monday at 02:30", "at startup after 5 minutes"), its actions, who it runs as and how, its last result and time limit. |
| `task_control` | `path, action, computer?` | `run`, `end`, `enable` or `disable`, after your yes. Only Task Scheduler's own run, stop and enable are ever called: nothing is created, deleted or rewritten. |

* **The order of checks:** *Task writes* → `access` → `folders` → `control` → a task of Windows' own (only by a `control` pattern starting `\Microsoft\`) → `controlActions` → **privileged** → the question.
* **Privileged:** a task that runs as SYSTEM or a service account, as a group, or with highest privileges. Running or ending one is refused unless the computer's entry says `"privileged": true`; enabling and disabling it are not, since they run nothing. A definition that can't be read counts as privileged.
* **A run's question** shows who it runs as, at what level, and every command line in full (never redacted: you decide on them).
* **History:** The *history* button on `/tasks` opens the task's runs on `/eventlog`, when the Task Scheduler's own log is on (Task Scheduler's *Enable All Tasks History*).
* Times are the computer's own clock.

### Active Directory

Under *Directory tools*. The directory is never written: the app's LDAP layer declares no call that writes. A list tool's `to` argument writes a CSV in the working directory, and only with *File tools* on and never in plan mode. The model looks up users, groups, computers and OUs in the domains you tick, and finds locked accounts, expiring passwords, stale and privileged ones.

| Tool | Arguments | What it does |
|---|---|---|
| `ad_user` | `name, domain?` | A user's card: enabled, locked and when (and where to find the source machine: Security event 4740 on the PDC emulator), bad passwords, password set and expiry, last sign-in, account expiry, title, department, manager, direct reports, OU, flags and groups. |
| `ad_group` | `name, nested?, members?, limit?, to?, domain?` | A group's card (type, owner, mail, created, the groups it is in) and its members, direct or `nested` (at any depth); `members` is `all`, `users`, `computers` or `groups`. |
| `ad_computer` | `name, domain?` | A computer's card, or a managed service account's: its OS, enabled, a domain controller or not, last sign-in, machine password, OU, owner and groups. |
| `ad_find` | `kind, days?, words?, type?, field?, ou?, limit?, to?, domain?` | A structured search; the kinds are below. |
| `ad_ou` | `ou?, limit?, to?, domain?` | The OU tree as paths with descriptions, or one OU: its owner, linked GPOs (and how many disabled or enforced) and the objects directly in it, counted by kind, its child OUs first. |
| `ad_domain` | `domain?` | Its domain controllers, functional levels, the role holders (the PDC emulator among them), the password and lockout policy, and the fine-grained password policies this account may see. |

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

* **`type`**: `user`, `group`, `computer`, `ou`, `contact` or `service` (a managed service account). A bare `name` search leaves OUs, contacts and service accounts out. A group, computer, OU or service account has only the `description` field.
* **`ou`**: a path such as `Neon/Engineering` (a `/` in a name written `\/`, a `\` as `\\`), a DN, or a name; only an OU or container under the domain. A name several OUs share is refused with their paths.
* **Nested members** are every member at any depth, each with the group it comes through (`via`), or `direct`. The members by primary group (how every user is in Domain Users, which no member list holds) are counted too. Members of nested groups in another domain are not listed. Every member is read (up to 10,000), sorted by account, the first `limit` listed, the total in the head line.
* **Kinds of member**: user, computer, group, contact, service account, a foreign principal (by its SID), "not readable" or "in another domain". Only `members: all` shows the last five.
* **`ad_user`'s groups** are its nested security groups (`tokenGroups`) and its direct groups, so a distribution group shows; without `tokenGroups`, the direct groups and the primary group.
* **`privileged`**: Domain Admins, Schema Admins, Enterprise Admins, Key Admins, Enterprise Key Admins, Administrators, Account Operators, Server Operators, Print Operators, Backup Operators and Replicator, found by SID (not Domain Controllers, whose members are DCs by design). A group the domain doesn't have is named. Listed separately: objects marked `adminCount=1` that no protected group reaches any more (krbtgt and the built-in Administrator are expected).
* **`office`** is personal data: refused on a `redacted` domain.
* **Lockout and expiry** come from the attributes the domain computes when it does, and are worked out from the domain's or a fine-grained policy otherwise; each card says which. Last sign-in is `lastLogonTimestamp`, up to about 14 days stale.
* **`domain`** takes a ticked entry's name or its DNS name (`lab` or `lab.neon.test`); left out, it is `current` when ticked, else the only one.
* **Searches stop at 1,000 entries** (10,000 for a group's members and for `to`), and say which cap cut them. A call has 40 seconds: a walk or a long list answers with what it read, "stopped at the time limit".
* **`to`** saves the whole list, up to 10,000 rows, as a CSV: a `.csv` path in the working directory, or a folder there (a name made from the question, `ad-group-staff.csv`, `-2` when taken). Never over an existing file; checked before anything is read. UTF-8 with a BOM, the card's columns and `dn`, dates as dates, a cell starting `=`, `+`, `-` or `@` guarded. Needs *File tools*, never in plan mode; the transcript's note names the file.
* **Never read:** passwords and their hashes, LAPS and BitLocker recovery values, managed and key credentials, certificates and photos.
* **`redacted`** (the default) leaves phones, addresses, employee numbers and home folders out; `full` is read only by the main chat.
* **`current`** is this PC's own domain when it is joined, built in (signing in as you, Kerberos). Other domains come from `ad.json`:

```json
{
  "domains": {
    "corp": { "domain": "corp.example", "server": "dc01.corp.example", "description": "the office" },
    "lab": { "domain": "lab.example", "server": "localhost", "auth": "runas", "user": "LAB\\reader", "passwordStore": "credman" }
  }
}
```

* **`domain`** (required): its DNS name. **`server`**: a domain controller (the domain's name when absent). **`baseDn`**: where to search (the domain when absent).
* **`tls`**: `true` for LDAPS on 636, the certificate checked against this machine's trust; otherwise 389, signed and sealed.
* **`auth`**, **`user`**, **`passwordStore`**: as above (`NeonSidekick/ad/<name>`). From a PC outside the domain, `runas` signs in with NTLM inside Negotiate.
* **`detail`**, **`description`**: as above.
* Configured domains are your own entries: *Network tools reach* doesn't judge them. `--ad-check <domain>` tests one.

### What the shell may not do while these are on

While *Service tools*, *Task tools* or *Directory tools* is on and *Shell police* is too, a `run_command` line, the script files it runs, an `execute_code` script or a `process` write that would change a service (`sc stop`, `net stop`, `Restart-Service`…), a scheduled task (`schtasks /run`, `Disable-ScheduledTask`…) or the directory (`Set-ADUser`, `Unlock-ADAccount`, `dsmod`, `ldapmodify`, `net user … /domain` with a change…) is refused before any approval, whatever *Shell command policy* says: changes go through the tools, their checks and your yes. Reading passes (`sc query`, `Get-Service`, `schtasks /query`, `Get-ADUser`). Like the forbidden strings, it's a tripwire, not a sandbox.

## Databases

### How the database tools work

The five families (SQL Server, MySQL/MariaDB, SQLite, PostgreSQL, Oracle) share one design. Each has its own switch (*SQL tools*, *MySQL tools*, *SQLite tools*, *PostgreSQL tools*, *Oracle tools*) and its own tab under `/tools` › Databases. They only read, unless you turn on a family's read-write mode.

#### Managing connections

* **The file:** each family keeps its connections in `sql.json`, `mysql.json`, `sqlite.json`, `postgres.json` or `oracle.json`. The home folder's (`%USERPROFILE%\.neonsidekick\configs\`, `~/.neonsidekick/configs/` on a Mac) is read by every profile; the profile's (`…\profiles\<profile>\configs\`) wins a name clash. Comments and trailing commas are allowed.
* **The wizard:** *… add/edit connection* (*SQLite add/edit database*) adds or edits one. Its test writes nothing: it signs in, warns when the account could change data, and names what else it could reach.
* ***… connections offered*** (*SQLite databases offered*) picks what the model sees: **nothing is offered until you tick it**. *… set password* changes a password.
* **Passwords:** `passwordStore` is `file` (default) or `credman`. With `file`, a password typed into the file is encrypted in place (DPAPI) at the next read. `credman` keeps it in Windows Credential Manager as `NeonSidekick/<family>/<connection_name>` (`sql`, `mysql`, `postgres` or `oracle`).
* **On a Mac**, `file` encrypts with a key kept in the Keychain, and `credman` is a Keychain password of the same name (`security add-generic-password -s NeonSidekick/sql/<connection_name> -a <user> -w`).

#### Reading safely

The query tools run one read-only statement behind three layers:

1. **The gate** lets through only a single read statement (`SELECT`, or a `WITH` ending in one). A second statement, changes, locking reads and the engine's ways out (files, links to other servers, dynamic SQL) are refused before the server sees anything.
2. **The session or transaction** is read-only and always rolled back.
3. **The account is the real guard.** Give the login read-only grants.

Values go in as parameters (`params`). Results are cut at *SQL query result max chars*; `max_rows` is 1–100,000, the family's *… max rows* when left out.

#### The connection's database

Each connection reaches **one database** (Oracle: one schema) and nothing else on the server.

* No tool takes a database or lists the server's databases. To work in another database, add a connection for it.
* A statement that names another database (`payroll.dbo.salaries`), or reads the server's own catalog (other databases, logins, other sessions), is refused before it reaches the server. If a table alias is spelled like another database's name, rename the alias.
* A view, synonym or routine that reads another database can't be seen in the text, so that stays the account's to guard.

#### Read-write mode

Changes go through each family's `_execute` tool. It needs **two keys**: the family's *… mode* set to `read-write` **and** the connection's `"access": "readwrite"` (SQLite has only the first). Both are checked on every call. It is **never offered headless or in plan mode**.

1. **The kinds:** *… statements allowed* ticks which kinds may run. A statement needs every kind it does (a `WITH` that deletes and inserts needs both), and a refusal names the kinds that are ticked.

   | Kind | Statements |
   |---|---|
   | `data` | `INSERT`, `UPDATE`, `MERGE` (`REPLACE` in MySQL and SQLite), upserts, and `RETURNING`/`OUTPUT` |
   | `delete` | `DELETE`, `TRUNCATE`, a `MERGE` that deletes |
   | `create` | `CREATE TABLE`, `INDEX`, `VIEW`, `SEQUENCE` and the like; SQL Server's `SELECT … INTO`; SQLite's `TRIGGER` and `VIRTUAL TABLE` |
   | `alter` | `ALTER`, `RENAME`, `COMMENT ON`; `CREATE OR REPLACE VIEW` needs `create` too |
   | `drop` | `DROP` of those, and of routines and triggers |
   | `upkeep` | statistics and index upkeep (`UPDATE STATISTICS`, `ANALYZE`, `OPTIMIZE`, `VACUUM`, `REINDEX`…) |
   | `procedures` | server databases: running code (`EXEC`, `CALL`, PostgreSQL's `DO`, an Oracle PL/SQL block) and creating or changing a procedure, function or trigger |
   | `pragma` | SQLite only: `PRAGMA` |
   | `read` | `SELECT` (and `VALUES`, `TABLE`, `EXPLAIN` where the engine has them): never asks, and runs on the read-only path |

   In SQL Server, a batch's lone first word reads as `EXEC` of it, so a typo there is a procedure call. A SQLite trigger also needs the kinds its body does.
2. **The gate:** one statement per call (a routine's body counts as one). Always refused, for example: transaction words, `SET`/`USE`, `GRANT`/`REVOKE`, users and roles, creating or dropping databases, dynamic SQL, file access (`BULK INSERT`, `LOAD DATA`, `COPY`, `ATTACH`) and links to other servers.
3. **Your yes:** every change asks you first, showing the statement (`v` shows all of it): *Deny*, *Allow once*, or *Allow for this session* (that connection or file only, until `/new`, `/clear` or a profile switch).
4. **The run:** the statement commits on its own; a failed, timed-out or canceled one changes nothing. Every change is logged with the connection, the rows changed and the statement.
5. **The account is still the real guard.** Give a `readwrite` connection only the permissions you want the model to use.

`--sql-check`, `--mysql-check`, `--sqlite-check`, `--postgres-check` and `--oracle-check` test a family against a real server or file ([COMMANDLINE.md](COMMANDLINE.md)).

#### The tools

The server families take a connection by name in `connection`, and SQLite a database in `database` (a name or a file's path); either may be left out when there is only one.

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

* **`server`**: `host`, `host,port` or `host\instance`. On a Mac a named-pipe (`np:`) server can't be reached.
* **`database`** (required): the one database the connection reaches.
* **`auth`**: `sql` (a SQL login: `user` and `password`), `windows` (your account; on a Mac, your Kerberos ticket from `kinit`), or `runas` (another Windows account, `DOMAIN\name` or `name@domain`, plus a password, used only to sign in to the server). On a Mac `runas` uses Kerberos, so the account is `name@REALM` ([MAC.md](MAC.md#another-account-runas)).
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

* `params` binds `@name`.
* The gate is SQL Server's own parser. Refused, for example: batches, `EXEC`, `INTO`, linked servers, `OPENROWSET`.
* Server-wide views and functions (`sys.databases`, `sys.dm_*`, logins) are refused; the database's own catalog (`sys.objects`, `INFORMATION_SCHEMA`) reads fine.
* CLR types (`geography`, `hierarchyid`) need `.ToString()`.

### MySQL and MariaDB

`mysql.json`; MySQL 8.0.16+ and MariaDB 10.2+.

* **`host`**, **`port`** (3306 by default), **`database`** (required), **`user`**, **`passwordStore`** `file` or `credman`.
* **`sslMode`**: `preferred` (default), `required`, `verify-ca`, `verify-full` or `none`.
* **`allowPublicKeyRetrieval`**: `true` only for a `caching_sha2_password` account without TLS (off by default, since a man in the middle could supply its own key).
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

* Queries end with `LIMIT n`, and `params` binds `@name`.
* Refused, for example: executable comments (`/*! … */`), `INTO`, `LOAD_FILE`, `USE`, and `information_schema`, `performance_schema`, `mysql` and `sys` as qualifiers.
* In a `CREATE PROCEDURE` or `TRIGGER` body, a column or label named `begin` or `end` must be in backticks.

### SQLite

The SQLite tools read database **files**: no server, no password. A database is a name from `sqlite.json` (a relative `path` counts from that file's own `configs` folder, the home's or the profile's) or, with *SQLite sandbox files* on, any file in the working directory by its path. *SQLite add/edit database* can test one by opening it read-only. Changes need only *SQLite mode* (no `access` key).

```json
{
  "databases": {
    "chinook": { "path": "D:\\data\\chinook.db", "description": "The music store sample" }
  }
}
```

* `params` binds `@name`, `:name`, `$name` or `#name`; positional `?` is refused.
* **Reading** opens the file read-only, whatever the mode. A statement past *SQLite query timeout (s)*, or one you stop with Esc, is interrupted.
* **Always refused:** `ATTACH`/`DETACH`, `VACUUM INTO`, `writable_schema` and `load_extension()`.
* **The rest of the app keeps out:** while *SQLite tools* is on, the shell police refuses a command or script that reaches SQLite, and the file tools never change a database file (`.db`, `.db3`, `.sqlite`, `.sqlite3`, their journals, or one `sqlite.json` names). Reading and copying one still work.
* **Creating a database:** `create: true` needs `create` ticked and *SQLite sandbox files* on. The file must end `.db`, `.sqlite`, `.sqlite3` or `.db3`, in a folder that exists in the working directory. If the statement fails, the new file is removed.

### PostgreSQL

`postgres.json`.

* **`host`**, **`port`** (5432 by default), **`database`** (required), **`user`**, **`passwordStore`** `file` or `credman`.
* **`schema`** (optional): holds the connection to that one schema; other schemas and `information_schema` are refused. Without it, every schema of the database is open.
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

* Queries end with `LIMIT n`, and `params` binds `@name`; positional `$1` is refused.
* Refused in queries, for example: `COPY`, `DO`, `dblink`, functions that reach files or other backends, and server-wide views (`pg_database`, `pg_roles`, `pg_stat_activity`).
* The wizard warns of a superuser role but doesn't refuse it. PostgreSQL lets every role connect to a new database unless `CONNECT` is revoked from `PUBLIC`; the test names the databases it could connect to.

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

* Queries end with `FETCH FIRST n ROWS ONLY` and no `;`, and `params` binds `:name`.
* `oracle_execute` answers with the rows changed only, and refuses `RETURNING … INTO`. A PL/SQL block counts as one statement.
* On 23ai and later the session itself is read-only for reads, so the server refuses any write. For the account, give `SELECT` grants (or `ALTER USER … READ ONLY` on 23ai).
* Refused, for example: `FOR UPDATE`, database links, `UTL_HTTP`/`UTL_FILE`/`DBMS_SQL`, and the whole-database views (`ALL_*`, `DBA_*`, `CDB_*`, `V$`). `USER_*` views work.
* A CLOB or BLOB shows its start and length; object types (`SDO_GEOMETRY`, `XMLTYPE`) need converting to text.

## Devices

### Camera

Under *Camera tool*. Not offered headless, or when the embedded model can't see pictures.

| Tool | Arguments | What it does |
|---|---|---|
| `camera_capture` | `prompt` | Shows the model's request ("Hold the label up"), then waits for you to take the photo or allow it (*Camera shutter*). Saved in *Camera output folder* under the profile's `images` folder. |

* A declined photo isn't asked for again that turn. See also [Camera](COMMANDS.md#camera).
* On a Mac (macOS 14 or later) it can use the built-in camera, a webcam or an iPhone, once macOS lets your terminal use the camera (System Settings › Privacy & Security › Camera).

### Screen

Under *Screen tools*. Not offered headless, or when the embedded model can't see pictures.

| Tool | Arguments | What it does |
|---|---|---|
| `screen_capture` | `target?, prompt` | Captures a monitor, every monitor or a window ([Screen capture](COMMANDS.md#screen-capture)), asking first per *Screen policy*. Saved in *Screen output folder* under the profile's `images` folder. |
| `screen_list` | — | The monitors and windows with their targets; titles and sizes only. |

* A denied capture isn't asked for again that turn.
* On a Mac (macOS 14 or later) your terminal needs Screen Recording permission (System Settings › Privacy & Security › Screen & System Audio Recording, then reopen it); without it `screen_list` gives monitors only.

### Printing

Under *Print tools*. `/print` and these tools print on any installed printer (on a Mac, those in System Settings › Printers & Scanners).

* **Drawn by the app:** text and code as a listing, Markdown formatted, a picture (PNG, JPEG, GIF, WebP, BMP) on one page, each page headed with the file's name and *page N of M*.
* **Anything else on Windows** (a PDF, an Office file) goes to its own program on the default printer, with no choice of printer, copies, pages or landscape.
* **On a Mac** a PDF prints as it is (no landscape); Office and other files are refused.
* Esc cancels a print. *Print action policy* decides whether `print_file` asks first.

| Tool | Arguments | What it does |
|---|---|---|
| `list_printers` | — | The printers, marking the system's default and *Print default printer*. |
| `print_file` | `path, printer?, copies?, pages?, landscape?` | Prints a working-directory file on a printer (else *Print default printer*, else the default), 1–10 copies, pages like `1-3,5`. |

#### Making PDFs

`/pdf` and `convert_to_pdf` make a PDF in the working directory.

* **Sources:** Markdown (pictures from the working directory only), text and code, a picture, an HTML file (scripts removed), or a web page (when *Web browser network mode* allows it). A PDF, local or a link, is refused with a pointer to `read_pdf`, `pdf_edit`, `web_fetch` or `download_file`.
* **Pages:** US Letter by default (`paper` `a4` or `legal`), portrait unless `landscape`; a web page keeps its own layout.
* **The engine** (*PDF engine*): Edge, Chrome, Brave or (on a Mac) Chromium. On Windows without one, Microsoft Print to PDF draws Markdown, text and pictures in black and white; on a Mac only the browser works.
* **Output:** beside the source (`example.com-intro.pdf` for a page), or `to`; an existing PDF is replaced only with `overwrite`.
* **A web page behind a bot check** goes to [the Browser window](#the-browser-window), which prints the page you let through.

## Home

### Home Assistant

Under *Home Assistant tools*, with *Home Assistant URL* and *Home Assistant API key* (a long-lived token).

* **Names, not ids:** "the den", "kitchen and hallway" or "all"; a room uses its group light if it has one. An ambiguous name comes back as a question.
* *Home Assistant action policy* decides what runs unasked; under `ask` only safe services do. States are read at most every 30 seconds.
* [The home bar](INTERFACE.md#the-home-bar) lets you control the home yourself: it needs the URL and key, not these tools or the policy.

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
| `ha_assist` | `text` | Hands a sentence to Home Assistant's Assist agent. Under `ask` it waits for your yes (add `conversation.process` to the safe services to skip that); refused under `off`. |

### Shopping list

Under *Shopping tools*. One list per profile and any number of stores, in the profile's `db\shopping\shopping.db`. The list sorts itself into the active store's walking order, ticked lines last.

* **Layout facts:** a store learns where a product, brand or category is (`Aisle 7`, `Dairy`); a guessed fact is marked `(guess)` until you confirm it.
* **Reference documents:** guides and floor maps go in `db\shopping\reference\<store>` (PDFs, `.md`, `.txt`, pictures), indexed when needed (`/shopping reindex` rereads them). PDFs are read in the browser that *Web browser path* names.
* **Never a file:** while these tools are offered, the file tools (`write_file`, `patch_file`, `copy`, `move`) and the vault's `vault_write` and `vault_move` won't write a file or note named as a shopping or grocery list (`shopping-list.md`, `Groceries.md`); the model is pointed to `shopping_add`. `/shopping export` still writes its copy, and an old list file can be read or deleted.

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
| `recall_memory` | — | Everything remembered, oldest first, except the memories turned off on the `/memory` pane. Called at the start of every conversation. |

### Skills

Under *Agent skills*.

| Tool | Arguments | What it does |
|---|---|---|
| `load_skill` | `name, file?` | Loads a skill's instructions or one of its files, up to 64,000 characters; never one you [disabled](SETTINGS.md#disabling-a-skill). |
| `skill_editor` | `action, scope?, name, description?, instructions?, path?, content?, old_text?, new_text?, replace_all?, summary?` | Creates or updates a skill in `profile` (default) or `global` scope (*Global skills*); `write_file` and `edit_file` change a supporting file, and `edit_file` on `SKILL.md` changes part of its instructions. |

`skill_editor` never deletes, and never touches an external skill or one you [locked](SETTINGS.md#locking-a-skill) or disabled.

* **Writes need their text:** `skill_editor`'s `write_file`, `write_file`, `unc_write` and `vault_write` refuse a call with no `content`; `edit_file`, `patch_file` and `unc_patch` one with no `new_text`. An empty `""` is still allowed. A pruned stub copied back from the history is refused too.
* **A skill's files by full path:** `read_file` and the other file tools refuse a path inside a skill folder and name `load_skill` (`name`, `file`) instead.

### Sessions

Under *Session tool*.

| Tool | Arguments | What it does |
|---|---|---|
| `session_manager` | `action, query?, id?, max_results?, from_turn?, to_turn?` | `search`, `list` or `read` this profile's earlier conversations (not the current one). Never restores or deletes one. |

### Advisors

Under *Advisors enabled*. Advisors are other profiles of this home, each in a role you give it, set up under `/settings` › Advisors ([SETTINGS.md](SETTINGS.md#advisors)); `/advisor` asks one yourself ([COMMANDS.md](COMMANDS.md#advisor)).

| Tool | Arguments | What it does |
|---|---|---|
| `neon_advisor` | `advisor, question, context?` | Asks one advisor, by profile or role. It answers on its own server and model, and may use this chat's read-only tools for up to *Advisor max tool iterations* round trips. |

* Offered while an advisor is listed; in plan mode the model consults fitting advisors before presenting a plan. Each advisor keeps one thread per conversation.
* An advisor on the Claude Code server runs its own `claude` process, until the conversation ends or this chat's server or model changes.

### Plan

Offered only in [plan mode](COMMANDS.md#plan-mode).

| Tool | Arguments | What it does |
|---|---|---|
| `present_plan` | `title, name?, markdown, gaps?, checked?, minor?` | Saves the plan as `.neon/plans/<name>.md`, prints it and asks for your approval. Revisions overwrite the file. |

* The header's `status` is `draft`, `approved`, `canceled`, `done` or `incomplete`. Headless, the plan is saved and `/plan approve` starts it.
* In a gap review (`/plan gaps`), `gaps` lists what a revision closed; an empty `gaps` ends the review and asks for approval.

## MCP servers

Under *MCP servers enabled*. Each connected server is a tool group, its tools named `<server>__<tool>` (`gateway__get_current_time`). Servers connect in the background (🔌 on the status bar); a reply started meanwhile gets those already connected. `/mcp` switches servers and single tools.
