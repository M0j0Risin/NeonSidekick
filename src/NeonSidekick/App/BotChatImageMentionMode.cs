using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>What a <c>/botchat</c> bot's mention of a picture it never drew does (<see cref="BotChatImageMentionMode"/>).</summary>
public enum BotImageMention
{
    /// <summary>Nothing: a picture the bot only talks about is not drawn.</summary>
    Disabled,

    /// <summary>The app draws it for the bot, so the reply is true (what <c>autonomous</c> did alone until 2026-10-13).</summary>
    Generate,
}

/// <summary>
/// The setting <c>Botchat image mention mode</c> (2026-10-13, the user's words and default): <c>off</c> or <c>generate</c>, and
/// their mapping to <see cref="BotImageMention"/> — the <see cref="BotChatImg2ImgMode"/> shape. Only read under
/// <c>Botchat image mode</c> <c>autonomous</c>, where a reply that talks about a picture its bot never called
/// <c>generate_image</c> for used to get that picture from the app every time; a <c>generate_image</c> call that failed still gets
/// its picture whatever this says (a call, not a mention). <see cref="Resolve"/> is the one place the saved string becomes the
/// enum: a hand-edited value that is neither falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class BotChatImageMentionMode
{
    /// <summary>Disabled: the compiled default (the user's call). Pinned.</summary>
    public const string Default = "off";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "off", "generate" };

    /// <summary>The words retired on 2026-10-13 (the settings verbiage pass, the user's call), still read: old → new (<see cref="RetiredWords"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["disabled"] = "off",
    };

    private const string Category = "Screen";

    /// <summary>Trims and ignores case; false (and <see cref="BotImageMention.Disabled"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out BotImageMention mode)
    {
        switch (RetiredWords.Current(text, Retired))
        {
            case "off": mode = BotImageMention.Disabled; return true;
            case "generate": mode = BotImageMention.Generate; return true;
            default: mode = BotImageMention.Disabled; return false;
        }
    }

    /// <summary>The saved word for <paramref name="mode"/>.</summary>
    public static string Name(BotImageMention mode) => mode == BotImageMention.Generate ? "generate" : "off";

    /// <summary>The menu hint next to a mode: the user's words. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "off" => "mentioned images are ignored",
        "generate" => "mentioned images are generated for the bots",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static BotImageMention Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.BotChatImageMentionMode, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.BotChatImageMentionMode)}='{effective.BotChatImageMentionMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return BotImageMention.Disabled;
    }
}
