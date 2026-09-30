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
    /// What a profile that never chose shows (later on 2026-09-29, the user's pick): Settings, Tools, Skills, Sessions and
    /// the path. Every item before, so a profile that had saved "all" as null shows these five from then on. Pinned.
    /// </summary>
    public static readonly string[] Defaults = [Settings, Tools, Skills, Sessions, Path];

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

    /// <summary>The dim note after an item's name: what a double-click opens, and when the item shows at all. Pinned.</summary>
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
        Perf => "/perf · shows or hides the performance bar",
        Memory => "/memory · while Memory is on",
        CmdList => "/cmdlist · " + ChatScreen.CmdAskToolGlyph + " under ask, " + ChatScreen.CmdYoloToolGlyph + " under yolo, none under off",
        Police => "/police · while Shell police outside paths is on",
        Path => "/cwd browse · at the row's right",
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

        var wanted = saved.Select(w => w.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
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

    /// <summary>The <c>Show toolbar</c> row's value: <c>all</c>, <c>off</c> with nothing checked, else <c>4 of 13</c>. Pinned.</summary>
    public static string Value(IReadOnlyList<string>? saved)
    {
        int count = Resolve(saved).Count;
        return count == Names.Length ? "all"
            : count == 0 ? "off"
            : count.ToString(CultureInfo.InvariantCulture) + " of " + Names.Length.ToString(CultureInfo.InvariantCulture);
    }
}
