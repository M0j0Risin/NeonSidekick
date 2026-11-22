using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;

namespace NeonSidekick.Mcp;

/// <summary>
/// The tokens in <c>mcp.json</c> (2026-11-19, feature idea #29): a plain <c>headers</c> or <c>env</c> value whose name says it's a secret is
/// encrypted in place, as the database families encrypt a password (<see cref="WindowsCredentials.Protect"/>: DPAPI on Windows, the Keychain
/// on a Mac), the file's other bytes kept by <see cref="ConnectionsFileEdit.WriteValue"/>. The stored value stays in the config the catalog,
/// the fingerprint and <c>/profile push</c> see; only <see cref="Reveal"/>, at connect, hands the transport a plain copy. <c>args</c> isn't
/// covered (it has no names to go by).
/// </summary>
public static class McpSecrets
{
    private const string HeadersKey = "headers";
    private const string EnvKey = "env";
    private const string OAuthKey = "oauth";
    private const string ClientSecretKey = "clientSecret";

    /// <summary>
    /// Whether a <c>headers</c> or <c>env</c> value is one to encrypt (2026-11-20, the mcp-ask review's #21, #47): its name says it's a secret
    /// (<see cref="SecretNames.IsSecret"/>, one rule for both), or the value is a URL carrying a password (<c>DATABASE_URL</c>'s
    /// <c>postgres://app:pw@db</c>, <c>redis://:pw@cache</c>), whatever its name. Pure.
    /// </summary>
    public static bool IsSecret(string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SecretNames.IsSecret(name) || (!string.IsNullOrEmpty(value) && Llm.Tools.ToolRedaction.Url(value) != value);
    }

    /// <summary>
    /// Whether <paramref name="value"/> is one to encrypt now: not empty, holding no <c>${…}</c> reference (since 2026-11-20 anywhere in it,
    /// <c>Bearer ${GITHUB_TOKEN}</c> too: a reference is expanded at connect and is no secret itself), not already sealed.
    /// </summary>
    public static bool NeedsSealing(string? value) =>
        !string.IsNullOrEmpty(value)
        && !value.Contains("${", StringComparison.Ordinal)
        && !WindowsCredentials.IsSealed(value);

    /// <summary>
    /// Encrypts every plain secret of server <paramref name="server"/> in <paramref name="path"/>, and sets each one in
    /// <paramref name="config"/> to the stored value. Where the store can't encrypt, nothing changes. Every value is encrypted first, then
    /// all are written in one atomic write (2026-11-20, the mcp-ask review's #42: <see cref="ConnectionsFileEdit.WriteValues"/>, one read and
    /// one write where each value used to be its own). A failure leaves that value plain and working, with a Warn that never holds it; a
    /// value changed in the file meanwhile is left alone, silent (the next load sees it); a key repeated along a value's path leaves that
    /// value plain, with one Warn for the server. Never throws on IO.
    /// </summary>
    public static void EncryptInPlace(string path, string server, McpServerConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(config);
        if (!WindowsCredentials.CanProtect)
        {
            return;
        }

        var pending = new List<(Dictionary<string, string> Values, string Key, string Sealed)>();
        var writes = new List<ConnectionsFileEdit.ValueWrite>();
        Collect(path, server, config.Headers, HeadersKey, pending, writes);
        Collect(path, server, config.Env, EnvKey, pending, writes);
        // A pre-registered app's secret (2026-11-21, feature idea #32): sealed as a token, its own path; a ${NAME} reference stays as written.
        Dictionary<string, string>? oauth = null;
        if (config.OAuth?.ClientSecret is { } secret && NeedsSealing(secret))
        {
            oauth = new Dictionary<string, string>(StringComparer.Ordinal) { [ClientSecretKey] = secret };
            var encrypted = WindowsCredentials.Protect(secret);
            if (encrypted.Error is { } protectError)
            {
                DiagnosticLog.Warn(McpConfigFile.Category, McpText.EncryptFailedLogLine(path, server, ClientSecretKey, protectError));
                oauth = null;
            }
            else
            {
                pending.Add((oauth, ClientSecretKey, encrypted.Value!));
                writes.Add(new ConnectionsFileEdit.ValueWrite([McpConfigFile.ServersKey, server, OAuthKey, ClientSecretKey], encrypted.Value!, secret));
            }
        }

        if (writes.Count == 0)
        {
            return;
        }

        var failed = ConnectionsFileEdit.WriteValues(path, writes).ToDictionary(e => e.Index, e => e.Error);
        bool repeatSaid = false;
        for (int i = 0; i < pending.Count; i++)
        {
            var (values, key, sealedValue) = pending[i];
            switch (failed.GetValueOrDefault(i))
            {
                case null when ReferenceEquals(values, oauth):
                    config.OAuth!.ClientSecret = sealedValue;
                    DiagnosticLog.Info(McpConfigFile.Category, McpText.EncryptedLogLine(path, server, key));
                    break;
                case null:
                    values[key] = sealedValue;
                    DiagnosticLog.Info(McpConfigFile.Category, McpText.EncryptedLogLine(path, server, key));
                    break;
                case ConnectionsFileEdit.PasswordChanged:
                    break;
                case ConnectionsFileEdit.KeyRepeated:
                    if (!repeatSaid)
                    {
                        repeatSaid = true;
                        DiagnosticLog.Warn(McpConfigFile.Category, McpText.RepeatedKeyLogLine(path, server));
                    }

                    break;
                case { } error:
                    DiagnosticLog.Warn(McpConfigFile.Category, McpText.EncryptFailedLogLine(path, server, key, error));
                    break;
            }
        }
    }

