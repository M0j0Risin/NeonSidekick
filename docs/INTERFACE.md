# Using the app

The screen, the input line, panes, keys, the bars and the app's own windows. Back to the [README](../README.md). Settings named in *italics* are in [SETTINGS.md](SETTINGS.md), with their values and defaults.

## Contents

- [Words used here](#words-used-here) · [The screen](#the-screen) · [The input line](#the-input-line) ([completion lists](#completion-lists))
- [Panes](#panes) · [Keyboard shortcuts](#keyboard-shortcuts) · [Commands typed during a reply](#commands-typed-during-a-reply)
- [The hints bar](#the-hints-bar) · [The status bar](#the-status-bar) · [The toolbar](#the-toolbar) · [The performance bar](#the-performance-bar)
- [The home bar](#the-home-bar) · [Bar checklists](#bar-checklists) · [The avatar picker](#the-avatar-picker) · [The welcome splash](#the-welcome-splash)
- [The app's windows](#the-apps-windows)
  - [picture viewer](#the-picture-viewer) · [picture menu](#the-picture-menu) · [thumbnail browser](#the-thumbnail-browser) · [log](#the-log-window)
  - [process](#the-process-window) · [camera](#the-camera-window) · [video](#the-video-window) · [Browser](#the-browser-window)
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

## The screen

From top to bottom:

* **The transcript**, under the banner (*Show header*). PgUp/PgDn scroll it; Ctrl+Home and Ctrl+End go to its top and bottom.
* **The picture strip**, while there are ComfyUI pictures: its 🎞️ opens the [picture viewer](#the-picture-viewer), × closes the strip.
* **The upper rule:** the session's name (a double-click renames it), **⤡** (a click folds or unfolds everything, like Ctrl+O), and a 🎞️ that brings a closed picture strip back.
* **[The input line](#the-input-line).** A dim ☺ at its right end opens the emoji picker (*Emoji picker*).
* **The lower rule:** **▶ n** counts the background processes still running; a double-click opens `/process`.
* **A mark:** `/mark <color>` draws both rules in that color for the conversation, kept with its session; `/mark default` takes it off.
* **Five bars**, in *Toolbar order*: [hints](#the-hints-bar) (while there is a hint), [status](#the-status-bar), [toolbar](#the-toolbar), [performance](#the-performance-bar), [home](#the-home-bar).
* **A short window** drops the home bar first, then the performance bar, then the toolbar; the hints bar goes last.

## The input line

* **Editing**, even during a reply: ←/→, Home/End (again for the whole message), Ctrl+←/→ by word, Shift to select. Ctrl+Backspace and Ctrl+Delete delete a word.
* **Enter** sends; Ctrl+Enter starts a new line. ↑/↓ step through earlier messages and move through a wrapped draft.
* **Copy and paste:** Ctrl+A or a drag selects, Ctrl+C copies, Ctrl+X cuts. A right-click on the input line, or Alt+V, pastes, pictures too.
* **Ctrl+C** with nothing selected stops the speech, else cancels the reply; twice at an idle line, it exits.
* **Lists:** `/` commands, `@` files, `#` skills, `$` tools, `%` database connections, `^` ComfyUI workflows, `*` UNC shares; inside a command's text too, except in `/read`, `/view`, `/print` and `/pdf` ([Completion lists](#completion-lists)).
* **Drop a picture** from the picture strip or the transcript onto the input row to attach it (**🖼️ drop on line**).
* **During a reply** Enter queues the message. Esc stops the speech, then closes an open list, then cancels the reply; it never clears your draft.
* **At the idle line** Esc clears the draft (↑ brings it back); Esc twice on an empty line opens `/rewind`.
* **Voice:** with *STT input* on, the *STT push-to-talk key* records a message; with *STT wake* on, saying the wake phrase does.
* **Tool calls** show as `🛠️ read_file notes.md` with the result under them (`→ 4 lines`, ✗ when it failed). Long results fold once the run ends; Ctrl+O or `/expand` opens them.
* **During `/read`** (📖): Space on an empty line pauses and resumes; ←/→ step a sentence.
* **The mouse:** a double-click selects a word, and on the `›` the whole draft.

### Pasting a picture or a PDF

* **A picture:** a right-click on the input line or Alt+V pastes a copied picture as `[Image #n]`; so does Ctrl+V where the terminal passes it on (Windows Terminal keeps it for text). With no picture, the text is pasted.
* Once sent, the picture is saved in *Pasted images folder* under the profile's `images` folder, and the model gets its path.
* **A PDF** dropped on the line (or its path pasted) is attached as `[PDF #n report.pdf]`; on a `/command` line it stays a path. Sending waits while its text is read (*Reading the attached PDF*; Esc there keeps the draft).
  * The Anthropic and OpenAI APIs get the PDF itself (Anthropic reads 100 pages at most and warns of a longer one).
  * Other servers get the text of its first 100 pages. A scan or a locked PDF is refused there; text over half the context window is cut, the rest left to `read_pdf`.
  * The line's pictures and PDFs together may total at most *LLM max images (MB)*; a PDF sent as text counts its text (32 MB a file at most). A saved session keeps the PDF's text, not the file.
* **On a Mac** the paste keys differ (Cmd+V is text only), and Option+Delete deletes a word: [What differs](MAC.md#what-differs).

### Completion lists

A list opens over the input line as you type `/` at the start, a command's argument, or `@`, `#`, `$`, `%`, `^` or `*` before a word.

* **Keys:** ↑/↓ move the highlight, Enter or Tab applies it, Esc closes the list (it stays closed for that word).
* **Mouse:** a click highlights a row, a double-click applies it. The wheel over the list moves the highlight; elsewhere it scrolls the transcript. A click on the `▲▼` row pages.

## Panes

* **Keys:** ←/→ switch tabs, ↑/↓ move, Enter edits or toggles, Space flips an on/off row, Esc closes or goes back a page.
* **Second row of tabs** (`/settings`, `/tools`, `/skills`' Offered, `/mcp`'s Tools): ←/→ step through each sub-tab and on into the next tab; Tab and Shift+Tab jump a whole tab.
* **Under the list:** the row's description and default. A number after a tab's name (`Client³`) counts its settings changed from the default.
* **Dim tabs** (`/tools`): a menu whose switch is off is dim and italic, and so is a group whose menus are all off.
* **Type to filter** on `/sessions`, `/server`, `/model`, `/docker`, the saved lists, the allowed commands and the Offered tabs; the first Esc clears it.
* **Find** on the read-only panes (`/help`, `/sys`, `/about`, `/tree`, `/docker logs`…): type to mark every match; Enter or F3 goes to the next, Shift+Enter or Shift+F3 back.
* **Lettered buttons** (`/webui`, an `/ad` card, `/help`'s key check…): a letter presses its button, and Shift+letter starts the filter or the find.
* **Pasting** into a filter or a find (Ctrl+V or a right-click) adds its first line, up to 40 characters.
* **Asking first:** removing something asks first, with No highlighted; `y` or `n` answers.
* **Mouse:** a click moves the cursor, a double-click picks a row or tab, and a click on the `▲▼` row under a long list pages. The × at the top right is Esc; a double-click outside a pane closes it.
* **On a Mac:** selecting terminal text with the mouse, and going back in a find, are in [What differs](MAC.md#what-differs).

### Pane keys

* **In every list** ↑/↓ move, Enter or a double-click picks, Esc closes; key letters act at once.
* **Where rows can be removed**, Delete removes the highlighted one, asking where that list asks. The settings' lists take it too: the Docker server match words, the Network host lists, the *Advisor list*.

| Pane | Keys |
|---|---|
| **Conversation** | |
| `/memory` | Enter: edit, disable/enable, forget (asks) · Delete forgets (asks) · `a` add · `m` *Memory mode* |
| `/queue` | Enter or Delete removes (asks) · `c` clear all · `s` send (while held, or under *Queue mode* `manual`) · `m` *Queue mode* · `k` *Queue cancel mode* |
| `/rewind` | Enter rewinds to before the message |
| `/sessions` | Enter opens · type to filter |
| `/plan open` | Enter opens (with `--chat`, to talk it through) · `o` open · `c` talk it through · `l` run as a [Ralph loop](COMMANDS.md#ralph-loop), outside plan mode · type to filter |
| `/plan edit` | Enter opens the file in its default app · type to filter |
| The plan approval pane | `a` approve & run · `f` clear context & run · `l` Ralph loop · `r` keep refining · `g` close gaps · `c` cancel plan · Esc keeps refining |
| Close gaps: who reviews? (after `g`, while advisors are offered) | `m` the model · `a` an advisor finds the gaps · `y` any (the model, asking an advisor where one fits) · `c` the model, an advisor confirms · Esc back |
| A Ralph loop's checkpoint | `k` keep · `r` redo with a note (typed below) · `v` view the changes · `s` stop the loop · Esc keeps |
| A Ralph loop's by-hand check | `w` it works · `r` redo with a note (typed below) · `v` view the changes (where it opens, so Enter alone ticks nothing) · `s` stop the loop · Esc stops |
| A code audit's done pane | `p` fix via plan (with a finding open) · `g` more passes… · `k` keep the report · `d` discard it · Esc keeps |
| `/find` | type to find · a paste or right-click adds its first line · Enter or F3 older · Shift+Enter or Shift+F3 newer · PgUp/PgDn scroll · Esc, Ctrl+F or a double-click outside done |
| Info panes (`/help`, `/sys`, `/about`, `/tree`…) | ↑/↓, PgUp/PgDn scroll · ←/→ or Tab switch tabs · type to find · Esc closes |
| `/help` › Keys | `k` **⌨ key check**: swaps in `/keycheck`'s list; pressed again, shows the keys |
| **Saved lists** | |
| `/eventlog` | ←/→ logs · Enter opens the event (`c` copy, `a` ask, Esc back to the list) · `r` refresh · `m` more · `c` computer · type to filter |
| `/services` | ←/→ states · Enter opens the service (`c` copy, `a` ask, and on a readwrite computer `s` start, `x` stop, `r` restart, `t` start type, each asking) · `r` refresh · `c` computer · type to filter |
| `/tasks` | ←/→ states · Enter opens the task (`c` copy, `a` ask, `h` history, and as allowed `r` run, `e` end, `n` enable, `d` disable, each asking) · `r` refresh · `c` computer · `f` folder · `w` Windows' own · type to filter |
| `/ad` card | Columns, long ones cut to the pane · a double-click on a row (a member, a result, a group, the manager or owner) opens its card, Esc goes back · `c` copy the whole card · `a` ask · Shift+letter starts a find |
| `/ad` card, on a group or a locked user | on a group, `n` nested or direct, `u` users only or all · on a locked user, **Search 4740** opens the PDC emulator's lockout events on `/eventlog` · a name several accounts share opens as a card of them |
| `/youtube`, `/youtube list` | Enter plays |
| `/youtube <words>`, `/youtube playlist` | Enter plays · `s` saves the video |
| `/prompts` | Enter sends the prompt (queued while a reply runs) · `d` puts it on the draft line · `a` add · `s` save the draft · `e` edit (a long one in the draft editor) · `r` or Delete removes (asks) · `c` clears all (asks) · Shift+↑/↓, Shift+Home/End move · type to filter |
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
| **Servers and processes** | |
| `/server`, `/model` | Enter chooses · type to filter |
| *Embedded models* | `s` size · `i` installed or not · `d` drafter · `o` sort · `x` uncensored |
| `/process` | Enter opens the [window](#the-process-window) · `k` kills (asks) · `f` all, running, ended |
| `/docker` | Enter opens · `r` refresh · `l` LLM servers only · type to filter |
| **Skills** | |
| `/skills` › a skill | Enter on a row: move (asks), rename, edit, revert, lock/unlock, disable/enable, delete (asks) |
| `/skills` › a skill › `revert` | Enter puts the version back · `d` or Delete removes it (asks) · `c` clear all (asks) |
| **Shopping** | |
| `/shopping` | Enter or Space ticks · `i` add · `p` place at an aisle or section · `n` rename · `r` or Delete remove · `a` tick all · `c` clear ticked · `s` stores · `+` / `-` quantity |
| Its add page (`i`) | Enter on the first row types new items · Enter on a past item adds it · `x` or Delete forgets it · type to filter |
| *Shopping stores* (`s`) | Enter shops there (the first row adds one) · `l` layout · `n` rename · `r` or Delete remove (asks) · `f` its folder · `i` reindex |
| A store's layout (`l`) | tabs **Aisles**, **Sections**, **Facts**, **Guesses** · Enter renames, moves or removes · Delete removes · on **Aisles**, Shift+↑/↓ move an aisle along the walk, Shift+Home/End to its start or end |
| **Settings and bars** | |
| `/settings changed` | Enter edits · `r` back to default |
| `/toolbar edit`, `/perfbar edit`, *Status bar* | see [Bar checklists](#bar-checklists) |
| `/homebar edit` | **On the bar**: see [Bar checklists](#bar-checklists); Enter for rename and emoji · other tabs: Space or Enter adds or removes, type to filter |
| A room's pane on the home bar | Space switches a device · Enter its actions |

### The event log pane

`/eventlog` shows a computer's events, newest first.

* **Tabs:** one per log (Application, System, Security, Setup, and the one you named). A tab reads "(no access)" once Windows refused it.
* **A row:** the time, a level icon, the provider, the id and the message's first line; a dim line marks each new day. Icons: ◆ critical, ● error, ▲ warning, ○ information; ✗ and ✓ Security's audit failures and successes.
* **Buttons** on the title row: `⟳ refresh`, `more` (lit while there is a next page) and `computer` (with more than one ticked).
* **An event's page** shows everything recorded, its XML too, as plain text: nothing an event holds is read as markup or a terminal code.
* **On a Mac** the title is `🪵 Event logs · this Mac`, with tabs `system`, `crashes` and `install` (and a `.logarchive` you named), each read when first opened, and no `computer` button. Icons: ◆ fault, ● error or a crash, ▲ a jetsam, ○ default and the other reports, · the rest.

### The services and tasks panes

Windows only.

* **`/services`:** a row per service (name, display name, state, a failure's exit code), a tab per state.
* **`/tasks`:** the scheduled tasks (path, state, last run and result, next run); Windows' own under `\Microsoft\` stay hidden until `w`.
* **Enter** opens one whole, with the actions its computer allows as buttons. Each asks first; a refusal (protected, a driver, privileged, not in `control`) says why.
* Times are the computer's own clock.

### Folder picker

* **Opens from** `/cwd browse` and the *Working directory (cwd)* and *Obsidian vault* rows: a folder tree, with `⌂ profile` (the profile's `files` folder) and `▣ images` above the drives.
* **Keys:** Space, → and ← (or a click on a folder's icon) open and close folders, `-` collapses all, and Enter or a double-click on a name chooses.

### File browser

* **`/browse [<folder>]`** opens a tree of the working directory, folders first. *File browser/tree mode* decides what is listed; `.git` folders and links are left out.
* **Keys:** Space, → and ← open and close folders, `-` collapses all, and Enter or a double-click opens a file in its default app.

## Keyboard shortcuts

Each runs its command as if typed, and keeps your draft. The last column is what it does during a reply.

| Shortcut | Runs | During a reply |
|---|---|---|
| Ctrl+. | `/terminal` | at once |
| Ctrl+/ | `/settings` | over the reply |
| Ctrl+B | `/browser` (the [Browser window](#the-browser-window)) | at once |
| Ctrl+E | `/explore` | at once |
| Ctrl+F | `/find` | waits |
| Ctrl+G | `/log` (the [log window](#the-log-window)) | at once |
| Ctrl+H | `/help` | over the reply |
| Ctrl+J | the emoji picker (not on a Mac) | idle line only |
| Ctrl+L | cancels the background learning (🧠) | at once |
| Ctrl+M | `/model` | waits |
| Ctrl+N | `/rename` | over the reply |
| Ctrl+O | folds or unfolds tool calls, code, diffs and thinking | at once |
| Ctrl+P | `/profile` | waits |
| Ctrl+Q | `/queue` | over the reply |
| Ctrl+R | `/reasoning` | over the reply |
| Ctrl+S | `/server` | waits |
| Ctrl+T | `/tools` | over the reply |
| Ctrl+U | `/usage` | over the reply |
| Ctrl+W | `/webui browser` (the [Web UI](#web-ui) on this PC) | at once |
| Ctrl+Y | `/sys` | over the reply |
| Ctrl+Z | `/theme` | waits |
| Ctrl+Alt+B | `/botchat --resume` | ends a running botchat at once; otherwise waits |
| Ctrl+Alt+C | `/clear` | stops the reply first |
| Ctrl+Alt+D | `/mcp` | over the reply |
| Ctrl+Alt+K | `/botchat --kill` (the extra embedded servers a botchat left) | waits |
| Ctrl+Alt+M | `/memory` | over the reply |
| Ctrl+Alt+N | `/new` | stops the reply first |
| Ctrl+Alt+O | `/tools shell police` | over the reply |
| Ctrl+Alt+P | `/prompts` | over the reply |
| Ctrl+Alt+Q | `/queue clear` | at once |
| Ctrl+Alt+S | `/skills` | over the reply |
| Ctrl+Alt+T | `/tools shell allowed` | over the reply |
| Ctrl+Alt+U | `/comfy view` (the [picture viewer](#the-picture-viewer)) | at once |
| Ctrl+Alt+V | `/camera live` (the [camera window](#the-camera-window)) | at once |
| Ctrl+Alt+W | `/webui` (the Web UI pane) | over the reply |
| Ctrl+Alt+X ×2 | unloads the embedded model | at once |
| Ctrl+Alt+Y | `/sessions` | over the reply |
| Ctrl+Alt+Z | `/splash` | stops the reply first |
| F9 | `/camera snap`, attached to the line | waits |
| F10 | `/screen`, attached to the line | waits |

* **In a pane** a shortcut closes the pane and runs. Ctrl+., Ctrl+E, Ctrl+W, Ctrl+L, the window chords and Ctrl+Alt+X act and leave it open; Ctrl+F in a find closes it. A pane that asks you something ignores them all.
* **Window chords** (Ctrl+G, Ctrl+B, Ctrl+Alt+U, Ctrl+Alt+V) close their window when pressed again; the typed command only opens it. Ctrl+B leaves the Browser window open while a bot check waits in it.
* **Bars** have no shortcut: `/perfbar`, `/toolbar`, `/homebar` and `/header` show or hide them.
* **Ctrl+Alt+X**, twice within two seconds, unloads the embedded model and frees its memory, canceling any reply, load or botchat using it. With any other server it does nothing.
* After an unload no server is connected until you pick one; *LLM URL* is kept.
* **F9 and F10** work with no modifier held; the picture viewer and thumbnail browser keep their own meanings for them.
* **On a Mac**, Ctrl+] stands in for Ctrl+., Ctrl+D for Ctrl+M and Ctrl+Option+A for Ctrl+Alt+M (the originals work in the app's windows). Ctrl+Option chords need Option as Meta ([What differs](MAC.md#what-differs)).
* **A chord that does nothing** may be another program's global hotkey. `/keycheck`, or `k` on `/help`'s Keys tab, lists what holds each one.

## Commands typed during a reply

* **Open their pane over the reply:** `/help`, `/settings`, `/tools`, `/comfy`, `/mcp`, `/sys`, `/usage`, `/about`, `/memory`, `/queue`, `/sessions`, `/sessions title`, `/rename`, `/skills`, `/reasoning`, `/sampling`, `/msgclear`, `/tree`, `/vault`, `/browse`, `/docker`, `/eventlog`, `/net`, `/services`, `/tasks`, `/ad`, `/youtube`, `/shopping`, `/prompts`, `/browser list`, `/camera list`, `/ha states`, `/keycheck`, `/process`, `/sys persona`, `/sys operata`, `/sys vocalia`, `/toolbar edit`, `/perfbar edit`, `/homebar edit`, a home bar chip's pane, `/webui`
* **Run at once:** `/mark`, `/webui copy`, `/webui browser`, `/ha`, `/camera live`, `/camera watch`, `/camera off`, `/camera use`, `/camera view`, `/camera thumbs`, `/screen view`, `/screen thumbs`, `/tts`, `/stt`, `/stt wake`, `/stt interrupt`, `/perfbar`, `/homebar`, `/homebar on`, `/homebar off`, `/toolbar`, `/header`, `/reasoning <level>`, `/sampling <field> <value>`, `/queue clear`, `/queue mode <word>`, `/queue cancel <word>`, `/copy`, `/remember`, `/explore`, `/open`, `/browser <url>`, `/prompts <n>`, `/terminal`, `/shortcut`, `/log`, `/process <id>`, `/timer`, `/expand`, `/collapse`, `/window`, `/cwd`, `/comfy view`, `/comfy thumbs`, `/splash view`, `/splash thumbs`, `/pasted view`, `/pasted thumbs`, `/view <path>`, `/view <path> --thumbs`
* **Stop the reply first:** `/clear`, `/new`, `/splash`, `/rewind`, `/exit`
* **Everything else:** waits for the reply, queued behind earlier messages (*Queue cancel mode* applies).

In a pane over a reply, rows that would disturb it (a reconnect, the profile, the working directory, the theme, a session, a skill's page, an MCP server) answer *(not while a reply runs)*.

## The hints bar

The keys that apply right now, on a row of their own over the status bar (*Hints bar*). It shows one hint at a time, the first of:

1. The kill switch's warning (Ctrl+Alt+X, press again to unload).
2. A waiting second press: Ctrl+C to exit, Esc to rewind, Delete for a splash picture.
3. An open pane's keys.
4. What the arrows do right now: the splash's pictures, the picture strip, `/read`'s keys.

* **auto** (the default): the row comes with a hint and stays, blank, until you send, a reply ends, a pane opens or closes, the window is resized, or 10 idle seconds pass. Typing never moves the input line.
* **always** keeps the row; **off** puts the hints at the end of the status bar's left group.
* During a reply it shows only a pane's keys or the kill switch's warning; **Esc to stop** stays on the status bar.
* A double-click on it opens `/settings`. It moves with the other bars in *Toolbar order*.

## The status bar

The row under the hints bar (*Status bar*). [Bar checklists](#bar-checklists) moves and pins its sections:

* **Left by default:** **Status** (🔊 🎤 👂 ✋, 🧠, 🏷️…), **Spinner** (during a reply, with its clock and **Esc to stop**; always on), **Queued**, **Timers** (⏰ countdowns, ⏱️ stopwatches), **Reading** (`/read`, `/echo`) and **Usage** (context and tokens).
* **Right by default:** **Model** (name and reasoning icon) and **Working directory** (off by default). During a reply the busy row keeps the same sides.
* **Narrow window:** the model's name and the path shrink, then go; then the right group gives way, then the left. The spinner goes last.
* **Background setups** (an embedded model's download, MCP servers connecting, the voice models' first load) show 📥, 🔌, 🎙️ or 🔈 while the input line stays yours.
* **A Ralph loop** leads the Status section with 🔁, its step and its pass (`🔁 step 2/5 · pass 3`); the hints bar says Esc stops it and typing pauses it.
* **A code audit** leads it with 🔎 and its pass (`🔎 2/5`, or `🔎 2` with no pass limit); plan mode's 📝 leads it while planning.
* **When an audit ends on its own**, its open findings print as a short table (most severe first, at most 40) and its done pane opens on **Keep report** ([Pane keys](#pane-keys), [Code audit](COMMANDS.md#code-audit)).

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

Icons under the status bar (*Tool bar*), each shown while it is checked there. A click says what an icon is; a double-click or a right-click opens it. "Dark" below means on a dark background, as a tool group that is off shows.

| Toolbar item | Shown | Opens |
|---|---|---|
| ⚙️ 🪪 🧮 🔌 🎭 💬 📊 | always | `/settings`, `/profile`, `/theme`, `/mcp`, `/sys`, `/sessions`, `/usage` |
| 🛠️ | dark while no tool is offered (*LLM offer tools* off, or every switch and tool off on `/tools`' Offered tab) | `/tools` |
| 🎓 | dark while *Agent skills* is off | `/skills` |
| 📱 | dark while *Web UI* is off | `/webui` |
| 💾 | dark while *Memory mode* is `off` | `/memory` |
| 📣 | always | `/prompts` |
| ⏳ | dark while *Queue mode* is `off` | `/queue` |
| 🙋 | dark while *Ask user* is off | `/tools ask` |
| 🔒 / 🔓 | *Shell command policy* `ask` / `yolo` (none under `off`) | `/tools shell allowed` |
| 👮 / 🥷 | *Shell police* on / off (none while the policy is `off`) | `/tools shell police` |
| 🐚 | dark under `off` | `/tools shell` |
| 📁 🌄 🌐 🦉 🐳 💎 🛢️ 🔮 🐬 🪶 🐘 🔗 🏠 🎨 📸 🖨️ | dark while off | `/tools files`, `image`, `web`, `advisors`, `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `sqlite`, `postgres`, `unc`, `ha`, `comfy`, `camera`, `print` |
| 🪵 | dark while off, and always where there are no event logs to read | `/tools eventlog` |
| 📡 | dark while off | `/tools network` |
| 🧩 📅 | dark while off, and always off Windows | `/tools services`, `tasks` |
| 🏢 | dark while off, and always on a system other than Windows or macOS | `/tools directory` |
| 📄 ⚡ | always | the [log window](#the-log-window); `/process` |
| 🎞️ 🪟 | always | the [picture viewer](#the-picture-viewer); the [thumbnail browser](#the-thumbnail-browser) (ComfyUI's folder) |
| 📺 ⭐ 🛒 | always | `/youtube list`, `/browser list`, `/shopping` |
| the working directory (`~` for your user folder) | pinned right | `/cwd browse` |
| **Model name** and its icon | pinned right, with a server connected | `/server`; the icon `/reasoning` |
| blank space | — | `/settings` |

* **Dividers** (`│`) show only between two visible items ([Bar checklists](#bar-checklists)).
* **Narrow window:** the icons come first; the model name and the path shrink, then go; then items drop from the right group, then the left.

## The performance bar

Meters updated once a second (*Performance bar*): **CPU**, **RAM**, **GPU**, **VRAM** (**GMEM** on a Mac), **NET**, **NET↓**, **NET↑** and **PROC**. `/perfbar text`, `gauge` or `led` picks the look, as does `l` on its checklist; `/perfbar` hides it.

* Values turn amber from 60 % and red from 85 %. A meter the machine can't read is left out.
* NET is the share of the link speed; NET↓ and NET↑ are bits per second. They follow the busiest adapter with a gateway.
* PROC counts the model's background processes still running.
* **On a Mac** RAM is Activity Monitor's *Memory Used*, and GMEM is the GPU's share of what macOS lets it use.
* Meters are pinned left, center or right, center by default. On a narrow row they shrink, then show numbers only, then the side groups give way; the center wins.
* A double-click or a right-click on the bar opens `/settings`.

## The home bar

Chips for Home Assistant rooms and devices and the shopping list (*Home bar*); see [TOOLS.md](TOOLS.md#home-assistant). `/homebar` hides or shows it.

* **Chips:** a light wears its own color, a room counts its lights on (`2/5`), sensors show values, thermostats current → target, players ▸ or ‖. Off is dim.
* **Alerts:** an open door, a leak, a jammed lock or a low battery turns amber or red. Every chip goes dim while Home Assistant is unreachable.
* **🛒 n** counts the shopping lines still to get; it needs no Home Assistant and opens `/shopping`.
* **Clicks:** a click shows a chip's state. A double-click acts: it toggles a light, switch or fan, turns a room's lights off (or on), runs a scene, plays or pauses, or moves a cover.
* **A right-click** opens the chip's pane of actions (a room's: its devices). A chip shows `…` until a change is reported, `!` after six seconds without.
* **Asks first:** unlocking, opening a garage, gate or door, and disarming an alarm. An alarm that wants a code is disarmed in Home Assistant itself.
* **Live:** while shown, the bar follows its devices as they change. A refused key isn't retried until the URL or key changes.
* **Narrow window:** names shrink, then the side groups give way, then the center pages behind ‹ ›. A double-click or a right-click on blank space opens `/settings`.

**The picker** (Enter on *Home bar*, or `/homebar edit`):

* **On the bar:** the chips under **Pinned left**, **Pinned center** and **Pinned right** ([Bar checklists](#bar-checklists)). Enter on one also offers:
  * **Rename on the bar…**: an alias the chip shows ("PC"; blank shows the emoji alone). **Default name** puts Home Assistant's back; panes keep the real name.
  * **Change emoji…**: the [emoji grid](#the-avatar-picker); **Default emoji** or 🚫 puts the chip's own back. A chosen emoji stays fixed, whatever the state. `/homebar emoji <item> = <emoji>` does the same.
  * **+ Add divider** (the last row): a dim `│`, shown only between two chips of its group.
* **The other tabs:** **Rooms** (by floor), **Lights**, **Switches** (and fans), **Sensors**, **Climate**, **Media**, **Scenes** (and scripts), **Security** (locks, covers, alarms), **Zones** (people in each), **Other** and **Shopping**.
* There, Space or Enter adds or removes, and typing filters by name, id, room or label. Without Home Assistant only **On the bar** and **Shopping** show.

## Bar checklists

The *Status bar*, *Tool bar* (`/toolbar edit`) and *Performance bar* (`/perfbar edit`) checklists, and the home bar's **On the bar**, share these keys:

* Space or Enter checks or unchecks; Delete unchecks (on the home bar, takes off). None checked hides the toolbar or the performance bar.
* `a`, `n` and `d` pick all, none or the default; `d` also puts back the default order, sides and dividers.
* Shift+←/→ (or Alt+←/→) pins the item to the other side; on the performance and home bars, to the next group over (left ↔ center ↔ right).
* Shift+↑/↓ (or Alt+↑/↓) moves it a place, crossing into the next group at the edge; Shift+Home/End moves it to its group's top or bottom.
* **Performance bar:** **look:** on the title row (`l`) steps the look: text, gauge, led.
* **Status bar:** the Spinner can't be unchecked, only moved. The row reads `default`, `all`, `none`, or the left group's sections, `|`, then the right group's.
* **Tool bar dividers:** `+` (*+ divider* on the title row) puts a dim `│` after the item under the cursor. It moves like an item and Delete takes it out.
* The toolbar's default has three dividers: `⚙️ 🛠️ 🎓 │ 💾 📣 ⏳ │ 🔒 👮 🐚 │ 📁 🌄 🌐 📱`.

*Toolbar order* orders the five bars: Shift+↑/↓ (or Alt+↑/↓) moves the bar under the cursor, Shift+Home/End to the top or bottom, and `d` puts the default back. A bar that is off keeps its place.

## The avatar picker

* Enter on *Model avatar* (or `/avatar`) opens a grid of every emoji your terminal draws (no flags). *User avatar* opens it for your own lines.
* **Tabs:** **⭐ For you** (matching the profile's name and persona), **🕘 Recent**, then the Unicode groups. Tab and Shift+Tab step them; the arrows move; a double-click picks.
* Type to search (`fox`, `red heart`) or paste an emoji; Esc clears. 🎲 picks at random; 🚫 clears the avatar.
* Enter on a hand or a person opens its six skin tones. Other profiles' avatars are marked.
* Recent picks and the last skin tone are kept per profile, in `emoji.json` in the profile's `configs`.

## The welcome splash

* *Welcome splash* `image`: one picture; ←/→ step through them. Delete twice on an empty line moves one of the profile's pictures to the folder's `.trash`.
* *Welcome splash* `robot`: a robot stands just above the draft line and follows the mouse and your typing. Your first line takes it away.
* **The robot** shows just its head when the room above the line is short, all of it again when there's room, nothing when there's none. Following the mouse needs a terminal that reports it.
* **Your own pictures** in the profile's `images\splash\` replace the built-in ones; `/splash view` or `/splash thumbs` opens the folder, `/splash purge` empties it.
* `/splash` shows the splash whatever *Welcome splash* says (the picture when it's `off`). On the Web UI the picture leads the transcript and goes at the first message sent from either side; the robot isn't shown there.

## The app's windows

What every window shares:

* **Focus:** opened by its key chord, a window stays behind the terminal; opened by a typed command, it comes to the front. However it closes (Esc, Alt+F4, ×, its chord), the terminal gets the keyboard back.
* **Keys:** Tab brings the terminal forward, and a Ctrl or Alt chord the window doesn't use runs in the chat. The Browser window keeps every key for its pages; on Windows the video window keeps Tab.
* **Place and look:** a window reopens where it last closed, closes with the app, and wears the theme unless *Themed external windows* is off.
* **On a Mac:** the windows' shared keys (⌘W, ⌃⌘F) and why there are none over SSH are in [What differs](MAC.md#what-differs).

### The picture viewer

* **Opened by** a double-click on a picture in the transcript (*Image viewer* can name another program), `/view <image or folder>` (which follows new pictures), and `/comfy view`, `/camera view`, `/screen view`, `/splash view` or `/pasted view`.
* Where no window can open, `/view` draws in the transcript; a PDF goes to [the Browser window](#the-browser-window).

| Key | Action |
|---|---|
| ← / →, the wheel, **<** / **>** | Newer / older (newest at the left; reaching it follows new pictures) |
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
* **The file:** copy its path, show it in Explorer (Finder on a Mac), attach it, print it, or delete it without asking (to the Recycle bin, the Trash on a Mac, under *File deletes to Recycle bin*, else for good).

### The thumbnail browser

`/view <folder> --thumbs`, `/comfy thumbs` (🪟), `/camera thumbs`, `/screen thumbs`, `/splash thumbs` and `/pasted thumbs` show a folder's pictures, oldest first, new ones added at the end.

| Key | Action |
|---|---|
| Click / double-click or Enter | Select (the viewer follows) / open in the viewer |
| Arrows, PgUp / PgDn, Home / End, the wheel | Move, scroll |
| + / − or Ctrl+wheel | Bigger / smaller thumbnails |
| F5 | Re-read the folder and fit the thumbnails |
| Del twice within 2 s | Deletes the picture: to the Recycle bin (the Trash on a Mac) under *File deletes to Recycle bin*, else for good |
| F11 or double-click between thumbnails | Full screen |
| Esc | Closes the menu, then leaves full screen, then closes |

* A right-click opens the picture menu, with *Open in the viewer* first.
* **On a Mac** ⌫ is Del; ⌘= and ⌘− or a pinch change the size.

### The log window

`/log` (Ctrl+G, 📄) shows this run's diagnostic log from the start, with or without `--log`: the newest 20,000 lines, colored by level. At the bottom it follows new lines; scrolled up it holds still (*paused*). Drag to select, Shift+click to extend.

| Key | Action |
|---|---|
| ↑ / ↓, PgUp / PgDn, the wheel | Scroll; at the bottom it follows again |
| Ctrl+Home / Ctrl+End or Ctrl+E | First line / last line, following |
| Ctrl+A / Ctrl+C | Select all / copy |
| F11 or double-click | Full screen |
| Esc | Leaves full screen, then closes |

* **On a Mac** ⌘A and ⌘C select and copy, ⌘↑ and ⌘↓ go to the top and bottom.

### The process window

* `/process <id>` (or Enter on `/process`'s list, ⚡) shows a background process's last 5,000 lines live, with the log window's keys, stderr in the warning color.
* The title shows the id, command and state (`running`, `exited 0`, `stopped by you`).
* **Ctrl+K** twice within 3 seconds stops it and everything it started, and the model hears of it.

### The camera window

`/camera live` (Ctrl+Alt+V) shows the camera live until you close the window or type `/camera off` (macOS 14+ on a Mac).

* It never takes the keyboard. F11 or a double-click is full screen; Esc leaves full screen, then closes it.
* *Camera preview* `live` shows the camera pane's shot here as you frame it.
* The camera is one shared stream, so live view, the pane, watch mode and `/botchat` can use it together. Ctrl+Alt+V closes only a window `/camera live` opened.
* **An unplugged camera freezes `/camera live` silently**: the window stays on its last frame, with nothing in the transcript, until `/camera off`.

### The video window

Where YouTube videos play under *YouTube open links in* `youtube-window`, the default ([TOOLS.md](TOOLS.md#youtube)).

* A video or playlist link clicked in it (the YouTube logo, an end screen) plays where the setting says; anything else opens in your default browser.
* It doesn't take the keyboard. F11 is full screen; Esc leaves it, then closes the window. YouTube's own keys (Space or K, ←/→, F, M) work once you click into the video.
* **On a Mac** it needs macOS 14 or later (WebKit). The player's own full-screen button fills a Space of its own; Esc brings it back.

### The Browser window

A browser window of the app's own (Windows; macOS 14+ on a Mac, where it is WebKit: [The Browser window on a Mac](MAC.md#the-browser-window-on-a-mac)). When you close it, its cookies go to the web tools.

* **What opens it:** the web tools, for a bot check ([TOOLS.md](TOOLS.md#the-browser-window)), which a Web UI page can also see and drive ([A bot check from the page](#a-bot-check-from-the-page)).
* **`/browser [url]`** opens it for you; a bare `/browser`, on *Web browser home page*.
* **The bar:** back, forward, reload (✕ stops a load), home, a padlock (https) or a warning (http), the address box, Go, and open in your default browser.
* **The address box:** words that aren't an address search with *Web search method*; `file:`, `javascript:` and local paths are refused.
* **Keys:** Ctrl+W closes; F5 or Ctrl+R reloads; Alt+← / Alt+→ go back and forward; Ctrl+L, Alt+D or F6 go to the address box (Esc there restores it).
* **More keys:** Ctrl+H the history; Ctrl+D saves or unsaves the page; Ctrl+Shift+O the saved links; Esc stops a load, and never closes the window.
* **The window's keys win:** inside it, Ctrl+W and Ctrl+H are its own keys, not the terminal's shortcuts. Ctrl+B in the terminal opens and closes it (under tmux, whose prefix is Ctrl+B, it never reaches the app).
* **On a Mac**, Safari's keys (its other differences: [The Browser window on a Mac](MAC.md#the-browser-window-on-a-mac)):
  * ⌘W closes, ⌘R reloads, ⌘[ / ⌘] (or a mouse's side buttons) go back and forward, ⌘L the address box, ⌘⇧H home.
  * ⌘Y the history, ⌘D saves, ⌘⇧O or ⌘⌥B the saved links, ⌘. or Esc stops.
  * ⌘F finds (⌘G, ⌘⇧G), ⌘+ ⌘- ⌘0 zoom, ⌘P prints; ⌘C, ⌘V and the other editing keys work in the address box and the page.
  * Full screen is ⌃⌘F; a page's own full screen fills a Space of its own, as in Safari.
* **Full screen:** F11, with the bar kept. A page's own full screen (a video's button) takes the window and hides the bar. Esc leaves the page's first, then F11's.
* **History** (🕘, Ctrl+H): newest first, older by day, with **Clear history**. *Web browser privacy* `clear-history` clears it at each close; `keep-history` keeps it in `<profile>\configs\browser-history.json`. Cookies and sign-ins stay either way.
* **Saved links** (☆, ★ on a saved page): up to 190, the first 15 on the menu, the rest in submenus. `/browser list` (⭐) has them all; they're kept in `<profile>\configs\browser-links.json`.
* An address holding a user name or password is never saved. The model reaches the links with `web_links` and `web_save_link` ([TOOLS.md](TOOLS.md#web)).
* **Per profile:** sign-ins and solved checks live in `<profile>\webview2-browser` (a Mac's: [MAC.md](MAC.md#the-browser-window-on-a-mac)). Switching profiles closes the window. Two copies of the app can each open it, on different profiles.
* **Clearing:** `/browser clear history` clears the history now. `/browser clear all` asks, then clears the history, cookies, sign-ins, cache and site settings; saved links stay.
* **A PDF:** `/view report.pdf` shows a PDF from the working directory in the window's PDF viewer, from a copy deleted when the window closes. The open button opens the file itself in its own program.
* A PDF view isn't kept in the history, can't be saved as a link, and hands no cookies over. A policy that makes Edge download PDFs makes the window download it.
* Where the window can't take it (a bot check waiting, no WebView2 runtime, a Mac before macOS 14 or over SSH), the PDF opens in its own program (Preview on a Mac).

## Web UI

A browser front end for the loaded profile, for your phone or another computer: the chat as it happens, the questions the model's tools ask, the house, the shopping list and a video player.

Turn it on with `/webui on` or *Web UI* (`/settings` › Client › Web UI). Not headless.

### Signing in

* **The pane:** `/webui` (Ctrl+Alt+W, or the toolbar's 📱) shows a tab per address of this machine, each with a QR code and its sign-in link. Scan the code with the phone's camera, or open the link in a browser on the same network.
* **The addresses:** Tailscale's comes first, since its link works at home and away ([Away from home](#away-from-home)). Then the home network's Ethernet and Wi-Fi adapters, fastest first (`localhost` under `local`).
* Other VPN tunnels and WSL's virtual switch aren't listed, though a browser through them still connects with the address typed.
* **The link** signs the browser in and leaves the address bar at once; the browser keeps a cookie for a year.
* **The token:** each profile has its own, kept encrypted. `/webui reset`, or a new port, makes a new one and signs every browser out.
* A link sent through a messaging service hands that service the token too: run `/webui reset` afterwards.
* **Without the pane** (a redirected console), `/webui` prints the links as notices, so the token reaches the scrollback.

#### The pane's buttons

* **The strip:** `📱 Web UI │ enabled │ lan │ reset │ 192.168.1.217 · 100.78.20.125`, three buttons ahead of the address tabs.
* `e` turns *Web UI* on or off, `l` switches *Web UI reach* between `lan` and `local`, and `r` makes a new token after a yes, as `/webui reset` does. The server restarts at once.
* **copy** (`c`) puts the shown tab's sign-in link on the clipboard; `/webui copy` puts the first tab's.
* **A double-click on the link or the QR code** opens it on this PC, as `/webui browser` does.
* **Over a reply** the pane opens too, its buttons acting at once; a phone watching that reply drops for a moment and reconnects.

#### On this PC: `/webui browser`

* `/webui browser` (or Ctrl+W) opens the Web UI signed in: in the Browser window under *Web open links in* `browser-window`, else in your default browser (with `--external`, always).
* **The link:** the default browser signs in as *Web browser sign in mode* says. The Browser window gets a one-time link while *Web browser privacy* is `keep-history`, else the usual one.
* A one-time link is good once, for a minute.
* The transcript and the log say where it opened, never the link. The Browser window keeps no sign-in link in its history or saved links, never shows one to the model, and never hands the Web UI's cookie to the web tools.

#### Away from home

* Install Tailscale on this PC and the phone, sign both in to the same tailnet (your own private Tailscale network), and scan the Tailscale tab's code once. The phone then reaches the Web UI wherever it is.
* Nothing to open on the router; *Web UI reach* must be `lan`. If Windows calls Tailscale's network Public, the phone can't connect ([the firewall](#safety-and-the-network)).

### The tabs

* **The tabs:** Chat, Home, Shopping and YouTube, each shown only while its setting is on: *Web UI chat controls* (on by default), *Web UI home controls*, *Web UI shopping controls* and *Web UI YouTube controls* (off).
* **The ☰ menu:** Chat is a link in the header; the other tabs sit behind ☰, which names the open one (🏠 Home ▾) and lists only the tabs that are on.
* **With Chat off** the page opens on Home, else Shopping, else YouTube. With none on, it names the four settings and opens a tab once one is turned on; a tab turned off while open moves to one that's on.
* **Chat off** also means no transcript, message line or questions on the phone; they open on the PC as always.

### The Chat tab

#### Messages

* **Your own draft:** the page and the PC never see each other's drafts. Sent lines share the profile's message history, so ↑/↓ on either side bring them back; a draft set aside with Esc at the PC never reaches the page.
* **Sending:** Enter sends on a desktop; on a phone it adds a line (tap ➤). ✕ clears the box and *Undo* puts it back. Stop (■) shows while a reply runs.
* **The dropdown** completes the `@ # $ % ^ *` mentions and, with *Web UI commands* on, the commands the page may run. `@` lists files only while *File tools* is on.
* *Web UI connection details* adds a `%` or `*` connection's server and database to its name.
* **Queued:** a message goes from the idle line after anything queued at the PC. During a reply it waits (counted in Queued, not in `/queue`) and goes when the reply ends, even under *Queue mode* `manual`.
* **After a cancel** under *Queue cancel mode* `hold`, the waiting messages are held under a **Resume** banner; a new message runs first.
* **Dropped** by `/clear`, `/new`, a resume, a rewind, a profile switch or *Queue cancel mode* `empty`; their text goes back to the page. `/queue clear` leaves them. At most 20 wait, up to 48 KB each.
* **Stop** ends a reply, a compact, a botchat, an advisor, `/imagine`, `/test` or a `/loop`, and stops speech. Before the reply's first word it takes the message back.
* Stop never stops a model loading (Ctrl+C at the PC) or your editor under `/draft`.

#### What the page shows

* **The transcript** as the terminal shows it: your lines, the replies (Markdown), pictures, tool lines, notices, thinking (*LLM show thinking*), diffs and folded tool runs. Not shown: the panes you open at the PC.
* **Folds** follow *Diff max lines*, *Diff collapse count* and *Tool collapse count*; a tap opens one. ↕ opens or closes every fold, as Ctrl+O does; ⤒ and ⤓ go to the top and bottom.
* It starts afresh when the screen does (`/clear`, a rewind, a resume, a theme change, a profile switch).
* **The status row** over the message box shows the PC's spinner and its clock while it works, and the usage at the right (`~` while estimated under *LLM mid-turn usage*).
* A tap on the usage opens the `/usage` figures in a card (`/usage` typed on the page stays refused).
* **🧠** leads the row while a reflection runs; tap it twice to cancel it, as Ctrl+L does.

#### Prompts, history and memory

**📣**, at the message box's left, opens a sheet with three tabs (a bare `/prompts` from the page opens it too):

* **📣 Prompts:** a tap sends a saved prompt as if typed; ✎ puts it on the message line; ⇪ saves the message line as a new one.
* **🕘 History:** a tap puts an earlier line on the message line. *Clear all* empties the history, stored and at the PC, as `/msgclear` does.
* **💾 Memory:** ⇪ saves the message line as a memory, as `/remember` does (refused with *Memory mode* off). A tap on a memory edits it or turns it off or on.
* A 🗑️ and *Clear all* take two taps. The PC's hint row flashes what the page saved, edited or deleted.

#### Pictures

* **Every picture the chat holds** shows as a thumbnail, even with *Show image thumbnails* off. The model's own `camera_capture` and `screen_capture` pictures show nowhere, as at the PC.
* **A tap** opens it full screen: swipe or ‹ › to step, and *Open original* opens the file itself. On the newest picture it's live, replaced as new ones arrive.
* **It follows the PC:** a picture you move to at the PC (on its strip, viewer or thumbnail browser) moves an open full view to the same file.
* **🎞️** (while *ComfyUI picture strip* is on and there are pictures) opens a drawer of the strip, the PC's pick outlined. A tap there moves the PC's strip, viewer and thumbnail browser to it too.
* **🗑️** (two taps) deletes a picture as the viewer's Del does: to the Recycle bin (the Trash on a Mac) under *File deletes to Recycle bin*, else for good. A pasted picture or a camera watch frame has none.
* A picture is served only while the page's transcript holds it: after `/clear` or a rewind its address answers nothing.
* **On a Mac**, where the app makes no thumbnails, the page shows the original pictures scaled down.

#### Questions

* **On both sides at once:** a command's approval, `ask_user`, an allow (camera, screen, a database write), a yes/no, the plan's approval, a code audit's done pane and a reflection's skill. The first answer wins.
* The page shows a command or a statement whole, and a reflection's skill as its summary with **Keep** and **Discard**.
* **The camera's shutter** (*Camera shutter* `user`) offers **Take and send**, which takes the photo with the PC's camera and no preview.
* **At the PC only:** the plan's Close gaps and a code audit's reviewer page; meanwhile the page says "waiting for an answer at the PC".
* **A bot check:** the allow question, then a card ([A bot check from the page](#a-bot-check-from-the-page)).
* **The Chat badge:** on the other tabs a dot shows while a reply runs and a count while a question waits; so does the tab's title (`(n)`). An Android phone also vibrates once on a new question.

#### The PC sees the phone

* Each change made on the page (a shopping line, a tile, a scene, the video remote, a saved video, a prompt…) flashes on the PC's hint row for a moment (📱 Kitchen light: toggle).
* Nothing the page does goes in the PC's transcript.

### The Shopping tab

* **The list** in the active store's aisle order, as the model reads it; a tap ticks or unticks a line. With two or more stores, a *Store* picker switches the active one, as `/shopping store <name>` does.
* **Adding:** the *Add items* box takes what `/shopping add` does (`2 milk, eggs`), at most 100 at once. 🕘 lists the past items, to add again or forget.
* **A line's sheet** (its ›, or a long-press): − and + change the quantity, rename (a name another line has merges the two), 🗑️ removes.
* **The whole list:** ☑ all ticks every line (☐ none unticks them), and ⊠ clear ticked takes the ticked lines off as bought. Removes and clears take two taps.
* **At the PC only:** placing a line, adding, renaming or removing stores, the layout editor, a store's folder and reindex, and clearing the whole list.
* **The PC follows:** its open `/shopping` pane shows the page's changes within a second.

### The Home tab

**Home** is the house and the PC at a glance, shown only with *Web UI home controls* on (off by default).

* **The tiles:** the home bar's items in its order, with your names and emoji, live and colored as on the bar (the 🛒 stays on Shopping). A tap does what a double-click on the chip does; a long-press or › opens its sheet of actions.
* **Rooms:** a box per room (as `/homebar show <room>`), each Hue zone, and *No area* for scenes in no room. The home bar's rooms come first and open; the others are folded.
* **A box:** its lights on (`3 of 5 on`) and its switch (off if any light is on, else on), then *All lights*, a row per device with its switch or first action, and its scenes.
* **Reach:** only the home bar's devices and the rooms' can be read or acted on. Unlocking, opening a garage, gate or door, and disarming ask on the page first.
* **Ask the house:** a line for Home Assistant's Assist (as `/ha say`), its answer under it.
* **Now playing** (while a video is open, in the PC's window or on the YouTube tab): its title and a remote.
* **The PC:** CPU, RAM, GPU, VRAM and the network's rates, and the model's name, server, context window and usage.
* **The log:** each action is logged with the phone's address; what was asked of the house is not. `/ha` typed in Chat follows *Web UI commands*, not this tab's setting.

### The YouTube tab

**YouTube** plays videos on the phone (or a browser) in the app's own player, so `youtube_control`, `youtube_status`, `/youtube pause` and Home's Now playing reach it.

* **Where plays go:** the model's plays go to the tab while *YouTube open links in* is `webui-window`.
* **The fallback:** with *Web UI YouTube controls* off (the default), or while the Web UI isn't serving, `webui-window` plays in the PC's video window. There is no tab where the app has no video window (a Mac before macOS 14).
* **With it on**, the tab shows whatever *YouTube open links in* says: a video you start there plays there, while `youtube_play`, `/youtube` and `open_url` follow the setting.
* **The jump:** a play with no YouTube tab in view waits for one, and the open pages go there. While you're typing or answering, a *▶ A video is waiting — Watch* banner shows instead.
* A video nobody opens a tab for goes after 10 minutes.
* **Which tab plays:** the one shown most recently; a playing tab keeps playing, even hidden. The others offer *Play here*, which takes the video over from about 3 seconds back.
* **Sound:** a phone starts a video only after a tap on the video itself. The tab tries with sound, then muted, else asks for the tap.
* **Resume:** leaving the tab, locking the phone or a reload stops the video and keeps its place; coming back offers *Resume "title" at 3:12*.
* **Saved videos** (`/youtube list`'s) sit under the player: a tap plays one here, whatever *YouTube open links in* says; ⋯ moves or removes it; ☆ saves the playing video.
* **⛶ Fill window** fills the browser's window with the video under a slim bar; YouTube's own button is still full screen. ✕ Exit, Esc or Back leave (after a tap on the video, Esc is YouTube's).
* **Limits:** on an iPhone *YouTube while speaking* `duck` does nothing (`pause` works).
* A `/youtube` command typed in Chat is refused from the page, except bare `/youtube` and `/youtube list`, which point to this tab's saved videos; ask the model to play instead.
* **Trust:** the tab runs YouTube's player script in the Web UI's own page, so while it's open that script can do what the page can, though it can never read the sign-in cookie.
* The page lets in only YouTube's player script, its frame, and its thumbnails (`i.ytimg.com`).

### Commands from the page

With *Web UI commands* on (off by default), the page sends slash commands too. They run at the idle line as if typed at the PC, after anything queued there; under a `/botchat` they wait for its end. Off, every command is refused, naming the setting.

* **A command's questions** show at the PC and on the page at once; the first answer wins.

| Kind | Commands | On the page |
|---|---|---|
| Run | `/new`, `/clear`, `/splash`, `/compact`, `/plan …` (`/plan ralph` too), `/mark <color>`, `/code-audit …`, `/claude`, `/advisor`, `/help <words>`, `/imagine`, `/loop …`, `/learn [<note>]`, `/test …`, `/pdf …`, `/print <file\|reply>`, `/avatar <emoji>\|clear`, `/remember`, `/timer …`, `/rename <name>`, `/tts on\|off`, `/stt on\|off`, `/memory <mode>`, `/cwd`, `/model <id>`, `/reasoning <level>`, `/ha …`, `/docker …`, `/shopping …`, `/prompts list\|save\|remove\|<n>`, `/browser save\|unsave`, `/msgclear` | Their lines; the status row shows its spinner while it runs |
| Pick | `/model`, `/reasoning`, `/rewind [<n>]`, `/plan open [--chat]` | A card with the choices, the one in use marked, a filter for a long list |
| List | `/ha states`, `/docker logs`, `/eventlog …`, `/net …`, `/services …`, `/tasks …`, `/ad …`, `/code-audit open`, `/print`, `/print printers`, `/camera list`, `/screen list`, `/tree` (needs *File tools*), `/vault` (needs *Obsidian tools*), `/process`, `/comfy status`, `/comfy offered` | A card with the lines; Close |
| Never | `/exit`, `/webui`, `/cmd`, `/profile`, `/plan edit`, `/cwd <path>`, `/sessions`, `/skills`, `/youtube` (bare and `list` point at the YouTube tab), `/queue`, `/botchat`, `/sampling`, `/server`, `/usage` | Refused: they run at the PC only |
| Page | bare `/prompts` | Opens the 📣 sheet on its Prompts tab (📣 does too, even with *Web UI commands* off) |
| Other | Every form that opens a pane or a window at the PC (`/settings`, `/theme`, `/view`, `/prompts list --clear`, `/camera`, `/screen`…) | Refused, naming the form that works here when there is one |

* **A Ralph loop:** its confirm, its checkpoint and its by-hand check ask on both sides; the status row shows 🔁, and Stop ends the loop.
* **`/loop`:** runs as at the PC (a looped `/read` is refused). Bare `/shopping` points at the Shopping tab, and bare `/youtube` and `/youtube list` at the YouTube tab's saved videos (or at the tab's setting while it's off).
* **What differs:** `/ha` and `/docker` ask before a change (at the PC they don't), though `/ha` runs a read or a *Home Assistant safe services* call unasked. A bare `/docker` lists the containers.
* `/msgclear` asks on both sides at once. `/rewind` puts the text on the page's draft; its pictures, PDFs and pasted blocks come back on the PC's line only.
* **Refused when it runs:** `/model <id>` and `/reasoning`, when connecting would mean looking for a server or naming an embedded model not installed; those pickers and that download are the PC's. `/tts on` and `/stt on` likewise, when their voice models aren't downloaded yet.
* **Stop:** the page's Stop ends `/claude` too, but not a model loading (Ctrl+C at the PC).
* **An unknown command:** the page offers *Send as a message*, and for `//tools` *Use /tools*.
* **The log:** each command from the page is logged with its name, the address and the verdict, never the rest of the line.

### A bot check from the page

When a web tool meets a bot check (a Cloudflare click, a captcha), the challenge runs in the PC's Browser window, because its pass is a cookie only that browser can use ([TOOLS.md](TOOLS.md#the-browser-window)). The Chat tab brings it to you:

* **The allow question** (*Web browser botcheck window* `ask`): Deny, Allow once or Allow for this session, on both sides at once; the first answer wins.
* **The card** shows the window live, with *Give up*. With *Web UI bot check control* off (on by default), the card has *Give up* alone.
  * **Tap** to click; **touch and hold** to press and hold, let go to release.
  * 🔍 zooms (fit, 2×, 3×); ▲ ▼ scroll the page.
  * The text box types into the field you tapped; ⏎ ⌫ ⇥ send Enter, Backspace and Tab.
* **It ends** when the page gets through, on *Give up* or Esc at either side, on the page's Stop, or after 5 minutes.
* **The PC:** the first input from a page flashes on the hint row (📱) and is logged with the phone's address; what you type is never logged.
* A minimized window is restored, without taking the keyboard, so it draws; a locked PC keeps drawing it, a sleeping one stops. **On a Mac**, see [The Browser window on a Mac](MAC.md#the-browser-window-on-a-mac).

### Safety and the network

* **Who may connect:** under `lan`, a browser at a private address (the home network, and Tailscale's 100.64/10) with the cookie.
* Private here doesn't mean safe (a router's NAT loopback, Docker or Tailscale can present one): the token is the gate.
* **Plain HTTP:** the links are IPv4 addresses, and the page is plain HTTP, so a browser won't send notifications or reach the clipboard.
* **The firewall:** Windows asks to let the app through the first time it serves the network; allow it on private networks. On a network Windows calls Public the phone can't connect. A Mac asks too.
* **A port that won't open:** another program uses it, or Windows reserves it (`netsh int ipv4 show excludedportrange protocol=tcp` lists the reserved ranges). Pick another in *Web UI port*.
* **Too fast:** taps, a bot check card's input, Ask the house and pasted sign-ins are limited per address; past that the page says to slow down.

### The page's look

* **The theme:** with *Web UI profile theme* on (the default), the pages take the profile's theme and follow a `/theme` at once; off, they keep their own dark look.
* **Add to Home Screen** installs the page as an app that opens full screen on Chat. One added from Safari keeps its own cookies: paste the sign-in link (or its token) on its sign-in page.
* **Reduced motion:** with the phone's reduce-motion on, the spinner and the animations stand still. The controls carry labels for a screen reader.
* **A new version** reloads the page once, its draft kept.
