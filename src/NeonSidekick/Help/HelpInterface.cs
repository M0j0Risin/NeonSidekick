using System.Globalization;
using System.Text;
using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.Help;

/// <summary>
/// The bars under the input line and the toolbar's items, for <c>neon_help</c> (2026-10-25, the neon_help plan's Step 3, scoped to the
/// bars and the toolbar; the windows, the status icons, the splash and the Web UI deferred). Read from the bars' own classes
/// (<see cref="PaneBars"/>, <see cref="ToolbarItems"/>, <see cref="StatusBarItems"/>, <see cref="PerfBarItems"/>,
/// <see cref="HintsBarMode"/>), so a new bar or item answers by itself; only what those classes don't say is written here: the Hints bar's
/// order, the Home bar's chips and what each item's dark background means. On a system without services and tasks their items are left
/// out (<see cref="ToolbarItems.IsHere(string, bool)"/>), and so is the Home bar's 🖥️ line off Windows.
/// </summary>
internal static class HelpInterface
{
    /// <summary>The bar answer's heading: <c>Bar Tool bar: the panes' icons and the path.</c>. Pinned.</summary>
    public static string BarHeading(string id) => $"Bar {PaneBars.Title(id)}: {PaneBars.Describe(id)}.";

    /// <summary>The toolbar item answer's heading: <c>Toolbar item 📡 Network: opens /tools network</c>. Pinned.</summary>
    public static string ItemHeading(string glyph, string id) =>
        $"Toolbar item {(glyph.Length > 0 ? glyph + " " : "")}{ToolbarItems.Title(id)}: opens {ToolbarItems.Describe(id)}";

    /// <summary>The list's heading (<c>kind: interface</c>, no query). Pinned.</summary>
    public const string ListHeading = "The bars under the input line, top to bottom by default (ask about one by name, \"tool bar\"):";

    /// <summary>The toolbar part of the list. Pinned.</summary>
    public const string ItemsHeading = "Toolbar items (ask about one by its icon, `/help 📡`, or its name):";

    /// <summary>How the Hints bar picks its hint. Pinned.</summary>
    public const string HintsLadderLine = "It shows one hint, the first there is: an alert (the kill switch's), a waiting second press (exit, a rewind, a splash picture's delete), an open pane's keys, then the arrows' keys (`/read`, the splash, the picture strip).";

    /// <summary>The Home bar's chips. Pinned.</summary>
    public const string HomeBarLine = "Its chips are picked with `/homebar edit`: a room's lights, a Home Assistant device, the shopping list's 🛒 (the lines still to get) and dividers; a click shows a chip's state, a double-click acts, a right-click opens its pane.";

    /// <summary>The Home bar's camera chip (2026-10-27, the ring-integration plan). Pinned.</summary>
    public const string HomeCameraLine = "A camera's 📹 chip fetches its still on a double-click, opens it in the picture window and keeps it in home-cameras under the profile's images folder.";

    /// <summary>The Home bar's monitors chip, Windows only. Pinned.</summary>
    public const string HomeMonitorsLine = "The 🖥️ chip stands for this PC's monitors; a right-click opens the monitors pane (`/monitor`).";

    /// <summary>How an item opens and names itself. Pinned.</summary>
    public const string ClickLine = "A double-click or a right-click opens it; one click names it on the Hints bar.";

    /// <summary>The toolbar's dark backgrounds in one line, for the bar's answer. Pinned.</summary>
    public const string DarkLine = "A tool switch's icon sits on a dark background while its switch is off; so do 📱 while the Web UI is off, 🎓 while Agent skills is off and 🛠️ while `/tools` offers nothing.";

    // The dark backgrounds that are no tool switch's (2026-10-25, read from ChatScreen.ToolbarItemOff). Pinned.
    public const string DarkWebUi = "while the Web UI is off";
    public const string DarkSkills = "while Agent skills is off";
    public const string DarkTools = "while `/tools` offers no tool at all";
    public const string DarkMemory = "while Memory mode is disabled";
    public const string DarkQueue = "while Queue mode is disabled";
    public const string DarkEventLogs = "while Event log tools is off, or where this computer has no event logs";
    public const string DarkDirectory = "while Directory tools is off, or where this computer has no directory to read";

