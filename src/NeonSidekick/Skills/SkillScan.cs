namespace NeonSidekick.Skills;

/// <summary>
/// What a <see cref="SkillCatalog.Scan(SkillScan)"/> reads (2026-11-23, feature idea #16): the external root when <paramref name="External"/>, a repo's
/// project skills when <paramref name="Project"/> is set (<c>Agent skills</c> and <c>Project skills</c> both on), and whether the project part is read
/// again (<paramref name="RefreshProject"/>) or the last full scan's is reused: the input line's <c>#</c> and <c>$</c> lists rescan at each keystroke,
/// and on a share the walk and its stamps would be network round trips under the typing. A turn's scan, a folder move, a profile switch, the
/// <c>/skills</c> pane's open and a trust change read it again.
/// </summary>
public sealed record SkillScan(bool External, ProjectSkillSource? Project = null, bool RefreshProject = true);

/// <summary>
/// Where the project skills come from: the live working folder, the user's home and the app's home (the walk's bounds,
/// <see cref="ProjectSkillWalk"/>), and the profile's trust store.
/// </summary>
public sealed record ProjectSkillSource(Func<string> WorkingFolder, string? UserHome, string? AppHome, ProjectSkillTrust Trust)
{
    /// <summary>The user's home for a given external skills folder: two levels up from <c>&lt;home&gt;/.agents/skills</c>.</summary>
    public static string? UserHomeOf(string externalSkills)
    {
        ArgumentNullException.ThrowIfNull(externalSkills);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(externalSkills));
        return Path.GetDirectoryName(Path.GetDirectoryName(full) ?? "");
    }
}

/// <summary>Why a project skill is listed but not offered (<see cref="SkillCatalog.ProjectPending"/>).</summary>
public enum ProjectPendingReason
{
    /// <summary>Its folder isn't trusted, or changed since it was.</summary>
    NotTrusted,

    /// <summary>Past the 50-skill cap.</summary>
    OverCap,

    /// <summary>Its folder or <c>SKILL.md</c> is a link leading outside the repo.</summary>
    LinkedOutside,

    /// <summary>A <c>.claude</c> skill marked <c>disable-model-invocation</c>: only started by hand in Claude Code.</summary>
    ManualOnly,

    /// <summary>A name over 64 characters or holding a control character.</summary>
    BadName,
}

/// <summary>A project skill listed but not offered, and why.</summary>
public sealed record ProjectPendingSkill(Skill Skill, ProjectPendingReason Reason);

/// <summary>A project skills folder's trust as the last scan found it.</summary>
public enum ProjectFolderTrust
{
    /// <summary>Never trusted (or untrusted).</summary>
    Untrusted,

    /// <summary>Trusted, and nothing changed since.</summary>
    Trusted,

    /// <summary>Trusted once, but its skills changed since: not trusted until asked again.</summary>
    Changed,
}

/// <summary>
/// One trust folder of the walk that holds skills (<paramref name="Level"/>, the working folder or an ancestor): its trust, the fingerprint the scan
/// read, and its skills' keys (<c>.agents/skills/x</c>) in order. <paramref name="SkillsFolders"/> are its <c>.agents/skills</c> and
/// <c>.claude/skills</c> that exist.
/// </summary>
public sealed record ProjectFolder(string Level, ProjectFolderTrust Trust, string Fingerprint, IReadOnlyList<string> Keys, IReadOnlyList<string> SkillsFolders);
