using System.Text;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace NeonSidekick.Llm;

/// <summary>
/// One request's tool calls as they stream (2026-10-29, session 832: a <c>present_audit</c> retry streamed 60,259 tokens of arguments
/// for seven minutes while the screen said "thinking" and the log "0 chars received"). Fed every update; keyed by the delta's
/// <see cref="StreamingChatToolCallUpdate.Index"/>, it keeps the name from the chunk that carries it and adds up the argument characters.
/// It answers two things: what the busy row shows (<see cref="Observe"/>'s return: the call being written, named or past
/// <see cref="CountShownFrom"/>, at most once a <see cref="ProgressInterval"/> after the request's first) and the first call whose
/// streamed arguments passed its tool's own ceiling (<see cref="Over"/>), which ends the turn.
/// <para>Only as live as the server's stream (measured that day with Qwen models, the same prompts): llama.cpp b11258 streams every
/// parameter in pieces; vLLM's <c>qwen3_coder</c> parser streams text parameters and holds a non-text one (an array, an object) until it
/// closes; SGLang's <c>qwen3_coder</c> holds every parameter until it closes. All three send the name first, so the label always
/// names the call. A held parameter arrives as one piece over <see cref="WholePiece"/>: written already, so it counts toward the size
/// shown but never toward the ceiling, which only the pieces of a live stream reach (the review of e7f741e8: a held array once switched
/// the guard off for the text streaming after it, and small closing pieces stopped a call already whole). The app's own Anthropic,
/// Responses and Claude Code streams read their deltas but raise one marker per block (<see cref="StreamProgress"/>), so the watch sees
/// nothing there. No timer runs between updates: the size shown trails the stream by at most an interval, and the
/// <see cref="TurnEvent.ToolCall"/> replaces it once the call is whole.</para>
/// </summary>
public sealed class ToolArgumentWatch
{
    /// <summary>From how many characters the busy row shows a call's size, and an unnamed call at all.</summary>
    public const int CountShownFrom = 1_000;

    /// <summary>
    /// A piece of arguments longer than this is a parameter the server held back and sent whole (SGLang, vLLM's arrays), not the
    /// tokens of a live stream.
    /// </summary>
    public const int WholePiece = 1_000;

    /// <summary>The shortest gap between two labels after a request's first: the call being written changes the busy row at most once a second.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    /// <summary>The name a call goes by on the busy row while no chunk has named it. Pinned.</summary>
    public const string UnnamedCall = "(unnamed)";

    /// <summary>The first call whose streamed arguments passed its tool's ceiling: what the turn ends on.</summary>
    public sealed record Runaway(string Name, long Chars, int Ceiling);

    private sealed class Call
    {
        public string? Name { get; set; }

        public long Chars { get; set; }

        /// <summary>The characters that came in a live stream's pieces (none over <see cref="WholePiece"/>): what the ceiling reads.</summary>
        public long Streamed { get; set; }

        public int? Ceiling { get; set; }
    }

    private readonly Func<string, int?> _ceiling;
    private readonly TimeProvider _time;
    private readonly Dictionary<int, Call> _calls = [];
    private TurnEvent.ToolWriting? _shown;
    private long? _lastShown;

    /// <param name="ceiling">A tool's ceiling by its name (<see cref="Tools.IArgumentCeiling"/>), null for none; read as a call is named.</param>
    /// <param name="time">The clock <see cref="ProgressInterval"/> is kept on.</param>
    public ToolArgumentWatch(Func<string, int?> ceiling, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(ceiling);
        ArgumentNullException.ThrowIfNull(time);
        _ceiling = ceiling;
        _time = time;
    }

    /// <summary>The first call past its ceiling, or null.</summary>
    public Runaway? Over { get; private set; }

    /// <summary>The call written last and its own characters, shown or not, or null before any; for the request's log lines.</summary>
    public TurnEvent.ToolWriting? Latest { get; private set; }

    /// <summary>Every call's arguments' characters so far, for the turn's closing log line.</summary>
    public long Chars { get; private set; }

    /// <summary>
    /// Reads <paramref name="update"/>'s tool-call deltas, if it carries the SDK's chunk (anything else changes nothing), and returns what
    /// the busy row should show now, or null: the call of the update's last showable delta, when it differs from the last shown and
    /// either nothing was shown yet or <see cref="ProgressInterval"/> has passed.
    /// </summary>
    public TurnEvent.ToolWriting? Observe(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.RawRepresentation is not StreamingChatCompletionUpdate { ToolCallUpdates: { Count: > 0 } deltas })
        {
            return null;
        }

        TurnEvent.ToolWriting? candidate = null;
        foreach (var delta in deltas)
        {
            if (!_calls.TryGetValue(delta.Index, out var call))
            {
                call = new Call();
                _calls.Add(delta.Index, call);
            }

            if (call.Name is null && !string.IsNullOrEmpty(delta.FunctionName))
            {
                call.Name = delta.FunctionName;
                call.Ceiling = _ceiling(call.Name);
            }

            int piece = delta.FunctionArgumentsUpdate is { } arguments ? Encoding.UTF8.GetCharCount(arguments.ToMemory().Span) : 0;
            call.Chars += piece;
            Chars += piece;
            if (piece <= WholePiece)
            {
                call.Streamed += piece;
            }

            Latest = new TurnEvent.ToolWriting(call.Name ?? UnnamedCall, call.Chars);
            if (call.Name is not null || call.Chars >= CountShownFrom)
            {
                candidate = Latest;
            }

            // A tool's own ceiling only (an unnamed call has none), against what streamed.
            if (Over is null && call is { Name: { } name, Ceiling: int ceiling } && call.Streamed > ceiling)
            {
                Over = new Runaway(name, call.Streamed, ceiling);
            }
        }

        long now = _time.GetTimestamp();
        if (candidate is null || candidate == _shown || (_lastShown is { } last && _time.GetElapsedTime(last, now) < ProgressInterval))
        {
            return null;
        }

        _shown = candidate;
        _lastShown = now;
        return candidate;
    }
}
