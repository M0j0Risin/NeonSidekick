namespace NeonSidekick.Plans;

/// <summary>Who looks for a plan's gaps on each pass of the loop (<see cref="PlanGapLoop"/>).</summary>
public enum PlanReviewer
{
    /// <summary>The planning model alone; the advisors are left out of the pass.</summary>
    Model,

    /// <summary>At least one advisor (<c>neon_advisor</c>) finds the gaps each pass; the model closes what they name.</summary>
    Advisor,

    /// <summary>The model, consulting an advisor only where a role fits (plan mode's own habit).</summary>
    Any,

    /// <summary>
    /// The model closes the gaps, and an advisor looks for more on each confirming pass (2026-10-17, the user's pick): a pass after a
    /// clean one is the advisor's (<see cref="PlanGapLoop.PassReviewer"/>), and the loop asks at least two clean passes in a row.
    /// </summary>
    Confirm,
}

/// <summary>What the words after <c>/plan gaps</c> are (<see cref="PlanText.ParseGapsArgs"/>, 2026-10-17).</summary>
public enum GapsArgs
{
    /// <summary>None, a reviewer, a pass count in range, or one of each in either order.</summary>
    Ok,

    /// <summary>One word that is neither a reviewer nor a number.</summary>
    Unknown,

    /// <summary>A pass count outside <see cref="Settings.AppSettingsData.MinPlanGapPasses"/>–<see cref="Settings.AppSettingsData.MaxPlanGapPasses"/>, a sign or an overflow too.</summary>
    OutOfRange,

    /// <summary>Anything else (two reviewers, two numbers, three words or more): plan detail, not the command.</summary>
    NotGaps,
}

/// <summary>Why a gap-closing loop ended (<see cref="PlanGapLoop.Next"/>).</summary>
public enum PlanGapStop
{
    /// <summary>Enough passes in a row reported no gaps (<see cref="PlanGapLoop.CleanPasses"/>): the plan went back to the user on the approval pane.</summary>
    Converged,

    /// <summary>A message was queued under the pass's reply: it goes first.</summary>
    UserSpoke,

    /// <summary>The pass limit was reached with gaps still reported.</summary>
    Limit,

    /// <summary>Gaps were reported but the plan did not change, or the same gaps came back.</summary>
    Stalled,

    /// <summary>The pass ended without a revision saved (a question in prose, a failed save, a file gone).</summary>
    NoRevision,

    /// <summary>The pass's reply did not end on its own: ESC, a failure, an interrupt.</summary>
    Stopped,

    /// <summary>
    /// The pass limit came with the last pass clean but the streak short of <see cref="PlanGapLoop.CleanPasses"/> (2026-10-17, the
    /// user's call): no gaps were found, but there was no pass left to check it again. The plan goes back to the pane, as at a limit.
    /// </summary>
    Unconfirmed,
}

/// <summary>
/// The usage at a moment of a gap review (2026-10-17, each pass's cost): when, the main model's tokens (<see cref="Llm.TokenTally.Session"/>
/// less the reflections') and the advisors' (<see cref="Llm.TokenTally.Advisors"/>), both cumulative, so a reset or a compaction can't
/// make a pass's figure go negative. The caller reads it; the loop keeps the pass's start and subtracts.
/// </summary>
public sealed record PlanPassMeter(DateTimeOffset At, long Tokens, long AdvisorTokens);

/// <summary>
/// One pass of a gap review as the summary tells it (2026-10-17, <see cref="PlanText.ReviewSummaryLines"/>): its number, the gaps and
/// minor fixes it reported, whether its last report was clean, the confirming focus (<see cref="PlanText.ConfirmFocuses"/>' index, -1 on
/// a pass that was not confirming), whether an advisor did it under <see cref="PlanReviewer.Confirm"/>, and — when both ends were
/// metered — its time and tokens.
/// </summary>
public sealed record PlanPassRecord(int Pass, int Gaps, int Minor, bool Clean, int Focus, bool Advisor = false, TimeSpan? Elapsed = null, long? Tokens = null, long? AdvisorTokens = null);

