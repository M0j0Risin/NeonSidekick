namespace NeonSidekick.Help;

/// <summary>One way to type a command and what it does: <c>/camera watch [&lt;seconds&gt;|off]</c>, then its sentence.</summary>
public sealed record CommandForm(string Syntax, string Meaning);

/// <summary>A slash command with every form it takes, the first its plainest.</summary>
public sealed record CommandHelp(string Command, IReadOnlyList<CommandForm> Forms);

/// <summary>
/// Every slash command's forms (2026-10-02, for <c>neon_help</c>, <see cref="NeonHelp"/>), A to Z by command: seeded from README's
/// Slash commands table, a subcommand a form of its own. <see cref="App.SlashCommands.HelpEntries"/> holds each command's short
/// description; <c>NeonHelpCatalogTests</c> pins that the two name the same commands, so a new command needs its forms here (and
/// its docs/COMMANDS.md row, README's until 2026-10-05). Since 2026-10-05 (the user's ask: <c>/help</c>'s Commands tabs were too tight) these are also the forms
/// <c>/help</c> shows in its third column (<see cref="HelpSyntax.Forms"/>), so every syntax keeps <see cref="HelpSyntax"/>'s
/// notation — the command, then subcommands, keywords and <c>--flags</c> as typed, <c>&lt;lowercase-hyphenated&gt;</c>
/// placeholders, <c>[optional]</c> parts, <c>a|b</c> choices with no blanks, <c>...</c> for a repeat — one shape per form, and
/// <c>HelpSyntaxTests</c> holds every one to it. The same day every command's parser was read against this list and the forms it
/// lacked were added (<c>/botchat --resume</c> and <c>--kill</c>, <c>/docker ps &lt;filter&gt;</c>, <c>/ha tv volume</c>,
/// <c>/sampling extra clear</c>, <c>/screen desktop</c>, <c>/theme export</c> and others).
/// </summary>
public static class HelpCommands
{
    /// <summary>The forms as written here, every system's but where <see cref="HelpMac.Forms"/> has a Mac text.</summary>
    internal static readonly IReadOnlyList<CommandHelp> AsWritten =
    [
        new("/about",
        [
            new("/about", "Show the app's version, runtime, folders, components, the embedded models' Hugging Face pages and the license."),
        ]),
        new("/advisor",
        [
            new("/advisor", "List the advisors from *Advisor list*: each profile, its role, and the kind of server and the model it is on, a missing one marked."),
            new("/advisor <name> <question>", "Ask one advisor, named by its profile or a word of its role, on its own profile's server and model, with the last 10 messages of this conversation. It may use this chat's read-only tools, and its answer joins the conversation, so the model can build on it. Works whether or not *Advisors enabled* is on."),
        ]),
        new("/avatar",
        [
            new("/avatar", "Open the emoji picker for the profile's *Model avatar*. Its tabs are For you (from the profile's name and persona), Recent, then each Unicode group; type to search names and keywords, Enter on a hand or a person for its skin tones, 🎲 for one at random."),
            new("/avatar <emoji>", "Set the avatar to that emoji at once (one emoji, typed or pasted)."),
            new("/avatar <words>", "Open the picker searching for the words (headless: set the best match)."),
            new("/avatar clear", "Clear the avatar: replies open with ● again, and the window title shows the name alone."),
            new("/avatar model [<emoji>|<words>|clear]", "Do the same, naming the *Model avatar* outright."),
            new("/avatar user", "Open the emoji picker for your *User avatar*, the emoji that opens each line you send in place of ›."),
            new("/avatar user <emoji>|<words>", "Set your avatar to that emoji, or open the picker searching for the words (headless: set the best match)."),
            new("/avatar user clear", "Clear your avatar: your lines open with › again."),
        ]),
        new("/botchat",
        [
            new("/botchat [<profile> ...] [--] [<topic>]", "Let profiles talk to each other, each in its own persona, until you stop them. Leading words that name profiles are the cast (every profile when none is named; the loaded one speaks first); the first other word starts the topic, and `--` makes everything after it the topic. Its settings are on `/settings` › Client › Botchat."),
            new("/botchat --resume [<line>]", "Carry on the last bot conversation, with an optional line of your own first. Ctrl+Alt+B or a double-click on the hint row's 👥 resumes it without a line; during a bot conversation, Ctrl+Alt+B ends it at once."),
            new("/botchat --kill", "Stop the extra embedded servers the bots started (Ctrl+Alt+K, or a double-click on the hint row's 🧟 while any run)."),
        ]),
        new("/browse",
        [
            new("/browse [<folder>]", "Browse the working directory, or a folder in it, as a tree in a pane (Tab completes the folder). Space, → and ← open and close folders, `-` collapses all, and Enter opens or closes a folder, or opens a file in its default app with the pane left open. Hidden, system and dot entries show only when *File browser/tree mode* is `show-hidden`; `.git` folders are left out. Works while a reply runs."),
        ]),
        new("/browser",
        [
            new("/browser [<url>]", "Open the page, or *Web browser home page* (a blank page when that is empty), in the Browser window, the app's own browser, to sign in to a site or get past its bot check. When you close it, its cookies for the sites it visited go to the web tools for this run, so `web_fetch` and `download_file` reach those sites as you did; the window keeps its own cookies between runs in the profile's `webview2-browser` folder, each profile its own. Its bar has back, forward, reload, home, history (🕘), saved links (☆, ★ on a saved page), an address box (words search with *Web search method*) and a button that opens the page in your default browser. Ctrl+W closes it, F5 reloads, Alt+← and Alt+→ go back and forward, Ctrl+L goes to the address box, Ctrl+H opens the history, Ctrl+D saves or unsaves the page and Ctrl+Shift+O opens the saved links. The web tools open the same window on a bot check they cannot get past, per *Web browser botcheck window*. Works while a reply runs. Windows only. Not headless."),
            new("/browser clear history|all", "Clear the Browser window's history, its list of visits too (`history`), or everything it keeps for this profile: history, cookies, sign-ins, cache and site settings (`all`, after a yes). Your saved links stay. The web tools also forget the cookies the window gave them. The window opens hidden for it when it isn't open. *Web browser privacy* `clear-history` clears the history by itself at each close. Windows only."),
            new("/browser <n>", "Open saved link n, its number in `/browser list`, in the Browser window. Windows only."),
            new("/browser save [<url> [<name>]]", "Save a link to the profile's saved links: the address with the words after it as its name, or with no address the page the Browser window shows, its title as the name. A link saved by its address alone takes its page's title once the window visits it."),
            new("/browser list", "Show the saved links on a pane: Enter or a double-click opens one in the Browser window. `a` adds an address, `n` renames, `e` changes the address, `d` removes and `c` clears the list (each removal after a yes); Shift+↑↓ and Shift+Home/End move a link. Type to filter; Shift+letter starts a filter with a button's letter. The toolbar's ⭐ opens it too."),
            new("/browser list --clear", "Take every saved link off the list, after a yes (headless: at once). `d` on the pane or `/browser unsave` takes one."),
            new("/browser unsave <n>|<url>", "Take a saved link off the list, by its number in `/browser list` or its address."),
        ]),
        new("/camera",
        [
            new("/camera", "Open the camera pane to take a photo: Space takes it, `r` takes it again, Enter puts it on the input line as `[Image #N]`, ESC drops it. Under *Camera preview* `live` a camera window of its own shows the shot as you frame it. The photo is saved in the *Camera output folder* under the profile's `images` folder. Without the pane, it takes one at once."),
            new("/camera snap", "Take a photo at once and put it on the input line."),
            new("/camera list", "List the cameras in a pane, numbered, the chosen one marked."),
            new("/camera use <n>|<name>", "Choose the camera by its number in the list, its name, or the start of its name (*Camera device*)."),
            new("/camera live", "Show the camera live in its own window until you close the window or `/camera off`."),
            new("/camera watch [<seconds>|off]", "Start watch mode: the camera looks every *Camera watch interval (s)* seconds (or the seconds given, 2–3600), and a picture that changed rides your next message. With *Camera watch unprompted* on, the model may also be shown it unasked. Watch mode never starts on its own; `/camera watch off` stops it."),
            new("/camera off", "Stop `/camera live` and watch mode; the camera closes a few seconds later."),
            new("/camera view", "Open the picture viewer on the *Camera output folder*. Works while a reply runs."),
            new("/camera thumbs", "Open the thumbnail browser on the *Camera output folder*. Works while a reply runs."),
        ]),
        new("/claude",
        [
            new("/claude <message>", "Send the message to Claude Code (the `claude` CLI) and stream its reply into the transcript. Its settings are on `/settings` › Servers › Anthropic."),
            new("/claude new", "Start a new Claude Code conversation; the next `/claude` begins it. `/clear`, `/new` and a profile switch start one too."),
        ]),
        new("/clear",
        [
            new("/clear", "Start a new conversation and clear the screen."),
        ]),
        new("/cmd",
        [
            new("/cmd <command>", "Run a shell line yourself in the working directory, in the *Shell default shell*, its output drawn as it prints; ESC kills it. It passes the model's checks: *Shell command policy* `off` refuses it, the shell police refuse what they refuse under any policy, and under `ask` a line not on *Shell allowed commands* goes to the approval pane. The line and its output go into the conversation, so the model sees them, cut at *Shell output max chars* (the whole in `.shell\\cmd_….log`)."),
            new("/cmd --shell <shell> <command>", "Run the line in another shell: `powershell`, `cmd` or `bash` on Windows; `zsh`, `bash` or `powershell` on a Mac."),
            new("/cmd --bg <command>", "Run the line in the background while you chat, after the same checks (with `--shell` too, in either order). `/process` lists it and its window shows the output live; Ctrl+K twice there stops it. When it ends or is stopped, its output is shown (cut at *Shell output max chars*, the whole in `.shell\\proc_….log`) and goes into the conversation before your next message; the model's process tool never sees it."),
        ]),
        new("/collapse",
        [
            new("/collapse", "Fold the tool runs, code blocks, diffs and thinking again."),
        ]),
        new("/comfy",
        [
            new("/comfy", "Show the ComfyUI server's status in a pane: the workflows found (family, input, size, placeholders), the skipped files and where workflows go. A server that does not answer is an error line in the chat."),
            new("/comfy offered", "List the workflows offered to the model in a pane, one bullet each (name, family, input, size, description), without asking the server."),
            new("/comfy edit json|markdown|md <workflow>", "Open a workflow's graph, or its `.md`, in your editor; a missing `.md` is created with the family filled in."),
            new("/comfy view", "Open the picture viewer on the output folder. Ctrl+Alt+U opens it behind the terminal, which keeps the keyboard, or closes it. Works while a reply runs."),
            new("/comfy thumbs", "Open the thumbnail browser on the output folder. Works while a reply runs."),
            new("/comfy purge", "Delete everything in the output folder (under the profile's `images` folder) for good, after a yes. Refused when the output folder is the `images` folder itself."),
        ]),
        new("/compact",
        [
            new("/compact [<focus>]", "Shrink the conversation's context, as *LLM compact mode* says. A focus tells the summary what to keep (under `prune` there is no summary, so it is not used)."),
        ]),
        new("/copy",
        [
            new("/copy [<n>|all] [--thinking]", "Copy the last reply, the last *n*, or the whole transcript (`all`) to the clipboard as Markdown. `--thinking`, before or after, includes the model's thinking, quoted under `💭 **Thinking**` where it happened."),
        ]),
        new("/cwd",
        [
            new("/cwd [<path>|~|browse]", "Show or change the working directory. A path is a full one, unquoted, and a folder that does not exist is created; once it starts with a drive (`D:\\`), the list completes that folder's subfolders. `~` returns to the profile's `files\\` folder; `browse` opens the folder picker."),
        ]),
        new("/docker",
        [
            new("/docker", "Show the Docker engine's containers on a pane, running first, with their state, health and ports. Enter on one offers what fits its state: stop, restart or pause (each asks first), start or unpause, its last 50 log lines, open a published port in the browser, copy the id. `l` narrows the list to the LLM servers (by *Docker server match words*, plus those ticked in *Docker server containers*). Type to filter; Shift+letter starts a filter with `r` or `l`. Without the pane, it lists them."),
            new("/docker ps [<filter>]", "List the containers, or those whose name or image holds the filter."),
            new("/docker status", "Show Docker Desktop's and the engine's versions, with the container counts."),
            new("/docker logs <container> [<lines>]", "Show a container's last lines in a pane: 50 unless you give a number, up to 2000."),
            new("/docker stats [<container>]", "Show the CPU, memory, network and disk use of one running container or all of them."),
            new("/docker start|stop|restart|pause|unpause <container>", "Act on one container, named by its name, a unique part of its name, or its id (4 or more leading characters). It is your own hand: *Docker writes* never applies, nothing is asked, and every change is logged. Works while a reply runs."),
        ]),
        new("/draft",
        [
            new("/draft", "Write the next message in your editor. It is sent when you save and close the file."),
        ]),
        new("/echo",
        [
            new("/echo <text>", "Print a line as a reply, read aloud when *TTS output* is on."),
        ]),
        new("/exit",
        [
            new("/exit", "Exit the app."),
        ]),
        new("/expand",
        [
            new("/expand", "Unfold every folded tool run, code block, diff and thinking block, now and from here on. Ctrl+O switches between this and `/collapse`, as does ⤡ on the rule over the input row (without a notice)."),
        ]),
        new("/explore",
        [
            new("/explore [<folder>]", "Open the working directory, or a folder in it, in your file browser. Ctrl+E runs it too."),
        ]),
        new("/find",
        [
            new("/find [<words>]", "Find words in the transcript: a find row opens over the input row, every match is marked, and the transcript scrolls to the newest. Typing changes the words; Enter or F3 goes to the next match up (older), Shift+Enter or Shift+F3 back down, PgUp/PgDn scroll. Folded blocks that hold a match open, and fold again when the find ends. ESC ends it at the bottom of the transcript. Ctrl+Shift+F runs it too."),
        ]),
        new("/gituser",
        [
            new("/gituser [force]", "Write *GitLib user email* and *GitLib user name* into the repository's config as `user.email` / `user.name`. An existing `[user]` section is kept unless you add `force`. Does nothing while *GitLib tools* is off."),
        ]),
        new("/ha",
        [
            new("/ha", "Show Home Assistant at a glance: the server, the lights on in each room, the TV, temperatures, motion, low batteries and to-do lists."),
            new("/ha on|off|toggle <name> [<brightness>]", "Switch a room (its group light), a light, a switch or the TV (`/ha on den 40%`, `/ha off kitchen and hallway`). A brightness (0–100, the `%` optional) is only for `on` and lights."),
            new("/ha scene <name>", "Activate a scene (`/ha scene den relax`)."),
            new("/ha tv on|off|mute|unmute|up|down", "Control the only media player."),
            new("/ha tv vol|volume <level>", "Set the media player's volume (0–100, the `%` optional)."),
            new("/ha tv source <name>", "Switch the media player's source (`/ha tv source hdmi 2`)."),
            new("/ha states [<filter>]", "List entities with their ids and states in a pane: a domain, words of their names, or an entity id (which shows all of its attributes)."),
            new("/ha say <sentence>", "Hand a sentence to Home Assistant's Assist agent (*Home Assistant Assist agent*)."),
        ]),
        new("/header",
        [
            new("/header [on|off]", "Show or hide the banner (*Show header*): alone it flips the setting; `on` and `off` say which. The change shows at the next `/clear`, `/splash`, `/theme` or profile switch; nothing is redrawn now. Works while a reply runs; Ctrl+Alt+H runs it too."),
        ]),
        new("/help",
        [
            new("/help", "Show the commands and keys: everyday commands on Basic, the rest on Advanced, then Keys. The Keys tab's **⌨ key check** button (`k`) swaps in `/keycheck`'s list, and pressed again shows the keys. Ctrl+H runs it too."),
        ]),
        new("/homebar",
        [
            new("/homebar [on|off]", "Show or hide the home bar, the row of Home Assistant chips and the shopping list's 🛒 (*Home bar*; under the performance bar unless *Toolbar order* moves it). Alone, it hides the bar or shows it again with its last items; the first time, with nothing picked, it opens the picker. Works while a reply runs; Ctrl+W runs it too."),
            new("/homebar edit", "Choose the bar's items, their order and their sides on the picker. Its first tab lists the items under Pinned left, center and right: Shift+←/→ pins one a group over, Shift+↑/↓ moves it, Shift+Home/End to its group's top or bottom, Delete takes it off, and Enter offers the same as a list plus Rename on the bar…, Change emoji… and their defaults. The other tabs (Rooms, Lights, Switches, Sensors, Climate, Media, Scenes, Security, Zones, Other and Shopping) add or remove an item with Space or Enter; typing filters. Without Home Assistant it opens with the bar and Shopping alone."),
            new("/homebar tap <item>", "Do what a double-click on a chip does, by its item (`entity:light.desk`, `area:kitchen`) or its name. A light, switch or fan toggles, a room's lights go off or on, a scene or script runs, a player plays or pauses. Unlocking, opening a garage, gate or door, and disarming an alarm ask first."),
            new("/homebar show <item>", "Open a chip's pane, as a right-click does: its actions (on, off, brightness, volume, open, close, lock…), or a room's devices to switch one by one."),
            new("/homebar name <item> = <alias>", "Show an item's chip under a name of your own (`/homebar name Den PC Monitor = PC`), the item named by its id, name or alias. Nothing after the `=` shows the chip's emoji alone (no name and no value: a room's 🏠 without its count); `default` puts its own name back. Only the chip's label changes: a click's note and its pane keep Home Assistant's name."),
            new("/homebar emoji <item> = <emoji>", "Show an item's chip with an emoji of your own in place of its glyph (`/homebar emoji Den Lamp = 🛋️`), the item named by its id, name or alias. Nothing after the `=`, or `default`, puts its own glyph back. One emoji only; it stays fixed, so a lock's 🔒/🔓 no longer follows its state."),
        ]),
        new("/imagine",
        [
            new("/imagine [<workflow>] <prompt> [-- <negative>|--no-negative]", "Generate a picture on ComfyUI from your own prompt, sent exactly as typed, with no model in between. The first word is the workflow when it names one; a workflow that takes no prompt runs without one. It renders behind the input line and is drawn when done; double-click 🖼️ on the hint row to cancel."),
            new("/imagine <prompt> [--seed <n>] [--size <w>x<h>] [--steps <n>] [--count <n>]", "Set the seed, size, steps and picture count (at most *ComfyUI max images per call*). The size's `x` may be `X` or `*`. Flags go anywhere on the line, in any case; a value with spaces goes in double quotes."),
            new("/imagine <prompt> [--cfg <x>] [--denoise <x>] [--image <path>] [--image2 <path>] [--image3 <path>]", "Set the guidance and denoise strength, and up to three input pictures (a path, or a pasted picture's label such as `\"[Image #1]\"`); `--image2` needs `--image`, and `--image3` needs `--image2`."),
        ]),
        new("/keycheck",
        [
            new("/keycheck", "List the app's key chords and whether another program holds each as a global hotkey, the held ones first; `/help`'s Keys tab shows this list too. A held chord never reaches the app: free it in the program that holds it, such as a graphics card's overlay. Only hotkeys registered with the system show; a keyboard hook or a terminal key binding can still take a key."),
        ]),
        new("/learn",
        [
            new("/learn [<note>]", "Write or improve a skill in the background from the last turn, a note steering what to keep. Needs *Agent skills* and *LLM offer tools* on."),
            new("/learn sessions [<n>|<search>]", "Do the same from stored sessions: the newest 5, the newest n (1–20), or those a search finds."),
        ]),
        new("/log",
        [
            new("/log", "Open the log window: this run's diagnostic lines, colored by level, following the newest while it is at the bottom. Scrolling away pauses it; Ctrl+E, Ctrl+End or scrolling back to the bottom follows again, and Ctrl+Home goes to the top. Drag to select, Ctrl+A selects all, Ctrl+C copies; F11 or a double-click is full screen, and Esc leaves full screen, then closes the window. TAB brings the terminal forward, and any other Ctrl or Alt chord runs in the chat (Ctrl+G closes the window). Opened by Ctrl+G, it stays behind the terminal, which keeps the keyboard."),
            new("/log file", "Open the diagnostic log file in your editor; only when the app was started with `--log <path>`."),
            new("/log path", "Copy the diagnostic log file's path to the clipboard; only when the app was started with `--log <path>`."),
        ]),
        new("/loop",
        [
            new("/loop <count>|infinite [<delay>] <message>", "Send the message that many times, or until ESC or Ctrl+C, waiting for each reply. A delay is one word such as `30s`, `5m` or `1h30m` (never a bare number), up to 24 hours."),
            new("/loop <count>|infinite [<delay>] /imagine|/read <arguments>", "Run `/imagine` or `/read` itself on each pass, with no model in between; no other command can be looped."),
        ]),
        new("/mcp",
        [
            new("/mcp", "Open the MCP pane: connect external MCP servers and switch their tools on or off. On the Tools tab, typing narrows the list to the tools whose name or description holds the text; Backspace erases, ESC clears it, a second ESC closes."),
        ]),
        new("/memory",
        [
            new("/memory", "List the memories on a pane. Enter on one offers **edit** (its text on the input row), **disable** (kept and grayed out, but never offered to the model; **enable** undoes it) and **forget** (after a yes); **+ add** (`a`) types a new one. **mode: …** (`m`) on the title row steps *Memory mode*: read-write, read-only, off."),
            new("/memory read-write|read-only|off|on", "Set *Memory mode* (`on` is read-write)."),
            new("/memory forget|edit", "Forget every memory, after a yes (`forget`), or open `memory.json` in your editor (`edit`; invalid JSON is ignored with a warning)."),
        ]),
        new("/model",
        [
            new("/model [<id>]", "Pick a model from the server's list, or set one by its id, as typed (the server is not asked). The list is A to Z with the model in use under the cursor; typing narrows it, Backspace erases, ESC clears it, a second ESC keeps the model. On the embedded LLM, it lists the installed embedded models. Ctrl+M runs it too."),
        ]),
        new("/msgclear",
        [
            new("/msgclear", "Clear this profile's message history, stored and in memory, after a yes."),
        ]),
        new("/new",
        [
            new("/new", "Start a new conversation without clearing the screen."),
        ]),
        new("/open",
        [
            new("/open <file>", "Open a file from the working directory in its default app, as double-clicking it would (Tab completes the path). A folder is refused: `/explore` or `/browse` opens one. Works while a reply runs."),
        ]),
        new("/pdf",
        [
            new("/pdf <file> [to=<path>] [paper=letter|a4|legal] [landscape] [overwrite]", "Make a PDF of a file in the working directory, beside it unless `to=` names a file or folder; an existing PDF is replaced only with `overwrite`. Markdown keeps its headings, tables, lists, links, colored code and the pictures beside it; text and code become a colored listing; HTML prints as the page, without its scripts or anything outside the working directory; a picture is fitted to one page. Options go anywhere. *PDF engine* chooses what makes it: Edge, Chrome or Brave, or without one Microsoft Print to PDF (Markdown, text and pictures, in black and white). Alone, `/pdf` shows how to use it."),
            new("/pdf <url> [to=<path>] [overwrite]", "Make a PDF of a web page (`http://` or `https://`) as the browser shows it, once *Web browser network mode* allows its address. The page keeps its own layout, so `paper=` and `landscape` are not taken."),
            new("/pdf reply [<options>]", "Make a PDF of the last reply, formatted as Markdown."),
        ]),
        new("/perfbar",
        [
            new("/perfbar [off|text|gauge|led]", "Show or hide the performance bar (*Performance bar*). Alone, it hides the bar or shows it again with its last meters (CPU, RAM, GPU and VRAM the first time); `off` hides it, and a look (`text`, `gauge`, `led`) sets that look and shows the bar. Works while a reply runs; the toolbar's 📈 and Ctrl+F run it too."),
            new("/perfbar edit", "Choose the bar's meters, their order and their sides on the *Performance bar* checklist. Enter or Space checks a meter, Shift+←/→ pins it a group over (left, center, right), Shift+↑/↓ moves it, Shift+Home/End to its group's top or bottom, Delete unchecks; `a`, `n` and `d` pick all, none or the default."),
        ]),
        new("/plan",
        [
            new("/plan <requirement>", "Have the model research with read-only tools and present a plan before anything changes; you approve it on the pane or with `/plan approve`."),
            new("/plan [show]", "While planning, say where the plan stands."),
            new("/plan <detail>", "While planning, add detail."),
            new("/plan approve [--fresh]", "While planning, approve the presented plan by typing instead of on the pane (you may edit the file first)."),
            new("/plan cancel", "While planning, leave plan mode; the file is kept, marked `cancelled`."),
            new("/plan save [<name>]", "While planning, keep a reply that looks like a plan as the plan when the model never called `present_plan` (a notice says so), and bring up the approval pane."),
            new("/plan gaps [model|advisor|any]", "While planning, review the presented plan pass after pass until a pass finds no gaps, then bring the approval pane back; the pane's *Close gaps…* does the same. `model` reviews alone (the default), `advisor` has an advisor find the gaps each pass, `any` consults advisors where a role fits. ESC stops it; *Plan gap passes* caps the passes."),
            new("/plan open <name>", "Pick a plan up again, in or out of plan mode (names complete from `.neon/plans/`). Plan mode turns on over that file, the file goes back to `draft`, and the model reads it and asks what should change; `/plan approve` then carries out only the unticked steps."),
            new("/plan open", "List the plans with their status and progress in a pane."),
        ]),
        new("/print",
        [
            new("/print <file> [printer=<name>] [copies=<n>] [pages=<range>] [landscape]", "Print a file from the working directory: text and code as a listing, Markdown formatted, a picture fitted to one page, each page headed with the file's name, the time and *page N of M*. Anything else (a PDF, a Word or Excel file) goes to the program the system has for it, on the default printer, without the options. `printer=` matches a name or part of one (`printer=color`; quote a name with spaces); copies are 1–10; pages are `3`, `1-3`, `4-` or a list such as `1,3-5`. Options go anywhere. *Print action policy* never applies to this command."),
            new("/print reply [<options>]", "Print the last reply, formatted as Markdown."),
            new("/print printers", "List the installed printers in a pane, marking the system default and *Print default printer*. Alone, `/print` shows how to use it and lists them too."),
        ]),
        new("/process",
        [
            new("/process", "List the background processes on a pane, the model's (`run_command` with `background`) and your `/cmd --bg` ones: the id, running or how it ended, how long it has run, the shell and the command. Enter or a double-click opens one in the process window; **kill** (`k`) stops the highlighted one after a yes; the filter button (`f`) steps from all to the running ones, the ended ones and back. The toolbar's ⚡ opens it too, as does a double-click on the ▶ count on the rule under the input line."),
            new("/process <id>", "Open the process window on one process (any unique start of its id; Tab completes it). It shows the output live, following the newest line while at the bottom, stderr in the warning color, its state in the title. Scroll, select and copy as in the log window; TAB brings the terminal forward, and `/process` with another id switches the window to that process. Ctrl+K twice within 3 seconds stops it: the chat says you stopped it, and the model hears so on its next turn."),
        ]),
        new("/profile",
        [
            new("/profile [<name>]", "Switch profiles: alone, a picker (a typed letter jumps to the next profile starting with it); with a name, straight to that one. Profiles whose name starts with `_` stay off the picker and the name list unless loaded; `/profile _name` still switches to one. Ctrl+P runs the bare `/profile`."),
            new("/profile add|delete <name>", "Create or delete a profile."),
            new("/profile rename <name> <new-name>", "Rename a profile."),
            new("/profile reset [<name>] [--all]", "Reset a profile to the defaults (the loaded one when no name is given), keeping its URLs, paths and keys; `--all` resets those too."),
            new("/profile push|pull <name>", "Copy every setting but the working directory to another profile (`push`) or from it into this one (`pull`). A pull clears the conversation; a section pulled (the forms below) applies at once, like a settings change, and the conversation stays."),
            new("/profile push|pull keys <name>", "Copy the five API keys (*LLM API key*, *Anthropic API key*, *OpenAI API key*, *Home Assistant API key*, *YouTube API key*) to another profile or from it, after a yes. They are mirrored: a key the source lacks is cleared in the target. Only saved keys are copied, encrypted ones as they are; a key set only by a `NEONSIDEKICK_*` variable is not."),
            new("/profile push|pull servers <name>", "Copy the server settings to another profile or from it, after a yes, replacing the target's. They are which server, model and reasoning (*LLM server scan mode*, *LLM URL*, *LLM model*, *LLM reasoning*), *LLM reasoning estimate*, every model's sampling overrides and *LLM sampling from Hugging Face*, and the rows of the Embedded, Docker, Anthropic and OpenAI tabs but the API keys and *Embedded download mode*. The saved values are copied, not a `--url` or variable that overrides only this run; installed embedded models are shared by every profile already."),
            new("/profile push|pull allowed <name> [overwrite]", "Copy *Shell allowed commands* to another profile or from it: added to the target's list, or replacing it with `overwrite`."),
            new("/profile push|pull history <name> [overwrite]", "Copy the message history to another profile or from it: added to the target's, or replacing it with `overwrite`. Refused while the target has *Keep message history* off."),
            new("/profile push|pull memory <name> [overwrite]", "Copy the memories to another profile or from it, after a yes: added to the target's (duplicates skipped), or replacing them with `overwrite`. *Memory mode* has no say."),
            new("/profile push|pull policestrings <name> [overwrite]", "Copy *Shell police forbidden strings* to another profile or from it: added to the target's list, or replacing it with `overwrite`."),
            new("/profile push|pull persona <name> [overwrite]", "Copy `persona.md` (the persona) to another profile or from it, after a yes. One already in the target is replaced only with `overwrite`."),
            new("/profile push|pull operata <name> [overwrite]", "Copy `operata.md` (the operating rules) to another profile or from it, after a yes. One already in the target is replaced only with `overwrite`."),
            new("/profile push|pull vocalia <name> [overwrite]", "Copy `vocalia.md` (the spoken-reply directive) to another profile or from it, after a yes. One already in the target is replaced only with `overwrite`."),
            new("/profile push|pull avatar user|model <name>", "Copy the *User avatar* (`user`) or the *Model avatar* (`model`) to another profile or from it, after a yes, replacing the target's. A profile with none has nothing to copy."),
            new("/profile edit|reload", "Open `profile.json` in your editor (`edit`), or read it back and reconnect only what changed (`reload`)."),
        ]),
        new("/queue",
        [
            new("/queue [clear]", "List and prune the messages queued during a reply; **⊠ clear all** (`c`) drops them all, and `/queue clear` does so without the pane. **mode: …** (`m`) and **cancel: …** (`k`) on the title row step *Queue mode* and *Queue cancel mode*, on the empty queue too. While the queue is held (*Queue cancel mode* `hold`, after a canceled reply), and always under *Queue mode* `manual`, **➤ send** (`s`) sends the next message now (under a reply, when it ends). The toolbar's ⏳ and Ctrl+Q open it; Ctrl+Alt+Q runs `/queue clear`."),
            new("/queue mode automatic|manual|off", "Set *Queue mode*: `automatic` queues what you send during a reply and sends it when the reply ends, `manual` queues every message for **➤ send**, `off` keeps no list. Works while a reply runs."),
            new("/queue cancel hold|drain|empty", "Set *Queue cancel mode*, what a canceled reply does to the queue: `hold` keeps it until your next message has run, `drain` sends the next one at once, `empty` drops them all. Works while a reply runs."),
        ]),
        new("/read",
        [
            new("/read [<file> [<n>]|<n>]", "Read a text file from the working directory aloud as a reply. Alone, it resumes; a number starts from that sentence (a file named only with digits is typed `./5`). On an empty line, Space pauses and resumes, and ←/→ step a sentence back or forward (while paused they only move the position, and Space or `/read` plays from there). The hint row shows the keys that apply: `← → ␣ pause`, `← → ␣ play` or `␣ replay`."),
        ]),
        new("/reasoning",
        [
            new("/reasoning [none|low|medium|high|xhigh]", "Pick or set the reasoning effort (*LLM reasoning*). Ctrl+R runs it too."),
        ]),
        new("/remember",
        [
            new("/remember <text>", "Add a memory. Refused while *Memory mode* is `off`."),
        ]),
        new("/rename",
        [
            new("/rename [<name>]", "Rename the current session, as `/sessions title` does: a name is set at once (after the reply, under one); alone, it opens the rename box with the current name, and works while a reply runs. There is nothing to rename before the first reply. Ctrl+Alt+R runs it too."),
        ]),
        new("/rewind",
        [
            new("/rewind [<n>]", "Go back to an earlier message. A list of the messages you sent opens (the cursor on the last, or n back); after a yes, the picked message and everything after it leave the conversation, and its text returns to the input row, pictures and pasted blocks included, to edit and send again. A stored session loses the same turns. Only the conversation rewinds: what a tool changed (files written, commands run, commits) stays, and the yes/no names those tools. Messages compacted into a summary cannot be picked. ESC twice on an empty input line opens it too."),
        ]),
        new("/sampling",
        [
            new("/sampling", "Edit the per-model sampling overrides on a pane (*LLM sampling*)."),
            new("/sampling <field> <value>|clear", "Set or clear one of the connected model's values: `temperature`, `top_p`, `top_k`, `min_p`, `presence_penalty`, `frequency_penalty` or `repetition_penalty` (`repeat_penalty` too)."),
            new("/sampling extra <json>|clear", "Set the extra request body as a JSON object, or clear it (`{}` clears it too)."),
            new("/sampling clear", "Clear every override of the connected model."),
        ]),
        new("/screen",
        [
            new("/screen", "Capture the monitor the app is on and put the screenshot on the input line as `[Image #N]`. It is saved in the *Screen output folder* under the profile's `images` folder, and opens in the picture viewer under *Screen preview*. It is your own hand: *Screen tools* and *Screen policy* never apply."),
            new("/screen all|desktop", "Capture every monitor as one picture."),
            new("/screen monitor:<n>", "Capture monitor n (`/screen list` numbers them; `monitor <n>` works too)."),
            new("/screen window:<id>|<title-words>", "Capture one window, even when another covers it: by its id from `/screen list`, or words of its title (or its process name)."),
            new("/screen behind", "Capture the window right behind the app's own: the one you were just in."),
            new("/screen list", "List the monitors and the windows (front to back) in a pane, with the target that names each; the argument list after `/screen ` offers them too."),
            new("/screen view", "Open the picture viewer on the *Screen output folder*. Works while a reply runs."),
            new("/screen thumbs", "Open the thumbnail browser on the *Screen output folder*. Works while a reply runs."),
        ]),
        new("/server",
        [
            new("/server [<url>|embedded|claude-code|docker|docker:<container>]", "Pick an LLM server found on the usual ports, or set one by URL (with its `http://` or `https://`). The list also offers the Anthropic and OpenAI APIs (each when enabled with a key), Claude Code (under *Claude Code server enabled*, when it is found), the installed embedded models and the chosen Docker containers (under *Docker servers enabled*). The model and reasoning pickers follow, and one reconnect applies all three. `embedded` lists only the installed embedded models (install one on `/settings` › Servers › Embedded); `claude-code` picks Claude Code; `docker` lists only the chosen containers, and `docker:<container>` switches to one, whose model is the one it serves, so no model picker follows. Ctrl+S runs it too."),
        ]),
        new("/sessions",
        [
            new("/sessions [<id>]", "List the stored sessions on a pane, or restore one by its id (`#12` works too). Ctrl+Alt+E runs it too."),
            new("/sessions purge <id>|all", "Delete one stored session, or every one."),
            new("/sessions purge older <age>", "Delete the sessions older than an age: a number of days (`30`) or a duration (`12h`, `90m`, `2 hours`, `1d 6h`)."),
            new("/sessions title [<text>]", "Rename the current session; alone, it opens a box with the current name (as a double-click on the name on the rule does). Works while a reply runs."),
        ]),
        new("/settings",
        [
            new("/settings", "Open the settings pane (`//` too); a letter typed on a tab searches every setting. Ctrl+/ runs it too."),
            new("/settings <words>", "Search every setting (`/settings`, `/tools`, `/skills`, `/mcp`) by its name, tab or description; Enter edits the row found."),
            new("/settings changed", "List the settings that differ from their defaults, each with its default; Enter edits a row, `r` puts it back to its default."),
        ]),
        new("/shopping",
        [
            new("/shopping", "Open the shopping list on a pane, walked through the active store: each aisle a heading over its items, the ticked ones last. Enter or Space ticks an item; `i` adds items (typed, or from the recently bought), `p` places the highlighted one at an aisle, `+` and `-` change its quantity, `n` renames it, `r` removes it, `a` ticks every item (or unticks them all), `c` clears the ticked ones and `s` opens the stores' manager. The toolbar's 🛒 opens it too; without the pane, the list is printed."),
            new("/shopping add [<items>]", "Put items on the list, separated by commas; a quantity may lead or follow one (`2 milk`, `eggs x3`). An item already there gets the quantity added. Alone, the add page on the pane: type new items, or Enter on one bought before (the most often first) to add it again."),
            new("/shopping check|uncheck|remove <item>", "Tick an item as bought, untick it, or take it off the list, by its name or its number in the order added."),
            new("/shopping rename <item> to <new-name>", "Rename an item, by its name or its number in the order added; its quantity and tick are kept, and it moves to wherever the store places the new name. A name already on the list merges the two, their quantities added."),
            new("/shopping list [<store>]", "Print the list walked through the active store, or the one named."),
            new("/shopping clear [all]", "Remove the ticked items; `all` empties the whole list, after a yes."),
            new("/shopping store [<name>]", "List the stores and the active one's layout; with a name, shop there: the list walks through it."),
            new("/shopping store rename <name> to <new-name>", "Rename a store; its reference folder is renamed to match (or left as it was, with the reason, if it cannot be moved). *Shopping stores* (`/tools` › Home › Shopping, or `s` on the `/shopping` pane) adds, renames, removes and reindexes stores on a pane."),
            new("/shopping store add|remove <name>", "Add a store (its reference folder is made), or remove one with its layout, after a yes."),
            new("/shopping export [<store>] [verbose]", "Write the items left to buy at the active store, or the one named, to a new `shopping-list-<time>.md` in the working directory (the local time: `shopping-list-20261011-162530.md`). The compact form is a heading naming the store, then a line per aisle; `verbose` puts a heading per aisle over a checkbox per item."),
            new("/shopping print [<store>] [verbose]", "Print the items left to buy at the active store, or the one named, through `/print`'s printer settings: compact (a line per aisle), or roomy with `verbose`."),
            new("/shopping reindex [<store>]", "Read the active store's reference documents again, or the named one's: every PDF, text file and picture in its folder under `db\\shopping\\reference`. A changed file is read again on its own; this reads them all."),
        ]),
        new("/shortcut",
        [
            new("/shortcut [<profile>] [--log]", "Put a shortcut on the desktop, `NeonSidekick (<profile>)`, that starts the app in the exe's folder on the loaded profile, or the one named (as `--profile <name>`). With `--log` it also writes a log, `logs\\neon-<profile>-{ts}.log` beside the exe, named by the time it starts. One already there is replaced; on a Mac it is a `.command` file. The first start offers it once (No, Yes, or Yes with a log)."),
        ]),
        new("/skills",
        [
            new("/skills", "Open the Skills pane: the skills on Offered, then the Reflection and Options settings. Enter on a skill opens its page: move it (between the profile and global skills while *Global skills* is on, or to another profile), rename, edit, revert to a kept version, lock, disable or delete it, each change after a yes. A locked skill cannot be changed by the model, a reflection or an update until it is unlocked; a disabled one (⛔, its row dim) stays in its folder but is never offered to the model. On Offered, typing narrows the list; Backspace erases, ESC clears it, a second ESC closes."),
            new("/skills add <source> [--global|--profile]", "Install an Agent Skill from the web after a preview: search words, `owner/repo[/skill]`, a GitHub URL (the repository, a `/tree/…` folder or a `SKILL.md`) or a `.zip` URL. A pane asks where it goes (the cursor on Cancel); `--global` and the global choice only while *Global skills* is on. Refused while a reply runs."),
            new("/skills purge list|commit <age>", "List the skills unused for that long (`list`; `30` days, `12h`, `90m`), deleting nothing, or delete them, folder and record, after a yes that lists them (`commit`)."),
        ]),
        new("/splash",
        [
            new("/splash", "Start a new conversation and show the splash screen."),
        ]),
        new("/stt",
        [
            new("/stt [on|off]", "Turn voice input on or off (*STT input*)."),
            new("/stt wake [on|off]", "Turn the wake word on or off (*STT wake*); off turns the interrupt off too."),
            new("/stt interrupt [on|off]", "Turn the wake-word interrupt during a spoken reply on or off (*STT interrupt*); it needs the wake word on."),
        ]),
        new("/sys",
        [
            new("/sys", "Show the system prompt and the tools sent to the model."),
            new("/sys persona [reset]", "Edit `persona.md` (the persona) in your editor, or reset it to the default."),
            new("/sys operata [reset]", "Edit `operata.md` (the operating rules) in your editor, or reset it to the default. A missing file is created with the rules in use now, the sentences for the tools that are on included."),
            new("/sys vocalia [reset]", "Edit `vocalia.md` (the spoken-reply directive, added last to every spoken reply; empty by default) in your editor, or remove it."),
        ]),
        new("/terminal",
        [
            new("/terminal [<folder>]", "Open a terminal window in the working directory, or in a folder under it (Tab completes the folder): Windows Terminal, or a console window without it. Ctrl+. runs it too."),
        ]),
        new("/test",
        [
            new("/test [<id>|reasoning|structured|long|all|history]", "Run benchmark tests against the connected model and save the results: one test by its id, a group (`reasoning`, `structured`, `long`) or `all`; `history` shows the results so far. Alone, it lists the tests with their last verdicts. The ids are `grid`, `rule`, `mind`, `sycophancy`, `json`, `state`, `needle`, `multihop` and `saturation`."),
        ]),
        new("/theme",
        [
            new("/theme [<name>]", "Switch the color theme (*Theme*), built-in or your own. Alone, it lists the themes, previewing the highlighted one beside the list when the window is wide enough; a typed letter jumps to the next theme starting with it, and nothing changes until Enter. During a reply, it runs when the reply ends; Ctrl+Z runs it too."),
            new("/theme export <name> [<new-name>]", "Write a theme to the `themes` folder as a file to edit, under a new name if one is given."),
        ]),
        new("/timer",
        [
            new("/timer [<duration> [<name>]]", "List the timers, or start one: `10m`, `90s`, `1h30m`, `5 min` (a bare number is minutes), up to 24 hours."),
            new("/timer stop <name>|all", "Stop one timer, or every one."),
        ]),
        new("/toolbar",
        [
            new("/toolbar [on|off]", "Show or hide the toolbar (*Tool bar*): alone it hides the toolbar, or shows it again with the items it last had (the default items the first time); `on` and `off` say which. Works while a reply runs; Ctrl+T runs it too."),
            new("/toolbar edit", "Choose the toolbar's items, their sides and their order on the *Tool bar* checklist. Enter or Space checks an item, Shift+←/→ pins it left or right, Shift+↑/↓ moves it, Shift+Home/End to its group's top or bottom, Delete unchecks, and `+` adds a divider after it; `a`, `n` and `d` pick all, none or the default."),
        ]),
        new("/tools",
        [
            new("/tools", "Open the tools pane: switch the model's tools on or off on Offered, and edit each tool group's settings on its own tab. On Offered, typing narrows the list to the tools whose name or description holds the text; Backspace erases, ESC clears it, a second ESC closes."),
            new("/tools <group>", "Open a group's page: `ask`, `shell`, `files`, `image`, `web`, `advisors`, `docker`, `obsidian`, `sql`, `oracle`, `mysql`, `sqlite`, `postgres`, `unc`, `ha`, `comfy`, `camera`, `print` or `shopping`. `shell` opens the *Shell command policy* picker (yolo asks first). Buttons at the top open related settings, and ESC comes back: `shell`'s allowed commands and police (`c`, `p`), `web`'s browser modes (`a`, `h`, `b`), `files`' browser mode (`b`), `image`'s edit mode (`e`), `advisors`' list (`a`), `obsidian`'s vault (`v`), `print`'s printer (`p`), `camera`'s watch, live, snap and screen (`w`, `l`, `s`, `c`), and the offered list of the database, UNC and ComfyUI groups. The toolbar's tool glyphs run it."),
            new("/tools shell allowed", "Open the *Shell allowed commands* list; Enter removes a prefix after a yes. The policy button (`p`) steps *Shell command policy* from off to ask to yolo, yolo after a yes (a no goes on to off); ESC closes it. The toolbar's lock and Ctrl+Alt+L run it."),
            new("/tools shell police", "Open the *Shell police* on/off page; turning it off asks first, and **strings** (`s`) opens *Shell police forbidden strings*. The toolbar's officer and Ctrl+Alt+O run it."),
        ]),
        new("/tree",
        [
            new("/tree [<path>]", "Show a tree of the working directory, or a path in it, in a pane, at most *File /tree max entries* long. Hidden, system and dot entries show only when *File browser/tree mode* is `show-hidden`; `.git` folders are left out unless you name one as the path."),
        ]),
        new("/tts",
        [
            new("/tts [on|off]", "Turn speech output on or off (*TTS output*)."),
        ]),
        new("/usage",
        [
            new("/usage", "Show token usage and performance statistics; a `~` marks a reasoning count the app estimated (*LLM reasoning estimate*). Ctrl+U runs it too."),
        ]),
        new("/vault",
        [
            new("/vault [<path>]", "Show a tree of the *Obsidian vault*, or a folder in it, in a pane, like `/tree`: dot-folders left out, at most *File /tree max entries* long, sizes per *File /tree show sizes*. It fails if *Obsidian tools* is off, no vault is set, or the folder cannot be reached or has no `.obsidian`."),
        ]),
        new("/view",
        [
            new("/view <image>|<folder>", "Open an image from the working directory in the picture viewer, or a folder there on its newest picture. The arrows or the mouse wheel step through the pictures, and a right-click opens the picture menu (rotate, flip, color, resize, convert, shrink, copy the path, show in the file browser, attach, print, delete; edits go where *Image edit mode* says). Works while a reply runs."),
            new("/view <image> --chat", "Draw the image in the transcript instead; `--chat` may be the first or last word."),
            new("/view <image>|<folder> --thumbs", "Open the folder (an image's folder, with the image selected) as thumbnails in a window of their own, in step with the picture viewer and the picture strip. A click shows the picture in the viewer, a double-click or Enter opens it there, and the viewer's own moves select it here; new pictures go on the end, so nothing moves. + and − or Ctrl+wheel resize the tiles, F5 lists and fits again, and a right-click opens the picture menu. `--thumbs` may be the first or last word. Works while a reply runs."),
        ]),
        new("/window",
        [
            new("/window", "Show the terminal window's width and height."),
        ]),
        new("/youtube",
        [
            new("/youtube", "Show what the video window is playing: the video, playing or paused, where it is and the volume."),
            new("/youtube <words>", "Search YouTube for the words and pick a video on the pane; Enter plays it in the video window. It needs a *YouTube API key* (each search costs 100 of its 10,000 daily quota units) and lists only videos that play embedded. Words that start with a verb below search too, unless what follows fits the verb."),
            new("/youtube search <words>", "Search for the words, whatever they start with."),
            new("/youtube search [--switches] <words>", "Search with switches before the words; a line that starts with a switch searches too. The switches are `--playlists` or `--channels` (Enter plays a playlist from its start, a channel's uploads), `--order relevance|date|views|rating|title`, `--length short|medium|long`, `--since today|week|month|year|<date>`, `--region <cc>`, `--lang <code>`, `--live`, `--captions` and `--safe none|moderate|strict`. Without them, the order, region, language and safe search come from `/tools` › Apps › YouTube."),
            new("/youtube play <id>|<link> [<time>]", "Play a video by its id or a YouTube link, from the time given (`90` or `1:30`) or the link's own; a link alone plays too. Needs no key. The window opens where it last closed, without taking the keyboard; F11 is full screen, and Esc leaves it, then closes the window."),
            new("/youtube play <playlist>|<link> [<n>]", "Play a public playlist through from entry n, by its id, a link with `list=` (from its `index=`, or its video) or a channel's id or link (its uploads); a playlist link alone plays too. Needs no key; an entry the player refuses is skipped."),
            new("/youtube next|previous", "Play the playlist's next video, or the one before (`prev` too)."),
            new("/youtube entry <n>", "Play the playlist's video n, 1 for the first."),
            new("/youtube playlist [<id>|<link>]", "Show a playlist's videos on the pane (with a *YouTube API key*: one quota unit, one more per 50 videos); Enter plays the playlist from the highlighted one, `s` saves that video. Alone, the playlist playing."),
            new("/youtube play|resume", "Carry on playing."),
            new("/youtube pause", "Pause the video."),
            new("/youtube seek <time>", "Go to a time in the video."),
            new("/youtube volume <level>", "Set the volume (0–100)."),
            new("/youtube mute|unmute", "Mute or unmute the video."),
            new("/youtube close", "Close the video window."),
            new("/youtube status", "Show the status line, as the bare `/youtube` does."),
            new("/youtube save [<id>|<link>]", "Save the video playing, or the one named, to this profile's saved videos; a named one's title is looked up (YouTube's oEmbed, no key needed, then the Data API with a *YouTube API key*). A saved video resumes where it was left the next time it plays (a time you give wins), and starts over once seen to the end. `s` in a search's picker saves the highlighted hit."),
            new("/youtube list", "Show the saved videos on the pane, each with where it was left: Enter plays one from there, `d` removes the highlighted one and `c` clears the whole list, each after a yes. Shift+↑↓ and Shift+Home/End move a video; type to filter, Shift+letter starting with `d` or `c`. Missing titles are looked up first."),
            new("/youtube list --clear", "Take every saved video off the list, after a yes (headless: at once). `d` on the pane or `/youtube unsave` takes one."),
            new("/youtube unsave <n>|<id>|<link>", "Take a saved video off the list, by its number in `/youtube list`, its id or a link."),
        ]),
    ];

    /// <summary>Every command's forms: <see cref="AsWritten"/>, with <see cref="HelpMac.Forms"/>' texts on macOS (2026-10-06). Declared after it, so it is set first.</summary>
    public static readonly IReadOnlyList<CommandHelp> Commands = OperatingSystem.IsMacOS() ? HelpMac.Apply(AsWritten) : AsWritten;
}
