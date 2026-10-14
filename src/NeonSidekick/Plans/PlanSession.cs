using Microsoft.Extensions.AI;
using NeonSidekick.Sessions;

namespace NeonSidekick.Plans;

/// <summary>What the user made of a presented plan.</summary>
public enum PlanChoice
{
    /// <summary>Not approved yet: ESC, the refine row (with feedback or none), or no pane to ask on.</summary>
    Refine,

    /// <summary>Approved: carried out in this conversation.</summary>
    Approve,

    /// <summary>Approved: carried out in a new conversation, the plan's text sent with the message.</summary>
    ApproveFresh,

    /// <summary>Dropped: plan mode ends, the file is marked cancelled.</summary>
    Cancel,

    /// <summary>Saved with nobody to ask (headless): the user approves with <c>/plan approve</c>.</summary>
    Saved,

    /// <summary>
    /// Not approved yet: the plan goes through the gap-closing loop first (2026-10-13, <see cref="PlanGapLoop"/>), the reviewer
    /// in <see cref="PlanVerdict.Reviewer"/>; it comes back to the pane once a pass finds no gaps.
    /// </summary>
    CloseGaps,
}

/// <summary>The user's answer to a presented plan, with what they want changed when they said, and the reviewer of a <see cref="PlanChoice.CloseGaps"/>.</summary>
public sealed record PlanVerdict(PlanChoice Choice, string? Feedback = null, PlanReviewer? Reviewer = null);

/// <summary>
/// A plan as <c>present_plan</c> saved it, for the approval pane; <see cref="Note"/> is a line the pane shows after the caption
/// (2026-10-13: how the gap-closing loop ended).
/// </summary>
public sealed record PlanPresentation(string Title, string Path, int Revision, string Markdown)
{
    public string? Note { get; init; }
}

/// <summary>What <see cref="NeonSidekick.App.ChatScreen.PrepareTurn"/> needs of plan mode for one turn: the directive and the tool that presents the plan.</summary>
public sealed record PlanTurn(string Directive, AIFunction Tool)
{
    /// <summary>The directive again with the advisors' bullet (2026-10-10, <see cref="PlanText.AdvisorsLine"/>); null composes none.</summary>
    public Func<IReadOnlyList<Settings.AdvisorEntry>, string>? WithAdvisors { get; init; }

    /// <summary>The directive a turn sends: with <paramref name="advisors"/> when the turn offers <c>neon_advisor</c> and any is listed, else <see cref="Directive"/>.</summary>
    public string DirectiveFor(IReadOnlyList<Settings.AdvisorEntry>? advisors) =>
        advisors is { Count: > 0 } && WithAdvisors is { } compose ? compose(advisors) : Directive;
}

/// <summary>
/// Plan mode's state (2026-09-26), one per screen (and one per headless run): whether it is on,
/// the requirement <c>/plan</c> started it with, and — once <c>present_plan</c> has saved one — the
/// plan's path, title, revision and first-save time; <see cref="Approved"/> holds an approval given on
/// the pane until the turn that gave it ends and the screen carries the plan out. Written on the turn
/// task (the tool) and on the idle loop, never both at once. <see cref="ToStored"/> /
/// <see cref="Restore"/> are the session row's copy, so a restored session is still planning.
/// </summary>
public sealed class PlanSession
{
    public bool Active { get; private set; }

    public string Requirement { get; private set; } = "";

    /// <summary>The plan's file relative to the working directory, once presented; kept for every revision.</summary>
    public string? Path { get; private set; }

    public string? Title { get; private set; }

    public int Revision { get; private set; }

    public DateTimeOffset? Created { get; private set; }

    /// <summary>An approval given on the pane, waiting for its turn to end: <see cref="PlanChoice.Approve"/> or <see cref="PlanChoice.ApproveFresh"/>.</summary>
    public PlanChoice? Approved { get; private set; }

