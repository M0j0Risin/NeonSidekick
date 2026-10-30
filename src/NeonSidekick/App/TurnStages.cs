using System.Text.Json;
using NeonSidekick.Comfy;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>
/// The turn spinner's label as the turn moves through its stages (2026-09-16, the user's call,
/// replacing the one word drawn once per turn): <c>thinking</c> from the start and after every
/// tool result — the server's wait, its reasoning — <c>writing</c> once the reply's text streams,
/// <c>writing present_audit</c> while the model writes a call (<see cref="TurnEvent.ToolWriting"/>, 2026-10-29: session 832's
/// seven minutes of a call's arguments read as thinking; <see cref="ToolWritingLabel"/>), and a running tool's bare name (<c>read_file</c>) between its
/// call and its result — but <c>generate_image</c> reads <see cref="ComfyText.GeneratingLabel"/>, as every
/// ComfyUI generation does (2026-09-25, the user's call; the pane draws it after the tally) — 🖼️ for a call with no
/// input picture, 🎨 for one with (<see cref="ComfyText.GeneratingLabelFor"/>, 2026-09-26). Decided from the turn's events alone (<see cref="TurnEvent"/> marks no
/// request, and its thinking — <see cref="TurnEvent.ThinkingDelta"/>, since 2026-09-26 — is the thinking
/// stage still, so the first text is where thinking ends). With
/// <see cref="AppSettingsData.LlmUseFunVerbs"/> on, every thinking and writing stage draws a
/// fresh verb from <see cref="ThinkingVerbs.All"/>, never the one shown just before (a call being written draws one for the call); the tool
/// stage still names the tool. The elapsed count is the pane's and runs on across stages.
/// Pure; the words are pinned.
/// </summary>
internal sealed class TurnStages
{
    /// <summary>The label while the reply's text streams; <see cref="ChatScreen.ThinkingLabel"/> is the wait's.</summary>
    public const string WritingLabel = "writing";

    /// <summary>
    /// The label while the model writes a call to <paramref name="name"/> (2026-10-29): <c>writing present_audit</c>, and from
    /// <see cref="ToolArgumentWatch.CountShownFrom"/> characters on its size, <c>writing present_audit (41.0k chars)</c> — in brackets, the
    /// busy row's parts being parted by <c> · </c>. A server that holds the arguments back (SGLang) shows the name alone. <paramref name="verb"/>
    /// is a fun verb in place of <see cref="WritingLabel"/>. Pinned.
    /// </summary>
    public static string ToolWritingLabel(string name, long chars, string verb = WritingLabel) =>
        chars >= ToolArgumentWatch.CountShownFrom ? $"{verb} {name} ({UsageText.CompactNumber(chars)} chars)" : $"{verb} {name}";

    private enum Stage
    {
        Thinking,
        Writing,
        CallWriting,
        Tool,
    }

    private readonly bool _funVerbs;
    private readonly Random _random;
    private Stage _stage;
    private string? _lastVerb;
    private string? _callName;
    private string _callVerb = WritingLabel;

    /// <param name="funVerbs"><see cref="AppSettingsData.LlmUseFunVerbs"/>: a verb in place of <c>thinking</c> and <c>writing</c>.</param>
    /// <param name="random">The verbs' source (tests seed one).</param>
    public TurnStages(bool funVerbs, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _funVerbs = funVerbs;
        _random = random;
    }

    /// <summary>Whether a tool runs (or a mid-turn compact): the stage whose own clock the busy row shows beside the turn's (2026-10-04).</summary>
    public bool InTool => _stage == Stage.Tool;

    /// <summary>The turn's first label: the thinking stage.</summary>
    public string Start()
    {
        _stage = Stage.Thinking;
        return Word(ChatScreen.ThinkingLabel);
    }

    /// <summary>
    /// The label after <paramref name="evt"/>, null when the stage did not change: the first text
    /// of a stretch is <see cref="WritingLabel"/>, a call being written <see cref="ToolWritingLabel"/> at each progress, a tool call its name, a tool result the thinking
    /// stage again (the next request's wait starts there), a mid-turn compact
    /// <see cref="CompactionText.CompactingLabel"/> and the thinking stage once it is done (2026-09-28), usage and notices nothing.
    /// </summary>
    public string? Advance(TurnEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        switch (evt)
        {
            case TurnEvent.TextDelta when _stage != Stage.Writing:
                _stage = Stage.Writing;
                return Word(WritingLabel);
            case TurnEvent.ToolWriting writing:
                // Not the tool stage: each size's new label would restart its clock; the turn's runs on (2026-10-29). One verb a call,
                // drawn as it is named, so the sizes after it do not reshuffle the word.
                if (_stage != Stage.CallWriting || !string.Equals(_callName, writing.Name, StringComparison.Ordinal))
                {
                    _stage = Stage.CallWriting;
                    _callName = writing.Name;
                    _callVerb = Word(WritingLabel);
                }

                return ToolWritingLabel(writing.Name, writing.Chars, _callVerb);
            case TurnEvent.ToolCall call:
                _stage = Stage.Tool;
                return string.Equals(call.Name, GenerateImageTool.ToolName, StringComparison.Ordinal) ? ComfyText.GeneratingLabelFor(InputImages(call.ArgumentsJson))
                    : string.Equals(call.Name, NeonAdvisorTool.ToolName, StringComparison.Ordinal) ? Advisors.AdvisorText.AskingLabel
                    : call.Name;
            case TurnEvent.ToolResult:
            case TurnEvent.Compacted:
                _stage = Stage.Thinking;
                return Word(ChatScreen.ThinkingLabel);
            case TurnEvent.Compacting:
                _stage = Stage.Tool;
                return CompactionText.CompactingLabel;
            default:
                return null;
        }
    }

    /// <summary>
    /// How many input pictures a <c>generate_image</c> call names (2026-09-26): its non-blank <c>image</c>, <c>image2</c>,
    /// <c>image3</c> — what picks 🎨 over 🖼️. Arguments that are no JSON object count none.
    /// </summary>
    private static int InputImages(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            return new[] { GenerateImageTool.ImageArgument, GenerateImageTool.Image2Argument, GenerateImageTool.Image3Argument }
                .Count(key => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()));
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>The stage's word, or under the fun verbs a fresh draw that is not the last one shown.</summary>
    private string Word(string plain)
    {
        if (!_funVerbs)
        {
            return plain;
        }

        // An empty verbs file (2026-10-12, the list an asset since) draws nothing: the stage's own word then.
        _lastVerb = ThinkingVerbs.Pick(_random, _lastVerb);
        return _lastVerb ?? plain;
    }
}