    // The two state icons the row draws besides the checklist's (2026-10-25). Pinned.
    public const string LockLine = "Its icon: 🔒 while Shell command policy is ask, 🔓 while it is yolo (commands run unasked); not drawn while it is off.";
    public const string PoliceLine = "Its icon: 👮 while Shell police is on, 🥷 while it is off; not drawn while Shell command policy is off.";

    /// <summary>The settings row that shows or hides each bar (the rows carry the bars' names).</summary>
    private static SettingsField RowOf(string id) => id switch
    {
        PaneBars.Hints => SettingsField.HintsBar,
        PaneBars.Status => SettingsField.StatusBarItems,
        PaneBars.Toolbar => SettingsField.ToolbarItems,
        PaneBars.Perf => SettingsField.ShowPerformanceBar,
        _ => SettingsField.ShowHomeBar,
    };

    /// <summary>Whether a setting is a bar's own row: under <c>any</c> its name is the bar's ("tool bar"), so the bar answers, naming the row.</summary>
    public static bool IsBarRow(SettingsField field) => PaneBars.Names.Any(id => RowOf(id) == field);

    /// <summary>The toolbar items a system draws, in checklist order: all but the path and the model's name have an icon.</summary>
    public static IReadOnlyList<string> ItemsHere(bool windows) => ToolbarItems.Names.Where(id => ToolbarItems.IsHere(id, windows)).ToList();

    /// <summary>One bar in full: what it is, its row, its own lines, its place in the order.</summary>
    public static string Bar(string id, bool windows)
    {
        var sb = new StringBuilder(BarHeading(id));
        var row = HelpLocation.Of(RowOf(id), windows);
        if (row is not null)
        {
            sb.Append("\n  Shown by: ").Append(row.Label).Append(" at ").Append(row.Path).Append(": ").Append(HelpText.FirstSentence(HelpSettings.Describe(RowOf(id))));
        }

        switch (id)
        {
            case PaneBars.Hints:
                sb.Append("\n  ").Append(HintsLadderLine);
                sb.Append("\n  Modes: ").Append(string.Join("; ", HintsBarMode.Names.Select(n => n + ": " + HintsBarMode.Describe(n))));
                break;
            case PaneBars.Status:
                sb.Append("\n  Sections: ").Append(string.Join("; ", StatusBarItems.Names.Select(n => StatusBarItems.Title(n) + ": " + StatusBarItems.Describe(n))));
                break;
            case PaneBars.Toolbar:
                sb.Append("\n  ").Append(ClickLine).Append("\n  ").Append(DarkLine);
                sb.Append("\n  Items: ").Append(string.Join(", ", ItemsHere(windows).Select(i => (ToolbarItems.Glyph(i) is { Length: > 0 } g && i != ToolbarItems.Path ? g + " " : "") + ToolbarItems.Title(i))));
                break;
            case PaneBars.Perf:
                sb.Append("\n  Meters: ").Append(string.Join("; ", PerfBarItems.Names.Select(n => PerfBarItems.Title(n) + ": " + PerfBarItems.Describe(n))));
                break;
            case PaneBars.Home:
                sb.Append("\n  ").Append(HomeBarLine).Append("\n  ").Append(HomeCameraLine);
                if (windows)
                {
                    sb.Append("\n  ").Append(HomeMonitorsLine);
                }

                break;
        }

        if (HelpLocation.Of(SettingsField.BarOrder, windows) is { } order)
        {
            sb.Append("\n  Order among the bars: ").Append(order.Label).Append(" at ").Append(order.Path);
        }

        return sb.ToString();
    }

