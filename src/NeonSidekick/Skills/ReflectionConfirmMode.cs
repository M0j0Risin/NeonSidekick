using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Skills;

/// <summary>Which of a reflection's writes wait for the user (<c>Reflection confirm mode</c>, 2026-10-12).</summary>
public enum ReflectionConfirmScope
{
    /// <summary>None: a reflection writes as it always has.</summary>
    None,

    /// <summary>New skills and edits alike.</summary>
    All,

    /// <summary>New skills only; an edit to one that exists is written unasked.</summary>
    New,

    /// <summary>Edits only (an update, a supporting file written or edited); a new skill is written unasked.</summary>
    Edit,
}

/// <summary>
/// The confirm-mode setting (2026-10-12, the user's ask and words): <c>none</c>, <c>all</c>, <c>new</c> (the default since 2026-10-18,
/// the user's call; <c>none</c> until then), <c>edit</c>, and their mapping to <see cref="ReflectionConfirmScope"/> — <see cref="ReflectionCooldownMode"/>'s shape.
/// <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited value that is none of them falls back to
/// <see cref="Default"/> with a warning. A confirmed write is <c>ChatScreen</c>'s pane at the idle line (<see cref="SkillProposal"/>).
/// </summary>
public static class ReflectionConfirmMode
{
    /// <summary>The compiled default (the user's call), pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "new";

    /// <summary>The choices in menu order (the user's).</summary>
    public static readonly string[] Names = { "none", "all", "new", "edit" };

    /// <summary>The words retired on 2026-10-13 (the settings verbiage pass, the user's call), still read: old → new (<see cref="RetiredWords"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["confirm-none"] = "none",
        ["confirm-all"] = "all",
        ["confirm-new"] = "new",
        ["confirm-edit"] = "edit",
    };

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out ReflectionConfirmScope scope)
    {
        switch (RetiredWords.Current(text, Retired))
        {
            case "none": scope = ReflectionConfirmScope.None; return true;
            case "all": scope = ReflectionConfirmScope.All; return true;
            case "new": scope = ReflectionConfirmScope.New; return true;
            case "edit": scope = ReflectionConfirmScope.Edit; return true;
            default: scope = ReflectionConfirmScope.None; return false;
        }
    }

    /// <summary>The menu hint next to a choice (the user's words). Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "none" => "a reflection never asks",
        "all" => "a reflection asks before a new skill and before an edit",
        "new" => "a reflection asks before a new skill, not before an edit",
        "edit" => "a reflection asks before an edit, not before a new skill",
        _ => "",
    };

    /// <summary>Whether <paramref name="scope"/> holds a write of <paramref name="kind"/> for the user's answer. Pure.</summary>
    public static bool Asks(ReflectionConfirmScope scope, SkillChangeKind kind) => scope switch
    {
        ReflectionConfirmScope.All => true,
        ReflectionConfirmScope.New => kind == SkillChangeKind.New,
        ReflectionConfirmScope.Edit => kind == SkillChangeKind.Edit,
        _ => false,
    };

    /// <summary>The choice in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static ReflectionConfirmScope Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.ReflectionConfirmMode, out var scope))
        {
            return scope;
        }

        DiagnosticLog.Warn(SkillCatalog.Category,
            $"{nameof(AppSettingsData.ReflectionConfirmMode)}='{effective.ReflectionConfirmMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        // The parsed default, so the fallback agrees with the warning (2026-10-18: None while the default was none, New since).
        TryParse(Default, out var fallback);
        return fallback;
    }
}
