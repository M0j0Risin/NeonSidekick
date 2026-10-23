using NeonSidekick.Plans;
using NeonSidekick.Printing;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

/// <summary>What a slash command from the Web UI's page comes to: it runs, it shows a pick card, it shows a list card, or it is refused.</summary>
public enum WebCommandKind
{
    Run,
    Pick,
    List,
    Refused,

    /// <summary>Opens on the page itself (later on 2026-10-15: bare <c>/prompts</c>, the page's saved prompts): answered by the session, never queued for the PC.</summary>
    Page,
}

/// <summary>
/// A verdict on a command from the page (later on 2026-10-14): its kind, the sentence a refusal shows, whether the page may send the line as a
/// message instead (<see cref="AsText"/>: an unknown command) and the line it may use instead (<see cref="Use"/>: <c>//tools</c> → <c>/tools</c>).
/// </summary>
public sealed record WebCommandVerdict(WebCommandKind Kind, string? Reason = null, bool AsText = false, string? Use = null)
{
    /// <summary>Whether the command may run from the page at the PC (a <see cref="WebCommandKind.Page"/> line opens on the page instead, never queued).</summary>
    public bool Allowed => Kind is not (WebCommandKind.Refused or WebCommandKind.Page);

    /// <summary>What the page opens for a <see cref="WebCommandKind.Page"/> verdict (later on 2026-10-15): <c>prompts</c>.</summary>
    public string? Open { get; init; }

    public static readonly WebCommandVerdict Run = new(WebCommandKind.Run);

    public static readonly WebCommandVerdict Pick = new(WebCommandKind.Pick);

    public static readonly WebCommandVerdict List = new(WebCommandKind.List);

    /// <summary>Bare <c>/prompts</c> from the page (later on 2026-10-15): the page's own saved prompts sheet; refused with its reason if it ever reached the PC.</summary>
    public static readonly WebCommandVerdict PromptsSheet = new(WebCommandKind.Page, WebUiText.OpensOnThePage) { Open = WebUiText.OpenPrompts };
}

/// <summary>
/// The settings a verdict reads: <i>Web UI commands</i>, the switches <c>/tree</c> and <c>/vault</c> need, and whether the page has its Shopping
/// tab (later on 2026-10-15: bare <c>/shopping</c> points at the tab, or at the setting while the tab is off) and (later on 2026-10-16) its
/// YouTube tab, for <c>/youtube list</c> (and the bare <c>/youtube</c> since 2026-10-18).
/// </summary>
public readonly record struct WebCommandSettings(bool Commands, bool FileTools, bool ObsidianTools, bool ShoppingTab = true, bool YouTubeTab = false);

/// <summary>
/// Which slash commands the Web UI's page may run (later on 2026-10-14, the user's calls): a phone has no PC pane to answer and no PC window to
/// look at, so a command runs from the page only where everything it shows reaches the page — its notices, a yes/no raced on the page and the
/// PC, a pick card (bare <c>/model</c>, <c>/reasoning</c>, <c>/rewind</c>), a list card (<c>/ha states</c>, <c>/docker logs</c>, the trees…).
/// Some are never the page's (<see cref="Never"/>: they end the app, change the server, run a shell line or switch the profile); every other
/// form that opens a pane or a window is refused with a hint at the form that works. Classed by the commands' own parsers, never by whether
/// the PC would open a pane. Pure. Read twice: when the page sends (<see cref="WebUiSession.Send"/>) and as the screen dispatches the line.
/// </summary>
public static class WebCommands
{
    /// <summary>The commands never run from the page, whatever their words.</summary>
    public static readonly IReadOnlySet<SlashCommand> Never = new HashSet<SlashCommand>
    {
        SlashCommand.Exit,
        SlashCommand.WebUi,
        SlashCommand.Cmd,
        SlashCommand.Profile,
        SlashCommand.Session,
        SlashCommand.Skills,
        SlashCommand.YouTube,
        SlashCommand.Queue,
        SlashCommand.BotChat,
        SlashCommand.Sampling,
        SlashCommand.Server,
        SlashCommand.Usage,
    };

