namespace NeonSidekick.Skills;

/// <summary>
/// One discovered skill: its frontmatter name (the key the model uses), the flattened description
/// the catalog shows, the folder the <c>SKILL.md</c> sits in and the scope it was found under. A
/// <see cref="Warning"/> is the lenient loader's note (the name differs from the folder, or runs
/// past the cap) — the skill loads anyway. <see cref="ShadowedBy"/> is set on the copies a higher
/// scope hides; those are listed on <c>/skill</c> and offered to nobody. The body is not held: it
/// is read at activation (<see cref="SkillCatalog.ReadBody"/>), so an edit shows without a rescan.
/// </summary>
public sealed record Skill(string Name, string Description, SkillScope Scope, string Directory, string? Warning = null, SkillScope? ShadowedBy = null)
{
    /// <summary>The <c>SKILL.md</c> path.</summary>
    public string FilePath => Path.Combine(Directory, SkillCatalog.FileName);

    /// <summary>The folder's own name (equals <see cref="Name"/> for a conforming skill).</summary>
    public string FolderName => Path.GetFileName(Directory);

    /// <summary>Locked by the user when the catalog scanned (2026-10-07, <see cref="SkillLock"/>); never for an external skill.</summary>
    public bool Locked { get; init; }

    /// <summary>
    /// Disabled by the user when the catalog scanned (2026-10-09, <see cref="SkillDisable"/>): not offered; never for an external skill. A project
    /// skill's comes from the profile's list (<see cref="ProjectSkillTrust.DisabledIn"/>), never a marker in the repo.
    /// </summary>
    public bool Disabled { get; init; }

    // ── A project skill's (2026-11-23, feature idea #16) ──

    /// <summary>The folder its trust is kept under (the working folder or an ancestor that holds the <c>.agents</c>/<c>.claude</c> folder); null for the other scopes.</summary>
    public string? TrustFolder { get; init; }

    /// <summary>Its key in that folder's trust: <c>.agents/skills/&lt;folder&gt;</c> or <c>.claude/skills/&lt;folder&gt;</c>.</summary>
    public string? ProjectKey { get; init; }

    /// <summary>Read by Claude Code's rules (a <c>.claude/skills</c> skill, <see cref="SkillFrontmatter.TryParseClaude"/>); the body read the same way.</summary>
    public bool ClaudeRules { get; init; }

    /// <summary>The hash it was trusted with: <c>load_skill</c> hands it over only while its files still hash to this (<see cref="SkillCatalog.StillTrusted"/>).</summary>
    public string? TrustedHash { get; init; }

    /// <summary>Its frontmatter holds a key that acts in Claude Code (<see cref="SkillFrontmatter.ActingKeys"/>): ignored here, marked on the trust question.</summary>
    public bool Acting { get; init; }

    /// <summary>The winner's folder when a project skill hides another of the same name (all are one scope, so the scope alone doesn't name it).</summary>
    public string? ShadowedByDirectory { get; init; }
}

/// <summary>A folder the catalog skipped and why: no description, no fence, unreadable.</summary>
public sealed record SkillProblem(string Directory, string Reason)
{
    public string FolderName => Path.GetFileName(Directory);
}
