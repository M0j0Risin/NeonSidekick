using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm;

/// <summary>
/// What the tool loop does when a request's usage reaches the <c>LLM auto compact (%)</c> share of
/// the window mid-turn (<see cref="Assistant.ContextGuard"/>) — the automatic compact checks only
/// at the top of a message, and one message can walk the context to the ceiling by itself.
/// </summary>
public enum ToolCompactMode
{
    /// <summary>
    /// <see cref="Prune"/> first; when the next request is still estimated at or over the share, a summary
    /// (2026-09-28, the user's report: a long turn climbed past the share toward the ceiling with nothing
    /// left to prune) — the turns before this one, then, if that is not enough, this turn's earlier iterations.
    /// </summary>
    Compact,

    /// <summary>This turn's older tool results (every iteration's but the last) become stubs and the loop carries on.</summary>
    Prune,

    /// <summary>The turn ends with an error notice; the results so far stay in the history.</summary>
    Stop,

    /// <summary>No check: the server's own limit answers.</summary>
    Nothing,
}

/// <summary>
/// The tool-compact-type setting: the four words the operator picks from (<c>compact</c>, <c>prune</c>, <c>stop</c>,
/// <c>nothing</c>) and their mapping to <see cref="ToolCompactMode"/>, the <see cref="CompactType"/>
/// shape. <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited
/// value that is none of them falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class ToolCompactType
{
    /// <summary>
    /// Prune, then summarise if that was not enough. The compiled default since 2026-09-28 (it was <c>prune</c>:
    /// a prune alone cannot touch the turns before this one, the model's own call arguments or the last
    /// iteration, and a long turn walked past the share toward the ceiling), pinned by <c>AppSettingsTests</c>.
    /// </summary>
    public const string Default = "compact";

    /// <summary>The types in menu order.</summary>
    public static readonly string[] Names = { "compact", "prune", "stop", "nothing" };

    private const string Category = "Llm";

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out ToolCompactMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "compact": mode = ToolCompactMode.Compact; return true;
            case "prune": mode = ToolCompactMode.Prune; return true;
            case "stop": mode = ToolCompactMode.Stop; return true;
            case "nothing": mode = ToolCompactMode.Nothing; return true;
            default: mode = ToolCompactMode.Compact; return false;
        }
    }

    /// <summary>The saved word for <paramref name="mode"/>.</summary>
    public static string Name(ToolCompactMode mode) => mode switch
    {
        ToolCompactMode.Prune => "prune",
        ToolCompactMode.Stop => "stop",
        ToolCompactMode.Nothing => "nothing",
        _ => "compact",
    };

    /// <summary>The menu hint next to a type. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "compact" => "prune, then summarise if the turn is still over the share",
        "prune" => "stub this turn's older tool results and carry on",
        "stop" => "end the turn with a notice; /compact or /clear first",
        "nothing" => "no check; the server's own limit answers",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static ToolCompactMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.LlmToolCompactType, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.LlmToolCompactType)}='{effective.LlmToolCompactType}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out mode);
        return mode;
    }
}
