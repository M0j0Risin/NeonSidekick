using NeonSidekick.Camera;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Shell;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

// ── The panes that ask, on the PC and the Web UI at once (2026-10-14) ───────
// Every pane a turn opens to ask the user something — ask_user, a command's approval, an allow, a yes/no, the plan — is put on the
// Web UI's page too, and the first answer wins. One helper (AskBothAsync) in place of the ten copies each asker had of the same
// RequestPaneAsync lambda.

/// <summary>What an ask came to: the answer, who gave it, or never asked (no watcher to run the pane).</summary>
internal readonly record struct AskOutcome<T>(T? Value, AskSource? Who, bool NeverAsked);

internal sealed partial class ChatScreen
{
    /// <summary>
    /// Puts <paramref name="spec"/> on the page and <paramref name="pane"/> on the PC, on the turn task, and returns the first answer. The web
    /// ask opens first, then the pane is requested from the watcher: a page can answer while the PC's keys are held by another pane
    /// (<c>/settings</c> open under the reply), and a pane whose ask was answered meanwhile returns without drawing. Both sides claim
    /// before they count (<see cref="WebAskHandle.TryClaim"/>): a web answer claims, then cancels the pane — which answers Deny, No or Refine
    /// when cancelled, an answer that must never count — and the pane's own answer counts only when its claim wins. A pane closed with no
    /// answer (the turn's end, the pane-close signal) is the pane's answer as before; no watcher is never asked, the web ask withdrawn.
    /// Under a web answer the transcript says so and the log names the address. <paramref name="failure"/> is the pane's own failure line.
    /// </summary>
    private async Task<AskOutcome<T>> AskBothAsync<T>(WebAskSpec spec, Func<CancellationToken, Task<T>> pane, Func<WebAnswerRequest, T> fromWeb, string failure, CancellationToken turnToken, bool noticeAnswer = true)
    {
        var handle = _webUi.Asks.Open(spec);
        var paneToken = _paneClose?.Token ?? CancellationToken.None;
        using var paneCts = CancellationTokenSource.CreateLinkedTokenSource(paneToken, turnToken);
        T? paneResult = default;
        bool paneAnswered = false;
        var request = _keys.RequestPaneAsync(async () =>
        {
            if (handle.Winner is not null)
            {
                return;   // answered on the page before the pane's turn came
            }

            try
            {
                var result = await pane(paneCts.Token).ConfigureAwait(false);
                if (handle.TryClaim(AskSource.Pc))
                {
                    paneResult = result;
                    paneAnswered = true;
                }
            }
            catch (OperationCanceledException) when (paneCts.IsCancellationRequested)
            {
                // Closed under it: by the page's answer, the turn's end or the pane-close signal.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !paneCts.IsCancellationRequested)
            {
                DiagnosticLog.Error(ScreenPane.Category, failure + Llm.Assistant.Explain(ex), ex);
            }
        });

        try
        {
            var first = await Task.WhenAny(request, handle.Answered).WaitAsync(turnToken).ConfigureAwait(false);
            if (first == request)
            {
                bool neverAsked = false;
                try
                {
                    await request.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!turnToken.IsCancellationRequested)
                {
                    neverAsked = true;
                }

                if (paneAnswered)
                {
                    return new AskOutcome<T>(paneResult, AskSource.Pc, NeverAsked: false);
                }

                // The pane closed with no answer, or was never run: the PC claims so the page may not answer it now. A claim that fails means
                // the page's answer landed in between (2026-10-19, the review: the claim's result was ignored and that answer, told OK, was
                // dropped), and it counts as below.
                if (handle.TryClaim(AskSource.Pc))
                {
                    return new AskOutcome<T>(default, null, neverAsked);
                }
            }

            // The page answered: the pane closes, its own answer dropped.
            var answer = await handle.Answered.ConfigureAwait(false);
            VoiceSession.SafeCancel(paneCts);
            string said = handle.Describe(answer);
            DiagnosticLog.Info(WebUiCategory, WebUiText.AnsweredLogLine(spec.Title, said, handle.Remote));
            if (noticeAnswer)
            {
                _transcript.Notice(WebUiText.AnsweredNotice(said));
            }

            return new AskOutcome<T>(fromWeb(answer), AskSource.Web, NeverAsked: false);
        }
        finally
        {
            _webUi.Asks.Close(handle);
        }
    }

    /// <summary>
    /// A yes/no question (a Docker change, a Home Assistant call, a print, the shopping list's clear, an advisor's confirm) on the PC's pane
    /// (<see cref="SettingsMenu.ConfirmAsync"/>: the cursor on No, ESC a no) and the page at once. Null (never asked) without the pane or a
    /// watcher to run it; no answer is a no.
    /// </summary>
    private async Task<bool?> ConfirmBothAsync(string question, string failure, CancellationToken turnToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        var spec = ConfirmSpec(question);
        var outcome = await AskBothAsync<bool?>(spec, async token => await _menu.ConfirmAsync(question, token).ConfigureAwait(false), a => a.Choice == "yes", failure, turnToken).ConfigureAwait(false);
        return outcome.NeverAsked ? null : outcome.Value ?? false;
    }

    /// <summary>
    /// An allow question (the camera, the screen, a SQLite or server database write) on the PC's pane (<see cref="CameraMenu.AllowAsync"/>) and
    /// the page at once: Deny, Once or this session. <paramref name="whole"/> is what the PC's View button shows (a statement), the page's body
    /// whole since it has no such button. Null when never asked or not answered.
    /// </summary>
    private async Task<CameraAllow?> AllowBothAsync(string title, string prompt, IReadOnlyList<string>? whole, Func<CancellationToken, Task<CameraAllow?>> pane, CancellationToken turnToken)
    {
        var spec = new WebAskSpec("allow", title,
            [new WebChoice("deny", CameraText.DenyRow), new WebChoice("once", CameraText.AllowOnceRow), new WebChoice("session", CameraText.AllowSessionRow)],
            Body: prompt, Lines: whole);
        var outcome = await AskBothAsync(spec, pane, a => a.Choice switch
        {
            "once" => CameraAllow.Once,
            "session" => CameraAllow.Session,
            _ => (CameraAllow?)CameraAllow.Deny,
        }, "The allow pane failed: ", turnToken).ConfigureAwait(false);
        return outcome.NeverAsked ? null : outcome.Value;
    }

    /// <summary>The page's form of a command's approval: the whole command (the PC's View), the PC's four rows, the prefixes named.</summary>
    internal static WebAskSpec ApprovalSpec(CommandRequest request) =>
        new("approval", request.IsScript ? ShellText.ScriptApprovalTitle : ShellText.ApprovalTitle,
            [
                new WebChoice("deny", ShellText.DenyRow),
                new WebChoice("once", ShellText.OnceRow),
                new WebChoice("session", ShellText.SessionRow(request)),
                new WebChoice("permanent", ShellText.PermanentRow(request)),
            ],
            Lines: ShellText.WholeCommandLines(request));

    /// <summary>The page's answer to an approval as the gate reads it.</summary>
    internal static CommandChoice ApprovalChoice(WebAnswerRequest answer) => answer.Choice switch
    {
        "once" => CommandChoice.Once,
        "session" => CommandChoice.Session,
        "permanent" => CommandChoice.Permanent,
        _ => CommandChoice.Deny,
    };

    /// <summary>The page's form of <c>ask_user</c>: every question at once, answered with the PC's Submit.</summary>
    internal static WebAskSpec QuestionSpec(IReadOnlyList<AskQuestion> questions) =>
        new("question", QuestionMenu.Title, [],
            Questions: questions.Select(q => new WebQuestion(q.Question, q.Title, q.Multi, q.Options)).ToList(),
            SubmitLabel: QuestionMenu.SubmitRow);

    /// <summary>The page's answers as the tool reads them: the options picked in option order, a typed answer last.</summary>
    internal static IReadOnlyList<AskAnswer> QuestionAnswers(IReadOnlyList<AskQuestion> questions, WebAnswerRequest answer)
    {
        var given = answer.Questions ?? [];
        var answers = new List<AskAnswer>(questions.Count);
        for (int i = 0; i < questions.Count; i++)
        {
            var picked = i < given.Count ? given[i].Picked ?? [] : [];
            var choices = picked.Where(p => p >= 0 && p < questions[i].Options.Count).Distinct().Order().Select(p => questions[i].Options[p]).ToList();
            if (i < given.Count && !string.IsNullOrWhiteSpace(given[i].Other))
            {
                choices.Add(given[i].Other!.Trim());
            }

            answers.Add(new AskAnswer(questions[i], choices));
        }

        return answers;
    }

    /// <summary>The page's form of the plan's approval: the plan whole, and Approve, Approve fresh, Refine (with words) and Cancel. Close gaps stays the PC's.</summary>
    internal static WebAskSpec PlanSpec(PlanPresentation plan, string? checkLine = null) =>
        new("plan", PlanText.ApprovalTitle,
            [
                new WebChoice("approve", PlanText.ApproveRow),
                new WebChoice("fresh", PlanText.ApproveFreshRow),
                new WebChoice("ralph", RalphText.ApproveRalphRow),
                new WebChoice("refine", PlanText.RefineRow),
                new WebChoice("cancel", PlanText.CancelRow),
            ],
            Body: checkLine is { Length: > 0 } line ? PlanText.RalphCaptionLead + line : null,   // the Ralph loop's checks (2026-10-18), as the pane's caption says them
            Markdown: plan.Markdown,
            TextChoice: "refine");

    /// <summary>The page's answer to the plan as the pane would give it; Refine's words as the PC's typed line is read.</summary>
    internal static PlanVerdict PlanVerdictOf(WebAnswerRequest answer) => answer.Choice switch
    {
        "approve" => new PlanVerdict(PlanChoice.Approve),
        "fresh" => new PlanVerdict(PlanChoice.ApproveFresh),
        "ralph" => new PlanVerdict(PlanChoice.ApproveRalph),
        "cancel" => new PlanVerdict(PlanChoice.Cancel),
        _ => new PlanVerdict(PlanChoice.Refine, (answer.Text ?? "").ReplaceLineEndings(" ").Trim() is { Length: > 0 } feedback ? feedback : null),
    };
}
