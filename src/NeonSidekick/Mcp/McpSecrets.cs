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
    /// <summary>The words a header's name is judged by, besides <see cref="Shell.ChildEnvironment.SecretWords"/>.</summary>
    private static readonly string[] HeaderWords = ["AUTH", "KEY", "TOKEN", "SECRET", "PASSWORD", "COOKIE", "CREDENTIAL"];

    private const string HeadersKey = "headers";
    private const string EnvKey = "env";

    /// <summary>
    /// Whether a value named <paramref name="name"/> is one to encrypt: a header holding <c>AUTH</c>, <c>KEY</c>, <c>TOKEN</c>, <c>SECRET</c>,
    /// <c>PASSWORD</c>, <c>COOKIE</c> or <c>CREDENTIAL</c>; an env name holding one of <see cref="Shell.ChildEnvironment.SecretWords"/> or
    /// <c>AUTH</c>. Without regard to case, anywhere in the name (no prefix rule). Pure.
    /// </summary>
    public static bool IsSecretName(string name, bool header)
    {
        ArgumentNullException.ThrowIfNull(name);
        IEnumerable<string> words = header ? HeaderWords : [.. Shell.ChildEnvironment.SecretWords, "AUTH"];
        return words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="value"/> is one to encrypt now: not empty, not a <c>${…}</c> reference, not already sealed.</summary>
    public static bool NeedsSealing(string? value) =>
        !string.IsNullOrEmpty(value)
        && !(value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'))
        && !WindowsCredentials.IsSealed(value);

    /// <summary>
    /// Encrypts every plain secret of server <paramref name="server"/> in <paramref name="path"/>, and sets each one in
    /// <paramref name="config"/> to the stored value. Where the store can't encrypt, nothing changes. A failure leaves that value plain and
    /// working, with a Warn that never holds it; a value changed in the file meanwhile is left alone, silent (the next load sees it). A key
    /// repeated along the path leaves the server plain, with one Warn. Never throws on IO.
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

        if (!Seal(path, server, config.Headers, HeadersKey, header: true))
        {
            return;
        }

        Seal(path, server, config.Env, EnvKey, header: false);
    }

    /// <summary>One dictionary's values; false once a repeated key stopped the server (its Warn written).</summary>
    private static bool Seal(string path, string server, Dictionary<string, string>? values, string section, bool header)
    {
        if (values is not { Count: > 0 })
        {
            return true;
        }

        foreach (var (key, value) in values.ToList())
        {
            if (!IsSecretName(key, header) || !NeedsSealing(value))
            {
                continue;
            }

            var encrypted = WindowsCredentials.Protect(value);
            if (encrypted.Error is { } protectError)
            {
                DiagnosticLog.Warn(McpConfigFile.Category, McpText.EncryptFailedLogLine(path, server, key, protectError));
                continue;
            }

            string? error = ConnectionsFileEdit.WriteValue(path, ["mcpServers", server, section, key], encrypted.Value!, value);
            switch (error)
            {
                case null:
                    values[key] = encrypted.Value!;
                    DiagnosticLog.Info(McpConfigFile.Category, McpText.EncryptedLogLine(path, server, key));
                    break;
                case ConnectionsFileEdit.PasswordChanged:
                    break;
                case ConnectionsFileEdit.KeyRepeated:
                    DiagnosticLog.Warn(McpConfigFile.Category, McpText.RepeatedKeyLogLine(path, server));
                    return false;
                default:
                    DiagnosticLog.Warn(McpConfigFile.Category, McpText.EncryptFailedLogLine(path, server, key, error));
                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// <paramref name="config"/> with every sealed <c>headers</c> and <c>env</c> value decrypted, as a copy (the stored config never holds the
    /// plain text), or the failed row's detail (<see cref="McpText.CannotReveal"/>) for the first value that can't be read here. A config
    /// with nothing sealed comes back as it is.
    /// </summary>
    public static (McpServerConfig? Config, string? Error) Reveal(McpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        bool sealedAny = (config.Headers?.Values.Any(WindowsCredentials.IsSealed) ?? false) || (config.Env?.Values.Any(WindowsCredentials.IsSealed) ?? false);
        if (!sealedAny)
        {
            return (config, null);
        }

        var (headers, headerError) = Open(config.Headers);
        if (headerError is not null)
        {
            return (null, headerError);
        }

        var (env, envError) = Open(config.Env);
        if (envError is not null)
        {
            return (null, envError);
        }

        return (new McpServerConfig
        {
            Type = config.Type,
            Command = config.Command,
            Args = config.Args,
            Env = env,
            Cwd = config.Cwd,
            Url = config.Url,
            Headers = headers,
            Ask = config.Ask,
            Instructions = config.Instructions,
        }, null);
    }

    /// <summary>
    /// Whether two entries are the same server (2026-11-19, <c>/profile push|pull mcp</c>): their fingerprints once each is
    /// <see cref="Reveal"/>ed, since a DPAPI value is randomized and two encrypted copies of one token never read alike; the stored ones
    /// when either can't be revealed here.
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

    private static (Dictionary<string, string>? Values, string? Error) Open(Dictionary<string, string>? values)
    {
        if (values is null)
        {
            return (null, null);
        }

        var open = new Dictionary<string, string>(values.Comparer);
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
                return (null, McpText.CannotReveal(key, error));
            }

            open[key] = plain.Value!;
        }

        return (open, null);
    }
}