    /// <summary>One toolbar item in full; <paramref name="glyph"/> the icon asked about (🔓 and 🥷 say their state).</summary>
    public static string Item(string id, bool windows, string? glyph = null)
    {
        string shown = glyph ?? (id == ToolbarItems.Path ? "" : ToolbarItems.Glyph(id));
        var sb = new StringBuilder(ItemHeading(shown, id)).Append("\n  ").Append(ClickLine);
        if (id == ToolbarItems.CmdList)
        {
            sb.Append("\n  ").Append(LockLine);
        }
        else if (id == ToolbarItems.Police)
        {
            sb.Append("\n  ").Append(PoliceLine);
        }

        if (ToolsText.SwitchField(id) is { } field && HelpLocation.Of(field, windows) is { } row)
        {
            sb.Append("\n  Setting: ").Append(row.Label).Append(" at ").Append(row.Path).Append(": ").Append(HelpText.FirstSentence(HelpSettings.Describe(field)));
        }

        if (Dark(id) is { } dark)
        {
            sb.Append("\n  Dark background: ").Append(dark);
        }

        return sb.ToString();
    }

    /// <summary>When an item sits on a dark background (<see cref="ChatScreen.ToolbarItemOff"/>'s cases); null for one that never does.</summary>
    public static string? Dark(string id) => id switch
    {
        ToolbarItems.WebUi => DarkWebUi,
        ToolbarItems.Skills => DarkSkills,
        ToolbarItems.Tools => DarkTools,
        ToolbarItems.Memory => DarkMemory,
        ToolbarItems.Queue => DarkQueue,
        ToolbarItems.EventLog => DarkEventLogs,
        ToolbarItems.Directory => DarkDirectory,
        _ => ToolsText.SwitchField(id) is { } field ? "while " + SettingsMenu.FieldName(field) + " is off" : null,
    };

    /// <summary>Every bar and every item of this system, a line each (<c>kind: interface</c>, no query).</summary>
    public static string List(bool windows)
    {
        var sb = new StringBuilder(ListHeading);
        foreach (string id in PaneBars.Names)
        {
            sb.Append("\n- ").Append(PaneBars.Title(id)).Append(": ").Append(PaneBars.Describe(id));
        }

        sb.Append("\n\n").Append(ItemsHeading);
        foreach (string id in ItemsHere(windows))
        {
            string glyph = id == ToolbarItems.Path ? "" : ToolbarItems.Glyph(id);
            sb.Append("\n- ").Append(glyph.Length > 0 ? glyph + " " : "").Append(ToolbarItems.Title(id)).Append(": ").Append(ToolbarItems.Describe(id));
        }

        return sb.ToString();
    }

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> s_windowsGlyphs = new(() => BuildGlyphMap(windows: true));
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> s_elsewhereGlyphs = new(() => BuildGlyphMap(windows: false));

    /// <summary>
    /// Every icon the toolbar draws on a system, U+FE0F dropped, to the items that draw it: the checklist's icons, and the row's two state
    /// icons 🔓 and 🥷 (<see cref="ChatScreen.CmdYoloToolGlyph"/>, <see cref="ChatScreen.NinjaToolGlyph"/>). The path draws its text, so
    /// nothing maps to it.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GlyphMap(bool windows) => windows ? s_windowsGlyphs.Value : s_elsewhereGlyphs.Value;

