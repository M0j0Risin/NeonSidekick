using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Skills;

/// <summary>What a reflection may do to an installed skill (<c>Reflection AgentSkills.io skills</c>, 2026-10-02).</summary>
public enum ReflectionInstalledPolicy
{
    /// <summary>Its <c>skill_editor</c> refuses every change to a skill with a <see cref="SkillProvenance"/> sidecar.</summary>
    ReadOnly,

    /// <summary>It may change one; the skill records keep the change and the install's update page warns before replacing it.</summary>
    AllowAndMark,
}

/// <summary>
/// The installed-skills setting (2026-10-02, the user's call in the reflection audit): the two words the operator picks from
/// (<c>read-only</c>, <c>allow-and-mark</c>) and their mapping to <see cref="ReflectionInstalledPolicy"/>, the
/// <see cref="ReflectionCooldownMode"/> shape. <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited
/// value that is neither falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class ReflectionInstalledSkills
{
    /// <summary>The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "read-only";

    /// <summary>The choices in menu order.</summary>
    public static readonly string[] Names = { "read-only", "allow-and-mark" };

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out ReflectionInstalledPolicy policy)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "read-only": policy = ReflectionInstalledPolicy.ReadOnly; return true;
            case "allow-and-mark": policy = ReflectionInstalledPolicy.AllowAndMark; return true;
            default: policy = ReflectionInstalledPolicy.ReadOnly; return false;
        }
    }

    /// <summary>The saved word for <paramref name="policy"/>.</summary>
    public static string Name(ReflectionInstalledPolicy policy) => policy switch
    {
        ReflectionInstalledPolicy.AllowAndMark => "allow-and-mark",
        _ => "read-only",
    };

    /// <summary>The menu hint next to a choice. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "read-only" => "a reflection never changes a skill installed with /skills add; it may write a companion skill",
        "allow-and-mark" => "a reflection may change an installed skill; an update from its source warns before replacing that",
        _ => "",
    };

    /// <summary>The choice in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static ReflectionInstalledPolicy Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.ReflectionInstalledSkills, out var policy))
        {
            return policy;
        }

        DiagnosticLog.Warn(SkillCatalog.Category,
            $"{nameof(AppSettingsData.ReflectionInstalledSkills)}='{effective.ReflectionInstalledSkills}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out policy);
        return policy;
    }
}
