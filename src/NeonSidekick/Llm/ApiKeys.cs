using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Llm.OpenAIPlatform;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm;

/// <summary>
/// Which key a request carries (2026-10-03, out of <c>ClaudeApi.KeyFor</c> when the OpenAI API joined): the Anthropic API's
/// own for its host, the OpenAI API's own for its host (each empty when none — the API then answers 401, which says so), and
/// the <c>LLM API key</c> for every other server. Switching servers must never send one's key to another.
/// </summary>
public static class ApiKeys
{
    /// <summary>The key a request to <paramref name="baseUrl"/> carries.</summary>
    public static string For(AppSettingsData effective, Uri? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (ClaudeApi.IsClaudeApi(baseUrl))
        {
            return ClaudeApi.Key(effective) ?? "";
        }

        return OpenAIApi.IsOpenAIApi(baseUrl) ? OpenAIApi.Key(effective) ?? "" : LlmEndpoint.KeyOf(effective);
    }

    /// <summary>Whether <paramref name="baseUrl"/> is a paid hosted API (the Anthropic API or the OpenAI API), not a local server.</summary>
    public static bool IsHostedApi(Uri? baseUrl) => ClaudeApi.IsClaudeApi(baseUrl) || OpenAIApi.IsOpenAIApi(baseUrl);
}
