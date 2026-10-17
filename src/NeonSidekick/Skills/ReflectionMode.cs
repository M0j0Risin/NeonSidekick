using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Skills;

/// <summary>What a reflection may write (<c>Reflection mode</c>, 2026-10-16).</summary>
public enum ReflectionWriteMode
{
    /// <summary>A new skill, or an improvement to one that exists: the reflection as it always was.</summary>
    CreateUpdate,

    /// <summary>Only an improvement to a skill that exists (its supporting files too under <c>Reflection edit supporting files</c>); never a new one.</summary>
    UpdateOnly,
}

/// <summary>
/// The reflection-mode setting (2026-10-16, the user's ask and words): <c>create-update</c> (the default) and <c>update-only</c>, and
/// their mapping to <see cref="ReflectionWriteMode"/> — <see cref="ReflectionInstalledSkills"/>' shape. <see cref="Resolve"/> is the one
/// place the saved string becomes the enum: a hand-edited value that is neither falls back to <see cref="Default"/> with a warning.
/// Under <c>update-only</c> a reflection is never told it may create (its instruction, its <c>skill_editor</c>'s schema, description and
/// errors), a create that gets through anyway is refused before any ask or write, and with no skill it could update none is started.
/// </summary>
public static class ReflectionMode
{
    /// <summary>The compiled default (a reflection as it always was), pinned by <c>ReflectionModeTests</c>.</summary>
    public const string Default = "create-update";

    /// <summary>The choices in menu order.</summary>
    public static readonly string[] Names = { "create-update", "update-only" };

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out ReflectionWriteMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "create-update": mode = ReflectionWriteMode.CreateUpdate; return true;
            case "update-only": mode = ReflectionWriteMode.UpdateOnly; return true;
            default: mode = ReflectionWriteMode.CreateUpdate; return false;
        }
    }

    /// <summary>The menu hint next to a choice. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "create-update" => "a reflection may write a new skill or improve one that exists",
        "update-only" => "a reflection only improves a skill that exists; it never writes a new one",
        _ => "",
    };

    /// <summary>The choice in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static ReflectionWriteMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.ReflectionMode, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(SkillCatalog.Category,
            $"{nameof(AppSettingsData.ReflectionMode)}='{effective.ReflectionMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out mode);
        return mode;
    }
}
