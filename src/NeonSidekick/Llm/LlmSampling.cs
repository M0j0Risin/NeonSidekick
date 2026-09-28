using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm;

/// <summary>
/// The sampling a request carries (2026-09-28, the user's ask): <see cref="AppSettingsData.LlmSampling"/> resolved for
/// one model — each field from the model's own entry, else the <see cref="AnyModel"/> entry, else null, which sends
/// nothing and leaves the server's default. <see cref="ApplyTo"/> puts it on a request's <see cref="ChatOptions"/>:
/// the standard fields as the options' own properties, and the whole of it under <see cref="OpenAICompatibleChatClient.SamplingKey"/>
/// for the fields the OpenAI schema has no slot for (top_k, min_p, the repetition penalty, the extra body), which the
/// OpenAI-compatible client writes into the request as it does <c>chat_template_kwargs</c>. The Claude API client reads
/// none of it.
/// </summary>
public sealed record LlmSampling
{
    /// <summary>The key of the entry every model without a value of its own falls back to.</summary>
    public const string AnyModel = "*";

    private const string Category = "Llm";

    // Before None: static initialisers run in declaration order, and None's Extra reads this.
    private static readonly IReadOnlyDictionary<string, JsonElement> NoExtra = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>Nothing set: every field the server's.</summary>
    public static LlmSampling None { get; } = new();

    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    public int? TopK { get; init; }

    public double? MinP { get; init; }

    public double? PresencePenalty { get; init; }

    public double? FrequencyPenalty { get; init; }

    public double? RepetitionPenalty { get; init; }

    /// <summary>The extra body's top-level fields, the model's entry's winning a key over <see cref="AnyModel"/>'s.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extra { get; init; } = NoExtra;

    /// <summary>True when nothing is set.</summary>
    public bool IsEmpty => SamplingField.All.All(f => Value(f.Key) is null) && Extra.Count == 0;

    /// <summary>True when something needs the request's raw fields: top_k, min_p, the repetition penalty or the extra body.</summary>
    public bool HasRawFields => TopK is not null || MinP is not null || RepetitionPenalty is not null || Extra.Count > 0;

    /// <summary>The value of <paramref name="key"/>, or null.</summary>
    public double? Value(SamplingKey key) => key switch
    {
        SamplingKey.Temperature => Temperature,
        SamplingKey.TopP => TopP,
        SamplingKey.TopK => TopK,
        SamplingKey.MinP => MinP,
        SamplingKey.PresencePenalty => PresencePenalty,
        SamplingKey.FrequencyPenalty => FrequencyPenalty,
        SamplingKey.RepetitionPenalty => RepetitionPenalty,
        _ => null,
    };

    /// <summary>The separator of <see cref="Describe"/>'s parts.</summary>
    public const string DescribeSeparator = " · ";

    /// <summary>
    /// The set fields in wire names, for the log and the <c>LLM sampling</c> row: <c>temperature 0.6 · top_k 20 · typical_p</c>
    /// — an extra field by its name alone. Empty when nothing is set.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>();
        foreach (var field in SamplingField.All)
        {
            if (Value(field.Key) is { } value)
            {
                parts.Add(field.Wire + " " + SamplingField.Format(value));
            }
        }

