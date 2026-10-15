# Using the app

The screen, the input line, panes, keys, the bars and the app's own windows. Back to the [README](../README.md). Settings named in *italics* are in [SETTINGS.md](SETTINGS.md), with their values and defaults.

## Contents

- [Words used here](#words-used-here) · [The screen](#the-screen) · [The input line](#the-input-line) ([completion lists](#completion-lists)) · [Panes](#panes) · [Keyboard shortcuts](#keyboard-shortcuts) · [Commands typed during a reply](#commands-typed-during-a-reply)
- [The hints bar](#the-hints-bar) · [The status bar](#the-status-bar) · [The toolbar](#the-toolbar) · [The performance bar](#the-performance-bar) · [The home bar](#the-home-bar) · [Bar checklists](#bar-checklists) · [The avatar picker](#the-avatar-picker) · [The welcome splash](#the-welcome-splash)
- [The app's windows](#the-apps-windows): [picture viewer](#the-picture-viewer) · [picture menu](#the-picture-menu) · [thumbnail browser](#the-thumbnail-browser) · [log](#the-log-window) · [process](#the-process-window) · [camera](#the-camera-window) · [video](#the-video-window) · [Browser](#the-browser-window)
- [Web UI](#web-ui)

## Words used here

* **Pane:** the box that opens at the bottom of the screen for settings, lists and questions. ESC closes it.
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
* **Five bars**, in *Toolbar order*: [hints bar](#the-hints-bar) (while there is a hint), [status bar](#the-status-bar), [toolbar](#the-toolbar), [performance bar](#the-performance-bar), [home bar](#the-home-bar). A short window drops the home bar first, then the performance bar, then the toolbar; the hints bar goes last.

## The input line

* **Editing**, even during a reply: ←/→, Home/End (again for the whole message), Ctrl+←/→ by word, Shift to select. Ctrl+Backspace and Ctrl+Delete delete a word (Option+Delete on a Mac).
* **Enter** sends; Ctrl+Enter starts a new line. ↑/↓ walk earlier messages and move through a wrapped draft.
* **Copy and paste:** Ctrl+A or a drag selects, Ctrl+C copies, Ctrl+X cuts. A right-click or Alt+V pastes, pictures too.
* **Ctrl+C** with nothing selected stops the speech, else cancels the reply; twice at an idle line, it exits.
* **Lists:** `/` commands, `@` files, `#` skills, `$` tools, `%` database connections, `^` ComfyUI workflows, `*` UNC shares. They work inside a command's text too, except in `/read`, `/view`, `/print` and `/pdf`. See [Completion lists](#completion-lists).
* **Drop a picture** from the picture strip or the transcript onto the input row to attach it (**🖼️ drop on line**).
* **During a reply** Enter queues the message. ESC stops the speech, then closes an open list, then cancels the reply; it never clears your draft.
* **At the idle line** ESC clears the draft (↑ brings it back); ESC twice on an empty line opens `/rewind`.
* **Voice:** with *STT input* on, the *STT push-to-talk key* records a message; with *STT wake* on, saying the wake phrase does.
* **Tool calls** show as `🛠️ read_file notes.md` with the result under them (`→ 4 lines`, ✗ when it failed). Long results fold once the run ends; Ctrl+O or `/expand` opens them.
* **During `/read`** (📖): Space on an empty line pauses and resumes; ←/→ step a sentence.

### Pasting a picture or a PDF

* A right-click or Alt+V puts a copied picture on the line as `[Image #n]`; so does Ctrl+V where the terminal passes it (Windows Terminal keeps Ctrl+V for text). With no picture, the text is pasted.
* Sent, the picture is saved in *Pasted images folder* under the profile's `images` folder, and the model gets its path.
* **A PDF** dropped on the line (or its path pasted) is attached as `[PDF #n report.pdf]`; on a `/command` line it stays its path. Its text is read in the background with your browser; sending waits for it under *Reading the attached PDF*, and ESC there keeps the draft.
  * The Anthropic and OpenAI APIs get the PDF itself (Anthropic reads 100 pages at most; a longer one is warned of). Other servers get its text, its first 100 pages: a scan or a locked PDF is refused there, and a text longer than half the model's window is cut, the rest left to `read_pdf`.
  * The line's pictures and PDFs together may come to *LLM max images (MB)*. The model gets the PDF's path in the sent text, and a saved session keeps its text, not the file.
* **On a Mac** Cmd+V pastes text only; use Ctrl+V, Option+V (with Option as Meta) or a right-click. A picture or PDF file copied in Finder is attached by name.

### Completion lists

A list opens over the input line as you type `/` at the start, a command's argument, or `@`, `#`, `$`, `%`, `^` or `*` before a word.

* **Keys:** ↑/↓ move the highlight, Enter or Tab applies it, ESC closes the list (it stays closed for that word).
* **Mouse:** a click highlights a row, a double-click applies it. The wheel over the list moves the highlight; elsewhere it scrolls the transcript. A click on the `▲▼` row pages.
* A right-click on the list does nothing (no paste), and a click off the list leaves it open.

## Panes

* **Keys:** ←/→ switch tabs, ↑/↓ move, Enter edits or toggles, Space flips an on/off row, ESC closes or goes back a page.
* **Second row of tabs** (`/settings`, `/tools`, `/skills`' Offered, `/mcp`'s Tools): ←/→ walk every one into the next tab; Tab and Shift+Tab jump a whole tab.
* **Under the list:** the row's description and default. A number after a tab's name (`Client³`) counts its settings changed from the default.
* **Height:** a pane keeps its height while it opens a value picker, a yes/no, a folder browse or details, so its title stays put; a taller details page gives the height back when it closes.
* **Type to filter** on `/sessions`, `/server`, `/model`, `/docker`, the saved lists, the allowed commands and the Offered tabs; the first ESC clears it. Where a pane has lettered buttons, a letter presses its button and Shift+letter starts a filter. A paste (Ctrl+V or a right-click) adds its first line, up to 40 characters.
* **Find** on the read-only panes (`/help`, `/sys`, `/about`, `/tree`, `/docker logs`…): type to mark every match; Enter or F3 goes to the next, Shift+Enter or Shift+F3 back (Shift+F3 on a Mac). A paste (Ctrl+V or a right-click) adds its first line, up to 40 characters.
* **Asking first:** a remove asks, with No on the cursor; `y` or `n` answers.
* **Mouse:** a click moves the cursor, a double-click picks a row or tab. A click on the `▲▼` row under a long list pages up (`▲`) or down (`▼`). On the input line a double-click selects a word, on the `›` the whole draft. The × at the top right is ESC; a double-click outside a pane closes it.
* **On a Mac**, to select terminal text with the mouse, hold Fn (Terminal.app) or Option (iTerm2), or toggle the app's mouse with Cmd+R in Terminal.app.

### Pane keys

In every list ↑/↓ move, Enter or a double-click picks, ESC closes; key letters act at once.

| Pane | Keys |
|---|---|
| **Conversation** | |
| `/memory` | Enter: edit, disable/enable, forget (asks) · `a` add · `m` *Memory mode* |
| `/queue` | Enter removes (asks) · `c` clear all · `s` send (while held, or under *Queue mode* `manual`) · `m` *Queue mode* · `k` *Queue cancel mode* |
| `/rewind` | Enter rewinds to before the message |
| `/sessions` | Enter opens · type to filter |
| `/find` | type to find · a paste or right-click adds its first line · Enter or F3 older · Shift+Enter or Shift+F3 newer · PgUp/PgDn scroll · ESC or a double-click outside done |
| Info panes (`/help`, `/sys`, `/about`, `/tree`…) | ↑/↓, PgUp/PgDn scroll · ←/→ or Tab switch tabs · type to find · ESC closes |
| **Saved lists** | |
| `/youtube list` | Enter plays |
| `/youtube <words>`, `/youtube playlist` | Enter plays · `s` saves the video |
| `/prompts` | Enter puts the prompt on the draft line · `a` add · `s` save the draft · `e` edit (a long one in the draft editor) |
| `/browser list` | Enter opens in the [Browser window](#the-browser-window) · `a` add · `n` name · `e` address |
| All three | `d` or Delete removes (asks) · `c` clears all (asks) · Shift+↑/↓, Shift+Home/End move · type to filter |
| **Tools and safety** | |
| `/tools shell` | `c` the allowed commands · `p` the police |
| `/tools shell allowed` | Enter removes (asks) · `p` steps *Shell command policy* (yolo asks; a no goes on to off) · type to filter |
| `/tools shell police` | `s` the forbidden strings |
| *Shell police forbidden strings* | top row adds · Enter removes (asks) · `r` restore defaults (asks) |
| `/tools web` | `m` *Web browser mode* · `o` opens the Browser window (Windows only) |
| `/tools files` · `image` · `obsidian` · `advisors` · `print` | `b` *File browser/tree mode* · `e` *Image edit mode* · `v` *Obsidian vault* · `a` *Advisor list* · `p` *Print default printer* |
| `/tools camera` | `w` watch · `l` live · `s` snap · `c` screen |
| The camera pane | Space takes · `r` retake · Enter attaches · ESC drops |
| The shell approval | `d` deny · `o` once · `s` session · `a` always · `v` the whole command · ESC denies |
| A database write's approval | `d` deny · `o` once · `s` session · `v` the whole statement · ESC denies |
| **Servers and processes** | |
| `/server`, `/model` | Enter chooses · type to filter |
| *Embedded models* | `s` size · `i` installed or not · `d` drafter · `o` sort · `x` uncensored |
| `/process` | Enter opens the [window](#the-process-window) · `k` kills (asks) · `f` all, running, ended |
| `/docker` | Enter opens · `r` refresh · `l` LLM servers only · type to filter |
| **Skills** | |
| `/skills` › a skill | Enter on a row: move (asks), rename, edit, revert, lock/unlock, disable/enable, delete (asks) |
| `/skills` › a skill › `revert` | Enter puts the version back · `d` removes it (asks) · `c` clear all (asks) |
| **Shopping** | |
| `/shopping` | Enter or Space ticks · `i` add · `p` place at an aisle or section · `n` rename · `r` remove · `a` tick all · `c` clear ticked · `s` stores · `+` / `-` quantity |
| Its add page (`i`) | Enter on the first row types new items · Enter on a past item adds it · `x` forgets it · type to filter |
| *Shopping stores* (`s`) | Enter shops there (the first row adds one) · `l` layout · `n` rename · `r` remove (asks) · `f` its folder · `i` reindex |
| A store's layout (`l`) | tabs **Aisles**, **Sections**, **Facts**, **Guesses** · Enter renames, moves or removes |
| **Settings and bars** | |
| `/settings changed` | Enter edits · `r` back to default |
| `/toolbar edit`, `/perfbar edit`, *Status bar* | see [Bar checklists](#bar-checklists) |
| `/homebar edit` | **On the bar**: see [Bar checklists](#bar-checklists); Enter for rename and emoji · other tabs: Space or Enter adds or removes, type to filter |
| A room's pane on the home bar | Space switches a device · Enter its actions |

### Folder picker

`/cwd browse` and the *Working directory (cwd)* and *Obsidian vault* rows open a folder tree. `⌂ profile` (the profile's `files` folder) and `▣ images` sit above the drives. Space, → and ← open and close folders (so does a click on a folder's glyph), `-` collapses all, and Enter or a double-click on a name chooses.

### File browser

`/browse [<folder>]` opens a tree of the working directory, folders first. Space, → and ← open and close folders, `-` collapses all, and Enter or a double-click opens a file in its default app. *File browser/tree mode* decides what is listed; `.git` folders and links are left out.

## Keyboard shortcuts

Each runs its command as if typed, and keeps your draft. The last column is what it does during a reply.

| Shortcut | Runs | During a reply |
|---|---|---|
| Ctrl+. | `/terminal` | at once |
| Ctrl+/ | `/settings` | over the reply |
| Ctrl+E | `/explore` | at once |
| Ctrl+F | `/perfbar` | at once |
| Ctrl+G | `/log` (the [log window](#the-log-window)) | at once |
| Ctrl+Shift+F | `/find` (Windows Terminal keeps it until you unbind it) | waits |
| Ctrl+H | `/help` | over the reply |
| Ctrl+J | the emoji picker (not on a Mac) | idle line only |
| Ctrl+L | cancels the background learning (🧠) | at once |
| Ctrl+M | `/model` | waits |
| Ctrl+O | folds or unfolds tool calls, code, diffs and thinking | at once |
| Ctrl+P | `/profile` | waits |
| Ctrl+Q | `/queue` | over the reply |
| Ctrl+R | `/reasoning` | over the reply |
| Ctrl+S | `/server` | waits |
| Ctrl+T | `/toolbar` | at once |
| Ctrl+U | `/usage` | over the reply |
| Ctrl+W | `/homebar` | at once |
| Ctrl+Y | `/sys` | over the reply |
| Ctrl+Z | `/theme` | waits |
| Ctrl+Alt+B | `/botchat --resume` | ends a running botchat at once; otherwise waits |
| Ctrl+Alt+C | `/clear` | stops the reply first |
| Ctrl+Alt+D | `/mcp` | over the reply |
| Ctrl+Alt+E | `/sessions` | over the reply |
| Ctrl+Alt+H | `/header` | at once |
| Ctrl+Alt+K | `/botchat --kill` (the extra embedded servers a botchat left) | waits |
| Ctrl+Alt+L | `/tools shell allowed` | over the reply |
| Ctrl+Alt+M | `/memory` | over the reply |
| Ctrl+Alt+N | `/new` | stops the reply first |
| Ctrl+Alt+O | `/tools shell police` | over the reply |
| Ctrl+Alt+P | `/splash` | stops the reply first |
| Ctrl+Alt+Q | `/queue clear` | at once |
| Ctrl+Alt+R | `/rename` | over the reply |
| Ctrl+Alt+S | `/skills` | over the reply |
| Ctrl+Alt+T | `/tools` | over the reply |
| Ctrl+Alt+U | `/comfy view` (the [picture viewer](#the-picture-viewer)) | at once |
| Ctrl+Alt+V | `/camera live` (the [camera window](#the-camera-window)) | at once |
| Ctrl+Alt+W | `/prompts` | over the reply |
| Ctrl+Alt+X ×2 | unloads the embedded model | at once |
| F9 | `/camera snap`, attached to the line | waits |
| F10 | `/screen`, attached to the line | waits |

* **In a pane** a shortcut closes the pane and runs. Ctrl+., Ctrl+E, Ctrl+F, Ctrl+L, Ctrl+T, Ctrl+W, Ctrl+Alt+H, the window chords and Ctrl+Alt+X act and leave it open. A pane that asks you something ignores them all.
* **Window chords** (Ctrl+G, Ctrl+Alt+U, Ctrl+Alt+V) close their window when pressed again; the typed command only opens it.
* **Ctrl+Alt+X**, twice within two seconds, unloads the embedded model and frees its memory, canceling any reply, load or botchat using it. No server is connected until you pick one; *LLM URL* is kept. With any other server it does nothing.
* **F9 and F10** work with no modifier held; the picture viewer and thumbnail browser keep their own meanings for them.
* **On a Mac**, Ctrl+] stands in for Ctrl+., Ctrl+D for Ctrl+M and Ctrl+Option+A for Ctrl+Alt+M (the originals still work in the app's windows). Ctrl+Shift+F arrives as Ctrl+F, so type `/find`. Ctrl+Option chords need Option as Meta ([MAC.md](MAC.md#what-differs)).
* **A chord that does nothing** may be another program's global hotkey. `/keycheck`, or `k` on `/help`'s Keys tab, lists what holds each one.

## Commands typed during a reply

| Behavior | Commands |
|---|---|
| Open their pane over the reply | `/help`, `/settings`, `/tools`, `/mcp`, `/sys`, `/usage`, `/about`, `/memory`, `/queue`, `/sessions`, `/sessions title`, `/rename`, `/skills`, `/reasoning`, `/sampling`, `/msgclear`, `/tree`, `/vault`, `/browse`, `/docker`, `/youtube`, `/shopping`, `/prompts`, `/browser list`, `/camera list`, `/ha states`, `/keycheck`, `/process`, `/sys persona`, `/sys operata`, `/sys vocalia`, `/toolbar edit`, `/perfbar edit`, `/homebar edit`, a home bar chip's pane |
| Run at once | `/ha`, `/camera live`, `/camera watch`, `/camera off`, `/camera use`, `/camera view`, `/camera thumbs`, `/screen view`, `/screen thumbs`, `/tts`, `/stt`, `/stt wake`, `/stt interrupt`, `/perfbar`, `/homebar`, `/homebar on`, `/homebar off`, `/toolbar`, `/header`, `/reasoning <level>`, `/sampling <field> <value>`, `/queue clear`, `/queue mode <word>`, `/queue cancel <word>`, `/copy`, `/remember`, `/explore`, `/open`, `/browser <url>`, `/prompts <n>`, `/terminal`, `/shortcut`, `/log`, `/process <id>`, `/timer`, `/expand`, `/collapse`, `/window`, `/cwd`, `/comfy view`, `/comfy thumbs`, `/view <path>`, `/view <path> --thumbs` |
| Stop the reply first | `/clear`, `/new`, `/splash`, `/rewind`, `/exit` |
| Everything else | Waits for the reply, queued behind earlier messages (*Queue cancel mode* applies) |

In a pane over a reply, rows that would disturb it (a reconnect, the profile, the working directory, the theme, a session, a skill's page, an MCP server) answer *(not while a reply runs)* until the reply ends.

## The hints bar

The keys that apply just now, on a row of their own over the status bar (*Hints bar*). It shows one hint at a time, the first of:

1. The kill switch's warning (Ctrl+Alt+X, press again to unload).
2. A waiting second press: Ctrl+C to exit, ESC to rewind, Delete for a splash picture.
3. An open pane's keys.
4. What the arrows do just now: the splash's pages, the picture strip, `/read`'s keys.

* **auto** (the default): the bar takes a row when a hint comes and keeps it, blank, until you send, a reply ends, a pane opens or closes, the window is resized, or 10 seconds pass with no hint and no key. Typing never moves the input line.
* **always** keeps the row; **off** puts the hints back at the end of the status bar's left group.
* During a reply it shows only a pane's keys or the kill switch's warning; **esc to stop** stays on the status bar.
* A double-click on it opens `/settings`. It moves with the other bars in *Toolbar order*.

## The status bar

The row under the hints bar (*Status bar*). [Bar checklists](#bar-checklists) moves and pins its sections:

* **Left by default:** **Status** (🔊 🎤 👂 ✋, 🧠, 🏷️…), **Spinner** (during a reply, with its clock and **esc to stop**; always on), **Queued**, **Timers**, **Reading** (`/read`, `/echo`) and **Usage** (context and tokens).
* **Right by default:** **Model** (name and reasoning glyph) and **Working directory** (off by default).
* During a reply the busy row keeps the same sides.
* **Narrow window:** the model's name and the path shrink, then go; then the right group gives way, then the left. The spinner goes last.
* **Background setups** (an embedded model's download, MCP servers connecting, the voice models' first load) show 📥, 🔌, 🎙️ or 🔈 while the input line stays yours.

| Double-click | Does |
|---|---|
| the model name | `/server` |
| the reasoning glyph | `/reasoning` |
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

Glyphs under the status bar (*Tool bar*), each shown while it is checked there. A click says what a glyph is; a double-click or a right-click opens it. A tool group that is off sits on a dark slab.

| Toolbar item | Shown | Opens |
|---|---|---|
| ⚙️ 🪪 🧮 🛠️ 🔌 🎓 🎭 💬 📊 | always | `/settings`, `/profile`, `/theme`, `/tools`, `/mcp`, `/skills`, `/sys`, `/sessions`, `/usage` |
| 📱 | slab while *Web UI* is off | `/webui` (after 🧮) |
| 💾 | slab while *Memory mode* is `off` | `/memory` |
| 📣 | always | `/prompts` |
| ⏳ | slab while *Queue mode* is `off` | `/queue` |
| 🙋 | slab while *Ask user* is off | `/tools ask` |
| 🔒 / 🔓 | *Shell command policy* `ask` / `yolo` (none under `off`) | `/tools shell allowed` |
| 👮 / 🥷 | *Shell police* on / off (none while the policy is `off`) | `/tools shell police` |
| 🐚 | slab under `off` | `/tools shell` |
| 📁 🌄 🌐 🦉 🐳 💎 🛢️ 🔮 🐬 🪶 🐘 🔗 🏠 🎨 📸 🖨️ | slab while off | `/tools files`, `image`, `web`, `advisors`, `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `sqlite`, `postgres`, `unc`, `ha`, `comfy`, `camera`, `print` |
| 📄 ⚡ | always | the [log window](#the-log-window); `/process` |
| 🎞️ 🪟 | always | the [picture viewer](#the-picture-viewer); the [thumbnail browser](#the-thumbnail-browser) (ComfyUI's folder) |
| 📺 ⭐ 🛒 | always | `/youtube list`, `/browser list`, `/shopping` |
| the working directory (`~` for your user folder) | pinned right | `/cwd browse` |
| **Model name** and its glyph | pinned right, with a server connected | `/server`; the glyph `/reasoning` |
| blank space | — | `/settings` |

* **Dividers** (`│`) show only between two visible items ([Bar checklists](#bar-checklists)).
* **Narrow window:** the glyphs come first; the model name and the path shrink, then go; then items drop from the right group, then the left.

## The performance bar

Meters updated once a second (*Performance bar*): **CPU**, **RAM**, **GPU**, **VRAM** (**GMEM** on a Mac), **NET**, **NET↓**, **NET↑** and **PROC**. `/perfbar text`, `gauge` or `led` picks the look, as does the checklist's **look:** button (`l`, stepping text → gauge → led); `/perfbar` or Ctrl+F hides it.

* Values turn amber from 60 % and red from 85 %. A meter the machine can't read is left out.
* NET is the share of the link speed; NET↓ and NET↑ are bits per second. They follow the busiest adapter with a gateway.
* PROC counts the model's background processes still running.
* **On a Mac** RAM is Activity Monitor's *Memory Used*; GMEM is the memory the GPU holds, as a share of what macOS lets it use.
* Meters are pinned left, center or right, center by default. On a narrow row they shrink, then show numbers only, then the side groups give way; the center wins.
* A double-click or a right-click on the bar opens `/settings`.

## The home bar

Chips for Home Assistant rooms and devices and the shopping list (*Home bar*); see [TOOLS.md](TOOLS.md#home-assistant). `/homebar` or Ctrl+W hides or shows it.

* **Chips:** a light wears its own color, a room counts its lights on (`2/5`), sensors show values, thermostats current → target, players ▸ or ‖. Off is dim; an open door, a leak, a jammed lock or a low battery turns amber or red. All go dim while Home Assistant is unreachable.
* **🛒 n** counts the shopping lines still to get; it needs no Home Assistant and opens `/shopping`.
* **Clicks:** a click shows a chip's state. A double-click acts: toggles a light, switch or fan, turns a room's lights off (or on), runs a scene, plays or pauses, closes or opens a cover. A right-click opens its pane of actions (a room's: its devices). A chip shows `…` until the change is reported, `!` after six seconds without.
* **Asks first:** unlocking, opening a garage, gate or door, and disarming an alarm. An alarm that wants a code is disarmed in Home Assistant itself.
* **Live:** while shown, one WebSocket watches the bar's devices; hidden for 15 seconds, it closes. A dropped one retries after 1, 2, 4… up to 30 seconds; a refused key isn't retried until the URL or key changes.
* **Narrow window:** names shrink, then the side groups give way, then the center pages behind ‹ ›.
* A double-click or a right-click on blank space opens `/settings`.

**The picker** (Enter on *Home bar*, or `/homebar edit`):

* **On the bar:** the chips under **Pinned left**, **Pinned center** and **Pinned right** ([Bar checklists](#bar-checklists)). Enter offers the moves too, plus:
  * **Rename on the bar…**: an alias the chip shows ("PC"); blank shows the emoji alone. **Default name** puts Home Assistant's back. Panes and notes keep the real name.
  * **Change emoji…**: the [emoji grid](#the-avatar-picker); **Default emoji** or 🚫 puts the chip's own back. A chosen emoji stays fixed, so a lock's 🔒/🔓 no longer follows its state. `/homebar emoji <item> = <emoji>` does the same.
  * **+ Add divider** (the last row): a dim `│`, shown only between two chips of its group.
* **The other tabs:** **Rooms** (by floor), **Lights**, **Switches** (and fans), **Sensors**, **Climate**, **Media**, **Scenes** (and scripts), **Security** (locks, covers, alarms), **Zones** (people in each), **Other** and **Shopping**, in sections by room. Space or Enter adds or removes; typing filters by name, id, room or label. Without Home Assistant only **On the bar** and **Shopping** show.

## Bar checklists

The *Status bar*, *Tool bar* (`/toolbar edit`) and *Performance bar* (`/perfbar edit`) checklists, and the home bar's **On the bar**, share these keys:

* Space or Enter checks or unchecks; Delete unchecks (on the home bar, takes off). `a`, `n` and `d` pick all, none or the default; `d` also puts back the default order, sides and dividers. None checked hides the toolbar or the performance bar.
* Shift+←/→ (or Alt+←/→) pins the item to the other side. The performance and home bars have three groups, so it moves a group over (left ↔ center ↔ right).
* Shift+↑/↓ (or Alt+↑/↓) moves it a place, crossing into the next group at the edge; Shift+Home/End moves it to its group's top or bottom.
* **Performance bar:** **look:** on the title row (`l`) steps the look: text, gauge, led.
* **Status bar:** the Spinner is always on; it can't be unchecked, only moved. The row reads `default`, `all`, `none`, or the left group's sections, `|`, then the right group's.
* **Tool bar dividers:** `+` (*+ divider* on the title row) puts a dim `│` after the item under the cursor. It moves like an item, Delete takes it out, and Enter or Space does nothing on it. It shows only between two visible items of its side. The default has three: `⚙️ 🛠️ 🎓 │ 💾 📣 ⏳ │ 🔒 👮 🐚 │ 📁 🌄 🌐`.

*Toolbar order* orders the five bars the same way: Shift+↑/↓ (or Alt+↑/↓) moves the bar under the cursor, Shift+Home/End to the top or bottom, `d` puts the default back, and the screen redraws at once. A bar that is off keeps its place.

## The avatar picker

* Enter on *Model avatar* (or `/avatar`) opens a grid of every emoji Windows Terminal draws (Unicode Emoji 15.1, no flags). *User avatar* opens it for your own lines.
* **Tabs:** **⭐ For you** (matching the profile's name and persona), **🕘 Recent**, then the Unicode groups. Tab and Shift+Tab step them; the arrows move; a double-click picks.
* Type to search (`fox`, `red heart`) or paste an emoji; ESC clears. 🎲 picks at random; 🚫 clears the avatar.
* Enter on a hand or a person opens its six skin tones. Other profiles' avatars are marked.
* Recent picks and the last skin tone are kept in `emoji.json` in the home folder, shared by every profile.

## The welcome splash

* `fullsize`: one picture; ←/→ step through them. Delete twice on an empty line moves one of the profile's pictures to the folder's `.trash`.
* `tiled`: thumbnails at *Image thumbnail size*; ←/→ page, a double-click opens one.
* Pictures in the profile's `images\splash_images\` replace the built-in ones. `/splash` shows the splash whatever *Welcome splash* says.

## The app's windows

What every window shares:

* **Focus:** opened by its key chord, a window stays behind the terminal; typed, it comes to the front. However it closes (Esc, Alt+F4, ×, its chord), the terminal gets the keyboard back.
* **Keys:** Tab brings the terminal forward, and a Ctrl or Alt chord the window doesn't use runs in the chat. The Browser window keeps every key for its pages; on Windows the video window keeps Tab.
* **Place and look:** a window reopens where it last closed, closes with the app, and wears the theme unless *Themed external windows* is off.
* **On a Mac** they need the Mac's desktop (not over SSH). ⌘W closes, ⌃⌘F is full screen (F11 too, once macOS's Show Desktop shortcut is off), and Option chords run in the chat.

### The picture viewer

Opened by a double-click on a picture in the transcript (*Image viewer* can name another program), `/view <image or folder>` (which follows new pictures), and `/comfy view`, `/camera view` or `/screen view`. Without a window (over SSH on a Mac), `/view` draws in the transcript. A PDF goes to [the Browser window](#the-browser-window) instead.

| Key | Action |
|---|---|
| ← / →, the wheel, **<** / **>** | Newer / older (newest at the left; reaching it follows new pictures) |
| Home / End | Newest / oldest |
| Right-click, Apps key, Shift+F10 | The [picture menu](#the-picture-menu) |
| F11 or double-click | Full screen |
| Drag the picture | Copies it where you drop it |
| Del twice within 2 s | Deletes the picture for good |
| F9 · ↑ / ↓ · F10 | Slide show on or off (5 s) · a second more or less · folder or random order |
| Esc | Stops the slide show, then leaves full screen, then closes |

* The viewer, the thumbnail browser and the picture strip follow each other's selection.
* **On a Mac** ⌫ is Del, a two-finger swipe steps, Control-click is a right-click.

### The picture menu

A right-click in the viewer or the thumbnail browser.

* **Edits:** rotate and flip, color (grayscale, sepia, negative, polaroid), resize, convert (PNG, JPEG, GIF, BMP), shrink the file under a size, strip metadata losslessly. *Image edit mode* decides whether an edit writes `photo-edited.png` or replaces the picture.
* **The file:** copy its path, show it in Explorer (Finder on a Mac), attach it, print it, or delete it for good, without asking.

### The thumbnail browser

`/view <folder> --thumbs`, `/comfy thumbs` (🪟), `/camera thumbs` and `/screen thumbs` show a folder's pictures, oldest first, new ones added at the end.

| Key | Action |
|---|---|
| Click / double-click or Enter | Select (the viewer follows) / open in the viewer |
| Arrows, PgUp / PgDn, Home / End, the wheel | Move, scroll |
| + / − or Ctrl+wheel | Bigger / smaller thumbnails |
| F5 | Re-read the folder and fit the thumbnails |
| Del twice within 2 s | Deletes the picture for good |
| F11 or double-click between thumbnails | Full screen |
| Esc | Closes the menu, then leaves full screen, then closes |

A right-click opens the picture menu, with *Open in the viewer* first. **On a Mac** ⌫ is Del; ⌘= and ⌘− or a pinch change the size.

### The log window

`/log` (Ctrl+G, 📄) shows this run's diagnostic log from the start, with or without `--log`: the newest 20,000 lines, colored by level. At the bottom it follows new lines; scrolled up it holds still (*paused*). Drag to select, Shift+click to extend.

| Key | Action |
|---|---|
| ↑ / ↓, PgUp / PgDn, the wheel | Scroll; at the bottom it follows again |
| Ctrl+Home / Ctrl+End or Ctrl+E | First line / last line, following |
| Ctrl+A / Ctrl+C | Select all / copy |
| F11 or double-click | Full screen |
| Esc | Leaves full screen, then closes |

**On a Mac** ⌘A and ⌘C select and copy, ⌘↑ and ⌘↓ go to the top and bottom.

### The process window

`/process <id>` (or Enter on `/process`'s list, ⚡) shows a background process's last 5,000 lines live, with the log window's keys. Stderr is in the warning color; the title shows the id, command and state (`running`, `exited 0`, `stopped by you`). Ctrl+K twice within 3 seconds stops it and everything it started, and the model hears of it.

### The camera window

`/camera live` (Ctrl+Alt+V) shows the camera live until you close the window or type `/camera off`.

* It never takes the keyboard. F11 or a double-click is full screen; Esc leaves full screen, then closes it.
* *Camera preview* `live` shows the camera pane's shot here as you frame it. The camera is one shared stream, so live view, the pane, watch mode and `/botchat` can use it together; Ctrl+Alt+V closes only a window `/camera live` opened.
* **On a Mac** the camera needs macOS 14 or later.
* **An unplugged camera freezes `/camera live` silently**: the window stays on its last frame, with nothing in the transcript, until `/camera off`.

### The video window

Where YouTube videos play ([TOOLS.md](TOOLS.md#youtube)). It doesn't take the keyboard; F11 is full screen, Esc leaves it and then closes the window. YouTube's own keys (Space or K, ←/→, F, M) work once you click into the video.

**On a Mac** it needs macOS 14 or later (WebKit). The player's own full-screen button fills a Space of its own; Esc brings it back.

### The Browser window

A browser window (Windows only): the web tools open it for bot checks ([TOOLS.md](TOOLS.md#the-browser-window)), and `/browser [url]` opens it for you. When you close it, its cookies go to the web tools. A bare `/browser` opens *Web browser home page*.

* **The bar:** back, forward, reload (✕ stops a load), home, a padlock (https) or a warning (http), the address box, Go, and open in your default browser. Words that aren't an address search with *Web search method*; `file:`, `javascript:` and local paths are refused.
* **Keys:** Ctrl+W closes; F5 or Ctrl+R reloads; Alt+← / Alt+→ go back and forward; Ctrl+L, Alt+D or F6 go to the address box (Esc there restores it); Ctrl+H opens the history; Ctrl+D saves or unsaves the page; Ctrl+Shift+O opens the saved links; Esc stops a load. Esc never closes the window.
* **Full screen:** F11, with the bar kept. A page's own full screen (a video's button) takes the window and hides the bar. Esc leaves the page's first, then F11's.
* **History** (🕘, Ctrl+H): newest first, older by day, with **Clear history**. *Web browser privacy* `clear-history` clears it at each close; `keep-history` keeps it in `<profile>\browser-history.json`. Cookies and sign-ins stay either way.
* **Saved links** (☆, ★ on a saved page): the first 15 on the menu, the rest in submenus, up to 190; `/browser list` (⭐) has them all. They're kept in `<profile>\browser-links.json`. An address holding a user name or password is never saved. The model reaches them with `web_links` and `web_save_link` ([TOOLS.md](TOOLS.md#web)).
* **Per profile:** sign-ins and solved checks live in `<profile>\webview2-browser`. Switching profiles closes the window. Two copies of the app can each open it, on different profiles.
* **Clearing:** `/browser clear history` clears the history now. `/browser clear all` asks, then clears the history, cookies, sign-ins, cache and site settings; saved links stay.
* **A PDF:** `/view report.pdf` shows a PDF from the working directory in the window's own PDF viewer. It shows a copy, made in `%TEMP%\NeonSidekick\pdfview` and deleted when the window closes; the open button opens the file itself in its own program. A PDF view isn't kept in the history, can't be saved as a link, and hands no cookies over. Where the window can't take it (a bot check waiting, no WebView2 runtime), the PDF opens in its own program; a policy that makes Edge download PDFs makes the window download it.

## Web UI

A browser front end the app hosts for the loaded profile: the chat as it happens, a message line of its own, the questions the model's tools ask, and the shopping list. Turn it on with `/webui on` or *Web UI* (`/settings` › Client › Web UI). Not headless.

* **Signing in:** `/webui` shows a tab per home network address of this machine, each with a QR code and its sign-in link: the Ethernet and Wi-Fi adapters with a gateway, the fastest first (`localhost` under `local`). This machine's Tailscale address (100.64/10, on Tailscale's own adapter) comes after them, its tab headed Tailscale (see Away from home). Other VPN tunnels and WSL's virtual switch aren't listed, though a browser through them still connects with the address typed; with no home adapter, every private address is listed. Scan the code with the phone's camera, or open the link in a browser on the same network. The link signs the browser in and leaves the address bar at once; the browser keeps a cookie for a year. `/webui copy` puts the link on the clipboard.
* **Away from home:** install Tailscale on this PC and the phone and sign both in to the same tailnet; scan the Tailscale tab's code once (it's another host to the browser, so another cookie, beside the home network's). The phone then reaches the Web UI wherever it is, with Tailscale on. Nothing to open on the router; *Web UI reach* must be `lan`. Windows' firewall treats Tailscale's network as it treats the home one: if Windows calls it Public, the phone can't connect (see The firewall).
* **The token:** each profile has its own, kept encrypted. `/webui reset` makes a new one and signs every browser out, and so does changing the port (the cookie is named by the port). A link sent through a messaging service hands that service the token too; `/webui reset` afterwards. A profile folder copied from another machine makes a new token.
* **The pages:** Chat and Shopping, in one fixed dark style. The page's own draft line stays in the browser; it never shows what you type at the PC, nor the PC what you type there. Enter sends on a desktop, a phone adds a line (tap ➤). Stop (■) shows while a reply runs.
* **What the page shows:** your lines and the replies (Markdown, the links opened in a new tab, no pictures loaded), the tool lines and the first line of their results, the notices, a divider for `/new`. It starts afresh when the screen does (`/clear`, a rewind, a resume, a theme change, a profile switch). Not shown: thinking, pictures, the panes you open at the PC.
* **Messages from the page:** sent from the idle line, after anything queued at the PC. During a reply they wait (counted in the status bar's Queued, not listed in `/queue`), and they're sent as the reply ends even under *Queue mode* `manual`; a held queue (*Queue cancel mode* `hold`) holds them too. `/clear`, `/new`, a resume, a rewind, a profile switch or *Queue cancel mode* `empty` drops them and puts the text back on the page; `/queue clear` leaves them. Slash commands are refused: they run at the PC. At most 20 wait at once; a message holds up to 48 KB.
* **Stop on the page:** ends a reply, a compact, a botchat, an advisor, `/imagine`, `/test` or `/loop`'s wait, and stops speech. Before the reply's first word it takes the message back to the page, as ESC does at the PC. It never stops a model loading (Ctrl+C at the PC) or your editor under `/draft`.
* **Questions:** a command's approval, `ask_user`, an allow (the camera, the screen, a database write), a yes/no (Docker, Home Assistant, a print, the shopping list's clear, an advisor), the plan's approval and a reflection's skill show on the PC's pane and on the page at once, in the PC's own words; the first answer wins, and the PC says when the page answered. The page shows a command or a statement whole. Not on the page: Close gaps (the plan's), the bot check, the camera's shutter. While the PC waits on one of those, the page says "waiting for an answer at the PC".
* **Shopping:** the list walked through the active store, as the model reads it; a tap ticks or unticks a line. It re-reads the list every five seconds while it's open.
* **The firewall:** Windows asks to let the app through the first time it serves the network; allow it on private networks. On a network Windows calls Public the phone can't connect. A Mac asks too.
* **Who may connect:** under `lan`, a browser at a private address (the home network, and Tailscale's 100.64/10) with the cookie. Private here doesn't mean safe (a router's NAT loopback, Docker or Tailscale can present one): the token is the gate. The links are IPv4 addresses only. The page is plain HTTP, so a browser won't send notifications or reach the clipboard; a waiting question shows as a count in the tab's title.
* **The pane's buttons:** `📱 Web UI │ enabled │ lan │ reset │ 192.168.1.217 · 100.78.20.125`. The first three are buttons, ahead of the address tabs: *enabled*/*disabled* (`e`) turns *Web UI* on or off, *lan*/*local* (`l`) switches *Web UI reach*, and *reset* (`r`) makes a new token after a yes, as `/webui reset` does. The server starts, stops or restarts at once and the pane opens again. With one address (`localhost` under `local`) the pane has one tab and shows no title for it.
* **The toolbar's 📱** opens the pane (*Tool bar*, off by default). It sits on a dark slab while *Web UI* is off.
* **A port that won't open:** another program uses it, or Windows reserves it (`netsh int ipv4 show excludedportrange protocol=tcp` lists the reserved ranges). Pick another in *Web UI port*.
* **Without the pane** (a redirected console), `/webui` prints the links as notices, so the token reaches the scrollback.
