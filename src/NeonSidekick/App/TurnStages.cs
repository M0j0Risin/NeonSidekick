using NeonSidekick.Comfy;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>
/// The turn spinner's label as the turn moves through its stages (2026-09-16, the user's call,
/// replacing the one word drawn once per turn): <c>thinking</c> from the start and after every
/// tool result — the server's wait, its reasoning, a tool call it is still writing — <c>writing</c>
/// once the reply's text streams, and a running tool's bare name (<c>read_file</c>) between its
/// call and its result — but <c>generate_image</c> reads <see cref="ComfyText.GeneratingLabel"/>, as every
/// ComfyUI generation does (2026-09-25, the user's call; the pane draws it after the tally). Decided from the turn's events alone (<see cref="TurnEvent"/> marks no
/// request and surfaces no reasoning, so the first text is where thinking ends). With
/// <see cref="AppSettingsData.LlmUseFunVerbs"/> on, every thinking and writing stage draws a
/// fresh verb from <see cref="ThinkingVerbs.All"/>, never the one shown just before; the tool
/// stage still names the tool. The elapsed count is the pane's and runs on across stages.
/// Pure; the words are pinned.
/// </summary>
internal sealed class TurnStages
{
    /// <summary>The label while the reply's text streams; <see cref="ChatScreen.ThinkingLabel"/> is the wait's.</summary>
    public const string WritingLabel = "writing";

    private enum Stage
    {
        Thinking,
        Writing,
        Tool,
    }

    private readonly bool _funVerbs;
    private readonly Random _random;
    private Stage _stage;
    private string? _lastVerb;

    /// <param name="funVerbs"><see cref="AppSettingsData.LlmUseFunVerbs"/>: a verb in place of <c>thinking</c> and <c>writing</c>.</param>
    /// <param name="random">The verbs' source (tests seed one).</param>
    public TurnStages(bool funVerbs, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _funVerbs = funVerbs;
        _random = random;
    }

    /// <summary>The turn's first label: the thinking stage.</summary>
    public string Start()
    {
        _stage = Stage.Thinking;
        return Word(ChatScreen.ThinkingLabel);
    }

    /// <summary>
    /// The label after <paramref name="evt"/>, null when the stage did not change: the first text
    /// of a stretch is <see cref="WritingLabel"/>, a tool call its name, a tool result the thinking
    /// stage again (the next request's wait starts there), usage and notices nothing.
    /// </summary>
    public string? Advance(TurnEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        switch (evt)
        {
            case TurnEvent.TextDelta when _stage != Stage.Writing:
                _stage = Stage.Writing;
                return Word(WritingLabel);
            case TurnEvent.ToolCall call:
                _stage = Stage.Tool;
                return string.Equals(call.Name, GenerateImageTool.ToolName, StringComparison.Ordinal) ? ComfyText.GeneratingLabel : call.Name;
            case TurnEvent.ToolResult:
                _stage = Stage.Thinking;
                return Word(ChatScreen.ThinkingLabel);
            default:
                return null;
        }
    }

    /// <summary>The stage's word, or under the fun verbs a fresh draw that is not the last one shown.</summary>
    private string Word(string plain)
    {
        if (!_funVerbs)
        {
            return plain;
        }

        _lastVerb = ThinkingVerbs.Pick(_random, _lastVerb);
        return _lastVerb;
    }
}
