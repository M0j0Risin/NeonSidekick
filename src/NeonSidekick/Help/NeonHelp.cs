using System.Text;
using NeonSidekick.App;

namespace NeonSidekick.Help;

/// <summary>
/// The answer behind <c>neon_help</c> (2026-10-02, the user's ask): the app's own manual over <see cref="HelpCommands"/>,
/// <see cref="HelpSettings"/>, <see cref="HelpLocation"/> and <see cref="ChatScreen.KeyRows"/>, documentation only — never
/// this profile's values (the user's call). A query is tried, in order, as: nothing (the overview); a pane or a tab
/// (<c>/tools</c>, <c>/tools camera</c>, <c>camera tab</c>); a command (<c>/camera</c>, <c>camera</c>, <c>//</c>); a key
/// (<c>ctrl+h</c>); a setting's name, whole or a part of it; and last as plain words, scored over every command form,
/// setting and key, the best <see cref="HelpText.MaxMatches"/> listed. <c>kind</c> narrows each step to one sort. Every
/// answer is capped at <see cref="HelpText.MaxChars"/>. Since 2026-10-25 (the neon_help plan, the user's call) it also knows the tools
/// (<see cref="HelpToolCatalog"/>, with a "Right now:" line from <see cref="HelpLive"/>, the one thing it reads from the running app),
/// the bars and toolbar icons (<see cref="HelpInterface"/>), the flags and variables (<see cref="HelpStartup"/>) and the home's files
/// (<see cref="HelpFiles"/>); the order is <see cref="Find"/>'s.
/// </summary>
internal static class NeonHelp
{
    // Words a question carries that say nothing about what it is after.
    private static readonly HashSet<string> s_stopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "how", "do", "does", "i", "can", "to", "is", "it", "in", "on", "of", "for", "and", "or", "what", "where", "which",
        "my", "me", "with", "set", "change", "turn", "make", "use", "using", "find", "there", "this", "that", "be", "you", "your", "by",
        "neon", "neonsidekick", "sidekick", "app", "setting", "settings", "option", "command", "slash", "tab", "pane", "menu", "please",
        "when", "why", "get", "want", "should", "about", "are", "at", "from",
    };

    /// <summary>
    /// The answer for <paramref name="query"/> under <paramref name="kind"/> (one of <see cref="HelpText.Kinds"/> or an alias of one,
    /// <see cref="HelpText.KindAliases"/>; empty is <c>any</c>). <paramref name="live"/> (2026-10-25, the user's call) is the running
    /// app's view of its tools, asked at most once and only by a tool answer, a group list, a group switch's answer or a command whose
    /// word names a group; null (the advisors' copy, the tests) leaves the "Right now:" line out.
    /// </summary>
    public static string Answer(string? query, string? kind = null, Func<HelpLive>? live = null) =>
        Answer(query, kind, live, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());

    /// <summary>The answer as one system gives it (<paramref name="windows"/> decides what is left out, <paramref name="mac"/> picks the Mac lines), so both are tested on either machine.</summary>
    internal static string Answer(string? query, string? kind, Func<HelpLive>? live, bool windows, bool mac)
    {
        string k = string.IsNullOrWhiteSpace(kind) ? "any" : kind.Trim().ToLowerInvariant();
        k = HelpText.KindAliases.TryGetValue(k, out var alias) ? alias : k;
        if (!HelpText.Kinds.Contains(k))
        {
            return HelpText.BadKind(kind!.Trim());
        }

        HelpLive? cached = null;
        bool asked = false;
        HelpLive? Live()
        {
            if (!asked)
            {
                asked = true;
                cached = live?.Invoke();
            }

            return cached;
        }

        string q = (query ?? "").Trim();
        return HelpText.Cap(Find(q, k, Live, windows, mac));
    }

    /// <summary>
    /// The steps in order (2026-10-25, the neon_help plan's numbering; the deferred steps 3 and 7's window keys are left out), each only under
    /// <c>any</c> or its own kind: no query; glyphs; pane; command (with its see-also line); keys; an exact setting name; tool names; a
    /// bar or toolbar item by name; flags and variables; files; a setting name's part; a lone tool or flag token that matched nothing is
    /// the miss; plain words, scored; the miss.
    /// </summary>
    private static string Find(string q, string kind, Func<HelpLive?> live, bool windows, bool mac)
    {
        bool any = kind == "any";
        bool Is(string k) => any || kind == k;
        if (q.Length == 0)
        {
            return kind switch
            {
                "keys" => HelpText.Keys(KeyRows()),
                "command" => HelpText.CommandList(SlashCommands.HelpEntries),
                "tool" => HelpToolCatalog.List(live(), windows),
                "interface" => HelpInterface.List(windows),
                "startup" => HelpStartup.List(windows),
                "files" => HelpFiles.Tree(windows, mac),
                _ => Overview(),
            };
        }

        var tokens = HelpToken.Read(q);
        if (Is("interface") && HelpInterface.ByGlyphs(q, windows) is { Count: > 0 } glyphs)
        {
            return string.Join("\n\n", glyphs);
        }

        if (Is("pane") && PaneAnswer(q) is { } pane)
        {
            return pane;
        }

        if (Is("command") && CommandOf(q) is { } command)
        {
            return HelpText.Command(command) + SeeAlso(command.Command, windows) + (any ? CommandsGroup(command.Command, live, windows) : "");
        }

        if (Is("keys"))
        {
            if (IsKeysWord(q))
            {
                return HelpText.Keys(KeyRows());
            }

            var key = KeyRows().FirstOrDefault(r => string.Equals(r.Key, q, StringComparison.OrdinalIgnoreCase));
            if (key.Key is not null)
            {
                return HelpText.Key(key.Key, key.Meaning);
            }
        }

        // A setting's name never answers a `--flag` (`--profile` is the flag, not *Profile*), nor names a row this system leaves out.
        bool flag = q.StartsWith('-');
        if (Is("setting") && !flag && SettingsNamedExactly(q).Where(f => (!any || !HelpInterface.IsBarRow(f)) && HelpLocation.Of(f, windows) is not null).ToList() is { Count: > 0 } named)
        {
            return string.Join("\n\n", named.Select(f => SwitchSetting(f, live, windows)));
        }

        if (Is("tool") && ToolItems(q, tokens, kind == "tool", live, windows, mac) is { Count: > 0 } tools)
        {
            return string.Join("\n\n", tools);
        }

        if (Is("interface") && HelpInterface.ByName(q, windows) is { } item)
        {
            return item;
        }

        if (Is("startup") && HelpStartup.Find(tokens, windows, mac, kind == "startup") is { Count: > 0 } startup)
        {
            return string.Join("\n\n", startup);
        }

        if (Is("files") && HelpFiles.Find(tokens, windows, mac) is { Count: > 0 } files)
        {
            return string.Join("\n\n", files);
        }

        if (Is("setting") && !flag && SettingsHolding(q).Where(f => HelpLocation.Of(f, windows) is not null).ToList() is { Count: > 0 } holding)
        {
            return string.Join("\n\n", holding.Select(Setting));
        }

        // A lone tool name or flag that matched nothing is the miss, never plain words (2026-10-25: on a Mac `service_control` and
        // `--services-check` are unknown, and their words would score unrelated rows).
        if (tokens.Count == 1 && (tokens[0].Text.Contains('_', StringComparison.Ordinal) || tokens[0].Text.StartsWith("--", StringComparison.Ordinal)))
        {
            return HelpText.NothingFound(q);
        }

        return Scored(q, kind, windows, mac) is { Count: > 0 } best
            ? HelpText.MatchesHeading(q) + "\n\n" + string.Join("\n\n", best)
            : HelpText.NothingFound(q);
    }

    /// <summary>
    /// A setting, and for a group's switch (<c>Web tools</c>) its tools and the group's live line (2026-10-25): "web tools" is the setting,
    /// so its answer says what the switch offers.
    /// </summary>
    private static string SwitchSetting(SettingsField field, Func<HelpLive?> live, bool windows) =>
        HelpToolCatalog.GroupOfSwitch(field, windows) is null ? Setting(field) : Setting(field) + "\n" + HelpToolCatalog.SwitchTools(field, live(), windows);

    /// <summary>
    /// The tools a query names (2026-10-25, step 9), in its order, each once, at most <see cref="HelpText.MaxMatches"/>: an underscored name
    /// anywhere (the Claude Code server's <c>mcp__neon__</c> taken off), a bare-word one (<c>copy</c>) only as the whole query, in
    /// backticks, before "tool" or under <c>kind: tool</c>, an MCP tool by its whole name; else a group ("camera tools").
    /// </summary>
    private static List<string> ToolItems(string q, IReadOnlyList<HelpToken> tokens, bool kindTool, Func<HelpLive?> live, bool windows, bool mac)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            string name = token.Text.StartsWith(Claude.ClaudeArguments.McpToolPrefix, StringComparison.OrdinalIgnoreCase) ? token.Text[Claude.ClaudeArguments.McpToolPrefix.Length..] : token.Text;
            if (HelpToolCatalog.Find(name, windows) is { } entry)
            {
                bool plain = !HelpToolCatalog.BareWords.Contains(entry.Name) || tokens.Count == 1 || token.Ticked || kindTool || token.Next is "tool" or "tools";
                if (plain && seen.Add(entry.Name))
                {
                    found.Add(HelpToolCatalog.Answer(entry, live(), windows, mac));
                }
            }
            else if (HelpToolCatalog.LooksLikeMcpName(name) && seen.Add(name))
            {
                found.Add(HelpToolCatalog.McpAnswer(name, live()));
            }

            if (found.Count == HelpText.MaxMatches)
            {
                break;
            }
        }

        if (found.Count == 0 && HelpToolCatalog.GroupNamed(Words(q), kindTool, windows) is { } group)
        {
            found.Add(HelpToolCatalog.GroupAnswer(group, live(), windows));
        }

        return found;
    }

    /// <summary>
    /// The line after a command's answer when its word names something else too (2026-10-25): a tool (<c>/copy</c>, <c>/process</c>) or a
    /// bar (<c>/toolbar</c>, <c>/perfbar</c>, <c>/homebar</c>), with the words that ask for it. Empty for none.
    /// </summary>
    private static string SeeAlso(string command, bool windows)
    {
        string word = command.TrimStart('/');
        if (HelpToolCatalog.BareWords.Contains(word) && HelpToolCatalog.Find(word, windows) is not null)
        {
            return "\n\n" + HelpText.SeeAlsoTool(word);
        }

        return HelpInterface.SeeAlso(command) is { } bar ? "\n\n" + bar : "";
    }

    /// <summary>
    /// The tool group a command's word also names, after the command's answer (2026-10-25, the user's report: asked "can you take a photo?",
    /// the model looked up "camera", got <c>/camera</c> alone and never read whether <c>camera_capture</c> was offered): the group's answer
    /// with its live line (<c>/camera</c>, <c>/screen</c>, <c>/docker</c>, <c>/youtube</c>…). Empty for none, and under <c>kind: command</c>.
    /// </summary>
    private static string CommandsGroup(string command, Func<HelpLive?> live, bool windows) =>
        (HelpToolCatalog.GroupNamed([command.TrimStart('/')], bare: true, windows)
            ?? (s_commandGroups.TryGetValue(command, out var field) ? HelpToolCatalog.GroupOfSwitch(field, windows) : null)) is { } group
            ? "\n\n" + HelpToolCatalog.GroupAnswer(group, live(), windows)
            : "";

    // The commands whose word is not their tool group's name (2026-10-25).
    private static readonly Dictionary<string, SettingsField> s_commandGroups = new(StringComparer.Ordinal)
    {
        ["/net"] = SettingsField.NetworkTools,
        ["/ha"] = SettingsField.HomeAssistantTools,
        ["/comfy"] = SettingsField.ComfyTools,
        ["/ad"] = SettingsField.DirectoryTools,
        ["/vault"] = SettingsField.ObsidianTools,
        ["/advisor"] = SettingsField.Advisors,
    };

    /// <summary>The overview: every pane and its tabs, every command (<c>/log</c> among them) and how to ask.</summary>
    public static string Overview() => HelpText.Overview(HelpLocation.Panes, SlashCommands.HelpEntries);

    /// <summary>A setting in full (<see cref="HelpText.Setting"/>).</summary>
    public static string Setting(SettingsField field) =>
        HelpText.Setting(HelpLocation.Of(field), HelpLocation.Default(field), HelpSettings.Describe(field));

    /// <summary>The keys as <c>/help</c> lists them, without the voice rows (they hang on this profile's settings; <see cref="HelpText.KeysTail"/> names them).</summary>
    public static IReadOnlyList<(string Key, string Meaning)> KeyRows() => [.. ChatScreen.KeyRows(voiceOn: false, ConsoleKey.F4, wakeReady: false, "").Select(r => r.Pair)];

    /// <summary>The command <paramref name="q"/> names: its word with or without the slash (<c>/camera watch</c> is <c>/camera</c>), or an alias (<c>//</c>).</summary>
    public static CommandHelp? CommandOf(string q)
    {
        string first = q.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        bool slashed = first.StartsWith('/');
        if (!slashed && q.Contains(' ', StringComparison.Ordinal))
        {
            // Plain words: "camera watch" is a question, not a command, unless it is one word ("camera").
            return null;
        }

        string word = slashed ? first : "/" + first;
        var alias = SlashCommands.HelpEntries.FirstOrDefault(e => e.Aliases.Contains(word, StringComparer.Ordinal));
        word = alias?.Command ?? word;
        return HelpCommands.Commands.FirstOrDefault(c => string.Equals(c.Command, word, StringComparison.Ordinal));
    }

    /// <summary>
    /// A pane or one of its tabs: <c>/tools</c> (or <c>tools pane</c>) is the pane; <c>/tools camera</c> its tab; <c>camera tab</c>
    /// every tab of that title. A bare pane command answers with the pane only when asked as one (<c>/tools</c>, <c>tools pane</c>,
    /// <c>/tools tabs</c>); null when nothing fits.
    /// </summary>
    private static string? PaneAnswer(string q)
    {
        var words = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string first = words[0].TrimStart('/');
        var pane = HelpLocation.Panes.FirstOrDefault(p => string.Equals(p.Command.TrimStart('/'), first, StringComparison.Ordinal));
        var rest = words.Skip(1).Where(w => w is not ("tab" or "tabs" or "pane" or "menu")).ToList();
        if (pane is not null && (words[0].StartsWith('/') || words.Length > 1))
        {
            if (rest.Count == 0)
            {
                return HelpText.Pane(pane, tab => HelpLocation.OnTab(pane.Command, tab));
            }

            string tabWords = string.Join(' ', rest);
            // A title said whole wins over one it starts (2026-10-10: /tools file is the File menu, not the Files tab it begins), tabs before menus each time.
            foreach (var matches in new Func<string, string, bool>[] { TabIs, TabMatches })
            {
                string? tab = pane.Tabs.FirstOrDefault(t => matches(t, tabWords));
                if (tab is not null)
                {
                    return TabAnswer(pane, tab);
                }

                // A menu on a group tab (2026-10-09): /tools sql is the SQL menu of the Database tab, as it was the SQL tab.
                var menu = HelpLocation.Menus(pane.Command).FirstOrDefault(m => matches(m.Menu, tabWords));
                if (menu.Menu is not null)
                {
                    return MenuAnswer(pane, menu.Tab, menu.Menu);
                }
            }

            return null;
        }

        if (words.Length >= 2 && words[^1] is "tab" or "tabs" or "menu")
        {
            string tabWords = string.Join(' ', words[..^1]);
            foreach (var matches in new Func<string, string, bool>[] { TabIs, TabMatches })
            {
                var found = HelpLocation.Panes.SelectMany(p => p.Tabs.Where(t => matches(t, tabWords)).Select(t => TabAnswer(p, t))
                    .Concat(HelpLocation.Menus(p.Command).Where(m => matches(m.Menu, tabWords)).Select(m => MenuAnswer(p, m.Tab, m.Menu)))).ToList();
                if (found.Count > 0)
                {
                    return string.Join("\n\n", found);
                }
            }

            return null;
        }

        return null;
    }

    private static string TabAnswer(HelpPane pane, string tab)
    {
        var rows = HelpLocation.OnTab(pane.Command, tab);
        return rows.Any(r => r.Menu is not null)
            ? HelpText.GroupTab(pane, tab, rows)   // a group tab, Devices or Databases (2026-10-09): the menus, each answered on its own
            : HelpText.Tab(pane, tab, rows, HelpLocation.Default, HelpSettings.Describe);
    }

    /// <summary>A menu on a group tab (2026-10-09) answered as a tab is, under its place: <c>Tab /tools › Databases › SQL:</c>.</summary>
    private static string MenuAnswer(HelpPane pane, string tab, string menu) =>
        HelpText.Tab(pane, tab + HelpText.PathSeparator + menu, HelpLocation.InMenu(pane.Command, menu), HelpLocation.Default, HelpSettings.Describe);

    /// <summary>A tab title against the words asked: the title itself, its start (<c>git</c> is GitLib), or the README's longer name (<c>HA</c> is Home Assistant).</summary>
    private static bool TabIs(string tab, string words) => string.Equals(tab, words, StringComparison.OrdinalIgnoreCase);

    private static bool TabMatches(string tab, string words) =>
        string.Equals(tab, words, StringComparison.OrdinalIgnoreCase)
        || (words.Length >= 3 && tab.StartsWith(words, StringComparison.OrdinalIgnoreCase))
        || (string.Equals(tab, ToolsText.HomeAssistantTabTitle, StringComparison.Ordinal) && words is "home assistant");   // "homeassistant" is the title itself since 2026-10-10

    private static bool IsKeysWord(string q) => q.ToLowerInvariant() is "keys" or "key" or "keyboard" or "shortcuts" or "keyboard shortcuts" or "hotkeys" or "key bindings";

    /// <summary>The settings whose name is <paramref name="q"/> (case aside, a unit suffix like <c>(s)</c> optional): <see cref="Find"/>'s step 8.</summary>
    private static List<SettingsField> SettingsNamedExactly(string q)
    {
        string wanted = Squash(q);
        return HelpLocation.Rows.Where(r => Squash(r.Label) == wanted || Squash(WithoutUnit(r.Label)) == wanted).Select(r => r.Field).ToList();
    }

    /// <summary>Every setting whose name holds <paramref name="q"/> (three letters or more, at most <see cref="HelpText.MaxMatches"/>, the shortest name first): step 13.</summary>
    private static List<SettingsField> SettingsHolding(string q)
    {
        string wanted = Squash(q);
        if (wanted.Length < 3)
        {
            return [];
        }

        // The shortest name first: "voice" is TTS voice before TTS voice preview.
        var holding = HelpLocation.Rows.Where(r => Squash(r.Label).Contains(wanted, StringComparison.Ordinal)).OrderBy(r => r.Label.Length).Select(r => r.Field).ToList();
        return holding.Count <= HelpText.MaxMatches ? holding : [];
    }

    /// <summary>
    /// The settings rows <paramref name="q"/> finds (2026-10-04, the UI review: <c>/settings &lt;words&gt;</c> and typing on a settings
    /// tab), by <see cref="Scored"/>'s rule over the settings alone — three points for a word in the row's name or tab, one for a
    /// word only in its description, the shorter name first on a tie — at most <paramref name="max"/>; none for no word.
    /// </summary>
    public static IReadOnlyList<HelpRow> FindSettings(string q, int max = 40)
    {
        ArgumentNullException.ThrowIfNull(q);
        var words = Words(q).Where(w => !s_stopWords.Contains(w)).Distinct(StringComparer.Ordinal).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        return HelpLocation.Rows
            .Select((row, index) => (Row: row, Score: Score(words, row.Label + " " + row.Place, HelpSettings.Describe(row.Field)), Index: index))
            .Where(i => i.Score > 0)
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.Row.Label.Length)
            .ThenBy(i => i.Index)
            .Take(max)
            .Select(i => i.Row)
            .ToList();
    }

    /// <summary>
    /// Plain words, scored: per query word, three points when it is in an item's name (a command's syntax, a setting's
    /// label, a key), one when only in its text; a word of four letters or more matches by prefix, and a trailing <c>s</c>
    /// is let go. Items that miss every word drop out; of two that tie the shorter name wins (TTS voice over TTS voice preview),
    /// then the catalog's order.
    /// </summary>
    private static IReadOnlyList<string> Scored(string q, string kind, bool windows, bool mac)
    {
        var words = Words(q).Where(w => !s_stopWords.Contains(w)).Distinct(StringComparer.Ordinal).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        var items = new List<(int Score, int Length, string Text)>();
        if (kind is "any" or "setting")
        {
            foreach (var row in HelpLocation.RowsFor(windows))
            {
                string description = HelpSettings.Describe(row.Field);
                items.Add((Score(words, row.Label + " " + row.Place, description), row.Label.Length, Setting(row.Field)));
            }
        }

        if (kind is "any" or "command")
        {
            foreach (var command in HelpCommands.Commands)
            {
                foreach (var form in command.Forms)
                {
                    items.Add((Score(words, form.Syntax, form.Meaning), form.Syntax.Length, HelpText.Command(command with { Forms = [form] })));
                }
            }
        }

        if (kind is "any" or "keys")
        {
            foreach (var (key, meaning) in KeyRows())
            {
                items.Add((Score(words, key, meaning), key.Length, HelpText.Key(key, meaning)));
            }
        }

        if (kind is "any" or "pane")
        {
            foreach (var pane in HelpLocation.Panes)
            {
                items.Add((Score(words, pane.Command + " " + string.Join(' ', pane.Tabs), pane.Summary), pane.Command.Length, HelpText.Pane(pane, tab => HelpLocation.OnTab(pane.Command, tab))));
            }
        }

        // The new sorts (2026-10-25), after the first four so a tie keeps their order; never a live line here.
        if (kind is "any" or "tool")
        {
            foreach (var entry in HelpToolCatalog.Entries(windows))
            {
                items.Add((Score(words, entry.Name + " " + entry.Group, entry.Does), entry.Name.Length, HelpToolCatalog.Answer(entry, null, windows, mac)));
            }
        }

        foreach (var item in (kind is "any" or "interface" ? HelpInterface.Items(windows) : [])
            .Concat(kind is "any" or "startup" ? HelpStartup.Items(windows, mac) : [])
            .Concat(kind is "any" or "files" ? HelpFiles.Items(windows, mac) : []))
        {
            items.Add((Score(words, item.Name, item.Text), item.Name.Length, item.Answer()));
        }

        return items
            .Select((item, index) => (item.Score, item.Length, item.Text, Index: index))
            .Where(i => i.Score > 0)
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.Length)
            .ThenBy(i => i.Index)
            .Take(HelpText.MaxMatches)
            .Select(i => i.Text)
            .ToList();
    }

    /// <summary>
    /// Three points for a word in the name, one for a word only in the text. A word with a synonym (2026-10-25, <see cref="HelpText.Synonyms"/>)
    /// counts as itself or as the word it stands for, the better of the two, never their sum: "shortcut" still finds <c>/shortcut</c>.
    /// </summary>
    private static int Score(IReadOnlyList<string> words, string name, string text)
    {
        var nameWords = Words(name);
        var textWords = Words(text);
        int score = 0;
        foreach (string word in words)
        {
            int best = Points(word, nameWords, textWords);
            if (best < 3 && HelpText.Synonyms.TryGetValue(word, out var synonym))
            {
                best = Math.Max(best, Points(synonym, nameWords, textWords));
            }

            score += best;
        }

        return score;
    }

    private static int Points(string word, List<string> nameWords, List<string> textWords) =>
        nameWords.Any(t => Matches(word, t)) ? 3 : textWords.Any(t => Matches(word, t)) ? 1 : 0;

    private static bool Matches(string word, string token) =>
        string.Equals(word, token, StringComparison.Ordinal)
        || (word.Length >= 4 && token.StartsWith(word, StringComparison.Ordinal))
        || (word.Length >= 5 && word.EndsWith('s') && string.Equals(word[..^1], token, StringComparison.Ordinal));

    /// <summary>Lower-case words of letters and digits; <c>/camera</c> is <c>camera</c>, <c>ctrl+h</c> is <c>ctrl</c> and <c>h</c>.</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb.Length > 0)
            {
                words.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0)
        {
            words.Add(sb.ToString());
        }

        return words;
    }

    /// <summary>Letters and digits only, lower case: <c>Camera watch interval (s)</c> is <c>camerawatchintervals</c>.</summary>
    private static string Squash(string text) => string.Concat(Words(text));

    /// <summary>A label without its trailing unit in brackets: <c>Camera watch interval (s)</c> is <c>Camera watch interval</c>.</summary>
    private static string WithoutUnit(string label)
    {
        int open = label.LastIndexOf(" (", StringComparison.Ordinal);
        return open > 0 && label.EndsWith(')') ? label[..open] : label;
    }
}