/// <summary>
/// The gap-closing loop (2026-10-13, the user's ask: a <c>Close gaps…</c> choice on the plan's approval pane that keeps
/// reviewing the plan until a pass finds nothing left): the reviewer, the pass number and its limit, the gaps each pass
/// closed and the plan's body as the pass began. One pass is one turn: the screen (or headless) sends the pass's message,
/// <c>present_plan</c> records what the pass closed (<see cref="Record"/>, <see cref="MarkClean"/>, <see cref="MarkConverged"/>), and
/// after the turn <see cref="Next"/> says whether another pass follows. Pure: the callers read the file and the queue. A stop
/// always ends the loop (<see cref="PlanSession.StopClosing"/>); a new <c>/plan gaps</c> starts a fresh one, so there is
/// no paused state to go stale.
/// <para>Clean passes in a row (2026-10-17, the user's report: a pass would answer "0 gaps" when the next would still find plenty, and
/// was believed at once): the loop converges only once <see cref="CleanPasses"/> passes in a row reported no gaps. A pass after a
/// clean one is a confirming pass (<see cref="Confirmation"/>), told to look again with a focus of its own; a pass that closed gaps
/// and then reported none has looked once at the revised plan, so it starts a streak of 1; two clean reports in one pass give the same
/// <see cref="CleanCount"/>, so a pass can never confirm itself. Each ended pass is kept in <see cref="History"/> for the summary.</para>
/// </summary>
public sealed class PlanGapLoop
{
    private List<string> _passGaps = [];
    private List<string> _passMinor = [];
    private readonly List<PlanPassRecord> _history = [];
    private string? _baseline;
    private PlanPassMeter? _passStart;
    private bool _passAdvisor;
    private bool _passRecorded = true;
    private readonly int _focusCount;

    /// <summary>
    /// Whether a review or an audit that stopped with <paramref name="reason"/> rings its stop tone (2026-10-21, the user's ask:
    /// <see cref="Settings.AppSettingsData.PlanGapStopTone"/>, <see cref="Settings.AppSettingsData.CodeAuditStopTone"/>): every stop the user
    /// didn't cause, as the Ralph loop's. A message (<see cref="PlanGapStop.UserSpoke"/>) never; <see cref="PlanGapStop.Stopped"/> only when
    /// <paramref name="failed"/> (a failed turn, the working directory moved, a pass never sent; Esc, the page's Stop and a withdrawal are not).
    /// </summary>
    public static bool Rings(PlanGapStop reason, bool failed) => reason switch
    {
        PlanGapStop.UserSpoke => false,
        PlanGapStop.Stopped => failed,
        _ => true,
    };

    public PlanGapLoop(PlanReviewer reviewer, int limit) : this(reviewer, limit, 1)
    {
    }

    /// <param name="cleanPasses">
    /// <see cref="Settings.AppSettingsData.PlanGapCleanPasses"/>: clamped to 1–5, raised to at least 2 under <see cref="PlanReviewer.Confirm"/>,
    /// then lowered to a pass limit that could never fit it (the user's call, 2026-10-17), once, here.
    /// </param>
    /// <param name="focusCount">
    /// How many confirming focuses a pass's record rotates through (2026-10-18, the code audit's dimensions): null for
    /// <see cref="PlanText.ConfirmFocuses"/>' count, a plan's.
    /// </param>
    public PlanGapLoop(PlanReviewer reviewer, int limit, int cleanPasses, int? focusCount = null)
    {
        _focusCount = Math.Max(1, focusCount ?? PlanText.ConfirmFocuses.Count);
        Reviewer = reviewer;
        Limit = Math.Max(0, limit);
        CleanAsked = Math.Clamp(cleanPasses, Settings.AppSettingsData.MinPlanGapCleanPasses, Settings.AppSettingsData.MaxPlanGapCleanPasses);
        int clean = reviewer == PlanReviewer.Confirm ? Math.Max(2, CleanAsked) : CleanAsked;
        CleanPasses = Limit > 0 && Limit < clean ? Limit : clean;
    }

    public PlanReviewer Reviewer { get; }

    /// <summary>The most passes the loop may run; 0 for no limit. Read once, when the loop starts.</summary>
    public int Limit { get; }

    /// <summary>The clean passes in a row the loop converges on (2026-10-17): the setting's, raised by <see cref="PlanReviewer.Confirm"/>, lowered by a short limit.</summary>
    public int CleanPasses { get; }

    /// <summary>The setting's value as given, clamped (2026-10-17), for <see cref="CleanLowered"/>'s notice.</summary>
    public int CleanAsked { get; }

    /// <summary>A pass limit below the setting lowered the clean count for this review (2026-10-17): said once as pass 1 begins.</summary>
    public bool CleanLowered => CleanPasses < CleanAsked;

    /// <summary>The pass under way, from 1; 0 while the loop waits for its first pass (<see cref="Pending"/>).</summary>
    public int Pass { get; private set; }

    /// <summary>Chosen, but the first pass not sent yet: the turn that chose it is still ending.</summary>
    public bool Pending => Pass == 0;

    /// <summary>Enough clean passes in a row; the loop is over once the turn ends.</summary>
    public bool Converged { get; private set; }

