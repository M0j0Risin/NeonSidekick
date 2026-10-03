using System.Globalization;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The setting <c>Show toolbar</c> as a checklist (2026-09-29, the user's ask, in place of the on/off switch of
/// 2026-09-21): one id per thing the toolbar can show — each pane glyph in strip order, then the working directory's
/// path — saved in <see cref="Settings.AppSettingsData.ToolbarItems"/>. Null there is <see cref="Defaults"/> (every item,
/// a new one too, until later on 2026-09-29, the user's call); an empty list is no toolbar row at all. <see cref="Resolve"/> is the one place the saved list becomes the set: a display
/// setting, so an unknown word is dropped without a warning. The glyphs are <see cref="ChatScreen"/>'s, one source; the
/// memory, lock and police items keep the switches they followed before (<see cref="ChatScreen.ToolbarStripFor(IReadOnlySet{string}, bool, Shell.CommandPolicyMode, bool)"/>).
/// </summary>
public static class ToolbarItems
{
    public const string Settings = "settings";
    public const string Profile = "profile";   // later on 2026-09-29, the user's ask: 🪪, /profile's picker
    public const string Tools = "tools";
    public const string Mcp = "mcp";
    public const string Skills = "skills";
    public const string Sys = "sys";
    public const string Sessions = "sessions";
    public const string Usage = "usage";
    public const string Perf = "perf";         // later on 2026-09-29, the user's ask: 📈, the performance bar shown or hidden
    public const string Memory = "memory";
    public const string CmdList = "cmdlist";
    public const string Police = "police";
    public const string Path = "path";

    /// <summary>Every item in strip order, the path last (it sits at the row's right). Pinned.</summary>
    public static readonly string[] Names = [Settings, Profile, Tools, Mcp, Skills, Sys, Sessions, Usage, Perf, Memory, CmdList, Police, Path];

    /// <summary>
    /// What a profile that never chose shows (2026-10-02, the user's pick): Shell allowed commands, Shell police and the
    /// path — the lock and the officer still only under a policy other than off. Settings, Tools, Skills, Sessions and the
    /// path from later on 2026-09-29, every item before; a profile that saved the defaults as null follows. Pinned.
    /// </summary>
    public static readonly string[] Defaults = [CmdList, Police, Path];

    /// <summary>
    /// The glyph an item draws on the checklist (the lock's closed one for <see cref="CmdList"/>; the folder for the path
    /// since 2026-09-29, the user's ask — the toolbar row itself still draws the bare path). Pinned.
    /// </summary>
    public static string Glyph(string id) => id switch
    {
        Settings => ChatScreen.SettingsToolGlyph,
        Profile => ChatScreen.ProfileToolGlyph,
        Tools => ChatScreen.ToolsToolGlyph,
        Mcp => ChatScreen.McpToolGlyph,
        Skills => ChatScreen.SkillsToolGlyph,
        Sys => ChatScreen.SysToolGlyph,
        Sessions => ChatScreen.SessionsToolGlyph,
        Usage => ChatScreen.UsageToolGlyph,
        Perf => ChatScreen.PerfToolGlyph,
        Memory => ChatScreen.MemoryToolGlyph,
        CmdList => ChatScreen.CmdAskToolGlyph,
        Police => ChatScreen.PoliceToolGlyph,
        Path => FolderText.FolderGlyph,
        _ => "",
    };

    /// <summary>An item's name on the checklist. Pinned.</summary>
    public static string Title(string id) => id switch
    {
        Settings => "Settings",
        Profile => "Profile",
        Tools => "Tools",
        Mcp => "MCP",
        Skills => "Skills",
        Sys => "System prompt",
        Sessions => "Sessions",
        Usage => "Usage",
        Perf => "Performance",
        Memory => "Memory",
        CmdList => "Shell allowed commands",
        Police => "Shell police",
        Path => "Working directory path",
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
        Tools => "/tools",
        Mcp => "/mcp",
        Skills => "/skills",
        Sys => "/sys",
        Sessions => "/sessions",
        Usage => "/usage",
        Perf => "/perf",
        Memory => "/memory",
        CmdList => "/cmdlist",
        Police => "/police",
        Path => "/cwd browse",
        _ => "",
    };

    /// <summary>The checklist's name column: "Working directory path" (22) plus two.</summary>
    public const int TitleWidth = 24;

    /// <summary>
    /// One row of the checklist: the mark, the glyph (two blanks for one without, so the names line up — the path's until
    /// it had the folder, 2026-09-29), the name and <see cref="Describe"/> dimmed. Pinned.
    /// </summary>
    public static string Label(string id, bool on)
    {
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
        var wanted = saved.OfType<string>().Select(w => w.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
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

    /// <summary><c>/tb</c>'s words (later on 2026-09-30): <c>on</c> and <c>off</c>, the completion's list. Pinned.</summary>
    public const string OnWord = "on";

    public const string OffWord = "off";

    public static readonly string[] Words = [OnWord, OffWord];

    /// <summary><c>/tb</c>'s completion hint beside a word. Pinned.</summary>
    public static string DescribeWord(string word) => word switch
    {
        OnWord => "show the toolbar with the items it last had",
        OffWord => "hide the toolbar",
        _ => "",
    };

    /// <summary><c>/tb</c>'s row on <c>/help</c>. Pinned.</summary>
    public const string HelpSummary = "show or hide the toolbar, or /tb on|off";

    /// <summary>What <c>/tb</c> says it did. Pinned.</summary>
    public static string Notice(bool shown) => shown ? "(toolbar on)" : "(toolbar off)";

    /// <summary><c>/tb</c> given something that is not on or off. Pinned.</summary>
    public const string UsageError = "/tb takes on or off, or nothing to toggle.";

    /// <summary>What <c>/tb</c> saves: the toolbar's items, and the ones a later <c>/tb</c> brings back.</summary>
    public readonly record struct ToolbarToggle(List<string>? Items, List<string>? LastItems);

    /// <summary>
    /// What <c>/tb</c> saves, pure (later on 2026-09-30, the user's ask, <see cref="PerfBarMode.Toggle"/>'s shape): bare, the
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

    /// <summary>The <c>Show toolbar</c> row's value: <c>all</c>, <c>off</c> with nothing checked, else <c>4 of 13</c>. Pinned.</summary>
    public static string Value(IReadOnlyList<string>? saved)
    {
        int count = Resolve(saved).Count;
        return count == Names.Length ? "all"
            : count == 0 ? "off"
            : count.ToString(CultureInfo.InvariantCulture) + " of " + Names.Length.ToString(CultureInfo.InvariantCulture);
    }
}