    /// <summary>The commands some form of which may run from the page: the dropdown offers these alone.</summary>
    public static readonly IReadOnlySet<SlashCommand> Offered = new HashSet<SlashCommand>
    {
        SlashCommand.Help, SlashCommand.Clear, SlashCommand.New, SlashCommand.Splash, SlashCommand.Rewind, SlashCommand.Compact,
        SlashCommand.Model, SlashCommand.Reasoning, SlashCommand.Tts, SlashCommand.Voice, SlashCommand.Remember, SlashCommand.Memory,
        SlashCommand.Timer, SlashCommand.Cwd, SlashCommand.Tree, SlashCommand.Vault, SlashCommand.Browser, SlashCommand.Prompts,
        SlashCommand.Plan, SlashCommand.CodeAudit, SlashCommand.Process, SlashCommand.Learn, SlashCommand.Imagine, SlashCommand.Comfy, SlashCommand.HomeAssistant,
        SlashCommand.Docker, SlashCommand.EventLog, SlashCommand.Net, SlashCommand.Services, SlashCommand.Tasks, SlashCommand.Ad, SlashCommand.Monitor, SlashCommand.Camera, SlashCommand.Screen, SlashCommand.Shopping, SlashCommand.Print, SlashCommand.Pdf,
        SlashCommand.Rename, SlashCommand.Claude, SlashCommand.Advisor, SlashCommand.Test, SlashCommand.Avatar, SlashCommand.Loop,
        SlashCommand.MsgClear, SlashCommand.Mark,   // /mark (2026-10-18): the PC's rules, set from the page too
    };

    /// <summary>Whether any form of <paramref name="command"/> may run from the page (the dropdown's commands).</summary>
    public static bool Offers(SlashCommand command) => Offered.Contains(command);

    /// <summary>The verdict on <paramref name="line"/>, a line that parses as a command.</summary>
    public static WebCommandVerdict Check(string line, WebCommandSettings settings)
    {
        ArgumentNullException.ThrowIfNull(line);
        var (command, args) = SlashCommands.Parse(line);
        return Check(command, args, line, settings);
    }

    /// <summary>The verdict on <paramref name="command"/> with <paramref name="args"/>; <paramref name="line"/> names it in a refusal.</summary>
    public static WebCommandVerdict Check(SlashCommand command, string args, string line, WebCommandSettings settings)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(line);
        string word = Word(line);
        if (command == SlashCommand.None)
        {
            return WebCommandVerdict.Run;   // a message
        }

        if (command == SlashCommand.Unknown)
        {
            // A path or a word the app does not know: the page may send it as a message, and //word may be the command meant.
            string? use = ChatScreen.TypoSlashes(line, SlashCommands.Completions) is { } typo ? typo.Line.TrimEnd() : null;
            return new WebCommandVerdict(WebCommandKind.Refused, settings.Commands ? WebUiText.UnknownCommand(word) : WebUiText.CommandsOff, AsText: true, Use: settings.Commands ? use : null);
        }

        if (!settings.Commands)
        {
            return Refused(WebUiText.CommandsOff);
        }

        string text = args.Trim();
        string first = FirstWord(text);

        // /youtube list (and list --clear) point at the YouTube tab's saved list (later on 2026-10-16), the Shopping tab's way, and so does
        // the bare /youtube since 2026-10-18 (the user's ask: the bare word is the saved list now); every other /youtube form stays the PC's.
        if (command == SlashCommand.YouTube && (text.Length == 0 || Is(first, "list")))
        {
            return Refused(settings.YouTubeTab ? WebUiText.UseYouTubePage : WebUiText.YouTubeTabOff);
        }

        if (Never.Contains(command))
        {
            return Refused(WebUiText.PcOnly(word));
        }

