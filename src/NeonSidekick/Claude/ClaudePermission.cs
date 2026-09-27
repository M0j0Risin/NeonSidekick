using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Claude;

/// <summary>What the Claude Code child may do on its own (<c>/claude</c>, 2026-09-27).</summary>
public enum ClaudePermissionLevel
{
    /// <summary>Read, search and fetch: <c>--tools Read,Grep,Glob,WebSearch,WebFetch</c>. Nothing is written, nothing run.</summary>
    ReadOnly,

    /// <summary>Its default tools under <c>--permission-mode acceptEdits</c>: file edits go through, a command is denied.</summary>
    Edit,

    /// <summary>Everything, unasked: <c>--permission-mode bypassPermissions</c>.</summary>
    Full,
}

/// <summary>
/// The <c>Claude permissions</c> setting (2026-09-27, the user's pick of the four offered: a level, denied
/// automatically past it — <c>--permission-prompts none</c> — rather than asked on the approval pane): the three
/// words and their mapping to <see cref="ClaudePermissionLevel"/>, the way <see cref="Llm.CompactType"/> maps its
/// words. <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited value that is none
/// of them falls back to <see cref="Default"/> with a warning — the safe end, never the open one.
/// </summary>
public static class ClaudePermission
{
    /// <summary>Read-only. The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "read-only";

    /// <summary>The levels in menu order, the narrowest first.</summary>
    public static readonly string[] Names = { "read-only", "edit", "full" };

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out ClaudePermissionLevel level)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "read-only": level = ClaudePermissionLevel.ReadOnly; return true;
            case "edit": level = ClaudePermissionLevel.Edit; return true;
            case "full": level = ClaudePermissionLevel.Full; return true;
            default: level = ClaudePermissionLevel.ReadOnly; return false;
        }
    }

    /// <summary>The saved word for <paramref name="level"/>.</summary>
    public static string Name(ClaudePermissionLevel level) => level switch
    {
        ClaudePermissionLevel.Edit => "edit",
        ClaudePermissionLevel.Full => "full",
        _ => "read-only",
    };

    /// <summary>The menu hint next to a level. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "read-only" => "read, search and fetch; nothing written, nothing run",
        "edit" => "edit files without asking; commands denied",
        "full" => "everything, unasked, commands included",
        _ => "",
    };

    /// <summary>The level in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static ClaudePermissionLevel Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.ClaudePermissions, out var level))
        {
            return level;
        }

        DiagnosticLog.Warn(ClaudeText.Category,
            $"{nameof(AppSettingsData.ClaudePermissions)}='{effective.ClaudePermissions}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return ClaudePermissionLevel.ReadOnly;
    }
}

/// <summary>
/// The <c>Claude effort</c> setting (2026-09-27): blank for the CLI's own default, else one of the words
/// <c>claude --effort</c> takes. Pure; an unknown saved word is left off the command line with a warning.
/// </summary>
public static class ClaudeEffort
{
    /// <summary>Blank: the CLI decides.</summary>
    public const string Default = "";

    /// <summary>The picker's rows: blank (the CLI's default) first, then <c>--effort</c>'s words.</summary>
    public static readonly string[] Names = { "", "low", "medium", "high", "xhigh", "max" };

    /// <summary>The word to pass, or null for none — blank, or not one of <see cref="Names"/> (warned).</summary>
    public static string? Resolve(string? saved)
    {
        string word = saved?.Trim().ToLowerInvariant() ?? "";
        if (word.Length == 0)
        {
            return null;
        }

        if (Array.IndexOf(Names, word) > 0)
        {
            return word;
        }

        DiagnosticLog.Warn(ClaudeText.Category, $"{nameof(AppSettingsData.ClaudeEffort)}='{saved}' is not one of {string.Join(", ", Names.Skip(1))}. Leaving it to the CLI.");
        return null;
    }
}
