namespace NeonSidekick.Skills;

/// <summary>
/// Where a skill folder lives, in precedence order: a profile's own skills shadow the global ones,
/// which shadow the external ones (<see cref="SkillCatalog"/> keeps the first found by name).
/// </summary>
public enum SkillScope
{
    /// <summary><c>&lt;home&gt;\profiles\&lt;name&gt;\skills</c>: this profile's alone.</summary>
    Profile,

    /// <summary><c>&lt;home&gt;\skills</c>: every profile's.</summary>
    Global,

    /// <summary><c>%USERPROFILE%\.agents\skills</c>: the cross-client convention, read only, behind <c>Use external skills</c>.</summary>
    External,

    /// <summary>
    /// A repo's own <c>.agents/skills</c> and <c>.claude/skills</c> (2026-11-23, feature idea #16, the user's calls): from the working folder up to
    /// its repo root, nearest first, read only, ranked last, and offered only after a per-folder trust (<see cref="ProjectSkillTrust"/>).
    /// Several folders, so <see cref="SkillRoots.Of"/> has none: <see cref="ProjectSkillWalk.Levels"/> lists them.
    /// </summary>
    Project,
}

/// <summary>The words for a <see cref="SkillScope"/>: the ones the model passes to <c>skill_editor</c> and the ones the panes show. Pinned.</summary>
public static class SkillScopes
{
    public const string ProfileName = "profile";
    public const string GlobalName = "global";
    public const string ExternalName = "external";
    public const string ProjectName = "project";

    /// <summary>The two scopes <c>skill_editor</c> writes, in schema order.</summary>
    public static readonly string[] Writable = { ProfileName, GlobalName };

    /// <summary>The word for <paramref name="scope"/>.</summary>
    public static string Name(SkillScope scope) => scope switch
    {
        SkillScope.Profile => ProfileName,
        SkillScope.Global => GlobalName,
        SkillScope.External => ExternalName,
        SkillScope.Project => ProjectName,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    /// <summary>
    /// Whether nothing here writes <paramref name="scope"/>'s skills (2026-11-23): the external folder and a repo's project folders. The
    /// editor, a reflection, <c>/skills add</c> and the records never touch either; a check that means the home's <c>.agents</c> alone stays
    /// <c>== External</c>.
    /// </summary>
    public static bool IsReadOnly(SkillScope scope) => scope is SkillScope.External or SkillScope.Project;

    /// <summary>Trims and ignores case; only <see cref="Writable"/> parse — the external folder is never written.</summary>
    public static bool TryParseWritable(string? text, out SkillScope scope)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case ProfileName: scope = SkillScope.Profile; return true;
            case GlobalName: scope = SkillScope.Global; return true;
            default: scope = SkillScope.Profile; return false;
        }
    }
}
