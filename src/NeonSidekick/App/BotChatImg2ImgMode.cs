using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>Which of a <c>/botchat</c>'s pictures may be reworked with the img2img workflow (<see cref="BotChatImg2ImgMode"/>).</summary>
public enum BotImg2ImgMode
{
    /// <summary>The chat's latest picture alone.</summary>
    Latest,

    /// <summary>Any of the chat's pictures so far (the last <see cref="BotChat.MaxReworkPictures"/>).</summary>
    ChatHistory,
}

/// <summary>
/// The setting <c>Botchat img2img mode</c> (2026-09-27, the user's words and default): <c>latest</c> or <c>chat-history</c>,
/// and their mapping to <see cref="BotImg2ImgMode"/> — the <see cref="BotChatImageMode"/> shape. Only read while
/// <c>Botchat img2img workflow</c> names a workflow. <see cref="Resolve"/> is the one place the saved string becomes the
/// enum: a hand-edited value that is neither falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class BotChatImg2ImgMode
{
    /// <summary>Latest: the compiled default (the user's call). Pinned.</summary>
    public const string Default = "latest";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "latest", "chat-history" };

    private const string Category = "Screen";

    /// <summary>Trims and ignores case; false (and <see cref="BotImg2ImgMode.Latest"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out BotImg2ImgMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "latest": mode = BotImg2ImgMode.Latest; return true;
            case "chat-history": mode = BotImg2ImgMode.ChatHistory; return true;
            default: mode = BotImg2ImgMode.Latest; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "latest" => "only the chat's latest picture may be reworked",
        "chat-history" => "any of the chat's pictures so far may be reworked",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static BotImg2ImgMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.BotChatImg2ImgMode, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.BotChatImg2ImgMode)}='{effective.BotChatImg2ImgMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return BotImg2ImgMode.Latest;
    }
}
