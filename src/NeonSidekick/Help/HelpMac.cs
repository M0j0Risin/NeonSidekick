using NeonSidekick.App;

namespace NeonSidekick.Help;

/// <summary>
/// What <c>neon_help</c> and <c>/help</c> say on macOS where Windows' text would name the wrong system (2026-10-06, the tidy-up
/// before the first Mac release: a Mac user never reads Windows wording). A layer over <see cref="HelpSettings"/> and
/// <see cref="HelpCommands"/>, used only when <see cref="OperatingSystem.IsMacOS"/>, so Windows' sentences stay byte for byte as
/// they were (many are pinned). A setting or a form missing here keeps its one text, true on both systems. A feature that needs
/// Windows (the camera, printing, <c>/keycheck</c>, <c>/terminal</c>) says so, in its area's own words where
/// it has them. The embedded LLM runs on a Mac since 2026-10-07 (Metal), so its rows here say how it differs, not that it is missing; pictures
/// came the same day (ImageIO), so <c>Embedded vision</c> needs no Mac text any more.
/// <c>HelpMacTests</c> pins that every key still names a setting or a form the tables hold, so the layer cannot go stale.
/// </summary>
public static class HelpMac
{
    /// <summary>The settings whose text differs on a Mac, by field.</summary>
    public static readonly IReadOnlyDictionary<SettingsField, string> Settings = new Dictionary<SettingsField, string>
    {
        [SettingsField.LlmApiKey] = "The bearer token the server expects; leave it empty for local servers that need no key. A real key is saved encrypted under a key the app keeps in your macOS Keychain, and shown as `(set, encrypted)`. Typing a new value replaces it (`empty` stays as it is).",
        [SettingsField.ExternalSkills] = "Also reads the skills in `~/.agents/skills`, read-only.",
        [SettingsField.DraftEditor] = "The program `/draft` opens its temporary file with (`code --wait`, `nano`…). Empty uses the app macOS opens `.txt` files with (TextEdit unless you chose another).",
        [SettingsField.ShellOutputMaxChars] = "The most output one result carries back (2000–500000). Past that, the start and end are kept, and the whole text goes to `.shell/<id>.log` under the working directory, where `read_file` can reach it.",
        [SettingsField.SqlSetPassword] = "Pick a connection that takes a password (`sql`) and type it, masked. It is saved to that connection's store: encrypted in its `sql.json`, or in the macOS Keychain. `windows` sign-in (Kerberos) needs none.",
        [SettingsField.ComfyOutputFolder] = "The folder under the working directory the pictures are saved in (`comfy_images/pony-txt2img-1234.png`). Empty uses the working directory itself.",
        [SettingsField.ImageEditor] = "Where a double-clicked picture opens. Empty: the built-in picture viewer (with no desktop to draw on, over SSH, the app macOS opens the file type with). `system`: the app macOS opens the file type with (Preview unless you chose another). Anything else is a command, with the file's path appended (`open -a Preview`).",
        [SettingsField.ClaudeCliExecutable] = "The Claude Code CLI to run. Blank looks for `claude` on the PATH, then `~/.local/bin/claude` (where the native installer puts it). A path you set must exist; it is never swapped for another.",
        [SettingsField.AnthropicApiKey] = "Your Anthropic API key (`sk-ant-…`), saved encrypted under the app's key in your macOS Keychain and shown as `(set, encrypted)`. Typing replaces it; an empty entry clears it.",
        [SettingsField.OpenAIApiKey] = "Your OpenAI API key (`sk-…`), saved encrypted under the app's key in your macOS Keychain and shown as `(set, encrypted)`. Typing (masked) replaces it; an empty entry clears it. It goes only to api.openai.com, never to a local server or the Anthropic API.",
        [SettingsField.ThemedExternalWindows] = "The app's own windows wear the theme: on a Mac the picture viewer, the thumbnail browser and the log and process windows, in the theme's colours with a dark or light title bar to match (the picture menu is the system's, dark or light with the window). When off, they stay black.",
        [SettingsField.HomeAssistantToken] = "A long-lived access token (in Home Assistant: your profile → Security → Long-lived access tokens). It is typed into a masked field and saved encrypted under the app's key in your macOS Keychain; empty clears it. Never written to the log.",
        [SettingsField.YouTubeApiKey] = "Your YouTube Data API v3 key, for searching. Make one in the Google Cloud Console: create a project, enable YouTube Data API v3 (APIs & Services › Library), create an API key under Credentials and restrict it to that API, with no application restriction. A search costs 100 of the project's 10,000 free units a day (about 100 searches). Typed into a masked field and saved encrypted under the app's key in your macOS Keychain; empty clears it. `/keycopy` copies it and a plain `/profile reset` keeps it. Never written to the log.",
        [SettingsField.PdfEngine] = "What makes a PDF for `convert_to_pdf` and `/pdf`. `auto` and `browser`: Edge, Chrome, Brave or Chromium from `/Applications`. `printer` (Microsoft Print to PDF) needs Windows.",
        [SettingsField.UncTools] = "Offers the UNC tools over the shares in `unc.json`. UNC shares need Windows, so on a Mac the group is never offered and `/tools` shows it as off: it needs Windows.",
        [SettingsField.DockerTools] = "Offers the Docker tools over Docker Desktop's engine pipe. They need Windows for now, so on a Mac the group is never offered and `/tools` shows it as off: it needs Windows.",
        [SettingsField.SqlAddConnection] = "A wizard for a new connection, one page per choice, or to edit a saved one. It signs in with a SQL login or, through Kerberos, with Windows sign-in as you (your ticket, no password); runas needs Windows. It can **test** the draft (`SELECT @@VERSION`) before saving it. With entries saved, its first page lists them: + New, or one to edit, which opens on its summary prefilled (Enter on a row changes it; its file kept, the stored password kept unless another is typed). See Managing connections.",
        [SettingsField.SttInput] = "Turns the microphone on: the push-to-talk key records a spoken message (`/stt`). macOS asks once whether your terminal app (Terminal, iTerm2…) may use the microphone; if it was refused, turn it on in System Settings › Privacy & Security › Microphone. A MacBook's own microphone is off while its lid is closed: pick another input in System Settings › Sound.",
        [SettingsField.PrintTools] = "Offers `list_printers` and `print_file` to the model. Printing needs Windows for now, so on a Mac the group is not offered.",
        [SettingsField.PrintDefaultPrinter] = "The printer a print goes to when none is named. Printing needs Windows for now.",
        [SettingsField.ShowPerformanceBar] = "A checklist of the meters the bar under the toolbar shows, updated once a second: **CPU**, **RAM** (what Activity Monitor calls Memory Used: app memory, wired and compressed), **GPU** (the graphics processor's load), **GMEM** (a Mac's GPU has no memory of its own: the memory it holds now, as a share of the part macOS lets it use, about two thirds of a 16 GB Mac's), **NET** (network use, as a share of the link speed; on Wi-Fi the radio's current rate), **NET↓** and **NET↑** (the download and upload rates), and **PROC** (the model's background processes still running; `/process` lists them). A meter the machine cannot read is left out. Enter or Space flips one; with none checked there is no bar. **A** / **N** / **D** check all, none or the default four (CPU, RAM, GPU and GMEM). The look is on the same screen's title row: **text** (T), **gauge** (G), **spark** (S) or **led** (L). `/perfbar` or the toolbar's 📈 hides the bar, or shows it again.",
        [SettingsField.OracleSetPassword] = "Pick a connection and type its password, masked. It is saved to that connection's store: encrypted in its `oracle.json`, or in the macOS Keychain.",
        [SettingsField.MySqlSetPassword] = "Pick a connection and type its password, masked. It is saved to that connection's store: encrypted in its `mysql.json`, or in the macOS Keychain.",
        [SettingsField.UncSetPassword] = "Pick a runas share and type its password, masked. UNC shares need Windows, so on a Mac the UNC tools are not offered.",
        [SettingsField.EmbeddedBackend] = "Which llama.cpp build runs the model. On a Mac there is one, Metal, which uses the Apple Silicon GPU: `auto` and `metal` both pick it. The row shows what `auto` picked.",
        [SettingsField.EmbeddedVramBudget] = "How much of the GPU's memory the embedded server may fill: `off` (llama.cpp leaves 1 GiB free) or 50–99 % of it, leaving the rest free for other programs. On a Mac the GPU shares the computer's memory, and the budget is a share of the part macOS lets the GPU use (about two thirds of the memory on a 16 GB Mac). A profile that saved `off` keeps it. The budget only adjusts what is left to fit: *Embedded context size* 0 (the context shrinks first) and *Embedded GPU layers* `auto` (then layers move to the CPU). If you set a context too big for the budget, layers are all it can move and replies slow down sharply, so it works best with context 0. The budget is measured when the server starts; memory other programs take later is not held back. A change restarts the server.",
        [SettingsField.EmbeddedVramOnly] = "Keeps the embedded model on the GPU. Every layer goes on the GPU (whatever *Embedded GPU layers* says; with *Embedded context size* 0 the context shrinks to fit instead). If llama.cpp still leaves layers on the CPU, or a buffer could not be allocated, the app stops the server and the connect fails with what to lower. On a Mac the GPU shares the computer's memory, so this checks where the layers went, not which memory holds them. Refused when llama-server does not report where it put the layers, so the load cannot be checked. A profile saved while the default was off keeps it off.",
        [SettingsField.ImageEditQuality] = "The quality `image_edit` writes a JPEG or HEIF at when the model gives none (1–100), and where `max_kb` starts lowering it from. A Mac's ImageIO writes no JPEG XL.",
        [SettingsField.DockerEnginePipe] = "The Docker engine's named pipe (Docker Desktop on Windows). The Docker tools need Windows for now, so on a Mac it changes nothing.",
        [SettingsField.CameraDevice] = "The camera the camera tool and `/camera` use. The camera needs Windows for now, so on a Mac it changes nothing.",
        [SettingsField.ScreenTools] = "Offers `screen_capture` and `screen_list`, which let the model see a monitor, every monitor or one window. Never offered without the pane, headless, or to an embedded model without its vision projector. `/screen` works either way. On a Mac the terminal app the app runs in (Terminal, iTerm2…) needs the Screen Recording permission: macOS asks once, at the first capture; turn it on in " + Screen.ScreenText.PermissionPage + ", then quit and reopen the terminal. Without it nothing is captured and the windows aren't listed.",
        [SettingsField.PostgresSetPassword] = "Pick a connection and type its password (masked); it is saved encrypted in postgres.json or in the macOS Keychain, as the connection says.",
    };

