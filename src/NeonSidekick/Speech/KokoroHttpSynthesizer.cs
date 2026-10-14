using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;

namespace NeonSidekick.Speech;

/// <summary>
/// <see cref="ISpeechSynthesizer"/> over a Kokoro-FastAPI server: <c>POST {v1}/audio/speech</c>
/// with <c>response_format=pcm</c> streams raw 24 kHz 16-bit mono PCM, <c>GET {v1}/audio/voices</c>
/// lists voices.
///
/// <para>Follows <see cref="LlmEndpointProbe"/>: the transport is injectable (tests pass one over
/// a stub handler; an owned one is disposed here), every call has its own linked-CTS ceiling,
/// and nothing throws except the caller's own cancellation. The per-call ceiling is a local
/// linked CTS by design — the "never a linked CTS" rule is about the turn budget, where
/// cancellation means barge-in.</para>
/// </summary>
public sealed class KokoroHttpSynthesizer : ISpeechSynthesizer
{
    private const string Category = "Speech";

    public const string ModelName = "kokoro";
    public const string ResponseFormat = "pcm";

    /// <summary>Same reasoning as the LLM probe: a real HTTP round trip on a server that may be busy.</summary>
    public static readonly TimeSpan DefaultListTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Per chunk. A sentence is at most 240 characters (the chunker's run-on limit), which a CPU
    /// Kokoro renders in a few seconds; thirty is generous without letting a hung server stall
    /// the end of a turn for long.
    /// </summary>
    public static readonly TimeSpan DefaultSynthesisTimeout = TimeSpan.FromSeconds(30);

    /// <summary>100 ms of audio per read, so playback can start before the sentence has finished rendering.</summary>
    private const int ReadBufferBytes = 4800;

    private readonly HttpClient _http;
    private readonly HttpClient? _ownedHttpClient;

    /// <param name="baseUrl">The server; normalised to end in <c>/v1</c>.</param>
    /// <param name="httpClient">Optional transport, the caller's to dispose. Null creates and owns one.</param>
    /// <param name="listTimeout">Ceiling for <see cref="ListVoicesAsync"/>; defaults to <see cref="DefaultListTimeout"/>.</param>
    /// <param name="synthesisTimeout">Ceiling per <see cref="SynthesizeAsync"/> call; defaults to <see cref="DefaultSynthesisTimeout"/>.</param>
    public KokoroHttpSynthesizer(Uri baseUrl, HttpClient? httpClient = null, TimeSpan? listTimeout = null, TimeSpan? synthesisTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        // The string overload is the one that insists on http(s).
        BaseUrl = LlmEndpoint.NormalizeBaseUrl(baseUrl.AbsoluteUri);
        AppliedListTimeout = listTimeout ?? DefaultListTimeout;
        AppliedSynthesisTimeout = synthesisTimeout ?? DefaultSynthesisTimeout;
        if (AppliedListTimeout <= TimeSpan.Zero || AppliedSynthesisTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(synthesisTimeout), "Timeouts must be positive.");
        }

        if (httpClient is not null)
        {
            _http = httpClient;
        }
        else
        {
            // The per-call budgets below are the ceilings; the client's own timeout would only
            // race them, and it also covers content reads, which a streamed response outlives.
            _ownedHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _http = _ownedHttpClient;
        }
    }

    /// <summary>The server, normalised to end in <c>/v1</c>.</summary>
    public Uri BaseUrl { get; }

    public PcmFormat Format => PcmFormat.Kokoro;

    /// <summary>The GET is the probe: a server that lists voices is a server that speaks.</summary>
    public Task<VoiceListResult> PrepareAsync(CancellationToken cancellationToken) => ListVoicesAsync(cancellationToken);

    /// <summary>The ceilings in force — "saved" and "in force" are different claims.</summary>
    internal TimeSpan AppliedListTimeout { get; }

    internal TimeSpan AppliedSynthesisTimeout { get; }

    internal bool OwnsTransport => _ownedHttpClient is not null;

    /// <summary><c>{v1}/audio/speech</c>, built with an explicit slash (a relative Uri would replace the <c>/v1</c> segment).</summary>
    public static Uri SpeechUrl(Uri v1Base) => new(LlmEndpoint.NormalizeBaseUrl(v1Base).AbsoluteUri.TrimEnd('/') + "/audio/speech");

    /// <summary><c>{v1}/audio/voices</c>.</summary>
    public static Uri VoicesUrl(Uri v1Base) => new(LlmEndpoint.NormalizeBaseUrl(v1Base).AbsoluteUri.TrimEnd('/') + "/audio/voices");

