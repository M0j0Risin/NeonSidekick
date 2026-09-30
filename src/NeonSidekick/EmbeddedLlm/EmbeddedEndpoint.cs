using System.Runtime.InteropServices;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// The embedded model as one more <c>/server</c> choice (2026-09-29, the user's ask: a small model the app downloads from
/// Hugging Face and runs itself on llama.cpp's <c>llama-server</c>, beside the servers the scan finds). As with the
/// Claude API (<see cref="Llm.Anthropic.ClaudeApi"/>), the provider is told by the URL alone: a saved
/// <see cref="Settings.AppSettingsData.LlmUrl"/> on <see cref="Host"/> is the embedded model, and
/// <see cref="Settings.AppSettingsData.LlmModel"/> holds its catalog id (<see cref="EmbeddedModelCatalog"/>). So no second
/// URL setting exists, and a profile switch, <c>/server</c>, <c>--url embedded</c> and <c>NEONSIDEKICK_LLM_URL=embedded</c>
/// all route it the same way.
///
/// <para>The URL is a sentinel, never a place: the server runs on a loopback port chosen at each start, which the
/// endpoint carries as <see cref="Llm.LlmEndpoint.LiveUrl"/>. <c>.localhost</c> is reserved (RFC 6761) for this machine
/// alone, which is where the server runs, so a code path that posts to the sentinel by mistake stays on loopback and
/// never reaches the network. <c>embedded-llm.invalid</c> (RFC 2606's never-resolving name) until later that day, when
/// the user asked for a word that reads better in the connected line and the saved profile; no synonym was kept (a
/// profile saved on the old one falls to the server picker once, the user's call).</para>
/// </summary>
public static class EmbeddedEndpoint
{
    /// <summary>The sentinel's host; a base URL on it is the embedded model. Pinned.</summary>
    public const string Host = "embedded.localhost";

    /// <summary>The base URL saved for the embedded model, already <c>/v1</c>-normalised like every other.</summary>
    public static readonly Uri BaseUrl = new("http://" + Host + "/v1");

    /// <summary>The word that stands for <see cref="BaseUrl"/> wherever a URL is typed (<c>/server embedded</c>, <c>--url embedded</c>, the settings row).</summary>
    public const string Alias = "embedded";

    /// <summary>The server picker's name for the rows (fits <c>SettingsMenu.ServerNameWidth</c>). Pinned.</summary>
    public const string ServerName = "Embedded";

    /// <summary>Whether <paramref name="baseUrl"/> is the embedded model.</summary>
    public static bool IsEmbedded(Uri? baseUrl) =>
        baseUrl is not null && string.Equals(baseUrl.Host, Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="url"/> (a saved setting, possibly blank, the alias or malformed) names the embedded model.</summary>
    public static bool IsEmbedded(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string trimmed = url.Trim();
        return string.Equals(trimmed, Alias, StringComparison.OrdinalIgnoreCase)
            || (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) && IsEmbedded(parsed));
    }

    /// <summary>
    /// Whether the embedded model is offered at all: llama.cpp's Windows x64 builds are the ones pinned
    /// (<see cref="LlamaRelease"/>), so another OS or an Arm64 machine sees no embedded rows.
    /// </summary>
    public static bool Offered => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

    /// <summary>
    /// Whether <paramref name="effective"/> has the embedded model switched off (<c>Embedded LLM server enabled</c>, 2026-09-29, the
    /// user's ask) while its URL names it: the saved sentinel then stands for nothing, and a connect finds a server as a
    /// blank URL would — the same as a saved Claude API URL with the Claude API off.
    /// </summary>
    public static bool SwitchedOff(Settings.AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return !effective.EmbeddedLlmServer && IsEmbedded(effective.LlmUrl);
    }

    /// <summary>Whether <paramref name="effective"/>'s URL names the embedded model and the switch lets it run.</summary>
    public static bool Chosen(Settings.AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.EmbeddedLlmServer && IsEmbedded(effective.LlmUrl);
    }
}
