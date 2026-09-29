using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NeonSidekick.App;

/// <summary>The long connects that run behind the input line (2026-09-29): one slot each.</summary>
public enum BackgroundJobKind
{
    /// <summary>An embedded model's download (and the llama.cpp runtime it needs).</summary>
    EmbeddedDownload,

    /// <summary>The MCP servers' connect wave.</summary>
    Mcp,

    /// <summary>Voice input's setup: the Whisper, Silero and Vosk downloads and loads.</summary>
    Voice,

    /// <summary>Speech output's setup: the TTS server's probe, or Kokoro's download and load.</summary>
    Speech,
}

/// <summary>How a background job ended.</summary>
public enum JobEnd
{
    Ok,
    Cancelled,
    Failed,
}

/// <summary>A background job's end, with the exception it failed with.</summary>
public readonly record struct JobOutcome(JobEnd End, Exception? Error = null);

/// <summary>
/// The long connects the screen runs behind the input line (2026-09-29, the user's ask: nothing on the line worked while an
/// embedded model downloaded — the download ran inside <c>/server</c> under a watcher that honoured Ctrl+C alone and held
/// every other key; the MCP wave and the voice and speech setups, whose first run downloads models, did the same). One slot
/// per <see cref="BackgroundJobKind"/>, the reflection job's shape (<c>ChatScreen.StartLearn</c>):
///
/// <para><see cref="StartAsync"/> runs on the idle loop. A job of the same kind still running is cancelled and awaited first,
/// and its end dropped — so a session never connects twice at once (voice's <c>Unload</c> would dispose a recognizer the other
/// is loading). The work is called on the loop's own thread, not <c>Task.Run</c>, so each session's synchronous prefix (its
/// unload, its flags) runs where it always did, with the microphone disarmed.</para>
///
/// <para>A job that ends within <see cref="Grace"/> of its start never goes behind the line: its end runs in line, before
/// <see cref="StartAsync"/> returns, as the connect's report did before — a TTS server's probe, a quick MCP wave, a voice
/// setup whose models are on disk. Only a longer one leaves the line to the user. The grace runs on the screen's clock, so a
/// test's manual clock keeps every job in line unless the test moves it.</para>
///
/// <para>The work's <c>phase</c> labels are kept (<see cref="Progress"/> reads the percentage or count off them) for the hint
/// row's strip (<see cref="Strip"/>), read per draw and on the tick from any thread. The end is queued from the pool and the
/// screen's alert signal nudges the idle read; <see cref="DrainAsync"/> runs the queued ends on the loop, at its top — never
/// under a reply, so an embedded model's connect never replaces the client a turn streams from. Nothing here writes.</para>
/// </summary>
public sealed partial class BackgroundJobs
{
    private static readonly BackgroundJobKind[] Kinds = Enum.GetValues<BackgroundJobKind>();

    private readonly Job?[] _slots = new Job?[Kinds.Length];
    private readonly ConcurrentQueue<(Func<JobOutcome, Task> OnDone, JobOutcome Outcome)> _done = new();
    private readonly Action _signal;
    private readonly TimeProvider _time;

    /// <summary>How long <see cref="StartAsync"/> waits for a job before leaving it behind the line: half a second, short enough not to feel held.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(500);

    /// <param name="signal">Ends the idle read so the loop top drains (the screen's <c>SignalAlert</c>); called from the pool.</param>
    /// <param name="time">The screen's clock, for <see cref="Grace"/>.</param>
    public BackgroundJobs(Action signal, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(time);
        _signal = signal;
        _time = time;
    }

