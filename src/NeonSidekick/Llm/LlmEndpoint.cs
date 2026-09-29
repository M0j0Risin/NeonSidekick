namespace NeonSidekick.Llm;

/// <summary>
/// Where the chat client posts: an OpenAI-compatible base URL ending in <c>/v1</c>, the model id
/// sent with every request, and the bearer token. <see cref="Source"/> is a human phrase for the
/// status line ("configured", "first listed", "probed http://127.0.0.1:1234/v1"), never parsed.
/// <see cref="PublishedContextLength"/> is the model's context window when the server's
/// <c>/v1/models</c> list carried one (vLLM's and SGLang's <c>max_model_len</c>), else null and
/// <see cref="ContextLengthProbe"/> asks the native endpoints.
/// </summary>
public sealed record LlmEndpoint(Uri BaseUrl, string ModelId, string ApiKey, string Source, ContextLength? PublishedContextLength = null)
{
    /// <summary>
    /// The model id sent when neither the settings nor the server named one. llama.cpp ignores
    /// the name; a server that routes on it (LM Studio, Ollama, vLLM) answers "model not found",
    /// which is at least a clear message.
    /// </summary>
    public const string FallbackModelId = "local-model";

    /// <summary>The key sent when the configured one is blank; keyless local servers accept anything non-empty.</summary>
    public const string DefaultApiKey = "empty";

    /// <summary>
    /// Where requests really go when <see cref="BaseUrl"/> is a sentinel (2026-09-29, the local model: its base URL is
    /// <see cref="LocalLlm.LocalEndpoint.BaseUrl"/>, which names it everywhere the user sees it, while the server runs on
    /// a loopback port chosen at each start). Null for every other server.
    /// </summary>
    public Uri? LiveUrl { get; init; }

    /// <summary>The base URL requests are posted to: <see cref="LiveUrl"/> when set, else <see cref="BaseUrl"/>.</summary>
    public Uri WireUrl => LiveUrl ?? BaseUrl;

    /// <summary>
    /// The <c>LLM API key</c> a request carries (2026-09-28, the key kept DPAPI-encrypted in the profile like the other two,
    /// the user's call): a <c>dpapi:</c> value decrypted (<see cref="Settings.SettingsSecrets.Reveal"/>), a plain one — a
    /// variable, the <see cref="DefaultApiKey"/> placeholder, a machine where DPAPI failed — as it is. An encrypted key this
    /// Windows user cannot read (another's profile) is <see cref="DefaultApiKey"/>, said in the log: the server's 401 then says the rest.
    /// </summary>
    public static string KeyOf(Settings.AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        string stored = effective.LlmApiKey ?? "";
        if (!Sql.WindowsCredentials.IsProtected(stored.Trim()))
        {
            return stored;
        }

        if (Settings.SettingsSecrets.Reveal(stored) is { } key)
        {
            return key;
        }

        Diagnostics.DiagnosticLog.Warn("Llm", "The saved LLM API key could not be decrypted (another Windows user's or machine's profile?); sending none. Type it again in /settings.");
        return DefaultApiKey;
    }

    /// <summary>
    /// Parses and normalises a user-supplied base URL. Requires an absolute http(s) URI:
    /// <c>UriBuilder</c> happily parses <c>localhost:8080</c> with <c>localhost</c> as the scheme
    /// and produces a nonsense endpoint. Throws <see cref="ArgumentException"/> on anything else.
    /// </summary>
    public static Uri NormalizeBaseUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("Base URL must not be empty.", nameof(raw));
        }

        var trimmed = raw.Trim();
        if (string.Equals(trimmed, LocalLlm.LocalEndpoint.Alias, StringComparison.OrdinalIgnoreCase))
        {
            // The local model (2026-09-29): "local" wherever a URL is typed stands for its sentinel.
            return LocalLlm.LocalEndpoint.BaseUrl;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Base URL must be an absolute http or https URI. Received: '{trimmed}'.", nameof(raw));
        }

        return NormalizeBaseUrl(parsed);
    }

    /// <summary>
    /// Returns <paramref name="parsed"/> with its path ending in exactly one <c>/v1</c>.
    ///
    /// <para>Shared by the client and the probe so the two cannot disagree.</para>
    /// </summary>
    public static Uri NormalizeBaseUrl(Uri parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        // Trim the trailing slash *before* the check — "/v1/" would otherwise fail
        // EndsWith("/v1") and yield "/v1/v1", 404ing every request.
        var builder = new UriBuilder(parsed);
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path += "/v1";
        }

        builder.Path = path;
        return builder.Uri;
    }

    /// <summary>
    /// <c>{v1}/models</c>, built with an explicit slash. A relative <see cref="Uri"/> resolved
    /// against a base of <c>/v1</c> <em>replaces</em> the last segment and asks for <c>/models</c>.
    /// </summary>
    public static Uri ModelsUrl(Uri v1Base)
    {
        ArgumentNullException.ThrowIfNull(v1Base);
        return new Uri(v1Base.AbsoluteUri.TrimEnd('/') + "/models");
    }

    /// <summary>
    /// The server root above a <c>/v1</c> base — where LM Studio's <c>/api/v0</c>, llama.cpp's
    /// <c>/props</c> and Ollama's <c>/api</c> live. Everything else about the URI is kept.
    /// </summary>
    public static Uri RootUrl(Uri v1Base)
    {
        ArgumentNullException.ThrowIfNull(v1Base);
        var builder = new UriBuilder(v1Base);
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
        }

        builder.Path = path.Length == 0 ? "/" : path;
        return builder.Uri;
    }
}
