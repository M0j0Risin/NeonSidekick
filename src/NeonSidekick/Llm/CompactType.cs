using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm;

/// <summary>What <c>/compact</c> does to the history (<see cref="ConversationCompactor"/>).</summary>
public enum CompactMode
{
    /// <summary>The older turns become one summary message; the recent turns stay verbatim. One model request.</summary>
    Summary,

    /// <summary>Every turn stays; the bulky tool results and long call arguments (2026-10-17) in the older turns become one-line stubs. No model request.</summary>
    Prune,
}

/// <summary>
/// The compact-type setting: the two words the operator picks from (<c>summary</c>, <c>prune</c>)
/// and their mapping to <see cref="CompactMode"/>, the way <see cref="ReasoningLevel"/> maps the
/// reasoning words. <see cref="Resolve"/> is the one place the saved string becomes the enum: a
/// hand-edited value that is neither falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class CompactType
{
    /// <summary>Summarise and replace. The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "summary";

    /// <summary>The types in menu order.</summary>
    public static readonly string[] Names = { "summary", "prune" };

    private const string Category = "Llm";

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out CompactMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "summary": mode = CompactMode.Summary; return true;
            case "prune": mode = CompactMode.Prune; return true;
            default: mode = CompactMode.Summary; return false;
        }
    }

    /// <summary>The saved word for <paramref name="mode"/>.</summary>
    public static string Name(CompactMode mode) => mode switch
    {
        CompactMode.Prune => "prune",
        _ => "summary",
    };

    /// <summary>The menu hint next to a type. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "summary" => "summarize the older turns into one message, keep the recent ones",
        "prune" => "stub the bulky tool results and call arguments in the older turns, keep every turn",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static CompactMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.LlmCompactType, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.LlmCompactType)}='{effective.LlmCompactType}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out mode);
        return mode;
    }
}