    /// <summary>The request body, through the source-generated context. Pinned by a test.</summary>
    public static string RequestJson(string text, string voice, double speed) =>
        JsonSerializer.Serialize(
            new KokoroSpeechRequest { Input = text, Voice = voice, Speed = speed },
            SpeechJsonContext.Default.KokoroSpeechRequest);

    /// <summary>
    /// Voice ids from a <c>/v1/audio/voices</c> payload: <c>{"voices":[...]}</c> whose entries are
    /// strings or objects with an <c>id</c> (or <c>name</c>); a bare array is accepted too.
    /// Distinct, sorted ordinally. <see cref="JsonDocument"/> needs no serializer context.
    /// </summary>
    public static IReadOnlyList<string> ParseVoices(string voicesJson)
    {
        var voices = new SortedSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(voicesJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(voicesJson);
            var root = document.RootElement;
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("voices", out array) || array.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            foreach (var entry in array.EnumerateArray())
            {
                string? id = entry.ValueKind switch
                {
                    JsonValueKind.String => entry.GetString(),
                    JsonValueKind.Object when entry.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String => idProperty.GetString(),
                    JsonValueKind.Object when entry.TryGetProperty("name", out var nameProperty) && nameProperty.ValueKind == JsonValueKind.String => nameProperty.GetString(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(id))
                {
                    voices.Add(id.Trim());
                }
            }
        }
        catch (JsonException)
        {
            // A server that answers 200 with a non-JSON body still exists; it just listed nothing.
        }

        return voices.ToArray();
    }

    public async Task<VoiceListResult> ListVoicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(AppliedListTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, VoicesUrl(BaseUrl));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, budget.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                var voices = ParseVoices(body);
                return new VoiceListResult(true, voices, voices.Count == 1 ? "1 voice" : $"{voices.Count.ToString(CultureInfo.InvariantCulture)} voices");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new VoiceListResult(true, Array.Empty<string>(), $"{(int)response.StatusCode} on /v1/audio/voices; the server wants a key");
            }

            return VoiceListResult.Missing($"HTTP {(int)response.StatusCode} on /v1/audio/voices");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return VoiceListResult.Missing("canceled");
        }
        catch (OperationCanceledException)
        {
            return VoiceListResult.Missing($"no answer within {LlmTimeouts.Format(AppliedListTimeout)}");
        }
        catch (Exception ex)
        {
            return VoiceListResult.Missing(ex.Message);
        }
    }

    public async Task<SynthesisResult> SynthesizeAsync(string text, string voice, double speed, Action<byte[], int> pcmSink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pcmSink);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SynthesisResult(true, 0, "nothing to say");
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(AppliedSynthesisTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, SpeechUrl(BaseUrl))
            {
                Content = new StringContent(RequestJson(text, voice, speed), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Accept", "audio/pcm");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                string snippet = body.Length > 160 ? body[..160] + "…" : body;
                return SynthesisResult.Failed($"HTTP {(int)response.StatusCode} on /v1/audio/speech" + (snippet.Length > 0 ? ": " + snippet.Trim() : ""));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            long total = await PumpAlignedAsync(stream, pcmSink, budget.Token).ConfigureAwait(false);
            return new SynthesisResult(true, total, $"{total.ToString(CultureInfo.InvariantCulture)} bytes");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return SynthesisResult.Failed($"no answer within {LlmTimeouts.Format(AppliedSynthesisTimeout)}");
        }
        catch (Exception ex)
        {
            return SynthesisResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Reads the response in 100 ms pieces and hands the sink only whole 16-bit samples. A chunk
    /// boundary can fall between the two bytes of a sample; the odd byte is carried into the next
    /// read rather than handed over, so playback never starts a buffer half a sample out of phase.
    /// </summary>
    private static async Task<long> PumpAlignedAsync(Stream stream, Action<byte[], int> pcmSink, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferBytes];
        int carry = 0;
        long total = 0;

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            int available = carry + read;
            int even = available & ~1;
            if (even > 0)
            {
                pcmSink(buffer, even);
                total += even;
            }

            carry = available - even;
            if (carry == 1)
            {
                buffer[0] = buffer[even];
            }
        }

        if (carry == 1)
        {
            DiagnosticLog.Debug(Category, "The speech stream ended on a half sample; dropped one byte.");
        }

        return total;
    }

    public void Dispose() => _ownedHttpClient?.Dispose();
}
