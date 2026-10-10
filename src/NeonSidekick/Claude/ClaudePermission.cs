using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Claude;

/// <summary>What the Claude Code child may do on its own (<c>/claude</c>, 2026-09-27).</summary>
public enum ClaudePermissionLevel
{
    /// <summary>Read, search and fetch: <c>--tools Read,Grep,Glob,WebSearch,WebFetch</c>. Nothing is written, nothing run.</summary>
    ReadOnly,

    /// <summary>Its default tools under <c>--permission-mode acceptEdits</c>: file edits go through, a command is denied.</summary>
    ReadWrite,
}

/// <summary>
/// The <c>Claude Code slash command permissions</c> setting (2026-09-27, the user's pick of the four offered: a level, denied
/// automatically past it — <c>--permission-prompts none</c> — rather than asked on the approval pane): the two
/// words and their mapping to <see cref="ClaudePermissionLevel"/>, the way <see cref="Llm.CompactType"/> maps its
/// words. <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited value that is none
/// of them falls back to <see cref="Default"/> with a warning — the safe end, never the open one.
/// 2026-10-10, the user's call: <c>full</c> (<c>bypassPermissions</c>, commands run unasked) is retired, so <c>/claude</c>
/// never runs a command, and <c>edit</c> is renamed <c>read-write</c>, its behaviour unchanged. No migration: a saved
/// <c>edit</c> or <c>full</c> is an unknown word now and reads as <c>read-only</c>.
/// </summary>
public static class ClaudePermission
{
    /// <summary>Read-only. The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "read-only";

    /// <summary>The levels in menu order, the narrowest first.</summary>
    public static readonly string[] Names = { "read-only", "read-write" };

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out ClaudePermissionLevel level)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "read-only": level = ClaudePermissionLevel.ReadOnly; return true;
            case "read-write": level = ClaudePermissionLevel.ReadWrite; return true;
            default: level = ClaudePermissionLevel.ReadOnly; return false;
        }
    }

    /// <summary>The saved word for <paramref name="level"/>.</summary>
    public static string Name(ClaudePermissionLevel level) => level switch
    {
        ClaudePermissionLevel.ReadWrite => "read-write",
        _ => "read-only",
    };

    /// <summary>The menu hint next to a level. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "read-only" => "read, search and fetch; nothing written, nothing run",
        "read-write" => "edit files without asking; commands denied",
        _ => "",
    };

    /// <summary>The level in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static ClaudePermissionLevel Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.ClaudeCodePermissions, out var level))
        {
            return level;
        }

        DiagnosticLog.Warn(ClaudeText.Category,
            $"{nameof(AppSettingsData.ClaudeCodePermissions)}='{effective.ClaudeCodePermissions}' is not one of {string.Join(", ", Names)}. Using {Default}.");
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

        DiagnosticLog.Warn(ClaudeText.Category, $"{nameof(AppSettingsData.ClaudeCodeEffort)}='{saved}' is not one of {string.Join(", ", Names.Skip(1))}. Leaving it to the CLI.");
        return null;
    }
}

/// <summary>
/// The <c>Claude Code slash command model</c> / <c>Claude Code advisor tool model</c> pickers' words (2026-09-27, the user's ask: pick, don't type):
/// the aliases <c>claude --model</c> takes for the latest of each family (its help names <c>fable</c>, <c>opus</c> and
/// <c>sonnet</c>; <c>haiku</c> is the live tests'). A full model name is still typed, through the picker's <c>Other…</c> row;
/// whatever is saved goes to <c>--model</c> as it is. Pure.
/// </summary>
public static class ClaudeModels
{
    /// <summary>The picker's alias rows, between the blank row and <c>Other…</c>.</summary>
    public static readonly string[] Aliases = { "fable", "opus", "sonnet", "haiku" };

    /// <summary>What each alias picks, for the picker's hint. Pinned.</summary>
    public static string Describe(string alias) => alias switch
    {
        "fable" => "the latest Fable",
        "opus" => "the latest Opus",
        "sonnet" => "the latest Sonnet",
        "haiku" => "the latest Haiku: fast, the cheapest",
        _ => "",
    };

    /// <summary>The alias <paramref name="saved"/> names (any case, trimmed), or null for a blank or a full name.</summary>
    public static string? AliasOf(string? saved)
    {
        string word = saved?.Trim().ToLowerInvariant() ?? "";
        return Array.IndexOf(Aliases, word) >= 0 ? word : null;
    }
}
