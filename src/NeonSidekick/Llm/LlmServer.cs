using System.Globalization;

namespace NeonSidekick.Llm;

/// <summary>
/// One OpenAI-compatible server the probe found: where it is, what to call it, and what its
/// <c>/v1/models</c> said. The server picker's row and the endpoint's raw material.
/// </summary>
/// <param name="BaseUrl">The base URL, ending in <c>/v1</c>.</param>
/// <param name="Name">A product name (<see cref="NameFor"/>), never parsed.</param>
/// <param name="Result">The probe's answer; <see cref="ProbeResult.Exists"/> is true for a discovered server.</param>
public sealed record LlmServer(Uri BaseUrl, string Name, ProbeResult Result)
{
    /// <summary>What lives on each candidate port by convention.</summary>
    public static readonly IReadOnlyDictionary<int, string> PortNames = new Dictionary<int, string>
    {
        [1234] = "LM Studio",
        [8000] = "vLLM",
        [30000] = "SGLang",
        [8080] = "llama.cpp",
        [11434] = "Ollama",
        [8888] = "Unsloth",
    };

    /// <summary>
    /// The <c>owned_by</c> each server writes into its own model list — what names a server on a
    /// port that is not its usual one.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> OwnerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["organization_owner"] = "LM Studio",
        ["vllm"] = "vLLM",
        ["sglang"] = "SGLang",
        ["llamacpp"] = "llama.cpp",
        ["library"] = "Ollama",
    };

    /// <summary>
    /// The server's own <c>owned_by</c> when it is one we recognise, else the port's conventional
    /// owner, else <c>host:port</c>. Pinned by tests.
    /// </summary>
    public static string NameFor(Uri baseUrl, string? ownedBy)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (Anthropic.ClaudeApi.IsClaudeApi(baseUrl))
        {
            return Anthropic.ClaudeApi.ServerName;
        }

        if (Claude.ClaudeCliEndpoint.IsClaudeCli(baseUrl))
        {
            return Claude.ClaudeCliEndpoint.ServerName;
        }

        if (ownedBy is not null && OwnerNames.TryGetValue(ownedBy.Trim(), out var byOwner))
        {
            return byOwner;
        }

        if (PortNames.TryGetValue(baseUrl.Port, out var byPort))
        {
            return byPort;
        }

        return baseUrl.Host + ":" + baseUrl.Port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A server from a probe answer, named by <see cref="NameFor"/>.</summary>
    public static LlmServer From(Uri baseUrl, ProbeResult result) => new(baseUrl, NameFor(baseUrl, result.OwnedBy), result);
}