    /// <summary>One dictionary's plain secrets, each encrypted now and queued for the one write; a value the store can't encrypt is a Warn and stays plain.</summary>
    private static void Collect(string path, string server, Dictionary<string, string>? values, string section,
        List<(Dictionary<string, string> Values, string Key, string Sealed)> pending, List<ConnectionsFileEdit.ValueWrite> writes)
    {
        if (values is not { Count: > 0 })
        {
            return;
        }

        foreach (var (key, value) in values)
        {
            if (!IsSecret(key, value) || !NeedsSealing(value))
            {
                continue;
            }

            var encrypted = WindowsCredentials.Protect(value);
            if (encrypted.Error is { } protectError)
            {
                DiagnosticLog.Warn(McpConfigFile.Category, McpText.EncryptFailedLogLine(path, server, key, protectError));
                continue;
            }

            pending.Add((values, key, encrypted.Value!));
            writes.Add(new ConnectionsFileEdit.ValueWrite([McpConfigFile.ServersKey, server, section, key], encrypted.Value!, value));
        }
    }

    /// <summary>
    /// <paramref name="config"/> with every sealed <c>headers</c> and <c>env</c> value decrypted, as a copy (the stored config never holds the
    /// plain text; <see cref="McpServerConfig.With"/>, new dictionaries), or the failed row's detail (<see cref="McpText.CannotReveal"/>) for
    /// the first value that can't be read here. A config with nothing sealed comes back as it is (since 2026-11-20, the mcp-ask review's
    /// #107, judged by the one walk, not a scan before it).
    /// </summary>
    public static (McpServerConfig? Config, string? Error) Reveal(McpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var (headers, headersChanged, headerError) = Open(config.Headers);
        if (headerError is not null)
        {
            return (null, headerError);
        }

        var (env, envChanged, envError) = Open(config.Env);
        if (envError is not null)
        {
            return (null, envError);
        }

        // The sign-in's secret (2026-11-21): opened into a copy of the oauth object, so the stored config never holds it plain.
        McpOAuthConfig? oauth = null;
        if (config.OAuth is { ClientSecret: { } secret } stored && WindowsCredentials.IsSealed(secret))
        {
            var plain = WindowsCredentials.Unprotect(secret);
            if (plain.Error is { } error)
            {
                return (null, McpText.CannotReveal(ClientSecretKey, error));
            }

            oauth = new McpOAuthConfig { ClientId = stored.ClientId, ClientSecret = plain.Value, Scopes = stored.Scopes, RedirectPort = stored.RedirectPort };
        }

        return headersChanged || envChanged || oauth is not null
            ? (config.With(env: envChanged ? env : null, headers: headersChanged ? headers : null, oauth: oauth), null)
            : (config, null);
    }

