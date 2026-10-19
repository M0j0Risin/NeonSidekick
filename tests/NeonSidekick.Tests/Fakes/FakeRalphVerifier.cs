using NeonSidekick.Plans;
using NeonSidekick.Shell;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A scripted check runner for the Ralph loop's tests (2026-10-18): each run takes the next queued result (a pass when none is queued), the
/// commands it was asked to run kept in order. <see cref="Hold"/> makes a run wait until its token is cancelled (Esc, the page's Stop).
/// </summary>
public sealed class FakeRalphVerifier : IRalphVerifier
{
    private readonly Queue<RalphCheckResult> _results = new();

    /// <summary>The commands run, in order.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>Each run's timeout, in order (2026-10-19: headless read the live setting, not the loop's snapshot).</summary>
    public List<TimeSpan> Timeouts { get; } = [];

    /// <summary>A run waits for its token (the next one only), as a long build would.</summary>
    public bool Hold { get; set; }

    /// <summary>The run (1 for the first) that waits for its token as <see cref="Hold"/> does; 0 for none.</summary>
    public int HoldRun { get; set; }

    /// <summary>Raised as a held run begins to wait, so a test can press Esc or Stop then.</summary>
    public Action? Holding { get; set; }

    /// <summary>Raised as each run ends, with its number (1 for the first): what a test changes between checks (2026-10-19).</summary>
    public Action<int>? Ran { get; set; }

    public FakeRalphVerifier Pass(string tail = "ok") => Queue(new RalphCheckResult(RalphCheckKind.Passed, 0, tail, TimeSpan.FromSeconds(1)));

    public FakeRalphVerifier Fail(string tail = "error: it broke", int exit = 1) => Queue(new RalphCheckResult(RalphCheckKind.Failed, exit, tail, TimeSpan.FromSeconds(1)));

    public FakeRalphVerifier Queue(RalphCheckResult result)
    {
        lock (_results)
        {
            _results.Enqueue(result);
        }

        return this;
    }

    public async Task<RalphCheckResult> RunAsync(string command, ShellKind shell, string workdir, TimeSpan timeout, CancellationToken cancellationToken)
    {
        int run;
        lock (_results)
        {
            Commands.Add(command);
            Timeouts.Add(timeout);
            run = Commands.Count;
        }

        if (Hold || run == HoldRun)
        {
            Hold = false;
            Holding?.Invoke();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new RalphCheckResult(RalphCheckKind.Cancelled, null, "", TimeSpan.Zero);
            }
        }

        RalphCheckResult ended;
        lock (_results)
        {
            ended = _results.TryDequeue(out var result) ? result : new RalphCheckResult(RalphCheckKind.Passed, 0, "ok", TimeSpan.FromSeconds(1));
        }

        Ran?.Invoke(run);
        return ended;
    }
}
