using System.Text;
using NeonSidekick.App;
using NeonSidekick.Mcp;

namespace NeonSidekick.Help;

/// <summary>
/// The words of <c>neon_help</c> (2026-10-02, the user's ask: a tool that answers how-to questions about the app itself): the
/// <c>/tools</c> group's title, the result's headings and layout, the overview, the misses. The <see cref="ToolsText"/> shape —
/// pure statics, the model reads every line, so the layout is plain text (no Markdown tables) and every heading is pinned.
/// </summary>
public static class HelpText
{
    /// <summary>The group <c>neon_help</c> sits in on <c>/tools</c>' Offered tab and <c>/sys</c>: no switch of its own, the tool switched off by name. Pinned.</summary>
    public const string GroupTitle = "Help";

    /// <summary>Between a pane, a tab and a row in a setting's place: <c>/tools › Camera › Camera tool</c>.</summary>
    public const string PathSeparator = " › ";

    /// <summary>The cap on one answer, in characters: the command list (<see cref="CommandList"/>) and the longest tab fit, and a broad query is cut with <see cref="CutNote"/>.</summary>
    public const int MaxChars = 9000;

    /// <summary>How many matches a free-word query lists, best first.</summary>
    public const int MaxMatches = 8;

    // The panes' one-liners: README's Available Panes, kept short.
    public const string SettingsPaneSummary = "the app's settings: the profile and screen, the embedded model, Docker servers, the Anthropic and OpenAI APIs and the Claude CLI server, the LLM connection, speech output (TTS), voice input (STT), sessions and /botchat";
    public const string ToolsPaneSummary = "the model's tools: Offered lists every tool, on or off (Enter flips one); the other tabs hold each tool group's settings";
    public const string SkillsPaneSummary = "agent skills (SKILL.md folders) and the model's self-reflection";
    public const string McpPaneSummary = "external MCP servers: Servers lists and edits them, Tools shows what each offers, Options holds the switch and timeout";

    /// <summary>The working directory's default, the profile's own folder (<see cref="HelpLocation.Default"/>).</summary>
    public const string WorkingDirectoryDefault = @"(the profile's files folder: <home>\profiles\<profile>\files)";

    /// <summary>A default the menu shows as nothing (<see cref="HelpLocation.Default"/>). Pinned.</summary>
    public const string EmptyDefault = "(empty)";

    /// <summary>The <c>kind</c> words the tool takes.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["any", "command", "setting", "pane", "keys"];

    /// <summary><c>Unknown kind 'x'; use any, command, setting, pane or keys.</c> Pinned.</summary>
    public static string BadKind(string kind) => $"Unknown kind '{kind}'; use {string.Join(", ", Kinds.Take(Kinds.Count - 1))} or {Kinds[^1]}.";

    /// <summary>A query nothing matched. Pinned.</summary>
    public static string NothingFound(string query) =>
        $"Nothing in NeonSidekick's help matches '{query}'. Try other words, a command (/camera), a setting's name, a pane or tab (/tools camera), or no query at all for the overview.";

    /// <summary>The line after an answer cut at <see cref="MaxChars"/>. Pinned.</summary>
    public const string CutNote = "… (cut short; ask about one command, setting or tab for the rest)";

    /// <summary>The overview's heading. Pinned.</summary>
    public const string OverviewHeading = "NeonSidekick help: the panes, their tabs, and every slash command.";

    /// <summary>The overview's last line: how to ask for more. Pinned.</summary>
    public const string OverviewTail = "Ask neon_help about one command (/camera), a setting by name, a pane or tab (/tools camera), keys, or in plain words; kind command with no query lists every command with a line on each.";

    /// <summary>The command list's heading (<c>kind: command</c>, no query). Pinned.</summary>
    public const string CommandListHeading = "Slash commands (type / to list them; ask about one for every form):";

    /// <summary>The keys answer's heading. Pinned.</summary>
    public const string KeysHeading = "Keys (also under /help › Keys):";

    /// <summary>The line after the keys: the voice keys depend on settings. Pinned.</summary>
    public const string KeysTail = "With voice input on, the STT push-to-talk key talks; with the wake word on, saying the wake phrase does.";

    /// <summary>The free-word answer's heading. Pinned.</summary>
    public static string MatchesHeading(string query) => $"Best matches for '{query}':";

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
        return $"Setting {row.Label}\n  Where: {row.Path} (open {row.Pane}, go to the {row.Tab} tab, Enter on the row edits it)\n  Default: {defaultValue}\n  What it does: {description}";
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
            sb.Append("\n- ").Append(tab).Append(": ").Append(rows.Count > 0 ? string.Join(", ", rows.Select(r => r.Label)) : ListTab(pane.Command, tab));
        }

        return sb.ToString();
    }

    /// <summary>A tab's rows, each with its default and the first sentence of what it does.</summary>
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

    /// <summary>The keys, one per line: <c>- Ctrl+H: open help (/help)</c>.</summary>
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

    /// <summary>One key on its own: <c>Key Ctrl+H: open help (/help)</c>.</summary>
    public static string Key(string key, string meaning) => $"Key {key}: {meaning}";

    /// <summary>What a tab without settings rows lists (<c>/tools</c>' and <c>/skills</c>' Offered, <c>/mcp</c>' Servers and Tools).</summary>
    public static string ListTab(string pane, string tab) => (pane, tab) switch
    {
        ("/tools", ToolsText.OfferedTabTitle) => "every tool the model can be offered, by group, each on or off; Enter or Space flips the tool under the cursor (a tool switched off is never offered)",
        ("/skills", SkillsText.OfferedTabTitle) => "the installed skills; Enter opens one, and the pane's keys add, edit or delete them",
        ("/mcp", McpText.ServersTabTitle) => "the MCP servers from mcp.json (global and this profile's); add, edit, switch on or off and reconnect them here",
        ("/mcp", McpText.ToolsTabTitle) => "the tools each connected MCP server offers, switched on or off one by one",
        _ => "no settings rows",
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
