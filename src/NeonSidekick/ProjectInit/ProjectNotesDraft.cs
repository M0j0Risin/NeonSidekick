using NeonSidekick.Files;

namespace NeonSidekick.ProjectInit;

/// <summary>
/// How <c>/project-init</c> treats the folder's notes (2026-11-23): <see cref="Create"/> a new <c>NEON.md</c> (none there); <see cref="Improve"/>
/// the file in play (a <c>NEON.md</c>, or the <c>AGENTS.md</c>/<c>CLAUDE.md</c> the user chose to update); <see cref="CreateBeside"/> a new
/// <c>NEON.md</c> beside an <c>AGENTS.md</c> or <c>CLAUDE.md</c>, which it then hides from the prompt.
/// </summary>
public enum ProjectInitMode
{
    Create,
    Improve,
    CreateBeside,
}

/// <summary>What the Write pane's choice was (2026-11-23). <see cref="WriteAndTurnOn"/> also turns on what keeps the notes out of the prompt.</summary>
public enum ProjectNotesChoice
{
    Write,
    WriteAndTurnOn,
    Revise,
    Cancel,
}

/// <summary>The user's answer to a draft: the choice, and a Revise's words (null for none said).</summary>
public sealed record ProjectNotesVerdict(ProjectNotesChoice Choice, string? Feedback = null);

/// <summary>How a pass ended its one answer (2026-11-23): not yet, written (or found unchanged), or canceled.</summary>
public enum ProjectInitAnswer
{
    None,
    Written,
    Canceled,
}

/// <summary>
/// What <c>/project-init</c> settled before its turn (2026-11-23): the folder (to tell a <c>/cwd</c> before the Write), the file written and
/// how, the one it would hide, the digest the directive carries, the focus words, the file in play's length (for the reply floor and the
/// long-file caption) and whether values in it were masked, whether the repo's root is above the folder, the digest's cut sections (the
/// <c>--log</c> line) and the model's window in tokens (0 unknown).
/// </summary>
public sealed record ProjectInitStart(
    string Root,
    string Target,
    ProjectInitMode Mode,
    string Digest,
    string? Focus = null,
    string? Hidden = null,
    int InPlayLength = 0,
    bool InPlayMasked = false,
    bool Subfolder = false,
    IReadOnlyList<string>? Cut = null,
    int WindowTokens = 0);

/// <summary>
/// A draft put to the user (2026-11-23): the file it would write, the text, the file's text now (null for a new file), what would change
/// (null for nothing, or a new file), and whether it is the same as the file now (line endings aside), so Write would write nothing.
/// </summary>
public sealed record ProjectNotesDraft(string Target, string Content, string? OldText, FileDiff? Diff, bool Unchanged)
{
    /// <summary>The draft's lines a secret rule would change, or that hold the mask already.</summary>
    public IReadOnlyList<ProjectSecretLine> SecretLines { get; init; } = [];
}

/// <summary>One line of a draft that looks like a secret (2026-11-23): its 1-based number, the line masked, and whether it holds the mask already.</summary>
public readonly record struct ProjectSecretLine(int Line, string Masked, bool HoldsMask);
