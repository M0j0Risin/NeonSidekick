using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm;

/// <summary>
/// One connection's count of the requests in flight to the main embedded server (2026-11-16, plan lucid-roaming-lantern #44): one for the
/// session, shared by every <see cref="UseTracker"/> over it (the main client and each link to the main host's server), so two bot links in
/// flight at once hold it until both end. <see cref="Retire"/> at the disconnect: a request that the disconnect cancelled, ending after it,
/// arms nothing against the next connection. Thread-safe.
/// </summary>
internal sealed class UseCount(Action? began = null, Action? ended = null)
{
    private int _inFlight;
    private volatile bool _retired;

    /// <summary>The requests in flight now.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>The connection is gone: ends from now on call nothing.</summary>
    public void Retire() => _retired = true;

    /// <summary>A request starts.</summary>
    public void Begin()
    {
        Interlocked.Increment(ref _inFlight);
        if (!_retired)
        {
            began?.Invoke();
        }
    }

    /// <summary>A request ended, however it ended; the last one out calls the ended action unless retired.</summary>
    public void End()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && !_retired)
        {
            ended?.Invoke();
        }
    }
}

/// <summary>
/// A client that counts its requests on a <see cref="UseCount"/> (2026-11-16, #44): a plain request from the call until its task ends; a
/// streaming one from its first read until its enumerator is disposed, so a stream cancelled or left half-read ends too. The updates pass
/// through as the inner client's own objects, never copies: <see cref="StreamProgress"/>'s markers are told apart by reference.
/// </summary>
internal sealed class UseTracker(IChatClient inner, UseCount count) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        count.Begin();
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            count.End();
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        count.Begin();
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            count.End();
        }
    }
}
