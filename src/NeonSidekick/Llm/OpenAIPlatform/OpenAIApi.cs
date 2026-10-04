using System.Text.Json;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.OpenAIPlatform;

/// <summary>
/// The OpenAI API as one more <c>/server</c> choice (2026-10-03, the user's ask: OpenAI's own API "similar to how we
/// implemented support for Claude API", offered only while the <c>OpenAI API</c> switch is on and a key is set). As with
/// <see cref="Anthropic.ClaudeApi"/>, the provider is told by the URL alone — a saved <see cref="AppSettingsData.LlmUrl"/>
/// on <see cref="Host"/> is the OpenAI API — and its key is its own (<see cref="AppSettingsData.OpenAIApiKey"/>, DPAPI in
/// the profile), never the <c>LLM API key</c> a local server gets nor the Claude API's (<see cref="ApiKeys.For"/>). Spoken
/// to over the Responses API (<see cref="OpenAIApiChatClient"/>; Chat Completions for the first cut that day, until the live
/// sweep found it refuses tools beside reasoning on GPT-5.4 and newer).
/// </summary>
public static class OpenAIApi
{
    /// <summary>The API's host; a base URL on it is the OpenAI API.</summary>
    public const string Host = "api.openai.com";

    /// <summary>The base URL <c>/server</c> saves for the OpenAI API, already <c>/v1</c>-normalised like every other.</summary>
    public static readonly Uri BaseUrl = new("https://" + Host + "/v1");

    /// <summary>The server picker's name for the row (fits <c>SettingsMenu.ServerNameWidth</c>). Pinned.</summary>
    public const string ServerName = "OpenAI API";

    /// <summary>The header naming the organization a request is billed to (<see cref="AppSettingsData.OpenAIApiOrganization"/>).</summary>
    public const string OrganizationHeader = "OpenAI-Organization";

    /// <summary>The header naming the project a request is billed to (<see cref="AppSettingsData.OpenAIApiProject"/>).</summary>
    public const string ProjectHeader = "OpenAI-Project";

    /// <summary><c>{v1}/responses</c>.</summary>
    public static Uri ResponsesUrl(Uri v1Base)
    {
        ArgumentNullException.ThrowIfNull(v1Base);
        return new Uri(v1Base.AbsoluteUri.TrimEnd('/') + "/responses");
    }

    /// <summary>The Bearer key and, when set, the organization and project headers on one request.</summary>
    public static void AddHeaders(HttpRequestMessage request, string? apiKey, string? organization, string? project)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
        }

        if (!string.IsNullOrWhiteSpace(organization))
        {
            request.Headers.TryAddWithoutValidation(OrganizationHeader, organization.Trim());
        }

        if (!string.IsNullOrWhiteSpace(project))
        {
            request.Headers.TryAddWithoutValidation(ProjectHeader, project.Trim());
        }
    }

    /// <summary>Whether <paramref name="baseUrl"/> is the OpenAI API.</summary>
    public static bool IsOpenAIApi(Uri? baseUrl) =>
        baseUrl is not null && string.Equals(baseUrl.Host, Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="url"/> (a saved setting, possibly blank or malformed) names the OpenAI API.</summary>
    public static bool IsOpenAIApi(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) && IsOpenAIApi(parsed);

    /// <summary>
    /// The key in <paramref name="effective"/>, readable: a <c>dpapi:</c> value decrypted, a plain one as it is; null when none
    /// is set or the stored one cannot be read (another user's or machine's profile), which reads as "no key".
    /// </summary>
    public static string? Key(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return SettingsSecrets.Reveal(effective.OpenAIApiKey);
    }

    /// <summary>Whether the OpenAI API is offered: the switch on and a key that reads.</summary>
    public static bool Offered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.OpenAIApi && Key(effective) is not null;
    }

    /// <summary>The organization header's value, or null for none.</summary>
    public static string? Organization(AppSettingsData effective) =>
        string.IsNullOrWhiteSpace(effective?.OpenAIApiOrganization) ? null : effective.OpenAIApiOrganization.Trim();

    /// <summary>The project header's value, or null for none.</summary>
    public static string? Project(AppSettingsData effective) =>
        string.IsNullOrWhiteSpace(effective?.OpenAIApiProject) ? null : effective.OpenAIApiProject.Trim();

    /// <summary>
    /// The organization and project headers a request to <paramref name="baseUrl"/> carries: the settings' for the OpenAI API
    /// (each only when set), none for any other server — a local one is never told the account's organization.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> HeadersFor(AppSettingsData effective, Uri? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (!IsOpenAIApi(baseUrl))
        {
            return [];
        }

        var headers = new List<KeyValuePair<string, string>>(2);
        if (Organization(effective) is { } organization)
        {
            headers.Add(new(OrganizationHeader, organization));
        }

        if (Project(effective) is { } project)
        {
            headers.Add(new(ProjectHeader, project));
        }

        return headers;
    }

    /// <summary>
    /// The chat models of the API's <c>/v1/models</c> answer (<see cref="OpenAIModelRules.IsChatModel"/>), newest first by their
    /// <c>created</c> time, then by id: the account's list holds every kind of model, in no useful order.
    /// <see cref="JsonDocument"/> parsing, no serializer context.
    /// </summary>
    public static IReadOnlyList<string> ChatModels(string modelsJson)
    {
        var models = new List<(string Id, long Created)>();
        if (string.IsNullOrWhiteSpace(modelsJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(modelsJson);
            if (!document.RootElement.TryGetProperty("data"u8, out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("id"u8, out var id) && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 } name && OpenAIModelRules.IsChatModel(name)
                    && !models.Exists(m => m.Id == name))
                {
                    long created = entry.TryGetProperty("created"u8, out var time) && time.ValueKind == JsonValueKind.Number && time.TryGetInt64(out long seconds) ? seconds : 0;
                    models.Add((name, created));
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return models.OrderByDescending(m => m.Created).ThenBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Id).ToList();
    }

    /// <summary>The key as the settings file keeps it: DPAPI-encrypted for this Windows user (<see cref="SettingsSecrets.Protect"/>).</summary>
    public static string Protect(string plain, out string? error) => SettingsSecrets.Protect(plain, out error);
}
