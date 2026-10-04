using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Anthropic;

/// <summary>
/// The Anthropic API as one more <c>/server</c> choice (2026-09-27, the user's ask: Anthropic's Messages API beside the
/// local OpenAI-compatible servers, offered only while the <c>Anthropic API</c> switch is on and a key is set). The
/// provider is told by the URL alone — a saved <see cref="AppSettingsData.LlmUrl"/> on <see cref="Host"/> is the Claude
/// API, anything else the OpenAI-compatible path — so no second URL setting exists and a profile switch, <c>/server</c>
/// and <c>--url</c> all route it the same way. Its key is its own (<see cref="AppSettingsData.AnthropicApiKey"/>, DPAPI in
/// the profile), never the <c>LLM API key</c> a local server gets: switching servers must not send one's key to the other.
/// </summary>
public static class ClaudeApi
{
    /// <summary>The API's host; a base URL on it is the Anthropic API.</summary>
    public const string Host = "api.anthropic.com";

    /// <summary>The base URL <c>/server</c> saves for the Anthropic API, already <c>/v1</c>-normalised like every other.</summary>
    public static readonly Uri BaseUrl = new("https://" + Host + "/v1");

    /// <summary>The <c>anthropic-version</c> header every request carries.</summary>
    public const string Version = "2023-06-01";

    /// <summary>The server picker's name for the row (fits <c>SettingsMenu.ServerNameWidth</c>). Pinned.</summary>
    public const string ServerName = "Anthropic API";

    /// <summary>Whether <paramref name="baseUrl"/> is the Anthropic API.</summary>
    public static bool IsClaudeApi(Uri? baseUrl) =>
        baseUrl is not null && string.Equals(baseUrl.Host, Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="url"/> (a saved setting, possibly blank or malformed) names the Anthropic API.</summary>
    public static bool IsClaudeApi(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) && IsClaudeApi(parsed);

    /// <summary>
    /// <c>{v1}/models</c> with the page size at the API's ceiling: the list pages at 20 by default and every model the
    /// account can use should be one request.
    /// </summary>
    public static Uri ModelsUrl(Uri v1Base) => new(LlmEndpoint.ModelsUrl(v1Base).AbsoluteUri + "?limit=1000");

    /// <summary><c>{v1}/messages</c>.</summary>
    public static Uri MessagesUrl(Uri v1Base)
    {
        ArgumentNullException.ThrowIfNull(v1Base);
        return new Uri(v1Base.AbsoluteUri.TrimEnd('/') + "/messages");
    }

    /// <summary>The two headers that stand in for <c>Authorization: Bearer</c> on every Anthropic API request.</summary>
    public static void AddHeaders(HttpRequestMessage request, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
        }

        request.Headers.TryAddWithoutValidation("anthropic-version", Version);
    }

    /// <summary>
    /// The key in <paramref name="effective"/>, readable: a <c>dpapi:</c> value decrypted (this Windows user on this
    /// machine), a plain one (a variable, a hand edit) as it is. Null when none is set or the stored one cannot be read
    /// — another user's or machine's profile — which reads as "no key", so the row is not offered.
    /// </summary>
    public static string? Key(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return SettingsSecrets.Reveal(effective.AnthropicApiKey);
    }

    /// <summary>Whether the Anthropic API is offered: the switch on and a key that reads.</summary>
    public static bool Offered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.AnthropicApi && Key(effective) is not null;
    }

    /// <summary>
    /// The key as the settings file keeps it: <paramref name="plain"/> encrypted with DPAPI for this Windows user
    /// (<see cref="SettingsSecrets.Protect"/>); empty for empty. Where DPAPI is unavailable the plain key is kept and
    /// <paramref name="error"/> says why, so the caller can warn.
    /// </summary>
    public static string Protect(string plain, out string? error) => SettingsSecrets.Protect(plain, out error);
}