    /// <summary>The gaps closed over every pass so far.</summary>
    public int Closed { get; private set; }

    /// <summary>The minor fixes (2026-10-17, <c>present_plan</c>'s <c>minor</c>) over every pass so far; never gaps.</summary>
    public int MinorFixed { get; private set; }

    /// <summary>The revisions <c>present_plan</c> saved (or found unchanged) in this pass.</summary>
    public int Presented { get; private set; }

    /// <summary>The gaps this pass closed, in the order they were reported.</summary>
    public IReadOnlyList<string> PassGaps => _passGaps;

    /// <summary>The minor fixes this pass reported (2026-10-17), in order.</summary>
    public IReadOnlyList<string> PassMinor => _passMinor;

    /// <summary>What this pass's latest clean report said it checked (2026-10-17, <c>present_plan</c>'s <c>checked</c>); empty otherwise.</summary>
    public IReadOnlyList<string> PassChecked { get; private set; } = [];

    /// <summary>The gaps the pass before this one closed; empty on the first pass.</summary>
    public IReadOnlyList<string> LastGaps { get; private set; } = [];

    /// <summary>The clean passes in a row before this one (2026-10-17).</summary>
    public int CleanStreak { get; private set; }

    /// <summary>This pass's latest <c>present_plan</c> reported no gaps (2026-10-17).</summary>
    public bool PassClean { get; private set; }

    /// <summary>What this pass adds to the streak if its report stands: one more than the streak, or 1 for a pass that closed gaps first.</summary>
    public int CleanCount => _passGaps.Count == 0 ? CleanStreak + 1 : 1;

    /// <summary>A clean report now would end the review: read before <see cref="MarkClean"/> or <see cref="MarkConverged"/>.</summary>
    public bool WouldConverge => CleanCount >= CleanPasses;

    /// <summary>The confirming passes begun so far (2026-10-17), across streaks, so a streak that breaks and starts again moves on to the next focus.</summary>
    public int Confirmations { get; private set; }

    /// <summary>This pass's confirmation, from 1; 0 on a pass that is not confirming.</summary>
    public int Confirmation { get; private set; }

    /// <summary>
    /// Who reviews this pass (2026-10-17): under <see cref="PlanReviewer.Confirm"/> the advisor on a confirming pass and the model
    /// otherwise; for the other reviewers, the reviewer itself.
    /// </summary>
    public PlanReviewer PassReviewer => Reviewer != PlanReviewer.Confirm ? Reviewer : CleanStreak > 0 ? PlanReviewer.Advisor : PlanReviewer.Model;

    /// <summary>Every pass that ended (2026-10-17), in order, for <see cref="PlanText.ReviewSummaryLines"/>.</summary>
    public IReadOnlyList<PlanPassRecord> History => _history;

    /// <summary>
    /// The next pass begins over <paramref name="body"/>, the plan as it stands (the file's body, header aside). <paramref name="meter"/>,
    /// when given, is the pass's start for its cost; <paramref name="advisorsOffered"/> whether the turn offers an advisor, so a
    /// confirming pass under <see cref="PlanReviewer.Confirm"/> is recorded as the advisor's only when one was there to ask.
    /// </summary>
    public void BeginPass(string? body, PlanPassMeter? meter = null, bool advisorsOffered = true)
    {
        if (Pass > 0)
        {
            LastGaps = _passGaps;
        }

        // The streak before the new pass: this one's report, read before its gaps are cleared.
        CleanStreak = Pass > 0 && PassClean ? CleanCount : 0;
        _passGaps = [];
        _passMinor = [];
        PassChecked = [];
        PassClean = false;
        Presented = 0;
        _baseline = body;
        Pass++;
        Confirmation = CleanStreak > 0 ? ++Confirmations : 0;
        _passStart = meter;
        _passAdvisor = advisorsOffered && Reviewer == PlanReviewer.Confirm && PassReviewer == PlanReviewer.Advisor;
        _passRecorded = false;
    }

