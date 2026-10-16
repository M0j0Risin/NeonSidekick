using NeonSidekick.Computers;

namespace NeonSidekick.Services;

/// <summary>
/// The services the app never stops, restarts or changes in start type (2026-10-15, the admin-tools plan): the ones whose loss takes the
/// machine, its remote reach or its protection with it — RPC and DCOM, the session manager, the security accounts, the event log, WMI, the
/// firewall and Defender, the Task Scheduler, file sharing, the domain's sign-in, name resolution and addressing, Remote Desktop, the crypto and
/// sign-in services. Starting one is allowed (bringing it back is safe). A computer's <c>protected</c> globs add to this list, never take from
/// it. Pinned.
/// </summary>
public static class ServiceGuard
{
    public static readonly IReadOnlyList<string> Protected =
    [
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "EventLog", "Winmgmt", "BFE", "MpsSvc", "WinDefend", "WdNisSvc", "SecurityHealthService",
        "Schedule", "LanmanServer", "LanmanWorkstation", "Netlogon", "Dnscache", "Dhcp", "TermService", "CryptSvc", "KeyIso", "gpsvc", "ProfSvc",
        "UserManager", "Power", "PlugPlay", "nsi", "Netman", "NlaSvc", "W32Time", "VaultSvc", "BrokerInfrastructure", "SystemEventsBroker", "CoreMessagingRegistrar",
    ];

    /// <summary>Which list protects <paramref name="name"/>: the app's, the computer's own, or neither.</summary>
    public static ServiceProtection Protection(string name, ServiceComputerConfig config)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(config);
        if (Protected.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return ServiceProtection.App;
        }

        return config.Protected is { } own && NameGlob.Any(own, name) ? ServiceProtection.File : ServiceProtection.None;
    }
}

/// <summary>Who protects a service.</summary>
public enum ServiceProtection
{
    None,
    App,
    File,
}
