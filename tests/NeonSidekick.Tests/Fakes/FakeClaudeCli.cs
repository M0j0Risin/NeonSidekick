using System.Runtime.CompilerServices;
using NeonSidekick.Claude;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A scripted <see cref="IClaudeCli"/> (2026-09-27): each run dequeues one script of events and yields them in order; a
/// script that is a <see cref="ClaudeStartException"/> throws it before any event. Records every request, so a test can
/// assert what <c>/claude</c> asked for (the session id, the resume, the permission level).
/// </summary>
public sealed class FakeClaudeCli : IClaudeCli
{
    private readonly Queue<object> _scripts = new();

    /// <summary>Every request, in call order.</summary>
    public List<ClaudeRequest> Requests { get; } = new();

    /// <summary>Called before each event is yielded, with its index; tests press keys and hold the run here.</summary>
    public Func<int, CancellationToken, Task>? BeforeEvent { get; set; }

    public FakeClaudeCli Enqueue(params ClaudeEvent[] events)
    {
        _scripts.Enqueue(events);
        return this;
    }

    /// <summary>A reply streamed in <paramref name="deltas"/>, then an ok result under <paramref name="sessionId"/>.</summary>
    public FakeClaudeCli EnqueueReply(string sessionId, params string[] deltas) =>
        Enqueue([.. deltas.Select(d => (ClaudeEvent)new ClaudeEvent.TextDelta(d)), Ok(sessionId, string.Concat(deltas))]);

    public FakeClaudeCli EnqueueStartFailure(string message)
    {
        _scripts.Enqueue(new ClaudeStartException(message));
        return this;
    }

    /// <summary>An ok result: 100 in, 5 out, two cents.</summary>
    public static ClaudeEvent.Result Ok(string sessionId, string text = "", IReadOnlyList<string>? denied = null) =>
        new(sessionId, 0.02m, new TokenUsage(100, 5, 105, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), false, null, text, denied ?? []);

    /// <summary>A failed result saying <paramref name="error"/>.</summary>
    public static ClaudeEvent.Result Failed(string? sessionId, string error) =>
        new(sessionId, 0m, default, true, error, "", []);

    public async IAsyncEnumerable<ClaudeEvent> RunAsync(ClaudeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var script = _scripts.Count > 0 ? _scripts.Dequeue() : throw new InvalidOperationException("No Claude script left.");
        if (script is ClaudeStartException failure)
        {
            throw failure;
        }

        var events = (ClaudeEvent[])script;
        for (int i = 0; i < events.Length; i++)
        {
            if (BeforeEvent is { } hook)
            {
                await hook(i, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return events[i];
        }
    }
}
