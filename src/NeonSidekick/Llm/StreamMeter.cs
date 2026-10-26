namespace NeonSidekick.Llm;

/// <summary>
/// The request streaming now, counted as it streams (2026-09-25, for <c>LLM mid-turn usage</c>'s <c>estimate</c>): the server
/// reports usage only in a request's last chunk, so the busy row's live figures come from here — every chunk with content
/// (text, reasoning, a tool call's delta: <see cref="StreamProgress.Shows"/>) counted as one token, which is what llama.cpp,
/// LM Studio and vLLM stream, and the time since the first of them. <see cref="Assistant"/> writes it on the turn's task (<see cref="Begin"/>, <see cref="Chunk"/>,
/// <see cref="End"/>); the pane's timer thread reads it (<see cref="Read"/>), hence the lock. Timestamps are the caller's
/// <see cref="TimeProvider"/>'s, so a test can advance them.
/// </summary>
public sealed class StreamMeter
{
    /// <summary>What <see cref="Read"/> answers: whether a request streams, its content chunks so far, and their rate (null before <see cref="MinRateSpan"/> has passed since the first chunk, or past <see cref="TokenUsage.MaxPlausibleTokensPerSecond"/>).</summary>
    public readonly record struct Reading(bool Streaming, long Chunks, double? ChunksPerSecond)
    {
        /// <summary>No request streaming.</summary>
        public static readonly Reading Idle = default;
    }

    /// <summary>
    /// How long after the first chunk the rate is first given (2026-10-26, the user's report of absurd speeds: a request's first
    /// chunks often come together, and their count over a few milliseconds read in the tens of thousands).
    /// </summary>
    public static readonly TimeSpan MinRateSpan = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private bool _streaming;
    private long _chunks;
    private long? _first;

    public StreamMeter(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A request was sent: the count starts over.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            _streaming = true;
            _chunks = 0;
            _first = null;
        }
    }

    /// <summary>One chunk with content arrived; the first stamps the start of the rate's span.</summary>
    public void Chunk()
    {
        long now = _time.GetTimestamp();
        lock (_gate)
        {
            _chunks++;
            _first ??= now;
        }
    }

    /// <summary>The request's stream ended — completed, cancelled or failed: nothing streams until the next <see cref="Begin"/>.</summary>
    public void End()
    {
        lock (_gate)
        {
            _streaming = false;
            _chunks = 0;
            _first = null;
        }
    }

    /// <summary>The meter now.</summary>
    public Reading Read()
    {
        long now = _time.GetTimestamp();
        lock (_gate)
        {
            if (!_streaming)
            {
                return Reading.Idle;
            }

            double? rate = _first is { } first && _time.GetElapsedTime(first, now) is var span && span >= MinRateSpan ? _chunks / span.TotalSeconds : null;
            return new Reading(true, _chunks, rate is > TokenUsage.MaxPlausibleTokensPerSecond ? null : rate);
        }
    }
}
