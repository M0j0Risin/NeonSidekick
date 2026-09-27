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
}

/// <summary>The user's answer to a presented plan, with what they want changed when they said.</summary>
public sealed record PlanVerdict(PlanChoice Choice, string? Feedback = null);

/// <summary>A plan as <c>present_plan</c> saved it, for the approval pane.</summary>
public sealed record PlanPresentation(string Title, string Path, int Revision, string Markdown);

/// <summary>What <see cref="NeonSidekick.App.ChatScreen.PrepareTurn"/> needs of plan mode for one turn: the directive and the tool that presents the plan.</summary>
public sealed record PlanTurn(string Directive, AIFunction Tool);

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

    /// <summary>Plan mode on for <paramref name="requirement"/>; a plan in progress is forgotten (its file stays).</summary>
    public void Enter(string requirement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement);
        Exit();
        Active = true;
        Requirement = requirement.Trim();
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
        return Active ? new PlanTurn(PlanText.Directive(Requirement, Path, Revision), tool) : null;
    }
}