    /// <summary>
    /// Starts <paramref name="work"/> as <paramref name="kind"/>'s job, superseding one still running (cancelled, awaited, its
    /// end dropped), and waits up to <see cref="Grace"/> for it: ended by then, <paramref name="onDone"/> runs here; else it runs
    /// on the loop at a later <see cref="DrainAsync"/>. The token handed to the work is linked to <paramref name="appToken"/>;
    /// an end the app token caused is dropped. True when the job is left running behind the line.
    /// </summary>
    public async Task<bool> StartAsync(BackgroundJobKind kind, Func<Action<string>, CancellationToken, Task> work, Func<JobOutcome, Task> onDone, CancellationToken appToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(onDone);
        await StopAsync(kind).ConfigureAwait(false);
        var job = new Job(kind, CancellationTokenSource.CreateLinkedTokenSource(appToken), onDone, appToken);
        Volatile.Write(ref _slots[(int)kind], job);
        Task task;
        try
        {
            task = work(job.SetLabel, job.Cts.Token);
        }
        catch (Exception e)
        {
            task = Task.FromException(e);
        }

        job.Task = task;
        if (!task.IsCompleted)
        {
            using var grace = new CancellationTokenSource();
            await Task.WhenAny(task, Task.Delay(Grace, _time, grace.Token)).ConfigureAwait(false);
            grace.Cancel();
        }

        if (task.IsCompleted)
        {
            // In line: no continuation was attached, so nothing else sees this end.
            Interlocked.CompareExchange(ref _slots[(int)kind], null, job);
            var outcome = Outcome(job, task);
            job.Cts.Dispose();
            if (!appToken.IsCancellationRequested)
            {
                await onDone(outcome).ConfigureAwait(false);
            }

            return false;
        }

        // Attached only now, so an end during the grace was the branch above's alone.
        _ = task.ContinueWith(static (t, state) => ((Job)state!).Owner!.Complete((Job)state!, t), job.WithOwner(this), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return true;
    }

    /// <summary>Whether <paramref name="kind"/>'s job is running. Any thread.</summary>
    public bool Running(BackgroundJobKind kind) => Volatile.Read(ref _slots[(int)kind]) is not null;

    /// <summary>The last label <paramref name="kind"/>'s job reported, or null. Any thread.</summary>
    public string? Label(BackgroundJobKind kind) => Volatile.Read(ref _slots[(int)kind])?.Label;

    /// <summary>
    /// Cancels <paramref name="kind"/>'s job, if one runs (the strip's double-click, a profile switch); its end is still
    /// delivered — <see cref="JobEnd.Cancelled"/> — so its owner says so. True when one was running.
    /// </summary>
    public bool Cancel(BackgroundJobKind kind)
    {
        if (Volatile.Read(ref _slots[(int)kind]) is not { } job)
        {
            return false;
        }

        VoiceSession.SafeCancel(job.Cts);
        return true;
    }

    /// <summary>Whether an end waits for <see cref="DrainAsync"/>: the idle read's arm-time check, so one queued before the read armed is not lost.</summary>
    public bool HasCompletions => !_done.IsEmpty;

    /// <summary>Runs the queued ends on the caller's thread (the loop), oldest first.</summary>
    public async Task DrainAsync()
    {
        while (_done.TryDequeue(out var done))
        {
            await done.OnDone(done.Outcome).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels and awaits every job, their ends dropped (the screen's exit): nothing is left touching a session being disposed.</summary>
    public async Task CancelAllAsync()
    {
        foreach (var kind in Kinds)
        {
            await StopAsync(kind).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The hint row's part (2026-09-29): each running job's glyph (<see cref="BackgroundJobText.Glyph"/>), with its
    /// <see cref="Progress"/> after a space when the label carries one — <c>📥 42% 🔌 1/3</c>; empty with none. Any thread. Pinned.
    /// </summary>
    public string Strip()
    {
        var parts = new List<string>(Kinds.Length);
        foreach (var kind in Kinds)
        {
            if (Volatile.Read(ref _slots[(int)kind]) is { } job)
            {
                string glyph = BackgroundJobText.Glyph(kind);
                parts.Add(Progress(job.Label) is { } progress ? glyph + " " + progress : glyph);
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>The job whose strip glyph is <paramref name="glyph"/>, or null: the strip's double-click. Pinned.</summary>
    public static BackgroundJobKind? KindOfGlyph(string glyph)
    {
        foreach (var kind in Kinds)
        {
            if (string.Equals(BackgroundJobText.Glyph(kind), glyph, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>
    /// What a label says of the progress: its trailing percentage (a download's <c>downloading X (6.1 GB)… 42%</c>), or an
    /// MCP wave's count (<c>connecting MCP servers (1 of 3)</c> reads <c>1/3</c>); null for anything else. Pinned.
    /// </summary>
    public static string? Progress(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return null;
        }

        var percent = PercentPattern().Match(label);
        if (percent.Success)
        {
            return percent.Groups[1].Value + "%";
        }

        var count = CountPattern().Match(label);
        return count.Success
            ? int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "/" + int.Parse(count.Groups[2].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : null;
    }

    [GeneratedRegex(@"(\d{1,3})%\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PercentPattern();

    [GeneratedRegex(@"\((\d+) of (\d+)\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CountPattern();

    /// <summary>Cancels and awaits <paramref name="kind"/>'s job with its end dropped: a job superseded, or the screen's exit, a switch turned off.</summary>
    public async Task StopAsync(BackgroundJobKind kind)
    {
        if (Volatile.Read(ref _slots[(int)kind]) is not { } job)
        {
            return;
        }

        job.Dropped = true;
        VoiceSession.SafeCancel(job.Cts);
        try
        {
            if (job.Task is { } task)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Its end is dropped: a cancel or a failure of work nobody waits for any more.
        }

        Interlocked.CompareExchange(ref _slots[(int)kind], null, job);
    }

    /// <summary>The job's task ended (on the pool): its slot freed, its end queued and the read nudged — unless it was dropped.</summary>
    private void Complete(Job job, Task task)
    {
        Interlocked.CompareExchange(ref _slots[(int)job.Kind], null, job);
        if (job.Dropped || job.AppToken.IsCancellationRequested)
        {
            job.Cts.Dispose();
            return;
        }

        var outcome = Outcome(job, task);
        job.Cts.Dispose();
        _done.Enqueue((job.OnDone, outcome));
        _signal();
    }

    /// <summary>
    /// How <paramref name="task"/> ended. A session may swallow the cancel into a result (the LLM probes do; speech sets
    /// "cancelled" and rethrows), so the token is read, not only the task's state.
    /// </summary>
    private static JobOutcome Outcome(Job job, Task task) =>
        job.Cts.IsCancellationRequested || task.IsCanceled
            ? new JobOutcome(JobEnd.Cancelled)
            : task.IsFaulted
                ? new JobOutcome(JobEnd.Failed, task.Exception!.InnerExceptions.Count == 1 ? task.Exception.InnerException : task.Exception)
                : new JobOutcome(JobEnd.Ok);

    private sealed class Job(BackgroundJobKind kind, CancellationTokenSource cts, Func<JobOutcome, Task> onDone, CancellationToken appToken)
    {
        private string? _label;

        public BackgroundJobKind Kind { get; } = kind;

        public CancellationTokenSource Cts { get; } = cts;

        public Func<JobOutcome, Task> OnDone { get; } = onDone;

        public CancellationToken AppToken { get; } = appToken;

        public Task? Task { get; set; }

        public BackgroundJobs? Owner { get; private set; }

        private volatile bool _dropped;

        public bool Dropped
        {
            get => _dropped;
            set => _dropped = value;
        }

        public string? Label => Volatile.Read(ref _label);

        public void SetLabel(string label) => Volatile.Write(ref _label, label);

        public Job WithOwner(BackgroundJobs owner)
        {
            Owner = owner;
            return this;
        }
    }
}
