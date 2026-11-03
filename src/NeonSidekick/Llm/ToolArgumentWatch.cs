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

    /// <summary>The first call whose streamed arguments passed its tool's ceiling: what the turn ends on.</summary>
    public sealed record Runaway(string Name, long Chars, int Ceiling);

    private sealed class Call
    {
        /// <summary>The name the stream gave, once the request offered that tool (<c>offered</c>); null until then, or for one it didn't.</summary>
        public string? Name { get; set; }

        /// <summary>The name the stream gave, offered or not: for the log lines alone (<see cref="Latest"/>), scrubbed there.</summary>
        public string? StreamedName { get; set; }

        public long Chars { get; set; }

        /// <summary>The characters that came in a live stream's pieces (none over <see cref="WholePiece"/>): what the ceiling reads.</summary>
        public long Streamed { get; set; }

        public int? Ceiling { get; set; }
    }

    private readonly Func<string, int?> _ceiling;
    private readonly Func<string, bool>? _offered;
    private readonly TimeProvider _time;
    private readonly Dictionary<int, Call> _calls = [];
    private (string Name, long Chars)? _shown;
    private long? _lastShown;

    // The call written last (2026-10-30, review F70: a record was built for it on every delta, and read only by a failed request's log line).
    private Call? _latest;

    /// <param name="ceiling">A tool's ceiling by its name (<see cref="Tools.IArgumentCeiling"/>), null for none; read as a call is named.</param>
    /// <param name="time">The clock <see cref="ProgressInterval"/> is kept on.</param>
    /// <param name="offered">
    /// Whether the request offered a tool of that name (2026-11-02, review P12-7); null takes every name. A name it didn't offer is never
    /// the call's name, so the busy row shows it as <see cref="TurnText.UnnamedCall"/> past <see cref="CountShownFrom"/> and it has no
    /// ceiling: an injected model's long name, or one carrying ESC or bidi characters, never reads as the app's own status.
    /// </param>
    public ToolArgumentWatch(Func<string, int?> ceiling, TimeProvider time, Func<string, bool>? offered = null)
    {
        ArgumentNullException.ThrowIfNull(ceiling);
        ArgumentNullException.ThrowIfNull(time);
        _ceiling = ceiling;
        _time = time;
        _offered = offered;
    }

    /// <summary>The first call past its ceiling, or null.</summary>
    public Runaway? Over { get; private set; }

    /// <summary>
    /// The call written last and its own characters, shown or not, or null before any; for the request's log lines, so its streamed name
    /// even when the request didn't offer it (troubleshooting), put through <see cref="Files.NameScrub.Display"/>.
    /// </summary>
    public TurnEvent.ToolWriting? Latest => _latest is { } call
        ? new TurnEvent.ToolWriting(call.Name ?? (call.StreamedName is { } streamed ? Files.NameScrub.Display(streamed) : TurnText.UnnamedCall), call.Chars)
        : null;

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

        Call? candidate = null;
        foreach (var delta in deltas)
        {
            if (!_calls.TryGetValue(delta.Index, out var call))
            {
                call = new Call();
                _calls.Add(delta.Index, call);
            }

            if (call.StreamedName is null && !string.IsNullOrEmpty(delta.FunctionName))
            {
                call.StreamedName = delta.FunctionName;
                if (_offered?.Invoke(delta.FunctionName) ?? true)
                {
                    call.Name = delta.FunctionName;
                    call.Ceiling = _ceiling(call.Name);
                }
            }

            int piece = delta.FunctionArgumentsUpdate is { } arguments ? Encoding.UTF8.GetCharCount(arguments.ToMemory().Span) : 0;
            call.Chars += piece;
            Chars += piece;
            if (piece <= WholePiece)
            {
                call.Streamed += piece;
            }

            _latest = call;
            if (call.Name is not null || call.Chars >= CountShownFrom)
            {
                candidate = call;
            }

            // A tool's own ceiling only (an unnamed call has none), against what streamed.
            if (Over is null && call is { Name: { } name, Ceiling: int ceiling } && call.Streamed > ceiling)
            {
                Over = new Runaway(name, call.Streamed, ceiling);
            }
        }

        // The candidate's values now: a later delta in the update can only be its own call's, which only grows it.
        if (candidate is null)
        {
            return null;
        }

        var seen = (candidate.Name ?? TurnText.UnnamedCall, candidate.Chars);
        long now = _time.GetTimestamp();
        if (seen == _shown || (_lastShown is { } last && _time.GetElapsedTime(last, now) < ProgressInterval))
        {
            return null;
        }

        _shown = seen;
        _lastShown = now;
        return new TurnEvent.ToolWriting(seen.Item1, seen.Chars);
    }
}