        return command switch
        {
            SlashCommand.Overloaded => Refused(ChatScreen.NoArgumentError(word)),
            SlashCommand.Help => text.Length > 0 ? WebCommandVerdict.Run : Pane(word, "/help <words>"),
            SlashCommand.Splash when text.Length > 0 => Pane(word, "/splash"),   // 2026-10-18: view|thumbs|purge are a window or a question on the PC
            SlashCommand.Clear or SlashCommand.New or SlashCommand.Splash or SlashCommand.Compact or SlashCommand.Remember or SlashCommand.Timer or SlashCommand.Mark
                or SlashCommand.Claude or SlashCommand.Advisor or SlashCommand.Test or SlashCommand.Pdf or SlashCommand.Imagine => WebCommandVerdict.Run,
            // 2026-10-16, the user's ask: the page shares the message history now, so it may clear it; the yes is asked on the page and the PC.
            SlashCommand.MsgClear => WebCommandVerdict.Run,
            SlashCommand.Rewind => WebCommandVerdict.Pick,
            SlashCommand.Model or SlashCommand.Reasoning => text.Length > 0 ? WebCommandVerdict.Run : WebCommandVerdict.Pick,
            // Bare /plan open and /plan open --chat are the plans picker (2026-10-18), a pick card on the page; a list until then. /plan edit
            // (2026-10-20) opens an editor on the PC: refused, unless its several words are detail or a requirement (the handler's fallback).
            SlashCommand.Plan => ChatScreen.ParsePlanArgs(args, planning: false, out string planRest) switch
            {
                ChatScreen.PlanCommand.List or ChatScreen.PlanCommand.ListChat => WebCommandVerdict.Pick,
                ChatScreen.PlanCommand.ListEdit => Refused(WebUiText.PcOnly("/plan edit")),
                ChatScreen.PlanCommand.Edit when !planRest.Any(char.IsWhiteSpace) => Refused(WebUiText.PcOnly("/plan edit")),
                _ => WebCommandVerdict.Run,
            },
            // 2026-10-18: the reports' list is lines; a report, a fix, a resume and an audit run (their pane asked on the page too).
            SlashCommand.CodeAudit => Is(first, Audits.AuditText.OpenWord) && FirstWord(text[first.Length..].Trim()).Length == 0 ? WebCommandVerdict.List : WebCommandVerdict.Run,
            SlashCommand.Learn => Is(first, LearnSessionsWord) ? Pane(word, "/learn [<note>]") : WebCommandVerdict.Run,
            SlashCommand.Print => text.Length == 0 || Is(text, PrintText.PrintersWord) ? WebCommandVerdict.List : WebCommandVerdict.Run,
            SlashCommand.Avatar => Emojis.AvatarCommand.ParseLine(args).Verb is Emojis.AvatarVerb.Set or Emojis.AvatarVerb.Clear ? WebCommandVerdict.Run : Pane(word, "/avatar <emoji>|clear"),
            SlashCommand.Rename => text.Length > 0 ? WebCommandVerdict.Run : Pane(word, "/rename <name>"),
            SlashCommand.Tts or SlashCommand.Voice => Is(text, "on") || Is(text, "off") ? WebCommandVerdict.Run : Pane(word, word + " on|off"),
            SlashCommand.Memory => MemoryModes.Any(m => Is(text, m)) ? WebCommandVerdict.Run : Pane(word, "/memory read-write|read-only|off"),
            SlashCommand.Cwd => text.Length == 0 ? WebCommandVerdict.Run : Refused(WebUiText.PcOnly(word + " <path>")),
            SlashCommand.HomeAssistant => Is(first, "states") ? WebCommandVerdict.List : WebCommandVerdict.Run,
            SlashCommand.Docker => Is(first, "logs") ? WebCommandVerdict.List : WebCommandVerdict.Run,
            SlashCommand.EventLog => WebCommandVerdict.List,   // 2026-10-15: the events as a list card, /docker's way from a web line
            SlashCommand.Net or SlashCommand.Services or SlashCommand.Tasks or SlashCommand.Ad => WebCommandVerdict.List,   // 2026-10-15, the admin-tools plan: their lines as a list card
            SlashCommand.Shopping => text.Length == 0 || Is(text, "add") ? Refused(settings.ShoppingTab ? WebUiText.UseShoppingPage : WebUiText.ShoppingTabOff) : WebCommandVerdict.Run,
            SlashCommand.Prompts => PromptsCommand.Parse(args).Verb switch
            {
                PromptsVerb.Pane => WebCommandVerdict.PromptsSheet,   // later on 2026-10-15: the page's own sheet
                PromptsVerb.ClearList => Pane(word, "/prompts list|<n>|save <text>|remove <n>"),
                _ => WebCommandVerdict.Run,
            },
            SlashCommand.Browser => BrowserCommand.Parse(args).Verb is BrowserVerb.Save or BrowserVerb.Unsave ? WebCommandVerdict.Run : Pane(word, "/browser save [<url> [<name>]]|unsave <n>"),
            SlashCommand.Camera => Camera.CameraCommand.Parse(args).Verb == Camera.CameraVerb.List ? WebCommandVerdict.List : Pane(word, "/camera list"),
            SlashCommand.Screen => Is(text, "list") ? WebCommandVerdict.List : Pane(word, "/screen list"),
            // 2026-10-23, the monitors plan: the list as a card; the pane and every change are the PC's (a switch away could take the PC's own screen).
            SlashCommand.Monitor => Is(text, Monitors.MonitorCommand.ListWord) ? WebCommandVerdict.List : Pane(word, Monitors.MonitorCommand.Word + " " + Monitors.MonitorCommand.ListWord),
            SlashCommand.Tree => settings.FileTools ? WebCommandVerdict.List : Refused(WebUiText.Needs(word, "File tools")),
            SlashCommand.Vault => settings.ObsidianTools ? WebCommandVerdict.List : Refused(WebUiText.Needs(word, "Obsidian tools")),
            SlashCommand.Process => text.Length == 0 ? WebCommandVerdict.List : Pane(word, "/process"),
            // 2026-10-18: the bare word is /tools comfy (a page on the PC); the status it showed is /comfy status, a list as before.
            SlashCommand.Comfy => Is(text, ChatScreen.ComfyStatusWord) || Is(text, ChatScreen.ComfyOfferedWord) ? WebCommandVerdict.List : Pane(word, "/comfy status or /comfy offered"),
            SlashCommand.Loop => LoopVerdict(args, settings),
            _ => Pane(word, null),
        };
    }

    /// <summary>
    /// <c>/loop</c> from the page (later on 2026-10-15, the user's ask; it was never the page's until then): a loop of messages runs; a looped
    /// command is the page's own verdict on its inner line, but only for the commands the PC loops (<see cref="ChatScreen.LoopableCommands"/>:
    /// <c>/imagine</c> runs, <c>/read</c> is refused with its own reason). Every other line runs and meets the PC's own checks — a usage error, an
    /// unknown command, one that can't be looped — whose words land on the page; the page's <i>send as text</i> offer is never borrowed, as it
    /// would send the whole <c>/loop</c> line to the model.
    /// </summary>
    private static WebCommandVerdict LoopVerdict(string args, WebCommandSettings settings)
    {
        if (!ChatScreen.TryParseLoopArgs(args, out _, out _, out string message) || !message.StartsWith('/'))
        {
            return WebCommandVerdict.Run;
        }

        var (inner, innerArgs) = SlashCommands.Parse(message);
        if (!ChatScreen.LoopableCommands.Contains(inner))
        {
            return WebCommandVerdict.Run;
        }

        var verdict = Check(inner, innerArgs, message, settings);
        return verdict.Allowed ? WebCommandVerdict.Run : Refused(WebUiText.LoopRefused(verdict.Reason ?? ""));
    }

    /// <summary><c>/learn</c>'s word for its stored sessions' form.</summary>
    public const string LearnSessionsWord = "sessions";

    /// <summary>The words <c>/memory</c> takes for a mode.</summary>
    public static readonly IReadOnlyList<string> MemoryModes = ["read-write", "read-only", "off", "on"];

    private static WebCommandVerdict Refused(string reason) => new(WebCommandKind.Refused, reason);

    private static WebCommandVerdict Pane(string word, string? works) => Refused(WebUiText.OpensPane(word, works));

    private static bool Is(string word, string expected) => string.Equals(word, expected, StringComparison.OrdinalIgnoreCase);

    private static string FirstWord(string text)
    {
        int end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return text[..end];
    }

    /// <summary>The command's word as typed: the line's first word.</summary>
    public static string Word(string line) => FirstWord((line ?? "").Trim());
}
