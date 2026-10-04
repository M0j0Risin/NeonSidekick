using NeonSidekick.Settings;

namespace NeonSidekick.Claude;

/// <summary>
/// The Claude Code CLI as one more <c>/server</c> choice (2026-09-30, the user's ask: the <c>claude</c> CLI kept running as
/// an open session and used as the chat's server, beside the Anthropic API and the embedded model). As with those two
/// (<see cref="Llm.Anthropic.ClaudeApi"/>, <see cref="EmbeddedLlm.EmbeddedEndpoint"/>), the provider is told by the URL
/// alone: a saved <see cref="AppSettingsData.LlmUrl"/> on <see cref="Host"/> is the Claude CLI, and
/// <see cref="AppSettingsData.LlmModel"/> holds the <c>--model</c> word (<see cref="ClaudeModels.Aliases"/>, or a full name).
///
/// <para>The URL is a sentinel, never a place: nothing is posted anywhere, the chat client
/// (<see cref="ClaudeCliChatClient"/>) writes to the CLI's stdin. <c>.localhost</c> keeps a code path that posts to it by
/// mistake on this machine, as the embedded model's does.</para>
/// </summary>
public static class ClaudeCliEndpoint
{
    /// <summary>The sentinel's host; a base URL on it is the Claude CLI. Pinned.</summary>
    public const string Host = "claude-cli.localhost";

    /// <summary>The base URL saved for the Claude CLI, already <c>/v1</c>-normalised like every other.</summary>
    public static readonly Uri BaseUrl = new("http://" + Host + "/v1");

    /// <summary>The word that stands for <see cref="BaseUrl"/> wherever a URL is typed (<c>/server claude-cli</c>, <c>--url claude-cli</c>).</summary>
    public const string Alias = "claude-cli";

    /// <summary>The server picker's name for the row (fits <c>SettingsMenu.ServerNameWidth</c>). Pinned.</summary>
    public const string ServerName = "Claude CLI";

    /// <summary>The model the row lists first and a blank <c>LLM model</c> means: the CLI's own pick would do too, but a named one shows on the hint row.</summary>
    public const string DefaultModel = "sonnet";

    /// <summary>
    /// The models the row and <c>/model</c> list: the <c>--model</c> aliases, <see cref="DefaultModel"/> first — the one a
    /// pick that names none (ESC at the startup picker) runs.
    /// </summary>
    public static readonly IReadOnlyList<string> Models = [DefaultModel, .. ClaudeModels.Aliases.Where(alias => alias != DefaultModel)];

    /// <summary>
    /// The context window the models behind the aliases have (2026-09-30: every current Claude model's is 200,000 tokens
    /// without the 1M beta; the spike's <c>result</c> lines said so, <c>modelUsage.contextWindow</c>). What <c>/usage</c>
    /// measures against; the CLI compacts its own conversation, so nothing here acts on it.
    /// </summary>
    public const int DefaultContextWindow = 200_000;

    /// <summary>Whether <paramref name="baseUrl"/> is the Claude CLI.</summary>
    public static bool IsClaudeCli(Uri? baseUrl) =>
        baseUrl is not null && string.Equals(baseUrl.Host, Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="url"/> (a saved setting, possibly blank, the alias or malformed) names the Claude CLI.</summary>
    public static bool IsClaudeCli(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string trimmed = url.Trim();
        return string.Equals(trimmed, Alias, StringComparison.OrdinalIgnoreCase)
            || (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) && IsClaudeCli(parsed));
    }

    /// <summary>
    /// Whether the Claude CLI is offered: the <c>Claude CLI server</c> switch on and the CLI found where
    /// <see cref="ClaudeExecutable.Locate"/> looks. <paramref name="environment"/> reads the system variables
    /// (<see cref="EnvironmentOverrides.System"/>), <paramref name="exists"/> the file test.
    /// </summary>
    public static bool Offered(AppSettingsData effective, Func<string, string?> environment, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(environment);
        return effective.ClaudeCliServer && ClaudeExecutable.Locate(effective.ClaudeCliExecutable, environment, exists ?? File.Exists) is not null;
    }

    /// <summary>The <c>--model</c> word for a saved <paramref name="model"/>: blank is <see cref="DefaultModel"/>.</summary>
    public static string ModelOf(string? model) => string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
}
