using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Plans;

namespace NeonSidekick.ProjectInit;

/// <summary>
/// One <c>/project-init</c> pass (2026-11-23): what was settled before the turn (<see cref="Start"/>), whether the pass's turn is running
/// (<see cref="InPass"/>, which narrows its tools and cuts the prompt's memories, notes and skills), and its one answer. <see cref="Begin"/> as
/// the turn is sent, <see cref="End"/> in the sender's <c>finally</c>, so an Esc, a dropped server or an error never leaves the narrowing on.
/// </summary>
public sealed class ProjectInitSession
{
    /// <summary>The pass's start, null outside one.</summary>
    public ProjectInitStart? Start { get; private set; }

    /// <summary>Whether a pass's turn is under way.</summary>
    public bool InPass => Start is not null;

    /// <summary>How the pass's one answer went: written (or found unchanged), canceled, or none yet (a Revise leaves it open).</summary>
    public ProjectInitAnswer Answer { get; private set; }

    /// <summary>Whether <c>present_project_notes</c> showed the pane at least once this pass.</summary>
    public bool Presented { get; private set; }

    /// <summary>
    /// Whether the last <c>present_project_notes</c> call's Write failed (2026-11-23, review #97/#91): its own error was shown, so the pass says
    /// nothing more, and its model-facing result isn't shown again. Cleared by <see cref="Begin"/> and at the start of every call.
    /// </summary>
    public bool WriteFailed { get; private set; }

    /// <summary>Whether this turn offers <c>ask_user</c>, as the directive was last built (a Revise with no words reads it).</summary>
    public bool AskUser { get; private set; }

    /// <summary>A pass begins: its start kept, the answer open.</summary>
    public void Begin(ProjectInitStart start)
    {
        Start = start ?? throw new ArgumentNullException(nameof(start));
        Answer = ProjectInitAnswer.None;
        Presented = false;
        WriteFailed = false;
    }

    /// <summary>A call's Write failed (true), or a call began (false).</summary>
    public void MarkWriteFailed(bool failed) => WriteFailed = failed;

    /// <summary>The pass is over (the turn ended, however): nothing narrowed after it.</summary>
    public void End() => Start = null;

    /// <summary>The pane was shown.</summary>
    public void MarkPresented() => Presented = true;

    /// <summary>The pass's one answer given: a later call in the same turn shows no pane.</summary>
    public void Finish(ProjectInitAnswer answer) => Answer = answer;

    /// <summary>Whether the working folder moved off the pass's own since it began (a <c>/cwd</c> before the Write).</summary>
    public bool Moved(string root) => Start is { } start && !RalphSnapshot.SameRoot(start.Root, root);

    /// <summary>
    /// The least room the turn leaves for the draft (2026-11-23), in tokens: the larger of the file in play and the directive's 8k aim, so the
    /// reads a pass makes are pruned before the draft runs out of room; 0 outside a pass. The file counts up to the notes' cap (review #48): a
    /// draft is never longer, and a long AGENTS.md reserved more than the window.
    /// </summary>
    public long ReplyFloor => Start is { } start ? Math.Max(Math.Min(start.InPlayLength, ProjectFile.MaxLength), ProjectInitText.AimChars) / ConversationCompactor.CharsPerToken : 0;

    /// <summary>
    /// The turn's part while a pass runs, else null: the directive (<see cref="ProjectInitText.Directive"/>) and <paramref name="tool"/>,
    /// <c>present_project_notes</c>. <paramref name="askUser"/> is whether <c>ask_user</c> is offered; <paramref name="maxToolIterations"/> the
    /// rounds the directive names. No <c>Compose</c>: the pass offers neither advisors nor the web.
    /// </summary>
    public PlanTurn? Turn(AIFunction tool, bool askUser, int maxToolIterations)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (Start is not { } start)
        {
            return null;
        }

        AskUser = askUser;
        return new PlanTurn(ProjectInitText.Directive(start, askUser, maxToolIterations), tool);
    }
}
