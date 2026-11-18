using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.AI;
using NeonSidekick.Audits;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;
using NeonSidekick.Timers;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The gap review's and the audit's passes made reliable (2026-11-17, the gap-audit-reliability plan, steps 2–4): a server blip retried once,
/// a pass that forgot to report asked once, a message pausing a gap review that <c>/plan gaps resume</c> goes on with.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>A connection refused: a transient failure (<see cref="Llm.ModelErrorText.IsTransient"/>).</summary>
    private static HttpRequestException Refused() => new("refused", new SocketException((int)SocketError.ConnectionRefused));

    [Fact]
    public async Task PlanGaps_ATransientFailure_IsRetriedOnce_TheSameMessage_AndTheReviewGoesOn()
    {
        _settings.Update(d => d.PlanGapCleanPasses = 1);
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());                  // 0: presented; Close gaps…
        _chat.EnqueueText("Closing the gaps.");            // 1
        _chat.EnqueueText("(never seen)");                 // 2: pass 1 fails: the server refused
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown)); // 3: pass 1 again: nothing left; canceled on the pane
        _chat.EnqueueText("Canceled.");                    // 4
        _chat.BeforeUpdateOf = (request, update, _) => request == 2 ? throw Refused() : Task.CompletedTask;

        string output = await RunAsync();

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Contains(PlanText.GapRetryNotice(1, TimeSpan.Zero), output);
        string message = PlanText.GapPassMessage(PlanPath, 1, 5, PlanReviewer.Model);
        Assert.Equal(message, LastUserText(_chat.Requests[3]));
        Assert.Single(_chat.Requests[3], m => m.Role == ChatRole.User && m.Text == message);   // the failed turn left the history
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 1, 0), output);
        Assert.DoesNotContain(PlanText.GapStopNotice(PlanGapStop.Stopped, 1, 0), output);
    }

    [Fact]
    public async Task PlanGaps_ChosenAndTheTurnHasAServerBlip_StartsPassOneAnyway()
    {
        // 2026-11-17, the hand check: the server dropped under the presenting reply's closing words, and the review chosen on its pane was dropped.
        _settings.Update(d =>
        {
            d.PlanGapCleanPasses = 1;
            d.PlanGapStopTone = TimerTones.Bell;
        });
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());                   // 0: presented; Close gaps…
        _chat.EnqueueText("(never seen)");                  // 1: the closing words: the server refused
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown)); // 2: pass 1: nothing left; canceled on the pane
        _chat.EnqueueText("Canceled.");                     // 3
        _chat.BeforeUpdateOf = (request, update, _) => request == 1 ? throw Refused() : Task.CompletedTask;

        string output = await RunAsync();

        Assert.DoesNotContain(PlanText.GapsDroppedNotice, output);
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 1, 5, PlanReviewer.Model), LastUserText(_chat.Requests[2]));
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 1, 0), output);
        await UntilAsync(() => _tonePlayback.Writes.Count > 0, CancellationToken.None);
        Assert.Single(_tonePlayback.Writes);   // the convergence's
    }

    [Fact]
    public async Task PlanGaps_ASecondTransientFailure_Stops_AndRingsTheTone()
    {
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('a'), Keys.Enter]], "/plan Add a guide", "/exit");
        _settings.Update(d => d.PlanGapStopTone = TimerTones.Bell);
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.EnqueueText("(never seen)");   // 2: fails
        _chat.EnqueueText("(never seen)");   // 3: the retry fails too
        _chat.EnqueueText("Approved.");
        _chat.BeforeUpdateOf = (request, update, _) => request is 2 or 3 ? throw Refused() : Task.CompletedTask;

        string output = await RunAsync();
        await UntilAsync(() => _tonePlayback.Writes.Count > 0, CancellationToken.None);

        Assert.Contains(PlanText.GapRetryNotice(1, TimeSpan.Zero), output);
        Assert.Equal(Clip(TimerTones.Bell), Assert.Single(_tonePlayback.Writes));
    }

    [Fact]
    public async Task PlanGaps_APassThatOnlyTalks_IsAskedOnce_AndThenPresents()
    {
        _settings.Update(d => d.PlanGapCleanPasses = 1);
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.EnqueueText("Which test framework do you use?");   // 2: pass 1 talks, no report
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown));       // 3: the follow-up: nothing left
        _chat.EnqueueText("Canceled.");

        string output = await RunAsync();

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Equal(PlanText.GapFollowUpMessage(PlanPath, 1), LastUserText(_chat.Requests[3]));
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 1, 0), output);
        Assert.DoesNotContain(PlanText.GapStopNotice(PlanGapStop.NoRevision, 1, 0), output);
    }

    [Fact]
    public async Task PlanGaps_APassTheToolCapEnded_IsAskedOnce_AndRingsNothing()
    {
        // The tool cap made a failed turn: Stopped, and the tone rang (2026-11-17: a pass that ended there is one that ended on its own).
        _settings.Update(d =>
        {
            d.PlanGapCleanPasses = 1;
            d.LlmMaxToolIterations = 2;   // every turn's: the planning turn's call and its words fit
            d.PlanGapStopTone = TimerTones.Bell;
        });
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.Enqueue(FakeChatClient.Call("r1", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = PlanPath }));   // 2
        _chat.Enqueue(FakeChatClient.Call("r2", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = PlanPath }));   // 3: the cap
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown));       // 4: the follow-up: nothing left
        _chat.EnqueueText("Canceled.");

        string output = await RunAsync();

        Assert.Contains("Stopped after 2 tool iterations", output);
        Assert.Equal(PlanText.GapFollowUpMessage(PlanPath, 1), LastUserText(_chat.Requests[4]));
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 1, 0), output);
        await UntilAsync(() => _tonePlayback.Writes.Count > 0, CancellationToken.None);
        Assert.Single(_tonePlayback.Writes);   // the convergence's; the cap rang nothing
    }

    [Fact]
    public async Task PlanGaps_AMessageDuringAPass_PausesTheReview_AndResumeGoesOnAtTheNextPass()
    {
        _settings.Update(d =>
        {
            d.PlanGapCleanPasses = 1;
            d.PlanGapStopTone = TimerTones.Bell;
        });
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "/plan gaps resume", "/exit");
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            if (request == 2 && update == 0)
            {
                // A page's line: before 2026-11-17 only the PC's drained queue stopped a gap review.
                Assert.Equal(WebOutcome.Ok, Web.Send(new WebSendRequest { Text = "add a test step too" }, null).Outcome);
            }

            return Task.CompletedTask;
        };
        _chat.Enqueue(PresentPlanCall());                                         // 0
        _chat.EnqueueText("Closing the gaps.");                                   // 1
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));   // 2: pass 1
        _chat.EnqueueText("Pass 1 done.");                                        // 3: then paused
        _chat.EnqueueText("Added.");                                              // 4: the page's line
        _chat.Enqueue(PresentGapsCall("p3", RevisedPlanMarkdown));                // 5: /plan gaps resume: pass 2, nothing left
        _chat.EnqueueText("Canceled.");                                           // 6

        string output = await RunAsync();

        Assert.Equal(7, _chat.Requests.Count);
        Assert.Contains(PlanText.GapsPausedNotice(1), output);
        Assert.Equal("add a test step too", LastUserText(_chat.Requests[4]));
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 2, 5, PlanReviewer.Model), LastUserText(_chat.Requests[5]));
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 2, 1), output);
        await UntilAsync(() => _tonePlayback.Writes.Count > 0, CancellationToken.None);
        Assert.Single(_tonePlayback.Writes);   // the convergence's; the pause, a stop you caused, rang nothing
    }

    [Fact]
    public async Task PlanGaps_TheShadowLine_EveryPass_AndAPauseKeepsItsStreak()
    {
        // 2026-11-17, phase 3: the plan shadow logged once a pass, with the stop the screen goes on to use; a review paused for a message and
        // resumed keeps its loop, so pass 2's shadow streak carries pass 1's.
        _settings.Update(d => d.PlanGapCleanPasses = 1);
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "/plan gaps resume", "/exit");
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            if (request == 2 && update == 0)
            {
                Assert.Equal(WebOutcome.Ok, Web.Send(new WebSendRequest { Text = "add a test step too" }, null).Outcome);
            }

            return Task.CompletedTask;
        };
        _chat.Enqueue(PresentPlanCall());                                                 // 0
        _chat.EnqueueText("Closing the gaps.");                                           // 1
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "[low] Add a test step"));   // 2: pass 1, one low gap
        _chat.EnqueueText("Pass 1 done.");                                                // 3: then paused
        _chat.EnqueueText("Added.");                                                      // 4: the page's line
        _chat.Enqueue(PresentGapsCall("p3", RevisedPlanMarkdown));                        // 5: /plan gaps resume: pass 2, nothing left
        _chat.EnqueueText("Canceled.");                                                   // 6
        var shadow = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Message.StartsWith(PlanText.ShadowLogPrefix, StringComparison.Ordinal)) shadow.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        string output;
        try
        {
            output = await RunAsync();
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Contains(PlanText.GapsPausedNotice(1), output);
        Assert.Equal(
            ["plan gaps shadow: pass 1: 1 low; shadow streak 1 of 1, would converge; real: you spoke",
             "plan gaps shadow: pass 2: no gaps; shadow streak 2 of 1, would converge; real: converged"],
            shadow);
        // The tag is the gap's own text: the user sees it as sent.
        Assert.Contains("[low] Add a test step", output);
    }

    [Fact]
    public async Task PlanGaps_ResumeAfterThePlanChanged_IsRefused()
    {
        PlanFixture([[Keys.Char('g'), Keys.Enter]], "/plan Add a guide", "/plan gaps resume", "/plan show", "/exit");
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            if (request == 2 && update == 0)
            {
                Assert.Equal(WebOutcome.Ok, Web.Send(new WebSendRequest { Text = "edit it by hand" }, null).Outcome);
            }

            if (request == 4 && update == 0)
            {
                File.WriteAllText(PlanFile, File.ReadAllText(PlanFile) + "\nEdited by hand.\n");
            }

            return Task.CompletedTask;
        };
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.EnqueueText("Go ahead.");

        string output = await RunAsync();

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Contains(PlanText.GapsPausedNotice(1), output);
        Assert.Contains(PlanText.PausedShowLine(1).Trim(), output);   // /plan show while paused
        Assert.Contains(PlanText.PausedPlanChangedError, output);
    }

    [Fact]
    public async Task CodeAudit_ATransientFailure_IsRetried_WithItsReadsForgotten()
    {
        AuditFixture(Line("/code-audit src security"), Key(Keys.Enter), Line("/exit"));
        _chat.Enqueue(ReadCall("r1"));          // 0: pass 1 reads the file
        _chat.EnqueueText("(never seen)");      // 1: then fails
        _chat.Enqueue(ReadCall("r2"));          // 2: the retry reads it again
        _chat.Enqueue(FindingCall("a1"));       // 3
        _chat.EnqueueText("Pass 1 done.");      // 4
        _chat.Enqueue(CleanCall("a2"));         // 5
        _chat.EnqueueText("Pass 2 done.");      // 6
        _chat.Enqueue(CleanCall("a3"));         // 7
        _chat.EnqueueText("Pass 3 done.");      // 8
        _chat.BeforeUpdateOf = (request, update, _) => request == 1 ? throw Refused() : Task.CompletedTask;

        string output = await RunAsync();

        string name = Path.GetFileNameWithoutExtension(AuditPath);
        Assert.Equal(9, _chat.Requests.Count);
        Assert.Contains(AuditText.RetryNotice(name, 1, TimeSpan.Zero), output);
        Assert.Equal(AuditText.PassMessage("src", AuditPath, 1, 5, PlanReviewer.Model, "security"), LastUserText(_chat.Requests[2]));
        Assert.DoesNotContain(_chat.Requests[2].SelectMany(m => m.Contents.OfType<FunctionResultContent>()), r => r.CallId == "r1");
        Assert.Contains("- Not read by any pass yet (1 file)", _chat.Requests[2][0].Text);   // the dropped turn's read forgotten
        Assert.Contains(AuditText.ConvergedNotice(name, 3, 2, 1, 1, pane: true), output);
    }
}
