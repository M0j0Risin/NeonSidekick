using System.Runtime.InteropServices;

namespace NeonSidekick.LocalLlm;

/// <summary>
/// The local model as one more <c>/server</c> choice (2026-09-29, the user's ask: a small model the app downloads from
/// Hugging Face and runs itself on llama.cpp's <c>llama-server</c>, beside the servers the scan finds). As with the
/// Claude API (<see cref="Llm.Anthropic.ClaudeApi"/>), the provider is told by the URL alone: a saved
/// <see cref="Settings.AppSettingsData.LlmUrl"/> on <see cref="Host"/> is the local model, and
/// <see cref="Settings.AppSettingsData.LlmModel"/> holds its catalog id (<see cref="LocalModelCatalog"/>). So no second
/// URL setting exists, and a profile switch, <c>/server</c>, <c>--url local</c> and <c>NEONSIDEKICK_LLM_URL=local</c>
/// all route it the same way.
///
/// <para>The URL is a sentinel, never a place: the server runs on a loopback port chosen at each start, which the
/// endpoint carries as <see cref="Llm.LlmEndpoint.LiveUrl"/>. <c>.invalid</c> is reserved (RFC 2606) and never
/// resolves, so a code path that posts to the sentinel by mistake fails fast with a DNS error instead of reaching
/// anything on the network.</para>
/// </summary>
public static class LocalEndpoint
{
    /// <summary>The sentinel's host; a base URL on it is the local model.</summary>
    public const string Host = "local-llm.invalid";

    /// <summary>The base URL saved for the local model, already <c>/v1</c>-normalised like every other.</summary>
    public static readonly Uri BaseUrl = new("http://" + Host + "/v1");

    /// <summary>The word that stands for <see cref="BaseUrl"/> wherever a URL is typed (<c>/server local</c>, <c>--url local</c>, the settings row).</summary>
    public const string Alias = "local";

    /// <summary>The server picker's name for the rows (fits <c>SettingsMenu.ServerNameWidth</c>). Pinned.</summary>
    public const string ServerName = "Local";

    /// <summary>Whether <paramref name="baseUrl"/> is the local model.</summary>
    public static bool IsLocal(Uri? baseUrl) =>
        baseUrl is not null && string.Equals(baseUrl.Host, Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="url"/> (a saved setting, possibly blank, the alias or malformed) names the local model.</summary>
    public static bool IsLocal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string trimmed = url.Trim();
        return string.Equals(trimmed, Alias, StringComparison.OrdinalIgnoreCase)
            || (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) && IsLocal(parsed));
    }

    /// <summary>
    /// Whether the local model is offered at all: llama.cpp's Windows x64 builds are the ones pinned
    /// (<see cref="LlamaRelease"/>), so another OS or an Arm64 machine sees no local rows.
    /// </summary>
    public static bool Offered => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;
}
