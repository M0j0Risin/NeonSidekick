using System.Globalization;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Llm;

/// <summary>The loaded model's context window, in tokens, and the pinned phrase naming where the figure came from.</summary>
public readonly record struct ContextLength(int Tokens, string Source)
{
    /// <summary>The source phrase of a window the settings name (<c>LlmContextLength</c> or its variable), not the server.</summary>
    public const string ConfiguredSource = "configured";

    /// <summary>The figure for <see cref="ConfiguredSource"/>.</summary>
    public static ContextLength Configured(int tokens) => new(tokens, ConfiguredSource);
}

/// <summary>
/// Asks the connected server how long the loaded model's context is, so the tally can say what share
/// of it the conversation fills. No server type is detected: the tiers are tried in order and the
/// first that answers wins, each a short request against the same host —
/// <list type="number">
/// <item><c>GET {v1}/models</c>: <c>max_model_len</c> (vLLM, SGLang) or <c>context_length</c> (OpenRouter-style) on the model's entry. This tier is the endpoint probe's: it already holds the payload, and <see cref="ParseModelsWindow"/> reads it into <see cref="LlmEndpoint.PublishedContextLength"/> with no second request. <see cref="DetectAsync"/> starts at the next;</item>
/// <item>LM Studio's native <c>GET {root}/api/v0/models</c>: <c>loaded_context_length</c> (the window the runtime enforces, present while loaded), else <c>max_context_length</c>;</item>
/// <item>llama.cpp's <c>GET {root}/props</c>: <c>default_generation_settings.n_ctx</c>;</item>
/// <item>Ollama's <c>GET {root}/api/ps</c>: <c>context_length</c> of the loaded entry (the runtime window), then <c>POST {root}/api/show</c>: a <c>num_ctx</c> line in <c>parameters</c>, else the <c>*.context_length</c> of <c>model_info</c> — the model's ceiling, not always what the runtime allots.</item>
/// </list>
/// A model is matched by exact id, then by the listed id being a suffix of ours (an aliased or
/// path-prefixed name), then any entry that publishes a window. Never throws; null means unknown.
/// </summary>
public sealed class ContextLengthProbe
{
    private const string Category = "Llm";

    // The source phrases, pinned: they sit dim beside the /usage pane's Context heading.
    public const string MaxModelLenSource = "max_model_len on /v1/models";
    public const string ContextLengthSource = "context_length on /v1/models";
    public const string MaxInputTokensSource = "max_input_tokens on /v1/models";
    public const string LmStudioLoadedSource = "loaded_context_length on /api/v0/models";
    public const string LmStudioMaxSource = "max_context_length on /api/v0/models";
    public const string LlamaPropsSource = "n_ctx on /props";
    public const string OllamaPsSource = "context_length on /api/ps";
    public const string OllamaNumCtxSource = "num_ctx on /api/show";
    public const string OllamaShowSource = "context_length on /api/show";

    private readonly HttpClient _http;

    /// <param name="http">The transport; tests pass one over a stub handler. The probe applies its own per-call timeout.</param>
    /// <param name="timeout">Per-call ceiling; defaults to <see cref="LlmEndpointProbe.DefaultTimeout"/>.</param>
    public ContextLengthProbe(HttpClient http, TimeSpan? timeout = null)
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
    /// The native tiers in order, for a server whose <c>/v1/models</c> list said nothing; null when
    /// none answered with a window for <paramref name="modelId"/>.
    /// </summary>
    public async Task<ContextLength?> DetectAsync(Uri baseUrl, string modelId, string? apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(modelId);
        var v1 = LlmEndpoint.NormalizeBaseUrl(baseUrl);
        var root = LlmEndpoint.RootUrl(v1);

        var found =
            await TierAsync(Under(root, "api/v0/models"), apiKey, body => ParseLmStudioWindow(body, modelId), cancellationToken).ConfigureAwait(false)
            ?? await TierAsync(Under(root, "props"), apiKey, ParseLlamaProps, cancellationToken).ConfigureAwait(false)
            ?? await TierAsync(Under(root, "api/ps"), apiKey, body => ParseOllamaPs(body, modelId), cancellationToken).ConfigureAwait(false)
            ?? await TierAsync(Under(root, "api/show"), apiKey, ParseOllamaShow, cancellationToken, ShowBody(modelId)).ConfigureAwait(false);

        if (found is { } window)
        {
            DiagnosticLog.Info(Category, $"Context length {window.Tokens.ToString("N0", CultureInfo.InvariantCulture)} ({window.Source}) for {modelId}.");
        }
        else
        {
            DiagnosticLog.Info(Category, $"No context length published by {v1} for {modelId}.");
        }

        return found;
    }

