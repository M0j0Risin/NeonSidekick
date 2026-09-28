using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>Who gets a <c>/botchat</c>'s preloaded skills (<see cref="BotChatSkillMode"/>).</summary>
public enum BotSkillMode
{
    /// <summary>The picture prompt writer alone.</summary>
    PromptWriterOnly,

    /// <summary>The picture prompt writer and every bot's system prompt.</summary>
    PromptWriterAndBots,
}

/// <summary>
/// The setting <c>Botchat skill mode</c> (2026-09-27, the user's words and default): <c>prompt-writer-only</c> or
/// <c>prompt-writer-and-bots</c>, and their mapping to <see cref="BotSkillMode"/> — the <see cref="BotChatImageMode"/> shape.
/// It says where the skills <c>Botchat preloaded skills</c> and the topic name go (<see cref="BotChat.PreloadedSkills"/>).
/// <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited value that is neither falls back
/// to <see cref="Default"/> with a warning.
/// </summary>
public static class BotChatSkillMode
{
    /// <summary>Both: the compiled default (the user's call). Pinned.</summary>
    public const string Default = "prompt-writer-and-bots";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "prompt-writer-only", "prompt-writer-and-bots" };

    private const string Category = "Screen";

    /// <summary>Trims and ignores case; false (and <see cref="BotSkillMode.PromptWriterAndBots"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out BotSkillMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "prompt-writer-only": mode = BotSkillMode.PromptWriterOnly; return true;
            case "prompt-writer-and-bots": mode = BotSkillMode.PromptWriterAndBots; return true;
            default: mode = BotSkillMode.PromptWriterAndBots; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "prompt-writer-only" => "the preloaded skills go to the picture prompt writer alone",
        "prompt-writer-and-bots" => "the preloaded skills go to the picture prompt writer and every bot",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static BotSkillMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.BotChatSkillMode, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.BotChatSkillMode)}='{effective.BotChatSkillMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return BotSkillMode.PromptWriterAndBots;
    }
}
