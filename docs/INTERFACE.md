# Using the app

The screen, the input line, panes, keys, the bars and the app's own windows. Back to the [README](../README.md). Settings named in *italics* are in [SETTINGS.md](SETTINGS.md), with their values and defaults.

## Contents

- [Words used here](#words-used-here) · [The screen](#the-screen) · [The input line](#the-input-line) ([completion lists](#completion-lists))
- [Panes](#panes) · [Keyboard shortcuts](#keyboard-shortcuts) · [Commands typed during a reply](#commands-typed-during-a-reply)
- [The hints bar](#the-hints-bar) · [The status bar](#the-status-bar) · [The toolbar](#the-toolbar) · [The performance bar](#the-performance-bar)
- [The home bar](#the-home-bar) · [Bar checklists](#bar-checklists) · [The avatar picker](#the-avatar-picker) · [The welcome splash](#the-welcome-splash)
- [The app's windows](#the-apps-windows)
  - [picture viewer](#the-picture-viewer) · [picture menu](#the-picture-menu) · [thumbnail browser](#the-thumbnail-browser) · [log](#the-log-window)
  - [process](#the-process-window) · [Docker log](#the-docker-log-window) · [camera](#the-camera-window) · [video](#the-video-window) · [Browser](#the-browser-window)
- [Web UI](#web-ui)
  - [signing in](#signing-in) · [tabs](#the-tabs) · [Chat](#the-chat-tab) · [Shopping](#the-shopping-tab) · [Home](#the-home-tab)
  - [YouTube](#the-youtube-tab) · [commands](#commands-from-the-page) · [bot checks](#a-bot-check-from-the-page) · [safety](#safety-and-the-network)

## Words used here

* **Pane:** the box that opens at the bottom of the screen for settings, lists and questions. Esc closes it.
* **Tab:** one page of a pane. Some tabs hold a second row of tabs.
* **Profile:** a named set of settings, memories, sessions and files, each in its own folder (`/profile`).
* **Working directory:** the only folder the model's file tools can reach. It starts as the profile's `files` folder; `/cwd` changes it.
* **The model:** the language model answering you. **Server:** where it runs (`/server`).
* **Embedded model:** a model Neon Sidekick downloads and runs itself, with no server to install.
* **Tool:** something the model can do, such as read a file or run a command (`/tools`).
* **Skill:** a folder of instructions (`SKILL.md`) the model loads when a task calls for it.
* **MCP server:** an outside program that offers the model more tools (`/mcp`).
* **Session:** one saved conversation (`/sessions`).
* **Headless:** running without the screen, for scripts ([HEADLESS.md](HEADLESS.md)).
* **Checkpoint:** a copy of a file kept before a tool changes it, so `/undo` and `/rewind … files` can put it back ([COMMANDS.md](COMMANDS.md#conversation)).
* **Custom command:** a Markdown file holding a prompt you send often, run as its own slash command ([COMMANDS.md](COMMANDS.md#custom-commands)).
* **Tailscale, tailnet:** Tailscale links your own devices into one private network, your tailnet ([Away from home](#away-from-home)).
* **Ralph loop:** carries out a plan one step at a time, each step a pass in a fresh conversation ([COMMANDS.md](COMMANDS.md#ralph-loop)).

## The screen

From top to bottom:

* **The transcript**, under the banner (*Show header*). PgUp/PgDn scroll, Ctrl+Home/Ctrl+End jump to the ends. A resize lays notices and the banner out again; replies re-wrap only when narrower.
* **Message jumps:** Ctrl+↑/↓ goes to the previous or next message (Option+↑/↓ on a Mac, [MAC.md](MAC.md#keys--mouse)).
* **Scrolled up**, a row under the transcript counts the rows written since you left the bottom and names the top message; a double-click goes back down.
* **Links and paths:** Ctrl+click opens a link in your browser, or a path in `/browse`; hovering shows where it goes. A Mac's chord differs ([MAC.md](MAC.md#keys--mouse)).
* **The picture strip**, while there are ComfyUI pictures: 🎞️ opens the [picture viewer](#the-picture-viewer), × closes the strip.
* **The upper rule:** the session's name (a double-click renames it), **⤡** (a click folds or unfolds everything, like Ctrl+O), and 🎞️ to bring a closed picture strip back.
* **[The input line](#the-input-line).** A dim ☺ at its right end opens the emoji picker (*Emoji picker*).
* **The lower rule:** **▶ n** counts the background processes running; a double-click opens `/process`.
* **The update marker:** left of **▶ n**, **📦** shows a new version and how its [update](COMMANDS.md#updating) stands (`📦 0.5.7`, `42%`, `checking`, `at quit`, `back to 0.5.6 at quit`); a double-click opens `/update`. *Check for updates at start* off hides the plain `📦 0.5.7`, and a narrow window drops the marker first.
* **A mark:** `/mark <color>` colors both rules for the conversation, kept with its session; `/mark default` takes it off.
* **Five bars**, in *Toolbar order*: [hints](#the-hints-bar) (while there is a hint), [status](#the-status-bar), [toolbar](#the-toolbar), [performance](#the-performance-bar), [home](#the-home-bar).
* **A short window** drops the home bar, then the performance bar, then the toolbar; the hints bar goes last.

## The input line

* **Editing**, even during a reply: ←/→, Home/End (again for the whole message), Ctrl+←/→ by word (Option+←/→ on a Mac, [MAC.md](MAC.md#keys--mouse)), Shift to select. Ctrl+Backspace and Ctrl+Delete delete a word.
* **Enter** sends; Ctrl+Enter starts a new line. ↑/↓ step through earlier messages and move through a wrapped draft.
* **Copy and paste:** Ctrl+A or a drag selects, Ctrl+C copies, Ctrl+X cuts. A right-click on the line, or Alt+V, pastes, pictures too.
* **Ctrl+C** with nothing selected stops the speech, else cancels the reply; twice at an idle line, it exits.
* **Lists:** `/` commands, `@` files, `#` skills, `$` tools, `%` database connections, `^` ComfyUI workflows, `*` UNC shares. They work inside a command's text too, except in `/read`, `/view`, `/print`, `/pdf` and `/docx` ([Completion lists](#completion-lists)).
* **Drop a picture** from the picture strip or the transcript onto the input row to attach it (**🖼️ drop on line**).
* **During a reply** Enter queues the message. Esc stops the speech, then closes an open list, then cancels the reply; it never clears your draft.
* **At the idle line** Esc clears the draft (↑ brings it back); Esc twice on an empty line opens `/rewind`.
* **Voice:** with *STT input* on, the *STT push-to-talk key* records a message; with *STT wake* on, so does saying the wake phrase.
* **Tool calls** show as `🛠️ read_file notes.md`, the result under them (`→ 4 lines`, ✗ on a failure). Long results fold once the run ends; Ctrl+O or `/expand` opens them.
* **Code blocks:** ⧉ on a block's label copies it.
* **Tables** too wide for the window show each row as a card of header and value lines, every other card shaded (with no color, a line between cards).
* **During `/read`** (📖): Space on an empty line pauses and resumes; ←/→ step a sentence.
* **The mouse:** a double-click selects a word, and on the `›` the whole draft.

### Pasting a picture or a PDF

* **A picture:** a right-click on the line or Alt+V pastes a copied picture as `[Image #n]`; so does Ctrl+V where the terminal passes it on (Windows Terminal keeps it for text). With no picture, the text is pasted.
* Once sent, the picture is saved in *Pasted images folder* under the profile's `images` folder, and the model gets its path.
* **A network share path** (`\\server\share\…`) is attached only when its server is a configured UNC share's or a mapped drive's. Any other stays text, unopened, and the line says so: a stranger's path is never signed in to.
* **A PDF** dropped on the line (or its path pasted) is attached as `[PDF #n report.pdf]`; on a `/command` line it stays a path. Sending waits while its text is read (*Reading the attached PDF*; Esc keeps the draft).
  * The Anthropic and OpenAI APIs get the PDF itself (Anthropic reads 100 pages at most and warns of more).
  * Other servers get its first 100 pages' text; a scan or a locked PDF is refused. Text over half the context window is cut, the rest left to `read_pdf`.
  * A line's pictures and PDFs may total *LLM max images (MB)*; a PDF sent as text counts its text (32 MB a file at most). A saved session keeps the PDF's text, not the file.
* **On a Mac** the paste keys differ (Cmd+V is text only), and Option+Delete deletes a word: [What differs](MAC.md#what-differs).

### Completion lists

A list opens over the input line as you type `/` at the start, a command's argument, or `@`, `#`, `$`, `%`, `^` or `*` before a word.

* **Keys:** ↑/↓ move the highlight, Enter or Tab applies it, Esc closes the list (it stays closed for that word).
* **Mouse:** a click highlights a row, a double-click applies it. The wheel over the list moves the highlight; elsewhere it scrolls the transcript. A click on the `▲▼` row pages.

## Panes

* **Keys:** ←/→ switch tabs, ↑/↓ move, Enter edits or toggles, Space flips an on/off row, Esc closes or goes back a page.
* **Second row of tabs** (`/settings`, `/tools`, `/skills`' Offered, `/mcp`'s Tools): ←/→ step through each sub-tab and on into the next tab; Tab and Shift+Tab jump a whole tab.
* **Under the list:** the row's description and default. A number after a tab's name (`Client³`) counts its settings changed from the default.
* **Dim tabs** (`/tools`): a menu whose switch is off is dim and italic, as is a group whose menus are all off.
* **Type to filter** on `/sessions`, `/server`, `/model`, `/docker`, the saved lists, the allowed commands and the Offered tabs; the first Esc clears it.
* **Find** on the read-only panes (`/help`, `/sys`, `/about`, `/tree`, `/docker logs --pane`…): type to mark every match; Enter or F3 goes to the next, Shift+Enter or Shift+F3 back.
* **Lettered buttons** (`/webui`, an `/ad` card, `/help`'s key check…): a letter presses its button; Shift+letter starts the filter or the find.
* **Pasting** into a filter or a find (Ctrl+V or a right-click) adds its first line, up to 40 characters.
* **Asking first:** a remove asks, with No highlighted; `y` or `n` answers.
* **Mouse:** a click moves the cursor, a double-click picks a row or tab, a click on the `▲▼` row pages. The × at the top right is Esc; a double-click outside a pane closes it.
* **On a Mac:** selecting terminal text with the mouse, and going back in a find: [What differs](MAC.md#what-differs).

### Pane keys

* **In every list** ↑/↓ move, Enter or a double-click picks, Esc closes; key letters act at once.
* **Where rows can be removed**, Delete removes the highlighted one, asking where that list asks. So do the settings' lists: the Docker server match words, the Network host lists, the *Advisor list*.

| Pane | Keys |
|---|---|
| **Conversation** | |
| `/memory` | Enter: edit, disable/enable, forget (asks) · Delete forgets (asks) · `a` add · `m` *Memory mode* |
| `/queue` | Enter or Delete removes (asks) · `c` clear all · `s` send (while held, or under *Queue mode* `manual`) · `m` *Queue mode* · `k` *Queue cancel mode* |
| `/rewind` | Enter rewinds to before the message |
| `/rewind`'s files question | `f` conversation and files · `c` conversation only · Enter chooses · Esc cancels |
| Files changed since (`/undo`, `/rewind … files`) | `r` restore over them (yours to the Recycle bin first) · `s` skip them · Esc cancels |
| `/sessions` | Enter opens · type to filter |
| `/plan open` | Enter opens (with `--chat`, to talk it through) · `o` open · `c` talk it through · `l` [Ralph loop](COMMANDS.md#ralph-loop) (outside plan mode) · type to filter |
| `/plan edit` | Enter opens the file in its default app · type to filter |
| The plan approval pane | `a` approve & run · `f` clear context & run · `l` Ralph loop · `r` keep refining · `g` close gaps · `c` cancel plan · Esc keeps refining |
| Close gaps: who reviews? (after `g`, while advisors are offered) | `m` the model · `a` an advisor finds the gaps · `y` any (the model asks an advisor where one fits) · `c` the model, an advisor confirms · Esc back |
| A Ralph loop's checkpoint | `k` keep · `r` redo with a note (typed below) · `v` view the changes · `s` stop the loop · Esc keeps |
| A Ralph loop's by-hand check | `w` it works · `r` redo with a note (typed below) · `v` view the changes (where it opens, so Enter alone ticks nothing) · `s` stop the loop · Esc stops |
| A code audit's done pane | `p` fix via plan (with a finding open) · `g` more passes… · `k` keep the report · `d` discard it · Esc keeps |
| `/find` | type to find (a paste or right-click adds a first line) · Enter or F3 older · Shift+Enter or Shift+F3 newer · PgUp/PgDn scroll · Ctrl+↑/↓ previous or next message · Esc, Ctrl+F or a double-click outside done |
| Info panes (`/help`, `/sys`, `/about`, `/tree`…) | ↑/↓, PgUp/PgDn scroll · ←/→ or Tab switch tabs · type to find · Esc closes |
| `/update` | `i` install · `l` later · ↑/↓, PgUp/PgDn scroll · a double-click on the release link opens it · Esc is later. Typing finds nothing here. |
| `/update`, with one under way | `c` cancel update · `k` keep · Esc keeps. Typing finds nothing here. |
| `/update` or `/update rollback` checking, under a reply | Esc stops it. |
| `/help` › Keys | `k` **⌨ key check**: swaps in `/keycheck`'s list; pressed again, shows the keys |
| **Saved lists** | |
| `/eventlog` | ←/→ logs · Enter opens the event (`c` copy, `a` ask, Esc back to the list) · `r` refresh · `m` more · `c` computer · type to filter |
| `/services` | ←/→ states · Enter opens the service (`c` copy, `a` ask, and on a readwrite computer `s` start, `x` stop, `r` restart, `t` start type, each asking) · `r` refresh · `c` computer · type to filter |
| `/tasks` | ←/→ states · Enter opens the task (`c` copy, `a` ask, `h` history, and as allowed `r` run, `e` end, `n` enable, `d` disable, each asking) · `r` refresh · `c` computer · `f` folder · `w` Windows' own · type to filter |
| `/ad` card | Long columns cut to the pane · a double-click on a row (member, result, group, manager or owner) opens its card, Esc back · `c` copy the card · `a` ask · Shift+letter finds |
| `/ad` card, on a group or a locked user | on a group, `n` nested or direct, `u` users only or all · on a locked user, **Search 4740** opens the PDC emulator's lockout events on `/eventlog` · a name several accounts share opens as a card of them |
| `/youtube`, `/youtube list` | Enter plays |
| `/youtube <words>`, `/youtube playlist` | Enter plays · `s` saves the video |
| `/prompts` | Enter sends (queued during a reply) · `d` to the draft line · `a` add · `s` save the draft · `e` edit (a long one in the draft editor) · `m` save as a [custom command](COMMANDS.md#custom-commands) · `r` or Delete removes, `c` clears all (both ask) · Shift+↑/↓, Shift+Home/End move · type to filter |
| `/browser list` | Enter opens in the [Browser window](#the-browser-window) · `a` add · `n` name · `e` address |
| `/youtube`, `/youtube list`, `/browser list` | `d` or Delete removes (asks) · `c` clears all (asks) · Shift+↑/↓, Shift+Home/End move · type to filter |
| **Tools and safety** | |
| `/tools shell` | `c` the allowed commands · `p` the police |
| `/tools shell allowed` | Enter or Delete removes (asks) · `p` steps *Shell command policy* (yolo asks; a no goes on to off) · type to filter |
| `/tools shell police` | `s` the forbidden strings |
| *Shell police forbidden strings* | top row adds · Enter or Delete removes (asks) · `r` restore defaults (asks) |
| `/tools web` | `m` *Web browser mode* · `o` opens the Browser window (Windows, macOS 14+) |
| `/tools files` · `image` · `obsidian` · `advisors` · `print` | `b` *File browser/tree mode* · `e` *Image edit mode* · `v` *Obsidian vault* · `a` *Advisor list* · `p` *Print default printer* |
| `/tools camera` | `w` watch · `l` live · `s` snap · `c` screen |
| The camera pane | Space takes · `r` retake · Enter attaches · Esc drops |
| The shell approval | `d` deny · `o` once · `s` session · `a` always · `v` the whole command · Esc denies |
| A database write's approval | `d` deny · `o` once · `s` session · `v` the whole statement · Esc denies |
| The project file's approval | `d` deny · `o` once · `s` session · Esc denies: the model's change to `NEON.md`, `AGENTS.md` or `CLAUDE.md` |
| **Servers and processes** | |
| `/server`, `/model` | Enter chooses · type to filter |
| *Embedded models* | `s` size · `i` installed or not · `d` drafter · `o` sort · `x` uncensored |
| `/process` | Enter opens the [window](#the-process-window) · `k` kills (asks) · `f` all, running, ended |
| `/docker` | Enter opens · `r` refresh · `l` LLM servers only · type to filter |
| **Skills** | |
| `/skills` › a skill | Enter on a row: move (asks), copy (asks), rename, edit, revert, lock/unlock, disable/enable, delete (asks) |
| `/skills` › a skill › `revert` | Enter puts the version back · `d` or Delete removes it (asks) · `c` clear all (asks) |
| `/commands` | ←/→ all, profile, global · **new command…** asks a name and where, then opens the template · type to filter |
| `/commands` › a command | Enter on a row: use (to the draft line), move (asks), copy, rename, edit, delete (asks) · a Shadowed file: rename, edit, delete, and for a hidden global one move to another profile · a Skipped file: edit, delete |
| **Shopping** | |
| `/shopping` | Enter or Space ticks · `i` add · `p` place at an aisle or section · `n` rename · `r` or Delete remove · `a` tick all · `c` clear ticked · `s` stores · `+` / `-` quantity |
| Its add page (`i`) | Enter on the first row types new items · Enter on a past item adds it · `x` or Delete forgets it · type to filter |
| *Shopping stores* (`s`) | Enter shops there (the first row adds one) · `l` layout · `n` rename · `r` or Delete remove (asks) · `f` its folder · `i` reindex |
| A store's layout (`l`) | tabs **Aisles**, **Sections**, **Facts**, **Guesses** · Enter renames, moves or removes · Delete removes · on **Aisles**, Shift+↑/↓ move an aisle along the walk, Shift+Home/End to either end |
| **Settings and bars** | |
| `/settings changed` | Enter edits · `r` back to default |
| `/toolbar edit`, `/perfbar edit`, *Status bar* | [Bar checklists](#bar-checklists) |
| `/homebar edit` | **On the bar**: [Bar checklists](#bar-checklists); Enter for rename and emoji · other tabs: Space or Enter adds or removes, type to filter |
| A room's pane on the home bar | Space switches a device · Enter its actions |
| **Monitors** | |
| The monitors pane (`/monitor`, the 🖥️ chip) | ←/→ a monitor's tab · Enter switches an input or applies a layout row · Esc closes |
| A monitor's *Input names…* | Enter renames · `h` hides or shows · Delete clears the name · Esc back to the monitor's tab |
| A monitor's *This PC's input…* | Enter picks · Esc back to the monitor's tab |

### The event log pane

`/eventlog` shows a computer's events, newest first.

* **Tabs:** one per log (Application, System, Security, Setup, and the one you named). A tab Windows refused reads "(no access)".
* **A row:** the time, a level icon, the provider, the id and the message's first line; a dim line marks each new day. Icons: ◆ critical, ● error, ▲ warning, ○ information; ✗ and ✓ Security's audit failures and successes.
* **Buttons** on the title row: `⟳ refresh`, `more` (lit while there is a next page) and `computer` (with more than one ticked).
* **An event's page** shows everything recorded, its XML too, as plain text: nothing in an event is read as markup or a terminal code.
* **On a Mac** the title is `🪵 Event logs · this Mac`, with tabs `system`, `crashes` and `install` (and a `.logarchive` you named), each read when first opened; no `computer` button. Icons: ◆ fault, ● error or a crash, ▲ a jetsam, ○ default and the other reports, · the rest.

### The services and tasks panes

Windows only.

* **`/services`:** a row per service (name, display name, state, a failure's exit code), a tab per state.
* **`/tasks`:** the scheduled tasks (path, state, last run and result, next run); Windows' own under `\Microsoft\` stay hidden until `w`.
* **Enter** opens one whole, with the actions its computer allows as buttons. Each asks first; a refusal (protected, a driver, privileged, not in `control`) says why.
* Times are the computer's own clock.

### The monitors pane

Windows only. `/monitor` or a right-click on the home bar's 🖥️.

* **Layout tab** (with two or more monitors): Extend all, Duplicate, Only *n* and Make *n* primary, leaving out a row that would change nothing. Its caption names a monitor showing another input.
* **A tab per monitor:** its inputs (● current, ↩ the one the app switched it away from, `this PC` your pick), then Only this monitor, Make primary, *This PC's input…*, *Enter a code…* and *Input names…*.
* **This PC's input…** picks which input this PC is on: the one shown now, one by name, or none (the cable guesses: an HDMI input on a DisplayPort cable, or the reverse, is another input). An adapter cable can fool the guess; a pick fixes it.
* **Numbers** are the screen capture's (`monitor:2`).
* **Off the desktop:** on an NVIDIA card, a monitor still lists and switches its inputs, and this PC's input adds it back with [Extend all](TOOLS.md#monitors). Otherwise it's dim, with only the rows that bring it back.
* **Asks first** when a switch would leave no monitor showing this PC, or a layout row affects one on another input (judged by your pick, else the cable, else the app's own switch).
* **No inputs?** Many monitors ship with DDC/CI off in their own on-screen menu (look for "DDC/CI"); a duplicated monitor's inputs can't be told apart until Extend.

### Folder picker

* **Opens from** `/cwd browse` and the *Working directory (cwd)* and *Obsidian vault* rows: a folder tree, with `⌂ profile` (the profile's `files` folder) and `▣ images` above the drives.
* **Keys:** Space, → and ← (or a click on a folder's icon) open and close folders, `-` collapses all, and Enter or a double-click on a name chooses.

### File browser

* **`/browse [<folder>]`** opens a tree of the working directory, folders first. *File browser/tree mode* decides what is listed; `.git` folders and links are left out.
* **Keys:** Space, → and ← open and close folders, `-` collapses all, and Enter or a double-click opens a file in its default app.

## Keyboard shortcuts

Each runs its command as if typed and keeps your draft. The last column says what it does during a reply: runs at once, opens over it, waits for it, or stops it first.

| Shortcut | Runs | During a reply |
|---|---|---|
| Ctrl+. | `/terminal` | at once |
| Ctrl+/ | `/settings` | over it |
| Ctrl+B | `/browser` (the [Browser window](#the-browser-window)) | at once |
| Ctrl+E | `/explore` | at once |
| Ctrl+F | `/find` | waits |
| Ctrl+G | `/log` (the [log window](#the-log-window)) | at once |
| Ctrl+H | `/help` | over it |
| Ctrl+J | the emoji picker (not on a Mac) | idle line only |
| Ctrl+L | cancels the background learning (🧠) | at once |
| Ctrl+M | `/model` | waits |
| Ctrl+N | `/rename` | over it |
| Ctrl+O | folds or unfolds tool calls, code, diffs and thinking | at once |
| Ctrl+P | `/profile` | waits |
| Ctrl+Q | `/queue` | over it |
| Ctrl+R | `/reasoning` | over it |
| Ctrl+S | `/server` | waits |
| Ctrl+T | `/tools` | over it |
| Ctrl+U | `/usage` | over it |
| Ctrl+W | `/webui browser` (the [Web UI](#web-ui) on the app's computer) | at once |
| Ctrl+Y | `/sys` | over it |
| Ctrl+Z | `/theme` | waits |
| Ctrl+Alt+B | `/botchat --resume` | ends a running botchat at once, else waits |
| Ctrl+Alt+C | `/clear` | stops it first |
| Ctrl+Alt+D | `/mcp` | over it |
| Ctrl+Alt+K | `/botchat --kill` (the extra embedded servers a botchat left) | waits |
| Ctrl+Alt+M | `/memory` | over it |
| Ctrl+Alt+N | `/new` | stops it first |
| Ctrl+Alt+O | `/tools shell police` | over it |
| Ctrl+Alt+P | `/prompts` | over it |
| Ctrl+Alt+Q | `/queue clear` | at once |
| Ctrl+Alt+S | `/skills` | over it |
| Ctrl+Alt+T | `/tools shell allowed` | over it |
| Ctrl+Alt+U | `/comfy view` (the [picture viewer](#the-picture-viewer)) | at once |
| Ctrl+Alt+V | `/camera live` (the [camera window](#the-camera-window)) | at once |
| Ctrl+Alt+W | `/webui` (the Web UI pane) | over it |
| Ctrl+Alt+X ×2 | unloads the embedded model | at once |
| Ctrl+Alt+Y | `/sessions` | over it |
| Ctrl+Alt+Z | `/splash` | stops it first |
| F9 | `/camera snap`, attached to the line | waits |
| F10 | `/screen`, attached to the line | waits |

* **In a pane** a shortcut closes the pane and runs. Ctrl+., Ctrl+E, Ctrl+W, Ctrl+L, the window chords and Ctrl+Alt+X leave it open; Ctrl+F in a find closes it. A pane that asks you something ignores them all.
* **Window chords** (Ctrl+G, Ctrl+B, Ctrl+Alt+U, Ctrl+Alt+V) close their window when pressed again (Ctrl+B a minimized Browser window too); the typed command only opens it. Ctrl+B leaves the Browser window open while a bot check waits in it.
* **Bars** have no shortcut: `/perfbar`, `/toolbar`, `/homebar` and `/header` show or hide them.
* **Ctrl+Alt+X**, twice within two seconds, unloads the embedded model and frees its memory, canceling any reply, load or botchat using it. No server is connected until you pick one; *LLM URL* is kept. Other servers ignore it.
* **F9 and F10** need no modifier; the picture viewer and thumbnail browser use them for their own keys.
* **On a Mac**, Ctrl+] stands in for Ctrl+., Ctrl+D for Ctrl+M and Ctrl+Option+A for Ctrl+Alt+M (the originals work in the app's windows). Ctrl+Option chords need Option as Meta ([What differs](MAC.md#what-differs)).
* **A chord that does nothing** may be another program's global hotkey: `/keycheck`, or `k` on `/help`'s Keys tab, lists what holds each one.

## Commands typed during a reply

* **Open their pane over the reply:**
  * Info: `/help`, `/sys` (and `/sys persona`, `/sys operata`, `/sys vocalia`), `/about`, `/usage`, `/keycheck`, `/tree`, `/vault`, `/browse`
  * Settings: `/settings`, `/tools`, `/mcp`, `/skills`, `/commands`, `/comfy`, `/reasoning`, `/sampling`, `/webui`, `/toolbar edit`, `/perfbar edit`, `/homebar edit`, a home bar chip's pane
  * Conversation: `/memory`, `/queue`, `/sessions`, `/sessions title`, `/rename`, `/msgclear`, `/prompts`
  * Lists: `/docker`, `/eventlog`, `/net`, `/services`, `/tasks`, `/monitor`, `/ad`, `/youtube`, `/shopping`, `/browser list`, `/camera list`, `/ha states`, `/process`, `/update`, `/update rollback`
* **Run at once:**
  * Display: `/mark`, `/header`, `/toolbar`, `/perfbar`, `/homebar` (and `on`, `off`), `/expand`, `/collapse`, `/window`, `/log`, `/terminal`, `/shortcut`, `/explore`, `/open`, `/cwd`
  * Voice and camera: `/tts`, `/stt` (and `wake`, `interrupt`), `/camera live`, `watch`, `off`, `use`, `view` and `thumbs`, `/screen view` and `thumbs`
  * Pictures: `/view <path>` (and `--thumbs`), and `view` or `thumbs` after `/comfy`, `/splash`, `/pasted` or `/ha stills`
  * The reply's settings: `/reasoning <level>`, `/sampling <field> <value>`, `/queue clear`, `/queue mode <word>`, `/queue cancel <word>`
  * Others: `/update notes` and `cancel`, `/copy`, `/remember`, `/timer`, `/ha`, `/webui copy`, `/webui browser`, `/browser <url>`, `/prompts <n>`, `/process <id>`, `/docker logs <container>`, `/monitor list`, `/commands save <name>` (saves the message the reply answers)
* **Stop the reply first:** `/clear`, `/new`, `/splash`, `/rewind`, `/undo` (`/undo purge` waits instead), `/exit`
* **Everything else** waits for the reply, queued behind earlier messages (*Queue cancel mode* applies).

In a pane over a reply, rows that would disturb it (a reconnect, the profile, the working directory, the theme, a session, a skill's page, an MCP server) answer *(not while a reply runs)*.

## The hints bar

The keys that apply right now, on a row of their own over the status bar (*Hints bar*). It shows one hint at a time, the first of:

1. The kill switch's warning (Ctrl+Alt+X, press again to unload).
2. A waiting second press: Ctrl+C to exit, Esc to rewind, Delete for a splash picture.
3. An open pane's keys.
4. The link or path under the pointer.
5. What the arrows do right now: the splash's pictures, the picture strip, `/read`'s keys.

* **auto** (the default): the row comes with a hint and stays, blank, until you send, a reply ends, a pane opens or closes, the window is resized, or 10 idle seconds pass. Typing never moves the input line; a hover never brings the row up.
* **always** keeps the row; **off** puts the hints at the end of the status bar's left group.
* During a reply it shows only a pane's keys or the kill switch's warning; **Esc to stop** stays on the status bar.
* A double-click opens `/settings`. It moves with the other bars in *Toolbar order*.

## The status bar

The row under the hints bar (*Status bar*); [Bar checklists](#bar-checklists) moves and pins its sections.

* **Left by default:** **Status** (🔊 🎤 👂 ✋, 🧠, 🏷️…), **Spinner** (during a reply, with its clock and **Esc to stop**; always on), **Queued**, **Timers** (⏰ countdowns, ⏱️ stopwatches), **Reading** (`/read`, `/echo`) and **Usage** (context and tokens).
* **Right by default:** **Model** (name and reasoning icon) and **Working directory** (off by default). The busy row keeps the same sides.
* **The spinner's word:** `thinking`, `writing` for the text, `writing present_audit` while the model writes a tool call, then the tool's name while it runs.
* **A call's size**, `(41.0k chars)`, shows past 1,000 characters where the server sends the call as it's written; elsewhere nothing shows until it's done.
* **A runaway `present_audit` call** (the model repeating itself) is stopped, and the reply ends on an error.
* **Narrow window:** the model's name and the path shrink, then go; then the right group gives way, then the left. The spinner goes last.
* **Background setups** (an embedded model's download, MCP servers connecting, the voice models' first load) show 📥, 🔌, 🎙️ or 🔈; the input line stays yours.
* **Leading the Status section:** 🔁 for a Ralph loop (`🔁 step 2/5 · pass 3`; Esc stops it, typing pauses it), 🔎 for a code audit (`🔎 2/5`, or `🔎 2` with no pass limit), 📝 in plan mode.
* **An audit that ends on its own** prints its open findings as a table (most severe first, at most 40) and opens its done pane on **Keep report** ([Pane keys](#pane-keys), [Code audit](COMMANDS.md#code-audit)).

| Double-click | Does |
|---|---|
| the model name | `/server` |
| the reasoning icon | `/reasoning` |
| the working directory | `/cwd browse` |
| the tokens or spinner | `/usage` |
| 🖼️/🎨 (ComfyUI rendering) | cancels the pictures; the reply goes on |
| 🧠 (background learning) | cancels it, like Ctrl+L |
| 📥 / 🔌 / 🎙️ / 🔈 | cancels that setup |
| the queued count | `/queue` |
| the timers | asks, then cancels them all |
| 📷 (camera on) | `/camera off` |
| 👥 / 🧟 (after a botchat) | `/botchat --resume` / `/botchat --kill` |
| blank space (a right-click too) | `/settings` |

## The toolbar

Icons under the status bar (*Tool bar*), each shown while checked there. A click names an icon; a double-click or a right-click opens it. "Dark" means on a dark background, as a group that is off shows.

| Toolbar item | Shown | Opens |
|---|---|---|
| ⚙️ 🪪 🧮 🔌 🪄 🎭 💬 📊 | always | `/settings`, `/profile`, `/theme`, `/mcp`, `/commands`, `/sys`, `/sessions`, `/usage` |
| 🛠️ | dark while no tool is offered (*LLM offer tools* off, or all off on `/tools`' Offered tab) | `/tools` |
| 🎓 | dark while *Agent skills* is off | `/skills` |
| 📱 | dark while *Web UI* is off | `/webui` |
| 💾 | dark while *Memory mode* is `off` | `/memory` |
| 📣 | always | `/prompts` |
| ⏳ | dark while *Queue mode* is `off` | `/queue` |
| 🙋 | dark while *Ask user* is off | `/tools ask` |
| 🔒 / 🔓 | *Shell command policy* `ask` / `yolo` (none under `off`) | `/tools shell allowed` |
| 👮 / 🥷 | *Shell police* on / off (none while the policy is `off`) | `/tools shell police` |
| 🐚 | dark under `off` | `/tools shell` |
| 📁 🖼️ 🌐 🦉 🐳 💎 🛢️ 🔮 🐬 🪶 🦆 🐘 🔗 🏠 🎨 📸 🖨️ | dark while off | `/tools files`, `image`, `web`, `advisors`, `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `sqlite`, `duckdb`, `postgres`, `unc`, `ha`, `comfy`, `camera`, `print` |
| 🪵 | dark while off, or where there are no event logs to read | `/tools eventlog` |
| 📡 | dark while off | `/tools network` |
| 🧩 📅 | dark while off; Windows only ([MAC.md](MAC.md#what-works)) | `/tools services`, `tasks` |
| 🏢 | dark while off, or on a system other than Windows or macOS | `/tools directory` |
| 📄 ⚡ | always | the [log window](#the-log-window); `/process` |
| 🎞️ 🪟 | always | the [picture viewer](#the-picture-viewer); the [thumbnail browser](#the-thumbnail-browser) (ComfyUI's folder) |
| 📺 ⭐ 🛒 | always | `/youtube list`, `/browser list`, `/shopping` |
| the working directory (`~` for your user folder) | pinned right | `/cwd browse` |
| **Model name** and its icon | pinned right, with a server connected | `/server`; the icon `/reasoning` |
| blank space | — | `/settings` |

* **Dividers** (`│`) show only between two visible items ([Bar checklists](#bar-checklists)).
* **Narrow window:** the icons come first; the model name and the path shrink, then go; then items drop from the right group, then the left.

## The performance bar

Meters updated once a second (*Performance bar*): **CPU**, **RAM**, **GPU**, **VRAM** (**GMEM** on a Mac), **NET**, **NET↓**, **NET↑** and **PROC**. `/perfbar text`, `gauge` or `led` (or `l` on its checklist) picks the look; `/perfbar` hides it.

* Values turn amber from 60 % and red from 85 %. A meter the machine can't read is left out.
* NET is the share of the link speed, NET↓ and NET↑ bits per second, on the busiest adapter with a gateway. PROC counts the model's background processes running.
* **On a Mac** RAM is Activity Monitor's *Memory Used*; GMEM is the GPU's share of what macOS lets it use.
* Meters are pinned left, center (the default) or right. On a narrow row they shrink, then show numbers only, then the side groups give way.
* A double-click or a right-click on the bar opens `/settings`.

## The home bar

Chips for Home Assistant rooms and devices, the shopping list and this PC's monitors (*Home bar*); see [TOOLS.md](TOOLS.md#home-assistant). `/homebar` hides or shows it.

* **Chips:** a light wears its own color, a room counts its lights on (`2/5`), sensors show values, thermostats current → target, players ▸ or ‖. Off is dim.
* **Alerts:** an open door, a leak, a jammed lock or a low battery turns amber or red. Every chip goes dim while Home Assistant is unreachable.
* **🛒 n** counts the shopping lines still to get and opens `/shopping`; it needs no Home Assistant.
* **🖥️** (Windows only, the glyph alone) is this PC's monitors: a double-click or right-click opens [the monitors pane](#the-monitors-pane).
* **📹** is a camera: a double-click or right-click shows its still in [the picture viewer](#the-picture-viewer) (else the transcript), kept in `home-cameras` in the profile's `images` folder. `…` while it comes, `!` if none came.
* **A click** shows a chip's state. **A double-click** toggles a light, switch or fan, turns a room's lights off (or on), runs a scene, plays or pauses, or moves a cover.
* **A right-click** opens the chip's actions (a room's: its devices). A chip shows `…` until a change is reported, `!` after six seconds without.
* **Asks first:** unlocking, opening a garage, gate or door, and disarming an alarm. An alarm that wants a code is disarmed in Home Assistant itself.
* **Live:** while shown, the bar follows its devices. A refused key isn't retried until the URL or key changes.
* **Narrow window:** names shrink, the side groups give way, then the center pages behind ‹ ›. A double-click or right-click on blank space opens `/settings`.

**The picker** (Enter on *Home bar*, or `/homebar edit`):

* **On the bar:** the chips under **Pinned left**, **Pinned center** and **Pinned right** ([Bar checklists](#bar-checklists)). Enter on one also offers:
  * **Rename on the bar…**: an alias ("PC"; blank shows the emoji alone). **Default name** puts Home Assistant's back; panes keep the real name.
  * **Change emoji…**: the [emoji grid](#the-avatar-picker), fixed whatever the state; **Default emoji** or 🚫 puts the chip's own back. `/homebar emoji <item> = <emoji>` does the same.
  * **+ Add divider** (the last row): a dim `│`, shown only between two chips of its group.
* **Other tabs:** **Rooms** (by floor), **Lights**, **Switches** (and fans), **Sensors**, **Climate**, **Media**, **Scenes** (and scripts), **Security** (locks, covers, alarms, cameras), **Zones** (people in each), **Other**, **Monitors** (Windows only), **Shopping**.
  * Space or Enter adds or removes; typing filters by name, id, room or label.
  * Without Home Assistant only **On the bar**, **Monitors** and **Shopping** show.

## Bar checklists

The *Status bar*, *Tool bar* (`/toolbar edit`) and *Performance bar* (`/perfbar edit`) checklists, and the home bar's **On the bar**, share these keys:

* Space or Enter checks or unchecks; Delete unchecks (on the home bar, takes off). None checked hides the toolbar or the performance bar.
* `a`, `n` and `d` pick all, none or the default; `d` also puts back the default order, sides and dividers.
* Shift+←/→ (or Alt+←/→) pins the item to the other side; on the performance and home bars, to the next group over (left ↔ center ↔ right).
* Shift+↑/↓ (or Alt+↑/↓) moves it a place, crossing into the next group at the edge; Shift+Home/End to its group's top or bottom.
* **Performance bar:** **look:** on the title row (`l`) steps text, gauge, led.
* **Status bar:** the Spinner can only be moved, not unchecked. The row reads `default`, `all`, `none`, or the left group's sections, `|`, then the right group's.
* **Tool bar dividers:** `+` (*+ divider* on the title row) puts a dim `│` after the item under the cursor; it moves like an item, and Delete takes it out. The default has three: `⚙️ 🛠️ 🎓 │ 💾 📣 ⏳ │ 🔒 👮 🐚 │ 📁 🖼️ 🌐 📱`.

*Toolbar order* orders the five bars: Shift+↑/↓ (or Alt+↑/↓) moves the bar under the cursor, Shift+Home/End to the top or bottom, `d` the default. A bar that is off keeps its place.

## The avatar picker

* Enter on *Model avatar* (or `/avatar`) opens a grid of every emoji your terminal draws (no flags); *User avatar* opens it for your own lines.
* **Tabs:** **⭐ For you** (matching the profile's name and persona), **🕘 Recent**, then the Unicode groups. Tab and Shift+Tab step them, the arrows move, a double-click picks.
* Type to search (`fox`, `red heart`) or paste an emoji; Esc clears. 🎲 picks at random; 🚫 clears the avatar.
* Enter on a hand or a person opens its six skin tones. Other profiles' avatars are marked.
* Recent picks and the last skin tone are kept per profile (`emoji.json` in its `configs`).

## The welcome splash

* *Welcome splash* `image`: one picture; ←/→ step through them. Delete twice on an empty line moves one of the profile's pictures to the folder's `.trash`.
* *Welcome splash* `robot`: a robot above the draft line follows the mouse (where the terminal reports it) and your typing; your first line takes it away. Short of room it shows just its head, or nothing.
* **Your own pictures** in the profile's `images\splash\` replace the built-in ones; `/splash view` or `/splash thumbs` opens the folder, `/splash purge` empties it.
* `/splash` shows the splash whatever *Welcome splash* says (the picture when it's `off`).
* **On the Web UI** the picture leads the transcript until the first message from either side; the robot isn't shown.

## The app's windows

What every window shares:

* **Focus:** opened by its key chord, a window stays behind the terminal; by a typed command, it comes to the front. However it closes (Esc, Alt+F4, ×, its chord), the terminal gets the keyboard back.
* **Keys:** Tab brings the terminal forward; a Ctrl or Alt chord the window doesn't use runs in the chat. The Browser window keeps every key for its pages; on Windows the video window keeps Tab.
* **Place and look:** a window reopens where it last closed, closes with the app, and wears the theme unless *Themed external windows* is off.
* **On a Mac:** the shared keys (⌘W, ⌃⌘F) and why there are no windows over SSH: [What differs](MAC.md#what-differs).

### The picture viewer

* **Opened by** a double-click on a picture in the transcript (*Image viewer* can name another program), `/view <image or folder>` (following new pictures), and `/comfy view`, `/camera view`, `/screen view`, `/splash view`, `/pasted view` or `/ha stills view`.
* With no window, `/view` draws in the transcript; a PDF goes to [the Browser window](#the-browser-window).

| Key | Action |
|---|---|
| ← / →, the wheel, **<** / **>** | Newer / older (newest at the left, where it follows new pictures) |
| Home / End | Newest / oldest |
| Right-click, Apps key, Shift+F10 | The [picture menu](#the-picture-menu) |
| F11 or double-click | Full screen |
| Drag the picture | Copies it where you drop it |
| Del twice within 2 s | Deletes the picture: to the Recycle bin (the Trash on a Mac) under *File deletes to Recycle bin*, else for good |
| F9 · ↑ / ↓ · F10 | Slide show on or off (5 s) · a second more or less · folder or random order |
| Esc | Stops the slide show, then leaves full screen, then closes |

* The viewer, the thumbnail browser and the picture strip follow each other's selection.
* **On a Mac** ⌫ is Del, a two-finger swipe steps, Control-click is a right-click.

### The picture menu

A right-click in the viewer or the thumbnail browser.

* **Edits:** rotate and flip, color (grayscale, sepia, negative, polaroid), resize, convert (PNG, JPEG, GIF, BMP), shrink the file under a size, strip metadata losslessly. *Image edit mode* decides whether an edit writes `photo-edited.png` or replaces the picture.
* **The file:** copy its path, show it in Explorer (Finder on a Mac), attach it, print it, or delete it without asking (to the Recycle bin or Trash under *File deletes to Recycle bin*, else for good).

### The thumbnail browser

`/view <folder> --thumbs`, `/comfy thumbs` (🪟), `/camera thumbs`, `/screen thumbs`, `/splash thumbs`, `/pasted thumbs` and `/ha stills thumbs` show a folder's pictures, oldest first, new ones at the end.

| Key | Action |
|---|---|
| Click / double-click or Enter | Select (the viewer follows) / open in the viewer |
| Arrows, PgUp / PgDn, Home / End, the wheel | Move, scroll |
| + / − or Ctrl+wheel | Bigger / smaller thumbnails |
| F5 | Re-read the folder and fit the thumbnails |
| Del twice within 2 s | Deletes the picture, as in the viewer (to the Recycle bin or Trash, else for good) |
| F11 or double-click between thumbnails | Full screen |
| Esc | Closes the menu, then leaves full screen, then closes |

* A right-click opens the picture menu, with *Open in the viewer* first.
* **On a Mac** ⌫ is Del; ⌘= and ⌘− or a pinch change the size.

### The log window

`/log` (Ctrl+G, 📄) shows this run's diagnostic log from the start, with or without `--log`: the newest 20,000 lines, colored by level. At the bottom it follows new lines; scrolled up it holds still (*paused*). Drag selects, Shift+click extends.

| Key | Action |
|---|---|
| ↑ / ↓, PgUp / PgDn, the wheel | Scroll; at the bottom it follows again |
| Ctrl+Home / Ctrl+End or Ctrl+E | First line / last line, following |
| Ctrl+A / Ctrl+C | Select all / copy |
| F11 or double-click | Full screen |
| Esc | Leaves full screen, then closes |

* **On a Mac** ⌘A and ⌘C select and copy, ⌘↑ and ⌘↓ go to the top and bottom.

### The process window

* `/process <id>` (or Enter on `/process`'s list, ⚡) shows a background process's last 5,000 lines live, with the log window's keys, stderr in the warning color. The title shows the id, command and state (`running`, `exited 0`, `stopped by you`).
* **Ctrl+K** twice within 3 seconds stops it and everything it started; the model hears of it.

### The Docker log window

* `/docker logs <container>` (or **logs** on a container in `/docker`'s pane, 🐳) follows a container's log live, with the log window's keys, stderr in the warning color.
* It starts with the last 200 lines, or the number you give. Another container switches the window in place.
* A stop, restart or a new container of the same name gets a dim line, and the window follows on, showing nothing twice.
* The title shows the container and whether it is following, waiting for it to start, waiting for Docker, or unable to read the log.
* None of it goes to the model, the session or the Web UI.
* A very long line from a container started with a terminal (`-t`) may show a time in the middle.

### The camera window

`/camera live` (Ctrl+Alt+V) shows the camera live until you close the window or type `/camera off` (macOS 14+ on a Mac).

* It never takes the keyboard. F11 or a double-click is full screen; Esc leaves full screen, then closes it.
* *Camera preview* `live` shows the camera pane's shot here as you frame it.
* Live view, the pane, watch mode and `/botchat` can share the camera at once. Ctrl+Alt+V closes only a window `/camera live` opened.
* **An unplugged camera freezes the window silently** on its last frame, with nothing in the transcript, until `/camera off`.

### The video window

Where YouTube videos play under *YouTube open links in* `youtube-window`, the default ([TOOLS.md](TOOLS.md#youtube)).

* A video or playlist link clicked in it (the YouTube logo, an end screen) plays where the setting says; anything else opens in your default browser.
* It doesn't take the keyboard. F11 is full screen; Esc leaves it, then closes the window. YouTube's own keys (Space or K, ←/→, F, M) work once you click into the video.
* **On a Mac** (macOS 14+, WebKit) the player's own full-screen button fills a Space of its own; Esc brings it back.

### The Browser window

A browser window of the app's own (Windows; macOS 14+ on a Mac, where it is WebKit: [The Browser window on a Mac](MAC.md#the-browser-window-on-a-mac)). When you close it, its cookies go to the web tools.

* **What opens it:** the web tools, for a bot check ([TOOLS.md](TOOLS.md#the-browser-window)), which a Web UI page can also see and drive ([A bot check from the page](#a-bot-check-from-the-page)). `/browser [url]` opens it for you (bare, on *Web browser home page*).
* **The bar:** back, forward, reload (✕ stops a load), home, a padlock (https) or a warning (http), the address box, Go, and open in your default browser.
* **The address box:** words that aren't an address search with *Web search method* (DuckDuckGo's site under `metasearch`, which has no results page); `file:`, `javascript:` and local paths are refused.
* **Keys:** Ctrl+W closes; F5 or Ctrl+R reloads; Alt+← / Alt+→ go back and forward; Ctrl+L, Alt+D or F6 go to the address box (Esc there restores it).
* **More keys:** Ctrl+H the history; Ctrl+D saves or unsaves the page; Ctrl+Shift+O the saved links; Esc stops a load and never closes the window.
* **Its keys win:** inside it, Ctrl+W and Ctrl+H are its own, not the terminal's. Ctrl+B in the terminal opens and closes it (never under tmux, whose prefix is Ctrl+B).
* **On a Mac**, Safari's keys (its other differences: [The Browser window on a Mac](MAC.md#the-browser-window-on-a-mac)):
  * ⌘W closes, ⌘R reloads, ⌘[ / ⌘] (or a mouse's side buttons) go back and forward, ⌘L the address box, ⌘⇧H home.
  * ⌘Y the history, ⌘D saves, ⌘⇧O or ⌘⌥B the saved links, ⌘. or Esc stops.
  * ⌘F finds (⌘G, ⌘⇧G), ⌘+ ⌘- ⌘0 zoom, ⌘P prints; ⌘C, ⌘V and the other editing keys work in the address box and the page.
  * Full screen is ⌃⌘F; a page's own full screen fills a Space of its own, as in Safari.
* **Full screen:** F11, keeping the bar. A page's own full screen (a video's button) takes the window and hides the bar. Esc leaves the page's first, then F11's.
* **History** (🕘, Ctrl+H): newest first, older by day, with **Clear history**. *Web browser privacy* `clear-history` clears it at each close; `keep-history` keeps it in `<profile>\configs\browser-history.json`. Cookies and sign-ins stay either way.
* **Saved links** (☆, ★ on a saved page): up to 190, the first 15 on the menu, the rest in submenus, all on `/browser list` (⭐). Kept in `<profile>\configs\browser-links.json`.
* An address holding a user name or password is never saved. The model reaches the links with `web_links` and `web_save_link` ([TOOLS.md](TOOLS.md#web)).
* **Per profile:** sign-ins and solved checks are kept per profile (a Mac's: [MAC.md](MAC.md#the-browser-window-on-a-mac)); switching profiles closes the window. Two copies of the app can each open it on different profiles; a second copy on the same profile can't open it.
* **Clearing:** `/browser clear history` clears the history now. `/browser clear all` asks, then clears the history, cookies, sign-ins, cache and site settings; saved links stay.
* **A PDF:** `/view report.pdf` shows a working-directory PDF in the window's viewer, from a copy deleted when the window closes; the open button opens the file in its own program.
  * Not kept in the history, can't be saved as a link, hands no cookies over. A policy that makes Edge download PDFs makes the window download it.
  * Where the window can't take it (a bot check waiting, no WebView2 runtime, a Mac before macOS 14 or over SSH), it opens in its own program (Preview on a Mac).

## Web UI

A browser front end for the loaded profile, on your phone or another computer: the chat as it happens, the questions the model's tools ask, the house, the shopping list and a video player.

Turn it on with `/webui on` or *Web UI* (`/settings` › Client › Web UI). Not headless.

Each page links to this version's source code on GitHub, and the sign-in page to the repository's home. A build between releases may link to a tag that doesn't exist yet, or to an older one without its changes.

### Signing in

* **The pane:** `/webui` (Ctrl+Alt+W, or the toolbar's 📱) shows a tab per address of the computer running NeonSidekick, each with a QR code and its sign-in link. Scan the code with the phone's camera, or open the link in a browser on the same network.
* **The addresses:** the HTTPS one first when there is one ([Tailscale Serve](#https-through-tailscale-serve)), then Tailscale's ([Away from home](#away-from-home)), then the home network's adapters, fastest first (`localhost` under `local`).
* **HTTPS only:** set *Web UI reach* to `local`; the tabs are then the HTTPS one and `localhost`.
* Other VPN tunnels (and WSL's virtual switch on Windows) aren't listed, but a browser through them connects with the address typed.
* **The link** signs the browser in and leaves the address bar at once; the browser keeps a cookie for a year.
* **The token:** each profile has its own, kept encrypted. `/webui reset`, or a new port, makes a new one and signs every browser out.
* A link sent through a messaging service hands it the token too: run `/webui reset` afterwards.
* **Without the pane** (a redirected console), `/webui` prints the links as notices, so the token reaches the scrollback.

#### The pane's buttons

* **The strip:** `📱 Web UI │ enabled │ lan │ reset │ HTTPS · 100.78.20.125 · 192.168.1.217`, three buttons ahead of the address tabs.
* `e` turns *Web UI* on or off, `l` switches *Web UI reach* between `lan` and `local`, `r` makes a new token after a yes (as `/webui reset`). The server restarts at once.
* **copy** (`c`) puts the shown tab's sign-in link on the clipboard; `/webui copy` puts the first tab's.
* **A double-click on the link or the QR code** opens it on the app's computer, as `/webui browser` does.
* **Over a reply** the buttons act at once; a phone watching that reply drops for a moment and reconnects.

#### On the app's computer: `/webui browser`

* `/webui browser` (or Ctrl+W) opens the Web UI signed in: in the Browser window under *Web open links in* `browser-window`, else in your default browser (always, with `--external`).
* **The link:** the default browser signs in as *Web browser sign in mode* says. The Browser window gets a one-time link (good once, for a minute) while *Web browser privacy* is `keep-history`, else the usual one.
* The transcript and the log say where it opened, never the link. The Browser window keeps no sign-in link in its history or saved links, never shows one to the model, and never hands the Web UI's cookie to the web tools.

#### Away from home

* Install Tailscale on both devices and sign them in to one tailnet. Scan the HTTPS tab's code once, or the Tailscale tab's without Serve.
* Nothing to open on the router; *Web UI reach* must be `lan`, or `local` with [Tailscale Serve](#https-through-tailscale-serve). If Windows calls Tailscale's network Public, the phone can't connect over plain http ([the firewall](#safety-and-the-network)).

#### HTTPS through Tailscale Serve

The page is plain HTTP. Tailscale Serve puts HTTPS in front of it with a real certificate, reachable only on your tailnet.

* **Once, in Tailscale's admin console:** turn on MagicDNS and HTTPS certificates (DNS page).
* **On the app's computer:** run `tailscale serve --bg <port>`, where `<port>` is *Web UI port*. `tailscale serve status` shows the address, `https://<computer>.<tailnet>.ts.net`. For HTTPS alone, set *Web UI reach* to `local` ([Signing in](#signing-in)).
* **Signing in:** `/webui` then opens on an **HTTPS** tab with that address's QR code and link. Windows finds the address itself; on a Mac, type it into *Web UI HTTPS address*.
* **Use `tailscale serve`, not `tailscale funnel`:** under `local`, Funnel puts the page on the internet with only the token in the way; `lan` refuses Funnel's requests. The HTTPS tab says which; a Funnel address gets only one-time links, each good once for a minute.
* **Another port:** Serve doesn't follow a new *Web UI port*: run `tailscale serve --bg <port>` again. A second profile takes its own HTTPS port, e.g. `tailscale serve --bg --https=8443 8766`.
* **No rule for the port:** `/webui` names the command that adds one. **To stop:** `tailscale serve reset`.
* The cookie is per address: a phone signed in over http signs in again over https.

### The tabs

* **The tabs:** Chat, Home, Shopping and YouTube, each shown only while its setting is on: *Web UI chat controls* (on by default), *Web UI home controls*, *Web UI shopping controls* and *Web UI YouTube controls* (off).
* **The ☰ menu:** Chat is a link in the header; the other tabs that are on sit behind ☰, which names the open one (🏠 Home ▾).
* **With Chat off** the page opens on Home, else Shopping, else YouTube, and the phone gets no transcript, message line or questions (the PC asks as always).
* With none on, the page names the four settings and opens a tab once one is on. A tab turned off while open moves to one that's on.

### The Chat tab

#### Messages

* **Drafts:** the page and the PC never see each other's drafts (one set aside with Esc at the PC included). Sent lines share the profile's message history, so ↑/↓ on either side bring them back.
* **Sending:** Enter sends on a desktop; on a phone it adds a line (tap ➤). ✕ clears the box and *Undo* puts it back. Stop (■) shows while a reply runs.
* **The dropdown** completes the `@ # $ % ^ *` mentions and, with *Web UI commands* on, the commands the page may run. `@` lists files only while *File tools* is on. *Web UI connection details* adds a `%` or `*` connection's server and database.
* **Queued:** a message goes after anything queued at the PC. During a reply it waits (counted in Queued, not in `/queue`) and goes when the reply ends, even under *Queue mode* `manual`. At most 20 wait, up to 48 KB each.
* **After a cancel** under *Queue cancel mode* `hold`, waiting messages are held under a **Resume** banner; a new message runs first.
* **Dropped** by `/clear`, `/new`, a resume, a rewind, a profile switch or *Queue cancel mode* `empty`, their text sent back to the page; `/queue clear` leaves them.
* **Stop** ends a reply, a compact, a botchat, an advisor, `/imagine`, `/test` or a `/loop`, and stops speech; before the reply's first word it takes the message back. It never stops a model loading (Esc or Ctrl+C at the PC) or your editor under `/draft`.

#### What the page shows

* **The transcript** as the terminal shows it: your lines, the replies (Markdown), pictures, tool lines, notices, thinking (*LLM show thinking*), diffs and folded tool runs; not the panes you open at the PC.
* **Folds** follow *Diff max lines*, *Diff collapse count* and *Tool collapse count*; a tap opens one. ↕ opens or closes every fold, as Ctrl+O does; ⤒ and ⤓ go to the top and bottom.
* It starts afresh when the screen does (`/clear`, a rewind, a resume, a theme change, a profile switch).
* **The status row** over the message box shows the PC's spinner and clock while it works, and the usage at the right (`~` while estimated under *LLM mid-turn usage*). A tap on the usage opens the `/usage` figures in a card (typed `/usage` stays refused).
* **🧠** leads the row while a reflection runs; two taps cancel it, as Ctrl+L does.

#### Prompts, history and memory

**📣**, at the message box's left (or a bare `/prompts` from the page), opens a sheet with three tabs:

* **📣 Prompts:** a tap sends a saved prompt as if typed; ✎ puts it on the message line; ⇪ saves the message line as a new one.
* **🕘 History:** a tap puts an earlier line on the message line. *Clear all* empties the history, stored and at the PC, as `/msgclear` does.
* **💾 Memory:** ⇪ saves the message line as a memory, as `/remember` does (refused with *Memory mode* off). A tap on a memory edits it or turns it off or on.
* A 🗑️ and *Clear all* take two taps. The PC's hint row flashes what the page saved, edited or deleted.

#### Pictures

* **Every picture the chat holds** shows as a thumbnail, even with *Show image thumbnails* off. The model's own `camera_capture` and `screen_capture` pictures show nowhere, as at the PC.
* **A tap** opens it full screen: swipe or ‹ › to step; *Open original* opens the file itself. On the newest picture it's live, replaced as new ones arrive.
* **It follows the PC:** moving to a picture at the PC (strip, viewer or thumbnail browser) moves an open full view to it.
* **🎞️** (while *ComfyUI picture strip* is on and there are pictures) opens a drawer of the strip, the PC's pick outlined; a tap there moves the PC's strip, viewer and thumbnail browser too.
* **🗑️** (two taps) deletes a picture as the viewer's Del does: to the Recycle bin or Trash under *File deletes to Recycle bin*, else for good. A pasted picture or a camera watch frame has none.
* A picture is served only while the page's transcript holds it: after `/clear` or a rewind its address answers nothing.
* **On a Mac**, where the app makes no thumbnails, the page shows the originals scaled down.

#### Questions

* **On both sides at once** (the first answer wins): a command's approval, `ask_user`, an allow (camera, screen, a database write), a yes/no, the plan's approval, a code audit's done pane and a reflection's skill.
* The page shows a command or a statement whole, and a reflection's skill as its summary with **Keep** and **Discard**.
* **The camera's shutter** (*Camera shutter* `user`) offers **Take and send**: the PC's camera takes the photo, with no preview.
* **At the PC only:** the plan's Close gaps and a code audit's reviewer page; meanwhile the page says "waiting for an answer at the PC".
* **A bot check:** the allow question, then a card ([A bot check from the page](#a-bot-check-from-the-page)).
* **The Chat badge:** on the other tabs, a dot while a reply runs and a count while a question waits, in the tab's title too (`(n)`). An Android phone also vibrates once on a new question.

#### The PC sees the phone

* Each change made on the page (a shopping line, a tile, a scene, the video remote, a saved video, a prompt…) flashes on the PC's hint row (📱 Kitchen light: toggle). None of it goes in the PC's transcript.

### The Shopping tab

* **The list** in the active store's aisle order, as the model reads it; a tap ticks or unticks a line. With two or more stores, a *Store* picker switches the active one (as `/shopping store <name>`).
* **Adding:** the *Add items* box takes what `/shopping add` does (`2 milk, eggs`), at most 100 at once. 🕘 lists past items, to add again or forget.
* **A line's sheet** (its ›, or a long-press): − and + change the quantity, rename (another line's name merges the two), 🗑️ removes.
* **The whole list:** ☑ all ticks every line (☐ none unticks them); ⊠ clear ticked takes the ticked lines off as bought. Removes and clears take two taps.
* **At the PC only:** placing a line, adding, renaming or removing stores, the layout editor, a store's folder and reindex, and clearing the whole list.
* **The PC follows:** its open `/shopping` pane shows the page's changes within a second.

### The Home tab

**Home** is the house and the PC at a glance, shown only with *Web UI home controls* on (off by default).

* **The tiles:** the home bar's items in its order, names and emoji, live and colored as on the bar (🛒 stays on Shopping).
  * A tap does what a double-click on the chip does; a camera's opens its sheet with a still and ↻.
  * A long-press or › opens the tile's sheet of actions.
* **Rooms:** a box per room (as `/homebar show <room>`), each Hue zone, and *No area* for scenes in no room. The home bar's rooms come first and open; the others are folded.
* **A box:** its lights on (`3 of 5 on`) and its switch (off if any light is on, else on), then *All lights*, a row per device with its switch or first action, and its scenes.
* **Reach:** only the home bar's and the rooms' devices can be read or acted on. Unlocking, opening a garage, gate or door, and disarming ask on the page first.
* **Ask the house:** a line for Home Assistant's Assist (as `/ha say`), its answer under it.
* **Now playing** (while a video is open, in the PC's window or on the YouTube tab): its title and a remote.
* **The PC:** CPU, RAM, GPU, VRAM, the network's rates, and the model's name, server, context window and usage.
* **The log:** each action and camera still viewed is logged with the phone's address; what was asked of the house is not. `/ha` typed in Chat follows *Web UI commands*, not this tab's setting.

### The YouTube tab

**YouTube** plays videos on the phone (or a browser) in the app's own player, so `youtube_control`, `youtube_status`, `/youtube pause` and Home's Now playing reach it.

* **Where plays go:** the model's plays go to the tab while *YouTube open links in* is `webui-window`.
* **The fallback:** with *Web UI YouTube controls* off (the default), or the Web UI not serving, `webui-window` plays in the PC's video window. Without a video window (a Mac before macOS 14) there is no tab.
* **With it on**, the tab shows whatever *YouTube open links in* says: a video you start there plays there; `youtube_play`, `/youtube` and `open_url` follow the setting.
* **The jump:** a play with no YouTube tab in view waits for one, and the open pages go there; while you're typing or answering, a *▶ A video is waiting — Watch* banner shows instead. Unopened, it goes after 10 minutes.
* **Which tab plays:** the one shown most recently; a playing tab keeps playing, even hidden. The others offer *Play here*, taking the video over from about 3 seconds back.
* **Sound:** a phone starts a video only after a tap on it. The tab tries with sound, then muted, else asks for the tap.
* **Resume:** leaving the tab, locking the phone or a reload stops the video and keeps its place; coming back offers *Resume "title" at 3:12*.
* **Saved videos** (`/youtube list`'s) sit under the player: a tap plays one here, whatever the setting says; ⋯ moves or removes it; ☆ saves the playing video.
* **⛶ Fill window** fills the browser's window with the video under a slim bar (YouTube's own button is still full screen). ✕ Exit, Esc or Back leave (after a tap on the video, Esc is YouTube's).
* **Limits:** on an iPhone *YouTube while speaking* `duck` does nothing (`pause` works).
* `/youtube` typed in Chat is refused, except bare `/youtube` and `/youtube list`, which point to this tab's saved videos; ask the model to play instead.
* **Trust:** the tab runs YouTube's player script in the Web UI's own page, so while it's open that script can do what the page can, but never read the sign-in cookie.
* The page lets in only YouTube's player script, its frame, and its thumbnails (`i.ytimg.com`).

### Commands from the page

With *Web UI commands* on (off by default), the page sends slash commands too. They run at the idle line as if typed at the PC, after anything queued there; under a `/botchat` they wait for its end. Off, every command is refused, naming the setting.

* **A command's questions** ask at the PC and on the page at once; the first answer wins.

| Kind | Commands | On the page |
|---|---|---|
| Run | Your [custom commands](COMMANDS.md#custom-commands), `/update notes`, `/new`, `/clear`, `/splash`, `/compact`, `/plan …` (`/plan ralph` too), `/mark <color>`, `/code-audit …`, `/claude`, `/advisor`, `/help <words>`, `/imagine`, `/loop …`, `/learn [<note>]`, `/test …`, `/pdf …`, `/docx …`, `/print <file\|reply>`, `/avatar <emoji>\|clear`, `/remember`, `/timer …`, `/rename <name>`, `/tts on\|off`, `/stt on\|off`, `/memory <mode>`, `/cwd`, `/model <id>`, `/reasoning <level>`, `/ha …` (not `stills`), `/docker …`, `/shopping …`, `/prompts list\|save\|remove\|<n>`, `/browser save\|unsave`, `/msgclear`, `/undo [<n>]` | Their lines; the status row shows its spinner while it runs |
| Pick | `/model`, `/reasoning`, `/rewind [<n>]`, `/plan open [--chat]` | A card with the choices, the one in use marked, a filter for a long list |
| List | `/ha states`, `/docker logs`, `/eventlog …`, `/net …`, `/services …`, `/tasks …`, `/ad …`, `/code-audit open`, `/print`, `/print printers`, `/camera list`, `/screen list`, `/tree` (needs *File tools*), `/vault` (needs *Obsidian tools*), `/process`, `/comfy status`, `/comfy offered` | A card with the lines; Close |
| Never | `/exit`, `/webui`, `/cmd`, `/profile`, `/plan edit`, `/cwd <path>`, `/sessions`, `/skills`, `/commands`, `/youtube` (bare and `list` point at the tab), `/queue`, `/botchat`, `/sampling`, `/server`, `/usage` | Refused: they run at the PC only |
| Page | bare `/prompts` | Opens the 📣 sheet on its Prompts tab (📣 does too, even with *Web UI commands* off) |
| Other | Every form that opens a pane or a window at the PC, and `/update`'s others, since installing happens at the PC (`/settings`, `/theme`, `/view`, `/prompts list --clear`, `/camera`, `/screen`, `/ha stills`, `/undo purge`…) | Refused, naming the form that works here when there is one |

* **A Ralph loop:** its confirm, checkpoint and by-hand check ask on both sides; the status row shows 🔁, and Stop ends the loop.
* **`/loop`** runs as at the PC (a looped `/read` is refused).
* **Pointers:** bare `/shopping` points at the Shopping tab; bare `/youtube` and `/youtube list` at the YouTube tab's saved videos (or its setting while the tab is off).
* **What differs:** `/ha` and `/docker` ask before a change (at the PC they don't), though `/ha` runs a read or a *Home Assistant safe services* call unasked. A bare `/docker` lists the containers.
* `/msgclear` asks on both sides at once. `/rewind` puts the text on the page's draft; its pictures, PDFs and pasted blocks come back on the PC's line only.
* **Refused when it runs:** `/model <id>` and `/reasoning`, when connecting would mean looking for a server or an embedded model not installed (those pickers and that download are the PC's); `/tts on` and `/stt on` when their voice models aren't downloaded.
* **Stop** ends `/claude` too, but not a model loading (Esc or Ctrl+C at the PC).
* **An unknown command:** the page offers *Send as a message*, and for `//tools` *Use /tools*.
* **The log:** each command from the page is logged with its name, the address and the verdict, never the rest of the line.

### A bot check from the page

A bot check (a Cloudflare click, a captcha) runs in the PC's Browser window, since its pass is a cookie only that browser can use ([TOOLS.md](TOOLS.md#the-browser-window)). The Chat tab brings it to you:

* **The allow question** (*Web browser botcheck window* `ask`): Deny, Allow once or Allow for this session, on both sides at once; the first answer wins.
* **The card** shows the window live, with *Give up* (alone with *Web UI bot check control* off; it's on by default).
  * **Tap** to click; **touch and hold** to press and hold, let go to release.
  * 🔍 zooms (fit, 2×, 3×); ▲ ▼ scroll the page.
  * The text box types into the field you tapped; ⏎ ⌫ ⇥ send Enter, Backspace and Tab.
* **It ends** when the page gets through, on *Give up* or Esc at either side, on the page's Stop, or after 5 minutes.
* **The PC:** the first input from a page flashes on the hint row (📱) and is logged with the phone's address; what you type is never logged.
* A minimized window is restored, without taking the keyboard, so it draws; a locked PC keeps drawing it, a sleeping one stops. A Mac: [The Browser window on a Mac](MAC.md#the-browser-window-on-a-mac).

### Safety and the network

* **Who may connect,** always with the cookie: under `lan`, a private address (home network, tailnet), Funnel's internet requests refused; under `local`, this computer and what a proxy on it forwards.
* Private here doesn't mean safe (a router's NAT loopback, Docker or Tailscale can present one): the token is the gate.
* **Plain HTTP:** the page itself is plain HTTP; for HTTPS, see [HTTPS through Tailscale Serve](#https-through-tailscale-serve).
* **The firewall:** Windows asks to let the app through the first time it serves the network; allow it on private networks (on one Windows calls Public, the phone can't connect). A Mac asks too.
* **A port that won't open:** another program uses it, or Windows reserves it (`netsh int ipv4 show excludedportrange protocol=tcp` lists the reserved ranges). Pick another in *Web UI port*.
* **Too fast:** taps, a bot check card's input, Ask the house and pasted sign-ins are limited per address; past that the page says to slow down.

### The page's look

* **The theme:** with *Web UI profile theme* on (the default), the pages take the profile's theme and follow a `/theme` at once; off, they keep their own dark look.
* **Add to Home Screen** installs the page as an app that opens full screen on Chat. One added from Safari keeps its own cookies: paste the sign-in link (or its token) on its sign-in page.
* **Reduced motion:** with the phone's reduce-motion on, the spinner and animations stand still. The controls carry labels for a screen reader.
* **A new version** reloads the page once, its draft kept.
