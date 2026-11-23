using NeonSidekick.Web;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Llm;

/// <summary>
/// One tool call's outcome as the turn loop hands it on (2026-11-16, the web-tools review's #51: a seven-field tuple repeated through
/// <c>InvokeGuardedAsync</c>, <c>InvokeWatchedAsync</c>, <c>InvokeToolWithDiffAsync</c> and <c>InvokeAsync</c>): the text the model reads, the
/// pictures a <see cref="ToolImageResult"/> carried, the <see cref="FileDiff"/> a <see cref="ToolDiffResult"/> carried, a
/// <see cref="ToolShownResult"/>'s line for the user alone, whether the loop's own gate refused the call (nothing ran), whether that refusal
/// was the model's argument mistake, and the <see cref="WebSource"/> a <see cref="ToolSourcedResult"/> carried. The tool-side wrappers stay
/// exclusive (no tool returns a source with pictures or a diff today); the record leaves room if one ever does.
/// <para><see cref="NothingRan"/> (2026-11-23, part of feature idea #64): the call changed nothing though no gate refused it: a tool's own
/// <see cref="ToolNothingRanResult"/> (<c>present_project_notes</c>' Revise or Cancel) or the project-file ask's no. The undo journal skips it,
/// so <c>/undo</c>'s "Not undone:" never names it; kept apart from <see cref="Refused"/>, whose argument-error accounting it leaves alone.</para>
/// </summary>
public sealed record ToolInvocation(
    string Text,
    IReadOnlyList<ImageAttachment>? Images = null,
    FileDiff? Diff = null,
    string? Shown = null,
    bool Refused = false,
    bool ArgumentError = false,
    WebSource? Source = null)
{
    /// <summary>The call changed nothing though no gate refused it (2026-11-23, see the summary): the undo journal skips it.</summary>
    public bool NothingRan { get; init; }

    /// <summary>The pictures, never null: empty when the tool carried none.</summary>
    public IReadOnlyList<ImageAttachment> Images { get; init; } = Images ?? [];
}
