using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm;

/// <summary>
/// One request's clock (2026-10-26, the review of 6d71509d: <see cref="Assistant"/>'s turn loop, its summary request and its side request each
/// kept a copy of the stamps, and they drifted). Started as the request goes; told each streamed update (<see cref="Observe"/>); asked for
/// the request's usage at the end (<see cref="Stamp"/>).
/// <list type="bullet">
/// <item><see cref="First"/>: the first update with any content, a <see cref="StreamProgress.Marker"/> or a chunk of tool-call deltas — the
/// server took the request (the dropped-request check: a marker says so too, the review's R15, and so do a call's arguments, whose chunks
/// carry no content, 2026-10-29).</item>
/// <item>The decode runs from the first update that shows the model producing (<see cref="StreamProgress.Shows"/>) to the last one that
/// does, so the trailing usage chunk and a count the client estimates after the stream stay out of it (R34).</item>
/// <item>The live meter ticks for an update that carries something, never for a bare marker: it counts tokens, and a marker is none (R19).</item>
/// </list>
/// Stamps are the given <see cref="TimeProvider"/>'s, so a test can advance them.
/// </summary>
public sealed class StreamClock
{
    private readonly TimeProvider _time;
    private readonly StreamMeter? _meter;
    private readonly long _sent;
    private long? _decodeFrom;
    private long? _lastShown;
    private long? _ended;
    private bool _firstShownMarked;

    /// <summary>The request is sent now; <paramref name="meter"/> is the turn's live meter, null outside the turn loop.</summary>
    public StreamClock(TimeProvider time, StreamMeter? meter = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _meter = meter;
        _sent = _time.GetTimestamp();
    }

    /// <summary>When the server first answered with anything (a content, even <c>""</c>, a marker or a call's arguments); null while it hasn't.</summary>
    public long? First { get; private set; }

    /// <summary>How many updates showed the model producing: one alone is a burst, no decode to time.</summary>
    public int Shown { get; private set; }

    /// <summary>Whether the model's thinking was seen streaming: its text, a signature, or a marker (hidden thinking is marked).</summary>
    public bool SawReasoning { get; private set; }

    /// <summary>One streamed update.</summary>
    public void Observe(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        long now = _time.GetTimestamp();
        bool marker = StreamProgress.IsMarker(update);
        if (First is null && (update.Contents.Count > 0 || StreamProgress.Shows(update)))
        {
            First = now;
        }

        if (marker || update.Contents.Any(c => c is TextReasoningContent r && (!string.IsNullOrEmpty(r.Text) || !string.IsNullOrEmpty(r.ProtectedData))))
        {
            SawReasoning = true;
        }

        if (!StreamProgress.Shows(update))
        {
            return;
        }

        if (_decodeFrom is null)
        {
            _decodeFrom = now;
            _firstShownMarked = marker;
        }

        _lastShown = now;
        Shown++;
        if (!marker)
        {
            _meter?.Chunk();
        }
    }

    /// <summary>The stream ended (completed, cancelled or failed).</summary>
    public void End() => _ended ??= _time.GetTimestamp();

    /// <summary>
    /// <paramref name="counted"/> — the request's reports summed, spans zero — with this request's spans, judged
    /// (<see cref="TokenUsage.Judged"/>): the wait from the send to the first showing update, the decode from it to the last. A request
    /// whose whole output came in one unmarked update has no known wait (the burst's arrival holds its decode too): its wait is zeroed and
    /// counted in <see cref="TokenUsage.UnknownWaits"/> (R37). Reported reasoning with no thinking seen streaming is untimed (R28: a server
    /// that hides it). Empty stays empty.
    /// </summary>
    public TokenUsage Stamp(TokenUsage counted)
    {
        if (counted.IsEmpty)
        {
            return counted;
        }

        long ended = _ended ?? _time.GetTimestamp();
        long firstAt = _decodeFrom ?? ended;
        var stamped = counted with
        {
            ToFirstToken = _time.GetElapsedTime(_sent, firstAt),
            Generating = _decodeFrom is { } from ? _time.GetElapsedTime(from, _lastShown ?? from) : TimeSpan.Zero,
        };
        if (!SawReasoning && stamped.Reasoning is > 0 and var hidden)
        {
            stamped = stamped with { UntimedOutput = Math.Min(stamped.Output, stamped.UntimedOutput + hidden) };
        }

        stamped = stamped.Judged(Shown);
        if (Shown < 2 && !_firstShownMarked && counted.Output > 1)
        {
            stamped = stamped with { ToFirstToken = TimeSpan.Zero, UnknownWaits = 1 };
        }

        return stamped;
    }
}
