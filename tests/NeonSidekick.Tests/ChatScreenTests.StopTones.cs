using NeonSidekick.Audits;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Timers;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The gap review's and the code audit's stop tones (2026-10-21, the user's ask: the Ralph loop's stop tone for the other two loops left to
/// run): <c>silent</c> by default; a converged review rings once, as its pane opens, an audit at its end, a limit at the stop.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task PlanGaps_Converged_RingsTheStopToneOnce()
    {
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('a'), Keys.Enter]], "/plan Add a guide", "/exit");
        _settings.Update(d => d.PlanGapStopTone = TimerTones.Chime);
        _chat.Enqueue(PresentPlanCall());                                               // presented; Close gaps…
        _chat.EnqueueText("Closing the gaps.");
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));  // pass 1 closes one
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(PresentGapsCall("p3", RevisedPlanMarkdown));                      // pass 2 clean, 1 of 2
        _chat.EnqueueText("Pass 2 clean.");
        _chat.Enqueue(PresentGapsCall("p4", RevisedPlanMarkdown));                      // pass 3 confirms: the pane rings, then approved
        _chat.EnqueueText("Approved, starting.");
        _chat.EnqueueText("Done.");

        string output = await RunAsync();
        await UntilAsync(() => _tonePlayback.Writes.Count > 0, CancellationToken.None);

        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 3, 1, 2), output);
        // Rung by the pane's opening, never again as the review's end is said after the answer.
        Assert.Equal(Clip(TimerTones.Chime), Assert.Single(_tonePlayback.Writes));
    }

    [Fact]
    public async Task PlanGaps_AtTheLimit_RingsTheStopTone()
    {
        _settings.Update(d => { d.PlanGapPasses = 1; d.PlanGapStopTone = TimerTones.Bell; });
        PlanGapsFixture([[Keys.Char('g'), Keys.Enter]], Line("/plan Add a guide"), Key(Keys.Escape), Line("/exit"));
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));
        _chat.EnqueueText("Pass 1 done.");

        string output = await RunAsync();
        await UntilAsync(() => _tonePlayback.Writes.Count > 0, CancellationToken.None);

        Assert.Contains(PlanText.GapStopNotice(PlanGapStop.Limit, 1, 1), output);
        Assert.Equal(Clip(TimerTones.Bell), Assert.Single(_tonePlayback.Writes));
    }

    [Fact]
    public async Task PlanGaps_TheDefault_RingsNothing()
    {
        _settings.Update(d => d.PlanGapPasses = 1);
        PlanGapsFixture([[Keys.Char('g'), Keys.Enter]], Line("/plan Add a guide"), Key(Keys.Escape), Line("/exit"));
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));
        _chat.EnqueueText("Pass 1 done.");

        string output = await RunAsync();

        Assert.Contains(PlanText.GapStopNotice(PlanGapStop.Limit, 1, 1), output);
        Assert.Equal(TimerTones.Silent, _settings.Current.PlanGapStopTone);
        Assert.Empty(_tonePlayback.Writes);
    }

    [Fact]
    public async Task CodeAudit_Converged_RingsTheStopToneOnce()
    {
        AuditFixture(Line("/code-audit src security"), Key(Keys.Enter), Line("/exit"));
        _settings.Update(d => d.CodeAuditStopTone = TimerTones.Beep);
        ThreePasses();

        string output = await RunAsync();
        await UntilAsync(() => _tonePlayback.Writes.Count > 0, CancellationToken.None);

        Assert.Contains(AuditText.ConvergedNotice(Path.GetFileNameWithoutExtension(AuditPath), 3, 2, 1, 1), output);
        Assert.Equal(Clip(TimerTones.Beep), Assert.Single(_tonePlayback.Writes));
    }

    [Fact]
    public async Task CodeAudit_TheDefault_RingsNothing()
    {
        AuditFixture(Line("/code-audit src security"), Key(Keys.Enter), Line("/exit"));
        ThreePasses();

        string output = await RunAsync();

        Assert.Contains(AuditText.ConvergedNotice(Path.GetFileNameWithoutExtension(AuditPath), 3, 2, 1, 1), output);
        Assert.Equal(TimerTones.Silent, _settings.Current.CodeAuditStopTone);
        Assert.Empty(_tonePlayback.Writes);
    }
}
