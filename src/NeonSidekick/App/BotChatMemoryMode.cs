using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>Whose memories the <c>/botchat</c> bots use (<see cref="BotChatMemoryMode"/>).</summary>
public enum BotMemoryMode
{
    /// <summary>Every bot the starting profile's.</summary>
    SharedParent,

    /// <summary>Each bot its own profile's.</summary>
    Independent,
}

/// <summary>
/// The setting <c>Botchat memory mode</c> (2026-10-04, the user's words and default): <c>shared-parent</c> or
/// <c>independent</c>, and their mapping to <see cref="BotMemoryMode"/> — the <see cref="BotChatImageMode"/> shape (and the
/// retired <c>Botchat skill mode</c>'s, whose file this took the place of). <see cref="Resolve"/> is the one place the saved
/// string becomes the enum: a hand-edited value that is neither falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class BotChatMemoryMode
{
    /// <summary>The parent's: the compiled default (the user's call). Pinned.</summary>
    public const string Default = "shared-parent";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "shared-parent", "independent" };

    private const string Category = "Screen";

    /// <summary>Trims and ignores case; false (and <see cref="BotMemoryMode.SharedParent"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out BotMemoryMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "shared-parent": mode = BotMemoryMode.SharedParent; return true;
            case "independent": mode = BotMemoryMode.Independent; return true;
            default: mode = BotMemoryMode.SharedParent; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "shared-parent" => "every bot uses the starting profile's memories",
        "independent" => "each bot uses its own profile's memories",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static BotMemoryMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.BotChatMemoryMode, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.BotChatMemoryMode)}='{effective.BotChatMemoryMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return BotMemoryMode.SharedParent;
    }
}
