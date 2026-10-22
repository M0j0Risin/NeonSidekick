using System.Runtime.Versioning;

namespace NeonSidekick.Unc;

/// <summary>
/// The network hosts a pasted path may reach (2026-10-22, review K7, the fix plan's D13, the user's call: known hosts only). Pasting
/// <c>\\attacker\s\cat.png</c> into the chat line made <see cref="Files.ImageFile"/>'s attach check call <c>File.Exists</c> on it, which
/// opens SMB to that host — Windows signs in with the user's NTLM hash on the way — and holds the paste for as long as the host takes to
/// answer. Now a UNC path is probed only when its host is a configured UNC share's server (<c>unc.json</c>, the profile's and the home's,
/// offered or not) or a persistent mapped drive's (<c>HKCU\Network\&lt;letter&gt;\RemotePath</c>; a <c>net use</c> mapping that isn't
/// persistent is not known, which only means its path stays text). Any other stays plain text, and the line says it wasn't opened. A
/// Mac has no UNC paths to probe (<c>//server/x</c> is a local path there), so this is Windows' alone.
/// <para>Process-wide, set once by <c>SidekickApp</c> before the screen reads a key; unset (a test, a check mode) knows no host.</para>
/// </summary>
public static class KnownUncHosts
{
    private static volatile Func<IEnumerable<string>>? s_shareRoots;

    /// <summary>Sets where the configured shares' roots come from (each read when a UNC path is pasted); the scope puts the previous back.</summary>
    public static IDisposable Configure(Func<IEnumerable<string>>? shareRoots)
    {
        var previous = s_shareRoots;
        s_shareRoots = shareRoots;
        return new Restore(previous);
    }

    private sealed class Restore(Func<IEnumerable<string>>? previous) : IDisposable
    {
        public void Dispose() => s_shareRoots = previous;
    }

    /// <summary>
    /// The host of a Windows UNC path (<c>\\host\…</c>, <c>//host/…</c>, <c>\\?\UNC\host\…</c>, a WebDAV <c>host@SSL</c> as <c>host</c>), or
    /// null for any other path, a local device path (<c>\\?\C:\…</c>, <c>\\.\…</c>) included. Pure.
    /// </summary>
    public static string? HostOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string slashed = path.Trim().Replace('/', '\\');
        string rest;
        if (slashed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || slashed.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            rest = slashed[@"\\?\UNC\".Length..];
        }
        else if (slashed.StartsWith(@"\\", StringComparison.Ordinal) && !UncPath.IsDevicePath(slashed))
        {
            rest = slashed[2..];
        }
        else
        {
            return null;
        }

        int end = rest.IndexOf('\\');
        string host = end < 0 ? rest : rest[..end];
        int at = host.IndexOf('@');
        host = at < 0 ? host : host[..at];
        return host.Length == 0 ? null : host;
    }

    /// <summary>Whether <paramref name="host"/> is a configured share's server or a persistent mapped drive's host (case blind).</summary>
    public static bool IsKnown(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (s_shareRoots?.Invoke() is { } roots && roots.Any(root => HostOf(root) is { } server && Same(server, host)))
        {
            return true;
        }

        return OperatingSystem.IsWindows() && MappedDriveHosts().Any(server => Same(server, host));
    }

    /// <summary>Whether <paramref name="path"/> is a UNC path whose host is not known: one a paste must not probe.</summary>
    public static bool IsUnknown(string path) => OperatingSystem.IsWindows() && HostOf(path) is { } host && !IsKnown(host);

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The hosts of the user's persistent mapped drives, read from the registry (no network call).</summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> MappedDriveHosts()
    {
        var hosts = new List<string>();
        try
        {
            using var network = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Network");
            if (network is null)
            {
                return hosts;
            }

            foreach (string letter in network.GetSubKeyNames())
            {
                using var drive = network.OpenSubKey(letter);
                if (drive?.GetValue("RemotePath") is string remote && HostOf(remote) is { } host)
                {
                    hosts.Add(host);
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // An unreadable key knows no host: the paste stays text.
        }

        return hosts;
    }
}
