using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;

namespace NeonSidekick.Comfy;

/// <summary>One picture ComfyUI made: the name it saved it under and its bytes.</summary>
public sealed record ComfyImage(string FileName, byte[] Bytes);

/// <summary>A run's outcome: the pictures, or <see cref="Error"/> (a sentence) with none. <see cref="PromptId"/> is ComfyUI's id for the job, empty when it was never queued.</summary>
public sealed record ComfyRunResult(IReadOnlyList<ComfyImage> Images, string PromptId, string? Error)
{
    public bool Ok => Error is null;

    public static ComfyRunResult Failed(string error, string promptId = "") => new([], promptId, error);
}

/// <summary>
/// ComfyUI's HTTP API, the part the image tools use (2026-09-24): <c>POST /prompt</c> queues a graph,
/// <c>GET /history/{id}</c> is polled until the job has outputs (no websocket: a poll a second is plenty for jobs
/// that take tens of seconds), <c>GET /view</c> fetches each picture, <c>POST /upload/image</c> sends an input
/// picture, <c>GET /system_stats</c> is the status line of <c>/comfy</c>.
///
/// <para>The <see cref="Speech.KokoroHttpSynthesizer"/> shape: the transport is injectable (tests pass one over a stub
/// handler; an owned one is disposed here), each call has its own linked-CTS ceiling, and nothing throws but the
/// caller's own cancellation. The server is the user's own, like the LLM's — often another machine on the LAN — so the
/// transport is a plain <see cref="HttpClient"/>, never the web tools' one that <see cref="Web.LanPolicy"/> judges.</para>
/// </summary>
public sealed class ComfyClient : IDisposable
{
    private const string Category = "Comfy";

    /// <summary>A status read's ceiling.</summary>
    public static readonly TimeSpan DefaultStatusTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long between two looks at a queued job's history.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly HttpClient? _owned;
    private readonly TimeSpan _poll;
    private readonly string _clientId = Guid.NewGuid().ToString("N");

    /// <param name="baseUrl">The server, <c>http://host:8188</c>; a trailing slash is dropped.</param>
    /// <param name="httpClient">Optional transport, the caller's to dispose. Null creates and owns one.</param>
    /// <param name="pollInterval">Between history reads; defaults to <see cref="DefaultPollInterval"/> (tests pass a millisecond).</param>
    public ComfyClient(Uri baseUrl, HttpClient? httpClient = null, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!Web.WebFetcher.IsHttp(baseUrl))
        {
            throw new ArgumentException("The ComfyUI URL must be http or https.", nameof(baseUrl));
        }

