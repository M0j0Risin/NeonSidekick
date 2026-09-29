using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using Xunit;

namespace NeonSidekick.Tests;

/// <summary>The jobs behind the input line (2026-09-29): the grace, the slot, the drain, the supersede, the strip.</summary>
public sealed class BackgroundJobsTests
{
    private readonly ManualTimeProvider _time = new();
    private int _signals;

    private BackgroundJobs Jobs() => new(() => Interlocked.Increment(ref _signals), _time);

    /// <summary>A task that ends when the test says, and whose phase it can drive.</summary>
    private sealed class Held
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<string>? Phase { get; private set; }

        public Task Work(Action<string> phase, CancellationToken token)
        {
            Phase = phase;
            return Done.Task.WaitAsync(token);
        }
    }

    /// <summary>Starts <paramref name="work"/> and moves the clock past the grace, so it goes behind the line.</summary>
    private async Task<bool> StartBehindAsync(BackgroundJobs jobs, BackgroundJobKind kind, Func<Action<string>, CancellationToken, Task> work, Func<JobOutcome, Task> onDone)
    {
        var start = jobs.StartAsync(kind, work, onDone, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => { _time.Advance(TimeSpan.FromMilliseconds(100)); return start.IsCompleted; }, TimeSpan.FromSeconds(5)));
        return await start;
    }

    [Fact]
    public async Task AJobDoneWithinTheGrace_EndsInLine()
    {
        var jobs = Jobs();
        JobOutcome? seen = null;

        bool behind = await jobs.StartAsync(BackgroundJobKind.Speech, (_, _) => Task.CompletedTask, o => { seen = o; return Task.CompletedTask; }, CancellationToken.None);

        Assert.False(behind);
        Assert.Equal(new JobOutcome(JobEnd.Ok), seen);
        Assert.False(jobs.Running(BackgroundJobKind.Speech));
        Assert.False(jobs.HasCompletions);
        Assert.Equal(0, _signals);
    }

    [Fact]
    public async Task AJobPastTheGrace_RunsBehind_ItsEndWaitsForTheDrain_AndNudgesTheRead()
    {
        var jobs = Jobs();
        var held = new Held();
        JobOutcome? seen = null;

        Assert.True(await StartBehindAsync(jobs, BackgroundJobKind.EmbeddedDownload, held.Work, o => { seen = o; return Task.CompletedTask; }));
        Assert.True(jobs.Running(BackgroundJobKind.EmbeddedDownload));
        held.Phase!("downloading Gemma 4 E2B (4.2 GB)… 42%");
        Assert.Equal(BackgroundJobText.DownloadGlyph + " 42%", jobs.Strip());

        held.Done.SetResult();
        Assert.True(SpinWait.SpinUntil(() => jobs.HasCompletions, TimeSpan.FromSeconds(5)));
        Assert.Null(seen);   // not before the drain
        Assert.Equal(1, _signals);
        Assert.False(jobs.Running(BackgroundJobKind.EmbeddedDownload));

        await jobs.DrainAsync();

        Assert.Equal(new JobOutcome(JobEnd.Ok), seen);
        Assert.Equal("", jobs.Strip());
    }

    [Fact]
    public async Task Cancel_DeliversCancelled_AFailure_DeliversTheException()
    {
        var jobs = Jobs();
        var held = new Held();
        var ends = new List<JobOutcome>();
        await StartBehindAsync(jobs, BackgroundJobKind.Mcp, held.Work, o => { ends.Add(o); return Task.CompletedTask; });
        var failing = new Held();
        await StartBehindAsync(jobs, BackgroundJobKind.Voice, failing.Work, o => { ends.Add(o); return Task.CompletedTask; });

        Assert.True(jobs.Cancel(BackgroundJobKind.Mcp));
        failing.Done.SetException(new InvalidOperationException("no model"));
        Assert.True(SpinWait.SpinUntil(() => !jobs.Running(BackgroundJobKind.Mcp) && !jobs.Running(BackgroundJobKind.Voice), TimeSpan.FromSeconds(5)));
        await jobs.DrainAsync();

        Assert.Contains(new JobOutcome(JobEnd.Cancelled), ends);
        var failed = Assert.Single(ends, e => e.End == JobEnd.Failed);
        Assert.Equal("no model", failed.Error!.Message);
        Assert.False(jobs.Cancel(BackgroundJobKind.Mcp));   // nothing left to cancel
    }

    [Fact]
    public async Task ASecondStart_SupersedesTheFirst_WhoseEndIsDropped()
    {
        var jobs = Jobs();
        var first = new Held();
        var second = new Held();
        var ends = new List<string>();
        await StartBehindAsync(jobs, BackgroundJobKind.Voice, first.Work, _ => { ends.Add("first"); return Task.CompletedTask; });

        await StartBehindAsync(jobs, BackgroundJobKind.Voice, second.Work, _ => { ends.Add("second"); return Task.CompletedTask; });

        Assert.True(first.Done.Task.IsCompleted is false);   // cancelled through its token, not finished
        second.Done.SetResult();
        Assert.True(SpinWait.SpinUntil(() => jobs.HasCompletions, TimeSpan.FromSeconds(5)));
        await jobs.DrainAsync();
        Assert.Equal(["second"], ends);
    }

    [Fact]
    public async Task CancelAll_AwaitsEveryJob_AndDropsTheirEnds()
    {
        var jobs = Jobs();
        var a = new Held();
        var b = new Held();
        var ends = 0;
        await StartBehindAsync(jobs, BackgroundJobKind.Mcp, a.Work, _ => { ends++; return Task.CompletedTask; });
        await StartBehindAsync(jobs, BackgroundJobKind.Speech, b.Work, _ => { ends++; return Task.CompletedTask; });

        await jobs.CancelAllAsync();

        Assert.False(jobs.Running(BackgroundJobKind.Mcp));
        Assert.False(jobs.Running(BackgroundJobKind.Speech));
        await Task.Delay(50);
        await jobs.DrainAsync();
        Assert.Equal(0, ends);
    }

    [Fact]
    public void Progress_AndGlyphs_ArePinned()
    {
        Assert.Equal("42%", BackgroundJobs.Progress("downloading Gemma 4 E2B (4.2 GB)… 42%"));
        Assert.Equal("1/3", BackgroundJobs.Progress("connecting MCP servers (1 of 3)"));
        Assert.Null(BackgroundJobs.Progress("verifying Gemma 4 E2B…"));
        Assert.Null(BackgroundJobs.Progress(null));
        Assert.Equal(("📥", "🔌", "🎧", "🔈"), (BackgroundJobText.Glyph(BackgroundJobKind.EmbeddedDownload), BackgroundJobText.Glyph(BackgroundJobKind.Mcp), BackgroundJobText.Glyph(BackgroundJobKind.Voice), BackgroundJobText.Glyph(BackgroundJobKind.Speech)));
        Assert.Equal(BackgroundJobKind.Speech, BackgroundJobs.KindOfGlyph("🔈"));
        Assert.Null(BackgroundJobs.KindOfGlyph(ChatScreen.TtsGlyph));   // the switch's loud speaker is not the job's
        Assert.Equal("voice input is still setting up (🎧 42%)", BackgroundJobText.VoiceSettingUp("42%"));
        Assert.Equal("voice input is still setting up (🎧)", BackgroundJobText.VoiceSettingUp(null));
        Assert.Equal("🧠 📥 42% 🔊", ChatScreen.StripGlyphs(true, false, "", true, false, false, false, "📥 42%"));
    }
}
