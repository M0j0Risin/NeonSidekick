using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>What a <c>/botchat</c> bot naming another embedded model than the running one gets (<see cref="BotChatMultiEmbedded"/>).</summary>
public enum BotEmbeddedMode
{
    /// <summary>The one embedded server: the running model (the starter's, or the first embedded bot's), with a warning.</summary>
    ParentServer,

    /// <summary>An extra <c>llama-server</c> of its own for its model, beside the running one.</summary>
    MultiServer,
}

/// <summary>
/// The setting <c>Botchat multi-embedded</c> (later on 2026-09-29, the user's ask and name; only under <c>Botchat LLM mode</c>
/// <c>multi</c>): <c>parent-server</c> — the default, one embedded server for every bot on the embedded URL, a bot naming
/// another model using the running one with a warning (it sat the chat out until then) — or <c>multi-server</c>, an extra
/// server per other model (<see cref="EmbeddedLlm.IEmbeddedLlm.StartExtraAsync"/>), which <c>Botchat multi-embedded kill</c>
/// stops at the chat's end or keeps for the next. The <see cref="BotChatLlmMode"/> shape: <see cref="Resolve"/> is the one
/// place the saved string becomes the enum, a hand-edited value falling back to <see cref="Default"/> with a warning.
/// </summary>
public static class BotChatMultiEmbedded
{
    /// <summary>Parent-server, the user's call. Pinned.</summary>
    public const string Default = "parent-server";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "parent-server", "multi-server" };

    private const string Category = "Screen";

    /// <summary>Trims and ignores case; false (and <see cref="BotEmbeddedMode.ParentServer"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out BotEmbeddedMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "parent-server": mode = BotEmbeddedMode.ParentServer; return true;
            case "multi-server": mode = BotEmbeddedMode.MultiServer; return true;
            default: mode = BotEmbeddedMode.ParentServer; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "parent-server" => "one embedded server: a bot naming another embedded model uses the one running, with a warning",
        "multi-server" => "an extra llama-server for each other embedded model the bots name; the running one is kept (more VRAM)",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static BotEmbeddedMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.BotChatMultiEmbedded, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.BotChatMultiEmbedded)}='{effective.BotChatMultiEmbedded}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return BotEmbeddedMode.ParentServer;
    }
}
