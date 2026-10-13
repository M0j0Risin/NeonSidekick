namespace NeonSidekick.Skills;

/// <summary>A reflection's write as <c>Reflection confirm mode</c> sorts it (2026-10-12): a new skill, or an edit to one that exists.</summary>
public enum SkillChangeKind
{
    /// <summary><c>create</c>.</summary>
    New,

    /// <summary><c>update</c> (its description, its instructions or both), <c>write_file</c>, <c>edit_file</c>.</summary>
    Edit,
}

/// <summary>
/// What a reflection's <c>skill_editor</c> is about to write (2026-10-12, the user's ask: <c>Reflection confirm mode</c>), for the pane that
/// asks the user first: the kind, the action, the skill and its scope (as far as the call names one), and the change itself — a create's
/// description and instructions, an update's new ones (null where the call leaves one as it is) with <see cref="Before"/> the SKILL.md as it
/// stands, a <c>write_file</c>'s path and content, an <c>edit_file</c>'s path and its old and new text — and the model's own
/// <see cref="Summary"/> of it. Nothing is written until the answer is yes.
/// </summary>
public sealed record SkillProposal(
    SkillChangeKind Kind,
    string Action,
    string Name,
    string? Description = null,
    string? Instructions = null,
    string? Before = null,
    string? Path = null,
    string? Content = null,
    string? OldText = null,
    string? NewText = null,
    string Summary = "");
