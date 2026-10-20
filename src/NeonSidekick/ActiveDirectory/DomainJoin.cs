using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// Whether this PC is joined to a domain, and that domain's DNS name (2026-10-15, the admin-tools plan: the built-in <c>current</c> domain).
/// The plan named <c>GetComputerNameExW(ComputerNameDnsDomain)</c>, but that is the primary DNS suffix: set on many unjoined PCs too, and in a
/// disjoint namespace (joined to corp.example, suffix na.example) not the domain at all. So both the join and the name come from
/// <c>DsRoleGetPrimaryDomainInformation</c> (netapi32, the basic level: the machine's role and the domain's <c>DomainNameDns</c>; 2026-10-20, a
/// review's finding, where <c>NetGetJoinInformation</c> and the suffix had been read). A local LSA read; a system library, no DLL ships. Null
/// off Windows, when not joined, and for a domain with no DNS name. Excluded from coverage.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class DomainJoinNative
{
    private const int DsRolePrimaryDomainInfoBasic = 1;

    // DSROLE_MACHINE_ROLE: a member workstation or server, or a domain controller.
    private const int MemberWorkstation = 1;
    private const int MemberServer = 3;
    private const int BackupDomainController = 4;
    private const int PrimaryDomainController = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct PrimaryDomainInfoBasic
    {
        public int MachineRole;
        public uint Flags;
        public char* DomainNameFlat;
        public char* DomainNameDns;
        public char* DomainForestName;
        public Guid DomainGuid;
    }

    [LibraryImport("netapi32.dll", EntryPoint = "DsRoleGetPrimaryDomainInformation", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DsRoleGetPrimaryDomainInformation(string? server, int level, PrimaryDomainInfoBasic** buffer);

    [LibraryImport("netapi32.dll", EntryPoint = "DsRoleFreeMemory")]
    private static partial void DsRoleFreeMemory(void* buffer);

    /// <summary>The joined domain's DNS name; null when this PC is not in a domain.</summary>
    public static string? JoinedDomain()
    {
        PrimaryDomainInfoBasic* info = null;
        if (DsRoleGetPrimaryDomainInformation(null, DsRolePrimaryDomainInfoBasic, &info) != 0 || info is null)
        {
            return null;
        }

        try
        {
            bool joined = info->MachineRole is MemberWorkstation or MemberServer or BackupDomainController or PrimaryDomainController;
            return joined && info->DomainNameDns is not null && info->DomainNameDns[0] != '\0' ? new string(info->DomainNameDns) : null;
        }
        finally
        {
            DsRoleFreeMemory(info);
        }
    }
}

/// <summary>The join as the portable code reads it (null off Windows).</summary>
public static class DomainJoin
{
    private static readonly Lazy<string?> s_joined = new(() => OperatingSystem.IsWindows() ? DomainJoinNative.JoinedDomain() : null);

    /// <summary>This PC's domain's DNS name, read once a run; null when not joined (or off Windows).</summary>
    public static string? Current => s_joined.Value;
}
