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
/// answer is capped at <see cref="HelpText.MaxChars"/>.
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

    /// <summary>The answer for <paramref name="query"/> under <paramref name="kind"/> (one of <see cref="HelpText.Kinds"/>; empty is <c>any</c>).</summary>
    public static string Answer(string? query, string? kind = null)
    {
        string k = string.IsNullOrWhiteSpace(kind) ? "any" : kind.Trim().ToLowerInvariant();
        if (!HelpText.Kinds.Contains(k))
        {
            return HelpText.BadKind(kind!.Trim());
        }

        string q = (query ?? "").Trim();
        return HelpText.Cap(Find(q, k));
    }

    private static string Find(string q, string kind)
    {
        bool any = kind == "any";
        if (kind == "keys" && q.Length == 0)
        {
            return HelpText.Keys(KeyRows());
        }

        if (kind == "command" && q.Length == 0)
        {
            return HelpText.CommandList(SlashCommands.HelpEntries);
        }

        if (q.Length == 0)
        {
            return Overview();
        }

        if ((any || kind == "pane") && PaneAnswer(q) is { } pane)
        {
            return pane;
        }

        if ((any || kind == "command") && CommandOf(q) is { } command)
        {
            return HelpText.Command(command);
        }

        if (any || kind == "keys")
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

        if ((any || kind == "setting") && SettingsNamed(q) is { Count: > 0 } named)
        {
            return string.Join("\n\n", named.Select(Setting));
        }

        return Scored(q, kind) is { Count: > 0 } best
            ? HelpText.MatchesHeading(q) + "\n\n" + string.Join("\n\n", best)
            : HelpText.NothingFound(q);
    }

    /// <summary>The overview: every pane and its tabs, every command (<c>/log</c> among them) and how to ask.</summary>
    public static string Overview() => HelpText.Overview(HelpLocation.Panes, SlashCommands.HelpEntries);

    /// <summary>A setting in full (<see cref="HelpText.Setting"/>).</summary>
    public static string Setting(SettingsField field) =>
        HelpText.Setting(HelpLocation.Of(field), HelpLocation.Default(field), HelpSettings.Describe(field));

    /// <summary>The keys as <c>/help</c> lists them, without the voice rows (they hang on this profile's settings; <see cref="HelpText.KeysTail"/> names them).</summary>
    public static IReadOnlyList<(string Key, string Meaning)> KeyRows() => ChatScreen.KeyRows(voiceOn: false, ConsoleKey.F4, wakeReady: false, "");

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
            string? tab = pane.Tabs.FirstOrDefault(t => TabMatches(t, tabWords));
            return tab is null ? null : TabAnswer(pane, tab);
        }

        if (words.Length >= 2 && words[^1] is "tab" or "tabs")
        {
            string tabWords = string.Join(' ', words[..^1]);
            var found = HelpLocation.Panes.SelectMany(p => p.Tabs.Where(t => TabMatches(t, tabWords)).Select(t => TabAnswer(p, t))).ToList();
            return found.Count > 0 ? string.Join("\n\n", found) : null;
        }

        return null;
    }

    private static string TabAnswer(HelpPane pane, string tab) =>
        HelpText.Tab(pane, tab, HelpLocation.OnTab(pane.Command, tab), HelpLocation.Default, HelpSettings.Describe);

    /// <summary>A tab title against the words asked: the title itself, its start (<c>git</c> is GitLib), or the README's longer name (<c>HA</c> is Home Assistant).</summary>
    private static bool TabMatches(string tab, string words) =>
        string.Equals(tab, words, StringComparison.OrdinalIgnoreCase)
        || (words.Length >= 3 && tab.StartsWith(words, StringComparison.OrdinalIgnoreCase))
        || (string.Equals(tab, ToolsText.HomeAssistantTabTitle, StringComparison.Ordinal) && words is "home assistant" or "homeassistant");

    private static bool IsKeysWord(string q) => q.ToLowerInvariant() is "keys" or "key" or "keyboard" or "shortcuts" or "keyboard shortcuts" or "hotkeys" or "key bindings";

    /// <summary>The settings whose name is <paramref name="q"/> (case aside, a unit suffix like <c>(s)</c> optional), else every one whose name holds it (three letters or more, at most <see cref="HelpText.MaxMatches"/>).</summary>
    public static IReadOnlyList<SettingsField> SettingsNamed(string q)
    {
        string wanted = Squash(q);
        var exact = HelpLocation.Rows.Where(r => Squash(r.Label) == wanted || Squash(WithoutUnit(r.Label)) == wanted).Select(r => r.Field).ToList();
        if (exact.Count > 0 || wanted.Length < 3)
        {
            return exact;
        }

        // The shortest name first: "voice" is TTS voice before TTS voice preview.
        var holding = HelpLocation.Rows.Where(r => Squash(r.Label).Contains(wanted, StringComparison.Ordinal)).OrderBy(r => r.Label.Length).Select(r => r.Field).ToList();
        return holding.Count <= HelpText.MaxMatches ? holding : [];
    }

    /// <summary>
    /// Plain words, scored: per query word, three points when it is in an item's name (a command's syntax, a setting's
    /// label, a key), one when only in its text; a word of four letters or more matches by prefix, and a trailing <c>s</c>
    /// is let go. Items that miss every word drop out; of two that tie the shorter name wins (TTS voice over TTS voice preview),
    /// then the catalog's order.
    /// </summary>
    private static IReadOnlyList<string> Scored(string q, string kind)
    {
        var words = Words(q).Where(w => !s_stopWords.Contains(w)).Distinct(StringComparer.Ordinal).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        var items = new List<(int Score, int Length, string Text)>();
        if (kind is "any" or "setting")
        {
            foreach (var row in HelpLocation.Rows)
            {
                string description = HelpSettings.Describe(row.Field);
                items.Add((Score(words, row.Label + " " + row.Tab, description), row.Label.Length, Setting(row.Field)));
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

    private static int Score(IReadOnlyList<string> words, string name, string text)
    {
        var nameWords = Words(name);
        var textWords = Words(text);
        int score = 0;
        foreach (string word in words)
        {
            score += nameWords.Any(t => Matches(word, t)) ? 3 : textWords.Any(t => Matches(word, t)) ? 1 : 0;
        }

        return score;
    }

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
