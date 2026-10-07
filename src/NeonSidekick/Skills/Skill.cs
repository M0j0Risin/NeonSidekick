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
}

/// <summary>A folder the catalog skipped and why: no description, no fence, unreadable.</summary>
public sealed record SkillProblem(string Directory, string Reason)
{
    public string FolderName => Path.GetFileName(Directory);
}
