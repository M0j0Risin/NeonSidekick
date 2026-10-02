using NeonSidekick.Settings;

namespace NeonSidekick.Docker;

/// <summary>
/// A chosen Docker container as a <c>/server</c> choice (2026-10-02, the user's ask, after their tray app DockerLlmPicker: the
/// vLLM and SGLang containers they keep, ticked on <c>/settings</c>' Docker tab, offered as servers, and only one of them running
/// at a time so the GPU is never asked to hold two). As with the embedded model (<see cref="EmbeddedLlm.EmbeddedEndpoint"/>), the
/// provider is told by the URL alone: <see cref="AppSettingsData.LlmUrl"/> saved as <c>http://docker.localhost/&lt;container&gt;/v1</c>
/// names the container, so <c>LLM URL</c> stays the one place the choice lives — a profile switch, <c>/server</c>,
/// <c>--url docker:&lt;name&gt;</c> and <c>NEONSIDEKICK_LLM_URL</c> all route it the same way. The URL is a sentinel, never a place:
/// the container's published port is found at each start (<see cref="DockerServerHost"/>) and carried as
/// <see cref="Llm.LlmEndpoint.LiveUrl"/>; <c>.localhost</c> keeps a request posted to it by mistake on this machine.
/// <para>Only the containers <c>Docker server containers</c> names take part: a container unticked since its URL was saved stands
/// for nothing (<see cref="SwitchedOff"/>), and the one-at-a-time rule never touches a container that is not chosen.</para>
/// </summary>
public static class DockerEndpoint
{
    /// <summary>The sentinel's host; a base URL on it is a Docker container. Pinned.</summary>
    public const string Host = "docker.localhost";

    /// <summary><c>/server docker</c>: the chosen containers' rows alone (not a URL alias: there is no one container it means).</summary>
    public const string Alias = "docker";

    /// <summary>The typed form of one container's URL, wherever a URL is typed: <c>docker:&lt;name&gt;</c>.</summary>
    public const string AliasPrefix = "docker:";

    /// <summary>The server picker's name for the rows (fits <c>SettingsMenu.ServerNameWidth</c>). Pinned.</summary>
    public const string ServerName = "Docker";

    /// <summary>Whether <paramref name="name"/> is a Docker container name (<c>[a-zA-Z0-9][a-zA-Z0-9_.-]*</c>): one that fits the URL's path as it is.</summary>
    public static bool IsName(string? name) =>
        !string.IsNullOrEmpty(name) && char.IsAsciiLetterOrDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    /// <summary>The sentinel for container <paramref name="container"/>: <c>http://docker.localhost/&lt;container&gt;/v1</c>.</summary>
    public static Uri BaseUrl(string container)
    {
        if (!IsName(container))
        {
            throw new ArgumentException($"'{container}' is not a Docker container name.", nameof(container));
        }

        return new Uri("http://" + Host + "/" + container + "/v1");
    }

    /// <summary>Whether <paramref name="baseUrl"/> is a Docker container's sentinel.</summary>
    public static bool IsDocker(Uri? baseUrl) =>
        baseUrl is not null && string.Equals(baseUrl.Host, Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="url"/> (a saved setting, possibly blank, the typed <c>docker:&lt;name&gt;</c> or malformed) names a Docker container.</summary>
    public static bool IsDocker(string? url) => ContainerOf(url) is not null;

    /// <summary>The container a sentinel names (its first path segment), or null.</summary>
    public static string? ContainerOf(Uri? baseUrl)
    {
        if (!IsDocker(baseUrl))
        {
            return null;
        }

        string name = baseUrl!.AbsolutePath.Trim('/').Split('/')[0];
        return IsName(name) ? name : null;
    }

    /// <summary>The container <paramref name="url"/> names: <c>docker:&lt;name&gt;</c> or the sentinel; null for anything else. The typed form is read first, since <c>docker:x</c> also parses as a URL of scheme <c>docker</c>.</summary>
    public static string? ContainerOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string trimmed = url.Trim();
        if (trimmed.StartsWith(AliasPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string name = trimmed[AliasPrefix.Length..].Trim();
            return IsName(name) ? name : null;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ? ContainerOf(parsed) : null;
    }

    /// <summary>The containers <c>Docker server containers</c> names, trimmed, distinct (ordinal: Docker's names are case-sensitive), in the list's order.</summary>
    public static IReadOnlyList<string> ChosenNames(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return (effective.DockerServerContainers ?? []).Select(n => (n ?? "").Trim()).Where(IsName).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether Docker servers are offered at all: the switch on, on Windows (the engine's pipe), and at least one container chosen.</summary>
    public static bool Offered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.DockerServers && OperatingSystem.IsWindows() && ChosenNames(effective).Count > 0;
    }

    /// <summary>Whether <paramref name="effective"/>'s URL names a chosen container while Docker servers are offered.</summary>
    public static bool Chosen(AppSettingsData effective) =>
        Offered(effective) && ContainerOf(effective.LlmUrl) is { } name && ChosenNames(effective).Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="effective"/>'s URL names a container that is no longer offered (the switch off, the container
    /// unticked): the saved sentinel then stands for nothing, and a connect finds a server as a blank URL would — the Claude
    /// CLI's rule.
    /// </summary>
    public static bool SwitchedOff(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return IsDocker(effective.LlmUrl) && !Chosen(effective);
    }
}
