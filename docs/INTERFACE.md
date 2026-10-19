# Using the app

The screen, the input line, panes, keys, the bars and the app's own windows. Back to the [README](../README.md). Settings named in *italics* are in [SETTINGS.md](SETTINGS.md), with their values and defaults.

## Contents

- [Words used here](#words-used-here) · [The screen](#the-screen) · [The input line](#the-input-line) ([completion lists](#completion-lists)) · [Panes](#panes) · [Keyboard shortcuts](#keyboard-shortcuts) · [Commands typed during a reply](#commands-typed-during-a-reply)
- [The hints bar](#the-hints-bar) · [The status bar](#the-status-bar) · [The toolbar](#the-toolbar) · [The performance bar](#the-performance-bar) · [The home bar](#the-home-bar) · [Bar checklists](#bar-checklists) · [The avatar picker](#the-avatar-picker) · [The welcome splash](#the-welcome-splash)
- [The app's windows](#the-apps-windows): [picture viewer](#the-picture-viewer) · [picture menu](#the-picture-menu) · [thumbnail browser](#the-thumbnail-browser) · [log](#the-log-window) · [process](#the-process-window) · [camera](#the-camera-window) · [video](#the-video-window) · [Browser](#the-browser-window)
- [Web UI](#web-ui)

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
* **The picture strip**, while there are ComfyUI pictures: its 🎞️ opens the [picture viewer](#the-picture-viewer), × closes the strip. The [Web UI](#web-ui)'s chat page has a strip of its own in a drawer.
* **The upper rule:** the session's name (a double-click renames it), **⤡** (a click folds or unfolds everything, like Ctrl+O), and a 🎞️ that brings a closed picture strip back.
* **[The input line](#the-input-line).** A dim ☺ at its right end opens the emoji picker (*Emoji picker*).
* **The lower rule:** **▶ n** counts the background processes still running; a double-click opens `/process`.
* **A mark:** `/mark <color>` draws both rules in that color for the conversation (kept with its session), the session's name keeping its own; `/mark default` takes it off.
* **Five bars**, in *Toolbar order*: [hints bar](#the-hints-bar) (while there is a hint), [status bar](#the-status-bar), [toolbar](#the-toolbar), [performance bar](#the-performance-bar), [home bar](#the-home-bar). A short window drops the home bar first, then the performance bar, then the toolbar; the hints bar goes last.

## The input line

* **Editing**, even during a reply: ←/→, Home/End (again for the whole message), Ctrl+←/→ by word, Shift to select. Ctrl+Backspace and Ctrl+Delete delete a word (Option+Delete on a Mac).
* **Enter** sends; Ctrl+Enter starts a new line. ↑/↓ step through earlier messages and move through a wrapped draft.
* **Copy and paste:** Ctrl+A or a drag selects, Ctrl+C copies, Ctrl+X cuts. A right-click on the input line, or Alt+V, pastes, pictures too; a right-click elsewhere does nothing.
* **Ctrl+C** with nothing selected stops the speech, else cancels the reply; twice at an idle line, it exits.
* **Lists:** `/` commands, `@` files, `#` skills, `$` tools, `%` database connections, `^` ComfyUI workflows, `*` UNC shares. They work inside a command's text too, except in `/read`, `/view`, `/print` and `/pdf`. See [Completion lists](#completion-lists).
* **Drop a picture** from the picture strip or the transcript onto the input row to attach it (**🖼️ drop on line**).
* **During a reply** Enter queues the message. Esc stops the speech, then closes an open list, then cancels the reply; it never clears your draft.
* **At the idle line** Esc clears the draft (↑ brings it back); Esc twice on an empty line opens `/rewind`.
* **Voice:** with *STT input* on, the *STT push-to-talk key* records a message; with *STT wake* on, saying the wake phrase does.
* **Tool calls** show as `🛠️ read_file notes.md` with the result under them (`→ 4 lines`, ✗ when it failed). Long results fold once the run ends; Ctrl+O or `/expand` opens them.
* **During `/read`** (📖): Space on an empty line pauses and resumes; ←/→ step a sentence.

### Pasting a picture or a PDF

* A right-click on the input line or Alt+V puts a copied picture on the line as `[Image #n]`; so does Ctrl+V where the terminal passes it (Windows Terminal keeps Ctrl+V for text). With no picture, the text is pasted.
* Once sent, the picture is saved in *Pasted images folder* under the profile's `images` folder, and the model gets its path.
* **A PDF** dropped on the line (or its path pasted) is attached as `[PDF #n report.pdf]`; on a `/command` line it stays its path. Its text is read in the background with your browser; sending waits for it under *Reading the attached PDF*, and Esc there keeps the draft.
  * The Anthropic and OpenAI APIs get the PDF itself (Anthropic reads 100 pages at most; you're warned about a longer one). Other servers get its text, its first 100 pages: a scan or a locked PDF is refused there, and a text longer than half the model's context window is cut, the rest left to `read_pdf`.
  * The line's pictures and PDFs together may total at most *LLM max images (MB)*. The model gets the PDF's path in the sent text, and a saved session keeps its text, not the file.
* **On a Mac** Cmd+V pastes text only; use Ctrl+V, Option+V (with Option as Meta) or a right-click on the input line. A picture or PDF file copied in Finder is attached by name.

### Completion lists

A list opens over the input line as you type `/` at the start, a command's argument, or `@`, `#`, `$`, `%`, `^` or `*` before a word.

* **Keys:** ↑/↓ move the highlight, Enter or Tab applies it, Esc closes the list (it stays closed for that word).
* **Mouse:** a click highlights a row, a double-click applies it. The wheel over the list moves the highlight; elsewhere it scrolls the transcript. A click on the `▲▼` row pages.
* A right-click on the list does nothing (no paste), and a click off the list leaves it open.

## Panes

* **Keys:** ←/→ switch tabs, ↑/↓ move, Enter edits or toggles, Space flips an on/off row, Esc closes or goes back a page.
* **Second row of tabs** (`/settings`, `/tools`, `/skills`' Offered, `/mcp`'s Tools): ←/→ step through each sub-tab and on into the next tab; Tab and Shift+Tab jump a whole tab.
* **Under the list:** the row's description and default. A number after a tab's name (`Client³`) counts its settings changed from the default.
* **Dim tabs** (`/tools`): a menu whose switch is off has its name dim and italic on the second row of tabs; a group's name too when every menu in it is off.
* **Height:** a pane keeps its height while it opens a value picker, a yes/no, a folder browse or details, so its title stays put; a taller details page gives the height back when it closes.
* **Type to filter** on `/sessions`, `/server`, `/model`, `/docker`, the saved lists, the allowed commands and the Offered tabs; the first Esc clears it. Where a pane has lettered buttons, a letter presses its button and Shift+letter starts a filter. A paste (Ctrl+V or a right-click) adds its first line, up to 40 characters.
* **Find** on the read-only panes (`/help`, `/sys`, `/about`, `/tree`, `/docker logs`…): type to mark every match; Enter or F3 goes to the next, Shift+Enter or Shift+F3 back (Shift+F3 on a Mac). Where the pane has lettered buttons (`/webui`, an `/ad` card, `/help`'s key check), a letter presses its button and Shift+letter starts the find. A paste (Ctrl+V or a right-click) adds its first line, up to 40 characters.
* **Asking first:** removing something asks first, with No highlighted; `y` or `n` answers.
* **Mouse:** a click moves the cursor, a double-click picks a row or tab. A click on the `▲▼` row under a long list pages up (`▲`) or down (`▼`). On the input line a double-click selects a word, on the `›` the whole draft. The × at the top right is Esc; a double-click outside a pane closes it.
* **On a Mac**, to select terminal text with the mouse, hold Fn (Terminal.app) or Option (iTerm2), or toggle the app's mouse with Cmd+R in Terminal.app.

### Pane keys

In every list ↑/↓ move, Enter or a double-click picks, Esc closes; key letters act at once. On a list whose rows can be removed, Delete removes the highlighted one as that list's own remove does, asking where it asks (the Docker server match words, the Network host lists and the *Advisor list* too).

| Pane | Keys |
|---|---|
| **Conversation** | |
| `/memory` | Enter: edit, disable/enable, forget (asks) · Delete forgets (asks) · `a` add · `m` *Memory mode* |
| `/queue` | Enter or Delete removes (asks) · `c` clear all · `s` send (while held, or under *Queue mode* `manual`) · `m` *Queue mode* · `k` *Queue cancel mode* |
| `/rewind` | Enter rewinds to before the message |
| `/sessions` | Enter opens · type to filter |
| `/plan open` | Enter opens (with `--chat`, to talk it through) · `o` open · `c` talk it through · `l` run as a [Ralph loop](COMMANDS.md#ralph-loop), outside plan mode · type to filter, Shift+letter to start one with a button's letter |
| The plan approval pane | `a` approve & run · `f` clear context & run · `l` Ralph loop · `r` keep refining · `g` close gaps · `c` cancel plan · Esc keeps refining |
| A Ralph loop's checkpoint | `k` keep · `r` redo with a note (typed below) · `v` view the changes · `s` stop the loop · Esc keeps |
| `/find` | type to find · a paste or right-click adds its first line · Enter or F3 older · Shift+Enter or Shift+F3 newer · PgUp/PgDn scroll · Esc or a double-click outside done |
| Info panes (`/help`, `/sys`, `/about`, `/tree`…) | ↑/↓, PgUp/PgDn scroll · ←/→ or Tab switch tabs · type to find (Shift+letter where the pane has lettered buttons) · Esc closes |
| **Saved lists** | |
| `/eventlog` | ←/→ logs · Enter opens the event (`c` copy, `a` ask, Esc back to the list) · `r` refresh · `m` more · `c` computer · type to filter, Shift+letter to start one with a button's letter |
| `/services` | ←/→ states · Enter opens the service (`c` copy, `a` ask, and on a readwrite computer `s` start, `x` stop, `r` restart, `t` start type, each asking) · `r` refresh · `c` computer · type to filter |
| `/tasks` | ←/→ states · Enter opens the task (`c` copy, `a` ask, `h` history, and as allowed `r` run, `e` end, `n` enable, `d` disable, each asking) · `r` refresh · `c` computer · `f` folder · `w` Windows' own · type to filter |
| `/ad` card | Columns under a dim header row, the long ones cut to the pane · a double-click on a row (a member, a result, a group, the manager or owner) opens that object's card, Esc goes back to the one before · `c` copy (the whole card, nothing cut) · `a` ask · on a group, `n` nested or direct, `u` users only or all · on a locked user, **Search 4740** opens the PDC emulator's lockout events on `/eventlog` · a name several accounts share opens as a card of them · Shift+letter starts a find |
| `/youtube list` | Enter plays |
| `/youtube <words>`, `/youtube playlist` | Enter plays · `s` saves the video |
| `/prompts` | Enter sends the prompt (queued while a reply runs) · `d` puts it on the draft line · `a` add · `s` save the draft · `e` edit (a long one in the draft editor) · `r` or Delete removes (asks) · `c` clears all (asks) · Shift+↑/↓, Shift+Home/End move · type to filter |
| `/browser list` | Enter opens in the [Browser window](#the-browser-window) · `a` add · `n` name · `e` address |
| `/youtube list`, `/browser list` | `d` or Delete removes (asks) · `c` clears all (asks) · Shift+↑/↓, Shift+Home/End move · type to filter |
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

`/eventlog` shows a computer's events newest first, a tab per log (Application, System, Security, Setup, and the one you named). Each row is the time, a level icon (◆ critical, ● error, ▲ warning, ○ information; ✗ and ✓ for Security's audit failures and successes), the provider, the id and the message's first line; a dim line marks each new day. A tab reads "(no access)" once Windows refused it. The title row's `⟳ refresh`, `more` (lit while there is a next page) and `computer` (with more than one ticked) are buttons. An event's page shows everything Windows recorded, its XML too, as plain text: nothing an event holds is read as markup or a terminal code.

On a Mac the title is `🪵 Event logs · this Mac` and the tabs are `system`, `crashes` and `install` (and a `.logarchive` you named). A row's icon is ◆ fault, ● error or a crash, ▲ a jetsam, ○ default and the other reports, · the rest; there is no `computer` button.

### The services and tasks panes

`/services` lists a computer's services a row each (name, display name, state, a failure's exit code), a tab per state. `/tasks` lists its scheduled tasks (path, state, last run and result, next run), Windows' own under `\Microsoft\` hidden until `w`. Enter opens one whole, with the actions its computer allows as buttons; each asks you first, and a refusal (protected, a driver, privileged, not in `control`) says why. Times are the computer's own clock.

### Folder picker

`/cwd browse` and the *Working directory (cwd)* and *Obsidian vault* rows open a folder tree. `⌂ profile` (the profile's `files` folder) and `▣ images` sit above the drives. Space, → and ← open and close folders (so does a click on a folder's icon), `-` collapses all, and Enter or a double-click on a name chooses.

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
| Open their pane over the reply | `/help`, `/settings`, `/tools`, `/mcp`, `/sys`, `/usage`, `/about`, `/memory`, `/queue`, `/sessions`, `/sessions title`, `/rename`, `/skills`, `/reasoning`, `/sampling`, `/msgclear`, `/tree`, `/vault`, `/browse`, `/docker`, `/youtube`, `/shopping`, `/prompts`, `/browser list`, `/camera list`, `/ha states`, `/keycheck`, `/process`, `/sys persona`, `/sys operata`, `/sys vocalia`, `/toolbar edit`, `/perfbar edit`, `/homebar edit`, a home bar chip's pane, `/webui` |
| Run at once | `/mark`, `/webui copy`, `/webui browser`, `/ha`, `/camera live`, `/camera watch`, `/camera off`, `/camera use`, `/camera view`, `/camera thumbs`, `/screen view`, `/screen thumbs`, `/tts`, `/stt`, `/stt wake`, `/stt interrupt`, `/perfbar`, `/homebar`, `/homebar on`, `/homebar off`, `/toolbar`, `/header`, `/reasoning <level>`, `/sampling <field> <value>`, `/queue clear`, `/queue mode <word>`, `/queue cancel <word>`, `/copy`, `/remember`, `/explore`, `/open`, `/browser <url>`, `/prompts <n>`, `/terminal`, `/shortcut`, `/log`, `/process <id>`, `/timer`, `/expand`, `/collapse`, `/window`, `/cwd`, `/comfy view`, `/comfy thumbs`, `/view <path>`, `/view <path> --thumbs` |
| Stop the reply first | `/clear`, `/new`, `/splash`, `/rewind`, `/exit` |
| Everything else | Waits for the reply, queued behind earlier messages (*Queue cancel mode* applies) |

In a pane over a reply, rows that would disturb it (a reconnect, the profile, the working directory, the theme, a session, a skill's page, an MCP server) answer *(not while a reply runs)* until the reply ends.

## The hints bar

The keys that apply right now, on a row of their own over the status bar (*Hints bar*). It shows one hint at a time, the first of:

1. The kill switch's warning (Ctrl+Alt+X, press again to unload).
2. A waiting second press: Ctrl+C to exit, Esc to rewind, Delete for a splash picture.
3. An open pane's keys.
4. What the arrows do right now: the splash's pages, the picture strip, `/read`'s keys.

* **auto** (the default): the bar takes a row when a hint comes and keeps it, blank, until you send, a reply ends, a pane opens or closes, the window is resized, or 10 seconds pass with no hint and no key. Typing never moves the input line.
* **always** keeps the row; **off** puts the hints back at the end of the status bar's left group.
* During a reply it shows only a pane's keys or the kill switch's warning; **Esc to stop** stays on the status bar.
* A double-click on it opens `/settings`. It moves with the other bars in *Toolbar order*.

## The status bar

The row under the hints bar (*Status bar*). [Bar checklists](#bar-checklists) moves and pins its sections:

* **Left by default:** **Status** (🔊 🎤 👂 ✋, 🧠, 🏷️…), **Spinner** (during a reply, with its clock and **Esc to stop**; always on), **Queued**, **Timers** (⏰ countdowns, ⏱️ stopwatches counting up), **Reading** (`/read`, `/echo`) and **Usage** (context and tokens).
* **Right by default:** **Model** (name and reasoning icon) and **Working directory** (off by default).
* During a reply the busy row keeps the same sides.
* **Narrow window:** the model's name and the path shrink, then go; then the right group gives way, then the left. The spinner goes last.
* **Background setups** (an embedded model's download, MCP servers connecting, the voice models' first load) show 📥, 🔌, 🎙️ or 🔈 while the input line stays yours.
* **A Ralph loop** leads the Status section with 🔁, its step and its pass (`🔁 step 2/5 · pass 3`) while it runs, and the hints bar says Esc stops it and typing pauses it.
* **A code audit** leads the Status section with 🔎 and its pass (`🔎 2/5`, or `🔎 2` with no pass limit) while its passes run; plan mode's 📝 leads it while planning. When the audit ends on its own, its open findings print as a short table (most severe first, at most 40) and the done pane opens on **Keep report**: `p` Fix via plan (only with a finding open), `g` More passes…, `k` Keep report, `d` Discard report; Esc keeps it. See [Code audit](COMMANDS.md#code-audit).

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

Icons under the status bar (*Tool bar*), each shown while it is checked there. A click says what an icon is; a double-click or a right-click opens it. A tool group that is off shows on a dark background.

| Toolbar item | Shown | Opens |
|---|---|---|
| ⚙️ 🪪 🧮 🔌 🎭 💬 📊 | always | `/settings`, `/profile`, `/theme`, `/mcp`, `/sys`, `/sessions`, `/usage` |
| 🛠️ | dark background while no tool is offered (every switch off and every tool unticked on `/tools`' Offered tab, or *LLM offer tools* off) | `/tools` |
| 🎓 | dark background while *Agent skills* is off | `/skills` |
| 📱 | dark background while *Web UI* is off | `/webui` (after 🌐; on by default) |
| 💾 | dark background while *Memory mode* is `off` | `/memory` |
| 📣 | always | `/prompts` |
| ⏳ | dark background while *Queue mode* is `off` | `/queue` |
| 🙋 | dark background while *Ask user* is off | `/tools ask` |
| 🔒 / 🔓 | *Shell command policy* `ask` / `yolo` (none under `off`) | `/tools shell allowed` |
| 👮 / 🥷 | *Shell police* on / off (none while the policy is `off`) | `/tools shell police` |
| 🐚 | dark background under `off` | `/tools shell` |
| 📁 🌄 🌐 🦉 🐳 💎 🛢️ 🔮 🐬 🪶 🐘 🔗 🏠 🎨 📸 🖨️ | dark background while off | `/tools files`, `image`, `web`, `advisors`, `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `sqlite`, `postgres`, `unc`, `ha`, `comfy`, `camera`, `print` |
| 🪵 | dark background while off, and always where there are no event logs to read | `/tools eventlog` |
| 📡 | dark background while off | `/tools network` |
| 🧩 📅 | dark background while off, and always off Windows | `/tools services`, `tasks` |
| 🏢 | dark background while off, and always on a system other than Windows or macOS | `/tools directory` |
| 📄 ⚡ | always | the [log window](#the-log-window); `/process` |
| 🎞️ 🪟 | always | the [picture viewer](#the-picture-viewer); the [thumbnail browser](#the-thumbnail-browser) (ComfyUI's folder) |
| 📺 ⭐ 🛒 | always | `/youtube list`, `/browser list`, `/shopping` |
| the working directory (`~` for your user folder) | pinned right | `/cwd browse` |
| **Model name** and its icon | pinned right, with a server connected | `/server`; the icon `/reasoning` |
| blank space | — | `/settings` |

* **Dividers** (`│`) show only between two visible items ([Bar checklists](#bar-checklists)).
* **Narrow window:** the icons come first; the model name and the path shrink, then go; then items drop from the right group, then the left.

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
* **Live:** while shown, one WebSocket watches the bar's devices; once the bar has been hidden for 15 seconds, it closes. A dropped one retries after 1, 2, 4… up to 30 seconds; a refused key isn't retried until the URL or key changes.
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
* **Tool bar dividers:** `+` (*+ divider* on the title row) puts a dim `│` after the item under the cursor. It moves like an item, Delete takes it out, and Enter or Space does nothing on it. It shows only between two visible items of its side. The default has three: `⚙️ 🛠️ 🎓 │ 💾 📣 ⏳ │ 🔒 👮 🐚 │ 📁 🌄 🌐 📱`.

*Toolbar order* orders the five bars the same way: Shift+↑/↓ (or Alt+↑/↓) moves the bar under the cursor, Shift+Home/End to the top or bottom, `d` puts the default back, and the screen redraws at once. A bar that is off keeps its place.

## The avatar picker

* Enter on *Model avatar* (or `/avatar`) opens a grid of every emoji Windows Terminal draws (Unicode Emoji 15.1, no flags). *User avatar* opens it for your own lines.
* **Tabs:** **⭐ For you** (matching the profile's name and persona), **🕘 Recent**, then the Unicode groups. Tab and Shift+Tab step them; the arrows move; a double-click picks.
* Type to search (`fox`, `red heart`) or paste an emoji; Esc clears. 🎲 picks at random; 🚫 clears the avatar.
* Enter on a hand or a person opens its six skin tones. Other profiles' avatars are marked.
* Recent picks and the last skin tone are kept per profile, in `emoji.json` in the profile's `configs`.

## The welcome splash

* *Welcome splash* `fullsize`: one picture; ←/→ step through them. Delete twice on an empty line moves one of the profile's pictures to the folder's `.trash`.
* *Welcome splash* `tiled`: thumbnails at *Image thumbnail size*; ←/→ page, a double-click opens one.
* Pictures in the profile's `images\splash_images\` replace the built-in ones. `/splash` shows the splash whatever *Welcome splash* says.
* On the Web UI the splash stands first in the transcript, centered and filling the page, and goes at the first message sent, from either side, as at the PC.

## The app's windows

What every window shares:

* **Focus:** opened by its key chord, a window stays behind the terminal; opened by a typed command, it comes to the front. However it closes (Esc, Alt+F4, ×, its chord), the terminal gets the keyboard back.
* **Keys:** Tab brings the terminal forward, and a Ctrl or Alt chord the window doesn't use runs in the chat. The Browser window keeps every key for its pages (on a Mac too: Tab and Control chords are the page's); on Windows the video window keeps Tab.
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
| Del twice within 2 s | Deletes the picture: to the Recycle bin under *File deletes to Recycle bin* (on by default), else for good |
| F9 · ↑ / ↓ · F10 | Slide show on or off (5 s) · a second more or less · folder or random order |
| Esc | Stops the slide show, then leaves full screen, then closes |

* The viewer, the thumbnail browser and the picture strip follow each other's selection.
* **On a Mac** ⌫ is Del, a two-finger swipe steps, Control-click is a right-click.

### The picture menu

A right-click in the viewer or the thumbnail browser.

* **Edits:** rotate and flip, color (grayscale, sepia, negative, polaroid), resize, convert (PNG, JPEG, GIF, BMP), shrink the file under a size, strip metadata losslessly. *Image edit mode* decides whether an edit writes `photo-edited.png` or replaces the picture.
* **The file:** copy its path, show it in Explorer (Finder on a Mac), attach it, print it, or delete it without asking (to the Recycle bin under *File deletes to Recycle bin*, else for good).

### The thumbnail browser

`/view <folder> --thumbs`, `/comfy thumbs` (🪟), `/camera thumbs` and `/screen thumbs` show a folder's pictures, oldest first, new ones added at the end.

| Key | Action |
|---|---|
| Click / double-click or Enter | Select (the viewer follows) / open in the viewer |
| Arrows, PgUp / PgDn, Home / End, the wheel | Move, scroll |
| + / − or Ctrl+wheel | Bigger / smaller thumbnails |
| F5 | Re-read the folder and fit the thumbnails |
| Del twice within 2 s | Deletes the picture: to the Recycle bin under *File deletes to Recycle bin* (on by default), else for good |
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

Where YouTube videos play under *YouTube open links in* `youtube-window`, the default ([TOOLS.md](TOOLS.md#youtube)). A video or playlist link clicked in it (the YouTube logo, an end screen) plays where the setting says; anything else opens in your default browser. It doesn't take the keyboard; F11 is full screen, Esc leaves it and then closes the window. YouTube's own keys (Space or K, ←/→, F, M) work once you click into the video.

**On a Mac** it needs macOS 14 or later (WebKit). The player's own full-screen button fills a Space of its own; Esc brings it back.

### The Browser window

A browser window (Windows, and a Mac on macOS 14 or later, where it is WebKit: [The Browser window on a Mac](MAC.md#the-browser-window-on-a-mac)): the web tools open it for bot checks ([TOOLS.md](TOOLS.md#the-browser-window)), which a connected Web UI page can also see and drive ([A bot check from the page](#a-bot-check-from-the-page)), and `/browser [url]` opens it for you. When you close it, its cookies go to the web tools. A bare `/browser` opens *Web browser home page*.

* **The bar:** back, forward, reload (✕ stops a load), home, a padlock (https) or a warning (http), the address box, Go, and open in your default browser. Words that aren't an address search with *Web search method*; `file:`, `javascript:` and local paths are refused.
* **Keys:** Ctrl+W closes; F5 or Ctrl+R reloads; Alt+← / Alt+→ go back and forward; Ctrl+L, Alt+D or F6 go to the address box (Esc there restores it); Ctrl+H opens the history; Ctrl+D saves or unsaves the page; Ctrl+Shift+O opens the saved links; Esc stops a load. Esc never closes the window. On a Mac, Safari's: ⌘W, ⌘R, ⌘[ / ⌘] (or a mouse's side buttons), ⌘L, ⌘⇧H home, ⌘Y, ⌘D, ⌘⇧O or ⌘⌥B, ⌘. or Esc stops, and ⌘F finds (⌘G, ⌘⇧G), ⌘+ ⌘- ⌘0 zoom, ⌘P prints.
* **Full screen:** F11 (⌃⌘F on a Mac), with the bar kept. A page's own full screen (a video's button) takes the window and hides the bar (on a Mac it fills a Space of its own, as in Safari). Esc leaves the page's first, then F11's.
* **History** (🕘, Ctrl+H): newest first, older by day, with **Clear history**. *Web browser privacy* `clear-history` clears it at each close; `keep-history` keeps it in `<profile>\configs\browser-history.json`. Cookies and sign-ins stay either way.
* **Saved links** (☆, ★ on a saved page): the first 15 on the menu, the rest in submenus, up to 190; `/browser list` (⭐) has them all. They're kept in `<profile>\configs\browser-links.json`. An address holding a user name or password is never saved. The model reaches them with `web_links` and `web_save_link` ([TOOLS.md](TOOLS.md#web)).
* **Per profile:** sign-ins and solved checks live in `<profile>\webview2-browser` (on a Mac, in a WebKit store the profile's `webkit-browser.id` names, removed by `/profile delete`). Switching profiles closes the window. Two copies of the app can each open it, on different profiles.
* **Clearing:** `/browser clear history` clears the history now. `/browser clear all` asks, then clears the history, cookies, sign-ins, cache and site settings; saved links stay.
* **A Mac's extras:** a status line at the foot shows a hovered link's address; page dialogs, HTTP sign-in and upload pickers are sheets; downloads go to ~/Downloads with a button on the bar for them; a page that fails to load shows the window's own error page, which Reload retries.
* **A PDF:** `/view report.pdf` shows a PDF from the working directory in the window's own PDF viewer. It shows a copy, made in `%TEMP%\NeonSidekick\pdfview` and deleted when the window closes; the open button opens the file itself in its own program. A PDF view isn't kept in the history, can't be saved as a link, and hands no cookies over. Where the window can't take it (a bot check waiting, no WebView2 runtime), the PDF opens in its own program; a policy that makes Edge download PDFs makes the window download it.

## Web UI

A browser front end the app hosts for the loaded profile: the chat as it happens, a message line of its own, the questions the model's tools ask, the house, and the shopping list. Turn it on with `/webui on` or *Web UI* (`/settings` › Client › Web UI). Not headless.

* **Signing in:** `/webui` shows a tab per address of this machine, each with a QR code and its sign-in link. This machine's Tailscale address (100.64/10, on Tailscale's own adapter) comes first, its tab headed Tailscale (see Away from home), since its link works at home and away; then the home network's: the Ethernet and Wi-Fi adapters with a gateway, the fastest first (`localhost` under `local`). Other VPN tunnels and WSL's virtual switch aren't listed, though a browser through them still connects with the address typed; with no home adapter, every private address is listed. Scan the code with the phone's camera, or open the link in a browser on the same network. The link signs the browser in and leaves the address bar at once; the browser keeps a cookie for a year. `/webui copy` puts the first tab's link (Tailscale's when there is one) on the clipboard.
* **Away from home:** install Tailscale on this PC and the phone and sign both in to the same tailnet; scan the Tailscale tab's code once (the browser treats it as a different site, so it keeps a second cookie beside the home network's). The phone then reaches the Web UI wherever it is, with Tailscale on. Nothing to open on the router; *Web UI reach* must be `lan`. Windows' firewall treats Tailscale's network as it treats the home one: if Windows calls it Public, the phone can't connect (see The firewall).
* **The token:** each profile has its own, kept encrypted. `/webui reset` makes a new one and signs every browser out, and so does changing the port (the cookie is named by the port). A link sent through a messaging service hands that service the token too; run `/webui reset` afterwards. A profile folder copied from another machine makes a new token.
* **The tabs:** Chat, Home, Shopping and YouTube, each only while its controls are on (`/settings` › Client › Web UI): *Web UI chat controls* (on by default), *Web UI home controls*, *Web UI shopping controls* and *Web UI YouTube controls* (all three off by default, so after an upgrade the Shopping tab is gone until it's turned on). Chat stays a link in the header; Home, Shopping and YouTube sit behind ☰, which reads the open tab's name there (🏠 Home ▾). Its menu lists only the tabs that are on, and ☰ goes when none of the three is on. A pick, a tap outside, Esc or a scroll closes it. With Chat off the page opens on Home, else Shopping, else YouTube; with none on it says so, names the four settings, and opens a tab by itself once one is turned on. A tab turned off while it's open goes to one that's on.
* **Chat off:** the phone has no transcript, no message line, and can't answer the PC's questions (they're the Chat tab's); they open on the PC as always. The Chat badge goes with it. The PC is unchanged.
* **The pages:** in one fixed dark style. The page's own draft line stays in the browser; it never shows what you type at the PC, nor the PC what you type there. Sent messages share one history, though: a line sent from the page joins the profile's message history as you send it, so ↑ brings it back at the PC too, and ↑/↓ on the page step through the history as at the PC (on a draft of several rows, from its first or last row). A draft you clear at the PC with Esc never reaches the page. Enter sends on a desktop; on a phone it adds a line (tap ➤ to send). Stop (■) shows while a reply runs. A ✕ inside the message box while it holds text clears it; *Undo* under it puts the text back, unless you've typed since, and ↑ brings it back too.
* **Prompts, history and memory:** 📣 at the message box's left opens a sheet with three tabs, on the one you used last (`/prompts` sent from the page opens it on Prompts). The button shows that tab's glyph: 📣, 🕘 or 💾. Each tab is read fresh as it opens.
  * **📣 Prompts:** the profile's saved prompts, numbered as at the PC. A tap on one sends it at once, as if typed (queued while a reply runs), and leaves the message line alone; its ✎ puts it on the message line ahead of what's there, with *Undo*. ⇪ saves the message line as a new prompt and leaves it there.
  * **🕘 History:** the message history, newest first. A tap puts a line on the message line, ➤ sends it. *Clear all* empties it, stored and at the PC, as `/msgclear` does.
  * **💾 Memory:** the profile's memories, a turned-off one marked *off*. ⇪ saves the message line as a memory, as `/remember` does; with *Memory mode* off the tab says so and saving is refused.
    A tap on a memory opens its editor: change the text and press Enter or *Save* (Esc or *Cancel* keeps it), or *Turn off* / *Turn on* at once, as `/memory` does at the PC, in any *Memory mode*.
  * **Deleting:** a prompt's or a memory's 🗑️, and *Clear all*, take two taps: the first turns it red for 3 seconds, the second acts. Esc takes the red back first.
  * A filter box appears once a tab has more than eight. ✕, Esc, Back or a tap on the transcript closes the sheet; it, the picture drawer and the usage card replace each other. The PC's hint row flashes what the page saved, edited or deleted.
* **The floating buttons:** faded at the transcript's right edge until touched. ↕ (under the header) opens every fold on the page (thinking, tool runs, diffs), as Ctrl+O does at the PC; it stays on for the tab, so folds that arrive open too, and a second tap closes them all. It shows only while there is something to fold. ⤒ goes to the top and ⤓ to the bottom (above the message box), each hidden at its end. They hide under a full-screen picture and the usage card. The bottom ones ride above a phone's keyboard; check that by hand on a phone, as no test can show it.
* **The picture strip:** 🎞️ (above ⤓) shows while *ComfyUI picture strip* is on and there are ComfyUI pictures; a dot on it says a picture came since you last looked. A tap opens a drawer over the status row: the pictures newest first, the one the PC's strip has highlighted outlined (the drawer opens scrolled to it, and scrolls to it as the PC's pick moves). A tap on one opens it full screen (swipe through the strip's pictures, the newest first) and moves the PC's highlight and its open picture viewer and thumbnail browser to it, as a click on the PC's strip does; a fast swipe moves them once. On the newest it's *live*: a new picture replaces it, with no pick sent to the PC (its viewer, if live, follows on its own). Nothing opens on the PC. ✕, Back, Esc or a tap on the transcript closes the drawer; a question arriving or the usage card closes it too. The PC's × and the page's ✕ each close only their own strip. On a Mac, where the app makes no thumbnails, the drawer shows the original pictures scaled down.
* **The status row:** one slim line over the message box. While the PC works it shows the PC's spinner as the busy row does: the stage (*thinking*, *writing*, a tool's name, a fun verb under *LLM fun verbs*, a model's load, a command's own label) and its clock, with a tool's own clock first while one runs. On the right, the usage the Status bar shows: the tokens in use, the share of the window, the speed (marked `~` while estimated under *LLM mid-turn usage*). At rest it shows the usage alone, and nothing before the first reply. A narrow phone drops the token count first, then the speed. A tap on the usage opens the full `/usage` figures in a card on the page: the context as a bar (amber past 80%, red past 95%) with where the window's size came from, then the last reply, this conversation and since launch side by side (stacked on a phone). The card follows a reply while it's open; ✕ or Esc closes it. `/usage` typed on the page stays refused.
* **A reflection running:** 🧠 leads the status row while a reflection runs, as on the PC's strip. Tap it twice to cancel the reflection, as the PC's double-click or Ctrl+L does; `(🧠 learning canceled)` follows in both transcripts.
* **What the page shows:** your lines and the replies (Markdown, the links opened in a new tab), the pictures, the tool lines and the first line of their results, the notices, a divider for `/new`. It starts afresh when the screen does (`/clear`, a rewind, a resume, a theme change, a profile switch). Not shown: the panes you open at the PC.
* **As the terminal shows it:** the model's thinking streams into a short box under *💭 thinking…* and folds to *💭 thought for 4.2s — …* when the answer starts (a tap opens it), with *LLM show thinking* on. An edit's diff shows under its tool line, its added and removed rows on green and red, cut at *Diff max lines* and folded past *Diff collapse count*. A run of tool lines past *Tool collapse count* shows its last few while it runs and folds under its summary (`🛠️ 6 tool calls · 1 failed — read_file ×4…`) when it ends; a tap opens it. A `/cmd` run's output shows in monospace, its error output in the warning color. A reply that said nothing reads *(no reply)*. Text after a tool line reads on without the reply's icon once the reply has spoken, as at the PC. A fold the page makes on its own keeps your reading place. Long code blocks are never folded on the page.
* **Pictures:** every picture the chat holds shows on the page as a thumbnail, even with *Show image thumbnails* off or one the terminal can't draw: `generate_image`'s and `/imagine`'s, a pasted or dropped one, a restored session's, the splash. The model's own `camera_capture` and `screen_capture` pictures show nowhere, as at the PC. A tap opens it full screen, newest first as the picture strip and the PC's viewer: swipe or ‹ (newer) › (older) for the others, Back, ✕, Esc or a tap closes it, and *Open original* opens the file itself in a new tab. On the newest picture (the far left) it's *live*, as the PC's viewer: a new picture replaces it as it arrives. Step back to an older one and it holds there; step onto the newest again and it follows again.
  * **It follows the PC:** a picture you move to at the PC (a click or an arrow on its picture strip, a step in its picture viewer, a pick in its thumbnail browser, or opening one in the viewer) moves an open full view to the same file, whether opened from the transcript or the drawer. Moved to an older picture it holds there; moved to the newest it's live again. The page's own taps, a slide show and a closed full view move nothing.
  * **🗑️ deletes the picture:** tap it twice (the first tap turns it red for 3 seconds; Delete twice on a desktop). It follows *File deletes to Recycle bin*, as the PC viewer's Del does: to the Recycle bin (the Trash on a Mac), or for good. The view moves to the next older picture, the PC's strip drops it and its hint row says so, and the log names the picture. A step, a swipe, a new picture or a move from the PC disarms it; Esc disarms it before it closes the view. A picture with no file of its own (pasted, a camera watch frame) has no 🗑️. A HEIC, TIFF or JXL original is sent as a JPEG. A file over 20 MB shows its name and opens as the original. A picture is served only while the page's transcript holds it: after `/clear` or a rewind its address answers nothing. A picture whose file is gone says so under its name.
* **Messages from the page:** sent from the idle line, after anything queued at the PC. During a reply they wait (counted in the status bar's Queued, not listed in `/queue`), and they're sent as the reply ends even under *Queue mode* `manual`. A held queue (*Queue cancel mode* `hold`) holds the ones that waited behind the canceled reply, with a banner over them and **Resume** to send them; a message you send after the cancel is your next message and runs at once, as one typed at the PC does, and the held ones follow when its reply ends. `/clear`, `/new`, a resume, a rewind, a profile switch or *Queue cancel mode* `empty` drops them and puts the text back on the page; `/queue clear` leaves them. Slash commands run only with *Web UI commands* on (see Commands from the page). At most 20 wait at once; a message holds up to 48 KB.
* **Stop on the page:** ends a reply, a compact, a botchat, an advisor, `/imagine`, `/test` or `/loop`'s wait and each pass's reply, and stops speech. Before the reply's first word it takes the message back to the page, as Esc does at the PC. It never stops a model loading (Ctrl+C at the PC) or your editor under `/draft`.
* **Questions:** a command's approval, `ask_user`, an allow (the camera, the screen, a database write), a yes/no (Docker, Home Assistant, a print, the shopping list's clear, an advisor), the plan's approval, a code audit's done pane and a reflection's skill show at the PC and on the page at once, in the PC's own words; the first answer wins, and the PC says when the page answered. The page shows a command or a statement whole. A reflection's skill shows its summary with **Keep** and **Discard**, the whole proposal folded under **View** below them. The camera's shutter (*Camera shutter* `user`) offers **Take and send** or **Cancel** on the page, which takes the photo with the PC's camera and no preview. Not on the page: Close gaps (the plan's) and a code audit's reviewer page (More passes takes the last run's reviewer). While the PC waits on that, the page says "waiting for an answer at the PC".
* **Shopping:** the list in the active store's aisle order, as the model reads it; a tap ticks or unticks a line. With two or more stores a *Store* picker sits over the list: a pick makes that store the active one, as `/shopping store <name>` does, and the list is reordered for it at once. The page re-reads the list (and the store) every five seconds while it's open.
  * **Adding:** the *Add items* box over the list takes what `/shopping add` does (commas between items, a quantity before or after: `2 milk, eggs`); Enter or ⊕ adds them, at most 100 at once. 🕘 opens the past items, what was bought before and isn't on the list now (the PC's add page): a tap adds one again at its last quantity, and 🗑️ forgets it. With more than eight, a filter box sits over them.
  * **A line's sheet:** the › at a row's end, or a long-press on the row, opens it: − and + change the quantity (posted once the taps stop), a rename box with Save (a name another line has merges the two, as at the PC), and 🗑️ removes the line. Back, ✕, Esc or a tap outside closes it.
  * **The whole list:** ☑ all ticks every line (☐ none unticks them once all are ticked), and ⊠ clear ticked takes the ticked lines off (they're remembered as bought). Remove, forget and clear take two taps (the first turns the button red for 3 seconds).
  * **At the PC only:** placing a line, the stores beyond the picker (adding, renaming, removing), the layout editor, a store's folder and its reindex, and clearing the whole list.
  * **The PC follows:** every change made on the page flashes on the PC's hint row (📱), and the PC's open `/shopping` pane follows it within a second.
* **The dropdown:** as at the PC, typing completes the `@ # $ % ^ *` mentions and, with *Web UI commands* on, the commands and their words. Only the commands and words the page may run are offered. ↑/↓ move, Tab or Enter take one, Esc closes it, or tap a row; Enter sends only while it's closed. `@` and the path words list the working directory only while *File tools* is on. The `%` and `*` connections show by name; *Web UI connection details* adds their server and database.
* **A new version:** after an upgrade the page reloads itself once, its draft kept, so a phone left open runs the new page. A page opened before this version doesn't; reload it by hand once.
* **The Chat badge:** on Home, Shopping and YouTube, the Chat link shows a dot while a reply runs and a count while a question waits for you (or the PC waits on an answer), and the tab's title shows `(n)`. An Android phone vibrates once on a new question; an iPhone can't (Safari has no vibration), so the badge and the title carry it there.
* **The PC sees the phone:** a line added, ticked, changed or removed, a past item forgotten, a store, a tile, a room's switch, a device's row, a scene, a question for Assist, the video remote, a YouTube tab's Play here or its Resume, a saved video played, moved, removed or saved, the saved list cleared, or a reflection canceled flashes on the PC's hint row for a moment (📱 Kitchen light: toggle), even over a reply's spinner. Nothing goes in the transcript.
* **A bot check:** the allow question, then a card for the check itself. See [A bot check from the page](#a-bot-check-from-the-page).
* **Too fast:** a bot check card's taps, holds, scrolls and typing (10 a second, bursts of 30); taps (5 a second, bursts of 15; the sheet's saves, deletes and *Clear all*, and the saved videos' plays, moves, removes, saves and *Clear all* count as taps), Ask the house (one every 2 seconds), pasted sign-ins (5 a minute) and the YouTube tab's player reports and in-view notes (10 a second, bursts of 30) are counted per address; past that the page says to slow down and doesn't try again by itself.
* **A reconnect:** a finished thinking block comes back as its summary alone, and a folded diff of more than 200 rows as its fold; either is read when you open it, or says it's no longer available if the chat let it go.
* **Add to Home Screen:** the page has a manifest and icons, so a phone installs it as an app that opens full screen on Chat. An app added from Safari keeps cookies of its own and opens no link: paste the sign-in link (or its token) on its sign-in page; the page reloads signed in where it was. No notifications: they need HTTPS, and the page is plain HTTP.
* **Reduced motion:** with the phone's reduce-motion on, the spinner stands still (⏳), and nothing pulses, shakes or slides. The status row, the tiles, the sheets and the remote's buttons carry labels for a screen reader.
* **The theme:** with *Web UI profile theme* on (the default), the pages take the profile's theme (its accents, tones and, where its ink reads on it, its grounds) and follow a `/theme` at once with no reload. Off, they keep their own look.
* **The firewall:** Windows asks to let the app through the first time it serves the network; allow it on private networks. On a network Windows calls Public the phone can't connect. A Mac asks too.
* **Who may connect:** under `lan`, a browser at a private address (the home network, and Tailscale's 100.64/10) with the cookie. Private here doesn't mean safe (a router's NAT loopback, Docker or Tailscale can present one): the token is the gate. The links are IPv4 addresses only. The page is plain HTTP, so a browser won't send notifications or reach the clipboard; a waiting question shows as a count in the tab's title.
* **The pane's buttons:** `📱 Web UI │ enabled │ lan │ reset │ 192.168.1.217 · 100.78.20.125`. The first three are buttons, ahead of the address tabs: *enabled*/*disabled* (`e`) turns *Web UI* on or off, *lan*/*local* (`l`) switches *Web UI reach*, and *reset* (`r`) makes a new token after a yes, as `/webui reset` does. The server starts, stops or restarts at once and the pane opens again. The pane opens over a reply too, its buttons acting at once: a phone watching that reply drops for a moment and reconnects (or, after a reset, signs in again). With one address (`localhost` under `local`) the pane has one tab and shows no title for it.
* **Copying and opening the link:** *copy* (`c`, after *reset*) puts the shown tab's sign-in link on the clipboard. A double-click on the link opens that address on this PC, as `/webui browser` does, and the pane stays open.
* **Opening it on this PC:** `/webui browser` opens `http://127.0.0.1:<port>` signed in: in the Browser window under *Web open links in* `browser-window` (a one-time link while *Web browser privacy* is `keep-history`, else the usual link), else in your default browser (`--external` always), signed in as *Web browser sign in mode* says. A one-time link is good once, for a minute; a link checker's HEAD request doesn't spend it. The transcript and the log say where it opened, never the link; the Browser window keeps no sign-in link in its history or saved links, never shows one to the model, and never hands the Web UI's cookie to the web tools.
* **The toolbar's 📱** opens the pane (*Tool bar*, on by default, after 🌐). It sits on a dark background while *Web UI* is off.
* **A port that won't open:** another program uses it, or Windows reserves it (`netsh int ipv4 show excludedportrange protocol=tcp` lists the reserved ranges). Pick another in *Web UI port*.
* **Without the pane** (a redirected console), `/webui` prints the links as notices, so the token reaches the scrollback.

### A bot check from the page

When a web tool meets a bot check (a Cloudflare click, a captcha) the challenge runs in the PC's Browser window, because its pass is a cookie only that browser can use ([TOOLS.md](TOOLS.md#the-browser-window)). The Chat tab brings it to you:

* **The allow question** (*Web browser botcheck window* `ask`): Deny, Allow once or Allow for this session, at the PC and on the page at once; the first answer wins.
* **The card:** while the check runs, its card shows the window live (about three pictures a second, nothing sent while it doesn't change) with *Give up*. With *Web UI bot check control* off (on by default), the card has *Give up* alone.
  * **Tap** to click; **touch and hold** past a moment to press and hold (a press-and-hold check), let go to release.
  * 🔍 zooms (fit, 2×, 3×); a zoomed picture pans with a swipe. ▲ ▼ scroll the page; a desktop wheel over the picture scrolls where it points.
  * The text box types into the field you tapped (its Enter types it, nothing more); ⏎ ⌫ ⇥ send Enter, Backspace and Tab.
* **It ends** when the page gets through (the card closes and the tool goes on), on *Give up* or Esc at either side, on the page's Stop, or after 5 minutes.
* **The PC:** the first input from a page flashes on the hint row (📱) and is logged with the phone's address; what you type is never logged. A minimized window is restored, without taking the keyboard, so it draws; a locked PC keeps drawing it. On a Mac the window draws covered or minimized and its input arrives as the page's own (a covered page runs its timers slowly, as Safari does); a locked Mac is untried. A sleeping PC stops: the card says so, and *Give up* still works.

### The Home tab

**Home** (2026-10-15), between Chat and Shopping, is the house and the PC at a glance.

* **The tiles:** the home bar's items in its order, with your names and emoji (`/homebar edit` at the PC chooses them; while the bar is hidden, its last items). The shopping list's 🛒 stays on the Shopping tab. Each tile shows its state live and is colored as on the bar: on in the device's own color, off dim, an alert (an unlocked lock, an open garage, a leak) bold. A divider starts a new group. A tap does what a double-click on the chip does, and the PC's chip shows the same pending mark; a long-press or the › opens its sheet.
* **The sheets:** a room's devices (all on, all off, then each one with its state and its actions: on, off, brightness, play, volume, open, close, warmer, cooler…) or one device's actions. Back, ✕ or a tap outside closes one. Only the home bar's devices, the rooms' (below) and their switches can be read or acted on from the page.
* **Rooms:** a box per room, as `/homebar show <room>` at the PC: every room with a light, switch, fan, toggle, player, cover, lock or thermostat, or a scene; each Hue zone (under its own name); and *No area* for scenes in no room. The home bar's rooms come first and open, the others folded (all open when no room is on the bar). A box you fold or open stays so on that device.
  * **The header:** the room's name, its lights on of all (`3 of 5 on`) and its switch, which works with the box folded: off if any light is on, else on (a zone's switches its group light).
  * **Inside:** *All lights* (the same switch), then a row per device with its state (a light's brightness too), named without its room's name as the scenes are ("Den Lamp" reads *Lamp* in Den), the state under the name on a phone: a switch for a light, switch, fan or toggle; the first action as a button for a player, a cover or a lock (unlocking asks first); nothing for a thermostat. Its › (or a long-press on the row) opens its sheet. Light groups get no row.
  * **Scenes:** under *Scenes*, each button without its room's name ("Den Relax" reads *Relax* in Den). A scene with no room goes under its device's name (a Hue zone).
* **Ask the house:** a line for Home Assistant's Assist (as `/ha say`), its answer under it.
* **Now playing:** the video playing, in the PC's window or the YouTube tab (the card says which), its title, place and sound, with a remote (play or pause, 10 seconds back or on, quieter, louder, mute, and the playlist's previous and next). Only while a video is open.
* **The PC:** CPU, RAM, GPU and VRAM as bars, and the network's rates, even with the performance bar off. **The model:** its name, server and context window, whether it loads or works, and the usage.
* ***Web UI home controls*** (off by default): with it off, there is no Home tab. On, the tab shows and acts: the tiles, the sheets, the rooms' switches and rows, the scenes, Ask the house and the remote. Unlocking, opening a garage, gate or door, and disarming ask on the page first, as the home bar asks at the PC. It covers this tab alone: `/ha` typed in Chat follows *Web UI commands*.
* **While it's open:** the tab keeps its devices' states and the PC's meters live, even with the home bar or the performance bar off; ten seconds after the phone leaves, the app stops watching them. Each action is logged with the phone's address and flashes on the PC's hint row (📱), but never what was asked of the house.

### The YouTube tab

**YouTube** (2026-10-16), last in the header's ☰ menu, plays videos on the phone (or a browser). The model's plays go there while *YouTube open links in* is `webui-window`. It is the app's own player, so `youtube_control`, `youtube_status`, `/youtube pause` and Home's Now playing card all reach it. While the Web UI isn't serving, videos play in the PC's video window instead.

* ***Web UI YouTube controls*** (off by default): with it off, there is no YouTube tab, and `webui-window` plays in the PC's video window. On, the tab shows whatever *YouTube open links in* says: a video you start on the tab plays there, while `youtube_play`, `/youtube` and `open_url` follow the setting. There is no tab where the app has no video window (before macOS 14).

* **The jump:** when a video is played and no YouTube tab is in view (none open, or the phone locked with its tab hidden), it waits for one, and every open Web UI page goes there: Chat at once, Home and Shopping at their next read, a page opened or woken later as it loads. While you're typing, have a question open, or have Chat's 📣 sheet open, the page shows a *▶ A video is waiting — Watch* banner instead. A page that showed the banner for a video never jumps for that one by itself; the banner's link takes you there. A video nobody opens a tab for goes after 10 minutes. A YouTube tab that comes into view plays the waiting video at once: unlock the phone and it starts.
* **Which tab plays:** a play goes to the YouTube tab shown most recently, and while nothing plays the player follows the tab you're looking at. A tab that is playing keeps playing (even hidden, so you can listen with the screen off) until a new play or a *Play here*. The other tabs say *Playing on another page — Play here*.
* **Play here** takes the video with it: the tab you tap carries on from about where it was (3 seconds back), and the other one stops. On a phone it may start muted until you tap the video.
* **Sound:** a phone starts a video only after a tap on the video itself. The tab tries with sound, then muted (*Tap the video's speaker for sound*), and if even that is blocked it says *Tap the video to start*. The hints sit above the video, never over it.
* **Resume:** leaving the tab, locking the phone or a reload stops the video, and the app keeps its place (as for a saved video). Coming back shows *Resume "title" at 3:12*, a few seconds before where it stopped.
* **A reload** is the same page: it keeps playing rights, and shows Resume. A duplicated browser tab is a new page.
* **Links** inside the player (the logo, "Watch on YouTube") open in the phone's own browser.
* **Limits:** on an iPhone *YouTube while speaking* `duck` does nothing, since a web page can't set the phone's volume; `pause` works. A `/youtube` command typed in Chat is refused from the page; ask the model to play instead.
* **Trust:** the tab runs YouTube's player script in the Web UI's own page, so while it's open that script can do what the page can, though it can never read the sign-in cookie. The page lets in only YouTube's player script, its frame, and its thumbnails (`i.ytimg.com`).
* **Saved videos:** under the player, the profile's saved videos (`/youtube list`'s), in your order, each with its thumbnail, title, channel and where it was left (a watched one dimmed). A tap plays it on this tab, resuming 3 seconds before where it was left, whatever *YouTube open links in* says; from a tab that was only waiting, the tap takes the player first. **⋯** opens a video's details and its controls: ▲ ▼ move it a place, ⤒ ⤓ to the top or bottom, 🗑️ removes it (tap twice). **☆ Save the playing video** saves the one playing (★ *Saved* once it is), and 🗑️ *Clear all* removes every one (tap twice). The filter matches a title, a channel or an id, and hides the moves while it is typed. A change made on the PC shows within a few seconds, and the PC's open `/youtube list` follows a change made here within a second. Titles not known yet are looked up while the list is open.

### Commands from the page

With *Web UI commands* on (off by default; `/settings` › Client › Web UI), the page sends slash commands too. They run at the idle line, as typed at the PC, after anything queued there; a command sent under a `/botchat` waits for its end. A question a command asks shows at the PC and on the page at once, and the first answer wins. A command typed at the PC asks at the PC alone, as before. Off, every command is refused, and the reason names the setting.

| Kind | Commands | On the page |
|---|---|---|
| Run | `/new`, `/clear`, `/splash`, `/compact`, `/plan …` (`/plan ralph` too), `/mark <color>`, `/code-audit …`, `/claude`, `/advisor`, `/help <words>`, `/imagine`, `/loop …`, `/learn [<note>]`, `/test …`, `/pdf …`, `/print <file\|reply>`, `/avatar <emoji>\|clear`, `/remember`, `/timer …`, `/rename <name>`, `/tts on\|off`, `/stt on\|off`, `/memory <mode>`, `/cwd`, `/model <id>`, `/reasoning <level>`, `/ha …`, `/docker …`, `/shopping …`, `/prompts list\|save\|remove\|<n>`, `/browser save\|unsave`, `/msgclear` | Their lines; the status row shows its spinner while it runs |
| Pick | `/model`, `/reasoning`, `/rewind [<n>]`, `/plan open [--chat]` | A card with the choices, the one in use marked, a filter for a long list |
| List | `/ha states`, `/docker logs`, `/eventlog …`, `/net …`, `/services …`, `/tasks …`, `/ad …`, `/code-audit open`, `/print`, `/print printers`, `/camera list`, `/screen list`, `/tree` (needs *File tools*), `/vault` (needs *Obsidian tools*), `/process`, `/comfy`, `/comfy offered` | A card with the lines; Close |
| Never | `/exit`, `/webui`, `/cmd`, `/profile`, `/cwd <path>`, `/sessions`, `/skills`, `/youtube`, `/queue`, `/botchat`, `/sampling`, `/server`, `/usage` | Refused: they run at the PC only |
| Page | bare `/prompts` | Opens the 📣 sheet on its Prompts tab (📣 does too, even with *Web UI commands* off) |
| Other | Every form that opens a pane or a window at the PC (`/settings`, `/theme`, `/view`, `/prompts list --clear`, `/camera`, `/screen`…) | Refused, naming the form that works here when there is one |

* **A Ralph loop:** its confirm and its checkpoint ask on the page and at the PC at once; the status row shows 🔁 with the step and the pass, and Stop ends the loop.
* **`/loop`:** a loop of messages runs as at the PC; a looped `/imagine` runs, a looped `/read` is refused (it is the PC's). Stop ends a pass or the wait between two, and with it the loop. A line `/loop` can't read gets the PC's own error. Bare `/shopping` points at the Shopping tab, or at *Web UI shopping controls* while the tab is off; `/youtube list` likewise points at the YouTube tab's saved videos, or at *Web UI YouTube controls*.
* **What differs from the PC:** `/ha` and `/docker` ask before a change (they act unasked at the PC); `/msgclear` asks its yes on the page and the PC at once; `/ha` runs a read or a safe service (*Home Assistant safe services*) without asking. A bare `/docker` lists the containers. `/rewind` puts the text on the page's draft, ahead of what's there, with *Undo* (`/prompts <n>` sends the prompt); a rewound message's pictures, PDFs and pasted blocks come back on the PC's line only. `/test` clears the chat first, as at the PC.
* **Refused when it runs:** `/model <id>` and `/reasoning` while connecting would mean looking for a server (no server saved, or the saved one not in use: its API or the embedded server switched off, a Docker container not chosen), or naming an embedded model not installed (for `/reasoning`, the saved one): those pickers and that download are the PC's. The settings are checked again as the line runs, so one switched off meanwhile is refused there, with an error line.
* **Stop:** the page's Stop ends `/claude` too. A model loading after `/model` or `/reasoning` can't be stopped from the page (Ctrl+C at the PC).
* **An unknown command:** the page offers *Send as a message* (a path like `/home/me` goes to the model as typed), and for `//tools` *Use /tools*.
* **The log:** each command from the page is logged with the command's name, the address and the verdict, never the rest of the line.
