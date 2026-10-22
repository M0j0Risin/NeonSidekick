using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Git;
using NeonSidekick.Plans;
using NeonSidekick.Shell;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The Ralph judge's host for its own tests (2026-10-21, the plan at <c>~/.claude/plans/i-put-you-in-snoopy-perlis.md</c>: the judge was tested only
/// through the screen and headless): a working directory, no repository, the notices and log lines recorded, every check passed, the review a set
/// verdict, and the by-hand question answered from a queue (null once it runs dry: nobody to ask), the clock moved on by <see cref="AnswerTakes"/>
/// while it answers.
/// </summary>
internal sealed class FakeRalphHost(WorkingDirectory files, ManualTimeProvider time) : IRalphHost
{
    public WorkingDirectory Files => files;

    public GitAccess? Git => null;

    public List<string> Notices { get; } = [];

    public List<string> Logs { get; } = [];

    /// <summary>What the review says; null is a review stopped.</summary>
    public RalphVerdict? Verdict { get; set; } = new(RalphVerdictKind.Pass, "it looks done");

    public int Reviews { get; private set; }

    /// <summary>The by-hand question's answers in turn; past the last, null.</summary>
    public Queue<RalphCheckpointAnswer?> Answers { get; } = new();

    /// <summary>How long each answer takes on the clock.</summary>
    public TimeSpan AnswerTakes { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Run as the question is answered (a test cancels the loop's token here).</summary>
    public Action? OnAsk { get; set; }

    /// <summary>Each by-hand question asked: the step, the summary, the reviewer's reason and the detail.</summary>
    public List<(PlanStep Step, string? Summary, string? Reason, string Detail)> Asked { get; } = [];

    /// <summary>Whether anyone can be asked (2026-10-22, <see cref="IRalphHost.CanAsk"/>): false is headless, or the screen with no pane and no page.</summary>
    public bool CanAsk { get; set; } = true;

    /// <summary>The step total each question was given.</summary>
    public List<int> AskedTotals { get; } = [];

    public void Notice(string text) => Notices.Add(text);

    public void Tail(string tail)
    {
    }

    public void Log(string line) => Logs.Add(line);

    public Task<RalphCheckResult?> CheckAsync(string command, ShellKind shell, string label, CancellationToken cancellationToken) =>
        Task.FromResult<RalphCheckResult?>(new RalphCheckResult(RalphCheckKind.Passed, 0, "", TimeSpan.Zero));

    public Task<RalphVerdict?> ReviewAsync(RalphLoop loop, PlanStep step, FinishReport? report, RalphDelta delta, CancellationToken cancellationToken)
    {
        Reviews++;
        return Task.FromResult(Verdict);
    }

    public Task<RalphCheckpointAnswer?> AskByHandAsync(PlanStep step, int total, string? summary, string? reviewerReason, string detail, CancellationToken cancellationToken)
    {
        Asked.Add((step, summary, reviewerReason, detail));
        AskedTotals.Add(total);
        time.Advance(AnswerTakes);
        OnAsk?.Invoke();
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);
    }
}
