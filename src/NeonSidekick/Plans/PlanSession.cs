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

/// <summary>
/// What the directive says of <c>/plan chat</c> (2026-10-17): <paramref name="Talking"/>, the model not to present until the user asks;
/// <paramref name="Opening"/>, the requirement is only the conversation's first message. The default is the directive as before.
/// </summary>
public readonly record struct PlanTalk(bool Talking, bool Opening);

/// <summary>What <see cref="NeonSidekick.App.ChatScreen.PrepareTurn"/> needs of plan mode for one turn: the directive and the tool that presents the plan.</summary>
public sealed record PlanTurn(string Directive, AIFunction Tool)
{
    /// <summary>
    /// The directive again with the advisors' bullet (2026-10-10, <see cref="PlanText.AdvisorsLine"/>) and the web research tools'
    /// (2026-10-17, <see cref="PlanText.WebResearchLine"/>), either null for none; null composes none.
    /// </summary>
    public Func<IReadOnlyList<Settings.AdvisorEntry>?, IReadOnlyList<string>?, string>? Compose { get; init; }

    /// <summary>
    /// The directive a turn sends: with <paramref name="advisors"/> when the turn offers <c>neon_advisor</c> and any is listed, and with
    /// <paramref name="research"/>, the turn's offered tools of <see cref="PlanText.ResearchTools"/>; else <see cref="Directive"/>.
    /// </summary>
    public string DirectiveFor(IReadOnlyList<Settings.AdvisorEntry>? advisors, IReadOnlyList<string>? research = null)
    {
        var listed = advisors is { Count: > 0 } ? advisors : null;
        var web = research is { Count: > 0 } ? research : null;
        return (listed is not null || web is not null) && Compose is { } compose ? compose(listed, web) : Directive;
    }

    /// <summary>Of <paramref name="tools"/>, the names in <see cref="PlanText.ResearchTools"/>, in that order (2026-10-17).</summary>
    public static IReadOnlyList<string> ResearchOf(IEnumerable<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return PlanText.ResearchTools.Where(names.Contains).ToList();
    }
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

    /// <summary>
    /// <see cref="Requirement"/> is only the conversation's opening (2026-10-17, <c>/plan chat</c>, the user's call): the first message,
    /// labeled as such in the directive until the first presentation, when the plan's title takes its place (<see cref="Settle"/>).
    /// </summary>
    public bool RequirementIsOpening { get; private set; }

    /// <summary>
    /// Talking the plan through before it is written (2026-10-17, <c>/plan chat</c>): the directive tells the model not to present
    /// until the user asks, and the plan-like-reply hint stays quiet. Cleared by <c>/plan ready</c>, any save of the plan
    /// (<see cref="Presented"/>), a gap review (<see cref="StartClosing"/>), <see cref="Exit"/> and <see cref="Enter"/>.
    /// </summary>
    public bool Talking { get; private set; }

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

    /// <summary>
    /// A loop that converged inside the turn (2026-10-17, review pass 9 of the clean-passes plan): moved here by
    /// <see cref="FinishClosing"/> so the pane its convergence opens can start another review or end plan mode without losing it.
    /// It survives <see cref="StartClosing"/> and <see cref="Exit"/>; only the after-turn code takes it (<see cref="TakeFinished"/>),
    /// so its summary, notice and <c>review:</c> line are said and written once.
    /// </summary>
    public PlanGapLoop? Finished { get; private set; }

    /// <summary>
    /// A fresh loop for <paramref name="reviewer"/>, at most <paramref name="limit"/> passes (0 for no limit), ending on
    /// <paramref name="cleanPasses"/> clean passes in a row (2026-10-17); one already running is replaced. Talking ends: a review
    /// means the plan is ready to be checked.
    /// </summary>
    public PlanGapLoop StartClosing(PlanReviewer reviewer, int limit, int cleanPasses = 1)
    {
        Talking = false;
        return Closing = new PlanGapLoop(reviewer, limit, cleanPasses);
    }

    /// <summary>The loop over, however it ended.</summary>
    public void StopClosing() => Closing = null;

    /// <summary>The converged loop moved from <see cref="Closing"/> to <see cref="Finished"/> (2026-10-17): a later call in the turn sees no loop.</summary>
    public void FinishClosing()
    {
        if (Closing is { } loop)
        {
            Finished = loop;
            _finishedPath = Path;
            Closing = null;
        }
    }

    private string? _finishedPath;

    /// <summary>The converged loop waiting for its turn to end, taken once, with the plan file it reviewed (plan mode may be off by now); null when there is none.</summary>
    public PlanGapLoop? TakeFinished(out string? path)
    {
        var finished = Finished;
        path = finished is null ? null : _finishedPath;
        Finished = null;
        _finishedPath = null;
        return finished;
    }

    /// <summary>A converged loop dropped unsaid (2026-10-17): the conversation it belonged to was replaced under the reply.</summary>
    public void ForgetFinished() => Finished = null;