    /// <summary>A revision saved with <paramref name="gaps"/> closed (blank ones already dropped), and <paramref name="minor"/> fixes that are not gaps.</summary>
    public void Record(IReadOnlyList<string> gaps, IReadOnlyList<string>? minor = null)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        Presented++;
        PassClean = false;
        _passGaps.AddRange(gaps);
        Closed += gaps.Count;
        AddMinor(minor);
    }

    /// <summary>A revision presented with no gaps that does not end the review yet (2026-10-17): the streak grows when the pass ends.</summary>
    public void MarkClean(IReadOnlyList<string>? @checked = null, IReadOnlyList<string>? minor = null)
    {
        Presented++;
        PassClean = true;
        PassChecked = @checked ?? [];
        AddMinor(minor);
    }

    /// <summary>A revision presented with no gaps that ends the review (<see cref="WouldConverge"/>): the loop has converged.</summary>
    public void MarkConverged(IReadOnlyList<string>? @checked = null, IReadOnlyList<string>? minor = null)
    {
        Presented++;
        PassClean = true;
        Converged = true;
        PassChecked = @checked ?? [];
        AddMinor(minor);
    }

    private void AddMinor(IReadOnlyList<string>? minor)
    {
        if (minor is { Count: > 0 })
        {
            _passMinor.AddRange(minor);
            MinorFixed += minor.Count;
        }
    }

    /// <summary>
    /// What follows the pass that just ended: null for another pass, else why the loop stops. <paramref name="endedOnItsOwn"/>
    /// is the turn's own ending (no ESC, failure or interrupt); <paramref name="userSpoke"/> a message waiting that would be sent
    /// next; <paramref name="body"/> the plan's body as the pass left it (null when the file cannot be read); <paramref name="meter"/>
    /// the usage now, for the pass's cost. The pass goes into <see cref="History"/> once, before the stop is decided.
    /// </summary>
    public PlanGapStop? Next(bool endedOnItsOwn, bool userSpoke, string? body, PlanPassMeter? meter = null)
    {
        RecordPass(meter);
        if (Converged)
        {
            return PlanGapStop.Converged;
        }

        if (!endedOnItsOwn)
        {
            return PlanGapStop.Stopped;
        }

        if (Presented == 0 || body is null)
        {
            return PlanGapStop.NoRevision;
        }

        // A confirming pass leaves the plan unchanged by design, so an unchanged body stalls only a pass that reported gaps.
        if (!PassClean && (string.Equals(body, _baseline, StringComparison.Ordinal) || (LastGaps.Count > 0 && SameGaps(_passGaps, LastGaps))))
        {
            return PlanGapStop.Stalled;
        }

        if (Limit > 0 && Pass >= Limit)
        {
            return PassClean ? PlanGapStop.Unconfirmed : PlanGapStop.Limit;
        }

        return userSpoke ? PlanGapStop.UserSpoke : null;
    }

    private void RecordPass(PlanPassMeter? meter)
    {
        if (_passRecorded || Pass == 0)
        {
            return;
        }

        _passRecorded = true;
        TimeSpan? elapsed = null;
        long? tokens = null;
        long? advisorTokens = null;
        if (_passStart is { } start && meter is { } end)
        {
            elapsed = end.At - start.At < TimeSpan.Zero ? TimeSpan.Zero : end.At - start.At;
            tokens = Math.Max(0, end.Tokens - start.Tokens);
            advisorTokens = Math.Max(0, end.AdvisorTokens - start.AdvisorTokens);
        }

        int focus = Confirmation > 0 ? (Confirmation - 1) % _focusCount : -1;
        _history.Add(new PlanPassRecord(Pass, _passGaps.Count, _passMinor.Count, PassClean, focus, _passAdvisor, elapsed, tokens, advisorTokens));
    }

    /// <summary>The same gaps, order aside: trimmed, case aside, as sets.</summary>
    public static bool SameGaps(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var left = new HashSet<string>(a.Select(g => g.Trim()), StringComparer.OrdinalIgnoreCase);
        var right = new HashSet<string>(b.Select(g => g.Trim()), StringComparer.OrdinalIgnoreCase);
        return left.SetEquals(right);
    }
}

/// <summary>
/// What the plan directive says of a running loop (<see cref="PlanText.Directive(string, string?, int, IReadOnlyList{Settings.AdvisorEntry}?, PlanGapState?)"/>):
/// the pass, its limit (0 for none) and the reviewer chosen; since 2026-10-17 the clean passes in a row before this one, the count the
/// review ends on, this pass's confirmation (0 when it is not confirming) and who reviews this pass (null for the reviewer chosen); since
/// 2026-10-18 whether the pass may and can ask the user (<see cref="Settings.AppSettingsData.PlanGapAskUser"/> with <c>ask_user</c> offered, since
/// 2026-10-21: false, the directive says to ask
/// nothing and to write a decision only the user can make into <c>## Risks</c> as an open question).
/// </summary>
public sealed record PlanGapState(int Pass, int Limit, PlanReviewer Reviewer, int CleanSoFar = 0, int CleanPasses = 1, int Confirmation = 0, PlanReviewer? PassReviewer = null, bool AskUser = true)
{
    /// <summary>Who reviews this pass: <see cref="PassReviewer"/>, else the reviewer chosen.</summary>
    public PlanReviewer Who => PassReviewer ?? Reviewer;
}
