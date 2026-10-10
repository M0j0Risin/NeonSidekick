using NeonSidekick.Settings;

namespace NeonSidekick.Advisors;

/// <summary>What an advisor's saved profile answers on: its server's kind (<see cref="AdvisorServer.Kind"/>) and its model.</summary>
public sealed record AdvisorServerFacts(string Kind, string Model);

/// <summary>
/// An advisor's server and model as the lists name them (2026-10-10, the user's ask: <c>Advisor list</c>'s band, its editor and a bare
/// <c>/advisor</c> say what each advisor runs on, one advisor a line): the kind read from the saved <c>LLM URL</c> the way the session
/// tells its providers apart (the sentinels first, then the two hosted APIs), and the saved <c>LLM model</c>. A plain URL is
/// <see cref="Http"/>, not its host (the user's pick). Pure, but for <see cref="Read"/>.
/// </summary>
public static class AdvisorServer
{
    /// <summary>The Claude Code server. Pinned.</summary>
    public const string ClaudeCode = "claudecode";

    /// <summary>The embedded model. Pinned.</summary>
    public const string Embedded = "embedded";

    /// <summary>A chosen Docker container. Pinned.</summary>
    public const string Docker = "docker";

    /// <summary>The Anthropic API. Pinned.</summary>
    public const string Anthropic = "anthropic";

    /// <summary>The OpenAI API. Pinned.</summary>
    public const string OpenAI = "openai";

    /// <summary>Any other OpenAI-compatible server by URL. Pinned.</summary>
    public const string Http = "http";

    /// <summary>A blank URL: the profile scans for a server when it loads. Pinned.</summary>
    public const string Scan = "scan";

    /// <summary>A blank model on a server that picks its own (the first it lists). Pinned.</summary>
    public const string DefaultModel = "default model";

    /// <summary>The kind of server <paramref name="url"/> (a saved <c>LLM URL</c>, possibly blank, an alias or malformed) names.</summary>
    public static string Kind(string? url) =>
        Claude.ClaudeCliEndpoint.IsClaudeCli(url) ? ClaudeCode
        : EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(url) ? Embedded
        : NeonSidekick.Docker.DockerEndpoint.IsDocker(url) ? Docker
        : Llm.Anthropic.ClaudeApi.IsClaudeApi(url) ? Anthropic
        : Llm.OpenAIPlatform.OpenAIApi.IsOpenAIApi(url) ? OpenAI
        : string.IsNullOrWhiteSpace(url) ? Scan
        : Http;

    /// <summary>The model <paramref name="saved"/> answers on: its <c>LLM model</c>; blank is <c>sonnet</c> on Claude Code (as the CLI is started), else <see cref="DefaultModel"/>.</summary>
    public static string Model(AppSettingsData saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (Claude.ClaudeCliEndpoint.IsClaudeCli(saved.LlmUrl))
        {
            return Claude.ClaudeCliEndpoint.ModelOf(saved.LlmModel);
        }

        return string.IsNullOrWhiteSpace(saved.LlmModel) ? DefaultModel : saved.LlmModel.Trim();
    }

    /// <summary>The kind and model of <paramref name="saved"/>.</summary>
    public static AdvisorServerFacts Of(AppSettingsData saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return new AdvisorServerFacts(Kind(saved.LlmUrl), Model(saved));
    }

    /// <summary>
    /// <paramref name="profile"/>'s facts as saved under <paramref name="home"/>: null when it is not there or its <c>profile.json</c>
    /// cannot be read (a corrupt one is never overwritten here, only not described).
    /// </summary>
    public static AdvisorServerFacts? Read(string home, string profile)
    {
        try
        {
            return Profiles.Resolve(home, profile) is { } onDisk ? Of(Profiles.ReadProfileFile(Profiles.ProfileFile(home, onDisk))) : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