    /// <summary>
    /// <c>/terminal</c> on a Mac (2026-10-07): <c>open</c> starts the terminal the app runs in when it is Terminal or iTerm2, else
    /// Terminal; Terminal opens a window, iTerm2 a tab in its front window (iTerm2's own choice); Ctrl+] for Ctrl+., which a Mac
    /// terminal sends as a bare ".". Pinned.
    /// </summary>
    public const string TerminalForm = "Open a new terminal in the working directory, or in a folder under it (Tab completes the folder): a new Terminal window, or a new tab in iTerm2's front window when the app runs in iTerm2 (iTerm2 decides; its settings can make it a window). Another terminal app opens Terminal. Ctrl+] runs it too (a Mac terminal sends Ctrl+. as a plain dot).";

    /// <summary>The command forms whose text differs on a Mac, by syntax as <see cref="HelpCommands"/> writes it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Forms = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["/camera"] = "Open the camera pane and take a photo. The camera needs Windows for now: this build has no camera support.",
        ["/camera list"] = "List the cameras in a pane, numbered, the chosen one marked. The camera needs Windows for now.",
        ["/keycheck"] = Hotkeys.KeyCheckText.Unsupported,
        ["/view <image>|<folder>"] = "Open an image from the working directory in the picture viewer, or a folder there on its newest picture. In the viewer the arrows, the mouse wheel or a two-finger swipe up and down step through the pictures, and a right-click (or Control-click) opens the picture menu (rotate, flip, colour, resize, convert, shrink, copy the path, show in Finder, attach, delete; edits go where *Image edit mode* says); ⌃⌘F is full screen (F11 too, if macOS's Show Desktop shortcut is turned off), ⌫ twice deletes, Esc or ⌘W closes. Drag the picture out to copy its file; TAB brings the terminal forward, and a Ctrl or Option chord runs in the chat. It needs the Mac's desktop (not over SSH). Works while a reply runs.",
        ["/view <image>|<folder> --thumbs"] = "Open the folder (an image's folder, with the image selected) as thumbnails in a window of its own, in step with the picture viewer and the picture strip: a click shows the picture in the viewer, a double-click or Enter opens it there, and the viewer's own moves select it here. New pictures go on the end, so nothing moves. The tiles fit the window; + and −, ⌘ with the wheel or a pinch resize them, F5 lists and fits again. A right-click (or Control-click) opens the picture menu, as in the viewer. `--thumbs` can be the first or last word. It needs the Mac's desktop (not over SSH). Works while a reply runs.",
        ["/log"] = "Open the log window: this run's diagnostic lines, coloured by level, following the newest while it is at the bottom. Scrolling away pauses it; ⌘↓, Ctrl+E or scrolling back to the bottom follows again, ⌘↑ goes to the top. Drag to select, ⌘A selects all, ⌘C copies. ⌃⌘F is full screen, Esc leaves full screen and then closes it, as ⌘W does. TAB brings the terminal forward, and any Ctrl or Option chord runs in the chat as if pressed there (Ctrl+Option+G closes the window; Ctrl+C cancels a reply). It needs the Mac's desktop (not over SSH).",
        ["/process <id>"] = "Open the process window on one (any unique start of its id; Tab completes it): its output live, following the newest line while at the bottom, stderr in the warning colour, the title its state. Scroll, select and copy as in the log window (`/log`); TAB brings the terminal forward. `/process` with another id switches the window to that process in the same place. Ctrl+K twice within 3 seconds stops the process: the chat says it was stopped by you, and the model hears of it on its next turn.",
        ["/print <file> [printer=<name>] [copies=<n>] [pages=<range>] [landscape]"] = Printing.PrintText.NeedsWindows,
        ["/print reply [<options>]"] = Printing.PrintText.NeedsWindows,
        ["/print printers"] = Printing.PrintText.NeedsWindows,
        ["/pdf <file> [to=<path>] [paper=letter|a4|legal] [landscape] [overwrite]"] = "Make a PDF in the working directory from a file: Markdown keeps its headings, tables, lists, links, coloured code and the pictures beside it; text and code become a coloured listing; HTML is printed as the page, its scripts and anything outside the working directory left out; a picture is fitted to one page. The PDF goes beside the file unless `to=` names a file or folder; an existing one is replaced only with `overwrite`. Options go anywhere. Edge, Chrome, Brave or Chromium makes it. On its own, `/pdf` shows how to use it. See Making PDFs.",
        ["/terminal [<folder>]"] = TerminalForm,
        ["/model [<id>]"] = "Pick a model from the server's list, or set one by its id (as typed; the server is not asked). The list is A to Z, the cursor on the model in use; type to narrow it to the ids that hold the text, Backspace erases, ESC clears it, the next ESC keeps the model. On the embedded LLM, this lists the installed embedded models. Ctrl+D runs it too (a Mac terminal sends Ctrl+M as Enter).",   // 2026-10-07
        ["/screen"] = "Capture the monitor the app is on and put the screenshot on the input line as `[Image #N]`, at the screen's full pixels (a Retina screen at twice its points). It is saved in the *Screen capture output folder* (`screen_images` by default) and shown in the picture viewer under *Screen capture preview*. Your own command: *Screen capture tool* and *Screen capture ask* never apply. The terminal app the app runs in needs the Screen Recording permission (" + Screen.ScreenText.PermissionPage + "; quit and reopen the terminal after turning it on). See Screen capture.",
        ["/screen window:<id>|<title-words>"] = "Capture one window, even when another covers it: by its id from `/screen list`, or words of its title (or its app's name). A window on another desktop, or minimized, must be brought up first.",
        ["/perfbar [off|text|gauge|spark|led]"] = "Show or hide the performance bar (*Show performance bar*). On its own it hides the bar, or shows it again with the meters it last had (CPU, RAM, GPU and GMEM the first time; GMEM is the GPU's share of the Mac's memory); `off` hides it; a look name sets that look and shows the bar. Works while a reply runs; the toolbar's 📈 and Ctrl+F run it too.",   // 2026-10-07, GMEM
    };

    /// <summary><paramref name="field"/>'s text on a Mac, or null where <see cref="HelpSettings"/>' own serves both.</summary>
    public static string? Setting(SettingsField field) => Settings.TryGetValue(field, out string? text) ? text : null;

    /// <summary><paramref name="commands"/> with <see cref="Forms"/>' texts in place, every other form as it was.</summary>
    public static IReadOnlyList<CommandHelp> Apply(IReadOnlyList<CommandHelp> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return commands
            .Select(c => c with { Forms = c.Forms.Select(f => Forms.TryGetValue(f.Syntax, out string? text) ? f with { Meaning = text } : f).ToList() })
            .ToList();
    }
}
