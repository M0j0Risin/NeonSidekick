using System.Text;
using NeonSidekick.App;
using NeonSidekick.Mcp;

namespace NeonSidekick.Help;

/// <summary>
/// The words of <c>neon_help</c> (2026-10-02, the user's ask: a tool that answers how-to questions about the app itself): the
/// <c>/tools</c> group's title, the result's headings and layout, the overview, the misses. The <see cref="ToolsText"/> shape —
/// pure statics, the model reads every line, so the layout is plain text (no Markdown tables) and every heading is pinned. Since
/// 2026-10-25 (the neon_help plan, the user's call) also the tool answers, the synonyms and the live "Right now:" lines (<c>Live*</c>),
/// which say whether a tool is offered and why not, never a setting's value.
/// </summary>
public static class HelpText
{
    /// <summary>The group <c>neon_help</c> sits in on <c>/tools</c>' Offered tab and <c>/sys</c>: its switch <c>Help tools</c> on <c>/tools</c> › Core › Help since 2026-10-18 (none until then), the tool switched off by name too. Pinned.</summary>
    public const string GroupTitle = "Help";

    /// <summary>Between a pane, a tab, a menu and a row in a setting's place: <c>/tools › Devices › Camera › Camera tool</c>.</summary>
    public const string PathSeparator = " › ";

    /// <summary>The cap on one answer, in characters: the command list (<see cref="CommandList"/>) and the longest tab fit, and a broad query is cut with <see cref="CutNote"/>.</summary>
    public const int MaxChars = 9000;

    /// <summary>How many matches a free-word query lists, best first.</summary>
    public const int MaxMatches = 8;

    // The panes' one-liners: README's Available Panes, kept short.
    public const string SettingsPaneSummary = "the app's settings, in five tabs, three with a second row of tabs: Client (Chat: the profile and the screen; Sessions; Botchat; Web UI), Servers (Common: what every LLM server reads; Http: what only an OpenAI-compatible HTTP server reads; Embedded, Docker, Anthropic with the Claude Code server and /claude, OpenAI), Advisors (the profile advisors), Automation (plan mode's gap review, the Ralph loop and code audits) and Voice (TTS: speech output; STT: voice input)";
    public const string ToolsPaneSummary = "the model's tools: Offered lists every tool, on or off (Enter flips one); Core, Files, Apps, Admin, Databases, Devices and Home hold each tool group's settings, one menu per group on a second row of tabs (←/→ step through every menu, Tab skips to the next main tab), with the pane's own options under Core › Options";
    public const string SkillsPaneSummary = "agent skills (SKILL.md folders): Offered lists and manages the installed skills, Reflection sets how the model writes and improves skills on its own, Options holds the skill switches and the project file";
    public const string McpPaneSummary = "external MCP servers: Servers lists and edits them, Tools shows what each offers, Options holds the master switch and the timeout";

    /// <summary>The working directory's default, the profile's own folder (<see cref="HelpLocation.Default"/>).</summary>
    public const string WorkingDirectoryDefault = @"(the profile's files folder: <home>\profiles\<profile>\files)";

    /// <summary>A default the menu shows as nothing (<see cref="HelpLocation.Default"/>). Pinned.</summary>
    public const string EmptyDefault = "(empty)";

    /// <summary>The <c>kind</c> words the tool takes (2026-10-25: <c>tool</c>, <c>interface</c>, <c>startup</c> and <c>files</c> joined the first five).</summary>
    public static readonly IReadOnlyList<string> Kinds = ["any", "command", "setting", "pane", "keys", "tool", "interface", "startup", "files"];

