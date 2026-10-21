using Microsoft.Extensions.AI;
using NeonSidekick.Advisors;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── The Ralph loop's reviewer (2026-10-18, phase 6) ───────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The review's busy label.</summary>
    public const string RalphReviewLabel = "reviewing the step";

    /// <summary>The loop whose run has said <see cref="RalphText.ReviewConfirmFallbackNotice"/> already (once a run).</summary>
    private RalphLoop? _ralphConfirmFallbackSaid;

    /// <summary>
    /// An independent look at a step whose checks stood (<c>Ralph loop reviewer</c>, <see cref="RalphReviewers"/>): the model or the first
    /// advisor, under the watch with a spinner; Esc stops the loop (null).
    /// </summary>
    private async Task<RalphVerdict?> ReviewRalphStepAsync(RalphLoop loop, PlanStep step, FinishReport? report, RalphDelta delta, CancellationToken cancellationToken)
    {
        var effective = _effective();
        var snapshot = loop.Snapshot;
        bool byAdvisor = RalphReviewers.ByAdvisor(snapshot.Reviewer, AdvisorPassOffered());
        if (byAdvisor && effective.AdvisorConfirm && snapshot.Checkpoint == 0)
        {
            // An unattended loop (no checkpoint: it never waits on the user) asks no yes either (2026-10-20, a review: each step's review waited on
            // one until someone answered): the model reviews, as headless does under the setting, said once a run.
            if (!ReferenceEquals(_ralphConfirmFallbackSaid, loop))
            {
                _ralphConfirmFallbackSaid = loop;
                _transcript.Notice(RalphText.ReviewConfirmFallbackNotice);
            }

            byAdvisor = false;
        }
        else if (byAdvisor && effective.AdvisorConfirm && _neonAdvisor.Advisors.FirstOrDefault() is { } advisor
            && !await RalphConfirmAsync(AdvisorText.ConfirmQuestion(advisor, RalphText.ReviewConfirmQuestion(step.Text)), cancellationToken).ConfigureAwait(false))
        {
            // Advisor confirm holds for the loop's review too (2026-10-20, a review: it called the advisor directly, past the yes the setting asks
            // for, and every step's diff went out unasked): a no has the model review instead, as headless always does under the setting.
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            DiagnosticLog.Info(AppCategory, $"ralph: the advisor's review of pass {loop.Pass} was declined; the model reviews");
            byAdvisor = false;
        }

        string request = RalphReviewers.Request(step, report, delta, _session.ContextLength?.Tokens ?? 0);
        RalphVerdict verdict = RalphVerdict.Unclear();
        long tokens = 0;
        bool stopped = await WaitUnderWatchAsync(async token =>
        {
            using (_transcript.BeginBusy(RalphReviewLabel))
            {
                (verdict, tokens) = byAdvisor
                    ? await RalphReviewers.ByAdvisorAsync(_neonAdvisor, _session.Usage, request, effective, token).ConfigureAwait(false)
                    : await RalphReviewers.ByModelAsync(_session, RalphFiles, snapshot, step, request, effective.LlmPictureKeep, effective.LlmPictureMegabytes, ModelSeesPictures(), token).ConfigureAwait(false);
            }
        }, KeySource.IsTurnCancel, cancellationToken, pointer: true).ConfigureAwait(false);
        loop.AddReviewTokens(tokens);
        if (stopped)
        {
            return null;
        }

        DiagnosticLog.Info(AppCategory, RalphReviewers.LogLine(loop.Pass, byAdvisor, verdict));
        return verdict;
    }
}

/// <summary>
/// The Ralph loop's reviewers (2026-10-18, phase 6), the screen's and headless's: <c>model</c> (or <c>any</c> with no advisor offered) asks the chat's
/// model once — no conversation, a <c>step_verdict</c> tool (<see cref="StepVerdictTool"/>, its call read, never run), the session's reasoning
/// effort, the step's named reference pictures — and <c>advisor</c> (or <c>any</c> with one offered) the first advisor, its thread neither replayed nor
/// added to (<see cref="NeonAdvisorTool.AskAsync"/>'s <c>thread: false</c>), so it never judges advice it gave. A request error, or nothing
/// connected, is <see cref="RalphVerdictKind.Unclear"/>.
/// </summary>
internal static class RalphReviewers
{
    /// <summary>Whether <paramref name="reviewer"/> means an advisor reviews: <c>advisor</c> with one offered, <c>any</c> with one offered.</summary>
    public static bool ByAdvisor(RalphReviewer reviewer, bool advisorOffered) => reviewer is RalphReviewer.Advisor or RalphReviewer.Any && advisorOffered;