        parts.AddRange(Extra.Keys.Order(StringComparer.Ordinal));
        return string.Join(DescribeSeparator, parts);
    }

    /// <summary>
    /// The sampling for <paramref name="modelId"/> under <paramref name="effective"/>: field by field, the model's entry
    /// (its key compared ignoring case), then <see cref="AnyModel"/>'s; the extra body merged by key, the model's winning.
    /// A saved value out of its field's range, or an extra key <see cref="ExtraProblem"/> refuses — only a hand-edited
    /// profile holds one — warns and is skipped (the next layer's value, or none, stands); never clamped.
    /// </summary>
    public static LlmSampling Resolve(AppSettingsData effective, string? modelId)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (effective.LlmSampling is not { Count: > 0 } map)
        {
            return None;
        }

        string? modelKey = string.IsNullOrWhiteSpace(modelId) ? null : KeyFor(map, modelId);
        var model = modelKey is null || modelKey == AnyModel ? null : map[modelKey];
        var any = KeyFor(map, AnyModel) is { } anyKey ? map[anyKey] : null;
        if (model is null && any is null)
        {
            return None;
        }

        var values = new double?[SamplingField.All.Count];
        foreach (var field in SamplingField.All)
        {
            values[(int)field.Key] = Checked(field, model, modelKey) ?? Checked(field, any, AnyModel);
        }

        var extra = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Merge(extra, any?.Extra, AnyModel);
        Merge(extra, model?.Extra, modelKey);
        return new LlmSampling
        {
            Temperature = values[(int)SamplingKey.Temperature],
            TopP = values[(int)SamplingKey.TopP],
            TopK = values[(int)SamplingKey.TopK] is { } k ? (int)k : null,
            MinP = values[(int)SamplingKey.MinP],
            PresencePenalty = values[(int)SamplingKey.PresencePenalty],
            FrequencyPenalty = values[(int)SamplingKey.FrequencyPenalty],
            RepetitionPenalty = values[(int)SamplingKey.RepetitionPenalty],
            Extra = extra.Count > 0 ? extra : NoExtra,
        };
    }

    private static double? Checked(SamplingField field, LlmSamplingEntry? entry, string? key)
    {
        if (field.Get(entry) is not { } value)
        {
            return null;
        }

        if (field.Accepts(value))
        {
            return value;
        }

        DiagnosticLog.Warn(Category, $"Sampling for {key}: {field.Wire}={SamplingField.Format(value)} is not {field.RangeText}. Not sending it.");
        return null;
    }

    private static void Merge(Dictionary<string, JsonElement> into, Dictionary<string, JsonElement>? from, string? key)
    {
        if (from is null)
        {
            return;
        }

        foreach (var (name, value) in from)
        {
            if (ExtraProblem(name, value) is { } problem)
            {
                DiagnosticLog.Warn(Category, $"Sampling for {key}: the extra body's {name} {problem}. Not sending it.");
                continue;
            }

            into[name] = value;
        }
    }

    /// <summary>The key <paramref name="map"/> stores <paramref name="modelId"/> under, compared ignoring case, or null.</summary>
    public static string? KeyFor(IReadOnlyDictionary<string, LlmSamplingEntry>? map, string modelId)
    {
        if (map is null)
        {
            return null;
        }

        if (map.ContainsKey(modelId))
        {
            return modelId;
        }

        foreach (var key in map.Keys)
        {
            if (string.Equals(key, modelId, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// The request fields the app writes itself, never taken from the extra body: the conversation, the tools, the
    /// stream, the effort. Pinned.
    /// </summary>
    public static IReadOnlyList<string> ReservedFields { get; } =
        ["model", "messages", "tools", "tool_choice", "parallel_tool_calls", "functions", "function_call", "stream", "stream_options", "n", "reasoning_effort"];

    /// <summary>What the extra body may not say about <c>chat_template_kwargs</c>: it must be an object, merged under the app's switches. Pinned.</summary>
    public const string TemplateKwargsNotObject = "must be a JSON object";

    /// <summary>The refusal of a reserved field. Pinned.</summary>
    public const string ReservedProblem = "is the app's to set";

    /// <summary>
    /// Why the extra body may not carry <paramref name="name"/>, or null when it may: a reserved field
    /// (<see cref="ReservedFields"/>), a named sampling field (it has its own row), or a
    /// <c>chat_template_kwargs</c> that is not an object (merged with the app's, which win a shared key).
    /// </summary>
    public static string? ExtraProblem(string name, JsonElement value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "is not a field name";
        }

        if (ReservedFields.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return ReservedProblem;
        }

        if (SamplingField.ByWire(name) is { } field)
        {
            return $"is a named field: set {field.Wire} itself";
        }

        if (string.Equals(name, OpenAICompatibleChatClient.TemplateKwargsField, StringComparison.Ordinal) && value.ValueKind != JsonValueKind.Object)
        {
            return TemplateKwargsNotObject;
        }

        return null;
    }

    /// <summary>
    /// An extra body typed in: a JSON object whose every key <see cref="ExtraProblem"/> passes; an empty object is none.
    /// False with the reason otherwise.
    /// </summary>
    public static bool TryParseExtra(string text, out Dictionary<string, JsonElement>? extra, out string problem)
    {
        extra = null;
        if (!TryParseObject(text, out var root, out problem))
        {
            return false;
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (ExtraProblem(property.Name, property.Value) is { } refused)
            {
                problem = $"{property.Name} {refused}";
                return false;
            }

            fields[property.Name] = property.Value.Clone();
        }

        extra = fields.Count > 0 ? fields : null;
        return true;
    }

    /// <summary>The refusal of text that is JSON but no object. Pinned.</summary>
    public const string NotAnObject = "must be a JSON object, {\"field\": value}";

    /// <summary><paramref name="text"/> as a JSON object (cloned: it outlives the document), or false with the reason.</summary>
    private static bool TryParseObject(string text, out JsonElement root, out string problem)
    {
        root = default;
        problem = "";
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            problem = "is not JSON (" + ex.Message + ")";
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            problem = NotAnObject;
            return false;
        }

        return true;
    }

    /// <summary>
    /// A whole entry in wire names (<c>NEONSIDEKICK_LLM_SAMPLING</c>): <c>{"temperature":0.6,"top_k":20,"typical_p":0.9}</c>
    /// — a named field (<see cref="SamplingField.ByWire"/>) must be a number in its range or null (unset), any other key
    /// joins the extra body under <see cref="ExtraProblem"/>'s rule. False with the reason otherwise.
    /// </summary>
    public static bool TryParseEntry(string text, out LlmSamplingEntry entry, out string problem)
    {
        entry = new LlmSamplingEntry();
        if (!TryParseObject(text, out var root, out problem))
        {
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (SamplingField.ByWire(property.Name) is { } field)
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out double value) || !field.Accepts(value))
                {
                    problem = $"{field.Wire} must be {field.RangeText}";
                    return false;
                }

                field.Set(entry, value);
                continue;
            }

            if (ExtraProblem(property.Name, property.Value) is { } refused)
            {
                problem = $"{property.Name} {refused}";
                return false;
            }

            (entry.Extra ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal))[property.Name] = property.Value.Clone();
        }

        return true;
    }

    /// <summary>
    /// Puts this sampling on <paramref name="options"/>: temperature, top_p, top_k and the two OpenAI penalties as the
    /// options' own properties (the MEAI adapter writes the four OpenAI ones; top_k it has no field for), and, when
    /// anything is set, this record under <see cref="OpenAICompatibleChatClient.SamplingKey"/> for the raw fields.
    /// </summary>
    public void ApplyTo(ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (IsEmpty)
        {
            return;
        }

        options.Temperature = (float?)Temperature;
        options.TopP = (float?)TopP;
        options.TopK = TopK;
        options.PresencePenalty = (float?)PresencePenalty;
        options.FrequencyPenalty = (float?)FrequencyPenalty;
        (options.AdditionalProperties ??= [])[OpenAICompatibleChatClient.SamplingKey] = this;
    }
}