    /// <summary>
    /// Other words a model writes for a kind (2026-10-25: the Responses client sends tools with <c>strict: false</c>, so the enum is a hint):
    /// <c>tools</c>, <c>file</c>, <c>bar(s)</c>, <c>flag(s)</c>, <c>variable(s)</c> and <c>env</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KindAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tools"] = "tool", ["file"] = "files", ["bar"] = "interface", ["bars"] = "interface",
        ["flag"] = "startup", ["flags"] = "startup", ["variable"] = "startup", ["variables"] = "startup", ["env"] = "startup",
    };

    /// <summary><c>Unknown kind 'x'; use any, command, setting, pane or keys.</c> Pinned.</summary>
    public static string BadKind(string kind) => $"Unknown kind '{kind}'; use {string.Join(", ", Kinds.Take(Kinds.Count - 1))} or {Kinds[^1]}.";

    /// <summary>A query nothing matched. Pinned.</summary>
    public static string NothingFound(string query) =>
        $"Nothing in NeonSidekick's help matches '{query}'. Try other words, a command (/camera), a setting's name, a pane or tab (/tools camera), a tool name, a toolbar icon, a `--flag`, a file (`sql.json`), or no query at all for the overview.";

    /// <summary>The line after an answer cut at <see cref="MaxChars"/>. Pinned.</summary>
    public const string CutNote = "… (cut short; ask about one command, setting or tab for the rest)";

    /// <summary>The overview's heading. Pinned.</summary>
    public const string OverviewHeading = "NeonSidekick help: the panes, their tabs, and every slash command.";

    /// <summary>The overview's last line: how to ask for more. Pinned.</summary>
    public const string OverviewTail = "Ask neon_help about one command (/camera), a setting by name, a pane or tab (/tools camera), keys, a tool, a bar or toolbar icon, a `--flag`, a variable or a file, or in plain words; kind command, tool, interface, startup or files with no query lists them.";

    /// <summary>The command list's heading (<c>kind: command</c>, no query). Pinned.</summary>
    public const string CommandListHeading = "Slash commands (type / to list them; ask about one for every form):";

    /// <summary>The keys answer's heading. Pinned.</summary>
    public const string KeysHeading = "Keys (also under /help › Keys):";

    /// <summary>The line after the keys: the voice keys depend on settings. Pinned.</summary>
    public const string KeysTail = "With STT input on, the STT push-to-talk key records a message; with STT wake on, saying the wake phrase starts listening.";

    /// <summary>The free-word answer's heading. Pinned.</summary>
    public static string MatchesHeading(string query) => $"Best matches for '{query}':";

    /// <summary>
    /// What <c>/help &lt;words&gt;</c> sends the model (2026-10-14, the user's ask: <c>/help claude</c> was refused, and the user typed
    /// <c>$neon_help claude</c> to get the answer): the words first, in quotes, so the stored message still reads as the question
    /// in <c>/rewind</c> and a session search. Pinned.
    /// </summary>
    public static string AskPrompt(string words) =>
        $"Look up \"{words.Trim()}\" in NeonSidekick's own manual with neon_help (search again with other words if the first answer misses) and answer from what it returns.";

    /// <summary>
    /// The notice ahead of the manual's own answer to <c>/help &lt;words&gt;</c> when a model is connected but not offered <c>neon_help</c>
    /// (the tools off, or the tool off on <c>/tools</c>), so the user knows why the model was not asked (2026-10-14).
    /// </summary>
    public const string NotAskedNotice = "neon_help is off, so this is the manual's own answer; /tools turns it on.";

    /// <summary>
    /// The overview: each pane with its tabs (<see cref="HelpLocation.Panes"/>), then every command's word on one line
    /// (<see cref="SlashCommands.HelpEntries"/>; a line on each is <see cref="CommandList"/>, which alone runs past 8,000
    /// characters — too much for a small model's context on a first look), then <see cref="OverviewTail"/>.
    /// </summary>
    public static string Overview(IReadOnlyList<HelpPane> panes, IReadOnlyList<SlashCommands.HelpEntry> commands)
    {
        ArgumentNullException.ThrowIfNull(panes);
        ArgumentNullException.ThrowIfNull(commands);
        var sb = new StringBuilder(OverviewHeading).Append("\n\nPanes:\n");
        foreach (var pane in panes)
        {
            sb.Append("- ").Append(pane.Command).Append(": ").Append(pane.Summary).Append(". Tabs: ").Append(string.Join(", ", pane.Tabs)).Append('\n');
        }

        sb.Append("\nSlash commands: ").Append(string.Join(", ", commands.Select(e => e.Label))).Append('\n');
        return sb.Append('\n').Append(OverviewTail).ToString();
    }

    /// <summary>Every command with the first sentence of its <c>/help</c> summary: <c>- /camera: Open the camera pane …</c>.</summary>
    public static string CommandList(IReadOnlyList<SlashCommands.HelpEntry> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var sb = new StringBuilder(CommandListHeading);
        foreach (var entry in commands)
        {
            sb.Append("\n- ").Append(entry.Label).Append(": ").Append(FirstSentence(entry.Summary));
        }

        return sb.ToString();
    }

    /// <summary>A command with every form: <c>/camera</c>, then <c>- /camera snap: Take a photo …</c> per form.</summary>
    public static string Command(CommandHelp command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var sb = new StringBuilder("Command ").Append(command.Command).Append(':');
        foreach (var form in command.Forms)
        {
            sb.Append("\n- ").Append(form.Syntax).Append(": ").Append(form.Meaning);
        }

        return sb.ToString();
    }

    /// <summary>A setting in full: its name, where it is, its default and what it does. Pinned.</summary>
    public static string Setting(HelpRow row, string defaultValue, string description)
    {
        ArgumentNullException.ThrowIfNull(row);
        string menu = row.Menu is null ? "" : $", then {row.Menu} on the second row of tabs";   // "Enter on" the menu until 2026-10-10, when the menus became sub-tabs
        return $"Setting {row.Label}\n  Where: {row.Path} (open {row.Pane}, go to the {row.Tab} tab{menu}, Enter on the row edits it)\n  Default: {defaultValue}\n  What it does: {description}";
    }

    /// <summary>A pane: its summary, then each tab with its rows' names (a tab without settings rows says what it lists).</summary>
    public static string Pane(HelpPane pane, Func<string, IReadOnlyList<HelpRow>> rowsOf)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(rowsOf);
        var sb = new StringBuilder("Pane ").Append(pane.Command).Append(": ").Append(pane.Summary).Append('.');
        foreach (string tab in pane.Tabs)
        {
            var rows = rowsOf(tab);
            var menus = rows.Select(r => r.Menu).OfType<string>().Distinct().ToList();
            sb.Append("\n- ").Append(tab).Append(": ").Append(
                menus.Count > 0 ? MenusTab(menus)
                : rows.Count > 0 ? string.Join(", ", rows.Select(r => r.Label))
                : ListTab(pane.Command, tab));
        }

        return sb.ToString();
    }

    /// <summary>
    /// What a group tab holds (2026-10-09): <c>the menus Print, Camera, … (a second row of tabs under the tab; ←/→ step through them)</c>. Pinned. "Enter opens
    /// one, each a strip of its rows" until 2026-10-10, when the menus became sub-tabs.
    /// </summary>
    public static string MenusTab(IReadOnlyList<string> menus)
    {
        ArgumentNullException.ThrowIfNull(menus);
        return "the menus " + string.Join(", ", menus) + " (a second row of tabs under the tab; ←/→ step through them)";
    }

    /// <summary>
    /// A group tab (2026-10-09, <c>/tools</c>' Media and Database): its menus, then each menu's rows' names on a line of its own, and how to
    /// ask for one menu's whole answer — the five menus' rows with every default and sentence would not fit <see cref="MaxChars"/>. Pinned.
    /// </summary>
    public static string GroupTab(HelpPane pane, string tab, IReadOnlyList<HelpRow> rows)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(rows);
        var menus = rows.GroupBy(r => r.Menu ?? "").ToList();
        var sb = new StringBuilder("Tab ").Append(pane.Command).Append(PathSeparator).Append(tab).Append(": ").Append(MenusTab(menus.Select(m => m.Key).ToList()));
        foreach (var menu in menus)
        {
            sb.Append("\n- ").Append(menu.Key).Append(": ").Append(string.Join(", ", menu.Select(r => r.Label)));
        }

        return sb.Append("\nAsk for one menu (").Append(pane.Command).Append(' ').Append(menus[0].Key.ToLowerInvariant()).Append(") for its rows' defaults and what each does.").ToString();
    }

    /// <summary>A tab's rows, each with its default and the first sentence of what it does (a menu's on a group tab too, <paramref name="tab"/> then <c>Database › SQL</c>).</summary>
    public static string Tab(HelpPane pane, string tab, IReadOnlyList<HelpRow> rows, Func<SettingsField, string> defaultOf, Func<SettingsField, string> describe)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(defaultOf);
        ArgumentNullException.ThrowIfNull(describe);
        var sb = new StringBuilder("Tab ").Append(pane.Command).Append(PathSeparator).Append(tab).Append(':');
        if (rows.Count == 0)
        {
            return sb.Append(' ').Append(ListTab(pane.Command, tab)).ToString();
        }

        foreach (var row in rows)
        {
            sb.Append("\n- ").Append(row.Label).Append(" (default ").Append(defaultOf(row.Field)).Append("): ").Append(FirstSentence(describe(row.Field)));
        }

        return sb.ToString();
    }

    /// <summary>The keys, one per line: <c>- Ctrl+H: open the help (/help)</c>.</summary>
    public static string Keys(IEnumerable<(string Key, string Meaning)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder(KeysHeading);
        foreach (var (key, meaning) in rows)
        {
            sb.Append("\n- ").Append(key).Append(": ").Append(meaning);
        }

        return sb.Append('\n').Append(KeysTail).ToString();
    }

    /// <summary>One key on its own: <c>Key Ctrl+H: open the help (/help)</c>.</summary>
    public static string Key(string key, string meaning) => $"Key {key}: {meaning}";

    /// <summary>What a tab without settings rows lists (<c>/tools</c>' and <c>/skills</c>' Offered, <c>/mcp</c>' Servers and Tools).</summary>
    public static string ListTab(string pane, string tab) => (pane, tab) switch
    {
        ("/tools", ToolsText.OfferedTabTitle) => "every tool the model can be offered, by group, each on or off; Enter or Space flips the tool under the cursor (a tool switched off is never offered)",
        ("/skills", SkillsText.OfferedTabTitle) => "the installed skills, all of them or one scope's (the all and profile sub-tabs, global while Global skills is on, external while one is listed); Enter on one moves, renames, edits, reverts, locks, disables or deletes it, and /skills add installs more",
        ("/mcp", McpText.ServersTabTitle) => "the MCP servers from mcp.json (global and this profile's); add, edit, switch on or off and reconnect them here",
        ("/mcp", McpText.ToolsTabTitle) => "the tools each connected MCP server offers, switched on or off one by one; with two servers or more, all of them or one server's on its sub-tab",
        _ => "no settings rows",
    };

    /// <summary>The tool answer's heading: <c>Tool `web_fetch`: Fetches a web page …</c>. Pinned.</summary>
    public static string ToolHeading(string name, string does) => $"Tool `{name}`: {does}";

    /// <summary>
    /// One tool in full (2026-10-25, the neon_help plan's Step 1): what it does, its group, its switch and where it is, plan mode, headless,
    /// the pane, its needs and setup, the Mac's line, and the live line last (so a cut never leaves it without its tool). Pinned by heading.
    /// </summary>
    public static string Tool(string name, string does, string group, string? switchLine, bool planMode, bool headless, bool needsPane, string? needs, string? setup, string? mac, string? live)
    {
        var sb = new StringBuilder(ToolHeading(name, does)).Append("\n  Group: ").Append(group);
        if (switchLine is not null)
        {
            sb.Append("\n  Switch: ").Append(switchLine);
        }

        sb.Append("\n  Plan mode: ").Append(planMode ? "offered (it only reads)" : "not offered");
        sb.Append("\n  Headless: ").Append(headless ? "offered" : "not offered (it needs the screen)");
        if (needsPane)
        {
            sb.Append("\n  Needs the pane: yes, it asks the user there");
        }

        if (needs is not null)
        {
            sb.Append("\n  Needs: ").Append(needs);
        }

        if (setup is not null)
        {
            sb.Append("\n  ").Append(switchLine is null ? "Offered: " : "Setup: ").Append(setup);
        }

        if (mac is not null)
        {
            sb.Append("\n  On a Mac: ").Append(mac);
        }

        if (live is not null)
        {
            sb.Append("\n  ").Append(live);
        }

        return sb.ToString();
    }

    /// <summary>A switch line: <c>Web tools at /tools › Core › Web › Web tools (on by default)</c>.</summary>
    public static string SwitchLine(string label, string path, bool onByDefault, bool toolStartsOff) =>
        $"{label} at {path} ({(onByDefault ? "on" : "off")} by default{(toolStartsOff ? "; this tool itself starts switched off on /tools › Offered" : "")})";

    /// <summary>The tool list's heading (<c>kind: tool</c>, no query). Pinned.</summary>
    public const string ToolListHeading = "Tools by group (ask about one by name for its switch, its needs and whether it is offered):";

    /// <summary>The tool list's last line: the MCP servers' tools. Pinned.</summary>
    public const string ToolListMcpLine = "- MCP: each connected server's tools, named `server__tool` (/mcp)";

    /// <summary>A group answer's heading: <c>Tool group Camera:</c>. Pinned.</summary>
    public static string ToolGroupHeading(string group) => $"Tool group {group}:";

    /// <summary>What a group switch's setting answer adds: <c>Tools: `web_fetch`, `web_search`…</c>.</summary>
    public static string ToolsLine(IEnumerable<string> names) => "  Tools: " + string.Join(", ", names.Select(n => $"`{n}`"));

    /// <summary>The MCP tool answer's heading. Pinned.</summary>
    public static string McpToolHeading(string name) => $"MCP tool `{name}`: a tool an MCP server offers, named after its server: the server's name, two underscores, then the tool's.";

    /// <summary>An MCP tool (2026-10-25): the app's own facts only, never the server's description.</summary>
    public static string McpTool(string name, string? server, string switchLine, string? live)
    {
        var sb = new StringBuilder(McpToolHeading(name));
        if (server is not null)
        {
            sb.Append("\n  Server: `").Append(server).Append('`');
        }

        sb.Append("\n  Where: /mcp › Tools switches each tool on or off; /mcp › Servers lists the servers")
            .Append("\n  Switch: ").Append(switchLine)
            .Append("\n  Plan mode: not offered\n  Headless: offered");
        if (live is not null)
        {
            sb.Append("\n  ").Append(live);
        }

        return sb.ToString();
    }

    /// <summary>The see-also line after a command whose word is a tool's too (<c>/copy</c>). Pinned.</summary>
    public static string SeeAlsoTool(string tool) => $"See also: the tool `{tool}` (ask \"{tool} tool\").";

    /// <summary>
    /// Single words a question may use for the word the manual uses (2026-10-25, the neon_help plan's Step 5), nothing ambiguous. Left out:
    /// password or key for api key, task, event, and "dark" ("dark background" is the help's own phrase for an off toolbar item).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Synonyms = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["mic"] = "stt", ["microphone"] = "stt", ["voice"] = "tts", ["speak"] = "tts",
        ["color"] = "theme", ["colour"] = "theme", ["hotkey"] = "key", ["shortcut"] = "key",
        ["llm"] = "model", ["model"] = "llm", ["glyph"] = "icon", ["ldap"] = "directory", ["traceroute"] = "trace",
        ["dns"] = "network", ["ping"] = "network", ["certificate"] = "network",
    };

    // The live line (2026-10-25, the neon_help plan's Step 2), pinned: offered, or not offered and the one reason why.
    public const string LiveOffered = "Right now: offered.";
    public const string LiveToolsOff = SystemPromptSummary.ToolsOffSuffix;
    public const string LiveNotHeadless = "a headless run never offers it";
    public const string LivePlanMode = "plan mode offers only the tools that read";
    public const string LiveOffByName = "switched off on /tools › Offered";
    public const string LiveMcpOffByName = "switched off on /mcp › Tools";
    public const string LiveNoMcpServer = "no MCP server here offers it";
    public const string LiveAuditPass = "a code-audit pass offers only the tools that read";
    public const string LiveAuditWeb = "a code-audit pass leaves the web tools out while Code audit web research is off";
    public const string LiveAuditAsk = "a code-audit pass leaves out the tools that ask while Code audit ask user is off";
    public const string LiveRalphAsk = "a Ralph loop pass leaves out the tools that ask the user while Ralph loop checkpoint is 0";
    public const string LiveHeadlessRalphAsk = "a headless Ralph loop pass leaves out the tools that ask the user";
    public const string LiveGapAsk = "a plan gap pass leaves out the tools that ask while Plan gap ask user is off";

    /// <summary><c>Right now: not offered (Camera tool is off).</c>, or with no reason known <c>Right now: not offered.</c> Pinned.</summary>
    public static string LiveNotOffered(string? why) => string.IsNullOrEmpty(why) ? "Right now: not offered." : $"Right now: not offered ({why}).";

    /// <summary>An MCP server that is not connected, by its state alone.</summary>
    public static string LiveMcpState(McpState state) => state switch
    {
        McpState.Connecting => "its server is still connecting",
        McpState.Failed => "its server failed to connect",
        _ => "its server is not connected",
    };

    /// <summary>The first sentence of <paramref name="text"/>, or all of it; at most 200 characters.</summary>
    public static string FirstSentence(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int end = text.IndexOf(". ", StringComparison.Ordinal);
        string first = end < 0 ? text : text[..(end + 1)];
        return first.Length <= 200 ? first : first[..199].TrimEnd() + "…";
    }

    /// <summary><paramref name="text"/> at most <see cref="MaxChars"/> long: cut at the last line break before it, then <see cref="CutNote"/>.</summary>
    public static string Cap(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= MaxChars)
        {
            return text;
        }

        int cut = text.LastIndexOf('\n', MaxChars - CutNote.Length - 1);
        return text[..(cut > 0 ? cut : MaxChars - CutNote.Length - 1)] + "\n" + CutNote;
    }
}
