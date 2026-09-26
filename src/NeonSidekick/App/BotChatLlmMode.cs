using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>Whose LLM a <c>/botchat</c> bot talks through (<see cref="BotChatLlmMode"/>).</summary>
public enum BotLlmMode
{
    /// <summary>The starter's: every bot on the loaded profile's one client, server, model and reasoning effort.</summary>
    Single,

    /// <summary>Each bot's own: its profile's server, model and reasoning effort (<see cref="LlmSession.LinkAsync"/>).</summary>
    Multi,
}

/// <summary>
/// The setting <c>Botchat LLM mode</c> (2026-09-25, the user's words): <c>single</c> — what <c>/botchat</c> always did, every
/// bot on the starting profile's LLM — or <c>multi</c>, each bot on the server, model and reasoning its own profile names,
/// and their mapping to <see cref="BotLlmMode"/> — the <see cref="BotChatImageMode"/> shape. <see cref="Resolve"/> is the one
/// place the saved string becomes the enum: a hand-edited value that is neither falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class BotChatLlmMode
{
    /// <summary>Single: the compiled default, what the chat did before the setting. Pinned.</summary>
    public const string Default = "single";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "single", "multi" };

    private const string Category = "Screen";

    /// <summary>Trims and ignores case; false (and <see cref="BotLlmMode.Single"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out BotLlmMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "single": mode = BotLlmMode.Single; return true;
            case "multi": mode = BotLlmMode.Multi; return true;
            default: mode = BotLlmMode.Single; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "single" => "every bot on this profile's LLM server, model and reasoning",
        "multi" => "each bot on its own profile's LLM server, model and reasoning; a blank URL borrows this profile's server",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static BotLlmMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.BotChatLlmMode, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.BotChatLlmMode)}='{effective.BotChatLlmMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return BotLlmMode.Single;
    }
}
