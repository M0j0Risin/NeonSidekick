using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Skills;

/// <summary>
/// The <c>Skill compact mode</c> setting: whether a loaded skill's instructions (a <c>load_skill</c>
/// result, tagged <c>ConversationHistory.SkillResultKey</c>) survive the prune compact and the
/// mid-turn tool-result guard — <c>protected</c>, the default and what the Agent Skills guide asks
/// for, since losing them silently degrades the model with no visible error — or prune like any
/// tool result (<c>unprotected</c>). A summary compact folds them either way; the catalog stays in
/// the prompt and the model can load one again. The <c>CompactType</c> shape: <see cref="Resolve"/>
/// is the one place the saved word is read, a hand-edited stranger warns and uses the default.
/// </summary>
public static class SkillCompactMode
{
    public const string Protected = "protected";
    public const string Unprotected = "unprotected";

    /// <summary>Kept through a prune. The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = Protected;

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { Protected, Unprotected };

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out bool protect)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case Protected: protect = true; return true;
            case Unprotected: protect = false; return true;
            default: protect = true; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        Protected => "loaded skills are kept through a prune, during a reply too",
        Unprotected => "loaded skills prune like any tool result",
        _ => "",
    };

    /// <summary>True when loaded skills are protected for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static bool Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.SkillCompactMode, out bool protect))
        {
            return protect;
        }

        DiagnosticLog.Warn(SkillCatalog.Category,
            $"{nameof(AppSettingsData.SkillCompactMode)}='{effective.SkillCompactMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return true;
    }
}
