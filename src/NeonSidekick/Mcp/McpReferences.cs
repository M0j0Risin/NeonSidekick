using System.Text.RegularExpressions;

namespace NeonSidekick.Mcp;

/// <summary>A <c>${NAME}</c> in an <c>mcp.json</c> entry that names a variable that isn't set and has no default: left as written.</summary>
/// <param name="Key">Where it stands: <c>command</c>, <c>args</c>, <c>url</c>, or the <c>env</c> or <c>headers</c> key.</param>
/// <param name="Name">The variable's name.</param>
public readonly record struct McpUnsetReference(string Key, string Name);

/// <summary>
/// <c>${NAME}</c> and <c>${NAME:-default}</c> in an <c>mcp.json</c> entry (2026-11-20, the mcp-ask review's #72), expanded at connect as
/// Claude Code expands them, in the same five fields: <c>command</c>, <c>args</c>, <c>env</c>, <c>url</c> and <c>headers</c> (<c>cwd</c>
/// isn't). The value comes from the environment a child process would see (<see cref="Shell.ChildEnvironment.ForReference"/>), so the app's
/// own keys are never handed over. A name that isn't set and has no default stays as written, and is returned for the caller's one Warn:
/// a server that ignores an optional variable keeps working, as it did when it was always sent the literal. The entry the catalog holds is
/// never changed (the load cache shares it): a changed config is a <see cref="McpServerConfig.With"/> copy with new lists. Pure but for the
/// lookup.
/// </summary>
public static partial class McpReferences
{
    /// <summary>
    /// <paramref name="config"/> with its references expanded, or the same object when no field holds <c>${</c>, and every reference left
    /// as written for want of a value.
    /// </summary>
    public static (McpServerConfig Config, IReadOnlyList<McpUnsetReference> Unset) Expand(McpServerConfig config, Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(lookup);
        if (!Holds(config))
        {
            return (config, []);
        }

        var unset = new List<McpUnsetReference>();
        string? Expand(string? text, string key) => text is null ? null : ExpandText(text, key, lookup, unset);

        var expanded = config.With(
            command: Expand(config.Command, "command"),
            args: config.Args?.Select(a => Expand(a, "args")!).ToList(),
            env: config.Env?.ToDictionary(e => e.Key, e => Expand(e.Value, e.Key)!, config.Env.Comparer),
            url: Expand(config.Url, "url"),
            headers: config.Headers?.ToDictionary(h => h.Key, h => Expand(h.Value, h.Key)!, config.Headers.Comparer),
            // The sign-in's app (2026-11-21, feature idea #32): its id and secret, on a copy of the oauth object.
            oauth: config.OAuth is { } o && (Has(o.ClientId) || Has(o.ClientSecret))
                ? new McpOAuthConfig { ClientId = Expand(o.ClientId, "oauth.clientId"), ClientSecret = Expand(o.ClientSecret, "oauth.clientSecret"), Scopes = o.Scopes, RedirectPort = o.RedirectPort }
                : null);
        return (expanded, unset);
    }

    /// <summary>Whether any of the five fields holds <c>${</c>.</summary>
    public static bool Holds(McpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Has(config.Command) || Has(config.Url)
            || (config.Args?.Any(Has) ?? false)
            || (config.Env?.Values.Any(Has) ?? false)
            || (config.Headers?.Values.Any(Has) ?? false)
            || Has(config.OAuth?.ClientId) || Has(config.OAuth?.ClientSecret);
    }

    private static bool Has(string? text) => text is not null && text.Contains("${", StringComparison.Ordinal);

    private static string ExpandText(string text, string key, Func<string, string?> lookup, List<McpUnsetReference> unset)
    {
        if (!Has(text))
        {
            return text;
        }

        return Reference().Replace(text, m =>
        {
            string name = m.Groups["name"].Value;
            if (lookup(name) is { } value)
            {
                return value;
            }

            if (m.Groups["default"].Success)
            {
                return m.Groups["default"].Value;
            }

            unset.Add(new McpUnsetReference(key, name));
            return m.Value;
        });
    }

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::-(?<default>[^}]*))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();
}