    private static Dictionary<string, IReadOnlyList<string>> BuildGlyphMap(bool windows)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string glyph, string id)
        {
            string key = Bare(glyph);
            if (key.Length == 0)
            {
                return;
            }

            if (!map.TryGetValue(key, out var ids))
            {
                map[key] = ids = [];
            }

            if (!ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }

        foreach (string id in ItemsHere(windows).Where(i => i is not (ToolbarItems.Path or ToolbarItems.Model)))
        {
            Add(ToolbarItems.Glyph(id), id);
        }

        Add(ChatScreen.CmdYoloToolGlyph, ToolbarItems.CmdList);
        Add(ChatScreen.NinjaToolGlyph, ToolbarItems.Police);
        return map.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// The items the icons in <paramref name="q"/> stand for, in its order (2026-10-25, step 4): the query split into whole clusters
    /// (<see cref="StringInfo"/>), so a joined sequence (ZWJ, a skin tone, a keycap) holding an icon as one part never matches it; U+FE0F dropped.
    /// </summary>
    public static List<string> ByGlyphs(string q, bool windows)
    {
        ArgumentNullException.ThrowIfNull(q);
        var map = GlyphMap(windows);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var clusters = StringInfo.GetTextElementEnumerator(q);
        while (clusters.MoveNext())
        {
            string cluster = Bare(clusters.GetTextElement());
            if (map.TryGetValue(cluster, out var ids))
            {
                foreach (string id in ids)
                {
                    if (seen.Add(id + "\u0000" + cluster))
                    {
                        found.Add(Item(id, windows, IconFor(cluster)));
                    }
                }
            }
        }

        return found;
    }

    // The icon to head an item asked by: the state icon itself (🔓, 🥷), else the checklist's.
    private static string? IconFor(string cluster) =>
        Bare(ChatScreen.CmdYoloToolGlyph) == cluster || Bare(ChatScreen.NinjaToolGlyph) == cluster ? cluster : null;

    private static string Bare(string glyph) => glyph.Replace("️", "", StringComparison.Ordinal);

    /// <summary>
    /// A bar by its full name (<c>tool bar</c>, <c>home bar</c>; the commands' one-word names <c>toolbar</c>, <c>perfbar</c> and <c>homebar</c>
    /// reach here only under <c>kind: interface</c>), else a toolbar item by its title (<c>saved links</c>, <c>shell police icon</c>); null for none.
    /// </summary>
    public static string? ByName(string q, bool windows)
    {
        ArgumentNullException.ThrowIfNull(q);
        string words = q.Trim().ToLowerInvariant();
        foreach (string id in PaneBars.Names)
        {
            string title = PaneBars.Title(id).ToLowerInvariant();
            if (words == title || words == title.Replace(" ", "", StringComparison.Ordinal) || words == "the " + title || words == id + "bar" || words == id + " bar")
            {
                return Bar(id, windows);
            }
        }

        foreach (string tail in (string[])[" icon", " item", " button", ""])
        {
            if (!words.EndsWith(tail, StringComparison.Ordinal))
            {
                continue;
            }

            string name = words[..^tail.Length].Trim();
            if (ItemsHere(windows).FirstOrDefault(i => string.Equals(ToolbarItems.Title(i), name, StringComparison.OrdinalIgnoreCase)) is { } item)
            {
                return Item(item, windows);
            }
        }

        return null;
    }

    /// <summary>The see-also line after <c>/toolbar</c>, <c>/perfbar</c> and <c>/homebar</c>'s answers: the bar itself is one question away; null for another command.</summary>
    public static string? SeeAlso(string command) => command switch
    {
        "/toolbar" => SeeAlsoBar(PaneBars.Toolbar),
        "/perfbar" => SeeAlsoBar(PaneBars.Perf),
        "/homebar" => SeeAlsoBar(PaneBars.Home),
        _ => null,
    };

    /// <summary><c>See also: the Tool bar itself (ask "tool bar").</c> Pinned.</summary>
    public static string SeeAlsoBar(string id) => $"See also: the {PaneBars.Title(id)} itself (ask \"{PaneBars.Title(id).ToLowerInvariant()}\").";

    /// <summary>The bars and the items for the free-word search: a name of three points, a text of one, the answer built when listed.</summary>
    public static IEnumerable<HelpItem> Items(bool windows)
    {
        foreach (string id in PaneBars.Names)
        {
            yield return new HelpItem(PaneBars.Title(id), PaneBars.Describe(id), () => Bar(id, windows));
        }

        foreach (string id in ItemsHere(windows))
        {
            yield return new HelpItem(ToolbarItems.Title(id) + " icon", ToolbarItems.Describe(id), () => Item(id, windows));
        }
    }
}
