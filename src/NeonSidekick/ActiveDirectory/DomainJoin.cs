using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// Whether this PC is joined to a domain, and that domain's DNS name (2026-10-15, the admin-tools plan: the built-in <c>current</c> domain).
/// The plan named <c>GetComputerNameExW(ComputerNameDnsDomain)</c> alone, but that is the primary DNS suffix, set on many unjoined PCs too; so
/// the join itself is read from <c>NetGetJoinInformation</c> (netapi32, <c>NetSetupDomainName</c>) and the name from
/// <c>GetComputerNameExW</c>. Both read only; system libraries, no DLL ships. Null off Windows and when not joined. Excluded from coverage.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class DomainJoinNative
{
    private const int NetSetupDomainName = 3;
    private const int ComputerNameDnsDomain = 2;

    [LibraryImport("netapi32.dll", EntryPoint = "NetGetJoinInformation", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetGetJoinInformation(string? server, char** name, int* status);

    [LibraryImport("netapi32.dll", EntryPoint = "NetApiBufferFree")]
    private static partial int NetApiBufferFree(void* buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "GetComputerNameExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetComputerNameEx(int format, char* buffer, uint* size);

    /// <summary>The joined domain's DNS name; null when this PC is not in a domain.</summary>
    public static string? JoinedDomain()
    {
        char* name = null;
        int status = 0;
        if (NetGetJoinInformation(null, &name, &status) != 0)
        {
            return null;
        }

        if (name is not null)
        {
            NetApiBufferFree(name);
        }

        if (status != NetSetupDomainName)
        {
            return null;
        }

        uint size = 0;
        GetComputerNameEx(ComputerNameDnsDomain, null, &size);
        if (size == 0)
        {
            return null;
        }

        char* buffer = stackalloc char[(int)size + 1];
        return GetComputerNameEx(ComputerNameDnsDomain, buffer, &size) && size > 0 ? new string(buffer, 0, (int)size) : null;
    }
}

/// <summary>The join as the portable code reads it (null off Windows).</summary>
public static class DomainJoin
{
    private static readonly Lazy<string?> s_joined = new(() => OperatingSystem.IsWindows() ? DomainJoinNative.JoinedDomain() : null);

    /// <summary>This PC's domain's DNS name, read once a run; null when not joined (or off Windows).</summary>
    public static string? Current => s_joined.Value;
}
