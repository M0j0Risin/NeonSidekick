using System.Globalization;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm;

/// <summary>The named sampling fields of <see cref="LlmSamplingEntry"/>, in the pane's order.</summary>
public enum SamplingKey
{
    Temperature,
    TopP,
    TopK,
    MinP,
    PresencePenalty,
    FrequencyPenalty,
    RepetitionPenalty,
}

/// <summary>
/// One named sampling field (2026-09-28): its wire name, the range a value must fall in — refused outside it, never
/// clamped, by the pane, <c>/sampling</c>, <c>NEONSIDEKICK_LLM_SAMPLING</c> and a hand-edited profile alike — and its
/// slot on <see cref="LlmSamplingEntry"/>. The ranges are the widest the servers agree on: temperature up to 5 (llama.cpp
/// takes more, OpenAI 2), top_p above 0 (vLLM refuses 0), top_k from -1 (vLLM's old "off"; 0 is "off" on the rest),
/// the OpenAI penalties' ±2, a repetition penalty above 0 (1 is "none").
/// </summary>
public sealed record SamplingField(SamplingKey Key, string Wire, double Min, double Max, bool MinExclusive = false, bool Integer = false)
{
    /// <summary>Every field in the pane's order: the user's six and <c>frequency_penalty</c>, the other OpenAI penalty.</summary>
    public static IReadOnlyList<SamplingField> All { get; } =
    [
        new(SamplingKey.Temperature, "temperature", 0, 5),
        new(SamplingKey.TopP, "top_p", 0, 1, MinExclusive: true),
        new(SamplingKey.TopK, "top_k", -1, 100_000, Integer: true),
        new(SamplingKey.MinP, "min_p", 0, 1),
        new(SamplingKey.PresencePenalty, "presence_penalty", -2, 2),
        new(SamplingKey.FrequencyPenalty, "frequency_penalty", -2, 2),
        new(SamplingKey.RepetitionPenalty, "repetition_penalty", 0, 2, MinExclusive: true),
    ];

    /// <summary>llama.cpp's and LM Studio's name for <see cref="SamplingKey.RepetitionPenalty"/>: accepted as the field by name, and sent beside it.</summary>
    public const string RepeatPenaltyWire = "repeat_penalty";

    /// <summary>The field by its wire name, ignoring case (<see cref="RepeatPenaltyWire"/> included), or null.</summary>
    public static SamplingField? ByWire(string? name)
    {
        string wanted = name?.Trim() ?? "";
        if (string.Equals(wanted, RepeatPenaltyWire, StringComparison.OrdinalIgnoreCase))
        {
            return For(SamplingKey.RepetitionPenalty);
        }

        return All.FirstOrDefault(f => string.Equals(f.Wire, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The field for <paramref name="key"/>.</summary>
    public static SamplingField For(SamplingKey key) => All[(int)key];

    /// <summary>Whether <paramref name="value"/> is in range (and whole, for an integer field).</summary>
    public bool Accepts(double value) =>
        double.IsFinite(value)
        && (MinExclusive ? value > Min : value >= Min)
        && value <= Max
        && (!Integer || Math.Floor(value) == value);

    /// <summary>Invariant-culture parse plus <see cref="Accepts"/>.</summary>
    public bool TryParse(string? text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Accepts(value);

    /// <summary>The range in words, for the refusals: <c>0 to 5</c>, <c>above 0, up to 1</c>, <c>a whole number from -1 to 100000</c>.</summary>
    public string RangeText =>
        Integer ? $"a whole number from {Format(Min)} to {Format(Max)}"
        : MinExclusive ? $"above {Format(Min)}, up to {Format(Max)}"
        : $"{Format(Min)} to {Format(Max)}";

    /// <summary>The value as it is shown and logged: invariant, shortest round-trip.</summary>
    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>This field's value on <paramref name="entry"/>, or null.</summary>
    public double? Get(LlmSamplingEntry? entry) => entry is null ? null : Key switch
    {
        SamplingKey.Temperature => entry.Temperature,
        SamplingKey.TopP => entry.TopP,
        SamplingKey.TopK => entry.TopK,
        SamplingKey.MinP => entry.MinP,
        SamplingKey.PresencePenalty => entry.PresencePenalty,
        SamplingKey.FrequencyPenalty => entry.FrequencyPenalty,
        SamplingKey.RepetitionPenalty => entry.RepetitionPenalty,
        _ => null,
    };

    /// <summary>Sets (or, with null, clears) this field on <paramref name="entry"/>. The caller has checked <see cref="Accepts"/>.</summary>
    public void Set(LlmSamplingEntry entry, double? value)
    {
        ArgumentNullException.ThrowIfNull(entry);
        switch (Key)
        {
            case SamplingKey.Temperature: entry.Temperature = value; break;
            case SamplingKey.TopP: entry.TopP = value; break;
            case SamplingKey.TopK: entry.TopK = value is { } k ? (int)k : null; break;
            case SamplingKey.MinP: entry.MinP = value; break;
            case SamplingKey.PresencePenalty: entry.PresencePenalty = value; break;
            case SamplingKey.FrequencyPenalty: entry.FrequencyPenalty = value; break;
            case SamplingKey.RepetitionPenalty: entry.RepetitionPenalty = value; break;
        }
    }
}
