using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonSidekick.Settings;

/// <summary>
/// One model's sampling overrides (2026-09-28, the user's ask: "LLM sampling settings like temperature, top_p, top_k,
/// min_p, presence_penalty and repetition_penalty … override the model defaults if desired"), a value of
/// <see cref="AppSettingsData.LlmSampling"/>. Every field is optional: null is "the server's own default", and a null
/// is not written to <c>profile.json</c>, so an entry holds only what was set. The ranges and the wire names are
/// <see cref="Llm.SamplingField"/>'s; the resolved form a request carries is <see cref="Llm.LlmSampling"/>.
/// </summary>
public sealed class LlmSamplingEntry
{
    /// <summary><c>temperature</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    /// <summary><c>top_p</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }

    /// <summary><c>top_k</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TopK { get; set; }

    /// <summary><c>min_p</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinP { get; set; }

    /// <summary><c>presence_penalty</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PresencePenalty { get; set; }

    /// <summary><c>frequency_penalty</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FrequencyPenalty { get; set; }

    /// <summary><c>repetition_penalty</c>, sent as <c>repeat_penalty</c> too (llama.cpp's and LM Studio's name).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RepetitionPenalty { get; set; }

    /// <summary>
    /// The extra body: top-level request fields written as they are (<c>typical_p</c>, <c>dry_multiplier</c>,
    /// <c>seed</c>, …), for what a server knows and the named fields do not cover. The keys
    /// <see cref="Llm.LlmSampling.ExtraProblem"/> refuses never get here from the pane or the variable.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>True when nothing is set: an entry that says nothing is removed rather than saved.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Temperature is null && TopP is null && TopK is null && MinP is null && PresencePenalty is null
        && FrequencyPenalty is null && RepetitionPenalty is null && Extra is not { Count: > 0 };

    /// <summary>A deep copy: <see cref="JsonElement"/>s are cloned so the copy outlives any document.</summary>
    public LlmSamplingEntry Copy() => new()
    {
        Temperature = Temperature,
        TopP = TopP,
        TopK = TopK,
        MinP = MinP,
        PresencePenalty = PresencePenalty,
        FrequencyPenalty = FrequencyPenalty,
        RepetitionPenalty = RepetitionPenalty,
        Extra = Extra?.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal),
    };

    /// <summary>A deep copy of a whole <see cref="AppSettingsData.LlmSampling"/> map, or null.</summary>
    public static Dictionary<string, LlmSamplingEntry>? CopyAll(Dictionary<string, LlmSamplingEntry>? source) =>
        source?.ToDictionary(p => p.Key, p => p.Value?.Copy() ?? new LlmSamplingEntry(), StringComparer.Ordinal);
}