    /// <summary>Plan mode on for <paramref name="requirement"/>; a plan in progress is forgotten (its file stays).</summary>
    public void Enter(string requirement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement);
        Exit();
        Active = true;
        Requirement = requirement.Trim();
    }

    /// <summary>
    /// Plan mode on to talk a plan through first (2026-10-17, <c>/plan chat</c>): no requirement yet — the first message becomes it
    /// (<see cref="NoteMessage"/>) — and <see cref="Talking"/> on; a plan in progress is forgotten (its file stays).
    /// </summary>
    public void EnterChat()
    {
        Exit();
        Active = true;
        Requirement = "";
        Talking = true;
    }

    /// <summary><c>/plan chat</c> while planning (2026-10-17): talking again, say after a plan was presented that the user wants to rethink.</summary>
    public void ResumeTalking() => Talking = Active;

    /// <summary><c>/plan ready</c> (2026-10-17): talking over; the model is asked for the plan.</summary>
    public void StopTalking() => Talking = false;

    /// <summary>
    /// A message sent while planning (2026-10-17): with no requirement yet (<c>/plan chat</c>), its non-blank text, trimmed, becomes the
    /// requirement, marked as the conversation's opening; never overwritten. A picture-only message leaves it for the next one. True when it was set.
    /// </summary>
    public bool NoteMessage(string text)
    {
        if (!Active || Requirement.Length > 0 || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Requirement = text.Trim();
        RequirementIsOpening = true;
        return true;
    }

    /// <summary>
    /// The requirement a save writes (2026-10-17): the plan's <paramref name="title"/> while the requirement is only the opening, else
    /// the requirement as it stands.
    /// </summary>
    public string RequirementFor(string title) => RequirementIsOpening || Requirement.Length == 0 ? title.Trim() : Requirement;

    /// <summary>The first presentation's title as the requirement (2026-10-17), the opening's label gone; a requirement already settled stays.</summary>
    public void Settle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (RequirementIsOpening || Requirement.Length == 0)
        {
            Requirement = title.Trim();
            RequirementIsOpening = false;
        }
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

    /// <summary>
    /// A revision saved to <paramref name="path"/> (<see cref="PlanFiles.Save"/>, its one caller, for <c>present_plan</c> and
    /// <c>/plan save</c> alike): since 2026-10-17 talking ends and the opening settles into the title (<see cref="Settle"/>).
    /// </summary>
    public void Presented(string path, string title, int revision, DateTimeOffset created)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(title);
        Path = path;
        Title = title;
        Revision = revision;
        Created ??= created;
        Talking = false;
        Settle(title);
    }

    /// <summary>The approval noted, to act on when the turn ends.</summary>
    public void Approve(bool fresh) => Approved = fresh ? PlanChoice.ApproveFresh : PlanChoice.Approve;

    /// <summary>A noted approval dropped (the reply that gave it was stopped): the plan stays presented, for <c>/plan approve</c>.</summary>
    public void ClearApproval() => Approved = null;

    /// <summary>Plan mode off and everything forgotten (a converged loop in <see cref="Finished"/> aside: its turn says it).</summary>
    public void Exit()
    {
        Active = false;
        Requirement = "";
        RequirementIsOpening = false;
        Talking = false;
        Path = null;
        Title = null;
        Revision = 0;
        Created = null;
        Approved = null;
        Closing = null;
    }

    /// <summary>The session row's copy; null while plan mode is off.</summary>
    public StoredPlan? ToStored() => Active
        ? new StoredPlan { Requirement = Requirement, Path = Path, Title = Title, Revision = Revision, Created = Created, Talking = Talking, RequirementIsOpening = RequirementIsOpening }
        : null;

    /// <summary>The state a session row kept (plan mode off for none); an approval is never restored, nor a converged loop.</summary>
    public void Restore(StoredPlan? stored)
    {
        Exit();
        Finished = null;
        if (stored is null || (string.IsNullOrWhiteSpace(stored.Requirement) && !stored.Talking))
        {
            return;
        }

        Active = true;
        Requirement = stored.Requirement.Trim();
        RequirementIsOpening = stored.RequirementIsOpening && Requirement.Length > 0;
        Talking = stored.Talking;
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
        // A pass of the gap-closing loop under way (2026-10-13): its rules ride in the directive, so a compaction cannot lose them; the
        // streak, the clean count, the confirmation and the pass's reviewer since 2026-10-17.
        var gap = Closing is { Pending: false, Converged: false } loop
            ? new PlanGapState(loop.Pass, loop.Limit, loop.Reviewer, loop.CleanStreak, loop.CleanPasses, loop.Confirmation, loop.PassReviewer)
            : null;
        // /plan chat (2026-10-17): the talking form while talking, the opening labeled while the requirement is only that.
        var talk = new PlanTalk(Talking, RequirementIsOpening);
        return new PlanTurn(PlanText.Directive(requirement, path, revision, null, gap, null, talk), tool) { Compose = (advisors, research) => PlanText.Directive(requirement, path, revision, advisors, gap, research, talk) };
    }
}