    /// <summary>One request (<see cref="NativeRequest"/>); the parser's answer on a 2xx, null on anything else (logged at Debug). A body makes it a POST.</summary>
    private async Task<ContextLength?> TierAsync(Uri url, string? apiKey, Func<string, ContextLength?> parse, CancellationToken cancellationToken, string? body = null)
    {
        if (await NativeRequest.TextAsync(_http, url, apiKey, Timeout, Category, cancellationToken, body).ConfigureAwait(false) is not { } text)
        {
            return null;
        }

        var parsed = parse(text);
        if (parsed is null)
        {
            DiagnosticLog.Debug(Category, $"{url.AbsolutePath}: answered without a context length.");
        }

        return parsed;
    }

    /// <summary><paramref name="path"/> under the server's root (<see cref="LlmEndpoint.RootUrl"/>); shared with <see cref="ServerSamplingProbe"/>.</summary>
    internal static Uri Under(Uri root, string path) => new(root.AbsoluteUri.TrimEnd('/') + "/" + path);

    /// <summary>The <c>/api/show</c> request body, <c>{"model":"…"}</c>, written by the JSON writer so any id is escaped correctly.</summary>
    public static string ShowBody(string modelId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("model", modelId);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ── Parsers: JsonDocument (not reflection-based, so no serializer context), each null on anything unexpected ──

    /// <summary>Tier 1, over the endpoint probe's own payload: the matching <c>data[]</c> entry's <c>context_length</c>, else its <c>max_model_len</c>.</summary>
    public static ContextLength? ParseModelsWindow(string json, string modelId) =>
        ParseList(json, "data", modelId, entry =>
            Positive(entry, "context_length") is { } context ? new ContextLength(context, ContextLengthSource)
            : Positive(entry, "max_model_len") is { } max ? new ContextLength(max, MaxModelLenSource)
            : Positive(entry, "max_input_tokens") is { } input ? new ContextLength(input, MaxInputTokensSource)
            : null);

    /// <summary>Tier 2, LM Studio native: the matching entry's <c>loaded_context_length</c>, else its <c>max_context_length</c>.</summary>
    public static ContextLength? ParseLmStudioWindow(string json, string modelId) =>
        ParseList(json, "data", modelId, entry =>
            Positive(entry, "loaded_context_length") is { } loaded ? new ContextLength(loaded, LmStudioLoadedSource)
            : Positive(entry, "max_context_length") is { } max ? new ContextLength(max, LmStudioMaxSource)
            : null);

    /// <summary>Tier 3, llama.cpp: <c>default_generation_settings.n_ctx</c>.</summary>
    public static ContextLength? ParseLlamaProps(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("default_generation_settings", out var settings)
                && settings.ValueKind == JsonValueKind.Object
                && Positive(settings, "n_ctx") is { } n)
            {
                return new ContextLength(n, LlamaPropsSource);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>Tier 4a, Ollama: the loaded entry's <c>context_length</c> (matched on <c>name</c> or <c>model</c>).</summary>
    public static ContextLength? ParseOllamaPs(string json, string modelId) =>
        ParseList(json, "models", modelId, entry =>
            Positive(entry, "context_length") is { } n ? new ContextLength(n, OllamaPsSource) : null,
            "name", "model");

    /// <summary>Tier 4b, Ollama: a <c>num_ctx</c> line in <c>parameters</c>, else the first <c>*.context_length</c> of <c>model_info</c>.</summary>
    public static ContextLength? ParseOllamaShow(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.String)
            {
                foreach (var line in parameters.GetString()!.Split('\n'))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[0] == "num_ctx"
                        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                    {
                        return new ContextLength(n, OllamaNumCtxSource);
                    }
                }
            }

            if (root.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in info.EnumerateObject())
                {
                    if (property.Name.EndsWith(".context_length", StringComparison.Ordinal) && PositiveValue(property.Value) is { } n)
                    {
                        return new ContextLength(n, OllamaShowSource);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// The entry of <paramref name="listProperty"/> for <paramref name="modelId"/> — exact id, then a
    /// listed id our id ends with, then any entry <paramref name="read"/> accepts — and what it read there.
    /// </summary>
    private static ContextLength? ParseList(string json, string listProperty, string modelId, Func<JsonElement, ContextLength?> read, params string[] idProperties)
    {
        if (idProperties.Length == 0)
        {
            idProperties = ["id"];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(listProperty, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            ContextLength? exact = null, suffix = null, any = null;
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var window = read(entry);
                if (window is null)
                {
                    continue;
                }

                string? id = null;
                foreach (var name in idProperties)
                {
                    if (entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        id = value.GetString();
                        break;
                    }
                }

                if (id == modelId)
                {
                    exact ??= window;
                }
                else if (!string.IsNullOrEmpty(id) && modelId.EndsWith(id, StringComparison.Ordinal))
                {
                    suffix ??= window;
                }

                any ??= window;
            }

            return exact ?? suffix ?? any;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? Positive(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) ? PositiveValue(value) : null;

    private static int? PositiveValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n) && n > 0 && n <= int.MaxValue ? (int)n : null;
}
