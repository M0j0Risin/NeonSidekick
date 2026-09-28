using System.Globalization;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Llm;

/// <summary>Where <see cref="ServerSampling"/>'s values were read.</summary>
public enum ServerSamplingSource
{
    /// <summary>llama.cpp's <c>GET /props</c>: <c>default_generation_settings.params</c>.</summary>
    LlamaProps,

    /// <summary>Ollama's <c>POST /api/show</c>: the Modelfile's <c>parameters</c> lines.</summary>
    OllamaShow,

    /// <summary>The model's <c>generation_config.json</c> on Hugging Face, the file vLLM and SGLang take their defaults from.</summary>
    HuggingFace,
}

/// <summary>
/// The sampling a server applies when a request says nothing (2026-09-28, the user's question: "is there a way to know
/// what the server default settings are?"): the fields it named, in range, and where they were read. <see cref="Detail"/>
/// is the Hugging Face repo id for <see cref="ServerSamplingSource.HuggingFace"/>, else null. Display only: nothing is
/// sent because of it.
/// </summary>
public sealed record ServerSampling(IReadOnlyDictionary<SamplingKey, double> Values, ServerSamplingSource Source, string? Detail = null)
{
    /// <summary>The value of <paramref name="key"/>, or null when the server named none.</summary>
    public double? Value(SamplingKey key) => Values.TryGetValue(key, out double value) ? value : null;

    /// <summary>The values in wire names for the log: <c>temperature 0.8 · top_k 40</c>.</summary>
    public string Describe() =>
        string.Join(LlmSampling.DescribeSeparator, SamplingField.All.Where(f => Values.ContainsKey(f.Key)).Select(f => f.Wire + " " + SamplingField.Format(Values[f.Key])));
}

/// <summary>
/// Asks the connected server what sampling it applies by default (2026-09-28), for the <c>/sampling</c> pane's
/// <c>(server)</c> values. Only some servers say: llama.cpp's <c>/props</c> (<c>default_generation_settings.params</c>)
/// and Ollama's <c>/api/show</c> (the Modelfile's <c>parameters</c>); vLLM, SGLang and LM Studio expose none over their
/// API. vLLM and SGLang take theirs from the model's Hugging Face <c>generation_config.json</c> by default, so with
/// <c>LLM sampling from Hugging Face</c> on (the user's call, off by default) that file is fetched when the server
/// said nothing and the model id reads as a repo (<see cref="HuggingFaceUrl"/>). The LLM key is never sent there.
/// The tiers in order, the first with a value wins; never throws; null means unknown.
/// </summary>
public sealed class ServerSamplingProbe
{
    private const string Category = "Llm";

    /// <summary>Where a repo's files are read; the tests' stub maps it.</summary>
    public const string HuggingFaceBase = "https://huggingface.co/";

    private readonly HttpClient _http;

