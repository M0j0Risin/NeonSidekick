using System.Collections.Concurrent;
using System.Net;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The Chat tab's voice seam (<see cref="IWebVoice"/>, 2026-11-13) for the host and page tests: records every start, chunk (in arrival order),
/// end, 🔊, progress report and clip GET, answers each with <see cref="Reply"/> (or a one-shot <see cref="Once"/>), and serves the clips a test
/// hands it.
/// </summary>
internal sealed class FakeWebVoice : IWebVoice
{
    public ConcurrentQueue<(string? Page, bool? Speak)> Seen { get; } = new();

    public ConcurrentQueue<WebVoiceStart> Starts { get; } = new();

    public ConcurrentQueue<WebVoiceChunk> Chunks { get; } = new();

    public ConcurrentQueue<WebVoiceEnd> Ends { get; } = new();

    public ConcurrentQueue<WebVoiceSpeak> Speaks { get; } = new();

    public ConcurrentQueue<WebVoicePlayed> Played { get; } = new();

    public ConcurrentQueue<(string Page, string Turn, long N)> ClipGets { get; } = new();

    /// <summary>The answer to every post.</summary>
    public WebReply Reply { get; set; } = new(WebOutcome.Ok, Listen: "listening");

    /// <summary>An answer for the next post alone (e.g. refuse once with TooMany), then <see cref="Reply"/> again.</summary>
    public WebReply? Once { get; set; }

    /// <summary>The clips served, by page, turn and number.</summary>
    public ConcurrentDictionary<(string Page, string Turn, long N), byte[]> Clips { get; } = new();

    public void Saw(string? page, bool? speak) => Seen.Enqueue((page, speak));

    public WebReply Start(WebVoiceStart request, IPAddress? remote)
    {
        Starts.Enqueue(request);
        return Answer();
    }

    public WebReply Chunk(WebVoiceChunk request, IPAddress? remote)
    {
        Chunks.Enqueue(request);
        return Answer();
    }

    public WebReply End(WebVoiceEnd request, IPAddress? remote)
    {
        Ends.Enqueue(request);
        return Answer();
    }

    public WebReply Speak(WebVoiceSpeak request, IPAddress? remote)
    {
        Speaks.Enqueue(request);
        return Answer();
    }

    WebReply IWebVoice.Played(WebVoicePlayed request, IPAddress? remote)
    {
        Played.Enqueue(request);
        return Answer();
    }

    public byte[]? Clip(string page, string turn, long n)
    {
        ClipGets.Enqueue((page, turn, n));
        return Clips.TryGetValue((page, turn, n), out var clip) ? clip : null;
    }

    private WebReply Answer()
    {
        if (Once is { } once)
        {
            Once = null;
            return once;
        }

        return Reply;
    }
}