        BaseUrl = baseUrl.AbsoluteUri.TrimEnd('/');
        _poll = pollInterval ?? DefaultPollInterval;
        if (httpClient is not null)
        {
            _http = httpClient;
        }
        else
        {
            // The per-call budgets are the ceilings; the client's own timeout would only race them.
            _owned = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _http = _owned;
        }
    }

    /// <summary>The server without a trailing slash.</summary>
    public string BaseUrl { get; }

    private Uri Url(string path) => new(BaseUrl + path);

    /// <summary><c>/view?filename=…&amp;subfolder=…&amp;type=…</c>, every part escaped.</summary>
    public static string ViewPath(string fileName, string subfolder, string type) =>
        "/view?filename=" + Uri.EscapeDataString(fileName) + "&subfolder=" + Uri.EscapeDataString(subfolder) + "&type=" + Uri.EscapeDataString(type);

    /// <summary>
    /// <c>GET /system_stats</c> as one line — <c>ComfyUI 0.3.40 · Python 3.12 · NVIDIA GeForce RTX 4090 (18.2 of 24.0 GB VRAM free)</c> —
    /// or the failure as a sentence (<c>Ok</c> false). Never throws but the caller's cancellation.
    /// </summary>
    public async Task<(bool Ok, string Text)> StatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(DefaultStatusTimeout);
            using var response = await _http.GetAsync(Url("/system_stats"), budget.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, ComfyText.HttpError((int)response.StatusCode, "/system_stats", body));
            }

            return (true, ComfyText.Stats(body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (false, ComfyText.NoAnswer(LlmTimeouts.Format(DefaultStatusTimeout)));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (false, ComfyText.Unreachable(BaseUrl, ex.Message));
        }
    }

    /// <summary>
    /// The choices ComfyUI offers for one combo input — <c>/object_info/CheckpointLoaderSimple</c>'s <c>ckpt_name</c> (the
    /// checkpoints installed), <c>/object_info/KSampler</c>'s <c>sampler_name</c> and <c>scheduler</c> — for the
    /// add-workflow wizard's pickers (later on 2026-09-24). The list, or the failure as a sentence. Handles both
    /// shapes ComfyUI has used for a combo: <c>[["a","b"]]</c> and <c>["COMBO", {"options": ["a","b"]}]</c>.
    /// </summary>
    public async Task<(IReadOnlyList<string> Choices, string? Error)> ChoicesAsync(string nodeClass, string input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeClass);
        ArgumentNullException.ThrowIfNull(input);
        string endpoint = "/object_info/" + Uri.EscapeDataString(nodeClass);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(DefaultStatusTimeout);
            using var response = await _http.GetAsync(Url(endpoint), budget.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ([], ComfyText.HttpError((int)response.StatusCode, endpoint, body));
            }

            return ParseChoices(body, nodeClass, input) is { } choices ? (choices, null) : ([], ComfyText.BadAnswer(endpoint));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ([], ComfyText.NoAnswer(LlmTimeouts.Format(DefaultStatusTimeout)));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return ([], ComfyText.Unreachable(BaseUrl, ex.Message));
        }
    }

    /// <summary>A combo input's choices out of an <c>/object_info</c> answer, or null when the node or the input is not there. Pure.</summary>
    public static IReadOnlyList<string>? ParseChoices(string json, string nodeClass, string input)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(nodeClass, out var node) || !node.TryGetProperty("input", out var inputs))
            {
                return null;
            }

            foreach (string group in (string[])["required", "optional"])
            {
                if (inputs.TryGetProperty(group, out var set) && set.ValueKind == JsonValueKind.Object && set.TryGetProperty(input, out var spec)
                    && spec.ValueKind == JsonValueKind.Array && spec.GetArrayLength() > 0)
                {
                    var first = spec[0];
                    var list = first.ValueKind == JsonValueKind.Array ? first
                        : spec.GetArrayLength() > 1 && spec[1].ValueKind == JsonValueKind.Object && spec[1].TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array ? options
                        : default;
                    if (list.ValueKind == JsonValueKind.Array)
                    {
                        return list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
                    }
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>POST /upload/image</c> (multipart, <c>overwrite=true</c>): the name to put in the graph's <c>LoadImage</c> —
    /// <c>subfolder/name</c> when ComfyUI filed it under one — or null with <paramref name="error"/>'s sentence.
    /// </summary>
    public async Task<(string? Name, string? Error)> UploadAsync(byte[] bytes, string fileName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(fileName);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeOf(fileName));
            form.Add(file, "image", fileName);
            form.Add(new StringContent("true"), "overwrite");
            using var response = await _http.PostAsync(Url("/upload/image"), form, budget.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, ComfyText.HttpError((int)response.StatusCode, "/upload/image", body));
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            string sub = root.TryGetProperty("subfolder", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            return name.Length == 0 ? (null, ComfyText.BadAnswer("/upload/image")) : (sub.Length == 0 ? name : sub + "/" + name, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, ComfyText.NoAnswer(LlmTimeouts.Format(timeout)));
        }
        catch (JsonException)
        {
            return (null, ComfyText.BadAnswer("/upload/image"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (null, ComfyText.Unreachable(BaseUrl, ex.Message));
        }
    }

    /// <summary>
    /// Queues <paramref name="graph"/>, waits for its outputs within <paramref name="timeout"/> (queue wait included)
    /// and fetches every picture it saved — the <c>output</c>-type images (SaveImage), or the temporary ones
    /// (PreviewImage) when it saved none. ComfyUI's refusal (<c>node_errors</c>), an execution error, a timeout and an
    /// unreachable server are each a sentence in <see cref="ComfyRunResult.Error"/>. The caller's cancellation is
    /// rethrown after a best-effort <c>POST /interrupt</c>, so a stopped turn does not leave the GPU busy.
    /// </summary>
    public async Task<ComfyRunResult> RunAsync(JsonObject graph, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        string promptId = "";
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            var body = new JsonObject { ["prompt"] = graph.DeepClone(), ["client_id"] = _clientId };
            using (var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"))
            using (var response = await _http.PostAsync(Url("/prompt"), content, budget.Token).ConfigureAwait(false))
            {
                string text = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return ComfyRunResult.Failed(ComfyText.Refused((int)response.StatusCode, text));
                }

                promptId = ReadString(text, "prompt_id");
                if (promptId.Length == 0)
                {
                    return ComfyRunResult.Failed(ComfyText.BadAnswer("/prompt"));
                }
            }

            DiagnosticLog.Debug(Category, $"Queued prompt {promptId} on {BaseUrl}.");
            JsonElement outputs;
            while (true)
            {
                using var response = await _http.GetAsync(Url("/history/" + Uri.EscapeDataString(promptId)), budget.Token).ConfigureAwait(false);
                string text = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && TryReadHistory(text, promptId, out outputs, out string? failure))
                {
                    if (failure is not null)
                    {
                        return ComfyRunResult.Failed(failure, promptId);
                    }

                    break;
                }

                await Task.Delay(_poll, budget.Token).ConfigureAwait(false);
            }

            var images = new List<ComfyImage>();
            foreach (var (fileName, subfolder, type) in ImagesOf(outputs))
            {
                using var response = await _http.GetAsync(Url(ViewPath(fileName, subfolder, type)), budget.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string text = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                    return ComfyRunResult.Failed(ComfyText.HttpError((int)response.StatusCode, "/view", text), promptId);
                }

                images.Add(new ComfyImage(fileName, await response.Content.ReadAsByteArrayAsync(budget.Token).ConfigureAwait(false)));
            }

            return images.Count == 0 ? ComfyRunResult.Failed(ComfyText.NoPictures, promptId) : new ComfyRunResult(images, promptId, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await InterruptQuietlyAsync(promptId).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            return ComfyRunResult.Failed(ComfyText.TimedOut(LlmTimeouts.Format(timeout), promptId), promptId);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            return ComfyRunResult.Failed(ComfyText.Unreachable(BaseUrl, ex.Message), promptId);
        }
    }

    /// <summary>
    /// A queued job's cancellation, best effort — a stopped turn should not leave the GPU busy for minutes. Since 2026-09-28 (the
    /// hint row's double-click cancelling the pictures, which may still be waiting in ComfyUI's queue): <c>POST /queue</c>
    /// <c>{"delete":[id]}</c> first, so a prompt not started yet never runs, then <c>POST /interrupt</c> <c>{"prompt_id":id}</c>,
    /// which a current ComfyUI applies to that prompt alone (an older one ignores the body and stops whatever runs, as the bare
    /// <c>{}</c> before did). One budget for both.
    /// </summary>
    private async Task InterruptQuietlyAsync(string promptId)
    {
        if (promptId.Length == 0)
        {
            return;
        }

        try
        {
            using var budget = new CancellationTokenSource(DefaultStatusTimeout);
            using (var queued = new StringContent(new JsonObject { ["delete"] = new JsonArray(promptId) }.ToJsonString(), Encoding.UTF8, "application/json"))
            using (await _http.PostAsync(Url("/queue"), queued, budget.Token).ConfigureAwait(false))
            {
            }

            using var content = new StringContent(new JsonObject { ["prompt_id"] = promptId }.ToJsonString(), Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(Url("/interrupt"), content, budget.Token).ConfigureAwait(false);
            DiagnosticLog.Debug(Category, $"Interrupted prompt {promptId}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            DiagnosticLog.Debug(Category, $"Could not interrupt prompt {promptId}: {ex.Message}");
        }
    }

    /// <summary>
    /// A <c>/history/{id}</c> answer: false while the job is not in it yet (queued or running); true with its
    /// <c>outputs</c> once it is, <paramref name="failure"/> set when its status says it failed. Pure.
    /// </summary>
    public static bool TryReadHistory(string json, string promptId, out JsonElement outputs, out string? failure)
    {
        outputs = default;
        failure = null;
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(promptId, out var job) || job.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (job.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
        {
            string state = status.TryGetProperty("status_str", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            if (string.Equals(state, "error", StringComparison.OrdinalIgnoreCase))
            {
                failure = ComfyText.ExecutionError(ExecutionMessage(status));
                return true;
            }

            if (status.TryGetProperty("completed", out var done) && done.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        if (!job.TryGetProperty("outputs", out var found) || found.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        outputs = found.Clone();
        return true;
    }

    /// <summary>The <c>execution_error</c> message's node and text, from a failed job's status <c>messages</c>.</summary>
    private static string ExecutionMessage(JsonElement status)
    {
        if (status.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind == JsonValueKind.Array && message.GetArrayLength() == 2
                    && message[0].ValueKind == JsonValueKind.String && message[0].GetString() == "execution_error"
                    && message[1].ValueKind == JsonValueKind.Object)
                {
                    var detail = message[1];
                    string type = detail.TryGetProperty("node_type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                    string text = detail.TryGetProperty("exception_message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                    return (type.Length > 0 ? type + ": " : "") + text.Trim();
                }
            }
        }

        return "";
    }

    /// <summary>Every picture in a job's outputs — <c>output</c>-type first choice, else every one — as (file, subfolder, type). Pure.</summary>
    public static IReadOnlyList<(string FileName, string Subfolder, string Type)> ImagesOf(JsonElement outputs)
    {
        var all = new List<(string, string, string)>();
        if (outputs.ValueKind != JsonValueKind.Object)
        {
            return all;
        }

        foreach (var node in outputs.EnumerateObject().OrderBy(p => NodeOrder(p.Name)))
        {
            if (node.Value.ValueKind != JsonValueKind.Object || !node.Value.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var image in images.EnumerateArray())
            {
                string name = Str(image, "filename");
                if (name.Length > 0)
                {
                    all.Add((name, Str(image, "subfolder"), Str(image, "type") is { Length: > 0 } type ? type : "output"));
                }
            }
        }

        var saved = all.Where(i => string.Equals(i.Item3, "output", StringComparison.OrdinalIgnoreCase)).ToList();
        return saved.Count > 0 ? saved : all;

        static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static int NodeOrder(string id) => int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;

    private static string ReadString(string json, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string MediaTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "image/png",
    };

    public void Dispose() => _owned?.Dispose();
}
