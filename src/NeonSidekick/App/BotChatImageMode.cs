using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>Who draws a <c>/botchat</c> picture (<see cref="BotChatImageMode"/>).</summary>
public enum BotImageMode
{
    /// <summary>The app: a prompt written from each reply, one picture per reply; the bots are offered no tool.</summary>
    Automatic,

    /// <summary>The bots: <c>generate_image</c> is offered and they draw when they choose; no picture of the app's.</summary>
    Autonomous,
}

/// <summary>
/// The setting <c>Botchat image mode</c> (2026-09-25, the user's words): <c>automatic</c> or <c>autonomous</c>, and their
/// mapping to <see cref="BotImageMode"/> — the <see cref="QueueCancelMode"/> shape. A third, <c>both</c> (the tool offered and
/// the app's picture of every reply), went later the same day — the user's call: the two cover every case, and both at once made
/// no sense; a profile that saved it now warns and uses <see cref="Default"/>, as any unknown word does.
/// <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited value that is none of
/// them falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class BotChatImageMode
{
    /// <summary>Automatic: the compiled default (the user's call). Pinned.</summary>
    public const string Default = "automatic";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "automatic", "autonomous" };

    private const string Category = "Screen";

    /// <summary>Trims and ignores case; false (and <see cref="BotImageMode.Automatic"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out BotImageMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "automatic": mode = BotImageMode.Automatic; return true;
            case "autonomous": mode = BotImageMode.Autonomous; return true;
            default: mode = BotImageMode.Automatic; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "automatic" => "a picture of every reply, its prompt written from the reply; the bots get no tool",
        "autonomous" => "the bots are offered generate_image and draw when they choose; a picture they only talk about is drawn for them",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static BotImageMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.BotChatImageMode, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.BotChatImageMode)}='{effective.BotChatImageMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return BotImageMode.Automatic;
    }

    /// <summary>Whether the app draws a picture of every reply.</summary>
    public static bool Draws(BotImageMode mode) => mode == BotImageMode.Automatic;

    /// <summary>Whether the bots are offered <c>generate_image</c>.</summary>
    public static bool Offers(BotImageMode mode) => mode == BotImageMode.Autonomous;
}