    /// <summary>
    /// Whether two entries are the same server (2026-11-19, <c>/profile push|pull mcp</c>): their fingerprints once each is
    /// <see cref="Reveal"/>ed, since a DPAPI value is randomized and two encrypted copies of one token never read alike; the stored ones
    /// when either can't be revealed here. <c>ask</c> and <c>instructions</c> count: a push that changes them changes the server.
    /// </summary>
    public static bool SameServer(McpServerConfig a, McpServerConfig b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Fingerprint() == b.Fingerprint())
        {
            return true;
        }

        var (openA, _) = Reveal(a);
        var (openB, _) = Reveal(b);
        return openA is not null && openB is not null && openA.Fingerprint() == openB.Fingerprint();
    }

    /// <summary>
    /// Whether a reload can keep <paramref name="was"/>'s live connection for <paramref name="now"/> (2026-11-20, the mcp-ask review's #16,
    /// #53, #115): the same connection shape (<see cref="McpServerConfig.ConnectionFingerprint"/>, so <c>ask</c> and <c>instructions</c> don't
    /// count: those are taken on in place). Equal as stored is the same; a shape that differs with every <c>env</c> and <c>headers</c> value
    /// blanked is a different server; otherwise the values decide once revealed, since a token sealed again reads differently. A value that
    /// can't be revealed here is a different server, so the connect's own failure says why.
    /// </summary>
    public static bool SameConnection(McpServerConfig was, McpServerConfig now)
    {
        ArgumentNullException.ThrowIfNull(was);
        ArgumentNullException.ThrowIfNull(now);
        if (was.ConnectionFingerprint() == now.ConnectionFingerprint())
        {
            return true;
        }

        if (Blanked(was).ConnectionFingerprint() != Blanked(now).ConnectionFingerprint())
        {
            return false;
        }

        var (openWas, _) = Reveal(was);
        var (openNow, _) = Reveal(now);
        return openWas is not null && openNow is not null && openWas.ConnectionFingerprint() == openNow.ConnectionFingerprint();
    }

    private static McpServerConfig Blanked(McpServerConfig config) =>
        config.With(env: BlankValues(config.Env), headers: BlankValues(config.Headers),
            oauth: config.OAuth is { } o ? new McpOAuthConfig { ClientId = o.ClientId, ClientSecret = o.ClientSecret is null ? null : "", Scopes = o.Scopes, RedirectPort = o.RedirectPort } : null);

    private static Dictionary<string, string>? BlankValues(Dictionary<string, string>? values) =>
        values?.ToDictionary(v => v.Key, _ => "", values.Comparer);

    /// <summary>One dictionary with its sealed values opened, as a new one; whether any was sealed; or the first one's error.</summary>
    private static (Dictionary<string, string>? Values, bool Changed, string? Error) Open(Dictionary<string, string>? values)
    {
        if (values is null)
        {
            return (null, false, null);
        }

        var open = new Dictionary<string, string>(values.Comparer);
        bool changed = false;
        foreach (var (key, value) in values)
        {
            if (!WindowsCredentials.IsSealed(value))
            {
                open[key] = value;
                continue;
            }

            var plain = WindowsCredentials.Unprotect(value);
            if (plain.Error is { } error)
            {
                return (null, false, McpText.CannotReveal(key, error));
            }

            open[key] = plain.Value!;
            changed = true;
        }

        return (open, changed, null);
    }
}
