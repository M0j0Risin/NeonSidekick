using System.Globalization;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The setting <c>Toolbar</c> as a checklist (2026-09-29, the user's ask, in place of the on/off switch of
/// 2026-09-21): one id per thing the toolbar can show — each pane glyph in strip order, then the working directory's
/// path — saved in <see cref="Settings.AppSettingsData.ToolbarItems"/>. Null there is <see cref="Defaults"/> (every item,
/// a new one too, until later on 2026-09-29, the user's call); an empty list is no toolbar row at all. <see cref="Resolve"/> is the one place the saved list becomes the set: a display
/// setting, so an unknown word is dropped without a warning. The glyphs are <see cref="ChatScreen"/>'s, one source; the
/// lock and police items keep the switches they followed before (<see cref="ChatScreen.ToolbarStripFor(IReadOnlySet{string}, Shell.CommandPolicyMode, bool)"/>);
/// the memory item is always drawn since later on 2026-10-03, on the off slab while Memory mode is disabled (<see cref="ChatScreen.ToolbarItemOff"/>).
/// Seventeen more on 2026-10-03 (the user's ask, their glyphs and names): the tool switches, each opening its group's switch
/// (<c>/tools &lt;group&gt;</c>) and drawn on the off slab while it is off, and the log and the two viewers, each opening its
/// window or closing it as its chord does; the user's order with them, the chart moved behind the log, and later that day
/// behind the two viewers, the last glyph on the row and the checklist (📈 Performance, retired on 2026-10-14, the user's ask: the
/// performance bar keeps /perfbar and Ctrl+F; a saved <c>perf</c> is an unknown word, dropped as any is). Two more on 2026-10-04 (the user's ask): 🧮 Themes after
/// the profile (<c>/theme</c>'s picker) and 🪟 the Comfy thumb viewer after the Comfy viewer (<c>/comfy thumbs</c>, opening or
/// closing the thumbnail browser) — 🪟 was SQL's until then, which took 🛢️. One more on 2026-10-05 (the user's ask): ⚡ Process
/// after the log, <c>/process</c>'s list of the model's background processes (a pane, not a window). 📺 YouTube in the Camera live
/// viewer's place on 2026-10-08 (the user's ask: the live viewer's item gone, <c>/camera live</c> and Ctrl+Alt+V still open its window),
/// <c>/youtube list</c>'s saved videos (a pane); a saved <c>liveview</c> is an unknown word, dropped as any is. Later that day (the
/// user's ask) 📺 moved behind 🪟, the two viewers together again. ⭐ Saved links after 📺 on 2026-10-13 (the user's ask and pick, off by
/// default): <c>/browser list</c>, the Browser window's saved links (a pane). 🙋 Ask on 2026-10-09 (the user's ask): the <c>Ask user</c> switch
/// between the disk and the lock, <c>/tools ask</c>. The order is the user's to change since 2026-10-12 (the user's ask, the home bar
/// picker's move keys on this checklist): <see cref="Order"/> over <see cref="Settings.AppSettingsData.ToolbarOrder"/>, the path
/// always last until 2026-10-13; <see cref="Names"/> is the default order, which <c>⊡ default</c> puts back with the checks. Since
/// 2026-10-13 (the user's ask) every item is pinned left or right (<see cref="Sides"/> over <see cref="Settings.AppSettingsData.ToolbarRight"/>,
/// <see cref="RightDefaults"/> the path and the model's name, the row as it was), each side in the order's own order.
/// </summary>
public static class ToolbarItems
{
    public const string Settings = "settings";
    public const string Profile = "profile";   // later on 2026-09-29, the user's ask: 🪪, /profile's picker
    public const string Themes = "themes";     // 2026-10-04, the user's ask: 🧮, /theme's picker
    public const string WebUi = "webui";       // 2026-10-14, the user's ask: 📱, /webui's pane, right after Themes
    public const string Tools = "tools";
    public const string Mcp = "mcp";
    public const string Skills = "skills";
    public const string Sys = "sys";
    public const string Sessions = "sessions";
    public const string Usage = "usage";
    public const string Memory = "memory";
    public const string Queue = "queue";       // 2026-10-13, the user's ask: ⏳, the /queue pane, after the disk (checked by default)
    public const string Ask = "ask";           // 2026-10-09, the user's ask: 🙋, the Ask user switch (/tools ask), between the disk and the lock
    public const string CmdList = "cmdlist";
    public const string Police = "police";

    // The tool switches (2026-10-03, the user's ask): each opens its group's switch on a pane (/tools <group>), and is drawn
    // on the off slab while the switch is off; Shell is the command policy, off/ask/yolo, on the slab under off. Each id is
    // the group's /tools word (ToolsText.SwitchWords).
    public const string Shell = "shell";
    public const string Files = "files";
    public const string Image = "image";     // 2026-10-10, the user's ask: 🌄, the Image tools switch (/tools image), after the folder
    public const string Web = "web";
    public const string Advisors = "advisors";   // 2026-10-10: 🦉, the Advisors enabled switch (/tools advisors), after Web (the Claude Code advisor's ✴️ "claude" stood between until it was retired that day; a saved "claude" is dropped)
    public const string Docker = "docker";
    public const string EventLog = "eventlog";   // 2026-10-15, the event-log plan: 🪵, the Event log tools switch (/tools eventlog), after Docker, off by default; on the slab off Windows
    public const string Network = "network";   // 2026-10-15, the admin-tools plan: 📡, the Network tools switch (/tools network), after the event logs, off by default
    public const string Services = "services";   // 2026-10-15: 🧩, the Service tools switch (/tools services), off by default; on the slab off Windows
    public const string Tasks = "tasks";   // 2026-10-15: 📅, the Task tools switch (/tools tasks), off by default; on the slab off Windows
    public const string Directory = "directory";   // 2026-10-15: 🏢, the Directory tools switch (/tools directory), off by default; on the slab off Windows
    public const string Obsidian = "obsidian";
    public const string Sql = "sql";
    public const string Oracle = "oracle";
    public const string MySql = "mysql";
    public const string Sqlite = "sqlite";     // 2026-10-04
    public const string Postgres = "postgres"; // 2026-10-04
    public const string Unc = "unc";
    public const string Ha = "ha";
    public const string Comfy = "comfy";
    public const string Camera = "camera";
    public const string Print = "print";

    // The windows (2026-10-03, the user's ask): each opens or closes its window as its Ctrl+Alt chord does.
    public const string Log = "log";
    public const string Process = "process";         // 2026-10-05, the user's ask: ⚡, /process's list (a pane)
    public const string ComfyView = "comfyview";
    public const string ComfyThumbs = "comfythumbs"; // 2026-10-04, the user's ask: 🪟, /comfy thumbs
    public const string YouTube = "youtube";         // 2026-10-08, the user's ask: 📺, /youtube list (a pane; after 🪟 later that day)
    public const string BrowserLinks = "browserlinks";   // later on 2026-10-13, the user's ask: ⭐, /browser list (a pane), after 📺, off by default
    public const string Prompts = "prompts";             // 2026-10-13, the user's ask: 📣, /prompts (a pane), after ⭐, off by default; after 💾 and checked by default since 2026-10-14 (the user's ask)
    public const string ShoppingList = "shoppinglist"; // 2026-10-11, the user's ask: 🛒, /shopping's list (a pane), after 📺; the Shopping tools switch (id shopping, after Print) until later that day
    public const string Path = "path";
    public const string Model = "model";   // 2026-10-12, the user's ask: the model's name (its reasoning glyph after it since later that day); after the path by default, and free to move past it

    /// <summary>
    /// Every item in strip order, the path last (it sits at the row's right). The order is the user's (2026-10-03): the panes,
    /// the disk, the lock and the officer, the tool switches, the log, the two viewers, the chart (the last glyph, later on
    /// 2026-10-03, the user's ask; retired on 2026-10-14); Themes after Profile and the Comfy thumb viewer after the Comfy viewer (2026-10-04); Process
    /// after the log (2026-10-05); YouTube where the Camera live viewer was (2026-10-08), after the Comfy thumb viewer later that day
    /// (the user's ask); Ask between the disk and the lock (2026-10-09, the user's ask); Image after Files (2026-10-10, the user's ask); Advisors after
    /// the Claude Code advisor (later that day, the profile advisors), after Web once that one was retired (later still); Queue after
    /// Memory (2026-10-13, the user's ask); Saved links after YouTube (later that day, the user's ask); Saved prompts after Saved links (later still), after Memory
    /// since 2026-10-14 (the user's ask); the Web UI after Themes (later that day, the user's ask). Pinned.
    /// </summary>
    public static readonly string[] Names =
    [
        Settings, Profile, Themes, WebUi, Tools, Mcp, Skills, Sys, Sessions, Usage, Memory, Prompts, Queue, Ask, CmdList, Police,
        Shell, Files, Image, Web, Advisors, Docker, EventLog, Network, Services, Tasks, Directory, Obsidian, Sql, Oracle, MySql, Sqlite, Postgres, Unc, Ha, Comfy, Camera, Print,
        Log, Process, ComfyView, ComfyThumbs, YouTube, BrowserLinks, ShoppingList, Path, Model,
    ];

    /// <summary>
    /// What a profile that never chose shows (2026-10-05, the user's pick): Settings, Tools, Skills, Memory, Saved prompts (2026-10-14, the user's ask),
    /// Queue (2026-10-13, the user's pick), Shell allowed
    /// commands, Shell police, the Shell, Files, Image (2026-10-10, the user's ask) and Web switches and the path — the lock and the officer still only under a
    /// policy other than off. Tools, Shell allowed commands, Shell police, the three switches and the path from 2026-10-03; Shell
    /// allowed commands, Shell police and the path from 2026-10-02; Settings, Tools, Skills, Sessions and the path from later on
    /// 2026-09-29, every item before; a profile that saved the defaults as null follows. In <see cref="Names"/> order, which
    /// <see cref="Save"/> compares by. Pinned.
    /// </summary>
    public static readonly string[] Defaults = [Settings, Tools, Skills, Memory, Prompts, Queue, CmdList, Police, Shell, Files, Image, Web, Path];

    /// <summary>
    /// The glyph an item draws on the checklist (the lock's closed one for <see cref="CmdList"/>; the folder for the path
    /// since 2026-09-29, the user's ask — the toolbar row itself still draws the bare path). Pinned.
    /// </summary>
    public static string Glyph(string id) => id switch
    {
        Settings => ChatScreen.SettingsToolGlyph,
        Profile => ChatScreen.ProfileToolGlyph,
        Themes => ChatScreen.ThemeToolGlyph,
        WebUi => ChatScreen.WebUiToolGlyph,
        Tools => ChatScreen.ToolsToolGlyph,
        Mcp => ChatScreen.McpToolGlyph,
        Skills => ChatScreen.SkillsToolGlyph,
        Sys => ChatScreen.SysToolGlyph,
        Sessions => ChatScreen.SessionsToolGlyph,
        Usage => ChatScreen.UsageToolGlyph,
        Memory => ChatScreen.MemoryToolGlyph,
        Queue => ChatScreen.QueueToolGlyph,
        Ask => ChatScreen.AskToolGlyph,
        CmdList => ChatScreen.CmdAskToolGlyph,
        Police => ChatScreen.PoliceToolGlyph,
        Shell => ChatScreen.ShellToolGlyph,
        Files => ChatScreen.FilesToolGlyph,
        Image => ChatScreen.ImageToolGlyph,
        Web => ChatScreen.WebToolGlyph,
        Advisors => ChatScreen.AdvisorsToolGlyph,
        Docker => ChatScreen.DockerToolGlyph,
        EventLog => ChatScreen.EventLogToolGlyph,
        Network => global::NeonSidekick.Network.NetworkText.Glyph,
        Services => global::NeonSidekick.Services.ServiceText.Glyph,
        Tasks => global::NeonSidekick.Tasks.TaskText.Glyph,
        Directory => global::NeonSidekick.ActiveDirectory.DirectoryText.Glyph,
        Obsidian => ChatScreen.ObsidianToolGlyph,
        Sql => ChatScreen.SqlToolGlyph,
        Oracle => ChatScreen.OracleToolGlyph,
        MySql => ChatScreen.MySqlToolGlyph,
        Sqlite => ChatScreen.SqliteToolGlyph,
        Postgres => ChatScreen.PostgresToolGlyph,
        Unc => ChatScreen.UncToolGlyph,
        Ha => ChatScreen.HaToolGlyph,
        Comfy => ChatScreen.ComfyToolGlyph,
        Camera => ChatScreen.CameraToolGlyph,
        Print => ChatScreen.PrintToolGlyph,
        Log => ChatScreen.LogToolGlyph,
        Process => ChatScreen.ProcessToolGlyph,
        ComfyView => ChatScreen.ComfyViewToolGlyph,
        ComfyThumbs => ChatScreen.ComfyThumbsToolGlyph,
        YouTube => ChatScreen.YouTubeToolGlyph,
        BrowserLinks => ChatScreen.BrowserLinksToolGlyph,
        Prompts => ChatScreen.PromptsToolGlyph,
        ShoppingList => ChatScreen.ShoppingToolGlyph,
        Path => FolderText.FolderGlyph,
        _ => "",
    };

    /// <summary>An item's name on the checklist. Pinned.</summary>
    public static string Title(string id) => id switch
    {
        Settings => "Settings",
        Profile => "Profile",
        Themes => "Themes",
        WebUi => "Web UI",
        Tools => "Tools",
        Mcp => "MCP",
        Skills => "Skills",
        Sys => "System prompt",
        Sessions => "Sessions",
        Usage => "Usage",
        Memory => "Memory",
        Queue => "Queue",
        Ask => "Ask",
        CmdList => "Shell allowed commands",
        Police => "Shell police",
        Shell => "Shell",
        Files => "Files",
        Image => "Image",
        Web => "Web",
        Advisors => "Advisors",
        Docker => "Docker",
        EventLog => "Event logs",
        Network => "Network",
        Services => "Services",
        Tasks => "Tasks",
        Directory => "Directory",
        Obsidian => "Obsidian",
        Sql => "SQL",
        Oracle => "Oracle",
        MySql => "MySQL",
        Sqlite => "SQLite",
        Postgres => "PostgreSQL",
        Unc => "UNC",
        Ha => "Home",   // "HA" until 2026-10-09 (the user's ask, after /tools' tab became Home)
        Comfy => "ComfyUI",
        Camera => "Camera",
        Print => "Print",
        Log => "Log",
        Process => "Process",
        ComfyView => "Comfy viewer",
        ComfyThumbs => "Comfy thumb viewer",
        YouTube => "YouTube",
        BrowserLinks => "Saved links",
        Prompts => "Saved prompts",
        ShoppingList => "Shopping list",
        Path => "Working directory path",
        Model => "Model name",
        _ => id,
    };

    /// <summary>
    /// The dim note after an item's name: what a double-click opens, nothing more (2026-10-02, the user's ask; when the
    /// item shows at all was said there too until then). Pinned.
    /// </summary>
    public static string Describe(string id) => id switch
    {
        Settings => "/settings",
        Profile => "/profile",
        Themes => SlashCommands.ThemeWord,
        WebUi => SlashCommands.WebUiWord,
        Tools => "/tools",
        Mcp => "/mcp",
        Skills => "/skills",
        Sys => "/sys",
        Sessions => "/sessions",
        Usage => "/usage",
        Memory => "/memory",
        Queue => SlashCommands.QueueWord,
        CmdList => ToolsText.ShellSubLine(ToolsText.ShellAllowedWord),
        Police => ToolsText.ShellSubLine(ToolsText.ShellPoliceWord),
        Log => ChatScreen.LogToolLine,
        Process => Viewer.ProcessWindowText.Word,
        ComfyView => ChatScreen.ComfyViewToolLine,
        ComfyThumbs => ChatScreen.ComfyThumbsToolLine,
        YouTube => ChatScreen.YouTubeToolLine,
        BrowserLinks => ChatScreen.BrowserLinksToolLine,
        Prompts => ChatScreen.PromptsToolLine,
        ShoppingList => ChatScreen.ShoppingToolLine,
        Path => "/cwd browse",
        Model => SlashCommands.ServerWord,
        _ => ToolsText.SwitchField(id) is null ? "" : ToolsText.SwitchLine(id),
    };

    /// <summary>The tail of the model name's note (<see cref="ModelNote"/>): what the second click does. Pinned.</summary>
    public const string DoubleClickNote = "double-click to open";

    /// <summary>The tail of a glyph's note (<see cref="Note"/>, 2026-10-13): the second click, or a right-click, as on the home bar. Pinned.</summary>
    public const string GlyphClickNote = "double-click or right-click to open";

    /// <summary>
    /// What one click on a toolbar glyph puts on the hint row (2026-10-04, the UI review: a row of glyphs and no way to tell them
    /// apart but to open each): the glyph, the item's name, its state where it has one, and <see cref="GlyphClickNote"/> —
    /// <c>🐚 Shell: ask · double-click or right-click to open</c>, <c>⚙️ Settings · double-click or right-click to open</c>. Pinned.
    /// </summary>
    public static string Note(string glyph, string id, string? state) =>
        glyph + " " + Title(id) + (state is null ? "" : ": " + state) + " · " + GlyphClickNote;

    /// <summary>A divider's name on the checklist (2026-10-13). Pinned.</summary>
    public const string DividerTitle = "Divider";

    /// <summary>A divider's dim note on the checklist (2026-10-13). Pinned.</summary>
    public const string DividerNote = "a divider between the items around it";

    /// <summary>One click on the model's name on the toolbar (2026-10-12): <c>Model name: Qwen3-30B · double-click to open</c>. Pinned.</summary>
    public static string ModelNote(string name) => Title(Model) + ": " + name + " · " + DoubleClickNote;

    /// <summary>The checklist's name column: "Working directory path" (22) plus two.</summary>
    public const int TitleWidth = 24;

    /// <summary>
    /// One row of the checklist: the mark, the glyph (two blanks for one without, so the names line up — the path's until
    /// it had the folder, 2026-09-29), the name and <see cref="Describe"/> dimmed. Pinned.
    /// </summary>
    public static string Label(string id, bool on)
    {
        if (ItemOrder.IsDivider(id))
        {
            // No check box (2026-10-13): a divider is on by being in the order; Delete takes it out.
            return Markup.Escape("    " + ItemOrder.DividerGlyph + "   " + DividerTitle.PadRight(TitleWidth)) + Theme.DimMarkup(DividerNote);
        }

        string glyph = Glyph(id);
        return Markup.Escape((on ? "[x] " : "[ ] ") + (glyph.Length == 0 ? "  " : glyph) + "  " + Title(id).PadRight(TitleWidth)) + Theme.DimMarkup(Describe(id));
    }

    /// <summary>The items <paramref name="saved"/> names: <see cref="Defaults"/> when null, the known ids (trimmed, any case) otherwise.</summary>
    public static IReadOnlySet<string> Resolve(IReadOnlyList<string>? saved)
    {
        if (saved is null)
        {
            return Defaults.ToHashSet(StringComparer.Ordinal);
        }

        // A null in a hand-edited list is skipped (later on 2026-09-30): this runs on the pane's tick, where a throw repeats.
        var wanted = saved.OfType<string>().Select(w => w.Trim().ToLowerInvariant()).Select(w => w == ToolsText.ShoppingSwitchWord ? ShoppingList : w).ToHashSet(StringComparer.Ordinal);   // 🛒's id was the switch word for a few hours (2026-10-11): a profile that checked it keeps the cart
        return Names.Where(wanted.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// What saves for <paramref name="on"/>: null when it is exactly <see cref="Defaults"/> (so the profile follows a later
    /// default), else the ids in <see cref="Names"/> order — every item a full list since the defaults narrowed (2026-09-29).
    /// </summary>
    public static List<string>? Save(IReadOnlySet<string> on)
    {
        ArgumentNullException.ThrowIfNull(on);
        var chosen = Names.Where(on.Contains).ToList();
        return chosen.SequenceEqual(Defaults, StringComparer.Ordinal) ? null : chosen;
    }

    /// <summary>
    /// The default order (2026-10-13, the user's ask: <c>⚙️ 🛠️ 🎓 | 💾 📣 ⏳ | 🔒 👮 🐚 | 📁 🌄 🌐</c>, 📣 since 2026-10-14): <see cref="Names"/> with a divider ahead of
    /// Memory (later still that day, the user's ask; after Usage, so 💾 📣 ⏳ stay together when System prompt, Sessions or Usage is checked), one after
    /// Queue (ahead of Ask, unchecked by default; after Memory until Queue came later that day) and one after Shell, numbered from the left. A
    /// divider whose neighbours are hidden is not drawn (<see cref="ItemOrder.ShownDividers"/>). Pinned.
    /// </summary>
    public static readonly string[] DefaultOrder =
    [
        .. Names.TakeWhile(id => id != Memory), ItemOrder.DividerPrefix + "1",
        .. Names.SkipWhile(id => id != Memory).TakeWhile(id => id != Ask), ItemOrder.DividerPrefix + "2",
        .. Names.SkipWhile(id => id != Ask).TakeWhile(id => id != Files), ItemOrder.DividerPrefix + "3",
        .. Names.SkipWhile(id => id != Files),
    ];

    /// <summary>
    /// The checklist's and the strip's order for <paramref name="saved"/> (<see cref="Settings.AppSettingsData.ToolbarOrder"/>,
    /// 2026-10-12): every item, <see cref="DefaultOrder"/> for null (<see cref="ItemOrder.Resolve"/>); the path pinned last until
    /// 2026-10-13, when the sides came (<see cref="Sides"/>), and the dividers with them (later that day, kept where saved).
    /// </summary>
    public static List<string> Order(IReadOnlyList<string>? saved) => ItemOrder.Resolve(saved, Names, keepDividers: true, defaultOrder: DefaultOrder);

    /// <summary>
    /// The items pinned right by default (2026-10-13, the user's ask: any item pinned left or right): the path and the model's name,
    /// the row as it was — the glyphs at the left, the path then the name at the right edge. Pinned.
    /// </summary>
    public static readonly string[] RightDefaults = [Path, Model];

    /// <summary>The items <paramref name="saved"/> (<see cref="Settings.AppSettingsData.ToolbarRight"/>) pins right: <see cref="RightDefaults"/> for null.</summary>
    public static IReadOnlySet<string> ResolveRight(IReadOnlyList<string>? saved) => ItemOrder.ResolveRight(saved, Names, RightDefaults, keepDividers: true);

    /// <summary>What saves for the right group: null for exactly <see cref="RightDefaults"/>.</summary>
    public static List<string>? SaveRight(IEnumerable<string> right) => ItemOrder.SaveRight(right, Names, RightDefaults);

    /// <summary>The toolbar's two groups for the saved <paramref name="order"/> and <paramref name="right"/> (2026-10-13): every item, checked or not.</summary>
    public static ItemOrder.Sided Sides(IReadOnlyList<string>? order, IReadOnlyList<string>? right) => ItemOrder.Split(Order(order), ResolveRight(right));

    /// <summary>What saves for <paramref name="order"/>: null for <see cref="DefaultOrder"/> (<see cref="ItemOrder.Save"/>).</summary>
    public static List<string>? SaveOrder(IReadOnlyList<string> order) => ItemOrder.Save(order, Names, DefaultOrder);

    /// <summary><c>/toolbar</c>'s words (later on 2026-09-30): <c>on</c> and <c>off</c>, the completion's list. Pinned.</summary>
    public const string OnWord = "on";

    public const string OffWord = "off";

    /// <summary><c>/toolbar edit</c> (2026-10-12, the user's ask, as <c>/homebar edit</c>): the <c>Toolbar</c> row's checklist. Pinned.</summary>
    public const string EditWord = "edit";

    public static readonly string[] Words = [OnWord, OffWord, EditWord];

    /// <summary>Whether <c>/toolbar</c>'s argument is <see cref="EditWord"/> (any case, trimmed): the checklist, a pane, not a toggle. Pure.</summary>
    public static bool IsEdit(string args) => string.Equals(args.Trim(), EditWord, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>/toolbar</c>'s completion hint beside a word. Pinned.</summary>
    public static string DescribeWord(string word) => word switch
    {
        OnWord => "show the toolbar with the items it last had",
        OffWord => "hide the toolbar",
        EditWord => "choose the toolbar's items and their order",
        _ => "",
    };

    /// <summary><c>/toolbar</c>'s row on <c>/help</c>. Pinned.</summary>
    public const string HelpSummary = "show or hide the toolbar";

    /// <summary>What <c>/toolbar</c> says it did. Pinned.</summary>
    public static string Notice(bool shown) => shown ? "(toolbar on)" : "(toolbar off)";

    /// <summary><c>/toolbar</c> given something that is not on or off. Pinned.</summary>
    public const string UsageError = "/toolbar takes on, off or edit, or nothing to toggle.";

    /// <summary>What <c>/toolbar</c> saves: the toolbar's items, and the ones a later <c>/toolbar</c> brings back.</summary>
    public readonly record struct ToolbarToggle(List<string>? Items, List<string>? LastItems);

    /// <summary>
    /// What <c>/toolbar</c> saves, pure (later on 2026-09-30, the user's ask, <see cref="PerfBarMode.Toggle"/>'s shape): bare, the
    /// toolbar hidden while it shows — an empty list, its items kept in <paramref name="last"/> as <see cref="Save"/> would
    /// write them (null for <see cref="Defaults"/>) — else shown again with <paramref name="last"/> (<see cref="Defaults"/> when
    /// it is null or names nothing); <c>on</c> and <c>off</c> say which, and leave a toolbar already that way as it is. Null for
    /// anything else, the usage error.
    /// </summary>
    public static ToolbarToggle? Toggle(string args, IReadOnlyList<string>? items, IReadOnlyList<string>? last)
    {
        ArgumentNullException.ThrowIfNull(args);
        var shown = Resolve(items);
        string word = args.Trim();
        bool? wanted = word.Length == 0 ? shown.Count == 0
            : string.Equals(word, OnWord, StringComparison.OrdinalIgnoreCase) ? true
            : string.Equals(word, OffWord, StringComparison.OrdinalIgnoreCase) ? false
            : null;
        if (wanted is not { } show)
        {
            return null;
        }

        if (show == shown.Count > 0)
        {
            return new(items?.ToList(), last?.ToList());
        }

        if (!show)
        {
            return new([], Save(shown));
        }

        return new(last is null || Resolve(last).Count == 0 ? null : Save(Resolve(last)), last?.ToList());
    }

    /// <summary>The <c>Toolbar</c> row's value: <c>all</c>, <c>off</c> with nothing checked, else <c>7 of 34</c>. Pinned.</summary>
    public static string Value(IReadOnlyList<string>? saved)
    {
        int count = Resolve(saved).Count;
        return count == Names.Length ? "all"
            : count == 0 ? "off"
            : count.ToString(CultureInfo.InvariantCulture) + " of " + Names.Length.ToString(CultureInfo.InvariantCulture);
    }
}