    /// <summary>The review's message: the step, its check, the report, the flags, the files and the diff, cut to a quarter of the known window (the diff first).</summary>
    public static string Request(PlanStep step, FinishReport? report, RalphDelta delta, int contextTokens)
    {
        int windowChars = contextTokens * ConversationCompactor.CharsPerToken;
        int room = windowChars > 0 ? Math.Min(RalphChanges.MaxDiffChars, windowChars / 4) : RalphChanges.MaxDiffChars;
        string diff = delta.Diff.Length <= room ? delta.Diff : delta.Diff[..room] + RalphText.ReviewDiffCut;
        return RalphText.ReviewRequest(step.Text, step.DoneWhen, report?.Summary ?? RalphText.NoReportSummary, report?.Verified ?? [], delta.Flags, delta.Changed, diff);
    }

    /// <summary>The --log line of a review.</summary>
    public static string LogLine(int pass, bool byAdvisor, RalphVerdict verdict) =>
        $"ralph: review of pass {pass} by {(byAdvisor ? "an advisor" : "the model")}: {verdict.Kind.ToString().ToLowerInvariant()}" + (verdict.Reason.Length > 0 ? " — " + verdict.Reason : "");

    /// <summary>The one-shot model review: its verdict and the tokens it spent (added to the session's tally too).</summary>
    public static async Task<(RalphVerdict Verdict, long Tokens)> ByModelAsync(LlmSession session, WorkingDirectory files, RalphSnapshot snapshot, PlanStep step, string request, int keep, int megabytes, bool canSee, CancellationToken cancellationToken)
    {
        if (session.CreateAssistant(new ConversationHistory(RalphText.ReviewerSystem)) is not { } reviewer)
        {
            return (RalphVerdict.Unclear("nothing connected"), 0);
        }

        var parts = new List<AIContent> { new TextContent(request) };
        if ((step.NamedRefs.Count > 0 || step.NamedPaths.Any(ImageFile.IsImagePath)) && PlanFiles.ReadWhole(files, snapshot.Path) is { } text)
        {
            foreach (var image in PlanReferences.ForRun(files, snapshot.Path, PlanDocument.Body(text), step, keep, megabytes, canSee, out _))
            {
                parts.Add(ConversationHistory.ImagePart(image));
            }
        }

        try
        {
            IReadOnlyList<ChatMessage> messages = [new(ChatRole.System, RalphText.ReviewerSystem), new(ChatRole.User, parts)];
            var response = await reviewer.RequestAsync(messages, [new StepVerdictTool()], reviewer.Reasoning, cancellationToken).ConfigureAwait(false);
            long tokens = response.Usage?.Total ?? 0;
            if (response.Usage is { } usage)
            {
                session.Usage.Add(usage);
            }

            var call = response.Calls.FirstOrDefault(c => string.Equals(c.Name, RalphReview.ToolName, StringComparison.Ordinal));
            return (call is not null && RalphReview.FromCall(call.Arguments) is { } called ? called : RalphReview.FromText(response.Text), tokens);
        }
        // A cancellation that is not the caller's (an HTTP client's timeout is a TaskCanceledException) is a failed review, not a stop
        // (2026-10-19, the second review: it ran out through the judge and left the loop running).
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Info(ChatScreen.AppCategory, "ralph: the review failed: " + Assistant.Explain(ex));
            return (RalphVerdict.Unclear(Assistant.Explain(ex)), 0);
        }
    }

    /// <summary>The advisor review: the first advisor, no thread; its verdict read from the answer and the tokens its tally grew by.</summary>
    public static async Task<(RalphVerdict Verdict, long Tokens)> ByAdvisorAsync(NeonAdvisorTool advisors, TokenTally usage, string request, AppSettingsData effective, CancellationToken cancellationToken)
    {
        if (advisors.Advisors.FirstOrDefault() is not { } advisor)
        {
            return (RalphVerdict.Unclear("no advisor"), 0);
        }

        long before = usage.Advisors.Total;
        string answer;
        try
        {
            answer = await advisors.AskAsync(advisor, RalphText.ReviewerSystem + "\n\n" + request, "", null, effective, cancellationToken, thread: false).ConfigureAwait(false);
        }
        // A cancellation that is not the caller's (the request's own timeout) is a failed review, as the model's is (2026-10-20, a review: it ran out
        // through the judge, after the step's box was ticked, and the loop failed with the box standing unreviewed).
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Info(ChatScreen.AppCategory, "ralph: the advisor's review failed: " + Assistant.Explain(ex));
            return (RalphVerdict.Unclear(Assistant.Explain(ex)), Math.Max(0, usage.Advisors.Total - before));
        }

        long tokens = Math.Max(0, usage.Advisors.Total - before);
        return (answer.StartsWith("Error:", StringComparison.Ordinal) ? RalphVerdict.Unclear(answer) : RalphReview.FromText(answer), tokens);
    }
}