    /// <param name="http">The transport; tests pass one over a stub handler.</param>
    /// <param name="timeout">Per-call ceiling; defaults to <see cref="LlmEndpointProbe.DefaultTimeout"/>.</param>
    public ServerSamplingProbe(HttpClient http, TimeSpan? timeout = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        Timeout = timeout ?? LlmEndpointProbe.DefaultTimeout;
        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), Timeout, "The probe timeout must be positive.");
        }
    }

    /// <summary>The per-call ceiling in force.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// The defaults for <paramref name="modelId"/>: <c>/props</c>, then <c>/api/show</c>, then — with
    /// <paramref name="huggingFace"/> and a repo-shaped id — the model card's <c>generation_config.json</c>, fetched
    /// with no key. One log line either way.
    /// </summary>
    public async Task<ServerSampling?> DetectAsync(Uri baseUrl, string modelId, string? apiKey, bool huggingFace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(modelId);
        var root = LlmEndpoint.RootUrl(LlmEndpoint.NormalizeBaseUrl(baseUrl));

        var found = ParseLlamaProps(await NativeRequest.TextAsync(_http, ContextLengthProbe.Under(root, "props"), apiKey, Timeout, Category, cancellationToken).ConfigureAwait(false))
            ?? ParseOllamaShow(await NativeRequest.TextAsync(_http, ContextLengthProbe.Under(root, "api/show"), apiKey, Timeout, Category, cancellationToken, ContextLengthProbe.ShowBody(modelId)).ConfigureAwait(false));

        if (found is null && huggingFace && HuggingFaceUrl(modelId) is { } card)
        {
            // Never the LLM's key: huggingface.co is not the server it was issued for.
            found = ParseGenerationConfig(await NativeRequest.TextAsync(_http, card, apiKey: null, Timeout, Category, cancellationToken).ConfigureAwait(false), modelId);
        }

        DiagnosticLog.Info(Category, found is { } server
            ? $"Server sampling defaults for {modelId}: {server.Describe()} ({SourceWord(server.Source)})."
            : $"Server sampling defaults for {modelId}: none found.");
        return found;
    }

    /// <summary>The log's word for a source.</summary>
    private static string SourceWord(ServerSamplingSource source) => source switch
    {
        ServerSamplingSource.LlamaProps => "/props",
        ServerSamplingSource.OllamaShow => "/api/show",
        _ => "Hugging Face",
    };

    /// <summary>
    /// The model card's <c>generation_config.json</c> for a repo-shaped <paramref name="modelId"/> (<c>Qwen/Qwen3-8B</c>:
    /// two parts of letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, each starting with a letter or digit), else null — an
    /// Ollama tag (<c>qwen3:8b</c>), a path, a bare name.
    /// </summary>
    public static Uri? HuggingFaceUrl(string? modelId)
    {
        var parts = (modelId ?? "").Trim().Split('/');
        if (parts.Length != 2 || !parts.All(IsRepoPart))
        {
            return null;
        }

        return new Uri(HuggingFaceBase + parts[0] + "/" + parts[1] + "/resolve/main/generation_config.json");
    }

    private static bool IsRepoPart(string part) =>
        part.Length > 0 && char.IsAsciiLetterOrDigit(part[0]) && !part.Contains("..", StringComparison.Ordinal)
        && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    // ── Parsers: JsonDocument (no serializer context), each null on anything unexpected or with no value ──

    /// <summary>llama.cpp: <c>default_generation_settings.params</c>, else the settings object itself (older builds). Rounded to 6 places: the server reports float32.</summary>
    public static ServerSampling? ParseLlamaProps(string? json) => Parse(json, root =>
    {
        if (!root.TryGetProperty("default_generation_settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var values = Numbers(settings.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object ? parameters : settings);
        return values.Count > 0 ? new ServerSampling(values, ServerSamplingSource.LlamaProps) : null;
    });

    /// <summary>Ollama: the <c>parameters</c> string, one <c>key value</c> per line (padded); a key that is no field (<c>stop</c>, <c>num_ctx</c>) skipped.</summary>
    public static ServerSampling? ParseOllamaShow(string? json) => Parse(json, root =>
    {
        if (!root.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var values = new Dictionary<SamplingKey, double>();
        foreach (var line in parameters.GetString()!.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && SamplingField.ByWire(parts[0]) is { } field
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                Add(values, field, value);
            }
        }

        return values.Count > 0 ? new ServerSampling(values, ServerSamplingSource.OllamaShow) : null;
    });

    /// <summary>A model card's <c>generation_config.json</c>: its top-level numeric sampling fields.</summary>
    public static ServerSampling? ParseGenerationConfig(string? json, string repo) => Parse(json, root =>
    {
        var values = Numbers(root);
        return values.Count > 0 ? new ServerSampling(values, ServerSamplingSource.HuggingFace, repo) : null;
    });

    private static ServerSampling? Parse(string? json, Func<JsonElement, ServerSampling?> read)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? read(document.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every numeric property of <paramref name="element"/> that names a sampling field.</summary>
    private static Dictionary<SamplingKey, double> Numbers(JsonElement element)
    {
        var values = new Dictionary<SamplingKey, double>();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && SamplingField.ByWire(property.Name) is { } field && property.Value.TryGetDouble(out double value))
            {
                Add(values, field, value);
            }
        }

        return values;
    }

    /// <summary>The value rounded to 6 places (float32 noise: <c>0.949999988079071</c> is 0.95), kept only in its field's range; the first of a key wins.</summary>
    private static void Add(Dictionary<SamplingKey, double> values, SamplingField field, double value)
    {
        double rounded = Math.Round(value, 6);
        if (field.Accepts(rounded))
        {
            values.TryAdd(field.Key, rounded);
        }
    }
}