    /// <summary>
    /// The gap-closing loop while one runs (2026-10-13, <see cref="PlanGapLoop"/>): from the <c>Close gaps…</c> choice or
    /// <c>/plan gaps</c> until it stops. Never kept in the session row and never restored, as <see cref="Approved"/>.
    /// </summary>
    public PlanGapLoop? Closing { get; private set; }

    /// <summary>A fresh loop for <paramref name="reviewer"/>, at most <paramref name="limit"/> passes (0 for no limit); one already running is replaced.</summary>
    public PlanGapLoop StartClosing(PlanReviewer reviewer, int limit) => Closing = new PlanGapLoop(reviewer, limit);

    /// <summary>The loop over, however it ended.</summary>
    public void StopClosing() => Closing = null;

    /// <summary>Plan mode on for <paramref name="requirement"/>; a plan in progress is forgotten (its file stays).</summary>
    public void Enter(string requirement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement);
        Exit();
        Active = true;
        Requirement = requirement.Trim();
    }

    /// <summary>
    /// Plan mode on over a plan file already on disk (<c>/plan open</c>, 2026-09-26): the path fixed, so the next
    /// <c>present_plan</c> overwrites it as revision <paramref name="revision"/> + 1 and <c>/plan approve</c> works at once.
    /// </summary>
    public void Open(string path, string title, string requirement, int revision, DateTimeOffset? created)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement);
        Exit();
        Active = true;
        Requirement = requirement.Trim();
        Path = path;
        Title = title;
        Revision = Math.Max(1, revision);
        Created = created;
    }

    /// <summary>A revision saved to <paramref name="path"/>.</summary>
    public void Presented(string path, string title, int revision, DateTimeOffset created)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(title);
        Path = path;
        Title = title;
        Revision = revision;
        Created ??= created;
    }

    /// <summary>The approval noted, to act on when the turn ends.</summary>
    public void Approve(bool fresh) => Approved = fresh ? PlanChoice.ApproveFresh : PlanChoice.Approve;

    /// <summary>A noted approval dropped (the reply that gave it was stopped): the plan stays presented, for <c>/plan approve</c>.</summary>
    public void ClearApproval() => Approved = null;

    /// <summary>Plan mode off and everything forgotten.</summary>
    public void Exit()
    {
        Active = false;
        Requirement = "";
        Path = null;
        Title = null;
        Revision = 0;
        Created = null;
        Approved = null;
        Closing = null;
    }

    /// <summary>The session row's copy; null while plan mode is off.</summary>
    public StoredPlan? ToStored() => Active
        ? new StoredPlan { Requirement = Requirement, Path = Path, Title = Title, Revision = Revision, Created = Created }
        : null;

    /// <summary>The state a session row kept (plan mode off for none); an approval is never restored.</summary>
    public void Restore(StoredPlan? stored)
    {
        Exit();
        if (stored is null || string.IsNullOrWhiteSpace(stored.Requirement))
        {
            return;
        }

        Active = true;
        Requirement = stored.Requirement.Trim();
        Path = string.IsNullOrWhiteSpace(stored.Path) ? null : stored.Path;
        Title = stored.Title;
        Revision = Path is null ? 0 : Math.Max(1, stored.Revision);
        Created = stored.Created;
    }

    /// <summary>The turn's plan-mode part while planning, else null.</summary>
    public PlanTurn? Turn(AIFunction tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!Active)
        {
            return null;
        }

        string requirement = Requirement;
        string? path = Path;
        int revision = Revision;
        // A pass of the gap-closing loop under way (2026-10-13): its rules ride in the directive, so a compaction cannot lose them.
        var gap = Closing is { Pending: false, Converged: false } loop ? new PlanGapState(loop.Pass, loop.Limit, loop.Reviewer) : null;
        return new PlanTurn(PlanText.Directive(requirement, path, revision, null, gap), tool) { WithAdvisors = advisors => PlanText.Directive(requirement, path, revision, advisors, gap) };
    }
}
